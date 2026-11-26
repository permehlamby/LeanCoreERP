using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>The stock ledger (10): receipts, issues and adjustments at moving average, the derived journal and the STOCK chain.</summary>
[Collection("db")]
public class StockTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);
    internal static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    internal static CommandContext Stock(TestTenant t, string key) => t.Context(key, "stock.post");

    internal sealed record S(TestTenant T, StockCommands Cmds, StockReads Reads, Guid Item, Guid Warehouse, Guid Inventory, Guid Cogs, Guid GrIr, Guid Gain, Guid Loss, Guid Group);

    internal static async Task<Guid> AccountAsync(TestTenant t, string no, string type, string mode)
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values (@t, @c, @a, @no, @no, @ty, 'FORBIDDEN', @m)",
            ("a", id), ("no", no), ("ty", type), ("m", mode));
        return id;
    }

    internal static async Task<Guid> MasterAsync(TestTenant t, string resource, string key, Dictionary<string, object?> fields)
    {
        var r = await new InventoryMasterCommands(t.App).CreateAsync(resource, t.TenantId, t.Context(key, "write.config"), new CreateInventoryMasterRequest(fields));
        Assert.True(r.Status == CommandStatus.Ok, resource + " " + Describe(r));
        return r.Value!.EntityId;
    }

    /// <summary>An ACTIVE DKK company with the stock series (documents and journals), the accounts of the stock roles with catch-all determinations, a goods item at moving average and a warehouse.</summary>
    internal static async Task<S> SetupAsync(bool determination = true, bool series = true)
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid inventory = await AccountAsync(t, "STK-INV", "ASSET", "NONE"), cogs = await AccountAsync(t, "STK-COGS", "EXPENSE", "NONE"), grir = await AccountAsync(t, "STK-GRIR", "LIABILITY", "GL_OPEN_ITEM");
        Guid gain = await AccountAsync(t, "STK-GAIN", "REVENUE", "NONE"), loss = await AccountAsync(t, "STK-LOSS", "EXPENSE", "NONE");
        if (series)
            await t.ExecuteAsync("""
                insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal)
                  values (@t, @c, gen_random_uuid(), 'STK', 'SD', true, '-', 5, false), (@t, @c, gen_random_uuid(), 'STJ', 'SJ', true, '-', 5, false);
                insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by)
                  select @t, @c, ty, (select series_id from core.number_series where tenant_id = @t and series_code = 'STK'), date '2026-01-01', 'test'
                  from unnest(array['STOCK_DOC_RECEIPT', 'STOCK_DOC_ISSUE', 'STOCK_DOC_ADJUSTMENT']) ty;
                insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by)
                  select @t, @c, ty, (select series_id from core.number_series where tenant_id = @t and series_code = 'STJ'), date '2026-01-01', 'test'
                  from unnest(array['STOCK_RECEIPT', 'STOCK_ISSUE', 'STOCK_ADJUSTMENT']) ty;
                insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number)
                  select @t, @c, series_id, 2026, 0 from core.number_series where tenant_id = @t and series_code in ('STK', 'STJ');
                """);
        Guid group = await MasterAsync(t, "item-groups", "grp", D(("code", "FG"), ("name", "Goods")));
        Guid item = await MasterAsync(t, "items", "item", D(("item_no", "A-100"), ("name", "Widget"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", group)));
        await MasterAsync(t, "item-companies", "ic", D(("company_id", t.CompanyId), ("item_id", item), ("valuation_method", "MOVING_AVERAGE")));
        Guid warehouse = await MasterAsync(t, "warehouses", "wh", D(("company_id", t.CompanyId), ("code", "MAIN"), ("name", "Main"), ("country_code", "DK")));
        if (determination)
        {
            var det = new AccountDeterminationCommands(t.App);
            foreach (var (role, account) in new[] { ("INVENTORY", inventory), ("COGS", cogs), ("GR_IR", grir), ("ADJUSTMENT_GAIN", gain), ("ADJUSTMENT_LOSS", loss) })
            {
                var r = await det.CreateAsync(t.TenantId, t.Context("det-" + role, "write.config"), new(t.CompanyId, D(("role_code", role), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", account))));
                Assert.True(r.Status == CommandStatus.Ok, Describe(r));
            }
        }
        return new S(t, new StockCommands(t.App), new StockReads(t.App), item, warehouse, inventory, cogs, grir, gain, loss, group);
    }

    internal static PostStockDocumentRequest Doc(S s, string type, string date, string? reason, params StockLineInput[] lines) =>
        new(s.T.CompanyId, type, DateOnly.Parse(date), DateOnly.Parse(date), lines, reason);

    internal static StockLineInput Line(S s, decimal qty, decimal? price = null, string @ref = "L1", StockAdjustment? adjustment = null) =>
        new(@ref, s.Item, s.Warehouse, qty, "C62", UnitPrice: price, Adjustment: adjustment);

    internal static async Task<StockDocumentResult> PostAsync(S s, string key, PostStockDocumentRequest req)
    {
        var r = await s.Cmds.PostAsync(s.T.TenantId, Stock(s.T, key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    internal static async Task<decimal> BalanceAsync(S s, Guid account) =>
        await s.T.ScalarAsync<decimal>($"select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and company_id = '{s.T.CompanyId}' and account_id = @a", ("a", account));

    [Fact]
    public async Task AReceiptValuesTheStockAndPostsInventoryAgainstGrIr()
    {
        var s = await SetupAsync();
        var t = s.T;
        var receipt = await PostAsync(s, "r1", Doc(s, "RECEIPT", "2026-03-10", null, Line(s, 10, 12.5m)));
        Assert.Equal(("SD-2026-00001", "STOCK_RECEIPT", 1L), (receipt.DocumentNumber, receipt.DocumentType, receipt.ChainSeq));
        var m = Assert.Single(receipt.Movements);
        Assert.Equal(("RECEIPT", "COMPUTED", 10m, 125m, 10m, 125m, 10m), (m.MovementType, m.CostSource, m.BaseQuantity, m.ValueLc, m.CostQtyAfter, m.CostValueAfterLc, m.StockQtyAfter));
        Assert.Equal("L1", m.LineRef);
        Assert.NotNull(receipt.JournalDocumentId);
        Assert.Equal((125m, -125m), (await BalanceAsync(s, s.Inventory), await BalanceAsync(s, s.GrIr)));

        // the journal is a system document of origin STOCK that the stock document binds, and the GR/IR line is a GL open item
        Assert.Equal(("STOCK_RECEIPT", "STOCK", true), (
            await t.ScalarAsync<string>($"select document_type from core.journal_document where tenant_id = @t and document_id = '{receipt.JournalDocumentId}'"),
            await t.ScalarAsync<string>($"select origin from core.journal_document where tenant_id = @t and document_id = '{receipt.JournalDocumentId}'"),
            await t.ScalarAsync<bool>($"select encode(detail_digest, 'hex') = '{receipt.RowHash}' from core.journal_document where tenant_id = @t and document_id = '{receipt.JournalDocumentId}'")));
        Assert.Equal(1L, await t.ScalarAsync<long>($"select count(*) from core.journal_line_open_item where tenant_id = @t and document_id = '{receipt.JournalDocumentId}'"));
        Assert.Equal(1L, await t.ScalarAsync<long>($"select last_seq from core.chain_head where tenant_id = @t and company_id = '{t.CompanyId}' and chain_type = 'STOCK'"));
        Assert.Equal(receipt.RowHash, await t.ScalarAsync<string>($"select encode(last_hash, 'hex') from core.chain_head where tenant_id = @t and company_id = '{t.CompanyId}' and chain_type = 'STOCK'"));
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v", "chain.verify"), new(t.CompanyId, "LEDGER"));
        Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, Describe(verify));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'stock.document_posted' and is_audit"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'stock.balance_changed' and not is_audit"));

        // the owning command only: the journal cannot be reversed through journal-documents:reverse
        var reversed = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("rev", "journal.reverse"), new(receipt.JournalDocumentId!.Value, null, "CORRECTION", null));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(reversed));

        // a replay returns the same document, and a quantity-only receipt has no journal
        var replay = await s.Cmds.PostAsync(t.TenantId, Stock(t, "r1"), Doc(s, "RECEIPT", "2026-03-10", null, Line(s, 10, 12.5m)));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(receipt.StockDocumentId, replay.Value!.StockDocumentId);
        var free = await PostAsync(s, "r2", Doc(s, "RECEIPT", "2026-03-11", "SAMPLE", Line(s, 4)));
        Assert.Equal((null, 0m, 14m), (free.JournalDocumentId, free.Movements[0].ValueLc, free.Movements[0].StockQtyAfter));
        Assert.Equal(2L, free.ChainSeq);
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await s.Cmds.PostAsync(t.TenantId, Stock(t, "r3"), Doc(s, "RECEIPT", "2026-03-11", null, Line(s, 4)))));   // quantity-only needs a reason
    }

    [Fact]
    public async Task IssuesTakeTheMovingAverageAndTheLastIssueTakesTheRestOfTheValue()
    {
        var s = await SetupAsync();
        await PostAsync(s, "r1", Doc(s, "RECEIPT", "2026-03-10", null, Line(s, 10, 12.5m)));
        await PostAsync(s, "r2", Doc(s, "RECEIPT", "2026-03-11", null, Line(s, 10, 14m)));   // 20 pieces worth 265
        var issue = await PostAsync(s, "i1", Doc(s, "ISSUE", "2026-03-12", null, Line(s, 5)));
        Assert.Equal((-5m, -66.25m, 15m, 198.75m), (issue.Movements[0].BaseQuantity, issue.Movements[0].ValueLc, issue.Movements[0].CostQtyAfter, issue.Movements[0].CostValueAfterLc));
        Assert.Equal(66.25m, await BalanceAsync(s, s.Cogs));
        Assert.Equal(198.75m, await BalanceAsync(s, s.Inventory));
        var all = await PostAsync(s, "i2", Doc(s, "ISSUE", "2026-03-13", null, Line(s, 15)));
        Assert.Equal((-198.75m, 0m, 0m), (all.Movements[0].ValueLc, all.Movements[0].CostQtyAfter, all.Movements[0].CostValueAfterLc));   // no value lingers on zero stock
        Assert.Equal(0m, await BalanceAsync(s, s.Inventory));
        Assert.Equal(["INVENTORY.INSUFFICIENT_STOCK"], Codes(await s.Cmds.PostAsync(s.T.TenantId, Stock(s.T, "i3"), Doc(s, "ISSUE", "2026-03-14", null, Line(s, 1)))));

        // rounding: 10.00 over three pieces issues 3.33, then 3.34 and then the remaining 3.33
        await PostAsync(s, "r3", Doc(s, "RECEIPT", "2026-03-15", null, new StockLineInput("L1", s.Item, s.Warehouse, 3, "C62", UnitPrice: 3.3333m)));
        var values = new List<decimal>();
        for (int i = 0; i < 3; i++) values.Add((await PostAsync(s, "ri" + i, Doc(s, "ISSUE", "2026-03-16", null, Line(s, 1)))).Movements[0].ValueLc);
        Assert.Equal(10m, -values.Sum());
        Assert.Equal(0m, await BalanceAsync(s, s.Inventory));
        // an issue for a loss goes to the loss account
        await PostAsync(s, "r4", Doc(s, "RECEIPT", "2026-03-17", null, Line(s, 2, 10)));
        await PostAsync(s, "i4", Doc(s, "ISSUE", "2026-03-18", "SCRAP", Line(s, 1)));
        Assert.Equal(10m, await BalanceAsync(s, s.Loss));
    }

    [Fact]
    public async Task AdjustmentsAreValuedByTheCoreOrTheSuppliedPriceAndCountsCompareTheBookQuantity()
    {
        var s = await SetupAsync();
        var t = s.T;
        var delta = (string direction) => new StockAdjustment("DELTA", direction);
        Assert.Equal(["INVENTORY.VALUE_REQUIRED"], Codes(await s.Cmds.PostAsync(t.TenantId, Stock(t, "a0"), Doc(s, "ADJUSTMENT", "2026-03-10", "FOUND", Line(s, 1, adjustment: delta("IN"))))));
        var supplied = await PostAsync(s, "a1", Doc(s, "ADJUSTMENT", "2026-03-10", "FOUND", Line(s, 10, 8m, adjustment: delta("IN"))));
        Assert.Equal(("ADJUST_IN", "SUPPLIED", 80m), (supplied.Movements[0].MovementType, supplied.Movements[0].CostSource, supplied.Movements[0].ValueLc));
        Assert.Equal(-80m, await BalanceAsync(s, s.Gain));
        var average = await PostAsync(s, "a2", Doc(s, "ADJUSTMENT", "2026-03-11", "FOUND", Line(s, 5, adjustment: delta("IN"))));
        Assert.Equal(("COMPUTED", 40m, 15m), (average.Movements[0].CostSource, average.Movements[0].ValueLc, average.Movements[0].CostQtyAfter));
        var lost = await PostAsync(s, "a3", Doc(s, "ADJUSTMENT", "2026-03-12", "BREAKAGE", Line(s, 3, adjustment: delta("OUT"))));
        Assert.Equal((-24m, 12m), (lost.Movements[0].ValueLc, lost.Movements[0].CostQtyAfter));
        Assert.Equal(24m, await BalanceAsync(s, s.Loss));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await s.Cmds.PostAsync(t.TenantId, Stock(t, "a4"), Doc(s, "ADJUSTMENT", "2026-03-12", null, Line(s, 1, adjustment: delta("IN"))))));   // an adjustment needs a reason
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await s.Cmds.PostAsync(t.TenantId, Stock(t, "a5"), Doc(s, "RECEIPT", "2026-03-12", "X", Line(s, 1, 1m, adjustment: delta("IN"))))));

        // a count sets the stock key to the counted quantity, and it must have seen the book quantity
        StockAdjustment Count(decimal counted, decimal expected) => new("TO_COUNTED", CountedQuantity: counted, ExpectedBookQuantity: expected, CountRef: "COUNT-1");
        var stale = await s.Cmds.PostAsync(t.TenantId, Stock(t, "c1"), Doc(s, "ADJUSTMENT", "2026-03-13", "COUNT", Line(s, 1, adjustment: Count(9, 11))));
        Assert.Equal(["INVENTORY.BOOK_QUANTITY_CHANGED"], Codes(stale));
        var counted = await PostAsync(s, "c2", Doc(s, "ADJUSTMENT", "2026-03-13", "COUNT", Line(s, 1, adjustment: Count(9, 12))));
        var cm = Assert.Single(counted.Movements);
        Assert.Equal(("ADJUST_OUT", -3m, 9m), (cm.MovementType, cm.BaseQuantity, cm.StockQtyAfter));
        Assert.Equal("COUNT-1", await t.ScalarAsync<string>($"select count_ref from core.stock_movement where tenant_id = @t and stock_document_id = '{counted.StockDocumentId}'"));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await s.Cmds.PostAsync(t.TenantId, Stock(t, "c3"), Doc(s, "ADJUSTMENT", "2026-03-14", "COUNT", Line(s, 1, adjustment: Count(9, 9))))));
    }

    [Fact]
    public async Task ARequestIsCheckedAgainstTheItemTheWarehouseTheCurrencyAndTheConfiguration()
    {
        var s = await SetupAsync();
        var t = s.T;
        Task<CommandResult<StockDocumentResult>> Post(string key, PostStockDocumentRequest r, params string[] permissions) =>
            s.Cmds.PostAsync(t.TenantId, permissions.Length == 0 ? Stock(t, key) : t.Context(key, permissions), r);
        StockLineInput With(StockLineInput l, Guid? item = null, Guid? warehouse = null, string? unit = null, string? currency = null, decimal? qty = null) =>
            l with { ItemId = item ?? l.ItemId, WarehouseId = warehouse ?? l.WarehouseId, UnitCode = unit ?? l.UnitCode, Currency = currency ?? l.Currency, Quantity = qty ?? l.Quantity };

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await Post("p0", Doc(s, "RECEIPT", "2026-03-10", null, Line(s, 1, 1)), "read.inventory")));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Post("p1", Doc(s, "RECEIPT", "2026-03-10", null, With(Line(s, 1, 1), item: Guid.NewGuid())))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Post("p2", Doc(s, "RECEIPT", "2026-03-10", null, With(Line(s, 1, 1), warehouse: Guid.NewGuid())))));
        Assert.Equal(["INVENTORY.UNIT_CONVERSION_MISSING"], Codes(await Post("p3", Doc(s, "RECEIPT", "2026-03-10", null, With(Line(s, 1, 1), unit: "BX")))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Post("p4", Doc(s, "RECEIPT", "2026-03-10", null, With(Line(s, 1, 1), qty: 0)))));
        Assert.Equal(["LIMIT.STOCK_FOREIGN_CURRENCY_NOT_BUILT"], Codes(await Post("p5", Doc(s, "RECEIPT", "2026-03-10", null, With(Line(s, 1, 1), currency: "EUR")))));
        Assert.Equal(["INVENTORY.CURRENCY_MIXED"], Codes(await Post("p6", Doc(s, "RECEIPT", "2026-03-10", null, With(Line(s, 1, 1), currency: "DKK"), With(Line(s, 1, 1, "L2"), currency: "EUR")))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Post("p7", Doc(s, "ISSUE", "2026-03-10", null, Line(s, 1, 5)))));   // a price is not accepted on an issue
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Post("p8", Doc(s, "RECEIPT", "2026-03-10", null, With(Line(s, 1, 1), item: await MasterAsync(t, "items", "svc", D(("item_no", "S-1"), ("name", "Service"), ("item_type", "SERVICE"), ("base_unit", "HUR"), ("quantity_scale", 0), ("item_group_id", s.Group))))))));
        Guid lot = await MasterAsync(t, "items", "lot", D(("item_no", "L-1"), ("name", "Lot item"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", s.Group), ("tracking_mode", "LOT")));
        await MasterAsync(t, "item-companies", "iclot", D(("company_id", t.CompanyId), ("item_id", lot), ("valuation_method", "MOVING_AVERAGE")));
        Assert.Equal(["INVENTORY.TRACKING_REQUIRED"], Codes(await Post("p9", Doc(s, "RECEIPT", "2026-03-10", null, With(Line(s, 1, 1), item: lot)))));
        Guid fifo = await MasterAsync(t, "items", "fifo", D(("item_no", "F-1"), ("name", "Fifo item"), ("item_type", "GOODS"), ("base_unit", "C62"), ("quantity_scale", 0), ("item_group_id", s.Group)));
        await MasterAsync(t, "item-companies", "icfifo", D(("company_id", t.CompanyId), ("item_id", fifo), ("valuation_method", "FIFO")));
        Assert.Equal(["INVENTORY.INSUFFICIENT_STOCK"], Codes(await Post("p10", Doc(s, "ISSUE", "2026-03-10", null, With(Line(s, 1), item: fifo)))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Post("p11", Doc(s, "TRANSFER", "2026-03-10", null, Line(s, 1)))));
        // a closed period is refused like a posting
        await t.SetPeriodStatusAsync(3, "HARD_CLOSED");
        Assert.Contains("PERIOD", Codes(await Post("p12", Doc(s, "RECEIPT", "2026-03-10", null, Line(s, 1, 1))))[0]);

        var noRules = await SetupAsync(determination: false);
        Assert.Equal(["INVENTORY.ACCOUNT_DETERMINATION_MISSING"], Codes(await noRules.Cmds.PostAsync(noRules.T.TenantId, Stock(noRules.T, "n1"), Doc(noRules, "RECEIPT", "2026-03-10", null, Line(noRules, 1, 1)))));
        var noSeries = await SetupAsync(series: false);
        Assert.Equal(["SERIES.NO_ASSIGNMENT"], Codes(await noSeries.Cmds.PostAsync(noSeries.T.TenantId, Stock(noSeries.T, "n2"), Doc(noSeries, "RECEIPT", "2026-03-10", null, Line(noSeries, 1, 1)))));
        // nothing was written by the refused requests
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.stock_document where tenant_id = @t"));
    }

    [Fact]
    public async Task TheStockDocumentsAndBalancesAreReadAndTheMovementsAreFrozen()
    {
        var s = await SetupAsync();
        var t = s.T;
        var r1 = await PostAsync(s, "r1", Doc(s, "RECEIPT", "2026-03-10", null, Line(s, 10, 12.5m)));
        await PostAsync(s, "r2", Doc(s, "RECEIPT", "2026-04-10", null, Line(s, 10, 14m)));
        var issue = await PostAsync(s, "i1", Doc(s, "ISSUE", "2026-04-12", null, Line(s, 5)));
        CommandContext Read(string key) => t.Context(key, "read.inventory");

        var detail = (await s.Reads.GetAsync(t.TenantId, Read("g1"), new(r1.StockDocumentId, ["movements"]))).Value!;
        Assert.Equal(("STOCK_RECEIPT", "posted"), (detail.Document.GetProperty("document_type").GetString(), detail.Document.GetProperty("status").GetString()));
        Assert.Equal(125m, Assert.Single(detail.Movements!.Value.EnumerateArray()).GetProperty("value_lc").GetDecimal());
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await s.Reads.GetAsync(t.TenantId, Read("g2"), new(r1.StockDocumentId, ["journal"]))));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await s.Reads.GetAsync(t.TenantId, Read("g3"), new(Guid.NewGuid()))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Reads.GetAsync(t.TenantId, Stock(t, "g4"), new(r1.StockDocumentId))));

        Assert.Equal(3, (await s.Reads.ListAsync(t.TenantId, Read("l1"), new(CompanyId: t.CompanyId))).Value!.Items.Count);
        Assert.Equal(2, (await s.Reads.ListAsync(t.TenantId, Read("l2"), new(DocumentType: "STOCK_RECEIPT"))).Value!.Items.Count);
        Assert.Equal(2, (await s.Reads.ListAsync(t.TenantId, Read("l3"), new(DateFrom: new DateOnly(2026, 4, 1), ItemId: s.Item))).Value!.Items.Count);
        Assert.Empty((await s.Reads.ListAsync(t.TenantId, Read("l4"), new(WarehouseId: Guid.NewGuid()))).Value!.Items);
        var page = await s.Reads.ListAsync(t.TenantId, Read("l5"), new(Limit: 2));
        Assert.Equal((2, true), (page.Value!.Items.Count, page.Value.Page.NextCursor is not null));
        Assert.Equal(["READ.CURSOR_INVALID"], Codes(await s.Reads.ListAsync(t.TenantId, Read("l6"), new(Cursor: "garbage"))));

        // balances by the sum of the movements up to the date (6.3, C1)
        var march = (await s.Reads.BalancesAsync(t.TenantId, Read("b1"), new(t.CompanyId, new DateOnly(2026, 3, 31)))).Value!;
        Assert.Equal((10m, 125m), (march.Rows.Single().BaseQuantity, march.Rows.Single().ValueLc));
        var april = (await s.Reads.BalancesAsync(t.TenantId, Read("b2"), new(t.CompanyId, new DateOnly(2026, 4, 30)))).Value!;
        Assert.Equal((15m, 198.75m, 198.75m), (april.Rows.Single().BaseQuantity, april.Rows.Single().ValueLc, april.TotalValueLc));
        Assert.Equal(issue.Movements[0].CostValueAfterLc, april.TotalValueLc);   // the running columns and the sums agree in processing order

        // movements are frozen, a stock document changes only its S columns, and nothing is deleted
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.stock_movement set value_lc = 1 where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.stock_document set posting_date = date '2026-01-01' where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("delete from core.stock_movement where tenant_id = @t"));
    }
}
