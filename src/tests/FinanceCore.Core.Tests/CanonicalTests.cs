using System.Globalization;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Tests;

public class CanonicalTests
{
    [Theory]
    [InlineData("12.5", 4, "12.5000")]
    [InlineData("-100", 4, "-100.0000")]
    [InlineData("0", 4, "0.0000")]
    [InlineData("-0.0", 4, "0.0000")]
    [InlineData("0.0922", 4, "0.0922")]
    [InlineData("1", 10, "1.0000000000")]
    [InlineData("0.9216589862", 10, "0.9216589862")]
    public void FormatFixedUsesExactScale(string input, int scale, string expected) =>
        Assert.Equal(expected, Canonical.FormatFixed(decimal.Parse(input, CultureInfo.InvariantCulture), scale));

    [Fact]
    public void FormatFixedNeverRounds() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Canonical.FormatFixed(0.00005m, 4));

    [Fact]
    public void TimestampMustBeUtcAndMicrosecondPrecise()
    {
        Assert.Equal("2026-10-21T08:30:00.123456Z",
            Canonical.FormatTimestamp(new DateTime(2026, 10, 21, 8, 30, 0, DateTimeKind.Utc).AddTicks(1234560)));
        Assert.Throws<ArgumentException>(() =>
            Canonical.FormatTimestamp(new DateTime(2026, 10, 21, 8, 30, 0, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() =>
            Canonical.FormatTimestamp(new DateTime(2026, 10, 21, 8, 30, 0, DateTimeKind.Utc).AddTicks(1)));
    }

    [Fact]
    public void NullAndEmptyTextAreDifferentFields()
    {
        var spec = new FieldSpec("t", FieldType.Text, Nullable: true);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, Canonical.Field(Canonical.EncodeValue(spec, null)));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Canonical.Field(Canonical.EncodeValue(spec, "")));
    }

    [Fact]
    public void FieldFramingPreventsConcatenationAmbiguity()
    {
        var spec = new FieldSpec("t", FieldType.Text);
        byte[] abThenC = [.. Canonical.Field(Canonical.EncodeValue(spec, "ab")), .. Canonical.Field(Canonical.EncodeValue(spec, "c"))];
        byte[] aThenBc = [.. Canonical.Field(Canonical.EncodeValue(spec, "a")), .. Canonical.Field(Canonical.EncodeValue(spec, "bc"))];
        Assert.NotEqual(abThenC, aThenBc);
    }

    [Fact]
    public void NonNullableColumnRejectsNull() =>
        Assert.Throws<ArgumentException>(() => Canonical.EncodeValue(new FieldSpec("x", FieldType.Id), null));

    [Fact]
    public void WrongClrTypeIsRejected() =>
        Assert.Throws<ArgumentException>(() => Canonical.EncodeValue(new FieldSpec("x", FieldType.Amount), "12.00"));

    [Fact]
    public void UnknownOrMissingColumnsAreRejected()
    {
        var schema = new[] { new FieldSpec("a", FieldType.Text) };
        Assert.Throws<ArgumentException>(() => Canonical.EncodeFields(schema, new Dictionary<string, object?> { ["b"] = "x" }));
        Assert.Throws<ArgumentException>(() => Canonical.EncodeFields(schema, new Dictionary<string, object?>()));
    }
}
