using System.Security.Cryptography;
using System.Text;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Periods;

namespace FinanceCore.Core.Posting;

/// <summary>One item of changes[] (06, 5.4). expected_old_value is the value the caller saw in canonical text form, null for an empty field.</summary>
public sealed record FieldChange(string EntityType, int? LineNo, string FieldName, string? ExpectedOldValue, string? NewValue);

/// <summary>Payload of journal-documents:change-open-fields (06, 5.4).</summary>
public sealed record ChangeOpenFieldsRequest(Guid DocumentId, IReadOnlyList<FieldChange> Changes, string ReasonCode, string? ReasonText);

/// <summary>A field that changed: one change_log row (02, 8).</summary>
public sealed record ChangedField(string EntityType, int? LineNo, string FieldName, string? OldValue, string? NewValue);

public sealed record ChangedDocument(Guid DocumentId, Guid ChangeSetId, string PeriodStatus, IReadOnlyList<ChangedField> Changed);

/// <summary>The L2 fields that the first slice can change, and how a field name that is not one of them is classified (06, 5.4).</summary>
public static class OpenFieldRules
{
    /// <summary>Text columns and their length in the table (02, 3 and 5.1).</summary>
    public static readonly IReadOnlyDictionary<string, int> HeaderFields =
        new Dictionary<string, int> { ["header_text"] = 200, ["external_reference"] = 100 };

    public static readonly IReadOnlyDictionary<string, int> LineFields =
        new Dictionary<string, int> { ["line_text"] = 200 };

    /// <summary>The kinds of value of an L2 field. The log row stores the kind (02, 8: value_type) and the text form is canonical: a date
    /// as yyyy-MM-dd, an amount with four fraction digits (the scale of core.amt), text as it is.</summary>
    public enum Kind { Text, Code, Date, Amount }

    public sealed record FieldKind(Kind Kind, int MaxLength = 0, string DbType = "text");

    /// <summary>L2 columns of journal_line_open_item (02, 5A) that the first slice can change.</summary>
    public static readonly IReadOnlyDictionary<string, FieldKind> OpenItemFields = new Dictionary<string, FieldKind>
    {
        ["baseline_date"] = new(Kind.Date, 0, "date"), ["due_date"] = new(Kind.Date, 0, "date"),
        ["discount_due_date"] = new(Kind.Date, 0, "date"), ["discount_amount_tc"] = new(Kind.Amount, 0, "numeric"),
        ["payment_block"] = new(Kind.Code, 50), ["dunning_block"] = new(Kind.Code, 50), ["payment_reference"] = new(Kind.Text, 100)
    };

    /// <summary>L2 fields of the spec whose tables or validation do not exist yet: the typed dimensions of a line need the dimension
    /// tables to be validated like a posting (06, 5.4 P4), the payment terms and the bank account of an open item need their tables.</summary>
    public static readonly IReadOnlySet<string> LineFieldsNotBuilt = new HashSet<string>
    {
        "cost_center_id", "project_id", "profit_center_id", "business_unit_id"
    };

    public static readonly IReadOnlySet<string> OpenItemFieldsNotBuilt = new HashSet<string> { "payment_terms_id", "bank_account_id" };

    public static FieldKind? KindOf(string entityType, string field) => entityType switch
    {
        "DOCUMENT" => HeaderFields.TryGetValue(field, out int h) ? new(Kind.Text, h) : null,
        "LINE" => LineFields.TryGetValue(field, out int l) ? new(Kind.Text, l) : null,
        "LINE_OPEN_ITEM" => OpenItemFields.GetValueOrDefault(field),
        _ => null
    };

    /// <summary>Checks a new value against the kind of its field and returns the canonical text, or the reason it is rejected.</summary>
    public static string? Canonicalise(FieldKind kind, string? value, out string? reason)
    {
        reason = null;
        if (value is null) return null;
        switch (kind.Kind)
        {
            case Kind.Date:
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateOnly d))
                { reason = "a date as yyyy-MM-dd"; return null; }
                return d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            case Kind.Amount:
                if (!decimal.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                        System.Globalization.CultureInfo.InvariantCulture, out decimal a) || a < 0 || Math.Round(a, Canonical.AmountScale) != a)
                { reason = $"a non-negative amount with at most {Canonical.AmountScale} fraction digits"; return null; }
                return a.ToString("F" + Canonical.AmountScale, System.Globalization.CultureInfo.InvariantCulture);
            default:
                if (value.Length == 0) { reason = "use null to clear a field"; return null; }
                if (value.Length > kind.MaxLength) { reason = $"at most {kind.MaxLength} characters"; return null; }
                return value;
        }
    }

    /// <summary>Entity types of the spec that this slice does not build, with the reason.</summary>
    public static readonly IReadOnlySet<string> EntityTypesNotBuilt = new HashSet<string> { "LINE_DIMENSION", "TAX_EVIDENCE" };

    /// <summary>The entity types the command takes. LINE and LINE_OPEN_ITEM name a line.</summary>
    public static readonly IReadOnlySet<string> EntityTypes = new HashSet<string> { "DOCUMENT", "LINE", "LINE_OPEN_ITEM" };

    public const string ReservedReason = "SYSTEM";

    private static readonly FieldSpec[] Header =
    [
        new("document_id", FieldType.Id), new("reason_code", FieldType.Text), new("reason_text", FieldType.Text, true),
        new("changes", FieldType.Hex32)
    ];

    private static readonly FieldSpec[] Item =
    [
        new("entity_type", FieldType.Text), new("line_no", FieldType.Int, true), new("field_name", FieldType.Text),
        new("expected_old_value", FieldType.Text, true), new("new_value", FieldType.Text, true)
    ];

    /// <summary>The fingerprint covers the items in the order given: the order is part of the request.</summary>
    public static byte[] Fingerprint(ChangeOpenFieldsRequest r)
    {
        using var items = new MemoryStream();
        foreach (FieldChange c in r.Changes)
            items.Write(CommandFingerprint.Compute("change-item", Item, new Dictionary<string, object?>
            {
                ["entity_type"] = c.EntityType, ["line_no"] = c.LineNo is null ? null : (long)c.LineNo, ["field_name"] = c.FieldName,
                ["expected_old_value"] = c.ExpectedOldValue, ["new_value"] = c.NewValue
            }));
        return CommandFingerprint.Compute("journal-documents:change-open-fields", Header, new Dictionary<string, object?>
        {
            ["document_id"] = r.DocumentId, ["reason_code"] = r.ReasonCode, ["reason_text"] = r.ReasonText,
            ["changes"] = SHA256.HashData(items.ToArray())
        });
    }
}
