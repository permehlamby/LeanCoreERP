namespace FinanceCore.Core.Masters;

// ---- open-orders:query and uninvoiced:query (11, 8, permission read.orders)
/// <summary>The lines of the orders of one kind (SALES or PURCHASE) that still have a quantity to deliver or to receive, filtered by partner, item and warehouse.</summary>
public sealed record OpenOrdersQueryRequest(Guid CompanyId, string Kind, Guid? PartnerId = null, Guid? ItemId = null, Guid? WarehouseId = null, int? Limit = null, string? Cursor = null);

/// <summary>An order line with its open quantity (4.3 for sales, 4.6 for purchase: the ordered less the fulfilled, over tolerance not counted, zero for a line or order that is not open) and both line dates.</summary>
public sealed record OpenOrderLine(
    Guid OrderId, string OrderNumber, int LineNo, Guid PartnerId, Guid ItemId, Guid? WarehouseId, decimal OrderedBaseQuantity, decimal FulfilledBaseQuantity, decimal OpenBaseQuantity,
    DateOnly? RequestedDate, DateOnly? ConfirmedDate);

public sealed record OpenOrdersQueryResult(IReadOnlyList<OpenOrderLine> Items, Reads.ReadPage Page);

/// <summary>Delivered and not invoiced (SALES) or received and not invoiced (PURCHASE) per order line, with the value at the net price of the order line. The purchase side is the detail of the GR/IR account.</summary>
public sealed record UninvoicedQueryRequest(Guid CompanyId, string Kind, Guid? PartnerId = null, Guid? ItemId = null, int? Limit = null, string? Cursor = null);

public sealed record UninvoicedLine(
    Guid OrderId, string OrderNumber, int LineNo, Guid PartnerId, Guid ItemId, string Currency, decimal FulfilledBaseQuantity, decimal InvoicedBaseQuantity, decimal UninvoicedBaseQuantity, decimal UnitPrice, decimal Value);

public sealed record UninvoicedQueryResult(IReadOnlyList<UninvoicedLine> Items, Reads.ReadPage Page);

public static class OrderQueryRules
{
    public static readonly string[] Kinds = ["SALES", "PURCHASE"];
    public const int DefaultLimit = 200, MaxLimit = 1000;
}
