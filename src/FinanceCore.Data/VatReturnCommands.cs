using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// vat-returns:prepare, :record-filing and :get (08, 7.1, 7.2, 7.4). A return is the frozen snapshot of the tax lines of one VAT period of a
/// registration: the result rows by return box, the tax lines that made them, a digest of both and a node of the VAT_RETURN chain.
/// Built: the return type STANDARD. The EC list, OSS and IOSS return types, the legacy tax summary of a migration and the missing-evidence
/// warning wait for their tables (LIMIT.RETURN_TYPE_NOT_BUILT).
/// Locks (04, 6.3): the VAT_RETURN head first, then the rows of the period are read. Two preparations of one company are serialised by the head.
/// </summary>
public sealed class VatReturnCommands(NpgsqlDataSource source)
{
    public const string PreparePermission = "vat.prepare";
    public const string FilePermission = "vat.file";
    public const string ReadPermission = "vat.read";

    private sealed record Registration(
        Guid CompanyId, string Country, string Scheme, string FilingFrequency, string CorrectionMethod, string ReturnCurrency,
        DateOnly ValidFrom, DateOnly? ValidTo, bool Active);

    private sealed record TaxRow(
        Guid DocumentId, int LineNo, DateOnly TaxDate, Guid VersionId, decimal VatBase, decimal VatTax, Guid AccountId, Guid TaxCodeId,
        DateOnly VersionFrom, Guid? ReverseChargeAccount);

    private sealed record BoxMapping(string LineSide, string BoxCode, string AmountKind, int Sign);

    internal static List<CommandError> ShapeErrors(CommandContext ctx)
    {
        var errors = new List<CommandError>();
        void Invalid(string path, string reason) => errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        return errors;
    }

