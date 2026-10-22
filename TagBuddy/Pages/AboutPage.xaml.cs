using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>The About tab under More: which build this is, what the app sends over the network, and a way to
/// report bugs or complain by email.</summary>
public partial class AboutPage : ContentPage
{
    public AboutPage()
    {
        InitializeComponent();
        VersionLabel.Text = BuildInfo.Label;
        BuildLabel.Text = BuildInfo.Details;
        EmailLabel.Text = BuildInfo.SupportEmail;
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
        await CodeActions.OpenLinkAsync(this, BuildInfo.PrivacyPolicyUrl);

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
