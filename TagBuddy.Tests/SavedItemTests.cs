using System.Text.Json;
using TagBuddy.Models;

namespace TagBuddy.Tests;

/// <summary>What a saved item looks like in saved.json.</summary>
public class SavedItemTests
{
    /// <summary>Only properties that can be read back belong in the file; anything worked out from them (IsQr,
    /// DisplayName, Subtitle, ...) must be marked [JsonIgnore].</summary>
    [Fact]
    public void Json_holds_only_stored_properties()
    {
        var item = new SavedItem { Value = "https://example.com", Format = "QrCode" };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(item));

        var stored = typeof(SavedItem).GetProperties()
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet();
        var written = json.RootElement.EnumerateObject().Select(p => p.Name);

        Assert.DoesNotContain(written, name => !stored.Contains(name));
    }

    [Fact]
    public void Saved_sorts_by_last_use_then_by_when_it_was_saved()
    {
        var saved = new DateTime(2026, 1, 1);
        var item = new SavedItem { CreatedAt = saved };
        Assert.Equal(saved, item.SortTime);

        item.LastUsedAt = saved.AddDays(3);
        Assert.Equal(saved.AddDays(3), item.SortTime);
    }

    [Fact]
    public void Items_saved_before_last_use_was_kept_load_as_never_used()
    {
        var item = JsonSerializer.Deserialize<SavedItem>("""{"Value":"x","CreatedAt":"2026-01-01T00:00:00"}""")!;
        Assert.Null(item.LastUsedAt);
        Assert.Equal(new DateTime(2026, 1, 1), item.SortTime);
    }

    [Fact]
    public void Json_round_trips()
    {
        var item = new SavedItem { Name = "Home", Kind = SavedKind.Text, Value = "hello", Caption = "" };
        var back = JsonSerializer.Deserialize<SavedItem>(JsonSerializer.Serialize(item))!;

        Assert.Equal(item.Id, back.Id);
        Assert.Equal("Home", back.Name);
        Assert.Equal(SavedKind.Text, back.Kind);
        Assert.Equal("hello", back.Value);
        Assert.Equal("", back.Caption);
    }
}
