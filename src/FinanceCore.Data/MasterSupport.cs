using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>What the master commands (07, 11.1) share: validation of a set of fields against a registry, the update statement, the rows of
/// master_change_log (10.1), the events (15, 6), and the retry around the commands.</summary>
internal static class MasterSupport
{
    internal sealed record Patch(MasterField Field, object? Value, string? Text);

    internal static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) =>
        JournalDocumentPoster.Error(code, phase, path, p);

    /// <summary>The fields must be known and fit their kind. An unknown field is SCHEMA.UNKNOWN_FIELD.</summary>
    internal static List<Patch> Validate(IReadOnlyDictionary<string, object?>? fields, IReadOnlyList<MasterField> registry, string path, List<CommandError> errors)
    {
        var patches = new List<Patch>();
        if (fields is null) return patches;
        foreach (var kv in fields)
        {
            MasterField? f = registry.FirstOrDefault(x => x.Name == kv.Key);
            if (f is null) { errors.Add(Error("SCHEMA.UNKNOWN_FIELD", "P1", $"{path}/{kv.Key}")); continue; }
            ParsedValue? parsed = MasterValues.Parse(f, kv.Value, out string? reason);
            if (parsed is null) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"{path}/{kv.Key}", ("reason", reason)));
            else patches.Add(new Patch(f, parsed.Value, parsed.Text));
        }
        return patches;
    }

    internal static void Require(IReadOnlyDictionary<string, object?>? fields, string path, List<CommandError> errors, params string[] names)
    {
        foreach (string n in names)
            if (fields is null || !fields.TryGetValue(n, out object? v) || v is null)
                errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", $"{path}/{n}"));
    }

    internal static List<CommandError> ValidateContext(CommandContext ctx)
    {
        var errors = new List<CommandError>();
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        return errors;
    }

    private static NpgsqlDbType DbType(MasterKind k) => k switch
    {
        MasterKind.Bool => NpgsqlDbType.Boolean, MasterKind.Amount or MasterKind.Percent => NpgsqlDbType.Numeric, MasterKind.Id => NpgsqlDbType.Uuid,
        MasterKind.SmallInt => NpgsqlDbType.Smallint, MasterKind.Date => NpgsqlDbType.Date, _ => NpgsqlDbType.Text
    };

    internal static void AddParam(NpgsqlCommand cmd, string name, Patch p) =>
        cmd.Parameters.Add(new NpgsqlParameter(name, DbType(p.Field.Kind)) { Value = p.Value ?? DBNull.Value });

    /// <summary>The current values of the named columns as canonical text, with a lock, or null if the row does not exist. The table and the
    /// columns come from the registries, never from the caller.</summary>
    internal static async Task<Dictionary<string, string?>?> ReadRowAsync(
        TenantTransaction tx, string table, string where, IReadOnlyList<string> columns, bool forUpdate, Guid tenantId, params (string, object)[] keys)
    {
        await using var cmd = tx.Command(
            $"select {string.Join(", ", columns.Select(c => c + "::text"))} from core.{table} where tenant_id = @t and {where}" + (forUpdate ? " for update" : ""));
        cmd.Parameters.AddWithValue("t", tenantId);
        foreach (var (n, v) in keys) cmd.Parameters.AddWithValue(n, v);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        var row = new Dictionary<string, string?>();
        for (int i = 0; i < columns.Count; i++) row[columns[i]] = r.IsDBNull(i) ? null : r.GetString(i);
        return row;
    }

    /// <summary>An UPDATE of the patched columns that also raises row_version and stamps the update.</summary>
    internal static async Task UpdateAsync(
        TenantTransaction tx, string table, string where, IReadOnlyList<Patch> patches, CommandContext ctx, Guid tenantId, params (string, object)[] keys)
    {
        var sets = new List<string>();
        await using var cmd = tx.Command("");
        for (int i = 0; i < patches.Count; i++)
        {
            sets.Add($"{patches[i].Field.Name} = @p{i}");
            AddParam(cmd, $"p{i}", patches[i]);
        }
        cmd.CommandText = $"update core.{table} set {string.Join(", ", sets)}, row_version = row_version + 1, updated_at = now(), updated_by = @by " +
                          $"where tenant_id = @t and {where}";
        cmd.Parameters.AddWithValue("by", ctx.Actor);
        cmd.Parameters.AddWithValue("t", tenantId);
        foreach (var (n, v) in keys) cmd.Parameters.AddWithValue(n, v);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException($"core.{table}: the update changed no row");
    }

    internal static async Task InsertAsync(
        TenantTransaction tx, string table, IReadOnlyList<(string Column, NpgsqlDbType Type, object? Value)> fixedColumns, IReadOnlyList<Patch> patches,
        CommandContext ctx, Guid tenantId)
    {
        var cols = new List<string> { "tenant_id", "created_by", "updated_by" };
        var vals = new List<string> { "@t", "@by", "@by" };
        await using var cmd = tx.Command("");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("by", ctx.Actor);
        foreach (var (column, type, value) in fixedColumns)
        {
            cols.Add(column); vals.Add("@f_" + column);
            cmd.Parameters.Add(new NpgsqlParameter("f_" + column, type) { Value = value ?? DBNull.Value });
        }
        for (int i = 0; i < patches.Count; i++)
        {
            cols.Add(patches[i].Field.Name); vals.Add($"@p{i}");
            AddParam(cmd, $"p{i}", patches[i]);
        }
        cmd.CommandText = $"insert into core.{table} ({string.Join(", ", cols)}) values ({string.Join(", ", vals)})";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>One row of master_change_log (07, 10.1). A personal value is never written: is_pii rows have null values.</summary>
    internal static async Task LogAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, Guid commandLogId, Guid changeSetId, string entityType, string entityId, string action,
        string? field, bool isPii, string? oldValue, string? newValue, string? reasonCode)
    {
        await using var cmd = tx.Command(
            "insert into core.master_change_log (tenant_id, change_set_id, entity_type, entity_id, action, field_name, is_pii, old_value, new_value, reason_code, " +
            "changed_by, changed_at, correlation_id, command_log_id) values (@t, @cs, @et, @eid, @a, @f, @pii, @old, @new, @rc, @by, now(), @corr, @log)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("cs", changeSetId);
        cmd.Parameters.AddWithValue("et", entityType);
        cmd.Parameters.AddWithValue("eid", entityId);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Text) { Value = (object?)field ?? DBNull.Value });
        cmd.Parameters.AddWithValue("pii", isPii);
        cmd.Parameters.Add(new NpgsqlParameter("old", NpgsqlDbType.Text) { Value = isPii ? DBNull.Value : (object?)oldValue ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("new", NpgsqlDbType.Text) { Value = isPii ? DBNull.Value : (object?)newValue ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Text) { Value = (object?)reasonCode ?? DBNull.Value });
        cmd.Parameters.AddWithValue("by", ctx.Actor);
        cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
        cmd.Parameters.AddWithValue("log", commandLogId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The audit event of a master change (15, 6 and 7): the entity and the names of the changed fields, never a value.</summary>
    internal static Task EventAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, string eventType, string entityType, string entityKey, string change, IReadOnlyList<string> fields, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        return CommandSupport.WriteOutboxAsync(tx, tenantId, null, ctx, eventType, entityType, entityKey, change,
            new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc), isAudit: true, null,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["entity_type"] = entityType, ["entity_key"] = entityKey, ["fields"] = fields }), ct);
    }

    /// <summary>The result of an earlier command with the same key, from its log rows.</summary>
    internal static async Task<MasterResult?> ReplayAsync(TenantTransaction tx, Guid tenantId, Guid commandLogId, string table, string idColumn, Guid entityId)
    {
        Guid? set = null;
        var fields = new List<string>();
        await using (var cmd = tx.Command(
            "select change_set_id, entity_type, field_name from core.master_change_log where tenant_id = @t and command_log_id = @l order by changed_at, change_id"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("l", commandLogId);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                set = r.GetGuid(0);
                if (!r.IsDBNull(2)) fields.Add(r.GetString(1) + "." + r.GetString(2));
            }
        }
        if (set is null) return null;
        await using var v = tx.Command($"select row_version from core.{table} where tenant_id = @t and {idColumn} = @id");
        v.Parameters.AddWithValue("t", tenantId);
        v.Parameters.AddWithValue("id", entityId);
        object? version = await v.ExecuteScalarAsync();
        return version is null ? null : new MasterResult(entityId, Convert.ToInt64(version, CultureInfo.InvariantCulture), set.Value, fields);
    }

    /// <summary>The retry of every command: a repeated key that raced the first command is read again, a lock timeout and a deadlock are retryable failures.</summary>
    internal static async Task<CommandResult<T>> RunAsync<T>(Func<Task<CommandResult<T>>> once)
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
                return CommandResult<T>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<T>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    internal static async Task<bool> ExistsAsync(TenantTransaction tx, string sql, Guid tenantId, params (string, object)[] keys)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId);
        foreach (var (n, v) in keys) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync() is not null;
    }
}
