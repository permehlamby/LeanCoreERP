using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinanceCore.Core.Masters;

namespace FinanceCore.Api.Http;

/// <summary>
/// The field maps of the master commands (07, 11.1: a patch of named fields) come from JSON as plain values. A field the core knows is read as its kind:
/// an id as a Guid, an amount or a percentage as a decimal, a whole number as a long, a flag as a bool. A value that does not fit stays as it is, so the
/// command answers with its own reason for that field. An unknown field is kept as the JSON value it is and the command refuses it by name.
/// </summary>
public sealed class MasterFieldsConverter : JsonConverter<IReadOnlyDictionary<string, object?>>
{
    private static readonly Dictionary<string, MasterKind> Kinds = MasterFields.Partner
        .Concat(MasterFields.Pii).Concat(MasterFields.Relation).Concat(MasterFields.PaymentTerms).Concat(DimensionFields.Type).Concat(DimensionFields.Value)
        .GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Kind);

    public override IReadOnlyDictionary<string, object?> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("a set of fields is an object");
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            if (fields.ContainsKey(name)) throw new JsonException($"the field '{name}' occurs twice");
            fields[name] = Value(ref reader, Kinds.TryGetValue(name, out MasterKind kind) ? kind : null);
        }
        return fields;
    }

    private static object? Value(ref Utf8JsonReader r, MasterKind? kind)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Null: return null;
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.String:
                string s = r.GetString()!;
                return kind switch
                {
                    MasterKind.Id when Guid.TryParse(s, out Guid g) => g,
                    MasterKind.Amount or MasterKind.Percent when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) => d,
                    _ => s
                };
            case JsonTokenType.Number:
                if (kind is MasterKind.Amount or MasterKind.Percent) return r.GetDecimal();
                if (r.TryGetInt64(out long l)) return l;   // two separate returns: a conditional of long and decimal would make both a decimal
                return r.GetDecimal();
            default:
                r.Skip();
                return "<structure>";   // an object or an array is never a valid value of a master field: the command refuses it as text
        }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, v) in value)
        {
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, v, options);
        }
        writer.WriteEndObject();
    }
}
