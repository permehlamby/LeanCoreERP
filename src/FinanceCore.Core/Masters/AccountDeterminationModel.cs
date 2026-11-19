namespace FinanceCore.Core.Masters;

/// <summary>The fields of an account determination version (10, 3.9) and the open-item mode that each role needs of its account (the role table, 07, 6.3).</summary>
public static class AccountDeterminationFields
{
    public static readonly string[] Roles =
    [
        "INVENTORY", "COGS", "GR_IR", "PRICE_VARIANCE", "ADJUSTMENT_GAIN", "ADJUSTMENT_LOSS", "NEG_STOCK_VARIANCE", "LANDED_COST_CLEARING", "STOCK_TRANSFER_CLEARING",
        "AR_CONTROL", "AP_CONTROL", "REVENUE", "PURCHASE_EXPENSE", "DROPSHIP_CLEARING", "GR_IR_WRITE_OFF"
    ];

    public static readonly IReadOnlyList<MasterField> Version =
    [
        new("role_code", MasterKind.Code, 50, false, Roles),
        new("item_group_id", MasterKind.Id), new("warehouse_id", MasterKind.Id), new("partner_id", MasterKind.Id), new("item_id", MasterKind.Id),
        new("valid_from", MasterKind.Date, 0, false), new("valid_to", MasterKind.Date), new("priority", MasterKind.SmallInt, 0, false), new("account_id", MasterKind.Id, 0, false)
    ];

    public static readonly string[] KeyColumns = ["role_code", "item_group_id", "warehouse_id", "partner_id", "item_id"];

    /// <summary>GR_IR and DROPSHIP_CLEARING are GL open-item accounts, AR_CONTROL and AP_CONTROL control accounts, every other role a plain account (3.9).</summary>
    public static string RequiredOpenItemMode(string role) => role switch
    {
        "GR_IR" or "DROPSHIP_CLEARING" => "GL_OPEN_ITEM",
        "AR_CONTROL" or "AP_CONTROL" => "CONTROL",
        _ => "NONE"
    };
}

public sealed record CreateAccountDeterminationRequest(Guid CompanyId, IReadOnlyDictionary<string, object?> Fields);

/// <summary>A later version of an existing determination. A version is insert-only.</summary>
public sealed record CreateAccountDeterminationVersionRequest(Guid DeterminationId, IReadOnlyDictionary<string, object?> Fields);

public sealed record AccountDeterminationResult(Guid DeterminationId, DateOnly ValidFrom);

public static class AccountDeterminationRules
{
    public static byte[] Fingerprint(CreateAccountDeterminationRequest r) =>
        MasterRules.Fingerprint("account-determinations:create", r.CompanyId.ToString("D") + "|" + MasterRules.Text(r.Fields));

    public static byte[] Fingerprint(CreateAccountDeterminationVersionRequest r) =>
        MasterRules.Fingerprint("account-determinations:create-version", r.DeterminationId.ToString("D") + "|" + MasterRules.Text(r.Fields));
}
