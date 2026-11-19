using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// The master data of items and warehouses (10, 3, 07, 11.1; permission write.config): item-groups, items, item-unit-conversions, unit-conversions, warehouses,
/// warehouse-locations, item-companies and item-warehouses, each with create, update, deactivate and reactivate on the generic master commands. A row is addressed by `id`.
/// Built choices are in 07, 13.2s. The rules that wait for the stock ledger (an item's type, unit, scale and tracking, the valuation method of an item company and the type of
/// a location cannot change once a stock movement uses them) are not enforced: no movement exists yet.
/// </summary>
public sealed class InventoryMasterCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;
    private readonly MasterTableCommands _generic = new(source);

    public static readonly string[] Resources =
        ["item-groups", "items", "item-unit-conversions", "unit-conversions", "warehouses", "warehouse-locations", "item-companies", "item-warehouses"];

    private static string Id(MasterCheckInput i) => (i.Id ?? Guid.Empty).ToString("D");

    private static async Task Ref(MasterCheckInput i, List<CommandError> errors, string field, string sql, params (string, object)[] extra)
    {
        if (i.Get(field) is not { } value || !i.Changed(field)) return;
        var p = new List<(string, object)> { ("x", Guid.Parse(value)) };
        p.AddRange(extra);
        if (!await ExistsAsync(i.Tx, sql, i.TenantId, [.. p]))
            errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/" + field, (field, value)));
    }

    private static async Task Duplicate(MasterCheckInput i, List<CommandError> errors, string sql, string path, params (string, object)[] p)
    {
        if (await ExistsAsync(i.Tx, sql, i.TenantId, [.. p, ("id", (object?)i.Id ?? DBNull.Value)]))
            errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", path));
    }

    private static async Task DimensionDefaults(MasterCheckInput i, List<CommandError> errors, Guid? company)
    {
        foreach (string f in new[] { "default_cost_center_id", "default_profit_center_id", "default_business_unit_id" })
            // a value of the tenant, for all companies or for the company of the warehouse (10, 3.6)
            await Ref(i, errors, f, "select 1 from core.dimension_value where tenant_id = @t and dimension_value_id = @x and (company_id is null or company_id is not distinct from @c::uuid)",
                ("c", (object?)company ?? DBNull.Value));
    }

    private static async Task<bool> UnitExists(MasterCheckInput i, string unit) =>
        await ExistsAsync(i.Tx, "select 1 from platform.ref_unit where unit_code = @u", i.TenantId, ("u", unit));

    // ---------------------------------------------------------------------------------------------------------
    // item groups
    // ---------------------------------------------------------------------------------------------------------
    private static readonly MasterSpec GroupSpec = new("item_group", "item_group_id", "ITEM_GROUP", "/id", "item-groups", Permission,
        InventoryFields.ItemGroup, InventoryFields.ItemGroup, ["code", "name"], [], async i =>
        {
            var errors = new List<CommandError>();
            if (i.Changed("code")) await Duplicate(i, errors, "select 1 from core.item_group where tenant_id = @t and code = @c and item_group_id is distinct from @id::uuid", "/fields/code", ("c", i.Get("code")!));
            await DimensionDefaults(i, errors, null);
            return errors;
        },
        async (tx, tenantId, id, _, ct) => await ExistsAsync(tx, "select 1 from core.item where tenant_id = @t and item_group_id = @x and active", tenantId, ("x", id))
            ? Error("CONFIG.REFERENCED", "P2", "/id", ("reason", "ACTIVE_ITEM")) : null);

    // ---------------------------------------------------------------------------------------------------------
    // items
    // ---------------------------------------------------------------------------------------------------------
    private static readonly MasterSpec ItemSpec = new("item", "item_id", "ITEM", "/id", "items", Permission,
        InventoryFields.Item, InventoryFields.Item, ["item_no", "name", "item_type", "base_unit", "quantity_scale", "item_group_id"], [], async i =>
        {
            var errors = new List<CommandError>();
            if (i.Changed("item_no")) await Duplicate(i, errors, "select 1 from core.item where tenant_id = @t and item_no = @c and item_id is distinct from @id::uuid", "/fields/item_no", ("c", i.Get("item_no")!));
            if (i.Changed("base_unit") && !await UnitExists(i, i.Get("base_unit")!))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/base_unit", ("unit_code", i.Get("base_unit"))));
            await Ref(i, errors, "item_group_id", "select 1 from core.item_group where tenant_id = @t and item_group_id = @x");
            await Ref(i, errors, "tax_class_id", "select 1 from core.tax_class where tenant_id = @t and tax_class_id = @x");
            if (i.Get("quantity_scale") is { } scale && int.Parse(scale, CultureInfo.InvariantCulture) > InventoryFields.MaxQuantityScale)
                errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/quantity_scale", ("reason", $"at most {InventoryFields.MaxQuantityScale}")));
            string tracking = i.Get("tracking_mode") ?? "NONE", type = i.Get("item_type") ?? "GOODS";
            bool expiry = i.Get("expiry_tracked") == "true";
            if (type != "GOODS" && (tracking != "NONE" || expiry))
                errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/tracking_mode", ("reason", "a service and a non-stock item are not tracked")));
            else if (expiry && tracking != "LOT")
                errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/expiry_tracked", ("reason", "only a lot has an expiry date")));
            if (i.Get("standard_identifier_scheme") is not null && i.Get("standard_identifier") is null)
                errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/standard_identifier", ("reason", "the scheme needs the identifier")));
            return errors;
        },
        async (tx, tenantId, id, _, ct) => await ExistsAsync(tx, "select 1 from core.item_company where tenant_id = @t and item_id = @x and active", tenantId, ("x", id))
            ? Error("CONFIG.REFERENCED", "P2", "/id", ("reason", "ACTIVE_ITEM_COMPANY")) : null);

    // ---------------------------------------------------------------------------------------------------------
    // unit conversions
    // ---------------------------------------------------------------------------------------------------------
    private static readonly MasterSpec ItemConversionSpec = new("item_unit_conversion", "item_unit_conversion_id", "ITEM_UNIT_CONVERSION", "/id", "item-unit-conversions", Permission,
        InventoryFields.ItemUnitConversionCreate, InventoryFields.ItemUnitConversionUpdate, ["item_id", "unit_code", "factor_to_base"], [], async i =>
        {
            var errors = new List<CommandError>();
            if (!i.Creating) return errors;
            Guid item = Guid.Parse(i.Get("item_id")!);
            string? baseUnit = null;
            await using (var cmd = i.Tx.Command("select base_unit from core.item where tenant_id = @t and item_id = @x"))
            {
                cmd.Parameters.AddWithValue("t", i.TenantId); cmd.Parameters.AddWithValue("x", item);
                baseUnit = await cmd.ExecuteScalarAsync(i.Ct) as string;
            }
            if (baseUnit is null) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/item_id", ("item_id", item))); return errors; }
            string unit = i.Get("unit_code")!;
            if (!await UnitExists(i, unit)) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/unit_code", ("unit_code", unit)));
            else if (unit == baseUnit) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/unit_code", ("reason", "the base unit converts with factor 1")));
            await Duplicate(i, errors, "select 1 from core.item_unit_conversion where tenant_id = @t and item_id = @x and unit_code = @u and item_unit_conversion_id is distinct from @id::uuid", "/fields/unit_code", ("x", item), ("u", unit));
            return errors;
        });

    private static readonly MasterSpec UnitConversionSpec = new("unit_conversion", "unit_conversion_id", "UNIT_CONVERSION", "/id", "unit-conversions", Permission,
        InventoryFields.UnitConversionCreate, InventoryFields.UnitConversionUpdate, ["from_unit", "to_unit", "factor"], [], async i =>
        {
            var errors = new List<CommandError>();
            if (!i.Creating) return errors;
            string from = i.Get("from_unit")!, to = i.Get("to_unit")!;
            string? dimFrom = null, dimTo = null;
            foreach (var (unit, set) in new[] { (from, 0), (to, 1) })
            {
                await using var cmd = i.Tx.Command("select dimension from platform.ref_unit where unit_code = @u");
                cmd.Parameters.AddWithValue("u", unit);
                string? d = await cmd.ExecuteScalarAsync(i.Ct) as string;
                if (d is null) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", set == 0 ? "/fields/from_unit" : "/fields/to_unit", ("unit_code", unit)));
                if (set == 0) dimFrom = d; else dimTo = d;
            }
            if (errors.Count > 0) return errors;
            if (from == to) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/to_unit", ("reason", "a different unit")));
            else if (dimFrom != dimTo) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/to_unit", ("reason", "both units have the same dimension"), ("from", dimFrom), ("to", dimTo)));
            // the lookup takes the row or its inverse (3.4), so a pair exists once
            await Duplicate(i, errors, "select 1 from core.unit_conversion where tenant_id = @t and ((from_unit = @f and to_unit = @o) or (from_unit = @o and to_unit = @f)) and unit_conversion_id is distinct from @id::uuid",
                "/fields/to_unit", ("f", from), ("o", to));
            return errors;
        });

    // ---------------------------------------------------------------------------------------------------------
    // warehouses and locations
    // ---------------------------------------------------------------------------------------------------------
    private static readonly MasterSpec WarehouseSpec = new("warehouse", "warehouse_id", "WAREHOUSE", "/id", "warehouses", Permission,
        InventoryFields.WarehouseCreate, InventoryFields.WarehouseUpdate, ["company_id", "code", "name", "country_code"], [], async i =>
        {
            var errors = new List<CommandError>();
            Guid company = Guid.Parse(i.Get("company_id") ?? i.Current?["company_id"]!);
            if (i.Creating && !await ExistsAsync(i.Tx, "select 1 from core.company where tenant_id = @t and company_id = @x", i.TenantId, ("x", company)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/company_id", ("company_id", company)));
            if (i.Changed("code")) await Duplicate(i, errors, "select 1 from core.warehouse where tenant_id = @t and company_id = @c and code = @k and warehouse_id is distinct from @id::uuid", "/fields/code", ("c", company), ("k", i.Get("code")!));
            if (i.Changed("country_code") && !await ExistsAsync(i.Tx, "select 1 from platform.ref_country where country_code = @x", i.TenantId, ("x", i.Get("country_code")!)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/country_code", ("country_code", i.Get("country_code"))));
            await DimensionDefaults(i, errors, company);
            return errors;
        },
        async (tx, tenantId, id, _, ct) => await ExistsAsync(tx, "select 1 from core.warehouse_location where tenant_id = @t and warehouse_id = @x and active", tenantId, ("x", id))
            ? Error("CONFIG.REFERENCED", "P2", "/id", ("reason", "ACTIVE_LOCATION"))
            : await ExistsAsync(tx, "select 1 from core.item_warehouse where tenant_id = @t and warehouse_id = @x and active", tenantId, ("x", id))
                ? Error("CONFIG.REFERENCED", "P2", "/id", ("reason", "ACTIVE_ITEM_WAREHOUSE")) : null);

    private static readonly MasterSpec LocationSpec = new("warehouse_location", "location_id", "WAREHOUSE_LOCATION", "/id", "warehouse-locations", Permission,
        InventoryFields.LocationCreate, InventoryFields.LocationUpdate, ["company_id", "warehouse_id", "location_code", "name", "location_type"], [], async i =>
        {
            var errors = new List<CommandError>();
            Guid company = Guid.Parse(i.Get("company_id") ?? i.Current?["company_id"]!), warehouse = Guid.Parse(i.Get("warehouse_id") ?? i.Current?["warehouse_id"]!);
            if (i.Creating && !await ExistsAsync(i.Tx, "select 1 from core.warehouse where tenant_id = @t and company_id = @c and warehouse_id = @x", i.TenantId, ("c", company), ("x", warehouse)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/warehouse_id", ("warehouse_id", warehouse)));
            if (i.Changed("location_code")) await Duplicate(i, errors, "select 1 from core.warehouse_location where tenant_id = @t and warehouse_id = @w and location_code = @k and location_id is distinct from @id::uuid", "/fields/location_code", ("w", warehouse), ("k", i.Get("location_code")!));
            return errors;
        });

    // ---------------------------------------------------------------------------------------------------------
    // item companies and item warehouses
    // ---------------------------------------------------------------------------------------------------------
    private static readonly MasterSpec ItemCompanySpec = new("item_company", "item_company_id", "ITEM_COMPANY", "/id", "item-companies", Permission,
        InventoryFields.ItemCompanyCreate, InventoryFields.ItemCompanyUpdate, ["company_id", "item_id", "valuation_method"], [], async i =>
        {
            var errors = new List<CommandError>();
            Guid company = Guid.Parse(i.Get("company_id") ?? i.Current?["company_id"]!);
            if (i.Creating)
            {
                Guid item = Guid.Parse(i.Get("item_id")!);
                if (!await ExistsAsync(i.Tx, "select 1 from core.company where tenant_id = @t and company_id = @x", i.TenantId, ("x", company)))
                    errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/company_id", ("company_id", company)));
                if (!await ExistsAsync(i.Tx, "select 1 from core.item where tenant_id = @t and item_id = @x", i.TenantId, ("x", item)))
                    errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/item_id", ("item_id", item)));
                await Duplicate(i, errors, "select 1 from core.item_company where tenant_id = @t and company_id = @c and item_id = @x", "/fields/item_id", ("c", company), ("x", item));
            }
            await Ref(i, errors, "default_warehouse_id", "select 1 from core.warehouse where tenant_id = @t and company_id = @c and warehouse_id = @x and active", ("c", company));
            return errors;
        });

    private static readonly MasterSpec ItemWarehouseSpec = new("item_warehouse", "item_warehouse_id", "ITEM_WAREHOUSE", "/id", "item-warehouses", Permission,
        InventoryFields.ItemWarehouseCreate, InventoryFields.ItemWarehouseUpdate, ["company_id", "warehouse_id", "item_id"], [], async i =>
        {
            var errors = new List<CommandError>();
            if (!i.Creating) return errors;
            Guid company = Guid.Parse(i.Get("company_id")!), warehouse = Guid.Parse(i.Get("warehouse_id")!), item = Guid.Parse(i.Get("item_id")!);
            if (!await ExistsAsync(i.Tx, "select 1 from core.warehouse where tenant_id = @t and company_id = @c and warehouse_id = @x", i.TenantId, ("c", company), ("x", warehouse)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/warehouse_id", ("warehouse_id", warehouse)));
            if (!await ExistsAsync(i.Tx, "select 1 from core.item where tenant_id = @t and item_id = @x", i.TenantId, ("x", item)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/item_id", ("item_id", item)));
            await Duplicate(i, errors, "select 1 from core.item_warehouse where tenant_id = @t and warehouse_id = @w and item_id = @x", "/fields/item_id", ("w", warehouse), ("x", item));
            return errors;
        });

    private static readonly Dictionary<string, MasterSpec> Specs = new(StringComparer.Ordinal)
    {
        ["item-groups"] = GroupSpec, ["items"] = ItemSpec, ["item-unit-conversions"] = ItemConversionSpec, ["unit-conversions"] = UnitConversionSpec,
        ["warehouses"] = WarehouseSpec, ["warehouse-locations"] = LocationSpec, ["item-companies"] = ItemCompanySpec, ["item-warehouses"] = ItemWarehouseSpec
    };

    public Task<CommandResult<MasterResult>> CreateAsync(string resource, Guid t, CommandContext c, CreateInventoryMasterRequest r, CancellationToken ct = default) =>
        _generic.CreateAsync(t, c, Specs[resource], r.Fields, InventoryRules.Fingerprint(resource, r), ct);

    public Task<CommandResult<MasterResult>> UpdateAsync(string resource, Guid t, CommandContext c, UpdateInventoryMasterRequest r, CancellationToken ct = default) =>
        _generic.UpdateAsync(t, c, Specs[resource], r.Id, r.ExpectedVersion, r.Fields, InventoryRules.Fingerprint(resource, r), ct);

    public Task<CommandResult<MasterResult>> DeactivateAsync(string resource, Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, Specs[resource], r, true, ct);

    public Task<CommandResult<MasterResult>> ReactivateAsync(string resource, Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, Specs[resource], r, false, ct);
}
