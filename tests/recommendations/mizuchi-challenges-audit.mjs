import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'vite';

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true },
  appType: 'custom',
  logLevel: 'silent',
});
let registryModule;
let recommendationModule;
let customRecipeModule;
let serviceModule;
let preferencesModule;
let automationModule;
let gameUiModule;
let dataModule;
try {
  [
    registryModule,
    recommendationModule,
    customRecipeModule,
    serviceModule,
    preferencesModule,
    automationModule,
    gameUiModule,
    dataModule,
  ] = await Promise.all([
    vite.ssrLoadModule('/src/companion/domain/special-business/registry.ts'),
    vite.ssrLoadModule('/src/recommendation-engine/index.ts'),
    vite.ssrLoadModule('/src/companion/domain/custom-recipes.ts'),
    vite.ssrLoadModule('/src/companion/domain/service-recommendations.ts'),
    vite.ssrLoadModule('/src/companion/preferences.ts'),
    vite.ssrLoadModule('/src/companion/domain/automation.ts'),
    vite.ssrLoadModule('/src/companion/domain/game-ui-targets.ts'),
    vite.ssrLoadModule('/src/lib/recommendation-data.ts'),
  ]);
} finally {
  await vite.close();
}

const {
  buildSpecialBusinessOrderRule,
  getSpecialBusinessOrderPriority,
} = registryModule;
const { buildRareFoodCandidates } = recommendationModule;
const { buildCustomFoodCandidates } = customRecipeModule;
const {
  buildOrderRecommendations,
  buildRecommendationRuntimeContext,
  createRecommendationCacheStore,
} = serviceModule;
const { normalizeCompanionPreferences } = preferencesModule;
const { selectOrderPreparationCandidates } = automationModule;
const { buildRareGameUiTarget } = gameUiModule;
const { buildRecommendationDataIndexes } = dataModule;

const baseIngredient = buildIngredient(11, '基础肉', ['下酒']);
const puyoyoFruit = buildIngredient(5002, '噗噗呦果', ['果味']);
const pepperWater = buildIngredient(5005, '辣椒水', ['辣']);
const recipe = buildRecipe(101, 201, '月都测试料理', [baseIngredient.name]);
const beverage = {
  id: 21,
  name: '月都测试酒水',
  description: '',
  tags: ['直饮'],
  dlc: 0,
  level: 1,
  price: 30,
  from: {},
};
const possessedCustomer = buildCustomer(901, '附身稀客');
const ordinaryCustomer = buildCustomer(902, '普通稀客');
const data = buildData({
  recipes: [recipe],
  ingredients: [baseIngredient, puyoyoFruit, pepperWater],
  customers: [possessedCustomer, ordinaryCustomer],
});
const runtime = buildRuntime(data);
const preferences = normalizeCompanionPreferences({
  filterMissingCookers: true,
  recommendationBudgetPolicy: 'warn',
  automationEnabled: true,
  autoRareOrderEnabled: true,
  autoPrepStartCooking: true,
  autoPrepTakeBeverage: true,
});
const specialBusiness = buildSpecialBusiness();
const possessedOrder = buildOrder({
  traceId: 'R-2',
  lifecycle: 2,
  deskCode: 2,
  customer: possessedCustomer,
  role: 'mizuchi-trial-possessed-order',
  firstSeenAtUtc: '2026-08-08T00:00:02.000Z',
});
const ordinaryOrder = buildOrder({
  traceId: 'R-1',
  lifecycle: 1,
  deskCode: 1,
  customer: ordinaryCustomer,
  role: 'mizuchi-trial-ordinary-order',
  firstSeenAtUtc: '2026-08-08T00:00:01.000Z',
});
const storyPossessedOrder = buildOrder({
  traceId: 'R-4',
  lifecycle: 4,
  deskCode: 4,
  customer: possessedCustomer,
  role: 'mizuchi-story-possessed-order',
  firstSeenAtUtc: '2026-08-08T00:00:04.000Z',
});
const storyOrdinaryOrder = buildOrder({
  traceId: 'R-3',
  lifecycle: 3,
  deskCode: 3,
  customer: ordinaryCustomer,
  role: 'mizuchi-story-ordinary-order',
  firstSeenAtUtc: '2026-08-08T00:00:03.000Z',
});

