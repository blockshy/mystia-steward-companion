import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'vite';
import {
  buildPrimaryExecutionPlanPolicy,
  getPrimaryExecutionPlan,
  normalizePrimaryExecutionPlans,
} from '../../apps/companion/src/companion/domain/primary-execution-plan.ts';

const root = new URL('../../', import.meta.url);
const vite = await createServer({
  configFile: new URL('../../apps/companion/vite.config.ts', import.meta.url).pathname,
  server: { middlewareMode: true },
  appType: 'custom',
  logLevel: 'silent',
});
let buildRareGameUiTarget;
let buildRareGameUiTargetSource;
let buildNormalGameUiTarget;
let buildNormalGameUiTargetSource;
let reconcileGameUiTarget;
try {
  ({
    buildNormalGameUiTarget,
    buildNormalGameUiTargetSource,
    buildRareGameUiTarget,
    buildRareGameUiTargetSource,
    reconcileGameUiTarget,
  } = await vite.ssrLoadModule('/src/companion/domain/game-ui-targets.ts'));
} finally {
  await vite.close();
}
const buildRareGameUiTargetForAudit = (recommendations, orderSortMode, indexes, options) =>
  buildRareGameUiTarget(
    recommendations,
    orderSortMode,
    '#FFDB2E',
    {
      listPinningEnabled: true,
      recipeVariantEnabled: true,
      cookerHighlightEnabled: true,
      seatHighlightEnabled: true,
      orderHighlightEnabled: true,
    },
    indexes,
    options,
  );
const basePreferences = {
  automationEnabled: true,
  autoRareOrderEnabled: true,
  autoPrepStartCooking: true,
  autoPrepTakeBeverage: true,
  autoPrepRecipeFavoritesOnly: true,
  autoPrepBeverageFavoritesOnly: true,
};

assert.deepEqual(buildPrimaryExecutionPlanPolicy(basePreferences), {
  requireRecipeFavorite: true,
  requireBeverageFavorite: true,
});
assert.deepEqual(buildPrimaryExecutionPlanPolicy(basePreferences, false), {
  requireRecipeFavorite: false,
  requireBeverageFavorite: false,
}, 'An order that forbids automation must retain its recommendation ordering.');
assert.deepEqual(buildPrimaryExecutionPlanPolicy({
  ...basePreferences,
  autoPrepStartCooking: false,
}), {
  requireRecipeFavorite: false,
  requireBeverageFavorite: true,
}, 'A disabled automation stage must not affect primary-plan ordering.');
assert.deepEqual(buildPrimaryExecutionPlanPolicy({
  ...basePreferences,
  automationEnabled: false,
}), {
  requireRecipeFavorite: false,
  requireBeverageFavorite: false,
}, 'Favorite-only settings must not affect display ordering while automation is disabled.');
assert.deepEqual(buildPrimaryExecutionPlanPolicy({
  ...basePreferences,
  autoRareOrderEnabled: false,
}), {
  requireRecipeFavorite: false,
  requireBeverageFavorite: false,
}, 'Rare favorite-only settings must not affect the primary plan while rare processing is disabled.');

const plans = Array.from({ length: 40 }, (_, index) => buildPlan(index + 1, index + 101));
const lateJointFavorite = plans[37];
const recipeKey = `${lateJointFavorite.food.recipe.id}:11,29`;
const normalized = normalizePrimaryExecutionPlans(plans, {
  favoriteRecipeKeys: new Set([recipeKey]),
  favoriteBeverageIds: new Set([lateJointFavorite.beverage.beverage.id]),
}, buildPrimaryExecutionPlanPolicy(basePreferences));
assert.equal(getPrimaryExecutionPlan(normalized), lateJointFavorite,
  'The eligible joint favorite must become the single primary plan.');
assert.ok(normalized.slice(0, 32).includes(lateJointFavorite),
  'Favorite normalization must happen before the automation plan limit is applied.');
assert.deepEqual(
  normalized.slice(1).map(planIdentity),
  plans.filter((plan) => plan !== lateJointFavorite).map(planIdentity),
  'Promoting the primary plan must preserve the relative order of all remaining plans.',
);

const splitFavorites = [
  buildPlan(1, 101),
  buildPlan(2, 102),
  buildPlan(3, 103),
  buildPlan(4, 104),
];
const jointOnly = normalizePrimaryExecutionPlans(splitFavorites, {
  favoriteRecipeKeys: new Set(['2:11,29', '4:11,29']),
  favoriteBeverageIds: new Set([103, 104]),
}, buildPrimaryExecutionPlanPolicy(basePreferences));
assert.equal(getPrimaryExecutionPlan(jointOnly), splitFavorites[3],
  'Recipe and beverage favorite restrictions must match the same executable plan.');

const noMatch = normalizePrimaryExecutionPlans(splitFavorites, {
  favoriteRecipeKeys: new Set(['99:']),
  favoriteBeverageIds: new Set([999]),
}, buildPrimaryExecutionPlanPolicy(basePreferences));
assert.deepEqual(noMatch.map(planIdentity), splitFavorites.map(planIdentity),
  'A missing favorite plan must not invent a fallback ordering.');

