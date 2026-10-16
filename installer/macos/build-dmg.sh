#!/usr/bin/env bash
set -euo pipefail

[ "$(uname)" = "Darwin" ] || { echo "this script runs on macOS only" >&2; exit 1; }

RID="${1:-osx-arm64}"
case "$RID" in osx-arm64|osx-x64) ;; *) echo "RID must be osx-arm64 or osx-x64" >&2; exit 1 ;; esac

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="$ROOT/artifacts/installers"
WORK="$ROOT/artifacts/mac-pkg/$RID"
BUNDLE_ID="${MATDOL_BUNDLE_ID:-io.github.tjaykim.matdol}"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/matdol.csproj" | head -n1)"
[ -n "$VERSION" ] || { echo "could not read <Version> from matdol.csproj" >&2; exit 1; }
echo "[matdol] version $VERSION ($RID)"

PUB="$ROOT/artifacts/publish/$RID"
rm -rf "$PUB" "$WORK"; mkdir -p "$WORK" "$OUT"
dotnet publish "$ROOT/matdol.csproj" -c Release -r "$RID" --self-contained -p:PublishSingleFile=true -o "$PUB"

APP="$WORK/matdol.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
(cd "$PUB" && find . -type f ! -name '*.pdb' ! -name 'matdol.png' -exec cp -p {} "$APP/Contents/MacOS/" \;)
chmod 0755 "$APP/Contents/MacOS/matdol"
mkdir -p "$APP/Contents/Resources/legal"
cp "$ROOT/Assets/Legal/LICENSE.txt" "$ROOT/Assets/Legal/Privacy.txt" "$ROOT/Assets/Legal/ThirdPartyNotices.txt" "$APP/Contents/Resources/legal/"

ICONSET="$WORK/matdol.iconset"; mkdir -p "$ICONSET"
I="$ROOT/Assets/Icons"
cp "$I/icon-16.png"  "$ICONSET/icon_16x16.png";   cp "$I/icon-32.png"  "$ICONSET/icon_16x16@2x.png"
cp "$I/icon-32.png"  "$ICONSET/icon_32x32.png";   cp "$I/icon-64.png"  "$ICONSET/icon_32x32@2x.png"
cp "$I/icon-128.png" "$ICONSET/icon_128x128.png"; cp "$I/icon-256.png" "$ICONSET/icon_128x128@2x.png"
cp "$I/icon-256.png" "$ICONSET/icon_256x256.png"; cp "$I/icon-512.png" "$ICONSET/icon_256x256@2x.png"
cp "$I/icon-512.png" "$ICONSET/icon_512x512.png"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/matdol.icns"

sed -e "s/@VERSION@/$VERSION/g" -e "s/@BUNDLE_ID@/$BUNDLE_ID/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist" >/dev/null
printf 'APPL????' > "$APP/Contents/PkgInfo"

ENT="$HERE/matdol.entitlements"
if [ -n "${MATDOL_SIGN_ID:-}" ]; then
    echo "[matdol] signing with Developer ID: $MATDOL_SIGN_ID"
    find "$APP/Contents/MacOS" -type f -name '*.dylib' -exec \
        codesign --force --timestamp --options runtime --sign "$MATDOL_SIGN_ID" {} \;
    codesign --force --timestamp --options runtime --entitlements "$ENT" --sign "$MATDOL_SIGN_ID" "$APP/Contents/MacOS/matdol"
    codesign --force --timestamp --options runtime --entitlements "$ENT" --sign "$MATDOL_SIGN_ID" "$APP"
else
    echo "[matdol] MATDOL_SIGN_ID not set → ad-hoc signature (for testing on this Mac, not for distribution)"
    find "$APP/Contents/MacOS" -type f -name '*.dylib' -exec codesign --force --sign - {} \;
    codesign --force --entitlements "$ENT" --sign - "$APP/Contents/MacOS/matdol"
    codesign --force --entitlements "$ENT" --sign - "$APP"
fi
codesign --verify --strict --verbose=1 "$APP"

DMGSRC="$WORK/dmg"; mkdir -p "$DMGSRC"
cp -R "$APP" "$DMGSRC/"
ln -s /Applications "$DMGSRC/Applications"
DMG="$OUT/matdol-$VERSION-$RID.dmg"
rm -f "$DMG"
hdiutil create -volname "matdol $VERSION" -srcfolder "$DMGSRC" -ov -format UDZO "$DMG" >/dev/null
[ -n "${MATDOL_SIGN_ID:-}" ] && codesign --force --timestamp --sign "$MATDOL_SIGN_ID" "$DMG"

if [ -n "${MATDOL_NOTARY_PROFILE:-}" ]; then
    [ -n "${MATDOL_SIGN_ID:-}" ] || { echo "notarization needs MATDOL_SIGN_ID (Developer ID signature)" >&2; exit 1; }
    echo "[matdol] submitting for notarization (may take a few minutes)"
    xcrun notarytool submit "$DMG" --keychain-profile "$MATDOL_NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG"
    spctl --assess --type open --context context:primary-signature --verbose "$DMG" || true
fi
echo "[matdol] $DMG"
