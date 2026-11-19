using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// sales-deliveries:post (11, 6.3) and purchase-receipts:post (11, 7.1), first slice: stock and service lines of live orders, items without tracking. One transaction, in the lock order
/// of 11, 9: the order rows, the counters, the LEDGER and STOCK heads (the stock command embeds its stock document and journal), then the FULFILMENT head. The delivery or receipt is a
/// node of the FULFILMENT chain; its row hash is the detail_digest of the stock document, which names it by id (11, 5.5). A line of a goods item is one movement of the derived stock
/// document (ISSUE for a delivery, RECEIPT for a receipt valued at the share of the net amount of the order line); a service line has no movement, and a document of service lines only
/// has no stock document. Progress (delivered or received, the limit with the over tolerance) is computed under the order lock. Built choices are in 07, 13.2w. Not built: tracking,
/// reservations (no CONSUME), the coverage of a receipt by an earlier supplier invoice, dropship, intercompany, reversal, foreign-currency orders, the lock order of the period row.
/// </summary>
public sealed class FulfilmentCommands(NpgsqlDataSource source)
{
    private readonly StockCommands _stock = new(source);

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<FulfilmentResult> Reject(CommandError e) => CommandResult<FulfilmentResult>.Rejected([e]);

    private static DateTime Now() { DateTime n = DateTime.UtcNow; return new DateTime(n.Ticks - n.Ticks % 10, DateTimeKind.Utc); }

    /// <summary>What differs between the two kinds: the tables, the order kind, the stock document type and the column names.</summary>
    private sealed record Kind(
        OrderKind Order, string Permission, string DocumentType, string Header, string Line, string Id, string Number, string Date, string PartyColumn, string OrderIdColumn, string OrderLineColumn,
        string StockType, string Event, string Entity, string SeriesType);

    private static readonly Kind Delivery = new(OrderKind.Sales, FulfilmentRules.DeliverPermission, FulfilmentHash2.Delivery, "sales_delivery", "sales_delivery_line", "delivery_id",
        "delivery_number", "delivery_date", "customer_partner_id", "sales_order_id", "sales_order_line_no", "ISSUE", "delivery.posted", "SALES_DELIVERY", "SALES_DELIVERY");

    private static readonly Kind Receipt = new(OrderKind.Purchase, FulfilmentRules.ReceivePermission, FulfilmentHash2.Receipt, "purchase_receipt", "purchase_receipt_line", "receipt_id",
        "receipt_number", "receipt_date", "supplier_partner_id", "purchase_order_id", "purchase_order_line_no", "RECEIPT", "receipt.posted", "PURCHASE_RECEIPT", "PURCHASE_RECEIPT");

    private sealed record In(string LineRef, Guid OrderId, int OrderLineNo, decimal Quantity, string UnitCode, Guid? WarehouseId, Guid? LocationId);

    public Task<CommandResult<FulfilmentResult>> PostDeliveryAsync(Guid tenantId, CommandContext ctx, PostDeliveryRequest req, CancellationToken ct = default) =>
        PostAsync(Delivery, tenantId, ctx, req.CompanyId, req.DeliveryDate, req.WarehouseId, req.CarrierReference, req.TrackingReference, req.SourceDocumentRef,
            (req.Lines ?? []).Select(l => new In(l.LineRef, l.SalesOrderId, l.SalesOrderLineNo, l.Quantity, l.UnitCode, l.WarehouseId, l.LocationId)).ToList(), FulfilmentRules.Fingerprint(req), ct);

    public Task<CommandResult<FulfilmentResult>> PostReceiptAsync(Guid tenantId, CommandContext ctx, PostReceiptRequest req, CancellationToken ct = default) =>
        PostAsync(Receipt, tenantId, ctx, req.CompanyId, req.ReceiptDate, req.WarehouseId, req.DeliveryNoteReference, null, req.SourceDocumentRef,
            (req.Lines ?? []).Select(l => new In(l.LineRef, l.PurchaseOrderId, l.PurchaseOrderLineNo, l.Quantity, l.UnitCode, l.WarehouseId, l.LocationId)).ToList(), FulfilmentRules.Fingerprint(req), ct);

