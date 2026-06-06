#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

export COREPACK_HOME="${COREPACK_HOME:-/tmp/corepack}"
corepack pnpm dev -- --host 127.0.0.1
