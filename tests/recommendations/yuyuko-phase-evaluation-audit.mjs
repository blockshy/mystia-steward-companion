import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'vite';

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true },
  appType: 'custom',
});
let yuyukoPositiveSpellModule;
let yuyukoChallengeModule;
let yuyukoNormalTargetModule;
let preferencesModule;
let specialBusinessRegistryModule;
let automationStateModule;
let serviceModule;
let recommendationDataModule;
try {
  [
    yuyukoPositiveSpellModule,
    yuyukoChallengeModule,
    yuyukoNormalTargetModule,
    preferencesModule,
    specialBusinessRegistryModule,
    automationStateModule,
    serviceModule,
    recommendationDataModule,
  ] = await Promise.all([
    vite.ssrLoadModule(
      '/src/companion/domain/special-business/yuyuko-positive-spell.ts',
    ),
    vite.ssrLoadModule(
      '/src/companion/domain/special-business/yuyuko-challenge.ts',
    ),
    vite.ssrLoadModule(
      '/src/companion/domain/special-business/normal-targets/yuyuko.ts',
    ),
    vite.ssrLoadModule('/src/companion/preferences.ts'),
    vite.ssrLoadModule('/src/companion/domain/special-business/registry.ts'),
    vite.ssrLoadModule('/src/companion/automation-state.ts'),
    vite.ssrLoadModule('/src/companion/domain/service-recommendations.ts'),
    vite.ssrLoadModule('/src/lib/recommendation-data.ts'),
  ]);
} finally {
  await vite.close();
}
const {
  evaluateYuyukoPositiveSpellPair,
  evaluateYuyukoTagOrderPair,
} = yuyukoPositiveSpellModule;
const {
  compareYuyukoPlans,
  evaluateYuyukoNormalOrderPair,
  evaluateYuyukoRareOrderPair,
  isYuyukoProgressPlan,
} = yuyukoChallengeModule;
const { selectYuyukoNormalExecutionTarget } = yuyukoNormalTargetModule;
const { normalizeCompanionPreferences } = preferencesModule;
const {
  buildSpecialBusinessOrderRule,
  requiresSpecialBusinessNormalExecutionTarget,
  selectSpecialBusinessNormalExecutionTarget,
} = specialBusinessRegistryModule;
const {
  clearNormalOrderExecutionTarget,
  emptyNormalAutoOrderState,
  getCurrentNormalOrderExecutionTarget,
  lockNormalOrderExecutionTarget,
} = automationStateModule;
const {
  buildOrderRecommendations,
  buildRareCustomerMap,
  createRecommendationCacheStore,
} = serviceModule;
const {
  buildRecommendationDataSet,
  buildRecommendationDataSignature,
} = recommendationDataModule;

const root = new URL('../../', import.meta.url);
const demand = {
  type: 'rare-tag-order',
  requiredFoodTag: '下酒',
  requiredBeverageTag: '直饮',
};
const yuyukoRuleContext = {
  active: true,
  challengeTypeAvailable: true,
  challengeType: 'Challenge_Yuyuko',
  displayName: '幽幽子重修',
  category: 'challenge',
  ruleSummary: '',
  foodTargetTags: [],
  beverageTargetTags: [],
  yuumaFoodTargetRevision: 0,
  phase: 'Phase 3',
  currentAnger: null,
  maxAnger: null,
  targetAnger: null,
  recommendationPolicy: '',
  automationPolicy: '',
  source: 'test',
  error: null,
};
const storyPhaseThreeRule = buildSpecialBusinessOrderRule(
  { ...yuyukoRuleContext, challengeType: 'Story_Yuyuko' },
  'yuyuko-boss-order',
);
assert.equal(storyPhaseThreeRule.yuyukoProgressEvaluationMode, 'story-level-sum',
  '剧情版幽幽子三阶段必须由 registry 映射到等级合计评价。');
const retakePhaseThreeRule = buildSpecialBusinessOrderRule(
  yuyukoRuleContext,
  'yuyuko-boss-order',
);
assert.equal(retakePhaseThreeRule.yuyukoProgressEvaluationMode, 'retake-tag-order',
  '重修版幽幽子三阶段必须由 registry 映射到标准 Tag 点单评价。');
assert.equal(
  buildSpecialBusinessOrderRule(yuyukoRuleContext, 'ordinary-order')
    .yuyukoProgressEvaluationMode,
  'none',
  '非幽幽子目标订单不得启用三阶段评价模式。',
);
assert.equal(
  buildSpecialBusinessOrderRule(
    { ...yuyukoRuleContext, phase: 'Phase 2' },
    'yuyuko-boss-order',
  ).yuyukoProgressEvaluationMode,
  'none',
  '幽幽子非三阶段订单不得启用三阶段评价模式。',
);
assert.equal(
  requiresSpecialBusinessNormalExecutionTarget(null, null),
  false,
  '普通经营不得进入特殊料理执行目标链路。',
);
assert.equal(
  requiresSpecialBusinessNormalExecutionTarget(yuyukoRuleContext, 'ordinary-order'),
  false,
  '未被幽幽子模块认领的订单角色不得进入特殊料理执行目标链路。',
);
assert.equal(
  requiresSpecialBusinessNormalExecutionTarget(yuyukoRuleContext, 'yuyuko-boss-order'),
  true,
  '幽幽子第三阶段明确认领的订单角色必须进入特殊料理执行目标链路。',
);
assert.equal(
  requiresSpecialBusinessNormalExecutionTarget(
    { ...yuyukoRuleContext, phase: 'Phase 2' },
    'yuyuko-boss-order',
  ),
  false,
  '幽幽子一、二阶段不得被第三阶段普客执行目标门禁误拦截。',
);
assert.equal(
  requiresSpecialBusinessNormalExecutionTarget(
    { ...yuyukoRuleContext, challengeType: 'Story_Basic' },
    'ordinary-order',
  ),
  false,
  '已明确注册的被动挑战不得因缺少特殊料理目标实现而阻断普通订单。',
);
assert.equal(
  requiresSpecialBusinessNormalExecutionTarget(
    { ...yuyukoRuleContext, challengeTypeAvailable: false },
    'ordinary-order',
  ),
  true,
  '活动特殊经营身份不可读时必须 fail-closed，不能退回普通经营路径。',
);
assert.equal(
  requiresSpecialBusinessNormalExecutionTarget(
    { ...yuyukoRuleContext, challengeType: 'Story_UnsupportedChallenge' },
    'ordinary-order',
  ),
  true,
  '未适配的活动特殊经营必须进入阻断链路，不能退回普通经营路径。',
);
assert.match(
  selectSpecialBusinessNormalExecutionTarget({
    order: {
      orderKey: 'unsupported-order',
      deskCode: 0,
      guestId: 1,
      guestName: '测试客人',
      foodId: 1,
      foodName: '测试料理',
      beverageId: 2,
      beverageName: '测试酒水',
      foodPreferenceTags: [],
      beveragePreferenceTags: [],
      hasServedFood: false,
      hasServedBeverage: false,
      readyToEvaluate: false,
      hasEvaluated: false,
      source: 'runtime',
    },
    specialBusiness: { ...yuyukoRuleContext, challengeType: 'Story_UnsupportedChallenge' },
    runtime: null,
    preferences: normalizeCompanionPreferences({}),
    dataSignature: 'unsupported-special-business',
  }).message,
  /尚未适配普客自动化执行目标/,
  '未适配的活动特殊经营必须返回确定性阻断原因。',
);

const executionTargetFixture = {
  specialTargetChallenge: '',
  specialTargetOwner: '',
  specialTargetGeneration: 0,
  specialTargetRevision: 0,
  specialTargetFoodTags: [],
  specialTargetMatchMode: '',
  specialTargetSignature: '',
  matchFoodId: 1,
  matchBeverageId: 2,
  foodId: 1,
  recipeId: 1,
  recipeName: '测试料理',
  extraIngredientIds: [3],
  beverageId: 2,
  beverageName: '测试酒水',
  cookerName: '煮锅',
  foodTags: ['家常'],
  expectedFoodModifierTags: ['大份'],
  beverageTags: ['直饮'],
  reason: '测试锁存目标',
};
const emptyExecutionState = emptyNormalAutoOrderState('normal:test', 100);
const lockedExecutionState = lockNormalOrderExecutionTarget(
  emptyExecutionState,
  executionTargetFixture,
  17,
);
assert.equal(lockedExecutionState.executionTarget, executionTargetFixture,
  '开锅前必须原子锁存完整普客执行目标。');
assert.equal(lockedExecutionState.executionTargetBusinessGeneration, 17,
  '锁存目标必须绑定当前经营代际。');
