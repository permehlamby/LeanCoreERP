#nullable disable warnings
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Feed;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// Extension attributes (15, 3.2, 3.5, 3.6, 3.6a, 4.2, 4.3): app-namespaces:create (apps.manage), extension-definitions:create, :update and :deactivate (ext.define) and extensions:set and :clear
/// (ext.write). The caller of the last five is an app (app_code, APP.NOT_REGISTERED and APP.INACTIVE) and writes only in the namespaces it owns (EXT.NOT_OWNER). A value is typed by its definition and
/// checked against its limits, its allowed values and for REF the existence of the referenced record. The version of a (record, namespace) is the counter of extension_counter, compared with expected_version
/// and increased by one by every successful command, whatever the number of values (E7). An extension write never changes or locks the owner row. Every changed value is a master_change_log row (without
/// content for is_pii) and an extension.changed event without values; a definition change is an extension.defined audit event. No core rule reads a value (4.2).
/// Not built: extensions:set-batch, the VALIDATE mode, the clearing of is_pii values at anonymisation (12, 4.3), app_id on the change log rows, the entity types DIMENSION_VALUE, WAREHOUSE_LOCATION and ITEM_PARTNER,
/// the values of several namespaces in one command (one namespace per command), narrowing a definition does not recheck its values.
/// </summary>
public sealed class ExtensionCommands(NpgsqlDataSource source)
{
    private static readonly Regex NamespacePattern = new("^[a-z][a-z0-9_]{1,49}$", RegexOptions.Compiled);
    private static readonly Regex AttrPattern = new("^[a-z][a-z0-9_]{0,49}$", RegexOptions.Compiled);

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);
    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);

    private sealed record AppIdentity(Guid AppId, string AppCode, bool Active, IReadOnlyList<Guid>? Scope);

    private sealed record Definition(Guid Id, string EntityType, string Namespace, string AttrKey, string DataType, int? MaxLength, int? Precision, int? Scale, IReadOnlyList<string>? Allowed, string? RefType, bool IsPii, bool Active, long Version);

    private static async Task<(AppIdentity? App, CommandError? Error)> IdentifyAsync(TenantTransaction tx, Guid tenantId, CommandContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.AppCode)) return (null, Error("APP.NOT_REGISTERED", "P0", "app_code"));
        await using var cmd = tx.Command("select app_id, active, company_scope::text from core.app_registration where tenant_id = @t and app_code = @c");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", ctx.AppCode);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, Error("APP.NOT_REGISTERED", "P0", "app_code", ("app_code", ctx.AppCode)));
        if (!r.GetBoolean(1)) return (null, Error("APP.INACTIVE", "P0", "app_code", ("app_code", ctx.AppCode)));
        return (new AppIdentity(r.GetGuid(0), ctx.AppCode, true, r.IsDBNull(2) ? null : JsonSerializer.Deserialize<List<Guid>>(r.GetString(2))), null);
    }

    private static async Task<bool> OwnsAsync(TenantTransaction tx, Guid tenantId, Guid appId, string ns, CancellationToken ct)
    {
        await using var cmd = tx.Command("select 1 from core.app_namespace where tenant_id = @t and namespace = @n and app_id = @a");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("n", ns); cmd.Parameters.AddWithValue("a", appId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static Task EventAsync(TenantTransaction tx, Guid tenantId, Guid? company, CommandContext ctx, string type, string entityType, string key, string change, bool audit, Dictionary<string, object?> summary, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        return CommandSupport.WriteOutboxAsync(tx, tenantId, company, ctx, type, entityType, key, change, new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc), audit, null, JsonSerializer.Serialize(summary), ct);
    }

    /// <summary>The shared frame of the commands: permission, context, retry, the idempotency record, the app, the work, the log row and the commit.</summary>
    private async Task<CommandResult<T>> RunAsync<T>(
        Guid tenantId, CommandContext ctx, string command, string permission, bool needsApp, byte[] fingerprint,
        Func<TenantTransaction, AppIdentity?, Guid, Guid, CancellationToken, Task<(T? Result, string EntityType, Guid EntityId, Guid? Company, CommandError? Error)>> work,
        Func<TenantTransaction, Guid, CancellationToken, Task<T>> replay, CancellationToken ct) where T : class
    {
        if (!ctx.Permissions.Contains(permission)) return Reject<T>(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", permission)));
        var shape = MasterSupport.ValidateContext(ctx);
        if (shape.Count > 0) return CommandResult<T>.Rejected(shape);
        return await MasterSupport.RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            AppIdentity? app = null;
            if (needsApp)
            {
                var id = await IdentifyAsync(tx, tenantId, ctx, ct);
                if (id.Error is not null) return Reject<T>(id.Error);
                app = id.App;
            }
            var earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject<T>(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                return earlier.EntityId is null
                    ? CommandResult<T>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                    : CommandResult<T>.Ok(await replay(tx, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
            }
            Guid logId = Guid.NewGuid(), changeSet = Guid.NewGuid();
            var done = await work(tx, app, logId, changeSet, ct);
            if (done.Error is not null) return Reject<T>(done.Error);
            await CommandSupport.WriteLogAsync(tx, tenantId, done.Company, ctx, command, fingerprint, done.EntityType, done.EntityId, logId, ct);
            await tx.CommitAsync(ct);
            return CommandResult<T>.Ok(done.Result!, logId);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // app-namespaces:create
    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<AppNamespaceResult>> CreateNamespaceAsync(Guid tenantId, CommandContext ctx, CreateAppNamespaceRequest req, CancellationToken ct = default)
    {
        if (ctx.Permissions.Contains(EventFeed.ManagePermission) && (!NamespacePattern.IsMatch(req.Namespace ?? "") || req.Namespace == "core"))
            return Task.FromResult(Reject<AppNamespaceResult>(Error("SCHEMA.INVALID_VALUE", "P1", "namespace", ("reason", "lower case code, 2 to 50 characters, not core"))));
        return RunAsync<AppNamespaceResult>(tenantId, ctx, "app-namespaces:create", EventFeed.ManagePermission, false, ExtensionRules.FingerprintBytes("app-namespaces:create", req), async (tx, _, logId, set, token) =>
        {
            await using (var cmd = tx.Command("select 1 from core.app_registration where tenant_id = @t and app_id = @a"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", req.AppId);
                if (await cmd.ExecuteScalarAsync(token) is null) return (null, "", Guid.Empty, null, Error("CONFIG.INVALID_REFERENCE", "P2", "app_id", ("app_id", req.AppId)));
            }
            await using (var cmd = tx.Command("select 1 from core.app_namespace where tenant_id = @t and namespace = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("n", req.Namespace!);
                if (await cmd.ExecuteScalarAsync(token) is not null) return (null, "", Guid.Empty, null, Error("APP.NAMESPACE_TAKEN", "P2", "namespace", ("namespace", req.Namespace)));
            }
            await using (var cmd = tx.Command("insert into core.app_namespace (tenant_id, namespace, app_id, created_by) values (@t, @n, @a, @by)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("n", req.Namespace!); cmd.Parameters.AddWithValue("a", req.AppId); cmd.Parameters.AddWithValue("by", ctx.Actor);
                await cmd.ExecuteNonQueryAsync(token);
            }
            await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "APP_NAMESPACE", req.Namespace, "CREATE", null, false, null, null, null);
            await EventAsync(tx, tenantId, null, ctx, "app.changed", "APP", req.AppId.ToString("D"), "CHANGED", true, new() { ["app_id"] = req.AppId, ["namespace"] = req.Namespace }, token);
            return (new AppNamespaceResult(req.AppId, req.Namespace), "APP", req.AppId, null, null);
        }, (_, _, _) => Task.FromResult(new AppNamespaceResult(req.AppId, req.Namespace!)), ct);
    }

    // ---------------------------------------------------------------------------------------------------------
    // definitions
    // ---------------------------------------------------------------------------------------------------------
    private const string DefinitionColumns = "definition_id, entity_type, namespace, attr_key, data_type, max_length, decimal_precision, decimal_scale, allowed_values::text, ref_entity_type, is_pii, active, row_version";

    private static Definition ReadDefinition(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : JsonSerializer.Deserialize<List<string>>(r.GetString(8)), r.IsDBNull(9) ? null : r.GetString(9),
        r.GetBoolean(10), r.GetBoolean(11), r.GetInt64(12));

    private static async Task<Definition?> LoadDefinitionAsync(TenantTransaction tx, Guid tenantId, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = tx.Command($"select {DefinitionColumns} from core.extension_definition where tenant_id = @t and definition_id = @d{(forUpdate ? " for update" : "")}");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDefinition(r) : null;
    }

    private static ExtensionDefinitionResult ToResult(Definition d) => new(d.Id, d.EntityType, d.Namespace, d.AttrKey, d.DataType, d.IsPii, d.Active, d.Version);

    private static string? ShapeProblem(string dataType, int? maxLength, int? precision, int? scale, IReadOnlyList<string>? allowed, string? refType)
    {
        if (!ExtensionRules.DataTypes.Contains(dataType)) return "data_type: " + string.Join(", ", ExtensionRules.DataTypes);
        if ((maxLength is not null || allowed is not null) && dataType != "TEXT") return "max_length and allowed_values are for TEXT";
        if ((precision is not null || scale is not null) && dataType != "DECIMAL") return "decimal_precision and decimal_scale are for DECIMAL";
        if (maxLength is < 1 or > 4000) return "max_length is 1 to 4000";
        if (precision is < 1 or > 28 || scale is < 0 or > 8 || (scale is not null && precision is not null && scale > precision)) return "precision 1 to 28, scale 0 to 8, not above the precision";
        if (allowed is not null && (allowed.Count == 0 || allowed.Count > 200 || allowed.Any(string.IsNullOrEmpty) || allowed.Distinct().Count() != allowed.Count)) return "allowed_values: 1 to 200 different non-empty values";
        if (dataType == "REF" && (refType is null || ExtensionRules.Entity(refType) is null)) return "ref_entity_type: one of the extendable types";
        if (dataType != "REF" && refType is not null) return "ref_entity_type is for REF";
        return null;
    }

    public Task<CommandResult<ExtensionDefinitionResult>> CreateDefinitionAsync(Guid tenantId, CommandContext ctx, CreateExtensionDefinitionRequest req, CancellationToken ct = default)
    {
        if (ctx.Permissions.Contains(ExtensionRules.DefinePermission))
        {
            if (ExtensionRules.Entity(req.EntityType ?? "") is null) return Task.FromResult(Reject<ExtensionDefinitionResult>(Error("EXT.ENTITY_NOT_EXTENDABLE", "P1", "entity_type", ("allowed", ExtensionRules.Entities.Select(e => e.Type).ToArray()))));
            if (!AttrPattern.IsMatch(req.AttrKey ?? "")) return Task.FromResult(Reject<ExtensionDefinitionResult>(Error("SCHEMA.INVALID_VALUE", "P1", "attr_key", ("reason", "lower case code, 1 to 50 characters"))));
            if (!NamespacePattern.IsMatch(req.Namespace ?? "")) return Task.FromResult(Reject<ExtensionDefinitionResult>(Error("SCHEMA.INVALID_VALUE", "P1", "namespace", ("reason", "lower case code"))));
            if (ShapeProblem(req.DataType ?? "", req.MaxLength, req.DecimalPrecision, req.DecimalScale, req.AllowedValues, req.RefEntityType) is { } problem)
                return Task.FromResult(Reject<ExtensionDefinitionResult>(Error("SCHEMA.INVALID_VALUE", "P1", "data_type", ("reason", problem))));
        }
        return RunAsync<ExtensionDefinitionResult>(tenantId, ctx, "extension-definitions:create", ExtensionRules.DefinePermission, true, ExtensionRules.FingerprintBytes("extension-definitions:create", req), async (tx, app, logId, set, token) =>
        {
            if (!await OwnsAsync(tx, tenantId, app!.AppId, req.Namespace!, token)) return (null, "", Guid.Empty, null, Error("EXT.NOT_OWNER", "P2", "namespace", ("namespace", req.Namespace)));
            await using (var cmd = tx.Command("select 1 from core.extension_definition where tenant_id = @t and entity_type = @e and namespace = @n and attr_key = @k"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", req.EntityType!); cmd.Parameters.AddWithValue("n", req.Namespace!); cmd.Parameters.AddWithValue("k", req.AttrKey!);
                if (await cmd.ExecuteScalarAsync(token) is not null) return (null, "", Guid.Empty, null, Error("CONFIG.DUPLICATE_CODE", "P2", "attr_key", ("code", req.AttrKey)));
            }
            await using (var cmd = tx.Command(
                "select (select count(*) from core.extension_definition where tenant_id = @t and entity_type = @e and namespace = @n and active), " +
                "(select extension_max_definitions from core.tenant_setting where tenant_id = @t)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", req.EntityType!); cmd.Parameters.AddWithValue("n", req.Namespace!);
                await using var r = await cmd.ExecuteReaderAsync(token);
                await r.ReadAsync(token);
                if (r.GetInt64(0) >= r.GetInt32(1)) return (null, "", Guid.Empty, null, Error("EXT.DEFINITION_LIMIT", "P2", "attr_key", ("max", r.GetInt32(1))));
            }
            Guid id = Guid.NewGuid();
            await using (var cmd = tx.Command(
                "insert into core.extension_definition (tenant_id, definition_id, entity_type, namespace, attr_key, data_type, max_length, decimal_precision, decimal_scale, allowed_values, ref_entity_type, is_pii, created_by, updated_by) " +
                "values (@t, @id, @e, @n, @k, @dt, @ml, @dp, @ds, @av, @rt, @pii, @by, @by)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("e", req.EntityType); cmd.Parameters.AddWithValue("n", req.Namespace);
                cmd.Parameters.AddWithValue("k", req.AttrKey!); cmd.Parameters.AddWithValue("dt", req.DataType!); cmd.Parameters.AddWithValue("pii", req.IsPii); cmd.Parameters.AddWithValue("by", ctx.Actor);
                cmd.Parameters.Add(new NpgsqlParameter("ml", NpgsqlDbType.Integer) { Value = (object?)req.MaxLength ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("dp", NpgsqlDbType.Integer) { Value = (object?)req.DecimalPrecision ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("ds", NpgsqlDbType.Integer) { Value = (object?)req.DecimalScale ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("av", NpgsqlDbType.Jsonb) { Value = req.AllowedValues is null ? DBNull.Value : JsonSerializer.Serialize(req.AllowedValues) });
                cmd.Parameters.Add(new NpgsqlParameter("rt", NpgsqlDbType.Text) { Value = (object?)req.RefEntityType ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(token);
            }
            await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "EXTENSION_DEFINITION", id.ToString("D"), "CREATE", null, false, null, null, null);
            await EventAsync(tx, tenantId, null, ctx, "extension.defined", "EXTENSION_DEFINITION", id.ToString("D"), "CREATED", true,
                new() { ["entity_type"] = req.EntityType, ["namespace"] = req.Namespace, ["attr_key"] = req.AttrKey }, token);
            return (ToResult((await LoadDefinitionAsync(tx, tenantId, id, false, token))!), "EXTENSION_DEFINITION", id, null, null);
        }, async (tx, id, token) => ToResult((await LoadDefinitionAsync(tx, tenantId, id, false, token))!), ct);
    }

    public Task<CommandResult<ExtensionDefinitionResult>> UpdateDefinitionAsync(Guid tenantId, CommandContext ctx, UpdateExtensionDefinitionRequest req, CancellationToken ct = default) =>
        RunAsync<ExtensionDefinitionResult>(tenantId, ctx, "extension-definitions:update", ExtensionRules.DefinePermission, true, ExtensionRules.FingerprintBytes("extension-definitions:update", req), async (tx, app, logId, set, token) =>
        {
            Definition? d = await LoadDefinitionAsync(tx, tenantId, req.DefinitionId, true, token);
            if (d is null) return (null, "", Guid.Empty, null, Error("CONFIG.INVALID_REFERENCE", "P2", "definition_id", ("definition_id", req.DefinitionId)));
            if (!await OwnsAsync(tx, tenantId, app!.AppId, d.Namespace, token)) return (null, "", Guid.Empty, null, Error("EXT.NOT_OWNER", "P2", "definition_id", ("namespace", d.Namespace)));
            if (d.Version != req.ExpectedVersion) return (null, "", Guid.Empty, null, Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "expected_version", ("current_version", d.Version)));
            int? ml = req.MaxLength ?? d.MaxLength, dp = req.DecimalPrecision ?? d.Precision, ds = req.DecimalScale ?? d.Scale;
            IReadOnlyList<string>? av = req.ClearAllowedValues ? null : req.AllowedValues ?? d.Allowed;
            bool pii = req.IsPii ?? d.IsPii;
            if (req.ClearAllowedValues && req.AllowedValues is not null) return (null, "", Guid.Empty, null, Error("SCHEMA.INVALID_VALUE", "P1", "allowed_values", ("reason", "a list or clear, not both")));
            if (ShapeProblem(d.DataType, ml, dp, ds, av, d.RefType) is { } problem) return (null, "", Guid.Empty, null, Error("SCHEMA.INVALID_VALUE", "P1", "max_length", ("reason", problem)));
            var changes = new List<(string Field, string? Old, string? New)>();
            if (ml != d.MaxLength) changes.Add(("max_length", d.MaxLength?.ToString(CultureInfo.InvariantCulture), ml?.ToString(CultureInfo.InvariantCulture)));
            if (dp != d.Precision) changes.Add(("decimal_precision", d.Precision?.ToString(CultureInfo.InvariantCulture), dp?.ToString(CultureInfo.InvariantCulture)));
            if (ds != d.Scale) changes.Add(("decimal_scale", d.Scale?.ToString(CultureInfo.InvariantCulture), ds?.ToString(CultureInfo.InvariantCulture)));
            string? oldAv = d.Allowed is null ? null : JsonSerializer.Serialize(d.Allowed), newAv = av is null ? null : JsonSerializer.Serialize(av);
            if (oldAv != newAv) changes.Add(("allowed_values", oldAv, newAv));
            if (pii != d.IsPii)
            {
                await using var cmd = tx.Command("select 1 from core.extension_value where tenant_id = @t and definition_id = @d limit 1");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", d.Id);
                if (await cmd.ExecuteScalarAsync(token) is not null) return (null, "", Guid.Empty, null, Error("EXT.DEFINITION_LOCKED", "P2", "is_pii"));
                changes.Add(("is_pii", d.IsPii.ToString(), pii.ToString()));
            }
            if (changes.Count == 0) return (null, "", Guid.Empty, null, Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P2", ""));
            await using (var cmd = tx.Command(
                "update core.extension_definition set max_length = @ml, decimal_precision = @dp, decimal_scale = @ds, allowed_values = @av, is_pii = @pii, row_version = row_version + 1, updated_at = now(), updated_by = @by " +
                "where tenant_id = @t and definition_id = @d"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", d.Id); cmd.Parameters.AddWithValue("pii", pii); cmd.Parameters.AddWithValue("by", ctx.Actor);
                cmd.Parameters.Add(new NpgsqlParameter("ml", NpgsqlDbType.Integer) { Value = (object?)ml ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("dp", NpgsqlDbType.Integer) { Value = (object?)dp ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("ds", NpgsqlDbType.Integer) { Value = (object?)ds ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("av", NpgsqlDbType.Jsonb) { Value = newAv is null ? DBNull.Value : newAv });
                await cmd.ExecuteNonQueryAsync(token);
            }
            foreach (var c in changes) await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "EXTENSION_DEFINITION", d.Id.ToString("D"), "UPDATE", c.Field, false, c.Old, c.New, null);
            await EventAsync(tx, tenantId, null, ctx, "extension.defined", "EXTENSION_DEFINITION", d.Id.ToString("D"), "CHANGED", true, new() { ["entity_type"] = d.EntityType, ["namespace"] = d.Namespace, ["attr_key"] = d.AttrKey }, token);
            return (ToResult((await LoadDefinitionAsync(tx, tenantId, d.Id, false, token))!), "EXTENSION_DEFINITION", d.Id, null, null);
        }, async (tx, id, token) => ToResult((await LoadDefinitionAsync(tx, tenantId, id, false, token))!), ct);

    public Task<CommandResult<ExtensionDefinitionResult>> DeactivateDefinitionAsync(Guid tenantId, CommandContext ctx, DeactivateExtensionDefinitionRequest req, CancellationToken ct = default) =>
        RunAsync<ExtensionDefinitionResult>(tenantId, ctx, "extension-definitions:deactivate", ExtensionRules.DefinePermission, true, ExtensionRules.FingerprintBytes("extension-definitions:deactivate", req), async (tx, app, logId, set, token) =>
        {
            Definition? d = await LoadDefinitionAsync(tx, tenantId, req.DefinitionId, true, token);
            if (d is null) return (null, "", Guid.Empty, null, Error("CONFIG.INVALID_REFERENCE", "P2", "definition_id", ("definition_id", req.DefinitionId)));
            if (!await OwnsAsync(tx, tenantId, app!.AppId, d.Namespace, token)) return (null, "", Guid.Empty, null, Error("EXT.NOT_OWNER", "P2", "definition_id", ("namespace", d.Namespace)));
            if (d.Version != req.ExpectedVersion) return (null, "", Guid.Empty, null, Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "expected_version", ("current_version", d.Version)));
            if (!d.Active) return (null, "", Guid.Empty, null, Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P2", ""));
            await using (var cmd = tx.Command("update core.extension_definition set active = false, row_version = row_version + 1, updated_at = now(), updated_by = @by where tenant_id = @t and definition_id = @d"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", d.Id); cmd.Parameters.AddWithValue("by", ctx.Actor);
                await cmd.ExecuteNonQueryAsync(token);
            }
            await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "EXTENSION_DEFINITION", d.Id.ToString("D"), "DEACTIVATE", null, false, null, null, null);
            await EventAsync(tx, tenantId, null, ctx, "extension.defined", "EXTENSION_DEFINITION", d.Id.ToString("D"), "DEACTIVATED", true, new() { ["entity_type"] = d.EntityType, ["namespace"] = d.Namespace, ["attr_key"] = d.AttrKey }, token);
            return (ToResult((await LoadDefinitionAsync(tx, tenantId, d.Id, false, token))!), "EXTENSION_DEFINITION", d.Id, null, null);
        }, async (tx, id, token) => ToResult((await LoadDefinitionAsync(tx, tenantId, id, false, token))!), ct);

    // ---------------------------------------------------------------------------------------------------------
    // the records that carry values
    // ---------------------------------------------------------------------------------------------------------
    internal sealed record EntityRow(string CanonicalKey, Guid? Company, string? Status);

    /// <summary>The record of an extendable type by its key (the key columns in key order, separated by a slash), or null when the key does not parse or the record does not exist in the tenant.</summary>
    internal static async Task<EntityRow?> LookupAsync(TenantTransaction tx, Guid tenantId, ExtendableEntity e, string key, CancellationToken ct)
    {
        string[] parts = (key ?? "").Split('/');
        if (parts.Length != e.KeyColumns.Length) return null;
        var values = new List<object>();
        for (int i = 0; i < parts.Length; i++)
        {
            if (e.KeyTypes[i] == 'G' ? Guid.TryParse(parts[i], out Guid g) : false) { values.Add(g); continue; }
            if (e.KeyTypes[i] == 'I' && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n)) { values.Add(n); continue; }
            return null;
        }
        string where = string.Join(" and ", e.KeyColumns.Select((c, i) => $"{c} = @k{i}"));
        await using var cmd = tx.Command($"select {e.CompanyColumn ?? "null::uuid"}, {(e.StatusColumn is null ? "null::text" : e.StatusColumn + "::text")} from core.{e.Table} where tenant_id = @t and {where}");
        cmd.Parameters.AddWithValue("t", tenantId);
        for (int i = 0; i < values.Count; i++) cmd.Parameters.AddWithValue($"k{i}", values[i]);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string canonical = string.Join('/', values.Select(v => v is Guid g ? g.ToString("D") : Convert.ToString(v, CultureInfo.InvariantCulture)));
        return new EntityRow(canonical, r.IsDBNull(0) ? null : r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private static Guid KeyId(string entityType, string key) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entityType + "|" + key))[..16]);

    private static string? ParseValue(Definition d, JsonElement v, out object? stored)
    {
        stored = null;
        switch (d.DataType)
        {
            case "TEXT":
                if (v.ValueKind != JsonValueKind.String) return "TYPE";
                string s = v.GetString()!;
                if (s.Length == 0 || s.Length > (d.MaxLength ?? 4000)) return "TYPE";
                if (d.Allowed is not null && !d.Allowed.Contains(s)) return "ALLOWED";
                stored = s; return null;
            case "DECIMAL":
                decimal m;
                if (v.ValueKind == JsonValueKind.Number) { if (!v.TryGetDecimal(out m)) return "TYPE"; }
                else if (v.ValueKind != JsonValueKind.String || !decimal.TryParse(v.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out m)) return "TYPE";
                decimal trimmed = m / 1.000000000000000000000000000000000m;
                int realScale = (decimal.GetBits(trimmed)[3] >> 16) & 0xFF;
                int precision = d.Precision ?? 28, sc = d.Scale ?? 8;
                if (realScale > sc || Math.Truncate(Math.Abs(m)).ToString(CultureInfo.InvariantCulture).TrimStart('0').Length > precision - sc) return "TYPE";
                stored = m; return null;
            case "DATE":
                if (v.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)) return "TYPE";
                stored = date; return null;
            case "BOOLEAN":
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "TYPE";
                stored = v.GetBoolean(); return null;
            default:
                if (v.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(v.GetString())) return "TYPE";
                stored = v.GetString(); return null;
        }
    }

    private static string Display(object? stored) => stored switch
    {
        null => "", DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), bool b => b ? "true" : "false", decimal m => m.ToString(CultureInfo.InvariantCulture), _ => Convert.ToString(stored, CultureInfo.InvariantCulture)!
    };

    private static string ColumnOf(string dataType) => dataType switch { "TEXT" => "value_text", "DECIMAL" => "value_decimal", "DATE" => "value_date", "BOOLEAN" => "value_bool", _ => "value_ref" };

    private static NpgsqlDbType DbTypeOf(string dataType) => dataType switch { "TEXT" or "REF" => NpgsqlDbType.Text, "DECIMAL" => NpgsqlDbType.Numeric, "DATE" => NpgsqlDbType.Date, _ => NpgsqlDbType.Boolean };

    // ---------------------------------------------------------------------------------------------------------
    // extensions:set and extensions:clear
    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<ExtensionsResult>> SetAsync(Guid tenantId, CommandContext ctx, SetExtensionsRequest req, CancellationToken ct = default)
    {
        if (ctx.Permissions.Contains(ExtensionRules.WritePermission) && Shape(req.EntityType, req.EntityKey, req.Values?.Keys.ToList(), req.ExpectedVersion, "values") is { } bad) return Task.FromResult(Reject<ExtensionsResult>(bad));
        return WriteAsync(tenantId, ctx, "extensions:set", req.EntityType, req.EntityKey, req.Values!.Keys.ToList(), req.ExpectedVersion, ExtensionRules.FingerprintBytes("extensions:set", req), req.Values, ct);
    }

    public Task<CommandResult<ExtensionsResult>> ClearAsync(Guid tenantId, CommandContext ctx, ClearExtensionsRequest req, CancellationToken ct = default)
    {
        if (ctx.Permissions.Contains(ExtensionRules.WritePermission) && Shape(req.EntityType, req.EntityKey, req.Keys?.ToList(), req.ExpectedVersion, "keys") is { } bad) return Task.FromResult(Reject<ExtensionsResult>(bad));
        return WriteAsync(tenantId, ctx, "extensions:clear", req.EntityType, req.EntityKey, req.Keys!.ToList(), req.ExpectedVersion, ExtensionRules.FingerprintBytes("extensions:clear", req), null, ct);
    }

    private static CommandError? Shape(string entityType, string entityKey, List<string>? keys, long? expected, string path)
    {
        if (ExtensionRules.Entity(entityType ?? "") is null) return Error("EXT.ENTITY_NOT_EXTENDABLE", "P1", "entity_type", ("allowed", ExtensionRules.Entities.Select(e => e.Type).ToArray()));
        if (string.IsNullOrWhiteSpace(entityKey) || entityKey.Length > 200) return Error("SCHEMA.INVALID_VALUE", "P1", "entity_key", ("reason", "required, at most 200 characters"));
        if (keys is null || keys.Count is 0 or > 100) return Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", "1 to 100 entries"));
        var parsed = keys.Select(ExtensionRules.SplitKey).ToList();
        if (parsed.Any(p => p is null)) return Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", "namespace.attr"));
        if (parsed.Select(p => p!.Value.Namespace).Distinct().Count() != 1) return Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", "one namespace per command"));
        if (keys.Distinct().Count() != keys.Count) return Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", "each attribute once"));
        if (expected is < 0) return Error("SCHEMA.INVALID_VALUE", "P1", "expected_version", ("reason", "0 or more"));
        return null;
    }

    private Task<CommandResult<ExtensionsResult>> WriteAsync(
        Guid tenantId, CommandContext ctx, string command, string entityType, string entityKey, List<string> keys, long? expected, byte[] fingerprint, IReadOnlyDictionary<string, JsonElement>? values, CancellationToken ct)
    {
        ExtendableEntity entity = ExtensionRules.Entity(entityType) ?? ExtensionRules.Entities[0];
        string ns = ExtensionRules.SplitKey(keys.FirstOrDefault() ?? "")?.Namespace ?? "";
        bool clear = values is null;
        return RunAsync<ExtensionsResult>(tenantId, ctx, command, ExtensionRules.WritePermission, true, fingerprint, async (tx, app, logId, set, token) =>
        {
            if (!await OwnsAsync(tx, tenantId, app!.AppId, ns, token)) return (null, "", Guid.Empty, null, Error("EXT.NOT_OWNER", "P2", "values", ("namespace", ns)));
            var defs = new Dictionary<string, Definition>();
            await using (var cmd = tx.Command($"select {DefinitionColumns} from core.extension_definition where tenant_id = @t and entity_type = @e and namespace = @n and attr_key = any(@k)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", entityType); cmd.Parameters.AddWithValue("n", ns);
                cmd.Parameters.AddWithValue("k", keys.Select(k => ExtensionRules.SplitKey(k)!.Value.Attr).ToArray());
                await using var r = await cmd.ExecuteReaderAsync(token);
                while (await r.ReadAsync(token)) { var d = ReadDefinition(r); defs[d.Namespace + "." + d.AttrKey] = d; }
            }
            foreach (string k in keys)
                if (!defs.TryGetValue(k, out var d) || (!clear && !d.Active)) return (null, "", Guid.Empty, null, Error("EXT.UNKNOWN_DEFINITION", "P2", "values", ("key", k)));
            EntityRow? row = await LookupAsync(tx, tenantId, entity, entityKey, token);
            if (row is null) return (null, "", Guid.Empty, null, Error("CONFIG.INVALID_REFERENCE", "P2", "entity_key", ("entity_type", entityType), ("entity_key", entityKey)));
            if (entity.AnonymisedValue is not null && row.Status == entity.AnonymisedValue) return (null, "", Guid.Empty, null, Error("EXT.ENTITY_ANONYMISED", "P2", "entity_key"));
            if (entity.ClosedValues is not null && row.Status is not null && entity.ClosedValues.Contains(row.Status)) return (null, "", Guid.Empty, null, Error("EXT.ENTITY_CLOSED", "P2", "entity_key"));
            if (app.Scope is not null && (row.Company is null || !app.Scope.Contains(row.Company.Value))) return (null, "", Guid.Empty, null, Error("EXT.COMPANY_OUT_OF_SCOPE", "P2", "entity_key"));

            // the counter of the record and namespace: created by the first write, locked here, compared with expected_version
            await using (var cmd = tx.Command("insert into core.extension_counter (tenant_id, entity_type, entity_key, namespace) values (@t, @e, @k, @n) on conflict do nothing"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", entityType); cmd.Parameters.AddWithValue("k", row.CanonicalKey); cmd.Parameters.AddWithValue("n", ns);
                await cmd.ExecuteNonQueryAsync(token);
            }
            long counter;
            await using (var cmd = tx.Command("select counter from core.extension_counter where tenant_id = @t and entity_type = @e and entity_key = @k and namespace = @n for update"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", entityType); cmd.Parameters.AddWithValue("k", row.CanonicalKey); cmd.Parameters.AddWithValue("n", ns);
                counter = (long)(await cmd.ExecuteScalarAsync(token))!;
            }
            if (expected is { } want && want != counter) return (null, "", Guid.Empty, null, Error("EXT.VERSION_CONFLICT", "P2", "expected_version", ("current_version", counter)));

            var changed = new List<string>();
            long next = counter + 1;
            foreach (string k in keys)
            {
                Definition d = defs[k];
                string col = ColumnOf(d.DataType);
                object? stored = null;
                if (!clear)
                {
                    string? problem = ParseValue(d, values![k], out stored);
                    if (problem == "ALLOWED") return (null, "", Guid.Empty, null, Error("EXT.VALUE_NOT_ALLOWED", "P2", "values", ("key", k)));
                    if (problem is not null) return (null, "", Guid.Empty, null, Error("EXT.TYPE_MISMATCH", "P2", "values", ("key", k), ("data_type", d.DataType)));
                    if (d.DataType == "REF")
                    {
                        ExtendableEntity? target = ExtensionRules.Entity(d.RefType!);
                        EntityRow? found = target is null ? null : await LookupAsync(tx, tenantId, target, (string)stored!, token);
                        if (found is null) return (null, "", Guid.Empty, null, Error("EXT.REF_INVALID", "P2", "values", ("key", k), ("ref_entity_type", d.RefType)));
                        stored = found.CanonicalKey;
                    }
                }
                string? old = null;
                await using (var cmd = tx.Command($"select {col}::text from core.extension_value where tenant_id = @t and definition_id = @d and entity_key = @k"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", d.Id); cmd.Parameters.AddWithValue("k", row.CanonicalKey);
                    old = await cmd.ExecuteScalarAsync(token) as string;
                }
                string? now = clear ? null : Display(stored);
                if (clear)
                {
                    if (old is null) continue;
                    await using (var cmd = tx.Command("delete from core.extension_value where tenant_id = @t and definition_id = @d and entity_key = @k"))
                    {
                        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", d.Id); cmd.Parameters.AddWithValue("k", row.CanonicalKey);
                        await cmd.ExecuteNonQueryAsync(token);
                    }
                    await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "EXTENSION_VALUE", $"{entityType}|{row.CanonicalKey}", "CLEAR", k, d.IsPii, old, null, null);
                }
                else
                {
                    if (old == now) continue;
                    await using (var cmd = tx.Command(
                        $"insert into core.extension_value (tenant_id, definition_id, entity_key, version_stamp, company_id, {col}) values (@t, @d, @k, @v, @c, @x) " +
                        $"on conflict (tenant_id, definition_id, entity_key) do update set version_stamp = excluded.version_stamp, {col} = excluded.{col}"))
                    {
                        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", d.Id); cmd.Parameters.AddWithValue("k", row.CanonicalKey); cmd.Parameters.AddWithValue("v", next);
                        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Uuid) { Value = (object?)row.Company ?? DBNull.Value });
                        cmd.Parameters.Add(new NpgsqlParameter("x", DbTypeOf(d.DataType)) { Value = stored! });
                        await cmd.ExecuteNonQueryAsync(token);
                    }
                    await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "EXTENSION_VALUE", $"{entityType}|{row.CanonicalKey}", "UPDATE", k, d.IsPii, old, now, null);
                }
                changed.Add(k);
                await EventAsync(tx, tenantId, row.Company, ctx, "extension.changed", entityType, row.CanonicalKey, "CHANGED", false,
                    new() { ["entity_type"] = entityType, ["entity_key"] = row.CanonicalKey, ["namespace"] = ns, ["attr_key"] = d.AttrKey }, token);
            }
            await using (var cmd = tx.Command("update core.extension_counter set counter = @n, updated_at = now() where tenant_id = @t and entity_type = @e and entity_key = @k and namespace = @ns"))
            {
                cmd.Parameters.AddWithValue("n", next); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", entityType); cmd.Parameters.AddWithValue("k", row.CanonicalKey); cmd.Parameters.AddWithValue("ns", ns);
                await cmd.ExecuteNonQueryAsync(token);
            }
            return (new ExtensionsResult(entityType, row.CanonicalKey, ns, next, changed), "EXTENSION", KeyId(entityType, row.CanonicalKey), row.Company, null);
        }, async (tx, _, token) =>
        {
            EntityRow? row = await LookupAsync(tx, tenantId, entity, entityKey, token);
            string canonical = row?.CanonicalKey ?? entityKey;
            await using var cmd = tx.Command("select coalesce(max(counter), 0) from core.extension_counter where tenant_id = @t and entity_type = @e and entity_key = @k and namespace = @n");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", entityType); cmd.Parameters.AddWithValue("k", canonical); cmd.Parameters.AddWithValue("n", ns);
            return new ExtensionsResult(entityType, canonical, ns, (long)(await cmd.ExecuteScalarAsync(token))!, keys);
        }, ct);
    }
}
