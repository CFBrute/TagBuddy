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
        UpdateSavedSwitch.IsToggled = AppSettings.AskUpdateSavedTag;
        MaxBrightnessSwitch.IsToggled = AppSettings.MaxBrightness;
        AutoPreviewSwitch.IsToggled = AppSettings.AutoPreviews;
        _loading = false;
        ShowBrightnessHint();
        ShowPreviewHint();

        KeySection.IsVisible = Features.MifareKeyTools;
        HardwareSection.IsVisible = Features.ProxmarkUsb;
        ShowKeyStoreInfo();
        ShowPasswordStoreInfo();
    }

    private async void OnOpenProxmark(object? sender, EventArgs e)
    {
#if ANDROID
        await Shell.Current.GoToAsync(ProxmarkPage.Route);
#else
        await Task.CompletedTask;
#endif
    }

    private readonly MifareKeyStore _keyStore = new();
    private readonly Type2PasswordStore _passwordStore = new();

    private void ShowPasswordStoreInfo()
    {
        int tags = _passwordStore.CardCount();
        PasswordStoreInfo.Text = tags == 0
            ? "A password that opens a protected NTAG/Ultralight is remembered by its serial, so the tag opens on its own next time. None remembered yet."
            : $"Passwords that opened a protected NTAG/Ultralight are remembered by serial, so each tag opens on its own next time: {tags} {(tags == 1 ? "tag" : "tags")} so far. Kept on this phone only.";
        ForgetPasswordsButton.IsEnabled = tags > 0;
    }

    private async void OnForgetRememberedPasswords(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Forget tag passwords", "Forget the passwords remembered for every tag? You will need to enter a tag's password again to open it.", "Forget", "Cancel"))
            return;
        _passwordStore.Clear();
        ShowPasswordStoreInfo();
        Notice.Show("Remembered tag passwords forgotten");
    }

    private void ShowKeyStoreInfo()
    {
        if (!Features.MifareKeyTools) return;
        int cards = _keyStore.RememberedCardCount();
        int imported = _keyStore.ImportedCount();
        KeyStoreInfo.Text = $"Keys that opened a card are remembered to open it quickly next time: {cards} {(cards == 1 ? "card" : "cards")} so far. Imported dictionary: {imported:N0} keys.";
        ForgetKeysButton.IsEnabled = cards > 0;
        ClearImportedButton.IsEnabled = imported > 0;
        UsedKeysButton.IsEnabled = _keyStore.UsedKeys().Count > 0;
    }

    /// <summary>Shows every key that has opened or been written to a card, newest first; tapping one copies its hex.</summary>
    private async void OnShowUsedKeys(object? sender, EventArgs e)
    {
        var used = _keyStore.UsedKeys();
        if (used.Count == 0)
        {
            await DisplayAlertAsync("Keys used", "No keys have been used yet. Read or write a MIFARE Classic card and its keys show up here.", "OK");
            return;
        }

        const int max = 30;
        var shown = used.Take(max).ToList();
        var labels = shown.Select(k => $"{k.Hex}  ·  {Ago(k.When)}").ToArray();
        var pick = await DisplayActionSheet($"Keys used — tap to copy{(used.Count > max ? $" (newest {max})" : "")}", "Cancel", null, labels);
        if (pick is null || pick == "Cancel") return;

        var hex = pick.Split("  ·  ")[0];
        await Clipboard.Default.SetTextAsync(hex);
        Notice.Show($"Copied {hex}");
    }

    /// <summary>A short "how long ago" for a time: "just now", "5 min ago", "3 h ago", "2 d ago", else the date.</summary>
    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} d ago";
        return when.ToLocalTime().ToString("d MMM yyyy");
    }

    private async void OnForgetRememberedKeys(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Forget tag keys", "Forget the keys remembered for every card? Reading them again re-learns the keys.", "Forget", "Cancel"))
            return;
        _keyStore.ClearRemembered();
        ShowKeyStoreInfo();
        Notice.Show("Remembered tag keys forgotten");
    }

    private async void OnClearImportedKeys(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Clear imported keys", "Remove the imported key dictionary? You can import it again on the NFC tab.", "Clear", "Cancel"))
            return;
        _keyStore.ClearImported();
        ShowKeyStoreInfo();
        Notice.Show("Imported keys cleared");
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

    private void OnUpdateSavedToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        AppSettings.AskUpdateSavedTag = e.Value;
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
