using System.Globalization;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Masters;

// ---- invoices:record-issue (12, 11, 22 delta 1, permission sales.invoice): the evidence that a file of an invoice or credit note went out.
/// <summary>Exactly one of InvoiceId (link record) and DocumentId (journal document of a direct invoice). IssuedAt defaults to the time of the command.</summary>
public sealed record RecordInvoiceIssueRequest(
    Guid CompanyId, string Format, string DocumentReference, string DocumentSha256, string DeliveryChannel, string? InvoiceKind = null, Guid? InvoiceId = null, Guid? DocumentId = null,
    DateTimeOffset? IssuedAt = null);

public sealed record InvoiceIssueResult(Guid IssueId, string InvoiceKind, Guid? InvoiceId, Guid? DocumentId, DateTimeOffset IssuedAt);

public static class InvoiceIssueRules
{
    public const string Permission = InvoiceRules.SalesPermission;
    public static readonly string[] Kinds = ["SALES", "PURCHASE"];
    public static readonly string[] Channels = ["EMAIL", "PEPPOL", "PORTAL", "POST", "OTHER"];

    /// <summary>The journal document types an issue record may name when it names a document (12, 11), and the kind they imply.</summary>
    public static string? KindOfDocumentType(string documentType) => documentType switch
    {
        "AR_INVOICE" or "AR_CREDIT" => "SALES",
        "AP_INVOICE" or "AP_CREDIT" => "PURCHASE",
        _ => null
    };

    /// <summary>The SHA-256 as 64 hexadecimal characters (either case); null when it is not.</summary>
    public static byte[]? ParseSha256(string? hex) =>
        hex is { Length: 64 } && hex.All(Uri.IsHexDigit) ? Convert.FromHexString(hex) : null;

    public static byte[] Fingerprint(RecordInvoiceIssueRequest r) =>
        MasterRules.Fingerprint("invoices:record-issue", string.Join("|", r.CompanyId.ToString("D"), r.InvoiceKind ?? "-", r.InvoiceId?.ToString("D") ?? "-", r.DocumentId?.ToString("D") ?? "-", r.Format,
            r.DocumentReference, r.DocumentSha256.ToLowerInvariant(), r.DeliveryChannel, r.IssuedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "-"));
}
