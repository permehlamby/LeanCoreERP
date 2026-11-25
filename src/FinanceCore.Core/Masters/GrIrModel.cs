using System.Globalization;

namespace FinanceCore.Core.Masters;

// ---- gr-ir:write-off (11, 7.5, permission purchase.invoice): clears GR/IR residuals that no supplier invoice will match.
public sealed record GrIrItemRef(Guid DocumentId, int LineNo);

public sealed record GrIrDirectReceiptRef(Guid StockDocumentId, int LineNo);

/// <summary>One entry: exactly one selector (open items, the order line, a direct receipt line) and the residual in local currency that the caller saw (compare-and-set).</summary>
public sealed record GrIrWriteOffItem(
    decimal ExpectedResidualLc, IReadOnlyList<GrIrItemRef>? OpenItems = null, Guid? PurchaseOrderId = null, int? PurchaseOrderLineNo = null, GrIrDirectReceiptRef? ReceiptRef = null);

public sealed record GrIrWriteOffRequest(Guid CompanyId, DateOnly PostingDate, string ReasonCode, string ReasonText, IReadOnlyList<GrIrWriteOffItem> Items, string? SourceDocumentRef = null);

public sealed record GrIrWriteOffResult(Guid JournalDocumentId, string DocumentNumber, decimal TotalResidualLc, int ItemCount, string RowHash, IReadOnlyList<Guid> ClearingIds);

/// <summary>gr-ir:reverse-write-off (11, 7.5a): the reason is CORRECTION or ERROR. posting_date defaults to the posting date of the write-off.</summary>
public sealed record ReverseGrIrWriteOffRequest(Guid CompanyId, Guid JournalDocumentId, string ReasonCode, string? ReasonText = null, DateOnly? PostingDate = null);

public sealed record ReversedGrIrWriteOff(Guid JournalDocumentId, Guid ReversalDocumentId, string DocumentNumber, string RowHash);

public static class GrIrRules
{
    public const string Permission = "purchase.invoice";
    public static readonly string[] ReverseReasons = ["CORRECTION", "ERROR"];

    public static byte[] Fingerprint(GrIrWriteOffRequest r) =>
        MasterRules.Fingerprint("gr-ir:write-off", $"{r.CompanyId:D}|{r.PostingDate:yyyy-MM-dd}|{r.ReasonCode}|{r.ReasonText}|{r.SourceDocumentRef}|" + string.Join("#", r.Items.Select(i =>
            string.Join("|", i.ExpectedResidualLc.ToString(CultureInfo.InvariantCulture), string.Join(",", (i.OpenItems ?? []).Select(x => $"{x.DocumentId:D}/{x.LineNo}")),
                i.PurchaseOrderId?.ToString("D") ?? "-", i.PurchaseOrderLineNo?.ToString(CultureInfo.InvariantCulture) ?? "-", i.ReceiptRef is null ? "-" : $"{i.ReceiptRef.StockDocumentId:D}/{i.ReceiptRef.LineNo}"))));
}
