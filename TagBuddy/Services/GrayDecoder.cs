using ZXing;

namespace TagBuddy.Services;

/// <summary>Finds a code in a grayscale picture, trying progressively "softer" views of it. Photos of screens
/// carry a fine moiré of monitor pixels that the binarizer reads as noise; a light blur or a smaller copy
/// removes it. Clean pictures decode on the first pass, so the extra passes cost nothing there.
///
/// The last passes are for a barcode on a dark page: an email or web page in dark mode shows the barcode as a
/// white box cropped tight around the bars, and the dark page on both sides leaves no quiet zone, which every
/// linear reader insists on. Those passes paint the dark surround white first (<see cref="WhitenDarkSurround"/>).</summary>
public static class GrayDecoder
{
    /// <summary>The linear formats the app reads: what the dark-surround passes look for.</summary>
    internal static readonly List<BarcodeFormat> LinearFormats =
    [
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93,
        BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
        BarcodeFormat.ITF, BarcodeFormat.CODABAR,
    ];

    /// <summary>Every format the app reads.</summary>
    internal static readonly List<BarcodeFormat> AllFormats =
    [
        .. LinearFormats,
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417,
    ];

    public static Result? Decode(byte[] gray, int width, int height)
    {
        // 1. As is.
        if (TryDecode(gray, width, height) is { } clean) return clean;

        // 2. Same size, blurred (two box passes of radius 2, close to a Gaussian with sigma 2).
        if (TryDecode(BoxBlur(BoxBlur(gray, width, height, 2), width, height, 2), width, height) is { } blurred) return blurred;

        // 3. Half size with a light blur (radius 1 twice, about sigma 1.2).
        var (half, hw, hh) = Downscale2x(gray, width, height);
        if (TryDecode(BoxBlur(BoxBlur(half, hw, hh, 1), hw, hh, 1), hw, hh) is { } halved) return halved;

        // 4. Quarter size, plain and then lightly blurred.
        var (quarter, qw, qh) = Downscale2x(half, hw, hh);
        if (qw >= 200 && qh >= 200)
        {
            if (TryDecode(quarter, qw, qh) is { } quartered) return quartered;
            if (TryDecode(BoxBlur(BoxBlur(quarter, qw, qh, 1), qw, qh, 1), qw, qh) is { } quarteredBlurred) return quarteredBlurred;
        }

        // 5. A barcode on a dark page, with the dark surround painted white: half size first (cheap), plain
        // and then blurred (a photo of a screen needs the blur against its moiré, a thin clean code cannot
        // take it), then the same at full size for a small code. The whitening works along rows, so a barcode
        // standing on end gets the same on the transposed picture.
        if (TryDecodeWithoutDarkSurround(half, hw, hh, blur: 0) is { } halfLight) return halfLight;
        if (TryDecodeWithoutDarkSurround(half, hw, hh, blur: 1) is { } halfLightBlurred) return halfLightBlurred;
        if (TryDecodeWithoutDarkSurround(gray, width, height, blur: 0) is { } light) return light;
        return TryDecodeWithoutDarkSurround(gray, width, height, blur: 2);
    }

    private static Result? TryDecode(byte[] gray, int width, int height)
    {
        var source = new RGBLuminanceSource(gray, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            // ReturnCodabarStartEnd: keep a Codabar's A-D start and stop letters, which are part of the code
            // (libraries use them); without it "A1234B" reads as "1234" and a saved or regenerated copy differs.
            Options = { TryHarder = true, TryInverted = true, ReturnCodabarStartEnd = true },
        };
        return reader.Decode(source);
    }

