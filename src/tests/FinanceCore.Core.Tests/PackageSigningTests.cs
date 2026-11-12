using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FinanceCore.Core.Packages;

namespace FinanceCore.Core.Tests;

/// <summary>The signed archive of a package (ADR-217, 20, 11): the manifest, the deterministic archive, the signature, the trust sets and the order of the checks.</summary>
public class PackageSigningTests
{
    private static readonly DateOnly Today = new(2026, 11, 12);

    private sealed record Key(string KeyId, string Private, string Public);

    private static Key NewKey(string id)
    {
        var (priv, pub) = PackageSigning.GenerateKey();
        return new Key(id, priv, pub);
    }

    private static TrustSet Trust(Key? dev = null, Key? release = null, string[]? revoked = null, DateOnly? validTo = null)
    {
        var keys = new List<TrustedKey>();
        if (dev is not null) keys.Add(new TrustedKey(dev.KeyId, dev.Public, false));
        if (release is not null) keys.Add(new TrustedKey(release.KeyId, release.Public, true, ValidTo: validTo));
        return new TrustSet(keys, (revoked ?? []).ToHashSet());
    }

    private static SortedDictionary<string, byte[]> Files(string state = "DRAFT", string format = "1.0", string verified = "UNVERIFIED")
    {
        static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
        return new(StringComparer.Ordinal)
        {
            ["package.json"] = B($$"""{ "format_version": "{{format}}", "package_code": "XX", "package_version": "1.2.3", "state": "{{state}}" }"""),
            ["verification.json"] = B($$"""{ "records": [ { "item_ids": ["XX-1"], "status": "{{verified}}" } ] }"""),
            ["core/tax_code.json"] = B("""{ "items": [] }"""),
            ["app/readme.txt"] = B("opaque")
        };
    }

    private static (byte[] Archive, string Signature) Sign(IReadOnlyDictionary<string, byte[]> files, Key key, Func<byte[], byte[]>? alterManifest = null)
    {
        byte[] manifest = PackageSigning.BuildManifest(files);
        if (alterManifest is not null) manifest = alterManifest(manifest);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(key.Private);
        return (PackageSigning.CreateArchive(files, manifest), PackageSigning.SignatureJson(manifest, ecdsa, key.KeyId));
    }

    private static string Code(Action act) => Assert.Throws<PackageException>(act).Code;

    [Fact]
    public void ASignedArchiveIsVerifiedAndGivesItsDigests()
    {
        var dev = NewKey("dev-1");
        var (archive, sig) = Sign(Files(), dev);
        var v = PackageSigning.Verify(archive, sig, Trust(dev), "TEST", Today);
        Assert.Equal(("XX", "1.2.3", "DRAFT", "dev-1"), (v.PackageCode, v.PackageVersion, v.State, v.SignerKeyId));
        Assert.Equal(64, v.ArchiveSha256.Length);
        Assert.Equal(4, v.Files.Count);
        Assert.Equal("opaque", Encoding.UTF8.GetString(v.Files["app/readme.txt"]));
        Assert.DoesNotContain(PackageSigning.ManifestName, v.Files.Keys);
    }

    [Fact]
    public void TheArchiveAndTheManifestAreTheSameBytesEveryTime()
    {
        var files = Files();
        byte[] m1 = PackageSigning.BuildManifest(files), m2 = PackageSigning.BuildManifest(files);
        Assert.Equal(m1, m2);
        Assert.Equal(PackageSigning.CreateArchive(files, m1), PackageSigning.CreateArchive(files, m2));
        var reordered = new Dictionary<string, byte[]>(files.Reverse());
        Assert.Equal(PackageSigning.CreateArchive(files, m1), PackageSigning.CreateArchive(reordered, PackageSigning.BuildManifest(reordered)));
    }

    [Fact]
    public void ASignatureOfAnotherKeyOrOfAnotherManifestIsRefused()
    {
        var dev = NewKey("dev-1"); var other = NewKey("dev-1");   // same id, different key pair
        var (archive, sig) = Sign(Files(), other);
        Assert.Equal("PACKAGE.SIGNATURE_INVALID", Code(() => PackageSigning.Verify(archive, sig, Trust(dev), "TEST", Today)));
        var (a2, s2) = Sign(Files(), dev);
        var (a3, _) = Sign(Files(state: "RELEASED", verified: "VERIFIED"), dev);
        Assert.Equal("PACKAGE.SIGNATURE_INVALID", Code(() => PackageSigning.Verify(a3, s2, Trust(dev), "TEST", Today)));   // the signature belongs to the other manifest
        Assert.Equal("PACKAGE.SIGNATURE_INVALID", Code(() => PackageSigning.Verify(a2, "not json", Trust(dev), "TEST", Today)));
        Assert.Equal("PACKAGE.SIGNATURE_INVALID", Code(() => PackageSigning.Verify(a2, s2, Trust(), "TEST", Today)));   // no key in the trust set
    }

