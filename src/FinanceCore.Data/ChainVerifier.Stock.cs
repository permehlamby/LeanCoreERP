using System.Security.Cryptography;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using Npgsql;

namespace FinanceCore.Data;

public sealed partial class ChainVerifier
{
    /// <summary>
    /// chains:verify for the STOCK chain (10, 4.1, 5.6): recomputes the movement digest and the row hash of every stock document from the stored columns, checks the links, the gapless
    /// chain_seq, the head, the movement count, that the journal document of a stock document binds what it should (the row hash of the stock document, or for a document derived by a purchase
    /// invoice the digest of its link digest and its row hash, ADR-052 extended) and that the cost key (item, cost warehouse, lot or serial) follows from the movements (the quantity and the value after a movement are
    /// those before plus the movement), that the movement digest covers the consumption rows, and that every FIFO layer matches its movement, is not over-consumed and has the exhausted flag that its consumption gives.
    /// </summary>
    public async Task<VerificationResult> VerifyStockAsync(Guid tenantId, Guid companyId, CancellationToken ct = default)
    {
        var problems = new List<VerificationProblem>();
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);

        long headSeq = 0; byte[]? headHash = null; bool headFound = false;
        await using (var cmd = tx.Command("select last_seq, last_hash from core.chain_head where tenant_id = @t and company_id = @c and chain_type = 'STOCK' and scope_key = ''"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { headFound = true; headSeq = r.GetInt64(0); headHash = (byte[])r[1]; }
        }

        var docs = new List<(long Seq, byte[] Prev, byte[] Row, int Version, Guid Id, Guid? Journal, string Command, byte[]? Detail, Dictionary<string, object?> Body)>();
        await using (var cmd = tx.Command(
            "select chain_seq, prev_hash, row_hash, hash_version, stock_document_id, document_type, number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, document_date, posting_date, " +
            "posted_at, reason_code, reversal_of_stock_document_id, journal_document_id, soft_close_override, currency_tc, source_system, source_document_ref, idempotency_key, command_name, posted_by, " +
            "correlation_id, movement_count, detail_digest, movement_digest from core.stock_document where tenant_id = @t and company_id = @c order by chain_seq"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim();
                Guid? G(int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
                byte[]? detail = r.IsDBNull(26) ? null : (byte[])r[26];
                var body = new Dictionary<string, object?>
                {
                    ["stock_document_id"] = r.GetGuid(4), ["document_type"] = r.GetString(5), ["number_series_id"] = r.GetGuid(6), ["fiscal_year"] = (long)r.GetInt32(7), ["sequence_no"] = r.GetInt64(8),
                    ["document_number"] = r.GetString(9), ["fiscal_period"] = (long)r.GetInt32(10), ["document_date"] = r.GetFieldValue<DateOnly>(11), ["posting_date"] = r.GetFieldValue<DateOnly>(12),
                    ["posted_at"] = DateTime.SpecifyKind(r.GetDateTime(13), DateTimeKind.Utc), ["reason_code"] = Str(14), ["reversal_of_stock_document_id"] = G(15), ["journal_document_id"] = G(16),
                    ["soft_close_override"] = r.GetBoolean(17), ["currency_tc"] = Str(18), ["source_system"] = r.GetString(19), ["source_document_ref"] = Str(20), ["idempotency_key"] = r.GetString(21),
                    ["command_name"] = r.GetString(22), ["posted_by"] = r.GetString(23), ["correlation_id"] = r.GetString(24), ["movement_count"] = (long)r.GetInt32(25), ["detail_digest"] = detail,
                    ["movement_digest"] = (byte[])r[27], ["hash_version"] = (long)r.GetInt16(3)
                };
                docs.Add((r.GetInt64(0), (byte[])r[1], (byte[])r[2], r.GetInt16(3), r.GetGuid(4), G(16), r.GetString(22), detail, body));
            }
        }

        var movesOf = new Dictionary<Guid, List<Dictionary<string, object?>>>();
        await using (var cmd = tx.Command(
            "select stock_document_id, line_no, chain_seq, movement_type, cost_source, valuation_ref_document_id, valuation_ref_line_no, posting_date, item_id, warehouse_id, location_id, tracking_id, stock_status, " +
            "quantity, unit_code, base_quantity, value_lc, unit_cost_lc, value_tc, currency_tc, cost_warehouse_id, cost_qty_after, cost_value_after_lc, stock_qty_after, lot_expired, count_ref, expected_book_quantity, " +
            "source_line_ref, reversal_of_line_no from core.stock_movement where tenant_id = @t and company_id = @c order by stock_document_id, line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim();
                Guid? G(int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
                decimal? D(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
                Guid id = r.GetGuid(0);
                if (!movesOf.TryGetValue(id, out var list)) movesOf[id] = list = [];
                list.Add(new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = companyId, ["stock_document_id"] = id, ["line_no"] = (long)r.GetInt32(1), ["chain_seq"] = r.GetInt64(2), ["movement_type"] = r.GetString(3).Trim(),
                    ["cost_source"] = r.GetString(4).Trim(), ["valuation_ref_document_id"] = G(5), ["valuation_ref_line_no"] = r.IsDBNull(6) ? null : (long)r.GetInt32(6), ["posting_date"] = r.GetFieldValue<DateOnly>(7),
                    ["item_id"] = r.GetGuid(8), ["warehouse_id"] = r.GetGuid(9), ["location_id"] = G(10), ["tracking_id"] = G(11), ["stock_status"] = r.GetString(12).Trim(), ["quantity"] = r.GetDecimal(13),
                    ["unit_code"] = r.GetString(14).Trim(), ["base_quantity"] = r.GetDecimal(15), ["value_lc"] = r.GetDecimal(16), ["unit_cost_lc"] = D(17), ["value_tc"] = D(18), ["currency_tc"] = Str(19),
                    ["cost_warehouse_id"] = G(20), ["cost_qty_after"] = r.GetDecimal(21), ["cost_value_after_lc"] = r.GetDecimal(22), ["stock_qty_after"] = r.GetDecimal(23), ["lot_expired"] = r.GetBoolean(24),
                    ["count_ref"] = Str(25), ["expected_book_quantity"] = D(26), ["source_line_ref"] = Str(27), ["reversal_of_line_no"] = r.IsDBNull(28) ? null : (long)r.GetInt32(28)
                });
            }
        }

        var consumptionOf = new Dictionary<Guid, List<IReadOnlyDictionary<string, object?>>>();
        await using (var cmd = tx.Command("select stock_document_id, line_no, layer_doc_id, layer_line_no, quantity_base, value_lc from core.stock_layer_consumption where tenant_id = @t and company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                Guid id = r.GetGuid(0);
                if (!consumptionOf.TryGetValue(id, out var list)) consumptionOf[id] = list = [];
                list.Add(new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = companyId, ["stock_document_id"] = id, ["line_no"] = (long)r.GetInt32(1), ["layer_doc_id"] = r.GetGuid(2), ["layer_line_no"] = (long)r.GetInt32(3),
                    ["quantity_base"] = r.GetDecimal(4), ["value_lc"] = r.GetDecimal(5)
                });
            }
        }

        var journalDigest = new Dictionary<Guid, byte[]?>();
        await using (var cmd = tx.Command(
            "select document_id, detail_digest from core.journal_document where tenant_id = @t and company_id = @c and document_id in (select journal_document_id from core.stock_document where tenant_id = @t and company_id = @c and journal_document_id is not null)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) journalDigest[r.GetGuid(0)] = r.IsDBNull(1) ? null : (byte[])r[1];
        }

        byte[] prev = LedgerHash2.Genesis(tenantId, companyId, StockHash2.ChainType, "");
        long expected = 1;
        var cost = new Dictionary<(Guid, Guid?, Guid?), (decimal Qty, decimal Value)>();
        string[] valueMoving = ["RECEIPT", "ISSUE", "ADJUST_IN", "ADJUST_OUT", "COST_ADJUST", "RETURN_IN", "RETURN_OUT", "OPENING_IN", "TRANSFER_OUT", "TRANSFER_IN"];
        foreach (var d in docs)
        {
            if (d.Seq != expected) { problems.Add(new("CHAIN.SEQUENCE_GAP", d.Seq, $"expected chain_seq {expected}")); expected = d.Seq; }
            if (!d.Prev.AsSpan().SequenceEqual(prev)) problems.Add(new("CHAIN.PREV_HASH_MISMATCH", d.Seq, "prev_hash is not the row_hash of the predecessor"));
            var moves = movesOf.GetValueOrDefault(d.Id) ?? [];
            if (d.Version != StockHash2.Version) problems.Add(new("CHAIN.HASH_VERSION_UNSUPPORTED", d.Seq, $"hash_version {d.Version}"));
            else
            {
                if (moves.Count != (int)(long)d.Body["movement_count"]!) problems.Add(new("STOCK.MOVEMENT_COUNT_MISMATCH", d.Seq, $"{moves.Count} movements, the document says {d.Body["movement_count"]}"));
                if (moves.Count > 0)
                {
                    byte[] digest = StockHash2.MovementDigest(moves.Select(m => (IReadOnlyDictionary<string, object?>)m).ToList(), consumptionOf.GetValueOrDefault(d.Id));
                    if (!digest.AsSpan().SequenceEqual((byte[])d.Body["movement_digest"]!)) problems.Add(new("STOCK.MOVEMENT_DIGEST_MISMATCH", d.Seq, "recomputed movement_digest differs from the stored one"));
                }
                byte[] row = StockHash2.RowHash(tenantId, companyId, d.Seq, d.Prev, d.Body);
                if (!row.AsSpan().SequenceEqual(d.Row)) problems.Add(new("CHAIN.ROW_HASH_MISMATCH", d.Seq, "recomputed row_hash differs from the stored one"));
            }
            // what the journal document binds: the row hash of the stock document, or for a document derived by a purchase invoice the digest of its link digest and its row hash
            if (d.Journal is { } j && journalDigest.TryGetValue(j, out var bound))
            {
                bool external = d.Command is "purchase-invoices:post" or "purchase-invoices:reverse" or "purchase-credit-notes:post" or "purchase-credit-notes:reverse" && d.Detail is not null;
                byte[] wanted = external ? SHA256.HashData([.. d.Detail!, .. d.Row]) : d.Row;
                if (bound is null || !bound.AsSpan().SequenceEqual(wanted)) problems.Add(new("STOCK.DETAIL_DIGEST_MISMATCH", d.Seq, "the journal document does not bind this stock document"));
            }
            // the cost key follows from the movements that move value
            foreach (var m in moves)
            {
                if (!valueMoving.Contains((string)m["movement_type"]!)) continue;
                var item = ((Guid)m["item_id"]!, (Guid?)m["cost_warehouse_id"], (Guid?)m["tracking_id"]);
                var before = cost.GetValueOrDefault(item);
                decimal qty = before.Qty + (decimal)m["base_quantity"]!, value = before.Value + (decimal)m["value_lc"]!;
                if (qty != (decimal)m["cost_qty_after"]! || value != (decimal)m["cost_value_after_lc"]!)
                    problems.Add(new("STOCK.COST_KEY_MISMATCH", d.Seq, $"line {m["line_no"]}: the cost key after the movement is qty {m["cost_qty_after"]}, value {m["cost_value_after_lc"]}, the movements give qty {qty}, value {value}"));
                cost[item] = ((decimal)m["cost_qty_after"]!, (decimal)m["cost_value_after_lc"]!);
            }
            prev = d.Row;
            expected = d.Seq + 1;
        }

        // the FIFO layers: each belongs to a receiving movement, is not over-consumed, and the exhausted flag follows from the consumption
        await using (var cmd = tx.Command(
            "select l.stock_document_id, l.line_no, l.quantity_base, l.value_lc, l.exhausted, m.base_quantity, m.value_lc, " +
            "coalesce((select sum(c.quantity_base) from core.stock_layer_consumption c where c.tenant_id = l.tenant_id and c.company_id = l.company_id and c.layer_doc_id = l.stock_document_id and c.layer_line_no = l.line_no), 0), " +
            "coalesce((select sum(c.value_lc) from core.stock_layer_consumption c where c.tenant_id = l.tenant_id and c.company_id = l.company_id and c.layer_doc_id = l.stock_document_id and c.layer_line_no = l.line_no), 0), " +
            "coalesce((select sum(a.value_lc) from core.stock_layer_adjustment a where a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.layer_doc_id = l.stock_document_id and a.layer_line_no = l.line_no), 0), " +
            "(select d.chain_seq from core.stock_document d where d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.stock_document_id = l.stock_document_id) " +
            "from core.stock_cost_layer l join core.stock_movement m on m.tenant_id = l.tenant_id and m.company_id = l.company_id and m.stock_document_id = l.stock_document_id and m.line_no = l.line_no " +
            "where l.tenant_id = @t and l.company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                decimal qty = r.GetDecimal(2), value = r.GetDecimal(3), movementQty = r.GetDecimal(5), consumedQty = r.GetDecimal(7), consumedValue = r.GetDecimal(8), adjusted = r.GetDecimal(9);
                long seq = r.GetInt64(10);
                string where = $"layer {r.GetGuid(0):D}/{r.GetInt32(1)}";
                if (qty > movementQty || movementQty <= 0m) problems.Add(new("STOCK.LAYER_MISMATCH", seq, $"{where}: the layer holds {qty}, its movement {movementQty}"));
                if (consumedQty > qty || consumedValue > value + adjusted) problems.Add(new("STOCK.LAYER_MISMATCH", seq, $"{where}: consumed quantity {consumedQty} and value {consumedValue} exceed the layer"));
                if (r.GetBoolean(4) != (consumedQty >= qty)) problems.Add(new("STOCK.LAYER_EXHAUSTED_MISMATCH", seq, $"{where}: the exhausted flag does not follow from the consumption"));
            }
        }

        if (headFound)
        {
            long newest = docs.Count == 0 ? 0 : docs[^1].Seq;
            if (headSeq != newest) problems.Add(new("CHAIN.HEAD_MISMATCH", headSeq, $"head sequence {headSeq}, newest stock document {newest}"));
            else if (!headHash!.AsSpan().SequenceEqual(prev)) problems.Add(new("CHAIN.HEAD_HASH_MISMATCH", headSeq, "head hash differs from the newest row_hash"));
        }
        else if (docs.Count > 0) problems.Add(new("CHAIN.HEAD_MISSING", null, "no STOCK chain head"));

        return new VerificationResult(problems.Count == 0, docs.Count, problems);
    }
}