assert.equal(
  getCurrentNormalOrderExecutionTarget(lockedExecutionState, 17, '', 0),
  executionTargetFixture,
  '同经营代际且同特殊目标签名时必须复用锁存目标。',
);
assert.equal(
  getCurrentNormalOrderExecutionTarget(lockedExecutionState, 18, '', 0),
  null,
  '经营代际变化后不得复用旧执行目标。',
);
assert.equal(
  getCurrentNormalOrderExecutionTarget(lockedExecutionState, 17, 'rotated-target', 0),
  null,
  '同场特殊目标签名轮换后不得复用旧执行目标。',
);
const revisionedExecutionTarget = {
  ...executionTargetFixture,
  specialTargetChallenge: 'Story_BloodPondHell',
  specialTargetOwner: 'yuuma',
  specialTargetGeneration: 17,
  specialTargetRevision: 5,
  specialTargetFoodTags: ['目标甲', '目标乙'],
  specialTargetMatchMode: 'all',
  specialTargetSignature: 'canonical-target-a',
};
const revisionedExecutionState = lockNormalOrderExecutionTarget(
  emptyExecutionState,
  revisionedExecutionTarget,
  17,
);
assert.equal(
  getCurrentNormalOrderExecutionTarget(revisionedExecutionState, 17, 'canonical-target-a', 6),
  null,
  'A -> B -> A 的规范签名恢复后，旧 revision 的锁存目标仍必须失效。',
);
const clearedExecutionState = clearNormalOrderExecutionTarget(lockedExecutionState);
assert.equal(clearedExecutionState.executionTarget, null,
  '明确退休执行目标时必须清除目标内容。');
assert.equal(clearedExecutionState.executionTargetBusinessGeneration, 0,
  '明确退休执行目标时必须同时清除经营代际。');

const phaseTwoPair = {
  food: buildFood({
    level: 1,
    activeTags: ['下酒', '小巧'],
    matchedPositiveTags: ['下酒', '小巧'],
  }),
  beverage: buildBeverage({
    level: 1,
    activeTags: ['直饮', '辛'],
    matchedTags: ['直饮', '辛'],
  }),
};
const phaseTwoEvaluation = evaluateYuyukoPositiveSpellPair(
  phaseTwoPair.food,
  phaseTwoPair.beverage,
  demand,
);
assert.equal(phaseTwoEvaluation.canTriggerPositiveSpell, true,
  '二阶段应允许当前客人不厌恶的“小巧”组合触发正面符卡。');
assert.equal(phaseTwoEvaluation.baseDemandScore, 2,
  '料理与酒水的点单 Tag 应只计入基础满足度。');
assert.equal(phaseTwoEvaluation.extraPreferenceScore, 2,
  '排除点单 Tag 后的两个额外喜好应达到正面符卡阈值。');
assert.deepEqual(phaseTwoEvaluation.negativeTags, [],
  '幽幽子三阶段的全局厌恶 Tag 不应泄漏到二阶段。');

const orderedTagsOnly = evaluateYuyukoPositiveSpellPair(
  buildFood({
    activeTags: ['下酒'],
    matchedPositiveTags: ['下酒'],
  }),
  buildBeverage({
    activeTags: ['直饮'],
    matchedTags: ['直饮'],
  }),
  demand,
);
assert.equal(orderedTagsOnly.baseDemandScore, 2);
assert.equal(orderedTagsOnly.extraPreferenceScore, 0,
  '料理和酒水点单 Tag 不能同时充当额外喜好重复计分。');
assert.equal(orderedTagsOnly.canTriggerPositiveSpell, false,
  '仅满足点单 Tag 不足以触发二阶段正面符卡。');

const currentGuestHate = evaluateYuyukoPositiveSpellPair(
  buildFood({
    activeTags: ['下酒', '小巧', '肉'],
    matchedPositiveTags: ['下酒', '肉'],
    matchedNegativeTags: ['小巧'],
  }),
  phaseTwoPair.beverage,
  demand,
);
assert.equal(currentGuestHate.canTriggerPositiveSpell, false,
  '当前客人的真实厌恶 Tag 必须阻止二阶段执行。');
assert.deepEqual(currentGuestHate.negativeTags, ['小巧']);

const orderedTagExcludedFromHateMatching = evaluateYuyukoPositiveSpellPair(
  buildFood({
    activeTags: ['下酒', '小巧'],
    matchedPositiveTags: ['下酒', '小巧'],
    matchedNegativeTags: ['下酒'],
  }),
  phaseTwoPair.beverage,
  demand,
);
assert.equal(orderedTagExcludedFromHateMatching.canTriggerPositiveSpell, true,
  '原生评价会在额外厌恶匹配前排除料理点单 Tag。');
assert.deepEqual(orderedTagExcludedFromHateMatching.negativeTags, []);

assert.equal(phaseTwoPair.food.recipe.level + phaseTwoPair.beverage.beverage.level, 2);
assert.equal(phaseTwoEvaluation.canTriggerPositiveSpell, true,
  '二阶段判定不得使用三阶段的等级合计阈值。');

const highLevelTagOnlyFood = buildFood({
  level: 5,
  activeTags: ['下酒'],
  matchedPositiveTags: ['下酒'],
});
const highLevelTagOnlyBeverage = buildBeverage({
  level: 4,
  activeTags: ['直饮'],
  matchedTags: ['直饮'],
});
const highLevelTagOnlyPlan = buildRarePlan(
  highLevelTagOnlyFood,
  highLevelTagOnlyBeverage,
);
const highLevelRetakeEvaluation = evaluateYuyukoRareOrderPair(
  'retake-tag-order',
  highLevelTagOnlyFood,
  highLevelTagOnlyBeverage,
  demand,
);
assert.equal(highLevelRetakeEvaluation.levelSum, 9);
assert.equal(highLevelRetakeEvaluation.baseDemandScore, 2);
assert.equal(highLevelRetakeEvaluation.extraPreferenceScore, 0);
assert.equal(highLevelRetakeEvaluation.evaluationScore, 2,
  '重修版 SpecialOrder 即使等级合计很高，仅满足点单 Tag 时也只能获得 Normal。');
assert.equal(highLevelRetakeEvaluation.canProgress, false,
  '重修版 SpecialOrder 不得用等级合计把仅满足点单的组合误判为可推进。');
assert.equal(isYuyukoProgressPlan(highLevelTagOnlyPlan, 'retake-tag-order'), false);

const lowLevelLikedFood = buildFood({
  level: 1,
  activeTags: ['下酒', '肉'],
  matchedPositiveTags: ['下酒', '肉'],
});
const lowLevelLikedBeverage = buildBeverage({
  level: 1,
  activeTags: ['直饮'],
  matchedTags: ['直饮'],
});
const lowLevelLikedPlan = buildRarePlan(lowLevelLikedFood, lowLevelLikedBeverage);
const lowLevelTagEvaluation = evaluateYuyukoTagOrderPair(
  lowLevelLikedFood,
  lowLevelLikedBeverage,
  demand,
);
assert.equal(lowLevelTagEvaluation.baseDemandScore, 2);
assert.equal(lowLevelTagEvaluation.extraPreferenceScore, 1);
assert.deepEqual(lowLevelTagEvaluation.foodExtraPreferenceTags, ['肉']);
assert.equal(lowLevelTagEvaluation.evaluationScore, 3);
const lowLevelRetakeEvaluation = evaluateYuyukoRareOrderPair(
  'retake-tag-order',
  lowLevelLikedFood,
  lowLevelLikedBeverage,
  demand,
);
assert.equal(lowLevelRetakeEvaluation.levelSum, 2);
assert.equal(lowLevelRetakeEvaluation.evaluationScore, 3,
  '重修版 SpecialOrder 应按完整上菜 Tag 的点单和额外喜好得到 Good。');
assert.equal(lowLevelRetakeEvaluation.canProgress, true,
  '低等级组合只要完整 Tag 评价达到 Good，重修版也必须允许推进。');
assert.equal(isYuyukoProgressPlan(lowLevelLikedPlan, 'retake-tag-order'), true);

const requestedHateRetakeFood = buildFood({
  level: 1,
  activeTags: ['下酒', '肉'],
  matchedPositiveTags: ['下酒', '肉'],
  matchedNegativeTags: ['下酒'],
});
const requestedHateRetakePlan = buildRarePlan(
  requestedHateRetakeFood,
  lowLevelLikedBeverage,
);
const requestedHateRetakeEvaluation = evaluateYuyukoRareOrderPair(
  'retake-tag-order',
  requestedHateRetakeFood,
  lowLevelLikedBeverage,
  demand,
);
assert.deepEqual(requestedHateRetakeEvaluation.negativeTags, [],
  '重修版必须在厌恶匹配中排除料理点单 Tag 本身。');
assert.deepEqual(requestedHateRetakeEvaluation.foodExtraPreferenceTags, ['肉']);
assert.equal(requestedHateRetakeEvaluation.evaluationScore, 3);
assert.equal(requestedHateRetakeEvaluation.canProgress, true,
  '料理点单 Tag 同时属于当前稀客厌恶时，额外喜好仍应使组合达到 Good。');
assert.equal(isYuyukoProgressPlan(requestedHateRetakePlan, 'retake-tag-order'), true);

