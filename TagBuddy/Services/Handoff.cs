using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TagBuddy.Services;

/// <summary>What another app on the phone could do with a scanned or read value.</summary>
public enum HandoffKind
{
    Web,
    Email,
    Phone,
    Sms,
    Map,
    /// <summary>A FIDO "hybrid" request (FIDO:/...): a computer or tablet asks to use a passkey on this phone.</summary>
    Passkey,
    /// <summary>An otpauth:// secret for an authenticator app.</summary>
    Authenticator,
    /// <summary>A wallet link: bitcoin:, lightning:, ethereum:.</summary>
    Payment,
    /// <summary>A Google Play link: market://.</summary>
    AppStore,
    /// <summary>Any other app link (scheme://...) some installed app may handle.</summary>
    OtherApp,
    /// <summary>WIFI:... network details.</summary>
    Wifi,
    /// <summary>A vCard or MeCard contact.</summary>
    Contact,
    /// <summary>An iCalendar event (BEGIN:VEVENT).</summary>
    Event,
}

/// <summary>A value that can be handed to another app: a link in the widest sense (web, mail, phone, passkey sign-in,
/// authenticator, wallet, app links) or structured content Android has an app for (Wi-Fi network, contact, calendar
/// event). Detection is pure; opening it is <see cref="HandoffLauncher"/>.</summary>
/// <param name="LaunchText">The text handed to Android as a link, with its scheme spelled as written: Android
/// matches schemes case-sensitively, and FIDO requests are registered as upper-case "FIDO".</param>
public sealed record Handoff(HandoffKind Kind, string Value, string LaunchText, Uri? Uri)
{
    /// <summary>Schemes that can reach into the phone or this app instead of another app's public entry point.</summary>
    private static readonly HashSet<string> NeverOpened = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "file", "content", "intent", "data", "about", "blob", "jar", "android-app", "view-source", "chrome",
    };

    public bool IsWeb => Kind == HandoffKind.Web;

    /// <summary>Finds what another app could do with <paramref name="value"/>; false when nothing on the phone would
    /// know what to do with it (plain text, numbers, unsafe schemes).</summary>
    public static bool TryDetect(string? value, [NotNullWhen(true)] out Handoff? handoff)
    {
        handoff = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();

        if (Links.TryGetOpenable(text, out var uri))
        {
            var kind = uri.Scheme.ToLowerInvariant() switch
            {
                "http" or "https" => HandoffKind.Web,
                "mailto" => HandoffKind.Email,
                "tel" => HandoffKind.Phone,
                "sms" or "smsto" => HandoffKind.Sms,
                _ => HandoffKind.Map,
            };
            // A bare domain became https://... in TryGetOpenable; hand over that, not the typed text.
            handoff = new(kind, text, kind == HandoffKind.Web ? uri.AbsoluteUri : text, uri);
            return true;
        }

        if (text.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase))
            return Make(WifiNetwork.TryParse(text, out _) ? HandoffKind.Wifi : null, text, out handoff);
        if (text.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase) || text.StartsWith("MECARD:", StringComparison.OrdinalIgnoreCase))
            return Make(ContactCard.TryParse(text, out _) ? HandoffKind.Contact : null, text, out handoff);
        if (text.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            return Make(CalendarEvent.TryParse(text, out _) ? HandoffKind.Event : null, text, out handoff);
        if (text.StartsWith("MATMSG:", StringComparison.OrdinalIgnoreCase) && MatMsgToMailto(text) is { } mailto)
        {
            handoff = new(HandoffKind.Email, text, mailto, Uri.TryCreate(mailto, UriKind.Absolute, out var m) ? m : null);
            return true;
        }

        var scheme = Regex.Match(text, @"^([A-Za-z][A-Za-z0-9+.-]*):(\S*)$", RegexOptions.Singleline);
        if (!scheme.Success || NeverOpened.Contains(scheme.Groups[1].Value)) return false;
        var name = scheme.Groups[1].Value.ToLowerInvariant();
        var rest = scheme.Groups[2].Value;
        HandoffKind? found = name switch
        {
            "fido" when rest.StartsWith('/') && rest.Length > 1 => HandoffKind.Passkey,
            "otpauth" when rest.StartsWith("//") => HandoffKind.Authenticator,
            "bitcoin" or "lightning" or "ethereum" when rest.Length > 0 => HandoffKind.Payment,
            "market" when rest.StartsWith("//") => HandoffKind.AppStore,
            // Other schemes only in the app-link shape, so "Note:hello" or "ID:123" never count as links.
            _ when rest.StartsWith("//") && rest.Length > 2 => HandoffKind.OtherApp,
            _ => null,
        };
        return Make(found, text, out handoff);
    }

    private static bool Make(HandoffKind? kind, string text, out Handoff? handoff)
    {
        handoff = kind is { } k ? new(k, text, text, Uri.TryCreate(text, UriKind.Absolute, out var u) ? u : null) : null;
        return handoff is not null;
    }

    /// <summary>The button or menu text. <paramref name="appName"/> is the app Android will use, when known.</summary>
    public string Label(string? appName = null)
    {
        bool named = !string.IsNullOrWhiteSpace(appName);
        return Kind switch
        {
            HandoffKind.Web => named ? $"Open in {appName}" : "Open link",
            HandoffKind.Email => "Send email",
            HandoffKind.Phone => "Call number",
            HandoffKind.Sms => "Send SMS",
            HandoffKind.Map => "Show on map",
            HandoffKind.Passkey => "Sign in with passkey",
            HandoffKind.Authenticator => named ? $"Add to {appName}" : "Add to authenticator app",
            HandoffKind.Payment => named ? $"Open in {appName}" : "Open in wallet app",
            HandoffKind.AppStore => "Open in Play Store",
            HandoffKind.OtherApp => named ? $"Open in {appName}" : "Open in app",
            HandoffKind.Wifi => "Join Wi-Fi network",
            HandoffKind.Contact => "Add to contacts",
            HandoffKind.Event => "Add to calendar",
            _ => "Open",
        };
    }

    /// <summary>A question to ask before handing over, for requests with consequences; null when none is needed.</summary>
    public (string Title, string Message, string Accept)? Confirmation => Kind switch
    {
        HandoffKind.Passkey => ("Sign in with passkey",
            "This code asks to use a passkey on this phone to sign in on another device, such as a computer or tablet next to you. " +
            "Android checks over Bluetooth that the device is nearby.\n\nContinue only if you are signing in there yourself.",
            "Continue"),
        HandoffKind.Authenticator => ("Add to authenticator",
            "This code holds the secret for one-time sign-in codes. Hand it to your authenticator app only if you are setting up two-step sign-in yourself.",
            "Continue"),
        _ => null,
    };

    /// <summary>Said when no app on the phone takes it.</summary>
    public string NoAppMessage => Kind switch
    {
        HandoffKind.Passkey => "This phone has no passkey support (Google Play services) that can take this request.",
        HandoffKind.Authenticator => "No authenticator app on this phone takes otpauth links. Install one, such as Google Authenticator.",
        HandoffKind.Payment => "No wallet app on this phone takes this link.",
        HandoffKind.Wifi => "This phone could not add the network.",
        _ => "No app on this phone can open this.",
    };

    /// <summary>Turns a MATMSG (NTT DoCoMo e-mail) code into a mailto link.</summary>
    internal static string? MatMsgToMailto(string text)
    {
        var fields = Fields(text["MATMSG:".Length..]);
        if (!fields.TryGetValue("TO", out var to) || to.Length == 0) return null;
        var query = new List<string>();
        if (fields.TryGetValue("SUB", out var sub) && sub.Length > 0) query.Add("subject=" + Uri.EscapeDataString(sub));
        if (fields.TryGetValue("BODY", out var body) && body.Length > 0) query.Add("body=" + Uri.EscapeDataString(body));
        return $"mailto:{to}" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
    }

    /// <summary>The KEY:value; fields of WIFI:, MECARD: and MATMSG: codes, with \; \, \: \\ escapes undone.
    /// A repeated key keeps its first value.</summary>
    internal static Dictionary<string, string> Fields(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(body, @"(?<k>[A-Za-z]+):(?<v>(?:\\.|[^;])*);?"))
        {
            var v = Regex.Replace(m.Groups["v"].Value, @"\\(.)", "$1");
            fields.TryAdd(m.Groups["k"].Value, v);
        }
        return fields;
    }
}