    // ---------------------------------------------------------------------------------------------------------
    // vat-returns:prepare
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<VatReturnResult>> PrepareAsync(
        Guid tenantId, CommandContext ctx, PrepareVatReturnRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(PreparePermission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", PreparePermission)));
        List<CommandError> shape = ShapeErrors(ctx);
        if (req.RegistrationId == Guid.Empty) shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "registration_id", ("reason", "required")));
        if (req.ReturnType is not ("STANDARD" or "EC_LIST" or "OSS" or "IOSS"))
            shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "return_type", ("allowed_values", new[] { "STANDARD", "EC_LIST", "OSS", "IOSS" })));
        if (req.FiscalYear < 1) shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "fiscal_year", ("reason", "at least 1")));
        if (req.VatPeriodNo < 1 || req.VatPeriodNo > short.MaxValue) shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "vat_period_no", ("reason", "at least 1")));
        if (shape.Count > 0) return CommandResult<VatReturnResult>.Rejected(shape);

        byte[] fingerprint = VatReturnHash2.Fingerprint(req);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await PrepareOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<VatReturnResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<VatReturnResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<VatReturnResult>> PrepareOnceAsync(
        Guid tenantId, CommandContext ctx, PrepareVatReturnRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);

        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            VatReturnResult? replay = earlier.EntityId is null ? null : await LoadAsync(tx, tenantId, null, earlier.EntityId.Value, ct);
            return replay is null
                ? CommandResult<VatReturnResult>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                : CommandResult<VatReturnResult>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        // P2: the registration, the company, the return type
        Registration? reg = await ReadRegistrationAsync(tx, tenantId, req.RegistrationId, ct);
        if (reg is null) return Reject(JournalDocumentPoster.Error("VAT.NO_REGISTRATION", "P2", "registration_id", ("registration_id", req.RegistrationId)));
        Guid companyId = reg.CompanyId;
        if (!reg.Active) return Reject(JournalDocumentPoster.Error("VAT.REGISTRATION_INACTIVE", "P2", "registration_id", ("registration_id", req.RegistrationId)));
        JournalDocumentPoster.CompanyRow? company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null || company.Status != "ACTIVE")
            return Reject(JournalDocumentPoster.Error("COMPANY.NOT_ACTIVE", "P2", "registration_id", ("status", company?.Status)));
        string schemeType = VatPeriods.ReturnTypeOfScheme(reg.Scheme);
        if (req.ReturnType != schemeType && !(req.ReturnType == "EC_LIST" && reg.Scheme == "STANDARD"))
            return Reject(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P2", "return_type", ("reason", $"the scheme {reg.Scheme} files {schemeType}")));
        if (req.ReturnType != "STANDARD")
            return Reject(JournalDocumentPoster.Error("LIMIT.RETURN_TYPE_NOT_BUILT", "P2", "return_type", ("return_type", req.ReturnType)));

        // P2: the VAT period of the fiscal year (08, 4.2)
        IReadOnlyList<VatPeriod> periods = VatPeriods.Build(await ReadRegularPeriodsAsync(tx, tenantId, companyId, req.FiscalYear, ct), reg.FilingFrequency);
        VatPeriod? period = periods.FirstOrDefault(p => p.No == req.VatPeriodNo);
        if (period is null)
            return Reject(JournalDocumentPoster.Error("VAT.PERIOD_NOT_FOUND", "P2", "vat_period_no", ("fiscal_year", req.FiscalYear), ("vat_period_no", req.VatPeriodNo)));
        if (reg.ValidFrom > period.End || (reg.ValidTo is { } to && to < period.Start))
            return Reject(JournalDocumentPoster.Error("VAT.NO_REGISTRATION", "P2", "registration_id", ("registration_id", req.RegistrationId), ("period_start", period.Start)));

        // P7: the head, then the state that the decisions rest on
        (long headSeq, byte[] headHash, _) = await CommandSupport.LockOrCreateHeadAsync(
            tx, tenantId, companyId, VatReturnHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, VatReturnHash2.ChainType, ""), ct);

        // P3: every fiscal period of the VAT period is at least soft-closed (Q1), read again after the lock
        var open = (await ReadRegularPeriodsAsync(tx, tenantId, companyId, req.FiscalYear, ct))
            .Where(p => period.Periods.Any(q => q.PeriodNo == p.PeriodNo) && p.Status == "OPEN").Select(p => p.PeriodNo).ToArray();
        if (open.Length > 0)
            return Reject(JournalDocumentPoster.Error("VAT.PERIOD_NOT_COMPLETE", "P3", "vat_period_no", ("open_periods", open)));

        // P4: the previous VAT period of the registration and return type has an original (08, 7.1), unless this is the first
        VatPeriod? previous = req.VatPeriodNo > 1 ? periods.FirstOrDefault(p => p.No == req.VatPeriodNo - 1) : null;
        int previousYear = req.FiscalYear, previousNo = req.VatPeriodNo - 1;
        if (previous is null && req.VatPeriodNo == 1)
        {
            var earlierYear = VatPeriods.Build(await ReadRegularPeriodsAsync(tx, tenantId, companyId, req.FiscalYear - 1, ct), reg.FilingFrequency);
            previous = earlierYear.LastOrDefault();
            previousYear = req.FiscalYear - 1; previousNo = previous?.No ?? 0;
        }
        if (previous is not null && reg.ValidFrom <= previous.End && (reg.ValidTo is null || reg.ValidTo >= previous.Start)
            && !await ExistsAsync(tx,
                "select 1 from core.vat_return where tenant_id = @t and company_id = @c and registration_id = @r and return_type = @rt and fiscal_year = @fy " +
                "and vat_period_no = @no and return_kind = 'ORIGINAL'", tenantId, companyId, req.RegistrationId, req.ReturnType, previousYear, previousNo, ct))
            return Reject(JournalDocumentPoster.Error("VAT.PREVIOUS_PERIOD_NOT_PREPARED", "P4", "vat_period_no", ("fiscal_year", previousYear), ("vat_period_no", previousNo)));

        // The returns of this period decide what a preparation is (Q2)
        var existing = new List<(Guid Id, string Kind, int Seq, Guid? SupersededBy)>();
        await using (var cmd = tx.Command(
            "select return_id, return_kind, return_seq, superseded_by_return_id from core.vat_return where tenant_id = @t and company_id = @c " +
            "and registration_id = @r and return_type = @rt and fiscal_year = @fy and vat_period_no = @no order by return_seq"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", req.RegistrationId);
            cmd.Parameters.AddWithValue("rt", req.ReturnType); cmd.Parameters.AddWithValue("fy", req.FiscalYear); cmd.Parameters.AddWithValue("no", req.VatPeriodNo);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) existing.Add((r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetGuid(3)));
        }
        string kind = "ORIGINAL"; int seq = 1; Guid? replaces = null;
        if (existing.Count > 0)
        {
            seq = existing.Max(e => e.Seq) + 1;
            switch (reg.CorrectionMethod)
            {
                case "ADJUST_NEXT":
                    return Reject(JournalDocumentPoster.Error("VAT.ALREADY_PREPARED", "P4", "vat_period_no", ("return_id", existing[0].Id)));
                case "SUPPLEMENTARY":
                    kind = "SUPPLEMENTARY";
                    break;
                default:
                    kind = "REPLACEMENT";
                    replaces = existing.Where(e => e.SupersededBy is null).OrderByDescending(e => e.Seq).First().Id;
                    break;
            }
        }

        // P5: the tax lines of the registration up to the period end that no current snapshot holds, and for a replacement those of the replaced one
        var rows = new List<TaxRow>();
        await using (var cmd = tx.Command(
            "select t.document_id, t.line_no, t.tax_date, t.tax_code_version_id, t.vat_base_amount, t.vat_tax_amount, l.account_id, v.tax_code_id, v.valid_from, v.reverse_charge_account_id " +
            "from core.journal_line_tax t " +
            "join core.journal_line l on l.tenant_id = t.tenant_id and l.company_id = t.company_id and l.document_id = t.document_id and l.line_no = t.line_no " +
            "join core.tax_code_version v on v.tenant_id = t.tenant_id and v.tax_code_version_id = t.tax_code_version_id " +
            "join core.tax_code c on c.tenant_id = v.tenant_id and c.company_id = v.company_id and c.tax_code_id = v.tax_code_id " +
            "where t.tenant_id = @t and t.company_id = @c and c.vat_registration_id = @r and t.tax_date <= @end and (" +
            "not exists (select 1 from core.vat_return_tax_line x where x.tenant_id = t.tenant_id and x.company_id = t.company_id and x.registration_id = @r " +
            "and x.return_type = @rt and x.document_id = t.document_id and x.line_no = t.line_no and x.is_current) " +
            "or exists (select 1 from core.vat_return_tax_line y where y.tenant_id = t.tenant_id and y.company_id = t.company_id and y.return_id = @rep " +
            "and y.document_id = t.document_id and y.line_no = t.line_no and y.is_current)) " +
            "order by t.document_id, t.line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", req.RegistrationId);
            cmd.Parameters.AddWithValue("rt", req.ReturnType); cmd.Parameters.AddWithValue("end", period.End);
            cmd.Parameters.Add(new NpgsqlParameter("rep", NpgsqlDbType.Uuid) { Value = replaces ?? Guid.Empty });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new TaxRow(r.GetGuid(0), r.GetInt32(1), r.GetFieldValue<DateOnly>(2), r.GetGuid(3), r.GetDecimal(4), r.GetDecimal(5), r.GetGuid(6),
                    r.GetGuid(7), r.GetFieldValue<DateOnly>(8), r.IsDBNull(9) ? null : r.GetGuid(9)));
        }
        // P6: a supplementary return has something to report
        if (kind == "SUPPLEMENTARY" && rows.Count == 0)
            return Reject(JournalDocumentPoster.Error("VAT.NOTHING_TO_REPORT", "P6", "vat_period_no"));

        // Aggregation by the box mapping of the version, with the side of the line (08, 5.2, 6.2)
        var mappings = new Dictionary<(Guid, DateOnly), List<BoxMapping>>();
        await using (var cmd = tx.Command(
            "select tax_code_id, valid_from, line_side, box_code, amount_kind, sign from core.tax_code_version_box " +
            "where tenant_id = @t and company_id = @c and return_type = @rt"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("rt", req.ReturnType);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var key = (r.GetGuid(0), r.GetFieldValue<DateOnly>(1));
                if (!mappings.TryGetValue(key, out var list)) mappings[key] = list = [];
                list.Add(new BoxMapping(r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt16(5)));
            }
        }
        var boxes = new SortedDictionary<string, (decimal Base, decimal Tax, HashSet<(Guid, int)> Lines)>(StringComparer.Ordinal);
        foreach (TaxRow row in rows)
        {
            string side = row.ReverseChargeAccount is { } rc && rc == row.AccountId ? "COUNTER" : "PRIMARY";
            foreach (BoxMapping m in mappings.GetValueOrDefault((row.TaxCodeId, row.VersionFrom)) ?? [])
            {
                if (m.LineSide != side) continue;
                var (b, t, lines) = boxes.GetValueOrDefault(m.BoxCode, (0m, 0m, new HashSet<(Guid, int)>()));
                if (m.AmountKind == "BASE") b += m.Sign * row.VatBase; else t += m.Sign * row.VatTax;
                lines.Add((row.DocumentId, row.LineNo));
                boxes[m.BoxCode] = (b, t, lines);
            }
        }

        // The snapshot, its digest and its node of the chain
        Guid returnId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        DateTime preparedAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        var lineRows = new List<Dictionary<string, object?>>();
        foreach (var (box, v) in boxes)
            lineRows.Add(new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = companyId, ["return_id"] = returnId, ["line_no"] = lineRows.Count + 1, ["row_type"] = "BOX",
                ["box_code"] = box, ["partner_id"] = null, ["partner_vat_id"] = null, ["country_code"] = null, ["category"] = null, ["rate"] = null,
                ["supply_kind"] = null, ["currency_code"] = reg.ReturnCurrency, ["base_amount"] = v.Base, ["tax_amount"] = v.Tax, ["line_count"] = v.Lines.Count,
                ["legacy_base_amount"] = 0m, ["legacy_tax_amount"] = 0m
            });
        var taxRows = rows.Select(r => new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = companyId, ["return_id"] = returnId, ["document_id"] = r.DocumentId, ["line_no"] = r.LineNo,
            ["registration_id"] = req.RegistrationId, ["return_type"] = req.ReturnType, ["tax_date"] = r.TaxDate, ["is_late"] = r.TaxDate < period.Start,
            ["tax_code_version_id"] = r.VersionId, ["vat_base_amount"] = r.VatBase, ["vat_tax_amount"] = r.VatTax
        }).ToList();
        int late = taxRows.Count(r => (bool)r["is_late"]!);
        byte[] digest = VatReturnHash2.SnapshotDigest(lineRows, taxRows);
        long chainSeq = headSeq + 1;
        var body = new Dictionary<string, object?>
        {
            ["return_id"] = returnId, ["registration_id"] = req.RegistrationId, ["return_type"] = req.ReturnType, ["fiscal_year"] = req.FiscalYear,
            ["vat_period_no"] = req.VatPeriodNo, ["period_start_date"] = period.Start, ["period_end_date"] = period.End, ["return_kind"] = kind,
            ["return_seq"] = seq, ["replaces_return_id"] = replaces, ["prepared_at"] = preparedAt, ["prepared_by"] = ctx.Actor,
            ["correlation_id"] = ctx.CorrelationId, ["idempotency_key"] = ctx.IdempotencyKey, ["tax_line_count"] = taxRows.Count, ["late_line_count"] = late
        };
        byte[] rowHash = VatReturnHash2.RowHash(tenantId, companyId, chainSeq, headHash, body, digest);

        await using (var cmd = tx.Command(
            "insert into core.vat_return (tenant_id, company_id, return_id, registration_id, return_type, fiscal_year, vat_period_no, period_start_date, period_end_date, " +
            "return_kind, return_seq, replaces_return_id, prepared_at, prepared_by, correlation_id, idempotency_key, tax_line_count, late_line_count, snapshot_digest, " +
            "chain_seq, prev_hash, row_hash, hash_version) values (@t, @c, @id, @r, @rt, @fy, @no, @ps, @pe, @kind, @seq, @rep, @at, @by, @corr, @key, @n, @late, @dig, @cs, @prev, @row, 2)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", returnId);
            cmd.Parameters.AddWithValue("r", req.RegistrationId); cmd.Parameters.AddWithValue("rt", req.ReturnType); cmd.Parameters.AddWithValue("fy", req.FiscalYear);
            cmd.Parameters.AddWithValue("no", (short)req.VatPeriodNo); cmd.Parameters.AddWithValue("ps", period.Start); cmd.Parameters.AddWithValue("pe", period.End);
            cmd.Parameters.AddWithValue("kind", kind); cmd.Parameters.AddWithValue("seq", seq);
            cmd.Parameters.Add(new NpgsqlParameter("rep", NpgsqlDbType.Uuid) { Value = (object?)replaces ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = preparedAt });
            cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
            cmd.Parameters.AddWithValue("n", taxRows.Count); cmd.Parameters.AddWithValue("late", late); cmd.Parameters.AddWithValue("dig", digest);
            cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("prev", headHash); cmd.Parameters.AddWithValue("row", rowHash);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var l in lineRows)
        {
            await using var cmd = tx.Command(
                "insert into core.vat_return_line (tenant_id, company_id, return_id, line_no, row_type, box_code, currency_code, base_amount, tax_amount, line_count) " +
                "values (@t, @c, @id, @n, 'BOX', @box, @ccy, @b, @x, @lc)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", returnId);
            cmd.Parameters.AddWithValue("n", (int)l["line_no"]!); cmd.Parameters.AddWithValue("box", (string)l["box_code"]!);
            cmd.Parameters.AddWithValue("ccy", reg.ReturnCurrency); cmd.Parameters.AddWithValue("b", (decimal)l["base_amount"]!);
            cmd.Parameters.AddWithValue("x", (decimal)l["tax_amount"]!); cmd.Parameters.AddWithValue("lc", (int)l["line_count"]!);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (replaces is { } replaced)
        {
            // the replaced snapshot stops being current: its tax lines are carried into this one (08, 7.1)
            await using (var cmd = tx.Command(
                "update core.vat_return_tax_line set is_current = false where tenant_id = @t and company_id = @c and return_id = @old and is_current"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("old", replaced);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = tx.Command(
                "update core.vat_return set superseded_by_return_id = @new where tenant_id = @t and company_id = @c and return_id = @old"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("old", replaced);
                cmd.Parameters.AddWithValue("new", returnId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        foreach (var r in taxRows)
        {
            await using var cmd = tx.Command(
                "insert into core.vat_return_tax_line (tenant_id, company_id, return_id, document_id, line_no, registration_id, return_type, tax_date, is_late, " +
                "tax_code_version_id, vat_base_amount, vat_tax_amount, is_current) values (@t, @c, @id, @d, @n, @r, @rt, @td, @late, @v, @b, @x, true)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", returnId);
            cmd.Parameters.AddWithValue("d", (Guid)r["document_id"]!); cmd.Parameters.AddWithValue("n", (int)r["line_no"]!);
            cmd.Parameters.AddWithValue("r", req.RegistrationId); cmd.Parameters.AddWithValue("rt", req.ReturnType);
            cmd.Parameters.AddWithValue("td", (DateOnly)r["tax_date"]!); cmd.Parameters.AddWithValue("late", (bool)r["is_late"]!);
            cmd.Parameters.AddWithValue("v", (Guid)r["tax_code_version_id"]!); cmd.Parameters.AddWithValue("b", (decimal)r["vat_base_amount"]!);
            cmd.Parameters.AddWithValue("x", (decimal)r["vat_tax_amount"]!);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, VatReturnHash2.ChainType, "", chainSeq, rowHash, VatReturnHash2.Version, ct);

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, "vat-returns:prepare", fingerprint, "VAT_RETURN", returnId, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "vat.return_prepared", "VAT_RETURN", $"{companyId:D}/{returnId:D}", "PREPARED",
            preparedAt, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["chain_type"] = VatReturnHash2.ChainType, ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash)
            }),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["registration_id"] = req.RegistrationId, ["return_type"] = req.ReturnType, ["fiscal_year"] = req.FiscalYear, ["vat_period_no"] = req.VatPeriodNo,
                ["return_kind"] = kind, ["tax_line_count"] = taxRows.Count, ["late_line_count"] = late
            }), ct);

        await tx.CommitAsync(ct);
        VatReturnResult result = await LoadForResultAsync(tenantId, companyId, returnId, ct);
        return CommandResult<VatReturnResult>.Ok(result, logId);
    }

    // ---------------------------------------------------------------------------------------------------------
    // vat-returns:record-filing
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<VatReturnResult>> RecordFilingAsync(
        Guid tenantId, CommandContext ctx, RecordVatFilingRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(FilePermission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", FilePermission)));
        List<CommandError> shape = ShapeErrors(ctx);
        if (req.ReturnId == Guid.Empty) shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "return_id", ("reason", "required")));
        if (string.IsNullOrWhiteSpace(req.FilingReference) || req.FilingReference.Length > 100)
            shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "filing_reference", ("reason", "required, at most 100 characters")));
        if (shape.Count > 0) return CommandResult<VatReturnResult>.Rejected(shape);

        byte[] fingerprint = VatReturnHash2.Fingerprint(req);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);
                CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
                if (earlier is not null)
                {
                    if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                        return Reject(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                    VatReturnResult? replay = earlier.EntityId is null ? null : await LoadAsync(tx, tenantId, null, earlier.EntityId.Value, ct);
                    return replay is null
                        ? CommandResult<VatReturnResult>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                        : CommandResult<VatReturnResult>.Ok(replay, earlier.CommandLogId, replay: true);
                }
                Guid? companyId = null; DateTime? filedAt = null;
                await using (var cmd = tx.Command("select company_id, filed_at from core.vat_return where tenant_id = @t and return_id = @id for update"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", req.ReturnId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct)) { companyId = r.GetGuid(0); filedAt = r.IsDBNull(1) ? null : r.GetDateTime(1); }
                }
                if (companyId is null) return Reject(JournalDocumentPoster.Error("VAT.RETURN_NOT_FOUND", "P2", "return_id", ("return_id", req.ReturnId)));
                if (filedAt is not null) return Reject(JournalDocumentPoster.Error("VAT.ALREADY_FILED", "P2", "return_id", ("return_id", req.ReturnId)));

                DateTime at = new(req.FiledAt.Ticks - req.FiledAt.Ticks % 10, DateTimeKind.Utc);
                await using (var cmd = tx.Command(
                    "update core.vat_return set filing_reference = @ref, filed_at = @at, filed_by = @by where tenant_id = @t and company_id = @c and return_id = @id"))
                {
                    cmd.Parameters.AddWithValue("ref", req.FilingReference); cmd.Parameters.AddWithValue("by", ctx.Actor);
                    cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = req.FiledAt.ToUniversalTime() });
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId.Value); cmd.Parameters.AddWithValue("id", req.ReturnId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, "vat-returns:record-filing", fingerprint, "VAT_RETURN", req.ReturnId, ct);
                await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "vat.return_filed", "VAT_RETURN", $"{companyId:D}/{req.ReturnId:D}", "FILED",
                    DateTime.UtcNow, isAudit: true, null,
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["filing_reference"] = req.FilingReference }), ct);
                await tx.CommitAsync(ct);
                return CommandResult<VatReturnResult>.Ok(await LoadForResultAsync(tenantId, companyId.Value, req.ReturnId, ct), logId);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<VatReturnResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // vat-returns:get
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<VatReturnResult>> GetAsync(Guid tenantId, CommandContext ctx, Guid returnId, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ReadPermission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", ReadPermission)));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);
        VatReturnResult? result = await LoadAsync(tx, tenantId, null, returnId, ct);
        return result is null
            ? Reject(JournalDocumentPoster.Error("VAT.RETURN_NOT_FOUND", "P2", "return_id", ("return_id", returnId)))
            : CommandResult<VatReturnResult>.Ok(result, Guid.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------
    private async Task<VatReturnResult> LoadForResultAsync(Guid tenantId, Guid companyId, Guid returnId, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);
        return (await LoadAsync(tx, tenantId, companyId, returnId, ct))!;
    }

    internal static async Task<VatReturnResult?> LoadAsync(TenantTransaction tx, Guid tenantId, Guid? companyId, Guid returnId, CancellationToken ct)
    {
        VatReturnResult? head = null;
        await using (var cmd = tx.Command(
            "select company_id, registration_id, return_type, fiscal_year, vat_period_no, period_start_date, period_end_date, return_kind, return_seq, replaces_return_id, " +
            "superseded_by_return_id, tax_line_count, late_line_count, snapshot_digest, chain_seq, row_hash, prev_hash, filing_reference, filed_at, settled_document_id " +
            "from core.vat_return where tenant_id = @t and return_id = @id"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", returnId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                head = new VatReturnResult(returnId, r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetInt16(4), r.GetFieldValue<DateOnly>(5),
                    r.GetFieldValue<DateOnly>(6), r.GetString(7), r.GetInt32(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.IsDBNull(10) ? null : r.GetGuid(10),
                    r.GetInt32(11), r.GetInt32(12), Canonical.FormatHex32((byte[])r[13]), r.GetInt64(14), Canonical.FormatHex32((byte[])r[15]),
                    Canonical.FormatHex32((byte[])r[16]), r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetDateTime(18),
                    r.IsDBNull(19) ? null : r.GetGuid(19), [], []);
        }
        if (head is null || (companyId is not null && head.CompanyId != companyId)) return null;
        var lines = new List<VatReturnLineResult>();
        await using (var cmd = tx.Command(
            "select line_no, row_type, box_code, currency_code, base_amount, tax_amount, line_count from core.vat_return_line " +
            "where tenant_id = @t and company_id = @c and return_id = @id order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", head.CompanyId); cmd.Parameters.AddWithValue("id", returnId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lines.Add(new VatReturnLineResult(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetInt32(6)));
        }
        var taxLines = new List<VatReturnTaxLineResult>();
        await using (var cmd = tx.Command(
            "select document_id, line_no, tax_date, is_late, tax_code_version_id, vat_base_amount, vat_tax_amount, is_current from core.vat_return_tax_line " +
            "where tenant_id = @t and company_id = @c and return_id = @id order by document_id, line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", head.CompanyId); cmd.Parameters.AddWithValue("id", returnId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                taxLines.Add(new VatReturnTaxLineResult(r.GetGuid(0), r.GetInt32(1), r.GetFieldValue<DateOnly>(2), r.GetBoolean(3), r.GetGuid(4), r.GetDecimal(5), r.GetDecimal(6), r.GetBoolean(7)));
        }
        return head with { Lines = lines, TaxLines = taxLines };
    }

    private static async Task<Registration?> ReadRegistrationAsync(TenantTransaction tx, Guid tenantId, Guid registrationId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select company_id, country_code, scheme, filing_frequency, correction_method, return_currency, valid_from, valid_to, active " +
            "from core.company_vat_registration where tenant_id = @t and registration_id = @r");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("r", registrationId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new Registration(r.GetGuid(0), r.GetString(1).Trim(), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5).Trim(),
                r.GetFieldValue<DateOnly>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7), r.GetBoolean(8))
            : null;
    }

    internal static async Task<List<RegularPeriod>> ReadRegularPeriodsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, int fiscalYear, CancellationToken ct)
    {
        var result = new List<RegularPeriod>();
        await using var cmd = tx.Command(
            "select period_no, start_date, end_date, status from core.fiscal_period where tenant_id = @t and company_id = @c and fiscal_year = @fy " +
            "and period_type = 'REGULAR' order by period_no");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("fy", fiscalYear);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(new RegularPeriod(r.GetInt32(0), r.GetFieldValue<DateOnly>(1), r.GetFieldValue<DateOnly>(2), r.GetString(3)));
        return result;
    }

    private static async Task<bool> ExistsAsync(
        TenantTransaction tx, string sql, Guid tenantId, Guid companyId, Guid registrationId, string returnType, int fiscalYear, int vatPeriodNo, CancellationToken ct)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", registrationId);
        cmd.Parameters.AddWithValue("rt", returnType); cmd.Parameters.AddWithValue("fy", fiscalYear); cmd.Parameters.AddWithValue("no", (short)vatPeriodNo);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static CommandResult<VatReturnResult> Reject(CommandError error) => CommandResult<VatReturnResult>.Rejected([error]);
}
