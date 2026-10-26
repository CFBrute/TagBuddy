using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>Shows a saved tag dump: what is known about it, the readable text inside, and the page memory.</summary>
public partial class DumpPage : ContentPage, IQueryAttributable
{
    public const string Route = "dump";

    private readonly SavedService _saved;
    private SavedItem? _item;
    private byte[]? _data;

    public DumpPage(SavedService saved)
    {
        InitializeComponent();
        _saved = saved;
    }

    public static Task ShowAsync(SavedItem item) =>
        Shell.Current.GoToAsync(Route, new Dictionary<string, object> { ["item"] = item });

    public async void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (!query.TryGetValue("item", out var obj) || obj is not SavedItem item) return;
        _item = item;
        WriteButton.IsVisible = item.IsType2Dump;   // a Classic or ISO 15693 read is written through the editor
        NameLabel.Text = item.DisplayName;
        SummaryLabel.Text = $"{item.FormatLabel}  ·  {item.Subtitle}  ·  saved {item.CreatedAt:yyyy-MM-dd HH:mm}";
        TextLabel.Text = item.HasDumpText ? item.DumpText
            : Features.Amiibo ? "No readable text found. Amiibo data is encrypted, so that is expected for an Amiibo."
            : "No readable text found.";

        try
        {
            _data = await _saved.ReadDumpAsync(item);
            MemoryMapView.Show(MemoryOverview, MemoryLabel, _data, item.DumpChip);
        }
        catch (Exception ex)
        {
            MemoryLabel.Text = "Could not read the file: " + ex.Message;
        }
    }

    private async void OnWrite(object? sender, EventArgs e)
    {
        if (_item is null) return;
        await AppNavigation.ToNfcWriteDumpAsync(_item);
    }

    private async void OnShare(object? sender, EventArgs e)
    {
        if (_item is not null && _data is not null)
            await CodeActions.ShareDumpAsync(this, _item.DumpFile, _data, _item.DisplayName);
    }

    private async void OnCopyHex(object? sender, EventArgs e)
    {
        if (_data is null) return;
        // From the bytes: the label holds coloured spans, whose Text is empty.
        await Clipboard.Default.SetTextAsync(string.Join("\n", NfcReader.FormatDump(_data) ?? []));
        Notice.Show($"{_data.Length / 4} pages copied");
    }
}
