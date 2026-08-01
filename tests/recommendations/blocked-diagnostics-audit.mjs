import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'vite';

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true },
  appType: 'custom',
});

let serviceModule;
let preferencesModule;
try {
  [serviceModule, preferencesModule] = await Promise.all([
    vite.ssrLoadModule('/src/companion/domain/service-recommendations.ts'),
    vite.ssrLoadModule('/src/companion/preferences.ts'),
  ]);
} finally {
  await vite.close();
}

const {
  buildOrderRecommendations,
  createRecommendationCacheStore,
} = serviceModule;
const { normalizeCompanionPreferences } = preferencesModule;
const root = new URL('../../', import.meta.url);

const customer = {
  id: 3,
  name: '橙',
  description: '',
  dlc: 0,
  places: ['妖怪兽道'],
  price: [],
  enduranceLimit: 0,
  positiveTags: ['烧烤', '肉'],
  negativeTags: [],
  beverageTags: ['水果', '低酒精'],
  collection: false,
  evaluation: {},
  spellCards: { positive: [], negative: [] },
};
const ingredient = {
  id: 11,
  name: '猪肉',
  description: '',
  type: '',
  tags: ['肉'],
  dlc: 0,
  level: 1,
  price: 20,
  from: {},
};
const recipe = {
  id: 101,
  recipeId: 201,
  name: '测试烤肉',
  description: '',
  ingredients: ['猪肉'],
  positiveTags: ['烧烤', '肉'],
  negativeTags: [],
  cooker: '烧烤架',
  baseCookTime: 1,
  dlc: 0,
  level: 2,
  price: 120,
  from: {},
};
const beverage = {
  id: 21,
  name: '测试果酒',
  description: '',
  tags: ['水果', '低酒精'],
  dlc: 0,
  level: 2,
  price: 80,
  from: {},
};
const data = {
  recipes: [recipe],
  ingredients: [ingredient],
  beverages: [beverage],
  normalCustomers: [],
  rareCustomers: [customer],
  rareCustomerProfiles: [{
    id: customer.id,
    name: customer.name,
    positiveTags: customer.positiveTags,
    negativeTags: customer.negativeTags,
    beverageTags: customer.beverageTags,
  }],
  foodTagIdMap: { 1: '烧烤' },
  beverageTagIdMap: { 2: '水果' },
  tagPriorityRules: [],
  source: 'runtime',
  status: 'test',
};
const order = {
  traceId: 'R-DIAGNOSTIC',
  deskCode: 1,
  guestId: customer.id,
  runtimeGuestId: 3003,
  guestName: customer.name,
  specialBusinessRole: 'yuyuko-boss-order',
  automationAllowed: true,
  foodTagId: 1,
  foodTag: '烧烤',
  beverageTagId: 2,
  beverageTag: '水果',
  source: 'test',
  hasServedFood: false,
  hasServedBeverage: false,
};
const specialBusiness = {
  active: true,
  challengeTypeAvailable: true,
  challengeType: 'Challenge_Yuyuko',
  displayName: '幽幽子重修',
  category: 'boss',
  ruleSummary: '',
  foodTargetTags: [],
  beverageTargetTags: [],
  phase: 'Phase 2',
  currentAnger: null,
  maxAnger: null,
  targetAnger: null,
};
const favorites = { version: 1, recipes: [], beverages: [] };
const customRecipes = { version: 1, enabled: true, recipes: [] };

const missingCooker = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [5],
});
assert.equal(missingCooker.executionPlans.length, 0);
assert.equal(missingCooker.blockedDiagnostic?.code, 'food-cooker-missing');
assert.equal(missingCooker.blockedDiagnostic?.firstEmptyStage, 'food-cooker');
assert.deepEqual(missingCooker.blockedDiagnostic?.requiredCookerNames, ['烧烤架']);
assert.deepEqual(missingCooker.blockedDiagnostic?.placedCookerNames, ['料理台']);
assert.equal(
  missingCooker.blockedDiagnostic?.counts.foodRecipeEligibility
    .requiredTagReachableBaseIngredientsReady > 0,
  true,
  '缺厨具前必须已经存在满足 Tag 且基础材料可用的候选。',
);
assert.equal(
  missingCooker.blockedDiagnostic?.counts.foodRecipeEligibility.requiredTagReachableCookerReady,
  0,
);
assert.match(missingCooker.blockedMessages[0], /缺少可用厨具/);

