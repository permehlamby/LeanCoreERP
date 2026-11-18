using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Reads;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>What a master resource lets a caller read (09, 3): its table, its key, the columns a `filter` may name (equal to) and whether the table is the tenant's.</summary>
public sealed record MasterReadSpec(string Resource, string Table, string IdColumn, string[] Filterable, string OrderBy, bool TenantTable = true);

/// <summary>
/// The generic get and list of the master data (09, 3, permission read.config): the row as JSON with its typed values, `active` = true by default and `include_inactive` for all
/// (Q12), a filter of equal-to on the columns the resource names, `limit` and a cursor. One repeatable-read transaction of the read role. Not built: the other operators of 09, 2.4,
/// sort, fields, includes, `as_of` and the extensions.
/// </summary>
public sealed class MasterReads(NpgsqlDataSource source)
{
    public const string Permission = PartnerReadRules.ConfigPermission;

    public static readonly IReadOnlyList<MasterReadSpec> InventorySpecs =
    [
        new("item-groups", "item_group", "item_group_id", ["code", "name"], "code"),
        new("items", "item", "item_id", ["item_no", "item_type", "item_group_id", "tracking_mode", "status", "base_unit"], "item_no"),
        new("item-unit-conversions", "item_unit_conversion", "item_unit_conversion_id", ["item_id", "unit_code"], "item_id, unit_code"),
        new("unit-conversions", "unit_conversion", "unit_conversion_id", ["from_unit", "to_unit"], "from_unit, to_unit"),
        new("warehouses", "warehouse", "warehouse_id", ["company_id", "code", "country_code"], "company_id, code"),
        new("warehouse-locations", "warehouse_location", "location_id", ["company_id", "warehouse_id", "location_code", "location_type"], "warehouse_id, location_code"),
        new("item-companies", "item_company", "item_company_id", ["company_id", "item_id", "valuation_method"], "company_id, item_id"),
        new("item-warehouses", "item_warehouse", "item_warehouse_id", ["company_id", "warehouse_id", "item_id"], "warehouse_id, item_id")
    ];

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);

    private static CommandError? Denied(CommandContext ctx) =>
        ctx.Permissions.Contains(Permission) ? null : Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission));

    public async Task<CommandResult<MasterRowResult>> GetAsync(Guid tenantId, CommandContext ctx, MasterReadSpec spec, GetMasterRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<MasterRowResult>(denied);
        if (req.Id == Guid.Empty) return Reject<MasterRowResult>(Error("SCHEMA.INVALID_VALUE", "P1", "id", ("reason", "required")));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        await using var cmd = tx.Command($"select (to_jsonb(x) - 'tenant_id')::text from core.{spec.Table} x where x.tenant_id = @t and x.{spec.IdColumn} = @id");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", req.Id);
        // a row is returned whatever its active flag: a get by key always finds it (Q12)
        if (await cmd.ExecuteScalarAsync(ct) is not string json) return Reject<MasterRowResult>(Error("READ.NOT_FOUND", "P2", "id"));
        return CommandResult<MasterRowResult>.Ok(new MasterRowResult(JsonDocument.Parse(json).RootElement.Clone()), Guid.Empty);
    }

    public async Task<CommandResult<MasterRowList>> ListAsync(Guid tenantId, CommandContext ctx, MasterReadSpec spec, ListMasterRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<MasterRowList>(denied);
        int limit = req.Limit ?? ReadRules.DefaultLimit;
        if (limit is < 1 or > ReadRules.MaxLimit) return Reject<MasterRowList>(Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit)));
        if (!ReadRules.TryDecodeCursor(req.Cursor, "mst:" + spec.Resource, out long offset)) return Reject<MasterRowList>(Error("READ.CURSOR_INVALID", "P1", "cursor"));
        var filter = req.Filter ?? new Dictionary<string, string>();
        foreach (string f in filter.Keys)
            if (!spec.Filterable.Contains(f)) return Reject<MasterRowList>(Error("READ.INVALID_FILTER", "P1", "filter", ("field", f), ("allowed", spec.Filterable)));

        // the column names come from the resource's list above, never from the request: only the values are parameters
        string where = string.Concat(filter.Keys.Select((f, n) => $" and (to_jsonb(x) ->> '{f}') = @f{n}"));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        await using var cmd = tx.Command(
            $"select (to_jsonb(x) - 'tenant_id')::text from core.{spec.Table} x where x.tenant_id = @t and (@all or x.active){where} order by {spec.OrderBy} offset @o limit @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("all", req.IncludeInactive); cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("n", limit + 1);
        int k = 0;
        foreach (string value in filter.Values) cmd.Parameters.AddWithValue($"f{k++}", value);
        var items = new List<JsonElement>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) items.Add(JsonDocument.Parse(r.GetString(0)).RootElement.Clone());
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor("mst:" + spec.Resource, offset + limit); }
        return CommandResult<MasterRowList>.Ok(new MasterRowList(items, new ReadPage(next, limit)), Guid.Empty);
    }

    /// <summary>units:list (10, 3.1): the global unit codes. No tenant, read.config.</summary>
    public async Task<CommandResult<MasterRowList>> ListUnitsAsync(Guid tenantId, CommandContext ctx, ListMasterRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<MasterRowList>(denied);
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        await using var cmd = tx.Command("select to_jsonb(u)::text from platform.ref_unit u where (@dim::text is null or u.dimension = @dim) order by unit_code");
        cmd.Parameters.Add(new NpgsqlParameter("dim", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)req.Filter?.GetValueOrDefault("dimension") ?? DBNull.Value });
        var items = new List<JsonElement>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(JsonDocument.Parse(r.GetString(0)).RootElement.Clone());
        return CommandResult<MasterRowList>.Ok(new MasterRowList(items, new ReadPage(null, items.Count)), Guid.Empty);
    }
}
