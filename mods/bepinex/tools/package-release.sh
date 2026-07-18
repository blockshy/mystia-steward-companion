#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$ROOT_DIR/../.." && pwd)"
CONFIGURATION="${1:-Release}"
OUTPUT_DIR="$ROOT_DIR/bin/$CONFIGURATION"
PACKAGE_DIR_NAME="mystia-steward-companion"
DIST_ROOT="$ROOT_DIR/dist"
ZIP_NAME="mystia-steward-companion-bepinex.zip"
COMPANION_EXE_NAME="mystia-steward-companion-companion-windows-x64.exe"
DLL_PATH="$OUTPUT_DIR/MystiaStewardCompanion.BepInEx.dll"
STAGE_ROOT=""
BACKUP_ROOT=""

fail() {
  echo "$*" >&2
  exit 1
}

assert_input_file() {
  local path="$1"
  local description="$2"

  [[ -f "$path" ]] || fail "Missing $description: $path"
  [[ -s "$path" ]] || fail "$description is empty: $path"
}

find_input_file() {
  local description="$1"
  local build_hint="$2"
  shift 2

  local candidate
  for candidate in "$@"; do
    if [[ -f "$candidate" ]]; then
      assert_input_file "$candidate" "$description"
      printf '%s\n' "$candidate"
      return 0
    fi
  done

  fail "Missing $description. $build_hint"
}

assert_managed_swap_path() {
  local path="$1"
  local parent
  local leaf

  parent="$(dirname "$path")"
  leaf="$(basename "$path")"
  [[ "$parent" == "$ROOT_DIR" ]] || fail "Refusing to manage unexpected release path: $path"
  [[ "$leaf" =~ ^dist\.(staging|backup)-[[:alnum:]]+$ ]] || \
    fail "Refusing to manage unexpected release path: $path"
}

remove_managed_swap_directory() {
  local path="$1"
  assert_managed_swap_path "$path"
  if [[ -e "$path" || -L "$path" ]]; then
    rm -rf -- "$path"
  fi
}

copy_validated_file() {
  local source="$1"
  local destination="$2"

  cp -- "$source" "$destination"
  cmp -s "$source" "$destination" || fail "Copied file mismatch: $source -> $destination"
}

cleanup_transaction() {
  local status="$1"
  trap - EXIT INT TERM

  if [[ -n "$STAGE_ROOT" && ( -e "$STAGE_ROOT" || -L "$STAGE_ROOT" ) ]]; then
    remove_managed_swap_directory "$STAGE_ROOT" || true
  fi

  if [[ ! -e "$DIST_ROOT" && ! -L "$DIST_ROOT" && -n "$BACKUP_ROOT" && -d "$BACKUP_ROOT" ]]; then
    mv -- "$BACKUP_ROOT" "$DIST_ROOT" || true
  fi

  exit "$status"
}

handle_exit() {
  cleanup_transaction "$?"
}

handle_interrupt() {
  cleanup_transaction 130
}

handle_terminate() {
  cleanup_transaction 143
}

