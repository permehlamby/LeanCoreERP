using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Reads;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The read side of the ledger (09): journal-documents:get and :list, balances:query, trial-balances:query, account-ledgers:query, open-items:query and
/// partner-balances:query. Every read runs in one repeatable-read transaction of the read role, so a response is one consistent snapshot (09, 2.2), and
/// carries the ledger position it is consistent with. A caller that needs to see its own posting passes min_ledger_seq (READ.STALE_SNAPSHOT otherwise).
/// Not built: company access beyond the tenant, several companies and groups, extensions, personal data and the read log, sort and filter on every field,
/// the other reports of 4.5, 4.7 and 4.8. Amounts are decimals, not strings, as everywhere in this API.
/// </summary>
public sealed class LedgerReads(NpgsqlDataSource source)
{
    public const string Permission = "read.ledger";

    private static readonly string[] BalanceSheet = ["ASSET", "LIABILITY", "EQUITY"];
    private static readonly string[] ClosingTypes = ["YEAR_END", "CLOSING"];

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);

    private static CommandError? Denied(CommandContext ctx) =>
        ctx.Permissions.Contains(Permission) ? null : Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission));

    private sealed record CompanyInfo(string Lc, string? Rc);

    /// <summary>The company and the group currency at the date, and the position of its ledgers. Errors: READ.NOT_FOUND, READ.STALE_SNAPSHOT.</summary>
    private static async Task<(CompanyInfo? Company, CurrencyCodes? Codes, ReadSnapshot? Snapshot, CommandError? Error)> OpenAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly groupDate, long? minSeq, CancellationToken ct)
    {
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return (null, null, null, Error("READ.NOT_FOUND", "P2", "company_id"));
        string? group = await ClearingPoster.ReadGroupCurrencyAsync(tx, tenantId, companyId, groupDate, ct);
        if (group is null) return (null, null, null, Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "company_id", ("posting_date", groupDate)));
        var ledgers = new List<LedgerPosition>();
        await using (var cmd = tx.Command("select scope_key, last_seq from core.chain_head where tenant_id = @t and company_id = @c and chain_type = 'LEDGER' order by scope_key"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ledgers.Add(new LedgerPosition(Guid.Parse(r.GetString(0)), r.GetInt64(1)));
        }
        if (minSeq is { } min && ledgers.All(l => l.LedgerSeq < min))
            return (null, null, null, Error("READ.STALE_SNAPSHOT", "P2", "min_ledger_seq", ("min_ledger_seq", min)) with { Retryable = true });
        DateTime now = DateTime.UtcNow;
        return (new CompanyInfo(company.LocalCurrency, company.ReportingCurrency), new CurrencyCodes(company.LocalCurrency, group, company.ReportingCurrency),
            new ReadSnapshot(new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc), ledgers), null);
    }

    private static Amounts Amt(NpgsqlDataReader r, int i, bool hasRc) => new(r.GetDecimal(i), r.GetDecimal(i + 1), hasRc ? r.GetDecimal(i + 2) : null);

    private static Amounts Add(Amounts a, Amounts b) => new(a.Lc + b.Lc, a.Gc + b.Gc, a.Rc is null ? null : a.Rc + (b.Rc ?? 0m));

    private static readonly Amounts ZeroNoRc = new(0m, 0m, null);

    private static async Task<bool> AllHardClosedAsync(TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly? from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select not exists (select 1 from core.fiscal_period p where p.tenant_id = @t and p.company_id = @c and p.status <> 'HARD_CLOSED' " +
            "and p.start_date <= @to and (@from::date is null or p.end_date >= @from))");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = (object?)from ?? DBNull.Value });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<int?> FiscalYearOfAsync(TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly date, CancellationToken ct)
    {
        await using var cmd = tx.Command("select fiscal_year from core.fiscal_year where tenant_id = @t and company_id = @c and start_date <= @d and end_date >= @d");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", date);
        return await cmd.ExecuteScalarAsync(ct) is int y ? y : null;
    }

    private static CommandError? CheckLimit(int? limit, out int effective)
    {
        effective = limit ?? ReadRules.DefaultLimit;
        return effective is < 1 or > ReadRules.MaxLimit ? Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit)) : null;
    }

    // ---------------------------------------------------------------------------------------------------------
    // journal-documents:get and :list
    // ---------------------------------------------------------------------------------------------------------
    private const string DocumentSelect =
        "d.document_id, d.company_id, d.ledger_id, d.document_type, d.document_number, d.fiscal_year, d.fiscal_period, d.document_date, d.posting_date, d.transaction_currency, " +
        "d.status, d.is_system_generated, d.reversal_of_document_id, d.reversed_by_document_id, d.reversal_reason_code, d.origin, d.header_text, d.external_reference, " +
        "d.chain_seq, d.row_hash, d.posted_by, d.correlation_id, d.posted_at";

    private static JournalDocumentRead ReadDocument(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetFieldValue<DateOnly>(7), r.GetFieldValue<DateOnly>(8),
        r.GetString(9).Trim(), r.GetString(10), r.GetBoolean(11), r.IsDBNull(12) ? null : r.GetGuid(12), r.IsDBNull(13) ? null : r.GetGuid(13), r.IsDBNull(14) ? null : r.GetString(14),
        r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? null : r.GetString(17), r.GetInt64(18), Canonical.FormatHex32((byte[])r[19]), r.GetString(20), r.GetString(21),
        DateTime.SpecifyKind(r.GetDateTime(22), DateTimeKind.Utc), null);

    private static async Task<Dictionary<Guid, List<JournalLineRead>>> ReadLinesAsync(
        TenantTransaction tx, Guid tenantId, IReadOnlyCollection<Guid> documentIds, bool hasRc, CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<JournalLineRead>>();
        if (documentIds.Count == 0) return result;
        await using var cmd = tx.Command(
            "select l.document_id, l.line_no, l.line_type, l.account_id, l.amount_tc, d.transaction_currency, l.amount_lc, l.amount_gc, l.amount_rc, l.reversed_line_no, o.partner_id, l.line_text " +
            "from core.journal_line l join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "left join core.journal_line_open_item o on o.tenant_id = l.tenant_id and o.company_id = l.company_id and o.document_id = l.document_id and o.line_no = l.line_no " +
            "where l.tenant_id = @t and l.document_id = any(@ids) order by l.document_id, l.line_no");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("ids", documentIds.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (!result.TryGetValue(r.GetGuid(0), out var list)) result[r.GetGuid(0)] = list = [];
            list.Add(new JournalLineRead(r.GetInt32(1), r.GetString(2), r.GetGuid(3), r.GetDecimal(4), r.GetString(5).Trim(),
                new Amounts(r.GetDecimal(6), r.GetDecimal(7), hasRc && !r.IsDBNull(8) ? r.GetDecimal(8) : null), r.IsDBNull(9) ? null : r.GetInt32(9),
                r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? null : r.GetString(11)));
        }
        return result;
    }

    public async Task<CommandResult<JournalDocumentResult>> GetDocumentAsync(Guid tenantId, CommandContext ctx, GetJournalDocumentRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<JournalDocumentResult>(denied);
        if (req.DocumentId == Guid.Empty) return Reject<JournalDocumentResult>(Error("SCHEMA.INVALID_VALUE", "P1", "document_id", ("reason", "required")));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        JournalDocumentRead? doc = null;
        await using (var cmd = tx.Command($"select {DocumentSelect} from core.journal_document d where d.tenant_id = @t and d.document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", req.DocumentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) doc = ReadDocument(r);
        }
        if (doc is null) return Reject<JournalDocumentResult>(Error("READ.NOT_FOUND", "P2", "document_id"));
        var opened = await OpenAsync(tx, tenantId, doc.CompanyId, doc.PostingDate, req.MinLedgerSeq, ct);
        if (opened.Error is not null) return Reject<JournalDocumentResult>(opened.Error);
        if (req.IncludeLines)
            doc = doc with { Lines = (await ReadLinesAsync(tx, tenantId, [doc.DocumentId], opened.Company!.Rc is not null, ct)).GetValueOrDefault(doc.DocumentId) ?? [] };
        return CommandResult<JournalDocumentResult>.Ok(new JournalDocumentResult(doc, opened.Snapshot!), Guid.Empty);
    }

    public async Task<CommandResult<JournalDocumentList>> ListDocumentsAsync(Guid tenantId, CommandContext ctx, ListJournalDocumentsRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<JournalDocumentList>(denied);
        if (CheckLimit(req.Limit, out int limit) is { } badLimit) return Reject<JournalDocumentList>(badLimit);
        if (!ReadRules.TryDecodeCursor(req.Cursor, "doc", out long after)) return Reject<JournalDocumentList>(Error("READ.CURSOR_INVALID", "P1", "cursor"));
        if (req.Status is not (null or "posted" or "reversed")) return Reject<JournalDocumentList>(Error("READ.INVALID_FILTER", "P1", "status", ("allowed", new[] { "posted", "reversed" })));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var opened = await OpenAsync(tx, tenantId, req.CompanyId, req.PostingDateTo ?? DateOnly.FromDateTime(DateTime.UtcNow), req.MinLedgerSeq, ct);
        if (opened.Error is not null && opened.Error.Code != "CONFIG.GROUP_MEMBERSHIP_MISSING") return Reject<JournalDocumentList>(opened.Error);
        // the group currency is not needed here: a list without it still carries the ledger position
        var snapshot = opened.Snapshot ?? await SnapshotOnlyAsync(tx, tenantId, req.CompanyId, ct);
        if (req.MinLedgerSeq is { } min && snapshot.Ledgers.All(l => l.LedgerSeq < min))
            return Reject<JournalDocumentList>(Error("READ.STALE_SNAPSHOT", "P2", "min_ledger_seq", ("min_ledger_seq", min)) with { Retryable = true });

        var items = new List<JournalDocumentRead>();
        await using (var cmd = tx.Command(
            $"select {DocumentSelect} from core.journal_document d where d.tenant_id = @t and d.company_id = @c and d.chain_seq > @after " +
            "and (@type::text is null or d.document_type = @type) and (@fy::int is null or d.fiscal_year = @fy) and (@fp::int is null or d.fiscal_period = @fp) " +
            "and (@from::date is null or d.posting_date >= @from) and (@to::date is null or d.posting_date <= @to) and (@status::text is null or d.status = @status) " +
            "and (@prefix::text is null or d.document_number like @prefix || '%') order by d.chain_seq, d.document_id limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("after", after); cmd.Parameters.AddWithValue("n", limit + 1);
            cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = (object?)req.DocumentType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("fy", NpgsqlDbType.Integer) { Value = (object?)req.FiscalYear ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("fp", NpgsqlDbType.Integer) { Value = (object?)req.FiscalPeriod ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = (object?)req.PostingDateFrom ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = (object?)req.PostingDateTo ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)req.Status ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("prefix", NpgsqlDbType.Text) { Value = (object?)req.NumberPrefix ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(ReadDocument(r));
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor("doc", items[^1].ChainSeq); }
        if (req.IncludeLines)
        {
            var lines = await ReadLinesAsync(tx, tenantId, items.Select(i => i.DocumentId).ToList(), opened.Company?.Rc is not null || await HasRcAsync(tx, tenantId, req.CompanyId, ct), ct);
            items = items.Select(i => i with { Lines = lines.GetValueOrDefault(i.DocumentId) ?? [] }).ToList();
        }
        return CommandResult<JournalDocumentList>.Ok(new JournalDocumentList(items, new ReadPage(next, limit), snapshot), Guid.Empty);
    }

    private static async Task<bool> HasRcAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select reporting_currency is not null from core.company where tenant_id = @t and company_id = @c");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<ReadSnapshot> SnapshotOnlyAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var ledgers = new List<LedgerPosition>();
        await using var cmd = tx.Command("select scope_key, last_seq from core.chain_head where tenant_id = @t and company_id = @c and chain_type = 'LEDGER' order by scope_key");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ledgers.Add(new LedgerPosition(Guid.Parse(r.GetString(0)), r.GetInt64(1)));
        DateTime now = DateTime.UtcNow;
        return new ReadSnapshot(new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc), ledgers);
    }

    // ---------------------------------------------------------------------------------------------------------
    // balances:query (09, 4.1)
    // ---------------------------------------------------------------------------------------------------------
    private static readonly Dictionary<string, string> GroupColumns = new()
    {
        ["account_id"] = "l.account_id::text", ["account_type"] = "a.account_type", ["account_group"] = "a.account_group", ["fiscal_year"] = "l.fiscal_year::text",
        ["fiscal_period"] = "l.fiscal_period::text", ["posting_date"] = "l.posting_date::text", ["cost_center_id"] = "l.cost_center_id::text", ["project_id"] = "l.project_id::text",
        ["profit_center_id"] = "l.profit_center_id::text", ["business_unit_id"] = "l.business_unit_id::text", ["transaction_currency"] = "d.transaction_currency::text",
        ["document_type"] = "d.document_type", ["line_type"] = "l.line_type", ["ledger_id"] = "l.ledger_id::text"
    };

    public async Task<CommandResult<BalancesResult>> BalancesAsync(Guid tenantId, CommandContext ctx, BalancesRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<BalancesResult>(denied);
        var shape = new List<CommandError>();
        string rule = req.Rule ?? "AUTO";
        if (rule is not ("AUTO" or "RAW")) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "rule", ("reason", "AUTO or RAW")));
        DateOnly from = req.FromDate ?? default, to;
        if (req.Mode == "CUMULATIVE")
        {
            if (req.AsOfDate is null) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "as_of_date", ("reason", "required for CUMULATIVE")));
            to = req.AsOfDate ?? default;
        }
        else if (req.Mode == "MOVEMENT")
        {
            if (req.FromDate is null || req.ToDate is null || req.FromDate > req.ToDate) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "from_date", ("reason", "from_date and to_date are required, from not after to")));
            to = req.ToDate ?? default;
        }
        else
        {
            shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "mode", ("reason", "CUMULATIVE or MOVEMENT")));
            to = default;
        }
        foreach (string g in req.GroupBy ?? [])
            if (!GroupColumns.ContainsKey(g)) shape.Add(Error("READ.INVALID_FILTER", "P1", "group_by", ("field", g), ("allowed", GroupColumns.Keys.ToArray())));
        if (req.AccountType is not null && req.AccountType is not ("ASSET" or "LIABILITY" or "EQUITY" or "REVENUE" or "EXPENSE"))
            shape.Add(Error("READ.INVALID_FILTER", "P1", "account_type", ("field", "account_type")));
        if (shape.Count > 0) return CommandResult<BalancesResult>.Rejected(shape);

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var opened = await OpenAsync(tx, tenantId, req.CompanyId, to, req.MinLedgerSeq, ct);
        if (opened.Error is not null) return Reject<BalancesResult>(opened.Error);
        bool hasRc = opened.Company!.Rc is not null;
        int? fiscalYear = rule == "AUTO" ? await FiscalYearOfAsync(tx, tenantId, req.CompanyId, to, ct) : null;

        string[] keys = (req.GroupBy ?? []).Distinct().ToArray();
        var excluded = (req.ExcludeDocumentTypes ?? []).ToArray();
        string keySql = string.Join(", ", keys.Select((k, i) => $"{GroupColumns[k]} as k{i}"));
        string dateRange = req.Mode == "CUMULATIVE" ? "l.posting_date <= @to" : "l.posting_date between @from and @to";
        string autoPl = req.Mode == "CUMULATIVE"
            ? "(a.account_type = any(@bs) or (l.fiscal_year = @fy and d.document_type <> all(@closing)))"
            : "(a.account_type = any(@bs) or d.document_type <> all(@closing))";
        string sql =
            "select " + (keys.Length > 0 ? keySql + ", " : "") +
            "coalesce(sum(greatest(l.amount_lc, 0)), 0), coalesce(sum(greatest(l.amount_gc, 0)), 0), coalesce(sum(greatest(l.amount_rc, 0)), 0), " +
            "coalesce(sum(least(l.amount_lc, 0)), 0), coalesce(sum(least(l.amount_gc, 0)), 0), coalesce(sum(least(l.amount_rc, 0)), 0), count(*) " +
            "from core.journal_line l join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            "where l.tenant_id = @t and l.company_id = @c and " + dateRange + (rule == "AUTO" ? " and " + autoPl : "") +
            " and (@acc::uuid is null or l.account_id = @acc) and (@atype::text is null or a.account_type = @atype) and (@dtype::text is null or d.document_type = @dtype) " +
            "and (@ltype::text is null or l.line_type = @ltype) and d.document_type <> all(@excl) " +
            (keys.Length > 0 ? "group by " + string.Join(", ", Enumerable.Range(1, keys.Length)) + " order by " + string.Join(", ", Enumerable.Range(1, keys.Length)) : "");
        var rows = new List<BalanceRow>();
        await using (var cmd = tx.Command(sql))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("to", to);
            if (req.Mode == "MOVEMENT") cmd.Parameters.AddWithValue("from", from);
            if (rule == "AUTO")
            {
                cmd.Parameters.AddWithValue("bs", BalanceSheet); cmd.Parameters.AddWithValue("closing", ClosingTypes);
                if (req.Mode == "CUMULATIVE") cmd.Parameters.Add(new NpgsqlParameter("fy", NpgsqlDbType.Integer) { Value = (object?)fiscalYear ?? -1 });
            }
            cmd.Parameters.Add(new NpgsqlParameter("acc", NpgsqlDbType.Uuid) { Value = (object?)req.AccountId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("atype", NpgsqlDbType.Text) { Value = (object?)req.AccountType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("dtype", NpgsqlDbType.Text) { Value = (object?)req.DocumentType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ltype", NpgsqlDbType.Text) { Value = (object?)req.LineType ?? DBNull.Value });
            cmd.Parameters.AddWithValue("excl", excluded);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (rows.Count >= ReadRules.MaxGroups) return Reject<BalancesResult>(Error("READ.RESULT_TOO_LARGE", "P2", "group_by", ("max", ReadRules.MaxGroups)));
                var k = new Dictionary<string, string?>();
                for (int i = 0; i < keys.Length; i++) k[keys[i]] = r.IsDBNull(i) ? null : r.GetString(i).Trim();
                int o = keys.Length;
                var debit = Amt(r, o, hasRc); var credit = Amt(r, o + 3, hasRc);
                rows.Add(new BalanceRow(k, debit, credit, Add(debit, credit), r.GetInt64(o + 6)));
            }
        }
        Amounts totalDebit = rows.Aggregate(hasRc ? new Amounts(0m, 0m, 0m) : ZeroNoRc, (s, x) => Add(s, x.Debit));
        Amounts totalCredit = rows.Aggregate(hasRc ? new Amounts(0m, 0m, 0m) : ZeroNoRc, (s, x) => Add(s, x.Credit));
        bool stable = await AllHardClosedAsync(tx, tenantId, req.CompanyId, req.Mode == "MOVEMENT" ? from : null, to, ct);
        return CommandResult<BalancesResult>.Ok(new BalancesResult(req.Mode, rule, opened.Codes!, rows, totalDebit, totalCredit, stable, opened.Snapshot!), Guid.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------
    // trial-balances:query (09, 4.2)
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<TrialBalanceResult>> TrialBalanceAsync(Guid tenantId, CommandContext ctx, TrialBalanceRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<TrialBalanceResult>(denied);
        if (req.FiscalYear < 1 || req.FromPeriod < 1 || req.ToPeriod < req.FromPeriod)
            return Reject<TrialBalanceResult>(Error("SCHEMA.INVALID_VALUE", "P1", "from_period", ("reason", "fiscal_year, from_period and to_period are required, from not after to")));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        DateOnly? end = null;
        await using (var cmd = tx.Command("select end_date from core.fiscal_year where tenant_id = @t and company_id = @c and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("y", req.FiscalYear);
            if (await cmd.ExecuteScalarAsync(ct) is DateTime e) end = DateOnly.FromDateTime(e);
        }
        if (end is null) return Reject<TrialBalanceResult>(Error("READ.NOT_FOUND", "P2", "fiscal_year", ("fiscal_year", req.FiscalYear)));
        var opened = await OpenAsync(tx, tenantId, req.CompanyId, end.Value, req.MinLedgerSeq, ct);
        if (opened.Error is not null) return Reject<TrialBalanceResult>(opened.Error);
        bool hasRc = opened.Company!.Rc is not null;

        var rows = new List<TrialBalanceRow>();
        await using (var cmd = tx.Command(
            "with x as (select l.account_id, l.amount_lc lc, l.amount_gc gc, coalesce(l.amount_rc, 0) rc, " +
            " ((l.fiscal_year < @y or (l.fiscal_year = @y and l.fiscal_period < @from)) and (a.account_type = any(@bs) or (l.fiscal_year = @y and d.document_type <> all(@closing)))) as prior, " +
            " (l.fiscal_year = @y and l.fiscal_period between @from and @to and (@sp or p.period_type = 'REGULAR') and (a.account_type = any(@bs) or d.document_type <> all(@closing))) as inr " +
            " from core.journal_line l join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            " join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            " join core.fiscal_period p on p.tenant_id = l.tenant_id and p.company_id = l.company_id and p.fiscal_year = l.fiscal_year and p.period_no = l.fiscal_period " +
            " where l.tenant_id = @t and l.company_id = @c and (l.fiscal_year < @y or (l.fiscal_year = @y and l.fiscal_period <= @to))) " +
            "select a.account_id, a.account_no, a.name, a.account_type, " +
            "coalesce(sum(lc) filter (where prior), 0), coalesce(sum(gc) filter (where prior), 0), coalesce(sum(rc) filter (where prior), 0), " +
            "coalesce(sum(greatest(lc, 0)) filter (where inr), 0), coalesce(sum(greatest(gc, 0)) filter (where inr), 0), coalesce(sum(greatest(rc, 0)) filter (where inr), 0), " +
            "coalesce(sum(least(lc, 0)) filter (where inr), 0), coalesce(sum(least(gc, 0)) filter (where inr), 0), coalesce(sum(least(rc, 0)) filter (where inr), 0) " +
            "from x join core.gl_account a on a.tenant_id = @t and a.company_id = @c and a.account_id = x.account_id " +
            "group by a.account_id, a.account_no, a.name, a.account_type having bool_or(prior or inr) order by a.account_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("y", req.FiscalYear);
            cmd.Parameters.AddWithValue("from", req.FromPeriod); cmd.Parameters.AddWithValue("to", req.ToPeriod); cmd.Parameters.AddWithValue("sp", req.IncludeSpecialPeriods);
            cmd.Parameters.AddWithValue("bs", BalanceSheet); cmd.Parameters.AddWithValue("closing", ClosingTypes);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (rows.Count >= ReadRules.MaxGroups) return Reject<TrialBalanceResult>(Error("READ.RESULT_TOO_LARGE", "P2", "fiscal_year", ("max", ReadRules.MaxGroups)));
                Amounts opening = Amt(r, 4, hasRc), debit = Amt(r, 7, hasRc), credit = Amt(r, 10, hasRc);
                rows.Add(new TrialBalanceRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), opening, debit, credit, Add(Add(opening, debit), credit)));
            }
        }
        Amounts zero = hasRc ? new Amounts(0m, 0m, 0m) : ZeroNoRc;
        Amounts totalDebit = rows.Aggregate(zero, (s, x) => Add(s, x.Debit)), totalCredit = rows.Aggregate(zero, (s, x) => Add(s, x.Credit));
        bool foots = totalDebit.Lc + totalCredit.Lc == 0m && totalDebit.Gc + totalCredit.Gc == 0m && (!hasRc || totalDebit.Rc + totalCredit.Rc == 0m);
        DateOnly? periodStart = null;
        await using (var cmd = tx.Command("select start_date from core.fiscal_period where tenant_id = @t and company_id = @c and fiscal_year = @y and period_no = @p"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("y", req.FiscalYear); cmd.Parameters.AddWithValue("p", req.FromPeriod);
            if (await cmd.ExecuteScalarAsync(ct) is DateTime s) periodStart = DateOnly.FromDateTime(s);
        }
        bool stable = await AllHardClosedAsync(tx, tenantId, req.CompanyId, periodStart, end.Value, ct);
        return CommandResult<TrialBalanceResult>.Ok(new TrialBalanceResult(req.FiscalYear, req.FromPeriod, req.ToPeriod, opened.Codes!, rows, totalDebit, totalCredit, foots, stable, opened.Snapshot!), Guid.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------
    // account-ledgers:query (09, 4.3)
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<AccountLedgerResult>> AccountLedgerAsync(Guid tenantId, CommandContext ctx, AccountLedgerRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<AccountLedgerResult>(denied);
        if (CheckLimit(req.Limit, out int limit) is { } badLimit) return Reject<AccountLedgerResult>(badLimit);
        if (!ReadRules.TryDecodeCursor(req.Cursor, "ledger", out long offset)) return Reject<AccountLedgerResult>(Error("READ.CURSOR_INVALID", "P1", "cursor"));
        if (req.FromDate > req.ToDate) return Reject<AccountLedgerResult>(Error("SCHEMA.INVALID_VALUE", "P1", "from_date", ("reason", "from_date is after to_date")));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var opened = await OpenAsync(tx, tenantId, req.CompanyId, req.ToDate, req.MinLedgerSeq, ct);
        if (opened.Error is not null) return Reject<AccountLedgerResult>(opened.Error);
        bool hasRc = opened.Company!.Rc is not null;
        string? accountNo = null, accountType = null;
        await using (var cmd = tx.Command("select account_no, account_type from core.gl_account where tenant_id = @t and company_id = @c and account_id = @a"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("a", req.AccountId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { accountNo = r.GetString(0); accountType = r.GetString(1); }
        }
        if (accountNo is null) return Reject<AccountLedgerResult>(Error("READ.NOT_FOUND", "P2", "account_id"));

        // the opening balance by the rule AUTO (4.1.1): balance sheet all lines before the date, P&L the lines of the fiscal year before the date, without the closing documents
        int? year = BalanceSheet.Contains(accountType!) ? null : await FiscalYearOfAsync(tx, tenantId, req.CompanyId, req.FromDate, ct);
        Amounts opening;
        await using (var cmd = tx.Command(
            "select coalesce(sum(l.amount_lc), 0), coalesce(sum(l.amount_gc), 0), coalesce(sum(l.amount_rc), 0) from core.journal_line l " +
            "join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "where l.tenant_id = @t and l.company_id = @c and l.account_id = @a and l.posting_date < @from " +
            "and (@bs or (l.fiscal_year = @fy and d.document_type <> all(@closing)))"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("a", req.AccountId);
            cmd.Parameters.AddWithValue("from", req.FromDate); cmd.Parameters.AddWithValue("bs", year is null); cmd.Parameters.AddWithValue("closing", ClosingTypes);
            cmd.Parameters.Add(new NpgsqlParameter("fy", NpgsqlDbType.Integer) { Value = (object?)year ?? -1 });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            opening = Amt(r, 0, hasRc);
        }

        const string rangeWhere =
            "from core.journal_line l join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "where l.tenant_id = @t and l.company_id = @c and l.account_id = @a and l.posting_date between @from and @to " +
            "and (@dtype::text is null or d.document_type = @dtype) and (@ltype::text is null or l.line_type = @ltype)";
        void Bind(NpgsqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("a", req.AccountId);
            cmd.Parameters.AddWithValue("from", req.FromDate); cmd.Parameters.AddWithValue("to", req.ToDate);
            cmd.Parameters.Add(new NpgsqlParameter("dtype", NpgsqlDbType.Text) { Value = (object?)req.DocumentType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ltype", NpgsqlDbType.Text) { Value = (object?)req.LineType ?? DBNull.Value });
        }

        Amounts closing = opening;
        await using (var cmd = tx.Command("select coalesce(sum(l.amount_lc), 0), coalesce(sum(l.amount_gc), 0), coalesce(sum(l.amount_rc), 0) " + rangeWhere))
        {
            Bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            closing = Add(opening, Amt(r, 0, hasRc));
        }

        var lines = new List<AccountLedgerLine>();
        await using (var cmd = tx.Command(
            "select * from (select l.document_id, d.document_number, d.document_type, d.chain_seq, l.line_no, l.posting_date, l.line_type, l.line_text, l.amount_tc, d.transaction_currency, " +
            "l.amount_lc, l.amount_gc, coalesce(l.amount_rc, 0), " +
            "sum(l.amount_lc) over w, sum(l.amount_gc) over w, sum(coalesce(l.amount_rc, 0)) over w " + rangeWhere +
            " window w as (order by l.posting_date, d.chain_seq, l.line_no)) s order by posting_date, chain_seq, line_no offset @o limit @n"))
        {
            Bind(cmd);
            cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("n", limit + 1);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var run = Amt(r, 13, hasRc);
                lines.Add(new AccountLedgerLine(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetFieldValue<DateOnly>(5), r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7), r.GetDecimal(8), r.GetString(9).Trim(), Amt(r, 10, hasRc), Add(opening, run)));
            }
        }
        string? next = null;
        if (lines.Count > limit) { lines.RemoveAt(limit); next = ReadRules.EncodeCursor("ledger", offset + limit); }
        return CommandResult<AccountLedgerResult>.Ok(new AccountLedgerResult(req.AccountId, accountNo, opened.Codes!, opening, lines, closing, new ReadPage(next, limit), opened.Snapshot!), Guid.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------
    // open-items:query and partner-balances:query (09, 4.4, 4.6)
    // ---------------------------------------------------------------------------------------------------------
    /// <summary>The open items as of a date from the line amounts and the clearing items up to the date, not from the cache (05, A1). The columns: 0 to 21, see Read.</summary>
    private const string OpenItemSql =
        "select l.document_id, l.line_no, d.document_number, l.account_id, o.partner_id, l.posting_date, o.due_date, d.transaction_currency, " +
        "l.amount_tc, coalesce(c.tc, 0), l.amount_lc, coalesce(c.lc, 0), l.amount_gc, coalesce(c.gc, 0), coalesce(l.amount_rc, 0), coalesce(c.rc, 0), o.clearing_status " +
        "from core.journal_line_open_item o " +
        "join core.journal_line l on l.tenant_id = o.tenant_id and l.company_id = o.company_id and l.document_id = o.document_id and l.line_no = o.line_no " +
        "join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
        "left join lateral (select sum(i.cleared_amount_tc) tc, sum(i.cleared_amount_lc) lc, sum(i.cleared_amount_gc) gc, sum(i.cleared_amount_rc) rc from core.clearing_item i " +
        " where i.tenant_id = l.tenant_id and i.company_id = l.company_id and i.open_item_document_id = l.document_id and i.open_item_line_no = l.line_no and i.clearing_date <= @asof) c on true " +
        "where o.tenant_id = @t and o.company_id = @c and l.posting_date <= @asof " +
        "and (@acc::uuid is null or l.account_id = @acc) and (@partner::uuid is null or o.partner_id = @partner) and (@duefrom::date is null or o.due_date >= @duefrom) " +
        "and (@dueto::date is null or o.due_date <= @dueto) and (@ccy::text is null or d.transaction_currency = @ccy) " +
        "and (@all or l.amount_tc - coalesce(c.tc, 0) <> 0 or l.amount_lc - coalesce(c.lc, 0) <> 0 or l.amount_gc - coalesce(c.gc, 0) <> 0)";

    private static void BindOpenItems(NpgsqlCommand cmd, Guid tenantId, Guid companyId, DateOnly asOf, Guid? account, Guid? partner, DateOnly? dueFrom, DateOnly? dueTo, string? currency, bool all)
    {
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("asof", asOf); cmd.Parameters.AddWithValue("all", all);
        cmd.Parameters.Add(new NpgsqlParameter("acc", NpgsqlDbType.Uuid) { Value = (object?)account ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("partner", NpgsqlDbType.Uuid) { Value = (object?)partner ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("duefrom", NpgsqlDbType.Date) { Value = (object?)dueFrom ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("dueto", NpgsqlDbType.Date) { Value = (object?)dueTo ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ccy", NpgsqlDbType.Text) { Value = (object?)currency ?? DBNull.Value });
    }

    private static OpenItemRead ReadOpenItem(NpgsqlDataReader r, bool hasRc, DateOnly asOf)
    {
        DateOnly? due = r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6);
        var original = new Amounts(r.GetDecimal(10), r.GetDecimal(12), hasRc ? r.GetDecimal(14) : null);
        var cleared = new Amounts(r.GetDecimal(11), r.GetDecimal(13), hasRc ? r.GetDecimal(15) : null);
        var remaining = new Amounts(original.Lc - cleared.Lc, original.Gc - cleared.Gc, hasRc ? original.Rc - cleared.Rc : null);
        return new OpenItemRead(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetFieldValue<DateOnly>(5), due, r.GetString(7).Trim(),
            r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(8) - r.GetDecimal(9), original, cleared, remaining, due is null ? 0 : Math.Max(0, asOf.DayNumber - due.Value.DayNumber), r.GetString(16));
    }

    public async Task<CommandResult<OpenItemsResult>> OpenItemsAsync(Guid tenantId, CommandContext ctx, OpenItemsRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<OpenItemsResult>(denied);
        if (CheckLimit(req.Limit, out int limit) is { } badLimit) return Reject<OpenItemsResult>(badLimit);
        if (!ReadRules.TryDecodeCursor(req.Cursor, "oi", out long offset)) return Reject<OpenItemsResult>(Error("READ.CURSOR_INVALID", "P1", "cursor"));
        DateOnly asOf = req.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var opened = await OpenAsync(tx, tenantId, req.CompanyId, asOf, req.MinLedgerSeq, ct);
        if (opened.Error is not null) return Reject<OpenItemsResult>(opened.Error);
        bool hasRc = opened.Company!.Rc is not null;
        var items = new List<OpenItemRead>();
        await using (var cmd = tx.Command(OpenItemSql + " order by o.due_date nulls last, l.document_id, l.line_no offset @o limit @n"))
        {
            BindOpenItems(cmd, tenantId, req.CompanyId, asOf, req.AccountId, req.PartnerId, req.DueFrom, req.DueTo, req.Currency, req.IncludeCleared);
            cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("n", limit + 1);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(ReadOpenItem(r, hasRc, asOf));
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor("oi", offset + limit); }
        return CommandResult<OpenItemsResult>.Ok(new OpenItemsResult(asOf, opened.Codes!, items, new ReadPage(next, limit), opened.Snapshot!), Guid.Empty);
    }

    public async Task<CommandResult<PartnerBalancesResult>> PartnerBalancesAsync(Guid tenantId, CommandContext ctx, PartnerBalancesRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<PartnerBalancesResult>(denied);
        DateOnly asOf = req.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var opened = await OpenAsync(tx, tenantId, req.CompanyId, asOf, req.MinLedgerSeq, ct);
        if (opened.Error is not null) return Reject<PartnerBalancesResult>(opened.Error);
        bool hasRc = opened.Company!.Rc is not null;
        var groups = new Dictionary<(Guid?, Guid), (Amounts Sum, DateOnly? Oldest, int Overdue, int Count)>();
        await using (var cmd = tx.Command(OpenItemSql))
        {
            BindOpenItems(cmd, tenantId, req.CompanyId, asOf, req.AccountId, req.PartnerId, null, null, null, false);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                OpenItemRead i = ReadOpenItem(r, hasRc, asOf);
                var key = (i.PartnerId, i.AccountId);
                var cur = groups.GetValueOrDefault(key, (hasRc ? new Amounts(0m, 0m, 0m) : ZeroNoRc, null, 0, 0));
                DateOnly? oldest = i.DueDate is null ? cur.Oldest : cur.Oldest is null || i.DueDate < cur.Oldest ? i.DueDate : cur.Oldest;
                groups[key] = (Add(cur.Sum, i.Remaining), oldest, cur.Overdue + (i.DaysPastDue > 0 ? 1 : 0), cur.Count + 1);
            }
        }
        var rows = groups.OrderBy(g => g.Key.Item1 ?? Guid.Empty).ThenBy(g => g.Key.Item2)
            .Select(g => new PartnerBalanceRow(g.Key.Item1, g.Key.Item2, g.Value.Sum, g.Value.Oldest, g.Value.Overdue, g.Value.Count)).ToList();
        return CommandResult<PartnerBalancesResult>.Ok(new PartnerBalancesResult(asOf, opened.Codes!, rows, opened.Snapshot!), Guid.Empty);
    }
}
