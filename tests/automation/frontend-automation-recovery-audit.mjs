import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { once } from 'node:events';
import {
  canAdvanceAutomationRuntimeEventSequence,
  getAutomationStageFailureRetirement,
  isAutomationResponseCurrent,
  isRecoverableCookingTerminalEvent,
  reduceAutomationStageOutcome,
  requiresManualAutomationResolution,
  resolveAutomationNextAttemptAtMs,
  resolveAutomationResponseStage,
  resolveAutomationStepSeconds,
  resolveAutomationStepStartedAtMs,
  resolveAutomationWaitingStep,
  selectAutomationRequestStage,
  shouldRetireMissingManualBarrier,
} from '../../apps/companion/src/companion/automation-machine.ts';

const root = new URL('../../', import.meta.url);
const initial = { retryCount: 2, lastProgressAtMs: 1000, retryStage: 'ensure-beverage' };

const waiting = reduceAutomationStageOutcome(initial, 'waiting', 'ensure-beverage', 5000, true, 3);
assert.deepEqual(waiting, {
  retryCount: 2,
  lastProgressAtMs: 1000,
  retryStage: 'ensure-beverage',
  progressed: false,
  paused: false,
});

const interrupted = reduceAutomationStageOutcome(initial, 'interrupted', 'ensure-beverage', 5000, true, 3);
assert.equal(interrupted.retryCount, 2);
assert.equal(interrupted.lastProgressAtMs, 1000);
assert.equal(interrupted.paused, false);

const retryLimit = reduceAutomationStageOutcome(initial, 'retryable-failure', 'ensure-beverage', 5000, true, 3);
assert.equal(retryLimit.retryCount, 3);
assert.equal(retryLimit.paused, true);
assert.equal(retryLimit.lastProgressAtMs, 1000);

const progressed = reduceAutomationStageOutcome(initial, 'progressed', 'ensure-beverage', 5000, true, 3);
assert.equal(progressed.retryCount, 0);
assert.equal(progressed.retryStage, '');
assert.equal(progressed.lastProgressAtMs, 5000);
assert.equal(progressed.progressed, true);

const blocked = reduceAutomationStageOutcome(initial, 'blocked', 'ensure-beverage', 5000, false, 3);
assert.equal(blocked.paused, true);

const switchedStage = reduceAutomationStageOutcome(initial, 'retryable-failure', 'ensure-cooking', 5000, true, 3);
assert.equal(switchedStage.retryCount, 1, 'A new stage must not inherit the beverage retry count.');
assert.equal(switchedStage.retryStage, 'ensure-cooking');
assert.equal(switchedStage.paused, false);

