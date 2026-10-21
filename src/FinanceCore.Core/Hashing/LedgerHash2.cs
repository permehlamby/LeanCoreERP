using System.Security.Cryptography;
using System.Text;

namespace FinanceCore.Core.Hashing;

/// <summary>One line with its optional side rows, as the hash sees it (02, 9.3a).</summary>
public sealed record HashedLine(
    IReadOnlyDictionary<string, object?> Line,
    IReadOnlyDictionary<string, object?>? Tax = null,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? TaxBases = null,
    IReadOnlyDictionary<string, object?>? OpenItem = null);

/// <summary>The row hash of a journal document, hash_version 2 (02, 9.3 and 9.3a, ADR-030, ADR-211).</summary>
public static class LedgerHash2
{
    public const int Version = 2;

    /// <summary>SHA-256 over the canonical bytes of one line and its side rows. Returns 32 raw bytes.</summary>
    public static byte[] LineHash(HashedLine line)
    {
        using var buffer = new MemoryStream();
        Write(buffer, Canonical.Block(Canonical.EncodeFields(Hash2Schemas.Line, line.Line)));

        Write(buffer, line.Tax is null
            ? Canonical.NullField()
            : Canonical.Block(Canonical.EncodeFields(Hash2Schemas.Tax, line.Tax)));

        // Tax base rows of this tax line, ordered by base_line_no. An empty block when there are none.
        var bases = (line.TaxBases ?? []).OrderBy(b => Convert.ToInt64(b["base_line_no"])).ToList();
        using (var list = new MemoryStream())
        {
            foreach (var row in bases)
                Write(list, Canonical.Block(Canonical.EncodeFields(Hash2Schemas.TaxBase, row)));
            Write(buffer, Canonical.Block(list.ToArray()));
        }

        Write(buffer, line.OpenItem is null
            ? Canonical.NullField()
            : Canonical.Block(Canonical.EncodeFields(Hash2Schemas.OpenItem, line.OpenItem)));

        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>SHA-256 of the raw line hashes in line_no order. At least one line (02, 3).</summary>
    public static byte[] LineDigest(IReadOnlyList<byte[]> lineHashesInLineOrder)
    {
        if (lineHashesInLineOrder.Count == 0) throw new ArgumentException("A document has at least one line");
        using var buffer = new MemoryStream();
        foreach (byte[] h in lineHashesInLineOrder)
        {
            if (h.Length != 32) throw new ArgumentException("Line hash must be 32 bytes");
            buffer.Write(h);
        }
        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>The row hash of a document node in a chain.</summary>
    public static byte[] RowHash(
        IReadOnlyDictionary<string, object?> header, long chainSeq, byte[] prevHash, byte[] lineDigest)
    {
        if (Convert.ToInt64(header["hash_version"]) != Version)
            throw new ArgumentException($"hash_version must be {Version}");

        using var buffer = new MemoryStream();
        Write(buffer, Version);
        Write(buffer, header["tenant_id"], FieldType.Id, "tenant_id");
        Write(buffer, header["company_id"], FieldType.Id, "company_id");
        Write(buffer, header["ledger_id"], FieldType.Id, "ledger_id");
        Write(buffer, chainSeq);
        Write(buffer, Canonical.Field(Encoding.UTF8.GetBytes(Canonical.FormatHex32(prevHash))));
        Write(buffer, Canonical.Block(Canonical.EncodeFields(Hash2Schemas.Document, header)));
        Write(buffer, Canonical.Field(Encoding.UTF8.GetBytes(Canonical.FormatHex32(lineDigest))));
        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>prev_hash of chain_seq 1: SHA-256 of "genesis|tenant|company|chain_type|scope_key".</summary>
    public static byte[] Genesis(Guid tenantId, Guid companyId, string chainType, string scopeKey)
    {
        string text = string.Join('|', "genesis",
            tenantId.ToString("D").ToLowerInvariant(), companyId.ToString("D").ToLowerInvariant(), chainType, scopeKey);
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }

    private static void Write(Stream s, byte[] bytes) => s.Write(bytes);

    private static void Write(Stream s, long integer) =>
        s.Write(Canonical.Field(Encoding.UTF8.GetBytes(integer.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    private static void Write(Stream s, object? value, FieldType type, string name) =>
        s.Write(Canonical.Field(Canonical.EncodeValue(new FieldSpec(name, type), value)));
}
