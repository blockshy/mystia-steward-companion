#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$ROOT_DIR/../.." && pwd)"
SOURCE_DIR="$REPO_ROOT/apps/companion/src/data"
TARGET_DIR="$ROOT_DIR/Data"

mkdir -p "$TARGET_DIR"
cp "$SOURCE_DIR/recipes.json" "$TARGET_DIR/recipes.json"
cp "$SOURCE_DIR/beverages.json" "$TARGET_DIR/beverages.json"
cp "$SOURCE_DIR/ingredients.json" "$TARGET_DIR/ingredients.json"
cp "$SOURCE_DIR/customer_normal.json" "$TARGET_DIR/customer_normal.json"
cp "$SOURCE_DIR/customer_rare.json" "$TARGET_DIR/customer_rare.json"
cp "$SOURCE_DIR/food-tag-id-map.json" "$TARGET_DIR/food-tag-id-map.json"

echo "Data synced from $SOURCE_DIR to $TARGET_DIR"
