using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Posting;

/// <summary>Payload of vat-returns:prepare (08, 7.1).</summary>
public sealed record PrepareVatReturnRequest(Guid RegistrationId, string ReturnType, int FiscalYear, int VatPeriodNo);

/// <summary>Payload of vat-returns:record-filing (08, 7.2).</summary>
public sealed record RecordVatFilingRequest(Guid ReturnId, string FilingReference, DateTime FiledAt);

/// <summary>Payload of vat-returns:settle (08, 7.3). posting_date defaults to the first postable date on or after the period end.</summary>
public sealed record SettleVatReturnRequest(Guid ReturnId, DateOnly? PostingDate);

/// <summary>Payload of vat-returns:reverse-settlement (08, 7.3a).</summary>
public sealed record ReverseVatSettlementRequest(Guid ReturnId, DateOnly ReversalDate, string ReasonCode, string ReasonText);

public sealed record VatReturnLineResult(
    int LineNo, string RowType, string? BoxCode, string CurrencyCode, decimal BaseAmount, decimal TaxAmount, int LineCount);

public sealed record VatReturnTaxLineResult(
    Guid DocumentId, int LineNo, DateOnly TaxDate, bool IsLate, Guid TaxCodeVersionId, decimal VatBaseAmount, decimal VatTaxAmount, bool IsCurrent);

public sealed record VatReturnResult(
    Guid ReturnId, Guid CompanyId, Guid RegistrationId, string ReturnType, int FiscalYear, int VatPeriodNo,
    DateOnly PeriodStartDate, DateOnly PeriodEndDate, string ReturnKind, int ReturnSeq, Guid? ReplacesReturnId, Guid? SupersededByReturnId,
    int TaxLineCount, int LateLineCount, string SnapshotDigest, long ChainSeq, string RowHash, string PrevHash,
    string? FilingReference, DateTime? FiledAt, Guid? SettledDocumentId,
    IReadOnlyList<VatReturnLineResult> Lines, IReadOnlyList<VatReturnTaxLineResult> TaxLines);

/// <summary>The settled return and the VAT_SETTLE document that was posted for it.</summary>
public sealed record SettledVatReturn(Guid ReturnId, PostedDocument Document);

/// <summary>A regular fiscal period as the VAT period calculation sees it.</summary>
public sealed record RegularPeriod(int PeriodNo, DateOnly Start, DateOnly End, string Status);

/// <summary>A VAT period (08, 4.2): a run of consecutive regular fiscal periods of a fiscal year.</summary>
public sealed record VatPeriod(int No, IReadOnlyList<RegularPeriod> Periods)
{
    public DateOnly Start => Periods[0].Start;
    public DateOnly End => Periods[^1].End;
}

public static class VatPeriods
{
    public static readonly IReadOnlyList<string> Frequencies = ["MONTHLY", "QUARTERLY", "HALF_YEARLY", "YEARLY"];

    public static int Size(string frequency) => frequency switch
    {
        "MONTHLY" => 1, "QUARTERLY" => 3, "HALF_YEARLY" => 6, "YEARLY" => int.MaxValue,
        _ => throw new ArgumentException("Unknown filing frequency " + frequency)
    };

    /// <summary>The VAT periods of a fiscal year, from its regular periods in period order. A short first year gives a shorter first period.</summary>
    public static IReadOnlyList<VatPeriod> Build(IReadOnlyList<RegularPeriod> regular, string frequency)
    {
        int size = Size(frequency);
        var ordered = regular.OrderBy(p => p.PeriodNo).ToList();
        var result = new List<VatPeriod>();
        for (int i = 0, no = 1; i < ordered.Count; i += Math.Min(size, ordered.Count - i), no++)
            result.Add(new VatPeriod(no, ordered.Skip(i).Take(size).ToList()));
        return result;
    }

    /// <summary>Which of the return types a registration scheme files (08, 6.1). Only STANDARD is built.</summary>
    public static string ReturnTypeOfScheme(string scheme) => scheme switch
    {
        "STANDARD" => "STANDARD", "OSS_UNION" or "OSS_NON_UNION" => "OSS", "IOSS" => "IOSS", _ => throw new ArgumentException(scheme)
    };
}

/// <summary>The fingerprints of the VAT return commands and the hash of a return (08, 6.8).</summary>
public static class VatReturnHash2
{
    public const int Version = 2;
    public const string ChainType = "VAT_RETURN";

    private static FieldSpec Id(string n, bool nullable = false) => new(n, FieldType.Id, nullable);
    private static FieldSpec Code(string n, bool nullable = false) => new(n, FieldType.Text, nullable);
    private static FieldSpec Int(string n) => new(n, FieldType.Int);
    private static FieldSpec Date(string n) => new(n, FieldType.Date);
    private static FieldSpec Amt(string n) => new(n, FieldType.Amount);

    /// <summary>The L1 columns of vat_return (08, 6.3) in table order, without the outer fields, the hash columns and the snapshot digest.</summary>
    public static readonly IReadOnlyList<FieldSpec> Body =
    [
        Id("return_id"), Id("registration_id"), Code("return_type"), Int("fiscal_year"), Int("vat_period_no"), Date("period_start_date"),
        Date("period_end_date"), Code("return_kind"), Int("return_seq"), Id("replaces_return_id", true), new("prepared_at", FieldType.Timestamp),
        Code("prepared_by"), Code("correlation_id"), Code("idempotency_key"), Int("tax_line_count"), Int("late_line_count")
    ];