assert.deepEqual(getAutomationStageFailureRetirement({
  retryStage: 'ensure-beverage',
  paused: true,
  pausedStage: 'ensure-beverage',
  enabledStages: ['ensure-cooking'],
}), { clearRetry: true, clearPause: true });
assert.deepEqual(getAutomationStageFailureRetirement({
  retryStage: 'ensure-cooking',
  paused: true,
  pausedStage: 'ensure-cooking',
  manualResolutionRequired: true,
  enabledStages: [],
}), { clearRetry: false, clearPause: false }, 'Stage switches must not retire a manual-resolution safety latch.');
assert.equal(resolveAutomationResponseStage('beverage', 'match-order'), 'ensure-beverage');
assert.equal(resolveAutomationResponseStage('cooking-start', 'ensure-beverage'), 'ensure-cooking');
assert.equal(resolveAutomationResponseStage('cooking-delivery', 'ensure-beverage'), 'deliver-food');
assert.equal(resolveAutomationResponseStage('unknown-stage', 'ensure-beverage'), 'ensure-beverage');
assert.equal(requiresManualAutomationResolution('cooking-progress-stalled'), true);
assert.equal(requiresManualAutomationResolution('beverage-delivery-commit-uncertain'), true);
assert.equal(requiresManualAutomationResolution('future-uncertain'), false);
assert.equal(requiresManualAutomationResolution('food-delivery-commit-uncertain'), true);
assert.equal(requiresManualAutomationResolution('cooking-delivery-cleanup-failed'), true);
assert.equal(requiresManualAutomationResolution('cooking-manual-handoff-unreadable'), true);
assert.equal(requiresManualAutomationResolution('order-evaluation-state-unreadable'), false);
assert.equal(requiresManualAutomationResolution('order-evaluation-commit-uncertain'), true);
assert.equal(requiresManualAutomationResolution('', ['cooking-warmer-commit-uncertain']), true);
assert.equal(requiresManualAutomationResolution('cooking-ownership-lost'), false);
for (const code of [
  'cooking-ownership-lost',
  'cooking-controller-reused',
  'cooking-mismatch-stored',
  'cooking-target-unavailable-stored',
]) {
  assert.equal(isRecoverableCookingTerminalEvent({
    code,
    reasonCode: code,
    outcome: 'interrupted',
    terminal: true,
  }), true, `${code} must remain an explicit cooking requeue event.`);
}
assert.equal(isRecoverableCookingTerminalEvent({
  code: 'future-interruption',
  reasonCode: 'future-interruption',
  outcome: 'interrupted',
  terminal: true,
}), false, 'An unknown interrupted event must fail closed instead of requeueing a cooking job.');
assert.equal(isRecoverableCookingTerminalEvent({
  code: 'cooking-ownership-lost',
  reasonCode: 'cooking-ownership-lost',
  outcome: 'waiting',
  terminal: true,
}), false, 'A non-interrupted ownership observation must not enter terminal recovery.');
assert.equal(isRecoverableCookingTerminalEvent({
  code: 'cooking-ownership-lost',
  reasonCode: 'cooking-ownership-lost',
  outcome: 'interrupted',
  terminal: false,
}), false, 'A non-terminal ownership observation must not enter terminal recovery.');
assert.equal(selectAutomationRequestStage({
  needsBeverage: true,
  needsCooking: true,
  needsDelivery: true,
  needsCompletion: true,
}), 'ensure-beverage', 'The request stage must follow the C# beverage-first execution order.');

