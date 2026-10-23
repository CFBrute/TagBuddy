using System.Text;
using TagBuddy.Controls;
using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>The QR tab: multi-line content, the code rendered live, and the error-correction and quiet-zone
/// options.</summary>
[QueryProperty(nameof(IncomingValue), AppNavigation.Value)]
[QueryProperty(nameof(IncomingErrorCorrection), AppNavigation.ErrorCorrection)]
[QueryProperty(nameof(IncomingMargin), AppNavigation.Margin)]
public partial class QrPage : CodeGeneratorPage
{
    public string? IncomingValue { get; set; }
    public string? IncomingErrorCorrection { get; set; }
    public string? IncomingMargin { get; set; }

    public QrPage(SavedService saved) : base(saved)
    {
        InitializeComponent();
        EccPicker.ItemsSource = QrOptions.Levels.Select(l => l.Label).ToList();
        _restoring = true;
        var level = QrOptions.Levels.ToList().FindIndex(l => l.Level == GeneratorSettings.QrErrorCorrection);
        EccPicker.SelectedIndex = level >= 0 ? level : QrOptions.Levels.ToList().FindIndex(l => l.Level == QrOptions.DefaultErrorCorrection);
        MarginStepper.Value = Math.Clamp(GeneratorSettings.QrMargin, (int)MarginStepper.Minimum, (int)MarginStepper.Maximum);
        _restoring = false;
        CaptionArea.Remember(GeneratorSettings.Qr);
    }

    /// <summary>Set while the options are put in place, from what was remembered or from a code handed over by
    /// another tab: those are shown, not remembered as the person's own choice. True from the start, because the
    /// margin's starting value in the XAML already raises a change during InitializeComponent.</summary>
    private bool _restoring = true;

    /// <summary>The person changed an option: keep it for next time.</summary>
    private void RememberOptions()
    {
        if (_restoring) return;
        var options = CurrentOptions;
        GeneratorSettings.QrErrorCorrection = options.ErrorCorrection;
        GeneratorSettings.QrMargin = options.Margin;
    }

    protected override InputView ValueInput => ValueEditor;
    protected override Label ErrorText => ErrorLabel;
    protected override View CodeCard => Card;
    protected override PreviewView CodePreview => PreviewImage;
    protected override CaptionSection Caption => CaptionArea;
    protected override View ActionRow => Actions;
    protected override Button OpenLink => OpenLinkButton;
    protected override ScrollView PageScroller => Scroller;
    protected override View BottomPad => BottomSpacer;
    protected override string AlreadySavedMessage => "This QR code is already saved.";

    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (IncomingValue is null) return;

        _restoring = true;
        if (IncomingErrorCorrection is not null)
        {
            var i = QrOptions.Levels.ToList().FindIndex(l => l.Level == IncomingErrorCorrection);
            if (i >= 0) EccPicker.SelectedIndex = i;
        }
        if (int.TryParse(IncomingMargin, out var margin))
            MarginStepper.Value = Math.Clamp(margin, 0, 4);
        _restoring = false;

        ValueEditor.Text = IncomingValue;
        IncomingValue = null;
        IncomingErrorCorrection = null;
        IncomingMargin = null;
    }

    private QrOptions CurrentOptions => new(
        QrOptions.Levels[Math.Max(0, EccPicker.SelectedIndex)].Level,
        (int)MarginStepper.Value);

    protected override SavedItem CurrentCode()
    {
        var options = CurrentOptions;
        return new SavedItem
        {
            Value = Value,
            Format = BarcodeTypes.QrCode.Key,
            QrErrorCorrection = options.ErrorCorrection,
            QrMargin = options.Margin,
        };
    }

    private void OnValueChanged(object? sender, TextChangedEventArgs e)
    {
        ClearButton.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);
        Refresh();
    }

    /// <summary>Starts over: empties the content and puts the cursor back in the editor.</summary>
    private void OnClear(object? sender, EventArgs e)
    {
        CaptionArea.StartOver();
        ValueEditor.Text = string.Empty;
        ValueEditor.Focus();
    }

    private void OnOptionsChanged(object? sender, EventArgs e)
    {
        RememberOptions();
        RefreshKeepingScroll();
    }

    private void OnMarginChanged(object? sender, ValueChangedEventArgs e)
    {
        MarginLabel.Text = $"{(int)e.NewValue} modules";
        RememberOptions();
        RefreshKeepingScroll();
    }

    /// <summary>Re-renders without letting Android scroll back to the focused text field.</summary>
    private void RefreshKeepingScroll()
    {
        var y = Scroller.ScrollY;
        ValueEditor.Unfocus();
        Refresh();
        Dispatcher.Dispatch(async () => await Scroller.ScrollToAsync(0, y, false));
    }

    private void OnToggleAdvanced(object? sender, EventArgs e)
    {
        AdvancedPanel.IsVisible = !AdvancedPanel.IsVisible;
        AdvancedButton.Text = AdvancedPanel.IsVisible ? "Advanced options  ▾" : "Advanced options  ▸";
    }

    private void Refresh()
    {
        var value = Value;
        var options = CurrentOptions;
        string? error = value.Length == 0 ? "Enter some text to generate a QR code." : null;
        var matrix = error is null ? BarcodeRenderer.TryEncode(BarcodeTypes.QrCode, value, options, out error) : null;

        InfoLabel.IsVisible = matrix is not null;
        if (matrix is not null)
        {
            var (version, modules) = BarcodeRenderer.QrInfo(matrix, options.Margin);
            var bytes = Encoding.UTF8.GetByteCount(value);
            InfoLabel.Text = $"Version {version}  ·  {modules}×{modules} modules  ·  ECC {options.ErrorCorrection}  ·  {bytes} bytes";
        }
        ShowCode(matrix is not null, error);
    }
}
