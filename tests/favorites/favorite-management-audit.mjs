import assert from 'node:assert/strict';

import {
  filterFavoriteManagementEntries,
  groupFavoriteManagementEntries,
  resolveFavoriteManagementEntries,
} from '../../apps/companion/src/companion/domain/favorite-management.ts';

const favorites = {
  version: 1,
  recipes: [
    {
      id: 'recipe-known',
      customerId: 2,
      customerName: '旧名称',
      foodTag: '甜',
      recipeId: 11,
      extraIngredientIds: [101],
      createdAtUtc: '2026-08-01T00:00:00Z',
      updatedAtUtc: '2026-08-02T00:00:00Z',
    },
    {
      id: 'recipe-missing',
      customerId: 3,
      customerName: '离线稀客',
      foodTag: '鲜',
      recipeId: 999,
      extraIngredientIds: [998],
      createdAtUtc: '2026-08-03T00:00:00Z',
      updatedAtUtc: '2026-08-04T00:00:00Z',
    },
  ],
  beverages: [
    {
      id: 'beverage-known',
      customerId: 2,
      customerName: '旧名称',
      beverageTag: '可加热',
      beverageId: 21,
      createdAtUtc: '2026-08-05T00:00:00Z',
      updatedAtUtc: '2026-08-06T00:00:00Z',
    },
    {
      id: 'beverage-missing',
      customerId: 4,
      customerName: '',
      beverageTag: '水果',
      beverageId: 997,
      createdAtUtc: '2026-08-07T00:00:00Z',
      updatedAtUtc: '2026-08-08T00:00:00Z',
    },
  ],
};

const data = {
  rareCustomers: [{ id: 2, name: '当前稀客名' }],
  recipes: [{ id: 11, name: '蜂蜜蛋糕', ingredients: ['鸡蛋', '蜂蜜'], cooker: '料理台' }],
  ingredients: [{ id: 101, name: '月光草' }],
  beverages: [{ id: 21, name: '热茶', tags: ['无酒精', '可加热'], price: 10 }],
};

const entries = resolveFavoriteManagementEntries(favorites, data);
assert.equal(entries.length, 4, '料理与酒水收藏没有进入同一管理集合');
assert.deepEqual(
  entries.filter((entry) => entry.customerId === 2).map((entry) => entry.kind),
  ['recipe', 'beverage'],
  '同一稀客下没有保持料理优先、酒水其次的稳定排序',
);

const knownRecipe = entries.find((entry) => entry.id === 'recipe-known');
assert.equal(knownRecipe?.customerName, '当前稀客名', '没有优先使用当前目录中的稀客名称');
assert.equal(knownRecipe?.itemName, '蜂蜜蛋糕', '没有解析当前料理目录');
assert.equal(knownRecipe?.catalogMissing, false, '完整料理收藏被错误标记为目录缺失');

const missingRecipe = entries.find((entry) => entry.id === 'recipe-missing');
assert.equal(missingRecipe?.itemName, '未知料理 #999', '目录缺失的料理没有保留可识别 ID');
assert.equal(missingRecipe?.customerName, '离线稀客', '目录缺失时没有保留收藏中的稀客名称');
assert.equal(missingRecipe?.catalogMissing, true, '目录缺失的料理没有标记为可清理的失配项');

const missingBeverage = entries.find((entry) => entry.id === 'beverage-missing');
assert.equal(missingBeverage?.customerName, '稀客 #4', '空名称收藏没有使用稳定稀客 ID 名称');
assert.equal(missingBeverage?.itemName, '未知酒水 #997', '目录缺失的酒水没有保留可识别 ID');

assert.deepEqual(
  filterFavoriteManagementEntries(entries, 'recipe', '月光草').map((entry) => entry.id),
  ['recipe-known'],
  '料理搜索没有覆盖加料名称',
);
assert.deepEqual(
  filterFavoriteManagementEntries(entries, 'beverage', '无酒精').map((entry) => entry.id),
  ['beverage-known'],
  '酒水搜索没有覆盖酒水标签',
);
assert.deepEqual(
  filterFavoriteManagementEntries(entries, 'all', '９９９').map((entry) => entry.id),
  ['recipe-missing'],
  '搜索没有对全角 ID 做 NFKC 归一化',
);

const groups = groupFavoriteManagementEntries(entries);
assert.equal(groups.length, 3, '收藏没有按稀客形成稳定分组');
assert.equal(groups.find((group) => group.customerId === 2)?.entries.length, 2, '同一稀客的料理与酒水没有归入同组');

console.log('PASS: favorite management resolves, filters and groups recipe/beverage favorites while retaining missing catalog entries.');
