import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  buildRareGuestInvitationRefreshIdentity,
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
  normalizedEndpoint: 'http://127.0.0.1:32145',
  scope: 'current',
  snapshot: baseSnapshot,
  tab: 'rare-invitations',
};

const identity = buildRareGuestInvitationRefreshIdentity(baseContext);
assert.ok(identity, 'A stable day scene must produce an invitation refresh identity.');

for (const context of [
  { ...baseContext, connected: false },
  { ...baseContext, tab: 'overview' },
  { ...baseContext, snapshot: null },
  { ...baseContext, snapshot: { ...baseSnapshot, runtimeLoaded: false } },
  { ...baseContext, snapshot: { ...baseSnapshot, runtimeDaySceneReady: false } },
  { ...baseContext, snapshot: { ...baseSnapshot, runtimeDaySceneGeneration: 0 } },
  { ...baseContext, snapshot: { ...baseSnapshot, activeDayMapLabel: '' } },
]) {
  assert.equal(
    buildRareGuestInvitationRefreshIdentity(context),
    null,
    'An unsafe or inactive context must not produce a passive invitation read.',
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
    buildRareGuestInvitationRefreshIdentity(context),
    identity,
    'Every state boundary that can stale a candidate list must change its refresh identity.',
  );
}

assert.equal(
  buildRareGuestInvitationRefreshIdentity({
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
const [apiSource, hookSource, workbenchSource, panelSource, mockSource] = await Promise.all([
  readFile(new URL('apps/companion/src/companion/api.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/hooks/useRareGuestInvitations.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/pages/ModRareGuestInvitationsPanel.tsx', root), 'utf8'),
  readFile(new URL('scripts/mock-local-api.mjs', root), 'utf8'),
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
  'buildRareGuestInvitationRefreshIdentity',
  'requestGenerationRef',
  'listAbortControllerRef',
  'attemptedListIdentityRef',
  'latestIdentityRef.current !== identity',
  'scheduleTransientListRetry(identity)',
  'clearTransientListRetry()',
  'if (!response.ok)',
  "getRareGuestInvitationError(response, '读取稀客邀请候选失败。')",
  "getRareGuestInvitationError(response, '批量邀请稀客失败。')",
  "getRareGuestInvitationError(response, '邀请稀客失败。')",
  'setRareGuestInvitationResult(null)',
  'rareGuestInvitationContextReady: refreshIdentity !== null',
  'const writeContext = useMemo<RareGuestInvitationWriteContext | null>',
  'expectedDaySceneGeneration: snapshot.runtimeDaySceneGeneration',
  'expectedMapLabel,',
  'rareGuestInvitationLevels,\n        writeContext,',
  'rareGuestInvitationScope,\n        writeContext,',
]) {
  assert.ok(hookSource.includes(contract), `Invitation Hook is missing stale-request contract: ${contract}`);
}
assert.ok(workbenchSource.includes('connectionRevision,'));
assert.ok(workbenchSource.includes('runtimeDaySceneReady={snapshot?.runtimeDaySceneReady ?? false}'));
assert.ok(workbenchSource.includes('invitationContextReady={rareGuestInvitationContextReady}'));
assert.ok(panelSource.includes('{inviteAllError && <EmptyRow text={inviteAllError} />}'));
assert.ok(panelSource.includes(') : !inviteAllError && ('));
assert.ok(panelSource.includes('const busy = inviteBusyKey === `guest:${entry.id}`;'));
assert.ok(panelSource.includes('onClick={() => onInviteRareGuest(entry.id)}'));
assert.ok(panelSource.includes('const key = entry.id >= 0'));
assert.ok(mockSource.includes("invitation(10, '雾雨魔理沙'"));
assert.ok(mockSource.includes("'DLC1_Marisa'"));

const postBranch = sourceSlice(mockSource, "if (request.method === 'POST')", "if (request.method !== 'GET')");
const getBranch = sourceSlice(mockSource, "if (request.method !== 'GET')", 'server.listen');
assert.doesNotMatch(postBranch, /path === '\/rare-guests\/invitations'/);
assert.match(getBranch, /path === '\/rare-guests\/invitations'/);

console.log(
  'PASS: rare-guest invitation reads use day-scene identities, GET-only transport, '
  + 'bounded transient-readiness retries, stale-response isolation, and guarded write contexts.',
);

function sourceSlice(source, startText, endText) {
  const start = source.indexOf(startText);
  const end = source.indexOf(endText, start + startText.length);
  assert.ok(start >= 0, `Source boundary not found: ${startText}`);
  assert.ok(end > start, `Source boundary not found: ${endText}`);
  return source.slice(start, end);
}