const missionPlans = Array.from({ length: 90 }, (_, index) =>
  buildPlan(index + 1, index + 1001, index + 501)
);
const lateMissionPlan = missionPlans[84];
const missionContext = {
  missionRecipeFoodId: lateMissionPlan.food.recipe.id,
  missionRecipeId: lateMissionPlan.food.recipe.recipeId,
};
const missionFirst = normalizePrimaryExecutionPlans(
  missionPlans,
  missionContext,
  { requireRecipeFavorite: false, requireBeverageFavorite: false },
);
assert.equal(getPrimaryExecutionPlan(missionFirst), lateMissionPlan,
  'The exact verified mission recipe must become the primary plan.');
assert.ok(missionFirst.slice(0, 80).includes(lateMissionPlan),
  'Mission normalization must happen before the display plan limit is applied.');
assert.deepEqual(
  missionFirst.slice(1).map(planIdentity),
  missionPlans.filter((plan) => plan !== lateMissionPlan).map(planIdentity),
  'Mission promotion must preserve the relative order of all other plans.',
);
const missionDisabled = normalizePrimaryExecutionPlans(
  missionPlans,
  {},
  { requireRecipeFavorite: false, requireBeverageFavorite: false },
);
assert.deepEqual(
  missionDisabled.map(planIdentity),
  missionPlans.map(planIdentity),
  'Disabling mission priority must preserve the original recommendation order.',
);

const sameFoodWrongRecipe = buildPlan(7, 107, 700);
const exactMissionRecipe = buildPlan(7, 108, 701);
const exactRecipeFirst = normalizePrimaryExecutionPlans(
  [sameFoodWrongRecipe, exactMissionRecipe],
  { missionRecipeFoodId: 7, missionRecipeId: 701 },
  { requireRecipeFavorite: false, requireBeverageFavorite: false },
);
assert.equal(getPrimaryExecutionPlan(exactRecipeFirst), exactMissionRecipe,
  'A matching food ID with a different recipe ID must not satisfy mission priority.');

for (const [description, validMissionPlan] of [
  ['food order Tag mismatch', buildPlan(7, 109, 701, { meetsRequiredFood: false })],
  ['rare-guest negative Tag', buildPlan(7, 111, 701, { matchedNegativeTags: ['厌恶'] })],
]) {
  const ordinaryPlan = buildPlan(8, 112, 702);
  const result = normalizePrimaryExecutionPlans(
    [ordinaryPlan, validMissionPlan],
    { missionRecipeFoodId: 7, missionRecipeId: 701 },
    { requireRecipeFavorite: false, requireBeverageFavorite: false },
  );
  assert.equal(getPrimaryExecutionPlan(result), validMissionPlan,
    `A verified mission recipe must bypass the ordinary ${description} evaluation gate.`);
}

const invalidMissionBeverage = buildPlan(7, 110, 701, { meetsRequiredBeverage: false });
const beverageFallback = buildPlan(8, 112, 702);
assert.equal(
  getPrimaryExecutionPlan(normalizePrimaryExecutionPlans(
    [beverageFallback, invalidMissionBeverage],
    { missionRecipeFoodId: 7, missionRecipeId: 701 },
    { requireRecipeFavorite: false, requireBeverageFavorite: false },
  )),
  beverageFallback,
  'Mission pinning must preserve the required beverage Tag gate.',
);

const cookerBlockedMission = buildPlan(7, 113, 701, {
  foodConditionResults: [hardFailure('food.cooker')],
});
const budgetBlockedMission = buildPlan(7, 114, 701, {
  planConditionResults: [hardFailure('plan.budget')],
});
for (const [description, blockedMission] of [
  ['missing cooker', cookerBlockedMission],
  ['blocking budget', budgetBlockedMission],
]) {
  const ordinaryPlan = buildPlan(8, 115, 702);
  assert.equal(
    getPrimaryExecutionPlan(normalizePrimaryExecutionPlans(
      [ordinaryPlan, blockedMission],
      { missionRecipeFoodId: 7, missionRecipeId: 701 },
      { requireRecipeFavorite: false, requireBeverageFavorite: false },
    )),
    ordinaryPlan,
    `Mission pinning must preserve the ${description} hard gate.`,
  );
}

const customPinnedPlan = buildPlan(8, 116, 702, { customRecipePinned: true });
const dislikedMissionPlan = buildPlan(7, 117, 701, {
  meetsRequiredFood: false,
  matchedNegativeTags: ['厌恶'],
});
const missionBeforePins = normalizePrimaryExecutionPlans(
  [customPinnedPlan, dislikedMissionPlan],
  {
    missionRecipeFoodId: 7,
    missionRecipeId: 701,
    pinFavoriteRecipe: true,
    favoriteRecipeKeys: new Set(['8:11,29']),
  },
  { requireRecipeFavorite: false, requireBeverageFavorite: false },
);
assert.equal(getPrimaryExecutionPlan(missionBeforePins), dislikedMissionPlan,
  'An executable verified mission recipe must outrank custom and ordinary favorite pins.');

