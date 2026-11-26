namespace FinanceCore.Core.Reads;

// ---- ic-reconciliations:query (23, 7.2, ADR-227; permissions read.ledger and read.cross_company): a control over the ledger, not a posting aid
/// <summary>At least two companies, as a list or as the members of a group at as_of_date. View BALANCES (default) or DOCUMENTS. Differences up to tolerance_gc are not flagged (default 0).</summary>
public sealed record IcReconciliationRequest(
    DateOnly AsOfDate, IReadOnlyList<Guid>? CompanyIds = null, Guid? GroupId = null, string? View = null, decimal? ToleranceGc = null);

/// <summary>One unordered pair of companies, A the one with the smaller id. AToB is the net of the marked balance sheet lines of A with B as counterpart, BToA the same for B.</summary>
public sealed record IcBalancePair(
    Guid CompanyA, Guid CompanyB, Amounts AToB, Amounts BToA, decimal DifferenceGc, IReadOnlyDictionary<string, decimal> DifferenceTc,
    decimal PnlAToBGc, decimal PnlBToAGc, decimal PnlDifferenceGc, IReadOnlyList<string> Flags);

/// <summary>A finding of the document view: ONE_SIDED, GC_DIFFERENT, TC_DIFFERENT, DATE_DIFFERENT or ONE_SIDE_REVERSED, with the documents of the transaction.</summary>
public sealed record IcDocumentFinding(Guid IcTransactionId, string Finding, IReadOnlyList<IcDocumentRef> Documents, decimal? Difference = null, string? Currency = null);

public sealed record IcDocumentRef(Guid DocumentId, Guid CompanyId, string DocumentType, string DocumentNumber, DateOnly PostingDate, string Status);

public sealed record IcCompanyPosition(Guid CompanyId, Guid LedgerId, long LedgerSeq);

public sealed record IcReconciliationResult(
    DateOnly AsOfDate, string View, IReadOnlyList<Guid> Companies, decimal ToleranceGc, IReadOnlyList<IcBalancePair>? Pairs, IReadOnlyList<IcDocumentFinding>? Findings,
    IReadOnlyList<IcCompanyPosition> Positions);

public static class IcReconciliationRules
{
    public const string CrossCompanyPermission = "read.cross_company";
    public static readonly string[] Views = ["BALANCES", "DOCUMENTS"];

    /// <summary>The flags of a pair (23, 7.2): a balance difference and a profit and loss difference larger than the tolerance in absolute group amount.</summary>
    public static IReadOnlyList<string> FlagsOf(decimal differenceGc, decimal pnlDifferenceGc, decimal toleranceGc)
    {
        var flags = new List<string>();
        if (Math.Abs(differenceGc) > toleranceGc) flags.Add("BALANCE_DIFFERENCE");
        if (Math.Abs(pnlDifferenceGc) > toleranceGc) flags.Add("PNL_DIFFERENCE");
        return flags;
    }
}
