# Tag Buddy

Android-only .NET MAUI app (C#) that generates and scans barcodes and QR codes, and reads and writes NFC tags.

## Features

- **Generate / Barcode** tab: pick a linear type, type text, and the barcode renders live.
  Types: Code 128, Code 39, Code 93, EAN-13, EAN-8, UPC-A, ITF, Codabar. Input is validated per type.
  Tap the code to open it full screen, brighter than the phone's own level (or at maximum, a setting), for scanning.
- **Everywhere a code is shown** (both Generate tabs, full screen, Saved menu): icon buttons (long-press for a label): share sends a PNG
  of the code through the Android share sheet, save stores it under Pictures/Tag Buddy in the
  gallery, and an open-link button appears when the value is a web link, mail address, phone number, SMS or
  map location.
- **Generate / QR Code** tab: multi-line content, live QR with a readout of version, size and byte count.
  "Advanced options" exposes the error-correction level (L/M/Q/H) and the quiet-zone margin.
- **Text under the image** (both Generate tabs): the text printed under the code in shared and saved images.
  It follows the content until edited; position, size, distance and max width are ratios of the code.
- **NFC** tab, Read: hold a tag or card to the phone. Shows its serial number, radio and protocol, and the chip
  behind it, worked out from the small public identifiers any card answers with even when the app cannot read it
  (`Services/CardTypes.cs`): the SAK byte of an ISO 14443-A card (MIFARE Classic, Ultralight/NTAG, Plus, DESFire),
  an ISO 15693 label's manufacturer and NXP ICODE product, a FeliCa system code, and a DESFire's generation and
  size from its public version. It also shows NDEF capacity,
  decodes NDEF records (text, links, MIME, external) and offers the same actions as a scanned code for the content.
  For NTAG/Ultralight tags it also dumps the full readable memory (hex and ASCII). For MIFARE Classic cards it
  tries the public default keys (`Services/MifareClassicKeys.cs`) and shows the sectors that open, hex and ASCII;
  a card secured with custom keys stays closed and shows only its serial. It is read-only and never recovers an
  unknown key, so it opens blank, test and unsecured cards, not secured ones. When some sectors stay locked, a
  key box appears: enter a 6-byte key (12 hex digits) and hold the card again to open the sectors it unlocks.
  Trying a wrong key is safe (Classic cards have no wrong-key lockout), so that box says so; the NTAG/Ultralight
  password box instead warns that a wrong password can lock some tags for good.
- **NFC** tab, Write: type a link, text or vCard (or load one from Saved) and hold a writable tag to write it as NDEF.
  **Private text** encrypts the text with a passphrase first (`Services/SealedText.cs`): PBKDF2-SHA256 with a
  random salt for the key, AES-256-GCM for the text, stored as an NDEF record of type
  `application/vnd.tagbuddy.sealed`. Other phones see nothing readable; Tag Buddy asks for the passphrase when it
  reads the tag, and a wrong passphrase or a changed tag does not open. The passphrase is never stored, so a
  forgotten one means the text is gone. About 90 bytes of the tag go to the record and the encryption, which
  leaves roughly 54 characters on an NTAG213, 414 on an NTAG215 and 798 on an NTAG216.
  Writable NDEF tags only; the serial number is never changed and locked cards cannot be written.
  **Tag password**: a protected tag is first tried with the factory default FFFFFFFF, one well-known guess; when
  that opens it, the read says the tag still has the default password. Only when the default does not open it does
  a password box appear (it is never shown before that). Enter the tag's 4-byte password (8 hex digits, or 4
  characters with the Hex/Text switch) and hold the tag again: the read then reaches the locked pages, or the
  write goes through (`Services/TagPassword.cs`). A password belongs to the tag it was entered for; it is never
  tried on a different tag held to the phone (that tag gets the default), so one tag's password cannot count
  against another's wrong-attempt limit. Each tag's default is tried at most once per run: once it fails on a
  tag it is not tried on that tag again, and a tag read as default is then written with it (the write says so).
  The default is auto-tried only when the tag confirms it has no wrong-attempt lock-out (AUTHLIM 0); when that
  byte cannot be read (a read-protected tag hides it), the default is not tried and the password box appears, so
  an automatic attempt never risks locking a tag. Auto-trying the default can also be turned off on the Settings tab. The password is kept only while the tab is open.
  A wrong one can count against a tag's wrong-attempt limit, so the box warns about that.
  Other write types: raw bytes (no NDEF wrapper), a binary dump (Amiibo or backup), and erase.
  **Edit pages or blocks** (`Services/TagSegments.cs`, `Services/NfcReader.Segments.cs`): after a read, "Edit and
  write to this tag" opens the write side with the tag's pages (Type 2), data blocks (MIFARE Classic, sectors that
  opened) or blocks (ISO 15693) to edit, drawn like the read view (`Controls/SegmentGrid.cs`): one row per segment,
  hex then text, Classic blocks under their sector, rows that are never written greyed with what they hold. Tap any
  byte, in the hex or the text column, to put the cursor there, and type: it works like overwrite mode in a hex
  editor, hex digits a nibble at a time or characters as UTF-8, running on into the next segment; backspace puts a
  byte back as it was. Undo and Redo go back or forward one keystroke at a time; "Page/Block to 00"
  and "All to 00" clear the cursor's segment or every writable one in the range (also undoable). A write that
  reached a tag can be undone too: the writer keeps what each segment held just before it wrote, so "Undo on the
  tag" writes those bytes back (to that same tag only, its serial is checked) and "Redo on the tag" writes the
  change again; the writes of the session are kept newest first, so earlier ones can be undone in turn. The edit expects
  that same tag: its serial and chip are checked when a tag is held to the phone, and anything else is refused with
  nothing written. Only the bytes that were changed are written: a segment is written whole, so its other bytes are
  read from the tag just before and kept, and each segment is read back to confirm. The expectations can also be
  set up by hand (tag type, an optional serial, the range of segments, and for ISO 15693 the bytes per block, which
  the label must confirm); every byte then starts unknown (`··`) and only the bytes typed are written. The serial, page 3, lock bits, password pages,
  Classic block 0 and sector trailers are never written, so an edit can neither lock a tag for good nor change
  how it opens. Classic sectors are opened for writing with the same keys a read tries (entered, defaults,
  remembered), as key A and then key B. A sector the read opened, so its key is known (a default, one entered, imported or
  remembered), can be given a new key from the editor: it is written as key A and key B, the sector's access bits
  kept as they are, and reported only once the sector opens with the new key; the key is then remembered for the
  tag (`MifareKeyStore`), so later reads open it and it can be changed again. A sector no key opened cannot. The serial is only ever compared, never written.
  Protected cards are identified from their public directory only: payment cards by brand (Visa, Mastercard, ...),
  passports and ID documents, MIFARE Classic access cards, and other DESFire-style cards. They are flagged as
  secure items; no cardholder data is requested.
