import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const readSource = (relativePath) => fs.readFileSync(path.join(root, relativePath), 'utf8');

const capture = readSource(
  'mods/bepinex/src/Save/RuntimeMissionDiagnosticCapture.cs',
);
const state = readSource(
  'mods/bepinex/src/Save/RuntimeMissionDiagnosticState.cs',
);
const definitionReader = readSource(
  'mods/bepinex/src/Save/RuntimeMissionDefinitionDiagnosticReader.cs',
);
const presentationModel = readSource(
  'mods/bepinex/src/Save/RuntimeMissionPresentation.cs',
);
const presentationReader = readSource(
  'mods/bepinex/src/Save/RuntimeMissionPresentationReader.cs',
);
const serveCapture = readSource(
  'mods/bepinex/src/Save/RuntimeServeInWorkMissionDiagnosticCapture.cs',
);
const serveState = readSource(
  'mods/bepinex/src/Save/RuntimeServeInWorkMissionDiagnosticState.cs',
);
const serveReconciler = readSource(
  'mods/bepinex/src/Save/RuntimeServeInWorkMissionSignalReconciler.cs',
);
const plugin = readSource(
  'mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs',
);
const localApi = readSource(
  'mods/bepinex/src/LocalApi/LocalApiServer.cs',
);
const trackedSnapshot = readSource(
  'mods/bepinex/src/Save/RuntimeTrackedMissionSnapshot.cs',
);
const trackedPayload = readSource(
  'mods/bepinex/src/LocalApi/LocalApiTrackedMissionsPayload.cs',
);
const missionPriorityProjection = readSource(
  'mods/bepinex/src/Save/RuntimeMissionRecipePriorityProjection.cs',
);
const models = readSource(
  'mods/bepinex/src/Core/Models.cs',
);
const overlay = readSource(
  'mods/bepinex/src/Ui/StewardOverlayController.cs',
);
const recommendationService = readSource(
  'apps/companion/src/companion/domain/service-recommendations.ts',
);
const primaryExecutionPlan = readSource(
  'apps/companion/src/companion/domain/primary-execution-plan.ts',
);

for (const required of [
  '"allNPCs"',
  '"mapData"',
  '"DaySceneNPCLanguage"',
  '"MapLanguageData"',
  '"possibleDestinations"',
  '"mapSpawnMarkerLabels"',
  '"spawnMarker"',
  '"characterId"',
  'RuntimeConcreteCollectionReader.TryGetDictionaryValue(',
  'RuntimeConcreteCollectionReader.TryReadDictionary(',
  'RuntimeConcreteCollectionReader.TryReadReferenceArray(',
  'RuntimeConcreteCollectionReader.TryReadStringArray(',
  'mappedGuestSnapshot.Entries',
  'SourceGuestId',
  'SourceDisplayName',
  'StringComparison.Ordinal',
  'BindingFlags.DeclaredOnly',
]) {
  assert.ok(
    presentationReader.includes(required),
    `Mission presentation reader is missing exact read contract: ${required}`,
  );
}
for (const forbidden of [
  'RefNPC',
  'RefDaySceneName',
  'GetMapLabelFromSpawnMarker',
  'GetMapLanguageData',
  'FindUnityObject',
  'EnumerateObjects',
  'GetMemberValue',
  '.ToString(',
]) {
  assert.ok(
    !presentationReader.includes(forbidden),
    `Mission presentation reader restored forbidden fallback/call: ${forbidden}`,
  );
}
for (const boundary of [
  'MaxReceiverLength = 512',
  'MaxDisplayNameLength = 256',
  'MaxSceneCount = 64',
  'MaxStatusLength = 256',
  'MaxRetryCount = 4',
  'TimeSpan.FromMilliseconds(500)',
  'TimeSpan.FromMilliseconds(1_000)',
  'TimeSpan.FromMilliseconds(2_000)',
  'TimeSpan.FromMilliseconds(4_000)',
  'NoReceiverStatus = "no-receiver"',
  'PendingStatus = "unavailable:pending"',
  'ReadyStatus = "ready"',
]) {
  assert.ok(
    presentationModel.includes(boundary),
    `Mission presentation model is missing bounded contract: ${boundary}`,
  );
}
assert.ok(
  state.includes('PresentationDaySceneGeneration')
    && state.includes('PresentationMappedCapturedAtUtc')
    && state.includes('PresentationAttemptCount')
    && state.includes('PresentationNextAttemptAtUtc')
    && state.includes('TryReadPresentationRequests(')
    && state.includes('TryApplyPresentations(')
    && state.includes('RuntimeMissionPresentationApply')
    && state.includes('_missionsByLabel.TryGetValue(')
    && state.includes('mission.Definition.Receiver')
    && state.includes('result.ReceiverLabel'),
  'Tracked mission presentation data must bind to mission/day/mapped identities.',
);
assert.ok(
  presentationReader.includes('foreach (var receiver in receivers)')
    && presentationReader.includes(
      'RuntimeMissionPresentation.EntryReadUnavailableStatus',
    )
    && presentationReader.includes('ReadMapCatalogCached(')
    && presentationReader.includes('ManagedMapCatalogCache')
    && presentationReader.includes('MissionGeneration')
    && presentationReader.includes('DaySceneGeneration')
    && presentationReader.includes('MappedCapturedAtUtc'),
  'Presentation metadata must isolate each receiver and cache only a generation-bound managed map index.',
);
assert.ok(
  overlay.includes('RuntimeMissionDiagnosticCapture.RefreshPresentations(')
    && overlay.includes('RuntimeMissionPresentationReader.ReadMany('),
  'Tracked and available mission presentation reads must run from the controlled main-thread owner.',
);

