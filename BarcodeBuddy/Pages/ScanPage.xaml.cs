using System.Text;
using BarcodeBuddy.Models;
using BarcodeBuddy.Services;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace BarcodeBuddy.Pages;

public partial class ScanPage : ContentPage
{
    private readonly FavoritesService _favorites;
    private CameraBarcodeReaderView? _camera;
    private ScanResult? _last;
    private bool _cameraAllowed;

    public ScanPage(FavoritesService favorites)
    {
        InitializeComponent();
        _favorites = favorites;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();

        _cameraAllowed = status == PermissionStatus.Granted;
        PermissionLabel.IsVisible = !_cameraAllowed;
        TorchButton.IsVisible = _cameraAllowed;

        if (_last is null) StartCamera();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopCamera();
    }

    /// <summary>Creates the camera view and starts detecting. The view is created fresh each time so the camera is
    /// fully released in between.</summary>
    private void StartCamera()
    {
        if (!_cameraAllowed || _camera is not null) return;

        _camera = new CameraBarcodeReaderView
        {
            Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormat.Code128 | BarcodeFormat.Code39 | BarcodeFormat.Code93 |
                          BarcodeFormat.Ean13 | BarcodeFormat.Ean8 | BarcodeFormat.UpcA | BarcodeFormat.UpcE |
                          BarcodeFormat.Itf | BarcodeFormat.Codabar | BarcodeFormat.QrCode |
                          BarcodeFormat.DataMatrix | BarcodeFormat.Aztec | BarcodeFormat.Pdf417,
                AutoRotate = true,
                Multiple = false,
                TryHarder = true,
            },
            IsDetecting = true,
        };
        _camera.BarcodesDetected += OnBarcodesDetected;
        CameraHost.Content = _camera;

