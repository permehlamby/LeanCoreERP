using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>What a master table needs for the generic commands: its table, its key column, the fields of a create and of an update, what a create must
/// carry, the fixed columns of a create, the permission, the checks that depend on the table, and what a deactivation must not meet.</summary>
internal sealed record MasterSpec(
    string Table, string IdColumn, string Entity, string IdPath, string Resource, string Permission,
    IReadOnlyList<MasterField> CreateFields, IReadOnlyList<MasterField> UpdateFields, string[] Required, string[] ExtraColumns,
    MasterCheck Check, MasterDeactivateGuard? DeactivateGuard = null)
{
    public IReadOnlyList<string> Columns => [.. CreateFields.Select(f => f.Name).Union(UpdateFields.Select(f => f.Name)), "active", "row_version", .. ExtraColumns];
}

/// <summary>The values a check sees: the row as it will be (the current values with the changes), the current row and its id for an update, and the changes.</summary>
internal sealed record MasterCheckInput(
    TenantTransaction Tx, Guid TenantId, MasterSpec Spec, Dictionary<string, string?> Effective, Dictionary<string, string?>? Current, Guid? Id,
    List<Patch>? Changes, CancellationToken Ct)
{
    public bool Creating => Current is null;
    public bool Changed(string name) => Current is null || Changes!.Any(p => p.Field.Name == name);
    public string? Get(string name) => Effective.GetValueOrDefault(name);
}

internal delegate Task<List<CommandError>> MasterCheck(MasterCheckInput input);

/// <summary>Returns an error when the row must not be deactivated. <paramref name="row"/> holds the columns of the spec as text.</summary>
internal delegate Task<CommandError?> MasterDeactivateGuard(TenantTransaction tx, Guid tenantId, Guid id, Dictionary<string, string?> row, CancellationToken ct);

/// <summary>
/// The generic master commands of 07, 11.1 for a table with a uuid key and an `active` flag: create, update with `expected_version`, deactivate and
/// reactivate. Every change is a row of master_change_log and an audit event, a repeated key is a replay. The checks that belong to a table are the
/// spec's. The dimension commands (DimensionCommands) have their own copy of this and may move here.
/// </summary>
internal sealed class MasterTableCommands(NpgsqlDataSource source)
{
    public Task<CommandResult<MasterResult>> CreateAsync(
        Guid tenantId, CommandContext ctx, MasterSpec spec, IReadOnlyDictionary<string, object?> fields, byte[] fingerprint, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(spec.Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", spec.Permission))));
        var errors = ValidateContext(ctx);
        Require(fields, "/fields", errors, spec.Required);
        var patches = Validate(fields, spec.CreateFields, "/fields", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        string command = spec.Resource + ":create";
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var replayed = await ReplayOrNullAsync(tx, tenantId, ctx, spec, fingerprint, ct);
            if (replayed is not null) return replayed;

            var effective = patches.ToDictionary(p => p.Field.Name, p => p.Text);
            var problems = await spec.Check(new MasterCheckInput(tx, tenantId, spec, effective, null, null, null, ct));
            if (problems.Count > 0) return CommandResult<MasterResult>.Rejected(problems);

            Guid id = Guid.NewGuid(), changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            await InsertAsync(tx, spec.Table, [(spec.IdColumn, NpgsqlDbType.Uuid, id)], patches, ctx, tenantId);
            await LogAsync(tx, tenantId, ctx, logId, changeSet, spec.Entity, id.ToString("D"), "CREATE", null, false, null, null, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, CompanyOf(effective), ctx, command, fingerprint, spec.Entity, id, logId, ct);
            var names = patches.Select(p => p.Field.Name).ToList();
            await EventAsync(tx, tenantId, ctx, "master.created", spec.Entity, id.ToString("D"), "CREATED", names, ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(id, 1, changeSet, names), logId);
        });
    }

