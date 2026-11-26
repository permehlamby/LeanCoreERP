using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>A movement as derived, for the fulfilment command that embeds the stock document: the line of the request it came from, its base quantity and value.</summary>
internal sealed record DerivedMovement(int LineNo, int RequestIndex, decimal BaseQuantity, decimal ValueLc);

internal sealed record StockDerivation(Guid StockDocumentId, Guid? JournalDocumentId, IReadOnlyList<DerivedMovement> Movements);

/// <summary>
/// How a fulfilment command (11) embeds the stock document in its own transaction: the command name, whether the stock command writes its command log and commits (a fulfilment
/// command does neither), the callback that gives the detail_digest once the movements are derived (the row hash of the fulfilment document, 11, 5.5), and the receipt value of a line
/// that order management computes from the order price (10, 5.6: a priced receipt is COMPUTED).
/// </summary>
/// <summary>The original movement that a return line refers to (11, 5.4): its quantity and value as positive numbers and what earlier returns of it took.</summary>
internal sealed record ReturnRef(Guid DocumentId, int LineNo, decimal Quantity, decimal Value, decimal ReturnedQuantity, decimal ReturnedValue);

internal sealed record StockPostOptions(
    string CommandName, bool WriteLog, bool Commit, Func<StockDerivation, Task<byte[]?>>? DetailDigest, IReadOnlyDictionary<int, decimal>? LineValues,
    string? ReturnDirection = null, IReadOnlyDictionary<int, ReturnRef>? ReturnRefs = null,
    IReadOnlyDictionary<int, string>? LineSources = null, Func<TenantTransaction, PostedDocument, IReadOnlyDictionary<int, int>, Task>? AfterJournal = null);

/// <summary>
/// stock-documents:post (10, 6.2): RECEIPT, ISSUE, TRANSFER, ADJUSTMENT (a delta or a count) and STATUS_CHANGE, valued by moving average (5.2) or FIFO (5.3) in the company currency, with lot
/// and serial tracking (3.8), the cost scope of the group (3.10), negative stock (5.4) and the location types STANDARD, TRANSIT and QUARANTINE. One transaction: the item rows (10, 6.4), the
/// numbers, the LEDGER and STOCK heads, the stock document with its movements, layers and consumption and the derived journal (a system document of origin STOCK whose detail_digest is the
/// row hash of the stock document, 4.1), the outbox. Costs are computed by the core and a price is accepted only on a receipt and on an adjustment in. The derivation is in
/// StockCommands.Engine.cs. Not built: foreign-currency prices, the dimension defaults of 7, STOCK_TRANSFER_CLEARING (a transfer posts between the two inventory accounts), a transfer or a status
/// change that would go below the stock of its source (it is rejected even when the item allows negative stock).
/// </summary>
public sealed partial class StockCommands(NpgsqlDataSource source)
{
    public const string Permission = StockValuation.PostPermission;

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<StockDocumentResult> Reject(CommandError e) => CommandResult<StockDocumentResult>.Rejected([e]);

    private static DateTime Now() { DateTime n = DateTime.UtcNow; return new DateTime(n.Ticks - n.Ticks % 10, DateTimeKind.Utc); }

    private async Task<CommandResult<StockDocumentResult>> RunAsync(Func<Task<CommandResult<StockDocumentResult>>> once)
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
                return CommandResult<StockDocumentResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<StockDocumentResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    internal sealed record ItemRow(Guid ItemId, Guid GroupId, string ItemType, string BaseUnit, int Scale, string Status, bool Active, string TrackingMode, string? Valuation, bool Activated, bool ExpiryTracked);

    internal sealed record TrackingRow(Guid Id, string No, string Kind, DateOnly? Expiry, string Status);

    internal sealed record LineInfo(ItemRow Item, decimal Factor, string? LocationType, string? ToLocationType, TrackingRow? Tracking);

