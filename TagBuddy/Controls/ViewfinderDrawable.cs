namespace TagBuddy.Controls;

/// <summary>What the viewfinder says about the scan: waiting, something code-like is in view (hold still), or a
/// code was read.</summary>
public enum ViewfinderState { Idle, Seen, Read }

/// <summary>Draws the scanner overlay: the camera picture dimmed except for a centre square, with corner brackets
/// that change colour with <see cref="State"/>. The square shows where to hold the code; the decoder favours it
/// when centre-first decoding is on.</summary>
public sealed class ViewfinderDrawable : IDrawable
{
    /// <summary>Side of the square as a share of the shorter side of the camera area.</summary>
    public const float BoxFraction = 0.62f;

    private const float CornerLength = 28f;
    private const float CornerWidth = 5f;
    private const float PulseGrow = 8f;

    private static readonly Color Shade = Color.FromArgb("#8C000000");
    private static readonly Color IdleColor = Colors.White;
    private static readonly Color SeenColor = Color.FromArgb("#F5A623");
    private static readonly Color ReadColor = Color.FromArgb("#22C55E");

    public ViewfinderState State { get; set; } = ViewfinderState.Idle;

    /// <summary>0..1: how far the square has grown for the "read" flash.</summary>
    public float Pulse { get; set; }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float w = dirtyRect.Width, h = dirtyRect.Height;
        if (w <= 0 || h <= 0) return;

        float side = BoxFraction * Math.Min(w, h);
        float left = dirtyRect.X + (w - side) / 2, top = dirtyRect.Y + (h - side) / 2;
        float right = left + side, bottom = top + side;

        // Dim everything around the square (four rectangles, so the square itself stays clear).
        canvas.FillColor = Shade;
        canvas.FillRectangle(dirtyRect.X, dirtyRect.Y, w, top - dirtyRect.Y);
        canvas.FillRectangle(dirtyRect.X, bottom, w, dirtyRect.Bottom - bottom);
        canvas.FillRectangle(dirtyRect.X, top, left - dirtyRect.X, side);
        canvas.FillRectangle(right, top, dirtyRect.Right - right, side);

        float grow = Pulse * PulseGrow;
        float l = left - grow, t = top - grow, r = right + grow, b = bottom + grow;

        canvas.StrokeColor = State switch
        {
            ViewfinderState.Seen => SeenColor,
            ViewfinderState.Read => ReadColor,
            _ => IdleColor,
        };
        canvas.StrokeSize = CornerWidth;
        canvas.StrokeLineCap = LineCap.Round;

        canvas.DrawLine(l, t, l + CornerLength, t);
        canvas.DrawLine(l, t, l, t + CornerLength);
        canvas.DrawLine(r, t, r - CornerLength, t);
        canvas.DrawLine(r, t, r, t + CornerLength);
        canvas.DrawLine(l, b, l + CornerLength, b);
        canvas.DrawLine(l, b, l, b - CornerLength);
        canvas.DrawLine(r, b, r - CornerLength, b);
        canvas.DrawLine(r, b, r, b - CornerLength);
    }
}
