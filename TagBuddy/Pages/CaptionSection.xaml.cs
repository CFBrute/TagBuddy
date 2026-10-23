using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>The "text under the image" section shared by the QR and barcode generators: the text itself, its
/// position chosen with a row of buttons, and text size, distance and max width with dials, all as ratios of
/// the code. The text follows the code's content (<see cref="Follow"/>) until the person edits it; then it is
/// theirs until they tap the reset button. The editor's alignment follows the position so the field being
/// typed in reads like the image. <see cref="Changed"/> asks the page to render its preview again.</summary>
public partial class CaptionSection : ContentView
{
    private static readonly string[] Aligns = ["Left", "Center", "Right"];

    /// <summary>The slider runs 0..1 and is mapped to the value on a curve: the first half of the track covers
    /// only a quarter of the range, so small values, where the eye notices every step, are easy to hit, and
    /// the upper end still reaches the maximum. The buttons move by one exact step.</summary>
    private const double Curve = 2.0;

    private readonly Button[] _alignButtons;
    private bool _updatingDial;

    /// <summary>Puts each dial's slider and label back in step with its value, after <see cref="Remember"/> loads one.</summary>
    private readonly List<Action> _refreshDials = [];

    /// <summary>Which generator's settings this section keeps (<see cref="GeneratorSettings"/>); null keeps nothing.</summary>
    private string? _settingsKey;
    private bool _restoring;

    /// <summary>The person edited the text, so it no longer follows the content.</summary>
    private bool _custom;
    private bool _following;
    private string _content = string.Empty;

    /// <summary>A setting or the text changed: the image needs rendering again.</summary>
    public event EventHandler? Changed;

    /// <summary>The section was opened or closed.</summary>
    public event EventHandler? OpenChanged;

    public bool IsOpen { get; private set; }
    public string Align { get; private set; } = "Center";
    public double TextScale { get; private set; } = SavedItem.DefaultCaptionTextScale;
    public double GapScale { get; private set; } = SavedItem.DefaultCaptionGapScale;
    public double WidthScale { get; private set; } = SavedItem.DefaultCaptionWidthScale;
    public string Font { get; private set; } = CaptionFonts.Default;

    /// <summary>Label of the button that makes the text follow the content again ("Same as text").</summary>
    public string ResetText
    {
        get => CaptionResetButton.Text;
        set => CaptionResetButton.Text = value;
    }

    public CaptionSection()
    {
        InitializeComponent();
        ResetText = "Same as content";

        _alignButtons = [AlignLeftButton, AlignCenterButton, AlignRightButton];
        for (int i = 0; i < _alignButtons.Length; i++)
        {
            var value = Aligns[i];
            _alignButtons[i].Clicked += (_, _) => { Align = value; ApplyAlign(); SettingChanged(); };
        }

        Wire(SizeSlider, SizeMinus, SizePlus, SizeLabel, SavedItem.MinCaptionTextScale, SavedItem.MaxCaptionTextScale,
            0.01, "0.00", () => TextScale, v => TextScale = v);
        Wire(DistanceSlider, DistanceMinus, DistancePlus, DistanceLabel, SavedItem.MinCaptionGapScale, SavedItem.MaxCaptionGapScale,
            0.01, "0.00", () => GapScale, v => GapScale = v);
        Wire(WidthSlider, WidthMinus, WidthPlus, WidthLabel, SavedItem.MinCaptionWidthScale, SavedItem.MaxCaptionWidthScale,
            0.1, "0.0", () => WidthScale, v => WidthScale = v);
        ApplyAlign();

        _restoring = true;
        FontPicker.ItemsSource = CaptionFonts.All.Select(f => f.Label).ToList();
        FontPicker.SelectedIndex = 0;
        _restoring = false;
        ApplyFont();
    }

    /// <summary>Opens with the settings this generator was last left with, and keeps every later change to them.
    /// Called once by the page, with <see cref="GeneratorSettings.Qr"/> or <see cref="GeneratorSettings.Barcode"/>.</summary>
    public void Remember(string generator)
    {
        var saved = GeneratorSettings.LoadCaption(generator);
        Align = saved.Align;
        TextScale = saved.TextScale;
        GapScale = saved.GapScale;
        WidthScale = saved.WidthScale;
        Font = saved.Font;

        _restoring = true;
        ApplyAlign();
        FontPicker.SelectedIndex = CaptionFonts.All.ToList().FindIndex(f => f.Key == Font);
        foreach (var refresh in _refreshDials) refresh();
        _restoring = false;
        ApplyFont();
        _settingsKey = generator;
    }

    /// <summary>The code's content changed. The text takes it over unless the person has edited the text. No
    /// <see cref="Changed"/> is raised: the page renders anyway when its content changes.</summary>
    public void Follow(string content)
    {
        _content = content;
        if (_custom) return;
        _following = true;
        CaptionEditor.Text = content;
        _following = false;
    }

    /// <summary>A fresh code: the text follows the content again.</summary>
    public void StartOver()
    {
        _custom = false;
        CaptionResetButton.IsVisible = false;
    }

