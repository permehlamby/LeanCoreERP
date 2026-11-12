using System.Text.Json;
using System.Text.Json.Serialization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Migration;

// ---- requests (13, 7)
public sealed record OpenBatchRequest(Guid CompanyId, DateOnly CutoverDate, string SourceSystem);

public sealed record ControlTotalInput(string ControlKind, string? Key1 = null, string? Key2 = null, decimal? AmountLc = null, decimal? AmountGc = null, decimal? AmountRc = null, string? SourceReportRef = null);

public sealed record DeclareTotalsRequest(Guid BatchId, IReadOnlyList<ControlTotalInput> Totals);

public sealed record StageRecordInput(string RecordType, string LegacyRef, JsonElement Payload);

public sealed record StageRecordsRequest(Guid BatchId, IReadOnlyList<StageRecordInput> Records);

/// <summary>The payload of validate, reconcile, commit, seal and abort: a batch.</summary>
public sealed record BatchRef(Guid BatchId);

// ---- results
public sealed record StagedRecordResult(long RecordNo, string RecordType, string LegacyRef, string Status, IReadOnlyList<CommandError> Errors);

public sealed record ReconciliationRow(
    int CheckNo, string CheckCode, string? Key, decimal? DeclaredLc, decimal? DeclaredGc, decimal? DeclaredRc, decimal? ComputedLc, decimal? ComputedGc, decimal? ComputedRc,
    string Result, string? Detail);

public sealed record ChunkResult(int ChunkNo, string Area, int DocumentCount, string Status);

public sealed record MigrationBatchResult(
    Guid BatchId, Guid CompanyId, string Status, DateOnly CutoverDate, string SourceSystem, int RecordCount, int InvalidCount, int TotalsCount,
    IReadOnlyList<StagedRecordResult>? Records = null, IReadOnlyList<ReconciliationRow>? Reconciliation = null, IReadOnlyList<ChunkResult>? Chunks = null, int Documents = 0);

// ---- canonical records (13, 5): the first slice has the opening balance and the open items
public static class RecordTypes
{
    public const string OpeningBalance = "OPENING_BALANCE", OpenItem = "OPEN_ITEM", GlOpenItem = "GLOPEN_ITEM";
    public static readonly IReadOnlyList<string> Built = [OpeningBalance, OpenItem, GlOpenItem];
    public static readonly IReadOnlyList<string> Known =
        [OpeningBalance, OpenItem, GlOpenItem, "HISTORY_PL_LINE", "STOCK_OPENING_LINE", "OPEN_SALES_ORDER_LINE", "OPEN_PURCHASE_ORDER_LINE", "DELIVERED_NOT_INVOICED",
         "RECEIVED_NOT_INVOICED", "DROPSHIP_PENDING", "LEGACY_TAX_SUMMARY"];
}

public static class ControlKinds
{
    public const string TbByAccount = "TB_BY_ACCOUNT", OpenItemsByAccount = "OPEN_ITEMS_BY_ACCOUNT";
    public static readonly IReadOnlyList<string> Built = [TbByAccount, OpenItemsByAccount];
}

public sealed record OpeningBalancePayload(
    [property: JsonPropertyName("account_no")] string? AccountNo, [property: JsonPropertyName("balance_lc")] decimal? BalanceLc,
    [property: JsonPropertyName("balance_gc")] decimal? BalanceGc, [property: JsonPropertyName("balance_rc")] decimal? BalanceRc);

/// <summary>An open item at the cutover date (13, 5): the remaining open amount in the transaction currency and in LC, GC and RC as the legacy system booked it.</summary>
public sealed record OpenItemPayload(
    [property: JsonPropertyName("account_no")] string? AccountNo, [property: JsonPropertyName("partner_code")] string? PartnerCode,
    [property: JsonPropertyName("document_date")] DateOnly? DocumentDate, [property: JsonPropertyName("posting_date")] DateOnly? PostingDate,
    [property: JsonPropertyName("due_date")] DateOnly? DueDate, [property: JsonPropertyName("baseline_date")] DateOnly? BaselineDate,
    [property: JsonPropertyName("currency")] string? Currency, [property: JsonPropertyName("amount_tc")] decimal? AmountTc,
    [property: JsonPropertyName("amount_lc")] decimal? AmountLc, [property: JsonPropertyName("amount_gc")] decimal? AmountGc, [property: JsonPropertyName("amount_rc")] decimal? AmountRc,
    [property: JsonPropertyName("legacy_document_ref")] string? LegacyDocumentRef, [property: JsonPropertyName("legacy_line_ref")] string? LegacyLineRef);

