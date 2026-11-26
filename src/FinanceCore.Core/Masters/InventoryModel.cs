using System.Text.Json;
using FinanceCore.Core.Reads;

namespace FinanceCore.Core.Masters;

/// <summary>The fields of the inventory master data (10, 3). A row is addressed by one `id` in the commands, whatever the business key of the table.</summary>
public static class InventoryFields
{
    public static readonly string[] ItemTypes = ["GOODS", "SERVICE", "NON_STOCK"];
    public static readonly string[] TrackingModes = ["NONE", "LOT", "SERIAL"];
    public static readonly string[] ValuationMethods = ["MOVING_AVERAGE", "FIFO"];
    public static readonly string[] LocationTypes = ["STANDARD", "TRANSIT", "QUARANTINE"];
    public const int MaxQuantityScale = 6;

    public static readonly IReadOnlyList<MasterField> ItemGroup =
    [
        new("code", MasterKind.Code, 50, false), new("name", MasterKind.Text, 100, false),
        new("default_cost_center_id", MasterKind.Id), new("default_profit_center_id", MasterKind.Id), new("default_business_unit_id", MasterKind.Id)
    ];

    public static readonly IReadOnlyList<MasterField> Item =
    [
        new("item_no", MasterKind.Code, 50, false), new("name", MasterKind.Text, 200, false), new("description", MasterKind.Text, 1000),
        new("item_type", MasterKind.Code, 50, false, ItemTypes), new("base_unit", MasterKind.Code, 3, false), new("quantity_scale", MasterKind.SmallInt, 0, false),
        new("item_group_id", MasterKind.Id, 0, false), new("tax_class_id", MasterKind.Id),
        new("tracking_mode", MasterKind.Code, 50, false, TrackingModes), new("expiry_tracked", MasterKind.Bool, 0, false),
        new("weight_kg", MasterKind.Amount), new("volume_m3", MasterKind.Amount),
        new("standard_identifier", MasterKind.Text, 50), new("standard_identifier_scheme", MasterKind.Code, 50), new("commodity_code", MasterKind.Text, 20),
        new("status", MasterKind.Code, 50, false, ["ACTIVE", "BLOCKED"])
    ];

    public static readonly IReadOnlyList<MasterField> ItemUnitConversionCreate =
    [
        new("item_id", MasterKind.Id, 0, false), new("unit_code", MasterKind.Code, 3, false), new("factor_to_base", MasterKind.Factor, 0, false)
    ];
    public static readonly IReadOnlyList<MasterField> ItemUnitConversionUpdate = [new("factor_to_base", MasterKind.Factor, 0, false)];

    public static readonly IReadOnlyList<MasterField> UnitConversionCreate =
    [
        new("from_unit", MasterKind.Code, 3, false), new("to_unit", MasterKind.Code, 3, false), new("factor", MasterKind.Factor, 0, false)
    ];
    public static readonly IReadOnlyList<MasterField> UnitConversionUpdate = [new("factor", MasterKind.Factor, 0, false)];

    public static readonly IReadOnlyList<MasterField> WarehouseCreate =
    [
        new("company_id", MasterKind.Id, 0, false), new("code", MasterKind.Code, 50, false), new("name", MasterKind.Text, 100, false),
        new("country_code", MasterKind.Country, 0, false), new("address", MasterKind.Text, 500),
        new("default_cost_center_id", MasterKind.Id), new("default_profit_center_id", MasterKind.Id), new("default_business_unit_id", MasterKind.Id)
    ];
    public static readonly IReadOnlyList<MasterField> WarehouseUpdate = [.. WarehouseCreate.Where(f => f.Name != "company_id")];

    public static readonly IReadOnlyList<MasterField> LocationCreate =
    [
        new("company_id", MasterKind.Id, 0, false), new("warehouse_id", MasterKind.Id, 0, false), new("location_code", MasterKind.Code, 50, false),
        new("name", MasterKind.Text, 100, false), new("location_type", MasterKind.Code, 50, false, LocationTypes)
    ];
    public static readonly IReadOnlyList<MasterField> LocationUpdate = [.. LocationCreate.Where(f => f.Name is "location_code" or "name" or "location_type")];

    public static readonly IReadOnlyList<MasterField> ItemCompanyCreate =
    [
        new("company_id", MasterKind.Id, 0, false), new("item_id", MasterKind.Id, 0, false),
        new("valuation_method", MasterKind.Code, 50, false, ValuationMethods), new("default_warehouse_id", MasterKind.Id)
    ];
    public static readonly IReadOnlyList<MasterField> ItemCompanyUpdate = [.. ItemCompanyCreate.Where(f => f.Name is "valuation_method" or "default_warehouse_id")];

    public static readonly IReadOnlyList<MasterField> ItemWarehouseCreate =
    [
        new("company_id", MasterKind.Id, 0, false), new("warehouse_id", MasterKind.Id, 0, false), new("item_id", MasterKind.Id, 0, false),
        new("allow_negative_stock", MasterKind.Bool, 0, false), new("min_quantity", MasterKind.Amount), new("reorder_quantity", MasterKind.Amount)
    ];
    public static readonly IReadOnlyList<MasterField> ItemWarehouseUpdate = [.. ItemWarehouseCreate.Where(f => f.Name is "allow_negative_stock" or "min_quantity" or "reorder_quantity")];
}

/// <summary>The payload of every create of the inventory master data, and of every update: the row is addressed by `id`.</summary>
public sealed record CreateInventoryMasterRequest(IReadOnlyDictionary<string, object?> Fields);

public sealed record UpdateInventoryMasterRequest(Guid Id, long ExpectedVersion, IReadOnlyDictionary<string, object?> Fields);

public static class InventoryRules
{
    public static byte[] Fingerprint(string resource, CreateInventoryMasterRequest r) => MasterRules.Fingerprint(resource + ":create", MasterRules.Text(r.Fields));

    public static byte[] Fingerprint(string resource, UpdateInventoryMasterRequest r) =>
        MasterRules.Fingerprint(resource + ":update", r.Id.ToString("D") + "|" + r.ExpectedVersion + "|" + MasterRules.Text(r.Fields));
}

// ---- the generic reads of the master data (09, 3): {resource}:get and {resource}:list, permission read.config
public sealed record GetMasterRequest(Guid Id, IReadOnlyList<string>? Include = null);

/// <summary>Contains: a substring search (09, 2.4.1) on name and description of the resources that have the column.</summary>
public sealed record ListMasterRequest(
    IReadOnlyDictionary<string, string>? Filter = null, bool IncludeInactive = false, int? Limit = null, string? Cursor = null, IReadOnlyDictionary<string, string>? Contains = null, IReadOnlyList<string>? Include = null, IReadOnlyList<Feed.ExtensionFilter>? Ext = null);

public sealed record MasterRowResult(JsonElement Row);

public sealed record MasterRowList(IReadOnlyList<JsonElement> Items, ReadPage Page);
