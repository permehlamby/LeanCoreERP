using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// sales-returns:post and purchase-returns:post (11, 5.4), first slice: goods lines of live deliveries and receipts, items without tracking, disposition AVAILABLE. One transaction in the
/// lock order of 11, 9 as the fulfilment commands. A return is a node of the FULFILMENT chain; its row hash is the detail_digest of the derived STOCK_RETURN document. The stock comes back
/// (RETURN_IN) or leaves (RETURN_OUT) at the value of the original movement in proportion to the returned quantity, not at the current average, and the journal is the one of 10, 7:
/// inventory against COGS for a sales return, GR/IR against inventory for a purchase return. A return can exceed neither the delivered or received quantity nor what is already returned of
/// the line (ORDER.RETURN_EXCEEDS_DELIVERED). Built choices are in 07, 13.2y. Not built: the reversal of a return, dispositions other than AVAILABLE, tracking, dropship, migrated deliveries
/// and receipts, FIFO, the return credit notes, the clearing of the GR/IR debit against the receipt credit.
/// </summary>
public sealed partial class ReturnCommands(NpgsqlDataSource source)
{
    private readonly StockCommands _stock = new(source);

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<FulfilmentResult> Reject(CommandError e) => CommandResult<FulfilmentResult>.Rejected([e]);

    private static DateTime Now() { DateTime n = DateTime.UtcNow; return new DateTime(n.Ticks - n.Ticks % 10, DateTimeKind.Utc); }

    private sealed record Kind(
        bool Sales, OrderKind Order, string Permission, string Command, string DocumentType, string Header, string Line, string PartyColumn, string SourceHeader, string SourceLine, string SourceId,
        string SourceOrderId, string SourceOrderLine, string ReturnSourceLine, string Direction, string SeriesType);

    private static readonly Kind SalesKind = new(true, OrderKind.Sales, ReturnRules.SalesPermission, "sales-returns:post", FulfilmentHash2.SalesReturn, "sales_return", "sales_return_line", "customer_partner_id",
        "sales_delivery", "sales_delivery_line", "delivery_id", "sales_order_id", "sales_order_line_no", "delivery_line_no", "IN", "SALES_RETURN");

    private static readonly Kind PurchaseKind = new(false, OrderKind.Purchase, ReturnRules.PurchasePermission, "purchase-returns:post", FulfilmentHash2.PurchaseReturn, "purchase_return", "purchase_return_line",
        "supplier_partner_id", "purchase_receipt", "purchase_receipt_line", "receipt_id", "purchase_order_id", "purchase_order_line_no", "receipt_line_no", "OUT", "PURCHASE_RETURN");

    private sealed record In(string LineRef, Guid SourceId, int SourceLineNo, decimal Quantity, string UnitCode, string ReasonCode, string? Disposition, Guid? LocationId);

    public Task<CommandResult<FulfilmentResult>> PostSalesReturnAsync(Guid tenantId, CommandContext ctx, PostSalesReturnRequest req, CancellationToken ct = default) =>
        PostAsync(SalesKind, tenantId, ctx, req.CompanyId, req.ReturnDate, req.SourceDocumentRef,
            (req.Lines ?? []).Select(l => new In(l.LineRef, l.DeliveryId, l.DeliveryLineNo, l.Quantity, l.UnitCode, l.ReasonCode, l.Disposition, l.LocationId)).ToList(), ReturnRules.Fingerprint(req), ct);

    public Task<CommandResult<FulfilmentResult>> PostPurchaseReturnAsync(Guid tenantId, CommandContext ctx, PostPurchaseReturnRequest req, CancellationToken ct = default) =>
        PostAsync(PurchaseKind, tenantId, ctx, req.CompanyId, req.ReturnDate, req.SourceDocumentRef,
            (req.Lines ?? []).Select(l => new In(l.LineRef, l.ReceiptId, l.ReceiptLineNo, l.Quantity, l.UnitCode, l.ReasonCode, l.Disposition, l.LocationId)).ToList(), ReturnRules.Fingerprint(req), ct);

