using FinanceCore.Core.Masters;

namespace FinanceCore.Core.Retention;

/// <summary>One version of a retention rule (12, 4.1): how many years a record class is kept, counted from which event, under which country's law.</summary>
public sealed record RetentionRuleVersion(
    Guid RuleId, string RecordClass, string CountryCode, int RetentionYears, string StartEvent, DateTime EffectiveFrom);

/// <summary>Payload of retention-rules:create-version (12, 13). EffectiveFrom defaults to the time of the command. The first version of a (company,
/// class, country) starts a rule, a later one is a version of it.</summary>
public sealed record CreateRetentionRuleVersionRequest(
    Guid CompanyId, string RecordClass, string CountryCode, int RetentionYears, string StartEvent, string? LegalBasis, DateTime? EffectiveFrom);

public sealed record CreatedRetentionRule(Guid RuleId, DateTime EffectiveFrom, bool NewRule);

public static class RetentionRules
{
    public const string Permission = "retention.manage";
    public const string CreateCommand = "retention-rules:create-version";

    public static readonly string[] RecordClasses =
    [
        "LEDGER", "VAT_RETURN", "STOCK", "ORDER", "FULFILMENT", "INVOICE_ISSUE", "LEGAL_SNAPSHOT", "ADDRESS_SNAPSHOT", "AUDIT_LOG", "READ_LOG"
    ];

    public static readonly string[] StartEvents =
        ["FISCAL_YEAR_END", "RECORD_DATE", "CALENDAR_YEAR_END_OF_RECORD", "CALENDAR_YEAR_END_OF_FISCAL_YEAR"];

    public static byte[] Fingerprint(CreateRetentionRuleVersionRequest r) =>
        MasterRules.Fingerprint(CreateCommand, string.Join('|', r.CompanyId.ToString("D"), r.RecordClass, r.CountryCode, r.RetentionYears, r.StartEvent,
            r.LegalBasis ?? "~null", r.EffectiveFrom?.ToUniversalTime().ToString("O") ?? "~now"));

    /// <summary>
    /// The date until which a record is kept under one rule (12, 4.1): the start of the retention plus the years. The start is the end of the
    /// fiscal year of the record, the record date, the end of the calendar year of the record date, or the end of the calendar year in which the
    /// fiscal year of the record ends.
    /// </summary>
    public static DateOnly RetentionUntil(string startEvent, int years, DateOnly fiscalYearEnd, DateOnly recordDate)
    {
        DateOnly start = startEvent switch
        {
            "FISCAL_YEAR_END" => fiscalYearEnd,
            "RECORD_DATE" => recordDate,
            "CALENDAR_YEAR_END_OF_RECORD" => new DateOnly(recordDate.Year, 12, 31),
            "CALENDAR_YEAR_END_OF_FISCAL_YEAR" => new DateOnly(fiscalYearEnd.Year, 12, 31),
            _ => throw new ArgumentOutOfRangeException(nameof(startEvent), startEvent, "unknown start event")
        };
        return start.AddYears(years);
    }

    /// <summary>
    /// The retention date of a new record of a class (12, 4.1). Of every rule (a country) the version with the highest effective_from not later than
    /// the moment of creation counts. If several countries are configured the **longest** retention applies. Null: no rule applies to the class.
    /// </summary>
    public static DateOnly? For(
        IEnumerable<RetentionRuleVersion> versions, string recordClass, DateTime createdAt, DateOnly fiscalYearEnd, DateOnly recordDate)
    {
        DateOnly? longest = null;
        foreach (var rule in versions.Where(v => v.RecordClass == recordClass && v.EffectiveFrom <= createdAt).GroupBy(v => (v.RuleId, v.CountryCode))
                     .Select(g => g.OrderByDescending(v => v.EffectiveFrom).First()))
        {
            DateOnly until = RetentionUntil(rule.StartEvent, rule.RetentionYears, fiscalYearEnd, recordDate);
            if (longest is null || until > longest) longest = until;
        }
        return longest;
    }

    /// <summary>
    /// The retention of a legal snapshot is never shorter than the retention of the document it belongs to (12, 4.1, consistency rule): the later of
    /// the LEGAL_SNAPSHOT rule and the LEDGER rule. Null if one of the two classes has no rule: a snapshot cannot be dated without both.
    /// </summary>
    public static DateOnly? ForLegalSnapshot(
        IReadOnlyList<RetentionRuleVersion> versions, DateTime createdAt, DateOnly fiscalYearEnd, DateOnly recordDate)
    {
        DateOnly? snapshot = For(versions, "LEGAL_SNAPSHOT", createdAt, fiscalYearEnd, recordDate);
        DateOnly? ledger = For(versions, "LEDGER", createdAt, fiscalYearEnd, recordDate);
        return snapshot is null || ledger is null ? null : snapshot > ledger ? snapshot : ledger;
    }
}
