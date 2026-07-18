#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SOURCE_SCRIPT="$REPO_ROOT/mods/bepinex/tools/package-release.sh"
FIXTURE_ROOT="$(mktemp -d /tmp/mystia-release-package-audit.XXXXXXXX)"

cleanup() {
  rm -rf -- "$FIXTURE_ROOT"
}
trap cleanup EXIT

fail() {
  echo "release package audit failed: $*" >&2
  exit 1
}

assert_file_content() {
  local path="$1"
  local expected="$2"
  [[ -f "$path" ]] || fail "missing file: $path"
  [[ "$(cat "$path")" == "$expected" ]] || fail "unexpected content: $path"
}

create_fixture() {
  local root="$1"

  mkdir -p \
    "$root/mods/bepinex/tools" \
    "$root/mods/bepinex/bin/Release" \
    "$root/apps/companion/src-tauri/target/release" \
    "$root/fake-bin"
  cp "$SOURCE_SCRIPT" "$root/mods/bepinex/tools/package-release.sh"
  chmod +x "$root/mods/bepinex/tools/package-release.sh"

  printf 'mod-dll\n' >"$root/mods/bepinex/bin/Release/MystiaStewardCompanion.BepInEx.dll"
  printf 'companion-exe\n' >"$root/apps/companion/src-tauri/target/release/mystia-steward-companion.exe"
  printf 'updater-exe\n' >"$root/apps/companion/src-tauri/target/release/mystia-steward-companion-updater.exe"

  cat >"$root/fake-bin/zip" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
trap 'exit 130' INT
trap 'exit 143' TERM
if [[ "${1:-}" == "-T" ]]; then
  [[ -s "${2:-}" ]]
  exit
fi
[[ "${1:-}" == "-qr" ]]
archive="$2"
package="$3"
if [[ -n "${FAKE_ZIP_STARTED:-}" ]]; then
  : >"$FAKE_ZIP_STARTED"
fi
if [[ -n "${FAKE_ZIP_DELAY_SECONDS:-}" ]]; then
  sleep "$FAKE_ZIP_DELAY_SECONDS"
fi
if [[ "${FAKE_ZIP_FAIL:-0}" == "1" ]]; then
  exit 9
fi
find "$package" -type f -printf '%P\n' | sort >"$archive"
EOF
  chmod +x "$root/fake-bin/zip"

  cat >"$root/fake-bin/rm" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${FAKE_RM_BACKUP_FAIL:-0}" == "1" ]]; then
  for argument in "$@"; do
    if [[ "$argument" == *'/dist.backup-'* ]]; then
      exit 73
    fi
  done
fi
exec /bin/rm "$@"
EOF
  chmod +x "$root/fake-bin/rm"

  cat >"$root/fake-bin/mv" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
source_path="${@: -2:1}"
destination_path="${@: -1}"
/bin/mv "$@"
if [[ -n "${FAKE_MV_DELAY_AFTER_DIST_BACKUP_SECONDS:-}" && \
      "$(basename "$source_path")" == "dist" && \
      "$(basename "$destination_path")" == dist.backup-* ]]; then
  sleep "$FAKE_MV_DELAY_AFTER_DIST_BACKUP_SECONDS"
fi
EOF
  chmod +x "$root/fake-bin/mv"
}

SUCCESS_ROOT="$FIXTURE_ROOT/success"
create_fixture "$SUCCESS_ROOT"
mkdir -p \
  "$SUCCESS_ROOT/mods/bepinex/dist/mystia-steward-companion-companion-windows-x64" \
  "$SUCCESS_ROOT/mods/bepinex/dist/mystia-steward-companion"
printf 'old-package\n' >"$SUCCESS_ROOT/mods/bepinex/dist/mystia-steward-companion/old.dll"
printf 'old-apk\n' >"$SUCCESS_ROOT/mods/bepinex/dist/mystia-steward-companion-android-arm64-v8a.apk"
printf 'old-manifest\n' >"$SUCCESS_ROOT/mods/bepinex/dist/update-manifest.json"
printf 'old-tar\n' >"$SUCCESS_ROOT/mods/bepinex/dist/mystia-steward-companion-bepinex.tar.gz"
printf 'old-zip\n' >"$SUCCESS_ROOT/mods/bepinex/dist/mystia-steward-companion-companion-windows-x64.zip"

PATH="$SUCCESS_ROOT/fake-bin:$PATH" \
  bash "$SUCCESS_ROOT/mods/bepinex/tools/package-release.sh" >/dev/null

