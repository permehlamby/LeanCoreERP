using System.Globalization;

namespace FinanceCore.Core.Masters;

// ---- sales-deliveries:post (11, 6.3) and purchase-receipts:post (11, 7.1), first slice: stock and service lines of live orders, items without tracking
public sealed record DeliveryLineInput(string LineRef, Guid SalesOrderId, int SalesOrderLineNo, decimal Quantity, string UnitCode, Guid? WarehouseId = null, Guid? LocationId = null);

public sealed record PostDeliveryRequest(
    Guid CompanyId, DateOnly DeliveryDate, IReadOnlyList<DeliveryLineInput> Lines, Guid? WarehouseId = null, string? CarrierReference = null, string? TrackingReference = null,
    string? SourceDocumentRef = null);

public sealed record ReceiptLineInput(string LineRef, Guid PurchaseOrderId, int PurchaseOrderLineNo, decimal Quantity, string UnitCode, Guid? WarehouseId = null, Guid? LocationId = null);

public sealed record PostReceiptRequest(
    Guid CompanyId, DateOnly ReceiptDate, IReadOnlyList<ReceiptLineInput> Lines, Guid? WarehouseId = null, string? DeliveryNoteReference = null, string? SourceDocumentRef = null);

/// <summary>A line of a fulfilment document as posted: the base quantity, the cost or receipt value, the movement of the stock document and the progress of the order line afterwards.</summary>
public sealed record FulfilmentLineResult(
    int LineNo, string LineRef, Guid OrderId, int OrderLineNo, decimal BaseQuantity, decimal? ValueLc, int? StockDocumentLineNo, decimal FulfilledBaseQuantity, decimal OpenBaseQuantity);

public sealed record FulfilmentResult(
    Guid DocumentId, string DocumentNumber, long ChainSeq, string RowHash, Guid? StockDocumentId, Guid? JournalDocumentId, IReadOnlyList<FulfilmentLineResult> Lines);

public static class FulfilmentRules
{
    public const string DeliverPermission = "sales.deliver", ReceivePermission = "purchase.receive";

    /// <summary>The most that can be delivered or received on a line (11, 4.3): the ordered quantity plus the over tolerance, less what is already done, never below zero. A limit, not a demand.</summary>
    public static decimal Limit(decimal orderedBase, decimal overTolerancePct, decimal fulfilledBase) =>
        Math.Max(0m, orderedBase + orderedBase * overTolerancePct / 100m - fulfilledBase);

    /// <summary>The open quantity (11, 4.3): the ordered quantity less what is done, never below zero, over tolerance not counted. Zero for a line that is not open.</summary>
    public static decimal Open(decimal orderedBase, decimal fulfilledBase, bool lineOpen) => lineOpen ? Math.Max(0m, orderedBase - fulfilledBase) : 0m;

    /// <summary>The value of a receipt line (11, 7.1): the share of the net amount of the order line that the received base quantity is, at the scale of the currency.</summary>
    public static decimal ReceiptValue(decimal orderLineNetAmount, decimal orderLineBaseQuantity, decimal receivedBaseQuantity, int currencyScale) =>
        Math.Round(orderLineNetAmount * receivedBaseQuantity / orderLineBaseQuantity, currencyScale, MidpointRounding.AwayFromZero);

    private static string Lines(IReadOnlyList<DeliveryLineInput> l) => string.Join("#", l.Select(x => string.Join("|", x.LineRef, x.SalesOrderId.ToString("D"), x.SalesOrderLineNo,
        x.Quantity.ToString(CultureInfo.InvariantCulture), x.UnitCode, x.WarehouseId?.ToString("D") ?? "-", x.LocationId?.ToString("D") ?? "-")));

    private static string Lines(IReadOnlyList<ReceiptLineInput> l) => string.Join("#", l.Select(x => string.Join("|", x.LineRef, x.PurchaseOrderId.ToString("D"), x.PurchaseOrderLineNo,
        x.Quantity.ToString(CultureInfo.InvariantCulture), x.UnitCode, x.WarehouseId?.ToString("D") ?? "-", x.LocationId?.ToString("D") ?? "-")));

    public static byte[] Fingerprint(PostDeliveryRequest r) =>
        MasterRules.Fingerprint("sales-deliveries:post", $"{r.CompanyId:D}|{r.DeliveryDate:yyyy-MM-dd}|{r.WarehouseId}|{r.CarrierReference}|{r.TrackingReference}|{r.SourceDocumentRef}|{Lines(r.Lines)}");

    public static byte[] Fingerprint(PostReceiptRequest r) =>
        MasterRules.Fingerprint("purchase-receipts:post", $"{r.CompanyId:D}|{r.ReceiptDate:yyyy-MM-dd}|{r.WarehouseId}|{r.DeliveryNoteReference}|{r.SourceDocumentRef}|{Lines(r.Lines)}");
}
