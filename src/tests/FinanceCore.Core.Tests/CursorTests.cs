using System.Text;
using FinanceCore.Core.Reads;

namespace FinanceCore.Core.Tests;

/// <summary>The cursor of the list reads is opaque and signed (20, 7): a made-up or changed cursor is refused.</summary>
public class CursorTests
{
    [Fact]
    public void ACursorRoundTripsForItsOwnKindOnly()
    {
        string cursor = ReadRules.EncodeCursor("doc", 42);
        Assert.True(ReadRules.TryDecodeCursor(cursor, "doc", out long value));
        Assert.Equal(42, value);
        Assert.False(ReadRules.TryDecodeCursor(cursor, "ledger", out _));
        Assert.True(ReadRules.TryDecodeCursor(null, "doc", out long none));
        Assert.Equal(0, none);
        Assert.True(ReadRules.TryDecodeCursor("", "doc", out _));
    }

    [Fact]
    public void ACursorThatIsChangedOrMadeUpIsRefused()
    {
        string cursor = ReadRules.EncodeCursor("doc", 42);
        string text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        string moved = Convert.ToBase64String(Encoding.UTF8.GetBytes(text.Replace("|42|", "|43|")));
        Assert.False(ReadRules.TryDecodeCursor(moved, "doc", out _));
        Assert.False(ReadRules.TryDecodeCursor(Convert.ToBase64String(Encoding.UTF8.GetBytes("v1|doc|42")), "doc", out _));   // the unsigned form of before
        Assert.False(ReadRules.TryDecodeCursor(Convert.ToBase64String(Encoding.UTF8.GetBytes("v2|doc|42|00")), "doc", out _));
        Assert.False(ReadRules.TryDecodeCursor("not base64!", "doc", out _));
        Assert.False(ReadRules.TryDecodeCursor(Convert.ToBase64String(Encoding.UTF8.GetBytes(text + "|extra")), "doc", out _));
    }

    [Fact]
    public void ACursorOfAnotherKeyIsRefused()
    {
        // the key is process-wide: a fresh random key is set again afterwards
        string before = ReadRules.EncodeCursor("doc", 7);
        try
        {
            ReadRules.UseCursorKey(Enumerable.Repeat((byte)9, 32).ToArray());
            Assert.False(ReadRules.TryDecodeCursor(before, "doc", out _));
            Assert.True(ReadRules.TryDecodeCursor(ReadRules.EncodeCursor("doc", 7), "doc", out long v));
            Assert.Equal(7, v);
        }
        finally
        {
            ReadRules.UseCursorKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        }
        Assert.Throws<ArgumentException>(() => ReadRules.UseCursorKey(new byte[31]));
    }
}
