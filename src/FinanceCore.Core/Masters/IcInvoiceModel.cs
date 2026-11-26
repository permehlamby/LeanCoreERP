namespace FinanceCore.Core.Masters;

// ---- ic-invoices:create (11, 6.7, ADR-016, permission ic.invoice): the AR invoice of the seller and the AP invoice of the buyer, one atomic pair
/// <summary>A line of the pair by its line number (the two orders have the same lines in the same order) and the quantity to invoice.</summary>
public sealed record IcInvoiceLineInput(string LineRef, int OrderLineNo, decimal Quantity);

public sealed record CreateIcInvoiceRequest(
    Guid IcTransactionId, DateOnly InvoiceDate, IReadOnlyList<IcInvoiceLineInput> Lines, DateOnly? PostingDate = null, DateOnly? DueDate = null, string? SupplierInvoiceNumber = null,
    string? SourceDocumentRef = null);

public sealed record IcInvoiceResult(Guid IcTransactionId, SalesInvoiceResult SalesInvoice, PurchaseInvoiceResult PurchaseInvoice);

public static class IcInvoiceRules
{
    public const string Permission = "ic.invoice";

    public static string SideKey(string baseKey, int position) => $"{baseKey}#{position}";

    public static byte[] Fingerprint(CreateIcInvoiceRequest r) =>
        MasterRules.Fingerprint("ic-invoices:create", string.Join("|", r.IcTransactionId.ToString("D"), r.InvoiceDate.ToString("yyyy-MM-dd"), r.PostingDate?.ToString("yyyy-MM-dd"), r.DueDate?.ToString("yyyy-MM-dd"),
            r.SupplierInvoiceNumber, r.SourceDocumentRef, string.Join("#", r.Lines.Select(l => $"{l.LineRef}/{l.OrderLineNo}/{l.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))));
}