public static class MigrationRecords
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static CommandError Bad(string path, string reason) => new("SCHEMA.INVALID_VALUE", "P1", path, new Dictionary<string, object?> { ["reason"] = reason });

    /// <summary>The shape of a payload: known fields only, the required ones present, amounts that fit their sign. The checks against the company's data are made by the command.</summary>
    public static (object? Payload, List<CommandError> Errors) Parse(string recordType, JsonElement payload)
    {
        var errors = new List<CommandError>();
        try
        {
            switch (recordType)
            {
                case RecordTypes.OpeningBalance:
                {
                    var p = payload.Deserialize<OpeningBalancePayload>(Options)!;
                    if (string.IsNullOrWhiteSpace(p.AccountNo)) errors.Add(Bad("/account_no", "required"));
                    if (p.BalanceLc is null || p.BalanceGc is null) errors.Add(Bad("/balance_lc", "balance_lc and balance_gc are required"));
                    return (errors.Count == 0 ? p : null, errors);
                }
                case RecordTypes.OpenItem or RecordTypes.GlOpenItem:
                {
                    var p = payload.Deserialize<OpenItemPayload>(Options)!;
                    if (string.IsNullOrWhiteSpace(p.AccountNo)) errors.Add(Bad("/account_no", "required"));
                    if (recordType == RecordTypes.OpenItem && string.IsNullOrWhiteSpace(p.PartnerCode)) errors.Add(Bad("/partner_code", "required for an OPEN_ITEM"));
                    if (recordType == RecordTypes.GlOpenItem && !string.IsNullOrWhiteSpace(p.PartnerCode)) errors.Add(Bad("/partner_code", "a GLOPEN_ITEM has no partner"));
                    if (p.DocumentDate is null) errors.Add(Bad("/document_date", "required"));
                    if (p.PostingDate is null) errors.Add(Bad("/posting_date", "required"));
                    if (string.IsNullOrWhiteSpace(p.Currency) || p.Currency.Length != 3) errors.Add(Bad("/currency", "a three-letter code"));
                    if (string.IsNullOrWhiteSpace(p.LegacyDocumentRef) || p.LegacyDocumentRef.Length > 100) errors.Add(Bad("/legacy_document_ref", "required, at most 100 characters"));
                    if (p.AmountTc is null || p.AmountLc is null || p.AmountGc is null) errors.Add(Bad("/amount_tc", "amount_tc, amount_lc and amount_gc are required"));
                    else if (p.AmountTc == 0m && p.AmountLc == 0m && p.AmountGc == 0m) errors.Add(Bad("/amount_tc", "an open item with nothing open is not staged"));
                    else if (p.AmountTc != 0m && p.AmountLc != 0m && Math.Sign(p.AmountTc!.Value) != Math.Sign(p.AmountLc!.Value)) errors.Add(Bad("/amount_lc", "the sign of the amounts must agree"));
                    return (errors.Count == 0 ? p : null, errors);
                }
                default:
                    errors.Add(new CommandError("MIGRATION.RECORD_TYPE_INVALID", "P1", "/record_type", new Dictionary<string, object?>
                    {
                        ["record_type"] = recordType, ["built"] = RecordTypes.Built.ToArray(), ["known"] = RecordTypes.Known.Contains(recordType)
                    }));
                    return (null, errors);
            }
        }
        catch (JsonException e)
        {
            errors.Add(Bad("/", e.Message.Split(" Path:")[0]));
            return (null, errors);
        }
    }

    public static string Digest(JsonElement payload) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload.GetRawText()))).ToLowerInvariant();

    private static readonly FieldSpec[] PayloadSpec = [new("payload", FieldType.Text)];

    public static byte[] Fingerprint(string command, object request) =>
        Periods.CommandFingerprint.Compute(command, PayloadSpec, new Dictionary<string, object?> { ["payload"] = JsonSerializer.Serialize(request, request.GetType()) });
}

// ---- the plan of the documents (13, 6.2, 6.3)
public sealed record PlannedItem(Guid AccountId, Guid? PartnerId, string Currency, decimal Tc, decimal Lc, decimal Gc, decimal? Rc, DateOnly DocumentDate, DateOnly PostingDate,
    DateOnly? DueDate, DateOnly? BaselineDate, string LegacyDocumentRef);

public sealed record PlannedLine(
    Guid AccountId, decimal Tc, decimal Lc, decimal Gc, decimal? Rc, decimal RateLc, decimal RateGc, decimal? RateRc, PlannedItem? Item);

public sealed record PlannedDocument(string DocumentType, string Currency, IReadOnlyList<PlannedLine> Lines);

public static class MigrationPlan
{
    /// <summary>The rate that a supplied amount implies against the transaction amount (rate_type DERIVED, 13, 6): the ratio of the absolute amounts, 1 when there is no ratio.</summary>
    public static decimal Implied(decimal tc, decimal amount)
    {
        if (tc == 0m || amount == 0m) return 1m;
        decimal r = Math.Round(Math.Abs(amount / tc), Canonical.RateScale, MidpointRounding.AwayFromZero);
        return r > 0m ? r : 1m;
    }

