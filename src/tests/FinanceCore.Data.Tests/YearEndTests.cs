using FinanceCore.Core.Closing;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>year-end:close and year-end:reopen (05, 5): a DKK company with one special period as the closing period.</summary>
[Collection("db")]
public class YearEndTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);
    private static CommandContext Close(TestTenant t, string key) => t.Context(key, "year_end.close");
    private static CommandContext Reopen(TestTenant t, string key) => t.Context(key, "year_end.reopen");

    /// <summary>Revenue 1000 and expense 400 in March, all twelve regular periods hard-closed.</summary>
    internal static async Task<TestTenant> ClosedYearAsync(bool postings = true)
    {
        var t = await RevaluationTests.ReadyAsync(special: 1, revalueBank: false);
        if (postings)
        {
            var poster = new JournalDocumentPoster(t.App);
            Assert.Equal(CommandStatus.Ok, (await poster.PostAsync(t.TenantId, t.Context("rev"), t.Journal(postingDate: "2026-03-15", amount: 1000m))).Status);
            var expense = new PostJournalDocumentRequest(t.CompanyId, null, "JOURNAL", new DateOnly(2026, 5, 2), new DateOnly(2026, 5, 2), "DKK", "Expense", null, null,
                [new RequestLine("1", t.Expense, 400m, null), new RequestLine("2", t.Bank, -400m)]);
            Assert.Equal(CommandStatus.Ok, (await poster.PostAsync(t.TenantId, t.Context("exp"), expense)).Status);
        }
        await HardCloseYearAsync(t);
        return t;
    }

    internal static async Task HardCloseYearAsync(TestTenant t)
    {
        var hard = new PeriodStatusCommands(t.App);
        for (int p = 1; p <= 12; p++)
        {
            var r = await hard.HardCloseAsync(t.TenantId, t.Context("hc" + p, "period.hard_close"), new(t.CompanyId, 2026, p));
            Assert.True(r.Status == CommandStatus.Ok, "hard-close " + p + ": " + Describe(r));
        }
    }

    private static async Task AssertChainsOk(TestTenant t)
    {
        var v = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("verify", "chain.verify"), new(t.CompanyId, "LEDGER"));
        Assert.True(v.Status == CommandStatus.Ok && v.Value!.Ok, Describe(v) + " " + string.Join("; ", v.Value?.Breaks.Select(b => $"{b.Code} {b.ChainSeq}") ?? []));
    }

    [Fact]
    public async Task CloseZeroesTheProfitAndLossAgainstRetainedEarningsAndClosesTheYear()
    {
        var t = await ClosedYearAsync();
        var cmds = new YearEndCommands(t.App);
        var r = await cmds.CloseAsync(t.TenantId, Close(t, "c1"), new(t.CompanyId, 2026));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        YearEndResult v = r.Value!;

        PostedDocument doc = v.Document!;
        Assert.Equal(("YEAR_END", new DateOnly(2026, 12, 31), 13), (doc.DocumentType, doc.PostingDate, doc.FiscalPeriod));
        Assert.Equal(13, v.ClosingPeriodNo);
        Assert.Equal(3, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Equal("CLOSING", l.LineType));
        Assert.Equal(1000m, doc.Lines.Single(l => l.AccountId == t.Revenue).AmountLc);
        Assert.Equal(-400m, doc.Lines.Single(l => l.AccountId == t.Expense).AmountLc);
        var retained = doc.Lines.Single(l => l.AccountId != t.Revenue && l.AccountId != t.Expense);
        Assert.Equal((-600m, -600m), (retained.AmountLc, retained.AmountTc));   // net profit, credit
        Assert.Equal(0m, doc.Lines.Sum(l => l.AmountLc));
        Assert.Equal(0m, doc.Lines.Sum(l => l.AmountGc));

        Assert.Equal("CLOSED", await t.ScalarAsync<string>("select status from core.fiscal_year where tenant_id = @t and fiscal_year = 2026"));
        Assert.Equal(doc.DocumentId, await t.ScalarAsync<Guid>("select year_end_document_id from core.fiscal_year where tenant_id = @t and fiscal_year = 2026"));
        Assert.Equal("HARD_CLOSED", await t.ScalarAsync<string>("select status from core.fiscal_period where tenant_id = @t and fiscal_year = 2026 and period_no = 13"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.period_event where tenant_id = @t and event_type = 'YEAR_END_CLOSE' and period_no is null and document_id = @d", ("d", doc.DocumentId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.period_event where tenant_id = @t and event_type = 'PERIOD_HARD_CLOSE' and period_no = 13"));

        // the P&L of the year is zero with the closing document and what it was without it (5.4)
        Assert.Equal(0m, await t.ScalarAsync<decimal>(
            "select sum(l.amount_lc) from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.account_id = l.account_id where l.tenant_id = @t and l.fiscal_year = 2026 and a.account_type in ('REVENUE', 'EXPENSE')"));
        Assert.Equal(-600m, await t.ScalarAsync<decimal>(
            "select sum(l.amount_lc) from core.journal_line l join core.journal_document d on d.tenant_id = l.tenant_id and d.document_id = l.document_id join core.gl_account a on a.tenant_id = l.tenant_id and a.account_id = l.account_id " +
            "where l.tenant_id = @t and l.fiscal_year = 2026 and a.account_type in ('REVENUE', 'EXPENSE') and d.document_type <> 'YEAR_END'"));

        // the anchor requests of the chains of the company, PENDING (5.1, step 4)
        Assert.True(v.AnchorRequests >= 2);
        Assert.True(await t.ScalarAsync<long>("select count(*) from core.chain_anchor where tenant_id = @t and anchor_state = 'PENDING'") >= v.AnchorRequests);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'year.closed'"));
        await AssertChainsOk(t);
        var events = await new ChainVerifier(t.App).VerifyPeriodEventsAsync(t.TenantId, t.CompanyId);
        Assert.True(events.Ok, string.Join("; ", events.Problems.Select(p => $"{p.Code} {p.ChainSeq} {p.Detail}")));

        var replay = await cmds.CloseAsync(t.TenantId, Close(t, "c1"), new(t.CompanyId, 2026));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(doc.DocumentId, replay.Value!.Document!.DocumentId);
        Assert.Equal(["CLOSE.YEAR_NOT_OPEN"], Codes(await cmds.CloseAsync(t.TenantId, Close(t, "c2"), new(t.CompanyId, 2026))));
    }

    [Fact]
    public async Task CloseChecksItsPreconditions()
    {
        var cmds = (Func<TestTenant, YearEndCommands>)(t => new YearEndCommands(t.App));

        var open = await RevaluationTests.ReadyAsync(special: 1, revalueBank: false);
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await cmds(open).CloseAsync(open.TenantId, open.Context("a"), new(open.CompanyId, 2026))));
        Assert.Equal(["CLOSE.PREDECESSOR_NOT_HARD_CLOSED"], Codes(await cmds(open).CloseAsync(open.TenantId, Close(open, "b"), new(open.CompanyId, 2026))));

        var none = await RevaluationTests.ReadyAsync(special: 0, revalueBank: false);
        await HardCloseYearAsync(none);
        Assert.Equal(["CLOSE.CLOSING_PERIOD_MISSING"], Codes(await cmds(none).CloseAsync(none.TenantId, Close(none, "c"), new(none.CompanyId, 2026))));

        var noAccount = await ClosedYearAsync();
        await noAccount.ExecuteAsync("update core.gl_account set posting_blocked = true where tenant_id = @t and company_id = @c and account_no = '8100'");
        Assert.Equal(["CLOSE.RETAINED_EARNINGS_INVALID"], Codes(await cmds(noAccount).CloseAsync(noAccount.TenantId, Close(noAccount, "d"), new(noAccount.CompanyId, 2026))));
    }

    [Fact]
    public async Task AYearWithoutProfitAndLossClosesWithoutADocument()
    {
        var t = await ClosedYearAsync(postings: false);
        var r = await new YearEndCommands(t.App).CloseAsync(t.TenantId, Close(t, "c1"), new(t.CompanyId, 2026));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Null(r.Value!.Document);
        Assert.Equal("CLOSED", await t.ScalarAsync<string>("select status from core.fiscal_year where tenant_id = @t and fiscal_year = 2026"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type = 'YEAR_END'"));

        var reopen = await new YearEndCommands(t.App).ReopenAsync(t.TenantId, Reopen(t, "o1"), new(t.CompanyId, 2026, "ERROR", "closed by mistake"));
        Assert.True(reopen.Status == CommandStatus.Ok, Describe(reopen));
        Assert.Null(reopen.Value!.Mirror);
        Assert.Equal("OPEN", await t.ScalarAsync<string>("select status from core.fiscal_year where tenant_id = @t and fiscal_year = 2026"));
    }

    [Fact]
    public async Task ReopenReversesTheClosingDocumentAndAReCloseDoesNotPickItUp()
    {
        var t = await ClosedYearAsync();
        var cmds = new YearEndCommands(t.App);
        Assert.Equal(["CLOSE.YEAR_NOT_CLOSED"], Codes(await cmds.ReopenAsync(t.TenantId, Reopen(t, "o0"), new(t.CompanyId, 2026, "ERROR", "x"))));
        var closed = (await cmds.CloseAsync(t.TenantId, Close(t, "c1"), new(t.CompanyId, 2026))).Value!;

        Assert.Equal(["SCHEMA.INVALID_VALUE", "SCHEMA.INVALID_VALUE"], Codes(await cmds.ReopenAsync(t.TenantId, Reopen(t, "o1"), new(t.CompanyId, 2026, "", ""))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await cmds.ReopenAsync(t.TenantId, t.Context("o2"), new(t.CompanyId, 2026, "ERROR", "x"))));
        var r = await cmds.ReopenAsync(t.TenantId, Reopen(t, "o3"), new(t.CompanyId, 2026, "ERROR", "a posting was missing"));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        PostedDocument mirror = r.Value!.Mirror!;
        Assert.Equal(("YEAR_END", new DateOnly(2026, 12, 31), 13), (mirror.DocumentType, mirror.PostingDate, mirror.FiscalPeriod));
        Assert.Equal(closed.Document!.Lines.Select(l => -l.AmountLc), mirror.Lines.Select(l => l.AmountLc));

        Assert.Equal(("OPEN", "OPEN"), (
            await t.ScalarAsync<string>("select status from core.fiscal_year where tenant_id = @t and fiscal_year = 2026"),
            await t.ScalarAsync<string>("select status from core.fiscal_period where tenant_id = @t and fiscal_year = 2026 and period_no = 13")));
        Assert.Equal("HARD_CLOSED", await t.ScalarAsync<string>("select status from core.fiscal_period where tenant_id = @t and fiscal_year = 2026 and period_no = 12"));
        Assert.Equal(mirror.DocumentId, await t.ScalarAsync<Guid>("select reversed_by_document_id from core.journal_document where tenant_id = @t and document_id = @d", ("d", closed.Document.DocumentId)));
        Assert.Equal("REOPEN", await t.ScalarAsync<string>("select reversal_reason_code from core.journal_document where tenant_id = @t and document_id = @d", ("d", mirror.DocumentId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.period_event where tenant_id = @t and event_type = 'YEAR_END_REOPEN' and reason_code = 'ERROR' and document_id = @d", ("d", closed.Document.DocumentId)));
        // journal-documents:reverse leaves a YEAR_END document to year-end:reopen (D1)
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(await new DocumentReverser(t.App).ReverseAsync(t.TenantId,
            t.Context("rv", "journal.reverse"), new ReverseDocumentRequest(closed.Document.DocumentId, new DateOnly(2026, 12, 31), "ERROR", "x"))));

        var again = await cmds.CloseAsync(t.TenantId, Close(t, "c2"), new(t.CompanyId, 2026));
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        Assert.NotEqual(closed.Document.DocumentId, again.Value!.Document!.DocumentId);
        Assert.Equal(closed.Document.Lines.Select(l => l.AmountLc), again.Value.Document.Lines.Select(l => l.AmountLc));
        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type = 'YEAR_END'"));
        await AssertChainsOk(t);
        var events = await new ChainVerifier(t.App).VerifyPeriodEventsAsync(t.TenantId, t.CompanyId);
        Assert.True(events.Ok, string.Join("; ", events.Problems.Select(p => $"{p.Code} {p.ChainSeq} {p.Detail}")));
    }

    [Fact]
    public async Task ReopenIsBlockedByPostingsInALaterYear()
    {
        var t = await ClosedYearAsync();
        var cmds = new YearEndCommands(t.App);
        Assert.Equal(CommandStatus.Ok, (await cmds.CloseAsync(t.TenantId, Close(t, "c1"), new(t.CompanyId, 2026))).Status);

        var opened = await new FiscalYearOpener(t.App).OpenAsync(t.TenantId, t.Context("y27", "period.admin"), new(t.CompanyId, 2027));
        Assert.True(opened.Status == CommandStatus.Ok, Describe(opened));
        await t.ExecuteAsync("insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) values (@t, 'SPOT', 'DKK', 'EUR', date '2027-01-10', 0.134)");
        var posted = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("p27"), t.Journal(postingDate: "2027-01-10", amount: 10m));
        Assert.True(posted.Status == CommandStatus.Ok, Describe(posted));

        Assert.Equal(["CLOSE.REOPEN_BLOCKED_BY_POSTINGS"], Codes(await cmds.ReopenAsync(t.TenantId, Reopen(t, "o1"), new(t.CompanyId, 2026, "ERROR", "x"))));
        Assert.Equal("CLOSED", await t.ScalarAsync<string>("select status from core.fiscal_year where tenant_id = @t and fiscal_year = 2026"));
    }
}
