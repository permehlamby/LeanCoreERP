using FinanceCore.Core.Commands;
using FinanceCore.Core.Packages;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>The platform operations of the CLI (C1, C2): tenant, company, country package, activation, and that a company made this way posts.</summary>
[Collection("db")]
public class PlatformProvisionerTests
{
    private static PlatformProvisioner Provisioner() => new(TestTenant.AdminSource);

    private static string NewCode() => "T" + Guid.NewGuid().ToString("N")[..8];   // a TEST tenant the cleanup script removes

    private static async Task<(Guid TenantId, CompanyInfo Company)> SetupAsync(bool activate = true)
    {
        var p = Provisioner();
        var tenant = await p.CreateTenantAsync(new(NewCode(), "Platform test", "TEST", ["DKK", "EUR"]));
        var company = await p.CreateCompanyAsync(new(tenant.Code, "DK01", "Test ApS", "DK", "DKK", "EUR", "EUR", 1, new DateOnly(2026, 1, 1)));
        await p.ApplyPackageAsync(tenant.TenantId, company, CountryPackages.DkMinimal, 2026);
        if (activate)
        {
            var (ok, failed) = await p.ActivateAsync(tenant.TenantId, company with { PackageCode = "dk-minimal" });
            Assert.True(ok, string.Join("; ", failed));
        }
        return (tenant.TenantId, company with { PackageCode = "dk-minimal" });
    }

    private static async Task<T> Scalar<T>(Guid tenantId, string sql, params (string, object)[] p)
    {
        await using NpgsqlConnection c = await TestTenant.AdminSource.OpenConnectionAsync();
        await using var set = new NpgsqlCommand("select set_config('app.tenant_id', @t, false)", c);
        set.Parameters.AddWithValue("t", tenantId.ToString("D"));
        await set.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("t", tenantId);
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    [Fact]
    public async Task ATenantAndACompanyAreCreatedAndListed()
    {
        var p = Provisioner();
        var tenant = await p.CreateTenantAsync(new(NewCode(), "Platform test", "TEST", ["DKK", "EUR", "DKK"]));
        Assert.Equal("ACTIVE", tenant.Status);
        Assert.Equal(2L, await Scalar<long>(tenant.TenantId, "select count(*) from core.tenant_currency where tenant_id = @t"));
        Assert.Contains((await p.ListTenantsAsync()), x => x.TenantId == tenant.TenantId && x.Companies == 0);

        var company = await p.CreateCompanyAsync(new(tenant.Code, "DK01", "Test ApS", "DK", "DKK", "EUR", "EUR", 1, new DateOnly(2026, 1, 1)));
        Assert.Equal("DRAFT", company.Status);
        Assert.Equal(1, (await p.ListTenantsAsync()).Single(x => x.TenantId == tenant.TenantId).Companies);
        Assert.Equal(company.CompanyId, (await p.RequireCompanyAsync(tenant.TenantId, "DK01")).CompanyId);
        Assert.Equal(1L, await Scalar<long>(tenant.TenantId, "select count(*) from core.ledger where tenant_id = @t and is_default"));
        Assert.Equal(1L, await Scalar<long>(tenant.TenantId, "select count(*) from core.group_membership where tenant_id = @t"));

        // a second company shares the group and the calendar
        await p.CreateCompanyAsync(new(tenant.Code, "DK02", "Test 2 ApS", "DK", "DKK", "EUR", "EUR", 1, new DateOnly(2026, 1, 1)));
        Assert.Equal(1L, await Scalar<long>(tenant.TenantId, "select count(*) from core.company_group where tenant_id = @t"));
        Assert.Equal(1L, await Scalar<long>(tenant.TenantId, "select count(*) from core.fiscal_calendar where tenant_id = @t"));
    }

    [Fact]
    public async Task BadInputIsRefusedWithAStableCode()
    {
        var p = Provisioner();
        async Task<string> Code(Func<Task> action) => (await Assert.ThrowsAsync<ProvisioningException>(action)).Code;

        Assert.Equal("SCHEMA.INVALID_VALUE", await Code(() => p.CreateTenantAsync(new("bad code!", "x", "TEST", ["DKK"]))));
        Assert.Equal("SCHEMA.INVALID_VALUE", await Code(() => p.CreateTenantAsync(new(NewCode(), "x", "LIVE", ["DKK"]))));
        Assert.Equal("SCHEMA.INVALID_VALUE", await Code(() => p.CreateTenantAsync(new(NewCode(), "x", "TEST", []))));
        Assert.Equal("CURRENCY.UNKNOWN", await Code(() => p.CreateTenantAsync(new(NewCode(), "x", "TEST", ["ZZZ"]))));

        var tenant = await p.CreateTenantAsync(new(NewCode(), "x", "TEST", ["DKK", "EUR"]));
        Assert.Equal("TENANT.CODE_EXISTS", await Code(() => p.CreateTenantAsync(new(tenant.Code, "again", "TEST", ["DKK"]))));
        Assert.Equal("TENANT.NOT_FOUND", await Code(() => p.RequireTenantAsync("NOPE")));

        CompanySpec Spec(string code = "C1", string country = "DK", string local = "DKK", string group = "EUR", int month = 1) =>
            new(tenant.Code, code, "Test ApS", country, local, "EUR", group, month, new DateOnly(2026, 1, 1));
        Assert.Equal("CONFIG.INVALID_REFERENCE", await Code(() => p.CreateCompanyAsync(Spec(country: "ZZ"))));
        Assert.Equal("CURRENCY.NOT_ACTIVE", await Code(() => p.CreateCompanyAsync(Spec(local: "SEK"))));
        Assert.Equal("SCHEMA.INVALID_VALUE", await Code(() => p.CreateCompanyAsync(Spec(month: 13))));
        await p.CreateCompanyAsync(Spec());
        Assert.Equal("COMPANY.CODE_EXISTS", await Code(() => p.CreateCompanyAsync(Spec())));
        Assert.Equal("CONFIG.GROUP_CURRENCY_MISMATCH", await Code(() => p.CreateCompanyAsync(Spec("C2", group: "DKK"))));
        Assert.Equal("COMPANY.NOT_FOUND", await Code(() => p.RequireCompanyAsync(tenant.TenantId, "NOPE")));
    }

    [Fact]
    public async Task ThePackageGivesAnActivatableCompanyThatPostsAndVerifies()
    {
        var (tenantId, company) = await SetupAsync();
        Assert.Equal("ACTIVE", await Scalar<string>(tenantId, "select status from core.company where tenant_id = @t"));
        Assert.Equal("dk-minimal", await Scalar<string>(tenantId, "select package_code from core.company where tenant_id = @t"));
        Assert.Equal(11L, await Scalar<long>(tenantId, "select count(*) from core.gl_account where tenant_id = @t"));
        Assert.Equal(7L, await Scalar<long>(tenantId, "select count(*) from core.number_series_assignment where tenant_id = @t"));

        Guid Account(string no) => Scalar<Guid>(tenantId, "select account_id from core.gl_account where tenant_id = @t and account_no = @n", ("n", no)).Result;
        var source = TestTenant.Source;
        var ctx = (string key) => new CommandContext(key, "c-" + key, "tester", new HashSet<string> { "journal.post" });
        Guid ledger = await Scalar<Guid>(tenantId, "select ledger_id from core.ledger where tenant_id = @t");
        await Provisioner().AddExchangeRateAsync(tenantId, "SPOT", "DKK", "EUR", new DateOnly(2026, 3, 15), 0.134m);

        var poster = new JournalDocumentPoster(source);
        var journal = await poster.PostAsync(tenantId, ctx("j"), new(company.CompanyId, null, "JOURNAL", new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 15), "DKK",
            "Opening", null, null, [new RequestLine("1", Account("1000"), 1000m), new RequestLine("2", Account("3000"), -1000m)]));
        Assert.True(journal.Status == CommandStatus.Ok, string.Join(", ", journal.Errors.Select(e => e.Code + " " + e.Path)));
        Assert.StartsWith("GL-2026-", journal.Value!.DocumentNumber);
        Assert.Equal(1L, await Scalar<long>(tenantId, "select count(*) from core.document_legal_snapshot where tenant_id = @t"));
        Assert.True((await new ChainVerifier(source).VerifyLedgerAsync(tenantId, company.CompanyId, ledger)).Ok);
    }