    /// <summary>Linear codes only, across the rows and then down the columns, each with the dark surround
    /// painted white and a box blur of <paramref name="blur"/> radius (0 for none).</summary>
    private static Result? TryDecodeWithoutDarkSurround(byte[] gray, int width, int height, int blur)
    {
        var reader = new BarcodeReaderGeneric
        {
            // No AutoRotate: the whitening only makes sense along the rows, so the other way round is a separate pass.
            AutoRotate = false,
            Options = { TryHarder = true, PossibleFormats = LinearFormats, ReturnCodabarStartEnd = true },
        };

        var across = WhitenDarkSurround(gray, width, height);
        if (blur > 0) across = BoxBlur(across, width, height, blur);
        if (reader.Decode(new RGBLuminanceSource(across, width, height, RGBLuminanceSource.BitmapFormat.Gray8)) is { } result)
            return result;

        var down = WhitenDarkSurround(Transpose(gray, width, height), height, width);
        if (blur > 0) down = BoxBlur(down, height, width, blur);
        return reader.Decode(new RGBLuminanceSource(down, height, width, RGBLuminanceSource.BitmapFormat.Gray8));
    }

    /// <summary>Paints the dark surround of a barcode white, row by row: a dark run that touches the left or
    /// right edge of the picture, or is longer than an eighth of its width, cannot be a bar (the widest bar of
    /// any linear format is four modules, and a code fits the picture with its quiet zones), so it is the page
    /// behind the code. Dark means below the midpoint of the row's own darkest and lightest pixels; rows with
    /// no contrast are left alone. The bars themselves, and anything else short and dark, stay as they are.
    /// Not for 2D codes: a QR code's finder pattern is a long dark run.</summary>
    internal static byte[] WhitenDarkSurround(byte[] src, int width, int height)
    {
        const int MinContrast = 32;
        int maxRun = Math.Max(1, width / 8);
        var dst = (byte[])src.Clone();

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int low = 255, high = 0;
            for (int x = 0; x < width; x++)
            {
                int v = src[row + x];
                if (v < low) low = v;
                if (v > high) high = v;
            }
            if (high - low < MinContrast) continue;
            int threshold = (low + high) / 2;

            int start = 0;
            while (start < width)
            {
                if (src[row + start] >= threshold) { start++; continue; }
                int end = start;
                while (end < width && src[row + end] < threshold) end++;
                if (start == 0 || end == width || end - start > maxRun)
                    Array.Fill(dst, (byte)255, row + start, end - start);
                start = end;
            }
        }
        return dst;
    }

    /// <summary>Rows become columns: the result is <paramref name="height"/> wide and <paramref name="width"/> high.</summary>
    internal static byte[] Transpose(byte[] src, int width, int height)
    {
        var dst = new byte[src.Length];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++) dst[x * height + y] = src[row + x];
        }
        return dst;
    }

    /// <summary>Separable box blur with edge replication.</summary>
    internal static byte[] BoxBlur(byte[] src, int width, int height, int radius)
    {
        int window = radius * 2 + 1;
        var tmp = new byte[src.Length];
        var dst = new byte[src.Length];

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int sum = 0;
            for (int k = -radius; k <= radius; k++) sum += src[row + Math.Clamp(k, 0, width - 1)];
            for (int x = 0; x < width; x++)
            {
                tmp[row + x] = (byte)(sum / window);
                int leaving = Math.Clamp(x - radius, 0, width - 1);
                int entering = Math.Clamp(x + radius + 1, 0, width - 1);
                sum += src[row + entering] - src[row + leaving];
            }
        }

        for (int x = 0; x < width; x++)
        {
            int sum = 0;
            for (int k = -radius; k <= radius; k++) sum += tmp[Math.Clamp(k, 0, height - 1) * width + x];
            for (int y = 0; y < height; y++)
            {
                dst[y * width + x] = (byte)(sum / window);
                int leaving = Math.Clamp(y - radius, 0, height - 1);
                int entering = Math.Clamp(y + radius + 1, 0, height - 1);
                sum += tmp[entering * width + x] - tmp[leaving * width + x];
            }
        }
        return dst;
    }

    /// <summary>Averages 2x2 blocks into one pixel.</summary>
    internal static (byte[] Gray, int Width, int Height) Downscale2x(byte[] src, int width, int height)
    {
        int w = width / 2, h = height / 2;
        var dst = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int r0 = (y * 2) * width, r1 = r0 + width;
            for (int x = 0; x < w; x++)
            {
                int c = x * 2;
                dst[y * w + x] = (byte)((src[r0 + c] + src[r0 + c + 1] + src[r1 + c] + src[r1 + c + 1]) / 4);
            }
        }
        return (dst, w, h);
    }
}
