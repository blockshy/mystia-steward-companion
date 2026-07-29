import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  buildPrimaryExecutionPlanPolicy,
  getPrimaryExecutionPlan,
  normalizePrimaryExecutionPlans,
  selectPrimaryExecutionPlanRecommendation,
} from '../../apps/companion/src/companion/domain/primary-execution-plan.ts';

const root = new URL('../../', import.meta.url);
const basePreferences = {
  automationEnabled: true,
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

const firstOrderPlan = buildPlan(8, 118, 702);
const missionOrderPlan = buildPlan(7, 119, 701, {
  meetsRequiredFood: false,
  matchedNegativeTags: ['厌恶'],
});
const firstRecommendation = buildRecommendation('R-0001', 1, firstOrderPlan);
const missionRecommendation = buildRecommendation('R-0002', 2, missionOrderPlan, true);
assert.equal(
  selectPrimaryExecutionPlanRecommendation(
    [firstRecommendation, missionRecommendation],
    { prioritizeMissionRecipe: true, requireExecutablePlan: false },
  ),
  missionRecommendation,
  'The global game target must select a later order with a verified mission primary plan.',
);
assert.equal(
  selectPrimaryExecutionPlanRecommendation(
    [firstRecommendation, missionRecommendation],
    { prioritizeMissionRecipe: false, requireExecutablePlan: false },
  ),
  firstRecommendation,
  'Disabling mission pinning must preserve the configured order sequence.',
);

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

function buildRecommendation(traceId, deskCode, plan, mission = false) {
  const guestId = 15;
  const runtimeGuestId = 15;
  return {
    order: {
      traceId,
      deskCode,
      guestId,
      runtimeGuestId,
      missionRecipePriority: mission
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
      && settings.includes('若启用相应自动化收藏限定也必须满足')
      && settings.includes('游戏内列表仍由上方实验性开关单独控制'),
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
  const pinning = functionSlice(automation, 'buildGameUiPinningTarget', 'hasAutomationActionEnabled');
  assert.ok(
    pinning.includes('selectPrimaryExecutionPlanRecommendation(')
      && pinning.includes('prioritizeMissionRecipe: options.prioritizeMissionRecipe === true')
      && pinning.includes('getPrimaryExecutionPlan(item.executionPlans)'),
    'Game UI pinning must select a verified mission primary plan before falling back to the configured order sequence.',
  );
  assert.equal(pinning.includes('item.recipes[0]'), false,
    'Game UI pinning must not fall back to independently paired display rows.');

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
    workbench.includes('pinningEnabled: companionPreferences.gameUiPinningEnabled'),
    'Publishing targets into the game list must remain controlled by the experimental game UI pinning switch.',
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
