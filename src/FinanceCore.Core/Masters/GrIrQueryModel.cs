namespace FinanceCore.Core.Masters;

// ---- gr-ir:query (11, 8, permission read.orders): the GR/IR items of a company with age, value and state.
/// <summary>State is OPEN (open or partly cleared, the default), WRITTEN_OFF (cleared by gr-ir:write-off and not reset), CLEARED (cleared otherwise) or ALL. AsOf is the date the age counts to (today when absent).</summary>
public sealed record GrIrQueryRequest(Guid CompanyId, string? State = null, Guid? PurchaseOrderId = null, Guid? SupplierPartnerId = null, DateOnly? AsOf = null, int? Limit = null);

/// <summary>One GR/IR item. The source is the document that holds it: a RECEIPT, a DIRECT_RECEIPT (a receipt without an order, 10), a RETURN, an INVOICE, a CREDIT_NOTE or a WRITE_OFF. The supplier is not known for a direct receipt.</summary>
public sealed record GrIrQueryItem(
    Guid DocumentId, int LineNo, Guid AccountId, string DocumentType, DateOnly PostingDate, int AgeDays, decimal AmountLc, decimal ClearedLc, decimal ResidualLc, string State, string SourceKind, Guid SourceId,
    Guid? SupplierPartnerId, IReadOnlyList<Guid> OrderIds);

/// <summary>The items, and the reconciliation: the residual of every open item on the GR/IR accounts of the company against the balance of those accounts in the ledger (both unfiltered, the filters and the limit narrow the items only).</summary>
public sealed record GrIrQueryResult(IReadOnlyList<GrIrQueryItem> Items, decimal TotalOpenLc, decimal GlBalanceLc, bool Reconciles, int Limit, bool Truncated);

public static class GrIrQueryRules
{
    public static readonly string[] States = ["OPEN", "WRITTEN_OFF", "CLEARED", "ALL"];
    public const int DefaultLimit = 200, MaxLimit = 1000;
}
