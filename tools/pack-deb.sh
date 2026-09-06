#!/usr/bin/env bash
# Build a system-wide Debian package for the GNOME Shell extension.
# Usage: tools/pack-deb.sh <version> [output-dir]
set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "Usage: $0 <version> [output-dir]" >&2
  exit 2
fi

VERSION=${1#v}
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([~+.-][0-9A-Za-z.+~-]+)?$ ]]; then
  echo "Invalid Debian package version: $VERSION" >&2
  exit 2
fi

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$REPO_ROOT/gnome-extension"
OUT="${2:-$REPO_ROOT/dist}"
UUID=claude-tray@awkto.github.io
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

EXT_DIR="$STAGE/usr/share/gnome-shell/extensions/$UUID"
DOC_DIR="$STAGE/usr/share/doc/claude-tray"
mkdir -p "$EXT_DIR/icons" "$EXT_DIR/schemas" "$DOC_DIR" "$STAGE/DEBIAN" "$OUT"
find "$STAGE" -type d -exec chmod 0755 {} +

install -m 0644 "$SRC/extension.js" "$SRC/prefs.js" "$SRC/metadata.json" \
  "$SRC/stylesheet.css" "$EXT_DIR/"
install -m 0644 "$SRC"/icons/* "$EXT_DIR/icons/"
install -m 0644 "$SRC"/schemas/*.xml "$EXT_DIR/schemas/"
glib-compile-schemas --strict "$EXT_DIR/schemas"
chmod 0644 "$EXT_DIR/schemas/gschemas.compiled"
install -m 0644 "$REPO_ROOT/packaging/deb/README.Debian" "$DOC_DIR/README.Debian"

sed "s/@VERSION@/$VERSION/g" "$REPO_ROOT/packaging/deb/control.in" > "$STAGE/DEBIAN/control"
dpkg-deb --build --root-owner-group "$STAGE" "$OUT/claude-tray_${VERSION}_all.deb"
