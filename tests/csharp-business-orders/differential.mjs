import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { createServer } from 'vite';

// 旧 TS 仅作为迁移期离线 oracle；生产不得引用测试宿主或以旧算法作隐式回退。
// 所有比较均检查完整 JSON 值树及纯函数输入不变性，覆盖推荐、特殊经营和 UI 精确身份。
const vite = await createServer({ configFile: 'tests/reference-client/vite.config.ts', server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom', logLevel: 'silent' });
try {
  const load = path => vite.ssrLoadModule(`/src/${path}.ts`);
  const [service, details, registry, ui, keys, preferences, indexes, engine, yuyuko, positive] = await Promise.all([
    load('companion/domain/service-recommendations'), load('companion/domain/normal-order-details'), load('companion/domain/special-business/registry'), load('companion/domain/game-ui-targets'), load('companion/domain/normal-order-key'), load('companion/preferences'), load('lib/recommendation-data'), load('recommendation-engine/index'), load('companion/domain/special-business/yuyuko-challenge'), load('companion/domain/special-business/yuyuko-positive-spell'),
  ]);
  const fixtures = [];
  const clean = value => JSON.parse(JSON.stringify(value));
  // 冻结 oracle 不修改：只在本测试边界移除已退出当前协议的排序选项。
  // 料理/酒水候选和评分继续逐字段对照旧实现；经营顶层行顺序按新固定规则独立验证。
  const normalizePreferences = value => {
    const result = preferences.normalizeCompanionPreferences(value);
    delete result.serviceOrderSortMode;
    return result;
  };
  // 冻结 TS 的算法与输出结构保持不动，仅在当前协议的已授权文案边界适配血池地狱四处说明。
  // 精确限制到目标选择的 reason/message 和普客详情对应字段：不能修改 rule、condition、
  // 标签数组、评分或任意业务输入，也不能省略实际结果的文案比较来掩盖行为变化。
  const yuumaDisplayText = text => {
    if (typeof text !== 'string') return text;
    if (text.startsWith('保持原订单料理与酒水，并同时满足')
      || text.startsWith('保持原订单料理与酒水；当前无法同时满足')) {
      return text.replaceAll('目标 Tag', '目标标签');
    }
    return text.replace('没有通过料理、酒水与厨具硬门禁的受控推进方案；当前目标 Tag：',
      '没有满足料理、酒水与厨具必要条件的受控推进方案；当前目标标签：');
  };
  const adaptYuumaDisplay = (operation, args, result) => {
    if (args.specialBusiness?.challengeType !== 'Story_BloodPondHell') return result;
    const selection = value => {
      value.message = yuumaDisplayText(value.message);
      if (value.target) value.target.reason = yuumaDisplayText(value.target.reason);
    };
    if (operation === 'normal') selection(result);
    // 缓存入口与未缓存入口返回同一完整协议；命中回归同样只适配这些展示字段，
    // 不能因使用共享缓存而漏掉文案迁移，也不能把实际结果或缓存计数纳入适配。
    if (operation === 'orders' || operation === 'orders-cached') {
      for (const value of result.normalExecutionTargets) selection(value);
      for (const value of result.normalOrderDetailPlans) {
        value.executionReason = yuumaDisplayText(value.executionReason);
        value.selectionMessage = yuumaDisplayText(value.selectionMessage);
      }
    }
    return result;
  };
  const compareDisplayOrder = (a, b) => {
    const seen = order => { const value = Date.parse(order.firstSeenAtUtc); return Number.isFinite(value) ? value : Number.MAX_SAFE_INTEGER; };
    const text = (a, b) => a < b ? -1 : a > b ? 1 : 0;
    return seen(a) - seen(b) || text(a.traceId ?? '', b.traceId ?? '')
      || (a.orderLifecycleSequence ?? 0) - (b.orderLifecycleSequence ?? 0)
      || text(a.orderKey ?? '', b.orderKey ?? '') || (a.deskCode ?? 0) - (b.deskCode ?? 0);
  };
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
      if (a.operation === 'rare') return ui.buildRareGameUiTarget(a.recommendations, 'ordered', a.color, a.features, indexes.buildRecommendationDataIndexes(a.data), a.options);
      if (a.operation === 'normal') return ui.buildNormalGameUiTarget(a);
      if (a.operation === 'reconcile') return ui.reconcileGameUiTarget(a.target, a.sources);
      return a.operation === 'rare-source' ? ui.buildRareGameUiTargetSource(a.order) : ui.buildNormalGameUiTargetSource(a.order);
    }
    const result = service.buildOrderRecommendations(a.orders, a.runtime, service.buildRareCustomerMap(a.data), service.createRecommendationCacheStore(), a.favorites, a.customRecipes, a.preferences, a.activeRareGuests, a.specialBusiness, a.specialBusinessRejectedRecipeKeys, a.data, { usage: a.usage });
    result.recommendations.sort((a, b) => compareDisplayOrder(a.order, b.order));
    result.recommendationIssues.sort((a, b) => compareDisplayOrder(a.order, b.order));
    const normalOrders = [...(a.normalOrders ?? [])].sort(compareDisplayOrder);
    const args = order => ({ order, specialBusiness: a.specialBusiness, runtime: a.runtime, preferences: a.preferences, data: a.data, dataSignature: a.dataSignature, rejectedRecipeKeys: a.specialBusinessRejectedRecipeKeys });
    return { ...result, normalOrderDetailPlans: a.includeNormalOrderDetails ? details.buildNormalOrderDetailPlans({ ...args(), orders: normalOrders }) : [], normalExecutionTargets: a.includeNormalExecutionTargets ? normalOrders.filter(o => !o.hasEvaluated).map(order => ({ orderKey: keys.buildNormalAutoOrderKey(order), ...registry.selectSpecialBusinessNormalExecutionTarget(args(order)) })) : [] };
  };
  const add = (operation, args, label = operation) => { const expected = adaptYuumaDisplay(operation, args, clean(oracle(operation, args))); fixtures.push({ operation, args: clean(args), expected, label }); return expected; };
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
  const base = { orders: [order], normalOrders: [normal], runtime, data, dataSignature: 'test', preferences: normalizePreferences({}), favorites: { version: 1, recipes: [], beverages: [] }, customRecipes: { version: 1, enabled: true, recipes: [] }, activeRareGuests: [], specialBusiness: null, specialBusinessRejectedRecipeKeys: [], includeNormalOrderDetails: true, includeNormalExecutionTargets: true, usage: 'display' };
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
  for (const policy of ['block', 'warn', 'ignore']) for (const fund of [0, 10, 70, 200]) add('orders', { ...base, preferences: normalizePreferences({ recommendationBudgetPolicy: policy }), orders: [{ ...order, fund }] }, `budget-${policy}-${fund}`);
  for (const foodTag of ['鲜', '未知', '肉']) for (const beverageTag of ['水果', '未知']) add('orders', { ...base, orders: [{ ...order, foodTag, beverageTag }] }, `tags-${foodTag}-${beverageTag}`);
  for (const pin of [false, true]) for (const favoritesOnly of [false, true]) add('orders', { ...base, favorites: { ...base.favorites, recipes: [{ customerId: 3, foodTag: '鲜', recipeId: 101, extraIngredientIds: [3] }], beverages: [{ customerId: 3, beverageTag: '水果', beverageId: 23 }] }, preferences: normalizePreferences({ pinFavoriteRecipeEnabled: pin, pinFavoriteBeverageEnabled: pin, automationEnabled: true, autoRareOrderEnabled: true, autoPrepStartCooking: true, autoPrepTakeBeverage: true, autoPrepRecipeFavoritesOnly: favoritesOnly, autoPrepBeverageFavoritesOnly: favoritesOnly }) }, `favorites-${pin}-${favoritesOnly}`);
  const customEntry = { id: 'test', customerId: 3, customerName: '橙', foodTag: '鲜', foodId: 101, recipeId: 201, recipeName: '料理1', extraIngredientIds: [3, 4], enabled: true, pinToTop: true, sortOrder: 0, createdAtUtc: '', updatedAtUtc: '' };
  add('orders', { ...base, customRecipes: { ...base.customRecipes, recipes: [customEntry] } }, 'pinned-custom');
  for (const reverse of [false, true]) {
    const orders = [{ ...order, traceId: 'R-2', deskCode: 2 }, { ...order, guestId: null, traceId: 'R-3', firstSeenAtUtc: '2026-10-09T09:00:00Z' }];
    add('orders', { ...base, orders: reverse ? orders.reverse() : orders }, `fixed-time-sorting-${reverse}`);
  }
  add('orders', { ...base, specialBusiness: { ...special, challengeType: 'Story_Mizuchi' }, orders: [
    { ...order, traceId: 'R-later', firstSeenAtUtc: '2026-10-09T10:01:00Z', specialBusinessRole: 'mizuchi-story-possessed-order' },
    { ...order, traceId: 'R-earlier', firstSeenAtUtc: '2026-10-09T10:00:00Z', specialBusinessRole: 'mizuchi-story-ordinary-order' },
  ] }, 'fixed-time-display-does-not-follow-special-execution-priority');
  const features = { recipeVariant: true, ingredientHighlight: true, beverageHighlight: true, cookerHighlight: true, seatHighlight: true };
  const target = add('game-ui', { operation: 'rare', recommendations: baseline.recommendations, data, color: '#ffffff', features, options: {} }, 'rare-target');
  const source = add('game-ui', { operation: 'rare-source', order });
  for (const patch of [{}, { hasServedFood: true }, { hasServedBeverage: true }, { hasServedFood: true, hasServedBeverage: true }, { terminal: true }, { sourceOrderSignature: 'other' }]) add('game-ui', { operation: 'reconcile', target, sources: [{ ...source, ...patch }] });
  add('game-ui', { operation: 'reconcile', target, sources: [source, source] }, 'ambiguous-source');
  for (const patch of [{}, { traceId: 'R-x' }, { orderLifecycleSequence: 0 }, { deskCode: -1 }]) add('game-ui', { operation: 'rare', recommendations: baseline.recommendations.map(r => ({ ...r, order: { ...r.order, ...patch } })), data, color: '#ffffff', features, options: {} }, 'rare-identity');
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
  for (const patch of [{}, { traceId: 'stale' }, { foodId: 999 }, { missionGeneration: 0 }]) for (const missionRecipePriorityEnabled of [false, true]) add('orders', { ...base, orders: [{ ...order, missionRecipePriority: { ...mission, ...patch } }], preferences: normalizePreferences({ missionRecipePriorityEnabled }) }, 'mission-primary');
  let seed = 0x4d595354;
  const random = maximum => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed % maximum; };
  // 固定种子的组合变化可重复还原差异，验证候选排序、预算、任务优先及特殊模式交互。
  for (let i = 0; i < 100; i++) {
    const prefs = normalizePreferences({ recommendationBudgetPolicy: ['block', 'warn', 'ignore'][random(3)], maxExtraIngredients: random(5), recipeVariantLimitPerBase: 1 + random(4), pinFavoriteRecipeEnabled: random(2) === 1, pinFavoriteBeverageEnabled: random(2) === 1 });
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
  // 价格下界优化覆盖空对侧、全硬失败、负价和未知预算：这些边界不能因预筛选而改变规则。
  for (const budgetPolicy of ['block', 'warn', 'ignore']) for (const budget of [null,
    { remainingBudget: 0, source: 'unknown', willPayMoney: true },
    { remainingBudget: null, source: 'unknown', willPayMoney: true },
    { remainingBudget: 30, source: 'unknown', willPayMoney: false }]) {
    for (const opposite of [[], drinks.map(d => ({ ...d, beverage: { ...d.beverage, price: -50 } })),
      drinks.map(d => ({ ...d, conditionResults: [{ status: 'fail', severity: 'hard' }] }))]) {
      add('recipe-rows', { foods, beverages: opposite,
        options: { limit: 20, variantLimitPerBase: 3, budget, budgetPolicy } }, 'pair-price-boundary');
    }
  }
  for (const fixture of fixtures.filter(f => f.operation === 'orders' && (f.label === 'ordinary-order' || f.label.startsWith('special-order-')))) {
    for (const patch of [{}, { lastSeenAtUtc: 'latest-observation' }, { hasServedFood: true }, { hasServedBeverage: true }, { fund: 1 }, { automationAllowed: false }, { traceId: 'R-900', orderLifecycleSequence: 900 }]) {
      add('orders-cached', { ...fixture.args, orders: fixture.args.orders.map(order => ({ ...order, ...patch })) }, `cached-${fixture.label}-${JSON.stringify(patch)}`);
    }
  }
  // 本桌预算必须取本轮活动稀客事实；无关到店/观察变化则不得使全部订单重算。
  // 在完整 TS oracle 比较之外检查相邻请求的命中计数，防止性能回归被相同输出掩盖。
  // 纯投影只消费真实目录，不消费外层 dataSignature。每个时序组用合成 data.status
  // 隔离前面已填充的等价条目；组内保持同一目录，不能给每一步新标记伪造必然失效。
  const cacheGroupData = group => ({ ...data, status: `cache-regression-${group}` });
  const guestCacheCases = [];
  const ownGuest = { guestId: 3, deskCode: 1, guestName: '橙', fund: 150, willPayMoney: true };
  const otherGuest = { guestId: 99, deskCode: 8, guestName: '其他客人', fund: 200, willPayMoney: true };
  for (const [label, guests, expectHit] of [
    ['guest-base', [ownGuest], false],
    ['unrelated-arrival', [ownGuest, otherGuest], true],
    ['unrelated-observation', [ownGuest, { ...otherGuest, fund: 180, lastSeenAtUtc: 'new' }], true],
    ['own-observation', [{ ...ownGuest, lastSeenAtUtc: 'new' }], true],
    ['own-budget', [{ ...ownGuest, fund: 10 }], false],
    ['own-payment', [{ ...ownGuest, fund: 10, willPayMoney: false }], false],
    ['guest-source-lost', [], false],
    ['unrelated-only', [otherGuest], true],
    ['desk-name-fallback', [{ ...ownGuest, guestId: null, fund: 25 }], false],
    ['desk-unique-fallback', [{ ...ownGuest, guestId: null, guestName: '未映射', fund: 25 }], true],
    ['desk-ambiguous', [{ ...ownGuest, guestId: null, guestName: '未映射', fund: 25 },
      { ...ownGuest, guestId: null, guestName: '另一个', fund: 30 }], false],
  ]) {
    add('orders-cached', { ...base, normalOrders: [], dataSignature: 'active-guest-cache-regression', data: cacheGroupData('active-guest'),
      includeNormalOrderDetails: false, includeNormalExecutionTargets: false, activeRareGuests: guests }, label);
    guestCacheCases.push({ index: fixtures.length - 1, expectHit, label });
  }
  // 缓存键归约后，必须重新核实每笔当前订单，再决定能否复用纯方案。以下各组保持
  // 同一个真实 C# 缓存进程，TS oracle 每次重新计算；同时检查命中/失效及当前 order，
  // 避免“输出看似相同”掩盖任务身份、收藏限定或特殊经营规划键遗漏。
  const semanticCacheCases = [];
  const addSemanticCacheCase = (label, args, expectation, missionActive) => {
    add('orders-cached', args, label);
    semanticCacheCases.push({ index: fixtures.length - 1, label, expectation, missionActive });
  };
  const semanticBase = { ...base, normalOrders: [], includeNormalOrderDetails: false, includeNormalExecutionTargets: false };
  const missionCacheBase = { ...semanticBase, dataSignature: 'semantic-mission-cache',
    data: cacheGroupData('semantic-mission'),
    preferences: normalizePreferences({ missionRecipePriorityEnabled: true }) };
  const firstMissionOrder = { ...order, traceId: 'R-mission-1', orderLifecycleSequence: 41 };
  const firstPriority = { ...mission, traceId: firstMissionOrder.traceId };
  const nextMissionOrder = { ...order, traceId: 'R-mission-2', orderLifecycleSequence: 42, deskCode: 2 };
  addSemanticCacheCase('semantic-mission-verified', { ...missionCacheBase,
    orders: [{ ...firstMissionOrder, missionRecipePriority: firstPriority }] }, 'first', true);
  addSemanticCacheCase('semantic-mission-new-trace-stale-priority', { ...missionCacheBase,
    orders: [{ ...nextMissionOrder, missionRecipePriority: firstPriority }] }, 'miss', false);
  addSemanticCacheCase('semantic-mission-new-trace-corrected-priority', { ...missionCacheBase,
    orders: [{ ...nextMissionOrder, missionRecipePriority: { ...firstPriority,
      traceId: nextMissionOrder.traceId, deskCode: nextMissionOrder.deskCode } }] }, 'hit', true);

  // 明确启用自动化与两个收藏限定，否则 automationAllowed 变化可能仍归约成相同的
  // 空策略。使用真实已有可执行方案作为收藏，确保测试确实走到合法方案而非缺目录分支。
  const favoritePlan = baseline.recommendations[0].executionPlans.find(plan =>
    plan.beverage.beverage.id !== pair.beverage.beverage.id) ?? baseline.recommendations[0].executionPlans.at(-1);
  assert.ok(favoritePlan, '收藏策略缓存夹具必须有合法执行方案。');
  const policyCacheBase = { ...semanticBase, dataSignature: 'semantic-favorite-policy-cache',
    data: cacheGroupData('semantic-favorite-policy'),
    preferences: normalizePreferences({ automationEnabled: true,
      autoRareOrderEnabled: true, autoPrepStartCooking: true, autoPrepTakeBeverage: true,
      autoPrepRecipeFavoritesOnly: true, autoPrepBeverageFavoritesOnly: true,
      pinFavoriteRecipeEnabled: false, pinFavoriteBeverageEnabled: false }),
    favorites: { ...base.favorites,
      recipes: [{ customerId: 3, foodTag: '鲜', recipeId: favoritePlan.food.recipe.id,
        extraIngredientIds: favoritePlan.food.extraIngredients.map(value => value.id) }],
      beverages: [{ customerId: 3, beverageTag: '水果', beverageId: favoritePlan.beverage.beverage.id }] } };
  for (const [suffix, allowed, expectation] of [['allowed', true, 'first'], ['not-allowed', false, 'miss'], ['allowed-again', true, 'hit']]) {
    addSemanticCacheCase(`semantic-favorite-policy-${suffix}`, { ...policyCacheBase,
      orders: [{ ...order, traceId: `R-policy-${suffix}`, automationAllowed: allowed }] }, expectation);
  }

  const koishiSpecial = { ...special, phase: 'Phase 3', wackyKoishiShieldBroken: true,
    maxValue: 100, targetValue: 12, currentValue: 8 };
  const koishiCacheBase = { ...semanticBase, dataSignature: 'semantic-koishi-planning-cache',
    data: cacheGroupData('semantic-koishi-planning'),
    specialBusiness: koishiSpecial };
  const koishiOrder = { ...order, specialBusinessRole: 'wacky-koishi-boss', fund: 150, remainingOrderCount: 3 };
  for (const [suffix, patch, specialPatch, expectation] of [
    ['base', {}, {}, 'first'],
    ['budget', { fund: 70 }, {}, 'miss'],
    ['attempts', { fund: 70, remainingOrderCount: 1 }, {}, 'miss'],
    ['remaining-score', { fund: 70, remainingOrderCount: 1 }, { targetValue: 90 }, 'miss'],
    ['free-known-fund', { fund: 70, remainingOrderCount: 1, isFreeOrder: true }, { targetValue: 90 }, 'miss'],
    ['unknown-budget', { fund: null, remainingOrderCount: 1, isFreeOrder: false }, { targetValue: 90 }, 'miss'],
    // 两者都无预算/规划金额时允许复用纯方案，但必须仍投影本轮不同的免费标志和 trace。
    ['free-unknown-budget', { fund: null, remainingOrderCount: 1, isFreeOrder: true }, { targetValue: 90 }, 'hit'],
  ]) {
    addSemanticCacheCase(`semantic-koishi-${suffix}`, { ...koishiCacheBase,
      specialBusiness: { ...koishiSpecial, ...specialPatch },
      orders: [{ ...koishiOrder, ...patch, traceId: `R-koishi-${suffix}` }] }, expectation);
  }
  // 食材压力等价仅影响纯投影缓存。完整TS结果验证20→19、无限库存与5的等价；
  // 穿过阈值、缺失、耗尽、可用集合、酒水数量必须仍按当前事实失效。
  // 同时覆盖自定义加料，因为它与普通搜索均消费库存压力。
  for (const customEnabled of [false, true]) {
    const pressureBase = { ...semanticBase, dataSignature: `ingredient-pressure-${customEnabled}`,
      data: cacheGroupData(`ingredient-pressure-${customEnabled}`),
      customRecipes: { version: 1, enabled: customEnabled, recipes: [{ id:'pressure', customerId:3,
        foodId:101, foodTag:null, extraIngredientIds:[3], sortOrder:0, pinToTop:true, enabled:true }] } };
    for (const [label, quantity, expectation, patch = {}] of [
      ['20',20,'first'], ['19',19,'hit'], ['5',5,'hit'], ['infinite',-1,'hit'],
      ['4',4,'miss'], ['0',0,'miss'], ['negative',-2,'miss'], ['missing',undefined,'miss'],
      ['restore',20,'hit'], ['availability',20,'miss',{availableIngredientIds:[1,2,4]}],
      ['beverage',20,'miss',{ownedBeverageQty:{21:2,22:10,23:20}}],
    ]) {
      const quantities={...runtime.ownedIngredientQty};
      if (quantity === undefined) delete quantities['3']; else quantities['3']=quantity;
      addSemanticCacheCase(`pressure-${customEnabled}-${label}`, { ...pressureBase,
        runtime:{...runtime,ownedIngredientQty:quantities,...patch} }, expectation);
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
  for (const { index, expectHit, label } of guestCacheCases.slice(1)) {
    const current = JSON.parse(lines[index]); const previous = JSON.parse(lines[index - 1]);
    if (expectHit) {
      assert.equal(current.cacheMisses, previous.cacheMisses, `${label}: unrelated facts must preserve order projection`);
      assert.equal(current.cacheHits, previous.cacheHits + 1, `${label}: must hit the final order projection`);
    }
    // 允许重新遇到先前完整预算时命中旧条目；完整JSON oracle已验证不能错用其它预算。
    assert.ok(current.cacheEntries <= 32 && current.cacheBytes <= 32 * 1024 * 1024, `${label}: bounded cache`);
  }
  for (const { index, label, expectation, missionActive } of semanticCacheCases) {
    const current = JSON.parse(lines[index]); const previous = JSON.parse(lines[index - 1]);
    const recommendation = current.result.recommendations[0];
    assert.ok(recommendation, `${label}: fixture must produce a recommendation projection`);
    assert.deepStrictEqual(recommendation.order, fixtures[index].args.orders[0], `${label}: cached projection must bind the exact current order`);
    if (expectation === 'hit') {
      assert.equal(current.cacheMisses, previous.cacheMisses, `${label}: equivalent verified planning input must reuse projection`);
      assert.equal(current.cacheHits, previous.cacheHits + 1, `${label}: exactly one final projection hit expected`);
    } else if (expectation === 'miss') {
      assert.ok(current.cacheMisses > previous.cacheMisses, `${label}: changed planning/permission input must invalidate projection`);
    }
    if (missionActive !== undefined) {
      assert.equal(recommendation.recipes.some(row => row.missionTarget === true), missionActive,
        `${label}: mission priority must be verified against this order before cache lookup`);
    }
    assert.ok(current.cacheEntries <= 32 && current.cacheBytes <= 32 * 1024 * 1024, `${label}: bounded cache`);
  }
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
    pages.push({ label: 'preferences', args: { ...pageBase, preferences: normalizePreferences(patch) } });
  }
  pages.push({ label: 'favorites', args: { ...pageBase, preferences: normalizePreferences({
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
