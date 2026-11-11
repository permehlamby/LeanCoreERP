namespace FinanceCore.Core.Packages;

public sealed record PackageAccount(string AccountNo, string Name, string AccountType, string OpenItemMode = "NONE");

/// <summary>A number series and the document types that draw from it.</summary>
public sealed record PackageSeries(string Code, string Description, string Prefix, IReadOnlyList<string> DocumentTypes);

public sealed record PackageRetention(string RecordClass, int Years, string StartEvent, string? LegalBasis);

/// <summary>
/// A country package of the first slice (07, 11.4, 18): the configuration a company of the country needs before it can post, as data in code.
/// This is not the signed, versioned package format of 18: it has no tax, no dimensions, no signature and no update path, and the
/// platform CLI applies it once to a company in DRAFT.
/// </summary>
public sealed record CountryPackage(
    string Code, string Version, string Country, string Description,
    IReadOnlyList<PackageAccount> Accounts, IReadOnlyList<PackageSeries> Series,
    IReadOnlyList<(string Role, string AccountNo)> Roles, IReadOnlyList<PackageRetention> Retention);

public static class CountryPackages
{
    /// <summary>A small Danish chart and the configuration for the posting commands of the slice. The legal basis of the retention rules is
    /// UNVERIFIED (18, ADR-198): the DK legal reviewer confirms it before a production tenant uses the package.</summary>
    public static readonly CountryPackage DkMinimal = new(
        "dk-minimal", "0.1", "DK", "Danish minimal chart, number series and retention rules for the first slice (no tax)",
        [
            new("1000", "Bank", "ASSET"),
            new("1300", "Debitorer", "ASSET", "CONTROL"),
            new("2000", "Kreditorer", "LIABILITY", "CONTROL"),
            new("3000", "Salg", "REVENUE"),
            new("4000", "Omkostninger", "EXPENSE"),
            new("7900", "Afrundingsdifferencer", "EXPENSE"),
            new("7910", "Afrunding af moms", "EXPENSE"),
            new("7920", "Realiserede kursdifferencer", "EXPENSE"),
            new("7930", "Urealiserede kursgevinster", "REVENUE"),
            new("7940", "Urealiserede kurstab", "EXPENSE"),
            new("8100", "Overført resultat", "EQUITY")
        ],
        [
            new("GL-JRN", "General journal", "GL", ["JOURNAL"]),
            new("GL-REV", "Reversals", "RV", ["REVERSAL"]),
            new("BANK", "Payments", "BK", ["PAYMENT"]),
            new("AR", "Sales invoices and credit notes", "AR", ["AR_INVOICE", "AR_CREDIT"]),
            new("AP", "Purchase invoices and credit notes", "AP", ["AP_INVOICE", "AP_CREDIT"])
        ],
        [
            ("ROUNDING", "7900"), ("TAX_ROUNDING", "7910"), ("FX_REALISED", "7920"),
            ("FX_UNREALISED_GAIN", "7930"), ("FX_UNREALISED_LOSS", "7940"), ("RETAINED_EARNINGS", "8100")
        ],
        [
            new("LEDGER", 5, "FISCAL_YEAR_END", "Bogføringsloven (UNVERIFIED)"),
            new("LEGAL_SNAPSHOT", 5, "FISCAL_YEAR_END", "Bogføringsloven (UNVERIFIED)")
        ]);

    public static readonly IReadOnlyList<CountryPackage> All = [DkMinimal];

    public static CountryPackage? Find(string? code) => All.FirstOrDefault(p => p.Code == code);
}
