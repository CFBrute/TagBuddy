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

            OpenLinkButton.IsVisible = Handoff.TryDetect(item.Value, out var handoff);
            if (handoff is not null) IconButtons.SetLabel(OpenLinkButton, HandoffLauncher.Label(handoff));
            Preview.Value = item.Value;
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Setting the slider applies the brightness through OnBrightnessChanged.
        BrightnessSlider.Value = ScreenBrightness.StartLevel(ScreenBrightness.Current(), AppSettings.MaxBrightness);
        DeviceDisplay.Current.KeepScreenOn = true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ScreenBrightness.Restore();
        DeviceDisplay.Current.KeepScreenOn = false;
    }

    private void OnBrightnessChanged(object? sender, ValueChangedEventArgs e)
    {
        ScreenBrightness.Set((float)e.NewValue);
        BrightnessLabel.Text = $"{Math.Round(e.NewValue * 100)} %";
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
