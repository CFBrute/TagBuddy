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
        Routing.RegisterRoute(HelpTopicPage.Route, typeof(HelpTopicPage));

#if ANDROID
        // A private tag Android handed the app: go to the NFC tab, which shows it with the passphrase box. The tag
        // can arrive before the shell has loaded when it started the app.
        NfcReader.Instance.TagDispatched += ShowDispatchedTag;
        Loaded += (_, _) => ShowDispatchedTag();
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
#endif
}
