using System.Text;
using BarcodeBuddy.Models;
using BarcodeBuddy.Services;

namespace BarcodeBuddy.Pages;

[QueryProperty(nameof(IncomingValue), "value")]
[QueryProperty(nameof(IncomingErrorCorrection), "ecc")]
[QueryProperty(nameof(IncomingMargin), "margin")]
public partial class QrPage : ContentPage
{
    private readonly FavoritesService _favorites;
    private bool _valid;

    /// <summary>The image text follows the content until the person edits it; then it is theirs.</summary>
    private bool _captionCustom;
    private bool _syncingCaption;
    private readonly CaptionControls _caption;

    public string? IncomingValue { get; set; }
    public string? IncomingErrorCorrection { get; set; }
    public string? IncomingMargin { get; set; }

    public QrPage(FavoritesService favorites)
    {
        InitializeComponent();
        _favorites = favorites;

        // Before the picker: choosing its index refreshes the page, which already touches the caption controls.
        _caption = new CaptionControls(CaptionEditor,
            [AlignLeftButton, AlignCenterButton, AlignRightButton],
            SizeSlider, SizeLabel, DistanceSlider, DistanceLabel, RenderPreview);

        PreviewImage.SizeChanged += OnPreviewSizeChanged;
        EccPicker.ItemsSource = QrOptions.Levels.Select(l => l.Label).ToList();
        EccPicker.SelectedIndex = QrOptions.Levels.ToList().FindIndex(l => l.Level == QrOptions.DefaultErrorCorrection);
    }

    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (IncomingValue is null) return;

        if (IncomingErrorCorrection is not null)
        {
            var i = QrOptions.Levels.ToList().FindIndex(l => l.Level == IncomingErrorCorrection);
            if (i >= 0) EccPicker.SelectedIndex = i;
        }
        if (int.TryParse(IncomingMargin, out var margin))
            MarginStepper.Value = Math.Clamp(margin, 0, 4);

