import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const read = (relativePath) => readFileSync(path.join(repoRoot, relativePath), 'utf8');

const server = read('mods/bepinex/src/LocalApi/LocalApiServer.cs');
const models = read('mods/bepinex/src/LocalApi/LocalApiModels.cs');
const dtos = read('mods/bepinex/src/LocalApi/LocalApiDtos.cs');
const controller = read('mods/bepinex/src/Ui/StewardOverlayController.cs');
const state = read('mods/bepinex/src/Save/RuntimeRareGuestParticipationState.cs');
const automationControl = read('mods/bepinex/src/Save/RuntimeAutomationControlState.cs');
const preparation = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.cs');
const preparationControl = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.AutomationControl.cs');
const participationPolicy = read('mods/bepinex/src/Save/RuntimeRareCookingJobParticipationPolicy.cs');
const cooking = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.Cooking.cs');
const directDelivery = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.DirectDelivery.cs');
const capture = read('mods/bepinex/src/Save/SpecialOrderRuntimeCapture.cs');
const uiTargetSet = read('mods/bepinex/src/Save/RuntimeUiTargetSet.cs');
const uiPinning = read('mods/bepinex/src/Save/RuntimeUiPinningService.cs');
const dotnet6Runner = read('scripts/run-dotnet6-smoke.mjs');

assert.match(server, /case "\/orders\/rare\/participation":/u);
assert.match(server, /UpdateRareGuestParticipation\(request, requestData\)/u);
for (const field of [
  'expectedAuthorityRevision',
  'expectedBusinessGeneration',
  'expectedParticipationRevision',
  'action',
  'target',
  'expectedCurrentOrders',
]) {
  assert.match(server, new RegExp(`"${field}"`, 'u'));
}
assert.match(server, /ValidateRareGuestParticipationMutationJson/u);
assert.match(server, /TryAuthorizeRuntimeWriter/u);
const mutationStart = server.indexOf('private LocalApiRareGuestParticipationMutationDto UpdateRareGuestParticipation');
const mutationEnd = server.indexOf('private CompanionDeviceAuthorityStateDto UpdateCompanionDeviceProfile', mutationStart);
const mutation = server.slice(mutationStart, mutationEnd);
assert.ok(mutationStart >= 0 && mutationEnd > mutationStart);
assert.match(mutation, /SnapshotRareGuestParticipationModuleEnabled/u);
assert.ok(
  mutation.indexOf('SnapshotRareGuestParticipationModuleEnabled')
    < mutation.indexOf('_automationCommandEpoch = checked'),
  'A disabled participation module advanced the automation command epoch before rejecting mutation.',
);
assert.ok(
  mutation.indexOf('_advanceAutomationCommandEpoch')
    < mutation.indexOf('SnapshotActiveRareAutomationQueueCandidates'),
  'Active queue anchors were captured before admitted automation commands drained.',
);
assert.ok(
  mutation.indexOf('SnapshotActiveRareAutomationQueueCandidates')
    < mutation.indexOf('RuntimeRareGuestParticipationState.MutateParticipation'),
  'Participation mutated before active automation anchors were captured.',
);
assert.ok(
  mutation.indexOf('_advanceAutomationCommandEpoch')
    < mutation.indexOf('RuntimeRareGuestParticipationState.MutateParticipation'),
  'Participation mutated before queued automation commands were fenced.',
);
assert.match(mutation, /RuntimeUiPinningService\.ReadTargetSet/u);
assert.match(mutation, /RuntimeRareGuestParticipationAction\.EnableFront/u);
assert.match(mutation, /RuntimeRareGuestParticipationTargetScope\.(?:Guest|Order)/u);
assert.match(mutation, /RuntimeRareGuestParticipationConflictException/u);
assert.ok(
  mutation.indexOf('RuntimeRareGuestParticipationState.MutateParticipation')
    < mutation.indexOf('RuntimeUiPinningService.RemoveRareTargetIfMatches'),
  'Paused rare UI target was cleared before the atomic participation mutation succeeded.',
);

