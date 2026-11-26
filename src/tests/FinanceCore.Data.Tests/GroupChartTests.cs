using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;

namespace FinanceCore.Data.Tests;

/// <summary>group-chart-accounts (07, 6.4, 23, 4): create, update, deactivate, reactivate, get and list on the generic master commands.</summary>
[Collection("db")]
public class GroupChartTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static CommandContext Write(TestTenant t, string key) => t.Context(key, GroupChartCommands.Permission);
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private static async Task<Guid> GroupId(TestTenant t) =>
        Guid.Parse(await t.ScalarAsync<string>("select group_id::text from core.group_membership where tenant_id = @t and company_id = @c", ("c", t.CompanyId)));

    private static Task<CommandResult<MasterResult>> Create(TestTenant t, string key, Dictionary<string, object?> fields) =>
        new GroupChartCommands(t.App).CreateAsync(t.TenantId, Write(t, key), new(fields));

    [Fact]
    public async Task AnAccountIsCreatedUpdatedDeactivatedAndRead()
    {
        var t = await TestTenant.CreateReadyAsync();
        var cmds = new GroupChartCommands(t.App);
        Guid group = await GroupId(t);
        var created = await Create(t, "a", D(("group_id", group), ("code", "G100"), ("name", "Cash"), ("account_type", "ASSET")));
        Assert.True(created.Status == CommandStatus.Ok, Describe(created));
        Guid id = created.Value!.EntityId;

        var upd = await cmds.UpdateAsync(t.TenantId, Write(t, "b"), new(id, 1, D(("name", "Cash and bank"), ("translation_rate_basis", "HISTORICAL"))));
        Assert.True(upd.Status == CommandStatus.Ok, Describe(upd));
        Assert.Equal(2L, upd.Value!.RowVersion);
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await cmds.UpdateAsync(t.TenantId, Write(t, "c"), new(id, 1, D(("name", "Again"))))));
        // the group and the type are fixed at creation
        Assert.Equal(["SCHEMA.UNKNOWN_FIELD"], Codes(await cmds.UpdateAsync(t.TenantId, Write(t, "d"), new(id, 2, D(("account_type", "EQUITY"))))));

        var read = await new MasterReads(t.App).GetAsync(t.TenantId, t.Context("r", "read.config"), GroupChartCommands.ReadSpec, new(id));
        Assert.True(read.Status == CommandStatus.Ok, Describe(read));
        Assert.Equal("Cash and bank", read.Value!.Row.GetProperty("name").GetString());
        Assert.Equal("HISTORICAL", read.Value.Row.GetProperty("translation_rate_basis").GetString());

        var off = await cmds.DeactivateAsync(t.TenantId, Write(t, "e"), new(id, 2, "CLOSED"));
        Assert.True(off.Status == CommandStatus.Ok, Describe(off));
        var list = await new MasterReads(t.App).ListAsync(t.TenantId, t.Context("l", "read.config"), GroupChartCommands.ReadSpec, new());
        Assert.Empty(list.Value!.Items);
        Assert.True((await cmds.ReactivateAsync(t.TenantId, Write(t, "f"), new(id, 3, "ADMIN"))).Status == CommandStatus.Ok);
    }

    [Fact]
    public async Task TheCodeIsUniqueInTheGroupTheGroupMustExistAndTheBasisIsAClosedList()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid group = await GroupId(t);
        Assert.True((await Create(t, "a", D(("group_id", group), ("code", "G100"), ("name", "Cash"), ("account_type", "ASSET")))).Status == CommandStatus.Ok);
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await Create(t, "b", D(("group_id", group), ("code", "G100"), ("name", "Other"), ("account_type", "ASSET")))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Create(t, "c", D(("group_id", Guid.NewGuid()), ("code", "G200"), ("name", "X"), ("account_type", "ASSET")))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Create(t, "d", D(("group_id", group), ("code", "G300"), ("name", "X"), ("account_type", "ASSET"), ("translation_rate_basis", "SPOT")))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Create(t, "e", D(("group_id", group), ("code", "G400"), ("name", "X"), ("account_type", "INCOME")))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await Create(t, "f", D(("group_id", group), ("code", "G500"), ("name", "X")))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new GroupChartCommands(t.App).CreateAsync(t.TenantId, t.Context("g", "journal.post"), new(D(("group_id", group))))));
    }

    [Fact]
    public void TheDefaultBasisFollowsTheAccountType()
    {
        Assert.Equal("CLOSING", GroupChartFields.EffectiveBasis("ASSET", null));
        Assert.Equal("CLOSING", GroupChartFields.EffectiveBasis("LIABILITY", null));
        Assert.Equal("AVERAGE", GroupChartFields.EffectiveBasis("REVENUE", null));
        Assert.Equal("AVERAGE", GroupChartFields.EffectiveBasis("EXPENSE", null));
        Assert.Equal("HISTORICAL", GroupChartFields.EffectiveBasis("EQUITY", null));
        Assert.Equal("CLOSING", GroupChartFields.EffectiveBasis("EQUITY", "CLOSING"));
    }
}