assert.equal(isAutomationResponseCurrent({
  requestEpoch: 4,
  currentEpoch: 5,
  runtimeEnabled: true,
}), false, 'A response from before explicit cancellation must be ignored.');
assert.equal(isAutomationResponseCurrent({
  requestEpoch: 5,
  currentEpoch: 5,
  runtimeEnabled: true,
  responseStartEventSequence: 10,
  currentEventSequence: 11,
}), false, 'A response older than a terminal runtime event must be ignored.');
assert.equal(isAutomationResponseCurrent({
  requestEpoch: 5,
  currentEpoch: 5,
  runtimeEnabled: true,
  responseStartEventSequence: 11,
  currentEventSequence: 11,
}), true);
assert.equal(resolveAutomationNextAttemptAtMs(0, 'interrupted', 5000, 750), 5750);
assert.equal(
  resolveAutomationNextAttemptAtMs(0, 'waiting', 5000, 750),
  5750,
  'A structured cooker wait must honor the runtime retry delay without becoming a failure.',
);
assert.equal(
  resolveAutomationNextAttemptAtMs(0, 'waiting', 5000, 1),
  5250,
  'A positive cooker-wait delay must retain the minimum scheduling interval.',
);
assert.equal(resolveAutomationNextAttemptAtMs(5750, 'waiting', 5100, 0), 5750);
assert.equal(resolveAutomationNextAttemptAtMs(5750, 'progressed', 5200, 0), 0);
const waitingStepStartedAt = resolveAutomationStepStartedAtMs(
  'ensure-cooking',
  'ensure-cooking',
  1000,
  5000,
);
assert.equal(waitingStepStartedAt, 1000, 'Waiting in the same stage must preserve its original start time.');
const changedStepStartedAt = resolveAutomationStepStartedAtMs(
  'ensure-cooking',
  'deliver-food',
  waitingStepStartedAt,
  5000,
);
assert.equal(changedStepStartedAt, 5000, 'A real stage transition must start its timer exactly once.');
assert.equal(
  resolveAutomationStepStartedAtMs('deliver-food', 'deliver-food', changedStepStartedAt, 6500),
  5000,
  'Repeated waiting responses must not restart the transitioned stage timer.',
);
assert.deepEqual(
  [5000, 5999, 6000, 7499].map((now) => resolveAutomationStepSeconds(changedStepStartedAt, now)),
  [0, 0, 1, 2],
  'Visible diagnostic clock samples must be monotonic within one stage.',
);
let rareLoopStep = 'complete-order';
let rareLoopStepStartedAt = 1000;
const rareLoopSeconds = [];
for (const responseAt of [2500, 4000, 5500]) {
  const authoritativeResponseStep = 'deliver-food';
  rareLoopStepStartedAt = resolveAutomationStepStartedAtMs(
    rareLoopStep,
    authoritativeResponseStep,
    rareLoopStepStartedAt,
    responseAt,
  );
  rareLoopStep = authoritativeResponseStep;
  const waitingStep = resolveAutomationWaitingStep({
    schedulerAvailable: true,
    authoritativeResponseStep,
    completionEnabled: true,
  });
  rareLoopStepStartedAt = resolveAutomationStepStartedAtMs(
    rareLoopStep,
    waitingStep,
    rareLoopStepStartedAt,
    responseAt,
  );
  rareLoopStep = waitingStep;
  rareLoopSeconds.push(resolveAutomationStepSeconds(rareLoopStepStartedAt, responseAt));
}
assert.equal(rareLoopStep, 'deliver-food');
assert.equal(rareLoopStepStartedAt, 2500, 'Three cooking-delivery waits must reset the timer only on the first real transition.');
assert.deepEqual(rareLoopSeconds, [0, 1, 3], 'Rare preflight/waiting loops must not oscillate back to complete-order.');
assert.equal(canAdvanceAutomationRuntimeEventSequence(false, false), true);
assert.equal(canAdvanceAutomationRuntimeEventSequence(true, true), true);
assert.equal(
  canAdvanceAutomationRuntimeEventSequence(true, false),
  false,
  'A recoverable or ordinary blocking event must not replace an unresolved barrier sequence.',
);
assert.equal(shouldRetireMissingManualBarrier(true, 0, new Set()), false);
assert.equal(shouldRetireMissingManualBarrier(true, 12, new Set([12])), false);
assert.equal(
  shouldRetireMissingManualBarrier(true, 12, new Set()),
  true,
  'A same-session snapshot that no longer contains an acknowledged barrier must retire the stale local latch.',
);

await assertOldRecoveryLogicRemoved();
await assertStageAndCancellationContracts();
await assertMockProtocol();

console.log('PASS: structured automation outcomes preserve waiting state, bound retries, pause blocked jobs, and expose job cancellation/snapshot contracts.');

async function assertOldRecoveryLogicRemoved() {
  const files = [
    'apps/companion/src/companion/automation-state.ts',
    'apps/companion/src/companion/domain/automation.ts',
    'apps/companion/src/companion/ModWorkbench.tsx',
  ];
  const source = (await Promise.all(files.map(async (file) => readFile(new URL(file, root), 'utf8')))).join('\n');
  for (const removed of [
    'preparedAtMs',
    'isNormalOrderPreparedStale',
    'isRareOrderPreparedStale',
    'isRecoverableNormalPausedState',
    'isNormalOrderCollected',
    "step.message.includes('已在制作中')",
    "lastError.includes('目标料理长时间未直接送达')",
  ]) {
    assert.equal(source.includes(removed), false, `obsolete recovery logic remains: ${removed}`);
  }
}

