using BarcodeBuddy.Models;
using ZXing.Common;

namespace BarcodeBuddy.Services;

/// <summary>Renders a code to a PNG and shares it or saves it to the phone's gallery.</summary>
public static class BarcodeImage
{
    /// <summary>How the caption came out: the widest line's natural width in code widths, and whether the
    /// max-width limit actually broke a line.</summary>
    public sealed record CaptionLayout(double TextWidthInCodes, bool LimitReached);

    public static byte[] RenderPng(BarcodeItem item) => RenderPng(item, out _);

    public static byte[] RenderPng(BarcodeItem item, out CaptionLayout layout)
    {
        var matrix = BarcodeRenderer.TryEncode(item, out var error)
            ?? throw new InvalidOperationException(error ?? "Could not encode the value.");
        return RenderPng(matrix, item, item.ImageText, out layout);
    }

    /// <summary>Pixel size of a PNG, read from its header without decoding it.</summary>
    public static (int Width, int Height) PngSize(byte[] png)
    {
        if (png.Length < 24) return (0, 0);
        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (width, height);
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

    /// <summary>Saves the image under Pictures/Tag Buddy. Returns the display path, or null when unsupported.</summary>
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
        values.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath, "Pictures/Tag Buddy");

        var uri = resolver.Insert(Android.Provider.MediaStore.Images.Media.ExternalContentUri!, values)
            ?? throw new InvalidOperationException("The gallery refused the image.");
        using (var stream = resolver.OpenOutputStream(uri)!)
            await stream.WriteAsync(png);

        return "Pictures/Tag Buddy/" + name;
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

    private static byte[] RenderPng(BitMatrix matrix, BarcodeItem item, string text, out CaptionLayout layout)
    {
#if ANDROID
        bool linear = matrix.Height == 1;
        int scale = linear ? Math.Max(4, 1400 / matrix.Width) : Math.Max(6, 1000 / matrix.Width);
        int codeW = matrix.Width * scale;
        int codeH = linear ? 420 : matrix.Height * scale;
        int pad = 48;
        int quietZone = linear ? 0 : item.QrMargin * scale;   // white rows at the bottom of a QR matrix

        // The text settings are fractions of the code as a person sees it: the bars, or the dark square of
        // a QR code without its quiet zone.
        int darkH = codeH - 2 * quietZone;
        int textSize = item.CaptionTextPx(darkH);
        int lineHeight = (int)(textSize * 1.3);
        int gap = item.CaptionGapPx(darkH, quietZone);

        using var textPaint = new Android.Graphics.Paint { AntiAlias = true, Color = Android.Graphics.Color.Black, TextSize = textSize };
        textPaint.TextAlign = item.CaptionLeft ? Android.Graphics.Paint.Align.Left
            : item.CaptionRight ? Android.Graphics.Paint.Align.Right
            : Android.Graphics.Paint.Align.Center;

        // Place the first baseline so the tops of capital letters sit exactly "gap" below the code.
        var capBounds = new Android.Graphics.Rect();
        textPaint.GetTextBounds("H", 0, 1, capBounds);
        int capHeight = capBounds.Height();

        // A line is never broken just because it is wider than the code: the image grows sideways instead,
        // up to the chosen number of code widths. Only a line wider than that is wrapped. Each line the
        // person typed starts a new line in the image. Up to 8 lines in all.
        int maxTextWidth = item.CaptionMaxTextWidthPx(codeW);
        float naturalWidth = 0;   // the widest line as typed, before any breaking
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Trim().Split('\n'))
        {
            if (lines.Count >= 8) break;
            var trimmed = paragraph.Trim();
            if (trimmed.Length == 0) { lines.Add(string.Empty); continue; }
            naturalWidth = Math.Max(naturalWidth, textPaint.MeasureText(trimmed));
            lines.AddRange(WrapText(textPaint, trimmed, maxTextWidth, maxLines: 8 - lines.Count));
        }
        layout = new CaptionLayout(naturalWidth / codeW, naturalWidth > maxTextWidth);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        int textWidth = lines.Count == 0 ? 0 : (int)Math.Ceiling(lines.Max(l => textPaint.MeasureText(l)));
        int width = Math.Max(codeW, textWidth) + 2 * pad;
        int codeLeft = (width - codeW) / 2;   // the code stays centred when the text is wider than it
        float textX = item.CaptionLeft ? pad : item.CaptionRight ? width - pad : width / 2f;

        // The text block starts "gap" below the code; the bottom margin matches the top one.
        int textTop = pad + codeH + gap;
        int height = lines.Count == 0 ? codeH + 2 * pad : textTop + capHeight + lineHeight * (lines.Count - 1) + pad;

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
                float left = codeLeft + start * scale;
                float top = linear ? pad : pad + y * scale;
                float right = codeLeft + x * scale;
                float bottom = linear ? pad + codeH : top + scale;
                canvas.DrawRect(left, top, right, bottom, paint);
            }
        }

        float baseline = textTop + capHeight;
        foreach (var line in lines)
        {
            canvas.DrawText(line, textX, baseline, textPaint);
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
