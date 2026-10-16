#!/bin/sh
set -e

usage() {
    cat <<EOF
usage: ./install.sh [--prefix DIR] [--no-desktop]
  --prefix DIR   install under DIR instead of ~/.local  (app goes to DIR/share/matdol)
  --no-desktop   do not put a shortcut on the desktop
EOF
}

PREFIX="${MATDOL_PREFIX:-$HOME/.local}"
DESKTOP_SHORTCUT=1
while [ $# -gt 0 ]; do
    case "$1" in
        --prefix) [ $# -ge 2 ] || { usage; exit 1; }; PREFIX="$2"; shift 2 ;;
        --prefix=*) PREFIX="${1#--prefix=}"; shift ;;
        --no-desktop) DESKTOP_SHORTCUT=0; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

SRC="$(cd "$(dirname "$0")" && pwd)"
[ -x "$SRC/app/matdol" ] || { echo "matdol: run install.sh from the extracted package folder" >&2; exit 1; }

APPDIR="$PREFIX/share/matdol"
BINDIR="$PREFIX/bin"
APPS="$PREFIX/share/applications"
ICONS="$PREFIX/share/icons"

if command -v pgrep >/dev/null 2>&1 && pgrep -u "$(id -u)" -x matdol >/dev/null 2>&1; then
    echo "note: matdol is running — restart it after the installation to use the new version."
fi

echo "installing matdol to $APPDIR"
mkdir -p "$PREFIX/share"
rm -rf "$APPDIR.new"
cp -R "$SRC/app" "$APPDIR.new"
chmod 0755 "$APPDIR.new/matdol" "$APPDIR.new/uninstall.sh"
rm -rf "$APPDIR"
mv "$APPDIR.new" "$APPDIR"
printf 'PREFIX=%s\nDESKTOP_FILE=\n' "$PREFIX" > "$APPDIR/.install-info"

for d in "$SRC"/icons/hicolor/*/apps; do
    size="$(basename "$(dirname "$d")")"
    mkdir -p "$ICONS/hicolor/$size/apps"
    cp "$d/matdol.png" "$ICONS/hicolor/$size/apps/matdol.png"
done
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$ICONS/hicolor" 2>/dev/null || true

mkdir -p "$APPS"
sed -e "s#@APPDIR@#$APPDIR#g" -e "s#@ICON@#$ICONS/hicolor/256x256/apps/matdol.png#g" \
    "$APPDIR/matdol.desktop.in" > "$APPS/matdol.desktop"
chmod 0644 "$APPS/matdol.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$APPS" 2>/dev/null || true

mkdir -p "$BINDIR"
if [ -d "$BINDIR/matdol" ] && [ ! -L "$BINDIR/matdol" ]; then
    echo "note: $BINDIR/matdol is a folder from an earlier manual install — left untouched."
    echo "      You can delete it once the new installation works:  rm -r \"$BINDIR/matdol\""
else
    ln -sfn "$APPDIR/matdol" "$BINDIR/matdol"
fi

if [ "$DESKTOP_SHORTCUT" = 1 ]; then
    DESK="$(xdg-user-dir DESKTOP 2>/dev/null || true)"
    [ -n "$DESK" ] && [ "$DESK" != "$HOME" ] || DESK="$HOME/Desktop"
    if [ -d "$DESK" ]; then
        cp "$APPS/matdol.desktop" "$DESK/matdol.desktop"
        chmod 0755 "$DESK/matdol.desktop"
        command -v gio >/dev/null 2>&1 && gio set "$DESK/matdol.desktop" metadata::trusted true 2>/dev/null || true
        printf 'PREFIX=%s\nDESKTOP_FILE=%s\n' "$PREFIX" "$DESK/matdol.desktop" > "$APPDIR/.install-info"
        echo "desktop shortcut: $DESK/matdol.desktop"
    fi
fi

echo "done. Start matdol from the application menu, the desktop shortcut, or: $APPDIR/matdol"
echo "uninstall: $APPDIR/uninstall.sh"
