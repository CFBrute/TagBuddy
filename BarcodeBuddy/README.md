# Barcode Buddy

Android-only .NET MAUI app (C#) that generates and scans barcodes.

## Features

- **Generate** tab: pick a barcode type, type text, and the barcode renders live.
  Supported types: Code 128, Code 39, Code 93, EAN-13, EAN-8, UPC-A, ITF, Codabar, QR Code.
  Input is validated per type (e.g. EAN-13 needs 12 or 13 digits) with a hint shown under the picker.
  Tap the barcode to open it full screen at maximum brightness for scanning.
- **Favorites** tab: save generated or scanned barcodes with an optional name.
  Tap to show full screen, swipe left to delete. Stored as JSON in the app's private data folder.
- **Scan** tab: camera scanner (ZXing). A scanned code can be copied, saved to favorites, or opened in the generator.

## Project layout

- `Models/BarcodeTypes.cs` – list of supported formats, hints, and per-format validation.
- `Models/BarcodeItem.cs` – a saved barcode (name, value, format).
- `Services/FavoritesService.cs` – loads/saves favorites to `favorites.json`.
- `Pages/GeneratePage` – text entry + live barcode.
- `Pages/ShowBarcodePage` – full-screen barcode display.
- `Pages/FavoritesPage` – saved list.
- `Pages/ScanPage` – camera scanner.

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
