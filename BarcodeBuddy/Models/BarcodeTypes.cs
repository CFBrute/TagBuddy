using System.Text.RegularExpressions;
using ZXing.Net.Maui;

namespace BarcodeBuddy.Models;

public record FormatInfo(string Key, string Label, BarcodeFormat ZxingFormat, bool IsLinear, string Hint);

public static class BarcodeTypes
{
    public static readonly IReadOnlyList<FormatInfo> All =
    [
        new("Code128", "Code 128", BarcodeFormat.Code128, true, "Any ASCII text (letters, digits, symbols)"),
        new("QrCode", "QR Code", BarcodeFormat.QrCode, false, "Any text: a link, Wi-Fi details, a phone number, a note"),
        new("Code39", "Code 39", BarcodeFormat.Code39, true, "Uppercase A-Z, digits, space and - . $ / + %"),
        new("Code93", "Code 93", BarcodeFormat.Code93, true, "Uppercase A-Z, digits, space and - . $ / + %"),
        new("Ean13", "EAN-13", BarcodeFormat.Ean13, true, "12 digits (check digit added) or 13 digits"),
        new("Ean8", "EAN-8", BarcodeFormat.Ean8, true, "7 digits (check digit added) or 8 digits"),
        new("UpcA", "UPC-A", BarcodeFormat.UpcA, true, "11 digits (check digit added) or 12 digits"),
        new("Itf", "ITF (Interleaved 2 of 5)", BarcodeFormat.Itf, true, "An even number of digits"),
        new("Codabar", "Codabar", BarcodeFormat.Codabar, true, "Digits and - $ : / . +"),
    ];

    public static FormatInfo Get(string key) =>
        All.FirstOrDefault(f => f.Key == key) ?? All[0];

    public static string LabelFor(string key) => Get(key).Label;

    public static FormatInfo? FromZxing(BarcodeFormat format) =>
        All.FirstOrDefault(f => f.ZxingFormat == format);

    /// <summary>Returns null when the value is valid for the format, otherwise a message explaining the problem.</summary>
    public static string? Validate(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            return "Enter some text to generate a barcode.";

        switch (key)
        {
            case "Code128":
                if (value.Any(c => c > 127))
                    return "Code 128 only supports ASCII characters.";
                return null;

            case "Code39":
            case "Code93":
                if (!Regex.IsMatch(value, @"^[0-9A-Z \-\.\$\/\+%]+$"))
                    return "Only uppercase A-Z, digits, space and - . $ / + % are allowed.";
                return null;

            case "Ean13":
                return DigitsOfLength(value, 12, 13, "EAN-13");

            case "Ean8":
                return DigitsOfLength(value, 7, 8, "EAN-8");

            case "UpcA":
                return DigitsOfLength(value, 11, 12, "UPC-A");

            case "Itf":
                if (!value.All(char.IsAsciiDigit))
                    return "ITF only supports digits.";
                if (value.Length % 2 != 0)
                    return "ITF needs an even number of digits.";
                return null;

            case "Codabar":
                if (!Regex.IsMatch(value, @"^[A-D]?[0-9\-\$:\/\.\+]+[A-D]?$"))
                    return "Codabar supports digits and - $ : / . + (optional A-D start/stop).";
                return null;

            default:
                return null;
        }
    }

    private static string? DigitsOfLength(string value, int withoutCheck, int withCheck, string name)
    {
        if (!value.All(char.IsAsciiDigit))
            return $"{name} only supports digits.";
        if (value.Length != withoutCheck && value.Length != withCheck)
            return $"{name} needs {withoutCheck} or {withCheck} digits (you have {value.Length}).";
        if (value.Length == withCheck && !CheckDigitOk(value))
            return $"The check digit is wrong. Enter {withoutCheck} digits and it will be added for you.";
        return null;
    }

    private static bool CheckDigitOk(string digits)
    {
        int sum = 0;
        for (int i = 0; i < digits.Length - 1; i++)
        {
            int d = digits[digits.Length - 2 - i] - '0';
            sum += (i % 2 == 0) ? d * 3 : d;
        }
        int check = (10 - sum % 10) % 10;
        return check == digits[^1] - '0';
    }
}
