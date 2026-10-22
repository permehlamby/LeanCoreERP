using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Posting;

/// <summary>Payload of journal-documents:post as far as the first slice supports it (06, 5.2): no tax, no partners, no open items.</summary>
public sealed record PostJournalDocumentRequest(
    Guid CompanyId,
    Guid? LedgerId,
    string DocumentType,
    DateOnly DocumentDate,
    DateOnly PostingDate,
    string TransactionCurrency,
    string? HeaderText,
    string? ExternalReference,
    string? SourceDocumentRef,
    IReadOnlyList<RequestLine> Lines);

public sealed record RequestLine(string LineRef, Guid AccountId, decimal AmountTc, string? LineText = null);

public sealed record PostedLine(
    int LineNo, string? LineRef, Guid AccountId, string LineType,
    decimal AmountTc, decimal AmountLc, string CurrencyLc, decimal RateLc,
    decimal AmountGc, string CurrencyGc, decimal RateGc,
    decimal? AmountRc, string? CurrencyRc, decimal? RateRc,
    string RateType, DateOnly RateDate);

public sealed record PostedDocument(
    Guid DocumentId, Guid CompanyId, Guid LedgerId, string DocumentType, string DocumentNumber, long SequenceNo,
    int FiscalYear, int FiscalPeriod, DateOnly DocumentDate, DateOnly PostingDate, string TransactionCurrency,
    string Status, bool SoftCloseOverride, long ChainSeq, string RowHash, string PrevHash, int HashVersion,
    IReadOnlyList<PostedLine> Lines);

/// <summary>The fingerprint of a request for the idempotency check (06, 4.1): SHA-256 over the canonical payload,
/// without the correlation id, the mode and the idempotency key.</summary>
public static class RequestFingerprint
{
    private static readonly FieldSpec[] Header =
    [
        new("company_id", FieldType.Id), new("ledger_id", FieldType.Id, true), new("document_type", FieldType.Text),
        new("document_date", FieldType.Date), new("posting_date", FieldType.Date), new("transaction_currency", FieldType.Text),
        new("header_text", FieldType.Text, true), new("external_reference", FieldType.Text, true),
        new("source_document_ref", FieldType.Text, true)
    ];

    private static readonly FieldSpec[] Line =
    [
        new("line_ref", FieldType.Text), new("account_id", FieldType.Id), new("amount_tc", FieldType.Amount),
        new("line_text", FieldType.Text, true)
    ];

    public static byte[] Compute(PostJournalDocumentRequest r)
    {
        var head = Canonical.EncodeFields(Header, new Dictionary<string, object?>
        {
            ["company_id"] = r.CompanyId, ["ledger_id"] = r.LedgerId, ["document_type"] = r.DocumentType,
            ["document_date"] = r.DocumentDate, ["posting_date"] = r.PostingDate,
            ["transaction_currency"] = r.TransactionCurrency, ["header_text"] = r.HeaderText,
            ["external_reference"] = r.ExternalReference, ["source_document_ref"] = r.SourceDocumentRef
        });
        using var all = new MemoryStream();
        all.Write(head);
        foreach (RequestLine l in r.Lines)   // request order matters: it assigns the line numbers
            all.Write(Canonical.Block(Canonical.EncodeFields(Line, new Dictionary<string, object?>
            {
                ["line_ref"] = l.LineRef, ["account_id"] = l.AccountId, ["amount_tc"] = l.AmountTc, ["line_text"] = l.LineText
            })));
        return System.Security.Cryptography.SHA256.HashData(all.ToArray());
    }
}

/// <summary>Rounding and number formatting rules.</summary>
public static class PostingRules
{
    /// <summary>ROUND of the specs (03, 05, 08, 19) is not defined there. Half away from zero is used: it is
    /// symmetric, so a line and its mirror always round to opposite amounts. Finding F-IMPL-6.</summary>
    public static decimal Round(decimal value, int scale) => Math.Round(value, scale, MidpointRounding.AwayFromZero);

    /// <summary>Renders document_number from the series format (04, 5.1): prefix, fiscal year, separator, zero-padded number.</summary>
    public static string FormatNumber(string? prefix, bool includeFiscalYear, string? separator, int numberLength, int fiscalYear, long sequenceNo)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(prefix)) parts.Add(prefix);
        if (includeFiscalYear) parts.Add(fiscalYear.ToString(System.Globalization.CultureInfo.InvariantCulture));
        parts.Add(sequenceNo.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(numberLength, '0'));
        return string.Join(separator ?? "", parts);
    }
}
