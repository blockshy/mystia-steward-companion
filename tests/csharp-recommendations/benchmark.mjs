import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

// 使用订单规模测试生成的同一人工目录，分离搜索与组合阶段耗时，避免用整轮缓存掩盖首轮成本。
const payload = JSON.parse(readFileSync('temp/csharp-business-load-fixture.json', 'utf8'));
const { data, runtime } = payload;
const customer = data.rareCustomers[0];
const context = { ...runtime, availableRecipeIds: runtime.availableRecipeIds, disabledIngredientIds: [], excludedIngredientIds: [], excludedBeverageIds: [], placedCookerNames: ['煮锅', '烧烤架', '油锅', '蒸锅', '料理台'], hasCookerSnapshot: true, maxExtraIngredients: 4, filterMissingCookers: true, tagPriorityRules: [], budgetPolicy: 'block', budget: null };
const demand = { customer, requiredFoodTag: '鲜', requiredBeverageTag: '水果', type: 'rare-tag-order' };
const invoke = (operation, args) => {
  const run = spawnSync('dotnet', ['tests/csharp-recommendations/bin/Release/net6.0/CSharpRecommendations.dll'], { input: JSON.stringify({ operation, args }) + '\n', encoding: 'utf8', maxBuffer: 128 * 1024 * 1024, windowsHide: true, timeout: 120000 });
  assert.equal(run.status, 0, run.stderr || run.error?.message);
  const response = JSON.parse(run.stdout); assert.equal(response.ok, true, response.error);
  console.log(JSON.stringify({ operation, elapsedMs: response.elapsedMs, count: response.result.length }));
  return response.result;
};
const foods = invoke('buildRareFoodCandidates', { data, demand, context });
const drinks = invoke('buildRareBeverageCandidates', { data, demand, context });
invoke('buildRareOrderPlansFromCandidates', { data, customer, requiredFoodTag: '鲜', requiredBeverageTag: '水果', context, foodCandidates: foods.slice(0, 24), beverageCandidates: drinks.slice(0, 16), limit: 80, sortProfile: { preset: 'balanced' }, sortContext: {} });
