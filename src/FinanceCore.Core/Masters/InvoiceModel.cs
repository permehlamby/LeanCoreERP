using System.Globalization;
using System.Security.Cryptography;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Masters;

// ---- sales-invoices:create (11, 6.4, permission sales.invoice), first slice: the lines of orders of one customer, both modes, stock and service lines, items without tracking.
public sealed record DeliveryLineRef(Guid DeliveryId, int DeliveryLineNo);

/// <summary>One invoice line: the order line, and in CORE_BUILDS optionally the quantity and the delivery lines; in APP_SUPPLIES the quantity, the unit price and the discount (6.4).</summary>
public sealed record SalesInvoiceLineInput(
    string LineRef, Guid SalesOrderId, int SalesOrderLineNo, decimal? Quantity = null, string? UnitCode = null, Guid? TaxCodeId = null, IReadOnlyList<DeliveryLineRef>? DeliveryLineRefs = null,
    decimal? UnitPrice = null, decimal? DiscountPct = null);

public sealed record CreateSalesInvoiceRequest(
    Guid CompanyId, DateOnly InvoiceDate, IReadOnlyList<SalesInvoiceLineInput> Lines, DateOnly? PostingDate = null, DateOnly? DueDate = null, string? SourceDocumentRef = null);

public sealed record SalesInvoiceLineResult(
    int LineNo, string LineRef, Guid OrderId, int OrderLineNo, Guid? DeliveryId, int? DeliveryLineNo, decimal BaseQuantity, decimal NetAmount, int JournalLineNo, Guid? TaxCodeId);

public sealed record SalesInvoiceResult(
    Guid SalesInvoiceId, Guid JournalDocumentId, string DocumentNumber, string InvoiceMode, decimal NetAmount, decimal TaxAmount, decimal GrossAmount, string RowHash,
    IReadOnlyList<SalesInvoiceLineResult> Lines);

public static class InvoiceRules
{
    public const string SalesPermission = "sales.invoice";

    /// <summary>The open quantity to invoice of an order line (11, 4.3): DELIVERED policy the delivered less the invoiced, ORDERED policy the ordered plus the over tolerance less the invoiced. Never below zero.</summary>
    public static decimal OpenToInvoice(string invoicePolicy, decimal orderedBase, decimal overTolerancePct, decimal deliveredBase, decimal invoicedBase) =>
        Math.Max(0m, (invoicePolicy == "DELIVERED" ? deliveredBase : orderedBase + orderedBase * overTolerancePct / 100m) - invoicedBase);

    private static FieldSpec Id(string n, bool nullable = false) => new(n, FieldType.Id, nullable);
    private static FieldSpec Code(string n, bool nullable = false) => new(n, FieldType.Text, nullable);
    private static FieldSpec Int(string n, bool nullable = false) => new(n, FieldType.Int, nullable);
    private static FieldSpec Amt(string n, bool nullable = false) => new(n, FieldType.Amount, nullable);

    /// <summary>The L1 columns of the link header that the journal document binds (the id of the journal document is not among them: it holds this digest).</summary>
    public static readonly IReadOnlyList<FieldSpec> Header =
    [
        Id("sales_invoice_id"), Code("invoice_kind"), Id("customer_partner_id"), new("invoice_date", FieldType.Date), new("due_date", FieldType.Date, true), Id("payment_terms_id", true),
        Code("order_currency"), Id("credits_invoice_id", true), Code("credit_kind", true), Id("reversal_of_sales_invoice_id", true), Code("reason_code", true), Code("invoice_mode", true), Int("line_count")
    ];

    public static readonly IReadOnlyList<FieldSpec> Line =
    [
        Int("line_no"), Id("sales_order_id"), Int("sales_order_line_no"), Id("delivery_id", true), Int("delivery_line_no", true), Id("dropship_confirmation_id", true),
        new("quantity", FieldType.Rate), Code("unit_code"), new("base_quantity", FieldType.Rate), Amt("unit_price"), new("discount_pct", FieldType.Rate), Amt("net_amount"), Amt("order_unit_price", true),
        new("order_discount_pct", FieldType.Rate, true), Id("tax_code_id", true), Id("tax_code_version_id", true), new("tax_date", FieldType.Date, true), Int("journal_line_no")
    ];

