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
/// sales-deliveries:reverse (11, 6.3, 6.8) and purchase-receipts:reverse (11, 7.1, 6.8): the owning commands of a delivery or a receipt, its stock document and its derived journal. The
/// reversal inserts a mirror document as the next node of the FULFILMENT chain, with the negative quantities and the negative values of the original lines, the reason code and the id of
/// the original; the original gets the status reversed. The stock document is reversed by the stock command in the same transaction (StockCommands.ReverseCoreAsync), and its
/// detail_digest is the row hash of the mirror node, as at posting. Built choices are in 07, 13.2x. Not built: the rules that need invoices and returns (ORDER.DELIVERY_INVOICED,
/// ORDER.DELIVERY_RETURNED, ORDER.RECEIPT_INVOICED), the clearings of a receipt covered by a supplier invoice, tracking.
/// </summary>
public sealed partial class FulfilmentCommands
{
    private sealed record OriginalLine(
        int LineNo, Guid OrderId, int OrderLineNo, Guid ItemId, decimal Quantity, string UnitCode, decimal BaseQuantity, Guid? WarehouseId, Guid? LocationId, int? StockLine, int StockLineCount,
        decimal? UnitPrice, string? Currency, decimal? Value);

    public Task<CommandResult<FulfilmentResult>> ReverseDeliveryAsync(Guid tenantId, CommandContext ctx, ReverseFulfilmentRequest req, CancellationToken ct = default) =>
        ReverseAsync(Delivery, "sales-deliveries:reverse", tenantId, ctx, req, ct);

    public Task<CommandResult<FulfilmentResult>> ReverseReceiptAsync(Guid tenantId, CommandContext ctx, ReverseFulfilmentRequest req, CancellationToken ct = default) =>
        ReverseAsync(Receipt, "purchase-receipts:reverse", tenantId, ctx, req, ct);

