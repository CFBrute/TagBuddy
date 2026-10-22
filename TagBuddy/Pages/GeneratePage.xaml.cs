using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

[QueryProperty(nameof(IncomingValue), "value")]
[QueryProperty(nameof(IncomingFormat), "format")]
public partial class GeneratePage : ContentPage
{
    private readonly SavedService _saved;
    private bool _valid;

    /// <summary>The image text follows the content until the person edits it; then it is theirs.</summary>
    private bool _captionCustom;
    private bool _syncingCaption;
    private readonly CaptionControls _caption;

    public string? IncomingValue { get; set; }
    public string? IncomingFormat { get; set; }

    public GeneratePage(SavedService favorites)
    {
        InitializeComponent();
        _saved = favorites;

        // Before the picker: choosing its index refreshes the page, which already touches the caption controls.
        _caption = new CaptionControls(CaptionEditor,
            [AlignLeftButton, AlignCenterButton, AlignRightButton],
            new CaptionControls.Dial(SizeSlider, SizeMinus, SizePlus, SizeLabel),
            new CaptionControls.Dial(DistanceSlider, DistanceMinus, DistancePlus, DistanceLabel),
            new CaptionControls.Dial(WidthSlider, WidthMinus, WidthPlus, WidthLabel),
            WidthNote, RenderPreview);

        PreviewImage.SizeChanged += OnPreviewSizeChanged;
        FormatPicker.ItemsSource = BarcodeTypes.Linear.Select(f => f.Label).ToList();
        FormatPicker.SelectedIndex = 0;
    }

    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (IncomingValue is null) return;

        var format = BarcodeTypes.Get(IncomingFormat ?? "Code128");
        var index = BarcodeTypes.Linear.ToList().IndexOf(format);
        FormatPicker.SelectedIndex = index < 0 ? 0 : index;
        ValueEntry.Text = IncomingValue;
        IncomingValue = null;
        IncomingFormat = null;
    }

    private FormatInfo CurrentFormat =>
        BarcodeTypes.Linear[Math.Max(0, FormatPicker.SelectedIndex)];

    private SavedItem CurrentItem()
    {
        var item = new SavedItem
        {
            Value = ValueEntry.Text,
            Format = CurrentFormat.Key,
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
        BarcodeImage.CaptionLayout layout;
        Microsoft.Maui.Graphics.IImage? picture;
        try
        {
            (png, layout) = await Task.Run(() => { var bytes = BarcodeImage.RenderPng(item, out var l); return (bytes, l); });
            picture = await Task.Run(() => Controls.PreviewView.Decode(png));
        }
        catch { return; }
        if (generation != _previewGeneration) { picture?.Dispose(); return; }
        _caption.ShowWidthUse(layout);

        // Fix the height before swapping the picture: while the new one loads the image would otherwise
        // collapse to nothing and the page would jump under the finger dragging a slider.
        // Fix the height before swapping the picture so the page does not jump while the new one loads.
        // Before the first layout the image has no width yet; the card is the page width minus its padding.
        var (w, h) = BarcodeImage.PngSize(png);
        double shownWidth = PreviewImage.Width > 0 ? PreviewImage.Width : Width > 0 ? Width - 42 : 0;
        if (w > 0 && shownWidth > 0) PreviewImage.HeightRequest = shownWidth * h / w;
        PreviewImage.Show(picture);
    }

    /// <summary>A taller card pushes the controls below it down the page. While those controls are in use,
    /// scroll by the same amount so a slider stays under the finger and the card's bottom edge, where the
    /// text is, stays in view. Runs once the new height is laid out, so the scroll range already includes it.</summary>
    private void OnPreviewSizeChanged(object? sender, EventArgs e)
    {
        double newHeight = PreviewImage.Height;
        double delta = _previewHeight > 0 ? newHeight - _previewHeight : 0;
        _previewHeight = newHeight;
        if (Math.Abs(delta) < 1 || !_captionOpen || BarcodeCard.Y >= Scroller.ScrollY + Scroller.Height) return;
        Dispatcher.Dispatch(async () => await Scroller.ScrollToAsync(0, Scroller.ScrollY + delta, false));
    }

    private void OnCaptionReset(object? sender, EventArgs e)
    {
        _captionCustom = false;
        CaptionResetButton.IsVisible = false;
        CaptionEditor.Unfocus();
        SyncCaption(ValueEntry.Text ?? string.Empty);
    }

    private void OnFormatChanged(object? sender, EventArgs e)
    {
        HintLabel.Text = CurrentFormat.Hint;
        Refresh();
    }

    private void OnValueChanged(object? sender, TextChangedEventArgs e) => Refresh();

    private bool _captionOpen;

    private void OnToggleCaption(object? sender, EventArgs e)
    {
        _captionOpen = !_captionOpen;
        CaptionPanel.IsVisible = _valid && _captionOpen;
        CaptionToggle.Text = _captionOpen ? "Text under the image  ▾" : "Text under the image  ▸";
        BottomSpacer.HeightRequest = _captionOpen ? 320 : 0;
        CaptionInfoButton.IsVisible = _captionOpen;
    }

    /// <summary>The (i) button, shown only while the section is open: shows or hides the explanation.</summary>
    private void OnToggleCaptionInfo(object? sender, EventArgs e) => CaptionInfo.IsVisible = !CaptionInfo.IsVisible;

    /// <summary>Replaces whatever is in the field with the clipboard text and shows the new barcode.</summary>
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
            ValueEntry.Text = text.Trim();
            ValueEntry.Unfocus();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not paste", ex.Message, "OK");
        }
    }

    private void Refresh()
    {
        var value = ValueEntry.Text ?? string.Empty;
        var format = CurrentFormat;
        var error = BarcodeTypes.Validate(format.Key, value);

        var matrix = error is null ? BarcodeRenderer.TryEncode(format, value, null, out error) : null;

        _valid = matrix is not null;
        ErrorLabel.Text = error ?? string.Empty;
        ErrorLabel.IsVisible = error is not null && value.Length > 0;
        BarcodeCard.IsVisible = _valid;
        CaptionHeader.IsVisible = _valid;
        CaptionPanel.IsVisible = _valid && _captionOpen;
        Actions.IsVisible = _valid;
        SyncCaption(value);
        var isLink = Links.TryGetOpenable(value, out var uri);
        OpenLinkButton.IsVisible = _valid && isLink;
        if (isLink) IconButtons.SetLabel(OpenLinkButton, Links.OpenLabel(uri));

        if (!_valid) { PreviewImage.Clear(); return; }

        RenderPreview();
    }

    private async void OnBarcodeTapped(object? sender, TappedEventArgs e)
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
        if (_valid) await CodeActions.OpenLinkAsync(this, ValueEntry.Text);
    }

    private async void OnAddFavorite(object? sender, EventArgs e)
    {
        if (!_valid) return;
        var item = CurrentItem();

        if (_saved.ContainsCode(item.Value, item.Format))
        {
            await DisplayAlertAsync("Already saved", "This barcode is already saved.", "OK");
            return;
        }

        var name = await DisplayPromptAsync("Save", "Give it a name (optional):",
            accept: "Save", cancel: "Cancel", placeholder: item.Value, maxLength: 60);
        if (name is null) return;

        item.Name = name.Trim();
        await _saved.AddAsync(item);
        Notice.Show("Saved");
    }
}
