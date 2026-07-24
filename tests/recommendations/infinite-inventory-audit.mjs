import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  cappedInventoryQuantityRank,
  formatInventoryQuantitySuffix,
  formatInventoryQuantityValue,
  inventoryQuantityRankValue,
  inventoryShortage,
  isInfiniteInventoryQuantity,
} from '../../apps/companion/src/lib/inventory-quantity.ts';

assert.equal(isInfiniteInventoryQuantity(-1), true);
assert.equal(isInfiniteInventoryQuantity(-2), false);
assert.equal(formatInventoryQuantityValue(-1), '无限');
assert.equal(formatInventoryQuantityValue(-2), '--');
assert.equal(formatInventoryQuantitySuffix(-1), '(∞)');
assert.equal(formatInventoryQuantitySuffix(undefined), '(?)');
assert.ok(inventoryQuantityRankValue(-1) > inventoryQuantityRankValue(999));
assert.equal(cappedInventoryQuantityRank(-1, 99), 99);
assert.equal(cappedInventoryQuantityRank(12, 99), 12);
assert.equal(inventoryShortage(-1, 5), 0);
assert.equal(inventoryShortage(2, 5), 3);

const root = new URL('../../', import.meta.url);
const [scoreSources, inventorySorting] = await Promise.all([
  Promise.all([
    'apps/companion/src/recommendation-engine/rare-orders.ts',
    'apps/companion/src/companion/domain/service-recommendations.ts',
    'apps/companion/src/companion/domain/special-business/yuyuko-challenge.ts',
    'apps/companion/src/companion/domain/special-business/yuyuko-positive-spell.ts',
    'apps/companion/src/companion/domain/special-business/normal-targets/yuyuko.ts',
    'apps/companion/src/companion/domain/special-business/normal-targets/wacky.ts',
  ].map((path) => readFile(new URL(path, root), 'utf8'))),
  readFile(new URL('apps/companion/src/companion/domain/inventory-sorting.ts', root), 'utf8'),
]);
const scoreSource = scoreSources.join('\n');
assert.doesNotMatch(
  scoreSource,
  /Math\.min\([^\n]*ownedQuantity/,
  'Inventory scores must not treat the -1 infinite sentinel as a negative finite quantity.',
);
assert.doesNotMatch(
  scoreSource,
  /return\s+(?:beverage|candidate)(?:\?|\.)[^\n;]*ownedQuantity\s*;/,
  'Inventory ranking objectives must normalize the -1 infinite sentinel.',
);
assert.match(
  inventorySorting,
  /inventoryQuantityRankValue\(quantity\)/,
  'Low-stock-first inventory sorting must place the infinite sentinel after finite quantities.',
);

console.log('PASS: the exact -1 inventory sentinel is displayed and ranked as infinite across recommendations.');
