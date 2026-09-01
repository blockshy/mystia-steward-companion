import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

import { MantineProvider } from '@mantine/core';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true },
  appType: 'custom',
  logLevel: 'error',
});

let domain;
let queueModule;
let extensionModule;
let extensionControlModule;
try {
  [domain, queueModule, extensionModule, extensionControlModule] = await Promise.all([
    vite.ssrLoadModule('/src/companion/domain/rare-order-participation.ts'),
    vite.ssrLoadModule('/src/companion/pages/service/RareOrderParticipationPanel.tsx'),
    vite.ssrLoadModule('/src/companion/pages/ModRareGuestParticipationPanel.tsx'),
    vite.ssrLoadModule('/src/companion/domain/extension-module-control.ts'),
  ]);
} finally {
  await vite.close();
}

const {
  buildManagedRareOrderGroups,
  buildRareGuestRosterSections,
  buildRareOrderExactIdentity,
  buildRareOrderExactIdentityKey,
  buildRareOrderParticipationProjection,
  countCurrentRareOrdersByGuestId,
  isRareOrderParticipationSnapshotAligned,
  updateManagedRareGuestIds,
} = domain;
const { RareOrderParticipationPanel } = queueModule;
const { ModRareGuestParticipationPanel } = extensionModule;
const { resolvePrimaryExtensionModuleControl } = extensionControlModule;

const unmanagedOrder = order({
  traceId: 'R-1',
  orderLifecycleSequence: 1,
  deskCode: 0,
  guestId: 1,
  runtimeGuestId: 101,
  guestName: '同名稀客',
  foodTagId: -1,
  beverageTagId: -2,
});
const pausedOrder = order({
  traceId: 'R-2',
  orderLifecycleSequence: 2,
  deskCode: 1,
  guestId: 10,
  runtimeGuestId: 110,
  guestName: '受控稀客',
  foodTagId: 11,
  beverageTagId: 12,
});
const queuedOrder = order({
  traceId: 'R-3',
  orderLifecycleSequence: 3,
  deskCode: 2,
  guestId: 10,
  runtimeGuestId: 110,
  guestName: '受控稀客',
  foodTagId: 13,
  beverageTagId: 14,
  missionRecipePriority: {
    traceId: 'mission-trace',
    deskCode: 2,
    guestId: 10,
    runtimeGuestId: 110,
    foodId: 13,
    recipeId: 99,
    missionGeneration: 2,
    businessGeneration: 7,
  },
});
const incompleteOrder = order({
  traceId: undefined,
  orderLifecycleSequence: 4,
  deskCode: 3,
  guestId: 20,
  runtimeGuestId: 120,
  guestName: '身份不完整稀客',
  foodTagId: 15,
  beverageTagId: 16,
});
const orders = [unmanagedOrder, pausedOrder, queuedOrder];
const ordersWithIncomplete = [...orders, incompleteOrder];
const snapshot = participationSnapshot([
  entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
  entry(pausedOrder, { managed: true, state: 'paused', queuePosition: null }),
  entry(queuedOrder, { managed: true, state: 'queued', queuePosition: 2 }),
]);

const projection = buildRareOrderParticipationProjection({
  orders,
  collectionComplete: true,
  managedGuestIds: [10, 20],
  businessGeneration: 7,
  snapshot,
});
assert.deepEqual(
  projection.operationalOrders.map((candidate) => candidate.traceId),
  ['R-1', 'R-3'],
  '只有 Mod 权威认定自动参与或已入队的订单可进入运行时消费者。',
);

