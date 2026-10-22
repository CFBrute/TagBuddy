# Builds the signed Release APK of Tag Buddy for sideloading and names it after the version stamped
# into it: publish\TagBuddy_1_0_14.apk next to this script. Signing comes from TagBuddy\Signing.local.props
# (git-ignored; see TagBuddy\README.md). With -Tag, also creates and pushes the git tag v1.0.14.
#
# The version is 1.0.<git commit count>, so commit first: publishing with uncommitted changes stamps a
# "*" after the commit in the app and refuses to tag.
param(
    [switch]$Tag
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "TagBuddy\TagBuddy.csproj"
$out = Join-Path $root "publish"

if (-not (Test-Path (Join-Path $root "TagBuddy\Signing.local.props"))) {
    throw "TagBuddy\Signing.local.props is missing; the APK would be unsigned. See TagBuddy\README.md."
}

$dirty = (git -C $root status --porcelain) -ne $null
$count = git -C $root rev-list --count HEAD
$version = "1.0.$count"
if ($dirty) { Write-Warning "Uncommitted changes: the app will show the commit with a '*'." }

dotnet publish $project -c Release -f net10.0-android -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$built = Join-Path $root "TagBuddy\bin\Release\net10.0-android\publish\com.barcodebuddy.app-Signed.apk"
if (-not (Test-Path $built)) { throw "No signed APK at $built" }

New-Item -ItemType Directory -Force $out | Out-Null
$apk = Join-Path $out ("TagBuddy_{0}.apk" -f ($version -replace '\.', '_'))
Copy-Item $built $apk -Force
"Published: $apk ({0:N1} MB)" -f ((Get-Item $apk).Length / 1MB)

if ($Tag) {
    if ($dirty) { throw "Commit first; a tag must point at the build's commit." }
    $name = "v$version"
    if (git -C $root tag --list $name) {
        "Tag $name already exists."
    } else {
        git -C $root tag -a $name -m "Tag Buddy $version"
        git -C $root push origin $name
        "Tagged and pushed $name."
    }
}

"`nInstall on a phone (a Release APK has its own signing key, so a Debug install must be removed first):"
"  adb uninstall com.barcodebuddy.app"
"  adb install `"$apk`""