for (const challengeType of ['Story_Mizuchi_1', 'Story_Mizuchi_2', 'Story_Mizuchi_3']) {
  const context = { ...specialBusiness, challengeType };
  const possessedRule = buildSpecialBusinessOrderRule(
    context,
    'mizuchi-trial-possessed-order',
  );
  assert.deepEqual(possessedRule.requiredExtraIngredientIds, [5005]);
  assert.deepEqual(possessedRule.forbiddenExtraIngredientIds, []);
  assert.equal(possessedRule.requiresBaseOrderMatch, true);
  assert.equal(possessedRule.blockingReason, '');
  assert.equal(getSpecialBusinessOrderPriority(context, 'mizuchi-trial-possessed-order'), 0);
  assert.equal(getSpecialBusinessOrderPriority(context, 'mizuchi-trial-ordinary-order'), 1);
}

const ordinaryRule = buildSpecialBusinessOrderRule(
  specialBusiness,
  'mizuchi-trial-ordinary-order',
);
assert.deepEqual(ordinaryRule.requiredExtraIngredientIds, []);
assert.deepEqual(ordinaryRule.forbiddenExtraIngredientIds, [5005]);
assert.equal(ordinaryRule.requiresBaseOrderMatch, true);
assert.equal(ordinaryRule.blockingReason, '');
assert.match(
  buildSpecialBusinessOrderRule(
    specialBusiness,
    'mizuchi-trial-unverified-order',
  ).blockingReason,
  /身份尚未确认/,
);
assert.match(
  buildSpecialBusinessOrderRule(specialBusiness, 'unknown-role').blockingReason,
  /不属于当前已验证/,
);
const storySpecialBusiness = {
  ...specialBusiness,
  challengeType: 'Story_Mizuchi',
  displayName: '寻找瑞灵踪迹',
  category: 'mizuchi-story',
  requiredExtraIngredientIds: [5002],
};
const storyPossessedRule = buildSpecialBusinessOrderRule(
  storySpecialBusiness,
  'mizuchi-story-possessed-order',
);
assert.deepEqual(storyPossessedRule.requiredExtraIngredientIds, [5002]);
assert.deepEqual(storyPossessedRule.forbiddenExtraIngredientIds, []);
assert.equal(storyPossessedRule.requiresBaseOrderMatch, true);
assert.equal(storyPossessedRule.blockingReason, '');
assert.equal(
  getSpecialBusinessOrderPriority(storySpecialBusiness, 'mizuchi-story-possessed-order'),
  0,
);
const storyOrdinaryRule = buildSpecialBusinessOrderRule(
  storySpecialBusiness,
  'mizuchi-story-ordinary-order',
);
assert.deepEqual(storyOrdinaryRule.requiredExtraIngredientIds, []);
assert.deepEqual(storyOrdinaryRule.forbiddenExtraIngredientIds, [5002]);
assert.equal(storyOrdinaryRule.requiresBaseOrderMatch, true);
assert.match(
  buildSpecialBusinessOrderRule(
    storySpecialBusiness,
    'mizuchi-story-unverified-order',
  ).blockingReason,
  /身份尚未确认/,
);
assert.match(
  buildSpecialBusinessOrderRule(
    storySpecialBusiness,
    'mizuchi-trial-possessed-order',
  ).blockingReason,
  /不属于当前已验证的瑞灵场景/,
  'A trial role must not enter the Story Mizuchi rule.',
);
assert.match(
  buildSpecialBusinessOrderRule(
    { ...specialBusiness, requiredExtraIngredientIds: [5002] },
    'mizuchi-trial-possessed-order',
  ).blockingReason,
  /上下文不一致/,
  'The base-scene ingredient must not be accepted by a trial.',
);
assert.match(
  buildSpecialBusinessOrderRule(
    { ...storySpecialBusiness, requiredExtraIngredientIds: [5005] },
    'mizuchi-story-possessed-order',
  ).blockingReason,
  /上下文不一致/,
  'The trial ingredient must not be accepted by Story Mizuchi.',
);
assert.equal(
  getSpecialBusinessOrderPriority(
    { ...specialBusiness, active: false },
    'mizuchi-trial-possessed-order',
  ),
  0,
  'Trial priority must not affect ordinary business.',
);

