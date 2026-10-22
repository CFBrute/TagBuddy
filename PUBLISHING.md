# Before publishing on Google Play

Tag Buddy is still a work in progress. This is everything that has to happen before the first Play Store
release, roughly in order. Tick items off as they are done; add new ones as they come up.

## 1. Decisions that cannot be undone

- [ ] **Final app ID.** It is `com.barcodebuddy.app` (from the old name) and can never change after the first
      upload. To make it `…tagbuddy`, change `ApplicationId` in `TagBuddy/TagBuddy.csproj` and the
      `com.barcodebuddy.app` references in `publish.ps1` and `TagBuddy/README.md` *before* uploading. A sideloaded
      install with the old ID would then be a separate app on the phone.
- [ ] **Free or paid.** A free app can never be made paid later (a paid one can become free). Options discussed:
      paid (about $1), or free with a one-time "supporter" or "Pro" in-app purchase. A paid app or in-app purchase
      needs a payments profile in the Play Console.
- [ ] **Keep the upload keystore safe.** Play App Signing holds the final signing key; the keystore in
      `Signing.local.props` becomes the upload key. Back it up and its passwords somewhere outside this machine.

## 2. Placeholders to replace

- [ ] **Support email.** `tagbuddy-support@example.com` is a placeholder (example.com never receives mail).
      Replace it in `BuildInfo.SupportEmail` (`TagBuddy/BuildInfo.cs`) and in the Contact section of
      `docs/privacy.html`. Play also needs a contact email for the store listing.
- [ ] **Privacy policy online.** Repository Settings → Pages → "Deploy from a branch", `main`, folder `/docs`.
      Check that `https://cfbrute.github.io/TagBuddy/privacy.html` opens and matches `BuildInfo.PrivacyPolicyUrl`.
      GitHub Pages needs a public repository or a paid GitHub plan.
- [ ] **Privacy policy date and wording.** Re-read `docs/privacy.html` against the version being released and
      update the "Effective" date. If the repository becomes public, decide on a licence (`LICENSE`) too.

## 3. Test on real phones

Nothing below has been run on a phone yet; the code was only compiled and unit-tested. Try at least one recent
phone and, if possible, an older one (the minimum is Android 7.0, API 24), in light and dark mode.

- [ ] **Generate tabs**: QR and every barcode format, paste, image text (position, size, distance, max width,
      "Same as content"), share, save to gallery, full-screen view and its brightness slider.
- [ ] **Scan tab**: live scanning of QR codes and barcodes (the centre-first decoder has never run on a phone;
      check the README's notes on `ScanPage.CentreFirstDecoding`), torch, tap to focus, "From image".
- [ ] **NFC tab** with real NTAG213/215/216 tags: read, write link/text/vCard, raw bytes, dump save/import/paste/
      write, erase. Confirm a written tag stays rewritable (no lock bits set) and a phone reads NDEF tags.
- [ ] **Link previews**: with automatic previews on and off ("Tap to load preview"), single-use links (no preview),
      page snapshots for pages without a picture, opening a page in the in-app viewer and seeing its card update.
- [ ] **Saved and About tabs**: filters, menus, rename, delete; the preview setting, clearing stored previews,
      "Report a bug" (opens the mail app with build details), "Full privacy policy".
- [ ] **Play Store edition**: build with `publish.ps1 -PlayStore` and check there is no Amiibo wording and that
      Amiibo dumps are refused. Install the bundle through an internal test track or `bundletool`.
- [ ] **Accessibility**: icon buttons read out sensibly with TalkBack.

## 4. Play Console

- [ ] Developer account (one-time fee, identity verification).
- [ ] **Closed test first.** New personal accounts must run a closed test with about 12 testers for 14 days
      before a production release.
- [ ] Upload the `.aab` from `publish.ps1 -PlayStore`. Each upload needs a higher version code; the build uses the
      git commit count, so commit before publishing.
- [ ] Check the current **target API level** requirement and that the build meets it (.NET 10 targets API 36).
- [ ] **Data safety form**: no data collected or shared with the developer, no ads. Read Google's definitions of
      "collected" and "shared" for the link previews (requests go from the phone to the site the person chose)
      and keep the answers consistent with `docs/privacy.html`.
- [ ] **Content rating** questionnaire, **target audience** not children, **ads**: no, **app access**: no login.
- [ ] **Store listing**: name, short description (80 characters), full description, at least two phone
      screenshots, a 512×512 icon and a 1024×500 feature graphic. Do not name or show other brands (Amiibo,
      Instagram, ...). Pitch: "QR codes, barcodes and NFC tags. No ads, no account, no tracking."
- [ ] Privacy policy URL in the listing.

## 5. Worth considering

- [ ] **Android backup.** `allowBackup="true"` lets Android copy saved items into the person's Google backup (the
      privacy policy says so). Keep it, or exclude data with backup rules and update the policy.
- [ ] **Cleartext traffic.** `usesCleartextTraffic="true"` is there so `http://` links can be previewed. It is
      allowed, but a reviewer may ask; it could be limited or dropped.
- [ ] **Page snapshots in the Play edition.** Rendering pages out of sight is legitimate but unusual; it could be
      left out of the Play build with the same switch as Amiibo (`Features.cs`) if a reviewer questions it.
- [ ] **Automated test runs.** A GitHub Action could run `dotnet test` on every push (it needs the .NET 10 SDK and
      the `maui-android` workload).
- [ ] Translations (the app is English only).

## Every release

- [ ] `dotnet test` passes (`TagBuddy.Tests`).
- [ ] Everything committed, then `publish.ps1 -PlayStore -Tag`.
- [ ] Privacy policy still matches what the app does.
