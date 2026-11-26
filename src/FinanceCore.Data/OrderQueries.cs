using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Reads;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// open-orders:query and uninvoiced:query (11, 8, permission read.orders). The open lines are the lines of orders that are open with a quantity left (ordered less fulfilled, the mirror rows of a reversal
/// counted, over tolerance not counted), by item, warehouse and partner, with both line dates. The uninvoiced lines are the lines with a quantity fulfilled and not invoiced: SALES delivered less
/// invoiced (the invoice rows less the rows of their reversals), PURCHASE received less returned less invoiced (a line invoiced before receipt counts as invoiced), each at the net price of the
/// order line. One snapshot of the read role. A cursor is an offset in the total order, signed and bound to the query (09, 2.4); the orders change in place, so a page is consistent with the outbox position, not frozen. Not built: a date range, the as-of reconstruction, the value of the purchase side at the GR/IR value of the receipt (the order price is used).
/// </summary>
public sealed class OrderQueries(NpgsqlDataSource source)
{
    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) => JournalDocumentPoster.Error(code, phase, path, parameters);
    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);

    private static CommandError? Check(CommandContext ctx, Guid company, string kind, int? limit, string? cursor, string name, object?[] filters, out int effective, out long offset)
    {
        offset = 0;
        effective = limit ?? OrderQueryRules.DefaultLimit;
        if (!ctx.Permissions.Contains(OrderKind.ReadPermission)) return Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", OrderKind.ReadPermission));
        if (company == Guid.Empty) return Error("SCHEMA.INVALID_VALUE", "P1", "company_id", ("reason", "required"));
        if (!OrderQueryRules.Kinds.Contains(kind)) return Error("READ.INVALID_FILTER", "P1", "kind", ("field", "kind"), ("allowed", OrderQueryRules.Kinds));
        if (effective < 1 || effective > OrderQueryRules.MaxLimit) return Error("READ.INVALID_FILTER", "P1", "limit", ("field", "limit"), ("allowed", $"1 to {OrderQueryRules.MaxLimit}"));
        if (!ReadRules.TryDecodeCursor(cursor, ReadRules.QueryKind(name, [company, kind, .. filters]), out offset)) return Error("READ.CURSOR_INVALID", "P1", "cursor");
        return null;
    }

    private static ReadPage Page(string name, object?[] filters, Guid company, string kind, int limit, long offset, int fetched) =>
        new(fetched > limit ? ReadRules.EncodeCursor(ReadRules.QueryKind(name, [company, kind, .. filters]), offset + limit) : null, limit);

    private static async Task<bool> CompanyExistsAsync(TenantTransaction tx, Guid tenantId, Guid company, CancellationToken ct)
    {
        await using var cmd = tx.Command("select 1 from core.company where tenant_id = @t and company_id = @c");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", company);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<CommandResult<OpenOrdersQueryResult>> OpenOrdersAsync(Guid tenantId, CommandContext ctx, OpenOrdersQueryRequest req, CancellationToken ct = default)
    {
        object?[] filters = [req.PartnerId, req.ItemId, req.WarehouseId];
        if (Check(ctx, req.CompanyId, req.Kind, req.Limit, req.Cursor, "open-orders", filters, out int limit, out long offset) is { } denied) return Reject<OpenOrdersQueryResult>(denied);
        bool sales = req.Kind == "SALES";
        string header = sales ? "sales_order" : "purchase_order", line = sales ? "sales_order_line" : "purchase_order_line", id = sales ? "sales_order_id" : "purchase_order_id", party = sales ? "customer_partner_id" : "supplier_partner_id";
        string fulfil = sales ? "sales_delivery_line" : "purchase_receipt_line", lineNo = sales ? "sales_order_line_no" : "purchase_order_line_no";
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        if (!await CompanyExistsAsync(tx, tenantId, req.CompanyId, ct)) return Reject<OpenOrdersQueryResult>(Error("READ.NOT_FOUND", "P2", "company_id"));
        var items = new List<OpenOrderLine>();
        await using (var cmd = tx.Command(
            $"select * from (select h.{id}, h.order_number, l.line_no, h.{party}, l.item_id, l.warehouse_id, l.base_quantity, coalesce(f.done, 0) done, " +
            "case when h.lifecycle_status = 'OPEN' and l.line_status = 'OPEN' then greatest(0, l.base_quantity - coalesce(f.done, 0)) else 0 end open_quantity, l.requested_date, l.confirmed_date " +
            $"from core.{header} h join core.{line} l on l.tenant_id = h.tenant_id and l.company_id = h.company_id and l.{id} = h.{id} " +
            $"left join (select {id} oid, {lineNo} lno, sum(base_quantity) done from core.{fulfil} where tenant_id = @t and company_id = @c group by 1, 2) f on f.oid = l.{id} and f.lno = l.line_no " +
            $"where h.tenant_id = @t and h.company_id = @c and (@p::uuid is null or h.{party} = @p) and (@i::uuid is null or l.item_id = @i) and (@w::uuid is null or l.warehouse_id = @w)) x " +
            "where open_quantity > 0 order by coalesce(confirmed_date, requested_date), order_number, line_no offset @o limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("n", limit + 1);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = (object?)req.PartnerId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Uuid) { Value = (object?)req.ItemId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Uuid) { Value = (object?)req.WarehouseId ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new OpenOrderLine(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetGuid(3), r.GetGuid(4), r.IsDBNull(5) ? null : r.GetGuid(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8),
                    r.IsDBNull(9) ? null : DateOnly.FromDateTime(r.GetDateTime(9)), r.IsDBNull(10) ? null : DateOnly.FromDateTime(r.GetDateTime(10))));
        }
        return CommandResult<OpenOrdersQueryResult>.Ok(new OpenOrdersQueryResult(items.Take(limit).ToList(), Page("open-orders", filters, req.CompanyId, req.Kind, limit, offset, items.Count)), Guid.Empty);
    }

    public async Task<CommandResult<UninvoicedQueryResult>> UninvoicedAsync(Guid tenantId, CommandContext ctx, UninvoicedQueryRequest req, CancellationToken ct = default)
    {
        object?[] filters = [req.PartnerId, req.ItemId];
        if (Check(ctx, req.CompanyId, req.Kind, req.Limit, req.Cursor, "uninvoiced", filters, out int limit, out long offset) is { } denied) return Reject<UninvoicedQueryResult>(denied);
        bool sales = req.Kind == "SALES";
        string header = sales ? "sales_order" : "purchase_order", line = sales ? "sales_order_line" : "purchase_order_line", id = sales ? "sales_order_id" : "purchase_order_id", party = sales ? "customer_partner_id" : "supplier_partner_id";
        string fulfil = sales ? "sales_delivery_line" : "purchase_receipt_line", lineNo = sales ? "sales_order_line_no" : "purchase_order_line_no";
        string invoiceLine = sales ? "sales_invoice_line" : "purchase_invoice_line", invoiceHeader = sales ? "sales_invoice" : "purchase_invoice", invoiceId = sales ? "sales_invoice_id" : "purchase_invoice_id";
        string reversalOf = sales ? "reversal_of_sales_invoice_id" : "reversal_of_purchase_invoice_id";
        string returned = sales ? "0"
            : "coalesce((select sum(x.base_quantity) from core.purchase_return_line x join core.purchase_return rh on rh.tenant_id = x.tenant_id and rh.company_id = x.company_id and rh.return_id = x.return_id " +
              "where x.tenant_id = h.tenant_id and x.company_id = h.company_id and x.purchase_order_line_no = l.line_no and x.purchase_order_id = h.purchase_order_id and rh.status = 'posted' and rh.reversal_of_return_id is null), 0)";
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        if (!await CompanyExistsAsync(tx, tenantId, req.CompanyId, ct)) return Reject<UninvoicedQueryResult>(Error("READ.NOT_FOUND", "P2", "company_id"));
        var items = new List<UninvoicedLine>();
        await using (var cmd = tx.Command((
            "select * from (select h.{ID}, h.order_number, l.line_no, h.{PARTY}, l.item_id, h.order_currency, " +
            "coalesce((select sum(x.base_quantity) from core.{FULFIL} x where x.tenant_id = h.tenant_id and x.company_id = h.company_id and x.{ID} = h.{ID} and x.{LNO} = l.line_no), 0) - {RET} fulfilled, " +
            "coalesce((select sum(x.base_quantity) from core.{ILINE} x join core.{IHEAD} ih on ih.tenant_id = x.tenant_id and ih.company_id = x.company_id and ih.{IID} = x.{IID} " +
            "left join core.{IHEAD} io on io.tenant_id = ih.tenant_id and io.company_id = ih.company_id and io.{IID} = ih.{REV} where x.tenant_id = h.tenant_id and x.company_id = h.company_id and x.{ID} = h.{ID} and x.{LNO} = l.line_no " +
            "and (ih.invoice_kind = 'INVOICE' or (ih.invoice_kind = 'REVERSAL' and io.invoice_kind = 'INVOICE'))), 0) invoiced, l.net_amount, l.base_quantity " +
            "from core.{HEAD} h join core.{LINE} l on l.tenant_id = h.tenant_id and l.company_id = h.company_id and l.{ID} = h.{ID} " +
            "where h.tenant_id = @t and h.company_id = @c and (@p::uuid is null or h.{PARTY} = @p) and (@i::uuid is null or l.item_id = @i)) x where fulfilled > invoiced order by order_number, line_no offset @o limit @n")
            .Replace("{ID}", id).Replace("{PARTY}", party).Replace("{FULFIL}", fulfil).Replace("{LNO}", lineNo).Replace("{RET}", returned).Replace("{ILINE}", invoiceLine).Replace("{IHEAD}", invoiceHeader)
            .Replace("{IID}", invoiceId).Replace("{REV}", reversalOf).Replace("{HEAD}", header).Replace("{LINE}", line)))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("n", limit + 1);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = (object?)req.PartnerId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Uuid) { Value = (object?)req.ItemId ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                decimal fulfilled = r.GetDecimal(6), invoiced = r.GetDecimal(7), net = r.GetDecimal(8), ordered = r.GetDecimal(9), open = fulfilled - invoiced, unit = Math.Round(net / ordered, 6);
                items.Add(new UninvoicedLine(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetGuid(3), r.GetGuid(4), r.GetString(5).Trim(), fulfilled, invoiced, open, unit, Math.Round(net * open / ordered, 4, MidpointRounding.AwayFromZero)));
            }
        }
        return CommandResult<UninvoicedQueryResult>.Ok(new UninvoicedQueryResult(items.Take(limit).ToList(), Page("uninvoiced", filters, req.CompanyId, req.Kind, limit, offset, items.Count)), Guid.Empty);
    }
}
