namespace TagBuddy.Controls;

/// <summary>Draws a slice of raw 125 kHz ADC samples from the Proxmark as a waveform, so the coil's signal is
/// visible: a modulated square wave means a tag is coupling; a flat line means nothing is there. The line is
/// tinted by the last read's result, and the decoded id (if any) is drawn in the corner as a hint.</summary>
public sealed class LfWaveform : IDrawable
{
    public enum Status { None, Signal, Decoded }

    public byte[] Samples { get; set; } = [];
    public Status State { get; set; } = Status.None;
    public string? Hint { get; set; }

    public void Draw(ICanvas canvas, RectF rect)
    {
        canvas.FillColor = Color.FromArgb("#0F172A");
        canvas.FillRectangle(rect);

        var line = State switch
        {
            Status.Decoded => Color.FromArgb("#22C55E"),   // green: an id decoded
            Status.Signal => Color.FromArgb("#F5A623"),    // amber: a tag's signal, but no id
            _ => Color.FromArgb("#475569"),                // grey: flat/no tag
        };

        var s = Samples;
        if (s.Length >= 8)
        {
            // A middle slice, past the field/AGC settling, so the bit pattern (not the ramp-up) is what's shown.
            int off = Math.Min(2000, s.Length / 4);
            int count = Math.Min(600, s.Length - off);
            if (count < 8) { off = 0; count = s.Length; }

            canvas.StrokeColor = line;
            canvas.StrokeSize = 1.4f;
            var path = new PathF();
            int w = Math.Max(2, (int)rect.Width);
            for (int px = 0; px < w; px++)
            {
                int idx = off + (int)((long)px * (count - 1) / (w - 1));
                float y = rect.Height - 2 - (s[idx] / 255f) * (rect.Height - 4);
                if (px == 0) path.MoveTo(px, y); else path.LineTo(px, y);
            }
            canvas.DrawPath(path);
        }

        if (!string.IsNullOrEmpty(Hint))
        {
            canvas.FontColor = line;
            canvas.FontSize = 12;
            canvas.DrawString(Hint, rect.Left + 6, rect.Top + 4, rect.Width - 12, 16,
                HorizontalAlignment.Left, VerticalAlignment.Top);
        }
    }
}
