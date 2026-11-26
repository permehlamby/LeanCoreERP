using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using Npgsql;

namespace FinanceCore.Data;

public sealed partial class ChainVerifier
{
    // the four tables of the FULFILMENT chain: the columns that differ, and the command that wrote a document of the table
    private sealed record FulfilmentTable(string DocumentType, string Header, string Line, string Id, string Number, string Date, string Party, string Reversal, string Ref1, string Ref2, string PostCommand, string ReverseCommand, bool Return, string LineColumns);

    private static readonly FulfilmentTable[] FulfilmentTables =
    [
        new(FulfilmentHash2.Delivery, "sales_delivery", "sales_delivery_line", "delivery_id", "delivery_number", "delivery_date", "customer_partner_id", "reversal_of_delivery_id", "carrier_reference", "tracking_reference",
            "sales-deliveries:post", "sales-deliveries:reverse", false,
            "sales_order_id, sales_order_line_no, item_id, quantity, unit_code, base_quantity, warehouse_id, location_id, tracking_id, stock_status, stock_document_line_no, null::numeric, null::text, case when stock_document_line_no is null then 0 else 1 end, value_lc"),
        new(FulfilmentHash2.Receipt, "purchase_receipt", "purchase_receipt_line", "receipt_id", "receipt_number", "receipt_date", "supplier_partner_id", "reversal_of_receipt_id", "delivery_note_reference", "null::text",
            "purchase-receipts:post", "purchase-receipts:reverse", false,
            "purchase_order_id, purchase_order_line_no, item_id, quantity, unit_code, base_quantity, warehouse_id, location_id, tracking_id, stock_status, stock_document_line_no, unit_price, currency, stock_document_line_count, value_lc"),
        new(FulfilmentHash2.SalesReturn, "sales_return", "sales_return_line", "return_id", "return_number", "return_date", "customer_partner_id", "reversal_of_return_id", "null::text", "null::text",
            "sales-returns:post", "sales-returns:reverse", true,
            "delivery_id, delivery_line_no, sales_order_id, sales_order_line_no, item_id, quantity, unit_code, base_quantity, reason_code, disposition, warehouse_id, location_id, tracking_id, stock_status, stock_document_line_no, value_lc, dropship_confirmation_id"),
        new(FulfilmentHash2.PurchaseReturn, "purchase_return", "purchase_return_line", "return_id", "return_number", "return_date", "supplier_partner_id", "reversal_of_return_id", "null::text", "null::text",
            "purchase-returns:post", "purchase-returns:reverse", true,
            "receipt_id, receipt_line_no, purchase_order_id, purchase_order_line_no, item_id, quantity, unit_code, base_quantity, reason_code, disposition, warehouse_id, location_id, tracking_id, stock_status, stock_document_line_no, value_lc, dropship_confirmation_id")
    ];

    /// <summary>
    /// chains:verify for the FULFILMENT chain (11, 5.5): the one chain over the sales deliveries, purchase receipts, sales returns and purchase returns of a company. It recomputes the line digest and the
    /// row hash of every document from the stored columns, checks the links across the four tables, the gapless chain_seq, the head, the line count, and that the stock document of a document binds the
    /// row hash of the document as its detail_digest.
    /// </summary>
    public async Task<VerificationResult> VerifyFulfilmentAsync(Guid tenantId, Guid companyId, CancellationToken ct = default)
    {
        var problems = new List<VerificationProblem>();
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);

