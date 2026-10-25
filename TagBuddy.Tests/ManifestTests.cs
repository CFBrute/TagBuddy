using System.Xml.Linq;

namespace TagBuddy.Tests;

/// <summary>The Android manifest. Android 11+ hides other apps unless the manifest declares them in queries;
/// without these, opening links and "Report a bug" fail quietly.</summary>
public class ManifestTests
{
    private static readonly XNamespace AndroidNs = "http://schemas.android.com/apk/res/android";

    [Theory]
    [InlineData("android.intent.action.VIEW", "http")]
    [InlineData("android.intent.action.VIEW", "https")]
    [InlineData("android.intent.action.SENDTO", "mailto")]
    [InlineData("android.intent.action.VIEW", "tel")]
    [InlineData("android.intent.action.VIEW", "sms")]
    [InlineData("android.intent.action.VIEW", "smsto")]
    [InlineData("android.intent.action.VIEW", "geo")]
    [InlineData("android.intent.action.VIEW", "FIDO")]
    [InlineData("android.intent.action.VIEW", "otpauth")]
    [InlineData("android.intent.action.VIEW", "market")]
    public void Manifest_can_see_the_apps_it_opens(string action, string scheme)
    {
        var intents = LoadManifest().Root!.Element("queries")?.Elements("intent") ?? [];

        Assert.Contains(intents, intent =>
            intent.Elements("action").Any(a => (string?)a.Attribute(AndroidNs + "name") == action) &&
            intent.Elements("data").Any(d => (string?)d.Attribute(AndroidNs + "scheme") == scheme));
    }

    /// <summary>Apps that take their own site's links are only visible with an entry for that host, so the viewer
    /// can say "Open in YouTube" instead of naming a browser.</summary>
    [Theory]
    [InlineData("youtube.com")]
    [InlineData("m.youtube.com")]
    [InlineData("youtu.be")]
    [InlineData("instagram.com")]
    [InlineData("open.spotify.com")]
    public void Manifest_can_see_apps_for_known_sites(string host)
    {
        var intents = LoadManifest().Root!.Element("queries")?.Elements("intent") ?? [];

        Assert.Contains(intents, intent =>
            intent.Elements("data").Any(d =>
                (string?)d.Attribute(AndroidNs + "scheme") == "https" && (string?)d.Attribute(AndroidNs + "host") == host));
    }

    /// <summary>Finds the manifest by walking up from the test output folder to the repository.</summary>
    private static XDocument LoadManifest()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "TagBuddy", "Platforms", "Android", "AndroidManifest.xml");
            if (File.Exists(path)) return XDocument.Load(path);
        }
        throw new FileNotFoundException("TagBuddy/Platforms/Android/AndroidManifest.xml not found above the test folder.");
    }
}
