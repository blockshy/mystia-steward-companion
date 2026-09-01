import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { createServer } from 'vite';

import {
  buildCurrentSharedProfileV3 as buildSharedProfile,
  hashCurrentSharedProfileV3,
} from './current-v3-profile-fixture.mjs';

const port = 35_000 + (process.pid % 1_000);
const endpoint = `http://127.0.0.1:${port}`;
const server = spawn(process.execPath, ['scripts/mock-local-api.mjs'], {
  cwd: process.cwd(),
  env: { ...process.env, MOCK_API_PORT: String(port) },
  stdio: ['ignore', 'pipe', 'pipe'],
});
let serverOutput = '';
server.stdout.on('data', (chunk) => { serverOutput += chunk.toString(); });
server.stderr.on('data', (chunk) => { serverOutput += chunk.toString(); });

const windows = identity('11111111-1111-1111-1111-111111111111', 'Windows companion');
const android = identity('22222222-2222-2222-2222-222222222222', 'Android companion');
const WINDOWS_PROFILE_V3_SHA256 = '9ea1c1f88b600dd853763bf48a18cfc594b8c7de025ea2e045f2032e2cb0cfc5';
const UPDATED_PROFILE_V3_SHA256 = '7e07975be90ec571d71264170a72fed80995ea56bd8633ed76ebe60dd7b96d50';

