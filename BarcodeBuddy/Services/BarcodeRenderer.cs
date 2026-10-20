using BarcodeBuddy.Models;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace BarcodeBuddy.Services;

/// <summary>Turns text into a module matrix (one row for linear codes, a square for QR) using the ZXing encoder.</summary>
public static class BarcodeRenderer
{
    /// <summary>Quiet zone (in modules) added on each side of a linear barcode.</summary>
    private const int LinearMargin = 6;

    public static BitMatrix? TryEncode(FormatInfo format, string value, QrOptions? qr, out string? error)
    {
        error = null;
        try
        {
            var hints = new Dictionary<EncodeHintType, object>();
            if (format.IsLinear)
            {
                hints[EncodeHintType.MARGIN] = LinearMargin;
            }
            else
            {
                var options = qr ?? new QrOptions();
                hints[EncodeHintType.MARGIN] = options.Margin;
                hints[EncodeHintType.ERROR_CORRECTION] = ParseLevel(options.ErrorCorrection);
                hints[EncodeHintType.CHARACTER_SET] = "UTF-8";
            }

            // Width/height 0 asks the encoder for the smallest matrix: one pixel per module.
            return new MultiFormatWriter().encode(value, format.CoreFormat, 0, 0, hints);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static BitMatrix? TryEncode(BarcodeItem item, out string? error) =>
        TryEncode(BarcodeTypes.Get(item.Format), item.Value, item.IsQr ? item.Qr : null, out error);

    /// <summary>QR version (1-40) and side length in modules, derived from the matrix size and margin.</summary>
    public static (int Version, int Modules) QrInfo(BitMatrix matrix, int margin)
    {
        int modules = matrix.Width - 2 * margin;
        int version = (modules - 21) / 4 + 1;
        return (version, modules);
    }

    private static ErrorCorrectionLevel ParseLevel(string? level) => level switch
    {
        "L" => ErrorCorrectionLevel.L,
        "Q" => ErrorCorrectionLevel.Q,
        "H" => ErrorCorrectionLevel.H,
        _ => ErrorCorrectionLevel.M,
    };
}
