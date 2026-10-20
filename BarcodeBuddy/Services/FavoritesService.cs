using System.Collections.ObjectModel;
using System.Text.Json;
using BarcodeBuddy.Models;

namespace BarcodeBuddy.Services;

public class FavoritesService
{
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "favorites.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private bool _loaded;

    public ObservableCollection<BarcodeItem> Items { get; } = [];

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            await using var stream = File.OpenRead(_path);
            var items = await JsonSerializer.DeserializeAsync<List<BarcodeItem>>(stream) ?? [];
            foreach (var item in items.OrderByDescending(i => i.CreatedAt))
                Items.Add(item);
        }
        catch
        {
            // A corrupt file should not prevent the app from starting.
        }
    }

    public async Task AddAsync(BarcodeItem item)
    {
        await LoadAsync();
        Items.Insert(0, item);
        await SaveAsync();
    }

    public async Task RemoveAsync(BarcodeItem item)
    {
        await LoadAsync();
        Items.Remove(item);
        await SaveAsync();
    }

    public async Task SaveAsync()
    {
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, Items.ToList(), JsonOptions);
    }

    public bool Contains(string value, string format) =>
        Items.Any(i => i.Value == value && i.Format == format);
}
