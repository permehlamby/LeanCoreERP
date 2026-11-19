using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;

namespace FinanceCore.Data.Tests;

/// <summary>The master data of items and warehouses (10, 3): units, item groups, items, conversions, warehouses, locations, item companies and item warehouses.</summary>
[Collection("db")]
public class InventoryMasterTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);
    private static CommandContext Write(TestTenant t, string key) => t.Context(key, "write.config");
    private static CommandContext Read(TestTenant t, string key = "rd") => t.Context(key, "read.config");
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static MasterReadSpec Spec(string resource) => MasterReads.InventorySpecs.Single(s => s.Resource == resource);

    private sealed record Ctx(TestTenant T, InventoryMasterCommands Cmds, MasterReads Reads);

    private static async Task<Ctx> NewAsync() { var t = await TestTenant.CreateReadyAsync(); return new Ctx(t, new InventoryMasterCommands(t.App), new MasterReads(t.App)); }

    private static async Task<Guid> CreateAsync(Ctx c, string resource, string key, Dictionary<string, object?> fields)
    {
        var r = await c.Cmds.CreateAsync(resource, c.T.TenantId, Write(c.T, key), new CreateInventoryMasterRequest(fields));
        Assert.True(r.Status == CommandStatus.Ok, resource + " " + Describe(r));
        return r.Value!.EntityId;
    }

    private static Task<Guid> GroupAsync(Ctx c, string key = "g", string code = "FG") => CreateAsync(c, "item-groups", key, D(("code", code), ("name", "Finished goods")));

    private static Task<Guid> ItemAsync(Ctx c, Guid group, string key = "i", string no = "A-100") =>
        CreateAsync(c, "items", key, D(("item_no", no), ("name", "Widget"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", group)));

    private static async Task<JsonElement> GetAsync(Ctx c, string resource, Guid id)
    {
        var r = await c.Reads.GetAsync(c.T.TenantId, Read(c.T), Spec(resource), new(id));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.Row;
    }

    [Fact]
    public async Task TheUnitsAreTheGlobalCodes()
    {
        var c = await NewAsync();
        var all = await c.Reads.ListUnitsAsync(c.T.TenantId, Read(c.T), new());
        Assert.True(all.Status == CommandStatus.Ok, Describe(all));
        Assert.True(all.Value!.Items.Count >= 20);
        var mass = await c.Reads.ListUnitsAsync(c.T.TenantId, Read(c.T), new(new Dictionary<string, string> { ["dimension"] = "MASS" }));
        Assert.Equal(["GRM", "KGM", "LBR", "TNE"], mass.Value!.Items.Select(u => u.GetProperty("unit_code").GetString()!).ToArray());
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await c.Reads.ListUnitsAsync(c.T.TenantId, c.T.Context("x", "read.ledger"), new())));
    }

    [Fact]
    public async Task AnItemGroupAndAnItemAreCreatedChangedAndFoundByTheirReads()
    {
        var c = await NewAsync();
        var t = c.T;
        Guid group = await GroupAsync(c);
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await c.Cmds.CreateAsync("item-groups", t.TenantId, Write(t, "g2"), new(D(("code", "FG"), ("name", "Again"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await c.Cmds.CreateAsync("item-groups", t.TenantId, Write(t, "g3"), new(D(("code", "X"), ("name", "X"), ("default_cost_center_id", Guid.NewGuid()))))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await c.Cmds.CreateAsync("item-groups", t.TenantId, t.Context("g4", "read.config"), new(D(("code", "Y"), ("name", "Y"))))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await c.Cmds.CreateAsync("item-groups", t.TenantId, Write(t, "g5"), new(D(("code", "Z"))))));
        Assert.Equal(["SCHEMA.UNKNOWN_FIELD"], Codes(await c.Cmds.CreateAsync("item-groups", t.TenantId, Write(t, "g6"), new(D(("code", "Z"), ("name", "Z"), ("colour", "red"))))));

        Guid item = await ItemAsync(c, group);
        var row = await GetAsync(c, "items", item);
        Assert.Equal(("A-100", "GOODS", "NONE", "ACTIVE", 1), (row.GetProperty("item_no").GetString(), row.GetProperty("item_type").GetString(), row.GetProperty("tracking_mode").GetString(),
            row.GetProperty("status").GetString(), row.GetProperty("row_version").GetInt32()));
        Assert.False(row.TryGetProperty("tenant_id", out _));
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await c.Cmds.CreateAsync("items", t.TenantId, Write(t, "i2"), new(D(("item_no", "A-100"), ("name", "Dup"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", group))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await c.Cmds.CreateAsync("items", t.TenantId, Write(t, "i3"), new(D(("item_no", "B"), ("name", "B"), ("item_type", "GOODS"), ("base_unit", "ZZZ"), ("quantity_scale", 0), ("item_group_id", group))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await c.Cmds.CreateAsync("items", t.TenantId, Write(t, "i4"), new(D(("item_no", "C"), ("name", "C"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", Guid.NewGuid()))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await c.Cmds.CreateAsync("items", t.TenantId, Write(t, "i5"), new(D(("item_no", "D"), ("name", "D"), ("item_type", "SERVICE"), ("base_unit", "HUR"), ("quantity_scale", 0), ("item_group_id", group), ("tracking_mode", "LOT"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await c.Cmds.CreateAsync("items", t.TenantId, Write(t, "i6"), new(D(("item_no", "E"), ("name", "E"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", group), ("expiry_tracked", true))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await c.Cmds.CreateAsync("items", t.TenantId, Write(t, "i7"), new(D(("item_no", "F"), ("name", "F"), ("item_type", "GOODS"), ("base_unit", "KGM"), ("quantity_scale", 7), ("item_group_id", group))))));
        Guid lot = await CreateAsync(c, "items", "i8", D(("item_no", "LOT-1"), ("name", "Milk"), ("item_type", "GOODS"), ("base_unit", "LTR"), ("quantity_scale", 3), ("item_group_id", group), ("tracking_mode", "LOT"), ("expiry_tracked", true), ("weight_kg", 1.03m)));
        Assert.Equal(1.03m, (await GetAsync(c, "items", lot)).GetProperty("weight_kg").GetDecimal());

        var updated = await c.Cmds.UpdateAsync("items", t.TenantId, Write(t, "u1"), new(item, 1, D(("name", "Widget XL"), ("status", "BLOCKED"))));
        Assert.True(updated.Status == CommandStatus.Ok, Describe(updated));
        Assert.Equal((2L, "name,status"), (updated.Value!.RowVersion, string.Join(",", updated.Value.ChangedFields.Order())));
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await c.Cmds.UpdateAsync("items", t.TenantId, Write(t, "u2"), new(item, 1, D(("name", "Stale"))))));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await c.Cmds.UpdateAsync("items", t.TenantId, Write(t, "u3"), new(item, 2, D(("name", "Widget XL"))))));
        // the change of an item is a CREATE row and an UPDATE row per changed field in the master change log
        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and entity_type = 'ITEM' and entity_id = @i", ("i", item.ToString("D"))));

        // an item group with an active item cannot be deactivated, and the list hides what is inactive
        var blocked = await c.Cmds.DeactivateAsync("item-groups", t.TenantId, Write(t, "d1"), new(group, 1, "CLOSED"));
        Assert.Equal(["CONFIG.REFERENCED"], Codes(blocked));
        foreach (Guid i in new[] { item, lot })
            Assert.Equal(CommandStatus.Ok, (await c.Cmds.DeactivateAsync("items", t.TenantId, Write(t, "d-" + i), new(i, i == item ? 2 : 1, "OBSOLETE"))).Status);
        Assert.Equal(CommandStatus.Ok, (await c.Cmds.DeactivateAsync("item-groups", t.TenantId, Write(t, "d2"), new(group, 1, "CLOSED"))).Status);
        Assert.Empty((await c.Reads.ListAsync(t.TenantId, Read(t), Spec("items"), new())).Value!.Items);
        Assert.Equal(2, (await c.Reads.ListAsync(t.TenantId, Read(t), Spec("items"), new(IncludeInactive: true))).Value!.Items.Count);
        Assert.Equal(CommandStatus.Ok, (await c.Cmds.ReactivateAsync("items", t.TenantId, Write(t, "r1"), new(item, 3, "BACK"))).Status);
        var filtered = await c.Reads.ListAsync(t.TenantId, Read(t), Spec("items"), new(new Dictionary<string, string> { ["item_type"] = "GOODS", ["base_unit"] = "C62" }));
        Assert.Equal(["A-100"], filtered.Value!.Items.Select(i => i.GetProperty("item_no").GetString()!).ToArray());
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await c.Reads.ListAsync(t.TenantId, Read(t), Spec("items"), new(new Dictionary<string, string> { ["weight_kg"] = "1" }))));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await c.Reads.GetAsync(t.TenantId, Read(t), Spec("items"), new(Guid.NewGuid()))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await c.Reads.GetAsync(t.TenantId, t.Context("x", "read.ledger"), Spec("items"), new(item))));
    }

    [Fact]
    public async Task ListsPageWithACursor()
    {
        var c = await NewAsync();
        Guid group = await GroupAsync(c);
        foreach (string no in new[] { "N1", "N2", "N3" }) await ItemAsync(c, group, "k-" + no, no);
        var p1 = await c.Reads.ListAsync(c.T.TenantId, Read(c.T), Spec("items"), new(Limit: 2));
        Assert.Equal((2, true), (p1.Value!.Items.Count, p1.Value.Page.NextCursor is not null));
        var p2 = await c.Reads.ListAsync(c.T.TenantId, Read(c.T), Spec("items"), new(Limit: 2, Cursor: p1.Value.Page.NextCursor));
        Assert.Equal(("N3", null), (p2.Value!.Items.Single().GetProperty("item_no").GetString(), p2.Value.Page.NextCursor));
        Assert.Equal(["READ.CURSOR_INVALID"], Codes(await c.Reads.ListAsync(c.T.TenantId, Read(c.T), Spec("items"), new(Cursor: "garbage"))));
        Assert.Equal(["READ.LIMIT_EXCEEDED"], Codes(await c.Reads.ListAsync(c.T.TenantId, Read(c.T), Spec("items"), new(Limit: 501))));
    }

    [Fact]
    public async Task ConversionsNeedAKnownUnitOfTheSameDimensionAndExistOnce()
    {
        var c = await NewAsync();
        var t = c.T;
        Guid item = await ItemAsync(c, await GroupAsync(c));
        Guid pallet = await CreateAsync(c, "item-unit-conversions", "c1", D(("item_id", item), ("unit_code", "PF"), ("factor_to_base", 24)));
        Assert.Equal(24m, (await GetAsync(c, "item-unit-conversions", pallet)).GetProperty("factor_to_base").GetDecimal());
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await c.Cmds.CreateAsync("item-unit-conversions", t.TenantId, Write(t, "c2"), new(D(("item_id", item), ("unit_code", "PF"), ("factor_to_base", 12))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await c.Cmds.CreateAsync("item-unit-conversions", t.TenantId, Write(t, "c3"), new(D(("item_id", item), ("unit_code", "C62"), ("factor_to_base", 1))))));   // the base unit
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await c.Cmds.CreateAsync("item-unit-conversions", t.TenantId, Write(t, "c4"), new(D(("item_id", Guid.NewGuid()), ("unit_code", "PF"), ("factor_to_base", 1))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await c.Cmds.CreateAsync("item-unit-conversions", t.TenantId, Write(t, "c5"), new(D(("item_id", item), ("unit_code", "BX"), ("factor_to_base", 0))))));
        Assert.Equal(CommandStatus.Ok, (await c.Cmds.UpdateAsync("item-unit-conversions", t.TenantId, Write(t, "c6"), new(pallet, 1, D(("factor_to_base", 20.5m))))).Status);

        Guid gram = await CreateAsync(c, "unit-conversions", "u1", D(("from_unit", "KGM"), ("to_unit", "GRM"), ("factor", 1000)));
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await c.Cmds.CreateAsync("unit-conversions", t.TenantId, Write(t, "u2"), new(D(("from_unit", "GRM"), ("to_unit", "KGM"), ("factor", 0.001m))))));   // the inverse
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await c.Cmds.CreateAsync("unit-conversions", t.TenantId, Write(t, "u3"), new(D(("from_unit", "KGM"), ("to_unit", "LTR"), ("factor", 1))))));   // another dimension
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await c.Cmds.CreateAsync("unit-conversions", t.TenantId, Write(t, "u4"), new(D(("from_unit", "KGM"), ("to_unit", "KGM"), ("factor", 1))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await c.Cmds.CreateAsync("unit-conversions", t.TenantId, Write(t, "u5"), new(D(("from_unit", "KGM"), ("to_unit", "ZZZ"), ("factor", 1))))));
        Assert.Equal(1000m, (await GetAsync(c, "unit-conversions", gram)).GetProperty("factor").GetDecimal());
    }

    [Fact]
    public async Task WarehousesLocationsItemCompaniesAndItemWarehousesHangTogether()
    {
        var c = await NewAsync();
        var t = c.T;
        Guid item = await ItemAsync(c, await GroupAsync(c));
        Guid wh = await CreateAsync(c, "warehouses", "w1", D(("company_id", t.CompanyId), ("code", "MAIN"), ("name", "Main warehouse"), ("country_code", "DK"), ("address", "Havnegade 1, 1058 København K")));
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await c.Cmds.CreateAsync("warehouses", t.TenantId, Write(t, "w2"), new(D(("company_id", t.CompanyId), ("code", "MAIN"), ("name", "Again"), ("country_code", "DK"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await c.Cmds.CreateAsync("warehouses", t.TenantId, Write(t, "w3"), new(D(("company_id", Guid.NewGuid()), ("code", "X"), ("name", "X"), ("country_code", "DK"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await c.Cmds.CreateAsync("warehouses", t.TenantId, Write(t, "w4"), new(D(("company_id", t.CompanyId), ("code", "Y"), ("name", "Y"), ("country_code", "ZZ"))))));

        Guid transit = await CreateAsync(c, "warehouse-locations", "l1", D(("company_id", t.CompanyId), ("warehouse_id", wh), ("location_code", "TRN"), ("name", "In transit"), ("location_type", "TRANSIT")));
        Assert.Equal("TRANSIT", (await GetAsync(c, "warehouse-locations", transit)).GetProperty("location_type").GetString());
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await c.Cmds.CreateAsync("warehouse-locations", t.TenantId, Write(t, "l2"), new(D(("company_id", t.CompanyId), ("warehouse_id", wh), ("location_code", "TRN"), ("name", "Dup"), ("location_type", "STANDARD"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await c.Cmds.CreateAsync("warehouse-locations", t.TenantId, Write(t, "l3"), new(D(("company_id", t.CompanyId), ("warehouse_id", Guid.NewGuid()), ("location_code", "Q"), ("name", "Q"), ("location_type", "QUARANTINE"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await c.Cmds.CreateAsync("warehouse-locations", t.TenantId, Write(t, "l4"), new(D(("company_id", t.CompanyId), ("warehouse_id", wh), ("location_code", "Q"), ("name", "Q"), ("location_type", "SHELF"))))));

        Guid ic = await CreateAsync(c, "item-companies", "ic1", D(("company_id", t.CompanyId), ("item_id", item), ("valuation_method", "FIFO"), ("default_warehouse_id", wh)));
        Assert.Equal("FIFO", (await GetAsync(c, "item-companies", ic)).GetProperty("valuation_method").GetString());
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await c.Cmds.CreateAsync("item-companies", t.TenantId, Write(t, "ic2"), new(D(("company_id", t.CompanyId), ("item_id", item), ("valuation_method", "MOVING_AVERAGE"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await c.Cmds.CreateAsync("item-companies", t.TenantId, Write(t, "ic3"), new(D(("company_id", t.CompanyId), ("item_id", Guid.NewGuid()), ("valuation_method", "FIFO"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await c.Cmds.CreateAsync("item-companies", t.TenantId, Write(t, "ic4"), new(D(("company_id", t.CompanyId), ("item_id", item), ("valuation_method", "LIFO"))))));
        Assert.Equal(CommandStatus.Ok, (await c.Cmds.UpdateAsync("item-companies", t.TenantId, Write(t, "ic5"), new(ic, 1, D(("valuation_method", "MOVING_AVERAGE"), ("default_warehouse_id", null))))).Status);

        Guid iw = await CreateAsync(c, "item-warehouses", "iw1", D(("company_id", t.CompanyId), ("warehouse_id", wh), ("item_id", item), ("allow_negative_stock", false), ("min_quantity", 5), ("reorder_quantity", 20)));
        Assert.Equal(5m, (await GetAsync(c, "item-warehouses", iw)).GetProperty("min_quantity").GetDecimal());
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await c.Cmds.CreateAsync("item-warehouses", t.TenantId, Write(t, "iw2"), new(D(("company_id", t.CompanyId), ("warehouse_id", wh), ("item_id", item), ("allow_negative_stock", true))))));
        Assert.Equal(CommandStatus.Ok, (await c.Cmds.UpdateAsync("item-warehouses", t.TenantId, Write(t, "iw3"), new(iw, 1, D(("allow_negative_stock", true))))).Status);

        // a warehouse with an active location or item warehouse is not deactivated, and an item with an active item company is not
        Assert.Equal(["CONFIG.REFERENCED"], Codes(await c.Cmds.DeactivateAsync("warehouses", t.TenantId, Write(t, "x1"), new(wh, 1, "CLOSED"))));
        Assert.Equal(["CONFIG.REFERENCED"], Codes(await c.Cmds.DeactivateAsync("items", t.TenantId, Write(t, "x2"), new(item, 1, "OBSOLETE"))));
        Assert.Equal(CommandStatus.Ok, (await c.Cmds.DeactivateAsync("item-companies", t.TenantId, Write(t, "x3"), new(ic, 2, "NOT_STOCKED"))).Status);
        Assert.Equal(CommandStatus.Ok, (await c.Cmds.DeactivateAsync("items", t.TenantId, Write(t, "x4"), new(item, 1, "OBSOLETE"))).Status);

        var byCompany = await c.Reads.ListAsync(t.TenantId, Read(t), Spec("warehouses"), new(new Dictionary<string, string> { ["company_id"] = t.CompanyId.ToString("D") }));
        Assert.Equal(["MAIN"], byCompany.Value!.Items.Select(w => w.GetProperty("code").GetString()!).ToArray());
        // the tables have no hard delete, and another tenant's rows are not seen
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("delete from core.warehouse where tenant_id = @t"));
        var other = await TestTenant.CreateReadyAsync();
        Assert.Empty((await new MasterReads(other.App).ListAsync(other.TenantId, Read(other), Spec("warehouses"), new(IncludeInactive: true))).Value!.Items);
    }
}
