#!/usr/bin/env bash
# 历史命令入口转交新的完整 Flutter 树事务夹具，避免继续维护五文件旧包测试。
set -euo pipefail
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec pwsh -NoLogo -NoProfile -File "$SCRIPT_DIR/../csharp-business-package/package-transaction.ps1"