/// <summary>A WIFI: network as in the de-facto standard from ZXing: T (WPA, WEP, SAE, nopass), S (name), P
/// (password), H (hidden).</summary>
public sealed record WifiNetwork(string Ssid, string? Password, string Security, bool Hidden)
{
    public bool IsOpen => Security is "nopass" || string.IsNullOrEmpty(Password);

    public static bool TryParse(string text, [NotNullWhen(true)] out WifiNetwork? network)
    {
        network = null;
        if (!text.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase)) return false;
        var f = Handoff.Fields(text[5..]);
        if (!f.TryGetValue("S", out var ssid) || ssid.Length == 0) return false;
        f.TryGetValue("P", out var password);
        var security = f.TryGetValue("T", out var t) && t.Length > 0 ? t.ToUpperInvariant() : (string.IsNullOrEmpty(password) ? "nopass" : "WPA");
        if (security == "NOPASS") security = "nopass";
        network = new(ssid, string.IsNullOrEmpty(password) ? null : password, security,
            f.TryGetValue("H", out var h) && h.Equals("true", StringComparison.OrdinalIgnoreCase));
        return true;
    }
}

/// <summary>The parts of a vCard or MeCard the contacts app can be given.</summary>
public sealed record ContactCard(string? Name, IReadOnlyList<string> Phones, IReadOnlyList<string> Emails,
    string? Organization, string? Title, string? Address, string? Url, string? Note)
{
    public static bool TryParse(string text, [NotNullWhen(true)] out ContactCard? card)
    {
        card = text.StartsWith("MECARD:", StringComparison.OrdinalIgnoreCase) ? FromMeCard(text) : FromVCard(text);
        return card is not null && (card.Name is not null || card.Phones.Count > 0 || card.Emails.Count > 0);
    }

    private static ContactCard? FromMeCard(string text)
    {
        var body = text["MECARD:".Length..];
        var all = Regex.Matches(body, @"(?<k>[A-Za-z-]+):(?<v>(?:\\.|[^;])*);?")
            .Select(m => (Key: m.Groups["k"].Value.ToUpperInvariant(), Value: Regex.Replace(m.Groups["v"].Value, @"\\(.)", "$1")))
            .Where(p => p.Value.Length > 0).ToList();
        string? one(string key) => all.FirstOrDefault(p => p.Key == key).Value;
        // MeCard writes "Last,First".
        var name = one("N") is { } n ? string.Join(" ", n.Split(',', StringSplitOptions.TrimEntries).Reverse()) : null;
        return new(name, all.Where(p => p.Key == "TEL").Select(p => p.Value).ToList(), all.Where(p => p.Key == "EMAIL").Select(p => p.Value).ToList(),
            one("ORG"), one("TITLE"), one("ADR"), one("URL"), one("NOTE"));
    }

    private static ContactCard FromVCard(string text)
    {
        // Unfold continuation lines (a line starting with a space or tab continues the one before).
        var lines = Regex.Replace(text, @"\r?\n[ \t]", "").Split('\n', StringSplitOptions.TrimEntries);
        var props = lines.Select(l =>
            {
                int colon = l.IndexOf(':');
                if (colon <= 0) return (Key: "", Value: "");
                var key = l[..colon].Split(';')[0];
                if (key.Contains('.')) key = key[(key.LastIndexOf('.') + 1)..];   // item1.TEL
                return (Key: key.ToUpperInvariant(), Value: Unescape(l[(colon + 1)..]));
            })
            .Where(p => p.Key.Length > 0 && p.Value.Length > 0).ToList();
        string? one(string key) => props.FirstOrDefault(p => p.Key == key).Value;

        var name = one("FN");
        if (name is null && one("N") is { } n)
        {
            var parts = n.Split(';');   // Family;Given;Middle;Prefix;Suffix
            name = string.Join(" ", new[] { parts.ElementAtOrDefault(3), parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2), parts[0], parts.ElementAtOrDefault(4) }
                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            if (name.Length == 0) name = null;
        }
        var address = one("ADR") is { } adr ? string.Join(", ", adr.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0)) : null;
        var org = one("ORG")?.Replace(';', ' ').Trim();
        return new(name, props.Where(p => p.Key == "TEL").Select(p => p.Value).ToList(), props.Where(p => p.Key == "EMAIL").Select(p => p.Value).ToList(),
            string.IsNullOrEmpty(org) ? null : org, one("TITLE"), string.IsNullOrEmpty(address) ? null : address, one("URL"), one("NOTE"));
    }

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
}

