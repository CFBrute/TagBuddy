using BarcodeBuddy.Models;

namespace BarcodeBuddy.Pages;

public partial class ShowBarcodePage : ContentPage, IQueryAttributable
{
    public const string Route = "show";

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
            var format = BarcodeTypes.Get(item.Format);
            Generator.Format = format.ZxingFormat;
            Generator.HeightRequest = format.IsLinear ? 220 : 340;
            Generator.WidthRequest = format.IsLinear ? -1 : 340;
            Generator.HorizontalOptions = format.IsLinear ? LayoutOptions.Fill : LayoutOptions.Center;
            Generator.Value = item.Value;
            ValueLabel.Text = item.Value;
            NameLabel.Text = string.IsNullOrWhiteSpace(item.Name) ? format.Label : $"{item.Name}  -  {format.Label}";
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
}

/// <summary>Pushes the screen to full brightness so scanners can read the barcode reliably.</summary>
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
