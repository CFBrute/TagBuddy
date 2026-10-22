using System.Collections.ObjectModel;
using System.Collections.Specialized;
using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>Everything kept in the app: codes, text notes and tag dumps, with a kind filter and a menu of
/// actions that fits each kind.</summary>
public partial class SavedPage : ContentPage
{
    private const string ActionShow = "Show full screen";
    private const string ActionViewPage = "View page in app";
    private const string ActionShare = "Share image";
    private const string ActionSaveImage = "Save image to gallery";
    private const string ActionBarcode = "Use text in Barcode generator";
    private const string ActionQr = "Use text in QR generator";
    private const string ActionWriteNfc = "Write to an NFC tag";
    private const string ActionCopy = "Copy text";
    private const string ActionEditText = "Edit text";
    private const string ActionTextToDump = "Make a tag dump from this text";
    private const string ActionViewDump = "View memory";
    private const string ActionWriteDump = "Write dump to a tag";
    private const string ActionShareDump = "Share .bin file";
    private const string ActionCopyDumpText = "Copy readable text";
    private const string ActionRename = "Rename";
    private const string ActionDelete = "Delete";

    private static readonly Color Active = Color.FromArgb("#F5A623");
    private static readonly Color ActiveText = Color.FromArgb("#1C1200");
    private static readonly Color Inactive = Color.FromArgb("#6B7280");

    private readonly SavedService _saved;
    private readonly ObservableCollection<SavedItem> _shown = [];
    private SavedKind? _filter;

    public SavedPage(SavedService saved)
    {
        InitializeComponent();
        _saved = saved;
        List.ItemsSource = _shown;
        VersionLabel.Text = BuildInfo.Label;
        BuildLabel.Text = BuildInfo.Details;
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
        Highlight(FilterAll, _filter is null);
        Highlight(FilterCodes, _filter == SavedKind.Code);
        Highlight(FilterTexts, _filter == SavedKind.Text);
        Highlight(FilterDumps, _filter == SavedKind.Dump);
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

    private static void Highlight(Button button, bool on)
    {
        button.BackgroundColor = on ? Active : Inactive;
        button.TextColor = on ? ActiveText : Colors.White;
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

    private async Task ShowMenuAsync(SavedItem item)
    {
        var actions = new List<string>();
        Uri? uri = null;
        var isLink = !item.IsDump && Links.TryGetOpenable(item.Value, out uri);
        var openLabel = isLink && uri is not null ? Links.OpenLabel(uri) : string.Empty;

        if (item.IsCode) actions.Add(ActionShow);
        if (isLink) actions.Add(openLabel);
        if (!item.IsDump && Links.IsWebLink(item.Value, out _)) actions.Add(ActionViewPage);

        if (item.IsCode) actions.AddRange([ActionShare, ActionSaveImage]);
        if (item.IsDump) actions.AddRange([ActionViewDump, ActionWriteDump, ActionShareDump]);
        if (item.HasDumpText) actions.Add(ActionCopyDumpText);
        if (!item.IsDump) actions.AddRange([ActionBarcode, ActionQr, ActionWriteNfc, ActionCopy]);
        if (item.IsText) actions.AddRange([ActionEditText, ActionTextToDump]);
        actions.Add(ActionRename);

        var choice = await DisplayActionSheetAsync(item.DisplayName, "Cancel", ActionDelete, actions.ToArray());

        if (isLink && choice == openLabel)
        {
            await CodeActions.OpenLinkAsync(this, item.Value);
            return;
        }

        switch (choice)
        {
            case ActionShow:
                await ShowBarcodePage.ShowAsync(item);
                break;

            case ActionViewPage:
                await WebPage.ShowAsync(item.Value);
                break;

            case ActionShare:
                await CodeActions.ShareImageAsync(this, item);
                break;

            case ActionSaveImage:
                await CodeActions.SaveImageAsync(this, item);
                break;

            case ActionBarcode:
                await Shell.Current.GoToAsync("//generate/barcode", new Dictionary<string, object>
                {
                    ["value"] = item.Value,
                    ["format"] = item.IsCode && !item.IsQr ? item.Format : "Code128",
                });
                break;

            case ActionQr:
                await Shell.Current.GoToAsync("//generate/qr", new Dictionary<string, object>
                {
                    ["value"] = item.Value,
                    ["ecc"] = item.QrErrorCorrection,
                    ["margin"] = item.QrMargin.ToString(),
                });
                break;

            case ActionWriteNfc:
                await Shell.Current.GoToAsync("//nfc", new Dictionary<string, object> { ["text"] = item.Value });
                break;

            case ActionCopy:
                await CodeActions.CopyTextAsync(this, item.Value);
                break;

            case ActionEditText:
                await EditTextAsync(item);
                break;

            case ActionTextToDump:
                // Raw UTF-8 bytes from page 4 on, the way "Raw bytes" on the NFC tab writes them; the dump
                // can then be written to any Type 2 tag, Amiibo-formatted ones included.
                var dump = await _saved.AddDumpAsync(item.DisplayName, "", "Text", DumpText.FromText(item.Value));
                Notice.Show($"Dump made: {dump.DumpSize} bytes. Find it under Tags.");
                break;

            case ActionViewDump:
                await DumpPage.ShowAsync(item);
                break;

            case ActionWriteDump:
                await Shell.Current.GoToAsync("//nfc", new Dictionary<string, object> { ["dump"] = item.Id.ToString() });
                break;

            case ActionShareDump:
                await ShareDumpAsync(item);
                break;

            case ActionCopyDumpText:
                await CodeActions.CopyTextAsync(this, item.DumpText);
                break;

            case ActionRename:
                await RenameAsync(item);
                break;

            case ActionDelete:
                await DeleteAsync(item);
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
        try
        {
            var data = await _saved.ReadDumpAsync(item);
            var path = await SavedService.ExportTempAsync(item.DumpFile, data);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = item.DisplayName,
                File = new ShareFile(path, "application/octet-stream"),
            });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not share", ex.Message, "OK");
        }
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
