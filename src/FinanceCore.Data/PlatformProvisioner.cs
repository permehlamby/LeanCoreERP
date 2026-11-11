using System.Text.RegularExpressions;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Packages;
using FinanceCore.Core.Periods;
using FinanceCore.Core.Retention;
using FinanceCore.Core.Roles;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>A refusal of a platform operation: a stable code and a message for the operator.</summary>
public sealed class ProvisioningException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record TenantSpec(string Code, string Name, string Purpose, IReadOnlyList<string> Currencies);

public sealed record CompanySpec(
    string TenantCode, string Code, string LegalName, string Country, string LocalCurrency, string? ReportingCurrency, string GroupCurrency,
    int FirstMonth, DateOnly ValidFrom);

public sealed record TenantInfo(Guid TenantId, string Code, string Name, string Status, string Purpose, int Companies);

public sealed record CompanyInfo(Guid CompanyId, string Code, string LegalName, string Country, string Status, string? PackageCode);

/// <summary>
/// The platform operations of the operator (12, 13: tenant provisioning is not a tenant permission): create a tenant, create a company in it, apply a
/// country package and activate. They run with the migration role of the environment (FINANCECORE_ADMIN_DB) and are not exposed through the API
/// (ADR-213). What a company needs afterwards goes through the same command handlers as for any caller, with the actor "platform-cli".
/// </summary>
public sealed class PlatformProvisioner(NpgsqlDataSource source)
{
    public const string Actor = "platform-cli";
    private static readonly Regex CodePattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,49}$", RegexOptions.Compiled);

    private static CommandContext Context(string key, params string[] permissions) =>
        new(key, "platform-" + key, Actor, new HashSet<string>(permissions), "PLATFORM");

    // ---------------------------------------------------------------------------------------------------------
    // Tenants
    // ---------------------------------------------------------------------------------------------------------
    public async Task<TenantInfo> CreateTenantAsync(TenantSpec spec, CancellationToken ct = default)
    {
        if (!CodePattern.IsMatch(spec.Code)) throw new ProvisioningException("SCHEMA.INVALID_VALUE", "The tenant code is 1 to 50 letters, digits, '_' or '-'");
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 100) throw new ProvisioningException("SCHEMA.INVALID_VALUE", "The name is 1 to 100 characters");
        if (spec.Purpose is not ("PRODUCTION" or "REHEARSAL" or "TEST")) throw new ProvisioningException("SCHEMA.INVALID_VALUE", "The purpose is PRODUCTION, REHEARSAL or TEST");
        if (spec.Currencies.Count == 0) throw new ProvisioningException("SCHEMA.INVALID_VALUE", "At least one currency is needed");

        Guid id = Guid.NewGuid();
        await using NpgsqlConnection connection = await source.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, id, ct);
        var scope = new Scope(connection, tx, id);
        if (await ExistsAsync(scope, "select 1 from platform.tenant where tenant_code = @c", ("c", spec.Code), ct))
            throw new ProvisioningException("TENANT.CODE_EXISTS", $"A tenant with code {spec.Code} exists");
        foreach (string currency in spec.Currencies)
            if (!await ExistsAsync(scope, "select 1 from platform.ref_currency where currency_code = @c", ("c", currency), ct))
                throw new ProvisioningException("CURRENCY.UNKNOWN", $"Unknown currency {currency}");

        await Run(scope, "insert into platform.tenant (tenant_id, tenant_code, name, status, purpose) values (@t, @c, @n, 'ACTIVE', @p)",
            ct, ("t", id), ("c", spec.Code), ("n", spec.Name), ("p", spec.Purpose));
        await Run(scope, "insert into core.tenant_setting (tenant_id) values (@t)", ct, ("t", id));
        await Run(scope, "insert into core.outbox_head (tenant_id) values (@t)", ct, ("t", id));
        foreach (string currency in spec.Currencies.Distinct())
            await Run(scope, "insert into core.tenant_currency (tenant_id, currency_code) values (@t, @c)", ct, ("t", id), ("c", currency));
        await tx.CommitAsync(ct);
        return new TenantInfo(id, spec.Code, spec.Name, "ACTIVE", spec.Purpose, 0);
    }

    public async Task<IReadOnlyList<TenantInfo>> ListTenantsAsync(CancellationToken ct = default)
    {
        var list = new List<TenantInfo>();
        await using NpgsqlConnection connection = await source.OpenConnectionAsync(ct);
        var ids = new List<(Guid, string, string, string, string)>();
        await using (var cmd = new NpgsqlCommand("select tenant_id, tenant_code, name, status, purpose from platform.tenant order by tenant_code", connection))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) ids.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        foreach (var (id, code, name, status, purpose) in ids)
        {
            await using var set = new NpgsqlCommand("select set_config('app.tenant_id', @t, false)", connection);
            set.Parameters.AddWithValue("t", id.ToString("D"));
            await set.ExecuteNonQueryAsync(ct);
            await using var count = new NpgsqlCommand("select count(*) from core.company where tenant_id = @t", connection);
            count.Parameters.AddWithValue("t", id);
            list.Add(new TenantInfo(id, code, name, status, purpose, (int)(long)(await count.ExecuteScalarAsync(ct))!));
        }
        return list;
    }

    public async Task<Guid> RequireTenantAsync(string code, CancellationToken ct = default)
    {
        await using NpgsqlConnection connection = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("select tenant_id from platform.tenant where tenant_code = @c", connection);
        cmd.Parameters.AddWithValue("c", code);
        return await cmd.ExecuteScalarAsync(ct) is Guid id ? id : throw new ProvisioningException("TENANT.NOT_FOUND", $"No tenant with code {code}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Companies
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CompanyInfo> CreateCompanyAsync(CompanySpec spec, CancellationToken ct = default)
    {
        if (!CodePattern.IsMatch(spec.Code)) throw new ProvisioningException("SCHEMA.INVALID_VALUE", "The company code is 1 to 50 letters, digits, '_' or '-'");
        if (string.IsNullOrWhiteSpace(spec.LegalName) || spec.LegalName.Length > 200) throw new ProvisioningException("SCHEMA.INVALID_VALUE", "The legal name is 1 to 200 characters");
        if (spec.FirstMonth is < 1 or > 12) throw new ProvisioningException("SCHEMA.INVALID_VALUE", "The first month of the fiscal year is 1 to 12");
        Guid tenantId = await RequireTenantAsync(spec.TenantCode, ct);

        await using NpgsqlConnection connection = await source.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        var scope = new Scope(connection, tx, tenantId);
        if (await ExistsAsync(scope, "select 1 from core.company where tenant_id = @t and company_code = @c", ("c", spec.Code), ct))
            throw new ProvisioningException("COMPANY.CODE_EXISTS", $"A company with code {spec.Code} exists in the tenant");
        if (!await ExistsAsync(scope, "select 1 from platform.ref_country where country_code = @c", ("c", spec.Country), ct))
            throw new ProvisioningException("CONFIG.INVALID_REFERENCE", $"Unknown country {spec.Country}");
        foreach (string currency in new[] { spec.LocalCurrency, spec.ReportingCurrency, spec.GroupCurrency }.Where(c => c is not null).Distinct()!)
            if (!await ExistsAsync(scope, "select 1 from core.tenant_currency where tenant_id = @t and currency_code = @c and active", ("c", currency!), ct))
                throw new ProvisioningException("CURRENCY.NOT_ACTIVE", $"Currency {currency} is not enabled for the tenant (create the tenant with it)");

        // one group per tenant in this slice, one calendar per first month
        Guid groupId = await FindOrCreateAsync(scope, ct,
            "select group_id from core.company_group where tenant_id = @t and group_code = 'GRP'",
            "insert into core.company_group (tenant_id, group_id, group_code, name, group_currency) values (@t, @id, 'GRP', 'Group', @cur) returning group_id",
            ("cur", spec.GroupCurrency));
        string groupCurrency = (string)(await ScalarAsync(scope, "select group_currency from core.company_group where tenant_id = @t and group_id = @g", ct, ("g", groupId)))!;
        if (groupCurrency != spec.GroupCurrency)
            throw new ProvisioningException("CONFIG.GROUP_CURRENCY_MISMATCH", $"The group of the tenant has currency {groupCurrency}, not {spec.GroupCurrency}");
        string calendarCode = "CAL-M" + spec.FirstMonth;
        Guid calendarId = await FindOrCreateAsync(scope, ct,
            "select calendar_id from core.fiscal_calendar where tenant_id = @t and calendar_code = @cc",
            "insert into core.fiscal_calendar (tenant_id, calendar_id, calendar_code, first_month, special_period_count, year_label_rule) " +
            "values (@t, @id, @cc, @fm, 0, 'START') returning calendar_id", ("cc", calendarCode), ("fm", (short)spec.FirstMonth));

        Guid companyId = Guid.NewGuid(), ledgerId = Guid.NewGuid();
        await Run(scope,
            "insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, reporting_currency, fiscal_calendar_id, status) " +
            "values (@t, @c, @code, @n, @cty, @lc, @rc, @cal, 'DRAFT')", ct,
            ("c", companyId), ("code", spec.Code), ("n", spec.LegalName), ("cty", spec.Country), ("lc", spec.LocalCurrency),
            ("rc", (object?)spec.ReportingCurrency ?? DBNull.Value), ("cal", calendarId));
        await Run(scope, "insert into core.ledger (tenant_id, company_id, ledger_id, ledger_code) values (@t, @c, @l, 'L1')", ct, ("c", companyId), ("l", ledgerId));
        await Run(scope, "insert into core.group_membership (tenant_id, company_id, valid_from, group_id) values (@t, @c, @from, @g)", ct,
            ("c", companyId), ("from", spec.ValidFrom), ("g", groupId));
        await Run(scope, "insert into core.company_setting_version (tenant_id, company_id, effective_from) values (@t, @c, @from)", ct,
            ("c", companyId), ("from", new DateTime(spec.ValidFrom.Year, spec.ValidFrom.Month, spec.ValidFrom.Day, 0, 0, 0, DateTimeKind.Utc)));
        await tx.CommitAsync(ct);
        return new CompanyInfo(companyId, spec.Code, spec.LegalName, spec.Country, "DRAFT", null);
    }

    public async Task<IReadOnlyList<CompanyInfo>> ListCompaniesAsync(Guid tenantId, CancellationToken ct = default)
    {
        var list = new List<CompanyInfo>();
        await using NpgsqlConnection connection = await source.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        await using var cmd = new NpgsqlCommand(
            "select company_id, company_code, legal_name, country_code, status, package_code from core.company where tenant_id = @t order by company_code", connection, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CompanyInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public async Task<CompanyInfo> RequireCompanyAsync(Guid tenantId, string code, CancellationToken ct = default) =>
        (await ListCompaniesAsync(tenantId, ct)).FirstOrDefault(c => c.Code == code)
        ?? throw new ProvisioningException("COMPANY.NOT_FOUND", $"No company with code {code} in the tenant");

    /// <summary>
    /// Adds an exchange rate (07, rates are configuration, ADR-020: the core never fetches them). There is no rate command in the contracts yet, so the
    /// operator adds them here (F-IMPL-30). A date that already has a rate for the pair is refused: a rate that has been used cannot change.
    /// </summary>
    public async Task AddExchangeRateAsync(Guid tenantId, string rateType, string from, string to, DateOnly validFrom, decimal rate, CancellationToken ct = default)
    {
        if (rateType is not ("SPOT" or "CLOSING")) throw new ProvisioningException("SCHEMA.INVALID_VALUE", "The rate type is SPOT or CLOSING");
        if (rate <= 0) throw new ProvisioningException("SCHEMA.INVALID_VALUE", "The rate is greater than zero");
        if (from == to) throw new ProvisioningException("SCHEMA.INVALID_VALUE", "The two currencies differ");
        await using NpgsqlConnection connection = await source.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        var scope = new Scope(connection, tx, tenantId);
        foreach (string currency in new[] { from, to })
            if (!await ExistsAsync(scope, "select 1 from core.tenant_currency where tenant_id = @t and currency_code = @c and active", ("c", currency), ct))
                throw new ProvisioningException("CURRENCY.NOT_ACTIVE", $"Currency {currency} is not enabled for the tenant");
        if (await ScalarAsync(scope, "select 1 from core.exchange_rate where tenant_id = @t and rate_type = @r and from_currency = @f and to_currency = @o and valid_from = @d", ct, ("r", rateType), ("f", from), ("o", to), ("d", validFrom)) is not null)
            throw new ProvisioningException("RATE.EXISTS", "A rate exists for that pair and date");
        await Run(scope, "insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) values (@t, @r, @f, @o, @d, @v)", ct,
            ("r", rateType), ("f", from), ("o", to), ("d", validFrom), ("v", rate));
        await tx.CommitAsync(ct);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Package and activation
    // ---------------------------------------------------------------------------------------------------------
    /// <summary>
    /// Applies a country package to a company in DRAFT, once (create-only, 07, 11.4): the accounts, the number series and their assignments, the roles
    /// that activation needs and the retention rules, then opens the fiscal year. The roles, rules and the year go through the command handlers.
    /// </summary>
    public async Task ApplyPackageAsync(Guid tenantId, CompanyInfo company, CountryPackage package, int fiscalYear, CancellationToken ct = default)
    {
        if (company.Status != "DRAFT") throw new ProvisioningException("COMPANY.NOT_DRAFT", "A package is applied to a company in DRAFT");
        if (company.PackageCode is not null) throw new ProvisioningException("PACKAGE.ALREADY_APPLIED", $"The package {company.PackageCode} is already applied to the company");
        if (company.Country != package.Country)
            throw new ProvisioningException("PACKAGE.COUNTRY_MISMATCH", $"The package {package.Code} is for {package.Country}, the company is in {company.Country}");

        var accountIds = new Dictionary<string, Guid>();
        await using (NpgsqlConnection connection = await source.OpenConnectionAsync(ct))
        await using (NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct))
        {
            await SetTenantAsync(connection, tx, tenantId, ct);
            var scope = new Scope(connection, tx, tenantId);
            foreach (PackageAccount a in package.Accounts)
            {
                Guid id = Guid.NewGuid();
                accountIds[a.AccountNo] = id;
                await Run(scope,
                    "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, open_item_mode, tax_code_rule) " +
                    "values (@t, @c, @id, @no, @n, @ty, @oi, 'FORBIDDEN')", ct,
                    ("c", company.CompanyId), ("id", id), ("no", a.AccountNo), ("n", a.Name), ("ty", a.AccountType), ("oi", a.OpenItemMode));
            }
            foreach (PackageSeries s in package.Series)
            {
                Guid id = Guid.NewGuid();
                await Run(scope,
                    "insert into core.number_series (tenant_id, company_id, series_id, series_code, description, prefix, include_fiscal_year, separator, number_length, is_legal) " +
                    "values (@t, @c, @id, @code, @d, @p, true, '-', 6, true)", ct,
                    ("c", company.CompanyId), ("id", id), ("code", s.Code), ("d", s.Description), ("p", s.Prefix));
                foreach (string type in s.DocumentTypes)
                    await Run(scope,
                        "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) " +
                        "values (@t, @c, @ty, @id, @from, @by)", ct,
                        ("c", company.CompanyId), ("ty", type), ("id", id), ("from", new DateOnly(fiscalYear, 1, 1)), ("by", Actor));
            }
            await tx.CommitAsync(ct);
        }

        string key = $"platform-package-{company.CompanyId:N}-{package.Code}";
        var roles = new AccountRoleAssigner(source);
        foreach (var (role, accountNo) in package.Roles)
        {
            var r = await roles.AssignAsync(tenantId, Context($"{key}-role-{role}", "write.config"), new AssignAccountRoleRequest(company.CompanyId, role, accountIds[accountNo]), ct);
            if (r.Status != CommandStatus.Ok) throw Failed($"role {role}", r.Errors);
        }
        var rules = new RetentionRuleCommands(source);
        foreach (PackageRetention p in package.Retention)
        {
            var r = await rules.CreateVersionAsync(tenantId, Context($"{key}-retention-{p.RecordClass}", RetentionRules.Permission),
                new CreateRetentionRuleVersionRequest(company.CompanyId, p.RecordClass, package.Country, p.Years, p.StartEvent, p.LegalBasis, null), ct);
            if (r.Status != CommandStatus.Ok) throw Failed($"retention rule {p.RecordClass}", r.Errors);
        }
        var year = await new FiscalYearOpener(source).OpenAsync(tenantId, Context($"{key}-year-{fiscalYear}", FiscalYearOpener.Permission),
            new OpenFiscalYearRequest(company.CompanyId, fiscalYear), ct);
        if (year.Status != CommandStatus.Ok) throw Failed($"fiscal year {fiscalYear}", year.Errors);

        await using NpgsqlConnection mark = await source.OpenConnectionAsync(ct);
        await using NpgsqlTransaction markTx = await mark.BeginTransactionAsync(ct);
        await SetTenantAsync(mark, markTx, tenantId, ct);
        var markScope = new Scope(mark, markTx, tenantId);
        await Run(markScope, "update core.company set package_code = @p, package_version = @v where tenant_id = @t and company_id = @c", ct,
            ("c", company.CompanyId), ("p", package.Code), ("v", package.Version));
        await markTx.CommitAsync(ct);
    }

    /// <summary>The readiness report of 07, 11.3 and, if the company is ready, its activation.</summary>
    public async Task<(bool Activated, IReadOnlyList<string> Failed)> ActivateAsync(Guid tenantId, CompanyInfo company, CancellationToken ct = default)
    {
        var activator = new CompanyActivator(source);
        var result = await activator.ActivateAsync(tenantId, Context($"platform-activate-{company.CompanyId:N}", CompanyActivator.ActivatePermission), company.CompanyId, ct);
        if (result.Status == CommandStatus.Ok) return (true, []);
        return (false, result.Errors.Select(e => e.Params is { } p && p.TryGetValue("check", out var check) ? $"{check}: {p.GetValueOrDefault("detail")}" : e.Code).ToList());
    }

    private static ProvisioningException Failed(string step, IReadOnlyList<CommandError> errors) =>
        new("PACKAGE.STEP_FAILED", $"{step}: " + string.Join(", ", errors.Select(e => e.Code + (e.Path.Length > 0 ? " " + e.Path : ""))));

    // ---------------------------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------------------------
    private static async Task SetTenantAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select set_config('app.tenant_id', @t, true)", c, tx);
        cmd.Parameters.AddWithValue("t", tenantId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record Scope(NpgsqlConnection C, NpgsqlTransaction Tx, Guid Tenant);

    private static NpgsqlCommand Build(Scope s, string sql, (string, object)[] p)
    {
        var cmd = new NpgsqlCommand(sql, s.C, s.Tx);
        cmd.Parameters.AddWithValue("t", s.Tenant);
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    private static async Task Run(Scope s, string sql, CancellationToken ct, params (string, object)[] p)
    {
        await using var cmd = Build(s, sql, p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(Scope s, string sql, CancellationToken ct, params (string, object)[] p)
    {
        await using var cmd = Build(s, sql, p);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<bool> ExistsAsync(Scope s, string sql, (string, object) p, CancellationToken ct) =>
        await ScalarAsync(s, sql, ct, p) is not null;

    /// <summary>Takes the id of the row that the find statement names, or inserts one with a new id (@id) and takes the id it returns.</summary>
    private static async Task<Guid> FindOrCreateAsync(Scope s, CancellationToken ct, string find, string insert, params (string, object)[] p)
    {
        if (await ScalarAsync(s, find, ct, p.Where(x => find.Contains("@" + x.Item1)).ToArray()) is Guid existing) return existing;
        return (Guid)(await ScalarAsync(s, insert, ct, p.Append(("id", (object)Guid.NewGuid())).ToArray()))!;
    }
}
