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
public sealed partial class FulfilmentCommands(NpgsqlDataSource source)
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
            catch (CoverRejection ex)
            {
                return Reject(ex.Error);
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

    /// <summary>
    /// The invoiced goods that are not received yet and that this receipt covers (11, 7.1): per receipt line the invoice lines of its order line that are waiting (TWO_WAY, a GR/IR debit with no receipt),
    /// oldest first, each with what is left of its quantity and of its value. The covered value of a piece is its share of the invoice value; the piece that takes the rest of a line takes the rest of its value.
    /// </summary>
    private static async Task<Dictionary<int, List<CoverPiece>>> PlanCoverageAsync(TenantTransaction tx, Guid tenantId, Guid companyId, List<Work> work, int lcScale, CancellationToken ct)
    {
        var result = new Dictionary<int, List<CoverPiece>>();
        var rowsOf = new Dictionary<(Guid, int), List<PendingInvoiceRow>>();
        foreach (Work w in work.Where(x => x.Stocked))
        {
            var key = (w.Input.OrderId, w.Input.OrderLineNo);
            if (!rowsOf.TryGetValue(key, out var rows))
            {
                rows = [];
                await using var cmd = tx.Command(
                    "select l.purchase_invoice_id, l.line_no, h.journal_document_id, l.journal_line_no, l.base_quantity, l.unreceived_gr_ir_lc, " +
                    "coalesce((select sum(c.covered_quantity) from core.purchase_invoice_coverage c where c.tenant_id = l.tenant_id and c.company_id = l.company_id and c.purchase_invoice_id = l.purchase_invoice_id and c.invoice_line_no = l.line_no), 0), " +
                    "coalesce((select sum(c.covered_value_lc) from core.purchase_invoice_coverage c where c.tenant_id = l.tenant_id and c.company_id = l.company_id and c.purchase_invoice_id = l.purchase_invoice_id and c.invoice_line_no = l.line_no), 0) " +
                    "from core.purchase_invoice_line l join core.purchase_invoice h on h.tenant_id = l.tenant_id and h.company_id = l.company_id and h.purchase_invoice_id = l.purchase_invoice_id " +
                    "where l.tenant_id = @t and l.company_id = @c and l.purchase_order_id = @o and l.purchase_order_line_no = @n and h.invoice_kind = 'INVOICE' and l.unreceived_gr_ir_lc > 0 " +
                    "and not exists (select 1 from core.purchase_invoice m where m.tenant_id = h.tenant_id and m.company_id = h.company_id and m.reversal_of_purchase_invoice_id = h.purchase_invoice_id) " +
                    "order by h.created_at, l.purchase_invoice_id, l.line_no");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", key.Item1); cmd.Parameters.AddWithValue("n", key.Item2);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    rows.Add(new PendingInvoiceRow(r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), r.GetInt32(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(4) - r.GetDecimal(6), r.GetDecimal(5) - r.GetDecimal(7)));
                rowsOf[key] = rows;
            }
            decimal need = w.BaseQuantity;
            var pieces = new List<CoverPiece>();
            foreach (PendingInvoiceRow row in rows)
            {
                if (need <= 0m) break;
                if (row.QtyLeft <= 0m) continue;
                decimal take = Math.Min(need, row.QtyLeft);
                decimal value = take == row.QtyLeft ? row.ValueLeft : Math.Min(row.ValueLeft, Math.Round(row.Value * take / row.Qty, lcScale, MidpointRounding.AwayFromZero));
                row.QtyLeft -= take; row.ValueLeft -= value; need -= take;
                pieces.Add(new CoverPiece(row.InvoiceId, row.LineNo, row.Journal, row.JournalLine, take, value));
            }
            if (pieces.Count > 0) result[w.Index] = pieces;
        }
        return result;
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

    /// <summary>The part of an invoice line that a receipt line covers (11, 7.1): the invoice line, its GR/IR item, the quantity and the value of the invoice.</summary>
    private sealed record CoverPiece(Guid InvoiceId, int InvoiceLineNo, Guid InvoiceJournal, int JournalLineNo, decimal Qty, decimal Value);

    private sealed record CoverageRow(Guid InvoiceId, int InvoiceLineNo, int WorkIndex, decimal Qty, decimal Value, Guid ClearingId);

    private sealed class PendingInvoiceRow(Guid invoiceId, int lineNo, Guid journal, int journalLine, decimal qty, decimal value, decimal qtyLeft, decimal valueLeft)
    {
        public Guid InvoiceId { get; } = invoiceId; public int LineNo { get; } = lineNo; public Guid Journal { get; } = journal; public int JournalLine { get; } = journalLine;
        public decimal Qty { get; } = qty; public decimal Value { get; } = value; public decimal QtyLeft { get; set; } = qtyLeft; public decimal ValueLeft { get; set; } = valueLeft;
    }

    private sealed class CoverRejection(CommandError error) : Exception("cover rejected")
    {
        public CommandError Error { get; } = error;
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
            if ((kind.Order.Kind == "SALES" && Str(ol, "fulfilment_type") == "DROPSHIP") || (kind.Order.Kind == "PURCHASE" && Str(ol, "dropship_sales_order_id") is not null))
            { errors.Add(Error("SCHEMA.INVALID_VALUE", "P2", p + "/order", ("reason", "a dropship line is confirmed with dropship-confirmations:post (11, 6.6)"), ("line_no", l.OrderLineNo))); continue; }
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
        var stockMap = new Dictionary<int, (int First, int Count)>();   // work index to its first request line and the number of request lines (2 when a receipt line is split)
        var covers = new Dictionary<int, List<CoverPiece>>();
        var pendingCoverage = new List<CoverageRow>();

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
                (int First, int Count) sm = stockMap.TryGetValue(w.Index, out var mapped) ? mapped : (-1, 0);
                DerivedMovement? m = sm.Count == 0 ? null : byRequest[sm.First];
                decimal? value = m is null ? null : Enumerable.Range(sm.First, sm.Count).Sum(x => kind.Order.Kind == "SALES" ? -byRequest[x].ValueLc : byRequest[x].ValueLc);
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
                    ["stock_document_line_no"] = m?.LineNo, ["stock_document_line_count"] = sm.Count, ["unit_price"] = unitPrice, ["currency"] = currency, ["value_lc"] = value
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
            var stockInputs = new List<StockLineInput>();
            var values = new Dictionary<int, decimal>();
            var sources = new Dictionary<int, string>();
            var requestWork = new List<int>();   // the work of every request line of the stock document
            bool purchase = kind.Order.Kind == "PURCHASE";
            covers = purchase ? await PlanCoverageAsync(tx, tenantId, companyId, work, lcScale, ct) : [];
            foreach (Work w in work.Where(w => w.Stocked))
            {
                int first = stockInputs.Count;
                decimal orderedNet = purchase ? Num(w.OrderLine, "net_amount") : 0m, orderedBase = purchase ? Num(w.OrderLine, "base_quantity") : 0m;
                if (!covers.TryGetValue(w.Index, out var pieces))
                {
                    stockInputs.Add(new StockLineInput(w.Input.LineRef, w.ItemId, w.Warehouse!.Value, w.Input.Quantity, w.Input.UnitCode, w.Input.LocationId,
                        ReservationId: purchase ? null : await OrderCommands.ActiveReservationOfLineAsync(tx, tenantId, companyId, w.Input.OrderId, w.Input.OrderLineNo, w.Warehouse!.Value, ct)));
                    requestWork.Add(w.Index);
                    if (purchase) values[first] = FulfilmentRules.ReceiptValue(orderedNet, orderedBase, w.BaseQuantity, lcScale);
                    stockMap[w.Index] = (first, 1);
                    continue;
                }
                // a receipt line that an earlier invoice covers is split: the covered units at the invoice value (source INVOICE), the rest at the order price (11, 7.1, 10, 5.6.1)
                decimal coveredBase = pieces.Sum(x => x.Qty), coveredValue = pieces.Sum(x => x.Value), factor = w.BaseQuantity / w.Input.Quantity;
                decimal coveredQty = coveredBase / factor, restQty = w.Input.Quantity - coveredQty;
                if (Math.Round(coveredQty, 6) != coveredQty || coveredQty * factor != coveredBase || Math.Round(restQty, 6) != restQty)
                    throw new CoverRejection(Error("LIMIT.RECEIPT_SPLIT_UNIT_NOT_BUILT", "P4", $"/lines/{w.Index}/quantity", ("unit_code", w.Input.UnitCode)));
                stockInputs.Add(new StockLineInput(w.Input.LineRef, w.ItemId, w.Warehouse!.Value, coveredQty, w.Input.UnitCode, w.Input.LocationId));
                requestWork.Add(w.Index); values[first] = coveredValue; sources[first] = "INVOICE";
                int count = 1;
                if (restQty > 0m)
                {
                    stockInputs.Add(new StockLineInput(w.Input.LineRef + "#R", w.ItemId, w.Warehouse!.Value, restQty, w.Input.UnitCode, w.Input.LocationId));
                    requestWork.Add(w.Index);
                    values[first + 1] = FulfilmentRules.ReceiptValue(orderedNet, orderedBase, w.BaseQuantity - coveredBase, lcScale);
                    count = 2;
                }
                stockMap[w.Index] = (first, count);
            }
            var stockRequest = new PostStockDocumentRequest(companyId, kind.StockType, date, date, stockInputs, null, null, number);

            // once the journal is posted: the GR/IR credit of the covered units is cleared against the GR/IR debit of the invoices, in the same transaction (11, 7.1)
            async Task CoverAsync(TenantTransaction t, PostedDocument journalDocument, IReadOnlyDictionary<int, int> creditLineOf)
            {
                var byAccount = new Dictionary<Guid, List<(int WorkIndex, PostedLine Credit, List<CoverPiece> Pieces)>>();
                foreach (var (workIndex, pieces) in covers)
                {
                    PostedLine credit = journalDocument.Lines.Single(l => l.LineNo == creditLineOf[stockMap[workIndex].First]);
                    if (!byAccount.TryGetValue(credit.AccountId, out var list)) byAccount[credit.AccountId] = list = [];
                    list.Add((workIndex, credit, pieces));
                }
                (long headSeq, byte[] headHash, _) = await CommandSupport.LockOrCreateHeadAsync(t, tenantId, companyId, ClearingHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, ClearingHash2.ChainType, ""), ct);
                DateTime clearedAt = Now();
                int number2 = 0;
                foreach (var (account, entries) in byAccount.OrderBy(x => x.Key))
                {
                    number2++;
                    var keys = entries.SelectMany(e => e.Pieces).Select(p => (Doc: p.InvoiceJournal, Line: p.JournalLineNo)).Distinct().OrderBy(k => k.Doc).ThenBy(k => k.Line).ToList();
                    var items = new Dictionary<(Guid, int), (Guid Account, decimal Tc, decimal Lc, decimal Gc, decimal? Rc, decimal CTc, decimal CLc, decimal CGc, decimal? CRc)>();
                    foreach (var (doc, line) in keys)
                    {
                        await using var cmd = t.Command(
                            "select l.account_id, l.amount_tc, l.amount_lc, l.amount_gc, l.amount_rc, oi.cleared_amount_tc, oi.cleared_amount_lc, oi.cleared_amount_gc, oi.cleared_amount_rc from core.journal_line_open_item oi " +
                            "join core.journal_line l on l.tenant_id = oi.tenant_id and l.company_id = oi.company_id and l.document_id = oi.document_id and l.line_no = oi.line_no " +
                            "where oi.tenant_id = @t and oi.company_id = @c and oi.document_id = @d and oi.line_no = @n for update of oi");
                        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", doc); cmd.Parameters.AddWithValue("n", line);
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        if (!await r.ReadAsync(ct)) throw new CoverRejection(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
                        items[(doc, line)] = (r.GetGuid(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.IsDBNull(8) ? null : r.GetDecimal(8));
                    }
                    Guid clearingId = Guid.NewGuid();
                    var clearingItems = new List<(Guid Doc, int Line, string Currency, decimal Tc, decimal Lc, decimal Gc, decimal? Rc)>();
                    var adds = new Dictionary<(Guid, int), (decimal Tc, decimal Lc, decimal Gc, decimal? Rc)>();
                    foreach (var (workIndex, credit, pieces) in entries.OrderBy(e => e.WorkIndex))
                    {
                        decimal coveredValue = pieces.Sum(p => p.Value);
                        if (credit.AmountTc != -coveredValue || credit.AmountLc != -coveredValue) throw new CoverRejection(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
                        clearingItems.Add((journalDocument.DocumentId, credit.LineNo, credit.CurrencyLc, credit.AmountTc, credit.AmountLc, credit.AmountGc, credit.AmountRc));
                        decimal gcLeft = -credit.AmountGc; decimal? rcLeft = credit.AmountRc is null ? null : -credit.AmountRc;
                        for (int k = 0; k < pieces.Count; k++)
                        {
                            CoverPiece p = pieces[k];
                            var item = items[(p.InvoiceJournal, p.JournalLineNo)];
                            if (item.Account != account) throw new CoverRejection(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
                            bool lastPiece = k == pieces.Count - 1;
                            decimal gc = lastPiece ? gcLeft : Math.Round(-credit.AmountGc * p.Value / coveredValue, 4, MidpointRounding.AwayFromZero);
                            gcLeft -= gc;
                            decimal? rc = rcLeft is null ? null : lastPiece ? rcLeft : Math.Round(-credit.AmountRc!.Value * p.Value / coveredValue, 4, MidpointRounding.AwayFromZero);
                            rcLeft = rcLeft is null ? null : rcLeft - rc;
                            clearingItems.Add((p.InvoiceJournal, p.JournalLineNo, credit.CurrencyLc, p.Value, p.Value, gc, rc));
                            var cur = adds.GetValueOrDefault((p.InvoiceJournal, p.JournalLineNo));
                            adds[(p.InvoiceJournal, p.JournalLineNo)] = (cur.Tc + p.Value, cur.Lc + p.Value, cur.Gc + gc, rc is null && cur.Rc is null ? null : (cur.Rc ?? 0m) + (rc ?? 0m));
                            pendingCoverage.Add(new CoverageRow(p.InvoiceId, p.InvoiceLineNo, workIndex, p.Qty, p.Value, clearingId));
                        }
                    }
                    foreach (var (key, add) in adds)
                        if (items[key].Lc - items[key].CLc < add.Lc) throw new CoverRejection(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
                    long seq = headSeq + 1;
                    string baseKey = ctx.IdempotencyKey.Length > 90 ? ctx.IdempotencyKey[..90] : ctx.IdempotencyKey;
                    CommandContext cctx = ctx with { IdempotencyKey = $"{baseKey}#K{number2}" };
                    string matchingRef = documentId.ToString("D");
                    var body2 = new Dictionary<string, object?>
                    {
                        ["clearing_id"] = clearingId, ["clearing_type"] = "CLEAR", ["clearing_date"] = date, ["fiscal_year"] = journalDocument.FiscalYear, ["fiscal_period"] = journalDocument.FiscalPeriod, ["cleared_at"] = clearedAt,
                        ["account_id"] = account, ["partner_id"] = null, ["document_id"] = null, ["reset_of_clearing_id"] = null, ["clearing_method"] = "AUTO_MATCH", ["matching_ref"] = matchingRef,
                        ["source_system"] = ctx.SourceSystem, ["idempotency_key"] = cctx.IdempotencyKey, ["command_name"] = "purchase-receipts:post", ["cleared_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId
                    };
                    var itemRows = clearingItems.Select((i, n) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    {
                        ["tenant_id"] = tenantId, ["company_id"] = companyId, ["clearing_id"] = clearingId, ["item_no"] = n + 1, ["clearing_date"] = date, ["open_item_document_id"] = i.Doc, ["open_item_line_no"] = i.Line,
                        ["currency_tc"] = i.Currency, ["cleared_amount_tc"] = i.Tc, ["cleared_amount_lc"] = i.Lc, ["cleared_amount_gc"] = i.Gc, ["cleared_amount_rc"] = i.Rc
                    }).ToList();
                    byte[] rowHash2 = ClearingHash2.RowHash(tenantId, companyId, seq, headHash, body2, ClearingHash2.ItemsDigest(itemRows), null);
                    var synthetic = new PostClearingRequest(companyId, account, null, date, "AUTO_MATCH", matchingRef, null, []);
                    await ClearingPoster.InsertClearingAsync(t, tenantId, synthetic, cctx, clearingId, null, seq, journalDocument.FiscalYear, journalDocument.FiscalPeriod, clearedAt, headHash, rowHash2, "CLEAR", null, "purchase-receipts:post", ct);
                    for (int n = 0; n < clearingItems.Count; n++)
                        await ClearingPoster.InsertItemAsync(t, tenantId, synthetic, clearingId, n + 1, clearingItems[n].Doc, clearingItems[n].Line, clearingItems[n].Currency, clearingItems[n].Tc, clearingItems[n].Lc, clearingItems[n].Gc, clearingItems[n].Rc, ct);
                    foreach (var (_, credit, _) in entries)
                        await ClearingPoster.UpdateOpenItemAsync(t, tenantId, companyId, journalDocument.DocumentId, credit.LineNo, clearingId, "CLEARED", credit.AmountTc, credit.AmountLc, credit.AmountGc, credit.AmountRc, ct);
                    foreach (var (key, add) in adds)
                    {
                        var item = items[key];
                        decimal tc = item.CTc + add.Tc, lc = item.CLc + add.Lc, gc2 = item.CGc + add.Gc; decimal? rc2 = item.CRc is null && add.Rc is null ? null : (item.CRc ?? 0m) + (add.Rc ?? 0m);
                        await ClearingPoster.UpdateOpenItemAsync(t, tenantId, companyId, key.Item1, key.Item2, clearingId, tc == item.Tc && lc == item.Lc ? "CLEARED" : "PARTIAL", tc, lc, gc2, rc2, ct);
                    }
                    await ClearingPoster.MoveHeadAsync(t, tenantId, companyId, ClearingHash2.ChainType, "", seq, rowHash2, ClearingHash2.Version, ct);
                    await CommandSupport.WriteOutboxAsync(t, tenantId, companyId, ctx, "clearing.posted", "CLEARING", $"{companyId:D}/{clearingId:D}", "POSTED", clearedAt, isAudit: true,
                        JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "CLEARING", ["scope"] = "", ["chain_seq"] = seq, ["row_hash"] = Canonical.FormatHex32(rowHash2) }),
                        JsonSerializer.Serialize(new Dictionary<string, object?> { ["clearing_id"] = clearingId, ["partner_id"] = null, ["account_id"] = account, ["items"] = clearingItems.Count }), ct);
                    headSeq = seq; headHash = rowHash2;
                }
            }
            var options = new StockPostOptions(kind.Order.Kind == "SALES" ? "sales-deliveries:post" : "purchase-receipts:post", WriteLog: false, Commit: false, BuildNodeAsync, purchase ? values : null,
                LineSources: sources.Count > 0 ? sources : null, AfterJournal: covers.Count > 0 ? CoverAsync : null);
            var posted = await _stock.PostCoreAsync(tx, tenantId, ctx, stockRequest, fingerprint, ct, options);
            if (posted.Status != CommandStatus.Ok)
            {
                // the stock request has the stock lines only: its paths name those lines, the request of this command names all of them
                var stockIndex = requestWork.ToArray();
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
        foreach (CoverageRow c in pendingCoverage)
        {
            await using var cmd = tx.Command(
                "insert into core.purchase_invoice_coverage (tenant_id, company_id, purchase_invoice_id, invoice_line_no, receipt_id, receipt_line_no, covered_quantity, covered_value_lc, clearing_id, created_by, correlation_id) " +
                "values (@t, @c, @i, @il, @r, @rl, @q, @v, @cl, @by, @corr)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", c.InvoiceId); cmd.Parameters.AddWithValue("il", c.InvoiceLineNo);
            cmd.Parameters.AddWithValue("r", documentId); cmd.Parameters.AddWithValue("rl", work.FindIndex(x => x.Index == c.WorkIndex) + 1); cmd.Parameters.AddWithValue("q", c.Qty);
            cmd.Parameters.AddWithValue("v", c.Value); cmd.Parameters.AddWithValue("cl", c.ClearingId); cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
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
