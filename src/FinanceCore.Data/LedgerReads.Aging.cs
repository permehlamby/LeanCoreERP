using FinanceCore.Core.Commands;
using FinanceCore.Core.Reads;

namespace FinanceCore.Data;

public sealed partial class LedgerReads
{
    private static readonly string[] AgingGroupKeys = ["partner_id", "account_id", "currency"];
    private static readonly string[] AgingBases = ["DUE_DATE", "BASELINE_DATE", "DOCUMENT_DATE"];

    /// <summary>
    /// aging:query (09, 4.5): the open items of 4.4 (as of the date, from the lines and the clearing items) in buckets of days past the basis date, per group and in total.
    /// One company, several (company_ids) or a group (group_id) with read.cross_company: the groups are then split by currency combination, nothing is added across currency codes (4.1.2). An item without the date of the basis counts as current.
    /// </summary>
    public async Task<CommandResult<AgingResult>> AgingAsync(Guid tenantId, CommandContext ctx, AgingRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<AgingResult>(denied);
        var shape = new List<CommandError>();
        string basis = req.Basis ?? "DUE_DATE";
        if (!AgingBases.Contains(basis)) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "basis", ("reason", string.Join(", ", AgingBases))));
        IReadOnlyList<int> bounds = req.Buckets ?? AgingRules.DefaultBuckets;
        if (!AgingRules.ValidBuckets(bounds)) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "buckets", ("reason", $"1 to {AgingRules.MaxBuckets} boundaries in rising order")));
        string[] keys = (req.GroupBy ?? []).Distinct().ToArray();
        foreach (string g in keys)
            if (!AgingGroupKeys.Contains(g)) shape.Add(Error("READ.INVALID_FILTER", "P1", "group_by", ("field", g), ("allowed", AgingGroupKeys)));
        if (shape.Count > 0) return CommandResult<AgingResult>.Rejected(shape);
        DateOnly asOf = req.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        if (CheckCompanySelection(ctx, req.CompanyId, req.CompanyIds, req.GroupId) is { } badSelection) return Reject<AgingResult>(badSelection);
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var selected = await ResolveCompaniesAsync(tx, tenantId, req.CompanyId, req.CompanyIds, req.GroupId, asOf, ct);
        if (selected.Error is not null) return Reject<AgingResult>(selected.Error);
        bool multi = req.GroupId is not null || req.CompanyIds is { Count: > 1 };
        var layout = AgingRules.Layout(bounds);
        static Amounts ZeroFor(CurrencyCodes c) => c.Rc is not null ? new Amounts(0m, 0m, 0m) : new Amounts(0m, 0m, null);

        // one group per key values and currency combination (4.1.2), one grand total per currency combination
        var groups = new Dictionary<string, (Dictionary<string, string?> Keys, CurrencyCodes Codes, Amounts[] Sums, long[] Counts)>(StringComparer.Ordinal);
        var grands = new Dictionary<CurrencyCodes, (Amounts[] Sums, long[] Counts)>();
        var companies = new List<CompanyRead>();
        var ledgers = new List<LedgerPosition>();
        DateTime takenAt = DateTime.UtcNow;
        foreach (Guid company in selected.Ids!)
        {
        var opened = await OpenAsync(tx, tenantId, company, asOf, req.MinLedgerSeq, ct);
        if (opened.Error is not null) return Reject<AgingResult>(opened.Error);
        bool hasRc = opened.Company!.Rc is not null;
        CurrencyCodes codes = opened.Codes!;
        Amounts Zero() => ZeroFor(codes);
        companies.Add(new CompanyRead(company, codes, true));
        ledgers.AddRange(opened.Snapshot!.Ledgers); takenAt = opened.Snapshot.TakenAt;
        if (!grands.ContainsKey(codes)) grands[codes] = (Enumerable.Range(0, layout.Count).Select(_ => Zero()).ToArray(), new long[layout.Count]);
        var grand = grands[codes];
        await using (var cmd = tx.Command(OpenItemSql))
        {
            BindOpenItems(cmd, tenantId, company, asOf, req.AccountId, req.PartnerId, null, null, req.Currency, false);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                OpenItemRead item = ReadOpenItem(r, hasRc, asOf);
                DateOnly? date = basis switch
                {
                    "DUE_DATE" => item.DueDate,
                    "BASELINE_DATE" => r.IsDBNull(17) ? null : r.GetFieldValue<DateOnly>(17),
                    _ => r.GetFieldValue<DateOnly>(18)
                };
                int bucket = AgingRules.BucketOf(bounds, date is null ? 0 : asOf.DayNumber - date.Value.DayNumber);

                var k = new Dictionary<string, string?>();
                foreach (string g in keys)
                    k[g] = g switch { "partner_id" => item.PartnerId?.ToString("D"), "account_id" => item.AccountId.ToString("D"), _ => item.CurrencyTc };
                string id = string.Join('|', keys.Select(g => k[g] ?? "")) + "\u001e" + codes;
                if (!groups.TryGetValue(id, out var group))
                {
                    if (groups.Count >= ReadRules.MaxGroups) return Reject<AgingResult>(Error("READ.RESULT_TOO_LARGE", "P2", "group_by", ("max", ReadRules.MaxGroups)));
                    groups[id] = group = (k, codes, Enumerable.Range(0, layout.Count).Select(_ => Zero()).ToArray(), new long[layout.Count]);
                }
                group.Sums[bucket] = Add(group.Sums[bucket], item.Remaining); group.Counts[bucket]++;
                grand.Sums[bucket] = Add(grand.Sums[bucket], item.Remaining); grand.Counts[bucket]++;
            }
        }
        }

        AgingRow Row(IReadOnlyDictionary<string, string?> keyValues, CurrencyCodes codes, Amounts[] sums, long[] counts) => new(
            keyValues, layout.Select((b, i) => new AgingBucketRead(b.Label, b.From, b.To, sums[i], counts[i])).ToList(),
            sums.Aggregate(ZeroFor(codes), Add), counts.Sum(), codes);
        var rows = groups.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Row(g.Value.Keys, g.Value.Codes, g.Value.Sums, g.Value.Counts)).ToList();
        var grandRows = grands.OrderBy(g => g.Key.Lc, StringComparer.Ordinal).ThenBy(g => g.Key.Gc, StringComparer.Ordinal).ThenBy(g => g.Key.Rc, StringComparer.Ordinal)
            .Select(g => Row(new Dictionary<string, string?>(), g.Key, g.Value.Sums, g.Value.Counts)).ToList();
        CurrencyCodes? only = grandRows.Count == 1 ? grandRows[0].Currencies : null;
        var snapshot = new ReadSnapshot(takenAt, ledgers);
        return CommandResult<AgingResult>.Ok(new AgingResult(asOf, basis, bounds, only, rows, only is null ? null : grandRows[0], snapshot, multi ? grandRows : null, multi ? companies : null, selected.Group), Guid.Empty);
    }
}
