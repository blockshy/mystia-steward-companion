import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createServer } from 'vite';

// 192个组合保证明显超过80/32输出上限。使用冻结TS完整值树和可选旧C#宿主双重对照，
// 不以“首个ID相同”替代预算、解释、排序、阻断诊断与所有嵌套输出的一致性。
const vite = await createServer({ configFile: 'tests/reference-client/vite.config.ts',
  server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom', logLevel: 'silent' });
try {
  const [service, preferences] = await Promise.all([
    vite.ssrLoadModule('/src/companion/domain/service-recommendations.ts'),
    vite.ssrLoadModule('/src/companion/preferences.ts'),
  ]);
  const ingredient = { id: 1, name: '基础材料', tags: ['素'], price: 2, level: 1 };
  const recipes = Array.from({ length: 12 }, (_, index) => ({ id: 101 + index, recipeId: 201 + index,
    name: `料理${index}`, ingredients: [ingredient.name], positiveTags: ['鲜', '素'], negativeTags: [],
    cooker: '煮锅', price: 20 + index, level: 1 + index % 5, baseCookTime: 1 }));
  const beverages = Array.from({ length: 16 }, (_, index) => ({ id: 301 + index, name: `酒水${index}`,
    tags: ['水果', '直饮'], price: 10 + index, level: index % 5 }));
  const customer = { id: 3, name: '组合测试客人', positiveTags: ['鲜', '素'], negativeTags: [],
    beverageTags: ['水果', '直饮'], places: ['妖怪兽道'] };
  const data = { ingredients: [ingredient], recipes, beverages, rareCustomers: [customer],
    rareCustomerProfiles: [customer], normalCustomers: [], tagPriorityRules: [], source: 'runtime' };
  const runtime = { availableRecipeIds: recipes.map(value => value.id), availableIngredientIds: [1],
    availableBeverageIds: beverages.map(value => value.id), ownedIngredientQty: { 1: 20 },
    ownedBeverageQty: Object.fromEntries(beverages.map((value, index) => [value.id, index === 15 ? -1 : index + 1])),
    placedCookerTypeIds: [1], placedCookers: [{ controllerIndex: 0, controllerIdentity: '0x1000',
      gridPosition: { x: 0, y: 0, z: 0 }, name: '煮锅', typeIds: [1], typeNames: ['煮锅'],
      challengeLocked: false, couldOpen: true, automationAvailable: true }], placedCookerSnapshotComplete: true,
    placedCookerControllerCount: 1, placedCookerEmptyControllerCount: 0, placedCookerLockedControllerCount: 0,
    placedCookerReadFailureCount: 0, popularFoodTag: null, popularHateFoodTag: null, famousShopEnabled: false };
  const order = { traceId: 'R-1', orderLifecycleSequence: 1, deskCode: 0, guestId: 3, runtimeGuestId: 3003,
    guestName: customer.name, foodTag: '鲜', beverageTag: '水果', automationAllowed: true,
    hasServedFood: false, hasServedBeverage: false, fund: 200 };
  const basis = { orders: [order], normalOrders: [], data, runtime, dataSignature: 'materialization',
    preferences: preferences.normalizeCompanionPreferences({ maxExtraIngredients: 0 }),
    favorites: { version: 1, recipes: [], beverages: [] }, customRecipes: { version: 1, enabled: true, recipes: [] },
    activeRareGuests: [], specialBusiness: null, specialBusinessRejectedRecipeKeys: [],
    includeNormalOrderDetails: false, includeNormalExecutionTargets: false, usage: 'display' };
  const cases = [];
  function add(label, change = () => {}) {
    const payload = structuredClone(basis); change(payload);
    const value = service.buildOrderRecommendations(payload.orders, payload.runtime, service.buildRareCustomerMap(payload.data),
      service.createRecommendationCacheStore(), payload.favorites, payload.customRecipes, payload.preferences,
      payload.activeRareGuests, payload.specialBusiness, payload.specialBusinessRejectedRecipeKeys, payload.data, { usage: payload.usage });
    const expected = JSON.parse(JSON.stringify({ ...value, normalOrderDetailPlans: [], normalExecutionTargets: [] }));
    cases.push({ label, args: payload, expected });
  }
  add('display-80');
  add('automation-32', payload => { payload.usage = 'automation'; });
  add('stable-ties', payload => {
    for (const recipe of payload.data.recipes) { recipe.price = 20; recipe.level = 1; }
    for (const beverage of payload.data.beverages) { beverage.price = 10; beverage.level = 1; payload.runtime.ownedBeverageQty[beverage.id] = 20; }
  });
  add('mission-last-recipe', payload => {
    payload.preferences.missionRecipePriorityEnabled = true;
    payload.orders[0].missionRecipePriority = { traceId: order.traceId, deskCode: order.deskCode,
      guestId: order.guestId, runtimeGuestId: order.runtimeGuestId, foodId: 112, recipeId: 212,
      missionGeneration: 1, businessGeneration: 1 };
  });
  add('favorites-only', payload => {
    Object.assign(payload.preferences, { automationEnabled: true, autoRareOrderEnabled: true,
      autoPrepStartCooking: true, autoPrepTakeBeverage: true, autoPrepRecipeFavoritesOnly: true,
      autoPrepBeverageFavoritesOnly: true, pinFavoriteRecipeEnabled: false, pinFavoriteBeverageEnabled: false });
    payload.favorites.recipes = [{ customerId: 3, foodTag: '鲜', recipeId: 112, extraIngredientIds: [] }];
    payload.favorites.beverages = [{ customerId: 3, beverageTag: '水果', beverageId: 316 }];
  });
  for (const policy of ['block', 'warn', 'ignore']) add(`budget-${policy}`, payload => {
    payload.orders[0].fund = 0; payload.preferences.recommendationBudgetPolicy = policy;
  });
  add('pinned-custom', payload => {
    payload.customRecipes.recipes = [{ id: 'custom', customerId: 3, customerName: customer.name,
      foodTag: '鲜', foodId: 112, recipeId: 212, recipeName: '料理11', extraIngredientIds: [],
      enabled: true, pinToTop: true, sortOrder: 0, createdAtUtc: '', updatedAtUtc: '' }];
  });
  for (const challenge of ['yuyuko', 'koishi']) add(challenge, payload => {
    payload.specialBusiness = { active: true, challengeTypeAvailable: true, phase: 'Phase 3',
      challengeType: challenge === 'yuyuko' ? 'Story_Yuyuko' : 'Story_WackyCookingCompetition',
      wackyKoishiShieldBroken: true, maxValue: 100, targetValue: 12, currentValue: 8,
      foodTargetTags: [], beverageTargetTags: [], requiredExtraIngredientIds: [] };
    payload.orders[0].specialBusinessRole = challenge === 'yuyuko' ? 'yuyuko-boss-order' : 'wacky-koishi-boss';
    payload.orders[0].remainingOrderCount = 4;
  });
  // 新的键排序必须保持两种三阶段评价、二阶段符卡以及阻断说明的完整TS结果。
  // 此处仍不截断原始方案集，特意覆盖同分同价与无法满足评价的诊断分支。
  for (const [label, phase, blocked, ties] of [
    ['yuyuko-retake-stage3', 'Phase 3', false, false],
    ['yuyuko-positive-stage2', 'Phase 2', false, false],
    ['yuyuko-blocked-stage3', 'Phase 3', true, false],
    ['yuyuko-stable-ties', 'Phase 3', false, true],
  ]) add(label, payload => {
    payload.specialBusiness = { active: true, challengeTypeAvailable: true,
      challengeType: 'Challenge_Yuyuko', phase, foodTargetTags: [], beverageTargetTags: [] };
    payload.orders[0].specialBusinessRole = 'yuyuko-boss-order';
    if (blocked) {
      payload.data.rareCustomers[0].positiveTags = ['鲜'];
      payload.data.rareCustomerProfiles[0].positiveTags = ['鲜'];
      payload.data.rareCustomers[0].beverageTags = ['水果'];
      payload.data.rareCustomerProfiles[0].beverageTags = ['水果'];
    }
    if (ties) {
      for (const recipe of payload.data.recipes) { recipe.price = 20; recipe.level = 1; }
      for (const beverage of payload.data.beverages) { beverage.price = 10; beverage.level = 1; payload.runtime.ownedBeverageQty[beverage.id] = 20; }
    }
  });
  const requests = cases.map(value => ({ operation: 'orders', args: value.args }));
  function run(host, values) {
    const result = spawnSync('dotnet', [host], { input: values.map(value => JSON.stringify(value)).join('\n') + '\n',
      encoding: 'utf8', windowsHide: true, maxBuffer: 128 * 1024 * 1024, timeout: 180000 });
    assert.equal(result.status, 0, result.error?.message ?? result.stderr);
    const rows = result.stdout.trim().split(/\r?\n/).map(line => JSON.parse(line));
    assert.equal(rows.length, values.length);
    for (const row of rows) assert.equal(row.ok, true, row.error);
    return rows;
  }
  const host = process.env.CSHARP_BUSINESS_ORDERS_DLL || 'tests/csharp-business-orders/bin/Release/net6.0/CSharpBusinessOrders.dll';
  const results = run(host, [...requests, { operation: 'plan-ownership', args: basis }, { operation: 'yuyuko-sort-probe', args: {} }]);
  for (const [index, fixture] of cases.entries()) assert.deepStrictEqual(results[index].result, fixture.expected, fixture.label);
  assert.equal(results[0].result.recommendations[0].executionPlans.length, 80, '夹具必须触及展示上限');
  assert.equal(results[1].result.recommendations[0].executionPlans.length, 32, '夹具必须触及自动化上限');
  assert.equal(results.at(-2).result.verified, true);
  assert.equal(results.at(-2).result.assertions, 10);
  assert.equal(results.at(-1).result.verified, true);
  assert.equal(results.at(-1).result.assertions, 33);
  console.log(JSON.stringify({ label: 'yuyuko-sort-probe', ...results.at(-1).result }));
  const baselineIndex = process.argv.indexOf('--baseline');
  let old;
  if (baselineIndex >= 0) {
    assert.ok(process.argv[baselineIndex + 1], '--baseline必须提供已冻结旧宿主DLL');
    old = run(process.argv[baselineIndex + 1], requests);
    for (const [index, fixture] of cases.entries()) assert.deepStrictEqual(results[index].result, old[index].result, `${fixture.label}: 旧C#完整输出`);
    for (const index of [0, 1]) assert.ok(results[index].allocatedBytes < old[index].allocatedBytes,
      `${cases[index].label}: 推迟物化必须确实降低完整求解分配`);
  }
  for (const [index, fixture] of cases.entries()) console.log(JSON.stringify({ label: fixture.label,
    resultSha256: createHash('sha256').update(JSON.stringify(results[index].result)).digest('hex'),
    allocatedBytes: results[index].allocatedBytes, baselineAllocatedBytes: old?.[index].allocatedBytes,
    elapsedMs: results[index].elapsedMs }));
  console.log(`PASS plan materialization: ${cases.length} complete frozen-oracle comparisons, 10 ownership/promotion assertions, 2 cap assertions${old ? `, ${cases.length} baseline comparisons and 2 allocation reductions` : ''}.`);
} finally { await vite.close(); }
