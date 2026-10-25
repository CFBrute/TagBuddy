using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Reading a MIFARE Classic card with public default keys: the key list, its formatting, and the
/// summary shown for how many sectors opened.</summary>
public class MifareClassicTests
{
    [Fact]
    public void The_factory_default_key_is_first()
    {
        var (key, name) = MifareClassicKeys.Defaults[0];
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, key);
        Assert.Equal("factory default", name);
    }

    [Fact]
    public void Every_default_key_is_six_bytes()
    {
        Assert.All(MifareClassicKeys.Defaults, k => Assert.Equal(6, k.Key.Length));
        Assert.Contains(MifareClassicKeys.Defaults, k => MifareClassicKeys.KeyHex(k.Key) == "D3F7D3F7D3F7");
    }

    [Fact]
    public void KeyHex_writes_six_bytes_as_twelve_hex_digits() =>
        Assert.Equal("A0A1A2A3A4A5", MifareClassicKeys.KeyHex([0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5]));

    [Fact]
    public void A_sector_header_names_the_key_that_opened_it() =>
        Assert.Equal("Sector 00 · key A FFFFFFFFFFFF (factory default)",
            MifareClassicKeys.SectorHeader(0, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], 'A', "factory default"));

    [Fact]
    public void A_locked_sector_says_it_was_not_read() =>
        Assert.Equal("Sector 15 · locked (custom key, not read)", MifareClassicKeys.LockedSector(15));

    [Fact]
    public void A_block_is_hex_and_ascii()
    {
        byte[] data = [0x48, 0x69, 0x00, 0x7F, 0x41, 0x42, 0x43, 0x44, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal("  004  4869007F 41424344 00000000 00000000  Hi..ABCD........",
            MifareClassicKeys.FormatBlock(4, data));
    }

    [Theory]
    [InlineData("A0A1A2A3A4A5")]
    [InlineData("a0a1a2a3a4a5")]
    [InlineData("A0:A1:A2:A3:A4:A5")]
    [InlineData("A0 A1 A2 A3 A4 A5")]
    public void TryParseKey_reads_twelve_hex_digits(string text)
    {
        Assert.True(MifareClassicKeys.TryParseKey(text, out var key));
        Assert.Equal(new byte[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }, key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A0A1A2A3A4")]      // 10 digits, too short
    [InlineData("A0A1A2A3A4A5A6")]  // 14 digits, too long
    [InlineData("A0A1A2A3A4AG")]    // not hex
    public void TryParseKey_rejects_anything_but_six_bytes(string? text)
    {
        Assert.False(MifareClassicKeys.TryParseKey(text, out var key));
        Assert.Empty(key);
    }

    [Theory]
    [InlineData(0, 16, "Secure item")]
    [InlineData(3, 16, "3 of 16 sectors")]
    [InlineData(16, 16, "All 16 sectors")]
    public void The_access_summary_matches_how_many_opened(int opened, int total, string expected) =>
        Assert.Contains(expected, MifareClassicKeys.AccessSummary(opened, total));

    [Fact]
    public void Factory_access_bits_mean_key_A_does_everything()
    {
        byte[] trailer = [0, 0, 0, 0, 0, 0, 0xFF, 0x07, 0x80, 0x69, 0, 0, 0, 0, 0, 0];
        var rules = MifareClassicKeys.DescribeAccess(trailer);
        Assert.NotNull(rules);
        var line = Assert.Single(rules);
        Assert.StartsWith("keys: A changes both keys and the rules", line);
    }

    [Fact]
    public void A_typical_secured_sector_is_spelled_out()
    {
        // 78 77 88: data blocks "read A or B, write B", trailer "B changes keys and rules".
        byte[] trailer = [0, 0, 0, 0, 0, 0, 0x78, 0x77, 0x88, 0x69, 0, 0, 0, 0, 0, 0];
        var rules = MifareClassicKeys.DescribeAccess(trailer)!;
        Assert.Equal(4, rules.Count);
        Assert.Equal("block 0: read with A or B, write with B", rules[0]);
        Assert.Equal("keys: B changes both keys and the rules", rules[3]);
    }

    [Fact]
    public void Corrupt_access_bits_are_flagged()
    {
        byte[] trailer = [0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0x80, 0x69, 0, 0, 0, 0, 0, 0];
        Assert.Null(MifareClassicKeys.DescribeAccess(trailer));
        Assert.Contains("corrupt", Assert.Single(MifareClassicKeys.AccessLines(trailer)));
    }
}
