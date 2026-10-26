using System.Security.Cryptography;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Periods;

namespace FinanceCore.Core.Posting;

/// <summary>One item of items[] of clearings:post (06, 7.1). ClearedAmountTc is optional: the default is the full remaining amount.</summary>
public sealed record ClearingItemRequest(Guid DocumentId, int LineNo, decimal? ClearedAmountTc = null);

/// <summary>Payload of clearings:post as far as the first slice supports it (06, 7.1, 03, 5): no partner, no difference document.</summary>
public sealed record PostClearingRequest(
    Guid CompanyId, Guid AccountId, Guid? PartnerId, DateOnly ClearingDate, string ClearingMethod, string? MatchingRef, string? Note,
    IReadOnlyList<ClearingItemRequest> Items);

/// <summary>An item as cleared: the four amounts that the core derived (03, 5.2) and the status of the open item after the clearing.</summary>
public sealed record ClearedItem(
    int ItemNo, Guid DocumentId, int LineNo, string CurrencyTc, decimal AmountTc, decimal AmountLc, decimal AmountGc, decimal? AmountRc,
    string StatusAfter);

public sealed record PostedClearing(
    Guid ClearingId, Guid CompanyId, long ChainSeq, string RowHash, string PrevHash, DateOnly ClearingDate, int FiscalYear, int FiscalPeriod,
    string ClearingMethod, IReadOnlyList<ClearedItem> Items);

/// <summary>An open item as the allocation sees it: the line amounts and what is cleared so far (03, 4.3).</summary>
public sealed record OpenLine(
    decimal Tc, decimal Lc, decimal Gc, decimal? Rc, decimal ClearedTc, decimal ClearedLc, decimal ClearedGc, decimal? ClearedRc);

public sealed record Allocation(decimal Tc, decimal Lc, decimal Gc, decimal? Rc);

public static class ClearingRules
{
    public const string PostCommand = "clearings:post";

    /// <summary>The methods a caller of clearings:post may name. REVERSAL belongs to the clearings that the reversal commands create (03, 5.7).</summary>
    public static readonly string[] ExternalMethods = ["MANUAL", "AUTO_MATCH", "PAYMENT_ALLOCATION"];

    public const string OverClearing = "CLEARING.OVER_CLEARING";
    public const string ZeroAmount = "SCHEMA.INVALID_VALUE";

    /// <summary>
    /// The amounts of one item (03, 5.2). The caller gives the transaction amount (full or partial), the core derives the other three.
    /// A full clearing of the remaining amount takes the remaining carried amounts, so the partials always add up to the line exactly.
    /// A partial clearing is ROUND(line amount x cleared TC / line TC) at the scale of each currency (half away from zero, ADR-229),
    /// never more than what remains. A line whose TC amount is zero (the offset line of a difference document) is cleared as a whole.
    /// Returns null and an error code when the amount is not allowed.
    /// </summary>
    public static Allocation? Allocate(OpenLine l, decimal? requestedTc, int scaleLc, int scaleGc, int? scaleRc, out string? error)
    {
        error = null;
        decimal remTc = l.Tc - l.ClearedTc, remLc = l.Lc - l.ClearedLc, remGc = l.Gc - l.ClearedGc;
        decimal? remRc = l.Rc is null ? null : l.Rc.Value - (l.ClearedRc ?? 0m);
        Allocation Whole() => new(remTc, remLc, remGc, remRc);

        if (l.Tc == 0m)
        {
            if (requestedTc is not null and not 0m) { error = OverClearing; return null; }
            return Whole();
        }
        if (requestedTc is null) return Whole();
        decimal req = requestedTc.Value;
        if (req == 0m) { error = ZeroAmount; return null; }
        // the sign of the line, and not more than what remains
        if (Math.Sign(req) != Math.Sign(l.Tc) || Math.Abs(req) > Math.Abs(remTc)) { error = OverClearing; return null; }
        if (req == remTc) return Whole();

        decimal Part(decimal amount, int scale, decimal remaining)
        {
            decimal v = PostingRules.Round(amount * req / l.Tc, scale);
            return Math.Abs(v) > Math.Abs(remaining) ? remaining : v;
        }
        return new Allocation(req, Part(l.Lc, scaleLc, remLc), Part(l.Gc, scaleGc, remGc),
            l.Rc is null ? null : Part(l.Rc.Value, scaleRc ?? 4, remRc!.Value));
    }

    /// <summary>The status of an open item from its cleared amounts (03, 4.3).</summary>
    public static string StatusOf(decimal lineTc, decimal lineLc, decimal clearedTc, decimal clearedLc) =>
        clearedTc == 0m && clearedLc == 0m ? "OPEN"
        : clearedTc == lineTc && clearedLc == lineLc ? "CLEARED"
        : "PARTIAL";

    private static readonly FieldSpec[] Header =
    [
        new("company_id", FieldType.Id), new("account_id", FieldType.Id), new("partner_id", FieldType.Id, true),
        new("clearing_date", FieldType.Date), new("clearing_method", FieldType.Text), new("matching_ref", FieldType.Text, true),
        new("note", FieldType.Text, true), new("items", FieldType.Hex32)
    ];

    private static readonly FieldSpec[] Item =
    [
        new("document_id", FieldType.Id), new("line_no", FieldType.Int), new("cleared_amount_tc", FieldType.Amount, true)
    ];

    /// <summary>The fingerprint covers the items in the order given: the order assigns the item numbers.</summary>
    public static byte[] Fingerprint(PostClearingRequest r)
    {
        using var items = new MemoryStream();
        foreach (ClearingItemRequest i in r.Items)
            items.Write(CommandFingerprint.Compute("clearing-item", Item, new Dictionary<string, object?>
            {
                ["document_id"] = i.DocumentId, ["line_no"] = (long)i.LineNo, ["cleared_amount_tc"] = i.ClearedAmountTc
            }));
        return CommandFingerprint.Compute(PostCommand, Header, new Dictionary<string, object?>
        {
            ["company_id"] = r.CompanyId, ["account_id"] = r.AccountId, ["partner_id"] = r.PartnerId, ["clearing_date"] = r.ClearingDate,
            ["clearing_method"] = r.ClearingMethod, ["matching_ref"] = r.MatchingRef, ["note"] = r.Note,
            ["items"] = SHA256.HashData(items.ToArray())
        });
    }
}
