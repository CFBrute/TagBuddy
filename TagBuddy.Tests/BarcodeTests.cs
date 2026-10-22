using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Input validation per format, encoding, and the build stamp shown in bug reports.</summary>
public class BarcodeTests
{
    [Theory]
    [InlineData("Ean13", "400638133393")]      // 12 digits: the check digit is added
    [InlineData("Ean13", "4006381333931")]     // 13 digits with the right check digit
    [InlineData("Ean8", "96385074")]
    [InlineData("UpcA", "036000291452")]
    [InlineData("Itf", "1234")]
    [InlineData("Code39", "ABC-123 $/+%")]
    [InlineData("Code128", "Any ASCII text!")]
    [InlineData("Codabar", "A1234B")]
    public void Validate_accepts_valid_input(string format, string value) =>
        Assert.Null(BarcodeTypes.Validate(format, value));

    [Theory]
    [InlineData("Ean13", "4006381333932", "check digit")]
    [InlineData("Ean13", "40063813", "12 or 13 digits")]
    [InlineData("Ean13", "40063813339A", "only supports digits")]
    [InlineData("UpcA", "036000291453", "check digit")]
    [InlineData("Itf", "123", "even number")]
    [InlineData("Code39", "lowercase", "uppercase")]
    [InlineData("Code128", "smörgås", "ASCII")]
    [InlineData("Code128", "", "Enter some text")]
    public void Validate_explains_what_is_wrong(string format, string value, string expected) =>
        Assert.Contains(expected, BarcodeTypes.Validate(format, value));

    [Fact]
    public void Get_falls_back_to_Code_128() =>
        Assert.Equal("Code128", BarcodeTypes.Get("NoSuchFormat").Key);

    [Fact]
    public void Every_linear_format_encodes_a_valid_value()
    {
        var samples = new Dictionary<string, string>
        {
            ["Code128"] = "Hello", ["Code39"] = "HELLO", ["Code93"] = "HELLO", ["Ean13"] = "400638133393",
            ["Ean8"] = "9638507", ["UpcA"] = "03600029145", ["Itf"] = "1234", ["Codabar"] = "A1234B",
        };
        foreach (var format in BarcodeTypes.Linear)
        {
            var matrix = BarcodeRenderer.TryEncode(format, samples[format.Key], null, out var error);
            Assert.True(matrix is not null, $"{format.Key}: {error}");
            Assert.Equal(1, matrix!.Height);   // linear codes are one row of bars
        }
    }

    [Fact]
    public void QrInfo_reports_version_and_size()
    {
        var options = new QrOptions("M", 2);
        var matrix = BarcodeRenderer.TryEncode(BarcodeTypes.QrCode, "hi", options, out _)!;
        Assert.Equal((1, 21), BarcodeRenderer.QrInfo(matrix, options.Margin));
    }

    [Fact]
    public void Higher_error_correction_makes_a_larger_code()
    {
        var text = new string('a', 60);
        var low = BarcodeRenderer.TryEncode(BarcodeTypes.QrCode, text, new QrOptions("L", 0), out _)!;
        var high = BarcodeRenderer.TryEncode(BarcodeTypes.QrCode, text, new QrOptions("H", 0), out _)!;
        Assert.True(high.Width > low.Width);
    }

    [Fact]
    public void SavedItem_DisplayName_falls_back_to_the_content()
    {
        Assert.Equal("https://example.com", new SavedItem { Value = "https://example.com" }.DisplayName);
        Assert.Equal("Mine", new SavedItem { Name = "Mine", Value = "x" }.DisplayName);
        Assert.Equal("NTAG215 04:A1", new SavedItem { Kind = SavedKind.Dump, DumpChip = "NTAG215", DumpSerial = "04:A1" }.DisplayName);
    }

    // ------------------------------------------------------------------ build stamp

    [Theory]
    [InlineData("1.0.14+5be6c14*", "1.0.14", "5be6c14*")]
    [InlineData("1.0.14", "1.0.14", "")]
    [InlineData(null, "0.0.0", "")]
    public void BuildInfo_Split_separates_version_and_commit(string? informational, string version, string commit) =>
        Assert.Equal((version, commit), BuildInfo.Split(informational));

    [Fact]
    public void BuildInfo_Explain_says_what_the_star_means()
    {
        Assert.Contains("not committed", BuildInfo.Explain("5be6c14*", "2026-10-22 13:40"));
        Assert.DoesNotContain("not committed", BuildInfo.Explain("5be6c14", ""));
        Assert.Equal("Tag Buddy v1.0.14  ·  5be6c14", BuildInfo.Describe("1.0.14", "5be6c14"));
    }
}