- **Saved** tab: codes with their options, text notes and tag dumps in one list, filtered by kind. Tap a code
  to show it full screen, a dump to see its memory; the menu on each row (`Models/SavedMenu.cs`) offers the rest
  for that kind. Everyday actions are on the first level (open the link, view the page, copy, rename, delete);
  "QR code image…" (share, save to gallery) and "Use the text…" (QR or barcode generator, write to an NFC tag,
  make a dump) open a second menu with Back. Stored as `saved.json` in the app's private data folder, with dump
  bytes as .bin files next to it; older `favorites.json` files are taken over on first start.
- **About** tab: version and build, the link-preview setting, what the app sends over the network, and "Report
  a bug" / "Send feedback", which open an email to `BuildInfo.SupportEmail`. That address is a placeholder
  (`@example.com` never receives mail): set a real one before publishing.
- **Scan** tab: camera scanner (ZXing), plus "From image" to decode a code from a picture on the phone.
  Shows what the code contains (web link, Wi-Fi, contact card, ...),
  its length, and decoder details such as the QR error-correction level, structured-append parts and byte
  segments. Wi-Fi and vCard payloads are broken into fields. A scanned code can be copied, saved, or opened in
  the matching generator.

Codes are rendered by the app itself (`Controls/BarcodeView.cs` draws the module matrix produced by the ZXing
encoder in `Services/BarcodeRenderer.cs`), which is what makes the QR options possible.

