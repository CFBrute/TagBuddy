using ZXing;

namespace TagBuddy.Services;

/// <summary>Finds a code in a grayscale picture, trying progressively "softer" views of it. Photos of screens
/// carry a fine moiré of monitor pixels that the binarizer reads as noise; a light blur or a smaller copy
/// removes it. Clean pictures decode on the first pass, so the extra passes cost nothing there.</summary>
public static class GrayDecoder
{
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
        if (qw < 200 || qh < 200) return null;
        if (TryDecode(quarter, qw, qh) is { } quartered) return quartered;
        return TryDecode(BoxBlur(BoxBlur(quarter, qw, qh, 1), qw, qh, 1), qw, qh);
    }

    private static Result? TryDecode(byte[] gray, int width, int height)
    {
        var source = new RGBLuminanceSource(gray, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = { TryHarder = true, TryInverted = true },
        };
        return reader.Decode(source);
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
