using FinanceCore.Core.Commands;

namespace FinanceCore.Data;

/// <summary>
/// The insert into read_access_log (09, 5.2), made in the transaction of the read by the read role, so a response is never returned without its log row.
/// A read writes no outbox event and takes no outbox_head lock (A6). The field groups are those of 5.2.
/// </summary>
internal static class ReadAccessLog
{
    public const string Identity = "PII_IDENTITY", Address = "PII_ADDRESS", Contact = "PII_CONTACT", LegalSnapshot = "LEGAL_SNAPSHOT", Search = "SEARCH";

    public static async Task WriteAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, string operation, string fieldGroup, Guid? companyId, Guid? partnerId, Guid? documentId,
        string? purposeCode, int? resultPosition, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.read_access_log (tenant_id, actor, operation, company_id, partner_id, document_id, field_group, purpose_code, correlation_id, result_position) " +
            "values (@t, @actor, @op, @company, @partner, @doc, @group, @purpose, @corr, @pos)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("actor", ctx.Actor); cmd.Parameters.AddWithValue("op", operation);
        cmd.Parameters.AddWithValue("group", fieldGroup); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("company", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)companyId ?? DBNull.Value });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("partner", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)partnerId ?? DBNull.Value });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("doc", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)documentId ?? DBNull.Value });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("purpose", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)purposeCode ?? DBNull.Value });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("pos", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)resultPosition ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A purpose code is a core.code (1 to 50 characters). The list of valid purposes is configuration of the compliance domain (Q5) and is not checked here.</summary>
    public static bool ValidPurpose(string? purpose) =>
        purpose is null || System.Text.RegularExpressions.Regex.IsMatch(purpose, "^[A-Za-z0-9_.-]{1,50}$");
}