const demand = {
  type: 'rare-tag-order',
  customer: possessedCustomer,
  requiredFoodTag: '下酒',
  requiredBeverageTag: '直饮',
};
const runtimeContext = buildRecommendationRuntimeContext(
  runtime,
  buildRuntimeSetsForAudit(runtime),
  preferences,
  data,
);
const requiredCandidates = buildRareFoodCandidates(data, demand, runtimeContext, {
  requiredExtraIngredientIds: [5005],
});
assert.ok(requiredCandidates.length > 0);
assert.ok(requiredCandidates.every((candidate) =>
  candidate.extraIngredients.filter((ingredient) => ingredient.id === 5005).length === 1
));
const storyRequiredCandidates = buildRareFoodCandidates(data, demand, runtimeContext, {
  requiredExtraIngredientIds: [5002],
});
assert.ok(storyRequiredCandidates.length > 0);
assert.ok(storyRequiredCandidates.every((candidate) =>
  candidate.extraIngredients.filter((ingredient) => ingredient.id === 5002).length === 1
));
const storyOrdinarySafeCandidates = buildRareFoodCandidates(data, demand, runtimeContext, {
  forbiddenExtraIngredientIds: [5002],
});
assert.ok(storyOrdinarySafeCandidates.length > 0);
assert.ok(storyOrdinarySafeCandidates.every((candidate) =>
  candidate.extraIngredients.every((ingredient) => ingredient.id !== 5002)
));
assert.equal(
  pepperWater.tags.includes('下酒'),
  false,
  'The mandatory ingredient fixture must be irrelevant to ordinary Tag reachability.',
);
const ordinarySafeCandidates = buildRareFoodCandidates(data, demand, runtimeContext, {
  forbiddenExtraIngredientIds: [5005],
});
assert.ok(ordinarySafeCandidates.length > 0);
assert.ok(ordinarySafeCandidates.every((candidate) =>
  candidate.extraIngredients.every((ingredient) => ingredient.id !== 5005)
));
assert.deepEqual(
  buildRareFoodCandidates(data, demand, runtimeContext, {
    requiredExtraIngredientIds: [5005],
    forbiddenExtraIngredientIds: [5005],
  }),
  [],
  'Overlapping required/forbidden Modifier identities must fail closed.',
);

for (const [label, variantData, variantContext] of [
  ['catalog-missing', { ...data, ingredients: [baseIngredient] }, runtimeContext],
  ['inventory-unavailable', data, {
    ...runtimeContext,
    availableIngredientIds: new Set([baseIngredient.id]),
  }],
  ['excluded', data, {
    ...runtimeContext,
    excludedIngredientIds: new Set([5005]),
  }],
  ['disabled', data, {
    ...runtimeContext,
    disabledIngredientIds: new Set([5005]),
  }],
  ['no-slot', {
    ...data,
    recipes: [{ ...recipe, ingredients: Array.from({ length: 5 }, () => baseIngredient.name) }],
  }, runtimeContext],
  ['base-ingredient-is-required-extra', {
    ...data,
    recipes: [{ ...recipe, ingredients: [baseIngredient.name, pepperWater.name] }],
  }, runtimeContext],
  ['recipe-forbids-required-extra', {
    ...data,
    recipes: [{ ...recipe, negativeTags: ['辣'] }],
  }, runtimeContext],
]) {
  assert.deepEqual(
    buildRareFoodCandidates(variantData, demand, variantContext, {
      requiredExtraIngredientIds: [5005],
    }),
    [],
    `Mandatory pepper-water planning must fail closed at ${label}.`,
  );
}
assert.deepEqual(
  buildRareFoodCandidates(data, demand, runtimeContext, {
    requiredExtraIngredientIds: [5005, 5005],
  }),
  [],
  'Duplicate mandatory ingredient identities must be rejected.',
);

