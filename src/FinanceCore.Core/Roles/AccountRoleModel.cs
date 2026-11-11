using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Roles;

/// <summary>Payload of account-role-assignments:create (07, 6.3, 11.1). effective_from defaults to now.</summary>
public sealed record AssignAccountRoleRequest(
    Guid CompanyId, string RoleCode, Guid AccountId, string? CurrencyCode = null,
    Guid? CounterpartCompanyId = null, Guid? VatRegistrationId = null, DateTime? EffectiveFrom = null);

public sealed record AssignedAccountRole(
    Guid AssignmentId, Guid CompanyId, string RoleCode, string? CurrencyCode, Guid? CounterpartCompanyId,
    Guid? VatRegistrationId, Guid AccountId, DateTime EffectiveFrom);

/// <summary>The facts about an account that the role table of 07, 6.3 looks at.</summary>
public sealed record RoleAccount(
    string AccountType, string OpenItemMode, string? CurrencyCode, bool RevalueFlag, bool TaxAccount);

public enum RoleAccountKind { ProfitAndLoss, Equity, BalanceSheet }

/// <summary>One row of the role table of 07, 6.3 for the roles resolved by account_role_assignment.</summary>
public sealed record RoleDefinition(
    string Code, RoleAccountKind Kind, bool PerCurrency = false, bool PerCounterpart = false, bool PerVatRegistration = false, bool NoMandatoryDimensions = false);

/// <summary>A rule an account does not satisfy: the code of 11.2 and, for CONFIG.ROLE_ACCOUNT_RULE_VIOLATED, the rule name.</summary>
public sealed record RoleViolation(string Code, string? Rule);

/// <summary>
/// The role table of 07, 6.3 (D5, ADR-149) as far as account_role_assignment goes, and the rules of an account for a role.
/// The required open-item mode of all these roles is NONE. "No mandatory dimension rules" (RETAINED_EARNINGS, the migration
/// roles) is checked by the role assignment and by account-dimension-rules (rule MANDATORY_DIMENSION).
/// </summary>
public static class AccountRoles
{
    public const string RuleViolated = "CONFIG.ROLE_ACCOUNT_RULE_VIOLATED";
    public const string RevalueOnRetainedEarnings = "CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS";

    public static readonly IReadOnlyList<RoleDefinition> Definitions =
    [
        new("ROUNDING", RoleAccountKind.ProfitAndLoss, PerCurrency: true),
        new("TAX_ROUNDING", RoleAccountKind.ProfitAndLoss),
        new("FX_REALISED", RoleAccountKind.ProfitAndLoss),
        new("FX_UNREALISED_GAIN", RoleAccountKind.ProfitAndLoss, PerCurrency: true),
        new("FX_UNREALISED_LOSS", RoleAccountKind.ProfitAndLoss, PerCurrency: true),
        new("RETAINED_EARNINGS", RoleAccountKind.Equity, NoMandatoryDimensions: true),
        new("IC_RECEIVABLE", RoleAccountKind.BalanceSheet, PerCounterpart: true),
        new("IC_PAYABLE", RoleAccountKind.BalanceSheet, PerCounterpart: true),
        new("VAT_SETTLEMENT", RoleAccountKind.BalanceSheet, PerVatRegistration: true),
        new("MIGRATION_CLEARING", RoleAccountKind.BalanceSheet, NoMandatoryDimensions: true),
        new("HISTORY_RESULT", RoleAccountKind.BalanceSheet, NoMandatoryDimensions: true)
    ];

    /// <summary>The roles every company needs before it can post (11.3). ROUNDING is needed for each currency in use or as a default.</summary>
    public static readonly IReadOnlyList<string> RequiredForActivation =
        ["ROUNDING", "TAX_ROUNDING", "FX_REALISED", "FX_UNREALISED_GAIN", "FX_UNREALISED_LOSS", "RETAINED_EARNINGS"];

    public static RoleDefinition? Find(string? code) => Definitions.FirstOrDefault(d => d.Code == code);

    private static readonly string[] ProfitAndLoss = ["REVENUE", "EXPENSE"];
    private static readonly string[] BalanceSheet = ["ASSET", "LIABILITY", "EQUITY"];

