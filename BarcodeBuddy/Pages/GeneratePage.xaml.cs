using BarcodeBuddy.Models;
using BarcodeBuddy.Services;

namespace BarcodeBuddy.Pages;

[QueryProperty(nameof(IncomingValue), "value")]
[QueryProperty(nameof(IncomingFormat), "format")]
public partial class GeneratePage : ContentPage
{
    private readonly FavoritesService _favorites;
    private bool _valid;

    public string? IncomingValue { get; set; }
    public string? IncomingFormat { get; set; }

    public GeneratePage(FavoritesService favorites)
    {
        InitializeComponent();
        _favorites = favorites;

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

    private BarcodeItem CurrentItem() => new()
    {
        Value = ValueEntry.Text,
        Format = CurrentFormat.Key,
    };

    private void OnFormatChanged(object? sender, EventArgs e)
    {
        HintLabel.Text = CurrentFormat.Hint;
        Refresh();
    }

    private void OnValueChanged(object? sender, TextChangedEventArgs e) => Refresh();

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
        Actions.IsVisible = _valid;
        var isLink = Links.TryGetOpenable(value, out var uri);
        OpenLinkButton.IsVisible = _valid && isLink;
        if (isLink) IconButtons.SetLabel(OpenLinkButton, Links.OpenLabel(uri));

        if (!_valid) return;

        Barcode.Matrix = matrix;
        ValueLabel.Text = value;
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

        if (_favorites.Contains(item.Value, item.Format))
        {
            await DisplayAlertAsync("Already saved", "This barcode is already in your favorites.", "OK");
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
