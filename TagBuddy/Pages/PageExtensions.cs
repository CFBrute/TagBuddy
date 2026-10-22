using TagBuddy.Models;

namespace TagBuddy.Pages;

/// <summary>The "text under the image" settings shared by the QR and barcode generators: position chosen with
/// a row of buttons; text size, distance and max width with sliders, all as ratios of the code. The editor's
/// alignment follows the position so the field being typed in reads like the image; the page re-renders its
/// preview whenever a setting changes.</summary>
internal sealed class CaptionControls
{
    private static readonly Color Active = Color.FromArgb("#F5A623");
    private static readonly Color ActiveText = Color.FromArgb("#1C1200");
    private static readonly Color Inactive = Color.FromArgb("#6B7280");

    private static readonly string[] Aligns = ["Left", "Center", "Right"];

    /// <summary>One slider with its minus and plus buttons and value label.</summary>
    public sealed record Dial(Slider Slider, Button Minus, Button Plus, Label Label);

    private readonly Editor _editor;
    private readonly Button[] _alignButtons;
    private readonly Dial _size, _distance, _width;
    private readonly Label _widthNote;
    private readonly Action _changed;
    private bool _updating;

    public string Align { get; private set; } = "Center";
    public double TextScale { get; private set; } = SavedItem.DefaultCaptionTextScale;
    public double GapScale { get; private set; } = SavedItem.DefaultCaptionGapScale;
    public double WidthScale { get; private set; } = SavedItem.DefaultCaptionWidthScale;

    public CaptionControls(Editor editor, Button[] alignButtons, Dial size, Dial distance, Dial width, Label widthNote, Action changed)
    {
        _editor = editor;
        _alignButtons = alignButtons;
        _size = size;
        _distance = distance;
        _width = width;
        _widthNote = widthNote;
        _changed = changed;

        for (int i = 0; i < alignButtons.Length; i++)
        {
            var value = Aligns[i];
            alignButtons[i].Clicked += (_, _) => { Align = value; Apply(); };
        }

        Wire(size, SavedItem.MinCaptionTextScale, SavedItem.MaxCaptionTextScale, 0.01, "0.00", () => TextScale, v => TextScale = v);
        Wire(distance, SavedItem.MinCaptionGapScale, SavedItem.MaxCaptionGapScale, 0.01, "0.00", () => GapScale, v => GapScale = v);
        Wire(width, SavedItem.MinCaptionWidthScale, SavedItem.MaxCaptionWidthScale, 0.1, "0.0", () => WidthScale, v => WidthScale = v);
        Apply(notify: false);
    }

    /// <summary>The slider runs 0..1 and is mapped to the value on a curve: the first half of the track covers
    /// only a quarter of the range, so small values, where the eye notices every step, are easy to hit, and
    /// the upper end still reaches the maximum. The buttons move by one exact step.</summary>
    private const double Curve = 2.0;

    private void Wire(Dial dial, double min, double max, double step, string format, Func<double> get, Action<double> set)
    {
        double toValue(double t) => min + (max - min) * Math.Pow(Math.Clamp(t, 0, 1), Curve);
        double toTrack(double v) => Math.Pow(Math.Clamp((v - min) / (max - min), 0, 1), 1 / Curve);
        double snap(double v) => Math.Clamp(Math.Round(v / step) * step, min, max);

        dial.Slider.Minimum = 0;
        dial.Slider.Maximum = 1;
        dial.Slider.Value = toTrack(get());
        dial.Label.Text = get().ToString(format);

        dial.Slider.ValueChanged += (_, e) =>
        {
            if (_updating) return;
            double value = snap(toValue(e.NewValue));
            if (value == get()) return;
            set(value);
            dial.Label.Text = value.ToString(format);
            Apply();
        };

        void nudge(int direction)
        {
            double value = snap(get() + direction * step);
            if (value == get()) return;
            set(value);
            dial.Label.Text = value.ToString(format);
            _updating = true;
            dial.Slider.Value = toTrack(value);
            _updating = false;
            Apply();
        }
        dial.Minus.Clicked += (_, _) => nudge(-1);
        dial.Plus.Clicked += (_, _) => nudge(+1);
    }

    /// <summary>Explains, after a render, whether the max width is doing anything: the limit only matters once
    /// a line wants to be wider than it, which is easy to mistake for a dial that does nothing.</summary>
    public void ShowWidthUse(Services.BarcodeImage.CaptionLayout layout)
    {
        if (layout.TextWidthInCodes <= 0)
        {
            _widthNote.Text = string.Empty;
            _widthNote.IsVisible = false;
            return;
        }
        _widthNote.IsVisible = true;
        _widthNote.Text = layout.LimitReached
            ? $"The text wants {layout.TextWidthInCodes:0.0} code widths, so the limit of {WidthScale:0.0} breaks it into lines."
            : $"The text uses {layout.TextWidthInCodes:0.0} code widths, within the limit of {WidthScale:0.0}. Raising the limit changes nothing until a line needs it.";
    }

    /// <summary>Copies the settings onto an item about to be previewed, shared, saved or exported.</summary>
    public void CopyTo(SavedItem item)
    {
        item.CaptionAlign = Align;
        item.CaptionTextScale = TextScale;
        item.CaptionGapScale = GapScale;
        item.CaptionWidthScale = WidthScale;
    }

    private void Apply(bool notify = true)
    {
        for (int i = 0; i < _alignButtons.Length; i++)
        {
            bool on = Aligns[i] == Align;
            _alignButtons[i].BackgroundColor = on ? Active : Inactive;
            _alignButtons[i].TextColor = on ? ActiveText : Colors.White;
        }
        _editor.HorizontalTextAlignment = Align switch
        {
            "Left" => TextAlignment.Start,
            "Right" => TextAlignment.End,
            _ => TextAlignment.Center,
        };
        if (notify) _changed();
    }
}

internal static class IconButtons
{
    /// <summary>Updates the long-press tooltip and accessibility label of an icon-only button.</summary>
    public static void SetLabel(Button button, string label)
    {
        ToolTipProperties.SetText(button, label);
        SemanticProperties.SetDescription(button, label);
    }
}
