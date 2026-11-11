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
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, int fiscalYear, DateOnly postingDate, DateTime createdAt, CancellationToken ct,
        Guid? partnerId = null, Guid? copyFromDocumentId = null)
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

        const string columns = "partner_id, legal_name, address_line_1, address_line_2, postal_code, city, country_code, partner_vat_id, " +
                               "electronic_address, electronic_address_scheme, legal_registration_id";
        // the partner's data as on the document: from the master now (partner_vat_id waits for the partner's tax registration), or, for a mirror,
        // the data of the document it reverses
        string source = (partnerId, copyFromDocumentId) switch
        {
            (not null, _) =>
                "select p.partner_id, i.legal_name, i.address_line_1, i.address_line_2, i.postal_code, i.city, i.country_code, null::varchar, " +
                "i.electronic_address, i.electronic_address_scheme, i.legal_registration_id " +
                "from core.business_partner p join core.business_partner_pii i on i.tenant_id = p.tenant_id and i.partner_id = p.partner_id " +
                "where p.tenant_id = @t and p.partner_id = @p",
            (_, not null) =>
                "select " + columns + " from core.document_legal_snapshot where tenant_id = @t and company_id = @c and document_id = @from",
            _ => "select null::uuid, null::varchar, null::varchar, null::varchar, null::varchar, null::varchar, null::char(2), null::varchar, null::varchar, null::text, null::varchar"
        };
        await using var insert = tx.Command(
            "insert into core.document_legal_snapshot (tenant_id, company_id, document_id, " + columns + ", retention_until) " +
            "select @t, @c, @d, s.*, @u from (" + source + ") s");
        insert.Parameters.AddWithValue("t", tenantId); insert.Parameters.AddWithValue("c", companyId); insert.Parameters.AddWithValue("d", documentId);
        if (partnerId is { } pid) insert.Parameters.AddWithValue("p", pid);
        if (copyFromDocumentId is { } from) insert.Parameters.AddWithValue("from", from);
        insert.Parameters.Add(new Npgsql.NpgsqlParameter("u", NpgsqlTypes.NpgsqlDbType.Date) { Value = until.Value });
        if (await insert.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The partner or the document to take the snapshot from does not exist");
        return null;
    }
}
