import assert from 'node:assert/strict';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';

import { createServer } from 'vite';

import { buildCurrentSharedProfileV3 } from '../device-authority/current-v3-profile-fixture.mjs';

const port = 38_000 + (process.pid % 1_000);
const endpoint = `http://127.0.0.1:${port}`;
const client = {
  clientId: '33333333-3333-3333-3333-333333333333',
  clientLabel: 'Rare Participation Audit',
};
const child = spawn(process.execPath, ['scripts/mock-local-api.mjs'], {
  cwd: process.cwd(),
  env: { ...process.env, MOCK_API_PORT: String(port) },
  stdio: ['ignore', 'pipe', 'pipe'],
});
let childOutput = '';
child.stdout.on('data', (chunk) => { childOutput += chunk.toString(); });
child.stderr.on('data', (chunk) => { childOutput += chunk.toString(); });

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true, hmr: false },
  appType: 'custom',
  logLevel: 'error',
});
const originalWindow = Object.getOwnPropertyDescriptor(globalThis, 'window');
const originalLocalStorage = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');

try {
  await waitForServer();
  installBrowserStubs();
  const { updateRareGuestParticipation } = await vite.ssrLoadModule('/src/companion/api.ts');

  const registration = await postJson('/devices/register', 0, {
    protocolVersion: 1,
    profileSchemaVersion: 3,
    platform: 'browser',
    appVersion: 'participation-audit',
    profile: buildCurrentSharedProfileV3({
      automationEnabled: true,
      autoPrepCompleteOrder: true,
      autoPrepStartCooking: true,
      autoPrepCollectCooking: true,
      rareGuestParticipationModuleEnabled: true,
      managedRareGuestIds: [1001],
    }),
  });
  assert.equal(registration.authorityRevision, 1);

  const initialSnapshot = await getJson('/snapshot');
  const initialParticipation = initialSnapshot.rareGuestParticipation;
  assert.equal(initialParticipation.active, true);
  assert.equal(initialParticipation.businessGeneration, 1);
  assert.deepEqual(initialParticipation.managedGuestIds, [1001]);
  assert.deepEqual(readEntry(initialParticipation, 1001), {
    traceId: 'R-0001',
    orderLifecycleSequence: 1,
    guestId: 1001,
    managed: true,
    participating: false,
    reasonCode: 'managed-lifecycle-default-paused',
    queuePosition: null,
  });
  assert.equal(readEntry(initialParticipation, 1002).participating, true);
  assert.equal(readEntry(initialParticipation, 1002).queuePosition, 1);

  const firstIdentity = {
    businessGeneration: 1,
    traceId: 'R-0001',
    orderLifecycleSequence: 1,
    guestId: 1001,
  };
  const queued = await updateRareGuestParticipation(
    endpoint,
    'mock-token',
    {
      expectedAuthorityRevision: registration.authorityRevision,
      expectedBusinessGeneration: initialParticipation.businessGeneration,
      expectedParticipationRevision: initialParticipation.participationRevision,
      action: 'enable-front',
      target: {
        type: 'guest',
        guestId: 1001,
        expectedCurrentOrders: [{ ...firstIdentity, deskCode: 1 }],
      },
      unexpectedTopLevelField: 'must not reach the strict wire body',
    },
  );
  assert.equal(queued.ok, true);
  assert.equal(queued.changed, true);
  assert.equal(queued.error, null);
  assert.equal(queued.status, 'selected rare guest orders queued after active work');
  const firstQueuedEntry = readEntry(queued.participation, 1001);
  assert.equal(firstQueuedEntry.participating, true);
  assert.equal(firstQueuedEntry.queuePosition, 1);
  assert.equal(readEntry(queued.participation, 1002).queuePosition, 2);
  assert.equal(firstQueuedEntry.reasonCode, 'managed-lifecycle-manually-enabled-front');

  const refreshedAfterMutation = await getJson(
    `/snapshot?knownSignature=${encodeURIComponent(initialSnapshot.snapshotSignature)}`,
  );
  assert.equal(refreshedAfterMutation.unchanged, undefined);
  assert.equal(
    refreshedAfterMutation.rareGuestParticipation.participationRevision,
    queued.participation.participationRevision,
    'participation changes must invalidate the mock snapshot signature.',
  );

  const idempotent = await updateRareGuestParticipation(endpoint, 'mock-token', {
    expectedAuthorityRevision: registration.authorityRevision,
    expectedBusinessGeneration: queued.participation.businessGeneration,
    expectedParticipationRevision: queued.participation.participationRevision,
    action: 'enable-front',
    target: {
      type: 'guest',
      guestId: 1001,
      expectedCurrentOrders: [firstIdentity],
    },
  });
  assert.equal(idempotent.changed, false);
  assert.equal(
    idempotent.participation.participationRevision,
    queued.participation.participationRevision,
  );
  assert.equal(readEntry(idempotent.participation, 1001).queuePosition, 1);

  const extraBodyField = await rawPost('/orders/rare/participation', 1, {
    ...mutationBody(idempotent.participation, firstIdentity, 'enable-front'),
    unexpected: true,
  });
  assert.equal(extraBodyField.status, 400);

  const extraIdentityField = await rawPost('/orders/rare/participation', 1, {
    ...mutationBody(idempotent.participation, firstIdentity, 'enable-front'),
    target: {
      type: 'guest',
      guestId: firstIdentity.guestId,
      expectedCurrentOrders: [{ ...firstIdentity, deskCode: 1 }],
    },
  });
  assert.equal(extraIdentityField.status, 400);

  const mismatchedExactSet = await rawPost('/orders/rare/participation', 1, {
    ...mutationBody(idempotent.participation, firstIdentity, 'pause'),
    target: {
      type: 'guest',
      guestId: firstIdentity.guestId,
      expectedCurrentOrders: [{
        ...firstIdentity,
        traceId: 'R-9999',
        orderLifecycleSequence: 9999,
      }],
    },
  });
  assert.equal(mismatchedExactSet.status, 409);

  const staleRevision = await rawPost('/orders/rare/participation', 1, {
    ...mutationBody(idempotent.participation, firstIdentity, 'pause'),
    expectedParticipationRevision: idempotent.participation.participationRevision - 1,
  });
  assert.equal(staleRevision.status, 409);

  const oversizedSet = await rawPost('/orders/rare/participation', 1, {
    ...mutationBody(idempotent.participation, firstIdentity, 'pause'),
    target: {
      type: 'guest',
      guestId: firstIdentity.guestId,
      expectedCurrentOrders: Array.from({ length: 513 }, (_, index) => ({
        ...firstIdentity,
        traceId: `R-${index + 1}`,
        orderLifecycleSequence: index + 1,
      })),
    },
  });
  assert.equal(oversizedSet.status, 400);

  const lease = await postJson('/automation/lease/acquire', registration.authorityRevision, {});
  assert.equal(lease.owned, true);
  const firstJobAction = await postJson(
    '/orders/prepare-next?orderLifecycleSequence=1&traceId=R-0001&guestId=1001&deskCode=1',
    registration.authorityRevision,
    {},
  );
  const secondJobAction = await postJson(
    '/orders/prepare-next?orderLifecycleSequence=2&traceId=R-0002&guestId=1002&deskCode=3',
    registration.authorityRevision,
    {},
  );
  assert.notEqual(firstJobAction.automation.jobId, secondJobAction.automation.jobId);
  const concurrentJobs = (await getJson('/snapshot')).automationCookingJobs;
  assert.equal(concurrentJobs.length, 2);
  assert.deepEqual(
    concurrentJobs.map((job) => [
      job.jobId,
      job.traceId,
      job.orderLifecycleSequence,
      job.guestId,
      job.controlState,
    ]),
    [
      [firstJobAction.automation.jobId, 'R-0001', 1, 1001, 'active'],
      [secondJobAction.automation.jobId, 'R-0002', 2, 1002, 'active'],
    ],
    'The mock must retain independent exact active jobs instead of replacing the first job.',
  );

  const duplicateFirstJobAction = await postJson(
    '/orders/prepare-next?orderLifecycleSequence=1&traceId=R-0001&guestId=1001&deskCode=1',
    registration.authorityRevision,
    {},
  );
  assert.equal(duplicateFirstJobAction.automation.jobId, firstJobAction.automation.jobId);
  assert.equal(
    (await getJson('/snapshot')).automationCookingJobs.length,
    2,
    'Repeating the same exact target must not create a second active job identity.',
  );

  const paused = await updateRareGuestParticipation(endpoint, 'mock-token', {
    expectedAuthorityRevision: registration.authorityRevision,
    expectedBusinessGeneration: idempotent.participation.businessGeneration,
    expectedParticipationRevision: idempotent.participation.participationRevision,
    action: 'pause',
    target: { type: 'order', order: firstIdentity },
  });
  assert.equal(paused.changed, true);
  assert.equal(readEntry(paused.participation, 1001).queuePosition, null);
  assert.equal(readEntry(paused.participation, 1002).queuePosition, 1);

  const frontQueuedAfterPausedJob = await updateRareGuestParticipation(endpoint, 'mock-token', {
    expectedAuthorityRevision: registration.authorityRevision,
    expectedBusinessGeneration: paused.participation.businessGeneration,
    expectedParticipationRevision: paused.participation.participationRevision,
    action: 'enable-front',
    target: { type: 'order', order: firstIdentity },
  });
  assert.equal(readEntry(frontQueuedAfterPausedJob.participation, 1002).queuePosition, 1);
  assert.equal(readEntry(frontQueuedAfterPausedJob.participation, 1001).queuePosition, 2);

  const pausedAgain = await updateRareGuestParticipation(endpoint, 'mock-token', {
    expectedAuthorityRevision: registration.authorityRevision,
    expectedBusinessGeneration: frontQueuedAfterPausedJob.participation.businessGeneration,
    expectedParticipationRevision: frontQueuedAfterPausedJob.participation.participationRevision,
    action: 'pause',
    target: { type: 'order', order: firstIdentity },
  });
  const requeued = await updateRareGuestParticipation(endpoint, 'mock-token', {
    expectedAuthorityRevision: registration.authorityRevision,
    expectedBusinessGeneration: pausedAgain.participation.businessGeneration,
    expectedParticipationRevision: pausedAgain.participation.participationRevision,
    action: 'enable-tail',
    target: { type: 'order', order: firstIdentity },
  });
  assert.equal(readEntry(requeued.participation, 1001).queuePosition, 2);
  assert.deepEqual(
    requeued.participation.entries.map((entry) => entry.guestId),
    [1002, 1001],
    're-enabled managed lifecycle must move behind the existing automatic queue entry.',
  );

  await postJson(
    '/orders/prepare-next?orderLifecycleSequence=999&traceId=R-9999&guestId=1002&deskCode=4',
    registration.authorityRevision,
    {},
  );
  const pausedBeforeMissingAnchor = await updateRareGuestParticipation(
    endpoint,
    'mock-token',
    {
      expectedAuthorityRevision: registration.authorityRevision,
      expectedBusinessGeneration: requeued.participation.businessGeneration,
      expectedParticipationRevision: requeued.participation.participationRevision,
      action: 'pause',
      target: { type: 'order', order: firstIdentity },
    },
  );
  const missingActiveAnchor = await rawPost(
    '/orders/rare/participation',
    registration.authorityRevision,
    {
      expectedAuthorityRevision: registration.authorityRevision,
      expectedBusinessGeneration: pausedBeforeMissingAnchor.participation.businessGeneration,
      expectedParticipationRevision:
        pausedBeforeMissingAnchor.participation.participationRevision,
      action: 'enable-front',
      target: { type: 'order', order: firstIdentity },
    },
  );
  assert.equal(missingActiveAnchor.status, 409);
  assert.match(
    (await missingActiveAnchor.json()).error,
    /is not one exact current participation entry/u,
  );

  const updatedProfile = await postJson('/devices/profile', 0, {
    protocolVersion: 1,
    profileSchemaVersion: 3,
    expectedAuthorityRevision: registration.authorityRevision,
    expectedProfileRevision: registration.currentDeviceProfileRevision,
    profile: buildCurrentSharedProfileV3({
      rareGuestParticipationModuleEnabled: true,
      managedRareGuestIds: [1002],
    }),
  });
  assert.equal(updatedProfile.authorityRevision, 2);
  const profileAlignedSnapshot = await getJson('/snapshot');
  const aligned = profileAlignedSnapshot.rareGuestParticipation;
  assert.deepEqual(aligned.managedGuestIds, [1002]);
  assert.equal(readEntry(aligned, 1002).participating, false);
  assert.equal(readEntry(aligned, 1002).reasonCode, 'managed-lifecycle-default-paused');
  assert.equal(readEntry(aligned, 1001).managed, false);
  assert.equal(readEntry(aligned, 1001).participating, true);
  assert.equal(readEntry(aligned, 1001).queuePosition, 1);

  const disabledProfile = await postJson('/devices/profile', 0, {
    protocolVersion: 1,
    profileSchemaVersion: 3,
    expectedAuthorityRevision: updatedProfile.authorityRevision,
    expectedProfileRevision: updatedProfile.currentDeviceProfileRevision,
    profile: buildCurrentSharedProfileV3({
      rareGuestParticipationModuleEnabled: false,
      managedRareGuestIds: [1002],
    }),
  });
  assert.equal(disabledProfile.authorityRevision, 3);
  const moduleDisabledSnapshot = await getJson('/snapshot');
  const moduleDisabled = moduleDisabledSnapshot.rareGuestParticipation;
  assert.deepEqual(moduleDisabled.managedGuestIds, []);
  assert.equal(readEntry(moduleDisabled, 1001).participating, true);
  assert.equal(readEntry(moduleDisabled, 1002).participating, true);
  const disabledMutation = await rawPost(
    '/orders/rare/participation',
    disabledProfile.authorityRevision,
    {
      expectedAuthorityRevision: disabledProfile.authorityRevision,
      expectedBusinessGeneration: moduleDisabled.businessGeneration,
      expectedParticipationRevision: moduleDisabled.participationRevision,
      action: 'pause',
      target: {
        type: 'order',
        order: {
          businessGeneration: 1,
          traceId: 'R-0002',
          orderLifecycleSequence: 2,
          guestId: 1002,
        },
      },
    },
  );
  assert.equal(disabledMutation.status, 409);
  assert.match((await disabledMutation.json()).error, /稀客调度模块已关闭/u);

  const secondary = {
    clientId: '44444444-4444-4444-4444-444444444444',
    clientLabel: 'Rare Participation Secondary',
  };
  const secondaryRegistration = await postJson('/devices/register', 0, {
    protocolVersion: 1,
    profileSchemaVersion: 3,
    platform: 'android',
    appVersion: 'participation-audit',
    profile: buildCurrentSharedProfileV3({
      rareGuestParticipationModuleEnabled: true,
      managedRareGuestIds: [1001],
    }),
  }, secondary);
  const switched = await postJson('/devices/primary', 0, {
    protocolVersion: 1,
    expectedAuthorityRevision: secondaryRegistration.authorityRevision,
    deviceId: secondary.clientId,
  });
  assert.equal(switched.authorityRevision, 4);
  const authorityResetSnapshot = await getJson('/snapshot');
  const authorityReset = authorityResetSnapshot.rareGuestParticipation;
  assert.deepEqual(authorityReset.managedGuestIds, [1001]);
  assert.equal(readEntry(authorityReset, 1001).participating, false);
  assert.equal(
    readEntry(authorityReset, 1001).reasonCode,
    'managed-lifecycle-authority-reset-paused',
  );
  assert.equal(readEntry(authorityReset, 1002).participating, true);
  assert.equal(readEntry(authorityReset, 1002).queuePosition, 1);

  await verifySourceContract();
  console.log(
    'PASS: frontend rare-participation API emits strict discriminated guest/order targets; the bounded mock preserves exact sets, multiple active jobs, paused-job exclusion, fail-closed anchors, front enable, and queue-tail re-enable semantics.',
  );
} finally {
  restoreGlobal('window', originalWindow);
  restoreGlobal('localStorage', originalLocalStorage);
  await vite.close();
  child.kill('SIGTERM');
  if (child.exitCode === null) await once(child, 'exit');
}

