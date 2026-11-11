using FinanceCore.Platform.Cli;

namespace FinanceCore.Data.Tests;

/// <summary>The platform CLI end to end (C1, C2): the commands, their output and exit codes against the database.</summary>
[Collection("db")]
public class PlatformCliTests
{
    private static async Task<(int Code, string Output)> Run(params string[] args)
    {
        var output = new StringWriter();
        int code = await new PlatformCli(new PlatformProvisioner(TestTenant.AdminSource), output).RunAsync(args);
        return (code, output.ToString());
    }

    [Fact]
    public async Task ATenantACompanyAndAPackageGiveAnActiveCompany()
    {
        string tenant = "T" + Guid.NewGuid().ToString("N")[..8];
        var created = await Run("tenant", "create", "--code", tenant, "--name", "Cli test", "--purpose", "TEST", "--currencies", "DKK,EUR");
        Assert.Equal(0, created.Code);
        Assert.Contains($"tenant {tenant} created", created.Output);

        var list = await Run("tenant", "list");
        Assert.Contains(tenant, list.Output);

        var company = await Run("company", "create", "--tenant", tenant, "--code", "DK01", "--name", "Cli ApS", "--country", "dk", "--currency", "dkk",
            "--reporting", "eur", "--valid-from", "2026-01-01");
        Assert.Equal(0, company.Code);
        Assert.Contains("in DRAFT", company.Output);

        var setup = await Run("company", "setup", "--tenant", tenant, "--company", "DK01", "--package", "dk-minimal", "--year", "2026");
        Assert.Equal(0, setup.Code);
        Assert.Contains("11 accounts, 5 number series, 6 roles, 2 retention rules, fiscal year 2026 open", setup.Output);
        Assert.Contains("rate add", setup.Output);

        var again = await Run("company", "setup", "--tenant", tenant, "--company", "DK01", "--package", "dk-minimal", "--year", "2026");
        Assert.Equal(1, again.Code);
        Assert.Contains("refused: PACKAGE.ALREADY_APPLIED", again.Output);

        var activate = await Run("company", "activate", "--tenant", tenant, "--company", "DK01");
        Assert.Equal(0, activate.Code);
        Assert.Contains("is ACTIVE", activate.Output);

        var rate = await Run("rate", "add", "--tenant", tenant, "--from", "dkk", "--to", "eur", "--date", "2026-03-15", "--rate", "0.134");
        Assert.Equal(0, rate.Code);
        var duplicate = await Run("rate", "add", "--tenant", tenant, "--from", "DKK", "--to", "EUR", "--date", "2026-03-15", "--rate", "0.135");
        Assert.Equal(1, duplicate.Code);
        Assert.Contains("RATE.EXISTS", duplicate.Output);

        var companies = await Run("company", "list", "--tenant", tenant);
        Assert.Contains("ACTIVE", companies.Output);
        Assert.Contains("dk-minimal", companies.Output);
    }

    [Fact]
    public async Task SetupWithTheActivateFlagActivatesInOneStep()
    {
        string tenant = "T" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(0, (await Run("tenant", "create", "--code", tenant, "--name", "x", "--purpose", "TEST")).Code);
        Assert.Equal(0, (await Run("company", "create", "--tenant", tenant, "--code", "C1", "--name", "x ApS", "--country", "DK", "--currency", "DKK", "--reporting", "EUR")).Code);
        var setup = await Run("company", "setup", "--tenant", tenant, "--company", "C1", "--package", "dk-minimal", "--year", "2026", "--activate");
        Assert.Equal(0, setup.Code);
        Assert.Contains("is ACTIVE", setup.Output);
    }

    [Fact]
    public async Task ACompanyThatIsNotReadyReportsTheFailedChecksAndExitsWithOne()
    {
        string tenant = "T" + Guid.NewGuid().ToString("N")[..8];
        await Run("tenant", "create", "--code", tenant, "--name", "x", "--purpose", "TEST");
        await Run("company", "create", "--tenant", tenant, "--code", "C1", "--name", "x ApS", "--country", "DK", "--currency", "DKK", "--reporting", "EUR");
        var activate = await Run("company", "activate", "--tenant", tenant, "--company", "C1");
        Assert.Equal(1, activate.Code);
        Assert.Contains("is not ready", activate.Output);
        Assert.Contains("ROLES", activate.Output);
        Assert.Contains("RETENTION_RULES", activate.Output);
    }

    [Fact]
    public async Task UsageErrorsExitWithTwoAndRefusalsWithOneAndTheirCode()
    {
        Assert.Equal(2, (await Run()).Code);
        Assert.Equal(2, (await Run("tenant")).Code);
        var missing = await Run("tenant", "create", "--name", "x");
        Assert.Equal(2, missing.Code);
        Assert.Contains("--code is required", missing.Output);
        var badDate = await Run("rate", "add", "--tenant", "x", "--from", "DKK", "--to", "EUR", "--date", "15-03-2026", "--rate", "1");
        Assert.Equal(2, badDate.Code);

        var noTenant = await Run("company", "list", "--tenant", "NOPE");
        Assert.Equal(1, noTenant.Code);
        Assert.Contains("refused: TENANT.NOT_FOUND", noTenant.Output);
        var noPackage = await Run("company", "setup", "--tenant", "NOPE", "--company", "C1", "--package", "se-minimal");
        Assert.Contains("PACKAGE.NOT_FOUND", noPackage.Output);

        var packages = await Run("package", "list");
        Assert.Equal(0, packages.Code);
        Assert.Contains("dk-minimal", packages.Output);
    }
}