## Link previews and site icons

Web links get a preview card (Scan and NFC results, Saved rows). By default it loads on its own as soon as the
link is shown, and saving anything with a link prefetches it. With "Load link previews automatically" switched
off on the Settings tab (`Services/AppSettings.cs`), nothing goes over the network until the card is tapped
("Tap to load preview"); previews and icons already on the phone still show.

Every request identifies itself honestly as `TagBuddy/<version> (Android; link preview)`; the app never poses
as a browser or a crawler. Sources, lightest first (`Services/LinkPreviews.cs`):

1. oEmbed for known sites (`Services/OEmbed.cs`): YouTube, Vimeo, TikTok, X/Twitter, Reddit, Spotify,
   SoundCloud, Flickr, Dailymotion and Bluesky. Posts without a thumbnail (X, Bluesky) show their text.
2. The page's head: Open Graph, Twitter cards, `image_src`, title and description, plus the oEmbed link the page
   advertises. Instagram, Facebook and Threads show such a request a login wall, so their links usually get
   just the site's icon.
3. A rendered picture of the page (`Services/PageSnapshot.cs`), the heavy last resort. The web view is attached
   behind the app's content so the page counts as visible, uses a phone-width viewport, loads lazy images at
   once, waits until the page and its web fonts stop changing, and hides cookie banners and other overlays in
   its private copy before drawing (nothing is clicked or accepted; if hiding leaves the page empty, they come
   back). A picture with (almost) nothing on it is never kept. The rendered page is also read by script for
   its title, description, preview picture and icons, which pages built by script only have after rendering;
   its own preview picture then beats the snapshot. Bot-check pages ("Just a moment...") are recognised and
   never kept.

When a load finds no picture but does learn something (a title, the site icon), that is kept and shown, and
the full preview is tried again after a few hours. When a link redirects to another domain, the icon is kept
for the address the link was known by too.

Opening a link in the in-app viewer (`Pages/WebPage`) teaches the app: once the page has loaded, its title,
description, preview picture and icons (or a picture of the top of the page as the person sees it) become the
link's preview when nothing better is stored, and cards on screen update (`LinkPreviews.PreviewUpdated`).
Only the page the link led to counts. For single-use links only the icon is kept, since their pages can show
personal things.

Known sites (`Services/KnownSites.cs`) are recognised from the address alone: a card says "YouTube · Video" or
"Short link (bit.ly)" before anything loads or with previews off, and a site's other addresses (`youtu.be`,
`redd.it`, `wa.me`, `amzn.to`, `m.`/`mobile.`/`amp.` hosts) share one stored icon. Short links never take on
their destination's icon.

Single-use links (sign-in codes, tokens, unsubscribe/confirm/reset paths) are never requested; only their
site's icon is shown.

Site icons (`Services/Favicons.cs`) are kept once per site in `cache/link-previews-v2/icons/` as PNG files of at
most 128 px and reused for every link to that site. Only the site itself is asked, never a third-party icon
service. Order, stopping at the first icon of 64 px or more: the icons the page declares (apple-touch-icon first,
SVG skipped), `/apple-touch-icon.png` and `/favicon.ico`, the icons the site's homepage declares (when the page
itself was not read, as for single-use links), and the parent domain's well-known files (`m.example.com` →
`example.com`; country suffixes such as `co.uk` are never treated as a site). A site without an icon of its
own borrows its parent site's stored one (`en.wikipedia.org` from `wikipedia.org`). Without an icon the blue
link glyph is shown. "Clear stored previews and icons" on the Settings tab empties the cache.