assert_no_pending_release_transactions() {
  local pending_paths=()

  shopt -s nullglob
  pending_paths=("$ROOT_DIR"/dist.staging-* "$ROOT_DIR"/dist.backup-*)
  shopt -u nullglob
  if (( ${#pending_paths[@]} == 0 )); then
    return
  fi

  echo "A previous release packaging transaction was not completed." >&2
  echo "Inspect and remove or restore these paths before packaging again:" >&2
  printf '  - %s\n' "${pending_paths[@]}" >&2
  exit 1
}

assert_no_pending_release_transactions
assert_input_file "$DLL_PATH" "built Mod DLL"

COMPANION_PATH="$(find_input_file \
  "companion executable" \
  "Run: pnpm tauri:build" \
  "$REPO_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion.exe" \
  "$REPO_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion")"

UPDATER_PATH="$(find_input_file \
  "updater executable" \
  "Run: cargo build --manifest-path apps/companion/src-tauri/Cargo.toml --release --bin mystia-steward-companion-updater" \
  "$REPO_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion-updater.exe" \
  "$REPO_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion-updater")"

ZIP_COMMAND="$(command -v zip || true)"
[[ -n "$ZIP_COMMAND" ]] || fail "zip is required to create the canonical release archive. Install zip and retry."

if [[ -e "$DIST_ROOT" || -L "$DIST_ROOT" ]]; then
  [[ -d "$DIST_ROOT" && ! -L "$DIST_ROOT" ]] || \
    fail "Release dist must be a real directory, not a file or symlink: $DIST_ROOT"
fi

TRANSACTION_ID="${BASHPID}${RANDOM}${RANDOM}"
STAGE_ROOT="$ROOT_DIR/dist.staging-$TRANSACTION_ID"
BACKUP_ROOT="$ROOT_DIR/dist.backup-$TRANSACTION_ID"
assert_managed_swap_path "$STAGE_ROOT"
assert_managed_swap_path "$BACKUP_ROOT"
[[ ! -e "$STAGE_ROOT" && ! -L "$STAGE_ROOT" ]] || fail "Release staging path already exists: $STAGE_ROOT"
[[ ! -e "$BACKUP_ROOT" && ! -L "$BACKUP_ROOT" ]] || fail "Release backup path already exists: $BACKUP_ROOT"
trap handle_exit EXIT
trap handle_interrupt INT
trap handle_terminate TERM
mkdir -- "$STAGE_ROOT"

STAGE_PACKAGE_DIR="$STAGE_ROOT/$PACKAGE_DIR_NAME"
STAGE_ZIP_PATH="$STAGE_ROOT/$ZIP_NAME"
COMPANION_NAME="$(basename "$COMPANION_PATH")"
UPDATER_NAME="$(basename "$UPDATER_PATH")"

mkdir -p "$STAGE_PACKAGE_DIR/companion"
copy_validated_file "$DLL_PATH" "$STAGE_PACKAGE_DIR/$(basename "$DLL_PATH")"
copy_validated_file "$COMPANION_PATH" "$STAGE_PACKAGE_DIR/companion/$COMPANION_NAME"
copy_validated_file "$UPDATER_PATH" "$STAGE_PACKAGE_DIR/$UPDATER_NAME"

HAS_STANDALONE_COMPANION=0
if [[ "$COMPANION_NAME" == *.exe ]]; then
  copy_validated_file "$COMPANION_PATH" "$STAGE_ROOT/$COMPANION_EXE_NAME"
  HAS_STANDALONE_COMPANION=1
fi

(
  cd "$STAGE_ROOT"
  "$ZIP_COMMAND" -qr "$ZIP_NAME" "$PACKAGE_DIR_NAME"
)
[[ -s "$STAGE_ZIP_PATH" ]] || fail "Release archive was not created: $STAGE_ZIP_PATH"
"$ZIP_COMMAND" -T "$STAGE_ZIP_PATH" >/dev/null

if [[ -e "$DIST_ROOT" || -L "$DIST_ROOT" ]]; then
  mv -- "$DIST_ROOT" "$BACKUP_ROOT"
fi

if ! mv -- "$STAGE_ROOT" "$DIST_ROOT"; then
  if [[ ! -e "$DIST_ROOT" && ! -L "$DIST_ROOT" && -d "$BACKUP_ROOT" ]]; then
    mv -- "$BACKUP_ROOT" "$DIST_ROOT"
  fi
  fail "Failed to activate the staged release directory. The previous dist was restored."
fi

if [[ -d "$BACKUP_ROOT" ]]; then
  if remove_managed_swap_directory "$BACKUP_ROOT"; then
    :
  else
    fail "The new release is active, but the previous dist backup could not be removed: $BACKUP_ROOT"
  fi
fi

echo "Included companion executable: $COMPANION_PATH"
echo "Included updater executable: $UPDATER_PATH"
if [[ "$HAS_STANDALONE_COMPANION" == "1" ]]; then
  echo "Companion executable created: $DIST_ROOT/$COMPANION_EXE_NAME"
fi
echo "Package created: $DIST_ROOT/$ZIP_NAME"
