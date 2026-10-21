using BarcodeBuddy.Models;

namespace BarcodeBuddy.Pages;

/// <summary>The "text under the image" settings shared by the QR and barcode generators: position chosen with
/// a row of buttons, text size and distance with sliders. The editor's alignment follows the position so the
/// field being typed in reads like the image; the page re-renders its preview whenever a setting changes.</summary>
internal sealed class CaptionControls
{
    private static readonly Color Active = Color.FromArgb("#F5A623");
    private static readonly Color ActiveText = Color.FromArgb("#1C1200");
    private static readonly Color Inactive = Color.FromArgb("#6B7280");

    private static readonly string[] Aligns = ["Left", "Center", "Right"];

    private readonly Editor _editor;
    private readonly Button[] _alignButtons;
    private readonly Label _sizeLabel, _distanceLabel;
    private readonly Action _changed;

    public string Align { get; private set; } = "Center";
    public int TextSize { get; private set; } = BarcodeItem.DefaultCaptionTextSize;
    public int Distance { get; private set; } = BarcodeItem.DefaultCaptionDistance;

    public CaptionControls(Editor editor, Button[] alignButtons,
        Slider size, Label sizeLabel, Slider distance, Label distanceLabel, Action changed)
    {
        _editor = editor;
        _alignButtons = alignButtons;
        _sizeLabel = sizeLabel;
        _distanceLabel = distanceLabel;
        _changed = changed;

        for (int i = 0; i < alignButtons.Length; i++)
        {
            var value = Aligns[i];
            alignButtons[i].Clicked += (_, _) => { Align = value; Apply(); };
        }

        WireSlider(size, BarcodeItem.MinCaptionTextSize, BarcodeItem.MaxCaptionTextSize, TextSize, v => TextSize = v);
        WireSlider(distance, 1, BarcodeItem.MaxCaptionDistance, Distance, v => Distance = v);
        Apply(notify: false);
    }

    private void WireSlider(Slider slider, int min, int max, int initial, Action<int> set)
    {
        slider.Minimum = min;
        slider.Maximum = max;
        slider.Value = initial;
        slider.ValueChanged += (_, e) =>
        {
            int value = (int)Math.Round(e.NewValue);
            set(value);
            Apply();
        };
    }

    /// <summary>Copies the settings onto an item about to be previewed, shared, saved or exported.</summary>
    public void CopyTo(BarcodeItem item)
    {
        item.CaptionAlign = Align;
        item.CaptionTextSize = TextSize;
        item.CaptionDistance = Distance;
    }

    private void Apply(bool notify = true)
    {
        for (int i = 0; i < _alignButtons.Length; i++)
        {
            bool on = Aligns[i] == Align;
            _alignButtons[i].BackgroundColor = on ? Active : Inactive;
            _alignButtons[i].TextColor = on ? ActiveText : Colors.White;
        }
        _sizeLabel.Text = $"{TextSize} px";
        _distanceLabel.Text = $"{Distance} px";
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