const hatedRetakeFood = buildFood({
  level: 5,
  activeTags: ['下酒', '小巧'],
  matchedPositiveTags: ['下酒'],
  matchedNegativeTags: ['小巧'],
});
const hatedRetakeEvaluation = evaluateYuyukoRareOrderPair(
  'retake-tag-order',
  hatedRetakeFood,
  highLevelTagOnlyBeverage,
  demand,
);
assert.deepEqual(hatedRetakeEvaluation.negativeTags, ['小巧']);
assert.equal(hatedRetakeEvaluation.evaluationScore, 1);
assert.equal(hatedRetakeEvaluation.canProgress, false,
  '重修版 SpecialOrder 的完整上菜 Tag 命中厌恶时必须阻断推进。');

const highLevelStoryEvaluation = evaluateYuyukoRareOrderPair(
  'story-level-sum',
  highLevelTagOnlyFood,
  highLevelTagOnlyBeverage,
  demand,
);
assert.equal(highLevelStoryEvaluation.evaluationScore, 4);
assert.equal(highLevelStoryEvaluation.canProgress, true,
  '剧情版 SpecialOrder 必须继续按等级合计获得 ExGood。');
const lowLevelStoryEvaluation = evaluateYuyukoRareOrderPair(
  'story-level-sum',
  lowLevelLikedFood,
  lowLevelLikedBeverage,
  demand,
);
assert.equal(lowLevelStoryEvaluation.evaluationScore, 2);
assert.equal(lowLevelStoryEvaluation.canProgress, false,
  '重修版的额外喜好 Tag 不得泄漏到剧情版等级评价。');

const retakeSortedPlans = [highLevelTagOnlyPlan, lowLevelLikedPlan]
  .sort((left, right) => compareYuyukoPlans(left, right, 'retake-tag-order'));
assert.equal(retakeSortedPlans[0], lowLevelLikedPlan,
  '重修版排序必须优先低等级但完整 Tag 达到 Good 的方案。');
const storySortedPlans = [lowLevelLikedPlan, highLevelTagOnlyPlan]
  .sort((left, right) => compareYuyukoPlans(left, right, 'story-level-sum'));
assert.equal(storySortedPlans[0], highLevelTagOnlyPlan,
  '剧情版排序必须继续优先等级合计更高的方案。');

assertServiceRecommendationModeRouting();

assert.equal(typeof evaluateYuyukoNormalOrderPair, 'function',
  '幽幽子三阶段普客评价必须提供按场景类型区分的唯一可执行入口。');

const retakeBaseFood = buildFood({
  level: 5,
  price: 30,
  recipePositiveTags: ['素', '小巧', '清淡'],
  activeTags: ['素', '小巧', '清淡'],
  matchedNegativeTags: ['素', '小巧', '清淡'],
});
const highLevelBeverage = buildBeverage({ level: 4 });
const yuyukoModifierPreferences = {
  positiveTags: ['高级', '传说', '和风', '大份', '肉', '水产', '中华', '饱腹'],
  negativeTags: ['素', '小巧', '清淡'],
};
const retakeBaseEvaluation = evaluateYuyukoNormalOrderPair(
  'Challenge_Yuyuko',
  retakeBaseFood,
  highLevelBeverage,
  yuyukoModifierPreferences,
);
assert.equal(retakeBaseEvaluation.mode, 'retake-food-modifiers');
assert.equal(retakeBaseEvaluation.levelSum, 9);
assert.equal(retakeBaseEvaluation.evaluationScore, 2,
  '重修版摊位的原料理即使等级合计很高，无有效加料修正时仍应是普通评价。');
assert.deepEqual(retakeBaseEvaluation.effectiveModifierTags, ['煮锅'],
  '原料理自带的素、小巧、清淡不能被误认为本次上菜的加料修正。');
assert.deepEqual(retakeBaseEvaluation.positiveModifierTags, []);
assert.deepEqual(retakeBaseEvaluation.negativeModifierTags, []);

const retakeLikedExtraEvaluation = evaluateYuyukoNormalOrderPair(
  'Challenge_Yuyuko',
  buildFood({
    level: 5,
    price: 30,
    recipePositiveTags: ['素', '小巧', '清淡'],
    extraIngredients: [buildIngredient({ id: 2, name: '肉类加料', tags: ['肉'] })],
    activeTags: ['小巧', '清淡', '肉'],
    suppressedTags: ['素'],
    matchedPositiveTags: [],
    matchedNegativeTags: ['肉', '小巧', '清淡'],
  }),
  highLevelBeverage,
  yuyukoModifierPreferences,
);
assert.equal(retakeLikedExtraEvaluation.evaluationScore, 3,
  '评价必须使用显式运行时偏好，不能读取候选搜索阶段的 matched 摘要。');
assert.deepEqual(retakeLikedExtraEvaluation.effectiveModifierTags, ['煮锅', '肉']);
assert.deepEqual(retakeLikedExtraEvaluation.positiveModifierTags, ['肉']);
assert.deepEqual(retakeLikedExtraEvaluation.negativeModifierTags, []);

const retakeRepeatedBaseHateEvaluation = evaluateYuyukoNormalOrderPair(
  'Challenge_Yuyuko',
  buildFood({
    level: 5,
    price: 30,
    recipePositiveTags: ['素', '小巧', '清淡'],
    extraIngredients: [buildIngredient({ id: 3, name: '清淡加料', tags: ['清淡'] })],
    activeTags: ['素', '小巧', '清淡'],
    matchedPositiveTags: ['清淡'],
    matchedNegativeTags: [],
  }),
  highLevelBeverage,
  yuyukoModifierPreferences,
);
assert.equal(retakeRepeatedBaseHateEvaluation.evaluationScore, 2,
  '额外材料重复原配方已有厌恶 Tag 时，原生 addedTags 不会重复计入该 Tag。');
assert.deepEqual(retakeRepeatedBaseHateEvaluation.effectiveModifierTags, ['煮锅']);
assert.deepEqual(retakeRepeatedBaseHateEvaluation.positiveModifierTags, []);
assert.deepEqual(retakeRepeatedBaseHateEvaluation.negativeModifierTags, []);

const retakeRepeatedBaseLikeEvaluation = evaluateYuyukoNormalOrderPair(
  'Challenge_Yuyuko',
  buildFood({
    level: 5,
    price: 30,
    recipePositiveTags: ['肉'],
    extraIngredients: [buildIngredient({ id: 4, name: '重复肉类加料', tags: ['肉'] })],
    activeTags: ['肉'],
  }),
  highLevelBeverage,
  yuyukoModifierPreferences,
);
assert.equal(retakeRepeatedBaseLikeEvaluation.evaluationScore, 2,
  '额外材料重复原配方已有喜好 Tag 时，不得虚构 Good 评价。');
assert.deepEqual(retakeRepeatedBaseLikeEvaluation.effectiveModifierTags, ['煮锅']);
assert.deepEqual(retakeRepeatedBaseLikeEvaluation.positiveModifierTags, []);

const retakeNewHatedExtraEvaluation = evaluateYuyukoNormalOrderPair(
  'Challenge_Yuyuko',
  buildFood({
    level: 5,
    price: 30,
    recipePositiveTags: ['素', '小巧'],
    extraIngredients: [buildIngredient({ id: 5, name: '新增清淡加料', tags: ['清淡'] })],
    activeTags: ['素', '小巧', '清淡'],
  }),
  highLevelBeverage,
  yuyukoModifierPreferences,
);
assert.equal(retakeNewHatedExtraEvaluation.evaluationScore, 1,
  '额外材料实际新增厌恶 Tag 时必须降为差评并阻止自动化推进。');
assert.deepEqual(retakeNewHatedExtraEvaluation.effectiveModifierTags, ['煮锅', '清淡']);
assert.deepEqual(retakeNewHatedExtraEvaluation.positiveModifierTags, []);
assert.deepEqual(retakeNewHatedExtraEvaluation.negativeModifierTags, ['清淡']);

const storyEvaluation = evaluateYuyukoNormalOrderPair(
  'Story_Yuyuko',
  retakeBaseFood,
  highLevelBeverage,
  null,
);
assert.equal(storyEvaluation.mode, 'story-level-sum');
assert.equal(storyEvaluation.evaluationScore, 4,
  '剧情版三阶段普客评价必须继续只按料理与酒水等级合计计算。');
assert.deepEqual(storyEvaluation.negativeModifierTags, [],
  '重修版摊位的加料喜恶规则不得泄漏到剧情版评价。');

assertYuyukoRetakeSearchIgnoresRepeatedBasePreferenceTags();
assertYuyukoRetakeBaseCandidateSurvivesBeamTruncation();
assertYuyukoStoryDoesNotRequireRuntimeYuyukoProfile();
assertRuntimeYuyukoProfileProjection();

await assertSourceContracts();

console.log('PASS: Yuyuko phase-two and challenge-specific phase-three evaluations stay isolated.');

