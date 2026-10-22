using System.Reflection;

namespace TagBuddy;

/// <summary>The version, commit and build time stamped into the app when it was built (the StampBuildVersion
/// target in TagBuddy.csproj).</summary>
public static class BuildInfo
{
    /// <summary>Where bug reports, feedback and complaints go (the About tab).
    /// PLACEHOLDER: example.com is reserved and never receives mail. Replace it with a real address before
    /// publishing.</summary>
    public const string SupportEmail = "tagbuddy-support@example.com";

    private static readonly Assembly App = typeof(BuildInfo).Assembly;

    private static readonly (string Version, string Commit) Parts =
        Split(App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Like "1.0.14".</summary>
    public static string Version => Parts.Version;

    /// <summary>Short commit the build came from, with "*" if it had uncommitted changes; empty without git.</summary>
    public static string Commit => Parts.Commit;

    /// <summary>Like "2026-10-22 13:40"; empty if unknown.</summary>
    public static string BuildTime =>
        App.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value ?? string.Empty;

    /// <summary>Shown on the About tab and in bug reports: "Tag Buddy v1.0.14 · 5be6c14".</summary>
    public static string Label => Describe(Version, Commit);

    /// <summary>The second line: when the build was made, and what a "*" means.</summary>
    public static string Details => WithEdition(Explain(Commit, BuildTime));

    /// <summary>"1.0.14+5be6c14*" into its version and commit.</summary>
    public static (string Version, string Commit) Split(string? informational)
    {
        if (string.IsNullOrEmpty(informational)) return ("0.0.0", string.Empty);
        var plus = informational.IndexOf('+');
        return plus < 0 ? (informational, string.Empty) : (informational[..plus], informational[(plus + 1)..]);
    }

    public static string Describe(string version, string commit) =>
        commit.Length > 0 ? $"Tag Buddy v{version}  ·  {commit}" : $"Tag Buddy v{version}";

    /// <summary>The second line names the edition too, so a bug report says which build it came from.</summary>
    private static string WithEdition(string details) => $"{Features.Edition} build  ·  {details}";

    public static string Explain(string commit, string buildTime)
    {
        var lines = new List<string> { buildTime.Length > 0 ? $"Built {buildTime}" : "Build time unknown" };
        if (commit.EndsWith('*')) lines.Add("* with changes that were not committed yet");
        return string.Join("\n", lines);
    }
}