function installBrowserStubs() {
  const values = new Map([
    ['mystia-steward-companion-client-id', client.clientId],
  ]);
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: {
      getItem: (key) => values.get(key) ?? null,
      setItem: (key, value) => values.set(key, String(value)),
      removeItem: (key) => values.delete(key),
    },
  });
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: {
      setTimeout: globalThis.setTimeout.bind(globalThis),
      clearTimeout: globalThis.clearTimeout.bind(globalThis),
    },
  });
}

function restoreGlobal(name, descriptor) {
  if (descriptor) Object.defineProperty(globalThis, name, descriptor);
  else delete globalThis[name];
}

function mutationBody(participation, identity, action) {
  return {
    expectedAuthorityRevision: 1,
    expectedBusinessGeneration: participation.businessGeneration,
    expectedParticipationRevision: participation.participationRevision,
    action,
    target: {
      type: 'guest',
      guestId: identity.guestId,
      expectedCurrentOrders: [identity],
    },
  };
}

function readEntry(participation, guestId) {
  const entry = participation.entries.find((candidate) => candidate.guestId === guestId);
  assert.ok(entry, `Missing participation entry for guest ${guestId}.`);
  return entry;
}

async function waitForServer() {
  const deadline = Date.now() + 5_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`Mock server exited early.\n${childOutput}`);
    try {
      const response = await fetch(`${endpoint}/health`);
      if (response.ok) return;
    } catch {
      // Startup race; retry until the deadline.
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(`Mock server did not become ready.\n${childOutput}`);
}

