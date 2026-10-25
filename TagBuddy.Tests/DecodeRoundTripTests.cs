using TagBuddy.Models;
using TagBuddy.Services;
using ZXing.Common;

namespace TagBuddy.Tests;

/// <summary>Codes the app draws can be read back by the decoder "From image" uses: every format, through the same
/// encoder and the same grey-picture decoder, and the scan details that come with the result.</summary>
public class DecodeRoundTripTests
{
    /// <summary>Draws a code matrix as a grey picture, <paramref name="module"/> pixels per module, with a white
    /// border, and 2D codes square; linear codes get height so the decoder sees bars.</summary>
    private static (byte[] Gray, int Width, int Height) Draw(BitMatrix matrix, int module = 4, int border = 40)
    {
        bool linear = matrix.Height < 8;
        int codeHeight = linear ? 120 : matrix.Height * module;
        int width = matrix.Width * module + 2 * border, height = codeHeight + 2 * border;
        var gray = new byte[width * height];
        Array.Fill(gray, (byte)255);
        for (int y = 0; y < codeHeight; y++)
        {
            int row = linear ? 0 : y / module;
            for (int x = 0; x < matrix.Width * module; x++)
                if (matrix[x / module, row]) gray[(border + y) * width + border + x] = 0;
        }
        return (gray, width, height);
    }

    [Theory]
    [InlineData("QrCode", "https://example.com/hello?x=1")]
    [InlineData("Code128", "TAG-BUDDY 42")]
    [InlineData("Code39", "ABC-123")]
    [InlineData("Code93", "CODE93")]
    [InlineData("Ean13", "400638133393")]       // the check digit is added when encoding
    [InlineData("Ean8", "9638507")]
    [InlineData("UpcA", "03600029145")]
    [InlineData("Itf", "12345678")]
    [InlineData("Codabar", "A1234B")]
    public void Every_format_the_app_draws_is_read_back(string formatKey, string value)
    {
        var format = BarcodeTypes.Get(formatKey);
        var matrix = BarcodeRenderer.TryEncode(format, value, format.IsLinear ? null : new QrOptions(), out var error);
        Assert.True(matrix is not null, error);

        var (gray, width, height) = Draw(matrix!);
        var result = GrayDecoder.Decode(gray, width, height);

        Assert.NotNull(result);
        Assert.Equal(format.Key, BarcodeTypes.FromZxing(ScanResult.From(result!).Format)?.Key);
        // Check-digit formats come back with the digit the encoder added; UPC-A may come back with EAN-13's
        // leading zero. Everything else, Codabar's start and stop letters included, comes back as it went in.
        Assert.StartsWith(value.TrimStart('0'), result!.Text.TrimStart('0'));
    }

    [Fact]
    public void A_Codabar_keeps_its_start_and_stop_letters()
    {
        var matrix = BarcodeRenderer.TryEncode(BarcodeTypes.Get("Codabar"), "A1234B", null, out _)!;
        var (gray, width, height) = Draw(matrix);
        Assert.Equal("A1234B", GrayDecoder.Decode(gray, width, height)!.Text);
    }

    [Fact]
    public void A_QR_code_reports_its_error_correction_level()
    {
        var matrix = BarcodeRenderer.TryEncode(BarcodeTypes.QrCode, "hello", new QrOptions("H", 2), out _)!;
        var (gray, width, height) = Draw(matrix);
        var scan = ScanResult.From(GrayDecoder.Decode(gray, width, height)!);

        Assert.Equal("H", scan.ErrorCorrectionLevel);
        Assert.Contains(ScanDetails.DescribeMetadata(scan), d => d.Label == "Error correction" && d.Value.StartsWith("H"));
    }

    [Fact]
    public void An_empty_picture_holds_no_code()
    {
        var gray = new byte[300 * 300];
        Array.Fill(gray, (byte)255);
        Assert.Null(GrayDecoder.Decode(gray, 300, 300));
    }
}
