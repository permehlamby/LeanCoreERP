using FinanceCore.Core.Commands;
using FinanceCore.Core.Reads;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// The operator contains (09, 2.4.1) as SQL: a literal substring, case- and accent-insensitive, on the fields of an allowlist and at least three characters. The column names come from the
/// allowlist of the resource, never from the request, and only the values are parameters. Both sides go through core.search_norm and the value through core.search_pattern (migration 0056),
/// which escapes the characters of LIKE after the normalisation, so the value is never a pattern.
/// </summary>
internal static class SearchFilters
{
    public static CommandError? Validate(IReadOnlyDictionary<string, string>? contains, string[] allowed, string path)
    {
        foreach ((string field, string value) in contains ?? new Dictionary<string, string>())
        {
            if (!allowed.Contains(field)) return JournalDocumentPoster.Error("READ.INVALID_FILTER", "P1", path, ("field", field), ("allowed", allowed));
            if (string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Length < SearchRules.MinLength)
                return JournalDocumentPoster.Error("READ.CONTAINS_TOO_SHORT", "P1", path + "." + field, ("minimum", SearchRules.MinLength));
        }
        return null;
    }

    /// <summary>" and core.search_norm(alias.field) like core.search_pattern(@prefixN)" per field, for fields that Validate has accepted.</summary>
    public static string Where(IReadOnlyDictionary<string, string>? contains, string alias, string prefix) =>
        string.Concat((contains ?? new Dictionary<string, string>()).Keys.Select((f, n) => $" and core.search_norm({alias}.{f}) like core.search_pattern(@{prefix}{n})"));

    public static void Bind(NpgsqlCommand cmd, IReadOnlyDictionary<string, string>? contains, string prefix)
    {
        int n = 0;
        foreach (string value in (contains ?? new Dictionary<string, string>()).Values) cmd.Parameters.AddWithValue($"{prefix}{n++}", value);
    }
}