        ShowCameraLayout(true);
    }

    /// <summary>Removes the camera view, which disconnects its handler and turns the camera off.</summary>
    private void StopCamera()
    {
        if (_camera is null) return;
        _camera.IsDetecting = false;
        _camera.IsTorchOn = false;
        _camera.BarcodesDetected -= OnBarcodesDetected;
        CameraHost.Content = null;
        _camera.Handler?.DisconnectHandler();
        _camera = null;
    }

    private void ShowCameraLayout(bool cameraOn)
    {
        CameraArea.IsVisible = cameraOn;
        Root.RowDefinitions[0].Height = cameraOn ? GridLength.Star : new GridLength(0);
        Root.RowDefinitions[1].Height = cameraOn ? GridLength.Auto : GridLength.Star;
        ResultScroll.MaximumHeightRequest = cameraOn ? 160 : double.PositiveInfinity;
        ResultTopBar.IsVisible = !cameraOn;
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var result = e.Results.FirstOrDefault();
        if (result is null) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_last is not null) return; // already handled a result for this scan
            ShowResult(ScanResult.From(result), "scanned with the camera");
            try { Vibration.Default.Vibrate(80); } catch { /* not supported on every device */ }
        });
    }

    private async void OnPickImage(object? sender, EventArgs e)
    {
        FileResult? file;
        try
        {
            file = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions { Title = "Pick a picture with a code" });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not open the picker", ex.Message, "OK");
            return;
        }
        if (file is null) return;

        StatusLabel.Text = "Looking for a code in the picture...";

        byte[] bytes;
        using (var stream = await file.OpenReadAsync())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        var result = await Task.Run(() => ImageDecoder.Decode(bytes));
        if (result is null)
        {
            StatusLabel.Text = "No barcode or QR code found in that picture";
            return;
        }

        ShowResult(result, "read from a picture");
    }

    private void ShowResult(ScanResult result, string source)
    {
        _last = result;
        StopCamera();
        ShowCameraLayout(false);

        StatusLabel.Text = $"{result.FormatLabel}, {source}";
        ResultLabel.Text = result.Value;
        ShowDetails(result);
        OpenLinkButton.IsVisible = Links.TryGetOpenable(result.Value, out var uri);
        if (OpenLinkButton.IsVisible) IconButtons.SetLabel(OpenLinkButton, Links.OpenLabel(uri));
        ResultButtons.IsVisible = true;
    }

    private void ShowDetails(ScanResult result)
    {
        Details.Children.Clear();

        AddDetail("Format", result.FormatLabel);
        AddDetail("Content", ScanDetails.DescribeContent(result.Value));
        AddDetail("Length", $"{result.Value.Length} characters, {Encoding.UTF8.GetByteCount(result.Value)} bytes");

        foreach (var (label, value) in ScanDetails.DescribeMetadata(result))
            AddDetail(label, value);

        foreach (var (label, value) in ScanDetails.DescribeStructuredContent(result.Value))
            AddDetail(label, value);

        Details.IsVisible = Details.Children.Count > 0;
    }

    private void AddDetail(string label, string value)
    {
        var row = new Grid { ColumnDefinitions = [new ColumnDefinition(110), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 8 };
        row.Add(new Label { Text = label, FontSize = 13, TextColor = Colors.Gray });
        var v = new Label { Text = value, FontSize = 13, LineBreakMode = LineBreakMode.WordWrap };
        Grid.SetColumn(v, 1);
        row.Add(v);
        Details.Children.Add(row);
    }

    private void OnScanAgain(object? sender, EventArgs e)
    {
        _last = null;
        StatusLabel.Text = "Point the camera at a barcode or QR code";
        ResultLabel.Text = string.Empty;
        Details.IsVisible = false;
        Details.Children.Clear();
        ResultButtons.IsVisible = false;
        StartCamera();
    }

    private void OnTorch(object? sender, EventArgs e)
    {
        if (_camera is not null) _camera.IsTorchOn = !_camera.IsTorchOn;
    }

    private async void OnUseInGenerator(object? sender, EventArgs e)
    {
        if (_last is null) return;
        var format = _last.KnownFormat;

        if (format is null || !format.IsLinear)
        {
            // QR, and 2D formats this app cannot generate, go to the QR tab.
            await Shell.Current.GoToAsync("//generate/qr", new Dictionary<string, object>
            {
                ["value"] = _last.Value,
                ["ecc"] = _last.ErrorCorrectionLevel ?? QrOptions.DefaultErrorCorrection,
            });
            return;
        }

        await Shell.Current.GoToAsync("//generate/barcode", new Dictionary<string, object>
        {
            ["value"] = _last.Value,
            ["format"] = format.Key,
        });
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        if (_last is null) return;
        var format = _last.KnownFormat;
        if (format is null)
        {
            await DisplayAlertAsync("Not supported", $"{_last.FormatLabel} can be scanned but not generated by this app.", "OK");
            return;
        }
        if (_favorites.Contains(_last.Value, format.Key))
        {
            await DisplayAlertAsync("Already saved", "This code is already in your favorites.", "OK");
            return;
        }

        var name = await DisplayPromptAsync("Add to favorites", "Give it a name (optional):",
            accept: "Save", cancel: "Cancel", placeholder: _last.Value, maxLength: 60);
        if (name is null) return;

        var item = new BarcodeItem { Name = name.Trim(), Value = _last.Value, Format = format.Key };
        if (item.IsQr && _last.ErrorCorrectionLevel is { Length: > 0 } level)
            item.QrErrorCorrection = level;

        await _favorites.AddAsync(item);
        await DisplayAlertAsync("Saved to favorites", null, "OK");
    }

    private async void OnCopy(object? sender, EventArgs e)
    {
        if (_last is not null) await CodeActions.CopyTextAsync(this, _last.Value);
    }

    private async void OnOpenLink(object? sender, EventArgs e)
    {
        if (_last is not null) await CodeActions.OpenLinkAsync(this, _last.Value);
    }
}
