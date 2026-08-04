import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'vite';

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true },
  appType: 'custom',
  logLevel: 'silent',
});
let presentationModule;
let registryModule;
try {
  [presentationModule, registryModule] = await Promise.all([
    vite.ssrLoadModule('/src/companion/domain/order-recommendation-presentation.ts'),
    vite.ssrLoadModule('/src/companion/domain/special-business/registry.ts'),
  ]);
} finally {
  await vite.close();
}

const {
  buildOrderDemandIdentity,
  buildOrderRecommendationPresentation,
} = presentationModule;
const {
  buildSpecialBusinessRecommendationSignature,
  buildSpecialFoodTargetWirePolicy,
} = registryModule;
const root = new URL('../../', import.meta.url);

const baseOrder = buildOrder({
  traceId: 'R-0001',
  deskCode: 1,
  runtimeGuestId: 100,
  foodTagId: 10,
  beverageTagId: 20,
  lastSeenAtUtc: '2026-07-30T00:00:00Z',
});
const retainedRecommendation = buildRecommendation(baseOrder);
const currentOrder = {
  ...baseOrder,
  lastSeenAtUtc: '2026-07-30T00:00:10Z',
  source: 'new-observation-source',
  specialBusinessRoleLabel: '最新显示标签',
};
const newOrder = buildOrder({
  traceId: 'R-0002',
  deskCode: 2,
  runtimeGuestId: 101,
  foodTagId: 11,
  beverageTagId: 21,
});
const presentation = buildOrderRecommendationPresentation({
  orders: [currentOrder, newOrder],
  recommendations: [retainedRecommendation],
  recommendationIssues: [],
  pending: true,
  isCurrent: false,
  resultContextSignature: 'same-context',
  currentContextSignature: 'same-context',
});
assert.equal(presentation.recommendations.length, 1);
assert.equal(presentation.recommendations[0].order, currentOrder,
  '观测时间、来源和显示标签变化应复用推荐内容并投影最新订单对象。');
assert.deepEqual(presentation.pendingOrders.map((order) => order.traceId), ['R-0002'],
  '新订单应只有自己的局部计算状态。');
assert.equal(presentation.updating, true);
assert.equal(
  buildOrderDemandIdentity(baseOrder),
  buildOrderDemandIdentity(currentOrder),
  '观测字段不得进入展示语义身份。',
);

const servedOrder = { ...currentOrder, hasServedFood: true };
const servedPresentation = buildOrderRecommendationPresentation({
  orders: [servedOrder, newOrder],
  recommendations: [retainedRecommendation],
  recommendationIssues: [],
  pending: true,
  isCurrent: false,
  resultContextSignature: 'same-context',
  currentContextSignature: 'same-context',
});
assert.equal(servedPresentation.recommendations.length, 0);
assert.deepEqual(
  servedPresentation.pendingOrders.map((order) => order.traceId),
  ['R-0001', 'R-0002'],
  '送达状态变化必须使对应订单旧结果失效，而不是继续展示已送达组件。',
);

const switchedContext = buildOrderRecommendationPresentation({
  orders: [currentOrder],
  recommendations: [retainedRecommendation],
  recommendationIssues: [],
  pending: true,
  isCurrent: false,
  resultContextSignature: 'old-connection',
  currentContextSignature: 'new-connection',
});
assert.equal(switchedContext.recommendations.length, 0);
assert.deepEqual(switchedContext.pendingOrders, [currentOrder],
  '连接、会话或经营硬上下文变化后不得复用旧结果。');

const synchronousContextSwitch = buildOrderRecommendationPresentation({
  orders: [currentOrder],
  recommendations: [retainedRecommendation],
  recommendationIssues: [],
  pending: false,
  isCurrent: true,
  resultContextSignature: 'old-connection',
  currentContextSignature: 'new-connection',
});
assert.equal(synchronousContextSwitch.recommendations.length, 0);
assert.deepEqual(synchronousContextSwitch.pendingOrders, [currentOrder]);
assert.equal(synchronousContextSwitch.updating, true,
  '硬上下文必须在 Hook pending 状态更新前同步清除旧展示。');

