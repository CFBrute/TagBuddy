#if ANDROID
using System.Runtime.InteropServices;
using ZXing;
using ZXing.Net.Maui;

namespace TagBuddy.Services;

/// <summary>Centre-first decoding of live camera frames with the ZXing core, used instead of the library's own
/// detection when <see cref="Pages.ScanPage.CentreFirstDecoding"/> is on.
///
/// Most frames are decoded only in a centre square (a little larger than the square drawn on screen), which is
/// a fraction of the pixels and where the person holds the code. Every fourth frame looks at the whole picture
/// at half size, so a code outside the square still reads. After five seconds without a result the centre
/// passes try harder.
///
/// This could not be tried on a phone when it was written, so it is defensive: anything unexpected (a frame
/// that is not a plain luminance plane, frames on the main thread, repeated exceptions) raises
/// <see cref="Unusable"/>, and the scan page then switches back to the library's own detection.</summary>
public sealed class FrameDecoder
{
    /// <summary>A code was read. Raised on the camera thread.</summary>
    public event Action<ScanResult>? Decoded;

    /// <summary>Finder patterns or barcode ends were found but nothing decoded yet. Raised on the camera thread,
    /// at most every 250 ms.</summary>
    public event Action? Seen;

    /// <summary>The frames cannot be used here; fall back to the library's detection. Raised once.</summary>
    public event Action? Unusable;

    /// <summary>Frames that arrived and could be read, for the scan page's watchdog.</summary>
    public int FramesDecoded => _framesDecoded;

    private const double CropFraction = 0.70;
    private const int MaxBadFrames = 15;
    private static readonly TimeSpan HarderAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SeenThrottle = TimeSpan.FromMilliseconds(250);

    /// <summary>The same formats the scan page asks the library for.</summary>
    private static readonly List<ZXing.BarcodeFormat> Formats =
    [
        ZXing.BarcodeFormat.CODE_128, ZXing.BarcodeFormat.CODE_39, ZXing.BarcodeFormat.CODE_93,
        ZXing.BarcodeFormat.EAN_13, ZXing.BarcodeFormat.EAN_8, ZXing.BarcodeFormat.UPC_A, ZXing.BarcodeFormat.UPC_E,
        ZXing.BarcodeFormat.ITF, ZXing.BarcodeFormat.CODABAR, ZXing.BarcodeFormat.QR_CODE,
        ZXing.BarcodeFormat.DATA_MATRIX, ZXing.BarcodeFormat.AZTEC, ZXing.BarcodeFormat.PDF_417,
    ];

    // One reader per option set: changing a reader's options makes it rebuild its decoders.
    private readonly BarcodeReaderGeneric _fast;
    private readonly BarcodeReaderGeneric _inverted;
    private readonly BarcodeReaderGeneric _harder;
    private readonly BarcodeReaderGeneric _harderInverted;

    private readonly DateTime _started = DateTime.UtcNow;
    private int _busy;
    private long _frame;
    private int _points;
    private int _badFrames;
    private int _framesDecoded;
    private bool _stopped;
    private bool _gaveUp;
    private DateTime _lastSeen;
    private byte[] _buffer = [];

    public FrameDecoder()
    {
        _fast = CreateReader(tryHarder: false, tryInverted: false);
        _inverted = CreateReader(tryHarder: false, tryInverted: true);
        _harder = CreateReader(tryHarder: true, tryInverted: false);
        _harderInverted = CreateReader(tryHarder: true, tryInverted: true);
    }

