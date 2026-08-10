import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  buildRareGuestInvitationContextIdentity,
  getRareGuestInvitationTransientRetryDelayMs,
  RARE_GUEST_INVITATION_TRANSIENT_RETRY_DELAYS_MS,
} from '../../apps/companion/src/companion/rare-guest-invitation-refresh.ts';

const baseSnapshot = {
  runtimeLoaded: true,
  runtimeDaySceneReady: true,
  runtimeDaySceneGeneration: 7,
  activeDayMapLabel: 'YoukaiTrail',
};
const baseContext = {
  connected: true,
  connectionRevision: 3,
  enabled: true,
  normalizedEndpoint: 'http://127.0.0.1:32145',
  scope: 'current',
  snapshot: baseSnapshot,
};

const identity = buildRareGuestInvitationContextIdentity(baseContext);
assert.ok(identity, 'An enabled module in a stable day scene must produce an invitation context identity.');

for (const context of [
  { ...baseContext, enabled: false },
  { ...baseContext, connected: false },
  { ...baseContext, snapshot: null },
  { ...baseContext, snapshot: { ...baseSnapshot, runtimeLoaded: false } },
  { ...baseContext, snapshot: { ...baseSnapshot, runtimeDaySceneReady: false } },
  { ...baseContext, snapshot: { ...baseSnapshot, runtimeDaySceneGeneration: 0 } },
  { ...baseContext, snapshot: { ...baseSnapshot, activeDayMapLabel: '' } },
]) {
  assert.equal(
    buildRareGuestInvitationContextIdentity(context),
    null,
    'A disabled or unsafe context must not permit invitation reads or writes.',
  );
}

for (const context of [
  { ...baseContext, connectionRevision: 4 },
  { ...baseContext, normalizedEndpoint: 'http://127.0.0.1:32146' },
  { ...baseContext, scope: 'all' },
  { ...baseContext, snapshot: { ...baseSnapshot, runtimeDaySceneGeneration: 8 } },
  { ...baseContext, snapshot: { ...baseSnapshot, activeDayMapLabel: 'HumanVillage' } },
]) {
  assert.notEqual(
    buildRareGuestInvitationContextIdentity(context),
    identity,
    'Every state boundary that can stale a candidate list must change its refresh identity.',
  );
}

assert.equal(
  buildRareGuestInvitationContextIdentity({
    ...baseContext,
    snapshot: {
      ...baseSnapshot,
      snapshotSignature: 'unrelated-snapshot-change',
      activeDayMapName: '妖怪兽道',
    },
  }),
  identity,
  'Unrelated snapshot updates must not repeatedly scan an unchanged invitation context.',
);

assert.deepEqual(
  RARE_GUEST_INVITATION_TRANSIENT_RETRY_DELAYS_MS,
  [500, 1_000, 2_000, 4_000],
  'Transient runtime readiness retries must use the reviewed finite backoff schedule.',
);
assert.deepEqual(
  RARE_GUEST_INVITATION_TRANSIENT_RETRY_DELAYS_MS.map(
    (_, index) => getRareGuestInvitationTransientRetryDelayMs(index),
  ),
  [500, 1_000, 2_000, 4_000],
);
assert.equal(getRareGuestInvitationTransientRetryDelayMs(-1), null);
assert.equal(getRareGuestInvitationTransientRetryDelayMs(4), null);
assert.equal(getRareGuestInvitationTransientRetryDelayMs(1.5), null);