const favoriteFallback = normalizePrimaryExecutionPlans(
  [lateMissionPlan, missionPlans[2]],
  {
    ...missionContext,
    favoriteRecipeKeys: new Set([`${missionPlans[2].food.recipe.id}:11,29`]),
    favoriteBeverageIds: new Set([missionPlans[2].beverage.beverage.id]),
  },
  { requireRecipeFavorite: true, requireBeverageFavorite: true },
);
assert.equal(getPrimaryExecutionPlan(favoriteFallback), missionPlans[2],
  'Mission priority must not bypass active recipe and beverage favorite-only policy.');

const favoriteMission = normalizePrimaryExecutionPlans(
  [missionPlans[2], lateMissionPlan],
  {
    ...missionContext,
    favoriteRecipeKeys: new Set([`${lateMissionPlan.food.recipe.id}:11,29`]),
    favoriteBeverageIds: new Set([lateMissionPlan.beverage.beverage.id]),
  },
  { requireRecipeFavorite: true, requireBeverageFavorite: true },
);
assert.equal(getPrimaryExecutionPlan(favoriteMission), lateMissionPlan,
  'An exact mission plan satisfying favorite-only policy must remain authoritative.');

assertRareGameUiTargetCompletionContracts();
assertNormalGameUiTargetContracts();
await assertSourceContracts();

console.log('PASS: one primary execution plan owns display, automation, pinning, mission recipe pinning, and favorite policy.');

function buildPlan(foodId, beverageId, recipeId = foodId, options = {}) {
  const foodConditionResults = options.foodConditionResults ?? [];
  const beverageConditionResults = options.beverageConditionResults ?? [];
  const planConditionResults = options.planConditionResults ?? [];
  return {
    bucket: options.bucket ?? 'complete',
    food: {
      recipe: { id: foodId, recipeId },
      extraIngredients: [{ id: 29 }, { id: 11 }, { id: 29 }],
      customRecipePinned: options.customRecipePinned ?? false,
      meetsRequiredFood: options.meetsRequiredFood ?? true,
      matchedNegativeTags: options.matchedNegativeTags ?? [],
      conditionResults: foodConditionResults,
    },
    beverage: {
      beverage: { id: beverageId },
      meetsRequiredBeverage: options.meetsRequiredBeverage ?? true,
      conditionResults: beverageConditionResults,
    },
    conditionResults: [
      ...foodConditionResults,
      ...beverageConditionResults,
      ...planConditionResults,
    ],
  };
}

function hardFailure(id) {
  return {
    id,
    target: 'plan',
    status: 'fail',
    severity: 'hard',
    label: id,
    detail: id,
  };
}

