using System.Globalization;

namespace FinanceCore.Core.Masters;

// ---- dropship-confirmations:post and :reverse (11, 5.3, 6.6), permission dropship.confirm
public sealed record DropshipConfirmationLineInput(string LineRef, Guid SalesOrderId, int SalesOrderLineNo, decimal Quantity, string UnitCode);

public sealed record PostDropshipConfirmationRequest(
    Guid CompanyId, DateOnly ConfirmationDate, IReadOnlyList<DropshipConfirmationLineInput> Lines, string? SupplierShipmentReference = null, string? CarrierReference = null,
    string? SourceDocumentRef = null);

public sealed record DropshipConfirmationLineResult(
    int LineNo, string LineRef, Guid ConfirmationId, string ConfirmationNumber, long ChainSeq, string RowHash, Guid JournalDocumentId, decimal ValueLc, decimal DeliveredBaseQuantity);

public sealed record DropshipConfirmationResult(IReadOnlyList<DropshipConfirmationLineResult> Confirmations);

/// <summary>The reversal date defaults to the confirmation date; the reason code is of the closed list of 06, 5.3 (external callers CORRECTION or ERROR).</summary>
public sealed record ReverseDropshipConfirmationRequest(Guid CompanyId, Guid ConfirmationId, string ReasonCode, DateOnly? ReversalDate = null);

public sealed record ReversedDropshipConfirmation(Guid ConfirmationId, Guid ReversalId, Guid JournalDocumentId, string RowHash);

public static class DropshipRules
{
    public const string Permission = "dropship.confirm";
    public const string SeriesType = "DROPSHIP_CONFIRMATION";
    public const string DocumentType = "DROPSHIP_CONFIRMATION";

    public static byte[] Fingerprint(PostDropshipConfirmationRequest r) =>
        MasterRules.Fingerprint("dropship-confirmations:post",
            $"{r.CompanyId:D}|{r.ConfirmationDate:yyyy-MM-dd}|{r.SupplierShipmentReference}|{r.CarrierReference}|{r.SourceDocumentRef}|" +
            string.Join("#", r.Lines.Select(l => string.Join("|", l.LineRef, l.SalesOrderId.ToString("D"), l.SalesOrderLineNo, l.Quantity.ToString(CultureInfo.InvariantCulture), l.UnitCode))));

    public static byte[] Fingerprint(ReverseDropshipConfirmationRequest r) =>
        MasterRules.Fingerprint("dropship-confirmations:reverse", $"{r.CompanyId:D}|{r.ConfirmationId:D}|{r.ReasonCode}|{r.ReversalDate:yyyy-MM-dd}");
}