const uiPublicationStart = server.indexOf('private static string PublishUiPinningTargetsWithParticipationGate');
const uiPublicationEnd = server.indexOf('private static void ValidateUiPinningTargetParameters', uiPublicationStart);
const uiPublication = server.slice(uiPublicationStart, uiPublicationEnd);
assert.ok(uiPublicationStart >= 0 && uiPublicationEnd > uiPublicationStart);
assert.match(server, /"GuestId"/u);
assert.match(uiPublication, /SnapshotManagedRareGuestIds/u);
assert.match(uiPublication, /managedGuestIds\.Count == 0/u);
assert.match(uiPublication, /AcquireAdmissionPermit/u);
assert.match(uiPublication, /participation\.ManagedGuestIds/u);
assert.match(uiPublication, /permit\.Decision\.Order\.Managed != configuredAsManaged/u);
assert.ok(
  uiPublication.indexOf('AcquireAdmissionPermit')
    < uiPublication.lastIndexOf('RuntimeUiPinningService.UpdateTargets'),
  'Rare UI target publication was not fenced by the short-lived participation permit.',
);

assert.match(uiTargetSet, /public int GuestId \{ get; \}/u);
assert.match(uiTargetSet, /RuntimeUiTargetKind\.Rare && guestId < -1/u);
assert.match(uiTargetSet, /RuntimeUiTargetKind\.Normal && guestId != -1/u);
assert.match(uiPinning, /RemoveRareTargetIfMatches/u);
assert.match(uiPinning, /lock \(TargetPublicationRoot\)/u);
assert.match(uiPinning, /target\.GuestId == guestId/u);
assert.match(uiPinning, /PublishTargets\(sessionGeneration, remainingTargets\)/u);

assert.match(dtos, /class LocalApiRareGuestParticipationMutationRequest/u);
assert.match(dtos, /class LocalApiRareGuestParticipationIdentityDto/u);
assert.match(dtos, /class LocalApiRareGuestParticipationMutationDto/u);
assert.match(models, /RareGuestParticipation \{ get; init; \}/u);
const publicProjectionStart = models.indexOf('internal sealed class LocalApiRareGuestParticipationSnapshot');
const publicProjectionEnd = models.indexOf('internal sealed class AutomationRuntimeEvent', publicProjectionStart);
const publicProjection = models.slice(publicProjectionStart, publicProjectionEnd);
assert.ok(publicProjectionStart >= 0 && publicProjectionEnd > publicProjectionStart);
assert.doesNotMatch(publicProjection, /Binding|OrderPointer|ControllerPointer/u);
for (const scalar of [
  'TraceId',
  'OrderLifecycleSequence',
  'GuestId',
  'Managed',
  'Participating',
  'ReasonCode',
  'QueuePosition',
]) {
  assert.match(publicProjection, new RegExp(`public .* ${scalar} `, 'u'));
}