    private async Task<CommandResult<FulfilmentResult>> PostAsync(
        Kind kind, Guid tenantId, CommandContext ctx, Guid companyId, DateOnly date, Guid? headerWarehouse, string? reference1, string? reference2, string? sourceRef,
        List<In> lines, byte[] fingerprint, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(kind.Permission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", kind.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (companyId == Guid.Empty) Invalid("/company_id", "required");
        if (lines.Count == 0) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/lines"));
        for (int i = 0; i < lines.Count; i++)
        {
            In l = lines[i];
            if (string.IsNullOrWhiteSpace(l.LineRef) || l.LineRef.Length > 100) Invalid($"/lines/{i}/line_ref", "required, at most 100 characters");
            if (l.OrderId == Guid.Empty || l.OrderLineNo < 1) Invalid($"/lines/{i}/order", "an order and a line number");
            if (l.Quantity <= 0m || Math.Round(l.Quantity, 6) != l.Quantity) Invalid($"/lines/{i}/quantity", "more than 0, at most 6 fraction digits");
            if (string.IsNullOrWhiteSpace(l.UnitCode)) Invalid($"/lines/{i}/unit_code", "required");
        }
        if (sourceRef?.Length > 100 || reference1?.Length > 100 || reference2?.Length > 100) Invalid("/source_document_ref", "at most 100 characters");
        if (errors.Count > 0) return CommandResult<FulfilmentResult>.Rejected(errors);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Stock, ct: ct);
                CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
                if (earlier is not null)
                {
                    if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                    return earlier.EntityId is null
                        ? CommandResult<FulfilmentResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                        : CommandResult<FulfilmentResult>.Ok(await LoadAsync(tx, kind, tenantId, companyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
                }
                return await PostCoreAsync(tx, kind, tenantId, ctx, companyId, date, headerWarehouse, reference1, reference2, sourceRef, lines, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<FulfilmentResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<FulfilmentResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private static decimal Num(JsonElement row, string name) => row.GetProperty(name).GetDecimal();

    private static string? Str(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static async Task<decimal> FulfilledAsync(TenantTransaction tx, Kind kind, Guid tenantId, Guid companyId, Guid orderId, int orderLineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            $"select coalesce(sum(base_quantity), 0) from core.{kind.Line} where tenant_id = @t and company_id = @c and {kind.OrderIdColumn.Replace("sales_order_id", "sales_order_id")} = @o and {kind.OrderLineColumn} = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", orderId); cmd.Parameters.AddWithValue("n", orderLineNo);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private sealed record Work(
        int Index, In Input, JsonElement OrderLine, Guid ItemId, string ItemType, decimal BaseQuantity, Guid? Warehouse, bool Stocked, int? StockLine);

    private async Task<CommandResult<FulfilmentResult>> PostCoreAsync(
        TenantTransaction tx, Kind kind, Guid tenantId, CommandContext ctx, Guid companyId, DateOnly date, Guid? headerWarehouse, string? reference1, string? reference2, string? sourceRef,
        List<In> lines, byte[] fingerprint, CancellationToken ct)
    {
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", companyId)));
        if (company.Status != "ACTIVE") return Reject(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company.Status)));
        var errors = new List<CommandError>();

        // position 1 of the lock order: the order rows, sorted. Every check that depends on progress is made after this lock.
        var orderIds = lines.Select(l => l.OrderId).Distinct().Order().ToArray();
        var orders = new Dictionary<Guid, (string Status, Guid Party, string Currency)>();
        await using (var cmd = tx.Command(
            $"select {kind.Order.IdColumn}, lifecycle_status, {kind.Order.PartyColumn}, order_currency from core.{kind.Order.HeaderTable} where tenant_id = @t and company_id = @c and {kind.Order.IdColumn} = any(@ids) " +
            $"order by {kind.Order.IdColumn} for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", orderIds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) orders[r.GetGuid(0)] = (r.GetString(1), r.GetGuid(2), r.GetString(3).Trim());
        }
        for (int i = 0; i < lines.Count; i++)
        {
            if (!orders.TryGetValue(lines[i].OrderId, out var o)) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/lines/{i}/order", ("order_id", lines[i].OrderId)));
            else if (o.Status != "OPEN") errors.Add(Error("ORDER.ORDER_NOT_OPEN", "P2", $"/lines/{i}/order", ("status", o.Status)));
        }
        if (errors.Count > 0) return CommandResult<FulfilmentResult>.Rejected(errors);
        var parties = orders.Values.Select(o => o.Party).Distinct().ToList();
        if (parties.Count != 1) return Reject(Error("SCHEMA.INVALID_VALUE", "P2", "/lines", ("reason", "the orders of one document have one " + (kind.Order.Kind == "SALES" ? "customer" : "supplier"))));
        Guid party = parties[0];
        await using (var cmd = tx.Command("select restriction_status from core.business_partner where tenant_id = @t and partner_id = @p"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", party);
            if (await cmd.ExecuteScalarAsync(ct) as string == "RESTRICTED") return Reject(Error("MASTER.PARTNER_RESTRICTED", "P2", "/lines", ("partner_id", party)));
        }
        if (kind.Order.Kind == "PURCHASE" && orders.Values.Any(o => o.Currency != company.LocalCurrency))
            return Reject(Error("LIMIT.STOCK_FOREIGN_CURRENCY_NOT_BUILT", "P2", "/lines", ("currency", orders.Values.First(o => o.Currency != company.LocalCurrency).Currency)));

        // the lines: the order lines, the base quantities, the progress under the lock and the limit with the over tolerance
        int lcScale = 2;
        await using (var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c"))
        {
            cmd.Parameters.AddWithValue("c", company.LocalCurrency);
            lcScale = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, CultureInfo.InvariantCulture);
        }
        var work = new List<Work>();
        var running = new Dictionary<(Guid, int), decimal>();
        int stockLines = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            In l = lines[i];
            string p = $"/lines/{i}";
            JsonElement? orderLine = null;
            await using (var cmd = tx.Command($"select to_jsonb(x)::text from core.{kind.Order.LineTable} x where tenant_id = @t and company_id = @c and {kind.Order.IdColumn} = @o and line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", l.OrderId); cmd.Parameters.AddWithValue("n", l.OrderLineNo);
                if (await cmd.ExecuteScalarAsync(ct) is string json) orderLine = JsonDocument.Parse(json).RootElement.Clone();
            }
            if (orderLine is not { } ol) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/order", ("line_no", l.OrderLineNo))); continue; }
            if (Str(ol, "line_status") != "OPEN") { errors.Add(Error("ORDER.LINE_NOT_OPEN", "P2", p + "/order", ("line_no", l.OrderLineNo), ("status", Str(ol, "line_status")))); continue; }
            Guid itemId = Guid.Parse(Str(ol, "item_id")!);
            string itemType = "", baseUnit = ""; int scale = 0;
            await using (var cmd = tx.Command("select item_type, base_unit, quantity_scale from core.item where tenant_id = @t and item_id = @i"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", itemId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                itemType = r.GetString(0); baseUnit = r.GetString(1).Trim(); scale = r.GetInt16(2);
            }
            decimal? factor = await OrderCommands.FactorAsync(tx, tenantId, itemId, l.UnitCode, baseUnit, ct);
            if (factor is null) { errors.Add(Error("INVENTORY.UNIT_CONVERSION_MISSING", "P2", p + "/unit_code", ("item_id", itemId), ("unit_code", l.UnitCode))); continue; }
            decimal baseQty = l.Quantity * factor.Value;
            if (Math.Round(baseQty, scale) != baseQty) { errors.Add(Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", p + "/quantity", ("base_quantity", baseQty), ("quantity_scale", scale))); continue; }
            var key = (l.OrderId, l.OrderLineNo);
            decimal done = await FulfilledAsync(tx, kind, tenantId, companyId, l.OrderId, l.OrderLineNo, ct) + running.GetValueOrDefault(key);
            decimal limit = FulfilmentRules.Limit(Num(ol, "base_quantity"), Num(ol, "over_tolerance_pct"), done);
            if (baseQty > limit) { errors.Add(Error("ORDER.OVER_DELIVERY", "P4", p + "/quantity", ("base_quantity", baseQty), ("limit", limit), ("fulfilled", done))); continue; }
            running[key] = running.GetValueOrDefault(key) + baseQty;
            bool stocked = kind.Order.Kind == "SALES" ? Str(ol, "fulfilment_type") == "STOCK" : itemType == "GOODS";
            Guid? warehouse = l.WarehouseId ?? headerWarehouse ?? (Guid.TryParse(Str(ol, "warehouse_id"), out Guid w) ? w : null);
            if (stocked && warehouse is null) { errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P2", p + "/warehouse_id")); continue; }
            work.Add(new Work(i, l, ol, itemId, itemType, baseQty, stocked ? warehouse : (l.WarehouseId ?? headerWarehouse), stocked, stocked ? ++stockLines : null));
        }
        if (errors.Count > 0) return CommandResult<FulfilmentResult>.Rejected(errors);

        // the number of the document: the series of its type at the date, in the fiscal year of the date (04)
        int fiscalYear;
        await using (var cmd = tx.Command("select fiscal_year from core.fiscal_year where tenant_id = @t and company_id = @c and start_date <= @d and end_date >= @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", date);
            if (await cmd.ExecuteScalarAsync(ct) is not int year) return Reject(Error("SYSTEM.FISCAL_YEAR_MISSING", "P2", "/date", ("date", date)));
            fiscalYear = year;
        }
        Guid ledger = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
        var series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, kind.SeriesType, date, ledger, ct);
        if (series is null) return Reject(Error("SERIES.NO_ASSIGNMENT", "P2", "/date", ("document_type", kind.SeriesType), ("posting_date", date)));
        long? last = await ClearingPoster.LockCounterAsync(tx, tenantId, companyId, series.SeriesId, fiscalYear, ct);
        if (last is null) return Reject(Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "/date", ("series_code", series.SeriesCode), ("fiscal_year", fiscalYear)));
        long sequenceNo = last.Value + 1;
        await using (var cmd = tx.Command("update core.number_range_counter set last_number = @n, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        string number = PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
        Guid documentId = Guid.NewGuid();
        DateTime at = Now();

        // the node of the chain, built when the movements are derived: the stock document binds its row hash
        long chainSeq = 0; byte[] prev = [], rowHash = [];
        Dictionary<string, object?>? body = null;
        List<Dictionary<string, object?>> lineRows = [];
        byte[] lineDigest = [];
        Guid? stockId = null, journalId = null;
        Dictionary<int, DerivedMovement> byRequest = [];

        async Task<byte[]?> BuildNodeAsync(StockDerivation? derivation)
        {
            (long seq, byte[] hash, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, FulfilmentHash2.ChainType, "",
                LedgerHash2.Genesis(tenantId, companyId, FulfilmentHash2.ChainType, ""), ct);
            chainSeq = seq + 1; prev = hash;
            stockId = derivation?.StockDocumentId; journalId = derivation?.JournalDocumentId;
            if (derivation is not null) byRequest = derivation.Movements.ToDictionary(m => m.RequestIndex);
            lineRows = [];
            foreach (Work w in work)
            {
                DerivedMovement? m = w.StockLine is null ? null : byRequest[w.StockLine.Value - 1];
                decimal? value = m is null ? null : kind.Order.Kind == "SALES" ? -m.ValueLc : m.ValueLc;
                decimal? unitPrice = null; string? currency = null;
                if (kind.Order.Kind == "PURCHASE")
                {
                    unitPrice = Math.Round(Num(w.OrderLine, "unit_price"), 4);
                    currency = company.LocalCurrency;
                }
                lineRows.Add(new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = companyId, ["document_id"] = documentId, ["line_no"] = lineRows.Count + 1, ["order_id"] = w.Input.OrderId,
                    ["order_line_no"] = w.Input.OrderLineNo, ["item_id"] = w.ItemId, ["quantity"] = w.Input.Quantity, ["unit_code"] = w.Input.UnitCode, ["base_quantity"] = w.BaseQuantity,
                    ["warehouse_id"] = w.Warehouse, ["location_id"] = w.Input.LocationId, ["tracking_id"] = null, ["stock_status"] = "AVAILABLE",
                    ["stock_document_line_no"] = m?.LineNo, ["stock_document_line_count"] = m is null ? 0 : 1, ["unit_price"] = unitPrice, ["currency"] = currency, ["value_lc"] = value
                });
            }
            lineDigest = FulfilmentHash2.LineDigest(lineRows);
            body = new Dictionary<string, object?>
            {
                ["document_id"] = documentId, ["document_type"] = kind.DocumentType, ["number_series_id"] = series.SeriesId, ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo,
                ["document_number"] = number, ["document_date"] = date, ["partner_id"] = party, ["warehouse_id"] = headerWarehouse, ["reference_1"] = reference1, ["reference_2"] = reference2,
                ["stock_document_id"] = stockId, ["reversal_of_id"] = null, ["reason_code"] = null, ["origin"] = "LIVE", ["migration_batch_id"] = null, ["source_system"] = ctx.SourceSystem,
                ["source_document_ref"] = sourceRef, ["posted_at"] = at, ["idempotency_key"] = ctx.IdempotencyKey,
                ["command_name"] = kind.Order.Resource == "sales-orders" ? "sales-deliveries:post" : "purchase-receipts:post", ["posted_by"] = ctx.Actor,
                ["correlation_id"] = ctx.CorrelationId, ["line_count"] = lineRows.Count, ["line_digest"] = lineDigest, ["hash_version"] = FulfilmentHash2.Version
            };
            rowHash = FulfilmentHash2.RowHash(tenantId, companyId, chainSeq, prev, body);
            return rowHash;
        }

        StockDocumentResult? stockResult = null;
        if (stockLines > 0)
        {
            var stockInputs = work.Where(w => w.Stocked).Select(w => new StockLineInput(
                w.Input.LineRef, w.ItemId, w.Warehouse!.Value, w.Input.Quantity, w.Input.UnitCode, w.Input.LocationId)).ToList();
            var stockRequest = new PostStockDocumentRequest(companyId, kind.StockType, date, date, stockInputs, null, null, number);
            Dictionary<int, decimal>? values = null;
            if (kind.Order.Kind == "PURCHASE")
            {
                values = [];
                int k = 0;
                foreach (Work w in work.Where(w => w.Stocked))
                    values[k++] = FulfilmentRules.ReceiptValue(Num(w.OrderLine, "net_amount"), Num(w.OrderLine, "base_quantity"), w.BaseQuantity, lcScale);
            }
            var options = new StockPostOptions(kind.Order.Kind == "SALES" ? "sales-deliveries:post" : "purchase-receipts:post", WriteLog: false, Commit: false, BuildNodeAsync, values);
            var posted = await _stock.PostCoreAsync(tx, tenantId, ctx, stockRequest, fingerprint, ct, options);
            if (posted.Status != CommandStatus.Ok)
            {
                // the stock request has the stock lines only: its paths name those lines, the request of this command names all of them
                var stockIndex = work.Where(w => w.Stocked).Select(w => w.Index).ToArray();
                return CommandResult<FulfilmentResult>.Rejected(posted.Errors.Select(e => e with
                {
                    Path = Regex.Replace(e.Path ?? "", @"^/lines/(\d+)", m => $"/lines/{stockIndex[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]}")
                }).ToList());
            }
            stockResult = posted.Value;
        }
        else await BuildNodeAsync(null);

        // the document, its lines and the chain
        await using (var cmd = tx.Command(
            $"insert into core.{kind.Header} (tenant_id, company_id, {kind.Id}, {kind.Number}, number_series_id, fiscal_year, sequence_no, {kind.Date}, {kind.PartyColumn}, warehouse_id, " +
            $"{(kind.Order.Kind == "SALES" ? "carrier_reference, tracking_reference" : "delivery_note_reference")}, stock_document_id, source_system, source_document_ref, posted_at, posted_by, correlation_id, " +
            "idempotency_key, line_count, chain_seq, prev_hash, row_hash, line_digest, hash_version) values (@t, @c, @id, @num, @s, @fy, @seq, @d, @party, @wh, @r1, " +
            (kind.Order.Kind == "SALES" ? "@r2, " : "") + "@stock, @src, @ref, @at, @by, @corr, @key, @n, @cs, @prev, @row, @ld, 2)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", documentId); cmd.Parameters.AddWithValue("num", number);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("fy", fiscalYear); cmd.Parameters.AddWithValue("seq", sequenceNo); cmd.Parameters.AddWithValue("d", date);
            cmd.Parameters.AddWithValue("party", party); cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey); cmd.Parameters.AddWithValue("n", lineRows.Count);
            cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("prev", prev); cmd.Parameters.AddWithValue("row", rowHash); cmd.Parameters.AddWithValue("ld", lineDigest);
            cmd.Parameters.Add(new NpgsqlParameter("wh", NpgsqlDbType.Uuid) { Value = (object?)headerWarehouse ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("r1", NpgsqlDbType.Text) { Value = (object?)reference1 ?? DBNull.Value });
            if (kind.Order.Kind == "SALES") cmd.Parameters.Add(new NpgsqlParameter("r2", NpgsqlDbType.Text) { Value = (object?)reference2 ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("stock", NpgsqlDbType.Uuid) { Value = (object?)stockId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)sourceRef ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var row in lineRows)
        {
            bool purchase = kind.Order.Kind == "PURCHASE";
            await using var cmd = tx.Command(
                $"insert into core.{kind.Line} (tenant_id, company_id, {kind.Id}, line_no, {kind.OrderIdColumn}, {kind.OrderLineColumn}, item_id, quantity, unit_code, base_quantity, warehouse_id, location_id, " +
                "stock_status, stock_document_line_no, value_lc" + (purchase ? ", unit_price, currency, stock_document_line_count" : "") + ") values (@t, @c, @id, @n, @o, @ol, @i, @q, @u, @bq, @w, @l, 'AVAILABLE', @sl, @v" +
                (purchase ? ", @up, @cur, @slc" : "") + ")");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", documentId); cmd.Parameters.AddWithValue("n", row["line_no"]!);
            cmd.Parameters.AddWithValue("o", row["order_id"]!); cmd.Parameters.AddWithValue("ol", row["order_line_no"]!); cmd.Parameters.AddWithValue("i", row["item_id"]!);
            cmd.Parameters.AddWithValue("q", row["quantity"]!); cmd.Parameters.AddWithValue("u", row["unit_code"]!); cmd.Parameters.AddWithValue("bq", row["base_quantity"]!);
            cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Uuid) { Value = row["warehouse_id"] ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = row["location_id"] ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("sl", NpgsqlDbType.Integer) { Value = row["stock_document_line_no"] ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Numeric) { Value = row["value_lc"] ?? DBNull.Value });
            if (purchase)
            {
                cmd.Parameters.Add(new NpgsqlParameter("up", NpgsqlDbType.Numeric) { Value = row["unit_price"] ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("cur", NpgsqlDbType.Text) { Value = row["currency"] ?? DBNull.Value });
                cmd.Parameters.AddWithValue("slc", row["stock_document_line_count"]!);
            }
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, FulfilmentHash2.ChainType, "", chainSeq, rowHash, FulfilmentHash2.Version, ct);
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, (string)body!["command_name"]!, fingerprint, kind.Entity, documentId, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, kind.Event, kind.Entity, $"{companyId:D}/{documentId:D}", "POSTED", at, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "FULFILMENT", ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["number"] = number, ["partner_id"] = party, ["lines"] = lineRows.Count, ["stock_document_id"] = stockId }), ct);

        FulfilmentResult result = await LoadAsync(tx, kind, tenantId, companyId, documentId, ct);
        result = result with
        {
            JournalDocumentId = journalId,
            Lines = result.Lines.Select(l => l with { LineRef = work[l.LineNo - 1].Input.LineRef }).ToList()
        };
        await tx.CommitAsync(ct);   // the deferred checks: the chains, the movement and line counts, the journal, the outbox position
        return CommandResult<FulfilmentResult>.Ok(result, logId);
    }

    private static async Task<FulfilmentResult> LoadAsync(TenantTransaction tx, Kind kind, Guid tenantId, Guid companyId, Guid id, CancellationToken ct)
    {
        string number; long seq; byte[] hash; Guid? stock, journal = null;
        await using (var cmd = tx.Command($"select {kind.Number}, chain_seq, row_hash, stock_document_id from core.{kind.Header} where tenant_id = @t and company_id = @c and {kind.Id} = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            number = r.GetString(0); seq = r.GetInt64(1); hash = (byte[])r[2]; stock = r.IsDBNull(3) ? null : r.GetGuid(3);
        }
        if (stock is { } sid)
            await using (var cmd = tx.Command("select journal_document_id from core.stock_document where tenant_id = @t and company_id = @c and stock_document_id = @s"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", sid);
                journal = await cmd.ExecuteScalarAsync(ct) as Guid?;
            }
        var lines = new List<FulfilmentLineResult>();
        await using (var cmd = tx.Command(
            $"select l.line_no, l.{kind.OrderIdColumn}, l.{kind.OrderLineColumn}, l.base_quantity, l.value_lc, l.stock_document_line_no from core.{kind.Line} l " +
            $"where l.tenant_id = @t and l.company_id = @c and l.{kind.Id} = @i order by l.line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            var raw = new List<(int, Guid, int, decimal, decimal?, int?)>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) raw.Add((r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetInt32(5)));
            foreach (var (no, order, orderLine, bq, value, stockLine) in raw)
            {
                decimal done = await FulfilledAsync(tx, kind, tenantId, companyId, order, orderLine, ct), ordered;
                bool open;
                await using (var oc = tx.Command($"select base_quantity, line_status = 'OPEN' from core.{kind.Order.LineTable} where tenant_id = @t and company_id = @c and {kind.Order.IdColumn} = @o and line_no = @n"))
                {
                    oc.Parameters.AddWithValue("t", tenantId); oc.Parameters.AddWithValue("c", companyId); oc.Parameters.AddWithValue("o", order); oc.Parameters.AddWithValue("n", orderLine);
                    await using var or = await oc.ExecuteReaderAsync(ct);
                    await or.ReadAsync(ct);
                    ordered = or.GetDecimal(0); open = or.GetBoolean(1);
                }
                lines.Add(new FulfilmentLineResult(no, "", order, orderLine, bq, value, stockLine, done, FulfilmentRules.Open(ordered, done, open)));
            }
        }
        return new FulfilmentResult(id, number, seq, Canonical.FormatHex32(hash), stock, journal, lines);
    }
}
