using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>Shows the picture a saved code or text was scanned from (kept when Settings says so), with a share
/// button and a way to drop the picture while keeping the item.</summary>
public partial class ScanPhotoPage : ContentPage, IQueryAttributable
{
    public const string Route = "scan-photo";

    private readonly SavedService _saved;
    private SavedItem? _item;

    public ScanPhotoPage(SavedService saved)
    {
        InitializeComponent();
        _saved = saved;
    }

    public static Task ShowAsync(SavedItem item) =>
        Shell.Current.GoToAsync(Route, new Dictionary<string, object> { ["item"] = item });

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (!query.TryGetValue("item", out var obj) || obj is not SavedItem item) return;
        _item = item;
        Photo.Source = item.HasScanPhoto ? ImageSource.FromFile(_saved.ScanPhotoPath(item)) : null;
        NameLabel.Text = item.DisplayName;
        SummaryLabel.Text = $"{item.FormatLabel}  ·  scanned {item.CreatedAt:yyyy-MM-dd HH:mm}";
    }

    /// <summary>Shares a copy from the cache folder, the same way code images are shared.</summary>
    private async void OnShare(object? sender, EventArgs e)
    {
        if (_item is null || !_item.HasScanPhoto) return;
        try
        {
            var path = Path.Combine(FileSystem.CacheDirectory, $"scanned-{_item.ScanPhoto}");
            File.Copy(_saved.ScanPhotoPath(_item), path, overwrite: true);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = _item.DisplayName,
                File = new ShareFile(path, "image/jpeg"),
            });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not share", ex.Message, "OK");
        }
    }

    private async void OnDelete(object? sender, EventArgs e)
    {
        if (_item is null) return;
        if (!await DisplayAlertAsync("Delete picture", $"Delete the picture \"{_item.DisplayName}\" was scanned from? The saved item stays.", "Delete", "Cancel"))
            return;
        await _saved.RemoveScanPhotoAsync(_item);
        Notice.Show("Picture deleted");
        await Shell.Current.GoToAsync("..");
    }
}