function assertYuyukoRetakeBaseCandidateSurvivesBeamTruncation() {
  const selection = selectYuyukoNormalExecutionTarget(
    buildYuyukoNormalSelectionArgs('Challenge_Yuyuko', true),
  );
  assert.ok(selection.target,
    '重修版精确订单必须在通用 beam 截断后仍保留可执行的无加料原菜。');
  assert.equal(selection.target.executionMode, 'refresh',
    '所有加料只重复基础喜好 Tag 时，应退回无加料 Normal 清单。');
  assert.deepEqual(selection.target.extraIngredientIds, [],
    '安全清单必须选择显式合并的无加料候选，不能被前 16 个加料状态替代。');
  assert.deepEqual(selection.target.expectedFoodModifierTags, ['煮锅'],
    '清理目标必须发布与原生 GetTagDiff addedTags 一致的预期修饰 Tag。');
}

function assertYuyukoRetakeSearchIgnoresRepeatedBasePreferenceTags() {
  const selection = selectYuyukoNormalExecutionTarget(
    buildYuyukoNormalSelectionArgs('Challenge_Yuyuko', true, {
      includeNewLikedExtra: true,
    }),
  );
  assert.ok(selection.target,
    '大量重复基础喜好加料不能挤掉真正新增喜好 Tag 的候选。');
  assert.equal(selection.target.executionMode, 'progress');
  assert.ok(selection.target.extraIngredientIds.includes(999),
    '推进目标必须包含真正新增“大份”喜好 Tag 的加料。');
  assert.deepEqual(selection.target.expectedFoodModifierTags, ['煮锅', '大份'],
    '推进目标必须发布相对原配方新增的精确 modifier Tag。');
}

function assertYuyukoStoryDoesNotRequireRuntimeYuyukoProfile() {
  const selection = selectYuyukoNormalExecutionTarget(
    buildYuyukoNormalSelectionArgs('Story_Yuyuko', false),
  );
  assert.ok(selection.target,
    '剧情版第三阶段只按精确订单等级和评价，不应依赖 characterId=23 档案。');
  assert.equal(selection.target.executionMode, 'progress');
  assert.deepEqual(selection.target.extraIngredientIds, [],
    '剧情版加料不会改变等级和，候选生成必须固定为无加料原菜。');
}

function assertRuntimeYuyukoProfileProjection() {
  const selectionArgs = buildYuyukoNormalSelectionArgs('Challenge_Yuyuko', false, {
    includeNewLikedExtra: true,
  });
  const rawYuyukoProfile = {
    id: 23,
    name: '西行寺幽幽子',
    places: [],
    positiveTags: ['肉', '大份'],
    negativeTags: ['清淡'],
    beverageTags: ['直饮'],
  };
  const rawMappedYuyuko = {
    ...rawYuyukoProfile,
    id: 40,
    places: ['妖怪兽道'],
    positiveTags: ['清淡'],
    negativeTags: ['肉', '大份'],
    beverageTags: ['低酒精'],
  };
  const runtimeSnapshot = {
    isComplete: true,
    source: 'runtime-test',
    status: 'complete',
    recipes: selectionArgs.data.recipes,
    ingredients: selectionArgs.data.ingredients,
    beverages: selectionArgs.data.beverages,
    normalCustomers: [{
      id: 9001,
      name: '运行时规范化测试普客',
      places: ['妖怪兽道'],
      positiveTags: ['家常'],
      beverageTags: [],
    }],
    rareCustomers: [rawYuyukoProfile, rawMappedYuyuko],
    foodTagIdMap: {},
    beverageTagIdMap: {},
    tagPriorityRules: [],
  };
  const runtimeData = buildRecommendationDataSet(runtimeSnapshot);

  assert.equal(runtimeData.source, 'runtime',
    '完整运行时快照必须成功构造推荐数据集。');
  assert.deepEqual(runtimeData.rareCustomers.map((customer) => customer.id), [40],
    '无日间地点的基础 characterId=23 不能污染常规可推荐稀客集合。');
  assert.ok(runtimeData.rareCustomerProfiles.some((profile) => profile.id === 23),
    '评价档案必须保留无日间地点的基础 characterId=23。');

  const selection = selectYuyukoNormalExecutionTarget({
    ...selectionArgs,
    data: runtimeData,
  });
  assert.ok(selection.target,
    '幽幽子重修第三阶段必须能从真实运行时规范化结果读取 characterId=23 档案并生成目标。');
  assert.doesNotMatch(selection.message, /characterId=23/,
    '存在基础评价档案时不得再报告 characterId=23 缺失。');

  const capturedRareOrder = {
    traceId: 'R-0018',
    deskCode: 2,
    guestId: 23,
    runtimeGuestId: 23,
    guestName: '西行寺幽幽子',
    specialBusinessRole: 'yuyuko-boss-order',
    automationAllowed: true,
    foodTagId: 1,
    foodTag: '肉',
    beverageTagId: 2,
    beverageTag: '直饮',
    source: 'RuntimeCapture:ControllerOrderAdd+OrderAdd+OrderAdd',
    isFreeOrder: true,
    hasServedFood: false,
    hasServedBeverage: false,
  };
  const serviceResult = buildOrderRecommendations(
    [capturedRareOrder],
    selectionArgs.runtime,
    buildRareCustomerMap(runtimeData),
    createRecommendationCacheStore(),
    { version: 1, recipes: [], beverages: [] },
    { version: 1, enabled: true, recipes: [] },
    selectionArgs.preferences,
    [],
    yuyukoRuleContext,
    [],
    runtimeData,
    { usage: 'automation' },
  );
  assert.equal(serviceResult.recommendationIssues.length, 0,
    '幽幽子三阶段 canonical 23 不应再被普通地点目录过滤为无法映射。');
  assert.equal(serviceResult.recommendations.length, 1);
  assert.equal(serviceResult.recommendations[0].customer.id, 23,
    '服务推荐必须使用 canonical 23 的完整评价档案，不能借用同名 mapped 40。');
  assert.ok(serviceResult.recommendations[0].executionPlans.length > 0,
    '诊断包中的 guestId/runtimeGuestId 均为 23 的订单必须恢复可执行推荐与自动化目标。');

  const ordinaryMappedResult = buildOrderRecommendations(
    [{
      ...capturedRareOrder,
      traceId: 'R-MAPPED-ORDINARY',
      guestId: 40,
      runtimeGuestId: 40,
      specialBusinessRole: '',
    }],
    selectionArgs.runtime,
    buildRareCustomerMap(runtimeData),
    createRecommendationCacheStore(),
    { version: 1, recipes: [], beverages: [] },
    { version: 1, enabled: true, recipes: [] },
    selectionArgs.preferences,
    [],
    yuyukoRuleContext,
    [],
    runtimeData,
    { usage: 'automation' },
  );
  assert.equal(ordinaryMappedResult.recommendationIssues.length, 0,
    '同场景普通 mapped 40 订单仍应使用普通地点目录。');
  assert.equal(ordinaryMappedResult.recommendations[0]?.customer.id, 40,
    '特殊经营档案解析不得改变非特殊角色的普通目录身份。');

  const dataWithoutBaseProfile = buildRecommendationDataSet({
    ...runtimeSnapshot,
    rareCustomers: [rawMappedYuyuko],
  });
  const blockedSelection = selectYuyukoNormalExecutionTarget({
    ...selectionArgs,
    data: dataWithoutBaseProfile,
  });
  assert.equal(blockedSelection.target, null,
    '只有 mapped guest 而没有 canonical characterId=23 档案时必须 fail-closed。');
  assert.match(blockedSelection.message, /characterId=23/,
    '缺少 canonical 档案时必须保留可诊断的精确身份错误。');
  const blockedServiceResult = buildOrderRecommendations(
    [capturedRareOrder],
    selectionArgs.runtime,
    buildRareCustomerMap(dataWithoutBaseProfile),
    createRecommendationCacheStore(),
    { version: 1, recipes: [], beverages: [] },
    { version: 1, enabled: true, recipes: [] },
    selectionArgs.preferences,
    [],
    yuyukoRuleContext,
    [],
    dataWithoutBaseProfile,
    { usage: 'automation' },
  );
  assert.equal(blockedServiceResult.recommendations.length, 0,
    '缺少 canonical 23 档案时不得按同名回退到 mapped 40。');
  assert.match(
    blockedServiceResult.recommendationIssues[0]?.message ?? '',
    /无法把该稀客映射/,
  );

  const changedProfileData = buildRecommendationDataSet({
    ...runtimeSnapshot,
    rareCustomers: runtimeSnapshot.rareCustomers.map((customer) => (
      customer.id === 23
        ? { ...customer, positiveTags: [...customer.positiveTags, '高级'] }
        : customer
    )),
  });
  assert.notEqual(
    buildRecommendationDataSignature(runtimeData),
    buildRecommendationDataSignature(changedProfileData),
    '评价档案变化必须改变推荐数据签名，防止 Worker 复用旧档案缓存。',
  );

  const cacheProbeArgs = {
    ...selectionArgs,
    order: { ...selectionArgs.order, traceId: 'N-DATA-GENERATION' },
    data: dataWithoutBaseProfile,
    dataSignature: buildRecommendationDataSignature(dataWithoutBaseProfile),
  };
  const cacheProbeBlocked = selectSpecialBusinessNormalExecutionTarget(cacheProbeArgs);
  assert.equal(cacheProbeBlocked.target, null,
    '缓存代际用例必须先保存缺少 canonical 档案的阻塞结果。');
  const cacheProbeRecovered = selectSpecialBusinessNormalExecutionTarget({
    ...cacheProbeArgs,
    data: runtimeData,
    dataSignature: buildRecommendationDataSignature(runtimeData),
  });
  assert.ok(cacheProbeRecovered.target,
    '完整数据签名变化后必须清除特殊经营目标缓存，不能复用旧阻塞结果。');
}

