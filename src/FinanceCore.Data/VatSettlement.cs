using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// vat-returns:settle and :reverse-settlement (08, 7.3, 7.3a). The settlement posts one VAT_SETTLE document that clears the VAT accounts of the
/// tax lines of a return against the role VAT_SETTLEMENT of the registration, from the posted ledger amounts of the tax lines. A replacement
/// settles the per-account difference to the last settled return along replaces_return_id. The document is system generated, has the origin VAT,
/// carries the digest of the snapshot, and is reversed only by this command's reversal and only before the filing.
/// Locks (04, 6.3): the number counter, the LEDGER head, then the return row.
/// </summary>
public sealed class VatSettlement(NpgsqlDataSource source)
{
    public const string Permission = "vat.settle";
    public const string DocumentType = "VAT_SETTLE";
    public const string SettleCommand = "vat-returns:settle";
    public const string ReverseCommand = "vat-returns:reverse-settlement";

    private sealed record ReturnRow(
        Guid CompanyId, Guid RegistrationId, string ReturnType, DateOnly PeriodEnd, Guid? ReplacesReturnId, Guid? SupersededBy, Guid? SettledDocumentId,
        DateTime? FiledAt, byte[] SnapshotDigest);

    private sealed record Sum(decimal Lc, decimal Gc, decimal Rc);

    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<SettledVatReturn>> SettleAsync(
        Guid tenantId, CommandContext ctx, SettleVatReturnRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission)));
        List<CommandError> shape = VatReturnCommands.ShapeErrors(ctx);
        if (req.ReturnId == Guid.Empty) shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "return_id", ("reason", "required")));
        if (shape.Count > 0) return CommandResult<SettledVatReturn>.Rejected(shape);

        byte[] fingerprint = VatReturnHash2.Fingerprint(req);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SettleOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<SettledVatReturn>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<SettledVatReturn>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<SettledVatReturn>> SettleOnceAsync(
        Guid tenantId, CommandContext ctx, SettleVatReturnRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);

        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            PostedDocument? replay = earlier.EntityId is null ? null : await LedgerReader.LoadDocumentAsync(tx, tenantId, earlier.EntityId.Value, ct);
            return replay is null
                ? CommandResult<SettledVatReturn>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                : CommandResult<SettledVatReturn>.Ok(new SettledVatReturn(req.ReturnId, replay), earlier.CommandLogId, replay: true);
        }

        // P2: the return and what it needs
        ReturnRow? ret = await ReadReturnAsync(tx, tenantId, req.ReturnId, forUpdate: false, ct);
        if (ret is null) return Reject(JournalDocumentPoster.Error("VAT.RETURN_NOT_FOUND", "P2", "return_id", ("return_id", req.ReturnId)));
        Guid companyId = ret.CompanyId;
        if (ret.ReturnType == "EC_LIST")
            return Reject(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P2", "return_id", ("reason", "an EC list has no amounts to settle")));
        JournalDocumentPoster.CompanyRow? company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null || company.Status != "ACTIVE")
            return Reject(JournalDocumentPoster.Error("COMPANY.NOT_ACTIVE", "P2", "return_id", ("status", company?.Status)));
        await using (var cmd = tx.Command("select active from core.company_vat_registration where tenant_id = @t and company_id = @c and registration_id = @r"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", ret.RegistrationId);
            if (await cmd.ExecuteScalarAsync(ct) is false)
                return Reject(JournalDocumentPoster.Error("VAT.REGISTRATION_INACTIVE", "P2", "return_id", ("registration_id", ret.RegistrationId)));
        }
        if (ret.SettledDocumentId is not null) return Reject(JournalDocumentPoster.Error("VAT.ALREADY_SETTLED", "P2", "return_id", ("document_id", ret.SettledDocumentId)));
        if (ret.SupersededBy is not null) return Reject(JournalDocumentPoster.Error("VAT.SUPERSEDED", "P2", "return_id", ("superseded_by_return_id", ret.SupersededBy)));

        Guid? ledgerId = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct);
        if (ledgerId is null) return Reject(JournalDocumentPoster.Error("LEDGER.NOT_FOUND", "P2", "return_id"));

        // P3: the posting date. Given, it must be postable. Not given, it is the first postable date on or after the period end (ADR-028).
        JournalDocumentPoster.PeriodCheck period;
        DateOnly postingDate;
        if (req.PostingDate is { } given)
        {
            postingDate = given;
            period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, postingDate, ctx, false, ct);
            if (period.Errors.Count > 0) return Reject(NotPostable(postingDate, period.Errors));
        }
        else
        {
            postingDate = ret.PeriodEnd;
            period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, postingDate, ctx, false, ct);
            for (int i = 0; period.Errors.Count > 0 && i < 400; i++)
            {
                postingDate = postingDate.AddDays(1);
                period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, postingDate, ctx, false, ct);
            }
            if (period.Errors.Count > 0) return Reject(NotPostable(ret.PeriodEnd, period.Errors));
        }

        JournalDocumentPoster.SeriesRow? series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, DocumentType, postingDate, ledgerId.Value, ct);
        if (series is null)
            return Reject(JournalDocumentPoster.Error("SERIES.NO_ASSIGNMENT", "P2", "posting_date", ("document_type", DocumentType), ("ledger_id", ledgerId), ("posting_date", postingDate)));
        string? groupCurrency = await ClearingPoster.ReadGroupCurrencyAsync(tx, tenantId, companyId, postingDate, ct);
        if (groupCurrency is null) return Reject(JournalDocumentPoster.Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "posting_date", ("posting_date", postingDate)));
        Guid? settlementAccount = await AccountRoleResolver.ResolveAsync(tx, tenantId, companyId, "VAT_SETTLEMENT", null, null, ret.RegistrationId, ct);
        if (settlementAccount is null)
            return Reject(JournalDocumentPoster.Error("CONFIG.ROLE_NOT_ASSIGNED", "P2", "return_id", ("role_code", "VAT_SETTLEMENT"), ("vat_registration_id", ret.RegistrationId)));
        var roleAccount = (await JournalDocumentPoster.ReadAccountRowsAsync(tx, tenantId, companyId, [settlementAccount.Value], ct)).GetValueOrDefault(settlementAccount.Value);
        if (roleAccount is null || !roleAccount.Active || roleAccount.PostingBlocked || roleAccount.ValidFrom > postingDate || (roleAccount.ValidTo is { } vt && vt < postingDate))
            return Reject(JournalDocumentPoster.Error("ACCOUNT.ROLE_ACCOUNT_NOT_USABLE", "P2", "return_id", ("role_code", "VAT_SETTLEMENT"), ("account_id", settlementAccount)));

        // P7: counter, LEDGER head, then the return row (04, 6.3)
        int fiscalYear = period.FiscalYear!.Value;
        long? last = await ClearingPoster.LockCounterAsync(tx, tenantId, companyId, series.SeriesId, fiscalYear, ct);
        if (last is null)
            return Reject(JournalDocumentPoster.Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "posting_date", ("series_code", series.SeriesCode), ("fiscal_year", fiscalYear)));
        (long ledgerSeq, byte[] ledgerHash) = await ClearingPoster.LockLedgerHeadAsync(tx, tenantId, companyId, ledgerId.Value, ct);
        ReturnRow? locked = await ReadReturnAsync(tx, tenantId, req.ReturnId, forUpdate: true, ct);
        if (locked!.SettledDocumentId is not null) return Reject(JournalDocumentPoster.Error("VAT.ALREADY_SETTLED", "P7", "return_id", ("document_id", locked.SettledDocumentId)));
        if (locked.SupersededBy is not null) return Reject(JournalDocumentPoster.Error("VAT.SUPERSEDED", "P7", "return_id", ("superseded_by_return_id", locked.SupersededBy)));
        JournalDocumentPoster.PeriodCheck recheck = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, postingDate, ctx, true, ct);
        if (recheck.Errors.Count > 0) return Reject(NotPostable(postingDate, recheck.Errors));

        // The amounts: the sums of the ledger amounts of the tax lines per account, less what the last settled return of the chain settled
        Dictionary<Guid, Sum> now = await SumsAsync(tx, tenantId, companyId, req.ReturnId, ct);
        Guid? baseReturn = null;
        for (Guid? id = locked.ReplacesReturnId; id is not null;)
        {
            ReturnRow? r = await ReadReturnAsync(tx, tenantId, id.Value, forUpdate: false, ct);
            if (r is null) break;
            if (r.SettledDocumentId is not null) { baseReturn = id; break; }
            id = r.ReplacesReturnId;
        }
        Dictionary<Guid, Sum> before = baseReturn is { } b ? await SumsAsync(tx, tenantId, companyId, b, ct) : [];
        var difference = new SortedDictionary<Guid, Sum>();
        foreach (Guid account in now.Keys.Union(before.Keys))
        {
            Sum n = now.GetValueOrDefault(account) ?? new Sum(0, 0, 0), o = before.GetValueOrDefault(account) ?? new Sum(0, 0, 0);
            var d = new Sum(n.Lc - o.Lc, n.Gc - o.Gc, n.Rc - o.Rc);
            if (d.Lc != 0m || d.Gc != 0m || d.Rc != 0m) difference[account] = d;
        }
        if (difference.Count == 0) return Reject(JournalDocumentPoster.Error("VAT.NOTHING_TO_SETTLE", "P6", "return_id", ("return_id", req.ReturnId)));

        // The document
        bool hasRc = company.ReportingCurrency is not null;
        long sequenceNo = last.Value + 1;
        await using (var cmd = tx.Command(
            "update core.number_range_counter set last_number = @n, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        string number = PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
        Guid documentId = Guid.NewGuid();
        long chainSeq = ledgerSeq + 1;
        DateTime nowUtc = DateTime.UtcNow;
        DateTime postedAt = new(nowUtc.Ticks - nowUtc.Ticks % 10, DateTimeKind.Utc);
        int periodNo = recheck.PeriodNo!.Value;

        var lines = new List<PostedLine>();
        PostedLine Line(Guid account, decimal lc, decimal gc, decimal rc) =>
            new(lines.Count + 1, null, account, "VAT_SETTLEMENT", lc, lc, company.LocalCurrency, 1m, gc, groupCurrency, 1m,
                hasRc ? rc : null, hasRc ? company.ReportingCurrency : null, hasRc ? 1m : null, "DERIVED", postingDate);
        foreach (var (account, d) in difference) lines.Add(Line(account, -d.Lc, -d.Gc, -d.Rc));
        lines.Add(Line(settlementAccount.Value, difference.Values.Sum(d => d.Lc), difference.Values.Sum(d => d.Gc), difference.Values.Sum(d => d.Rc)));

        var header = new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = companyId, ["document_id"] = documentId, ["ledger_id"] = ledgerId.Value,
            ["document_type"] = DocumentType, ["is_system_generated"] = true, ["number_series_id"] = series.SeriesId,
            ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo, ["document_number"] = number,
            ["fiscal_period"] = periodNo, ["soft_close_override"] = recheck.SoftCloseOverride,
            ["migration_batch_id"] = null, ["detail_digest"] = locked.SnapshotDigest,
            ["document_date"] = postingDate, ["posting_date"] = postingDate, ["posted_at"] = postedAt,
            ["transaction_currency"] = company.LocalCurrency,
            ["reversal_of_document_id"] = null, ["reversal_reason_code"] = null, ["auto_reversal_date"] = null,
            ["ic_transaction_id"] = null, ["ic_counterpart_company_id"] = null, ["origin"] = "VAT",
            ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = null, ["idempotency_key"] = ctx.IdempotencyKey,
            ["command_name"] = SettleCommand, ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId,
            ["hash_version"] = LedgerHash2.Version
        };
        Dictionary<string, object?> LineRow(PostedLine l) => new()
        {
            ["tenant_id"] = tenantId, ["company_id"] = companyId, ["document_id"] = documentId, ["line_no"] = l.LineNo,
            ["ledger_id"] = ledgerId.Value, ["line_type"] = l.LineType, ["account_id"] = l.AccountId,
            ["posting_date"] = postingDate, ["fiscal_year"] = fiscalYear, ["fiscal_period"] = periodNo,
            ["reversed_line_no"] = null, ["tax_code_id"] = null,
            ["amount_tc"] = l.AmountTc, ["amount_lc"] = l.AmountLc, ["currency_lc"] = l.CurrencyLc, ["rate_lc"] = l.RateLc, ["rate_lc_table"] = null,
            ["amount_gc"] = l.AmountGc, ["currency_gc"] = l.CurrencyGc, ["rate_gc"] = l.RateGc, ["rate_gc_table"] = null,
            ["amount_rc"] = l.AmountRc, ["currency_rc"] = l.CurrencyRc, ["rate_rc"] = l.RateRc, ["rate_rc_table"] = null,
            ["rate_type"] = l.RateType, ["rate_date"] = l.RateDate, ["ic_counterpart_company_id"] = null
        };
        byte[] rowHash = LedgerHash2.RowHash(header, chainSeq, ledgerHash, LedgerHash2.LineDigest(lines.Select(l => LedgerHash2.LineHash(new HashedLine(LineRow(l)))).ToList()));

        await using (var cmd = tx.Command(
            "insert into core.journal_document (tenant_id, company_id, document_id, ledger_id, document_type, is_system_generated, " +
            "number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, soft_close_override, detail_digest, document_date, posting_date, " +
            "posted_at, transaction_currency, origin, source_system, idempotency_key, command_name, posted_by, correlation_id, chain_seq, prev_hash, row_hash, hash_version) " +
            "values (@t, @c, @id, @l, 'VAT_SETTLE', true, @s, @fy, @seq, @num, @fp, @soft, @dig, @d, @d, @at, @tc, 'VAT', @src, @key, @cmd, @by, @corr, @cs, @prev, @row, @hv)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", documentId);
            cmd.Parameters.AddWithValue("l", ledgerId.Value); cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("fy", fiscalYear);
            cmd.Parameters.AddWithValue("seq", sequenceNo); cmd.Parameters.AddWithValue("num", number); cmd.Parameters.AddWithValue("fp", periodNo);
            cmd.Parameters.AddWithValue("soft", recheck.SoftCloseOverride); cmd.Parameters.AddWithValue("dig", locked.SnapshotDigest);
            cmd.Parameters.AddWithValue("d", postingDate); cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = postedAt });
            cmd.Parameters.AddWithValue("tc", company.LocalCurrency); cmd.Parameters.AddWithValue("src", ctx.SourceSystem);
            cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey); cmd.Parameters.AddWithValue("cmd", SettleCommand); cmd.Parameters.AddWithValue("by", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("prev", ledgerHash);
            cmd.Parameters.AddWithValue("row", rowHash); cmd.Parameters.AddWithValue("hv", (short)LedgerHash2.Version);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (await LegalSnapshots.WriteAsync(tx, tenantId, companyId, documentId, fiscalYear, postingDate, postedAt, ct) is { } noSnapshot)
            return Reject(noSnapshot);
        foreach (PostedLine l in lines)
            await ClearingPoster.InsertLineAsync(tx, tenantId, companyId, documentId, ledgerId.Value, fiscalYear, periodNo, postingDate, l, null, ct);
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, "LEDGER", ledgerId.Value.ToString("D"), chainSeq, rowHash, LedgerHash2.Version, ct);
        await using (var cmd = tx.Command(
            "update core.vat_return set settled_document_id = @d where tenant_id = @t and company_id = @c and return_id = @r and settled_document_id is null"))
        {
            cmd.Parameters.AddWithValue("d", documentId); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", req.ReturnId);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The return to settle is already settled");
        }

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, SettleCommand, fingerprint, "DOCUMENT", documentId, ct);
        string rowHashHex = Canonical.FormatHex32(rowHash);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "ledger.document_posted", "DOCUMENT", $"{companyId:D}/{documentId:D}", "POSTED", postedAt, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["chain_type"] = "LEDGER", ["scope"] = ledgerId.Value.ToString("D"), ["chain_seq"] = chainSeq, ["row_hash"] = rowHashHex
            }),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["document_type"] = DocumentType, ["posting_date"] = postingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["period"] = periodNo, ["ledger_ids"] = new[] { ledgerId.Value.ToString("D") }, ["ledger_seq"] = chainSeq
            }), ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "vat.settled", "VAT_RETURN", $"{companyId:D}/{req.ReturnId:D}", "SETTLED", postedAt, isAudit: true, null,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["document_id"] = documentId, ["return_id"] = req.ReturnId }), ct);

        await tx.CommitAsync(ct);
        var posted = new PostedDocument(documentId, companyId, ledgerId.Value, DocumentType, number, sequenceNo, fiscalYear, periodNo, postingDate, postingDate,
            company.LocalCurrency, "posted", recheck.SoftCloseOverride, chainSeq, rowHashHex, Canonical.FormatHex32(ledgerHash), LedgerHash2.Version, lines);
        return CommandResult<SettledVatReturn>.Ok(new SettledVatReturn(req.ReturnId, posted), logId, recheck.Warnings);
    }

    // ---------------------------------------------------------------------------------------------------------
    // vat-returns:reverse-settlement (08, 7.3a)
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<SettledVatReturn>> ReverseAsync(
        Guid tenantId, CommandContext ctx, ReverseVatSettlementRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission)));
        List<CommandError> shape = VatReturnCommands.ShapeErrors(ctx);
        if (req.ReturnId == Guid.Empty) shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "return_id", ("reason", "required")));
        if (req.ReversalDate == default) shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "reversal_date", ("reason", "required")));
        if (string.IsNullOrWhiteSpace(req.ReasonText) || req.ReasonText.Length > 500)
            shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "reason_text", ("reason", "required, at most 500 characters")));
        if (shape.Count > 0) return CommandResult<SettledVatReturn>.Rejected(shape);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Reject(JournalDocumentPoster.Error("REVERSAL.REASON_REQUIRED", "P1", "reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode))
            return Reject(JournalDocumentPoster.Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "reason_code", ("allowed_values", ReversalRules.ExternalReasons)));

        // The document of the settlement is found through the return (7.3a). The precheck below reads it again under the lock of the return row.
        Guid? documentId; Guid? companyId = null;
        await using (TenantTransaction probe = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct))
        {
            ReturnRow? row = await ReadReturnAsync(probe, tenantId, req.ReturnId, forUpdate: false, ct);
            // a replay of the same request finds the return unsettled: the idempotency record is looked at first
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(probe, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null && row is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(VatReturnHash2.Fingerprint(req)))
                    return Reject(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                PostedDocument? replay = earlier.EntityId is null ? null : await LedgerReader.LoadDocumentAsync(probe, tenantId, earlier.EntityId.Value, ct);
                return replay is null
                    ? CommandResult<SettledVatReturn>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                    : CommandResult<SettledVatReturn>.Ok(new SettledVatReturn(req.ReturnId, replay), earlier.CommandLogId, replay: true);
            }
            if (row is null) return Reject(JournalDocumentPoster.Error("VAT.RETURN_NOT_FOUND", "P2", "return_id", ("return_id", req.ReturnId)));
            if (row.FiledAt is not null) return Reject(JournalDocumentPoster.Error("VAT.ALREADY_FILED", "P2", "return_id", ("return_id", req.ReturnId)));
            documentId = row.SettledDocumentId; companyId = row.CompanyId;
        }
        if (documentId is null) return Reject(JournalDocumentPoster.Error("VAT.NOT_SETTLED", "P2", "return_id", ("return_id", req.ReturnId)));

        var mode = new DocumentReverser.ReverseMode(ReverseCommand, DocumentType, "VAT",
            async (tx, original, token) =>
            {
                ReturnRow? row = await ReadReturnAsync(tx, tenantId, req.ReturnId, forUpdate: true, token);
                if (row is null) return JournalDocumentPoster.Error("VAT.RETURN_NOT_FOUND", "P2", "return_id", ("return_id", req.ReturnId));
                if (row.FiledAt is not null) return JournalDocumentPoster.Error("VAT.ALREADY_FILED", "P2", "return_id", ("return_id", req.ReturnId));
                if (row.SettledDocumentId != documentId) return JournalDocumentPoster.Error("VAT.NOT_SETTLED", "P2", "return_id", ("return_id", req.ReturnId));
                // a superseded return whose successor along the chain is settled is reversed after that settlement (B9)
                for (Guid? id = row.SupersededBy; id is not null;)
                {
                    ReturnRow? next = await ReadReturnAsync(tx, tenantId, id.Value, forUpdate: false, token);
                    if (next is null) break;
                    if (next.SettledDocumentId is not null) return JournalDocumentPoster.Error("VAT.SUPERSEDED", "P2", "return_id", ("superseded_by_return_id", id));
                    id = next.SupersededBy;
                }
                return null;
            },
            async (tx, mirrorId, token) =>
            {
                await using (var cmd = tx.Command(
                    "update core.vat_return set settled_document_id = null where tenant_id = @t and company_id = @c and return_id = @r and settled_document_id = @d and filed_at is null"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId!.Value);
                    cmd.Parameters.AddWithValue("r", req.ReturnId); cmd.Parameters.AddWithValue("d", documentId!.Value);
                    if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("The settlement to reverse is no longer the settlement of the return");
                }
                await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "vat.settlement_reversed", "VAT_RETURN", $"{companyId.Value:D}/{req.ReturnId:D}", "SETTLEMENT_REVERSED",
                    DateTime.UtcNow, isAudit: true, null,
                    JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["return_id"] = req.ReturnId, ["document_id"] = documentId, ["reversal_document_id"] = mirrorId, ["reason_code"] = req.ReasonCode
                    }), token);
            });

        var inner = await new DocumentReverser(source).ReverseOwnedAsync(
            tenantId, ctx, new ReverseDocumentRequest(documentId.Value, req.ReversalDate, req.ReasonCode, req.ReasonText), VatReturnHash2.Fingerprint(req), mode, ct);
        if (inner.Status != CommandStatus.Ok)
        {
            // A reversal date that cannot be posted to is a VAT error (7.3a). No date shifting.
            var errors = inner.Errors.Select(e => e.Code.StartsWith("PERIOD.", StringComparison.Ordinal) || e.Code.StartsWith("POSTING_WINDOW.", StringComparison.Ordinal)
                ? NotPostable(req.ReversalDate, [e]) : e).ToList();
            return inner.Status == CommandStatus.Rejected ? CommandResult<SettledVatReturn>.Rejected(errors) : CommandResult<SettledVatReturn>.Failed(errors[0]);
        }
        return CommandResult<SettledVatReturn>.Ok(new SettledVatReturn(req.ReturnId, inner.Value!), inner.CommandLogId!.Value, inner.Warnings, inner.IdempotentReplay);
    }

    // ---------------------------------------------------------------------------------------------------------
    private static CommandError NotPostable(DateOnly date, IEnumerable<CommandError> causes) =>
        JournalDocumentPoster.Error("VAT.SETTLEMENT_DATE_NOT_POSTABLE", "P3", "posting_date", ("date", date), ("cause", causes.First().Code));

    private static async Task<ReturnRow?> ReadReturnAsync(TenantTransaction tx, Guid tenantId, Guid returnId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select company_id, registration_id, return_type, period_end_date, replaces_return_id, superseded_by_return_id, settled_document_id, filed_at, snapshot_digest " +
            "from core.vat_return where tenant_id = @t and return_id = @id" + (forUpdate ? " for update" : ""));
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", returnId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ReturnRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.IsDBNull(4) ? null : r.GetGuid(4),
                r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetDateTime(7), (byte[])r[8])
            : null;
    }

    /// <summary>The ledger amounts of the tax lines of a return, per account of the tax line (7.3).</summary>
    private static async Task<Dictionary<Guid, Sum>> SumsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid returnId, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Sum>();
        await using var cmd = tx.Command(
            "select l.account_id, sum(l.amount_lc), sum(l.amount_gc), coalesce(sum(l.amount_rc), 0) from core.vat_return_tax_line x " +
            "join core.journal_line l on l.tenant_id = x.tenant_id and l.company_id = x.company_id and l.document_id = x.document_id and l.line_no = x.line_no " +
            "where x.tenant_id = @t and x.company_id = @c and x.return_id = @r group by l.account_id");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", returnId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetGuid(0)] = new Sum(r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3));
        return result;
    }

    private static CommandResult<SettledVatReturn> Reject(CommandError error) => CommandResult<SettledVatReturn>.Rejected([error]);
}
