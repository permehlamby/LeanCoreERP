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

    /// <summary>L2 fields of the spec whose tables or validation do not exist yet: the typed dimensions of a line need the dimension
    /// tables to be validated like a posting (06, 5.4 P4), the open-item fields need journal_line_open_item.</summary>
    public static readonly IReadOnlySet<string> LineFieldsNotBuilt = new HashSet<string>
    {
        "cost_center_id", "project_id", "profit_center_id", "business_unit_id",
        "baseline_date", "payment_terms_id", "due_date", "discount_due_date", "discount_amount_tc",
        "payment_block", "dunning_block", "payment_reference", "bank_account_id"
    };

    /// <summary>Entity types of the spec that this slice does not build, with the reason.</summary>
    public static readonly IReadOnlySet<string> EntityTypesNotBuilt = new HashSet<string> { "LINE_DIMENSION", "TAX_EVIDENCE" };

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
