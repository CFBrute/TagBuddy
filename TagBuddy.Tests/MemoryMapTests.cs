using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>The memory map: which page of a tag is what, and what can be done to it, worked out from the image.</summary>
public class MemoryMapTests
{
    private static readonly Type2Chip Ntag215 = new("NTAG215", 135, 130, true, 131);
    private static readonly Type2Chip Ntag213 = new("NTAG213", 45, 40, true, 41);

    /// <summary>An NTAG215 as an Amiibo writer leaves it: page 3 F1 10 FF EE, static lock bits 0F E0, dynamic lock
    /// bits 01 00 0F, AUTH0 = 4 (writes need the password from page 4), and a figure ID in pages 21-22.</summary>
    private static byte[] AmiiboImage()
    {
        var m = new byte[135 * 4];
        m[10] = 0x0F; m[11] = 0xE0;
        m[12] = 0xF1; m[13] = 0x10; m[14] = 0xFF; m[15] = 0xEE;
        m[130 * 4] = 0x01; m[130 * 4 + 2] = 0x0F;
        m[131 * 4 + 3] = 0x04;
        byte[] figure = [0x01, 0x00, 0x00, 0x00, 0x03, 0x4E, 0x02, 0x02];
        figure.CopyTo(m, 84);
        return m;
    }

    [Fact]
    public void An_Amiibo_tag_is_mapped_page_by_page()
    {
        var map = Type2Memory.MapPages(AmiiboImage(), Ntag215, amiibo: true);

        Assert.Equal(135, map.Count);
        Assert.All(map.Take(2), p => Assert.Equal(PageAccess.Fixed, p.Access));
        Assert.Equal(PageAccess.OneTime, map[2].Access);                 // the static lock bits share page 2
        Assert.Equal(PageAccess.Locked, map[3].Access);                  // static lock bit 3
        Assert.All(map.Skip(4).Take(9), p => Assert.Equal(PageAccess.Password, p.Access));      // 4-12
        Assert.All(map.Skip(13).Take(19), p => Assert.Equal(PageAccess.Locked, p.Access));      // 13-31
        Assert.All(map.Skip(32).Take(98), p => Assert.Equal(PageAccess.Password, p.Access));    // 32-129
        Assert.Equal(PageAccess.OneTime, map[130].Access);
        Assert.Equal("dynamic lock bits", map[130].Role);
        Assert.Equal(PageAccess.Config, map[131].Access);
        Assert.Equal(PageAccess.Config, map[132].Access);
        Assert.Equal(PageAccess.Hidden, map[133].Access);
        Assert.Equal(PageAccess.Hidden, map[134].Access);
        Assert.Equal("figure ID", map[21].Role);
        Assert.Equal("figure ID", map[22].Role);
    }

    [Fact]
    public void The_overview_groups_pages_into_ranges()
    {
        var summary = Type2Memory.SummarizePages(Type2Memory.MapPages(AmiiboImage(), Ntag215));
        Assert.Equal(
            [(PageAccess.Password, "4-12, 32-129"), (PageAccess.Locked, "3, 13-31"), (PageAccess.OneTime, "2, 130"),
             (PageAccess.Fixed, "0-1"), (PageAccess.Config, "131-132"), (PageAccess.Hidden, "133-134")],
            summary);
    }

    [Fact]
    public void A_blank_tag_is_writable_without_a_password()
    {
        var m = new byte[45 * 4];
        m[41 * 4 + 3] = 0xFF;   // AUTH0 beyond the last page: no password protection
        var map = Type2Memory.MapPages(m, Ntag213);

        Assert.Equal(PageAccess.OneTime, map[3].Access);
        Assert.All(map.Skip(4).Take(36), p => Assert.Equal(PageAccess.Writable, p.Access));
        Assert.Equal(PageAccess.OneTime, map[40].Access);
        Assert.Equal(PageAccess.Config, map[41].Access);
    }

    [Fact]
    public void Without_a_known_chip_only_the_fixed_pages_are_named()
    {
        var map = Type2Memory.MapPages(new byte[60 * 4], chip: null);
        Assert.Equal(PageAccess.Fixed, map[0].Access);
        Assert.Equal(PageAccess.OneTime, map[2].Access);
        Assert.Equal(PageAccess.OneTime, map[3].Access);
        Assert.All(map.Skip(4), p => Assert.Equal(PageAccess.Unknown, p.Access));
    }

    [Theory]
    [InlineData("NTAG215", 0, "NTAG215")]
    [InlineData("Amiibo", 0, "NTAG215")]
    [InlineData(null, 45 * 4, "NTAG213")]
    [InlineData(null, 135 * 4, "NTAG215")]
    [InlineData("Type2", 231 * 4, "NTAG216")]
    public void The_chip_is_known_by_name_or_size(string? name, int length, string expected) =>
        Assert.Equal(expected, Type2Memory.ChipFor(name, length)?.Name);

    [Fact]
    public void An_unknown_size_gives_no_chip() =>
        Assert.Null(Type2Memory.ChipFor("Type2", 100));

    [Fact]
    public void The_figure_ID_is_read_from_pages_21_and_22() =>
        Assert.Equal("01000000 034E0202 (figure)", Type2Memory.AmiiboFigure(AmiiboImage()));

    [Fact]
    public void No_figure_ID_on_an_empty_tag() =>
        Assert.Null(Type2Memory.AmiiboFigure(new byte[135 * 4]));
}
