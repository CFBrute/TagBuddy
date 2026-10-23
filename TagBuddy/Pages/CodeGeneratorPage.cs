using TagBuddy.Controls;
using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>What the QR and barcode tabs have in common: the live preview of the exported image, the "text under
/// the image" section, Paste, and the save / share / open actions. A tab supplies its controls and the code it
/// currently shows, and calls <see cref="ShowCode"/> whenever its content or options change.</summary>
public abstract class CodeGeneratorPage : ContentPage
{
    protected readonly SavedService Saved;

    /// <summary>The current content makes a valid code; the actions only work then.</summary>
    protected bool IsValid { get; private set; }

    /// <summary>A newer render cancels the display of an older one, so fast typing never shows a stale picture.</summary>
    private int _previewGeneration;
    private double _previewHeight;   // the card's last laid-out height

    protected CodeGeneratorPage(SavedService saved)
    {
        Saved = saved;
    }

    // The controls of the tab, named in its XAML.
    protected abstract InputView ValueInput { get; }
    protected abstract Label ErrorText { get; }
    protected abstract View CodeCard { get; }
    protected abstract PreviewView CodePreview { get; }
    protected abstract CaptionSection Caption { get; }
    protected abstract View ActionRow { get; }
    protected abstract Button OpenLink { get; }
    protected abstract ScrollView PageScroller { get; }
    protected abstract View BottomPad { get; }

    /// <summary>The code on show, without its image text (<see cref="CurrentItem"/> adds that).</summary>
    protected abstract SavedItem CurrentCode();

    /// <summary>Said when the code is already in Saved: "This QR code is already saved."</summary>
    protected abstract string AlreadySavedMessage { get; }

    protected string Value => ValueInput.Text ?? string.Empty;

    protected SavedItem CurrentItem()
    {
        var item = CurrentCode();
        Caption.ApplyTo(item);
        return item;
    }

    /// <summary>Shows the outcome of encoding the current content: the error, or the code with its actions.</summary>
    protected void ShowCode(bool valid, string? error)
    {
        var value = Value;
        IsValid = valid;
        ErrorText.Text = error ?? string.Empty;
        ErrorText.IsVisible = error is not null && value.Length > 0;
        CodeCard.IsVisible = valid;
        Caption.IsVisible = valid;
        ActionRow.IsVisible = valid;
        Caption.Follow(value);

        var canOpen = Handoff.TryDetect(value, out var handoff);
        OpenLink.IsVisible = valid && canOpen;
        if (canOpen) IconButtons.SetLabel(OpenLink, HandoffLauncher.Label(handoff!));

        if (valid) RenderPreview();
        else CodePreview.Clear();
    }

    /// <summary>Renders the exported image off the UI thread and shows it in the card, so the preview is exactly
    /// what gets shared.</summary>
    protected async void RenderPreview()
    {
        if (!IsValid) return;
        var item = CurrentItem();
        int generation = ++_previewGeneration;
        byte[] png;
        BarcodeImage.CaptionLayout layout;
        Microsoft.Maui.Graphics.IImage? picture;
        try
        {
            (png, layout) = await Task.Run(() => { var bytes = BarcodeImage.RenderPng(item, out var l); return (bytes, l); });
            picture = await Task.Run(() => PreviewView.Decode(png));
        }
        catch { return; }
        if (generation != _previewGeneration) { picture?.Dispose(); return; }
        Caption.ShowWidthUse(layout);

        // Fix the height before swapping the picture: while the new one loads the image would otherwise
        // collapse to nothing and the page would jump under the finger dragging a slider.
        // Before the first layout the image has no width yet; the card is the page width minus its padding.
        var (w, h) = BarcodeImage.PngSize(png);
        double shownWidth = CodePreview.Width > 0 ? CodePreview.Width : Width > 0 ? Width - 42 : 0;
        if (w > 0 && shownWidth > 0) CodePreview.HeightRequest = shownWidth * h / w;
        CodePreview.Show(picture);
    }

    /// <summary>A taller card pushes the controls below it down the page. While those controls are in use,
    /// scroll by the same amount so a slider stays under the finger and the card's bottom edge, where the
    /// text is, stays in view. Runs once the new height is laid out, so the scroll range already includes it.</summary>
    protected void OnPreviewSizeChanged(object? sender, EventArgs e)
    {
        double newHeight = CodePreview.Height;
        double delta = _previewHeight > 0 ? newHeight - _previewHeight : 0;
        _previewHeight = newHeight;
        if (Math.Abs(delta) < 1 || !Caption.IsOpen || CodeCard.Y >= PageScroller.ScrollY + PageScroller.Height) return;
        Dispatcher.Dispatch(async () => await PageScroller.ScrollToAsync(0, PageScroller.ScrollY + delta, false));
    }

    protected void OnCaptionChanged(object? sender, EventArgs e) => RenderPreview();

    /// <summary>Spare room at the end of the page while the caption section is open: when the card shrinks the
    /// page would otherwise run out of scroll range and the controls would be pushed down under the finger.</summary>
    protected void OnCaptionOpenChanged(object? sender, EventArgs e) =>
        BottomPad.HeightRequest = Caption.IsOpen ? 320 : 0;

    /// <summary>Replaces whatever is in the field with the clipboard text and shows the new code.</summary>
    protected async void OnPaste(object? sender, EventArgs e)
    {
        try
        {
            var text = await Clipboard.Default.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                Notice.Show("Nothing to paste: the clipboard has no text");
                return;
            }
            ValueInput.Text = text.Trim();
            ValueInput.Unfocus();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not paste", ex.Message, "OK");
        }
    }

    protected async void OnCodeTapped(object? sender, TappedEventArgs e)
    {
        if (IsValid) await ShowBarcodePage.ShowAsync(CurrentItem());
    }

    protected async void OnShare(object? sender, EventArgs e)
    {
        if (IsValid) await CodeActions.ShareImageAsync(this, CurrentItem());
    }

    protected async void OnSaveImage(object? sender, EventArgs e)
    {
        if (IsValid) await CodeActions.SaveImageAsync(this, CurrentItem());
    }

    protected async void OnOpenLink(object? sender, EventArgs e)
    {
        if (IsValid) await CodeActions.OpenLinkAsync(this, Value);
    }

    protected async void OnAddFavorite(object? sender, EventArgs e)
    {
        if (IsValid) await CodeActions.SaveCodeAsync(this, Saved, CurrentItem(), AlreadySavedMessage);
    }
}