const groups = buildManagedRareOrderGroups({
  orders,
  collectionComplete: true,
  managedGuestIds: [10, 20],
  businessGeneration: 7,
  snapshot,
});
assert.deepEqual(groups.map((group) => group.guestId), [10]);
assert.deepEqual(groups[0].rows.map((row) => row.state), ['queued', 'paused']);
assert.equal(groups[0].rows[0].queuePosition, 2, '队列位置必须包含非名单的自动参与订单。');
assert.equal(groups[0].pausedTargets.length, 1);
assert.equal(groups[0].queuedTargets.length, 1);
assert.equal(groups[0].allCurrentTargets.length, 2);
assert.deepEqual(
  groups[0].allCurrentTargets.map((identity) => identity.traceId).sort(),
  ['R-2', 'R-3'],
  'mixed paused/queued lifecycle 的两个按钮都必须回显完整当前集合。',
);
assert.equal(groups[0].pausedTargets[0].traceId, 'R-2');
assert.equal(groups[0].queuedTargets[0].orderLifecycleSequence, 3);
const incompleteGroups = buildManagedRareOrderGroups({
  orders: ordersWithIncomplete,
  collectionComplete: true,
  managedGuestIds: [10, 20],
  businessGeneration: 7,
  snapshot,
});
assert.equal(incompleteGroups.every((group) => group.hasUnavailableRows), true);
assert.equal(
  incompleteGroups.every((group) => group.allCurrentTargets.length === 0),
  true,
  '一个 malformed/缺失 lifecycle 必须使整份当前订单集合不可操作，不能只禁用坏行。',
);

assert.equal(
  buildRareOrderExactIdentityKey(buildRareOrderExactIdentity(unmanagedOrder, 7)),
  buildRareOrderExactIdentityKey(buildRareOrderExactIdentity({
    ...unmanagedOrder,
    deskCode: 99,
    runtimeGuestId: 999,
    foodTagId: 999,
    beverageTagId: 998,
  }, 7)),
  '桌位、runtime guest 和 Tag 观测值不得扩大或改写公开 lifecycle identity。',
);
assert.equal(buildRareOrderExactIdentity(incompleteOrder, 7), null);
assert.equal(
  buildRareOrderExactIdentity({ ...unmanagedOrder, traceId: ' R-1' }, 7),
  null,
  '带空白的 trace 不得 trim 后冒充 canonical exact identity。',
);
assert.equal(buildRareOrderExactIdentity({ ...unmanagedOrder, traceId: 'R-1 ' }, 7), null);
assert.equal(buildRareOrderExactIdentity({ ...unmanagedOrder, traceId: 'order-1' }, 7), null);

const sameGuestNewLifecycle = order({
  ...pausedOrder,
  traceId: 'R-5',
  orderLifecycleSequence: 5,
});
const newLifecycleGroups = buildManagedRareOrderGroups({
  orders: [pausedOrder, sameGuestNewLifecycle],
  collectionComplete: true,
  managedGuestIds: [10],
  businessGeneration: 7,
  snapshot,
});
assert.equal(newLifecycleGroups[0].rows.find((row) => row.order === sameGuestNewLifecycle)?.state, 'unavailable');
assert.deepEqual(
  newLifecycleGroups[0].pausedTargets,
  [],
  '过去的稀客级授权不得自动授权后续新 lifecycle。',
);
assert.deepEqual(newLifecycleGroups[0].allCurrentTargets, []);

const explicitNewLifecycleSnapshot = participationSnapshot([
  entry(sameGuestNewLifecycle, { managed: true, state: 'paused', queuePosition: null }),
], [10]);
assert.equal(
  buildManagedRareOrderGroups({
    orders: [sameGuestNewLifecycle],
    collectionComplete: true,
    managedGuestIds: [10],
    businessGeneration: 7,
    snapshot: explicitNewLifecycleSnapshot,
  })[0].rows[0].state,
  'paused',
  '同一稀客的新 lifecycle 必须由 Mod 显式投影为默认暂停。',
);

const requeuedSnapshot = participationSnapshot([
  entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
  entry(queuedOrder, { managed: true, state: 'queued', queuePosition: 2 }),
  entry(pausedOrder, { managed: true, state: 'queued', queuePosition: 3 }),
], [10]);
const requeuedGroups = buildManagedRareOrderGroups({
  orders: [unmanagedOrder, pausedOrder, queuedOrder],
  collectionComplete: true,
  managedGuestIds: [10],
  businessGeneration: 7,
  snapshot: requeuedSnapshot,
});
assert.deepEqual(requeuedGroups[0].rows.map((row) => row.order.traceId), ['R-3', 'R-2']);
assert.equal(requeuedGroups[0].rows[1].queuePosition, 3, '重新启用必须反映新的队尾序号。');