function assertRareGameUiTargetCompletionContracts() {
  const indexes = {
    ingredientByName: new Map([
      ['基础材料', { id: 11 }],
      ['额外材料', { id: 29 }],
    ]),
  };
  const firstPlan = buildRareTargetPlan(20, 120, 220);
  const secondPlan = buildRareTargetPlan(21, 121, 221);
  const servedFirst = buildRareTargetRecommendation('R-101', 1, firstPlan, {
    hasServedFood: true,
    hasServedBeverage: true,
  });
  const activeSecond = buildRareTargetRecommendation('R-102', 2, secondPlan);

  assert.equal(
    buildRareGameUiTargetForAudit([servedFirst], 'ordered', indexes),
    null,
    'A fully served order must not keep a game UI target.',
  );
  const nextTarget = buildRareGameUiTargetForAudit([servedFirst, activeSecond], 'ordered', indexes);
  assert.equal(nextTarget?.sourceOrderKey, 'R-102|lifecycle:2',
    'A fully served first order must be skipped in favor of the next actionable order.');
  assert.equal(nextTarget?.traceId, 'R-102',
    'A game UI target must carry the exact runtime order trace without a fallback identity.');
  assert.equal(nextTarget?.recipeId, secondPlan.food.recipe.recipeId,
    'The next actionable order must own the recipe target.');
  assert.equal(nextTarget?.beverageId, secondPlan.beverage.beverage.id,
    'The next actionable order must own the beverage target.');
  assert.deepEqual(nextTarget?.extraIngredientIds, [29, 29],
    'The target must preserve the primary plan extra ingredients separately from list highlighting IDs.');
  assert.equal(nextTarget?.deskCode, 2,
    'The target must carry the exact source order desk code for table highlighting.');

  const missingTrace = buildRareTargetRecommendation('', 1, firstPlan);
  const missingTraceTarget = buildRareGameUiTargetForAudit([missingTrace, activeSecond], 'ordered', indexes);
  assert.equal(missingTraceTarget?.traceId, 'R-102',
    'A missing runtime order trace must make that order ineligible instead of deriving an alias.');
  const opaqueTraceTarget = buildRareGameUiTargetForAudit([
    buildRareTargetRecommendation(' R-OPAQUE ', 1, firstPlan),
  ], 'ordered', indexes);
  assert.equal(opaqueTraceTarget, null,
    'An invalid trace must fail closed instead of being trimmed or normalized.');

  const activeSecondSource = buildRareGameUiTargetSource(activeSecond.order);
  assert.equal(
    reconcileGameUiTarget(nextTarget, [
      activeSecondSource,
      buildRareGameUiTargetSource(servedFirst.order),
    ]),
    nextTarget,
    'An unrelated order state must not invalidate the current target.',
  );

  const foodDeliveredDuringPending = reconcileGameUiTarget(nextTarget, [{
    ...activeSecondSource,
    hasServedFood: true,
  }]);
  assert.equal(foodDeliveredDuringPending?.recipeId, -1,
    'Pending reconciliation must remove a delivered recipe immediately.');
  assert.deepEqual(foodDeliveredDuringPending?.ingredientIds, []);
  assert.deepEqual(foodDeliveredDuringPending?.extraIngredientIds, []);
  assert.equal(foodDeliveredDuringPending?.cookerTypeId, -1);
  assert.equal(foodDeliveredDuringPending?.beverageId, secondPlan.beverage.beverage.id,
    'Pending reconciliation must retain an unserved beverage.');

  const beverageDeliveredDuringPending = reconcileGameUiTarget(nextTarget, [{
    ...activeSecondSource,
    hasServedBeverage: true,
  }]);
  assert.equal(beverageDeliveredDuringPending?.recipeId, secondPlan.food.recipe.recipeId,
    'Pending reconciliation must retain an unserved recipe.');
  assert.equal(beverageDeliveredDuringPending?.beverageId, -1,
    'Pending reconciliation must remove a delivered beverage immediately.');

  assert.equal(reconcileGameUiTarget(nextTarget, []), null,
    'A missing source order must clear the target.');
  assert.equal(reconcileGameUiTarget(nextTarget, [activeSecondSource, activeSecondSource]), null,
    'An ambiguous source order must clear the target.');
  assert.equal(reconcileGameUiTarget(nextTarget, [{
    ...activeSecondSource,
    sourceOrderSignature: `${activeSecondSource.sourceOrderSignature}|changed`,
  }]), null, 'An immutable source identity change must clear the target.');
  assert.equal(reconcileGameUiTarget(nextTarget, [{
    ...activeSecondSource,
    hasServedFood: true,
    hasServedBeverage: true,
  }]), null, 'A fully delivered source order must clear the target.');

  const foodServedTarget = buildRareGameUiTargetForAudit([
    buildRareTargetRecommendation('R-103', 1, firstPlan, { hasServedFood: true }),
  ], 'ordered', indexes);
  assert.equal(foodServedTarget?.sourceOrderKey, 'R-103|lifecycle:1');
  assert.equal(foodServedTarget?.recipeId, -1,
    'A served food component must not remain pinned.');
  assert.deepEqual(foodServedTarget?.ingredientIds, [],
    'A served food component must clear its ingredient targets.');
  assert.deepEqual(foodServedTarget?.extraIngredientIds, [],
    'A served food component must clear its automatic extra-ingredient targets.');
  assert.equal(foodServedTarget?.cookerTypeId, -1,
    'A served food component must clear its cooker target.');
  assert.equal(foodServedTarget?.beverageId, firstPlan.beverage.beverage.id,
    'An unserved beverage must remain targetable after food delivery.');

  const beverageServedTarget = buildRareGameUiTargetForAudit([
    buildRareTargetRecommendation('R-104', 1, firstPlan, { hasServedBeverage: true }),
  ], 'ordered', indexes);
  assert.equal(beverageServedTarget?.recipeId, firstPlan.food.recipe.recipeId,
    'An unserved food must remain targetable after beverage delivery.');
  assert.deepEqual(beverageServedTarget?.ingredientIds, [11, 29]);
  assert.deepEqual(beverageServedTarget?.extraIngredientIds, [29, 29]);
  assert.equal(beverageServedTarget?.cookerTypeId, 1);
  assert.equal(beverageServedTarget?.beverageId, -1,
    'A served beverage component must not remain pinned.');

  const foodOnlyPlan = { ...firstPlan, beverage: null };
  const noProjectableFirst = buildRareTargetRecommendation('R-105', 1, foodOnlyPlan, {
    hasServedFood: true,
  });
  assert.equal(
    buildRareGameUiTargetForAudit([noProjectableFirst, activeSecond], 'ordered', indexes)?.sourceOrderKey,
    'R-102|lifecycle:2',
    'A primary plan with no unserved projectable component must not block a later order.',
  );

  const ordinaryFirst = buildRareTargetRecommendation('R-106', 1, firstPlan);
  const servedMissionFood = buildRareTargetRecommendation('R-107', 2, secondPlan, {
    hasServedFood: true,
    mission: true,
  });
  assert.equal(
    buildRareGameUiTargetForAudit(
      [ordinaryFirst, servedMissionFood],
      'ordered',
      indexes,
      { prioritizeMissionRecipe: true },
    )?.sourceOrderKey,
    'R-106|lifecycle:1',
    'A delivered mission recipe must not retain cross-order mission priority.',
  );
  const activeMission = buildRareTargetRecommendation('R-108', 2, secondPlan, {
    mission: true,
  });
  assert.equal(
    buildRareGameUiTargetForAudit(
      [ordinaryFirst, activeMission],
      'ordered',
      indexes,
      { prioritizeMissionRecipe: true },
    )?.sourceOrderKey,
    'R-108|lifecycle:2',
    'An unserved verified mission recipe must retain cross-order priority.',
  );
}