    /// <summary>SHA-256 over the header block and one block per line in line_no order: the detail_digest of the journal document (ADR-052).</summary>
    public static byte[] Digest(IReadOnlyDictionary<string, object?> header, IReadOnlyList<IReadOnlyDictionary<string, object?>> linesInOrder)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Canonical.Block(Canonical.EncodeFields(Header, header)));
        foreach (var line in linesInOrder) buffer.Write(Canonical.Block(Canonical.EncodeFields(Line, line)));
        return SHA256.HashData(buffer.ToArray());
    }

    public static byte[] Fingerprint(CreateSalesInvoiceRequest r) =>
        MasterRules.Fingerprint("sales-invoices:create", $"{r.CompanyId:D}|{r.InvoiceDate:yyyy-MM-dd}|{r.PostingDate:yyyy-MM-dd}|{r.DueDate:yyyy-MM-dd}|{r.SourceDocumentRef}|" + string.Join("#", r.Lines.Select(l =>
            string.Join("|", l.LineRef, l.SalesOrderId.ToString("D"), l.SalesOrderLineNo, l.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "-", l.UnitCode ?? "-", l.TaxCodeId?.ToString("D") ?? "-",
                string.Join(",", (l.DeliveryLineRefs ?? []).Select(d => $"{d.DeliveryId:D}/{d.DeliveryLineNo}")), l.UnitPrice?.ToString(CultureInfo.InvariantCulture) ?? "-",
                l.DiscountPct?.ToString(CultureInfo.InvariantCulture) ?? "-"))));
}

// ---- sales-invoices:reverse (11, 6.4, permission sales.invoice): the owning command of an order-built AR_INVOICE and its link records. posting_date defaults to the posting date of the invoice.
public sealed record ReverseSalesInvoiceRequest(Guid CompanyId, Guid SalesInvoiceId, string ReasonCode, DateOnly? PostingDate = null);

public sealed record ReversedSalesInvoice(Guid SalesInvoiceId, Guid ReversalInvoiceId, Guid JournalDocumentId, string DocumentNumber, string RowHash);

// ---- sales-credit-notes:create (11, 6.5, permission sales.invoice): credits the lines of one invoice. A return credit names return lines and credits the returned quantity at the original invoice
// price and tax codes; an amount credit names invoice lines and a net amount (price correction or goodwill, no quantity).
/// <summary>One credit line: the invoice line it credits and, for a return credit, the return line and the quantity (default the most that the caps allow); for an amount credit the net amount.</summary>
public sealed record CreditLineInput(string LineRef, int InvoiceLineNo, Guid? ReturnId = null, int? ReturnLineNo = null, decimal? Quantity = null, decimal? CreditAmountNet = null);

public sealed record CreateSalesCreditNoteRequest(
    Guid CompanyId, Guid SalesInvoiceId, string CreditKind, DateOnly CreditDate, IReadOnlyList<CreditLineInput> Lines, string? ReasonCode = null, DateOnly? PostingDate = null, DateOnly? DueDate = null,
    string? SourceDocumentRef = null);

public sealed record SalesCreditNoteResult(
    Guid CreditNoteId, Guid JournalDocumentId, string DocumentNumber, string CreditKind, decimal NetAmount, decimal TaxAmount, decimal GrossAmount, string RowHash, IReadOnlyList<SalesInvoiceLineResult> Lines);

public sealed record ReverseSalesCreditNoteRequest(Guid CompanyId, Guid CreditNoteId, string ReasonCode, DateOnly? PostingDate = null);

public sealed record ReversedSalesCreditNote(Guid CreditNoteId, Guid ReversalId, Guid JournalDocumentId, string DocumentNumber, string RowHash);

public static class CreditRules
{
    public static readonly string[] Kinds = ["RETURN", "AMOUNT"];

    /// <summary>The L1 columns of a credit line: those of an invoice line and the credited invoice line and the return line (6.9).</summary>
    public static readonly IReadOnlyList<FieldSpec> Line = [.. InvoiceRules.Line, new("credits_invoice_line_no", FieldType.Int, true), new("return_id", FieldType.Id, true), new("return_line_no", FieldType.Int, true)];

    public static byte[] Digest(IReadOnlyDictionary<string, object?> header, IReadOnlyList<IReadOnlyDictionary<string, object?>> linesInOrder)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Canonical.Block(Canonical.EncodeFields(InvoiceRules.Header, header)));
        foreach (var line in linesInOrder) buffer.Write(Canonical.Block(Canonical.EncodeFields(Line, line)));
        return SHA256.HashData(buffer.ToArray());
    }

    public static byte[] Fingerprint(CreateSalesCreditNoteRequest r) =>
        MasterRules.Fingerprint("sales-credit-notes:create", $"{r.CompanyId:D}|{r.SalesInvoiceId:D}|{r.CreditKind}|{r.CreditDate:yyyy-MM-dd}|{r.PostingDate:yyyy-MM-dd}|{r.DueDate:yyyy-MM-dd}|{r.ReasonCode}|{r.SourceDocumentRef}|" +
            string.Join("#", r.Lines.Select(l => string.Join("|", l.LineRef, l.InvoiceLineNo, l.ReturnId?.ToString("D") ?? "-", l.ReturnLineNo?.ToString(CultureInfo.InvariantCulture) ?? "-",
                l.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "-", l.CreditAmountNet?.ToString(CultureInfo.InvariantCulture) ?? "-"))));
}
