namespace FinanceCore.Core.Masters;

// ---- company-consolidation-settings:create-version (23, 3.1, permission write.config)
public sealed record CreateConsolidationSettingVersionRequest(Guid CompanyId, IReadOnlyDictionary<string, object?> Fields);

public sealed record ConsolidationSettingVersionResult(Guid CompanyId, DateOnly EffectiveFrom);

public static class ConsolidationFields
{
    public static readonly string[] Methods = ["FULL", "PROPORTIONAL", "EQUITY", "NOT_CONSOLIDATED"];
    public static readonly string[] TranslationMethods = ["NONE", "CURRENT_RATE", "TEMPORAL"];

    public static readonly IReadOnlyList<MasterField> Version =
    [
        new("effective_from", MasterKind.Date, 0, false), new("parent_company_id", MasterKind.Id), new("ownership_pct", MasterKind.Rate),
        new("consolidation_method", MasterKind.Code, 50, false, Methods), new("translation_method", MasterKind.Code, 50, false, TranslationMethods)
    ];

    /// <summary>NONE exactly when the local currency equals the group currency (23, 3.1, CONFIG.TRANSLATION_METHOD_INVALID).</summary>
    public static bool TranslationFits(string method, string localCurrency, string groupCurrency) => (method == "NONE") == (localCurrency == groupCurrency);

    /// <summary>The percentage is above 0 and at most 100, and present exactly when there is a parent (CONFIG.OWNERSHIP_INVALID).</summary>
    public static bool OwnershipFits(Guid? parent, decimal? pct) => (parent is null) == (pct is null) && (pct is null || (pct > 0m && pct <= 100m));

    public static byte[] Fingerprint(CreateConsolidationSettingVersionRequest r) =>
        MasterRules.Fingerprint("company-consolidation-settings:create-version", $"{r.CompanyId:D}|{MasterRules.Text(r.Fields)}");
}

// ---- group-chart-accounts (07, 6.4, 23, 4, ADR-227): create, update, deactivate, reactivate on the generic master commands (write.config), get and list (read.config)
public static class GroupChartFields
{
    public static readonly string[] AccountTypes = ["ASSET", "LIABILITY", "EQUITY", "REVENUE", "EXPENSE"];
    public static readonly string[] RateBases = ["CLOSING", "AVERAGE", "HISTORICAL"];

    /// <summary>The group and the account type are fixed at creation: a mapping and a consolidation rely on them.</summary>
    public static readonly IReadOnlyList<MasterField> Create =
    [
        new("group_id", MasterKind.Id, 0, false), new("code", MasterKind.Code, 50, false), new("name", MasterKind.Text, 100, false),
        new("account_type", MasterKind.Code, 50, false, AccountTypes), new("translation_rate_basis", MasterKind.Code, 50, true, RateBases)
    ];

    public static readonly IReadOnlyList<MasterField> Update =
    [
        new("code", MasterKind.Code, 50, false), new("name", MasterKind.Text, 100, false), new("translation_rate_basis", MasterKind.Code, 50, true, RateBases)
    ];

    /// <summary>The basis the consolidation app uses when the account has none (23, 4): ASSET and LIABILITY CLOSING, REVENUE and EXPENSE AVERAGE, EQUITY HISTORICAL.</summary>
    public static string EffectiveBasis(string accountType, string? basis) => basis ?? accountType switch
    {
        "REVENUE" or "EXPENSE" => "AVERAGE",
        "EQUITY" => "HISTORICAL",
        _ => "CLOSING"
    };
}

// ---- account-group-mappings:create (07, 6.4, 23, 4, ADR-227, permission write.config) and account-group-mappings:list (read.config)
/// <summary>A company account maps to a group account from ValidFrom. The open mapping of the account, if any, ends on ValidFrom.</summary>
public sealed record CreateAccountGroupMappingRequest(Guid CompanyId, Guid AccountId, Guid GroupAccountId, DateOnly ValidFrom);

public sealed record AccountGroupMappingResult(Guid CompanyId, Guid AccountId, DateOnly ValidFrom, Guid GroupAccountId, DateOnly? ClosedPreviousFrom);

public static class AccountGroupMappingRules
{
    public static byte[] Fingerprint(CreateAccountGroupMappingRequest r) =>
        MasterRules.Fingerprint("account-group-mappings:create", $"{r.CompanyId:D}|{r.AccountId:D}|{r.GroupAccountId:D}|{r.ValidFrom:yyyy-MM-dd}");
}

// ---- exchange-rates:mark-used (23, 5, 07, 7.3, ADR-225, permission write.config): the usage lock for rates that only an app used
public sealed record MarkRatesUsedRequest(IReadOnlyList<Guid> RateIds, string PurposeRef);

/// <summary>Marked: rows that got first_used_at now. AlreadyUsed: rows that had it before (nothing changed).</summary>
public sealed record MarkedRatesResult(string PurposeRef, IReadOnlyList<Guid> Marked, IReadOnlyList<Guid> AlreadyUsed);

public static class RateUsageRules
{
    /// <summary>The batch limit of the command (07, 7.3 names master_batch_max_items, which has no home yet: a constant).</summary>
    public const int MaxRateIds = 500;

    public static byte[] Fingerprint(MarkRatesUsedRequest r) =>
        MasterRules.Fingerprint("exchange-rates:mark-used", $"{r.PurposeRef}|" + string.Join(",", r.RateIds.Select(i => i.ToString("D"))));
}
