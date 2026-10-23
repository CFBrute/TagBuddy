using TagBuddy.Models;

namespace TagBuddy.Tests;

/// <summary>Saving a code that is already saved: the same picture is a duplicate, a different look (text under the
/// image, QR options) is a version worth keeping, and the Saved list tells versions apart.</summary>
public class SavedVariantTests
{
    private static SavedItem Qr(string value = "https://example.com") => new() { Value = value, Format = BarcodeTypes.QrCode.Key };

    [Fact]
    public void Same_content_and_format_is_the_same_code()
    {
        Assert.True(Qr().IsSameCode(Qr()));
        Assert.False(Qr().IsSameCode(Qr("https://example.org")));
        Assert.False(Qr().IsSameCode(new SavedItem { Value = "https://example.com", Format = "Code128" }));
        Assert.False(Qr().IsSameCode(new SavedItem { Kind = SavedKind.Text, Value = "https://example.com" }));
    }

    [Fact]
    public void Identical_pictures_have_no_differences() =>
        Assert.Empty(Qr().ImageDifferences(Qr()));

    [Fact]
    public void Text_that_follows_the_content_equals_the_same_text_typed()
    {
        var typed = Qr();
        typed.Caption = typed.Value;
        Assert.Empty(Qr().ImageDifferences(typed));
    }

    [Fact]
    public void Different_text_under_the_image_is_a_difference()
    {
        var other = Qr();
        other.Caption = "Scan me";
        Assert.Equal(["the text under the image"], Qr().ImageDifferences(other));
    }

    [Fact]
    public void Layout_differences_are_named()
    {
        var other = Qr();
        other.CaptionAlign = "Left";
        other.CaptionFont = "serif";
        other.CaptionTextScale += 0.05;
        Assert.Equal(["the text's position", "the text's font", "the text's size"], Qr().ImageDifferences(other));
    }

    [Fact]
    public void Layout_does_not_matter_without_text()
    {
        var a = Qr();
        a.Caption = "";
        var b = Qr();
        b.Caption = "";
        b.CaptionFont = "mono";
        b.CaptionAlign = "Right";
        Assert.Empty(a.ImageDifferences(b));
    }

    [Fact]
    public void QR_options_are_differences()
    {
        var other = Qr();
        other.QrErrorCorrection = "H";
        other.QrMargin = 4;
        Assert.Equal(["error correction", "margin"], Qr().ImageDifferences(other));
    }

    [Fact]
    public void Steps_smaller_than_the_dials_are_the_same_setting()
    {
        var other = Qr();
        other.CaptionTextScale += 0.0001;
        Assert.Empty(Qr().ImageDifferences(other));
    }

    [Fact]
    public void The_list_shows_the_text_of_a_version()
    {
        var item = Qr("ABC123");
        Assert.Equal("ABC123", item.Subtitle);

        item.Caption = "Locker 7\nsecond line";
        Assert.Equal("ABC123  ·  text: Locker 7", item.Subtitle);

        item.Caption = "";
        Assert.Equal("ABC123  ·  no text under the image", item.Subtitle);
    }
}
