import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createServer } from 'vite';

// 旧 TypeScript 仅在本离线测试作为 oracle 执行。生产客户端和 Mod 均不得以它作为失败回退。
// 集合在线路上统一使用数组，进入 oracle 时恢复 Set，保证两种实现读取相同的语义输入。
const vite = await createServer({ configFile: 'tests/reference/vite.config.ts' });
const modules = {};
try {
  for (const name of ['rare-orders', 'normal-coverage', 'tag-resolution', 'dynamic-food-tags', 'sort-profile', 'mission-recipe-priority', 'koishi-feed']) {
    Object.assign(modules, await vite.ssrLoadModule(`/src/recommendation-engine/${name}.ts`));
  }
  const support = {};
  Object.assign(support, await vite.ssrLoadModule('/src/lib/recommendation-data.ts'));
  for (const name of ['custom-recipes', 'cookers', 'service-recommendations', 'primary-execution-plan']) Object.assign(support, await vite.ssrLoadModule(`/src/companion/domain/${name}.ts`));
  const setKeys = new Set(['availableRecipeIds', 'availableIngredientIds', 'availableBeverageIds', 'disabledIngredientIds', 'excludedIngredientIds', 'excludedBeverageIds', 'placedCookerNames', 'favoriteRecipeKeys', 'favoriteBeverageIds', 'specialTargetFoodTags', 'specialTargetBeverageTags', 'recipeIds', 'ingredientIds', 'beverageIds', 'unavailableIngredientIds', 'usableCookerNames', 'runtimeUnavailableCookerNames']);
  const hydrate = (value, key = '') => setKeys.has(key) ? new Set(value ?? []) : Array.isArray(value) ? value.map(item => hydrate(item)) : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([name, item]) => [name, hydrate(item, name)])) : value;
  const encode = value => JSON.parse(JSON.stringify(value, (_key, item) => item instanceof Set ? [...item] : item instanceof Map ? Object.fromEntries(item) : item));
  const invoke = (operation, raw) => {
    const a = hydrate(structuredClone(raw));
    if (operation.startsWith('support.')) {
      const name = operation.slice(8);
      const fn = support[name];
      if (name === 'buildRecommendationDataSet') return fn(raw.runtimeData);
      if (name === 'getEffectiveCustomRecipeEntries') return fn(raw.customRecipes, raw.customerId, raw.foodTag);
      if (name === 'normalizeIdList') return fn(raw.ids);
      if (name === 'mergeCustomFoodCandidates') return fn(raw.foodCandidates, raw.customFoodCandidates);
      if (name === 'buildRuntimeSets') return fn(raw.runtime, raw.data);
      if (name === 'validateRecommendationCookerSnapshot' || name === 'buildAutomationCookerPool') return fn(raw.runtime);
      if (name === 'buildRecommendationCookerNameSet') return fn(a.runtimeSets, a.filterMissingCookers);
      if (name === 'findAvailableAutomationCookerSlot') return fn(raw.pool, raw.cookerKey, new Set(raw.unavailableControllerIndexes));
      if (name === 'resolveCookerTypeId') return fn(raw.value);
      if (name === 'buildRecommendationRuntimeContext') return fn(raw.runtime, a.runtimeSets, raw.preferences, raw.data, raw.options);
      if (name === 'buildRecommendationPlanSortContext') return fn(raw.favorites, raw.customerId, raw.foodTag, raw.beverageTag, raw.preferences);
      if (name === 'buildPrimaryExecutionPlanPolicy') return fn(raw.preferences, raw.automationAllowed);
      if (name === 'normalizePrimaryExecutionPlans') return fn(raw.plans, a.sortContext, raw.policy);
      if (name === 'getPrimaryExecutionPlan') return fn(raw.plans);
      return fn(a);
    }
    const fn = modules[operation];
    assert.equal(typeof fn, 'function', `缺少 TypeScript oracle: ${operation}`);
    if (operation === 'resolveTagPriority') return fn(a.rawTags, a.runtimeRules ?? []);
    if (operation === 'findTagsThatCanSuppress') return fn(a.activeTags, a.tagsToSuppress, a.runtimeRules ?? []);
    if (operation === 'hasForbiddenIngredientTag') return fn(a.ingredient, a.recipe);
    if (operation === 'getNormalCustomersByPlace') return fn(a.data, a.place);
    if (operation === 'buildRareFoodCandidates') return fn(a.data, a.demand, a.context, a.options);
    if (operation === 'buildRareBeverageCandidates' || operation === 'diagnoseRareBeverageCandidateSearch') return fn(a.data, a.demand, a.context);
    if (operation === 'diagnoseRareFoodCandidateSearch') return fn(a.data, a.demand, a.context, a.generatedCandidates);
    if (operation === 'sortRareOrderPlans') return fn(a.plans, a.sortProfile, a.sortContext);
    if (operation.startsWith('compare')) return Math.sign(fn(a.left, a.right));
    if (operation === 'isMissionRecipeFoodCandidate') return fn(a.food, a.sortContext);
    if (operation === 'isMissionRecipeExecutionPlan') return fn(a.plan, a.sortContext);
    if (operation === 'buildDefaultRecommendationSortProfile') return fn(a.preset);
    return fn(a);
  };
  const fixtures = [];
  const add = (operation, args, label = '') => {
    const result = encode(invoke(operation, args));
    fixtures.push({ operation, args: structuredClone(args), expected: result, label: label || operation });
    return result;
  };

  const ingredient = (id, name, tags, price = id) => ({ id, name, tags, price, level: 1, description: '', type: '', dlc: 0, from: {} });
  const ingredients = [ingredient(1, '豆腐', ['素', '清淡'], 2), ingredient(2, '猪肉', ['肉'], 10), ingredient(3, '油', ['重油'], 3), ingredient(4, '辣椒', ['辣', '灼热'], 5), ingredient(5, '冰块', ['凉爽'], 1), ingredient(6, '米', ['饱腹'], 4), ingredient(7, '蘑菇', ['鲜', '下酒'], 8), ingredient(8, '蜂蜜', ['甜'], 12), ingredient(9, '特殊A', ['目标甲'], 7), ingredient(10, '特殊B', ['目标乙'], 9)];
  const recipe = (id, names, tags, price, level, negativeTags = [], cooker = '煮锅') => ({ id, recipeId: id + 100, name: `料理${id}`, ingredients: names, positiveTags: tags, negativeTags, price, level, cooker, baseCookTime: 1, description: '', dlc: 0, from: {} });
  const recipes = [recipe(11, ['豆腐'], ['素', '清淡'], 19, 1), recipe(12, ['猪肉', '米'], ['肉', '饱腹', '招牌'], 61, 3), recipe(13, ['豆腐', '豆腐', '米', '油'], ['不可加价', '小巧'], 100, 4), recipe(14, ['豆腐'], ['鲜'], 20, 2, ['肉']), recipe(15, ['猪肉'], ['下酒'], 60, 2, [], '烧烤架'), recipe(16, ['缺失材料'], ['甜'], 10, 1)];
  const beverages = [{ id: 21, name: '果酒', tags: ['水果', '低酒精'], price: 40, level: 2 }, { id: 22, name: '烈酒', tags: ['高酒精'], price: 120, level: 3 }, { id: 23, name: '水', tags: ['无酒精', '清淡'], price: 0, level: 0 }, { id: 24, name: '甜酒', tags: ['水果', '甜'], price: 80, level: 2 }].map(x => ({ ...x, description: '', dlc: 0, from: {} }));
  const customer = { id: 31, name: '客人', positiveTags: ['素', '鲜', '甜', '流行喜爱', '大份'], negativeTags: ['肉', '辣'], beverageTags: ['水果', '低酒精'], places: ['妖怪兽道'], description: '', dlc: 0, price: [], enduranceLimit: 0, collection: false, evaluation: {}, spellCards: { positive: [], negative: [] } };
  const data = { recipes, ingredients, beverages, normalCustomers: [customer, { ...customer, id: 32, name: '普客乙', positiveTags: ['肉', '饱腹'], beverageTags: ['高酒精'] }], rareCustomers: [customer], rareCustomerProfiles: [], foodTagIdMap: {}, beverageTagIdMap: {}, tagPriorityRules: [], source: 'runtime', status: 'test' };
  const context = { availableRecipeIds: recipes.map(x => x.id), availableIngredientIds: ingredients.map(x => x.id), availableBeverageIds: beverages.map(x => x.id), disabledIngredientIds: [], excludedIngredientIds: [], excludedBeverageIds: [], ownedIngredientQty: { 1: -1, 2: 0, 3: 2, 4: -2, 5: 5, 6: 10, 7: 1, 8: 20, 9: 5, 10: 5 }, ownedBeverageQty: { 21: -1, 22: 100, 23: 0, 24: 5 }, placedCookerNames: ['煮锅'], hasCookerSnapshot: true, popularFoodTag: '素', popularHateFoodTag: '肉', famousShopEnabled: true, tagPriorityRules: [], maxExtraIngredients: 4, filterMissingCookers: true, budget: { remainingBudget: 150, source: 'manual', willPayMoney: true }, budgetPolicy: 'block' };
  const demand = { type: 'rare-tag-order', customer, requiredFoodTag: '鲜', requiredBeverageTag: '水果' };

  add('resolveTagPriority', { rawTags: [' 素 ', '肉', '素', '', '清淡', '重油', '下酒', '饱腹'], runtimeRules: [] });
  add('resolveTagPriority', { rawTags: ['A', 'B', 'C'], runtimeRules: [{ id: 1, tags: ['B', 'A'] }, { id: 2, tags: ['C', 'B'] }] });
  add('resolveTagPriority', { rawTags: ['A', 'B'], runtimeRules: [{ id: 1, tags: ['A', 'A', 'B'] }] }, 'duplicate-strongest-priority-tag');
  add('findTagsThatCanSuppress', { activeTags: ['素', '清淡', '凉爽'], tagsToSuppress: ['素', '凉爽'], runtimeRules: [] });
  for (const price of [0, 19, 20, 60, 61]) for (const noPrice of [false, true]) {
    const r = { ...recipes[0], price, positiveTags: noPrice ? ['不可加价', '招牌'] : ['素', '招牌'] };
    add('buildDynamicFoodTags', { recipe: r, extraIngredients: ingredients.slice(0, 4) });
    add('resolveFoodTags', { recipe: r, extraIngredients: [ingredients[1], ingredients[2]], ...context });
  }
  add('hasForbiddenIngredientTag', { ingredient: ingredients[1], recipe: recipes[3] });
  for (const preset of ['balanced', 'resources', 'profit', 'simple', 'bad']) {
    if (preset !== 'bad') add('buildDefaultRecommendationSortProfile', { preset });
    add('normalizeRecommendationSortProfile', { preset, objectives: [{ key: 'extraCount', weight: 120, enabled: false, direction: 'desc' }, { key: 'extraCount', weight: -4.2 }, { key: 'profit', weight: 'bad', enabled: 1 }, { key: 'unknown', weight: 80 }] });
  }
  for (const place of ['妖怪兽道', '月之都']) {
    add('getNormalCustomersByPlace', { data, place });
    add('buildNormalFoodRecommendations', { data, place, context });
    add('buildNormalBeverageRecommendations', { data, place, context });
  }

  // 普通、未知、排除、必选/禁选加料、双标签可达性以及库存/预算边界的完整候选值树对照。
  const variants = [context, { ...context, filterMissingCookers: false }, { ...context, hasCookerSnapshot: false }, { ...context, maxExtraIngredients: 0 }, { ...context, availableIngredientIds: [1, 2] }, { ...context, excludedIngredientIds: [1], disabledIngredientIds: [7] }, { ...context, excludedBeverageIds: [21, 24] }, { ...context, availableRecipeIds: [] }, { ...context, availableBeverageIds: [] }, { ...context, tagPriorityRules: [{ id: 9, tags: ['鲜', '素'] }] }];
  for (const [i, c] of variants.entries()) {
    const foods = add('buildRareFoodCandidates', { data, demand, context: c }, `foods-${i}`);
    add('buildRareBeverageCandidates', { data, demand, context: c }, `drinks-${i}`);
    add('diagnoseRareFoodCandidateSearch', { data, demand, context: c, generatedCandidates: foods }, `food-diagnostic-${i}`);
    add('diagnoseRareBeverageCandidateSearch', { data, demand, context: c }, `drink-diagnostic-${i}`);
    add('buildRareOrderPlans', { data, customer, requiredFoodTag: demand.requiredFoodTag, requiredBeverageTag: demand.requiredBeverageTag, context: c, limit: 12 }, `plans-${i}`);
  }
  for (const foodTag of ['素', '大份', '未知']) for (const match of ['any', 'all']) {
    const specialDemand = { ...demand, requiredFoodTag: foodTag, specialFoodTarget: { enforcement: 'require', match, tags: ['目标甲', '目标乙'] } };
    for (const options of [{}, { preserveTwoTagSpecialTargetReachability: true }, { requiredExtraIngredientIds: [9], forbiddenExtraIngredientIds: [2] }, { requiredExtraIngredientIds: [9, 9] }, { requiredExtraIngredientIds: [-1] }, { forbiddenExtraIngredientIds: [2, 2] }, { requiredExtraIngredientIds: [9], forbiddenExtraIngredientIds: [9] }]) {
      add('buildRareFoodCandidates', { data, demand: specialDemand, context, options }, `special-${foodTag}-${match}`);
    }
  }
  const foods = invoke('buildRareFoodCandidates', { data, demand, context });
  const drinks = invoke('buildRareBeverageCandidates', { data, demand, context });
  for (const policy of ['block', 'warn', 'ignore']) for (const budget of [null, { source: 'unknown', remainingBudget: null }, { source: 'manual', remainingBudget: 0, willPayMoney: false }, { source: 'manual', remainingBudget: 101.8, willPayMoney: true }]) {
    add('buildRareOrderPlansFromCandidates', { data, customer, requiredFoodTag: '鲜', requiredBeverageTag: '水果', context: { ...context, budgetPolicy: policy, budget }, foodCandidates: foods.slice(0, 12), beverageCandidates: drinks, limit: 20 }, `budget-${policy}-${JSON.stringify(budget)}`);
  }
  const plans = encode(invoke('buildRareOrderPlansFromCandidates', { data, customer, requiredFoodTag: '鲜', requiredBeverageTag: '水果', context, foodCandidates: foods.slice(0, 12), beverageCandidates: drinks }));
  plans[2].food.customRecipePinned = true; plans[2].food.customRecipeSortOrder = 2;
  plans[4].food.customRecipePinned = true; plans[4].food.customRecipeSortOrder = 1;
  const contexts = [{}, { pinFavoriteRecipe: true, favoriteRecipeKeys: ['11:7'], pinFavoriteBeverage: true, favoriteBeverageIds: [24] }, { missionRecipeFoodId: 11, missionRecipeId: 111 }, { specialTargetFoodTags: ['鲜', '大份'], specialTargetBeverageTags: ['低酒精'] }, { specialPreferHighFoodLevel: true, specialPreferHighBeverageLevel: true }, { specialPreferDamageLevel: true }, { specialPreferDamageLevel: true, specialKoishiRemainingScore: 12, specialKoishiRemainingOrderCount: 2 }];
  for (const preset of ['balanced', 'resources', 'profit', 'simple']) for (const sortContext of contexts) add('sortRareOrderPlans', { plans, sortProfile: { preset }, sortContext });
  for (const remainingScore of [null, 0, 1, 12]) for (const remainingBudget of [null, 0, 100, 300]) for (const remainingOrderCount of [null, 0, 2]) {
    add('buildKoishiFeedPlanningInfo', { remainingScore, remainingBudget, remainingOrderCount });
    add('isKoishiFeedPlanSustainable', { remainingScore, remainingBudget, remainingOrderCount, estimatedPrice: 80, estimatedFeedScore: 5 });
  }
  for (const negativeMatches of [0, 1, 4]) for (const preferenceMatches of [0, 3, 6]) for (const foodLevel of [null, 0, 3]) {
    const score = { meetsRequiredFood: true, meetsRequiredBeverage: true, negativeMatches, preferenceMatches, foodLevel, beverageLevel: 2, foodPrice: 80, beveragePrice: 120, estimatedPrice: 200 };
    for (const operation of ['estimateKoishiBrokenShieldEvaluationScore', 'estimateKoishiBrokenShieldDamageLevel', 'estimateKoishiBrokenShieldFeedScore']) add(operation, score);
  }
  const order = { traceId: 'R-1', deskCode: 1, guestId: 31, runtimeGuestId: 3031, missionRecipePriority: { traceId: 'R-1', deskCode: 1, guestId: 31, runtimeGuestId: 3031, foodId: 11, recipeId: 111, missionGeneration: 1, businessGeneration: 2 } };
  add('getVerifiedMissionRecipeSortContext', order);
  for (const field of ['traceId', 'deskCode', 'guestId', 'runtimeGuestId', 'missionGeneration', 'businessGeneration', 'foodId', 'recipeId']) add('getVerifiedMissionRecipeSortContext', { ...order, missionRecipePriority: { ...order.missionRecipePriority, [field]: field === 'traceId' ? 'R-2' : -1 } });
  for (const plan of plans.slice(0, 8)) add('isMissionRecipeExecutionPlan', { plan, sortContext: { missionRecipeFoodId: 11, missionRecipeId: 111 } });

  const customEntry = (id, foodId, extraIngredientIds, overrides = {}) => ({ id, customerId: 31, customerName: ' 客人 ', foodTag: '鲜', foodId, recipeId: foodId + 100, recipeName: ` 料理${foodId} `, extraIngredientIds, enabled: true, pinToTop: true, sortOrder: 0, createdAtUtc: '', updatedAtUtc: '', ...overrides });
  const customRecipes = { version: 1, enabled: true, recipes: [customEntry('c-1', 11, [7]), customEntry('c-2', 11, [7], { sortOrder: 1 }), customEntry('c-3', 12, [8], { foodTag: null, pinToTop: false }), customEntry('c-4', 11, [9, 10], { foodTag: null }), customEntry('c-5', 13, [7, 8]), customEntry('c-6', 14, [2]), customEntry('c-7', 11, [1]), customEntry('c-8', 15, []), customEntry('c-9', 11, [999]), customEntry('c-10', 11, [8], { enabled: false }), customEntry('c-11', 11, [8], { foodTag: '甜' })] };
  add('support.normalizeIdList', { ids: [1, 1, -1, 1.9, 2.1, 2, 0, 10] });
  add('support.normalizeCustomRecipeData', customRecipes);
  add('support.normalizeCustomRecipeData', { version: 0, recipes: [customEntry('', 11, []), customEntry('valid', 11.9, [7.9, 7], { foodTag: '  ', sortOrder: 2.9 }), customEntry('negative', -1, [])] });
  for (const enabled of [true, false]) for (const foodTag of ['鲜', '甜', '未知']) add('support.getEffectiveCustomRecipeEntries', { customRecipes: { ...customRecipes, enabled }, customerId: 31, foodTag });
  for (const c of variants) for (const specialFoodTarget of [undefined, { enforcement: 'require', match: 'all', tags: ['目标甲', '目标乙'] }]) {
    const customFoods = add('support.buildCustomFoodCandidates', { customRecipes, data, customer, requiredFoodTag: '鲜', requiredBeverageTag: '水果', context: c, ...(specialFoodTarget ? { specialFoodTarget } : {}) });
    add('support.mergeCustomFoodCandidates', { foodCandidates: foods, customFoodCandidates: customFoods });
  }
  add('support.mergeCustomFoodCandidates', { foodCandidates: [foods[0], foods[0]], customFoodCandidates: [] }, 'empty-custom-preserves-standard-input');
  for (const constraints of [{ requiredExtraIngredientIds: [9] }, { requiredExtraIngredientIds: [9, 9] }, { forbiddenExtraIngredientIds: [7] }, { forbiddenExtraIngredientIds: [7, 7] }, { requiredExtraIngredientIds: [-1] }]) add('support.buildCustomFoodCandidates', { customRecipes, data, customer, requiredFoodTag: '鲜', requiredBeverageTag: '水果', context, ...constraints });

  const cooker = (index, typeIds, overrides = {}) => ({ controllerIndex: index, controllerIdentity: `0x${(4096 + index).toString(16).toUpperCase()}`, gridPosition: { x: index, y: 0, z: 0 }, typeIds, typeNames: typeIds.map(id => ({ 1: '煮锅', 2: '烧烤架', 3: '油锅', 4: '蒸锅', 5: '料理台' })[id]), name: typeIds.map(id => ({ 1: '煮锅', 2: '烧烤架', 3: '油锅', 4: '蒸锅', 5: '料理台' })[id]).join('/'), challengeLocked: false, couldOpen: true, automationAvailable: true, automationAvailability: 'StrictIdle', automationAvailabilityDiagnostic: 'test', source: 'test', ...overrides });
  const runtime = { ...context, placedCookerTypeIds: [1, 2, 3], placedCookers: [cooker(0, [1]), cooker(1, [1, 2]), cooker(2, [3])], placedCookerSnapshotComplete: true, placedCookerControllerCount: 3, placedCookerEmptyControllerCount: 0, placedCookerLockedControllerCount: 0, placedCookerReadFailureCount: 0, placedCookerStatus: 'test' };
  const preferences = { filterMissingCookers: true, recommendationBudgetPolicy: 'block', recommendationExclusions: { excludedIngredientIds: [2], excludedBeverageIds: [22] }, pinFavoriteRecipeEnabled: true, pinFavoriteBeverageEnabled: true };
  const runtimeVariants = [runtime, { ...runtime, placedCookerSnapshotComplete: false, placedCookers: [], placedCookerTypeIds: [], placedCookerReadFailureCount: 3 }, { ...runtime, placedCookerLockedControllerCount: 1, placedCookerControllerCount: 4 }, { ...runtime, availableRecipeIds: [11, 11, 12], availableBeverageIds: [21, 21], ownedIngredientQty: { '01': 9, '2': -1 } }];
  for (const value of runtimeVariants) {
    const sets = add('support.buildRuntimeSets', { runtime: value, data });
    add('support.validateRecommendationCookerSnapshot', { runtime: value });
    const pool = add('support.buildAutomationCookerPool', { runtime: value });
    for (const key of ['煮锅', '烧烤架', '油锅']) for (const unavailable of [[], [0], [1], [0, 1]]) add('support.findAvailableAutomationCookerSlot', { pool, cookerKey: key, unavailableControllerIndexes: unavailable });
    for (const filterMissingCookers of [true, false]) {
      add('support.buildRecommendationCookerNameSet', { runtimeSets: sets, filterMissingCookers });
      add('support.buildRecommendationRuntimeContext', { runtime: value, runtimeSets: sets, preferences: { ...preferences, filterMissingCookers }, data, options: { budget: context.budget } });
    }
  }
  for (const patch of [{ placedCookerTypeIds: [1, 1] }, { placedCookerControllerCount: -1 }, { placedCookerSnapshotComplete: false }, { placedCookerReadFailureCount: 1 }, { placedCookers: [cooker(0, [1]), cooker(0, [2]), cooker(2, [3])] }, { placedCookers: [cooker(0, [1]), cooker(1, [2], { controllerIdentity: '0x0' }), cooker(2, [3])] }, { placedCookers: [cooker(0, [1], { automationAvailable: false }), cooker(1, [2]), cooker(2, [3])] }]) {
    add('support.validateRecommendationCookerSnapshot', { runtime: { ...runtime, ...patch } });
    add('support.buildAutomationCookerPool', { runtime: { ...runtime, ...patch } });
  }
  for (const value of ['煮锅', ' 烤架 ', '锅', '炸锅', 'unknown', '']) add('support.resolveCookerTypeId', { value });
  const favorites = { recipes: [{ customerId: 31, foodTag: '鲜', recipeId: 11, extraIngredientIds: [7, 7, 8] }, { customerId: 31, foodTag: '甜', recipeId: 12, extraIngredientIds: [] }], beverages: [{ customerId: 31, beverageTag: '水果', beverageId: 24 }] };
  const favoriteContext = add('support.buildRecommendationPlanSortContext', { favorites, customerId: 31, foodTag: '鲜', beverageTag: '水果', preferences });
  for (const enabled of [true, false]) for (const allowed of [true, false]) for (const recipeOnly of [true, false]) for (const beverageOnly of [true, false]) {
    const policy = add('support.buildPrimaryExecutionPlanPolicy', { preferences: { automationEnabled: enabled, autoRareOrderEnabled: true, autoPrepStartCooking: true, autoPrepTakeBeverage: true, autoPrepRecipeFavoritesOnly: recipeOnly, autoPrepBeverageFavoritesOnly: beverageOnly }, automationAllowed: allowed });
    add('support.normalizePrimaryExecutionPlans', { plans, sortContext: { ...favoriteContext, missionRecipeFoodId: 11, missionRecipeId: 111 }, policy });
  }
  add('support.getPrimaryExecutionPlan', { plans });
  add('support.getPrimaryExecutionPlan', { plans: [] });

  // 原始游戏目录是共享业务输入：同时校验不完整快照、稀客画像与地点、重复材料及 JS 转换边界。
  const rawCatalog = { ...data, isComplete: true };
  for (const runtimeData of [null, {}, { ...rawCatalog, isComplete: false }, rawCatalog, { ...rawCatalog, status: '', source: '' }]) add('support.buildRecommendationDataSet', { runtimeData });
  for (const key of ['recipes', 'ingredients', 'beverages', 'normalCustomers', 'rareCustomers']) {
    add('support.buildRecommendationDataSet', { runtimeData: { ...rawCatalog, [key]: [], status: '' } }, `catalog-empty-${key}`);
    for (const invalid of [{ id: '1' }, { name: '' }, { name: 'missing' }, { name: '#未知' }, { name: '??' }, { name: ' 客人 ' }, { places: ['无效地点'] }]) add('support.buildRecommendationDataSet', { runtimeData: { ...rawCatalog, [key]: [...rawCatalog[key], { ...rawCatalog[key][0], ...invalid }] } }, `catalog-row-${key}-${JSON.stringify(invalid)}`);
  }
  for (const values of [[' 豆腐 ', '豆腐', '', null, true, 12], [{ x: 1 }, ['A', null, 'B'], [], false], ['流行喜爱', '流行厌恶', '素', '素']]) add('support.buildRecommendationDataSet', { runtimeData: { ...rawCatalog, recipes: [{ ...recipes[0], ingredients: values, positiveTags: values }], rareCustomers: [{ ...customer, positiveTags: values }, { ...customer, id: 777, places: [] }], foodTagIdMap: { ' 1 ': values[0], '2': values[1], '': 'x' } } }, 'catalog-string-conversion');
  for (const id of [null, '', '  ', true, -1, 2.9, '0x10', {}, [], ['2']]) add('support.buildRecommendationDataSet', { runtimeData: { ...rawCatalog, tagPriorityRules: [{ id, tagIds: [null, '', true, '2', 2.9, -1, {}, [], ['3'], '0x10'], tags: [' A ', 'A', 'B'] }, { tagIds: [1], tags: ['A'] }] } }, `catalog-rule-${JSON.stringify(id)}`);

  // 固定种子的随机矩阵补充组合覆盖；失败用例可以由编号和种子稳定重现，不依赖当前时间。
  let seed = 0x5eed1234;
  const random = maximum => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed % maximum; };
  const choose = values => values[random(values.length)];
  for (let index = 0; index < 72; index++) {
    const randomDemand = { ...demand, requiredFoodTag: choose(['鲜', '肉', '素', '大份', '昂贵', '招牌']), requiredBeverageTag: choose(['水果', '高酒精', '无酒精', '未知']) };
    if (index % 3 === 0) randomDemand.specialFoodTarget = { enforcement: 'require', match: choose(['all', 'any']), tags: choose([['目标甲', '目标乙'], ['素', '肉'], ['大份'], []]) };
    const randomContext = { ...context, availableRecipeIds: recipes.filter(() => random(4) !== 0).map(x => x.id), availableIngredientIds: ingredients.filter(() => random(5) !== 0).map(x => x.id), availableBeverageIds: beverages.filter(() => random(4) !== 0).map(x => x.id), excludedIngredientIds: ingredients.filter(() => random(8) === 0).map(x => x.id), excludedBeverageIds: beverages.filter(() => random(7) === 0).map(x => x.id), filterMissingCookers: random(2) === 0, hasCookerSnapshot: random(3) !== 0, maxExtraIngredients: random(6), budgetPolicy: choose(['block', 'warn', 'ignore']), budget: { source: 'manual', remainingBudget: choose([null, 0, 40, 100, 199.9, 300]), willPayMoney: choose([null, false, true]) } };
    const randomOptions = { preserveTwoTagSpecialTargetReachability: random(2) === 0 };
    const generated = add('buildRareFoodCandidates', { data, demand: randomDemand, context: randomContext, options: randomOptions }, `seed-food-${index}`);
    const generatedDrinks = add('buildRareBeverageCandidates', { data, demand: randomDemand, context: randomContext }, `seed-drink-${index}`);
    add('diagnoseRareFoodCandidateSearch', { data, demand: randomDemand, context: randomContext, generatedCandidates: generated }, `seed-diagnostic-${index}`);
    add('buildRareOrderPlansFromCandidates', { data, customer, requiredFoodTag: randomDemand.requiredFoodTag, requiredBeverageTag: randomDemand.requiredBeverageTag, ...(randomDemand.specialFoodTarget ? { specialFoodTarget: randomDemand.specialFoodTarget } : {}), context: randomContext, foodCandidates: generated.slice(0, 20), beverageCandidates: generatedDrinks, limit: 8, sortProfile: { preset: choose(['balanced', 'resources', 'profit', 'simple']) }, sortContext: choose(contexts) }, `seed-plans-${index}`);
  }

  const dll = process.env.CSHARP_RECOMMENDATIONS_DLL || 'tests/csharp-recommendations/bin/Release/net6.0/CSharpRecommendations.dll';
  const run = spawnSync('dotnet', [dll], { input: fixtures.map(({ operation, args }) => JSON.stringify({ operation, args })).join('\n') + '\n', encoding: 'utf8', maxBuffer: 128 * 1024 * 1024, windowsHide: true });
  assert.equal(run.status, 0, `C# 测试宿主失败：${run.stderr || run.error || ''}`);
  const lines = run.stdout.trim().split(/\r?\n/);
  assert.equal(lines.length, fixtures.length, 'C# 返回数量必须与 fixture 一致。');
  let failed = 0;
  for (const [index, fixture] of fixtures.entries()) {
    const response = JSON.parse(lines[index]);
    try {
      assert.equal(response.ok, true, response.error);
      assert.deepStrictEqual(response.result, fixture.expected);
    } catch (error) {
      failed++;
      console.error(`FAIL ${index} ${fixture.label}: ${String(error.message).slice(0, 5000)}`);
      if (failed >= 8) break;
    }
  }
  assert.equal(failed, 0, 'C# 与 TS 存在语义差异。');
  console.log(`PASS C# recommendation differential: ${fixtures.length} cases; full JSON equality; input immutability checked.`);
} finally {
  await vite.close();
}
