using FinanceCore.Core.Commands;
using FinanceCore.Core.Reads;

namespace FinanceCore.Data;

public sealed partial class LedgerReads
{
    private static readonly string[] AgingGroupKeys = ["partner_id", "account_id", "currency"];
    private static readonly string[] AgingBases = ["DUE_DATE", "BASELINE_DATE", "DOCUMENT_DATE"];

    /// <summary>
    /// aging:query (09, 4.5): the open items of 4.4 (as of the date, from the lines and the clearing items) in buckets of days past the basis date, per group and in total.
    /// One company (cross-company reads are not built). An item without the date of the basis counts as current.
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

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var opened = await OpenAsync(tx, tenantId, req.CompanyId, asOf, req.MinLedgerSeq, ct);
        if (opened.Error is not null) return Reject<AgingResult>(opened.Error);
        bool hasRc = opened.Company!.Rc is not null;
        var layout = AgingRules.Layout(bounds);
        Amounts Zero() => hasRc ? new Amounts(0m, 0m, 0m) : ZeroNoRc;

        var groups = new Dictionary<string, (Dictionary<string, string?> Keys, Amounts[] Sums, long[] Counts)>(StringComparer.Ordinal);
        var grand = (Sums: Enumerable.Range(0, layout.Count).Select(_ => Zero()).ToArray(), Counts: new long[layout.Count]);
        await using (var cmd = tx.Command(OpenItemSql))
        {
            BindOpenItems(cmd, tenantId, req.CompanyId, asOf, req.AccountId, req.PartnerId, null, null, req.Currency, false);
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
                string id = string.Join('|', keys.Select(g => k[g] ?? ""));
                if (!groups.TryGetValue(id, out var group))
                {
                    if (groups.Count >= ReadRules.MaxGroups) return Reject<AgingResult>(Error("READ.RESULT_TOO_LARGE", "P2", "group_by", ("max", ReadRules.MaxGroups)));
                    groups[id] = group = (k, Enumerable.Range(0, layout.Count).Select(_ => Zero()).ToArray(), new long[layout.Count]);
                }
                group.Sums[bucket] = Add(group.Sums[bucket], item.Remaining); group.Counts[bucket]++;
                grand.Sums[bucket] = Add(grand.Sums[bucket], item.Remaining); grand.Counts[bucket]++;
            }
        }

        AgingRow Row(IReadOnlyDictionary<string, string?> keyValues, Amounts[] sums, long[] counts) => new(
            keyValues, layout.Select((b, i) => new AgingBucketRead(b.Label, b.From, b.To, sums[i], counts[i])).ToList(),
            sums.Aggregate(Zero(), Add), counts.Sum());
        var rows = groups.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Row(g.Value.Keys, g.Value.Sums, g.Value.Counts)).ToList();
        return CommandResult<AgingResult>.Ok(new AgingResult(asOf, basis, bounds, opened.Codes!, rows, Row(new Dictionary<string, string?>(), grand.Sums, grand.Counts), opened.Snapshot!), Guid.Empty);
    }
}
