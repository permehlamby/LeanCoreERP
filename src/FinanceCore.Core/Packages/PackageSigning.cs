using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FinanceCore.Core.Packages;

/// <summary>A refusal of the package gate, with a stable code of the area PACKAGE (18, 6, 7.2, 20, 11).</summary>
public sealed class PackageException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A key of a trust set (20, 11): an id, its public key, a validity period and whether it is a release key.</summary>
public sealed record TrustedKey(string KeyId, string PublicKeyPem, bool IsRelease, DateOnly? ValidFrom = null, DateOnly? ValidTo = null);

/// <summary>
/// The two trust sets (ADR-217, 20, 11). A PRODUCTION tenant accepts the release keys only, TEST and REHEARSAL tenants the development and the release keys. A revoked key is
/// accepted nowhere. The file is part of a core release: a rotation adds a key before its first signature, a revocation removes it.
/// </summary>
public sealed class TrustSet(IReadOnlyList<TrustedKey> keys, IReadOnlySet<string> revoked)
{
    public IReadOnlyList<TrustedKey> Keys { get; } = keys;

    public static TrustSet Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var keys = new List<TrustedKey>();
        foreach (JsonElement k in doc.RootElement.GetProperty("keys").EnumerateArray())
        {
            string role = k.GetProperty("role").GetString()!;
            if (role is not ("release" or "development")) throw new PackageException("PACKAGE.SIGNATURE_INVALID", $"A trust set key has the role {role}");
            DateOnly? Date(string name) => k.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? DateOnly.Parse(v.GetString()!) : null;
            keys.Add(new TrustedKey(k.GetProperty("key_id").GetString()!, k.GetProperty("public_key_pem").GetString()!, role == "release", Date("valid_from"), Date("valid_to")));
        }
        var revoked = doc.RootElement.TryGetProperty("revoked", out JsonElement r) ? r.EnumerateArray().Select(e => e.GetString()!).ToHashSet() : [];
        return new TrustSet(keys, revoked);
    }

    /// <summary>The key of the id if the tenant purpose accepts it on the date.</summary>
    public TrustedKey? Find(string keyId, string purpose, DateOnly today)
    {
        TrustedKey? key = Keys.FirstOrDefault(k => k.KeyId == keyId);
        if (key is null || revoked.Contains(keyId)) return null;
        if (purpose == "PRODUCTION" && !key.IsRelease) return null;
        if (key.ValidFrom > today || key.ValidTo < today) return null;
        return key;
    }
}

/// <summary>What a verified archive holds: the package, the key that signed it, the digests that the apply record keeps, and the files.</summary>
public sealed record VerifiedPackage(
    string PackageCode, string PackageVersion, string State, string FormatVersion, string SignerKeyId, string ArchiveSha256, string ManifestSha256,
    IReadOnlyDictionary<string, byte[]> Files);

