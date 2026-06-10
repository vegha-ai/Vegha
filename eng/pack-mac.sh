#!/usr/bin/env bash
# Builds a Developer ID-signed, notarized macOS .app + .dmg for Vegha.
#
# Prereqs (one-time on the build machine):
#   1. macOS with Xcode command-line tools (codesign, hdiutil, sips, iconutil, xcrun)
#   2. "Developer ID Application" cert in the login keychain:
#        security find-identity -v -p codesigning
#   3. notarytool credential profile stored in keychain:
#        xcrun notarytool store-credentials vegha-notary \
#          --apple-id <your-apple-id> --team-id 2RAQC96997 --password <app-specific-pw>
#
# Examples:
#   ./eng/pack-mac.sh --version 0.1.0
#   ./eng/pack-mac.sh --version 0.1.0 --skip-notarize     # local dev / smoke test
#
# Output: releases/osx-arm64/Vegha-<Version>-osx-arm64.dmg  (notarized + stapled)

set -euo pipefail

VERSION=""
CONFIGURATION="Release"
RUNTIME="osx-arm64"
BUNDLE_ID="ai.vegha.app"
BUNDLE_NAME="Vegha"
SIGNING_IDENTITY="Developer ID Application: VAMC Consulting LLC (2RAQC96997)"
NOTARY_PROFILE="vegha-notary"
SKIP_NOTARIZE=0
SKIP_RESTORE=0

usage() {
    cat <<EOF
Usage: $0 --version X.Y.Z [options]
  --version VER           (required) version string, e.g. 0.1.0
  --configuration CFG     Release (default) | Debug
  --runtime RID           osx-arm64 (default)
  --bundle-id ID          ai.vegha.app
  --bundle-name NAME      Vegha
  --signing-identity STR  substring match for security find-identity
  --notary-profile NAME   keychain profile from notarytool store-credentials
  --skip-notarize         build & sign only; skip notarytool + stapler
  --skip-restore          skip dotnet restore
  -h, --help              show this help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)          VERSION="$2"; shift 2;;
        --configuration)    CONFIGURATION="$2"; shift 2;;
        --runtime)          RUNTIME="$2"; shift 2;;
        --bundle-id)        BUNDLE_ID="$2"; shift 2;;
        --bundle-name)      BUNDLE_NAME="$2"; shift 2;;
        --signing-identity) SIGNING_IDENTITY="$2"; shift 2;;
        --notary-profile)   NOTARY_PROFILE="$2"; shift 2;;
        --skip-notarize)    SKIP_NOTARIZE=1; shift;;
        --skip-restore)     SKIP_RESTORE=1; shift;;
        -h|--help)          usage; exit 0;;
        *) echo "Unknown arg: $1" >&2; usage; exit 1;;
    esac
done

