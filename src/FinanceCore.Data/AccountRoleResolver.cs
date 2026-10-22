using NpgsqlTypes;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>Resolution of an account role at command time (07, 6.3): the most specific row (currency, counterpart and VAT
/// registration match), then the highest effective_from not later than now. The caller decides what no match means
/// (CONFIG.ROLE_NOT_ASSIGNED for a command that needs the role).</summary>
internal static class AccountRoleResolver
{
    public static async Task<Guid?> ResolveAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, string roleCode, string? currency, Guid? counterpart, Guid? vatRegistration,
        CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select account_id from core.account_role_assignment " +
            "where tenant_id = @t and company_id = @c and role_code = @r and effective_from <= now() " +
            "and (currency_code is null or currency_code = @cur) " +
            "and (counterpart_company_id is null or counterpart_company_id = @cp) " +
            "and (vat_registration_id is null or vat_registration_id = @v) " +
            "order by (currency_code is not null) desc, (counterpart_company_id is not null) desc, " +
            "(vat_registration_id is not null) desc, effective_from desc limit 1");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("r", roleCode);
        cmd.Parameters.Add(new NpgsqlParameter("cur", NpgsqlDbType.Text) { Value = (object?)currency ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("cp", NpgsqlDbType.Uuid) { Value = (object?)counterpart ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Uuid) { Value = (object?)vatRegistration ?? DBNull.Value });
        return await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null;
    }
}
