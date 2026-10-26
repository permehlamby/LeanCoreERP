using FinanceCore.Core.Commands;
using FinanceCore.Core.Retention;

namespace FinanceCore.Data;

/// <summary>
/// The legal snapshot of a document (02, 4) for every command that writes a journal document. The retention date is the later of the LEGAL_SNAPSHOT
/// and the LEDGER rule (12, 4.1, consistency rule), dated from the end of the fiscal year of the document and its posting date. Without a rule for
/// both classes nothing is written and the command answers RETENTION.NO_RULE. The personal columns stay empty until the document has a partner.
/// </summary>
internal static class LegalSnapshots
{
    /// <summary>Writes the snapshot row in the transaction of the document. Null on success, else the error that rejects the command.</summary>
    internal static async Task<CommandError?> WriteAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, int fiscalYear, DateOnly postingDate, DateTime createdAt, CancellationToken ct)
    {
        DateOnly fiscalYearEnd;
        await using (var cmd = tx.Command(
            "select end_date from core.fiscal_year where tenant_id = @t and company_id = @c and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", fiscalYear);
            object? v = await cmd.ExecuteScalarAsync(ct);
            if (v is not DateTime end) return new CommandError("SYSTEM.FISCAL_YEAR_MISSING", "P7", "", new Dictionary<string, object?> { ["fiscal_year"] = fiscalYear });
            fiscalYearEnd = DateOnly.FromDateTime(end);
        }

        var versions = await RetentionRuleCommands.ReadVersionsAsync(tx, tenantId, companyId, ct);
        DateOnly? until = RetentionRules.ForLegalSnapshot(versions, createdAt, fiscalYearEnd, postingDate);
        if (until is null)
        {
            var missing = new[] { "LEDGER", "LEGAL_SNAPSHOT" }.Where(c => RetentionRules.For(versions, c, createdAt, fiscalYearEnd, postingDate) is null).ToArray();
            return new CommandError("RETENTION.NO_RULE", "P6", "", new Dictionary<string, object?> { ["record_classes"] = missing });
        }

        await using var insert = tx.Command(
            "insert into core.document_legal_snapshot (tenant_id, company_id, document_id, retention_until) values (@t, @c, @d, @u)");
        insert.Parameters.AddWithValue("t", tenantId); insert.Parameters.AddWithValue("c", companyId); insert.Parameters.AddWithValue("d", documentId);
        insert.Parameters.Add(new Npgsql.NpgsqlParameter("u", NpgsqlTypes.NpgsqlDbType.Date) { Value = until.Value });
        await insert.ExecuteNonQueryAsync(ct);
        return null;
    }
}