const extractStaticMethod = (source, methodName) => {
  const signature = new RegExp(
    `private static[\\s\\S]{0,180}\\b${methodName}\\s*\\(`,
  ).exec(source);
  assert.ok(signature, `Missing static method ${methodName}.`);
  const openBrace = source.indexOf('{', signature.index + signature[0].length);
  assert.notEqual(openBrace, -1, `Missing body for ${methodName}.`);

  let depth = 0;
  for (let index = openBrace; index < source.length; index += 1) {
    if (source[index] === '{') depth += 1;
    if (source[index] === '}') {
      depth -= 1;
      if (depth === 0) return source.slice(openBrace, index + 1);
    }
  }
  assert.fail(`Unterminated body for ${methodName}.`);
};

const extractStaticVoidMethod = (source, methodName) => {
  const signature = `private static void ${methodName}(`;
  const signatureIndex = source.indexOf(signature);
  assert.notEqual(signatureIndex, -1, `Missing static void method ${methodName}.`);
  const openBrace = source.indexOf('{', signatureIndex + signature.length);
  assert.notEqual(openBrace, -1, `Missing body for ${methodName}.`);

  let depth = 0;
  for (let index = openBrace; index < source.length; index += 1) {
    if (source[index] === '{') depth += 1;
    if (source[index] === '}') {
      depth -= 1;
      if (depth === 0) return source.slice(openBrace, index + 1);
    }
  }
  assert.fail(`Unterminated body for ${methodName}.`);
};

const definitionValidation = extractStaticMethod(
  state,
  'DefinitionValidationError',
);
assert.ok(
  definitionValidation.includes(
    'if (stateVerified && finishStateCount != definition.ConditionCount)',
  )
    && definitionValidation.includes('"refreshed-condition-count-mismatch"')
    && !definitionValidation.includes('saved-condition-count-mismatch'),
  'Saved condition booleans must remain diagnostic-only; only verified native state shapes are strict.',
);

for (const hook of [
  'OnTryUpgradePrefix',
  'OnTryUpgradePostfix',
  'OnTryUpgradeFinalizer',
  'OnInitializePrefix',
  'OnInitializePostfix',
  'OnInitializeFinalizer',
  'OnStartMissionPrefix',
  'OnGenerateTrackingDataPostfix',
  'OnStartMissionPostfix',
  'OnStartMissionFinalizer',
  'OnObservedMethodFinalizer',
  'OnRemoveMissionPostfix',
  'OnFinishMissionPostfix',
  'OnSetFinishedMissionPostfix',
  'OnFinishNodePrefix',
  'OnFinishNodePostfix',
  'OnFinishNodeFinalizer',
  'OnUpdateFinishStatesPostfix',
]) {
  const body = extractStaticMethod(capture, hook);
  assert.ok(
    body.includes(`${hook}Unsafe`) && /catch\s*\{\s*\}/.test(body),
    `Mission Harmony callback ${hook} must have an outer no-throw shell.`,
  );
  if (hook.endsWith('Finalizer')) {
    assert.ok(
      body.includes('return __exception;'),
      `Mission finalizer ${hook} must return the original exception.`,
    );
  }
}
for (const hook of ['OnPrefix', 'OnPostfix', 'OnFinalizer']) {
  const body = extractStaticMethod(serveCapture, hook);
  assert.ok(
    body.includes(`${hook}Unsafe`) && /catch\s*\{\s*\}/.test(body),
    `ServeInWork Harmony callback ${hook} must have an outer no-throw shell.`,
  );
  if (hook === 'OnFinalizer') {
    assert.ok(
      body.includes('return __exception;'),
      'ServeInWork finalizer must return the original exception.',
    );
  }
}

