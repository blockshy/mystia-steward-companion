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
    } = await vite.ssrLoadModule('/src/companion/preferences.ts');
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
  } finally {
    await vite.close();
  }
}

async function verifySharedProfileContract() {
  const [typescriptSource, csharpSource, authorityHookSource] = await Promise.all([
    readFile('apps/companion/src/companion/preferences.ts', 'utf8'),
    readFile('mods/bepinex/src/LocalApi/CompanionDeviceAuthorityStore.cs', 'utf8'),
    readFile('apps/companion/src/companion/hooks/useCompanionDeviceAuthority.ts', 'utf8'),
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
    authorityHookSource.includes('const parsedState = parseAuthorityState(next);'),
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
    !authorityHookSource.includes('normalizeSharedCompanionPreferences'),
    'Device-authority wire reads must not fall back to tolerant local normalization.',
  );
}

function requireBlock(source, pattern, label) {
  const match = pattern.exec(source);
  assert.ok(match?.groups?.body, `${label} was not found.`);
  return match.groups.body;
}
