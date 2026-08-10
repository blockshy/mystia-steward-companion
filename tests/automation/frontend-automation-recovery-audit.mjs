import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { once } from 'node:events';
import {
  assertAutomationDirectDeliveryCompletionInvariant,
  canAdvanceAutomationRuntimeEventSequence,
  getAutomationStageFailureRetirement,
  hasAutomationSpecialTargetRotated,
  isAutomationResponseCurrent,
  isRecoverableCookingTerminalEvent,
  reconcileAutomationRollbackTarget,
  reduceAutomationCookingRollbackBudget,
  reduceAutomationManualRetry,
  reduceAutomationStageOutcome,
  requiresManualAutomationResolution,
  resolveAutomationNextAttemptAtMs,
  resolveAutomationResponseStage,
  resolveAutomationStepSeconds,
  resolveAutomationStepStartedAtMs,
  resolveAutomationWaitingStep,
  selectAutomationRequestStage,
  shouldRequestNormalOrderCompletion,
  shouldRetainAutomationStateWithoutCandidate,
  shouldRetireMissingManualBarrier,
} from '../../apps/companion/src/companion/automation-machine.ts';
import {
  getNightBusinessAutomationPauseMessage,
  getNightBusinessAutomationPauseLabel,
  getNightBusinessAutomationSummary,
  NIGHT_BUSINESS_LIFECYCLE_UNAVAILABLE,
  NIGHT_BUSINESS_TUTORIAL_ACTIVE,
  NIGHT_BUSINESS_TUTORIAL_STATE_UNAVAILABLE,
} from '../../apps/companion/src/companion/domain/automation-runtime.ts';

const root = new URL('../../', import.meta.url);
const initial = { retryCount: 2, lastProgressAtMs: 1000, retryStage: 'ensure-beverage' };

assert.equal(
  getNightBusinessAutomationPauseMessage(NIGHT_BUSINESS_TUTORIAL_ACTIVE),
  '教学经营中，自动化已暂停。',
);
assert.equal(getNightBusinessAutomationPauseLabel(NIGHT_BUSINESS_TUTORIAL_ACTIVE), '教学暂停');
assert.equal(getNightBusinessAutomationPauseLabel(NIGHT_BUSINESS_TUTORIAL_STATE_UNAVAILABLE), '状态待确认');
assert.equal(getNightBusinessAutomationPauseLabel(NIGHT_BUSINESS_LIFECYCLE_UNAVAILABLE), '');
assert.equal(
  getNightBusinessAutomationPauseMessage(NIGHT_BUSINESS_TUTORIAL_STATE_UNAVAILABLE),
  '暂时无法确认教学状态，自动化已暂停。',
);
assert.equal(
  getNightBusinessAutomationPauseMessage(NIGHT_BUSINESS_LIFECYCLE_UNAVAILABLE),
  '',
  'A day scene or inactive business must keep the existing waiting-for-business wording.',
);
assert.equal(getNightBusinessAutomationSummary({
  configured: true,
  allowed: false,
  blockReason: NIGHT_BUSINESS_TUTORIAL_ACTIVE,
  trackedCount: 2,
}), '已暂停 · 教学经营');
assert.equal(getNightBusinessAutomationSummary({
  configured: true,
  allowed: false,
  blockReason: NIGHT_BUSINESS_LIFECYCLE_UNAVAILABLE,
  trackedCount: 2,
}), '已开启 · 等待经营');
assert.equal(getNightBusinessAutomationSummary({
  configured: true,
  allowed: true,
  blockReason: '',
  trackedCount: 2,
}), '已开启 · 跟踪 2 笔');

assert.equal(shouldRequestNormalOrderCompletion({
  beverageDeliveryEnabled: false,
  completionEnabled: true,
  completionReady: false,
  foodDeliveryEnabled: true,
  forceKoishiFullFeedAutomation: false,
}), true, 'A normal cooking job that may directly deliver food must capture the enabled completion intent before food is served.');
assert.equal(shouldRequestNormalOrderCompletion({
  beverageDeliveryEnabled: false,
  completionEnabled: false,
  completionReady: false,
  foodDeliveryEnabled: true,
  forceKoishiFullFeedAutomation: false,
}), false, 'An invalid disabled completion stage must not manufacture completion intent.');
assert.equal(shouldRequestNormalOrderCompletion({
  beverageDeliveryEnabled: false,
  completionEnabled: true,
  completionReady: false,
  foodDeliveryEnabled: false,
  forceKoishiFullFeedAutomation: false,
}), false, 'Starting cooking without direct delivery must leave the completed dish for manual handoff.');
assert.equal(shouldRequestNormalOrderCompletion({
  beverageDeliveryEnabled: true,
  completionEnabled: true,
  completionReady: false,
  foodDeliveryEnabled: false,
  forceKoishiFullFeedAutomation: false,
}), true, 'Direct beverage delivery must carry completion intent even before the order is ready.');
assert.equal(shouldRequestNormalOrderCompletion({
  beverageDeliveryEnabled: false,
  completionEnabled: true,
  completionReady: true,
  foodDeliveryEnabled: false,
  forceKoishiFullFeedAutomation: false,
}), true, 'An ordinary normal order may request completion after becoming ready.');
assert.equal(shouldRequestNormalOrderCompletion({
  beverageDeliveryEnabled: false,
  completionEnabled: true,
  completionReady: false,
  foodDeliveryEnabled: false,
  forceKoishiFullFeedAutomation: true,
}), true, 'The Koishi full-feed path must carry completion intent into its exact native evaluation route.');

assert.doesNotThrow(() => assertAutomationDirectDeliveryCompletionInvariant({
  beverageDeliveryEnabled: true,
  completionEnabled: true,
  foodDeliveryEnabled: true,
  targetLabel: '测试',
}));
assert.throws(() => assertAutomationDirectDeliveryCompletionInvariant({
  beverageDeliveryEnabled: true,
  completionEnabled: false,
  foodDeliveryEnabled: false,
  targetLabel: '测试',
}), /自动送达必须同时启用自动完成订单/);
assert.throws(() => assertAutomationDirectDeliveryCompletionInvariant({
  beverageDeliveryEnabled: false,
  completionEnabled: false,
  foodDeliveryEnabled: true,
  targetLabel: '测试',
}), /自动送达必须同时启用自动完成订单/);

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
assert.equal(
  requiresManualAutomationResolution('cooking-manual-handoff'),
  false,
  'A deterministic final-food handoff must remain a waiting state, not an ACK barrier.',
);
assert.equal(requiresManualAutomationResolution('cooking-manual-handoff-unreadable'), true);
assert.equal(requiresManualAutomationResolution('order-evaluation-state-unreadable'), false);
assert.equal(requiresManualAutomationResolution('order-evaluation-commit-uncertain'), true);
assert.equal(requiresManualAutomationResolution('order-evaluation-target-mismatch'), true,
  'A deterministic target mismatch must remain latched until its exact backend safety barrier is acknowledged.');
assert.equal(requiresManualAutomationResolution('mizuchi-contract-mismatch'), true,
  'Mizuchi role/closure/Modifier drift must remain latched until its exact backend safety barrier is acknowledged.');
assert.equal(requiresManualAutomationResolution('order-evaluation-closeout-unresolved'), true,
  'An exhausted exact closeout must remain latched until its backend safety barrier is acknowledged.');
