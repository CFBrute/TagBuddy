using System.Collections.ObjectModel;
using System.Collections.Specialized;
using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>Everything kept in the app: codes, text notes and tag dumps, with a kind filter and a menu of
/// actions that fits each kind.</summary>
public partial class SavedPage : ContentPage
{
    private readonly SavedService _saved;
    private readonly ObservableCollection<SavedItem> _shown = [];
    private SavedKind? _filter;

    public SavedPage(SavedService saved)
    {
        InitializeComponent();
        _saved = saved;
        List.ItemsSource = _shown;
        _saved.Items.CollectionChanged += OnItemsChanged;
        ApplyFilter();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _saved.LoadAsync();
        Refill();
    }

    // ---------------------------------------------------------------- filter

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refill();

    private void OnFilterAll(object? sender, EventArgs e) { _filter = null; ApplyFilter(); }
    private void OnFilterCodes(object? sender, EventArgs e) { _filter = SavedKind.Code; ApplyFilter(); }
    private void OnFilterTexts(object? sender, EventArgs e) { _filter = SavedKind.Text; ApplyFilter(); }
    private void OnFilterDumps(object? sender, EventArgs e) { _filter = SavedKind.Dump; ApplyFilter(); }

    private void ApplyFilter()
    {
        Palette.Select(FilterAll, _filter is null);
        Palette.Select(FilterCodes, _filter == SavedKind.Code);
        Palette.Select(FilterTexts, _filter == SavedKind.Text);
        Palette.Select(FilterDumps, _filter == SavedKind.Dump);
        EmptyTitle.Text = _filter switch
        {
            SavedKind.Code => "No saved codes",
            SavedKind.Text => "No saved text",
            SavedKind.Dump => "No saved tag dumps",
            _ => "Nothing saved yet",
        };
        EmptyHint.Text = _filter switch
        {
            SavedKind.Code => "Generate or scan a code and tap Save.",
            SavedKind.Text => "Tap + Text here, or save the content of a scanned code or an NFC tag as text.",
            SavedKind.Dump => "Read a tag on the NFC tab and tap Save dump, or import a .bin file there.",
            _ => "Codes you generate or scan, text, and tag dumps you save end up here.",
        };
        Refill();
    }

    private void Refill()
    {
        _shown.Clear();
        foreach (var item in _saved.Items)
            if (_filter is null || item.Kind == _filter) _shown.Add(item);
    }

    // ---------------------------------------------------------------- add text

    private async void OnNewText(object? sender, EventArgs e)
    {
        try
        {
            var text = await DisplayPromptAsync("Save text", "The text to keep:", accept: "Next", cancel: "Cancel", maxLength: 4000);
            if (string.IsNullOrWhiteSpace(text)) return;
            var name = await DisplayPromptAsync("Name", "Give it a name (optional):", accept: "Save", cancel: "Cancel",
                placeholder: text.Trim(), maxLength: 60);
            if (name is null) return;
            await _saved.AddTextAsync(name, text.Trim());
            _filter = _filter == SavedKind.Text ? _filter : null;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not save", ex.Message, "OK");
        }
    }

