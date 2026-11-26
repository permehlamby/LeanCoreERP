using FinanceCore.Core.Commands;
using FinanceCore.Core.Reads;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// ic-reconciliations:query (23, 7.2; permissions read.ledger and read.cross_company). View BALANCES: per unordered pair of companies the net of the marked balance sheet lines each way (ASSET and
/// LIABILITY, cumulative to as_of_date), the difference in group amount and per transaction currency, and the same for the marked BASE lines of REVENUE and EXPENSE accounts of the fiscal year
/// of the date (YEAR_END and CLOSING documents left out); flags BALANCE_DIFFERENCE and PNL_DIFFERENCE above the tolerance. View DOCUMENTS: per ic_transaction_id the findings ONE_SIDED,
/// GC_DIFFERENT, TC_DIFFERENT, DATE_DIFFERENT and ONE_SIDE_REVERSED. One snapshot of the read role. Not built: access per company (COUNTERPART_NOT_VISIBLE), postings to IC accounts without marker
/// as findings of their own.
/// </summary>
public sealed class IcReconciliationReads(NpgsqlDataSource source)
{
    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);

    public async Task<CommandResult<IcReconciliationResult>> QueryAsync(Guid tenantId, CommandContext ctx, IcReconciliationRequest req, CancellationToken ct = default)
    {
        foreach (string permission in new[] { LedgerReads.Permission, IcReconciliationRules.CrossCompanyPermission })
            if (!ctx.Permissions.Contains(permission)) return CommandResult<IcReconciliationResult>.Rejected([Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", permission))]);
        var shape = new List<CommandError>();
        string view = req.View ?? "BALANCES";
        if (!IcReconciliationRules.Views.Contains(view)) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "view", ("reason", "BALANCES or DOCUMENTS")));
        if ((req.CompanyIds is null or { Count: 0 }) == (req.GroupId is null)) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "company_ids", ("reason", "exactly one of company_ids and group_id")));
        if (req.ToleranceGc is < 0m) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "tolerance_gc", ("reason", "not negative")));
        if (shape.Count > 0) return CommandResult<IcReconciliationResult>.Rejected(shape);
        decimal tolerance = req.ToleranceGc ?? 0m;

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        Guid[] companies;
        if (req.GroupId is { } group)
        {
            var members = new List<Guid>();
            await using var cmd = tx.Command("select company_id from core.group_membership where tenant_id = @t and group_id = @g and valid_from <= @d and (valid_to is null or valid_to > @d) order by company_id");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("g", group); cmd.Parameters.AddWithValue("d", req.AsOfDate);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) members.Add(r.GetGuid(0));
            companies = [.. members];
        }
        else companies = req.CompanyIds!.Distinct().Order().ToArray();
        if (companies.Length < 2) return CommandResult<IcReconciliationResult>.Rejected([Error("SCHEMA.INVALID_VALUE", "P1", "company_ids", ("reason", "at least two companies"))]);

        await using (var cmd = tx.Command("select count(*) from core.company where tenant_id = @t and company_id = any(@c)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companies);
            if ((long)(await cmd.ExecuteScalarAsync(ct))! != companies.Length) return CommandResult<IcReconciliationResult>.Rejected([Error("READ.NOT_FOUND", "P2", "company_ids")]);
        }

        var positions = new List<IcCompanyPosition>();
        await using (var cmd = tx.Command("select company_id, scope_key, last_seq from core.chain_head where tenant_id = @t and company_id = any(@c) and chain_type = 'LEDGER' order by company_id, scope_key"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companies);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) positions.Add(new IcCompanyPosition(r.GetGuid(0), Guid.Parse(r.GetString(1)), r.GetInt64(2)));
        }

        return view == "BALANCES"
            ? CommandResult<IcReconciliationResult>.Ok(new IcReconciliationResult(req.AsOfDate, view, companies, tolerance, await PairsAsync(tx, tenantId, companies, req.AsOfDate, tolerance, ct), null, positions), Guid.Empty)
            : CommandResult<IcReconciliationResult>.Ok(new IcReconciliationResult(req.AsOfDate, view, companies, tolerance, null, await FindingsAsync(tx, tenantId, companies, req.AsOfDate, ct), positions), Guid.Empty);
    }

    private sealed record Side(decimal Lc, decimal Gc, decimal? Rc, decimal Tc);

    private static async Task<List<IcBalancePair>> PairsAsync(TenantTransaction tx, Guid tenantId, Guid[] companies, DateOnly asOf, decimal tolerance, CancellationToken ct)
    {
        // (company, counterpart, transaction currency) -> net of the marked balance sheet lines
        var balance = new Dictionary<(Guid, Guid, string), Side>();
        await using (var cmd = tx.Command(
            "select l.company_id, l.ic_counterpart_company_id, d.transaction_currency, sum(l.amount_lc), sum(l.amount_gc), sum(l.amount_rc), sum(l.amount_tc) " +
            "from core.journal_line l join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            "where l.tenant_id = @t and l.company_id = any(@c) and l.ic_counterpart_company_id = any(@c) and l.posting_date <= @d and a.account_type in ('ASSET', 'LIABILITY') " +
            "group by 1, 2, 3"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companies); cmd.Parameters.AddWithValue("d", asOf);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) balance[(r.GetGuid(0), r.GetGuid(1), r.GetString(2).Trim())] = new Side(r.GetDecimal(3), r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDecimal(5), r.GetDecimal(6));
        }
        // the marked BASE lines of the profit and loss accounts of the fiscal year of the date, in group amount
        var pnl = new Dictionary<(Guid, Guid), decimal>();
        await using (var cmd = tx.Command(
            "select l.company_id, l.ic_counterpart_company_id, sum(l.amount_gc) " +
            "from core.journal_line l join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            "join core.fiscal_year fy on fy.tenant_id = l.tenant_id and fy.company_id = l.company_id and @d between fy.start_date and fy.end_date and fy.fiscal_year = l.fiscal_year " +
            "where l.tenant_id = @t and l.company_id = any(@c) and l.ic_counterpart_company_id = any(@c) and l.posting_date <= @d and l.line_type = 'BASE' " +
            "and a.account_type in ('REVENUE', 'EXPENSE') and d.document_type not in ('YEAR_END', 'CLOSING') group by 1, 2"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companies); cmd.Parameters.AddWithValue("d", asOf);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) pnl[(r.GetGuid(0), r.GetGuid(1))] = r.GetDecimal(2);
        }

        Amounts Sum(Guid from, Guid to)
        {
            var rows = balance.Where(k => k.Key.Item1 == from && k.Key.Item2 == to).Select(k => k.Value).ToList();
            return new Amounts(rows.Sum(x => x.Lc), rows.Sum(x => x.Gc), rows.Any(x => x.Rc is not null) ? rows.Sum(x => x.Rc ?? 0m) : null);
        }

        var pairs = new List<IcBalancePair>();
        for (int i = 0; i < companies.Length; i++)
            for (int j = i + 1; j < companies.Length; j++)
            {
                Guid a = companies[i], b = companies[j];
                Amounts ab = Sum(a, b), ba = Sum(b, a);
                var currencies = balance.Keys.Where(k => (k.Item1 == a && k.Item2 == b) || (k.Item1 == b && k.Item2 == a)).Select(k => k.Item3).Distinct().Order();
                var diffTc = currencies.ToDictionary(c => c, c => (balance.TryGetValue((a, b, c), out var x) ? x.Tc : 0m) + (balance.TryGetValue((b, a, c), out var y) ? y.Tc : 0m));
                decimal pa = pnl.GetValueOrDefault((a, b)), pb = pnl.GetValueOrDefault((b, a));
                if (ab.Gc == 0m && ba.Gc == 0m && pa == 0m && pb == 0m && diffTc.Count == 0) continue;   // a pair without any marked line is not listed
                decimal difference = ab.Gc + ba.Gc, pnlDifference = pa + pb;
                pairs.Add(new IcBalancePair(a, b, ab, ba, difference, diffTc, pa, pb, pnlDifference, IcReconciliationRules.FlagsOf(difference, pnlDifference, tolerance)));
            }
        return pairs;
    }

    private static async Task<List<IcDocumentFinding>> FindingsAsync(TenantTransaction tx, Guid tenantId, Guid[] companies, DateOnly asOf, CancellationToken ct)
    {
        // the original documents with an IC transaction id that touch the companies, with the sums of their marked lines
        var docs = new List<(Guid Tx, IcDocumentRef Ref, decimal Tc, decimal Gc, string Currency, DateOnly Posting)>();
        await using (var cmd = tx.Command(
            "select d.ic_transaction_id, d.document_id, d.company_id, d.document_type, d.document_number, d.posting_date, d.status, d.transaction_currency, " +
            "coalesce(sum(l.amount_tc) filter (where l.ic_counterpart_company_id is not null and l.line_type = 'BASE'), 0), " +
            "coalesce(sum(l.amount_gc) filter (where l.ic_counterpart_company_id is not null and l.line_type = 'BASE'), 0) " +
            "from core.journal_document d left join core.journal_line l on l.tenant_id = d.tenant_id and l.company_id = d.company_id and l.document_id = d.document_id " +
            "left join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            "where d.tenant_id = @t and d.ic_transaction_id is not null and d.reversal_of_document_id is null and d.posting_date <= @dt " +
            "and d.ic_transaction_id in (select ic_transaction_id from core.journal_document where tenant_id = @t and company_id = any(@c) and ic_transaction_id is not null) " +
            "group by 1, 2, 3, 4, 5, 6, 7, 8 order by d.ic_transaction_id, d.company_id"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companies); cmd.Parameters.AddWithValue("dt", asOf);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var posting = DateOnly.FromDateTime(r.GetDateTime(5));
                docs.Add((r.GetGuid(0), new IcDocumentRef(r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), posting, r.GetString(6)), r.GetDecimal(8), r.GetDecimal(9), r.GetString(7).Trim(), posting));
            }
        }

        var findings = new List<IcDocumentFinding>();
        foreach (var group in docs.GroupBy(d => d.Tx))
        {
            var items = group.ToList();
            var refs = items.Select(i => i.Ref).ToList();
            if (items.Count < 2) { findings.Add(new IcDocumentFinding(group.Key, "ONE_SIDED", refs)); continue; }
            if (items.Select(i => i.Posting).Distinct().Count() > 1) findings.Add(new IcDocumentFinding(group.Key, "DATE_DIFFERENT", refs));
            // the marked lines of the two sides offset in transaction currency (ADR-016 copies them) and the group amount of the IC account lines offsets
            if (items.Select(i => i.Currency).Distinct().Count() == 1 && items.Sum(i => i.Tc) != 0m)
                findings.Add(new IcDocumentFinding(group.Key, "TC_DIFFERENT", refs, items.Sum(i => i.Tc), items[0].Currency));
            if (items.Sum(i => i.Gc) != 0m) findings.Add(new IcDocumentFinding(group.Key, "GC_DIFFERENT", refs, items.Sum(i => i.Gc)));
            if (items.Any(i => i.Ref.Status == "reversed") && items.Any(i => i.Ref.Status != "reversed")) findings.Add(new IcDocumentFinding(group.Key, "ONE_SIDE_REVERSED", refs));
        }
        return findings;
    }
}
