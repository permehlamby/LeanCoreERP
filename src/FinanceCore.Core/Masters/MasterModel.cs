using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Periods;

namespace FinanceCore.Core.Masters;

/// <summary>The kinds of value of a master data field. The text form of a value is canonical, so that old and new values of a change compare and log.</summary>
public enum MasterKind { Code, Text, Bool, Amount, Percent, Id, Country, Currency, SmallInt }

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

    public static byte[] Fingerprint(CreatePaymentTermsRequest r) => Fingerprint("payment-terms:create", Text(r.Fields));

    public static byte[] Fingerprint(UpdatePaymentTermsRequest r) =>
        Fingerprint("payment-terms:update", r.PaymentTermsId.ToString("D") + "|" + r.ExpectedVersion + "|" + Text(r.Fields));
}
