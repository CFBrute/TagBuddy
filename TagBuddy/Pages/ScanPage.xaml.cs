using System.Text;
using TagBuddy.Controls;
using TagBuddy.Models;
using TagBuddy.Services;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace TagBuddy.Pages;

public partial class ScanPage : ContentPage
{
    /// <summary>Centre-first decoding: the app decodes the camera frames itself, the centre square first and the
    /// whole picture every fourth frame (see <see cref="FrameDecoder"/>), instead of letting the library decode
    /// every whole frame. It could not be tried on a phone when it was written, so it falls back to the library's
    /// own detection by itself when the frames turn out to be unusable or never arrive.
    /// To turn it off for good, set this to false: the camera then uses the library's detection only.</summary>
    public static bool CentreFirstDecoding = true;

    /// <summary>How long to wait for the first decoded frame before deciding that the frames never arrive.</summary>
    private static readonly TimeSpan DecoderWatchdog = TimeSpan.FromSeconds(4);

    /// <summary>Set once the own decoder had to give up, so later scans in this session start with the library's
    /// detection straight away instead of waiting for the watchdog again.</summary>
    private static bool _ownDecoderUnavailable;

    private readonly SavedService _saved;
    private CameraBarcodeReaderView? _camera;
    private ScanResult? _last;
    private bool _cameraAllowed;

    /// <summary>A linear barcode must be read identically on two frames in a row before it counts. Linear
    /// decoders have no real checksum against noise and happily "find" a UPC code inside a dense QR code or a
    /// textured surface; a genuine barcode repeats, a hallucination does not.</summary>
    private string? _candidate;
    private int _candidateHits;

    private const string DefaultHint = "Hold the code inside the square";
    private const string FirstHint = "Nothing found yet. Move closer, hold steady, or turn on the torch.";
    private const string SecondHint = "Still nothing. Tap the square to focus, or use From image.";

    /// <summary>The overlay, a timer for the hints and for fading the "seen" state, and when this scan started.</summary>
    private readonly ViewfinderDrawable _finder = new();
    private IDispatcherTimer? _hintTimer;
    private DateTime _scanStarted;
    private DateTime _seenAt;

    /// <summary>Set between a successful read and the result being shown, so the green flash plays once.</summary>
    private bool _resultPending;

    /// <summary>The app's own decoder while centre-first decoding is in use; null with the library's detection.</summary>
    private FrameDecoder? _decoder;

