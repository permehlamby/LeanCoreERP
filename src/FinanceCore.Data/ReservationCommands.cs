using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// reservations:create, :adjust and :release (10, 4.5, 6.5, permission stock.reserve) and availability:query (8, read.inventory). The reserved quantity of a reservation is the sum of its events, and
/// available is the on-hand quantity with status AVAILABLE outside TRANSIT and QUARANTINE locations less the reserved quantity of the active reservations of the item in the warehouse (of the
/// lot or serial, for a reservation of one). Every command locks the item_company row first, like a stock document (6.4), so a reservation and an issue serialise. An issue that consumes a
/// reservation, and the check that an issue does not eat into reserved stock, are in the stock command (StockCommands.Engine.cs). Events reservation.created, .adjusted, .released and .consumed
/// (15, 6). Not built: the best effort reservation at order creation (auto_reserve, 11, 3.1).
/// </summary>
public sealed class ReservationCommands(NpgsqlDataSource source)
{
    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);
    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);

    // ---------------------------------------------------------------------------------------------------------
    // the quantities, shared with the stock command
    // ---------------------------------------------------------------------------------------------------------
    /// <summary>On hand with status AVAILABLE in the warehouse (of one lot or serial when given), without the locations of type TRANSIT and QUARANTINE (4.5).</summary>
    internal static async Task<decimal> OnHandAvailableAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid item, Guid warehouse, Guid? tracking, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select coalesce(sum(x.q), 0) from (select distinct on (m.location_id, m.tracking_id) m.location_id, m.stock_qty_after q from core.stock_movement m where m.tenant_id = @t and m.company_id = @c " +
            "and m.item_id = @i and m.warehouse_id = @w and m.stock_status = 'AVAILABLE' and (@tr::uuid is null or m.tracking_id = @tr) order by m.location_id, m.tracking_id, m.chain_seq desc, m.line_no desc) x " +
            "left join core.warehouse_location l on l.tenant_id = @t and l.company_id = @c and l.warehouse_id = @w and l.location_id = x.location_id where l.location_type is null or l.location_type = 'STANDARD'");
        Bind(cmd, tenantId, companyId, item, warehouse, tracking);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The reserved quantity of the active reservations: of the warehouse, or with lotOnly of the reservations of the given lot or serial.</summary>
    internal static async Task<decimal> ReservedAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid item, Guid warehouse, Guid? tracking, bool lotOnly, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select coalesce(sum(e.base_quantity), 0) from core.stock_reservation r join core.stock_reservation_event e on e.tenant_id = r.tenant_id and e.company_id = r.company_id and e.reservation_id = r.reservation_id " +
            "where r.tenant_id = @t and r.company_id = @c and r.item_id = @i and r.warehouse_id = @w and (@tr::uuid is null or r.tracking_id = @tr or not @lot)");
        Bind(cmd, tenantId, companyId, item, warehouse, tracking);
        cmd.Parameters.AddWithValue("lot", lotOnly);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static void Bind(NpgsqlCommand cmd, Guid tenantId, Guid companyId, Guid item, Guid warehouse, Guid? tracking)
    {
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", item); cmd.Parameters.AddWithValue("w", warehouse);
        cmd.Parameters.Add(new NpgsqlParameter("tr", NpgsqlDbType.Uuid) { Value = (object?)tracking ?? DBNull.Value });
    }

    private sealed record Reservation(Guid Id, Guid Item, Guid Warehouse, Guid? Tracking, string DemandType, string DemandRef, string UnitCode, decimal Reserved);

    private static async Task<Reservation?> ReadAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid id, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select r.item_id, r.warehouse_id, r.tracking_id, r.demand_ref_type, r.demand_ref, r.unit_code, coalesce((select sum(e.base_quantity) from core.stock_reservation_event e where e.tenant_id = r.tenant_id " +
            "and e.company_id = r.company_id and e.reservation_id = r.reservation_id), 0) from core.stock_reservation r where r.tenant_id = @t and r.company_id = @c and r.reservation_id = @r");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? new Reservation(id, rd.GetGuid(0), rd.GetGuid(1), rd.IsDBNull(2) ? null : rd.GetGuid(2), rd.GetString(3).Trim(), rd.GetString(4), rd.GetString(5).Trim(), rd.GetDecimal(6)) : null;
    }

    /// <summary>The reservation that an issue names: its item, warehouse, lot or serial, demand and the quantity it still holds.</summary>
    internal static async Task<(Guid Item, Guid Warehouse, Guid? Tracking, string DemandType, string DemandRef, decimal Reserved)?> ReadForIssueAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid id, CancellationToken ct)
    {
        Reservation? r = await ReadAsync(tx, tenantId, companyId, id, ct);
        return r is null ? null : (r.Item, r.Warehouse, r.Tracking, r.DemandType, r.DemandRef, r.Reserved);
    }

    internal static async Task AppendEventAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid reservationId, string type, decimal baseQuantity, string actor, string? reason, Guid? stockDocumentId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.stock_reservation_event (tenant_id, company_id, reservation_id, event_no, event_type, base_quantity, event_by, reason_code, stock_document_id) " +
            "select @t, @c, @r, coalesce(max(event_no), 0) + 1, @type, @q, @by, @reason, @doc from core.stock_reservation_event where tenant_id = @t and company_id = @c and reservation_id = @r");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", reservationId); cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("q", baseQuantity); cmd.Parameters.AddWithValue("by", actor);
        cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)reason ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("doc", NpgsqlDbType.Uuid) { Value = (object?)stockDocumentId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static Task EventAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, string type, Guid reservationId, string change, Guid item, Guid warehouse, decimal quantity, string? orderLineRef, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        return CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, type, "RESERVATION", $"{companyId:D}/{reservationId:D}", change, new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc), isAudit: false, null,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["item_id"] = item, ["warehouse_id"] = warehouse, ["qty"] = quantity.ToString(CultureInfo.InvariantCulture), ["order_line_ref"] = orderLineRef }), ct);
    }

    private async Task<CommandResult<ReservationResult>> RunAsync(
        Guid tenantId, CommandContext ctx, string command, byte[] fingerprint, Guid companyId, Func<TenantTransaction, Task<(ReservationResult? Result, CommandError? Error)>> work, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(ReservationRules.Permission)) return Reject<ReservationResult>(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", ReservationRules.Permission)));
        var shape = MasterSupport.ValidateContext(ctx);
        if (companyId == Guid.Empty) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (shape.Count > 0) return CommandResult<ReservationResult>.Rejected(shape);
        return await MasterSupport.RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Stock, ct: ct);
            var earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject<ReservationResult>(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                if (earlier.EntityId is null) return CommandResult<ReservationResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""));
                Reservation? known = await ReadAsync(tx, tenantId, companyId, earlier.EntityId.Value, ct);
                return CommandResult<ReservationResult>.Ok(new ReservationResult(known!.Id, known.Item, known.Warehouse, known.Tracking, known.Reserved, 0m), earlier.CommandLogId, replay: true);
            }
            var done = await work(tx);
            if (done.Error is not null) return Reject<ReservationResult>(done.Error);
            await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, command, fingerprint, "RESERVATION", done.Result!.ReservationId, ct);
            await tx.CommitAsync(ct);
            return CommandResult<ReservationResult>.Ok(done.Result, Guid.Empty);
        });
    }

    private static async Task LockItemAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid item, CancellationToken ct)
    {
        await using var cmd = tx.Command("select item_id from core.item_company where tenant_id = @t and company_id = @c and item_id = @i for update");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", item);
        await cmd.ExecuteScalarAsync(ct);
    }

    // ---------------------------------------------------------------------------------------------------------
    // the commands
    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<ReservationResult>> CreateAsync(Guid tenantId, CommandContext ctx, CreateReservationRequest req, CancellationToken ct = default)
    {
        if (ctx.Permissions.Contains(ReservationRules.Permission))
        {
            if (req.ItemId == Guid.Empty || req.WarehouseId == Guid.Empty) return Task.FromResult(Reject<ReservationResult>(Error("SCHEMA.INVALID_VALUE", "P1", "/item_id", ("reason", "item_id and warehouse_id are required"))));
            if (req.Quantity <= 0m || Math.Round(req.Quantity, 6) != req.Quantity) return Task.FromResult(Reject<ReservationResult>(Error("SCHEMA.INVALID_VALUE", "P1", "/quantity", ("reason", "more than 0, at most 6 fraction digits"))));
            if (!ReservationRules.DemandTypes.Contains(req.DemandRefType)) return Task.FromResult(Reject<ReservationResult>(Error("SCHEMA.INVALID_VALUE", "P1", "/demand_ref_type", ("reason", string.Join(", ", ReservationRules.DemandTypes)))));
            if (string.IsNullOrWhiteSpace(req.DemandRef) || req.DemandRef.Length > 100) return Task.FromResult(Reject<ReservationResult>(Error("SCHEMA.INVALID_VALUE", "P1", "/demand_ref", ("reason", "required, at most 100 characters"))));
        }
        byte[] fingerprint = ReservationRules.Fingerprint("reservations:create", $"{req.CompanyId:D}|{req.ItemId:D}|{req.WarehouseId:D}|{req.Quantity.ToString(CultureInfo.InvariantCulture)}|{req.UnitCode}|{req.DemandRefType}|{req.DemandRef}|{req.TrackingNo}");
        return RunAsync(tenantId, ctx, "reservations:create", fingerprint, req.CompanyId, async tx =>
        {
            await LockItemAsync(tx, tenantId, req.CompanyId, req.ItemId, ct);
            string baseUnit; int scale; string tracking;
            await using (var cmd = tx.Command(
                "select i.base_unit, i.quantity_scale, i.tracking_mode from core.item i join core.item_company c on c.tenant_id = i.tenant_id and c.item_id = i.item_id and c.company_id = @c and c.active " +
                "where i.tenant_id = @t and i.item_id = @i and i.active and i.item_type = 'GOODS'"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.ItemId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return (null, Error("CONFIG.INVALID_REFERENCE", "P2", "/item_id", ("item_id", req.ItemId)));
                baseUnit = r.GetString(0).Trim(); scale = r.GetInt16(1); tracking = r.GetString(2).Trim();
            }
            await using (var cmd = tx.Command("select 1 from core.warehouse where tenant_id = @t and company_id = @c and warehouse_id = @w and active"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("w", req.WarehouseId);
                if (await cmd.ExecuteScalarAsync(ct) is null) return (null, Error("CONFIG.INVALID_REFERENCE", "P2", "/warehouse_id", ("warehouse_id", req.WarehouseId)));
            }
            decimal? factor = await OrderCommands.FactorAsync(tx, tenantId, req.ItemId, req.UnitCode, baseUnit, ct);
            if (factor is null) return (null, Error("INVENTORY.UNIT_CONVERSION_MISSING", "P2", "/unit_code", ("item_id", req.ItemId), ("unit_code", req.UnitCode)));
            decimal baseQty = req.Quantity * factor.Value;
            if (Math.Round(baseQty, scale) != baseQty) return (null, Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", "/quantity", ("base_quantity", baseQty), ("quantity_scale", scale)));
            Guid? trackingId = null;
            if (req.TrackingNo is not null)
            {
                await using var cmd = tx.Command("select tracking_id from core.item_tracking where tenant_id = @t and item_id = @i and tracking_no = @n");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", req.ItemId); cmd.Parameters.AddWithValue("n", req.TrackingNo);
                if (tracking == "NONE" || await cmd.ExecuteScalarAsync(ct) is not Guid found) return (null, Error("CONFIG.INVALID_REFERENCE", "P2", "/tracking_no", ("tracking_no", req.TrackingNo)));
                trackingId = found;
            }
            decimal onHand = await OnHandAvailableAsync(tx, tenantId, req.CompanyId, req.ItemId, req.WarehouseId, trackingId, ct);
            decimal reserved = await ReservedAsync(tx, tenantId, req.CompanyId, req.ItemId, req.WarehouseId, trackingId, trackingId is not null, ct);
            decimal available = onHand - reserved;
            if (baseQty > available) return (null, Error("INVENTORY.RESERVATION_EXCEEDS_AVAILABLE", "P4", "/quantity", ("available", available), ("requested", baseQty)));
            Guid id = Guid.NewGuid();
            await using (var cmd = tx.Command(
                "insert into core.stock_reservation (tenant_id, company_id, reservation_id, item_id, warehouse_id, tracking_id, demand_ref_type, demand_ref, unit_code, created_by, correlation_id, idempotency_key) " +
                "values (@t, @c, @r, @i, @w, @tr, @dt, @dr, @u, @by, @corr, @key)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("r", id); cmd.Parameters.AddWithValue("i", req.ItemId);
                cmd.Parameters.AddWithValue("w", req.WarehouseId); cmd.Parameters.Add(new NpgsqlParameter("tr", NpgsqlDbType.Uuid) { Value = (object?)trackingId ?? DBNull.Value });
                cmd.Parameters.AddWithValue("dt", req.DemandRefType); cmd.Parameters.AddWithValue("dr", req.DemandRef); cmd.Parameters.AddWithValue("u", baseUnit);
                cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await AppendEventAsync(tx, tenantId, req.CompanyId, id, "CREATE", baseQty, ctx.Actor, null, null, ct);
            await EventAsync(tx, tenantId, req.CompanyId, ctx, "reservation.created", id, "CREATED", req.ItemId, req.WarehouseId, baseQty, req.DemandRefType == "ORDER_LINE" ? req.DemandRef : null, ct);
            return (new ReservationResult(id, req.ItemId, req.WarehouseId, trackingId, baseQty, available - baseQty), null);
        }, ct);
    }

    public Task<CommandResult<ReservationResult>> AdjustAsync(Guid tenantId, CommandContext ctx, AdjustReservationRequest req, CancellationToken ct = default)
    {
        if (ctx.Permissions.Contains(ReservationRules.Permission))
        {
            if (req.ReservationId == Guid.Empty) return Task.FromResult(Reject<ReservationResult>(Error("SCHEMA.INVALID_VALUE", "P1", "/reservation_id", ("reason", "required"))));
            if (req.Direction is not ("INCREASE" or "DECREASE")) return Task.FromResult(Reject<ReservationResult>(Error("SCHEMA.INVALID_VALUE", "P1", "/direction", ("reason", "INCREASE or DECREASE"))));
            if (req.Quantity <= 0m || Math.Round(req.Quantity, 6) != req.Quantity) return Task.FromResult(Reject<ReservationResult>(Error("SCHEMA.INVALID_VALUE", "P1", "/quantity", ("reason", "more than 0, at most 6 fraction digits"))));
        }
        byte[] fingerprint = ReservationRules.Fingerprint("reservations:adjust", $"{req.CompanyId:D}|{req.ReservationId:D}|{req.Direction}|{req.Quantity.ToString(CultureInfo.InvariantCulture)}|{req.ReasonCode}");
        return RunAsync(tenantId, ctx, "reservations:adjust", fingerprint, req.CompanyId, async tx =>
        {
            Reservation? known = await ReadAsync(tx, tenantId, req.CompanyId, req.ReservationId, ct);
            if (known is null) return (null, Error("READ.NOT_FOUND", "P2", "/reservation_id"));
            await LockItemAsync(tx, tenantId, req.CompanyId, known.Item, ct);
            known = (await ReadAsync(tx, tenantId, req.CompanyId, req.ReservationId, ct))!;
            if (known.Reserved <= 0m) return (null, Error("CONFIG.INVALID_REFERENCE", "P2", "/reservation_id", ("reason", "the reservation is released or consumed")));
            decimal q = req.Quantity;   // in base units: a reservation keeps the base unit of its item
            decimal available = await OnHandAvailableAsync(tx, tenantId, req.CompanyId, known.Item, known.Warehouse, known.Tracking, ct)
                - await ReservedAsync(tx, tenantId, req.CompanyId, known.Item, known.Warehouse, known.Tracking, known.Tracking is not null, ct);
            if (req.Direction == "INCREASE")
            {
                if (q > available) return (null, Error("INVENTORY.RESERVATION_EXCEEDS_AVAILABLE", "P4", "/quantity", ("available", available), ("requested", q)));
                await AppendEventAsync(tx, tenantId, req.CompanyId, known.Id, "INCREASE", q, ctx.Actor, req.ReasonCode, null, ct);
                known = known with { Reserved = known.Reserved + q }; available -= q;
            }
            else
            {
                if (q >= known.Reserved) return (null, Error("SCHEMA.INVALID_VALUE", "P1", "/quantity", ("reason", "less than the reserved quantity: release the reservation instead"), ("reserved", known.Reserved)));
                await AppendEventAsync(tx, tenantId, req.CompanyId, known.Id, "DECREASE", -q, ctx.Actor, req.ReasonCode, null, ct);
                known = known with { Reserved = known.Reserved - q }; available += q;
            }
            await EventAsync(tx, tenantId, req.CompanyId, ctx, "reservation.adjusted", known.Id, "CHANGED", known.Item, known.Warehouse, req.Direction == "INCREASE" ? q : -q, known.DemandType == "ORDER_LINE" ? known.DemandRef : null, ct);
            return (new ReservationResult(known.Id, known.Item, known.Warehouse, known.Tracking, known.Reserved, available), null);
        }, ct);
    }

    public Task<CommandResult<ReservationResult>> ReleaseAsync(Guid tenantId, CommandContext ctx, ReleaseReservationRequest req, CancellationToken ct = default)
    {
        if (ctx.Permissions.Contains(ReservationRules.Permission) && req.ReservationId == Guid.Empty) return Task.FromResult(Reject<ReservationResult>(Error("SCHEMA.INVALID_VALUE", "P1", "/reservation_id", ("reason", "required"))));
        byte[] fingerprint = ReservationRules.Fingerprint("reservations:release", $"{req.CompanyId:D}|{req.ReservationId:D}|{req.ReasonCode}");
        return RunAsync(tenantId, ctx, "reservations:release", fingerprint, req.CompanyId, async tx =>
        {
            Reservation? known = await ReadAsync(tx, tenantId, req.CompanyId, req.ReservationId, ct);
            if (known is null) return (null, Error("READ.NOT_FOUND", "P2", "/reservation_id"));
            await LockItemAsync(tx, tenantId, req.CompanyId, known.Item, ct);
            known = (await ReadAsync(tx, tenantId, req.CompanyId, req.ReservationId, ct))!;
            if (known.Reserved <= 0m) return (null, Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P2", "/reservation_id", ("reason", "nothing is reserved")));
            await AppendEventAsync(tx, tenantId, req.CompanyId, known.Id, "RELEASE", -known.Reserved, ctx.Actor, req.ReasonCode, null, ct);
            await EventAsync(tx, tenantId, req.CompanyId, ctx, "reservation.released", known.Id, "STATUS", known.Item, known.Warehouse, -known.Reserved, known.DemandType == "ORDER_LINE" ? known.DemandRef : null, ct);
            decimal available = await OnHandAvailableAsync(tx, tenantId, req.CompanyId, known.Item, known.Warehouse, known.Tracking, ct)
                - await ReservedAsync(tx, tenantId, req.CompanyId, known.Item, known.Warehouse, known.Tracking, known.Tracking is not null, ct);
            return (new ReservationResult(known.Id, known.Item, known.Warehouse, known.Tracking, 0m, available), null);
        }, ct);
    }

    // ---------------------------------------------------------------------------------------------------------
    // availability:query (8)
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<AvailabilityResult>> AvailabilityAsync(Guid tenantId, CommandContext ctx, AvailabilityRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(StockValuation.ReadPermission)) return Reject<AvailabilityResult>(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", StockValuation.ReadPermission)));
        if (req.CompanyId == Guid.Empty || req.ItemId == Guid.Empty || req.WarehouseId == Guid.Empty) return Reject<AvailabilityResult>(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "company_id, item_id and warehouse_id are required")));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        await using (var cmd = tx.Command("select 1 from core.item_company where tenant_id = @t and company_id = @c and item_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.ItemId);
            if (await cmd.ExecuteScalarAsync(ct) is null) return Reject<AvailabilityResult>(Error("READ.NOT_FOUND", "P2", "/item_id"));
        }
        Guid? tracking = null;
        if (req.TrackingNo is not null)
        {
            await using var cmd = tx.Command("select tracking_id from core.item_tracking where tenant_id = @t and item_id = @i and tracking_no = @n");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", req.ItemId); cmd.Parameters.AddWithValue("n", req.TrackingNo);
            if (await cmd.ExecuteScalarAsync(ct) is not Guid found) return Reject<AvailabilityResult>(Error("READ.NOT_FOUND", "P2", "/tracking_no"));
            tracking = found;
        }
        decimal onHand = await OnHandAvailableAsync(tx, tenantId, req.CompanyId, req.ItemId, req.WarehouseId, tracking, ct);
        decimal reserved = await ReservedAsync(tx, tenantId, req.CompanyId, req.ItemId, req.WarehouseId, tracking, tracking is not null, ct);
        return CommandResult<AvailabilityResult>.Ok(new AvailabilityResult(req.ItemId, req.WarehouseId, tracking, onHand, reserved, onHand - reserved), Guid.Empty);
    }
}
