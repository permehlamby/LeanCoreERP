using System.Globalization;
using System.Security.Cryptography;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Masters;

// ---- purchase-invoices:post (11, 7.2, permission purchase.invoice), first slice: three-way matching of stock lines against receipts, two-way expense lines for service and non-stock items.
/// <summary>One invoice line of the supplier: the purchase order line, the quantity, the price and the net amount as the supplier states them, and the tax code and the supplier's tax for the line.</summary>
public sealed record PurchaseInvoiceLineInput(
    string LineRef, Guid PurchaseOrderId, int PurchaseOrderLineNo, decimal Quantity, decimal UnitPrice, decimal NetAmount, Guid? TaxCodeId = null, decimal? TaxAmount = null,
    IReadOnlyList<ReceiptLineRef>? ReceiptLineRefs = null, GrIrDirectReceiptRef? ReceiptRef = null);

public sealed record ReceiptLineRef(Guid ReceiptId, int ReceiptLineNo);

/// <summary>An additional charge of the invoice without an order (7.2): freight, duty, a fee. EXPENSE goes to PURCHASE_EXPENSE, LANDED_COST to LANDED_COST_CLEARING (to be allocated later).</summary>
public sealed record PurchaseChargeInput(string LineRef, string Description, decimal NetAmount, Guid? TaxCodeId, decimal? TaxAmount, string Treatment);

public sealed record PostPurchaseInvoiceRequest(
    Guid CompanyId, Guid SupplierPartnerId, string SupplierInvoiceNumber, DateOnly InvoiceDate, string Currency, decimal GrossAmount, IReadOnlyList<PurchaseInvoiceLineInput> Lines,
    DateOnly? PostingDate = null, DateOnly? DueDate = null, string? SourceDocumentRef = null, IReadOnlyList<PurchaseChargeInput>? Charges = null);

public sealed record PurchaseInvoiceLineResult(
    int LineNo, string LineRef, Guid OrderId, int OrderLineNo, Guid? ReceiptId, int? ReceiptLineNo, decimal BaseQuantity, decimal NetAmount, decimal TaxAmount, string MatchModeUsed,
    int JournalLineNo, Guid? GrIrClearingId);

public sealed record PurchaseInvoiceResult(
    Guid PurchaseInvoiceId, Guid JournalDocumentId, string DocumentNumber, decimal NetAmount, decimal TaxAmount, decimal GrossAmount, string RowHash,
    IReadOnlyList<PurchaseInvoiceLineResult> Lines, Guid? GrIrClearingId, Guid? StockDocumentId = null);

public static class PurchaseInvoiceRules
{
    public const string Permission = "purchase.invoice";
    public static readonly string[] ChargeTreatments = ["EXPENSE", "LANDED_COST"];

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
                    string.Join(",", (l.ReceiptLineRefs ?? []).Select(x => $"{x.ReceiptId:D}/{x.ReceiptLineNo}")), l.ReceiptRef is null ? "-" : $"{l.ReceiptRef.StockDocumentId:D}/{l.ReceiptRef.LineNo}"))) +
            "|" + string.Join("#", (r.Charges ?? []).Select(c => string.Join("|", c.LineRef, c.Description, c.NetAmount.ToString(CultureInfo.InvariantCulture), c.TaxCodeId?.ToString("D") ?? "-", c.TaxAmount?.ToString(CultureInfo.InvariantCulture) ?? "-", c.Treatment))));
}

// ---- purchase-invoices:reverse (11, 7.4, permission purchase.invoice): the owning command of an order-built AP_INVOICE, its cost adjustment and the clearing of its GR/IR items. posting_date defaults to the posting date of the invoice.
public sealed record ReversePurchaseInvoiceRequest(Guid CompanyId, Guid PurchaseInvoiceId, string ReasonCode, DateOnly? PostingDate = null);

public sealed record ReversedPurchaseInvoice(Guid PurchaseInvoiceId, Guid ReversalInvoiceId, Guid JournalDocumentId, string DocumentNumber, string RowHash, Guid? StockReversalId);