        ValueEditor.Text = IncomingValue;
        IncomingValue = null;
        IncomingErrorCorrection = null;
        IncomingMargin = null;
    }

    private QrOptions CurrentOptions => new(
        QrOptions.Levels[Math.Max(0, EccPicker.SelectedIndex)].Level,
        (int)MarginStepper.Value);

    private BarcodeItem CurrentItem()
    {
        var options = CurrentOptions;
        var item = new BarcodeItem
        {
            Value = ValueEditor.Text,
            Format = BarcodeTypes.QrCode.Key,
            QrErrorCorrection = options.ErrorCorrection,
            QrMargin = options.Margin,
            Caption = _captionCustom ? CaptionEditor.Text ?? string.Empty : null,
        };
        _caption.CopyTo(item);
        return item;
    }

    private void SyncCaption(string value)
    {
        if (_captionCustom) return;
        _syncingCaption = true;
        CaptionEditor.Text = value;
        _syncingCaption = false;
    }

    private void OnCaptionChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingCaption) return;   // Refresh is running and renders the preview itself
        _captionCustom = true;
        CaptionResetButton.IsVisible = true;
        RenderPreview();
    }

    /// <summary>Renders the exported image off the UI thread and shows it in the card. A newer request
    /// cancels the display of an older one, so fast typing never shows a stale picture.</summary>
    private int _previewGeneration;
    private double _previewHeight;   // the card's last laid-out height

    private async void RenderPreview()
    {
        if (!_valid) return;
        var item = CurrentItem();
        int generation = ++_previewGeneration;
        byte[] png;
        try { png = await Task.Run(() => BarcodeImage.RenderPng(item)); }
        catch { return; }
        if (generation != _previewGeneration) return;

        // Fix the height before swapping the picture: while the new one loads the image would otherwise
        // collapse to nothing and the page would jump under the finger dragging a slider.
        var (w, h) = BarcodeImage.PngSize(png);
        if (w > 0 && PreviewImage.Width > 0)
        {
            PreviewImage.HeightRequest = PreviewImage.Width * h / w;
        }
        PreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(png));
    }

    /// <summary>A taller card pushes the controls below it down the page. While those controls are in use,
    /// scroll by the same amount so a slider stays under the finger and the card's bottom edge, where the
    /// text is, stays in view. Runs once the new height is laid out, so the scroll range already includes it.</summary>
    private void OnPreviewSizeChanged(object? sender, EventArgs e)
    {
        double newHeight = PreviewImage.Height;
        double delta = _previewHeight > 0 ? newHeight - _previewHeight : 0;
        _previewHeight = newHeight;
        if (Math.Abs(delta) < 1 || !_captionOpen || QrCard.Y >= Scroller.ScrollY + Scroller.Height) return;
        Dispatcher.Dispatch(async () => await Scroller.ScrollToAsync(0, Scroller.ScrollY + delta, false));
    }

    private void OnCaptionReset(object? sender, EventArgs e)
    {
        _captionCustom = false;
        CaptionResetButton.IsVisible = false;
        CaptionEditor.Unfocus();
        SyncCaption(ValueEditor.Text ?? string.Empty);
    }

    private void OnValueChanged(object? sender, TextChangedEventArgs e)
    {
        ClearButton.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);
        Refresh();
    }

    /// <summary>Replaces whatever is in the editor with the clipboard text and shows the new code.</summary>
    private async void OnPaste(object? sender, EventArgs e)
    {
        try
        {
            var text = await Clipboard.Default.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                await DisplayAlertAsync("Nothing to paste", "The clipboard has no text.", "OK");
                return;
            }
            ValueEditor.Text = text.Trim();
            ValueEditor.Unfocus();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not paste", ex.Message, "OK");
        }
    }

    /// <summary>Starts over: empties the content and puts the cursor back in the editor.</summary>
    private void OnClear(object? sender, EventArgs e)
    {
        _captionCustom = false;
        CaptionResetButton.IsVisible = false;
        ValueEditor.Text = string.Empty;
        ValueEditor.Focus();
    }

    private void OnOptionsChanged(object? sender, EventArgs e) => RefreshKeepingScroll();

    private void OnMarginChanged(object? sender, ValueChangedEventArgs e)
    {
        MarginLabel.Text = $"{(int)e.NewValue} modules";
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

    private bool _captionOpen;

    private void OnToggleCaption(object? sender, EventArgs e)
    {
        _captionOpen = !_captionOpen;
        CaptionPanel.IsVisible = _valid && _captionOpen;
        CaptionToggle.Text = _captionOpen ? "Text under the image  ▾" : "Text under the image  ▸";
    }

    private void OnToggleAdvanced(object? sender, EventArgs e)
    {
        AdvancedPanel.IsVisible = !AdvancedPanel.IsVisible;
        AdvancedButton.Text = AdvancedPanel.IsVisible ? "Advanced options  ▾" : "Advanced options  ▸";
    }

    private void Refresh()
    {
        var value = ValueEditor.Text ?? string.Empty;
        var options = CurrentOptions;
        string? error = value.Length == 0 ? "Enter some text to generate a QR code." : null;

        var matrix = error is null ? BarcodeRenderer.TryEncode(BarcodeTypes.QrCode, value, options, out error) : null;

        _valid = matrix is not null;
        ErrorLabel.Text = error ?? string.Empty;
        ErrorLabel.IsVisible = error is not null && value.Length > 0;
        QrCard.IsVisible = _valid;
        InfoLabel.IsVisible = _valid;
        CaptionHeader.IsVisible = _valid;
        CaptionPanel.IsVisible = _valid && _captionOpen;
        Actions.IsVisible = _valid;
        SyncCaption(value);
        var isLink = Links.TryGetOpenable(value, out var uri);
        OpenLinkButton.IsVisible = _valid && isLink;
        if (isLink) IconButtons.SetLabel(OpenLinkButton, Links.OpenLabel(uri));

        if (!_valid) { PreviewImage.Source = null; return; }

        var (version, modules) = BarcodeRenderer.QrInfo(matrix!, options.Margin);
        var bytes = Encoding.UTF8.GetByteCount(value);
        InfoLabel.Text = $"Version {version}  ·  {modules}×{modules} modules  ·  ECC {options.ErrorCorrection}  ·  {bytes} bytes";
        RenderPreview();
    }

    private async void OnQrTapped(object? sender, TappedEventArgs e)
    {
        if (_valid) await ShowBarcodePage.ShowAsync(CurrentItem());
    }

    private async void OnShare(object? sender, EventArgs e)
    {
        if (_valid) await CodeActions.ShareImageAsync(this, CurrentItem());
    }

    private async void OnSaveImage(object? sender, EventArgs e)
    {
        if (_valid) await CodeActions.SaveImageAsync(this, CurrentItem());
    }

    private async void OnOpenLink(object? sender, EventArgs e)
    {
        if (_valid) await CodeActions.OpenLinkAsync(this, ValueEditor.Text);
    }

    private async void OnAddFavorite(object? sender, EventArgs e)
    {
        if (!_valid) return;
        var item = CurrentItem();

        if (_favorites.Contains(item.Value, item.Format))
        {
            await DisplayAlertAsync("Already saved", "This QR code is already in your favorites.", "OK");
            return;
        }

        var name = await DisplayPromptAsync("Add to favorites", "Give it a name (optional):",
            accept: "Save", cancel: "Cancel", placeholder: item.Value, maxLength: 60);
        if (name is null) return;

        item.Name = name.Trim();
        await _favorites.AddAsync(item);
        await DisplayAlertAsync("Saved to favorites", null, "OK");
    }
}