    public static readonly IReadOnlyList<FieldSpec> Line =
    [
        Id("tenant_id"), Id("company_id"), Id("return_id"), Int("line_no"), Code("row_type"), Code("box_code", true), Id("partner_id", true),
        Code("partner_vat_id", true), Code("country_code", true), Code("category", true), new("rate", FieldType.Rate, true), Code("supply_kind", true),
        Code("currency_code"), Amt("base_amount"), Amt("tax_amount"), Int("line_count"), Amt("legacy_base_amount"), Amt("legacy_tax_amount")
    ];

    public static readonly IReadOnlyList<FieldSpec> TaxLine =
    [
        Id("tenant_id"), Id("company_id"), Id("return_id"), Id("document_id"), Int("line_no"), Id("registration_id"), Code("return_type"),
        Date("tax_date"), new("is_late", FieldType.Bool), Id("tax_code_version_id"), Amt("vat_base_amount"), Amt("vat_tax_amount")
    ];

    /// <summary>SHA-256 over the lines in line_no order and then the tax lines in (document_id, line_no) order. The caller passes them sorted.</summary>
    public static byte[] SnapshotDigest(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> linesInOrder, IReadOnlyList<IReadOnlyDictionary<string, object?>> taxLinesInOrder)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Canonical.Field(Encoding.UTF8.GetBytes(linesInOrder.Count.ToString(CultureInfo.InvariantCulture))));
        foreach (var l in linesInOrder) buffer.Write(Canonical.Block(Canonical.EncodeFields(Line, l)));
        buffer.Write(Canonical.Field(Encoding.UTF8.GetBytes(taxLinesInOrder.Count.ToString(CultureInfo.InvariantCulture))));
        foreach (var t in taxLinesInOrder) buffer.Write(Canonical.Block(Canonical.EncodeFields(TaxLine, t)));
        return SHA256.HashData(buffer.ToArray());
    }

    public static byte[] RowHash(Guid tenantId, Guid companyId, long chainSeq, byte[] prevHash, IReadOnlyDictionary<string, object?> body, byte[] snapshotDigest)
    {
        using var buffer = new MemoryStream();
        void Write(byte[] value) => buffer.Write(Canonical.Field(value));
        Write(Encoding.UTF8.GetBytes(Version.ToString(CultureInfo.InvariantCulture)));
        Write(Encoding.UTF8.GetBytes(tenantId.ToString("D").ToLowerInvariant()));
        Write(Encoding.UTF8.GetBytes(companyId.ToString("D").ToLowerInvariant()));
        Write(Encoding.UTF8.GetBytes(chainSeq.ToString(CultureInfo.InvariantCulture)));
        Write(Encoding.UTF8.GetBytes(Canonical.FormatHex32(prevHash)));
        buffer.Write(Canonical.Block(Canonical.EncodeFields(Body, body)));
        Write(Encoding.UTF8.GetBytes(Canonical.FormatHex32(snapshotDigest)));
        return SHA256.HashData(buffer.ToArray());
    }

    private static readonly FieldSpec[] PrepareSpec = [Id("registration_id"), Code("return_type"), Int("fiscal_year"), Int("vat_period_no")];
    private static readonly FieldSpec[] FilingSpec = [Id("return_id"), Code("filing_reference"), new("filed_at", FieldType.Timestamp)];
    private static readonly FieldSpec[] SettleSpec = [Id("return_id"), new("posting_date", FieldType.Date, true)];
    private static readonly FieldSpec[] ReverseSpec = [Id("return_id"), Date("reversal_date"), Code("reason_code"), Code("reason_text")];

    public static byte[] Fingerprint(PrepareVatReturnRequest r) => Periods.CommandFingerprint.Compute("vat-returns:prepare", PrepareSpec,
        new Dictionary<string, object?> { ["registration_id"] = r.RegistrationId, ["return_type"] = r.ReturnType, ["fiscal_year"] = r.FiscalYear, ["vat_period_no"] = r.VatPeriodNo });

    public static byte[] Fingerprint(RecordVatFilingRequest r) => Periods.CommandFingerprint.Compute("vat-returns:record-filing", FilingSpec,
        new Dictionary<string, object?> { ["return_id"] = r.ReturnId, ["filing_reference"] = r.FilingReference, ["filed_at"] = r.FiledAt });

    public static byte[] Fingerprint(SettleVatReturnRequest r) => Periods.CommandFingerprint.Compute("vat-returns:settle", SettleSpec,
        new Dictionary<string, object?> { ["return_id"] = r.ReturnId, ["posting_date"] = r.PostingDate });

    public static byte[] Fingerprint(ReverseVatSettlementRequest r) => Periods.CommandFingerprint.Compute("vat-returns:reverse-settlement", ReverseSpec,
        new Dictionary<string, object?> { ["return_id"] = r.ReturnId, ["reversal_date"] = r.ReversalDate, ["reason_code"] = r.ReasonCode, ["reason_text"] = r.ReasonText });
}

/// <summary>Payload of vat-returns:get (08, 7.4).</summary>
public sealed record GetVatReturnRequest(Guid ReturnId);
