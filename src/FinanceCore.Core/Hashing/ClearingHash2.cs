using System.Security.Cryptography;
using System.Text;

namespace FinanceCore.Core.Hashing;

/// <summary>
/// The row hash of a clearing, hash_version 2 (03, 6 and 6.1). Same scheme as the ledger chain and the period event chain:
/// the hash version, tenant, company, chain_seq and the hex text of prev_hash are outer fields, the L1 columns of the clearing
/// are one block, then the digest of the items and the row hash of the difference document (a NULL field when there is none).
/// </summary>
public static class ClearingHash2
{
    public const int Version = 2;
    public const string ChainType = "CLEARING";

    private static FieldSpec Id(string n, bool nullable = false) => new(n, FieldType.Id, nullable);
    private static FieldSpec Code(string n, bool nullable = false) => new(n, FieldType.Text, nullable);
    private static FieldSpec Int(string n) => new(n, FieldType.Int);
    private static FieldSpec Date(string n) => new(n, FieldType.Date);
    private static FieldSpec Amt(string n, bool nullable = false) => new(n, FieldType.Amount, nullable);

    /// <summary>The L1 columns of clearing (03, 4.1) in table order, without the outer fields, the hash columns and the L2 column note.</summary>
    public static readonly IReadOnlyList<FieldSpec> Body =
    [
        Id("clearing_id"), Code("clearing_type"), Date("clearing_date"), Int("fiscal_year"), Int("fiscal_period"),
        new("cleared_at", FieldType.Timestamp), Id("account_id"), Id("partner_id", true), Id("document_id", true),
        Id("reset_of_clearing_id", true), Code("clearing_method"), Code("matching_ref", true), Code("source_system"),
        Code("idempotency_key"), Code("command_name"), Code("cleared_by"), Code("correlation_id")
    ];

    /// <summary>The L1 columns of clearing_item (03, 4.2) in table order.</summary>
    public static readonly IReadOnlyList<FieldSpec> Item =
    [
        Id("tenant_id"), Id("company_id"), Id("clearing_id"), Int("item_no"), Date("clearing_date"),
        Id("open_item_document_id"), Int("open_item_line_no"), Code("currency_tc"),
        Amt("cleared_amount_tc"), Amt("cleared_amount_lc"), Amt("cleared_amount_gc"), Amt("cleared_amount_rc", true)
    ];

    /// <summary>SHA-256 of one block per item, in item_no order. The caller passes the items in that order. At least one item.</summary>
    public static byte[] ItemsDigest(IReadOnlyList<IReadOnlyDictionary<string, object?>> itemsInItemNoOrder)
    {
        if (itemsInItemNoOrder.Count == 0) throw new ArgumentException("A clearing has at least one item");
        using var buffer = new MemoryStream();
        foreach (var item in itemsInItemNoOrder)
            buffer.Write(Canonical.Block(Canonical.EncodeFields(Item, item)));
        return SHA256.HashData(buffer.ToArray());
    }

    /// <param name="linkedDocumentRowHash">The row_hash of the difference document, or null when the clearing has none.</param>
    public static byte[] RowHash(
        Guid tenantId, Guid companyId, long chainSeq, byte[] prevHash, IReadOnlyDictionary<string, object?> body,
        byte[] itemsDigest, byte[]? linkedDocumentRowHash)
    {
        using var buffer = new MemoryStream();
        Write(buffer, Encoding.UTF8.GetBytes(Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Write(buffer, Encoding.UTF8.GetBytes(tenantId.ToString("D").ToLowerInvariant()));
        Write(buffer, Encoding.UTF8.GetBytes(companyId.ToString("D").ToLowerInvariant()));
        Write(buffer, Encoding.UTF8.GetBytes(chainSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Write(buffer, Encoding.UTF8.GetBytes(Canonical.FormatHex32(prevHash)));
        buffer.Write(Canonical.Block(Canonical.EncodeFields(Body, body)));
        Write(buffer, Encoding.UTF8.GetBytes(Canonical.FormatHex32(itemsDigest)));
        if (linkedDocumentRowHash is null) buffer.Write(Canonical.NullField());
        else Write(buffer, Encoding.UTF8.GetBytes(Canonical.FormatHex32(linkedDocumentRowHash)));
        return SHA256.HashData(buffer.ToArray());
    }

    private static void Write(Stream s, byte[] value) => s.Write(Canonical.Field(value));
}