// ---- purchase-credit-notes:post (11, 7.3, permission purchase.invoice), first slice: the amount credit (a price correction or goodwill, no quantity) against the lines of one invoice.
/// <summary>One credit line: the invoice line it credits (a link row of the invoice), the net amount and the supplier's tax for it; the tax code defaults to that of the invoice line.</summary>
public sealed record PurchaseCreditLineInput(string LineRef, int InvoiceLineNo, decimal CreditAmountNet, decimal? TaxAmount, Guid? TaxCodeId = null);

public sealed record PostPurchaseCreditNoteRequest(
    Guid CompanyId, Guid PurchaseInvoiceId, string CreditKind, string SupplierCreditNoteNumber, DateOnly CreditDate, decimal GrossAmount, string ReasonCode, IReadOnlyList<PurchaseCreditLineInput> Lines,
    DateOnly? PostingDate = null, DateOnly? DueDate = null, string? SourceDocumentRef = null);

public sealed record PurchaseCreditLineResult(int LineNo, string LineRef, int CreditsInvoiceLineNo, decimal NetAmount, decimal TaxAmount, int JournalLineNo, decimal OnHandPart, decimal ConsumedPart);

public sealed record PurchaseCreditNoteResult(
    Guid CreditNoteId, Guid JournalDocumentId, string DocumentNumber, string CreditKind, decimal NetAmount, decimal TaxAmount, decimal GrossAmount, string RowHash,
    IReadOnlyList<PurchaseCreditLineResult> Lines, Guid? StockDocumentId);

public static class PurchaseCreditRules
{
    public static readonly string[] Kinds = ["RETURN", "AMOUNT"];

    /// <summary>The L1 columns of a credit line: those of an invoice line and the credited invoice line and the return line (6.9).</summary>
    public static readonly IReadOnlyList<FieldSpec> Line = [.. PurchaseInvoiceRules.Line, new("credits_invoice_line_no", FieldType.Int, true), new("return_id", FieldType.Id, true), new("return_line_no", FieldType.Int, true)];

    public static byte[] Digest(IReadOnlyDictionary<string, object?> header, IReadOnlyList<IReadOnlyDictionary<string, object?>> linesInOrder)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Canonical.Block(Canonical.EncodeFields(PurchaseInvoiceRules.Header, header)));
        foreach (var line in linesInOrder) buffer.Write(Canonical.Block(Canonical.EncodeFields(Line, line)));
        return SHA256.HashData(buffer.ToArray());
    }

    public static byte[] Fingerprint(PostPurchaseCreditNoteRequest r) =>
        MasterRules.Fingerprint("purchase-credit-notes:post", $"{r.CompanyId:D}|{r.PurchaseInvoiceId:D}|{r.CreditKind}|{r.SupplierCreditNoteNumber}|{r.CreditDate:yyyy-MM-dd}|{r.PostingDate:yyyy-MM-dd}|{r.DueDate:yyyy-MM-dd}|" +
            $"{r.GrossAmount.ToString(CultureInfo.InvariantCulture)}|{r.ReasonCode}|{r.SourceDocumentRef}|" + string.Join("#", r.Lines.Select(l =>
                string.Join("|", l.LineRef, l.InvoiceLineNo, l.CreditAmountNet.ToString(CultureInfo.InvariantCulture), l.TaxAmount?.ToString(CultureInfo.InvariantCulture) ?? "-", l.TaxCodeId?.ToString("D") ?? "-"))));
}

// ---- purchase-credit-notes:reverse (11, 7.3, permission purchase.invoice): the owning command of an order-built AP_CREDIT, its cost adjustment and the clearing of its GR/IR pair. posting_date defaults to the posting date of the credit note.
public sealed record ReversePurchaseCreditNoteRequest(Guid CompanyId, Guid CreditNoteId, string ReasonCode, DateOnly? PostingDate = null);

public sealed record ReversedPurchaseCreditNote(Guid CreditNoteId, Guid ReversalId, Guid JournalDocumentId, string DocumentNumber, string RowHash, Guid? StockReversalId);
