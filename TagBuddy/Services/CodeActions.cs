using TagBuddy.Models;

namespace TagBuddy.Services;

/// <summary>Shared implementations of the actions offered wherever a code is shown.</summary>
public static class CodeActions
{
    public static async Task ShareImageAsync(Page page, SavedItem item)
    {
        try
        {
            await BarcodeImage.ShareAsync(item);
        }
        catch (Exception ex)
        {
            await page.DisplayAlertAsync("Could not share", ex.Message, "OK");
        }
    }

    public static async Task SaveImageAsync(Page page, SavedItem item)
    {
        try
        {
            var where = await BarcodeImage.SaveToGalleryAsync(item);
            if (where is null)
                await page.DisplayAlertAsync("Not supported", "Saving to the gallery needs Android 10 or newer. Use Share instead.", "OK");
            else
                Notice.Show("Image saved to " + where);
        }
        catch (Exception ex)
        {
            await page.DisplayAlertAsync("Could not save", ex.Message, "OK");
        }
    }

    /// <summary>Hands the value to the app that takes it (<see cref="Handoff"/>): a link, a passkey sign-in, a Wi-Fi
    /// network, a contact, an event. Requests with consequences are confirmed first.</summary>
    public static async Task OpenLinkAsync(Page page, string value)
    {
        if (!Handoff.TryDetect(value, out var handoff))
        {
            await page.DisplayAlertAsync("Nothing to open", "No app on the phone knows what to do with this.", "OK");
            return;
        }
        if (handoff.Confirmation is { } ask && !await page.DisplayAlertAsync(ask.Title, ask.Message, ask.Accept, "Cancel"))
            return;
        if (!await HandoffLauncher.OpenAsync(handoff))
            await page.DisplayAlertAsync("Could not open", handoff.NoAppMessage, "OK");
    }

    public static async Task CopyTextAsync(Page page, string value)
    {
        await Clipboard.Default.SetTextAsync(value);
        Notice.Show("Copied");
    }

    /// <summary>Lets the person pick a group for what is being saved or moved: one of the groups in use, a new one, or
    /// none. Returns the group ("" for none), or null when they cancelled. <paramref name="current"/> is marked.</summary>
    public static async Task<string?> PickGroupAsync(Page page, SavedService saved, string current = "", string title = "Which group?")
    {
        const string none = "No group", fresh = "New group…";
        await saved.LoadAsync();
        var groups = saved.Groups;
        var options = groups.Select(g => string.Equals(g, current, StringComparison.CurrentCultureIgnoreCase) ? $"✓ {g}" : g)
                            .Prepend(current.Length == 0 ? $"✓ {none}" : none).Append(fresh).ToArray();
        var choice = await page.DisplayActionSheetAsync(title, "Cancel", null, options);
        if (choice == $"✓ {none}") choice = none;
        if (choice is null || choice == "Cancel") return null;
        if (choice == none) return "";
        if (choice == fresh)
        {
            var name = await page.DisplayPromptAsync("New group", "A name for the group:", accept: "OK", cancel: "Cancel", maxLength: 40);
            return name is null ? null : name.Trim().Length == 0 ? null : name.Trim();
        }
        return choice.StartsWith("✓ ") ? choice[2..] : choice;
    }

    /// <summary>At save time: the group to file the item under (none, one in use, or a new one).</summary>
    private static Task<string?> GroupForNewAsync(Page page, SavedService saved) =>
        PickGroupAsync(page, saved, title: "Save in which group?");

    /// <summary>Asks for an optional name and keeps the code in Saved, unless the same code is there already.</summary>
    public static async Task SaveCodeAsync(Page page, SavedService saved, SavedItem item, string alreadySavedMessage)
    {
        // The same code may be saved already, with the same picture (nothing to do) or a different one (text under
        // the image, QR options): then saving this one too is a choice, since both are worth keeping.
        var same = saved.SameCodes(item);
        if (same.FirstOrDefault(s => s.ImageDifferences(item).Count == 0) is { } identical)
        {
            await page.DisplayAlertAsync("Already saved", $"{alreadySavedMessage} It looks exactly the same, as \"{identical.DisplayName}\".", "OK");
            return;
        }
        if (same.Count > 0)
        {
            var closest = same.OrderBy(s => s.ImageDifferences(item).Count).First();
            var differs = string.Join(", ", closest.ImageDifferences(item));
            if (!await page.DisplayAlertAsync("Saved with a different look",
                    $"{alreadySavedMessage} As \"{closest.DisplayName}\", but its picture differs in {differs}.\n\nSave this version too?",
                    "Save this version", "Cancel"))
                return;
        }

        var name = await page.DisplayPromptAsync("Save", "Give it a name (optional):",
            accept: "Save", cancel: "Cancel", placeholder: item.Value, maxLength: 60);
        if (name is null) return;

        if (await GroupForNewAsync(page, saved) is not { } group) return;
        item.Name = name.Trim();
        item.Group = group;
        await saved.AddAsync(item);
        Notice.Show(group.Length > 0 ? $"Saved in {group}" : "Saved");
    }

    /// <summary>Asks for an optional name and keeps the text in Saved as a text note, unless it is there already.
    /// From there it can become a QR code, a barcode, or be written to a tag.</summary>
    public static async Task SaveTextAsync(Page page, SavedService saved, string text, string prompt = "Give it a name (optional):")
    {
        if (saved.ContainsText(text))
        {
            await page.DisplayAlertAsync("Already saved", "This content is already saved as text.", "OK");
            return;
        }

        var name = await page.DisplayPromptAsync("Save as text", prompt,
            accept: "Save", cancel: "Cancel", placeholder: text, maxLength: 60);
        if (name is null) return;

        if (await GroupForNewAsync(page, saved) is not { } group) return;
        await saved.AddTextAsync(name, text, group);
        Notice.Show(group.Length > 0 ? $"Saved as text in {group}" : "Saved as text");
    }

    /// <summary>Shares a tag dump as a .bin file through the Android share sheet.</summary>
    public static async Task ShareDumpAsync(Page page, string fileName, byte[] data, string title)
    {
        try
        {
            var path = await SavedService.ExportTempAsync(fileName, data);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = title,
                File = new ShareFile(path, "application/octet-stream"),
            });
        }
        catch (Exception ex)
        {
            await page.DisplayAlertAsync("Could not share", ex.Message, "OK");
        }
    }
}