assert.equal(requiresManualAutomationResolution('order-terminated-before-evaluation'), false,
  'A proven native order removal retires the receipt and must not manufacture an ACK barrier.');
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
assert.deepEqual(
  reduceAutomationCookingRollbackBudget(1, {
    reasonCode: 'cooking-food-mismatch-stored',
  }),
  {
    rollbackCount: 2,
    action: 'consumed',
  },
  'A dark or otherwise mismatched result under the same execution target must consume rollback budget.',
);
assert.deepEqual(
  reduceAutomationCookingRollbackBudget(0, {
    reasonCode: 'cooking-ownership-lost',
  }),
  {
    rollbackCount: 1,
    action: 'consumed',
  },
  'A native or player Extract is one bounded external-intervention rollback; later locked-cooker waits do not add another.',
);
assert.deepEqual(
  reduceAutomationCookingRollbackBudget(2, {
    reasonCode: 'cooking-target-changed-stored',
  }),
  {
    rollbackCount: 2,
    action: 'deferred',
  },
  'A target-change terminal event alone must retain the old budget until a different nonempty target is observed.',
);
const manualRetryState = {
  rollbackCount: 2,
  paused: true,
  manualResolutionRequired: false,
  pauseReasonCode: 'rollback-limit-reached',
  pausedStage: 'ensure-cooking',
  step: 'paused',
  stepStartedAtMs: 1000,
  retryCount: 3,
  retryStage: 'ensure-cooking',
  nextAttemptAtMs: 9000,
  lastError: 'limit',
  orderKey: 'normal:desk-3:guest-5',
  prepared: true,
  cookingJobId: 'job-17',
  beverageHandled: true,
  foodDelivered: true,
  completed: false,
  executionTarget: { foodId: 23, recipeId: 17 },
  rollbackTargetSignature: 'target-a',
  rollbackTargetRevision: 4,
  lastRuntimeEventSequence: 37,
  lastProgressAtMs: 4200,
  detailMessage: '已确认的订单事实必须保留。',
};
const rollbackLimitRetry = reduceAutomationManualRetry(
  manualRetryState,
  'complete-order',
  5000,
);
assert.equal(rollbackLimitRetry.resumed, true);
assert.equal(rollbackLimitRetry.rollbackBudgetReset, true);
assert.deepEqual(rollbackLimitRetry.state, {
  ...manualRetryState,
  rollbackCount: 0,
  paused: false,
  pauseReasonCode: '',
  pausedStage: '',
  step: 'ensure-cooking',
  stepStartedAtMs: 5000,
  retryCount: 0,
  retryStage: '',
  nextAttemptAtMs: 0,
  lastError: '已手动重试，自动回退计数已从 2 重开为 0，等待下一轮自动化继续。',
}, 'A deliberate retry after the rollback limit must open a new bounded budget without rebuilding order state.');
assert.equal(rollbackLimitRetry.state.rollbackTargetSignature, 'target-a');
assert.equal(rollbackLimitRetry.state.rollbackTargetRevision, 4);
assert.equal(rollbackLimitRetry.state.lastRuntimeEventSequence, 37);
assert.equal(rollbackLimitRetry.state.prepared, true);
assert.equal(rollbackLimitRetry.state.beverageHandled, true);
assert.equal(rollbackLimitRetry.state.orderKey, 'normal:desk-3:guest-5');
assert.equal(rollbackLimitRetry.state.cookingJobId, 'job-17');
assert.equal(rollbackLimitRetry.state.lastProgressAtMs, 4200);
assert.equal(rollbackLimitRetry.state.detailMessage, '已确认的订单事实必须保留。');
assert.equal(rollbackLimitRetry.state.foodDelivered, true);
assert.equal(rollbackLimitRetry.state.completed, false);
assert.deepEqual(rollbackLimitRetry.state.executionTarget, { foodId: 23, recipeId: 17 });
const transportRetry = reduceAutomationManualRetry({
  ...manualRetryState,
  pauseReasonCode: 'transport-failure',
}, 'complete-order', 6000);
assert.equal(transportRetry.resumed, true);
assert.equal(transportRetry.rollbackBudgetReset, false);
assert.equal(transportRetry.state.rollbackCount, 2,
  'Retrying an unrelated pause must not silently replenish rollback budget.');
assert.equal(transportRetry.state.step, 'ensure-cooking',
  'A manual retry must resume the exact executable stage recorded when the order paused.');
const invalidPausedStageRetry = reduceAutomationManualRetry({
  ...manualRetryState,
  pausedStage: 'paused',
}, 'complete-order', 6500);
assert.equal(invalidPausedStageRetry.state.step, 'complete-order',
  'A non-executable paused stage must use the caller\'s current-facts fallback.');
const manualBarrierState = {
  ...manualRetryState,
  manualResolutionRequired: true,
  pauseReasonCode: 'cooking-delivery-commit-uncertain',
};
const manualBarrierRetry = reduceAutomationManualRetry(
  manualBarrierState,
  'complete-order',
  7000,
);
assert.equal(manualBarrierRetry.resumed, false);
assert.equal(manualBarrierRetry.rollbackBudgetReset, false);
assert.strictEqual(manualBarrierRetry.state, manualBarrierState,
  'A normal Retry must leave a manual safety barrier object untouched.');
assert.strictEqual(manualBarrierRetry.state.manualResolutionRequired, true);
assert.equal(manualBarrierRetry.state.rollbackCount, 2,
  'A normal Retry must never clear the rollback count or manual safety latch of an uncertain side effect.');
const activeRetry = reduceAutomationManualRetry({
  ...manualRetryState,
  paused: false,
  pauseReasonCode: '',
}, 'complete-order', 8000);
assert.equal(activeRetry.resumed, false);
assert.equal(activeRetry.rollbackBudgetReset, false);
assert.equal(activeRetry.state.rollbackCount, 2,
  'An inactive Retry action must not mutate a running order.');
assert.equal(hasAutomationSpecialTargetRotated('', 0, 'target-a', 1), false,
  'Initial target acquisition must not be treated as retirement of an earlier budget.');
assert.equal(hasAutomationSpecialTargetRotated('target-a', 1, 'target-a', 1), false);
assert.equal(hasAutomationSpecialTargetRotated('target-a', 1, 'target-b', 2), true);
assert.equal(hasAutomationSpecialTargetRotated('target-a', 1, 'target-a', 3), true,
  'A -> B -> A must still rotate by runtime revision when the canonical A signature returns.');
assert.equal(hasAutomationSpecialTargetRotated('target-a', 1, '', 0), false,
  'Ending a target action must not manufacture a target-rotation recovery.');
const rollbackState = {
  rollbackCount: 2,
  rollbackTargetSignature: 'target-a',
  rollbackTargetRevision: 1,
  paused: true,
  manualResolutionRequired: false,
  pauseReasonCode: 'rollback-limit-reached',
  step: 'paused',
  stepStartedAtMs: 1000,
  retryCount: 3,
  retryStage: 'ensure-cooking',
  nextAttemptAtMs: 9000,
  lastError: 'limit',
  pausedStage: 'ensure-cooking',
};
const emptyTarget = reconcileAutomationRollbackTarget(rollbackState, '', 0, 5000);
assert.equal(emptyTarget.state.rollbackTargetSignature, 'target-a',
  'A temporary empty target must retain the last nonempty rollback owner.');
assert.equal(emptyTarget.rotated, false);
const returnedCanonicalTarget = reconcileAutomationRollbackTarget(
  emptyTarget.state,
  'target-a',
  3,
  5750,
);
assert.equal(returnedCanonicalTarget.rotated, true,
  'A returned canonical signature must retire stale rollback state when its runtime revision advanced.');