const customRecipeBase = {
  id: 'mizuchi-custom',
  customerId: possessedCustomer.id,
  customerName: possessedCustomer.name,
  foodTag: '下酒',
  foodId: recipe.id,
  recipeId: recipe.recipeId,
  recipeName: recipe.name,
  enabled: true,
  pinToTop: true,
  sortOrder: 0,
  createdAtUtc: '',
  updatedAtUtc: '',
};
const customCandidateOptions = {
  data,
  customer: possessedCustomer,
  requiredFoodTag: '下酒',
  requiredBeverageTag: '直饮',
  requiredExtraIngredientIds: [5005],
  context: runtimeContext,
};
assert.deepEqual(buildCustomFoodCandidates({
  ...customCandidateOptions,
  customRecipes: {
    version: 1,
    enabled: true,
    recipes: [{ ...customRecipeBase, extraIngredientIds: [] }],
  },
}), [], 'A legacy custom recipe without the mandatory extra must not bypass the trial rule.');
const validCustomCandidates = buildCustomFoodCandidates({
  ...customCandidateOptions,
  customRecipes: {
    version: 1,
    enabled: true,
    recipes: [{ ...customRecipeBase, extraIngredientIds: [5005] }],
  },
});
assert.equal(validCustomCandidates.length, 1);
assert.deepEqual(validCustomCandidates[0].extraIngredients.map((ingredient) => ingredient.id), [5005]);
assert.deepEqual(buildCustomFoodCandidates({
  ...customCandidateOptions,
  requiredExtraIngredientIds: [],
  forbiddenExtraIngredientIds: [5005],
  customRecipes: {
    version: 1,
    enabled: true,
    recipes: [{ ...customRecipeBase, extraIngredientIds: [5005] }],
  },
}), [], 'An ordinary-order custom recipe must not add pepper water to Modifier.');

const recommendationResult = buildOrderRecommendations(
  [ordinaryOrder, possessedOrder],
  runtime,
  new Map([
    [possessedCustomer.id, possessedCustomer],
    [ordinaryCustomer.id, ordinaryCustomer],
  ]),
  createRecommendationCacheStore(),
  { version: 1, recipes: [], beverages: [] },
  { version: 1, enabled: true, recipes: [] },
  preferences,
  [],
  specialBusiness,
  [],
  data,
  { usage: 'automation' },
);
assert.deepEqual(
  recommendationResult.recommendations.map((item) => item.order.traceId),
  ['R-2', 'R-1'],
  'The possessed order must lead recommendation output even when it arrived later.',
);
const possessedRecommendation = recommendationResult.recommendations[0];
const ordinaryRecommendation = recommendationResult.recommendations[1];
assert.deepEqual(
  possessedRecommendation.executionPlans[0].food.extraIngredients.map((ingredient) => ingredient.id),
  [5005],
);
assert.deepEqual(
  possessedRecommendation.recipes[0].extraIngredients.map((ingredient) => ingredient.id),
  [5005],
  'The visible first recipe must remain the unique primary executable variant.',
);
assert.deepEqual(
  ordinaryRecommendation.executionPlans[0].food.extraIngredients.map((ingredient) => ingredient.id),
  [],
  'An ordinary trial order must explicitly keep pepper water out of Modifier.',
);
assert.match(ordinaryRecommendation.executionPlans[0].reasons.join('\n'), /禁止额外加料：辣椒水/);

const reverseRecommendations = [ordinaryRecommendation, possessedRecommendation];
const automationSelection = selectOrderPreparationCandidates(
  reverseRecommendations,
  { version: 1, recipes: [], beverages: [] },
  preferences,
  undefined,
  specialBusiness,
);
assert.equal(automationSelection.selections[0].item.order.traceId, 'R-2');
const indexes = buildRecommendationDataIndexes(data);
const uiTarget = buildRareGameUiTarget(
  reverseRecommendations,
  'ordered',
  '#ff0000',
  {
    listPinningEnabled: true,
    recipeVariantEnabled: true,
    cookerHighlightEnabled: true,
    seatHighlightEnabled: true,
    orderHighlightEnabled: true,
  },
  indexes,
  { specialBusiness },
);
assert.match(uiTarget.sourceOrderKey, /^R-2\|/);
assert.deepEqual(uiTarget.extraIngredientIds, [5005]);