    public Task<CommandResult<MasterResult>> UpdateAsync(
        Guid tenantId, CommandContext ctx, MasterSpec spec, Guid id, long expectedVersion, IReadOnlyDictionary<string, object?> fields, byte[] fingerprint, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(spec.Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", spec.Permission))));
        var errors = ValidateContext(ctx);
        if (id == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", spec.IdPath, ("reason", "required")));
        if (expectedVersion < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        var patches = Validate(fields, spec.UpdateFields, "/fields", errors);
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
            var effective = current.Where(kv => spec.CreateFields.Any(f => f.Name == kv.Key) || spec.UpdateFields.Any(f => f.Name == kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (Patch p in effectiveChanges) effective[p.Field.Name] = p.Text;
            var problems = await spec.Check(new MasterCheckInput(tx, tenantId, spec, effective, current, id, effectiveChanges, ct));
            if (problems.Count > 0) return CommandResult<MasterResult>.Rejected(problems);
            if (effectiveChanges.Count == 0) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/fields"));

            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            await MasterSupport.UpdateAsync(tx, spec.Table, spec.IdColumn + " = @id", effectiveChanges, ctx, tenantId, ("id", id));
            foreach (Patch p in effectiveChanges)
                await LogAsync(tx, tenantId, ctx, logId, changeSet, spec.Entity, id.ToString("D"), "UPDATE", p.Field.Name, false, current[p.Field.Name], p.Text, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, CompanyOf(current), ctx, command, fingerprint, spec.Entity, id, logId, ct);
            var names = effectiveChanges.Select(p => p.Field.Name).ToList();
            await EventAsync(tx, tenantId, ctx, "master.changed", spec.Entity, id.ToString("D"), "CHANGED", names, ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(id, version + 1, changeSet, names), logId);
        });
    }

    public Task<CommandResult<MasterResult>> StatusAsync(
        Guid tenantId, CommandContext ctx, MasterSpec spec, MasterStatusRequest req, bool deactivate, CancellationToken ct)
    {
        string command = spec.Resource + (deactivate ? ":deactivate" : ":reactivate");
        if (!ctx.Permissions.Contains(spec.Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", spec.Permission))));
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

            var row = await ReadRowAsync(tx, spec.Table, spec.IdColumn + " = @id", spec.Columns, true, tenantId, ("id", req.Id));
            if (row is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", spec.IdPath, (spec.IdColumn, req.Id)));
            long version = long.Parse(row["row_version"]!, CultureInfo.InvariantCulture);
            if (version != req.ExpectedVersion)
                return Reject(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", version), ("expected_version", req.ExpectedVersion)));
            if (deactivate && spec.DeactivateGuard is not null && await spec.DeactivateGuard(tx, tenantId, req.Id, row, ct) is { } blocked) return Reject(blocked);
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
            await CommandSupport.WriteLogAsync(tx, tenantId, CompanyOf(row), ctx, command, fingerprint, spec.Entity, req.Id, logId, ct);
            await EventAsync(tx, tenantId, ctx, deactivate ? "master.deactivated" : "master.reactivated", spec.Entity, req.Id.ToString("D"),
                deactivate ? "DEACTIVATED" : "REACTIVATED", ["active"], ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(req.Id, version + 1, changeSet, ["active"]), logId);
        });
    }

    /// <summary>The company a command belongs to, when the row has one: the command log row then names it.</summary>
    private static Guid? CompanyOf(IReadOnlyDictionary<string, string?> row) =>
        row.TryGetValue("company_id", out string? c) && Guid.TryParse(c, out Guid g) ? g : null;

    private static async Task<CommandResult<MasterResult>?> ReplayOrNullAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, MasterSpec spec, byte[] fingerprint, CancellationToken ct)
    {
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is null) return null;
        if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
            return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
        MasterResult? replay = earlier.EntityId is null ? null : await ReplayAsync(tx, tenantId, earlier.CommandLogId, spec.Table, spec.IdColumn, earlier.EntityId.Value);
        return replay is null ? CommandResult<MasterResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", "")) : CommandResult<MasterResult>.Ok(replay, earlier.CommandLogId, replay: true);
    }

    private static CommandResult<MasterResult> Reject(CommandError error) => CommandResult<MasterResult>.Rejected([error]);
}