    /// <summary>
    /// The rules of 6.3 for one account and one role row. <paramref name="otherRolesOfAccount"/> are the role codes that
    /// other rows give the same account (rule F8, 2). <paramref name="currencyCode"/> is the currency of the row.
    /// </summary>
    public static List<RoleViolation> CheckAccount(
        RoleDefinition role, string? currencyCode, RoleAccount account, IEnumerable<string> otherRolesOfAccount)
    {
        var v = new List<RoleViolation>();
        void Rule(string rule) => v.Add(new RoleViolation(RuleViolated, rule));

        bool typeOk = role.Kind switch
        {
            RoleAccountKind.ProfitAndLoss => ProfitAndLoss.Contains(account.AccountType),
            RoleAccountKind.Equity => account.AccountType == "EQUITY",
            _ => BalanceSheet.Contains(account.AccountType)
        };
        if (!typeOk) Rule("ACCOUNT_TYPE");
        if (account.OpenItemMode != "NONE") Rule("OPEN_ITEM_MODE");
        if (otherRolesOfAccount.Any(r => r != role.Code)) Rule("ONE_ROLE_PER_ACCOUNT");
        // F8, 3: a row with a currency needs an account without currency or in that currency, a default row an account without
        if (currencyCode is null ? account.CurrencyCode is not null : account.CurrencyCode is not null && account.CurrencyCode != currencyCode)
            Rule("CURRENCY");
        // F8, 4: a tax account accepts only VAT_SETTLEMENT
        if (account.TaxAccount && role.Code != "VAT_SETTLEMENT") Rule("TAX_ACCOUNT");
        if (role.Code == "RETAINED_EARNINGS" && account.RevalueFlag) v.Add(new RoleViolation(RevalueOnRetainedEarnings, null));
        return v;
    }

    /// <summary>The shape of a request: which of currency, counterpart and VAT registration the role takes (6.3).</summary>
    public static List<(string Path, string Reason)> CheckShape(AssignAccountRoleRequest r)
    {
        var errors = new List<(string, string)>();
        if (r.CompanyId == Guid.Empty) errors.Add(("company_id", "required"));
        if (r.AccountId == Guid.Empty) errors.Add(("account_id", "required"));
        RoleDefinition? role = Find(r.RoleCode);
        if (role is null)
        {
            errors.Add(("role_code", "one of " + string.Join(", ", Definitions.Select(d => d.Code))));
            return errors;
        }
        if (r.CurrencyCode is not null && !role.PerCurrency) errors.Add(("currency_code", $"{role.Code} is not per currency"));
        if (r.CurrencyCode is not null && (r.CurrencyCode.Length != 3 || !r.CurrencyCode.All(c => c is >= 'A' and <= 'Z')))
            errors.Add(("currency_code", "three capital letters"));
        if (role.PerCounterpart && r.CounterpartCompanyId is null) errors.Add(("counterpart_company_id", $"required for {role.Code}"));
        if (!role.PerCounterpart && r.CounterpartCompanyId is not null) errors.Add(("counterpart_company_id", $"{role.Code} has no counterpart"));
        if (r.CounterpartCompanyId == r.CompanyId) errors.Add(("counterpart_company_id", "must be another company"));
        if (!role.PerVatRegistration && r.VatRegistrationId is not null) errors.Add(("vat_registration_id", $"{role.Code} has no VAT registration"));
        return errors;
    }

    private static readonly FieldSpec[] Spec =
    [
        new("company_id", FieldType.Id), new("role_code", FieldType.Text), new("account_id", FieldType.Id),
        new("currency_code", FieldType.Text, true), new("counterpart_company_id", FieldType.Id, true),
        new("vat_registration_id", FieldType.Id, true), new("effective_from", FieldType.Timestamp, true)
    ];

    public static byte[] Fingerprint(AssignAccountRoleRequest r) =>
        Periods.CommandFingerprint.Compute("account-role-assignments:create", Spec, new Dictionary<string, object?>
        {
            ["company_id"] = r.CompanyId, ["role_code"] = r.RoleCode, ["account_id"] = r.AccountId,
            ["currency_code"] = r.CurrencyCode, ["counterpart_company_id"] = r.CounterpartCompanyId,
            ["vat_registration_id"] = r.VatRegistrationId, ["effective_from"] = r.EffectiveFrom
        });
}