function assertNormalGameUiTargetContracts() {
  const recipe = {
    id: 501,
    recipeId: 1501,
    name: '普客目标料理',
    description: '',
    ingredients: ['普客基础材料'],
    positiveTags: [],
    negativeTags: [],
    cooker: '煮锅',
    baseCookTime: 5,
    dlc: 0,
    level: 1,
    price: 10,
    from: {},
  };
  const data = {
    source: 'runtime',
    status: 'ok',
    recipes: [recipe],
    ingredients: [{
      id: 701,
      name: '普客基础材料',
      description: '',
      type: '',
      tags: [],
      dlc: 0,
      level: 1,
      price: 1,
      from: {},
    }],
    beverages: [{
      id: 601,
      name: '普客目标酒水',
      description: '',
      tags: [],
      dlc: 0,
      level: 1,
      price: 2,
      from: {},
    }],
    normalCustomers: [],
    rareCustomers: [],
    rareCustomerProfiles: [],
    foodTagIdMap: {},
    beverageTagIdMap: {},
    tagPriorityRules: [],
  };
  const order = buildNormalTargetOrder();
  const target = buildNormalGameUiTarget({
    orders: [order],
    executionTargets: [],
    executionTargetsCurrent: false,
    specialBusiness: null,
    businessGeneration: 9,
    color: '#5FACD3',
    features: {
      listPinningEnabled: true,
      recipeVariantEnabled: false,
      cookerHighlightEnabled: true,
      seatHighlightEnabled: false,
      orderHighlightEnabled: true,
    },
    data,
  });

  assert.equal(target?.kind, 'normal');
  assert.equal(target?.color, '#5FACD3');
  assert.equal(target?.traceId, 'N-201');
  assert.equal(target?.orderKey, 'ptr:abc');
  assert.equal(target?.orderLifecycleSequence, 12);
  assert.equal(target?.deskCode, 3);
  assert.equal(target?.recipeId, recipe.recipeId);
  assert.deepEqual(target?.ingredientIds, [701]);
  assert.equal(target?.beverageId, 601);
  assert.equal(target?.cookerTypeId, 1);

  assert.equal(buildNormalGameUiTarget({
    orders: [{ ...order, orderKey: 'ptr:ABC' }],
    executionTargets: [],
    executionTargetsCurrent: false,
    specialBusiness: null,
    businessGeneration: 9,
    color: '#5FACD3',
    features: {
      listPinningEnabled: true,
      recipeVariantEnabled: false,
      cookerHighlightEnabled: true,
      seatHighlightEnabled: false,
      orderHighlightEnabled: true,
    },
    data,
  }), null, 'A normal target must reject a non-canonical raw pointer key.');

  const source = buildNormalGameUiTargetSource(order);
  const foodServed = reconcileGameUiTarget(target, [{ ...source, hasServedFood: true }]);
  assert.equal(foodServed?.recipeId, -1);
  assert.deepEqual(foodServed?.ingredientIds, []);
  assert.equal(foodServed?.cookerTypeId, -1);
  assert.equal(foodServed?.beverageId, 601,
    'A normal target must retain its unserved beverage after food delivery.');
  assert.equal(reconcileGameUiTarget(target, [{ ...source, terminal: true }]), null,
    'An evaluated normal order must clear only its own target lane.');
}

function buildNormalTargetOrder(overrides = {}) {
  return {
    traceId: 'N-201',
    orderKey: 'ptr:abc',
    orderLifecycleSequence: 12,
    deskCode: 3,
    guestId: 41,
    runtimeGuestId: 41,
    guestName: '普客测试',
    specialBusinessRole: '',
    foodId: 501,
    foodName: '普客目标料理',
    beverageId: 601,
    beverageName: '普客目标酒水',
    hasServedFood: false,
    hasServedBeverage: false,
    readyToEvaluate: false,
    hasEvaluated: false,
    firstSeenAtUtc: '2026-08-06T00:00:00.000Z',
    source: 'capture',
    ...overrides,
  };
}

