/**
 * 搜索工作区复用的完整值树差分夹具。
 *
 * 预期值仍由固定的旧 TypeScript oracle 计算，不把新 C# 实现的输出写成期望。
 * 重点覆盖复用后最容易丢失的首次标签顺序、先压制后派生、双目标代表和末排列值；
 * 这里不以减少 beam 宽度、候选数量或忽略解释字段来降低测试成本。
 */
export function addSearchWorkspaceFixtures(add) {
  const rules = [
    { id: 1, tags: ['强', '强', '弱', '流行喜爱'] },
    { id: 2, tags: ['后', '强'] },
    { id: 3, tags: [' 目标甲 ', '目标乙'] },
    { id: 4, tags: ['目标丙', '目标甲'] },
  ];
  const rawVariants = [
    [' 弱 ', '强', '强', '', ' ', '后', '流行喜爱'],
    ['目标甲', '目标乙', '目标丙', ' 目标甲 '],
    ['招牌', '流行喜爱', '强', '素', '清淡'],
    ['流行厌恶', '招牌', '弱', '后', '强'],
  ];
  for (const [index, rawTags] of rawVariants.entries()) {
    add('resolveTagPriority', { rawTags, runtimeRules: rules }, `workspace-tags-${index}`);
    add('resolveTagPriority', { rawTags, runtimeRules: [...rules].reverse() }, `workspace-tags-reverse-${index}`);
  }

  const ingredient = (id, tags, price = 1) => ({
    id, name: `工作区食材${id}`, tags, price, level: 1,
    description: '', type: '', dlc: 0, from: {},
  });
  // 十四项可用加料使第三层真实超过 64 个状态，保证代表选择和稳定截断都被执行。
  // 重复、空白及相互压制的标签不会被夹具预先清洗，而交由原算法按原顺序处理。
  const ingredients = [ingredient(1, ['基础']), ...Array.from({ length: 14 }, (_, index) => ingredient(index + 2,
    [['素', '弱', ' 目标甲 '], ['后', '目标乙'], ['强', '清淡'], ['招牌', '目标丙'],
      ['流行喜爱', '鲜'], ['流行厌恶', '弱'], ['目标甲', '目标乙', '']][index % 7], index % 3 + 1))];
  const recipe = (id, tags, names = ['工作区食材1']) => ({
    id, recipeId: id + 100, name: `工作区料理${id}`, ingredients: names,
    positiveTags: tags, negativeTags: [], price: 19, level: 1, cooker: '煮锅',
    baseCookTime: 1, description: '', dlc: 0, from: {},
  });
  const recipes = [recipe(101, [' 弱 ', '招牌', '流行喜爱']), recipe(102, ['目标乙', '小巧'])];
  const customer = { id: 31, name: '工作区客人', positiveTags: ['素', '鲜', '清淡', '流行喜爱', '大份'],
    negativeTags: ['弱', '流行厌恶'], beverageTags: [], places: [], description: '', dlc: 0,
    price: [], enduranceLimit: 0, collection: false, evaluation: {}, spellCards: { positive: [], negative: [] } };
  const data = { recipes, ingredients, beverages: [], normalCustomers: [], rareCustomers: [customer],
    rareCustomerProfiles: [], foodTagIdMap: {}, beverageTagIdMap: {}, tagPriorityRules: rules, source: 'runtime', status: 'test' };
  const context = { availableRecipeIds: recipes.map(value => value.id), availableIngredientIds: ingredients.map(value => value.id),
    availableBeverageIds: [], disabledIngredientIds: [], excludedIngredientIds: [], excludedBeverageIds: [],
    ownedIngredientQty: Object.fromEntries(ingredients.map(value => [value.id, value.id % 3 === 0 ? -1 : value.id % 6])),
    ownedBeverageQty: {}, placedCookerNames: ['煮锅'], hasCookerSnapshot: true, popularFoodTag: '招牌',
    popularHateFoodTag: '流行喜爱', famousShopEnabled: true, tagPriorityRules: rules, maxExtraIngredients: 3, filterMissingCookers: true };
  const demand = { type: 'rare-tag-order', customer, requiredFoodTag: '鲜', requiredBeverageTag: '',
    specialFoodTarget: { enforcement: 'require', match: 'all', tags: ['目标甲', '目标乙'] } };

  for (const famousShopEnabled of [false, true]) for (const popularHateFoodTag of ['', '流行喜爱']) {
    add('resolveFoodTags', { recipe: recipes[0], extraIngredients: ingredients.slice(1, 5),
      ...context, famousShopEnabled, popularHateFoodTag }, `workspace-popular-${famousShopEnabled}-${popularHateFoodTag}`);
  }
  for (const preserve of [false, true]) for (const match of ['any', 'all']) {
    const currentDemand = { ...demand, specialFoodTarget: { ...demand.specialFoodTarget, match } };
    const options = { preserveTwoTagSpecialTargetReachability: preserve };
    const candidates = add('buildRareFoodCandidates', { data, demand: currentDemand, context, options }, `workspace-beam-${preserve}-${match}`);
    add('diagnoseRareFoodCandidateSearch', { data, demand: currentDemand, context, generatedCandidates: candidates }, `workspace-diagnostic-${preserve}-${match}`);
    add('buildRareFoodCandidates', { data: { ...data, ingredients: [...ingredients].reverse(), recipes: [...recipes].reverse() },
      demand: currentDemand, context, options }, `workspace-source-order-${preserve}-${match}`);
  }
  for (const requiredExtraIngredientIds of [[2, 3], [3, 2]]) {
    add('buildRareFoodCandidates', { data, demand, context,
      options: { preserveTwoTagSpecialTargetReachability: true, requiredExtraIngredientIds } },
    `workspace-required-order-${requiredExtraIngredientIds.join('-')}`);
  }
  for (const tags of [[], ['目标甲'], ['目标甲', '目标甲'], ['目标甲', '目标乙', '目标丙']]) {
    add('buildRareFoodCandidates', { data, demand: { ...demand, specialFoodTarget: { ...demand.specialFoodTarget, tags } }, context,
      options: { preserveTwoTagSpecialTargetReachability: true } }, `workspace-target-count-${tags.join('-')}`);
  }
  // 原始目录同 ID 以末条为准；不得因投影复用而拿到前条标签/价格。可用集合不增加重复
  // ID，避免把 JSON 数组和 TypeScript Set 的输入归一化差异当成此次工作区优化的行为。
  add('buildRareFoodCandidates', { data: { ...data, ingredients: [ingredient(2, ['不应采用旧项'], 999), ...ingredients] },
    demand, context, options: { preserveTwoTagSpecialTargetReachability: true } }, 'workspace-duplicate-catalog-id-last-wins');
}