    private async Task<CommandResult<FulfilmentResult>> PostAsync(
        Kind kind, Guid tenantId, CommandContext ctx, Guid companyId, DateOnly date, string? sourceRef, List<In> lines, byte[] fingerprint, CancellationToken ct)
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
            if (l.SourceId == Guid.Empty || l.SourceLineNo < 1) Invalid($"/lines/{i}/source", "a " + (kind.Sales ? "delivery" : "receipt") + " and a line number");
            if (l.Quantity <= 0m || Math.Round(l.Quantity, 6) != l.Quantity) Invalid($"/lines/{i}/quantity", "more than 0, at most 6 fraction digits");
            if (string.IsNullOrWhiteSpace(l.UnitCode)) Invalid($"/lines/{i}/unit_code", "required");
            if (string.IsNullOrWhiteSpace(l.ReasonCode) || l.ReasonCode.Length > 50) Invalid($"/lines/{i}/reason_code", "required, at most 50 characters");
            if (lines.Take(i).Any(x => x.SourceId == l.SourceId && x.SourceLineNo == l.SourceLineNo)) Invalid($"/lines/{i}/source", "one line for each line of the " + (kind.Sales ? "delivery" : "receipt"));
        }
        if (sourceRef?.Length > 100) Invalid("/source_document_ref", "at most 100 characters");
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
                        : CommandResult<FulfilmentResult>.Ok(await LoadAsync(tx, kind, tenantId, companyId, earlier.EntityId.Value, null, ct), earlier.CommandLogId, replay: true);
                }
                return await PostCoreAsync(tx, kind, tenantId, ctx, companyId, date, sourceRef, lines, fingerprint, ct);
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

    private sealed record Source(
        Guid OrderId, int OrderLineNo, Guid ItemId, decimal BaseQuantity, Guid? WarehouseId, Guid? LocationId, int? StockLine, Guid? StockDocumentId, Guid Party, string Status, bool IsMirror);

    private sealed record Work(int Index, In Input, Source Source, Guid ItemId, decimal BaseQuantity, Guid Warehouse, Guid? Location, ReturnRef Ref);

    private async Task<CommandResult<FulfilmentResult>> PostCoreAsync(
        TenantTransaction tx, Kind kind, Guid tenantId, CommandContext ctx, Guid companyId, DateOnly date, string? sourceRef, List<In> lines, byte[] fingerprint, CancellationToken ct)
    {
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", companyId)));
        if (company.Status != "ACTIVE") return Reject(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company.Status)));
        var errors = new List<CommandError>();

        // the delivery or receipt lines that are returned
        var sources = new Dictionary<(Guid, int), Source>();
        for (int i = 0; i < lines.Count; i++)
        {
            In l = lines[i];
            await using var cmd = tx.Command(
                $"select x.{kind.SourceOrderId}, x.{kind.SourceOrderLine}, x.item_id, x.base_quantity, x.warehouse_id, x.location_id, x.stock_document_line_no, h.stock_document_id, h.{kind.PartyColumn}, h.status, " +
                $"h.reversal_of_{(kind.Sales ? "delivery" : "receipt")}_id is not null from core.{kind.SourceLine} x join core.{kind.SourceHeader} h on h.tenant_id = x.tenant_id and h.company_id = x.company_id and h.{kind.SourceId} = x.{kind.SourceId} " +
                $"where x.tenant_id = @t and x.company_id = @c and x.{kind.SourceId} = @d and x.line_no = @n");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", l.SourceId); cmd.Parameters.AddWithValue("n", l.SourceLineNo);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/lines/{i}/source", ("line_no", l.SourceLineNo))); continue; }
            var s = new Source(r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetInt32(6),
                r.IsDBNull(7) ? null : r.GetGuid(7), r.GetGuid(8), r.GetString(9), r.GetBoolean(10));
            if (s.Status != "posted" || s.IsMirror || s.StockLine is null || s.StockDocumentId is null)
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/lines/{i}/source", ("reason", "a posted goods line that is not a reversal")));
            else sources[(l.SourceId, l.SourceLineNo)] = s;
        }
        if (errors.Count > 0) return CommandResult<FulfilmentResult>.Rejected(errors);
        var parties = sources.Values.Select(s => s.Party).Distinct().ToList();
        if (parties.Count != 1) return Reject(Error("SCHEMA.INVALID_VALUE", "P2", "/lines", ("reason", "the lines of one return belong to one " + (kind.Sales ? "customer" : "supplier"))));
        Guid party = parties[0];
        await using (var cmd = tx.Command("select restriction_status from core.business_partner where tenant_id = @t and partner_id = @p"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", party);
            if (await cmd.ExecuteScalarAsync(ct) as string == "RESTRICTED") return Reject(Error("MASTER.PARTNER_RESTRICTED", "P2", "/lines", ("partner_id", party)));
        }

        // position 1 of the lock order: the order rows, sorted. The returnable quantities are read under this lock.
        await using (var cmd = tx.Command(
            $"select {kind.Order.IdColumn} from core.{kind.Order.HeaderTable} where tenant_id = @t and company_id = @c and {kind.Order.IdColumn} = any(@ids) order by {kind.Order.IdColumn} for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", sources.Values.Select(s => s.OrderId).Distinct().Order().ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { }
        }

        int lcScale = 2;
        await using (var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c"))
        {
            cmd.Parameters.AddWithValue("c", company.LocalCurrency);
            lcScale = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, CultureInfo.InvariantCulture);
        }
        var work = new List<Work>();
        for (int i = 0; i < lines.Count; i++)
        {
            In l = lines[i];
            string p = $"/lines/{i}";
            Source s = sources[(l.SourceId, l.SourceLineNo)];
            if (l.Disposition is not (null or "AVAILABLE")) { errors.Add(Error("LIMIT.RETURN_DISPOSITION_NOT_BUILT", "P2", p + "/disposition", ("disposition", l.Disposition))); continue; }
            string baseUnit = ""; int scale = 0;
            await using (var cmd = tx.Command("select base_unit, quantity_scale from core.item where tenant_id = @t and item_id = @i"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", s.ItemId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                baseUnit = r.GetString(0).Trim(); scale = r.GetInt16(1);
            }
            decimal? factor = await OrderCommands.FactorAsync(tx, tenantId, s.ItemId, l.UnitCode, baseUnit, ct);
            if (factor is null) { errors.Add(Error("INVENTORY.UNIT_CONVERSION_MISSING", "P2", p + "/unit_code", ("item_id", s.ItemId), ("unit_code", l.UnitCode))); continue; }
            decimal baseQty = l.Quantity * factor.Value;
            if (Math.Round(baseQty, scale) != baseQty) { errors.Add(Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", p + "/quantity", ("base_quantity", baseQty), ("quantity_scale", scale))); continue; }
            decimal returned = await ReturnedAsync(tx, kind, tenantId, companyId, l.SourceId, l.SourceLineNo, ct);
            decimal returnable = Math.Max(0m, s.BaseQuantity - returned);
            if (baseQty > returnable) { errors.Add(Error("ORDER.RETURN_EXCEEDS_DELIVERED", "P4", p + "/quantity", ("base_quantity", baseQty), ("returnable", returnable), ("returned", returned))); continue; }

            // the original movement and what earlier returns of it took (signed sums: a mirror of a return cancels it)
            decimal originalQty, originalValue, takenQty, takenValue;
            await using (var cmd = tx.Command("select abs(base_quantity), abs(value_lc) from core.stock_movement where tenant_id = @t and company_id = @c and stock_document_id = @d and line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", s.StockDocumentId!.Value); cmd.Parameters.AddWithValue("n", s.StockLine!.Value);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                originalQty = r.GetDecimal(0); originalValue = r.GetDecimal(1);
            }
            await using (var cmd = tx.Command(
                "select abs(coalesce(sum(base_quantity), 0)), abs(coalesce(sum(value_lc), 0)) from core.stock_movement where tenant_id = @t and company_id = @c and valuation_ref_document_id = @d and valuation_ref_line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", s.StockDocumentId!.Value); cmd.Parameters.AddWithValue("n", s.StockLine!.Value);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                takenQty = r.GetDecimal(0); takenValue = r.GetDecimal(1);
            }
            work.Add(new Work(i, l, s, s.ItemId, baseQty, s.WarehouseId!.Value, l.LocationId ?? s.LocationId, new ReturnRef(s.StockDocumentId!.Value, s.StockLine!.Value, originalQty, originalValue, takenQty, takenValue)));
        }
        if (errors.Count > 0) return CommandResult<FulfilmentResult>.Rejected(errors);

        // the number of the return
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

        long chainSeq = 0; byte[] prev = [], rowHash = [], lineDigest = [];
        List<Dictionary<string, object?>> lineRows = [];
        Guid? stockId = null, journalId = null;

        async Task<byte[]?> BuildNodeAsync(StockDerivation derivation)
        {
            (long seq, byte[] hash, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, FulfilmentHash2.ChainType, "",
                LedgerHash2.Genesis(tenantId, companyId, FulfilmentHash2.ChainType, ""), ct);
            chainSeq = seq + 1; prev = hash;
            stockId = derivation.StockDocumentId; journalId = derivation.JournalDocumentId;
            var byRequest = derivation.Movements.ToDictionary(m => m.RequestIndex);
            lineRows = [];
            foreach (Work w in work)
            {
                DerivedMovement m = byRequest[w.Index];
                lineRows.Add(new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = companyId, ["document_id"] = documentId, ["line_no"] = lineRows.Count + 1, ["source_document_id"] = w.Input.SourceId,
                    ["source_line_no"] = w.Input.SourceLineNo, ["order_id"] = w.Source.OrderId, ["order_line_no"] = w.Source.OrderLineNo, ["item_id"] = w.ItemId, ["quantity"] = w.Input.Quantity,
                    ["unit_code"] = w.Input.UnitCode, ["base_quantity"] = w.BaseQuantity, ["reason_code"] = w.Input.ReasonCode, ["disposition"] = "AVAILABLE", ["warehouse_id"] = w.Warehouse,
                    ["location_id"] = w.Location, ["tracking_id"] = null, ["stock_status"] = "AVAILABLE", ["stock_document_line_no"] = m.LineNo, ["value_lc"] = Math.Abs(m.ValueLc),
                    ["dropship_confirmation_id"] = null
                });
            }
            lineDigest = FulfilmentHash2.ReturnLineDigest(lineRows);
            var body = new Dictionary<string, object?>
            {
                ["document_id"] = documentId, ["document_type"] = kind.DocumentType, ["number_series_id"] = series.SeriesId, ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo,
                ["document_number"] = number, ["document_date"] = date, ["partner_id"] = party, ["warehouse_id"] = null, ["reference_1"] = null, ["reference_2"] = null,
                ["stock_document_id"] = stockId, ["reversal_of_id"] = null, ["reason_code"] = null, ["origin"] = "LIVE", ["migration_batch_id"] = null, ["source_system"] = ctx.SourceSystem,
                ["source_document_ref"] = sourceRef, ["posted_at"] = at, ["idempotency_key"] = ctx.IdempotencyKey, ["command_name"] = kind.Command, ["posted_by"] = ctx.Actor,
                ["correlation_id"] = ctx.CorrelationId, ["line_count"] = lineRows.Count, ["line_digest"] = lineDigest, ["hash_version"] = FulfilmentHash2.Version
            };
            rowHash = FulfilmentHash2.RowHash(tenantId, companyId, chainSeq, prev, body);
            return rowHash;
        }

        var stockInputs = work.Select(w => new StockLineInput(w.Input.LineRef, w.ItemId, w.Warehouse, w.Input.Quantity, w.Input.UnitCode, w.Location)).ToList();
        var stockRequest = new PostStockDocumentRequest(companyId, "RETURN", date, date, stockInputs, null, null, number);
        var refs = work.Select((w, k) => (k, w.Ref)).ToDictionary(x => x.k, x => x.Ref);
        var options = new StockPostOptions(kind.Command, WriteLog: false, Commit: false, BuildNodeAsync, null, kind.Direction, refs);
        var posted = await _stock.PostCoreAsync(tx, tenantId, ctx, stockRequest, fingerprint, ct, options);
        if (posted.Status != CommandStatus.Ok) return CommandResult<FulfilmentResult>.Rejected(posted.Errors.ToList());

        await using (var cmd = tx.Command(
            $"insert into core.{kind.Header} (tenant_id, company_id, return_id, return_number, number_series_id, fiscal_year, sequence_no, return_date, {kind.PartyColumn}, stock_document_id, source_system, source_document_ref, " +
            "posted_at, posted_by, correlation_id, idempotency_key, line_count, chain_seq, prev_hash, row_hash, line_digest, hash_version) values (@t, @c, @id, @num, @s, @fy, @seq, @d, @party, @stock, @src, @ref, @at, @by, " +
            "@corr, @key, @n, @cs, @prev, @row, @ld, 2)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", documentId); cmd.Parameters.AddWithValue("num", number);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("fy", fiscalYear); cmd.Parameters.AddWithValue("seq", sequenceNo); cmd.Parameters.AddWithValue("d", date);
            cmd.Parameters.AddWithValue("party", party); cmd.Parameters.AddWithValue("stock", stockId!.Value); cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey); cmd.Parameters.AddWithValue("n", lineRows.Count);
            cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("prev", prev); cmd.Parameters.AddWithValue("row", rowHash); cmd.Parameters.AddWithValue("ld", lineDigest);
            cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)sourceRef ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var row in lineRows)
        {
            await using var cmd = tx.Command(
                $"insert into core.{kind.Line} (tenant_id, company_id, return_id, line_no, {kind.SourceId}, {kind.ReturnSourceLine}, {kind.SourceOrderId}, {kind.SourceOrderLine}, item_id, quantity, unit_code, base_quantity, " +
                "reason_code, disposition, warehouse_id, location_id, stock_status, stock_document_line_no, value_lc) values (@t, @c, @id, @n, @sd, @sl, @o, @ol, @i, @q, @u, @bq, @rc, 'AVAILABLE', @w, @l, 'AVAILABLE', @stl, @v)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", documentId); cmd.Parameters.AddWithValue("n", row["line_no"]!);
            cmd.Parameters.AddWithValue("sd", row["source_document_id"]!); cmd.Parameters.AddWithValue("sl", row["source_line_no"]!); cmd.Parameters.AddWithValue("o", row["order_id"]!);
            cmd.Parameters.AddWithValue("ol", row["order_line_no"]!); cmd.Parameters.AddWithValue("i", row["item_id"]!); cmd.Parameters.AddWithValue("q", row["quantity"]!);
            cmd.Parameters.AddWithValue("u", row["unit_code"]!); cmd.Parameters.AddWithValue("bq", row["base_quantity"]!); cmd.Parameters.AddWithValue("rc", row["reason_code"]!);
            cmd.Parameters.AddWithValue("stl", row["stock_document_line_no"]!); cmd.Parameters.AddWithValue("v", row["value_lc"]!);
            cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Uuid) { Value = row["warehouse_id"] ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = row["location_id"] ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, FulfilmentHash2.ChainType, "", chainSeq, rowHash, FulfilmentHash2.Version, ct);
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, kind.Command, fingerprint, kind.Sales ? "SALES_RETURN" : "PURCHASE_RETURN", documentId, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "return.posted", kind.Sales ? "SALES_RETURN" : "PURCHASE_RETURN", $"{companyId:D}/{documentId:D}", "POSTED", at, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "FULFILMENT", ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["number"] = number, ["partner_id"] = party, ["lines"] = lineRows.Count, ["stock_document_id"] = stockId }), ct);

        FulfilmentResult result = await LoadAsync(tx, kind, tenantId, companyId, documentId, journalId, ct);
        result = result with { Lines = result.Lines.Select(l => l with { LineRef = work[l.LineNo - 1].Input.LineRef }).ToList() };
        await tx.CommitAsync(ct);   // the deferred checks: the chains, the movement and line counts, the journal, the outbox position
        return CommandResult<FulfilmentResult>.Ok(result, logId);
    }

    /// <summary>What is returned of a delivery or receipt line, mirror rows included (11, 4.3). Read under the order lock.</summary>
    private static async Task<decimal> ReturnedAsync(TenantTransaction tx, Kind kind, Guid tenantId, Guid companyId, Guid sourceId, int sourceLineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command($"select coalesce(sum(base_quantity), 0) from core.{kind.Line} where tenant_id = @t and company_id = @c and {kind.SourceId} = @d and {kind.ReturnSourceLine} = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", sourceId); cmd.Parameters.AddWithValue("n", sourceLineNo);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<FulfilmentResult> LoadAsync(TenantTransaction tx, Kind kind, Guid tenantId, Guid companyId, Guid id, Guid? journal, CancellationToken ct)
    {
        string number; long seq; byte[] hash; Guid? stock;
        await using (var cmd = tx.Command($"select return_number, chain_seq, row_hash, stock_document_id from core.{kind.Header} where tenant_id = @t and company_id = @c and return_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            number = r.GetString(0); seq = r.GetInt64(1); hash = (byte[])r[2]; stock = r.IsDBNull(3) ? null : r.GetGuid(3);
        }
        if (journal is null && stock is { } sid)
            await using (var cmd = tx.Command("select journal_document_id from core.stock_document where tenant_id = @t and company_id = @c and stock_document_id = @s"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", sid);
                journal = await cmd.ExecuteScalarAsync(ct) as Guid?;
            }
        var raw = new List<(int, Guid, int, decimal, decimal?, int?, Guid, int)>();
        await using (var cmd = tx.Command(
            $"select line_no, {kind.SourceOrderId}, {kind.SourceOrderLine}, base_quantity, value_lc, stock_document_line_no, {kind.SourceId}, {kind.ReturnSourceLine} from core.{kind.Line} " +
            "where tenant_id = @t and company_id = @c and return_id = @i order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) raw.Add((r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.GetGuid(6), r.GetInt32(7)));
        }
        var lines = new List<FulfilmentLineResult>();
        foreach (var (no, order, orderLine, bq, value, stockLine, sourceId, sourceLine) in raw)
        {
            decimal returned = await ReturnedAsync(tx, kind, tenantId, companyId, sourceId, sourceLine, ct), delivered;
            await using (var cmd = tx.Command($"select base_quantity from core.{kind.SourceLine} where tenant_id = @t and company_id = @c and {kind.SourceId} = @d and line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", sourceId); cmd.Parameters.AddWithValue("n", sourceLine);
                delivered = (decimal)(await cmd.ExecuteScalarAsync(ct))!;
            }
            lines.Add(new FulfilmentLineResult(no, "", order, orderLine, bq, value, stockLine, returned, Math.Max(0m, delivered - returned)));
        }
        return new FulfilmentResult(id, number, seq, Canonical.FormatHex32(hash), stock, journal, lines);
    }
}
