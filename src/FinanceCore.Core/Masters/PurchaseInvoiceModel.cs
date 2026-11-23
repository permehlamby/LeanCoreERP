using System.Globalization;
using System.Security.Cryptography;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Masters;

// ---- purchase-invoices:post (11, 7.2, permission purchase.invoice), first slice: three-way matching of stock lines against receipts, two-way expense lines for service and non-stock items.
/// <summary>One invoice line of the supplier: the purchase order line, the quantity, the price and the net amount as the supplier states them, and the tax code and the supplier's tax for the line.</summary>
public sealed record PurchaseInvoiceLineInput(
    string LineRef, Guid PurchaseOrderId, int PurchaseOrderLineNo, decimal Quantity, decimal UnitPrice, decimal NetAmount, Guid? TaxCodeId = null, decimal? TaxAmount = null,
    IReadOnlyList<ReceiptLineRef>? ReceiptLineRefs = null);

public sealed record ReceiptLineRef(Guid ReceiptId, int ReceiptLineNo);

public sealed record PostPurchaseInvoiceRequest(
    Guid CompanyId, Guid SupplierPartnerId, string SupplierInvoiceNumber, DateOnly InvoiceDate, string Currency, decimal GrossAmount, IReadOnlyList<PurchaseInvoiceLineInput> Lines,
    DateOnly? PostingDate = null, DateOnly? DueDate = null, string? SourceDocumentRef = null);

public sealed record PurchaseInvoiceLineResult(
    int LineNo, string LineRef, Guid OrderId, int OrderLineNo, Guid? ReceiptId, int? ReceiptLineNo, decimal BaseQuantity, decimal NetAmount, decimal TaxAmount, string MatchModeUsed,
    int JournalLineNo, Guid? GrIrClearingId);

public sealed record PurchaseInvoiceResult(
    Guid PurchaseInvoiceId, Guid JournalDocumentId, string DocumentNumber, decimal NetAmount, decimal TaxAmount, decimal GrossAmount, string RowHash,
    IReadOnlyList<PurchaseInvoiceLineResult> Lines, Guid? GrIrClearingId, Guid? StockDocumentId = null);

public static class PurchaseInvoiceRules
{
    public const string Permission = "purchase.invoice";

    private static FieldSpec Id(string n, bool nullable = false) => new(n, FieldType.Id, nullable);
    private static FieldSpec Code(string n, bool nullable = false) => new(n, FieldType.Text, nullable);
    private static FieldSpec Int(string n, bool nullable = false) => new(n, FieldType.Int, nullable);
    private static FieldSpec Amt(string n, bool nullable = false) => new(n, FieldType.Amount, nullable);

    /// <summary>The L1 columns of the link header that the journal document binds (11, 6.9). The id of the journal document is not among them: it holds this digest.</summary>
    public static readonly IReadOnlyList<FieldSpec> Header =
    [
        Id("purchase_invoice_id"), Code("invoice_kind"), Id("supplier_partner_id"), Code("supplier_invoice_number", true), new("invoice_date", FieldType.Date), new("due_date", FieldType.Date, true),
        Code("currency"), Amt("gross_amount"), Id("stock_document_id", true), Id("credits_invoice_id", true), Code("credit_kind", true), Id("reversal_of_purchase_invoice_id", true), Code("reason_code", true),
        Int("line_count")
    ];

    public static readonly IReadOnlyList<FieldSpec> Line =
    [
        Int("line_no"), Code("line_kind"), Id("purchase_order_id", true), Int("purchase_order_line_no", true), Id("receipt_id", true), Int("receipt_line_no", true), Id("stock_document_id", true),
        Int("stock_document_line_no", true), new("base_quantity", FieldType.Rate), Amt("unit_price"), Amt("net_amount"), Amt("tax_amount"), Amt("order_unit_price", true), Code("match_mode_used", true),
        new("tolerance_used", FieldType.Rate, true), Amt("price_difference_lc"), Amt("on_hand_part_lc"), Amt("consumed_part_lc"), Amt("dropship_part_lc"), Amt("unreceived_gr_ir_lc"),
        Id("gr_ir_clearing_id", true), Id("tax_code_id", true), Id("tax_code_version_id", true), new("tax_date", FieldType.Date, true), Int("journal_line_no")
    ];

    /// <summary>SHA-256 over the header block and one block per line in line_no order: the detail_digest of the journal document (ADR-052).</summary>
    public static byte[] Digest(IReadOnlyDictionary<string, object?> header, IReadOnlyList<IReadOnlyDictionary<string, object?>> linesInOrder)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Canonical.Block(Canonical.EncodeFields(Header, header)));
        foreach (var line in linesInOrder) buffer.Write(Canonical.Block(Canonical.EncodeFields(Line, line)));
        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>The most that may be invoiced of an order line (7.2, D1): THREE_WAY the received less the returned, TWO_WAY the ordered plus the line over tolerance. Never below zero.</summary>
    public static decimal OpenToInvoice(string matchMode, decimal orderedBase, decimal overTolerancePct, decimal receivedBase, decimal returnedBase, decimal invoicedBase) =>
        Math.Max(0m, (matchMode == "THREE_WAY" ? receivedBase - returnedBase : orderedBase + orderedBase * overTolerancePct / 100m) - invoicedBase);

    public static byte[] Fingerprint(PostPurchaseInvoiceRequest r) =>
        MasterRules.Fingerprint("purchase-invoices:post", $"{r.CompanyId:D}|{r.SupplierPartnerId:D}|{r.SupplierInvoiceNumber}|{r.InvoiceDate:yyyy-MM-dd}|{r.PostingDate:yyyy-MM-dd}|{r.DueDate:yyyy-MM-dd}|{r.Currency}|" +
            $"{r.GrossAmount.ToString(CultureInfo.InvariantCulture)}|{r.SourceDocumentRef}|" + string.Join("#", r.Lines.Select(l =>
                string.Join("|", l.LineRef, l.PurchaseOrderId.ToString("D"), l.PurchaseOrderLineNo, l.Quantity.ToString(CultureInfo.InvariantCulture), l.UnitPrice.ToString(CultureInfo.InvariantCulture),
                    l.NetAmount.ToString(CultureInfo.InvariantCulture), l.TaxCodeId?.ToString("D") ?? "-", l.TaxAmount?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    string.Join(",", (l.ReceiptLineRefs ?? []).Select(x => $"{x.ReceiptId:D}/{x.ReceiptLineNo}"))))));
}
