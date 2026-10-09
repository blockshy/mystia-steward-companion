import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createServer } from 'vite';
import { mkdirSync, writeFileSync } from 'node:fs';

// 完全人工生成的规模夹具，只评估纯业务吞吐；不打开游戏、不读取存档。
const vite = await createServer({ configFile: 'tests/reference/vite.config.ts', server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom', logLevel: 'silent' });
try {
  const { normalizeCompanionPreferences } = await vite.ssrLoadModule('/src/companion/preferences.ts');
  const tags = ['素', '肉', '水产', '鲜', '甜', '辣', '清淡', '重油', '高级', '下酒', '家常', '文化底蕴', '中华', '日式', '西式', '饱腹', '小巧', '菌类', '生', '凉爽'];
  const ingredients = Array.from({ length: 61 }, (_, i) => ({ id: i + 1, name: `材料${i + 1}`, tags: [tags[i % tags.length], tags[(i + 7) % tags.length]], price: 1 + i % 20, level: i % 6, description: '', type: '', dlc: 0, from: {} }));
  const recipes = Array.from({ length: 163 }, (_, i) => ({ id: i + 101, recipeId: i + 201, name: `料理${i + 1}`, ingredients: [ingredients[i % 61].name, ingredients[(i + 1) % 61].name], positiveTags: [tags[i % tags.length], tags[(i + 7) % tags.length]], negativeTags: [], cooker: ['煮锅', '烧烤架', '油锅', '蒸锅', '料理台'][i % 5], price: 20 + i % 90, level: i % 6, baseCookTime: 1, description: '', dlc: 0, from: {} }));
  const beverages = Array.from({ length: 62 }, (_, i) => ({ id: i + 1, name: `酒水${i + 1}`, tags: [i % 2 ? '水果' : '低酒精', '直饮'], level: i % 6, price: i % 100, description: '', dlc: 0, from: {} }));
  const customer = { id: 3, name: '测试稀客', positiveTags: ['鲜', '素', '下酒', '甜'], negativeTags: ['肉'], beverageTags: ['水果', '直饮'], places: ['妖怪兽道'], description: '', dlc: 0, price: [], enduranceLimit: 0, collection: false, evaluation: {}, spellCards: { positive: [], negative: [] } };
  const data = { ingredients, recipes, beverages, rareCustomers: [customer], normalCustomers: [customer], rareCustomerProfiles: [customer], foodTagIdMap: {}, beverageTagIdMap: {}, tagPriorityRules: [], source: 'runtime', status: 'test' };
  const runtime = { availableRecipeIds: recipes.map(x => x.id), availableIngredientIds: ingredients.map(x => x.id), availableBeverageIds: beverages.map(x => x.id), ownedIngredientQty: Object.fromEntries(ingredients.map(i => [i.id, 20])), ownedBeverageQty: Object.fromEntries(beverages.map(i => [i.id, 20])), placedCookerTypeIds: [1, 2, 3, 4, 5], placedCookers: [1, 2, 3, 4, 5].map((id, index) => ({ controllerIndex: index, controllerIdentity: `0x${4096 + index}`, gridPosition: { x: index, y: 0, z: 0 }, name: ['煮锅', '烧烤架', '油锅', '蒸锅', '料理台'][index], typeIds: [id], typeNames: [['煮锅', '烧烤架', '油锅', '蒸锅', '料理台'][index]], challengeLocked: false, couldOpen: true, automationAvailable: true })), placedCookerSnapshotComplete: true, placedCookerControllerCount: 5, placedCookerEmptyControllerCount: 0, placedCookerLockedControllerCount: 0, placedCookerReadFailureCount: 0, popularFoodTag: null, popularHateFoodTag: null, famousShopEnabled: false };
  const order = { traceId: 'R-1', orderLifecycleSequence: 1, deskCode: 1, guestId: 3, runtimeGuestId: 3003, guestName: '测试稀客', foodTag: '鲜', beverageTag: '水果', foodTagId: 1, beverageTagId: 2, automationAllowed: true, hasServedFood: false, hasServedBeverage: false, firstSeenAtUtc: '2026-10-09T10:00:00Z', fund: 200 };
  const args = { orders: [order], normalOrders: [], runtime, data, dataSignature: 'load', preferences: normalizeCompanionPreferences({}), favorites: { version: 1, recipes: [], beverages: [] }, customRecipes: { version: 1, enabled: true, recipes: [] }, activeRareGuests: [], specialBusiness: null, specialBusinessRejectedRecipeKeys: [], includeNormalOrderDetails: true, includeNormalExecutionTargets: true, usage: 'display' };
  if (process.argv.includes('--write-fixture')) {
    mkdirSync('temp', { recursive: true }); writeFileSync('temp/csharp-business-load-fixture.json', JSON.stringify(args));
    console.log('Wrote temp/csharp-business-load-fixture.json');
  } else {
  if (process.argv.includes('--page')) {
    // 首次页面查询包含完整候选与最终排序；后续只改变无关订单观察信息，单独报告页面投影命中耗时。
    const page = { query: { protocolVersion: 1, kind: 'rare', customerId: 3, foodTag: '鲜', beverageTag: '水果' },
      snapshot: { recommendationState: runtime }, data, preferences: args.preferences,
      favorites: args.favorites, customRecipes: args.customRecipes };
    const input = [0, 1, 2].map(index => JSON.stringify({ operation: 'page-cached', args: { ...page,
      snapshot: { ...page.snapshot, snapshotSignature: `page-${index}`, nightBusinessProgress: index } } })).join('\n') + '\n';
    const run = spawnSync('dotnet', ['tests/csharp-business-orders/bin/Release/net6.0/CSharpBusinessOrders.dll'], {
      input, encoding: 'utf8', maxBuffer: 128 * 1024 * 1024, windowsHide: true, timeout: 120000 });
    assert.equal(run.status, 0, run.stderr || run.error?.message);
    for (const [index, line] of run.stdout.trim().split(/\r?\n/).entries()) {
      const response = JSON.parse(line); assert.equal(response.ok, true, response.error);
      console.log(JSON.stringify({ operation: 'page', index, elapsedMs: response.elapsedMs,
        cacheHits: response.cacheHits, cacheMisses: response.cacheMisses, cacheBytes: response.cacheBytes }));
    }
  } else if (process.argv.includes('--ts')) {
    const service = await vite.ssrLoadModule('/src/companion/domain/service-recommendations.ts');
    const started = performance.now();
    const result = service.buildOrderRecommendations(args.orders, args.runtime, service.buildRareCustomerMap(args.data), service.createRecommendationCacheStore(), args.favorites, args.customRecipes, args.preferences, args.activeRareGuests, args.specialBusiness, args.specialBusinessRejectedRecipeKeys, args.data, { usage: args.usage });
    console.log(JSON.stringify({ implementation: 'frozen-ts', orderCount: 1, elapsedMs: performance.now() - started, firstPlanCount: result.recommendations[0]?.executionPlans.length }));
  } else {
  const counts = process.argv.includes('--full') ? [1, 4] : [1];
  for (const count of counts) {
    const payload = { ...args, orders: Array.from({ length: count }, (_, i) => ({ ...order, traceId: `R-${i + 1}`, orderLifecycleSequence: i + 1, deskCode: i + 1 })) };
    const run = spawnSync('dotnet', ['tests/csharp-business-orders/bin/Release/net6.0/CSharpBusinessOrders.dll'], { input: JSON.stringify({ operation: 'orders', args: payload }) + '\n', encoding: 'utf8', maxBuffer: 128 * 1024 * 1024, windowsHide: true, timeout: 120000 });
    assert.equal(run.status, 0, run.stderr || run.error?.message);
    const response = JSON.parse(run.stdout); assert.equal(response.ok, true, response.error);
    assert.equal(response.result.recommendations.length, count);
    console.log(JSON.stringify({ recipeCount: recipes.length, ingredientCount: ingredients.length, beverageCount: beverages.length, orderCount: count, elapsedMs: response.elapsedMs, firstPlanCount: response.result.recommendations[0].executionPlans.length }));
  }
  }
  }
} finally { await vite.close(); }
