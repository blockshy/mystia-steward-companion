import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { register } from 'node:module';

register('../mission-src-alias-loader.mjs', import.meta.url);

const {
  buildTrackedMissionStatusViews,
  compareTrackedMissions,
  getTrackedMissionTransientRetryDelayMs,
  isTrackedMissionStatusView,
  parseTrackedMissionsApiResponse,
  TRACKED_MISSION_POLL_INTERVAL_MS,
  TRACKED_MISSION_STATUS_ORDER,
  TRACKED_MISSION_STATUS_VIEW_ORDER,
  TRACKED_MISSION_TRANSIENT_RETRY_DELAYS_MS,
} = await import('../../apps/companion/src/companion/tracked-missions.ts');
const {
  MAX_MISSION_CHARACTER_NAME_LENGTH,
  MAX_MISSION_PRESENTATION_STATUS_LENGTH,
  MAX_MISSION_RECEIVER_LABEL_LENGTH,
  MAX_MISSION_SCENE_COUNT,
  MAX_MISSION_SCENE_NAME_LENGTH,
} = await import('../../apps/companion/src/companion/mission-presentation.ts');

const signature = 'a'.repeat(64);
const fullResponse = {
  ok: true,
  runtimeAvailable: true,
  generation: 7,
  status: 'ready',
  contentSignature: signature,
  unverifiedCount: 1,
  trackingCount: 1,
  fulfilledCount: 1,
  missions: [
    mission('pending', '待确认任务', 'unverified', [null, null], null),
    mission('active', '进行中任务', 'tracking', [true, false], 1),
    mission('done', '可完成任务', 'fulfilled', [true, true], 2),
  ],
  error: null,
};
const legacyMission = {
  label: 'legacy',
  title: '缺少展示元数据的旧任务',
  status: 'unverified',
  conditionCount: 0,
  completedConditionCount: null,
  conditionStates: [],
};

assert.deepEqual(parseTrackedMissionsApiResponse(fullResponse), fullResponse);
assert.deepEqual(
  parseTrackedMissionsApiResponse(replaceMissionPresentation(fullResponse, {
    sceneNames: [],
  })).missions[0].sceneNames,
  [],
  'A ready receiver with no declared destination must remain valid.',
);
assert.deepEqual(
  parseTrackedMissionsApiResponse(replaceMissionPresentation(fullResponse, {
    presentationStatus: 'unavailable:entry-read',
    characterName: '已独立确认的角色',
    sceneNames: [],
  })).missions[0],
  {
    ...fullResponse.missions[0],
    presentationStatus: 'unavailable:entry-read',
    characterName: '已独立确认的角色',
    sceneNames: [],
  },
  'Unavailable presentation metadata must retain independently confirmed fields.',
);
assert.deepEqual(
  parseTrackedMissionsApiResponse({
    unchanged: true,
    contentSignature: signature,
  }),
  {
    unchanged: true,
    contentSignature: signature,
  },
);

for (const invalidResponse of [
  { ...fullResponse, contentSignature: 'not-a-signature' },
  { ...fullResponse, generation: 0 },
  { ...fullResponse, fulfilledCount: 2 },
  {
    ...fullResponse,
    missions: [
      ...fullResponse.missions,
      fullResponse.missions[0],
    ],
    unverifiedCount: 2,
  },
  {
    ...fullResponse,
    missions: [
      mission('bad-status', '错误状态', 'fulfilled', [true, false], 1),
      fullResponse.missions[1],
      fullResponse.missions[2],
    ],
  },
  { ...fullResponse, status: 'mission-data-incomplete' },
  { ...fullResponse, status: 'partially-ready' },
  { ...fullResponse, ok: false },
  {
    ...fullResponse,
    runtimeAvailable: false,
    status: 'ready',
    unverifiedCount: 0,
    trackingCount: 0,
    fulfilledCount: 0,
    missions: [],
  },
  {
    ...fullResponse,
    missions: [legacyMission, fullResponse.missions[1], fullResponse.missions[2]],
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
    characterName: 'unexpected',
    sceneNames: [],
    presentationStatus: 'no-receiver',
  }),
  replaceMissionPresentation(fullResponse, {
    receiverLabel: '',
    characterName: '',
    sceneNames: ['unexpected'],
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
    presentationStatus: 'unavailable:pending',
  }),
  replaceMissionPresentation(fullResponse, {
    presentationStatus: 'unavailable:unknown-code',
  }),
  replaceMissionPresentation(fullResponse, {
    presentationStatus: 'pending',
  }),
]) {
  assert.throws(
    () => parseTrackedMissionsApiResponse(invalidResponse),
    Error,
    'Malformed mission payloads must fail closed.',
  );
}

