using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;

namespace FinanceCore.Data.Tests;

/// <summary>Sales and purchase orders (11, 4, 6): placement, amendment, short-close, cancellation, line dates, the settings and the reads.</summary>
[Collection("db")]
public class OrderTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static Dictionary<string, object?> W(Dictionary<string, object?> d, string key, object? value) { var c = new Dictionary<string, object?>(d); c[key] = value; return c; }
    private static Dictionary<string, object?> Wo(Dictionary<string, object?> d, string key) => d.Where(x => x.Key != key).ToDictionary(x => x.Key, x => x.Value);
    private static CommandContext Sales(TestTenant t, string key) => t.Context(key, "sales.order");
    private static CommandContext Buy(TestTenant t, string key) => t.Context(key, "purchase.order");

    private sealed record S(TestTenant T, OrderCommands Orders, OrderReads Reads, Guid Customer, Guid Supplier, Guid Group, Guid Item, Guid Service, Guid Warehouse);

    private static async Task<Guid> MasterAsync(TestTenant t, string resource, string key, Dictionary<string, object?> fields)
    {
        var r = await new InventoryMasterCommands(t.App).CreateAsync(resource, t.TenantId, t.Context(key, "write.config"), new CreateInventoryMasterRequest(fields));
        Assert.True(r.Status == CommandStatus.Ok, resource + " " + Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<Guid> PartnerAsync(TestTenant t, string key, string code, bool customer)
    {
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context(key, "write.master"), new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", "ORGANISATION"), ("is_customer", customer), ("is_supplier", !customer)),
            D(("legal_name", code + " A/S"), ("country_code", "DK")), [new PartnerRelationInput(t.CompanyId, D(("is_customer", customer), ("is_supplier", !customer)))]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    /// <summary>A DKK company with the order series, the year 2026, a customer, a supplier, a goods item (pieces, a pallet of 24), a service item and a warehouse in DK.</summary>
    private static async Task<S> SetupAsync()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        await t.ExecuteAsync("""
            insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal)
              values (@t, @c, gen_random_uuid(), 'ORD', 'SO', true, '-', 5, false);
            insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by)
              select @t, @c, ty, (select series_id from core.number_series where tenant_id = @t and series_code = 'ORD'), date '2026-01-01', 'test'
              from unnest(array['SALES_ORDER', 'PURCHASE_ORDER']) ty;
            """);
        var year = await new FiscalYearOpener(t.App).OpenAsync(t.TenantId, t.Context("year", "period.admin"), new(t.CompanyId, 2026));
        Assert.True(year.Status == CommandStatus.Ok, Describe(year));
        Guid customer = await PartnerAsync(t, "cust", "ACME", true), supplier = await PartnerAsync(t, "supp", "BOLT", false);
        Guid group = await MasterAsync(t, "item-groups", "grp", D(("code", "FG"), ("name", "Goods")));
        Guid item = await MasterAsync(t, "items", "item", D(("item_no", "A-100"), ("name", "Widget"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", group)));
        Guid service = await MasterAsync(t, "items", "svc", D(("item_no", "S-1"), ("name", "Consulting"), ("item_type", "SERVICE"), ("base_unit", "HUR"), ("quantity_scale", 2), ("item_group_id", group)));
        await MasterAsync(t, "item-unit-conversions", "pallet", D(("item_id", item), ("unit_code", "PF"), ("factor_to_base", 24)));
        foreach (Guid i in new[] { item, service }) await MasterAsync(t, "item-companies", "ic-" + i, D(("company_id", t.CompanyId), ("item_id", i), ("valuation_method", "MOVING_AVERAGE")));
        Guid warehouse = await MasterAsync(t, "warehouses", "wh", D(("company_id", t.CompanyId), ("code", "MAIN"), ("name", "Main"), ("country_code", "DK")));
        return new S(t, new OrderCommands(t.App), new OrderReads(t.App), customer, supplier, group, item, service, warehouse);
    }

    private static Dictionary<string, object?> SalesHeader(S s) =>
        D(("company_id", s.T.CompanyId), ("order_date", "2026-03-10"), ("order_currency", "DKK"), ("customer_partner_id", s.Customer), ("customer_reference", "PO-77"));

    private static Dictionary<string, object?> StockLine(S s, decimal qty = 10, decimal price = 100, string unit = "C62", string @ref = "L1") =>
        D(("line_ref", @ref), ("item_id", s.Item), ("quantity", qty), ("unit_code", unit), ("unit_price", price), ("warehouse_id", s.Warehouse), ("destination_country", "SE"));

    private static async Task<OrderResult> PlaceSalesAsync(S s, string key, params Dictionary<string, object?>[] lines)
    {
        var r = await s.Orders.CreateAsync(OrderKind.Sales, s.T.TenantId, Sales(s.T, key), new(SalesHeader(s), lines.Length > 0 ? lines : [StockLine(s)]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static async Task<long> CountAsync(TestTenant t, string sql) => await t.ScalarAsync<long>(sql);

    [Fact]
    public async Task ASalesOrderIsPlacedWithNumberDerivedAmountsAndDefaults()
    {
        var s = await SetupAsync();
        var t = s.T;
        var order = await PlaceSalesAsync(s, "o1",
            StockLine(s, 3, 10.005m, "C62", "L1"), D(("line_ref", "L2"), ("item_id", s.Service), ("quantity", 1.5m), ("unit_code", "HUR"), ("unit_price", 800), ("discount_pct", 10), ("destination_country", "DK")));
        Assert.Equal(("SO-2026-00001", 1L, "OPEN"), (order.OrderNumber, order.RowVersion, order.LifecycleStatus));
        Assert.Equal([30.02m, 1080m], order.Lines.Select(l => l.NetAmount).ToArray());   // 3 x 10.005 = 30.015 rounds to 30.02, 1.5 x 800 x 0.9
        Assert.Equal(1110.02m, order.TotalNetAmount);
        Assert.Equal(["L1", "L2"], order.Lines.Select(l => l.LineRef!).ToArray());

        var detail = (await s.Reads.GetAsync(OrderKind.Sales, t.TenantId, t.Context("g", "read.orders"), new(order.OrderId, ["lines", "events"]))).Value!;
        var l1 = detail.Lines!.Value[0]; var l2 = detail.Lines.Value[1];
        Assert.Equal(("STOCK", "DELIVERED", "DK", "SE"), (l1.GetProperty("fulfilment_type").GetString(), l1.GetProperty("invoice_policy").GetString(), l1.GetProperty("origin_country").GetString(), l1.GetProperty("destination_country").GetString()));
        Assert.Equal(("SERVICE", "ORDERED", "DK"), (l2.GetProperty("fulfilment_type").GetString(), l2.GetProperty("invoice_policy").GetString(), l2.GetProperty("origin_country").GetString()));   // a service: the company country
        Assert.Equal(0m, l1.GetProperty("over_tolerance_pct").GetDecimal());
        Assert.Equal("PLACED", Assert.Single(detail.Events!.Value.EnumerateArray()).GetProperty("event_type").GetString());
        Assert.Equal(("OPEN", "PO-77"), (detail.Order.GetProperty("lifecycle_status").GetString(), detail.Order.GetProperty("customer_reference").GetString()));

        var second = await PlaceSalesAsync(s, "o2");
        Assert.Equal("SO-2026-00002", second.OrderNumber);
        Assert.Equal(1L, await CountAsync(t, "select count(*) from core.outbox_event where tenant_id = @t and event_type = 'order.created' and entity_key like '%' || '" + order.OrderId + "'"));
        var replay = await s.Orders.CreateAsync(OrderKind.Sales, t.TenantId, Sales(t, "o1"), new(SalesHeader(s), [StockLine(s, 3, 10.005m, "C62", "L1"), D(("line_ref", "L2"), ("item_id", s.Service), ("quantity", 1.5m), ("unit_code", "HUR"), ("unit_price", 800), ("discount_pct", 10), ("destination_country", "DK"))]));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(order.OrderNumber, replay.Value!.OrderNumber);
    }

    [Fact]
    public async Task UnitsScalesPartiesItemsAndWarehousesAreChecked()
    {
        var s = await SetupAsync();
        var t = s.T;
        Task<CommandResult<OrderResult>> Place(string key, Dictionary<string, object?> header, params Dictionary<string, object?>[] lines) =>
            s.Orders.CreateAsync(OrderKind.Sales, t.TenantId, Sales(t, key), new(header, lines.Length > 0 ? lines : [StockLine(s)]));

        var pallet = await PlaceSalesAsync(s, "p", StockLine(s, 2, 50, "PF"));
        Assert.Equal(48m, pallet.Lines.Single().BaseQuantity);   // a pallet of 24
        Assert.Equal(["INVENTORY.QUANTITY_SCALE_EXCEEDED"], Codes(await Place("q1", SalesHeader(s), StockLine(s, 0.5m))));
        Assert.Equal(["INVENTORY.UNIT_CONVERSION_MISSING"], Codes(await Place("q2", SalesHeader(s), StockLine(s, 1, 1, "BX"))));
        Assert.Equal(["ORDER.CONFIRMED_DATE_ON_CREATE"], Codes(await Place("q3", SalesHeader(s), W(StockLine(s), "confirmed_date", "2026-04-01"))));
        Assert.Equal(["LIMIT.ORDER_DROPSHIP_NOT_BUILT"], Codes(await Place("q4", SalesHeader(s), Wo(W(StockLine(s), "fulfilment_type", "DROPSHIP"), "warehouse_id"))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await Place("q5", SalesHeader(s), Wo(StockLine(s), "warehouse_id"))).Distinct().ToArray());   // a stock line needs a warehouse
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await Place("q6", SalesHeader(s), Wo(StockLine(s), "destination_country"))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await s.Orders.CreateAsync(OrderKind.Sales, t.TenantId, Sales(t, "q7"), new(SalesHeader(s), []))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Place("q8", SalesHeader(s), W(StockLine(s), "item_id", Guid.NewGuid()))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Place("q9", SalesHeader(s), W(StockLine(s, 1, 1), "discount_pct", 101))));
        Assert.Equal(["ORDER.DATE_INVALID"], Codes(await Place("q10", W(SalesHeader(s), "order_date", "1850-01-01"))));
        Assert.Equal(["CONFIG.CURRENCY_NOT_ENABLED"], Codes(await Place("q11", W(SalesHeader(s), "order_currency", "JPY"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Orders.CreateAsync(OrderKind.Sales, t.TenantId, t.Context("q12", "purchase.order"), new(SalesHeader(s), [StockLine(s)]))));

        // a supplier is no customer, a restricted partner is refused, and an item that the company has not activated is not sold
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Place("c1", W(SalesHeader(s), "customer_partner_id", s.Supplier))));
        await t.ExecuteAsync("update core.business_partner set restriction_status = 'RESTRICTED' where tenant_id = @t and partner_id = @p", ("p", s.Customer));
        Assert.Equal(["MASTER.PARTNER_RESTRICTED"], Codes(await Place("c2", SalesHeader(s))));
        await t.ExecuteAsync("update core.business_partner set restriction_status = 'NONE' where tenant_id = @t and partner_id = @p", ("p", s.Customer));
        Guid other = await MasterAsync(t, "items", "other", D(("item_no", "X-1"), ("name", "Unlisted"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", s.Group)));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Place("c3", SalesHeader(s), W(StockLine(s), "item_id", other))));
        Assert.Equal(CommandStatus.Ok, (await Place("c4", SalesHeader(s))).Status);
    }

    [Fact]
    public async Task AnOrderIsChangedAndEveryChangeIsLoggedWithItsEvent()
    {
        var s = await SetupAsync();
        var t = s.T;
        var order = await PlaceSalesAsync(s, "o", StockLine(s, 10, 100));
        var changed = await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales(t, "c1"),
            new(order.OrderId, 1, D(("header_text", "Rush"), ("customer_reference", "PO-78")), [new(1, D(("quantity", 20), ("discount_pct", 5), ("line_text", "Blue")))]));
        Assert.True(changed.Status == CommandStatus.Ok, Describe(changed));
        Assert.Equal((2L, 20m, 1900m), (changed.Value!.RowVersion, changed.Value.Lines[0].BaseQuantity, changed.Value.Lines[0].NetAmount));   // 20 x 100 x 0.95
        Assert.Equal(new[] { "header_text", "customer_reference", "lines/1/quantity", "lines/1/discount_pct", "lines/1/line_text", "lines/1/base_quantity", "lines/1/net_amount" }.Order().ToArray(), changed.Value.ChangedFields.Order().ToArray());
        Assert.Equal(7L, await CountAsync(t, $"select count(*) from core.order_change_log where tenant_id = @t and order_id = '{order.OrderId}'"));
        Assert.Equal(1900m, Convert.ToDecimal(await t.ScalarAsync<string>($"select new_value from core.order_change_log where tenant_id = @t and order_id = '{order.OrderId}' and field_name = 'net_amount'"), CultureInfo.InvariantCulture));
        Assert.Equal(["PLACED", "CHANGED"], (await s.Reads.GetAsync(OrderKind.Sales, t.TenantId, t.Context("g", "read.orders"), new(order.OrderId, ["events"]))).Value!.Events!.Value.EnumerateArray().Select(e => e.GetProperty("event_type").GetString()!).ToArray());

        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales(t, "c2"), new(order.OrderId, 1, D(("header_text", "Stale"))))));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales(t, "c3"), new(order.OrderId, 2, D(("header_text", "Rush"))))));
        Assert.Equal(["SCHEMA.UNKNOWN_FIELD"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales(t, "c4"), new(order.OrderId, 2, D(("order_currency", "EUR"))))));   // an L1 field
        Assert.Equal(["SCHEMA.UNKNOWN_FIELD"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales(t, "c5"), new(order.OrderId, 2, null, [new(1, D(("item_id", s.Service)))]))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales(t, "c6"), new(order.OrderId, 2, null, [new(9, D(("quantity", 1)))]))));
        Assert.Equal(["INVENTORY.QUANTITY_SCALE_EXCEEDED"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales(t, "c7"), new(order.OrderId, 2, null, [new(1, D(("quantity", 2.5m)))]))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales(t, "c8"), new(Guid.NewGuid(), 1, D(("header_text", "x"))))));
        Assert.True((await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales(t, "c1"), new(order.OrderId, 1, D(("header_text", "Rush"), ("customer_reference", "PO-78")), [new(1, D(("quantity", 20), ("discount_pct", 5), ("line_text", "Blue")))]))).IdempotentReplay);

        // the L1 columns and the status are guarded in the database too
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.sales_order set order_currency = 'EUR' where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.sales_order_line set item_id = gen_random_uuid() where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("delete from core.sales_order where tenant_id = @t"));
    }

    [Fact]
    public async Task LinesAreAddedClosedAndCancelledAndTheOrderFollows()
    {
        var s = await SetupAsync();
        var t = s.T;
        var order = await PlaceSalesAsync(s, "o", StockLine(s, 10, 100), D(("item_id", s.Service), ("quantity", 5), ("unit_code", "HUR"), ("unit_price", 400), ("destination_country", "DK")));
        var added = await s.Orders.AddLineAsync(OrderKind.Sales, t.TenantId, Sales(t, "a1"), new(order.OrderId, 1, StockLine(s, 4, 25, "C62", "L3")));
        Assert.True(added.Status == CommandStatus.Ok, Describe(added));
        Assert.Equal((2L, 3, 100m), (added.Value!.RowVersion, added.Value.Lines.Count, added.Value.Lines[2].NetAmount));
        Assert.Equal("L3", added.Value.Lines[2].LineRef);
        Assert.Equal(["ORDER.CONFIRMED_DATE_ON_CREATE"], Codes(await s.Orders.AddLineAsync(OrderKind.Sales, t.TenantId, Sales(t, "a2"), new(order.OrderId, 2, W(StockLine(s), "confirmed_date", "2026-05-01")))));

        // a short-closed line stays on the order, a cancelled one too, and neither can be changed again
        var closed = await s.Orders.CloseLineAsync(OrderKind.Sales, t.TenantId, Sales(t, "k1"), new(order.OrderId, 2, 1, "FULFILLED"));
        Assert.Equal(("CLOSED", "OPEN", 3L), (closed.Value!.Lines[0].LineStatus, closed.Value.LifecycleStatus, closed.Value.RowVersion));
        Assert.Equal(["ORDER.LINE_NOT_OPEN"], Codes(await s.Orders.CloseLineAsync(OrderKind.Sales, t.TenantId, Sales(t, "k2"), new(order.OrderId, 3, 1, "AGAIN"))));
        Assert.Equal(["ORDER.LINE_NOT_OPEN"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales(t, "k3"), new(order.OrderId, 3, null, [new(1, D(("quantity", 1)))]))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await s.Orders.CloseLineAsync(OrderKind.Sales, t.TenantId, Sales(t, "k4"), new(order.OrderId, 3, 0, ""))).Distinct().ToArray());
        var cancelledLine = await s.Orders.CancelAsync(OrderKind.Sales, t.TenantId, Sales(t, "x1"), new(order.OrderId, 3, "CUSTOMER", 2));
        Assert.Equal("CANCELLED", cancelledLine.Value!.Lines[1].LineStatus);
        // the last open line closes the order: CLOSED, since a line was closed
        var last = await s.Orders.CloseLineAsync(OrderKind.Sales, t.TenantId, Sales(t, "k5"), new(order.OrderId, 4, 3, "FULFILLED"));
        Assert.Equal("CLOSED", last.Value!.LifecycleStatus);
        Assert.Equal(["ORDER.ORDER_NOT_OPEN"], Codes(await s.Orders.AddLineAsync(OrderKind.Sales, t.TenantId, Sales(t, "a3"), new(order.OrderId, 5, StockLine(s)))));
        Assert.Equal(["PLACED", "LINE_ADDED", "LINE_CLOSED", "LINE_CANCELLED", "LINE_CLOSED", "CLOSED"],
            (await s.Reads.GetAsync(OrderKind.Sales, t.TenantId, t.Context("g", "read.orders"), new(order.OrderId, ["events"]))).Value!.Events!.Value.EnumerateArray().Select(e => e.GetProperty("event_type").GetString()!).ToArray());
        Assert.Equal(2L, await CountAsync(t, "select count(*) from core.outbox_event where tenant_id = @t and event_type = 'order.line_closed'"));

        // cancelling a whole order cancels every open line, and an order of cancelled lines is CANCELLED
        var second = await PlaceSalesAsync(s, "o2", StockLine(s), StockLine(s, 2, 10, "C62", "L2"));
        var cancelled = await s.Orders.CancelAsync(OrderKind.Sales, t.TenantId, Sales(t, "x2"), new(second.OrderId, 1, "CUSTOMER"));
        Assert.Equal("CANCELLED,CANCELLED,CANCELLED", cancelled.Value!.LifecycleStatus + "," + string.Join(",", cancelled.Value.Lines.Select(l => l.LineStatus)));
        Assert.Equal(1L, await CountAsync(t, "select count(*) from core.outbox_event where tenant_id = @t and event_type = 'order.cancelled'"));
        var third = await PlaceSalesAsync(s, "o3", StockLine(s));
        Assert.Equal("CANCELLED", (await s.Orders.CancelAsync(OrderKind.Sales, t.TenantId, Sales(t, "x3"), new(third.OrderId, 1, "CUSTOMER", 1))).Value!.LifecycleStatus);
        Assert.Equal(["ORDER.ORDER_NOT_OPEN"], Codes(await s.Orders.CancelAsync(OrderKind.Sales, t.TenantId, Sales(t, "x4"), new(third.OrderId, 2, "AGAIN"))));
    }

    [Fact]
    public async Task LineDatesAreChangedByPermissionAndNotOnAClosedLine()
    {
        var s = await SetupAsync();
        var t = s.T;
        var order = await PlaceSalesAsync(s, "o", StockLine(s), StockLine(s, 2, 10, "C62", "L2"));
        var dates = await s.Orders.ChangeLineDatesAsync(OrderKind.Sales, t.TenantId, Sales(t, "d1"), new(order.OrderId, 1, [new(1, D(("requested_date", "2026-04-01")))]));
        Assert.True(dates.Status == CommandStatus.Ok, Describe(dates));
        Assert.Equal(2L, dates.Value!.RowVersion);
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Orders.ChangeLineDatesAsync(OrderKind.Sales, t.TenantId, Sales(t, "d2"), new(order.OrderId, 2, [new(1, D(("confirmed_date", "2026-04-10")))]))));
        var promised = await s.Orders.ChangeLineDatesAsync(OrderKind.Sales, t.TenantId, t.Context("d3", "sales.order", "order.promise"), new(order.OrderId, 2, [new(1, D(("confirmed_date", "2026-04-10"), ("requested_date", "2026-04-02"))), new(2, D(("confirmed_date", "2026-04-12")))]));
        Assert.True(promised.Status == CommandStatus.Ok, Describe(promised));
        Assert.Equal(["ORDER.DATE_INVALID"], Codes(await s.Orders.ChangeLineDatesAsync(OrderKind.Sales, t.TenantId, t.Context("d4", "sales.order", "order.promise"), new(order.OrderId, 3, [new(1, D(("confirmed_date", "2300-01-01")))]))));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await s.Orders.ChangeLineDatesAsync(OrderKind.Sales, t.TenantId, Sales(t, "d5"), new(order.OrderId, 3, [new(1, D(("requested_date", "2026-04-02")))]))));
        Assert.Equal(CommandStatus.Ok, (await s.Orders.CloseLineAsync(OrderKind.Sales, t.TenantId, Sales(t, "k"), new(order.OrderId, 3, 1, "FULFILLED"))).Status);
        Assert.Equal(["ORDER.LINE_NOT_OPEN"], Codes(await s.Orders.ChangeLineDatesAsync(OrderKind.Sales, t.TenantId, Sales(t, "d6"), new(order.OrderId, 4, [new(1, D(("requested_date", "2026-05-01")))]))));
        Assert.Equal(4L, await CountAsync(t, "select count(*) from core.order_change_log where tenant_id = @t and field_name in ('requested_date', 'confirmed_date')"));   // requested once, then confirmed and requested on line 1 and confirmed on line 2
        Assert.Equal(2L, await CountAsync(t, "select count(*) from core.outbox_event where tenant_id = @t and event_type = 'order.date_changed'"));
        var line = (await s.Reads.GetAsync(OrderKind.Sales, t.TenantId, t.Context("g", "read.orders"), new(order.OrderId, ["lines"]))).Value!.Lines!.Value[0];
        Assert.Equal(("2026-04-02", "2026-04-10"), (line.GetProperty("requested_date").GetString(), line.GetProperty("confirmed_date").GetString()));
    }

    [Fact]
    public async Task APurchaseOrderFollowsTheSettingsOfItsCompany()
    {
        var s = await SetupAsync();
        var t = s.T;
        var header = D(("company_id", t.CompanyId), ("order_date", "2026-03-10"), ("order_currency", "DKK"), ("supplier_partner_id", s.Supplier), ("ship_from_country", "DE"));
        Dictionary<string, object?> Line(string matchMode) => D(("item_id", s.Item), ("quantity", 100), ("unit_code", "C62"), ("unit_price", 12.5m), ("warehouse_id", s.Warehouse), ("match_mode", matchMode));

        var order = await s.Orders.CreateAsync(OrderKind.Purchase, t.TenantId, Buy(t, "p1"), new(header, [D(("item_id", s.Item), ("quantity", 100), ("unit_code", "C62"), ("unit_price", 12.5m), ("warehouse_id", s.Warehouse))]));
        Assert.True(order.Status == CommandStatus.Ok, Describe(order));
        Assert.Equal(("SO-2026-00001", 1250m), (order.Value!.OrderNumber, order.Value.TotalNetAmount));
        var line = (await s.Reads.GetAsync(OrderKind.Purchase, t.TenantId, t.Context("g", "read.orders"), new(order.Value.OrderId, ["lines"]))).Value!.Lines!.Value[0];
        Assert.Equal(("THREE_WAY", "DE", "DK"), (line.GetProperty("match_mode").GetString(), line.GetProperty("origin_country").GetString(), line.GetProperty("destination_country").GetString()));   // from the supplier, to the warehouse

        // a stock item is three-way matched until the company allows an invoice before the receipt
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await s.Orders.CreateAsync(OrderKind.Purchase, t.TenantId, Buy(t, "p2"), new(header, [Line("TWO_WAY")]))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Orders.CreateAsync(OrderKind.Purchase, t.TenantId, Sales(t, "p3"), new(header, [Line("THREE_WAY")]))));
        var settings = new OrderSettingCommands(t.App);
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await settings.CreateVersionAsync(t.TenantId, Buy(t, "s0"), new(t.CompanyId, D(("effective_from", "2026-01-01"))))));
        var version = await settings.CreateVersionAsync(t.TenantId, t.Context("s1", "write.config"), new(t.CompanyId,
            D(("effective_from", "2026-03-01"), ("match_mode_default", "TWO_WAY"), ("allow_invoice_before_receipt", true), ("default_over_tolerance_pct", 10), ("default_under_tolerance_pct", 5))));
        Assert.True(version.Status == CommandStatus.Ok, Describe(version));
        Assert.True((await settings.CreateVersionAsync(t.TenantId, t.Context("s1", "write.config"), new(t.CompanyId,
            D(("effective_from", "2026-03-01"), ("match_mode_default", "TWO_WAY"), ("allow_invoice_before_receipt", true), ("default_over_tolerance_pct", 10), ("default_under_tolerance_pct", 5))))).IdempotentReplay);
        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], Codes(await settings.CreateVersionAsync(t.TenantId, t.Context("s2", "write.config"), new(t.CompanyId, D(("effective_from", "2026-02-01"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await settings.CreateVersionAsync(t.TenantId, t.Context("s3", "write.config"), new(Guid.NewGuid(), D(("effective_from", "2026-04-01"))))));

        var two = await s.Orders.CreateAsync(OrderKind.Purchase, t.TenantId, Buy(t, "p4"), new(header, [Line("TWO_WAY")]));
        Assert.True(two.Status == CommandStatus.Ok, Describe(two));
        var defaults = (await s.Reads.GetAsync(OrderKind.Purchase, t.TenantId, t.Context("g", "read.orders"), new(two.Value!.OrderId, ["lines"]))).Value!.Lines!.Value[0];
        Assert.Equal((10m, 5m), (defaults.GetProperty("over_tolerance_pct").GetDecimal(), defaults.GetProperty("under_tolerance_pct").GetDecimal()));
        // an order before the version keeps the earlier settings
        var early = await s.Orders.CreateAsync(OrderKind.Purchase, t.TenantId, Buy(t, "p5"), new(W(header, "order_date", "2026-02-10"), [Line("THREE_WAY")]));
        Assert.Equal(CommandStatus.Ok, early.Status);
        var early0 = (await s.Reads.GetAsync(OrderKind.Purchase, t.TenantId, t.Context("g", "read.orders"), new(early.Value!.OrderId, ["lines"]))).Value!.Lines!.Value[0];
        Assert.Equal(0m, early0.GetProperty("over_tolerance_pct").GetDecimal());

        var cancelled = await s.Orders.CancelAsync(OrderKind.Purchase, t.TenantId, Buy(t, "x"), new(early.Value.OrderId, 1, "SUPPLIER"));
        Assert.Equal("CANCELLED", cancelled.Value!.LifecycleStatus);
    }

    [Fact]
    public async Task OrdersAreListedWithFiltersAndPagesAndNeedReadOrders()
    {
        var s = await SetupAsync();
        var t = s.T;
        var a = await PlaceSalesAsync(s, "o1"); await PlaceSalesAsync(s, "o2"); var c = await PlaceSalesAsync(s, "o3");
        await s.Orders.CancelAsync(OrderKind.Sales, t.TenantId, Sales(t, "x"), new(c.OrderId, 1, "CUSTOMER"));
        CommandContext Read(string key) => t.Context(key, "read.orders");

        var p1 = await s.Reads.ListAsync(OrderKind.Sales, t.TenantId, Read("l1"), new(Limit: 2));
        Assert.Equal((2, true), (p1.Value!.Items.Count, p1.Value.Page.NextCursor is not null));
        var p2 = await s.Reads.ListAsync(OrderKind.Sales, t.TenantId, Read("l2"), new(Limit: 2, Cursor: p1.Value.Page.NextCursor));
        Assert.Equal((1, null), (p2.Value!.Items.Count, p2.Value.Page.NextCursor));
        Assert.Equal(2, (await s.Reads.ListAsync(OrderKind.Sales, t.TenantId, Read("l3"), new(LifecycleStatus: "OPEN"))).Value!.Items.Count);
        Assert.Equal(3, (await s.Reads.ListAsync(OrderKind.Sales, t.TenantId, Read("l4"), new(PartnerId: s.Customer, DateFrom: new DateOnly(2026, 3, 1), DateTo: new DateOnly(2026, 3, 31)))).Value!.Items.Count);
        Assert.Empty((await s.Reads.ListAsync(OrderKind.Sales, t.TenantId, Read("l5"), new(PartnerId: s.Supplier))).Value!.Items);
        Assert.Empty((await s.Reads.ListAsync(OrderKind.Purchase, t.TenantId, Read("l6"), new())).Value!.Items);
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await s.Reads.ListAsync(OrderKind.Sales, t.TenantId, Read("l7"), new(LifecycleStatus: "NOPE"))));
        Assert.Equal(["READ.CURSOR_INVALID"], Codes(await s.Reads.ListAsync(OrderKind.Sales, t.TenantId, Read("l8"), new(Cursor: "garbage"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Reads.ListAsync(OrderKind.Sales, t.TenantId, t.Context("l9", "sales.order"), new())));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await s.Reads.GetAsync(OrderKind.Sales, t.TenantId, Read("g1"), new(Guid.NewGuid()))));
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await s.Reads.GetAsync(OrderKind.Sales, t.TenantId, Read("g2"), new(a.OrderId, ["address"]))));
        var changes = (await s.Reads.GetAsync(OrderKind.Sales, t.TenantId, Read("g3"), new(c.OrderId, ["changes"]))).Value!.Changes!.Value;
        Assert.Equal("line_status", Assert.Single(changes.EnumerateArray()).GetProperty("field_name").GetString());
        var other = await TestTenant.CreateReadyAsync();
        Assert.Empty((await new OrderReads(other.App).ListAsync(OrderKind.Sales, other.TenantId, other.Context("o", "read.orders"), new())).Value!.Items);
    }
}
