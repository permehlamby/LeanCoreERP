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
                ('STOCK_DOC_RECEIPT', 'STK'), ('STOCK_DOC_ISSUE', 'STK'), ('STOCK_DOC_ADJUSTMENT', 'STK'), ('STOCK_DOC_REVERSAL', 'STK'), ('STOCK_REVERSAL', 'STJ'), ('STOCK_DOC_RETURN', 'STK'), ('STOCK_RETURN', 'STJ'), ('SALES_RETURN', 'FUL'), ('PURCHASE_RETURN', 'FUL'), ('STOCK_RECEIPT', 'STJ'), ('STOCK_ISSUE', 'STJ'), ('STOCK_ADJUSTMENT', 'STJ'),
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

    // ---- reversal (F-IMPL-52): stock-documents:reverse, sales-deliveries:reverse, purchase-receipts:reverse

    private static CommandContext Stock(S s, string key, params string[] more) => s.T.Context(key, more.Length == 0 ? ["stock.post", "stock.reverse"] : more);

    private static async Task<StockDocumentResult> StockPostAsync(S s, string key, string type, decimal qty, decimal? price, string date = "2026-03-10")
    {
        var r = await new StockCommands(s.T.App).PostAsync(s.T.TenantId, Stock(s, key, "stock.post"), new(s.T.CompanyId, type, DateOnly.Parse(date), DateOnly.Parse(date),
            [new("L1", s.Item, s.Warehouse, qty, "C62", UnitPrice: price)], type == "RECEIPT" && price is null ? "OPENING" : null));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static Task<CommandResult<StockDocumentResult>> StockReverseAsync(S s, string key, Guid id, string reason = "CORRECTION", DateOnly? date = null, params string[] permissions) =>
        new StockCommands(s.T.App).ReverseAsync(s.T.TenantId, Stock(s, key, permissions), new(s.T.CompanyId, id, reason, date));

    [Fact]
    public async Task AStockDocumentIsReversedAtItsOriginalValueAndItsGrIrOpenItemIsCleared()
    {
        var s = await SetupAsync();
        var t = s.T;
        var receipt = await StockPostAsync(s, "r1", "RECEIPT", 100, 10m);
        Assert.Equal((1000m, -1000m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));
        var rev = await StockReverseAsync(s, "rev1", receipt.StockDocumentId);
        Assert.True(rev.Status == CommandStatus.Ok, Describe(rev));
        var m = rev.Value!;
        Assert.Equal(("STOCK_REVERSAL", "SD-2026-00002", 2L), (m.DocumentType, m.DocumentNumber, m.ChainSeq));
        var move = Assert.Single(m.Movements);
        Assert.Equal(("RECEIPT", -100m, -1000m, 0m, 0m), (move.MovementType, move.BaseQuantity, move.ValueLc, move.CostQtyAfter, move.CostValueAfterLc));
        Assert.Equal((0m, 0m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));

        // the original: status, link, the mirror link of the movement; the journal mirror with its reason and the cleared GR/IR items
        Assert.Equal(("reversed", m.StockDocumentId), (await t.ScalarAsync<string>($"select status from core.stock_document where tenant_id = @t and stock_document_id = '{receipt.StockDocumentId}'"),
            await t.ScalarAsync<Guid>($"select reversed_by_stock_document_id from core.stock_document where tenant_id = @t and stock_document_id = '{receipt.StockDocumentId}'")));
        Assert.Equal((receipt.StockDocumentId, "CORRECTION", 1), (await t.ScalarAsync<Guid>($"select reversal_of_stock_document_id from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<string>($"select reason_code from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<int>($"select reversal_of_line_no from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'")));
        Assert.Equal(("reversed", "STOCK_REVERSAL", "STOCK"), (await t.ScalarAsync<string>($"select status from core.journal_document where tenant_id = @t and document_id = '{receipt.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select origin from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'")));
        Assert.Equal(2L, await t.ScalarAsync<long>($"select count(*) from core.journal_line_open_item where tenant_id = @t and clearing_status = 'CLEARED' and document_id in ('{receipt.JournalDocumentId}', '{m.JournalDocumentId}')"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t and clearing_method = 'REVERSAL'"));
        Assert.True(await t.ScalarAsync<bool>($"select detail_digest is null from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"));
        Assert.Equal(m.RowHash, await t.ScalarAsync<string>($"select encode(last_hash, 'hex') from core.chain_head where tenant_id = @t and company_id = '{t.CompanyId}' and chain_type = 'STOCK'"));

        // twice, the mirror itself, the replay, the journal through the other command
        Assert.Equal(["REVERSAL.ALREADY_REVERSED"], Codes(await StockReverseAsync(s, "rev2", receipt.StockDocumentId)));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await StockReverseAsync(s, "rev3", m.StockDocumentId)));
        var replay = await StockReverseAsync(s, "rev1", receipt.StockDocumentId);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(m.StockDocumentId, replay.Value!.StockDocumentId);
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("jr", "journal.reverse"), new(m.JournalDocumentId!.Value, null, "CORRECTION", null))));
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }

    [Fact]
    public async Task AReceiptThatIsConsumedCannotBeReversedAndAnIssueReturnsAtItsOwnValue()
    {
        var s = await SetupAsync();
        await StockPostAsync(s, "r1", "RECEIPT", 100, 10m);   // 100 for 1000
        var second = await StockPostAsync(s, "r2", "RECEIPT", 100, 20m, "2026-03-11");   // 200 pieces for 3000, average 15
        var issue = await StockPostAsync(s, "i1", "ISSUE", 40, null, "2026-03-12");   // 40 at 15 = 600
        Assert.Equal(2400m, await BalanceAsync(s, s.Inventory));
        var consumed = await StockPostAsync(s, "i2", "ISSUE", 150, null, "2026-03-13");   // 10 are left, worth 150
        long documents = await s.T.ScalarAsync<long>("select count(*) from core.stock_document where tenant_id = @t");
        Assert.Equal(["INVENTORY.RECEIPT_CONSUMED"], Codes(await StockReverseAsync(s, "x1", second.StockDocumentId)));
        Assert.Equal(documents, await s.T.ScalarAsync<long>("select count(*) from core.stock_document where tenant_id = @t"));   // nothing was written by the refused reversal

        // the issues are reversed at their original values: the stock and the value are back, and then the receipt is reversible
        Assert.Equal(CommandStatus.Ok, (await StockReverseAsync(s, "x2", consumed.StockDocumentId)).Status);
        Assert.Equal(CommandStatus.Ok, (await StockReverseAsync(s, "x3", issue.StockDocumentId)).Status);
        Assert.Equal(3000m, await BalanceAsync(s, s.Inventory));
        var undo = await StockReverseAsync(s, "x4", second.StockDocumentId);
        Assert.True(undo.Status == CommandStatus.Ok, Describe(undo));
        Assert.Equal((100m, 1000m), (undo.Value!.Movements[0].CostQtyAfter, undo.Value.Movements[0].CostValueAfterLc));
        Assert.Equal(1000m, await BalanceAsync(s, s.Inventory));

        // the reasons and the permission
        Assert.Equal(["REVERSAL.REASON_REQUIRED"], Codes(await StockReverseAsync(s, "y1", second.StockDocumentId, "")));
        Assert.Equal(["REVERSAL.REASON_NOT_ALLOWED"], Codes(await StockReverseAsync(s, "y2", second.StockDocumentId, "RETURN")));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await StockReverseAsync(s, "y3", second.StockDocumentId, "CORRECTION", null, "stock.post")));
        Assert.Equal(["REVERSAL.NOT_FOUND"], Codes(await StockReverseAsync(s, "y4", Guid.NewGuid())));
    }

    [Fact]
    public async Task ADeliveryIsReversedWithItsStockDocumentAndJournalAndTheOrderIsOpenAgain()
    {
        var s = await SetupAsync();
        var t = s.T;
        await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 100, 10), 100);
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 30, 25), ServiceLine(s, 5, 400));
        var delivery = (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12),
            [new("D1", so.OrderId, 1, 10, "C62"), new("D2", so.OrderId, 2, 2, "HUR")], CarrierReference: "DHL", TrackingReference: "TRK-9"))).Value!;
        Assert.Equal(900m, await BalanceAsync(s, s.Inventory));

        // a delivery is not reversed through the stock command nor through the journal command
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await StockReverseAsync(s, "z1", delivery.StockDocumentId!.Value)));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("jr", "journal.reverse"), new(delivery.JournalDocumentId!.Value, null, "CORRECTION", null))));

        var r = await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr", "sales.deliver"), new(t.CompanyId, delivery.DocumentId, "CORRECTION"));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal(("FU-2026-00003", 3L), (m.DocumentNumber, m.ChainSeq));
        Assert.Equal((-10m, -100m, 1, 0m, 30m), (m.Lines[0].BaseQuantity, m.Lines[0].ValueLc, m.Lines[0].StockDocumentLineNo, m.Lines[0].FulfilledBaseQuantity, m.Lines[0].OpenBaseQuantity));
        Assert.Equal((-2m, null, 0m, 5m), (m.Lines[1].BaseQuantity, m.Lines[1].ValueLc, m.Lines[1].FulfilledBaseQuantity, m.Lines[1].OpenBaseQuantity));
        Assert.Equal((1000m, 0m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.Cogs)));
        Assert.Equal(("reversed", delivery.DocumentId, "CORRECTION", "DHL"), (
            await t.ScalarAsync<string>($"select status from core.sales_delivery where tenant_id = @t and delivery_id = '{delivery.DocumentId}'"),
            await t.ScalarAsync<Guid>($"select reversal_of_delivery_id from core.sales_delivery where tenant_id = @t and delivery_id = '{m.DocumentId}'"),
            await t.ScalarAsync<string>($"select reason_code from core.sales_delivery where tenant_id = @t and delivery_id = '{m.DocumentId}'"),
            await t.ScalarAsync<string>($"select carrier_reference from core.sales_delivery where tenant_id = @t and delivery_id = '{m.DocumentId}'")));
        Assert.True(await t.ScalarAsync<bool>($"select encode(detail_digest, 'hex') = '{m.RowHash}' from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"));
        Assert.Equal(("STOCK_REVERSAL", "sales-deliveries:reverse", "reversed"), (
            await t.ScalarAsync<string>($"select document_type from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<string>($"select command_name from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<string>($"select status from core.stock_document where tenant_id = @t and stock_document_id = '{delivery.StockDocumentId}'")));
        Assert.Equal(m.RowHash, await t.ScalarAsync<string>($"select encode(last_hash, 'hex') from core.chain_head where tenant_id = @t and company_id = '{t.CompanyId}' and chain_type = 'FULFILMENT'"));

        // the order is open again, the whole quantity can be delivered, and the reversal cannot be repeated or reversed
        Assert.Equal(["REVERSAL.ALREADY_REVERSED"], Codes(await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr2", "sales.deliver"), new(t.CompanyId, delivery.DocumentId, "CORRECTION"))));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr3", "sales.deliver"), new(t.CompanyId, m.DocumentId, "CORRECTION"))));
        var again = await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d2", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("D1", so.OrderId, 1, 30, "C62")]));
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        var replay = await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr", "sales.deliver"), new(t.CompanyId, delivery.DocumentId, "CORRECTION"));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(m.DocumentId, replay.Value!.DocumentId);
        Assert.Equal(["REVERSAL.NOT_FOUND"], Codes(await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr4", "sales.deliver"), new(t.CompanyId, Guid.NewGuid(), "ERROR"))));
        Assert.Equal(["REVERSAL.REASON_NOT_ALLOWED"], Codes(await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr5", "sales.deliver"), new(t.CompanyId, again.Value!.DocumentId, "RETURN"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr6", "purchase.receive"), new(t.CompanyId, again.Value.DocumentId, "ERROR"))));
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v", "chain.verify"), new(t.CompanyId, "LEDGER"));
        Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, Describe(verify));
    }

    [Fact]
    public async Task AReceiptIsReversedWithItsGrIrAndAConsumedReceiptIsRefused()
    {
        var s = await SetupAsync();
        var t = s.T;
        var po = await PurchaseOrderAsync(s, "po", 100, 12.5m);
        var receipt = await ReceiveAsync(s, "r1", po, 40);
        Assert.Equal((500m, -500m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));

        var r = await s.Cmds.ReverseReceiptAsync(t.TenantId, Ctx(s, "rr", "purchase.receive"), new(t.CompanyId, receipt.DocumentId, "ERROR", new DateOnly(2026, 3, 11)));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal((-40m, -500m, 0m, 100m), (m.Lines[0].BaseQuantity, m.Lines[0].ValueLc, m.Lines[0].FulfilledBaseQuantity, m.Lines[0].OpenBaseQuantity));
        Assert.Equal((0m, 0m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));
        Assert.Equal(("2026-03-11", "reversed", "purchase-receipts:reverse"), (
            await t.ScalarAsync<string>($"select receipt_date::text from core.purchase_receipt where tenant_id = @t and receipt_id = '{m.DocumentId}'"),
            await t.ScalarAsync<string>($"select status from core.purchase_receipt where tenant_id = @t and receipt_id = '{receipt.DocumentId}'"),
            await t.ScalarAsync<string>($"select command_name from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'")));
        Assert.Equal(2L, await t.ScalarAsync<long>($"select count(*) from core.journal_line_open_item where tenant_id = @t and clearing_status = 'CLEARED' and document_id in ('{receipt.JournalDocumentId}', '{m.JournalDocumentId}')"));
        Assert.Equal(("DN-1", 12.5m), (await t.ScalarAsync<string>($"select delivery_note_reference from core.purchase_receipt where tenant_id = @t and receipt_id = '{m.DocumentId}'"),
            await t.ScalarAsync<decimal>($"select unit_price from core.purchase_receipt_line where tenant_id = @t and receipt_id = '{m.DocumentId}'")));

        // a receipt whose goods are gone is refused with nothing written
        var second = await ReceiveAsync(s, "r2", po, 60, "2026-03-12");
        var sold = await SalesOrderAsync(s, "so", GoodsLine(s, 50, 25));
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 13), [new("D1", sold.OrderId, 1, 50, "C62")]))).Status);
        long before = await t.ScalarAsync<long>("select count(*) from core.purchase_receipt where tenant_id = @t");
        var refused = await s.Cmds.ReverseReceiptAsync(t.TenantId, Ctx(s, "rr2", "purchase.receive"), new(t.CompanyId, second.DocumentId, "CORRECTION"));
        Assert.Equal(["INVENTORY.RECEIPT_CONSUMED"], Codes(refused));
        Assert.Equal(before, await t.ScalarAsync<long>("select count(*) from core.purchase_receipt where tenant_id = @t"));
        Assert.Equal("posted", await t.ScalarAsync<string>($"select status from core.purchase_receipt where tenant_id = @t and receipt_id = '{second.DocumentId}'"));
    }

    // ---- returns (F-IMPL-53): sales-returns:post and purchase-returns:post

    [Fact]
    public async Task ASalesReturnTakesTheGoodsBackAtTheOriginalCostAndIsLimitedByTheDeliveredQuantity()
    {
        var s = await SetupAsync();
        var t = s.T;
        var returns = new ReturnCommands(t.App);
        await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 100, 10), 100);   // 100 for 1000
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 30, 25));
        var delivery = (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("D1", so.OrderId, 1, 30, "C62")]))).Value!;
        await ReceiveAsync(s, "r2", await PurchaseOrderAsync(s, "po2", 50, 20), 50, "2026-03-13");   // the average moves to 1000 - 300 + 1000 = 1700 for 120
        Assert.Equal(1700m, await BalanceAsync(s, s.Inventory) - 0m);

        Task<CommandResult<FulfilmentResult>> Return(string key, decimal qty, string? disposition = null, string permission = "sales.return") =>
            returns.PostSalesReturnAsync(t.TenantId, Ctx(s, key, permission), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("R1", delivery.DocumentId, 1, qty, "C62", "DAMAGED", disposition)], "RMA-1"));

        var first = await Return("ret1", 10);
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        var m = first.Value!;
        Assert.Equal(("FU-2026-00004", 4L), (m.DocumentNumber, m.ChainSeq));
        Assert.Equal((10m, 100m, 1, 10m, 20m), (m.Lines[0].BaseQuantity, m.Lines[0].ValueLc, m.Lines[0].StockDocumentLineNo, m.Lines[0].FulfilledBaseQuantity, m.Lines[0].OpenBaseQuantity));
        // the original cost of 10 per piece, not the average of the moment
        Assert.Equal((1800m, 200m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.Cogs)));
        Assert.Equal(("STOCK_RETURN", "RETURN_IN", 10m, "sales-returns:post"), (
            await t.ScalarAsync<string>($"select document_type from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<string>($"select movement_type from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select base_quantity from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<string>($"select command_name from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'")));
        Assert.Equal(delivery.StockDocumentId, await t.ScalarAsync<Guid>($"select valuation_ref_document_id from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"));
        Assert.True(await t.ScalarAsync<bool>($"select encode(detail_digest, 'hex') = '{m.RowHash}' from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"));
        Assert.Equal(m.RowHash, await t.ScalarAsync<string>($"select encode(last_hash, 'hex') from core.chain_head where tenant_id = @t and company_id = '{t.CompanyId}' and chain_type = 'FULFILMENT'"));

        // the returned quantity shows in the progress, the delivery cannot be reversed while the return stands, the rest comes back at the rest of the cost
        var progress = (await s.Reads.GetAsync(OrderKind.Sales, t.TenantId, Ctx(s, "g", "read.orders"), new(so.OrderId, ["progress"]))).Value!.Progress!.Value[0];
        Assert.Equal((30m, 10m), (progress.GetProperty("fulfilled").GetDecimal(), progress.GetProperty("returned").GetDecimal()));
        Assert.Equal(["ORDER.DELIVERY_RETURNED"], Codes(await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr", "sales.deliver"), new(t.CompanyId, delivery.DocumentId, "CORRECTION"))));
        Assert.Equal(["ORDER.RETURN_EXCEEDS_DELIVERED"], Codes(await Return("ret2", 21)));
        var rest = await Return("ret3", 20);
        Assert.True(rest.Status == CommandStatus.Ok, Describe(rest));
        Assert.Equal((200m, 0m, 0m), (rest.Value!.Lines[0].ValueLc, rest.Value.Lines[0].OpenBaseQuantity, await BalanceAsync(s, s.Cogs)));
        Assert.Equal(["ORDER.RETURN_EXCEEDS_DELIVERED"], Codes(await Return("ret4", 1)));

        // the checks of the request
        Assert.Equal(["LIMIT.RETURN_DISPOSITION_NOT_BUILT"], Codes(await Return("ret5", 1, "QUARANTINE")));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await Return("ret6", 1, null, "sales.deliver")));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await returns.PostSalesReturnAsync(t.TenantId, Ctx(s, "ret7", "sales.return"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("R1", delivery.DocumentId, 9, 1, "C62", "X")]))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await returns.PostSalesReturnAsync(t.TenantId, Ctx(s, "ret8", "sales.return"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("R1", delivery.DocumentId, 1, 0, "C62", "X")]))));
        var replay = await returns.PostSalesReturnAsync(t.TenantId, Ctx(s, "ret1", "sales.return"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("R1", delivery.DocumentId, 1, 10, "C62", "DAMAGED")], "RMA-1"));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(m.DocumentId, replay.Value!.DocumentId);
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v", "chain.verify"), new(t.CompanyId, "LEDGER"));
        Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, Describe(verify));
    }

    [Fact]
    public async Task APurchaseReturnTakesTheGoodsOutAtTheReceiptValueAndAConsumedReceiptCannotBeReturned()
    {
        var s = await SetupAsync();
        var t = s.T;
        var returns = new ReturnCommands(t.App);
        var po = await PurchaseOrderAsync(s, "po", 100, 12.5m);
        var receipt = await ReceiveAsync(s, "r1", po, 40);   // 40 for 500
        await ReceiveAsync(s, "r2", await PurchaseOrderAsync(s, "po2", 40, 20), 40, "2026-03-11");   // 40 for 800
        Task<CommandResult<FulfilmentResult>> Return(string key, decimal qty) =>
            returns.PostPurchaseReturnAsync(t.TenantId, Ctx(s, key, "purchase.return"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("R1", receipt.DocumentId, 1, qty, "C62", "WRONG_ITEM")]));

        var first = await Return("pr1", 10);
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        var m = first.Value!;
        Assert.Equal((10m, 125m, 10m, 30m), (m.Lines[0].BaseQuantity, m.Lines[0].ValueLc, m.Lines[0].FulfilledBaseQuantity, m.Lines[0].OpenBaseQuantity));
        Assert.Equal((1175m, -1300m + 125m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));   // 500 + 800 - 125; GR/IR credits 1300 less the debit of 125
        Assert.Equal(("RETURN_OUT", -10m, -125m), (
            await t.ScalarAsync<string>($"select movement_type from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select base_quantity from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select value_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'")));
        Assert.True(await t.ScalarAsync<bool>($"select encode(detail_digest, 'hex') = '{m.RowHash}' from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"));
        Assert.Equal(["ORDER.RECEIPT_RETURNED"], Codes(await s.Cmds.ReverseReceiptAsync(t.TenantId, Ctx(s, "rr", "purchase.receive"), new(t.CompanyId, receipt.DocumentId, "CORRECTION"))));
        Assert.Equal(["ORDER.RETURN_EXCEEDS_DELIVERED"], Codes(await Return("pr2", 31)));

        // the goods are sold: 70 of the 70 in stock go, so what the receipt brought is gone
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 70, 25));
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 13), [new("D1", so.OrderId, 1, 70, "C62")]))).Status);
        long before = await t.ScalarAsync<long>("select count(*) from core.purchase_return where tenant_id = @t");
        Assert.Equal(["INVENTORY.RECEIPT_CONSUMED"], Codes(await Return("pr3", 5)));
        Assert.Equal(before, await t.ScalarAsync<long>("select count(*) from core.purchase_return where tenant_id = @t"));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await returns.PostPurchaseReturnAsync(t.TenantId, Ctx(s, "pr4", "sales.return"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("R1", receipt.DocumentId, 1, 1, "C62", "X")]))));
    }

    // ---- sales invoices (F-IMPL-55): sales-invoices:create

    private sealed record Inv(Guid Revenue, Guid Receivable, Guid Vat, Guid Code);

    /// <summary>The receivable control account, a revenue account that needs a tax code, a VAT registration with a code of 25 per cent and the rule for DK to DK, the determinations (the series of AR_INVOICE is the one of the test tenant).</summary>
    private static async Task<Inv> InvoiceSetupAsync(S s)
    {
        var t = s.T;
        Guid revenue = Guid.NewGuid(), receivable = Guid.NewGuid(), vat = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values " +
            "(@t, @c, @rev, 'INV-REV', 'Revenue', 'REVENUE', 'MANDATORY', 'NONE'), (@t, @c, @ar, 'INV-AR', 'Receivables', 'ASSET', 'FORBIDDEN', 'CONTROL'), (@t, @c, @vat, 'INV-VAT', 'VAT payable', 'LIABILITY', 'FORBIDDEN', 'NONE')",
            ("rev", revenue), ("ar", receivable), ("vat", vat));
        var det = new AccountDeterminationCommands(t.App);
        foreach (var (role, account) in new[] { ("REVENUE", revenue), ("AR_CONTROL", receivable) })
            Assert.Equal(CommandStatus.Ok, (await det.CreateAsync(t.TenantId, t.Context("det-" + role, "write.config"), new(t.CompanyId, D(("role_code", role), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", account))))).Status);
        var tax = new TaxCommands(t.App);
        var reg = await tax.CreateRegistrationAsync(t.TenantId, t.Context("w-reg", "write.config"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK12345678"), ("scheme", "STANDARD"),
            ("filing_frequency", "QUARTERLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2026-01-01"))));
        Assert.True(reg.Status == CommandStatus.Ok, Describe(reg));
        var code = await tax.CreateCodeAsync(t.TenantId, t.Context("w-c", "write.config"), new(D(("company_id", t.CompanyId), ("code", "U25"), ("description", "U25"), ("tax_type", "OUTPUT"), ("vat_registration_id", reg.Value!.EntityId))));
        Assert.True(code.Status == CommandStatus.Ok, Describe(code));
        var version = await tax.CreateVersionAsync(t.TenantId, t.Context("w-v", "write.config"), new(code.Value!.EntityId, D(("valid_from", "2026-01-01"), ("vat_account_id", vat), ("rate", 25m)), []));
        Assert.True(version.Status == CommandStatus.Ok, Describe(version));
        var rule = await new TaxDeterminationCommands(t.App).CreateRuleAsync(t.TenantId, t.Context("w-r", "write.config"), new(t.CompanyId,
            D(("valid_from", "2026-01-01"), ("priority", 10), ("tax_code_id", code.Value.EntityId), ("origin_country", "DK"), ("destination_country", "SE")), []));
        Assert.True(rule.Status == CommandStatus.Ok, Describe(rule));
        var rule2 = await new TaxDeterminationCommands(t.App).CreateRuleAsync(t.TenantId, t.Context("w-r2", "write.config"), new(t.CompanyId,
            D(("valid_from", "2026-01-01"), ("priority", 10), ("tax_code_id", code.Value.EntityId), ("origin_country", "DK"), ("destination_country", "DK")), []));
        Assert.True(rule2.Status == CommandStatus.Ok, Describe(rule2));
        return new Inv(revenue, receivable, vat, code.Value.EntityId);
    }

    private static Task<CommandResult<SalesInvoiceResult>> Invoice(S s, string key, params SalesInvoiceLineInput[] lines) =>
        new SalesInvoiceCommands(s.T.App).CreateAsync(s.T.TenantId, Ctx(s, key, "sales.invoice"), new(s.T.CompanyId, new DateOnly(2026, 3, 20), lines, SourceDocumentRef: "INV-REF"));

    [Fact]
    public async Task ASalesInvoiceIsBuiltFromTheOrderAndTheDeliveredQuantityAndBindsItsLinkRecords()
    {
        var s = await SetupAsync();
        var t = s.T;
        var inv = await InvoiceSetupAsync(s);
        await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 100, 10), 100);
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 30, 25));
        Assert.Equal(["ORDER.INVOICE_EXCEEDS_OPEN"], Codes(await Invoice(s, "i0", new SalesInvoiceLineInput("L1", so.OrderId, 1))));   // nothing is delivered yet
        var delivery = (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("D1", so.OrderId, 1, 10, "C62")]))).Value!;

        // the quantity defaults to what is open to invoice: 10 delivered at 25, tax 25 per cent
        var r = await Invoice(s, "i1", new SalesInvoiceLineInput("L1", so.OrderId, 1, DeliveryLineRefs: [new(delivery.DocumentId, 1)]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal(("CORE_BUILDS", 250m, 62.5m, 312.5m, "GL-2026-000001"), (m.InvoiceMode, m.NetAmount, m.TaxAmount, m.GrossAmount, m.DocumentNumber));
        var line = Assert.Single(m.Lines);
        Assert.Equal((10m, 250m, delivery.DocumentId, 1, 1, inv.Code), (line.BaseQuantity, line.NetAmount, line.DeliveryId, line.DeliveryLineNo, line.JournalLineNo, line.TaxCodeId));
        Assert.Equal((312.5m, -250m, -62.5m), (
            await t.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and document_id = '{m.JournalDocumentId}' and account_id = '{inv.Receivable}'"),
            await t.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and document_id = '{m.JournalDocumentId}' and account_id = '{inv.Revenue}'"),
            await t.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and document_id = '{m.JournalDocumentId}' and account_id = '{inv.Vat}'")));
        Assert.Equal(("AR_INVOICE", "ORDER", "sales-invoices:create", true), (
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select origin from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select command_name from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<bool>($"select detail_digest is not null from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'")));
        Assert.Equal(("OPEN", s.Customer), (await t.ScalarAsync<string>($"select clearing_status from core.journal_line_open_item where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<Guid>($"select partner_id from core.journal_line_open_item where tenant_id = @t and document_id = '{m.JournalDocumentId}'")));
        Assert.Equal(("2026-03-12", 1L), (await t.ScalarAsync<string>($"select tax_date::text from core.sales_invoice_line where tenant_id = @t and sales_invoice_id = '{m.SalesInvoiceId}'"),
            await t.ScalarAsync<long>("select count(*) from core.sales_invoice where tenant_id = @t")));   // the tax point is the delivery date

        // the open quantity is used up, a named delivery line gives only its own quantity, and the price of the line is locked
        Assert.Equal(["ORDER.INVOICE_EXCEEDS_OPEN"], Codes(await Invoice(s, "i2", new SalesInvoiceLineInput("L1", so.OrderId, 1, 1))));
        var more = (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d2", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 13), [new("D1", so.OrderId, 1, 5, "C62")]))).Value!;
        Assert.Equal(["ORDER.INVOICE_EXCEEDS_OPEN"], Codes(await Invoice(s, "i3", new SalesInvoiceLineInput("L1", so.OrderId, 1, 6, DeliveryLineRefs: [new(more.DocumentId, 1)]))));
        var five = await Invoice(s, "i4", new SalesInvoiceLineInput("L1", so.OrderId, 1, 5, DeliveryLineRefs: [new(more.DocumentId, 1)]));
        Assert.True(five.Status == CommandStatus.Ok, Describe(five));
        Assert.Equal(["ORDER.PRICE_LOCKED_INVOICED"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Ctx(s, "chg", "sales.order"), new(so.OrderId, so.RowVersion, null, [new(1, D(("unit_price", 26m)))]))));
        var replay = await Invoice(s, "i1", new SalesInvoiceLineInput("L1", so.OrderId, 1, DeliveryLineRefs: [new(delivery.DocumentId, 1)]));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(m.SalesInvoiceId, replay.Value!.SalesInvoiceId);
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v", "chain.verify"), new(t.CompanyId, "LEDGER"));
        Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, Describe(verify));
    }

    [Fact]
    public async Task ASalesInvoiceIsReversedWithItsLinkRecordsAndTheQuantityCanBeInvoicedAgain()
    {
        var s = await SetupAsync();
        var t = s.T;
        var inv = await InvoiceSetupAsync(s);
        await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 100, 10), 100);
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 30, 25));
        var delivery = (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("D1", so.OrderId, 1, 10, "C62")]))).Value!;
        var invoice = (await Invoice(s, "i1", new SalesInvoiceLineInput("L1", so.OrderId, 1, DeliveryLineRefs: [new(delivery.DocumentId, 1)]))).Value!;
        var commands = new SalesInvoiceCommands(t.App);
        Task<CommandResult<ReversedSalesInvoice>> Reverse(string key, Guid id, string reason = "CORRECTION", string permission = "sales.invoice") =>
            commands.ReverseAsync(t.TenantId, Ctx(s, key, permission), new(t.CompanyId, id, reason));

        // the delivery is protected while the invoice names it, and the journal is not reversed through the finance command
        Assert.Equal(["ORDER.DELIVERY_INVOICED"], Codes(await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr", "sales.deliver"), new(t.CompanyId, delivery.DocumentId, "CORRECTION"))));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("jr", "journal.reverse"), new(invoice.JournalDocumentId, null, "CORRECTION", null))));

        var r = await Reverse("rev", invoice.SalesInvoiceId);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal((0m, 0m, 0m), (
            await t.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and company_id = '{t.CompanyId}' and account_id = '{inv.Receivable}'"),
            await t.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and company_id = '{t.CompanyId}' and account_id = '{inv.Revenue}'"),
            await t.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and company_id = '{t.CompanyId}' and account_id = '{inv.Vat}'")));
        Assert.Equal(("reversed", "AR_INVOICE", "ORDER", "sales-invoices:reverse", true), (
            await t.ScalarAsync<string>($"select status from core.journal_document where tenant_id = @t and document_id = '{invoice.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select origin from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select command_name from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<bool>($"select detail_digest is not null from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'")));
        Assert.Equal(2L, await t.ScalarAsync<long>($"select count(*) from core.journal_line_open_item where tenant_id = @t and clearing_status = 'CLEARED' and document_id in ('{invoice.JournalDocumentId}', '{m.JournalDocumentId}')"));
        Assert.Equal(("REVERSAL", invoice.SalesInvoiceId, -10m, -250m, "CORRECTION"), (
            await t.ScalarAsync<string>($"select invoice_kind from core.sales_invoice where tenant_id = @t and sales_invoice_id = '{m.ReversalInvoiceId}'"),
            await t.ScalarAsync<Guid>($"select reversal_of_sales_invoice_id from core.sales_invoice where tenant_id = @t and sales_invoice_id = '{m.ReversalInvoiceId}'"),
            await t.ScalarAsync<decimal>($"select base_quantity from core.sales_invoice_line where tenant_id = @t and sales_invoice_id = '{m.ReversalInvoiceId}' and line_no = 1"),
            await t.ScalarAsync<decimal>($"select net_amount from core.sales_invoice_line where tenant_id = @t and sales_invoice_id = '{m.ReversalInvoiceId}' and line_no = 1"),
            await t.ScalarAsync<string>($"select reason_code from core.sales_invoice where tenant_id = @t and sales_invoice_id = '{m.ReversalInvoiceId}'")));

        // the quantity is open to invoice again, the reversal cannot be repeated or reversed, and the delivery can now be reversed
        var again = await Invoice(s, "i2", new SalesInvoiceLineInput("L1", so.OrderId, 1, DeliveryLineRefs: [new(delivery.DocumentId, 1)]));
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        Assert.Equal(["REVERSAL.ALREADY_REVERSED"], Codes(await Reverse("rev2", invoice.SalesInvoiceId)));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await Reverse("rev3", m.ReversalInvoiceId)));
        Assert.Equal(["REVERSAL.NOT_FOUND"], Codes(await Reverse("rev4", Guid.NewGuid())));
        Assert.Equal(["REVERSAL.REASON_NOT_ALLOWED"], Codes(await Reverse("rev5", again.Value!.SalesInvoiceId, "RETURN")));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await Reverse("rev6", again.Value.SalesInvoiceId, "CORRECTION", "sales.order")));
        var replay = await Reverse("rev", invoice.SalesInvoiceId);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(m.ReversalInvoiceId, replay.Value!.ReversalInvoiceId);
        Assert.Equal(CommandStatus.Ok, (await Reverse("rev7", again.Value.SalesInvoiceId, "ERROR")).Status);
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr2", "sales.deliver"), new(t.CompanyId, delivery.DocumentId, "CORRECTION"))).Status);
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }

    [Fact]
    public async Task AReturnCreditCreditsTheReturnedQuantityAtTheInvoicePriceWithinTheCapsAndIsReversedBeforeTheReturnAndTheInvoice()
    {
        var s = await SetupAsync();
        var t = s.T;
        var inv = await InvoiceSetupAsync(s);
        var returns = new ReturnCommands(t.App);
        var credits = new SalesCreditNoteCommands(t.App);
        await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 100, 10), 100);
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 30, 25));
        var delivery = (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("D1", so.OrderId, 1, 10, "C62")]))).Value!;
        var invoice = (await Invoice(s, "i1", new SalesInvoiceLineInput("L1", so.OrderId, 1, DeliveryLineRefs: [new(delivery.DocumentId, 1)]))).Value!;
        var ret = (await returns.PostSalesReturnAsync(t.TenantId, Ctx(s, "ret", "sales.return"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("R1", delivery.DocumentId, 1, 4, "C62", "DAMAGED")]))).Value!;
        Task<CommandResult<SalesCreditNoteResult>> Credit(string key, string kind, decimal? qty, string permission = "sales.invoice", int lineNo = 1) =>
            credits.CreateAsync(t.TenantId, Ctx(s, key, permission), new(t.CompanyId, invoice.SalesInvoiceId, kind, new DateOnly(2026, 3, 21),
                [new CreditLineInput("C1", lineNo, ret.DocumentId, 1, qty)], SourceDocumentRef: "CN-REF"));
        async Task<decimal> Receivable() => await t.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and company_id = '{t.CompanyId}' and account_id = '{inv.Receivable}'");

        // 4 returned at 25, tax 25 per cent: 100 net, 25 tax, 125 gross credited to the receivable
        var r = await Credit("c1", "RETURN", null);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal(("RETURN", 100m, 25m, 125m, -4m, -100m), (m.CreditKind, m.NetAmount, m.TaxAmount, m.GrossAmount, m.Lines[0].BaseQuantity, m.Lines[0].NetAmount));
        Assert.Equal(187.5m, await Receivable());
        Assert.Equal(("AR_CREDIT", "ORDER", true), (
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select origin from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<bool>($"select detail_digest is not null from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'")));
        Assert.Equal(("CREDIT_NOTE", invoice.SalesInvoiceId, ret.DocumentId), (
            await t.ScalarAsync<string>($"select invoice_kind from core.sales_invoice where tenant_id = @t and sales_invoice_id = '{m.CreditNoteId}'"),
            await t.ScalarAsync<Guid>($"select credits_invoice_id from core.sales_invoice where tenant_id = @t and sales_invoice_id = '{m.CreditNoteId}'"),
            await t.ScalarAsync<Guid>($"select return_id from core.sales_invoice_line where tenant_id = @t and sales_invoice_id = '{m.CreditNoteId}' and line_no = 1")));

        // the caps: the whole return is credited, the invoice cannot be reversed and the return cannot be reversed while the credit note stands
        Assert.Equal(["ORDER.CREDIT_EXCEEDS_INVOICED"], Codes(await Credit("c2", "RETURN", 1)));
        Assert.Equal("RETURN_LINE", (string)(await Credit("c3", "RETURN", 1)).Errors[0].Params!["cap_source"]!);
        Assert.Equal(["ORDER.INVOICE_HAS_CREDIT_NOTES"], Codes(await new SalesInvoiceCommands(t.App).ReverseAsync(t.TenantId, Ctx(s, "ri", "sales.invoice"), new(t.CompanyId, invoice.SalesInvoiceId, "CORRECTION"))));
        Assert.Equal(["ORDER.RETURN_CREDITED"], Codes(await returns.ReverseSalesReturnAsync(t.TenantId, Ctx(s, "rr", "sales.return"), new(t.CompanyId, ret.DocumentId, "CORRECTION"))));

        // reversing the credit note restores the caps: it can be credited again, and the order of the reversals is credit note, return, invoice
        var back = await credits.ReverseAsync(t.TenantId, Ctx(s, "rc", "sales.invoice"), new(t.CompanyId, m.CreditNoteId, "CORRECTION"));
        Assert.True(back.Status == CommandStatus.Ok, Describe(back));
        Assert.Equal(312.5m, await Receivable());
        Assert.Equal(("reversed", "AR_CREDIT", 4m), (
            await t.ScalarAsync<string>($"select status from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{back.Value!.JournalDocumentId}'"),
            await t.ScalarAsync<decimal>($"select base_quantity from core.sales_invoice_line where tenant_id = @t and sales_invoice_id = '{back.Value.ReversalId}' and line_no = 1")));
        Assert.Equal(["ORDER.CREDIT_NOTE_ALREADY_REVERSED"], Codes(await credits.ReverseAsync(t.TenantId, Ctx(s, "rc2", "sales.invoice"), new(t.CompanyId, m.CreditNoteId, "CORRECTION"))));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await credits.ReverseAsync(t.TenantId, Ctx(s, "rc3", "sales.invoice"), new(t.CompanyId, back.Value.ReversalId, "CORRECTION"))));
        Assert.Equal(["ORDER.CREDIT_NOTE_NOT_FOUND"], Codes(await credits.ReverseAsync(t.TenantId, Ctx(s, "rc4", "sales.invoice"), new(t.CompanyId, Guid.NewGuid(), "CORRECTION"))));
        var again = await Credit("c4", "RETURN", 3);
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        Assert.Equal(CommandStatus.Ok, (await credits.ReverseAsync(t.TenantId, Ctx(s, "rc5", "sales.invoice"), new(t.CompanyId, again.Value!.CreditNoteId, "ERROR"))).Status);
        Assert.Equal(CommandStatus.Ok, (await returns.ReverseSalesReturnAsync(t.TenantId, Ctx(s, "rr2", "sales.return"), new(t.CompanyId, ret.DocumentId, "CORRECTION"))).Status);
        Assert.Equal(CommandStatus.Ok, (await new SalesInvoiceCommands(t.App).ReverseAsync(t.TenantId, Ctx(s, "ri2", "sales.invoice"), new(t.CompanyId, invoice.SalesInvoiceId, "CORRECTION"))).Status);
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await Credit("c5", "RETURN", null, "sales.order")));
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }

    [Fact]
    public async Task AnAmountCreditCorrectsThePriceWithTheTaxOfTheInvoiceLineAndIsCappedByTheNetAmount()
    {
        var s = await SetupAsync();
        var t = s.T;
        var inv = await InvoiceSetupAsync(s);
        var credits = new SalesCreditNoteCommands(t.App);
        var so = await SalesOrderAsync(s, "so", W(GoodsLine(s, 10, 25), "invoice_policy", "ORDERED"));
        var invoice = (await Invoice(s, "i1", new SalesInvoiceLineInput("L1", so.OrderId, 1))).Value!;   // 250 net, 62.5 tax
        Task<CommandResult<SalesCreditNoteResult>> Credit(string key, decimal amount, string? reason = "PRICE") =>
            credits.CreateAsync(t.TenantId, Ctx(s, key, "sales.invoice"), new(t.CompanyId, invoice.SalesInvoiceId, "AMOUNT", new DateOnly(2026, 3, 21), [new CreditLineInput("C1", 1, CreditAmountNet: amount)], reason));

        var r = await Credit("c1", 50m);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(("AMOUNT", 50m, 12.5m, 62.5m, 0m, -50m), (r.Value!.CreditKind, r.Value.NetAmount, r.Value.TaxAmount, r.Value.GrossAmount, r.Value.Lines[0].BaseQuantity, r.Value.Lines[0].NetAmount));
        Assert.Equal(250m, await t.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and company_id = '{t.CompanyId}' and account_id = '{inv.Receivable}'"));   // 312.5 less 62.5

        // 200 net are left to credit
        var over = await Credit("c2", 200.01m);
        Assert.Equal(["ORDER.CREDIT_EXCEEDS_INVOICED"], Codes(over));
        Assert.Equal((200m, "INVOICE_LINE"), (Convert.ToDecimal(over.Errors[0].Params!["cap"]), (string)over.Errors[0].Params!["cap_source"]!));
        Assert.Equal(CommandStatus.Ok, (await Credit("c3", 200m)).Status);
        Assert.Equal(["ORDER.CREDIT_EXCEEDS_INVOICED"], Codes(await Credit("c4", 0.01m)));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await Credit("c5", 1m, null)));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await credits.CreateAsync(t.TenantId, Ctx(s, "c6", "sales.invoice"), new(t.CompanyId, invoice.SalesInvoiceId, "OTHER", new DateOnly(2026, 3, 21), [new CreditLineInput("C1", 1, CreditAmountNet: 1m)], "X"))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await credits.CreateAsync(t.TenantId, Ctx(s, "c7", "sales.invoice"), new(t.CompanyId, invoice.SalesInvoiceId, "AMOUNT", new DateOnly(2026, 3, 21), [new CreditLineInput("C1", 9, CreditAmountNet: 1m)], "X"))));
        var replay = await Credit("c1", 50m);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(r.Value.CreditNoteId, replay.Value!.CreditNoteId);

        // an invoice that is reversed cannot be credited: its credit notes go first, then the invoice
        var reversed = await new SalesInvoiceCommands(t.App).ReverseAsync(t.TenantId, Ctx(s, "ri", "sales.invoice"), new(t.CompanyId, invoice.SalesInvoiceId, "CORRECTION"));
        Assert.Equal(["ORDER.INVOICE_HAS_CREDIT_NOTES"], Codes(reversed));
    }

    [Fact]
    public async Task AGoodsLineWithPolicyOrderedIsInvoicedBeforeDeliveryAtTheOrderedQuantityAndTheOverToleranceIsOnlyTheCeiling()
    {
        var s = await SetupAsync();
        var t = s.T;
        await InvoiceSetupAsync(s);
        var so = await SalesOrderAsync(s, "so", W(GoodsLine(s, 30, 25, over: 10), "invoice_policy", "ORDERED"));

        // no delivery and no delivery line: the default is the ordered quantity, not the quantity plus the tolerance
        var first = await Invoice(s, "i1", new SalesInvoiceLineInput("L1", so.OrderId, 1));
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        Assert.Equal((750m, 30m, true), (first.Value!.NetAmount, first.Value.Lines[0].BaseQuantity, first.Value.Lines[0].DeliveryId is null));
        Assert.Equal("2026-03-20", await t.ScalarAsync<string>($"select tax_date::text from core.sales_invoice_line where tenant_id = @t and sales_invoice_id = '{first.Value.SalesInvoiceId}'"));   // the invoice date: nothing is delivered

        // the tolerance is the ceiling: 3 more can be invoiced, 4 cannot, and then nothing is left as a default
        Assert.Equal(["ORDER.INVOICE_EXCEEDS_OPEN"], Codes(await Invoice(s, "i2", new SalesInvoiceLineInput("L1", so.OrderId, 1, 4))));
        Assert.Equal(["ORDER.INVOICE_EXCEEDS_OPEN"], Codes(await Invoice(s, "i3", new SalesInvoiceLineInput("L1", so.OrderId, 1))));   // ordered less invoiced is zero
        var three = await Invoice(s, "i4", new SalesInvoiceLineInput("L1", so.OrderId, 1, 3));
        Assert.True(three.Status == CommandStatus.Ok, Describe(three));

        // 4.4: the quantity cannot fall below what is invoiced, and the tax geography is locked by the invoice
        Assert.Equal(["ORDER.QUANTITY_BELOW_PROGRESS"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Ctx(s, "c1", "sales.order"), new(so.OrderId, so.RowVersion, null, [new(1, D(("quantity", 20)))]))));
        Assert.Equal(["ORDER.LINE_HAS_PROGRESS"], Codes(await s.Orders.ChangeAsync(OrderKind.Sales, t.TenantId, Ctx(s, "c2", "sales.order"), new(so.OrderId, so.RowVersion, null, [new(1, D(("destination_country", "DK")))]))));
    }

    [Fact]
    public async Task InAppSuppliesModeTheCallerSendsPriceAndDiscountAndTheOrderPriceIsKeptBesideThem()
    {
        var s = await SetupAsync();
        var t = s.T;
        await InvoiceSetupAsync(s);
        await t.ExecuteAsync("insert into core.company_order_setting_version (tenant_id, company_id, effective_from, sales_invoice_mode, created_by) values (@t, @c, date '2026-01-01', 'APP_SUPPLIES', 'test')");
        await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 100, 10), 100);
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 30, 25), ServiceLine(s, 5, 400));
        var delivery = (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("D1", so.OrderId, 1, 10, "C62")]))).Value!;

        Assert.Equal(["ORDER.INVOICE_LINE_REFERENCE_MISSING"], Codes(await Invoice(s, "i0", new SalesInvoiceLineInput("L1", so.OrderId, 1, 10))));
        Assert.Equal(["ORDER.INVOICE_LINE_REFERENCE_MISSING"], Codes(await Invoice(s, "i1", new SalesInvoiceLineInput("L1", so.OrderId, 1, 10, UnitPrice: 30m, DiscountPct: 10m))));   // a goods line with policy DELIVERED needs a delivery line
        Assert.Equal(["ORDER.INVOICE_LINE_REFERENCE_MISSING"], Codes(await Invoice(s, "i2", new SalesInvoiceLineInput("L1", so.OrderId, 1, 10, DiscountPct: 10m, DeliveryLineRefs: [new(delivery.DocumentId, 1)]))));
        // a service line with policy ORDERED is invoiced before delivery and needs no reference; the goods line carries the sent price and the order price beside it
        var r = await Invoice(s, "i3", new SalesInvoiceLineInput("L1", so.OrderId, 1, 10, UnitPrice: 30m, DiscountPct: 10m, DeliveryLineRefs: [new(delivery.DocumentId, 1)]), new SalesInvoiceLineInput("L2", so.OrderId, 2, 5, UnitPrice: 400m, DiscountPct: 0m));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal(("APP_SUPPLIES", 270m + 2000m, 2, 2), (m.InvoiceMode, m.NetAmount, m.Lines.Count, m.Lines[1].JournalLineNo));
        Assert.Equal((270m, 25m, 0m), (m.Lines[0].NetAmount, await s.T.ScalarAsync<decimal>($"select order_unit_price from core.sales_invoice_line where tenant_id = @t and sales_invoice_id = '{m.SalesInvoiceId}' and line_no = 1"),
            await s.T.ScalarAsync<decimal>($"select order_discount_pct from core.sales_invoice_line where tenant_id = @t and sales_invoice_id = '{m.SalesInvoiceId}' and line_no = 1")));
        Assert.Equal((true, 2000m), (await s.T.ScalarAsync<bool>($"select delivery_id is null from core.sales_invoice_line where tenant_id = @t and sales_invoice_id = '{m.SalesInvoiceId}' and line_no = 2"), m.Lines[1].NetAmount));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await new SalesInvoiceCommands(t.App).CreateAsync(t.TenantId, Ctx(s, "i4", "sales.invoice"), new(t.CompanyId, new DateOnly(2026, 3, 20), [new("L1", so.OrderId, 1, 1, UnitPrice: -1m, DiscountPct: 0m)]))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new SalesInvoiceCommands(t.App).CreateAsync(t.TenantId, Ctx(s, "i5", "sales.order"), new(t.CompanyId, new DateOnly(2026, 3, 20), [new("L1", so.OrderId, 1, 1)]))));
    }

    // ---- reversal of returns (F-IMPL-54)

    [Fact]
    public async Task ASalesReturnIsReversedAndTheDeliveryCanBeReversedAfterIt()
    {
        var s = await SetupAsync();
        var t = s.T;
        var returns = new ReturnCommands(t.App);
        await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 100, 10), 100);
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 30, 25));
        var delivery = (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 12), [new("D1", so.OrderId, 1, 30, "C62")]))).Value!;
        var ret = (await returns.PostSalesReturnAsync(t.TenantId, Ctx(s, "ret", "sales.return"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("R1", delivery.DocumentId, 1, 10, "C62", "DAMAGED")]))).Value!;
        Assert.Equal((800m, 200m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.Cogs)));

        var r = await returns.ReverseSalesReturnAsync(t.TenantId, Ctx(s, "rev", "sales.return"), new(t.CompanyId, ret.DocumentId, "CORRECTION"));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal((-10m, -100m, 0m, 30m), (m.Lines[0].BaseQuantity, m.Lines[0].ValueLc, m.Lines[0].FulfilledBaseQuantity, m.Lines[0].OpenBaseQuantity));
        Assert.Equal((700m, 300m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.Cogs)));
        Assert.Equal(("reversed", ret.DocumentId, "CORRECTION"), (
            await t.ScalarAsync<string>($"select status from core.sales_return where tenant_id = @t and return_id = '{ret.DocumentId}'"),
            await t.ScalarAsync<Guid>($"select reversal_of_return_id from core.sales_return where tenant_id = @t and return_id = '{m.DocumentId}'"),
            await t.ScalarAsync<string>($"select reason_code from core.sales_return where tenant_id = @t and return_id = '{m.DocumentId}'")));
        Assert.True(await t.ScalarAsync<bool>($"select encode(detail_digest, 'hex') = '{m.RowHash}' from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"));
        Assert.Equal(("STOCK_REVERSAL", "sales-returns:reverse"), (
            await t.ScalarAsync<string>($"select document_type from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<string>($"select command_name from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'")));

        // the mirror names the original movement too, so the full quantity can be returned again and the cost is the original one
        Assert.Equal(["ORDER.RETURN_ALREADY_REVERSED"], Codes(await returns.ReverseSalesReturnAsync(t.TenantId, Ctx(s, "rev2", "sales.return"), new(t.CompanyId, ret.DocumentId, "CORRECTION"))));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await returns.ReverseSalesReturnAsync(t.TenantId, Ctx(s, "rev3", "sales.return"), new(t.CompanyId, m.DocumentId, "CORRECTION"))));
        Assert.Equal(["ORDER.RETURN_NOT_FOUND"], Codes(await returns.ReverseSalesReturnAsync(t.TenantId, Ctx(s, "rev4", "sales.return"), new(t.CompanyId, Guid.NewGuid(), "CORRECTION"))));
        Assert.Equal(["REVERSAL.REASON_NOT_ALLOWED"], Codes(await returns.ReverseSalesReturnAsync(t.TenantId, Ctx(s, "rev5", "sales.return"), new(t.CompanyId, ret.DocumentId, "RETURN"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await returns.ReverseSalesReturnAsync(t.TenantId, Ctx(s, "rev6", "purchase.return"), new(t.CompanyId, ret.DocumentId, "CORRECTION"))));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await StockReverseAsync(s, "sr", ret.StockDocumentId!.Value)));
        var all = await returns.PostSalesReturnAsync(t.TenantId, Ctx(s, "ret2", "sales.return"), new(t.CompanyId, new DateOnly(2026, 3, 15), [new("R1", delivery.DocumentId, 1, 30, "C62", "WRONG")]));
        Assert.True(all.Status == CommandStatus.Ok, Describe(all));
        Assert.Equal((1000m, 0m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.Cogs)));

        // the return of the whole delivery stands: the delivery is reversed after reversing that return
        Assert.Equal(["ORDER.DELIVERY_RETURNED"], Codes(await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr", "sales.deliver"), new(t.CompanyId, delivery.DocumentId, "CORRECTION"))));
        Assert.Equal(CommandStatus.Ok, (await returns.ReverseSalesReturnAsync(t.TenantId, Ctx(s, "rev7", "sales.return"), new(t.CompanyId, all.Value!.DocumentId, "ERROR"))).Status);
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.ReverseDeliveryAsync(t.TenantId, Ctx(s, "dr2", "sales.deliver"), new(t.CompanyId, delivery.DocumentId, "CORRECTION"))).Status);
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v", "chain.verify"), new(t.CompanyId, "LEDGER"));
        Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, Describe(verify));
    }

    [Fact]
    public async Task APurchaseReturnIsReversedWithItsGrIrOpenItemAndReturnedGoodsThatAreConsumedAreRefused()
    {
        var s = await SetupAsync();
        var t = s.T;
        var returns = new ReturnCommands(t.App);
        var receipt = await ReceiveAsync(s, "r1", await PurchaseOrderAsync(s, "po", 40, 12.5m), 40);
        var ret = (await returns.PostPurchaseReturnAsync(t.TenantId, Ctx(s, "pr", "purchase.return"), new(t.CompanyId, new DateOnly(2026, 3, 14), [new("R1", receipt.DocumentId, 1, 10, "C62", "WRONG")]))).Value!;
        Assert.Equal((375m, -375m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));

        var r = await returns.ReversePurchaseReturnAsync(t.TenantId, Ctx(s, "rev", "purchase.return"), new(t.CompanyId, ret.DocumentId, "ERROR", new DateOnly(2026, 3, 15)));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal((-10m, -125m, 0m, 40m), (m.Lines[0].BaseQuantity, m.Lines[0].ValueLc, m.Lines[0].FulfilledBaseQuantity, m.Lines[0].OpenBaseQuantity));
        Assert.Equal((500m, -500m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));
        Assert.Equal(2L, await t.ScalarAsync<long>($"select count(*) from core.journal_line_open_item where tenant_id = @t and clearing_status = 'CLEARED' and document_id in ('{ret.JournalDocumentId}', '{m.JournalDocumentId}')"));
        Assert.Equal("reversed", await t.ScalarAsync<string>($"select status from core.journal_document where tenant_id = @t and document_id = '{ret.JournalDocumentId}'"));

        // the goods that came back are sold again, so the reversal of the sales return is refused with nothing written
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 40, 25));
        var delivery = (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 16), [new("D1", so.OrderId, 1, 40, "C62")]))).Value!;
        var back = (await returns.PostSalesReturnAsync(t.TenantId, Ctx(s, "sr", "sales.return"), new(t.CompanyId, new DateOnly(2026, 3, 17), [new("R1", delivery.DocumentId, 1, 40, "C62", "WRONG")]))).Value!;
        var so2 = await SalesOrderAsync(s, "so2", GoodsLine(s, 40, 25));
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d2", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 18), [new("D1", so2.OrderId, 1, 40, "C62")]))).Status);
        long before = await t.ScalarAsync<long>("select count(*) from core.sales_return where tenant_id = @t");
        Assert.Equal(["INVENTORY.RECEIPT_CONSUMED"], Codes(await returns.ReverseSalesReturnAsync(t.TenantId, Ctx(s, "rev2", "sales.return"), new(t.CompanyId, back.DocumentId, "CORRECTION"))));
        Assert.Equal(before, await t.ScalarAsync<long>("select count(*) from core.sales_return where tenant_id = @t"));
        Assert.Equal("posted", await t.ScalarAsync<string>($"select status from core.sales_return where tenant_id = @t and return_id = '{back.DocumentId}'"));
    }
    // ---- purchase invoices (F-IMPL-58): purchase-invoices:post

    private sealed record Pur(Guid Payable, Guid InputVat, Guid Expense, Guid Code);

    /// <summary>The payable control account, an input VAT account, an expense account that needs a tax code, a DK registration with an input code of 25 per cent and the rule for DK to DK, the determinations.</summary>
    private static async Task<Pur> PurchaseSetupAsync(S s)
    {
        var t = s.T;
        Guid payable = Guid.NewGuid(), vat = Guid.NewGuid(), expense = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values " +
            "(@t, @c, @ap, 'PUR-AP', 'Payables', 'LIABILITY', 'FORBIDDEN', 'CONTROL'), (@t, @c, @vat, 'PUR-VAT', 'Input VAT', 'ASSET', 'FORBIDDEN', 'NONE'), (@t, @c, @ex, 'PUR-EXP', 'Purchases', 'EXPENSE', 'MANDATORY', 'NONE')",
            ("ap", payable), ("vat", vat), ("ex", expense));
        // the GR/IR account takes the tax code of a matched stock line, so it may not forbid it
        await t.ExecuteAsync($"update core.gl_account set tax_code_rule = 'OPTIONAL' where tenant_id = @t and account_id = '{s.GrIr}'");
        // the number series of the stock document of a cost adjustment (the series of the stock documents of the test tenant)
        await t.ExecuteAsync("insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) select @t, @c, 'STOCK_DOC_COST_ADJUSTMENT', series_id, date '2026-01-01', 'test' from core.number_series where tenant_id = @t and series_code = 'STK'");
        var det = new AccountDeterminationCommands(t.App);
        foreach (var (role, account) in new[] { ("AP_CONTROL", payable), ("PURCHASE_EXPENSE", expense) })
            Assert.Equal(CommandStatus.Ok, (await det.CreateAsync(t.TenantId, t.Context("det-" + role, "write.config"), new(t.CompanyId, D(("role_code", role), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", account))))).Status);
        var tax = new TaxCommands(t.App);
        var reg = await tax.CreateRegistrationAsync(t.TenantId, t.Context("w-reg", "write.config"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK12345678"), ("scheme", "STANDARD"),
            ("filing_frequency", "QUARTERLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2026-01-01"))));
        Assert.True(reg.Status == CommandStatus.Ok, Describe(reg));
        var code = await tax.CreateCodeAsync(t.TenantId, t.Context("w-c", "write.config"), new(D(("company_id", t.CompanyId), ("code", "I25"), ("description", "I25"), ("tax_type", "INPUT"), ("vat_registration_id", reg.Value!.EntityId))));
        Assert.True(code.Status == CommandStatus.Ok, Describe(code));
        var version = await tax.CreateVersionAsync(t.TenantId, t.Context("w-v", "write.config"), new(code.Value!.EntityId, D(("valid_from", "2026-01-01"), ("vat_account_id", vat), ("rate", 25m)), []));
        Assert.True(version.Status == CommandStatus.Ok, Describe(version));
        var rule = await new TaxDeterminationCommands(t.App).CreateRuleAsync(t.TenantId, t.Context("w-r", "write.config"), new(t.CompanyId,
            D(("valid_from", "2026-01-01"), ("priority", 10), ("tax_code_id", code.Value.EntityId), ("origin_country", "DK"), ("destination_country", "DK")), []));
        Assert.True(rule.Status == CommandStatus.Ok, Describe(rule));
        return new Pur(payable, vat, expense, code.Value.EntityId);
    }

    private static async Task<OrderResult> DomesticPurchaseOrderAsync(S s, string key, decimal qty, decimal price, Guid? item = null, string unit = "C62")
    {
        var r = await s.Orders.CreateAsync(OrderKind.Purchase, s.T.TenantId, Ctx(s, key, "purchase.order"), new(
            D(("company_id", s.T.CompanyId), ("order_date", "2026-03-01"), ("order_currency", "DKK"), ("supplier_partner_id", s.Supplier), ("ship_from_country", "DK")),
            [D(("item_id", item ?? s.Item), ("quantity", qty), ("unit_code", unit), ("unit_price", price), ("warehouse_id", s.Warehouse))]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static Task<CommandResult<PurchaseInvoiceResult>> PostPurchaseInvoice(S s, string key, string number, decimal gross, params PurchaseInvoiceLineInput[] lines) =>
        new PurchaseInvoiceCommands(s.T.App).PostAsync(s.T.TenantId, Ctx(s, key, "purchase.invoice"), new(s.T.CompanyId, s.Supplier, number, new DateOnly(2026, 3, 20), "DKK", gross, lines, SourceDocumentRef: "SUP-REF"));

    private static Task<decimal> SumOfAsync(S s, Guid document, Guid account) =>
        s.T.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and document_id = '{document}' and account_id = '{account}'");

    private static Task<string> ItemStatusAsync(S s, Guid document) =>
        s.T.ScalarAsync<string>($"select oi.clearing_status from core.journal_line_open_item oi join core.journal_line l using (tenant_id, company_id, document_id, line_no) where oi.tenant_id = @t and oi.document_id = '{document}' and l.account_id = '{s.GrIr}'");

    [Fact]
    public async Task APurchaseInvoiceIsMatchedThreeWayAndClearsTheGrIrItemsOfTheReceipts()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        var po = await DomesticPurchaseOrderAsync(s, "po", 100, 10);
        Assert.Equal(["MATCH.QUANTITY_EXCEEDS_RECEIVED"], Codes(await PostPurchaseInvoice(s, "i0", "SI-0", 125, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 10, 10, 100, pur.Code, 25))));   // nothing is received yet
        var receipt = await ReceiveAsync(s, "r1", po, 100);

        // 60 of the 100 at the order price: Dr GR/IR 600, Dr input VAT 150, Cr payable 750
        var r = await PostPurchaseInvoice(s, "i1", "SI-1", 750, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 10, 600, pur.Code, 150));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal((600m, 150m, 750m, "GL-2026-000001"), (m.NetAmount, m.TaxAmount, m.GrossAmount, m.DocumentNumber));
        var line = Assert.Single(m.Lines);
        Assert.Equal((receipt.DocumentId, 1, 60m, "THREE_WAY", 1), (line.ReceiptId, line.ReceiptLineNo, line.BaseQuantity, line.MatchModeUsed, line.JournalLineNo));
        Assert.NotNull(line.GrIrClearingId);
        Assert.Equal((600m, 150m, -750m), (await SumOfAsync(s, m.JournalDocumentId, s.GrIr), await SumOfAsync(s, m.JournalDocumentId, pur.InputVat), await SumOfAsync(s, m.JournalDocumentId, pur.Payable)));
        Assert.Equal(("AP_INVOICE", "ORDER", "purchase-invoices:post"), (
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select origin from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select command_name from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'")));
        Assert.True(await t.ScalarAsync<bool>($"select detail_digest is not null from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"));

        // the GR/IR credit of the receipt is cleared by 600 of its 1000, the invoice debit in full, in one clearing of method AUTO_MATCH
        Assert.Equal(-400m, await BalanceAsync(s, s.GrIr));
        Assert.Equal(("PARTIAL", "CLEARED"), (await ItemStatusAsync(s, receipt.JournalDocumentId!.Value), await ItemStatusAsync(s, m.JournalDocumentId)));
        Assert.Equal(("AUTO_MATCH", 2L), (
            await t.ScalarAsync<string>($"select clearing_method from core.clearing where tenant_id = @t and clearing_id = '{line.GrIrClearingId}'"),
            await t.ScalarAsync<long>($"select count(*) from core.clearing_item where tenant_id = @t and clearing_id = '{line.GrIrClearingId}'")));

        // the supplier invoice number is used once; a wrong control total and too much quantity are rejected without a trace
        long invoices = await t.ScalarAsync<long>("select count(*) from core.purchase_invoice where tenant_id = @t");
        Assert.Equal(["ORDER.DUPLICATE_SUPPLIER_INVOICE"], Codes(await PostPurchaseInvoice(s, "i2", "SI-1", 500, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 40, 10, 400, pur.Code, 100))));
        Assert.Equal(["MATCH.GROSS_MISMATCH"], Codes(await PostPurchaseInvoice(s, "i3", "SI-3", 499, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 40, 10, 400, pur.Code, 100))));
        Assert.Equal(["MATCH.QUANTITY_EXCEEDS_RECEIVED"], Codes(await PostPurchaseInvoice(s, "i5", "SI-5", 512.5m, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 41, 10, 410, pur.Code, 102.5m))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new PurchaseInvoiceCommands(t.App).PostAsync(t.TenantId, Ctx(s, "i6", "sales.invoice"),
            new(t.CompanyId, s.Supplier, "SI-6", new DateOnly(2026, 3, 20), "DKK", 500, [new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 40, 10, 400, pur.Code, 100)]))));
        Assert.Equal(invoices, await t.ScalarAsync<long>("select count(*) from core.purchase_invoice where tenant_id = @t"));
        Assert.Equal(-400m, await BalanceAsync(s, s.GrIr));

        // the rest completes the receipt line: its item is cleared in full and the GR/IR account is empty
        var rest = await PostPurchaseInvoice(s, "i7", "SI-7", 500, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 40, 10, 400, pur.Code, 100));
        Assert.True(rest.Status == CommandStatus.Ok, Describe(rest));
        Assert.Equal(0m, await BalanceAsync(s, s.GrIr));
        Assert.Equal("CLEARED", await ItemStatusAsync(s, receipt.JournalDocumentId!.Value));
        Assert.Equal(["MATCH.QUANTITY_EXCEEDS_RECEIVED"], Codes(await PostPurchaseInvoice(s, "i8", "SI-8", 12.5m, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 1, 10, 10, pur.Code, 2.5m))));

        // the replay and the chains
        var replay = await PostPurchaseInvoice(s, "i1", "SI-1", 750, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 10, 600, pur.Code, 150));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(m.PurchaseInvoiceId, replay.Value!.PurchaseInvoiceId);
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }

    [Fact]
    public async Task APurchaseInvoiceForAServiceIsBookedToTheExpenseAccountWithinTheOrderQuantity()
    {
        var s = await SetupAsync();
        var pur = await PurchaseSetupAsync(s);
        var po = await DomesticPurchaseOrderAsync(s, "po", 10, 500, s.Service, "HUR");
        var r = await PostPurchaseInvoice(s, "i1", "SI-1", 3125, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 5, 500, 2500, pur.Code, 625));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal((2500m, 625m, 3125m), (m.NetAmount, m.TaxAmount, m.GrossAmount));
        Assert.Equal((2500m, 625m, -3125m, "TWO_WAY"), (await SumOfAsync(s, m.JournalDocumentId, pur.Expense), await SumOfAsync(s, m.JournalDocumentId, pur.InputVat), await SumOfAsync(s, m.JournalDocumentId, pur.Payable), m.Lines[0].MatchModeUsed));
        Assert.Null(m.GrIrClearingId);
        Assert.Equal(["MATCH.QUANTITY_EXCEEDS_ORDERED"], Codes(await PostPurchaseInvoice(s, "i2", "SI-2", 3750, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 6, 500, 3000, pur.Code, 750))));   // 5 are invoiced, 10 are ordered
        Assert.Equal(CommandStatus.Ok, (await PostPurchaseInvoice(s, "i3", "SI-3", 3125, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 5, 500, 2500, pur.Code, 625))).Status);
    }

    [Fact]
    public async Task APurchaseInvoiceWithAPriceDifferenceAdjustsTheCostOfTheStockOnHandAndPostsTheConsumedPartToCogs()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        var po = await DomesticPurchaseOrderAsync(s, "po", 100, 10);
        var receipt = await ReceiveAsync(s, "r1", po, 100);   // 1000 in stock and in GR/IR

        // 60 at 11 against 10: 660 against 600, the 60 more is all on hand and raises the cost of the stock
        var r = await PostPurchaseInvoice(s, "i1", "SI-1", 825, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 11, 660, pur.Code, 165));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.NotNull(m.StockDocumentId);
        Assert.Equal((600m, 60m, 0m, 165m, -825m), (await SumOfAsync(s, m.JournalDocumentId, s.GrIr), await SumOfAsync(s, m.JournalDocumentId, s.Inventory), await SumOfAsync(s, m.JournalDocumentId, s.Cogs),
            await SumOfAsync(s, m.JournalDocumentId, pur.InputVat), await SumOfAsync(s, m.JournalDocumentId, pur.Payable)));
        Assert.Equal(60m, m.Lines[0].BaseQuantity - 0m);
        Assert.Equal(("STOCK_COST_ADJUSTMENT", m.JournalDocumentId, "purchase-invoices:post"), (
            await t.ScalarAsync<string>($"select document_type from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<Guid>($"select journal_document_id from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<string>($"select command_name from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'")));
        Assert.Equal(("COST_ADJUST", "INVOICE", 0m, 60m, 1060m, 100m), (
            await t.ScalarAsync<string>($"select movement_type from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<string>($"select cost_source from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select base_quantity from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select value_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select cost_value_after_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select cost_qty_after from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'")));
        Assert.Equal((1060m, -400m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));
        Assert.Equal(("PARTIAL", "CLEARED"), (await ItemStatusAsync(s, receipt.JournalDocumentId!.Value), await ItemStatusAsync(s, m.JournalDocumentId)));

        // 70 of the 100 are sold, so 30 are left; the last 40 are invoiced at 9: 360 against 400, minus 40, of which 30 is on hand and 10 consumed
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 70, 25));
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 13), [new("D1", so.OrderId, 1, 70, "C62")]))).Status);
        var r2 = await PostPurchaseInvoice(s, "i2", "SI-2", 450, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 40, 9, 360, pur.Code, 90));
        Assert.True(r2.Status == CommandStatus.Ok, Describe(r2));
        var m2 = r2.Value!;
        Assert.Equal((400m, -30m, -10m), (await SumOfAsync(s, m2.JournalDocumentId, s.GrIr), await SumOfAsync(s, m2.JournalDocumentId, s.Inventory), await SumOfAsync(s, m2.JournalDocumentId, s.Cogs)));
        Assert.Equal(0m, await BalanceAsync(s, s.GrIr));
        Assert.Equal("CLEARED", await ItemStatusAsync(s, receipt.JournalDocumentId!.Value));
        Assert.Equal((-30m, 288m, 30m), (
            await t.ScalarAsync<decimal>($"select value_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{m2.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select cost_value_after_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{m2.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select cost_qty_after from core.stock_movement where tenant_id = @t and stock_document_id = '{m2.StockDocumentId}'")));
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }

    private static Task<CommandResult<ReversedPurchaseInvoice>> ReversePurchaseInvoice(S s, string key, Guid invoice) =>
        new PurchaseInvoiceCommands(s.T.App).ReverseAsync(s.T.TenantId, Ctx(s, key, "purchase.invoice"), new(s.T.CompanyId, invoice, "CORRECTION"));

    [Fact]
    public async Task APurchaseInvoiceIsReversedWithItsCostAdjustmentAndItsGrIrClearingAndCanBeInvoicedAgain()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        var po = await DomesticPurchaseOrderAsync(s, "po", 100, 10);
        var receipt = await ReceiveAsync(s, "r1", po, 100);
        var m = (await PostPurchaseInvoice(s, "i1", "SI-1", 825, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 11, 660, pur.Code, 165))).Value!;
        Assert.Equal((1060m, -400m, "PARTIAL"), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr), await ItemStatusAsync(s, receipt.JournalDocumentId!.Value)));

        // the receipt cannot be reversed while the invoice is matched to it (11, 7.1)
        Assert.Equal(["ORDER.RECEIPT_INVOICED"], Codes(await s.Cmds.ReverseReceiptAsync(t.TenantId, Ctx(s, "rr", "purchase.receive"), new(t.CompanyId, receipt.DocumentId, "CORRECTION"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new PurchaseInvoiceCommands(t.App).ReverseAsync(t.TenantId, Ctx(s, "x0", "sales.invoice"), new(t.CompanyId, m.PurchaseInvoiceId, "CORRECTION"))));

        var r = await ReversePurchaseInvoice(s, "rev1", m.PurchaseInvoiceId);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var rv = r.Value!;
        Assert.NotNull(rv.StockReversalId);
        // the ledger, the stock and the GR/IR account are where they were before the invoice; the payable is clear
        Assert.Equal((1000m, -1000m, 0m, 0m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr), await BalanceAsync(s, pur.Payable), await BalanceAsync(s, pur.InputVat)));
        Assert.Equal("OPEN", await ItemStatusAsync(s, receipt.JournalDocumentId!.Value));
        Assert.Equal(("AP_INVOICE", "reversed", "STOCK_REVERSAL", "reversed", -60m, 1000m), (
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{rv.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select status from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select document_type from core.stock_document where tenant_id = @t and stock_document_id = '{rv.StockReversalId}'"),
            await t.ScalarAsync<string>($"select status from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select value_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{rv.StockReversalId}'"),
            await t.ScalarAsync<decimal>($"select cost_value_after_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{rv.StockReversalId}'")));
        Assert.Equal(1L, await t.ScalarAsync<long>($"select count(*) from core.purchase_invoice where tenant_id = @t and reversal_of_purchase_invoice_id = '{m.PurchaseInvoiceId}'"));
        Assert.Equal(["REVERSAL.ALREADY_REVERSED"], Codes(await ReversePurchaseInvoice(s, "rev2", m.PurchaseInvoiceId)));

        // the quantity is free again: the same 60 can be invoiced, and the receipt can now be reversed only when nothing is matched
        var again = await PostPurchaseInvoice(s, "i2", "SI-2", 825, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 11, 660, pur.Code, 165));
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        Assert.Equal((1060m, -400m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));
        Assert.True((await ReversePurchaseInvoice(s, "rev1", m.PurchaseInvoiceId)).IdempotentReplay);
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }

    [Fact]
    public async Task ThePurchaseInvoiceIsNotReversedWhenItsCostAdjustmentIsConsumed()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        var po = await DomesticPurchaseOrderAsync(s, "po", 100, 10);
        await ReceiveAsync(s, "r1", po, 100);
        var m = (await PostPurchaseInvoice(s, "i1", "SI-1", 825, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 11, 660, pur.Code, 165))).Value!;
        // all 100 are sold, so the 60 that the invoice added to the cost are gone with the goods
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 100, 25));
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 13), [new("D1", so.OrderId, 1, 100, "C62")]))).Status);
        long journals = await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t");
        Assert.Equal(["INVENTORY.RECEIPT_CONSUMED"], Codes(await ReversePurchaseInvoice(s, "rev1", m.PurchaseInvoiceId)));
        Assert.Equal(journals, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
        Assert.Equal("posted", await t.ScalarAsync<string>($"select status from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"));
        Assert.Equal("CLEARED", await ItemStatusAsync(s, m.JournalDocumentId));   // the reset is rolled back with the rest
    }

    [Fact]
    public async Task ATamperedStockMovementIsFoundByTheVerificationOfTheStockChain()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        var po = await DomesticPurchaseOrderAsync(s, "po", 100, 10);
        await ReceiveAsync(s, "r1", po, 100);
        var m = (await PostPurchaseInvoice(s, "i1", "SI-1", 825, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 11, 660, pur.Code, 165))).Value!;
        var verify = new ChainVerification(t.App);
        var ok = await verify.VerifyAsync(t.TenantId, t.Context("v0", "chain.verify"), new(t.CompanyId, "STOCK"));
        Assert.True(ok.Status == CommandStatus.Ok && ok.Value!.Ok && ok.Value.Checked == 2, Describe(ok));
        await t.ExecuteAsync($"""
            alter table core.stock_movement disable trigger stock_movement_guard;
            update core.stock_movement set value_lc = value_lc + 1 where tenant_id = @t and stock_document_id = '{m.StockDocumentId}' and line_no = 1;
            alter table core.stock_movement enable trigger stock_movement_guard;
            """);
        var r = await verify.VerifyAsync(t.TenantId, t.Context("v1", "chain.verify"), new(t.CompanyId, "STOCK"));
        Assert.Equal(CommandStatus.Ok, r.Status);
        Assert.False(r.Value!.Ok);
        Assert.Equal(2L, r.Value.FirstBreakSeq);
        Assert.Contains(r.Value.Breaks, b => b is { Kind: "digest_mismatch", Code: "STOCK.MOVEMENT_DIGEST_MISMATCH", ChainSeq: 2 });
        Assert.Contains(r.Value.Breaks, b => b.Code == "STOCK.COST_KEY_MISMATCH");   // the row hash binds the stored digest, so it is the digest that shows the change
    }

    private static Task<CommandResult<PurchaseCreditNoteResult>> PostCredit(S s, string key, string number, Guid invoice, decimal gross, params PurchaseCreditLineInput[] lines) =>
        new PurchaseInvoiceCommands(s.T.App).PostCreditNoteAsync(s.T.TenantId, Ctx(s, key, "purchase.invoice"), new(s.T.CompanyId, invoice, "AMOUNT", number, new DateOnly(2026, 3, 25), gross, "PRICE", lines));

    private static Task<long> OpenItemsOfAsync(S s, Guid document, Guid account, string status) =>
        s.T.ScalarAsync<long>($"select count(*) from core.journal_line_open_item oi join core.journal_line l using (tenant_id, company_id, document_id, line_no) where oi.tenant_id = @t and oi.document_id = '{document}' and l.account_id = '{account}' and oi.clearing_status = '{status}'");

    [Fact]
    public async Task AnAmountCreditNoteCorrectsThePriceOfAStockLineAndTheCostOfTheStockOnHand()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        var po = await DomesticPurchaseOrderAsync(s, "po", 100, 10);
        await ReceiveAsync(s, "r1", po, 100);
        var inv = (await PostPurchaseInvoice(s, "i1", "SI-1", 825, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 11, 660, pur.Code, 165))).Value!;
        Assert.Equal(1060m, await BalanceAsync(s, s.Inventory));

        // the supplier corrects 66 of the 660: tax 16.5, all on hand because 100 are in stock
        var r = await PostCredit(s, "c1", "CN-1", inv.PurchaseInvoiceId, 82.5m, new PurchaseCreditLineInput("C1", 1, 66, 16.5m));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal((66m, 16.5m, 82.5m, "AMOUNT"), (m.NetAmount, m.TaxAmount, m.GrossAmount, m.CreditKind));
        Assert.Equal((0m, -66m, 0m, -16.5m, 82.5m), (await SumOfAsync(s, m.JournalDocumentId, s.GrIr), await SumOfAsync(s, m.JournalDocumentId, s.Inventory), await SumOfAsync(s, m.JournalDocumentId, s.Cogs),
            await SumOfAsync(s, m.JournalDocumentId, pur.InputVat), await SumOfAsync(s, m.JournalDocumentId, pur.Payable)));
        Assert.Equal(("AP_CREDIT", "ORDER", "purchase-credit-notes:post"), (
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select origin from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select command_name from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'")));
        Assert.NotNull(m.StockDocumentId);
        Assert.Equal((m.JournalDocumentId, "COST_ADJUST", -66m, 994m, 994m), (
            await t.ScalarAsync<Guid>($"select journal_document_id from core.stock_document where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<string>($"select movement_type from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select value_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select cost_value_after_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{m.StockDocumentId}'"),
            await BalanceAsync(s, s.Inventory)));
        // the two GR/IR lines of the credit are cleared against each other, the payable of the credit is open for the caller to clear
        Assert.Equal((2L, 0m), (await OpenItemsOfAsync(s, m.JournalDocumentId, s.GrIr, "CLEARED"), await BalanceAsync(s, s.GrIr) + 400m));

        // the cap is the net amount of the invoice line less what is credited: 660 - 66 = 594
        Assert.Equal(["ORDER.CREDIT_EXCEEDS_INVOICED"], Codes(await PostCredit(s, "c2", "CN-2", inv.PurchaseInvoiceId, 744m, new PurchaseCreditLineInput("C1", 1, 595, 149m))));
        Assert.Equal(["ORDER.DUPLICATE_SUPPLIER_INVOICE"], Codes(await PostCredit(s, "c3", "CN-1", inv.PurchaseInvoiceId, 12.5m, new PurchaseCreditLineInput("C1", 1, 10, 2.5m))));
        Assert.Equal(["MATCH.GROSS_MISMATCH"], Codes(await PostCredit(s, "c4", "CN-4", inv.PurchaseInvoiceId, 13m, new PurchaseCreditLineInput("C1", 1, 10, 2.5m))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await PostCredit(s, "c5", "CN-5", inv.PurchaseInvoiceId, 12.5m, new PurchaseCreditLineInput("C1", 9, 10, 2.5m))));
        Assert.Equal(["LIMIT.CREDIT_RETURN_NOT_BUILT"], Codes(await new PurchaseInvoiceCommands(t.App).PostCreditNoteAsync(t.TenantId, Ctx(s, "c6", "purchase.invoice"),
            new(t.CompanyId, inv.PurchaseInvoiceId, "RETURN", "CN-6", new DateOnly(2026, 3, 25), 12.5m, "PRICE", [new PurchaseCreditLineInput("C1", 1, 10, 2.5m)]))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new PurchaseInvoiceCommands(t.App).PostCreditNoteAsync(t.TenantId, Ctx(s, "c7", "sales.invoice"),
            new(t.CompanyId, inv.PurchaseInvoiceId, "AMOUNT", "CN-7", new DateOnly(2026, 3, 25), 12.5m, "PRICE", [new PurchaseCreditLineInput("C1", 1, 10, 2.5m)]))));
        // the rest, 594, can be credited, and the invoice cannot be reversed while a credit note stands
        Assert.Equal(CommandStatus.Ok, (await PostCredit(s, "c8", "CN-8", inv.PurchaseInvoiceId, 742.5m, new PurchaseCreditLineInput("C1", 1, 594, 148.5m))).Status);
        Assert.Equal(["ORDER.INVOICE_HAS_CREDIT_NOTES"], Codes(await ReversePurchaseInvoice(s, "rev", inv.PurchaseInvoiceId)));
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }

    [Fact]
    public async Task AnAmountCreditNoteOfAServiceCreditsTheExpenseAndOfSoldStockTheCostOfGoodsSold()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        var svc = await DomesticPurchaseOrderAsync(s, "pos", 10, 500, s.Service, "HUR");
        var si = (await PostPurchaseInvoice(s, "i1", "SI-1", 3125, new PurchaseInvoiceLineInput("L1", svc.OrderId, 1, 5, 500, 2500, pur.Code, 625))).Value!;
        var r = await PostCredit(s, "c1", "CN-1", si.PurchaseInvoiceId, 125m, new PurchaseCreditLineInput("C1", 1, 100, 25m));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal((-100m, -25m, 125m), (await SumOfAsync(s, r.Value!.JournalDocumentId, pur.Expense), await SumOfAsync(s, r.Value.JournalDocumentId, pur.InputVat), await SumOfAsync(s, r.Value.JournalDocumentId, pur.Payable)));
        Assert.Null(r.Value.StockDocumentId);

        // stock that is sold: the correction has nothing on hand to go to and is cost of goods sold
        var po = await DomesticPurchaseOrderAsync(s, "po", 100, 10);
        await ReceiveAsync(s, "r1", po, 100);
        var inv = (await PostPurchaseInvoice(s, "i2", "SI-2", 825, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 11, 660, pur.Code, 165))).Value!;
        var so = await SalesOrderAsync(s, "so", GoodsLine(s, 100, 25));
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.PostDeliveryAsync(t.TenantId, Ctx(s, "d1", "sales.deliver"), new(t.CompanyId, new DateOnly(2026, 3, 13), [new("D1", so.OrderId, 1, 100, "C62")]))).Status);
        var c = await PostCredit(s, "c2", "CN-2", inv.PurchaseInvoiceId, 75m, new PurchaseCreditLineInput("C1", 1, 60, 15m));
        Assert.True(c.Status == CommandStatus.Ok, Describe(c));
        Assert.Equal((0m, -60m, -15m, 75m), (await SumOfAsync(s, c.Value!.JournalDocumentId, s.Inventory), await SumOfAsync(s, c.Value.JournalDocumentId, s.Cogs),
            await SumOfAsync(s, c.Value.JournalDocumentId, pur.InputVat), await SumOfAsync(s, c.Value.JournalDocumentId, pur.Payable)));
        Assert.Null(c.Value.StockDocumentId);

        // an invoice that is reversed cannot be credited
        var third = (await PostPurchaseInvoice(s, "i3", "SI-3", 3125, new PurchaseInvoiceLineInput("L1", svc.OrderId, 1, 5, 500, 2500, pur.Code, 625))).Value!;
        Assert.Equal(CommandStatus.Ok, (await ReversePurchaseInvoice(s, "rev", third.PurchaseInvoiceId)).Status);
        Assert.Equal(["ORDER.INVOICE_REVERSED"], Codes(await PostCredit(s, "c3", "CN-3", third.PurchaseInvoiceId, 125m, new PurchaseCreditLineInput("C1", 1, 100, 25m))));
    }

    private static Task<CommandResult<ReversedPurchaseCreditNote>> ReverseCredit(S s, string key, Guid credit) =>
        new PurchaseInvoiceCommands(s.T.App).ReverseCreditNoteAsync(s.T.TenantId, Ctx(s, key, "purchase.invoice"), new(s.T.CompanyId, credit, "CORRECTION"));

    [Fact]
    public async Task APurchaseCreditNoteIsReversedWithItsCostAdjustmentRestoresTheCapAndLetsTheInvoiceBeReversed()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        var po = await DomesticPurchaseOrderAsync(s, "po", 100, 10);
        await ReceiveAsync(s, "r1", po, 100);
        var inv = (await PostPurchaseInvoice(s, "i1", "SI-1", 825, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 11, 660, pur.Code, 165))).Value!;
        var c = (await PostCredit(s, "c1", "CN-1", inv.PurchaseInvoiceId, 82.5m, new PurchaseCreditLineInput("C1", 1, 66, 16.5m))).Value!;
        Assert.Equal((994m, 82.5m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, pur.Payable) + 825m));

        // the types are kept apart: an invoice is not a credit note and the other way round
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await ReverseCredit(s, "x1", inv.PurchaseInvoiceId)));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await ReversePurchaseInvoice(s, "x2", c.CreditNoteId)));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new PurchaseInvoiceCommands(t.App).ReverseCreditNoteAsync(t.TenantId, Ctx(s, "x3", "sales.invoice"), new(t.CompanyId, c.CreditNoteId, "CORRECTION"))));

        var r = await ReverseCredit(s, "rev1", c.CreditNoteId);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var rv = r.Value!;
        Assert.NotNull(rv.StockReversalId);
        // the ledger and the stock are as before the credit note, and the GR/IR pair of the credit is cleared again by the reversal
        Assert.Equal((1060m, 0m, 0m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, pur.Payable) + 825m, await BalanceAsync(s, pur.InputVat) - 165m));
        Assert.Equal(("AP_CREDIT", "reversed", "STOCK_REVERSAL", "reversed", 66m, 1060m), (
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{rv.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select status from core.journal_document where tenant_id = @t and document_id = '{c.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select document_type from core.stock_document where tenant_id = @t and stock_document_id = '{rv.StockReversalId}'"),
            await t.ScalarAsync<string>($"select status from core.stock_document where tenant_id = @t and stock_document_id = '{c.StockDocumentId}'"),
            await t.ScalarAsync<decimal>($"select value_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{rv.StockReversalId}'"),
            await t.ScalarAsync<decimal>($"select cost_value_after_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{rv.StockReversalId}'")));
        Assert.Equal(["REVERSAL.ALREADY_REVERSED"], Codes(await ReverseCredit(s, "rev2", c.CreditNoteId)));
        Assert.True((await ReverseCredit(s, "rev1", c.CreditNoteId)).IdempotentReplay);

        // the cap is whole again: all 660 can be credited, and then the invoice is reversed after the credit notes
        var all = await PostCredit(s, "c2", "CN-2", inv.PurchaseInvoiceId, 825m, new PurchaseCreditLineInput("C1", 1, 660, 165m));
        Assert.True(all.Status == CommandStatus.Ok, Describe(all));
        Assert.Equal(["ORDER.INVOICE_HAS_CREDIT_NOTES"], Codes(await ReversePurchaseInvoice(s, "rev3", inv.PurchaseInvoiceId)));
        Assert.Equal(CommandStatus.Ok, (await ReverseCredit(s, "rev4", all.Value!.CreditNoteId)).Status);
        var back = await ReversePurchaseInvoice(s, "rev5", inv.PurchaseInvoiceId);
        Assert.True(back.Status == CommandStatus.Ok, Describe(back));
        Assert.Equal((1000m, -1000m, 0m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr), await BalanceAsync(s, pur.Payable)));
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }

    /// <summary>The counter account of a write-off (an expense account without open items), its determination and the series of the document type (the series of the supplier invoices).</summary>
    private static async Task<Guid> WriteOffSetupAsync(S s)
    {
        var t = s.T;
        Guid account = await AccountAsync(t, "WO-ACC", "EXPENSE", "NONE");
        Assert.Equal(CommandStatus.Ok, (await new AccountDeterminationCommands(t.App).CreateAsync(t.TenantId, t.Context("det-wo", "write.config"),
            new(t.CompanyId, D(("role_code", "GR_IR_WRITE_OFF"), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", account))))).Status);
        await t.ExecuteAsync("insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) select @t, @c, 'GR_IR_WRITE_OFF', series_id, date '2026-01-01', 'test' from core.number_series_assignment where tenant_id = @t and document_type = 'AP_INVOICE' limit 1");
        return account;
    }

    private static Task<CommandResult<GrIrWriteOffResult>> WriteOff(S s, string key, decimal expected, Guid order, string reason = "NO_INVOICE") =>
        new PurchaseInvoiceCommands(s.T.App).WriteOffAsync(s.T.TenantId, Ctx(s, key, "purchase.invoice"), new(s.T.CompanyId, new DateOnly(2026, 4, 1), reason, "never invoiced", [new GrIrWriteOffItem(expected, PurchaseOrderId: order, PurchaseOrderLineNo: 1)]));

    private static Task<CommandResult<ReversedGrIrWriteOff>> ReverseWriteOff(S s, string key, Guid document, string reason = "CORRECTION") =>
        new PurchaseInvoiceCommands(s.T.App).ReverseWriteOffAsync(s.T.TenantId, Ctx(s, key, "purchase.invoice"), new(s.T.CompanyId, document, reason));

    [Fact]
    public async Task AGrIrResidualIsWrittenOffAgainstTheWriteOffAccountAndTheWriteOffCanBeReversed()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        Guid wo = await WriteOffSetupAsync(s);
        var po = await DomesticPurchaseOrderAsync(s, "po", 100, 10);
        var receipt = await ReceiveAsync(s, "r1", po, 100);
        var inv = (await PostPurchaseInvoice(s, "i1", "SI-1", 750, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 10, 600, pur.Code, 150))).Value!;
        Assert.Equal(-400m, await BalanceAsync(s, s.GrIr));   // 400 of the 1000 received are never invoiced

        Assert.Equal(["ORDER.GRIR_RESIDUAL_CHANGED"], Codes(await WriteOff(s, "w1", -300, po.OrderId)));
        Assert.Equal(["ORDER.GRIR_ITEM_INVALID"], Codes(await new PurchaseInvoiceCommands(t.App).WriteOffAsync(t.TenantId, Ctx(s, "w2", "purchase.invoice"),
            new(t.CompanyId, new DateOnly(2026, 4, 1), "X", "x", [new GrIrWriteOffItem(0m, OpenItems: [new GrIrItemRef(inv.JournalDocumentId, 1)])]))));   // the invoice item is cleared
        Assert.Equal(["ORDER.GRIR_ITEM_INVALID"], Codes(await new PurchaseInvoiceCommands(t.App).WriteOffAsync(t.TenantId, Ctx(s, "w3", "purchase.invoice"),
            new(t.CompanyId, new DateOnly(2026, 4, 1), "X", "x", [new GrIrWriteOffItem(-400m, ReceiptRef: new GrIrDirectReceiptRef(Guid.NewGuid(), 1))]))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await new PurchaseInvoiceCommands(t.App).WriteOffAsync(t.TenantId, Ctx(s, "w4", "purchase.invoice"),
            new(t.CompanyId, new DateOnly(2026, 4, 1), "X", "x", [new GrIrWriteOffItem(-400m)]))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new PurchaseInvoiceCommands(t.App).WriteOffAsync(t.TenantId, Ctx(s, "w5", "sales.invoice"),
            new(t.CompanyId, new DateOnly(2026, 4, 1), "X", "x", [new GrIrWriteOffItem(-400m, PurchaseOrderId: po.OrderId, PurchaseOrderLineNo: 1)]))));

        var r = await WriteOff(s, "w6", -400, po.OrderId);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal((-400m, 1), (m.TotalResidualLc, m.ItemCount));
        Assert.Equal(("GR_IR_WRITE_OFF", "ORDER", "gr-ir:write-off"), (
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select origin from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select command_name from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'")));
        Assert.Equal((0m, -400m, "CLEARED"), (await BalanceAsync(s, s.GrIr), await BalanceAsync(s, wo), await ItemStatusAsync(s, receipt.JournalDocumentId!.Value)));
        Assert.Equal(["ORDER.GRIR_ITEM_INVALID"], Codes(await WriteOff(s, "w7", 0, po.OrderId)));   // nothing is left to write off

        // the reversal: the reason is CORRECTION or ERROR, the document is a write-off, and it is reversed once
        Assert.Equal(["REVERSAL.REASON_NOT_ALLOWED"], Codes(await ReverseWriteOff(s, "x1", m.JournalDocumentId, "AUTO_REVERSAL")));
        Assert.Equal(["ORDER.GRIR_WRITE_OFF_NOT_FOUND"], Codes(await ReverseWriteOff(s, "x2", inv.JournalDocumentId)));
        var rv = await ReverseWriteOff(s, "x3", m.JournalDocumentId);
        Assert.True(rv.Status == CommandStatus.Ok, Describe(rv));
        Assert.Equal((-400m, 0m, "PARTIAL", "reversed"), (await BalanceAsync(s, s.GrIr), await BalanceAsync(s, wo), await ItemStatusAsync(s, receipt.JournalDocumentId!.Value),
            await t.ScalarAsync<string>($"select status from core.journal_document where tenant_id = @t and document_id = '{m.JournalDocumentId}'")));
        Assert.Equal("GR_IR_WRITE_OFF", await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{rv.Value!.ReversalDocumentId}'"));
        Assert.Equal(["ORDER.GRIR_WRITE_OFF_REVERSED"], Codes(await ReverseWriteOff(s, "x4", m.JournalDocumentId)));
        Assert.True((await ReverseWriteOff(s, "x3", m.JournalDocumentId)).IdempotentReplay);

        // the residual is open again and can be written off again
        var again = await WriteOff(s, "w8", -400, po.OrderId);
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        Assert.Equal(0m, await BalanceAsync(s, s.GrIr));
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }

    private static async Task<StockDocumentResult> DirectReceiptAsync(S s, string key, decimal qty, decimal price)
    {
        var r = await new StockCommands(s.T.App).PostAsync(s.T.TenantId, Ctx(s, key, "stock.post"),
            new PostStockDocumentRequest(s.T.CompanyId, "RECEIPT", new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10), [new StockLineInput("D1", s.Item, s.Warehouse, qty, "C62", UnitPrice: price)]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static PurchaseInvoiceLineInput DirectLine(Guid stockDocument, decimal qty, decimal price, decimal net, Guid code, decimal tax) =>
        new("L1", Guid.Empty, 0, qty, price, net, code, tax, ReceiptRef: new GrIrDirectReceiptRef(stockDocument, 1));

    [Fact]
    public async Task AReceiptWithoutAnOrderIsMatchedByItsReceiptReferenceAndItsResidualCanBeWrittenOff()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        Guid wo = await WriteOffSetupAsync(s);
        var receipt = await DirectReceiptAsync(s, "sr1", 100, 10);   // 1000 in stock and in GR/IR, no order
        Assert.Equal(-1000m, await BalanceAsync(s, s.GrIr));

        // 60 at 11: the match is as for an order line, the difference of 60 is on hand, and no order exists
        var r = await PostPurchaseInvoice(s, "i1", "SI-1", 825, DirectLine(receipt.StockDocumentId, 60, 11, 660, pur.Code, 165));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var m = r.Value!;
        Assert.Equal((1060m, -400m, "PARTIAL"), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr), await ItemStatusAsync(s, receipt.JournalDocumentId!.Value)));
        Assert.Equal((true, true, receipt.StockDocumentId, 1L), (
            await t.ScalarAsync<bool>($"select purchase_order_id is null and receipt_id is null from core.purchase_invoice_line where tenant_id = @t and purchase_invoice_id = '{m.PurchaseInvoiceId}'"),
            await t.ScalarAsync<bool>($"select price_difference_lc = 60 and on_hand_part_lc = 60 from core.purchase_invoice_line where tenant_id = @t and purchase_invoice_id = '{m.PurchaseInvoiceId}'"),
            await t.ScalarAsync<Guid>($"select stock_document_id from core.purchase_invoice_line where tenant_id = @t and purchase_invoice_id = '{m.PurchaseInvoiceId}'"),
            await t.ScalarAsync<long>($"select stock_document_line_no from core.purchase_invoice_line where tenant_id = @t and purchase_invoice_id = '{m.PurchaseInvoiceId}'")));

        // the limits of the receipt line, the two reference forms, the credit and the reversal of the receipt
        Assert.Equal(["MATCH.QUANTITY_EXCEEDS_RECEIVED"], Codes(await PostPurchaseInvoice(s, "i2", "SI-2", 512.5m, DirectLine(receipt.StockDocumentId, 41, 10, 410, pur.Code, 102.5m))));
        Assert.Equal(["MATCH.NO_RECEIPT"], Codes(await PostPurchaseInvoice(s, "i3", "SI-3", 125, DirectLine(Guid.NewGuid(), 10, 10, 100, pur.Code, 25))));
        var po = await DomesticPurchaseOrderAsync(s, "po", 10, 10);
        Assert.Equal(["ORDER.INVOICE_LINE_REFERENCE_MISSING"], Codes(await PostPurchaseInvoice(s, "i4", "SI-4", 125, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 10, 10, 100, pur.Code, 25, ReceiptRef: new GrIrDirectReceiptRef(receipt.StockDocumentId, 1)))));
        Assert.Equal(["ORDER.INVOICE_LINE_REFERENCE_MISSING"], Codes(await PostPurchaseInvoice(s, "i5", "SI-5", 125, new PurchaseInvoiceLineInput("L1", Guid.Empty, 0, 10, 10, 100, pur.Code, 25))));
        // a credit note against the line of a receipt without an order: the price correction goes to the stock on hand as for an order line, and the invoice waits for its credit note to be reversed
        var credit = await PostCredit(s, "c1", "CN-1", m.PurchaseInvoiceId, 82.5m, new PurchaseCreditLineInput("C1", 1, 66, 16.5m));
        Assert.True(credit.Status == CommandStatus.Ok, Describe(credit));
        Assert.Equal((994m, -66m, 66m), (await BalanceAsync(s, s.Inventory), await SumOfAsync(s, credit.Value!.JournalDocumentId, s.Inventory), await t.ScalarAsync<decimal>($"select -value_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{credit.Value.StockDocumentId}'")));
        Assert.Equal(["ORDER.CREDIT_EXCEEDS_INVOICED"], Codes(await PostCredit(s, "c2", "CN-2", m.PurchaseInvoiceId, 744m, new PurchaseCreditLineInput("C1", 1, 595, 149m))));
        Assert.Equal(["ORDER.INVOICE_HAS_CREDIT_NOTES"], Codes(await ReversePurchaseInvoice(s, "rev0", m.PurchaseInvoiceId)));
        Assert.Equal(CommandStatus.Ok, (await ReverseCredit(s, "revc", credit.Value.CreditNoteId)).Status);
        Assert.Equal(["ORDER.RECEIPT_INVOICED"], Codes(await new StockCommands(t.App).ReverseAsync(t.TenantId, Ctx(s, "sr", "stock.reverse"), new(t.CompanyId, receipt.StockDocumentId, "CORRECTION"))));

        // the invoice is reversed with its adjustment and the GR/IR clearing, then matched again
        var rev = await ReversePurchaseInvoice(s, "rev1", m.PurchaseInvoiceId);
        Assert.True(rev.Status == CommandStatus.Ok, Describe(rev));
        Assert.Equal((1000m, -1000m, "OPEN"), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr), await ItemStatusAsync(s, receipt.JournalDocumentId!.Value)));
        var again = await PostPurchaseInvoice(s, "i6", "SI-6", 825, DirectLine(receipt.StockDocumentId, 60, 11, 660, pur.Code, 165));
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));

        // what is never invoiced is written off through the receipt reference
        var wr = await new PurchaseInvoiceCommands(t.App).WriteOffAsync(t.TenantId, Ctx(s, "w1", "purchase.invoice"),
            new(t.CompanyId, new DateOnly(2026, 4, 1), "NO_INVOICE", "never invoiced", [new GrIrWriteOffItem(-400m, ReceiptRef: new GrIrDirectReceiptRef(receipt.StockDocumentId, 1))]));
        Assert.True(wr.Status == CommandStatus.Ok, Describe(wr));
        Assert.Equal((0m, -400m), (await BalanceAsync(s, s.GrIr), await BalanceAsync(s, wo)));
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }

    [Fact]
    public async Task AnInvoiceBeforeReceiptWaitsInGrIrAndTheReceiptsCoverItAtTheInvoiceValue()
    {
        var s = await SetupAsync();
        var t = s.T;
        var pur = await PurchaseSetupAsync(s);
        await t.ExecuteAsync("insert into core.company_order_setting_version (tenant_id, company_id, effective_from, allow_invoice_before_receipt, created_by) values (@t, @c, date '2026-01-01', true, 'test')");
        var created = await s.Orders.CreateAsync(OrderKind.Purchase, t.TenantId, Ctx(s, "po", "purchase.order"), new(
            D(("company_id", t.CompanyId), ("order_date", "2026-03-01"), ("order_currency", "DKK"), ("supplier_partner_id", s.Supplier), ("ship_from_country", "DK")),
            [D(("item_id", s.Item), ("quantity", 100), ("unit_code", "C62"), ("unit_price", 10), ("warehouse_id", s.Warehouse), ("match_mode", "TWO_WAY"))]));
        Assert.True(created.Status == CommandStatus.Ok, Describe(created));
        var po = created.Value!;

        // 60 of the 100 are invoiced at 11 before anything is received: Dr GR/IR 660 waits, no stock moves, no difference is posted
        var r = await PostPurchaseInvoice(s, "i1", "SI-1", 825, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 60, 11, 660, pur.Code, 165));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var inv = r.Value!;
        Assert.Null(inv.StockDocumentId);
        Assert.Equal((660m, 660m, 0m, 660m), (await SumOfAsync(s, inv.JournalDocumentId, s.GrIr), await BalanceAsync(s, s.GrIr), await BalanceAsync(s, s.Inventory),
            await t.ScalarAsync<decimal>($"select unreceived_gr_ir_lc from core.purchase_invoice_line where tenant_id = @t and purchase_invoice_id = '{inv.PurchaseInvoiceId}'")));
        Assert.Equal("OPEN", await ItemStatusAsync(s, inv.JournalDocumentId));
        Assert.Equal(["MATCH.QUANTITY_EXCEEDS_ORDERED"], Codes(await PostPurchaseInvoice(s, "i2", "SI-2", 512.5m, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 41, 10, 410, pur.Code, 102.5m))));

        // 40 are received: they cover 40 of the 60 at the invoice value 440, and the receipt clears that part of the invoice's GR/IR debit
        var first = await ReceiveAsync(s, "r1", po, 40);
        Assert.Equal((440m, 440m, 220m, "PARTIAL"), (first.Lines[0].ValueLc ?? 0m, await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr), await ItemStatusAsync(s, inv.JournalDocumentId)));
        Assert.Equal(("INVOICE", 40m), (
            await t.ScalarAsync<string>($"select cost_source from core.stock_movement where tenant_id = @t and stock_document_id = '{first.StockDocumentId}' and line_no = 1"),
            await t.ScalarAsync<decimal>("select coalesce(sum(covered_quantity), 0) from core.purchase_invoice_coverage where tenant_id = @t")));

        // 60 more: 20 cover the rest of the invoice at its value (220), 40 are at the order price (400); the line is split in two movements
        var second = await ReceiveAsync(s, "r2", po, 60, "2026-03-11");
        Assert.Equal((620m, 1060m, -400m, "CLEARED"), (second.Lines[0].ValueLc ?? 0m, await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr), await ItemStatusAsync(s, inv.JournalDocumentId)));
        Assert.Equal((2L, "INVOICE", 220m, "COMPUTED", 400m), (
            await t.ScalarAsync<long>($"select stock_document_line_count from core.purchase_receipt_line where tenant_id = @t and receipt_id = '{second.DocumentId}'"),
            await t.ScalarAsync<string>($"select cost_source from core.stock_movement where tenant_id = @t and stock_document_id = '{second.StockDocumentId}' and line_no = 1"),
            await t.ScalarAsync<decimal>($"select value_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{second.StockDocumentId}' and line_no = 1"),
            await t.ScalarAsync<string>($"select cost_source from core.stock_movement where tenant_id = @t and stock_document_id = '{second.StockDocumentId}' and line_no = 2"),
            await t.ScalarAsync<decimal>($"select value_lc from core.stock_movement where tenant_id = @t and stock_document_id = '{second.StockDocumentId}' and line_no = 2")));

        // the last 40 are matched to the units that no invoice covered, at the order price: no difference and GR/IR is empty
        var rest = await PostPurchaseInvoice(s, "i3", "SI-3", 500, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 40, 10, 400, pur.Code, 100));
        Assert.True(rest.Status == CommandStatus.Ok, Describe(rest));
        Assert.Equal((0m, 1060m), (await BalanceAsync(s, s.GrIr), await BalanceAsync(s, s.Inventory)));
        Assert.Equal(["MATCH.QUANTITY_EXCEEDS_ORDERED"], Codes(await PostPurchaseInvoice(s, "i4", "SI-4", 12.5m, new PurchaseInvoiceLineInput("L1", po.OrderId, 1, 1, 10, 10, pur.Code, 2.5m))));

        // the receipt that covered is reversed (after the invoice that matched it): the coverage is given back and the invoice waits again for those 20
        Assert.Equal(["ORDER.RECEIPT_INVOICED"], Codes(await s.Cmds.ReverseReceiptAsync(t.TenantId, Ctx(s, "rr0", "purchase.receive"), new(t.CompanyId, second.DocumentId, "CORRECTION"))));
        Assert.Equal(CommandStatus.Ok, (await ReversePurchaseInvoice(s, "rev3", rest.Value!.PurchaseInvoiceId)).Status);
        var rv = await s.Cmds.ReverseReceiptAsync(t.TenantId, Ctx(s, "rr1", "purchase.receive"), new(t.CompanyId, second.DocumentId, "CORRECTION"));
        Assert.True(rv.Status == CommandStatus.Ok, Describe(rv));
        Assert.Equal((440m, 220m, "PARTIAL", 40m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr), await ItemStatusAsync(s, inv.JournalDocumentId),
            await t.ScalarAsync<decimal>("select coalesce(sum(covered_quantity), 0) from core.purchase_invoice_coverage where tenant_id = @t")));
        var again = await ReceiveAsync(s, "r3", po, 60, "2026-03-12");
        Assert.Equal((620m, "CLEARED"), (again.Lines[0].ValueLc ?? 0m, await ItemStatusAsync(s, inv.JournalDocumentId)));
        foreach (string chain in new[] { "LEDGER", "CLEARING", "STOCK" })
        {
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + chain, "chain.verify"), new(t.CompanyId, chain));
            Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, chain + " " + Describe(verify));
        }
    }
}
