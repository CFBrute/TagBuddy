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

    public string? IncomingValue { get; set; }
    public string? IncomingErrorCorrection { get; set; }
    public string? IncomingMargin { get; set; }

    public QrPage(FavoritesService favorites)
    {
        InitializeComponent();
        _favorites = favorites;

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
        return new BarcodeItem
        {
            Value = ValueEditor.Text,
            Format = BarcodeTypes.QrCode.Key,
            QrErrorCorrection = options.ErrorCorrection,
            QrMargin = options.Margin,
        };
    }

    private void OnValueChanged(object? sender, TextChangedEventArgs e) => Refresh();

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
        Actions.IsVisible = _valid;
        var isLink = Links.TryGetOpenable(value, out var uri);
        OpenLinkButton.IsVisible = _valid && isLink;
        if (isLink) IconButtons.SetLabel(OpenLinkButton, Links.OpenLabel(uri));

        if (!_valid) return;

        Qr.Matrix = matrix;
        var (version, modules) = BarcodeRenderer.QrInfo(matrix!, options.Margin);
        var bytes = Encoding.UTF8.GetByteCount(value);
        InfoLabel.Text = $"Version {version}  ·  {modules}×{modules} modules  ·  ECC {options.ErrorCorrection}  ·  {bytes} bytes";
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