function buildYuyukoNormalSelectionArgs(
  challengeType,
  includeYuyukoProfile,
  { includeNewLikedExtra = false } = {},
) {
  const baseIngredient = buildIngredient({
    id: 100,
    name: '基础材料',
    tags: ['肉'],
  });
  const extraIngredients = Array.from({ length: 24 }, (_, index) => buildIngredient({
    id: 200 + index,
    name: `截断加料 ${index + 1}`,
    tags: ['肉'],
  }));
  if (includeNewLikedExtra) {
    extraIngredients.push(buildIngredient({
      id: 999,
      name: '真正新增喜好加料',
      tags: ['大份'],
    }));
  }
  const recipe = {
    ...buildFood({
      level: 4,
      price: 30,
      recipePositiveTags: ['肉'],
      recipeIngredients: [baseIngredient.name],
      activeTags: ['肉'],
    }).recipe,
    id: 81,
    recipeId: 181,
    name: '截断测试料理',
  };
  const beverage = {
    ...buildBeverage({ level: 2 }).beverage,
    id: 91,
    name: '截断测试酒水',
  };
  const rareCustomers = includeYuyukoProfile
    ? [{
        id: 23,
        name: '西行寺幽幽子',
        description: '',
        dlc: 0,
        places: [],
        price: [9999, 9999],
        enduranceLimit: 1,
        positiveTags: ['肉', '大份'],
        negativeTags: ['清淡'],
        beverageTags: [],
        collection: false,
        evaluation: {},
        spellCards: { positive: [], negative: [] },
      }]
    : [];
  const ingredients = [baseIngredient, ...extraIngredients];

  return {
    order: {
      traceId: 'N-BEAM',
      deskCode: 1,
      guestName: 'Yuyuko',
      specialBusinessRole: 'yuyuko-boss-order',
      foodId: recipe.id,
      foodName: recipe.name,
      beverageId: beverage.id,
      beverageName: beverage.name,
      hasServedFood: false,
      hasServedBeverage: false,
      readyToEvaluate: false,
    },
    specialBusiness: {
      active: true,
      challengeTypeAvailable: true,
      challengeType,
      displayName: '幽幽子挑战',
      category: 'challenge',
      ruleSummary: '',
      foodTargetTags: [],
      beverageTargetTags: [],
      phase: 'Phase3',
      currentAnger: null,
      maxAnger: null,
      targetAnger: null,
      recommendationPolicy: '',
      automationPolicy: '',
      source: 'test',
      error: null,
    },
    runtime: {
      availableRecipeIds: [recipe.id],
      availableBeverageIds: [beverage.id],
      availableIngredientIds: ingredients.map((ingredient) => ingredient.id),
      ownedIngredientQty: Object.fromEntries(
        ingredients.map((ingredient) => [ingredient.id, 10]),
      ),
      ownedBeverageQty: { [beverage.id]: 10 },
      ...buildCookerSnapshot([1]),
      popularFoodTag: null,
      popularHateFoodTag: null,
      famousShopEnabled: false,
    },
    preferences: normalizeCompanionPreferences({}),
    data: {
      recipes: [recipe],
      ingredients,
      beverages: [beverage],
      normalCustomers: [],
      rareCustomers,
      rareCustomerProfiles: rareCustomers.map((customer) => ({
        id: customer.id,
        name: customer.name,
        positiveTags: customer.positiveTags,
        negativeTags: customer.negativeTags,
        beverageTags: customer.beverageTags,
      })),
      foodTagIdMap: {},
      beverageTagIdMap: {},
      tagPriorityRules: [],
      source: 'runtime',
      status: 'test',
    },
  };
}

function assertServiceRecommendationModeRouting() {
  const storyHighLevel = buildYuyukoRareServiceRecommendation({
    challengeType: 'Story_Yuyuko',
    recipeLevel: 5,
    beverageLevel: 4,
    recipeTags: ['下酒'],
    customerPositiveTags: ['下酒'],
  });
  assert.ok(storyHighLevel.executionPlans.length > 0,
    '服务链路必须让剧情版三阶段按等级合计接受高等级组合。');

  const retakeHighLevel = buildYuyukoRareServiceRecommendation({
    challengeType: 'Challenge_Yuyuko',
    recipeLevel: 5,
    beverageLevel: 4,
    recipeTags: ['下酒'],
    customerPositiveTags: ['下酒'],
  });
  assert.equal(retakeHighLevel.executionPlans.length, 0,
    '服务链路不得让重修版三阶段沿用剧情版等级合计规则。');
  assert.equal(retakeHighLevel.customer.id, 23,
    '特殊经营必须按 canonical guestId 从完整评价档案读取幽幽子，不得按同名回退到 mapped 40。');
  assert.deepEqual(retakeHighLevel.customer.places, [],
    '特殊经营评价档案不应伪造普通日间地点。');

  const retakeRequestedHate = buildYuyukoRareServiceRecommendation({
    challengeType: 'Challenge_Yuyuko',
    recipeLevel: 1,
    beverageLevel: 1,
    recipeTags: ['下酒', '肉'],
    customerPositiveTags: ['下酒', '肉'],
    customerNegativeTags: ['下酒'],
  });
  assert.ok(retakeRequestedHate.executionPlans.length > 0,
    '服务候选过滤必须排除与点单 Tag 重合的厌恶，并保留可达 Good 的重修组合。');
  assert.deepEqual(
    retakeRequestedHate.executionPlans[0].food?.matchedNegativeTags,
    ['下酒'],
    '集成用例必须证明候选原始厌恶中确实包含料理点单 Tag。',
  );
  assert.equal(
    evaluateYuyukoRareOrderPair(
      'retake-tag-order',
      retakeRequestedHate.executionPlans[0].food,
      retakeRequestedHate.executionPlans[0].beverage,
      retakeRequestedHate.executionPlans[0].demand,
    ).canProgress,
    true,
    '服务发布的主执行计划必须按重修 Tag 模式达到 Good。',
  );

  const storyLowLevel = buildYuyukoRareServiceRecommendation({
    challengeType: 'Story_Yuyuko',
    recipeLevel: 1,
    beverageLevel: 1,
    recipeTags: ['下酒', '肉'],
    customerPositiveTags: ['下酒', '肉'],
    customerNegativeTags: ['下酒'],
  });
  assert.equal(storyLowLevel.executionPlans.length, 0,
    '服务链路不得让重修版额外喜好评价泄漏到剧情版三阶段。');
}

function buildYuyukoRareServiceRecommendation({
  challengeType,
  recipeLevel,
  beverageLevel,
  recipeTags,
  customerPositiveTags,
  customerNegativeTags = [],
}) {
  const customer = {
    id: 23,
    name: '西行寺幽幽子',
    description: '',
    dlc: 0,
    places: ['妖怪兽道'],
    price: [9999, 9999],
    enduranceLimit: 1,
    positiveTags: customerPositiveTags,
    negativeTags: customerNegativeTags,
    beverageTags: ['直饮'],
    collection: false,
    evaluation: {},
    spellCards: { positive: [], negative: [] },
  };
  const mappedCustomer = {
    ...customer,
    id: 40,
    places: ['博丽神社'],
  };
  const ingredient = buildIngredient({
    id: 101,
    name: '基础材料',
    tags: ['肉'],
  });
  const recipe = {
    ...buildFood({
      level: recipeLevel,
      price: 100,
      recipePositiveTags: recipeTags,
      recipeIngredients: [ingredient.name],
      activeTags: recipeTags,
    }).recipe,
    id: 201,
    recipeId: 301,
    name: '稀客服务链路测试料理',
  };
  const beverage = {
    ...buildBeverage({
      level: beverageLevel,
      activeTags: ['直饮'],
      matchedTags: ['直饮'],
    }).beverage,
    id: 401,
    name: '稀客服务链路测试酒水',
  };
  const data = {
    recipes: [recipe],
    ingredients: [ingredient],
    beverages: [beverage],
    normalCustomers: [],
    rareCustomers: [mappedCustomer],
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
    availableIngredientIds: [ingredient.id],
    ownedIngredientQty: { [ingredient.id]: 10 },
    ownedBeverageQty: { [beverage.id]: 10 },
    ...buildCookerSnapshot([1]),
    popularFoodTag: null,
    popularHateFoodTag: null,
    famousShopEnabled: false,
  };
  const order = {
    traceId: `R-${challengeType}-${recipeLevel}-${beverageLevel}`,
    deskCode: 1,
    guestId: customer.id,
    runtimeGuestId: 2300,
    guestName: customer.name,
    specialBusinessRole: 'yuyuko-boss-order',
    automationAllowed: true,
    foodTagId: 1,
    foodTag: '下酒',
    beverageTagId: 2,
    beverageTag: '直饮',
    source: 'test',
    hasServedFood: false,
    hasServedBeverage: false,
  };
  const result = buildOrderRecommendations(
    [order],
    runtime,
    new Map([[mappedCustomer.id, mappedCustomer]]),
    createRecommendationCacheStore(),
    { version: 1, recipes: [], beverages: [] },
    { version: 1, enabled: true, recipes: [] },
    normalizeCompanionPreferences({
      filterMissingCookers: true,
      recommendationBudgetPolicy: 'block',
    }),
    [],
    { ...yuyukoRuleContext, challengeType },
    [],
    data,
    { usage: 'automation' },
  );
  assert.equal(result.recommendations.length, 1);
  return result.recommendations[0];
}