    // ---------------------------------------------------------------- tap and menu

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not SavedItem item) return;
        try
        {
            if (item.IsCode) await ShowBarcodePage.ShowAsync(item);
            else if (item.IsDump) await DumpPage.ShowAsync(item);
            else await ShowMenuAsync(item);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not open", ex.Message, "OK");
        }
    }

    private async void OnMenuClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: SavedItem item })
        {
            try { await ShowMenuAsync(item); }
            catch (Exception ex) { await DisplayAlertAsync("Could not do that", ex.Message, "OK"); }
        }
    }

    /// <summary>The item's menu (<see cref="SavedMenu"/>). A group opens a second menu whose "Back" returns here.</summary>
    private async Task ShowMenuAsync(SavedItem item)
    {
        var appName = Links.IsWebLink(item.Value, out var web) ? Links.HandlerName(web) : null;
        var menu = SavedMenu.For(item, appName);
        while (true)
        {
            var choice = await DisplayActionSheetAsync(item.DisplayName, "Cancel", SavedMenu.Delete, menu.Select(e => e.Label).ToArray());
            if (choice == SavedMenu.Delete)
            {
                await DeleteAsync(item);
                return;
            }
            if (menu.FirstOrDefault(e => e.Label == choice) is not { } entry) return;   // Cancel, or tapped outside

            if (entry.Children is { } children)
            {
                var sub = await DisplayActionSheetAsync(entry.Label.TrimEnd('…'), "Back", null, children.Select(c => c.Label).ToArray());
                if (sub == "Back") continue;
                if (children.FirstOrDefault(c => c.Label == sub)?.Action is not { } picked) return;
                await RunAsync(item, picked);
                return;
            }
            if (entry.Action is { } action) await RunAsync(item, action);
            return;
        }
    }

    private async Task RunAsync(SavedItem item, SavedAction action)
    {
        switch (action)
        {
            case SavedAction.Open:
                await CodeActions.OpenLinkAsync(this, item.Value);
                break;

            case SavedAction.ViewPage:
                await WebPage.ShowAsync(item.Value);
                break;

            case SavedAction.ShareImage:
                await CodeActions.ShareImageAsync(this, item);
                break;

            case SavedAction.SaveImage:
                await CodeActions.SaveImageAsync(this, item);
                break;

            case SavedAction.ToBarcode:
                await AppNavigation.ToBarcodeGeneratorAsync(item.Value,
                    item.IsCode && !item.IsQr ? item.Format : BarcodeTypes.Linear[0].Key);
                break;

            case SavedAction.ToQr:
                await AppNavigation.ToQrGeneratorAsync(item.Value, item.QrErrorCorrection, item.QrMargin);
                break;

            case SavedAction.ToNfc:
                await AppNavigation.ToNfcWriteTextAsync(item.Value);
                break;

            case SavedAction.Copy:
                await CodeActions.CopyTextAsync(this, item.Value);
                break;

            case SavedAction.EditText:
                await EditTextAsync(item);
                break;

            case SavedAction.ToDump:
                // Raw UTF-8 bytes from page 4 on, the way "Raw bytes" on the NFC tab writes them; the dump
                // can then be written to any Type 2 tag, Amiibo-formatted ones included.
                var dump = await _saved.AddDumpAsync(item.DisplayName, "", "Text", DumpText.FromText(item.Value));
                Notice.Show($"Dump made: {dump.DumpSize} bytes. Find it under Tags.");
                break;

            case SavedAction.WriteDump:
                await AppNavigation.ToNfcWriteDumpAsync(item);
                break;

            case SavedAction.ShareDump:
                await ShareDumpAsync(item);
                break;

            case SavedAction.CopyDumpText:
                await CodeActions.CopyTextAsync(this, item.DumpText);
                break;

            case SavedAction.Rename:
                await RenameAsync(item);
                break;
        }
    }

    private async Task EditTextAsync(SavedItem item)
    {
        var text = await DisplayPromptAsync("Edit text", null, accept: "Save", cancel: "Cancel", initialValue: item.Value, maxLength: 4000);
        if (text is null || text.Trim().Length == 0) return;
        item.Value = text.Trim();
        await _saved.UpdateAsync(item);
    }

    private async Task RenameAsync(SavedItem item)
    {
        var name = await DisplayPromptAsync("Rename", "A name for this item (empty shows its content):",
            accept: "Save", cancel: "Cancel", initialValue: item.Name, placeholder: item.DisplayName, maxLength: 60);
        if (name is null) return;
        item.Name = name.Trim();
        await _saved.UpdateAsync(item);
    }

    private async Task ShareDumpAsync(SavedItem item)
    {
        byte[] data;
        try { data = await _saved.ReadDumpAsync(item); }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not share", ex.Message, "OK");
            return;
        }
        await CodeActions.ShareDumpAsync(this, item.DumpFile, data, item.DisplayName);
    }

    private async void OnDeleteInvoked(object? sender, EventArgs e)
    {
        if (sender is SwipeItem { CommandParameter: SavedItem item })
            await DeleteAsync(item);
    }

    private async Task DeleteAsync(SavedItem item)
    {
        var what = item.Kind switch { SavedKind.Dump => "tag dump", SavedKind.Text => "text", _ => "code" };
        var ok = await DisplayAlertAsync($"Delete {what}", $"Remove \"{item.DisplayName}\"?", "Delete", "Cancel");
        if (ok)
            await _saved.RemoveAsync(item);
    }
}
