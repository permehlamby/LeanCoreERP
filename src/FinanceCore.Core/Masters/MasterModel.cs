using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Periods;

namespace FinanceCore.Core.Masters;

/// <summary>The kinds of value of a master data field. The text form of a value is canonical, so that old and new values of a change compare and log.</summary>
public enum MasterKind { Code, Text, Bool, Amount, Percent, Id, Country, Currency, SmallInt, Date, Rate }

/// <summary>
/// One field that a master command may set: the column, the kind, the length, whether it may be null, the allowed values of a code and whether
/// the field is personal data (always, or only for a partner of kind PERSON: the credit limits, 07, 9.1).
/// </summary>
public sealed record MasterField(
    string Name, MasterKind Kind, int Max = 0, bool Nullable = true, string[]? Allowed = null, bool Pii = false, bool PiiIfPerson = false);

public sealed record ParsedValue(object? Value, string? Text);

public static class MasterValues
{
    /// <summary>Checks one value against its field and returns the value and its canonical text, or null and the reason.</summary>
    public static ParsedValue? Parse(MasterField f, object? v, out string? reason)
    {
        reason = null;
        if (v is null)
        {
            if (!f.Nullable) { reason = "required, cannot be cleared"; return null; }
            return new ParsedValue(null, null);
        }
        switch (f.Kind)
        {
            case MasterKind.Bool:
                if (v is bool b) return new ParsedValue(b, b ? "true" : "false");
                reason = "true or false"; return null;
            case MasterKind.Id:
                if (v is Guid g && g != Guid.Empty) return new ParsedValue(g, g.ToString("D").ToLowerInvariant());
                reason = "an id"; return null;
            case MasterKind.Country:
                if (v is string c && c.Length == 2 && c.All(ch => ch is >= 'A' and <= 'Z')) return new ParsedValue(c, c);
                reason = "two upper-case letters"; return null;
            case MasterKind.Currency:
                if (v is string cu && cu.Length == 3 && cu.All(ch => ch is >= 'A' and <= 'Z')) return new ParsedValue(cu, cu);
                reason = "three upper-case letters"; return null;
            case MasterKind.SmallInt:
                if (v is int or long or short && Convert.ToInt64(v, CultureInfo.InvariantCulture) is >= 0 and <= short.MaxValue)
                    return new ParsedValue((short)Convert.ToInt64(v, CultureInfo.InvariantCulture), Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                reason = "a whole number from 0 to 32767"; return null;
            case MasterKind.Amount:
            case MasterKind.Percent:
                if (v is int or long) v = Convert.ToDecimal(v, CultureInfo.InvariantCulture);
                if (v is not decimal d) { reason = "a decimal number"; return null; }
                int scale = f.Kind == MasterKind.Amount ? Canonical.AmountScale : Canonical.RateScale;
                if (Math.Round(d, scale) != d) { reason = $"at most {scale} fraction digits"; return null; }
                if (f.Kind == MasterKind.Amount && d < 0) { reason = "not negative"; return null; }
                if (f.Kind == MasterKind.Percent && (d <= 0 || d > 100)) { reason = "more than 0 and at most 100"; return null; }
                return new ParsedValue(d, d.ToString("F" + scale, CultureInfo.InvariantCulture));
            case MasterKind.Rate:
                // a rate in percent, 0 to 100 (a VAT rate of 0, a deductible share of 0): Percent is more than 0
                if (v is int or long) v = Convert.ToDecimal(v, CultureInfo.InvariantCulture);
                if (v is not decimal r) { reason = "a decimal number"; return null; }
                if (Math.Round(r, Canonical.RateScale) != r) { reason = $"at most {Canonical.RateScale} fraction digits"; return null; }
                if (r < 0 || r > 100) { reason = "0 to 100"; return null; }
                return new ParsedValue(r, r.ToString("F" + Canonical.RateScale, CultureInfo.InvariantCulture));
            case MasterKind.Date:
                if (v is DateOnly day) return new ParsedValue(day, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (v is string ds && DateOnly.TryParseExact(ds, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedDay))
                    return new ParsedValue(parsedDay, ds);
                reason = "a date as yyyy-MM-dd"; return null;
            default:   // Code and Text
                if (v is not string s) { reason = "text"; return null; }
                if (s.Length == 0 || s.Length > f.Max) { reason = $"1 to {f.Max} characters, or null to clear"; return null; }
                if (f.Allowed is not null && !f.Allowed.Contains(s)) { reason = "one of " + string.Join(", ", f.Allowed); return null; }
                return new ParsedValue(s, s);
        }
    }
}

/// <summary>The fields of the master tables that the first commands take (07, 9.1 to 9.3 and 9.6). The S columns of the partner are not among them.</summary>
public static class MasterFields
{
    public static readonly IReadOnlyList<MasterField> Partner =
    [
        new("partner_code", MasterKind.Code, 50, false),
        new("partner_kind", MasterKind.Code, 50, false, ["ORGANISATION", "PERSON"]),
        new("is_customer", MasterKind.Bool, 0, false), new("is_supplier", MasterKind.Bool, 0, false),
        new("ic_company_id", MasterKind.Id),
        new("tenant_credit_limit", MasterKind.Amount, PiiIfPerson: true), new("tenant_credit_limit_currency", MasterKind.Currency, PiiIfPerson: true)
    ];

    public static readonly IReadOnlyList<MasterField> Pii =
    [
        new("legal_name", MasterKind.Text, 200, false, Pii: true), new("trade_name", MasterKind.Text, 200, Pii: true),
        new("address_line_1", MasterKind.Text, 200, Pii: true), new("address_line_2", MasterKind.Text, 200, Pii: true),
        new("postal_code", MasterKind.Text, 20, Pii: true), new("city", MasterKind.Text, 100, Pii: true),
        new("country_code", MasterKind.Country, 0, false, Pii: true),
        new("email", MasterKind.Text, 200, Pii: true), new("phone", MasterKind.Text, 50, Pii: true), new("contact_person", MasterKind.Text, 200, Pii: true),
        new("electronic_address", MasterKind.Text, 100, Pii: true), new("electronic_address_scheme", MasterKind.Code, 50, Pii: true),
        new("legal_registration_id", MasterKind.Text, 50, Pii: true)
    ];

    public static readonly IReadOnlyList<MasterField> Relation =
    [
        new("is_customer", MasterKind.Bool, 0, false), new("is_supplier", MasterKind.Bool, 0, false),
        new("default_payment_terms_id", MasterKind.Id),
        new("credit_limit", MasterKind.Amount, PiiIfPerson: true), new("credit_limit_currency", MasterKind.Currency, PiiIfPerson: true),
        new("default_currency", MasterKind.Currency),
        new("payment_block", MasterKind.Code, 50), new("dunning_block", MasterKind.Code, 50),
        new("status", MasterKind.Code, 50, false, ["ACTIVE", "BLOCKED"])
    ];

    public static readonly IReadOnlyList<MasterField> PaymentTerms =
    [
        new("code", MasterKind.Code, 50, false), new("description", MasterKind.Text, 100, false),
        new("baseline_rule", MasterKind.Code, 50, false, ["DOCUMENT_DATE", "POSTING_DATE", "END_OF_MONTH"]),
        new("net_days", MasterKind.SmallInt, 0, false), new("discount_days", MasterKind.SmallInt), new("discount_pct", MasterKind.Percent)
    ];
}

/// <summary>The fields of the dimension commands (07, 6.5). The kind of a type is FREE for every type a command creates: the four standard types exist in every tenant.</summary>
public static class DimensionFields
{
    public static readonly IReadOnlyList<MasterField> Type =
    [
        new("type_code", MasterKind.Code, 50, false), new("name", MasterKind.Text, 100, false)
    ];

    public static readonly IReadOnlyList<MasterField> Value =
    [
        new("dimension_type_id", MasterKind.Id, 0, false), new("company_id", MasterKind.Id),
        new("value_code", MasterKind.Code, 50, false), new("name", MasterKind.Text, 100, false),
        new("parent_value_id", MasterKind.Id), new("valid_from", MasterKind.Date, 0, false), new("valid_to", MasterKind.Date)
    ];

    public static readonly string[] Rules = ["MANDATORY", "OPTIONAL", "FORBIDDEN"];
    public static readonly string[] StandardRoles = ["COST_CENTER", "PROJECT", "PROFIT_CENTER", "BUSINESS_UNIT"];
}

public sealed record CreateDimensionTypeRequest(IReadOnlyDictionary<string, object?> Fields);
public sealed record UpdateDimensionTypeRequest(Guid DimensionTypeId, long ExpectedVersion, IReadOnlyDictionary<string, object?> Fields);
public sealed record CreateDimensionValueRequest(IReadOnlyDictionary<string, object?> Fields);
public sealed record UpdateDimensionValueRequest(Guid DimensionValueId, long ExpectedVersion, IReadOnlyDictionary<string, object?> Fields);

/// <summary>Payload of account-dimension-rules:create (07, 6.2). The row is keyed by company, account and type. Absence of a row means OPTIONAL.</summary>
public sealed record CreateAccountDimensionRuleRequest(Guid CompanyId, Guid AccountId, Guid DimensionTypeId, string Rule);
public sealed record UpdateAccountDimensionRuleRequest(Guid CompanyId, Guid AccountId, Guid DimensionTypeId, long ExpectedVersion, string Rule);

/// <summary>The fields of the VAT configuration commands (07, 8.1 to 8.3, 08, 4.1, 19, 3.1).</summary>
public static class TaxFields
{
    public static readonly string[] Schemes = ["STANDARD", "OSS_UNION", "OSS_NON_UNION", "IOSS"];
    public static readonly string[] Frequencies = ["MONTHLY", "QUARTERLY", "HALF_YEARLY", "YEARLY"];
    public static readonly string[] CorrectionMethods = ["ADJUST_NEXT", "SUPPLEMENTARY", "REPLACEMENT"];
    public static readonly string[] TaxTypes = ["OUTPUT", "INPUT", "EXEMPT", "ZERO_RATED"];
    public static readonly string[] ReturnTypes = ["STANDARD", "EC_LIST", "OSS", "IOSS"];

    public static readonly IReadOnlyList<MasterField> TaxClass =
    [
        new("code", MasterKind.Code, 50, false), new("name", MasterKind.Text, 100, false)
    ];

    private static readonly MasterField[] RegistrationShared =
    [
        new("country_code", MasterKind.Country, 0, false), new("vat_id", MasterKind.Text, 30, false),
        new("scheme", MasterKind.Code, 50, false, Schemes), new("filing_frequency", MasterKind.Code, 50, false, Frequencies),
        new("ec_list_frequency", MasterKind.Code, 50, true, ["MONTHLY", "QUARTERLY"]),
        new("correction_method", MasterKind.Code, 50, false, CorrectionMethods), new("return_currency", MasterKind.Currency, 0, false),
        new("vat_rate_type", MasterKind.Code, 50, false, ["BOOKING", "VAT"]),
        new("valid_from", MasterKind.Date, 0, false), new("valid_to", MasterKind.Date)
    ];
    public static readonly IReadOnlyList<MasterField> RegistrationCreate = [new("company_id", MasterKind.Id, 0, false), .. RegistrationShared];
    public static readonly IReadOnlyList<MasterField> RegistrationUpdate = RegistrationShared;

    public static readonly IReadOnlyList<MasterField> TaxCodeCreate =
    [
        new("company_id", MasterKind.Id, 0, false), new("code", MasterKind.Code, 50, false), new("description", MasterKind.Text, 100, false),
        new("tax_type", MasterKind.Code, 50, false, TaxTypes), new("vat_registration_id", MasterKind.Id, 0, false)
    ];
    public static readonly IReadOnlyList<MasterField> TaxCodeUpdate =
    [
        new("code", MasterKind.Code, 50, false), new("description", MasterKind.Text, 100, false),
        new("tax_type", MasterKind.Code, 50, false, TaxTypes), new("vat_registration_id", MasterKind.Id, 0, false)
    ];

    public static readonly IReadOnlyList<MasterField> TaxCodeVersion =
    [
        new("valid_from", MasterKind.Date, 0, false), new("valid_to", MasterKind.Date), new("rate", MasterKind.Rate, 0, false),
        new("vat_account_id", MasterKind.Id, 0, false), new("reverse_charge", MasterKind.Bool, 0, false), new("reverse_charge_account_id", MasterKind.Id),
        new("en16931_vat_category", MasterKind.Code, 50, true, ["S", "Z", "E", "AE", "K", "G", "O", "L", "M"]),
        new("exemption_reason_code", MasterKind.Code, 50), new("exemption_reason_text", MasterKind.Text, 200),
        new("ec_list_category", MasterKind.Code, 50, true, ["GOODS", "SERVICES", "TRIANGULATION"]), new("supply_kind", MasterKind.Code, 50, true, ["GOODS", "SERVICES"]),
        new("evidence_required_type", MasterKind.Code, 50, true, ["CUSTOMS_DECLARATION", "PROOF_OF_EXPORT", "TRANSPORT_DOCUMENT", "PARTNER_VAT_CHECK", "OTHER"]),
        new("deductible_pct", MasterKind.Rate, 0, false), new("deduction_key", MasterKind.Code, 50),
        new("deduction_cap", MasterKind.Code, 50, false, ["NONE", "SUPPLIED_AMOUNT"])
    ];

    public static readonly IReadOnlyList<MasterField> ReturnBoxFields =
    [
        new("name", MasterKind.Text, 100, false), new("box_group", MasterKind.Code, 50)
    ];

    public static readonly string[] VatStatuses = ["REGISTERED", "NOT_REGISTERED"];
    private static readonly MasterField[] PartnerRegistrationShared =
    [
        new("vat_status", MasterKind.Code, 50, false, VatStatuses), new("vat_id", MasterKind.Text, 30), new("valid_to", MasterKind.Date)
    ];
    public static readonly IReadOnlyList<MasterField> PartnerRegistrationCreate =
    [
        new("partner_id", MasterKind.Id, 0, false), new("country_code", MasterKind.Country, 0, false), new("valid_from", MasterKind.Date, 0, false), .. PartnerRegistrationShared
    ];
    public static readonly IReadOnlyList<MasterField> PartnerRegistrationUpdate = PartnerRegistrationShared;

    public static readonly string[] KeyKinds = ["PRO_RATA", "ESTIMATE", "SECTOR", "AREA"];
    public static readonly IReadOnlyList<MasterField> DeductionKeyCreate =
    [
        new("company_id", MasterKind.Id, 0, false), new("registration_id", MasterKind.Id, 0, false), new("key_code", MasterKind.Code, 50, false),
        new("kind", MasterKind.Code, 50, false, KeyKinds), new("description", MasterKind.Text, 200)
    ];
    public static readonly IReadOnlyList<MasterField> DeductionKeyUpdate =
    [
        new("kind", MasterKind.Code, 50, false, KeyKinds), new("description", MasterKind.Text, 200)
    ];
    /// <summary>One rate version of a deduction key (07, 8.6). The rate may be 0.</summary>
    public static readonly IReadOnlyList<MasterField> DeductionRate =
    [
        new("valid_from", MasterKind.Date, 0, false), new("valid_to", MasterKind.Date), new("rate_pct", MasterKind.Rate, 0, false),
        new("rate_status", MasterKind.Code, 50, false, ["PROVISIONAL", "FINAL"]), new("basis_note", MasterKind.Text, 200)
    ];

    /// <summary>The fields of one version of a determination rule (07, 8.4). A key that is absent or null means any.</summary>
    public static readonly IReadOnlyList<MasterField> RuleVersion =
    [
        new("valid_from", MasterKind.Date, 0, false), new("valid_to", MasterKind.Date),
        new("transaction_type", MasterKind.Code, 50, true, ["SALES", "PURCHASE"]),
        new("origin_country", MasterKind.Country), new("destination_country", MasterKind.Country),
        new("origin_eu", MasterKind.Bool), new("destination_eu", MasterKind.Bool),
        new("partner_vat_status", MasterKind.Code, 50, true, VatStatuses),
        new("account_id", MasterKind.Id), new("tax_class_id", MasterKind.Id),
        new("priority", MasterKind.SmallInt, 0, false), new("tax_code_id", MasterKind.Id, 0, false)
    ];
}

public sealed record CreateDeductionKeyRequest(IReadOnlyDictionary<string, object?> Fields);
public sealed record UpdateDeductionKeyRequest(Guid DeductionKeyId, long ExpectedVersion, IReadOnlyDictionary<string, object?> Fields);
/// <summary>Payload of vat-deduction-rates:create-version (07, 8.6): a rate version of the key with this id. A version is insert-only.</summary>
public sealed record CreateDeductionRateRequest(Guid DeductionKeyId, IReadOnlyDictionary<string, object?> Fields);

public sealed record CreatePartnerTaxRegistrationRequest(IReadOnlyDictionary<string, object?> Fields);
public sealed record UpdatePartnerTaxRegistrationRequest(Guid RegistrationId, long ExpectedVersion, IReadOnlyDictionary<string, object?> Fields);

/// <summary>Payload of tax-determination-rules:create (07, 8.4): the first version of a new rule, with the codes a caller may supply instead of the determined one.</summary>
public sealed record CreateTaxRuleRequest(Guid CompanyId, IReadOnlyDictionary<string, object?> Fields, IReadOnlyList<Guid> Alternatives);
/// <summary>Payload of tax-determination-rules:create-version: a later version of an existing rule. A rule version is insert-only.</summary>
public sealed record CreateTaxRuleVersionRequest(Guid RuleId, IReadOnlyDictionary<string, object?> Fields, IReadOnlyList<Guid> Alternatives);

public sealed record CreateTaxClassRequest(IReadOnlyDictionary<string, object?> Fields);
public sealed record UpdateTaxClassRequest(Guid TaxClassId, long ExpectedVersion, IReadOnlyDictionary<string, object?> Fields);
public sealed record CreateVatRegistrationRequest(IReadOnlyDictionary<string, object?> Fields);
public sealed record UpdateVatRegistrationRequest(Guid RegistrationId, long ExpectedVersion, IReadOnlyDictionary<string, object?> Fields);
public sealed record CreateTaxCodeRequest(IReadOnlyDictionary<string, object?> Fields);
public sealed record UpdateTaxCodeRequest(Guid TaxCodeId, long ExpectedVersion, IReadOnlyDictionary<string, object?> Fields);

/// <summary>One box a tax code version maps to (08, 6.2): the side of the line, the return type, the box, whether the base or the tax goes in it, and the sign.</summary>
public sealed record TaxBoxMapping(string LineSide, string ReturnType, string BoxCode, string AmountKind, int Sign);

/// <summary>Payload of tax-codes:create-version (07, 8.3). The boxes are part of the version and are written with it.</summary>
public sealed record CreateTaxCodeVersionRequest(Guid TaxCodeId, IReadOnlyDictionary<string, object?> Fields, IReadOnlyList<TaxBoxMapping> Boxes);

/// <summary>A return box is keyed by country, return type and code (07, 8.2).</summary>
public sealed record CreateTaxReturnBoxRequest(string CountryCode, string ReturnType, string BoxCode, IReadOnlyDictionary<string, object?> Fields);
public sealed record UpdateTaxReturnBoxRequest(string CountryCode, string ReturnType, string BoxCode, long ExpectedVersion, IReadOnlyDictionary<string, object?> Fields);
public sealed record TaxReturnBoxStatusRequest(string CountryCode, string ReturnType, string BoxCode, long ExpectedVersion, string ReasonCode);

/// <summary>One relation of a partner to a company, given as the fields to set. On create it is a new row, on update it creates the row or changes it.</summary>
public sealed record PartnerRelationInput(Guid CompanyId, IReadOnlyDictionary<string, object?> Fields);

/// <summary>Payload of business-partners:create (07, 11.1). Pii needs legal_name and country_code. Partner needs partner_code and partner_kind.</summary>
public sealed record CreatePartnerRequest(
    IReadOnlyDictionary<string, object?> Partner, IReadOnlyDictionary<string, object?> Pii, IReadOnlyList<PartnerRelationInput> Relations);

/// <summary>Payload of business-partners:update. A field that is present is set (null clears it where allowed), a field that is absent is left as it is.</summary>
public sealed record UpdatePartnerRequest(
    Guid PartnerId, long ExpectedVersion, IReadOnlyDictionary<string, object?> Partner, IReadOnlyDictionary<string, object?>? Pii,
    IReadOnlyList<PartnerRelationInput> Relations);

/// <summary>Payload of business-partners:deactivate and :reactivate, and of payment-terms:deactivate and :reactivate.</summary>
public sealed record MasterStatusRequest(Guid Id, long ExpectedVersion, string ReasonCode);

public sealed record CreatePaymentTermsRequest(IReadOnlyDictionary<string, object?> Fields);
public sealed record UpdatePaymentTermsRequest(Guid PaymentTermsId, long ExpectedVersion, IReadOnlyDictionary<string, object?> Fields);

/// <summary>What a master command did: the row, its new version, the change set of the log rows and the fields that changed.</summary>
public sealed record MasterResult(Guid EntityId, long RowVersion, Guid ChangeSetId, IReadOnlyList<string> ChangedFields);

public static class MasterRules
{
    public const string PartnerPermission = "write.master";
    public const string ConfigPermission = "write.config";
    public const string DimensionPermission = PartnerPermission;

    private static readonly FieldSpec[] Payload = [new("payload", FieldType.Hex32)];

    /// <summary>A stable text of a set of fields: keys in order, values in the canonical form of Describe. Absent and null differ.</summary>
    public static string Text(IReadOnlyDictionary<string, object?>? fields)
    {
        if (fields is null) return "-";
        var sb = new StringBuilder();
        foreach (var kv in fields.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('=').Append(Describe(kv.Value)).Append(';');
        return sb.ToString();
    }

    private static string Describe(object? v) => v switch
    {
        null => "~null",
        string s => "s:" + s,
        bool b => b ? "b:true" : "b:false",
        Guid g => "g:" + g.ToString("D").ToLowerInvariant(),
        decimal d => "d:" + d.ToString(CultureInfo.InvariantCulture),
        IFormattable f => "n:" + f.ToString(null, CultureInfo.InvariantCulture),
        _ => "?:" + v
    };

    public static byte[] Fingerprint(string command, string payloadText) =>
        CommandFingerprint.Compute(command, Payload, new Dictionary<string, object?> { ["payload"] = SHA256.HashData(Encoding.UTF8.GetBytes(payloadText)) });

    public static string RelationText(IReadOnlyList<PartnerRelationInput> relations) =>
        string.Concat(relations.Select(r => r.CompanyId.ToString("D") + "{" + Text(r.Fields) + "}"));

    public static byte[] Fingerprint(CreatePartnerRequest r) =>
        Fingerprint("business-partners:create", Text(r.Partner) + "|" + Text(r.Pii) + "|" + RelationText(r.Relations));

    public static byte[] Fingerprint(UpdatePartnerRequest r) =>
        Fingerprint("business-partners:update", r.PartnerId.ToString("D") + "|" + r.ExpectedVersion + "|" + Text(r.Partner) + "|" + Text(r.Pii) + "|" + RelationText(r.Relations));

    public static byte[] Fingerprint(string command, MasterStatusRequest r) =>
        Fingerprint(command, r.Id.ToString("D") + "|" + r.ExpectedVersion + "|" + r.ReasonCode);

    public static byte[] Fingerprint(CreateDimensionTypeRequest r) => Fingerprint("dimension-types:create", Text(r.Fields));
    public static byte[] Fingerprint(UpdateDimensionTypeRequest r) =>
        Fingerprint("dimension-types:update", r.DimensionTypeId.ToString("D") + "|" + r.ExpectedVersion + "|" + Text(r.Fields));
    public static byte[] Fingerprint(CreateDimensionValueRequest r) => Fingerprint("dimension-values:create", Text(r.Fields));
    public static byte[] Fingerprint(UpdateDimensionValueRequest r) =>
        Fingerprint("dimension-values:update", r.DimensionValueId.ToString("D") + "|" + r.ExpectedVersion + "|" + Text(r.Fields));
    public static byte[] Fingerprint(CreateAccountDimensionRuleRequest r) =>
        Fingerprint("account-dimension-rules:create", $"{r.CompanyId:D}|{r.AccountId:D}|{r.DimensionTypeId:D}|{r.Rule}");
    public static byte[] Fingerprint(UpdateAccountDimensionRuleRequest r) =>
        Fingerprint("account-dimension-rules:update", $"{r.CompanyId:D}|{r.AccountId:D}|{r.DimensionTypeId:D}|{r.ExpectedVersion}|{r.Rule}");

    public static byte[] Fingerprint(CreateTaxClassRequest r) => Fingerprint("tax-classes:create", Text(r.Fields));
    public static byte[] Fingerprint(UpdateTaxClassRequest r) => Fingerprint("tax-classes:update", r.TaxClassId.ToString("D") + "|" + r.ExpectedVersion + "|" + Text(r.Fields));
    public static byte[] Fingerprint(CreateVatRegistrationRequest r) => Fingerprint("company-vat-registrations:create", Text(r.Fields));
    public static byte[] Fingerprint(UpdateVatRegistrationRequest r) =>
        Fingerprint("company-vat-registrations:update", r.RegistrationId.ToString("D") + "|" + r.ExpectedVersion + "|" + Text(r.Fields));
    public static byte[] Fingerprint(CreateTaxCodeRequest r) => Fingerprint("tax-codes:create", Text(r.Fields));
    public static byte[] Fingerprint(UpdateTaxCodeRequest r) => Fingerprint("tax-codes:update", r.TaxCodeId.ToString("D") + "|" + r.ExpectedVersion + "|" + Text(r.Fields));
    public static byte[] Fingerprint(CreateTaxCodeVersionRequest r) =>
        Fingerprint("tax-codes:create-version", r.TaxCodeId.ToString("D") + "|" + Text(r.Fields) + "|" +
            string.Join(";", r.Boxes.Select(b => $"{b.LineSide}/{b.ReturnType}/{b.BoxCode}/{b.AmountKind}/{b.Sign}")));
    public static byte[] Fingerprint(CreateTaxReturnBoxRequest r) =>
        Fingerprint("tax-return-boxes:create", $"{r.CountryCode}|{r.ReturnType}|{r.BoxCode}|" + Text(r.Fields));
    public static byte[] Fingerprint(UpdateTaxReturnBoxRequest r) =>
        Fingerprint("tax-return-boxes:update", $"{r.CountryCode}|{r.ReturnType}|{r.BoxCode}|{r.ExpectedVersion}|" + Text(r.Fields));
    public static byte[] Fingerprint(string command, TaxReturnBoxStatusRequest r) =>
        Fingerprint(command, $"{r.CountryCode}|{r.ReturnType}|{r.BoxCode}|{r.ExpectedVersion}|{r.ReasonCode}");

    public static byte[] Fingerprint(CreateDeductionKeyRequest r) => Fingerprint("vat-deduction-keys:create", Text(r.Fields));
    public static byte[] Fingerprint(UpdateDeductionKeyRequest r) =>
        Fingerprint("vat-deduction-keys:update", r.DeductionKeyId.ToString("D") + "|" + r.ExpectedVersion + "|" + Text(r.Fields));
    public static byte[] Fingerprint(CreateDeductionRateRequest r) => Fingerprint("vat-deduction-rates:create-version", r.DeductionKeyId.ToString("D") + "|" + Text(r.Fields));
    public static byte[] Fingerprint(CreatePartnerTaxRegistrationRequest r) => Fingerprint("partner-tax-registrations:create", Text(r.Fields));
    public static byte[] Fingerprint(UpdatePartnerTaxRegistrationRequest r) =>
        Fingerprint("partner-tax-registrations:update", r.RegistrationId.ToString("D") + "|" + r.ExpectedVersion + "|" + Text(r.Fields));
    private static string Alternatives(IReadOnlyList<Guid> a) => string.Join(",", a.Select(g => g.ToString("D")).Order(StringComparer.Ordinal));
    public static byte[] Fingerprint(CreateTaxRuleRequest r) =>
        Fingerprint("tax-determination-rules:create", r.CompanyId.ToString("D") + "|" + Text(r.Fields) + "|" + Alternatives(r.Alternatives));
    public static byte[] Fingerprint(CreateTaxRuleVersionRequest r) =>
        Fingerprint("tax-determination-rules:create-version", r.RuleId.ToString("D") + "|" + Text(r.Fields) + "|" + Alternatives(r.Alternatives));

    public static byte[] Fingerprint(CreatePaymentTermsRequest r) => Fingerprint("payment-terms:create", Text(r.Fields));

    public static byte[] Fingerprint(UpdatePaymentTermsRequest r) =>
        Fingerprint("payment-terms:update", r.PaymentTermsId.ToString("D") + "|" + r.ExpectedVersion + "|" + Text(r.Fields));
}
