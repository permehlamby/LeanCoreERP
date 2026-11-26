using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Reads;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// sales-orders:get and :list, purchase-orders:get and :list (11, 8, permission read.orders): the order as typed JSON with the includes `lines`, `events` and `changes`, and a list with
/// the filters partner, lifecycle status, order date range and source reference. One repeatable-read transaction of the read role. Not built: the includes `progress`, `address`,
/// `fulfilments` and `invoices` (nothing of them exists yet), the filters on item and warehouse, `open-orders:query` and the other order reads.
/// </summary>
public sealed class OrderReads(NpgsqlDataSource source)
{
    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);

    private static CommandError? Denied(CommandContext ctx) =>
        ctx.Permissions.Contains(OrderKind.ReadPermission) ? null : Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", OrderKind.ReadPermission));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public async Task<CommandResult<OrderDetail>> GetAsync(OrderKind kind, Guid tenantId, CommandContext ctx, GetOrderRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<OrderDetail>(denied);
        if (req.Id == Guid.Empty) return Reject<OrderDetail>(Error("SCHEMA.INVALID_VALUE", "P1", "id", ("reason", "required")));
        IReadOnlyList<string> include = req.Include ?? [];
        foreach (string i in include)
            if (!OrderRules.Includes.Contains(i)) return Reject<OrderDetail>(Error("READ.INVALID_FILTER", "P1", "include", ("field", i), ("allowed", OrderRules.Includes.ToArray())));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        string? json; Guid companyId;
        await using (var cmd = tx.Command($"select company_id, (to_jsonb(h) - 'tenant_id')::text from core.{kind.HeaderTable} h where tenant_id = @t and {kind.IdColumn} = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", req.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Reject<OrderDetail>(Error("READ.NOT_FOUND", "P2", "id"));
            companyId = r.GetGuid(0); json = r.GetString(1);
        }
        async Task<JsonElement> Array(string sql)
        {
            await using var cmd = tx.Command(sql);
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", req.Id); cmd.Parameters.AddWithValue("k", kind.Kind);
            return Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
        }
        JsonElement? lines = include.Contains("lines")
            ? await Array($"select coalesce(jsonb_agg(to_jsonb(l) - 'tenant_id' - 'company_id' - '{kind.IdColumn}' order by line_no), '[]'::jsonb)::text from core.{kind.LineTable} l where tenant_id = @t and company_id = @c and {kind.IdColumn} = @o") : null;
        JsonElement? events = include.Contains("events")
            ? await Array("select coalesce(jsonb_agg(to_jsonb(e) - 'tenant_id' - 'company_id' order by event_no), '[]'::jsonb)::text from core.order_event e where tenant_id = @t and company_id = @c and order_kind = @k and order_id = @o") : null;
        JsonElement? changes = include.Contains("changes")
            ? await Array("select coalesce(jsonb_agg(to_jsonb(x) - 'tenant_id' - 'company_id' order by changed_at, change_id), '[]'::jsonb)::text from core.order_change_log x where tenant_id = @t and company_id = @c and order_kind = @k and order_id = @o") : null;
        // 4.3: ordered, fulfilled (delivered or received, mirror rows included), the open quantity (over tolerance not counted, zero for a line that is not open) and the limit with the over tolerance
        string fulfilTable = kind.Kind == "SALES" ? "sales_delivery_line" : "purchase_receipt_line", fulfilId = kind.Kind == "SALES" ? "sales_order_id" : "purchase_order_id", fulfilLine = kind.Kind == "SALES" ? "sales_order_line_no" : "purchase_order_line_no";
        string returnTable = kind.Kind == "SALES" ? "sales_return_line" : "purchase_return_line";
        JsonElement? progress = include.Contains("progress")
            ? await Array(
                $"select coalesce(jsonb_agg(jsonb_build_object('line_no', l.line_no, 'line_status', l.line_status, 'ordered', l.base_quantity, 'fulfilled', coalesce(f.q, 0), " +
                "'returned', coalesce(rt.q, 0), 'open', case when l.line_status = 'OPEN' then greatest(0, l.base_quantity - coalesce(f.q, 0)) else 0 end, " +
                "'limit', greatest(0, l.base_quantity + l.base_quantity * l.over_tolerance_pct / 100 - coalesce(f.q, 0))) order by l.line_no), '[]'::jsonb)::text " +
                $"from core.{kind.LineTable} l left join lateral (select sum(x.base_quantity) q from core.{fulfilTable} x where x.tenant_id = l.tenant_id and x.company_id = l.company_id " +
                $"and x.{fulfilId} = l.{kind.IdColumn} and x.{fulfilLine} = l.line_no) f on true " +
                $"left join lateral (select sum(y.base_quantity) q from core.{returnTable} y where y.tenant_id = l.tenant_id and y.company_id = l.company_id and y.{fulfilId} = l.{kind.IdColumn} and y.{fulfilLine} = l.line_no) rt on true where l.tenant_id = @t and l.company_id = @c and l.{kind.IdColumn} = @o")
            : null;
        JsonElement header = Parse(json);
        if (include.Contains("extensions"))
        {
            string headerType = kind.Kind + "_ORDER", id = req.Id.ToString("D");
            header = ExtensionRead.Attach(header, (await ExtensionRead.LoadAsync(tx, tenantId, ctx, headerType, [id], kind.Kind.ToLowerInvariant() + "-orders:get", ct))[id]);
            if (lines is { } lineArray)
            {
                string Key(JsonElement l) => id + "/" + l.GetProperty("line_no").GetInt32();
                var values = await ExtensionRead.LoadAsync(tx, tenantId, ctx, headerType + "_LINE", lineArray.EnumerateArray().Select(Key).ToList(), kind.Kind.ToLowerInvariant() + "-orders:get", ct);
                lines = JsonSerializer.SerializeToElement(lineArray.EnumerateArray().Select(l => ExtensionRead.Attach(l, values[Key(l)])).ToList());
            }
            await tx.CommitAsync(ct);
        }
        return CommandResult<OrderDetail>.Ok(new OrderDetail(header, lines, events, changes, progress), Guid.Empty);
    }

    public async Task<CommandResult<OrderList>> ListAsync(OrderKind kind, Guid tenantId, CommandContext ctx, ListOrdersRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<OrderList>(denied);
        int limit = req.Limit ?? ReadRules.DefaultLimit;
        if (limit is < 1 or > ReadRules.MaxLimit) return Reject<OrderList>(Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit)));
        if (!ReadRules.TryDecodeCursor(req.Cursor, "ord:" + kind.Kind, out long offset)) return Reject<OrderList>(Error("READ.CURSOR_INVALID", "P1", "cursor"));
        if (req.LifecycleStatus is not (null or "OPEN" or "CLOSED" or "CANCELLED")) return Reject<OrderList>(Error("READ.INVALID_FILTER", "P1", "lifecycle_status", ("field", "lifecycle_status")));

        foreach (string i in req.Include ?? [])
            if (i != "extensions") return Reject<OrderList>(Error("READ.INVALID_FILTER", "P1", "include", ("field", i), ("allowed", new[] { "extensions" })));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        string headerType = kind.Kind + "_ORDER";
        var ext = await ExtensionRead.FiltersAsync(tx, tenantId, ctx, headerType, req.Ext, $"h.{kind.IdColumn}::text", true, kind.Kind.ToLowerInvariant() + "-orders:list", ct);
        if (ext.Error is not null) return Reject<OrderList>(ext.Error);
        var items = new List<JsonElement>();
        await using (var cmd = tx.Command(
            $"select (to_jsonb(h) - 'tenant_id')::text from core.{kind.HeaderTable} h where tenant_id = @t{ext.Sql} and (@p::uuid is null or {kind.PartyColumn} = @p) and (@s::text is null or lifecycle_status = @s) " +
            "and (@from::date is null or order_date >= @from) and (@to::date is null or order_date <= @to) and (@ref::text is null or source_document_ref = @ref) " +
            "order by order_date, order_number offset @o limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("n", limit + 1); ext.Bind(cmd);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = (object?)req.PartnerId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)req.LifecycleStatus ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = (object?)req.DateFrom ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = (object?)req.DateTo ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)req.SourceDocumentRef ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(Parse(r.GetString(0)));
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor("ord:" + kind.Kind, offset + limit); }
        if (req.Include?.Contains("extensions") == true)
        {
            string Key(JsonElement e) => e.GetProperty(kind.IdColumn).GetGuid().ToString("D");
            var values = await ExtensionRead.LoadAsync(tx, tenantId, ctx, headerType, items.Select(Key).ToList(), kind.Kind.ToLowerInvariant() + "-orders:list", ct);
            items = items.Select(e => ExtensionRead.Attach(e, values[Key(e)])).ToList();
            await tx.CommitAsync(ct);
        }
        return CommandResult<OrderList>.Ok(new OrderList(items, new ReadPage(next, limit)), Guid.Empty);
    }
}