    [Fact]
    public async Task APackageIsAppliedOnceToAPlainDraftCompanyOfItsCountry()
    {
        var p = Provisioner();
        var tenant = await p.CreateTenantAsync(new(NewCode(), "x", "TEST", ["DKK", "EUR", "SEK"]));
        var se = await p.CreateCompanyAsync(new(tenant.Code, "SE01", "Test AB", "SE", "SEK", "EUR", "EUR", 1, new DateOnly(2026, 1, 1)));
        Assert.Equal("PACKAGE.COUNTRY_MISMATCH",
            (await Assert.ThrowsAsync<ProvisioningException>(() => p.ApplyPackageAsync(tenant.TenantId, se, CountryPackages.DkMinimal, 2026))).Code);

        var (tenantId, company) = await SetupAsync(activate: false);
        Assert.Equal("PACKAGE.ALREADY_APPLIED",
            (await Assert.ThrowsAsync<ProvisioningException>(() => p.ApplyPackageAsync(tenantId, company, CountryPackages.DkMinimal, 2026))).Code);
        var activated = await p.ActivateAsync(tenantId, company);
        Assert.True(activated.Activated);
        Assert.Equal("COMPANY.NOT_DRAFT",
            (await Assert.ThrowsAsync<ProvisioningException>(() => p.ApplyPackageAsync(tenantId, company with { Status = "ACTIVE", PackageCode = null }, CountryPackages.DkMinimal, 2026))).Code);
    }

    [Fact]
    public async Task ACompanyThatIsNotReadyIsNotActivatedAndTheFailedChecksAreNamed()
    {
        var p = Provisioner();
        var tenant = await p.CreateTenantAsync(new(NewCode(), "x", "TEST", ["DKK", "EUR"]));
        var company = await p.CreateCompanyAsync(new(tenant.Code, "DK01", "Test ApS", "DK", "DKK", "EUR", "EUR", 1, new DateOnly(2026, 1, 1)));
        var (ok, failed) = await p.ActivateAsync(tenant.TenantId, company);
        Assert.False(ok);
        Assert.Contains(failed, f => f.StartsWith("ROLES"));
        Assert.Contains(failed, f => f.StartsWith("RETENTION_RULES"));
        Assert.Contains(failed, f => f.StartsWith("FISCAL_YEAR_OPEN"));
        Assert.Equal("DRAFT", await Scalar<string>(tenant.TenantId, "select status from core.company where tenant_id = @t"));
    }
}