const initialPresentation = buildOrderRecommendationPresentation({
  orders: [currentOrder],
  recommendations: [],
  recommendationIssues: [],
  pending: false,
  isCurrent: true,
  resultContextSignature: '',
  currentContextSignature: 'first-context',
});
assert.deepEqual(initialPresentation.pendingOrders, [currentOrder]);
assert.equal(initialPresentation.updating, true,
  '首次冷启动没有 Worker 结果时应逐单显示计算状态。');

const emptySwitchedContext = buildOrderRecommendationPresentation({
  orders: [],
  recommendations: [retainedRecommendation],
  recommendationIssues: [],
  pending: false,
  isCurrent: true,
  resultContextSignature: 'old-connection',
  currentContextSignature: 'new-connection',
});
assert.deepEqual(emptySwitchedContext, {
  recommendations: [],
  recommendationIssues: [],
  pendingOrders: [],
  updating: false,
  updateError: null,
}, '硬上下文变化且已无订单时应立即清空，不显示计算状态。');

const missingTagIdOrder = buildOrder({
  traceId: 'R-MISSING-TAG',
  foodTagId: null,
  foodTag: '旧料理要求',
  beverageTagId: null,
  beverageTag: '旧酒水要求',
});
const changedMissingTagText = {
  ...missingTagIdOrder,
  foodTag: '新料理要求',
};
const missingTagPresentation = buildOrderRecommendationPresentation({
  orders: [changedMissingTagText],
  recommendations: [buildRecommendation(missingTagIdOrder)],
  recommendationIssues: [],
  pending: true,
  isCurrent: false,
  resultContextSignature: 'same-context',
  currentContextSignature: 'same-context',
});
assert.equal(missingTagPresentation.recommendations.length, 0);
assert.deepEqual(missingTagPresentation.pendingOrders, [changedMissingTagText],
  '原始 Tag ID 缺失时，精确需求文本变化必须使旧结果失效。');

const changedInformationalTagText = {
  ...baseOrder,
  foodTag: '仅显示文本变化',
  beverageTag: '仅显示酒水文本变化',
};
assert.equal(
  buildOrderDemandIdentity(baseOrder),
  buildOrderDemandIdentity(changedInformationalTagText),
  '原始 Tag ID 存在时，显示文本不得重复参与需求身份。',
);

const retainedAfterError = buildOrderRecommendationPresentation({
  orders: [currentOrder],
  recommendations: [retainedRecommendation],
  recommendationIssues: [],
  pending: false,
  isCurrent: false,
  resultContextSignature: 'same-context',
  currentContextSignature: 'same-context',
  error: 'Worker deterministic failure',
  retainedAfterError: true,
});
assert.equal(retainedAfterError.recommendations.length, 1);
assert.equal(retainedAfterError.updating, false);
assert.equal(retainedAfterError.updateError, 'Worker deterministic failure',
  '同一硬上下文计算失败时可保留旧结果，但必须明确显示失败。');

const failedAfterOrderReplacement = buildOrderRecommendationPresentation({
  orders: [newOrder],
  recommendations: [retainedRecommendation],
  recommendationIssues: [],
  pending: false,
  isCurrent: false,
  resultContextSignature: 'same-context',
  currentContextSignature: 'same-context',
  error: 'Worker deterministic failure',
  retainedAfterError: true,
});
assert.equal(failedAfterOrderReplacement.recommendations.length, 0);
assert.equal(failedAfterOrderReplacement.updateError, 'Worker deterministic failure',
  '同上下文 A 订单替换为 B 后失败时，即使没有可复用行也不得误显示暂无推荐。');

const failedAcrossContext = buildOrderRecommendationPresentation({
  orders: [currentOrder],
  recommendations: [retainedRecommendation],
  recommendationIssues: [],
  pending: false,
  isCurrent: false,
  resultContextSignature: 'old-context',
  currentContextSignature: 'new-context',
  error: 'Worker deterministic failure',
  retainedAfterError: true,
});
assert.equal(failedAcrossContext.recommendations.length, 0);
assert.equal(failedAcrossContext.updateError, null,
  '跨硬上下文失败不得把旧结果伪装为新上下文的上次结果。');

