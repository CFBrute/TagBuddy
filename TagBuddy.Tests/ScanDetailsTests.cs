using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>What the details under a scan say about the content: its kind, and the fields of Wi-Fi codes, contact
/// cards, events and web links, with secrets kept off the screen.</summary>
public class ScanDetailsTests
{
    [Theory]
    [InlineData("https://example.com", "Web link")]
    [InlineData("WIFI:T:WPA;S:Home;P:pw;;", "Wi-Fi network")]
    [InlineData("BEGIN:VCARD\nFN:Ada\nEND:VCARD", "Contact card (vCard)")]
    [InlineData("MECARD:N:Ada;;", "Contact card (MeCard)")]
    [InlineData("BEGIN:VEVENT\nSUMMARY:x\nEND:VEVENT", "Calendar event")]
    [InlineData("MATMSG:TO:a@b.c;;", "Email")]
    [InlineData("tel:123", "Phone number")]
    [InlineData("geo:1,2", "Location")]
    [InlineData("otpauth://totp/x?secret=A", "One-time password secret (authenticator)")]
    [InlineData("FIDO:/123456", "Passkey sign-in request (FIDO) from another device")]
    [InlineData("spotify://track/1", "App link (spotify)")]
    [InlineData("12345", "Numeric")]
    [InlineData("ABC-123", "Alphanumeric (QR compact mode)")]
    [InlineData("héllo", "Text (Unicode)")]
    [InlineData("hello there", "Text")]
    [InlineData("   ", "empty")]
    public void DescribeContent_names_the_kind(string value, string expected) =>
        Assert.Equal(expected, ScanDetails.DescribeContent(value));

    private static Dictionary<string, string> Fields(string value) =>
        ScanDetails.DescribeStructuredContent(value).GroupBy(f => f.Label).ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(f => f.Value)));

    [Fact]
    public void Wifi_shows_the_network_and_hides_the_password()
    {
        var f = Fields("WIFI:T:WPA;S:Home Net;P:supersecret;H:true;;");
        Assert.Equal("Home Net", f["Network"]);
        Assert.Equal("WPA", f["Security"]);
        Assert.Equal("•••••••••••  (11 characters)", f["Password"]);
        Assert.DoesNotContain("supersecret", f["Password"]);
        Assert.Equal("yes", f["Hidden network"]);
    }

    [Fact]
    public void An_open_network_says_so_and_has_no_password_row()
    {
        var f = Fields("WIFI:T:nopass;S:Cafe;;");
        Assert.Equal("open", f["Security"]);
        Assert.False(f.ContainsKey("Password"));
        Assert.False(f.ContainsKey("Hidden network"));
    }

    [Fact]
    public void Wifi_with_lower_case_keys_and_no_final_semicolon_is_still_read() =>
        Assert.Equal("Home", Fields("WIFI:s:Home;t:WPA;p:pw")["Network"]);

    [Fact]
    public void A_vcard_shows_every_phone_and_email()
    {
        var f = Fields("BEGIN:VCARD\nFN:Ada Lovelace\nORG:Engines\nTEL:+1\nitem1.TEL:+2\nEMAIL:a@b.c\nURL:https://ada.example\nEND:VCARD");
        Assert.Equal("Ada Lovelace", f["Name"]);
        Assert.Equal("Engines", f["Organisation"]);
        Assert.Equal("+1 | +2", f["Phone"]);
        Assert.Equal("a@b.c", f["Email"]);
        Assert.Equal("https://ada.example", f["Website"]);
    }

    [Fact]
    public void A_mecard_is_described_too() =>
        Assert.Equal("Ada Lovelace", Fields("MECARD:N:Lovelace,Ada;TEL:+1;;")["Name"]);

    [Fact]
    public void An_all_day_event_shows_its_date()
    {
        var f = Fields("BEGIN:VEVENT\nSUMMARY:Holiday\nDTSTART;VALUE=DATE:20261224\nLOCATION:Home\nEND:VEVENT");
        Assert.Equal("Holiday", f["Event"]);
        Assert.Equal("2026-12-24", f["Starts"]);
        Assert.Equal("Home", f["Where"]);
    }

    [Fact]
    public void A_web_link_shows_host_path_and_warns_about_http()
    {
        var f = Fields("http://example.com:8080/some%20page?x=1");
        Assert.Equal("example.com", f["Host"]);
        Assert.Equal("8080", f["Port"]);
        Assert.Equal("/some page", f["Path"]);
        Assert.Contains("not encrypted", f["Warning"]);
        Assert.Equal("1", f["x"]);
    }

    [Fact]
    public void Secret_looking_parameters_are_shortened()
    {
        var parameters = ScanDetails.QueryParameters(new Uri("https://example.com/?token=abcdefghijklmnop&name=Ada")).ToDictionary();
        Assert.Equal("abcdef…mnop  (16 characters)", parameters["token"]);
        Assert.Equal("Ada", parameters["name"]);
    }

    [Fact]
    public void A_query_hidden_in_the_fragment_is_read()
    {
        var parameters = ScanDetails.QueryParameters(new Uri("https://app.example/#/login?server=eu&empty=")).ToDictionary();
        Assert.Equal("eu", parameters["server"]);
        Assert.Equal("(empty)", parameters["empty"]);
    }

    [Fact]
    public void Plain_text_has_no_fields() =>
        Assert.Empty(ScanDetails.DescribeStructuredContent("just text"));
}
