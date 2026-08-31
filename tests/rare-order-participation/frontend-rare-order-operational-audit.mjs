import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

import { createServer } from 'vite';

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true },
  appType: 'custom',
  logLevel: 'error',
});

let sorting;
let automation;
let gameUiTargets;
try {
  [sorting, automation, gameUiTargets] = await Promise.all([
    vite.ssrLoadModule('/src/companion/domain/sorting.ts'),
    vite.ssrLoadModule('/src/companion/domain/automation.ts'),
    vite.ssrLoadModule('/src/companion/domain/game-ui-targets.ts'),
  ]);
} finally {
  await vite.close();
}

const { sortOperationalNightOrderRows } = sorting;
const { selectOperationalOrderPreparationCandidates } = automation;
const { buildRareGameUiTarget, buildRareGameUiTargetFromParticipationQueue } = gameUiTargets;

const specialBusiness = buildSpecialBusiness();
const possessedLate = order({
  traceId: 'R-100',
  lifecycle: 100,
  role: 'mizuchi-trial-possessed-order',
  seenAt: '2026-08-31T00:00:10.000Z',
});
const possessedQueueTieOlder = order({
  traceId: 'R-20',
  lifecycle: 20,
  role: 'mizuchi-trial-possessed-order',
  seenAt: '2026-08-31T00:00:02.000Z',
});
const possessedQueueTieNewer = order({
  traceId: 'R-21',
  lifecycle: 21,
  role: 'mizuchi-trial-possessed-order',
  seenAt: '2026-08-31T00:00:03.000Z',
});
const possessedQueueFive = order({
  traceId: 'R-50',
  lifecycle: 50,
  role: 'mizuchi-trial-possessed-order',
  seenAt: '2026-08-31T00:00:01.000Z',
});
const ordinaryQueueOne = order({
  traceId: 'R-1',
  lifecycle: 1,
  role: 'mizuchi-trial-ordinary-order',
  seenAt: '2026-08-31T00:00:00.000Z',
});
const pausedPossessed = order({
  traceId: 'R-9',
  lifecycle: 9,
  role: 'mizuchi-trial-possessed-order',
  seenAt: '2026-08-30T00:00:00.000Z',
});
const invalidQueuePossessed = order({
  traceId: 'R-8',
  lifecycle: 8,
  role: 'mizuchi-trial-possessed-order',
  seenAt: '2026-08-29T00:00:00.000Z',
});

const operationalRows = [
  row(ordinaryQueueOne, true, 1),
  row(possessedLate, true, 100),
  row(possessedQueueFive, true, 5),
  row(possessedQueueTieNewer, true, 2),
  row(possessedQueueTieOlder, true, 2),
  row(pausedPossessed, false, null),
  row(invalidQueuePossessed, true, null),
];
const sorted = sortOperationalNightOrderRows(operationalRows, 'ordered', specialBusiness);
assert.deepEqual(
  sorted.map(({ order: candidate }) => candidate.traceId),
  ['R-20', 'R-21', 'R-50', 'R-100', 'R-1'],
  '顺序必须严格为特殊经营硬 lane -> queuePosition -> 原有稳定顺序。',
);
assert.deepEqual(
  sortOperationalNightOrderRows(operationalRows, 'ordered', null)
    .map(({ order: candidate }) => candidate.traceId),
  ['R-1', 'R-20', 'R-21', 'R-50', 'R-100'],
  '没有活动特殊经营时，队列序号必须成为第一跨订单顺序。',
);
assert.ok(!sorted.some(({ order: candidate }) => candidate === pausedPossessed));
assert.ok(!sorted.some(({ order: candidate }) => candidate === invalidQueuePossessed));

