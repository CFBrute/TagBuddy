using TagBuddy.Pages;
using Proxmark3;
using TagBuddy.Services;

namespace TagBuddy;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Start on Saved when there is something in it: what people kept is what they come back for. Decided before
        // the first frame, from the size of the saved file, so the app does not flash Generate first. A tag, share
        // link or Proxmark that opened the app still goes to its own tab afterwards.
        try
        {
            if (SavedService.HasItems(SavedService.FilePath) && SavedContent.Parent is ShellSection saved)
                CurrentItem.CurrentItem = saved;
        }
        catch
        {
            // Storage not readable yet: start on Generate as before.
        }

        Routing.RegisterRoute(ShowBarcodePage.Route, typeof(ShowBarcodePage));
        Routing.RegisterRoute(WebPage.Route, typeof(WebPage));
        Routing.RegisterRoute(DumpPage.Route, typeof(DumpPage));
        Routing.RegisterRoute(ScanPhotoPage.Route, typeof(ScanPhotoPage));
        Routing.RegisterRoute(DiffPage.Route, typeof(DiffPage));
        Routing.RegisterRoute(HelpTopicPage.Route, typeof(HelpTopicPage));

        // A share link from another Tag Buddy: open Saved and offer to save what it holds. The link can arrive
        // before the shell has loaded when it started the app.
        IncomingShare.Arrived += () => MainThread.BeginInvokeOnMainThread(ShowIncomingShare);
        Loaded += (_, _) => ShowIncomingShare();

#if ANDROID
        // A private tag Android handed the app: go to the NFC tab, which shows it with the passphrase box. The tag
        // can arrive before the shell has loaded when it started the app.
        NfcReader.Instance.TagDispatched += ShowDispatchedTag;
        Loaded += (_, _) => ShowDispatchedTag();

        // Plugging in a Proxmark jumps to the RF tab (the NFC route), where its panel appears. The page itself
        // shows/hides that panel; here we only bring the tab forward on a fresh attach.
        ProxmarkUsb.PresenceChanged += ShowRfOnAttach;
        Loaded += (_, _) => ShowRfOnAttach();
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

    private bool _showingShare;

    private async void ShowIncomingShare()
    {
        if (_showingShare || !IsLoaded || !IncomingShare.HasPending) return;
        _showingShare = true;
        try
        {
            var (item, damaged) = IncomingShare.Take();
            await GoToAsync("//saved");
            if (CurrentPage is not { } page) return;
            if (damaged)
            {
                await page.DisplayAlertAsync("Link not readable",
                    "This Tag Buddy link is incomplete or damaged, or it comes from a newer version of the app. Ask for it again, and check that the whole link was copied.", "OK");
                return;
            }
            if (item is not null && IPlatformApplication.Current?.Services.GetService<SavedService>() is { } saved)
                await CodeActions.SaveSharedAsync(page, saved, item);
        }
        catch
        {
            // Navigation or a dialog was not possible right now; the link can be opened again.
        }
        finally
        {
            _showingShare = false;
        }
        // Another link may have come in while this one was shown.
        if (IncomingShare.HasPending) ShowIncomingShare();
    }

#if ANDROID
    private async void ShowDispatchedTag()
    {
        if (!NfcReader.Instance.HasDispatchedTag || !IsLoaded || CurrentPage is NfcPage) return;
        try { await GoToAsync("//nfc"); }
        catch { /* navigation not possible right now; the tag waits for the next visit to the NFC tab */ }
    }

    /// <summary>On a fresh Proxmark attach, bring the RF tab (the NFC route) forward so its panel is seen.</summary>
    private void ShowRfOnAttach() => MainThread.BeginInvokeOnMainThread(async () =>
    {
        if (!IsLoaded || !ProxmarkUsb.PendingAttach || !ProxmarkUsb.IsAttached()) return;
        ProxmarkUsb.ClearPending();
        if (CurrentPage is NfcPage) return;
        try { await GoToAsync("//nfc"); } catch { /* can't navigate right now; the RF tab is still there to tap */ }
    });
#endif
}
