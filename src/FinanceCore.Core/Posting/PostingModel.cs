using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Posting;

/// <summary>Payload of journal-documents:post as far as the first slice supports it (06, 5.2): no tax, no partners, open items on GL_OPEN_ITEM accounts only.</summary>
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
    IReadOnlyList<RequestLine> Lines,
    Guid? PartnerId = null,
    DateOnly? TaxDate = null,
    string? OriginCountry = null,
    string? DestinationCountry = null);

/// <summary>
/// The open_item object of a line (06, 5.2). Required on a line of a GL_OPEN_ITEM account (it may be empty: every attribute is optional),
/// forbidden on a line of a NONE account. PartnerId, PaymentTermsId and BankAccountId are accepted by the model and rejected by the
/// command until the partner, payment terms and bank account tables exist.
/// </summary>
public sealed record OpenItemRequest(
    Guid? PartnerId = null, DateOnly? BaselineDate = null, Guid? PaymentTermsId = null, DateOnly? DueDate = null,
    DateOnly? DiscountDueDate = null, decimal? DiscountAmountTc = null, string? PaymentBlock = null, string? DunningBlock = null,
    string? PaymentReference = null, Guid? BankAccountId = null);

/// <summary>One free dimension of a line (06, 5.2): the type and the value. A value is always given, a line without the dimension names no entry.</summary>
public sealed record FreeDimension(Guid DimensionTypeId, Guid DimensionValueId);

/// <summary>The dimensions of a base line (06, 5.2, 02, 5.3): the four standard types as typed columns and the free ones as a list. Every one is optional.</summary>
public sealed record LineDimensions(
    Guid? CostCenterId = null, Guid? ProjectId = null, Guid? ProfitCenterId = null, Guid? BusinessUnitId = null, IReadOnlyList<FreeDimension>? Free = null);

/// <summary>One base line of a request (06, 5.2). The tax fields (07, 8.4, 8.5): the tax class, the tax point, origin and destination, a tax code to use instead of the
/// determined one, and the tax amount of the supplier document.</summary>
public sealed record RequestLine(
    string LineRef, Guid AccountId, decimal AmountTc, string? LineText = null, OpenItemRequest? OpenItem = null, LineDimensions? Dimensions = null,
    Guid? TaxClassId = null, DateOnly? TaxDate = null, string? OriginCountry = null, string? DestinationCountry = null, Guid? TaxCodeId = null, decimal? TaxAmountTc = null);

public sealed record PostedLine(
    int LineNo, string? LineRef, Guid AccountId, string LineType,
    decimal AmountTc, decimal AmountLc, string CurrencyLc, decimal RateLc,
    decimal AmountGc, string CurrencyGc, decimal RateGc,
    decimal? AmountRc, string? CurrencyRc, decimal? RateRc,
    string RateType, DateOnly RateDate, Guid? TaxCodeId = null);

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

    private static readonly FieldSpec[] OpenItemSpec =
    [
        new("partner_id", FieldType.Id, true), new("baseline_date", FieldType.Date, true), new("payment_terms_id", FieldType.Id, true),
        new("due_date", FieldType.Date, true), new("discount_due_date", FieldType.Date, true), new("discount_amount_tc", FieldType.Amount, true),
        new("payment_block", FieldType.Text, true), new("dunning_block", FieldType.Text, true), new("payment_reference", FieldType.Text, true),
        new("bank_account_id", FieldType.Id, true)
    ];

    private static readonly FieldSpec[] DimensionSpec =
    [
        new("cost_center_id", FieldType.Id, true), new("project_id", FieldType.Id, true), new("profit_center_id", FieldType.Id, true),
        new("business_unit_id", FieldType.Id, true), new("free", FieldType.Text)
    ];

    private static readonly FieldSpec[] TaxHeaderSpec =
    [
        new("tax_date", FieldType.Date, true), new("origin_country", FieldType.Text, true), new("destination_country", FieldType.Text, true)
    ];

    private static readonly FieldSpec[] TaxLineSpec =
    [
        new("tax_class_id", FieldType.Id, true), new("tax_date", FieldType.Date, true), new("origin_country", FieldType.Text, true),
        new("destination_country", FieldType.Text, true), new("tax_code_id", FieldType.Id, true), new("tax_amount_tc", FieldType.Amount, true)
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
        // The open_item objects follow, one field per line in request order: a line without one is a NULL field. They are part of the
        // request, so a repeated key with another open_item is a different request (IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST).
        foreach (RequestLine l in r.Lines)
            all.Write(l.OpenItem is not { } o ? Canonical.NullField() : Canonical.Block(Canonical.EncodeFields(OpenItemSpec, new Dictionary<string, object?>
            {
                ["partner_id"] = o.PartnerId, ["baseline_date"] = o.BaselineDate, ["payment_terms_id"] = o.PaymentTermsId, ["due_date"] = o.DueDate,
                ["discount_due_date"] = o.DiscountDueDate, ["discount_amount_tc"] = o.DiscountAmountTc, ["payment_block"] = o.PaymentBlock,
                ["dunning_block"] = o.DunningBlock, ["payment_reference"] = o.PaymentReference, ["bank_account_id"] = o.BankAccountId
            })));
        // The dimensions follow only when a line has some, one field per line in request order: the fingerprint of a request without them is what it was
        // before the dimensions existed. The free ones are sorted by type, so the order of the list does not matter.
        if (r.Lines.Any(l => l.Dimensions is not null))
            foreach (RequestLine l in r.Lines)
                all.Write(l.Dimensions is not { } d ? Canonical.NullField() : Canonical.Block(Canonical.EncodeFields(DimensionSpec, new Dictionary<string, object?>
                {
                    ["cost_center_id"] = d.CostCenterId, ["project_id"] = d.ProjectId, ["profit_center_id"] = d.ProfitCenterId, ["business_unit_id"] = d.BusinessUnitId,
                    ["free"] = string.Concat((d.Free ?? []).OrderBy(f => f.DimensionTypeId).Select(f => f.DimensionTypeId.ToString("D") + "=" + f.DimensionValueId.ToString("D") + ";"))
                })));
        // The tax fields follow only when the request has some: the fingerprint of a request without them is what it was before the tax lines
        if (r.TaxDate is not null || r.OriginCountry is not null || r.DestinationCountry is not null || r.Lines.Any(l => l.TaxCodeId is not null || l.TaxAmountTc is not null || l.TaxClassId is not null
                || l.TaxDate is not null || l.OriginCountry is not null || l.DestinationCountry is not null))
        {
            all.Write(Canonical.Block(Canonical.EncodeFields(TaxHeaderSpec, new Dictionary<string, object?>
                { ["tax_date"] = r.TaxDate, ["origin_country"] = r.OriginCountry, ["destination_country"] = r.DestinationCountry })));
            foreach (RequestLine l in r.Lines)
                all.Write(Canonical.Block(Canonical.EncodeFields(TaxLineSpec, new Dictionary<string, object?>
                {
                    ["tax_class_id"] = l.TaxClassId, ["tax_date"] = l.TaxDate, ["origin_country"] = l.OriginCountry, ["destination_country"] = l.DestinationCountry,
                    ["tax_code_id"] = l.TaxCodeId, ["tax_amount_tc"] = l.TaxAmountTc
                })));
        }
        // The header partner follows only when there is one, so the fingerprint of a request without it is what it was before the invoice types
        if (r.PartnerId is { } partner)
            all.Write(Canonical.Block(Canonical.EncodeFields([new FieldSpec("header_partner_id", FieldType.Id)], new Dictionary<string, object?> { ["header_partner_id"] = partner })));
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
