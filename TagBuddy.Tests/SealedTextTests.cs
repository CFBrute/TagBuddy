using System.Text;
using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Private tags. A mistake here either leaks the text or locks people out of their own tags.</summary>
public class SealedTextTests
{
    // Few iterations keep the tests fast; the format stores the count, so opening works the same way.
    private const int Fast = 1_000;

    [Theory]
    [InlineData("Wi-Fi: Home5G / correct horse battery staple")]
    [InlineData("Smörgåsbord 🍞 日本語")]
    [InlineData("")]
    public void Sealed_text_opens_with_the_right_passphrase(string text)
    {
        var sealedBytes = SealedText.Seal(text, "my secret phrase", Fast);
        Assert.True(SealedText.TryOpen(sealedBytes, "my secret phrase", out var opened));
        Assert.Equal(text, opened);
    }

    [Fact]
    public void The_default_strength_opens_too()
    {
        var sealedBytes = SealedText.Seal("locker 42: 1234", "a long passphrase");
        Assert.True(SealedText.TryOpen(sealedBytes, "a long passphrase", out var opened));
        Assert.Equal("locker 42: 1234", opened);
    }

    [Theory]
    [InlineData("my secret phrasE")]
    [InlineData("my secret phras")]
    [InlineData("")]
    public void A_wrong_passphrase_does_not_open(string passphrase)
    {
        var sealedBytes = SealedText.Seal("hidden", "my secret phrase", Fast);
        Assert.False(SealedText.TryOpen(sealedBytes, passphrase, out var opened));
        Assert.Equal("", opened);
    }

    [Theory]
    [InlineData(0)]     // version
    [InlineData(4)]     // iteration count
    [InlineData(10)]    // salt
    [InlineData(25)]    // nonce
    [InlineData(35)]    // encrypted text
    [InlineData(-1)]    // authentication tag
    public void A_changed_byte_anywhere_is_detected(int index)
    {
        var sealedBytes = SealedText.Seal("hidden text", "my secret phrase", Fast);
        sealedBytes[index < 0 ? sealedBytes.Length + index : index] ^= 0x01;
        Assert.False(SealedText.TryOpen(sealedBytes, "my secret phrase", out _));
    }

    [Fact]
    public void Each_seal_is_different()
    {
        var a = SealedText.Seal("same", "same passphrase", Fast);
        var b = SealedText.Seal("same", "same passphrase", Fast);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_text_is_not_visible_in_the_bytes()
    {
        var sealedBytes = SealedText.Seal("PLAINLY VISIBLE SECRET", "my secret phrase", Fast);
        Assert.DoesNotContain("VISIBLE", Encoding.UTF8.GetString(sealedBytes));
    }

    [Fact]
    public void Size_is_the_text_plus_the_overhead()
    {
        Assert.Equal(49, SealedText.Overhead);
        Assert.Equal(5 + SealedText.Overhead, SealedText.Seal("hello", "my secret phrase", Fast).Length);
    }

    [Fact]
    public void Passphrases_are_compared_after_normalising()
    {
        // "é" as one character, and as "e" followed by a combining accent: different keyboards, same passphrase.
        var sealedBytes = SealedText.Seal("hidden", "café au lait", Fast);
        Assert.True(SealedText.TryOpen(sealedBytes, "café au lait", out _));
    }

    [Fact]
    public void Other_bytes_are_not_taken_for_a_sealed_record()
    {
        Assert.False(SealedText.LooksSealed(null));
        Assert.False(SealedText.LooksSealed(new byte[10]));
        Assert.False(SealedText.LooksSealed(Encoding.UTF8.GetBytes("just an ordinary piece of plain text, long enough")));
    }

    [Fact]
    public void A_tag_cannot_demand_endless_work()
    {
        var sealedBytes = SealedText.Seal("hidden", "my secret phrase", Fast);
        sealedBytes[1] = 0x7F;   // about two billion iterations: refused without trying
        Assert.False(SealedText.LooksSealed(sealedBytes));
        Assert.False(SealedText.TryOpen(sealedBytes, "my secret phrase", out _));
    }

    [Fact]
    public void Sealing_needs_a_passphrase() =>
        Assert.Throws<ArgumentException>(() => SealedText.Seal("text", ""));
}
