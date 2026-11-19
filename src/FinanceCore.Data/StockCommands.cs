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
    string? ReturnDirection = null, IReadOnlyDictionary<int, ReturnRef>? ReturnRefs = null);

/// <summary>
/// stock-documents:post (10, 6.2), first slice: RECEIPT, ISSUE and ADJUSTMENT (a delta or a count) of items without tracking, valued by moving average (5.2) in the company currency, in
/// the locations of type STANDARD or none. One transaction: the item rows (10, 6.4), the numbers, the LEDGER and STOCK heads, the stock document with its movements and the derived
/// journal (a system document of origin STOCK whose detail_digest is the row hash of the stock document, 4.1), the outbox. Costs are computed by the core and a price is accepted only on a
/// receipt and on an adjustment in. Built choices are in 07, 13.2v. Not built: FIFO, tracking, foreign-currency prices, negative stock, transfers, status changes, returns, cost
/// adjustments, reversal, reservations, the dimension defaults of 7 and the verification of the STOCK chain.
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

    private sealed record ItemRow(Guid ItemId, Guid GroupId, string ItemType, string BaseUnit, int Scale, string Status, bool Active, string TrackingMode, string? Valuation, bool Activated);

    private sealed record Planned(
        int LineNo, StockLineInput Input, ItemRow Item, string MovementType, string CostSource, decimal Quantity, decimal BaseQuantity, decimal ValueLc, decimal? ValueTc,
        decimal CostQtyAfter, decimal CostValueAfter, decimal StockQtyAfter, decimal? ExpectedBook, string? CountRef, string UnitCode, int RequestIndex, Guid? RefDocument = null, int? RefLine = null);

    private sealed record JournalPair(string DebitRole, string CreditRole, decimal Value, Guid ItemGroup, Guid Warehouse, Guid Item, int LineNo);

    public async Task<CommandResult<StockDocumentResult>> PostAsync(Guid tenantId, CommandContext ctx, PostStockDocumentRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (!StockValuation.DocumentTypes.Contains(req.DocumentType)) Invalid("/document_type", "RECEIPT, ISSUE or ADJUSTMENT");
        if (req.ReasonCode is { } rc && (rc.Length == 0 || rc.Length > 50)) Invalid("/reason_code", "1 to 50 characters");
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
            if (req.DocumentType == "ISSUE" && l.UnitPrice is not null) Invalid(p + "/unit_price", "an issue is valued by the core, a price is not accepted (5.6)");
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
            "select line_no, movement_type, cost_source, item_id, warehouse_id, base_quantity, value_lc, cost_qty_after, cost_value_after_lc, stock_qty_after from core.stock_movement " +
            "where tenant_id = @t and company_id = @c and stock_document_id = @d order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                movements.Add(new StockMovementResult(r.GetInt32(0), null, r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9)));
        }
        return new StockDocumentResult(id, number, type, seq, Canonical.FormatHex32(hash), journal, movements);
    }

    private static async Task<ItemRow?> ReadItemAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid itemId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select i.item_group_id, i.item_type, i.base_unit, i.quantity_scale, i.status, i.active, i.tracking_mode, c.valuation_method, coalesce(c.active, false) from core.item i " +
            "left join core.item_company c on c.tenant_id = i.tenant_id and c.company_id = @c and c.item_id = i.item_id where i.tenant_id = @t and i.item_id = @i");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", itemId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ItemRow(itemId, r.GetGuid(0), r.GetString(1), r.GetString(2).Trim(), r.GetInt16(3), r.GetString(4), r.GetBoolean(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8))
            : null;
    }

    private static async Task<(decimal Qty, decimal Value)> CostBalanceAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid itemId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select cost_qty_after, cost_value_after_lc from core.stock_movement where tenant_id = @t and company_id = @c and item_id = @i and cost_warehouse_id is null and tracking_id is null " +
            "order by chain_seq desc, line_no desc limit 1");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", itemId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetDecimal(0), r.GetDecimal(1)) : (0m, 0m);
    }

    private static async Task<decimal> StockBalanceAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid itemId, Guid warehouseId, Guid? locationId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select stock_qty_after from core.stock_movement where tenant_id = @t and company_id = @c and item_id = @i and warehouse_id = @w and location_id is not distinct from @l::uuid " +
            "and tracking_id is null and stock_status = 'AVAILABLE' order by chain_seq desc, line_no desc limit 1");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", itemId); cmd.Parameters.AddWithValue("w", warehouseId);
        cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = (object?)locationId ?? DBNull.Value });
        return await cmd.ExecuteScalarAsync(ct) is decimal d ? d : 0m;
    }

    private static async Task<int> ScaleOfAsync(TenantTransaction tx, string currency, CancellationToken ct)
    {
        await using var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c");
        cmd.Parameters.AddWithValue("c", currency);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, CultureInfo.InvariantCulture);
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

        // P2: the items, warehouses, locations and units
        var items = new Dictionary<Guid, ItemRow>();
        var factors = new Dictionary<int, decimal>();
        for (int i = 0; i < req.Lines.Count; i++)
        {
            StockLineInput l = req.Lines[i];
            string p = $"/lines/{i}";
            if (!items.TryGetValue(l.ItemId, out ItemRow? item))
            {
                item = await ReadItemAsync(tx, tenantId, companyId, l.ItemId, ct);
                if (item is not null) items[l.ItemId] = item;
            }
            if (item is null) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/item_id", ("item_id", l.ItemId))); continue; }
            if (!item.Active || item.Status != "ACTIVE" || item.ItemType != "GOODS" || !item.Activated)
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/item_id", ("reason", "an active, stocked item that the company has activated")));
            else if (item.TrackingMode != "NONE") errors.Add(Error("LIMIT.STOCK_TRACKING_NOT_BUILT", "P2", p + "/item_id"));
            else if (item.Valuation != "MOVING_AVERAGE") errors.Add(Error("LIMIT.STOCK_FIFO_NOT_BUILT", "P2", p + "/item_id"));
            await using (var cmd = tx.Command("select 1 from core.warehouse where tenant_id = @t and company_id = @c and warehouse_id = @w and active"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("w", l.WarehouseId);
                if (await cmd.ExecuteScalarAsync(ct) is null) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/warehouse_id", ("warehouse_id", l.WarehouseId)));
            }
            if (l.LocationId is { } loc)
            {
                await using var cmd = tx.Command("select location_type from core.warehouse_location where tenant_id = @t and company_id = @c and warehouse_id = @w and location_id = @l and active");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("w", l.WarehouseId); cmd.Parameters.AddWithValue("l", loc);
                if (await cmd.ExecuteScalarAsync(ct) is not string type) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/location_id", ("location_id", loc)));
                else if (type != "STANDARD") errors.Add(Error("LIMIT.STOCK_LOCATION_NOT_BUILT", "P2", p + "/location_id", ("location_type", type)));
            }
            string? currency = l.Currency;
            if (currency is not null && currency != company.LocalCurrency) errors.Add(Error("LIMIT.STOCK_FOREIGN_CURRENCY_NOT_BUILT", "P2", p + "/currency", ("currency", currency)));
            if (l.Adjustment?.Mode == "TO_COUNTED") { factors[i] = 1m; continue; }
            if (errors.Any(e => e.Path.StartsWith(p, StringComparison.Ordinal))) continue;
            decimal? factor = await OrderCommands.FactorAsync(tx, tenantId, item.ItemId, l.UnitCode, item.BaseUnit, ct);
            if (factor is null) { errors.Add(Error("INVENTORY.UNIT_CONVERSION_MISSING", "P2", p + "/unit_code", ("item_id", item.ItemId), ("unit_code", l.UnitCode))); continue; }
            if (Math.Round(l.Quantity * factor.Value, item.Scale) != l.Quantity * factor.Value)
                errors.Add(Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", p + "/quantity", ("base_quantity", l.Quantity * factor.Value), ("quantity_scale", item.Scale)));
            else factors[i] = factor.Value;
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

        // P4 to P6 under the locks: the running balances and the values (5.2)
        int lcScale = await ScaleOfAsync(tx, company.LocalCurrency, ct);
        var cost = new Dictionary<Guid, (decimal Qty, decimal Value)>();
        var stock = new Dictionary<(Guid, Guid, Guid?), decimal>();
        var planned = new List<Planned>();
        var pairs = new List<JournalPair>();
        for (int i = 0; i < req.Lines.Count; i++)
        {
            StockLineInput l = req.Lines[i];
            string p = $"/lines/{i}";
            ItemRow item = items[l.ItemId];
            if (!cost.ContainsKey(item.ItemId)) cost[item.ItemId] = await CostBalanceAsync(tx, tenantId, companyId, item.ItemId, ct);
            var skey = (item.ItemId, l.WarehouseId, l.LocationId);
            if (!stock.ContainsKey(skey)) stock[skey] = await StockBalanceAsync(tx, tenantId, companyId, item.ItemId, l.WarehouseId, l.LocationId, ct);
            (decimal costQty, decimal costValue) = cost[item.ItemId];
            string unitCode = l.UnitCode;
            decimal quantity = l.Quantity, baseQty = quantity * factors[i];
            decimal? expectedBook = null; string? countRef = null;
            string type; string source = "COMPUTED"; decimal value; decimal? valueTc = null;
            bool inbound;
            ReturnRef? returnRef = null;
            if (req.DocumentType == "RECEIPT")
            {
                type = "RECEIPT"; inbound = true;
                if (options?.LineValues is { } lineValues && lineValues.TryGetValue(i, out decimal orderValue)) { value = orderValue; valueTc = value; }
                else
                {
                    value = l.UnitPrice is { } price ? StockValuation.ReceiptValue(quantity, price, lcScale) : 0m;
                    valueTc = l.UnitPrice is null ? null : value;
                }
            }
            else if (req.DocumentType == "ISSUE") { type = "ISSUE"; inbound = false; value = 0m; }
            else if (req.DocumentType == "RETURN")
            {
                // 11, 5.4: the value comes from the original movement, in proportion to the returned quantity; the caller supplies none
                ReturnRef rr = options!.ReturnRefs![i];
                inbound = options.ReturnDirection == "IN"; type = inbound ? "RETURN_IN" : "RETURN_OUT";
                value = ReturnRules.ReturnValue(rr.Value, rr.Quantity, rr.ReturnedValue, rr.ReturnedQuantity, baseQty, lcScale);
                returnRef = rr;
            }
            else if (l.Adjustment!.Mode == "TO_COUNTED")
            {
                decimal book = stock[skey];
                if (book != l.Adjustment.ExpectedBookQuantity)
                    return Reject(Error("INVENTORY.BOOK_QUANTITY_CHANGED", "P4", p + "/adjustment/expected_book_quantity", ("current", book), ("expected", l.Adjustment.ExpectedBookQuantity)));
                decimal diff = l.Adjustment.CountedQuantity!.Value - book;
                if (Math.Round(diff, item.Scale) != diff) return Reject(Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", p + "/adjustment/counted_quantity", ("quantity_scale", item.Scale)));
                if (diff == 0m) continue;   // a count without a difference creates no movement (6.2)
                inbound = diff > 0m; baseQty = Math.Abs(diff); quantity = baseQty; unitCode = item.BaseUnit; type = inbound ? "ADJUST_IN" : "ADJUST_OUT";
                expectedBook = l.Adjustment.ExpectedBookQuantity; countRef = l.Adjustment.CountRef; value = 0m;
            }
            else { inbound = l.Adjustment!.Direction == "IN"; type = inbound ? "ADJUST_IN" : "ADJUST_OUT"; value = 0m; }

            if (inbound)
            {
                if (req.DocumentType == "ADJUSTMENT")
                {
                    if (l.UnitPrice is { } suppliedPrice) { value = StockValuation.ReceiptValue(quantity, suppliedPrice, lcScale); source = "SUPPLIED"; valueTc = value; }
                    else if (costQty > 0m) value = Math.Round(costValue * baseQty / costQty, lcScale, MidpointRounding.AwayFromZero);
                    else return Reject(Error("INVENTORY.VALUE_REQUIRED", "P4", p + "/unit_price", ("item_id", item.ItemId)));
                }
                costQty += baseQty; costValue += value; stock[skey] += baseQty;
            }
            else if (returnRef is not null)
            {
                // a return to the supplier takes the original value out; what the receipt brought must still be there (10, 5.3)
                costQty -= baseQty; costValue -= value; stock[skey] -= baseQty;
                if (costQty < 0m || costValue < 0m || (costQty == 0m && costValue != 0m) || stock[skey] < 0m)
                    return Reject(Error("INVENTORY.RECEIPT_CONSUMED", "P4", p + "/quantity", ("item_id", item.ItemId), ("on_hand", cost[item.ItemId].Qty), ("requested", baseQty)));
                value = -value; baseQty = -baseQty; quantity = -quantity;
            }
            else
            {
                if (costQty < baseQty) return Reject(Error("INVENTORY.INSUFFICIENT_STOCK", "P4", p + "/quantity", ("item_id", item.ItemId), ("on_hand", costQty), ("requested", baseQty)));
                value = -StockValuation.IssueValue(costQty, costValue, baseQty, lcScale);
                costQty -= baseQty; costValue += value; stock[skey] -= baseQty; baseQty = -baseQty; quantity = -quantity;
            }
            cost[item.ItemId] = (costQty, costValue);
            planned.Add(new Planned(planned.Count + 1, l, item, type, source, quantity, baseQty, value, valueTc, costQty, costValue, stock[skey], expectedBook, countRef, unitCode, i, returnRef?.DocumentId, returnRef?.LineNo));
            if (value != 0m)
            {
                (string debit, string credit) = type switch
                {
                    "RECEIPT" => ("INVENTORY", "GR_IR"), "ISSUE" => (StockValuation.IssueRole(req.ReasonCode), "INVENTORY"),
                    "ADJUST_IN" => ("INVENTORY", "ADJUSTMENT_GAIN"), "RETURN_IN" => ("INVENTORY", "COGS"), "RETURN_OUT" => ("GR_IR", "INVENTORY"), _ => ("ADJUSTMENT_LOSS", "INVENTORY")
                };
                pairs.Add(new JournalPair(debit, credit, Math.Abs(value), item.GroupId, l.WarehouseId, item.ItemId, planned.Count));
            }
        }
        if (planned.Count == 0) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/lines", ("reason", "no line has a difference")));

        // the derived journal (10, 7): one debit and one credit line per valued movement, the accounts from the account determination
        var journalLines = new List<NewLine>();
        Guid? journalId = pairs.Count > 0 ? Guid.NewGuid() : null;
        if (pairs.Count > 0)
        {
            if (journalSeries is null) return Reject(Error("SERIES.NO_ASSIGNMENT", "P2", "/document_type", ("document_type", journalType), ("posting_date", req.PostingDate)));
            int gcScale = await ScaleOfAsync(tx, groupCurrency, ct);
            int? rcScale = company.ReportingCurrency is null ? null : await ScaleOfAsync(tx, company.ReportingCurrency, ct);
            int fallback = await JournalDocumentPoster.ReadFallbackDaysAsync(tx, tenantId, ct);
            string? pivot = await JournalDocumentPoster.ReadPivotAsync(tx, tenantId, ct);
            var used = new List<Guid>(); var warnings = new List<CommandError>(); var rateErrors = new List<CommandError>();
            decimal rateGc = (await JournalDocumentPoster.LookupRateAsync(tx, tenantId, company.LocalCurrency, groupCurrency, req.PostingDate, fallback, pivot, used, warnings, rateErrors, "group", ct)).Value;
            decimal rateRc = company.ReportingCurrency is null ? 0m
                : (await JournalDocumentPoster.LookupRateAsync(tx, tenantId, company.LocalCurrency, company.ReportingCurrency, req.PostingDate, fallback, pivot, used, warnings, rateErrors, "reporting", ct)).Value;
            if (rateErrors.Count > 0) return CommandResult<StockDocumentResult>.Rejected(rateErrors);
            foreach (JournalPair pair in pairs)
            {
                foreach (var (role, sign) in new[] { (pair.DebitRole, 1m), (pair.CreditRole, -1m) })
                {
                    var account = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, role, pair.ItemGroup, pair.Warehouse, null, pair.Item, req.PostingDate, ct);
                    if (account is null) return Reject(Error("INVENTORY.ACCOUNT_DETERMINATION_MISSING", "P5", $"/lines/{pair.LineNo - 1}", ("role_code", role), ("item_id", pair.Item), ("warehouse_id", pair.Warehouse)));
                    decimal lc = sign * pair.Value, gc = Math.Round(lc * rateGc, gcScale, MidpointRounding.AwayFromZero);
                    decimal? rc = company.ReportingCurrency is null ? null : Math.Round(lc * rateRc, rcScale!.Value, MidpointRounding.AwayFromZero);
                    NewLine line = ClosingDocuments.Line(ledger, account.AccountId, "BASE", lc, lc, gc, rc, req.PostingDate);
                    // a GR/IR line is a GL open item without partner and due date (ADR-040)
                    journalLines.Add(role == "GR_IR" ? line with { OpenItem = new OpenItemSpec(null, null, null, null, null, null) } : line);
                }
            }
        }

        // the numbers, the movements and the hash of the stock document
        DateTime at = Now();
        long sequenceNo;
        await using (var cmd = tx.Command("update core.number_range_counter set last_number = last_number + 1, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y returning last_number"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", docSeries.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            sequenceNo = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        string number = Core.Posting.PostingRules.FormatNumber(docSeries.Prefix, docSeries.IncludeFiscalYear, docSeries.Separator, docSeries.NumberLength, fiscalYear, sequenceNo);
        Guid stockId = Guid.NewGuid();
        long chainSeq = stockSeq + 1;
        string stockType = StockValuation.DocumentTypeOf(req.DocumentType);
        string? currencyTc = planned.Any(m => m.ValueTc is not null) ? company.LocalCurrency : null;
        var movementRows = planned.Select(m => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = companyId, ["stock_document_id"] = stockId, ["line_no"] = m.LineNo, ["chain_seq"] = chainSeq, ["movement_type"] = m.MovementType,
            ["cost_source"] = m.CostSource, ["valuation_ref_document_id"] = m.RefDocument, ["valuation_ref_line_no"] = m.RefLine, ["posting_date"] = req.PostingDate, ["item_id"] = m.Item.ItemId,
            ["warehouse_id"] = m.Input.WarehouseId, ["location_id"] = m.Input.LocationId, ["tracking_id"] = null, ["stock_status"] = "AVAILABLE", ["quantity"] = m.Quantity,
            ["unit_code"] = m.UnitCode, ["base_quantity"] = m.BaseQuantity, ["value_lc"] = m.ValueLc, ["unit_cost_lc"] = StockValuation.UnitCost(m.ValueLc, m.BaseQuantity),
            ["value_tc"] = m.ValueTc, ["currency_tc"] = m.ValueTc is null ? null : company.LocalCurrency, ["cost_warehouse_id"] = null, ["cost_qty_after"] = m.CostQtyAfter,
            ["cost_value_after_lc"] = m.CostValueAfter, ["stock_qty_after"] = m.StockQtyAfter, ["lot_expired"] = false, ["count_ref"] = m.CountRef,
            ["expected_book_quantity"] = m.ExpectedBook, ["source_line_ref"] = m.Input.LineRef, ["reversal_of_line_no"] = null
        }).ToList();
        byte[] movementDigest = StockHash2.MovementDigest(movementRows);
        byte[]? detailDigest = options?.DetailDigest is { } provide
            ? await provide(new StockDerivation(stockId, journalId, planned.Select(m => new DerivedMovement(m.LineNo, m.RequestIndex, m.BaseQuantity, m.ValueLc)).ToList()))
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
        }
        await using (var cmd = tx.Command(
            "insert into core.stock_document (tenant_id, company_id, stock_document_id, document_type, number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, document_date, " +
            "posting_date, posted_at, reason_code, journal_document_id, soft_close_override, currency_tc, source_system, source_document_ref, idempotency_key, command_name, posted_by, correlation_id, " +
            "header_text, movement_count, detail_digest, movement_digest, chain_seq, prev_hash, row_hash, hash_version) values (@t, @c, @id, @type, @s, @fy, @seq, @num, @fp, @dd, @pd, @at, @reason, @j, @soft, @tc, " +
            "@src, @ref, @key, @cmdname, @by, @corr, @text, @n, @dd2, @md, @cs, @prev, @row, 2)"))
        {
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
        foreach (Planned m in planned)
        {
            await using var cmd = tx.Command(
                "insert into core.stock_movement (tenant_id, company_id, stock_document_id, line_no, chain_seq, movement_type, cost_source, posting_date, item_id, warehouse_id, location_id, stock_status, " +
                "quantity, unit_code, base_quantity, value_lc, unit_cost_lc, value_tc, currency_tc, cost_qty_after, cost_value_after_lc, stock_qty_after, lot_expired, count_ref, expected_book_quantity, " +
                "source_line_ref, line_text, valuation_ref_document_id, valuation_ref_line_no) values (@t, @c, @d, @n, @cs, @type, @src, @pd, @i, @w, @l, 'AVAILABLE', @q, @u, @bq, @v, @uc, @vt, @ct, @cq, @cv, @sq, false, @cr, @eb, @ref, @text, @rd, @rn)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", stockId); cmd.Parameters.AddWithValue("n", m.LineNo);
            cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("type", m.MovementType); cmd.Parameters.AddWithValue("src", m.CostSource);
            cmd.Parameters.AddWithValue("pd", req.PostingDate); cmd.Parameters.AddWithValue("i", m.Item.ItemId); cmd.Parameters.AddWithValue("w", m.Input.WarehouseId);
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = (object?)m.Input.LocationId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("q", m.Quantity); cmd.Parameters.AddWithValue("u", m.UnitCode); cmd.Parameters.AddWithValue("bq", m.BaseQuantity); cmd.Parameters.AddWithValue("v", m.ValueLc);
            cmd.Parameters.Add(new NpgsqlParameter("uc", NpgsqlDbType.Numeric) { Value = (object?)StockValuation.UnitCost(m.ValueLc, m.BaseQuantity) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("vt", NpgsqlDbType.Numeric) { Value = (object?)m.ValueTc ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ct", NpgsqlDbType.Text) { Value = m.ValueTc is null ? DBNull.Value : company.LocalCurrency });
            cmd.Parameters.AddWithValue("cq", m.CostQtyAfter); cmd.Parameters.AddWithValue("cv", m.CostValueAfter); cmd.Parameters.AddWithValue("sq", m.StockQtyAfter);
            cmd.Parameters.Add(new NpgsqlParameter("cr", NpgsqlDbType.Text) { Value = (object?)m.CountRef ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("eb", NpgsqlDbType.Numeric) { Value = (object?)m.ExpectedBook ?? DBNull.Value });
            cmd.Parameters.AddWithValue("ref", m.Input.LineRef);
            cmd.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = (object?)m.Input.LineText ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rd", NpgsqlDbType.Uuid) { Value = (object?)m.RefDocument ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rn", NpgsqlDbType.Integer) { Value = (object?)m.RefLine ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, StockHash2.ChainType, "", chainSeq, rowHash, StockHash2.Version, ct);
        Guid logId = options?.WriteLog == false ? Guid.Empty : await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, commandName, fingerprint, "STOCK_DOCUMENT", stockId, ct);

        // the events (15, 6), the outbox position last: the journal, the stock document and a balance change per stock key
        if (journal is not null) await ClosingDocuments.WriteDocumentEventAsync(ledger, ctx, journal, "ledger.document_posted", journal.DocumentId, "POSTED", ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "stock.document_posted", "STOCK_DOCUMENT", $"{companyId:D}/{stockId:D}", "POSTED", at, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "STOCK", ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["document_type"] = stockType, ["posting_date"] = req.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["movement_count"] = planned.Count, ["journal_document_id"] = journalId }), ct);
        foreach (var last in planned.GroupBy(m => (m.Item.ItemId, m.Input.WarehouseId, m.Input.LocationId)).Select(g => g.Last()))
            await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "stock.balance_changed", "STOCK_BALANCE", $"{companyId:D}/{last.Item.ItemId:D}/{last.Input.WarehouseId:D}", "CHANGED", at, isAudit: false, null,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["item_id"] = last.Item.ItemId, ["warehouse_id"] = last.Input.WarehouseId, ["location_id"] = last.Input.LocationId, ["quantity_after"] = last.StockQtyAfter }), ct);

        StockDocumentResult result = await LoadResultAsync(tx, tenantId, companyId, stockId, ct);
        result = result with { Movements = result.Movements.Select(m => m with { LineRef = planned[m.LineNo - 1].Input.LineRef }).ToList() };
        if (options?.Commit != false) await tx.CommitAsync(ct);   // the deferred checks: the journal balances, the chain links and the head, the movement count, the outbox position
        return CommandResult<StockDocumentResult>.Ok(result, logId);
    }
}
