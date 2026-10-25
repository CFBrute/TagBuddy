namespace TagBuddy.Models;

/// <summary>One piece of a help topic, drawn by <c>Pages/HelpTopicPage</c>.</summary>
public abstract record HelpBlock;

/// <summary>A paragraph.</summary>
public sealed record HelpText(string Text) : HelpBlock;

/// <summary>A small heading inside a topic.</summary>
public sealed record HelpHeading(string Text) : HelpBlock;

/// <summary>A list: numbered for steps to follow, bulleted otherwise.</summary>
public sealed record HelpList(bool Numbered, params string[] Items) : HelpBlock;

/// <summary>A highlighted box: a tip, or a warning about something that cannot be undone.</summary>
public sealed record HelpTip(string Text, bool Warning = false) : HelpBlock;

/// <summary>An illustration from Resources/Images (the .png name MAUI makes of the .svg), with a caption.</summary>
public sealed record HelpImage(string File, string Caption, double Height = 170) : HelpBlock;

/// <summary>A box that opens on a tap, for the in-depth part: the topic reads well without it.</summary>
public sealed record HelpMore(string Title, params HelpBlock[] Blocks) : HelpBlock;

/// <summary>Links to other topics, by id.</summary>
public sealed record HelpSeeAlso(params string[] TopicIds) : HelpBlock;

/// <summary>One help topic: a card in the Help list and a page of its own.</summary>
public sealed record HelpTopic(string Id, string Title, string Icon, string Summary, IReadOnlyList<HelpBlock> Blocks);

/// <summary>The manual under More → Help. Written for someone who has never used an NFC tag, with the details in
/// boxes that open on a tap. Keep it in step with the app: a changed screen needs a changed topic.</summary>
public static class HelpContent
{
    public static IReadOnlyList<HelpTopic> Topics { get; } = Build(Features.Amiibo);

    public static HelpTopic? Find(string? id) => Topics.FirstOrDefault(t => t.Id == id);

