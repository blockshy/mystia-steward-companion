import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createServer } from 'vite';

// 用连续真实缓存请求模拟经营中的观察更新。每步同时运行无缓存 C# 和冻结 TS oracle，
// 比较完整输出；命中计数只是证明没有重搜，绝不代替库存、预算、规则与详情的语义断言。
// 夹具全部为合成值，不读取游戏、配置或连接凭据，不把离线命中解释为游戏 FPS。
export async function verifyProjectionCache() {
  const vite = await createServer({ configFile: 'tests/reference-client/vite.config.ts',
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom', logLevel: 'silent' });
  try {
    const load = path => vite.ssrLoadModule(`/src/${path}.ts`);
    const [service, preferences] = await Promise.all([
      load('companion/domain/service-recommendations'), load('companion/preferences'),
    ]);
    const ingredient = (id, name, tags) => ({ id, name, tags, price: 2, level: 1, description: '', type: '', dlc: 0, from: {} });
    const ingredients = [ingredient(1, '豆腐', ['素']), ingredient(2, '蘑菇', ['鲜']), ingredient(3, '糖', ['甜'])];
    const recipes = [
      { id: 11, recipeId: 101, name: '素菜', ingredients: ['豆腐'], positiveTags: ['素'], negativeTags: [], cooker: '煮锅', price: 20, level: 1 },
      { id: 12, recipeId: 102, name: '鲜菜', ingredients: ['豆腐'], positiveTags: ['鲜'], negativeTags: [], cooker: '煮锅', price: 35, level: 3 },
      { id: 13, recipeId: 103, name: '甜菜', ingredients: ['豆腐'], positiveTags: ['甜'], negativeTags: [], cooker: '烧烤架', price: 50, level: 5 },
    ];
    const beverages = [{ id: 21, name: '果酒', tags: ['水果'], price: 30, level: 3 },
      { id: 22, name: '果饮', tags: ['水果', '低酒精'], price: 20, level: 1 }];
    const customer = { id: 31, name: '投影缓存客人', positiveTags: ['素', '鲜', '甜'], negativeTags: [],
      beverageTags: ['水果', '低酒精'], places: ['合成地图'], description: '', dlc: 0, price: [],
      enduranceLimit: 0, collection: false, evaluation: {}, spellCards: { positive: [], negative: [] } };
    const basis = {
      orders: [{ traceId: 'R-1', orderLifecycleSequence: 1, deskCode: 1, guestId: 31, runtimeGuestId: 3031,
        guestName: customer.name, foodTag: '鲜', beverageTag: '水果', foodTagId: 1, beverageTagId: 2,
        automationAllowed: true, hasServedFood: false, hasServedBeverage: false,
        firstSeenAtUtc: '2026-10-12T00:00:00Z', fund: 150, specialBusinessRole: 'yuyuko-boss-order', source: 'test' }],
      normalOrders: [], activeRareGuests: [], usage: 'display',
      data: { ingredients, recipes, beverages, rareCustomers: [customer], rareCustomerProfiles: [customer],
        normalCustomers: [], foodTagIdMap: {}, beverageTagIdMap: {}, tagPriorityRules: [], source: 'runtime', status: 'test' },
      runtime: { availableRecipeIds: [11, 12, 13], availableIngredientIds: [1, 2, 3], availableBeverageIds: [21, 22],
        ownedIngredientQty: { 1: 20, 2: 20, 3: 20 }, ownedBeverageQty: { 21: 10, 22: 10 },
        placedCookerTypeIds: [1, 2], placedCookers: [1, 2].map((type, index) => ({
          controllerIndex: index, controllerIdentity: `0x${4096 + index}`, gridPosition: { x: index, y: 0, z: 0 },
          name: type === 1 ? '煮锅' : '烧烤架', typeIds: [type], typeNames: [type === 1 ? '煮锅' : '烧烤架'],
          challengeLocked: false, couldOpen: true, automationAvailable: true, automationAvailability: 'StrictIdle',
          automationAvailabilityDiagnostic: '', source: 'test',
        })), placedCookerSnapshotComplete: true, placedCookerControllerCount: 2,
        placedCookerEmptyControllerCount: 0, placedCookerLockedControllerCount: 0, placedCookerReadFailureCount: 0,
        placedCookerStatus: '', popularFoodTag: null, popularHateFoodTag: null, famousShopEnabled: false },
      preferences: preferences.normalizeCompanionPreferences({ filterMissingCookers: true }),
      favorites: { version: 1, recipes: [], beverages: [] }, customRecipes: { version: 1, enabled: true, recipes: [] },
      specialBusiness: { active: true, challengeTypeAvailable: true, challengeType: 'Challenge_Yuyuko',
        phase: 'Phase 3', displayName: '', foodTargetTags: [], beverageTargetTags: [],
        lastTargetUpdated: '2026-10-12T00:00:00Z', targetTimeProgress: 0, currentAnger: 0 },
      specialBusinessRejectedRecipeKeys: [],
    };
    const clean = value => JSON.parse(JSON.stringify(value));
    const oracle = args => ({ ...service.buildOrderRecommendations(args.orders, args.runtime,
      service.buildRareCustomerMap(args.data), service.createRecommendationCacheStore(), args.favorites,
      args.customRecipes, args.preferences, args.activeRareGuests, args.specialBusiness,
      args.specialBusinessRejectedRecipeKeys, args.data, { usage: args.usage }),
    normalOrderDetailPlans: [], normalExecutionTargets: [] });
    const cases = [];
    const requests = [];
    function scenario(label, mutate, hit, options = {}) {
      const original = structuredClone(basis);
      // 完整目录仍是键的一部分，以合成目录标记隔离不同场景，不依赖缓存清空测试接口。
      original.data.status = `projection-${label}`;
      options.prepare?.(original);
      const changed = structuredClone(original);
      mutate(changed);
      const start = requests.length;
      for (const [args, cached] of [[original, false], [original, true], [changed, false], [changed, true]])
        requests.push({ operation: cached ? 'orders-cached' : 'orders', args });
      cases.push({ label, start, hit, different: options.different ?? false,
        expected: options.withoutOracle ? null : [clean(oracle(original)), clean(oracle(changed))] });
    }

    // 观察字段和物理实例字段不参与纯方案；真正传给算法的 runtime 并未删除这些字段。
    scenario('observed-target-time', value => { value.specialBusiness.lastTargetUpdated = '2026-10-12T00:00:20Z'; }, true);
    scenario('target-countdown', value => { value.specialBusiness.targetTimeProgress = 0.75; }, true);
    scenario('observed-anger', value => { value.specialBusiness.currentAnger = 50; }, true);
    scenario('native-cooker-busy', value => {
      Object.assign(value.runtime.placedCookers[0], { automationAvailable: false,
        automationAvailability: 'Unavailable', automationAvailabilityDiagnostic: '合成锅正在料理' });
    }, true);
    scenario('native-cooker-position', value => { value.runtime.placedCookers[0].gridPosition = { x: 9, y: 1, z: 0 }; }, true);
    scenario('native-cooker-identity', value => { value.runtime.placedCookers[0].controllerIdentity = '0xA123'; }, true);
    scenario('ingredient-pressure-high', value => { value.runtime.ownedIngredientQty[2] = 19; }, true);
    scenario('ingredient-pressure-infinite', value => { value.runtime.ownedIngredientQty[2] = -1; }, true);

    // 这些字段真正影响候选、预算、排序或解释，必须失效；完整结果必须等于重新计算。
    scenario('locked-cooker-type', value => {
      value.runtime.placedCookerTypeIds = [2]; value.runtime.placedCookers.shift();
      value.runtime.placedCookerLockedControllerCount = 1;
    }, false, { different: true });
    scenario('ingredient-pressure-threshold', value => { value.runtime.ownedIngredientQty[2] = 4; }, false, { different: true });
    scenario('ingredient-unavailable', value => {
      value.runtime.ownedIngredientQty[2] = 0; value.runtime.availableIngredientIds = [1, 3];
    }, false, { different: true });
    scenario('beverage-exact-stock', value => { value.runtime.ownedBeverageQty[21] = 9; }, false, { different: true });
    scenario('customer-preference', value => {
      value.data.rareCustomers[0].positiveTags = ['鲜']; value.data.rareCustomerProfiles[0].positiveTags = ['鲜'];
    }, false, { different: true });
    scenario('budget', value => { value.orders[0].fund = 30; }, false, { different: true });
    scenario('score-profile', value => {
      value.preferences.recommendationSortProfile = preferences.normalizeCompanionPreferences({
        recommendationSortProfile: { preset: 'profit' },
      }).recommendationSortProfile;
    }, false);
    scenario('special-phase', value => { value.specialBusiness.phase = 'Phase 2'; }, false, { different: true });
    scenario('popular-tag', value => { value.runtime.popularFoodTag = '鲜'; }, false, { different: true });
    scenario('excluded-ingredient', value => { value.preferences.recommendationExclusions.excludedIngredientIds = [2]; }, false, { different: true });
    // 规范配置默认每种料理只保留1个变体；改为2才是真实的显示/主方案投影依赖变化。
    scenario('variant-limit', value => { value.preferences.recipeVariantLimitPerBase = 2; }, false);
    scenario('usage-limit', value => { value.usage = 'automation'; }, false);
    scenario('favorite-order', value => {
      value.favorites.recipes = [{ customerId: 31, foodTag: '鲜', recipeId: 11, extraIngredientIds: [2] }];
    }, false, { prepare: value => { value.preferences.pinFavoriteRecipeEnabled = true; } });

    // 过滤关闭时 context 中的厨具集合可能相同，但无方案诊断的实际摆放说明必须更新。
    scenario('diagnostic-cooker-set', value => {
      value.runtime.placedCookerTypeIds = [1]; value.runtime.placedCookers.pop();
      value.runtime.placedCookerEmptyControllerCount = 1;
    }, false, { different: true, prepare: value => {
      value.preferences.filterMissingCookers = false; value.runtime.availableBeverageIds = [];
    } });

    // v2模式是新契约，旧TS没有该存储格式；仍以完整生产无缓存值树对照，不能只检查计数。
    scenario('custom-active-mode', value => { value.customRecipes.activeMode = 'food'; value.customRecipes.modeRevision++; }, false,
      { withoutOracle: true, different: true, prepare: value => {
        value.customRecipes = { version: 2, enabled: true, activeMode: 'customer', modeRevision: 0, recipes: [{
          id: 'synthetic-custom', mode: 'customer', customerId: 31, customerName: customer.name,
          foodTag: '鲜', foodId: 11, recipeId: 101, recipeName: '素菜', extraIngredientIds: [2, 3],
          enabled: true, pinToTop: true, sortOrder: 0, createdAtUtc: '', updatedAtUtc: '',
        }] };
      } });

    const host = process.env.CSHARP_BUSINESS_ORDERS_DLL || 'tests/csharp-business-orders/bin/Release/net6.0/CSharpBusinessOrders.dll';
    const run = spawnSync('dotnet', [host], { input: requests.map(value => JSON.stringify(value)).join('\n') + '\n',
      encoding: 'utf8', windowsHide: true, maxBuffer: 96 * 1024 * 1024, timeout: 120000 });
    assert.equal(run.status, 0, run.stderr || run.error?.message);
    const responses = run.stdout.trim().split(/\r?\n/).map(line => JSON.parse(line));
    assert.equal(responses.length, requests.length);
    responses.forEach(response => assert.equal(response.ok, true, response.error));
    for (const test of cases) {
      const [plain, cold, changedPlain, changedCached] = responses.slice(test.start, test.start + 4);
      assert.deepStrictEqual(cold.result, plain.result, `${test.label}: 冷缓存完整结果`);
      assert.deepStrictEqual(changedCached.result, changedPlain.result, `${test.label}: 更新后完整结果`);
      assert.ok(cold.cacheMisses > plain.cacheMisses, `${test.label}: 首次必须真正计算`);
      if (test.expected) {
        assert.deepStrictEqual(plain.result, test.expected[0], `${test.label}: 首次冻结TS对照`);
        assert.deepStrictEqual(changedPlain.result, test.expected[1], `${test.label}: 更新冻结TS对照`);
      }
      if (test.hit) {
        assert.equal(changedCached.cacheMisses, cold.cacheMisses, `${test.label}: 无关更新不得重新搜索`);
        assert.equal(changedCached.cacheHits, cold.cacheHits + 1, `${test.label}: 只命中最终纯投影`);
        assert.deepStrictEqual(changedCached.result, cold.result, `${test.label}: 确认完整输出确实不变`);
      } else {
        assert.ok(changedCached.cacheMisses > cold.cacheMisses, `${test.label}: 真实依赖变化必须失效`);
      }
      if (test.different) assert.notDeepStrictEqual(changedPlain.result, plain.result, `${test.label}: 夹具必须改变实际结果`);
    }
    console.log(`PASS projection cache: ${cases.length} temporal scenarios, ${cases.length * 2} complete uncached comparisons, ${cases.filter(test => test.expected).length * 2} frozen TS comparisons; input ownership checked by host.`);
  } finally { await vite.close(); }
}
