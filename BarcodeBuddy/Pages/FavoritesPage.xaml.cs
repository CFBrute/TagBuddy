using BarcodeBuddy.Models;
using BarcodeBuddy.Services;

namespace BarcodeBuddy.Pages;

public partial class FavoritesPage : ContentPage
{
    private const string ActionShow = "Show full screen";
    private const string ActionViewPage = "View page in app";
    private const string ActionShare = "Share image";
    private const string ActionSaveImage = "Save image to gallery";
    private const string ActionBarcode = "Use text in Barcode generator";
    private const string ActionQr = "Use text in QR generator";
    private const string ActionCopy = "Copy text";
    private const string ActionDelete = "Delete";

    private readonly FavoritesService _favorites;

    public FavoritesPage(FavoritesService favorites)
    {
        InitializeComponent();
        _favorites = favorites;
        List.ItemsSource = _favorites.Items;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _favorites.LoadAsync();
    }

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is BarcodeItem item)
            await ShowBarcodePage.ShowAsync(item);
    }

    private async void OnMenuClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: BarcodeItem item }) return;

        var actions = new List<string> { ActionShow };
        var isLink = Links.TryGetOpenable(item.Value, out var uri);
        var openLabel = isLink ? Links.OpenLabel(uri) : string.Empty;
        if (isLink) actions.Add(openLabel);
        if (Links.IsWebLink(item.Value, out _)) actions.Add(ActionViewPage);
        actions.AddRange([ActionShare, ActionSaveImage, ActionBarcode, ActionQr, ActionCopy]);

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
                    ["format"] = item.IsQr ? "Code128" : item.Format,
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

            case ActionCopy:
                await CodeActions.CopyTextAsync(this, item.Value);
                break;

            case ActionDelete:
                await DeleteAsync(item);
                break;
        }
    }

    private async void OnDeleteInvoked(object? sender, EventArgs e)
    {
        if (sender is SwipeItem { CommandParameter: BarcodeItem item })
            await DeleteAsync(item);
    }

    private async Task DeleteAsync(BarcodeItem item)
    {
        var ok = await DisplayAlertAsync("Delete favorite", $"Remove \"{item.DisplayName}\"?", "Delete", "Cancel");
        if (ok)
            await _favorites.RemoveAsync(item);
    }
}
