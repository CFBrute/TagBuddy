using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>The About tab: which build this is, the settings, what the app sends over the network, and a way to
/// report bugs or complain by email.</summary>
public partial class AboutPage : ContentPage
{
    private bool _loading;

    public AboutPage()
    {
        InitializeComponent();
        VersionLabel.Text = BuildInfo.Label;
        BuildLabel.Text = BuildInfo.Details;
        EmailLabel.Text = BuildInfo.SupportEmail;

        _loading = true;
        AutoPreviewSwitch.IsToggled = AppSettings.AutoPreviews;
        _loading = false;
        ShowPreviewHint();
    }

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

    /// <summary>A bug report starts with the questions worth answering and ends with the build and the phone.</summary>
    private async void OnReportBug(object? sender, EventArgs e) =>
        await ComposeAsync("Tag Buddy bug report",
            "What I did:\n\n\nWhat happened:\n\n\nWhat I expected:\n\n\n" +
            $"--\n{BuildInfo.Label}\n{BuildInfo.Details}\n" +
            $"Android {DeviceInfo.Current.VersionString}, {DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}\n");

    private async void OnSendFeedback(object? sender, EventArgs e) =>
        await ComposeAsync("Tag Buddy feedback", $"\n\n--\n{BuildInfo.Label}\n");

    private async void OnPrivacyPolicy(object? sender, EventArgs e) =>
        await Links.OpenAsync(new Uri(BuildInfo.PrivacyPolicyUrl));

    private async void OnCopyAddress(object? sender, EventArgs e) =>
        await CodeActions.CopyTextAsync(this, BuildInfo.SupportEmail);

    /// <summary>Opens the phone's mail app with the address, subject and a starting text filled in.</summary>
    private async Task ComposeAsync(string subject, string body)
    {
        var uri = new Uri($"mailto:{BuildInfo.SupportEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}");
        try
        {
            await Launcher.Default.OpenAsync(uri);
        }
        catch
        {
            await DisplayAlertAsync("No mail app", $"Write to {BuildInfo.SupportEmail}; \"Copy address\" puts it on the clipboard.", "OK");
        }
    }
}
