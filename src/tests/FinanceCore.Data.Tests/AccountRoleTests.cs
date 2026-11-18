using FinanceCore.Core.Commands;
using FinanceCore.Core.Roles;

namespace FinanceCore.Data.Tests;

[Collection("db")]
public class AccountRoleTests
{
    private static CommandContext Config(TestTenant t, string key) => t.Context(key, "write.config");

    private static string Describe(CommandResult<AssignedAccountRole> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Params?.GetValueOrDefault("rule")}"));

    private static string[] Rules(CommandResult<AssignedAccountRole> r) =>
        r.Errors.Where(e => e.Code == "CONFIG.ROLE_ACCOUNT_RULE_VIOLATED").Select(e => (string)e.Params!["rule"]!).ToArray();

    private static async Task<Guid> Account(TestTenant t, string no, string type, string openItem = "NONE", string? currency = null, bool revalue = false)
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, open_item_mode, currency_code, revalue_flag, tax_code_rule) " +
            "values (@t, @c, @id, @no, 'Test', @type, @oi, @cur, @rv, 'FORBIDDEN')",
            ("id", id), ("no", no), ("type", type), ("oi", openItem),
            ("cur", (object?)currency ?? DBNull.Value), ("rv", revalue));
        return id;
    }

    [Fact]
    public async Task ARoleIsAssignedLoggedAndReplayed()
    {
        var t = await TestTenant.CreateAsync(withRoles: false);
        Guid account = await Account(t, "7900", "EXPENSE");
        var assigner = new AccountRoleAssigner(t.App);
        var req = new AssignAccountRoleRequest(t.CompanyId, "ROUNDING", account);

        var r = await assigner.AssignAsync(t.TenantId, Config(t, "a"), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal("ROUNDING", r.Value!.RoleCode);
        Assert.Equal(account, r.Value.AccountId);
        Assert.Null(r.Value.CurrencyCode);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.account_role_assignment where tenant_id = @t"));
        Assert.Equal(1L, await t.ScalarAsync<long>(
            "select count(*) from core.outbox_event where tenant_id = @t and event_type = 'config.version_created' and is_audit and entity_type = 'ACCOUNT_ROLE_ASSIGNMENT'"));

        var replay = await assigner.AssignAsync(t.TenantId, Config(t, "a"), req);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(r.Value.AssignmentId, replay.Value!.AssignmentId);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.account_role_assignment where tenant_id = @t"));

        var reuse = await assigner.AssignAsync(t.TenantId, Config(t, "a"), req with { RoleCode = "TAX_ROUNDING" });
        Assert.Contains("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", reuse.Errors.Select(e => e.Code));
    }

    [Fact]
    public async Task PermissionCompanyAndShapeAreChecked()
    {
        var t = await TestTenant.CreateAsync(withRoles: false);
        Guid account = await Account(t, "7900", "EXPENSE");
        var assigner = new AccountRoleAssigner(t.App);

        var denied = await assigner.AssignAsync(t.TenantId, t.Context("p", "journal.post"), new(t.CompanyId, "ROUNDING", account));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], denied.Errors.Select(e => e.Code));

        var noCompany = await assigner.AssignAsync(t.TenantId, Config(t, "q"), new(Guid.NewGuid(), "ROUNDING", account));
        Assert.Equal(["COMPANY.NOT_FOUND"], noCompany.Errors.Select(e => e.Code));

        foreach (var bad in new AssignAccountRoleRequest[]
        {
            new(t.CompanyId, "NOT_A_ROLE", account),
            new(t.CompanyId, "TAX_ROUNDING", account, CurrencyCode: "USD"),          // not per currency
            new(t.CompanyId, "ROUNDING", account, CounterpartCompanyId: Guid.NewGuid()),
            new(t.CompanyId, "IC_RECEIVABLE", account),                               // needs a counterpart
            new(t.CompanyId, "IC_PAYABLE", account, CounterpartCompanyId: t.CompanyId), // not itself
            new(t.CompanyId, "ROUNDING", account, VatRegistrationId: Guid.NewGuid())
        })
        {
            var r = await assigner.AssignAsync(t.TenantId, Config(t, "bad-" + bad.RoleCode), bad);
            Assert.Contains("SCHEMA.INVALID_VALUE", r.Errors.Select(e => e.Code));
        }

        var vat = await assigner.AssignAsync(t.TenantId, Config(t, "vat"), new(t.CompanyId, "VAT_SETTLEMENT", account, VatRegistrationId: Guid.NewGuid()));
        Assert.Contains("CONFIG.INVALID_REFERENCE", vat.Errors.Select(e => e.Code));

        var sek = await assigner.AssignAsync(t.TenantId, Config(t, "sek"), new(t.CompanyId, "ROUNDING", account, "SEK"));
        Assert.Contains("CONFIG.CURRENCY_NOT_ENABLED", sek.Errors.Select(e => e.Code));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.account_role_assignment where tenant_id = @t"));
    }

    [Fact]
    public async Task TheAccountMustSatisfyTheRoleTable()
    {
        var t = await TestTenant.CreateAsync();   // the six default roles exist, 7900 is the ROUNDING account
        var assigner = new AccountRoleAssigner(t.App);
        DateTime later = DateTime.UtcNow.AddSeconds(-5);
        Guid bs = await Account(t, "2000", "LIABILITY"), control = await Account(t, "7960", "EXPENSE", openItem: "CONTROL"),
             dkk = await Account(t, "7961", "EXPENSE", currency: "DKK"), revalued = await Account(t, "8200", "EQUITY", revalue: true),
             income = await Account(t, "7962", "REVENUE");

        // ROUNDING is P&L only
        Assert.Equal(["ACCOUNT_TYPE"], Rules(await assigner.AssignAsync(t.TenantId, Config(t, "1"), new(t.CompanyId, "ROUNDING", bs, EffectiveFrom: later))));
        // RETAINED_EARNINGS is EQUITY only, and not a revalued account
        Assert.Equal(["ACCOUNT_TYPE"], Rules(await assigner.AssignAsync(t.TenantId, Config(t, "2"), new(t.CompanyId, "RETAINED_EARNINGS", income, EffectiveFrom: later))));
        var reval = await assigner.AssignAsync(t.TenantId, Config(t, "3"), new(t.CompanyId, "RETAINED_EARNINGS", revalued, EffectiveFrom: later));
        Assert.Equal(["CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS"], reval.Errors.Select(e => e.Code));
        // open item mode must be NONE
        Assert.Equal(["OPEN_ITEM_MODE"], Rules(await assigner.AssignAsync(t.TenantId, Config(t, "4"), new(t.CompanyId, "TAX_ROUNDING", control, EffectiveFrom: later))));
        // one role per account
        Assert.Equal(["ONE_ROLE_PER_ACCOUNT"], Rules(await assigner.AssignAsync(t.TenantId, Config(t, "5"), new(t.CompanyId, "FX_REALISED", t.Rounding, EffectiveFrom: later))));
        // an account in one currency: not as default row, and only for that currency
        Assert.Equal(["CURRENCY"], Rules(await assigner.AssignAsync(t.TenantId, Config(t, "6"), new(t.CompanyId, "TAX_ROUNDING", dkk, EffectiveFrom: later))));
        Assert.Equal(["CURRENCY"], Rules(await assigner.AssignAsync(t.TenantId, Config(t, "7"), new(t.CompanyId, "ROUNDING", dkk, "USD", EffectiveFrom: later))));
        var ok = await assigner.AssignAsync(t.TenantId, Config(t, "8"), new(t.CompanyId, "ROUNDING", dkk, "DKK", EffectiveFrom: later));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        // the same account may serve other rows of the same role
        var again = await assigner.AssignAsync(t.TenantId, Config(t, "9"), new(t.CompanyId, "ROUNDING", dkk, "DKK", EffectiveFrom: later.AddSeconds(1)));
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
    }

    [Fact]
    public async Task ATaxAccountAcceptsOnlyTheVatSettlementRole()
    {
        var t = await TestTenant.CreateAsync(withRoles: false);
        Guid account = await Account(t, "7900", "EXPENSE");
        await t.ExecuteAsync("update core.gl_account set tax_account = true where tenant_id = @t and account_id = @a", ("a", account));
        var r = await new AccountRoleAssigner(t.App).AssignAsync(t.TenantId, Config(t, "x"), new(t.CompanyId, "ROUNDING", account));
        Assert.Contains("TAX_ACCOUNT", Rules(r));
    }

    [Fact]
    public async Task ANewVersionMustBeLaterAndTheNewestEffectiveRowWins()
    {
        var t = await TestTenant.CreateAsync();
        var assigner = new AccountRoleAssigner(t.App);
        Guid second = await Account(t, "7970", "EXPENSE");

        // the default ROUNDING row of the tenant is effective from 2026-01-01
        var early = await assigner.AssignAsync(t.TenantId, Config(t, "e"),
            new(t.CompanyId, "ROUNDING", second, EffectiveFrom: new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], early.Errors.Select(e => e.Code));
        var same = await assigner.AssignAsync(t.TenantId, Config(t, "s"),
            new(t.CompanyId, "ROUNDING", second, EffectiveFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], same.Errors.Select(e => e.Code));

        // a version in the future is not resolved yet, a version in the past is
        var future = await assigner.AssignAsync(t.TenantId, Config(t, "f"), new(t.CompanyId, "ROUNDING", second, EffectiveFrom: DateTime.UtcNow.AddDays(30)));
        Assert.True(future.Status == CommandStatus.Ok, Describe(future));
        var poster = new JournalDocumentPoster(t.App);
        var one = await poster.PostAsync(t.TenantId, t.Context("p1"), Rounding(t));
        Assert.Equal(t.Rounding, one.Value!.Lines[3].AccountId);

        // two versions of the USD row: the newest one that is effective wins over the older one and over the default
        Guid third = await Account(t, "7971", "EXPENSE");
        foreach (var (key, account, from) in new[] { ("v1", second, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)), ("v2", third, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)) })
        {
            var v = await assigner.AssignAsync(t.TenantId, Config(t, key), new(t.CompanyId, "ROUNDING", account, "USD", EffectiveFrom: from));
            Assert.True(v.Status == CommandStatus.Ok, Describe(v));
        }
        var two = await poster.PostAsync(t.TenantId, t.Context("p2"), Rounding(t));
        Assert.Equal(third, two.Value!.Lines[3].AccountId);
    }

    [Fact]
    public async Task AnIntercompanyRoleBelongsToACounterpart()
    {
        var t = await TestTenant.CreateAsync();
        Guid other = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, fiscal_calendar_id) " +
            "select @t, @other, 'C2', 'Other A/S', 'DK', 'DKK', fiscal_calendar_id from core.company where tenant_id = @t and company_id = @c",
            ("other", other));
        Guid receivable = await Account(t, "1500", "ASSET");
        var assigner = new AccountRoleAssigner(t.App);

        var r = await assigner.AssignAsync(t.TenantId, Config(t, "ic"), new(t.CompanyId, "IC_RECEIVABLE", receivable, CounterpartCompanyId: other));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var unknown = await assigner.AssignAsync(t.TenantId, Config(t, "ic2"), new(t.CompanyId, "IC_PAYABLE", receivable, CounterpartCompanyId: Guid.NewGuid()));
        Assert.Contains("CONFIG.INVALID_REFERENCE", unknown.Errors.Select(e => e.Code));
        // IC_RECEIVABLE and IC_PAYABLE are two roles: one account cannot have both
        var both = await assigner.AssignAsync(t.TenantId, Config(t, "ic3"), new(t.CompanyId, "IC_PAYABLE", receivable, CounterpartCompanyId: other));
        Assert.Contains("ONE_ROLE_PER_ACCOUNT", Rules(both));
    }

    [Fact]
    public async Task OnlyTheConfigurationRoleCanWriteTheTable()
    {
        var t = await TestTenant.CreateAsync();
        foreach (var role in new[] { CommandRole.Post, CommandRole.Period, CommandRole.Read })
        {
            await using var tx = await TenantTransaction.BeginAsync(t.App, t.TenantId, role);
            await using var cmd = tx.Command(
                "insert into core.account_role_assignment (tenant_id, company_id, role_code, account_id, effective_from) " +
                "select tenant_id, company_id, 'TAX_ROUNDING', account_id, now() + interval '1 day' from core.gl_account where tenant_id = @t and account_no = '7910'");
            cmd.Parameters.AddWithValue("t", t.TenantId);
            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    private static Core.Posting.PostJournalDocumentRequest Rounding(TestTenant t) =>
        t.Journal(postingDate: "2026-01-02", currency: "USD") with
        {
            Lines = [new("1", t.Bank, 0.03m), new("2", t.Expense, 0.03m), new("3", t.Revenue, -0.06m)]
        };
}
