import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const [
  workbenchSource,
  hookSource,
  domainSource,
  serviceSource,
  extensionSource,
  settingsSource,
  queueSource,
  cardSource,
  apiSource,
  typesSource,
  mockSource,
] = await Promise.all([
  readFile('apps/companion/src/companion/ModWorkbench.tsx', 'utf8'),
  readFile('apps/companion/src/companion/hooks/useRareOrderParticipation.ts', 'utf8'),
  readFile('apps/companion/src/companion/domain/rare-order-participation.ts', 'utf8'),
  readFile('apps/companion/src/companion/pages/ModServicePanel.tsx', 'utf8'),
  readFile('apps/companion/src/companion/pages/ModRareGuestParticipationPanel.tsx', 'utf8'),
  readFile('apps/companion/src/companion/pages/ModSettingsPanel.tsx', 'utf8'),
  readFile('apps/companion/src/companion/pages/service/RareOrderParticipationPanel.tsx', 'utf8'),
  readFile('apps/companion/src/companion/pages/service/RareOrderRecommendationCard.tsx', 'utf8'),
  readFile('apps/companion/src/companion/api.ts', 'utf8'),
  readFile('apps/companion/src/companion/types.ts', 'utf8'),
  readFile('scripts/mock-local-api.mjs', 'utf8'),
]);

for (const contract of [
  'useRareOrderParticipation({',
  'moduleEnabled: companionPreferences.rareGuestParticipationModuleEnabled,',
  'if (!rareParticipationActive) return null;',
  'selectOperationalOrderPreparationCandidates(',
  'buildRareGameUiTargetFromParticipationQueue(',
  'operationalRecommendations={operationalOrderRecommendations}',
  'rareParticipationModuleEnabled={rareOrderParticipation.moduleEnabled}',
  'collectionComplete: rareOrderCollectionComplete,',
  'const effectiveServiceRecommendationTab: ServiceRecommendationTab =',
  'serviceRecommendationTab={effectiveServiceRecommendationTab}',
  '<ModRareGuestParticipationPanel',
]) {
  assert.ok(workbenchSource.includes(contract), `ModWorkbench is missing rare participation wiring: ${contract}`);
}
assert.match(
  workbenchSource,
  /rareOrderParticipation\.participationActive\s*\?\s*rareOrderParticipation\.projectionReady\s*\?\s*rareOrderParticipation\.projection\?\.operationalOrders/,
  'Game UI target source reconciliation must use only the authoritative operational projection.',
);
assert.match(
  workbenchSource,
  /operationalOrderRecommendations === null[\s\S]+selectOrderPreparationCandidates[\s\S]+selectOperationalOrderPreparationCandidates/,
  'Only the explicit empty-roster sentinel may use the legacy automation path.',
);
assert.match(
  workbenchSource,
  /markRareParticipationMutationBoundary[\s\S]+rareParticipationMutationBusyRef\.current = true;[\s\S]+automationRequestEpochRef\.current \+= 1;/,
  'A participation mutation must synchronously freeze new automation and retire admitted client requests.',
);
assert.match(
  workbenchSource,
  /const runAutoFirstOrder = useCallback[\s\S]+rareParticipationMutationBusyRef\.current[\s\S]+const runAutoNormalOrder = useCallback[\s\S]+rareParticipationMutationBusyRef\.current/,
  'Rare and normal automation entry points must both remain closed while a participation mutation is in flight.',
);
assert.match(
  workbenchSource,
  /const effectiveServiceRecommendationTab:[\s\S]+!rareOrderParticipation\.moduleEnabled\s*&&\s*serviceRecommendationTab === 'rare-queue'[\s\S]+\? 'rare'[\s\S]+setServiceRecommendationTab\(\(current\) => current === 'rare-queue' \? 'rare' : current\)/,
  'A disabled rare scheduling module must synchronously normalize the effective queue tab and persisted tab state to rare.',
);

for (const contract of [
  'requestEpochRef',
  'requestAbortRef',
  'sameExactTargetSet(currentTargets, requestedTarget.expectedCurrentOrders)',
  'onMutationBoundary();',
  'setOverlay({ bindingKey, snapshot: response.participation })',
  'isRareOrderParticipationSnapshotAligned(',
  'Promise.allSettled([refreshSnapshot(), refreshAuthority()])',
  'buildRareOrderParticipationMutationKey(action, target)',
  "type: 'order', order",
  'const participationActive = moduleEnabled && managedGuestIds.length > 0;',
  'const projectionReady = !participationActive || snapshotAligned;',
  'busyMutationKey,',
  "moduleEnabled ? 'module-enabled' : 'module-disabled'",
  'moduleEnabled,\n    participationActive,',
]) {
  assert.ok(hookSource.includes(contract), `Participation mutation lifecycle is missing: ${contract}`);
}
assert.ok(
  hookSource.includes('connectionRevision,')
    && hookSource.includes('authorityRevision,')
    && hookSource.includes('businessGeneration,')
    && hookSource.includes("collectionComplete ? 'complete' : 'incomplete'")
    && hookSource.includes('orderCollectionSignature,')
    && hookSource.includes('managedSignature,'),
  'Late-response binding must include connection, authority, business generation, complete exact order collection, and full roster.',
);

