namespace FinanceCore.Core.Masters;

// ---- landed-costs:allocate and :reverse (10, 4.7, 6.6, 6.8, permission landed_cost.allocate)
/// <summary>The finance line that carries the cost: a debit line on an account of the role LANDED_COST_CLEARING, normally the charge line of a supplier invoice.</summary>
public sealed record LandedCostSource(Guid DocumentId, int LineNo);

/// <summary>A receipt line (a RECEIPT movement of a stock document) that receives a part of the cost.</summary>
public sealed record LandedCostTarget(Guid StockDocumentId, int LineNo);

/// <summary>AmountLc is the part of the source line to allocate; absent, the whole remaining amount of the line.</summary>
public sealed record AllocateLandedCostRequest(
    Guid CompanyId, LandedCostSource CostSource, string AllocationMethod, IReadOnlyList<LandedCostTarget> Targets, DateOnly PostingDate, decimal? AmountLc = null);

public sealed record ReverseLandedCostRequest(Guid CompanyId, Guid AllocationId, string ReasonCode, DateOnly PostingDate);

public sealed record LandedCostLineResult(int LineNo, Guid ReceiptDocumentId, int ReceiptLineNo, decimal AllocatedCostLc, decimal OnHandPartLc, decimal ConsumedPartLc);

public sealed record LandedCostResult(
    Guid AllocationId, Guid StockDocumentId, string DocumentNumber, Guid? JournalDocumentId, decimal TotalCostLc, IReadOnlyList<LandedCostLineResult> Lines);

public static class LandedCostRules
{
    public const string Permission = "landed_cost.allocate";
    public const int MaxTargets = 200;
    public static readonly string[] Methods = ["VALUE", "QUANTITY", "WEIGHT", "VOLUME"];

    /// <summary>Proportional shares of an amount at the currency scale, the remainder on the last target (6.6).</summary>
    public static decimal[] Split(decimal amount, IReadOnlyList<decimal> basis, int scale)
    {
        decimal total = basis.Sum();
        var parts = new decimal[basis.Count];
        decimal given = 0m;
        for (int i = 0; i < basis.Count; i++)
        {
            parts[i] = i == basis.Count - 1 ? amount - given : Math.Round(amount * basis[i] / total, scale, MidpointRounding.AwayFromZero);
            given += parts[i];
        }
        return parts;
    }

    public static byte[] Fingerprint(string command, string payload) => MasterRules.Fingerprint(command, payload);
}
