using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>What a scanned or read value can be handed to: links of every kind, passkey sign-in, authenticators,
/// wallets, Wi-Fi networks, contacts and events; and what must never be opened.</summary>
public class HandoffTests
{
    private const string Fido = "FIDO:/4689527958562423009499526548803663032560695256510653288288886557636269452624233228716440103336559143694994246798394501900613457586992358601778458008038610709665408307";

    [Theory]
    [InlineData("https://example.com", HandoffKind.Web)]
    [InlineData("example.com/page", HandoffKind.Web)]
    [InlineData("mailto:a@example.com", HandoffKind.Email)]
    [InlineData("MATMSG:TO:a@example.com;SUB:Hi;BODY:Hello there;;", HandoffKind.Email)]
    [InlineData("tel:+46700000000", HandoffKind.Phone)]
    [InlineData("smsto:+46700000000:hi", HandoffKind.Sms)]
    [InlineData("geo:59.33,18.06", HandoffKind.Map)]
    [InlineData(Fido, HandoffKind.Passkey)]
    [InlineData("otpauth://totp/Example:me?secret=JBSWY3DPEHPK3PXP&issuer=Example", HandoffKind.Authenticator)]
    [InlineData("bitcoin:bc1qxyz?amount=0.001", HandoffKind.Payment)]
    [InlineData("lightning:lnbc1...", HandoffKind.Payment)]
    [InlineData("market://details?id=com.example", HandoffKind.AppStore)]
    [InlineData("spotify://track/123", HandoffKind.OtherApp)]
    [InlineData("WIFI:T:WPA;S:Home;P:secret;;", HandoffKind.Wifi)]
    [InlineData("BEGIN:VCARD\nVERSION:3.0\nFN:Ada Lovelace\nTEL:+44123\nEND:VCARD", HandoffKind.Contact)]
    [InlineData("MECARD:N:Lovelace,Ada;TEL:+44123;;", HandoffKind.Contact)]
    [InlineData("BEGIN:VEVENT\nSUMMARY:Meeting\nDTSTART:20261023T140000Z\nEND:VEVENT", HandoffKind.Event)]
    public void Detects_what_another_app_can_take(string value, HandoffKind kind)
    {
        Assert.True(Handoff.TryDetect(value, out var handoff));
        Assert.Equal(kind, handoff.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some text")]
    [InlineData("12345678")]
    [InlineData("Note:hello")]                     // a scheme-like word is not an app link
    [InlineData("ID:123")]
    [InlineData("javascript:alert(1)")]            // never opened
    [InlineData("file:///sdcard/secret.txt")]
    [InlineData("content://contacts/people")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    [InlineData("FIDO:")]                          // not a real request
    [InlineData("WIFI:T:WPA;P:secret;;")]          // no network name
    [InlineData("BEGIN:VCARD\nVERSION:3.0\nEND:VCARD")]
    public void Rejects_what_nothing_should_open(string? value) =>
        Assert.False(Handoff.TryDetect(value, out _));

    [Fact]
    public void A_passkey_request_keeps_its_upper_case_scheme()
    {
        // Android matches schemes case-sensitively, and Google Play services registers "FIDO".
        Assert.True(Handoff.TryDetect(Fido, out var handoff));
        Assert.StartsWith("FIDO:/", handoff.LaunchText, StringComparison.Ordinal);
        Assert.Equal("Sign in with passkey", handoff.Label());
        Assert.NotNull(handoff.Confirmation);
    }

    [Theory]
    [InlineData("https://example.com", null, "Open link")]
    [InlineData("https://youtube.com/watch?v=x", "YouTube", "Open in YouTube")]
    [InlineData("https://example.com", " ", "Open link")]
    [InlineData("tel:123", "Phone", "Call number")]
    [InlineData("mailto:a@example.com", "Gmail", "Send email")]
    [InlineData("sms:123", null, "Send SMS")]
    [InlineData("geo:1,2", null, "Show on map")]
    [InlineData("otpauth://totp/x?secret=ABC", "Authenticator", "Add to Authenticator")]
    [InlineData("otpauth://totp/x?secret=ABC", null, "Add to authenticator app")]
    [InlineData("bitcoin:abc", null, "Open in wallet app")]
    [InlineData("market://details?id=x", null, "Open in Play Store")]
    [InlineData("spotify://track/1", "Spotify", "Open in Spotify")]
    [InlineData("WIFI:S:Home;;", null, "Join Wi-Fi network")]
    [InlineData("MECARD:N:Ada;;", null, "Add to contacts")]
    [InlineData("BEGIN:VEVENT\nSUMMARY:x\nEND:VEVENT", null, "Add to calendar")]
    public void Labels_say_what_will_happen(string value, string? app, string expected)
    {
        Assert.True(Handoff.TryDetect(value, out var handoff));
        Assert.Equal(expected, handoff.Label(app));
    }

    [Fact]
    public void A_bare_domain_is_handed_over_as_https() =>
        Assert.True(Handoff.TryDetect("example.com", out var h) && h.LaunchText == "https://example.com/");

    [Fact]
    public void MATMSG_becomes_a_mailto_link()
    {
        Assert.True(Handoff.TryDetect("MATMSG:TO:a@example.com;SUB:Hi there;BODY:Line;;", out var h));
        Assert.Equal("mailto:a@example.com?subject=Hi%20there&body=Line", h.LaunchText);
    }

    [Fact]
    public void Wifi_fields_are_read_with_escapes()
    {
        Assert.True(WifiNetwork.TryParse(@"WIFI:T:WPA;S:My\;Net;P:pa\:ss\\word;H:true;;", out var n));
        Assert.Equal(("My;Net", "pa:ss\\word", "WPA", true), (n.Ssid, n.Password, n.Security, n.Hidden));
        Assert.False(n.IsOpen);
    }

    [Fact]
    public void An_open_network_has_no_password()
    {
        Assert.True(WifiNetwork.TryParse("WIFI:T:nopass;S:Cafe;;", out var n));
        Assert.True(n.IsOpen);
        Assert.Null(n.Password);
    }

    [Fact]
    public void A_vcard_gives_the_contacts_app_its_fields()
    {
        const string card = "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Lovelace;Ada;;;\r\nFN:Ada Lovelace\r\nORG:Analytical Engines;Research\r\n" +
                            "TITLE:Programmer\r\nTEL;TYPE=CELL:+44 123\r\nitem1.TEL:+44 456\r\nEMAIL:ada@example.com\r\n" +
                            "ADR;TYPE=HOME:;;12 Street;London;;N1;UK\r\nNOTE:First\\nprogrammer\r\nEND:VCARD";
        Assert.True(ContactCard.TryParse(card, out var c));
        Assert.Equal("Ada Lovelace", c.Name);
        Assert.Equal(["+44 123", "+44 456"], c.Phones);
        Assert.Equal(["ada@example.com"], c.Emails);
        Assert.Equal("Analytical Engines Research", c.Organization);
        Assert.Equal("12 Street, London, N1, UK", c.Address);
        Assert.Equal("First\nprogrammer", c.Note);
    }

    [Fact]
    public void A_vcard_without_FN_builds_the_name_from_N()
    {
        Assert.True(ContactCard.TryParse("BEGIN:VCARD\nN:Lovelace;Ada;;;\nEND:VCARD", out var c));
        Assert.Equal("Ada Lovelace", c.Name);
    }

    [Fact]
    public void A_mecard_name_is_turned_around()
    {
        Assert.True(ContactCard.TryParse("MECARD:N:Lovelace,Ada;TEL:+44123;EMAIL:ada@example.com;;", out var c));
        Assert.Equal("Ada Lovelace", c.Name);
        Assert.Equal(["+44123"], c.Phones);
    }

    [Fact]
    public void An_event_is_read_with_times_and_place()
    {
        const string ics = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nSUMMARY:Team meeting\nDTSTART:20261023T140000Z\nDTEND:20261023T150000Z\n" +
                           "LOCATION:Room 4\\, floor 2\nDESCRIPTION:Bring notes\nEND:VEVENT\nEND:VCALENDAR";
        Assert.True(CalendarEvent.TryParse(ics, out var e));
        Assert.Equal("Team meeting", e.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 14, 0, 0, TimeSpan.Zero), e.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 15, 0, 0, TimeSpan.Zero), e.End);
        Assert.Equal("Room 4, floor 2", e.Location);
        Assert.False(e.AllDay);
    }

    [Fact]
    public void A_date_only_event_is_all_day()
    {
        Assert.True(CalendarEvent.TryParse("BEGIN:VEVENT\nSUMMARY:Holiday\nDTSTART;VALUE=DATE:20261224\nEND:VEVENT", out var e));
        Assert.True(e.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 12, 24, 0, 0, 0, TimeSpan.Zero), e.Start);
    }
}
