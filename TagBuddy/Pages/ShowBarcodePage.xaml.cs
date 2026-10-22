using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

public partial class ShowBarcodePage : ContentPage, IQueryAttributable
{
    public const string Route = "show";

    private SavedItem? _item;

    public ShowBarcodePage()
    {
        InitializeComponent();
    }

    public static Task ShowAsync(SavedItem item) =>
        Shell.Current.GoToAsync(Route, new Dictionary<string, object> { ["item"] = item });

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("item", out var obj) && obj is SavedItem item)
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
        bool dim = ScreenBrightness.Current() < ScreenBrightness.DimThreshold;
        if (dim) ScreenBrightness.SetHalf(); else ScreenBrightness.SetMax();
        BrighterButton.IsVisible = dim;
        DeviceDisplay.Current.KeepScreenOn = true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ScreenBrightness.Restore();
        DeviceDisplay.Current.KeepScreenOn = false;
    }

    private void OnBrighter(object? sender, EventArgs e)
    {
        ScreenBrightness.SetMax();
        BrighterButton.IsVisible = false;
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

/// <summary>Pushes the screen up so scanners can read the code reliably. In a bright place that means full
/// brightness; in the dark, where the phone is already dimmed, a sudden full-brightness screen hurts, so
/// the page goes to half and offers a button for the rest.</summary>
static class ScreenBrightness
{
    /// <summary>Below this system brightness the page opens at half instead of full.</summary>
    public const float DimThreshold = 0.30f;
    public const float Half = 0.5f;

    /// <summary>The phone's current screen brightness, 0..1, or 1 when it cannot be read.</summary>
    public static float Current()
    {
#if ANDROID
        try
        {
            var resolver = Platform.AppContext.ContentResolver;
            int value = Android.Provider.Settings.System.GetInt(resolver, Android.Provider.Settings.System.ScreenBrightness, 255);
            return Math.Clamp(value / 255f, 0f, 1f);
        }
        catch
        {
            return 1f;
        }
#else
        return 1f;
#endif
    }

    public static void SetMax() => Set(1f);
    public static void SetHalf() => Set(Half);

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
