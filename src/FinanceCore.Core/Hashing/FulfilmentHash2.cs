using System.Security.Cryptography;
using System.Text;

namespace FinanceCore.Core.Hashing;

/// <summary>
/// The row hash of a fulfilment document (a sales delivery or a purchase receipt, 11, 5.5), hash_version 2. The same scheme as the other chains: the hash version, tenant, company,
/// chain_seq and the hex text of prev_hash are outer fields, the L1 columns of the header are one block (the status is outside), then the digest of the lines. The derived stock document
/// is named by its id; the stock document in turn binds this row hash as its detail_digest, so there is no circle. One block layout serves both kinds, a column that a kind does not
/// have is NULL.
/// </summary>
public static class FulfilmentHash2
{
    public const int Version = 2;
    public const string ChainType = "FULFILMENT";
    public const string Delivery = "SALES_DELIVERY", Receipt = "PURCHASE_RECEIPT", SalesReturn = "SALES_RETURN", PurchaseReturn = "PURCHASE_RETURN", DropshipConfirmation = "DROPSHIP_CONFIRMATION";

    private static FieldSpec Id(string n, bool nullable = false) => new(n, FieldType.Id, nullable);
    private static FieldSpec Code(string n, bool nullable = false) => new(n, FieldType.Text, nullable);
    private static FieldSpec Int(string n, bool nullable = false) => new(n, FieldType.Int, nullable);
    private static FieldSpec Qty(string n) => new(n, FieldType.Rate);

    /// <summary>The L1 columns of the header in table order, without the outer fields, the status and the hash columns. reference_1 is the carrier reference or the supplier's delivery note, reference_2 the tracking reference.</summary>
    public static readonly IReadOnlyList<FieldSpec> Body =
    [
        Id("document_id"), Code("document_type"), Id("number_series_id"), Int("fiscal_year"), Int("sequence_no"), Code("document_number"), new("document_date", FieldType.Date),
        Id("partner_id"), Id("warehouse_id", true), Code("reference_1", true), Code("reference_2", true), Id("stock_document_id", true), Id("reversal_of_id", true),
        Code("reason_code", true), Code("origin"), Id("migration_batch_id", true), Code("source_system"), Code("source_document_ref", true), new("posted_at", FieldType.Timestamp),
        Code("idempotency_key"), Code("command_name"), Code("posted_by"), Code("correlation_id"), Int("line_count"), new("line_digest", FieldType.Hex32), Int("hash_version")
    ];

    /// <summary>The L1 columns of a line in table order.</summary>
    public static readonly IReadOnlyList<FieldSpec> Line =
    [
        Id("tenant_id"), Id("company_id"), Id("document_id"), Int("line_no"), Id("order_id"), Int("order_line_no"), Id("item_id"), Qty("quantity"), Code("unit_code"), Qty("base_quantity"),
        Id("warehouse_id", true), Id("location_id", true), Id("tracking_id", true), Code("stock_status"), Int("stock_document_line_no", true), Int("stock_document_line_count", true),
        new("unit_price", FieldType.Amount, true), Code("currency", true), new("value_lc", FieldType.Amount, true)
    ];

    /// <summary>The L1 columns of a return line in table order (11, 5.4): the delivery or receipt line it returns and the order line, the reason and the disposition.</summary>
    public static readonly IReadOnlyList<FieldSpec> ReturnLine =
    [
        Id("tenant_id"), Id("company_id"), Id("document_id"), Int("line_no"), Id("source_document_id"), Int("source_line_no"), Id("order_id"), Int("order_line_no"), Id("item_id"),
        Qty("quantity"), Code("unit_code"), Qty("base_quantity"), Code("reason_code"), Code("disposition"), Id("warehouse_id", true), Id("location_id", true), Id("tracking_id", true),
        Code("stock_status"), Int("stock_document_line_no", true), new("value_lc", FieldType.Amount, true), Id("dropship_confirmation_id", true)
    ];

    /// <summary>SHA-256 of one block per return line, in line_no order. At least one line.</summary>
    public static byte[] ReturnLineDigest(IReadOnlyList<IReadOnlyDictionary<string, object?>> linesInOrder)
    {
        if (linesInOrder.Count == 0) throw new ArgumentException("A return has at least one line");
        using var buffer = new MemoryStream();
        foreach (var line in linesInOrder) buffer.Write(Canonical.Block(Canonical.EncodeFields(ReturnLine, line)));
        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>SHA-256 of one block per line, in line_no order. At least one line.</summary>
    public static byte[] LineDigest(IReadOnlyList<IReadOnlyDictionary<string, object?>> linesInOrder)
    {
        if (linesInOrder.Count == 0) throw new ArgumentException("A fulfilment document has at least one line");
        using var buffer = new MemoryStream();
        foreach (var line in linesInOrder) buffer.Write(Canonical.Block(Canonical.EncodeFields(Line, line)));
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
