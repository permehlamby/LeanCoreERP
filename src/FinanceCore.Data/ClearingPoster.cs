using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// clearings:post (06, 7.1, 03, 5) for a clean match: open items of one GL_OPEN_ITEM account, cleared in full or in part against each
/// other, with no difference document. A set whose local, group or reporting amounts do not net to zero needs the two lines of 03, 3.1
/// and is rejected with LIMIT.CLEARING_DIFFERENCE_NOT_BUILT. Items on CONTROL accounts need a partner and wait for business_partner.
/// Locks (04, 6.3): the CLEARING chain head, the period row in share mode (A1: the status is read again after the head lock), the open
/// items sorted by key, outbox_head last. The amounts are derived after the locks, from the locked rows. A clean match consumes no
/// counter and no LEDGER head, so the pre-lock rule of 03, 5.6 has nothing to lock yet.
/// </summary>
public sealed class ClearingPoster(NpgsqlDataSource source)
{
    public const string Permission = "clearing.post";
    public const int MaxItems = 500;

    public async Task<CommandResult<PostedClearing>> PostAsync(
        Guid tenantId, CommandContext ctx, PostClearingRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_DENIED", "P0", "", ("permission", Permission)));

        List<CommandError> shape = Validate(ctx, req);
        if (shape.Count > 0) return CommandResult<PostedClearing>.Rejected(shape);

        byte[] fingerprint = ClearingRules.Fingerprint(req);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await PostOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<PostedClearing>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<PostedClearing>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // P1: the shape of the request, without the database
    // ---------------------------------------------------------------------------------------------------------
    internal static List<CommandError> Validate(CommandContext ctx, PostClearingRequest req)
    {
        var errors = new List<CommandError>();
        void Invalid(string path, string reason) => errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.SourceSystem) || ctx.SourceSystem.Length > 50) Invalid("source_system", "required, at most 50 characters");
        if (req.CompanyId == Guid.Empty) Invalid("company_id", "required");
        if (req.AccountId == Guid.Empty) Invalid("account_id", "required");
        if (!ClearingRules.ExternalMethods.Contains(req.ClearingMethod))
            errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "clearing_method", ("allowed_values", ClearingRules.ExternalMethods)));
        if (req.MatchingRef is { } m && (m.Length == 0 || m.Length > 100)) Invalid("matching_ref", "1 to 100 characters, or absent");
        if (req.Note is { } n && (n.Length == 0 || n.Length > 200)) Invalid("note", "1 to 200 characters, or absent");

        if (req.Items is null || req.Items.Count < 2)
        {
            errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "items", ("reason", "a clearing has at least two items")));
            return errors;
        }
        if (req.Items.Count > MaxItems)
        {
            errors.Add(JournalDocumentPoster.Error("SCHEMA.BATCH_TOO_LARGE", "P1", "items", ("limit", MaxItems)));
            return errors;
        }
        var seen = new HashSet<(Guid, int)>();
        for (int i = 0; i < req.Items.Count; i++)
        {
            ClearingItemRequest it = req.Items[i];
            if (it.DocumentId == Guid.Empty) Invalid($"items[{i}].open_item_document_id", "required");
            if (it.LineNo < 1) Invalid($"items[{i}].open_item_line_no", "at least 1");
            if (!seen.Add((it.DocumentId, it.LineNo))) Invalid($"items[{i}]", "the same open item twice in one clearing");
            if (it.ClearedAmountTc is { } a && Math.Round(a, Canonical.AmountScale) != a)
                Invalid($"items[{i}].cleared_amount_tc", $"at most {Canonical.AmountScale} fraction digits");
        }
        return errors;
    }

    // ---------------------------------------------------------------------------------------------------------
    private sealed record Locked(
        Guid CompanyId, Guid DocumentId, int LineNo, Guid? PartnerId, string Status, decimal ClearedTc, decimal ClearedLc, decimal ClearedGc, decimal? ClearedRc,
        Guid AccountId, decimal LineTc, decimal LineLc, decimal LineGc, decimal? LineRc, string CurrencyLc, string CurrencyGc, string? CurrencyRc, string CurrencyTc);

    private async Task<CommandResult<PostedClearing>> PostOnceAsync(
        Guid tenantId, CommandContext ctx, PostClearingRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Clearing, ct: ct);

        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            PostedClearing? replay = earlier.EntityId is null ? null : await LoadAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct);
            return replay is null
                ? CommandResult<PostedClearing>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                : CommandResult<PostedClearing>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        // P2: company and account (no lock: both are read again by what the locks protect)
        var errors = new List<CommandError>();
        JournalDocumentPoster.CompanyRow? company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, req.CompanyId, ct);
        if (company is null) return Reject(JournalDocumentPoster.Error("COMPANY.NOT_FOUND", "P2", "company_id"));
        if (company.Status != "ACTIVE") errors.Add(JournalDocumentPoster.Error("COMPANY.NOT_ACTIVE", "P2", "company_id", ("status", company.Status)));
        string? mode = await ReadOpenItemModeAsync(tx, tenantId, req.CompanyId, req.AccountId, ct);
        if (mode is null) errors.Add(JournalDocumentPoster.Error("ACCOUNT.NOT_FOUND", "P2", "account_id", ("account_id", req.AccountId)));
        else if (mode == "NONE") errors.Add(JournalDocumentPoster.Error("CLEARING.NOT_AN_OPEN_ITEM", "P2", "account_id", ("reason", "the account is not open-item managed")));
        else if (mode == "CONTROL") errors.Add(JournalDocumentPoster.Error("LIMIT.CONTROL_ACCOUNT_NEEDS_PARTNER", "P2", "account_id"));
        if (req.PartnerId is not null)
            errors.Add(JournalDocumentPoster.Error("ACCOUNT_RULE.PARTNER_FORBIDDEN", "P2", "partner_id", ("reason", "a GL_OPEN_ITEM account takes no partner")));
        if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);

        // P7: the CLEARING head, then the period row in share mode (A1), then the open items sorted by key, outbox_head last
        (long headSeq, byte[] headHash, _) = await CommandSupport.LockOrCreateHeadAsync(
            tx, tenantId, req.CompanyId, ClearingHash2.ChainType, "", LedgerHash2.Genesis(tenantId, req.CompanyId, ClearingHash2.ChainType, ""), ct);

        JournalDocumentPoster.PeriodCheck period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, req.CompanyId, req.ClearingDate, ctx, true, ct);
        errors.AddRange(period.Errors.Select(e => e with { Path = "clearing_date" }));
        if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);

        Dictionary<(Guid, int), Locked> locked = await LockItemsAsync(tx, tenantId, req.Items, ct);

        // P2, P4, P5, P6 after the locks, from the locked rows
        var allocations = new List<(ClearingItemRequest Request, Locked Row, Allocation Amounts)>();
        var scaleCache = new Dictionary<string, int>();
        async Task<int> Scale(string currency)
        {
            if (!scaleCache.TryGetValue(currency, out int s)) scaleCache[currency] = s = await ReadMinorUnitsAsync(tx, currency, ct);
            return s;
        }
        for (int i = 0; i < req.Items.Count; i++)
        {
            ClearingItemRequest it = req.Items[i];
            string path = $"items[{i}]";
            if (!locked.TryGetValue((it.DocumentId, it.LineNo), out Locked? row))
            {
                errors.Add(JournalDocumentPoster.Error("CLEARING.NOT_AN_OPEN_ITEM", "P2", path, ("document_id", it.DocumentId), ("line_no", it.LineNo)));
                continue;
            }
            if (row.CompanyId != req.CompanyId)
            {
                errors.Add(JournalDocumentPoster.Error("CLEARING.ITEM_OTHER_COMPANY", "P2", path));
                continue;
            }
            if (row.AccountId != req.AccountId)
            {
                errors.Add(JournalDocumentPoster.Error("CLEARING.ITEMS_NOT_SAME_ACCOUNT", "P4", path, ("account_id", row.AccountId)));
                continue;
            }
            if (row.PartnerId is not null)
            {
                errors.Add(JournalDocumentPoster.Error("CLEARING.ITEMS_NOT_SAME_PARTNER", "P4", path));
                continue;
            }
            if (row.Status == "CLEARED")
            {
                errors.Add(JournalDocumentPoster.Error("CONFLICT.ITEM_ALREADY_CLEARED", "P6", path, ("document_id", it.DocumentId), ("line_no", it.LineNo)));
                continue;
            }
            Allocation? a = ClearingRules.Allocate(
                new OpenLine(row.LineTc, row.LineLc, row.LineGc, row.LineRc, row.ClearedTc, row.ClearedLc, row.ClearedGc, row.ClearedRc),
                it.ClearedAmountTc, await Scale(row.CurrencyLc), await Scale(row.CurrencyGc), row.CurrencyRc is null ? null : await Scale(row.CurrencyRc), out string? error);
            if (a is null)
                errors.Add(JournalDocumentPoster.Error(error!, "P6", $"{path}.cleared_amount_tc", ("remaining_tc", row.LineTc - row.ClearedTc)));
            else
                allocations.Add((it, row, a));
        }
        if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);

        // P6: the set nets to zero. In one transaction currency, TC must (Q1). LC, GC and RC that do not need the difference
        // document of 03, 3.1, which is the next step.
        decimal sumTc = allocations.Sum(x => x.Amounts.Tc), sumLc = allocations.Sum(x => x.Amounts.Lc), sumGc = allocations.Sum(x => x.Amounts.Gc);
        decimal sumRc = allocations.Sum(x => x.Amounts.Rc ?? 0m);
        if (allocations.Select(x => x.Row.CurrencyTc).Distinct().Count() == 1 && sumTc != 0m)
            return Reject(JournalDocumentPoster.Error("CLEARING.TC_DIFFERENCE", "P6", "items", ("difference_tc", sumTc)));
        if (sumLc != 0m || sumGc != 0m || sumRc != 0m)
            return Reject(JournalDocumentPoster.Error("LIMIT.CLEARING_DIFFERENCE_NOT_BUILT", "P6", "items",
                ("difference_lc", sumLc), ("difference_gc", sumGc), ("difference_rc", sumRc)));

        // The node of the clearing chain
        Guid clearingId = Guid.NewGuid();
        long seq = headSeq + 1;
        DateTime now = DateTime.UtcNow;
        DateTime clearedAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        var body = new Dictionary<string, object?>
        {
            ["clearing_id"] = clearingId, ["clearing_type"] = "CLEAR", ["clearing_date"] = req.ClearingDate,
            ["fiscal_year"] = period.FiscalYear!.Value, ["fiscal_period"] = period.PeriodNo!.Value, ["cleared_at"] = clearedAt,
            ["account_id"] = req.AccountId, ["partner_id"] = null, ["document_id"] = null, ["reset_of_clearing_id"] = null,
            ["clearing_method"] = req.ClearingMethod, ["matching_ref"] = req.MatchingRef, ["source_system"] = ctx.SourceSystem,
            ["idempotency_key"] = ctx.IdempotencyKey, ["command_name"] = ClearingRules.PostCommand, ["cleared_by"] = ctx.Actor,
            ["correlation_id"] = ctx.CorrelationId
        };
        var itemRows = allocations.Select((x, n) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["clearing_id"] = clearingId, ["item_no"] = n + 1,
            ["clearing_date"] = req.ClearingDate, ["open_item_document_id"] = x.Row.DocumentId, ["open_item_line_no"] = x.Row.LineNo,
            ["currency_tc"] = x.Row.CurrencyTc, ["cleared_amount_tc"] = x.Amounts.Tc, ["cleared_amount_lc"] = x.Amounts.Lc,
            ["cleared_amount_gc"] = x.Amounts.Gc, ["cleared_amount_rc"] = x.Amounts.Rc
        }).ToList();
        byte[] rowHash = ClearingHash2.RowHash(tenantId, req.CompanyId, seq, headHash, body, ClearingHash2.ItemsDigest(itemRows), null);

        await InsertClearingAsync(tx, tenantId, req, ctx, clearingId, seq, period.FiscalYear!.Value, period.PeriodNo!.Value, clearedAt, headHash, rowHash, ct);
        var cleared = new List<ClearedItem>();
        for (int n = 0; n < allocations.Count; n++)
        {
            var (_, row, a) = allocations[n];
            await InsertItemAsync(tx, tenantId, req, clearingId, n + 1, row, a, ct);
            decimal tc = row.ClearedTc + a.Tc, lc = row.ClearedLc + a.Lc, gc = row.ClearedGc + a.Gc;
            decimal? rc = row.LineRc is null ? null : (row.ClearedRc ?? 0m) + (a.Rc ?? 0m);
            string status = ClearingRules.StatusOf(row.LineTc, row.LineLc, tc, lc);
            await UpdateOpenItemAsync(tx, tenantId, row, clearingId, status, tc, lc, gc, rc, ct);
            cleared.Add(new ClearedItem(n + 1, row.DocumentId, row.LineNo, row.CurrencyTc, a.Tc, a.Lc, a.Gc, a.Rc, status));
        }

        await using (var cmd = tx.Command(
            "update core.chain_head set last_seq = @s, last_hash = @h, hash_version = @v, updated_at = now() " +
            "where tenant_id = @t and company_id = @c and chain_type = 'CLEARING' and scope_key = ''"))
        {
            cmd.Parameters.AddWithValue("s", seq);
            cmd.Parameters.AddWithValue("h", rowHash);
            cmd.Parameters.AddWithValue("v", (short)ClearingHash2.Version);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, ClearingRules.PostCommand, fingerprint, "CLEARING", clearingId, ct);

        // Events (15, 6): the clearing carries its chain reference only, every open item says that it was cleared. outbox_head last.
        DateTime at = clearedAt;
        string chainRef = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["chain_type"] = "CLEARING", ["scope"] = "", ["chain_seq"] = seq, ["row_hash"] = Canonical.FormatHex32(rowHash)
        });
        await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "clearing.posted", "CLEARING", $"{req.CompanyId:D}/{clearingId:D}", "POSTED",
            at, isAudit: true, chainRef, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["clearing_id"] = clearingId, ["partner_id"] = null, ["account_id"] = req.AccountId, ["items"] = cleared.Count
            }), ct);
        foreach (ClearedItem c in cleared)
            await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "open_item.cleared", "OPEN_ITEM",
                $"{req.CompanyId:D}/{c.DocumentId:D}/{c.LineNo}", "CLEARED", at, isAudit: false, null,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["account_id"] = req.AccountId, ["partner_id"] = null, ["due_date"] = null,
                    ["amount_tc"] = c.AmountTc.ToString("F4", CultureInfo.InvariantCulture), ["currency"] = c.CurrencyTc,
                    ["clearing_id"] = clearingId, ["status"] = c.StatusAfter
                }), ct);

        await tx.CommitAsync(ct);
        return CommandResult<PostedClearing>.Ok(
            new PostedClearing(clearingId, req.CompanyId, seq, Canonical.FormatHex32(rowHash), Canonical.FormatHex32(headHash), req.ClearingDate,
                period.FiscalYear!.Value, period.PeriodNo!.Value, req.ClearingMethod, cleared),
            logId, period.Warnings);
    }

    // ---------------------------------------------------------------------------------------------------------
    private static async Task<string?> ReadOpenItemModeAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid accountId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select open_item_mode from core.gl_account where tenant_id = @t and company_id = @c and account_id = @a");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("a", accountId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<int> ReadMinorUnitsAsync(TenantTransaction tx, string currency, CancellationToken ct)
    {
        await using var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c");
        cmd.Parameters.AddWithValue("c", currency);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 4, CultureInfo.InvariantCulture);
    }

    /// <summary>Locks the open item rows in the order of their key (04, 6.3) and reads them with their lines. A key without an open item row is missing from the result.</summary>
    private static async Task<Dictionary<(Guid, int), Locked>> LockItemsAsync(
        TenantTransaction tx, Guid tenantId, IReadOnlyList<ClearingItemRequest> items, CancellationToken ct)
    {
        var rows = new Dictionary<(Guid, int), Locked>();
        await using var cmd = tx.Command(
            "select o.company_id, o.document_id, o.line_no, o.partner_id, o.clearing_status, o.cleared_amount_tc, o.cleared_amount_lc, " +
            "o.cleared_amount_gc, o.cleared_amount_rc, l.account_id, l.amount_tc, l.amount_lc, l.amount_gc, l.amount_rc, " +
            "l.currency_lc, l.currency_gc, l.currency_rc, d.transaction_currency " +
            "from core.journal_line_open_item o " +
            "join core.journal_line l on l.tenant_id = o.tenant_id and l.company_id = o.company_id and l.document_id = o.document_id and l.line_no = o.line_no " +
            "join core.journal_document d on d.tenant_id = o.tenant_id and d.company_id = o.company_id and d.document_id = o.document_id " +
            "join unnest(@docs, @lines) as k(doc, line) on k.doc = o.document_id and k.line = o.line_no " +
            "where o.tenant_id = @t order by o.document_id, o.line_no for update of o");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("docs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = items.Select(i => i.DocumentId).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("lines", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = items.Select(i => i.LineNo).ToArray() });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = new Locked(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4),
                r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.IsDBNull(8) ? null : r.GetDecimal(8), r.GetGuid(9),
                r.GetDecimal(10), r.GetDecimal(11), r.GetDecimal(12), r.IsDBNull(13) ? null : r.GetDecimal(13),
                r.GetString(14), r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16), r.GetString(17));
            rows[(row.DocumentId, row.LineNo)] = row;
        }
        return rows;
    }

    private static async Task InsertClearingAsync(
        TenantTransaction tx, Guid tenantId, PostClearingRequest req, CommandContext ctx, Guid clearingId, long seq, int fiscalYear, int period,
        DateTime clearedAt, byte[] prevHash, byte[] rowHash, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.clearing (tenant_id, company_id, clearing_id, chain_seq, clearing_type, clearing_date, fiscal_year, fiscal_period, " +
            "cleared_at, account_id, clearing_method, matching_ref, source_system, idempotency_key, command_name, cleared_by, correlation_id, note, " +
            "prev_hash, row_hash, hash_version) " +
            "values (@t, @c, @id, @seq, 'CLEAR', @date, @fy, @fp, @at, @acc, @method, @ref, @src, @key, @cmd, @by, @corr, @note, @prev, @hash, @v)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        cmd.Parameters.AddWithValue("id", clearingId);
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("date", req.ClearingDate);
        cmd.Parameters.AddWithValue("fy", fiscalYear);
        cmd.Parameters.AddWithValue("fp", period);
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = clearedAt });
        cmd.Parameters.AddWithValue("acc", req.AccountId);
        cmd.Parameters.AddWithValue("method", req.ClearingMethod);
        cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)req.MatchingRef ?? DBNull.Value });
        cmd.Parameters.AddWithValue("src", ctx.SourceSystem);
        cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
        cmd.Parameters.AddWithValue("cmd", ClearingRules.PostCommand);
        cmd.Parameters.AddWithValue("by", ctx.Actor);
        cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
        cmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)req.Note ?? DBNull.Value });
        cmd.Parameters.AddWithValue("prev", prevHash);
        cmd.Parameters.AddWithValue("hash", rowHash);
        cmd.Parameters.AddWithValue("v", (short)ClearingHash2.Version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertItemAsync(
        TenantTransaction tx, Guid tenantId, PostClearingRequest req, Guid clearingId, int itemNo, Locked row, Allocation a, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.clearing_item (tenant_id, company_id, clearing_id, item_no, clearing_date, open_item_document_id, open_item_line_no, " +
            "currency_tc, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, cleared_amount_rc) " +
            "values (@t, @c, @id, @n, @date, @doc, @line, @ccy, @tc, @lc, @gc, @rc)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        cmd.Parameters.AddWithValue("id", clearingId);
        cmd.Parameters.AddWithValue("n", itemNo);
        cmd.Parameters.AddWithValue("date", req.ClearingDate);
        cmd.Parameters.AddWithValue("doc", row.DocumentId);
        cmd.Parameters.AddWithValue("line", row.LineNo);
        cmd.Parameters.AddWithValue("ccy", row.CurrencyTc);
        cmd.Parameters.AddWithValue("tc", a.Tc);
        cmd.Parameters.AddWithValue("lc", a.Lc);
        cmd.Parameters.AddWithValue("gc", a.Gc);
        cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = (object?)a.Rc ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateOpenItemAsync(
        TenantTransaction tx, Guid tenantId, Locked row, Guid clearingId, string status, decimal tc, decimal lc, decimal gc, decimal? rc, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "update core.journal_line_open_item set clearing_status = @s, cleared_amount_tc = @tc, cleared_amount_lc = @lc, cleared_amount_gc = @gc, " +
            "cleared_amount_rc = @rc, last_clearing_id = @id where tenant_id = @t and company_id = @c and document_id = @d and line_no = @n");
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("tc", tc);
        cmd.Parameters.AddWithValue("lc", lc);
        cmd.Parameters.AddWithValue("gc", gc);
        cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = (object?)rc ?? DBNull.Value });
        cmd.Parameters.AddWithValue("id", clearingId);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", row.CompanyId);
        cmd.Parameters.AddWithValue("d", row.DocumentId);
        cmd.Parameters.AddWithValue("n", row.LineNo);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The open item row to update does not exist");
    }

    /// <summary>The clearing as stored, with the status that each of its open items has now. For the replay of an idempotent request.</summary>
    internal static async Task<PostedClearing?> LoadAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid clearingId, CancellationToken ct)
    {
        PostedClearing? head = null;
        await using (var cmd = tx.Command(
            "select chain_seq, row_hash, prev_hash, clearing_date, fiscal_year, fiscal_period, clearing_method from core.clearing " +
            "where tenant_id = @t and company_id = @c and clearing_id = @id"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("id", clearingId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                head = new PostedClearing(clearingId, companyId, r.GetInt64(0), Canonical.FormatHex32((byte[])r[1]), Canonical.FormatHex32((byte[])r[2]),
                    r.GetFieldValue<DateOnly>(3), r.GetInt32(4), r.GetInt32(5), r.GetString(6), []);
        }
        if (head is null) return null;
        var items = new List<ClearedItem>();
        await using (var cmd = tx.Command(
            "select i.item_no, i.open_item_document_id, i.open_item_line_no, i.currency_tc, i.cleared_amount_tc, i.cleared_amount_lc, i.cleared_amount_gc, " +
            "i.cleared_amount_rc, o.clearing_status from core.clearing_item i " +
            "join core.journal_line_open_item o on o.tenant_id = i.tenant_id and o.company_id = i.company_id and o.document_id = i.open_item_document_id and o.line_no = i.open_item_line_no " +
            "where i.tenant_id = @t and i.company_id = @c and i.clearing_id = @id order by i.item_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("id", clearingId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new ClearedItem(r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6),
                    r.IsDBNull(7) ? null : r.GetDecimal(7), r.GetString(8)));
        }
        return head with { Items = items };
    }

    private static CommandResult<PostedClearing> Reject(CommandError error) => CommandResult<PostedClearing>.Rejected([error]);
}
