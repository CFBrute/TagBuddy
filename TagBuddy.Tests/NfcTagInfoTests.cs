using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Which record of a tag counts as its content: what the NFC tab shows, opens, copies and saves.</summary>
public class NfcTagInfoTests
{
    private static NfcTagInfo Tag(params NfcRecordInfo[] records) => new("04:AA:BB", ["NfcA"], [], records);

    [Fact]
    public void A_link_wins_over_text() =>
        Assert.Equal("https://example.com", Tag(new("Text (en)", "hello"), new("Link", "https://example.com")).Content);

    [Fact]
    public void Text_wins_over_other_records() =>
        Assert.Equal("hello", Tag(new("MIME application/json", "{}"), new("Text (en)", "hello")).Content);

    [Fact]
    public void Any_record_with_text_is_content_when_there_is_no_link_or_text() =>
        Assert.Equal("{}", Tag(new NfcRecordInfo("MIME application/json", "{}")).Content);

    [Fact]
    public void Private_text_is_never_content()
    {
        var tag = Tag(new NfcRecordInfo("Private text", "Encrypted with a passphrase", [1, 2, 3]));
        Assert.Null(tag.Content);
        Assert.False(tag.HasContent);
        Assert.NotNull(tag.SealedRecord);
    }

    [Fact]
    public void Empty_records_are_no_content() =>
        Assert.False(Tag(new NfcRecordInfo("Empty", "")).HasContent);

    [Fact]
    public void The_serial_is_safe_for_a_file_name() =>
        Assert.Equal("04AABB", Tag().SerialForFileName);
}
