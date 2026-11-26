using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Reads;

namespace FinanceCore.Core.Feed;

// ---- extension attributes (15, 3.2, 3.5, 3.6, 4): the namespaces of the apps, the definitions, the values and the reads.
public sealed record CreateAppNamespaceRequest(Guid AppId, string Namespace);

public sealed record AppNamespaceResult(Guid AppId, string Namespace);

/// <summary>extension-definitions:create (15, 3.5): the owner is the app of the caller (app_code), which must own the namespace. AllowedValues and MaxLength are for TEXT, the precision and scale for DECIMAL, RefEntityType for REF.</summary>
public sealed record CreateExtensionDefinitionRequest(
    string EntityType, string Namespace, string AttrKey, string DataType, int? MaxLength = null, int? DecimalPrecision = null, int? DecimalScale = null,
    IReadOnlyList<string>? AllowedValues = null, string? RefEntityType = null, bool IsPii = false);

/// <summary>extension-definitions:update: the columns that may change. AllowedValues replaces the list, ClearAllowedValues removes it. IsPii cannot change once a value exists (EXT.DEFINITION_LOCKED).</summary>
public sealed record UpdateExtensionDefinitionRequest(
    Guid DefinitionId, long ExpectedVersion, int? MaxLength = null, int? DecimalPrecision = null, int? DecimalScale = null, IReadOnlyList<string>? AllowedValues = null,
    bool ClearAllowedValues = false, bool? IsPii = null);

public sealed record DeactivateExtensionDefinitionRequest(Guid DefinitionId, long ExpectedVersion);

public sealed record ExtensionDefinitionResult(Guid DefinitionId, string EntityType, string Namespace, string AttrKey, string DataType, bool IsPii, bool Active, long RowVersion);

/// <summary>extensions:set (15, 4.3): values of one record in one namespace of the caller, as "namespace.attr" to value (a JSON string, number, boolean or date text). ExpectedVersion is compared with the counter of the (record, namespace).</summary>
public sealed record SetExtensionsRequest(string EntityType, string EntityKey, IReadOnlyDictionary<string, JsonElement> Values, long? ExpectedVersion = null);

public sealed record ClearExtensionsRequest(string EntityType, string EntityKey, IReadOnlyList<string> Keys, long? ExpectedVersion = null);

public sealed record ExtensionsResult(string EntityType, string EntityKey, string Namespace, long Version, IReadOnlyList<string> Changed);

public sealed record ListExtensionDefinitionsRequest(string? EntityType = null, string? Namespace = null, string? AttrKey = null, bool? Active = null, int? Limit = null, string? Cursor = null);

public sealed record ExtensionDefinitionList(IReadOnlyList<JsonElement> Items, ReadPage Page);

/// <summary>A filter ext.&lt;namespace&gt;.&lt;attr&gt; of a list (15, 4.4): Field is "namespace.attr", Op one of =, !=, &lt;, &lt;=, &gt;, &gt;=, in, is_null, contains. Value is the operand (is_null: "true" or "false"), Values the list of in.</summary>
public sealed record ExtensionFilter(string Field, string Op, string? Value = null, IReadOnlyList<string>? Values = null);

/// <summary>One extendable entity type (15, 4.1): its table, its key columns in key order without the tenant (G a uuid, I an integer), the company column when it has one, and the status column and the values that close the record.</summary>
public sealed record ExtendableEntity(string Type, string Table, string[] KeyColumns, string KeyTypes, string? CompanyColumn, string? StatusColumn = null, string[]? ClosedValues = null, string? AnonymisedValue = null)
{
    public string KeyExpression(string alias) => string.Join(" || '/' || ", KeyColumns.Select(c => $"{alias}.{c}::text"));
}

public static class ExtensionRules
{
    public const string DefinePermission = "ext.define", WritePermission = "ext.write", ReadDefinitionsPermission = "read.config";
    public const int MaxFilters = 5;

    public static readonly IReadOnlyList<ExtendableEntity> Entities =
    [
        new("BUSINESS_PARTNER", "business_partner", ["partner_id"], "G", null, "status", null, "ANONYMISED"),
        new("PARTNER_COMPANY_RELATION", "partner_company_relation", ["partner_id", "company_id"], "GG", "company_id"),
        new("ACCOUNT", "gl_account", ["company_id", "account_id"], "GG", "company_id"),
        new("COMPANY", "company", ["company_id"], "G", "company_id"),
        new("ITEM", "item", ["item_id"], "G", null),
        new("ITEM_COMPANY", "item_company", ["item_company_id"], "G", "company_id"),
        new("ITEM_WAREHOUSE", "item_warehouse", ["item_warehouse_id"], "G", "company_id"),
        new("WAREHOUSE", "warehouse", ["warehouse_id"], "G", "company_id"),
        new("SALES_ORDER", "sales_order", ["sales_order_id"], "G", "company_id", "lifecycle_status", ["CLOSED", "CANCELLED"]),
        new("SALES_ORDER_LINE", "sales_order_line", ["sales_order_id", "line_no"], "GI", "company_id", "line_status", ["CLOSED", "CANCELLED"]),
        new("PURCHASE_ORDER", "purchase_order", ["purchase_order_id"], "G", "company_id", "lifecycle_status", ["CLOSED", "CANCELLED"]),
        new("PURCHASE_ORDER_LINE", "purchase_order_line", ["purchase_order_id", "line_no"], "GI", "company_id", "line_status", ["CLOSED", "CANCELLED"]),
        new("OPEN_ITEM", "journal_line_open_item", ["company_id", "document_id", "line_no"], "GGI", "company_id")
    ];

    public static ExtendableEntity? Entity(string type) => Entities.FirstOrDefault(e => e.Type == type);

    public static readonly string[] DataTypes = ["TEXT", "DECIMAL", "DATE", "BOOLEAN", "REF"];
    public static readonly string[] Operators = ["=", "!=", "<", "<=", ">", ">=", "in", "is_null", "contains"];

    public static string Fingerprint(string command, object request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command + "\u001f" + JsonSerializer.Serialize(request))));

    public static byte[] FingerprintBytes(string command, object request) => SHA256.HashData(Encoding.UTF8.GetBytes(command + "\u001f" + JsonSerializer.Serialize(request)));

    /// <summary>"namespace.attr" into its two parts, or null.</summary>
    public static (string Namespace, string Attr)? SplitKey(string key)
    {
        string[] p = (key ?? "").Split('.');
        return p.Length == 2 && p[0].Length > 0 && p[1].Length > 0 ? (p[0], p[1]) : null;
    }
}