    public async Task<CommandResult<StockDocumentResult>> PostAsync(Guid tenantId, CommandContext ctx, PostStockDocumentRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (!StockValuation.DocumentTypes.Contains(req.DocumentType)) Invalid("/document_type", string.Join(", ", StockValuation.DocumentTypes));
        if (req.ReasonCode is { } rc && (rc.Length == 0 || rc.Length > 50)) Invalid("/reason_code", "1 to 50 characters");
        if (req.SupplierPartnerId is { } sp && (sp == Guid.Empty || req.DocumentType != "RECEIPT")) Invalid("/supplier_partner_id", "the supplier of a receipt, only a receipt has one (11, 7.2)");
        if (req.Lines is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/lines"));
        for (int i = 0; i < (req.Lines?.Count ?? 0); i++)
        {
            StockLineInput l = req.Lines![i];
            string p = $"/lines/{i}";
            if (string.IsNullOrWhiteSpace(l.LineRef) || l.LineRef.Length > 100) Invalid(p + "/line_ref", "required, at most 100 characters");
            if (l.ItemId == Guid.Empty) Invalid(p + "/item_id", "required");
            if (l.WarehouseId == Guid.Empty) Invalid(p + "/warehouse_id", "required");
            if (string.IsNullOrWhiteSpace(l.UnitCode)) Invalid(p + "/unit_code", "required");
            bool counted = l.Adjustment?.Mode == "TO_COUNTED";
            if (!counted && (l.Quantity <= 0m || Math.Round(l.Quantity, 6) != l.Quantity)) Invalid(p + "/quantity", "more than 0, at most 6 fraction digits");
            if (l.UnitPrice is < 0m || (l.UnitPrice is { } up && Math.Round(up, 4) != up)) Invalid(p + "/unit_price", "not negative, at most 4 fraction digits");
            if (req.DocumentType is "ISSUE" or "TRANSFER" or "STATUS_CHANGE" && l.UnitPrice is not null) Invalid(p + "/unit_price", "a movement is valued by the core, a price is not accepted (5.6)");
            if (l.Tracking is { } tr && (string.IsNullOrWhiteSpace(tr.TrackingNo) || tr.TrackingNo.Length > 50)) Invalid(p + "/tracking/tracking_no", "required, at most 50 characters");
            if (l.StockStatus is not null && !StockValuation.StockStatuses.Contains(l.StockStatus)) Invalid(p + "/stock_status", string.Join(", ", StockValuation.StockStatuses));
            if (req.DocumentType == "TRANSFER" && (l.ToWarehouseId is null || l.ToWarehouseId == Guid.Empty)) Invalid(p + "/to_warehouse_id", "required for a transfer");
            if (l.ReservationId is not null && req.DocumentType != "ISSUE") Invalid(p + "/reservation_id", "only an issue consumes a reservation");
            if (req.DocumentType != "TRANSFER" && (l.ToWarehouseId is not null || l.ToLocationId is not null)) Invalid(p + "/to_warehouse_id", "only a transfer has a destination");
            if (req.DocumentType == "STATUS_CHANGE")
            {
                if (l.FromStatus is null || !StockValuation.StockStatuses.Contains(l.FromStatus)) Invalid(p + "/from_status", string.Join(", ", StockValuation.StockStatuses));
                if (l.ToStatus is null || !StockValuation.StockStatuses.Contains(l.ToStatus)) Invalid(p + "/to_status", string.Join(", ", StockValuation.StockStatuses));
                else if (l.ToStatus == l.FromStatus) Invalid(p + "/to_status", "not the status it comes from");
            }
            else if (l.FromStatus is not null || l.ToStatus is not null) Invalid(p + "/from_status", "only a status change has from_status and to_status");
            if (req.DocumentType == "ADJUSTMENT")
            {
                StockAdjustment? a = l.Adjustment;
                if (a is null) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", p + "/adjustment"));
                else if (a.Mode == "DELTA") { if (a.Direction is not ("IN" or "OUT")) Invalid(p + "/adjustment/direction", "IN or OUT"); }
                else if (a.Mode == "TO_COUNTED")
                {
                    if (a.CountedQuantity is null or < 0m || a.ExpectedBookQuantity is null) Invalid(p + "/adjustment", "counted_quantity and expected_book_quantity are required");
                    if (string.IsNullOrWhiteSpace(a.CountRef)) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", p + "/adjustment/count_ref"));
                }
                else Invalid(p + "/adjustment/mode", "DELTA or TO_COUNTED");
            }
            else if (l.Adjustment is not null) Invalid(p + "/adjustment", "only an adjustment has an adjustment");
        }
        if (req.DocumentType == "ADJUSTMENT" && string.IsNullOrWhiteSpace(req.ReasonCode)) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/reason_code"));
        if (req.DocumentType == "RECEIPT" && req.Lines?.Any(l => l.UnitPrice is null) == true && string.IsNullOrWhiteSpace(req.ReasonCode))
            errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/reason_code", ("reason", "a receipt without a price is quantity-only and needs a reason")));
        var currencies = (req.Lines ?? []).Where(l => l.Currency is not null).Select(l => l.Currency!).Distinct().ToList();
        if (currencies.Count > 1) errors.Add(Error("INVENTORY.CURRENCY_MIXED", "P1", "/lines", ("currencies", currencies)));
        if (errors.Count > 0) return CommandResult<StockDocumentResult>.Rejected(errors);
        byte[] fingerprint = StockValuation.Fingerprint(req);

        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Stock, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                return earlier.EntityId is null
                    ? CommandResult<StockDocumentResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                    : CommandResult<StockDocumentResult>.Ok(await LoadResultAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
            }
            return await PostCoreAsync(tx, tenantId, ctx, req, fingerprint, ct);
        });
    }

    private static async Task<StockDocumentResult> LoadResultAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid id, CancellationToken ct)
    {
        string number, type; long seq; byte[] hash; Guid? journal;
        await using (var cmd = tx.Command("select document_number, document_type, chain_seq, row_hash, journal_document_id from core.stock_document where tenant_id = @t and company_id = @c and stock_document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            number = r.GetString(0); type = r.GetString(1); seq = r.GetInt64(2); hash = (byte[])r[3]; journal = r.IsDBNull(4) ? null : r.GetGuid(4);
        }
        var movements = new List<StockMovementResult>();
        await using (var cmd = tx.Command(
            "select line_no, movement_type, cost_source, item_id, warehouse_id, base_quantity, value_lc, cost_qty_after, cost_value_after_lc, stock_qty_after, tracking_id, stock_status, location_id from core.stock_movement " +
            "where tenant_id = @t and company_id = @c and stock_document_id = @d order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                movements.Add(new StockMovementResult(r.GetInt32(0), null, r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9),
                    r.IsDBNull(10) ? null : r.GetGuid(10), r.GetString(11).Trim(), r.IsDBNull(12) ? null : r.GetGuid(12)));
        }
        return new StockDocumentResult(id, number, type, seq, Canonical.FormatHex32(hash), journal, movements);
    }

    private static async Task<ItemRow?> ReadItemAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid itemId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select i.item_group_id, i.item_type, i.base_unit, i.quantity_scale, i.status, i.active, i.tracking_mode, c.valuation_method, coalesce(c.active, false), i.expiry_tracked from core.item i " +
            "left join core.item_company c on c.tenant_id = i.tenant_id and c.company_id = @c and c.item_id = i.item_id where i.tenant_id = @t and i.item_id = @i");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", itemId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ItemRow(itemId, r.GetGuid(0), r.GetString(1), r.GetString(2).Trim(), r.GetInt16(3), r.GetString(4), r.GetBoolean(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8), r.GetBoolean(9))
            : null;
    }

    private static async Task<TrackingRow?> ReadTrackingAsync(TenantTransaction tx, Guid tenantId, Guid itemId, string trackingNo, CancellationToken ct)
    {
        await using var cmd = tx.Command("select tracking_id, tracking_no, kind, expiry_date, status from core.item_tracking where tenant_id = @t and item_id = @i and tracking_no = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", itemId); cmd.Parameters.AddWithValue("n", trackingNo);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new TrackingRow(r.GetGuid(0), r.GetString(1), r.GetString(2).Trim(), r.IsDBNull(3) ? null : r.GetFieldValue<DateOnly>(3), r.GetString(4).Trim()) : null;
    }

    private static async Task<int> ScaleOfAsync(TenantTransaction tx, string currency, CancellationToken ct)
    {
        await using var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c");
        cmd.Parameters.AddWithValue("c", currency);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, CultureInfo.InvariantCulture);
    }

    /// <summary>The cost scope of the group of the company at the date (10, 3.10): COMPANY, or WAREHOUSE. A company outside any group uses COMPANY.</summary>
    internal static async Task<string> ReadCostScopeAsync(TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly date, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select g.inventory_cost_scope from core.group_membership m join core.company_group g on g.tenant_id = m.tenant_id and g.group_id = m.group_id " +
            "where m.tenant_id = @t and m.company_id = @c and m.valid_from <= @d and (m.valid_to is null or m.valid_to > @d)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", date);
        return await cmd.ExecuteScalarAsync(ct) is string s ? s.Trim() : "COMPANY";
    }

    internal async Task<CommandResult<StockDocumentResult>> PostCoreAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, PostStockDocumentRequest req, byte[] fingerprint, CancellationToken ct, StockPostOptions? options = null)
    {
        string commandName = options?.CommandName ?? "stock-documents:post";
        Guid companyId = req.CompanyId;
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", companyId)));
        if (company.Status != "ACTIVE") return Reject(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company.Status)));
        var errors = new List<CommandError>();
        if (req.SupplierPartnerId is { } supplierId)
        {
            await using var cmd = tx.Command("select is_supplier, restriction_status from core.business_partner where tenant_id = @t and partner_id = @p");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", supplierId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || !r.GetBoolean(0)) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/supplier_partner_id", ("partner_id", supplierId), ("reason", "not a supplier")));
            if (r.GetString(1).Trim() == "RESTRICTED") return Reject(Error("MASTER.PARTNER_RESTRICTED", "P2", "/supplier_partner_id", ("partner_id", supplierId)));
        }

        // P2: the items, warehouses, locations, tracking rows and units
        var items = new Dictionary<Guid, ItemRow>();
        var infos = new LineInfo?[req.Lines.Count];
        var locationTypes = new Dictionary<(Guid, Guid), string?>();
        async Task<string?> LocationTypeAsync(Guid warehouse, Guid location, string path)
        {
            if (locationTypes.TryGetValue((warehouse, location), out string? known)) return known;
            await using var cmd = tx.Command("select location_type from core.warehouse_location where tenant_id = @t and company_id = @c and warehouse_id = @w and location_id = @l and active");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("w", warehouse); cmd.Parameters.AddWithValue("l", location);
            string? type = await cmd.ExecuteScalarAsync(ct) is string s ? s.Trim() : null;
            if (type is null) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", path, ("location_id", location)));
            locationTypes[(warehouse, location)] = type;
            return type;
        }
        async Task<bool> WarehouseOkAsync(Guid warehouse)
        {
            await using var cmd = tx.Command("select 1 from core.warehouse where tenant_id = @t and company_id = @c and warehouse_id = @w and active");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("w", warehouse);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }
        for (int i = 0; i < req.Lines.Count; i++)
        {
            StockLineInput l = req.Lines[i];
            string p = $"/lines/{i}";
            int before = errors.Count;
            if (!items.TryGetValue(l.ItemId, out ItemRow? item))
            {
                item = await ReadItemAsync(tx, tenantId, companyId, l.ItemId, ct);
                if (item is not null) items[l.ItemId] = item;
            }
            if (item is null) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/item_id", ("item_id", l.ItemId))); continue; }
            if (!item.Active || item.Status != "ACTIVE" || item.ItemType != "GOODS" || !item.Activated)
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/item_id", ("reason", "an active, stocked item that the company has activated")));
            else if (item.Valuation is not ("MOVING_AVERAGE" or "FIFO")) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/item_id", ("reason", "the item company has no valuation method")));
            if (!await WarehouseOkAsync(l.WarehouseId)) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/warehouse_id", ("warehouse_id", l.WarehouseId)));
            string? locType = l.LocationId is { } loc ? await LocationTypeAsync(l.WarehouseId, loc, p + "/location_id") : null, toLocType = null;
            if (req.DocumentType == "TRANSFER" && l.ToWarehouseId is { } toWh)
            {
                if (!await WarehouseOkAsync(toWh)) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/to_warehouse_id", ("warehouse_id", toWh)));
                if (l.ToLocationId is { } toLoc) toLocType = await LocationTypeAsync(toWh, toLoc, p + "/to_location_id");
                if (toWh == l.WarehouseId && l.ToLocationId == l.LocationId) errors.Add(Error("SCHEMA.INVALID_VALUE", "P2", p + "/to_warehouse_id", ("reason", "a transfer to the same place")));
            }
            // the location types (3.6, 6.2): a receipt and an issue are not made in a TRANSIT or QUARANTINE location, and a status into QUARANTINE is QUARANTINE or BLOCKED
            if (req.DocumentType is "RECEIPT" or "ISSUE" && locType is "TRANSIT" or "QUARANTINE") errors.Add(Error("INVENTORY.LOCATION_NOT_AVAILABLE", "P2", p + "/location_id", ("location_type", locType)));
            string movedStatus = req.DocumentType == "STATUS_CHANGE" ? l.ToStatus ?? "AVAILABLE" : l.StockStatus ?? "AVAILABLE";
            string? intoType = req.DocumentType == "TRANSFER" ? toLocType : locType;
            if (intoType == "QUARANTINE" && movedStatus == "AVAILABLE" && req.DocumentType is not ("RECEIPT" or "ISSUE"))
                errors.Add(Error("INVENTORY.LOCATION_STATUS_MISMATCH", "P2", p + (req.DocumentType == "STATUS_CHANGE" ? "/to_status" : "/stock_status"), ("location_type", intoType)));
            string? currency = l.Currency;
            if (currency is not null && currency != company.LocalCurrency) errors.Add(Error("LIMIT.STOCK_FOREIGN_CURRENCY_NOT_BUILT", "P2", p + "/currency", ("currency", currency)));
            // lot and serial (3.8.1)
            TrackingRow? tracking = null;
            if (item.TrackingMode == "NONE")
            {
                if (l.Tracking is not null) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", p + "/tracking", ("reason", "the item has no tracking")));
            }
            else if (l.Tracking is null) errors.Add(Error("INVENTORY.TRACKING_REQUIRED", "P2", p + "/tracking", ("item_id", item.ItemId), ("tracking_mode", item.TrackingMode)));
            else
            {
                tracking = await ReadTrackingAsync(tx, tenantId, item.ItemId, l.Tracking.TrackingNo, ct);
                if (tracking is not null && tracking.Kind != item.TrackingMode) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/tracking/tracking_no", ("kind", tracking.Kind)));
                if (item.TrackingMode == "LOT" && l.Tracking.ExpiryDate is null && item.ExpiryTracked && tracking is null && req.DocumentType is "RECEIPT" or "ADJUSTMENT")
                    errors.Add(Error("INVENTORY.TRACKING_REQUIRED", "P2", p + "/tracking/expiry_date", ("item_id", item.ItemId), ("reason", "an expiry tracked lot needs its expiry date")));
            }
            decimal factor = 1m;
            if (l.Adjustment?.Mode != "TO_COUNTED" && errors.Count == before)
            {
                decimal? f = await OrderCommands.FactorAsync(tx, tenantId, item.ItemId, l.UnitCode, item.BaseUnit, ct);
                if (f is null) errors.Add(Error("INVENTORY.UNIT_CONVERSION_MISSING", "P2", p + "/unit_code", ("item_id", item.ItemId), ("unit_code", l.UnitCode)));
                else if (Math.Round(l.Quantity * f.Value, item.Scale) != l.Quantity * f.Value)
                    errors.Add(Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", p + "/quantity", ("base_quantity", l.Quantity * f.Value), ("quantity_scale", item.Scale)));
                else
                {
                    factor = f.Value;
                    if (item.TrackingMode == "SERIAL" && l.Quantity * factor != 1m) errors.Add(Error("INVENTORY.SERIAL_QUANTITY_INVALID", "P2", p + "/quantity", ("base_quantity", l.Quantity * factor)));
                }
            }
            infos[i] = new LineInfo(item, factor, locType, toLocType, tracking);
        }
        if (errors.Count > 0) return CommandResult<StockDocumentResult>.Rejected(errors);

        // P3: the finance calendar, and the series of the stock document and of its journal
        var period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, req.PostingDate, ctx, false, ct);
        if (period.Errors.Count > 0) return CommandResult<StockDocumentResult>.Rejected(period.Errors.Select(e => e with { Path = "/posting_date" }).ToList());
        Guid ledgerId = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
        string journalType = StockValuation.DocumentTypeOf(req.DocumentType), seriesKey = "STOCK_DOC_" + req.DocumentType;
        var docSeries = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, seriesKey, req.PostingDate, ledgerId, ct);
        if (docSeries is null) return Reject(Error("SERIES.NO_ASSIGNMENT", "P2", "/document_type", ("document_type", seriesKey), ("posting_date", req.PostingDate)));
        var journalSeries = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, journalType, req.PostingDate, ledgerId, ct);
        string? groupCurrency = await ClearingPoster.ReadGroupCurrencyAsync(tx, tenantId, companyId, req.PostingDate, ct);
        if (groupCurrency is null) return Reject(Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "/company_id", ("posting_date", req.PostingDate)));
        int fiscalYear = period.FiscalYear!.Value, periodNo = period.PeriodNo!.Value;

        // P7: all the locks, in the order of 10, 6.4: the item rows by item, the counters, the LEDGER and then the STOCK head, the period rows in share mode
        await using (var cmd = tx.Command("select item_id from core.item_company where tenant_id = @t and company_id = @c and item_id = any(@ids) order by item_id for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", items.Keys.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { }
        }
        var counters = new List<(JournalDocumentPoster.SeriesRow, int)> { (docSeries, fiscalYear) };
        if (journalSeries is not null) counters.Add((journalSeries, fiscalYear));
        if (await ClosingDocuments.LockCountersAsync(tx, tenantId, companyId, counters, ct) is { } missing) return Reject(missing);
        ClosingLedger ledger = (await ClosingDocuments.OpenLedgerAsync(tx, tenantId, companyId, ledgerId, company, groupCurrency, ct))!;
        var (stockSeq, stockPrev, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, StockHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, StockHash2.ChainType, ""), ct);
        var recheck = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, req.PostingDate, ctx, true, ct);
        if (recheck.Errors.Count > 0) return CommandResult<StockDocumentResult>.Rejected(recheck.Errors.Select(e => e with { Path = "/posting_date" }).ToList());

        // P4 to P6 under the locks: the running balances, the layers and the values (5.1 to 5.4), in the engine
        int lcScale = await ScaleOfAsync(tx, company.LocalCurrency, ct);
        Guid stockId = Guid.NewGuid();
        string costScope = await ReadCostScopeAsync(tx, tenantId, companyId, req.PostingDate, ct);
        var engine = new Derivation(tx, tenantId, companyId, req.PostingDate, lcScale, costScope, stockId);
        var derived = await engine.DeriveAsync(req, infos!, options, ct);
        if (derived is not null) return Reject(derived);
        var planned = engine.Moves;
        if (planned.Count == 0) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/lines", ("reason", "no line has a difference")));

        // the derived journal (10, 7): one debit and one credit line per valued movement, the accounts from the account determination
        var built = await BuildJournalAsync(tx, tenantId, companyId, company, groupCurrency, ledger, req.PostingDate, engine.Pairs, ct);
        if (built.Error is not null) return Reject(built.Error);
        var journalLines = built.Lines;
        var creditLineOf = built.CreditLineOf;
        Guid? journalId = journalLines.Count > 0 ? Guid.NewGuid() : null;
        if (journalId is not null && journalSeries is null) return Reject(Error("SERIES.NO_ASSIGNMENT", "P2", "/document_type", ("document_type", journalType), ("posting_date", req.PostingDate)));

        // the numbers, the movements and the hash of the stock document
        DateTime at = Now();
        long sequenceNo;
        await using (var cmd = tx.Command("update core.number_range_counter set last_number = last_number + 1, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y returning last_number"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", docSeries.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            sequenceNo = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        string number = Core.Posting.PostingRules.FormatNumber(docSeries.Prefix, docSeries.IncludeFiscalYear, docSeries.Separator, docSeries.NumberLength, fiscalYear, sequenceNo);
        long chainSeq = stockSeq + 1;
        string stockType = StockValuation.DocumentTypeOf(req.DocumentType);
        string? currencyTc = planned.Any(m => m.ValueTc is not null) ? company.LocalCurrency : null;
        var movementRows = planned.Select(m => (IReadOnlyDictionary<string, object?>)MovementRow(tenantId, companyId, stockId, chainSeq, req.PostingDate, m, company.LocalCurrency)).ToList();
        var consumptionRows = planned.SelectMany(m => m.Consumptions.Select(c => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = companyId, ["stock_document_id"] = stockId, ["line_no"] = m.LineNo, ["layer_doc_id"] = c.LayerDoc, ["layer_line_no"] = c.LayerLine,
            ["quantity_base"] = c.Quantity, ["value_lc"] = c.Value
        })).ToList();
        byte[] movementDigest = StockHash2.MovementDigest(movementRows, consumptionRows);
        byte[]? detailDigest = options?.DetailDigest is { } provide
            ? await provide(new StockDerivation(stockId, journalId, planned.Where(m => m.RequestIndex >= 0).Select(m => new DerivedMovement(m.LineNo, m.RequestIndex, m.BaseQuantity, m.ValueLc)).ToList()))
            : null;
        var body = new Dictionary<string, object?>
        {
            ["stock_document_id"] = stockId, ["document_type"] = stockType, ["number_series_id"] = docSeries.SeriesId, ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo,
            ["document_number"] = number, ["fiscal_period"] = periodNo, ["document_date"] = req.DocumentDate, ["posting_date"] = req.PostingDate, ["posted_at"] = at,
            ["reason_code"] = req.ReasonCode, ["reversal_of_stock_document_id"] = null, ["journal_document_id"] = journalId, ["soft_close_override"] = recheck.SoftCloseOverride,
            ["currency_tc"] = currencyTc, ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = req.SourceDocumentRef, ["idempotency_key"] = ctx.IdempotencyKey,
            ["command_name"] = commandName, ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId, ["movement_count"] = planned.Count, ["detail_digest"] = detailDigest,
            ["movement_digest"] = movementDigest, ["hash_version"] = StockHash2.Version
        };
        byte[] rowHash = StockHash2.RowHash(tenantId, companyId, chainSeq, stockPrev, body);

        // the journal first (it binds the stock hash), then the stock document and its movements
        PostedDocument? journal = null;
        if (journalId is { } jid)
        {
            var posted = await ClosingDocuments.PostAsync(ledger, ctx with { SourceSystem = "INVENTORY" },
                new DocumentSpec(stockType, commandName, journalSeries!, fiscalYear, periodNo, recheck.SoftCloseOverride, req.PostingDate, ctx.IdempotencyKey + "#J",
                    DetailDigest: rowHash, Origin: "STOCK", TransactionCurrency: company.LocalCurrency, DocumentId: jid), journalLines, null, ct);
            if (posted.Error is not null) return Reject(posted.Error);
            journal = posted.Document;
            if (journal is not null && options?.AfterJournal is { } afterJournal) await afterJournal(tx, journal, creditLineOf);
        }
        await engine.CreateTrackingAsync(ctx, ct);
        await using (var cmd = tx.Command(
            "insert into core.stock_document (tenant_id, company_id, stock_document_id, document_type, number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, document_date, " +
            "posting_date, posted_at, reason_code, journal_document_id, soft_close_override, currency_tc, source_system, source_document_ref, idempotency_key, command_name, posted_by, correlation_id, " +
            "header_text, movement_count, detail_digest, movement_digest, chain_seq, prev_hash, row_hash, hash_version, supplier_partner_id) values (@t, @c, @id, @type, @s, @fy, @seq, @num, @fp, @dd, @pd, @at, @reason, @j, @soft, @tc, " +
            "@src, @ref, @key, @cmdname, @by, @corr, @text, @n, @dd2, @md, @cs, @prev, @row, 2, @sup)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter("sup", NpgsqlDbType.Uuid) { Value = (object?)req.SupplierPartnerId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", stockId); cmd.Parameters.AddWithValue("type", stockType);
            cmd.Parameters.AddWithValue("s", docSeries.SeriesId); cmd.Parameters.AddWithValue("fy", fiscalYear); cmd.Parameters.AddWithValue("seq", sequenceNo); cmd.Parameters.AddWithValue("num", number);
            cmd.Parameters.AddWithValue("fp", periodNo); cmd.Parameters.AddWithValue("dd", req.DocumentDate); cmd.Parameters.AddWithValue("pd", req.PostingDate);
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
            cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)req.ReasonCode ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("j", NpgsqlDbType.Uuid) { Value = (object?)journalId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("soft", recheck.SoftCloseOverride);
            cmd.Parameters.Add(new NpgsqlParameter("tc", NpgsqlDbType.Text) { Value = (object?)currencyTc ?? DBNull.Value });
            cmd.Parameters.AddWithValue("src", ctx.SourceSystem);
            cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)req.SourceDocumentRef ?? DBNull.Value });
            cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey); cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
            cmd.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = (object?)req.HeaderText ?? DBNull.Value });
            cmd.Parameters.AddWithValue("cmdname", commandName);
            cmd.Parameters.Add(new NpgsqlParameter("dd2", NpgsqlDbType.Bytea) { Value = (object?)detailDigest ?? DBNull.Value });
            cmd.Parameters.AddWithValue("n", planned.Count); cmd.Parameters.AddWithValue("md", movementDigest); cmd.Parameters.AddWithValue("cs", chainSeq);
            cmd.Parameters.AddWithValue("prev", stockPrev); cmd.Parameters.AddWithValue("row", rowHash);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await WriteMovementsAsync(tx, tenantId, companyId, stockId, chainSeq, req.PostingDate, planned, company.LocalCurrency, ct);
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, StockHash2.ChainType, "", chainSeq, rowHash, StockHash2.Version, ct);
        Guid logId = options?.WriteLog == false ? Guid.Empty : await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, commandName, fingerprint, "STOCK_DOCUMENT", stockId, ct);

        // the events (15, 6), the outbox position last: the journal, the stock document and a balance change per stock key
        if (journal is not null) await ClosingDocuments.WriteDocumentEventAsync(ledger, ctx, journal, "ledger.document_posted", journal.DocumentId, "POSTED", ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "stock.document_posted", "STOCK_DOCUMENT", $"{companyId:D}/{stockId:D}", "POSTED", at, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "STOCK", ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["document_type"] = stockType, ["posting_date"] = req.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["movement_count"] = planned.Count, ["journal_document_id"] = journalId }), ct);
        foreach (var last in planned.GroupBy(m => (m.Item.ItemId, m.WarehouseId, m.LocationId)).Select(g => g.Last()))
            await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "stock.balance_changed", "STOCK_BALANCE", $"{companyId:D}/{last.Item.ItemId:D}/{last.WarehouseId:D}", "CHANGED", at, isAudit: false, null,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["item_id"] = last.Item.ItemId, ["warehouse_id"] = last.WarehouseId, ["location_id"] = last.LocationId, ["quantity_after"] = last.StockQtyAfter }), ct);
        await engine.WriteReservationEventsAsync(ctx, at, ct);

        StockDocumentResult result = await LoadResultAsync(tx, tenantId, companyId, stockId, ct);
        result = result with { Movements = result.Movements.Select(m => m with { LineRef = planned[m.LineNo - 1].LineRef }).ToList() };
        if (options?.Commit != false) await tx.CommitAsync(ct);   // the deferred checks: the journal balances, the chain links and the head, the movement count, the outbox position
        return CommandResult<StockDocumentResult>.Ok(result, logId, engine.Warnings);
    }


    /// <summary>The derived journal of a stock document (10, 7): per pair one debit and one credit line, the accounts from the account determination of the role, the group and reporting amounts at the rates of the date.</summary>
    internal static async Task<(List<NewLine> Lines, Dictionary<int, int> CreditLineOf, CommandError? Error)> BuildJournalAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, JournalDocumentPoster.CompanyRow company, string groupCurrency, ClosingLedger ledger, DateOnly date, IReadOnlyList<Derivation.JournalPair> pairs, CancellationToken ct)
    {
        var journalLines = new List<NewLine>();
        var creditLineOf = new Dictionary<int, int>();   // request index to the line of the journal that holds its credit (a receipt: the GR/IR line)
        if (pairs.Count > 0)
        {
            int gcScale = await ScaleOfAsync(tx, groupCurrency, ct);
            int? rcScale = company.ReportingCurrency is null ? null : await ScaleOfAsync(tx, company.ReportingCurrency, ct);
            int fallback = await JournalDocumentPoster.ReadFallbackDaysAsync(tx, tenantId, ct);
            string? pivot = await JournalDocumentPoster.ReadPivotAsync(tx, tenantId, ct);
            var used = new List<Guid>(); var warnings = new List<CommandError>(); var rateErrors = new List<CommandError>();
            decimal rateGc = (await JournalDocumentPoster.LookupRateAsync(tx, tenantId, company.LocalCurrency, groupCurrency, date, fallback, pivot, used, warnings, rateErrors, "group", ct)).Value;
            decimal rateRc = company.ReportingCurrency is null ? 0m
                : (await JournalDocumentPoster.LookupRateAsync(tx, tenantId, company.LocalCurrency, company.ReportingCurrency, date, fallback, pivot, used, warnings, rateErrors, "reporting", ct)).Value;
            if (rateErrors.Count > 0) return (journalLines, creditLineOf, rateErrors[0]);
            foreach (Derivation.JournalPair pair in pairs)
            {
                var resolved = new List<(string Role, decimal Sign, Guid Account)>();
                foreach (var (role, sign, warehouse) in new[] { (pair.DebitRole, 1m, pair.DebitWarehouse), (pair.CreditRole, -1m, pair.CreditWarehouse) })
                {
                    var account = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, role, pair.ItemGroup, warehouse, null, pair.Item, date, ct);
                    if (account is null) return (journalLines, creditLineOf, Error("INVENTORY.ACCOUNT_DETERMINATION_MISSING", "P5", $"/lines/{pair.RequestIndex}", ("role_code", role), ("item_id", pair.Item), ("warehouse_id", warehouse)));
                    resolved.Add((role, sign, account.AccountId));
                }
                // a transfer between two warehouses that post to the same inventory account has no journal (7)
                if (pair.SkipWhenSameAccount && resolved[0].Account == resolved[1].Account) continue;
                foreach (var (role, sign, accountId) in resolved)
                {
                    decimal lc = sign * pair.Value, gc = Math.Round(lc * rateGc, gcScale, MidpointRounding.AwayFromZero);
                    decimal? rc = company.ReportingCurrency is null ? null : Math.Round(lc * rateRc, rcScale!.Value, MidpointRounding.AwayFromZero);
                    NewLine line = ClosingDocuments.Line(ledger, accountId, "BASE", lc, lc, gc, rc, date);
                    // a GR/IR line is a GL open item without partner and due date (ADR-040)
                    journalLines.Add(role == "GR_IR" ? line with { OpenItem = new OpenItemSpec(null, null, null, null, null, null) } : line);
                    if (sign < 0m && pair.CreditRequestIndex is { } creditIndex) creditLineOf[creditIndex] = journalLines.Count;
                }
            }
        }
        return (journalLines, creditLineOf, null);
    }

    // ---------------------------------------------------------------------------------------------------------
    // the rows of a movement: the hash and the insert
    // ---------------------------------------------------------------------------------------------------------
    internal static Dictionary<string, object?> MovementRow(Guid tenantId, Guid companyId, Guid stockId, long chainSeq, DateOnly postingDate, Derivation.Planned m, string localCurrency) => new()
    {
        ["tenant_id"] = tenantId, ["company_id"] = companyId, ["stock_document_id"] = stockId, ["line_no"] = m.LineNo, ["chain_seq"] = chainSeq, ["movement_type"] = m.MovementType,
        ["cost_source"] = m.CostSource, ["valuation_ref_document_id"] = m.RefDocument, ["valuation_ref_line_no"] = m.RefLine, ["posting_date"] = postingDate, ["item_id"] = m.Item.ItemId,
        ["warehouse_id"] = m.WarehouseId, ["location_id"] = m.LocationId, ["tracking_id"] = m.TrackingId, ["stock_status"] = m.StockStatus, ["quantity"] = m.Quantity,
        ["unit_code"] = m.UnitCode, ["base_quantity"] = m.BaseQuantity, ["value_lc"] = m.ValueLc, ["unit_cost_lc"] = StockValuation.UnitCost(m.ValueLc, m.BaseQuantity),
        ["value_tc"] = m.ValueTc, ["currency_tc"] = m.ValueTc is null ? null : localCurrency, ["cost_warehouse_id"] = m.CostWarehouseId, ["cost_qty_after"] = m.CostQtyAfter,
        ["cost_value_after_lc"] = m.CostValueAfter, ["stock_qty_after"] = m.StockQtyAfter, ["lot_expired"] = m.LotExpired, ["count_ref"] = m.CountRef,
        ["expected_book_quantity"] = m.ExpectedBook, ["source_line_ref"] = m.LineRef, ["reversal_of_line_no"] = m.ReversalOfLine
    };

    /// <summary>Inserts the movements, then the layers that they create, the consumption rows and the exhausted flags of the layers that were used up or given back (10, 4.2, 4.3).</summary>
    internal static async Task WriteMovementsAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid stockId, long chainSeq, DateOnly postingDate, IReadOnlyList<Derivation.Planned> planned, string localCurrency, CancellationToken ct)
    {
        foreach (Derivation.Planned m in planned)
        {
            await using var cmd = tx.Command(
                "insert into core.stock_movement (tenant_id, company_id, stock_document_id, line_no, chain_seq, movement_type, cost_source, posting_date, item_id, warehouse_id, location_id, tracking_id, stock_status, " +
                "quantity, unit_code, base_quantity, value_lc, unit_cost_lc, value_tc, currency_tc, cost_warehouse_id, cost_qty_after, cost_value_after_lc, stock_qty_after, lot_expired, count_ref, expected_book_quantity, " +
                "source_line_ref, line_text, valuation_ref_document_id, valuation_ref_line_no, reversal_of_line_no) values (@t, @c, @d, @n, @cs, @type, @src, @pd, @i, @w, @l, @tr, @st, @q, @u, @bq, @v, @uc, @vt, @ct, @cw, @cq, @cv, @sq, " +
                "@lx, @cr, @eb, @ref, @text, @rd, @rn, @rl)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", stockId); cmd.Parameters.AddWithValue("n", m.LineNo);
            cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("type", m.MovementType); cmd.Parameters.AddWithValue("src", m.CostSource);
            cmd.Parameters.AddWithValue("pd", postingDate); cmd.Parameters.AddWithValue("i", m.Item.ItemId); cmd.Parameters.AddWithValue("w", m.WarehouseId);
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = (object?)m.LocationId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("tr", NpgsqlDbType.Uuid) { Value = (object?)m.TrackingId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("st", m.StockStatus);
            cmd.Parameters.AddWithValue("q", m.Quantity); cmd.Parameters.AddWithValue("u", m.UnitCode); cmd.Parameters.AddWithValue("bq", m.BaseQuantity); cmd.Parameters.AddWithValue("v", m.ValueLc);
            cmd.Parameters.Add(new NpgsqlParameter("uc", NpgsqlDbType.Numeric) { Value = (object?)StockValuation.UnitCost(m.ValueLc, m.BaseQuantity) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("vt", NpgsqlDbType.Numeric) { Value = (object?)m.ValueTc ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ct", NpgsqlDbType.Text) { Value = m.ValueTc is null ? DBNull.Value : localCurrency });
            cmd.Parameters.Add(new NpgsqlParameter("cw", NpgsqlDbType.Uuid) { Value = (object?)m.CostWarehouseId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("cq", m.CostQtyAfter); cmd.Parameters.AddWithValue("cv", m.CostValueAfter); cmd.Parameters.AddWithValue("sq", m.StockQtyAfter); cmd.Parameters.AddWithValue("lx", m.LotExpired);
            cmd.Parameters.Add(new NpgsqlParameter("cr", NpgsqlDbType.Text) { Value = (object?)m.CountRef ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("eb", NpgsqlDbType.Numeric) { Value = (object?)m.ExpectedBook ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)m.LineRef ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = (object?)m.LineText ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rd", NpgsqlDbType.Uuid) { Value = (object?)m.RefDocument ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rn", NpgsqlDbType.Integer) { Value = (object?)m.RefLine ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rl", NpgsqlDbType.Integer) { Value = (object?)m.ReversalOfLine ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await WriteLayersAsync(tx, tenantId, companyId, stockId, postingDate, planned, ct);
    }

    internal static async Task WriteLayersAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid stockId, DateOnly postingDate, IReadOnlyList<Derivation.Planned> planned, CancellationToken ct)
    {
        foreach (Derivation.Planned m in planned.Where(m => m.NewLayer is not null))
        {
            Derivation.LayerPlan layer = m.NewLayer!;
            await using var cmd = tx.Command(
                "insert into core.stock_cost_layer (tenant_id, company_id, stock_document_id, line_no, item_id, cost_warehouse_id, tracking_id, layer_seq, layer_date, quantity_base, unit_cost_lc, value_lc) " +
                "values (@t, @c, @d, @n, @i, @cw, @tr, @seq, @ld, @q, @uc, @v)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", stockId); cmd.Parameters.AddWithValue("n", m.LineNo);
            cmd.Parameters.AddWithValue("i", m.Item.ItemId);
            cmd.Parameters.Add(new NpgsqlParameter("cw", NpgsqlDbType.Uuid) { Value = (object?)m.CostWarehouseId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("tr", NpgsqlDbType.Uuid) { Value = (object?)m.TrackingId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("seq", layer.Seq); cmd.Parameters.AddWithValue("ld", layer.Date); cmd.Parameters.AddWithValue("q", layer.Quantity);
            cmd.Parameters.AddWithValue("uc", layer.Quantity == 0m ? 0m : Math.Round(layer.Value / layer.Quantity, 10, MidpointRounding.AwayFromZero)); cmd.Parameters.AddWithValue("v", layer.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (Derivation.Planned m in planned)
            foreach (Derivation.ConsumptionPlan c in m.Consumptions)
            {
                await using var cmd = tx.Command(
                    "insert into core.stock_layer_consumption (tenant_id, company_id, stock_document_id, line_no, layer_doc_id, layer_line_no, quantity_base, value_lc) values (@t, @c, @d, @n, @ld, @ll, @q, @v)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", stockId); cmd.Parameters.AddWithValue("n", m.LineNo);
                cmd.Parameters.AddWithValue("ld", c.LayerDoc); cmd.Parameters.AddWithValue("ll", c.LayerLine); cmd.Parameters.AddWithValue("q", c.Quantity); cmd.Parameters.AddWithValue("v", c.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        // the exhausted flag (S) follows the consumption: a layer used up is set, a layer that was given back is cleared
        await using var flag = tx.Command(
            "update core.stock_cost_layer l set exhausted = (l.quantity_base - coalesce((select sum(c.quantity_base) from core.stock_layer_consumption c where c.tenant_id = l.tenant_id and c.company_id = l.company_id " +
            "and c.layer_doc_id = l.stock_document_id and c.layer_line_no = l.line_no), 0) <= 0) where l.tenant_id = @t and l.company_id = @c and (l.stock_document_id, l.line_no) in " +
            "(select layer_doc_id, layer_line_no from core.stock_layer_consumption where tenant_id = @t and company_id = @c and stock_document_id = @d)");
        flag.Parameters.AddWithValue("t", tenantId); flag.Parameters.AddWithValue("c", companyId); flag.Parameters.AddWithValue("d", stockId);
        await flag.ExecuteNonQueryAsync(ct);
    }
}
