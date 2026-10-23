import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { verifyProjectionCache } from './projection-cache.mjs';
import { verifyPageCache } from './page-cache.mjs';

// 本夹具直接比较生产缓存与无缓存求解的完整 JSON，不维护另一套候选算法。
// 每个场景使用独立上下文标记保证首请求真正未命中，再检查仅另一侧标签变化可复用。
const ingredient = (id, name, tags) => ({ id, name, tags, price: id, level: 1, description: '', type: '', dlc: 0, from: {} });
const ingredients = [ingredient(1, '基础豆腐', ['素']), ingredient(2, '蘑菇', ['鲜']), ingredient(3, '蜂蜜', ['甜'])];
const recipes = [
  { id: 11, recipeId: 101, name: '素菜', ingredients: ['基础豆腐'], positiveTags: ['素'], negativeTags: [], price: 20, level: 1, cooker: '煮锅' },
  { id: 12, recipeId: 102, name: '鲜菜', ingredients: ['基础豆腐'], positiveTags: ['鲜'], negativeTags: [], price: 25, level: 2, cooker: '煮锅' },
];
const beverages = [
  { id: 21, name: '果酒', tags: ['水果'], price: 30, level: 1 },
  { id: 22, name: '米酒', tags: ['低酒精'], price: 20, level: 1 },
];
const customer = { id: 31, name: '候选缓存客人', positiveTags: ['素', '鲜'], negativeTags: [], beverageTags: ['水果'] };
const basis = {
  data: { ingredients, recipes, beverages, rareCustomers: [customer], normalCustomers: [], tagPriorityRules: [], source: 'runtime' },
  demand: { type: 'rare-tag-order', customer, requiredFoodTag: '素', requiredBeverageTag: '水果' },
  context: { availableRecipeIds: [11, 12], availableIngredientIds: [1, 2, 3], availableBeverageIds: [21, 22],
    disabledIngredientIds: [], excludedIngredientIds: [], excludedBeverageIds: [],
    ownedIngredientQty: { 1: 20, 2: 4, 3: 10 }, ownedBeverageQty: { 21: 5, 22: 10 },
    hasCookerSnapshot: true, placedCookerNames: ['煮锅'], filterMissingCookers: true, maxExtraIngredients: 2,
    tagPriorityRules: [], famousShopEnabled: false, popularFoodTag: '', popularHateFoodTag: '' },
  options: {},
};
const requests = [];
const scenarios = [];
function addScenario(label, kind, change, hit, different = false) {
  const original = structuredClone(basis);
  original.context.candidateCacheTestCase = label;
  const changed = structuredClone(original);
  change(changed);
  const start = requests.length;
  for (const [cached, args] of [[false, original], [true, original], [false, changed], [true, changed]]) {
    requests.push({ operation: `candidate-${kind}${cached ? '-cached' : ''}`, args: structuredClone(args) });
  }
  scenarios.push({ label, start, hit, different });
}

