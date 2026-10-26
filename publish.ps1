# Builds the signed Release APK of Tag Buddy for sideloading and names it after the version stamped
# into it: publish\TagBuddy_1_1_3.apk next to this script. Signing comes from TagBuddy\Signing.local.props
# (git-ignored; see TagBuddy\README.md). With -Tag, also creates and pushes the git tag v1.1.3.
#
# The version is major.(commit count / 100).(commit count % 100), e.g. 1.1.3 at 103 commits (see $major and
# $minorOffset below, kept in step with TagBuddy.csproj). Commit first: publishing with uncommitted changes
# stamps a "*" after the commit in the app and refuses to tag.
#
# With -PlayStore, builds the Play Store edition instead (no Amiibo support; see TagBuddy\Features.cs) as a
# signed app bundle, publish\TagBuddy_1_1_3_play.aab, to upload in the Play Console.
param(
    [switch]$Tag,
    [switch]$PlayStore
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "TagBuddy\TagBuddy.csproj"
$out = Join-Path $root "publish"

if (-not (Test-Path (Join-Path $root "TagBuddy\Signing.local.props"))) {
    throw "TagBuddy\Signing.local.props is missing; the APK would be unsigned. See TagBuddy\README.md."
}

$dirty = (git -C $root status --porcelain) -ne $null
$count = [int](git -C $root rev-list --count HEAD)
# Version = major.(count/100).(count%100), e.g. 1.1.3 at 103 commits. Keep $major/$minorOffset in step with
# MajorVersion/MinorOffset in TagBuddy.csproj, which stamps the version into the app itself.
$major = 1
$minorOffset = 0
$version = "$major.$($minorOffset + [math]::Floor($count / 100)).$($count % 100)"
if ($dirty) { Write-Warning "Uncommitted changes: the app will show the commit with a '*'." }

$extension = if ($PlayStore) { "aab" } else { "apk" }
$suffix = if ($PlayStore) { "_play" } else { "" }

dotnet publish $project -c Release -f net10.0-android -v q -nologo "-p:PlayStore=$($PlayStore.IsPresent.ToString().ToLower())"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$built = Join-Path $root "TagBuddy\bin\Release\net10.0-android\publish\com.barcodebuddy.app-Signed.$extension"
if (-not (Test-Path $built)) { throw "No signed .$extension at $built" }

New-Item -ItemType Directory -Force $out | Out-Null
$apk = Join-Path $out ("TagBuddy_{0}{1}.{2}" -f ($version -replace '\.', '_'), $suffix, $extension)
Copy-Item $built $apk -Force
"Published: $apk ({0:N1} MB)" -f ((Get-Item $apk).Length / 1MB)

if ($Tag) {
    if ($dirty) { throw "Commit first; a tag must point at the build's commit." }
    $name = "v$version"
    if (git -C $root tag --list $name) {
        "Tag $name already exists."
    } else {
        git -C $root tag -a $name -m "Tag Buddy $version"
        # git reports progress on stderr, which Windows PowerShell 5.1 would otherwise treat as an error.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        cmd /c "git -C `"$root`" push origin $name 2>&1" | Out-Null
        $pushed = $LASTEXITCODE -eq 0
        $ErrorActionPreference = $previous
        if (-not $pushed) { throw "git push of $name failed with exit code $LASTEXITCODE" }
        "Tagged and pushed $name."
    }
}

if ($PlayStore) {
    "`nUpload the .aab in the Play Console. To try it on a phone first, use bundletool or an internal test track."
    exit 0
}

"`nInstall on a phone (a Release APK has its own signing key, so a Debug install must be removed first):"
"  adb uninstall com.barcodebuddy.app"
"  adb install `"$apk`""
