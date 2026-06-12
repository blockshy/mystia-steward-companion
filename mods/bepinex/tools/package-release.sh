#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$ROOT_DIR/../.." && pwd)"
CONFIGURATION="${1:-Release}"
OUTPUT_DIR="$ROOT_DIR/bin/$CONFIGURATION"
PACKAGE_DIR_NAME="mystia-steward-companion"
DIST_DIR="$ROOT_DIR/dist/$PACKAGE_DIR_NAME"
ZIP_PATH="$ROOT_DIR/dist/mystia-steward-companion-bepinex.zip"
TAR_PATH="$ROOT_DIR/dist/mystia-steward-companion-bepinex.tar.gz"
DLL_PATH="$OUTPUT_DIR/MystiaStewardCompanion.BepInEx.dll"

if [[ ! -f "$DLL_PATH" ]]; then
  echo "Missing built DLL: $DLL_PATH" >&2
  echo "Run: dotnet build $ROOT_DIR/MystiaStewardCompanion.BepInEx.csproj -c $CONFIGURATION" >&2
  exit 1
fi

rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"
cp "$DLL_PATH" "$DIST_DIR/"
cp -R "$ROOT_DIR/Data" "$DIST_DIR/Data"

for companion_path in \
  "$REPO_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion.exe" \
  "$REPO_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion"; do
  if [[ -f "$companion_path" ]]; then
    mkdir -p "$DIST_DIR/companion"
    cp "$companion_path" "$DIST_DIR/companion/$(basename "$companion_path")"
    echo "Included companion executable: $companion_path"
    break
  fi
done

rm -f "$ZIP_PATH" "$TAR_PATH"
if command -v zip >/dev/null 2>&1; then
  (
    cd "$ROOT_DIR/dist"
    zip -qr "$(basename "$ZIP_PATH")" "$PACKAGE_DIR_NAME"
  )
  echo "Package created: $ZIP_PATH"
else
  (
    cd "$ROOT_DIR/dist"
    tar -czf "$(basename "$TAR_PATH")" "$PACKAGE_DIR_NAME"
  )
  echo "Package created: $TAR_PATH"
fi
