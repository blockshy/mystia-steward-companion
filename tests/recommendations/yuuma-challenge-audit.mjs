import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'vite';

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true },
  appType: 'custom',
  logLevel: 'silent',
});
let serviceModule;
let registryModule;
let preferencesModule;
let dataModule;
let automationModule;
let automationStateModule;
let recommendationModule;
let normalTargetSharedModule;
try {
  [
    serviceModule,
    registryModule,
    preferencesModule,
    dataModule,
    automationModule,
    automationStateModule,
    recommendationModule,
    normalTargetSharedModule,
  ] = await Promise.all([
    vite.ssrLoadModule('/src/companion/domain/service-recommendations.ts'),
    vite.ssrLoadModule('/src/companion/domain/special-business/registry.ts'),
    vite.ssrLoadModule('/src/companion/preferences.ts'),
    vite.ssrLoadModule('/src/lib/recommendation-data.ts'),
    vite.ssrLoadModule('/src/companion/domain/automation.ts'),
    vite.ssrLoadModule('/src/companion/automation-state.ts'),
    vite.ssrLoadModule('/src/recommendation-engine/index.ts'),
    vite.ssrLoadModule('/src/companion/domain/special-business/normal-targets/shared.ts'),
  ]);
} finally {
  await vite.close();
}

const {
  buildOrderRecommendations,
  createRecommendationCacheStore,
} = serviceModule;
const {
  buildSpecialBusinessOrderRule,
  buildSpecialFoodTargetWirePolicy,
  selectSpecialBusinessNormalExecutionTarget,
} = registryModule;
const { normalizeCompanionPreferences } = preferencesModule;
const { buildRecommendationDataSignature } = dataModule;
const {
  buildNormalOrderAutomationSignature,
  reconcileRareRecipeTargetForSpecialBusiness,
  selectOrderPreparationCandidates,
} = automationModule;
const { emptyAutoFirstOrderState } = automationStateModule;
const { buildRareFoodCandidates } = recommendationModule;
const {
  buildNormalTargetRuntimeContext,
  buildSyntheticDemand,
} = normalTargetSharedModule;
const root = new URL('../../', import.meta.url);

