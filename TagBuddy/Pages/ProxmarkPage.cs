#if ANDROID
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>Experimental Proxmark-over-USB screen. One button opens the attached Proxmark's serial interface and
/// sends its own ping/version command, logging every step. It answers a single question: can this phone talk to
/// the Proxmark at all over OTG? It is a diagnostic, not a Proxmark client.</summary>
public sealed class ProxmarkPage : ContentPage
{
    public const string Route = "proxmark";

    private readonly Button _connect;
    private readonly Label _log;

    public ProxmarkPage()
    {
        Title = "Proxmark (USB)";

        var intro = new Label
        {
            FontSize = 14,
            LineHeight = 1.2,
            Text = "Plug a Proxmark3 into the phone with a USB-OTG cable, then tap Connect. Tag Buddy opens the "
                 + "Proxmark's serial interface and asks it for its version, to find out whether this phone's USB can "
                 + "speak to the device. This is an early experiment — it reads nothing from cards yet.",
        };

        _connect = new Button
        {
            Text = "Connect & probe",
            BackgroundColor = Palette.Accent,
            TextColor = Palette.AccentText,
            HorizontalOptions = LayoutOptions.Start,
            Padding = new Thickness(18, 8),
        };
        _connect.Clicked += OnConnect;

        _log = new Label
        {
            FontFamily = "monospace",
            FontSize = 12,
            LineHeight = 1.25,
            Text = ProxmarkUsb.IsAttached() ? "A Proxmark is attached. Tap Connect." : "No Proxmark detected yet.",
        };
        _log.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#374151"), Color.FromArgb("#E5E7EB"));

        var logCard = HelpViews.Card(_log);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 16, 16, 32),
                Spacing = 14,
                Children = { intro, _connect, logCard },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ProxmarkUsb.ClearPending();   // arriving here is acting on the attach; don't re-navigate
    }

    private async void OnConnect(object? sender, EventArgs e)
    {
        _connect.IsEnabled = false;
        _log.Text = "";
        var usb = new ProxmarkUsb();
        usb.Log += Append;
        try { await usb.ProbeAsync(); }
        catch (Exception ex) { Append($"Error: {ex.Message}"); }
        finally
        {
            usb.Log -= Append;
            _connect.IsEnabled = true;
        }
    }

    // Log lines can arrive on a background thread; append on the UI thread.
    private void Append(string line) => MainThread.BeginInvokeOnMainThread(() =>
        _log.Text = string.IsNullOrEmpty(_log.Text) ? line : _log.Text + "\n" + line);
}
#endif