/// <summary>
/// The archive of a package and its detached signature (ADR-217, 20, 11): a deterministic tar (sorted entries, fixed times, no owner) of the package directory plus a generated
/// signing-manifest.json with the SHA-256 of every file, and a signature of ECDSA P-256 with SHA-256 over the bytes of that manifest. The core receives the archive and never fetches it.
/// </summary>
public static class PackageSigning
{
    public const string ManifestName = "signing-manifest.json";
    public const string Algorithm = "ECDSA-P256-SHA256";
    public const long MaxArchiveBytes = 20L * 1024 * 1024;
    private const string SupportedMajor = "1";

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>The files of a package directory by relative path with forward slashes, in ordinal order. The signing manifest is not among them: it is generated.</summary>
    public static SortedDictionary<string, byte[]> ReadDirectory(string dir)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            if (rel == ManifestName) continue;
            files[rel] = File.ReadAllBytes(f);
        }
        return files;
    }

    /// <summary>The signing manifest of the files: the copied fields of package.json and the digest of every file. Same input, same bytes.</summary>
    public static byte[] BuildManifest(IReadOnlyDictionary<string, byte[]> files)
    {
        if (!files.TryGetValue("package.json", out byte[]? pj)) throw new PackageException("PACKAGE.NOT_FOUND", "The directory has no package.json");
        using JsonDocument package = JsonDocument.Parse(pj);
        string Field(string name) => package.RootElement.TryGetProperty(name, out JsonElement v) ? v.GetString()! : throw new PackageException("PACKAGE.MANIFEST_MISMATCH", $"package.json has no {name}");
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("package_code", Field("package_code"));
            w.WriteString("package_version", Field("package_version"));
            w.WriteString("format_version", Field("format_version"));
            w.WriteString("state", Field("state"));
            w.WriteStartArray("files");
            foreach (var (path, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("path", path);
                w.WriteString("sha256", Hex(SHA256.HashData(bytes)));
                w.WriteNumber("size", bytes.Length);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>The deterministic archive: the files and the manifest, sorted, epoch times, no owner, mode 0644.</summary>
    public static byte[] CreateArchive(IReadOnlyDictionary<string, byte[]> files, byte[] manifest)
    {
        var all = new SortedDictionary<string, byte[]>(files.ToDictionary(f => f.Key, f => f.Value), StringComparer.Ordinal) { [ManifestName] = manifest };
        using var ms = new MemoryStream();
        using (var tar = new TarWriter(ms, TarEntryFormat.Ustar, leaveOpen: true))
            foreach (var (path, bytes) in all)
                tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(bytes), ModificationTime = DateTimeOffset.UnixEpoch, Uid = 0, Gid = 0, UserName = "", GroupName = "",
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                });
        return ms.ToArray();
    }

    public static string SignatureJson(byte[] manifest, ECDsa privateKey, string keyId)
    {
        byte[] sig = privateKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.Serialize(new Dictionary<string, string> { ["key_id"] = keyId, ["algorithm"] = Algorithm, ["signature"] = Convert.ToBase64String(sig) },
            new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>A new P-256 key pair as PEM (for development keys: a release key lives in a KMS or an HSM and is never exported).</summary>
    public static (string PrivatePem, string PublicPem) GenerateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    private static Dictionary<string, byte[]> ReadArchive(byte[] archive)
    {
        if (archive.Length > MaxArchiveBytes) throw new PackageException("PACKAGE.DIGEST_MISMATCH", "The archive is larger than 20 MB");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var reader = new TarReader(new MemoryStream(archive));
            long total = 0;
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType != TarEntryType.RegularFile) throw new PackageException("PACKAGE.DIGEST_MISMATCH", $"The archive holds an entry that is not a file: {entry.Name}");
                string name = entry.Name;
                if (name.StartsWith('/') || name.Contains("..", StringComparison.Ordinal) || name.Contains('\\'))
                    throw new PackageException("PACKAGE.DIGEST_MISMATCH", $"The archive holds a path outside the package: {name}");
                using var data = new MemoryStream();
                entry.DataStream?.CopyTo(data);
                total += data.Length;
                if (total > MaxArchiveBytes) throw new PackageException("PACKAGE.DIGEST_MISMATCH", "The archive expands to more than 20 MB");
                if (!files.TryAdd(name, data.ToArray())) throw new PackageException("PACKAGE.DIGEST_MISMATCH", $"The archive holds {name} twice");
            }
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException or NotSupportedException or ArgumentException)
        {
            throw new PackageException("PACKAGE.DIGEST_MISMATCH", "The archive is not a tar file: " + e.Message);
        }
        return files;
    }

    /// <summary>
    /// The verification of 20, 11 in its fixed order: the signature against the trust set of the tenant purpose, the SHA-256 of every file against the signing manifest
    /// (PACKAGE.DIGEST_MISMATCH), the copied fields against package.json and the state against verification.json (PACKAGE.MANIFEST_MISMATCH), the format version
    /// (PACKAGE.UNKNOWN_FORMAT_VERSION), then the gate of 18, 6 (PACKAGE.NOT_RELEASED) for a PRODUCTION tenant. Nothing is written.
    /// </summary>
    public static VerifiedPackage Verify(byte[] archive, string signatureJson, TrustSet trust, string purpose, DateOnly today)
    {
        Dictionary<string, byte[]> files = ReadArchive(archive);
        if (!files.TryGetValue(ManifestName, out byte[]? manifest))
            throw new PackageException("PACKAGE.SIGNATURE_INVALID", "The archive has no signing manifest");

        // 1. the signature
        string keyId, signature;
        try
        {
            using JsonDocument sig = JsonDocument.Parse(signatureJson);
            if (sig.RootElement.GetProperty("algorithm").GetString() != Algorithm) throw new PackageException("PACKAGE.SIGNATURE_INVALID", "The signature is not ECDSA P-256 with SHA-256");
            keyId = sig.RootElement.GetProperty("key_id").GetString()!;
            signature = sig.RootElement.GetProperty("signature").GetString()!;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new PackageException("PACKAGE.SIGNATURE_INVALID", "The signature file is not readable");
        }
        TrustedKey key = trust.Find(keyId, purpose, today)
            ?? throw new PackageException("PACKAGE.SIGNATURE_INVALID", $"The key {keyId} is not in the trust set of a {purpose} tenant, is revoked or is not valid today");
        bool valid;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            valid = ecdsa.KeySize == 256 && ecdsa.VerifyData(manifest, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            valid = false;
        }
        if (!valid) throw new PackageException("PACKAGE.SIGNATURE_INVALID", "The signature does not match the signing manifest");

        // 2. the digest of every file
        using JsonDocument m = JsonDocument.Parse(manifest);
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement f in m.RootElement.GetProperty("files").EnumerateArray()) listed[f.GetProperty("path").GetString()!] = f.GetProperty("sha256").GetString()!;
        foreach (var (path, sha) in listed)
        {
            if (!files.TryGetValue(path, out byte[]? bytes)) throw new PackageException("PACKAGE.DIGEST_MISMATCH", $"{path} is in the signing manifest and not in the archive");
            if (Hex(SHA256.HashData(bytes)) != sha) throw new PackageException("PACKAGE.DIGEST_MISMATCH", $"{path} does not match its digest");
        }
        foreach (string path in files.Keys.Where(p => p != ManifestName && !listed.ContainsKey(p)))
            throw new PackageException("PACKAGE.DIGEST_MISMATCH", $"{path} is in the archive and not in the signing manifest");

        // 3. the copied fields, and the state against the verification records
        if (!files.TryGetValue("package.json", out byte[]? pj)) throw new PackageException("PACKAGE.MANIFEST_MISMATCH", "The archive has no package.json");
        using JsonDocument package = JsonDocument.Parse(pj);
        string Copied(string name) => m.RootElement.GetProperty(name).GetString()!;
        foreach (string name in new[] { "package_code", "package_version", "format_version", "state" })
            if (package.RootElement.TryGetProperty(name, out JsonElement v) ? v.GetString() != Copied(name) : true)
                throw new PackageException("PACKAGE.MANIFEST_MISMATCH", $"{name} of the signing manifest differs from package.json");
        string state = Copied("state");
        if (state == "RELEASED")
        {
            if (!files.TryGetValue("verification.json", out byte[]? vj)) throw new PackageException("PACKAGE.NOT_RELEASED", "A RELEASED package has a verification.json");
            using JsonDocument ver = JsonDocument.Parse(vj);
            if (ver.RootElement.GetProperty("records").EnumerateArray().Any(r => r.GetProperty("status").GetString() == "UNVERIFIED"))
                throw new PackageException("PACKAGE.MANIFEST_MISMATCH", "package.json says RELEASED and verification.json holds an UNVERIFIED record");
        }

        // 4. the format version
        string format = Copied("format_version");
        if (format.Split('.')[0] != SupportedMajor) throw new PackageException("PACKAGE.UNKNOWN_FORMAT_VERSION", $"The format version {format} is not supported");

        // 5. the gate
        if (purpose == "PRODUCTION" && state != "RELEASED")
            throw new PackageException("PACKAGE.NOT_RELEASED", $"The package {Copied("package_code")} {Copied("package_version")} is {state}: a PRODUCTION tenant accepts only RELEASED packages");

        files.Remove(ManifestName);
        return new VerifiedPackage(Copied("package_code"), Copied("package_version"), state, format, keyId, Hex(SHA256.HashData(archive)), Hex(SHA256.HashData(manifest)), files);
    }
}
