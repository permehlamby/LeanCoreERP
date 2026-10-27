using System.Globalization;
using FinanceCore.Core.Packages;
using FinanceCore.Data;

namespace FinanceCore.Platform.Cli;

/// <summary>
/// The platform CLI (C1, C2): the operator's way to create a tenant and a company, apply a country package, add exchange rates and activate. It
/// works on the database with the migration role (FINANCECORE_ADMIN_DB) and is not part of the API (ADR-213, 12, 13). Exit code 0 is done, 1 is a
/// refusal with a stable code, 2 is a usage error.
/// </summary>
public sealed class PlatformCli(PlatformProvisioner provisioner, TextWriter output)
{
    public const string Usage = """
        financecore-platform <command> [options]

          tenant create   --code <code> --name <name> [--purpose PRODUCTION|REHEARSAL|TEST] [--currencies DKK,EUR]
          tenant list
          company create  --tenant <code> --code <code> --name <legal name> --country <DK> --currency <DKK>
                          [--reporting <EUR>] [--group-currency <EUR>] [--first-month <1-12>] [--valid-from <yyyy-MM-dd>]
          company list    --tenant <code>
          company setup   --tenant <code> --company <code> --package <dk-minimal> [--year <yyyy>] [--activate]
          company activate --tenant <code> --company <code>
          rate add        --tenant <code> --from <DKK> --to <EUR> --date <yyyy-MM-dd> --rate <0.134> [--type SPOT|CLOSING]
          package list

        The database comes from FINANCECORE_ADMIN_DB (a postgresql:// URI or an Npgsql connection string).
        """;

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        try
        {
            var (words, options, flags) = Parse(args);
            string command = string.Join(' ', words.Take(2));
            switch (command)
            {
                case "tenant create": return await TenantCreate(options, ct);
                case "tenant list": return await TenantList(ct);
                case "company create": return await CompanyCreate(options, ct);
                case "company list": return await CompanyList(options, ct);
                case "company setup": return await CompanySetup(options, flags, ct);
                case "company activate": return await CompanyActivate(options, ct);
                case "rate add": return await RateAdd(options, ct);
                case "package list": return PackageList();
                default:
                    output.WriteLine(Usage);
                    return 2;
            }
        }
        catch (UsageException e)
        {
            output.WriteLine("usage: " + e.Message);
            output.WriteLine();
            output.WriteLine(Usage);
            return 2;
        }
        catch (ProvisioningException e)
        {
            output.WriteLine($"refused: {e.Code}: {e.Message}");
            return 1;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    private async Task<int> TenantCreate(Dictionary<string, string> o, CancellationToken ct)
    {
        var currencies = Opt(o, "currencies", "DKK,EUR").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tenant = await provisioner.CreateTenantAsync(new(Req(o, "code"), Req(o, "name"), Opt(o, "purpose", "PRODUCTION").ToUpperInvariant(), currencies), ct);
        output.WriteLine($"tenant {tenant.Code} created ({tenant.TenantId}), purpose {tenant.Purpose}, currencies {string.Join(", ", currencies)}");
        return 0;
    }

    private async Task<int> TenantList(CancellationToken ct)
    {
        var tenants = await provisioner.ListTenantsAsync(ct);
        if (tenants.Count == 0) output.WriteLine("no tenants");
        foreach (var t in tenants) output.WriteLine($"{t.Code,-20} {t.Status,-10} {t.Purpose,-10} companies {t.Companies,-3} {t.Name} ({t.TenantId})");
        return 0;
    }

    private async Task<int> CompanyCreate(Dictionary<string, string> o, CancellationToken ct)
    {
        string local = Req(o, "currency").ToUpperInvariant();
        string? reporting = o.TryGetValue("reporting", out var r) ? r.ToUpperInvariant() : null;
        string group = Opt(o, "group-currency", reporting ?? local).ToUpperInvariant();
        int month = int.Parse(Opt(o, "first-month", "1"), CultureInfo.InvariantCulture);
        DateOnly from = o.TryGetValue("valid-from", out var vf) ? ParseDate(vf, "valid-from") : new DateOnly(DateTime.UtcNow.Year, 1, 1);
        var company = await provisioner.CreateCompanyAsync(
            new(Req(o, "tenant"), Req(o, "code"), Req(o, "name"), Req(o, "country").ToUpperInvariant(), local, reporting, group, month, from), ct);
        output.WriteLine($"company {company.Code} created ({company.CompanyId}) in DRAFT: {company.LegalName}, {company.Country}");
        return 0;
    }

    private async Task<int> CompanyList(Dictionary<string, string> o, CancellationToken ct)
    {
        Guid tenantId = await provisioner.RequireTenantAsync(Req(o, "tenant"), ct);
        var companies = await provisioner.ListCompaniesAsync(tenantId, ct);
        if (companies.Count == 0) output.WriteLine("no companies");
        foreach (var c in companies)
            output.WriteLine($"{c.Code,-12} {c.Status,-9} {c.Country} {c.PackageCode ?? "-",-12} {c.LegalName} ({c.CompanyId})");
        return 0;
    }

    private async Task<int> CompanySetup(Dictionary<string, string> o, HashSet<string> flags, CancellationToken ct)
    {
        string packageCode = Req(o, "package");
        CountryPackage package = CountryPackages.Find(packageCode)
            ?? throw new ProvisioningException("PACKAGE.NOT_FOUND", $"No package {packageCode} (try: package list)");
        int year = int.Parse(Opt(o, "year", DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        Guid tenantId = await provisioner.RequireTenantAsync(Req(o, "tenant"), ct);
        CompanyInfo company = await provisioner.RequireCompanyAsync(tenantId, Req(o, "company"), ct);
        await provisioner.ApplyPackageAsync(tenantId, company, package, year, ct);
        output.WriteLine($"package {package.Code} {package.Version} applied to {company.Code}: {package.Accounts.Count} accounts, {package.Series.Count} number series, " +
                         $"{package.Roles.Count} roles, {package.Retention.Count} retention rules, fiscal year {year} open");
        if (flags.Contains("activate")) return await Activate(tenantId, company with { PackageCode = package.Code }, ct);
        output.WriteLine("the company is still in DRAFT; exchange rates are not part of a package (rate add), then: company activate");
        return 0;
    }

    private async Task<int> CompanyActivate(Dictionary<string, string> o, CancellationToken ct)
    {
        Guid tenantId = await provisioner.RequireTenantAsync(Req(o, "tenant"), ct);
        return await Activate(tenantId, await provisioner.RequireCompanyAsync(tenantId, Req(o, "company"), ct), ct);
    }

    private async Task<int> Activate(Guid tenantId, CompanyInfo company, CancellationToken ct)
    {
        var (activated, failed) = await provisioner.ActivateAsync(tenantId, company, ct);
        if (activated) { output.WriteLine($"company {company.Code} is ACTIVE"); return 0; }
        output.WriteLine($"company {company.Code} is not ready:");
        foreach (string f in failed) output.WriteLine("  " + f);
        return 1;
    }

    private async Task<int> RateAdd(Dictionary<string, string> o, CancellationToken ct)
    {
        decimal rate = decimal.TryParse(Req(o, "rate"), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new UsageException("--rate is a number");
        DateOnly date = ParseDate(Req(o, "date"), "date");
        Guid tenantId = await provisioner.RequireTenantAsync(Req(o, "tenant"), ct);
        await provisioner.AddExchangeRateAsync(tenantId, Opt(o, "type", "SPOT").ToUpperInvariant(), Req(o, "from").ToUpperInvariant(), Req(o, "to").ToUpperInvariant(),
            date, rate, ct);
        output.WriteLine($"rate {Req(o, "from").ToUpperInvariant()}/{Req(o, "to").ToUpperInvariant()} {rate.ToString(CultureInfo.InvariantCulture)} added for {Req(o, "date")}");
        return 0;
    }

    private int PackageList()
    {
        foreach (var p in CountryPackages.All) output.WriteLine($"{p.Code,-12} {p.Version,-5} {p.Country}  {p.Description}");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------
    private sealed class UsageException(string message) : Exception(message);

    private static string Req(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var v) && v.Length > 0 ? v : throw new UsageException($"--{name} is required");

    private static string Opt(Dictionary<string, string> o, string name, string fallback) => o.TryGetValue(name, out var v) ? v : fallback;

    private static DateOnly ParseDate(string text, string name) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new UsageException($"--{name} is a date as yyyy-MM-dd");

    /// <summary>Words first, then --name value pairs. A name followed by another name or nothing is a flag.</summary>
    private static (List<string> Words, Dictionary<string, string> Options, HashSet<string> Flags) Parse(string[] args)
    {
        var words = new List<string>();
        var options = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) { words.Add(args[i]); continue; }
            string name = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) options[name] = args[++i];
            else flags.Add(name);
        }
        return (words, options, flags);
    }
}
