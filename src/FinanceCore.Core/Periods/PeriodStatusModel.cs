using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Periods;

/// <summary>Payload of periods:soft-close and periods:hard-close (06, command table). company_id is part of the key of a period.</summary>
public sealed record PeriodRef(Guid CompanyId, int FiscalYear, int PeriodNo);

/// <summary>Payload of periods:reopen: reason code and text are mandatory (ADR-023). target_status is SOFT_CLOSED or OPEN.</summary>
public sealed record ReopenPeriodRequest(PeriodRef Period, string TargetStatus, string ReasonCode, string ReasonText);

public sealed record PeriodStatusChange(
    Guid CompanyId, int FiscalYear, int PeriodNo, string FromStatus, string ToStatus,
    Guid EventId, long EventSeq, string RowHash, string PrevHash);

public static class PeriodFingerprint
{
    private static readonly FieldSpec[] Ref =
    [
        new("company_id", FieldType.Id), new("fiscal_year", FieldType.Int), new("period_no", FieldType.Int)
    ];

    private static readonly FieldSpec[] Reopen =
    [
        new("company_id", FieldType.Id), new("fiscal_year", FieldType.Int), new("period_no", FieldType.Int),
        new("target_status", FieldType.Text), new("reason_code", FieldType.Text), new("reason_text", FieldType.Text)
    ];

    public static byte[] ForStatus(string commandName, PeriodRef p) =>
        CommandFingerprint.Compute(commandName, Ref, new Dictionary<string, object?>
        {
            ["company_id"] = p.CompanyId, ["fiscal_year"] = (long)p.FiscalYear, ["period_no"] = (long)p.PeriodNo
        });

    public static byte[] ForReopen(ReopenPeriodRequest r) =>
        CommandFingerprint.Compute("periods:reopen", Reopen, new Dictionary<string, object?>
        {
            ["company_id"] = r.Period.CompanyId, ["fiscal_year"] = (long)r.Period.FiscalYear, ["period_no"] = (long)r.Period.PeriodNo,
            ["target_status"] = r.TargetStatus, ["reason_code"] = r.ReasonCode, ["reason_text"] = r.ReasonText
        });
}
