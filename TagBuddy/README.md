# Tag Buddy

Android-only .NET MAUI app (C#) that generates and scans barcodes.

## Features

- **Generate / Barcode** tab: pick a linear type, type text, and the barcode renders live.
  Types: Code 128, Code 39, Code 93, EAN-13, EAN-8, UPC-A, ITF, Codabar. Input is validated per type.
  Tap the code to open it full screen at maximum brightness for scanning.
- **Everywhere a code is shown** (both Generate tabs, full screen, Favorites menu): icon buttons (long-press for a label): share sends a PNG
  of the code through the Android share sheet, save stores it under Pictures/Tag Buddy in the
  gallery, and an open-link button appears when the value is a web link, mail address, phone number, SMS or
  map location.
- **Generate / QR Code** tab: multi-line content, live QR with a readout of version, size and byte count.
  "Advanced options" exposes the error-correction level (L/M/Q/H) and the quiet-zone margin.
- **NFC** tab, Read: hold a tag or card to the phone. Shows its serial number, radio/chip type and NDEF capacity,
  decodes NDEF records (text, links, MIME, external) and offers the same actions as a scanned code for the content.
  For NTAG/Ultralight tags it also dumps the full readable memory (hex and ASCII).
- **NFC** tab, Write: type a link, text or vCard (or load a favorite) and hold a writable tag to write it as NDEF.
  Writable NDEF tags only; the serial number is never changed and locked cards cannot be written.
  Protected cards are identified from their public directory only: payment cards by brand (Visa, Mastercard, ...),
  passports and ID documents, MIFARE Classic access cards, and other DESFire-style cards. They are flagged as
  secure items; no cardholder data is requested.
- **Favorites** tab: saved codes with their options. Tap to show full screen; the menu on each row offers
  Open link (for links), Share image, Save image, "Use text in Barcode generator", "Use text in QR generator",
  "Copy text" and "Delete". Stored as JSON in the
  app's private data folder.
- **Scan** tab: camera scanner (ZXing), plus "From image" to decode a code from a picture on the phone.
  Shows what the code contains (web link, Wi-Fi, contact card, ...),
  its length, and decoder details such as the QR error-correction level, structured-append parts and byte
  segments. Wi-Fi and vCard payloads are broken into fields. A scanned code can be copied, saved, or opened in
  the matching generator.

Codes are rendered by the app itself (`Controls/BarcodeView.cs` draws the module matrix produced by the ZXing
encoder in `Services/BarcodeRenderer.cs`), which is what makes the QR options possible.

## Link previews and site icons

Web links get a preview card (Scan and NFC results, Saved rows). It loads on its own as soon as the link is
shown, and saving anything with a link prefetches it. Sources, lightest first (`Services/LinkPreviews.cs`):

1. oEmbed for known sites (`Services/OEmbed.cs`): YouTube, Vimeo, TikTok, X/Twitter, Reddit, Spotify,
   SoundCloud, Flickr, Dailymotion and Bluesky. Posts without a thumbnail (X, Bluesky) show their text.
2. The page's head: Open Graph, Twitter cards, `image_src`, title and description, plus the oEmbed link the page
   advertises. Instagram, Facebook and Threads are requested with the Facebook crawler's user agent
   (`facebookexternalhit`), because they show crawlers their preview tags and everyone else a login wall.
   This is unofficial and may stop working.
3. A rendered picture of the page (`Services/PageSnapshot.cs`), the heavy last resort. The web view is attached
   behind the app's content so the page counts as visible, uses a phone-width viewport, waits until the page
   stops changing, and a picture with (almost) nothing on it is never kept.

Single-use links (sign-in codes, tokens, unsubscribe/confirm/reset paths) are never requested; only their
site's icon is shown.

Site icons (`Services/Favicons.cs`) are kept once per site in `cache/link-previews-v2/icons/` as PNG files of at
most 128 px and reused for every link to that site. Order: apple-touch-icon, the largest declared icon (SVG
skipped), `/apple-touch-icon.png`, `/favicon.ico`, and Google's favicon service as a last resort
(`Favicons.UseGoogleFallback`). Without an icon the blue link glyph is shown.

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
- `Models/BarcodeItem.cs` - a saved code (name, value, format, QR options).
- `Services/BarcodeRenderer.cs` - text to module matrix via the ZXing encoder.
- `Services/ScanDetails.cs` - explains scanned content and decoder metadata.
- `Services/ScanResult.cs` - one result type for camera scans and picture decodes.
- `Services/ImageDecoder.cs` - finds a code inside a picked photo.
- `Services/NfcReader.cs` - NFC reader mode and NDEF decoding (Android).
- `Pages/NfcPage` - NFC tab.
- `Services/BarcodeImage.cs` - renders a code to PNG, shares it, saves it to the gallery.
- `Services/Links.cs` - detects openable values (web, mail, tel, sms, geo).
- `Services/CodeActions.cs` - the shared share / save / open / copy actions.
- `Services/FavoritesService.cs` - loads/saves favorites to `favorites.json`.
- `Controls/BarcodeView.cs` - draws a module matrix as bars or squares.
- `Pages/GeneratePage` - linear barcode tab.
- `Pages/QrPage` - QR tab with advanced options.
- `Pages/ShowBarcodePage` - full-screen display.
- `Pages/FavoritesPage` - saved list with per-item actions.
- `Pages/ScanPage` - camera scanner with details.

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

## Versions

The version is `1.0.<git commit count>`: the first two numbers are `VersionPrefix` in the project file,
bumped by hand; the third is filled in at build time by the `StampBuildVersion` target, which also uses the
count as the Android `versionCode` and stamps the short commit (with `*` when there were uncommitted
changes) and the build time into the assembly. `BuildInfo` reads them and the Saved tab shows them at the
bottom of the list, which is what to quote in a bug report. Releases are tagged `v1.0.<n>`.
A Release APK and a Debug APK use different signing keys, so switching between them on a phone needs an
uninstall first (`adb uninstall com.barcodebuddy.app`).

To create a new keystore (keytool ships with the JDK installed by the Android workload):

```bash
keytool -genkeypair -v -keystore barcodebuddy.keystore -alias barcodebuddy -keyalg RSA -keysize 2048 -validity 10000
```

Keep the keystore and its password safe: the same key must sign every future update, including a Play Store release.

## Dependencies

- [ZXing.Net.Maui.Controls](https://github.com/Redth/ZXing.Net.Maui) 0.4.0 – barcode generation view and camera reader view.

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