    /// <summary>All topics. <paramref name="amiibo"/> is off in the Play Store edition, which must not name
    /// Amiibo anywhere (see Features.cs).</summary>
    public static IReadOnlyList<HelpTopic> Build(bool amiibo) =>
    [
        new("start", "Getting started", "ic_help.png", "What each tab is for, in one minute.",
        [
            new HelpText("Tag Buddy makes and reads the two kinds of codes a phone can deal with: printed codes (QR codes and barcodes) and NFC tags, the small chips in stickers, key fobs and cards."),
            new HelpHeading("The tabs"),
            new HelpList(false,
                "Generate: make a QR code or a barcode from a link, text, Wi-Fi details or a contact.",
                "Saved: everything you keep, such as codes, text notes and tag backups.",
                "Scan: read a QR code or barcode with the camera, or from a picture on the phone.",
                "NFC: read a tag or card held to the phone, or write a link or text to a tag.",
                "More: settings, this help, and information about the app."),
            new HelpTip("Buttons that only show an icon have a name too. Long-press one to see what it does."),
            new HelpMore("Where does my data go?",
                new HelpText("Everything you save stays in the app's own storage on this phone. There is no account, no advertising and no tracking, and nothing is sent to the developer."),
                new HelpText("The only time the app uses the internet is for link previews: the title and picture of a web page you scanned or saved, fetched from that website. You can turn that off in Settings."),
                new HelpText("If Android backup is on for this phone, Android may include the app's saved items in your Google backup. Uninstalling the app deletes everything it stored.")),
            new HelpSeeAlso("qr", "scan", "nfc-read", "previews"),
        ]),

        new("qr", "QR codes", "tab_generate.png", "Make a QR code from a link, text, Wi-Fi details or a contact.",
        [
            new HelpList(true,
                "Open Generate and pick the QR Code tab at the top.",
                "Type the content, or tap Paste to use what you copied.",
                "The code is drawn as you type. Below it you see how big it is.",
                "Tap the code to show it full screen, or use the buttons under it to share it as a picture, save it to the gallery or keep it in Saved."),
            new HelpImage("help_qr.png", "The three big corner squares tell a scanner where the code is and which way up it is. The white border around it, the quiet zone, belongs to the code: keep it when you print or crop."),
            new HelpMore("What can a QR code hold?",
                new HelpText("Up to a few thousand characters of any text. In practice, keep it short: the more it holds, the smaller its squares and the harder it is to scan from a distance or from a small print."),
                new HelpText("Phones recognise some content and offer to act on it:"),
                new HelpList(false,
                    "A web link: https://example.com",
                    "Wi-Fi: WIFI:T:WPA;S:network name;P:password;;  joins the network when scanned.",
                    "A contact card, starting with BEGIN:VCARD.",
                    "Email: mailto:name@example.com",
                    "Phone call: tel:+46701234567, or a text message: smsto:+46701234567",
                    "A place on the map: geo:59.3293,18.0686")),
            new HelpMore("Advanced options: error correction and margin",
                new HelpText("Error correction lets a scanner read a code that is scratched, dirty or partly covered. It costs space: a higher level makes the code denser."),
                new HelpList(false,
                    "L: up to 7% of the code can be damaged. The smallest code.",
                    "M: up to 15%. The default, right for screens and most prints.",
                    "Q: up to 25%.",
                    "H: up to 30%. Use it for a logo on top of the code, or for stickers that wear."),
                new HelpText("The margin is the white quiet zone around the code, counted in the code's own squares. Scanners need some white around a code to find it. 2 is the default; the QR standard asks for 4, which helps when the code is printed on a busy background.")),
            new HelpMore("Text under the image",
                new HelpText("When you share or save the code as a picture, a line of text can be printed under it, for example what the code is for. It follows the content until you change it, and an empty text means no text at all."),
                new HelpText("Its size, distance from the code and maximum width are measured against the code itself, so the picture looks the same however large it is printed. The text is only in the picture; the code itself does not change."),
                new HelpText("Pick a font for it too: sans, condensed, serif or monospace, regular or bold.")),
            new HelpTip("The QR and Barcode tabs remember how you left them: the advanced options, the barcode type, and the position, font, size, distance and width of the text under the image. Each tab keeps its own."),
            new HelpMore("Saving versions of the same code",
                new HelpText("Saving a code that is already in Saved with exactly the same look just tells you so. If the same code is saved with a different look, such as other text under the image or other QR options, the app says what differs and asks whether to keep this version too."),
                new HelpText("In Saved, a version with its own text under the image shows that text after the content, so the versions are easy to tell apart.")),
            new HelpSeeAlso("show", "barcodes", "saved"),
        ]),

        new("barcodes", "Barcodes", "tab_generate.png", "Product codes, Code 128 and the other striped codes.",
        [
            new HelpList(true,
                "Open Generate and pick the Barcode tab at the top.",
                "Choose the type of barcode.",
                "Type the value. If it does not fit the type, a message says what is wrong."),
            new HelpImage("help_barcode.png", "A barcode is read across the stripes. Like a QR code it needs a white quiet zone at both ends, so do not crop it tight.", 130),
            new HelpMore("Which type should I use?",
                new HelpList(false,
                    "Code 128: any text with letters, digits and symbols, and compact. The best choice for labels, IDs and stock codes.",
                    "Code 39: capital letters, digits and a few symbols. Older and wider, but nearly every scanner reads it.",
                    "Code 93: the same characters as Code 39, more compact.",
                    "EAN-13 and EAN-8: product codes in shops in most of the world. Digits only.",
                    "UPC-A: product codes in North America. Digits only.",
                    "ITF (Interleaved 2 of 5): an even number of digits, used on shipping boxes.",
                    "Codabar: digits and a few symbols, still used by libraries and blood banks.")),
            new HelpMore("Check digits",
                new HelpText("EAN and UPC codes end in a check digit, worked out from the other digits, so a scanner can tell when it misread a stripe. Type the code without its last digit and the app adds it for you. If you type all digits and the last one is wrong, the app says so."),
                new HelpText("A real product code is assigned to a company (by GS1). Making one up works in your own shop or at home, but a till elsewhere will not know the product.")),
            new HelpSeeAlso("show", "qr"),
        ]),

        new("show", "Showing a code to a scanner", "tab_generate.png", "Full screen and brightness, for tills, gates and other scanners.",
        [
            new HelpText("Tap a code in Generate or in Saved to show it full screen. The screen stays on and gets brighter while the code is shown, and goes back to normal when you leave. Tap the code again to close it."),
            new HelpText("It starts at double your normal brightness (at least 20%). The slider under the code turns it up or down."),
            new HelpTip("Scanners at tills and gates struggle with dim screens and reflections. Turn the brightness up, tilt the phone a little to avoid glare, and hold it still about 10 to 20 cm from the scanner."),
            new HelpMore("Why not always at full brightness?",
                new HelpText("Going from a dimmed phone in a dark room straight to full brightness is blinding, and most scanners read a code well before that. If you would rather always have full brightness, turn on \"Always show codes at maximum brightness\" in Settings.")),
            new HelpSeeAlso("settings", "saved"),
        ]),

        new("scan", "Scanning codes", "tab_scan.png", "Read QR codes and barcodes with the camera, or from a picture.",
        [
            new HelpList(true,
                "Open Scan. The first time, allow the app to use the camera.",
                "Hold the code inside the square in the middle.",
                "When it is read, the phone vibrates and the result appears."),
            new HelpImage("help_viewfinder.png", "The corners of the square tell you what is happening. White: looking. Amber: something like a code is in view, hold still. Green: read.", 120),
            new HelpText("The result says what the code contains, such as a web link, Wi-Fi details or a contact, with buttons to save it, copy it, open the link, view the page inside the app, or make a code of your own from it. Tap \"Scan again\" for the next one."),
            new HelpTip("Nothing found? Move closer so the code fills most of the square, hold still, tap the square to focus, or turn on the torch."),
            new HelpMore("Codes that another app acts on",
                new HelpText("Some codes are requests for another app. Tag Buddy says what the code is and offers a button that hands it over:"),
                new HelpList(false,
                    "Passkey sign-in (FIDO): a computer or tablet asks to use a passkey on this phone. \"Sign in with passkey\" hands it to Android, which checks over Bluetooth that the device is nearby. Continue only if you are signing in there yourself.",
                    "Wi-Fi details: \"Join Wi-Fi network\" asks Android to save the network.",
                    "A contact card or a calendar event: opens the contacts or calendar app with the details filled in, for you to save.",
                    "An authenticator setup code (otpauth): hands the secret to your authenticator app.",
                    "Wallet, Google Play and other app links: open in the app that takes them, named on the button when Android can tell."),
                new HelpText("Nothing is saved or signed in without you confirming it in the other app. Links that could reach into the phone itself, such as file or JavaScript links, are never opened.")),
            new HelpMore("Reading a code from a picture",
                new HelpText("\"From image\" finds a code in a photo or screenshot that is already on the phone, for example a ticket in an email. The camera turns off while you choose, and comes back if you go back without a picture.")),
            new HelpMore("Why do barcodes take a moment longer?",
                new HelpText("A striped barcode has to be read the same way twice in a row before it counts. Striped codes have little protection against misreading, and a single glance can find a \"barcode\" in a pattern or in part of a QR code. QR codes check themselves and count at once.")),
            new HelpMore("What the details mean",
                new HelpList(false,
                    "Format: the kind of code, such as QR Code or EAN-13.",
                    "Content: what the text is, such as a web link, Wi-Fi network or contact card. Wi-Fi details and contacts are broken into their fields.",
                    "Length: how many characters and bytes the code holds.",
                    "Error correction: for QR codes, how much damage the code could have survived (see QR codes).")),
            new HelpSeeAlso("previews", "saved", "trouble"),
        ]),

        new("nfc-read", "Reading NFC tags", "tab_nfc.png", "Hold a tag or card to the phone to see what it is and what it holds.",
        [
            new HelpText("NFC tags are small chips with an antenna, in stickers, key fobs, cards and wristbands. A phone powers them by radio when they are a few centimetres away, so they need no battery."),
            new HelpList(true,
                "Open NFC and make sure \"Read a tag\" is selected. If NFC is off, a button takes you to the phone's settings.",
                "Hold the tag flat against the back of the phone and keep it still for a moment.",
                "The phone vibrates and shows what it found."),
            new HelpImage("help_nfc_phone.png", "On most phones the NFC antenna sits in the middle of the back, a little above the centre. If nothing happens, slide the tag slowly around. A thick case or metal nearby shortens the range."),
            new HelpText("You see the tag's serial number, its chip, how much it can hold, and its content. A link or text gets the same buttons as a scanned code."),
            new HelpMore("Bank cards, passports and access cards",
                new HelpText("These are recognised by type only: a payment card by its brand (Visa, Mastercard, ...), a passport or ID card, a MIFARE Classic access card. The app never asks such a card for the holder's data; it shows them as secure items with their type and serial number.")),
            new HelpMore("What is NDEF?",
                new HelpText("NDEF is the standard way to store content on a tag so that any phone understands it: a link, a text, a contact card. A tag without NDEF content still has a serial number and memory, which the app shows.")),
            .. Features.MifareKeyTools
                ? new HelpBlock[]
                {
                    new HelpMore("MIFARE Classic keys",
                        new HelpText("Each sector of a MIFARE Classic card is locked behind a 6-byte key. The app opens sectors that still use a well-known public key and shows only the serial for the rest."),
                        new HelpText("When some sectors stay locked, you can type one key to try, or import a dictionary of default keys (a .dic/.txt, such as the one shipped with Proxmark tools) and tap \"Try all default keys\" to run the whole list against the locked sectors. Trying keys is safe: Classic cards have no wrong-key lockout."),
                        new HelpText("A key that opens a sector is remembered for that card's serial, so the same card opens quickly next time. A sector with a genuinely custom key stays closed; only its real key opens it.")),
                }
                : [],
            new HelpMore("Readable memory and backups",
                new HelpText("For NTAG and Ultralight tags, the most common stickers, the app reads every page of the tag's memory and shows it. \"Save dump\" keeps a copy in Saved, which can be written back to a tag later."),
                new HelpSeeAlso("dumps")),
            new HelpSeeAlso("nfc-write", "private", "trouble"),
        ]),

        new("nfc-write", "Writing NFC tags", "tab_nfc.png", "Put a link, text or contact on a tag that any phone can read.",
        [
            new HelpList(true,
                "Open NFC and tap \"Write a tag\".",
                "Choose what to write. \"Auto\" writes a link when the text is a link, and plain text otherwise.",
                "Type the content, or tap \"Load from Saved\".",
                "When it says Ready, hold a writable tag to the back of the phone and keep it still until it vibrates.",
                "It stays ready: hold another tag to write the same content again."),
            new HelpTip("For tags that every phone can read, buy NTAG213, NTAG215 or NTAG216 stickers. An NTAG213 holds about 140 bytes, enough for a link; an NTAG215 about 500 and an NTAG216 about 870."),
            new HelpMore("The kinds of content you can write",
                new HelpList(false,
                    "Auto: a link or plain text, decided by what you type.",
                    "Link: opens in the browser or the right app when another phone reads the tag.",
                    "Plain text: shown as text.",
                    "Contact (vCard): a contact card the phone offers to save.",
                    "Private text: encrypted with a passphrase; only Tag Buddy can read it (see Private text).",
                    "Raw bytes: text stored without the NDEF format. Only this app reads it back; it is meant for tags that cannot hold NDEF any more.",
                    "Pages or blocks: edit a tag's memory directly, in hex or as text. Read the tag first and tap \"Edit and write to this tag\", then tap any byte and type over it; Undo and Redo go one keystroke at a time, \"All to 00\" clears every page, and a MIFARE Classic sector the read opened can be given a new key, which the phone remembers for that tag, and \"Undo on the tag\" puts back what a write changed. The write only goes to that same tag, and only the bytes you changed are written. The serial, lock and password settings are never written.",
                    amiibo ? "Binary dump: writes a saved backup back to a tag, including Amiibo dumps (see Tag backups)." : "Binary dump: writes a saved backup back to a tag (see Tag backups).",
                    "Erase: clears the tag so it is empty again.")),
            new HelpMore("Can a tag get locked by accident?",
                new HelpText("No. Writing content never locks a tag: you can rewrite a tag as often as you like. Tags have one-time lock bits that make pages read-only for good, but only someone who sets them on purpose locks a tag, and Tag Buddy never does."),
                new HelpText("A tag locked by another tool cannot be changed. The app writes around locked pages where it can, and says which pages it had to leave alone.")),
            new HelpMore("Why can some tags not be written?",
                new HelpList(false,
                    "The tag is locked (see above).",
                    "The tag has a password. The app never guesses passwords, because some tags lock for good after a few wrong tries.",
                    "It is a bank, transit or access card. Those are not writable.",
                    "The content is too big for the tag. An NTAG213 is small; a long text needs an NTAG215 or 216.",
                    amiibo
                        ? "The tag was once used for an Amiibo. That permanently changes a setting on the tag, and phones will not read it as a link or text tag again. \"Raw bytes\" can still store text on it for this app."
                        : "The tag was formatted for another use, which permanently changes a setting on it. \"Raw bytes\" can still store text on it for this app.")),
            new HelpSeeAlso("private", "dumps", "trouble"),
        ]),

        new("private", "Private text on tags", "ic_lock.png", "Encrypt text with a passphrase so only you can read it.",
        [
            new HelpText("Private text is encrypted on your phone before it is written. Anyone can read the tag's bytes, but without the passphrase they are meaningless. Other phones see nothing readable."),
            new HelpHeading("Writing"),
            new HelpList(true,
                "Open NFC, tap \"Write a tag\" and choose \"Private text (passphrase)\".",
                "Type the text.",
                "Type a passphrase of at least 8 characters, twice.",
                "When it says Ready, hold the tag to the phone."),
            new HelpHeading("Reading"),
            new HelpText("Hold the tag to the phone. Tag Buddy opens by itself, even when it is closed, and asks for the passphrase. Type it and tap Unlock."),
            new HelpTip("The passphrase is never stored, not even on this phone. If it is forgotten, the text cannot be recovered by anyone.", Warning: true),
            new HelpMore("How secure is it?",
                new HelpText("The text is encrypted with AES-256-GCM. The key is made from your passphrase with PBKDF2-SHA256 and 200,000 rounds, which makes every guess slow for someone trying passphrases on a copied tag."),
                new HelpText("Because a tag can be copied, the protection is only as good as the passphrase. Several unrelated words are better than one short word with symbols."),
                new HelpText("A wrong passphrase, or a tag whose bytes were changed, simply does not open. It never shows scrambled text.")),
            new HelpMore("How much fits?",
                new HelpText("The encryption and its record take about 90 bytes of the tag. That leaves roughly 54 characters on an NTAG213, 414 on an NTAG215 and 798 on an NTAG216. Letters outside the English alphabet and emoji take 2 to 4 bytes each.")),
            new HelpSeeAlso("nfc-write", "nfc-read"),
        ]),

        new("dumps", amiibo ? "Tag backups and Amiibo" : "Tag backups (dumps)", "tab_nfc.png", "Copy a tag's whole memory and write it back later.",
        [
            new HelpText("A dump is a byte-for-byte copy of a tag's memory. It keeps everything on the tag, including content the app cannot show as text."),
            new HelpList(true,
                "Read the tag on the NFC tab and tap \"Save dump\". It appears in Saved under Tags.",
                "To write it back, open NFC, tap \"Write a tag\" and choose \"Binary dump\".",
                "Pick the dump (\"Choose saved dump\", \"Import .bin file\" or \"Paste hex\") and hold the tag to the phone."),
            new HelpTip("Share a dump as a .bin file to keep a copy outside the phone, for example in your cloud storage."),
            new HelpMore("What gets written",
                new HelpText("Page 3 and every user page of the dump. The serial number (pages 0 to 2) is burned in at the factory and is never changed, and the lock bits are never written, so the tag stays rewritable.")),
            new HelpMore("Tag memory explained",
                new HelpImage("help_tag_memory.png", "Memory of an NTAG tag, in 4-byte pages. Grey: the serial number, fixed at the factory. Orange: lock bits and page 3, which can only be set, never cleared. Green: user memory, where the content goes. Blue: settings and the password, at the end.", 150),
                new HelpText("Page 3 can only ever have bits added. A tag's first real use decides what it is for, which is why some tags cannot go back to holding links."),
                new HelpText("When you read a tag, and in a saved dump, the memory list is a map of the tag: an overview says which pages are writable, writable only with the password, locked for good, one-time, or fixed at the factory, and every page is coloured to match, with the special pages named on their line.")),
            new HelpMore("Making a dump from text",
                new HelpText("In Saved, open the menu of a text note and choose \"Use the text…\", then \"Make a tag dump from it\". That turns the text into tag memory you can write with \"Binary dump\".")),
            .. amiibo
                ? new HelpBlock[]
                {
                    new HelpMore("Amiibo",
                        new HelpText("Amiibo figures hold an NTAG215 tag. A dump of one can be written to a blank NTAG215 sticker or card."),
                        new HelpText("Amiibo data is tied to the serial number of the tag it was made for, so write it back to the same tag it came from. The app sets the Nintendo password from that tag's serial number by itself."),
                        new HelpTip("Writing an Amiibo dump changes the tag for good: it can never be a normal link or text tag again.", Warning: true)),
                }
                : [],
            new HelpSeeAlso("nfc-read", "nfc-write", "saved"),
        ]),

        new("saved", "Saved", "tab_saved.png", "Everything you keep: codes, text notes and tag backups.",
        [
            new HelpText("Use the buttons at the top to show everything, or only codes, text notes or tags. Text notes come from saving the content of a scanned code or an NFC tag as text."),
            new HelpTip("Long-press an item to start selecting, tap more items to add them, then tap Delete to remove them all at once. Cancel, or the back button, stops selecting."),
            new HelpText("The list keeps what you used last at the top: showing, opening, sharing, copying or writing an item moves it up the next time you come back to Saved. Renaming or editing does not."),
            new HelpList(false,
                "Tap a code to show it full screen.",
                "Tap a tag backup to see its memory.",
                "Tap a text note, or the ⋮ button on any row, for its menu."),
            new HelpMore("What is in the menu",
                new HelpList(false,
                    "Open the link, or view the page inside the app.",
                    "Copy the text, rename, or delete.",
                    "\"QR code image…\" or \"Barcode image…\": share the picture or save it to the gallery.",
                    "\"Use the text…\": open it in a generator, write it to an NFC tag, or make a tag dump from a note.",
                    "For tag backups: write it to a tag, share the .bin file, copy its readable text.")),
            new HelpMore("Where is it stored?",
                new HelpText("In the app's private storage on this phone. It is not synced anywhere, and uninstalling the app deletes it. Share important tag backups as .bin files to keep a copy elsewhere.")),
            new HelpSeeAlso("show", "dumps"),
        ]),

        new("previews", "Links and previews", "ic_link.png", "The title, picture and icon shown for web links, and what that sends.",
        [
            new HelpText("Web links you scan, read from a tag or save get a preview card with the page's title, picture and site icon. To make it, the app asks that website for the page, saying honestly that it is Tag Buddy. Nothing goes to the developer or to any other service."),
            new HelpTip("Turn off \"Load link previews automatically\" in Settings and nothing is fetched until you tap \"Tap to load preview\"."),
            new HelpMore("Sign-in and one-time links",
                new HelpText("Links that carry a sign-in code, a token or an action (unsubscribe, confirm, reset a password) are never loaded by the app, because loading them could use up the code or do the action. Only the site's icon is shown.")),
            new HelpMore("Opening links",
                new HelpList(false,
                    "The open button names the app the link will open in, such as \"Open in YouTube\" or \"Open in Chrome\".",
                    "\"View page\" opens the page inside Tag Buddy, which also improves the link's preview.",
                    "In that page viewer, long-press the address at the top to copy it."),
                new HelpText("Which app opens a link is decided by Android. To change it, go to the phone's Settings, Apps, Default apps, Opening links.")),
            new HelpMore("What is stored",
                new HelpText("Previews are kept on the phone for a week, and site icons are shared between links to the same site. \"Clear stored previews and icons\" in Settings removes them all.")),
            new HelpSeeAlso("settings", "scan"),
        ]),

        new("settings", "Settings", "tab_more.png", "What each setting does.",
        [
            new HelpList(false,
                "Vibrate on scan and NFC: a short buzz when a code is read or a tag is read or written.",
                "Beep when a code is scanned: a short beep when the camera reads a code. It stays quiet while the phone is on silent or vibrate.",
                "Android's NFC sound: the sound Android itself plays when the NFC tab finds a tag. Turn it off for quiet reading and writing. A private tag that opens Tag Buddy from outside the app still gets Android's sound.",
                "Try the default tag password: when a protected NTAG/Ultralight is read, try the factory default FFFFFFFF once (only when the tag reports no wrong-attempt lock-out). Off, you get a one-tap button to try it on the next read instead.",
                "Always show codes at maximum brightness: a code shown full screen starts at 100% instead of double your normal brightness.",
                "Load link previews automatically: fetch previews for web links as soon as they appear. Off, nothing is fetched until you tap a preview.",
                "Clear stored previews and icons: forget every stored preview; they load again when needed.",
                "MIFARE Classic keys: \"Forget remembered tag keys\" drops the keys learned per card; \"Clear imported keys\" removes the dictionary you imported on the NFC tab."),
            new HelpSeeAlso("show", "previews"),
        ]),

        new("trouble", "Troubleshooting", "ic_help.png", "Common problems and what to do about them.",
        [
            new HelpMore("The camera shows nothing",
                new HelpText("The app needs permission to use the camera. Open the phone's Settings, Apps, Tag Buddy, Permissions, and allow the camera. Another app using the camera at the same time can also block it.")),
            new HelpMore("A code will not scan",
                new HelpList(false,
                    "Move closer, so the code fills most of the square.",
                    "Hold still for a second, and tap the square to focus.",
                    "Turn on the torch in a dark place; avoid glare from lamps on glossy prints.",
                    "A code on another phone's screen reads better when that screen is bright.",
                    "Very small or damaged prints may not be readable by any phone.")),
            new HelpMore("Nothing happens when I hold a tag",
                new HelpList(false,
                    "Check that NFC is on (the NFC tab tells you).",
                    "Slide the tag slowly over the back of the phone: the antenna is usually above the middle.",
                    "Take the phone out of a thick or metal case.",
                    "Some fobs and old access cards use a different radio (125 kHz) that phones cannot read at all.")),
            new HelpMore("Writing a tag fails",
                new HelpList(false,
                    "Keep the tag still until the phone vibrates; moving it during writing breaks the write. Try again.",
                    "The tag may be locked or have a password (see Writing NFC tags).",
                    "The content may be too big for the tag.")),
            new HelpMore("A link opens in the wrong app",
                new HelpText("Android decides which app opens a link. Change it in the phone's Settings, Apps, Default apps, Opening links, or in the settings of the app that took the link.")),
            new HelpMore("Reporting a problem",
                new HelpText("Open More, then About, and tap \"Report a bug\". The email it prepares already includes the app version and phone model. Say what you did, what happened and what you expected.")),
            new HelpSeeAlso("scan", "nfc-read", "nfc-write"),
        ]),
    ];
}
