namespace TagBuddy.Services;

/// <summary>Detects values the phone can open directly (web links, mail, phone, SMS, map locations).</summary>
public static class Links
{
    private static readonly HashSet<string> OpenableSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "tel", "sms", "smsto", "geo" };

    public static bool TryGetOpenable(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();

        // A bare domain such as example.com/page also counts as a web link.
        if (!text.Contains("://") && !text.Contains(':') &&
            System.Text.RegularExpressions.Regex.IsMatch(text, @"^(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)+(/\S*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
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

    public static async Task<bool> OpenAsync(Uri uri)
    {
        try
        {
            if (uri.Scheme is "http" or "https")
                return await Browser.Default.OpenAsync(uri, BrowserLaunchMode.External);
            return await Launcher.Default.TryOpenAsync(uri);
        }
        catch
        {
            return false;
        }
    }
}
