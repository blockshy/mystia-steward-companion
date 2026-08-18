#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$ROOT_DIR/../.." && pwd)"
EXPECTED_DOTNET_SDK="$(sed -nE 's/^[[:space:]]*"dotnetSdk":[[:space:]]*"([^"]+)".*/\1/p' "$REPO_ROOT/toolchain.lock.json")"
REFERENCE_VERIFIER="$REPO_ROOT/scripts/restore-build-references.mjs"
REFERENCE_DIR="$ROOT_DIR/References"
FAILED=0

while (( $# > 0 )); do
  case "$1" in
    --reference-dir)
      [[ $# -ge 2 && -n "$2" ]] || {
        echo "Missing value for --reference-dir" >&2
        exit 2
      }
      REFERENCE_DIR="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      echo "Usage: bash mods/bepinex/tools/preflight.sh [--reference-dir <directory>]" >&2
      exit 2
      ;;
  esac
done

if [[ -z "$EXPECTED_DOTNET_SDK" ]]; then
  echo "MISS toolchain.lock.json dotnetSdk"
  exit 1
fi

echo "Checking .NET SDK"
if command -v dotnet >/dev/null 2>&1; then
  if ACTUAL_DOTNET_SDK="$(cd "$REPO_ROOT" && dotnet --version)" \
    && [[ "$ACTUAL_DOTNET_SDK" == "$EXPECTED_DOTNET_SDK" ]]; then
    echo "OK   dotnet $ACTUAL_DOTNET_SDK"
  else
    echo "MISMATCH dotnet expected=$EXPECTED_DOTNET_SDK actual=${ACTUAL_DOTNET_SDK:-<unavailable>}"
    FAILED=1
  fi
else
  echo "MISS dotnet"
  FAILED=1
fi

echo
echo "Checking build references: $REFERENCE_DIR"
if ! command -v node >/dev/null 2>&1; then
  echo "MISS node (required for strict reference verification)"
  FAILED=1
elif [[ ! -f "$REFERENCE_VERIFIER" ]]; then
  echo "MISS $REFERENCE_VERIFIER"
  FAILED=1
elif ! node "$REFERENCE_VERIFIER" --verify --output "$REFERENCE_DIR"; then
  FAILED=1
fi

if [[ "$FAILED" -ne 0 ]]; then
  echo
  echo "Preflight failed. Install the locked .NET SDK and restore the exact bundle from References/references.lock.json."
  exit 1
fi

echo
echo "Preflight passed."