const missingIngredient = build({
  availableIngredientIds: [],
  placedCookerTypeIds: [2],
});
assert.equal(missingIngredient.executionPlans.length, 0);
assert.equal(missingIngredient.blockedDiagnostic?.code, 'food-base-ingredient-missing');
assert.deepEqual(missingIngredient.blockedDiagnostic?.missingIngredientNames, ['猪肉']);

const unsupportedFoodTag = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [2],
  recommendationData: {
    ...data,
    recipes: [{ ...recipe, positiveTags: ['肉'] }],
  },
});
assert.equal(unsupportedFoodTag.blockedDiagnostic?.code, 'food-tag-not-supported');

const optionalSweetIngredient = {
  ...ingredient,
  id: 12,
  name: '砂糖',
  tags: ['甜'],
};
const unavailableRequiredTagAddIn = build({
  availableIngredientIds: [ingredient.id],
  orderOverrides: { foodTagId: 3, foodTag: '甜' },
  placedCookerTypeIds: [2],
  recommendationData: {
    ...data,
    ingredients: [ingredient, optionalSweetIngredient],
    recipes: [{ ...recipe, positiveTags: ['肉'] }],
  },
});
assert.equal(
  unavailableRequiredTagAddIn.blockedDiagnostic?.code,
  'food-required-tag-not-generated',
);
assert.equal(
  unavailableRequiredTagAddIn.blockedDiagnostic?.firstEmptyStage,
  'food-candidate-generation',
);

const lockedRecipe = build({
  availableIngredientIds: [ingredient.id],
  availableRecipeIds: [],
  placedCookerTypeIds: [2],
});
assert.equal(lockedRecipe.blockedDiagnostic?.code, 'food-recipe-locked');

const hatedFood = build({
  availableIngredientIds: [ingredient.id],
  customerOverrides: { negativeTags: ['肉'] },
  placedCookerTypeIds: [2],
});
assert.equal(hatedFood.blockedDiagnostic?.code, 'food-negative-tag');

const unavailableBeverage = build({
  availableBeverageIds: [],
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [2],
});
assert.equal(unavailableBeverage.blockedDiagnostic?.code, 'beverage-unavailable');

const excludedBeverage = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [2],
  preferenceOverrides: {
    recommendationExclusions: {
      excludedIngredientIds: [],
      excludedBeverageIds: [beverage.id],
    },
  },
});
assert.equal(excludedBeverage.blockedDiagnostic?.code, 'beverage-excluded');

const beverageTagMismatch = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [2],
  recommendationData: {
    ...data,
    beverages: [{ ...beverage, tags: ['低酒精'] }],
  },
});
assert.equal(beverageTagMismatch.blockedDiagnostic?.code, 'beverage-tag-mismatch');

const ordinaryLockedRecipe = build({
  availableIngredientIds: [ingredient.id],
  availableRecipeIds: [],
  placedCookerTypeIds: [2],
  specialBusinessContext: null,
});
assert.equal(ordinaryLockedRecipe.blockedDiagnostic?.code, 'food-recipe-locked');
assert.equal(ordinaryLockedRecipe.blockedDiagnostic?.firstEmptyStage, 'food-recipe-unlocked');

const ordinaryMissingIngredient = build({
  availableIngredientIds: [],
  placedCookerTypeIds: [2],
  specialBusinessContext: null,
});
assert.equal(
  ordinaryMissingIngredient.blockedDiagnostic?.code,
  'food-base-ingredient-missing',
);
assert.deepEqual(ordinaryMissingIngredient.blockedDiagnostic?.missingIngredientNames, ['猪肉']);

const ordinaryMissingCooker = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [5],
  specialBusinessContext: null,
});
assert.equal(ordinaryMissingCooker.blockedDiagnostic?.code, 'food-cooker-missing');
assert.deepEqual(ordinaryMissingCooker.blockedDiagnostic?.requiredCookerNames, ['烧烤架']);

