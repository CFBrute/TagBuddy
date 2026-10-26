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
    /// <summary>What the list shows: saved items, and (when groups are used) a <see cref="GroupRow"/> heading before
    /// each group's items. A collapsed group contributes only its heading, which keeps the list short.</summary>
    private readonly ObservableCollection<object> _shown = [];
    private SavedKind? _filter;

    /// <summary>Groups currently expanded; collapsed by default so a filed-away group stays out of the way, except the
    /// most recent one, which is opened once when the list is first built (<see cref="_autoOpenedTop"/>).</summary>
    private readonly HashSet<string> _expandedGroups = new(StringComparer.CurrentCultureIgnoreCase);
    private bool _autoOpenedTop;

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
        // Whatever was used since the list was last shown moves to the top now, not while it was being looked at.
        _saved.SortByUse();
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
            SavedKind.Text => "Save the content of a scanned code or an NFC tag as text.",
            SavedKind.Dump => "Read a tag on the NFC tab and tap \"Save tag as read\", or import a .bin file there.",
            _ => "Codes you generate or scan, text, and tag dumps you save end up here.",
        };
        Refill();
    }

    /// <summary>Rebuilds the list. With no groups in use it is a plain list. Once anything is filed, every item is
    /// under a heading — the ungrouped ones under "No group" — and the groups are ordered so the one holding the most
    /// recently added or used item comes first. Items are already sorted by recent use, so a group's first item is its
    /// most recent, and taking the groups in first-seen order puts the most recent group on top. A group shows its
    /// items only when open.</summary>
    private void Refill()
    {
        var items = _saved.Items.Where(i => _filter is null || i.Kind == _filter).ToList();
        var groups = items.GroupBy(i => i.Group.Trim(), StringComparer.CurrentCultureIgnoreCase).ToList();

        _shown.Clear();
        if (!groups.Any(g => g.Key.Length > 0))   // nothing filed: a plain list, no headings or indent
        {
            foreach (var item in items) { item.UnderGroup = false; _shown.Add(item); }
            return;
        }

        // Open the most recent group (the first one) once, the first time the list is built with groups.
        if (!_autoOpenedTop)
        {
            _expandedGroups.Add(groups[0].Key);
            _autoOpenedTop = true;
        }

        int index = 0;
        foreach (var group in groups)
        {
            bool groupAlt = index++ % 2 == 1;
            bool expanded = _expandedGroups.Contains(group.Key);
            _shown.Add(new GroupRow(group.Key, group.Key.Length == 0 ? "No group" : group.Key, group.Count(), expanded, groupAlt));
            if (expanded)
            {
                int row = 0, last = group.Count() - 1;
                foreach (var item in group)
                {
                    item.UnderGroup = true;
                    item.GroupAlt = groupAlt;
                    item.FirstInGroup = row == 0;
                    item.LastInGroup = row == last;
                    item.RowAlt = row++ % 2 == 1;
                    _shown.Add(item);
                }
            }
        }
    }

    /// <summary>Opens or closes a group.</summary>
    private void OnGroupHeaderTapped(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GroupRow row) return;
        if (!_expandedGroups.Remove(row.Key)) _expandedGroups.Add(row.Key);
        Refill();

        // Rebuilding the list resets its scroll to the top, which shoots the view away from the group that was
        // just tapped. Put that heading back at the top so opening a group happens where the person is looking.
        var heading = _shown.OfType<GroupRow>().FirstOrDefault(g => g.Key == row.Key);
        if (heading is not null) List.ScrollTo(heading, position: ScrollToPosition.Start, animate: false);
    }

    // ---------------------------------------------------------------- select mode (long press)

    private bool _selecting;

    private static SavedItem? ItemOf(object? row) => (row as BindableObject)?.BindingContext as SavedItem;

    private void OnRowLongPressed(object? sender, EventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!_selecting)
        {
            _selecting = true;
            FilterBar.IsVisible = false;
            SelectBar.IsVisible = true;
        }
        Toggle(item);
    }

    private void Toggle(SavedItem item)
    {
        item.IsSelected = !item.IsSelected;
        int count = _saved.Items.Count(i => i.IsSelected);
        if (count == 0)
        {
            StopSelecting();
            return;
        }
        SelectCount.Text = $"{count} selected";
        SelectAllButton.IsVisible = Visible().Any(i => !i.IsSelected);
    }

    /// <summary>The saved items on screen now (a collapsed group's items are not among them).</summary>
    private IEnumerable<SavedItem> Visible() => _shown.OfType<SavedItem>();

    private void StopSelecting()
    {
        _selecting = false;
        foreach (var item in _saved.Items) item.IsSelected = false;
        SelectBar.IsVisible = false;
        FilterBar.IsVisible = true;
        Refill();   // a group move while selecting changes which groups exist
    }

    private void OnCancelSelect(object? sender, EventArgs e) => StopSelecting();

    /// <summary>Picks every item on screen (items inside collapsed groups are left as they are).</summary>
    private void OnSelectAll(object? sender, EventArgs e)
    {
        var visible = Visible().ToList();
        foreach (var item in visible) item.IsSelected = true;
        SelectCount.Text = $"{visible.Count} selected";
        SelectAllButton.IsVisible = false;
    }

    /// <summary>Moves every picked item to a group (or out of its group).</summary>
    private async void OnGroupSelected(object? sender, EventArgs e)
    {
        var picked = _saved.Items.Where(i => i.IsSelected).ToList();
        if (picked.Count == 0) return;
        var shared = picked.Select(i => i.Group.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var current = shared.Count == 1 ? shared[0] : "";
        var what = picked.Count == 1 ? $"\"{picked[0].DisplayName}\"" : $"{picked.Count} items";
        if (await CodeActions.PickGroupAsync(this, _saved, current, $"Move {what} to which group?") is not { } group) return;
        try
        {
            await _saved.SetGroupAsync(picked, group);
            if (group.Length > 0) _expandedGroups.Add(group);   // open the group they went to
            Notice.Show(group.Length > 0 ? $"Moved to {group}" : "Taken out of the group");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not move", ex.Message, "OK");
        }
        StopSelecting();
    }

    private async void OnDeleteSelected(object? sender, EventArgs e)
    {
        var picked = _saved.Items.Where(i => i.IsSelected).ToList();
        if (picked.Count == 0) return;
        var what = picked.Count == 1 ? $"\"{picked[0].DisplayName}\"" : $"these {picked.Count} items";
        if (!await DisplayAlertAsync("Delete", $"Remove {what} from Saved? This cannot be undone.", "Delete", "Cancel"))
            return;
        try
        {
            await _saved.RemoveManyAsync(picked);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not delete", ex.Message, "OK");
        }
        StopSelecting();
    }

    /// <summary>Back leaves select mode before it leaves the page.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (!_selecting) return base.OnBackButtonPressed();
        StopSelecting();
        return true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_selecting) StopSelecting();
    }

    // ---------------------------------------------------------------- tap and menu

    private async void OnRowPressed(object? sender, EventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (_selecting)
        {
            Toggle(item);
            return;
        }
        try
        {
            if (item.IsCode || item.IsDump) await _saved.MarkUsedAsync(item);
            if (item.IsCode) await ShowBarcodePage.ShowAsync(item);
            else if (item.HasRead) await AppNavigation.ToNfcViewReadAsync(item);   // the whole read, not just the bytes
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
            if (_selecting)
            {
                Toggle(item);
                return;
            }
            try { await ShowMenuAsync(item); }
            catch (Exception ex) { await DisplayAlertAsync("Could not do that", ex.Message, "OK"); }
        }
    }

    /// <summary>The item's menu (<see cref="SavedMenu"/>). A group opens a second menu whose "Back" returns here.</summary>
    private async Task ShowMenuAsync(SavedItem item)
    {
        var appName = Handoff.TryDetect(item.Value, out var handoff) ? HandoffLauncher.AppName(handoff) : null;
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
        // Doing something with the item counts as using it; tidying it (rename, edit, group) does not.
        if (action is not (SavedAction.Rename or SavedAction.EditText or SavedAction.MoveToGroup))
            await _saved.MarkUsedAsync(item);

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

            case SavedAction.ViewRead:
                await AppNavigation.ToNfcViewReadAsync(item);
                break;

            case SavedAction.EditRead:
                await AppNavigation.ToNfcEditReadAsync(item);
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

            case SavedAction.MoveToGroup:
                if (await CodeActions.PickGroupAsync(this, _saved, item.Group, $"Move \"{item.DisplayName}\" to which group?") is { } group)
                {
                    await _saved.SetGroupAsync([item], group);
                    if (group.Length > 0) _expandedGroups.Add(group);   // open the group it went to, so it is seen
                    Refill();
                    Notice.Show(group.Length > 0 ? $"Moved to {group}" : "Taken out of the group");
                }
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

    private async Task DeleteAsync(SavedItem item)
    {
        var what = item.Kind switch { SavedKind.Dump => "tag dump", SavedKind.Text => "text", _ => "code" };
        var ok = await DisplayAlertAsync($"Delete {what}", $"Remove \"{item.DisplayName}\"?", "Delete", "Cancel");
        if (ok)
            await _saved.RemoveAsync(item);
    }
}