[[ -n "$VERSION" ]] || { echo "Error: --version required" >&2; exit 1; }
[[ "$(uname)" == "Darwin" ]] || { echo "Error: pack-mac.sh must run on macOS" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_DIR="$REPO_ROOT/app/Vegha.App"
PROJECT_PATH="$PROJECT_DIR/Vegha.App.csproj"
PUBLISH_DIR="$REPO_ROOT/publish/$RUNTIME"
OUTPUT_DIR="$REPO_ROOT/releases/$RUNTIME"
APP_BUNDLE="$OUTPUT_DIR/$BUNDLE_NAME.app"
DMG_PATH="$OUTPUT_DIR/$BUNDLE_NAME-$VERSION-$RUNTIME.dmg"
ENTITLEMENTS_PATH="$OUTPUT_DIR/Vegha.entitlements"

[[ -f "$PROJECT_PATH" ]] || { echo "Project not found: $PROJECT_PATH" >&2; exit 1; }
mkdir -p "$OUTPUT_DIR"

# ----------------------------------------------------------------------------
# 1. dotnet publish (self-contained, arm64)
# ----------------------------------------------------------------------------
if [[ $SKIP_RESTORE -eq 0 ]]; then
    echo "Restoring NuGet packages..."
    dotnet restore "$PROJECT_PATH"
fi

echo "Publishing $RUNTIME self-contained to $PUBLISH_DIR..."
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT_PATH" \
    --configuration "$CONFIGURATION" \
    --runtime "$RUNTIME" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishReadyToRun=true \
    --output "$PUBLISH_DIR"

# ----------------------------------------------------------------------------
# 2. Lay out Vegha.app bundle
# ----------------------------------------------------------------------------
echo "Assembling $APP_BUNDLE ..."
rm -rf "$APP_BUNDLE"
CONTENTS_DIR="$APP_BUNDLE/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

cp -R "$PUBLISH_DIR"/* "$MACOS_DIR/"
chmod +x "$MACOS_DIR/Vegha.App"

# Move data directories out of MacOS/ into Resources/ where they belong.
# codesign applies strict code-object rules to everything in MacOS/ and rejects
# unsignable data files (.bru, .json, etc). Resources/ is for app data.
for datadir in samples; do
    [[ -d "$MACOS_DIR/$datadir" ]] && mv "$MACOS_DIR/$datadir" "$RESOURCES_DIR/$datadir"
done

# ----------------------------------------------------------------------------
# 3. Generate Vegha.icns from Assets/logo.png
# ----------------------------------------------------------------------------
SOURCE_PNG="$PROJECT_DIR/Assets/logo.png"
ICNS_PATH="$RESOURCES_DIR/Vegha.icns"
if [[ -f "$SOURCE_PNG" ]]; then
    echo "Generating Vegha.icns from Assets/logo.png ..."
    ICONSET="$OUTPUT_DIR/Vegha.iconset"
    rm -rf "$ICONSET"
    mkdir -p "$ICONSET"
    for s in 16 32 64 128 256 512 1024; do
        sips -z "$s" "$s" "$SOURCE_PNG" --out "$ICONSET/icon_${s}x${s}.png" > /dev/null
        if (( s <= 512 )); then
            d=$((s * 2))
            sips -z "$d" "$d" "$SOURCE_PNG" --out "$ICONSET/icon_${s}x${s}@2x.png" > /dev/null
        fi
    done
    iconutil -c icns "$ICONSET" -o "$ICNS_PATH"
    rm -rf "$ICONSET"
else
    echo "Warning: no logo.png at $SOURCE_PNG — bundle will use default icon." >&2
fi

# ----------------------------------------------------------------------------
# 4. Write Info.plist
# ----------------------------------------------------------------------------
cat > "$CONTENTS_DIR/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>            <string>$BUNDLE_NAME</string>
    <key>CFBundleDisplayName</key>     <string>$BUNDLE_NAME</string>
    <key>CFBundleIdentifier</key>      <string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key>         <string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleExecutable</key>      <string>Vegha.App</string>
    <key>CFBundleIconFile</key>        <string>Vegha</string>
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleSignature</key>       <string>????</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>LSMinimumSystemVersion</key>  <string>11.0</string>
    <key>NSHighResolutionCapable</key> <true/>
    <key>NSPrincipalClass</key>        <string>NSApplication</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
</dict>
</plist>
EOF

# ----------------------------------------------------------------------------
# 5. Write entitlements for hardened runtime + .NET requirements
# ----------------------------------------------------------------------------
# Canonical entitlements for a self-contained .NET app on macOS with hardened
# runtime. Required for notarization to succeed.
cat > "$ENTITLEMENTS_PATH" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>com.apple.security.cs.allow-jit</key>                       <true/>
    <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
    <key>com.apple.security.cs.disable-library-validation</key>      <true/>
    <key>com.apple.security.cs.allow-dyld-environment-variables</key><true/>
</dict>
</plist>
EOF

# ----------------------------------------------------------------------------
# 6. Codesign — inside-out, every binary, then the bundle
# ----------------------------------------------------------------------------
echo "Signing bundle with '$SIGNING_IDENTITY' ..."

cs() {
    # cs <target> [extra codesign args...]
    # Skips if already signed by our identity (idempotent re-runs).
    local target="$1"; shift
    if codesign -dvv "$target" 2>&1 | grep -q "TeamIdentifier=2RAQC96997"; then
        return 0
    fi
    codesign --force --timestamp --options runtime --sign "$SIGNING_IDENTITY" "$@" "$target"
}

# Strip debug symbols — .pdb files are not signable and have no place in a
# release bundle. codesign treats any file referenced by the apphost as a
# nested code object and fails if it cannot sign it.
find "$MACOS_DIR" -name "*.pdb" -delete

# Sign every nested native binary first (bottom-up).
# With PublishSingleFile=true, managed .dll assemblies and runtimeconfig.json
# are embedded inside the Vegha.App binary — only .dylib/.so files remain
# as separate signable objects.
while IFS= read -r -d '' f; do
    cs "$f"
done < <(find "$MACOS_DIR" -type f \( -name "*.dylib" -o -name "*.so" \) -print0)

# Sign the main apphost executable.
cs "$MACOS_DIR/Vegha.App" --entitlements "$ENTITLEMENTS_PATH"

# Finally seal the whole bundle.
cs "$APP_BUNDLE" --entitlements "$ENTITLEMENTS_PATH"

echo "Verifying signature..."
codesign --verify --deep --strict --verbose=2 "$APP_BUNDLE"

# ----------------------------------------------------------------------------
# 7. Build .dmg
# ----------------------------------------------------------------------------
echo "Creating $DMG_PATH ..."
rm -f "$DMG_PATH"
# Stage the volume contents: the app plus an /Applications symlink, so Finder
# shows the conventional "drag to Applications" install layout.
DMG_STAGE="$OUTPUT_DIR/dmg-stage"
rm -rf "$DMG_STAGE"
mkdir -p "$DMG_STAGE"
cp -R "$APP_BUNDLE" "$DMG_STAGE/"
ln -s /Applications "$DMG_STAGE/Applications"
hdiutil create -volname "$BUNDLE_NAME" -srcfolder "$DMG_STAGE" -ov -format UDZO "$DMG_PATH" > /dev/null
rm -rf "$DMG_STAGE"
cs "$DMG_PATH"

# ----------------------------------------------------------------------------
# 8. Notarize + staple
# ----------------------------------------------------------------------------
if [[ $SKIP_NOTARIZE -eq 1 ]]; then
    echo "Warning: skipping notarization (--skip-notarize). DMG will trigger Gatekeeper warnings on other machines." >&2
else
    echo "Submitting $DMG_PATH to notarytool (profile: $NOTARY_PROFILE) ..."
    xcrun notarytool submit "$DMG_PATH" --keychain-profile "$NOTARY_PROFILE" --wait
    echo "Stapling ticket to DMG..."
    xcrun stapler staple "$DMG_PATH"
fi

echo ""
echo "Done. Artifacts in $OUTPUT_DIR"
ls -lh "$OUTPUT_DIR"
