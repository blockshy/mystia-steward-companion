import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'vite';

const root = new URL('../../', import.meta.url);
const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true },
  appType: 'custom',
  logLevel: 'silent',
});

let cookerModule;
let automationModule;
try {
  [cookerModule, automationModule] = await Promise.all([
    vite.ssrLoadModule('/src/companion/domain/cookers.ts'),
    vite.ssrLoadModule('/src/companion/domain/automation.ts'),
  ]);
} finally {
  await vite.close();
}

const {
  buildAutomationCookerCapacity,
  buildAutomationCookerPool,
  buildRecommendationCookerNameSet,
  buildRuntimeSets,
  getCookerSlotCapacity,
  validateRecommendationCookerSnapshot,
} = cookerModule;
const {
  reserveAutomationCookerSlot,
} = automationModule;

const completeRuntime = buildRuntime({
  placedCookers: [
    buildCooker(0, [1], ['煮锅'], {
      couldOpen: true,
      automationAvailable: false,
      automationAvailability: 'Unavailable',
    }),
    buildCooker(1, [2], ['烧烤架'], {
      couldOpen: true,
      automationAvailable: true,
      automationAvailability: 'ExtractedResidual',
    }),
  ],
  placedCookerTypeIds: [1, 2],
  placedCookerSnapshotComplete: true,
  placedCookerControllerCount: 2,
});
assert.equal(validateRecommendationCookerSnapshot(completeRuntime), '');
const completeRuntimeSets = buildRuntimeSets(completeRuntime);
assert.equal(completeRuntimeSets.hasCookerSnapshot, true);
assert.deepEqual([...completeRuntimeSets.placedCookerNames].sort(), ['烧烤架', '煮锅']);
assert.deepEqual([...completeRuntimeSets.usableCookerNames].sort(), ['烧烤架', '煮锅']);
assert.deepEqual([...completeRuntimeSets.runtimeUnavailableCookerNames], []);

const mixedOpenRuntime = buildRuntime({
  placedCookers: [
    buildCooker(1, [2], ['烧烤架']),
  ],
  placedCookerTypeIds: [2],
  placedCookerControllerCount: 2,
  placedCookerLockedControllerCount: 1,
});
const mixedOpenSets = buildRuntimeSets(mixedOpenRuntime);
assert.deepEqual([...mixedOpenSets.usableCookerNames], ['烧烤架'],
  'One open controller must keep a shared physical cooker type usable.');
assert.equal(mixedOpenSets.runtimeUnavailableCookerNames.has('烧烤架'), false);
assert.equal(mixedOpenSets.runtimeUnavailableCookerNames.has('油锅'), true,
  'During an event lock, only types proven by surviving unlocked controllers may remain usable.');

const allLockedRuntime = buildRuntime({
  placedCookers: [],
  placedCookerTypeIds: [],
  placedCookerControllerCount: 2,
  placedCookerLockedControllerCount: 2,
});
const allLockedSets = buildRuntimeSets(allLockedRuntime);
assert.deepEqual([...allLockedSets.placedCookerNames], [],
  'Locked controllers must not enter the physical cooker projection.');
assert.deepEqual([...allLockedSets.usableCookerNames], []);
assert.equal(allLockedSets.runtimeUnavailableCookerNames.has('烧烤架'), true,
  'A hidden locked controller must conservatively block an unconfirmed cooker type.');
assert.equal(
  buildRecommendationCookerNameSet(allLockedSets, false).has('烧烤架'),
  false,
  'Disabling the missing-cooker preference must not re-enable a known locked physical type.',
);
assert.equal(
  buildRecommendationCookerNameSet(allLockedSets, false).has('油锅'),
  false,
  'A hidden locked controller must prevent missing types from being re-enabled without evidence.',
);

const unlockedSets = buildRuntimeSets(buildRuntime({
  placedCookers: [buildCooker(0, [2], ['烧烤架'])],
  placedCookerTypeIds: [2],
  placedCookerControllerCount: 1,
}));
assert.deepEqual([...unlockedSets.usableCookerNames], ['烧烤架']);
assert.deepEqual([...unlockedSets.runtimeUnavailableCookerNames], [],
  'A fresh complete snapshot must clear the prior locked-type projection.');

const completePool = buildAutomationCookerPool(completeRuntime);
assert.deepEqual(completePool.slots.map((slot) => slot.controllerIndex), [1],
  'Only automationAvailable controllers may enter the real-time slot pool.');
assert.equal(completePool.slots[0].controllerIdentity, '0x1001',
  'The real-time slot pool dropped the native controller identity.');
assert.deepEqual(completePool.slots[0].gridPosition, { x: 1, y: 0, z: 0 },
  'The real-time slot pool dropped the exact dictionary position.');
const completeCapacity = buildAutomationCookerCapacity(completePool);
assert.equal(getCookerSlotCapacity('烧烤架', completeCapacity), 1);
assert.equal(getCookerSlotCapacity('煮锅', completeCapacity), 0,
  'A known unavailable physical cooker type must retain zero automation capacity.');
assert.equal(getCookerSlotCapacity('蒸锅', completeCapacity), 0,
  'An unknown cooker type must fail closed instead of receiving a synthetic slot.');