for (const contract of [
  'active: boolean;',
  'managedGuestIds: readonly number[];',
  'snapshot.businessGeneration !== expectedBusinessGeneration',
  'sameGuestIdSet(snapshot.managedGuestIds, expectedManagedIds)',
  'if (managedGuestIds.length === 0)',
  'queuePositionsSeen.has(entry.queuePosition!)',
  'queuePositionsSeen.has(expectedPosition)',
  'if (entries.size !== currentOrderKeys.size)',
  'operationallyParticipating: false',
]) {
  assert.ok(domainSource.includes(contract), `Projection authority boundary is missing: ${contract}`);
}
assert.ok(!domainSource.includes('recommendationOrders'), 'The removed always-visible paused recommendation path must not remain.');

for (const contract of [
  "export type ServiceRecommendationTab = 'rare' | 'rare-queue' | 'normal'",
  'data-service-order-tab="rare-queue"',
  '<RareOrderParticipationPanel',
  'busyMutationKey={rareParticipationBusyMutationKey}',
  'onMutateGuest={onMutateRareGuestOrders}',
  'onMutateOrder={onMutateRareOrder}',
  'participationEnabled={rareParticipationEnabled}',
  'participationReady={rareParticipationReady}',
  'buildParticipatingRareOrderPresentationRows(',
  'operationalRecommendations,',
]) {
  assert.ok(serviceSource.includes(contract), `Service queue/recommendation wiring is missing: ${contract}`);
}
assert.match(
  serviceSource,
  /\{rareParticipationModuleEnabled && \(\s*<TabsTrigger[\s\S]+?value="rare-queue"/,
  'The rare queue tab trigger must only render while the module is enabled.',
);
assert.match(
  serviceSource,
  /\{rareParticipationModuleEnabled && \(\s*<TabsContent value="rare-queue"/,
  'The rare queue tab content must only render while the module is enabled.',
);
assert.ok(
  serviceSource.includes("rareParticipationModuleEnabled && value === 'rare-queue'"),
  'A disabled module must reject rare-queue tab selection events.',
);
for (const contract of [
  '<ModuleControlPanel',
  'moduleId="rare-guest-participation"',
  'label="启用稀客调度模块"',
  'data-rare-guest-participation-roster="true"',
  'extensions:rare-participation:module-toggle',
]) {
  assert.ok(extensionSource.includes(contract), `Extension module wiring is missing: ${contract}`);
}
assert.ok(!settingsSource.includes('rare-participation'), 'Settings must not retain the removed rare-participation route.');
assert.ok(queueSource.includes('group.allCurrentTargets'));
assert.ok(queueSource.includes('busyMutationKey !== null'));
assert.ok(queueSource.includes("'enable-front'"));
assert.ok(queueSource.includes("'enable-tail'"));
assert.ok(queueSource.includes('onMutateOrder'));
assert.ok(queueSource.includes('const groupMutationDisabled = readOnly || busyMutationKey !== null'));
assert.ok(queueSource.includes('const actionDisabled = readOnly || busyMutationKey !== null || !identity'));
assert.ok(queueSource.includes('children={error'));
assert.ok(!queueSource.includes('moduleEnabled'));
assert.ok(!queueSource.includes('onOpenModule'));
assert.ok(!queueSource.includes('data-rare-order-participation-read-only'));
assert.ok(!queueSource.includes('只读'));
assert.ok(!queueSource.includes('稀客调度模块已停用'));
assert.ok(!queueSource.includes('队列说明'));
assert.ok(!queueSource.includes('data-rare-order-participation-disclosure'));
assert.ok(!queueSource.includes('暂停会从经营中稀客推荐隐藏订单'));
assert.ok(!queueSource.includes('已开锅任务按安全边界保留并等待恢复'));
assert.ok(
  !domainSource.includes('已暂停：仅在稀客队列和诊断中保留；不显示经营推荐，也不参与高亮、新自动化或资源预约。已开锅任务等待恢复。'),
  'The removed per-order paused description must not remain in the participation domain.',
);
assert.ok(extensionSource.includes('placeholder="输入姓名、ID或地区"'));
assert.ok(!extensionSource.includes('placeholder="输入姓名、ID、地区或 DLC"'));
assert.ok(!cardSource.includes('参与已暂停'));
assert.ok(!cardSource.includes('推荐保留'));
assert.ok(!cardSource.includes('参与状态不可用'));
assert.ok(cardSource.includes('queuedPosition'));
assert.ok(apiSource.includes('action: request.action'));
assert.ok(apiSource.includes("type: 'order'"));
assert.ok(typesSource.includes("'suspended-participation'"));

assert.match(typesSource, /export type ExtensionTab = [^;]*'rare-participation'/);
assert.doesNotMatch(
  typesSource.match(/export type SettingsTab =[\s\S]*?;/)?.[0] ?? '',
  /rare-participation/,
);

const removedDismissSources = [workbenchSource, serviceSource, extensionSource, apiSource, typesSource, mockSource];
for (const source of removedDismissSources) {
  assert.ok(!source.includes('dismissRuntimeRareOrder'));
  assert.ok(!source.includes('RareOrderDismissResponse'));
  assert.ok(!source.includes('/orders/rare/dismiss'));
  assert.ok(!source.includes('rare-order-dismiss:'));
}

console.log(
  'PASS: production UI wires a default-off primary-owned extension module, an exact-lifecycle queue tab, '
  + 'authoritative queue-filtered recommendations, queue-ordered highlighter/automation consumers, stale-response fencing, '
  + 'and no legacy settings or dismiss path.',
);
