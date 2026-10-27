using TagBuddy.Pages;
using TagBuddy.Services;

namespace TagBuddy;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(ShowBarcodePage.Route, typeof(ShowBarcodePage));
        Routing.RegisterRoute(WebPage.Route, typeof(WebPage));
        Routing.RegisterRoute(DumpPage.Route, typeof(DumpPage));
        Routing.RegisterRoute(DiffPage.Route, typeof(DiffPage));
        Routing.RegisterRoute(HelpTopicPage.Route, typeof(HelpTopicPage));

#if ANDROID
        // A private tag Android handed the app: go to the NFC tab, which shows it with the passphrase box. The tag
        // can arrive before the shell has loaded when it started the app.
        NfcReader.Instance.TagDispatched += ShowDispatchedTag;
        Loaded += (_, _) => ShowDispatchedTag();

        // The RF tab appears only while a Proxmark is plugged in. Track attach/detach and the current state.
        ProxmarkUsb.PresenceChanged += RefreshRfTab;
        Loaded += (_, _) => RefreshRfTab();
#endif
    }

    /// <summary>Leaving the More tab closes any help topic open there, so coming back shows the tab itself rather
    /// than wherever the reading had got to.</summary>
    protected override async void OnNavigated(ShellNavigatedEventArgs args)
    {
        base.OnNavigated(args);
        if (CurrentItem?.CurrentItem == MoreTab || MoreTab.Navigation.NavigationStack.Count <= 1) return;
        try { await MoreTab.Navigation.PopToRootAsync(false); }
        catch { /* the tab is not shown; it is tidied on the next visit instead */ }
    }

#if ANDROID
    private async void ShowDispatchedTag()
    {
        if (!NfcReader.Instance.HasDispatchedTag || !IsLoaded || CurrentPage is NfcPage) return;
        try { await GoToAsync("//nfc"); }
        catch { /* navigation not possible right now; the tag waits for the next visit to the NFC tab */ }
    }

    /// <summary>Shows the RF tab while a Proxmark is attached and hides it otherwise, re-checking the live USB state
    /// rather than trusting the event. On a fresh attach it jumps to the tab; on detach it leaves the tab if open.</summary>
    private void RefreshRfTab() => MainThread.BeginInvokeOnMainThread(async () =>
    {
        if (!IsLoaded) return;
        bool present = ProxmarkUsb.IsAttached();
        RfTab.IsVisible = present;

        if (present && ProxmarkUsb.PendingAttach)
        {
            ProxmarkUsb.ClearPending();
            try { await GoToAsync("//rf"); } catch { /* can't navigate right now; the RF tab is still there to tap */ }
        }
        else if (!present && CurrentPage is ProxmarkPage)
        {
            try { await GoToAsync("//nfc"); } catch { /* leaving the now-gone tab; it just hides */ }
        }
    });
#endif
}
