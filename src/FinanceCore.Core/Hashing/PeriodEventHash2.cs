using System.Security.Cryptography;
using System.Text;

namespace FinanceCore.Core.Hashing;

/// <summary>
/// The row hash of a period_event, hash_version 2 (04, 3.5 and 3.5a). Same scheme as the ledger chain (02, 9.3a):
/// the hash version, tenant, company, event_seq and the hex text of prev_hash are outer fields, and the remaining
/// columns of the row are one block in table order. The hash columns themselves are not part of it.
/// </summary>
public static class PeriodEventHash2
{
    public const int Version = 2;
    public const string ChainType = "PERIOD_EVENT";

    private static FieldSpec Id(string n, bool nullable = false) => new(n, FieldType.Id, nullable);
    private static FieldSpec Code(string n, bool nullable = false) => new(n, FieldType.Text, nullable);
    private static FieldSpec Int(string n, bool nullable = false) => new(n, FieldType.Int, nullable);

    /// <summary>The columns of period_event between event_seq and the hash columns, in the order of 04, 3.4.</summary>
    public static readonly IReadOnlyList<FieldSpec> Body =
    [
        Id("event_id"), Int("fiscal_year"), Int("period_no", true), Code("event_type"), Code("from_status", true),
        Code("to_status"), Code("reason_code", true), Code("reason_text", true), Code("authorisation", true),
        Id("document_id", true), new("event_at", FieldType.Timestamp), Code("event_by"), Code("correlation_id"),
        Code("idempotency_key")
    ];

    public static byte[] RowHash(Guid tenantId, Guid companyId, long eventSeq, byte[] prevHash, IReadOnlyDictionary<string, object?> body)
    {
        using var buffer = new MemoryStream();
        Write(buffer, Encoding.UTF8.GetBytes(Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Write(buffer, Encoding.UTF8.GetBytes(tenantId.ToString("D").ToLowerInvariant()));
        Write(buffer, Encoding.UTF8.GetBytes(companyId.ToString("D").ToLowerInvariant()));
        Write(buffer, Encoding.UTF8.GetBytes(eventSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Write(buffer, Encoding.UTF8.GetBytes(Canonical.FormatHex32(prevHash)));
        buffer.Write(Canonical.Block(Canonical.EncodeFields(Body, body)));
        return SHA256.HashData(buffer.ToArray());
    }

    private static void Write(Stream s, byte[] value) => s.Write(Canonical.Field(value));
}