SUCCESS_DIST="$SUCCESS_ROOT/mods/bepinex/dist"
assert_file_content "$SUCCESS_DIST/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll" "mod-dll"
assert_file_content "$SUCCESS_DIST/mystia-steward-companion/companion/mystia-steward-companion.exe" "companion-exe"
assert_file_content "$SUCCESS_DIST/mystia-steward-companion/mystia-steward-companion-updater.exe" "updater-exe"
assert_file_content "$SUCCESS_DIST/mystia-steward-companion-companion-windows-x64.exe" "companion-exe"
[[ -s "$SUCCESS_DIST/mystia-steward-companion-bepinex.zip" ]] || fail "canonical ZIP is missing"
[[ ! -e "$SUCCESS_DIST/mystia-steward-companion-android-arm64-v8a.apk" ]] || fail "stale APK survived"
[[ ! -e "$SUCCESS_DIST/update-manifest.json" ]] || fail "stale update manifest survived"
[[ ! -e "$SUCCESS_DIST/mystia-steward-companion-bepinex.tar.gz" ]] || fail "stale tar archive survived"
[[ ! -e "$SUCCESS_DIST/mystia-steward-companion-companion-windows-x64" ]] || fail "legacy directory survived"
[[ ! -e "$SUCCESS_DIST/mystia-steward-companion-companion-windows-x64.zip" ]] || fail "legacy ZIP survived"
if find "$SUCCESS_ROOT/mods/bepinex" -maxdepth 1 \( -name 'dist.staging-*' -o -name 'dist.backup-*' \) | grep -q .; then
  fail "transaction directory survived successful packaging"
fi

MISSING_INPUT_ROOT="$FIXTURE_ROOT/missing-input"
create_fixture "$MISSING_INPUT_ROOT"
mkdir -p "$MISSING_INPUT_ROOT/mods/bepinex/dist"
printf 'keep-me\n' >"$MISSING_INPUT_ROOT/mods/bepinex/dist/sentinel.txt"
rm "$MISSING_INPUT_ROOT/apps/companion/src-tauri/target/release/mystia-steward-companion-updater.exe"
if PATH="$MISSING_INPUT_ROOT/fake-bin:$PATH" \
  bash "$MISSING_INPUT_ROOT/mods/bepinex/tools/package-release.sh" >/dev/null 2>&1; then
  fail "packaging unexpectedly succeeded with a missing updater"
fi
assert_file_content "$MISSING_INPUT_ROOT/mods/bepinex/dist/sentinel.txt" "keep-me"

PENDING_TRANSACTION_ROOT="$FIXTURE_ROOT/pending-transaction"
create_fixture "$PENDING_TRANSACTION_ROOT"
mkdir -p \
  "$PENDING_TRANSACTION_ROOT/mods/bepinex/dist" \
  "$PENDING_TRANSACTION_ROOT/mods/bepinex/dist.staging-orphan" \
  "$PENDING_TRANSACTION_ROOT/mods/bepinex/dist.backup-orphan"
printf 'keep-me\n' >"$PENDING_TRANSACTION_ROOT/mods/bepinex/dist/sentinel.txt"
if PATH="$PENDING_TRANSACTION_ROOT/fake-bin:$PATH" \
  bash "$PENDING_TRANSACTION_ROOT/mods/bepinex/tools/package-release.sh" >/dev/null 2>&1; then
  fail "packaging unexpectedly continued with orphaned transaction directories"
fi
assert_file_content "$PENDING_TRANSACTION_ROOT/mods/bepinex/dist/sentinel.txt" "keep-me"
[[ -d "$PENDING_TRANSACTION_ROOT/mods/bepinex/dist.staging-orphan" ]] || \
  fail "orphaned staging directory was silently deleted"
[[ -d "$PENDING_TRANSACTION_ROOT/mods/bepinex/dist.backup-orphan" ]] || \
  fail "orphaned backup directory was silently deleted"

ARCHIVE_FAILURE_ROOT="$FIXTURE_ROOT/archive-failure"
create_fixture "$ARCHIVE_FAILURE_ROOT"
mkdir -p "$ARCHIVE_FAILURE_ROOT/mods/bepinex/dist"
printf 'keep-me\n' >"$ARCHIVE_FAILURE_ROOT/mods/bepinex/dist/sentinel.txt"
if PATH="$ARCHIVE_FAILURE_ROOT/fake-bin:$PATH" FAKE_ZIP_FAIL=1 \
  bash "$ARCHIVE_FAILURE_ROOT/mods/bepinex/tools/package-release.sh" >/dev/null 2>&1; then
  fail "packaging unexpectedly succeeded when ZIP creation failed"
