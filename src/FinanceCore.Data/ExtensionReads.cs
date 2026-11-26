using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Feed;
using FinanceCore.Core.Reads;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The reads of extension attributes (09, 2.4.2; 15, 4.4): extension-definitions:list (read.config) and the helpers that the gets and lists of the extendable entities share. include=extensions embeds the values
/// of all namespaces as one flat map "namespace.attr" to value (decimals as strings, dates as ISO text); values of is_pii definitions only with read.pii, and every such read writes a read_access_log row
/// with the field group PII_EXTENSION (5.2, so the read must commit its transaction). A filter ext.namespace.attr with =, !=, less, greater, in, is_null and, for TEXT, contains is ANDed with the other filters;
/// a filter on an is_pii definition needs read.pii and is logged, and a contains has the minimum length of 2.4.1 and, on the ledger-scale OPEN_ITEM, a scope. Extension values are never a sort key.
/// </summary>
public sealed class ExtensionReads(NpgsqlDataSource source)
{
    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);

    public async Task<CommandResult<ExtensionDefinitionList>> ListDefinitionsAsync(Guid tenantId, CommandContext ctx, ListExtensionDefinitionsRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ExtensionRules.ReadDefinitionsPermission)) return CommandResult<ExtensionDefinitionList>.Rejected([Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", ExtensionRules.ReadDefinitionsPermission))]);
        int limit = req.Limit ?? ReadRules.DefaultLimit;
        if (limit is < 1 or > ReadRules.MaxLimit) return CommandResult<ExtensionDefinitionList>.Rejected([Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit))]);
        string kind = ReadRules.QueryKind("extdef", req.EntityType, req.Namespace, req.AttrKey, req.Active);
        if (!ReadRules.TryDecodeCursor(req.Cursor, kind, out long offset)) return CommandResult<ExtensionDefinitionList>.Rejected([Error("READ.CURSOR_INVALID", "P1", "cursor")]);
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var items = new List<JsonElement>();
        await using (var cmd = tx.Command(
            "select (to_jsonb(d) - 'tenant_id')::text from core.extension_definition d where d.tenant_id = @t and (@e::text is null or d.entity_type = @e) and (@n::text is null or d.namespace = @n) " +
            "and (@k::text is null or d.attr_key = @k) and (@a::boolean is null or d.active = @a) order by d.entity_type, d.namespace, d.attr_key offset @o limit @l"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("l", limit + 1);
            cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)req.EntityType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)req.Namespace ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = (object?)req.AttrKey ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Boolean) { Value = (object?)req.Active ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(JsonDocument.Parse(r.GetString(0)).RootElement.Clone());
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor(kind, offset + limit); }
        return CommandResult<ExtensionDefinitionList>.Ok(new ExtensionDefinitionList(items, new ReadPage(next, limit)), Guid.Empty);
    }
}

/// <summary>What the gets and lists of the extendable entities share (see ExtensionReads).</summary>
internal static class ExtensionRead
{
    public const string PiiPermission = "read.pii";

