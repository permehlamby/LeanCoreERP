using FinanceCore.Core.Commands;
using FinanceCore.Core.Periods;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

[Collection("db")]
public class PeriodStatusTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private static CommandContext Soft(TestTenant t, string key) => t.Context(key, "period.admin");
    private static CommandContext Hard(TestTenant t, string key) => t.Context(key, "period.hard_close");
    private static CommandContext Reopen(TestTenant t, string key) => t.Context(key, "period.reopen");

    private static PeriodRef P(TestTenant t, int no, int year = 2026) => new(t.CompanyId, year, no);

    private static async Task<string> Status(TestTenant t, int no) => await t.ScalarAsync<string>(
        "select status from core.fiscal_period where tenant_id = @t and fiscal_year = 2026 and period_no = @p", ("p", no));

    private static async Task AssertChainOk(TestTenant t)
    {
        var v = await new ChainVerifier(t.App).VerifyPeriodEventsAsync(t.TenantId, t.CompanyId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => $"{p.Code} {p.ChainSeq} {p.Detail}")));
    }

    [Fact]
    public async Task SoftCloseChangesTheStatusAndTheRulesOfPosting()
    {
        var t = await TestTenant.CreateReadyAsync();
        var cmds = new PeriodStatusCommands(t.App);
        var r = await cmds.SoftCloseAsync(t.TenantId, Soft(t, "s3"), P(t, 3));

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(("OPEN", "SOFT_CLOSED"), (r.Value!.FromStatus, r.Value.ToStatus));
        Assert.Equal(2L, r.Value.EventSeq);   // 1 is YEAR_OPEN
        Assert.Equal("SOFT_CLOSED", await Status(t, 3));
        await AssertChainOk(t);

        var poster = new JournalDocumentPoster(t.App);
        var denied = await poster.PostAsync(t.TenantId, t.Context("p1"), t.Journal(postingDate: "2026-03-10"));
        Assert.Contains("PERIOD.SOFT_CLOSED_NOT_AUTHORISED", Codes(denied));
        var allowed = await poster.PostAsync(t.TenantId, t.Context("p2", "journal.post", "post.soft_closed"), t.Journal(postingDate: "2026-03-10"));
        Assert.Equal(CommandStatus.Ok, allowed.Status);

        var again = await cmds.SoftCloseAsync(t.TenantId, Soft(t, "s3b"), P(t, 3));
        Assert.Equal(["PERIOD.STATUS_INVALID"], Codes(again));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.period_event where tenant_id = @t and event_type = 'PERIOD_SOFT_CLOSE'"));
    }

    [Fact]
    public async Task HardCloseNeedsTheHardClosedPredecessorAndStopsPosting()
    {
        var t = await TestTenant.CreateReadyAsync();
        var cmds = new PeriodStatusCommands(t.App);

        var out2 = await cmds.HardCloseAsync(t.TenantId, Hard(t, "h2"), P(t, 2));
        Assert.Equal(["CLOSE.PREDECESSOR_NOT_HARD_CLOSED"], Codes(out2));
        Assert.Equal(1, (int)out2.Errors[0].Params!["predecessor_period_no"]!);

        Assert.Equal(CommandStatus.Ok, (await cmds.HardCloseAsync(t.TenantId, Hard(t, "h1"), P(t, 1))).Status);   // first period: no predecessor
        await cmds.SoftCloseAsync(t.TenantId, Soft(t, "s2"), P(t, 2));
        var from2 = await cmds.HardCloseAsync(t.TenantId, Hard(t, "h2b"), P(t, 2));                              // from SOFT_CLOSED
        Assert.True(from2.Status == CommandStatus.Ok, Describe(from2));
        Assert.Equal("SOFT_CLOSED", from2.Value!.FromStatus);
        Assert.Equal("HARD_CLOSED", await Status(t, 2));

        var post = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("p"), t.Journal(postingDate: "2026-02-10"));
        Assert.Contains("PERIOD.HARD_CLOSED", Codes(post));

        var closedAgain = await cmds.HardCloseAsync(t.TenantId, Hard(t, "h2c"), P(t, 2));
        Assert.Equal(["PERIOD.STATUS_INVALID"], Codes(closedAgain));
        await AssertChainOk(t);
    }

    [Fact]
    public async Task ReopenFollowsTheLaterPeriodRuleAndNeedsAReason()
    {
        var t = await TestTenant.CreateReadyAsync();
        var cmds = new PeriodStatusCommands(t.App);
        await cmds.HardCloseAsync(t.TenantId, Hard(t, "h1"), P(t, 1));
        await cmds.HardCloseAsync(t.TenantId, Hard(t, "h2"), P(t, 2));

        // 1 cannot be reopened while 2 is hard-closed
        var blocked = await cmds.ReopenAsync(t.TenantId, Reopen(t, "r1"), new(P(t, 1), "OPEN", "CORRECTION", "Revisor"));
        Assert.Equal(["PERIOD.REOPEN_LATER_PERIOD_CLOSED"], Codes(blocked));
        Assert.Equal(2, (int)blocked.Errors[0].Params!["later_period_no"]!);

        var noReason = await cmds.ReopenAsync(t.TenantId, Reopen(t, "r2"), new(P(t, 2), "OPEN", "", ""));
        Assert.Equal(["SCHEMA.INVALID_VALUE", "SCHEMA.INVALID_VALUE"], Codes(noReason));
        var badTarget = await cmds.ReopenAsync(t.TenantId, Reopen(t, "r3"), new(P(t, 2), "HARD_CLOSED", "CORRECTION", "x"));
        Assert.Equal("target_status", badTarget.Errors[0].Path);

        var two = await cmds.ReopenAsync(t.TenantId, Reopen(t, "r4"), new(P(t, 2), "SOFT_CLOSED", "CORRECTION", "Bilag fra revisor: æøå"));
        Assert.True(two.Status == CommandStatus.Ok, Describe(two));
        Assert.Equal("SOFT_CLOSED", await Status(t, 2));
        Assert.Equal("Bilag fra revisor: æøå", await t.ScalarAsync<string>(
            "select reason_text from core.period_event where tenant_id = @t and event_type = 'PERIOD_REOPEN'"));

        var one = await cmds.ReopenAsync(t.TenantId, Reopen(t, "r5"), new(P(t, 1), "OPEN", "CORRECTION", "Revisor"));
        Assert.True(one.Status == CommandStatus.Ok, Describe(one));
        Assert.Equal("OPEN", await Status(t, 1));

        // an OPEN period cannot be reopened, and posting works again in period 1
        var open = await cmds.ReopenAsync(t.TenantId, Reopen(t, "r6"), new(P(t, 1), "OPEN", "CORRECTION", "x"));
        Assert.Equal(["PERIOD.STATUS_INVALID"], Codes(open));
        var post = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("p"), t.Journal(postingDate: "2026-01-20"));
        Assert.Equal(CommandStatus.Ok, post.Status);
        await AssertChainOk(t);
    }

    [Fact]
    public async Task TheClosingPeriodIsManagedByYearEndOnly()
    {
        var t = await TestTenant.CreateReadyAsync(specialPeriods: 1);   // period 13 is the closing period
        var cmds = new PeriodStatusCommands(t.App);

        Assert.Equal(["PERIOD.CLOSING_PERIOD_MANAGED"], Codes(await cmds.SoftCloseAsync(t.TenantId, Soft(t, "a"), P(t, 13))));
        Assert.Equal(["PERIOD.CLOSING_PERIOD_MANAGED"], Codes(await cmds.HardCloseAsync(t.TenantId, Hard(t, "b"), P(t, 13))));
        Assert.Equal(["PERIOD.CLOSING_PERIOD_MANAGED"],
            Codes(await cmds.ReopenAsync(t.TenantId, Reopen(t, "c"), new(P(t, 13), "OPEN", "CORRECTION", "x"))));
        Assert.Equal("OPEN", await Status(t, 13));
    }

    [Fact]
    public async Task NonClosingSpecialPeriodsFollowTheOrdinaryRules()
    {
        var t = await TestTenant.CreateReadyAsync(specialPeriods: 2);   // 13 is ordinary, 14 is the closing period
        var cmds = new PeriodStatusCommands(t.App);
        for (int p = 1; p <= 12; p++) await cmds.HardCloseAsync(t.TenantId, Hard(t, "h" + p), P(t, p));

        var thirteen = await cmds.HardCloseAsync(t.TenantId, Hard(t, "h13"), P(t, 13));
        Assert.True(thirteen.Status == CommandStatus.Ok, Describe(thirteen));
        Assert.Equal("HARD_CLOSED", await Status(t, 13));
        await AssertChainOk(t);
    }

    [Fact]
    public async Task UnknownPeriodsAndMissingPermissionsAreRejected()
    {
        var t = await TestTenant.CreateReadyAsync();
        var cmds = new PeriodStatusCommands(t.App);

        Assert.Equal(["PERIOD.NOT_FOUND"], Codes(await cmds.SoftCloseAsync(t.TenantId, Soft(t, "a"), P(t, 14))));
        Assert.Equal(["PERIOD.NOT_FOUND"], Codes(await cmds.HardCloseAsync(t.TenantId, Hard(t, "b"), P(t, 1, 2030))));

        // each command needs its own permission: period.admin does not hard-close
        Assert.Equal(["AUTH.PERMISSION_DENIED"], Codes(await cmds.HardCloseAsync(t.TenantId, Soft(t, "c"), P(t, 1))));
        Assert.Equal(["AUTH.PERMISSION_DENIED"], Codes(await cmds.SoftCloseAsync(t.TenantId, Hard(t, "d"), P(t, 1))));
        Assert.Equal(["AUTH.PERMISSION_DENIED"],
            Codes(await cmds.ReopenAsync(t.TenantId, Hard(t, "e"), new(P(t, 1), "OPEN", "CORRECTION", "x"))));
        Assert.Equal("OPEN", await Status(t, 1));
    }

    [Fact]
    public async Task SameKeyIsAReplayAndAnotherRequestWithTheKeyIsRejected()
    {
        var t = await TestTenant.CreateReadyAsync();
        var cmds = new PeriodStatusCommands(t.App);
        var first = await cmds.SoftCloseAsync(t.TenantId, Soft(t, "same"), P(t, 4));
        var replay = await cmds.SoftCloseAsync(t.TenantId, Soft(t, "same"), P(t, 4));

        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Value!.EventId, replay.Value!.EventId);
        Assert.Equal(first.Value.RowHash, replay.Value.RowHash);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.period_event where tenant_id = @t and event_type = 'PERIOD_SOFT_CLOSE'"));

        var other = await cmds.SoftCloseAsync(t.TenantId, Soft(t, "same"), P(t, 5));
        Assert.Contains("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", Codes(other));
    }

    [Fact]
    public async Task EveryStatusChangeIsAnAuditedEventOnTheChain()
    {
        var t = await TestTenant.CreateReadyAsync();
        var cmds = new PeriodStatusCommands(t.App);
        await cmds.SoftCloseAsync(t.TenantId, Soft(t, "a"), P(t, 1));
        await cmds.HardCloseAsync(t.TenantId, Hard(t, "b"), P(t, 1));
        await cmds.ReopenAsync(t.TenantId, Reopen(t, "c"), new(P(t, 1), "SOFT_CLOSED", "CORRECTION", "x"));

        Assert.Equal(4L, await t.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'PERIOD_EVENT'"));
        Assert.Equal(3L, await t.ScalarAsync<long>(
            "select count(*) from core.outbox_event where tenant_id = @t and event_type = 'period.status_changed' and is_audit"));
        Assert.Equal("2026/1/SOFT_CLOSED", await t.ScalarAsync<string>(
            "select (summary->>'fiscal_year') || '/' || (summary->>'period_no') || '/' || (summary->>'status') " +
            "from core.outbox_event where tenant_id = @t and event_type = 'period.status_changed' order by position desc limit 1"));
        await AssertChainOk(t);

        // The status is a consequence of the events: if one is changed behind the back of the commands, chains:verify says so
        await t.ExecuteAsync("""
            alter table core.fiscal_period disable trigger fiscal_period_guard;
            update core.fiscal_period set status = 'HARD_CLOSED' where tenant_id = @t and fiscal_year = 2026 and period_no = 5;
            alter table core.fiscal_period enable trigger fiscal_period_guard;
            """);
        var v = await new ChainVerifier(t.App).VerifyPeriodEventsAsync(t.TenantId, t.CompanyId);
        Assert.Contains(v.Problems, p => p.Code == "PERIOD.STATUS_MISMATCH" && p.Detail.Contains("period 5"));
    }

    [Fact]
    public async Task ConcurrentPostingsAndAHardCloseNeverLeaveADocumentAfterTheClose()
    {
        var t = await TestTenant.CreateReadyAsync();
        if (t.Settings.IsLocalTestDouble) return;   // PGlite serves one connection at a time: real PostgreSQL only

        var poster = new JournalDocumentPoster(t.App);
        var close = new PeriodStatusCommands(t.App).HardCloseAsync(t.TenantId, Hard(t, "close"), P(t, 1));
        var posts = Enumerable.Range(1, 6)
            .Select(i => poster.PostAsync(t.TenantId, t.Context("c" + i), t.Journal(postingDate: "2026-01-15", amount: i))).ToArray();
        await Task.WhenAll(posts.Cast<Task>().Append(close));

        Assert.Equal(CommandStatus.Ok, (await close).Status);
        foreach (var post in posts)
        {
            var result = await post;
            Assert.True(result.Status == CommandStatus.Ok || result.Errors.All(e => e.Code == "PERIOD.HARD_CLOSED" || e.Retryable),
                Describe(result));
        }

        // every posted document is older than the close event: nothing was committed into the hard-closed period afterwards
        Assert.Equal(0L, await t.ScalarAsync<long>(
            "select count(*) from core.journal_document d, core.period_event e " +
            "where d.tenant_id = @t and e.tenant_id = @t and e.event_type = 'PERIOD_HARD_CLOSE' and d.posted_at > e.event_at"));
        var ledger = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        Assert.True(ledger.Ok, string.Join("; ", ledger.Problems.Select(p => p.Code + " " + p.Detail)));
        await AssertChainOk(t);
    }
}
