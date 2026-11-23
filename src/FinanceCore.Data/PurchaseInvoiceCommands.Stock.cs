using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The cost adjustment of a purchase invoice (11, 7.2; 10, 5.6, 6.6). A stock line whose invoice value differs from the receipt value posts the difference: the part on hand to INVENTORY and the
/// part consumed to COGS (moving average: on hand is the lesser of the matched quantity and the quantity of the cost key). The on-hand part is a COST_ADJUST movement of a STOCK_COST_ADJUSTMENT
/// document that uses the journal document of the invoice as its derived journal (external journal mode). The stock document binds the digest of the link records, the journal document binds the
/// digest of that digest and the row hash of the stock document (ADR-052 extended); the journal id is allocated first, so there is no circle.
/// </summary>
public sealed partial class PurchaseInvoiceCommands
{
    private const string StockSeriesKey = "STOCK_DOC_COST_ADJUSTMENT";

    private sealed record StockDraft(
        Guid StockId, Guid JournalId, long ChainSeq, byte[] Prev, byte[] RowHash, long SequenceNo, string Number, int FiscalYear, int PeriodNo, bool SoftOverride, Guid SeriesId, DateTime At,
        byte[] LinkDigest, byte[] MovementDigest, List<Dictionary<string, object?>> Movements);

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

