using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Tests;

/// <summary>Reproduces test-vectors/period-event-hash-v2.json. The expected values come from the independent Node
/// implementation (test-vectors/verify-period-event-hash-v2.mjs), so a pass means two implementations agree (ADR-211).</summary>
public class PeriodEventHashTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-vectors", "period-event-hash-v2.json")));

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

        byte[] genesis = LedgerHash2.Genesis(tenant, company, PeriodEventHash2.ChainType, "");
        Assert.Equal(expected.GetProperty("genesis").GetString(), Convert.ToHexString(genesis).ToLowerInvariant());

        string prevText = c.GetProperty("prev_hash").GetString()!;
        byte[] prev = prevText == "GENESIS" ? genesis : Convert.FromHexString(prevText);

        var body = new Dictionary<string, object?>();
        foreach (JsonProperty p in c.GetProperty("event").EnumerateObject())
            body[p.Name] = ToClr(PeriodEventHash2.Body.Single(s => s.Name == p.Name), p.Value);

        byte[] row = PeriodEventHash2.RowHash(tenant, company, c.GetProperty("event_seq").GetInt64(), prev, body);
        Assert.Equal(expected.GetProperty("row_hash").GetString(), Convert.ToHexString(row).ToLowerInvariant());
    }
}