const baseIngredient = buildIngredient(11, '基础肉', ['下酒']);
const targetAIngredient = buildIngredient(12, '目标甲材料', ['目标甲']);
const targetBIngredient = buildIngredient(13, '目标乙材料', ['目标乙']);
const recipe = {
  id: 101,
  recipeId: 201,
  name: '血池测试料理',
  description: '',
  ingredients: [baseIngredient.name],
  positiveTags: ['下酒'],
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
  name: '血池测试酒水',
  description: '',
  tags: ['直饮'],
  dlc: 0,
  level: 1,
  price: 30,
  from: {},
};
const customer = {
  id: 1003,
  name: '运行时角色 1003',
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
const data = {
  recipes: [recipe],
  ingredients: [baseIngredient, targetAIngredient, targetBIngredient],
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
  foodTagIdMap: { 1: '下酒' },
  beverageTagIdMap: { 2: '直饮' },
  tagPriorityRules: [],
  source: 'runtime',
  status: 'test',
};
const runtime = {
  availableRecipeIds: [recipe.id],
  availableBeverageIds: [beverage.id],
  availableIngredientIds: data.ingredients.map((ingredient) => ingredient.id),
  ownedIngredientQty: Object.fromEntries(data.ingredients.map((ingredient) => [ingredient.id, 10])),
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
    automationAvailabilityDiagnostic: 'recommendation audit',
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
const preferences = normalizeCompanionPreferences({
  filterMissingCookers: true,
  recommendationBudgetPolicy: 'warn',
});
const specialBusiness = {
  active: true,
  challengeTypeAvailable: true,
  challengeType: 'Story_BloodPondHell',
  displayName: '血池地狱',
  category: 'challenge',
  ruleSummary: '',
  foodTargetTags: ['目标甲', '目标乙'],
  beverageTargetTags: [],
  yuumaFoodTargetRevision: 17,
  phase: 'Phase 1',
  currentAnger: 10,
  maxAnger: 100,
  targetAnger: 80,
  recommendationPolicy: 'yuuma-target',
  automationPolicy: 'manual',
  source: 'test',
  error: null,
};
const rareOrder = {
  traceId: 'R-YUUMA',
  deskCode: 1,
  guestId: customer.id,
  runtimeGuestId: customer.id,
  guestName: customer.name,
  specialBusinessRole: 'yuuma-boss-order',
  automationAllowed: true,
  automationBlockReason: '',
  foodTagId: 1,
  foodTag: '下酒',
  beverageTagId: 2,
  beverageTag: '直饮',
  source: 'test',
  hasServedFood: false,
  hasServedBeverage: false,
};

const yuumaRule = buildSpecialBusinessOrderRule(specialBusiness, 'yuuma-boss-order');
assert.deepEqual(yuumaRule.foodTarget, {
  enforcement: 'require',
  match: 'all',
  tags: ['目标甲', '目标乙'],
});
assert.equal(yuumaRule.requiresBaseOrderMatch, true);
assert.equal(yuumaRule.blockingReason, '');
const unverifiedRule = buildSpecialBusinessOrderRule(
  specialBusiness,
  'yuuma-order-unverified',
);
assert.match(unverifiedRule.blockingReason, /角色身份尚未确认/);
assert.equal(unverifiedRule.foodTarget.enforcement, 'none');

for (const [context, role] of [
  [{ ...specialBusiness, challengeType: 'Story_Yuyuko' }, 'yuuma-boss-order'],
  [specialBusiness, 'ordinary-order'],
  [{ ...specialBusiness, active: false }, 'yuuma-boss-order'],
]) {
  assert.equal(
    buildSpecialBusinessOrderRule(context, role).foodTarget.enforcement,
    'none',
    'Yuuma policy must require both exact challenge and exact order role.',
  );
}
assert.equal(
  buildSpecialBusinessOrderRule(
    {
      ...specialBusiness,
      challengeType: 'Challenge_Yuyuko',
      category: 'boss',
      phase: 'Phase 3',
    },
    'ordinary-order',
  ).yuyukoProgressEvaluationMode,
  'none',
  'A broad boss category must not activate Yuyuko rules.',
);
assert.equal(
  buildSpecialBusinessOrderRule(
    { ...specialBusiness, challengeType: 'Unknown_Challenge' },
    'ordinary-order',
  ).foodTarget.enforcement,
  'none',
  'Unknown special-business scenes must not inject passive target sorting.',
);
assert.match(
  buildSpecialBusinessOrderRule(
    {
      ...specialBusiness,
      active: false,
      challengeTypeAvailable: false,
      error: 'challenge type unavailable',
    },
    'ordinary-order',
  ).blockingReason,
  /challenge type unavailable/,
  'An unavailable challenge identity must fail closed instead of falling through to ordinary rules.',
);
const missingChallengeAvailability = { ...specialBusiness };
delete missingChallengeAvailability.challengeTypeAvailable;
assert.match(
  buildSpecialBusinessOrderRule(
    missingChallengeAvailability,
    'yuuma-boss-order',
  ).blockingReason,
  /特殊经营类型暂时无法读取/,
  'A structurally incomplete special-business response must fail closed.',
);

const recommendation = buildRareRecommendation();
assert.equal(recommendation.executionPlans.length > 0, true);
const primary = recommendation.executionPlans[0];
assert.equal(primary.food?.recipe.id, recipe.id);
assert.equal(primary.beverage?.beverage.id, beverage.id);
assert.deepEqual(
  primary.food?.extraIngredients.map((ingredient) => ingredient.id).sort((left, right) => left - right),
  [targetAIngredient.id, targetBIngredient.id],
  'Yuuma planning must search an ingredient combination that satisfies both target tags.',
);
assert.equal(primary.food?.activeTags.includes('目标甲'), true);
assert.equal(primary.food?.activeTags.includes('目标乙'), true);
assert.equal(recommendation.recipes[0].recipe.id, primary.food?.recipe.id);
assert.deepEqual(
  recommendation.recipes[0].extraIngredients.map((ingredient) => ingredient.id).sort((left, right) => left - right),
  [targetAIngredient.id, targetBIngredient.id],
  'The unique primary plan must remain the first visible recipe row.',
);
const candidateResult = selectOrderPreparationCandidates(
  [recommendation],
  { version: 1, recipes: [], beverages: [] },
  normalizeCompanionPreferences({
    automationEnabled: true,
    autoPrepStartCooking: true,
    autoPrepTakeBeverage: true,
  }),
);
assert.equal(candidateResult.selections.length, 1,
  'A verified Yuuma boss order with a require/all primary plan must enter rare automation.');
const candidate = candidateResult.selections[0];
const reconciled = reconcileRareRecipeTargetForSpecialBusiness(
  specialBusiness,
  7,
  candidate.item,
  emptyAutoFirstOrderState('R-YUUMA', 1000),
  candidate.recipeTarget,
  true,
  1000,
);
assert.equal(reconciled.state.recipeTarget?.specialTargetOwner, 'yuuma');
assert.equal(reconciled.state.recipeTarget?.specialTargetMatchMode, 'all');
assert.equal(
  reconciled.state.recipeTarget?.specialTargetSignature,
  'Story_BloodPondHell|yuuma|generation:7|match:all|food:目标乙,目标甲',
);
const rotatedTargetReconciliation = reconcileRareRecipeTargetForSpecialBusiness(
  specialBusiness,
  8,
  candidate.item,
  {
    ...reconciled.state,
    rollbackCount: 2,
    paused: true,
    step: 'paused',
    pausedStage: 'ensure-cooking',
    pauseReasonCode: 'rollback-limit-reached',
    lastError: '旧目标已达到回退上限。',
  },
  candidate.recipeTarget,
  true,
  1050,
);
assert.equal(rotatedTargetReconciliation.state.rollbackCount, 0,
  'A new special-target signature must retire the rollback budget owned by the previous target.');
assert.equal(rotatedTargetReconciliation.state.paused, false,
  'A rollback-limit pause from the previous special target must not block the new target.');
assert.equal(rotatedTargetReconciliation.state.step, 'ensure-cooking');
assert.equal(
  rotatedTargetReconciliation.state.recipeTargetSignature,
  'Story_BloodPondHell|yuuma|generation:8|match:all|food:目标乙,目标甲',
);
assert.equal(rotatedTargetReconciliation.rollbackTargetRotated, true,
  'Rare scheduling must expose signature retirement for structured diagnostics.');
const manualSafetyState = {
  ...reconciled.state,
  rollbackCount: 2,
  rollbackTargetSignature: reconciled.specialTargetPolicy.specialTargetSignature,
  prepared: true,
  cookingJobId: 'CJ-MANUAL',
  paused: true,
  manualResolutionRequired: true,
  pauseReasonCode: 'cooking-warmer-commit-uncertain',
  step: 'paused',
};
const manualSafetyRotation = reconcileRareRecipeTargetForSpecialBusiness(
  specialBusiness,
  8,
  candidate.item,
  manualSafetyState,
  null,
  true,
  1075,
);
assert.equal(manualSafetyRotation.rollbackTargetRotated, true);
assert.equal(manualSafetyRotation.state.rollbackCount, 0);
assert.equal(manualSafetyRotation.state.prepared, true,
  'Target rotation must not erase committed/prepared facts behind a warmer-commit safety barrier.');
assert.equal(manualSafetyRotation.state.cookingJobId, 'CJ-MANUAL',
  'Target rotation must retain the exact job identity behind a warmer-commit safety barrier.');
assert.equal(manualSafetyRotation.state.recipeTarget, manualSafetyState.recipeTarget,
  'Target rotation must not replace the recipe target while a warmer-commit safety barrier is latched.');
assert.equal(manualSafetyRotation.state.manualResolutionRequired, true);
assert.equal(manualSafetyRotation.state.paused, true);
const beverageOnlyReconciliation = reconcileRareRecipeTargetForSpecialBusiness(
  specialBusiness,
  7,
  candidate.item,
  emptyAutoFirstOrderState('R-YUUMA-BEVERAGE', 1100),
  candidate.recipeTarget,
  false,
  1100,
);
assert.equal(beverageOnlyReconciliation.state.recipeTarget, null,
  '只送酒时不应要求或锁存料理动作目标。');
assert.equal(
  beverageOnlyReconciliation.specialTargetPolicy.specialTargetSignature,
  'Story_BloodPondHell|yuuma|generation:7|match:all|food:目标乙,目标甲',
  '只送酒时仍必须独立携带完整血池地狱策略。',
);
const preparedCompletionReconciliation = reconcileRareRecipeTargetForSpecialBusiness(
  specialBusiness,
  7,
  candidate.item,
  {
    ...reconciled.state,
    prepared: true,
    step: 'complete-order',
  },
  null,
  false,
  1200,
);
assert.equal(preparedCompletionReconciliation.state.recipeTarget, null,
  '料理已处理后的完成阶段不应继续携带料理动作目标。');
assert.equal(preparedCompletionReconciliation.state.prepared, true,
  '策略与料理目标分离不得回滚已准备状态。');
assert.equal(
  preparedCompletionReconciliation.specialTargetPolicy.specialTargetSignature,
  reconciled.specialTargetPolicy.specialTargetSignature,
  '完成阶段必须继续携带与开锅阶段相同的特殊策略。',
);
const wackyBeverageOnlyReconciliation = reconcileRareRecipeTargetForSpecialBusiness(
  {
    ...specialBusiness,
    challengeType: 'Story_WackyCookingCompetition',
    displayName: '怪诞料理大赛',
    phase: 'Phase 2',
    foodTargetTags: ['肉'],
  },
  9,
  {
    ...candidate.item,
    order: {
      ...candidate.item.order,
      specialBusinessRole: 'wacky-target-order',
    },
  },
  emptyAutoFirstOrderState('R-WACKY-BEVERAGE', 1300),
  null,
  false,
  1300,
);
assert.equal(wackyBeverageOnlyReconciliation.state.recipeTarget, null);
assert.equal(
  wackyBeverageOnlyReconciliation.specialTargetPolicy.specialTargetSignature,
  'Story_WackyCookingCompetition|koishi|generation:9|match:any|food:肉',
  '怪诞料理只送酒或只完成阶段也必须独立携带 require/any 策略。',
);

const customRecommendation = buildRareRecommendation({
  customRecipeData: {
    version: 1,
    enabled: true,
    recipes: [{
      id: 'yuuma-custom',
      customerId: customer.id,
      customerName: customer.name,
      foodTag: null,
      foodId: recipe.id,
      recipeId: recipe.recipeId,
      recipeName: recipe.name,
      extraIngredientIds: [targetAIngredient.id, targetBIngredient.id],
      enabled: true,
      pinToTop: true,
      sortOrder: 0,
      createdAtUtc: '2026-07-30T00:00:00Z',
      updatedAtUtc: '2026-07-30T00:00:00Z',
    }],
  },
});
assert.equal(customRecommendation.executionPlans[0]?.food?.customRecipe, true);
assert.deepEqual(
  customRecommendation.executionPlans[0]?.food?.matchedSpecialFoodTargetTags.sort(),
  ['目标乙', '目标甲'].sort(),
  'Custom recipes must evaluate the same require/all target policy as generated candidates.',
);

const oneTargetUnavailable = buildRareRecommendation({
  runtimeOverrides: {
    availableIngredientIds: [baseIngredient.id, targetAIngredient.id],
  },
});
assert.equal(oneTargetUnavailable.executionPlans.length, 0);
assert.match(oneTargetUnavailable.blockedMessages.join('\n'), /同时满足目标 Tag|目标乙/);

const incompleteTarget = buildRareRecommendation({
  specialBusinessOverrides: { foodTargetTags: ['目标甲'] },
});
assert.equal(incompleteTarget.executionPlans.length, 0);
assert.match(incompleteTarget.blockedMessages.join('\n'), /当前读取到 1 个/);

const missingCanonicalIdentity = buildRecommendations({
  orderOverrides: { guestId: null },
});
assert.equal(missingCanonicalIdentity.recommendations.length, 0);
assert.match(missingCanonicalIdentity.recommendationIssues[0]?.message ?? '', /无法把该稀客映射/);

const unverifiedKnownIdentity = buildRareRecommendation({
  orderOverrides: { specialBusinessRole: 'yuuma-order-unverified' },
});
assert.equal(unverifiedKnownIdentity.executionPlans.length, 0);
assert.match(unverifiedKnownIdentity.blockedMessages.join('\n'), /角色身份尚未确认/);
const unverifiedMissingIdentity = buildRecommendations({
  orderOverrides: {
    guestId: null,
    guestName: customer.name,
    specialBusinessRole: 'yuuma-order-unverified',
  },
});
assert.equal(unverifiedMissingIdentity.recommendations.length, 0);
assert.match(
  unverifiedMissingIdentity.recommendationIssues[0]?.message ?? '',
  /无法把该稀客映射/,
  'The exact unverified role must not fall back to a matching guest name.',
);

const ordinaryOrder = {
  ...rareOrder,
  traceId: 'R-ORDINARY',
  specialBusinessRole: '',
  automationAllowed: true,
};
const ordinaryWithoutContext = buildRecommendations({
  order: ordinaryOrder,
  specialBusinessContext: null,
});
const ordinaryInsideBloodPond = buildRecommendations({
  order: ordinaryOrder,
  specialBusinessContext: specialBusiness,
});
assert.deepEqual(
  ordinaryInsideBloodPond,
  ordinaryWithoutContext,
  'An ordinary order must remain byte-for-byte equivalent inside a Yuuma challenge.',
);

const normalOrder = {
  traceId: 'N-YUUMA',
  orderKey: 'N-YUUMA',
  deskCode: 1,
  guestId: customer.id,
  runtimeGuestId: customer.id,
  guestName: customer.name,
  specialBusinessRole: 'yuuma-boss-order',
  foodPreferenceTags: ['下酒'],
  beveragePreferenceTags: ['直饮'],
  foodId: recipe.id,
  foodName: recipe.name,
  beverageId: beverage.id,
  beverageName: beverage.name,
  hasServedFood: false,
  hasServedBeverage: false,
  readyToEvaluate: false,
  hasEvaluated: false,
  controllerAvailable: true,
  canAutomate: false,
  source: 'test',
};
const profileOnlyData = {
  ...data,
  rareCustomers: [],
};
const normalSelection = selectSpecialBusinessNormalExecutionTarget({
  order: normalOrder,
  specialBusiness,
  runtime,
  preferences,
  dataSignature: buildRecommendationDataSignature(profileOnlyData),
  data: profileOnlyData,
});
assert.ok(normalSelection.target);
assert.equal(normalSelection.target.foodId, normalOrder.foodId);
assert.equal(normalSelection.target.beverageId, normalOrder.beverageId);
assert.equal(normalSelection.target.allowYuumaControlledProgression, false,
  'A strict all-Tag plan must not carry controlled-progression permission.');
assert.deepEqual(normalSelection.target.specialTargetFoodTags, ['目标甲', '目标乙']);
assert.deepEqual(
  buildSpecialFoodTargetWirePolicy(specialBusiness, normalOrder.specialBusinessRole, 7),
  {
    specialTargetChallenge: 'Story_BloodPondHell',
    specialTargetOwner: 'yuuma',
    specialTargetGeneration: 7,
    specialTargetRevision: 17,
    specialTargetFoodTags: ['目标乙', '目标甲'],
    specialTargetMatchMode: 'all',
    specialTargetSignature: 'Story_BloodPondHell|yuuma|generation:7|match:all|food:目标乙,目标甲',
  },
);
assert.deepEqual(
  normalSelection.target.extraIngredientIds.sort((left, right) => left - right),
  [targetAIngredient.id, targetBIngredient.id],
);
const controlledProgressionSelection = selectSpecialBusinessNormalExecutionTarget({
  order: normalOrder,
  specialBusiness,
  runtime: {
    ...runtime,
    availableIngredientIds: [baseIngredient.id, targetAIngredient.id],
  },
  preferences,
  dataSignature: buildRecommendationDataSignature(data),
  data,
});
assert.ok(controlledProgressionSelection.target,
  'A buildable original order must remain executable when only the all-Tag challenge bonus is impossible.');
assert.equal(controlledProgressionSelection.target.allowYuumaControlledProgression, true);
assert.deepEqual(controlledProgressionSelection.target.extraIngredientIds, [targetAIngredient.id],
  'Controlled progression must still maximize reachable target Tags with the same special-target demand.');
assert.deepEqual(controlledProgressionSelection.target.specialTargetFoodTags, ['目标甲', '目标乙'],
  'Controlled progression must retain the complete active target policy for runtime revision checks.');
assert.match(controlledProgressionSelection.target.reason, /受控推进方案/);
assert.match(controlledProgressionSelection.target.reason, /较低伤害并增加狂暴/);
assert.equal(controlledProgressionSelection.message, '');

const missingCookerSelection = selectSpecialBusinessNormalExecutionTarget({
  order: normalOrder,
  specialBusiness,
  runtime: {
    ...runtime,
    placedCookerTypeIds: [],
    placedCookers: [],
    placedCookerControllerCount: 0,
  },
  preferences,
  dataSignature: buildRecommendationDataSignature(data),
  data,
});
assert.equal(missingCookerSelection.target, null,
  'Controlled progression must not bypass the original recipe cooker gate.');
assert.match(missingCookerSelection.message, /所需厨具 烧烤架 当前不可用/);

const negativeTargetVariant = selectYuumaNormalVariant({
  recipeOverrides: {
    id: 301,
    recipeId: 401,
    name: '负面目标料理',
    negativeTags: ['目标乙'],
  },
  ingredients: [baseIngredient, targetAIngredient, targetBIngredient],
  targetTags: ['目标甲', '目标乙'],
});
assertControlledOriginalOrder(negativeTargetVariant,
  'A target Tag forbidden by the original recipe must use controlled progression.');
assert.equal(negativeTargetVariant.selection.target.foodTags.includes('目标乙'), false);

const hotBaseIngredient = buildIngredient(14, '凉爽材料', ['凉爽']);
const saltyIngredient = buildIngredient(15, '咸味材料', ['咸']);
const suppressedTargetVariant = selectYuumaNormalVariant({
  recipeOverrides: {
    id: 302,
    recipeId: 402,
    name: '灼热基础料理',
    positiveTags: ['灼热'],
  },
  ingredients: [baseIngredient, hotBaseIngredient, saltyIngredient],
  targetTags: ['凉爽', '咸'],
});
assertControlledOriginalOrder(suppressedTargetVariant,
  'A base hot Tag suppressing the cool target must use controlled progression.');
assert.equal(suppressedTargetVariant.selection.target.foodTags.includes('凉爽'), false);
assert.equal(suppressedTargetVariant.selection.target.foodTags.includes('灼热'), true);

const fullSlotIngredients = Array.from({ length: 5 }, (_, index) =>
  buildIngredient(31 + index, `满槽材料 ${index + 1}`, []));
const fullSlotVariant = selectYuumaNormalVariant({
  recipeOverrides: {
    id: 303,
    recipeId: 403,
    name: '五材料满槽料理',
    ingredients: fullSlotIngredients.map((ingredient) => ingredient.name),
    positiveTags: [],
  },
  ingredients: [...fullSlotIngredients, targetAIngredient, targetBIngredient],
  targetTags: ['目标甲', '目标乙'],
});
assertControlledOriginalOrder(fullSlotVariant,
  'An original recipe with all five ingredient slots occupied must use controlled progression.');
assert.deepEqual(fullSlotVariant.selection.target.extraIngredientIds, []);

const beamTargetIngredient = buildIngredient(9999, '唯一可达目标材料', ['目标甲']);
const beamFillers = Array.from({ length: 80 }, (_, index) =>
  buildIngredient(100 + index, `高优先候选 ${index + 1}`, ['下酒']));
const wideBeamVariant = selectYuumaNormalVariant({
  recipeOverrides: {
    id: 304,
    recipeId: 404,
    name: '宽候选池料理',
  },
  ingredients: [baseIngredient, ...beamFillers, beamTargetIngredient],
  targetTags: ['目标甲', '目标乙'],
});
assertControlledOriginalOrder(wideBeamVariant,
  'Controlled progression must survive a candidate pool wider than the ingredient beam.');
assert.equal(wideBeamVariant.selection.target.extraIngredientIds.includes(beamTargetIngredient.id), true,
  'The beam must retain the state with the greatest reachable special-target coverage.');
assert.equal(wideBeamVariant.selection.target.foodTags.includes('目标甲'), true);

const blockingSaltyIngredients = Array.from({ length: 80 }, (_, index) =>
  buildIngredient(200 + index, `阻断凉爽的咸味材料 ${index + 1}`, ['咸', '灼热']));
const safeSaltyIngredient = buildIngredient(9998, '安全咸味材料', ['咸']);
const coolIngredient = buildIngredient(9999, '凉爽材料', ['凉爽']);
const strictReachabilityVariant = selectYuumaNormalVariant({
  recipeOverrides: {
    id: 305,
    recipeId: 405,
    name: '严格可达性料理',
    positiveTags: [],
  },
  ingredients: [
    baseIngredient,
    ...blockingSaltyIngredients,
    safeSaltyIngredient,
    coolIngredient,
  ],
  targetTags: ['凉爽', '咸'],
});
assert.equal(strictReachabilityVariant.selection.target.allowYuumaControlledProgression, false,
  'Yuuma must not fall back to controlled progression while a strict two-Tag combination remains reachable.');
assert.deepEqual(
  strictReachabilityVariant.selection.target.extraIngredientIds.sort((left, right) => left - right),
  [safeSaltyIngredient.id, coolIngredient.id],
  'The Yuuma-only search must preserve the safe intermediate state beyond the common 64-state beam.',
);

const strictReachabilityContext = buildNormalTargetRuntimeContext(
  strictReachabilityVariant.runtime,
  preferences,
  strictReachabilityVariant.data,
);
assert.ok(strictReachabilityContext);
const commonBeamCandidates = buildRareFoodCandidates(
  strictReachabilityVariant.data,
  buildSyntheticDemand(customer, '', '', {
    enforcement: 'require',
    match: 'all',
    tags: strictReachabilityVariant.targetTags,
  }),
  strictReachabilityContext,
);
assert.equal(
  commonBeamCandidates.some((candidate) => candidate.meetsSpecialFoodTarget),
  false,
  'The regression fixture must actually exceed and defeat the unchanged common beam.',
);

const wackyPreferenceIngredients = Array.from({ length: 80 }, (_, index) =>
  buildIngredient(400 + index, `怪诞偏好材料 ${index + 1}`, ['目标甲', '偏好']));
const wackyDualTargetIngredient = buildIngredient(9997, '怪诞双目标材料', ['目标甲', '目标乙']);
const wackyStyleData = {
  ...data,
  recipes: [{
    ...recipe,
    id: 306,
    recipeId: 406,
    name: '怪诞默认排序料理',
    positiveTags: [],
  }],
  ingredients: [baseIngredient, ...wackyPreferenceIngredients, wackyDualTargetIngredient],
};
const wackyStyleRuntime = {
  ...runtime,
  availableRecipeIds: [306],
  availableIngredientIds: wackyStyleData.ingredients.map((ingredient) => ingredient.id),
  ownedIngredientQty: Object.fromEntries(wackyStyleData.ingredients.map((ingredient) => [ingredient.id, 10])),
};
const wackyStyleContext = buildNormalTargetRuntimeContext(wackyStyleRuntime, preferences, wackyStyleData);
assert.ok(wackyStyleContext);
const wackyStyleCandidates = buildRareFoodCandidates(
  wackyStyleData,
  buildSyntheticDemand({ ...customer, positiveTags: ['偏好'] }, '偏好', '', {
    enforcement: 'require',
    match: 'any',
    tags: ['目标甲', '目标乙'],
  }),
  wackyStyleContext,
);
assert.equal(
  wackyStyleCandidates.some((candidate) => candidate.matchedSpecialFoodTargetTags.length === 2),
  false,
  'The default Wacky-style any-Tag search must retain its original preference-first beam ordering.',
);

const missingRuntimeIdentitySelection = selectSpecialBusinessNormalExecutionTarget({
  order: {
    ...normalOrder,
    runtimeGuestId: null,
  },
  specialBusiness,
  runtime,
  preferences,
  dataSignature: buildRecommendationDataSignature(data),
  data,
});
assert.equal(missingRuntimeIdentitySelection.target, null);
assert.match(
  missingRuntimeIdentitySelection.message,
  /runtimeGuestId=1003.*missing/,
  'A normalized catalog guestId must not substitute for the verified runtime order identity.',
);
const blockedNormalSignature = buildNormalOrderAutomationSignature([normalOrder]);
assert.notEqual(
  buildNormalOrderAutomationSignature([{
    ...normalOrder,
    runtimeGuestId: null,
  }]),
  blockedNormalSignature,
  'A verified runtime identity transition must immediately wake normal automation.',
);
assert.notEqual(
  buildNormalOrderAutomationSignature([{
    ...normalOrder,
    canAutomate: true,
  }]),
  blockedNormalSignature,
  'A newly verified Yuuma order must immediately wake normal automation when canAutomate changes.',
);
assert.notEqual(
  buildNormalOrderAutomationSignature([{
    ...normalOrder,
    specialBusinessRole: 'yuuma-order-unverified',
  }]),
  blockedNormalSignature,
  'A Yuuma role transition must invalidate the normal automation scheduler signature.',
);
const missingProfileData = {
  ...data,
  rareCustomerProfiles: [],
};
const missingProfileSelection = selectSpecialBusinessNormalExecutionTarget({
  order: normalOrder,
  specialBusiness,
  runtime,
  preferences,
  dataSignature: buildRecommendationDataSignature(missingProfileData),
  data: missingProfileData,
});
assert.equal(missingProfileSelection.target, null);
assert.match(
  missingProfileSelection.message,
  /characterId=1003.*完整料理、酒水喜好档案/,
  'Yuuma target planning must fail closed instead of using the place-filtered rare-customer catalog or recipe tags.',
);
const unverifiedNormalSelection = selectSpecialBusinessNormalExecutionTarget({
  order: {
    ...normalOrder,
    specialBusinessRole: 'yuuma-order-unverified',
  },
  specialBusiness,
  runtime,
  preferences,
  dataSignature: buildRecommendationDataSignature(data),
  data,
});
assert.equal(unverifiedNormalSelection.target, null);
assert.match(unverifiedNormalSelection.message, /角色身份尚未确认/);
const ordinaryNormalSelection = selectSpecialBusinessNormalExecutionTarget({
  order: {
    ...normalOrder,
    guestId: 1,
    guestName: '普通顾客',
    specialBusinessRole: '',
    canAutomate: true,
  },
  specialBusiness,
  runtime,
  preferences,
  dataSignature: buildRecommendationDataSignature(data),
  data,
});
assert.deepEqual(
  ordinaryNormalSelection,
  { target: null, message: '' },
  'An ordinary normal order must not inherit the Yuuma target inside Blood Pond Hell.',
);

await assertSourceContracts();
console.log('PASS: Yuuma NormalOrder planning is strict-first and uses explicit controlled progression only when the original order remains executable.');

function buildRareRecommendation(options = {}) {
  const result = buildRecommendations(options);
  assert.equal(result.recommendations.length, 1);
  return result.recommendations[0];
}

function buildRecommendations({
  order = rareOrder,
  orderOverrides = {},
  runtimeOverrides = {},
  specialBusinessContext = specialBusiness,
  specialBusinessOverrides = {},
  customRecipeData = { version: 1, enabled: true, recipes: [] },
} = {}) {
  const result = buildOrderRecommendations(
    [{ ...order, ...orderOverrides }],
    { ...runtime, ...runtimeOverrides },
    new Map([[customer.id, customer]]),
    createRecommendationCacheStore(),
    { version: 1, recipes: [], beverages: [] },
    customRecipeData,
    preferences,
    [],
    specialBusinessContext
      ? { ...specialBusinessContext, ...specialBusinessOverrides }
      : null,
    [],
    data,
    { usage: 'automation' },
  );
  return result;
}

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

function selectYuumaNormalVariant({ recipeOverrides, ingredients, targetTags }) {
  const variantRecipe = {
    ...recipe,
    ...recipeOverrides,
  };
  const variantData = {
    ...data,
    recipes: [variantRecipe],
    ingredients,
  };
  const variantOrder = {
    ...normalOrder,
    traceId: `N-YUUMA-${variantRecipe.id}`,
    orderKey: `N-YUUMA-${variantRecipe.id}`,
    foodId: variantRecipe.id,
    foodName: variantRecipe.name,
  };
  const variantSpecialBusiness = {
    ...specialBusiness,
    foodTargetTags: targetTags,
    yuumaFoodTargetRevision: variantRecipe.id,
  };
  const variantRuntime = {
    ...runtime,
    availableRecipeIds: [variantRecipe.id],
    availableIngredientIds: ingredients.map((ingredient) => ingredient.id),
    ownedIngredientQty: Object.fromEntries(ingredients.map((ingredient) => [ingredient.id, 10])),
  };
  const selection = selectSpecialBusinessNormalExecutionTarget({
    order: variantOrder,
    specialBusiness: variantSpecialBusiness,
    runtime: variantRuntime,
    preferences,
    dataSignature: buildRecommendationDataSignature(variantData),
    data: variantData,
  });
  assert.ok(selection.target);
  return {
    selection,
    order: variantOrder,
    beverage,
    data: variantData,
    runtime: variantRuntime,
    targetTags,
  };
}

function assertControlledOriginalOrder(variant, message) {
  const { target } = variant.selection;
  assert.ok(target, message);
  assert.equal(target.foodId, variant.order.foodId, message);
  assert.equal(target.beverageId, variant.beverage.id, message);
  assert.equal(target.allowYuumaControlledProgression, true, message);
  assert.equal(
    target.specialTargetFoodTags.every((tag) => target.foodTags.includes(tag)),
    false,
    'A controlled target must not be misclassified as a strict all-Tag plan.',
  );
  assert.match(target.reason, /受控推进方案/);
}

async function assertSourceContracts() {
  const [
    types,
    companionTypes,
    passive,
    yuyuko,
    automation,
    api,
    workbench,
    publisher,
    recommendationTypes,
    registry,
    worker,
    rareOrders,
    yuumaNormalTarget,
    wackyNormalTarget,
    normalSnapshot,
    yuumaOrderModule,
  ] = await Promise.all([
    readFile(new URL('apps/companion/src/companion/domain/special-business/rules/types.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/types.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/rules/passive.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/rules/yuyuko.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/automation.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/api.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/hooks/useGameUiPinningPublisher.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/recommendation-engine/types.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/registry.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/workers/order-recommendations.worker.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/recommendation-engine/rare-orders.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/normal-targets/yuuma.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/normal-targets/wacky.ts', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/RuntimeNormalOrderSnapshotService.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/SpecialBusiness/YuumaChallengeOrderModule.cs', root), 'utf8'),
  ]);
  for (const source of [types, automation, api]) {
    assert.equal(source.includes('requiresWackyFoodTarget'), false);
    assert.equal(source.includes('wackyTargetFoodTags'), false);
  }
  assert.match(passive, /return emptySpecialBusinessOrderRule\(\)/);
  assert.equal(yuyuko.includes("specialBusiness.category === 'boss'"), false);
  assert.match(automation, /getWackyRecipeCookingDeferral/);
  for (const field of [
    'specialTargetChallenge',
    'specialTargetOwner',
    'specialTargetGeneration',
    'specialTargetRevision',
    'specialTargetFoodTags',
    'specialTargetMatchMode',
    'specialTargetSignature',
  ]) {
    assert.match(api, new RegExp(`${field}:`));
  }
  assert.match(workbench, /buildOrderRecommendationPresentation\(/);
  assert.match(workbench, /yuumaControlled=\$\{target\.allowYuumaControlledProgression \? 1 : 0\}/,
    'Normal automation diagnostics must distinguish strict and controlled Yuuma targets.');
  assert.equal(workbench.includes('visibleOrderRecommendations = orderRecommendationsPending'), false);
  assert.match(publisher, /targetPolicySignature/);
  assert.equal(publisher.includes('targetContextSignature'), false);
  assert.match(publisher, /reconcileGameUiPinningTarget\(state\.lastCurrentTarget, sourceOrders\)/);
  assert.equal(recommendationTypes.includes("'prefer'"), false);
  assert.match(registry, /challengeTypeAvailable !== true/);
  assert.match(companionTypes, /interface NormalBusinessOrder[\s\S]*runtimeGuestId: number \| null/);
  assert.match(companionTypes, /interface NormalOrderExecutionTarget[\s\S]*allowYuumaControlledProgression: boolean/);
  assert.match(companionTypes, /interface AutomationCookingJobSnapshot[\s\S]*allowYuumaControlledProgression: boolean/);
  assert.match(worker, /item\.target\?\.allowYuumaControlledProgression \? 1 : 0/,
    'The worker result signature must change when a Yuuma target switches execution policy.');
  assert.match(yuumaNormalTarget, /preserveTwoTagSpecialTargetReachability: true/,
    'Only the Yuuma strict-first selector must request reachability-preserving ingredient search.');
  assert.equal(wackyNormalTarget.includes('preserveTwoTagSpecialTargetReachability'), false,
    'Wacky candidate ordering must remain on the common beam.');
  const commonIngredientComparator = rareOrders.slice(
    rareOrders.indexOf('function compareIngredientStates('),
    rareOrders.indexOf('export function compareFoodCandidates('),
  );
  assert.equal(commonIngredientComparator.includes('matchedSpecialFoodTargetTags'), false,
    'Partial special-target coverage must not globally change the common beam ordering.');
  const normalOrderAction = api.slice(
    api.indexOf('export async function completeFirstNormalOrder('),
    api.indexOf('export async function readFavorites('),
  );
  assert.match(
    normalOrderAction,
    /if \(order\.runtimeGuestId != null\) params\.set\('runtimeGuestId', String\(order\.runtimeGuestId\)\)/,
    'Normal-order automation must transmit the verified runtime identity.',
  );
  assert.match(
    normalOrderAction,
    /allowYuumaControlledProgression: String\(executionTarget\?\.allowYuumaControlledProgression === true\)/,
    'Normal-order automation must transmit controlled progression as an explicit boolean.',
  );
  assert.match(
    normalSnapshot,
    /RuntimeGuestId = classification\.RuntimeGuestId/,
    'The normal-order snapshot must project only the identity verified by the special-business classifier.',
  );
  assert.match(
    yuumaOrderModule,
    /AllowedSpecialOrder\([\s\S]*identity\.OrderGuestId\.Value\)/,
    'The Blood Pond Hell classifier must publish its exact order/controller-verified identity.',
  );
}