const root = new URL('../../', import.meta.url);
const [
  apiSource,
  hookSource,
  workbenchSource,
  panelSource,
  mockSource,
  storageSource,
  moduleControlSource,
] = await Promise.all([
  readFile(new URL('apps/companion/src/companion/api.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/hooks/useRareGuestInvitations.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/pages/ModRareGuestInvitationsPanel.tsx', root), 'utf8'),
  readFile(new URL('scripts/mock-local-api.mjs', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/storage.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/pages/ModuleControlPanel.tsx', root), 'utf8'),
]);

const listApi = sourceSlice(
  apiSource,
  'export async function fetchAvailableRareGuestInvitations',
  'export async function inviteAvailableRareGuest',
);
assert.match(listApi, /readLocalApiJson<RareGuestInvitationResponse>/);
assert.doesNotMatch(listApi, /mutateRareGuestInvitation/);
assert.match(listApi, /signal: AbortSignal/);

const inviteAllApi = sourceSlice(
  apiSource,
  'export async function inviteAllAvailableRareGuests',
  'export async function fetchAvailableRareGuestInvitations',
);
const inviteOneApi = sourceSlice(
  apiSource,
  'export async function inviteAvailableRareGuest',
  'export async function dismissRuntimeRareOrder',
);
for (const [name, source] of [
  ['invite-all', inviteAllApi],
  ['invite-one', inviteOneApi],
]) {
  assert.match(source, /context: RareGuestInvitationWriteContext/);
  assert.match(source, /expectedDaySceneGeneration/);
  assert.match(source, /expectedMapLabel/);
  assert.match(source, /mutateRareGuestInvitation/);
  assert.ok(source.includes('scope'), `${name} must carry its explicit current/all scope.`);
}

for (const contract of [
  'buildRareGuestInvitationContextIdentity',
  'const listIdentity = visible ? contextIdentity : null',
  'requestGenerationRef',
  'listAbortControllerRef',
  'attemptedListIdentityRef',
  'latestListIdentityRef.current !== identity',
  'latestContextIdentityRef.current !== contextIdentity',
  'scheduleTransientListRetry(identity)',
  'clearTransientListRetry()',
  'if (!response.ok)',
  "getRareGuestInvitationError(response, '读取稀客邀请候选失败。')",
  "getRareGuestInvitationError(response, '批量邀请稀客失败。')",
  "getRareGuestInvitationError(response, '邀请稀客失败。')",
  'setRareGuestInvitationResult(null)',
  'rareGuestInvitationContextReady: contextIdentity !== null',
  "rareGuestInvitationBusyKey !== 'list'",
  'const writeContext = useMemo<RareGuestInvitationWriteContext | null>',
  'expectedDaySceneGeneration: snapshot.runtimeDaySceneGeneration',
  'expectedMapLabel,',
  'rareGuestInvitationLevels,\n        writeContext,',
  'rareGuestInvitationScope,\n        writeContext,',
]) {
  assert.ok(hookSource.includes(contract), `Invitation Hook is missing stale-request contract: ${contract}`);
}
assert.ok(workbenchSource.includes('connectionRevision,'));
assert.ok(workbenchSource.includes("const rareGuestInvitationVisible = tab === 'extensions' && extensionTab === 'rare-invitations'"));
assert.ok(workbenchSource.includes('enabled: rareGuestInvitationModuleEnabled'));
assert.ok(workbenchSource.includes('visible: rareGuestInvitationVisible'));
assert.ok(workbenchSource.includes('readStoredRareGuestInvitationModuleEnabled'));
assert.ok(workbenchSource.includes('persistRareGuestInvitationModuleEnabled(enabled)'));
assert.ok(workbenchSource.includes('runtimeDaySceneReady={snapshot?.runtimeDaySceneReady ?? false}'));
assert.ok(workbenchSource.includes('invitationContextReady={rareGuestInvitationContextReady}'));
assert.ok(workbenchSource.includes('data-gamepad-tab-value="extensions"'));
assert.ok(workbenchSource.includes('data-extension-tabs="true"'));
assert.ok(workbenchSource.includes('<TabsTrigger value="rare-invitations"'));
assert.ok(workbenchSource.includes('<TabsContent value="rare-invitations"'));
assert.doesNotMatch(workbenchSource, /data-gamepad-tab-value="(?:missions|rare-invitations|inventory)"/);
assert.ok(workbenchSource.includes('<ModRareGuestInvitationsPanel'));
assert.doesNotMatch(workbenchSource, /MissionPanelView|missionPanelView/);
assert.ok(panelSource.includes('{inviteAllError && <EmptyRow text={inviteAllError} />}'));
assert.ok(panelSource.includes(') : !inviteAllError && ('));
assert.ok(panelSource.includes('onClick={() => onInviteRareGuest(entry.id)}'));
assert.ok(panelSource.includes('const key = entry.id >= 0'));
assert.ok(panelSource.includes('label="启用稀客邀请模块"'));
assert.ok(panelSource.includes('稀客邀请模块已停用'));
assert.ok(panelSource.includes('rareGuestInvitationModuleToggleDisabled'));
assert.ok(panelSource.includes('const sourceEntries = inviteAllResult?.candidates ?? [];'));
assert.ok(panelSource.includes('...inviteAllResult.existingInvited,'));
assert.ok(panelSource.includes('const batchEligibleEntries = availableEntries'));
assert.ok(panelSource.includes('搜索只改变下方列表展示；羁绊筛选同时决定批量邀请范围。'));
assert.ok(panelSource.includes('kind="available"'));
assert.ok(panelSource.includes('kind="unavailable"'));
assert.ok(panelSource.includes('data-gamepad-focus-key="rare-invitations:invite-all"'));
assert.doesNotMatch(panelSource, /slice\(0, 12\)|formatFilteredCount|summarizeInvitationSkipped|mission-invitation/);
const invitedSectionIndex = panelSource.indexOf('data-rare-invitation-section="invited"');
const filterSectionIndex = panelSource.indexOf('data-rare-invitation-section="filters"');
const availableSectionIndex = panelSource.indexOf('kind="available"');
const unavailableSectionIndex = panelSource.indexOf('kind="unavailable"');
assert.ok(
  invitedSectionIndex >= 0
    && invitedSectionIndex < filterSectionIndex
    && filterSectionIndex < availableSectionIndex
    && availableSectionIndex < unavailableSectionIndex,
  'Invitation information hierarchy must be invited -> filters -> available -> unavailable.',
);
assert.ok(storageSource.includes("`${STORAGE_PREFIX}-rare-guest-invitation-module-enabled`"));
assert.match(
  storageSource,
  /readStoredRareGuestInvitationModuleEnabled\(\): boolean \{\s+return readStoredBoolean\(RARE_GUEST_INVITATION_MODULE_ENABLED_STORAGE_KEY, false\);/,
);
assert.ok(moduleControlSource.includes('data-gamepad-focus-key={focusKey}'));
assert.ok(moduleControlSource.includes('data-feature-module={moduleId}'));
assert.ok(mockSource.includes("invitation(10, '雾雨魔理沙'"));
assert.ok(mockSource.includes("'DLC1_Marisa'"));
assert.ok(mockSource.includes('existingInvited,'));

const postBranch = sourceSlice(mockSource, "if (request.method === 'POST')", "if (request.method !== 'GET')");
const getBranch = sourceSlice(mockSource, "if (request.method !== 'GET')", 'server.listen');
assert.doesNotMatch(postBranch, /path === '\/rare-guests\/invitations'/);
assert.match(getBranch, /path === '\/rare-guests\/invitations'/);

console.log(
  'PASS: rare-guest invitations use an isolated extension subpage and default-off persisted module gate, '
  + 'render invited/filter/available/unavailable sections in canonical order, keep search out of bulk scope, '
  + 'and retain GET-only reads, bounded retries, write identity, and stale-response isolation.',
);

function sourceSlice(source, startText, endText) {
  const start = source.indexOf(startText);
  const end = source.indexOf(endText, start + startText.length);
  assert.ok(start >= 0, `Source boundary not found: ${startText}`);
  assert.ok(end > start, `Source boundary not found: ${endText}`);
  return source.slice(start, end);
}
