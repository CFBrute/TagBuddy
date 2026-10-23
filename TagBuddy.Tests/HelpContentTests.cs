using TagBuddy.Models;

namespace TagBuddy.Tests;

/// <summary>The help manual: every topic can be reached and drawn, and the Play Store edition does not name Amiibo.</summary>
public class HelpContentTests
{
    public static TheoryData<bool> Editions => new() { true, false };

    [Theory]
    [MemberData(nameof(Editions))]
    public void Topic_ids_are_unique_and_every_topic_has_content(bool amiibo)
    {
        var topics = HelpContent.Build(amiibo);
        Assert.Equal(topics.Count, topics.Select(t => t.Id).Distinct().Count());
        Assert.All(topics, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Summary));
            Assert.NotEmpty(t.Blocks);
        });
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void See_also_links_point_at_existing_topics(bool amiibo)
    {
        var topics = HelpContent.Build(amiibo);
        var ids = topics.Select(t => t.Id).ToHashSet();
        foreach (var topic in topics)
            foreach (var see in AllBlocks(topic.Blocks).OfType<HelpSeeAlso>())
                Assert.All(see.TopicIds, id => Assert.Contains(id, ids));
    }

    /// <summary>Icons and pictures are named by the .png MAUI makes of an .svg in Resources/Images.</summary>
    [Fact]
    public void Icons_and_pictures_exist()
    {
        var images = Path.Combine(RepositoryRoot(), "TagBuddy", "Resources", "Images");
        var files = HelpContent.Build(true).Select(t => t.Icon)
            .Concat(HelpContent.Build(true).SelectMany(t => AllBlocks(t.Blocks)).OfType<HelpImage>().Select(i => i.File));
        Assert.All(files.Distinct(), file =>
            Assert.True(File.Exists(Path.Combine(images, Path.ChangeExtension(file, ".svg"))), $"{file} has no .svg in Resources/Images"));
    }

    [Fact]
    public void Play_Store_edition_does_not_name_Amiibo()
    {
        var text = string.Join("\n", HelpContent.Build(false).SelectMany(t => new[] { t.Title, t.Summary }.Concat(AllBlocks(t.Blocks).SelectMany(Texts))));
        Assert.DoesNotContain("Amiibo", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sideload_edition_explains_Amiibo() =>
        Assert.Contains(HelpContent.Build(true).SelectMany(t => AllBlocks(t.Blocks)).OfType<HelpMore>(), m => m.Title == "Amiibo");

    private static IEnumerable<HelpBlock> AllBlocks(IEnumerable<HelpBlock> blocks) =>
        blocks.SelectMany(b => b is HelpMore more ? AllBlocks(more.Blocks).Prepend(b) : [b]);

    private static IEnumerable<string> Texts(HelpBlock block) => block switch
    {
        HelpText t => [t.Text],
        HelpHeading h => [h.Text],
        HelpList l => l.Items,
        HelpTip tip => [tip.Text],
        HelpImage img => [img.Caption],
        HelpMore more => [more.Title],
        _ => [],
    };

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TagBuddy", "Resources", "Images"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root not found above the test folder.");
    }
}
