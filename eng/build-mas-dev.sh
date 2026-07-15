#!/usr/bin/env bash
# Builds a MAS-flavored Vegha.app signed with a Mac App Development cert +
# Mac App Development provisioning profile, for LOCAL sandboxed testing
# without going through TestFlight.
#
# This mirrors eng/build-mas.sh (same VEGHA_MAS compile flag, same
# entitlements, same 3-pass signing) but swaps the Distribution cert +
# profile for their Development equivalents so macOS will actually launch
# the resulting bundle from /Applications. No .pkg is built.
#
# Prerequisites:
#   1. Apple Development cert in login keychain (Xcode → Settings → Accounts
#      → Manage Certificates → + → Apple Development).
#   2. Mac App Development profile with com.vegha.vegha at
#      ~/apple-dev/Vegha_MAS_Dev.provisionprofile (override with PROFILE=…).
#   3. This Mac's Provisioning UDID registered as a Development device.
#
# Usage:
#   ./eng/build-mas-dev.sh
#   sudo cp -R build/Vegha.app /Applications/
#   open /Applications/Vegha.app

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

PROFILE="${PROFILE:-$HOME/apple-dev/Vegha_MAS_Dev.provisionprofile}"

# Auto-detect the Apple Development identity if APP_CERT wasn't set. The full
# name has a per-user suffix (e.g. "Apple Development: Name (TEAMID)") so we
# match on the prefix.
if [ -z "${APP_CERT:-}" ]; then
  APP_CERT="$(security find-identity -v -p codesigning 2>/dev/null \
    | awk -F'"' '/Apple Development:/ {print $2; exit}')"
fi

if [ -z "$APP_CERT" ]; then
  echo "No 'Apple Development' identity in keychain." >&2
  echo "Create one via Xcode → Settings → Accounts → Manage Certificates → + → Apple Development" >&2
  exit 1
fi
if [ ! -f "$PROFILE" ]; then
  echo "Provisioning profile not found at $PROFILE" >&2
  echo "Download a Mac App Development profile for com.vegha.vegha and place it there." >&2
  exit 1
fi

echo "==> Using cert: $APP_CERT"
echo "==> Using profile: $PROFILE"

echo "==> Restoring + publishing Vegha.App (osx-arm64, self-contained, MAS flavor)"
dotnet restore
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
if [ -f app/Vegha.App/Assets/Vegha.icns ]; then
  cp app/Vegha.App/Assets/Vegha.icns "$APP/Contents/Resources/"
fi

echo "==> Embedding provisioning profile"
cp "$PROFILE" "$APP/Contents/embedded.provisionprofile"

echo "==> Normalizing permissions inside the bundle"
chmod -R go+rX "$APP"

echo "==> Stripping extended attributes"
xattr -cr "$APP"

# Same 3-pass signing as build-mas.sh so we exercise the same code path
# that App Review sees.
echo "==> Pass 1: deep sign all nested code"
codesign --deep --force --options runtime --sign "$APP_CERT" "$APP"

echo "==> Pass 2: re-sign helper executables with sandbox+inherit"
HELPER_ENT="app/Vegha.App/Resources/Vegha.helper.entitlements"
find "$APP/Contents/MacOS" -type f -perm +111 \
  ! -name "Vegha.App" ! -name "*.dylib" ! -name "*.dll" ! -name "*.so" | \
  while read -r f; do
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

echo
echo "Done. Local-testable MAS build at: $APP"
echo
echo "Deploy + launch:"
echo "  sudo rm -rf /Applications/Vegha.app"
echo "  sudo cp -R $APP /Applications/"
echo "  open /Applications/Vegha.app"
