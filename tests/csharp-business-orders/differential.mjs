import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { createServer } from 'vite';

// 旧 TS 仅作为迁移期离线 oracle；生产不得引用测试宿主或以旧算法作隐式回退。
// 所有比较均检查完整 JSON 值树及纯函数输入不变性，覆盖推荐、特殊经营和 UI 精确身份。
const vite = await createServer({ configFile: 'tests/reference/vite.config.ts', server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom', logLevel: 'silent' });
try {
  const load = path => vite.ssrLoadModule(`/src/${path}.ts`);
  const [service, details, registry, ui, sorting, keys, preferences, indexes, engine, yuyuko, positive] = await Promise.all([
    load('companion/domain/service-recommendations'), load('companion/domain/normal-order-details'), load('companion/domain/special-business/registry'), load('companion/domain/game-ui-targets'), load('companion/domain/sorting'), load('companion/domain/normal-order-key'), load('companion/preferences'), load('lib/recommendation-data'), load('recommendation-engine/index'), load('companion/domain/special-business/yuyuko-challenge'), load('companion/domain/special-business/yuyuko-positive-spell'),
  ]);
  const fixtures = [];
  const clean = value => JSON.parse(JSON.stringify(value));
  const oracle = (operation, a) => {
    if (operation === 'recipe-rows') return service.deriveRecipeRowsFromCandidates(a.foods, a.beverages, a.options);
    if (operation === 'beverage-rows') return service.deriveBeverageRowsFromCandidates(a.beverages, a.foods, a.options);
    if (operation === 'rule') return registry.buildSpecialBusinessOrderRule(a.specialBusiness, a.role);
    if (operation === 'wire') return registry.buildSpecialFoodTargetWirePolicy(a.specialBusiness, a.role, a.generation);
    if (operation === 'requires') return registry.requiresSpecialBusinessNormalExecutionTarget(a.specialBusiness, a.role);
    if (operation === 'normal') return registry.selectSpecialBusinessNormalExecutionTarget(a);
    if (operation === 'yuyuko-normal') return yuyuko.evaluateYuyukoNormalOrderPair(a.challengeType, a.food, a.beverage, a.modifierPreferences);
    if (operation === 'yuyuko-rare') return yuyuko.evaluateYuyukoRareOrderPair(a.mode, a.food, a.beverage, a.demand);
    if (operation === 'yuyuko-positive') return positive.evaluateYuyukoPositiveSpellPair(a.food, a.beverage, a.demand);
    if (operation === 'game-ui') {
      if (a.operation === 'rare') return ui.buildRareGameUiTarget(a.recommendations, a.orderSortMode, a.color, a.features, indexes.buildRecommendationDataIndexes(a.data), a.options);
      if (a.operation === 'normal') return ui.buildNormalGameUiTarget(a);
      if (a.operation === 'reconcile') return ui.reconcileGameUiTarget(a.target, a.sources);
      return a.operation === 'rare-source' ? ui.buildRareGameUiTargetSource(a.order) : ui.buildNormalGameUiTargetSource(a.order);
    }
    const result = service.buildOrderRecommendations(a.orders, a.runtime, service.buildRareCustomerMap(a.data), service.createRecommendationCacheStore(), a.favorites, a.customRecipes, a.preferences, a.activeRareGuests, a.specialBusiness, a.specialBusinessRejectedRecipeKeys, a.data, { usage: a.usage });
    const normalOrders = sorting.sortNormalOrders(a.normalOrders ?? []);
    const args = order => ({ order, specialBusiness: a.specialBusiness, runtime: a.runtime, preferences: a.preferences, data: a.data, dataSignature: a.dataSignature, rejectedRecipeKeys: a.specialBusinessRejectedRecipeKeys });
    return { ...result, normalOrderDetailPlans: a.includeNormalOrderDetails ? details.buildNormalOrderDetailPlans({ ...args(), orders: normalOrders }) : [], normalExecutionTargets: a.includeNormalExecutionTargets ? normalOrders.filter(o => !o.hasEvaluated).map(order => ({ orderKey: keys.buildNormalAutoOrderKey(order), ...registry.selectSpecialBusinessNormalExecutionTarget(args(order)) })) : [] };
  };
  const add = (operation, args, label = operation) => { const expected = clean(oracle(operation, args)); fixtures.push({ operation, args: clean(args), expected, label }); return expected; };
  const ingredient = (id, name, tags) => ({ id, name, tags, price: 2, level: 1, description: '', type: '', dlc: 0, from: {} });
  const ingredients = [ingredient(1, '豆腐', ['素', '清淡']), ingredient(2, '猪肉', ['肉']), ingredient(3, '蘑菇', ['鲜', '下酒']), ingredient(4, '糖', ['甜']), ingredient(5002, '噗噗呦果', ['水果']), ingredient(5005, '辣椒水', ['辣']), ingredient(5, '目标材料', ['目标甲', '目标乙'])];
  const recipes = [1, 2, 3].map((id, i) => ({ id: id + 100, recipeId: id + 200, name: `料理${id}`, ingredients: [ingredients[i].name], positiveTags: ingredients[i].tags, negativeTags: [], cooker: ['煮锅', '烧烤架', '料理台'][i], price: [20, 60, 90][i], level: [1, 3, 5][i], baseCookTime: 1, description: '', dlc: 0, from: {} }));
  const beverages = [{ id: 21, name: '果酒', tags: ['水果', '低酒精'], level: 3, price: 20 }, { id: 22, name: '水', tags: ['直饮', '清淡'], level: 0, price: 0 }, { id: 23, name: '甜酒', tags: ['甜', '水果', '直饮'], level: 5, price: 80 }].map(b => ({ ...b, description: '', dlc: 0, from: {} }));
  const customer = { id: 3, name: '橙', positiveTags: ['鲜', '素', '下酒', '甜'], negativeTags: ['肉'], beverageTags: ['水果', '直饮'], places: ['妖怪兽道'], description: '', dlc: 0, price: [], enduranceLimit: 0, collection: false, evaluation: {}, spellCards: { positive: [], negative: [] } };
  const data = { ingredients, recipes, beverages, rareCustomers: [customer], normalCustomers: [customer], rareCustomerProfiles: [customer, { ...customer, id: 23 }, { ...customer, id: 1003 }], foodTagIdMap: {}, beverageTagIdMap: {}, tagPriorityRules: [], source: 'runtime', status: 'test' };
  const typeNames = { 1: '煮锅', 2: '烧烤架', 5: '料理台' };
  const runtime = { availableRecipeIds: [101, 102, 103], availableIngredientIds: ingredients.map(i => i.id), availableBeverageIds: [21, 22, 23], ownedIngredientQty: Object.fromEntries(ingredients.map(i => [i.id, i.id === 1 ? -1 : 20])), ownedBeverageQty: { 21: -1, 22: 10, 23: 20 }, placedCookerTypeIds: [1, 2, 5], placedCookers: [1, 2, 5].map((id, index) => ({ controllerIndex: index, controllerIdentity: `0x${4096 + index}`, gridPosition: { x: index, y: 0, z: 0 }, name: typeNames[id], typeIds: [id], typeNames: [typeNames[id]], challengeLocked: false, couldOpen: true, automationAvailable: true, automationAvailability: 'StrictIdle', automationAvailabilityDiagnostic: '', source: 'test' })), placedCookerSnapshotComplete: true, placedCookerControllerCount: 3, placedCookerEmptyControllerCount: 0, placedCookerLockedControllerCount: 0, placedCookerReadFailureCount: 0, placedCookerStatus: '', popularFoodTag: null, popularHateFoodTag: null, famousShopEnabled: false };
  const order = { traceId: 'R-1', orderLifecycleSequence: 1, deskCode: 1, guestId: 3, runtimeGuestId: 3003, guestName: '橙', foodTag: '鲜', beverageTag: '水果', foodTagId: 1, beverageTagId: 2, automationAllowed: true, hasServedFood: false, hasServedBeverage: false, firstSeenAtUtc: '2026-10-09T10:00:00Z', fund: 150, source: 'test' };
  const normal = { ...order, traceId: 'N-1', orderKey: 'ptr:123', foodId: 101, foodName: '料理1', beverageId: 21, beverageName: '果酒', hasEvaluated: false, readyToEvaluate: false };
  const base = { orders: [order], normalOrders: [normal], runtime, data, dataSignature: 'test', preferences: preferences.normalizeCompanionPreferences({}), favorites: { version: 1, recipes: [], beverages: [] }, customRecipes: { version: 1, enabled: true, recipes: [] }, activeRareGuests: [], specialBusiness: null, specialBusinessRejectedRecipeKeys: [], includeNormalOrderDetails: true, includeNormalExecutionTargets: true, usage: 'display' };
  const special = { active: true, challengeTypeAvailable: true, challengeType: 'Story_WackyCookingCompetition', displayName: '', foodTargetTags: ['目标甲', '目标乙'], beverageTargetTags: [], requiredExtraIngredientIds: [], phase: 'Phase 1', yuumaFoodTargetRevision: 2 };
  const scenarios = [null, { ...special, active: false }, { ...special, challengeTypeAvailable: false }, special, { ...special, phase: 'Phase 2' }, { ...special, phase: 'Phase 3' }, { ...special, phase: 'Phase 3', wackyKoishiShieldBroken: true, maxValue: 100, targetValue: 12, currentValue: 8 }, { ...special, phase: 'Phase 3', wackyKoishiFoodPreferenceTags: ['鲜', '素', '甜'], wackyKoishiFoodHateTags: ['肉'], wackyKoishiBeveragePreferenceTags: ['水果'] }, { ...special, challengeType: 'Story_Yuyuko', phase: 'Phase 3' }, { ...special, challengeType: 'Challenge_Yuyuko', phase: 'Phase 3' }, { ...special, challengeType: 'Challenge_Yuyuko', phase: 'Phase 2' }, { ...special, challengeType: 'Story_BloodPondHell' }, { ...special, challengeType: 'Story_Mizuchi', requiredExtraIngredientIds: [5002] }, { ...special, challengeType: 'Story_Mizuchi_1', requiredExtraIngredientIds: [5005] }, { ...special, challengeType: 'unknown' }];
  const roles = ['', 'wacky-koishi-boss', 'wacky-target-order', 'yuyuko-boss-order', 'yuuma-boss-order', 'yuuma-order-unverified', 'mizuchi-story-possessed-order', 'mizuchi-story-ordinary-order', 'mizuchi-story-unverified-order', 'mizuchi-trial-possessed-order', 'mizuchi-trial-ordinary-order', 'mizuchi-trial-unverified-order'];
  for (const s of scenarios) for (const role of roles) {
    add('rule', { specialBusiness: s, role }); add('requires', { specialBusiness: s, role }); add('wire', { specialBusiness: s, role, generation: 3 });
  }
  const baseline = add('orders', base, 'ordinary-order');
  for (const [i, s] of scenarios.entries()) {
    const role = s?.challengeType?.includes('Yuyuko') ? 'yuyuko-boss-order' : s?.challengeType === 'Story_BloodPondHell' ? 'yuuma-boss-order' : s?.challengeType?.startsWith('Story_Mizuchi') ? s.challengeType === 'Story_Mizuchi' ? 'mizuchi-story-possessed-order' : 'mizuchi-trial-possessed-order' : 'wacky-koishi-boss';
    const payload = { ...base, dataSignature: `scenario-${i}`, specialBusiness: s, orders: [{ ...order, specialBusinessRole: role }], normalOrders: [{ ...normal, specialBusinessRole: role, runtimeGuestId: role === 'yuuma-boss-order' ? 1003 : normal.runtimeGuestId }] };
    add('orders', payload, `special-order-${i}`);
    add('orders', { ...payload, usage: 'automation' }, `special-automation-${i}`);
  }
  const runtimeChanges = [null, { ...runtime, availableRecipeIds: [] }, { ...runtime, availableIngredientIds: [] }, { ...runtime, availableBeverageIds: [] }, { ...runtime, placedCookerTypeIds: [], placedCookers: [], placedCookerControllerCount: 0 }, { ...runtime, placedCookerReadFailureCount: 1, placedCookerSnapshotComplete: false }];
  for (const [i, r] of runtimeChanges.entries()) add('orders', { ...base, runtime: r }, `unavailable-${i}`);
  for (const policy of ['block', 'warn', 'ignore']) for (const fund of [0, 10, 70, 200]) add('orders', { ...base, preferences: preferences.normalizeCompanionPreferences({ recommendationBudgetPolicy: policy }), orders: [{ ...order, fund }] }, `budget-${policy}-${fund}`);
  for (const foodTag of ['鲜', '未知', '肉']) for (const beverageTag of ['水果', '未知']) add('orders', { ...base, orders: [{ ...order, foodTag, beverageTag }] }, `tags-${foodTag}-${beverageTag}`);
  for (const pin of [false, true]) for (const favoritesOnly of [false, true]) add('orders', { ...base, favorites: { ...base.favorites, recipes: [{ customerId: 3, foodTag: '鲜', recipeId: 101, extraIngredientIds: [3] }], beverages: [{ customerId: 3, beverageTag: '水果', beverageId: 23 }] }, preferences: preferences.normalizeCompanionPreferences({ pinFavoriteRecipeEnabled: pin, pinFavoriteBeverageEnabled: pin, automationEnabled: true, autoRareOrderEnabled: true, autoPrepStartCooking: true, autoPrepTakeBeverage: true, autoPrepRecipeFavoritesOnly: favoritesOnly, autoPrepBeverageFavoritesOnly: favoritesOnly }) }, `favorites-${pin}-${favoritesOnly}`);
  const customEntry = { id: 'test', customerId: 3, customerName: '橙', foodTag: '鲜', foodId: 101, recipeId: 201, recipeName: '料理1', extraIngredientIds: [3, 4], enabled: true, pinToTop: true, sortOrder: 0, createdAtUtc: '', updatedAtUtc: '' };
  add('orders', { ...base, customRecipes: { ...base.customRecipes, recipes: [customEntry] } }, 'pinned-custom');
  for (const mode of ['ordered', 'guest']) add('orders', { ...base, orders: [{ ...order, traceId: 'R-2', deskCode: 2 }, { ...order, guestId: null, traceId: 'R-3', firstSeenAtUtc: '2026-10-09T09:00:00Z' }], preferences: { ...base.preferences, serviceOrderSortMode: mode } }, `sorting-${mode}`);
  const features = { recipeVariant: true, ingredientHighlight: true, beverageHighlight: true, cookerHighlight: true, seatHighlight: true };
  const target = add('game-ui', { operation: 'rare', recommendations: baseline.recommendations, data, orderSortMode: 'ordered', color: '#ffffff', features, options: {} }, 'rare-target');
  const source = add('game-ui', { operation: 'rare-source', order });
  for (const patch of [{}, { hasServedFood: true }, { hasServedBeverage: true }, { hasServedFood: true, hasServedBeverage: true }, { terminal: true }, { sourceOrderSignature: 'other' }]) add('game-ui', { operation: 'reconcile', target, sources: [{ ...source, ...patch }] });
  add('game-ui', { operation: 'reconcile', target, sources: [source, source] }, 'ambiguous-source');
  for (const patch of [{}, { traceId: 'R-x' }, { orderLifecycleSequence: 0 }, { deskCode: -1 }]) add('game-ui', { operation: 'rare', recommendations: baseline.recommendations.map(r => ({ ...r, order: { ...r.order, ...patch } })), data, orderSortMode: 'ordered', color: '#ffffff', features, options: {} }, 'rare-identity');
  for (const patch of [{}, { orderKey: 'ptr:0' }, { orderKey: 'ptr:xyz' }, { hasServedFood: true }, { hasEvaluated: true }]) add('game-ui', { operation: 'normal', orders: [{ ...normal, ...patch }], executionTargets: [], executionTargetsCurrent: true, specialBusiness: null, businessGeneration: 3, color: '#fff', features, data }, 'normal-target');
  const pair = baseline.recommendations[0].executionPlans[0];
  for (const challengeType of ['Story_Yuyuko', 'Challenge_Yuyuko', 'unknown']) for (const modifierPreferences of [null, customer]) add('yuyuko-normal', { challengeType, food: pair.food, beverage: pair.beverage, modifierPreferences });
  for (const mode of ['none', 'story-level-sum', 'retake-tag-order']) add('yuyuko-rare', { mode, food: pair.food, beverage: pair.beverage, demand: pair.demand });
  add('yuyuko-positive', { food: pair.food, beverage: pair.beverage, demand: pair.demand });
  // 精确普通目标覆盖：每个场景都变化订单身份、原配方、原酒水、库存和已揭示偏好。
  // 场景名称进入dataSignature，确保参考实现的缓存不会掩盖不同输入造成的变化。
  let normalCase = 0;
  for (const s of scenarios.filter(s => s?.active)) for (const role of ['wacky-target-order', 'wacky-ghost-order', 'wacky-koishi-boss', 'yuyuko-boss-order', 'yuuma-boss-order', 'yuuma-order-unverified']) {
    for (const patch of [{}, { foodId: 9999 }, { beverageId: 9999 }, { fund: 0, remainingOrderCount: 0 }, { guestId: null, runtimeGuestId: null }, { foodPreferenceTags: ['鲜', '甜', '目标甲'], beveragePreferenceTags: ['水果', '直饮'] }]) {
      const args = { order: { ...normal, specialBusinessRole: role, runtimeGuestId: role === 'yuuma-boss-order' ? 1003 : normal.runtimeGuestId, ...patch }, specialBusiness: s, runtime, preferences: base.preferences, data, dataSignature: `normal-${++normalCase}`, rejectedRecipeKeys: [] };
      add('normal', args, args.dataSignature);
    }
  }
  const mission = { traceId: order.traceId, deskCode: order.deskCode, guestId: order.guestId, runtimeGuestId: order.runtimeGuestId, foodId: 101, recipeId: 201, missionGeneration: 1, businessGeneration: 1 };
  for (const patch of [{}, { traceId: 'stale' }, { foodId: 999 }, { missionGeneration: 0 }]) for (const missionRecipePriorityEnabled of [false, true]) add('orders', { ...base, orders: [{ ...order, missionRecipePriority: { ...mission, ...patch } }], preferences: preferences.normalizeCompanionPreferences({ missionRecipePriorityEnabled }) }, 'mission-primary');
  let seed = 0x4d595354;
  const random = maximum => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed % maximum; };
  // 固定种子的组合变化可重复还原差异，验证候选排序、预算、任务优先及特殊模式交互。
  for (let i = 0; i < 100; i++) {
    const prefs = preferences.normalizeCompanionPreferences({ recommendationBudgetPolicy: ['block', 'warn', 'ignore'][random(3)], maxExtraIngredients: random(5), recipeVariantLimitPerBase: 1 + random(4), pinFavoriteRecipeEnabled: random(2) === 1, pinFavoriteBeverageEnabled: random(2) === 1 });
    const s = scenarios[random(scenarios.length)];
    const altered = { ...data, ingredients: ingredients.map(x => ({ ...x, price: random(10), tags: x.tags.concat(random(2) ? ['甜'] : []) })), recipes: recipes.map(x => ({ ...x, level: random(6), price: random(110), positiveTags: x.positiveTags.concat(random(2) ? ['目标甲', '目标乙'] : ['甜']), negativeTags: random(4) ? [] : ['鲜'] })), beverages: beverages.map(x => ({ ...x, level: random(6), price: random(80), tags: x.tags.concat(random(2) ? ['甜'] : []) })) };
    const currentOrder = { ...order, fund: random(240), remainingOrderCount: random(8), foodTag: ['鲜', '素', '肉', '目标甲'][random(4)], beverageTag: ['水果', '直饮', '甜'][random(3)], specialBusinessRole: roles[random(roles.length)] };
    const alteredRuntime = { ...runtime, ownedIngredientQty: Object.fromEntries(ingredients.map(x => [x.id, random(6) === 0 ? 0 : random(6) === 0 ? -1 : random(20)])), ownedBeverageQty: Object.fromEntries(beverages.map(x => [x.id, random(5) ? random(10) : -1])) };
    add('orders', { ...base, data: altered, runtime: alteredRuntime, dataSignature: `random-${i}`, preferences: prefs, specialBusiness: s, orders: [currentOrder], normalOrders: [{ ...normal, specialBusinessRole: currentOrder.specialBusinessRole }], usage: random(2) ? 'display' : 'automation' }, `random-order-${i}`);
  }
  const foods = [...new Map(baseline.recommendations[0].executionPlans.map(p => [p.food.key, p.food])).values()];
  const drinks = [...new Map(baseline.recommendations[0].executionPlans.map(p => [p.beverage.beverage.id, p.beverage])).values()];
  for (const limit of [0, 1, 5, 20]) for (const variantLimitPerBase of [0, 1, 3]) for (const budgetPolicy of ['block', 'warn', 'ignore']) {
    const args = { foods, beverages: drinks, options: { limit, variantLimitPerBase, budget: { remainingBudget: 30, source: 'unknown', willPayMoney: true }, budgetPolicy, sortProfile: base.preferences.recommendationSortProfile } };
    add('recipe-rows', args, `recipe-rows-${limit}-${variantLimitPerBase}-${budgetPolicy}`); add('beverage-rows', args, `beverage-rows-${limit}-${variantLimitPerBase}-${budgetPolicy}`);
  }
  // 连续使用同一个C#缓存，逐字段对比无缓存原实现；输出必须带当前订单，关键约束变化必须重算。
  for (const fixture of fixtures.filter(f => f.operation === 'orders' && (f.label === 'ordinary-order' || f.label.startsWith('special-order-')))) {
    for (const patch of [{}, { lastSeenAtUtc: 'latest-observation' }, { hasServedFood: true }, { hasServedBeverage: true }, { fund: 1 }, { automationAllowed: false }, { traceId: 'R-900', orderLifecycleSequence: 900 }]) {
      add('orders-cached', { ...fixture.args, orders: fixture.args.orders.map(order => ({ ...order, ...patch })) }, `cached-${fixture.label}-${JSON.stringify(patch)}`);
    }
  }
  const run = spawnSync('dotnet', ['tests/csharp-business-orders/bin/Release/net6.0/CSharpBusinessOrders.dll'], { input: fixtures.map(({ operation, args }) => JSON.stringify({ operation, args })).join('\n') + '\n', encoding: 'utf8', maxBuffer: 128 * 1024 * 1024, windowsHide: true });
  assert.equal(run.status, 0, run.stderr || run.error?.message);
  const lines = run.stdout.trim().split(/\r?\n/); assert.equal(lines.length, fixtures.length);
  let failed = 0;
  for (const [i, fixture] of fixtures.entries()) {
    const response = JSON.parse(lines[i]);
    try { assert.equal(response.ok, true, response.error); assert.deepStrictEqual(response.result, fixture.expected); }
    catch (error) { failed++; console.error(`FAIL ${i} ${fixture.label}: ${String(error.message).slice(0, 6000)}`); if (failed === 1) writeFileSync('tests/csharp-business-orders/last-failure.json', JSON.stringify({ fixture, actual: response }, null, 2)); if (failed >= 6) break; }
  }
  assert.equal(failed, 0, '订单领域存在 TS/C# 语义差异。');
  console.log(`PASS order/special-business/UI differential: ${fixtures.length} complete JSON cases; input immutability verified.`);

  // 页面缓存仅改变复用边界，不改变页面规则；每次用未缓存的真实生产函数作为同输入参照。
  // 同一个缓存进程连续接收输入变化，逐字段比较完整JSON，防止只按客人/地点复用过期结果。
  const pageBase = { query: { protocolVersion: 1, kind: 'rare', customerId: 3, foodTag: '鲜', beverageTag: '水果' },
    snapshot: { recommendationState: runtime, nightBusiness: { orders: [order] }, inputVersion: 1 },
    data, preferences: base.preferences, favorites: base.favorites, customRecipes: base.customRecipes };
  const pages = [{ label: 'base', args: pageBase },
    { label: 'observation-only', args: { ...pageBase, snapshot: { ...pageBase.snapshot, inputVersion: 99,
      snapshotSignature: 'changed', nightBusinessProgress: 8, nightBusinessAutomationAllowed: false,
      nightBusiness: { orders: [{ ...order, lastSeenAtUtc: 'latest', hasServedFood: true }] } } } }];
  for (const query of [{ ...pageBase.query, foodTag: '肉' }, { ...pageBase.query, beverageTag: '直饮' },
    { ...pageBase.query, customerId: 999 }, { protocolVersion: 1, kind: 'normal', selectedPlace: '妖怪兽道' },
    { protocolVersion: 1, kind: 'normal', selectedPlace: '不存在的地点' }]) pages.push({ label: 'query', args: { ...pageBase, query } });
  for (const changedRuntime of [...runtimeChanges,
    { ...runtime, ownedIngredientQty: { ...runtime.ownedIngredientQty, 1: 1 } },
    { ...runtime, ownedBeverageQty: { ...runtime.ownedBeverageQty, 21: 1 } },
    { ...runtime, popularFoodTag: '鲜', popularHateFoodTag: '肉', famousShopEnabled: true }]) {
    pages.push({ label: 'runtime', args: { ...pageBase, snapshot: { recommendationState: changedRuntime } } });
  }
  for (const changedData of [{ ...data, source: 'unavailable' },
    { ...data, recipes: recipes.map(r => ({ ...r, price: r.price + 50, positiveTags: ['肉'] })) },
    { ...data, ingredients: ingredients.map(i => ({ ...i, price: i.price + 10, tags: ['甜'] })) },
    { ...data, beverages: beverages.map(b => ({ ...b, price: b.price + 20, tags: ['直饮'] })) },
    { ...data, rareCustomers: [{ ...customer, positiveTags: ['肉'], negativeTags: ['素'] }] },
    { ...data, tagPriorityRules: [{ id: 1, tagIds: [], tags: ['肉', '素'] }] }]) {
    pages.push({ label: 'data', args: { ...pageBase, data: changedData } });
  }
  for (const patch of [{ maxExtraIngredients: 0 }, { recipeVariantLimitPerBase: 1 },
    { recommendationBudgetPolicy: 'block' }, { recommendationSortProfile: { preset: 'resources' } },
    { pinFavoriteRecipeEnabled: true, pinFavoriteBeverageEnabled: true }]) {
    pages.push({ label: 'preferences', args: { ...pageBase, preferences: preferences.normalizeCompanionPreferences(patch) } });
  }
  pages.push({ label: 'favorites', args: { ...pageBase, preferences: preferences.normalizeCompanionPreferences({
    pinFavoriteRecipeEnabled: true, pinFavoriteBeverageEnabled: true }), favorites: { ...base.favorites,
    recipes: [{ customerId: 3, foodTag: '鲜', recipeId: 101, extraIngredientIds: [3] }],
    beverages: [{ customerId: 3, beverageTag: '水果', beverageId: 23 }] } } });
  for (const enabled of [true, false]) pages.push({ label: 'custom', args: { ...pageBase,
    customRecipes: { ...base.customRecipes, enabled, recipes: [customEntry] } } });
  const pageRequests = pages.flatMap(({ args }) => ['page', 'page-cached', 'page-cached'].map(operation => ({ operation, args })));
  const pageRun = spawnSync('dotnet', ['tests/csharp-business-orders/bin/Release/net6.0/CSharpBusinessOrders.dll'], {
    input: pageRequests.map(request => JSON.stringify(request)).join('\n') + '\n', encoding: 'utf8',
    maxBuffer: 128 * 1024 * 1024, windowsHide: true });
  assert.equal(pageRun.status, 0, pageRun.stderr || pageRun.error?.message);
  const pageResponses = pageRun.stdout.trim().split(/\r?\n/).map(line => JSON.parse(line));
  assert.equal(pageResponses.length, pageRequests.length);
  for (const [index, fixture] of pages.entries()) {
    const [uncached, cached, warm] = pageResponses.slice(index * 3, index * 3 + 3);
    for (const result of [uncached, cached, warm]) assert.equal(result.ok, true, `${fixture.label}: ${result.error}`);
    assert.deepStrictEqual(cached.result, uncached.result, `${fixture.label}: cached page differs`);
    assert.deepStrictEqual(warm.result, uncached.result, `${fixture.label}: warm page differs`);
    assert.deepStrictEqual(Object.keys(warm.result).sort(), ['beverages', 'kind', 'recipes']);
    assert.equal(warm.cacheHits, cached.cacheHits + 1, `${fixture.label}: repeated page must hit final projection cache`);
    assert.equal(warm.cacheMisses, cached.cacheMisses, `${fixture.label}: repeated page must not rebuild candidates`);
    assert.ok(warm.cacheEntries <= 32 && warm.cacheBytes <= 32 * 1024 * 1024, 'All caches must share their bounded budget.');
  }
  assert.deepStrictEqual(pageResponses[4].result, pageResponses[1].result, 'Observation/permission changes cannot change pure page projection.');
  assert.equal(pageResponses[4].cacheMisses, pageResponses[2].cacheMisses, 'Unrelated snapshot facts must not invalidate pure page projection.');
  console.log(`PASS page projection cache differential: ${pages.length} input variants, ${pages.length * 2} complete JSON comparisons; shared bound and current-fact exclusion verified.`);
} finally { await vite.close(); }
