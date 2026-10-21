using BarcodeBuddy.Models;
using BarcodeBuddy.Services;
using ZXing.Common;

namespace BarcodeBuddy.Controls;

/// <summary>Draws a ZXing module matrix crisply: a single-row matrix becomes bars, a square matrix becomes a 2D code.</summary>
public class BarcodeView : GraphicsView
{
    public static readonly BindableProperty MatrixProperty = BindableProperty.Create(
        nameof(Matrix), typeof(BitMatrix), typeof(BarcodeView), null,
        propertyChanged: (b, _, _) => ((BarcodeView)b).Invalidate());

    public static readonly BindableProperty ForegroundProperty = BindableProperty.Create(
        nameof(Foreground), typeof(Color), typeof(BarcodeView), Colors.Black,
        propertyChanged: (b, _, _) => ((BarcodeView)b).Invalidate());

    public static readonly BindableProperty BackgroundProperty = BindableProperty.Create(
        nameof(Background), typeof(Color), typeof(BarcodeView), Colors.White,
        propertyChanged: (b, _, _) => ((BarcodeView)b).Invalidate());

    /// <summary>A saved code to draw. Set this from a list template instead of <see cref="Matrix"/>: the item is
    /// encoded on first use and remembered, so scrolling a list of favorites does not re-encode every row.</summary>
    public static readonly BindableProperty ItemProperty = BindableProperty.Create(
        nameof(Item), typeof(BarcodeItem), typeof(BarcodeView), null,
        propertyChanged: (b, _, v) => ((BarcodeView)b).Matrix = v is BarcodeItem item ? Encode(item) : null);

    private static readonly Dictionary<string, BitMatrix?> _encoded = new();

    public BitMatrix? Matrix
    {
        get => (BitMatrix?)GetValue(MatrixProperty);
        set => SetValue(MatrixProperty, value);
    }

    public BarcodeItem? Item
    {
        get => (BarcodeItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private static BitMatrix? Encode(BarcodeItem item)
    {
        var key = $"{item.Format}\n{item.QrErrorCorrection}\n{item.QrMargin}\n{item.Value}";
        lock (_encoded)
        {
            if (_encoded.TryGetValue(key, out var cached)) return cached;
            if (_encoded.Count > 200) _encoded.Clear();
            var matrix = BarcodeRenderer.TryEncode(item, out _);
            _encoded[key] = matrix;
            return matrix;
        }
    }

    public new Color Foreground
    {
        get => (Color)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public new Color Background
    {
        get => (Color)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public BarcodeView()
    {
        Drawable = new MatrixDrawable(this);
        BackgroundColor = Colors.White;
    }

    private sealed class MatrixDrawable(BarcodeView owner) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF rect)
        {
            canvas.Antialias = false;
            canvas.FillColor = owner.Background;
            canvas.FillRectangle(rect);

            var m = owner.Matrix;
            if (m is null || m.Width == 0 || m.Height == 0) return;

            canvas.FillColor = owner.Foreground;

            if (m.Height == 1)
                DrawBars(canvas, rect, m);
            else
                DrawModules(canvas, rect, m);
        }

        private static void DrawBars(ICanvas canvas, RectF rect, BitMatrix m)
        {
            // Whole-pixel module width keeps bars sharp; fall back to fractional only if the code does not fit.
            float scale = MathF.Floor(rect.Width / m.Width);
            if (scale < 1) scale = rect.Width / m.Width;
            float x0 = rect.X + (rect.Width - scale * m.Width) / 2;

            int x = 0;
            while (x < m.Width)
            {
                if (!m[x, 0]) { x++; continue; }
                int start = x;
                while (x < m.Width && m[x, 0]) x++;
                canvas.FillRectangle(x0 + start * scale, rect.Y, (x - start) * scale, rect.Height);
            }
        }

        private static void DrawModules(ICanvas canvas, RectF rect, BitMatrix m)
        {
            float scale = MathF.Floor(MathF.Min(rect.Width / m.Width, rect.Height / m.Height));
            if (scale < 1) scale = MathF.Min(rect.Width / m.Width, rect.Height / m.Height);
            float x0 = rect.X + (rect.Width - scale * m.Width) / 2;
            float y0 = rect.Y + (rect.Height - scale * m.Height) / 2;

            for (int y = 0; y < m.Height; y++)
            {
                int x = 0;
                while (x < m.Width)
                {
                    if (!m[x, y]) { x++; continue; }
                    int start = x;
                    while (x < m.Width && m[x, y]) x++;
                    canvas.FillRectangle(x0 + start * scale, y0 + y * scale, (x - start) * scale, scale);
                }
            }
        }
    }
}
