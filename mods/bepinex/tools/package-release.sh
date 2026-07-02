#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$ROOT_DIR/../.." && pwd)"
CONFIGURATION="${1:-Release}"
OUTPUT_DIR="$ROOT_DIR/bin/$CONFIGURATION"
PACKAGE_DIR_NAME="mystia-steward-companion"
COMPANION_PACKAGE_DIR_NAME="mystia-steward-companion-companion-windows-x64"
DIST_DIR="$ROOT_DIR/dist/$PACKAGE_DIR_NAME"
COMPANION_DIST_DIR="$ROOT_DIR/dist/$COMPANION_PACKAGE_DIR_NAME"
ZIP_PATH="$ROOT_DIR/dist/mystia-steward-companion-bepinex.zip"
TAR_PATH="$ROOT_DIR/dist/mystia-steward-companion-bepinex.tar.gz"
COMPANION_ZIP_PATH="$ROOT_DIR/dist/$COMPANION_PACKAGE_DIR_NAME.zip"
COMPANION_TAR_PATH="$ROOT_DIR/dist/$COMPANION_PACKAGE_DIR_NAME.tar.gz"
DLL_PATH="$OUTPUT_DIR/MystiaStewardCompanion.BepInEx.dll"

if [[ ! -f "$DLL_PATH" ]]; then
  echo "Missing built DLL: $DLL_PATH" >&2
  echo "Run: dotnet build $ROOT_DIR/MystiaStewardCompanion.BepInEx.csproj -c $CONFIGURATION" >&2
  exit 1
fi

rm -rf "$DIST_DIR"
rm -rf "$COMPANION_DIST_DIR"
mkdir -p "$DIST_DIR"
cp "$DLL_PATH" "$DIST_DIR/"

for companion_path in \
  "$REPO_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion.exe" \
  "$REPO_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion"; do
  if [[ -f "$companion_path" ]]; then
    mkdir -p "$DIST_DIR/companion"
    cp "$companion_path" "$DIST_DIR/companion/$(basename "$companion_path")"
    echo "Included companion executable: $companion_path"
    if [[ "$(basename "$companion_path")" == *.exe ]]; then
      mkdir -p "$COMPANION_DIST_DIR"
      cp "$companion_path" "$COMPANION_DIST_DIR/$(basename "$companion_path")"
      cat >"$COMPANION_DIST_DIR/README-remote-connection.txt" <<'EOF'
mystia-steward-companion companion window

This package is only the Windows x64 companion window for a second device.
It is not a BepInEx Mod installer.

Typical LAN setup:
1. On device A, install BepInEx #783 and mystia-steward-companion-bepinex.zip, then start the game.
2. On device A, open the companion window, go to Settings -> Connection, and enable LAN access.
3. Copy the LAN address and Token from device A.
4. On device B, run mystia-steward-companion.exe from this package.
5. Enter the LAN address and Token from device A, then click Connect.

Only use this on a trusted LAN. Do not expose the local API through public port forwarding.
EOF
    fi
    break
  fi
done

updater_included=0
for updater_path in \
  "$REPO_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion-updater.exe" \
  "$REPO_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion-updater"; do
  if [[ -f "$updater_path" ]]; then
    cp "$updater_path" "$DIST_DIR/$(basename "$updater_path")"
    echo "Included updater executable: $updater_path"
    updater_included=1
    break
  fi
done

if [[ "$updater_included" != "1" ]]; then
  echo "Missing updater executable. Run: cargo build --manifest-path apps/companion/src-tauri/Cargo.toml --release --bin mystia-steward-companion-updater" >&2
  exit 1
fi

rm -f "$ZIP_PATH" "$TAR_PATH" "$COMPANION_ZIP_PATH" "$COMPANION_TAR_PATH"
if command -v zip >/dev/null 2>&1; then
  (
    cd "$ROOT_DIR/dist"
    zip -qr "$(basename "$ZIP_PATH")" "$PACKAGE_DIR_NAME"
  )
  echo "Package created: $ZIP_PATH"
  if [[ -d "$COMPANION_DIST_DIR" ]]; then
    (
      cd "$ROOT_DIR/dist"
      zip -qr "$(basename "$COMPANION_ZIP_PATH")" "$COMPANION_PACKAGE_DIR_NAME"
    )
    echo "Companion package created: $COMPANION_ZIP_PATH"
  fi
else
  (
    cd "$ROOT_DIR/dist"
    tar -czf "$(basename "$TAR_PATH")" "$PACKAGE_DIR_NAME"
  )
  echo "Package created: $TAR_PATH"
  if [[ -d "$COMPANION_DIST_DIR" ]]; then
    (
      cd "$ROOT_DIR/dist"
      tar -czf "$(basename "$COMPANION_TAR_PATH")" "$COMPANION_PACKAGE_DIR_NAME"
    )
    echo "Companion package created: $COMPANION_TAR_PATH"
  fi
fi
