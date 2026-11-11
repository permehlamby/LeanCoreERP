using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// The dimension commands (07, 6.2, 6.5, 11.1): dimension-types:create, :update, :deactivate, :reactivate and the same four for dimension-values
/// (write.master), and account-dimension-rules:create and :update (write.config). The four standard types exist in every tenant (0022), so a type
/// that a command creates is FREE. Every change is a row of master_change_log and an audit event. What these commands cannot change once lines refer to
/// a value (its type, its company) the database guards too (0022).
/// </summary>
public sealed class DimensionCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.DimensionPermission;
    public const string RulePermission = MasterRules.ConfigPermission;

    private sealed record Spec(
        string Table, string IdColumn, string Entity, string IdPath, string Resource, IReadOnlyList<MasterField> Fields, string[] Required,
        string[] ExtraColumns, bool SetKindFree)
    {
        public IReadOnlyList<string> Columns => [.. Fields.Select(f => f.Name), "active", "row_version", .. ExtraColumns];
    }

    private static readonly Spec TypeSpec = new("dimension_type", "dimension_type_id", "DIMENSION_TYPE", "/dimension_type_id", "dimension-types",
        DimensionFields.Type, ["type_code", "name"], ["kind"], true);
    private static readonly Spec ValueSpec = new("dimension_value", "dimension_value_id", "DIMENSION_VALUE", "/dimension_value_id", "dimension-values",
        DimensionFields.Value, ["dimension_type_id", "value_code", "name", "valid_from"], [], false);

    // ---------------------------------------------------------------------------------------------------------
    // Types and values
    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<MasterResult>> CreateTypeAsync(Guid tenantId, CommandContext ctx, CreateDimensionTypeRequest req, CancellationToken ct = default) =>
        CreateAsync(tenantId, ctx, TypeSpec, req.Fields, MasterRules.Fingerprint(req), ct);

    public Task<CommandResult<MasterResult>> UpdateTypeAsync(Guid tenantId, CommandContext ctx, UpdateDimensionTypeRequest req, CancellationToken ct = default) =>
        UpdateAsync(tenantId, ctx, TypeSpec, req.DimensionTypeId, req.ExpectedVersion, req.Fields, MasterRules.Fingerprint(req), ct);

    public Task<CommandResult<MasterResult>> DeactivateTypeAsync(Guid tenantId, CommandContext ctx, MasterStatusRequest req, CancellationToken ct = default) =>
        StatusAsync(tenantId, ctx, TypeSpec, req, "dimension-types:deactivate", true, ct);

    public Task<CommandResult<MasterResult>> ReactivateTypeAsync(Guid tenantId, CommandContext ctx, MasterStatusRequest req, CancellationToken ct = default) =>
        StatusAsync(tenantId, ctx, TypeSpec, req, "dimension-types:reactivate", false, ct);

    public Task<CommandResult<MasterResult>> CreateValueAsync(Guid tenantId, CommandContext ctx, CreateDimensionValueRequest req, CancellationToken ct = default) =>
        CreateAsync(tenantId, ctx, ValueSpec, req.Fields, MasterRules.Fingerprint(req), ct);

    public Task<CommandResult<MasterResult>> UpdateValueAsync(Guid tenantId, CommandContext ctx, UpdateDimensionValueRequest req, CancellationToken ct = default) =>
        UpdateAsync(tenantId, ctx, ValueSpec, req.DimensionValueId, req.ExpectedVersion, req.Fields, MasterRules.Fingerprint(req), ct);

    public Task<CommandResult<MasterResult>> DeactivateValueAsync(Guid tenantId, CommandContext ctx, MasterStatusRequest req, CancellationToken ct = default) =>
        StatusAsync(tenantId, ctx, ValueSpec, req, "dimension-values:deactivate", true, ct);

    public Task<CommandResult<MasterResult>> ReactivateValueAsync(Guid tenantId, CommandContext ctx, MasterStatusRequest req, CancellationToken ct = default) =>
        StatusAsync(tenantId, ctx, ValueSpec, req, "dimension-values:reactivate", false, ct);

    private Task<CommandResult<MasterResult>> CreateAsync(
        Guid tenantId, CommandContext ctx, Spec spec, IReadOnlyDictionary<string, object?> fields, byte[] fingerprint, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        Require(fields, "/fields", errors, spec.Required);
        var patches = Validate(fields, spec.Fields, "/fields", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        string command = spec.Resource + ":create";
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var replayed = await ReplayOrNullAsync(tx, tenantId, ctx, spec, fingerprint, ct);
            if (replayed is not null) return replayed;

            var effective = patches.ToDictionary(p => p.Field.Name, p => p.Text);
            var problems = await CheckAsync(tx, tenantId, spec, effective, null, null, ct);
            if (problems.Count > 0) return CommandResult<MasterResult>.Rejected(problems);

            Guid id = Guid.NewGuid(), changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            var fixedColumns = new List<(string, NpgsqlDbType, object?)> { (spec.IdColumn, NpgsqlDbType.Uuid, id) };
            if (spec.SetKindFree) fixedColumns.Add(("kind", NpgsqlDbType.Text, "FREE"));
            await InsertAsync(tx, spec.Table, fixedColumns, patches, ctx, tenantId);
            await LogAsync(tx, tenantId, ctx, logId, changeSet, spec.Entity, id.ToString("D"), "CREATE", null, false, null, null, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, command, fingerprint, spec.Entity, id, logId, ct);
            var names = patches.Select(p => p.Field.Name).ToList();
            await EventAsync(tx, tenantId, ctx, "master.created", spec.Entity, id.ToString("D"), "CREATED", names, ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(id, 1, changeSet, names), logId);
        });
    }

    private Task<CommandResult<MasterResult>> UpdateAsync(
        Guid tenantId, CommandContext ctx, Spec spec, Guid id, long expectedVersion, IReadOnlyDictionary<string, object?> fields, byte[] fingerprint, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (id == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", spec.IdPath, ("reason", "required")));
        if (expectedVersion < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        var patches = Validate(fields, spec.Fields, "/fields", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        string command = spec.Resource + ":update";
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var replayed = await ReplayOrNullAsync(tx, tenantId, ctx, spec, fingerprint, ct);
            if (replayed is not null) return replayed;

            var current = await ReadRowAsync(tx, spec.Table, spec.IdColumn + " = @id", spec.Columns, true, tenantId, ("id", id));
            if (current is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", spec.IdPath, (spec.IdColumn, id)));
            long version = long.Parse(current["row_version"]!, CultureInfo.InvariantCulture);
            if (version != expectedVersion)
                return Reject(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", version), ("expected_version", expectedVersion)));

            var effectiveChanges = patches.Where(p => p.Text != current[p.Field.Name]).ToList();
            var effective = spec.Fields.ToDictionary(f => f.Name, f => current[f.Name]);
            foreach (Patch p in effectiveChanges) effective[p.Field.Name] = p.Text;
            var problems = await CheckAsync(tx, tenantId, spec, effective, current, id, ct, effectiveChanges);
            if (problems.Count > 0) return CommandResult<MasterResult>.Rejected(problems);
            if (effectiveChanges.Count == 0) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/fields"));

            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            await MasterSupport.UpdateAsync(tx, spec.Table, spec.IdColumn + " = @id", effectiveChanges, ctx, tenantId, ("id", id));
            foreach (Patch p in effectiveChanges)
                await LogAsync(tx, tenantId, ctx, logId, changeSet, spec.Entity, id.ToString("D"), "UPDATE", p.Field.Name, false, current[p.Field.Name], p.Text, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, command, fingerprint, spec.Entity, id, logId, ct);
            var names = effectiveChanges.Select(p => p.Field.Name).ToList();
            await EventAsync(tx, tenantId, ctx, "master.changed", spec.Entity, id.ToString("D"), "CHANGED", names, ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(id, version + 1, changeSet, names), logId);
        });
    }

    private Task<CommandResult<MasterResult>> StatusAsync(
        Guid tenantId, CommandContext ctx, Spec spec, MasterStatusRequest req, string command, bool deactivate, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.Id == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", spec.IdPath, ("reason", "required")));
        if (req.ExpectedVersion < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/reason_code"));
        else if (req.ReasonCode.Length > 50) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/reason_code", ("reason", "at most 50 characters")));
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(command, req);
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var replayed = await ReplayOrNullAsync(tx, tenantId, ctx, spec, fingerprint, ct);
            if (replayed is not null) return replayed;

            var row = await ReadRowAsync(tx, spec.Table, spec.IdColumn + " = @id", ["active", "row_version", .. spec.ExtraColumns], true, tenantId, ("id", req.Id));
            if (row is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", spec.IdPath, (spec.IdColumn, req.Id)));
            long version = long.Parse(row["row_version"]!, CultureInfo.InvariantCulture);
            if (version != req.ExpectedVersion)
                return Reject(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", version), ("expected_version", req.ExpectedVersion)));
            // the four standard types exist in every tenant and are never deactivated (07, 6.5)
            if (deactivate && spec == TypeSpec && row["kind"] == "STANDARD")
                return Reject(Error("CONFIG.REFERENCED", "P2", spec.IdPath, ("reason", "STANDARD_TYPE")));
            if ((row["active"] == "true") != deactivate) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", spec.IdPath, ("active", row["active"])));

            await using (var cmd = tx.Command(
                $"update core.{spec.Table} set active = @a, row_version = row_version + 1, updated_at = now(), updated_by = @by where tenant_id = @t and {spec.IdColumn} = @id"))
            {
                cmd.Parameters.AddWithValue("a", !deactivate); cmd.Parameters.AddWithValue("by", ctx.Actor);
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", req.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            await LogAsync(tx, tenantId, ctx, logId, changeSet, spec.Entity, req.Id.ToString("D"), deactivate ? "DEACTIVATE" : "REACTIVATE", null, false, null, null, req.ReasonCode);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, command, fingerprint, spec.Entity, req.Id, logId, ct);
            await EventAsync(tx, tenantId, ctx, deactivate ? "master.deactivated" : "master.reactivated", spec.Entity, req.Id.ToString("D"),
                deactivate ? "DEACTIVATED" : "REACTIVATED", ["active"], ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(req.Id, version + 1, changeSet, ["active"]), logId);
        });
    }

    /// <summary>The checks of a create or an update of a type or a value, on the values as they will be (the current ones with the patches).</summary>
    private static async Task<List<CommandError>> CheckAsync(
        TenantTransaction tx, Guid tenantId, Spec spec, Dictionary<string, string?> effective, Dictionary<string, string?>? current, Guid? id,
        CancellationToken ct, List<Patch>? changes = null)
    {
        var errors = new List<CommandError>();
        bool Changed(string name) => current is null || changes!.Any(p => p.Field.Name == name);
        string? Get(string name) => effective.GetValueOrDefault(name);
        if (spec == TypeSpec)
        {
            if (current is not null && current["kind"] == "STANDARD" && Changed("type_code"))
                errors.Add(Error("CONFIG.IMMUTABLE_AFTER_POSTING", "P2", "/fields/type_code", ("reason", "the code of a standard type never changes")));
            if (Changed("type_code") && await ExistsAsync(tx, "select 1 from core.dimension_type where tenant_id = @t and type_code = @c and dimension_type_id is distinct from @id::uuid", tenantId,
                    ("c", effective["type_code"]!), ("id", (object?)id ?? DBNull.Value)))
                errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/fields/type_code", ("code", effective["type_code"])));
            return errors;
        }

        // a value
        Guid typeId = Guid.Parse(effective["dimension_type_id"]!);
        bool? typeActive = await ReadBoolAsync(tx, "select active from core.dimension_type where tenant_id = @t and dimension_type_id = @x", tenantId, typeId);
        if (typeActive is null) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/dimension_type_id", ("dimension_type_id", typeId)));
        else if (current is null && typeActive == false) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/dimension_type_id", ("dimension_type_id", typeId), ("reason", "TYPE_INACTIVE")));

        if (Get("company_id") is { } company && Changed("company_id")
            && !await ExistsAsync(tx, "select 1 from core.company where tenant_id = @t and company_id = @x", tenantId, ("x", Guid.Parse(company))))
            errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/company_id", ("company_id", company)));

        if (Get("valid_to") is { } to && DateOnly.Parse(to, CultureInfo.InvariantCulture) < DateOnly.Parse(Get("valid_from")!, CultureInfo.InvariantCulture))
            errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/valid_to", ("reason", "not before valid_from")));

        if (Changed("value_code") || Changed("dimension_type_id"))
            if (await ExistsAsync(tx, "select 1 from core.dimension_value where tenant_id = @t and dimension_type_id = @ty and value_code = @c and dimension_value_id is distinct from @id::uuid", tenantId,
                    ("ty", typeId), ("c", Get("value_code")!), ("id", (object?)id ?? DBNull.Value)))
                errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/fields/value_code", ("code", Get("value_code"))));

        if (Get("parent_value_id") is { } parentText && Changed("parent_value_id"))
        {
            Guid parent = Guid.Parse(parentText);
            Guid? parentType = await ReadGuidAsync(tx, "select dimension_type_id from core.dimension_value where tenant_id = @t and dimension_value_id = @x", tenantId, parent);
            if (parentType is null || parentType != typeId)
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/parent_value_id", ("parent_value_id", parent), ("reason", parentType is null ? "NOT_FOUND" : "OTHER_TYPE")));
            else if (id is { } self && await LeadsBackAsync(tx, tenantId, parent, self))
                errors.Add(Error("CONFIG.HIERARCHY_CYCLE", "P2", "/fields/parent_value_id", ("parent_value_id", parent)));
        }

        if (current is not null && id is { } valueId)
        {
            bool referenced = await ExistsAsync(tx, "select 1 where core.dimension_value_referenced(@t, @x)", tenantId, ("x", valueId));
            if (referenced && Changed("dimension_type_id"))
                errors.Add(Error("CONFIG.IMMUTABLE_AFTER_POSTING", "P2", "/fields/dimension_type_id", ("reason", "lines refer to the value")));
            if (referenced && Changed("company_id") && Get("company_id") is { } restricted
                && await ExistsAsync(tx,
                    "select 1 from core.journal_line l where l.tenant_id = @t and l.company_id <> @c and @x in (l.cost_center_id, l.project_id, l.profit_center_id, l.business_unit_id) " +
                    "union all select 1 from core.journal_line_dimension d where d.tenant_id = @t and d.company_id <> @c and d.dimension_value_id = @x", tenantId,
                    ("c", Guid.Parse(restricted)), ("x", valueId)))
                errors.Add(Error("CONFIG.DIMENSION_COMPANY_MISMATCH", "P2", "/fields/company_id", ("company_id", restricted)));
        }
        return errors;
    }

    /// <summary>True when the parent chain that starts at <paramref name="parent"/> reaches <paramref name="self"/> (or is <paramref name="self"/>).</summary>
    private static async Task<bool> LeadsBackAsync(TenantTransaction tx, Guid tenantId, Guid parent, Guid self)
    {
        Guid? up = parent;
        for (int depth = 0; up is not null && depth <= 1000; depth++)
        {
            if (up == self) return true;
            up = await ReadGuidAsync(tx, "select parent_value_id from core.dimension_value where tenant_id = @t and dimension_value_id = @x", tenantId, up.Value);
        }
        return up is not null;   // deeper than 1000 levels counts as a cycle
    }

    private static async Task<bool?> ReadBoolAsync(TenantTransaction tx, string sql, Guid tenantId, Guid key)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("x", key);
        return await cmd.ExecuteScalarAsync() is bool b ? b : null;
    }

    private static async Task<Guid?> ReadGuidAsync(TenantTransaction tx, string sql, Guid tenantId, Guid key)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("x", key);
        return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
    }

    private static async Task<bool> ExistsAsync(TenantTransaction tx, string sql, Guid tenantId, params (string, object)[] keys)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId);
        foreach (var (n, v) in keys) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task<CommandResult<MasterResult>?> ReplayOrNullAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, Spec spec, byte[] fingerprint, CancellationToken ct)
    {
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is null) return null;
        if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
            return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
        MasterResult? replay = earlier.EntityId is null ? null : await ReplayAsync(tx, tenantId, earlier.CommandLogId, spec.Table, spec.IdColumn, earlier.EntityId.Value);
        return replay is null ? CommandResult<MasterResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", "")) : CommandResult<MasterResult>.Ok(replay, earlier.CommandLogId, replay: true);
    }

    private static CommandResult<MasterResult> Reject(CommandError error) => CommandResult<MasterResult>.Rejected([error]);

    // ---------------------------------------------------------------------------------------------------------
    // account-dimension-rules (07, 6.2): the key is company, account and type
    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<MasterResult>> CreateRuleAsync(Guid tenantId, CommandContext ctx, CreateAccountDimensionRuleRequest req, CancellationToken ct = default) =>
        RuleAsync(tenantId, ctx, req.CompanyId, req.AccountId, req.DimensionTypeId, null, req.Rule, "account-dimension-rules:create", MasterRules.Fingerprint(req), ct);

    public Task<CommandResult<MasterResult>> UpdateRuleAsync(Guid tenantId, CommandContext ctx, UpdateAccountDimensionRuleRequest req, CancellationToken ct = default) =>
        RuleAsync(tenantId, ctx, req.CompanyId, req.AccountId, req.DimensionTypeId, req.ExpectedVersion, req.Rule, "account-dimension-rules:update", MasterRules.Fingerprint(req), ct);

    private Task<CommandResult<MasterResult>> RuleAsync(
        Guid tenantId, CommandContext ctx, Guid companyId, Guid accountId, Guid typeId, long? expectedVersion, string rule, string command, byte[] fingerprint, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(RulePermission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", RulePermission))));
        var errors = ValidateContext(ctx);
        if (companyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (accountId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/account_id", ("reason", "required")));
        if (typeId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/dimension_type_id", ("reason", "required")));
        if (!DimensionFields.Rules.Contains(rule)) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/rule", ("reason", "one of " + string.Join(", ", DimensionFields.Rules))));
        if (command.EndsWith(":update", StringComparison.Ordinal) && expectedVersion is null or < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        string entityId = $"{accountId:D}/{typeId:D}";   // the company is the account's; 110 characters with it would not fit the 100 of master_change_log
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                MasterResult? replay = await ReplayRuleAsync(tx, tenantId, earlier.CommandLogId, companyId, accountId, typeId);
                return replay is null ? CommandResult<MasterResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", "")) : CommandResult<MasterResult>.Ok(replay, earlier.CommandLogId, replay: true);
            }

            var problems = new List<CommandError>();
            if (!await ExistsAsync(tx, "select 1 from core.gl_account where tenant_id = @t and company_id = @c and account_id = @a", tenantId, ("c", companyId), ("a", accountId)))
                problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/account_id", ("account_id", accountId)));
            if (await ReadBoolAsync(tx, "select active from core.dimension_type where tenant_id = @t and dimension_type_id = @x", tenantId, typeId) is not { } typeActive)
                problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/dimension_type_id", ("dimension_type_id", typeId)));
            else if (!typeActive && expectedVersion is null)
                problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/dimension_type_id", ("dimension_type_id", typeId), ("reason", "TYPE_INACTIVE")));
            // the roles that take no mandatory dimension (07, 6.3): the account of RETAINED_EARNINGS and of the migration roles
            if (problems.Count == 0 && rule == "MANDATORY" && await ExistsAsync(tx,
                    "select 1 from core.account_role_assignment where tenant_id = @t and company_id = @c and account_id = @a and role_code in ('RETAINED_EARNINGS', 'MIGRATION_CLEARING', 'HISTORY_RESULT')",
                    tenantId, ("c", companyId), ("a", accountId)))
                problems.Add(Error("CONFIG.ROLE_ACCOUNT_RULE_VIOLATED", "P2", "/rule", ("rule", "MANDATORY_DIMENSION"), ("account_id", accountId)));
            if (problems.Count > 0) return CommandResult<MasterResult>.Rejected(problems);

            var current = await ReadRowAsync(tx, "account_dimension_rule", "company_id = @c and account_id = @a and dimension_type_id = @ty", ["rule", "row_version"], true, tenantId,
                ("c", companyId), ("a", accountId), ("ty", typeId));
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            long version;
            if (expectedVersion is null)
            {
                if (current is not null) return Reject(Error("CONFIG.DUPLICATE_CODE", "P2", "/dimension_type_id", ("rule", current["rule"]), ("row_version", current["row_version"])));
                await using (var cmd = tx.Command("insert into core.account_dimension_rule (tenant_id, company_id, account_id, dimension_type_id, rule, created_by, updated_by) values (@t, @c, @a, @ty, @r, @by, @by)"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", accountId);
                    cmd.Parameters.AddWithValue("ty", typeId); cmd.Parameters.AddWithValue("r", rule); cmd.Parameters.AddWithValue("by", ctx.Actor);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                version = 1;
                await LogAsync(tx, tenantId, ctx, logId, changeSet, "ACCOUNT_DIMENSION_RULE", entityId, "CREATE", null, false, null, null, null);
            }
            else
            {
                if (current is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/dimension_type_id", ("reason", "NO_RULE")));
                version = long.Parse(current["row_version"]!, CultureInfo.InvariantCulture);
                if (version != expectedVersion)
                    return Reject(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", version), ("expected_version", expectedVersion)));
                if (current["rule"] == rule) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/rule", ("rule", rule)));
                await using (var cmd = tx.Command("update core.account_dimension_rule set rule = @r, row_version = row_version + 1, updated_at = now(), updated_by = @by where tenant_id = @t and company_id = @c and account_id = @a and dimension_type_id = @ty"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", accountId);
                    cmd.Parameters.AddWithValue("ty", typeId); cmd.Parameters.AddWithValue("r", rule); cmd.Parameters.AddWithValue("by", ctx.Actor);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                version++;
                await LogAsync(tx, tenantId, ctx, logId, changeSet, "ACCOUNT_DIMENSION_RULE", entityId, "UPDATE", "rule", false, current["rule"], rule, null);
            }
            await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, command, fingerprint, "ACCOUNT_DIMENSION_RULE", accountId, logId, ct);
            await EventAsync(tx, tenantId, ctx, expectedVersion is null ? "master.created" : "master.changed", "ACCOUNT_DIMENSION_RULE", entityId,
                expectedVersion is null ? "CREATED" : "CHANGED", ["rule"], ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(accountId, version, changeSet, ["rule"]), logId);
        });
    }

    private static async Task<MasterResult?> ReplayRuleAsync(TenantTransaction tx, Guid tenantId, Guid commandLogId, Guid companyId, Guid accountId, Guid typeId)
    {
        Guid? set = null;
        await using (var cmd = tx.Command("select change_set_id from core.master_change_log where tenant_id = @t and command_log_id = @l limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("l", commandLogId);
            if (await cmd.ExecuteScalarAsync() is Guid g) set = g;
        }
        if (set is null) return null;
        await using var v = tx.Command("select row_version from core.account_dimension_rule where tenant_id = @t and company_id = @c and account_id = @a and dimension_type_id = @ty");
        v.Parameters.AddWithValue("t", tenantId); v.Parameters.AddWithValue("c", companyId); v.Parameters.AddWithValue("a", accountId); v.Parameters.AddWithValue("ty", typeId);
        object? version = await v.ExecuteScalarAsync();
        return version is null ? null : new MasterResult(accountId, Convert.ToInt64(version, CultureInfo.InvariantCulture), set.Value, ["rule"]);
    }
}
