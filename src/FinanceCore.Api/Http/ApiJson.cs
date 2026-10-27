using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinanceCore.Api.Http;

/// <summary>
/// The JSON of the API (20, 7): snake_case names as in the contracts, no unknown fields, no silent defaults for a missing or null field, exact
/// decimals as strings (a number is accepted on input), plain ISO dates and hashes as lower-case hex.
/// </summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.Strict
        };
        o.Converters.Add(new DecimalConverter());
        o.Converters.Add(new NullableDecimalConverter());
        o.Converters.Add(new HexBytesConverter());
        return o;
    }

    private sealed class DecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType switch
        {
            JsonTokenType.Number => r.GetDecimal(),
            JsonTokenType.String when decimal.TryParse(r.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) => v,
            _ => throw new JsonException("a decimal is a number or a string of digits")
        };

        public override void Write(Utf8JsonWriter w, decimal v, JsonSerializerOptions o) => w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class NullableDecimalConverter : JsonConverter<decimal?>
    {
        private readonly DecimalConverter _inner = new();

        public override decimal? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
            r.TokenType == JsonTokenType.Null ? null : _inner.Read(ref r, typeof(decimal), o);

        public override void Write(Utf8JsonWriter w, decimal? v, JsonSerializerOptions o)
        {
            if (v is null) w.WriteNullValue(); else _inner.Write(w, v.Value, o);
        }
    }

    private sealed class HexBytesConverter : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
            r.TokenType == JsonTokenType.String ? Convert.FromHexString(r.GetString()!) : throw new JsonException("a hash is a hex string");

        public override void Write(Utf8JsonWriter w, byte[] v, JsonSerializerOptions o) => w.WriteStringValue(Convert.ToHexString(v).ToLowerInvariant());
    }
}
