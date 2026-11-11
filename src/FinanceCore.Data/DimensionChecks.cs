using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data;

/// <summary>
/// The dimensions of a line checked as a posting checks them (06, 5.2 P2 and P4, 02, 5.3): every value exists, is active, belongs to the type it is used
/// for (the typed column of its role, or the free type of the entry), lies within its validity at the posting date and is not restricted to another
/// company, and the rules of the account hold: MANDATORY needs a value of the type, FORBIDDEN takes none, no row means OPTIONAL (07, 6.2). The rules
/// in force at command time count (Q8). All problems are reported together, in the order of the lines.
/// </summary>
internal static class DimensionChecks
{
    internal sealed record TypeRow(Guid TypeId, string Code, string Kind, string? StandardRole, bool Active);
    internal sealed record ValueRow(Guid ValueId, Guid TypeId, Guid? CompanyId, DateOnly ValidFrom, DateOnly? ValidTo, bool Active);

    internal static readonly (string Column, string Role)[] Typed =
    [
        ("cost_center_id", "COST_CENTER"), ("project_id", "PROJECT"), ("profit_center_id", "PROFIT_CENTER"), ("business_unit_id", "BUSINESS_UNIT")
    ];

    internal static Guid? TypedValue(LineDimensions d, string column) => column switch
    {
        "cost_center_id" => d.CostCenterId, "project_id" => d.ProjectId, "profit_center_id" => d.ProfitCenterId, _ => d.BusinessUnitId
    };