const duplicateSnapshot = participationSnapshot([
  entry(pausedOrder, { managed: true, state: 'paused', queuePosition: null }),
  entry(pausedOrder, { managed: true, state: 'paused', queuePosition: null }),
], [10]);
const duplicateProjection = buildRareOrderParticipationProjection({
  orders: [pausedOrder],
  collectionComplete: true,
  managedGuestIds: [10],
  businessGeneration: 7,
  snapshot: duplicateSnapshot,
});
assert.deepEqual(duplicateProjection.operationalOrders, []);
assert.equal(duplicateProjection.resolutions[0].displayState, 'unavailable');

const nonCanonicalEntryProjection = buildRareOrderParticipationProjection({
  orders: [pausedOrder],
  collectionComplete: true,
  managedGuestIds: [10],
  businessGeneration: 7,
  snapshot: participationSnapshot([{
    ...entry(pausedOrder, { managed: true, state: 'queued', queuePosition: 1 }),
    traceId: ' R-2',
  }], [10]),
});
assert.deepEqual(nonCanonicalEntryProjection.operationalOrders, []);
assert.equal(
  nonCanonicalEntryProjection.resolutions[0].displayState,
  'unavailable',
  'Mod entry 的非 canonical trace 不得 trim 后绑定订单。',
);

