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
}
