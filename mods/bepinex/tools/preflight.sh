#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FAILED=0

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
  dotnet --version
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
  echo "Preflight failed. See References/README.md and README.md for setup steps."
  exit 1
fi

echo
echo "Preflight passed."
