using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;

namespace FinanceCore.Data.Tests;

/// <summary>ic-orders:create (11, 6.7, ADR-016): a sales order in the seller and a purchase order in the buyer, one id, one link row, one transaction.</summary>
[Collection("db")]
public class IcOrderTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static CommandContext Ctx(TestTenant t, string key) => t.Context(key, IcOrderRules.Permission);

    private sealed record Setup(TestTenant T, Guid Seller, Guid Buyer, Guid CustomerOfSeller, Guid SupplierOfBuyer, Guid Item, Guid SellerWarehouse, Guid BuyerWarehouse);

    private static async Task<Guid> MasterAsync(TestTenant t, string resource, string key, Dictionary<string, object?> fields)
    {
        var r = await new InventoryMasterCommands(t.App).CreateAsync(resource, t.TenantId, t.Context(key, "write.config"), new CreateInventoryMasterRequest(fields));
        Assert.True(r.Status == CommandStatus.Ok, resource + " " + Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<Guid> PartnerAsync(TestTenant t, string key, string code, Guid company, bool customer, Guid icCompany)
    {
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context(key, "write.master"), new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", "ORGANISATION"), ("is_customer", customer), ("is_supplier", !customer), ("ic_company_id", icCompany)),
            D(("legal_name", code + " A/S"), ("country_code", "DK")), [new PartnerRelationInput(company, D(("is_customer", customer), ("is_supplier", !customer)))]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<Setup> SetupAsync()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        Guid buyer = Guid.NewGuid();
        // two companies with an order series each; the second one cloned from the first
        await t.ExecuteAsync(
            "insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, reporting_currency, fiscal_calendar_id, status) " +
            "select tenant_id, @n, 'C2', 'Buyer A/S', country_code, local_currency, reporting_currency, fiscal_calendar_id, 'DRAFT' from core.company where tenant_id = @t and company_id = @c; " +
            "insert into core.ledger (tenant_id, company_id, ledger_id, ledger_code) values (@t, @n, gen_random_uuid(), 'L1'); " +
            "insert into core.group_membership (tenant_id, company_id, valid_from, group_id) select tenant_id, @n, date '2026-01-01', group_id from core.group_membership where tenant_id = @t and company_id = @c; " +
            "insert into core.fiscal_year (tenant_id, company_id, fiscal_year, calendar_id, start_date, end_date, opened_by) select @t, @n, 2026, fiscal_calendar_id, date '2026-01-01', date '2026-12-31', 'test' from core.company where tenant_id = @t and company_id = @n; " +
            "insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) values (@t, @c, gen_random_uuid(), 'ORD', 'SO', true, '-', 5, false), (@t, @n, gen_random_uuid(), 'ORD', 'SO', true, '-', 5, false); " +
            "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) " +
            "select @t, s.company_id, ty, s.series_id, date '2026-01-01', 'test' from core.number_series s, unnest(array['SALES_ORDER', 'PURCHASE_ORDER']) ty where s.tenant_id = @t and s.series_code = 'ORD'",
            ("n", buyer));
        var year = await new FiscalYearOpener(t.App).OpenAsync(t.TenantId, t.Context("year", "period.admin"), new(t.CompanyId, 2026));
        Assert.True(year.Status == CommandStatus.Ok, Describe(year));
        await t.ExecuteAsync("insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) select @t, company_id, series_id, 2026, 0 from core.number_series where tenant_id = @t and company_id = @n and series_code = 'ORD'", ("n", buyer));

        Guid customer = await PartnerAsync(t, "cust", "BUYERCO", t.CompanyId, true, buyer), supplier = await PartnerAsync(t, "supp", "SELLERCO", buyer, false, t.CompanyId);
        Guid group = await MasterAsync(t, "item-groups", "grp", D(("code", "FG"), ("name", "Goods")));
        Guid item = await MasterAsync(t, "items", "item", D(("item_no", "A-100"), ("name", "Widget"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", group)));
        foreach (Guid c in new[] { t.CompanyId, buyer }) await MasterAsync(t, "item-companies", "ic-" + c, D(("company_id", c), ("item_id", item), ("valuation_method", "MOVING_AVERAGE")));
        Guid wh1 = await MasterAsync(t, "warehouses", "wh1", D(("company_id", t.CompanyId), ("code", "MAIN"), ("name", "Main"), ("country_code", "DK")));
        Guid wh2 = await MasterAsync(t, "warehouses", "wh2", D(("company_id", buyer), ("code", "MAIN"), ("name", "Main"), ("country_code", "DK")));
        return new Setup(t, t.CompanyId, buyer, customer, supplier, item, wh1, wh2);
    }

    private static CreateIcOrderRequest Request(Setup s, decimal quantity = 10m, decimal price = 100m) =>
        new(s.Seller, s.Buyer, new DateOnly(2026, 3, 10), "DKK", s.CustomerOfSeller, s.SupplierOfBuyer,
            [D(("line_ref", "L1"), ("item_id", s.Item), ("quantity", quantity), ("unit_code", "C62"), ("unit_price", price), ("warehouse_id", s.SellerWarehouse), ("buyer_warehouse_id", s.BuyerWarehouse), ("destination_country", "DK"))],
            DeliveryDate: new DateOnly(2026, 3, 20), HeaderText: "Recharge of widgets", SourceDocumentRef: "IC-1", ShipFromCountry: "DK");

    [Fact]
    public async Task APairIsPlacedInOneTransactionWithOneIdAndOneLink()
    {
        var s = await SetupAsync();
        var cmds = new IcOrderCommands(s.T.App);
        var r = await cmds.CreateAsync(s.T.TenantId, Ctx(s.T, "ic1"), Request(s));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var v = r.Value!;
        Assert.Equal(1000m, v.SalesOrder.TotalNetAmount);
        Assert.Equal(1000m, v.PurchaseOrder.TotalNetAmount);
        Assert.Equal("SO-2026-00001", v.SalesOrder.OrderNumber);

        // both orders carry the id, the link has both, each side has its own company and warehouse, the keys are derived
        Assert.Equal(1L, await s.T.ScalarAsync<long>("select count(*) from core.sales_order where tenant_id = @t and ic_transaction_id = @i and company_id = @s and idempotency_key = 'ic1#1'", ("i", v.IcTransactionId), ("s", s.Seller)));
        Assert.Equal(1L, await s.T.ScalarAsync<long>("select count(*) from core.purchase_order where tenant_id = @t and ic_transaction_id = @i and company_id = @b and idempotency_key = 'ic1#2'", ("i", v.IcTransactionId), ("b", s.Buyer)));
        Assert.Equal(1L, await s.T.ScalarAsync<long>("select count(*) from core.ic_order_link where tenant_id = @t and ic_transaction_id = @i and sales_order_id = @so and purchase_order_id = @po",
            ("i", v.IcTransactionId), ("so", v.SalesOrder.OrderId), ("po", v.PurchaseOrder.OrderId)));
        Assert.Equal(s.SellerWarehouse.ToString(), await s.T.ScalarAsync<string>("select warehouse_id::text from core.sales_order_line where tenant_id = @t and sales_order_id = @o", ("o", v.SalesOrder.OrderId)));
        Assert.Equal(s.BuyerWarehouse.ToString(), await s.T.ScalarAsync<string>("select warehouse_id::text from core.purchase_order_line where tenant_id = @t and purchase_order_id = @o", ("o", v.PurchaseOrder.OrderId)));

        // an order belongs to one pair, the link cannot change
        await Assert.ThrowsAnyAsync<Exception>(() => s.T.ExecuteAsync("update core.ic_order_link set created_by = 'x' where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<Exception>(() => s.T.ExecuteAsync("delete from core.ic_order_link where tenant_id = @t"));

        // replay, changed request
        var replay = await cmds.CreateAsync(s.T.TenantId, Ctx(s.T, "ic1"), Request(s));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(v.SalesOrder.OrderId, replay.Value!.SalesOrder.OrderId);
        Assert.Equal(v.PurchaseOrder.OrderId, replay.Value.PurchaseOrder.OrderId);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await cmds.CreateAsync(s.T.TenantId, Ctx(s.T, "ic1"), Request(s, quantity: 5m))));
        Assert.Equal(1L, await s.T.ScalarAsync<long>("select count(*) from core.ic_order_link where tenant_id = @t"));
    }

    [Fact]
    public async Task ThePartnersMustBeTheIntercompanyPartnersAndASideThatFailsRollsBackTheOther()
    {
        var s = await SetupAsync();
        var cmds = new IcOrderCommands(s.T.App);
        Guid other = await PartnerAsync(s.T, "other", "OTHERCO", s.Seller, true, s.Seller);   // a customer of the seller that is not the buyer

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await cmds.CreateAsync(s.T.TenantId, s.T.Context("a", "sales.order"), Request(s))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await cmds.CreateAsync(s.T.TenantId, Ctx(s.T, "b"), Request(s) with { PurchaseCompanyId = s.Seller })));
        var wrongCustomer = await cmds.CreateAsync(s.T.TenantId, Ctx(s.T, "c"), Request(s) with { CustomerPartnerId = other });
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(wrongCustomer));
        Assert.Equal("customer_partner_id", wrongCustomer.Errors[0].Path);
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await cmds.CreateAsync(s.T.TenantId, Ctx(s.T, "d"), Request(s) with { SupplierPartnerId = s.CustomerOfSeller })));

        // a line that the buyer cannot take: its item has no item company there. Nothing of the seller's order is kept
        Guid group = await MasterAsync(s.T, "item-groups", "grp2", D(("code", "XG"), ("name", "Other")));
        Guid sellerOnly = await MasterAsync(s.T, "items", "item2", D(("item_no", "B-200"), ("name", "Gadget"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", group)));
        await MasterAsync(s.T, "item-companies", "ic2", D(("company_id", s.Seller), ("item_id", sellerOnly), ("valuation_method", "MOVING_AVERAGE")));
        var broken = Request(s) with { Lines = [D(("line_ref", "L1"), ("item_id", sellerOnly), ("quantity", 1), ("unit_code", "C62"), ("unit_price", 10), ("warehouse_id", s.SellerWarehouse), ("buyer_warehouse_id", s.BuyerWarehouse), ("destination_country", "DK"))] };
        var r = await cmds.CreateAsync(s.T.TenantId, Ctx(s.T, "e"), broken);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.StartsWith("purchase_order", r.Errors[0].Path);
        Assert.Equal(0L, await s.T.ScalarAsync<long>("select count(*) from core.sales_order where tenant_id = @t"));
        Assert.Equal(0L, await s.T.ScalarAsync<long>("select count(*) from core.ic_order_link where tenant_id = @t"));
        Assert.Equal(0L, await s.T.ScalarAsync<long>("select count(*) from core.command_log where tenant_id = @t and command_name = 'ic-orders:create'"));
    }
}