    private BarcodeReaderGeneric CreateReader(bool tryHarder, bool tryInverted)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            // ReturnCodabarStartEnd: keep a Codabar's A-D start and stop letters (see GrayDecoder).
            Options = { TryHarder = tryHarder, TryInverted = tryInverted, PossibleFormats = Formats, ReturnCodabarStartEnd = true },
        };
        reader.ResultPointFound += _ => _points++;
        return reader;
    }

    /// <summary>No more events after this, even from a frame that is being decoded right now.</summary>
    public void Stop() => _stopped = true;

    /// <summary>Handler for the camera view's FrameReady event. Runs on the camera thread; the frame's buffer is
    /// only valid during the call, so it is copied and decoded synchronously. While one frame is decoded the
    /// next ones are dropped, and the camera keeps only the latest frame meanwhile.</summary>
    public void OnFrame(object? sender, CameraFrameBufferEventArgs e)
    {
        if (_stopped || _gaveUp) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            if (MainThread.IsMainThread)
            {
                // Decoding here would stall the screen. The library handles this case itself.
                GiveUp();
                return;
            }

            var holder = e?.Data;
            var buffer = holder?.Data;
            int width = (int)(holder?.Size.Width ?? 0), height = (int)(holder?.Size.Height ?? 0);
            if (buffer is null || width < 32 || height < 32)
            {
                BadFrame();
                return;
            }

            // ZXing.Net.Maui asks CameraX for RGBA_8888 frames (four bytes per pixel, already rotated to the
            // screen's orientation) and converts them to grey itself. A plain luminance plane is handled too,
            // in case a later version of the library switches to YUV.
            int capacity = buffer.Capacity();
            int bytesPerPixel = capacity >= (long)width * height * 4 ? 4 : 1;
            int stride = bytesPerPixel == 4
                ? (capacity % height == 0 && capacity / height >= width * 4 ? capacity / height : 0)
                : RowStride(capacity, width, height);
            if (stride == 0)
            {
                // Not a frame layout this code understands.
                BadFrame();
                return;
            }

            long n = _frame++;
            Result? result;
            bool harder = DateTime.UtcNow - _started > HarderAfter;
            var normal = harder ? _harder : _fast;
            var inverted = harder ? _harderInverted : _inverted;

            // A six-frame cycle over the centre square, with one look at the whole picture. Measured on photos
            // of a dense QR code shown on a screen and a laser-etched Data Matrix: the half-size, lightly
            // blurred pass reads both in a few milliseconds, where the raw frame reads neither (pixel-grid
            // moire and noise), and both were light-on-dark, so the inverted reader runs on every other pass.
            // The raw passes stay for small codes that need every pixel.
            switch (n % 6)
            {
                case 3:
                    // The whole picture at half size: codes outside the square still read.
                    Copy(buffer, stride, bytesPerPixel, 0, 0, width, height);
                    var (half, hw, hh) = GrayDecoder.Downscale2x(_buffer, width, height);
                    result = Decode((n / 6) % 2 == 0 ? normal : inverted, GrayDecoder.BoxBlur(half, hw, hh, 1), hw, hh);
                    break;

                default:
                    int side = (int)Math.Round(CropFraction * Math.Min(width, height));
                    int left = (width - side) / 2, top = (height - side) / 2;
                    Copy(buffer, stride, bytesPerPixel, left, top, side, side);
                    result = (n % 6) switch
                    {
                        0 => DecodeHalfBlurred(normal, side),
                        1 => DecodeHalfBlurred(inverted, side),
                        2 => Decode(normal, _buffer, side, side),
                        4 => Decode(inverted, _buffer, side, side),
                        _ => Decode(normal, GrayDecoder.BoxBlur(_buffer, side, side, 2), side, side),
                    };
                    break;
            }

            _badFrames = 0;
            Interlocked.Increment(ref _framesDecoded);
            if (_stopped) return;

            if (result is not null)
            {
                Decoded?.Invoke(ScanResult.From(result));
            }
            else if (_points >= 3 && DateTime.UtcNow - _lastSeen > SeenThrottle)
            {
                _lastSeen = DateTime.UtcNow;
                Seen?.Invoke();
            }
        }
        catch
        {
            BadFrame();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Bytes per row of a luminance plane, worked out from the buffer size, or 0 when the size does not
    /// fit one. Rows can be padded (a 1440 px row stored in 1536 bytes), and the last row may or may not be.
    /// A whole NV21 or I420 frame (1.5 bytes per pixel) starts with an unpadded luminance plane. Only used for
    /// buffers smaller than four bytes per pixel; RGBA frames are recognised before this is called.</summary>
    internal static int RowStride(int capacity, int width, int height)
    {
        const int MaxPadding = 256;
        long pixels = (long)width * height;
        if (capacity == pixels) return width;
        if (capacity < pixels) return 0;
        if (capacity % height == 0 && capacity / height - width is >= 0 and <= MaxPadding) return capacity / height;
        if ((capacity - width) % (height - 1) == 0 && (capacity - width) / (height - 1) - width is >= 0 and <= MaxPadding)
            return (capacity - width) / (height - 1);
        if (capacity >= pixels * 3 / 2) return width;
        return 0;
    }

    /// <summary>The centre square at half size with a light blur: a dense code photographed off a screen is
    /// mostly moire at full size, and halving it averages the screen's pixel grid away.</summary>
    private Result? DecodeHalfBlurred(BarcodeReaderGeneric reader, int side)
    {
        var (half, hw, hh) = GrayDecoder.Downscale2x(_buffer, side, side);
        return Decode(reader, GrayDecoder.BoxBlur(half, hw, hh, 1), hw, hh);
    }

    private Result? Decode(BarcodeReaderGeneric reader, byte[] luminance, int width, int height)
    {
        _points = 0;
        var source = new PlanarYUVLuminanceSource(luminance, width, height, 0, 0, width, height, false);
        return reader.Decode(source);
    }

    private byte[] _row = [];

    /// <summary>Copies a rectangle of the frame into the reused managed luminance buffer, row by row (rows may
    /// be padded to <paramref name="stride"/> bytes). RGBA pixels are turned into grey with the same weights
    /// the library uses, so the decoder sees exactly what it would see through the library.</summary>
    private void Copy(Java.Nio.ByteBuffer buffer, int stride, int bytesPerPixel, int left, int top, int width, int height)
    {
        int length = width * height;
        if (_buffer.Length != length) _buffer = new byte[length];

        IntPtr address = buffer.IsDirect ? buffer.GetDirectBufferAddress() : IntPtr.Zero;
        // Not a direct buffer: read through a duplicate, so the library's buffer keeps its position.
        using var view = address == IntPtr.Zero ? buffer.Duplicate() : null;

        if (bytesPerPixel == 1)
        {
            for (int y = 0; y < height; y++)
            {
                int offset = (top + y) * stride + left;
                if (view is null) Marshal.Copy(address + offset, _buffer, y * width, width);
                else { view.Position(offset); view.Get(_buffer, y * width, width); }
            }
            return;
        }

        int rowBytes = width * 4;
        if (_row.Length < rowBytes) _row = new byte[rowBytes];
        for (int y = 0; y < height; y++)
        {
            int offset = (top + y) * stride + left * 4;
            if (view is null) Marshal.Copy(address + offset, _row, 0, rowBytes);
            else { view.Position(offset); view.Get(_row, 0, rowBytes); }

            int o = y * width;
            for (int x = 0, i = 0; x < width; x++, i += 4)
                _buffer[o + x] = (byte)(((66 * _row[i] + 129 * _row[i + 1] + 25 * _row[i + 2]) >> 8) + 16);
        }
    }

    private void BadFrame()
    {
        if (++_badFrames >= MaxBadFrames) GiveUp();
    }

    private void GiveUp()
    {
        if (_gaveUp) return;
        _gaveUp = true;
        if (!_stopped) Unusable?.Invoke();
    }
}
#endif
