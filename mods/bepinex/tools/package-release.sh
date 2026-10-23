#!/usr/bin/env bash
# 只转交唯一 PowerShell 打包实现，避免另一套五文件/旧客户端规则继续进入正式产物。
set -euo pipefail
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec pwsh -NoLogo -NoProfile -File "$SCRIPT_DIR/package-release.ps1" "$@"