const coldStartFailure = buildOrderRecommendationPresentation({
  orders: [currentOrder],
  recommendations: [],
  recommendationIssues: [{
    order: currentOrder,
    message: 'Worker cold-start failure',
  }],
  pending: false,
  isCurrent: true,
  resultContextSignature: 'same-context',
  currentContextSignature: 'same-context',
  error: 'Worker cold-start failure',
  retainedAfterError: false,
});
assert.equal(coldStartFailure.recommendationIssues[0]?.message, 'Worker cold-start failure');
assert.equal(coldStartFailure.updateError, null,
  '冷启动失败应显示逐单失败 issue，而不是声称正在展示上次结果。');

const bloodPond = buildSpecialBusiness({
  challengeType: 'Story_BloodPondHell',
  phase: 'Phase 1',
  foodTargetTags: ['目标甲', '目标乙'],
  currentValue: 970,
  maxValue: 1000,
  targetValue: 940,
});
assert.equal(
  buildSpecialBusinessRecommendationSignature(bloodPond),
  buildSpecialBusinessRecommendationSignature({
    ...bloodPond,
    phase: 'Phase 2',
    currentValue: 200,
    maxValue: 500,
    targetValue: 120,
    currentAnger: 99,
  }),
  '血池地狱阶段文本、生命值和怒气不参与当前推荐语义。',
);
assert.notEqual(
  buildSpecialBusinessRecommendationSignature(bloodPond),
  buildSpecialBusinessRecommendationSignature({
    ...bloodPond,
    foodTargetTags: ['目标甲', '目标丙'],
  }),
  '血池地狱双 Tag 变化必须触发重新计算。',
);
assert.notEqual(
  buildSpecialBusinessRecommendationSignature(bloodPond),
  buildSpecialBusinessRecommendationSignature({
    ...bloodPond,
    yuumaFoodTargetRevision: bloodPond.yuumaFoodTargetRevision + 2,
  }),
  '血池地狱 A -> B -> A 即使 Tag 恢复，也必须由运行时 revision 触发重新计算。',
);

const yuumaPolicy = buildSpecialFoodTargetWirePolicy(
  bloodPond,
  'yuuma-boss-order',
  7,
);
assert.deepEqual(yuumaPolicy, {
  specialTargetChallenge: 'Story_BloodPondHell',
  specialTargetOwner: 'yuuma',
  specialTargetGeneration: 7,
  specialTargetRevision: bloodPond.yuumaFoodTargetRevision,
  specialTargetFoodTags: ['目标乙', '目标甲'],
  specialTargetMatchMode: 'all',
  specialTargetSignature: 'Story_BloodPondHell|yuuma|generation:7|match:all|food:目标乙,目标甲',
});
const wackyPolicy = buildSpecialFoodTargetWirePolicy(
  buildSpecialBusiness({
    challengeType: 'Story_WackyCookingCompetition',
    foodTargetTags: ['清淡', '肉'],
  }),
  'wacky-target-order',
  8,
);
assert.equal(wackyPolicy.specialTargetOwner, 'koishi');
assert.equal(wackyPolicy.specialTargetRevision, 0);
assert.equal(wackyPolicy.specialTargetMatchMode, 'any');
assert.equal(
  wackyPolicy.specialTargetSignature,
  'Story_WackyCookingCompetition|koishi|generation:8|match:any|food:清淡,肉',
);
assert.deepEqual(
  buildSpecialFoodTargetWirePolicy(
    buildSpecialBusiness({
      challengeType: 'Story_WackyCookingCompetition',
      phase: 'Phase 3',
      foodTargetTags: ['清淡', '肉'],
    }),
    'wacky-koishi-boss',
    8,
  ),
  {
    specialTargetChallenge: '',
    specialTargetOwner: '',
    specialTargetGeneration: 0,
    specialTargetRevision: 0,
    specialTargetFoodTags: [],
    specialTargetMatchMode: '',
    specialTargetSignature: '',
  },
  '怪诞料理三阶段古明地恋规则没有动态料理目标，不得从全局 Tag 伪造策略。',
);
assert.equal(
  buildSpecialFoodTargetWirePolicy(bloodPond, 'yuuma-boss-order', 0).specialTargetSignature,
  '',
  '缺少有效经营 generation 时必须 fail closed。',
);
assert.equal(
  buildSpecialFoodTargetWirePolicy(
    { ...bloodPond, yuumaFoodTargetRevision: 0 },
    'yuuma-boss-order',
    7,
  ).specialTargetSignature,
  '',
  '血池地狱缺少正 revision 时必须 fail closed，不能回退到仅 Tag 身份。',
);
const returnedYuumaPolicy = buildSpecialFoodTargetWirePolicy(
  { ...bloodPond, yuumaFoodTargetRevision: bloodPond.yuumaFoodTargetRevision + 2 },
  'yuuma-boss-order',
  7,
);
assert.equal(returnedYuumaPolicy.specialTargetSignature, yuumaPolicy.specialTargetSignature,
  '运行时 revision 不得拼入游戏规范 target signature。');
