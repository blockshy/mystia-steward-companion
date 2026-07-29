import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { register } from 'node:module';

register('../mission-src-alias-loader.mjs', import.meta.url);

const {
  AVAILABLE_MISSION_POLL_INTERVAL_MS,
  AVAILABLE_MISSION_TRANSIENT_RETRY_DELAYS_MS,
  compareAvailableMissions,
  getAvailableMissionTransientRetryDelayMs,
  parseAvailableMissionsApiResponse,
} = await import('../../apps/companion/src/companion/available-missions.ts');
const {
  MAX_MISSION_CHARACTER_NAME_LENGTH,
  MAX_MISSION_PRESENTATION_STATUS_LENGTH,
  MAX_MISSION_RECEIVER_LABEL_LENGTH,
  MAX_MISSION_SCENE_COUNT,
  MAX_MISSION_SCENE_NAME_LENGTH,
} = await import('../../apps/companion/src/companion/mission-presentation.ts');

const signature = 'b'.repeat(64);
const fullResponse = {
  ok: true,
  runtimeAvailable: true,
  status: 'ready',
  missionGeneration: 7,
  daySceneGeneration: 3,
  contentSignature: signature,
  availableCount: 2,
  missions: [
    availableMission('mission-z', '同名任务', '角色乙', ['妖怪兽道']),
    availableMission('mission-a', '同名任务', '角色甲', ['博丽神社', '人间之里']),
  ],
  error: null,
};

assert.deepEqual(parseAvailableMissionsApiResponse(fullResponse), fullResponse);
const opaqueIdentityResponse = parseAvailableMissionsApiResponse({
  ...fullResponse,
  availableCount: 1,
  missions: [availableMission('  mission-id  ', '  保留原始标题  ', '接取角色', [])],
});
assert.equal(opaqueIdentityResponse.missions[0].label, '  mission-id  ');
assert.equal(opaqueIdentityResponse.missions[0].title, '  保留原始标题  ');
assert.equal(opaqueIdentityResponse.missions[0].presentationStatus, 'ready');
assert.deepEqual(
  parseAvailableMissionsApiResponse({
    unchanged: true,
    contentSignature: signature,
  }),
  {
    unchanged: true,
    contentSignature: signature,
  },
);

for (const invalidResponse of [
  { ...fullResponse, contentSignature: 'invalid' },
  { ...fullResponse, missionGeneration: 0 },
  { ...fullResponse, daySceneGeneration: 0 },
  { ...fullResponse, availableCount: 1 },
  { ...fullResponse, status: 'partially-ready' },
  { ...fullResponse, ok: false },
  { ...fullResponse, missions: [...fullResponse.missions, fullResponse.missions[0]], availableCount: 3 },
  {
    ...fullResponse,
    runtimeAvailable: false,
    status: 'runtime-unavailable',
  },
  {
    ...fullResponse,
    missions: [
      { label: 'legacy', title: '缺少展示元数据的旧任务' },
      fullResponse.missions[1],
    ],
  },
  replaceMissionPresentation(fullResponse, {
    receiverLabel: 'r'.repeat(MAX_MISSION_RECEIVER_LABEL_LENGTH + 1),
  }),
  replaceMissionPresentation(fullResponse, {
    characterName: '角'.repeat(MAX_MISSION_CHARACTER_NAME_LENGTH + 1),
  }),
  replaceMissionPresentation(fullResponse, {
    characterName: '   ',
    presentationStatus: 'unavailable:scene-marker',
  }),
  replaceMissionPresentation(fullResponse, {
    presentationStatus: 's'.repeat(MAX_MISSION_PRESENTATION_STATUS_LENGTH + 1),
  }),
  replaceMissionPresentation(fullResponse, {
    sceneNames: Array.from({ length: MAX_MISSION_SCENE_COUNT + 1 }, (_, index) => `场景${index}`),
  }),
  replaceMissionPresentation(fullResponse, {
    sceneNames: ['同一场景', '同一场景'],
  }),
  replaceMissionPresentation(fullResponse, {
    sceneNames: ['场'.repeat(MAX_MISSION_SCENE_NAME_LENGTH + 1)],
  }),
  replaceMissionPresentation(fullResponse, {
    sceneNames: ['   '],
  }),
  replaceMissionPresentation(fullResponse, {
    presentationStatus: '   ',
  }),
  replaceMissionPresentation(fullResponse, {
    receiverLabel: 'unexpected',
    characterName: '',
    sceneNames: [],
    presentationStatus: 'no-receiver',
  }),
  replaceMissionPresentation(fullResponse, {
    receiverLabel: '',
    characterName: '',
    sceneNames: [],
    presentationStatus: 'no-receiver',
  }),
  replaceMissionPresentation(fullResponse, {
    receiverLabel: '',
    presentationStatus: 'ready',
  }),
  replaceMissionPresentation(fullResponse, {
    characterName: '',
    presentationStatus: 'ready',
  }),
  replaceMissionPresentation(fullResponse, {
    receiverLabel: '',
    characterName: '',
    sceneNames: [],
    presentationStatus: 'unavailable:destinations',
  }),
  replaceMissionPresentation(fullResponse, {
    presentationStatus: 'unavailable:unknown-code',
  }),
]) {
  assert.throws(
    () => parseAvailableMissionsApiResponse(invalidResponse),
    Error,
    'Malformed available mission payloads must fail closed.',
  );
}

