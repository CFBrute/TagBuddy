using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Parsing the password the person types for a protected tag. A half-typed password must never parse,
/// since a wrong one can count against a tag's lockout limit.</summary>
public class TagPasswordTests
{
    [Theory]
    [InlineData("FFFFFFFF")]
    [InlineData("ffffffff")]
    [InlineData("FF FF FF FF")]
    [InlineData("FF:FF:FF:FF")]
    [InlineData("FF-FF-FF-FF")]
    public void Eight_hex_digits_parse_to_four_bytes(string text)
    {
        Assert.True(TagPassword.TryParse(text, out var pwd));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, pwd);
    }

    [Fact]
    public void The_bytes_are_in_order()
    {
        Assert.True(TagPassword.TryParse("0A1B2C3D", out var pwd));
        Assert.Equal(new byte[] { 0x0A, 0x1B, 0x2C, 0x3D }, pwd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FFFF")]            // too short
    [InlineData("FFFFFFFFFF")]      // too long
    [InlineData("FFFFFFFG")]        // not hex
    [InlineData("the cat sat")]
    public void Anything_that_is_not_four_bytes_is_rejected(string? text)
    {
        Assert.False(TagPassword.TryParse(text, out var pwd));
        Assert.Empty(pwd);
    }

    [Fact]
    public void Format_shows_the_password_back()
    {
        Assert.Equal("FF:FF:FF:FF", TagPassword.Format(TagPassword.FactoryDefault));
        Assert.Equal("", TagPassword.Format(null));
        Assert.Equal("", TagPassword.Format([0x01, 0x02]));
    }
}
