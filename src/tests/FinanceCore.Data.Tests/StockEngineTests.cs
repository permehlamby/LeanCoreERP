using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using static FinanceCore.Data.Tests.StockTests;

namespace FinanceCore.Data.Tests;

/// <summary>The rest of the stock ledger (10; F-IMPL-96): lot and serial tracking, negative stock, transfers and status changes, the cost scope, and FIFO with its layers.</summary>
[Collection("db")]
public class StockEngineTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);

    private sealed record E(S S, Guid NegVariance, Guid Lcc);

    private static async Task<E> SetupAsync()
    {
        var s = await StockTests.SetupAsync();
        var t = s.T;
        await t.ExecuteAsync("""
            insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by)
              select @t, @c, v.ty, (select series_id from core.number_series where tenant_id = @t and series_code = v.sc), date '2026-01-01', 'test'
              from (values ('STOCK_DOC_TRANSFER', 'STK'), ('STOCK_DOC_STATUS_CHANGE', 'STK'), ('STOCK_TRANSFER', 'STJ'), ('STOCK_DOC_REVERSAL', 'STK'), ('STOCK_REVERSAL', 'STJ'), ('STOCK_DOC_COST_ADJUSTMENT', 'STK'), ('STOCK_COST_ADJUSTMENT', 'STJ')) v(ty, sc);
            """);
        Guid neg = await AccountAsync(t, "STK-NEG", "EXPENSE", "NONE");
        var det = await new AccountDeterminationCommands(t.App).CreateAsync(t.TenantId, t.Context("det-neg", "write.config"), new(t.CompanyId, D(("role_code", "NEG_STOCK_VARIANCE"), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", neg))));
        Assert.True(det.Status == CommandStatus.Ok, Describe(det));
        Guid lcc = await AccountAsync(t, "STK-LCC", "EXPENSE", "NONE");
        var det2 = await new AccountDeterminationCommands(t.App).CreateAsync(t.TenantId, t.Context("det-lcc", "write.config"), new(t.CompanyId, D(("role_code", "LANDED_COST_CLEARING"), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", lcc))));
        Assert.True(det2.Status == CommandStatus.Ok, Describe(det2));
        return new E(s, neg, lcc);
    }

    private static async Task<Guid> ItemAsync(S s, string no, string valuation = "MOVING_AVERAGE", string tracking = "NONE", bool expiry = false, bool allowNegative = false, Guid? warehouse = null)
    {
        var t = s.T;
        Guid item = await MasterAsync(t, "items", "i" + no, D(("item_no", no), ("name", no), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", s.Group), ("tracking_mode", tracking), ("expiry_tracked", expiry)));
        await MasterAsync(t, "item-companies", "ic" + no, D(("company_id", t.CompanyId), ("item_id", item), ("valuation_method", valuation)));
        if (allowNegative)
            await MasterAsync(t, "item-warehouses", "iw" + no, D(("company_id", t.CompanyId), ("warehouse_id", warehouse ?? s.Warehouse), ("item_id", item), ("allow_negative_stock", true)));
        return item;
    }

    private static StockLineInput L(Guid item, Guid warehouse, decimal qty, decimal? price = null, string @ref = "L1", StockTracking? tracking = null, Guid? location = null, string? status = null,
        Guid? toWarehouse = null, Guid? toLocation = null, string? from = null, string? to = null) =>
        new(@ref, item, warehouse, qty, "C62", location, price, Tracking: tracking, StockStatus: status, ToWarehouseId: toWarehouse, ToLocationId: toLocation, FromStatus: from, ToStatus: to);

    private static async Task<CommandResult<StockDocumentResult>> Post(S s, string key, string type, string date, string? reason, params StockLineInput[] lines) =>
        await s.Cmds.PostAsync(s.T.TenantId, Stock(s.T, key), Doc(s, type, date, reason, lines));

    private static async Task<StockDocumentResult> Ok(S s, string key, string type, string date, string? reason, params StockLineInput[] lines)
    {
        var r = await Post(s, key, type, date, reason, lines);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static async Task VerifyStockAsync(S s)
    {
        var v = await new ChainVerification(s.T.App).VerifyAsync(s.T.TenantId, s.T.Context("v" + Guid.NewGuid().ToString("N")[..6], "chain.verify"), new(s.T.CompanyId, "STOCK"));
        Assert.True(v.Status == CommandStatus.Ok && v.Value!.Ok, Describe(v) + " " + string.Join("; ", v.Value?.Breaks.Select(b => b.Code + " " + b.Detail) ?? []));
    }

    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task LotsHaveTheirOwnCostKeyAndExpiredAndRecalledLotsAreHandled()
    {
        var e = await SetupAsync();
        var s = e.S; var t = s.T;
        Guid lot = await ItemAsync(s, "LOT-1", tracking: "LOT", expiry: true);
        var a = new StockTracking("A1", new DateOnly(2026, 3, 1), SupplierRef: "SUP-A"); var b = new StockTracking("B1", new DateOnly(2027, 1, 1));

        Assert.Equal(["INVENTORY.TRACKING_REQUIRED"], Codes(await Post(s, "x1", "RECEIPT", "2026-02-01", null, L(lot, s.Warehouse, 10, 5))));
        Assert.Equal(["INVENTORY.TRACKING_REQUIRED"], Codes(await Post(s, "x2", "RECEIPT", "2026-02-01", null, L(lot, s.Warehouse, 10, 5, tracking: new StockTracking("A1")))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Post(s, "x3", "RECEIPT", "2026-02-01", null, L(s.Item, s.Warehouse, 1, 5, tracking: a))));
        var r1 = await Ok(s, "r1", "RECEIPT", "2026-02-01", null, L(lot, s.Warehouse, 10, 5, tracking: a));
        await Ok(s, "r2", "RECEIPT", "2026-02-02", null, L(lot, s.Warehouse, 10, 7, tracking: b));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.item_tracking where tenant_id = @t and item_id = @i", ("i", lot)));
        Assert.Equal("SUP-A", await t.ScalarAsync<string>("select supplier_ref from core.item_tracking where tenant_id = @t and item_id = @i and tracking_no = 'A1'", ("i", lot)));
        Assert.NotNull(r1.Movements[0].TrackingId);

        // each lot is a cost key of its own: five of lot A leave at 5, five of lot B at 7
        var issueA = await Post(s, "i1", "ISSUE", "2026-02-10", "SALE", L(lot, s.Warehouse, 5, tracking: new StockTracking("A1")));
        Assert.True(issueA.Status == CommandStatus.Ok, Describe(issueA));
        Assert.Equal((-25m, 5m, 25m), (issueA.Value!.Movements[0].ValueLc, issueA.Value.Movements[0].CostQtyAfter, issueA.Value.Movements[0].CostValueAfterLc));
        var issueB = await Ok(s, "i2", "ISSUE", "2026-02-11", "SALE", L(lot, s.Warehouse, 5, tracking: new StockTracking("B1")));
        Assert.Equal((-35m, 5m, 35m), (issueB.Movements[0].ValueLc, issueB.Movements[0].CostQtyAfter, issueB.Movements[0].CostValueAfterLc));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Post(s, "i3", "ISSUE", "2026-02-12", "SALE", L(lot, s.Warehouse, 1, tracking: new StockTracking("NOPE")))));
        Assert.Equal(["INVENTORY.INSUFFICIENT_STOCK"], Codes(await Post(s, "i4", "ISSUE", "2026-02-12", "SALE", L(lot, s.Warehouse, 6, tracking: new StockTracking("A1")))));

        // an expired lot can be issued with a warning, and the movement says so (Q15)
        var expired = await Post(s, "i5", "ISSUE", "2026-03-20", "SALE", L(lot, s.Warehouse, 1, tracking: new StockTracking("A1")));
        Assert.True(expired.Status == CommandStatus.Ok, Describe(expired));
        Assert.Contains(expired.Warnings, w => w.Code == "INVENTORY.LOT_EXPIRED" && w.Severity == "WARNING");
        Assert.True(await t.ScalarAsync<bool>("select lot_expired from core.stock_movement where tenant_id = @t and stock_document_id = @d", ("d", expired.Value!.StockDocumentId)));
        Assert.False(await t.ScalarAsync<bool>("select lot_expired from core.stock_movement where tenant_id = @t and stock_document_id = @d", ("d", issueB.StockDocumentId)));

        // a recalled lot blocks issues, and the expiry date of a used lot does not change
        Guid trackingB = await t.ScalarAsync<Guid>("select tracking_id from core.item_tracking where tenant_id = @t and item_id = @i and tracking_no = 'B1'", ("i", lot));
        var trackings = new ItemTrackingCommands(t.App);
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await trackings.SetStatusAsync(t.TenantId, t.Context("rc0", "write.config"), new(trackingB, 7, "RECALLED"))));
        var recalled = await trackings.SetStatusAsync(t.TenantId, t.Context("rc1", "write.config"), new(trackingB, 1, "RECALLED"));
        Assert.True(recalled.Status == CommandStatus.Ok, Describe(recalled));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await trackings.SetStatusAsync(t.TenantId, t.Context("rc2", "write.config"), new(trackingB, 2, "RECALLED"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await trackings.SetStatusAsync(t.TenantId, t.Context("rc3", "stock.post"), new(trackingB, 2, "ACTIVE"))));
        Assert.Equal(["INVENTORY.TRACKING_RECALLED"], Codes(await Post(s, "i6", "ISSUE", "2026-02-13", "SALE", L(lot, s.Warehouse, 1, tracking: new StockTracking("B1")))));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.item_tracking set expiry_date = date '2030-01-01' where tenant_id = @t and item_id = @i and tracking_no = 'A1'", ("i", lot)));
        await VerifyStockAsync(s);
    }

    [Fact]
    public async Task ASerialIsReceivedOnceOneAtATimeAndIssuedOnlyWhenOnHand()
    {
        var e = await SetupAsync();
        var s = e.S;
        Guid serial = await ItemAsync(s, "SER-1", tracking: "SERIAL");
        Assert.Equal(["INVENTORY.SERIAL_QUANTITY_INVALID"], Codes(await Post(s, "a1", "RECEIPT", "2026-02-01", null, L(serial, s.Warehouse, 2, 100, tracking: new StockTracking("S1")))));
        await Ok(s, "a2", "RECEIPT", "2026-02-01", null, L(serial, s.Warehouse, 1, 100, tracking: new StockTracking("S1")), L(serial, s.Warehouse, 1, 110, "L2", new StockTracking("S2")));
        Assert.Equal(["INVENTORY.SERIAL_ON_HAND"], Codes(await Post(s, "a3", "RECEIPT", "2026-02-02", null, L(serial, s.Warehouse, 1, 100, tracking: new StockTracking("S1")))));
        Assert.Equal(["INVENTORY.SERIAL_ON_HAND"], Codes(await Post(s, "a4", "RECEIPT", "2026-02-02", null, L(serial, s.Warehouse, 1, 100, tracking: new StockTracking("S3")), L(serial, s.Warehouse, 1, 100, "L2", new StockTracking("S3")))));
        var issue = await Ok(s, "a5", "ISSUE", "2026-02-03", "SALE", L(serial, s.Warehouse, 1, tracking: new StockTracking("S2")));
        Assert.Equal(-110m, issue.Movements[0].ValueLc);
        Assert.Equal(["INVENTORY.INSUFFICIENT_STOCK"], Codes(await Post(s, "a6", "ISSUE", "2026-02-04", "SALE", L(serial, s.Warehouse, 1, tracking: new StockTracking("S2")))));
        // a serial that left can be received again
        await Ok(s, "a7", "RECEIPT", "2026-02-05", null, L(serial, s.Warehouse, 1, 90, tracking: new StockTracking("S2")));
        await VerifyStockAsync(s);
    }

    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task NegativeStockIsIssuedAtTheLastCostAndSettledByTheNextReceipt()
    {
        var e = await SetupAsync();
        var s = e.S; var t = s.T;
        Guid item = await ItemAsync(s, "NEG-1", allowNegative: true);
        Guid strict = await ItemAsync(s, "NEG-2");
        await Ok(s, "r1", "RECEIPT", "2026-03-01", null, L(item, s.Warehouse, 10, 5));
        await Ok(s, "r2", "RECEIPT", "2026-03-01", null, L(strict, s.Warehouse, 10, 5));
        Assert.Equal(["INVENTORY.INSUFFICIENT_STOCK"], Codes(await Post(s, "x1", "ISSUE", "2026-03-02", "SALE", L(strict, s.Warehouse, 11))));

        // 15 out of 10: the cost key goes to minus 5 and its value to minus 25, with a warning
        var issue = await Post(s, "i1", "ISSUE", "2026-03-02", "SALE", L(item, s.Warehouse, 15));
        Assert.True(issue.Status == CommandStatus.Ok, Describe(issue));
        Assert.Contains(issue.Warnings, w => w.Code == "INVENTORY.NEGATIVE_STOCK_ISSUED");
        var m = issue.Value!.Movements[0];
        Assert.Equal((-75m, -5m, -25m), (m.ValueLc, m.CostQtyAfter, m.CostValueAfterLc));

        // 20 received at 8: 5 of them cover the negative quantity at 8 (40) against 25 issued, so 15 are posted to the variance account
        var receipt = await Ok(s, "r3", "RECEIPT", "2026-03-03", null, L(item, s.Warehouse, 20, 8));
        Assert.Equal(["RECEIPT", "COST_ADJUST"], receipt.Movements.Select(x => x.MovementType).ToArray());
        Assert.Equal((160m, 15m, 135m), (receipt.Movements[0].ValueLc, receipt.Movements[0].CostQtyAfter, receipt.Movements[0].CostValueAfterLc));
        Assert.Equal((-15m, 15m, 120m), (receipt.Movements[1].ValueLc, receipt.Movements[1].CostQtyAfter, receipt.Movements[1].CostValueAfterLc));
        Assert.Equal(15m, await BalanceAsync(s, e.NegVariance));
        Assert.Equal(120m + 50m, await BalanceAsync(s, s.Inventory));   // the other item holds 50
        await VerifyStockAsync(s);

        // the variance goes with the document when it is reversed
        var reversed = await s.Cmds.ReverseAsync(t.TenantId, Stock(t, "rev") with { Permissions = new HashSet<string> { "stock.reverse" } }, new(t.CompanyId, receipt.StockDocumentId, "CORRECTION"));
        Assert.True(reversed.Status == CommandStatus.Ok, Describe(reversed));
        Assert.Equal(0m, await BalanceAsync(s, e.NegVariance));
        await VerifyStockAsync(s);
    }

    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ATransferMovesTheStockBetweenWarehousesAndPostsOnlyBetweenDifferentInventoryAccounts()
    {
        var e = await SetupAsync();
        var s = e.S; var t = s.T;
        Guid wh2 = await MasterAsync(t, "warehouses", "wh2", D(("company_id", t.CompanyId), ("code", "SECOND"), ("name", "Second"), ("country_code", "DK")));
        Guid transit = await MasterAsync(t, "warehouse-locations", "tr", D(("company_id", t.CompanyId), ("warehouse_id", wh2), ("location_code", "TRANSIT"), ("name", "In transit"), ("location_type", "TRANSIT")));
        Guid quarantine = await MasterAsync(t, "warehouse-locations", "qu", D(("company_id", t.CompanyId), ("warehouse_id", s.Warehouse), ("location_code", "QUAR"), ("name", "Quarantine"), ("location_type", "QUARANTINE")));
        await Ok(s, "r1", "RECEIPT", "2026-03-01", null, L(s.Item, s.Warehouse, 10, 5));

        // the same inventory account in both warehouses: no journal, the cost key does not change
        var tr = await Ok(s, "t1", "TRANSFER", "2026-03-02", null, L(s.Item, s.Warehouse, 4, toWarehouse: wh2));
        Assert.Null(tr.JournalDocumentId);
        Assert.Equal(["TRANSFER_OUT", "TRANSFER_IN"], tr.Movements.Select(m => m.MovementType).ToArray());
        Assert.Equal((-4m, 4m, -20m, 20m), (tr.Movements[0].BaseQuantity, tr.Movements[1].BaseQuantity, tr.Movements[0].ValueLc, tr.Movements[1].ValueLc));
        Assert.Equal((6m, 4m), (tr.Movements[0].StockQtyAfter, tr.Movements[1].StockQtyAfter));
        Assert.Equal((10m, 50m), (tr.Movements[1].CostQtyAfter, tr.Movements[1].CostValueAfterLc));
        Assert.Equal(50m, await BalanceAsync(s, s.Inventory));

        // the stock key must hold what is moved, whatever the total of the company
        Assert.Equal(["INVENTORY.INSUFFICIENT_STOCK"], Codes(await Post(s, "t2", "TRANSFER", "2026-03-03", null, L(s.Item, s.Warehouse, 7, toWarehouse: wh2))));

        // a second inventory account for the second warehouse: the transfer posts between the two
        Guid inv2 = await AccountAsync(t, "STK-INV2", "ASSET", "NONE");
        var det = await new AccountDeterminationCommands(t.App).CreateAsync(t.TenantId, t.Context("det-inv2", "write.config"), new(t.CompanyId, D(("role_code", "INVENTORY"), ("warehouse_id", wh2), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", inv2))));
        Assert.True(det.Status == CommandStatus.Ok, Describe(det));
        var tr2 = await Ok(s, "t3", "TRANSFER", "2026-03-04", null, L(s.Item, s.Warehouse, 2, toWarehouse: wh2));
        Assert.NotNull(tr2.JournalDocumentId);
        Assert.Equal((10m, 40m), (await BalanceAsync(s, inv2), await BalanceAsync(s, s.Inventory)));

        // the location types (3.6): a receipt and an issue refuse TRANSIT and QUARANTINE, a transfer goes in and out of transit
        Assert.Equal(["INVENTORY.LOCATION_NOT_AVAILABLE"], Codes(await Post(s, "l1", "RECEIPT", "2026-03-05", null, L(s.Item, wh2, 1, 5, location: transit))));
        var toTransit = await Ok(s, "l2", "TRANSFER", "2026-03-05", null, L(s.Item, s.Warehouse, 1, toWarehouse: wh2, toLocation: transit));
        Assert.Equal(transit, toTransit.Movements[1].LocationId);
        var outOfTransit = await Ok(s, "l3", "TRANSFER", "2026-03-06", null, L(s.Item, wh2, 1, location: transit, toWarehouse: wh2));
        Assert.Equal<(Guid?, Guid?)>((null, transit), (outOfTransit.Movements[1].LocationId, outOfTransit.Movements[0].LocationId));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Post(s, "l4", "TRANSFER", "2026-03-06", null, L(s.Item, wh2, 1, toWarehouse: wh2))));

        // a status change: into a QUARANTINE location only QUARANTINE or BLOCKED
        Assert.Equal(["INVENTORY.LOCATION_STATUS_MISMATCH"], Codes(await Post(s, "q1", "STATUS_CHANGE", "2026-03-07", null, L(s.Item, s.Warehouse, 1, location: quarantine, from: "BLOCKED", to: "AVAILABLE"))));
        var change = await Ok(s, "q2", "STATUS_CHANGE", "2026-03-07", null, L(s.Item, s.Warehouse, 2, from: "AVAILABLE", to: "QUARANTINE"));
        Assert.Null(change.JournalDocumentId);
        Assert.Equal(["STATUS_OUT", "STATUS_IN"], change.Movements.Select(m => m.MovementType).ToArray());
        Assert.Equal(["AVAILABLE", "QUARANTINE"], change.Movements.Select(m => m.StockStatus).ToArray());
        Assert.Equal(["INVENTORY.INSUFFICIENT_STOCK"], Codes(await Post(s, "q3", "STATUS_CHANGE", "2026-03-08", null, L(s.Item, s.Warehouse, 99, from: "QUARANTINE", to: "AVAILABLE"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Post(s, "q4", "STATUS_CHANGE", "2026-03-08", null, L(s.Item, s.Warehouse, 1, from: "BLOCKED", to: "BLOCKED"))));

        // a transfer is reversed by its owning command and the stock returns
        var reversed = await s.Cmds.ReverseAsync(t.TenantId, Stock(t, "rev") with { Permissions = new HashSet<string> { "stock.reverse" } }, new(t.CompanyId, tr2.StockDocumentId, "CORRECTION"));
        Assert.True(reversed.Status == CommandStatus.Ok, Describe(reversed));
        Assert.Equal((5m, 45m), (await BalanceAsync(s, inv2), await BalanceAsync(s, s.Inventory)));
        await VerifyStockAsync(s);
    }

    [Fact]
    public async Task WithTheCostScopeWarehouseEveryWarehouseHasItsOwnAverageAndATransferChangesTheDestination()
    {
        var e = await SetupAsync();
        var s = e.S; var t = s.T;
        await t.ExecuteAsync("update core.company_group set inventory_cost_scope = 'WAREHOUSE' where tenant_id = @t");
        Guid wh2 = await MasterAsync(t, "warehouses", "wh2", D(("company_id", t.CompanyId), ("code", "SECOND"), ("name", "Second"), ("country_code", "DK")));
        await Ok(s, "r1", "RECEIPT", "2026-03-01", null, L(s.Item, s.Warehouse, 10, 5));
        var r2 = await Ok(s, "r2", "RECEIPT", "2026-03-01", null, L(s.Item, wh2, 10, 9));
        Assert.Equal((10m, 90m), (r2.Movements[0].CostQtyAfter, r2.Movements[0].CostValueAfterLc));
        Assert.Equal(s.Warehouse.ToString(), await t.ScalarAsync<string>("select cost_warehouse_id::text from core.stock_movement m join core.stock_document d on d.stock_document_id = m.stock_document_id where d.tenant_id = @t and d.chain_seq = 1"));
        var tr = await Ok(s, "t1", "TRANSFER", "2026-03-02", null, L(s.Item, s.Warehouse, 5, toWarehouse: wh2));
        Assert.Equal((-5m, -25m, 5m, 25m), (tr.Movements[0].BaseQuantity, tr.Movements[0].ValueLc, tr.Movements[1].BaseQuantity, tr.Movements[1].ValueLc));
        Assert.Equal((5m, 25m, 15m, 115m), (tr.Movements[0].CostQtyAfter, tr.Movements[0].CostValueAfterLc, tr.Movements[1].CostQtyAfter, tr.Movements[1].CostValueAfterLc));
        // the issue from the second warehouse is at its new average (115 / 15)
        var issue = await Ok(s, "i1", "ISSUE", "2026-03-03", "SALE", L(s.Item, wh2, 3));
        Assert.Equal(-23m, issue.Movements[0].ValueLc);
        await VerifyStockAsync(s);
    }

    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task FifoIssuesTakeTheOldestLayersAndAReversalGivesTheQuantityBackToThem()
    {
        var e = await SetupAsync();
        var s = e.S; var t = s.T;
        Guid item = await ItemAsync(s, "FIFO-1", "FIFO");
        var r1 = await Ok(s, "r1", "RECEIPT", "2026-03-01", null, L(item, s.Warehouse, 10, 5));
        var r2 = await Ok(s, "r2", "RECEIPT", "2026-03-02", null, L(item, s.Warehouse, 10, 7));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.stock_cost_layer where tenant_id = @t and item_id = @i", ("i", item)));
        Assert.Equal((1L, 2L), (await t.ScalarAsync<long>("select layer_seq from core.stock_cost_layer where tenant_id = @t and stock_document_id = @d", ("d", r1.StockDocumentId)), await t.ScalarAsync<long>("select layer_seq from core.stock_cost_layer where tenant_id = @t and stock_document_id = @d", ("d", r2.StockDocumentId))));

        // 12 out: the whole first layer (50) and two of the second (14)
        var issue = await Ok(s, "i1", "ISSUE", "2026-03-03", "SALE", L(item, s.Warehouse, 12));
        Assert.Equal((-64m, 8m, 56m), (issue.Movements[0].ValueLc, issue.Movements[0].CostQtyAfter, issue.Movements[0].CostValueAfterLc));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.stock_layer_consumption where tenant_id = @t and stock_document_id = @d", ("d", issue.StockDocumentId)));
        Assert.True(await t.ScalarAsync<bool>("select exhausted from core.stock_cost_layer where tenant_id = @t and stock_document_id = @d", ("d", r1.StockDocumentId)));
        Assert.False(await t.ScalarAsync<bool>("select exhausted from core.stock_cost_layer where tenant_id = @t and stock_document_id = @d", ("d", r2.StockDocumentId)));
        Assert.Equal(64m, await BalanceAsync(s, s.Inventory) * -1m + 120m);   // inventory 120 less the issue
        await VerifyStockAsync(s);

        // a receipt of which something has been consumed cannot be reversed (5.5, B8), also when the quantity would stay positive
        Assert.Equal(["INVENTORY.RECEIPT_CONSUMED"], Codes(await s.Cmds.ReverseAsync(t.TenantId, Stock(t, "rv1") with { Permissions = new HashSet<string> { "stock.reverse" } }, new(t.CompanyId, r1.StockDocumentId, "CORRECTION"))));

        // the issue is reversed at its original value and the layers have their quantity back
        var back = await s.Cmds.ReverseAsync(t.TenantId, Stock(t, "rv2") with { Permissions = new HashSet<string> { "stock.reverse" } }, new(t.CompanyId, issue.StockDocumentId, "CORRECTION"));
        Assert.True(back.Status == CommandStatus.Ok, Describe(back));
        Assert.Equal((64m, 20m, 120m), (back.Value!.Movements[0].ValueLc, back.Value.Movements[0].CostQtyAfter, back.Value.Movements[0].CostValueAfterLc));
        Assert.False(await t.ScalarAsync<bool>("select exhausted from core.stock_cost_layer where tenant_id = @t and stock_document_id = @d", ("d", r1.StockDocumentId)));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.stock_layer_consumption where tenant_id = @t and stock_document_id = @d and quantity_base < 0", ("d", back.Value.StockDocumentId)));
        await VerifyStockAsync(s);

        // the whole stock goes in the order of the layers, and the first receipt can be reversed when nothing of it is used
        var all = await Ok(s, "i2", "ISSUE", "2026-03-04", "SALE", L(item, s.Warehouse, 20));
        Assert.Equal((-120m, 0m, 0m), (all.Movements[0].ValueLc, all.Movements[0].CostQtyAfter, all.Movements[0].CostValueAfterLc));
        Assert.Equal(["INVENTORY.INSUFFICIENT_STOCK"], Codes(await Post(s, "i3", "ISSUE", "2026-03-05", "SALE", L(item, s.Warehouse, 1))));
        await VerifyStockAsync(s);
    }

    [Fact]
    public async Task AFifoReceiptIsReversedWhenNothingOfItsLayerIsUsedAndAnAdjustmentInTakesTheLastLayerCost()
    {
        var e = await SetupAsync();
        var s = e.S; var t = s.T;
        Guid item = await ItemAsync(s, "FIFO-2", "FIFO");
        var adj = (decimal qty, string key) => Post(s, key, "ADJUSTMENT", "2026-03-01", "COUNT", new StockLineInput("L1", item, s.Warehouse, qty, "C62", Adjustment: new StockAdjustment("DELTA", "IN")));
        Assert.Equal(["INVENTORY.VALUE_REQUIRED"], Codes(await adj(2, "a1")));
        var supplied = await Ok(s, "a2", "ADJUSTMENT", "2026-03-01", "COUNT", new StockLineInput("L1", item, s.Warehouse, 4, "C62", UnitPrice: 6m, Adjustment: new StockAdjustment("DELTA", "IN")));
        Assert.Equal(("SUPPLIED", 24m), (supplied.Movements[0].CostSource, supplied.Movements[0].ValueLc));
        var again = await adj(2, "a3");
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        Assert.Equal(12m, again.Value!.Movements[0].ValueLc);   // the unit cost of the last layer
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.stock_cost_layer where tenant_id = @t and item_id = @i", ("i", item)));

        // the second layer is whole, so its adjustment can be reversed; the first is whole too until something is issued
        var issue = await Ok(s, "i1", "ISSUE", "2026-03-02", "SALE", L(item, s.Warehouse, 1));
        Assert.Equal(-6m, issue.Movements[0].ValueLc);
        Assert.Equal(["INVENTORY.RECEIPT_CONSUMED"], Codes(await s.Cmds.ReverseAsync(t.TenantId, Stock(t, "rv1") with { Permissions = new HashSet<string> { "stock.reverse" } }, new(t.CompanyId, supplied.StockDocumentId, "CORRECTION"))));
        var ok = await s.Cmds.ReverseAsync(t.TenantId, Stock(t, "rv2") with { Permissions = new HashSet<string> { "stock.reverse" } }, new(t.CompanyId, again.Value.StockDocumentId, "CORRECTION"));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        Assert.Equal((-12m, 3m, 18m), (ok.Value!.Movements[0].ValueLc, ok.Value.Movements[0].CostQtyAfter, ok.Value.Movements[0].CostValueAfterLc));
        Assert.True(await t.ScalarAsync<bool>("select exhausted from core.stock_cost_layer where tenant_id = @t and stock_document_id = @d", ("d", again.Value.StockDocumentId)));
        await VerifyStockAsync(s);
    }

    [Fact]
    public async Task FifoLayersMoveWithATransferUnderTheCostScopeWarehouseAndKeepTheirDate()
    {
        var e = await SetupAsync();
        var s = e.S; var t = s.T;
        await t.ExecuteAsync("update core.company_group set inventory_cost_scope = 'WAREHOUSE' where tenant_id = @t");
        Guid wh2 = await MasterAsync(t, "warehouses", "wh2", D(("company_id", t.CompanyId), ("code", "SECOND"), ("name", "Second"), ("country_code", "DK")));
        Guid item = await ItemAsync(s, "FIFO-3", "FIFO");
        await Ok(s, "r1", "RECEIPT", "2026-03-01", null, L(item, s.Warehouse, 4, 5));
        await Ok(s, "r2", "RECEIPT", "2026-03-02", null, L(item, s.Warehouse, 4, 7));
        // 6 move: the whole first layer and two of the second; the destination gets one layer for each, with the date of the source layer
        var tr = await Ok(s, "t1", "TRANSFER", "2026-03-10", null, L(item, s.Warehouse, 6, toWarehouse: wh2));
        Assert.Equal(["TRANSFER_OUT", "TRANSFER_IN", "TRANSFER_IN"], tr.Movements.Select(m => m.MovementType).ToArray());
        Assert.Equal((-34m, 20m, 14m), (tr.Movements[0].ValueLc, tr.Movements[1].ValueLc, tr.Movements[2].ValueLc));
        var layers = await t.ScalarAsync<string>("select string_agg(layer_date::text || ':' || quantity_base::text || ':' || value_lc::text, ',' order by layer_seq) from core.stock_cost_layer where tenant_id = @t and item_id = @i and cost_warehouse_id = @w", ("i", item), ("w", wh2));
        Assert.Equal("2026-03-01:4.000000:20.0000,2026-03-02:2.000000:14.0000", layers);
        // the destination issues in that order
        var issue = await Ok(s, "i1", "ISSUE", "2026-03-11", "SALE", L(item, wh2, 5));
        Assert.Equal(-27m, issue.Movements[0].ValueLc);   // the four of the first layer (20) and one of the second (7)
        await VerifyStockAsync(s);
    }

    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task AReservationHoldsStockAndAnIssueMayNotEatIntoItUnlessItIsTheReservingDemand()
    {
        var e = await SetupAsync();
        var s = e.S; var t = s.T;
        var reservations = new ReservationCommands(t.App);
        CommandContext Res(string key, params string[] more) => t.Context(key, ["stock.reserve", .. more]);
        await Ok(s, "r1", "RECEIPT", "2026-03-01", null, L(s.Item, s.Warehouse, 10, 5));

        var first = await reservations.CreateAsync(t.TenantId, Res("c1"), new(t.CompanyId, s.Item, s.Warehouse, 6, "C62", "ORDER_LINE", "SO-1/1"));
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        Assert.Equal((6m, 4m), (first.Value!.ReservedQuantity, first.Value.Available));
        Assert.Equal(["INVENTORY.RESERVATION_EXCEEDS_AVAILABLE"], Codes(await reservations.CreateAsync(t.TenantId, Res("c2"), new(t.CompanyId, s.Item, s.Warehouse, 5, "C62", "MANUAL", "R2"))));
        var again = await reservations.CreateAsync(t.TenantId, Res("c1"), new(t.CompanyId, s.Item, s.Warehouse, 6, "C62", "ORDER_LINE", "SO-1/1"));
        Assert.True(again.IdempotentReplay);
        Assert.Equal(first.Value.ReservationId, again.Value!.ReservationId);
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await reservations.CreateAsync(t.TenantId, t.Context("c3", "stock.post"), new(t.CompanyId, s.Item, s.Warehouse, 1, "C62", "MANUAL", "R3"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await reservations.CreateAsync(t.TenantId, Res("c4"), new(t.CompanyId, s.Item, s.Warehouse, 1, "C62", "OTHER", "R3"))));

        var availability = await reservations.AvailabilityAsync(t.TenantId, t.Context("q1", "read.inventory"), new(t.CompanyId, s.Item, s.Warehouse));
        Assert.Equal((10m, 6m, 4m), (availability.Value!.OnHand, availability.Value.Reserved, availability.Value.Available));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'reservation.created' and not is_audit"));

        // an issue of another demand may take what is not reserved, and not more
        Assert.Equal(["INVENTORY.STOCK_RESERVED"], Codes(await Post(s, "i1", "ISSUE", "2026-03-02", "SALE", L(s.Item, s.Warehouse, 5))));
        await Ok(s, "i2", "ISSUE", "2026-03-02", "SALE", L(s.Item, s.Warehouse, 4));

        // the reserving demand consumes the reservation: 3 of the 6
        var issue = await Post(s, "i3", "ISSUE", "2026-03-03", "SALE", new StockLineInput("L1", s.Item, s.Warehouse, 3, "C62", ReservationId: first.Value.ReservationId));
        Assert.True(issue.Status == CommandStatus.Ok, Describe(issue));
        Assert.Equal(-3m, await t.ScalarAsync<decimal>("select base_quantity from core.stock_reservation_event where tenant_id = @t and reservation_id = @r and event_type = 'CONSUME'", ("r", first.Value.ReservationId)));
        Assert.Equal(issue.Value!.StockDocumentId, await t.ScalarAsync<Guid>("select stock_document_id from core.stock_reservation_event where tenant_id = @t and reservation_id = @r and event_type = 'CONSUME'", ("r", first.Value.ReservationId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'reservation.consumed'"));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Post(s, "i4", "ISSUE", "2026-03-03", "SALE", new StockLineInput("L1", s.Item, s.Warehouse, 1, "C62", ReservationId: Guid.NewGuid()))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Post(s, "i5", "RECEIPT", "2026-03-03", "X", new StockLineInput("L1", s.Item, s.Warehouse, 1, "C62", ReservationId: first.Value.ReservationId))));
        var left = await reservations.AvailabilityAsync(t.TenantId, t.Context("q2", "read.inventory"), new(t.CompanyId, s.Item, s.Warehouse));
        Assert.Equal((3m, 3m, 0m), (left.Value!.OnHand, left.Value.Reserved, left.Value.Available));

        // adjust and release
        Assert.Equal(["INVENTORY.RESERVATION_EXCEEDS_AVAILABLE"], Codes(await reservations.AdjustAsync(t.TenantId, Res("a1"), new(t.CompanyId, first.Value.ReservationId, "INCREASE", 1))));
        var less = await reservations.AdjustAsync(t.TenantId, Res("a2"), new(t.CompanyId, first.Value.ReservationId, "DECREASE", 1, "CHANGED"));
        Assert.True(less.Status == CommandStatus.Ok, Describe(less));
        Assert.Equal((2m, 1m), (less.Value!.ReservedQuantity, less.Value.Available));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await reservations.AdjustAsync(t.TenantId, Res("a3"), new(t.CompanyId, first.Value.ReservationId, "DECREASE", 2))));
        var released = await reservations.ReleaseAsync(t.TenantId, Res("a4"), new(t.CompanyId, first.Value.ReservationId));
        Assert.Equal((0m, 3m), (released.Value!.ReservedQuantity, released.Value.Available));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await reservations.ReleaseAsync(t.TenantId, Res("a5"), new(t.CompanyId, first.Value.ReservationId))));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await reservations.ReleaseAsync(t.TenantId, Res("a6"), new(t.CompanyId, Guid.NewGuid()))));
        await Ok(s, "i6", "ISSUE", "2026-03-04", "SALE", L(s.Item, s.Warehouse, 3));
        await VerifyStockAsync(s);
    }

    // -------------------------------------------------------------------------------------------------------------
    private static async Task<Guid> ChargeAsync(E e, string key, decimal amount)
    {
        var t = e.S.T;
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), new FinanceCore.Core.Posting.PostJournalDocumentRequest(t.CompanyId, null, "JOURNAL", new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10), "DKK", "Freight", null, null,
            [new FinanceCore.Core.Posting.RequestLine("1", e.Lcc, amount), new FinanceCore.Core.Posting.RequestLine("2", t.Bank, -amount)]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    private static CommandContext Landed(S s, string key, string permission = "landed_cost.allocate") => s.T.Context(key, permission);

    [Fact]
    public async Task ALandedCostIsSplitBetweenTheStockOnHandAndTheCostOfGoodsSoldAndReversedOnlyWhileNothingHasMoved()
    {
        var e = await SetupAsync();
        var s = e.S; var t = s.T;
        Guid a = await ItemAsync(s, "LC-A"), b = await ItemAsync(s, "LC-B");
        var ra = await Ok(s, "r1", "RECEIPT", "2026-03-01", null, L(a, s.Warehouse, 10, 5));
        var rb = await Ok(s, "r2", "RECEIPT", "2026-03-01", null, L(b, s.Warehouse, 10, 10));
        var issue = await Ok(s, "i1", "ISSUE", "2026-03-02", "SALE", L(a, s.Warehouse, 4));
        Guid charge = await ChargeAsync(e, "ch", 1000m);
        var source = new LandedCostSource(charge, 1);
        var targets = new[] { new LandedCostTarget(ra.StockDocumentId, 1), new LandedCostTarget(rb.StockDocumentId, 1) };
        var date = new DateOnly(2026, 3, 5);

        // the checks of the request
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "x1", "stock.post"), new(t.CompanyId, source, "VALUE", targets, date))));
        Assert.Equal(["INVENTORY.ALLOCATION_BASIS_MISSING"], Codes(await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "x2"), new(t.CompanyId, source, "WEIGHT", targets, date))));
        Assert.Equal(["INVENTORY.ALLOCATION_EXCEEDS_SOURCE"], Codes(await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "x3"), new(t.CompanyId, source, "VALUE", targets, date, 1001m))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "x4"), new(t.CompanyId, source, "VALUE", [new(issue.StockDocumentId, 1)], date))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "x5"), new(t.CompanyId, new(charge, 2), "VALUE", targets, date))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "x6"), new(t.CompanyId, source, "OTHER", targets, date))));

        // 90 by value: A gets 30 and B 60. Of A's receipt only 6 of 10 are on hand, so 18 raise the stock and 12 go to the cost of goods sold
        var alloc = await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "a1"), new(t.CompanyId, source, "VALUE", targets, date, 90m));
        Assert.True(alloc.Status == CommandStatus.Ok, Describe(alloc));
        Assert.Equal([(30m, 18m, 12m), (60m, 60m, 0m)], alloc.Value!.Lines.Select(l => (l.AllocatedCostLc, l.OnHandPartLc, l.ConsumedPartLc)).ToArray());
        Assert.Equal((208m, 32m, 910m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.Cogs), await BalanceAsync(s, e.Lcc)));
        var moves = await t.ScalarAsync<string>($"select string_agg(movement_type || ':' || base_quantity::text || ':' || value_lc::text || ':' || cost_qty_after::text || ':' || cost_value_after_lc::text, '|' order by line_no) from core.stock_movement where tenant_id = @t and stock_document_id = '{alloc.Value.StockDocumentId}'");
        Assert.Equal("COST_ADJUST:0.000000:18.0000:6.000000:48.0000|COST_ADJUST:0.000000:60.0000:10.000000:160.0000", moves);
        Assert.Equal("STOCK_COST_ADJUSTMENT", await t.ScalarAsync<string>($"select document_type from core.stock_document where tenant_id = @t and stock_document_id = '{alloc.Value.StockDocumentId}'"));
        var replay = await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "a1"), new(t.CompanyId, source, "VALUE", targets, date, 90m));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(alloc.Value.AllocationId, replay.Value!.AllocationId);

        // the owning command only
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await s.Cmds.ReverseAsync(t.TenantId, Stock(t, "rv0") with { Permissions = new HashSet<string> { "stock.reverse" } }, new(t.CompanyId, alloc.Value.StockDocumentId, "CORRECTION"))));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("jr", "journal.reverse"), new(alloc.Value.JournalDocumentId!.Value, null, "CORRECTION", null))));
        await VerifyStockAsync(s);

        // reversed: the stock, the cost of goods sold and the clearing account are as they were, and the amount can be allocated again
        Assert.Equal(["REVERSAL.REASON_NOT_ALLOWED"], Codes(await s.Cmds.ReverseLandedCostAsync(t.TenantId, Landed(s, "v0"), new(t.CompanyId, alloc.Value.AllocationId, "AUTO_REVERSAL", date))));
        Assert.Equal(["INVENTORY.ALLOCATION_NOT_FOUND"], Codes(await s.Cmds.ReverseLandedCostAsync(t.TenantId, Landed(s, "v1"), new(t.CompanyId, Guid.NewGuid(), "CORRECTION", date))));
        var undone = await s.Cmds.ReverseLandedCostAsync(t.TenantId, Landed(s, "v2"), new(t.CompanyId, alloc.Value.AllocationId, "CORRECTION", new DateOnly(2026, 3, 6)));
        Assert.True(undone.Status == CommandStatus.Ok, Describe(undone));
        Assert.Equal((-90m, -30m, -18m), (undone.Value!.TotalCostLc, undone.Value.Lines[0].AllocatedCostLc, undone.Value.Lines[0].OnHandPartLc));
        Assert.Equal((130m, 20m, 1000m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.Cogs), await BalanceAsync(s, e.Lcc)));
        Assert.Equal(("REVERSED", "MIRROR"), (await t.ScalarAsync<string>($"select status from core.landed_cost_allocation where tenant_id = @t and allocation_id = '{alloc.Value.AllocationId}'"),
            await t.ScalarAsync<string>($"select status from core.landed_cost_allocation where tenant_id = @t and allocation_id = '{undone.Value.AllocationId}'")));
        Assert.Equal(["INVENTORY.ALLOCATION_ALREADY_REVERSED"], Codes(await s.Cmds.ReverseLandedCostAsync(t.TenantId, Landed(s, "v3"), new(t.CompanyId, alloc.Value.AllocationId, "CORRECTION", date))));
        Assert.Equal(["INVENTORY.ALLOCATION_NOT_REVERSIBLE"], Codes(await s.Cmds.ReverseLandedCostAsync(t.TenantId, Landed(s, "v4"), new(t.CompanyId, undone.Value.AllocationId, "CORRECTION", date))));
        await VerifyStockAsync(s);

        // allocated again, and then a movement on the cost key of a target: the reversal is refused (6.8)
        var again = await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "a2"), new(t.CompanyId, source, "QUANTITY", targets, date, 1000m));
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        await Ok(s, "i2", "ISSUE", "2026-03-08", "SALE", L(b, s.Warehouse, 1));
        Assert.Equal(["INVENTORY.ALLOCATION_NOT_REVERSIBLE"], Codes(await s.Cmds.ReverseLandedCostAsync(t.TenantId, Landed(s, "v5"), new(t.CompanyId, again.Value!.AllocationId, "CORRECTION", new DateOnly(2026, 3, 9)))));
        await VerifyStockAsync(s);
        var ledgerVerify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("vl", "chain.verify"), new(t.CompanyId, "LEDGER"));
        Assert.True(ledgerVerify.Status == CommandStatus.Ok && ledgerVerify.Value!.Ok, Describe(ledgerVerify));
    }

    [Fact]
    public async Task AFifoLayerTakesTheOnHandPartOfALandedCostIntoItsValue()
    {
        var e = await SetupAsync();
        var s = e.S; var t = s.T;
        Guid item = await ItemAsync(s, "LC-F", "FIFO");
        await Ok(s, "r1", "RECEIPT", "2026-03-01", null, L(item, s.Warehouse, 10, 5));
        var second = await Ok(s, "r2", "RECEIPT", "2026-03-02", null, L(item, s.Warehouse, 10, 7));
        await Ok(s, "i1", "ISSUE", "2026-03-03", "SALE", L(item, s.Warehouse, 12));
        Guid charge = await ChargeAsync(e, "ch", 100m);
        var target = new[] { new LandedCostTarget(second.StockDocumentId, 1) };

        // 40 to the second receipt: 8 of its 10 are left, so 32 raise its layer and 8 go to the cost of goods sold
        var alloc = await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "a1"), new(t.CompanyId, new(charge, 1), "QUANTITY", target, new DateOnly(2026, 3, 4), 40m));
        Assert.True(alloc.Status == CommandStatus.Ok, Describe(alloc));
        Assert.Equal((32m, 8m), (alloc.Value!.Lines[0].OnHandPartLc, alloc.Value.Lines[0].ConsumedPartLc));
        Assert.Equal(32m, await t.ScalarAsync<decimal>("select value_lc from core.stock_layer_adjustment where tenant_id = @t and stock_document_id = @d", ("d", alloc.Value.StockDocumentId)));
        var undone = await s.Cmds.ReverseLandedCostAsync(t.TenantId, Landed(s, "v1"), new(t.CompanyId, alloc.Value.AllocationId, "ERROR", new DateOnly(2026, 3, 5)));
        Assert.True(undone.Status == CommandStatus.Ok, Describe(undone));
        Assert.Equal(0m, await t.ScalarAsync<decimal>("select coalesce(sum(value_lc), 0) from core.stock_layer_adjustment where tenant_id = @t and layer_doc_id = @d", ("d", second.StockDocumentId)));
        await VerifyStockAsync(s);

        // allocated again, the last eight units leave at their cost and their share of the landed cost
        var again = await s.Cmds.AllocateLandedCostAsync(t.TenantId, Landed(s, "a2"), new(t.CompanyId, new(charge, 1), "QUANTITY", target, new DateOnly(2026, 3, 6), 40m));
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        var rest = await Ok(s, "i2", "ISSUE", "2026-03-07", "SALE", L(item, s.Warehouse, 8));
        Assert.Equal((-88m, 0m, 0m), (rest.Movements[0].ValueLc, rest.Movements[0].CostQtyAfter, rest.Movements[0].CostValueAfterLc));
        await VerifyStockAsync(s);
    }
}
