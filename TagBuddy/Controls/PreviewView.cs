namespace TagBuddy.Controls;

/// <summary>Shows a rendered PNG scaled to the view's width. Unlike an Image control, swapping the picture is
/// synchronous: the previous picture stays on screen until the new one is drawn, so live re-rendering does
/// not blink. Decode the PNG off the UI thread with <see cref="Decode"/>, then <see cref="Show"/> it.</summary>
public sealed class PreviewView : GraphicsView
{
    private Microsoft.Maui.Graphics.IImage? _image;

    public PreviewView()
    {
        Drawable = new PictureDrawable(this);
        BackgroundColor = Colors.White;
    }

    /// <summary>Pixel size of the picture on show, or 0×0.</summary>
    public (float Width, float Height) PictureSize => _image is null ? (0, 0) : (_image.Width, _image.Height);

    /// <summary>Decodes a PNG into a platform image. Safe to call on a background thread.</summary>
    public static Microsoft.Maui.Graphics.IImage? Decode(byte[] png)
    {
#if ANDROID
        using var stream = new MemoryStream(png);
        return Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(stream);
#else
        return null;
#endif
    }

    public void Show(Microsoft.Maui.Graphics.IImage? image)
    {
        var old = _image;
        _image = image;
        Invalidate();
        if (!ReferenceEquals(old, image)) old?.Dispose();
    }

    public void Clear() => Show(null);

    private sealed class PictureDrawable(PreviewView owner) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF rect)
        {
            canvas.FillColor = Colors.White;
            canvas.FillRectangle(rect);

            var image = owner._image;
            if (image is null || image.Width <= 0 || image.Height <= 0) return;

            // Fit to the width; the view's height is set by the page to match the picture's aspect.
            float scale = Math.Min(rect.Width / image.Width, rect.Height / image.Height);
            float w = image.Width * scale, h = image.Height * scale;
            canvas.DrawImage(image, rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
        }
    }
}