try {
  await verifySharedProfileContract();
  await verifyFrontendSharedProfileBoundaries();
  await waitForServer();
  const windowsProfile = buildSharedProfile({ automationEnabled: true, autoRareConcurrency: 2 });
  const androidProfile = buildSharedProfile({ automationEnabled: false, autoRareConcurrency: 3 });

  const rejectedPreviousWireSchema = await rawPost('/devices/register', windows, {
    protocolVersion: 1,
    profileSchemaVersion: 2,
    platform: 'windows',
    appVersion: '1.2.0',
    profile: windowsProfile,
  });
  assert.equal(rejectedPreviousWireSchema.status, 409);

  const missingFieldProfile = structuredClone(windowsProfile);
  delete missingFieldProfile.rareGuestParticipationModuleEnabled;
  const invalidCurrentProfiles = [
    ['missing field', missingFieldProfile],
    ['unexpected field', { ...windowsProfile, unexpectedField: true }],
    ['non-boolean field', { ...windowsProfile, automationEnabled: 'true' }],
    ['out-of-range integer', { ...windowsProfile, autoRareConcurrency: 5 }],
    ['non-canonical color', { ...windowsProfile, rareTargetHighlightColor: '#ffDB2E' }],
    [
      'nested unexpected field',
      {
        ...windowsProfile,
        recommendationSortProfile: {
          ...windowsProfile.recommendationSortProfile,
          unexpectedField: true,
        },
      },
    ],
    [
      'invalid delivery dependency',
      {
        ...windowsProfile,
        autoNormalCompleteOrder: false,
        autoNormalDeliverFood: true,
      },
    ],
    [
      'non-canonical exclusion IDs',
      {
        ...windowsProfile,
        recommendationExclusions: {
          excludedIngredientIds: [3, 3],
          excludedBeverageIds: [],
        },
      },
    ],
  ];
  for (const [label, profile] of invalidCurrentProfiles) {
    const rejected = await rawPost('/devices/register', windows, {
      protocolVersion: 1,
      profileSchemaVersion: 3,
      platform: 'windows',
      appVersion: '1.2.0',
      profile,
    });
    assert.equal(rejected.status, 400, `Mock accepted a current v3 profile with ${label}.`);
  }

  const first = await postJson('/devices/register', windows, {
    protocolVersion: 1,
    profileSchemaVersion: 3,
    platform: 'windows',
    appVersion: '1.2.0',
    profile: windowsProfile,
  });
  assert.equal(first.currentDeviceIsPrimary, true);
  assert.equal(first.authorityRevision, 1);
  assert.equal(first.devices.length, 1);
  assert.equal(
    hashCurrentSharedProfileV3(windowsProfile),
    WINDOWS_PROFILE_V3_SHA256,
    'The exact current-v3 fixture changed without refreshing its canonical hash golden.',
  );
  assert.equal(first.activeProfileHash, WINDOWS_PROFILE_V3_SHA256);
  assert.equal(first.currentDeviceProfileHash, first.activeProfileHash);

  const second = await postJson('/devices/register', android, {
    protocolVersion: 1,
    profileSchemaVersion: 3,
    platform: 'android',
    appVersion: '1.2.0',
    profile: androidProfile,
  });
  assert.equal(second.currentDeviceIsPrimary, false);
  assert.equal(second.activeProfile.automationEnabled, true);
  assert.equal(second.currentDeviceProfile.automationEnabled, false);

  const forbiddenProfileWrite = await rawPost('/devices/profile', android, {
    protocolVersion: 1,
    profileSchemaVersion: 3,
    expectedAuthorityRevision: second.authorityRevision,
    expectedProfileRevision: second.currentDeviceProfileRevision,
    profile: androidProfile,
  });
  assert.equal(forbiddenProfileWrite.status, 403);

  const secondaryLease = await postWithoutBody(
    '/automation/lease/acquire',
    android,
    second.authorityRevision,
  );
  assert.equal(secondaryLease.ok, false);
  assert.match(secondaryLease.error, /不是主设备/);

  const rejectedNonCanonicalProfile = await rawPost('/devices/profile', windows, {
    protocolVersion: 1,
    profileSchemaVersion: 3,
    expectedAuthorityRevision: second.authorityRevision,
    expectedProfileRevision: first.currentDeviceProfileRevision,
    profile: buildSharedProfile({ managedRareGuestIds: [8, 3] }),
  });
  assert.equal(rejectedNonCanonicalProfile.status, 400);

  const updatedProfile = buildSharedProfile({
    automationEnabled: true,
    autoRareConcurrency: 4,
    pinFavoriteRecipeEnabled: true,
    rareGuestParticipationModuleEnabled: true,
    managedRareGuestIds: [3, 8],
  });
  const updated = await postJson('/devices/profile', windows, {
    protocolVersion: 1,
    profileSchemaVersion: 3,
    expectedAuthorityRevision: first.authorityRevision,
    expectedProfileRevision: first.currentDeviceProfileRevision,
    profile: updatedProfile,
  });
  assert.equal(updated.authorityRevision, 2);
  assert.equal(updated.activeProfile.autoRareConcurrency, 4);
  assert.equal(updated.activeProfile.rareGuestParticipationModuleEnabled, true);
  assert.deepEqual(updated.activeProfile.managedRareGuestIds, [3, 8]);
  assert.equal(
    hashCurrentSharedProfileV3(updatedProfile),
    UPDATED_PROFILE_V3_SHA256,
    'The updated current-v3 fixture changed without refreshing its canonical hash golden.',
  );
  assert.equal(updated.activeProfileHash, UPDATED_PROFILE_V3_SHA256);

  const stalePrimaryLease = await postWithoutBody('/automation/lease/acquire', windows, 1);
  assert.equal(stalePrimaryLease.ok, false);
  assert.match(stalePrimaryLease.error, /权威版本/);
  const currentPrimaryLease = await postWithoutBody(
    '/automation/lease/acquire',
    windows,
    updated.authorityRevision,
  );
  assert.equal(currentPrimaryLease.ok, true);
  assert.equal(currentPrimaryLease.owned, true);

  const pending = await postJson('/devices/sync', android, {
    protocolVersion: 1,
    expectedAuthorityRevision: updated.authorityRevision,
    deviceId: android.clientId,
  });
  assert.ok(pending.pendingSyncId);
  assert.equal(pending.currentDeviceProfileHash, pending.activeProfileHash);
  assert.equal(pending.devices.find((device) => device.isCurrent)?.syncPending, true);

  const acknowledged = await postJson('/devices/sync-ack', android, {
    protocolVersion: 1,
    syncId: pending.pendingSyncId,
    profileRevision: pending.currentDeviceProfileRevision,
    profileHash: pending.currentDeviceProfileHash,
  });
  assert.equal(acknowledged.pendingSyncId, null);
  assert.equal(acknowledged.currentDeviceProfileHash, acknowledged.activeProfileHash);

  const switched = await postJson('/devices/primary', windows, {
    protocolVersion: 1,
    expectedAuthorityRevision: acknowledged.authorityRevision,
    deviceId: android.clientId,
  });
  assert.equal(switched.primaryDeviceId, android.clientId);
  assert.equal(switched.currentDeviceIsPrimary, false);
  assert.equal(switched.authorityRevision, 3);

  const formerPrimaryLease = await postWithoutBody(
    '/automation/lease/acquire',
    windows,
    switched.authorityRevision,
  );
  assert.equal(formerPrimaryLease.ok, false);
  assert.match(formerPrimaryLease.error, /不是主设备/);
  const newPrimaryLease = await postWithoutBody(
    '/automation/lease/acquire',
    android,
    switched.authorityRevision,
  );
  assert.equal(newPrimaryLease.ok, true);
  assert.equal(newPrimaryLease.owned, true);

  const renamed = await postJson('/devices/rename', android, {
    protocolVersion: 1,
    label: 'Android 主设备',
  });
  assert.equal(renamed.devices.find((device) => device.isCurrent)?.label, 'Android 主设备');

  console.log('PASS: device authority registration, profile CAS, sync acknowledgement, primary transfer and stale-writer fencing are coherent.');
} finally {
  server.kill('SIGTERM');
  await Promise.race([
    new Promise((resolve) => server.once('exit', resolve)),
    new Promise((resolve) => setTimeout(resolve, 2_000)),
  ]);
}