const missingCookerPreferenceDisabled = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [5],
  preferenceOverrides: { filterMissingCookers: false },
  specialBusinessContext: null,
});
assert.equal(missingCookerPreferenceDisabled.blockedDiagnostic, null);
assert.equal(missingCookerPreferenceDisabled.executionPlans.length > 0, true,
  'A physically missing cooker remains governed by the existing user preference.');

const oneLockedOneOpen = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [2],
  cookerSnapshotOverrides: {
    placedCookers: [
      buildCooker(1, 2),
    ],
    placedCookerControllerCount: 2,
    placedCookerLockedControllerCount: 1,
  },
  preferenceOverrides: { filterMissingCookers: false },
  specialBusinessContext: null,
});
assert.equal(oneLockedOneOpen.blockedDiagnostic, null);
assert.equal(oneLockedOneOpen.executionPlans.length > 0, true,
  'A cooker type remains executable while at least one physical controller can open.');

const allLocked = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [],
  cookerSnapshotOverrides: {
    placedCookers: [],
    placedCookerControllerCount: 2,
    placedCookerLockedControllerCount: 2,
  },
  preferenceOverrides: { filterMissingCookers: false },
  specialBusinessContext: null,
});
assert.equal(allLocked.executionPlans.length, 0);
assert.equal(allLocked.blockedDiagnostic?.code, 'food-cooker-runtime-unavailable');
assert.deepEqual(allLocked.blockedDiagnostic?.placedCookerNames, []);
assert.deepEqual(allLocked.blockedDiagnostic?.usableCookerNames, []);
assert.deepEqual(allLocked.blockedDiagnostic?.runtimeUnavailableCookerNames, ['烧烤架']);
assert.match(allLocked.blockedDiagnostic?.message ?? '', /游戏机制锁定/);

const unlockedAfterLocked = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [2],
  cookerSnapshotOverrides: {
    placedCookers: [buildCooker(0, 2)],
  },
  preferenceOverrides: { filterMissingCookers: false },
  specialBusinessContext: null,
});
assert.equal(unlockedAfterLocked.blockedDiagnostic, null);
assert.equal(unlockedAfterLocked.executionPlans.length > 0, true,
  'A later complete snapshot with an open controller must restore the recommendation.');

const partialLockedRead = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [],
  cookerSnapshotOverrides: {
    placedCookers: [],
    placedCookerSnapshotComplete: false,
    placedCookerControllerCount: 2,
    placedCookerLockedControllerCount: 1,
    placedCookerReadFailureCount: 1,
  },
  specialBusinessContext: null,
});
assert.equal(partialLockedRead.blockedDiagnostic, null);
assert.equal(partialLockedRead.executionPlans.length > 0, true,
  'A diagnostic locked count in an unavailable snapshot must not infer a runtime-unavailable cooker type.');

const blockedBudget = build({
  availableIngredientIds: [ingredient.id],
  orderOverrides: { fund: 100, willPayMoney: true },
  placedCookerTypeIds: [2],
});
assert.equal(blockedBudget.blockedDiagnostic?.code, 'budget-unavailable');
assert.equal(blockedBudget.blockedDiagnostic?.remainingBudget, 100);
assert.equal(blockedBudget.blockedDiagnostic?.minimumPairPrice, 200);

const executable = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [2],
});
assert.equal(executable.blockedDiagnostic, null);
assert.equal(executable.executionPlans.length > 0, true);
assert.equal(executable.executionPlans[0].food?.recipe.id, recipe.id);
assert.equal(executable.executionPlans[0].beverage?.beverage.id, beverage.id);

const lowEvaluationData = {
  ...data,
  recipes: [{ ...recipe, positiveTags: ['烧烤'] }],
  beverages: [{ ...beverage, tags: ['水果'] }],
};
const lowEvaluation = build({
  availableIngredientIds: [ingredient.id],
  placedCookerTypeIds: [2],
  recommendationData: lowEvaluationData,
});
assert.equal(lowEvaluation.executionPlans.length, 0);
assert.equal(lowEvaluation.blockedDiagnostic?.code, 'special-evaluation-unmet');
assert.equal(lowEvaluation.blockedDiagnostic?.firstEmptyStage, 'special-evaluation');
assert.match(lowEvaluation.blockedDiagnostic?.message ?? '', /ExGood/);
assert.equal(lowEvaluation.blockedDiagnostic?.counts.plans.rawExecutable > 0, true);
assert.equal(lowEvaluation.blockedDiagnostic?.counts.plans.specialRuleSafe, 0);

