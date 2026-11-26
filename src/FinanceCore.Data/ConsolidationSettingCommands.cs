using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// company-consolidation-settings:create-version (23, 3.1, ADR-224, permission write.config): ownership and method of a company from a later effective_from. Checked at effective_from: the company is a
/// member of a group (CONFIG.NOT_A_GROUP_MEMBER), the parent is a member of the same group, the percentage fits (CONFIG.OWNERSHIP_INVALID), the parent chain does not return to the company
/// (CONFIG.OWNERSHIP_CYCLE) and the translation method fits the currencies (CONFIG.TRANSLATION_METHOD_INVALID). The core does not judge the method against the percentage. A new row restates nothing.
/// </summary>
public sealed class ConsolidationSettingCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;
    private const string CommandName = "company-consolidation-settings:create-version";

    public static readonly MasterReadSpec ReadSpec = new("company-consolidation-settings", "company_consolidation_setting_version", "company_id", ["company_id", "parent_company_id", "consolidation_method", "translation_method"], "company_id, effective_from", Versioned: true);

    private static CommandResult<ConsolidationSettingVersionResult> Reject(CommandError e) => CommandResult<ConsolidationSettingVersionResult>.Rejected([e]);

    public Task<CommandResult<ConsolidationSettingVersionResult>> CreateVersionAsync(Guid tenantId, CommandContext ctx, CreateConsolidationSettingVersionRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        Require(req.Fields, "/fields", errors, "effective_from", "consolidation_method", "translation_method");
        var patches = Validate(req.Fields, ConsolidationFields.Version, "/fields", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<ConsolidationSettingVersionResult>.Rejected(errors));
        byte[] fingerprint = ConsolidationFields.Fingerprint(req);

        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                await using var cmd = tx.Command("select entity_id from core.master_change_log where tenant_id = @t and command_log_id = @l limit 1");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("l", earlier.CommandLogId);
                return await cmd.ExecuteScalarAsync(ct) is string key
                    ? CommandResult<ConsolidationSettingVersionResult>.Ok(new ConsolidationSettingVersionResult(req.CompanyId, DateOnly.ParseExact(key.Split('/')[1], "yyyy-MM-dd", CultureInfo.InvariantCulture)), earlier.CommandLogId, replay: true)
                    : CommandResult<ConsolidationSettingVersionResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""));
            }
            string? local = await TextAsync(tx, "select local_currency from core.company where tenant_id = @t and company_id = @x", tenantId, req.CompanyId, ct);
            if (local is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", req.CompanyId)));
            var f = patches.ToDictionary(p => p.Field.Name);
            DateOnly from = (DateOnly)f["effective_from"].Value!;
            Guid? parent = f.TryGetValue("parent_company_id", out var pp) ? pp.Value as Guid? : null;
            decimal? pct = f.TryGetValue("ownership_pct", out var op) ? op.Value as decimal? : null;
            string method = f["translation_method"].Text!;

            await using (var cmd = tx.Command("select max(effective_from) from core.company_consolidation_setting_version where tenant_id = @t and company_id = @c"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
                if (await cmd.ExecuteScalarAsync(ct) is DateTime latest && from <= DateOnly.FromDateTime(latest))
                    return Reject(Error("CONFIG.VALID_FROM_NOT_LATER", "P2", "/fields/effective_from", ("effective_from", from), ("latest_effective_from", DateOnly.FromDateTime(latest))));
            }
            Guid? group = await GroupAsync(tx, tenantId, req.CompanyId, from, ct);
            if (group is null) return Reject(Error("CONFIG.NOT_A_GROUP_MEMBER", "P2", "/company_id", ("company_id", req.CompanyId), ("effective_from", from)));
            if (!ConsolidationFields.OwnershipFits(parent, pct))
                return Reject(Error("CONFIG.OWNERSHIP_INVALID", "P2", "/fields/ownership_pct", ("reason", "above 0 and at most 100, present exactly with a parent")));
            if (parent is { } p)
            {
                if (p == req.CompanyId || await GroupAsync(tx, tenantId, p, from, ct) != group)
                    return Reject(Error("CONFIG.OWNERSHIP_INVALID", "P2", "/fields/parent_company_id", ("reason", "the parent is not another member of the same group")));
                if (await ChainReturnsAsync(tx, tenantId, req.CompanyId, p, from, ct))
                    return Reject(Error("CONFIG.OWNERSHIP_CYCLE", "P2", "/fields/parent_company_id", ("company_id", req.CompanyId)));
            }
            string groupCurrency = (await TextAsync(tx, "select group_currency from core.company_group where tenant_id = @t and group_id = @x", tenantId, group.Value, ct))!.Trim();
            if (!ConsolidationFields.TranslationFits(method, local.Trim(), groupCurrency))
                return Reject(Error("CONFIG.TRANSLATION_METHOD_INVALID", "P2", "/fields/translation_method", ("local_currency", local.Trim()), ("group_currency", groupCurrency)));

            var cols = new List<string> { "tenant_id", "company_id", "created_by" };
            var vals = new List<string> { "@t", "@c", "@by" };
            await using (var insert = tx.Command(""))
            {
                insert.Parameters.AddWithValue("t", tenantId); insert.Parameters.AddWithValue("c", req.CompanyId); insert.Parameters.AddWithValue("by", ctx.Actor);
                for (int i = 0; i < patches.Count; i++) { cols.Add(patches[i].Field.Name); vals.Add($"@p{i}"); AddParam(insert, $"p{i}", patches[i]); }
                insert.CommandText = $"insert into core.company_consolidation_setting_version ({string.Join(", ", cols)}) values ({string.Join(", ", vals)})";
                await insert.ExecuteNonQueryAsync(ct);
            }
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            string entityKey = $"{req.CompanyId:D}/{from:yyyy-MM-dd}";
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "COMPANY_CONSOLIDATION_SETTING", entityKey, "CREATE", null, false, null, null, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, CommandName, fingerprint, "COMPANY_CONSOLIDATION_SETTING", req.CompanyId, logId, ct);
            await EventAsync(tx, tenantId, ctx, "config.version_created", "COMPANY_CONSOLIDATION_SETTING", entityKey, "CREATED", patches.Select(x => x.Field.Name).ToList(), ct);
            await tx.CommitAsync(ct);
            return CommandResult<ConsolidationSettingVersionResult>.Ok(new ConsolidationSettingVersionResult(req.CompanyId, from), logId);
        });
    }

    private static async Task<string?> TextAsync(TenantTransaction tx, string sql, Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("x", id);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>The group the company is a member of at the date (ADR-018), or null.</summary>
    private static async Task<Guid?> GroupAsync(TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly date, CancellationToken ct)
    {
        await using var cmd = tx.Command("select group_id from core.group_membership where tenant_id = @t and company_id = @c and valid_from <= @d and (valid_to is null or valid_to > @d)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", date);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>Follows the parents in force at the date from the new parent: true when the chain reaches the company.</summary>
    private static async Task<bool> ChainReturnsAsync(TenantTransaction tx, Guid tenantId, Guid company, Guid parent, DateOnly date, CancellationToken ct)
    {
        Guid? current = parent;
        for (int depth = 0; current is { } c && depth < 1000; depth++)
        {
            if (c == company) return true;
            await using var cmd = tx.Command(
                "select parent_company_id from core.company_consolidation_setting_version where tenant_id = @t and company_id = @c and effective_from <= @d order by effective_from desc limit 1");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", c); cmd.Parameters.AddWithValue("d", date);
            current = await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
        return false;
    }

    private async Task<CommandResult<ConsolidationSettingVersionResult>> RunAsync(Func<Task<CommandResult<ConsolidationSettingVersionResult>>> once)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await once();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<ConsolidationSettingVersionResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
        }
    }
}