    /// <summary>The values of the records with the given keys: key to the flat map. Values of is_pii definitions are left out without read.pii, and each record that gets one is logged (PII_EXTENSION). The caller commits.</summary>
    public static async Task<Dictionary<string, Dictionary<string, object?>>> LoadAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, string entityType, IReadOnlyCollection<string> keys, string operation, CancellationToken ct)
    {
        var result = keys.Distinct().ToDictionary(k => k, _ => new Dictionary<string, object?>());
        if (keys.Count == 0) return result;
        bool pii = ctx.Permissions.Contains(PiiPermission);
        var logged = new HashSet<string>();
        await using (var cmd = tx.Command(
            "select v.entity_key, d.namespace || '.' || d.attr_key, d.is_pii, v.value_text, v.value_decimal, v.value_date, v.value_bool, v.value_ref, d.decimal_scale from core.extension_value v " +
            "join core.extension_definition d on d.tenant_id = v.tenant_id and d.definition_id = v.definition_id where v.tenant_id = @t and d.entity_type = @e and v.entity_key = any(@k) order by 1, 2"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", entityType); cmd.Parameters.AddWithValue("k", keys.Distinct().ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.GetBoolean(2))
                {
                    if (!pii) continue;
                    logged.Add(r.GetString(0));
                }
                object? value = !r.IsDBNull(3) ? r.GetString(3) : !r.IsDBNull(4) ? (r.IsDBNull(8) ? (r.GetDecimal(4) / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture) : r.GetDecimal(4).ToString("F" + r.GetInt32(8), CultureInfo.InvariantCulture)) : !r.IsDBNull(5) ? r.GetFieldValue<DateOnly>(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : !r.IsDBNull(6) ? r.GetBoolean(6) : r.GetString(7);
                result[r.GetString(0)][r.GetString(1)] = value;
            }
        }
        foreach (string key in logged)
            await ReadAccessLog.WriteAsync(tx, tenantId, ctx, operation, "PII_EXTENSION", null, entityType == "BUSINESS_PARTNER" && Guid.TryParse(key, out Guid p) ? p : null, null, null, null, ct);
        return result;
    }

    /// <summary>The JSON row with the property "extensions".</summary>
    public static JsonElement Attach(JsonElement row, IReadOnlyDictionary<string, object?>? values)
    {
        var o = new Dictionary<string, object?>();
        foreach (JsonProperty p in row.EnumerateObject()) o[p.Name] = p.Value;
        o["extensions"] = values ?? new Dictionary<string, object?>();
        return JsonSerializer.SerializeToElement(o);
    }

    public static CommandError? CheckEntityType(string entityType) =>
        ExtensionRules.Entity(entityType) is null ? Error("EXT.ENTITY_NOT_EXTENDABLE", "P1", "entity_type") : null;

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);

    /// <summary>
    /// The SQL fragment (" and exists (...)" per filter) and the binding of the filters ext.namespace.attr on a list of records of the type. keyExpr is the SQL expression of the canonical key of the row.
    /// scoped is whether the list has a scope of its own, which a contains on a ledger-scale resource needs (2.4.2).
    /// </summary>
    public static async Task<(string Sql, Action<NpgsqlCommand> Bind, CommandError? Error)> FiltersAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, string entityType, IReadOnlyList<ExtensionFilter>? filters, string keyExpr, bool scoped, string operation, CancellationToken ct)
    {
        if (filters is null || filters.Count == 0) return ("", _ => { }, null);
        if (filters.Count > ExtensionRules.MaxFilters) return ("", _ => { }, Error("READ.INVALID_FILTER", "P1", "ext", ("max", ExtensionRules.MaxFilters)));
        if (CheckEntityType(entityType) is { } notExtendable) return ("", _ => { }, notExtendable);
        var sql = new System.Text.StringBuilder();
        var binds = new List<Action<NpgsqlCommand>>();
        bool usedPii = false;
        for (int idx = 0; idx < filters.Count; idx++)
        {
            int i = idx;
            ExtensionFilter f = filters[i];
            var key = ExtensionRules.SplitKey(f.Field);
            if (key is null || !ExtensionRules.Operators.Contains(f.Op)) return ("", _ => { }, Error("READ.INVALID_FILTER", "P1", "ext", ("field", f.Field), ("allowed", ExtensionRules.Operators)));
            Guid? id = null; string dataType = ""; bool isPii = false;
            await using (var cmd = tx.Command("select definition_id, data_type, is_pii from core.extension_definition where tenant_id = @t and entity_type = @e and namespace = @n and attr_key = @k"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", entityType); cmd.Parameters.AddWithValue("n", key.Value.Namespace); cmd.Parameters.AddWithValue("k", key.Value.Attr);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { id = r.GetGuid(0); dataType = r.GetString(1); isPii = r.GetBoolean(2); }
            }
            if (id is null) return ("", _ => { }, Error("READ.INVALID_FILTER", "P1", "ext", ("field", f.Field)));
            if (isPii)
            {
                if (!ctx.Permissions.Contains(PiiPermission)) return ("", _ => { }, Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", PiiPermission)));
                usedPii = true;
            }
            string col = dataType switch { "TEXT" => "value_text", "DECIMAL" => "value_decimal", "DATE" => "value_date", "BOOLEAN" => "value_bool", _ => "value_ref" };
            NpgsqlDbType db = dataType switch { "TEXT" or "REF" => NpgsqlDbType.Text, "DECIMAL" => NpgsqlDbType.Numeric, "DATE" => NpgsqlDbType.Date, _ => NpgsqlDbType.Boolean };
            bool ordered = dataType is "DECIMAL" or "DATE";
            if ((f.Op is "<" or "<=" or ">" or ">=") && !ordered) return ("", _ => { }, Error("READ.INVALID_FILTER", "P1", "ext", ("field", f.Field), ("reason", "an order needs DECIMAL or DATE")));
            if (f.Op == "contains" && dataType != "TEXT") return ("", _ => { }, Error("READ.INVALID_FILTER", "P1", "ext", ("field", f.Field), ("reason", "contains is for TEXT")));
            string exists = $"exists (select 1 from core.extension_value v{i} where v{i}.tenant_id = @t and v{i}.definition_id = @efd{i} and v{i}.entity_key = {keyExpr}";
            string d = $"@efd{i}", v = $"@efv{i}";
            binds.Add(cmd => cmd.Parameters.AddWithValue($"efd{i}", id.Value));
            if (f.Op == "is_null")
            {
                if (f.Value is not ("true" or "false")) return ("", _ => { }, Error("READ.INVALID_FILTER", "P1", "ext", ("field", f.Field), ("reason", "is_null takes true or false")));
                sql.Append(f.Value == "true" ? $" and not {exists})" : $" and {exists})");
                continue;
            }
            if (f.Op == "in")
            {
                if (f.Values is not { Count: > 0 and <= 100 }) return ("", _ => { }, Error("READ.INVALID_FILTER", "P1", "ext", ("field", f.Field), ("reason", "in takes 1 to 100 values")));
                var parsed = new List<object>();
                foreach (string s in f.Values) { if (!TryParse(dataType, s, out object? p)) return ("", _ => { }, Error("READ.INVALID_FILTER", "P1", "ext", ("field", f.Field), ("reason", "value does not match " + dataType))); parsed.Add(p!); }
                sql.Append($" and {exists} and v{i}.{col} = any({v}))");
                Array typed = ToTypedArray(dataType, parsed);
                binds.Add(cmd => cmd.Parameters.Add(new NpgsqlParameter($"efv{i}", db | NpgsqlDbType.Array) { Value = typed }));
                continue;
            }
            if (f.Value is null) return ("", _ => { }, Error("READ.INVALID_FILTER", "P1", "ext", ("field", f.Field), ("reason", "a value is required")));
            if (f.Op == "contains")
            {
                if (string.Join(' ', f.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Length < SearchRules.MinLength) return ("", _ => { }, Error("READ.CONTAINS_TOO_SHORT", "P1", "ext", ("field", f.Field)));
                if (!scoped) return ("", _ => { }, Error("READ.CONTAINS_REQUIRES_SCOPE", "P1", "ext", ("field", f.Field)));
                sql.Append($" and {exists} and core.search_norm(v{i}.value_text) like core.search_pattern({v}))");
                string text = f.Value;
                binds.Add(cmd => cmd.Parameters.AddWithValue($"efv{i}", text));
                continue;
            }
            if (!TryParse(dataType, f.Value, out object? value)) return ("", _ => { }, Error("READ.INVALID_FILTER", "P1", "ext", ("field", f.Field), ("reason", "value does not match " + dataType)));
            string op = f.Op == "=" ? "=" : f.Op == "!=" ? "<>" : f.Op;
            sql.Append($" and {exists} and v{i}.{col} {op} {v})");
            binds.Add(cmd => cmd.Parameters.Add(new NpgsqlParameter($"efv{i}", db) { Value = value! }));
        }
        if (usedPii) await ReadAccessLog.WriteAsync(tx, tenantId, ctx, operation, "PII_EXTENSION", null, null, null, null, null, ct);
        return (sql.ToString(), cmd => { foreach (var b in binds) b(cmd); }, null);
    }

    private static Array ToTypedArray(string dataType, List<object> values) => dataType switch
    {
        "DECIMAL" => values.Cast<decimal>().ToArray(), "DATE" => values.Cast<DateOnly>().ToArray(), "BOOLEAN" => values.Cast<bool>().ToArray(), _ => values.Cast<string>().ToArray()
    };

    private static bool TryParse(string dataType, string text, out object? value)
    {
        value = null;
        switch (dataType)
        {
            case "DECIMAL": if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal m)) { value = m; return true; } return false;
            case "DATE": if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d)) { value = d; return true; } return false;
            case "BOOLEAN": if (text is "true" or "false") { value = text == "true"; return true; } return false;
            default: value = text; return text.Length > 0;
        }
    }
}