const scaleIngredients = Array.from({ length: 61 }, (_, index) => ({
  ...ingredient,
  id: 10_000 + index,
  name: `性能材料${index}`,
  tags: [['烧烤', '肉', '甜', '水果'][index % 4]],
}));
const scaleRecipes = Array.from({ length: 163 }, (_, index) => ({
  ...recipe,
  id: 20_000 + index,
  recipeId: 30_000 + index,
  name: `性能配方${index}`,
  ingredients: [scaleIngredients[0].name],
}));
const scaleData = {
  ...data,
  ingredients: scaleIngredients,
  recipes: scaleRecipes,
};
const scaleRecipeIds = scaleRecipes.map((item) => item.id);
const scaleIngredientIds = scaleIngredients.map((item) => item.id);
const performanceStartedAt = performance.now();
const scaleLocked = build({
  availableIngredientIds: scaleIngredientIds,
  availableRecipeIds: [],
  placedCookerTypeIds: [2],
  recommendationData: scaleData,
});
const scaleMissingIngredient = build({
  availableIngredientIds: scaleIngredientIds.slice(1),
  availableRecipeIds: scaleRecipeIds,
  placedCookerTypeIds: [2],
  recommendationData: scaleData,
});
const scaleMissingCooker = build({
  availableIngredientIds: scaleIngredientIds,
  availableRecipeIds: scaleRecipeIds,
  placedCookerTypeIds: [5],
  recommendationData: scaleData,
});
const scaleDiagnosticElapsedMs = performance.now() - performanceStartedAt;
assert.equal(scaleLocked.blockedDiagnostic?.code, 'food-recipe-locked');
assert.equal(scaleMissingIngredient.blockedDiagnostic?.code, 'food-base-ingredient-missing');
assert.equal(scaleMissingCooker.blockedDiagnostic?.code, 'food-cooker-missing');
assert.ok(
  scaleDiagnosticElapsedMs < 1_000,
  `163 配方/61 材料的三类前置阻塞诊断不应执行 beam search，实际 ${scaleDiagnosticElapsedMs.toFixed(1)}ms`,
);

await assertSourceContracts();

console.log(
  `PASS: blocked recommendation diagnostics identify the first empty stage without changing executable plans (${scaleDiagnosticElapsedMs.toFixed(1)}ms at 163 recipes/61 ingredients).`,
);

function build({
  availableIngredientIds,
  availableBeverageIds = [beverage.id],
  availableRecipeIds = [recipe.id],
  customerOverrides = {},
  orderOverrides = {},
  placedCookerTypeIds,
  cookerSnapshotOverrides = {},
  preferenceOverrides = {},
  recommendationData = data,
  specialBusinessContext = specialBusiness,
}) {
  const effectiveCustomer = { ...customer, ...customerOverrides };
  const effectiveRecommendationData = {
    ...recommendationData,
    rareCustomerProfiles: [{
      id: effectiveCustomer.id,
      name: effectiveCustomer.name,
      positiveTags: effectiveCustomer.positiveTags,
      negativeTags: effectiveCustomer.negativeTags,
      beverageTags: effectiveCustomer.beverageTags,
    }],
  };
  const runtime = {
    availableRecipeIds,
    availableBeverageIds,
    availableIngredientIds,
    ownedIngredientQty: { [ingredient.id]: 10 },
    ownedBeverageQty: { [beverage.id]: 10 },
    ...buildCookerSnapshot(placedCookerTypeIds, cookerSnapshotOverrides),
    popularFoodTag: null,
    popularHateFoodTag: null,
    famousShopEnabled: false,
  };
  const result = buildOrderRecommendations(
    [{ ...order, ...orderOverrides }],
    runtime,
    new Map([[effectiveCustomer.id, effectiveCustomer]]),
    createRecommendationCacheStore(),
    favorites,
    customRecipes,
    normalizeCompanionPreferences({
      filterMissingCookers: true,
      recommendationBudgetPolicy: 'block',
      ...preferenceOverrides,
    }),
    [],
    specialBusinessContext,
    [],
    effectiveRecommendationData,
    { usage: 'automation' },
  );
  assert.equal(result.recommendations.length, 1);
  return result.recommendations[0];
}

