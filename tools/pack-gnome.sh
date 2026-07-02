#!/usr/bin/env bash
# Packs the GNOME Shell extension into an installable zip.
# Usage: tools/pack-gnome.sh [output-dir]   (default: dist/)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$REPO_ROOT/gnome-extension"
OUT="${1:-$REPO_ROOT/dist}"
UUID="claude-tray@awkto.github.io"

glib-compile-schemas --strict "$SRC/schemas/"

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)" # absolute — the zip step below runs from another directory
rm -f "$OUT/$UUID.shell-extension.zip"
(cd "$SRC" && python3 -m zipfile -c "$OUT/$UUID.shell-extension.zip" \
    extension.js prefs.js metadata.json stylesheet.css icons schemas)

echo "Packed: $OUT/$UUID.shell-extension.zip"
echo "Install with: gnome-extensions install --force $UUID.shell-extension.zip"