const exactEmptyDeskRuntime = buildRuntime({
  placedCookers: [
    buildCooker(0, [1], ['煮锅']),
    buildCooker(2, [2], ['烧烤架']),
  ],
  placedCookerTypeIds: [1, 2],
  placedCookerControllerCount: 3,
  placedCookerEmptyControllerCount: 1,
});
assert.equal(validateRecommendationCookerSnapshot(exactEmptyDeskRuntime), '',
  'An exact empty desk must not make a complete controller snapshot invalid.');
const exactEmptyDeskPool = buildAutomationCookerPool(exactEmptyDeskRuntime);
assert.equal(exactEmptyDeskPool.snapshotComplete, true);
assert.deepEqual(exactEmptyDeskPool.slots.map((slot) => slot.controllerIndex), [0, 2],
  'An exact empty desk must preserve source controller indexes without creating capacity.');
assert.equal(getCookerSlotCapacity('煮锅', buildAutomationCookerCapacity(exactEmptyDeskPool)), 1);
assert.equal(getCookerSlotCapacity('烧烤架', buildAutomationCookerCapacity(exactEmptyDeskPool)), 1);

const allExactEmptyDesksRuntime = buildRuntime({
  placedCookerControllerCount: 3,
  placedCookerEmptyControllerCount: 3,
});
assert.equal(validateRecommendationCookerSnapshot(allExactEmptyDesksRuntime), '',
  'An all-empty exact controller snapshot must remain complete.');
const allExactEmptyDesksPool = buildAutomationCookerPool(allExactEmptyDesksRuntime);
assert.equal(allExactEmptyDesksPool.snapshotComplete, true);
assert.deepEqual(allExactEmptyDesksPool.slots, [],
  'Exact empty desks must not create automation capacity.');

const missingEmptyCountRuntime = buildRuntime();
delete missingEmptyCountRuntime.placedCookerEmptyControllerCount;
assert.match(
  validateRecommendationCookerSnapshot(missingEmptyCountRuntime),
  /placedCookerEmptyControllerCount/,
  'The new exact empty-controller wire field must be required.',
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookerEmptyControllerCount: -1,
  })),
  /placedCookerEmptyControllerCount/,
  'A negative exact empty-controller count must be rejected.',
);

const missingLockedCountRuntime = buildRuntime();
delete missingLockedCountRuntime.placedCookerLockedControllerCount;
assert.match(
  validateRecommendationCookerSnapshot(missingLockedCountRuntime),
  /placedCookerLockedControllerCount/,
  'The exact locked-controller wire field must be required.',
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookerLockedControllerCount: -1,
  })),
  /placedCookerLockedControllerCount/,
  'A negative exact locked-controller count must be rejected.',
);

const unavailableRuntime = buildRuntime({
  placedCookers: [],
  placedCookerTypeIds: [],
  placedCookerSnapshotComplete: false,
  placedCookerControllerCount: 2,
  placedCookerReadFailureCount: 2,
});
assert.equal(validateRecommendationCookerSnapshot(unavailableRuntime), '');
assert.equal(buildRuntimeSets(unavailableRuntime).hasCookerSnapshot, false,
  'An unavailable physical snapshot must not enable missing-cooker hard filtering.');
assert.deepEqual([...buildRuntimeSets(unavailableRuntime).runtimeUnavailableCookerNames], [],
  'An unavailable snapshot must not infer an entire cooker type is runtime unavailable.');
assert.deepEqual(buildAutomationCookerPool(unavailableRuntime).slots, [],
  'An unavailable snapshot must expose no automation capacity.');

const lockedThenFailedRuntime = buildRuntime({
  placedCookers: [],
  placedCookerTypeIds: [],
  placedCookerSnapshotComplete: false,
  placedCookerControllerCount: 2,
  placedCookerLockedControllerCount: 1,
  placedCookerReadFailureCount: 1,
});
assert.equal(validateRecommendationCookerSnapshot(lockedThenFailedRuntime), '',
  'An unavailable round may retain only the safely classified locked count for diagnostics.');
assert.equal(buildRuntimeSets(lockedThenFailedRuntime).hasCookerSnapshot, false);
assert.deepEqual(buildAutomationCookerPool(lockedThenFailedRuntime).slots, [],
  'A diagnostic locked count must not expose partial capacity from an unavailable round.');
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [buildCooker(0, [4], ['蒸锅'])],
    placedCookerTypeIds: [4],
    placedCookerSnapshotComplete: false,
    placedCookerControllerCount: 2,
    placedCookerReadFailureCount: 1,
  })),
  /包含部分控制器/,
  'A partial controller snapshot must be rejected instead of retaining legacy partial capacity.',
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [buildCooker(0, [1], ['煮锅'])],
    placedCookerTypeIds: [1],
    placedCookerControllerCount: 3,
    placedCookerEmptyControllerCount: 1,
  })),
  /emptyControllerCount/,
  'A controller total that is not closed by placed, empty, and failed counts must be rejected.',
);

