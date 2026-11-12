using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinanceCore.Core.Packages;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Roles;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>The inputs of the VAT content of a country package (18, 4.3): facts of the company, not content of the package.</summary>
public sealed record PackageInputs(
    string VatId, string FilingFrequency, string? EcListFrequency, string CorrectionMethod, int VatProfile, bool IncludeSpecialCodes,
    DateOnly FirstFiscalYearStart, string LocalCurrency, decimal? ProRataRatePct = null, decimal? EstimateRatePct = null,
    decimal? SectorRatePct = null, decimal? AreaRatePct = null);

/// <summary>An object of the package and what the apply did with it (18, 7.1): CREATED, or SKIPPED_EXISTS when the company already had it.</summary>
public sealed record AppliedItem(string ItemId, string EntityType, string Action, string? EntityId = null);

/// <summary>What the verification of a signed archive proves (20, 11): kept in the record of the apply.</summary>
public sealed record SignedSource(string ArchiveSha256, string SignerKeyId, string ManifestSha256);

public sealed record PackageApplyResult(string PackageCode, string Version, string State, IReadOnlyList<AppliedItem> Items, Guid AppliedId = default);

/// <summary>A row of the record of applies (18, 7.1).</summary>
public sealed record AppliedPackageInfo(Guid AppliedId, string PackageCode, string PackageVersion, string State, int ItemCount, int CreatedCount, DateTime AppliedAt, string AppliedBy);

/// <summary>
/// The VAT content of a country package (18, 8: tax return boxes, the VAT registration, the deduction keys and rates, the tax codes with their versions and
/// box mappings, the determination rules with their alternatives, the VAT_SETTLEMENT role and the accounts they name, and the tax classes of EU_VAT) applied
/// to one company through the command handlers of 07 (create-only: an object that exists is never changed, 18, 7). The package is read from a directory
/// in the format of 18, 4 (package.json and core/*.json). The signed archive (ADR-217) is applied by ApplyVatFromArchivesAsync and a PRODUCTION tenant accepts nothing else. Not built: VALIDATE
/// mode, config-packages:diff, the policy file, and the other core content of the package (the chart, number series, retention, payment terms, settings), which the first-slice
/// package of 07, 11.4 still carries (F-IMPL-39).
/// A package whose state is not RELEASED is refused for a PRODUCTION tenant (18, 6: PACKAGE.NOT_RELEASED).
/// </summary>
public sealed class CountryPackageApplier(NpgsqlDataSource source)
{
    private const string Actor = PlatformProvisioner.Actor;

    private static CommandContext Context(string key, params string[] permissions) =>
        new(key, "platform-" + key, Actor, new HashSet<string>(permissions), "PLATFORM");

