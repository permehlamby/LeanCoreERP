using System.Globalization;
using System.Text;

namespace FinanceCore.Core.Hashing;

/// <summary>Column types of the canonical form (02, 9.3a).</summary>
public enum FieldType { Id, Text, Int, Bool, Date, Timestamp, Amount, Rate, Hex32 }

/// <summary>One column of a hashed record: name, type and whether NULL is allowed.</summary>
public sealed record FieldSpec(string Name, FieldType Type, bool Nullable = false);

/// <summary>
/// Canonical byte encoding of 02, 9.3a, hash_version 2. A value is encoded by its type, a field is a
/// 4-byte big-endian length followed by the value bytes (NULL is length FFFFFFFF), a block is a field
/// that wraps the concatenated inner fields.
/// </summary>
public static class Canonical
{
    public const int AmountScale = 4;
    public const int RateScale = 10;

    /// <summary>Encodes one value. Returns null for NULL (allowed only for nullable columns).</summary>
    public static byte[]? EncodeValue(FieldSpec spec, object? value)
    {
        if (value is null)
        {
            if (!spec.Nullable) throw new ArgumentException($"Column {spec.Name} is not nullable");
            return null;
        }

        try
        {
            string text = spec.Type switch
            {
                FieldType.Id => ((Guid)value).ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant(),
                FieldType.Text => (string)value,
                FieldType.Int => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                FieldType.Bool => (bool)value ? "true" : "false",
                FieldType.Date => ((DateOnly)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                FieldType.Timestamp => FormatTimestamp((DateTime)value),
                FieldType.Amount => FormatFixed((decimal)value, AmountScale),
                FieldType.Rate => FormatFixed((decimal)value, RateScale),
                FieldType.Hex32 => FormatHex32((byte[])value),
                _ => throw new ArgumentOutOfRangeException(nameof(spec))
            };
            return Encoding.UTF8.GetBytes(text);
        }
        catch (InvalidCastException ex)
        {
            throw new ArgumentException($"Column {spec.Name} expects {spec.Type}, got {value.GetType().Name}", ex);
        }
    }

    public static string FormatFixed(decimal value, int scale)
    {
        if (Math.Round(value, scale, MidpointRounding.ToEven) != value)
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} has more than {scale} fraction digits");
        if (value == 0m) return "0." + new string('0', scale);
        return value.ToString("F" + scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    public static string FormatTimestamp(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("Timestamp must be UTC");
        if (value.Ticks % 10 != 0) throw new ArgumentException("Timestamp must not have sub-microsecond precision");
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    }

    public static string FormatHex32(byte[] value)
    {
        if (value.Length != 32) throw new ArgumentException("BINARY(32) must have 32 bytes");
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    /// <summary>Appends one field: the length prefix and the value, or the NULL marker.</summary>
    public static void WriteField(Stream output, byte[]? value)
    {
        Span<byte> length = stackalloc byte[4];
        if (value is null)
        {
            length.Fill(0xFF);
            output.Write(length);
            return;
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        output.Write(length);
        output.Write(value);
    }

    /// <summary>The concatenated fields of a record, in schema order.</summary>
    public static byte[] EncodeFields(IReadOnlyList<FieldSpec> schema, IReadOnlyDictionary<string, object?> values)
    {
        foreach (string key in values.Keys)
            if (!schema.Any(s => s.Name == key)) throw new ArgumentException($"Unknown column {key}");
        using var buffer = new MemoryStream();
        foreach (FieldSpec spec in schema)
        {
            if (!values.TryGetValue(spec.Name, out object? value))
                throw new ArgumentException($"Missing column {spec.Name}");
            WriteField(buffer, EncodeValue(spec, value));
        }
        return buffer.ToArray();
    }

    /// <summary>A block is one field whose value is the given inner bytes. Returns the wrapped field.</summary>
    public static byte[] Block(byte[] innerFields)
    {
        using var buffer = new MemoryStream();
        WriteField(buffer, innerFields);
        return buffer.ToArray();
    }

    /// <summary>The NULL field (a missing optional block).</summary>
    public static byte[] NullField()
    {
        using var buffer = new MemoryStream();
        WriteField(buffer, null);
        return buffer.ToArray();
    }

    /// <summary>One field holding a value (used for the outer row hash fields).</summary>
    public static byte[] Field(byte[]? value)
    {
        using var buffer = new MemoryStream();
        WriteField(buffer, value);
        return buffer.ToArray();
    }
}