const staleRosterProjection = buildRareOrderParticipationProjection({
  orders: [pausedOrder],
  collectionComplete: true,
  managedGuestIds: [10],
  businessGeneration: 7,
  snapshot: participationSnapshot([
    entry(pausedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
  ], [10]),
});
assert.deepEqual(
  staleRosterProjection.operationalOrders,
  [],
  'profile 名单与 participation 快照未对齐时即使 entry 正在参与也必须 fail-closed。',
);

const noAuthorityProjection = buildRareOrderParticipationProjection({
  orders: [unmanagedOrder, pausedOrder],
  collectionComplete: true,
  managedGuestIds: [10],
  businessGeneration: 7,
  snapshot: null,
});
assert.deepEqual(
  noAuthorityProjection.operationalOrders,
  [],
  '非空名单下 participation 快照缺失时，所有稀客运行时消费者都必须 fail-closed。',
);
const zeroRevisionSnapshot = {
  ...participationSnapshot([
    entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
  ], [10]),
  participationRevision: 0,
};
assert.deepEqual(
  buildRareOrderParticipationProjection({
    orders: [unmanagedOrder],
    collectionComplete: true,
    managedGuestIds: [10],
    businessGeneration: 7,
    snapshot: zeroRevisionSnapshot,
  }).operationalOrders,
  [],
  'active participation 快照的 revision 必须为正数，零值不得进入运行时消费者。',
);
assert.equal(
  isRareOrderParticipationSnapshotAligned({
    snapshot: zeroRevisionSnapshot,
    businessGeneration: 7,
    managedGuestIds: [10],
    orders: [unmanagedOrder],
    collectionComplete: true,
  }),
  false,
  '零 participation revision 不得授权写请求。',
);
const mismatchedRosterProjection = buildRareOrderParticipationProjection({
  orders: [unmanagedOrder, queuedOrder],
  collectionComplete: true,
  managedGuestIds: [10],
  businessGeneration: 7,
  snapshot: participationSnapshot([
    entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
    entry(queuedOrder, { managed: true, state: 'queued', queuePosition: 2 }),
  ], []),
});
assert.deepEqual(
  mismatchedRosterProjection.operationalOrders,
  [],
  'profile 与 snapshot 的完整受控名单不一致时，不得仅按单条 entry 放行未受控订单。',
);

const malformedMixedSnapshot = participationSnapshot([
  entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
  {
    ...entry(queuedOrder, { managed: true, state: 'queued', queuePosition: 2 }),
    traceId: ' R-3',
  },
], [10]);
assert.deepEqual(
  buildRareOrderParticipationProjection({
    orders: [unmanagedOrder, queuedOrder],
    collectionComplete: true,
    managedGuestIds: [10],
    businessGeneration: 7,
    snapshot: malformedMixedSnapshot,
  }).operationalOrders,
  [],
  '一个 malformed entry 必须使同快照内原本有效的订单也 fail-closed。',
);

const duplicateQueueSnapshot = participationSnapshot([
  entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
  entry(queuedOrder, { managed: true, state: 'queued', queuePosition: 1 }),
], [10]);
assert.deepEqual(
  buildRareOrderParticipationProjection({
    orders: [unmanagedOrder, queuedOrder],
    collectionComplete: true,
    managedGuestIds: [10],
    businessGeneration: 7,
    snapshot: duplicateQueueSnapshot,
  }).operationalOrders,
  [],
  '不同 lifecycle 不得共享 queuePosition 后回退到前端稳定排序。',
);

for (const [label, candidateOrders, candidateSnapshot] of [
  [
    'snapshot-extra',
    [unmanagedOrder],
    participationSnapshot([
      entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
      entry(queuedOrder, { managed: true, state: 'queued', queuePosition: 2 }),
    ], [10]),
  ],
  [
    'orders-extra',
    [unmanagedOrder, queuedOrder],
    participationSnapshot([
      entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
    ], [10]),
  ],
  [
    'duplicate-order',
    [unmanagedOrder, unmanagedOrder],
    participationSnapshot([
      entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
    ], [10]),
  ],
]) {
  assert.deepEqual(
    buildRareOrderParticipationProjection({
      orders: candidateOrders,
      collectionComplete: true,
      managedGuestIds: [10],
      businessGeneration: 7,
      snapshot: candidateSnapshot,
    }).operationalOrders,
    [],
    `${label} exact identity 集合不一致时不得放行任何订单。`,
  );
}

const completePairSnapshot = participationSnapshot([
  entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
  entry(queuedOrder, { managed: true, state: 'queued', queuePosition: 2 }),
], [10]);
assert.deepEqual(
  buildRareOrderParticipationProjection({
    orders: [unmanagedOrder, queuedOrder],
    collectionComplete: false,
    managedGuestIds: [10],
    businessGeneration: 7,
    snapshot: completePairSnapshot,
  }).operationalOrders,
  [],
  'night collection 标记为 partial/error 时不得使用上一份 participation 队列跳过缺失队首。',
);
assert.equal(
  isRareOrderParticipationSnapshotAligned({
    snapshot: completePairSnapshot,
    businessGeneration: 7,
    managedGuestIds: [10],
    orders: [unmanagedOrder, queuedOrder],
    collectionComplete: false,
  }),
  false,
  'partial/error current orders 不得接纳 mutation response overlay。',
);

for (const [label, invalidQueuePosition] of [
  ['non-contiguous', 3],
  ['not-safe', Number.MAX_SAFE_INTEGER + 1],
]) {
  assert.deepEqual(
    buildRareOrderParticipationProjection({
      orders: [unmanagedOrder, queuedOrder],
      collectionComplete: true,
      managedGuestIds: [10],
      businessGeneration: 7,
      snapshot: {
        ...completePairSnapshot,
        entries: completePairSnapshot.entries.map((candidate) => candidate.traceId === queuedOrder.traceId
          ? { ...candidate, queuePosition: invalidQueuePosition }
          : candidate),
      },
    }).operationalOrders,
    [],
    `${label} queuePosition 不得授权运行时消费者。`,
  );
}

for (const invalidRoster of [[-1], [10, 10], [10.5]]) {
  assert.deepEqual(
    buildRareOrderParticipationProjection({
      orders: [unmanagedOrder],
      collectionComplete: true,
      managedGuestIds: invalidRoster,
      businessGeneration: 7,
      snapshot: participationSnapshot([
        entry(unmanagedOrder, { managed: false, state: 'automatic', queuePosition: 1 }),
      ], []),
    }).operationalOrders,
    [],
    '非空但非法的 profile roster 不得被静默过滤成空名单旧路径。',
  );
}
assert.deepEqual(
  buildRareOrderParticipationProjection({
    orders: [
      unmanagedOrder,
      pausedOrder,
      { ...incompleteOrder, guestId: null },
      { ...unmanagedOrder, traceId: ' R-1', guestId: null },
    ],
    collectionComplete: false,
    managedGuestIds: [],
    businessGeneration: 7,
    snapshot,
  }).operationalOrders,
  [
    unmanagedOrder,
    pausedOrder,
    { ...incompleteOrder, guestId: null },
    { ...unmanagedOrder, traceId: ' R-1', guestId: null },
  ],
  '空名单必须硬旁路参与投影，即使快照存在、guestId/trace 缺失也保持现有行为。',
);

const roster = buildRareGuestRosterSections({
  customers: [
    customer(10, '受控稀客'),
    customer(20, '可添加稀客'),
  ],
  managedGuestIds: [999, 10, 10],
});
assert.deepEqual(roster.managed.map((row) => row.guestId).sort((a, b) => a - b), [10, 999]);
assert.equal(roster.managed.find((row) => row.guestId === 999)?.catalogAvailable, false);
assert.deepEqual(roster.available.map((row) => row.guestId), [20]);
assert.deepEqual(updateManagedRareGuestIds([10, 2, 10], 7, true), [2, 7, 10]);
assert.deepEqual(updateManagedRareGuestIds([2, 7, 10], 7, false), [2, 10]);
assert.equal(countCurrentRareOrdersByGuestId(orders).get(10), 2);

const queueMarkup = renderToStaticMarkup(React.createElement(
  MantineProvider,
  null,
  React.createElement(RareOrderParticipationPanel, {
    orders,
    collectionComplete: true,
    managedGuestIds: [10, 20],
    snapshot,
    businessGeneration: 7,
    businessActive: true,
    readOnly: false,
    onMutateGuest: () => undefined,
    onMutateOrder: () => undefined,
  }),
));
for (const expectedText of [
  '已暂停',
  '队列 #2',
  '任务料理优先',
  '全部优先启用',
  '全部队尾启用',
  '优先启用',
  '暂停该订单',
]) {
  assert.ok(queueMarkup.includes(expectedText), `Queue panel is missing reviewed text: ${expectedText}`);
}
assert.ok(queueMarkup.includes('data-list-panel-header-only="true"'));
assert.ok(!queueMarkup.includes('队列说明'));
assert.ok(!queueMarkup.includes('data-rare-order-participation-disclosure'));
assert.ok(!queueMarkup.includes('data-rare-order-participation-read-only'));
assert.ok(!queueMarkup.includes('只读'));
assert.ok(!queueMarkup.includes(
  '已暂停：仅在稀客队列和诊断中保留；不显示经营推荐，也不参与高亮、新自动化或资源预约。已开锅任务等待恢复。',
));
assert.ok(queueMarkup.includes('service:rare-participation:guest:10:enable-front'));
assert.ok(queueMarkup.includes('service:rare-participation:guest:10:enable-tail'));
assert.ok(queueMarkup.includes('service:rare-participation:guest:10:pause'));
assert.ok(queueMarkup.includes('service:rare-participation:order:R-2:2:enable-front'));
assert.ok(queueMarkup.includes('service:rare-participation:order:R-3:3:pause'));

const readOnlyQueueMarkup = renderToStaticMarkup(React.createElement(
  MantineProvider,
  null,
  React.createElement(RareOrderParticipationPanel, {
    orders,
    collectionComplete: true,
    managedGuestIds: [10, 20],
    snapshot,
    businessGeneration: 7,
    businessActive: true,
    readOnly: true,
    onMutateGuest: () => undefined,
    onMutateOrder: () => undefined,
  }),
));
assert.ok(!readOnlyQueueMarkup.includes('data-rare-order-participation-read-only'));
assert.ok(!readOnlyQueueMarkup.includes('只读'));
assert.ok(readOnlyQueueMarkup.includes('data-list-panel-header-only="true"'));
const readOnlyActionTags = readOnlyQueueMarkup.match(
  /<button\b[^>]*data-rare-order-participation-action="true"[^>]*>/g,
) ?? [];
assert.ok(readOnlyActionTags.length > 0, 'Read-only queue must still render its mutation actions.');
assert.ok(
  readOnlyActionTags.every((tag) => tag.includes('disabled=""') && tag.includes('data-disabled="true"')),
  'Read-only queue must keep every guest/order mutation action disabled.',
);

const errorQueueMarkup = renderToStaticMarkup(React.createElement(
  MantineProvider,
  null,
  React.createElement(RareOrderParticipationPanel, {
    orders,
    collectionComplete: true,
    managedGuestIds: [10, 20],
    snapshot,
    businessGeneration: 7,
    businessActive: true,
    readOnly: false,
    error: '模拟队列错误',
    onMutateGuest: () => undefined,
    onMutateOrder: () => undefined,
  }),
));
assert.ok(errorQueueMarkup.includes('role="alert"'));
assert.ok(errorQueueMarkup.includes('模拟队列错误'));

const unavailableQueueMarkup = renderToStaticMarkup(React.createElement(
  MantineProvider,
  null,
  React.createElement(RareOrderParticipationPanel, {
    orders: ordersWithIncomplete,
    collectionComplete: true,
    managedGuestIds: [10, 20],
    snapshot,
    businessGeneration: 7,
    businessActive: true,
    readOnly: false,
    onMutateGuest: () => undefined,
    onMutateOrder: () => undefined,
  }),
));
assert.ok(unavailableQueueMarkup.includes('状态不可用'));
assert.ok(unavailableQueueMarkup.includes('为避免部分授权，本组暂不可操作'));
assert.ok(unavailableQueueMarkup.includes('订单缺少 trace、lifecycle 或原始身份标量，已拒绝猜测。'));

const extensionMarkup = renderToStaticMarkup(React.createElement(
  MantineProvider,
  null,
  React.createElement(ModRareGuestParticipationPanel, {
    control: resolvePrimaryExtensionModuleControl({
      enabled: true,
      connected: true,
      authorityReady: true,
      currentDeviceIsPrimary: false,
      secondaryReadOnlyReason: '副设备只读',
    }),
    customers: [customer(10, '受控稀客'), customer(20, '可添加稀客')],
    managedGuestIds: [10],
    currentOrders: orders,
    onModuleEnabledChange: () => undefined,
    onManagedGuestIdsChange: () => undefined,
  }),
));
for (const expectedText of [
  '参与随时启用/暂停的稀客列表',
  '名单内稀客的每一笔新订单都默认暂停',
  '副设备只读',
  '当前 2 笔',
]) {
  assert.ok(extensionMarkup.includes(expectedText), `Extension panel is missing reviewed text: ${expectedText}`);
}
assert.ok(extensionMarkup.includes('启用稀客调度模块'));
assert.ok(extensionMarkup.includes('placeholder="输入姓名、ID或地区"'));
assert.ok(extensionMarkup.includes('data-gamepad-focus-key="extensions:rare-participation:guest:10:remove"'));
assert.match(extensionMarkup, /<button[^>]*data-disabled="true"[^>]*extensions:rare-participation:guest:10:remove/);

const root = new URL('../../', import.meta.url);
const extensionSource = await readFile(
  new URL('apps/companion/src/companion/pages/ModRareGuestParticipationPanel.tsx', root),
  'utf8',
);
assert.ok(extensionSource.includes('移出并自动排尾'));
assert.ok(extensionSource.includes('这些订单将由 Mod'));
assert.ok(extensionSource.includes('currentOrderCounts.get(guestId)'));
assert.ok(extensionSource.includes('关闭时保留名单'));

console.log(
  'PASS: rare-order participation hides paused recommendations, gates all participating consumers with exact lifecycle state, '
  + 'supports exact order/guest mutation controls with continuous queue positions, and exposes a default-off primary-only extension module.',
);

function participationSnapshot(entries, managedGuestIds = [10, 20]) {
  return {
    active: true,
    businessGeneration: 7,
    participationRevision: 4,
    managedGuestIds,
    entries,
  };
}

function entry(sourceOrder, participation) {
  return {
    traceId: sourceOrder.traceId,
    orderLifecycleSequence: sourceOrder.orderLifecycleSequence,
    guestId: sourceOrder.guestId,
    managed: participation.managed,
    participating: participation.state !== 'paused',
    reasonCode: participation.state,
    queuePosition: participation.queuePosition ?? null,
  };
}

function order(overrides) {
  return {
    traceId: 'R-99',
    orderLifecycleSequence: 1,
    deskCode: 0,
    guestId: 1,
    runtimeGuestId: 101,
    guestName: '稀客',
    foodTagId: 1,
    foodTag: '家常',
    beverageTagId: 2,
    beverageTag: '可加热',
    source: 'audit',
    ...overrides,
  };
}

function customer(id, name) {
  return {
    id,
    name,
    places: ['人间之里'],
    dlc: 0,
  };
}
