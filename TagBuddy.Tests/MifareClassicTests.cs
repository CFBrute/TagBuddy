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

    [Theory]
    [InlineData(0xFF, 0x07, 0x80)]   // transport
    [InlineData(0x78, 0x77, 0x88)]   // common secured
    [InlineData(0x7F, 0x07, 0x88)]
    public void Rules_decode_and_encode_back_to_the_same_bytes(byte b6, byte b7, byte b8)
    {
        byte[] trailer = [0, 0, 0, 0, 0, 0, b6, b7, b8, 0x69, 0, 0, 0, 0, 0, 0];
        var rules = MifareClassicKeys.DecodeRules(trailer);
        Assert.NotNull(rules);
        Assert.Equal(new[] { b6, b7, b8 }, rules.Encode());
    }

    [Fact]
    public void Transport_rules_are_the_factory_bytes_and_not_permanent()
    {
        Assert.Equal(MifareClassicKeys.TransportAccessBits, SectorRules.Transport.Encode());
        Assert.False(SectorRules.Transport.IsPermanent);
        Assert.True((SectorRules.Transport with { Trailer = 0b010 }).IsPermanent);
    }

    [Fact]
    public void Every_rule_encodes_with_matching_inverted_halves()
    {
        foreach (var d in MifareClassicKeys.DataRules)
            foreach (var t in MifareClassicKeys.TrailerRules)
            {
                var rules = new SectorRules(d.Bits, 0, d.Bits, t.Bits);
                byte[] trailer = [.. new byte[6], .. rules.Encode(), 0x69, .. new byte[6]];
                Assert.Equal(rules, MifareClassicKeys.DecodeRules(trailer));
            }
    }

    [Fact]
    public void A_trailer_is_built_from_keys_and_either_new_or_kept_rules()
    {
        byte[] a = [1, 1, 1, 1, 1, 1], b = [2, 2, 2, 2, 2, 2];
        byte[] current = [0, 0, 0, 0, 0, 0, 0x78, 0x77, 0x88, 0x42, 0, 0, 0, 0, 0, 0];
        var kept = MifareClassicKeys.TrailerFor(a, b, current, null);
        Assert.Equal(new byte[] { 0x78, 0x77, 0x88, 0x42 }, kept[6..10]);
        Assert.Equal(a, kept[..6]);
        Assert.Equal(b, kept[10..]);
        var changed = MifareClassicKeys.TrailerFor(a, b, current, SectorRules.Transport);
        Assert.Equal(new byte[] { 0xFF, 0x07, 0x80, 0x42 }, changed[6..10]);
        Assert.Equal(new byte[] { 0xFF, 0x07, 0x80, 0x69 }, MifareClassicKeys.TrailerFor(a, b, [], null)[6..10]);
    }

    // Block 0 of the user's blank card: UID 76 36 DA 47, BCC DD, then the manufacturer bytes.
    private static byte[] Block0(byte u0 = 0x76) =>
        [u0, 0x36, 0xDA, 0x47, (byte)(u0 ^ 0x36 ^ 0xDA ^ 0x47), 0x88, 0x04, 0x00, 0xC8, 0x04, 0x00, 0x20, 0x00, 0x00, 0x00, 0x19];

    [Fact]
    public void The_manufacturer_block_shows_the_check_byte_and_maker_bytes_but_not_the_UID()
    {
        var rows = MifareClassicKeys.ManufacturerBlock(Block0()).ToDictionary(r => r.Label, r => r.Value);
        Assert.StartsWith("DD — valid", rows["Check byte (BCC)"]);
        Assert.Equal("88 04 00 C8 04 00 20 00 00 00 19", rows["Manufacturer bytes"]);
        Assert.DoesNotContain("UID", rows.Keys);   // the UID is already the serial number; not repeated here
    }

    [Fact]
    public void A_short_block_has_no_parts() =>
        Assert.Empty(MifareClassicKeys.ManufacturerBlock(new byte[4]));

    [Fact]
    public void A_non_NXP_uid_with_no_signature_reads_as_a_likely_clone()
    {
        var verdict = MifareClassicKeys.GenuineVerdict(Block0(0x76), signature: null);
        Assert.StartsWith("Likely a clone", verdict);
        Assert.Contains("no originality signature", verdict);
        Assert.Contains("usually writable", verdict);
    }

    [Fact]
    public void An_NXP_uid_with_a_signature_reads_as_likely_genuine()
    {
        var verdict = MifareClassicKeys.GenuineVerdict(Block0(0x04), signature: new byte[32]);
        Assert.StartsWith("Likely genuine", verdict);
        Assert.Contains("NXP's manufacturer code", verdict);
        Assert.Contains("not cryptographically verified", verdict);
    }

    [Fact]
    public void A_wrong_check_byte_is_flagged()
    {
        var block = Block0(0x04);
        block[4] ^= 0xFF;   // break the BCC
        Assert.Contains("check byte does not match", MifareClassicKeys.GenuineVerdict(block, null));
    }

    [Fact]
    public void Without_block_zero_the_verdict_is_just_the_signature() =>
        Assert.Equal("It returned no originality signature; genuine NXP EV1 chips do, while older genuine Classic chips and most clones do not.",
            MifareClassicKeys.GenuineVerdict(null, null));

    [Fact]
    public void A_trailer_line_puts_each_byte_where_the_colouring_expects_it()
    {
        // ClassicDumpView colours the trailer by column: hex from column 7, four groups of 8 digits, a space between.
        byte[] trailer = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xFF, 0x07, 0x80, 0x69, 0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5];
        var line = MifareClassicKeys.FormatBlock(3, trailer);
        Assert.StartsWith("  003  A0A1A2A3 A4A5FF07 8069B0B1 B2B3B4B5  ", line);
        for (int k = 0; k < 16; k++)
        {
            int pos = 7 + k / 4 * 9 + k % 4 * 2;
            Assert.Equal(trailer[k].ToString("X2"), line.Substring(pos, 2));
        }
    }
}
