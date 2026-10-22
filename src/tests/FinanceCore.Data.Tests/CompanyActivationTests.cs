using FinanceCore.Core.Commands;
using FinanceCore.Core.Periods;

namespace FinanceCore.Data.Tests;

[Collection("db")]
public class CompanyActivationTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Params?.GetValueOrDefault("check")}"));

    private static string[] FailedChecks<T>(CommandResult<T> r) =>
        r.Errors.Where(e => e.Code == "CONFIG.COMPANY_NOT_READY").Select(e => (string)e.Params!["check"]!).ToArray();

    private static CommandContext Activate(TestTenant t, string key) => t.Context(key, "company.activate");

    private static Task OpenYear(TestTenant t, string key = "year") =>
        new FiscalYearOpener(t.App).OpenAsync(t.TenantId, t.Context(key, "period.admin"), new(t.CompanyId, 2026));

    [Fact]
    public async Task CompanyWithoutAnOpenYearIsNotReady()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        var activator = new CompanyActivator(t.App);

        var check = await activator.CheckReadinessAsync(t.TenantId, t.Context("r", "read.config"), t.CompanyId);
        Assert.Equal(CommandStatus.Rejected, check.Status);
        Assert.False(check.Value!.Ready);
        Assert.Equal(["FISCAL_YEAR_OPEN"], FailedChecks(check));
        Assert.Equal("DRAFT", check.Value.Status);

        var r = await activator.ActivateAsync(t.TenantId, Activate(t, "a"), t.CompanyId);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Equal(["FISCAL_YEAR_OPEN"], FailedChecks(r));
        Assert.Equal("DRAFT", await t.ScalarAsync<string>("select status from core.company where tenant_id = @t"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.command_log where tenant_id = @t"));
    }

    [Fact]
    public async Task AllFailedChecksAreListedTogether()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        await t.ExecuteAsync("""
            update core.tenant_currency set active = false where tenant_id = @t and currency_code = 'EUR';
            update core.ledger set active = false where tenant_id = @t;
            """);
        var r = await new CompanyActivator(t.App).ActivateAsync(t.TenantId, Activate(t, "a"), t.CompanyId);

        Assert.Equal(CommandStatus.Rejected, r.Status);
        string[] failed = FailedChecks(r);
        Assert.Contains("CURRENCIES_ENABLED", failed);
        Assert.Contains("LEDGER", failed);
        Assert.Contains("FISCAL_YEAR_OPEN", failed);
        Assert.DoesNotContain("COMPANY_SETTING_VERSION", failed);
        Assert.Contains("EUR", (string)r.Errors.Single(e => (string?)e.Params?["check"] == "CURRENCIES_ENABLED").Params!["detail"]!);
    }

    [Fact]
    public async Task ReadyCompanyIsActivatedAndItsChainsStart()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        await OpenYear(t);

        var r = await new CompanyActivator(t.App).ActivateAsync(t.TenantId, Activate(t, "a"), t.CompanyId);

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal("ACTIVE", r.Value!.Status);
        // the PERIOD_EVENT head exists from the year opening, the LEDGER head is new
        Assert.Equal([$"LEDGER/{t.LedgerId:D}"], r.Value.CreatedChainHeads);
        Assert.Contains(r.Warnings, w => w.Code == "LIMIT.READINESS_CHECKS_NOT_IMPLEMENTED");
        Assert.Equal("ACTIVE", await t.ScalarAsync<string>("select status from core.company where tenant_id = @t"));
        Assert.Equal(2L, await t.ScalarAsync<long>("select row_version from core.company where tenant_id = @t"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'LEDGER'"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'company.activated'"));
    }

    [Fact]
    public async Task ActivationIsIdempotentAndOnlyFromDraft()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        await OpenYear(t);
        var activator = new CompanyActivator(t.App);

        var first = await activator.ActivateAsync(t.TenantId, Activate(t, "same"), t.CompanyId);
        var replay = await activator.ActivateAsync(t.TenantId, Activate(t, "same"), t.CompanyId);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.CommandLogId, replay.CommandLogId);

        var again = await activator.ActivateAsync(t.TenantId, Activate(t, "other"), t.CompanyId);
        Assert.Equal(["COMPANY.NOT_DRAFT"], again.Errors.Select(e => e.Code));

        var reuse = await activator.ActivateAsync(t.TenantId, Activate(t, "same"), Guid.NewGuid());
        Assert.Contains("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", reuse.Errors.Select(e => e.Code));
        Assert.Equal(2L, await t.ScalarAsync<long>("select row_version from core.company where tenant_id = @t"));
    }

    [Fact]
    public async Task PermissionsAndCompanyAreChecked()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        var activator = new CompanyActivator(t.App);
        Assert.Equal(["AUTH.PERMISSION_DENIED"], (await activator.ActivateAsync(t.TenantId, t.Context("p", "read.config"), t.CompanyId)).Errors.Select(e => e.Code));
        Assert.Equal(["AUTH.PERMISSION_DENIED"], (await activator.CheckReadinessAsync(t.TenantId, t.Context("q", "company.activate"), t.CompanyId)).Errors.Select(e => e.Code));
        Assert.Equal(["COMPANY.NOT_FOUND"], (await activator.ActivateAsync(t.TenantId, Activate(t, "u"), Guid.NewGuid())).Errors.Select(e => e.Code));
    }

    [Fact]
    public async Task FromDraftToFirstPostedDocumentThroughTheCommands()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        var poster = new JournalDocumentPoster(t.App);

        // DRAFT accepts no postings (07, 11.3)
        var early = await poster.PostAsync(t.TenantId, t.Context("early"), t.Journal());
        Assert.Contains("COMPANY.NOT_ACTIVE", early.Errors.Select(e => e.Code));

        var year = await new FiscalYearOpener(t.App).OpenAsync(t.TenantId, t.Context("year", "period.admin"), new(t.CompanyId, 2026));
        Assert.Equal(CommandStatus.Ok, year.Status);
        var activated = await new CompanyActivator(t.App).ActivateAsync(t.TenantId, Activate(t, "act"), t.CompanyId);
        Assert.Equal(CommandStatus.Ok, activated.Status);

        var posted = await poster.PostAsync(t.TenantId, t.Context("post"), t.Journal());
        Assert.True(posted.Status == CommandStatus.Ok, string.Join(", ", posted.Errors.Select(e => e.Code)));
        Assert.Equal("GL-2026-000001", posted.Value!.DocumentNumber);
        Assert.Equal(1L, posted.Value.ChainSeq);

        var ledger = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        var periods = await new ChainVerifier(t.App).VerifyPeriodEventsAsync(t.TenantId, t.CompanyId);
        Assert.True(ledger.Ok, string.Join("; ", ledger.Problems.Select(p => p.Code + " " + p.Detail)));
        Assert.True(periods.Ok, string.Join("; ", periods.Problems.Select(p => p.Code + " " + p.Detail)));

        // the three events of the flow, in outbox order
        Assert.Equal(["year.opened", "company.activated", "ledger.document_posted"], await Events(t));
    }

    private static async Task<string[]> Events(TestTenant t)
    {
        await using var admin = await t.OpenAdminAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("select event_type from core.outbox_event where tenant_id = @t order by position", admin);
        cmd.Parameters.AddWithValue("t", t.TenantId);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list.ToArray();
    }
}