function identity(clientId, clientLabel) {
  return { clientId, clientLabel };
}

async function waitForServer() {
  const deadline = Date.now() + 5_000;
  while (Date.now() < deadline) {
    if (server.exitCode !== null) throw new Error(`Mock server exited early.\n${serverOutput}`);
    try {
      const response = await fetch(`${endpoint}/health`);
      if (response.ok) return;
    } catch {
      // Startup race; retry until the deadline.
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(`Mock server did not become ready.\n${serverOutput}`);
}

async function postJson(path, client, body) {
  const response = await rawPost(path, client, body);
  const payload = await response.json();
  if (!response.ok) throw new Error(`${path} failed with HTTP ${response.status}: ${JSON.stringify(payload)}`);
  return payload;
}

function rawPost(path, client, body) {
  return fetch(`${endpoint}${path}`, {
    method: 'POST',
    headers: requestHeaders(client, 0, true),
    body: JSON.stringify(body),
  });
}

async function postWithoutBody(path, client, authorityRevision) {
  const response = await fetch(`${endpoint}${path}`, {
    method: 'POST',
    headers: requestHeaders(client, authorityRevision, false),
  });
  assert.equal(response.status, 200);
  return response.json();
}

function requestHeaders(client, authorityRevision, json) {
  return {
    'X-Mystia-Steward-Companion-Token': 'mock-token',
    'X-Mystia-Steward-Companion-Client-Id': client.clientId,
    'X-Mystia-Steward-Companion-Client-Label': client.clientLabel,
    ...(authorityRevision > 0
      ? { 'X-Mystia-Steward-Companion-Authority-Revision': String(authorityRevision) }
      : {}),
    ...(json ? { 'Content-Type': 'application/json; charset=utf-8' } : {}),
  };
}

async function verifyFrontendSharedProfileBoundaries() {
  const vite = await createServer({
    configFile: 'apps/companion/vite.config.ts',
    server: { middlewareMode: true, hmr: false },
    appType: 'custom',
  });
  try {
    const {
      normalizeManagedRareGuestIds,
      normalizeSharedCompanionPreferences,
      parseSharedCompanionPreferences,
      serializeSharedCompanionPreferences,
    } = await vite.ssrLoadModule('/src/companion/preferences.ts');
    const {
      resolveLocalExtensionModuleControl,
      resolvePrimaryExtensionModuleControl,
    } = await vite.ssrLoadModule('/src/companion/domain/extension-module-control.ts');
    const {
      capturePrimaryProfileTransactionBase,
      resolvePrimaryProfileObservation,
    } = await vite.ssrLoadModule('/src/companion/domain/primary-profile-transaction.ts');
    assert.deepEqual(normalizeManagedRareGuestIds([9, 3, 9, 0]), [0, 3, 9]);
    assert.deepEqual(normalizeManagedRareGuestIds('3'), []);
    const bounded = normalizeManagedRareGuestIds([
      ...Array.from({ length: 520 }, (_, index) => 519 - index),
      -1,
      1.5,
      '4',
      2_147_483_648,
    ]);
    assert.equal(bounded.length, 512);
    assert.equal(bounded[0], 0);
    assert.equal(bounded.at(-1), 511);

    const validProfile = buildSharedProfile({
      rareGuestParticipationModuleEnabled: true,
      managedRareGuestIds: [3, 8],
      autoNormalCompleteOrder: true,
      autoNormalTakeBeverage: true,
      autoNormalDeliverFood: true,
      autoPrepCompleteOrder: true,
      autoPrepTakeBeverage: true,
      autoPrepCollectCooking: true,
      recommendationExclusions: {
        excludedIngredientIds: [1, 9],
        excludedBeverageIds: [2, 10],
      },
    });
    assert.deepEqual(
      parseSharedCompanionPreferences(validProfile),
      validProfile,
      'A complete canonical v3 profile must pass strict wire parsing unchanged.',
    );

    const v2ProfileMasqueradingAsV3 = structuredClone(validProfile);
    delete v2ProfileMasqueradingAsV3.rareGuestParticipationModuleEnabled;
    assert.throws(
      () => parseSharedCompanionPreferences(v2ProfileMasqueradingAsV3),
      /wire schema/,
      'A v2 profile shape must not be completed with the v3 module default.',
    );
    assert.throws(
      () => parseSharedCompanionPreferences({ ...validProfile, unexpectedField: false }),
      /wire schema/,
      'Unknown wire fields must be rejected instead of discarded.',
    );
    assert.throws(
      () => parseSharedCompanionPreferences({
        ...validProfile,
        rareGuestParticipationModuleEnabled: 'false',
      }),
      /rareGuestParticipationModuleEnabled.*布尔值/,
      'The module flag must not use truthy local-preference coercion on the wire.',
    );

    for (const [label, invalidProfile] of [
      ['integer range', { ...validProfile, autoRareConcurrency: 5 }],
      ['integer type', { ...validProfile, autoMaxRollbacks: 1.5 }],
      ['enum', { ...validProfile, recommendationBudgetPolicy: 'fallback' }],
      ['canonical color', { ...validProfile, rareTargetHighlightColor: '#ffdb2e' }],
      ['managed ID order', { ...validProfile, managedRareGuestIds: [8, 3] }],
      ['exclusion ID uniqueness', {
        ...validProfile,
        recommendationExclusions: {
          ...validProfile.recommendationExclusions,
          excludedIngredientIds: [1, 1],
        },
      }],
      ['normal completion dependency', {
        ...validProfile,
        autoNormalCompleteOrder: false,
        autoNormalTakeBeverage: true,
      }],
      ['rare completion dependency', {
        ...validProfile,
        autoPrepCompleteOrder: false,
        autoPrepCollectCooking: true,
      }],
    ]) {
      assert.throws(
        () => parseSharedCompanionPreferences(invalidProfile),
        undefined,
        `Strict wire parsing accepted invalid ${label}.`,
      );
    }

    const nestedExtraField = structuredClone(validProfile);
    nestedExtraField.recommendationSortProfile.objectives[0].legacyWeight = 10;
    assert.throws(() => parseSharedCompanionPreferences(nestedExtraField), /wire schema/);
    const duplicateObjective = structuredClone(validProfile);
    duplicateObjective.recommendationSortProfile.objectives[1].key = 'foodPreference';
    assert.throws(() => parseSharedCompanionPreferences(duplicateObjective), /不得重复/);

    assert.equal(
      normalizeSharedCompanionPreferences({}).rareGuestParticipationModuleEnabled,
      false,
      'Local preference normalization must remain tolerant and defaulted.',
    );

    assert.deepEqual(
      pickModuleControl(resolveLocalExtensionModuleControl({ enabled: true, connected: false })),
      {
        scope: 'local-client',
        status: 'disconnected',
        enabled: true,
        writable: true,
        pending: false,
        scopeLabel: '当前设备',
      },
      'A disconnected local module must remain writable without claiming a runtime mutation.',
    );
    assert.deepEqual(
      pickModuleControl(resolveLocalExtensionModuleControl({
        enabled: true,
        connected: true,
        operationInFlight: true,
      })),
      {
        scope: 'local-client',
        status: 'operation-in-flight',
        enabled: true,
        writable: false,
        pending: true,
        scopeLabel: '当前设备',
      },
      'An accepted local-module write must remain locked until its outcome is known.',
    );

    const primaryCases = [
      ['disconnected', {
        enabled: true,
        connected: false,
        authorityReady: false,
        currentDeviceIsPrimary: false,
      }],
      ['saving', {
        enabled: true,
        connected: true,
        authorityReady: false,
        currentDeviceIsPrimary: false,
        profileUpdatePending: true,
      }],
      ['waiting-authority', {
        enabled: true,
        connected: true,
        authorityReady: false,
        currentDeviceIsPrimary: false,
      }],
      ['secondary-read-only', {
        enabled: true,
        connected: true,
        authorityReady: true,
        currentDeviceIsPrimary: false,
        primaryDeviceLabel: '主窗口',
      }],
      ['operation-in-flight', {
        enabled: true,
        connected: true,
        authorityReady: true,
        currentDeviceIsPrimary: true,
        operationInFlight: true,
      }],
      ['authority-busy', {
        enabled: true,
        connected: true,
        authorityReady: true,
        currentDeviceIsPrimary: true,
        authorityBusy: true,
      }],
      ['writable', {
        enabled: true,
        connected: true,
        authorityReady: true,
        currentDeviceIsPrimary: true,
      }],
    ];
    for (const [expectedStatus, input] of primaryCases) {
      const control = resolvePrimaryExtensionModuleControl(input);
      assert.equal(control.scope, 'primary-profile');
      assert.equal(control.scopeLabel, '主设备共享');
      assert.equal(control.status, expectedStatus);
      assert.equal(control.writable, expectedStatus === 'writable');
      assert.equal(
        control.pending,
        ['saving', 'operation-in-flight', 'authority-busy'].includes(expectedStatus),
      );
    }

    const baselineProfile = buildSharedProfile({
      automationEnabled: false,
      pinFavoriteRecipeEnabled: false,
    });
    const desiredProfile = buildSharedProfile({
      automationEnabled: false,
      pinFavoriteRecipeEnabled: true,
    });
    const baselineState = buildFrontendAuthorityState(baselineProfile);
    const transactionBase = capturePrimaryProfileTransactionBase(baselineState);
    const desiredSignature = serializeSharedCompanionPreferences(desiredProfile);
    assert.deepEqual(
      resolvePrimaryProfileObservation(
        transactionBase,
        desiredSignature,
        { ...baselineState, stateRevision: baselineState.stateRevision + 5 },
      ),
      { action: 'retain-draft', reason: 'same-authority-baseline' },
      'State-only device metadata changes must not cancel a profile draft.',
    );
    assert.deepEqual(
      resolvePrimaryProfileObservation(transactionBase, desiredSignature, {
        ...baselineState,
        authorityRevision: baselineState.authorityRevision + 1,
        stateRevision: baselineState.stateRevision + 1,
        activeProfileRevision: baselineState.activeProfileRevision + 1,
        activeProfileHash: 'desired-profile-hash',
        activeProfile: desiredProfile,
        currentDeviceProfileRevision: baselineState.currentDeviceProfileRevision + 1,
        currentDeviceProfileHash: 'desired-profile-hash',
        currentDeviceProfile: desiredProfile,
      }),
      { action: 'confirm-draft', reason: 'desired-profile-committed' },
      'Only the exact next CAS point carrying the desired full profile may confirm the draft.',
    );

    for (const [label, observation, expectedReason] of [
      ['registry', { ...baselineState, registryId: 'other-registry-id' }, 'authority-changed'],
      ['current device', { ...baselineState, currentDeviceId: 'other-current-device' }, 'authority-changed'],
      ['primary device', {
        ...baselineState,
        primaryDeviceId: 'other-primary-device',
        currentDeviceIsPrimary: false,
      }, 'authority-changed'],
      ['authority revision', {
        ...baselineState,
        authorityRevision: baselineState.authorityRevision + 2,
      }, 'authority-changed'],
      ['active profile revision', {
        ...baselineState,
        activeProfileRevision: baselineState.activeProfileRevision + 1,
      }, 'profile-conflict'],
      ['active profile hash', {
        ...baselineState,
        activeProfileHash: 'other-active-profile-hash',
      }, 'profile-conflict'],
      ['current profile revision', {
        ...baselineState,
        currentDeviceProfileRevision: baselineState.currentDeviceProfileRevision + 1,
      }, 'profile-conflict'],
      ['current profile hash', {
        ...baselineState,
        currentDeviceProfileHash: 'other-current-profile-hash',
      }, 'profile-conflict'],
      ['profile content', {
        ...baselineState,
        activeProfile: desiredProfile,
      }, 'profile-conflict'],
    ]) {
      assert.deepEqual(
        resolvePrimaryProfileObservation(transactionBase, desiredSignature, observation),
        { action: 'rollback-draft', reason: expectedReason },
        `${label} drift must roll back instead of rebasing the profile draft.`,
      );
    }
    assert.throws(
      () => capturePrimaryProfileTransactionBase({
        ...baselineState,
        currentDeviceIsPrimary: false,
        primaryDeviceId: 'other-primary-device',
      }),
      /基线未对齐/,
      'A secondary device must not create a primary-profile transaction.',
    );
  } finally {
    await vite.close();
  }
}

async function verifySharedProfileContract() {
  const [
    typescriptSource,
    csharpSource,
    authorityHookSource,
    workbenchSource,
    settingsSource,
  ] = await Promise.all([
    readFile('apps/companion/src/companion/preferences.ts', 'utf8'),
    readFile('mods/bepinex/src/LocalApi/CompanionDeviceAuthorityStore.cs', 'utf8'),
    readFile('apps/companion/src/companion/hooks/useCompanionDeviceAuthority.ts', 'utf8'),
    readFile('apps/companion/src/companion/ModWorkbench.tsx', 'utf8'),
    readFile('apps/companion/src/companion/pages/ModSettingsPanel.tsx', 'utf8'),
  ]);
  const interfaceBody = requireBlock(
    typescriptSource,
    /export interface SharedCompanionPreferences \{(?<body>[\s\S]*?)\n\}/,
    'TypeScript shared profile interface',
  );
  const serverProfileFieldDefinitions = requireBlock(
    csharpSource,
    /ProfileBooleanFieldsV1 =(?<body>[\s\S]*?)private static readonly HashSet<string> StoredDataFields/,
    'C# versioned shared profile fields',
  );
  const typescriptFields = [...interfaceBody.matchAll(/^\s{2}(?<name>[A-Za-z][A-Za-z0-9]*):/gm)]
    .map((match) => match.groups.name)
    .sort();
  const serverFields = [...new Set(
    [...serverProfileFieldDefinitions.matchAll(/"(?<name>[A-Za-z][A-Za-z0-9]*)"/g)]
      .map((match) => match.groups.name),
  )].sort();
  assert.deepEqual(serverFields, typescriptFields, 'Frontend and Mod shared-profile field sets diverged.');
  assert.deepEqual(
    Object.keys(buildSharedProfile()).sort(),
    typescriptFields,
    'Device-authority audit fixture no longer covers the complete shared profile.',
  );
  assert.ok(
    /const\s+parsedState\s*=\s*parseAuthorityState\(next\);/.test(authorityHookSource),
    'Device authority must strictly parse profiles before commit and pending-sync application.',
  );
  assert.ok(
    authorityHookSource.includes('activeProfile: parseSharedCompanionPreferences(state.activeProfile)')
      && authorityHookSource.includes(
        'currentDeviceProfile: parseSharedCompanionPreferences(state.currentDeviceProfile)',
      ),
    'Both active and current wire profiles must use the strict v3 parser.',
  );
  assert.ok(
    authorityHookSource.includes('profileUpdatePending: boolean;')
      && authorityHookSource.includes('profileTransactionPhase: PrimaryProfileTransactionPhase | null;')
      && authorityHookSource.includes('const profileUpdatePending = profileTransactionPhase !== null;')
      && authorityHookSource.includes('stagePrimaryProfile: (profile: Partial<SharedCompanionPreferences>)'),
    'The authority controller must own an explicit staged full-profile transaction.',
  );
  assert.ok(
    workbenchSource.includes('const updateLocalCompanionPreferences = useCallback')
      && workbenchSource.includes('const updateSharedCompanionPreferences = useCallback')
      && workbenchSource.includes('stagePrimaryProfile(next);')
      && !workbenchSource.includes("companionDeviceAuthority.busy !== 'profile'"),
    'Local and shared preference commands must remain split, with the hook as the only shared mutation boundary.',
  );
  assert.ok(
    authorityHookSource.includes('capturePrimaryProfileTransactionBase(current)')
      && authorityHookSource.includes('resolvePrimaryProfileObservation(')
      && authorityHookSource.includes('transaction.base.authorityRevision')
      && authorityHookSource.includes('transaction.base.currentDeviceProfileRevision')
      && !authorityHookSource.includes('const sharedSignature = useMemo('),
    'Profile writes must use one frozen CAS baseline instead of an implicit shared-signature effect.',
  );
  assert.ok(
    authorityHookSource.includes('generationConnectionKeyRef.current === renderConnectionKeyRef.current')
      && authorityHookSource.includes('if (!isActive() || !isAuthorityGenerationCurrent(generation)) return false;')
      && authorityHookSource.indexOf('if (!isActive() || !isAuthorityGenerationCurrent(generation)) return false;')
        < authorityHookSource.indexOf('applySharedPreferencesRef.current(parsedState.currentDeviceProfile)'),
    'A stale connection generation must be rejected before pending-sync apply/ACK side effects.',
  );
  assert.ok(
    authorityHookSource.includes('const authorityWriteOutcomeRef = useRef<AuthorityWriteOutcome | null>(null);')
      && authorityHookSource.includes('const beginAuthorityWriteOutcome = useCallback')
      && authorityHookSource.includes('const finishAuthorityWriteOutcome = useCallback')
      && authorityHookSource.includes('const waitForAuthorityWriteOutcomes = useCallback')
      && authorityHookSource.includes('await waitForAuthorityWriteOutcomes();')
      && authorityHookSource.indexOf('await waitForAuthorityWriteOutcomes();')
        < authorityHookSource.indexOf('const next = await registerCompanionDevice('),
    'A new connection generation must wait for every outcome-unknown authority write before registering.',
  );
  assert.ok(
    authorityHookSource.includes('const pendingSyncApplicationRef = useRef<PendingSyncApplication | null>(null);')
      && authorityHookSource.includes('const [pendingSyncApplying, setPendingSyncApplying] = useState(false);')
      && authorityHookSource.includes('function buildPendingSyncKey(')
      && authorityHookSource.includes('return runningSync.result;')
      && authorityHookSource.includes('&& !state.pendingSyncId')
      && authorityHookSource.includes('&& !pendingSyncApplying'),
    'Pending sync must be keyed single-flight and close ready/profile/runtime writer gates until ACK settles.',
  );
  assert.ok(
    authorityHookSource.includes('const authorityOperationRef = useRef<AuthorityOperation | null>(null);')
      && authorityHookSource.includes('const acquireAuthorityOperation = useCallback')
      && authorityHookSource.includes('const releaseAuthorityOperation = useCallback')
      && authorityHookSource.includes("const operation = acquireAuthorityOperation(kind, true);")
      && authorityHookSource.includes("const operation = acquireAuthorityOperation('refresh', false);")
      && authorityHookSource.includes('authorityOperationRef.current = operation;'),
    'Device mutations and refreshes must acquire one synchronous generation-owned command slot.',
  );
  assert.ok(
    !workbenchSource.includes('const updateCompanionPreferences = useCallback')
      && settingsSource.includes('onLocalPreferenceChange: (next: Partial<LocalCompanionPreferences>)')
      && settingsSource.includes('onSharedPreferenceChange: (next: Partial<SharedCompanionPreferences>)'),
    'The removed untyped preference mutation path must not remain in the settings composition root.',
  );
}

function pickModuleControl(control) {
  const {
    scope,
    status,
    enabled,
    writable,
    pending,
    scopeLabel,
  } = control;
  return { scope, status, enabled, writable, pending, scopeLabel };
}

function buildFrontendAuthorityState(profile) {
  const deviceId = 'frontend-primary-device-0001';
  return {
    ok: true,
    protocolVersion: 1,
    profileSchemaVersion: 3,
    registryId: 'frontend-authority-registry-0001',
    authorityRevision: 7,
    stateRevision: 11,
    primaryDeviceId: deviceId,
    currentDeviceId: deviceId,
    currentDeviceIsPrimary: true,
    activeProfileRevision: 3,
    activeProfileHash: 'baseline-profile-hash',
    activeProfile: structuredClone(profile),
    currentDeviceProfileRevision: 3,
    currentDeviceProfileHash: 'baseline-profile-hash',
    currentDeviceProfile: structuredClone(profile),
    pendingSyncId: null,
    devices: [],
    error: null,
  };
}

function requireBlock(source, pattern, label) {
  const match = pattern.exec(source);
  assert.ok(match?.groups?.body, `${label} was not found.`);
  return match.groups.body;
}
