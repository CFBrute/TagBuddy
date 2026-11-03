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

    /// <summary>A barcode as an email in dark mode shows it: the bars in a white box cut tight around them (no
    /// quiet zone), on a dark page. The page is wide enough on each side to hold a quiet zone, which is what a
    /// reader would want to see there. <paramref name="onEnd"/> stands the barcode up.</summary>
    private static (byte[] Gray, int Width, int Height) DrawOnDarkPage(BitMatrix matrix, int module = 4, bool onEnd = false)
    {
        const int pageMargin = 120, boxMargin = 2, codeHeight = 120;
        const byte page = 45, box = 230, bar = 15;
        int bars = matrix.Width * module;
        int width = bars + 2 * (boxMargin + pageMargin), height = codeHeight + 2 * (boxMargin + pageMargin);
        var gray = new byte[width * height];
        Array.Fill(gray, page);
        for (int y = pageMargin; y < height - pageMargin; y++)
            for (int x = pageMargin; x < width - pageMargin; x++)
            {
                int bx = x - pageMargin - boxMargin, by = y - pageMargin - boxMargin;
                bool inBars = bx >= 0 && bx < bars && by >= 0 && by < codeHeight;
                gray[y * width + x] = inBars && matrix[bx / module, 0] ? bar : box;
            }
        return onEnd ? (GrayDecoder.Transpose(gray, width, height), height, width) : (gray, width, height);
    }

    [Theory]
    [InlineData("Code128", "23943727", false)]
    [InlineData("Code128", "23943727", true)]
    [InlineData("Ean13", "4006381333931", false)]
    [InlineData("Itf", "12345678", false)]
    [InlineData("Code39", "ABC-123", true)]
    public void A_barcode_on_a_dark_page_without_a_quiet_zone_is_read(string formatKey, string value, bool onEnd)
    {
        var format = BarcodeTypes.Get(formatKey);
        // Margin 0: the encoder's own quiet zone would defeat the point.
        var matrix = new ZXing.MultiFormatWriter().encode(value, format.CoreFormat, 0, 0,
            new Dictionary<ZXing.EncodeHintType, object> { [ZXing.EncodeHintType.MARGIN] = 0 });

        var (gray, width, height) = DrawOnDarkPage(matrix, onEnd: onEnd);
        var result = GrayDecoder.Decode(gray, width, height);

        Assert.NotNull(result);
        Assert.Equal(format.Key, BarcodeTypes.FromZxing(ScanResult.From(result!).Format)?.Key);
        Assert.Equal(value, result!.Text);
    }

    [Fact]
    public void Whitening_the_dark_surround_keeps_the_bars()
    {
        // One row: page | box with bars | long dark gap | box | page. Two rows, the second without contrast.
        const byte D = 20, W = 240;
        byte[] row = [D, D, D, W, D, W, W, D, D, W, D, D, D, D, D, D, W, D, W, D, D, D];
        int width = row.Length;
        var gray = new byte[width * 2];
        row.CopyTo(gray, 0);
        Array.Fill(gray, (byte)50, width, width);

        var light = GrayDecoder.WhitenDarkSurround(gray, width, 2);

        // Edges and the run longer than width / 8 (= 2) are white; bars of one and two pixels stay.
        byte[] expected = [255, 255, 255, W, D, W, W, D, D, W, 255, 255, 255, 255, 255, 255, W, D, W, 255, 255, 255];
        Assert.Equal(expected, light.Take(width));
        Assert.All(light.Skip(width), v => Assert.Equal(50, v));
    }

    [Fact]
    public void Transposing_turns_rows_into_columns()
    {
        byte[] gray = [1, 2, 3, 4, 5, 6];   // 3 wide, 2 high
        Assert.Equal(new byte[] { 1, 4, 2, 5, 3, 6 }, GrayDecoder.Transpose(gray, 3, 2));
    }
}
