namespace TagBuddy.Services;

/// <summary>The picture a code was scanned from, as a JPEG small enough to keep with the saved item: the camera
/// frame the code was read in, or the picture picked with "From image". Only made when Settings says to keep
/// scanned pictures (<see cref="AppSettings.KeepScanPhotos"/>).</summary>
public static class ScanPhoto
{
    /// <summary>Longest side of a kept picture, in pixels: enough to see the code and what was around it.</summary>
    public const int MaxSide = 1600;
    private const int Quality = 85;

#if ANDROID
    /// <summary>A camera frame as the library delivers it (RGBA, or a plain luminance plane; rows may be padded to
    /// <paramref name="stride"/> bytes) as a JPEG. Null when it cannot be made; never throws, since it runs on the
    /// camera thread right after a read.</summary>
    public static byte[]? FromFrame(Java.Nio.ByteBuffer buffer, int width, int height, int stride, int bytesPerPixel)
    {
        try
        {
            using var bitmap = Android.Graphics.Bitmap.CreateBitmap(width, height, Android.Graphics.Bitmap.Config.Argb8888!)!;
            // A duplicate reads from the start without moving the library's own position.
            using var view = buffer.Duplicate()!;
            if (bytesPerPixel == 4)
            {
                // ARGB_8888 is laid out as R, G, B, A bytes, exactly what CameraX's RGBA_8888 frames hold.
                if (stride == width * 4)
                {
                    view.Rewind();
                    bitmap.CopyPixelsFromBuffer(view);
                }
                else
                {
                    var packed = new byte[width * height * 4];
                    for (int y = 0; y < height; y++)
                    {
                        view.Position(y * stride);
                        view.Get(packed, y * width * 4, width * 4);
                    }
                    bitmap.CopyPixelsFromBuffer(Java.Nio.ByteBuffer.Wrap(packed)!);
                }
            }
            else
            {
                var pixels = new int[width * height];
                var row = new byte[width];
                for (int y = 0; y < height; y++)
                {
                    view.Position(y * stride);
                    view.Get(row, 0, width);
                    int o = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int v = row[x];
                        pixels[o + x] = unchecked((int)0xFF000000) | (v << 16) | (v << 8) | v;
                    }
                }
                bitmap.SetPixels(pixels, 0, width, 0, 0, width, height);
            }
            return Encode(bitmap, rotation: 0);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A picked picture re-encoded no larger than <see cref="MaxSide"/>, turned the way its EXIF
    /// orientation says, so it takes little space and shows the right way up. Null when it cannot be read.</summary>
    public static byte[]? FromPicture(byte[] imageBytes)
    {
        try
        {
            var bounds = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
            Android.Graphics.BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

            // Decode at most twice the target size (powers of two only), then scale the rest of the way.
            int sample = 1;
            while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > MaxSide * 2) sample *= 2;
            var options = new Android.Graphics.BitmapFactory.Options { InSampleSize = sample };
            using var bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length, options);
            return bitmap is null ? null : Encode(bitmap, Rotation(imageBytes));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Degrees a picture must be turned clockwise to show the right way up, from its EXIF orientation.</summary>
    private static int Rotation(byte[] imageBytes)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes);
            var exif = new Android.Media.ExifInterface(stream);
            return (Android.Media.Orientation)exif.GetAttributeInt(Android.Media.ExifInterface.TagOrientation, 1) switch
            {
                Android.Media.Orientation.Rotate90 => 90,
                Android.Media.Orientation.Rotate180 => 180,
                Android.Media.Orientation.Rotate270 => 270,
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Scales the bitmap down to <see cref="MaxSide"/> if needed, turns it, and compresses it as a JPEG.</summary>
    private static byte[] Encode(Android.Graphics.Bitmap bitmap, int rotation)
    {
        int longest = Math.Max(bitmap.Width, bitmap.Height);
        var matrix = new Android.Graphics.Matrix();
        if (longest > MaxSide) matrix.PostScale((float)MaxSide / longest, (float)MaxSide / longest);
        if (rotation != 0) matrix.PostRotate(rotation);

        var source = bitmap;
        Android.Graphics.Bitmap? transformed = null;
        if (!matrix.IsIdentity)
        {
            transformed = Android.Graphics.Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true)!;
            source = transformed;
        }
        try
        {
            using var stream = new MemoryStream();
            source.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, Quality, stream);
            return stream.ToArray();
        }
        finally
        {
            if (!ReferenceEquals(transformed, bitmap)) transformed?.Dispose();
        }
    }
#else
    /// <summary>Only Android can decode pictures; elsewhere there is nothing to keep.</summary>
    public static byte[]? FromPicture(byte[] imageBytes) => null;
#endif
}