Previews are cached for 7 days; one without a real picture is retried after 6 hours (`RetryAfter`).

## Scanner

The Scan tab shows a viewfinder: the picture is dimmed around a centre square where the code should be held.
The corners turn amber when something code-like is seen and flash green on a read; hints change after 6 and 12
seconds without a result, and tapping the square focuses. Live frames are decoded without TryHarder (pictures
from "From image" still use it).

Centre-first decoding (`ScanPage.CentreFirstDecoding`, `Services/FrameDecoder.cs`) decodes the centre square of
most camera frames and the whole picture at half size every fourth frame. It could not be tested on a phone when
it was written: when the frames are unusable or never arrive it switches back to the library's own detection by
itself, and setting `CentreFirstDecoding = false` turns it off for good. With the library's detection, QR codes
cannot show the amber "seen" state, because the library reports nothing before a successful read.

## NFC tags: what can and cannot be written

The NFC tab works with Type 2 tags (NTAG213/215/216, Ultralight EV1): 4-byte pages, a factory serial in pages
0-2, the capability container (CC) in page 3, user memory from page 4, then lock and configuration pages. Three
things about them decide what the app can do, and they are documented at the top of `Services/NfcReader.cs`:

- **Lock bits are one-time and only set on purpose.** Writing data never locks anything; a tag rewritten a
  hundred times stays unlocked. An Amiibo writer, though, copies Nintendo's lock bytes, which make pages 3,
  13-15 and 16-31 read-only for the life of the tag. This app never sets a lock bit. It decodes them and
  writes around them, and its result lists the pages it had to leave alone.
- **Page 3 is one-time on its own.** NDEF formatting writes `E1 10 ...`; an Amiibo image writes `F1 10 FF EE`.
  Once a tag has had the Amiibo value, phones will never read it as a text or link tag again, so a tag's first
  write decides what it is for. "Raw bytes (no NDEF)" stores text in the user pages anyway; this app reads it
  back, phones ignore it.
- **Passwords.** Amiibo tags use a key that is a fixed formula of the serial, which the app knows. Other
  password-protected tags are never guessed at, because wrong guesses count towards a limit that locks the tag
  for good.

## Project layout

- `Models/BarcodeTypes.cs` - supported formats, hints and per-format validation.
- `Models/SavedItem.cs` - a saved code, text note or tag dump (with QR and image-text options).
- `Pages/CodeGeneratorPage.cs` - what the QR and Barcode tabs share: live preview, paste, save/share/open.
- `Pages/QrPage`, `Pages/GeneratePage` - the QR and linear barcode tabs.
- `Pages/CaptionSection` - the "text under the image" section of both generators.
- `Pages/ShowBarcodePage` - full-screen display with a brightness slider.
- `Pages/SavedPage`, `Pages/DumpPage` - the Saved list, and one saved tag dump.
- `Pages/ScanPage` - camera scanner with details; `Pages/WebPage` - in-app view of a link.
- `Pages/NfcPage` - NFC tab; `Pages/SettingsPage` and `Pages/AboutPage` - the Settings and About top tabs under More (settings; build, privacy and feedback).
- `Pages/AppNavigation.cs` - hands content from one tab to another (generators, NFC writing).
- `Pages/Palette.cs`, `Pages/DetailRows.cs`, `Pages/IconButtons.cs` - small UI helpers shared by pages.
- `Services/BarcodeRenderer.cs` - text to module matrix via the ZXing encoder.
- `Services/BarcodeImage.cs` - renders a code to PNG, shares it, saves it to the gallery.
- `Services/ScanDetails.cs` - explains scanned content and decoder metadata.
- `Services/ScanResult.cs` - one result type for camera scans and picture decodes.
- `Services/FrameDecoder.cs`, `Services/GrayDecoder.cs`, `Services/ImageDecoder.cs` - centre-first live decoding,
  and finding a code in a picked photo.
