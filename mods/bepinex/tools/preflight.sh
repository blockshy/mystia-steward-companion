#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$ROOT_DIR/../.." && pwd)"
EXPECTED_DOTNET_SDK="$(sed -nE 's/^[[:space:]]*"dotnetSdk":[[:space:]]*"([^"]+)".*/\1/p' "$REPO_ROOT/toolchain.lock.json")"
FAILED=0

if [[ -z "$EXPECTED_DOTNET_SDK" ]]; then
  echo "MISS toolchain.lock.json dotnetSdk"
  exit 1
fi

check_file() {
  local path="$1"
  if [[ -f "$path" ]]; then
    echo "OK   $path"
  else
    echo "MISS $path"
    FAILED=1
  fi
}

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
echo "Checking build references"
check_file "$ROOT_DIR/References/BepInEx.Core.dll"
check_file "$ROOT_DIR/References/BepInEx.Unity.IL2CPP.dll"
check_file "$ROOT_DIR/References/0Harmony.dll"
check_file "$ROOT_DIR/References/Il2CppInterop.Runtime.dll"
check_file "$ROOT_DIR/References/Il2Cppmscorlib.dll"
check_file "$ROOT_DIR/References/UnityEngine.CoreModule.dll"
check_file "$ROOT_DIR/References/UnityEngine.InputLegacyModule.dll"

if [[ "$FAILED" -ne 0 ]]; then
  echo
  echo "Preflight failed. Install the locked .NET SDK and see References/README.md for reference setup."
  exit 1
fi

echo
echo "Preflight passed."
