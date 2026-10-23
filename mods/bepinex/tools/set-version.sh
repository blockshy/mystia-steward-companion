#!/usr/bin/env bash
# 两个参数分别是产品版本和显式递增的 Android build number。
set -euo pipefail
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec pwsh -NoLogo -NoProfile -File "$SCRIPT_DIR/set-version.ps1" "$@"
