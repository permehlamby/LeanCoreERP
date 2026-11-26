using System.Text.Json;

namespace FinanceCore.Core.Masters;

// ---- the get and list of the documents of orders (11, 8, permission read.orders): deliveries, receipts, returns and invoices.
public sealed record GetDocumentRequest(Guid Id, IReadOnlyList<string>? Include = null);

/// <summary>The header as typed JSON and the includes asked for: lines, for an invoice the journal document, and the issue records of an invoice (22).</summary>
public sealed record DocumentDetail(JsonElement Document, JsonElement? Lines, JsonElement? JournalDocument, JsonElement? IssueRecords);

/// <summary>The filters of a list: the company, the partner (customer or supplier), the date range of the document, the status of a fulfilment document (posted or reversed) or the kind of an invoice (INVOICE, CREDIT_NOTE, REVERSAL).</summary>
public sealed record ListDocumentsRequest(Guid CompanyId, Guid? PartnerId = null, DateOnly? DateFrom = null, DateOnly? DateTo = null, string? Status = null, int? Limit = null);

public sealed record DocumentList(IReadOnlyList<JsonElement> Items, int Limit, bool Truncated);

/// <summary>One kind of document: the resource, the tables and the columns that the read needs.</summary>
public sealed record DocumentKind(string Resource, string Header, string Line, string IdColumn, string DateColumn, string PartyColumn, string StatusColumn, bool Chained, bool Invoice, string InvoiceKind = "")
{
    public static readonly IReadOnlyList<DocumentKind> All =
    [
        new("sales-deliveries", "sales_delivery", "sales_delivery_line", "delivery_id", "delivery_date", "customer_partner_id", "status", true, false),
        new("purchase-receipts", "purchase_receipt", "purchase_receipt_line", "receipt_id", "receipt_date", "supplier_partner_id", "status", true, false),
        new("sales-returns", "sales_return", "sales_return_line", "return_id", "return_date", "customer_partner_id", "status", true, false),
        new("purchase-returns", "purchase_return", "purchase_return_line", "return_id", "return_date", "supplier_partner_id", "status", true, false),
        new("sales-invoices", "sales_invoice", "sales_invoice_line", "sales_invoice_id", "invoice_date", "customer_partner_id", "invoice_kind", false, true, "SALES"),
        new("purchase-invoices", "purchase_invoice", "purchase_invoice_line", "purchase_invoice_id", "invoice_date", "supplier_partner_id", "invoice_kind", false, true, "PURCHASE")
    ];

    public static readonly string[] Includes = ["lines", "journal_document", "issue_records"];
    public const int DefaultLimit = 200, MaxLimit = 1000;
}
