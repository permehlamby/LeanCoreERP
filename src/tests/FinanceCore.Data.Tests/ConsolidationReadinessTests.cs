using FinanceCore.Core.Commands;
using FinanceCore.Core.Periods;

namespace FinanceCore.Data.Tests;

/// <summary>The three consolidation warnings of companies:check-readiness (07, 11.3, 23, 9): a warning, never an error.</summary>
[Collection("db")]
public class ConsolidationReadinessTests
{
    private static async Task<CommandResult<ReadinessReport>> Check(TestTenant t, string key) =>
        await new CompanyActivator(t.App).CheckReadinessAsync(t.TenantId, t.Context(key, "read.config"), t.CompanyId);

    private static string[] Warned(CommandResult<ReadinessReport> r) =>
        r.Warnings.Where(w => w.Code == "CONFIG.READINESS_WARNING").Select(w => (string)w.Params!["check"]!).Order().ToArray();

    private static async Task<Guid> Group(TestTenant t) =>
        Guid.Parse(await t.ScalarAsync<string>("select group_id::text from core.group_membership where tenant_id = @t and company_id = @c", ("c", t.CompanyId)));

    private static async Task<Guid> GroupAccount(TestTenant t, Guid group, string code)
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.group_chart_account (tenant_id, group_account_id, group_id, code, name, account_type, created_by, updated_by) values (@t, @id, @g, @code, 'Cash', 'ASSET', 'test', 'test')",
            ("id", id), ("g", group), ("code", code));
        return id;
    }

    private static async Task Map(TestTenant t, Guid groupAccount)
    {
        Guid account = Guid.Parse(await t.ScalarAsync<string>("select account_id::text from core.gl_account where tenant_id = @t and company_id = @c and account_no = '1000'", ("c", t.CompanyId)));
        await t.ExecuteAsync("insert into core.account_group_mapping (tenant_id, company_id, account_id, valid_from, group_account_id, created_by) values (@t, @c, @a, date '2026-01-01', @g, 'test')",
            ("c", t.CompanyId), ("a", account), ("g", groupAccount));
    }

    [Fact]
    public async Task ACompanyWithoutConsolidationDataHasNoWarning()
    {
        var t = await TestTenant.CreateReadyAsync();
        var r = await Check(t, "a");
        Assert.True(r.Status == CommandStatus.Ok, string.Join(", ", r.Errors.Select(e => e.Code)));
        Assert.Empty(Warned(r));
        Assert.Equal(3, r.Value!.Checks.Count(c => c.IsWarning));
    }

    [Fact]
    public async Task GroupAccountsWithoutAMappingWarnAndAMappingClearsIt()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid group = await Group(t);
        Guid ga = await GroupAccount(t, group, "G100");
        var warned = await Check(t, "a");
        Assert.True(warned.Status == CommandStatus.Ok);
        Assert.Equal(["GROUP_ACCOUNT_MAPPING"], Warned(warned));

        await Map(t, ga);
        Assert.Empty(Warned(await Check(t, "b")));
    }

    [Fact]
    public async Task AMappingToAnotherGroupAndAMethodWithoutAGroupWarn()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid other = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.company_group (tenant_id, group_id, group_code, name, group_currency) values (@t, @g, 'GRP2', 'Other', 'EUR')", ("g", other));
        await Map(t, await GroupAccount(t, other, "X100"));
        var r = await Check(t, "a");
        Assert.True(r.Status == CommandStatus.Ok);
        Assert.Equal(["GROUP_CHANGE_MAPPING"], Warned(r));

        // a method other than NOT_CONSOLIDATED with the membership ended: the membership check is an error, the consolidation check a warning in the report
        await t.ExecuteAsync("insert into core.company_consolidation_setting_version (tenant_id, company_id, effective_from, consolidation_method, translation_method, created_by) values (@t, @c, date '2026-01-01', 'FULL', 'CURRENT_RATE', 'test')", ("c", t.CompanyId));
        await t.ExecuteAsync("update core.group_membership set valid_to = date '2026-02-01' where tenant_id = @t and company_id = @c", ("c", t.CompanyId));
        var ended = await Check(t, "b");
        Assert.Equal(CommandStatus.Rejected, ended.Status);
        Assert.False(ended.Value!.Ready);
        Assert.Contains(ended.Value.Checks, c => c.Check == "CONSOLIDATION_MEMBERSHIP" && !c.Passed && c.IsWarning);
        Assert.DoesNotContain(ended.Errors, e => (string?)e.Params?["check"] == "CONSOLIDATION_MEMBERSHIP");
    }
}