    [Fact]
    public void TheTrustSetsFollowThePurposeTheRevocationsAndTheValidity()
    {
        var dev = NewKey("dev-1"); var rel = NewKey("rel-1");
        var released = Files(state: "RELEASED", verified: "VERIFIED");
        var (devArchive, devSig) = Sign(released, dev);
        var (relArchive, relSig) = Sign(released, rel);
        var trust = Trust(dev, rel);

        PackageSigning.Verify(devArchive, devSig, trust, "TEST", Today);
        PackageSigning.Verify(devArchive, devSig, trust, "REHEARSAL", Today);
        PackageSigning.Verify(relArchive, relSig, trust, "PRODUCTION", Today);
        PackageSigning.Verify(relArchive, relSig, trust, "TEST", Today);   // the development set accepts the release key too
        Assert.Equal("PACKAGE.SIGNATURE_INVALID", Code(() => PackageSigning.Verify(devArchive, devSig, trust, "PRODUCTION", Today)));
        Assert.Equal("PACKAGE.SIGNATURE_INVALID", Code(() => PackageSigning.Verify(relArchive, relSig, Trust(dev, rel, revoked: ["rel-1"]), "PRODUCTION", Today)));
        Assert.Equal("PACKAGE.SIGNATURE_INVALID", Code(() => PackageSigning.Verify(relArchive, relSig, Trust(dev, rel, validTo: Today.AddDays(-1)), "PRODUCTION", Today)));
    }

    [Fact]
    public void AFileThatDiffersOrIsMissingOrIsExtraIsADigestMismatch()
    {
        var dev = NewKey("dev-1");
        var files = Files();
        byte[] manifest = PackageSigning.BuildManifest(files);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(dev.Private);
        string sig = PackageSigning.SignatureJson(manifest, ecdsa, dev.KeyId);

        var changed = Files(); changed["core/tax_code.json"] = Encoding.UTF8.GetBytes("""{ "items": [1] }""");
        Assert.Equal("PACKAGE.DIGEST_MISMATCH", Code(() => PackageSigning.Verify(PackageSigning.CreateArchive(changed, manifest), sig, Trust(dev), "TEST", Today)));
        var missing = Files(); missing.Remove("app/readme.txt");
        Assert.Equal("PACKAGE.DIGEST_MISMATCH", Code(() => PackageSigning.Verify(PackageSigning.CreateArchive(missing, manifest), sig, Trust(dev), "TEST", Today)));
        var extra = Files(); extra["core/extra.json"] = [1];
        Assert.Equal("PACKAGE.DIGEST_MISMATCH", Code(() => PackageSigning.Verify(PackageSigning.CreateArchive(extra, manifest), sig, Trust(dev), "TEST", Today)));
        Assert.Equal("PACKAGE.DIGEST_MISMATCH", Code(() => PackageSigning.Verify([1, 2, 3], sig, Trust(dev), "TEST", Today)));
    }

    [Fact]
    public void TheCopiedFieldsAndTheStateMustAgreeWithTheAuthoredFiles()
    {
        var dev = NewKey("dev-1");
        static byte[] Edit(byte[] manifest, string field, string value)
        {
            JsonNode node = JsonNode.Parse(manifest)!;
            node[field] = value;
            return Encoding.UTF8.GetBytes(node.ToJsonString());
        }
        var (a1, s1) = Sign(Files(), dev, m => Edit(m, "state", "RELEASED"));
        Assert.Equal("PACKAGE.MANIFEST_MISMATCH", Code(() => PackageSigning.Verify(a1, s1, Trust(dev), "TEST", Today)));
        var (a2, s2) = Sign(Files(), dev, m => Edit(m, "package_version", "9.9.9"));
        Assert.Equal("PACKAGE.MANIFEST_MISMATCH", Code(() => PackageSigning.Verify(a2, s2, Trust(dev), "TEST", Today)));
        // RELEASED in package.json with an UNVERIFIED record in verification.json
        var (a3, s3) = Sign(Files(state: "RELEASED", verified: "UNVERIFIED"), dev);
        Assert.Equal("PACKAGE.MANIFEST_MISMATCH", Code(() => PackageSigning.Verify(a3, s3, Trust(dev), "TEST", Today)));
    }

    [Fact]
    public void AnUnknownFormatMajorAndTheProductionGateAreChecked()
    {
        var dev = NewKey("dev-1"); var rel = NewKey("rel-1");
        var (a1, s1) = Sign(Files(format: "2.0"), dev);
        Assert.Equal("PACKAGE.UNKNOWN_FORMAT_VERSION", Code(() => PackageSigning.Verify(a1, s1, Trust(dev), "TEST", Today)));
        PackageSigning.Verify(Sign(Files(format: "1.7"), dev).Archive, Sign(Files(format: "1.7"), dev).Signature, Trust(dev), "TEST", Today);

        // a DRAFT package signed with a release key: the gate of 18, 6 still refuses it for a PRODUCTION tenant
        var (a2, s2) = Sign(Files(), rel);
        Assert.Equal("PACKAGE.NOT_RELEASED", Code(() => PackageSigning.Verify(a2, s2, Trust(release: rel), "PRODUCTION", Today)));
        PackageSigning.Verify(a2, s2, Trust(release: rel), "TEST", Today);
    }

    [Fact]
    public void ATrustSetIsReadFromJson()
    {
        var rel = NewKey("rel-1");
        string json = new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject { ["key_id"] = "rel-1", ["role"] = "release", ["public_key_pem"] = rel.Public, ["valid_from"] = "2026-01-01", ["valid_to"] = "2027-01-01" }),
            ["revoked"] = new JsonArray("old-key")
        }.ToJsonString();
        var trust = TrustSet.Parse(json);
        Assert.NotNull(trust.Find("rel-1", "PRODUCTION", Today));
        Assert.Null(trust.Find("rel-1", "PRODUCTION", new DateOnly(2027, 6, 1)));
        Assert.Null(trust.Find("rel-1", "PRODUCTION", new DateOnly(2025, 6, 1)));
        Assert.Null(trust.Find("old-key", "TEST", Today));
        Assert.Throws<PackageException>(() => TrustSet.Parse("""{ "keys": [ { "key_id": "x", "role": "root", "public_key_pem": "" } ] }"""));
    }
}
