namespace TagBuddy.Services;

/// <summary>A well-known site with its other addresses (short domains, old names), and what its links usually
/// point at. <paramref name="Home"/> is the address its icon is kept under, shared by every alias.</summary>
public sealed record KnownSite(string Name, string Home, string[] Domains, Func<Uri, string?>? Kind = null);

/// <summary>Recognises links to well-known sites and link shorteners from the address alone, without any request:
/// the card can say "YouTube · Video" before anything loads (or when previews are off), and youtu.be, m.youtube.com
/// and youtube.com share one icon.</summary>
public static class KnownSites
{
    private static string? PathKind(Uri uri, params (string Prefix, string Kind)[] kinds)
    {
        var path = uri.AbsolutePath.ToLowerInvariant();
        foreach (var (prefix, kind) in kinds)
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return kind;
        return null;
    }

    public static readonly IReadOnlyList<KnownSite> Sites =
    [
        new("YouTube", "youtube.com", ["youtube.com", "youtu.be", "youtube-nocookie.com"], u =>
            u.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase) ? "Video"
            : PathKind(u, ("/shorts/", "Short"), ("/watch", "Video"), ("/live/", "Live video"), ("/playlist", "Playlist"),
                ("/@", "Channel"), ("/channel/", "Channel"), ("/c/", "Channel"))),
        new("X", "x.com", ["x.com", "twitter.com"], u => u.AbsolutePath.Contains("/status/") ? "Post" : null),
        new("Instagram", "instagram.com", ["instagram.com", "instagr.am"],
            u => PathKind(u, ("/p/", "Post"), ("/reel/", "Reel"), ("/reels/", "Reel"), ("/stories/", "Story"))),
        new("Facebook", "facebook.com", ["facebook.com", "fb.com", "fb.me", "fb.watch"],
            u => u.Host.Contains("fb.watch") ? "Video" : PathKind(u, ("/watch", "Video"), ("/events/", "Event"), ("/groups/", "Group"))),
        new("Threads", "threads.net", ["threads.net", "threads.com"], u => u.AbsolutePath.Contains("/post/") ? "Post" : null),
        new("TikTok", "tiktok.com", ["tiktok.com"], u => u.AbsolutePath.Contains("/video/") || u.Host.StartsWith("vm.") ? "Video" : null),
        new("Reddit", "reddit.com", ["reddit.com", "redd.it"], u => u.AbsolutePath.Contains("/comments/") ? "Post" : PathKind(u, ("/r/", "Community"))),
        new("Bluesky", "bsky.app", ["bsky.app"], u => u.AbsolutePath.Contains("/post/") ? "Post" : null),
        new("LinkedIn", "linkedin.com", ["linkedin.com", "lnkd.in"], u => PathKind(u, ("/in/", "Profile"), ("/company/", "Company"), ("/jobs/", "Job"), ("/posts/", "Post"))),
        new("Pinterest", "pinterest.com", ["pinterest.com", "pin.it"], u => PathKind(u, ("/pin/", "Pin"))),
        new("Twitch", "twitch.tv", ["twitch.tv"], u => PathKind(u, ("/videos/", "Video"))),
        new("Vimeo", "vimeo.com", ["vimeo.com"], _ => "Video"),
        new("Dailymotion", "dailymotion.com", ["dailymotion.com", "dai.ly"], _ => "Video"),
        new("Spotify", "spotify.com", ["spotify.com", "spotify.link"],
            u => PathKind(u, ("/track/", "Song"), ("/album/", "Album"), ("/playlist/", "Playlist"), ("/artist/", "Artist"),
                ("/episode/", "Podcast episode"), ("/show/", "Podcast"))),
        new("SoundCloud", "soundcloud.com", ["soundcloud.com"], _ => "Audio"),
        new("Flickr", "flickr.com", ["flickr.com", "flic.kr"], _ => "Photo"),
        new("WhatsApp", "whatsapp.com", ["whatsapp.com", "wa.me"], u => u.Host.Contains("wa.me") ? "Chat" : null),
        new("Telegram", "telegram.org", ["telegram.org", "t.me", "telegram.me"], u => u.Host is "t.me" or "telegram.me" ? "Chat or channel" : null),
        new("Discord", "discord.com", ["discord.com", "discord.gg"], u => u.Host.Contains("discord.gg") || u.AbsolutePath.StartsWith("/invite") ? "Invite" : null),
        new("Snapchat", "snapchat.com", ["snapchat.com"], null),
        new("Google Maps", "maps.google.com", ["maps.google.com", "maps.app.goo.gl"], _ => "Map location"),
        new("Google Play", "play.google.com", ["play.google.com"], u => u.AbsolutePath.Contains("/details") ? "App" : null),
        new("App Store", "apps.apple.com", ["apps.apple.com"], _ => "App"),
        new("GitHub", "github.com", ["github.com"], null),
        new("Wikipedia", "wikipedia.org", ["wikipedia.org"], u => PathKind(u, ("/wiki/", "Article"))),
        new("Amazon", "amazon.com",
            ["amazon.com", "amazon.se", "amazon.de", "amazon.co.uk", "amazon.fr", "amazon.it", "amazon.es", "amazon.nl",
             "amazon.pl", "amazon.ca", "amazon.com.au", "amazon.co.jp", "amazon.in", "amzn.to", "amzn.eu", "a.co"],
            u => u.AbsolutePath.Contains("/dp/") || u.AbsolutePath.Contains("/gp/product") ? "Product" : null),
    ];

    /// <summary>Link shorteners: their links lead anywhere, so a short link never takes on its destination's icon.</summary>
    private static readonly string[] Shorteners =
        ["bit.ly", "tinyurl.com", "t.co", "ow.ly", "buff.ly", "is.gd", "rebrand.ly", "cutt.ly", "shorturl.at", "tiny.cc",
         "s.id", "rb.gy", "t.ly", "lnk.to", "linktr.ee", "qrco.de", "goo.gl"];

    /// <summary>Host prefixes that are the same site in another form.</summary>
    private static readonly string[] SamePrefixes = ["www.", "m.", "mobile.", "amp."];

    public static KnownSite? Match(Uri uri) =>
        Sites.FirstOrDefault(s => OEmbed.HostIs(uri.Host, s.Domains));

    public static bool IsShortener(Uri uri) => OEmbed.HostIs(uri.Host, Shorteners);

    /// <summary>The name of the site and what the link points at, when the address says so: "YouTube · Video",
    /// "Short link (bit.ly)", or null for an address this table does not know.</summary>
    public static string? Describe(Uri uri)
    {
        if (IsShortener(uri)) return $"Short link ({Bare(uri.Host)}): it leads to another address";
        if (Match(uri) is not { } site) return null;
        var kind = site.Kind?.Invoke(uri);
        return kind is null ? site.Name : $"{site.Name} · {kind}";
    }

    /// <summary>The host a site's icon is kept under: the known site's home for its aliases (youtu.be →
    /// youtube.com), otherwise the host without www., m., mobile. or amp. in front.</summary>
    public static string IconHost(Uri uri)
    {
        if (Match(uri) is { } site) return site.Home;
        return Bare(uri.Host);
    }

    private static string Bare(string host)
    {
        host = host.ToLowerInvariant();
        foreach (var prefix in SamePrefixes)
            if (host.StartsWith(prefix, StringComparison.Ordinal) && host.Count(c => c == '.') >= 2)
                return host[prefix.Length..];
        return host;
    }
}