assert.deepEqual(
  fullResponse.missions.slice().sort(compareTrackedMissions).map((entry) => entry.label),
  ['done', 'active', 'pending'],
  'Mission rows must put completable work before tracking and unverified entries.',
);
const statusViews = buildTrackedMissionStatusViews([
  mission('tracking-z', '同名进行中任务', 'tracking', [false], 0),
  fullResponse.missions[0],
  mission('tracking-a', '同名进行中任务', 'tracking', [false], 0),
  fullResponse.missions[2],
]);
assert.deepEqual(
  TRACKED_MISSION_STATUS_ORDER,
  ['fulfilled', 'tracking', 'unverified'],
  'Mission status order changed.',
);
assert.deepEqual(
  TRACKED_MISSION_STATUS_VIEW_ORDER,
  ['all', 'fulfilled', 'tracking', 'unverified'],
  'Mission status tabs changed their fixed order.',
);
assert.deepEqual(
  statusViews.map((view) => view.value),
  ['all', 'fulfilled', 'tracking', 'unverified'],
  'Mission status view models did not include every mutually exclusive tab.',
);
assert.deepEqual(
  statusViews.map((view) => view.missions.map((entry) => entry.label)),
  [
    ['done', 'tracking-a', 'tracking-z', 'pending'],
    ['done'],
    ['tracking-a', 'tracking-z'],
    ['pending'],
  ],
  'Mission status views lost their stable status/title/label ordering.',
);
assert.deepEqual(
  buildTrackedMissionStatusViews([]).map((view) => view.missions.length),
  [0, 0, 0, 0],
  'Empty mission status tabs must remain available with zero counts.',
);
assert.equal(isTrackedMissionStatusView('all'), true);
assert.equal(isTrackedMissionStatusView('fulfilled'), true);
assert.equal(isTrackedMissionStatusView('available'), false);
assert.equal(isTrackedMissionStatusView(null), false);
assert.equal(TRACKED_MISSION_POLL_INTERVAL_MS, 2_000);
assert.deepEqual(TRACKED_MISSION_TRANSIENT_RETRY_DELAYS_MS, [500, 1_000, 2_000, 4_000]);
assert.deepEqual(
  TRACKED_MISSION_TRANSIENT_RETRY_DELAYS_MS.map(
    (_, index) => getTrackedMissionTransientRetryDelayMs(index),
  ),
  [500, 1_000, 2_000, 4_000],
);
assert.equal(getTrackedMissionTransientRetryDelayMs(-1), null);
assert.equal(getTrackedMissionTransientRetryDelayMs(4), null);
assert.equal(getTrackedMissionTransientRetryDelayMs(1.5), null);

