using TagBuddy.Services;

namespace TagBuddy.Models;

/// <summary>Everything the Saved menu can do with an item.</summary>
public enum SavedAction
{
    Open, ViewPage, ShareImage, SaveImage, ToQr, ToBarcode, ToNfc, ToDump, Copy, EditText, Rename, MoveToGroup,
    WriteDump, ShareDump, CopyDumpText,
}

/// <summary>One line of the menu: an action, or a group (<paramref name="Children"/>) that opens a second menu.
/// Group labels end in "…" so they read as "more choices behind this".</summary>
public sealed record MenuEntry(string Label, SavedAction? Action = null, IReadOnlyList<MenuEntry>? Children = null)
{
    public bool IsGroup => Children is not null;
}

/// <summary>The menu of a saved item, kept short. Tapping a row already does the main thing (a code opens full
/// screen, a dump shows its memory), so the menu leaves that out. What people do most stays on the first level;
/// the image actions and "use the text elsewhere" are grouped one level down.</summary>
public static class SavedMenu
{
    public const string Delete = "Delete";

    /// <param name="appName">The app the value opens in ("YouTube", an authenticator), when known, for the open
    /// line's label.</param>
    public static IReadOnlyList<MenuEntry> For(SavedItem item, string? appName = null)
    {
        var menu = new List<MenuEntry>();

        if (item.IsDump)
        {
            menu.Add(new("Write to a tag", SavedAction.WriteDump));
            menu.Add(new("Share .bin file", SavedAction.ShareDump));
            if (item.HasDumpText) menu.Add(new("Copy readable text", SavedAction.CopyDumpText));
            menu.Add(new("Rename", SavedAction.Rename));
            menu.Add(new(GroupLabel(item), SavedAction.MoveToGroup));
            return menu;
        }

        if (Handoff.TryDetect(item.Value, out var handoff))
            menu.Add(new(handoff.Label(appName), SavedAction.Open));
        if (Links.IsWebLink(item.Value, out _))
            menu.Add(new("View page in app", SavedAction.ViewPage));

        if (item.IsCode)
        {
            var image = item.IsQr ? "QR code image" : "Barcode image";
            menu.Add(Group($"{image}…", new("Share image", SavedAction.ShareImage), new("Save to gallery", SavedAction.SaveImage)));
        }

        menu.Add(new("Copy text", SavedAction.Copy));

        // A code's own generator comes first: for a saved QR code, "QR generator" is where it is edited.
        var generators = item.IsCode && !item.IsQr
            ? new[] { new MenuEntry("In the Barcode generator", SavedAction.ToBarcode), new MenuEntry("In the QR generator", SavedAction.ToQr) }
            : [new MenuEntry("In the QR generator", SavedAction.ToQr), new MenuEntry("In the Barcode generator", SavedAction.ToBarcode)];
        var useText = new List<MenuEntry>(generators) { new("Write it to an NFC tag", SavedAction.ToNfc) };
        if (item.IsText) useText.Add(new("Make a tag dump from it", SavedAction.ToDump));
        menu.Add(Group("Use the text…", [.. useText]));

        if (item.IsText) menu.Add(new("Edit text", SavedAction.EditText));
        menu.Add(new("Rename", SavedAction.Rename));
        menu.Add(new(GroupLabel(item), SavedAction.MoveToGroup));
        return menu;
    }

    /// <summary>"Move to a group" for an item in none, "Group: Work" for one filed under Work.</summary>
    public static string GroupLabel(SavedItem item) => item.HasGroup ? $"Group: {item.Group}" : "Move to a group";

    /// <summary>A group; a group of one is no group, just that line.</summary>
    private static MenuEntry Group(string label, params MenuEntry[] children) =>
        children.Length == 1 ? children[0] : new MenuEntry(label, Children: children);
}