    public async Task<PackageApplyResult> ApplyVatAsync(
        Guid tenantId, CompanyInfo company, string packagesDirectory, string packageCode, PackageInputs inputs, CancellationToken ct = default, SignedSource? signed = null)
    {
        string dir = Path.Combine(packagesDirectory, packageCode);
        if (!File.Exists(Path.Combine(dir, "package.json")))
            throw new ProvisioningException("PACKAGE.NOT_FOUND", $"No package {packageCode} in {packagesDirectory}");
        JsonObject manifest = Read(Path.Combine(dir, "package.json"));
        string version = (string)manifest["package_version"]!, state = (string)manifest["state"]!;
        if ((string)manifest["country_code"]! != company.Country)
            throw new ProvisioningException("PACKAGE.COUNTRY_MISMATCH", $"The package {packageCode} is for {manifest["country_code"]}, the company is in {company.Country}");
        if (inputs.VatProfile is < 1 or > 4) throw new ProvisioningException("PACKAGE.INPUT_INVALID", "vat_profile is 1 to 4");
        if (inputs.FilingFrequency is not ("MONTHLY" or "QUARTERLY" or "HALF_YEARLY"))
            throw new ProvisioningException("PACKAGE.INPUT_INVALID", "filing_frequency is MONTHLY, QUARTERLY or HALF_YEARLY");
        if (string.IsNullOrWhiteSpace(inputs.VatId)) throw new ProvisioningException("PACKAGE.INPUT_INVALID", "vat_id is required");
        string purpose = (string?)await ScalarAsync(tenantId, "select purpose from platform.tenant where tenant_id = @t", ct)
                         ?? throw new ProvisioningException("TENANT.NOT_FOUND", "No such tenant");
        if (purpose == "PRODUCTION" && state != "RELEASED")
            throw new ProvisioningException("PACKAGE.NOT_RELEASED", $"The package {packageCode} {version} is {state}: a PRODUCTION tenant accepts only RELEASED packages");
        if (purpose == "PRODUCTION" && signed is null)
            throw new ProvisioningException("PACKAGE.SIGNATURE_INVALID", $"The package {packageCode} {version} comes from a directory: a PRODUCTION tenant accepts signed archives only (20, 11)");

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", inputs.VatId, inputs.FilingFrequency, inputs.EcListFrequency, inputs.CorrectionMethod, inputs.VatProfile, inputs.IncludeSpecialCodes,
                inputs.FirstFiscalYearStart, inputs.LocalCurrency, inputs.ProRataRatePct, inputs.EstimateRatePct, inputs.SectorRatePct, inputs.AreaRatePct)))).ToLowerInvariant()[..8];
        string prefix = $"pkg-{company.CompanyId:N}-{packageCode}{version}-{digest}-";
        var items = new List<AppliedItem>();
        string Track<T>(string itemId, string type, CommandResult<T> r)
        {
            string action = Classify(itemId, r);
            items.Add(new AppliedItem(itemId, type, action, action == "CREATED" ? EntityOf(r.Value) : null));
            return action;
        }
        var tax = new TaxCommands(source);
        var deduction = new DeductionCommands(source);
        var rules = new TaxDeterminationCommands(source);
        const string Config = "write.config";

        // the tax classes of EU_VAT, once per tenant
        var classIds = new Dictionary<string, Guid>();
        string classFile = Path.Combine(packagesDirectory, "EU_VAT", "core", "tax_class.json");
        if (File.Exists(classFile))
            foreach (JsonObject o in Objects(Read(classFile)))
            {
                string code = (string)o["code"]!;
                var r = await tax.CreateClassAsync(tenantId, Context(prefix + (string)o["item_id"]!, Config), new(Fields(("code", code), ("name", (string)o["name"]!))), ct);
                Track((string)o["item_id"]!, "TAX_CLASS", r);
                classIds["EU_VAT:" + (string)o["ref"]!] = (Guid)(await ScalarAsync(tenantId, "select tax_class_id from core.tax_class where tenant_id = @t and code = '" + code.Replace("'", "''") + "'", ct))!;
            }

        // the return boxes, once per tenant and country
        foreach (JsonObject o in Objects(Read(Path.Combine(dir, "core", "tax_return_box.json"))))
        {
            var fields = Fields(("name", (string)o["name"]!));
            if (o["box_group"] is { } g) fields["box_group"] = (string)g!;
            var r = await tax.CreateBoxAsync(tenantId, Context(prefix + (string)o["item_id"]!, Config), new((string)o["country_code"]!, (string)o["return_type"]!, (string)o["box_code"]!, fields), ct);
            Track((string)o["item_id"]!, "TAX_RETURN_BOX", r);
        }

        // the accounts that the VAT content names, created when the company has no account of that number
        JsonObject chart = Read(Path.Combine(dir, "core", "gl_account.json"));
        var accountNeeded = new HashSet<string>();
        JsonObject codeFile = Read(Path.Combine(dir, "core", "tax_code.json"));
        var selected = Objects(codeFile).Where(o => Selected(o, inputs)).ToList();
        foreach (JsonObject o in selected)
        {
            accountNeeded.Add((string)o["version"]!["vat_account"]!);
            if (o["version"]!["reverse_charge_account"] is { } rc) accountNeeded.Add((string)rc!);
        }
        JsonObject? settlementRole = Objects(Read(Path.Combine(dir, "core", "account_role_assignment.json"))).FirstOrDefault(o => (string)o["role_code"]! == "VAT_SETTLEMENT");
        if (settlementRole is not null) accountNeeded.Add((string)settlementRole["account"]!);
        var accountIds = new Dictionary<string, Guid>();
        foreach (string accountRef in accountNeeded.Order())
        {
            JsonObject a = Objects(chart).First(o => (string)o["ref"]! == accountRef);
            string no = (string)a["account_no"]!;
            if (await ScalarAsync(tenantId, "select account_id from core.gl_account where tenant_id = @t and company_id = '" + company.CompanyId + "' and account_no = '" + no + "'", ct) is Guid existing)
            {
                accountIds[accountRef] = existing;
                items.Add(new AppliedItem((string)a["item_id"]!, "GL_ACCOUNT", "SKIPPED_EXISTS"));
                continue;
            }
            Guid id = Guid.NewGuid();
            await ExecuteAsync(tenantId,
                "insert into core.gl_account (tenant_id, company_id, account_id, account_no, standard_account_code, name, account_type, open_item_mode, tax_code_rule) " +
                "values (@t, @c, @id, @no, @std, @n, @ty, @oi, @rule)", ct,
                ("c", company.CompanyId), ("id", id), ("no", no), ("std", (string)a["standard_account_code"]!), ("n", (string)a["name"]!), ("ty", (string)a["account_type"]!),
                ("oi", (string)a["open_item_mode"]!), ("rule", (string)a["tax_code_rule"]!));
            accountIds[accountRef] = id;
            items.Add(new AppliedItem((string)a["item_id"]!, "GL_ACCOUNT", "CREATED"));
        }

        // the registration: the facts are inputs (18, 4.3)
        JsonObject regObject = Objects(Read(Path.Combine(dir, "core", "company_vat_registration.json"))).First();
        var regFields = Fields(("company_id", company.CompanyId), ("country_code", (string)regObject["country_code"]!), ("vat_id", inputs.VatId), ("scheme", (string)regObject["scheme"]!),
            ("filing_frequency", inputs.FilingFrequency), ("correction_method", inputs.CorrectionMethod), ("return_currency", inputs.LocalCurrency),
            ("vat_rate_type", (string)regObject["vat_rate_type"]!), ("valid_from", inputs.FirstFiscalYearStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (inputs.EcListFrequency is not null) regFields["ec_list_frequency"] = inputs.EcListFrequency;
        Track((string)regObject["item_id"]!, "VAT_REGISTRATION", await tax.CreateRegistrationAsync(tenantId, Context(prefix + (string)regObject["item_id"]!, Config), new(regFields), ct));
        Guid registrationId = (Guid)(await ScalarAsync(tenantId,
            "select registration_id from core.company_vat_registration where tenant_id = @t and company_id = '" + company.CompanyId + "' and country_code = '" + (string)regObject["country_code"]! +
            "' and scheme = '" + (string)regObject["scheme"]! + "' and valid_from = date '" + inputs.FirstFiscalYearStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'", ct))!;

        // the deduction keys and the initial rates that the inputs give
        var keyIds = new Dictionary<string, Guid>();
        var rateInputs = new Dictionary<string, decimal?>
        {
            ["pro_rata_rate_pct"] = inputs.ProRataRatePct, ["estimate_rate_pct"] = inputs.EstimateRatePct, ["sector_rate_pct"] = inputs.SectorRatePct, ["area_rate_pct"] = inputs.AreaRatePct
        };
        foreach (JsonObject o in Objects(Read(Path.Combine(dir, "core", "vat_deduction_key.json"))))
        {
            var fields = Fields(("company_id", company.CompanyId), ("registration_id", registrationId), ("key_code", (string)o["key_code"]!), ("kind", (string)o["kind"]!), ("description", (string)o["description"]!));
            Track((string)o["item_id"]!, "VAT_DEDUCTION_KEY", await deduction.CreateKeyAsync(tenantId, Context(prefix + (string)o["item_id"]!, Config), new(fields), ct));
            keyIds[(string)o["ref"]!] = (Guid)(await ScalarAsync(tenantId,
                "select deduction_key_id from core.vat_deduction_key where tenant_id = @t and company_id = '" + company.CompanyId + "' and registration_id = '" + registrationId + "' and key_code = '" + (string)o["key_code"]! + "'", ct))!;
        }
        foreach (JsonObject o in Objects(Read(Path.Combine(dir, "core", "vat_deduction_rate.json"))))
        {
            if (rateInputs.GetValueOrDefault((string)o["when_input"]!) is not { } pct) continue;
            var fields = Fields(("valid_from", inputs.FirstFiscalYearStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("rate_pct", pct), ("rate_status", (string)o["rate_status"]!));
            if (o["basis_note"] is { } note) fields["basis_note"] = (string)note!;
            Track((string)o["item_id"]!, "VAT_DEDUCTION_RATE", await deduction.CreateRateAsync(tenantId, Context(prefix + (string)o["item_id"]!, Config), new(keyIds[(string)o["key"]!], fields), ct));
        }

        // the tax codes of the chosen profile, each with its first version and its boxes
        var codeIds = new Dictionary<string, Guid>();
        foreach (JsonObject o in selected)
        {
            string code = (string)o["code"]!;
            var codeFields = Fields(("company_id", company.CompanyId), ("code", code), ("description", (string)o["description"]!), ("tax_type", (string)o["tax_type"]!), ("vat_registration_id", registrationId));
            Track((string)o["item_id"]!, "TAX_CODE", await tax.CreateCodeAsync(tenantId, Context(prefix + (string)o["item_id"]!, Config), new(codeFields), ct));
            Guid codeId = (Guid)(await ScalarAsync(tenantId, "select tax_code_id from core.tax_code where tenant_id = @t and company_id = '" + company.CompanyId + "' and code = '" + code.Replace("'", "''") + "'", ct))!;
            codeIds[(string)o["ref"]!] = codeId;

            JsonObject v = (JsonObject)o["version"]!;
            var vf = Fields(("valid_from", (string)v["valid_from"]!), ("rate", Dec(v["rate"])), ("vat_account_id", accountIds[(string)v["vat_account"]!]), ("reverse_charge", (bool)v["reverse_charge"]!));
            if (v["reverse_charge_account"] is { } rca) vf["reverse_charge_account_id"] = accountIds[(string)rca!];
            foreach (string name in new[] { "en16931_vat_category", "exemption_reason_code", "ec_list_category", "supply_kind", "evidence_required_type", "deduction_key", "deduction_cap" })
                if (v[name] is { } value) vf[name] = (string)value!;
            if (v["deductible_pct"] is { } pctValue) vf["deductible_pct"] = Dec(pctValue);
            var boxes = ((JsonArray)(v["boxes"] ?? new JsonArray())).Select(b => new TaxBoxMapping((string)b!["line_side"]!, (string)b["return_type"]!, (string)b["box"]!, (string)b["amount_kind"]!, (int)b["sign"]!)).ToList();
            var r = await tax.CreateVersionAsync(tenantId, Context(prefix + (string)o["item_id"]! + "-v1", Config), new(codeId, vf, boxes), ct);
            // a code that exists with a version already is create-only: the version is not a second one (18, 7)
            string versionAction = Classify((string)o["item_id"]! + "/v1", r);
            items.Add(new AppliedItem((string)o["item_id"]! + "/v1", "TAX_CODE_VERSION", versionAction, versionAction == "CREATED" ? codeId.ToString("D") : null));
        }

        // the determination rules whose default code is applied, with the alternatives that are applied
        foreach (JsonObject o in Objects(Read(Path.Combine(dir, "core", "tax_determination_rule.json"))))
        {
            if (!codeIds.TryGetValue((string)o["tax_code"]!, out Guid defaultCode)) { items.Add(new AppliedItem((string)o["item_id"]!, "TAX_DETERMINATION_RULE", "SKIPPED_NOT_SELECTED")); continue; }
            var fields = Fields(("valid_from", (string)o["valid_from"]!), ("priority", (int)o["priority"]!), ("tax_code_id", defaultCode));
            foreach (string name in new[] { "transaction_type", "origin_country", "destination_country", "partner_vat_status" })
                if (o[name] is { } value) fields[name] = (string)value!;
            foreach (string name in new[] { "origin_eu", "destination_eu" })
                if (o[name] is { } value) fields[name] = (bool)value!;
            if (o["tax_class"] is { } cls) fields["tax_class_id"] = classIds[(string)cls!];
            var alternatives = ((JsonArray)o["alternatives"]!).Select(a => (string)a!).Where(codeIds.ContainsKey).Select(a => codeIds[a]).ToList();
            Track((string)o["item_id"]!, "TAX_DETERMINATION_RULE", await rules.CreateRuleAsync(tenantId, Context(prefix + (string)o["item_id"]!, Config), new(company.CompanyId, fields, alternatives), ct));
        }

        // the settlement document needs a series (04, 5.2): the package assigns VAT_SETTLE to its general ledger series, which the company has
        JsonObject numbering = Read(Path.Combine(dir, "core", "number_series.json"));
        foreach (JsonObject a in ((JsonArray)numbering["assignments"]!).Select(x => (JsonObject)x!).Where(x => VatDocumentTypes.Contains((string)x["document_type"]!)))
        {
            string seriesCode = (string)((JsonArray)numbering["series"]!).Select(s => (JsonObject)s!).First(s => (string)s["ref"]! == (string)a["series"]!)["series_code"]!;
            if (await ScalarAsync(tenantId, "select series_id from core.number_series where tenant_id = @t and company_id = '" + company.CompanyId + "'::uuid and series_code = '" + seriesCode.Replace("'", "''") + "'", ct) is not Guid seriesId)
                throw new ProvisioningException("PACKAGE.REFERENCE_UNRESOLVED", $"{(string)a["item_id"]!}: the company has no number series {seriesCode}, which the package assigns to {(string)a["document_type"]!}");
            if (await ScalarAsync(tenantId, "select 1 from core.number_series_assignment where tenant_id = @t and company_id = '" + company.CompanyId + "'::uuid and document_type = '" + (string)a["document_type"]! + "' limit 1", ct) is not null)
            {
                items.Add(new AppliedItem((string)a["item_id"]!, "NUMBER_SERIES_ASSIGNMENT", "SKIPPED_EXISTS"));
                continue;
            }
            await ExecuteAsync(tenantId,
                "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) values (@t, @c, @ty, @s, @from, @by)", ct,
                ("c", company.CompanyId), ("ty", (string)a["document_type"]!), ("s", seriesId), ("from", inputs.FirstFiscalYearStart), ("by", Actor));
            items.Add(new AppliedItem((string)a["item_id"]!, "NUMBER_SERIES_ASSIGNMENT", "CREATED", seriesId.ToString("D")));
        }

        // the role of the settlement (08, 6.2: per registration)
        if (settlementRole is not null && await ScalarAsync(tenantId, "select 1 from core.account_role_assignment where tenant_id = @t and company_id = '" + company.CompanyId + "'::uuid and role_code = 'VAT_SETTLEMENT' and vat_registration_id = '" + registrationId + "'::uuid limit 1", ct) is not null)
            items.Add(new AppliedItem((string)settlementRole["item_id"]!, "ACCOUNT_ROLE_ASSIGNMENT", "SKIPPED_EXISTS"));   // a role version is not create-only: a second assignment would be a new version
        else if (settlementRole is not null)
        {
            var r = await new AccountRoleAssigner(source).AssignAsync(tenantId, Context(prefix + (string)settlementRole["item_id"]!, Config),
                new AssignAccountRoleRequest(company.CompanyId, "VAT_SETTLEMENT", accountIds[(string)settlementRole["account"]!], VatRegistrationId: registrationId), ct);
            Track((string)settlementRole["item_id"]!, "ACCOUNT_ROLE_ASSIGNMENT", r);
        }

        // the record of the apply (18, 7.1)
        string manifestDigest = ManifestDigest(dir);
        string requires = File.Exists(classFile) ? "EU_VAT " + (string?)Read(Path.Combine(packagesDirectory, "EU_VAT", "package.json"))["package_version"] : "";
        Guid appliedId = Guid.NewGuid();
        await ExecuteAsync(tenantId,
            "insert into core.applied_config_package (tenant_id, applied_id, company_id, package_code, package_version, country_code, kind, state, manifest_digest, inputs_digest, requires_snapshot, " +
            "item_count, created_count, applied_by, archive_sha256, signer_key_id, signing_manifest_sha256) values (@t, @id, @c, @code, @v, @cty, @kind, @state, @md, @idg, @req, @n, @created, @by, @ash, @key, @msh)", ct,
            ("id", appliedId), ("c", company.CompanyId), ("code", packageCode), ("v", version), ("cty", company.Country), ("kind", (string)manifest["kind"]!), ("state", state),
            ("md", manifestDigest), ("idg", digest), ("req", requires), ("n", items.Count), ("created", items.Count(i => i.Action == "CREATED")), ("by", Actor),
            ("ash", (object?)signed?.ArchiveSha256 ?? DBNull.Value), ("key", (object?)signed?.SignerKeyId ?? DBNull.Value), ("msh", (object?)signed?.ManifestSha256 ?? DBNull.Value));
        for (int n = 0; n < items.Count; n++)
            await ExecuteAsync(tenantId,
                "insert into core.applied_config_package_item (tenant_id, applied_id, item_no, item_id, entity_type, entity_id, action) values (@t, @id, @n, @item, @type, @eid, @action)", ct,
                ("id", appliedId), ("n", n + 1), ("item", items[n].ItemId), ("type", items[n].EntityType), ("eid", (object?)items[n].EntityId ?? DBNull.Value), ("action", items[n].Action));
        return new PackageApplyResult(packageCode, version, state, items, appliedId);
    }


    /// <summary>
    /// The apply from signed archives (ADR-217, 20, 11): the archive of the package and of every package it requires, each with its detached signature, are verified
    /// against the trust set of the tenant purpose, extracted to a temporary directory and applied from there. The record keeps the digest of the archive and the signer.
    /// The archives are named {code}-{version}.tar with the signature in {code}-{version}.sig.json. The core never fetches them.
    /// </summary>
    public async Task<PackageApplyResult> ApplyVatFromArchivesAsync(
        Guid tenantId, CompanyInfo company, string archiveDirectory, string packageCode, TrustSet trust, PackageInputs inputs, CancellationToken ct = default)
    {
        string purpose = (string?)await ScalarAsync(tenantId, "select purpose from platform.tenant where tenant_id = @t", ct)
                         ?? throw new ProvisioningException("TENANT.NOT_FOUND", "No such tenant");
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        string root = Path.Combine(Path.GetTempPath(), "financecore-pkg-" + Guid.NewGuid().ToString("N"));
        try
        {
            VerifiedPackage country = LoadArchive(archiveDirectory, packageCode, trust, purpose, today);
            Extract(country, root);
            using (JsonDocument manifest = JsonDocument.Parse(country.Files["package.json"]))
                foreach (JsonElement req in manifest.RootElement.TryGetProperty("requires", out JsonElement list) ? list.EnumerateArray() : [])
                {
                    string code = req.GetProperty("package_code").GetString()!;
                    VerifiedPackage required;
                    try
                    {
                        required = LoadArchive(archiveDirectory, code, trust, purpose, today);
                    }
                    catch (PackageException e) when (e.Code == "PACKAGE.NOT_FOUND")
                    {
                        throw new ProvisioningException("PACKAGE.REQUIRES_NOT_SATISFIED", $"{packageCode} requires {code}, and {archiveDirectory} has no archive of it");
                    }
                    string range = req.TryGetProperty("range", out JsonElement rg) ? rg.GetString() ?? "" : "";
                    if (range.StartsWith('^') && required.PackageVersion.Split('.')[0] != range[1..].Split('.')[0])
                        throw new ProvisioningException("PACKAGE.REQUIRES_NOT_SATISFIED", $"{packageCode} requires {code} {range}, the archive is {required.PackageVersion}");
                    Extract(required, root);
                }
            return await ApplyVatAsync(tenantId, company, root, packageCode, inputs, ct, new SignedSource(country.ArchiveSha256, country.SignerKeyId, country.ManifestSha256));
        }
        catch (PackageException e)
        {
            throw new ProvisioningException(e.Code, e.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static VerifiedPackage LoadArchive(string directory, string code, TrustSet trust, string purpose, DateOnly today)
    {
        if (!Directory.Exists(directory)) throw new PackageException("PACKAGE.NOT_FOUND", $"No directory {directory}");
        // the newest version that has both the archive and its signature
        string? tar = Directory.GetFiles(directory, code + "-*.tar")
            .Where(f => File.Exists(Path.ChangeExtension(f, ".sig.json")) && Path.GetFileNameWithoutExtension(f)[(code.Length + 1)..].Split('.').All(p => p.All(char.IsDigit)))
            .OrderByDescending(f => Version.TryParse(Path.GetFileNameWithoutExtension(f)[(code.Length + 1)..], out Version? v) ? v : new Version(0, 0), Comparer<Version>.Default).FirstOrDefault();
        if (tar is null) throw new PackageException("PACKAGE.NOT_FOUND", $"No signed archive of {code} in {directory}");
        return PackageSigning.Verify(File.ReadAllBytes(tar), File.ReadAllText(Path.ChangeExtension(tar, ".sig.json")), trust, purpose, today);
    }

    private static void Extract(VerifiedPackage package, string root)
    {
        foreach (var (path, bytes) in package.Files)
        {
            string target = Path.GetFullPath(Path.Combine(root, package.PackageCode, path));
            if (!target.StartsWith(Path.GetFullPath(Path.Combine(root, package.PackageCode)) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new PackageException("PACKAGE.DIGEST_MISMATCH", $"The path {path} leaves the package directory");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
    }

    /// <summary>The applies that a company has had, the newest first (18, 7.2 `config-packages:list-applied`).</summary>
    public async Task<IReadOnlyList<AppliedPackageInfo>> ListAppliedAsync(Guid tenantId, Guid companyId, CancellationToken ct = default)
    {
        var list = new List<AppliedPackageInfo>();
        await using NpgsqlConnection c = await source.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await c.BeginTransactionAsync(ct);
        await SetTenantAsync(c, tx, tenantId, ct);
        await using var cmd = new NpgsqlCommand(
            "select applied_id, package_code, package_version, state, item_count, created_count, applied_at, applied_by from core.applied_config_package " +
            "where tenant_id = @t and company_id = @c order by applied_at desc, applied_id", c, tx);
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AppliedPackageInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetDateTime(6), r.GetString(7)));
        return list;
    }

    /// <summary>The document types of the VAT content whose number series assignment the package carries.</summary>
    private static readonly string[] VatDocumentTypes = ["VAT_SETTLE"];

    private static string EntityOf(object? value)
    {
        if (value is null) return null!;
        foreach (string name in new[] { "EntityId", "RuleId", "AssignmentId" })
            if (value.GetType().GetProperty(name)?.GetValue(value) is Guid id) return id.ToString("D");
        return null!;
    }

    /// <summary>SHA-256 over package.json and the files of core/ in name order (18, 4.1 content_digest, for the core files).</summary>
    private static string ManifestDigest(string dir)
    {
        using var all = new MemoryStream();
        foreach (string file in new[] { Path.Combine(dir, "package.json") }.Concat(Directory.GetFiles(Path.Combine(dir, "core"), "*.json").Order(StringComparer.Ordinal)))
        {
            all.Write(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\n"));
            all.Write(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return Convert.ToHexString(SHA256.HashData(all.ToArray())).ToLowerInvariant();
    }

    /// <summary>The codes of the chosen business type, and the special codes (profile 5) on request (package.json, inputs).</summary>
    private static bool Selected(JsonObject code, PackageInputs inputs)
    {
        var profiles = ((JsonArray)code["profiles"]!).Select(p => (int)p!).ToHashSet();
        return profiles.Contains(inputs.VatProfile) || (inputs.IncludeSpecialCodes && profiles.Contains(5));
    }

    private static string Classify<T>(string itemId, CommandResult<T> r)
    {
        if (r.Status == CommandStatus.Ok) return r.IdempotentReplay ? "SKIPPED_EXISTS" : "CREATED";
        // create-only: an object that exists is not changed. These are the answers of the commands for an object that is there
        if (r.Errors.All(e => e.Code is "CONFIG.DUPLICATE_CODE" or "CONFIG.OVERLAPPING_RULE" or "CONFIG.VALID_FROM_NOT_LATER")) return "SKIPPED_EXISTS";
        throw new ProvisioningException("PACKAGE.STEP_FAILED", $"{itemId}: " + string.Join(", ", r.Errors.Select(e => e.Code + (e.Path.Length > 0 ? " " + e.Path : ""))));
    }

    private static decimal Dec(JsonNode? node) =>
        decimal.Parse(node is JsonValue v && v.TryGetValue<string>(out string? s) ? s! : node!.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static Dictionary<string, object?> Fields(params (string, object?)[] fields) => fields.ToDictionary(f => f.Item1, f => f.Item2);

    private static JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    private static IEnumerable<JsonObject> Objects(JsonObject file) => ((JsonArray)file["objects"]!).Select(o => (JsonObject)o!);

    // ---------------------------------------------------------------------------------------------------------
    private async Task<object?> ScalarAsync(Guid tenantId, string sql, CancellationToken ct)
    {
        await using NpgsqlConnection c = await source.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await c.BeginTransactionAsync(ct);
        await SetTenantAsync(c, tx, tenantId, ct);
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private async Task ExecuteAsync(Guid tenantId, string sql, CancellationToken ct, params (string, object)[] parameters)
    {
        await using NpgsqlConnection c = await source.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await c.BeginTransactionAsync(ct);
        await SetTenantAsync(c, tx, tenantId, ct);
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task SetTenantAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select set_config('app.tenant_id', @t, true)", c, tx);
        cmd.Parameters.AddWithValue("t", tenantId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