function buildRareTargetPlan(foodId, beverageId, recipeId) {
  const plan = buildPlan(foodId, beverageId, recipeId);
  return {
    ...plan,
    food: {
      ...plan.food,
      recipe: {
        id: foodId,
        recipeId,
        name: `料理 ${foodId}`,
        description: '',
        ingredients: ['基础材料'],
        positiveTags: [],
        negativeTags: [],
        cooker: '煮锅',
        baseCookTime: 1,
        dlc: 0,
        level: 1,
        price: 100,
        from: {},
      },
      extraIngredients: [{
        id: 29,
        name: '额外材料',
        description: '',
        type: '',
        tags: [],
        dlc: 0,
        level: 1,
        price: 1,
        from: {},
      }, {
        id: 29,
        name: '额外材料',
        description: '',
        type: '',
        tags: [],
        dlc: 0,
        level: 1,
        price: 1,
        from: {},
      }],
      extraIngredientReasonTags: {},
      activeTags: [],
      suppressedTags: [],
      matchedPositiveTags: [],
      matchedNegativeTags: [],
      matchedSpecialFoodTargetTags: [],
      baseCost: 1,
      extraCost: 1,
      resourcePressure: 0,
      cookerAvailable: true,
    },
    beverage: {
      ...plan.beverage,
      beverage: {
        id: beverageId,
        name: `酒水 ${beverageId}`,
        description: '',
        tags: [],
        dlc: 0,
        level: 1,
        price: 10,
        from: {},
      },
      activeTags: [],
      matchedTags: [],
      ownedQuantity: 1,
    },
  };
}

function buildRareTargetRecommendation(traceId, deskCode, plan, options = {}) {
  const guestId = 15;
  const runtimeGuestId = 15;
  return {
    order: {
      traceId,
      orderLifecycleSequence: deskCode,
      deskCode,
      guestId,
      runtimeGuestId,
      guestName: `稀客 ${deskCode}`,
      foodTagId: 1,
      foodTag: '甜',
      beverageTagId: 2,
      beverageTag: '清酒',
      source: 'test',
      firstSeenAtUtc: new Date(Date.UTC(2026, 0, 1, 0, deskCode)).toISOString(),
      hasServedFood: options.hasServedFood === true,
      hasServedBeverage: options.hasServedBeverage === true,
      missionRecipePriority: options.mission
        ? {
          traceId,
          deskCode,
          guestId,
          runtimeGuestId,
          foodId: plan.food.recipe.id,
          recipeId: plan.food.recipe.recipeId,
          missionGeneration: 1,
          businessGeneration: 1,
        }
        : null,
    },
    executionPlans: [plan],
    recipes: [],
    beverages: [],
  };
}

function planIdentity(plan) {
  return `${plan.food.recipe.id}:${plan.food.recipe.recipeId}/${plan.beverage.beverage.id}`;
}

