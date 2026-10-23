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

    public static async Task OpenLinkAsync(Page page, string value)
    {
        if (!Links.TryGetOpenable(value, out var uri))
        {
            await page.DisplayAlertAsync("Not a link", "This value is not something the phone can open.", "OK");
            return;
        }
        if (!await Links.OpenAsync(uri))
            await page.DisplayAlertAsync("Could not open", uri.ToString(), "OK");
    }

    public static async Task CopyTextAsync(Page page, string value)
    {
        await Clipboard.Default.SetTextAsync(value);
        Notice.Show("Copied");
    }

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

        item.Name = name.Trim();
        await saved.AddAsync(item);
        Notice.Show("Saved");
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

        await saved.AddTextAsync(name, text);
        Notice.Show("Saved as text");
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
