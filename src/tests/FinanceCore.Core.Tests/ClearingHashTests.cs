using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Tests;

/// <summary>Reproduces test-vectors/clearing-hash-v2.json. The expected values come from the independent Node
/// implementation (test-vectors/verify-clearing-hash-v2.mjs), so a pass means two implementations agree (ADR-211).</summary>
public class ClearingHashTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-vectors", "clearing-hash-v2.json")));

    public static IEnumerable<object[]> CaseNames() =>
        Vectors.RootElement.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()! });

    private static object? ToClr(FieldSpec spec, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null) return null;
        return spec.Type switch
        {
            FieldType.Id => Guid.Parse(e.GetString()!),
            FieldType.Text => e.GetString()!,
            FieldType.Int => e.GetInt64(),
            FieldType.Date => DateOnly.ParseExact(e.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Amount => decimal.Parse(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText(), CultureInfo.InvariantCulture),
            FieldType.Timestamp => DateTime.SpecifyKind(
                DateTime.ParseExact(e.GetString()!, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc),
            _ => throw new NotSupportedException()
        };
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void CSharpReproducesVector(string name)
    {
        JsonElement c = Vectors.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        Guid tenant = Guid.Parse(c.GetProperty("tenant_id").GetString()!);
        Guid company = Guid.Parse(c.GetProperty("company_id").GetString()!);
        JsonElement expected = c.GetProperty("expected");

        byte[] genesis = LedgerHash2.Genesis(tenant, company, ClearingHash2.ChainType, "");
        Assert.Equal(expected.GetProperty("genesis").GetString(), Convert.ToHexString(genesis).ToLowerInvariant());

        string prevText = c.GetProperty("prev_hash").GetString()!;
        byte[] prev = prevText == "GENESIS" ? genesis : Convert.FromHexString(prevText);

        var body = new Dictionary<string, object?>();
        foreach (JsonProperty p in c.GetProperty("clearing").EnumerateObject())
            body[p.Name] = ToClr(ClearingHash2.Body.Single(s => s.Name == p.Name), p.Value);
        Guid clearingId = (Guid)body["clearing_id"]!;

        // the items in item_no order, with the outer columns the item block repeats
        var items = c.GetProperty("items").EnumerateArray().Select(i =>
        {
            var d = new Dictionary<string, object?> { ["tenant_id"] = tenant, ["company_id"] = company, ["clearing_id"] = clearingId };
            foreach (JsonProperty p in i.EnumerateObject())
                d[p.Name] = ToClr(ClearingHash2.Item.Single(s => s.Name == p.Name), p.Value);
            return (IReadOnlyDictionary<string, object?>)d;
        }).OrderBy(d => (long)d["item_no"]!).ToList();

        byte[] digest = ClearingHash2.ItemsDigest(items);
        Assert.Equal(expected.GetProperty("items_digest").GetString(), Convert.ToHexString(digest).ToLowerInvariant());

        string? linked = c.GetProperty("document_row_hash").GetString();
        byte[] row = ClearingHash2.RowHash(tenant, company, c.GetProperty("chain_seq").GetInt64(), prev, body, digest,
            linked is null ? null : Convert.FromHexString(linked));
        Assert.Equal(expected.GetProperty("row_hash").GetString(), Convert.ToHexString(row).ToLowerInvariant());
    }

    [Fact]
    public void ALinkedDocumentAndNoDocumentGiveDifferentHashes()
    {
        JsonElement c = Vectors.RootElement.GetProperty("cases").EnumerateArray().First();
        Guid tenant = Guid.Parse(c.GetProperty("tenant_id").GetString()!), company = Guid.Parse(c.GetProperty("company_id").GetString()!);
        byte[] genesis = LedgerHash2.Genesis(tenant, company, ClearingHash2.ChainType, "");
        var body = c.GetProperty("clearing").EnumerateObject().ToDictionary(p => p.Name, p => ToClr(ClearingHash2.Body.Single(s => s.Name == p.Name), p.Value));
        byte[] digest = new byte[32];
        byte[] without = ClearingHash2.RowHash(tenant, company, 1, genesis, body, digest, null);
        byte[] with = ClearingHash2.RowHash(tenant, company, 1, genesis, body, digest, new byte[32]);
        Assert.NotEqual(Convert.ToHexString(without), Convert.ToHexString(with));
    }

    [Fact]
    public void AClearingWithoutItemsHasNoDigest() =>
        Assert.Throws<ArgumentException>(() => ClearingHash2.ItemsDigest([]));
}
