#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: bash mods/bepinex/tools/set-version.sh <version>"
  echo "Example: bash mods/bepinex/tools/set-version.sh 1.0.3"
  exit 2
fi

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT_DIR=$(cd -- "$SCRIPT_DIR/.." && pwd)
REPO_ROOT=$(cd -- "$ROOT_DIR/../.." && pwd)
VERSION=${1#v}

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]]; then
  echo "Invalid version: $1. Expected SemVer like 1.0.3 or v1.0.3." >&2
  exit 2
fi

if [[ -n "${NODE:-}" ]]; then
  NODE_CMD=$NODE
elif command -v node >/dev/null 2>&1; then
  NODE_CMD=$(command -v node)
elif [[ -x /huyu/environment/.nvm/versions/node/v22.22.2/bin/node ]]; then
  NODE_CMD=/huyu/environment/.nvm/versions/node/v22.22.2/bin/node
else
  echo "node was not found. Install Node.js or set NODE=/path/to/node." >&2
  exit 1
fi

"$NODE_CMD" - "$VERSION" "$REPO_ROOT" <<'NODE'
const fs = require('fs');
const path = require('path');

const [version, repoRoot] = process.argv.slice(2);

function updateFirstMatch(relativePath, pattern) {
  const absolutePath = path.join(repoRoot, relativePath);
  const content = fs.readFileSync(absolutePath, 'utf8');
  let matched = false;
  const updated = content.replace(pattern, (...args) => {
    matched = true;
    return `${args[1]}${version}${args[2]}`;
  });

  if (!matched) {
    throw new Error(`Version pattern not found in ${absolutePath}`);
  }

  if (updated !== content) {
    fs.writeFileSync(absolutePath, updated);
    console.log(`Updated ${absolutePath}`);
  } else {
    console.log(`Already ${version}: ${absolutePath}`);
  }
}

updateFirstMatch('package.json', /("version"\s*:\s*")[^"]+(")/);
updateFirstMatch('apps/companion/src-tauri/tauri.conf.json', /("version"\s*:\s*")[^"]+(")/);
updateFirstMatch('apps/companion/src-tauri/Cargo.toml', /(^version = ")[^"]+(")/m);
updateFirstMatch('apps/companion/src-tauri/Cargo.lock', /(name = "mystia-steward-companion"\s+version = ")[^"]+(")/);
updateFirstMatch('mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs', /(public const string PluginVersion = ")[^"]+(";)/);

console.log('');
console.log(`Project version synchronized to ${version}`);
NODE