    private async Task<CommandResult<FulfilmentResult>> ReverseAsync(Kind kind, string commandName, Guid tenantId, CommandContext ctx, ReverseFulfilmentRequest req, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(kind.Permission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", kind.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (req.DocumentId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/document_id", ("reason", "required")));
        if (errors.Count > 0) return CommandResult<FulfilmentResult>.Rejected(errors);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Reject(Error("REVERSAL.REASON_REQUIRED", "P1", "/reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode)) return Reject(Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "/reason_code", ("allowed_values", ReversalRules.ExternalReasons)));
        byte[] fingerprint = StockReversalRules.Fingerprint(commandName, req.CompanyId, req.DocumentId, req.ReasonCode, req.ReversalDate);

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
                        : CommandResult<FulfilmentResult>.Ok(await LoadAsync(tx, kind, tenantId, req.CompanyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
                }
                return await ReverseCoreAsync(tx, kind, commandName, tenantId, ctx, req, fingerprint, ct);
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

    private async Task<CommandResult<FulfilmentResult>> ReverseCoreAsync(
        TenantTransaction tx, Kind kind, string commandName, Guid tenantId, CommandContext ctx, ReverseFulfilmentRequest req, byte[] fingerprint, CancellationToken ct)
    {
        Guid companyId = req.CompanyId, originalId = req.DocumentId;
        bool sales = kind.Order.Kind == "SALES";
        string reversalColumn = sales ? "reversal_of_delivery_id" : "reversal_of_receipt_id";
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null || company.Status != "ACTIVE") return Reject(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company?.Status)));

        // P2: the original
        string status; Guid party; Guid? headerWarehouse, stockId; string? reference1, reference2; DateOnly originalDate;
        await using (var cmd = tx.Command(
            $"select status, {kind.PartyColumn}, warehouse_id, {(sales ? "carrier_reference, tracking_reference" : "delivery_note_reference, null::varchar")}, stock_document_id, {kind.Date}, {reversalColumn} is not null " +
            $"from core.{kind.Header} where tenant_id = @t and company_id = @c and {kind.Id} = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", originalId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Reject(Error("REVERSAL.NOT_FOUND", "P2", "/document_id"));
            if (r.GetBoolean(7)) return Reject(Error("REVERSAL.TYPE_NOT_REVERSIBLE", "P2", "/document_id", ("reason", "a reversal document is not reversed")));
            status = r.GetString(0); party = r.GetGuid(1); headerWarehouse = r.IsDBNull(2) ? null : r.GetGuid(2);
            reference1 = r.IsDBNull(3) ? null : r.GetString(3); reference2 = r.IsDBNull(4) ? null : r.GetString(4); stockId = r.IsDBNull(5) ? null : r.GetGuid(5);
            originalDate = DateOnly.FromDateTime(r.GetDateTime(6));
        }
        if (status != "posted") return Reject(Error("REVERSAL.ALREADY_REVERSED", "P2", "/document_id"));
        DateOnly date = req.ReversalDate ?? originalDate;

        var original = new List<OriginalLine>();
        await using (var cmd = tx.Command(
            $"select line_no, {kind.OrderIdColumn}, {kind.OrderLineColumn}, item_id, quantity, unit_code, base_quantity, warehouse_id, location_id, stock_document_line_no, " +
            $"{(sales ? "case when stock_document_line_no is null then 0 else 1 end, null::numeric, null::char, value_lc" : "stock_document_line_count, unit_price, currency, value_lc")} " +
            $"from core.{kind.Line} where tenant_id = @t and company_id = @c and {kind.Id} = @i order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", originalId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                original.Add(new OriginalLine(r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetGuid(3), r.GetDecimal(4), r.GetString(5).Trim(), r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetGuid(7),
                    r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetInt32(9), r.GetInt32(10), r.IsDBNull(11) ? null : r.GetDecimal(11), r.IsDBNull(12) ? null : r.GetString(12).Trim(),
                    r.IsDBNull(13) ? null : r.GetDecimal(13)));
        }

        // position 1 of the lock order of 11, 9: the order rows, sorted
        await using (var cmd = tx.Command(
            $"select {kind.Order.IdColumn} from core.{kind.Order.HeaderTable} where tenant_id = @t and company_id = @c and {kind.Order.IdColumn} = any(@ids) order by {kind.Order.IdColumn} for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", original.Select(l => l.OrderId).Distinct().Order().ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { }
        }
        await using (var cmd = tx.Command($"select status from core.{kind.Header} where tenant_id = @t and company_id = @c and {kind.Id} = @i for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", originalId);
            if ((string?)await cmd.ExecuteScalarAsync(ct) != "posted") return Reject(Error("REVERSAL.ALREADY_REVERSED", "P7", "/document_id"));
        }

        // a delivery or receipt that a return that is not reversed refers to is reversed after the return (11, 6.3, B6)
        string returnLine = sales ? "sales_return_line" : "purchase_return_line", returnHeader = sales ? "sales_return" : "purchase_return", returnSource = sales ? "delivery_id" : "receipt_id";
        await using (var cmd = tx.Command(
            $"select exists (select 1 from core.{returnLine} l join core.{returnHeader} h on h.tenant_id = l.tenant_id and h.company_id = l.company_id and h.return_id = l.return_id " +
            $"where l.tenant_id = @t and l.company_id = @c and l.{returnSource} = @i and h.status = 'posted' and h.reversal_of_return_id is null)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", originalId);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return Reject(Error(sales ? "ORDER.DELIVERY_RETURNED" : "ORDER.RECEIPT_RETURNED", "P6", "/document_id", ("reason", "the return is reversed first")));
        }

        // a delivery is not reversed while an invoice that is not reversed names one of its lines, nor when the delivered quantity of a DELIVERED line would fall below what is invoiced (11, 6.3, D2)
        if (sales)
        {
            const string InvoiceRows = "from core.sales_invoice_line {a} join core.sales_invoice {h} on {h}.tenant_id = {a}.tenant_id and {h}.company_id = {a}.company_id and {h}.sales_invoice_id = {a}.sales_invoice_id " +
                "left join core.sales_invoice {o} on {o}.tenant_id = {h}.tenant_id and {o}.company_id = {h}.company_id and {o}.sales_invoice_id = {h}.reversal_of_sales_invoice_id " +
                "where ({h}.invoice_kind = 'INVOICE' or ({h}.invoice_kind = 'REVERSAL' and {o}.invoice_kind = 'INVOICE'))";
            string named = InvoiceRows.Replace("{a}", "l").Replace("{h}", "h").Replace("{o}", "o") + " and l.tenant_id = d.tenant_id and l.company_id = d.company_id and l.delivery_id = d.delivery_id and l.delivery_line_no = d.line_no";
            string byOrder = InvoiceRows.Replace("{a}", "l2").Replace("{h}", "h2").Replace("{o}", "o2") + " and l2.tenant_id = d.tenant_id and l2.company_id = d.company_id and l2.sales_order_id = d.sales_order_id and l2.sales_order_line_no = d.sales_order_line_no";
            await using var cmd = tx.Command(
                "select exists (select 1 from core.sales_delivery_line d where d.tenant_id = @t and d.company_id = @c and d.delivery_id = @i and (" +
                $"(select coalesce(sum(l.base_quantity), 0) {named}) > 0 or exists (select 1 from core.sales_order_line ol where ol.tenant_id = d.tenant_id and ol.company_id = d.company_id and ol.sales_order_id = d.sales_order_id " +
                "and ol.line_no = d.sales_order_line_no and ol.invoice_policy = 'DELIVERED' and (select coalesce(sum(x.base_quantity), 0) from core.sales_delivery_line x where x.tenant_id = d.tenant_id and x.company_id = d.company_id " +
                $"and x.sales_order_id = d.sales_order_id and x.sales_order_line_no = d.sales_order_line_no) - d.base_quantity < (select coalesce(sum(l2.base_quantity), 0) {byOrder}))))");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", originalId);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return Reject(Error("ORDER.DELIVERY_INVOICED", "P6", "/document_id", ("reason", "the invoice is reversed or credited first")));
        }

        // the number of the mirror document: the series of its type (a type without a mirror type keeps its document type, 14, 11)
        int fiscalYear;
        await using (var cmd = tx.Command("select fiscal_year from core.fiscal_year where tenant_id = @t and company_id = @c and start_date <= @d and end_date >= @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", date);
            if (await cmd.ExecuteScalarAsync(ct) is not int year) return Reject(Error("SYSTEM.FISCAL_YEAR_MISSING", "P2", "/reversal_date", ("date", date)));
            fiscalYear = year;
        }
        Guid ledger = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
        var series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, kind.SeriesType, date, ledger, ct);
        if (series is null) return Reject(Error("SERIES.NO_ASSIGNMENT", "P2", "/reversal_date", ("document_type", kind.SeriesType), ("posting_date", date)));
        long? last = await ClearingPoster.LockCounterAsync(tx, tenantId, companyId, series.SeriesId, fiscalYear, ct);
        if (last is null) return Reject(Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "/reversal_date", ("series_code", series.SeriesCode), ("fiscal_year", fiscalYear)));
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

        // the node of the chain, built once the stock reversal has derived its movements: the mirror stock document binds its row hash
        long chainSeq = 0; byte[] prev = [], rowHash = [];
        List<Dictionary<string, object?>> lineRows = [];
        byte[] lineDigest = [];
        Guid? mirrorStockId = null, journalId = null;

        async Task<byte[]?> BuildNodeAsync(StockDerivation? derivation)
        {
            (long seq, byte[] hash, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, FulfilmentHash2.ChainType, "",
                LedgerHash2.Genesis(tenantId, companyId, FulfilmentHash2.ChainType, ""), ct);
            chainSeq = seq + 1; prev = hash;
            mirrorStockId = derivation?.StockDocumentId; journalId = derivation?.JournalDocumentId;
            var byStockLine = derivation?.Movements.ToDictionary(m => m.RequestIndex) ?? [];
            lineRows = [];
            foreach (OriginalLine l in original)
            {
                DerivedMovement? m = l.StockLine is null ? null : byStockLine[l.StockLine.Value - 1];
                lineRows.Add(new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = companyId, ["document_id"] = documentId, ["line_no"] = l.LineNo, ["order_id"] = l.OrderId, ["order_line_no"] = l.OrderLineNo,
                    ["item_id"] = l.ItemId, ["quantity"] = -l.Quantity, ["unit_code"] = l.UnitCode, ["base_quantity"] = -l.BaseQuantity, ["warehouse_id"] = l.WarehouseId,
                    ["location_id"] = l.LocationId, ["tracking_id"] = null, ["stock_status"] = "AVAILABLE", ["stock_document_line_no"] = m?.LineNo, ["stock_document_line_count"] = m is null ? 0 : 1,
                    ["unit_price"] = l.UnitPrice, ["currency"] = l.Currency, ["value_lc"] = m is null ? null : sales ? -m.ValueLc : m.ValueLc
                });
            }
            lineDigest = FulfilmentHash2.LineDigest(lineRows);
            var body = new Dictionary<string, object?>
            {
                ["document_id"] = documentId, ["document_type"] = kind.DocumentType, ["number_series_id"] = series.SeriesId, ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo,
                ["document_number"] = number, ["document_date"] = date, ["partner_id"] = party, ["warehouse_id"] = headerWarehouse, ["reference_1"] = reference1, ["reference_2"] = reference2,
                ["stock_document_id"] = mirrorStockId, ["reversal_of_id"] = originalId, ["reason_code"] = req.ReasonCode, ["origin"] = "LIVE", ["migration_batch_id"] = null,
                ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = null, ["posted_at"] = at, ["idempotency_key"] = ctx.IdempotencyKey, ["command_name"] = commandName,
                ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId, ["line_count"] = lineRows.Count, ["line_digest"] = lineDigest, ["hash_version"] = FulfilmentHash2.Version
            };
            rowHash = FulfilmentHash2.RowHash(tenantId, companyId, chainSeq, prev, body);
            return rowHash;
        }

        if (stockId is { } sid)
        {
            var options = new StockReverseOptions(commandName, WriteLog: false, Commit: false, AllowDerived: true, BuildNodeAsync);
            var reversed = await _stock.ReverseCoreAsync(tx, tenantId, ctx, companyId, sid, req.ReasonCode, date, fingerprint, ct, options);
            if (reversed.Status != CommandStatus.Ok)
                return CommandResult<FulfilmentResult>.Rejected(reversed.Errors.Select(e => e with { Path = Regex.Replace(e.Path ?? "", @"^/stock_document_id", "/document_id") }).ToList());
        }
        else await BuildNodeAsync(null);

        // the mirror document, its lines and the chain
        await using (var cmd = tx.Command(
            $"insert into core.{kind.Header} (tenant_id, company_id, {kind.Id}, {kind.Number}, number_series_id, fiscal_year, sequence_no, {kind.Date}, {kind.PartyColumn}, warehouse_id, " +
            $"{(sales ? "carrier_reference, tracking_reference" : "delivery_note_reference")}, stock_document_id, {reversalColumn}, reason_code, source_system, posted_at, posted_by, correlation_id, " +
            "idempotency_key, line_count, chain_seq, prev_hash, row_hash, line_digest, hash_version) values (@t, @c, @id, @num, @s, @fy, @seq, @d, @party, @wh, @r1, " +
            (sales ? "@r2, " : "") + "@stock, @orig, @reason, @src, @at, @by, @corr, @key, @n, @cs, @prev, @row, @ld, 2)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", documentId); cmd.Parameters.AddWithValue("num", number);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("fy", fiscalYear); cmd.Parameters.AddWithValue("seq", sequenceNo); cmd.Parameters.AddWithValue("d", date);
            cmd.Parameters.AddWithValue("party", party); cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey); cmd.Parameters.AddWithValue("n", lineRows.Count);
            cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("prev", prev); cmd.Parameters.AddWithValue("row", rowHash); cmd.Parameters.AddWithValue("ld", lineDigest);
            cmd.Parameters.AddWithValue("orig", originalId); cmd.Parameters.AddWithValue("reason", req.ReasonCode);
            cmd.Parameters.Add(new NpgsqlParameter("wh", NpgsqlDbType.Uuid) { Value = (object?)headerWarehouse ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("r1", NpgsqlDbType.Text) { Value = (object?)reference1 ?? DBNull.Value });
            if (sales) cmd.Parameters.Add(new NpgsqlParameter("r2", NpgsqlDbType.Text) { Value = (object?)reference2 ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("stock", NpgsqlDbType.Uuid) { Value = (object?)mirrorStockId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var row in lineRows)
        {
            await using var cmd = tx.Command(
                $"insert into core.{kind.Line} (tenant_id, company_id, {kind.Id}, line_no, {kind.OrderIdColumn}, {kind.OrderLineColumn}, item_id, quantity, unit_code, base_quantity, warehouse_id, location_id, " +
                "stock_status, stock_document_line_no, value_lc" + (sales ? "" : ", unit_price, currency, stock_document_line_count") + ") values (@t, @c, @id, @n, @o, @ol, @i, @q, @u, @bq, @w, @l, 'AVAILABLE', @sl, @v" +
                (sales ? "" : ", @up, @cur, @slc") + ")");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", documentId); cmd.Parameters.AddWithValue("n", row["line_no"]!);
            cmd.Parameters.AddWithValue("o", row["order_id"]!); cmd.Parameters.AddWithValue("ol", row["order_line_no"]!); cmd.Parameters.AddWithValue("i", row["item_id"]!);
            cmd.Parameters.AddWithValue("q", row["quantity"]!); cmd.Parameters.AddWithValue("u", row["unit_code"]!); cmd.Parameters.AddWithValue("bq", row["base_quantity"]!);
            cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Uuid) { Value = row["warehouse_id"] ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = row["location_id"] ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("sl", NpgsqlDbType.Integer) { Value = row["stock_document_line_no"] ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Numeric) { Value = row["value_lc"] ?? DBNull.Value });
            if (!sales)
            {
                cmd.Parameters.Add(new NpgsqlParameter("up", NpgsqlDbType.Numeric) { Value = row["unit_price"] ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("cur", NpgsqlDbType.Text) { Value = row["currency"] ?? DBNull.Value });
                cmd.Parameters.AddWithValue("slc", row["stock_document_line_count"]!);
            }
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = tx.Command($"update core.{kind.Header} set status = 'reversed' where tenant_id = @t and company_id = @c and {kind.Id} = @i and status = 'posted'"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", originalId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, FulfilmentHash2.ChainType, "", chainSeq, rowHash, FulfilmentHash2.Version, ct);
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, commandName, fingerprint, kind.Entity, documentId, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, kind.Event.Replace(".posted", ".reversed", StringComparison.Ordinal), kind.Entity, $"{companyId:D}/{originalId:D}", "REVERSED", at, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "FULFILMENT", ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["number"] = number, ["partner_id"] = party, ["lines"] = lineRows.Count, ["stock_document_id"] = mirrorStockId, ["reversal_of_id"] = originalId }), ct);

        FulfilmentResult result = await LoadAsync(tx, kind, tenantId, companyId, documentId, ct);
        result = result with { JournalDocumentId = journalId };
        await tx.CommitAsync(ct);   // the deferred checks: the chains, the movement and line counts, the journal, the outbox position
        return CommandResult<FulfilmentResult>.Ok(result, logId);
    }
}