assert.notEqual(returnedYuumaPolicy.specialTargetRevision, yuumaPolicy.specialTargetRevision);

await assertSourceContracts();
console.log('PASS: order recommendation presentation stays stable per order while action paths remain current-only.');

function buildOrder(overrides = {}) {
  return {
    traceId: 'R-BASE',
    deskCode: 1,
    guestId: 100,
    runtimeGuestId: 100,
    guestName: '测试稀客',
    specialBusinessRole: '',
    specialBusinessRoleLabel: '',
    automationAllowed: true,
    automationBlockReason: '',
    foodTagId: 10,
    foodTag: '料理要求',
    beverageTagId: 20,
    beverageTag: '酒水要求',
    source: 'old-observation-source',
    firstSeenAtUtc: '2026-07-30T00:00:00Z',
    lastSeenAtUtc: '2026-07-30T00:00:00Z',
    isFreeOrder: false,
    hasServedFood: false,
    hasServedBeverage: false,
    ...overrides,
  };
}

function buildRecommendation(order) {
  return {
    order,
    customer: { id: order.guestId, name: order.guestName },
    executionPlans: [],
    budget: null,
    blockedMessages: [],
    blockedDiagnostic: null,
    recipes: [],
    beverages: [],
  };
}

function buildSpecialBusiness(overrides = {}) {
  return {
    active: true,
    challengeTypeAvailable: true,
    challengeType: 'Story_BloodPondHell',
    displayName: '测试特殊经营',
    category: 'challenge',
    ruleSummary: '',
    foodTargetTags: [],
    beverageTargetTags: [],
    yuumaFoodTargetRevision: 23,
    phase: '',
    currentValue: null,
    maxValue: null,
    targetValue: null,
    currentAnger: null,
    maxAnger: null,
    targetAnger: null,
    recommendationPolicy: '',
    automationPolicy: '',
    source: 'test',
    error: null,
    ...overrides,
  };
}

