namespace TagBuddy.Services;

/// <summary>Hands a <see cref="Handoff"/> to the app that takes it: links to whichever app Android picks for them,
/// Wi-Fi networks to the system's "add network" screen, contacts to the contacts app's new-contact screen and events
/// to the calendar's new-event screen. Nothing is added without the person confirming it in that app.</summary>
public static class HandoffLauncher
{
    /// <summary>The app a link-shaped handoff will open in, when Android can tell (see the queries in
    /// AndroidManifest.xml); null for the others and when unknown.</summary>
    public static string? AppName(Handoff handoff) => handoff.Kind switch
    {
        HandoffKind.Web => handoff.Uri is { } uri ? Links.HandlerName(uri) : null,
        HandoffKind.Authenticator or HandoffKind.Payment or HandoffKind.OtherApp => Links.HandlerName(handoff.LaunchText),
        _ => null,
    };

    /// <summary>The label with the app's name filled in where Android knows it.</summary>
    public static string Label(Handoff handoff) => handoff.Label(AppName(handoff));

    /// <summary>Opens it; false when no app on the phone takes it.</summary>
    public static async Task<bool> OpenAsync(Handoff handoff)
    {
        switch (handoff.Kind)
        {
            case HandoffKind.Web when handoff.Uri is { } uri:
                return await Links.OpenAsync(uri);
#if ANDROID
            case HandoffKind.Wifi when WifiNetwork.TryParse(handoff.Value, out var network):
                return await JoinWifiAsync(network);
            case HandoffKind.Contact when ContactCard.TryParse(handoff.Value, out var card):
                return Start(ContactIntent(card));
            case HandoffKind.Event when CalendarEvent.TryParse(handoff.Value, out var calendarEvent):
                return Start(EventIntent(calendarEvent));
#endif
            default:
                return Links.OpenText(handoff.LaunchText);
        }
    }

#if ANDROID
    private static bool Start(Android.Content.Intent intent)
    {
        try
        {
            var activity = Platform.CurrentActivity;
            if (activity is null) intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            (activity ?? Platform.AppContext).StartActivity(intent);
            return true;
        }
        catch (Android.Content.ActivityNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Android 11 and later show their own "save this network?" screen for a suggestion. Older versions, and
    /// WEP networks (which suggestions cannot hold), get the password on the clipboard and the Wi-Fi settings.</summary>
    private static async Task<bool> JoinWifiAsync(WifiNetwork network)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && network.Security != "WEP")
        {
            try
            {
                var builder = new Android.Net.Wifi.WifiNetworkSuggestion.Builder().SetSsid(network.Ssid);
                if (!network.IsOpen)
                {
                    if (network.Security is "SAE" or "WPA3") builder.SetWpa3Passphrase(network.Password!);
                    else builder.SetWpa2Passphrase(network.Password!);
                }
                if (network.Hidden) builder.SetIsHiddenSsid(true);

                var intent = new Android.Content.Intent(Android.Provider.Settings.ActionWifiAddNetworks);
                intent.PutParcelableArrayListExtra(Android.Provider.Settings.ExtraWifiNetworkList,
                    new List<Android.OS.IParcelable> { builder.Build() });
                if (Start(intent)) return true;
            }
            catch
            {
                // Fall back to the settings screen below.
            }
        }

        if (network.Password is { } password)
        {
            await Clipboard.Default.SetTextAsync(password);
            Notice.Show($"Password for {network.Ssid} copied");
        }
        return Start(new Android.Content.Intent(Android.Provider.Settings.ActionWifiSettings));
    }

    private static Android.Content.Intent ContactIntent(ContactCard card)
    {
        var intent = new Android.Content.Intent(Android.Provider.ContactsContract.Intents.Insert.Action);
        intent.SetType(Android.Provider.ContactsContract.RawContacts.ContentType);
        void put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) intent.PutExtra(key, value);
        }
        put(Android.Provider.ContactsContract.Intents.Insert.Name, card.Name);
        put(Android.Provider.ContactsContract.Intents.Insert.Phone, card.Phones.ElementAtOrDefault(0));
        put(Android.Provider.ContactsContract.Intents.Insert.SecondaryPhone, card.Phones.ElementAtOrDefault(1));
        put(Android.Provider.ContactsContract.Intents.Insert.TertiaryPhone, card.Phones.ElementAtOrDefault(2));
        put(Android.Provider.ContactsContract.Intents.Insert.Email, card.Emails.ElementAtOrDefault(0));
        put(Android.Provider.ContactsContract.Intents.Insert.SecondaryEmail, card.Emails.ElementAtOrDefault(1));
        put(Android.Provider.ContactsContract.Intents.Insert.Company, card.Organization);
        put(Android.Provider.ContactsContract.Intents.Insert.JobTitle, card.Title);
        put(Android.Provider.ContactsContract.Intents.Insert.Postal, card.Address);
        put(Android.Provider.ContactsContract.Intents.Insert.Notes, string.Join("\n", new[] { card.Url, card.Note }.Where(s => !string.IsNullOrWhiteSpace(s))));
        return intent;
    }

    private static Android.Content.Intent EventIntent(CalendarEvent e)
    {
        var intent = new Android.Content.Intent(Android.Content.Intent.ActionInsert, Android.Provider.CalendarContract.Events.ContentUri);
        if (e.Title is { } title) intent.PutExtra(Android.Provider.CalendarContract.EventsColumns.Title, title);
        if (e.Location is { } location) intent.PutExtra(Android.Provider.CalendarContract.EventsColumns.EventLocation, location);
        if (e.Description is { } description) intent.PutExtra(Android.Provider.CalendarContract.EventsColumns.Description, description);
        if (e.Start is { } start) intent.PutExtra(Android.Provider.CalendarContract.ExtraEventBeginTime, start.ToUnixTimeMilliseconds());
        if (e.End is { } end) intent.PutExtra(Android.Provider.CalendarContract.ExtraEventEndTime, end.ToUnixTimeMilliseconds());
        if (e.AllDay) intent.PutExtra(Android.Provider.CalendarContract.ExtraEventAllDay, true);
        return intent;
    }
#endif
}
