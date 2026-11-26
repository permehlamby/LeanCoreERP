using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// Reservations in the order flows (11, 3.1, 4.4, 6.2, 6.3; 10, 4.5): best effort reservation at the placement of a sales order when the company setting auto_reserve is on, the consumption by a
/// delivery (the delivery names the active reservation of its order line on the stock line), release by short-close, cancellation and a change of warehouse, and a reduction when the quantity
/// of the line falls below what is reserved. A reservation uses the demand ORDER_LINE with the demand_ref `{order id}/{line no}`, whoever created it. Built choices are in 07, 13.2bm.
/// Not built: the increase of a reservation when the quantity of a line grows, the reservation of tracked stock (a lot or serial), the reservation of a line added later.
/// </summary>
public sealed partial class OrderCommands
{
    internal static string DemandRef(Guid orderId, int lineNo) => $"{orderId:D}/{lineNo}";

    private static async Task LockItemAsync(TenantTransaction tx, Guid companyId, Guid item, CancellationToken ct)
    {
        await using var cmd = tx.Command("select core.lock_item_company(@c, @i)");
        cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", item);
        await cmd.ExecuteScalarAsync(ct);
    }

    private sealed record ActiveReservation(Guid Id, Guid Item, Guid Warehouse, decimal Reserved);

    private static async Task<List<ActiveReservation>> ActiveOfLineAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid orderId, int lineNo, CancellationToken ct)
    {
        var list = new List<ActiveReservation>();
        await using var cmd = tx.Command(
            "select r.reservation_id, r.item_id, r.warehouse_id, sum(e.base_quantity) from core.stock_reservation r join core.stock_reservation_event e on e.tenant_id = r.tenant_id and e.company_id = r.company_id " +
            "and e.reservation_id = r.reservation_id where r.tenant_id = @t and r.company_id = @c and r.demand_ref_type = 'ORDER_LINE' and r.demand_ref = @d group by r.reservation_id, r.item_id, r.warehouse_id having sum(e.base_quantity) > 0");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", DemandRef(orderId, lineNo));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ActiveReservation(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetDecimal(3)));
        return list;
    }

    /// <summary>The active reservation of an order line in a warehouse: what a delivery of the line consumes (6.3).</summary>
    internal static async Task<Guid?> ActiveReservationOfLineAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid orderId, int lineNo, Guid warehouse, CancellationToken ct) =>
        (await ActiveOfLineAsync(tx, tenantId, companyId, orderId, lineNo, ct)).Where(r => r.Warehouse == warehouse).Select(r => (Guid?)r.Id).FirstOrDefault();

    /// <summary>Best effort at placement (3.1): what is available, possibly nothing; the shortfall is the backorder that apps plan. Lines of stocked items with a warehouse only.</summary>
    private static async Task<List<LineReservationResult>> AutoReserveAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, Guid orderId, List<NewLine> lines, CancellationToken ct)
    {
        var results = new List<LineReservationResult>();
        for (int i = 0; i < lines.Count; i++)
        {
            NewLine l = lines[i];
            if (l.Fulfilment != "STOCK" || l.Warehouse is not { } warehouse) continue;
            int lineNo = i + 1;
            await LockItemAsync(tx, companyId, l.ItemId, ct);
            string baseUnit, tracking;
            await using (var cmd = tx.Command("select base_unit, tracking_mode from core.item where tenant_id = @t and item_id = @i"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", l.ItemId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                baseUnit = r.GetString(0).Trim(); tracking = r.GetString(1).Trim();
            }
            if (tracking != "NONE") { results.Add(new LineReservationResult(lineNo, null, 0m, l.BaseQuantity)); continue; }
            decimal available = await ReservationCommands.OnHandAvailableAsync(tx, tenantId, companyId, l.ItemId, warehouse, null, ct)
                - await ReservationCommands.ReservedAsync(tx, tenantId, companyId, l.ItemId, warehouse, null, false, ct);
            decimal reserve = Math.Min(l.BaseQuantity, Math.Max(0m, available));
            if (reserve <= 0m) { results.Add(new LineReservationResult(lineNo, null, 0m, l.BaseQuantity)); continue; }
            Guid id = Guid.NewGuid();
            await using (var cmd = tx.Command(
                "insert into core.stock_reservation (tenant_id, company_id, reservation_id, item_id, warehouse_id, tracking_id, demand_ref_type, demand_ref, unit_code, created_by, correlation_id, idempotency_key) " +
                "values (@t, @c, @r, @i, @w, null, 'ORDER_LINE', @dr, @u, @by, @corr, @key)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", id); cmd.Parameters.AddWithValue("i", l.ItemId);
                cmd.Parameters.AddWithValue("w", warehouse); cmd.Parameters.AddWithValue("dr", DemandRef(orderId, lineNo)); cmd.Parameters.AddWithValue("u", baseUnit);
                cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", $"{(ctx.IdempotencyKey.Length > 85 ? ctx.IdempotencyKey[..85] : ctx.IdempotencyKey)}#R{lineNo}");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await ReservationCommands.AppendEventAsync(tx, tenantId, companyId, id, "CREATE", reserve, ctx.Actor, null, null, ct);
            await ReservationCommands.EventAsync(tx, tenantId, companyId, ctx, "reservation.created", id, "CREATED", l.ItemId, warehouse, reserve, DemandRef(orderId, lineNo), ct);
            results.Add(new LineReservationResult(lineNo, id, reserve, l.BaseQuantity - reserve));
        }
        return results;
    }

    /// <summary>A line is short-closed or cancelled, or its warehouse changes: its reservations are released (4.4).</summary>
    private static async Task ReleaseLineReservationsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, Guid orderId, int lineNo, string? reason, CancellationToken ct)
    {
        foreach (ActiveReservation held in await ActiveOfLineAsync(tx, tenantId, companyId, orderId, lineNo, ct))
        {
            await LockItemAsync(tx, companyId, held.Item, ct);
            await ReservationCommands.AppendEventAsync(tx, tenantId, companyId, held.Id, "RELEASE", -held.Reserved, ctx.Actor, reason, null, ct);
            await ReservationCommands.EventAsync(tx, tenantId, companyId, ctx, "reservation.released", held.Id, "STATUS", held.Item, held.Warehouse, -held.Reserved, DemandRef(orderId, lineNo), ct);
        }
    }

    /// <summary>The quantity of a line falls: a reservation never holds more than what is still to be delivered (4.4, "a reservation follows the change").</summary>
    private static async Task ReduceLineReservationsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, Guid orderId, int lineNo, decimal openBaseQuantity, CancellationToken ct)
    {
        foreach (ActiveReservation held in await ActiveOfLineAsync(tx, tenantId, companyId, orderId, lineNo, ct))
        {
            if (held.Reserved <= openBaseQuantity) continue;
            await LockItemAsync(tx, companyId, held.Item, ct);
            decimal cut = held.Reserved - Math.Max(0m, openBaseQuantity);
            bool all = openBaseQuantity <= 0m;
            await ReservationCommands.AppendEventAsync(tx, tenantId, companyId, held.Id, all ? "RELEASE" : "DECREASE", -cut, ctx.Actor, null, null, ct);
            await ReservationCommands.EventAsync(tx, tenantId, companyId, ctx, all ? "reservation.released" : "reservation.adjusted", held.Id, all ? "STATUS" : "CHANGED", held.Item, held.Warehouse, -cut, DemandRef(orderId, lineNo), ct);
        }
    }
}