    private static PlannedLine Line(Guid account, decimal tc, decimal lc, decimal gc, decimal? rc, PlannedItem? item = null) =>
        new(account, tc, lc, gc, rc, Implied(tc, lc), Implied(tc, gc), rc is null ? null : Implied(tc, rc.Value), item);

    /// <summary>The OPENING documents (6.2): one line per account with the amount that brings it to its target, in the company currency, and the counter line on
    /// MIGRATION_CLEARING in each document, so that every document balances. Split by the line limit.</summary>
    public static IReadOnlyList<PlannedDocument> OpeningDocuments(
        IEnumerable<(Guid Account, decimal Lc, decimal Gc, decimal? Rc)> lines, Guid clearing, string companyCurrency, bool hasRc, int maxLines)
    {
        var docs = new List<PlannedDocument>();
        var ordered = lines.Where(l => l.Lc != 0m || l.Gc != 0m || (l.Rc ?? 0m) != 0m).OrderBy(l => l.Account).ToList();
        for (int i = 0; i < ordered.Count; i += Math.Max(1, maxLines - 1))
        {
            var part = ordered.Skip(i).Take(Math.Max(1, maxLines - 1)).ToList();
            var planned = part.Select(l => Line(l.Account, l.Lc, l.Lc, l.Gc, hasRc ? l.Rc ?? 0m : null)).ToList();
            decimal lc = -part.Sum(l => l.Lc), gc = -part.Sum(l => l.Gc), rc = -part.Sum(l => l.Rc ?? 0m);
            planned.Add(Line(clearing, lc, lc, gc, hasRc ? rc : null));
            docs.Add(new PlannedDocument("OPENING", companyCurrency, planned));
        }
        return docs;
    }

    /// <summary>The OPENING_ITEMS documents (6.3): per transaction currency (one currency per document, D7), the item lines and the counter line on MIGRATION_CLEARING in
    /// that currency. Split by the line limit.</summary>
    public static IReadOnlyList<PlannedDocument> ItemDocuments(IEnumerable<PlannedItem> items, Guid clearing, bool hasRc, int maxLines)
    {
        var docs = new List<PlannedDocument>();
        foreach (var currency in items.GroupBy(i => i.Currency).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = currency.OrderBy(i => i.AccountId).ThenBy(i => i.PartnerId ?? Guid.Empty).ThenBy(i => i.LegacyDocumentRef, StringComparer.Ordinal).ToList();
            for (int i = 0; i < ordered.Count; i += Math.Max(1, maxLines - 1))
            {
                var part = ordered.Skip(i).Take(Math.Max(1, maxLines - 1)).ToList();
                var planned = part.Select(x => Line(x.AccountId, x.Tc, x.Lc, x.Gc, hasRc ? x.Rc ?? 0m : null, x)).ToList();
                decimal tc = -part.Sum(x => x.Tc), lc = -part.Sum(x => x.Lc), gc = -part.Sum(x => x.Gc), rc = -part.Sum(x => x.Rc ?? 0m);
                planned.Add(Line(clearing, tc, lc, gc, hasRc ? rc : null));
                docs.Add(new PlannedDocument("OPENING_ITEMS", currency.Key, planned));
            }
        }
        return docs;
    }
}

// ---- reconciliation (13, 8)
public sealed record AccountAmounts(string AccountNo, decimal Lc, decimal Gc, decimal? Rc);

public static class MigrationReconciliation
{
    public const string Pass = "PASS", Fail = "FAIL", Warning = "WARNING";

