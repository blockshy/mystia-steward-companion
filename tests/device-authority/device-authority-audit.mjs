import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';

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

try {
  await verifySharedProfileContract();
  await waitForServer();
  const windowsProfile = buildSharedProfile({ automationEnabled: true, autoRareConcurrency: 2 });
  const androidProfile = buildSharedProfile({ automationEnabled: false, autoRareConcurrency: 3 });

  const first = await postJson('/devices/register', windows, {
    protocolVersion: 1,
    profileSchemaVersion: 1,
    platform: 'windows',
    appVersion: '1.2.0',
    profile: windowsProfile,
  });
  assert.equal(first.currentDeviceIsPrimary, true);
  assert.equal(first.authorityRevision, 1);
  assert.equal(first.devices.length, 1);

  const second = await postJson('/devices/register', android, {
    protocolVersion: 1,
    profileSchemaVersion: 1,
    platform: 'android',
    appVersion: '1.2.0',
    profile: androidProfile,
  });
  assert.equal(second.currentDeviceIsPrimary, false);
  assert.equal(second.activeProfile.automationEnabled, true);
  assert.equal(second.currentDeviceProfile.automationEnabled, false);

  const forbiddenProfileWrite = await rawPost('/devices/profile', android, {
    protocolVersion: 1,
    profileSchemaVersion: 1,
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

  const updatedProfile = buildSharedProfile({
    automationEnabled: true,
    autoRareConcurrency: 4,
    pinFavoriteRecipeEnabled: true,
  });
  const updated = await postJson('/devices/profile', windows, {
    protocolVersion: 1,
    profileSchemaVersion: 1,
    expectedAuthorityRevision: first.authorityRevision,
    expectedProfileRevision: first.currentDeviceProfileRevision,
    profile: updatedProfile,
  });
  assert.equal(updated.authorityRevision, 2);
  assert.equal(updated.activeProfile.autoRareConcurrency, 4);

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

function buildSharedProfile(overrides = {}) {
  const profile = {
    automationEnabled: false,
    autoRareOrderEnabled: true,
    autoNormalOrderEnabled: false,
    autoNormalTakeBeverage: false,
    autoNormalStartCooking: false,
    autoNormalDeliverFood: false,
    autoNormalCompleteOrder: false,
    autoNormalStopOnError: false,
    autoPrepCompleteOrder: false,
    autoPrepTakeBeverage: false,
    autoPrepStartCooking: false,
    autoPrepCollectCooking: false,
    autoPrepRecipeFavoritesOnly: false,
    autoPrepBeverageFavoritesOnly: false,
    autoPrepStopOnError: false,
    autoRareConcurrency: 2,
    autoNormalConcurrency: 3,
    autoMaxStepRetries: 3,
    autoMaxRollbacks: 2,
    filterMissingCookers: true,
    missionRecipePriorityEnabled: true,
    pinFavoriteRecipeEnabled: false,
    pinFavoriteBeverageEnabled: false,
    rareGameUiPinningEnabled: false,
    normalGameUiPinningEnabled: false,
    rareRecipeVariantEnabled: false,
    normalRecipeVariantEnabled: false,
    rareCookerHighlightEnabled: false,
    normalCookerHighlightEnabled: false,
    rareSeatHighlightEnabled: false,
    normalSeatHighlightEnabled: false,
    rareOrderHighlightEnabled: false,
    normalOrderHighlightEnabled: false,
    rareTargetHighlightColor: '#FFDB2E',
    normalTargetHighlightColor: '#5FACD3',
    serviceOrderSortMode: 'ordered',
    recommendationSortProfile: {
      preset: 'balanced',
      objectives: [
        objective('foodPreference', 'desc'),
        objective('beveragePreference', 'desc'),
        objective('negativeRisk', 'asc'),
        objective('extraCount', 'asc'),
        objective('resourcePressure', 'asc'),
        objective('totalCost', 'asc'),
        objective('profit', 'desc'),
        objective('beverageStock', 'desc'),
        objective('cookerAvailable', 'desc'),
      ],
    },
    recommendationBudgetPolicy: 'block',
    recipeVariantLimitPerBase: 1,
    recommendationExclusions: {
      excludedIngredientIds: [],
      excludedBeverageIds: [],
    },
  };
  return { ...profile, ...overrides };
}

function objective(key, direction) {
  return { key, enabled: true, weight: 50, direction };
}

async function verifySharedProfileContract() {
  const [typescriptSource, csharpSource] = await Promise.all([
    readFile('apps/companion/src/companion/preferences.ts', 'utf8'),
    readFile('mods/bepinex/src/LocalApi/CompanionDeviceAuthorityStore.cs', 'utf8'),
  ]);
  const interfaceBody = requireBlock(
    typescriptSource,
    /export interface SharedCompanionPreferences \{(?<body>[\s\S]*?)\n\}/,
    'TypeScript shared profile interface',
  );
  const booleanBody = requireBlock(
    csharpSource,
    /ProfileBooleanFields = new\(StringComparer\.Ordinal\)\s*\{(?<body>[\s\S]*?)\n\s*\};/,
    'C# shared boolean fields',
  );
  const additionalBody = requireBlock(
    csharpSource,
    /ProfileBooleanFields\.Concat\(new\[\]\s*\{(?<body>[\s\S]*?)\n\s*\}\),/,
    'C# additional shared fields',
  );
  const typescriptFields = [...interfaceBody.matchAll(/^\s{2}(?<name>[A-Za-z][A-Za-z0-9]*):/gm)]
    .map((match) => match.groups.name)
    .sort();
  const serverFields = [...booleanBody.matchAll(/"(?<name>[A-Za-z][A-Za-z0-9]*)"/g),
    ...additionalBody.matchAll(/"(?<name>[A-Za-z][A-Za-z0-9]*)"/g)]
    .map((match) => match.groups.name)
    .sort();
  assert.deepEqual(serverFields, typescriptFields, 'Frontend and Mod shared-profile field sets diverged.');
  assert.deepEqual(
    Object.keys(buildSharedProfile()).sort(),
    typescriptFields,
    'Device-authority audit fixture no longer covers the complete shared profile.',
  );
}

function requireBlock(source, pattern, label) {
  const match = pattern.exec(source);
  assert.ok(match?.groups?.body, `${label} was not found.`);
  return match.groups.body;
}
