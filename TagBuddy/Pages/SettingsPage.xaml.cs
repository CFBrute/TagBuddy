using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>The Settings tab under More: scanning and NFC feedback, full-screen brightness and link previews.</summary>
public partial class SettingsPage : ContentPage
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();

        _loading = true;
        VibrateSwitch.IsToggled = AppSettings.Vibrate;
        SoundSwitch.IsToggled = AppSettings.Sound;
        NfcSoundSwitch.IsToggled = AppSettings.NfcSound;
        AutoDefaultSwitch.IsToggled = AppSettings.AutoTryDefaultPassword;
        MaxBrightnessSwitch.IsToggled = AppSettings.MaxBrightness;
        AutoPreviewSwitch.IsToggled = AppSettings.AutoPreviews;
        _loading = false;
        ShowBrightnessHint();
        ShowPreviewHint();
    }

    private void OnVibrateToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        AppSettings.Vibrate = e.Value;
        if (e.Value) Feedback.Success();
    }

    // Play the beep straight away, so the person hears what they chose (and finds out if the phone is on silent).
    private void OnSoundToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        AppSettings.Sound = e.Value;
        if (e.Value) Feedback.Success();
    }

    /// <summary>Takes effect the next time the NFC tab starts reading, which is whenever it is opened.</summary>
    private void OnNfcSoundToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        AppSettings.NfcSound = e.Value;
    }

    private void OnAutoDefaultToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        AppSettings.AutoTryDefaultPassword = e.Value;
    }

    private void OnMaxBrightnessToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        AppSettings.MaxBrightness = e.Value;
        ShowBrightnessHint();
    }

    private void ShowBrightnessHint() => MaxBrightnessHint.Text = AppSettings.MaxBrightness
        ? "A code tapped to full screen starts at 100 %. The slider can still turn it down."
        : "A code tapped to full screen starts at double the phone's brightness (at least 20 %), so it is easy to scan without being blinding.";

    private void OnAutoPreviewToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        AppSettings.AutoPreviews = e.Value;
        ShowPreviewHint();
    }

    private void ShowPreviewHint() => AutoPreviewHint.Text = AppSettings.AutoPreviews
        ? "Web links show their title, picture and site icon as soon as they appear."
        : "Nothing is fetched until you tap \"Tap to load preview\" or open the page. Previews and icons already on the phone still show.";

    private async void OnClearCache(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Clear previews", "Forget every stored link preview and site icon? They load again when needed.", "Clear", "Cancel"))
            return;
        await Task.Run(LinkPreviews.ClearCache);
        Notice.Show("Previews and icons cleared");
    }
}
