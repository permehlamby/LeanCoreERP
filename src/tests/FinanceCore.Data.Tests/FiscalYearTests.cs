using FinanceCore.Core.Commands;
using FinanceCore.Core.Periods;

namespace FinanceCore.Data.Tests;

[Collection("db")]
public class FiscalYearTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static CommandContext Admin(TestTenant t, string key) => t.Context(key, "period.admin");

    [Fact]
    public async Task FirstYearCreatesPeriodsCountersAndTheFirstChainEvent()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        var r = await new FiscalYearOpener(t.App).OpenAsync(t.TenantId, Admin(t, "y2026"), new(t.CompanyId, 2026));

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        OpenedFiscalYear y = r.Value!;
        Assert.Equal(new DateOnly(2026, 1, 1), y.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), y.EndDate);
        Assert.Equal(1, y.FirstPeriodNo);
        Assert.Equal(12, y.Periods.Count);
        Assert.Equal(new DateOnly(2026, 2, 28), y.Periods[1].EndDate);
        Assert.Equal(new[] { t.SeriesId, t.ReversalSeriesId }.Order(), y.CounterSeriesIds.Order());
        Assert.Equal(1L, y.EventSeq);

        Assert.Equal(12L, await t.ScalarAsync<long>("select count(*) from core.fiscal_period where tenant_id = @t and status = 'OPEN'"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select max(last_number) from core.number_range_counter where tenant_id = @t"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'PERIOD_EVENT'"));
        Assert.Equal("year.opened", await t.ScalarAsync<string>("select event_type from core.outbox_event where tenant_id = @t"));

        var v = await new ChainVerifier(t.App).VerifyPeriodEventsAsync(t.TenantId, t.CompanyId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => $"{p.Code} {p.ChainSeq} {p.Detail}")));
        Assert.Equal(1L, v.DocumentsChecked);
    }

    [Fact]
    public async Task ShortFirstYearStartsInALaterMonth()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        var r = await new FiscalYearOpener(t.App).OpenAsync(t.TenantId, Admin(t, "k"), new(t.CompanyId, 2026, new DateOnly(2026, 4, 1)));

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(4, r.Value!.FirstPeriodNo);
        Assert.Equal(new DateOnly(2026, 4, 1), r.Value.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), r.Value.EndDate);
        Assert.Equal(Enumerable.Range(4, 9), r.Value.Periods.Select(p => p.PeriodNo));
        Assert.Equal(new DateOnly(2026, 4, 1), r.Value.Periods[0].StartDate);
    }

    [Fact]
    public async Task FollowingYearsMustBeContiguousAndTheChainGrows()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        var opener = new FiscalYearOpener(t.App);
        var first = await opener.OpenAsync(t.TenantId, Admin(t, "a"), new(t.CompanyId, 2026));
        var second = await opener.OpenAsync(t.TenantId, Admin(t, "b"), new(t.CompanyId, 2027));

        Assert.True(second.Status == CommandStatus.Ok, Describe(second));
        Assert.Equal(2L, second.Value!.EventSeq);
        Assert.Equal(first.Value!.RowHash, second.Value.PrevHash);
        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.number_range_counter where tenant_id = @t"));

        var gap = await opener.OpenAsync(t.TenantId, Admin(t, "c"), new(t.CompanyId, 2029));
        Assert.Contains("FISCAL_YEAR.NOT_CONTIGUOUS", gap.Errors.Select(e => e.Code));

        var again = await opener.OpenAsync(t.TenantId, Admin(t, "d"), new(t.CompanyId, 2026));
        Assert.Contains("FISCAL_YEAR.ALREADY_OPEN", again.Errors.Select(e => e.Code));

        var startDate = await opener.OpenAsync(t.TenantId, Admin(t, "e"), new(t.CompanyId, 2028, new DateOnly(2028, 1, 1)));
        Assert.Contains("FISCAL_YEAR.START_DATE_NOT_ALLOWED", startDate.Errors.Select(e => e.Code));

        // rejected commands wrote nothing
        Assert.Equal(2L, await t.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'PERIOD_EVENT'"));
        var v = await new ChainVerifier(t.App).VerifyPeriodEventsAsync(t.TenantId, t.CompanyId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => $"{p.Code} {p.ChainSeq} {p.Detail}")));
    }

    [Fact]
    public async Task InvalidStartDatesOfTheFirstYearAreRejected()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        var opener = new FiscalYearOpener(t.App);
        foreach (var bad in new[] { new DateOnly(2026, 4, 2), new DateOnly(2025, 12, 1), new DateOnly(2027, 1, 1) })
        {
            var r = await opener.OpenAsync(t.TenantId, Admin(t, "bad-" + bad), new(t.CompanyId, 2026, bad));
            Assert.Contains("FISCAL_YEAR.START_DATE_INVALID", r.Errors.Select(e => e.Code));
        }
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.fiscal_year where tenant_id = @t"));
    }

    [Fact]
    public async Task SpecialPeriodsHaveTheRangeOfPeriodTwelve()
    {
        var t = await TestTenant.CreateAsync(bare: true, specialPeriods: 2);
        var r = await new FiscalYearOpener(t.App).OpenAsync(t.TenantId, Admin(t, "s"), new(t.CompanyId, 2026));

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(14, r.Value!.Periods.Count);
        Assert.Equal(2, r.Value.SpecialPeriodCount);
        var p13 = r.Value.Periods.Single(p => p.PeriodNo == 13);
        Assert.Equal("SPECIAL", p13.PeriodType);
        Assert.Equal(r.Value.Periods.Single(p => p.PeriodNo == 12).StartDate, p13.StartDate);
        Assert.Equal(r.Value.Periods.Single(p => p.PeriodNo == 12).EndDate, p13.EndDate);
    }

    [Theory]
    [InlineData("START", 2026)]
    [InlineData("END", 2027)]
    public async Task BrokenFiscalYearFollowsTheLabelRule(string rule, int label)
    {
        var t = await TestTenant.CreateAsync(bare: true, firstMonth: 4, labelRule: rule);
        var r = await new FiscalYearOpener(t.App).OpenAsync(t.TenantId, Admin(t, "f"), new(t.CompanyId, label));

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(new DateOnly(2026, 4, 1), r.Value!.StartDate);
        Assert.Equal(new DateOnly(2027, 3, 31), r.Value.EndDate);
        Assert.Equal(new DateOnly(2026, 4, 1), r.Value.Periods[0].StartDate);
        Assert.Equal(new DateOnly(2027, 3, 31), r.Value.Periods[^1].EndDate);
    }

    [Fact]
    public async Task SameKeyIsAReplayAndAnotherRequestWithTheKeyIsRejected()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        var opener = new FiscalYearOpener(t.App);
        var first = await opener.OpenAsync(t.TenantId, Admin(t, "same"), new(t.CompanyId, 2026));
        var second = await opener.OpenAsync(t.TenantId, Admin(t, "same"), new(t.CompanyId, 2026));

        Assert.True(second.IdempotentReplay);
        Assert.Equal(first.Value!.EventId, second.Value!.EventId);
        Assert.Equal(first.Value.RowHash, second.Value.RowHash);
        Assert.Equal(12, second.Value.Periods.Count);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.fiscal_year where tenant_id = @t"));

        var other = await opener.OpenAsync(t.TenantId, Admin(t, "same"), new(t.CompanyId, 2027));
        Assert.Contains("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", other.Errors.Select(e => e.Code));
    }

    [Fact]
    public async Task PermissionAndCompanyAreChecked()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        var opener = new FiscalYearOpener(t.App);
        var denied = await opener.OpenAsync(t.TenantId, t.Context("p", "journal.post"), new(t.CompanyId, 2026));
        Assert.Equal(["AUTH.PERMISSION_DENIED"], denied.Errors.Select(e => e.Code));

        var unknown = await opener.OpenAsync(t.TenantId, Admin(t, "u"), new(Guid.NewGuid(), 2026));
        Assert.Equal(["COMPANY.NOT_FOUND"], unknown.Errors.Select(e => e.Code));
    }

    [Fact]
    public async Task TheOpenedYearCanBePostedTo()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        await new FiscalYearOpener(t.App).OpenAsync(t.TenantId, Admin(t, "y"), new(t.CompanyId, 2026));
        // activation needs the LEDGER chain head, which companies:activate creates. Here only the year matters,
        // so the company is activated by hand and the head created as the migration role.
        await t.ExecuteAsync("""
            update core.company set status = 'ACTIVE' where tenant_id = @t and company_id = @c;
            insert into core.chain_head (tenant_id, company_id, chain_type, scope_key, last_seq, last_hash, hash_version)
              values (@t, @c, 'LEDGER', @l::text, 0, @g, 2);
            """, ("l", t.LedgerId), ("g", FinanceCore.Core.Hashing.LedgerHash2.Genesis(t.TenantId, t.CompanyId, "LEDGER", t.LedgerId.ToString("D"))));

        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("post"), t.Journal());
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal("GL-2026-000001", r.Value!.DocumentNumber);
    }

    [Fact]
    public async Task ConcurrentOpeningsOfTheSameCompanyStayContiguousAndChained()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        if (t.Settings.IsLocalTestDouble) return;   // PGlite serves one connection at a time: real PostgreSQL only

        var opener = new FiscalYearOpener(t.App);
        var results = await Task.WhenAll(
            opener.OpenAsync(t.TenantId, Admin(t, "c1"), new(t.CompanyId, 2026)),
            opener.OpenAsync(t.TenantId, Admin(t, "c2"), new(t.CompanyId, 2027)));

        // Either order is legal. If 2027 wins the lock it becomes the first year and 2026 is rejected as not contiguous.
        // What must hold in both cases: the period chain is intact and has one event per opened year.
        var v = await new ChainVerifier(t.App).VerifyPeriodEventsAsync(t.TenantId, t.CompanyId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => $"{p.Code} {p.ChainSeq} {p.Detail}")));
        Assert.Equal(results.Count(r => r.Status == CommandStatus.Ok), (int)v.DocumentsChecked);
    }
}
