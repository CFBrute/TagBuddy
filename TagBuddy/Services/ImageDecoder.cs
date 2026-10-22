using ZXing;

namespace TagBuddy.Services;

/// <summary>Finds a barcode or QR code inside a picture using the ZXing reader.</summary>
public static class ImageDecoder
{
    private const int MaxSide = 2200;

    public static ScanResult? Decode(byte[] imageBytes)
    {
#if ANDROID
        var bounds = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
        Android.Graphics.BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

        // Downsample very large photos: the decoder does not need more than ~2000 px on the long side.
        int sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > MaxSide) sample *= 2;

        var options = new Android.Graphics.BitmapFactory.Options { InSampleSize = sample, InPreferredConfig = Android.Graphics.Bitmap.Config.Argb8888 };
        using var bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length, options);
        if (bitmap is null) return null;

        int w = bitmap.Width, h = bitmap.Height;
        var pixels = new int[w * h];
        bitmap.GetPixels(pixels, 0, w, 0, 0, w, h);

        var gray = new byte[w * h];
        for (int i = 0; i < pixels.Length; i++)
        {
            int p = pixels[i];
            int r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
            gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }

        var result = GrayDecoder.Decode(gray, w, h);
        return result is null ? null : ScanResult.From(result);
#else
        return null;
#endif
    }
}