    /// <summary>The checks of 13, 8 that the staged records of this slice allow. There is no tolerance: any difference is a FAIL. Each total is a signed amount per account.</summary>
    public static IReadOnlyList<ReconciliationRow> Run(
        int invalidRecords, IReadOnlyList<AccountAmounts> declaredTb, IReadOnlyList<AccountAmounts> declaredOpenItems,
        IReadOnlyList<AccountAmounts> openingBalances, IReadOnlyList<AccountAmounts> openItems, IReadOnlySet<string> subledgerAccounts, bool hasRc)
    {
        var rows = new List<ReconciliationRow>();
        void Add(string code, string? key, decimal? dl, decimal? dg, decimal? dr, decimal? cl, decimal? cg, decimal? cr, string result, string? detail = null) =>
            rows.Add(new ReconciliationRow(rows.Count + 1, code, key, dl, dg, dr, cl, cg, cr, result, detail));
        static decimal Rc(AccountAmounts a) => a.Rc ?? 0m;

        Add("RECORDS_VALID", null, 0, null, null, invalidRecords, null, null, invalidRecords == 0 ? Pass : Fail, invalidRecords == 0 ? null : $"{invalidRecords} current records are INVALID");

        decimal sl = declaredTb.Sum(a => a.Lc), sg = declaredTb.Sum(a => a.Gc), sr = declaredTb.Sum(Rc);
        bool balanced = sl == 0m && sg == 0m && (!hasRc || sr == 0m);
        Add("TB_BALANCES", null, 0, 0, hasRc ? 0 : null, sl, sg, hasRc ? sr : null, balanced ? Pass : Fail, balanced ? null : "The declared trial balance does not net to zero");

        // every declared balance has a source: an opening balance record, or open items on its account
        var covered = openingBalances.Select(a => a.AccountNo).Concat(openItems.Select(a => a.AccountNo)).ToHashSet();
        foreach (AccountAmounts a in declaredTb.Where(a => a.Lc != 0m || a.Gc != 0m || Rc(a) != 0m).OrderBy(a => a.AccountNo, StringComparer.Ordinal))
            if (!covered.Contains(a.AccountNo)) Add("TB_ACCOUNTS_COMPLETE", a.AccountNo, a.Lc, a.Gc, a.Rc, null, null, null, Fail, "The account has a declared balance and no opening balance record or open item");

        // an opening balance record on a non-subledger account equals the declared balance of the account
        var tb = declaredTb.ToDictionary(a => a.AccountNo, StringComparer.Ordinal);
        foreach (AccountAmounts o in openingBalances.OrderBy(a => a.AccountNo, StringComparer.Ordinal))
        {
            AccountAmounts d = tb.GetValueOrDefault(o.AccountNo) ?? new AccountAmounts(o.AccountNo, 0m, 0m, 0m);
            bool ok = d.Lc == o.Lc && d.Gc == o.Gc && (!hasRc || Rc(d) == Rc(o));
            if (!ok) Add("OPENING_VS_TB", o.AccountNo, d.Lc, d.Gc, d.Rc, o.Lc, o.Gc, o.Rc, Fail, "The opening balance differs from the declared balance");
        }

        // open items per account against the declared total, and for the accounts that the open items stand for, against the declared trial balance
        var sums = openItems.GroupBy(a => a.AccountNo).ToDictionary(g => g.Key, g => new AccountAmounts(g.Key, g.Sum(x => x.Lc), g.Sum(x => x.Gc), g.Sum(Rc)), StringComparer.Ordinal);
        var declaredItems = declaredOpenItems.ToDictionary(a => a.AccountNo, StringComparer.Ordinal);
        foreach (string account in sums.Keys.Concat(declaredItems.Keys).Concat(subledgerAccounts.Where(tb.ContainsKey)).Distinct().OrderBy(a => a, StringComparer.Ordinal))
        {
            AccountAmounts s = sums.GetValueOrDefault(account) ?? new AccountAmounts(account, 0m, 0m, 0m);
            if (declaredItems.TryGetValue(account, out AccountAmounts? di) && (di.Lc != s.Lc || di.Gc != s.Gc || (hasRc && Rc(di) != Rc(s))))
                Add("OPEN_ITEMS_VS_DECLARED", account, di.Lc, di.Gc, di.Rc, s.Lc, s.Gc, s.Rc, Fail, "The open items differ from the declared open item total");
            if (subledgerAccounts.Contains(account))
            {
                AccountAmounts d = tb.GetValueOrDefault(account) ?? new AccountAmounts(account, 0m, 0m, 0m);
                if (d.Lc != s.Lc || d.Gc != s.Gc || (hasRc && Rc(d) != Rc(s)))
                    Add("OPEN_ITEMS_VS_TB", account, d.Lc, d.Gc, d.Rc, s.Lc, s.Gc, s.Rc, Fail, "The open items differ from the declared balance of the account");
            }
        }

        // what MIGRATION_CLEARING would hold after the commit: minus the sum of everything that is posted
        decimal cl = -(openingBalances.Sum(a => a.Lc) + openItems.Sum(a => a.Lc)), cg = -(openingBalances.Sum(a => a.Gc) + openItems.Sum(a => a.Gc));
        decimal cr = -(openingBalances.Sum(Rc) + openItems.Sum(Rc));
        bool zero = cl == 0m && cg == 0m && (!hasRc || cr == 0m);
        Add("CLEARING_ZERO", null, 0, 0, hasRc ? 0 : null, -cl, -cg, hasRc ? -cr : null, zero ? Pass : Fail, zero ? null : "MIGRATION_CLEARING would not be zero after the commit");

        bool failed = rows.Any(r => r.Result == Fail);
        Add("ZERO_TOLERANCE", null, null, null, null, null, null, null, failed ? Fail : Pass, failed ? "There is no tolerance: any difference is a failure" : null);
        return rows;
    }

    /// <summary>A reconciliation has no FAIL.</summary>
    public static bool Passed(IEnumerable<ReconciliationRow> rows) => rows.All(r => r.Result != Fail);
}