        long headSeq = 0; byte[]? headHash = null; bool headFound = false;
        await using (var cmd = tx.Command("select last_seq, last_hash from core.chain_head where tenant_id = @t and company_id = @c and chain_type = 'FULFILMENT' and scope_key = ''"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { headFound = true; headSeq = r.GetInt64(0); headHash = (byte[])r[1]; }
        }

        var docs = new List<(long Seq, byte[] Prev, byte[] Row, int Version, int LineCount, Guid? Stock, Guid Id, FulfilmentTable Table, Dictionary<string, object?> Body, List<Dictionary<string, object?>> Lines)>();
        foreach (FulfilmentTable table in FulfilmentTables)
        {
            var headers = new List<(long Seq, byte[] Prev, byte[] Row, int Version, int LineCount, Guid? Stock, Guid Id, Dictionary<string, object?> Body)>();
            await using (var cmd = tx.Command(
                $"select chain_seq, prev_hash, row_hash, hash_version, {table.Id}, {table.Number}, number_series_id, fiscal_year, sequence_no, {table.Date}, {table.Party}, warehouse_id, {table.Ref1}, {table.Ref2}, " +
                $"stock_document_id, {table.Reversal}, reason_code, origin, migration_batch_id, source_system, source_document_ref, posted_at, idempotency_key, posted_by, correlation_id, line_count, line_digest " +
                $"from core.{table.Header} where tenant_id = @t and company_id = @c"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim();
                    Guid? G(int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
                    Guid? reversalOf = G(15);
                    var body = new Dictionary<string, object?>
                    {
                        ["document_id"] = r.GetGuid(4), ["document_type"] = table.DocumentType, ["number_series_id"] = r.GetGuid(6), ["fiscal_year"] = (long)r.GetInt32(7), ["sequence_no"] = r.GetInt64(8),
                        ["document_number"] = r.GetString(5), ["document_date"] = r.GetFieldValue<DateOnly>(9), ["partner_id"] = r.GetGuid(10), ["warehouse_id"] = G(11), ["reference_1"] = Str(12), ["reference_2"] = Str(13),
                        ["stock_document_id"] = G(14), ["reversal_of_id"] = reversalOf, ["reason_code"] = Str(16), ["origin"] = r.GetString(17).Trim(), ["migration_batch_id"] = G(18), ["source_system"] = r.GetString(19).Trim(),
                        ["source_document_ref"] = Str(20), ["posted_at"] = DateTime.SpecifyKind(r.GetDateTime(21), DateTimeKind.Utc), ["idempotency_key"] = r.GetString(22),
                        ["command_name"] = reversalOf is null ? table.PostCommand : table.ReverseCommand, ["posted_by"] = r.GetString(23), ["correlation_id"] = r.GetString(24), ["line_count"] = (long)r.GetInt32(25),
                        ["line_digest"] = (byte[])r[26], ["hash_version"] = (long)r.GetInt16(3)
                    };
                    headers.Add((r.GetInt64(0), (byte[])r[1], (byte[])r[2], r.GetInt16(3), r.GetInt32(25), G(14), r.GetGuid(4), body));
                }
            }
            var linesOf = new Dictionary<Guid, List<Dictionary<string, object?>>>();
            await using (var cmd = tx.Command($"select {table.Id}, line_no, {table.LineColumns} from core.{table.Line} where tenant_id = @t and company_id = @c order by {table.Id}, line_no"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim();
                    Guid? G(int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
                    decimal? D(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
                    long? N(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
                    Guid id = r.GetGuid(0);
                    var line = new Dictionary<string, object?> { ["tenant_id"] = tenantId, ["company_id"] = companyId, ["document_id"] = id, ["line_no"] = (long)r.GetInt32(1) };
                    if (table.Return)
                    {
                        line["source_document_id"] = r.GetGuid(2); line["source_line_no"] = (long)r.GetInt32(3); line["order_id"] = r.GetGuid(4); line["order_line_no"] = (long)r.GetInt32(5); line["item_id"] = r.GetGuid(6);
                        line["quantity"] = r.GetDecimal(7); line["unit_code"] = r.GetString(8).Trim(); line["base_quantity"] = r.GetDecimal(9); line["reason_code"] = r.GetString(10).Trim(); line["disposition"] = r.GetString(11).Trim();
                        line["warehouse_id"] = G(12); line["location_id"] = G(13); line["tracking_id"] = G(14); line["stock_status"] = r.GetString(15).Trim(); line["stock_document_line_no"] = N(16);
                        line["value_lc"] = D(17); line["dropship_confirmation_id"] = G(18);
                    }
                    else
                    {
                        line["order_id"] = r.GetGuid(2); line["order_line_no"] = (long)r.GetInt32(3); line["item_id"] = r.GetGuid(4); line["quantity"] = r.GetDecimal(5); line["unit_code"] = r.GetString(6).Trim();
                        line["base_quantity"] = r.GetDecimal(7); line["warehouse_id"] = G(8); line["location_id"] = G(9); line["tracking_id"] = G(10); line["stock_status"] = r.GetString(11).Trim();
                        line["stock_document_line_no"] = N(12); line["unit_price"] = D(13); line["currency"] = Str(14); line["stock_document_line_count"] = N(15); line["value_lc"] = D(16);
                    }
                    if (!linesOf.TryGetValue(id, out var list)) linesOf[id] = list = [];
                    list.Add(line);
                }
            }
            foreach (var h in headers) docs.Add((h.Seq, h.Prev, h.Row, h.Version, h.LineCount, h.Stock, h.Id, table, h.Body, linesOf.GetValueOrDefault(h.Id) ?? []));
        }
        docs.Sort((a, b) => a.Seq.CompareTo(b.Seq));

        var stockDigest = new Dictionary<Guid, byte[]?>();
        await using (var cmd = tx.Command("select stock_document_id, detail_digest from core.stock_document where tenant_id = @t and company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) stockDigest[r.GetGuid(0)] = r.IsDBNull(1) ? null : (byte[])r[1];
        }

        byte[] prev = LedgerHash2.Genesis(tenantId, companyId, FulfilmentHash2.ChainType, "");
        long expected = 1;
        foreach (var d in docs)
        {
            if (d.Seq != expected) { problems.Add(new("CHAIN.SEQUENCE_GAP", d.Seq, $"expected chain_seq {expected}")); expected = d.Seq; }
            if (!d.Prev.AsSpan().SequenceEqual(prev)) problems.Add(new("CHAIN.PREV_HASH_MISMATCH", d.Seq, "prev_hash is not the row_hash of the predecessor"));
            if (d.Version != FulfilmentHash2.Version) problems.Add(new("CHAIN.HASH_VERSION_UNSUPPORTED", d.Seq, $"hash_version {d.Version}"));
            else
            {
                if (d.Lines.Count != d.LineCount) problems.Add(new("FULFILMENT.LINE_COUNT_MISMATCH", d.Seq, $"{d.Lines.Count} lines, the document says {d.LineCount}"));
                if (d.Lines.Count > 0)
                {
                    var rows = d.Lines.Select(l => (IReadOnlyDictionary<string, object?>)l).ToList();
                    byte[] digest = d.Table.Return ? FulfilmentHash2.ReturnLineDigest(rows) : FulfilmentHash2.LineDigest(rows);
                    if (!digest.AsSpan().SequenceEqual((byte[])d.Body["line_digest"]!)) problems.Add(new("FULFILMENT.LINE_DIGEST_MISMATCH", d.Seq, "recomputed line_digest differs from the stored one"));
                }
                byte[] row = FulfilmentHash2.RowHash(tenantId, companyId, d.Seq, d.Prev, d.Body);
                if (!row.AsSpan().SequenceEqual(d.Row)) problems.Add(new("CHAIN.ROW_HASH_MISMATCH", d.Seq, "recomputed row_hash differs from the stored one"));
            }
            // the stock document that the fulfilment document derives binds its row hash
            if (d.Stock is { } s && stockDigest.TryGetValue(s, out var bound) && (bound is null || !bound.AsSpan().SequenceEqual(d.Row)))
                problems.Add(new("FULFILMENT.DETAIL_DIGEST_MISMATCH", d.Seq, "the stock document does not bind the row hash of this document"));
            prev = d.Row;
            expected = d.Seq + 1;
        }

        if (headFound)
        {
            long newest = docs.Count == 0 ? 0 : docs[^1].Seq;
            if (headSeq != newest) problems.Add(new("CHAIN.HEAD_MISMATCH", headSeq, $"head sequence {headSeq}, newest fulfilment document {newest}"));
            else if (!headHash!.AsSpan().SequenceEqual(prev)) problems.Add(new("CHAIN.HEAD_HASH_MISMATCH", headSeq, "head hash differs from the newest row_hash"));
        }
        else if (docs.Count > 0) problems.Add(new("CHAIN.HEAD_MISSING", null, "no FULFILMENT chain head"));

        return new VerificationResult(problems.Count == 0, docs.Count, problems);
    }
}
