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
        NameLabel.Text = item.DisplayName;
        SummaryLabel.Text = $"{item.FormatLabel}  ·  {item.Subtitle}  ·  saved {item.CreatedAt:yyyy-MM-dd HH:mm}";
        TextLabel.Text = item.HasDumpText ? item.DumpText : "No readable text found. Amiibo data is encrypted, so that is expected for an Amiibo.";

        try
        {
            _data = await _saved.ReadDumpAsync(item);
            MemoryLabel.Text = string.Join("\n", NfcReader.FormatDump(_data) ?? []);
        }
        catch (Exception ex)
        {
            MemoryLabel.Text = "Could not read the file: " + ex.Message;
        }
    }

    private async void OnWrite(object? sender, EventArgs e)
    {
        if (_item is null) return;
        await Shell.Current.GoToAsync("//nfc", new Dictionary<string, object> { ["dump"] = _item.Id.ToString() });
    }

    private async void OnShare(object? sender, EventArgs e)
    {
        if (_item is null || _data is null) return;
        try
        {
            var path = await SavedService.ExportTempAsync(_item.DumpFile, _data);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = _item.DisplayName,
                File = new ShareFile(path, "application/octet-stream"),
            });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not share", ex.Message, "OK");
        }
    }

    private async void OnCopyHex(object? sender, EventArgs e)
    {
        if (_data is null) return;
        await Clipboard.Default.SetTextAsync(MemoryLabel.Text);
        Notice.Show($"{_data.Length / 4} pages copied");
    }
}
