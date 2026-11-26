using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Retention;
using Npgsql;
using static FinanceCore.Data.MasterSupport;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The address snapshots of an order (11, 4.5, 4.7): a sales order keeps the ship-to and the bill-to address, a purchase order the ship-from address of the supplier, copied from the partner at
/// placement unless the caller gives another one. The country of the ship-to address is the default destination country of a sales line (4.5). Built choices are in 07, 13.2bl.
/// </summary>
public sealed partial class OrderCommands
{
    private sealed record SnapshotRow(string Role, string? Name, string? Line1, string? Line2, string? PostalCode, string? City, string Country, string? Contact, bool Copied);

    /// <summary>The addresses of a new order: those the caller gives (roles of the kind, a country that exists) and for the others the address of the partner.</summary>
    private static async Task<(List<SnapshotRow> Rows, CommandError? Error)> ResolveAddressesAsync(
        TenantTransaction tx, OrderKind kind, Guid tenantId, Guid party, IReadOnlyDictionary<string, OrderAddressInput>? given, CancellationToken ct)
    {
        var roles = OrderRules.AddressRoles(kind.Kind);
        foreach (var (role, input) in given ?? new Dictionary<string, OrderAddressInput>())
        {
            if (!roles.Contains(role)) return ([], Error("SCHEMA.INVALID_VALUE", "P1", $"/addresses/{role}", ("reason", "the address roles of a " + kind.Kind.ToLowerInvariant() + " order are " + string.Join(", ", roles))));
            if (string.IsNullOrWhiteSpace(input.CountryCode) || !await ExistsAsync(tx, "select 1 from platform.ref_country where country_code = @x", tenantId, ("x", input.CountryCode)))
                return ([], Error("CONFIG.INVALID_REFERENCE", "P2", $"/addresses/{role}/country_code", ("reason", "no such country")));
        }
        SnapshotRow? fromPartner = null;
        if (roles.Any(r => given is null || !given.ContainsKey(r)))
        {
            await using var cmd = tx.Command("select legal_name, address_line_1, address_line_2, postal_code, city, country_code, contact_person from core.business_partner_pii where tenant_id = @t and partner_id = @p");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", party);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return ([], Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/" + kind.PartyColumn, ("reason", "the partner has no address data")));
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim();
            fromPartner = new SnapshotRow("", S(0), S(1), S(2), S(3), S(4), r.GetString(5).Trim(), S(6), true);
        }
        var rows = new List<SnapshotRow>();
        foreach (string role in roles)
            rows.Add(given is not null && given.TryGetValue(role, out OrderAddressInput? a)
                ? new SnapshotRow(role, a.Name, a.AddressLine1, a.AddressLine2, a.PostalCode, a.City, a.CountryCode, a.ContactReference, false)
                : fromPartner! with { Role = role });
        return (rows, null);
    }

    /// <summary>The retention date of the snapshots: the rule of the class ADDRESS_SNAPSHOT (12, 4.1). A tenant without that rule gets none: the retention job sets it when the rule exists.</summary>
    private static async Task<DateOnly?> AddressRetentionAsync(TenantTransaction tx, Guid tenantId, Guid companyId, int fiscalYear, DateOnly orderDate, DateTime createdAt, CancellationToken ct)
    {
        DateOnly fiscalYearEnd;
        await using (var cmd = tx.Command("select end_date from core.fiscal_year where tenant_id = @t and company_id = @c and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", fiscalYear);
            if (await cmd.ExecuteScalarAsync(ct) is not DateTime end) return null;
            fiscalYearEnd = DateOnly.FromDateTime(end);
        }
        var versions = await RetentionRuleCommands.ReadVersionsAsync(tx, tenantId, companyId, ct);
        return RetentionRules.For(versions, "ADDRESS_SNAPSHOT", createdAt, fiscalYearEnd, orderDate);
    }

    private static async Task InsertAddressesAsync(TenantTransaction tx, OrderKind kind, Guid tenantId, Guid companyId, Guid orderId, List<SnapshotRow> rows, DateOnly? retentionUntil, CancellationToken ct)
    {
        foreach (SnapshotRow a in rows)
        {
            await using var cmd = tx.Command(
                "insert into core.order_address_snapshot (tenant_id, company_id, order_kind, order_id, address_role, name, address_line_1, address_line_2, postal_code, city, country_code, contact_reference, copied_from_partner, retention_until) " +
                "values (@t, @c, @k, @o, @role, @n, @l1, @l2, @pc, @city, @cc, @cr, @cp, @ru)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("k", kind.Kind); cmd.Parameters.AddWithValue("o", orderId);
            cmd.Parameters.AddWithValue("role", a.Role); cmd.Parameters.AddWithValue("cc", a.Country); cmd.Parameters.AddWithValue("cp", a.Copied);
            foreach (var (name, value) in new[] { ("n", a.Name), ("l1", a.Line1), ("l2", a.Line2), ("pc", a.PostalCode), ("city", a.City), ("cr", a.Contact) })
                cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ru", NpgsqlDbType.Date) { Value = (object?)retentionUntil ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The country of the ship-to address of an existing sales order: the default destination of a line added later (4.5).</summary>
    private static async Task<string?> ShipToCountryAsync(TenantTransaction tx, OrderKind kind, Guid tenantId, Guid companyId, Guid orderId, CancellationToken ct)
    {
        if (kind.Kind != "SALES") return null;
        await using var cmd = tx.Command("select country_code from core.order_address_snapshot where tenant_id = @t and company_id = @c and order_kind = 'SALES' and order_id = @o and address_role = 'SHIP_TO'");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", orderId);
        return (await cmd.ExecuteScalarAsync(ct) as string)?.Trim();
    }
}