assert.equal(returnedCanonicalTarget.state.rollbackTargetRevision, 3);
const targetUnavailableEvent = reduceAutomationCookingRollbackBudget(1, {
  reasonCode: 'cooking-target-changed-stored',
});
const emptyAfterRuntimeEvent = reconcileAutomationRollbackTarget({
  ...rollbackState,
  rollbackCount: targetUnavailableEvent.rollbackCount,
  paused: false,
  pauseReasonCode: '',
}, '', 0, 5500);
assert.equal(emptyAfterRuntimeEvent.state.rollbackCount, 1,
  'A -> empty plus the runtime storage event must preserve A budget.');
assert.equal(emptyAfterRuntimeEvent.state.rollbackTargetSignature, 'target-a');
const rotatedAfterRuntimeEvent = reconcileAutomationRollbackTarget(
  emptyAfterRuntimeEvent.state,
  'target-b',
  2,
  6000,
);
assert.equal(rotatedAfterRuntimeEvent.state.rollbackCount, 0);
assert.equal(rotatedAfterRuntimeEvent.rotated, true,
  'Only a different nonempty target may retire the retained A budget.');
assert.equal(
  shouldRetainAutomationStateWithoutCandidate(emptyAfterRuntimeEvent.state, true),
  true,
  'An active order must retain its last nonempty rollback owner while the special target is empty.',
);
assert.equal(
  shouldRetainAutomationStateWithoutCandidate(emptyAfterRuntimeEvent.state, false),
  false,
  'A removed order must not keep a stale rollback owner.',
);
assert.equal(
  shouldRetainAutomationStateWithoutCandidate({
    ...emptyAfterRuntimeEvent.state,
    manualResolutionRequired: true,
    rollbackTargetSignature: '',
  }, false),
  true,
  'A manual safety barrier must survive even after its order disappears from the current recommendation set.',
);
const rotatedTarget = reconcileAutomationRollbackTarget(emptyTarget.state, 'target-b', 2, 6000);
assert.equal(rotatedTarget.rotated, true);
assert.equal(rotatedTarget.state.rollbackCount, 0);
assert.equal(rotatedTarget.state.rollbackTargetSignature, 'target-b');
assert.equal(rotatedTarget.state.rollbackTargetRevision, 2);
assert.equal(rotatedTarget.state.paused, false,
  'A rollback-limit pause must resume when a different nonempty target arrives.');
assert.equal(rotatedTarget.state.step, 'ensure-cooking');
const manualBarrier = reconcileAutomationRollbackTarget({
  ...rollbackState,
  manualResolutionRequired: true,
  pauseReasonCode: 'cooking-delivery-commit-uncertain',
}, 'target-b', 2, 6000);
assert.equal(manualBarrier.state.paused, true);
assert.equal(manualBarrier.state.manualResolutionRequired, true,
  'Target rotation must not release a manual safety barrier.');
const unrelatedPause = reconcileAutomationRollbackTarget({
  ...rollbackState,
  pauseReasonCode: 'stage-retry-limit-reached',
}, 'target-b', 2, 6000);
assert.equal(unrelatedPause.state.paused, true,
  'Target rotation must not release an unrelated pause.');
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
}), false, 'A response from before a control-state transition must be ignored.');
assert.equal(isAutomationResponseCurrent({
  requestEpoch: 5,
  currentEpoch: 5,
  runtimeEnabled: false,
}), false, 'A response arriving after the tutorial gate closes must be ignored.');
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
await assertStageAndControlContracts();
await assertMockProtocol();

console.log('PASS: structured automation outcomes preserve waiting state, bound retries, pause blocked jobs, and retain jobs across control-state transitions.');

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

