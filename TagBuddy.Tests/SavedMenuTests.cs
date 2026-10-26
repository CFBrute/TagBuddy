using TagBuddy.Models;

namespace TagBuddy.Tests;

/// <summary>The Saved menu: short, the right actions for each kind, and nothing lost when it was regrouped.</summary>
public class SavedMenuTests
{
    private static string[] Labels(IEnumerable<MenuEntry> menu) => menu.Select(e => e.Label).ToArray();

    private static MenuEntry Group(IEnumerable<MenuEntry> menu, string label) =>
        Assert.Single(menu, e => e.Label == label);

    private static IEnumerable<SavedAction> AllActions(IEnumerable<MenuEntry> menu) =>
        menu.SelectMany(e => e.Children is { } c ? AllActions(c) : [e.Action!.Value]);

    [Fact]
    public void A_saved_QR_code_of_a_link_has_six_lines()
    {
        var item = new SavedItem { Kind = SavedKind.Code, Format = "QrCode", Value = "https://youtube.com/shorts/abc" };
        var menu = SavedMenu.For(item, "YouTube");

        Assert.Equal(["Open in YouTube", "View page in app", "QR code image…", "Copy text", "Use the text…", "Rename", "Move to a group"], Labels(menu));
        Assert.Equal(["Share image", "Save to gallery"], Labels(Group(menu, "QR code image…").Children!));
        Assert.Equal(["In the QR generator", "In the Barcode generator", "Write it to an NFC tag"],
            Labels(Group(menu, "Use the text…").Children!));
    }

    [Fact]
    public void A_barcode_names_its_image_and_puts_its_own_generator_first()
    {
        var menu = SavedMenu.For(new SavedItem { Kind = SavedKind.Code, Format = "Ean13", Value = "400638133393" });

        Assert.Equal(["Barcode image…", "Copy text", "Use the text…", "Rename", "Move to a group"], Labels(menu));
        Assert.Equal("In the Barcode generator", Group(menu, "Use the text…").Children![0].Label);
    }

    [Fact]
    public void A_text_note_can_be_edited_and_made_into_a_dump()
    {
        var menu = SavedMenu.For(new SavedItem { Kind = SavedKind.Text, Value = "Door code 4711" });

        Assert.Equal(["Copy text", "Use the text…", "Edit text", "Rename", "Move to a group"], Labels(menu));
        Assert.Contains(SavedAction.ToDump, AllActions(menu));
        Assert.DoesNotContain(SavedAction.ShareImage, AllActions(menu));   // a note has no image
    }

    [Fact]
    public void A_link_without_a_known_app_opens_as_a_link()
    {
        var menu = SavedMenu.For(new SavedItem { Kind = SavedKind.Text, Value = "https://example.com" });
        Assert.Equal("Open link", menu[0].Label);
    }

    [Fact]
    public void A_phone_number_offers_a_call_but_no_page()
    {
        var menu = SavedMenu.For(new SavedItem { Kind = SavedKind.Text, Value = "tel:+46700000000" });
        Assert.Equal("Call number", menu[0].Label);
        Assert.DoesNotContain(SavedAction.ViewPage, AllActions(menu));
    }

    [Fact]
    public void A_dump_offers_what_fits_a_dump()
    {
        var withText = new SavedItem { Kind = SavedKind.Dump, DumpChip = "NTAG215", DumpText = "Hello" };
        Assert.Equal(["Write to a tag", "Share .bin file", "Copy readable text", "Rename", "Move to a group"], Labels(SavedMenu.For(withText)));

        var withoutText = new SavedItem { Kind = SavedKind.Dump, DumpChip = "NTAG215" };
        Assert.DoesNotContain("Copy readable text", Labels(SavedMenu.For(withoutText)));
    }

    [Fact]
    public void The_tap_action_is_not_repeated_in_the_menu()
    {
        // Tapping a code shows it full screen and tapping a dump shows its memory; the menu has the rest.
        var code = SavedMenu.For(new SavedItem { Kind = SavedKind.Code, Format = "QrCode", Value = "x" });
        var dump = SavedMenu.For(new SavedItem { Kind = SavedKind.Dump });
        Assert.DoesNotContain("Show full screen", Labels(code));
        Assert.DoesNotContain("View memory", Labels(dump));
    }

    [Fact]
    public void Groups_read_as_groups()
    {
        var menu = SavedMenu.For(new SavedItem { Kind = SavedKind.Code, Format = "QrCode", Value = "x" });
        Assert.All(menu, e => Assert.Equal(e.IsGroup, e.Label.EndsWith('…')));
        Assert.All(menu.Where(e => e.IsGroup), e => Assert.True(e.Children!.Count > 1));
    }

    [Fact]
    public void The_group_line_names_the_group_the_item_is_in()
    {
        Assert.Equal("Move to a group", SavedMenu.GroupLabel(new SavedItem()));
        Assert.Equal("Group: Work", SavedMenu.GroupLabel(new SavedItem { Group = "Work" }));
        Assert.EndsWith("Group: Work", SavedMenu.For(new SavedItem { Kind = SavedKind.Text, Value = "x", Group = "Work" }).Last().Label);
    }
}
