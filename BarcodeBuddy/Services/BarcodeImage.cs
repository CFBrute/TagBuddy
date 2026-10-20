using BarcodeBuddy.Models;
using ZXing.Common;

namespace BarcodeBuddy.Services;

/// <summary>Renders a code to a PNG and shares it or saves it to the phone's gallery.</summary>
public static class BarcodeImage
{
    public static byte[] RenderPng(BarcodeItem item)
    {
        var matrix = BarcodeRenderer.TryEncode(item, out var error)
            ?? throw new InvalidOperationException(error ?? "Could not encode the value.");
        return RenderPng(matrix, item.Value);
    }

    public static async Task ShareAsync(BarcodeItem item)
    {
        var png = RenderPng(item);
        var path = Path.Combine(FileSystem.CacheDirectory, FileName(item));
        await File.WriteAllBytesAsync(path, png);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = item.DisplayName,
            File = new ShareFile(path, "image/png"),
        });
    }

    /// <summary>Saves the image under Pictures/Barcode Buddy. Returns the display path, or null when unsupported.</summary>
    public static async Task<string?> SaveToGalleryAsync(BarcodeItem item)
    {
        var png = RenderPng(item);
        var name = FileName(item);
#if ANDROID
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return null;

        var resolver = Platform.AppContext.ContentResolver!;
        var values = new Android.Content.ContentValues();
        values.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, name);
        values.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, "image/png");
        values.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath, "Pictures/Barcode Buddy");

        var uri = resolver.Insert(Android.Provider.MediaStore.Images.Media.ExternalContentUri!, values)
            ?? throw new InvalidOperationException("The gallery refused the image.");
        using (var stream = resolver.OpenOutputStream(uri)!)
            await stream.WriteAsync(png);

        return "Pictures/Barcode Buddy/" + name;
#else
        return null;
#endif
    }

    private static string FileName(BarcodeItem item)
    {
        var baseName = string.IsNullOrWhiteSpace(item.Name) ? item.Value : item.Name;
        // For links, drop the scheme so the file is named after the site rather than "https___".
        if (string.IsNullOrWhiteSpace(item.Name) && Links.TryGetOpenable(item.Value, out var uri) && uri.Scheme is "http" or "https")
            baseName = uri.Host + uri.AbsolutePath.TrimEnd('/');
        var safe = new string(baseName.Select(c => char.IsLetterOrDigit(c) ? c : '_').Take(40).ToArray()).Trim('_');
        while (safe.Contains("__")) safe = safe.Replace("__", "_");
        if (safe.Length == 0) safe = "code";
        return $"{safe}-{item.FormatLabel.Replace(' ', '_')}.png";
    }

    private static byte[] RenderPng(BitMatrix matrix, string text)
    {
#if ANDROID
        bool linear = matrix.Height == 1;
        int scale = linear ? Math.Max(4, 1400 / matrix.Width) : Math.Max(6, 1000 / matrix.Width);
        int codeW = matrix.Width * scale;
        int codeH = linear ? 420 : matrix.Height * scale;
        int pad = 48;
        int textSize = 44;
        int lineHeight = (int)(textSize * 1.3);
        int width = codeW + 2 * pad;

        using var textPaint = new Android.Graphics.Paint { AntiAlias = true, Color = Android.Graphics.Color.Black, TextSize = textSize };
        textPaint.TextAlign = Android.Graphics.Paint.Align.Center;
        var lines = WrapText(textPaint, text.Replace('\r', ' ').Replace('\n', ' ').Trim(), width - 2 * pad, maxLines: 4);

        int height = codeH + 2 * pad + lineHeight * lines.Count + pad / 2;

        using var bitmap = Android.Graphics.Bitmap.CreateBitmap(width, height, Android.Graphics.Bitmap.Config.Argb8888!)!;
        using var canvas = new Android.Graphics.Canvas(bitmap);
        canvas.DrawColor(Android.Graphics.Color.White);

        using var paint = new Android.Graphics.Paint { AntiAlias = false, Color = Android.Graphics.Color.Black };
        paint.SetStyle(Android.Graphics.Paint.Style.Fill);

        for (int y = 0; y < matrix.Height; y++)
        {
            int x = 0;
            while (x < matrix.Width)
            {
                if (!matrix[x, y]) { x++; continue; }
                int start = x;
                while (x < matrix.Width && matrix[x, y]) x++;
                float left = pad + start * scale;
                float top = linear ? pad : pad + y * scale;
                float right = pad + x * scale;
                float bottom = linear ? pad + codeH : top + scale;
                canvas.DrawRect(left, top, right, bottom, paint);
            }
        }

        float baseline = codeH + 2 * pad + textSize;
        foreach (var line in lines)
        {
            canvas.DrawText(line, width / 2f, baseline, textPaint);
            baseline += lineHeight;
        }

        using var stream = new MemoryStream();
        bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 100, stream);
        return stream.ToArray();
#else
        throw new PlatformNotSupportedException();
#endif
    }

#if ANDROID
    /// <summary>Greedy word wrap that also breaks long tokens (URLs) at slashes and punctuation.</summary>
    private static List<string> WrapText(Android.Graphics.Paint paint, string text, float maxWidth, int maxLines)
    {
        var lines = new List<string>();
        if (text.Length == 0) return lines;

        int start = 0;
        while (start < text.Length && lines.Count < maxLines)
        {
            // Longest prefix that fits.
            int count = paint.BreakText(text, start, text.Length, true, maxWidth, null);
            if (count <= 0) count = 1;
            int end = start + count;

            if (end < text.Length)
            {
                // Prefer to break after a separator in the last part of the line.
                int minBreak = start + (int)(count * 0.5);
                for (int i = end - 1; i > minBreak; i--)
                {
                    if (" /?&=-_.,;:".Contains(text[i])) { end = i + 1; break; }
                }
            }

            lines.Add(text[start..end].Trim());
            start = end;
            while (start < text.Length && text[start] == ' ') start++;
        }

        if (start < text.Length && lines.Count > 0)
        {
            // Ran out of lines: end the last one with an ellipsis.
            var last = lines[^1];
            while (last.Length > 1 && paint.MeasureText(last + "…") > maxWidth) last = last[..^1];
            lines[^1] = last + "…";
        }
        return lines;
    }
#endif
}
