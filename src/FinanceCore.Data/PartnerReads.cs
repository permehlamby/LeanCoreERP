using FinanceCore.Core.Commands;
using FinanceCore.Core.Reads;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// business-partners:get and :list (09, 3, 5): the partner master without personal data with read.config, and with include_pii the personal data of 07, 9.2 with
/// read.pii. Without read.pii the personal fields are left out (not masked) and the response says pii_omitted. Every personal-data read writes its rows to
/// read_access_log in the same transaction (5.2): one row per partner and field group (PII_IDENTITY, PII_ADDRESS, PII_CONTACT), with the position in a list.
/// A RESTRICTED partner (12, 5.3) is read only for the statutory purpose. business-partners:search (5.3) finds partners by prefix or contains on the personal fields, needs read.pii and logs the field group SEARCH and the groups of the fields returned. Not built: company_relations,
/// tax_registrations and bank accounts as includes, extensions, the filter ic_company_id.
/// </summary>
public sealed class PartnerReads(NpgsqlDataSource source)
{
    private const string Operation = "business-partners";

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);

    private static CommandError? Denied(CommandContext ctx) =>
        ctx.Permissions.Contains(PartnerReadRules.ConfigPermission)
            ? null
            : Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", PartnerReadRules.ConfigPermission));

    private static string? Text(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private const string Select =
        "p.partner_id, p.partner_code, p.partner_kind, p.is_customer, p.is_supplier, p.ic_company_id, p.status, p.restriction_status, p.retention_until, p.anonymised_at, p.row_version, " +
        "i.legal_name, i.trade_name, i.address_line_1, i.address_line_2, i.postal_code, i.city, i.country_code, i.email, i.phone, i.contact_person, i.electronic_address, " +
        "i.electronic_address_scheme, i.legal_registration_id " +
        "from core.business_partner p left join core.business_partner_pii i on i.tenant_id = p.tenant_id and i.partner_id = p.partner_id ";

    private static PartnerRead ReadPartner(NpgsqlDataReader r, bool withPii) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetGuid(5), r.GetString(6), r.GetString(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateOnly>(8), r.IsDBNull(9) ? null : DateTime.SpecifyKind(r.GetDateTime(9), DateTimeKind.Utc), r.GetInt64(10),
        withPii && !r.IsDBNull(11)
            ? new PartnerPiiRead(r.GetString(11), Text(r, 12), Text(r, 13), Text(r, 14), Text(r, 15), Text(r, 16), r.GetString(17).Trim(), Text(r, 18), Text(r, 19), Text(r, 20),
                Text(r, 21), Text(r, 22), Text(r, 23))
            : null);

    private static async Task LogAsync(TenantTransaction tx, Guid tenantId, CommandContext ctx, PartnerRead p, string operation, string? purpose, int? position, CancellationToken ct)
    {
        PartnerPiiRead pii = p.Pii!;
        await ReadAccessLog.WriteAsync(tx, tenantId, ctx, operation, ReadAccessLog.Identity, null, p.PartnerId, null, purpose, position, ct);
        await ReadAccessLog.WriteAsync(tx, tenantId, ctx, operation, ReadAccessLog.Address, null, p.PartnerId, null, purpose, position, ct);
        if (pii.Email is not null || pii.Phone is not null || pii.ContactPerson is not null || pii.ElectronicAddress is not null)
            await ReadAccessLog.WriteAsync(tx, tenantId, ctx, operation, ReadAccessLog.Contact, null, p.PartnerId, null, purpose, position, ct);
    }

    private static CommandError? Restricted(PartnerRead p, string? purpose) =>
        p.RestrictionStatus == "RESTRICTED" && purpose != PartnerReadRules.StatutoryPurpose
            ? Error("READ.PARTNER_RESTRICTED", "P2", "include_pii", ("partner_id", p.PartnerId))
            : null;

    public async Task<CommandResult<PartnerResult>> GetAsync(Guid tenantId, CommandContext ctx, GetPartnerRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<PartnerResult>(denied);
        if (req.PartnerId == Guid.Empty) return Reject<PartnerResult>(Error("SCHEMA.INVALID_VALUE", "P1", "partner_id", ("reason", "required")));
        if (!ReadAccessLog.ValidPurpose(req.PurposeCode)) return Reject<PartnerResult>(Error("SCHEMA.INVALID_VALUE", "P1", "purpose_code", ("reason", "1 to 50 characters")));
        bool wantPii = req.IncludePii && ctx.Permissions.Contains(PartnerReadRules.PiiPermission);

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        PartnerRead? partner = null;
        await using (var cmd = tx.Command("select " + Select + "where p.tenant_id = @t and p.partner_id = @p"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", req.PartnerId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) partner = ReadPartner(r, wantPii);
        }
        if (partner is null) return Reject<PartnerResult>(Error("READ.NOT_FOUND", "P2", "partner_id"));
        if (wantPii)
        {
            if (Restricted(partner, req.PurposeCode) is { } restricted) return Reject<PartnerResult>(restricted);
            if (partner.Pii is not null) await LogAsync(tx, tenantId, ctx, partner, Operation + ":get", req.PurposeCode, null, ct);
        }
        if (req.IncludeExtensions) partner = partner with { Extensions = (await ExtensionRead.LoadAsync(tx, tenantId, ctx, "BUSINESS_PARTNER", [partner.PartnerId.ToString("D")], Operation + ":get", ct))[partner.PartnerId.ToString("D")] };
        await tx.CommitAsync(ct);
        return CommandResult<PartnerResult>.Ok(new PartnerResult(partner, req.IncludePii && !wantPii), Guid.Empty);
    }

    /// <summary>business-partners:search (09, 5.3). Needs read.config and read.pii: the search runs on personal data. A RESTRICTED partner is not found without the statutory purpose.</summary>
    public async Task<CommandResult<PartnerList>> SearchAsync(Guid tenantId, CommandContext ctx, SearchPartnersRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<PartnerList>(denied);
        if (!ctx.Permissions.Contains(PartnerReadRules.PiiPermission)) return Reject<PartnerList>(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", PartnerReadRules.PiiPermission)));
        int limit = req.Limit ?? ReadRules.DefaultLimit;
        if (limit is < 1 or > ReadRules.MaxLimit) return Reject<PartnerList>(Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit)));
        if (req.PartnerKind is not (null or "ORGANISATION" or "PERSON")) return Reject<PartnerList>(Error("READ.INVALID_FILTER", "P1", "partner_kind", ("field", "partner_kind")));
        if (req.Status is not (null or "ACTIVE" or "BLOCKED" or "ANONYMISED")) return Reject<PartnerList>(Error("READ.INVALID_FILTER", "P1", "status", ("field", "status")));
        if (!ReadAccessLog.ValidPurpose(req.PurposeCode)) return Reject<PartnerList>(Error("SCHEMA.INVALID_VALUE", "P1", "purpose_code", ("reason", "1 to 50 characters")));
        if (req.Contains is not { Count: > 0 } && req.Prefix is not { Count: > 0 }) return Reject<PartnerList>(Error("SCHEMA.INVALID_VALUE", "P1", "contains", ("reason", "contains or prefix is required")));
        if (SearchFilters.Validate(req.Contains, SearchRules.PartnerFields, "contains") is { } badContains) return Reject<PartnerList>(badContains);
        foreach (string f in (req.Prefix ?? new Dictionary<string, string>()).Keys)
            if (!SearchRules.PartnerFields.Contains(f)) return Reject<PartnerList>(Error("READ.INVALID_FILTER", "P1", "prefix", ("field", f), ("allowed", SearchRules.PartnerFields)));
        string Flat(IReadOnlyDictionary<string, string>? d) => string.Join(',', (d ?? new Dictionary<string, string>()).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
        string cursorKind = ReadRules.QueryKind("bps", Flat(req.Contains), Flat(req.Prefix), req.PartnerKind, req.IsCustomer, req.IsSupplier, req.Status, req.PurposeCode);
        if (!ReadRules.TryDecodeCursor(req.Cursor, cursorKind, out long offset)) return Reject<PartnerList>(Error("READ.CURSOR_INVALID", "P1", "cursor"));

        // the columns come from the allowlist; a prefix compares the normalised start of the column with the normalised value
        string prefixSql = string.Concat((req.Prefix ?? new Dictionary<string, string>()).Keys.Select((f, n) => $" and left(core.search_norm(i.{f}), length(core.search_norm(@x{n}))) = core.search_norm(@x{n})"));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var items = new List<PartnerRead>();
        await using (var cmd = tx.Command(
            "select " + Select + "where p.tenant_id = @t and i.partner_id is not null" + SearchFilters.Where(req.Contains, "i", "s") + prefixSql +
            " and (@kind::text is null or p.partner_kind = @kind) and (@cust::boolean is null or p.is_customer = @cust) and (@supp::boolean is null or p.is_supplier = @supp) " +
            "and (@status::text is null or p.status = @status) and (p.restriction_status <> 'RESTRICTED' or @statutory) order by p.partner_code offset @o limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("n", limit + 1);
            cmd.Parameters.AddWithValue("statutory", req.PurposeCode == PartnerReadRules.StatutoryPurpose);
            cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = (object?)req.PartnerKind ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("cust", NpgsqlDbType.Boolean) { Value = (object?)req.IsCustomer ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("supp", NpgsqlDbType.Boolean) { Value = (object?)req.IsSupplier ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)req.Status ?? DBNull.Value });
            SearchFilters.Bind(cmd, req.Contains, "s");
            int n = 0;
            foreach (string value in (req.Prefix ?? new Dictionary<string, string>()).Values) cmd.Parameters.AddWithValue($"x{n++}", value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(ReadPartner(r, true));
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor(cursorKind, offset + limit); }
        // the log (5.2): the field group SEARCH for every partner found, and the groups of the fields returned; a search that finds nothing is logged once, without a partner
        if (items.Count == 0) await ReadAccessLog.WriteAsync(tx, tenantId, ctx, Operation + ":search", ReadAccessLog.Search, null, null, null, req.PurposeCode, null, ct);
        for (int i = 0; i < items.Count; i++)
        {
            await ReadAccessLog.WriteAsync(tx, tenantId, ctx, Operation + ":search", ReadAccessLog.Search, null, items[i].PartnerId, null, req.PurposeCode, (int)offset + i + 1, ct);
            if (items[i].Pii is not null) await LogAsync(tx, tenantId, ctx, items[i], Operation + ":search", req.PurposeCode, (int)offset + i + 1, ct);
        }
        await tx.CommitAsync(ct);
        return CommandResult<PartnerList>.Ok(new PartnerList(items, new ReadPage(next, limit), false), Guid.Empty);
    }

    public async Task<CommandResult<PartnerList>> ListAsync(Guid tenantId, CommandContext ctx, ListPartnersRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<PartnerList>(denied);
        int limit = req.Limit ?? ReadRules.DefaultLimit;
        if (limit is < 1 or > ReadRules.MaxLimit) return Reject<PartnerList>(Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit)));
        if (!ReadRules.TryDecodeCursor(req.Cursor, "bp", out long offset)) return Reject<PartnerList>(Error("READ.CURSOR_INVALID", "P1", "cursor"));
        if (req.PartnerKind is not (null or "ORGANISATION" or "PERSON")) return Reject<PartnerList>(Error("READ.INVALID_FILTER", "P1", "partner_kind", ("field", "partner_kind")));
        if (req.Status is not (null or "ACTIVE" or "BLOCKED" or "ANONYMISED")) return Reject<PartnerList>(Error("READ.INVALID_FILTER", "P1", "status", ("field", "status")));
        if (!ReadAccessLog.ValidPurpose(req.PurposeCode)) return Reject<PartnerList>(Error("SCHEMA.INVALID_VALUE", "P1", "purpose_code", ("reason", "1 to 50 characters")));
        bool wantPii = req.IncludePii && ctx.Permissions.Contains(PartnerReadRules.PiiPermission);

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var ext = await ExtensionRead.FiltersAsync(tx, tenantId, ctx, "BUSINESS_PARTNER", req.Ext, "p.partner_id::text", true, Operation + ":list", ct);
        if (ext.Error is not null) return Reject<PartnerList>(ext.Error);
        var items = new List<PartnerRead>();
        await using (var cmd = tx.Command(
            "select " + Select + "where p.tenant_id = @t" + ext.Sql + " and (@prefix::text is null or p.partner_code like @prefix || '%') and (@kind::text is null or p.partner_kind = @kind) " +
            "and (@cust::boolean is null or p.is_customer = @cust) and (@supp::boolean is null or p.is_supplier = @supp) and (@status::text is null or p.status = @status) " +
            "order by p.partner_code offset @o limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("n", limit + 1); ext.Bind(cmd);
            cmd.Parameters.Add(new NpgsqlParameter("prefix", NpgsqlDbType.Text) { Value = (object?)req.CodePrefix ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = (object?)req.PartnerKind ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("cust", NpgsqlDbType.Boolean) { Value = (object?)req.IsCustomer ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("supp", NpgsqlDbType.Boolean) { Value = (object?)req.IsSupplier ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)req.Status ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(ReadPartner(r, wantPii));
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor("bp", offset + limit); }
        if (wantPii)
        {
            // a RESTRICTED partner in the page is returned without personal data: a list does not fail for one row, and a row that is left out is not logged
            for (int i = 0; i < items.Count; i++)
            {
                if (Restricted(items[i], req.PurposeCode) is not null) { items[i] = items[i] with { Pii = null }; continue; }
                if (items[i].Pii is not null) await LogAsync(tx, tenantId, ctx, items[i], Operation + ":list", req.PurposeCode, (int)offset + i + 1, ct);
            }
        }
        if (req.IncludeExtensions)
        {
            var values = await ExtensionRead.LoadAsync(tx, tenantId, ctx, "BUSINESS_PARTNER", items.Select(i => i.PartnerId.ToString("D")).ToList(), Operation + ":list", ct);
            items = items.Select(i => i with { Extensions = values[i.PartnerId.ToString("D")] }).ToList();
        }
        await tx.CommitAsync(ct);
        return CommandResult<PartnerList>.Ok(new PartnerList(items, new ReadPage(next, limit), req.IncludePii && !wantPii), Guid.Empty);
    }
}