    public ScanPage(SavedService favorites)
    {
        InitializeComponent();
        _saved = favorites;
        Viewfinder.Drawable = _finder;
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
                // TryHarder costs several times the decode time per frame, and live frames keep coming: a frame
                // that does not decode fast is better replaced by the next one. Pictures picked with "From image"
                // still use it (GrayDecoder).
                TryHarder = false,
                // The library waits 150 ms between analysed frames (and 300 ms before the first) by default; without
                // TryHarder a frame decodes well within that, so look at more of them.
                DelayBetweenAnalyzingFrames = 50,
                InitialDelayBeforeAnalyzingFrames = 150,
                CameraResolutionSelector = ChooseResolution,
            },
            IsDetecting = !UseOwnDecoder,
        };
        _candidate = null;
        _candidateHits = 0;
        _resultPending = false;
        _camera.BarcodesDetected += OnBarcodesDetected;
        if (UseOwnDecoder) StartOwnDecoder(_camera);
        CameraHost.Content = _camera;

        ShowCameraLayout(true);
        StartHints();
    }

    private static bool UseOwnDecoder => CentreFirstDecoding && !_ownDecoderUnavailable;

    /// <summary>Feeds the camera frames to the app's own decoder. Its events arrive on the camera thread and are
    /// passed to the same handlers as the library's. Any problem switches to the library's detection.</summary>
    private void StartOwnDecoder(CameraBarcodeReaderView camera)
    {
        try
        {
            var decoder = new FrameDecoder();
            decoder.Decoded += result => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (ReferenceEquals(decoder, _decoder)) OnCandidate(result);
            });
            decoder.Seen += () => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (ReferenceEquals(decoder, _decoder)) ShowSeen();
            });
            decoder.Unusable += () => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (ReferenceEquals(decoder, _decoder)) UseLibraryDetection();
            });
            _decoder = decoder;
            camera.FrameReady += decoder.OnFrame;
        }
        catch
        {
            _decoder = null;
            camera.IsDetecting = true;
        }
    }

    /// <summary>Stops the app's own decoder (if any) and lets the library decode the frames, as without
    /// centre-first decoding.</summary>
    private void UseLibraryDetection()
    {
        var decoder = _decoder;
        _decoder = null;
        if (decoder is null) return;
        decoder.Stop();
        _ownDecoderUnavailable = true;
        if (_camera is null) return;
        try { _camera.FrameReady -= decoder.OnFrame; } catch { /* the view is going away */ }
        System.Diagnostics.Debug.WriteLine("ScanPage: centre-first decoding unavailable, using the library's detection");
        if (_last is null) _camera.IsDetecting = true;
    }

    /// <summary>Resets the overlay and starts the timer that changes the hint when nothing is found for a while
    /// and lets an amber "seen" state fade back to white.</summary>
    private void StartHints()
    {
        _scanStarted = DateTime.UtcNow;
        _finder.State = ViewfinderState.Idle;
        _finder.Pulse = 0;
        HintLabel.Text = DefaultHint;
        Viewfinder.Invalidate();

        _hintTimer ??= CreateHintTimer();
        _hintTimer.Start();
    }

    private IDispatcherTimer CreateHintTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) => OnHintTick();
        return timer;
    }

    private void OnHintTick()
    {
        if (_camera is null) return;
        var now = DateTime.UtcNow;

        // No decoded frame after a few seconds: the frames do not reach the app's decoder on this phone.
        if (_decoder is not null && _decoder.FramesDecoded == 0 && now - _scanStarted > DecoderWatchdog)
            UseLibraryDetection();

        if (_finder.State == ViewfinderState.Seen && now - _seenAt > TimeSpan.FromMilliseconds(700))
        {
            _finder.State = ViewfinderState.Idle;
            Viewfinder.Invalidate();
        }

        var elapsed = now - _scanStarted;
        var hint = elapsed > TimeSpan.FromSeconds(12) ? SecondHint
                 : elapsed > TimeSpan.FromSeconds(6) ? FirstHint
                 : DefaultHint;
        if (HintLabel.Text != hint) HintLabel.Text = hint;
    }

    /// <summary>Something code-like is in view but not read yet: tell the person to hold still.</summary>
    private void ShowSeen()
    {
        if (_camera is null || _resultPending) return;
        _seenAt = DateTime.UtcNow;
        if (_finder.State == ViewfinderState.Seen) return;
        _finder.State = ViewfinderState.Seen;
        Viewfinder.Invalidate();
    }

    private void OnViewfinderTapped(object? sender, TappedEventArgs e)
    {
        // The library focuses on the centre of the picture, which is where the square is.
        try { _camera?.AutoFocus(); } catch { /* not every camera can focus */ }
    }

    /// <summary>Removes the camera view, which disconnects its handler and turns the camera off.</summary>
    private void StopCamera()
    {
        _hintTimer?.Stop();
        if (_camera is null) return;
        _camera.IsDetecting = false;
        _camera.IsTorchOn = false;
        _camera.BarcodesDetected -= OnBarcodesDetected;
        // Before the view goes: a late frame must not reach a decoder whose page has moved on.
        if (_decoder is { } decoder)
        {
            decoder.Stop();
            try { _camera.FrameReady -= decoder.OnFrame; } catch { /* the view is going away */ }
            _decoder = null;
        }
        CameraHost.Content = null;
        _camera.Handler?.DisconnectHandler();
        _camera = null;
    }

    /// <summary>Three layouts: live camera on top; a chosen picture on top (large while it is being analysed,
    /// a strip once there is a result); or results only.</summary>
    private void ShowCameraLayout(bool cameraOn, bool picture = false, bool pictureLarge = false)
    {
        CameraArea.IsVisible = cameraOn || picture;
        CameraHost.IsVisible = cameraOn;
        CameraButtons.IsVisible = cameraOn;
        Viewfinder.IsVisible = cameraOn;
        HintBorder.IsVisible = cameraOn;
        PickedImage.IsVisible = picture;

        Root.RowDefinitions[0].Height = cameraOn || pictureLarge ? GridLength.Star : picture ? new GridLength(240) : new GridLength(0);
        Root.RowDefinitions[1].Height = cameraOn || pictureLarge ? GridLength.Auto : GridLength.Star;
        ResultScroll.MaximumHeightRequest = cameraOn || pictureLarge ? 160 : double.PositiveInfinity;
        ResultTopBar.IsVisible = !cameraOn;
    }

    /// <summary>Picks the analysis frame size: the largest the camera offers up to about full HD. The default
    /// is small and cannot resolve dense QR codes (login codes, long links); larger than this only costs time.</summary>
    private static CameraResolution? ChooseResolution(IReadOnlyList<CameraResolution> available)
    {
        if (available.Count == 0) return null;
        var fits = available.Where(r => Math.Max(r.Width, r.Height) <= 1920).ToList();
        var pool = fits.Count > 0 ? fits : available.ToList();
        return pool.OrderByDescending(r => (long)r.Width * r.Height).First();
    }

    private static bool IsTwoDimensional(BarcodeFormat format) =>
        format is BarcodeFormat.QrCode or BarcodeFormat.DataMatrix or BarcodeFormat.Aztec or BarcodeFormat.Pdf417;

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var result = e.Results.FirstOrDefault();
        if (result is null) return;
        var scanned = ScanResult.From(result);
        MainThread.BeginInvokeOnMainThread(() => OnCandidate(scanned));
    }

    /// <summary>A decoded code from the camera, on the main thread. Linear codes need a second identical read
    /// first; the first one shows as "seen".</summary>
    private async void OnCandidate(ScanResult result)
    {
        if (_last is not null || _resultPending || _camera is null) return; // already handled a result for this scan

        if (!IsTwoDimensional(result.Format))
        {
            var key = $"{result.Format}:{result.Value}";
            _candidateHits = key == _candidate ? _candidateHits + 1 : 1;
            _candidate = key;
            if (_candidateHits < 2)
            {
                ShowSeen();
                return;
            }
        }

        // Flash the corners green, and show the result a moment later so the flash is visible before the camera
        // view is removed.
        _resultPending = true;
        _finder.State = ViewfinderState.Read;
        new Animation(v => { _finder.Pulse = (float)v; Viewfinder.Invalidate(); }, 0, 1)
            .Commit(this, "ViewfinderPulse", length: 250, easing: Easing.CubicOut);
        try { Vibration.Default.Vibrate(80); } catch { /* not supported on every device */ }

        await Task.Delay(150);
        _resultPending = false;
        if (_last is not null) return;   // a picture was read meanwhile
        ShowResult(result, "scanned with the camera");
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

        byte[] bytes;
        using (var stream = await file.OpenReadAsync())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        // Show the picture itself while it is analysed; the camera is released meanwhile.
        _last = null;
        StopCamera();
        PickedImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
        ShowCameraLayout(cameraOn: false, picture: true, pictureLarge: true);
        ResultLabel.Text = string.Empty;
        ResultButtons.IsVisible = false;
        Details.IsVisible = false;
        StatusLabel.Text = "Looking for a code in the picture...";
        AnalysingOverlay.IsVisible = true;
        AnalysingSpinner.IsRunning = true;

        ScanResult? result;
        try
        {
            result = await Task.Run(() => ImageDecoder.Decode(bytes));
        }
        finally
        {
            AnalysingSpinner.IsRunning = false;
            AnalysingOverlay.IsVisible = false;
        }

        if (result is null)
        {
            StatusLabel.Text = "No barcode or QR code found in that picture. Try a sharper or closer picture, or scan again.";
            return;
        }

        ShowResult(result, "read from a picture", keepPicture: true);
    }

    private void ShowResult(ScanResult result, string source, bool keepPicture = false)
    {
        _last = result;
        StopCamera();
        ShowCameraLayout(cameraOn: false, picture: keepPicture);

        StatusLabel.Text = $"{result.FormatLabel}, {source}";
        ResultLabel.Text = result.Value;
        ShowDetails(result);
        OpenLinkButton.IsVisible = Links.TryGetOpenable(result.Value, out var uri);
        if (OpenLinkButton.IsVisible) IconButtons.SetLabel(OpenLinkButton, Links.OpenLabel(uri));
        ViewPageButton.IsVisible = Links.IsWebLink(result.Value, out _);
        Preview.Value = result.Value;
        ResultButtons.IsVisible = true;
    }

    private async void OnViewPage(object? sender, EventArgs e)
    {
        if (_last is not null) await WebPage.ShowAsync(_last.Value);
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
        PickedImage.Source = null;
        StatusLabel.Text = "Point the camera at a barcode or QR code";
        ResultLabel.Text = string.Empty;
        Details.IsVisible = false;
        Details.Children.Clear();
        ResultButtons.IsVisible = false;
        Preview.Value = null;
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
            // The app cannot draw this format, but the content is still worth keeping: save it as text.
            if (_saved.ContainsText(_last.Value))
            {
                await DisplayAlertAsync("Already saved", "This content is already saved as text.", "OK");
                return;
            }
            var textName = await DisplayPromptAsync("Save as text",
                $"{_last.FormatLabel} cannot be generated by this app, so the content is kept as text. Give it a name (optional):",
                accept: "Save", cancel: "Cancel", placeholder: _last.Value, maxLength: 60);
            if (textName is null) return;
            await _saved.AddTextAsync(textName, _last.Value);
            Notice.Show("Saved as text");
            return;
        }
        if (_saved.ContainsCode(_last.Value, format.Key))
        {
            await DisplayAlertAsync("Already saved", "This code is already saved.", "OK");
            return;
        }

        var name = await DisplayPromptAsync("Save", "Give it a name (optional):",
            accept: "Save", cancel: "Cancel", placeholder: _last.Value, maxLength: 60);
        if (name is null) return;

        var item = new SavedItem { Name = name.Trim(), Value = _last.Value, Format = format.Key };
        if (item.IsQr && _last.ErrorCorrectionLevel is { Length: > 0 } level)
            item.QrErrorCorrection = level;

        await _saved.AddAsync(item);
        Notice.Show("Saved");
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
