using System.Globalization;

namespace FinanceCore.Core.Masters;

// ---- sales-returns:post (11, 5.4, permission sales.return) and purchase-returns:post (permission purchase.return), first slice: goods lines of live deliveries and receipts, items
// without tracking, disposition AVAILABLE. The result is a FulfilmentResult: FulfilledBaseQuantity is what is returned on the delivery or receipt line so far, OpenBaseQuantity what can still be returned.
public sealed record SalesReturnLineInput(string LineRef, Guid DeliveryId, int DeliveryLineNo, decimal Quantity, string UnitCode, string ReasonCode, string? Disposition = null, Guid? LocationId = null);

public sealed record PostSalesReturnRequest(Guid CompanyId, DateOnly ReturnDate, IReadOnlyList<SalesReturnLineInput> Lines, string? SourceDocumentRef = null);

public sealed record PurchaseReturnLineInput(string LineRef, Guid ReceiptId, int ReceiptLineNo, decimal Quantity, string UnitCode, string ReasonCode, string? Disposition = null, Guid? LocationId = null);

public sealed record PostPurchaseReturnRequest(Guid CompanyId, DateOnly ReturnDate, IReadOnlyList<PurchaseReturnLineInput> Lines, string? SourceDocumentRef = null);

/// <summary>sales-returns:reverse and purchase-returns:reverse (11, 5.6): the owning commands of a return, its STOCK_RETURN document and its journal. posting_date defaults to the return date.</summary>
public sealed record ReverseReturnRequest(Guid CompanyId, Guid ReturnId, string ReasonCode, DateOnly? PostingDate = null);

public static class ReturnRules
{
    public const string SalesPermission = "sales.return", PurchasePermission = "purchase.return";

    /// <summary>The value of a returned quantity (11, 5.4): the share of the original value that the returned base quantity is, rounded at the currency scale. The return that takes the last of the original quantity takes what is left, so that the returns add up to the original value.</summary>
    public static decimal ReturnValue(decimal originalValue, decimal originalQuantity, decimal alreadyReturnedValue, decimal alreadyReturnedQuantity, decimal returnedQuantity, int currencyScale) =>
        alreadyReturnedQuantity + returnedQuantity == originalQuantity
            ? originalValue - alreadyReturnedValue
            : Math.Round(originalValue * returnedQuantity / originalQuantity, currencyScale, MidpointRounding.AwayFromZero);

    public static byte[] Fingerprint(PostSalesReturnRequest r) =>
        MasterRules.Fingerprint("sales-returns:post", $"{r.CompanyId:D}|{r.ReturnDate:yyyy-MM-dd}|{r.SourceDocumentRef}|" + string.Join("#", r.Lines.Select(x =>
            string.Join("|", x.LineRef, x.DeliveryId.ToString("D"), x.DeliveryLineNo, x.Quantity.ToString(CultureInfo.InvariantCulture), x.UnitCode, x.ReasonCode, x.Disposition ?? "-", x.LocationId?.ToString("D") ?? "-"))));

    public static byte[] Fingerprint(PostPurchaseReturnRequest r) =>
        MasterRules.Fingerprint("purchase-returns:post", $"{r.CompanyId:D}|{r.ReturnDate:yyyy-MM-dd}|{r.SourceDocumentRef}|" + string.Join("#", r.Lines.Select(x =>
            string.Join("|", x.LineRef, x.ReceiptId.ToString("D"), x.ReceiptLineNo, x.Quantity.ToString(CultureInfo.InvariantCulture), x.UnitCode, x.ReasonCode, x.Disposition ?? "-", x.LocationId?.ToString("D") ?? "-"))));
}