function buildFood({
  level = 3,
  price = 100,
  activeTags = ['下酒'],
  recipePositiveTags = activeTags,
  recipeIngredients = ['基础材料'],
  extraIngredients = [],
  suppressedTags = [],
  matchedPositiveTags = [],
  matchedNegativeTags = [],
} = {}) {
  return {
    recipe: {
      id: 1,
      recipeId: 1,
      name: '测试料理',
      description: '',
      ingredients: recipeIngredients,
      positiveTags: recipePositiveTags,
      negativeTags: [],
      cooker: '煮锅',
      baseCookTime: 1,
      dlc: 0,
      level,
      price,
      from: {},
    },
    extraIngredients,
    extraIngredientReasonTags: {},
    activeTags,
    suppressedTags,
    matchedPositiveTags,
    matchedNegativeTags,
    matchedSpecialFoodTargetTags: [],
    meetsRequiredFood: activeTags.includes(demand.requiredFoodTag),
    baseCost: 10,
    extraCost: 0,
    resourcePressure: 0,
    cookerAvailable: true,
    conditionResults: [],
  };
}

function buildIngredient({
  id,
  name,
  tags,
}) {
  return {
    id,
    name,
    description: '',
    type: '其他',
    tags,
    dlc: 0,
    level: 1,
    price: 1,
    from: {},
  };
}

function buildRarePlan(food, beverage) {
  return {
    demand,
    food,
    beverage,
    bucket: 'complete',
    estimatedPrice: food.recipe.price + beverage.beverage.price,
    budget: null,
    conditionResults: [],
    reasons: [],
    warnings: [],
  };
}

function buildBeverage({
  level = 3,
  activeTags = ['直饮'],
  matchedTags = [],
} = {}) {
  return {
    beverage: {
      id: 1,
      name: '测试酒水',
      description: '',
      tags: activeTags,
      dlc: 0,
      level,
      price: 100,
      from: {},
    },
    activeTags,
    matchedTags,
    meetsRequiredBeverage: activeTags.includes(demand.requiredBeverageTag),
    ownedQuantity: 10,
    conditionResults: [],
  };
}

