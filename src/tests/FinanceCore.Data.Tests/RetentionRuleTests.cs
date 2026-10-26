using FinanceCore.Core.Commands;
using FinanceCore.Core.Retention;
using Npgsql;

namespace FinanceCore.Data.Tests;

[Collection("db")]
public class RetentionRuleTests
{
    private static CommandContext Manage(TestTenant t, string key) => t.Context(key, RetentionRules.Permission);

    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => e.Code));

    private static CreateRetentionRuleVersionRequest Rule(TestTenant t, string cls = "LEDGER", string country = "DK", int years = 5, DateTime? from = null) =>
        new(t.CompanyId, cls, country, years, "FISCAL_YEAR_END", "Bogføringsloven", from);

    [Fact]
    public async Task ARuleIsCreatedVersionedLoggedAndReplayed()
    {
        var t = await TestTenant.CreateAsync(withRetention: false);
        var commands = new RetentionRuleCommands(t.App);

        var first = await commands.CreateVersionAsync(t.TenantId, Manage(t, "r1"), Rule(t));
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        Assert.True(first.Value!.NewRule);

        var second = await commands.CreateVersionAsync(t.TenantId, Manage(t, "r2"), Rule(t, years: 10));
        Assert.True(second.Status == CommandStatus.Ok, Describe(second));
        Assert.False(second.Value!.NewRule);
        Assert.Equal(first.Value.RuleId, second.Value.RuleId);
        Assert.True(second.Value.EffectiveFrom > first.Value.EffectiveFrom);

        var other = await commands.CreateVersionAsync(t.TenantId, Manage(t, "r3"), Rule(t, country: "SE", years: 7));
        Assert.True(other.Value!.NewRule);
        Assert.NotEqual(first.Value.RuleId, other.Value.RuleId);

        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.retention_rule where tenant_id = @t"));
        Assert.Equal(3L, await t.ScalarAsync<long>(
            "select count(*) from core.outbox_event where tenant_id = @t and event_type = 'config.version_created' and is_audit and entity_type = 'RETENTION_RULE'"));

        var replay = await commands.CreateVersionAsync(t.TenantId, Manage(t, "r2"), Rule(t, years: 10));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(second.Value.RuleId, replay.Value!.RuleId);
        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.retention_rule where tenant_id = @t"));

        var reuse = await commands.CreateVersionAsync(t.TenantId, Manage(t, "r2"), Rule(t, years: 11));
        Assert.Contains("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", reuse.Errors.Select(e => e.Code));
    }

    [Fact]
    public async Task PermissionCompanyCountryAndShapeAreChecked()
    {
        var t = await TestTenant.CreateAsync(withRetention: false);
        var commands = new RetentionRuleCommands(t.App);

        var denied = await commands.CreateVersionAsync(t.TenantId, t.Context("p", "write.config"), Rule(t));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], denied.Errors.Select(e => e.Code));

        var noCompany = await commands.CreateVersionAsync(t.TenantId, Manage(t, "c"), Rule(t) with { CompanyId = Guid.NewGuid() });
        Assert.Equal(["COMPANY.NOT_FOUND"], noCompany.Errors.Select(e => e.Code));

        var noCountry = await commands.CreateVersionAsync(t.TenantId, Manage(t, "k"), Rule(t, country: "ZZ"));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], noCountry.Errors.Select(e => e.Code));

        foreach (var bad in new[]
        {
            Rule(t, cls: "NOT_A_CLASS"), Rule(t, country: "dk"), Rule(t, country: "DKK"), Rule(t, years: 0), Rule(t, years: 101),
            Rule(t) with { StartEvent = "TOMORROW" }, Rule(t) with { LegalBasis = "" }, Rule(t) with { LegalBasis = new string('x', 201) }
        })
        {
            var r = await commands.CreateVersionAsync(t.TenantId, Manage(t, "bad-" + Guid.NewGuid()), bad);
            Assert.Equal(["SCHEMA.INVALID_VALUE"], r.Errors.Select(e => e.Code));
        }
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.retention_rule where tenant_id = @t"));
    }

    [Fact]
    public async Task ANewVersionMustStartAfterTheNewestOne()
    {
        var t = await TestTenant.CreateAsync(withRetention: false);
        var commands = new RetentionRuleCommands(t.App);
        var first = await commands.CreateVersionAsync(t.TenantId, Manage(t, "a"), Rule(t));
        DateTime start = first.Value!.EffectiveFrom;

        var same = await commands.CreateVersionAsync(t.TenantId, Manage(t, "b"), Rule(t, years: 6, from: start));
        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], same.Errors.Select(e => e.Code));
        var earlier = await commands.CreateVersionAsync(t.TenantId, Manage(t, "c"), Rule(t, years: 6, from: start.AddDays(-1)));
        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], earlier.Errors.Select(e => e.Code));

        var future = await commands.CreateVersionAsync(t.TenantId, Manage(t, "d"), Rule(t, years: 6, from: start.AddDays(30)));
        Assert.True(future.Status == CommandStatus.Ok, Describe(future));
        Assert.Equal(start.AddDays(30), future.Value!.EffectiveFrom);
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.retention_rule where tenant_id = @t"));
    }

    [Fact]
    public async Task TheVersionsReadBackGiveTheRightDateForARecord()
    {
        var t = await TestTenant.CreateAsync(withRetention: false);
        var commands = new RetentionRuleCommands(t.App);
        var v1 = await commands.CreateVersionAsync(t.TenantId, Manage(t, "a"), Rule(t, years: 5));
        var v2 = await commands.CreateVersionAsync(t.TenantId, Manage(t, "b"), Rule(t, years: 10, from: v1.Value!.EffectiveFrom.AddDays(30)));
        await commands.CreateVersionAsync(t.TenantId, Manage(t, "c"), Rule(t, country: "SE", years: 7));

        await using var tx = await TenantTransaction.BeginAsync(t.App, t.TenantId, CommandRole.Read);
        var versions = await RetentionRuleCommands.ReadVersionsAsync(tx, t.TenantId, t.CompanyId, default);
        Assert.Equal(3, versions.Count);
        var fye = new DateOnly(2026, 12, 31);
        var rec = new DateOnly(2026, 7, 1);
        Assert.Equal(new DateOnly(2033, 12, 31), RetentionRules.For(versions, "LEDGER", v1.Value.EffectiveFrom.AddDays(1), fye, rec));   // DK 5y = 2031, SE 7y = 2033
        Assert.Equal(new DateOnly(2036, 12, 31), RetentionRules.For(versions, "LEDGER", v2.Value!.EffectiveFrom, fye, rec));            // DK 10y
        Assert.Null(RetentionRules.For(versions, "STOCK", v2.Value.EffectiveFrom, fye, rec));
    }

    [Fact]
    public async Task TheTableIsInsertOnlyAndAVersionKeepsItsClassAndCountry()
    {
        var t = await TestTenant.CreateAsync(withRetention: false);
        var commands = new RetentionRuleCommands(t.App);
        var made = await commands.CreateVersionAsync(t.TenantId, Manage(t, "a"), Rule(t));
        Guid ruleId = made.Value!.RuleId;

        await Assert.ThrowsAnyAsync<PostgresException>(() => t.ExecuteAsync(
            "update core.retention_rule set retention_years = 99 where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => t.ExecuteAsync(
            "delete from core.retention_rule where tenant_id = @t"));

        // a later version of the same rule_id with another class or country is refused by the guard, whatever the writer
        await Assert.ThrowsAnyAsync<PostgresException>(() => t.ExecuteAsync(
            "insert into core.retention_rule (tenant_id, company_id, rule_id, effective_from, record_class, country_code, retention_years, start_event, created_by) " +
            "values (@t, @c, @r, now() + interval '1 day', 'STOCK', 'DK', 5, 'RECORD_DATE', 'x')", ("r", ruleId)));
        await Assert.ThrowsAnyAsync<PostgresException>(() => t.ExecuteAsync(
            "insert into core.retention_rule (tenant_id, company_id, rule_id, effective_from, record_class, country_code, retention_years, start_event, created_by) " +
            "values (@t, @c, @r, now() + interval '1 day', 'LEDGER', 'SE', 5, 'RECORD_DATE', 'x')", ("r", ruleId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.retention_rule where tenant_id = @t"));
    }

    [Fact]
    public async Task OnlyTheTenantsOwnRulesAreVisible()
    {
        var a = await TestTenant.CreateAsync(withRetention: false);
        var b = await TestTenant.CreateAsync(withRetention: false);
        var commands = new RetentionRuleCommands(a.App);
        await commands.CreateVersionAsync(a.TenantId, Manage(a, "x"), Rule(a));

        await using (var tx = await TenantTransaction.BeginAsync(a.App, b.TenantId, CommandRole.Read))
            Assert.Empty(await RetentionRuleCommands.ReadVersionsAsync(tx, b.TenantId, a.CompanyId, default));
        await using var own = await TenantTransaction.BeginAsync(a.App, a.TenantId, CommandRole.Read);
        Assert.Single(await RetentionRuleCommands.ReadVersionsAsync(own, a.TenantId, a.CompanyId, default));
    }
}