async function assertSourceContracts() {
  const [
    service,
    automation,
    types,
    workbench,
    missionPriority,
    rareOrders,
    preferences,
    settings,
    shared,
    worker,
    gameUiTargets,
  ] = await Promise.all([
    readFile(new URL('apps/companion/src/companion/domain/service-recommendations.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/automation.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/types.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/recommendation-engine/mission-recipe-priority.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/recommendation-engine/rare-orders.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/preferences.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/pages/ModSettingsPanel.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/pages/shared.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/workers/order-recommendations.worker.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/game-ui-targets.ts', root), 'utf8'),
  ]);

  const normalizeIndex = service.indexOf('normalizePrimaryExecutionPlans(');
  const truncateIndex = service.indexOf('executionPlans: executionPlans.slice(0, executionPlanLimit)');
  assert.ok(normalizeIndex >= 0 && truncateIndex > normalizeIndex,
    'Primary favorite normalization must happen before execution-plan truncation.');
  assert.ok(service.includes('projectPrimaryExecutionPlanRows('),
    'The primary plan must be projected into both display lists.');
  assert.ok(
    service.includes('const missionExecutionPair = findMissionExecutionPair(')
      && service.includes('missionExecutionPair?.food ?? null')
      && service.includes('missionExecutionPair?.beverage ?? null')
      && service.includes('...limited.filter((food) => food !== missionCandidate)')
      && service.includes('...limited.filter((beverage) => beverage !== missionCandidate)'),
    'One exact legal mission food/beverage pair must survive both candidate limits without bypassing primary policy.',
  );
  for (const gate of [
    "plan.bucket !== 'blocked'",
    'isMissionRecipeFoodCandidate(plan.food, sortContext)',
    'hasNoHardFailures(plan.food.conditionResults)',
    'hasNoHardFailures(plan.beverage.conditionResults)',
    'hasNoHardFailures(plan.conditionResults)',
    'plan.beverage.meetsRequiredBeverage',
  ]) {
    assert.ok(
      missionPriority.includes(gate),
      `The shared mission execution predicate is missing gate: ${gate}`,
    );
  }
  assert.equal(missionPriority.includes('plan.food.meetsRequiredFood'), false,
    'Mission pinning must not require the mission food to match the ordinary order Tag.');
  assert.equal(missionPriority.includes('plan.food.matchedNegativeTags'), false,
    'Mission pinning must not reject the exact mission food because of ordinary guest dislike Tags.');
  const missionPair = functionSlice(service, 'findMissionExecutionPair', 'selectExecutionBeverageCandidates');
  assert.ok(
    missionPair.includes('isMissionRecipeFoodCandidate(food, sortContext)')
      && missionPair.includes('candidateHasNoHardFailures(food.conditionResults)')
      && missionPair.includes('candidateHasNoHardFailures(beverage.conditionResults)')
      && missionPair.includes('beverage.meetsRequiredBeverage')
      && missionPair.includes('primaryPolicy.requireRecipeFavorite')
      && missionPair.includes('primaryPolicy.requireBeverageFavorite')
      && missionPair.includes('canPairFoodWithinBudget('),
    'Mission candidate discovery must retain hard-failure, beverage, favorite-only, and budget gates.',
  );
  assert.equal(missionPair.includes('food.meetsRequiredFood'), false,
    'Mission candidate discovery must not require the ordinary food order Tag.');
  assert.equal(missionPair.includes('food.matchedNegativeTags'), false,
    'Mission candidate discovery must not reject the exact task food for ordinary dislike Tags.');
  assert.ok(
    rareOrders.includes('if (isMissionRecipeExecutionPlan(plan, sortContext))')
      && service.includes('if (!isMissionRecipeExecutionPlan(plan, sortContext))'),
    'Plan sorting and task reasons must reuse the shared legal mission predicate.',
  );
  const planPinRank = functionSlice(rareOrders, 'getPlanPinRank', 'getPlanSpecialBusinessRank');
  assert.ok(
    planPinRank.includes('isMissionRecipeExecutionPlan(plan, sortContext)) rank = Math.max(rank, 50)')
      && planPinRank.includes('plan.food?.customRecipePinned) rank = Math.max(rank, 40)')
      && planPinRank.includes('pinFavoriteRecipe')
      && planPinRank.includes('pinFavoriteBeverage')
      && planPinRank.match(/Math\.max\(rank, 20\)/g)?.length === 2,
    'Mission, custom, and paired favorite pins must use 50/40/20 tiers with recipe and beverage favorites equal.',
  );
  assert.ok(
    service.includes('serializeMissionRecipePriority(order.missionRecipePriority)'),
    'Per-order recommendation cache identity must include the full mission target.',
  );
  assert.ok(
    service.includes('preferences.missionRecipePriorityEnabled')
      && service.includes('if (!enabled || specialBusiness?.active) return base;')
      && service.includes('getVerifiedMissionRecipeSortContext(order)')
      && service.includes("`missionPriorityEnabled:${preferences.missionRecipePriorityEnabled ? '1' : '0'}`"),
    'The explicit preference must be the only frontend gate before a safe mission target enters sorting and the order cache.',
  );
  assert.ok(
    preferences.includes('const MISSION_RECIPE_PRIORITY_STORAGE_KEY')
      && preferences.includes('missionRecipePriorityEnabled: readStoredBoolean(MISSION_RECIPE_PRIORITY_STORAGE_KEY, true)')
      && preferences.includes('missionRecipePriorityEnabled: value.missionRecipePriorityEnabled !== false')
      && preferences.includes("normalized.missionRecipePriorityEnabled ? '1' : '0'"),
    'Mission recipe priority must use a new default-on persisted preference.',
  );
  assert.equal(
    preferences.includes('pinMissionRecipeEnabled')
      || preferences.includes('pin-mission-recipe'),
    false,
    'The removed v1.2.0 mission pin preference must not be migrated or restored.',
  );
  assert.ok(
    settings.includes('label="任务料理置顶"')
      && settings.includes('checked={preferences.missionRecipePriorityEnabled}')
      && settings.includes('若启用相应自动化，收藏限定也必须满足')
      && settings.includes('游戏内列表仍由游戏界面置顶推荐开关单独控制'),
    'Recommendation settings must expose the independent mission-pinning switch and explain the game-list boundary.',
  );
  assert.ok(
    service.includes('missionTarget: isMissionRecipeExecutionPlan(plan, sortContext)')
      && shared.includes('recipe.missionTarget && <Badge variant="secondary">任务目标</Badge>')
      && worker.includes('recipe.missionTarget ? 1 : 0'),
    'The matching primary row must expose a visible task-target marker included in the worker result signature.',
  );
  assert.ok(service.includes('order.automationAllowed !== false'),
    'Per-order automation policy must gate favorite-only primary normalization.');
  assert.equal(service.includes('pinSpecialBusinessExecutionPlanRows'), false,
    'The special-business-only row projection must be removed.');
  assert.equal(`${service}\n${automation}\n${types}\n${workbench}`.includes('preparationPlan'), false,
    'The removed duplicate preparation-plan contract must not remain.');

  const picker = functionSlice(automation, 'pickPlanForPreparation', 'emptyPlanPick');
  assert.ok(picker.includes('getPrimaryExecutionPlan(item.executionPlans)'),
    'Rare automation must read the shared primary plan.');
  assert.equal(picker.includes('for (const plan of'), false,
    'Rare automation must not scan later plans and create a second target.');
  const pinning = functionSlice(gameUiTargets, 'buildRareGameUiTarget', 'buildNormalGameUiTarget');
  assert.ok(
    pinning.includes("order.hasServedFood === true ? null : plan.food")
      && pinning.includes("order.hasServedBeverage === true ? null : plan.beverage")
      && pinning.includes('food != null && isVerifiedMissionPrimaryExecutionPlan(recommendation)')
      && pinning.includes('getPrimaryExecutionPlan(recommendation.executionPlans)'),
    'Game UI pinning must project only unserved primary-plan components and prioritize only an unserved mission recipe.',
  );
  assert.ok(
    pinning.includes('sourceOrderKey: buildRareSourceOrderKey(order)')
      && pinning.includes('traceId: order.traceId!')
      && types.includes('sourceOrderKey: string;')
      && types.includes('traceId: string;')
      && types.includes('orderLifecycleSequence: number;'),
    'A game UI target must carry exact frontend and runtime order identities without deriving a trace fallback.',
  );
  assert.equal(pinning.includes('item.recipes[0]'), false,
    'Game UI pinning must not fall back to independently paired display rows.');
  const normalPinning = functionSlice(gameUiTargets, 'buildNormalGameUiTarget', 'buildRareGameUiTargetSource');
  assert.ok(
    normalPinning.includes('selection?.target')
      && normalPinning.includes('isCurrentNormalExecutionTarget(')
      && normalPinning.includes('if (requiresFinalTarget && !executionTarget) continue;'),
    'Special-business normal targets must require a current final worker selection without an original-order fallback.',
  );
  const normalIdentity = functionSlice(gameUiTargets, 'isCurrentNormalExecutionTarget', 'resolveIngredientNames');
  assert.ok(
    normalIdentity.includes('target.matchFoodId !== order.foodId')
      && normalIdentity.includes('target.matchBeverageId !== order.beverageId')
      && normalIdentity.includes('target.specialTargetSignature === policy.specialTargetSignature')
      && normalIdentity.includes('target.specialTargetRevision === policy.specialTargetRevision'),
    'A normal final target must match the source order and current special-target policy identity.',
  );
  const recipeResolver = functionSlice(gameUiTargets, 'resolveNormalRecipe', 'isCurrentNormalExecutionTarget');
  assert.ok(
    recipeResolver.includes('recipe.id === target.foodId && recipe.recipeId === target.recipeId')
      && recipeResolver.includes('matches.length === 1'),
    'A special normal recipe must match exact foodId + recipeId and reject ambiguous or mismatched catalog rows.',
  );

  assert.ok(
    workbench.includes('serializePrimaryExecutionPlanPolicy(buildPrimaryExecutionPlanPolicy(preferences))'),
    'Worker preference signature must use the normalized primary policy.',
  );
  assert.ok(
    workbench.includes('preferences.missionRecipePriorityEnabled ? 1 : 0'),
    'The recommendation worker input signature must include mission-priority preference changes.',
  );
  assert.ok(
    workbench.includes(
      'prioritizeMissionRecipe: companionPreferences.missionRecipePriorityEnabled\n'
        + '            && !snapshot?.specialBusiness?.active',
    ),
    'The global game target selector must receive the persisted mission-pinning preference only in ordinary business.',
  );
  assert.ok(
    workbench.includes('listPinningEnabled: companionPreferences.rareGameUiPinningEnabled')
      && workbench.includes('listPinningEnabled: companionPreferences.normalGameUiPinningEnabled'),
    'Rare and normal list pinning must remain independently controlled.',
  );
  assert.ok(
    workbench.includes('recipeVariantEnabled: companionPreferences.rareGameUiPinningEnabled')
      && workbench.includes('&& companionPreferences.rareRecipeVariantEnabled')
      && workbench.includes('recipeVariantEnabled: companionPreferences.normalGameUiPinningEnabled')
      && workbench.includes('&& companionPreferences.normalRecipeVariantEnabled')
      && workbench.includes('seatHighlightEnabled: companionPreferences.rareSeatHighlightEnabled')
      && workbench.includes('seatHighlightEnabled: companionPreferences.normalSeatHighlightEnabled')
      && workbench.includes('orderHighlightEnabled: companionPreferences.rareOrderHighlightEnabled')
      && workbench.includes('orderHighlightEnabled: companionPreferences.normalOrderHighlightEnabled'),
    'Each target kind must own its recipe-variant and visual feature switches.',
  );
  assert.equal(
    workbench.includes('preferences.autoPrepRecipeFavoritesOnly ? 1 : 0'),
    false,
    'Inactive favorite-only flags must not invalidate the recommendation worker signature.',
  );
  assert.ok(
    workbench.includes("primaryTargetMismatch ? 'rare-primary-target-mismatch' : eventName"),
    'A low-noise diagnostic must identify any regression that separates the displayed first row from the primary plan.',
  );
}

function functionSlice(source, methodName, nextMethodName) {
  const start = source.indexOf(`function ${methodName}(`);
  const end = source.indexOf(`function ${nextMethodName}(`, start + 1);
  assert.ok(start >= 0, `Method not found: ${methodName}`);
  assert.ok(end > start, `Method boundary not found: ${methodName} -> ${nextMethodName}`);
  return source.slice(start, end);
}
