using System.Text.RegularExpressions;

namespace TagBuddy.Services;

/// <summary>Detects values the phone can open directly (web links, mail, phone, SMS, map locations), and links
/// that must only ever be opened by the person.</summary>
public static class Links
{
    // Not "t": YouTube timestamps (?t=30) and many share links use it, and they are ordinary links.
    private static readonly Regex SingleUseQuery = new(
        @"[?&#](code|token|otp|ticket|nonce|auth|session|sig|key|access_token|id_token|state|signature|hash|magic|confirm)=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SingleUsePath = new(
        @"/(unsubscribe|confirm|verify|activate|reset|magic|invite/accept)(?![a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True for a sign-in, one-time or side-effect link (unsubscribe, confirm, password reset). A GET on
    /// it would use the code up or do the thing, so only the page itself, opened by the person, may request it.</summary>
    public static bool IsSingleUse(Uri uri) =>
        SingleUseQuery.IsMatch(uri.Query + uri.Fragment) || SingleUsePath.IsMatch(uri.AbsolutePath);

    private static readonly HashSet<string> OpenableSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "tel", "sms", "smsto", "geo" };

    public static bool TryGetOpenable(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();

        // A bare domain such as example.com/page also counts as a web link.
        if (!text.Contains("://") && !text.Contains(':') &&
            Regex.IsMatch(text, @"^(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)+(/\S*)?$", RegexOptions.IgnoreCase))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return false;
        if (!OpenableSchemes.Contains(parsed.Scheme)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>True for http(s) links: the ones that can be shown in the in-app page viewer and previewed.</summary>
    public static bool IsWebLink(string? value, out Uri uri) =>
        TryGetOpenable(value, out uri) && uri.Scheme is "http" or "https";

    /// <summary>The name of the app Android will open the link in, such as "YouTube" or "Chrome". Null when
    /// Android will ask the person to choose (no default app), when no app can take it, or off Android.</summary>
    public static string? HandlerName(Uri uri) => HandlerName(uri.AbsoluteUri);

    /// <summary>The same for a link given as text, with its scheme spelled as written (see <see cref="OpenText"/>).</summary>
    public static string? HandlerName(string link)
    {
#if ANDROID
        try
        {
            var context = Platform.AppContext;
            var pm = context.PackageManager;
            if (pm is null) return null;
            var intent = new Android.Content.Intent(Android.Content.Intent.ActionView, Android.Net.Uri.Parse(link));
            var info = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? pm.ResolveActivity(intent, Android.Content.PM.PackageManager.ResolveInfoFlags.Of((long)Android.Content.PM.PackageInfoFlags.MatchDefaultOnly))
                : pm.ResolveActivity(intent, Android.Content.PM.PackageInfoFlags.MatchDefaultOnly);
            var activity = info?.ActivityInfo;
            if (activity is null) return null;

            // Without a default app Android resolves to its own "Open with" picker; that is not an app to name.
            if (activity.PackageName is "android" or "com.android.intentresolver" || activity.PackageName == context.PackageName ||
                activity.Name?.EndsWith("ResolverActivity", StringComparison.Ordinal) == true)
                return null;

            var name = info!.LoadLabel(pm)?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
#else
        return null;
#endif
    }

    /// <summary>Opens the link in the app that handles it: the browser, mail, phone, SMS or maps.</summary>
    public static async Task<bool> OpenAsync(Uri uri)
    {
        try
        {
            bool opened = uri.Scheme is "http" or "https"
                ? await Browser.Default.OpenAsync(uri, BrowserLaunchMode.External)
                : await Launcher.Default.TryOpenAsync(uri);
            if (opened) return true;
        }
        catch
        {
            // Refused or failed: try handing it to Android directly below.
        }
        return OpenText(uri.AbsoluteUri);
    }

    /// <summary>Hands a link straight to Android, which picks the app itself; only when there truly is none does this
    /// fail. MAUI's own launchers first check that some app can take the link, and that check fails when Android
    /// hides the other apps (see the queries in AndroidManifest.xml).
    /// <para>The link is passed as text, not as a parsed Uri, because Android matches schemes case-sensitively and
    /// .NET lower-cases them: a passkey request must arrive as "FIDO:/...", which is how it is registered.</para></summary>
    public static bool OpenText(string link)
    {
#if ANDROID
        try
        {
            var intent = new Android.Content.Intent(Android.Content.Intent.ActionView, Android.Net.Uri.Parse(link));
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Platform.AppContext.StartActivity(intent);
            return true;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }
}