function buildCookerSnapshot(typeIds, overrides = {}) {
  return {
    placedCookerTypeIds: typeIds,
    placedCookers: typeIds.map((typeId, controllerIndex) => buildCooker(controllerIndex, typeId)),
    placedCookerSnapshotComplete: true,
    placedCookerControllerCount: typeIds.length,
    placedCookerEmptyControllerCount: 0,
    placedCookerLockedControllerCount: 0,
    placedCookerReadFailureCount: 0,
    placedCookerStatus: 'test',
    ...overrides,
  };
}

function buildCooker(controllerIndex, typeId, overrides = {}) {
  const typeNames = new Map([
    [1, '煮锅'],
    [2, '烧烤架'],
    [3, '油锅'],
    [4, '蒸锅'],
    [5, '料理台'],
  ]);
  return {
    controllerIndex,
    gridPosition: { x: controllerIndex, y: 0, z: 0 },
    controllerIdentity: `0x${(0x2000 + controllerIndex).toString(16).toUpperCase()}`,
    typeIds: [typeId],
    typeNames: [typeNames.get(typeId)],
    name: typeNames.get(typeId),
    challengeLocked: overrides.couldOpen === false,
    couldOpen: true,
    automationAvailable: overrides.couldOpen !== false,
    automationAvailability: overrides.couldOpen === false ? 'Unavailable' : 'StrictIdle',
    automationAvailabilityDiagnostic: 'recommendation audit',
    source: 'test',
    ...overrides,
  };
}

async function assertSourceContracts() {
  const [service, automation, workbench, worker, rareOrders] = await Promise.all([
    readFile(new URL('apps/companion/src/companion/domain/service-recommendations.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/automation.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/workers/order-recommendations.worker.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/recommendation-engine/rare-orders.ts', root), 'utf8'),
  ]);

  assert.ok(service.includes('executionPlans.length === 0'),
    '结构化诊断只应在最终没有执行计划时构建。');
  assert.ok(service.includes('diagnoseRareFoodCandidateSearch('),
    '料理首次清零必须复用候选搜索诊断入口。');
  assert.ok(
    service.includes('preferences.filterMissingCookers || hasRuntimeUnavailableCookers'),
    '完整快照确认的运行时锁锅必须独立于缺失厨具设置进入硬过滤。',
  );
  assert.ok(
    service.includes("requiredFoodTag.trim()")
      && service.includes(": '当前订单'"),
    '运行时锁锅诊断不得为缺少点单 Tag 的订单生成空 Tag 文案。',
  );
  assert.ok(automation.includes("item.blockedDiagnostic?.message ?? '没有可用的推荐料理。'"),
    '自动化无料理目标时应优先显示结构化首个清零原因。');
  assert.ok(worker.includes("item.blockedDiagnostic?.stateSignature ?? ''"),
    'Worker 结果签名必须包含候选诊断状态。');
  assert.ok(workbench.includes('rareAutomationDecisionDiagnosticSignaturesRef'));
  assert.ok(workbench.includes('normalAutomationDecisionDiagnosticSignaturesRef'));
  assert.ok(
    workbench.includes('hashDiagnosticSignature(diagnostic.stateSignature)'),
    '自动化日志去重签名必须消费完整阻塞状态的短哈希。',
  );
  assert.equal(workbench.includes('lastAutomationDecisionDiagnosticSignatureRef'), false,
    '稀客和普客不得继续共用会互相覆盖的单一诊断签名。');

  const diagnosticStart = rareOrders.indexOf('export function diagnoseRareFoodCandidateSearch');
  const diagnosticEnd = rareOrders.indexOf('export function buildRareBeverageCandidates', diagnosticStart);
  assert.ok(diagnosticStart >= 0 && diagnosticEnd > diagnosticStart);
  const diagnosticSource = rareOrders.slice(diagnosticStart, diagnosticEnd);
  assert.equal(
    diagnosticSource.includes('buildFoodCandidatesForRecipe'),
    false,
    '前置阻塞诊断不得为未解锁、缺材料或缺厨具配方运行 beam search。',
  );
}