assert.match(controller, /collectionComplete = nightBusiness != null\s*&& string\.IsNullOrWhiteSpace\(nightBusiness\.Error\)/u);
assert.match(controller, /ReconcileCurrentOrders\(\s*lifecycle\.Generation,\s*state\.Revision,\s*collectionComplete/u);
assert.match(controller, /order\.GuestId \?\? -1/u);
assert.match(controller, /AppendRareGuestParticipation\(builder, snapshot\.RareGuestParticipation\)/u);
assert.match(controller, /Rare-guest participation projection retained its previous complete state/u);
const prepareActionStart = controller.indexOf('private OrderPreparationResult ApplyOrderPreparation(');
const prepareActionEnd = controller.indexOf('private static OrderPreparationResult BuildUnavailableOrderResult(', prepareActionStart);
const prepareAction = controller.slice(prepareActionStart, prepareActionEnd);
assert.ok(prepareActionStart >= 0 && prepareActionEnd > prepareActionStart);
assert.ok(
  prepareAction.indexOf('AcquireRareOrderParticipationAdmission')
    < prepareAction.indexOf('RuntimeOrderPreparationService.Prepare('),
  'Rare preparation reached the runtime service before exact participation admission.',
);
assert.match(
  prepareAction,
  /requireRareParticipationBinding:\s*participationPermit\s*!=\s*null/u,
  'Rare preparation did not pass its already-admitted participation context into the runtime service.',
);
const completionActionStart = controller.indexOf('private OrderPreparationResult ApplyOrderCompletion(');
const completionActionEnd = controller.indexOf('private OrderPreparationResult ApplyNormalOrderCompletion(', completionActionStart);
const completionAction = controller.slice(completionActionStart, completionActionEnd);
assert.ok(completionActionStart >= 0 && completionActionEnd > completionActionStart);
assert.ok(
  completionAction.indexOf('AcquireRareOrderParticipationAdmission')
    < completionAction.indexOf('RuntimeOrderPreparationService.CompleteFirst('),
  'Rare completion reached the runtime service before exact participation admission.',
);
assert.match(
  completionAction,
  /requireRareParticipationBinding:\s*participationPermit\s*!=\s*null/u,
  'Rare completion did not pass its already-admitted participation context into the runtime service.',
);

assert.match(preparation, /RuntimeRareGuestParticipationState\.TryEnrichCurrentBinding/u);
assert.match(preparation, /managed rare-order participation requires a canonical guest identity/u);
const bindingStart = preparation.indexOf('private static bool TryBindCookingTargetRuntimeOrder(');
const bindingEnd = preparation.indexOf('\n    private static ', bindingStart + 1);
const binding = preparation.slice(bindingStart, bindingEnd);
assert.ok(bindingStart >= 0 && bindingEnd > bindingStart, 'Runtime order-binding block is unavailable.');
assert.match(binding, /bool requireRareParticipationBinding/u);
assert.doesNotMatch(
  binding,
  /RuntimeAutomationControlState/u,
  'A participation-held runtime order-binding path reacquired the automation state lock.',
);
assert.match(preparationControl, /RuntimeRareGuestParticipationState\.AcquireBoundSideEffectPermit/u);
assert.match(preparationControl, /RuntimeOrderTerminalReceiptStore\.MatchesActiveLifecycle/u);
assert.match(
  preparationControl,
  /RuntimeRareCookingJobParticipationPolicy\.CreateIdentityFromExactBinding\(\s*binding,/u,
);
assert.match(
  preparationControl,
  /RuntimeRareCookingJobParticipationPolicy\.IsRosterAlignedWithExactBinding\(\s*participation,\s*binding,/u,
);
assert.doesNotMatch(
  preparationControl,
  /job\.(?:Generation|CookingOwnershipGeneration)|RuntimeNightBusinessLifecycle/u,
  'Rare cooking participation still uses cooker ownership or mutable current lifecycle generation.',
);
assert.match(participationPolicy, /exactBinding\.BusinessGeneration/u);
assert.doesNotMatch(
  participationPolicy,
  /CookingOwnershipGeneration|RuntimeNightBusinessLifecycle/u,
  'The exact-binding participation policy gained a non-binding generation source.',
);
const cookingJobStart = preparation.indexOf('private sealed class AutomationCookingJob');
const cookingTargetStart = preparation.indexOf('private sealed class CookingCollectionTarget', cookingJobStart);
const cookingJob = preparation.slice(cookingJobStart, cookingTargetStart);
assert.ok(cookingJobStart >= 0 && cookingTargetStart > cookingJobStart);
assert.match(cookingJob, /public long CookingOwnershipGeneration \{ get; init; \}/u);
assert.doesNotMatch(cookingJob, /public long Generation \{ get; init; \}/u);
assert.match(cooking, /CookingOwnershipGeneration = ownershipSnapshot\.Generation/u);
assert.match(preparation, /Generation = CookingOwnershipGeneration/u);
const sideEffectPermitStart = preparationControl.indexOf('private static RuntimeAutomationCookingJobControlPermit AcquireAutomationCookingJobControlPermit(');
const sideEffectPermitEnd = preparationControl.indexOf('private static RuntimeRareGuestParticipationPermit? AcquireRareCookingJobParticipationPermit(', sideEffectPermitStart);
const sideEffectPermit = preparationControl.slice(sideEffectPermitStart, sideEffectPermitEnd);
assert.ok(sideEffectPermitStart >= 0 && sideEffectPermitEnd > sideEffectPermitStart);
assert.ok(
  sideEffectPermit.indexOf('RuntimeAutomationControlState.AcquirePermit')
    < sideEffectPermit.indexOf('AcquireRareCookingJobParticipationPermit'),
  'Native side effects acquire participation before automation control, violating the reviewed lock order.',
);
const participationMergeStart = preparationControl.indexOf('private static RuntimeAutomationControlDecision MergeRareCookingJobParticipationDecision(');
const participationMergeEnd = preparationControl.indexOf('private static RuntimeAutomationControlDecision SuspendForRareGuestParticipation(', participationMergeStart);
const participationMerge = preparationControl.slice(participationMergeStart, participationMergeEnd);
assert.ok(participationMergeStart >= 0 && participationMergeEnd > participationMergeStart);
assert.match(participationMerge, /IReadOnlyList<int> managedGuestIds/u);
assert.doesNotMatch(
  participationMerge,
  /RuntimeAutomationControlState/u,
  'Participation-held decision merge reacquired the automation state lock.',
);
for (const caller of [
  preparationControl.slice(
    preparationControl.indexOf('private static RuntimeAutomationControlDecision ObserveAutomationCookingJobControl('),
    sideEffectPermitStart,
  ),
  sideEffectPermit,
]) {
  assert.ok(
    caller.indexOf('SnapshotManagedRareGuestIds')
      < caller.indexOf('AcquireRareCookingJobParticipationPermit'),
    'Managed roster must be captured before acquiring the participation monitor.',
  );
}
const deliveryDirective = cooking.slice(
  cooking.indexOf('if (transition.Directive == AutomationCookingJobDirective.DeliverOwnedResult'),
  cooking.indexOf('private static (bool Remove, string Message, string Code) EnterManualHandoff'),
);
assert.match(deliveryDirective, /using var permit = AcquireAutomationCookingJobControlPermit/u);
assert.ok(
  deliveryDirective.indexOf('using var permit = AcquireAutomationCookingJobControlPermit')
    < deliveryDirective.indexOf('TryDeliverAutomationCookedFood'),
  'Cooked-food delivery is not enclosed by the composite automation/participation permit.',
);
const evaluationStart = directDelivery.indexOf('private static bool TryResolveCommittedFoodDeliveryEvaluation(');
const evaluationEnd = directDelivery.indexOf('private static bool ContinueOrCloseCommittedFoodDeliveryEvaluation(', evaluationStart);
const evaluation = directDelivery.slice(evaluationStart, evaluationEnd);
assert.ok(evaluationStart >= 0 && evaluationEnd > evaluationStart);
assert.match(evaluation, /AcquireAutomationCookingJobControlPermit/u);
assert.ok(
  evaluation.indexOf('RuntimeOrderTerminalReceiptStore.TryFind')
    < evaluation.indexOf('AcquireAutomationCookingJobControlPermit'),
  'Scalar terminal receipts must retire the exact managed job before active-lifecycle participation gating.',
);

assert.match(state, /MutateParticipation/u);
assert.match(state, /guest-current-orders-mismatch/u);
assert.match(state, /protected-order-not-participating/u);
assert.match(state, /RuntimeRareGuestParticipationAction\.EnableFront/u);
assert.match(state, /RuntimeRareGuestParticipationTargetScope\.Order/u);
assert.match(
  state,
  /var insertionIndex = protectedSet\.Count == 0[\s\S]+\.Max\(entry => entry\.index\) \+ 1;[\s\S]+AddRange\(_participationQueue\.Take\(insertionIndex\)\)[\s\S]+AddRange\(identitiesToEnable\)[\s\S]+AddRange\(_participationQueue\.Skip\(insertionIndex\)\)/u,
  'Front insertion must preserve the complete existing queue and insert after its last protected position.',
);
assert.doesNotMatch(state, /QueueSequence|LastQueueSequence|SetGuestParticipating/u);
assert.match(state, /AcquireAdmissionPermit/u);
assert.match(state, /AcquireBoundSideEffectPermit/u);
assert.match(state, /ApplyManagedGuestIdsFromAuthority/u);
assert.match(state, /public static long EndBusinessIfCurrent\(long businessGeneration\)/u);
assert.doesNotMatch(state, /public static long EndBusiness\(/u);
assert.match(server, /resetManagedParticipation: true/u);
assert.match(server, /resetManagedParticipation: false/u);
assert.match(automationControl, /SnapshotManagedRareGuestIds/u);
assert.match(dotnet6Runner, /'runtime-rare-guest-participation'/u);
assert.match(preparation, /SnapshotActiveRareAutomationQueueCandidates/u);
assert.match(mutation, /placements=\{FormatRareGuestParticipationPlacements/u);
assert.match(mutation, /suspendedActiveJobs=\{FormatLoggedValues/u);
assert.ok(
  mutation.indexOf('var before = RuntimeRareGuestParticipationState.Snapshot')
    < mutation.indexOf('SnapshotActiveRareAutomationQueueCandidates'),
  'Cached-active job candidates were not classified against the expected participation snapshot.',
);
assert.match(
  mutation,
  /currentParticipation\.TryGetValue\(candidate\.Identity,[\s\S]+if \(!participation\.Participating\)[\s\S]+suspendedJobIds\.Add\(candidate\.JobId\)[\s\S]+protectedSet\.Add\(candidate\.Identity\)/u,
  'Explicitly paused cached-active jobs must be diagnosed and excluded from operational protection.',
);
const activeAnchorStart = preparation.indexOf('internal static IReadOnlyList<RuntimeRareAutomationQueueCandidate> SnapshotActiveRareAutomationQueueCandidates(');
const activeAnchorEnd = preparation.indexOf('public static AutomationSafetyBarrierAckResult AcknowledgeAutomationSafetyBarrier', activeAnchorStart);
const activeAnchor = preparation.slice(activeAnchorStart, activeAnchorEnd);
assert.ok(activeAnchorStart >= 0 && activeAnchorEnd > activeAnchorStart);
assert.match(activeAnchor, /lock \(AutomationCookingJobLock\)/u);
assert.match(activeAnchor, /job\.ControlState, "active"/u);
assert.match(activeAnchor, /binding\.BusinessGeneration/u);
assert.match(activeAnchor, /binding\.OrderPointer == 0/u);
assert.match(activeAnchor, /binding\.ControllerPointer == 0/u);
assert.match(activeAnchor, /RuntimeOrderTerminalReceiptStore\.MatchesRequestedLifecycle/u);
assert.doesNotMatch(activeAnchor, /RuntimeRareGuestParticipationState/u,
  'Active job capture acquired participation state while holding the cooking-job lock.');
assert.match(mutation, /Rare participation front placement rejected/u);
assert.match(mutation, /code=active-anchor-invalid/u);
assert.match(
  mutation,
  /catch \(RuntimeRareGuestParticipationConflictException ex\)[\s\S]+RuntimeRareGuestParticipationAction\.EnableFront[\s\S]+protected=\{FormatRareGuestParticipationQueuePositions/u,
  'CAS-time front placement conflicts must retain bounded protected-anchor diagnostics.',
);
assert.doesNotMatch(server, /\/orders\/rare\/dismiss|BuildRareOrderDismissJson/u);
assert.doesNotMatch(capture, /DismissOrder|IsDismissRequestMatch/u);

console.log('PASS: rare-order participation API, snapshot projection, lifecycle reconciliation, authority fencing, and locked smoke wiring are present.');
