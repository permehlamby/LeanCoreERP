using System.Security.Cryptography;
using System.Text;

namespace FinanceCore.Core.Hashing;

/// <summary>
/// The row hash of a stock document, hash_version 2 (10, 4.1, 4.2). The same scheme as the ledger, clearing and period event chains: the hash version, tenant, company, chain_seq and
/// the hex text of prev_hash are outer fields, the L1 columns of the document are one block (the status, the reversal pointer S column and the header text are outside), then the
/// digest of the movements. The derived journal id is part of the block and its detail_digest is this row hash, so the journal binds the stock document and not the other way round.
/// </summary>
public static class StockHash2
{
    public const int Version = 2;
    public const string ChainType = "STOCK";

    private static FieldSpec Id(string n, bool nullable = false) => new(n, FieldType.Id, nullable);
    private static FieldSpec Code(string n, bool nullable = false) => new(n, FieldType.Text, nullable);
    private static FieldSpec Int(string n, bool nullable = false) => new(n, FieldType.Int, nullable);
    private static FieldSpec Date(string n) => new(n, FieldType.Date);
    private static FieldSpec Amt(string n, bool nullable = false) => new(n, FieldType.Amount, nullable);
    private static FieldSpec Qty(string n, bool nullable = false) => new(n, FieldType.Rate, nullable);

    /// <summary>The L1 columns of stock_document in table order, without the outer fields (tenant, company, chain_seq, prev_hash), row_hash, status, the S pointer and header_text.</summary>
    public static readonly IReadOnlyList<FieldSpec> Body =
    [
        Id("stock_document_id"), Code("document_type"), Id("number_series_id"), Int("fiscal_year"), Int("sequence_no"), Code("document_number"), Int("fiscal_period"),
        Date("document_date"), Date("posting_date"), new("posted_at", FieldType.Timestamp), Code("reason_code", true), Id("reversal_of_stock_document_id", true),
        Id("journal_document_id", true), new("soft_close_override", FieldType.Bool), Code("currency_tc", true), Code("source_system"), Code("source_document_ref", true),
        Code("idempotency_key"), Code("command_name"), Code("posted_by"), Code("correlation_id"), Int("movement_count"),
        new("detail_digest", FieldType.Hex32, true), new("movement_digest", FieldType.Hex32), Int("hash_version")
    ];

    /// <summary>The L1 columns of stock_movement in table order, without line_text.</summary>
    public static readonly IReadOnlyList<FieldSpec> Movement =
    [
        Id("tenant_id"), Id("company_id"), Id("stock_document_id"), Int("line_no"), new("chain_seq", FieldType.Int), Code("movement_type"), Code("cost_source"),
        Id("valuation_ref_document_id", true), Int("valuation_ref_line_no", true), Date("posting_date"), Id("item_id"), Id("warehouse_id"), Id("location_id", true), Id("tracking_id", true),
        Code("stock_status"), Qty("quantity"), Code("unit_code"), Qty("base_quantity"), Amt("value_lc"), Qty("unit_cost_lc", true), Amt("value_tc", true), Code("currency_tc", true),
        Id("cost_warehouse_id", true), Qty("cost_qty_after"), Amt("cost_value_after_lc"), Qty("stock_qty_after"), new("lot_expired", FieldType.Bool), Code("count_ref", true),
        Qty("expected_book_quantity", true), Code("source_line_ref", true), Int("reversal_of_line_no", true)
    ];

    /// <summary>The L1 columns of stock_layer_consumption in table order (10, 4.1: the movement digest covers the movements and the consumption rows).</summary>
    public static readonly IReadOnlyList<FieldSpec> Consumption =
    [
        Id("tenant_id"), Id("company_id"), Id("stock_document_id"), Int("line_no"), Id("layer_doc_id"), Int("layer_line_no"), Qty("quantity_base"), Amt("value_lc")
    ];

    /// <summary>
    /// SHA-256 of one block per movement, in line_no order, each followed by the blocks of its consumption rows (in the order of layer_doc_id and layer_line_no). The caller passes the movements in
    /// line order and the consumption rows of the document (a document without FIFO consumption has none, and its digest is the digest of the movements alone). At least one movement.
    /// </summary>
    public static byte[] MovementDigest(IReadOnlyList<IReadOnlyDictionary<string, object?>> movementsInLineOrder, IReadOnlyList<IReadOnlyDictionary<string, object?>>? consumptions = null)
    {
        if (movementsInLineOrder.Count == 0) throw new ArgumentException("A stock document has at least one movement");
        using var buffer = new MemoryStream();
        foreach (var movement in movementsInLineOrder)
        {
            buffer.Write(Canonical.Block(Canonical.EncodeFields(Movement, movement)));
            long line = Convert.ToInt64(movement["line_no"], System.Globalization.CultureInfo.InvariantCulture);
            foreach (var c in (consumptions ?? []).Where(c => Convert.ToInt64(c["line_no"], System.Globalization.CultureInfo.InvariantCulture) == line)
                         .OrderBy(c => c["layer_doc_id"]!.ToString(), StringComparer.Ordinal).ThenBy(c => Convert.ToInt64(c["layer_line_no"], System.Globalization.CultureInfo.InvariantCulture)))
                buffer.Write(Canonical.Block(Canonical.EncodeFields(Consumption, c)));
        }
        return SHA256.HashData(buffer.ToArray());
    }

    public static byte[] RowHash(Guid tenantId, Guid companyId, long chainSeq, byte[] prevHash, IReadOnlyDictionary<string, object?> body)
    {
        using var buffer = new MemoryStream();
        Write(buffer, Encoding.UTF8.GetBytes(Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Write(buffer, Encoding.UTF8.GetBytes(tenantId.ToString("D").ToLowerInvariant()));
        Write(buffer, Encoding.UTF8.GetBytes(companyId.ToString("D").ToLowerInvariant()));
        Write(buffer, Encoding.UTF8.GetBytes(chainSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Write(buffer, Encoding.UTF8.GetBytes(Canonical.FormatHex32(prevHash)));
        buffer.Write(Canonical.Block(Canonical.EncodeFields(Body, body)));
        return SHA256.HashData(buffer.ToArray());
    }

    private static void Write(Stream s, byte[] value) => s.Write(Canonical.Field(value));
}