async function assertStageAndControlContracts() {
  const workbench = await readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8');
  const domain = await readFile(new URL('apps/companion/src/companion/domain/automation.ts', root), 'utf8');
  const normalOrderKey = await readFile(new URL('apps/companion/src/companion/domain/normal-order-key.ts', root), 'utf8');
  const intervals = await readFile(new URL('apps/companion/src/companion/hooks/useOrderAutomationIntervals.ts', root), 'utf8');
  const storage = await readFile(new URL('apps/companion/src/companion/storage.ts', root), 'utf8');
  const api = await readFile(new URL('apps/companion/src/companion/api.ts', root), 'utf8');
  const types = await readFile(new URL('apps/companion/src/companion/types.ts', root), 'utf8');
  const servicePanel = await readFile(new URL('apps/companion/src/companion/pages/ModServicePanel.tsx', root), 'utf8');
  const stateMachine = await readFile(new URL('apps/companion/src/companion/automation-state.ts', root), 'utf8');
  const automationMachine = await readFile(new URL('apps/companion/src/companion/automation-machine.ts', root), 'utf8');
  const connection = await readFile(new URL('apps/companion/src/companion/hooks/useCompanionConnection.ts', root), 'utf8');
  const runtime = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.cs', root), 'utf8');
  const delivery = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.Delivery.cs', root), 'utf8');
  const directDelivery = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.DirectDelivery.cs', root), 'utf8');
  const yuumaSettlement = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.YuumaSettlement.cs', root), 'utf8');
  const cooking = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.Cooking.cs', root), 'utf8');
  const runtimeControl = await readFile(new URL('mods/bepinex/src/Save/RuntimeAutomationControlState.cs', root), 'utf8');
  const jobControl = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.AutomationControl.cs', root), 'utf8');
  const specialTargetPolicy = await readFile(
    new URL('mods/bepinex/src/Save/SpecialBusiness/RuntimeOrderPreparationService.SpecialFoodTargetPolicy.cs', root),
    'utf8',
  );
  const cookingLifecycle = await readFile(new URL('mods/bepinex/src/Save/AutomationCookingJobLifecycle.cs', root), 'utf8');
  const orderMatching = await readFile(new URL('mods/bepinex/src/Save/RuntimeOrderPreparationService.OrderMatching.cs', root), 'utf8');
  const generationTracker = await readFile(new URL('mods/bepinex/src/Save/RuntimeCookingGenerationTracker.cs', root), 'utf8');

  assert.match(domain, /order\.orderLifecycleSequence > 0[\s\S]*lifecycle:\$\{order\.orderLifecycleSequence\}/,
    'Rare automation state keys must include the exact order lifecycle sequence.');
  assert.ok(normalOrderKey.includes('buildNormalLifecycleAutoOrderKey')
    && normalOrderKey.includes('`${rawOrderIdentity}|lifecycle:${orderLifecycleSequence}`')
    && normalOrderKey.includes('order.orderKey || order.traceId'),
    'Normal event and snapshot state keys must share one lifecycle-aware constructor.');
  assert.ok(workbench.includes('event.orderLifecycleSequence !== item.order.orderLifecycleSequence'),
    'Rare runtime events must not cross an order lifecycle boundary.');
  assert.ok(workbench.includes('event.orderLifecycleSequence !== order.orderLifecycleSequence'),
    'Normal runtime events must not cross an order lifecycle boundary.');
  assert.ok(workbench.includes('event.orderRuntimeKind')
    && workbench.includes('event.orderControllerId')
    && workbench.includes('event.orderLifecycleSequence'),
  'Manual barrier presentation must group events by the exact backend order lifecycle identity.');
  assert.match(
    workbench,
    /function isManualResolutionAutomationEvent[\s\S]*event\.terminal[\s\S]*event\.outcome === 'blocked'[\s\S]*requiresManualAutomationResolution/,
    'Cancelled old-generation uncertainty events must not become unacknowledgeable manual barriers.',
  );
  assert.ok(api.includes("orderKey: order.orderKey ?? ''"),
    'The UI lifecycle state key must not replace the native normal-order request key.');
  assert.ok(workbench.includes('event.orderKey === order.orderKey')
    && workbench.includes('job.orderKey === order.orderKey'),
  'Normal runtime events and jobs must compare backend raw order keys instead of composite UI state keys.');
  assert.ok(workbench.includes('event.orderKey || event.traceId')
    && workbench.includes('normalOrderStatesRef.current.has(eventStateKey)'),
  'A temporarily missing normal order must recover an event only through its exact composite lifecycle state key.');
  assert.ok((workbench.match(/job\.orderLifecycleSequence !== order\.orderLifecycleSequence/g) ?? []).length >= 2,
    'Rare and normal cooking jobs must not cross an order lifecycle boundary.');
  assert.ok(api.includes('orderLifecycleSequence: String(item.order.orderLifecycleSequence)'),
    'Rare automation requests must carry the exact snapshot lifecycle sequence.');
  assert.ok(api.includes('orderLifecycleSequence: String(order.orderLifecycleSequence)'),
    'Normal automation requests must carry the exact snapshot lifecycle sequence.');

  assert.equal(domain.includes('buildCompleteOrderPreferences'), false, 'Completion must not force unrelated rare stages.');
  assert.ok(runtime.includes('else if (!request.AutoTakeBeverage)'), 'Rare completion must honor the beverage switch.');
  assert.match(
    workbench,
    /autoNormalCompleteOrder: shouldRequestNormalOrderCompletion\(\{/,
    'Normal requests must derive one explicit completion intent before entering the runtime API.',
  );
  assert.match(
    workbench,
    /autoNormalCompleteOrder: shouldRequestNormalOrderCompletion\(\{[\s\S]*beverageDeliveryEnabled:[\s\S]*completionEnabled:[\s\S]*completionReady:[\s\S]*foodDeliveryEnabled:[\s\S]*forceKoishiFullFeedAutomation/,
    'Every direct-delivery request must carry its completion intent into the runtime request.',
  );
  assert.match(api, /assertAutomationDirectDeliveryCompletionInvariant\(\{[\s\S]*preferences\.autoNormalTakeBeverage[\s\S]*preferences\.autoNormalCompleteOrder[\s\S]*preferences\.autoNormalDeliverFood/);
  assert.match(api, /assertAutomationDirectDeliveryCompletionInvariant\(\{[\s\S]*preferences\.autoPrepTakeBeverage[\s\S]*preferences\.autoPrepCompleteOrder[\s\S]*preferences\.autoPrepCollectCooking/);
  assert.match(api, /autoCompleteOrder: String\(preferences\.autoNormalCompleteOrder\)/);
  assert.match(api, /autoCompleteOrder: String\(preferences\.autoPrepCompleteOrder\)/);
  assert.match(
    workbench,
    /autoPrepCompleteOrder: forceKoishiFullFeedAutomation\s*\|\| companionPreferences\.autoPrepCompleteOrder/,
    'Koishi rare full-feed requests must carry completion intent into the exact runtime evaluation route.',
  );
  const stagedRarePrepareRequest = workbench.slice(
    workbench.indexOf('const preparePreferences = {'),
    workbench.indexOf('const prepareResponseAt = Date.now();'),
  );
  assert.match(
    stagedRarePrepareRequest,
    /autoPrepTakeBeverage: shouldPrepareBeverage,[\s\S]*autoPrepStartCooking: shouldPrepareFood/,
    'Rare staged actions must remain controlled exclusively by the current autoPrep action gates.',
  );
  assert.match(
    stagedRarePrepareRequest,
    /prepareNextRareOrder\([\s\S]*specialTargetPolicy,\s*currentState\.recipeTarget,\s*currentState\.beverageTarget,\s*preparePreferences,\s*shouldPrepareFood \? cookerReservation : null/,
    'A food-only retry after beverage delivery must retain the locked beverage target together with the locked recipe target.',
  );
  assert.equal(
    stagedRarePrepareRequest.includes('shouldPrepareFood ? currentState.recipeTarget : null')
      || stagedRarePrepareRequest.includes('shouldPrepareBeverage ? currentState.beverageTarget : null'),
    false,
    'Rare staged requests must not turn an inactive action into missing execution-plan identity.',
  );
  assert.match(
    automationMachine,
    /if \(input\.forceKoishiFullFeedAutomation\) return true;/,
    'Koishi normal full-feed requests must carry completion intent instead of falling through to generic manual handoff.',
  );
  assert.equal(
    `${automationMachine}\n${workbench}\n${api}\n${types}\n${runtime}`.includes('AutoFinalizeCookingJob')
      || `${automationMachine}\n${workbench}\n${api}\n${types}\n${runtime}`.includes('autoFinalizeCookingJob')
      || `${automationMachine}\n${workbench}\n${api}\n${types}\n${runtime}`.includes('resolveAutomationCompletionIntent'),
    false,
    'The removed Blood Pond Hell direct-finalization protocol must not remain as a no-op field.',
  );
  const directYuumaDelivery = directDelivery.indexOf('private static (bool Remove, string Message, string Code) TryDeliverAutomationCookedFood(');
  const directYuumaFinalize = directDelivery.indexOf(
    'return TryFinalizeYuumaCookingJob(job, cookedFood);',
    directYuumaDelivery,
  );
  assert.ok(
    directYuumaDelivery >= 0
      && directYuumaFinalize > directYuumaDelivery
      && !directDelivery.slice(directYuumaDelivery, directYuumaFinalize).includes('job.AutoDeliverFood')
      && !directDelivery.slice(directYuumaDelivery, directYuumaFinalize).includes('job.AutoCompleteOrder'),
    'Blood Pond Hell settlement must use the current control permit instead of creation-time action flags.',
  );
  assert.ok(jobControl.includes('RuntimeAutomationControlStage.YuumaSettlement')
    && jobControl.includes('IsWackyKoishiBossTarget(job.Target)'),
  'A Blood Pond Hell cooking job must use its named combined settlement boundary and preserve the Koishi override.');
  assert.ok(runtimeControl.includes('if (!profile.AutomationEnabled)')
    && runtimeControl.includes('if (!targetEnabled)')
    && runtimeControl.includes('forceStageConfiguration || targetKind switch'),
  'A named stage override may bypass only delivery/completion flags, never the global/group/authority gates.');
  assert.ok(
    yuumaSettlement.includes('TryFinalizeYuumaCookingJob(')
      && yuumaSettlement.includes('TryInvokeYuumaEvaluation('),
    'The guarded Blood Pond Hell automatic path must use the dedicated settlement transaction before evaluation.',
  );
  assert.equal(
    automationMachine.includes("'yuuma-settlement-state-uncertain'"),
    false,
    'The frontend still exposes the removed Blood Pond Hell settlement ACK path.',
  );
  assert.ok(
    specialTargetPolicy.includes('$"autoDeliverFood: {request.AutoDeliverFood}"')
      && specialTargetPolicy.includes('$"autoCompleteOrder: {request.AutoCompleteOrder}"'),
    'Blood Pond Hell request diagnostics must expose the current staged actions.',
  );
  assert.ok(
    specialTargetPolicy.includes('$"{request.AutoDeliverFood}|{request.AutoCompleteOrder}|"'),
    'Blood Pond Hell diagnostic deduplication must distinguish current action transitions.',
  );
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
  assert.ok(
    domain.includes('if (preferences.autoRareOrderEnabled && preferences.autoPrepStartCooking)'),
    'The resource overview must not reserve rare cooker capacity while rare processing is disabled.',
  );
  assert.ok(
    workbench.includes('rareAutomationNeedsRecommendations = automationRuntimeEnabled')
      && workbench.includes('companionPreferences.autoRareOrderEnabled')
      && workbench.includes('resetRareStateWhenDisabled: !companionPreferences.automationEnabled'),
    'The rare group switch must gate recommendation work and own an explicit disable transition.',
  );
  assert.match(
    intervals,
    /if \(!autoRareOrderEnabled\) \{[\s\S]*return undefined;[\s\S]*void runAutoFirstOrder\(\)/,
    'The rare interval must stop scheduling without disabling the independent normal interval.',
  );
  assert.match(
    workbench,
    /const handleRareAutomationDisabled = useCallback\(\(\) => \{[\s\S]*retainRareAutomationExecutionStates\(rareOrderStatesRef\.current\)[\s\S]*retainRareAutomationExecutionDiagnosticItems/,
    'Disabling rare automation must preserve active cooking-job identity and manual-resolution safety state.',
  );
  assert.ok(workbench.includes('window.setInterval(refreshDiagnosticClock, 1000)'), 'Visible automation diagnostics need a one-second display clock.');
  assert.ok(workbench.includes('authoritativeResponseStep: preflightResponseStep'), 'Rare waiting diagnostics must preserve the authoritative preflight stage.');
  assert.ok(workbench.includes('refreshRareOrderDiagnostics(Date.now());'), 'Rare loop completion must use a timestamp taken after its responses.');
  assert.ok(workbench.includes('refreshNormalOrderDiagnostics(orders, Date.now());'), 'Normal loop completion must use a timestamp taken after its responses.');
  assert.equal(storage.includes('AUTOMATION_CANCELLATION'), false,
    'The deleted destructive cancellation barrier must not remain in local storage.');
  assert.equal(workbench.includes('AutomationCancellation'), false,
    'The workbench still carries the deleted cancellation request or ACK state machine.');
  assert.equal(automationMachine.includes('AutomationCancellation'), false,
    'The frontend state machine still exposes targeted job deletion helpers.');
  const preferenceUpdate = workbench.slice(
    workbench.indexOf('const updateCompanionPreferences = useCallback('),
    workbench.indexOf('useEffect(() => {', workbench.indexOf('const updateCompanionPreferences = useCallback(')),
  );
  assert.equal(preferenceUpdate.includes('cancel'), false,
    'A preference edit must not delete active cooking jobs from local state.');
  assert.ok(workbench.includes('function buildAutomationControlSignature(')
    && workbench.includes('preferences.autoPrepCollectCooking')
    && workbench.includes('preferences.autoPrepCompleteOrder')
    && workbench.includes('preferences.autoNormalDeliverFood')
    && workbench.includes('preferences.autoNormalCompleteOrder'),
  'The control-state transition signature must include both order groups and their future delivery/evaluation stages.');
  assert.ok(workbench.includes('previousAutomationControlSignatureRef.current')
    && workbench.includes('automationRequestEpochRef.current += 1')
    && workbench.includes('automationLeaseRevalidationRequiredRef.current = true')
    && workbench.includes('setAutomationLease(null)')
    && workbench.includes('await waitForAutomationLeaseAcquire()')
    && workbench.includes('await releaseAutomationLease('),
  'A live control-state change must stop local scheduling, wait for in-flight acquire, and release the old lease.');
  assert.ok(workbench.includes('AUTOMATION_CONTROL_DETAIL_PREFIX')
    && workbench.includes("job.controlState === 'active'")
    && workbench.includes('job.controlMessage'),
  'Suspended cooking jobs must expose the backend control reason without becoming an error-pause state.');
  assert.ok(workbench.includes('retryNormalAutomationOrder'));
  assert.ok(workbench.includes('resetNormalAutomationOrder'));
  assert.equal(
    (workbench.match(/reduceAutomationManualRetry\(/g) ?? []).length,
    2,
    'Rare and normal Retry buttons must share the same pure state transition.',
  );
  assert.ok(servicePanel.includes('normal-auto:${diagnostic.orderKey}:retry'));
  assert.ok(servicePanel.includes('normal-auto:${diagnostic.orderKey}:reset'));
  assert.ok(stateMachine.includes('manualResolutionRequired: true'));
  assert.ok(stateMachine.includes("prepared: state.prepared || responseStep === 'ensure-cooking' || responseStep === 'deliver-food'"));
  assert.ok(domain.includes('!base.manualResolutionRequired && snapshotPassesPausedStage'));
  assert.ok(domain.includes('!state.manualResolutionRequired && snapshotPassesPausedStage'));
  assert.ok(workbench.includes('function retainRareAutomationExecutionStates(')
    && workbench.includes('!state.manualResolutionRequired && !state.cookingJobId'),
  'Rare control suspension must retain exact cooking-job identity while dropping unrelated local work.');
  assert.ok(workbench.includes('retainNormalAutomationExecutionStates(normalOrderStatesRef.current)'));
  assert.equal(automationMachine.includes('retainAutomationManualResolutionStates'), false,
    'The obsolete cancellation-only local state cleanup helper must be removed.');
  assert.ok(workbench.includes('lastRuntimeEventSequence: state.lastRuntimeEventSequence'));
  assert.equal(
    (workbench.match(/reduceAutomationCookingRollbackBudget\(state\.rollbackCount, event\)/g) ?? []).length,
    2,
    'Rare and normal runtime mismatch recovery must share the structured rollback-budget policy.',
  );
  assert.ok(workbench.includes("'automation-rollback-budget-retired'"),
    'Target-rotation budget retirement must be written through the existing structured automation diagnostic endpoint.');
  assert.ok(workbench.includes('publishAutomationTargetRotationDiagnostic({'),
    'A pure signature rotation must publish a bounded structured retirement diagnostic.');
  assert.ok(workbench.includes('previousRevision=${input.previousRevision}')
    && workbench.includes('nextRevision=${input.nextRevision}'),
    'Target-rotation diagnostics must distinguish an A -> B -> A return by runtime revision.');
  assert.ok(workbench.includes('retainRareAutomationContinuityStates('),
    'A temporary zero-candidate recommendation pass must preserve an active order rollback owner.');
  assert.ok(workbench.includes('orderRecommendations.recommendations.map(buildAutoOrderKey)'),
    'Zero-candidate retention must be bounded by orders that still exist in the current recommendation result.');
  assert.ok(workbench.includes('source=target-signature-reconciliation'),
    'Target-signature retirement diagnostics must identify their non-runtime-event source.');
  assert.equal(workbench.includes('selectionCount: 1,\n      skipCount: 0,'), false,
    'Rollback diagnostics must not claim a selection when selectionLines is empty.');
  assert.ok(domain.includes('reconcileAutomationRollbackTarget('),
    'Rare special-target reconciliation must retire an old budget even before the terminal event snapshot arrives.');
  assert.equal(
    (workbench.match(/reconcileAutomationRollbackTarget\(/g) ?? []).length >= 2,
    true,
    'Normal-order scheduling and execution must reconcile target ownership before paused orders are skipped.',
  );
  assert.ok(stateMachine.includes('rollbackTargetSignature: string;'),
    'Rare and normal automation states must retain the last nonempty rollback-owner signature.');
  assert.ok(stateMachine.includes('rollbackTargetRevision: number;'),
    'Rare and normal automation states must retain the independent runtime target revision.');
  assert.ok(domain.includes('state.recipeTargetRevision !== revision'),
    'Rare target locking must invalidate an A -> B -> A return even when its canonical signature matches.');
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
  const controlReleaseStart = workbench.indexOf('const previousSignature = previousAutomationControlSignatureRef.current;');
  const controlReleaseEnd = workbench.indexOf('useEffect(() => {', controlReleaseStart);
  const controlReleaseEffect = workbench.slice(controlReleaseStart, controlReleaseEnd);
  const waitIndex = controlReleaseEffect.indexOf('await waitForAutomationLeaseAcquire();');
  const releaseIndex = controlReleaseEffect.indexOf('await releaseAutomationLease(', waitIndex);
  assert.ok(controlReleaseStart >= 0 && waitIndex >= 0 && releaseIndex > waitIndex,
    'A configuration transition must wait for the single in-flight acquire before releasing the old lease.');
  assert.ok(controlReleaseEffect.includes('const previousRelease = automationControlReleaseRef.current?.promise;')
    && controlReleaseEffect.includes('if (previousRelease) await previousRelease;')
    && controlReleaseEffect.includes('automationControlReleasePendingRef.current = true')
    && controlReleaseEffect.includes('setAutomationControlReleasePending(true)'),
  'Rapid control-state changes must serialize release requests and close lease reacquisition before the first release starts.');
  assert.ok(controlReleaseEffect.includes('automationRuntimeEnabledRef.current = false')
    && controlReleaseEffect.includes('setAutomationLease(null)')
    && controlReleaseEffect.includes('setAutomationLeaseBindingKey(\'\')'),
  'A configuration transition must stop local scheduling before the backend profile revision changes.');
  assert.equal(controlReleaseEffect.includes('rareOrderStatesRef.current.delete'), false);
  assert.equal(controlReleaseEffect.includes('normalOrderStatesRef.current.delete'), false,
    'A control-state transition must retain both groups\' active cooking-job identity.');
  assert.ok(workbench.includes('|| automationControlReleasePending) return undefined;')
    && workbench.includes('if (automationControlReleasePendingRef.current) return;'),
  'Lease renewal must remain closed until the latest serialized control-state release finishes.');
  assert.ok(connection.includes('if (inFlightRequestIdRef.current !== null) return null;')
    && connection.includes('if (isSnapshotUnchanged(data))')
    && connection.includes('return currentSnapshot;')
    && connection.includes('return data;'),
  'A forced refresh must return the actual full or cached snapshot for job-control convergence.');
  assert.ok(api.includes('export async function releaseAutomationLease(')
    && api.includes("'/automation/lease/release'"),
  'The frontend must use the canonical explicit lease-release endpoint.');
  assert.equal(api.includes('/automation/cancel'), false);
  assert.equal(api.includes('/automation/jobs/cancel'), false);
  assert.ok(servicePanel.includes('diagnostic.manualResolutionRequired ? \'确认已处理\' : \'重置\''));
  assert.ok(servicePanel.includes('!diagnostic.paused || diagnostic.manualResolutionRequired'));
  assert.ok(api.includes('/automation/barriers/ack?${params.toString()}'), 'The frontend must use the canonical safety-barrier ACK endpoint.');
  assert.ok(api.includes("params.set('runtimeGuestId', String(item.order.runtimeGuestId))"), 'Rare automation must send the raw runtime guest identity.');
  const normalOrderActionStart = api.indexOf('export async function completeFirstNormalOrder(');
  const normalOrderActionEnd = api.indexOf('export async function readFavorites(', normalOrderActionStart);
  const normalOrderAction = api.slice(normalOrderActionStart, normalOrderActionEnd);
  assert.ok(
    normalOrderAction.includes("if (order.runtimeGuestId != null) params.set('runtimeGuestId', String(order.runtimeGuestId))"),
    'Normal automation must send the classifier-verified runtime guest identity.',
  );
  assert.ok(api.includes("params.set('foodTagId', String(item.order.foodTagId))"), 'Rare automation must send the raw food Tag identity.');
  assert.ok(api.includes("params.set('beverageTagId', String(item.order.beverageTagId))"), 'Rare automation must send the raw beverage Tag identity.');
  assert.ok(domain.includes("'runtime-identity-missing'"), 'Orders with incomplete runtime identity must be skipped before automation requests.');
  assert.ok(types.includes('acknowledgedSequences: number[];'), 'The ACK response must expose every barrier sequence cleared by the Mod.');
  assert.ok(workbench.includes('automationLeaseOwnedRef.current'), 'ACK must require the current-session automation lease.');
  assert.ok(workbench.includes('&& nightBusinessAutomationAllowed;'),
    'Frontend automation scheduling must consume the authoritative tutorial gate from the snapshot.');
  assert.ok(workbench.includes('previousAutomationRuntimeEnabledRef.current && !automationRuntimeEnabled'),
    'Closing the runtime tutorial gate must advance the request epoch and isolate late responses.');
  assert.ok(workbench.includes('resetStateWhenDisabled: !companionPreferences.automationEnabled'),
    'A runtime-only tutorial pause must preserve rare-order automation state.');
  assert.ok(workbench.includes('resetNormalStateWhenDisabled: !companionPreferences.automationEnabled'),
    'A runtime-only tutorial pause must preserve normal-order automation state.');
  assert.ok(workbench.includes('getNightBusinessAutomationPauseMessage('),
    'The UI must explain a tutorial pause without changing the stored automation preference.');
  assert.ok(servicePanel.includes('runtimePauseLabel && <Badge variant="destructive">'),
    'Rare and normal automation status rows must show the runtime gate separately from order pauses.');
  assert.equal(servicePanel.includes("{paused ? '已暂停' : '运行中'}"), false,
    'The rare global status must not claim automation is running while the tutorial gate is closed.');
  assert.ok(workbench.includes('clearAcknowledgedAutomationBarriers(response.acknowledgedSequences'), 'ACK success must clear every frontend latch acknowledged by the Mod.');
  assert.ok(workbench.match(/canAdvanceAutomationRuntimeEventSequence\(state\.manualResolutionRequired, manualResolutionRequired\)/g)?.length >= 2, 'Rare and normal reducers must both preserve manual barrier sequences.');
  assert.ok(workbench.includes('events.filter(isManualResolutionAutomationEvent)'), 'Same-session barrier convergence must use the unresolved manual-event set from the snapshot.');
  assert.ok(workbench.match(/shouldRetireMissingManualBarrier\(/g)?.length >= 2, 'Rare and normal latches must converge after another window acknowledges their barrier.');
  assert.ok(workbench.includes("event.code.startsWith('order-')"), 'Order-evaluation barriers must map to the order stage.');
  assert.ok(stateMachine.includes("'order-evaluation-state-unreadable'"));
  assert.ok(stateMachine.includes("'order-evaluation-commit-uncertain'"));
  assert.ok(stateMachine.includes("'order-evaluation-target-mismatch'"));
  assert.ok(stateMachine.includes("'order-evaluation-closeout-unresolved'"));
  assert.ok(stateMachine.includes("'order-terminated-before-evaluation'"));
  assert.ok(stateMachine.includes("'cooking-manual-handoff-unreadable'"));
  assert.ok(stateMachine.includes("'cooking-manual-handoff-expired'"));
  assert.ok(stateMachine.includes("'cooking-manual-handoff-resolved'"));
  assert.ok(stateMachine.includes("'cooking-cooker-waiting'"));
  assert.ok(stateMachine.includes("'cooking-ownership-lost'"));
  assert.equal(stateMachine.includes("'cooking-result-removed'"), false);
  assert.ok(automationMachine.includes("'cooking-manual-handoff-unreadable'"));
  assert.equal(
    automationMachine.includes("'cooking-manual-handoff-target-changed'"),
    false,
    'A rotated handoff must no longer be treated as a recoverable terminal that starts another cook.',
  );
  assert.ok(
    workbench.match(/activeCookingJob\?\.state === 'manual-handoff-expired'/g)?.length >= 2,
    'Rare and normal automation must both retain an expired handoff without issuing another food request.',
  );
  const normalAdmissionStart = workbench.indexOf('const runnableOrders: NormalBusinessOrder[] = [];');
  const normalExpiredGate = workbench.indexOf(
    "activeCookingJob?.state === 'manual-handoff-expired'",
    normalAdmissionStart,
  );
  const normalAdmission = workbench.indexOf('runnableOrders.push(order);', normalAdmissionStart);
  assert.ok(
    normalAdmissionStart >= 0
      && normalExpiredGate > normalAdmissionStart
      && normalAdmission > normalExpiredGate,
    'An expired normal-order handoff must be excluded before it can consume a concurrency slot.',
  );
  assert.ok(
    workbench.includes('if (detailMessage === state.detailMessage) return state;'),
    'An unchanged automation detail must preserve its timestamp instead of refreshing every poll.',
  );
  assert.ok(
    workbench.includes('if (expiredHandoffState !== currentState)'),
    'An unchanged rare-order expired-handoff detail must not increment the updated-order count.',
  );
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
  for (const field of [
    'transactionStage',
    'controlState',
    'controlReasonCode',
    'controlMessage',
    'controlAuthorityRevision',
    'controlStage',
    'controlSuspendedAtUtc',
    'holdsControllerReservation',
    'controllerLeaseReleaseReason',
    'orderRuntimeKind',
    'orderId',
    'orderControllerId',
    'orderLifecycleSequence',
    'foodDeliveryCleanupCompleted',
    'foodDeliveryCleanupTerminal',
    'foodDeliveryEvaluationState',
    'foodDeliveryEvaluationAttempts',
    'foodDeliveryEvaluationEffectiveSeconds',
  ]) {
    assert.ok(types.includes(`${field}:`), `Automation cooking-job API type is missing ${field}.`);
  }
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
    const enabledProfile = {
      automationEnabled: true,
      autoRareOrderEnabled: true,
      autoNormalOrderEnabled: true,
      autoPrepCollectCooking: true,
      autoPrepCompleteOrder: true,
      autoNormalDeliverFood: true,
      autoNormalCompleteOrder: true,
    };
    const registrationResponse = await fetch(`http://127.0.0.1:${port}/devices/register`, {
      method: 'POST',
      headers: { ...headers, 'content-type': 'application/json; charset=utf-8' },
      body: JSON.stringify({
        protocolVersion: 1,
        profileSchemaVersion: 1,
        platform: 'browser',
        appVersion: '1.2.0',
        profile: enabledProfile,
      }),
    });
    assert.equal(registrationResponse.ok, true);
    const registration = await registrationResponse.json();
    let runtimeHeaders = {
      ...headers,
      'x-mystia-steward-companion-authority-revision': String(registration.authorityRevision),
    };
    const deniedAck = await postJson(`http://127.0.0.1:${port}/automation/barriers/ack?sequence=9001`, headers);
    assert.equal(deniedAck.ok, false);
    assert.deepEqual(deniedAck.acknowledgedSequences, []);

    const lease = await postJson(`http://127.0.0.1:${port}/automation/lease/acquire`, runtimeHeaders);
    assert.equal(lease.owned, true);

    const removedCancellationResponse = await fetch(
      `http://127.0.0.1:${port}/automation/cancel`,
      { method: 'POST', headers: runtimeHeaders },
    );
    assert.equal(removedCancellationResponse.status, 404,
      'The mock must not retain the destructive cancellation route as a compatibility alias.');

    const snapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.equal(snapshot.automationSessionId, 'automation-audit-session');
    assert.equal(snapshot.nightBusinessAutomationAllowed, true);
    assert.equal(snapshot.nightBusinessAutomationBlockReason, '');
    assert.equal(snapshot.runtimeNightBusinessAutomationStatus, 'mock automation allowed generation=1');
    assert.ok(Array.isArray(snapshot.automationCookingJobs));
    assert.ok(Array.isArray(snapshot.automationEvents));
    assert.deepEqual(snapshot.automationEvents.map((event) => event.sequence), [9000, 9001]);
    assert.deepEqual(snapshot.nightBusiness.orders.map((order) => order.orderLifecycleSequence), [1, 2]);
    assert.deepEqual(snapshot.normalBusiness.orders.map((order) => order.orderLifecycleSequence), [3, 4]);
    for (const event of snapshot.automationEvents) {
      assert.equal(event.orderRuntimeKind, 'Special');
      assert.equal(event.orderId, '0x1001');
      assert.equal(event.orderControllerId, '0x2001');
      assert.equal(event.orderLifecycleSequence, 7);
    }

    const missingAck = await postJson(`http://127.0.0.1:${port}/automation/barriers/ack?sequence=9999`, runtimeHeaders);
    assert.equal(missingAck.ok, false);
    assert.deepEqual(missingAck.acknowledgedSequences, []);
    const unchangedSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.deepEqual(unchangedSnapshot.automationEvents.map((event) => event.sequence), [9000, 9001]);

    const acknowledged = await postJson(`http://127.0.0.1:${port}/automation/barriers/ack?sequence=9001`, runtimeHeaders);
    assert.equal(acknowledged.ok, true);
    assert.equal(acknowledged.sequence, 9001);
    assert.equal(acknowledged.acknowledgedCount, 2);
    assert.deepEqual(acknowledged.acknowledgedSequences, [9000, 9001]);

    const duplicateAck = await postJson(`http://127.0.0.1:${port}/automation/barriers/ack?sequence=9001`, runtimeHeaders);
    assert.equal(duplicateAck.ok, false);
    assert.deepEqual(duplicateAck.acknowledgedSequences, []);

    const acknowledgedSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.deepEqual(acknowledgedSnapshot.automationEvents, []);
    assert.notEqual(acknowledgedSnapshot.snapshotSignature, snapshot.snapshotSignature);

    const missingLifecycle = await fetch(
      `http://127.0.0.1:${port}/orders/prepare-next`,
      { method: 'POST', headers: runtimeHeaders },
    );
    assert.equal(missingLifecycle.status, 400);
    assert.equal((await missingLifecycle.json()).error, 'missing or invalid orderLifecycleSequence');

    const response = await postJson(
      `http://127.0.0.1:${port}/orders/prepare-next?orderLifecycleSequence=1`,
      runtimeHeaders,
    );
    assert.equal(response.automation.outcome, 'progressed');
    assert.equal(response.automation.stage, 'cooking-start');
    assert.equal(response.automation.reasonCode, 'cooking-started');
    assert.equal(typeof response.automation.jobId, 'string');

    const activeSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    const activeJob = activeSnapshot.automationCookingJobs.find((job) => job.jobId === response.automation.jobId);
    assert.ok(activeJob, 'Mock snapshot must expose the cooking job created by the action response.');
    assert.equal('autoFinalizeCookingJob' in activeJob, false);
    assert.equal('autoDeliverFood' in activeJob, false,
      'A cooking job must not latch its creation-time delivery switch.');
    assert.equal(activeJob.controlState, 'active');
    assert.equal(activeJob.controlReasonCode, '');
    assert.equal(activeJob.controlAuthorityRevision, registration.authorityRevision);
    assert.equal(activeJob.controlStage, 'FoodDelivery');
    assert.equal(activeJob.controlSuspendedAtUtc, null);
    assert.equal(activeJob.warmerStoreCommitUncertain, false);
    assert.equal(activeJob.foodDeliveryCommitted, false);
    assert.equal(activeJob.foodDeliveryCommitUncertain, false);
    assert.equal(activeJob.foodDeliveryCleanupAttempts, 0);
    assert.equal(activeJob.transactionStage, 'cooking');
    assert.equal(activeJob.holdsControllerReservation, true);
    assert.equal(activeJob.controllerLeaseReleaseReason, '');
    assert.equal(activeJob.orderRuntimeKind, 'Special');
    assert.equal(activeJob.orderId, 'mock-order-1');
    assert.equal(activeJob.orderControllerId, 'mock-order-controller-1');
    assert.equal(activeJob.orderLifecycleSequence, 1);
    assert.equal(activeJob.foodDeliveryCleanupCompleted, false);
    assert.equal(activeJob.foodDeliveryCleanupTerminal, false);
    assert.equal(activeJob.foodDeliveryEvaluationState, 'Pending');
    assert.equal(activeJob.foodDeliveryEvaluationAttempts, 0);
    assert.equal(activeJob.foodDeliveryEvaluationEffectiveSeconds, 0);

    const released = await postJson(
      `http://127.0.0.1:${port}/automation/lease/release`,
      runtimeHeaders,
    );
    assert.equal(released.ok, true);
    assert.equal(released.owned, false);
    const leaseSuspendedSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.equal(leaseSuspendedSnapshot.automationCookingJobs.length, 1);
    assert.equal(leaseSuspendedSnapshot.automationCookingJobs[0].jobId, activeJob.jobId);
    assert.equal(leaseSuspendedSnapshot.automationCookingJobs[0].controlState, 'suspended-authority');
    assert.equal(leaseSuspendedSnapshot.automationCookingJobs[0].controlReasonCode, 'automation-lease-released');

    const reacquired = await postJson(`http://127.0.0.1:${port}/automation/lease/acquire`, runtimeHeaders);
    assert.equal(reacquired.owned, true);
    const leaseResumedSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.equal(leaseResumedSnapshot.automationCookingJobs[0].jobId, activeJob.jobId);
    assert.equal(leaseResumedSnapshot.automationCookingJobs[0].controlState, 'active');

    const deliveryDisabledProfile = { ...enabledProfile, autoPrepCollectCooking: false };
    const disabledAuthority = await postJsonBody(
      `http://127.0.0.1:${port}/devices/profile`,
      headers,
      {
        protocolVersion: 1,
        profileSchemaVersion: 1,
        expectedAuthorityRevision: registration.authorityRevision,
        expectedProfileRevision: registration.activeProfileRevision,
        profile: deliveryDisabledProfile,
      },
    );
    runtimeHeaders = {
      ...headers,
      'x-mystia-steward-companion-authority-revision': String(disabledAuthority.authorityRevision),
    };
    const profileTransitionSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.equal(profileTransitionSnapshot.automationCookingJobs[0].jobId, activeJob.jobId);
    assert.equal(profileTransitionSnapshot.automationCookingJobs[0].controlState, 'suspended-authority');
    assert.equal(profileTransitionSnapshot.automationCookingJobs[0].controlReasonCode, 'automation-profile-changing');
    const disabledLease = await postJson(`http://127.0.0.1:${port}/automation/lease/acquire`, runtimeHeaders);
    assert.equal(disabledLease.owned, true);
    const configurationSuspendedSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.equal(configurationSuspendedSnapshot.automationCookingJobs[0].jobId, activeJob.jobId);
    assert.equal(configurationSuspendedSnapshot.automationCookingJobs[0].controlState, 'suspended-configuration');
    assert.equal(configurationSuspendedSnapshot.automationCookingJobs[0].controlReasonCode, 'rare-food-delivery-disabled');

    const enabledAuthority = await postJsonBody(
      `http://127.0.0.1:${port}/devices/profile`,
      headers,
      {
        protocolVersion: 1,
        profileSchemaVersion: 1,
        expectedAuthorityRevision: disabledAuthority.authorityRevision,
        expectedProfileRevision: disabledAuthority.activeProfileRevision,
        profile: enabledProfile,
      },
    );
    runtimeHeaders = {
      ...headers,
      'x-mystia-steward-companion-authority-revision': String(enabledAuthority.authorityRevision),
    };
    await postJson(`http://127.0.0.1:${port}/automation/lease/acquire`, runtimeHeaders);
    const configurationResumedSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.equal(configurationResumedSnapshot.automationCookingJobs[0].jobId, activeJob.jobId);
    assert.equal(configurationResumedSnapshot.automationCookingJobs[0].controlState, 'active');

    const nextHeaders = {
      'x-mystia-steward-companion-client-id': 'automation-audit-next',
      'x-mystia-steward-companion-client-label': 'Automation Audit Next',
    };
    const nextRegistration = await postJsonBody(
      `http://127.0.0.1:${port}/devices/register`,
      nextHeaders,
      {
        protocolVersion: 1,
        profileSchemaVersion: 1,
        platform: 'browser',
        appVersion: '1.2.0',
        profile: enabledProfile,
      },
    );
    const switchedAuthority = await postJsonBody(
      `http://127.0.0.1:${port}/devices/primary`,
      headers,
      {
        protocolVersion: 1,
        expectedAuthorityRevision: nextRegistration.authorityRevision,
        deviceId: nextRegistration.currentDeviceId,
      },
    );
    const switchSuspendedSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.equal(switchSuspendedSnapshot.automationCookingJobs[0].jobId, activeJob.jobId);
    assert.equal(switchSuspendedSnapshot.automationCookingJobs[0].controlState, 'suspended-authority');
    assert.equal(switchSuspendedSnapshot.automationCookingJobs[0].controlReasonCode, 'automation-primary-device-changing');
    const nextRuntimeHeaders = {
      ...nextHeaders,
      'x-mystia-steward-companion-authority-revision': String(switchedAuthority.authorityRevision),
    };
    const nextLease = await postJson(`http://127.0.0.1:${port}/automation/lease/acquire`, nextRuntimeHeaders);
    assert.equal(nextLease.owned, true);
    const switchResumedSnapshot = await getJson(`http://127.0.0.1:${port}/snapshot`);
    assert.equal(switchResumedSnapshot.automationCookingJobs[0].jobId, activeJob.jobId);
    assert.equal(switchResumedSnapshot.automationCookingJobs[0].controlState, 'active');

    const superseded = await postJson(
      `http://127.0.0.1:${port}/orders/prepare-next?orderLifecycleSequence=2`,
      runtimeHeaders,
    );
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

async function postJsonBody(url, headers, body) {
  const response = await fetch(url, {
    method: 'POST',
    headers: { ...headers, 'content-type': 'application/json; charset=utf-8' },
    body: JSON.stringify(body),
  });
  assert.equal(response.ok, true);
  return response.json();
}