fi
assert_file_content "$ARCHIVE_FAILURE_ROOT/mods/bepinex/dist/sentinel.txt" "keep-me"
if find "$ARCHIVE_FAILURE_ROOT/mods/bepinex" -maxdepth 1 \( -name 'dist.staging-*' -o -name 'dist.backup-*' \) | grep -q .; then
  fail "transaction directory survived failed packaging"
fi

BACKUP_FAILURE_ROOT="$FIXTURE_ROOT/backup-failure"
create_fixture "$BACKUP_FAILURE_ROOT"
mkdir -p "$BACKUP_FAILURE_ROOT/mods/bepinex/dist"
printf 'previous-release\n' >"$BACKUP_FAILURE_ROOT/mods/bepinex/dist/sentinel.txt"
if PATH="$BACKUP_FAILURE_ROOT/fake-bin:$PATH" FAKE_RM_BACKUP_FAIL=1 \
  bash "$BACKUP_FAILURE_ROOT/mods/bepinex/tools/package-release.sh" >/dev/null 2>&1; then
  fail "packaging hid a previous-dist cleanup failure"
fi
assert_file_content \
  "$BACKUP_FAILURE_ROOT/mods/bepinex/dist/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll" \
  "mod-dll"
BACKUP_FAILURE_PATH="$(find "$BACKUP_FAILURE_ROOT/mods/bepinex" -maxdepth 1 -type d -name 'dist.backup-*' -print -quit)"
[[ -n "$BACKUP_FAILURE_PATH" ]] || fail "previous dist backup was lost after cleanup failure"
assert_file_content "$BACKUP_FAILURE_PATH/sentinel.txt" "previous-release"

run_signal_audit() {
  local signal_name="$1"
  local expected_status="$2"
  local root="$FIXTURE_ROOT/signal-${signal_name,,}"

  create_fixture "$root"
  mkdir -p "$root/mods/bepinex/dist"
  printf 'previous-release\n' >"$root/mods/bepinex/dist/sentinel.txt"

  local actual_status=0
  set +e
  timeout --foreground --preserve-status --signal="$signal_name" 1 \
    env PATH="$root/fake-bin:$PATH" FAKE_ZIP_DELAY_SECONDS=5 \
    bash "$root/mods/bepinex/tools/package-release.sh" >/dev/null 2>&1
  actual_status=$?
  set -e

  [[ "$actual_status" == "$expected_status" ]] || \
    fail "$signal_name returned $actual_status instead of $expected_status"
  assert_file_content "$root/mods/bepinex/dist/sentinel.txt" "previous-release"
  if find "$root/mods/bepinex" -maxdepth 1 \( -name 'dist.staging-*' -o -name 'dist.backup-*' \) | grep -q .; then
    fail "$signal_name left a transaction directory behind"
  fi
}

command -v timeout >/dev/null || fail "timeout is required for signal transaction audits"
run_signal_audit INT 130
run_signal_audit TERM 143

MOVE_SIGNAL_ROOT="$FIXTURE_ROOT/signal-after-dist-backup"
create_fixture "$MOVE_SIGNAL_ROOT"
mkdir -p "$MOVE_SIGNAL_ROOT/mods/bepinex/dist"
printf 'previous-release\n' >"$MOVE_SIGNAL_ROOT/mods/bepinex/dist/sentinel.txt"
set +e
timeout --foreground --preserve-status --signal=TERM 1 \
  env PATH="$MOVE_SIGNAL_ROOT/fake-bin:$PATH" FAKE_MV_DELAY_AFTER_DIST_BACKUP_SECONDS=5 \
  bash "$MOVE_SIGNAL_ROOT/mods/bepinex/tools/package-release.sh" >/dev/null 2>&1
MOVE_SIGNAL_STATUS=$?
set -e
[[ "$MOVE_SIGNAL_STATUS" == "143" ]] || \
  fail "TERM after dist backup returned $MOVE_SIGNAL_STATUS instead of 143"
assert_file_content "$MOVE_SIGNAL_ROOT/mods/bepinex/dist/sentinel.txt" "previous-release"
if find "$MOVE_SIGNAL_ROOT/mods/bepinex" -maxdepth 1 \( -name 'dist.staging-*' -o -name 'dist.backup-*' \) | grep -q .; then
  fail "TERM after dist backup left a transaction directory behind"
fi

echo "release package transaction audit passed"
