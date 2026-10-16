#!/bin/sh
set -e
APPDIR="$(cd "$(dirname "$0")" && pwd)"
[ -x "$APPDIR/matdol" ] && [ -f "$APPDIR/.install-info" ] || { echo "matdol: this does not look like a matdol installation: $APPDIR" >&2; exit 1; }

PREFIX=""; DESKTOP_FILE=""
. "$APPDIR/.install-info"
[ -n "$PREFIX" ] || PREFIX="$(cd "$APPDIR/../.." && pwd)"

echo "removing matdol from $APPDIR"
[ -L "$PREFIX/bin/matdol" ] && [ "$(readlink "$PREFIX/bin/matdol")" = "$APPDIR/matdol" ] && rm -f "$PREFIX/bin/matdol"
rm -f "$PREFIX/share/applications/matdol.desktop"
[ -n "$DESKTOP_FILE" ] && [ -f "$DESKTOP_FILE" ] && rm -f "$DESKTOP_FILE"
for f in "$PREFIX"/share/icons/hicolor/*/apps/matdol.png; do [ -f "$f" ] && rm -f "$f"; done
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$PREFIX/share/applications" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$PREFIX/share/icons/hicolor" 2>/dev/null || true

cd /
rm -rf "$APPDIR"
echo "done. (settings in ~/.config/matdol were kept — delete that folder too if you like)"