for (const [kind, other] of [['food', 'requiredBeverageTag'], ['beverage', 'requiredFoodTag']]) {
  addScenario(`${kind}-other-tag`, kind, value => { value.demand[other] = '另一侧新标签'; }, true);
  addScenario(`${kind}-other-absent`, kind, value => { delete value.demand[other]; }, true);
  addScenario(`${kind}-other-null`, kind, value => { value.demand[other] = null; }, true);
}
addScenario('food-own-tag', 'food', value => { value.demand.requiredFoodTag = '鲜'; }, false, true);
addScenario('beverage-own-tag', 'beverage', value => { value.demand.requiredBeverageTag = '低酒精'; }, false, true);
addScenario('food-customer', 'food', value => { value.demand.customer.positiveTags = ['甜']; }, false, true);
addScenario('beverage-customer', 'beverage', value => { value.demand.customer.beverageTags = ['低酒精']; }, false, true);
addScenario('food-special-target', 'food', value => {
  value.demand.specialFoodTarget = { enforcement: 'require', match: 'all', tags: ['鲜', '甜'] };
}, false, true);
addScenario('food-options', 'food', value => { value.options.requiredExtraIngredientIds = [2]; }, false, true);
addScenario('food-quantity', 'food', value => { value.context.ownedIngredientQty[2] = 0; }, false, true);
addScenario('beverage-quantity', 'beverage', value => { value.context.ownedBeverageQty[21] = 0; }, false, true);
addScenario('food-exclusion', 'food', value => { value.context.excludedIngredientIds = [2]; }, false, true);
addScenario('beverage-exclusion', 'beverage', value => { value.context.excludedBeverageIds = [21]; }, false, true);
addScenario('food-catalog', 'food', value => { value.data.recipes[0].price = 100; }, false, true);
addScenario('beverage-catalog', 'beverage', value => { value.data.beverages[0].price = 100; }, false, true);
// 本次只排除另一侧顶层标签，不顺便合并其它看似无关的输入，防止缓存契约无意扩大。
addScenario('food-keeps-beverage-stock', 'food', value => { value.context.ownedBeverageQty[21] = 1; }, false);
addScenario('beverage-keeps-food-target', 'beverage', value => {
  value.demand.specialFoodTarget = { enforcement: 'require', match: 'any', tags: ['鲜'] };
}, false);
for (const kind of ['food', 'beverage']) requests.push({ operation: 'candidate-ownership',
  args: { ...structuredClone(basis), kind, options: kind === 'food' ? { requiredExtraIngredientIds: [2] } : {} } });

const host = process.env.CSHARP_BUSINESS_ORDERS_DLL || 'tests/csharp-business-orders/bin/Release/net6.0/CSharpBusinessOrders.dll';
const run = spawnSync('dotnet', [host], {
  input: requests.map(value => JSON.stringify(value)).join('\n') + '\n', encoding: 'utf8',
  windowsHide: true, maxBuffer: 64 * 1024 * 1024, timeout: 120000,
});
assert.equal(run.status, 0, run.stderr || run.error?.message);
const responses = run.stdout.trim().split(/\r?\n/).map(line => JSON.parse(line));
assert.equal(responses.length, requests.length);
responses.forEach(response => assert.equal(response.ok, true, response.error));
for (const { label, start, hit, different } of scenarios) {
  const [plain, cold, changedPlain, changedCached] = responses.slice(start, start + 4);
  assert.deepStrictEqual(cold.result, plain.result, `${label}: 冷缓存必须等于完整无缓存结果`);
  assert.deepStrictEqual(changedCached.result, changedPlain.result, `${label}: 变更后缓存必须等于完整无缓存结果`);
  assert.equal(cold.cacheMisses, plain.cacheMisses + 1, `${label}: 场景首次请求必须真正求解`);
  assert.equal(changedCached.cacheHits, cold.cacheHits + (hit ? 1 : 0), `${label}: 命中边界不符`);
  assert.equal(changedCached.cacheMisses, cold.cacheMisses + (hit ? 0 : 1), `${label}: 失效边界不符`);
  if (hit) assert.deepStrictEqual(changedCached.result, cold.result, `${label}: 忽略字段必须确实与完整结果无关`);
  if (different) assert.notDeepStrictEqual(changedCached.result, cold.result, `${label}: 夹具必须触发实际结果变化`);
}
for (const response of responses.slice(-2)) {
  assert.equal(response.result.verified, true);
  assert.ok(response.result.candidateCount > 0);
  assert.equal(response.result.cacheHits, 2);
  assert.equal(response.result.cacheMisses, 1);
}
console.log(`PASS candidate cache: ${scenarios.length} boundary scenarios, ${scenarios.length * 2} complete result comparisons, 2 cold/hit ownership probes; input immutability checked.`);
await verifyProjectionCache();
await verifyPageCache();
