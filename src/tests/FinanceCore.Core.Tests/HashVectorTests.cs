using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Tests;

/// <summary>Reproduces test-vectors/hash-v2.json. The expected values were computed by the independent
/// Node implementation (test-vectors/verify-hash-v2.mjs), so a pass means two implementations agree (ADR-211).</summary>
public class HashVectorTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-vectors", "hash-v2.json")));

    public static IEnumerable<object[]> CaseNames() =>
        Vectors.RootElement.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()! });

    private static JsonElement Case(string name) =>
        Vectors.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static object? ToClr(FieldSpec spec, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null) return null;
        return spec.Type switch
        {
            FieldType.Id => Guid.Parse(e.GetString()!),
            FieldType.Text => e.GetString()!,
            FieldType.Int => e.GetInt64(),
            FieldType.Bool => e.GetBoolean(),
            FieldType.Date => DateOnly.ParseExact(e.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Timestamp => DateTime.SpecifyKind(
                DateTime.ParseExact(e.GetString()!, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc),
            FieldType.Amount or FieldType.Rate => decimal.Parse(e.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
            FieldType.Hex32 => Convert.FromHexString(e.GetString()!),
            _ => throw new NotSupportedException()
        };
    }

    private static Dictionary<string, object?> Record(IReadOnlyList<FieldSpec> schema, JsonElement e)
    {
        var d = new Dictionary<string, object?>();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            FieldSpec spec = schema.Single(s => s.Name == p.Name);
            d[p.Name] = ToClr(spec, p.Value);
        }
        return d;
    }

    private static HashedLine ToLine(JsonElement e)
    {
        var tax = e.TryGetProperty("tax", out var t) && t.ValueKind != JsonValueKind.Null ? Record(Hash2Schemas.Tax, t) : null;
        var bases = e.TryGetProperty("tax_bases", out var b)
            ? b.EnumerateArray().Select(x => (IReadOnlyDictionary<string, object?>)Record(Hash2Schemas.TaxBase, x)).ToList()
            : null;
        var oi = e.TryGetProperty("open_item", out var o) && o.ValueKind != JsonValueKind.Null ? Record(Hash2Schemas.OpenItem, o) : null;
        return new HashedLine(Record(Hash2Schemas.Line, e.GetProperty("line")), tax, bases, oi);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void CSharpReproducesVector(string name)
    {
        JsonElement c = Case(name);
        JsonElement expected = c.GetProperty("expected");
        var lines = c.GetProperty("lines").EnumerateArray().Select(ToLine)
            .OrderBy(l => Convert.ToInt64(l.Line["line_no"])).ToList();

        var lineHashes = lines.Select(LedgerHash2.LineHash).ToList();
        Assert.Equal(
            expected.GetProperty("line_hashes").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            lineHashes.Select(Hex).ToArray());

        byte[] digest = LedgerHash2.LineDigest(lineHashes);
        Assert.Equal(expected.GetProperty("line_digest").GetString(), Hex(digest));

        JsonElement chain = c.GetProperty("chain");
        byte[] genesis = LedgerHash2.Genesis(
            Guid.Parse(chain.GetProperty("tenant_id").GetString()!),
            Guid.Parse(chain.GetProperty("company_id").GetString()!),
            chain.GetProperty("chain_type").GetString()!,
            chain.GetProperty("scope_key").GetString()!);
        Assert.Equal(expected.GetProperty("genesis").GetString(), Hex(genesis));

        string prev = c.GetProperty("prev_hash").GetString()!;
        byte[] prevHash = prev == "GENESIS" ? genesis : Convert.FromHexString(prev);
        var header = Record(Hash2Schemas.Document, c.GetProperty("document"));
        byte[] row = LedgerHash2.RowHash(header, c.GetProperty("chain_seq").GetInt64(), prevHash, digest);
        Assert.Equal(expected.GetProperty("row_hash").GetString(), Hex(row));
    }
}