const automationRows = operationalRows.map(({ order: candidate, participation }) => ({
  recommendation: recommendation(candidate),
  participation,
}));
const automationResult = selectOperationalOrderPreparationCandidates(
  automationRows,
  { version: 1, recipes: [], beverages: [] },
  {
    serviceOrderSortMode: 'ordered',
    autoPrepStartCooking: false,
    autoPrepTakeBeverage: false,
    autoPrepRecipeFavoritesOnly: false,
    autoPrepBeverageFavoritesOnly: false,
  },
  undefined,
  specialBusiness,
);
assert.deepEqual(
  automationResult.selections.map((selection) => selection.item.order.traceId),
  ['R-20', 'R-21', 'R-50', 'R-100', 'R-1'],
  '自动化候选入口必须复用 operational 队列顺序。',
);
assert.deepEqual(automationResult.skips, [], '暂停行应在自动化身份/计划检查前被排除。');

const indexes = {
  ingredientByName: new Map([['基础材料', { id: 11 }]]),
};
const features = {
  listPinningEnabled: true,
  recipeVariantEnabled: true,
  cookerHighlightEnabled: true,
  seatHighlightEnabled: true,
  orderHighlightEnabled: true,
};
const queueHead = recommendation(order({ traceId: 'R-2', lifecycle: 2 }), plan(202));
const laterMission = recommendation(
  order({
    traceId: 'R-3',
    lifecycle: 3,
    mission: { foodId: 203, recipeId: 1203 },
  }),
  plan(203),
);
const pausedMission = recommendation(
  order({
    traceId: 'R-4',
    lifecycle: 4,
    mission: { foodId: 204, recipeId: 1204 },
  }),
  plan(204),
);
const queueTarget = buildRareGameUiTargetFromParticipationQueue(
  [
    operationalRecommendation(pausedMission, false, null),
    operationalRecommendation(laterMission, true, 3),
    operationalRecommendation(queueHead, true, 2),
  ],
  'ordered',
  '#FFDB2E',
  features,
  indexes,
);
assert.equal(queueTarget?.sourceOrderKey, 'R-2|lifecycle:2');
assert.equal(
  queueTarget?.recipeId,
  1202,
  '跨订单任务料理不得把后入队订单拉到现有队首之前。',
);

const ordinaryTargetRecommendation = recommendation(order({
  traceId: 'R-5',
  lifecycle: 5,
  role: 'mizuchi-trial-ordinary-order',
}), plan(205));
const possessedTargetRecommendation = recommendation(order({
  traceId: 'R-6',
  lifecycle: 6,
  role: 'mizuchi-trial-possessed-order',
}), plan(206));
const specialTarget = buildRareGameUiTargetFromParticipationQueue(
  [
    operationalRecommendation(ordinaryTargetRecommendation, true, 1),
    operationalRecommendation(possessedTargetRecommendation, true, 99),
  ],
  'ordered',
  '#FFDB2E',
  features,
  indexes,
  { specialBusiness },
);
assert.equal(
  specialTarget?.sourceOrderKey,
  'R-6|lifecycle:6',
  '特殊经营已验证的硬 lane 必须保持在普通 queuePosition 之前。',
);

assert.equal(buildRareGameUiTargetFromParticipationQueue(
  [operationalRecommendation(queueHead, false, null)],
  'ordered',
  '#FFDB2E',
  features,
  indexes,
), null, '只有暂停订单时不得发布游戏 UI target。');
assert.equal(buildRareGameUiTargetFromParticipationQueue(
  [operationalRecommendation(queueHead, true, null)],
  'ordered',
  '#FFDB2E',
  features,
  indexes,
), null, '缺失 Mod queuePosition 时不得由前端猜测 target 顺序。');

