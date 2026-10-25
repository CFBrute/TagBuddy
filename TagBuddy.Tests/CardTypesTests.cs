using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Naming a card from the small public identifiers it answers with: the SAK byte, an ISO 15693
/// manufacturer, and a DESFire version frame.</summary>
public class CardTypesTests
{
    [Theory]
    [InlineData(0x08, "MIFARE Classic 1K (or compatible)")]
    [InlineData(0x18, "MIFARE Classic 4K (or compatible)")]
    [InlineData(0x00, "MIFARE Ultralight or NTAG (Type 2 tag)")]
    [InlineData(0x20, "ISO 14443-4 smart card (DESFire, MIFARE Plus SL3, or a secure element)")]
    [InlineData(0x09, "MIFARE Mini (0.3K)")]
    public void ChipFromSak_names_common_cards(int sak, string expected) =>
        Assert.Equal(expected, CardTypes.ChipFromSak(sak));

    [Fact]
    public void ChipFromSak_ignores_the_cascade_bit() =>
        Assert.Equal("MIFARE Classic 1K (or compatible)", CardTypes.ChipFromSak(0x08 | 0x04));

    [Fact]
    public void ChipFromSak_falls_back_to_the_protocol_bits()
    {
        Assert.Equal("ISO 14443-4 smart card", CardTypes.ChipFromSak(0x60));   // bit 0x20 set, no exact match
        Assert.Equal("MIFARE Classic compatible", CardTypes.ChipFromSak(0x88));   // bit 0x08 set
        Assert.Null(CardTypes.ChipFromSak(0x40));   // says nothing useful
    }

    [Theory]
    [InlineData(0x04, "NXP Semiconductors")]
    [InlineData(0x02, "STMicroelectronics")]
    [InlineData(0x07, "Texas Instruments")]
    public void Iso15693Manufacturer_names_known_makers(byte code, string expected) =>
        Assert.Equal(expected, CardTypes.Iso15693Manufacturer(code));

    [Fact]
    public void Iso15693Manufacturer_shows_an_unknown_code_as_hex() =>
        Assert.Equal("manufacturer 0x99", CardTypes.Iso15693Manufacturer(0x99));

    [Theory]
    [InlineData(0x01, "ICODE SLI")]
    [InlineData(0x02, "ICODE SLIX")]
    public void NxpIcodeProduct_names_icode_chips(byte type, string expected) =>
        Assert.Equal(expected, CardTypes.NxpIcodeProduct(type));

    [Fact]
    public void NxpIcodeProduct_is_null_for_an_unknown_type() =>
        Assert.Null(CardTypes.NxpIcodeProduct(0x7F));

    [Fact]
    public void DesfireFromVersion_reads_the_generation_and_size()
    {
        // vendor NXP, type, subtype, major=EV1, minor, storage 0x18 = 4 KB, protocol
        byte[] block = [0x04, 0x01, 0x01, 0x01, 0x00, 0x18, 0x05];
        Assert.Equal("MIFARE DESFire EV1, 4 KB", CardTypes.DesfireFromVersion(block));

        byte[] ev3 = [0x04, 0x01, 0x01, 0x33, 0x00, 0x1A, 0x05];
        Assert.Equal("MIFARE DESFire EV3, 8 KB", CardTypes.DesfireFromVersion(ev3));
    }

    [Fact]
    public void DesfireFromVersion_rejects_a_non_NXP_or_short_block()
    {
        Assert.Null(CardTypes.DesfireFromVersion([0x02, 0x01, 0x01, 0x01, 0x00, 0x18, 0x05]));
        Assert.Null(CardTypes.DesfireFromVersion([0x04, 0x01]));
    }
}