assert.ok(
  capture.includes('private const int ExpectedHookCount = 9;')
    && capture.includes('$"patched:{ExpectedHookCount}/{ExpectedHookCount}"'),
  'Mission diagnostics must publish one exact 9-hook contract.',
);
for (const hook of [
  'PatchTryUpgrade(harmony, shape.TryUpgradeSaveVersion);',
  'PatchInitialize(harmony, shape.Initialize);',
  'PatchStartMission(harmony, shape.StartMission);',
  'shape.GenerateTrackingData',
  'shape.RemoveMissionFromList',
  'shape.FinishMission',
  'shape.SetFinishedMissions',
  'PatchFinishNode(harmony, shape.FinishNodeExtern);',
  'shape.UpdateFinishStates',
]) {
  assert.ok(capture.includes(hook), `Stable runtime mission hook ${hook} is missing.`);
}
assert.equal(
  capture.match(/PatchObservedPostfix\(\s*harmony,\s*shape\./g)?.length ?? 0,
  5,
  'Exactly five incremental hooks must share the native-exception finalizer.',
);
assert.ok(
  capture.includes('nameof(OnTryUpgradePrefix)')
    && capture.includes('nameof(OnTryUpgradePostfix)')
    && capture.includes('nameof(OnTryUpgradeFinalizer)')
    && capture.includes('nameof(OnInitializePrefix)')
    && capture.includes('nameof(OnInitializePostfix)')
    && capture.includes('nameof(OnInitializeFinalizer)'),
  'TryUpgradeSaveVersion and Initialize must each use a guarded prefix/postfix/finalizer frame.',
);

assert.ok(
  capture.includes(
    'var tryUpgrade = RequireStaticMethod(\n'
      + '                saveManagementType,\n'
      + '                "TryUpgradeSaveVersion",\n'
      + '                playerSaveFileType,\n'
      + '                playerSaveFileType);',
  ),
  'Load capture must bind the exact PlayerSaveFile TryUpgradeSaveVersion overload.',
);
assert.equal(
  capture.match(/shape\.GenerateSaveString\.Invoke\(/g)?.length ?? 0,
  1,
  'GenerateSaveString must be invoked exactly once by the passive load callback.',
);
assert.equal(
  capture.match(/var rawJson = InvokeGenerateSaveString\(shape, __result\);/g)?.length ?? 0,
  1,
  'The upgraded save result must have one serialization point.',
);
assert.ok(
  capture.includes('private static void OnTryUpgradePrefix(out LoadHookFrame? __state)')
    && !/__result\s*=(?!=)/.test(capture)
    && !capture.includes('shape.TryUpgradeSaveVersion.Invoke(')
    && capture.includes('formattingNone = Enum.ToObject(formattingType, 0);'),
  'Load capture must observe the native return without mutating or reinvoking the game method.',
);
const pendingSeedRecord = capture.slice(
  capture.indexOf('private sealed record PendingLoadSeed('),
  capture.indexOf('private sealed class InitializeHookFrame'),
);
assert.ok(
  pendingSeedRecord.length > 0
    && !pendingSeedRecord.includes('string RawJson')
    && !pendingSeedRecord.includes('string Json'),
  'Raw save JSON must not survive beyond synchronous parsing.',
);
assert.ok(
  capture.includes('LoadJsonLength')
    && capture.includes('LoadJsonSha256')
    && capture.includes('SerializeElapsedMilliseconds')
    && capture.includes('ParseElapsedMilliseconds'),
  'Load diagnostics must retain only bounded metrics after parsing.',
);
assert.ok(
  capture.includes('ArrayPool<byte>.Shared.Return(buffer, clearArray: true);'),
  'The pooled hash buffer must be cleared before it can retain save JSON fragments.',
);

assert.ok(
  capture.includes('RuntimeConcreteCollectionReader.TryReadDictionaryCount(')
    && capture.includes('RuntimeConcreteCollectionReader.TryContainsDictionaryKey(')
    && capture.includes('RuntimeConcreteCollectionReader.TryGetDictionaryValue(')
    && capture.includes('RuntimeConcreteCollectionReader.TryReadList(')
    && !capture.includes('RuntimeConcreteCollectionReader.TryReadDictionary('),
  'Runtime initialization may use only bounded counts, exact known-key lookup, and concrete list indexing.',
);
assert.ok(
  capture.includes('private const int MaxDefinitionReadsPerLoad = 512;')
    && capture.includes('frame.Selection.Tasks.Count > MaxDefinitionReadsPerLoad')
    && capture.includes('"definition-read-count-overflow"'),
  'Definition reads must stop before an oversized load can enter the native reflection loop.',
);
assert.ok(
  state.includes('loaded.RefreshedState.FinishStates')
    && state.includes('Identity: loaded.RefreshedState.Identity')
    && state.includes('CurrentFinishStates: loaded.RefreshedState.FinishStates.ToArray()')
    && state.includes('stateVerified')
    && state.includes('finishStateCount != definition.ConditionCount')
    && state.includes('finishStates.All(value => value)'),
  'Loaded saved flags must be replaced by a verified native refresh before publication.',
);
const initializeCapture = extractStaticVoidMethod(capture, 'CaptureInitializedState');
const generateTrackingPostfix = extractStaticVoidMethod(
  capture,
  'OnGenerateTrackingDataPostfixUnsafe',
);
const startMissionPostfix = extractStaticVoidMethod(
  capture,
  'OnStartMissionPostfixUnsafe',
);
const updateFinishStatesPostfix = extractStaticVoidMethod(
  capture,
  'OnUpdateFinishStatesPostfixUnsafe',
);
const refreshInvoker = extractStaticVoidMethod(capture, 'InvokeTrackedStateRefresh');
const startFrameReadyGate = extractStaticMethod(
  capture,
  'IsCurrentReadyStartFrame',
);
const startFrameResolver = capture.slice(
  capture.indexOf('private static bool TryResolveStartFrameForRefresh('),
  capture.indexOf('private static bool TryPopStartFrame(', capture.indexOf(
    'private static bool TryResolveStartFrameForRefresh(',
  )),
);
assert.equal(
  capture.match(/InvokeTrackedStateRefresh\(/g)?.length ?? 0,
  3,
  'Controlled task refresh must have exactly two callers and one implementation.',
);
assert.ok(
  initializeCapture.includes('foreach (var boundMission in boundMissions)')
    && initializeCapture.includes(
      'InvokeTrackedStateRefresh(boundMission.Instance);',
    )
    && initializeCapture.includes('"tracking-refresh-bucket-count-changed"')
    && initializeCapture.includes('"tracking-refresh-buffer-count-changed"')
    && initializeCapture.includes('"tracking-refresh-finished-list-changed"')
    && initializeCapture.includes('"tracking-refresh-identity-changed"'),
  'Load initialization must refresh only the exact bound tasks and reject any container or identity drift.',
);
const initializeRefreshIndex = initializeCapture.indexOf(
  'InvokeTrackedStateRefresh(boundMission.Instance);',
);
for (const preflightGate of [
  'finalTrackingCount != frame.Selection.Buckets.Count',
  'finalBufferCount != 0',
  'HaveSameLabelMultiset(',
]) {
  assert.ok(
    initializeCapture.includes(preflightGate)
      && initializeCapture.indexOf(preflightGate) < initializeRefreshIndex,
    `Initialization gate ${preflightGate} must run before the first active refresh.`,
  );
}
const definitionPreflightIndex = initializeCapture.indexOf(
  'RuntimeMissionDefinitionDiagnosticReader.Read(',
);
assert.ok(
  definitionPreflightIndex >= 0
    && definitionPreflightIndex < initializeRefreshIndex
    && initializeCapture.includes('var definitionsByLabel =')
    && initializeCapture.includes('!definition.Success')
    && initializeCapture.includes('"definition-preflight-identity-mismatch"')
    && initializeCapture.includes('definitionsByLabel[source.Label]')
    && initializeCapture.match(
      /RuntimeMissionDefinitionDiagnosticReader\.Read\(/g,
    )?.length === 1,
  'All exact mission definitions must be preflighted once before any active refresh.',
);
assert.ok(
  initializeCapture.includes('if (!State.TryCommitInitialization(')
    && initializeCapture.includes('"initialize-commit-rejected"'),
  'Initialization must not report success after the managed state rejects its seed.',
);
assert.ok(
  generateTrackingPostfix.includes('frame.GeneratedSeed = seed;')
    && generateTrackingPostfix.includes('frame.GeneratedInstance = __result;')
    && !generateTrackingPostfix.includes('InvokeTrackedStateRefresh('),
  'GenerateTrackingData must capture the exact task without evaluating it before list insertion.',
);
assert.ok(
  startMissionPostfix.includes('ReferenceEquals(PeekStartFrame(), __state)')
    && startMissionPostfix.includes('__state.RefreshedSeed == null')
    && startMissionPostfix.includes(
      'InvokeTrackedStateRefresh(__state.GeneratedInstance);',
    )
    && startMissionPostfix.indexOf(
      'InvokeTrackedStateRefresh(__state.GeneratedInstance);',
    ) < startMissionPostfix.indexOf('TryPopStartFrame(__state)')
    && startMissionPostfix.includes('framePopped = true;')
    && startMissionPostfix.includes(
      '(__state.GeneratedSeed == null) != (__state.RefreshedSeed == null)',
    )
    && startMissionPostfix.includes('if (!State.TryCommitStartedMission('),
  'A newly started task must refresh after insertion, only when needed, while its frame remains current.',
);
const startReadyGateIndexes = [
  ...startMissionPostfix.matchAll(/IsCurrentReadyStartFrame\(__state\)/g),
].map((match) => match.index);
const startDefinitionIndex = startMissionPostfix.indexOf(
  'RuntimeMissionDefinitionDiagnosticReader.Read(',
);
const startRefreshIndex = startMissionPostfix.indexOf(
  'InvokeTrackedStateRefresh(__state.GeneratedInstance);',
);
const startPopIndex = startMissionPostfix.indexOf('TryPopStartFrame(__state)');
const startCommitIndex = startMissionPostfix.indexOf(
  'State.TryCommitStartedMission(',
);
assert.ok(
  startReadyGateIndexes.length === 3
    && startReadyGateIndexes[0] < startDefinitionIndex
    && startDefinitionIndex < startRefreshIndex
    && startRefreshIndex < startReadyGateIndexes[1]
    && startReadyGateIndexes[1] < startPopIndex
    && startPopIndex < startReadyGateIndexes[2]
    && startReadyGateIndexes[2] < startCommitIndex
    && startMissionPostfix.includes('definition == null')
    && startMissionPostfix.includes(
      '"start-definition-preflight-identity-mismatch"',
    )
    && startMissionPostfix.match(
      /RuntimeMissionDefinitionDiagnosticReader\.Read\(/g,
    )?.length === 1,
  'New tasks must preflight one exact definition inside three current-generation gates before refresh and commit.',
);
assert.ok(
  startFrameReadyGate.includes('snapshot.RuntimeAvailable')
    && startFrameReadyGate.includes(
      'snapshot.Phase == RuntimeMissionDiagnosticPhase.Ready',
    )
    && startFrameReadyGate.includes(
      'snapshot.Generation == frame.Generation',
    )
    && startFrameReadyGate.includes(
      'snapshot.OwnerThreadId == frame.ThreadId',
    )
    && startFrameReadyGate.includes('threadId == frame.ThreadId'),
  'The new-task gate must bind Ready availability, generation, owner, and callback thread.',
);
assert.ok(
  startFrameResolver.length > 0
    && startFrameResolver.includes('foreach (var frame in frames)')
    && startFrameResolver.includes('generated == null')
    && startFrameResolver.includes('generated.Identity != seed.Identity')
    && startFrameResolver.includes('generated.Label,')
    && startFrameResolver.includes('"ambiguous-start-refresh-frame"')
    && updateFinishStatesPostfix.includes('TryResolveStartFrameForRefresh(')
    && !updateFinishStatesPostfix.includes(
      'var currentStart = PeekStartFrame();',
    ),
  'Nested StartMission refreshes must bind to one generated identity+label frame across the full stack.',
);
assert.ok(
  refreshInvoker.includes('instance.GetType() != shape.TrackedMissionType')
    && refreshInvoker.includes('shape.UpdateFinishStates.Invoke(instance, null);')
    && refreshInvoker.includes('catch (TargetInvocationException ex)')
    && !refreshInvoker.includes('HasFulfilled')
    && !refreshInvoker.includes('FinishMission')
    && !refreshInvoker.includes('FinishNode'),
  'Controlled refresh must invoke only the exact TrackedMissionData.UpdateFinishStates method.',
);
assert.ok(
  state.includes('seed.RuntimeTrackingBucketCount != seed.SeedTrackingBucketCount')
    && state.includes('seed.TrackingBufferCount != 0')
    && state.includes('"finished-mission-multiset-mismatch"'),
  'Initialization must atomically gate tracking buckets, buffer, and finished-label multiplicity.',
);
assert.ok(
  state.includes('replacement.Identity != 0')
    && state.includes('boundMission.Identity != seed.Identity')
    && state.includes('_labelsByIdentity.Add(seed.Identity, seed.Label);')
    && state.includes('TryReleaseInactiveMissionIdentityLocked')
    && state.includes('duplicate-active-mission-identity'),
  'Loaded labels may bind once, while active conflicts and stale loop-task pointers fail closed.',
);
assert.ok(
  capture.includes('private static bool TryGetAppendedFinishedLabels(')
    && capture.includes('if (after.Count < before.Count) return false;')
    && capture.includes(
      'if (!string.Equals(before[index], after[index], StringComparison.Ordinal))',
    ),
  'FinishNodeExtern must accept only an unchanged list or an exact appended suffix.',
);

const publishCountsBody = state.slice(
  state.indexOf('private void PublishCountsLocked('),
  state.indexOf('private static RuntimeMissionDiagnosticTaskSnapshot ToTaskSnapshot('),
);
assert.ok(
  publishCountsBody.length > 0
    && !publishCountsBody.includes('.ToArray()')
    && !publishCountsBody.includes('.ToList()'),
  'Publishing mission counters must not allocate full active/definition collections on every refresh.',
);
const lifecycleClearBody = serveState.slice(
  serveState.indexOf('public bool ClearForMissionLifecycle('),
  serveState.indexOf('private bool CanObserveLocked('),
);
assert.ok(
  lifecycleClearBody.includes('_signals.Count == 0')
    && lifecycleClearBody.indexOf('_signals.Count == 0')
      < lifecycleClearBody.indexOf('RecordEventLocked('),
  'An unchanged ServeInWork lifecycle clear must return before publishing a duplicate event.',
);
const lifecycleReconcileBody = serveState.slice(
  serveState.indexOf('public bool ReconcileForMissionLifecycle('),
  serveState.indexOf('public bool ClearForMissionLifecycle('),
);
assert.ok(
  lifecycleReconcileBody.includes('RuntimeServeInWorkMissionSignalKey')
    && lifecycleReconcileBody.includes('activeSignalSet.Contains(')
    && lifecycleReconcileBody.includes('removedCanonicalIds.Length == 0')
    && lifecycleReconcileBody.includes('"mission-lifecycle-reconciled"'),
  'ServeInWork lifecycle reconciliation must retain exact active guest/food signals without redundant changes.',
);
const stateRefreshBody = extractStaticVoidMethod(
  capture,
  'OnUpdateFinishStatesPostfixUnsafe',
);
assert.ok(
  stateRefreshBody.includes('State.TryObserveStateRefresh(')
    && stateRefreshBody.includes(
      'RuntimeServeInWorkMissionDiagnosticCapture.ReconcileForMissionLifecycle(',
    )
    && stateRefreshBody.includes(
      'RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(',
    ),
  'A verified task state refresh must reconcile active ServeInWork signals while failed refreshes clear them.',
);
const serveLifecycleReconcile = extractStaticMethod(
  serveCapture,
  'ReconcileForMissionLifecycleUnsafe',
);
assert.ok(
  serveLifecycleReconcile.includes(
    'RuntimeMappedGuestCatalog.TryGetLoadedSnapshot',
  )
    && serveLifecycleReconcile.includes(
      'RuntimeMissionDiagnosticCapture.TryGetServeInWorkDefinitions',
    )
    && serveLifecycleReconcile.includes(
      'RuntimeServeInWorkMissionSignalReconciler.TryBuildActiveSignalKeys',
    )
    && serveLifecycleReconcile.includes('State.ReconcileForMissionLifecycle(')
    && serveLifecycleReconcile.includes('State.ClearForMissionLifecycle('),
  'Lifecycle reconciliation must derive exact active pairs from complete current definitions and fail closed.',
);
const definitionSnapshotBody = state.slice(
  state.indexOf('public bool TryGetServeInWorkDefinitions('),
  state.indexOf('private string ValidateInitializationSeed('),
);
assert.ok(
  definitionSnapshotBody.includes('_snapshot.DefinitionFailureCount != 0')
    && definitionSnapshotBody.indexOf('_snapshot.DefinitionFailureCount != 0')
      < definitionSnapshotBody.indexOf('_missionsByLabel.Values')
    && definitionSnapshotBody.includes('!mission.Definition!.HasReceiver')
    && definitionSnapshotBody.includes(
      'string.IsNullOrWhiteSpace(mission.Definition.Receiver)',
    ),
  'ServeInWork definition reads must reject incomplete active definitions and invalid receivers before projection.',
);
assert.ok(
  serveReconciler.includes('definition.Fulfilled')
    && serveReconciler.includes('resolveCanonicalGuestId(definition.Receiver)')
    && serveReconciler.includes('if (foodId < 0)')
    && serveReconciler.includes(
      'new RuntimeServeInWorkMissionSignalKey(',
    ),
  'The pure ServeInWork reconciler must require non-fulfilled exact canonical/food pairs.',
);
const reconcileWrapper = serveCapture.slice(
  serveCapture.indexOf('public static void ReconcileForMissionLifecycle('),
  serveCapture.indexOf('// A diagnostic callback must not replace'),
);
assert.ok(
  reconcileWrapper.includes('try')
    && reconcileWrapper.includes('catch (Exception ex)')
    && reconcileWrapper.includes('State.ClearForMissionLifecycle(')
    && reconcileWrapper.includes('failed closed'),
  'Unexpected lifecycle reconciliation failures must clear the expected generation.',
);
for (const lifecycleMethod of [
  'OnStartMissionPostfixUnsafe',
  'OnSetFinishedMissionPostfixUnsafe',
  'OnFinishNodePostfixUnsafe',
  'OnUpdateFinishStatesPostfixUnsafe',
  'ObserveTrackedSeed',
]) {
  const lifecycleBody = extractStaticVoidMethod(capture, lifecycleMethod);
  assert.ok(
    lifecycleBody.includes(
      'RuntimeServeInWorkMissionDiagnosticCapture.ReconcileForMissionLifecycle(',
    )
      && lifecycleBody.includes(
        'RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(',
      ),
    `Mission lifecycle method ${lifecycleMethod} must reconcile success and clear failed current-generation evidence.`,
  );
}
for (const failureMethod of [
  'OnStartMissionPrefixUnsafe',
  'OnFinishNodePrefixUnsafe',
]) {
  const failureBody = extractStaticVoidMethod(capture, failureMethod);
  assert.ok(
    failureBody.includes('State.FailCurrentGeneration(')
      && failureBody.includes(
        'RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(',
      ),
    `Mission lifecycle failure method ${failureMethod} must clear current-generation ServeInWork evidence.`,
  );
}

assert.ok(
  definitionReader.includes('"TargetNodeExists"')
    && definitionReader.includes('"RefMission"')
    && definitionReader.includes('RuntimeConcreteCollectionReader.TryReadReferenceArray(')
    && definitionReader.includes('"Missions"')
    && definitionReader.includes('"Name"'),
  'Static definitions and localized titles must use the exact verified lookup path.',
);
assert.ok(
  definitionReader.includes('private static readonly object ShapeRoot = new();')
    && definitionReader.includes('GetDefinitionShape()')
    && definitionReader.includes('TryGetLanguageShape()'),
  'Definition metadata must be cached without retaining native mission or language objects.',
);
for (const forbiddenDefinitionPath of [
  'GetMissionLanguage',
  'm_Name',
  '.ToString()',
]) {
  assert.ok(
    !definitionReader.includes(forbiddenDefinitionPath),
    `Definition diagnostics restored unsafe language path ${forbiddenDefinitionPath}.`,
  );
}

const allMissionSources = `${capture}\n${definitionReader}\n${serveCapture}`;
for (const forbiddenCall of [
  'ParseActiveMissionData',
  'HasFulfilled',
  'GetAllMissionData',
  'GetMissionData',
  'GetTrackedMissionData',
  'GetAllNodes',
  'AllNodesMapping',
  'GetAllMissionDefinitions',
]) {
  assert.ok(
    !allMissionSources.includes(forbiddenCall),
    `Mission diagnostics restored forbidden runtime path ${forbiddenCall}.`,
  );
}
assert.ok(
  serveCapture.includes('TargetMethodName = "ContainsSpecialNPCServeInWorkMission"')
    && serveCapture.includes('nameof(OnPostfix)')
    && serveCapture.includes('return __exception;')
    && !serveCapture.includes('target.Invoke(')
    && !serveCapture.includes('ContainsSpecialNPCServeInWorkMission.Invoke('),
  'ServeInWork observation must remain a passive postfix that preserves native exceptions.',
);

assert.ok(
  capture.includes('public static RuntimeMissionDiagnosticReport Report()')
    && plugin.includes('RuntimeMissionDiagnosticCapture.Attach(Log);')
    && plugin.includes('RuntimeServeInWorkMissionDiagnosticCapture.Attach(Log);'),
  'Plugin startup must attach both passive diagnostic observers.',
);
assert.ok(
  localApi.includes('"snapshot/runtime-mission-diagnostic.json"')
    && localApi.includes('ToJson(RuntimeMissionDiagnosticCapture.Report())')
    && localApi.includes('"snapshot/runtime-mission-serve-in-work-diagnostic.json"')
    && localApi.includes('ToJson(RuntimeServeInWorkMissionDiagnosticCapture.Snapshot())'),
  'Diagnostic packages must include task details and the separate ServeInWork report.',
);
assert.equal(
  localApi.match(/case "\/missions\/tracked":/g)?.length ?? 0,
  1,
  'The business API must expose exactly one canonical tracked-mission GET route.',
);
assert.ok(
  localApi.includes(
    'case "/missions/tracked":\n'
      + '                    WriteResponse(stream, 200, "OK", GetTrackedMissionsJson(query));',
  )
    && localApi.includes(
      'RuntimeMissionDiagnosticCapture.ReadTrackedMissions(),\n'
        + '            ReadStringQuery(query, "knownSignature"),',
    ),
  'The tracked-mission GET must read the managed projection and forward knownSignature.',
);
for (const forbiddenRoute of [
  'case "/missions"',
  'case "/runtime-missions"',
  'case "/mission-tasks"',
  'case "/serve-in-work-missions"',
  'case "/missions/active"',
  'case "/tasks"',
]) {
  assert.ok(
    !localApi.includes(forbiddenRoute),
    `A removed or ambiguous task route returned: ${forbiddenRoute}.`,
  );
}

const trackedReadBody = state.slice(
  state.indexOf('public RuntimeTrackedMissionsSnapshot ReadTrackedMissions()'),
  state.indexOf('public void SetHookStatus(', state.indexOf(
    'public RuntimeTrackedMissionsSnapshot ReadTrackedMissions()',
  )),
);
assert.ok(
  trackedReadBody.length > 0
    && trackedReadBody.includes('.Where(mission => mission.Active)')
    && trackedReadBody.includes('TryProjectTrackedMission(')
    && trackedReadBody.includes('activeMissions.Length != _activeMissionCount'),
  'Tracked missions must be an active-only, all-or-nothing managed projection.',
);
for (const forbiddenRuntimeRead of [
  'RuntimeReflectionUtility',
  '.Invoke(',
  'Report()',
  'ToTaskSnapshot(',
  'RuntimeMissionDiagnosticReport',
]) {
  assert.ok(
    !trackedReadBody.includes(forbiddenRuntimeRead),
    `Tracked mission projection escaped its managed state boundary: ${forbiddenRuntimeRead}.`,
  );
}
assert.ok(
  trackedSnapshot.includes('enum RuntimeTrackedMissionStatus')
    && trackedSnapshot.includes('Unverified,')
    && trackedSnapshot.includes('Tracking,')
    && trackedSnapshot.includes('Fulfilled,')
    && trackedPayload.includes(
      'RuntimeTrackedMissionStatus.Unverified => "unverified"',
    )
    && trackedPayload.includes(
      'RuntimeTrackedMissionStatus.Tracking => "tracking"',
    )
    && trackedPayload.includes(
      'RuntimeTrackedMissionStatus.Fulfilled => "fulfilled"',
    ),
  'The public task contract must use exactly three lowercase progress states.',
);
assert.ok(
  trackedPayload.includes(
    'var canonicalJson = JsonSerializer.Serialize(content, jsonOptions);',
  )
    && trackedPayload.includes(
      'var contentSignature = LocalApiSnapshotSignature.Compute(canonicalJson);',
    )
    && trackedPayload.includes(
      'string.Equals(\n'
        + '                knownSignature,\n'
        + '                contentSignature,\n'
        + '                StringComparison.Ordinal)',
    )
    && trackedPayload.includes('LocalApiTrackedMissionsUnchangedDto'),
  'Tracked mission reads must use a stable canonical signature and knownSignature response.',
);
for (const forbiddenBusinessField of [
  'SourcePartition',
  'SourceBucket',
  'MergedBucket',
  'SourceOrdinal',
  'SavedFinishStateCount',
  'SavedTrueFinishStateCount',
  'ConditionDataCount',
  'NativeIdentityBound',
  'DefinitionStatus',
  'ValidationError',
  'ServeInWorkFoodIds',
  'ObservedAtUtc',
  'HookStatus',
  'ChangeVersion',
  'StateRefreshCount',
]) {
  assert.ok(
    !`${trackedSnapshot}\n${trackedPayload}`.includes(forbiddenBusinessField),
    `The business task payload leaked diagnostic field ${forbiddenBusinessField}.`,
  );
}

assert.ok(
  missionPriorityProjection.includes('business.IsActive')
    && missionPriorityProjection.includes('business.Generation > 0')
    && missionPriorityProjection.includes('mission.Ready')
    && missionPriorityProjection.includes('mission.RuntimeAvailable')
    && missionPriorityProjection.includes('mission.Generation > 0')
    && missionPriorityProjection.includes(
      'serveInWork.MissionGeneration == mission.Generation',
    )
    && missionPriorityProjection.includes(
      'serveInWork.BusinessGeneration == business.Generation',
    )
    && missionPriorityProjection.includes(
      'string.Equals(serveInWork.NightPhase, ActivePhase, StringComparison.Ordinal)',
    )
    && missionPriorityProjection.includes(
      'SpecialBusinessChallengeTypes.NotChallenge',
    ),
  'Mission recipe priority must require current mission/business generations and ordinary Active business.',
);
assert.ok(
  missionPriorityProjection.includes(
    'order.GuestId != signal.CanonicalGuestId',
  )
    && missionPriorityProjection.includes(
      'order.RuntimeGuestId != signal.RawGuestId',
    )
    && missionPriorityProjection.includes('if (matchingOrderIndex >= 0)')
    && missionPriorityProjection.includes('if (matchedRecipe != null)')
    && missionPriorityProjection.includes(
      'if (matchedRecipe == null || matchedRecipe.RecipeId < 0)',
    )
    && missionPriorityProjection.includes(
      'if (!PriorityEquals(existing, priority))',
    )
    && missionPriorityProjection.includes(
      'conflictedOrderIndexes.Add(orderIndex);',
    ),
  'Mission recipe priority must bind canonical+raw identity to one live order and one exact food-to-recipe mapping.',
);
const missionPriorityModel = models.slice(
  models.indexOf('public sealed class MissionRecipePriority'),
  models.indexOf('public sealed class NightBusinessOrder'),
);
for (const forbiddenFreshnessMechanism of [
  'ObservedAt',
  'DateTime',
  'TimeSpan',
  'ttl',
  'TTL',
  'Expires',
  'expiry',
]) {
  assert.ok(
    !`${missionPriorityProjection}\n${missionPriorityModel}`.includes(
      forbiddenFreshnessMechanism,
    ),
    `Mission recipe priority must not use a time-based validity fallback: ${forbiddenFreshnessMechanism}.`,
  );
}
assert.ok(
  overlay.includes('RuntimeMissionRecipePriorityProjection.Enrich(')
    && overlay.includes('NightBusiness = publishedNightBusiness,')
    && overlay.includes('var missionPriority = order.MissionRecipePriority;')
    && overlay.includes('AppendValue(builder, missionPriority.MissionGeneration);')
    && overlay.includes('AppendValue(builder, missionPriority.BusinessGeneration);'),
  'The verified priority must enter the signed night-business snapshot.',
);
assert.ok(
  recommendationService.includes(
    'const sortContext = buildMissionRecipeSortContext(',
  )
    && recommendationService.includes(
      'serializeMissionRecipePriority(order.missionRecipePriority)',
    )
    && recommendationService.includes(
      'const executionPlans = normalizePrimaryExecutionPlans(',
    )
    && recommendationService.includes(
      'const primaryRows = projectPrimaryExecutionPlanRows(',
    )
    && primaryExecutionPlan.includes(
      'const missionIndex = normalizedPlans.findIndex(',
    )
    && primaryExecutionPlan.includes(
      'return movePlanToFront(normalizedPlans, missionIndex);',
    ),
  'Mission recipe priority must flow through the shared primary plan used downstream.',
);

console.log(
  'PASS: runtime mission diagnostics use one passive load seed, two bounded controlled '
    + 'native refresh boundaries, nine exact hooks, active-only signed task reads, strict '
    + 'generation-bound mission recipe priority, and no completion/reward mission APIs.',
);
