using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;

namespace FinanceCore.Data.Tests;

/// <summary>Deliveries and receipts against orders (11, 5.1, 5.2, 6.3, 7.1) on the stock ledger, and the order rules that need progress (11, 4.4).</summary>
[Collection("db")]
public class FulfilmentTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static Dictionary<string, object?> W(Dictionary<string, object?> d, string key, object? value) { var c = new Dictionary<string, object?>(d); c[key] = value; return c; }

    private sealed record S(TestTenant T, FulfilmentCommands Cmds, OrderCommands Orders, OrderReads Reads, Guid Customer, Guid Supplier, Guid Item, Guid Service, Guid Warehouse, Guid Inventory, Guid Cogs, Guid GrIr);

    private static async Task<Guid> AccountAsync(TestTenant t, string no, string type, string mode)
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values (@t, @c, @a, @no, @no, @ty, 'FORBIDDEN', @m)",
            ("a", id), ("no", no), ("ty", type), ("m", mode));
        return id;
    }

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

    /// <summary>An ACTIVE DKK company with the series of orders, fulfilment, stock documents and their journals, the stock accounts with determinations, a customer, a supplier, a goods item, a service and a warehouse.</summary>
    private static async Task<S> SetupAsync()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid inventory = await AccountAsync(t, "STK-INV", "ASSET", "NONE"), cogs = await AccountAsync(t, "STK-COGS", "EXPENSE", "NONE"), grir = await AccountAsync(t, "STK-GRIR", "LIABILITY", "GL_OPEN_ITEM");
        await t.ExecuteAsync("""
            insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) values
              (@t, @c, gen_random_uuid(), 'STK', 'SD', true, '-', 5, false), (@t, @c, gen_random_uuid(), 'STJ', 'SJ', true, '-', 5, false),
              (@t, @c, gen_random_uuid(), 'ORD', 'SO', true, '-', 5, false), (@t, @c, gen_random_uuid(), 'FUL', 'FU', true, '-', 5, false);
            insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by)
              select @t, @c, m.ty, (select series_id from core.number_series where tenant_id = @t and series_code = m.sc), date '2026-01-01', 'test' from (values
                ('STOCK_DOC_RECEIPT', 'STK'), ('STOCK_DOC_ISSUE', 'STK'), ('STOCK_DOC_ADJUSTMENT', 'STK'), ('STOCK_RECEIPT', 'STJ'), ('STOCK_ISSUE', 'STJ'), ('STOCK_ADJUSTMENT', 'STJ'),
                ('SALES_ORDER', 'ORD'), ('PURCHASE_ORDER', 'ORD'), ('SALES_DELIVERY', 'FUL'), ('PURCHASE_RECEIPT', 'FUL')) m(ty, sc);
            insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number)
              select @t, @c, series_id, 2026, 0 from core.number_series where tenant_id = @t and series_code in ('STK', 'STJ', 'ORD', 'FUL');
            """);
        var det = new AccountDeterminationCommands(t.App);
        foreach (var (role, account) in new[] { ("INVENTORY", inventory), ("COGS", cogs), ("GR_IR", grir) })
            Assert.Equal(CommandStatus.Ok, (await det.CreateAsync(t.TenantId, t.Context("det-" + role, "write.config"), new(t.CompanyId, D(("role_code", role), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", account))))).Status);
        Guid customer = await PartnerAsync(t, "cust", "ACME", true), supplier = await PartnerAsync(t, "supp", "BOLT", false);
        Guid group = await MasterAsync(t, "item-groups", "grp", D(("code", "FG"), ("name", "Goods")));
        Guid item = await MasterAsync(t, "items", "item", D(("item_no", "A-100"), ("name", "Widget"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", group)));
        Guid service = await MasterAsync(t, "items", "svc", D(("item_no", "S-1"), ("name", "Consulting"), ("item_type", "SERVICE"), ("base_unit", "HUR"), ("quantity_scale", 0), ("item_group_id", group)));
        await MasterAsync(t, "item-unit-conversions", "pallet", D(("item_id", item), ("unit_code", "PF"), ("factor_to_base", 24)));
        foreach (Guid i in new[] { item, service }) await MasterAsync(t, "item-companies", "ic-" + i, D(("company_id", t.CompanyId), ("item_id", i), ("valuation_method", "MOVING_AVERAGE")));
        Guid warehouse = await MasterAsync(t, "warehouses", "wh", D(("company_id", t.CompanyId), ("code", "MAIN"), ("name", "Main"), ("country_code", "DK")));
        return new S(t, new FulfilmentCommands(t.App), new OrderCommands(t.App), new OrderReads(t.App), customer, supplier, item, service, warehouse, inventory, cogs, grir);
    }

    private static CommandContext Ctx(S s, string key, params string[] more) => s.T.Context(key, more);

    private static async Task<OrderResult> PurchaseOrderAsync(S s, string key, decimal qty, decimal price, decimal over = 0)
    {
        var r = await s.Orders.CreateAsync(OrderKind.Purchase, s.T.TenantId, Ctx(s, key, "purchase.order"), new(
            D(("company_id", s.T.CompanyId), ("order_date", "2026-03-01"), ("order_currency", "DKK"), ("supplier_partner_id", s.Supplier), ("ship_from_country", "DE")),
            [D(("item_id", s.Item), ("quantity", qty), ("unit_code", "C62"), ("unit_price", price), ("warehouse_id", s.Warehouse), ("over_tolerance_pct", over))]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static async Task<OrderResult> SalesOrderAsync(S s, string key, params Dictionary<string, object?>[] lines)
    {
        var r = await s.Orders.CreateAsync(OrderKind.Sales, s.T.TenantId, Ctx(s, key, "sales.order"), new(
            D(("company_id", s.T.CompanyId), ("order_date", "2026-03-01"), ("order_currency", "DKK"), ("customer_partner_id", s.Customer)), lines));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private Dictionary<string, object?> GoodsLine(S s, decimal qty, decimal price, decimal over = 0) =>
        D(("item_id", s.Item), ("quantity", qty), ("unit_code", "C62"), ("unit_price", price), ("warehouse_id", s.Warehouse), ("destination_country", "SE"), ("over_tolerance_pct", over));

    private static Dictionary<string, object?> ServiceLine(S s, decimal qty, decimal price) =>
        D(("item_id", s.Service), ("quantity", qty), ("unit_code", "HUR"), ("unit_price", price), ("destination_country", "DK"));

    private static async Task<FulfilmentResult> ReceiveAsync(S s, string key, OrderResult po, decimal qty, string date = "2026-03-10")
    {
        var r = await s.Cmds.PostReceiptAsync(s.T.TenantId, Ctx(s, key, "purchase.receive"), new(s.T.CompanyId, DateOnly.Parse(date), [new("R1", po.OrderId, 1, qty, "C62")], DeliveryNoteReference: "DN-1"));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static async Task<decimal> BalanceAsync(S s, Guid account) =>
        await s.T.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and company_id = '{s.T.CompanyId}' and account_id = @a", ("a", account));

    [Fact]
    public async Task APurchaseReceiptValuesTheStockAtTheShareOfTheOrderAndTheLimitIsTheOrderPlusTolerance()
    {
        var s = await SetupAsync();
        var t = s.T;
        var po = await PurchaseOrderAsync(s, "po", 100, 12.5m, over: 10);
        var first = await ReceiveAsync(s, "r1", po, 40);
        Assert.Equal(("FU-2026-00001", 1L), (first.DocumentNumber, first.ChainSeq));
        var line = Assert.Single(first.Lines);
        Assert.Equal((40m, 500m, 1, 40m, 60m, "R1"), (line.BaseQuantity, line.ValueLc, line.StockDocumentLineNo, line.FulfilledBaseQuantity, line.OpenBaseQuantity, line.LineRef));
        Assert.NotNull(first.StockDocumentId);
        Assert.NotNull(first.JournalDocumentId);
        Assert.Equal((500m, -500m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));

        // the stock document binds the row hash of the receipt, which names the stock document by id (11, 5.5)
        Assert.True(await t.ScalarAsync<bool>($"select encode(detail_digest, 'hex') = '{first.RowHash}' from core.stock_document where tenant_id = @t and stock_document_id = '{first.StockDocumentId}'"));
        Assert.Equal(first.RowHash, await t.ScalarAsync<string>($"select encode(last_hash, 'hex') from core.chain_head where tenant_id = @t and company_id = '{t.CompanyId}' and chain_type = 'FULFILMENT'"));
        Assert.Equal("purchase-receipts:post", await t.ScalarAsync<string>($"select command_name from core.stock_document where tenant_id = @t and stock_document_id = '{first.StockDocumentId}'"));
        Assert.Equal(("RECEIPT", "COMPUTED"), (await t.ScalarAsync<string>($"select movement_type from core.stock_movement where tenant_id = @t and stock_document_id = '{first.StockDocumentId}'"),
            await t.ScalarAsync<string>($"select cost_source from core.stock_movement where tenant_id = @t and stock_document_id = '{first.StockDocumentId}'")));

        // the rest of the order, up to the order plus the over tolerance of 10 per cent: 110 in all
        var second = await ReceiveAsync(s, "r2", po, 60, "2026-03-11");
        Assert.Equal((750m, 2L, 100m, 0m), (second.Lines[0].ValueLc, second.ChainSeq, second.Lines[0].FulfilledBaseQuantity, second.Lines[0].OpenBaseQuantity));
        Assert.Equal(1250m, await BalanceAsync(s, s.Inventory));
        var more = await s.Cmds.PostReceiptAsync(t.TenantId, Ctx(s, "r3", "purchase.receive"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("R1", po.OrderId, 1, 10, "C62")]));
        Assert.True(more.Status == CommandStatus.Ok, Describe(more));   // 110 is the limit
        Assert.Equal(["ORDER.OVER_DELIVERY"], Codes(await s.Cmds.PostReceiptAsync(t.TenantId, Ctx(s, "r4", "purchase.receive"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("R1", po.OrderId, 1, 1, "C62")]))));

        // progress and the replay
        var progress = (await s.Reads.GetAsync(OrderKind.Purchase, t.TenantId, Ctx(s, "g", "read.orders"), new(po.OrderId, ["progress"]))).Value!.Progress!.Value[0];
        Assert.Equal((110m, 0m, 0m), (progress.GetProperty("fulfilled").GetDecimal(), progress.GetProperty("open").GetDecimal(), progress.GetProperty("limit").GetDecimal()));
        var replay = await s.Cmds.PostReceiptAsync(t.TenantId, Ctx(s, "r1", "purchase.receive"), new(t.CompanyId, new DateOnly(2026, 3, 10), [new("R1", po.OrderId, 1, 40, "C62")], DeliveryNoteReference: "DN-1"));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.DocumentId, replay.Value!.DocumentId);
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v", "chain.verify"), new(t.CompanyId, "LEDGER"));
        Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, Describe(verify));
    }

    [Fact]
    public async Task ASalesDeliveryIssuesTheStockAtCostAndAServiceLineMovesNoStock()
    {
        var s = await SetupAsync();
        var t = s.T;
        await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 100, 10), 100);   // 100 pieces worth 1000
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 30, 25), ServiceLine(s, 5, 400));

        var delivery = await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12),
            [new("D1", so.OrderId, 1, 10, "C62"), new("D2", so.OrderId, 2, 2, "HUR")], CarrierReference: "DHL", TrackingReference: "TRK-9"));
        Assert.True(delivery.Status == CommandStatus.Ok, Describe(delivery));
        var d = delivery.Value!;
        Assert.Equal(("FU-2026-00002", 2L), (d.DocumentNumber, d.ChainSeq));
        Assert.Equal((10m, 100m, 1, 10m, 20m), (d.Lines[0].BaseQuantity, d.Lines[0].ValueLc, d.Lines[0].StockDocumentLineNo, d.Lines[0].FulfilledBaseQuantity, d.Lines[0].OpenBaseQuantity));
        Assert.Equal((2m, null, null, 2m, 3m), (d.Lines[1].BaseQuantity, d.Lines[1].ValueLc, d.Lines[1].StockDocumentLineNo, d.Lines[1].FulfilledBaseQuantity, d.Lines[1].OpenBaseQuantity));
        Assert.Equal(900m, await BalanceAsync(s, s.Inventory));
        Assert.Equal(100m, await BalanceAsync(s, s.Cogs));
        Assert.Equal(("ISSUE", -10m), (await t.ScalarAsync<string>($"select movement_type from core.stock_movement where tenant_id = @t and stock_document_id = '{d.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select base_quantity from core.stock_movement where tenant_id = @t and stock_document_id = '{d.StockDocumentId}'")));
        Assert.True(await t.ScalarAsync<bool>($"select encode(detail_digest, 'hex') = '{d.RowHash}' from core.stock_document where tenant_id = @t and stock_document_id = '{d.StockDocumentId}'"));
        Assert.Equal(("DHL", "TRK-9"), (await t.ScalarAsync<string>($"select carrier_reference from core.sales_delivery where tenant_id = @t and delivery_id = '{d.DocumentId}'"),
            await t.ScalarAsync<string>($"select tracking_reference from core.sales_delivery where tenant_id = @t and delivery_id = '{d.DocumentId}'")));

        // a delivery of services only has no stock document and no journal, and its node is in the chain
        var services = await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d2", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 13), [new("D1", so.OrderId, 2, 3, "HUR")]));
        Assert.True(services.Status == CommandStatus.Ok, Describe(services));
        Assert.Equal((null, null, 3L, 5m, 0m), (services.Value!.StockDocumentId, services.Value.JournalDocumentId, services.Value.ChainSeq, services.Value.Lines[0].FulfilledBaseQuantity, services.Value.Lines[0].OpenBaseQuantity));
        Assert.Equal(services.Value.RowHash, await t.ScalarAsync<string>($"select encode(last_hash, 'hex') from core.chain_head where tenant_id = @t and company_id = '{t.CompanyId}' and chain_type = 'FULFILMENT'"));

        // the limit, the stock and the units
        Assert.Equal(["ORDER.OVER_DELIVERY"], Codes(await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d3", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("D1", so.OrderId, 1, 21, "C62")]))));
        Assert.Equal(["INVENTORY.UNIT_CONVERSION_MISSING"], Codes(await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d4", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("D1", so.OrderId, 1, 1, "BX")]))));
        var big = await SalesOrderAsync(s, "so2", GoodsLine(s, 500, 25));
        var short1 = await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d5", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("D1", big.OrderId, 1, 100, "C62")]));
        Assert.Equal(["INVENTORY.INSUFFICIENT_STOCK"], Codes(short1));
        Assert.Equal("/lines/0/quantity", short1.Errors[0].Path);
        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.sales_delivery where tenant_id = @t") + 1);   // nothing was written by the refused ones: 2 documents
        Assert.Equal(900m, await BalanceAsync(s, s.Inventory));

        // a pallet is 24 pieces
        var pallet = await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d6", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("D1", so.OrderId, 1, 0.25m, "PF")]));
        Assert.Equal(6m, pallet.Value!.Lines[0].BaseQuantity);
    }

    [Fact]
    public async Task OrdersPartiesAndPermissionsAreChecked()
    {
        var s = await SetupAsync();
        var t = s.T;
        await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 50, 10), 50);
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 10, 25), GoodsLine(s, 5, 25));
        Task<CommandResult<FulfilmentResult>> Deliver(string key, params DeliveryLineInput[] lines) =>
            s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, key, "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12), lines));

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "p0", "sales.order"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("D1", so.OrderId, 1, 1, "C62")]))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Cmds.PostReceiptAsync(t.TenantId, Ctx(s, "p1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("D1", so.OrderId, 1, 1, "C62")]))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Deliver("p2", new DeliveryLineInput("D1", Guid.NewGuid(), 1, 1, "C62"))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Deliver("p3", new DeliveryLineInput("D1", so.OrderId, 9, 1, "C62"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Deliver("p4", new DeliveryLineInput("D1", so.OrderId, 1, 0, "C62"))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "p5", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12), []))));
        Assert.Equal(["SYSTEM.FISCAL_YEAR_MISSING"], Codes(await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "p6", "sales.deliver"), new(t.CompanyId, new DateOnly(2025, 6, 1), [new("D1", so.OrderId, 1, 1, "C62")]))));

        // a closed line and a cancelled order do not take a delivery, and a restricted customer does not either
        Assert.Equal(CommandStatus.Ok, (await s.Orders.CloseLineAsync(OrderKind.Sales, t.TenantId, Ctx(s, "k", "sales.order"), new(so.OrderId, 1, 2, "FULFILLED"))).Status);
        Assert.Equal(["ORDER.LINE_NOT_OPEN"], Codes(await Deliver("p7", new DeliveryLineInput("D1", so.OrderId, 2, 1, "C62"))));
        await t.ExecuteAsync("update core.business_partner set restriction_status = 'RESTRICTED' where tenant_id = @t and partner_id = @p", ("p", s.Customer));
        Assert.Equal(["MASTER.PARTNER_RESTRICTED"], Codes(await Deliver("p8", new DeliveryLineInput("D1", so.OrderId, 1, 1, "C62"))));
        await t.ExecuteAsync("update core.business_partner set restriction_status = 'NONE' where tenant_id = @t and partner_id = @p", ("p", s.Customer));
        Assert.Equal(CommandStatus.Ok, (await s.Orders.CancelAsync(OrderKind.Sales, t.TenantId, Ctx(s, "x", "sales.order"), new(so.OrderId, 2, "CUSTOMER"))).Status);
        Assert.Equal(["ORDER.ORDER_NOT_OPEN"], Codes(await Deliver("p9", new DeliveryLineInput("D1", so.OrderId, 1, 1, "C62"))));

        // one document, one customer
        var other = await PartnerAsync(t, "cust2", "ZED", true);
        var so2 = await s.Orders.CreateAsync(OrderKind.Sales, t.TenantId, Ctx(s, "so2", "sales.order"), new(
            D(("company_id", t.CompanyId), ("order_date", "2026-03-01"), ("order_currency", "DKK"), ("customer_partner_id", other)), [GoodsLine(s, 5, 25)]));
        var so3 = await SalesOrderAsync(s, "so3", GoodsLine(s, 5, 25));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Deliver("p10", new DeliveryLineInput("D1", so2.Value!.OrderId, 1, 1, "C62"), new DeliveryLineInput("D2", so3.OrderId, 1, 1, "C62"))));
        var two = await Deliver("p11", new DeliveryLineInput("D1", so3.OrderId, 1, 1, "C62"), new DeliveryLineInput("D2", so3.OrderId, 1, 2, "C62"));
        Assert.True(two.Status == CommandStatus.Ok, Describe(two));   // two lines of one order line add up
        Assert.Equal(["ORDER.OVER_DELIVERY"], Codes(await Deliver("p12", new DeliveryLineInput("D1", so3.OrderId, 1, 3, "C62"), new DeliveryLineInput("D2", so3.OrderId, 1, 2, "C62"))));   // 3 + 2 on 2 open
    }

    [Fact]
    public async Task TheOrderRulesThatNeedProgressAreEnforcedOnceSomethingIsDelivered()
    {
        var s = await SetupAsync();
        var t = s.T;
        await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 100, 10), 100);
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 20, 25), GoodsLine(s, 8, 25));
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("D1", so.OrderId, 1, 12, "C62")]))).Status);
        CommandContext Sales(string key) => Ctx(s, key, "sales.order");

        // the quantity cannot fall below what is delivered, the warehouse is fixed after the first delivery, and a line with a delivery is short-closed, not cancelled
        Assert.Equal(["ORDER.QUANTITY_BELOW_PROGRESS"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales("c1"), new(so.OrderId, 1, null, [new(1, D(("quantity", 11)))]))));
        var wh2 = await MasterAsync(t, "warehouses", "wh2", D(("company_id", t.CompanyId), ("code", "AUX"), ("name", "Aux"), ("country_code", "DK")));
        Assert.Equal(["ORDER.LINE_HAS_PROGRESS"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales("c2"), new(so.OrderId, 1, null, [new(1, D(("warehouse_id", wh2)))]))));
        Assert.Equal(CommandStatus.Ok, (await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales("c3"), new(so.OrderId, 1, null, [new(1, D(("quantity", 12)))]))).Status);   // down to what is delivered
        Assert.Equal(CommandStatus.Ok, (await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Sales("c4"), new(so.OrderId, 2, null, [new(2, D(("warehouse_id", wh2)))]))).Status);   // line 2 has no delivery
        Assert.Equal(["ORDER.LINE_HAS_PROGRESS"], Codes(await s.Orders.CancelAsync(OrderKind.Sales, t.TenantId, Sales("c5"), new(so.OrderId, 3, "CUSTOMER", 1))));
        Assert.Equal(["ORDER.LINE_HAS_PROGRESS"], Codes(await s.Orders.CancelAsync(OrderKind.Sales, t.TenantId, Sales("c6"), new(so.OrderId, 3, "CUSTOMER"))));   // the whole order has a line with a delivery
        var closed = await s.Orders.CloseLineAsync(OrderKind.Sales, t.TenantId, Sales("c7"), new(so.OrderId, 3, 1, "FULFILLED"));
        Assert.Equal(CommandStatus.Ok, closed.Status);
        // the closed line takes no more, and its open quantity is zero
        Assert.Equal(["ORDER.LINE_NOT_OPEN"], Codes(await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d2", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 13), [new("D1", so.OrderId, 1, 1, "C62")]))));
        var progress = (await s.Reads.GetAsync(OrderKind.Sales, t.TenantId, Ctx(s, "g", "read.orders"), new(so.OrderId, ["progress"]))).Value!.Progress!.Value;
        Assert.Equal((12m, 0m, "CLOSED"), (progress[0].GetProperty("fulfilled").GetDecimal(), progress[0].GetProperty("open").GetDecimal(), progress[0].GetProperty("line_status").GetString()));
        Assert.Equal((0m, 8m), (progress[1].GetProperty("fulfilled").GetDecimal(), progress[1].GetProperty("open").GetDecimal()));

        // the documents are frozen and nothing is deleted
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.sales_delivery_line set base_quantity = 1 where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.sales_delivery set delivery_date = date '2026-01-01' where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("delete from core.sales_delivery where tenant_id = @t"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.sales_delivery where tenant_id = @t"));
    }
}