async function assertSourceContracts() {
  const [
    service,
    sortProfile,
    ruleTypes,
    yuyukoRule,
    yuyukoChallenge,
    yuyukoPositiveSpell,
    yuyukoNormalTarget,
    yuyukoRuntimePolicy,
    foodModifierValidation,
    workbench,
    automationState,
    automationDomain,
    companionApi,
    orderRecommendationWorker,
    orderPreparationModels,
    localApiServer,
    runtimeOrderPreparationService,
    runtimeOrderDirectDelivery,
  ] = await Promise.all([
    readFile(new URL('apps/companion/src/companion/domain/service-recommendations.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/recommendation-engine/sort-profile.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/rules/types.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/rules/yuyuko.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/yuyuko-challenge.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/yuyuko-positive-spell.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/normal-targets/yuyuko.ts', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/SpecialBusiness/RuntimeOrderPreparationService.YuyukoChallengePolicy.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/SpecialBusiness/RuntimeOrderPreparationService.FoodModifierValidation.cs', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/automation-state.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/automation.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/api.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/workers/order-recommendations.worker.ts', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/LocalApi/OrderPreparationModels.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/LocalApi/LocalApiServer.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.DirectDelivery.cs', root), 'utf8'),
  ]);

  const recommendationSources = `${service}\n${sortProfile}\n${ruleTypes}\n${yuyukoRule}`;
  const workerDataResolver = functionSlice(
    orderRecommendationWorker,
    'resolveRecommendationData',
    'now',
  );
  for (const cacheName of ['orders', 'foodCandidates', 'beverageCandidates']) {
    assert.ok(
      workerDataResolver.includes(`recommendationCaches.${cacheName}.clear()`),
      `Worker 数据代际变化时必须清空 ${cacheName} 缓存。`,
    );
  }
  assert.ok(
    workerDataResolver.includes('cachedDataSignature !== payload.dataSignature'),
    'Worker 候选缓存必须按完整推荐数据签名切换代际。',
  );
  assert.equal(recommendationSources.includes('preferYuyukoSafeEvaluation'), false,
    '已移除的跨阶段安全评价标记不得残留。');
  assert.ok(service.includes('specialPreferYuyukoPositiveSpell'),
    '二阶段必须向候选搜索传递独立的正面符卡排序标记。');
  assert.ok(sortProfile.includes('specialPreferYuyukoPositiveSpell?: boolean'),
    '排序上下文必须显式建模二阶段正面符卡候选。');

  const foodSelection = functionSlice(service, 'selectExecutionFoodCandidates', 'selectExecutionBeverageCandidates');
  const beverageSelection = functionSlice(service, 'selectExecutionBeverageCandidates', 'limitCandidatesByPinRank');
  assert.ok(foodSelection.includes('usesExpandedExecutionCandidateSearch(sortContext)'),
    '二阶段料理搜索必须进入扩容候选路径。');
  assert.ok(beverageSelection.includes('usesExpandedExecutionCandidateSearch(sortContext)'),
    '二阶段酒水搜索必须进入扩容候选路径。');
  const expandedSearch = functionSlice(service, 'usesExpandedExecutionCandidateSearch', 'getFoodCandidatePinRank');
  assert.ok(expandedSearch.includes('specialPreferYuyukoPositiveSpell === true'),
    '扩容候选判定必须包含二阶段正面符卡标记。');
  assert.ok(service.includes('getYuyukoPositiveSpellFoodCandidateRank'),
    '料理候选必须使用二阶段专用排序。');
  assert.ok(service.includes('getYuyukoPositiveSpellBeverageCandidateRank'),
    '酒水候选必须使用二阶段专用排序。');

  assert.ok(yuyukoChallenge.includes('export const YUYUKO_GOOD_LEVEL_SUM = 5'),
    '剧情版三阶段必须继续使用等级合计 5 的满意评价门槛。');
  const tagOrderEvaluation = functionSlice(
    yuyukoPositiveSpell,
    'evaluateYuyukoTagOrderPair',
    'evaluateYuyukoPositiveSpellPair',
  );
  assert.ok(tagOrderEvaluation.includes('food?.meetsRequiredFood')
    && tagOrderEvaluation.includes('beverage?.meetsRequiredBeverage')
    && tagOrderEvaluation.includes('food?.matchedPositiveTags')
    && tagOrderEvaluation.includes('beverage?.matchedTags')
    && tagOrderEvaluation.includes('getYuyukoPositiveSpellNegativeTags'),
  '重修版 SpecialOrder 评价必须统一读取完整上菜候选的点单、喜好和厌恶结果。');
  const rareOrderEvaluation = functionSlice(
    yuyukoChallenge,
    'evaluateYuyukoRareOrderPair',
    'getYuyukoLevelSum',
  );
  assert.ok(rareOrderEvaluation.includes("mode === 'story-level-sum'")
    && rareOrderEvaluation.includes('estimateYuyukoStoryLevelEvaluationScore(levelSum)'),
  '剧情版稀客订单必须保留等级合计评价。');
  assert.ok(rareOrderEvaluation.includes("mode === 'retake-tag-order'")
    && rareOrderEvaluation.includes('evaluateYuyukoTagOrderPair(food, beverage, demand)')
    && rareOrderEvaluation.includes('tagEvaluation.negativeTags.length === 0'),
  '重修版稀客订单必须改用完整 Tag 点单评价并显式拒绝厌恶。');
  const progressPlan = functionSlice(
    yuyukoChallenge,
    'isYuyukoProgressPlan',
    'scoreYuyukoPair',
  );
  assert.ok(progressPlan.includes('evaluateYuyukoRareOrderPair(mode, plan.food, plan.beverage, plan.demand).canProgress'),
    '稀客三阶段进度筛选必须显式传入评价模式。');
  assert.ok(service.includes('rule.yuyukoProgressEvaluationMode')
    && service.includes('compareYuyukoPlans(')
    && service.includes('isYuyukoProgressPlan(plan, rule.yuyukoProgressEvaluationMode)'),
  '稀客过滤与排序必须消费场景规则发布的同一评价模式。');

  assert.ok(yuyukoNormalTarget.includes('evaluateYuyukoNormalOrderPair('),
    '普客目标选择器必须统一消费按场景类型区分的评价结果。');
  assert.ok(yuyukoNormalTarget.includes('specialBusiness.challengeType'),
    '普客目标选择器必须把原始特殊经营类型传给评价入口。');
  const normalTargetSelection = functionSlice(
    yuyukoNormalTarget,
    'selectStrictOriginalOrderExecutionTarget',
    'resolveYuyukoNormalOrderModifierPreferences',
  );
  assert.ok(normalTargetSelection.includes('recipes: [originalRecipe]'),
    '三阶段搜索必须在生成候选前收窄到原点单料理。');
  assert.ok(normalTargetSelection.includes('beverages: [originalBeverage]'),
    '三阶段搜索必须在生成候选前收窄到原点单酒水。');
  assert.ok(normalTargetSelection.includes('{ ...context, maxExtraIngredients: 0 }'),
    '三阶段必须显式生成无加料原菜候选。');
  assert.ok(normalTargetSelection.includes('mergeYuyukoFoodCandidates('),
    '重修版必须把无加料原菜合并回通用 beam 结果。');
  assert.ok(normalTargetSelection.includes("executionMode: 'refresh'"),
    '原订单无满意评价修正时必须明确进入 Normal 清理模式。');
  assert.ok(normalTargetSelection.includes('expectedFoodModifierTags: evaluation.effectiveModifierTags'),
    '推进与清理目标必须携带同一评价器输出的预期 modifier Tag。');
  assert.equal(normalTargetSelection.includes('firstTag('), false,
    '精确订单候选不得再使用任意首个幽幽子喜好伪造 Tag 点单。');
  const refreshEvaluation = functionSlice(
    yuyukoNormalTarget,
    'isYuyukoRefreshEvaluationPair',
    'buildYuyukoNormalTargetReason',
  );
  assert.ok(refreshEvaluation.includes('evaluateYuyukoNormalOrderPair(')
    && refreshEvaluation.includes('modifierPreferences'),
    '清理模式必须消费同一份按场景区分的可执行评价。');
  assert.ok(refreshEvaluation.includes('evaluation.evaluationScore >= YUYUKO_REFRESH_EVALUATION_SCORE'),
    'Normal 评价必须达到清理模式的下限。');
  assert.ok(refreshEvaluation.includes('evaluation.evaluationScore < YUYUKO_GOOD_EVALUATION_SCORE'),
    'Good/ExGood 评价必须进入推进模式，不能误归为清理模式。');

  const exactOrderEvaluation = functionSlice(
    yuyukoChallenge,
    'evaluateYuyukoNormalOrderPair',
    'evaluateYuyukoRareOrderPair',
  );
  assert.ok(exactOrderEvaluation.includes('modifierPreferences.positiveTags')
    && exactOrderEvaluation.includes('modifierPreferences.negativeTags'),
    '重修评价必须显式读取运行时幽幽子料理喜恶。');
  assert.equal(exactOrderEvaluation.includes('food.matchedPositiveTags'), false,
    '重修评价不得隐式依赖候选搜索阶段的正向匹配摘要。');
  assert.equal(exactOrderEvaluation.includes('food.matchedNegativeTags'), false,
    '重修评价不得隐式依赖候选搜索阶段的负向匹配摘要。');
  const effectiveModifierTags = functionSlice(
    yuyukoChallenge,
    'getEffectiveYuyukoNormalOrderModifierTags',
    'intersectTags',
  );
  assert.ok(effectiveModifierTags.includes('new Set(food.recipe.positiveTags)')
    && effectiveModifierTags.includes("filter((tag) => !baseTags.has(tag))"),
  '重修 modifier 必须严格排除原配方基础 Tag，匹配原生 Tags.Except(RawTags)。');
  const searchCustomer = functionSlice(
    yuyukoNormalTarget,
    'buildYuyukoNormalOrderSearchCustomer',
    'mergeYuyukoFoodCandidates',
  );
  assert.ok(searchCustomer.includes('positiveTags:')
    && searchCustomer.includes('negativeTags:')
    && searchCustomer.match(/filter\(\(tag\) => !baseTags\.has\(tag\)\)/g)?.length === 2,
  '重修 beam 搜索的喜好和厌恶都必须排除原配方基础 Tag。');

  const retakeProgressValidation = sourceSlice(
    yuyukoRuntimePolicy,
    'private static RuntimeOrderEvaluationResult TryEvaluateRetakeYuyukoPhase3OrderIfReady(',
    'private static bool TryValidateYuyukoStoryPhase3ProgressEvaluation(',
  );
  assert.ok(retakeProgressValidation.includes('TryValidateYuyukoPhase3ServedExactTarget('),
    '重修版进度回调前必须校验实际送出的料理和酒水仍是精确目标。');
  assert.equal(retakeProgressValidation.includes('ReadSellableLevel('), false,
    '重修版进度校验不得恢复剧情版的等级合计判定。');
  const servedTargetDispatcher = sourceSlice(
    yuyukoRuntimePolicy,
    'private static bool TryValidateYuyukoPhase3ServedExactTarget(',
    'private static bool TryValidateYuyukoRetakeSpecialOrderServedContract(',
  );
  assert.match(
    servedTargetDispatcher,
    /RuntimeOrderTypeResolver\.Resolve\(runtimeOrder\.Order\)[\s\S]*!resolution\.Resolved \|\| resolution\.ReadableOrder == null[\s\S]*return false;[\s\S]*retakeContractMatched\s*=\s*orderKind == RuntimeOrderKind\.Special\s*\?\s*TryValidateYuyukoRetakeSpecialOrderServedContract\(\s*request,\s*servedFood,\s*servedBeverage,\s*out retakeContractDiagnostic\)\s*:\s*TryValidateYuyukoRetakeNormalOrderServedContract\(\s*request,\s*servedFood,\s*out retakeContractDiagnostic\)\s*;/,
    '重修版 dispatcher 必须先唯一解析具体订单类型，再严格分流 Special/Normal 契约。',
  );
  assert.match(
    servedTargetDispatcher,
    /return\s+foodMatched\s*&&\s*beverageMatched\s*&&\s*retakeContractMatched\s*;/,
    'Special/Normal 契约返回值必须参与最终上菜目标校验。',
  );

  const specialOrderServedContract = sourceSlice(
    yuyukoRuntimePolicy,
    'private static bool TryValidateYuyukoRetakeSpecialOrderServedContract(',
    'private static bool TryValidateYuyukoRetakeNormalOrderServedContract(',
  );
  assert.ok(specialOrderServedContract.includes('TryValidateServedFoodExtraIngredients(')
    && specialOrderServedContract.includes('request.FoodTagId')
    && specialOrderServedContract.includes('request.BeverageTagId')
    && specialOrderServedContract.match(/TryReadYuyukoSellableTagIds\(/g)?.length === 2,
  '重修版 SpecialOrder 必须校验加料 ID 及料理、酒水完整 Tags 中的原始点单 Tag。');
  assert.equal(specialOrderServedContract.includes('ExpectedFoodModifierTags'), false,
    'SpecialOrder 不得消费只属于 NormalOrder 的预期 modifier Tag。');
  assert.equal(specialOrderServedContract.includes('"RawTags"'), false,
    'SpecialOrder 不得使用 Tags.Except(RawTags) 的普通形态评价模型。');
  assert.equal(specialOrderServedContract.includes('ReadSellableLevel('), false,
    '重修版 SpecialOrder 契约不得退回剧情版等级模型。');
  assert.match(
    specialOrderServedContract,
    /return\s+foodTagMatched\s*&&\s*beverageTagMatched\s*;/,
    'SpecialOrder 的两个请求 Tag 匹配结果必须参与契约返回值。',
  );

  const normalOrderServedContract = sourceSlice(
    yuyukoRuntimePolicy,
    'private static bool TryValidateYuyukoRetakeNormalOrderServedContract(',
    'private static bool TryReadYuyukoSellableTagIds(',
  );
  assert.ok(normalOrderServedContract.includes('TryValidateServedFoodExtraIngredients(')
    && normalOrderServedContract.includes('request.ExpectedFoodModifierTags')
    && normalOrderServedContract.includes('TryReadYuyukoNormalOrderFoodModifierTags(')
    && normalOrderServedContract.includes('actualModifierTags.SequenceEqual(expectedModifierTags)'),
  'NormalOrder 必须继续严格校验实际加料与前端锁存的 modifier Tag。');
  const sharedExtraIngredientContract = sourceSlice(
    foodModifierValidation,
    'private static bool TryValidateServedFoodExtraIngredients(',
    '\n}',
  );
  assert.ok(sharedExtraIngredientContract.includes('"Modifier"')
    && sharedExtraIngredientContract.includes('expectedExtraIngredientIds')
    && sharedExtraIngredientContract.includes('RuntimeConcreteCollectionReader.TryReadIntArray(')
    && sharedExtraIngredientContract.includes('actual.SequenceEqual(expected)'),
  '两类重修订单都必须严格校验实际 Modifier 材料 ID。');
  assert.equal(
    yuyukoRuntimePolicy.includes('TryValidateYuyukoRetakeServedExtraIngredients('),
    false,
    '幽幽子不得保留已被共享严格读取器替代的私有兼容入口。',
  );
  const normalOrderModifierReader = sourceSlice(
    yuyukoRuntimePolicy,
    'private static bool TryReadYuyukoNormalOrderFoodModifierTags(',
    'private static bool TryValidateYuyukoStoryPhase3ServedProgressTarget(',
  );
  assert.ok(normalOrderModifierReader.includes('"Tags"')
    && normalOrderModifierReader.includes('"RawTags"')
    && normalOrderModifierReader.includes('.Except(baseTagIds)'),
  'NormalOrder 必须保留 Tags.Except(RawTags) 的原生 modifier 读取方式。');

  assert.ok(companionApi.includes('expectedFoodModifierTags: executionTarget ? executionTarget.expectedFoodModifierTags.join')
    && orderRecommendationWorker.includes("item.target?.expectedFoodModifierTags.join(',')"),
  '预期 modifier Tag 必须进入 API 请求和 Worker 结果签名。');
  const rareOrderApi = functionSlice(
    companionApi,
    'rareOrderAction',
    'buildRareOrderExecutionReason',
  );
  assert.equal(rareOrderApi.includes('expectedFoodModifierTags'), false,
    '稀客 SpecialOrder 请求不得发送只属于 NormalOrder 的 expectedFoodModifierTags。');
  assert.ok(orderPreparationModels.includes('ExpectedFoodModifierTags')
    && localApiServer.includes('ExpectedFoodModifierTags = ReadStringListQuery(query, "expectedFoodModifierTags")'),
  '本地 API 必须将预期 modifier Tag 解析为显式请求字段。');
  assert.ok(runtimeOrderPreparationService.includes('ExpectedFoodModifierTags = SpecialFoodTargetPolicy.NormalizeTags')
    && runtimeOrderDirectDelivery.includes('ExpectedFoodModifierTags = target.ExpectedFoodModifierTags'),
  '异步 cooking target 必须持久化并在送达后重建预期 modifier Tag。');

  assert.ok(automationState.includes('executionTarget: NormalOrderExecutionTarget | null')
    && automationState.includes('executionTargetBusinessGeneration: number')
    && automationState.includes('lockNormalOrderExecutionTarget(')
    && automationState.includes('clearNormalOrderExecutionTarget(')
    && automationState.includes('getCurrentNormalOrderExecutionTarget(')
    && automationState.includes('target.specialTargetSignature === specialTargetSignature')
    && automationState.includes('target.specialTargetRevision === specialTargetRevision'),
  '普客自动化状态必须显式锁存完整执行目标及其经营代际，并以目标签名和独立 revision 提供有效性判定和清理入口。');
  const normalTargetSelectionContract = sourceSlice(
    workbench,
    'function getNormalAutomationTargetSelection(',
    'function buildNormalOrderWorkerPayload(',
  );
  assert.ok(normalTargetSelectionContract.includes('if (!requiresSpecialTarget)')
    && normalTargetSelectionContract.indexOf('if (currentExecutionTarget)')
      < normalTargetSelectionContract.indexOf('if (!requiresRecipeTarget)')
    && normalTargetSelectionContract.includes('const missingTargetMessage = \'特殊经营料理执行目标未在执行前锁存，自动化已暂停该订单。\'')
    && normalTargetSelectionContract.includes('policyError: missingTargetMessage')
    && workbench.includes('const normalOrdersRequireSpecialExecutionTarget = (snapshot?.normalBusiness?.orders ?? []).some('),
  '普通订单不得进入特殊目标门禁；特殊经营已锁存目标必须优先透传，缺失时酒水与评价阶段也必须 fail-closed。');
  assert.ok(workbench.includes('getCurrentNormalOrderExecutionTarget(')
    && workbench.includes('target: applySpecialFoodTargetWirePolicy(currentExecutionTarget, targetPolicy)')
    && workbench.includes('currentState = lockNormalOrderExecutionTarget(')
    && workbench.indexOf('currentState = lockNormalOrderExecutionTarget(')
      < workbench.lastIndexOf('completeFirstNormalOrder(')
    && workbench.includes('clearNormalOrderExecutionTarget(currentState)')
    && workbench.includes('executionTargetBusinessGeneration: 0'),
  '执行目标必须在开锅副作用前按经营代际锁存，送达和评价阶段复用，明确重置时同时释放目标与代际。');
  assert.ok(workbench.includes('function retainNormalAutomationExecutionStates')
    && workbench.includes('!state.executionTarget && !state.cookingJobId')
    && workbench.includes('job.targetKind === \'normal\' && job.jobId === state.cookingJobId')
    && workbench.includes('!state?.manualResolutionRequired && !hasActiveCookingJob')
    && (workbench.match(/retainNormalAutomationExecutionStates\(normalOrderStatesRef\.current\)/g)?.length ?? 0) >= 3,
  '局部关闭普客自动化或处理阶段时必须保留已确认目标和活动 cooking job。');
  assert.ok(automationDomain.includes('requiresSpecialBusinessNormalExecutionTarget(')
    && automationDomain.includes('const specialTargetPolicy = buildSpecialFoodTargetWirePolicy(')
    && automationDomain.includes('const currentExecutionTarget = getCurrentNormalOrderExecutionTarget(')
    && automationDomain.includes('specialTargetPolicy.specialTargetSignature')
    && automationDomain.includes('specialTargetPolicy.specialTargetRevision')
    && automationDomain.includes('applySpecialFoodTargetWirePolicy(currentExecutionTarget, specialTargetPolicy)'),
  '厨具容量估算必须只为模块认领的订单复用同经营代际、同特殊目标签名和 revision 的锁存目标。');

  const storyTargetValidation = sourceSlice(
    yuyukoRuntimePolicy,
    'private static bool TryValidateYuyukoStoryPhase3ServedProgressTarget(',
    'private static void AppendYuyukoRuntimeDiagnostic(',
  );
  assert.ok(storyTargetValidation.includes('ReadSellableLevel('),
    '料理与酒水等级合计只用于剧情版三阶段上菜目标校验。');
  assert.ok(storyTargetValidation.includes('YuyukoStoryPhase3ProgressEvaluationMinLevelSum'),
    '剧情版三阶段必须继续校验满意评价所需的等级合计。');

  const diagnosticSignature = functionSlice(
    workbench,
    'buildAutomationDecisionDiagnosticSignature',
    'buildAutomationDecisionOrderLine',
  );
  assert.equal(diagnosticSignature.includes('snapshotSignature'), false,
    '快照内容更新不得让相同的自动化决策重复记录。');
}

function buildCookerSnapshot(typeIds) {
  const typeNames = new Map([
    [1, '煮锅'],
    [2, '烧烤架'],
    [3, '油锅'],
    [4, '蒸锅'],
    [5, '料理台'],
  ]);
  return {
    placedCookerTypeIds: typeIds,
    placedCookers: typeIds.map((typeId, controllerIndex) => ({
      controllerIndex,
      gridPosition: { x: controllerIndex, y: 0, z: 0 },
      controllerIdentity: `0x${(0x4000 + controllerIndex).toString(16).toUpperCase()}`,
      typeIds: [typeId],
      typeNames: [typeNames.get(typeId)],
      name: typeNames.get(typeId),
      challengeLocked: false,
      couldOpen: true,
      automationAvailable: true,
      automationAvailability: 'StrictIdle',
      automationAvailabilityDiagnostic: 'recommendation audit',
      source: 'test',
    })),
    placedCookerSnapshotComplete: true,
    placedCookerControllerCount: typeIds.length,
    placedCookerEmptyControllerCount: 0,
    placedCookerLockedControllerCount: 0,
    placedCookerReadFailureCount: 0,
    placedCookerStatus: 'test',
  };
}

function functionSlice(source, methodName, nextMethodName) {
  return sourceSlice(source, `function ${methodName}`, `function ${nextMethodName}`);
}

function sourceSlice(source, startToken, endToken) {
  const start = source.indexOf(startToken);
  const end = source.indexOf(endToken, start + 1);
  assert.ok(start >= 0, `Source token not found: ${startToken}`);
  assert.ok(end > start, `Source boundary not found: ${startToken} -> ${endToken}`);
  return source.slice(start, end);
}