const root = new URL('../../', import.meta.url);
const [
  apiSource,
  hookSource,
  panelSource,
  workbenchSource,
  mockSource,
  trackedParserSource,
  availableParserSource,
  presentationParserSource,
  storageSource,
  moduleControlSource,
] = await Promise.all([
  readFile(new URL('apps/companion/src/companion/api.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/hooks/useTrackedMissions.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/pages/ModMissionListPanel.tsx', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
  readFile(new URL('scripts/mock-local-api.mjs', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/tracked-missions.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/available-missions.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/mission-presentation.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/storage.ts', root), 'utf8'),
  readFile(new URL('apps/companion/src/companion/pages/ModuleControlPanel.tsx', root), 'utf8'),
]);

const apiFunction = sourceSlice(
  apiSource,
  'export async function readTrackedMissions',
  'export async function inviteAvailableRareGuest',
);
assert.match(apiFunction, /readLocalApiJson<TrackedMissionsApiResponse>/);
assert.match(apiFunction, /\/missions\/tracked/);
assert.match(apiFunction, /knownSignature/);
assert.match(apiFunction, /signal: options\.signal/);
assert.match(
  trackedParserSource,
  /import \{ parseMissionPresentationMetadata \} from '@\/companion\/mission-presentation'/,
);
assert.match(
  availableParserSource,
  /import \{ parseMissionPresentationMetadata \} from '@\/companion\/mission-presentation'/,
);
assert.equal(
  (presentationParserSource.match(/function parseMissionPresentationMetadata/g) ?? []).length,
  1,
  'Mission presentation parsing must have one canonical implementation.',
);
assert.match(presentationParserSource, /presentationStatus === 'no-receiver'/);
assert.match(presentationParserSource, /presentationStatus === 'ready'/);
assert.match(presentationParserSource, /MISSION_PRESENTATION_UNAVAILABLE_STATUSES\.has/);

for (const contract of [
  'activeRequestIdentityRef',
  'requestGenerationRef',
  'abortControllerRef',
  'abortControllerRef.current?.abort()',
  'latestIdentityRef.current !== requestIdentity',
  'previousResult?.contentSignature',
  'parseTrackedMissionsApiResponse',
  'TRACKED_MISSION_POLL_INTERVAL_MS',
  'scheduleTransientRetry(requestIdentity)',
  'transientDelay ?? TRACKED_MISSION_POLL_INTERVAL_MS',
  'if (transientDelay != null) retryAttemptRef.current += 1',
  'clearResult()',
]) {
  assert.ok(hookSource.includes(contract), `Tracked mission Hook is missing request contract: ${contract}`);
}

assert.ok(workbenchSource.includes('active: missionListModuleEnabled && missionListVisible'));
assert.ok(workbenchSource.includes('readStoredMissionListModuleEnabled'));
assert.ok(workbenchSource.includes('persistMissionListModuleEnabled(enabled)'));
assert.ok(workbenchSource.includes('refreshTrackedMissions();'));
assert.ok(workbenchSource.includes("const missionListVisible = tab === 'extensions' && extensionTab === 'missions'"));
assert.ok(workbenchSource.includes('data-gamepad-tab-value="extensions"'));
assert.ok(workbenchSource.includes('data-extension-tabs="true"'));
assert.ok(workbenchSource.includes('<TabsTrigger value="missions"'));
assert.ok(workbenchSource.includes('<TabsTrigger value="rare-invitations"'));
assert.doesNotMatch(workbenchSource, /data-gamepad-tab-value="(?:missions|rare-invitations|inventory)"/);
assert.doesNotMatch(workbenchSource, /MissionPanelView|missionPanelView/);
assert.doesNotMatch(panelSource, /<TabsTrigger value="tasks"|<TabsTrigger value="invitations"/);
assert.ok(panelSource.includes('label="启用任务列表模块"'));
assert.ok(panelSource.includes('任务列表模块已停用'));
assert.ok(panelSource.includes('missionListModuleEnabled'));
assert.ok(panelSource.includes('data-gamepad-focus-key="missions:refresh"'));
assert.ok(panelSource.includes('gamepadScrollKey="missions"'));
assert.ok(panelSource.includes('data-mission-status-tabs="true"'));
assert.ok(panelSource.includes('aria-label="任务状态筛选"'));
assert.ok(panelSource.includes('data-gamepad-focus-key={`missions:status:${status.value}`}'));
assert.ok(panelSource.includes('data-mission-status-tab={status.value}'));
assert.ok(panelSource.includes('data-mission-status-list={activeStatusView.value}'));
assert.ok(panelSource.includes('data-mission-status={mission.status}'));
assert.ok(panelSource.includes('data-mission-character-name={mission.characterName}'));
assert.ok(panelSource.includes('data-mission-related-scenes="true"'));
assert.ok(panelSource.includes('data-mission-scene-name={sceneName}'));
assert.ok(panelSource.includes('data-mission-presentation-debug="true"'));
assert.ok(panelSource.includes('receiverLabel='));
assert.ok(panelSource.includes('presentationStatus='));
assert.ok(panelSource.includes('任务角色'));
assert.ok(panelSource.includes('相关场景'));
assert.doesNotMatch(panelSource, /data-mission-status-group/);
assert.doesNotMatch(panelSource, /可在游戏内对应角色处接取/);
assert.match(panelSource, /可接取/);
assert.ok(panelSource.includes('任务进度尚未完成原生校验'));
assert.doesNotMatch(panelSource, /待刷新|等待游戏自然刷新任务进度/);
assert.ok(panelSource.includes('mission.conditionStates.map'));
assert.ok(storageSource.includes("`${STORAGE_PREFIX}-mission-list-module-enabled`"));
assert.match(
  storageSource,
  /readStoredMissionListModuleEnabled\(\): boolean \{\s+return readStoredBoolean\(MISSION_LIST_MODULE_ENABLED_STORAGE_KEY, false\);/,
);
assert.ok(moduleControlSource.includes('data-gamepad-focus-key={focusKey}'));

const postBranch = sourceSlice(
  mockSource,
  "if (request.method === 'POST')",
  "if (request.method !== 'GET')",
);
const getBranch = sourceSlice(mockSource, "if (request.method !== 'GET')", 'server.listen');
assert.doesNotMatch(postBranch, /path === '\/missions\/tracked'/);
assert.match(getBranch, /path === '\/missions\/tracked'/);
assert.match(mockSource, /knownSignature/);
assert.match(mockSource, /unchanged: true/);

console.log(
  'PASS: tracked missions use a default-off persisted module gate, strict GET protocol, '
  + 'bounded request lifecycle, signature polling, canonical task navigation, and fail-closed response parsing.',
);

function mission(label, title, status, conditionStates, completedConditionCount) {
  return {
    label,
    title,
    receiverLabel: `receiver:${label}`,
    characterName: `角色：${title}`,
    sceneNames: ['妖怪兽道'],
    presentationStatus: 'ready',
    status,
    conditionCount: conditionStates.length,
    completedConditionCount,
    conditionStates,
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
