import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createServer } from 'vite';

/**
 * 连续页面查询的缓存边界回归。每组均比较完整无缓存 C#、共享缓存 C# 与冻结 TS 结果，
 * 并验证真正依赖必须失效、原生观察字段只能命中一个最终投影。这里不模拟任何游戏调用。
 */
export async function verifyPageCache() {
  const vite = await createServer({ configFile: 'tests/reference-client/vite.config.ts',
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom', logLevel: 'silent' });
  try {
    const load = path => vite.ssrLoadModule(`/src/${path}.ts`);
    const [service, cookers, engine, custom, preferences] = await Promise.all([
      load('companion/domain/service-recommendations'), load('companion/domain/cookers'),
      load('recommendation-engine/index'), load('companion/domain/custom-recipes'), load('companion/preferences'),
    ]);
    const ingredients = [
      { id: 1, name: '豆腐', tags: ['素'], price: 2, level: 1 },
      { id: 2, name: '蘑菇', tags: ['鲜'], price: 3, level: 1 },
      { id: 3, name: '糖', tags: ['甜'], price: 4, level: 1 },
    ];
    const recipes = [
      { id: 11, recipeId: 101, name: '素菜', ingredients: ['豆腐'], positiveTags: ['素'], negativeTags: [], cooker: '煮锅', price: 20, level: 1 },
      { id: 12, recipeId: 102, name: '鲜菜', ingredients: ['豆腐'], positiveTags: ['鲜'], negativeTags: [], cooker: '烧烤架', price: 35, level: 3 },
    ];
    const beverages = [{ id: 21, name: '果酒', tags: ['水果'], price: 30, level: 3 },
      { id: 22, name: '果饮', tags: ['水果', '低酒精'], price: 20, level: 1 }];
    const customer = { id: 31, name: '页面缓存客人', positiveTags: ['素', '鲜', '甜', '流行喜爱'], negativeTags: ['肉'],
      beverageTags: ['水果', '低酒精'], places: ['合成地图'] };
    const basis = {
      query: { protocolVersion: 1, kind: 'rare', customerId: 31, foodTag: '鲜', beverageTag: '水果' },
      snapshot: { recommendationState: {
        availableRecipeIds: [11, 12], availableIngredientIds: [1, 2, 3], availableBeverageIds: [21, 22],
        ownedIngredientQty: { 1: 20, 2: 20, 3: 20 }, ownedBeverageQty: { 21: 10, 22: 10 },
        placedCookerTypeIds: [1, 2], placedCookers: [1, 2].map((type, index) => ({
          controllerIndex: index, controllerIdentity: `0x${4096 + index}`, gridPosition: { x: index, y: 0, z: 0 },
          name: type === 1 ? '煮锅' : '烧烤架', typeIds: [type], typeNames: [type === 1 ? '煮锅' : '烧烤架'],
          challengeLocked: false, couldOpen: true, automationAvailable: true, automationAvailability: 'StrictIdle',
          automationAvailabilityDiagnostic: '', source: 'test',
        })), placedCookerSnapshotComplete: true, placedCookerControllerCount: 2,
        placedCookerEmptyControllerCount: 0, placedCookerLockedControllerCount: 0, placedCookerReadFailureCount: 0,
        placedCookerStatus: '', popularFoodTag: null, popularHateFoodTag: null, famousShopEnabled: false,
      } },
      data: { ingredients, recipes, beverages, rareCustomers: [customer], rareCustomerProfiles: [customer],
        normalCustomers: [customer], foodTagIdMap: {}, beverageTagIdMap: {}, tagPriorityRules: [], source: 'runtime', status: 'test' },
      preferences: preferences.normalizeCompanionPreferences({ filterMissingCookers: true }),
      favorites: { version: 1, recipes: [], beverages: [] }, customRecipes: { version: 1, enabled: true, recipes: [] },
    };
    const clean = value => JSON.parse(JSON.stringify(value));
    const oracle = value => {
      const { query, snapshot, data, preferences: prefs, favorites, customRecipes } = value;
      const runtime = snapshot.recommendationState;
      const sets = cookers.buildRuntimeSets(runtime, data);
      if (!sets || data.source !== 'runtime') return { kind: query.kind, recipes: [], beverages: [] };
      if (query.kind === 'normal') {
        const context = { availableRecipeIds: sets.recipeIds, availableBeverageIds: sets.beverageIds,
          disabledIngredientIds: sets.unavailableIngredientIds, popularFoodTag: runtime.popularFoodTag,
          popularHateFoodTag: runtime.popularHateFoodTag, famousShopEnabled: runtime.famousShopEnabled,
          tagPriorityRules: data.tagPriorityRules };
        const options = { data, place: query.selectedPlace, context };
        return { kind: query.kind, recipes: engine.buildNormalFoodRecommendations(options).slice(0, 8),
          beverages: engine.buildNormalBeverageRecommendations(options).slice(0, 8) };
      }
      const selected = data.rareCustomers.find(item => item.id === query.customerId);
      if (!selected || !query.foodTag || !query.beverageTag) return { kind: query.kind, recipes: [], beverages: [] };
      const context = service.buildRecommendationRuntimeContext(runtime, sets, prefs, data);
      const demand = { type: 'rare-tag-order', customer: selected,
        requiredFoodTag: query.foodTag, requiredBeverageTag: query.beverageTag };
      const foods = custom.mergeCustomFoodCandidates(engine.buildRareFoodCandidates(data, demand, context),
        custom.buildCustomFoodCandidates({ customRecipes, data, customer: selected,
          requiredFoodTag: query.foodTag, requiredBeverageTag: query.beverageTag, context }));
      const drinks = engine.buildRareBeverageCandidates(data, demand, context);
      const options = { variantLimitPerBase: prefs.recipeVariantLimitPerBase, limit: 8,
        budget: context.budget, budgetPolicy: context.budgetPolicy, sortProfile: prefs.recommendationSortProfile,
        sortContext: service.buildRecommendationPlanSortContext(favorites, selected.id, query.foodTag, query.beverageTag, prefs) };
      return { kind: query.kind, recipes: service.deriveRecipeRowsFromCandidates(foods, drinks, options),
        beverages: service.deriveBeverageRowsFromCandidates(drinks, foods, options) };
    };
    const cases = []; const requests = [];
    function scenario(label, change, hit, { normal = false, different = false, prepare } = {}) {
      const original = structuredClone(basis);
      // data.status 属于仍完整保留的目录值，用实际键隔离场景，不能用已经排除的原始签名假隔离。
      original.data.status = `page-cache-${label}`;
      if (normal) original.query = { protocolVersion: 1, kind: 'normal', selectedPlace: '合成地图' };
      prepare?.(original);
      const changed = structuredClone(original); change(changed, changed.snapshot.recommendationState);
      const start = requests.length;
      for (const [args, cached] of [[original, false], [original, true], [changed, false], [changed, true]])
        requests.push({ operation: cached ? 'page-cached' : 'page', args });
      cases.push({ label, start, hit, different, expected: [clean(oracle(original)), clean(oracle(changed))] });
    }

    for (const normal of [false, true]) {
      const prefix = normal ? 'normal' : 'rare';
      scenario(`${prefix}-busy`, (_, runtime) => {
        for (const cooker of runtime.placedCookers) Object.assign(cooker, { automationAvailable: false,
          automationAvailability: 'Unavailable', automationAvailabilityDiagnostic: '合成忙碌观察-1' });
      }, true, { normal });
      scenario(`${prefix}-diagnostic`, (_, runtime) => {
        runtime.placedCookerStatus = '合成观察状态-2';
        runtime.placedCookers[0].automationAvailabilityDiagnostic = '合成观察-2';
      }, true, { normal });
      scenario(`${prefix}-physical-identity`, (_, runtime) => {
        runtime.placedCookers[0].controllerIdentity = '0xA123';
        runtime.placedCookers[0].gridPosition = { x: 9, y: 1, z: 0 };
      }, true, { normal });
      scenario(`${prefix}-popular`, (_, runtime) => { runtime.popularFoodTag = '鲜'; }, false, { normal, different: true });
      scenario(`${prefix}-popular-hate`, (_, runtime) => { runtime.popularHateFoodTag = '素'; }, false, { normal, different: true });
      scenario(`${prefix}-famous-shop`, (_, runtime) => { runtime.famousShopEnabled = true; }, false, { normal });
      scenario(`${prefix}-available-recipes`, (_, runtime) => { runtime.availableRecipeIds = [11]; }, false, { normal, different: true });
      scenario(`${prefix}-available-beverages`, (_, runtime) => { runtime.availableBeverageIds = [22]; }, false, { normal, different: true });
    }
    scenario('rare-locked-cooker', (_, runtime) => {
      runtime.placedCookerTypeIds = [2]; runtime.placedCookers.shift(); runtime.placedCookerLockedControllerCount = 1;
    }, false, { different: true });
    // 页面行不输出数量/压力原值，因此仅把某份可用加料从20改成4不保证投影变化。
    // 构造同标签、同成本的两种合法加料，仅启用资源压力排序：数量不足后必须改选另一份加料，
    // 不改 available 集合来间接触发变化，确保本断言确实覆盖 ownedIngredientQty 依赖。
    scenario('rare-ingredient-stock', (_, runtime) => { runtime.ownedIngredientQty[2] = 0; }, false,
      { different: true, prepare: value => {
        value.data.ingredients.push({ id: 4, name: '香菇', tags: ['鲜'], price: 3, level: 1 });
        value.data.rareCustomers[0].positiveTags = ['鲜'];
        value.data.rareCustomerProfiles[0].positiveTags = ['鲜'];
        value.snapshot.recommendationState.availableIngredientIds.push(4);
        value.snapshot.recommendationState.availableRecipeIds = [11];
        value.snapshot.recommendationState.ownedIngredientQty[4] = 20;
        for (const rule of value.preferences.recommendationSortProfile.objectives)
          Object.assign(rule, { enabled: rule.key === 'resourcePressure', weight: 100, direction: 'asc' });
      } });
    // 酒水数量也不直接出现在页面行中。让两款酒水命中完全相同的标签，仅启用库存排序，
    // 首次同库存由既有价格规则选21；随后21降为0必须改选22，避免偏好优势掩盖数量变化。
    scenario('rare-beverage-stock', (_, runtime) => { runtime.ownedBeverageQty[21] = 0; }, false,
      { different: true, prepare: value => {
        for (const beverage of value.data.beverages) beverage.tags = ['水果'];
        for (const rule of value.preferences.recommendationSortProfile.objectives)
          Object.assign(rule, { enabled: rule.key === 'beverageStock', weight: 100, direction: 'desc' });
      } });
    scenario('normal-disabled-ingredient', (_, runtime) => { runtime.availableIngredientIds = [2, 3]; }, false, { normal: true, different: true });
    // 页面没有订单余额输入；预算策略仍是实际候选/行解释的上下文，不能把它与经营余额混为一谈。
    scenario('rare-budget-policy', value => { value.preferences.recommendationBudgetPolicy = 'ignore'; }, false);
    scenario('rare-sort-profile', value => {
      value.preferences.recommendationSortProfile = preferences.normalizeCompanionPreferences({
        recommendationSortProfile: { preset: 'profit' },
      }).recommendationSortProfile;
    }, false);
    scenario('rare-favorites', value => {
      value.favorites.beverages = [{ customerId: 31, beverageTag: '水果', beverageId: 22 }];
    }, false, { prepare: value => { value.preferences.pinFavoriteBeverageEnabled = true; } });
    scenario('rare-custom', value => {
      value.customRecipes.recipes = [{ id: 'page-custom', customerId: 31, customerName: customer.name,
        foodTag: '鲜', foodId: 11, recipeId: 101, recipeName: '素菜', extraIngredientIds: [2, 3],
        enabled: true, pinToTop: true, sortOrder: 0, createdAtUtc: '', updatedAtUtc: '' }];
    }, false, { different: true });
    scenario('missing-runtime', value => { value.snapshot.recommendationState = null; }, false, { different: true });

    // 单组连续48次不同观察验证缓存不随观察数量增长，覆盖32槽共同预算被无效页面投影挤占的原故障。
    const stream = structuredClone(basis); stream.data.status = 'page-cache-observation-stream';
    const streamStart = requests.length; const streamExpected = clean(oracle(stream));
    for (let index = 0; index < 48; index++) {
      const value = structuredClone(stream);
      for (const cooker of value.snapshot.recommendationState.placedCookers) Object.assign(cooker, {
        automationAvailable: index % 2 === 0,
        automationAvailability: index % 2 === 0 ? 'StrictIdle' : 'Unavailable',
        automationAvailabilityDiagnostic: `synthetic-busy-${index}`,
      });
      requests.push({ operation: 'page-cached', args: value });
    }

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
      assert.deepStrictEqual(changedCached.result, changedPlain.result, `${test.label}: 变化后完整结果`);
      assert.deepStrictEqual(plain.result, test.expected[0], `${test.label}: 首次冻结TS对照`);
      assert.deepStrictEqual(changedPlain.result, test.expected[1], `${test.label}: 变化后冻结TS对照`);
      assert.ok(cold.cacheMisses > plain.cacheMisses, `${test.label}: 首次必须真正计算`);
      if (test.hit) {
        assert.equal(changedCached.cacheMisses, cold.cacheMisses, `${test.label}: 无关观察不得新增未命中`);
        assert.equal(changedCached.cacheHits, cold.cacheHits + 1, `${test.label}: 必须只命中最终投影`);
        assert.deepStrictEqual(changedCached.result, cold.result, `${test.label}: 完整输出确实不变`);
      } else assert.ok(changedCached.cacheMisses > cold.cacheMisses, `${test.label}: 真实依赖必须失效`);
      if (test.different) assert.notDeepStrictEqual(changedPlain.result, plain.result, `${test.label}: 夹具必须影响实际输出`);
      if (test.label === 'rare-ingredient-stock') {
        assert.deepStrictEqual(plain.result.recipes[0].extraIngredients.map(item => item.id), [2], '初始同压力量级应沿既有稳定顺序选蘑菇');
        assert.deepStrictEqual(changedPlain.result.recipes[0].extraIngredients.map(item => item.id), [4], '蘑菇数量不足后必须选择同标签的香菇');
      }
      if (test.label === 'rare-beverage-stock') {
        assert.equal(plain.result.beverages[0].beverage.id, 21, '初始同库存按既有价格规则选21');
        assert.equal(changedPlain.result.beverages[0].beverage.id, 22, '仅库存变化后应改选22');
      }
    }
    const initial = responses[streamStart];
    for (const [index, response] of responses.slice(streamStart).entries()) {
      assert.deepStrictEqual(response.result, streamExpected, `continuous-${index}: 完整冻结TS对照`);
      assert.equal(response.cacheMisses, initial.cacheMisses, `continuous-${index}: 不得重新求解`);
      assert.equal(response.cacheHits, initial.cacheHits + index, `continuous-${index}: 最终投影命中`);
      assert.equal(response.cacheEntries, initial.cacheEntries, `continuous-${index}: 不得挤占新槽位`);
      assert.equal(response.cacheBytes, initial.cacheBytes, `continuous-${index}: 缓存正文大小应稳定`);
      assert.ok(response.cacheEntries <= 32 && response.cacheBytes <= 32 * 1024 * 1024);
    }
    console.log(`PASS page cache runtime closure: ${cases.length} temporal scenarios, ${cases.length * 2} complete uncached comparisons, ${cases.length * 2 + 48} frozen TS comparisons, 48 stable-slot observations; input ownership checked by host.`);
  } finally { await vite.close(); }
}