/// <summary>The first event of an iCalendar text, as far as the calendar app's "new event" screen takes it.</summary>
public sealed record CalendarEvent(string? Title, DateTimeOffset? Start, DateTimeOffset? End, bool AllDay, string? Location, string? Description)
{
    public static bool TryParse(string text, [NotNullWhen(true)] out CalendarEvent? calendarEvent)
    {
        calendarEvent = null;
        var unfolded = Regex.Replace(text, @"\r?\n[ \t]", "");
        int begin = unfolded.IndexOf("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase);
        if (begin < 0) return false;
        var lines = unfolded[begin..].Split('\n', StringSplitOptions.TrimEntries)
            .TakeWhile(l => !l.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase)).ToList();

        (string Params, string Value)? prop(string key)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase) || l.StartsWith(key + ";", StringComparison.OrdinalIgnoreCase));
            if (line is null) return null;
            int colon = line.IndexOf(':');
            return colon < 0 ? null : (line[key.Length..colon], line[(colon + 1)..].Replace("\\n", "\n").Replace("\\,", ",").Replace("\\;", ";").Trim());
        }

        var start = prop("DTSTART");
        bool allDay = start is { } s && (s.Params.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) || s.Value.Length == 8);
        calendarEvent = new(
            prop("SUMMARY")?.Value is { Length: > 0 } t ? t : null,
            start is { } st ? ParseTime(st.Value) : null,
            prop("DTEND") is { } e ? ParseTime(e.Value) : null,
            allDay,
            prop("LOCATION")?.Value is { Length: > 0 } loc ? loc : null,
            prop("DESCRIPTION")?.Value is { Length: > 0 } d ? d : null);
        return calendarEvent.Title is not null || calendarEvent.Start is not null;
    }

    /// <summary>20261023T140000Z (UTC), 20261023T140000 (phone's local time) or 20261023 (a date).</summary>
    internal static DateTimeOffset? ParseTime(string value)
    {
        var v = value.Trim();
        if (DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            return new DateTimeOffset(utc, TimeSpan.Zero);
        if (DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return new DateTimeOffset(local);
        if (DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return new DateTimeOffset(date, TimeSpan.Zero);
        return null;
    }
}
