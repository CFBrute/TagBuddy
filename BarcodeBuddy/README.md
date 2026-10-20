# Barcode Buddy

Android-only .NET MAUI app (C#) that generates and scans barcodes.

## Features

- **Generate / Barcode** tab: pick a linear type, type text, and the barcode renders live.
  Types: Code 128, Code 39, Code 93, EAN-13, EAN-8, UPC-A, ITF, Codabar. Input is validated per type.
  Tap the code to open it full screen at maximum brightness for scanning.
- **Everywhere a code is shown** (both Generate tabs, full screen, Favorites menu): icon buttons (long-press for a label): share sends a PNG
  of the code through the Android share sheet, save stores it under Pictures/Barcode Buddy in the
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
Keep the project somewhere short such as `C:\dotnet\BarcodeBuddy`, or redirect the build folders:

```bash
dotnet build -c Debug -p:BaseIntermediateOutputPath=C:/tmp/bb/obj/ -p:BaseOutputPath=C:/tmp/bb/bin/
```

## Release build (signed, for sideloading)

Release builds are signed with a self-signed keystore that lives outside the repo. The project imports
`Signing.local.props` (git-ignored) when it exists; it sets `AndroidKeyStore`, `AndroidSigningKeyStore`,
`AndroidSigningKeyAlias`, `AndroidSigningKeyPass` and `AndroidSigningStorePass`.

```bash
dotnet publish -c Release -f net10.0-android
```

The signed APK lands in `bin/Release/net10.0-android/publish/com.barcodebuddy.app-Signed.apk`.
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