    internal static async Task<Dictionary<Guid, TypeRow>> ReadTypesAsync(TenantTransaction tx, Guid tenantId, CancellationToken ct)
    {
        var types = new Dictionary<Guid, TypeRow>();
        await using var cmd = tx.Command("select dimension_type_id, type_code, kind, standard_role, active from core.dimension_type where tenant_id = @t");
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            types[r.GetGuid(0)] = new TypeRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4));
        return types;
    }

    internal static async Task<Dictionary<Guid, ValueRow>> ReadValuesAsync(TenantTransaction tx, Guid tenantId, Guid[] ids, CancellationToken ct)
    {
        var values = new Dictionary<Guid, ValueRow>();
        if (ids.Length == 0) return values;
        await using var cmd = tx.Command(
            "select dimension_value_id, dimension_type_id, company_id, valid_from, valid_to, active from core.dimension_value " +
            "where tenant_id = @t and dimension_value_id = any(@ids)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("ids", ids);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            values[r.GetGuid(0)] = new ValueRow(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetFieldValue<DateOnly>(3),
                r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4), r.GetBoolean(5));
        return values;
    }

    /// <summary>The rule rows of the given accounts: account to (type to rule). A type without a row is OPTIONAL.</summary>
    internal static async Task<Dictionary<Guid, Dictionary<Guid, string>>> ReadRulesAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid[] accountIds, CancellationToken ct)
    {
        var rules = new Dictionary<Guid, Dictionary<Guid, string>>();
        await using var cmd = tx.Command(
            "select account_id, dimension_type_id, rule from core.account_dimension_rule where tenant_id = @t and company_id = @c and account_id = any(@a) and rule <> 'OPTIONAL'");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("a", accountIds);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (!rules.TryGetValue(r.GetGuid(0), out var byType)) rules[r.GetGuid(0)] = byType = [];
            byType[r.GetGuid(1)] = r.GetString(2);
        }
        return rules;
    }

    /// <summary>One dimension a line names: where it is written in the request, its type and its value.</summary>
    internal sealed record Given(string Path, Guid? TypeId, Guid ValueId, bool Free);

    /// <summary>The entries of one line, with the typed ones resolved to the standard type of their role. A problem of the shape itself (a free entry
    /// naming a standard type, a type twice) is an error of the line.</summary>
    internal static List<Given> Entries(int line, LineDimensions d, IReadOnlyDictionary<string, Guid> standardTypes, IReadOnlyDictionary<Guid, TypeRow> types, List<CommandError> errors)
    {
        var given = new List<Given>();
        foreach (var (column, role) in Typed)
            if (TypedValue(d, column) is { } v) given.Add(new Given($"lines[{line}].dimensions.{column}", standardTypes.GetValueOrDefault(role), v, false));
        var seen = new HashSet<Guid>();
        for (int j = 0; j < (d.Free?.Count ?? 0); j++)
        {
            FreeDimension f = d.Free![j];
            string path = $"lines[{line}].dimensions.free[{j}]";
            if (!types.TryGetValue(f.DimensionTypeId, out TypeRow? type))
                errors.Add(Invalid(path + ".dimension_type_id", "TYPE_NOT_FOUND", ("dimension_type_id", f.DimensionTypeId)));
            else if (type.Kind != "FREE")
                errors.Add(Invalid(path + ".dimension_type_id", "TYPE_NOT_FREE", ("dimension_type_id", f.DimensionTypeId), ("type_code", type.Code)));
            else if (!seen.Add(f.DimensionTypeId))
                errors.Add(Invalid(path + ".dimension_type_id", "DUPLICATE_TYPE", ("dimension_type_id", f.DimensionTypeId), ("type_code", type.Code)));
            else
                given.Add(new Given(path + ".dimension_value_id", f.DimensionTypeId, f.DimensionValueId, true));
        }
        return given;
    }

    internal static CommandError Invalid(string path, string reason, params (string Key, object? Value)[] p) =>
        JournalDocumentPoster.Error("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "P4", path, [("reason", reason), .. p]);

    /// <summary>The state of one value for one use: its type, its state, its validity at the date and its company.</summary>
    internal static CommandError? CheckValue(Given g, ValueRow? value, IReadOnlyDictionary<Guid, TypeRow> types, DateOnly date, Guid companyId)
    {
        if (value is null) return Invalid(g.Path, "NOT_FOUND", ("dimension_value_id", g.ValueId));
        if (g.TypeId is null || value.TypeId != g.TypeId) return Invalid(g.Path, "TYPE_MISMATCH", ("dimension_value_id", g.ValueId));
        if (types.TryGetValue(value.TypeId, out TypeRow? type) && !type.Active) return Invalid(g.Path, "TYPE_INACTIVE", ("dimension_value_id", g.ValueId), ("type_code", type.Code));
        if (!value.Active) return Invalid(g.Path, "INACTIVE", ("dimension_value_id", g.ValueId));
        if (value.ValidFrom > date || (value.ValidTo is { } to && to < date))
            return Invalid(g.Path, "NOT_VALID_AT_DATE", ("dimension_value_id", g.ValueId), ("date", date));
        if (value.CompanyId is { } restricted && restricted != companyId) return Invalid(g.Path, "COMPANY_MISMATCH", ("dimension_value_id", g.ValueId));
        return null;
    }

    /// <summary>The rule check of one line: what the account's rules ask for against what the line carries.</summary>
    internal static void CheckRules(
        int line, string accountPath, Guid accountId, IReadOnlyDictionary<Guid, Dictionary<Guid, string>> rules, IReadOnlyCollection<Given> given,
        IReadOnlyDictionary<Guid, TypeRow> types, List<CommandError> errors)
    {
        if (!rules.TryGetValue(accountId, out var byType)) return;
        foreach (var (typeId, rule) in byType.OrderBy(kv => types.GetValueOrDefault(kv.Key)?.Code, StringComparer.Ordinal))
        {
            if (!types.TryGetValue(typeId, out TypeRow? type) || !type.Active) continue;
            bool has = given.Any(g => g.TypeId == typeId);
            if (rule == "MANDATORY" && !has)
                errors.Add(JournalDocumentPoster.Error("ACCOUNT_RULE.DIMENSION_REQUIRED", "P4", $"lines[{line}].dimensions",
                    ("account_id", accountId), ("dimension_type_id", typeId), ("type_code", type.Code)));
            else if (rule == "FORBIDDEN" && has)
                errors.Add(JournalDocumentPoster.Error("ACCOUNT_RULE.DIMENSION_FORBIDDEN", "P4", given.First(g => g.TypeId == typeId).Path,
                    ("account_id", accountId), ("dimension_type_id", typeId), ("type_code", type.Code)));
        }
    }

    /// <summary>Checks the dimensions of the base lines of a posting. The lines are given by index so that the paths name the request.</summary>
    internal static async Task<List<CommandError>> CheckPostingAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly postingDate, IReadOnlyList<RequestLine> lines, CancellationToken ct)
    {
        var errors = new List<CommandError>();
        var types = await ReadTypesAsync(tx, tenantId, ct);
        var standard = types.Values.Where(t => t.StandardRole is not null).ToDictionary(t => t.StandardRole!, t => t.TypeId);
        var perLine = new List<List<Given>>();
        foreach (var (l, i) in lines.Select((l, i) => (l, i)))
            perLine.Add(l.Dimensions is { } d ? Entries(i, d, standard, types, errors) : []);

        Guid[] valueIds = perLine.SelectMany(g => g).Select(g => g.ValueId).Distinct().ToArray();
        var values = await ReadValuesAsync(tx, tenantId, valueIds, ct);
        var rules = await ReadRulesAsync(tx, tenantId, companyId, lines.Select(l => l.AccountId).Distinct().ToArray(), ct);
        for (int i = 0; i < lines.Count; i++)
        {
            foreach (Given g in perLine[i])
                if (CheckValue(g, values.GetValueOrDefault(g.ValueId), types, postingDate, companyId) is { } error) errors.Add(error);
            CheckRules(i, $"lines[{i}].account_id", lines[i].AccountId, rules, perLine[i], types, errors);
        }
        return errors;
    }
}