const legacyMissingGuestRecommendation = recommendation({
  ...queueHead.order,
  guestId: null,
}, plan(202));
const legacyMissingGuestTarget = buildRareGameUiTarget(
  [legacyMissingGuestRecommendation],
  'ordered',
  '#FFDB2E',
  features,
  indexes,
);
assert.equal(
  legacyMissingGuestTarget?.guestId,
  -1,
  '空名单 legacy selector 必须保留旧的弱 guestId 快照，并显式发送 unknown sentinel。',
);
assert.equal(
  buildRareGameUiTargetFromParticipationQueue(
    [operationalRecommendation(legacyMissingGuestRecommendation, true, 2)],
    'ordered',
    '#FFDB2E',
    features,
    indexes,
  ),
  null,
  '非空参与队列不得接纳 canonical guestId 缺失的 UI target。',
);

const root = new URL('../../', import.meta.url);
const [sortingSource, automationSource, targetSource] = await Promise.all([
  readFile(new URL('apps/companion/src/companion/domain/sorting.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/domain/automation.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/domain/game-ui-targets.ts', root), 'utf8'),
]);
assert.ok(sortingSource.includes('compareSpecialBusinessLane'));
assert.ok(sortingSource.indexOf('compareSpecialBusinessLane') < sortingSource.indexOf('queueDifference'));
assert.ok(automationSource.includes('selectOperationalOrderPreparationCandidates'));
assert.ok(targetSource.includes('buildRareGameUiTargetFromParticipationQueue'));
assert.ok(targetSource.includes('跨订单任务料理不会跳过新队列序号'));

console.log(
  'PASS: rare operational consumers filter paused/invalid rows and order hard special-business lanes before queue position and stable presentation order.',
);

function row(candidate, operationallyParticipating, queuePosition) {
  return {
    order: candidate,
    participation: { operationallyParticipating, queuePosition },
  };
}

function operationalRecommendation(candidate, operationallyParticipating, queuePosition) {
  return {
    recommendation: candidate,
    participation: { operationallyParticipating, queuePosition },
  };
}

function order({
  traceId,
  lifecycle,
  role = '',
  seenAt = '2026-08-31T00:00:00.000Z',
  mission = null,
}) {
  const foodTagId = 1;
  const beverageTagId = 2;
  const guestId = lifecycle + 100;
  return {
    traceId,
    orderLifecycleSequence: lifecycle,
    deskCode: lifecycle,
    guestId,
    runtimeGuestId: guestId,
    guestName: `稀客 ${lifecycle}`,
    specialBusinessRole: role,
    specialBusinessRoleLabel: '',
    automationAllowed: true,
    automationBlockReason: '',
    foodTagId,
    foodTag: '家常',
    beverageTagId,
    beverageTag: '可加热',
    source: 'operational-audit',
    firstSeenAtUtc: seenAt,
    lastSeenAtUtc: seenAt,
    hasServedFood: false,
    hasServedBeverage: false,
    missionRecipePriority: mission
      ? {
        traceId,
        deskCode: lifecycle,
        guestId,
        runtimeGuestId: guestId,
        foodId: mission.foodId,
        recipeId: mission.recipeId,
        missionGeneration: 1,
        businessGeneration: 7,
      }
      : null,
  };
}

function recommendation(sourceOrder, executionPlan = null) {
  return {
    order: sourceOrder,
    customer: { id: sourceOrder.guestId, name: sourceOrder.guestName },
    executionPlans: executionPlan ? [executionPlan] : [],
    recipes: [],
    beverages: [],
  };
}

function plan(foodId) {
  return {
    bucket: 'complete',
    food: {
      recipe: {
        id: foodId,
        recipeId: foodId + 1_000,
        name: `料理 ${foodId}`,
        ingredients: ['基础材料'],
        cooker: '煮锅',
      },
      extraIngredients: [],
    },
    beverage: {
      beverage: {
        id: foodId + 2_000,
        name: `酒水 ${foodId}`,
      },
    },
    conditionResults: [],
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
    currentAnger: null,
    maxAnger: null,
    targetAnger: null,
    recommendationPolicy: 'mizuchi-trial',
    automationPolicy: 'strict-role',
    source: 'operational-audit',
    error: null,
  };
}
