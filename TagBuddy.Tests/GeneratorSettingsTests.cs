using System.Text.Json;
using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Remembered generator settings and the image-text fonts: values from the store can never break a
/// dial, and older saves without a font still draw.</summary>
public class GeneratorSettingsTests
{
    [Fact]
    public void Sanitized_keeps_valid_settings()
    {
        var caption = new GeneratorSettings.Caption("Left", 0.3, 0.2, 3.0, "serif-bold");
        Assert.Equal(caption, caption.Sanitized());
    }

    [Fact]
    public void Sanitized_brings_values_into_the_dials_range()
    {
        var caption = new GeneratorSettings.Caption("Diagonal", 50, -1, double.NaN, "comic-sans").Sanitized();

        Assert.Equal("Center", caption.Align);
        Assert.Equal(SavedItem.MaxCaptionTextScale, caption.TextScale);
        Assert.Equal(SavedItem.MinCaptionGapScale, caption.GapScale);
        Assert.Equal(SavedItem.DefaultCaptionWidthScale, caption.WidthScale);
        Assert.Equal(CaptionFonts.Default, caption.Font);
    }

    [Fact]
    public void Default_settings_match_a_new_saved_item()
    {
        var item = new SavedItem();
        var d = GeneratorSettings.Caption.Default;
        Assert.Equal((item.CaptionAlign, item.CaptionTextScale, item.CaptionGapScale, item.CaptionWidthScale, item.CaptionFont),
            (d.Align, d.TextScale, d.GapScale, d.WidthScale, d.Font));
    }

    [Fact]
    public void Font_keys_are_unique_and_the_default_comes_first()
    {
        Assert.Equal(CaptionFonts.All.Count, CaptionFonts.All.Select(f => f.Key).Distinct().Count());
        Assert.Equal(CaptionFonts.Default, CaptionFonts.All[0].Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-font")]
    public void Unknown_font_gets_the_default(string? key) =>
        Assert.Equal(CaptionFonts.Default, CaptionFonts.Get(key).Key);

    [Fact]
    public void Saved_items_from_before_fonts_load_with_the_default_font()
    {
        var item = JsonSerializer.Deserialize<SavedItem>("""{"Value":"hello","Format":"QrCode"}""")!;
        Assert.Equal(CaptionFonts.Default, item.CaptionFont);
    }

    [Fact]
    public void A_saved_items_font_is_kept()
    {
        var item = new SavedItem { Value = "x", CaptionFont = "mono" };
        var back = JsonSerializer.Deserialize<SavedItem>(JsonSerializer.Serialize(item))!;
        Assert.Equal("mono", back.CaptionFont);
    }
}