    /// <summary>Copies the text and settings onto an item about to be previewed, shared, saved or exported.</summary>
    public void ApplyTo(SavedItem item)
    {
        item.Caption = _custom ? CaptionEditor.Text ?? string.Empty : null;
        item.CaptionAlign = Align;
        item.CaptionTextScale = TextScale;
        item.CaptionGapScale = GapScale;
        item.CaptionWidthScale = WidthScale;
        item.CaptionFont = Font;
    }

    /// <summary>Explains, after a render, whether the max width is doing anything: the limit only matters once
    /// a line wants to be wider than it, which is easy to mistake for a dial that does nothing.</summary>
    public void ShowWidthUse(BarcodeImage.CaptionLayout layout)
    {
        if (layout.TextWidthInCodes <= 0)
        {
            WidthNote.Text = string.Empty;
            WidthNote.IsVisible = false;
            return;
        }
        WidthNote.IsVisible = true;
        WidthNote.Text = layout.LimitReached
            ? $"The text wants {layout.TextWidthInCodes:0.0} code widths, so the limit of {WidthScale:0.0} breaks it into lines."
            : $"The text uses {layout.TextWidthInCodes:0.0} code widths, within the limit of {WidthScale:0.0}. Raising the limit changes nothing until a line needs it.";
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_following) return;
        _custom = true;
        CaptionResetButton.IsVisible = true;
        RaiseChanged();
    }

    private void OnReset(object? sender, EventArgs e)
    {
        StartOver();
        CaptionEditor.Unfocus();
        Follow(_content);
        RaiseChanged();   // the image still shows the edited text
    }

    private void OnToggle(object? sender, EventArgs e)
    {
        IsOpen = !IsOpen;
        CaptionPanel.IsVisible = IsOpen;
        CaptionInfoButton.IsVisible = IsOpen;
        CaptionToggle.Text = IsOpen ? "Text under the image  ▾" : "Text under the image  ▸";
        OpenChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The (i) button, shown only while the section is open: shows or hides the explanation.</summary>
    private void OnToggleInfo(object? sender, EventArgs e) => CaptionInfo.IsVisible = !CaptionInfo.IsVisible;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>A setting (not the text) changed: keep it for next time, and render again.</summary>
    private void SettingChanged()
    {
        if (_restoring) return;
        if (_settingsKey is not null)
            GeneratorSettings.SaveCaption(_settingsKey, new GeneratorSettings.Caption(Align, TextScale, GapScale, WidthScale, Font));
        RaiseChanged();
    }

    private void OnFontChanged(object? sender, EventArgs e)
    {
        if (_restoring || FontPicker.SelectedIndex < 0) return;
        Font = CaptionFonts.All[FontPicker.SelectedIndex].Key;
        ApplyFont();
        SettingChanged();
    }

    /// <summary>The editor shows the text in the chosen font, so it reads like the image.</summary>
    private void ApplyFont()
    {
        var font = CaptionFonts.Get(Font);
        CaptionEditor.FontFamily = font.Family;
        CaptionEditor.FontAttributes = font.Bold ? FontAttributes.Bold : FontAttributes.None;
    }

    /// <summary>One dial: a slider with its minus and plus buttons and value label.</summary>
    private void Wire(Slider slider, Button minus, Button plus, Label label,
        double min, double max, double step, string format, Func<double> get, Action<double> set)
    {
        double toValue(double t) => min + (max - min) * Math.Pow(Math.Clamp(t, 0, 1), Curve);
        double toTrack(double v) => Math.Pow(Math.Clamp((v - min) / (max - min), 0, 1), 1 / Curve);
        double snap(double v) => Math.Clamp(Math.Round(v / step) * step, min, max);

        slider.Minimum = 0;
        slider.Maximum = 1;
        slider.Value = toTrack(get());
        label.Text = get().ToString(format);
        _refreshDials.Add(() =>
        {
            _updatingDial = true;
            slider.Value = toTrack(get());
            _updatingDial = false;
            label.Text = get().ToString(format);
        });

        slider.ValueChanged += (_, e) =>
        {
            if (_updatingDial) return;
            double value = snap(toValue(e.NewValue));
            if (value == get()) return;
            set(value);
            label.Text = value.ToString(format);
            SettingChanged();
        };

        void nudge(int direction)
        {
            double value = snap(get() + direction * step);
            if (value == get()) return;
            set(value);
            label.Text = value.ToString(format);
            _updatingDial = true;
            slider.Value = toTrack(value);
            _updatingDial = false;
            SettingChanged();
        }
        minus.Clicked += (_, _) => nudge(-1);
        plus.Clicked += (_, _) => nudge(+1);
    }

    private void ApplyAlign()
    {
        for (int i = 0; i < _alignButtons.Length; i++)
            Palette.Select(_alignButtons[i], Aligns[i] == Align);
        CaptionEditor.HorizontalTextAlignment = Align switch
        {
            "Left" => TextAlignment.Start,
            "Right" => TextAlignment.End,
            _ => TextAlignment.Center,
        };
    }
}
