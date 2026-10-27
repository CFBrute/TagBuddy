#if ANDROID
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>The RF tab: reading over a Proxmark3 attached by USB (HF 13.56 MHz and LF 125 kHz — the LF band the
/// phone's own NFC cannot reach). The tab appears only while a Proxmark is plugged in (AppShell toggles it). For
/// now it holds the connection check; card read/write and LF come next.</summary>
public sealed class ProxmarkPage : ContentPage
{
    private readonly Button _connect;
    private readonly Label _status;
    private readonly Label _log;

    public ProxmarkPage()
    {
        Title = "RF";

        var intro = new Label
        {
            FontSize = 14,
            LineHeight = 1.2,
            Text = "A Proxmark3 on USB reads tags the phone's own NFC can't — both the 13.56 MHz (HF) band and the "
                 + "125 kHz (LF) band. This tab appears whenever a Proxmark is plugged in. Reading cards is coming; "
                 + "for now, Connect checks that the phone can talk to the device.",
        };

        _status = new Label { FontFamily = "OpenSansSemibold", FontSize = 15 };
        _status.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#1F2937"), Color.FromArgb("#E5E7EB"));

        _connect = new Button
        {
            Text = "Connect & probe",
            BackgroundColor = Palette.Accent,
            TextColor = Palette.AccentText,
            HorizontalOptions = LayoutOptions.Start,
            Padding = new Thickness(18, 8),
        };
        _connect.Clicked += OnConnect;

        _log = new Label { FontFamily = "monospace", FontSize = 12, LineHeight = 1.25 };
        _log.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#374151"), Color.FromArgb("#E5E7EB"));
        var logCard = HelpViews.Card(_log);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 16, 16, 32),
                Spacing = 12,
                Children =
                {
                    intro,
                    new Label { Text = "Connection", FontAttributes = FontAttributes.Bold, FontSize = 16, Margin = new Thickness(0, 6, 0, 0) },
                    _status,
                    _connect,
                    logCard,
                },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ProxmarkUsb.ClearPending();   // arriving here is acting on the attach; don't re-navigate
        ShowStatus();
    }

    private void ShowStatus()
    {
        bool present = ProxmarkUsb.IsAttached();
        _status.Text = present ? "Proxmark detected on USB." : "No Proxmark detected.";
        _connect.IsEnabled = present;
        if (_log.Text?.Length is null or 0)
            _log.Text = present ? "Tap Connect to check the link." : "Plug a Proxmark into the phone over USB.";
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
            _connect.IsEnabled = ProxmarkUsb.IsAttached();
        }
    }

    // Log lines can arrive on a background thread; append on the UI thread.
    private void Append(string line) => MainThread.BeginInvokeOnMainThread(() =>
        _log.Text = string.IsNullOrEmpty(_log.Text) ? line : _log.Text + "\n" + line);
}
#endif
