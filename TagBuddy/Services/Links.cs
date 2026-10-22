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

    public static string OpenLabel(Uri uri) => uri.Scheme.ToLowerInvariant() switch
    {
        "http" or "https" => "Open link",
        "mailto" => "Send email",
        "tel" => "Call number",
        "sms" or "smsto" => "Send SMS",
        "geo" => "Show on map",
        _ => "Open",
    };

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
        return OpenWithAndroid(uri);
    }

    /// <summary>MAUI first checks that some app can take the link, and that check fails when Android hides the
    /// other apps (see the queries in AndroidManifest.xml). Starting the intent directly lets Android pick the
    /// app itself; only when there truly is none does this fail.</summary>
    private static bool OpenWithAndroid(Uri uri)
    {
#if ANDROID
        try
        {
            var intent = new Android.Content.Intent(Android.Content.Intent.ActionView, Android.Net.Uri.Parse(uri.AbsoluteUri));
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
