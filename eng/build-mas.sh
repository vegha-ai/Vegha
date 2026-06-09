#!/usr/bin/env bash
# Builds and signs a Mac App Store .pkg for Vegha, ready to upload to
# App Store Connect (TestFlight or production).
#
# Prerequisites (one-time setup):
#   1. Mac App Distribution + Mac Installer Distribution certs in keychain.
#      Verify with: security find-identity -v -p basic
#   2. Mac App Store provisioning profile at ~/apple-dev/Vegha_MAS.provisionprofile
#      (override with PROFILE=/some/path).
#   3. .NET 10 SDK installed (`dotnet --list-sdks` should show 10.x).
#
# Usage:
#   ./eng/build-mas.sh 0.1.0
#
# Output:
#   build/Vegha-<version>.pkg   (signed, ready to upload)

set -euo pipefail

VERSION="${1:-}"
if [ -z "$VERSION" ]; then
  echo "Usage: $0 <version>  (e.g. 0.1.0)" >&2
  exit 1
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

PROFILE="${PROFILE:-$HOME/apple-dev/Vegha_MAS.provisionprofile}"
APP_CERT="${APP_CERT:-3rd Party Mac Developer Application: VAMC Consulting LLC (2RAQC96997)}"
PKG_CERT="${PKG_CERT:-3rd Party Mac Developer Installer: VAMC Consulting LLC (2RAQC96997)}"

if [ ! -f "$PROFILE" ]; then
  echo "Provisioning profile not found at $PROFILE" >&2
  echo "Download it from Apple Developer portal and place it there, or set PROFILE=/your/path" >&2
  exit 1
fi

echo "==> Patching Info.plist with version $VERSION"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" \
  app/Vegha.App/Resources/Info.plist
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" \
  app/Vegha.App/Resources/Info.plist

echo "==> Restoring + publishing Vegha.App (osx-arm64, self-contained, MAS flavor)"
dotnet restore
# VeghaFlavor=MAS makes Directory.Build.props define the VEGHA_MAS compile symbol
# (same mechanism Pack-Msix.ps1 uses for MSIX). This keeps the flavor guards in
# Program.cs (Velopack disabled) and AboutDialog (shows "Mac App Store") in sync —
# both read the all-caps VEGHA_MAS. Do NOT pass -p:DefineConstants here: that
# overrides the property wholesale and defined a mismatched mixed-case symbol.
dotnet publish app/Vegha.App/Vegha.App.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -p:VeghaFlavor=MAS \
  -p:PublishReadyToRun=true \
  -o publish/osx-arm64

echo "==> Assembling Vegha.app bundle"
APP="build/Vegha.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R publish/osx-arm64/* "$APP/Contents/MacOS/"
cp app/Vegha.App/Resources/Info.plist "$APP/Contents/"
# Mac App Store requires an .icns with a 512pt@2x (1024x1024) image; CFBundleIconFile
# in Info.plist points to this file by basename ("Vegha"), no extension.
if [ -f app/Vegha.App/Assets/Vegha.icns ]; then
  cp app/Vegha.App/Assets/Vegha.icns "$APP/Contents/Resources/"
fi

echo "==> Embedding provisioning profile"
cp "$PROFILE" "$APP/Contents/embedded.provisionprofile"

# Mac App Store rejects packages with files only readable by root — every file inside
# the .app must be world-readable so non-admin users can verify the code signature at
# launch. Capital X on chmod adds execute only where it already exists (preserves the
# main binary's +x, dylibs, etc.) while adding read for group/other everywhere.
echo "==> Normalizing permissions inside the bundle"
chmod -R go+rX "$APP"

# Files downloaded via a browser (notably the provisioning profile) carry the
# com.apple.quarantine xattr. The App Store rejects any file with extended
# attributes, so strip them recursively before signing.
echo "==> Stripping extended attributes"
xattr -cr "$APP"

# Three-pass signing.
#   1. --deep, no entitlements: seals every nested code object (dylibs, .so,
#      managed .dll, helper executables — codesign handles all formats).
#   2. Helper Mach-O executables (createdump etc.) get re-signed with helper
#      entitlements: sandbox + inherit only. Sandbox is required on every
#      nested executable; inherit makes them adopt the parent app's sandbox
#      container at launch. We can't include application-identifier here
#      because loose binaries can't have a provisioning profile, and
#      TestFlight rejects identity entitlements without a matching profile.
#   3. The outer bundle's main executable gets full entitlements: sandbox,
#      application-identifier, team-identifier.
echo "==> Pass 1: deep sign all nested code"
codesign --deep --force --options runtime --sign "$APP_CERT" "$APP"

echo "==> Pass 2: re-sign helper executables with sandbox+inherit"
HELPER_ENT="app/Vegha.App/Resources/Vegha.helper.entitlements"
find "$APP/Contents/MacOS" -type f -perm +111 \
  ! -name "Vegha.App" ! -name "*.dylib" ! -name "*.dll" ! -name "*.so" | \
  while read -r f; do
    # Only re-sign actual Mach-O executables, skip data files that happen
    # to have the executable bit set.
    if file "$f" | grep -q "Mach-O.*executable"; then
      codesign --force --options runtime \
        --entitlements "$HELPER_ENT" \
        --sign "$APP_CERT" "$f"
    fi
  done

echo "==> Pass 3: re-sign outer bundle with full entitlements"
codesign --force --options runtime \
  --entitlements app/Vegha.App/Resources/Vegha.entitlements \
  --sign "$APP_CERT" \
  "$APP"

echo "==> Verifying signature"
codesign --verify --deep --strict --verbose=2 "$APP"

PKG="build/Vegha-${VERSION}.pkg"
echo "==> Building signed .pkg with Mac Installer Distribution cert"
productbuild --component "$APP" /Applications \
  --sign "$PKG_CERT" \
  "$PKG"

echo
echo "Done. Signed pkg: $PKG"
echo
echo "Upload to App Store Connect with one of:"
echo "  1. Transporter app (Mac App Store) — drag $PKG into it, click Deliver"
echo "  2. xcrun altool --upload-app -f $PKG -t macos \\"
echo "       -u <your-appleid> -p '@keychain:AC_PASSWORD'"
echo "     (store the app-specific password first via:"
echo "      xcrun altool --store-password-in-keychain-item AC_PASSWORD \\"
echo "                   -u <appleid> -p <app-specific-password>)"