const storyRecommendationResult = buildOrderRecommendations(
  [storyOrdinaryOrder, storyPossessedOrder],
  runtime,
  new Map([
    [possessedCustomer.id, possessedCustomer],
    [ordinaryCustomer.id, ordinaryCustomer],
  ]),
  createRecommendationCacheStore(),
  { version: 1, recipes: [], beverages: [] },
  { version: 1, enabled: true, recipes: [] },
  preferences,
  [],
  storySpecialBusiness,
  [],
  data,
  { usage: 'automation' },
);
assert.deepEqual(
  storyRecommendationResult.recommendations.map((item) => item.order.traceId),
  ['R-4', 'R-3'],
  'Story Mizuchi possessed order must lead recommendation output.',
);
const storyPossessedRecommendation = storyRecommendationResult.recommendations[0];
const storyOrdinaryRecommendation = storyRecommendationResult.recommendations[1];
assert.equal(
  storyPossessedRecommendation.executionPlans[0].food.extraIngredients
    .filter((ingredient) => ingredient.id === 5002).length,
  1,
  'Story possessed primary plan must contain exactly one Puyoyo fruit.',
);
assert.ok(
  storyOrdinaryRecommendation.executionPlans[0].food.extraIngredients
    .every((ingredient) => ingredient.id !== 5002),
  'Story ordinary primary plan must forbid Puyoyo fruit.',
);
assert.match(
  storyPossessedRecommendation.executionPlans[0].reasons.join('\n'),
  /强制加料：噗噗呦果/,
);
assert.match(
  storyOrdinaryRecommendation.executionPlans[0].reasons.join('\n'),
  /禁止额外加料：噗噗呦果/,
);
const storyAutomationSelection = selectOrderPreparationCandidates(
  [storyOrdinaryRecommendation, storyPossessedRecommendation],
  { version: 1, recipes: [], beverages: [] },
  preferences,
  undefined,
  storySpecialBusiness,
);
assert.equal(storyAutomationSelection.selections[0].item.order.traceId, 'R-4');
const storyUiTarget = buildRareGameUiTarget(
  [storyOrdinaryRecommendation, storyPossessedRecommendation],
  'ordered',
  '#ff0000',
  {
    listPinningEnabled: true,
    recipeVariantEnabled: true,
    cookerHighlightEnabled: true,
    seatHighlightEnabled: true,
    orderHighlightEnabled: true,
  },
  indexes,
  { specialBusiness: storySpecialBusiness },
);
assert.match(storyUiTarget.sourceOrderKey, /^R-4\|/);
assert.equal(storyUiTarget.extraIngredientIds.filter((id) => id === 5002).length, 1);

const ordinaryUiTarget = buildRareGameUiTarget(
  reverseRecommendations,
  'ordered',
  '#ff0000',
  {
    listPinningEnabled: true,
    recipeVariantEnabled: true,
    cookerHighlightEnabled: true,
    seatHighlightEnabled: true,
    orderHighlightEnabled: true,
  },
  indexes,
);
assert.match(
  ordinaryUiTarget.sourceOrderKey,
  /^R-1\|/,
  'Without the exact active trial context, the ordinary time ordering must remain unchanged.',
);

