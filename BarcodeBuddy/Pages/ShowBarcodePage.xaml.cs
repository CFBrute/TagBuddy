using BarcodeBuddy.Models;
using BarcodeBuddy.Services;

namespace BarcodeBuddy.Pages;

public partial class ShowBarcodePage : ContentPage, IQueryAttributable
{
    public const string Route = "show";

    private BarcodeItem? _item;

    public ShowBarcodePage()
    {
        InitializeComponent();
    }

    public static Task ShowAsync(BarcodeItem item) =>
        Shell.Current.GoToAsync(Route, new Dictionary<string, object> { ["item"] = item });

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("item", out var obj) && obj is BarcodeItem item)
        {
            _item = item;
            var format = BarcodeTypes.Get(item.Format);
            Barcode.Matrix = BarcodeRenderer.TryEncode(item, out _);
            if (format.IsLinear)
            {
                Barcode.HeightRequest = 220;
                Barcode.WidthRequest = -1;
                Barcode.HorizontalOptions = LayoutOptions.Fill;
            }
            else
            {
                Barcode.HeightRequest = 340;
                Barcode.WidthRequest = 340;
                Barcode.HorizontalOptions = LayoutOptions.Center;
            }
            ValueLabel.Text = item.Value;
            NameLabel.Text = string.IsNullOrWhiteSpace(item.Name) ? format.Label : $"{item.Name}  -  {format.Label}";

            OpenLinkButton.IsVisible = Links.TryGetOpenable(item.Value, out var uri);
            if (OpenLinkButton.IsVisible) IconButtons.SetLabel(OpenLinkButton, Links.OpenLabel(uri));
            Preview.Value = item.Value;
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ScreenBrightness.SetMax();
        DeviceDisplay.Current.KeepScreenOn = true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ScreenBrightness.Restore();
        DeviceDisplay.Current.KeepScreenOn = false;
    }

    private async void OnTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync("..");

    private async void OnShare(object? sender, EventArgs e)
    {
        if (_item is not null) await CodeActions.ShareImageAsync(this, _item);
    }

    private async void OnSaveImage(object? sender, EventArgs e)
    {
        if (_item is not null) await CodeActions.SaveImageAsync(this, _item);
    }

    private async void OnOpenLink(object? sender, EventArgs e)
    {
        if (_item is not null) await CodeActions.OpenLinkAsync(this, _item.Value);
    }
}

/// <summary>Pushes the screen to full brightness so scanners can read the code reliably.</summary>
static class ScreenBrightness
{
    public static void SetMax() => Set(1f);

    // -1 means "follow the system brightness setting".
    public static void Restore() => Set(-1f);

    private static void Set(float brightness)
    {
#if ANDROID
        var window = Platform.CurrentActivity?.Window;
        if (window?.Attributes is null) return;
        var attrs = window.Attributes;
        attrs.ScreenBrightness = brightness;
        window.Attributes = attrs;
#endif
    }
}