async function assertSourceContracts() {
  const [workbench, hook, api, panel, servicePresentation, automation, worker] = await Promise.all([
    readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/hooks/useOrderRecommendations.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/api.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/pages/ModServicePanel.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/pages/service/ServiceOrderPresentation.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/automation.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/workers/order-recommendations.worker.ts', root), 'utf8'),
  ]);
  const orderSignature = workbench.slice(
    workbench.indexOf('function buildNightBusinessOrderSignature'),
    workbench.indexOf('function buildFavoriteDataSignature'),
  );
  assert.equal(orderSignature.includes('lastSeenAtUtc'), false);
  assert.equal(orderSignature.includes('order.source'), false);
  assert.equal(orderSignature.includes('specialBusinessRoleLabel'), false);
  assert.match(orderSignature, /automationAllowed/);
  assert.match(orderSignature, /automationBlockReason/);
  assert.match(workbench, /buildOrderRecommendationPresentation\(/);
  assert.equal(workbench.includes('visibleOrderRecommendations = orderRecommendationsPending'), false);
  assert.match(hook, /resultContextSignature/);
  assert.match(hook, /retainedAfterError/);
  assert.match(hook, /retainedAfterError: Boolean\(queueError\)/,
    '最新排队请求投递失败时，已成功返回的上一轮结果必须显式标记为失败后保留。');
  assert.match(hook, /lastResultSignatureRef\.current = ''/);
  assert.match(servicePresentation, /data-recommendation-pending-order=\{pending \? 'true' : undefined\}/);
  assert.match(servicePresentation, /更新失败，当前为上次结果/);
  assert.match(panel, /推荐更新失败/);
  assert.match(panel, /mode="normal"/);
  assert.match(panel, /mode=\{fillAvailableHeight \? 'rare-focus' : 'rare'\}/);
  for (const field of [
    'specialTargetChallenge',
    'specialTargetOwner',
    'specialTargetGeneration',
    'specialTargetRevision',
    'specialTargetFoodTags',
    'specialTargetMatchMode',
    'specialTargetSignature',
  ]) {
    assert.match(api, new RegExp(`${field}:`), `本地 API 缺少 ${field}。`);
    assert.match(
      api,
      new RegExp(`specialTargetPolicy\\.${field}`),
      `${field} 必须来自独立策略，而不是可空料理动作目标。`,
    );
  }
  assert.match(worker, /item\.target\?\.specialTargetRevision \?\? ''/,
    'Worker 结果身份必须携带独立 revision，避免 A -> B -> A 复用旧结果。');
  assert.equal(
    (api.match(/specialTargetRevision: String\(specialTargetPolicy\.specialTargetRevision\)/g) ?? []).length,
    2,
    '普客和稀客 API 都必须使用规范参数 specialTargetRevision。',
  );
  assert.equal(api.includes('specialTargetChallenge: recipeTarget?.specialTargetChallenge'), false);
  assert.equal(api.includes('specialTargetChallenge: executionTarget?.specialTargetChallenge'), false);
  assert.match(
    workbench,
    /prepareNextRareOrder\([\s\S]{0,260}specialTargetPolicy,\s*currentState\.recipeTarget,\s*currentState\.beverageTarget,\s*preparePreferences,\s*shouldPrepareFood \? cookerReservation : null/,
    '稀客分阶段请求必须传递当前订单锁定的完整料理和酒水目标，只有厨具预约可随料理动作关闭。',
  );
  assert.equal(
    workbench.includes('shouldPrepareFood ? currentState.recipeTarget : null'),
    false,
    '料理动作关闭时不得裁剪锁定料理目标。',
  );
  assert.equal(
    workbench.includes('shouldPrepareBeverage ? currentState.beverageTarget : null'),
    false,
    '酒水动作关闭时不得裁剪锁定酒水目标。',
  );
  assert.match(
    workbench,
    /completeFirstRareOrder\([\s\S]{0,220}specialTargetPolicy,[\s\S]{0,160}(currentState|finalState)\.recipeTarget/,
    '稀客完成请求必须独立传策略。',
  );
  assert.match(
    workbench,
    /completeFirstNormalOrder\([\s\S]{0,220}specialTargetSelection\.specialTargetPolicy,[\s\S]{0,120}requestPreferences,[\s\S]{0,220}requestPreferences\.autoNormalStartCooking[\s\S]{0,160}recommendationData,[\s\S]{0,120}specialTargetSelection\.target/,
    '普客完成请求必须把策略、精确厨具预约与可空料理执行目标分开。',
  );
  assert.equal(
    workbench.includes('normalAutomationTargetsEnabled && (normalAutomationTargets.pending || !normalAutomationTargets.isCurrent)'),
    false,
    '特殊目标 Worker pending 不得全局阻断只送酒或只完成阶段。',
  );
  assert.match(workbench, /const requiresRecipeTarget = shouldStartCooking[\s\S]{0,180}shouldDeliverFood/);
  assert.match(automation, /if \(!requiresRecipeTarget\)/);
  assert.equal(automation.includes('buildWackyFoodTargetSignature'), false);
  assert.match(automation, /reconcileRareRecipeTargetForSpecialBusiness/);
}