assert.deepEqual(
  fullResponse.missions.slice().sort(compareAvailableMissions).map((mission) => mission.label),
  ['mission-a', 'mission-z'],
);
assert.equal(AVAILABLE_MISSION_POLL_INTERVAL_MS, 2_000);
assert.deepEqual(AVAILABLE_MISSION_TRANSIENT_RETRY_DELAYS_MS, [500, 1_000, 2_000, 4_000]);
assert.deepEqual(
  AVAILABLE_MISSION_TRANSIENT_RETRY_DELAYS_MS.map(
    (_, index) => getAvailableMissionTransientRetryDelayMs(index),
  ),
  [500, 1_000, 2_000, 4_000],
);
assert.equal(getAvailableMissionTransientRetryDelayMs(-1), null);
assert.equal(getAvailableMissionTransientRetryDelayMs(4), null);

const root = new URL('../../', import.meta.url);
const [apiSource, hookSource, panelSource, workbenchSource, mockSource] = await Promise.all([
  readFile(new URL('apps/companion/src/companion/api.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/hooks/useAvailableMissions.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/pages/ModMissionsPanel.tsx', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
  readFile(new URL('scripts/mock-local-api.mjs', root), 'utf8'),
]);

const apiFunction = sourceSlice(
  apiSource,
  'export async function readAvailableMissions',
  'export async function inviteAvailableRareGuest',
);
assert.match(apiFunction, /readLocalApiJson<AvailableMissionsApiResponse>/);
assert.match(apiFunction, /\/missions\/available/);
assert.match(apiFunction, /knownSignature/);
assert.match(apiFunction, /signal: options\.signal/);

for (const contract of [
  'activeRequestIdentityRef',
  'requestGenerationRef',
  'abortControllerRef',
  'abortControllerRef.current?.abort()',
  'latestIdentityRef.current !== requestIdentity',
  'previousResult?.contentSignature',
  'parseAvailableMissionsApiResponse',
  'AVAILABLE_MISSION_POLL_INTERVAL_MS',
  'daySceneGeneration',
  'daySceneReady',
  'missionGeneration',
  'response.daySceneGeneration !== daySceneGeneration',
  'response.missionGeneration !== missionGeneration',
  'transientDelay ?? AVAILABLE_MISSION_POLL_INTERVAL_MS',
  'if (transientDelay != null) retryAttemptRef.current += 1',
]) {
  assert.ok(hookSource.includes(contract), `Available mission Hook is missing contract: ${contract}`);
}

assert.ok(workbenchSource.includes("missionPanelView === 'tasks'"));
assert.ok(workbenchSource.includes('refreshAvailableMissions();'));
assert.ok(workbenchSource.includes('refreshTrackedMissions();'));
assert.ok(panelSource.includes('<TabsTrigger value="tasks"'));
assert.ok(panelSource.includes('任务列表'));
assert.ok(panelSource.includes('data-gamepad-focus-key="missions:tasks:refresh"'));
assert.ok(panelSource.includes('missions:tasks:status:${status.value}'));
assert.ok(panelSource.includes("'available'"));
assert.ok(panelSource.includes('trackedLabels.has(mission.label)'));
assert.ok(panelSource.includes("kind: 'available'"));
assert.ok(panelSource.includes("kind: 'tracked'"));
assert.ok(panelSource.includes("availableResult !== null && trackedResult !== null"));
assert.ok(panelSource.includes("statusView === 'available'"));
assert.match(
  await readFile(new URL('apps/companion/src/companion/available-missions.ts', root), 'utf8'),
  /presentation\.presentationStatus === 'no-receiver'/,
);
assert.ok(panelSource.includes('data-mission-character-name={mission.characterName}'));
assert.ok(panelSource.includes('data-mission-related-scenes="true"'));
assert.ok(panelSource.includes('data-mission-presentation-debug="true"'));
assert.ok(panelSource.includes('任务角色'));
assert.ok(panelSource.includes('相关场景'));
assert.doesNotMatch(panelSource, /可在游戏内对应角色处接取/);

const postBranch = sourceSlice(
  mockSource,
  "if (request.method === 'POST')",
  "if (request.method !== 'GET')",
);
const getBranch = sourceSlice(mockSource, "if (request.method !== 'GET')", 'server.listen');
assert.doesNotMatch(postBranch, /path === '\/missions\/available'/);
assert.match(getBranch, /path === '\/missions\/available'/);

console.log(
  'PASS: available missions use strict parsing, day-scene request identity, '
  + 'bounded presentation metadata, canonical GET, and merge into the task list without duplicate tracked labels.',
);

function availableMission(label, title, characterName, sceneNames) {
  return {
    label,
    title,
    receiverLabel: `receiver:${label}`,
    characterName,
    sceneNames,
    presentationStatus: 'ready',
  };
}

function replaceMissionPresentation(response, presentation) {
  return {
    ...response,
    missions: [
      {
        ...response.missions[0],
        ...presentation,
      },
      ...response.missions.slice(1),
    ],
  };
}

function sourceSlice(source, startText, endText) {
  const start = source.indexOf(startText);
  const end = source.indexOf(endText, start + startText.length);
  assert.ok(start >= 0, `Source boundary not found: ${startText}`);
  assert.ok(end > start, `Source boundary not found: ${endText}`);
  return source.slice(start, end);
}
