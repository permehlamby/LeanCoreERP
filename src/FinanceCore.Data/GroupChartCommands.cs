using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// group-chart-accounts:create, :update, :deactivate and :reactivate (07, 6.4, 23, 4, ADR-227; permission write.config) on the generic master commands. The group must exist, the code is unique
/// in the group (CONFIG.DUPLICATE_CODE). Group and account type are set at creation only. Nothing blocks a deactivation yet: the mapping of company accounts (account_group_mapping) is not built.
/// A row is addressed by `id`.
/// </summary>
public sealed class GroupChartCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;
    public const string Resource = "group-chart-accounts";
    private readonly MasterTableCommands _generic = new(source);

    public static readonly MasterReadSpec ReadSpec = new(Resource, "group_chart_account", "group_account_id", ["group_id", "code", "account_type", "translation_rate_basis"], "group_id, code");

    private static readonly MasterSpec Spec = new("group_chart_account", "group_account_id", "GROUP_CHART_ACCOUNT", "/id", Resource, Permission,
        GroupChartFields.Create, GroupChartFields.Update, ["group_id", "code", "name", "account_type"], [], async i =>
        {
            var errors = new List<CommandError>();
            Guid? id = i.Id;
            string? group = i.Get("group_id");
            if (i.Creating && !await ExistsAsync(i.Tx, "select 1 from core.company_group where tenant_id = @t and group_id = @x", i.TenantId, ("x", Guid.Parse(group!))))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/group_id", ("group_id", group)));
            else if ((i.Creating || i.Changed("code")) && i.Get("code") is { } code
                && await ExistsAsync(i.Tx, "select 1 from core.group_chart_account where tenant_id = @t and group_id = @g and code = @c and group_account_id is distinct from @id::uuid", i.TenantId,
                    ("g", Guid.Parse(group ?? Guid.Empty.ToString("D"))), ("c", code), ("id", (object?)id ?? DBNull.Value)))
                errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/fields/code"));
            return errors;
        });

    public Task<CommandResult<MasterResult>> CreateAsync(Guid t, CommandContext c, CreateInventoryMasterRequest r, CancellationToken ct = default) =>
        _generic.CreateAsync(t, c, Spec, r.Fields, InventoryRules.Fingerprint(Resource, r), ct);

    public Task<CommandResult<MasterResult>> UpdateAsync(Guid t, CommandContext c, UpdateInventoryMasterRequest r, CancellationToken ct = default) =>
        _generic.UpdateAsync(t, c, Spec, r.Id, r.ExpectedVersion, r.Fields, InventoryRules.Fingerprint(Resource, r), ct);

    public Task<CommandResult<MasterResult>> DeactivateAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, Spec, r, true, ct);

    public Task<CommandResult<MasterResult>> ReactivateAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, Spec, r, false, ct);
}
