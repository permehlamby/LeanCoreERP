using FinanceCore.Core.Closing;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;
using FinanceCore.Core.Reads;

namespace FinanceCore.Data.Tests;

/// <summary>The ledger reads (09): documents, balances, trial balance, account ledger, open items and partner balances.</summary>
[Collection("db")]
public class LedgerReadTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);
    private static CommandContext Read(TestTenant t, string key = "rd") => t.Context(key, "read.ledger");
    private static LedgerReads Reads(TestTenant t) => new(t.App);

    private static async Task<Guid> PostAsync(TestTenant t, string key, string date, decimal amount, Guid? debit = null, Guid? credit = null)
    {
        var req = new PostJournalDocumentRequest(t.CompanyId, null, "JOURNAL", DateOnly.Parse(date), DateOnly.Parse(date), "DKK", "Test", null, null,
            [new RequestLine("1", debit ?? t.Bank, amount, null), new RequestLine("2", credit ?? t.Revenue, -amount)]);
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    [Fact]
    public async Task DocumentsAreListedInChainOrderWithCursorsAndFilters()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid d1 = await PostAsync(t, "p1", "2026-03-10", 100m), d2 = await PostAsync(t, "p2", "2026-03-11", 200m), d3 = await PostAsync(t, "p3", "2026-04-02", 300m);
        var reads = Reads(t);

        var page1 = await reads.ListDocumentsAsync(t.TenantId, Read(t), new(t.CompanyId, Limit: 2));
        Assert.True(page1.Status == CommandStatus.Ok, Describe(page1));
        Assert.Equal([d1, d2], page1.Value!.Items.Select(i => i.DocumentId).ToArray());
        Assert.NotNull(page1.Value.Page.NextCursor);
        Assert.Equal(3L, page1.Value.Snapshot.Ledgers.Single().LedgerSeq);
        var page2 = await reads.ListDocumentsAsync(t.TenantId, Read(t), new(t.CompanyId, Limit: 2, Cursor: page1.Value.Page.NextCursor));
        Assert.Equal([d3], page2.Value!.Items.Select(i => i.DocumentId).ToArray());
        Assert.Null(page2.Value.Page.NextCursor);

        var april = await reads.ListDocumentsAsync(t.TenantId, Read(t), new(t.CompanyId, PostingDateFrom: new DateOnly(2026, 4, 1), IncludeLines: true));
        var only = Assert.Single(april.Value!.Items);
        Assert.Equal(2, only.Lines!.Count);
        Assert.Equal(300m, only.Lines[0].Amounts.Lc);
        Assert.Empty((await reads.ListDocumentsAsync(t.TenantId, Read(t), new(t.CompanyId, Status: "reversed"))).Value!.Items);

        var get = await reads.GetDocumentAsync(t.TenantId, Read(t), new(d2));
        Assert.Equal(("posted", 2L, 2), (get.Value!.Document.Status, get.Value.Document.ChainSeq, get.Value.Document.Lines!.Count));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await reads.GetDocumentAsync(t.TenantId, Read(t), new(Guid.NewGuid()))));

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await reads.ListDocumentsAsync(t.TenantId, t.Context("x"), new(t.CompanyId))));
        Assert.Equal(["READ.CURSOR_INVALID"], Codes(await reads.ListDocumentsAsync(t.TenantId, Read(t), new(t.CompanyId, Cursor: "garbage"))));
        Assert.Equal(["READ.LIMIT_EXCEEDED"], Codes(await reads.ListDocumentsAsync(t.TenantId, Read(t), new(t.CompanyId, Limit: 501))));
        var stale = await reads.ListDocumentsAsync(t.TenantId, Read(t), new(t.CompanyId, MinLedgerSeq: 99));
        Assert.Equal(["READ.STALE_SNAPSHOT"], Codes(stale));
        Assert.True(stale.Errors[0].Retryable);
    }

    [Fact]
    public async Task BalancesFollowTheRulesOfTheAccountTypeAndSplitByGroupKeys()
    {
        var t = await TestTenant.CreateReadyAsync();
        await PostAsync(t, "p1", "2026-03-10", 1000m);
        await PostAsync(t, "p2", "2026-05-02", 400m, debit: t.Expense, credit: t.Bank);
        var reads = Reads(t);

        var byType = await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), GroupBy: ["account_type"]));
        Assert.True(byType.Status == CommandStatus.Ok, Describe(byType));
        var rows = byType.Value!.Rows.ToDictionary(r => r.Keys["account_type"]!);
        Assert.Equal(600m, rows["ASSET"].Net.Lc);
        Assert.Equal(-1000m, rows["REVENUE"].Net.Lc);
        Assert.Equal(400m, rows["EXPENSE"].Debit.Lc);
        Assert.Equal(0m, rows.Values.Sum(r => r.Net.Lc));
        Assert.Equal(("DKK", "EUR", "EUR"), (byType.Value.Currencies.Lc, byType.Value.Currencies.Gc, byType.Value.Currencies.Rc));
        Assert.False(byType.Value.L2Stable);

        var movement = await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "MOVEMENT", FromDate: new DateOnly(2026, 5, 1), ToDate: new DateOnly(2026, 5, 31), GroupBy: ["account_id"]));
        Assert.Equal(2, movement.Value!.Rows.Count);
        var total = await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30)));
        Assert.Equal(4L, Assert.Single(total.Value!.Rows).LineCount);

        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE"))));
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), GroupBy: ["nope"]))));
    }

    [Fact]
    public async Task TheYearEndDocumentIsLeftOutOfProfitAndLossUnderAutoAndKeptUnderRaw()
    {
        var t = await YearEndTests.ClosedYearAsync();
        var reads = Reads(t);
        var tbBefore = await reads.TrialBalanceAsync(t.TenantId, Read(t), new(t.CompanyId, 2026, 1, 12));
        Assert.True(tbBefore.Status == CommandStatus.Ok, Describe(tbBefore));
        Assert.True(tbBefore.Value!.Foots);
        Assert.False(tbBefore.Value.L2Stable);   // the closing period is still open
        Assert.Equal(3, tbBefore.Value.Rows.Count);

        Assert.Equal(CommandStatus.Ok, (await new YearEndCommands(t.App).CloseAsync(t.TenantId, t.Context("c", "year_end.close"), new(t.CompanyId, 2026))).Status);
        static string Net(BalancesResult b, string type) => b.Rows.Single(r => r.Keys["account_type"] == type).Net.Lc.ToString("F0");
        var auto = (await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 12, 31), GroupBy: ["account_type"]))).Value!;
        Assert.Equal(("-1000", "400", "-600"), (Net(auto, "REVENUE"), Net(auto, "EXPENSE"), Net(auto, "EQUITY")));
        var raw = (await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 12, 31), Rule: "RAW", GroupBy: ["account_type"]))).Value!;
        Assert.Equal(("0", "0"), (Net(raw, "REVENUE"), Net(raw, "EXPENSE")));

        // with the closing period the AUTO trial balance shows the P&L before and the retained earnings after the closing: it does not foot (F-IMPL-41)
        var tbAfter = (await reads.TrialBalanceAsync(t.TenantId, Read(t), new(t.CompanyId, 2026, 1, 13))).Value!;
        Assert.False(tbAfter.Foots);
        Assert.True(tbAfter.L2Stable);
        Assert.True((await reads.TrialBalanceAsync(t.TenantId, Read(t), new(t.CompanyId, 2026, 1, 12, IncludeSpecialPeriods: false))).Value!.Foots);
        Assert.Equal(["READ.NOT_FOUND"], Codes(await reads.TrialBalanceAsync(t.TenantId, Read(t), new(t.CompanyId, 2030, 1, 12))));
    }

    [Fact]
    public async Task AnAccountLedgerCarriesTheRunningBalanceAcrossPages()
    {
        var t = await TestTenant.CreateReadyAsync();
        await PostAsync(t, "p0", "2026-02-10", 50m);
        await PostAsync(t, "p1", "2026-03-10", 100m);
        await PostAsync(t, "p2", "2026-03-11", 200m);
        await PostAsync(t, "p3", "2026-03-12", -30m);
        var reads = Reads(t);

        var page1 = await reads.AccountLedgerAsync(t.TenantId, Read(t), new(t.CompanyId, t.Bank, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), Limit: 2));
        Assert.True(page1.Status == CommandStatus.Ok, Describe(page1));
        Assert.Equal((50m, 320m), (page1.Value!.OpeningBalance.Lc, page1.Value.ClosingBalance.Lc));
        Assert.Equal([150m, 350m], page1.Value.Lines.Select(l => l.RunningBalance.Lc).ToArray());
        var page2 = await reads.AccountLedgerAsync(t.TenantId, Read(t), new(t.CompanyId, t.Bank, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), Limit: 2, Cursor: page1.Value.Page.NextCursor));
        Assert.Equal([320m], page2.Value!.Lines.Select(l => l.RunningBalance.Lc).ToArray());
        Assert.Null(page2.Value.Page.NextCursor);
        Assert.Equal(["READ.NOT_FOUND"], Codes(await reads.AccountLedgerAsync(t.TenantId, Read(t), new(t.CompanyId, Guid.NewGuid(), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)))));
    }

    [Fact]
    public async Task OpenItemsAreComputedAsOfADateAndRolledUpPerPartnerAndAccount()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid glOpen = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values (@t, @c, @g, '1900', 'Clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM')", ("g", glOpen));
        var req = new PostJournalDocumentRequest(t.CompanyId, null, "JOURNAL", new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 15), "DKK", "Open", null, null,
            [new RequestLine("1", glOpen, 1000m, null, new OpenItemRequest(DueDate: new DateOnly(2026, 4, 15))), new RequestLine("2", t.Revenue, -1000m)]);
        Assert.Equal(CommandStatus.Ok, (await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("oi"), req)).Status);
        var reads = Reads(t);

        var items = await reads.OpenItemsAsync(t.TenantId, Read(t), new(t.CompanyId, new DateOnly(2026, 5, 1)));
        Assert.True(items.Status == CommandStatus.Ok, Describe(items));
        var item = Assert.Single(items.Value!.Items);
        Assert.Equal((1000m, 0m, 1000m, 16, "OPEN"), (item.Remaining.Lc, item.Cleared.Lc, item.RemainingTc, item.DaysPastDue, item.CachedStatus));
        Assert.Equal(new DateOnly(2026, 4, 15), item.DueDate);
        Assert.Empty((await reads.OpenItemsAsync(t.TenantId, Read(t), new(t.CompanyId, new DateOnly(2026, 3, 1)))).Value!.Items);
        Assert.Empty((await reads.OpenItemsAsync(t.TenantId, Read(t), new(t.CompanyId, new DateOnly(2026, 5, 1), DueTo: new DateOnly(2026, 4, 1)))).Value!.Items);

        var balances = await reads.PartnerBalancesAsync(t.TenantId, Read(t), new(t.CompanyId, new DateOnly(2026, 5, 1)));
        var row = Assert.Single(balances.Value!.Rows);
        Assert.Equal((glOpen, 1000m, 1, 1, new DateOnly(2026, 4, 15)), (row.AccountId, row.Remaining.Lc, row.OverdueItems, row.ItemCount, row.OldestDueDate));
    }
}