const [passiveSource, moduleSource] = await Promise.all([
  readFile(new URL('../../apps/companion/src/companion/domain/special-business/modules/passive-special-business.ts', import.meta.url), 'utf8'),
  readFile(new URL('../../apps/companion/src/companion/domain/special-business/modules/mizuchi-challenges.ts', import.meta.url), 'utf8'),
]);
assert.doesNotMatch(passiveSource, /Story_Mizuchi/);
assert.doesNotMatch(moduleSource, /startsWith|includes\(['"]Story_Mizuchi/);

console.log('PASS: Story Mizuchi and trials use strict scene roles, distinct mandatory ingredients, and shared possessed-order priority.');

function buildIngredient(id, name, tags) {
  return {
    id,
    name,
    description: '',
    type: '',
    tags,
    dlc: 0,
    level: 1,
    price: 10,
    from: {},
  };
}

function buildRecipe(id, recipeId, name, ingredients) {
  return {
    id,
    recipeId,
    name,
    description: '',
    ingredients,
    positiveTags: ['下酒'],
    negativeTags: [],
    cooker: '烧烤架',
    baseCookTime: 1,
    dlc: 0,
    level: 2,
    price: 120,
    from: {},
  };
}

function buildCustomer(id, name) {
  return {
    id,
    name,
    description: '',
    dlc: 0,
    places: [],
    price: [],
    enduranceLimit: 0,
    positiveTags: ['下酒'],
    negativeTags: [],
    beverageTags: ['直饮'],
    collection: false,
    evaluation: {},
    spellCards: { positive: [], negative: [] },
  };
}

function buildData({ recipes, ingredients, customers }) {
  return {
    recipes,
    ingredients,
    beverages: [beverage],
    normalCustomers: [],
    rareCustomers: customers,
    rareCustomerProfiles: customers.map((customer) => ({
      id: customer.id,
      name: customer.name,
      positiveTags: customer.positiveTags,
      negativeTags: customer.negativeTags,
      beverageTags: customer.beverageTags,
    })),
    foodTagIdMap: { 1: '下酒' },
    beverageTagIdMap: { 2: '直饮' },
    tagPriorityRules: [],
    source: 'runtime',
    status: 'test',
  };
}

function buildRuntime(dataSet) {
  return {
    availableRecipeIds: dataSet.recipes.map((item) => item.id),
    availableBeverageIds: [beverage.id],
    availableIngredientIds: dataSet.ingredients.map((item) => item.id),
    ownedIngredientQty: Object.fromEntries(dataSet.ingredients.map((item) => [item.id, 10])),
    ownedBeverageQty: { [beverage.id]: 10 },
    placedCookerTypeIds: [2],
    placedCookers: [{
      controllerIndex: 0,
      gridPosition: { x: 0, y: 0, z: 0 },
      controllerIdentity: '0x3000',
      typeIds: [2],
      typeNames: ['烧烤架'],
      name: '烧烤架',
      challengeLocked: false,
      couldOpen: true,
      automationAvailable: true,
      automationAvailability: 'StrictIdle',
      automationAvailabilityDiagnostic: 'mizuchi audit',
      source: 'test',
    }],
    placedCookerSnapshotComplete: true,
    placedCookerControllerCount: 1,
    placedCookerEmptyControllerCount: 0,
    placedCookerLockedControllerCount: 0,
    placedCookerReadFailureCount: 0,
    placedCookerStatus: 'test',
    popularFoodTag: null,
    popularHateFoodTag: null,
    famousShopEnabled: false,
  };
}

function buildRuntimeSetsForAudit(runtimeSnapshot) {
  return {
    recipeIds: new Set(runtimeSnapshot.availableRecipeIds),
    beverageIds: new Set(runtimeSnapshot.availableBeverageIds),
    ingredientIds: new Set(runtimeSnapshot.availableIngredientIds),
    unavailableIngredientIds: new Set(),
    ownedIngredientQty: runtimeSnapshot.ownedIngredientQty,
    ownedBeverageQty: runtimeSnapshot.ownedBeverageQty,
    placedCookerTypeIds: new Set(runtimeSnapshot.placedCookerTypeIds),
    placedCookerNames: new Set(['烧烤架']),
    usableCookerNames: new Set(['烧烤架']),
    runtimeUnavailableCookerNames: new Set(),
    hasCookerSnapshot: true,
  };
}

function buildSpecialBusiness() {
  return {
    active: true,
    challengeTypeAvailable: true,
    challengeType: 'Story_Mizuchi_1',
    displayName: '月都试炼 1',
    category: 'challenge',
    ruleSummary: '',
    foodTargetTags: [],
    beverageTargetTags: [],
    requiredExtraIngredientIds: [5005],
    yuumaFoodTargetRevision: 0,
    currentValue: 0,
    maxValue: 3,
    currentAnger: null,
    maxAnger: null,
    targetAnger: null,
    recommendationPolicy: 'mizuchi-trial',
    automationPolicy: 'strict-role',
    source: 'test',
    error: null,
  };
}

function buildOrder({ traceId, lifecycle, deskCode, customer, role, firstSeenAtUtc }) {
  return {
    traceId,
    orderLifecycleSequence: lifecycle,
    deskCode,
    guestId: customer.id,
    runtimeGuestId: customer.id,
    guestName: customer.name,
    specialBusinessRole: role,
    specialBusinessRoleLabel: role.endsWith('-possessed-order') ? '附身订单' : '普通订单',
    automationAllowed: true,
    automationBlockReason: '',
    foodTagId: 1,
    foodTag: '下酒',
    beverageTagId: 2,
    beverageTag: '直饮',
    source: 'test',
    firstSeenAtUtc,
    lastSeenAtUtc: firstSeenAtUtc,
    hasServedFood: false,
    hasServedBeverage: false,
  };
}
