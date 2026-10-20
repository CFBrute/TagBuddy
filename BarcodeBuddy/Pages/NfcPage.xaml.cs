using System.Text;
using BarcodeBuddy.Models;
using BarcodeBuddy.Services;

namespace BarcodeBuddy.Pages;

public partial class NfcPage : ContentPage
{
    private readonly FavoritesService _favorites;
    private readonly NfcReader _nfc = NfcReader.Instance;
    private NfcTagInfo? _last;

    public NfcPage(FavoritesService favorites)
    {
        InitializeComponent();
        _favorites = favorites;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (!_nfc.IsSupported)
        {
            StatusLabel.Text = "This phone has no NFC reader";
            HintLabel.IsVisible = false;
            return;
        }
        if (!_nfc.IsEnabled)
        {
            StatusLabel.Text = "NFC is turned off";
            SettingsButton.IsVisible = true;
            return;
        }

        StatusLabel.Text = "Hold a tag or card against the back of the phone";
        SettingsButton.IsVisible = false;
        _nfc.TagRead += OnTagRead;
        _nfc.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _nfc.TagRead -= OnTagRead;
        _nfc.Stop();
    }

    private void OnOpenSettings(object? sender, EventArgs e) => _nfc.OpenSettings();

    private void OnTagRead(NfcTagInfo tag)
    {
        _last = tag;
        try { Vibration.Default.Vibrate(60); } catch { /* not supported on every device */ }

        WaitingPanel.IsVisible = false;
        ResultPanel.IsVisible = true;

        TagTitle.Text = $"Tag {tag.Id}  ·  {string.Join(", ", tag.Technologies)}";
        var card = tag.Details.FirstOrDefault(d => d.Label == "Card").Value;
        ContentLabel.Text = tag.Content ?? card ?? "No readable content on this tag";
        ContentLabel.TextColor = tag.HasContent ? (Color)Application.Current!.Resources["Primary"] : Colors.Gray;

        ContentActions.IsVisible = tag.HasContent;
        if (tag.HasContent)
        {
            OpenLinkButton.IsVisible = Links.TryGetOpenable(tag.Content, out var uri);
            if (OpenLinkButton.IsVisible) IconButtons.SetLabel(OpenLinkButton, Links.OpenLabel(uri));
        }

        Details.Children.Clear();
        AddDetail("Serial number", tag.Id);
        foreach (var (label, value) in tag.Details)
            AddDetail(label, value);

        if (tag.Records.Count > 0)
        {
            AddDetail("Records", tag.Records.Count.ToString());
            foreach (var record in tag.Records)
                AddDetail(record.Kind, record.Text.Length == 0 ? "(empty)" : record.Text);
        }

        if (tag.HasContent)
        {
            AddDetail("Content", ScanDetails.DescribeContent(tag.Content!));
            AddDetail("Length", $"{tag.Content!.Length} characters, {Encoding.UTF8.GetByteCount(tag.Content)} bytes");
            foreach (var (label, value) in ScanDetails.DescribeStructuredContent(tag.Content))
                AddDetail(label, value);
        }
    }

    private void AddDetail(string label, string value)
    {
        var row = new Grid { ColumnDefinitions = [new ColumnDefinition(120), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 8 };
        row.Add(new Label { Text = label, FontSize = 13, TextColor = Colors.Gray });
        var v = new Label { Text = value, FontSize = 13, LineBreakMode = LineBreakMode.WordWrap };
        Grid.SetColumn(v, 1);
        row.Add(v);
        Details.Children.Add(row);
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        if (_last?.Content is not { } content) return;

        if (_favorites.Contains(content, BarcodeTypes.QrCode.Key))
        {
            await DisplayAlertAsync("Already saved", "This content is already in your favorites.", "OK");
            return;
        }

        var name = await DisplayPromptAsync("Add to favorites", "Give it a name (optional):",
            accept: "Save", cancel: "Cancel", placeholder: content, maxLength: 60);
        if (name is null) return;

        await _favorites.AddAsync(new BarcodeItem { Name = name.Trim(), Value = content, Format = BarcodeTypes.QrCode.Key });
        await DisplayAlertAsync("Saved to favorites", "Saved as a QR code with the tag's content.", "OK");
    }

    private async void OnCopy(object? sender, EventArgs e)
    {
        if (_last?.Content is { } content) await CodeActions.CopyTextAsync(this, content);
    }

    private async void OnOpenLink(object? sender, EventArgs e)
    {
        if (_last?.Content is { } content) await CodeActions.OpenLinkAsync(this, content);
    }

    private async void OnUseInGenerator(object? sender, EventArgs e)
    {
        if (_last?.Content is not { } content) return;
        await Shell.Current.GoToAsync("//generate/qr", new Dictionary<string, object> { ["value"] = content });
    }
}
