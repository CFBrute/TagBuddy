using System.Text.RegularExpressions;
using TagBuddy.Models;
using ZXing.Net.Maui;

namespace TagBuddy.Services;

/// <summary>Explains what a scanned code contains and which encoding quirks it uses.</summary>
public static class ScanDetails
{
    public static string DescribeContent(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return "empty";
        if (Regex.IsMatch(v, @"^https?://", RegexOptions.IgnoreCase)) return "Web link";
        if (v.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase)) return "Wi-Fi network";
        if (v.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)) return "Contact card (vCard)";
        if (v.StartsWith("MECARD:", StringComparison.OrdinalIgnoreCase)) return "Contact card (MeCard)";
        if (v.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase) || v.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) return "Calendar event";
        if (v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("MATMSG:", StringComparison.OrdinalIgnoreCase)) return "Email";
        if (v.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return "Phone number";
        if (v.StartsWith("sms:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("smsto:", StringComparison.OrdinalIgnoreCase)) return "SMS";
        if (v.StartsWith("geo:", StringComparison.OrdinalIgnoreCase)) return "Location";
        if (v.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)) return "One-time password secret (authenticator)";
        if (v.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase)) return "Bitcoin payment";
        if (Regex.IsMatch(v, @"^[A-Za-z][A-Za-z0-9+.-]*://")) return "App link (" + v[..v.IndexOf(':')] + ")";
        if (v.All(char.IsAsciiDigit)) return "Numeric";
        if (Regex.IsMatch(v, @"^[0-9A-Z $%*+\-./:]+$")) return "Alphanumeric (QR compact mode)";
        if (v.Any(c => c > 127)) return "Text (Unicode)";
        return "Text";
    }

    /// <summary>Metadata reported by the decoder: error correction, structured append, byte segments, and so on.</summary>
    public static IEnumerable<(string Label, string Value)> DescribeMetadata(ScanResult result)
    {
        foreach (var (key, value) in result.Metadata)
        {
            if (value is null) continue;

            switch (key)
            {
                case "errorcorrectionlevel":
                    yield return ("Error correction", QrOptions.DescribeLevel(value.ToString()));
                    break;

                case "qrversion":
                    yield return ("QR version", $"{value} ({21 + 4 * (Convert.ToInt32(value) - 1)}×{21 + 4 * (Convert.ToInt32(value) - 1)} modules)");
                    break;

                case "qrmaskpattern":
                    yield return ("Mask pattern", value.ToString()!);
                    break;

                case "errorscorrected":
                    yield return ("Errors corrected", Convert.ToInt32(value) > 0
                        ? $"{value} (the code was damaged but still readable)"
                        : "0 (read cleanly)");
                    break;

                case "structuredappendsequence":
                    if (value is int seq)
                        yield return ("Structured append", $"part {(seq >> 4) + 1} of {(seq & 0x0F) + 1} (this code is one piece of a larger message)");
                    else
                        yield return ("Structured append", value.ToString()!);
                    break;

                case "structuredappendparity":
                    yield return ("Append parity", value.ToString()!);
                    break;

                case "bytesegments":
                    if (value is IEnumerable<byte[]> segments)
                    {
                        var list = segments.ToList();
                        yield return ("Byte segments", $"{list.Count} segment(s), {list.Sum(s => s.Length)} raw bytes");
                    }
                    break;

                case "symbologyidentifier":
                    yield return ("Symbology id", $"{value} (ISO/IEC 15424 identifier)");
                    break;

                case "orientation":
                    yield return ("Orientation", $"{value}°");
                    break;

                case "possiblecountry":
                    yield return ("Country prefix", value.ToString()!);
                    break;

                case "upceanextension":
                    yield return ("EAN extension", value.ToString()!);
                    break;

                case "issuenumber":
                    yield return ("Issue number", value.ToString()!);
                    break;

                case "suggestedprice":
                    yield return ("Suggested price", value.ToString()!);
                    break;

                case "other":
                    break;

                default:
                    yield return (key, value.ToString()!);
                    break;
            }
        }
    }

    /// <summary>Breaks well-known payloads (Wi-Fi, contact cards) into readable fields.</summary>
    public static IEnumerable<(string Label, string Value)> DescribeStructuredContent(string value)
    {
        var v = value.Trim();

        if (v.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in Regex.Matches(v[5..], @"(?<k>[A-Z]):(?<v>(?:\\.|[^;])*);"))
            {
                var field = m.Groups["v"].Value.Replace("\\;", ";").Replace("\\:", ":").Replace("\\\\", "\\");
                switch (m.Groups["k"].Value)
                {
                    case "S": yield return ("Network", field); break;
                    case "T": yield return ("Security", field.Length == 0 ? "open" : field); break;
                    case "P": yield return ("Password", new string('•', Math.Min(field.Length, 12)) + $"  ({field.Length} characters)"); break;
                    case "H": yield return ("Hidden network", field.Equals("true", StringComparison.OrdinalIgnoreCase) ? "yes" : "no"); break;
                }
            }
            yield break;
        }

        if (v.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in v.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                var colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                var key = trimmed[..colon].Split(';')[0].ToUpperInvariant();
                var val = trimmed[(colon + 1)..];
                switch (key)
                {
                    case "FN": yield return ("Name", val); break;
                    case "ORG": yield return ("Organisation", val); break;
                    case "TEL": yield return ("Phone", val); break;
                    case "EMAIL": yield return ("Email", val); break;
                    case "URL": yield return ("Website", val); break;
                }
            }
            yield break;
        }

        if (Regex.IsMatch(v, @"^https?://", RegexOptions.IgnoreCase) && Uri.TryCreate(v, UriKind.Absolute, out var uri))
        {
            yield return ("Host", uri.Host);
            if (!uri.IsDefaultPort) yield return ("Port", uri.Port.ToString());
            if (uri.AbsolutePath.Length > 1) yield return ("Path", Uri.UnescapeDataString(uri.AbsolutePath));
            if (uri.Scheme == "http") yield return ("Warning", "Plain http link, not encrypted");

            // Login and setup links carry their settings as parameters (server addresses, tenant, tokens).
            // Show each one so the reader can see where the link points before opening it.
            foreach (var parameter in QueryParameters(uri))
                yield return parameter;
        }
    }

    /// <summary>Key/value pairs from the query string and from a query hidden in the fragment (#...?a=b).
    /// Values that look like secrets are shortened so the screen does not expose them in full.</summary>
    public static IEnumerable<(string Key, string Value)> QueryParameters(Uri uri)
    {
        var parts = new List<string>();
        if (uri.Query.Length > 1) parts.Add(uri.Query[1..]);
        var fragment = uri.Fragment.Length > 1 ? uri.Fragment[1..] : "";
        int q = fragment.IndexOf('?');
        if (q >= 0) parts.Add(fragment[(q + 1)..]);
        else if (fragment.Contains('=')) parts.Add(fragment);

        foreach (var part in parts)
        {
            foreach (var pair in part.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]).Replace('+', ' ');
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                if (key.Length == 0) continue;

                bool secret = Regex.IsMatch(key, "token|secret|password|pwd|key|auth|code|sig", RegexOptions.IgnoreCase) && value.Length > 12;
                if (secret) value = value[..6] + "…" + value[^4..] + $"  ({value.Length} characters)";
                yield return (key, value.Length == 0 ? "(empty)" : value);
            }
        }
    }
}
