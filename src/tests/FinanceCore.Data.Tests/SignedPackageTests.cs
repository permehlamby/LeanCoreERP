using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FinanceCore.Core.Packages;

namespace FinanceCore.Data.Tests;

/// <summary>The apply of the DK package from signed archives (ADR-217, 20, 11) and the gate for a PRODUCTION tenant.</summary>
[Collection("db")]
public class SignedPackageTests
{
    private static string PackagesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "country-packages", "DK"))) return Path.Combine(dir.FullName, "country-packages");
        throw new DirectoryNotFoundException("country-packages was not found above " + AppContext.BaseDirectory);
    }

    private static CompanyInfo Company(TestTenant t) => new(t.CompanyId, "C1", "Test A/S", "DK", "ACTIVE", null);
    private static PackageInputs Inputs() => new("DK12345678", "QUARTERLY", null, "ADJUST_NEXT", 1, false, new DateOnly(2026, 1, 1), "DKK");

    private sealed class Archives : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "fc-test-" + Guid.NewGuid().ToString("N"));
        public string KeyId { get; }
        public string PublicPem { get; }
        private readonly string _private;

        public Archives(string keyId = "dev-1")
        {
            KeyId = keyId;
            (_private, PublicPem) = PackageSigning.GenerateKey();
            System.IO.Directory.CreateDirectory(Directory);
        }

        public TrustSet Trust(bool release = false) => new([new TrustedKey(KeyId, PublicPem, release)], new HashSet<string>());

        /// <summary>Packs a package directory, with an optional change to package.json and verification.json before the manifest is made.</summary>
        public void Pack(string packageDir, Action<SortedDictionary<string, byte[]>>? edit = null)
        {
            var files = PackageSigning.ReadDirectory(packageDir);
            edit?.Invoke(files);
            byte[] manifest = PackageSigning.BuildManifest(files);
            using var key = ECDsa.Create();
            key.ImportFromPem(_private);
            using var pj = System.Text.Json.JsonDocument.Parse(files["package.json"]);
            string name = pj.RootElement.GetProperty("package_code").GetString() + "-" + pj.RootElement.GetProperty("package_version").GetString();
            File.WriteAllBytes(Path.Combine(Directory, name + ".tar"), PackageSigning.CreateArchive(files, manifest));
            File.WriteAllText(Path.Combine(Directory, name + ".sig.json"), PackageSigning.SignatureJson(manifest, key, KeyId));
        }

        public void PackAll(string root, string[] codes, Action<string, SortedDictionary<string, byte[]>>? edit = null)
        {
            foreach (string code in codes) Pack(Path.Combine(root, code), f => edit?.Invoke(code, f));
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private static readonly string[] All = ["DK", "EU_VAT", "STD_EN16931", "STD_SAFT_OECD"];

    /// <summary>A RELEASED copy: every record VERIFIED, as a legal reviewer would leave it.</summary>
    private static void Release(string code, SortedDictionary<string, byte[]> files)
    {
        var pj = JsonNode.Parse(files["package.json"])!;
        pj["state"] = "RELEASED";
        files["package.json"] = System.Text.Encoding.UTF8.GetBytes(pj.ToJsonString());
        var ver = JsonNode.Parse(files["verification.json"])!;
        foreach (JsonNode? r in ver["records"]!.AsArray()) r!["status"] = "VERIFIED";
        files["verification.json"] = System.Text.Encoding.UTF8.GetBytes(ver.ToJsonString());
    }

    [Fact]
    public async Task ASignedArchiveIsAppliedLikeTheDirectoryAndTheRecordKeepsTheSigner()
    {
        using var archives = new Archives();
        archives.PackAll(PackagesDirectory(), All);
        var t = await TestTenant.CreateReadyAsync();
        var applier = new CountryPackageApplier(TestTenant.AdminSource);

        var r = await applier.ApplyVatFromArchivesAsync(t.TenantId, Company(t), archives.Directory, "DK", archives.Trust(), Inputs());
        Assert.Equal(("DK", "DRAFT"), (r.PackageCode, r.State));
        Assert.Equal(10, r.Items.Count(i => i.EntityType == "TAX_CODE" && i.Action == "CREATED"));
        Assert.Equal(archives.KeyId, await t.ScalarAsync<string>("select signer_key_id from core.applied_config_package where tenant_id = @t and applied_id = @a", ("a", r.AppliedId)));
        Assert.Equal(64, (await t.ScalarAsync<string>("select archive_sha256 from core.applied_config_package where tenant_id = @t and applied_id = @a", ("a", r.AppliedId))).Length);
        Assert.Equal(64, (await t.ScalarAsync<string>("select signing_manifest_sha256 from core.applied_config_package where tenant_id = @t and applied_id = @a", ("a", r.AppliedId))).Length);
        Assert.StartsWith("EU_VAT", await t.ScalarAsync<string>("select requires_snapshot from core.applied_config_package where tenant_id = @t and applied_id = @a", ("a", r.AppliedId)));
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "financecore-pkg-*"));   // the extraction is removed
    }

    [Fact]
    public async Task ATamperedArchiveAnUntrustedKeyAndAMissingRequirementAreRefusedBeforeAnythingIsWritten()
    {
        using var archives = new Archives();
        archives.PackAll(PackagesDirectory(), All);
        var t = await TestTenant.CreateReadyAsync();
        var applier = new CountryPackageApplier(TestTenant.AdminSource);
        async Task<string> Refused(TrustSet trust, string? dir = null) =>
            (await Assert.ThrowsAsync<ProvisioningException>(() => applier.ApplyVatFromArchivesAsync(t.TenantId, Company(t), dir ?? archives.Directory, "DK", trust, Inputs()))).Code;

        using var other = new Archives("dev-1");
        Assert.Equal("PACKAGE.SIGNATURE_INVALID", await Refused(other.Trust()));   // the same key id, another key

        // one byte changed in the archive of DK
        string tar = Directory.GetFiles(archives.Directory, "DK-*.tar").Single();
        byte[] bytes = File.ReadAllBytes(tar);
        bytes[2000] ^= 0xFF;
        File.WriteAllBytes(tar, bytes);
        Assert.Equal("PACKAGE.DIGEST_MISMATCH", await Refused(archives.Trust()));

        using var partial = new Archives();
        partial.Pack(Path.Combine(PackagesDirectory(), "DK"));
        Assert.Equal("PACKAGE.REQUIRES_NOT_SATISFIED", await Refused(partial.Trust(), partial.Directory));
        Assert.Equal("PACKAGE.NOT_FOUND", await Refused(archives.Trust(), Path.Combine(archives.Directory, "empty")));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.tax_code where tenant_id = @t"));
    }

    [Fact]
    public async Task AProductionTenantTakesAReleasedPackageFromASignedArchiveWithAReleaseKeyOnly()
    {
        using var archives = new Archives("rel-1");
        archives.PackAll(PackagesDirectory(), All, Release);
        var t = await TestTenant.CreateReadyAsync();
        await t.ExecuteAsync("update platform.tenant set purpose = 'PRODUCTION' where tenant_id = @t");
        var applier = new CountryPackageApplier(TestTenant.AdminSource);

        // the same key as a development key is not enough for PRODUCTION
        var dev = await Assert.ThrowsAsync<ProvisioningException>(() => applier.ApplyVatFromArchivesAsync(t.TenantId, Company(t), archives.Directory, "DK", archives.Trust(release: false), Inputs()));
        Assert.Equal("PACKAGE.SIGNATURE_INVALID", dev.Code);

        // from a directory a PRODUCTION tenant takes nothing, however released it is
        string dir = Path.Combine(archives.Directory, "dir");
        foreach (string code in All)
        {
            var files = PackageSigning.ReadDirectory(Path.Combine(PackagesDirectory(), code));
            Release(code, files);
            foreach (var (path, content) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, code, path))!);
                File.WriteAllBytes(Path.Combine(dir, code, path), content);
            }
        }
        var unsigned = await Assert.ThrowsAsync<ProvisioningException>(() => applier.ApplyVatAsync(t.TenantId, Company(t), dir, "DK", Inputs()));
        Assert.Equal("PACKAGE.SIGNATURE_INVALID", unsigned.Code);

        var r = await applier.ApplyVatFromArchivesAsync(t.TenantId, Company(t), archives.Directory, "DK", archives.Trust(release: true), Inputs());
        Assert.Equal("RELEASED", r.State);
        Assert.Equal("rel-1", await t.ScalarAsync<string>("select signer_key_id from core.applied_config_package where tenant_id = @t and applied_id = @a", ("a", r.AppliedId)));

        // a DRAFT package signed with the release key is stopped by the gate of 18, 6
        using var draft = new Archives("rel-1");
        draft.PackAll(PackagesDirectory(), All);
        var t2 = await TestTenant.CreateReadyAsync();
        await t2.ExecuteAsync("update platform.tenant set purpose = 'PRODUCTION' where tenant_id = @t");
        var gate = await Assert.ThrowsAsync<ProvisioningException>(() => applier.ApplyVatFromArchivesAsync(t2.TenantId, Company(t2), draft.Directory, "DK", draft.Trust(release: true), Inputs()));
        Assert.Equal("PACKAGE.NOT_RELEASED", gate.Code);
    }
}