const malformedRuntime = buildRuntime({
  placedCookers: [
    buildCooker(0, [2], ['烧烤架'], {
      automationAvailable: true,
      automationAvailability: 'Unavailable',
    }),
  ],
  placedCookerTypeIds: [2],
  placedCookerControllerCount: 1,
});
assert.match(
  validateRecommendationCookerSnapshot(malformedRuntime),
  /自动化可用状态不一致/,
  'Contradictory protocol fields must be rejected before replacing the current snapshot.',
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [buildCooker(2, [2], ['烧烤架'])],
    placedCookerTypeIds: [2],
    placedCookerControllerCount: 1,
  })),
  /controllerIndex/,
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [buildCooker(0, [], [])],
    placedCookerTypeIds: [],
    placedCookerControllerCount: 1,
  })),
  /typeIds/,
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [
      buildCooker(0, [1], ['料理台'], { name: '料理台' }),
    ],
    placedCookerTypeIds: [1],
    placedCookerControllerCount: 1,
  })),
  /厨具名称与 typeIds 不一致/,
  'Display names must not create cooker capabilities that contradict the exact type IDs.',
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [buildCooker(0, [2], ['烧烤架'])],
    placedCookerTypeIds: [2],
    placedCookerSnapshotComplete: false,
    placedCookerControllerCount: 1,
  })),
  /包含部分控制器/,
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [
      buildCooker(0, [2], ['烧烤架'], { couldOpen: false }),
    ],
    placedCookerTypeIds: [2],
    placedCookerControllerCount: 1,
  })),
  /已锁定或不可开/,
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [
      buildCooker(0, [2], ['烧烤架'], { controllerIdentity: '' }),
    ],
    placedCookerTypeIds: [2],
    placedCookerControllerCount: 1,
  })),
  /controllerIdentity/,
  'Missing native controller identity must reject the wire snapshot.',
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [
      buildCooker(0, [2], ['烧烤架'], { controllerIdentity: '0x0' }),
    ],
    placedCookerTypeIds: [2],
    placedCookerControllerCount: 1,
  })),
  /controllerIdentity/,
  'The zero native controller identity must reject the wire snapshot.',
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [
      buildCooker(0, [2], ['烧烤架']),
      buildCooker(1, [2], ['烧烤架'], { gridPosition: { x: 0, y: 0, z: 0 } }),
    ],
    placedCookerTypeIds: [2],
    placedCookerControllerCount: 2,
  })),
  /重复 gridPosition/,
  'Duplicate dictionary positions must reject the wire snapshot.',
);
assert.match(
  validateRecommendationCookerSnapshot(buildRuntime({
    placedCookers: [
      buildCooker(0, [2], ['烧烤架'], { challengeLocked: true }),
    ],
    placedCookerTypeIds: [2],
    placedCookerControllerCount: 1,
  })),
  /已锁定或不可开/,
  'A challenge-locked controller must never be published in placedCookers.',
);

const sharedControllerRuntime = buildRuntime({
  placedCookers: [
    buildCooker(0, [1, 2], ['煮锅', '烧烤架']),
  ],
  placedCookerTypeIds: [1, 2],
  placedCookerControllerCount: 1,
});
const sharedPool = buildAutomationCookerPool(sharedControllerRuntime);
const sharedCycle = buildCycle();
assert.equal(
  reserveAutomationCookerSlot(sharedCycle, requirement('煮锅'), '订单 A', sharedPool).ok,
  true,
);
assert.equal(
  reserveAutomationCookerSlot(sharedCycle, requirement('烧烤架'), '订单 B', sharedPool).ok,
  false,
  'One multi-type controller must not be reserved twice through different type keys.',
);

const specializedRuntime = buildRuntime({
  placedCookers: [
    buildCooker(0, [1, 2], ['煮锅', '烧烤架']),
    buildCooker(1, [2], ['烧烤架']),
  ],
  placedCookerTypeIds: [1, 2],
  placedCookerControllerCount: 2,
});
const specializedPool = buildAutomationCookerPool(specializedRuntime);
const specializedCycle = buildCycle();
const grillReservation = reserveAutomationCookerSlot(
  specializedCycle,
  requirement('烧烤架'),
  '烧烤订单',
  specializedPool,
);
const boilReservation = reserveAutomationCookerSlot(
  specializedCycle,
  requirement('煮锅'),
  '煮锅订单',
  specializedPool,
);
assert.equal(grillReservation.controllerIndex, 1,
  'A compatible controller with fewer supported types must be selected first.');
assert.equal(boilReservation.controllerIndex, 0,
  'The multi-type controller must remain available for the later exclusive requirement.');

const headOfLinePool = buildAutomationCookerPool(buildRuntime({
  placedCookers: [
    buildCooker(0, [2], ['烧烤架']),
  ],
  placedCookerTypeIds: [2],
  placedCookerControllerCount: 2,
  placedCookerLockedControllerCount: 1,
}));
const headOfLineCycle = buildCycle();
const admitted = [];
for (const [label, cooker] of [
  ['锁定煮锅订单', requirement('煮锅')],
  ['开放烧烤订单', requirement('烧烤架')],
]) {
  const reservation = reserveAutomationCookerSlot(headOfLineCycle, cooker, label, headOfLinePool);
  if (!reservation.ok) continue;
  admitted.push(label);
  if (admitted.length >= 1) break;
}
assert.deepEqual(admitted, ['开放烧烤订单'],
  'A zero-capacity head order must not consume concurrency before later executable types are scanned.');

await assertSourceContracts();
console.log('PASS: cooker automation uses exact controller slots, fail-closed capacity, and post-capacity concurrency admission.');

