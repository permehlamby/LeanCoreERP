using System.Globalization;

namespace FinanceCore.Core.Masters;

// ---- ic-orders:create (11, 6.7, ADR-016, permission ic.order): a sales order in the seller and a purchase order in the buyer, one set of lines
/// <summary>
/// The seller is SalesCompanyId, the buyer PurchaseCompanyId. CustomerPartnerId is the buyer as a customer of the seller (its ic_company_id is the buyer), SupplierPartnerId the seller as a supplier of the buyer
/// (its ic_company_id is the seller). A line is a line of a sales order (11, 6.1) with the optional field buyer_warehouse_id, the warehouse of the buyer's purchase line (warehouse_id is the seller's).
/// </summary>
public sealed record CreateIcOrderRequest(
    Guid SalesCompanyId, Guid PurchaseCompanyId, DateOnly OrderDate, string OrderCurrency, Guid CustomerPartnerId, Guid SupplierPartnerId,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Lines, DateOnly? DeliveryDate = null, string? HeaderText = null, string? SourceDocumentRef = null, string? ShipFromCountry = null);

public sealed record IcOrderResult(Guid IcTransactionId, OrderResult SalesOrder, OrderResult PurchaseOrder);

public static class IcOrderRules
{
    public const string Permission = "ic.order";
    public const string BuyerWarehouseField = "buyer_warehouse_id";

    /// <summary>The fields of a sales line that a purchase line does not have (6.1).</summary>
    private static readonly string[] SalesOnly = ["fulfilment_type", "invoice_policy", "warehouse_id", BuyerWarehouseField];

    /// <summary>The sales line without the buyer's warehouse.</summary>
    public static IReadOnlyDictionary<string, object?> SalesLine(IReadOnlyDictionary<string, object?> line) =>
        line.Where(f => f.Key != BuyerWarehouseField).ToDictionary(f => f.Key, f => f.Value);

    /// <summary>The purchase line: the common fields of the line and the buyer's warehouse as its warehouse.</summary>
    public static IReadOnlyDictionary<string, object?> PurchaseLine(IReadOnlyDictionary<string, object?> line)
    {
        var purchase = line.Where(f => !SalesOnly.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value);
        if (line.TryGetValue(BuyerWarehouseField, out object? warehouse) && warehouse is not null) purchase["warehouse_id"] = warehouse;
        return purchase;
    }

    public static string SideKey(string baseKey, int position) => $"{baseKey}#{position}";

    public static byte[] Fingerprint(CreateIcOrderRequest r) =>
        MasterRules.Fingerprint("ic-orders:create", string.Join("|", r.SalesCompanyId.ToString("D"), r.PurchaseCompanyId.ToString("D"), r.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.OrderCurrency,
            r.CustomerPartnerId.ToString("D"), r.SupplierPartnerId.ToString("D"), r.DeliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.HeaderText, r.SourceDocumentRef, r.ShipFromCountry,
            string.Join("#", r.Lines.Select(MasterRules.Text))));
}
