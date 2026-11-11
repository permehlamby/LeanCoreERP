using System.Text.Json;
using FinanceCore.Api.Http;

namespace FinanceCore.Api.Tests;

public class ApiJsonTests
{
    private sealed record Sample(string Name, string? Note, Guid? Partner, DateOnly? Date, IReadOnlyList<Item> Items);

    private sealed record Item(string Ref, decimal? Amount);

    private static Sample Parse(string json) => JsonSerializer.Deserialize<Sample>(json, ApiJson.Options)!;

    [Fact]
    public void ANullableFieldIsOptionalAndAFieldThatIsNotNullableIsRequired()
    {
        var s = Parse("""{"name":"x","items":[{"ref":"1"}]}""");
        Assert.Null(s.Note);
        Assert.Null(s.Partner);
        Assert.Null(s.Date);
        Assert.Null(s.Items[0].Amount);

        Assert.Throws<JsonException>(() => Parse("""{"items":[]}"""));                          // name is required
        Assert.Throws<JsonException>(() => Parse("""{"name":"x"}"""));                          // items is required
        Assert.Throws<JsonException>(() => Parse("""{"name":null,"items":[]}"""));
        Assert.Throws<JsonException>(() => Parse("""{"name":"x","items":[{"amount":"1"}]}""")); // the item's ref is required
        Assert.Throws<JsonException>(() => Parse("""{"name":"x","items":[],"other":1}"""));
        Assert.Equal("n", Parse("""{"name":"x","note":"n","items":[]}""").Note);
        Assert.Equal(12.5m, Parse("""{"name":"x","items":[{"ref":"1","amount":12.5}]}""").Items[0].Amount);
        Assert.Equal(new DateOnly(2026, 3, 15), Parse("""{"name":"x","date":"2026-03-15","items":[]}""").Date);
    }
}