- `Services/NfcReader*.cs` - NFC reader mode (Android), split by concern: lifecycle (`NfcReader.cs`), reading
  (`.Read.cs`), Type 2 page access and passwords (`.Type2.cs`), writing (`.Write.cs`), segment edits (`.Segments.cs`).
- `Services/TagSegments.cs` - the segment editor: which pages or blocks each tag type may write, the hex and text
  views, what changed, and the checks that the tag held to the phone is the one expected.
- `Services/Type2Dump.cs` - checks on a dump's bytes (size, serial, chip label) before it goes near a tag.
- `Services/SavedService.cs` - loads/saves everything in Saved; `DumpText` finds the readable text in a dump.
- `Services/KnownSites.cs` - well-known sites, their other addresses, and link shorteners.
- `Services/AppSettings.cs` - the person's settings (automatic link previews, vibration, beep, full-screen brightness).
- `Services/Feedback.cs` - the vibration and beep after a scan or an NFC read or write, as set on the Settings tab.
- `Services/Links.cs` - detects openable values (web, mail, tel, sms, geo).
- `Services/CodeActions.cs` - the shared share / save / open / copy actions and save prompts.
- `Services/LinkPreviews.cs`, `OEmbed.cs`, `HtmlHead.cs`, `PageSnapshot.cs`, `Favicons.cs` - link previews and site icons.
- `Controls/BarcodeView.cs` - draws a module matrix as bars or squares.
- `Controls/PreviewView.cs`, `LinkPreviewView.cs`, `SiteIconView.cs`, `ViewfinderDrawable.cs` - the other drawn views.

## Building

Requirements: .NET 10 SDK with the `maui-android` workload and an Android SDK (API 35+).

```bash
dotnet build -c Debug
```

The signed debug APK ends up in `bin/Debug/net10.0-android/com.barcodebuddy.app-Signed.apk`.
The project sets `EmbedAssembliesIntoApk=true`, so even a Debug APK is self-contained and can be sideloaded.
Without it, a Debug build relies on Visual Studio "Fast Deployment" and aborts at startup when installed by hand.
Install it on a phone with USB debugging enabled:

```bash
adb install -r bin/Debug/net10.0-android/com.barcodebuddy.app-Signed.apk
```

### Note on long paths (only if you move the project)

The Android toolchain fails with `PathTooLongException` if the project sits in a deeply nested folder.
Keep the project somewhere short such as `C:\dotnet\TagBuddy`, or redirect the build folders:

```bash
dotnet build -c Debug -p:BaseIntermediateOutputPath=C:/tmp/bb/obj/ -p:BaseOutputPath=C:/tmp/bb/bin/
```

## Release build (signed, for sideloading)

Release builds are signed with a self-signed keystore that lives outside the repo. The project imports
`Signing.local.props` (git-ignored) when it exists; it sets `AndroidKeyStore`, `AndroidSigningKeyStore`,
`AndroidSigningKeyAlias`, `AndroidSigningKeyPass` and `AndroidSigningStorePass`.

```powershell
..\publish.ps1          # signed APK as ..\publish\TagBuddy_1_0_<n>.apk
..\publish.ps1 -Tag     # also creates and pushes the git tag v1.0.<n>
```

Or by hand, `dotnet publish -c Release -f net10.0-android`; the signed APK then lands in
`bin/Release/net10.0-android/publish/com.barcodebuddy.app-Signed.apk`.

## Tests

`TagBuddy.Tests` holds unit tests for the logic where a mistake does real harm: Type 2 tag memory (lock bits,
NDEF TLV, where raw text goes, the Amiibo key), dump checks and hex parsing, barcode validation and encoding,
what counts as a link and which links are single-use, known sites and icon addresses, reading previews from
HTML, and judging snapshot pictures. Run them from the repository root:

