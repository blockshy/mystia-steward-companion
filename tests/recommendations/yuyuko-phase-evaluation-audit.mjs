import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'vite';

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true },
  appType: 'custom',
});
let yuyukoModule;
try {
  yuyukoModule = await vite.ssrLoadModule(
    '/src/companion/domain/special-business/yuyuko-positive-spell.ts',
  );
} finally {
  await vite.close();
}
const { evaluateYuyukoPositiveSpellPair } = yuyukoModule;

const root = new URL('../../', import.meta.url);
const demand = {
  type: 'rare-tag-order',
  requiredFoodTag: '下酒',
  requiredBeverageTag: '直饮',
};

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

await assertSourceContracts();

console.log('PASS: Yuyuko phase-two positive-spell evaluation is isolated from phase-three rules.');

function buildFood({
  level = 3,
  activeTags = ['下酒'],
  matchedPositiveTags = [],
  matchedNegativeTags = [],
} = {}) {
  return {
    recipe: {
      id: 1,
      recipeId: 1,
      name: '测试料理',
      level,
      price: 100,
    },
    extraIngredients: [],
    extraIngredientReasonTags: {},
    activeTags,
    suppressedTags: [],
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

function buildBeverage({
  level = 3,
  activeTags = ['直饮'],
  matchedTags = [],
} = {}) {
  return {
    beverage: {
      id: 1,
      name: '测试酒水',
      level,
      price: 100,
    },
    activeTags,
    matchedTags,
    meetsRequiredBeverage: activeTags.includes(demand.requiredBeverageTag),
    ownedQuantity: 10,
    conditionResults: [],
  };
}

async function assertSourceContracts() {
  const [service, sortProfile, ruleTypes, yuyukoRule, yuyukoChallenge, workbench] = await Promise.all([
    readFile(new URL('apps/companion/src/companion/domain/service-recommendations.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/recommendation-engine/sort-profile.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/rules/types.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/rules/yuyuko.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/yuyuko-challenge.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
  ]);

  const recommendationSources = `${service}\n${sortProfile}\n${ruleTypes}\n${yuyukoRule}`;
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

  assert.ok(yuyukoChallenge.includes("YUYUKO_CHALLENGE_FOOD_HATE_TAGS = ['素', '小巧', '清淡']"),
    '三阶段必须继续拦截幽幽子全局厌恶 Tag“小巧”。');
  assert.ok(yuyukoChallenge.includes('export const YUYUKO_GOOD_LEVEL_SUM = 5'),
    '三阶段必须继续使用等级合计 5 的满意评价门槛。');
  const phaseThreeEvaluation = functionSlice(
    yuyukoChallenge,
    'isYuyukoProgressEvaluationPair',
    'isYuyukoProgressPlan',
  );
  assert.ok(phaseThreeEvaluation.includes('getYuyukoChallengeNegativeTags(food).length > 0'),
    '三阶段进度判定不得绕过幽幽子全局厌恶 Tag。');
  assert.ok(phaseThreeEvaluation.includes('>= YUYUKO_GOOD_EVALUATION_SCORE'),
    '三阶段进度判定不得绕过满意评价门槛。');

  const diagnosticSignature = functionSlice(
    workbench,
    'buildAutomationDecisionDiagnosticSignature',
    'buildAutomationDecisionOrderLine',
  );
  assert.equal(diagnosticSignature.includes('snapshotSignature'), false,
    '快照内容更新不得让相同的自动化决策重复记录。');
}

function functionSlice(source, methodName, nextMethodName) {
  const start = source.indexOf(`function ${methodName}`);
  const end = source.indexOf(`function ${nextMethodName}`, start + 1);
  assert.ok(start >= 0, `Method not found: ${methodName}`);
  assert.ok(end > start, `Method boundary not found: ${methodName} -> ${nextMethodName}`);
  return source.slice(start, end);
}
