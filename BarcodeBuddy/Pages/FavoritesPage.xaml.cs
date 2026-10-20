using BarcodeBuddy.Models;
using BarcodeBuddy.Services;

namespace BarcodeBuddy.Pages;

public partial class FavoritesPage : ContentPage
{
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

    private async void OnDeleteInvoked(object? sender, EventArgs e)
    {
        if (sender is not SwipeItem { CommandParameter: BarcodeItem item }) return;

        var ok = await DisplayAlertAsync("Delete favorite", $"Remove \"{item.DisplayName}\"?", "Delete", "Cancel");
        if (ok)
            await _favorites.RemoveAsync(item);
    }
}