    /// <summary>The warehouse and location of the movement of a receipt line: where the stock of the receipt is, so where its cost adjustment goes.</summary>
    private static async Task<(Guid Warehouse, Guid? Location)?> MovementPlaceAsync(TenantTransaction tx, Guid tenantId, Guid companyId, ReceiptLine receipt, CancellationToken ct)
    {
        await using var cmd = tx.Command("select warehouse_id, location_id from core.stock_movement where tenant_id = @t and company_id = @c and stock_document_id = @s and line_no = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", receipt.StockDocumentId!.Value); cmd.Parameters.AddWithValue("n", receipt.StockDocumentLineNo ?? -1);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1)) : null;
    }

    /// <summary>
    /// Under the LEDGER lock of the poster: the number and the STOCK head, the cost state read again (it must equal what the split was made from, else the caller retries), the movements and the
    /// row hash of the stock document. Nothing is written but the number counter.
    /// </summary>
    private static async Task<StockDraft> PrepareStockAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, Guid stockId, Guid journalId, DateOnly invoiceDate, DateOnly postingDate, string currency, List<Work> work,
        Dictionary<Guid, (decimal Qty, decimal Value)> costRead, byte[] linkDigest, CancellationToken ct)
    {
        var recheck = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, postingDate, ctx, true, ct);
        if (recheck.Errors.Count > 0) throw new MatchRejection(recheck.Errors[0]);
        Guid ledgerId = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
        var series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, StockSeriesKey, postingDate, ledgerId, ct)
            ?? throw new MatchRejection(Error("SERIES.NO_ASSIGNMENT", "P2", "/lines", ("document_type", StockSeriesKey), ("posting_date", postingDate)));
        int fiscalYear = recheck.FiscalYear!.Value, periodNo = recheck.PeriodNo!.Value;
        long sequenceNo;
        await using (var cmd = tx.Command("update core.number_range_counter set last_number = last_number + 1, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y returning last_number"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            sequenceNo = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        string number = Core.Posting.PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
        (long headSeq, byte[] prev, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, StockHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, StockHash2.ChainType, ""), ct);

        // the cost state under the lock must be the one the split was made from
        var cost = new Dictionary<Guid, (decimal Qty, decimal Value)>();
        foreach (var (item, before) in costRead)
        {
            var now = await CostBalanceAsync(tx, tenantId, companyId, item, ct);
            if (now != before) throw new MatchRejection(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            cost[item] = now;
        }

        DateTime at = DateTime.UtcNow;
        at = new DateTime(at.Ticks - at.Ticks % 10, DateTimeKind.Utc);
        long chainSeq = headSeq + 1;
        var movements = new List<Dictionary<string, object?>>();
        foreach (Work w in work.Where(x => x.Stock))
            foreach (Link k in w.Links.Where(x => x.OnHand != 0m))
            {
                var (costQty, costValue) = cost[w.Line.ItemId];
                costValue += k.OnHand;
                cost[w.Line.ItemId] = (costQty, costValue);
                decimal stockQty = await StockBalanceAsync(tx, tenantId, companyId, w.Line.ItemId, k.Warehouse!.Value, k.Location, ct);
                string baseUnit;
                await using (var cmd = tx.Command("select base_unit from core.item where tenant_id = @t and item_id = @i"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", w.Line.ItemId);
                    baseUnit = ((string)(await cmd.ExecuteScalarAsync(ct))!).Trim();
                }
                movements.Add(new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = companyId, ["stock_document_id"] = stockId, ["line_no"] = movements.Count + 1, ["chain_seq"] = chainSeq, ["movement_type"] = "COST_ADJUST",
                    ["cost_source"] = "INVOICE", ["valuation_ref_document_id"] = null, ["valuation_ref_line_no"] = null, ["posting_date"] = postingDate, ["item_id"] = w.Line.ItemId,
                    ["warehouse_id"] = k.Warehouse, ["location_id"] = k.Location, ["tracking_id"] = null, ["stock_status"] = "AVAILABLE", ["quantity"] = 0m, ["unit_code"] = baseUnit, ["base_quantity"] = 0m,
                    ["value_lc"] = k.OnHand, ["unit_cost_lc"] = null, ["value_tc"] = k.OnHand, ["currency_tc"] = currency, ["cost_warehouse_id"] = null, ["cost_qty_after"] = costQty,
                    ["cost_value_after_lc"] = costValue, ["stock_qty_after"] = stockQty, ["lot_expired"] = false, ["count_ref"] = null, ["expected_book_quantity"] = null,
                    ["source_line_ref"] = w.Input.LineRef, ["reversal_of_line_no"] = null
                });
            }
        byte[] movementDigest = StockHash2.MovementDigest(movements.Select(m => (IReadOnlyDictionary<string, object?>)m).ToList());
        var body = new Dictionary<string, object?>
        {
            ["stock_document_id"] = stockId, ["document_type"] = "STOCK_COST_ADJUSTMENT", ["number_series_id"] = series.SeriesId, ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo,
            ["document_number"] = number, ["fiscal_period"] = periodNo, ["document_date"] = invoiceDate, ["posting_date"] = postingDate, ["posted_at"] = at, ["reason_code"] = null,
            ["reversal_of_stock_document_id"] = null, ["journal_document_id"] = journalId, ["soft_close_override"] = recheck.SoftCloseOverride, ["currency_tc"] = currency,
            ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = null, ["idempotency_key"] = ctx.IdempotencyKey, ["command_name"] = CommandName, ["posted_by"] = ctx.Actor,
            ["correlation_id"] = ctx.CorrelationId, ["movement_count"] = movements.Count, ["detail_digest"] = linkDigest, ["movement_digest"] = movementDigest, ["hash_version"] = StockHash2.Version
        };
        byte[] rowHash = StockHash2.RowHash(tenantId, companyId, chainSeq, prev, body);
        return new StockDraft(stockId, journalId, chainSeq, prev, rowHash, sequenceNo, number, fiscalYear, periodNo, recheck.SoftCloseOverride, series.SeriesId, at, linkDigest, movementDigest, movements);
    }

    /// <summary>The journal document binds the digest of the link records and the row hash of the stock document (a digest of digests).</summary>
    private static byte[] CombinedDigest(byte[] linkDigest, byte[] stockRowHash) => SHA256.HashData([.. linkDigest, .. stockRowHash]);

    private static async Task InsertStockAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, StockDraft d, DateOnly invoiceDate, DateOnly postingDate, string currency, CancellationToken ct)
    {
        await using (var cmd = tx.Command(
            "insert into core.stock_document (tenant_id, company_id, stock_document_id, document_type, number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, document_date, " +
            "posting_date, posted_at, journal_document_id, soft_close_override, currency_tc, source_system, idempotency_key, command_name, posted_by, correlation_id, movement_count, detail_digest, " +
            "movement_digest, chain_seq, prev_hash, row_hash, hash_version) values (@t, @c, @id, 'STOCK_COST_ADJUSTMENT', @s, @fy, @seq, @num, @fp, @dd, @pd, @at, @j, @soft, @tc, @src, @key, @cmdname, @by, @corr, " +
            "@n, @det, @md, @cs, @prev, @row, 2)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", d.StockId); cmd.Parameters.AddWithValue("s", d.SeriesId);
            cmd.Parameters.AddWithValue("fy", d.FiscalYear); cmd.Parameters.AddWithValue("seq", d.SequenceNo); cmd.Parameters.AddWithValue("num", d.Number); cmd.Parameters.AddWithValue("fp", d.PeriodNo);
            cmd.Parameters.AddWithValue("dd", invoiceDate); cmd.Parameters.AddWithValue("pd", postingDate); cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = d.At });
            cmd.Parameters.AddWithValue("j", d.JournalId); cmd.Parameters.AddWithValue("soft", d.SoftOverride); cmd.Parameters.AddWithValue("tc", currency); cmd.Parameters.AddWithValue("src", ctx.SourceSystem);
            cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey); cmd.Parameters.AddWithValue("cmdname", CommandName); cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
            cmd.Parameters.AddWithValue("n", d.Movements.Count); cmd.Parameters.AddWithValue("det", d.LinkDigest); cmd.Parameters.AddWithValue("md", d.MovementDigest); cmd.Parameters.AddWithValue("cs", d.ChainSeq);
            cmd.Parameters.AddWithValue("prev", d.Prev); cmd.Parameters.AddWithValue("row", d.RowHash);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var m in d.Movements)
        {
            await using var cmd = tx.Command(
                "insert into core.stock_movement (tenant_id, company_id, stock_document_id, line_no, chain_seq, movement_type, cost_source, posting_date, item_id, warehouse_id, location_id, stock_status, " +
                "quantity, unit_code, base_quantity, value_lc, unit_cost_lc, value_tc, currency_tc, cost_qty_after, cost_value_after_lc, stock_qty_after, lot_expired, source_line_ref) " +
                "values (@t, @c, @d, @n, @cs, 'COST_ADJUST', 'INVOICE', @pd, @i, @w, @l, 'AVAILABLE', 0, @u, 0, @v, null, @v, @ct, @cq, @cv, @sq, false, @ref)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", d.StockId); cmd.Parameters.AddWithValue("n", m["line_no"]!);
            cmd.Parameters.AddWithValue("cs", d.ChainSeq); cmd.Parameters.AddWithValue("pd", postingDate); cmd.Parameters.AddWithValue("i", m["item_id"]!); cmd.Parameters.AddWithValue("w", m["warehouse_id"]!);
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = m["location_id"] ?? DBNull.Value });
            cmd.Parameters.AddWithValue("u", m["unit_code"]!); cmd.Parameters.AddWithValue("v", m["value_lc"]!); cmd.Parameters.AddWithValue("ct", currency); cmd.Parameters.AddWithValue("cq", m["cost_qty_after"]!);
            cmd.Parameters.AddWithValue("cv", m["cost_value_after_lc"]!); cmd.Parameters.AddWithValue("sq", m["stock_qty_after"]!); cmd.Parameters.AddWithValue("ref", m["source_line_ref"]!);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, StockHash2.ChainType, "", d.ChainSeq, d.RowHash, StockHash2.Version, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "stock.document_posted", "STOCK_DOCUMENT", $"{companyId:D}/{d.StockId:D}", "POSTED", d.At, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "STOCK", ["scope"] = "", ["chain_seq"] = d.ChainSeq, ["row_hash"] = Canonical.FormatHex32(d.RowHash) }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["document_type"] = "STOCK_COST_ADJUSTMENT", ["posting_date"] = postingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["movement_count"] = d.Movements.Count, ["journal_document_id"] = d.JournalId }), ct);
        foreach (var last in d.Movements.GroupBy(m => ((Guid)m["item_id"]!, (Guid)m["warehouse_id"]!, (Guid?)m["location_id"])).Select(g => g.Last()))
            await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "stock.balance_changed", "STOCK_BALANCE", $"{companyId:D}/{(Guid)last["item_id"]!:D}/{(Guid)last["warehouse_id"]!:D}", "CHANGED", d.At, isAudit: false, null,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["item_id"] = last["item_id"], ["warehouse_id"] = last["warehouse_id"], ["location_id"] = last["location_id"], ["quantity_after"] = last["stock_qty_after"] }), ct);
    }
}
