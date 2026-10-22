using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Posting;

/// <summary>Payload of journal-documents:reverse (06, 5.3). reversal_date defaults to the posting date of the original.</summary>
public sealed record ReverseDocumentRequest(Guid DocumentId, DateOnly? ReversalDate, string ReasonCode, string? ReasonText);

public static class ReversalRules
{
    /// <summary>The document types the external command may reverse (06, 5.2 allow-list).</summary>
    public static readonly IReadOnlySet<string> ReversibleTypes =
        new HashSet<string> { "JOURNAL", "AP_INVOICE", "AP_CREDIT", "AR_INVOICE", "AR_CREDIT", "PAYMENT" };

    /// <summary>Reason codes an external caller may use. The other three of the closed list belong to system commands.</summary>
    public static readonly string[] ExternalReasons = ["CORRECTION", "ERROR"];

    public static readonly string[] SystemReasons = ["AUTO_REVERSAL", "RERUN", "REOPEN", "RETURN"];

    /// <summary>The owning command of a document type that this command does not reverse (06, 5.3 table).</summary>
    public static string? OwningCommand(string documentType) => documentType switch
    {
        "CLEARING" => "clearings:reset",
        "REVALUATION" or "REVAL_REVERSAL" => "revaluation:run",
        "DROPSHIP_COST" => "dropship-confirmations:reverse",
        "VAT_SETTLE" => "vat-returns:reverse-settlement",
        "YEAR_END" => "year-end:reopen",
        "GR_IR_WRITE_OFF" => "gr-ir:reverse-write-off",
        "HISTORY_SUMMARY" or "CLOSING" or "OPENING" or "OPENING_ITEMS" or "STOCK_OPENING" => "migration-batches:rollback",
        var t when t.StartsWith("STOCK_", StringComparison.Ordinal) => "stock-documents:reverse",
        _ => null
    };

    private static readonly FieldSpec[] Spec =
    [
        new("document_id", FieldType.Id), new("reversal_date", FieldType.Date, true), new("reason_code", FieldType.Text),
        new("reason_text", FieldType.Text, true)
    ];

    public static byte[] Fingerprint(ReverseDocumentRequest r) =>
        Periods.CommandFingerprint.Compute("journal-documents:reverse", Spec, new Dictionary<string, object?>
        {
            ["document_id"] = r.DocumentId, ["reversal_date"] = r.ReversalDate, ["reason_code"] = r.ReasonCode, ["reason_text"] = r.ReasonText
        });
}