async function getJson(path) {
  const response = await fetch(`${endpoint}${path}`);
  assert.equal(response.status, 200);
  return response.json();
}

async function postJson(path, authorityRevision, body, identity = client) {
  const response = await rawPost(path, authorityRevision, body, identity);
  const payload = await response.json();
  if (!response.ok) {
    throw new Error(`${path} failed with HTTP ${response.status}: ${JSON.stringify(payload)}`);
  }
  return payload;
}

function rawPost(path, authorityRevision, body, identity = client) {
  return fetch(`${endpoint}${path}`, {
    method: 'POST',
    headers: {
      'X-Mystia-Steward-Companion-Token': 'mock-token',
      'X-Mystia-Steward-Companion-Client-Id': identity.clientId,
      'X-Mystia-Steward-Companion-Client-Label': identity.clientLabel,
      ...(authorityRevision > 0
        ? { 'X-Mystia-Steward-Companion-Authority-Revision': String(authorityRevision) }
        : {}),
      'Content-Type': 'application/json; charset=utf-8',
    },
    body: JSON.stringify(body),
  });
}

async function verifySourceContract() {
  const [typesSource, apiSource, mockSource] = await Promise.all([
    readFile('apps/companion/src/companion/types.ts', 'utf8'),
    readFile('apps/companion/src/companion/api.ts', 'utf8'),
    readFile('scripts/mock-local-api.mjs', 'utf8'),
  ]);
  assert.ok(typesSource.includes('rareGuestParticipation: RareGuestParticipationSnapshot;'));
  assert.ok(typesSource.includes('export interface RareGuestParticipationMutationRequest'));
  assert.ok(apiSource.includes("'/orders/rare/participation'"));
  assert.ok(apiSource.includes('authorityRevision: request.expectedAuthorityRevision'));
  assert.ok(apiSource.includes('expectedBusinessGeneration: request.expectedBusinessGeneration'));
  assert.ok(apiSource.includes('expectedParticipationRevision: request.expectedParticipationRevision'));
  assert.ok(apiSource.includes('action: request.action'));
  assert.ok(apiSource.includes("request.target.type === 'guest'"));
  assert.ok(typesSource.includes("| 'enable-front';"));
  assert.ok(typesSource.includes("type: 'order';"));
  const mutationRequestType = typesSource.match(
    /export interface RareGuestParticipationMutationRequest \{[\s\S]*?\n\}/,
  )?.[0] ?? '';
  assert.ok(mutationRequestType.includes('action: RareGuestParticipationMutationAction;'));
  assert.ok(mutationRequestType.includes('target: RareGuestParticipationMutationTarget;'));
  assert.ok(!mutationRequestType.includes('enabled:'), '旧 enabled bool 不得保留为兼容 mutation 路径。');
  assert.ok(!typesSource.includes('RareOrderDismissResponse'), 'removed dismiss DTO must not remain in frontend types.');
  assert.ok(!apiSource.includes('/orders/rare/dismiss'), 'removed dismiss endpoint must not remain in the frontend API.');
  assert.ok(!mockSource.includes('/orders/rare/dismiss'), 'removed dismiss endpoint must not remain in the mock API.');
  assert.ok(mockSource.includes('alignMockRareGuestParticipationToPrimaryProfile(true)'));
  assert.ok(mockSource.includes('expectedCurrentOrders.length > 512'));
  assert.ok(mockSource.includes('buildMockRareGuestProtectedQueueKeys('));
  assert.ok(mockSource.includes('automationCookingJobs.push(job)'));
  assert.ok(!mockSource.includes('automationCookingJobs = [buildMockAutomationCookingJob'));
  assert.ok(mockSource.includes('mockAutomationCookingJobMetadata.get(job.jobId)'));
  assert.ok(mockSource.includes('currentEntry.queuePosition === null'));
  assert.ok(mockSource.includes('has no exact current-generation identity'));
  assert.ok(mockSource.includes('is not one exact current participation entry'));
  assert.match(
    mockSource,
    /const insertionIndex = protectedIndexes\.length === 0[\s\S]+Math\.max\(\.\.\.protectedIndexes\) \+ 1;[\s\S]+currentQueue\.slice\(0, insertionIndex\)[\s\S]+eligibleTargets[\s\S]+currentQueue\.slice\(insertionIndex\)/u,
    'Mock front insertion must preserve the existing queue and insert after its last protected position.',
  );
  const moduleGate = mockSource.indexOf('rareGuestParticipationModuleEnabled !== true');
  const commandFence = mockSource.indexOf('automationCommandEpoch += 1', moduleGate);
  assert.ok(moduleGate >= 0 && commandFence > moduleGate);
}