async function assertStageAndCancellationContracts() {
  const workbench = await readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8');
  const domain = await readFile(new URL('apps/companion/src/companion/domain/automation.ts', root), 'utf8');
  const storage = await readFile(new URL('apps/companion/src/companion/storage.ts', root), 'utf8');
  const api = await readFile(new URL('apps/companion/src/companion/api.ts', root), 'utf8');
  const types = await readFile(new URL('apps/companion/src/companion/types.ts', root), 'utf8');
  const servicePanel = await readFile(new URL('apps/companion/src/companion/pages/ModServicePanel.tsx', root), 'utf8');
  const stateMachine = await readFile(new URL('apps/companion/src/companion/automation-state.ts', root), 'utf8');
  const automationMachine = await readFile(new URL('apps/companion/src/companion/automation-machine.ts', root), 'utf8');
  const runtime = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.cs', root), 'utf8');
  const delivery = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.Delivery.cs', root), 'utf8');
  const directDelivery = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.DirectDelivery.cs', root), 'utf8');
  const cooking = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.Cooking.cs', root), 'utf8');
  const cookingLifecycle = await readFile(new URL('mods/bepinex/src/Save/AutomationCookingJobLifecycle.cs', root), 'utf8');
  const orderMatching = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.OrderMatching.cs', root), 'utf8');
  const generationTracker = await readFile(new URL('mods/bepinex/src/Save/RuntimeCookingGenerationTracker.cs', root), 'utf8');

  assert.equal(domain.includes('buildCompleteOrderPreferences'), false, 'Completion must not force unrelated rare stages.');
  assert.ok(runtime.includes('else if (!request.AutoTakeBeverage)'), 'Rare completion must honor the beverage switch.');
  assert.equal(stateMachine.includes("response.automation.stage === 'cooking'"), false, 'The removed generic cooking stage is still accepted.');
  assert.ok(stateMachine.includes("response.automation.stage === 'cooking-delivery'"));
  assert.equal(automationMachine.includes("case 'cooking':"), false, 'The frontend still carries the removed generic cooking-stage contract.');
  assert.ok(runtime.includes('request.StopOnError || IsAutomationSafetyBarrierCode('), 'Safety barriers must stop the current request independently of StopOnError.');
  assert.ok(runtime.includes('else if (IsAutomationSafetyBarrierCode(cookingJobResult.Code))'));
  assert.ok(runtime.includes('RecordOrderSafetyBarrierIfNeeded('), 'Non-job commit uncertainty must survive HTTP response loss as a runtime event.');
  assert.ok(delivery.includes('finalSetterAttempted\n                ? UncertainDelivery('), 'An exception after the final setter starts must not be classified as definitely uncommitted.');
  assert.ok(delivery.includes('CompareObjectIdentity(existingInAirItem, sellable) != RuntimeObjectIdentityComparison.Same'), 'The delivery entry must not overwrite another in-air object.');
  assert.ok(delivery.includes('TryClearOrderInAirAndVerify'), 'In-air cleanup must be verified after callbacks that can fail after writing the field.');
  assert.equal(delivery.includes('private static bool TrySetOrderInAir('), false, 'The old boolean-only in-air setter path still exists.');
  assert.ok(directDelivery.includes('pendingFood != null && !IsSameObject(pendingFood, cookedFood)'), 'A cooking job must not overwrite a different in-air food object with the same ID.');
  assert.ok(cooking.includes('invalidResultDiagnostic = $"Result 读取失败：{readDiagnostic}"'), 'A failed cooker Result read must not be classified as a confirmed null result.');
  assert.ok(orderMatching.includes('RuntimeObjectIdentityComparison.Unknown'), 'Side-effect identity checks must preserve an unknown state.');
  assert.equal(orderMatching.includes('return ReadObjectPointer(left) == ReadObjectPointer(right);'), false, 'Side-effect identity must not use the managed-hash pointer fallback.');
  assert.ok(directDelivery.includes('StoredFoods[{index}] 与目标成品的原生身份无法确认'), 'StoreFood commit proof must reject unknown object identity.');
  const resetIndex = directDelivery.indexOf('job.FoodDeliveryCleanupTracker.Complete();');
  const extractIndex = directDelivery.indexOf('CompleteCookerExtractionAfterReset(job);', resetIndex);
  assert.ok(resetIndex >= 0 && extractIndex > resetIndex, 'Special-cooker extraction callbacks must run only after the old generation is strictly reset.');
  assert.ok(directDelivery.includes('"OnCookerAvailabilityUpdate", new object?[] { -1 }'), 'Direct extraction must publish the native cooker-availability notification.');
  for (const [methodName, prefixName, postfixName] of [
    ['setCook', 'setCookPrefix', 'setCookPostfix'],
    ['extract', 'extractPrefix', 'extractPostfix'],
    ['store', 'storePrefix', 'storePostfix'],
  ]) {
    assert.match(
      generationTracker,
      new RegExp(`_harmony\\.Patch\\(\\s*${methodName},[\\s\\S]*?prefix: new HarmonyMethod\\(${prefixName}\\),[\\s\\S]*?postfix: new HarmonyMethod\\(${postfixName}\\)\\)`),
      `Cooking ownership must observe entry and normal completion for ${methodName}.`,
    );
  }
  assert.ok(
    generationTracker.includes('public static bool TryGetOwnershipSnapshot('),
    'Cooking jobs must read the generation and content revision through the exact ownership snapshot.',
  );
  assert.match(
    generationTracker,
    /TryRecordContentMutation\([\s\S]*catch \(Exception ex\)[\s\S]*return default;[\s\S]*TryCompleteContentMutation\([\s\S]*catch \(Exception ex\)[\s\S]*ReportHookFailure/,
    'Cooking ownership callbacks must remain no-throw.',
  );
  assert.match(
    generationTracker,
    /bool __runOriginal[\s\S]*CompleteContentMutation\(__state, __runOriginal\)[\s\S]*if \(!originalRan \|\| token\.ControllerPointer == 0 \|\| token\.ContentRevision <= 0\) return;[\s\S]*current\.ContentRevision != token\.ContentRevision[\s\S]*current\.LastMutation != token\.Mutation/,
    'Only a normally executed current native mutation may become completed.',
  );
  assert.equal(
    [generationTracker, cooking, directDelivery].some((source) => source.includes('TryGetGeneration')),
    false,
    'The generation-only ownership read must not remain after content-revision tracking is enabled.',
  );
  assert.equal(
    [stateMachine, workbench, cookingLifecycle, cooking, directDelivery]
      .some((source) => source.includes('cooking-result-removed')),
    false,
    'The removed ambiguous cooking-result code must not remain in the frontend or runtime ownership contract.',
  );
  assert.ok(workbench.includes('retireDisabledRareAutomationFailure'));
  assert.ok(workbench.includes('retireDisabledNormalAutomationFailure'));
  assert.ok(workbench.includes('window.setInterval(refreshDiagnosticClock, 1000)'), 'Visible automation diagnostics need a one-second display clock.');
  assert.ok(workbench.includes('authoritativeResponseStep: preflightResponseStep'), 'Rare waiting diagnostics must preserve the authoritative preflight stage.');
  assert.ok(workbench.includes('refreshRareOrderDiagnostics(Date.now());'), 'Rare loop completion must use a timestamp taken after its responses.');
  assert.ok(workbench.includes('refreshNormalOrderDiagnostics(orders, Date.now());'), 'Normal loop completion must use a timestamp taken after its responses.');
  assert.ok(storage.includes('AUTOMATION_CANCELLATION_ENDPOINT_STORAGE_KEY'));
  assert.ok(workbench.includes('readStoredAutomationCancellationEndpoint'));
  assert.ok(workbench.includes("persistAutomationCancellationEndpoint('')"));
  assert.ok(workbench.includes('retryNormalAutomationOrder'));
  assert.ok(workbench.includes('resetNormalAutomationOrder'));
  assert.ok(servicePanel.includes('normal-auto:${diagnostic.orderKey}:retry'));
  assert.ok(servicePanel.includes('normal-auto:${diagnostic.orderKey}:reset'));
  assert.ok(stateMachine.includes('manualResolutionRequired: true'));
  assert.ok(stateMachine.includes("prepared: state.prepared || responseStep === 'ensure-cooking' || responseStep === 'deliver-food'"));
  assert.ok(domain.includes('!base.manualResolutionRequired && snapshotPassesPausedStage'));
  assert.ok(domain.includes('!state.manualResolutionRequired && snapshotPassesPausedStage'));
  assert.ok(workbench.includes('retainAutomationSafetyStates(rareOrderStatesRef.current)'));
  assert.ok(workbench.includes('retainAutomationSafetyStates(normalOrderStatesRef.current)'));
  assert.ok(workbench.includes('if (!state.manualResolutionRequired) states.delete(orderKey);'), 'Only manual-resolution latches may survive an explicit automation reset.');
  assert.ok(workbench.includes('lastRuntimeEventSequence: state.lastRuntimeEventSequence'));
  assert.ok(workbench.includes('acquireAutomationLeaseSingleFlight'));
  assert.ok(workbench.includes('isAutomationLeaseOwnedForConnection('), 'A cached lease must be bound to the current connection and Mod session.');
  assert.ok(workbench.includes('buildAutomationLeaseConnectionKey('), 'The lease binding must use the endpoint, token, and authoritative Mod session.');
  assert.equal(workbench.includes('automationConnectionEpochRef'), false, 'A transient connection error must not manufacture a new lease identity.');
  assert.ok(workbench.includes('automationLeaseRevalidationRequiredRef.current = true'), 'Connection loss and lease rejection must require authoritative lease revalidation.');
  assert.equal(workbench.includes('bindAutomationStatesToNewSession'), false, 'A new Mod session must not preserve stale automation state.');
  assert.ok(workbench.includes('rareOrderStatesRef.current.clear();'), 'A new Mod session must clear rare-order state.');
  assert.ok(workbench.includes('rareOrderDiagnosticItemsRef.current.clear();'), 'A new Mod session must clear rare diagnostic candidates.');
  assert.ok(workbench.includes('normalOrderStatesRef.current.clear();'), 'A new Mod session must clear normal-order state.');
  const runtimeEventReducerIndex = workbench.indexOf('const events = snapshot?.automationEvents ?? [];');
  const matcherLoopIndex = workbench.indexOf('for (const item of orderRecommendations.recommendations)', runtimeEventReducerIndex);
  const matcherRevisionIndex = workbench.indexOf('orderRecommendations.successRevision', matcherLoopIndex);
  assert.ok(runtimeEventReducerIndex >= 0 && matcherLoopIndex > runtimeEventReducerIndex, 'Rare event replay must not depend on an already-populated diagnostic ref.');
  assert.ok(matcherRevisionIndex > matcherLoopIndex, 'Rare runtime events must replay when fresh recommendation matchers arrive.');
  assert.ok(workbench.includes("response.automation.reasonCode === 'automation-lease-unavailable'"));
  assert.ok(workbench.includes('handleAutomationControlPlaneResponse(prepareResponse)'), 'Lease loss must invalidate the control plane before reducing an order response.');
  assert.ok(workbench.includes('handleAutomationControlPlaneResponse(response)'), 'Normal-order lease loss must not count as an order-stage retry.');
  const waitIndex = workbench.indexOf('await waitForAutomationLeaseAcquire();');
  const cancelIndex = workbench.indexOf('cancelAutomationCookingJobs(automationCancellationEndpoint, apiToken)', waitIndex);
  assert.ok(waitIndex >= 0 && cancelIndex > waitIndex, 'Cancellation must wait for the single in-flight lease acquire.');
  assert.ok(servicePanel.includes('diagnostic.manualResolutionRequired ? \'确认已处理\' : \'重置\''));
  assert.ok(servicePanel.includes('!diagnostic.paused || diagnostic.manualResolutionRequired'));
  assert.ok(api.includes('/automation/barriers/ack?${params.toString()}'), 'The frontend must use the canonical safety-barrier ACK endpoint.');
  assert.ok(api.includes("params.set('runtimeGuestId', String(item.order.runtimeGuestId))"), 'Rare automation must send the raw runtime guest identity.');
  assert.ok(api.includes("params.set('foodTagId', String(item.order.foodTagId))"), 'Rare automation must send the raw food Tag identity.');
  assert.ok(api.includes("params.set('beverageTagId', String(item.order.beverageTagId))"), 'Rare automation must send the raw beverage Tag identity.');
  assert.ok(domain.includes("'runtime-identity-missing'"), 'Orders with incomplete runtime identity must be skipped before automation requests.');
  assert.ok(types.includes('acknowledgedSequences: number[];'), 'The ACK response must expose every barrier sequence cleared by the Mod.');
  assert.ok(workbench.includes('automationLeaseOwnedRef.current'), 'ACK must require the current-session automation lease.');
  assert.ok(workbench.includes('clearAcknowledgedAutomationBarriers(response.acknowledgedSequences'), 'ACK success must clear every frontend latch acknowledged by the Mod.');
  assert.ok(workbench.match(/canAdvanceAutomationRuntimeEventSequence\(state\.manualResolutionRequired, manualResolutionRequired\)/g)?.length >= 2, 'Rare and normal reducers must both preserve manual barrier sequences.');
  assert.ok(workbench.includes('events.filter(isManualResolutionAutomationEvent)'), 'Same-session barrier convergence must use the unresolved manual-event set from the snapshot.');
  assert.ok(workbench.match(/shouldRetireMissingManualBarrier\(/g)?.length >= 2, 'Rare and normal latches must converge after another window acknowledges their barrier.');
  assert.ok(workbench.includes("event.code.startsWith('order-')"), 'Order-evaluation barriers must map to the order stage.');
  assert.ok(stateMachine.includes("'order-evaluation-state-unreadable'"));
  assert.ok(stateMachine.includes("'order-evaluation-commit-uncertain'"));
  assert.ok(stateMachine.includes("'cooking-manual-handoff-unreadable'"));
  assert.ok(stateMachine.includes("'cooking-cooker-waiting'"));
  assert.ok(stateMachine.includes("'cooking-ownership-lost'"));
  assert.equal(stateMachine.includes("'cooking-result-removed'"), false);
  assert.ok(automationMachine.includes("'cooking-manual-handoff-unreadable'"));
  assert.ok(
    automationMachine.includes("(outcome === 'waiting' && retryAfterMs > 0)"),
    'A structured waiting response with a delay must schedule the next attempt.',
  );
  assert.equal(workbench.includes("'cooking-result-removed'"), false);
  assert.ok(
    workbench.includes('const recoverable = isRecoverableCookingTerminalEvent(event);'),
    'Runtime events must use the tested explicit cooking-recovery classifier.',
  );
  assert.ok(servicePanel.includes('title={`待人工确认 (${diagnostics.length})`}'), 'Unresolved barriers need an order-independent panel.');
  assert.ok(servicePanel.includes('automation-barrier:${diagnostic.sequence}:ack'));
  assert.ok(servicePanel.includes("isBusy ? '确认中' : '确认已处理'"));
}

async function assertMockProtocol() {
  const port = 32157;
  const child = spawn(
    process.execPath,
    [new URL('scripts/mock-local-api.mjs', root).pathname],
    {
      env: {
        ...process.env,
        MOCK_API_PORT: String(port),
        MOCK_AUTOMATION_SESSION_ID: 'automation-audit-session',
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    },
  );
  const output = [];
  child.stdout.on('data', (chunk) => output.push(chunk.toString()));
  child.stderr.on('data', (chunk) => output.push(chunk.toString()));

  try {
    await waitForServer(`http://127.0.0.1:${port}/health`, child, output);
    const headers = {
      'x-mystia-steward-companion-client-id': 'automation-audit',
      'x-mystia-steward-companion-client-label': 'Automation Audit',
    };
    const deniedAck = await postJson(`http://127.0.0.1:${port}/automation/barriers/ack?sequence=9001`, headers);
    assert.equal(deniedAck.ok, false);
    assert.deepEqual(deniedAck.acknowledgedSequences, []);

    const lease = await postJson(`http://127.0.0.1:${port}/automation/lease/acquire`, headers);
    assert.equal(lease.owned, true);

    const snapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.equal(snapshot.automationSessionId, 'automation-audit-session');
    assert.ok(Array.isArray(snapshot.automationCookingJobs));
    assert.ok(Array.isArray(snapshot.automationEvents));
    assert.deepEqual(snapshot.automationEvents.map((event) => event.sequence), [9000, 9001]);

    const missingAck = await postJson(`http://127.0.0.1:${port}/automation/barriers/ack?sequence=9999`, headers);
    assert.equal(missingAck.ok, false);
    assert.deepEqual(missingAck.acknowledgedSequences, []);
    const unchangedSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.deepEqual(unchangedSnapshot.automationEvents.map((event) => event.sequence), [9000, 9001]);

    const acknowledged = await postJson(`http://127.0.0.1:${port}/automation/barriers/ack?sequence=9001`, headers);
    assert.equal(acknowledged.ok, true);
    assert.equal(acknowledged.sequence, 9001);
    assert.equal(acknowledged.acknowledgedCount, 2);
    assert.deepEqual(acknowledged.acknowledgedSequences, [9000, 9001]);

    const duplicateAck = await postJson(`http://127.0.0.1:${port}/automation/barriers/ack?sequence=9001`, headers);
    assert.equal(duplicateAck.ok, false);
    assert.deepEqual(duplicateAck.acknowledgedSequences, []);

    const acknowledgedSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.deepEqual(acknowledgedSnapshot.automationEvents, []);
    assert.notEqual(acknowledgedSnapshot.snapshotSignature, snapshot.snapshotSignature);

    const response = await postJson(`http://127.0.0.1:${port}/orders/prepare-next`, headers);
    assert.equal(response.automation.outcome, 'progressed');
    assert.equal(response.automation.stage, 'cooking-start');
    assert.equal(response.automation.reasonCode, 'cooking-started');
    assert.equal(typeof response.automation.jobId, 'string');

    const activeSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    const activeJob = activeSnapshot.automationCookingJobs.find((job) => job.jobId === response.automation.jobId);
    assert.ok(activeJob, 'Mock snapshot must expose the cooking job created by the action response.');
    assert.equal(activeJob.warmerStoreCommitUncertain, false);
    assert.equal(activeJob.foodDeliveryCommitted, false);
    assert.equal(activeJob.foodDeliveryCommitUncertain, false);
    assert.equal(activeJob.foodDeliveryCleanupAttempts, 0);

    const cancelled = await postJson(`http://127.0.0.1:${port}/automation/jobs/cancel`, headers);
    assert.equal(cancelled.ok, true);
    assert.equal(cancelled.leaseReleased, true);
    assert.equal(cancelled.cancelledJobs, 1);
    assert.equal(typeof cancelled.commandEpoch, 'number');

    const superseded = await postJson(`http://127.0.0.1:${port}/orders/prepare-next`, headers);
    assert.equal(superseded.ok, false);
    assert.equal(superseded.automation.reasonCode, 'automation-lease-unavailable');
  } finally {
    child.kill('SIGTERM');
    if (child.exitCode === null) await once(child, 'exit');
  }
}

async function waitForServer(url, child, output) {
  for (let attempt = 0; attempt < 60; attempt += 1) {
    if (child.exitCode !== null) throw new Error(`mock API exited early: ${output.join('')}`);
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      // The listener may still be starting.
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(`mock API did not start: ${output.join('')}`);
}

async function getJson(url) {
  const response = await fetch(url);
  assert.equal(response.ok, true);
  return response.json();
}

async function postJson(url, headers) {
  const response = await fetch(url, { method: 'POST', headers });
  assert.equal(response.ok, true);
  return response.json();
}