async function assertSourceContracts() {
  const [
    cookers,
    automation,
    workbench,
    connection,
    servicePanel,
    specialRegistry,
    api,
    orderPreparationModels,
    localApiServer,
    cookerReflection,
    cookerSnapshot,
    cookerHighlight,
    runtimeService,
    cooking,
  ] = await Promise.all([
    readFile(new URL('apps/companion/src/companion/domain/cookers.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/automation.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/hooks/useCompanionConnection.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/pages/ModServicePanel.tsx', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/special-business/registry.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/api.ts', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/LocalApi/OrderPreparationModels.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/LocalApi/LocalApiServer.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/RuntimeCookerReflection.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/RuntimeCookerSnapshotService.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/RuntimeCookerHighlightService.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.cs', root), 'utf8'),
    readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.Cooking.cs', root), 'utf8'),
  ]);

  assert.equal(cookers.includes('Math.max(1, capacity.get(key) ?? 1)'), false);
  assert.equal(cookers.includes('runtime.placedCookerTypeIds ?? []'), false);
  assert.ok(cookers.includes('if (cooker.automationAvailable !== true) continue;'));
  assert.ok(cookers.includes(
    'if (cooker.couldOpen !== true || cooker.challengeLocked !== false) continue;',
  ));
  assert.ok(cookers.includes('runtimeSets.runtimeUnavailableCookerNames.has(normalized)'));
  assert.ok(cookers.includes('left.supportedKeys.length - right.supportedKeys.length'));
  const supportedKeyBuilder = sourceSlice(
    cookers,
    'function buildCookerSupportedKeySet(',
    'function isSnapshotCount(',
  );
  assert.equal(supportedKeyBuilder.includes('typeNames'), false,
    'Automation capacity must derive only from exact cooker type IDs.');
  assert.equal(automation.includes('if (selections.length >= limit) break;'), false,
    'Rare candidate discovery must not truncate before cooker admission.');
  assert.match(
    workbench,
    /toCookerControllerReservation\(schedulerNote\)[\s\S]*if \(!schedulerNote\.ok \|\| \(shouldPrepareFood && cookerReservation == null\)\) \{\s*shouldPrepareFood = false;\s*cookerReservation = null;\s*\}[\s\S]*const hasExecutableCandidateAction[\s\S]*admittedCandidateCount \+= 1;/,
    'Rare concurrency must be consumed only after cooker admission.',
  );
  const preflightStart = workbench.indexOf('const canAttemptCompletionPreflight');
  const preflightEnd = workbench.indexOf('const hasExecutableCandidateAction', preflightStart);
  const preflightSource = workbench.slice(preflightStart, preflightEnd);
  const preflightRequestSource = sourceSlice(
    workbench,
    'if (canAttemptCompletionPreflight) {',
    'const completeResponseAt = Date.now();',
  );
  assert.equal(preflightSource.includes('currentState.beverageHandled'), false);
  assert.equal(preflightSource.includes('hasServedBeverage'), false,
    'Beverage-only progress must not let a locked cooking order consume rare concurrency.');
  assert.ok(connection.includes('validateRecommendationCookerSnapshot(data.recommendationState)'));
  assert.ok(servicePanel.includes("if (!applicable) return '不适用';"));
  assert.ok(servicePanel.includes("locked || '未摆放'"));
  assert.ok(servicePanel.includes('个厨具被事件锁定'));
  assert.ok(servicePanel.includes('`读取不可用${runtime.placedCookerStatus'));
  assert.equal(servicePanel.includes('部分读取'), false,
    'Fail-closed cooker snapshots must not retain the removed partial-read UI path.');
  const workbenchRuntimeSignature = sourceSlice(
    workbench,
    'function buildNormalOrderDetailRuntimeSignature(',
    'function buildNormalOrderDetailSpecialBusinessSignature(',
  );
  const specialRuntimeSignature = sourceSlice(
    specialRegistry,
    'function buildRuntimeSignature(',
    'function buildPreferenceSignature(',
  );
  for (const signature of [workbenchRuntimeSignature, specialRuntimeSignature]) {
    assert.ok(signature.includes('runtime.placedCookerSnapshotComplete ? 1 : 0'));
    assert.ok(signature.includes('runtime.placedCookerControllerCount'));
    assert.ok(signature.includes('runtime.placedCookerEmptyControllerCount'));
    assert.ok(signature.includes('runtime.placedCookerLockedControllerCount'));
    assert.ok(signature.includes('runtime.placedCookerReadFailureCount'));
    assert.equal(signature.includes('couldOpen'), false);
    assert.equal(signature.includes('automationAvailable'), false);
    assert.equal(signature.includes('automationAvailabilityDiagnostic'), false);
  }
  const placedCookerSignature = sourceSlice(
    workbench,
    'function buildPlacedCookerSignature(',
    'function stableNumberArraySignature(',
  );
  const placedCookerSemanticSignature = sourceSlice(
    specialRegistry,
    'function buildPlacedCookerSemanticSignature(',
    'function compareOrdinal(',
  );
  for (const signature of [placedCookerSignature, placedCookerSemanticSignature]) {
    assert.ok(
      signature.includes('cooker.controllerIndex')
        && signature.includes('cooker.controllerIdentity')
        && signature.includes('cooker.gridPosition.x')
        && signature.includes('cooker.challengeLocked ? 1 : 0')
        && signature.includes('cooker.couldOpen ? 1 : 0'),
      'Recommendation Worker/cache signatures must include exact controller identity, position, and lock state.',
    );
  }

  const prepareRareApi = sourceSlice(
    api,
    'export async function prepareNextRareOrder(',
    'export async function completeFirstRareOrder(',
  );
  const completeRareApi = sourceSlice(
    api,
    'export async function completeFirstRareOrder(',
    'export async function completeFirstNormalOrder(',
  );
  const completeNormalApi = sourceSlice(
    api,
    'export async function completeFirstNormalOrder(',
    'export async function readFavorites(',
  );
  const rareOrderAction = sourceSlice(
    api,
    'async function rareOrderAction(',
    'function buildRareOrderExecutionReason(',
  );
  for (const source of [prepareRareApi, completeRareApi]) {
    assert.match(source, /cookerReservation: CookerControllerReservation \| null/);
    assert.match(source, /authorityRevision: number/);
    assert.match(source, /preferences,\s*cookerReservation,\s*authorityRevision,\s*\);/);
  }
  for (const source of [completeNormalApi, rareOrderAction]) {
    assert.match(source, /cookerReservation: CookerControllerReservation \| null/);
    assert.ok(source.includes('appendCookerReservation(params, cookerReservation)'));
  }
  const appendReservation = sourceSlice(
    api,
    'function appendCookerReservation(',
    'function buildRareOrderExecutionReason(',
  );
  for (const field of [
    'cookerControllerIndex',
    'cookerControllerIdentity',
    'cookerGridX',
    'cookerGridY',
    'cookerGridZ',
  ]) {
    assert.ok(appendReservation.includes(`params.set('${field}'`),
      `Cooker reservation API dropped ${field}.`);
  }

  assert.match(
    workbench,
    /const schedulerNote: CookerReservationResult = shouldPrepareFood[\s\S]*toCookerControllerReservation\(schedulerNote\)[\s\S]*if \(!schedulerNote\.ok \|\| \(shouldPrepareFood && cookerReservation == null\)\)[\s\S]*prepareNextRareOrder\([\s\S]*shouldPrepareFood \? cookerReservation : null/,
    'Rare cooking must pass the exact index, native identity, and grid position returned by the current reservation.',
  );
  assert.match(
    preflightRequestSource,
    /completeFirstRareOrder\([\s\S]*autoPrepStartCooking: false,[\s\S]*null,\s*companionDeviceAuthority\.authorityRevision,\s*\);/,
    'A rare completion preflight must explicitly disable cooking and carry no cooker reservation.',
  );
  const immediateCompletionSource = sourceSlice(
    workbench,
    'const immediateCompleteResponse = await completeFirstRareOrder(',
    'const immediateCompleteResponseAt = Date.now();',
  );
  assert.match(
    immediateCompletionSource,
    /autoPrepStartCooking: false,[\s\S]*null,\s*companionDeviceAuthority\.authorityRevision,\s*\);/,
    'An immediate rare completion request must explicitly disable cooking and carry no cooker reservation.',
  );
  assert.match(
    workbench,
    /const cookerReservationByOrderKey = new Map<string, CookerControllerReservation>\(\);[\s\S]*toCookerControllerReservation\(reservation\)[\s\S]*cookerReservationByOrderKey\.set\(orderKey, exactReservation\);[\s\S]*completeFirstNormalOrder\([\s\S]*requestPreferences\.autoNormalStartCooking\s*\? cookerReservationByOrderKey\.get\(orderKey\) \?\? null\s*: null,/,
    'Normal scheduling must retain each first-pass reservation by order key and reuse it for that order request.',
  );

  assert.match(
    orderPreparationModels,
    /public int CookerControllerIndex \{ get; init; \} = -1;[\s\S]*public string CookerControllerIdentity \{ get; init; \} = "";[\s\S]*public int\? CookerGridX[\s\S]*public int\? CookerGridY[\s\S]*public int\? CookerGridZ/,
    'The parsed order request must carry the exact controller index, native identity, and grid position.',
  );
  assert.match(
    localApiServer,
    /CookerControllerIndex = ReadIntQuery\(query, "cookerControllerIndex", -1\)[\s\S]*CookerControllerIdentity = ReadStringQuery\(query, "cookerControllerIdentity"\)[\s\S]*CookerGridX = ReadNullableIntQuery\(query, "cookerGridX"\)[\s\S]*CookerGridY = ReadNullableIntQuery\(query, "cookerGridY"\)[\s\S]*CookerGridZ = ReadNullableIntQuery\(query, "cookerGridZ"\)/,
    'The Local API must parse every exact cooker reservation field without aliases.',
  );

  const exactControllerEntries = sourceSlice(
    cookerReflection,
    'public static bool TryReadCookerControllerEntriesFromCookSystem(',
    'public static bool TryReadLockedCookerPositions(',
  );
  assert.match(
    exactControllerEntries,
    /TryGetExactAllCookersShape\([\s\S]*entry\.Key\.GetType\(\) != keyType[\s\S]*entry\.Value is not Il2CppObjectBase controller[\s\S]*controller\.Pointer == IntPtr\.Zero/,
    'AllCookers must retain its exact Vector3Int/CookController key, value, and native-pointer contract.',
  );
  assert.match(
    exactControllerEntries,
    /IReadOnlySet<RuntimeCookerGridPosition> lockedPositions[\s\S]*TryReadExactVector3Int\(entry\.Key,[\s\S]*locked-grid-missing[\s\S]*if \(lockedPositions\.Contains\(entry\.GridPosition\)\)[\s\S]*continue;[\s\S]*TryReadControllerGridPosition\([\s\S]*entry\.GridPosition != controllerPosition/,
    'Locked dictionary keys must skip unsafe controller getters; every unlocked key must match its controller GridPosition.',
  );
  assert.match(
    exactControllerEntries,
    /result\.Sort\([\s\S]*GridPosition\.X[\s\S]*GridPosition\.Y[\s\S]*GridPosition\.Z[\s\S]*ControllerIdentity/,
    'Controller indexes must come from one deterministic coordinate and identity ordering.',
  );
  const exactLockedPositions = sourceSlice(
    cookerReflection,
    'public static bool TryReadLockedCookerPositions(',
    'private static bool TryReadControllerGridPosition(',
  );
  assert.match(
    exactLockedPositions,
    /TryGetExactMonoSingletonInstance\(eventManagerType,[\s\S]*"get_LockedCookers"[\s\S]*TryGetClosedStructArrayElementType\([\s\S]*Vector3IntTypeName/,
    'Challenge locks must come from the exact EventManager LockedCookers struct array.',
  );
  assert.doesNotMatch(
    exactLockedPositions,
    /LockedCookersRaw|GetStaticMemberValue|Enumerate|IEnumerable/,
    'Challenge lock reads must not restore raw-field, broad singleton, or generic enumeration paths.',
  );
  const exactSingleton = sourceSlice(
    cookerReflection,
    'private static bool TryGetExactMonoSingletonInstance(',
    'private static bool TryGetExactAllCookersShape(',
  );
  assert.match(
    exactSingleton,
    /concreteType\.BaseType[\s\S]*GetGenericTypeDefinition\(\)[\s\S]*definition\.FullName != MonoSingletonTypeName[\s\S]*arguments\[0\] != concreteType[\s\S]*BindingFlags\.DeclaredOnly[\s\S]*"get_Instance"/,
    'CookSystemManager and EventManager must resolve only their direct closed MonoSingleton<T> base getter.',
  );
  assert.doesNotMatch(
    cookerReflection,
    /GetStaticMemberValue\(.*Instance/,
    'Cooker reflection must not use the broad static singleton reader.',
  );
  const snapshotRead = sourceSlice(
    cookerSnapshot,
    'private static RuntimeCookerSnapshotReadResult ReadPlacedCookers(',
    'private static string SanitizeDiagnostic(',
  );
  assert.match(
    snapshotRead,
    /TryReadLockedCookerPositions\([\s\S]*TryReadCookerControllerEntriesFromCookSystem\(\s*cookSystem,\s*lockedPositions,[\s\S]*lockedControllerCount = controllerEntries\.Count\([\s\S]*lockedPositions\.Contains\(entry\.GridPosition\)[\s\S]*continue;[\s\S]*TryReadCookerControllerState\([\s\S]*if \(!controllerState\.CouldOpen\)[\s\S]*RuntimeCookerSnapshotReadResult\.Unavailable/,
    'A snapshot round must classify exact LockedCookers keys before touching only the remaining live controllers.',
  );
  assert.match(
    snapshotRead,
    /GridPosition = new CookerGridPosition[\s\S]*ControllerIdentity = entry\.ControllerIdentity[\s\S]*ChallengeLocked = false[\s\S]*cookers\.Count \+ emptyControllerCount \+ lockedControllerCount != controllerEntries\.Count[\s\S]*LockedControllerCount = lockedControllerCount/,
    'Only unlocked controllers may be published, and complete counts must close across placed, empty, and locked controllers.',
  );
  const highlightScan = sourceSlice(
    cookerHighlight,
    'private static void ScanAndApply(',
    'private static IEnumerable<SpriteRenderer> ReadCookerRenderers(',
  );
  assert.match(
    highlightScan,
    /TryReadLockedCookerPositions\([\s\S]*TryReadCookerControllerEntriesFromCookSystem\(\s*cookSystem,\s*lockedPositions,[\s\S]*lockedPositions\.Contains\(entry\.GridPosition\)[\s\S]*continue;[\s\S]*TryReadCookerControllerState\([\s\S]*if \(!state\.CouldOpen\)[\s\S]*if \(state\.IsEmptyDesk\) continue;[\s\S]*ReadCookerRenderers\(entry\.Controller\)[\s\S]*openRenderers\.AddRange\(controllerRenderers\)[\s\S]*var claims = RuntimeUiTargetKinds\.None;[\s\S]*if \(HasCookerHighlightTargets\(targetSet\)\)[\s\S]*foreach \(var cookerTypeId in state\.TypeIds\)[\s\S]*claims \|= targetSet\.GetCookerClaims\(cookerTypeId\);[\s\S]*if \(claims != RuntimeUiTargetKinds\.None\)[\s\S]*existing\.Claims \|= claims[\s\S]*new TargetRenderer\(renderer, pointer, claims\)/,
    'Cooker highlighting must skip locked keys, collect only fresh open renderers, and merge rare/normal claims for every matching cooker type.',
  );
  assert.match(
    highlightScan,
    /RestoreRetainedBaselinesLocked\(openRenderers\);[\s\S]*if \(!HasCookerHighlightTargets\(targetSet\)\)[\s\S]*foreach \(var targetRenderer in targetRenderers\.Values\)/,
    'Post-event highlighting must restore surviving old-target baselines before disabling or applying the claim-bearing target renderer set.',
  );
  assert.match(
    highlightScan,
    /if \(!string\.IsNullOrWhiteSpace\(error\)\)[\s\S]*RestoreAllLocked\(\);[\s\S]*_status = \$"error:/,
    'Any uncertain highlighter source must restore all previously highlighted renderers.',
  );
  assert.match(
    runtimeService,
    /TryStartCooking\([\s\S]*request\.CookerControllerIndex[\s\S]*request\.CookerControllerIdentity[\s\S]*request\.CookerGridX[\s\S]*request\.CookerGridY[\s\S]*request\.CookerGridZ[\s\S]*TryStartCooking\([\s\S]*request\.CookerControllerIndex[\s\S]*request\.CookerControllerIdentity[\s\S]*request\.CookerGridX[\s\S]*request\.CookerGridY[\s\S]*request\.CookerGridZ/,
    'Both rare and normal cooking paths must forward the parsed cooker reservation triple.',
  );
  const cookingJob = sourceSlice(
    runtimeService,
    'private sealed class AutomationCookingJob',
    'private sealed class CookingCollectionTarget',
  );
  assert.match(
    cookingJob,
    /public RuntimeCookerReservation CookerReservation \{ get; init; \}/,
    'An active cooking job must retain the exact managed reservation used at SetCook.',
  );
  assert.doesNotMatch(
    cookingJob,
    /object CookController/,
    'An active cooking job must not retain its start-time IL2CPP controller wrapper.',
  );

  const existingJobBinding = sourceSlice(
    cooking,
    'private static bool TryReacquireAutomationCooker(',
    'private static (bool Remove, string Message, string Code) HandleAutomationCookerReacquireFailure(',
  );
  assert.match(
    existingJobBinding,
    /TryReadLockedCookerPositions\([\s\S]*TryReadCookerControllerEntriesFromCookSystem\(\s*cookSystem,\s*lockedPositions,[\s\S]*job\.CookerReservation\.TryMatch\([\s\S]*lockedPositions\.Contains\(job\.CookerReservation\.GridPosition\)[\s\S]*controllerPointer != job\.ControllerPointer[\s\S]*TryReadCookerControllerState\([\s\S]*job\.CookerReservation\.EvaluateChallengeGate\(/,
    'An existing job must reject a challenge-locked reservation before reading its controller state.',
  );
  assert.match(
    existingJobBinding,
    /ownershipBefore != ownershipAfter[\s\S]*ownershipAfter\.Generation == job\.Generation[\s\S]*ownershipAfter\.ContentRevision == job\.ContentRevision[\s\S]*if \(!ownershipMatches\)/,
    'An existing job must reject ownership drift before publishing a fresh wrapper.',
  );
  const existingJobProcessor = sourceSlice(
    cooking,
    'private static (bool Remove, string Message, string Code) TryProcessAutomationCookingJob(',
    'private static (bool Remove, string Message, string Code) EnterManualHandoff(',
  );
  assert.match(
    existingJobProcessor,
    /TryReacquireAutomationCooker\([\s\S]*cookerBinding\.State[\s\S]*cookerBinding\.Ownership/,
    'An existing job must fresh-bind before reading content and ownership.',
  );
  assert.doesNotMatch(
    existingJobProcessor,
    /job\.CookController/,
    'Existing-job polling must not touch a retained native wrapper.',
  );

  const reservedCookerSelection = sourceSlice(
    cooking,
    'RuntimeCookerControllerState? ControllerState,\n        string Message) TryGetCookerFromCookSystem(',
    'private static bool TryRevalidateCookerBeforeStart(',
  );
  assert.match(
    reservedCookerSelection,
    /TryReadLockedCookerPositions\([\s\S]*TryReadCookerControllerEntriesFromCookSystem\(\s*cookSystem,\s*lockedPositions,[\s\S]*reservation\.TryMatch\([\s\S]*lockedPositions\.Contains\(reservation\.GridPosition\)[\s\S]*var cookController = controllerEntry\.Controller/,
    'The Mod must reject a locked exact reserved index, identity, and grid position before reading controller state.',
  );
  assert.match(
    reservedCookerSelection,
    /lockedPositions\.Contains\(reservation\.GridPosition\)[\s\S]*TryReadCookerControllerState\([\s\S]*reservation\.EvaluateChallengeGate\([\s\S]*RuntimeCookerChallengeGateState\.Inconsistent[\s\S]*controllerState\.TypeIds\.Contains\(recipeCookerType\)[\s\S]*IsCookControllerReserved\(cookController, out var reservationDiagnostic\)[\s\S]*RuntimeCookerStartAvailabilityService\.Classify\(/,
    'Only an unlocked exact reserved controller may pass state, open gate, type, Mod reservation, and shared native availability checks.',
  );
  assert.doesNotMatch(
    reservedCookerSelection,
    /foreach|selectedController|\?\?= cookController/,
    'The Mod must not scan for or fall back to another compatible controller.',
  );
  assert.match(
    reservedCookerSelection,
    /自动化不会改选其他厨具/,
    'Exact-controller drift must remain a local waiting result instead of selecting an alternate cooker.',
  );
  const cookerRevalidation = sourceSlice(
    cooking,
    'private static bool TryRevalidateCookerBeforeStart(',
    'private static bool IsCookControllerReserved(',
  );
  assert.match(
    cookerRevalidation,
    /TryGetCookerFromCookSystem\([\s\S]*reservation[\s\S]*IsSameObject\(cookController, current\.CookController\)[\s\S]*IsSameObject\(selectedCooker, current\.ControllerState\.Cooker\)/,
    'Every repeated validation must fresh-read the reservation and retain both controller and bound-cooker identity.',
  );
  const cookerReservation = sourceSlice(
    cooking,
    'private static bool IsCookControllerReserved(',
    'private static string DescribeCookController(',
  );
  assert.match(
    cookerReservation,
    /job\.HoldsControllerReservation[\s\S]*job\.ControllerPointer == controllerPointer[\s\S]*ownerJob=/,
    'Physical slot scheduling must report only the exact job holding the explicit Mod controller lease.',
  );
  assert.doesNotMatch(
    cookerReservation,
    /!job\.ManualHandoffObserved/,
    'A passive order receipt must not remain a cooker reservation through the legacy handoff predicate.',
  );
  const cookerStart = sourceSlice(
    cooking,
    'private static CookingStartResult TryStartCooking(',
    'private static CookingStartResult BlockCookingStartUnowned(',
  );
  assert.match(
    cookerStart,
    /RuntimeCookerReservation\.TryCreate\([\s\S]*cookerControllerIndex[\s\S]*cookerControllerIdentity[\s\S]*cookerGridX[\s\S]*cookerGridY[\s\S]*cookerGridZ/,
    'A cooking action must reject incomplete reservation identity before selecting a controller.',
  );
  const preMaterialValidation = cookerStart.indexOf('TryRevalidateCookerBeforeStart(');
  const materialDeduction = cookerStart.indexOf('InvokeRuntimeStorageOut("IngredientOut"');
  const finalValidation = cookerStart.indexOf(
    'TryRevalidateCookerBeforeStart(',
    preMaterialValidation + 1,
  );
  const setCook = cookerStart.indexOf('InvokeInstance(cookController, "SetCook"');
  assert.ok(
    preMaterialValidation >= 0
      && materialDeduction > preMaterialValidation
      && finalValidation > materialDeduction
      && setCook > finalValidation,
    'The same exact reservation must be revalidated before material deduction and immediately before SetCook.',
  );
  assert.match(
    cookerStart.slice(finalValidation, setCook),
    /BlockCookingStartUnowned\([\s\S]*Mod 未调用 SetCook/,
    'Reservation drift after material deduction must stop before SetCook and enter an authoritative safety barrier.',
  );
  assert.equal(
    cookerStart.slice(finalValidation, setCook).includes('TryGetCookerFromCookSystem('),
    false,
    'The SetCook caller must not perform an alternate selection after final reservation validation.',
  );
  assert.equal(
    cookerReflection.includes('TryReadCookerControllersFromCookSystem'),
    false,
    'The removed index-only controller projection must not remain as a production wrapper.',
  );
}

function buildRuntime(overrides = {}) {
  return {
    availableRecipeIds: [],
    availableBeverageIds: [],
    availableIngredientIds: [],
    ownedIngredientQty: {},
    ownedBeverageQty: {},
    placedCookerTypeIds: [],
    placedCookers: [],
    placedCookerSnapshotComplete: true,
    placedCookerControllerCount: 0,
    placedCookerEmptyControllerCount: 0,
    placedCookerLockedControllerCount: 0,
    placedCookerReadFailureCount: 0,
    placedCookerStatus: 'test',
    popularFoodTag: null,
    popularHateFoodTag: null,
    famousShopEnabled: false,
    ...overrides,
  };
}

function buildCooker(
  controllerIndex,
  typeIds,
  typeNames,
  overrides = {},
) {
  return {
    controllerIndex,
    gridPosition: { x: controllerIndex, y: 0, z: 0 },
    controllerIdentity: `0x${(0x1000 + controllerIndex).toString(16).toUpperCase()}`,
    typeIds,
    typeNames,
    name: typeNames.join('/'),
    challengeLocked: overrides.couldOpen === false,
    couldOpen: true,
    automationAvailable: true,
    automationAvailability: 'StrictIdle',
    automationAvailabilityDiagnostic: 'audit',
    source: 'audit',
    ...overrides,
  };
}

function buildCycle() {
  return {
    bucket: 1,
    usedControllerIndexes: new Set(),
    labelsByControllerIndex: new Map(),
  };
}

function requirement(key) {
  return { key, label: key };
}

function sourceSlice(source, startToken, endToken) {
  const start = source.indexOf(startToken);
  const end = source.indexOf(endToken, start + startToken.length);
  assert.ok(start >= 0, `Source token not found: ${startToken}`);
  assert.ok(end > start, `Source boundary not found: ${startToken} -> ${endToken}`);
  return source.slice(start, end);
}