```bash
dotnet test TagBuddy.Tests
```

The app targets Android only, which a test runner cannot load, so the test project compiles the app's
`Services`, `Models`, `BuildInfo.cs` and `Features.cs` for plain .NET instead. Code inside `#if ANDROID` drops
out there; that is why the byte-level tag rules live in `Services/Type2Memory.cs`, apart from the radio code in
`NfcReader`. Keep new logic that can be tested out of Android-only code and add tests for it. Screens, the
camera and real tags are tested by hand (see `../PUBLISHING.md`).

## Privacy policy

`docs/privacy.html` is the privacy policy Google Play asks for, with a small `docs/index.html` next to it.
Publish it with GitHub Pages: repository Settings → Pages → "Deploy from a branch", branch `main`, folder
`/docs`. It then lives at `https://cfbrute.github.io/TagBuddy/privacy.html` (`BuildInfo.PrivacyPolicyUrl`, opened
from the About tab); use the same address in the Play Console. The page loads nothing from other sites. Keep it
in step with the app: a change in what the app sends or stores needs a new date and text there. Its contact
address is the same placeholder as `BuildInfo.SupportEmail`.

## Editions: sideload and Play Store

One switch, `-p:PlayStore=true`, builds the Play Store edition. It defines `PLAY_STORE`, which `Features.cs`
turns into flags, and produces an app bundle (.aab), the only format Play accepts:

```powershell
..\publish.ps1 -PlayStore    # signed bundle as ..\publish\TagBuddy_1_0_<n>_play.aab
```

The Play Store edition leaves out what Google Play's policies object to:

- **Amiibo support.** Nintendo-protected tags are treated like any other password-protected tag (the app never
  guesses a password), Amiibo dumps are refused for writing, and the app does not name Amiibo content.
  Reading tags, plain NTAG backups, raw text and erasing unprotected tags work as before.

The sideload build (the default) keeps it. The About tab names the edition under the version, so a bug report
says which build it came from.

## Versions

The version is `1.0.<git commit count>`: the first two numbers are `VersionPrefix` in the project file,
bumped by hand; the third is filled in at build time by the `StampBuildVersion` target, which also uses the
count as the Android `versionCode` and stamps the short commit (with `*` when there were uncommitted
changes) and the build time into the assembly. `BuildInfo` reads them and the About tab shows them, which is
what to quote in a bug report; "Report a bug" there puts them into the email by itself. Releases are tagged `v1.0.<n>`.
A Release APK and a Debug APK use different signing keys, so switching between them on a phone needs an
uninstall first (`adb uninstall com.barcodebuddy.app`).

To create a new keystore (keytool ships with the JDK installed by the Android workload):

```bash
keytool -genkeypair -v -keystore barcodebuddy.keystore -alias barcodebuddy -keyalg RSA -keysize 2048 -validity 10000
```

Keep the keystore and its password safe: the same key must sign every future update, including a Play Store release.

## Dependencies

- [ZXing.Net.Maui.Controls](https://github.com/Redth/ZXing.Net.Maui) 0.10.4 – barcode generation view and camera reader view.

## App icon, splash and theme

The launcher icon is hand-drawn SVG: `Resources/AppIcon/appiconfg.svg` (black Code 128 bars plus a QR finder
square with an orange centre) over `Resources/AppIcon/appicon.svg` (plain white background). Android crops and
masks adaptive icons, so the artwork sits inside the central safe zone.

The splash screen is a custom dark Android theme (`Platforms/Android/Resources/values*/styles.xml` and the
`drawable/splash_*.xml` files): near-black background, the same mark in white, and a thin orange stripe.

Colours live in `Resources/Styles/Colors.xaml` (golden orange `#F5A623` as primary) and
`Platforms/Android/Resources/values/colors.xml` for the status bar.

`design/` keeps the original icon artwork that was used before the SVG version, plus the script that turned it
into adaptive-icon layers, in case a bitmap icon is wanted again.
