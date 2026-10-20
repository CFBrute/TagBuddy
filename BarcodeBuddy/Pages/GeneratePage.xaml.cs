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

        FormatPicker.ItemsSource = BarcodeTypes.All.Select(f => f.Label).ToList();
        FormatPicker.SelectedIndex = 0;
    }

    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (IncomingValue is null) return;

        var format = BarcodeTypes.Get(IncomingFormat ?? "Code128");
        FormatPicker.SelectedIndex = BarcodeTypes.All.ToList().IndexOf(format);
        ValueEntry.Text = IncomingValue;
        IncomingValue = null;
        IncomingFormat = null;
    }

    private FormatInfo CurrentFormat =>
        BarcodeTypes.All[Math.Max(0, FormatPicker.SelectedIndex)];

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

        _valid = error is null;
        ErrorLabel.Text = error ?? string.Empty;
        ErrorLabel.IsVisible = error is not null && value.Length > 0;
        FavoriteButton.IsEnabled = _valid;
        BarcodeCard.IsVisible = _valid;

        if (!_valid) return;

        Generator.Format = format.ZxingFormat;
        // Linear codes get a wide strip; 2D codes such as QR get a square.
        Generator.HeightRequest = format.IsLinear ? 140 : 260;
        Generator.WidthRequest = format.IsLinear ? -1 : 260;
        Generator.HorizontalOptions = format.IsLinear ? LayoutOptions.Fill : LayoutOptions.Center;
        Generator.Value = value;
        ValueLabel.Text = value;
    }

    private async void OnBarcodeTapped(object? sender, TappedEventArgs e)
    {
        if (!_valid) return;
        await ShowBarcodePage.ShowAsync(new BarcodeItem
        {
            Value = ValueEntry.Text,
            Format = CurrentFormat.Key,
        });
    }

    private async void OnAddFavorite(object? sender, EventArgs e)
    {
        if (!_valid) return;
        var value = ValueEntry.Text;
        var format = CurrentFormat;

        if (_favorites.Contains(value, format.Key))
        {
            await DisplayAlertAsync("Already saved", "This barcode is already in your favorites.", "OK");
            return;
        }

        var name = await DisplayPromptAsync("Add to favorites", "Give it a name (optional):",
            accept: "Save", cancel: "Cancel", placeholder: value, maxLength: 60);
        if (name is null) return;

        await _favorites.AddAsync(new BarcodeItem
        {
            Name = name.Trim(),
            Value = value,
            Format = format.Key,
        });

        await DisplayAlertAsync("Saved to favorites", null, "OK");
    }

    private async void OnCopy(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(ValueEntry.Text)) return;
        await Clipboard.Default.SetTextAsync(ValueEntry.Text);
        await DisplayAlertAsync("Copied", null, "OK");
    }
}
