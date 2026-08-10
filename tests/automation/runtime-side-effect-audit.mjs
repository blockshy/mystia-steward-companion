import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const read = (relativePath) => fs.readFileSync(path.join(root, relativePath), 'utf8');

const lifecycle = read('mods/bepinex/src/Save/AutomationCookingJobLifecycle.cs');
const cooking = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.Cooking.cs');
const automationControl = read(
  'mods/bepinex/src/Save/RuntimeOrderPreparationService.AutomationControl.cs',
);
const delivery = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.Delivery.cs');
const directDelivery = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.DirectDelivery.cs');
const yuumaSettlement = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.YuumaSettlement.cs');
const yuumaSettlementTracker = read(
  'mods/bepinex/src/Save/SpecialBusiness/YuumaSettlementTransactionTracker.cs',
);
const cookerHighlight = read('mods/bepinex/src/Save/RuntimeCookerHighlightService.cs');
const cookerSnapshot = read('mods/bepinex/src/Save/RuntimeCookerSnapshotService.cs');
const cookerSnapshotSignature = read('mods/bepinex/src/Save/RuntimeCookerSnapshotContentSignature.cs');
const cookerReflection = read('mods/bepinex/src/Save/RuntimeCookerReflection.cs');
const cookerTypeSequenceReader = read('mods/bepinex/src/Save/RuntimeCookerTypeSequenceReader.cs');
const cookerStartAvailabilityService = read('mods/bepinex/src/Save/RuntimeCookerStartAvailabilityService.cs');
const cookingOwnership = read('mods/bepinex/src/Save/RuntimeCookingGenerationTracker.cs');
const cookerStartPolicy = read('mods/bepinex/src/Save/AutomationCookerStartPolicy.cs');
const matching = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.OrderMatching.cs');
const runtimeReflection = read('mods/bepinex/src/Save/RuntimeReflectionUtility.cs');
const runtimeOrderTypes = read('mods/bepinex/src/Save/RuntimeOrderTypeResolver.cs');
const service = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.cs');
const specialTargetPolicy = read(
  'mods/bepinex/src/Save/SpecialBusiness/RuntimeOrderPreparationService.SpecialFoodTargetPolicy.cs',
);
const yuyukoPolicy = read('mods/bepinex/src/Save/SpecialBusiness/RuntimeOrderPreparationService.YuyukoChallengePolicy.cs');
const mizuchiPolicy = read('mods/bepinex/src/Save/SpecialBusiness/RuntimeOrderPreparationService.MizuchiPolicy.cs');
const mizuchiRolePolicy = read('mods/bepinex/src/Save/SpecialBusiness/MizuchiAutomationPolicy.cs');
const foodModifierValidation = read('mods/bepinex/src/Save/SpecialBusiness/RuntimeOrderPreparationService.FoodModifierValidation.cs');
const yuyukoEvaluationTracker = read(
  'mods/bepinex/src/Save/SpecialBusiness/YuyukoChallengeEvaluationTracker.cs',
);
const capture = read('mods/bepinex/src/Save/SpecialOrderRuntimeCapture.cs');
const normalCapture = read('mods/bepinex/src/Save/NormalOrderRuntimeCapture.cs');
const provider = read('mods/bepinex/src/Save/NightBusinessReflectionProvider.cs');
const overlay = read('mods/bepinex/src/Ui/StewardOverlayController.cs');
const localApiServer = read('mods/bepinex/src/LocalApi/LocalApiServer.cs');
const localApiModels = read('mods/bepinex/src/LocalApi/LocalApiModels.cs');
const orderPreparationModels = read('mods/bepinex/src/LocalApi/OrderPreparationModels.cs');
const frontendApi = read('apps/companion/src/companion/api.ts');
const frontendTypes = read('apps/companion/src/companion/types.ts');
const frontendTargetPolicy = read(
  'apps/companion/src/companion/domain/special-business/target-policy.ts',
);
const specialBusinessContext = read(
  'mods/bepinex/src/Save/RuntimeSpecialBusinessContextService.cs',
);
const normalOrderSnapshot = read('mods/bepinex/src/Save/RuntimeNormalOrderSnapshotService.cs');
const wackyOrderModule = read(
  'mods/bepinex/src/Save/SpecialBusiness/WackyCookingCompetitionOrderModule.cs',
);
const plugin = read('mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs');

const productionSourceRoot = path.join(root, 'mods/bepinex/src');
const productionCsRelativePaths = fs.readdirSync(productionSourceRoot, { recursive: true })
  .filter((relativePath) => typeof relativePath === 'string' && relativePath.endsWith('.cs'));
const throwDeliveryOrderHighlightRelativePath = path.join(
  'Save',
  'RuntimeThrowDeliverOrderHighlightService.cs',
);
const throwDeliveryOrderHighlightPath = path.join(
  productionSourceRoot,
  throwDeliveryOrderHighlightRelativePath,
);
const obsoleteWrongPageOrderHighlightPath = path.join(
  productionSourceRoot,
  'Save',
  ['Runtime', 'Serve', 'PanelOrderHighlightService.cs'].join(''),
);
assert.ok(
  fs.existsSync(throwDeliveryOrderHighlightPath),
  'The exact passive throw-delivery order-highlight service is missing.',
);
assert.ok(
  !fs.existsSync(obsoleteWrongPageOrderHighlightPath),
  'The obsolete wrong-page order-highlight service must be removed instead of retained as a compatibility path.',
);
const throwDeliveryOrderHighlight = fs.readFileSync(throwDeliveryOrderHighlightPath, 'utf8');
const obsoleteWrongPageServicePattern = new RegExp(
  ['Runtime', 'Serve', 'PanelOrderHighlightService'].join(''),
);
const throwDeliveryPanelLifecycleObservers = productionCsRelativePaths
  .filter((relativePath) => {
    const source = fs.readFileSync(path.join(productionSourceRoot, relativePath), 'utf8');
    assert.doesNotMatch(
      source,
      obsoleteWrongPageServicePattern,
      `Production source retained the obsolete wrong-page service identity: ${relativePath}`,
    );
    assert.doesNotMatch(
      source,
      /OpenThrowDeliverPanel/,
      `Production source restored the active throw-delivery panel entry: ${relativePath}`,
    );
    return /WorkSceneThrowDeliverPanel/.test(source)
      && /OnPanelOpen|OnPanelClose/.test(source);
  })
  .map((relativePath) => relativePath.split(path.sep).join('/'));
assert.deepEqual(
  throwDeliveryPanelLifecycleObservers,
  ['Save/RuntimeThrowDeliverOrderHighlightService.cs'],
  'Only the dedicated passive visual service may observe WorkSceneThrowDeliverPanel OnPanelOpen/OnPanelClose.',
);
assert.match(
  throwDeliveryOrderHighlight,
  /ThrowDeliverPanelTypeName\s*=\s*"NightScene\.UI\.HUDUtility\.WorkSceneThrowDeliverPanel"/,
  'The passive visual service must bind the exact verified throw-delivery panel type.',
);
assert.match(
  throwDeliveryOrderHighlight,
  /OpenPatchKey\s*=\s*ThrowDeliverPanelTypeName\s*\+\s*"\.OnPanelOpen\/0"/,
  'The passive visual service must bind the exact zero-argument open lifecycle.',
);
assert.match(
  throwDeliveryOrderHighlight,
  /ClosePatchKey\s*=\s*ThrowDeliverPanelTypeName\s*\+\s*"\.OnPanelClose\/0"/,
  'The passive visual service must bind the exact zero-argument close lifecycle.',
);
assert.doesNotMatch(
  throwDeliveryOrderHighlight,
  /WorkSceneServePannel|OpenThrowDeliverPanel|DescribeCurrentOrder|TryFocusToOrder|EnterGroup|GetShowInUIOrders|DisplayClass|MoveNext|OnThrowDelivering|ExecuteThrowDeliver|ThrowDeliver\(|EvaluateOrder|EvaulateManualOrder|FindObjectOfType|FindObjectsOfType|FindObjectsByType|FindFirstObjectByType|FindAnyObjectByType|FindObjectsOfTypeAll|GetComponentInChildren|GetComponentsInChildren/,
  'The passive throw-delivery visual service restored a wrong-page, active/generated, evaluation, or scene-scan path.',
);

const storageOutHookSources = fs.readdirSync(productionSourceRoot, { recursive: true })
  .filter((relativePath) => typeof relativePath === 'string' && relativePath.endsWith('.cs'))
  .filter((relativePath) => {
    const source = fs.readFileSync(path.join(productionSourceRoot, relativePath), 'utf8');
    return /RunTimeStorage/.test(source)
      && /(?:Object|Badge|Beverage|Cooker|Food|Ingredient|Item)Out(?:Range)?/.test(source)
      && /(?:HarmonyPatch|HarmonyMethod|new Harmony|\.Patch\s*\()/.test(source);
  });

assert.deepEqual(
  storageOutHookSources,
  [],
  'Production code must not install Harmony hooks on RunTimeStorage *Out/ObjectOut entries.',
);
assert.doesNotMatch(
  plugin,
  /RuntimeStorageSentinelDiagnostic/,
  'The plugin must not restore the removed RunTimeStorage sentinel diagnostic.',
);

assert.ok(!cooking.includes('"FinishCooking"'), 'The Mod must not invoke the non-idempotent FinishCooking entry.');
assert.ok(!lifecycle.includes('FinalizeOwnedResult'), 'The cooking tracker still exposes the removed active-finalize directive.');
assert.match(lifecycle, /"cooking-native-finalize-waiting"[\s\S]*AutomationCookingJobDirective\.None/);

assert.match(cooking, /parameters\.Length == 2[\s\S]*parameters\[0\]\.ParameterType == typeof\(int\)[\s\S]*parameters\[1\]\.ParameterType == typeof\(bool\)/);
assert.match(cooking, /method\.Invoke\(null, new object\?\[\] \{ itemId, false \}\)/);
assert.ok(!cooking.includes('CopyNormalOrderRequestWithoutOrderKey'));
assert.ok(!matching.includes('CopyNormalOrderRequestWithoutOrderKey'));
assert.ok(!matching.includes('orderKeyFallback'));
assert.match(matching, /BuildRequestOrderIdentity[\s\S]*request\.RuntimeGuestId[\s\S]*request\.FoodTagId[\s\S]*request\.BeverageTagId/);
assert.match(orderPreparationModels, /public long OrderLifecycleSequence \{ get; init; \} = -1;/,
  'Order automation requests must expose one explicit lifecycle sequence.');
assert.match(localApiServer, /OrderLifecycleSequence = ReadLongQuery\(query, "orderLifecycleSequence", -1\)/,
  'The local API must parse the explicit order lifecycle sequence without deriving it from trace text.');
assert.match(frontendApi, /orderLifecycleSequence: String\(item\.order\.orderLifecycleSequence\)/,
  'Rare automation requests must send the exact snapshot lifecycle sequence.');
assert.match(frontendApi, /orderLifecycleSequence: String\(order\.orderLifecycleSequence\)/,
  'Normal automation requests must send the exact snapshot lifecycle sequence.');
assert.match(service, /request\.OrderLifecycleSequence <= 0[\s\S]*OrderLifecycleMismatch/,
  'Missing request lifecycle identity must fail before any automation side effect.');
assert.match(service, /MatchesRequestedLifecycle\([\s\S]*target\.RequestedOrderLifecycleSequence,[\s\S]*runtimeOrder\.OrderLifecycleSequence/,
  'Fresh target binding must require request and captured lifecycle equality.');
assert.match(matching, /TryMatchCapturedOrderIdentity[\s\S]*MatchesRequestedLifecycle\([\s\S]*request\.OrderLifecycleSequence,[\s\S]*captured\.OrderLifecycleSequence/,
  'Rare capture matching must reject delayed requests from another lifecycle.');
assert.match(matching, /ScoreCapturedNormalOrder[\s\S]*MatchesRequestedLifecycle\([\s\S]*request\.OrderLifecycleSequence,[\s\S]*captured\.OrderLifecycleSequence/,
  'Normal capture matching must reject delayed requests from another lifecycle.');
assert.match(matching, /TryReadGuestId[\s\S]*TryReadInt\([\s\S]*get_Id[\s\S]*ReadMember\(guest, "Id"\)/);
assert.match(matching, /private static IEnumerable<object> EnumerateControllerOrders\(object controller\)[\s\S]*TryInvokeInstanceValue\(controller, "PeekOrders"\)/);
assert.doesNotMatch(matching, /"AllOrders"|"AllOrdersData"/,
  'Automation must not treat the historical controller order stack as current ownership.');
assert.match(matching, /Delivery,[\s\S]*Completion,[\s\S]*NativeEvaluation/);
assert.match(service, /FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.Completion\)/);
assert.match(cooking, /FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.Completion\)/);
assert.match(service, /RuntimeGuestId = request\.RuntimeGuestId/);
const normalFrontendAction = sourceSlice(
  frontendApi,
  'export async function completeFirstNormalOrder(',
  'export async function readFavorites(');
assert.match(
  normalFrontendAction,
  /if \(order\.runtimeGuestId != null\) params\.set\('runtimeGuestId', String\(order\.runtimeGuestId\)\)/,
  'Normal-order automation must send the verified runtime guest identity to the backend target.',
);
assert.match(
  normalOrderSnapshot,
  /RuntimeGuestId = classification\.RuntimeGuestId/,
  'Normal-order runtime identity must come from the exact special-business classifier.',
);
assert.match(directDelivery, /RuntimeGuestId = target\.RuntimeGuestId[\s\S]*FoodTagId = target\.FoodTagId[\s\S]*BeverageTagId = target\.BeverageTagId/);
const rareTargetMatchStart = cooking.indexOf('private static bool IsSameCookingCollectionTarget');
const rareTargetMatchEnd = cooking.indexOf('private static (bool Remove, string Message, string Code) TryProcessAutomationCookingJob', rareTargetMatchStart);
assert.ok(rareTargetMatchStart >= 0 && rareTargetMatchEnd > rareTargetMatchStart);
const rareTargetMatch = cooking.slice(rareTargetMatchStart, rareTargetMatchEnd);
assert.match(rareTargetMatch, /RareOrderIdentityMatcher\.IsSameCookingTarget[\s\S]*RuntimeGuestId[\s\S]*FoodTagId[\s\S]*BeverageTagId/);
assert.doesNotMatch(rareTargetMatch, /left\.FoodTag(?!Id)/);
assert.doesNotMatch(rareTargetMatch, /left\.BeverageTag(?!Id)/);
assert.ok(!rareTargetMatch.includes('left.GuestId'));

const rareLookup = sourceSlice(
  matching,
  'private static RuntimeOrderMatch FindRuntimeOrder(',
  'private static RuntimeOrderMatch FindRuntimeNormalOrder(');
assert.match(rareLookup, /FindCapturedRuntimeOrder\(request, manager, purpose\)/,
  'Native evaluation must retain the exact captured-order discovery path.');
assert.match(rareLookup, /requiresLiveKoishiBoss[\s\S]*BuildWackyKoishiCaptureSkippedDiagnostic\("capturedSkipped"\)[\s\S]*FindCapturedRuntimeOrder\(request, manager, purpose\)/,
  'Koishi boss orders must retain their independent manager-live capture gate.');
assert.ok(!rareLookup.includes('BuildYuyukoPhase3CaptureSkippedDiagnostic'),
  'Yuyuko phase-three native evaluation must not unconditionally skip an exact captured order.');
assert.match(rareLookup, /requiresLiveYuyukoPhase3Boss[\s\S]*IsMatchingYuyukoPhase3EvaluationOrder\([\s\S]*captured\.Order[\s\S]*captured\.Controller/,
  'A captured Yuyuko phase-three order must pass the same live callback and served-target validator before evaluation.');
assert.match(
  rareLookup,
  /if \(!requiresLiveKoishiBoss && !requiresLiveYuyukoPhase3Boss\)[\s\S]*exact capture unavailable[\s\S]*foreach \(var controller in EnumerateGuestControllers\(manager\)\)/,
  'General special-order lookup must fail closed before the named Koishi/Yuyuko manager scan.',
);

const capturedSpecialLiveness = sourceSlice(
  matching,
  'private static bool IsCapturedSpecialOrderLive(',
  'private static IEnumerable<object> EnumerateGuestControllers(');
assert.match(capturedSpecialLiveness, /TryInvokeInstanceValue\(controllerObject, "PeekOrders"\)[\s\S]*CompareObjectIdentity\(currentOrder, orderObject\)/,
  'Captured-order liveness must still prove current controller ownership.');
assert.match(capturedSpecialLiveness, /RareOrderIdentityMatcher\.IsExecutableCapturedOrder/);
assert.match(capturedSpecialLiveness, /return IsMatchingSpecialOrder\(orderObject!, controllerObject!, request, purpose, out rejectReason\)/,
  'Captured orders must still pass the complete raw runtime identity matcher.');

assert.match(
  rareLookup,
  /foreach \(var enumeratedOrder in EnumerateControllerOrders\(controller\)\)[\s\S]*TryResolveRuntimeOrder\([\s\S]*enumeratedOrder,[\s\S]*RuntimeOrderKind\.Special,[\s\S]*out var order,[\s\S]*out var typeRejectReason\)[\s\S]*IsMatching(?:YuyukoPhase3EvaluationOrder|SpecialOrder)\(order/,
  'Orders enumerated through OrderBase must resolve uniquely to SpecialOrder before raw Tag IDs are read.',
);
assert.doesNotMatch(
  matching,
  /NormalizeRuntimeSpecialOrder|TryCastRuntimeObject\(order, SpecialOrderTypeName\) \?\? order/,
  'Automation matching must not restore the unresolved-wrapper passthrough normalizer.',
);
assert.match(runtimeReflection, /TryCastRuntimeObject\(object\? value, string targetTypeName\)[\s\S]*FindType\(targetTypeName\)[\s\S]*value is not Il2CppObjectBase[\s\S]*TryCastMethodCache\.GetOrAdd\([\s\S]*ResolveTryCastMethod/,
  'The runtime-object cast entry must resolve one exact named IL2CPP type and cast method.');
assert.match(runtimeReflection, /ResolveTryCastMethod\(Type targetType\)[\s\S]*typeof\(Il2CppObjectBase\)[\s\S]*method\.Name == "TryCast"[\s\S]*MakeGenericMethod\(targetType\)/,
  'The shared runtime-object cast entry must invoke the concrete generic runtime cast.');

const storyManualCallbackLookup = namedMethodSource(matching, 'TryResolveSpecialManualContext');
assert.match(storyManualCallbackLookup, /CompareObjectIdentity\(captured\.OrderObject, order\) == RuntimeObjectIdentityComparison\.Same/,
  'A manual callback must belong to the exact captured order object selected by the live lookup.');
assert.match(storyManualCallbackLookup, /CompareObjectIdentity\(captured\.ControllerObject, controller\) == RuntimeObjectIdentityComparison\.Same/,
  'A manual callback must belong to the exact captured controller object selected by the live lookup.');
assert.match(storyManualCallbackLookup, /captured\.ManualOrder[\s\S]*ManualEvaluationCallback != null[\s\S]*HasCaptureSource\(candidate\.CaptureSource, "ManualOrderSet"\)/,
  'A manual callback must come from the exact ManualOrderSet capture for a ManualOrder=true order.');
assert.ok(!storyManualCallbackLookup.includes('TryMatchCapturedOrderIdentity'),
  'Manual callbacks must not be selected from the first request-identity capture.');
assert.doesNotMatch(storyManualCallbackLookup, /OrderPreparationRequest\s+request/,
  'Manual callback lookup must accept the matched order/controller pair, not a request identity.');

const yuyukoManualBindingLookup = namedMethodSource(
  matching,
  'TryResolveCapturedYuyukoPhase3ManualEvaluationBinding',
);
assert.match(
  yuyukoManualBindingLookup,
  /RuntimeOrderTypeResolver\.Resolve\(order\)[\s\S]*TryResolveCapturedYuyukoPhase3SpecialManualEvaluationBinding\([\s\S]*TryResolveCapturedYuyukoPhase3NormalManualEvaluationBinding\(/,
  'Yuyuko manual binding lookup must resolve one concrete order type before reading its capture.',
);
const yuyukoSpecialBindingLookup = namedMethodSource(
  matching,
  'TryResolveCapturedYuyukoPhase3SpecialManualEvaluationBinding',
);
const yuyukoNormalBindingLookup = namedMethodSource(
  matching,
  'TryResolveCapturedYuyukoPhase3NormalManualEvaluationBinding',
);
for (const [label, lookup, captureName] of [
  ['special', yuyukoSpecialBindingLookup, 'SpecialOrderRuntimeCapture'],
  ['normal', yuyukoNormalBindingLookup, 'NormalOrderRuntimeCapture'],
]) {
  assert.match(lookup, new RegExp(`if \\(!${captureName}\\.IsBusinessReady\\)[\\s\\S]*return false`),
    `Yuyuko ${label} binding must not consume a partially covered capture generation.`);
  assert.match(lookup, /CompareObjectIdentity\(captured\.OrderObject, order\) == RuntimeObjectIdentityComparison\.Same/,
    `Yuyuko ${label} binding must belong to the exact current order object.`);
  assert.match(lookup, /CompareObjectIdentity\(captured\.ControllerObject, controller\) == RuntimeObjectIdentityComparison\.Same/,
    `Yuyuko ${label} binding must belong to the exact current controller object.`);
  assert.match(lookup, /candidate\.ManualEvaluationBindingConflict[\s\S]*candidate\.ManualEvaluationBindingObserved[\s\S]*candidate\.ManualEvaluationBindingCallback == null[\s\S]*HasCaptureSource\(candidate\.CaptureSource, "ManualOrderSet"\)/,
    `Yuyuko ${label} manual binding must require stable, non-conflicting ManualOrderSet evidence.`);
  assert.doesNotMatch(lookup, /TryReadExactManualOrder\(/,
    `Yuyuko ${label} routing must not discard captured setter evidence using the transient ManualOrder property.`);
}
const capturedSpecialManualLookup = namedMethodSource(matching, 'FindCapturedRuntimeOrder');
assert.match(capturedSpecialManualLookup, /requiresYuumaSettlementManualContext\s*=\s*purpose == RuntimeOrderLookupPurpose\.YuumaSettlement/,
  'Strict Yuuma manual-order resolution must have a dedicated lookup purpose.');
assert.match(capturedSpecialManualLookup, /if \(requiresYuumaSettlementManualContext[\s\S]*!TryResolveSpecialManualContext\(/,
  'Captured special-order ManualOrder/callback fail-closed validation must be scoped to Yuuma settlement.');
assert.doesNotMatch(rareLookup, /requiresYuumaSettlementManualContext|TryResolveSpecialManualContext/,
  'Yuuma settlement must not restore a manager-scan manual-context fallback.');
assert.match(rareLookup, /requiresLiveYuyukoPhase3Boss[\s\S]*TryResolveCapturedYuyukoPhase3ManualEvaluationBinding\(/,
  'Every live Yuyuko phase-3 native-evaluation match must resolve the exact captured manual binding.');
assert.match(rareLookup, /YuyukoManualBindingResolved = requiresLiveYuyukoPhase3Boss[\s\S]*YuyukoManualBindingCaptured = requiresLiveYuyukoPhase3Boss && manualBindingCaptured/,
  'The live Yuyuko match must preserve whether the current capture proves a manual binding or its absence.');

const normalLookup = sourceSlice(
  matching,
  'private static RuntimeOrderMatch FindRuntimeNormalOrder(',
  'private static IEnumerable<object> EnumerateOrderControllerOrders(',
);
assert.match(normalLookup, /RuntimeOrderLookupPurpose purpose = RuntimeOrderLookupPurpose\.Delivery/,
  'Normal-order lookup must retain Delivery as its default behavior.');
const capturedNormalManualLookup = namedMethodSource(matching, 'FindCapturedRuntimeNormalOrder');
assert.match(capturedNormalManualLookup, /requiresYuumaSettlementManualContext\s*=\s*purpose == RuntimeOrderLookupPurpose\.YuumaSettlement/,
  'Normal-order strict manual context must have the same dedicated Yuuma purpose.');
assert.match(capturedNormalManualLookup, /if \(requiresYuumaSettlementManualContext[\s\S]*!TryResolveNormalManualContext\(/,
  'Captured normal-order ManualOrder/callback fail-closed validation must be scoped to Yuuma settlement.');
assert.match(capturedNormalManualLookup, /requiresYuyukoManualBinding[\s\S]*TryResolveCapturedYuyukoPhase3ManualEvaluationBinding\(/,
  'A captured Yuyuko normal order must resolve its exact manual binding for NativeEvaluation.');
assert.doesNotMatch(normalLookup, /requiresYuumaSettlementManualContext|TryResolveNormalManualContext/,
  'Yuuma settlement must not restore a manager-scan normal-order manual-context fallback.');
assert.match(
  normalLookup,
  /if \(!requiresLiveKoishiBoss\)[\s\S]*exact normal capture unavailable[\s\S]*foreach \(var controller in EnumerateGuestControllers\(manager\)\)/,
  'General normal-order lookup must fail closed before the named Koishi manager scan.',
);
assert.match(matching,
  /FindCapturedRuntimeNormalOrder\([\s\S]*TryInvokeInstanceValue\(captured\.ControllerObject, "PeekOrders"\)[\s\S]*CompareObjectIdentity\(currentOrder, captured\.OrderObject\)[\s\S]*RuntimeObjectIdentityComparison\.Same/,
  'Every captured normal order must remain owned by the exact controller.');

const yuyukoEvaluationReadiness = sourceSlice(
  yuyukoPolicy,
  'private static RuntimeOrderEvaluationResult TryEvaluateYuyukoChallengeRuntimeOrderIfReady(',
  'private static RuntimeOrderEvaluationResult TryEvaluateStoryYuyukoPhase3OrderIfReady(');
const nativeEvaluationLookupIndex = yuyukoEvaluationReadiness.indexOf(
  'FindRuntimeOrder(request, RuntimeOrderLookupPurpose.NativeEvaluation)');
const fulfilledReadIndex = yuyukoEvaluationReadiness.indexOf('get_IsFullfilled');
const unfulfilledWaitIndex = yuyukoEvaluationReadiness.indexOf('订单尚未同时满足料理和酒水，等待下一轮补齐。');
assert.ok(nativeEvaluationLookupIndex >= 0, 'Yuyuko native-evaluation lookup is missing.');
assert.ok(fulfilledReadIndex >= 0 && fulfilledReadIndex < nativeEvaluationLookupIndex,
  'The exact completion match must be checked for fulfillment before native-evaluation reacquisition.');
assert.ok(unfulfilledWaitIndex > fulfilledReadIndex && unfulfilledWaitIndex < nativeEvaluationLookupIndex,
  'An unfulfilled Yuyuko order must return a normal wait outcome before native-evaluation reacquisition.');
assert.match(
  yuyukoEvaluationReadiness,
  /reacquireLiveOrder[\s\S]*FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.NativeEvaluation\)[\s\S]*FindRuntimeNormalOrder\(request, RuntimeOrderLookupPurpose\.NativeEvaluation\)/,
  'Yuyuko phase-3 evaluation must fresh-read both special and normal current-order paths.',
);

const yuyukoRetakeRoute = namedMethodSource(
  yuyukoPolicy,
  'TryResolveYuyukoRetakePhase3EvaluationRoute',
);
assert.match(
  yuyukoRetakeRoute,
  /TryInvokeInstanceValue\(runtimeOrder\.Controller, "PeekOrders"\)[\s\S]*CompareObjectIdentity\(currentOrder, runtimeOrder\.Order\)[\s\S]*RuntimeObjectIdentityComparison\.Same/,
  'The retake route must prove fresh exact controller ownership immediately before evaluation.',
);
assert.match(
  yuyukoRetakeRoute,
  /!runtimeOrder\.YuyukoManualBindingResolved[\s\S]*return false/,
  'The retake route must fail closed when the current capture generation did not resolve manual binding state.',
);
assert.match(
  yuyukoRetakeRoute,
  /!resolution\.Resolved \|\| resolution\.ReadableOrder == null[\s\S]*return false/,
  'The retake route must fail closed unless OrderBase resolves uniquely to an exact NormalOrder or SpecialOrder.',
);
assert.match(
  yuyukoRetakeRoute,
  /resolution\.Kind != RuntimeOrderKind\.Normal[\s\S]*resolution\.Kind != RuntimeOrderKind\.Special[\s\S]*return false/,
  'The native-evaluation route must explicitly allow only exact NormalOrder and SpecialOrder resolutions.',
);
assert.match(
  yuyukoRetakeRoute,
  /YuyukoManualBindingCaptured[\s\S]*TryFindYuyukoRetakePhase3ManualProgressCallback\([\s\S]*!hasBossProgress \|\| hasGroupProgress[\s\S]*ManualBoss/,
  'Manual retake orders require the exact b__77/b__78 binding together with boss _50 and no group _70.',
);
assert.match(
  yuyukoRetakeRoute,
  /ManualEvaluationCallback != null[\s\S]*route=conflict[\s\S]*hasBossProgress \|\| !hasGroupProgress[\s\S]*StandardGroup/,
  'Standard retake evaluation requires an exact resolved order, an explicitly absent manual binding, group _70, and no boss _50.',
);
const yuyukoRuntimeDiagnostic = namedMethodSource(
  yuyukoPolicy,
  'AppendYuyukoRuntimeDiagnostic',
);
assert.match(
  yuyukoRuntimeDiagnostic,
  /!AggregateModLogService\.Enabled[\s\S]*try[\s\S]*AppendYuyukoRuntimeDiagnosticCore[\s\S]*catch/,
  'Yuyuko diagnostics must be disabled without runtime reads and must never interrupt native evaluation.',
);
const yuyukoRuntimeDiagnosticCore = namedMethodSource(
  yuyukoPolicy,
  'AppendYuyukoRuntimeDiagnosticCore',
);
assert.match(
  yuyukoRuntimeDiagnosticCore,
  /BuildYuyukoRuntimeDiagnosticOnceKey\([\s\S]*SpecialBusinessDiagnostics\.AppendYuyukoSnapshot\([\s\S]*onceKey/,
  'Repeated native-evaluation diagnostics must be deduplicated only at the final bounded snapshot writer.',
);
const yuyukoRuntimeDiagnosticOnceKey = namedMethodSource(
  yuyukoPolicy,
  'BuildYuyukoRuntimeDiagnosticOnceKey',
);
assert.match(
  yuyukoRuntimeDiagnosticOnceKey,
  /native-evaluate-entry-blocked[\s\S]*RuntimeNightBusinessLifecycle\.Generation[\s\S]*DescribeObject\(runtimeOrder\.Order\)[\s\S]*DescribeObject\(runtimeOrder\.Controller\)[\s\S]*LocalApiSnapshotSignature\.Compute\(detail\)/,
  'Blocked native-evaluation diagnostics must bind their once key to generation, exact runtime objects, and the route evidence hash.',
);
assert.doesNotMatch(
  yuyukoRuntimeDiagnosticOnceKey,
  /yuyuko-native-evaluate-before/,
  'Pre-evaluation snapshots must remain periodically observable while runtime evidence changes.',
);
const yuyukoRuntimeDiagnosticThrottle = namedMethodSource(
  yuyukoPolicy,
  'ShouldThrottleYuyukoRuntimeDiagnostic',
);
assert.match(
  yuyukoRuntimeDiagnosticThrottle,
  /RuntimeNightBusinessLifecycle\.Generation/,
  'Yuyuko diagnostic throttling must be scoped to the current business generation.',
);
assert.match(
  yuyukoRuntimeDiagnosticThrottle,
  /yuyuko-native-evaluate-before[\s\S]*YuyukoNativeEvaluationAttemptDiagnosticThrottle[\s\S]*YuyukoRuntimeDiagnosticThrottle[\s\S]*now - last < throttle/,
  'Pre-evaluation snapshots must use a dedicated low-frequency throttle without changing business retries.',
);
assert.match(
  yuyukoRuntimeDiagnosticThrottle,
  /BuildYuyukoNativeEvaluationDiagnosticEvidence\(runtimeOrder\)/,
  'Pre-evaluation throttling must immediately reopen when fresh native identity, binding, callback, fulfilled, or evaluated evidence changes.',
);
const yuyukoNativeEvaluationDiagnosticEvidence = namedMethodSource(
  yuyukoPolicy,
  'BuildYuyukoNativeEvaluationDiagnosticEvidence',
);
assert.match(
  yuyukoNativeEvaluationDiagnosticEvidence,
  /try[\s\S]*DescribeObject\(runtimeOrder\.Order\)[\s\S]*DescribeObject\(runtimeOrder\.Controller\)[\s\S]*YuyukoManualBindingResolved[\s\S]*YuyukoManualBindingCaptured[\s\S]*DescribeObject\(runtimeOrder\.ManualEvaluationCallback\)[\s\S]*OverrideEvaluationCallback[\s\S]*IsRuntimeOrderFulfilledForYuyukoDiagnostic\(runtimeOrder\)[\s\S]*HasEvaluated[\s\S]*catch \(Exception ex\)[\s\S]*evidence-unreadable/,
  'Fresh native diagnostic evidence must be complete and convert reflection failures into a stable no-throw identity.',
);
assert.doesNotMatch(
  yuyukoRuntimeDiagnosticThrottle,
  /DescribeYuyukoProgressForDiagnostics/,
  'Unrelated challenge progress changes must not bypass per-order diagnostic throttling.',
);
assert.doesNotMatch(
  yuyukoPolicy,
  /TryFindYuyukoRetakePhase3ProgressCallback|RetakeNative/,
  'Retake routing must not restore the old challenge-wide _50/_70 classifier or fixed EvaluateOrder mode.',
);

const retakeEvaluation = namedMethodSource(
  yuyukoPolicy,
  'TryEvaluateRetakeYuyukoPhase3OrderIfReady',
);
const targetGateIndex = retakeEvaluation.indexOf('TryValidateYuyukoPhase3ServedExactTarget(');
const routeIndex = retakeEvaluation.indexOf('TryResolveYuyukoRetakePhase3EvaluationRoute(');
const manualInvokeIndex = retakeEvaluation.indexOf('"EvaulateManualOrder"');
const standardInvokeIndex = retakeEvaluation.indexOf('TryEvaluateRuntimeOrderIfReady(');
assert.ok(targetGateIndex >= 0 && routeIndex > targetGateIndex,
  'Retake routing must run only after the exact served target gate.');
assert.ok(manualInvokeIndex > routeIndex && standardInvokeIndex > routeIndex,
  'Retake routing must expose exactly the manual and standard native entries after route resolution.');
assert.match(
  retakeEvaluation,
  /evaluationRoute == YuyukoRetakePhase3EvaluationRoute\.ManualBoss[\s\S]*TryInvokeRuntimeOrderEvaluationOnce\([\s\S]*"EvaulateManualOrder"[\s\S]*:[\s\S]*TryEvaluateRuntimeOrderIfReady\(/,
  'Each resolved retake route must call only its corresponding native evaluation entry.',
);

const retakeRefreshEvaluation = namedMethodSource(
  yuyukoPolicy,
  'TryEvaluateYuyukoPhase3RefreshOrderIfReady',
);
assert.match(
  retakeRefreshEvaluation,
  /YuyukoPhase3EvaluationContract\.Retake[\s\S]*TryResolveYuyukoRetakePhase3EvaluationRoute\([\s\S]*YuyukoRetakePhase3EvaluationRoute\.ManualBoss[\s\S]*"EvaulateManualOrder"[\s\S]*TryEvaluateRuntimeOrderIfReady\(/,
  'Retake refresh orders must reuse the same per-order native route.',
);

const retakeManualClassifier = namedMethodSource(
  yuyukoEvaluationTracker,
  'IsYuyukoRetakePhase3ManualProgressCallbackEntry',
);
assert.match(retakeManualClassifier, /DisplayClass16_10[\s\S]*<MainChallengeLoop>b__77[\s\S]*<MainChallengeLoop>b__78/,
  'Retake manual callbacks must be limited to the observed DisplayClass16_10 b__77/b__78 methods.');
const retakeBossClassifier = namedMethodSource(
  yuyukoEvaluationTracker,
  'IsYuyukoRetakeBossProgressCallbackEntry',
);
assert.match(retakeBossClassifier, /YuyukoOverrideEvaluationCallback_50[\s\S]*DisplayClass16_6[\s\S]*\|50/,
  'Retake boss progress must use the exact _50 callback shape.');
assert.doesNotMatch(retakeBossClassifier, /GroupOverrideEvaluationCallback|DisplayClass16_9|\|70/,
  'The boss classifier must not also accept group _70.');
const retakeGroupClassifier = namedMethodSource(
  yuyukoEvaluationTracker,
  'IsYuyukoRetakeGroupProgressCallbackEntry',
);
assert.match(retakeGroupClassifier, /GroupOverrideEvaluationCallback_70[\s\S]*DisplayClass16_9[\s\S]*\|70/,
  'Retake group progress must use the exact _70 callback shape.');
assert.doesNotMatch(retakeGroupClassifier, /YuyukoOverrideEvaluationCallback_50|DisplayClass16_6|\|50/,
  'The group classifier must not also accept boss _50.');

assert.match(capture, /IsOrderDeliveryContext[\s\S]*order\.IsFulfilled[\s\S]*UpdateExistingOrder\(order with[\s\S]*"Fulfilled"/);
assert.match(provider, /HasServedFood = captured\.IsFulfilled[\s\S]*HasServedBeverage = captured\.IsFulfilled/);
assert.match(
  provider,
  /captured\.OrderObject == null[\s\S]*captured\.ControllerObject == null[\s\S]*string\.IsNullOrWhiteSpace\(captured\.RuntimeKey\)[\s\S]*continue;/,
  'Only a capture created with an exact order/controller/native-key binding may be projected.',
);
assert.match(
  capture,
  /"PushToOrder"[\s\S]*null,[\s\S]*nameof\(OnControllerOrderAdded\)[\s\S]*exactMethodPredicate: IsExactOrderBaseMethod/,
  'Special-order ownership must be committed only by the successful PushToOrder postfix.',
);
assert.match(
  capture,
  /OnControllerOrderAdded\(object __instance, object __0, bool __runOriginal\)[\s\S]*if \(!__runOriginal\) return;[\s\S]*lifecycleSequence = BeginOrderLifecycle\([\s\S]*__0,[\s\S]*__instance,[\s\S]*"ControllerOrderAdd"[\s\S]*order = ParseOrder\(__0, "ControllerOrderAdd", __instance\)[\s\S]*AddOrder\(order == null \|\| lifecycleSequence <= 0/,
  'A skipped PushToOrder call must not create a controller binding or order lifecycle.',
);
assert.match(
  capture,
  /"CleanOrderInfo"[\s\S]*nameof\(CaptureControllerOrderBeforeCompletion\)[\s\S]*nameof\(OnOrderCleanupSucceeded\)/,
  'The exact native cleanup path must retire its latched current order after success.',
);
for (const [label, source, callbackName] of [
  ['special', capture, 'OnOrderRepellSucceeded'],
  ['normal', normalCapture, 'OnControllerOrderRepellSucceeded'],
]) {
  const repellPredicate = methodSource(source, 'private static bool IsExactRepellInternal(');
  assert.match(
    repellPredicate,
    /parameters\[1\]\.IsOut[\s\S]*ParameterType\.IsByRef[\s\S]*GetElementType\(\) == typeof\(bool\)[\s\S]*LeaveTypeName/,
    `${label} capture must select the exact RepellInternal out-bool signature.`,
  );
  const repellPostfix = methodSource(source, `private static void ${callbackName}(`);
  assert.match(
    repellPostfix,
    /if \(!__runOriginal \|\| __state == null\) return;[\s\S]*PublishAndRemoveOrder\([\s\S]*__state,[\s\S]*RuntimeOrderTerminalDisposition\.Removed,[\s\S]*RuntimeOrderTerminalReceiptSource\.RepellInternal/,
    `${label} capture must publish an exact removal receipt and retire the latched order whenever RepellInternal returns normally.`,
  );
  assert.doesNotMatch(
    repellPostfix,
    /__1|haveSeated/,
    `${label} capture must not treat RepellInternal.haveSeated as order-specific cleanup evidence.`,
  );
}
assert.doesNotMatch(provider, /"AllOrders"|"AllOrdersData"|RuntimeSpecialOrderOwnership/,
  'Night-business projection must not restore historical-stack ownership polling.');
assert.doesNotMatch(normalOrderSnapshot, /"AllOrders"|"AllOrdersData"/,
  'Normal-order projection must not publish historical controller orders.');
assert.doesNotMatch(
  normalOrderSnapshot,
  /PruneMissing|CopyWithOrderKey|BuildRuntimeOrderSlotKey|desk\|food\|beverage/,
  'Normal-order capture must not be rebound or retired through a transient HUD slot identity.',
);
assert.doesNotMatch(
  normalOrderSnapshot,
  /ReconcileRuntimeCapturedOrders|RuntimeCaptureMerged|RuntimeCaptureLive/,
  'Transient HUD visibility must not filter authoritative runtime captures.',
);
const normalSnapshotLoad = methodSource(normalOrderSnapshot, 'public NormalBusinessContext Load(');
assert.match(
  normalSnapshotLoad,
  /if \(runtimeCaptureReady\)[\s\S]*normalOrderMode=authoritativeCapture[\s\S]*capturedNativeKeys[\s\S]*runtimeCapturedOrders\.Concat\(unboundVisibleOrders\)/,
  'A ready normal-order snapshot must publish authoritative captures even during a transient HUD gap.',
);
assert.match(
  normalOrderSnapshot,
  /OrderLifecycleSequence > 0[\s\S]*\|lifecycle:\{order\.OrderLifecycleSequence\}[\s\S]*\|unbound/,
  'Normal-order projection groups must isolate a reused native key by its positive lifecycle sequence.',
);
assert.doesNotMatch(
  provider,
  /MatchesActiveGuest|UnmatchedCapturedOrderGrace/,
  'An active guest or fixed grace period must not keep a retired special order visible.',
);
assert.match(
  runtimeOrderTypes,
  /OrderBaseTypeName[\s\S]*NormalOrderTypeName[\s\S]*SpecialOrderTypeName[\s\S]*hasNormalOrder == hasSpecialOrder/,
  'Concrete order types must use the shared exact OrderBase XOR resolver.',
);
const normalOrderProjection = sourceSlice(
  normalOrderSnapshot,
  'private NormalBusinessOrder? ReadNormalOrder(',
  'private static string BuildRuntimeOrderKey(',
);
const normalCaptureOrderKey = methodSource(normalCapture, 'private static string RuntimeOrderKey(');
const specialCaptureOrderKey = methodSource(capture, 'private static string GetRuntimeObjectKey(');
const normalSnapshotOrderKey = methodSource(normalOrderSnapshot, 'private static string BuildRuntimeOrderKey(');
const normalActionOrderKey = methodSource(matching, 'private static string BuildRuntimeOrderKey(');
for (const [label, keyReader] of [
  ['normal capture', normalCaptureOrderKey],
  ['special capture', specialCaptureOrderKey],
  ['normal snapshot', normalSnapshotOrderKey],
  ['normal action', normalActionOrderKey],
]) {
  assert.match(
    keyReader,
    /TryReadNativeObjectPointer\([^,]+, out var pointer\)[\s\S]*\? \$?"ptr:\{pointer:x\}"[\s\S]*: ""/,
    `${label} key must come only from a readable nonzero native pointer.`,
  );
  assert.doesNotMatch(
    keyReader,
    /ReadObjectPointer|RuntimeHelpers|GetHashCode|RuntimeHelpers\.GetHashCode/,
    `${label} key restored a managed hash or throwing pointer fallback.`,
  );
}
const exactNativePointerReader = methodSource(
  runtimeReflection,
  'public static bool TryReadNativeObjectPointer(',
);
assert.match(
  exactNativePointerReader,
  /pointer = 0[\s\S]*Pointer[\s\S]*NativePointer[\s\S]*m_CachedPtr[\s\S]*return pointer != 0[\s\S]*catch[\s\S]*return false/,
  'The shared native pointer reader must reject missing, zero, and unreadable identities.',
);
assert.doesNotMatch(
  exactNativePointerReader,
  /ReadObjectPointer|RuntimeHelpers|GetHashCode/,
  'The shared native pointer reader restored a managed identity fallback.',
);
assert.match(
  normalOrderProjection,
  /RuntimeOrderTypeResolver\.Resolve\(order\)[\s\S]*resolution\.Kind != RuntimeOrderKind\.Normal[\s\S]*var readableOrder = resolution\.ReadableOrder/,
  'The normal-order business projection must use the shared exact resolver and its concrete wrapper.',
);
assert.doesNotMatch(
  normalOrderProjection,
  /GetType\(\)\.Name|SafeGet\(order, "Type"\)|\.ToString\(\).*Normal/,
  'The normal-order business projection restored a text or enum-value type fallback.',
);
const runtimeOrderResolver = methodSource(
  matching,
  'private static bool TryResolveRuntimeOrder(',
);
assert.match(
  runtimeOrderResolver,
  /RuntimeOrderTypeResolver\.Resolve\(order\)[\s\S]*!resolution\.Resolved[\s\S]*resolution\.Kind != expectedKind[\s\S]*readableOrder = resolution\.ReadableOrder/,
  'Automation matching must fail closed unless the shared resolver returns the expected concrete kind.',
);
assert.doesNotMatch(
  matching,
  /NormalizeRuntimeSpecialOrder|GetType\(\)\.Name[\s\S]{0,180}NormalOrder|SpecialGuests[\s\S]{0,180}Contains\("Special"/,
  'Automation matching restored a local broad order-type inference path.',
);
const yuyukoTargetInvariant = methodSource(
  yuyukoPolicy,
  'private static (bool Applies, bool FoodMatched, bool BeverageMatched, string Diagnostic) BuildYuyukoPhase3NormalOrderTargetInvariant(',
);
assert.match(
  yuyukoTargetInvariant,
  /RuntimeOrderTypeResolver\.Resolve\(runtimeOrder\.Order\)[\s\S]*!resolution\.Resolved[\s\S]*resolution\.Kind == RuntimeOrderKind\.Special/,
  'Yuyuko order branching must reject unresolved wrappers before selecting normal or special behavior.',
);
assert.doesNotMatch(yuyukoPolicy, /IsSpecialOrder\(/);
assert.match(
  methodSource(wackyOrderModule, 'private static bool IsExactSpecialOrder('),
  /RuntimeOrderTypeResolver\.Resolve\(order\)[\s\S]*resolution\.Resolved && resolution\.Kind == RuntimeOrderKind\.Special/,
  'Wacky phase-3 order evidence must use the shared exact concrete type resolver.',
);
const wackyManualOrder = methodSource(
  wackyOrderModule,
  'private static bool TryReadExactManualOrder(',
);
assert.match(
  wackyManualOrder,
  /RuntimeOrderTypeResolver\.Resolve\(order\)[\s\S]*GetProperty\([\s\S]*"ManualOrder"[\s\S]*PropertyType != typeof\(bool\)[\s\S]*GetValue\(resolution\.ReadableOrder\) is not bool/,
  'Wacky manual-order evidence must use the exact bool property on a resolved concrete order.',
);
assert.doesNotMatch(
  wackyOrderModule,
  /LooksLikeKoishiSpecialOrder|GetType\(\)\.FullName|bool\.TryParse|raw\?\.ToString/,
  'Wacky classification restored type-name or stringified-bool compatibility inference.',
);
assert.doesNotMatch(
  capture,
  /ParseOrderText|SafeToString|LooksLikeSpecialOrder|IsSpecialOrderType|BuildControllerRemovalOrder/,
  'Special-order capture restored text inference or an identity-incomplete removal fallback.',
);

assert.match(directDelivery, /parameters\.Length == 2[\s\S]*parameters\[1\]\.ParameterType == typeof\(int\)/);
assert.match(directDelivery, /methods\[0\]\.Invoke\(configure, new object\?\[\] \{ cookedFood, -1 \}\)/);
assert.ok(!directDelivery.includes('TryBuildStoreFoodArguments'));
assert.match(directDelivery, /storedAfterException[\s\S]*AutomationCommitResolution\.Committed[\s\S]*AutomationCommitResolution\.Uncertain/);
assert.match(
  directDelivery,
  /if \(target\.FoodId < 0\)[\s\S]*目标料理 ID 无效/,
  'Automation delivery must reject an invalid negative target before any delivery or warmer write.',
);
assert.match(
  directDelivery,
  /if \(cookedFoodIdentity\.FoodId != target\.FoodId\)[\s\S]*TryStoreMismatchedCookResultInWarmer/,
  'A valid target with an exact Food/-1 result must enter mismatch warmer recovery.',
);
assert.doesNotMatch(
  directDelivery,
  /if \(target\.FoodId >= 0 && cookedFoodIdentity\.FoodId != target\.FoodId\)/,
  'Negative targets must not bypass the exact result comparison.',
);

assert.match(delivery, /TryReadRuntimeOrderEvaluated[\s\S]*InvokeInstance\(manager, methodName, args\)[\s\S]*OrderEvaluationCommitUncertain/);
assert.match(delivery, /InvokeInstance\(manager, methodName, args\)[\s\S]*IsNightBusinessGenerationActive\(sessionGeneration\)[\s\S]*BuildEndedNightBusinessEvaluation\(orderLabel, commitMayHaveStarted: true\)/);
assert.match(delivery, /TryInvokeDeliverySetter[\s\S]*out bool invocationAttempted/);
assert.match(delivery, /writtenInAirItem == null[\s\S]*return UncertainDelivery/);
assert.match(delivery, /TryUpdateGuestTableVisual[\s\S]*IsNightBusinessGenerationActive\(sessionGeneration\)[\s\S]*TryClearOrderInAirAndVerify/);
assert.ok(!delivery.includes('TryInvokeInstance(runtimeOrder.Order, setterName'));
const completionBuilder = methodSource(
  directDelivery,
  'private static AutomationFoodDeliveryCompletion BuildFoodDeliveryCompletion(',
);
assert.ok(
  !completionBuilder.includes('TryEvaluate'),
  'The food-delivery metadata builder must not evaluate an order.',
);
const committedFoodTransaction = methodSource(
  directDelivery,
  'private static (bool Remove, string Message, string Code) TryCompleteCommittedFoodDeliveryTransaction(',
);
assert.match(
  committedFoodTransaction,
  /TryCompleteCommittedFoodDeliveryCleanup\(job\)[\s\S]*TryResolveCommittedFoodDeliveryEvaluation\(job/,
  'A committed food-delivery transaction must independently close cooker cleanup and current evaluation control.',
);
const committedFoodEvaluation = methodSource(
  directDelivery,
  'private static bool TryResolveCommittedFoodDeliveryEvaluation(',
);
assert.match(
  committedFoodEvaluation,
  /AcquireAutomationCookingJobControlPermit\([\s\S]*RuntimeAutomationControlStage\.OrderEvaluation[\s\S]*if \(!permit\.Allowed\)[\s\S]*get_IsFullfilled[\s\S]*TryEvaluateMatchedAutomationOrderRuntimeIfReady/,
  'Committed delivery must reacquire current completion authority before reading fulfillment or evaluating.',
);
assert.ok(
  committedFoodEvaluation.indexOf('RuntimeOrderTerminalReceiptStore.TryFind(')
      < committedFoodEvaluation.indexOf('FindRuntimeNormalOrder(')
    && committedFoodEvaluation.indexOf('TryMatchRuntimeOrderBinding(')
      < committedFoodEvaluation.indexOf('get_IsFullfilled'),
  'Committed evaluation must consume an exact terminal receipt first, then match the fresh order/controller token before reading fulfillment.',
);
assert.doesNotMatch(service, /public bool Auto(?:DeliverFood|CompleteOrder) \{ get; set; \}/,
  'Cooking jobs must not retain creation-time delivery or completion switches.');
assert.doesNotMatch(cooking, /Auto(?:DeliverFood|CompleteOrder) = auto(?:DeliverFood|CompleteOrder)/,
  'Cooking-job registration must not latch delivery or completion intent.');

const directFoodDelivery = methodSource(
  directDelivery,
  'private static (bool Remove, string Message, string Code) TryDeliverAutomationCookedFood(',
);
const foodIdentityValidation = directFoodDelivery.indexOf('TryReadCookControllerFoodResultIdentity(');
const targetIdentityValidation = directFoodDelivery.indexOf('TryDetectSpecialFoodTargetPolicyChanged(');
const targetUnavailableGate = directFoodDelivery.indexOf('if (!specialTargetComparisonAvailable)');
const targetChangedGate = directFoodDelivery.indexOf('if (specialTargetChanged)');
const tagValidation = directFoodDelivery.indexOf('ValidateSpecialFoodTargetTags(');
const yuumaSettlementCall = directFoodDelivery.indexOf('TryFinalizeYuumaCookingJob(job, cookedFood)');
const exactRuntimeOrderGate = directFoodDelivery.indexOf('TryMatchRuntimeOrderBinding(');
const deliveryFailureReset = directFoodDelivery.indexOf('job.ResetDeliveryFailures()');
assert.ok(
  foodIdentityValidation >= 0
    && targetIdentityValidation > foodIdentityValidation
    && targetUnavailableGate > targetIdentityValidation
    && targetChangedGate > targetUnavailableGate
    && tagValidation > targetIdentityValidation
    && yuumaSettlementCall > tagValidation,
  'Blood Pond Hell cooked food must validate ID, target identity, and exact Tags before entering its settlement transaction.',
);
assert.ok(
  exactRuntimeOrderGate > tagValidation
    && deliveryFailureReset > exactRuntimeOrderGate
    && yuumaSettlementCall > exactRuntimeOrderGate,
  'No cooked food may reach a delivery side effect or Yuuma settlement before matching the opening generation/kind/order/controller token.',
);
assert.match(
  directFoodDelivery,
  /if \(!specialTargetComparisonAvailable\)[\s\S]*本轮未送达、入箱或复位厨具，等待权威目标恢复。[\s\S]*if \(specialTargetChanged\)/,
  'An unavailable special target must wait without being treated as a confirmed target rotation.',
);
assert.match(
  directFoodDelivery,
  /TryFinalizeYuumaCookingJob\(job, cookedFood\)/,
  'Validated Blood Pond Hell food does not enter its dedicated settlement transaction.',
);
assert.match(
  directFoodDelivery,
  /YuumaSettlementTracker\.Stage[\s\S]*!=[\s\S]*YuumaSettlementTransactionStage\.Ready[\s\S]*TryFinalizeYuumaCookingJob\([\s\S]*return TryFinalizeYuumaCookingJob\(job, cookedFood\)/,
  'A validated Yuuma result must remain on the dedicated monotonic settlement path after current control admits it.',
);
assert.doesNotMatch(
  directFoodDelivery,
  /job\.AutoDeliverFood|job\.AutoCompleteOrder|EnterManualHandoff\(/,
  'Yuuma delivery still branches on creation-time switches or treats a configuration pause as manual handoff.',
);

const cookingProcessor = methodSource(
  cooking,
  'private static (bool Remove, string Message, string Code) TryProcessAutomationCookingJob(',
);
const controlObservation = cookingProcessor.indexOf('ObserveAutomationCookingJobControl(');
const cookerReacquire = cookingProcessor.indexOf('TryReacquireAutomationCooker(');
const ownedResultDirective = cookingProcessor.indexOf(
  'if (transition.Directive == AutomationCookingJobDirective.DeliverOwnedResult && cookedFood != null)',
);
const currentControlGate = cookingProcessor.indexOf('if (!controlDecision.Allowed)', ownedResultDirective);
const currentControlPermit = cookingProcessor.indexOf(
  'AcquireAutomationCookingJobControlPermit(',
  currentControlGate,
);
const deliveryCall = cookingProcessor.indexOf(
  'return TryDeliverAutomationCookedFood(job, cookedFood);',
  currentControlPermit,
);
assert.ok(
  controlObservation >= 0
    && cookerReacquire > controlObservation
    && ownedResultDirective > cookerReacquire
    && currentControlGate > ownedResultDirective
    && currentControlPermit > currentControlGate
    && deliveryCall > currentControlPermit,
  'A ready cooked result can reach delivery before observing and acquiring current automation control.',
);
assert.match(
  cookingProcessor,
  /job\.ControlSuspended[\s\S]*cooking-controller-reused[\s\S]*cooking-ownership-lost[\s\S]*return EnterManualHandoff\(job, nowUtc\)/,
  'A suspended job no longer converts exact player ownership loss into a retained manual-handoff receipt.',
);
assert.doesNotMatch(cookingProcessor, /job\.AutoDeliverFood|job\.AutoCompleteOrder/,
  'The cooking processor still reads creation-time stage switches.');
const pendingControlStage = methodSource(
  automationControl,
  'private static RuntimeAutomationControlStage GetPendingCookingJobControlStage(',
);
assert.match(
  pendingControlStage,
  /IsYuumaBossTarget\(job\.Target\)[\s\S]*RuntimeAutomationControlStage\.YuumaSettlement/,
  'Yuuma finalization no longer uses the atomic settlement control stage.',
);

const yuumaRequestIdentity = methodSource(
  specialTargetPolicy,
  'private static bool IsYuumaBossRequest(',
);
assert.match(
  yuumaRequestIdentity,
  /string\.Equals\([\s\S]*request\.SpecialBusinessRole,[\s\S]*SpecialBusinessOrderRoles\.YuumaBoss,[\s\S]*StringComparison\.Ordinal\)/,
  'Yuuma request identity must use the exact Ordinal boss role.',
);
assert.doesNotMatch(yuumaRequestIdentity, /IgnoreCase|Contains|StartsWith|EndsWith/);
const yuumaTargetIdentity = methodSource(
  specialTargetPolicy,
  'private static bool IsYuumaBossTarget(',
);
assert.match(
  yuumaTargetIdentity,
  /string\.Equals\([\s\S]*target\.SpecialBusinessRole,[\s\S]*SpecialBusinessOrderRoles\.YuumaBoss,[\s\S]*StringComparison\.Ordinal\)/,
  'Yuuma cooking-target identity must use the exact Ordinal boss role.',
);
assert.doesNotMatch(yuumaTargetIdentity, /IgnoreCase|Contains|StartsWith|EndsWith/);

const yuumaDeliveryState = methodSource(
  service,
  'private static bool TryReadYuumaOrderDeliveryState(',
);
assert.match(
  yuumaDeliveryState,
  /TryReadOrderServedItem\([\s\S]*RuntimeDeliveryItemKind\.Food[\s\S]*TryReadOrderInAirItem\([\s\S]*RuntimeDeliveryItemKind\.Food[\s\S]*TryReadOrderServedItem\([\s\S]*RuntimeDeliveryItemKind\.Beverage[\s\S]*TryReadOrderInAirItem\([\s\S]*RuntimeDeliveryItemKind\.Beverage[\s\S]*return foodRead && foodInAirRead && beverageRead && beverageInAirRead;/,
  'The final-item gate must strictly read final and in-air food and beverage fields.',
);

for (const [label, source] of [['special', capture], ['normal', normalCapture]]) {
  assert.match(source, /public bool ManualOrder \{ get; init; \}/,
    `${label} capture must retain exact OrderBase.ManualOrder.`);
  assert.match(source, /internal object\? ManualEvaluationCallback \{ get; init; \}/,
    `${label} capture must retain the exact native manual callback.`);
  assert.match(source, /public bool ManualEvaluationBindingObserved \{ get; init; \}/,
    `${label} capture must distinguish a stable setter binding from transient ManualOrder state.`);
  assert.match(source, /public bool ManualEvaluationBindingConflict \{ get; init; \}/,
    `${label} capture must retain conflicting setter evidence for fail-closed routing.`);
  assert.match(source, /internal object\? ManualEvaluationBindingCallback \{ get; init; \}/,
    `${label} capture must retain the stable setter callback until order retirement.`);
  const manualSetter = methodSource(source, 'private static void OnManualControllerOrderSet(');
  assert.match(manualSetter, /object\? __1[\s\S]*order is not \{ ManualOrder: true \}[\s\S]*ManualEvaluationCallback = __1[\s\S]*ManualEvaluationBindingObserved = true[\s\S]*ManualEvaluationBindingCallback = __1/,
    `${label} manual setter must bind its exact nullable callback as stable lifecycle evidence only after ManualOrder=true.`);
  const mergeCapturedOrder = namedMethodSource(source, 'MergeCapturedOrder');
  assert.match(mergeCapturedOrder, /ManualEvaluationBindingConflict[\s\S]*HaveConflictingManualEvaluationBindings[\s\S]*ManualEvaluationBindingObserved[\s\S]*ManualEvaluationBindingCallback/,
    `${label} capture must keep setter evidence across later status merges and detect conflicts.`);
  assert.match(source, /requireExactManualOrderSetter: true/,
    `${label} capture must request the exact BepInEx 783 manual-order setter signature.`);
  const exactManualSetter = methodSource(source, 'private static bool IsExactManualOrderSetter(');
  assert.match(exactManualSetter,
    /parameters\[0\]\.ParameterType\.FullName[\s\S]*GuestGroupControllerTypeName[\s\S]*parameters\[2\]\.ParameterType\.FullName[\s\S]*OrderBaseTypeName/,
    `${label} capture must match the exact controller and OrderBase wrapper names.`);
  assert.match(exactManualSetter,
    /GetGenericTypeDefinition\(\)\.FullName[\s\S]*Il2CppActionGenericTypeName[\s\S]*callbackArguments\[0\]\.FullName[\s\S]*EvaluationResultTypeName/,
    `${label} capture must match only Il2CppSystem.Action<GuestGroupController.EvaluationResult>.`);
  const exactManualOrder = methodSource(source, 'private static bool TryReadExactManualOrder(');
  assert.match(
    exactManualOrder,
    /TryReadExactOrderBool\(order, "ManualOrder", out manualOrder\)/,
    `${label} capture must route ManualOrder through the exact bool-property reader.`,
  );
  const exactOrderBool = methodSource(source, 'private static bool TryReadExactOrderBool(');
  assert.match(
    exactOrderBool,
    /GetProperty\(propertyName, flags\)[\s\S]*PropertyType != typeof\(bool\)[\s\S]*GetValue\(order\) is not bool/,
    `${label} capture must read the exact bool ManualOrder property.`);
  assert.doesNotMatch(exactOrderBool, /ReadMember|GetMemberValue|InvokeMethod/,
    `${label} ManualOrder capture restored a broad reflection fallback.`);
  assert.match(
    source,
    /TryReadExactOrderBool\([^,\n]+, "IsFullfilled", out var \w+\)/,
    `${label} completion capture must read the exact OrderBase.IsFullfilled bool property.`,
  );
}
assert.match(matching, /public bool ManualOrder \{ get; init; \}/);
assert.match(matching, /ManualOrder = requiresYuumaSettlementManualContext[\s\S]*\? manualOrder[\s\S]*: requiresYuyukoManualBinding/);
assert.match(
  matching,
  /ManualEvaluationCallback = requiresYuumaSettlementManualContext[\s\S]*\? manualOrder \? manualEvaluationCallback : null[\s\S]*: requiresYuyukoManualBinding/,
  'Yuuma settlement matches must project the fresh wrapper ManualOrder/callback state.',
);
assert.doesNotMatch(
  matching,
  /ManualOrder = requiresYuumaSettlementManualContext && captured\.ManualOrder/,
  'Captured ManualOrder must not be trusted as current executable state.',
);

const capturedSpecialLookup = namedMethodSource(matching, 'FindCapturedRuntimeOrder');
const capturedSpecialLivenessGate = capturedSpecialLookup.indexOf('IsCapturedSpecialOrderLive(');
const capturedSpecialManualRefresh = capturedSpecialLookup.indexOf(
  'TryResolveSpecialManualContext(',
  capturedSpecialLivenessGate,
);
const capturedSpecialProjection = capturedSpecialLookup.indexOf(
  'return new RuntimeOrderMatch',
  capturedSpecialManualRefresh,
);
assert.ok(
  capturedSpecialLivenessGate >= 0
    && capturedSpecialManualRefresh > capturedSpecialLivenessGate
    && capturedSpecialProjection > capturedSpecialManualRefresh,
  'A captured special Yuuma candidate must refresh manual state after liveness/identity and before projection.',
);
assert.match(
  capturedSpecialLookup.slice(capturedSpecialManualRefresh, capturedSpecialProjection + 800),
  /out manualOrder[\s\S]*out manualEvaluationCallback[\s\S]*ManualOrder = requiresYuumaSettlementManualContext[\s\S]*\? manualOrder[\s\S]*: requiresYuyukoManualBinding/,
  'Captured special Yuuma projection must use only current wrapper manual state.',
);

const capturedNormalLookup = namedMethodSource(matching, 'FindCapturedRuntimeNormalOrder');
const capturedNormalOwnershipGate = capturedNormalLookup.indexOf('TryInvokeInstanceValue(captured.ControllerObject, "PeekOrders")');
const capturedNormalIdentityGate = capturedNormalLookup.indexOf(
  'IsMatchingNormalOrder(',
  capturedNormalOwnershipGate,
);
const capturedNormalManualRefresh = capturedNormalLookup.indexOf(
  'TryResolveNormalManualContext(',
  capturedNormalIdentityGate,
);
const capturedNormalProjection = capturedNormalLookup.indexOf(
  'return new RuntimeOrderMatch',
  capturedNormalManualRefresh,
);
assert.ok(
  capturedNormalOwnershipGate >= 0
    && capturedNormalIdentityGate > capturedNormalOwnershipGate
    && capturedNormalManualRefresh > capturedNormalIdentityGate
    && capturedNormalProjection > capturedNormalManualRefresh,
  'A captured normal Yuuma candidate must refresh manual state after ownership/identity and before projection.',
);
assert.match(
  capturedNormalLookup.slice(capturedNormalManualRefresh, capturedNormalProjection + 800),
  /out manualOrder[\s\S]*out manualEvaluationCallback[\s\S]*ManualOrder = requiresYuumaSettlementManualContext[\s\S]*\? manualOrder[\s\S]*: requiresYuyukoManualBinding/,
  'Captured normal Yuuma projection must use only current wrapper manual state.',
);

const normalManualContextLookup = namedMethodSource(matching, 'TryResolveNormalManualContext');
for (const [label, resolver] of [
  ['special', storyManualCallbackLookup],
  ['normal', normalManualContextLookup],
]) {
  assert.match(
    resolver,
    label === 'special'
      ? /if \(!SpecialOrderRuntimeCapture\.IsBusinessReady\)[\s\S]*return false/
      : /if \(!NormalOrderRuntimeCapture\.IsBusinessReady\)[\s\S]*return false/,
    `${label} manual callback lookup must reject a partially covered capture generation.`,
  );
  assert.match(
    resolver,
    /TryReadExactManualOrder\([\s\S]*if \(!manualOrder\)[\s\S]*manualCallback=not-required[\s\S]*return true/,
    `${label} captured candidates must accept current ManualOrder=false regardless of captured ManualOrder=true.`,
  );
  assert.match(
    resolver,
    /if \(!manualOrder\)[\s\S]*callbackCandidate[\s\S]*if \(callbackCandidate == null\)[\s\S]*manualCallback=missing[\s\S]*return false/,
    `${label} captured candidates must reject current ManualOrder=true when no exact current callback can be recovered.`,
  );
}

const yuumaFinalization = namedMethodSource(yuumaSettlement, 'TryFinalizeYuumaCookingJob');
assert.doesNotMatch(
  yuumaFinalization,
  /ShouldPlayerThrowDeliver/,
  'The player ThrowDeliver buff capability must not block dedicated headless food settlement.',
);
assert.doesNotMatch(
  yuumaSettlement,
  /TryReadShouldPlayerThrowDeliver/,
  'The removed ThrowDeliver capability reader was restored to the Yuuma settlement service.',
);
const yuumaSettlementOrderValidation = namedMethodSource(
  yuumaSettlement,
  'TryValidateYuumaSettlementOrder',
);
assert.match(
  yuumaSettlementOrderValidation,
  /TryReadYuumaOrderDeliveryState\([\s\S]*out var foodInAir,[\s\S]*out var servedBeverage,[\s\S]*out var beverageInAir,[\s\S]*if \(servedFood != null \|\| foodInAir != null\)[\s\S]*if \(beverageInAir != null\)[\s\S]*return false;[\s\S]*if \(servedBeverage == null\)/,
  'Final-food settlement must reject both FoodInAir and BeverageInAir before committing food.',
);
const yuumaIdentityGate = yuumaFinalization.indexOf('!IsYuumaBossTarget(job.Target)');
const settlementPreflight = yuumaFinalization.indexOf('TryPreflightYuumaSettlement(');
const settlementOrderValidation = yuumaFinalization.indexOf('TryValidateYuumaSettlementOrder(');
const settlementFreshCooker = yuumaFinalization.indexOf(
  'TryValidateYuumaCookerBeforeFoodCommit(',
);
const settlementIrreversibleClaim = yuumaFinalization.indexOf('TryBeginFoodCommit()');
const settlementCommit = yuumaFinalization.indexOf(
  'finalFoodSetter.Invoke(runtimeOrder.Order, new[] { cookedFood })',
);
const firstOrderReacquire = yuumaFinalization.indexOf(
  'FindYuumaRuntimeOrder(job.Target, request)',
  settlementCommit,
);
const firstReacquireValidation = yuumaFinalization.indexOf(
  'TryValidateReacquiredYuumaSettlementOrder(',
  firstOrderReacquire,
);
const settlementCookerReset = yuumaFinalization.indexOf(
  'TryResetCookControllerAfterCommittedSideEffect(job, cookedFood, out var resetDiagnostic)',
  firstReacquireValidation,
);
const settlementExtraction = yuumaFinalization.indexOf(
  'TryCompleteYuumaCookerExtraction(',
  settlementCookerReset,
);
const settlementCleanup = yuumaFinalization.indexOf('MarkCleanupCommitted(');
const secondOrderReacquire = yuumaFinalization.indexOf(
  'FindYuumaRuntimeOrder(job.Target, request)',
  firstOrderReacquire + 1,
);
const secondReacquireValidation = yuumaFinalization.indexOf(
  'TryValidateReacquiredYuumaSettlementOrder(',
  firstReacquireValidation + 1,
);
const settlementEvaluation = yuumaFinalization.indexOf('TryInvokeYuumaEvaluation(');
const settlementBookkeeping = yuumaFinalization.indexOf('TryApplyYuumaDeliveryBookkeeping(');
assert.ok(
  yuumaIdentityGate >= 0
    && yuumaIdentityGate < settlementCommit
    && settlementOrderValidation >= 0
    && settlementOrderValidation < settlementPreflight
    && settlementPreflight >= 0
    && settlementFreshCooker > settlementPreflight
    && settlementIrreversibleClaim > settlementFreshCooker
    && settlementCommit > settlementIrreversibleClaim
    && firstOrderReacquire > settlementCommit
    && firstReacquireValidation > firstOrderReacquire
    && settlementCookerReset > firstReacquireValidation
    && settlementExtraction > settlementCookerReset
    && settlementCleanup > settlementExtraction
    && secondOrderReacquire > settlementCleanup
    && secondReacquireValidation > secondOrderReacquire
    && settlementEvaluation > secondReacquireValidation
    && settlementBookkeeping > settlementEvaluation,
  'Yuuma settlement must fresh-bind the cooker before the irreversible claim, commit the final setter, revalidate, reset the cooker, run exact extraction callbacks, reacquire and fully revalidate a second time, then evaluate and apply bookkeeping.',
);
assert.doesNotMatch(
  yuumaFinalization,
  /job\.AutoDeliverFood|job\.AutoCompleteOrder/,
  'Yuuma settlement still reads creation-time switches after the current atomic permit admitted it.',
);
assert.doesNotMatch(
  yuumaFinalization.slice(settlementFreshCooker, settlementIrreversibleClaim),
  /MarkUncertain/,
  'A side-effect-free fresh-cooker rejection must not become an uncertain native commit.',
);
assert.doesNotMatch(
  yuumaFinalization,
  /TryCommitRuntimeDelivery\(/,
  'Yuuma settlement must not restore the generic in-air/table-visual delivery path.',
);
const evaluationReturned = yuumaFinalization.indexOf(
  'if (!job.YuumaSettlementTracker.MarkEvaluationCommitted())',
  settlementEvaluation,
);
assert.ok(evaluationReturned > settlementEvaluation,
  'The post-evaluation source boundary is missing.');
const postEvaluationSettlement = yuumaFinalization.slice(evaluationReturned);
assert.doesNotMatch(
  postEvaluationSettlement,
  /FindYuumaRuntimeOrder|TryRead[A-Z]|ReadSellableId|CompareObjectIdentity|runtimeOrder\.|committedOrder\.|cookedFood/,
  'After native evaluation returns, settlement must not reacquire or inspect any order/item wrapper.',
);
assert.match(
  postEvaluationSettlement,
  /TryApplyYuumaDeliveryBookkeeping\(bookkeepingContext, out var bookkeepingDiagnostic\)/,
  'Post-evaluation bookkeeping must consume only the opaque context cached before evaluation.',
);

const yuumaEvaluation = namedMethodSource(yuumaSettlement, 'TryInvokeYuumaEvaluation');
const yuumaContextCreation = namedMethodSource(yuumaSettlement, 'TryCreateYuumaSettlementContext');
assert.match(yuumaContextCreation, /runtimeOrder\.ManualOrder[\s\S]*YuumaOrderEvaluationRoute\.ManualControlled[\s\S]*YuumaOrderEvaluationRoute\.Standard/,
  'Yuuma evaluation route must derive only from the exact runtime match ManualOrder bit.');
assert.match(yuumaEvaluation, /get_IsFullfilled/,
  'Yuuma evaluation must fresh-check fulfillment after the delivery setter.');
assert.match(yuumaEvaluation, /YuumaOrderEvaluationRoute\.ManualControlled[\s\S]*ManualEvaluationCallback == null[\s\S]*manualMethod\.Invoke/,
  'Manual-controlled Yuuma orders must require their exact callback and use EvaulateManualOrder.');
assert.match(yuumaEvaluation, /YuumaOrderEvaluationRoute\.Standard[\s\S]*standardMethod\.Invoke/,
  'Standard Yuuma orders must use EvaluateOrder.');
assert.match(yuumaEvaluation, /manualMethod\.Invoke\([\s\S]*runtimeOrder\.Controller, runtimeOrder\.ManualEvaluationCallback/,
  'Manual-controlled evaluation must invoke the unique two-argument entry with the exact captured callback.');
assert.match(yuumaEvaluation, /standardMethod\.Invoke\([\s\S]*runtimeOrder\.Controller, false, null/,
  'Standard evaluation must invoke the unique three-argument entry with partner=false and no callback.');
const yuumaEvaluationResolver = namedMethodSource(yuumaSettlement, 'TryResolveYuumaEvaluationMethod');
assert.match(yuumaEvaluationResolver, /"EvaulateManualOrder"[\s\S]*"EvaluateOrder"/,
  'The exact evaluation resolver must name both native evaluation routes.');
assert.match(
  yuumaEvaluationResolver,
  /parameters\[0\]\.ParameterType\.FullName[\s\S]*YuumaGuestGroupControllerTypeName[\s\S]*parameters\[0\]\.ParameterType\.IsInstanceOfType\(runtimeOrder\.Controller\)/,
  'Both Yuuma evaluation routes must require the exact GuestGroupController FullName and wrapper instance.',
);
assert.match(yuumaEvaluationResolver, /parameters\.Length == 2[\s\S]*parameters\[1\]\.ParameterType == context\.ManualEvaluationCallback\.GetType\(\)[\s\S]*IsExactYuumaManualEvaluationCallbackType\(parameters\[1\]\.ParameterType\)/,
  'The manual route must validate the exact controller and callback parameter shapes.');
assert.match(yuumaEvaluationResolver, /parameters\.Length == 3[\s\S]*parameters\[1\]\.ParameterType == typeof\(bool\)[\s\S]*parameters\[2\]\.ParameterType == typeof\(Il2CppSystem\.Action\)/,
  'The standard route must validate controller, bool, and Il2CppSystem.Action parameter shapes.');
assert.doesNotMatch(yuumaEvaluation, /ManualEvaluationCallback\s*\?\?/,
  'A missing manual callback must never fall back to standard evaluation.');
assert.doesNotMatch(yuumaEvaluation, /IsManualControlledOrder|CaptureSource|ToString\(/,
  'Yuuma evaluation must not re-infer the exact captured route from broad runtime heuristics.');
const yuumaManualCallbackType = namedMethodSource(
  yuumaSettlement,
  'IsExactYuumaManualEvaluationCallbackType',
);
assert.match(
  yuumaManualCallbackType,
  /type\.IsGenericType[\s\S]*type\.GetGenericTypeDefinition\(\)\.FullName[\s\S]*Il2CppActionGenericTypeName[\s\S]*arguments\.Length == 1[\s\S]*arguments\[0\]\.FullName[\s\S]*YuumaEvaluationResultTypeName/,
  'Manual Yuuma evaluation must accept only the closed Il2CppSystem.Action<EvaluationResult> callback type.',
);
const yuumaReacquire = namedMethodSource(yuumaSettlement, 'TryValidateReacquiredYuumaSettlementOrder');
assert.match(yuumaReacquire, /currentRoute != context\.EvaluationRoute[\s\S]*ReferenceEquals\(runtimeOrder\.ManualEvaluationCallback, context\.ManualEvaluationCallback\)/,
  'Reacquisition must preserve both the exact route and callback object across the delivery boundary.');
for (const [label, pattern] of [
  ['order pointer', /orderPointer != context\.OrderPointer/],
  ['controller pointer', /controllerPointer != context\.ControllerPointer/],
  ['complete delivery state', /TryReadYuumaOrderDeliveryState\([\s\S]*out var servedFood[\s\S]*out var foodInAir[\s\S]*out var servedBeverage[\s\S]*out var beverageInAir/],
  ['no native item in air', /foodInAir != null \|\| beverageInAir != null/],
  ['final cooked food', /CompareObjectIdentity\(servedFood, cookedFood\) != RuntimeObjectIdentityComparison\.Same/],
  ['final beverage', /servedBeverage == null[\s\S]*TryValidateYuumaDeliveredItemAgainstOriginalOrder\([\s\S]*job\.Target[\s\S]*servedBeverage[\s\S]*RuntimeDeliveryItemKind\.Beverage/],
  ['canonical Yuuma identity', /YuumaChallengeOrderIdentity\.Read\(runtimeOrder\.Order, runtimeOrder\.Controller\)[\s\S]*identity\.OrderGuestId != SpecialBusinessGuestIds\.YuumaBoss[\s\S]*identity\.ControllerGuestId != SpecialBusinessGuestIds\.YuumaBoss/],
  ['active business generation', /IsNightBusinessGenerationActive\(context\.BusinessGeneration\)/],
]) {
  assert.match(yuumaReacquire, pattern,
    `Yuuma order reacquisition is missing strict post-callback ${label} validation.`);
}

const yuumaRuntimeOrderLookup = namedMethodSource(yuumaSettlement, 'FindYuumaRuntimeOrder');
const yuumaGenerationGate = yuumaRuntimeOrderLookup.indexOf(
  'policy == null || !IsNightBusinessGenerationActive(policy.BusinessGeneration)',
);
const yuumaNormalOrderScan = yuumaRuntimeOrderLookup.indexOf(
  'FindRuntimeNormalOrder(request, RuntimeOrderLookupPurpose.YuumaSettlement)',
);
const yuumaSpecialOrderScan = yuumaRuntimeOrderLookup.indexOf(
  'FindRuntimeOrder(request, RuntimeOrderLookupPurpose.YuumaSettlement)',
);
assert.ok(
  yuumaGenerationGate >= 0
    && yuumaNormalOrderScan > yuumaGenerationGate
    && yuumaSpecialOrderScan > yuumaGenerationGate,
  'Yuuma runtime-order lookup must reject an inactive target generation before scanning either order shape.',
);
assert.match(
  yuumaRuntimeOrderLookup,
  /target\.Kind == CookingCollectionTargetKind\.NormalOrder[\s\S]*FindRuntimeNormalOrder\(request, RuntimeOrderLookupPurpose\.YuumaSettlement\)[\s\S]*FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.YuumaSettlement\)/,
  'Rare and normal Yuuma orders must share the dedicated settlement lookup before the beverage transaction.',
);

const yuumaFinalSetterResolver = namedMethodSource(yuumaSettlement, 'TryResolveYuumaFinalSetter');
assert.match(
  yuumaFinalSetterResolver,
  /parameters\[0\]\.ParameterType\.FullName[\s\S]*YuumaSellableTypeName[\s\S]*parameters\[0\]\.ParameterType\.IsInstanceOfType\(deliveredItem\)/,
  'Yuuma final setters must require the exact Sellable FullName and delivered wrapper instance.',
);

const yuumaPreCommitCookerValidation = namedMethodSource(
  yuumaSettlement,
  'TryValidateYuumaCookerBeforeFoodCommit',
);
assert.match(
  yuumaPreCommitCookerValidation,
  /TryReacquireAutomationCooker\([\s\S]*\.State\.Result[\s\S]*IsSameObject\(current\.State\.Result, cookedFood\)/,
  'Yuuma final food commit must compare cookedFood with the result of a fresh exact cooker binding.',
);
assert.doesNotMatch(
  yuumaPreCommitCookerValidation,
  /job\.CookController/,
  'Yuuma pre-commit validation must not read a retained cooker wrapper.',
);

const yuumaExtractionPreflight = namedMethodSource(
  yuumaSettlement,
  'TryCreateYuumaCookerExtractionContext',
);
assert.match(
  yuumaExtractionPreflight,
  /"OnCookerAvailabilityUpdate"[\s\S]*method\.ReturnType == typeof\(void\)[\s\S]*parameters\.Length == 1[\s\S]*parameters\[0\]\.ParameterType == typeof\(int\)/,
  'Yuuma extraction must resolve the unique exact PartnerManager availability callback.',
);
assert.match(
  yuumaExtractionPreflight,
  /"AfterPlayerExtract"[\s\S]*method\.ReturnType == typeof\(void\)[\s\S]*method\.GetParameters\(\)\.Length == 0/,
  'Yuuma extraction must resolve the unique exact CookController AfterPlayerExtract callback.',
);
assert.match(yuumaExtractionPreflight, /availabilityMethods\.Length != 1 \|\| extractionMethods\.Length != 1/,
  'Yuuma extraction must fail closed unless both callback shapes are unique.');
assert.doesNotMatch(yuumaExtractionPreflight, /TryInvokeInstance/,
  'Yuuma extraction restored a broad reflective invocation fallback.');
const yuumaExtractionContext = sourceSlice(
  yuumaSettlement,
  'private sealed record YuumaCookerExtractionContext(',
  'private sealed record YuumaBeverageStorageContext(',
);
assert.doesNotMatch(
  yuumaExtractionContext,
  /object (?:CookController|PartnerManager)/,
  'Yuuma extraction context must not retain native wrappers across callbacks.',
);

const yuumaExtraction = namedMethodSource(yuumaSettlement, 'TryCompleteYuumaCookerExtraction');
const firstExtractionBinding = yuumaExtraction.indexOf('TryReacquireAutomationCooker(');
const availabilityCallback = yuumaExtraction.indexOf('context.AvailabilityMethod.Invoke(');
const availabilityMinusOne = yuumaExtraction.indexOf('new object?[] { -1 }', availabilityCallback);
const secondExtractionBinding = yuumaExtraction.indexOf(
  'TryReacquireAutomationCooker(',
  firstExtractionBinding + 1,
);
const afterPlayerExtractCallback = yuumaExtraction.indexOf(
  'context.ExtractionMethod.Invoke(',
  secondExtractionBinding,
);
const thirdExtractionBinding = yuumaExtraction.indexOf(
  'TryReacquireAutomationCooker(',
  secondExtractionBinding + 1,
);
assert.ok(
  firstExtractionBinding >= 0
    && availabilityCallback > firstExtractionBinding
    && availabilityMinusOne > availabilityCallback
    && secondExtractionBinding > availabilityMinusOne
    && afterPlayerExtractCallback > secondExtractionBinding,
  'Yuuma cooker cleanup must fresh-bind before availability(-1), then rebind before AfterPlayerExtract.',
);
assert.equal(
  thirdExtractionBinding,
  -1,
  'AfterPlayerExtract may legally start the next PureHellFryer batch and must not reacquire the old cooker generation.',
);
assert.doesNotMatch(
  yuumaExtraction.slice(afterPlayerExtractCallback),
  /RuntimeCookingContentMutation\.Extract/,
  'A legal post-extract cooker takeover must not be rejected for replacing the old Extract receipt.',
);
assert.doesNotMatch(
  yuumaExtraction,
  /context\.CookController|job\.CookController/,
  'Yuuma cooker cleanup invoked a wrapper cached before a native callback.',
);
assert.doesNotMatch(yuumaExtraction, /TryInvokeInstance/,
  'Yuuma extraction completion restored a broad reflective invocation fallback.');
const generalExtraction = namedMethodSource(directDelivery, 'CompleteCookerExtractionAfterReset');
const generalFirstExtractionBinding = generalExtraction.indexOf('TryReacquireAutomationCooker(');
const generalAvailabilityCallback = generalExtraction.indexOf(
  'OnCookerAvailabilityUpdate',
  generalFirstExtractionBinding,
);
const generalSecondExtractionBinding = generalExtraction.indexOf(
  'TryReacquireAutomationCooker(',
  generalFirstExtractionBinding + 1,
);
const generalAfterPlayerExtract = generalExtraction.indexOf(
  'AfterPlayerExtract',
  generalSecondExtractionBinding,
);
const generalThirdExtractionBinding = generalExtraction.indexOf(
  'TryReacquireAutomationCooker(',
  generalSecondExtractionBinding + 1,
);
assert.ok(
  generalFirstExtractionBinding >= 0
    && generalAvailabilityCallback > generalFirstExtractionBinding
    && generalSecondExtractionBinding > generalAvailabilityCallback
    && generalAfterPlayerExtract > generalSecondExtractionBinding
    && generalThirdExtractionBinding < 0,
  'General committed cleanup must fresh-bind before each callback without reading the old cooker after AfterPlayerExtract.',
);
assert.doesNotMatch(
  generalExtraction.slice(generalAfterPlayerExtract),
  /RuntimeCookingContentMutation\.Extract/,
  'General committed cleanup must allow AfterPlayerExtract to start the next cooker generation.',
);
const extractionCall = yuumaFinalization.indexOf('TryCompleteYuumaCookerExtraction(');
const cleanupCommit = yuumaFinalization.indexOf('MarkCleanupCommitted()', extractionCall);
const postExtractTargetValidation = yuumaFinalization.indexOf(
  'TryValidateCurrentYuumaFoodTarget(',
  cleanupCommit,
);
const postExtractOrderReacquire = yuumaFinalization.indexOf(
  'FindYuumaRuntimeOrder(job.Target, request)',
  postExtractTargetValidation,
);
const postExtractOrderValidation = yuumaFinalization.indexOf(
  'TryValidateReacquiredYuumaSettlementOrder(',
  postExtractOrderReacquire,
);
assert.ok(
  extractionCall >= 0
    && cleanupCommit > extractionCall
    && postExtractTargetValidation > cleanupCommit
    && postExtractOrderReacquire > postExtractTargetValidation
    && postExtractOrderValidation > postExtractOrderReacquire,
  'A normal AfterPlayerExtract return must be followed by fresh business-target and exact-order validation before evaluation.',
);

const yuumaBeverageDelivery = namedMethodSource(yuumaSettlement, 'TryDeliverYuumaOrderBeverage');
assert.doesNotMatch(
  yuumaBeverageDelivery,
  /ShouldPlayerThrowDeliver/,
  'The player ThrowDeliver buff capability must not block the dedicated headless beverage transaction.',
);
assert.match(
  yuumaBeverageDelivery,
  /TryDeliverYuumaOrderBeverage\(\s*CookingCollectionTarget target,\s*int beverageId,\s*string beverageName,\s*string orderLabel\)/,
  'The Yuuma beverage entry must accept only its stable target identity and item inputs.',
);
assert.doesNotMatch(
  yuumaBeverageDelivery,
  /TryDeliverYuumaOrderBeverage\([\s\S]*RuntimeOrderMatch\s+runtimeOrder/,
  'The Yuuma beverage entry must not trust a generic runtime-order wrapper supplied by its caller.',
);
const beverageRequestBuild = yuumaBeverageDelivery.indexOf('BuildOrderRequestFromCookingTarget(');
const beverageInitialLookup = yuumaBeverageDelivery.indexOf(
  'FindYuumaRuntimeOrder(target, request)',
  beverageRequestBuild,
);
const beverageInitialStateRead = yuumaBeverageDelivery.indexOf(
  'TryReadYuumaOrderDeliveryState(',
  beverageInitialLookup,
);
const beverageInAirGate = yuumaBeverageDelivery.indexOf(
  'if (beverageInAir != null)',
  beverageInitialStateRead,
);
const foodInAirGate = yuumaBeverageDelivery.indexOf(
  'if (servedFood != null || foodInAir != null)',
  beverageInAirGate,
);
const beverageQuantityRead = yuumaBeverageDelivery.indexOf(
  'GetBeverageQuantity(beverageId)',
  foodInAirGate,
);
const beverageStoragePreflight = yuumaBeverageDelivery.indexOf(
  'TryCreateYuumaBeverageStorageContext(',
);
const beverageOut = yuumaBeverageDelivery.indexOf('storageContext.BeverageOutMethod.Invoke(');
const deductedTargetValidation = yuumaBeverageDelivery.indexOf(
  'TryValidateCurrentYuumaTarget(target, out var deductedTargetDiagnostic)',
  beverageOut,
);
const deductedOrderLookup = yuumaBeverageDelivery.indexOf(
  'var deductedOrder = FindYuumaRuntimeOrder(target, request)',
  deductedTargetValidation,
);
const deductedOrderValidation = yuumaBeverageDelivery.indexOf(
  'TryValidateReacquiredYuumaBeverageOrder(',
  deductedOrderLookup,
);
const freshSetterResolution = yuumaBeverageDelivery.indexOf(
  'TryResolveYuumaFinalSetter(',
  deductedOrderValidation,
);
const beverageSetter = yuumaBeverageDelivery.indexOf(
  'freshFinalBeverageSetter.Invoke(deductedOrder.Order, new[] { sellable })',
  freshSetterResolution,
);
const committedTargetValidation = yuumaBeverageDelivery.indexOf(
  'TryValidateCurrentYuumaTarget(target, out var committedTargetDiagnostic)',
  beverageSetter,
);
const committedOrderLookup = yuumaBeverageDelivery.indexOf(
  'var committedOrder = FindYuumaRuntimeOrder(target, request)',
  committedTargetValidation,
);
const committedOrderValidation = yuumaBeverageDelivery.indexOf(
  'TryValidateReacquiredYuumaBeverageOrder(',
  committedOrderLookup,
);
const beveragePatientRecovery = yuumaBeverageDelivery.indexOf(
  'TryRecoverPatientAfterPartialDelivery(',
  committedOrderValidation,
);
const recoveredTargetValidation = yuumaBeverageDelivery.indexOf(
  'TryValidateCurrentYuumaTarget(target, out var recoveredTargetDiagnostic)',
  beveragePatientRecovery,
);
const recoveredOrderLookup = yuumaBeverageDelivery.indexOf(
  'var recoveredOrder = FindYuumaRuntimeOrder(target, request)',
  recoveredTargetValidation,
);
const recoveredOrderValidation = yuumaBeverageDelivery.indexOf(
  'TryValidateReacquiredYuumaBeverageOrder(',
  recoveredOrderLookup,
);
const beverageRangeAdjustment = yuumaBeverageDelivery.indexOf(
  'ApplyYuumaBeverageCostPolicy(',
  recoveredOrderValidation,
);
const adjustedTargetValidation = yuumaBeverageDelivery.indexOf(
  'TryValidateCurrentYuumaTarget(target, out var adjustedTargetDiagnostic)',
  beverageRangeAdjustment,
);
const adjustedOrderLookup = yuumaBeverageDelivery.indexOf(
  'var adjustedOrder = FindYuumaRuntimeOrder(target, request)',
  adjustedTargetValidation,
);
const adjustedOrderValidation = yuumaBeverageDelivery.indexOf(
  'TryValidateReacquiredYuumaBeverageOrder(',
  adjustedOrderLookup,
);
const freshBookkeepingContext = yuumaBeverageDelivery.indexOf(
  'TryCreateYuumaBookkeepingContext(',
  adjustedOrderValidation,
);
const beverageBookkeeping = yuumaBeverageDelivery.indexOf(
  'TryApplyYuumaDeliveryBookkeeping(freshBookkeepingContext,',
  freshBookkeepingContext,
);
assert.ok(
  beverageRequestBuild >= 0
    && beverageInitialLookup > beverageRequestBuild
    && beverageInitialStateRead > beverageInitialLookup
    && beverageInAirGate > beverageInitialStateRead
    && foodInAirGate > beverageInAirGate
    && beverageQuantityRead > foodInAirGate
    && beverageStoragePreflight > beverageQuantityRead
    && beverageOut > beverageStoragePreflight
    && deductedTargetValidation > beverageOut
    && deductedOrderLookup > deductedTargetValidation
    && deductedOrderValidation > deductedOrderLookup
    && freshSetterResolution > deductedOrderValidation
    && beverageSetter > freshSetterResolution
    && committedTargetValidation > beverageSetter
    && committedOrderLookup > committedTargetValidation
    && committedOrderValidation > committedOrderLookup
    && beveragePatientRecovery > committedOrderValidation
    && recoveredTargetValidation > beveragePatientRecovery
    && recoveredOrderLookup > recoveredTargetValidation
    && recoveredOrderValidation > recoveredOrderLookup
    && beverageRangeAdjustment > recoveredOrderValidation
    && adjustedTargetValidation > beverageRangeAdjustment
    && adjustedOrderLookup > adjustedTargetValidation
    && adjustedOrderValidation > adjustedOrderLookup
    && freshBookkeepingContext > adjustedOrderValidation
    && beverageBookkeeping > freshBookkeepingContext,
  'Each irreversible Yuuma beverage step, including patient recovery, must be followed by target/revision validation and a fresh exact-order lookup before bookkeeping.',
);
assert.match(
  yuumaBeverageDelivery.slice(beverageInAirGate, foodInAirGate),
  /OrderPreparationStepCodes\.CookingPending/,
  'A native BeverageInAir must stop the shared rare/normal Yuuma beverage entry as a retryable CookingPending state.',
);
assert.match(
  yuumaBeverageDelivery.slice(foodInAirGate, beverageQuantityRead),
  /OrderPreparationStepCodes\.CookingPending/,
  'A native FoodInAir must stop the Yuuma beverage transaction before inventory can be consumed.',
);
assert.doesNotMatch(
  yuumaBeverageDelivery.slice(beverageInitialLookup, beverageQuantityRead),
  /BeverageOutMethod\.Invoke|freshFinalBeverageSetter\.Invoke|ApplyYuumaBeverageCostPolicy|TryRecoverPatientAfterPartialDelivery|TryApplyYuumaDeliveryBookkeeping/,
  'The shared rare/normal in-air preflight performs an irreversible side effect before rejecting the transaction.',
);
const afterBeverageOut = yuumaBeverageDelivery.slice(beverageOut);
assert.doesNotMatch(
  afterBeverageOut,
  /runtimeOrder\.Order|runtimeOrder\.Controller|runtimeOrder\.Manager/,
  'The preflight runtime-order wrapper must never cross the irreversible BeverageOut boundary.',
);
assert.match(
  yuumaBeverageDelivery.slice(deductedOrderValidation, beverageSetter),
  /expectCommitted: false[\s\S]*freshFinalBeverageSetter/,
  'The post-BeverageOut lookup must prove the order is still uncommitted before resolving a fresh setter.',
);
assert.match(
  yuumaBeverageDelivery.slice(committedOrderValidation, recoveredTargetValidation),
  /expectCommitted: true[\s\S]*TryRecoverPatientAfterPartialDelivery\([\s\S]*committedOrder,[\s\S]*deliveredItemCount: 1/,
  'The post-setter lookup must prove the exact committed order before one-item patient recovery.',
);
assert.match(
  yuumaBeverageDelivery.slice(recoveredOrderValidation, beverageRangeAdjustment),
  /expectCommitted: true/,
  'The post-recovery lookup must prove the exact committed order before range adjustment.',
);
assert.match(
  yuumaBeverageDelivery.slice(adjustedOrderValidation, beverageBookkeeping),
  /expectCommitted: true[\s\S]*adjustedOrder[\s\S]*freshBookkeepingContext/,
  'The post-range lookup must prove the exact committed order before building fresh bookkeeping.',
);
assert.doesNotMatch(
  yuumaBeverageDelivery.slice(beveragePatientRecovery, beverageBookkeeping),
  /committedOrder\.(?:Order|Controller|Manager)/,
  'The pre-recovery order wrapper must not cross the patient-recovery callback boundary.',
);
assert.doesNotMatch(
  yuumaBeverageDelivery.slice(beverageRangeAdjustment, beverageBookkeeping),
  /recoveredOrder\.(?:Order|Controller|Manager)/,
  'The pre-range order wrapper must not cross the inventory callback boundary.',
);
const beverageReacquireValidation = namedMethodSource(
  yuumaSettlement,
  'TryValidateReacquiredYuumaBeverageOrder',
);
assert.match(
  beverageReacquireValidation,
  /TryReadYuumaOrderDeliveryState\([\s\S]*out var beverageInAir[\s\S]*if \(beverageInAir != null\)[\s\S]*return false;/,
  'Every fresh Yuuma beverage order must reject a native BeverageInAir before another transaction step.',
);
const reacquiredBeverageInAirGate = beverageReacquireValidation.indexOf(
  'if (beverageInAir != null)',
);
const reacquiredFoodGate = beverageReacquireValidation.indexOf(
  'if (servedFood != null || foodInAir != null)',
);
const reacquiredCommitGate = beverageReacquireValidation.indexOf(
  'if (!expectCommitted && servedBeverage != null)',
);
assert.ok(
  reacquiredBeverageInAirGate >= 0
    && reacquiredFoodGate > reacquiredBeverageInAirGate
    && reacquiredCommitGate > reacquiredBeverageInAirGate,
  'The fresh-order BeverageInAir gate must run before food and committed-beverage state validation.',
);
assert.equal(
  [...yuumaBeverageDelivery.matchAll(/TryValidateReacquiredYuumaBeverageOrder\(/g)].length,
  4,
  'All four fresh Yuuma beverage reacquisitions must pass the BeverageInAir validator.',
);
assert.match(
  beverageReacquireValidation,
  /!expectCommitted && servedBeverage != null[\s\S]*expectCommitted[\s\S]*CompareObjectIdentity\(servedBeverage, deliveredBeverage\)[\s\S]*RuntimeObjectIdentityComparison\.Same/,
  'The beverage reacquire validator must distinguish uncommitted state from the exact committed item.',
);
const patientRecovery = namedMethodSource(
  delivery,
  'TryRecoverPatientAfterPartialDelivery',
);
const manualControlledSkip = patientRecovery.indexOf('IsManualControlledOrder(');
const patientBoundsRead = patientRecovery.indexOf('TryReadPatientBounds(');
const patientMutation = patientRecovery.indexOf('TryInvokeInstance(', patientBoundsRead);
assert.ok(
  manualControlledSkip >= 0
    && patientBoundsRead > manualControlledSkip
    && patientMutation > patientBoundsRead,
  'Manual-controlled orders must return from the shared recovery helper before patient reads or mutations.',
);
assert.match(
  patientRecovery.slice(manualControlledSkip, patientBoundsRead),
  /message = "";[\s\S]*return true;/,
  'The manual-controlled patient-recovery path must remain an explicit successful no-op.',
);
assert.equal(
  [...yuumaBeverageDelivery.matchAll(/currentQuantity > 0/g)].length,
  1,
  'Infinite beverage stock must still execute the native inventory sequence; only finite sufficiency may branch on currentQuantity > 0.',
);
assert.match(yuumaBeverageDelivery, /currentQuantity < 0[\s\S]*\? "无限库存"/,
  'Infinite beverage stock must retain its native -1 result display.');
assert.match(
  yuumaBeverageDelivery,
  /currentQuantity - \(isFreeBeverage \? 0 : extraCostBeverages\)/,
  'Finite beverage result text must show zero net cost for free service and the full extra-cost quantity otherwise.',
);

const yuumaBeverageCostPolicy = namedMethodSource(yuumaSettlement, 'ApplyYuumaBeverageCostPolicy');
assert.match(
  yuumaBeverageCostPolicy,
  /if \(isFreeBeverage\)[\s\S]*InvokeExactRuntimeStorageRange\([\s\S]*storageContext\.BeverageInRangeMethod,[\s\S]*beverageId,[\s\S]*1\)[\s\S]*return/,
  'Free beverage service must reverse the base BeverageOut exactly once.',
);
assert.match(
  yuumaBeverageCostPolicy,
  /var additionalCost = extraCostBeverages - 1[\s\S]*additionalCost > 0[\s\S]*InvokeExactRuntimeStorageRange\([\s\S]*storageContext\.BeverageOutRangeMethod,[\s\S]*beverageId,[\s\S]*additionalCost\)/,
  'Extra-cost beverage service must apply the remaining native range cost after the base BeverageOut.',
);

const yuumaBeverageStoragePreflight = namedMethodSource(
  yuumaSettlement,
  'TryCreateYuumaBeverageStorageContext',
);
assert.match(
  yuumaBeverageStoragePreflight,
  /"BeverageOut"[\s\S]*method\.ReturnType == typeof\(void\)[\s\S]*parameters\.Length == 2[\s\S]*parameters\[0\]\.ParameterType == typeof\(int\)[\s\S]*parameters\[1\]\.ParameterType == typeof\(bool\)/,
  'Yuuma beverage storage must uniquely preflight BeverageOut(int,bool).',
);
assert.match(
  yuumaBeverageStoragePreflight,
  /FindYuumaBeverageRangeMethods\([\s\S]*"BeverageInRange"[\s\S]*FindYuumaBeverageRangeMethods\([\s\S]*"BeverageOutRange"/,
  'Yuuma beverage storage must preflight both exact range methods before any write.',
);
assert.match(
  yuumaBeverageStoragePreflight,
  /beverageOutMethods\.Length != 1[\s\S]*beverageInRangeMethods\.Length != 1[\s\S]*beverageOutRangeMethods\.Length != 1/,
  'Yuuma beverage storage must fail closed unless all three native entry shapes are unique.',
);
const yuumaBeverageRangeResolver = namedMethodSource(
  yuumaSettlement,
  'FindYuumaBeverageRangeMethods',
);
assert.match(
  yuumaBeverageRangeResolver,
  /parameters\[0\]\.ParameterType[\s\S]*typeof\(Il2CppSystem\.Collections\.Generic\.IEnumerable<int>\)[\s\S]*parameters\[1\]\.ParameterType == typeof\(bool\)/,
  'Yuuma range storage must resolve only the exact IEnumerable<int>,bool signature.',
);
const yuumaBeverageRangeInvoke = namedMethodSource(
  yuumaSettlement,
  'InvokeExactRuntimeStorageRange',
);
assert.match(
  yuumaBeverageRangeInvoke,
  /new Il2CppStructArray<int>\(count\)[\s\S]*Cast<Il2CppSystem\.Collections\.Generic\.IEnumerable<int>>\(\)[\s\S]*method\.Invoke\(null, new object\?\[\] \{ enumerable, false \}\)/,
  'Yuuma range storage must build the exact IL2CPP array and invoke the cached MethodInfo.',
);

const yuumaTagReader = namedMethodSource(yuumaSettlement, 'TryReadExactSellableTagIds');
assert.match(
  yuumaTagReader,
  /FindExactInstanceMethod\([\s\S]*"get_Tags",[\s\S]*0,[\s\S]*typeof\(Il2CppStructArray<int>\)\)/,
  'Yuuma item Tags getter must declare the exact Il2CppStructArray<int> return type before invocation.',
);
assert.match(yuumaTagReader, /rawTags is Il2CppStructArray<int> il2CppTags/,
  'Yuuma item Tags must accept only the BepInEx 783 Il2CppStructArray<int> container.');
assert.match(yuumaTagReader, /get_Tags\(\) 返回未验证的容器/,
  'Yuuma item Tags must fail closed on an unverified container shape.');
assert.doesNotMatch(
  yuumaTagReader,
  /rawTags is (?:int\[\]|IEnumerable)|EnumerateIl2Cpp|TryReadIntSequence/,
  'Yuuma item Tags restored an unsupported array/enumerable compatibility fallback.',
);

const normalYuumaCookingJob = namedMethodSource(cooking, 'TryProcessNormalOrderCookingJob');
const normalYuumaTerminal = normalYuumaCookingJob.indexOf(
  'if (result.Remove && IsYuumaBossTarget(job.Target))',
);
const normalServedFoodFallback = normalYuumaCookingJob.indexOf('ReadOrderServedFood(order)');
assert.ok(normalYuumaTerminal >= 0 && normalServedFoodFallback > normalYuumaTerminal,
  'A terminal normal-order Yuuma job must return before reading the potentially invalidated order wrapper\'s served food.');

const yuumaBookkeepingPreflight = namedMethodSource(
  yuumaSettlement,
  'TryCreateYuumaBookkeepingContext',
);
assert.match(
  yuumaBookkeepingPreflight,
  /parameters\[0\]\.ParameterType[\s\S]*typeof\(Il2CppSystem\.Collections\.Generic\.IEnumerable<int>\)/,
  'Yuuma consume bookkeeping must require the exact generic IEnumerable<int> parameter.',
);
assert.match(
  yuumaBookkeepingPreflight,
  /parameters\[0\]\.ParameterType\.FullName[\s\S]*YuumaOrderBaseTypeName[\s\S]*parameters\[0\]\.ParameterType\.IsInstanceOfType\(runtimeOrder\.Order\)[\s\S]*parameters\[1\]\.ParameterType\.FullName[\s\S]*YuumaOrderChangeContextTypeName[\s\S]*parameters\[2\]\.ParameterType == typeof\(int\)/,
  'Yuuma Partner status bookkeeping must require exact OrderBase, OrderChangeContext, and int parameter identities.',
);

const yuumaBookkeeping = namedMethodSource(yuumaSettlement, 'TryApplyYuumaDeliveryBookkeeping');
assert.match(
  yuumaBookkeeping,
  /TryApplyYuumaDeliveryBookkeeping\(\s*YuumaDeliveryBookkeepingContext context,\s*out string diagnostic\)/,
  'Post-evaluation bookkeeping accepts live order/item inputs instead of only its cached context.',
);
const consumeUpdate = yuumaBookkeeping.indexOf('AddBussinessFoodConsumes');
const statusUpdate = yuumaBookkeeping.indexOf('OnOrderBaseStatusUpdate');
const deskUpdate = yuumaBookkeeping.indexOf('TryAddPlayerOccupiedDeskCode');
const consumeInvoke = yuumaBookkeeping.indexOf('context.ConsumeMethod.Invoke');
const statusInvoke = yuumaBookkeeping.indexOf('context.StatusMethod.Invoke');
const deskInvoke = yuumaBookkeeping.indexOf('context.DeskMethod.Invoke');
assert.ok(consumeUpdate >= 0 && statusUpdate > consumeUpdate && deskUpdate > statusUpdate,
  'Yuuma bookkeeping must mirror native consume -> FoodDelivered -> occupied-desk order.');
assert.ok(consumeInvoke >= 0 && statusInvoke > consumeInvoke && deskInvoke > statusInvoke,
  'Yuuma bookkeeping invocation order must remain consume -> status -> occupied desk.');
assert.match(yuumaBookkeeping, /FoodDelivered/);
assert.doesNotMatch(
  yuumaBookkeeping,
  /RuntimeOrderMatch|deliveredItem|FindExact|GetType\(|ReadMember|ReadSellable|get_DeskCode|TryReadNativeObjectPointer|FindYuumaRuntimeOrder/,
  'Post-evaluation bookkeeping must consume only the opaque context cached before native evaluation.',
);

assert.match(yuumaSettlementTracker, /Ready[\s\S]*Attempting[\s\S]*Committed[\s\S]*Uncertain/,
  'Yuuma settlement tracker must expose monotonic irreversible states.');
assert.match(yuumaSettlementTracker, /TryBegin/,
  'Yuuma settlement tracker must atomically claim an attempt.');
assert.match(yuumaSettlementTracker, /MarkUncertain/,
  'Yuuma settlement tracker must permanently quarantine an uncertain native call.');
const uncertainYuumaSettlement = namedMethodSource(yuumaSettlement, 'BlockUncertainYuumaSettlement');
assert.match(yuumaFinalization, /MarkUncertain/,
  'An irreversible Yuuma exception must latch the transaction as uncertain.');
assert.match(uncertainYuumaSettlement, /OrderEvaluationCommitUncertain[\s\S]*yuuma-settlement-uncertain[\s\S]*terminal: true/,
  'An irreversible Yuuma exception must become an acknowledged non-replay barrier.');

assert.doesNotMatch(
  `${yuumaSettlement}\n${yuumaSettlementTracker}`,
  /WorkSceneServePannel|WorkSceneThrowDeliverPanel|OpenThrowDeliverPanel|OnThrowDelivering|ExecuteThrowDeliver|ThrowDeliver\(|ShowOrder|ShowManualOrder|FinishOrderStatus|InvokeOrderUpdate|DisplayClass|MoveNext|CookController\.Extract|YuumaFinalizationTransactionGate|YuumaOrderSettlementCoordinator|YuumaSettlementProgressState|TryClaimYuumaSettlement/,
  'Yuuma settlement restored a UI/generated callback or the removed oversized transaction path.',
);

const manualHandoffReceipt = methodSource(
  cooking,
  'private static (bool Remove, string Message, string Code) TryProcessManualHandoffReceipt(',
);
assert.match(
  manualHandoffReceipt,
  /TryDetectSpecialFoodTargetPolicyChanged\([\s\S]*CookingManualHandoffExpired[\s\S]*terminal: false[\s\S]*FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.Completion\)/,
  'A rotated Blood Pond Hell target does not retain its stale handoff receipt before order polling.',
);
assert.match(
  manualHandoffReceipt,
  /ManualHandoffMissingOrderCount < MissingTargetRetireAttempts[\s\S]*ManualHandoffMissingOrderClock\.Elapsed < MissingTargetRetireDelay[\s\S]*TryReadOrderServedItem\([\s\S]*RuntimeDeliveryItemKind\.Food[\s\S]*if \(servedFood == null\)[\s\S]*CookingManualHandoffCompleted/,
  'Manual handoff receipt retirement no longer requires bounded missing-order or exact final-food evidence.',
);
assert.match(
  manualHandoffReceipt,
  /HandleManualHandoffReadFailure\([\s\S]*CookingPending/,
  'Manual handoff read failures no longer remain side-effect-free and bounded.',
);

for (const removedSettlementFile of [
  'mods/bepinex/src/Save/SpecialBusiness/YuumaFinalizationTransactionGate.cs',
  'mods/bepinex/src/Save/YuumaOrderSettlementCoordinator.cs',
  'mods/bepinex/src/Save/YuumaSettlementProgressState.cs',
]) {
  assert.equal(
    fs.existsSync(path.join(root, removedSettlementFile)),
    false,
    `Removed Blood Pond Hell finalization file was restored: ${removedSettlementFile}`,
  );
}

assert.match(
  orderPreparationModels,
  /public long SpecialTargetRevision \{ get; init; \}/,
  'The order request must expose the exact Blood Pond Hell target revision.',
);
assert.match(
  localApiServer,
  /SpecialTargetRevision = ReadLongQuery\(query, "specialTargetRevision", 0\)/,
  'The Local API parser must read target revision as a long and default non-Yuuma requests to zero.',
);
const frontendWirePolicy = sourceSlice(
  frontendTypes,
  'export interface SpecialFoodTargetWirePolicy',
  'export interface NormalOrderExecutionTarget',
);
assert.match(
  frontendWirePolicy,
  /specialTargetRevision: number/,
  'The frontend wire contract must carry the target revision.',
);
const createWirePolicy = methodSource(
  frontendTargetPolicy,
  'export function createSpecialFoodTargetWirePolicy(',
);
assert.match(
  createWirePolicy,
  /const yuumaRevision = specialBusiness\?\.yuumaFoodTargetRevision[\s\S]*owner === 'yuuma'[\s\S]*typeof yuumaRevision === 'number'[\s\S]*Number\.isSafeInteger\(yuumaRevision\)[\s\S]*yuumaRevision > 0[\s\S]*specialTargetRevision: revision/,
  'Blood Pond Hell must publish only a positive, safe runtime revision into the wire policy.',
);
const emptyWirePolicy = methodSource(
  frontendTargetPolicy,
  'export function emptySpecialFoodTargetWirePolicy(',
);
assert.match(
  emptyWirePolicy,
  /specialTargetRevision: 0/,
  'Ordinary and non-revision special-business requests must use revision zero.',
);
assert.equal(
  [...frontendApi.matchAll(/specialTargetRevision: String\(specialTargetPolicy\.specialTargetRevision\)/g)].length,
  2,
  'Rare and normal order actions must both serialize the exact wire-policy revision.',
);

const rareTargetFactory = methodSource(
  service,
  'public static CookingCollectionTarget ForRareOrder(',
);
assert.match(
  rareTargetFactory,
  /SpecialFoodTargetRevision = request\.SpecialTargetRevision/,
  'Rare-order targets must retain the request revision.',
);
const normalTargetFactory = methodSource(
  service,
  'public static CookingCollectionTarget ForNormalOrder(',
);
assert.match(
  normalTargetFactory,
  /SpecialFoodTargetRevision = specialFoodTargetRevision/,
  'Normal-order targets must retain the request revision.',
);
const syntheticOrderRequest = methodSource(
  directDelivery,
  'private static OrderPreparationRequest BuildOrderRequestFromCookingTarget(',
);
assert.match(
  syntheticOrderRequest,
  /SpecialTargetRevision = target\.SpecialFoodTargetRevision/,
  'Fresh runtime-order lookups must reconstruct the exact target revision.',
);
const cookingJobSource = sourceSlice(
  service,
  'private sealed class AutomationCookingJob',
  'private sealed class CookingCollectionTarget',
);
assert.match(
  cookingJobSource,
  /public long SpecialFoodTargetRevision \{ get; init; \}/,
  'The automation job must latch the exact revision captured before cooking side effects.',
);
assert.match(
  cookingJobSource,
  /public RuntimeCookerReservation CookerReservation \{ get; init; \}/,
  'The automation job must retain the exact managed cooker reservation.',
);
assert.doesNotMatch(
  cookingJobSource,
  /object CookController/,
  'The automation job must not retain an IL2CPP cooker wrapper.',
);
assert.match(
  cookingJobSource,
  /SpecialTargetRevision = SpecialFoodTargetRevision/,
  'The job snapshot must publish the latched revision.',
);
assert.match(
  localApiModels,
  /class AutomationCookingJobSnapshot[\s\S]*public long SpecialTargetRevision \{ get; init; \}/,
  'The Local API job snapshot schema must retain target revision.',
);
const revisionAwareTargetMatch = methodSource(
  cooking,
  'private static bool IsSameCookingCollectionTarget(',
);
assert.match(
  revisionAwareTargetMatch,
  /left\.SpecialFoodTargetRevision != right\.SpecialFoodTargetRevision[\s\S]*return false/,
  'Cooking jobs from different target revisions must never be reused as the same target.',
);

const requestedTargetValidation = methodSource(
  specialTargetPolicy,
  'private static bool TryValidateRequestedSpecialFoodTargetPolicy(',
);
assert.match(
  requestedTargetValidation,
  /request\.SpecialTargetRevision <= 0[\s\S]*request\.SpecialTargetRevision != activeYuumaRevision/,
  'A Yuuma request must carry a positive revision exactly equal to the current runtime revision.',
);
assert.match(
  requestedTargetValidation,
  /request\.SpecialTargetRevision != 0[\s\S]*非血池地狱特殊料理目标不能携带 target revision/,
  'Ordinary and Yuyuko paths must remain isolated from the Yuuma revision protocol.',
);
const currentTargetValidation = methodSource(
  specialTargetPolicy,
  'private static bool TryValidateCurrentSpecialFoodTargetPolicy(',
);
assert.match(
  currentTargetValidation,
  /target\.SpecialFoodTargetRevision <= 0[\s\S]*target\.SpecialFoodTargetRevision != currentRevision/,
  'A running Yuuma target must still match the exact current revision.',
);
assert.match(
  currentTargetValidation,
  /target\.SpecialFoodTargetRevision != 0[\s\S]*非血池地狱自动料理目标不能携带 target revision/,
  'Non-Yuuma cooking targets must keep revision zero.',
);
const revisionCapture = methodSource(
  specialTargetPolicy,
  'private static bool TryCaptureYuumaFoodTargetRevision(',
);
assert.match(
  revisionCapture,
  /expectedRevision <= 0[\s\S]*expectedRevision != currentRevision[\s\S]*revision = expectedRevision/,
  'Job registration must capture only the exact positive current revision.',
);
const revisionUpdate = methodSource(
  specialBusinessContext,
  'private static void UpdateYuumaFoodTarget(',
);
assert.match(
  revisionUpdate,
  /!string\.Equals\([\s\S]*_yuumaFoodTargetIdentity,[\s\S]*identity,[\s\S]*StringComparison\.Ordinal\)[\s\S]*_yuumaFoodTargetRevision\+\+[\s\S]*_yuumaFoodTargetIdentity = identity/,
  'Every complete A -> B or B -> A identity transition must advance the monotonic revision.',
);
const currentYuumaTargetValidation = namedMethodSource(
  yuumaSettlement,
  'TryValidateCurrentYuumaTarget',
);
assert.match(
  currentYuumaTargetValidation,
  /target\.SpecialFoodTargetRevision <= 0[\s\S]*!expectedPolicy\.HasSameIdentity\(currentPolicy\)[\s\S]*target\.SpecialFoodTargetRevision != currentRevision/,
  'A stale A revision must be rejected after A -> B -> A even when policy identity returns to A.',
);

const rarePrepare = methodSource(
  service,
  'public static OrderPreparationResult Prepare(',
);
assert.match(
  rarePrepare,
  /var yuumaRequest = IsYuumaBossRequest\(request\)[\s\S]*runtimeOrderCache \?\?= yuumaRequest[\s\S]*FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.YuumaSettlement\)[\s\S]*: FindRuntimeOrder\(request\)/,
  'Rare prepare must use the strict Yuuma settlement lookup while ordinary and Yuyuko requests retain their original lookup.',
);
const rarePrepareLookup = rarePrepare.indexOf('RuntimeOrderMatch GetRuntimeOrder()');
assert.ok(
  rarePrepareLookup >= 0
    && rarePrepareLookup < rarePrepare.indexOf('if (request.AutoTakeBeverage)')
    && rarePrepareLookup < rarePrepare.indexOf('if (request.AutoStartCooking)'),
  'Rare prepare must select strict Yuuma lookup before beverage state reads or cooking side effects.',
);
assert.match(
  rarePrepare,
  /existingBeverage != null[\s\S]*yuumaRequest[\s\S]*TryValidateYuumaDeliveredItemAgainstOriginalOrder\([\s\S]*RuntimeDeliveryItemKind\.Beverage/,
  'Rare prepare must revalidate an outer-layer existing Yuuma beverage against the original Tag identity.',
);

const rareComplete = methodSource(
  service,
  'public static OrderPreparationResult CompleteFirst(',
);
assert.match(
  rareComplete,
  /IsYuumaBossRequest\(request\)[\s\S]*FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.YuumaSettlement\)[\s\S]*: FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.Completion\)/,
  'Rare completion must reserve settlement lookup for Yuuma and retain Completion lookup for ordinary and Yuyuko orders.',
);
assert.match(
  rareComplete,
  /currentBeverage != null[\s\S]*TryValidateYuumaDeliveredItemAgainstOriginalOrder\([\s\S]*RuntimeDeliveryItemKind\.Beverage/,
  'Rare completion must revalidate an existing Yuuma beverage against the original Tag identity.',
);

const normalComplete = methodSource(
  service,
  'public static OrderPreparationResult CompleteNormalFirst(',
);
assert.match(
  normalComplete,
  /var yuumaSettlement = IsYuumaBossRequest\(request\)[\s\S]*yuumaSettlement[\s\S]*FindRuntimeNormalOrder\(request, RuntimeOrderLookupPurpose\.YuumaSettlement\)[\s\S]*: FindRuntimeNormalOrder\(request\)/,
  'Normal completion must reserve settlement lookup for Yuuma and retain the original lookup for ordinary and Yuyuko orders.',
);
assert.match(
  normalComplete,
  /servedBeverage != null[\s\S]*TryValidateYuumaDeliveredItemAgainstOriginalOrder\([\s\S]*RuntimeDeliveryItemKind\.Beverage/,
  'Normal completion must revalidate an existing Yuuma beverage against the original item ID.',
);
const normalJobResult = normalComplete.indexOf('var cookingJobResult = autoDeliverFood');
const normalJobCompleted = normalComplete.indexOf(
  'if (cookingJobResult.CompletedOrder)',
  normalJobResult,
);
const normalCompletedOrder = normalComplete.indexOf(
  'result.CompletedOrder = true',
  normalJobCompleted,
);
const normalCompletedImmediateFinish = normalComplete.indexOf(
  'return Finish(result)',
  normalCompletedOrder,
);
const normalJobDelivered = normalComplete.indexOf(
  'else if (cookingJobResult.Delivered)',
  normalCompletedImmediateFinish,
);
const normalYuumaCompletion = normalComplete.indexOf(
  'if (yuumaSettlement)',
  normalJobDelivered,
);
const normalYuumaCompletedOrder = normalComplete.indexOf(
  'result.CompletedOrder = true',
  normalYuumaCompletion,
);
const normalYuumaImmediateFinish = normalComplete.indexOf(
  'return Finish(result)',
  normalYuumaCompletedOrder,
);
const normalLegacyServedRead = normalComplete.indexOf(
  'result.ServedFood = ReadOrderServedFood(runtimeOrder.Order)',
  normalYuumaImmediateFinish,
);
assert.ok(
  normalJobResult >= 0
    && normalJobCompleted > normalJobResult
    && normalCompletedOrder > normalJobCompleted
    && normalCompletedImmediateFinish > normalCompletedOrder
    && normalJobDelivered > normalCompletedImmediateFinish
    && normalYuumaCompletion > normalJobDelivered
    && normalYuumaCompletedOrder > normalYuumaCompletion
    && normalYuumaImmediateFinish > normalYuumaCompletedOrder
    && normalLegacyServedRead > normalYuumaImmediateFinish,
  'A cooking job that completed evaluation, including Yuuma settlement, must immediately finish before stale served-item reads.',
);
const normalYuumaEvaluationGate = normalComplete.indexOf(
  'if (yuumaSettlement)',
  normalLegacyServedRead,
);
const normalYuyukoEvaluation = normalComplete.indexOf(
  'TryEvaluateYuyukoChallengeOrderIfReady(',
  normalYuumaEvaluationGate,
);
const normalGenericEvaluation = normalComplete.indexOf(
  'TryEvaluateOrderIfReady(',
  normalYuumaEvaluationGate,
);
assert.ok(
  normalYuumaEvaluationGate > normalLegacyServedRead
    && normalYuyukoEvaluation > normalYuumaEvaluationGate
    && normalGenericEvaluation > normalYuyukoEvaluation,
  'Normal evaluation must intercept Yuuma before the independent Yuyuko and generic evaluation branches.',
);
assert.doesNotMatch(
  normalComplete.slice(normalYuumaEvaluationGate, normalYuyukoEvaluation),
  /TryEvaluate(?:OrderIfReady|YuyukoChallengeOrderIfReady)/,
  'The Yuuma evaluation intercept must remain side-effect-free while no completed cooking transaction is available.',
);
const normalCookingJobProcessor = namedMethodSource(
  cooking,
  'TryProcessNormalOrderCookingJob',
);
const normalEvaluationCompletedResult = normalCookingJobProcessor.indexOf(
  'job.FoodDeliveryEvaluationState == AutomationFoodDeliveryEvaluationState.Completed',
);
const normalFoodDeliveredResult = normalCookingJobProcessor.indexOf(
  'result.Code == OrderPreparationStepCodes.FoodDelivered',
);
const normalYuumaRemovedResult = normalCookingJobProcessor.indexOf(
  'result.Remove && IsYuumaBossTarget(job.Target)',
  normalFoodDeliveredResult,
);
const normalLegacyJobServedRead = normalCookingJobProcessor.indexOf(
  'ReadOrderServedFood(order)',
  normalYuumaRemovedResult,
);
assert.ok(
  normalEvaluationCompletedResult >= 0
    && normalFoodDeliveredResult > normalEvaluationCompletedResult
    && normalYuumaRemovedResult > normalFoodDeliveredResult
    && normalLegacyJobServedRead > normalYuumaRemovedResult,
  'A normal cooking job must report completed evaluation before any legacy served-field fallback, while Yuuma still requires its exact settlement result.',
);

const directYuumaLookup = directFoodDelivery.indexOf(
  'var runtimeOrder = yuumaTarget',
);
const directYuumaStateRead = directFoodDelivery.indexOf(
  'TryReadYuumaOrderDeliveryState(',
  directYuumaLookup,
);
assert.match(
  directFoodDelivery,
  /var yuumaTarget = IsYuumaBossTarget\(target\)[\s\S]*yuumaTarget[\s\S]*FindYuumaRuntimeOrder\(target, request\)[\s\S]*FindRuntimeNormalOrder\(request\)[\s\S]*FindRuntimeOrder\(request\)/,
  'Cooking-job delivery must use settlement lookup only for Yuuma and preserve ordinary/Yuyuko lookup paths.',
);
assert.ok(
  directYuumaLookup >= 0
    && directYuumaStateRead > directYuumaLookup
    && directYuumaLookup < directFoodDelivery.indexOf('StoreCookedFoodForAlreadyHandledTarget(', directYuumaLookup)
    && directYuumaLookup < directFoodDelivery.indexOf('TryFinalizeYuumaCookingJob(', directYuumaLookup),
  'The strict Yuuma order must be acquired before order-state reads, release/store decisions, or final settlement.',
);

const manualHandoffLookup = manualHandoffReceipt.indexOf('FindYuumaRuntimeOrder(job.Target, request)');
assert.match(
  manualHandoffReceipt,
  /IsYuumaBossTarget\(job\.Target\)[\s\S]*FindYuumaRuntimeOrder\(job\.Target, request\)[\s\S]*FindRuntimeNormalOrder\(request\)[\s\S]*FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.Completion\)/,
  'Yuuma cooking-job handoff must use settlement lookup while ordinary and Yuyuko receipts retain their original purposes.',
);
assert.ok(
  manualHandoffLookup >= 0
    && manualHandoffLookup < manualHandoffReceipt.indexOf('if (runtimeOrder.Order == null)')
    && manualHandoffLookup < manualHandoffReceipt.indexOf('TryReadOrderServedItem('),
  'Yuuma handoff must acquire the strict order before deciding to release a receipt or reading delivered state.',
);

assert.equal(
  [...yuumaBeverageDelivery.matchAll(/TryValidateYuumaDeliveredItemAgainstOriginalOrder\(/g)].length,
  2,
  'Dedicated Yuuma beverage delivery must validate both an existing beverage and the new candidate against the original order.',
);
const deliveredItemIdentity = namedMethodSource(
  yuumaSettlement,
  'TryValidateYuumaDeliveredItemAgainstOriginalOrder',
);
assert.match(
  deliveredItemIdentity,
  /target\.Kind == CookingCollectionTargetKind\.NormalOrder[\s\S]*target\.MatchFoodId[\s\S]*target\.MatchBeverageId[\s\S]*actualId != expectedId/,
  'Normal Yuuma orders must validate delivered food/beverage by the exact original item ID.',
);
assert.match(
  deliveredItemIdentity,
  /target\.FoodTagId[\s\S]*target\.BeverageTagId[\s\S]*TryReadExactSellableTagIds[\s\S]*tagIds\.Contains\(expectedTagId\.Value\)/,
  'Rare Yuuma orders must validate delivered food/beverage by the exact original Tag ID.',
);

const startCooking = methodSource(cooking, 'private static CookingStartResult TryStartCooking(');
const registerCookingJob = methodSource(
  cooking,
  'private static AutomationCookingJob RegisterAutomationCookingJob(',
);
assert.doesNotMatch(
  startCooking.slice(0, startCooking.indexOf('{')),
  /autoCollect|autoCompleteOrder/,
  'Cooking start still carries obsolete creation-time delivery/completion switches.',
);
assert.match(
  registerCookingJob,
  /ApplyAutomationCookingJobControlDecision\([\s\S]*ObserveAutomationCookingJobControl\(/,
  'Cooking-job registration no longer publishes its initial current-control state.',
);
assert.match(cooking, /ManualHandoffObserved[\s\S]*TryProcessManualHandoffReceipt/);
assert.match(cooking, /TryGetUnresolvedAutomationSafetyBarrier\(job\.Target[\s\S]*job\.Tracker\.Suspend/);
assert.match(
  service,
  /TryBindCookingTargetRuntimeOrder\([\s\S]*actionTarget[\s\S]*TryApplyUnresolvedAutomationSafetyBarrier\(result, actionTarget\)/,
  'Rare preparation must bind the fresh exact order lifecycle before applying an unresolved barrier.',
);
assert.match(
  service,
  /TryBindCookingTargetRuntimeOrder\([\s\S]*automationTarget[\s\S]*TryApplyUnresolvedAutomationSafetyBarrier\(result, automationTarget\)/,
  'Rare completion must bind the fresh exact order lifecycle before applying an unresolved barrier.',
);
assert.match(
  service,
  /TryBindCookingTargetRuntimeOrder\([\s\S]*orderAutomationTarget[\s\S]*TryApplyUnresolvedAutomationSafetyBarrier\(result, orderAutomationTarget\)/,
  'Normal completion must bind the fresh exact order lifecycle before applying an unresolved barrier.',
);
assert.doesNotMatch(
  service,
  /TryApplyUnresolvedAutomationSafetyBarrier\(result,\s*"(?:rare|normal)"/,
  'Request trace/order-key barriers must not run before fresh lifecycle binding.',
);
const safetyBarrierIdentity = methodSource(
  service,
  'private static string BuildAutomationSafetyTargetIdentity(',
);
assert.match(
  safetyBarrierIdentity,
  /target\.OrderBinding[\s\S]*BusinessGeneration[\s\S]*OrderKind[\s\S]*OrderPointer[\s\S]*ControllerPointer[\s\S]*LifecycleSequence/,
  'Automation safety barriers must use the complete exact order lifecycle token.',
);
assert.doesNotMatch(
  safetyBarrierIdentity,
  /TraceId|OrderKey|\.Trim\(/,
  'Automation safety barriers must not retain request trace/order-key identity fallbacks.',
);
assert.match(
  service,
  /OrderRuntimeKind = target\.OrderBinding[\s\S]*OrderId = target\.OrderBinding[\s\S]*OrderControllerId = target\.OrderBinding[\s\S]*OrderLifecycleSequence = target\.OrderBinding/,
  'Published runtime events must expose the exact lifecycle identity used by backend barriers.',
);
assert.match(service, /AcknowledgeAutomationSafetyBarrier[\s\S]*AcknowledgedSequences/);
assert.match(service, /TryProcessAutomationCookingJob\(job, timeoutEligible\)[\s\S]*ReferenceEquals\(AutomationCookingJobs\[i\], job\)/);
assert.match(cooking, /TryProcessNormalOrderCookingJob[\s\S]*ReferenceEquals\(AutomationCookingJobs\[i\], job\)/);

const sharedCookerControllerReader =
  'RuntimeCookerReflection.TryReadCookerControllerEntriesFromCookSystem(';
for (const [label, source] of [
  ['cooking', cooking],
  ['snapshot', cookerSnapshot],
  ['highlight', cookerHighlight],
]) {
  assert.ok(source.includes(sharedCookerControllerReader),
    `${label} must consume the shared exact AllCookers controller reader.`);
}

const cookerTypeEntry = methodSource(
  cookerReflection,
  'public static bool TryReadCookerTypeIds(',
);
assert.match(
  cookerTypeEntry,
  /TryReadExactCookerTypeSequence\(/,
  'Cooker type consumers must require the complete AllAvailableCookerType sequence.',
);
assert.doesNotMatch(
  cookerTypeEntry,
  /get_Type|EnumerateObjects/,
  'The authoritative cooker type entry must not fall back to a base type or broad object enumeration.',
);
const exactCookerTypeReader = methodSource(
  cookerReflection,
  'private static bool TryReadExactCookerTypeSequence(',
);
for (const requiredToken of [
  '"get_AllAvailableCookerType"',
  'Il2CppEnumerableTypeName',
  'Il2CppGenericEnumeratorTypeName',
  '"get_Current"',
  'typeof(Il2CppSystem.Collections.IEnumerator)',
  'typeof(Il2CppSystem.IDisposable)',
  'moveNext.Invoke(',
  'getCurrent.Invoke(',
  'RuntimeCookerTypeSequenceReader.TryRead(',
  'dispose.Invoke(',
]) {
  assert.ok(
    exactCookerTypeReader.includes(requiredToken),
    `Exact cooker type wrapper enumeration is missing ${requiredToken}.`,
  );
}
for (const requiredToken of [
  'if (typeId == 0)',
  'observedEmpty = true;',
  'if (failure.Length == 0 && !observedAny)',
  '"cooker-types=sequence-empty"',
  'if (typeId is < 1 or > 5)',
  'dispose();',
  'MaxExceptionDiagnosticLength',
]) {
  assert.ok(
    cookerTypeSequenceReader.includes(requiredToken),
    `Bounded cooker type sequence behavior is missing ${requiredToken}.`,
  );
}
assert.doesNotMatch(
  `${exactCookerTypeReader}\n${cookerTypeSequenceReader}`,
  /get_Type|EnumerateObjects|ReadIntEnumerable|typeIds\.Count > 0/,
  'Complete cooker type reads must not retain base-type or broad enumerable fallbacks.',
);
const controllerStateReader = methodSource(
  cookerReflection,
  'public static bool TryReadCookerControllerState(',
);
for (const requiredToken of [
  '"get_IsEmptyDesk"',
  'RuntimeCookerTypeSequenceReader.TryValidateControllerState(',
  'IsEmptyDesk = isEmptyDesk',
]) {
  assert.ok(
    controllerStateReader.includes(requiredToken),
    `Exact empty-desk validation is missing ${requiredToken}.`,
  );
}
for (const requiredToken of [
  'isEmptyDesk && (!observedEmpty || capabilityCount != 0)',
  'isEmptyDesk && (phase != 0 || !resultEmpty || !chosenRecipeEmpty)',
  '!isEmptyDesk && capabilityCount == 0',
]) {
  assert.ok(
    cookerTypeSequenceReader.includes(requiredToken),
    `Exact empty-desk consistency validation is missing ${requiredToken}.`,
  );
}
assert.ok(
  cookerSnapshot.includes('RuntimeCookerReflection.TryReadCookerControllerState(')
    && cookerHighlight.includes('RuntimeCookerReflection.TryReadCookerControllerState(')
    && cooking.includes('RuntimeCookerReflection.TryReadCookerControllerState('),
  'Snapshot, highlight, and cooking must share the complete cooker type reader.',
);
const exactControllerStateReader = methodSource(
  cookerReflection,
  'public static bool TryReadCookerControllerState(',
);
for (const getter of [
  '"get_Cooker"',
  '"get_Phase"',
  '"get_Result"',
  '"get_ChosenRecipe"',
  '"get_CouldCookerOpen"',
]) {
  assert.ok(
    exactControllerStateReader.includes(getter),
    `Exact cooker availability is missing ${getter}.`,
  );
}
assert.doesNotMatch(
  cookerReflection,
  /public bool IsIdle/,
  'The shared native state must not expose an idle shortcut that can classify an empty desk as capacity.',
);
assert.match(
  cookerStartAvailabilityService,
  /state\.IsEmptyDesk \|\| state\.TypeIds\.Count == 0[\s\S]*AutomationCookerStartAvailability\.Unavailable/,
  'Exact empty desks and zero-capability controllers must remain unavailable before idle classification.',
);
assert.match(
  cookerStartPolicy,
  /if \(phase != 0 \|\| !resultEmpty \|\| !couldOpen\)[\s\S]*if \(chosenRecipeEmpty\)[\s\S]*StrictIdle[\s\S]*completedExtractObserved[\s\S]*ExtractedResidual[\s\S]*Unavailable/,
  'Cooker reuse must allow only strict idle or a completed Extract with residual recipe metadata.',
);
const reservedCookerSelection = methodSource(
  cooking,
  'string Message) TryGetCookerFromCookSystem(',
);
assert.ok(
  reservedCookerSelection.includes('RuntimeCookerReflection.GetCookSystemManager()')
    && reservedCookerSelection.includes('RuntimeCookerReflection.TryReadCookerControllerState('),
  'Reserved cooking must share the exact manager and full controller state reader.',
);
assert.doesNotMatch(
  reservedCookerSelection,
  /GetSingletonInstance\(CookSystemManagerTypeName\)|get_CouldCookerOpen|foreach\s*\(|selectedController|\?\?=\s*cookController/,
  'Reserved cooking must not scan for an alternate controller or treat CouldCookerOpen as complete availability.',
);
assert.match(
  reservedCookerSelection,
  /RuntimeCookerStartAvailabilityService\.Classify\([\s\S]*AutomationCookerStartAvailability\.Unavailable/,
  'Reserved cooker validation must use the shared exact start-availability classifier.',
);
assert.match(
  reservedCookerSelection,
  /TryReadLockedCookerPositions\([\s\S]*if \(!RuntimeCookerReflection\.TryReadCookerControllerEntriesFromCookSystem\(\s*cookSystem,\s*lockedPositions,[\s\S]*return \(false, true, null, null,[\s\S]*经营厨具来源暂时无法读取/,
  'AllCookers source failures must remain a local waiting outcome instead of proving the target cooker absent.',
);
assert.match(
  reservedCookerSelection,
  /TryReadLockedCookerPositions\([\s\S]*reservation\.TryMatch\([\s\S]*lockedPositions\.Contains\(reservation\.GridPosition\)[\s\S]*var cookController = controllerEntry\.Controller/,
  'Cooking must reject a locked exact index, identity, and grid position before touching its controller state.',
);
assert.match(
  reservedCookerSelection,
  /lockedPositions\.Contains\(reservation\.GridPosition\)[\s\S]*TryReadCookerControllerState\([\s\S]*reservation\.EvaluateChallengeGate\([\s\S]*RuntimeCookerChallengeGateState\.Inconsistent[\s\S]*controllerState\.TypeIds\.Contains\(recipeCookerType\)[\s\S]*不会改选其他厨具[\s\S]*IsCookControllerReserved\(cookController, out var reservationDiagnostic\)[\s\S]*RuntimeCookerStartAvailabilityService\.Classify/,
  'Only an unlocked exact controller may be checked for readability, open gate, type, Mod reservation, and shared availability.',
);
assert.doesNotMatch(
  cooking,
  /TryGetCookerForOrder/,
  'Cooking must not accept a second controller source outside the exact AllCookers dictionary.',
);
assert.match(
  reservedCookerSelection,
  /AutomationCookerStartAvailability\.Unavailable[\s\S]*return \(false, true, null,[\s\S]*不会改选其他厨具/,
  'A reserved controller that becomes natively unavailable must stay in a local waiting outcome.',
);
assert.match(
  orderPreparationModels,
  /public int CookerControllerIndex \{ get; init; \} = -1;[\s\S]*public string CookerControllerIdentity \{ get; init; \} = "";[\s\S]*public int\? CookerGridX[\s\S]*public int\? CookerGridY[\s\S]*public int\? CookerGridZ/,
  'The order request must carry the exact reserved cooker identity and coordinates.',
);
assert.match(
  localApiServer,
  /CookerControllerIndex = ReadIntQuery\(query, "cookerControllerIndex", -1\)[\s\S]*CookerControllerIdentity = ReadStringQuery\(query, "cookerControllerIdentity"\)[\s\S]*CookerGridX = ReadNullableIntQuery\(query, "cookerGridX"\)[\s\S]*CookerGridY = ReadNullableIntQuery\(query, "cookerGridY"\)[\s\S]*CookerGridZ = ReadNullableIntQuery\(query, "cookerGridZ"\)/,
  'The Local API parser must read every exact cooker reservation field without aliases.',
);
assert.match(
  service,
  /TryStartCooking\([\s\S]*request\.CookerControllerIndex[\s\S]*request\.CookerControllerIdentity[\s\S]*request\.CookerGridX[\s\S]*request\.CookerGridY[\s\S]*request\.CookerGridZ[\s\S]*TryStartCooking\([\s\S]*request\.CookerControllerIndex[\s\S]*request\.CookerControllerIdentity[\s\S]*request\.CookerGridX[\s\S]*request\.CookerGridY[\s\S]*request\.CookerGridZ/,
  'Rare and normal cooking starts must both forward the parsed reserved controller triple.',
);
assert.match(
  service,
  /CookingCookerWaiting = "cooking-cooker-waiting"[\s\S]*result\.Automation\.Outcome = "waiting"[\s\S]*result\.Automation\.RetryAfterMs = 1000/,
  'Cooker contention must not enter the retryable-failure budget.',
);
const cookerStart = methodSource(
  cooking,
  'private static CookingStartResult TryStartCooking(',
);
assert.match(
  cookerStart,
  /RuntimeCookerReservation\.TryCreate\([\s\S]*cookerControllerIndex[\s\S]*cookerControllerIdentity[\s\S]*cookerGridX[\s\S]*cookerGridY[\s\S]*cookerGridZ[\s\S]*TryGetCookerFromCookSystem\([\s\S]*recipeCookerType,[\s\S]*cookerReservation/,
  'Cooking must build one complete reservation triple before exact cooker selection.',
);
const cookerRevalidationIndex = cookerStart.indexOf('TryRevalidateCookerBeforeStart(');
const materialDeductionIndex = cookerStart.indexOf('InvokeRuntimeStorageOut("IngredientOut"');
const finalCookerRevalidationIndex = cookerStart.indexOf(
  'TryRevalidateCookerBeforeStart(',
  cookerRevalidationIndex + 1,
);
assert.ok(
  cookerRevalidationIndex >= 0
    && materialDeductionIndex >= 0
    && cookerRevalidationIndex < materialDeductionIndex
    && finalCookerRevalidationIndex > materialDeductionIndex,
  'The exact reservation must be fully revalidated before material deduction and again after it.',
);
const setCookIndex = cookerStart.indexOf('InvokeInstance(cookController, "SetCook"');
assert.ok(
  setCookIndex > finalCookerRevalidationIndex,
  'The final exact identity/grid/lock validation must immediately precede SetCook.',
);
assert.match(
  cookerStart.slice(finalCookerRevalidationIndex, setCookIndex),
  /BlockCookingStartUnowned\([\s\S]*Mod 未调用 SetCook/,
  'Post-deduction reservation drift must stop before SetCook behind an authoritative safety barrier.',
);
const immediateOwnershipIndex = cookerStart.indexOf(
  'RuntimeCookingGenerationTracker.TryGetOwnershipSnapshot(',
  setCookIndex,
);
const startCallbackIndex = cookerStart.indexOf('CallCookerStartCallback', immediateOwnershipIndex);
const validatedOwnershipIndex = cookerStart.indexOf(
  'out var validatedOwnership',
  startCallbackIndex,
);
const registerCookingJobIndex = cookerStart.indexOf(
  'RegisterAutomationCookingJob(',
  validatedOwnershipIndex,
);
assert.ok(
  setCookIndex >= 0
    && immediateOwnershipIndex > setCookIndex
    && startCallbackIndex > immediateOwnershipIndex
    && validatedOwnershipIndex > startCallbackIndex
    && registerCookingJobIndex > validatedOwnershipIndex,
  'The Mod must claim its SetCook snapshot before callbacks, revalidate it afterwards, then register.',
);
assert.match(
  cookerStart,
  /ownershipSnapshot\.LastMutation != RuntimeCookingContentMutation\.SetCook[\s\S]*!ownershipSnapshot\.MutationCompleted/,
  'A job must not claim an incomplete SetCook or an Extract/Store snapshot as its own start.',
);
const cookerRevalidation = methodSource(
  cooking,
  'private static bool TryRevalidateCookerBeforeStart(',
);
assert.match(
  cookerRevalidation,
  /TryGetCookerFromCookSystem\([\s\S]*reservation[\s\S]*IsSameObject\(cookController, current\.CookController\)[\s\S]*IsSameObject\(selectedCooker, current\.ControllerState\.Cooker\)/,
  'Every repeated validation must fresh-read the reservation and preserve controller and bound-cooker identity.',
);
const cookerStartAvailability = methodSource(
  cookerStartAvailabilityService,
  'public static AutomationCookerStartAvailability Classify(',
);
assert.match(
  cookerStartAvailability,
  /state\.Phase == 0[\s\S]*state\.ResultEmpty[\s\S]*!state\.ChosenRecipeEmpty[\s\S]*state\.CouldOpen[\s\S]*TryGetOwnershipSnapshot\([\s\S]*LastMutation == RuntimeCookingContentMutation\.Extract[\s\S]*ownershipSnapshot\.MutationCompleted[\s\S]*AutomationCookerStartPolicy\.Classify/,
  'Residual recipe metadata must be reusable only after an exact normally completed Extract.',
);
assert.doesNotMatch(
  cookerStartAvailability,
  /InvokeInstance|AfterPlayerExtract|ReleaseCooker|IzakayaTray|ChosenRecipe\s*=/,
  'Start classification must remain passive and must not mutate native cooker or tray state.',
);
const cookingJobRegistration = methodSource(
  cooking,
  'private static AutomationCookingJob RegisterAutomationCookingJob(',
);
assert.match(
  cookingJobRegistration,
  /long specialFoodTargetRevision[\s\S]*SpecialFoodTargetRevision = specialFoodTargetRevision/,
  'Cooking-job registration must persist the exact revision captured before ingredient side effects.',
);
assert.match(
  cookingJobRegistration,
  /TryGetOwnershipSnapshot\([\s\S]*registrationOwnership != ownershipSnapshot[\s\S]*throw new InvalidOperationException/,
  'Job registration must retain the final exact ownership fence.',
);
assert.match(
  cookingJobRegistration,
  /duplicateTarget[\s\S]*IsSameCookingCollectionTarget\(job\.Target, target\)[\s\S]*throw new InvalidOperationException[\s\S]*job\.HoldsControllerReservation[\s\S]*job\.ControllerPointer == controllerPointer[\s\S]*throw new InvalidOperationException/,
  'Registration must fail closed on duplicate targets or an active controller lease.',
);
assert.doesNotMatch(
  cookingJobRegistration,
  /replacedJobs|cooking-job-replaced/,
  'The obsolete silent job-replacement path must not delete an evaluation receipt during controller reuse.',
);
const cookingJobLookup = methodSource(
  cooking,
  'private static bool TryFindAutomationCookingJob(',
);
assert.match(
  cookingJobLookup,
  /IsYuumaBossTarget\(target\)[\s\S]*job\.ManualHandoffObserved[\s\S]*IsSameCookingOrderIdentity\(job\.Target, target\)/,
  'Blood Pond Hell must keep one manual-handoff slot per exact order across target rotations.',
);
assert.match(
  cookerStart,
  /TryFindAutomationCookingJob\([\s\S]*TryCaptureYuumaFoodTargetRevision\([\s\S]*InvokeRuntimeStorageOut\("IngredientOut"/,
  'The exact-order handoff slot and Yuuma target revision must be checked before material side effects.',
);
assert.match(
  manualHandoffReceipt,
  /!job\.ManualHandoffExpired[\s\S]*TryDetectSpecialFoodTargetPolicyChanged\([\s\S]*targetComparisonAvailable && targetChanged[\s\S]*job\.ManualHandoffExpired = true[\s\S]*MarkManualHandoffExpired\([\s\S]*terminal: false/,
  'A rotated handoff must be latched once as non-terminal instead of being released for another cook.',
);
const targetComparison = methodSource(
  directDelivery,
  'private static bool TryDetectSpecialFoodTargetPolicyChanged(',
);
assert.match(
  targetComparison,
  /if \(currentPolicy == null\)[\s\S]*comparisonAvailable = false;[\s\S]*return false;/,
  'A missing current target policy must remain unavailable instead of becoming a confirmed rotation.',
);
assert.match(
  targetComparison,
  /TryGetActiveYuumaFoodTargetState\([\s\S]*currentYuumaPolicy\.BusinessGeneration != expectedPolicy\.BusinessGeneration[\s\S]*comparisonAvailable = false;[\s\S]*return !expectedPolicy\.HasSameIdentity\(currentYuumaPolicy\)[\s\S]*currentRevision != originalRevision;/,
  'A Yuuma policy and revision must be read atomically in the exact generation before they can confirm rotation.',
);
assert.match(
  manualHandoffReceipt,
  /TryReadNativeObjectPointer\(servedFood,[\s\S]*servedPointer == job\.CurrentResultPointer[\s\S]*CookingManualHandoffResolved/,
  'Manual handoff completion must distinguish the exact job result from another final food.',
);
assert.match(
  manualHandoffReceipt,
  /TryReadCookControllerFoodResultIdentity\(\s*servedFood,\s*"OrderBase\.ServFood"[\s\S]*actualIdentity\.FoodId == job\.Target\.FoodId/,
  'Manual handoff source diagnostics must use the exact managed Sellable identity reader.',
);
assert.doesNotMatch(
  manualHandoffReceipt,
  /ReadSellable(?:Type|Id)\(servedFood\)/,
  'Manual handoff source diagnostics must not fall back to broad Sellable reflection.',
);
assert.doesNotMatch(
  manualHandoffReceipt,
  /CookingManualHandoffTargetChanged|cooking-manual-handoff-target-changed/,
  'The removed target-change terminal/restart path was restored.',
);
const cookerReservation = methodSource(
  cooking,
  'private static bool IsCookControllerReserved(',
);
assert.match(
  cookerReservation,
  /TryReadNativeObjectPointer\(cookController, out var controllerPointer\)[\s\S]*job\.HoldsControllerReservation[\s\S]*job\.ControllerPointer == controllerPointer/,
  'Only an explicit active controller lease may reserve an otherwise idle cooker.',
);
assert.doesNotMatch(
  cookerReservation,
  /!job\.ManualHandoffObserved/,
  'Controller reservation must not be inferred from the legacy manual-handoff predicate.',
);
const committedCleanup = methodSource(
  directDelivery,
  'private static (bool Remove, string Message, string Code) TryCompleteCommittedFoodDeliveryCleanup(',
);
assert.match(
  committedCleanup,
  /FoodDeliveryCleanupTracker\.Complete\(\)[\s\S]*CompleteCookerExtractionAfterReset\(job\)[\s\S]*EnterCommittedFoodDeliveryEvaluationReceipt/,
  'A completed cleanup must release the controller only after all one-shot cooker callbacks finish.',
);
assert.match(
  committedCleanup,
  /if \(job\.FoodDeliveryCleanupCompleted\)[\s\S]*if \(job\.FoodDeliveryCleanupTerminal\)[\s\S]*if \(job\.DeliveredFood == null/,
  'A released receipt must recognize its terminal cleanup state before checking the intentionally cleared wrapper.',
);
const terminalCleanup = methodSource(
  directDelivery,
  'private static (bool Remove, string Message, string Code) BlockCommittedFoodDeliveryCleanup(',
);
assert.match(
  terminalCleanup,
  /DeliveryCleanupTerminated[\s\S]*cooking-evaluation-after-cleanup-terminal/,
  'A terminal cleanup that promises no further controller access must release its Mod lease.',
);
const evaluationReceiptTransition = methodSource(
  directDelivery,
  'private static void EnterCommittedFoodDeliveryEvaluationReceipt(',
);
assert.match(
  evaluationReceiptTransition,
  /ControllerLease\.Release[\s\S]*DeliveredFood = null[\s\S]*CurrentResultPointer = 0[\s\S]*EnterEvaluationPending/,
  'Evaluation-only receipts must release the controller and discard the delivered IL2CPP wrapper.',
);
for (const [methodName, prefixName, postfixName] of [
  ['setCook', 'setCookPrefix', 'setCookPostfix'],
  ['extract', 'extractPrefix', 'extractPostfix'],
  ['store', 'storePrefix', 'storePostfix'],
]) {
  assert.match(
    cookingOwnership,
    new RegExp(`_harmony\\.Patch\\(\\s*${methodName},[\\s\\S]*?prefix: new HarmonyMethod\\(${prefixName}\\),[\\s\\S]*?postfix: new HarmonyMethod\\(${postfixName}\\)\\)`),
    `Cooking ownership must observe both entry and normal completion for ${methodName}.`,
  );
}
assert.match(
  cookingOwnership,
  /RuntimeCookingOwnershipSnapshot\([\s\S]*Generation,[\s\S]*ContentRevision,[\s\S]*LastMutation,[\s\S]*MutationCompleted/,
  'Cooking ownership must bind generation, content revision, mutation, and normal completion.',
);
assert.match(
  cookingOwnership,
  /OnSetCookCompleted\([\s\S]*bool __runOriginal[\s\S]*OnExtractCompleted\([\s\S]*bool __runOriginal[\s\S]*OnStoreCompleted\([\s\S]*bool __runOriginal[\s\S]*RecordContentMutation\([\s\S]*MutationCompleted: false[\s\S]*CompleteContentMutation\([\s\S]*!originalRan[\s\S]*current\.ContentRevision != token\.ContentRevision[\s\S]*MutationCompleted = true/,
  'A native mutation may become completed only when its original ran and its matching postfix still owns the same revision.',
);
const cookingJobProcessor = methodSource(
  cooking,
  'private static (bool Remove, string Message, string Code) TryProcessAutomationCookingJob(',
);
assert.match(
  cookingJobProcessor,
  /TryReacquireAutomationCooker\([\s\S]*cookerBinding\.State[\s\S]*cookerBinding\.Ownership/,
  'A job must fresh-bind its exact reservation before reading cooker content or ownership.',
);
assert.doesNotMatch(
  cookingJobProcessor,
  /job\.CookController/,
  'An existing job still reads its start-time cooker wrapper.',
);
const freshCookerBinding = methodSource(
  cooking,
  'private static bool TryReacquireAutomationCooker(',
);
assert.match(
  freshCookerBinding,
  /TryReadLockedCookerPositions\([\s\S]*TryReadCookerControllerEntriesFromCookSystem\(\s*cookSystem,\s*lockedPositions,[\s\S]*job\.CookerReservation\.TryMatch\([\s\S]*lockedPositions\.Contains\(job\.CookerReservation\.GridPosition\)[\s\S]*controllerPointer != job\.ControllerPointer[\s\S]*TryReadCookerControllerState\([\s\S]*job\.CookerReservation\.EvaluateChallengeGate\(/,
  'Fresh job binding must reject a challenge-locked exact reservation before reading its controller state.',
);
assert.match(
  freshCookerBinding,
  /ownershipBefore != ownershipAfter[\s\S]*ownershipAfter\.Generation == job\.Generation[\s\S]*ownershipAfter\.ContentRevision == job\.ContentRevision[\s\S]*if \(!ownershipMatches\)/,
  'Fresh job binding must reject ownership changes before publishing a wrapper.',
);
assert.match(
  freshCookerBinding,
  /expectedCompletedMutation\.HasValue[\s\S]*ownershipAfter\.LastMutation == expectedCompletedMutation\.Value[\s\S]*ownershipAfter\.MutationCompleted/,
  'Fresh post-callback binding must require the exact completed native mutation receipt.',
);
assert.match(
  cookingJobSource,
  /public string CookerBindingFailureCode \{ get; set; \}/,
  'The job snapshot must retain a managed diagnostic for fresh-binding failure without caching a wrapper.',
);
const committedCookerReset = methodSource(
  directDelivery,
  'private static bool TryResetCookControllerAfterCommittedSideEffect(',
);
const resetFreshBinding = committedCookerReset.indexOf('TryReacquireAutomationCooker(');
const resetFirstMutation = Math.min(
  ...[
    committedCookerReset.indexOf('CloseCookingVisual'),
    committedCookerReset.indexOf('WriteMember('),
  ].filter((index) => index >= 0),
);
const resetConfirmationBinding = committedCookerReset.indexOf(
  'TryReacquireAutomationCooker(',
  resetFreshBinding + 1,
);
assert.ok(
  resetFreshBinding >= 0
    && resetFirstMutation > resetFreshBinding
    && resetConfirmationBinding > resetFirstMutation,
  'Committed cooker cleanup must fresh-bind before mutation and rebind before confirming reset.',
);
assert.doesNotMatch(
  committedCookerReset,
  /job\.CookController/,
  'Committed cooker cleanup still mutates a retained wrapper.',
);
assert.doesNotMatch(
  cooking,
  /InvokeInstance\([^;]*(?:"Extract"|"Store"|"FinishCooking")/,
  'Ownership recovery must not replay native cooker mutation entries.',
);
const snapshotReader = methodSource(
  cookerSnapshot,
  'private static RuntimeCookerSnapshotReadResult ReadPlacedCookers()',
);
assert.match(
  snapshotReader,
  /TryReadLockedCookerPositions\([\s\S]*TryReadCookerControllerEntriesFromCookSystem\(\s*cookSystem,\s*lockedPositions,[\s\S]*lockedControllerCount = controllerEntries\.Count\([\s\S]*for \(var controllerIndex = 0; controllerIndex < controllerEntries\.Count; controllerIndex\+\+\)[\s\S]*lockedPositions\.Contains\(entry\.GridPosition\)[\s\S]*continue;[\s\S]*TryReadCookerControllerState\([\s\S]*RuntimeCookerSnapshotReadResult\.Unavailable\([\s\S]*if \(!controllerState\.CouldOpen\)[\s\S]*RuntimeCookerSnapshotReadResult\.Unavailable[\s\S]*controllerState\.IsEmptyDesk[\s\S]*emptyControllerCount\+\+[\s\S]*continue/,
  'Placed-cooker snapshots must classify locked keys before touching only unlocked controllers and fail the whole round on any remaining uncertainty.',
);
assert.match(
  cooking,
  /if \(controllerState\.IsEmptyDesk\)[\s\S]*已变为空厨具位[\s\S]*等待最新快照重新调度/,
  'An action-time reservation that became an empty desk must wait without selecting another controller.',
);
assert.match(
  cookerHighlight,
  /if \(state\.IsEmptyDesk\) continue;[\s\S]*openRenderers\.AddRange\(controllerRenderers\);[\s\S]*var claims = RuntimeUiTargetKinds\.None;[\s\S]*if \(HasCookerHighlightTargets\(targetSet\)\)[\s\S]*foreach \(var cookerTypeId in state\.TypeIds\)[\s\S]*claims \|= targetSet\.GetCookerClaims\(cookerTypeId\);[\s\S]*if \(claims != RuntimeUiTargetKinds\.None\)[\s\S]*existing\.Claims \|= claims[\s\S]*new TargetRenderer\(renderer, pointer, claims\)/,
  'Cooker highlighting must skip empty desks, retain every fresh open renderer, and merge rare/normal claims for each matching cooker type.',
);
assert.match(
  cookerHighlight,
  /RestoreRetainedBaselinesLocked\(openRenderers\);[\s\S]*if \(!HasCookerHighlightTargets\(targetSet\)\)[\s\S]*foreach \(var targetRenderer in targetRenderers\.Values\)/,
  'Cooker highlighting must restore post-event baselines before disabling or applying the claim-bearing target renderer set.',
);
assert.match(
  snapshotReader,
  /cookers\.Count \+ emptyControllerCount \+ lockedControllerCount != controllerEntries\.Count[\s\S]*Complete = true[\s\S]*ControllerCount = controllerEntries\.Count[\s\S]*EmptyControllerCount = emptyControllerCount[\s\S]*LockedControllerCount = lockedControllerCount[\s\S]*ReadFailureCount = 0/,
  'A published cooker snapshot must be fully complete with no partial controller capacity.',
);
assert.match(
  snapshotReader,
  /RuntimeCookerStartAvailabilityService\.Classify\([\s\S]*out var automationAvailabilityDiagnostic[\s\S]*GridPosition = new CookerGridPosition[\s\S]*ControllerIdentity = entry\.ControllerIdentity[\s\S]*ChallengeLocked = false[\s\S]*CouldOpen = controllerState\.CouldOpen[\s\S]*AutomationAvailable = automationAvailability != AutomationCookerStartAvailability\.Unavailable/,
  'Readable unlocked cooker snapshots must publish exact identity/grid and distinguish start availability.',
);
const snapshotApply = methodSource(
  cookerSnapshot,
  'public static void ApplyTo(RecommendationState state)',
);
assert.match(
  snapshotApply,
  /PlacedCookers\.Clear\(\)[\s\S]*PlacedCookerTypeIds\.Clear\(\)[\s\S]*PlacedCookerSnapshotComplete = snapshot\.Complete[\s\S]*PlacedCookerControllerCount = snapshot\.ControllerCount[\s\S]*PlacedCookerEmptyControllerCount = snapshot\.EmptyControllerCount[\s\S]*PlacedCookerLockedControllerCount = snapshot\.LockedControllerCount[\s\S]*PlacedCookerReadFailureCount = snapshot\.ReadFailureCount/,
  'Each cooker snapshot read must replace prior entries and structured read state.',
);
assert.match(
  cookerSnapshotSignature,
  /PlacedCookerSnapshotComplete[\s\S]*PlacedCookerControllerCount[\s\S]*PlacedCookerEmptyControllerCount[\s\S]*PlacedCookerLockedControllerCount[\s\S]*PlacedCookerReadFailureCount[\s\S]*cooker\.GridPosition\.X[\s\S]*cooker\.GridPosition\.Y[\s\S]*cooker\.GridPosition\.Z[\s\S]*cooker\.ControllerIdentity[\s\S]*cooker\.ChallengeLocked[\s\S]*cooker\.CouldOpen[\s\S]*cooker\.AutomationAvailable/,
  'Cooker content signatures must include exact grid, native identity, challenge lock, gate, and availability.',
);
const clearPlacedCookers = methodSource(
  overlay,
  'private void ClearPlacedCookersFromCurrentState(string status)',
);
assert.match(
  clearPlacedCookers,
  /PlacedCookers\.Clear\(\)[\s\S]*PlacedCookerTypeIds\.Clear\(\)[\s\S]*PlacedCookerSnapshotComplete = false[\s\S]*PlacedCookerControllerCount = 0[\s\S]*PlacedCookerEmptyControllerCount = 0[\s\S]*PlacedCookerLockedControllerCount = 0[\s\S]*PlacedCookerReadFailureCount = 0[\s\S]*PlacedCookerStatus = status/,
  'Leaving night business must clear cooker entries, physical types, and all structured snapshot fields.',
);

const exactAllCookersReader = methodSource(
  cookerReflection,
  'public static bool TryReadCookerControllerEntriesFromCookSystem(',
);
assert.match(
  cookerReflection,
  /BindingFlags\.Public \| BindingFlags\.NonPublic \| BindingFlags\.Instance \| BindingFlags\.DeclaredOnly/,
  'The exact member reader must include NonPublic because CookSystemManager.AllCookers is private.',
);
assert.match(
  exactAllCookersReader,
  /TryGetSingleDeclaredMethod\([\s\S]*"get_AllCookers"[\s\S]*getAllCookers\.Invoke\(cookSystem/,
  'The shared controller reader must use the declared CookSystemManager.get_AllCookers member.',
);
assert.match(
  exactAllCookersReader,
  /IReadOnlySet<RuntimeCookerGridPosition> lockedPositions[\s\S]*RuntimeConcreteCollectionReader\.TryReadDictionary\([\s\S]*TryReadExactVector3Int\(entry\.Key,[\s\S]*locked-grid-missing[\s\S]*if \(lockedPositions\.Contains\(entry\.GridPosition\)\)[\s\S]*continue;[\s\S]*TryReadControllerGridPosition\([\s\S]*entry\.GridPosition != controllerPosition/,
  'The shared controller reader must skip unsafe getters for exact locked keys and match every unlocked dictionary key to its controller GridPosition.',
);
assert.ok(
  cookerReflection.includes('Il2CppDictionaryTypeName')
    && !cookerReflection.includes('ManagedDictionaryTypeName'),
  'CookSystemManager.AllCookers must accept only the exact BepInEx 783 IL2CPP dictionary shape.',
);
assert.doesNotMatch(
  exactAllCookersReader,
  /AllCookerControllers|UnityFind|FindUnityObjects|ReadDictionaryValues|ReadObjectPointer|IDictionary|DictionaryEntry|NormalizeKeyValueValue|"(?:entries|_entries|m_Entries)"/,
  'The exact AllCookers reader must not retain alternate controller discovery or ad-hoc dictionary paths.',
);
assert.doesNotMatch(
  cookerReflection,
  /TryReadCookerControllersFromCookSystem/,
  'The removed index-only controller projection must not remain in production.',
);
const exactLockedCookersReader = methodSource(
  cookerReflection,
  'public static bool TryReadLockedCookerPositions(',
);
assert.match(
  exactLockedCookersReader,
  /TryGetExactMonoSingletonInstance\([\s\S]*"get_LockedCookers"[\s\S]*TryGetClosedStructArrayElementType\([\s\S]*TryReadExactVector3IntArray\(/,
  'Challenge locks must use the exact EventManager singleton and Il2CppStructArray<Vector3Int> getter.',
);
assert.doesNotMatch(
  exactLockedCookersReader,
  /LockedCookersRaw|GetStaticMemberValue|IEnumerable|Enumerate/,
  'Challenge lock reads must not restore raw, broad singleton, or generic enumeration paths.',
);

const deprecatedCookerDiscoveryPattern =
  /AllCookerControllers|UnityFind|FindUnityObjects|ReadDictionaryValues|DictionaryEntry|NormalizeDictionaryItem|NormalizeKeyValueValue|"(?:entries|_entries|m_Entries)"/;
for (const [label, source] of [
  ['shared cooker reflection', cookerReflection],
  ['cooking', cooking],
  ['snapshot', cookerSnapshot],
  ['highlight', cookerHighlight],
]) {
  assert.doesNotMatch(source, deprecatedCookerDiscoveryPattern,
    `${label} must not retain deprecated cooker discovery or dictionary parsing paths.`);
}

const publishCookerTarget = sourceSlice(cookerHighlight, 'public static void UpdateTargets(', 'public static void Tick()');
assert.ok(!/SpriteRenderer|UnityEngine|Time\.|Restore|ScanAndApply|PulseHighlightedRenderers/.test(publishCookerTarget),
  'Background cooker-target publication must only replace managed desired state.');
assert.doesNotMatch(
  cookerHighlight,
  /RuntimeHelpers|GetHashCode\(/,
  'Cooker renderer identity must not fall back to managed object hashes.',
);
const reconcileCookerTarget = sourceSlice(cookerHighlight, 'public static void Tick()', 'public static void Suspend(');
assert.match(reconcileCookerTarget, /RuntimeNightBusinessLifecycle\.Snapshot/);
assert.match(reconcileCookerTarget, /ScanAndApply\(desired\)/);
assert.match(reconcileCookerTarget, /PulseHighlightedRenderers\(desired\)/);
const scanAndApplyCookerHighlight = methodSource(cookerHighlight, 'private static void ScanAndApply(');
assert.match(
  scanAndApplyCookerHighlight,
  /Dictionary<nint, TargetRenderer>[\s\S]*targetSet\.GetCookerClaims\(cookerTypeId\)[\s\S]*existing\.Claims \|= claims/,
  'A cooker shared by rare and normal targets must merge both claims under one native renderer identity.',
);
assert.match(
  scanAndApplyCookerHighlight,
  /HighlightedRenderers\.TryGetValue\(pointer, out var existing\)[\s\S]*existing\.Claims = targetRenderer\.Claims[\s\S]*new RendererBaseline\(renderer\.color, renderer\.enabled\)/,
  'Cooker reconciliation must update one owned renderer claim while capturing its baseline only on first ownership.',
);
const pulseCookerHighlight = methodSource(cookerHighlight, 'private static void PulseHighlightedRenderers(');
assert.match(
  pulseCookerHighlight,
  /BuildCookerSpritePulseColor\([\s\S]*item\.OriginalColor,[\s\S]*item\.Claims,[\s\S]*targetSet\.Palette/,
  'Cooker pulse must render merged claims through the published two-color palette.',
);

assert.match(overlay, /AppendAutomationRuntimeEvents[\s\S]*OrderBy\(item => item\.Sequence\)[\s\S]*AppendValue\(builder, item\.Sequence\)/);
assert.match(overlay, /AppendValue\(StringBuilder builder, long value\)[\s\S]*InvariantCulture/);
const appendAutomationRuntimeEvents = methodSource(
  overlay,
  'private static void AppendAutomationRuntimeEvents(',
);
for (const field of ['OrderRuntimeKind', 'OrderId', 'OrderControllerId', 'OrderLifecycleSequence']) {
  assert.ok(
    appendAutomationRuntimeEvents.includes(`item.${field}`),
    `The canonical snapshot signature omits automation runtime-event field ${field}.`,
  );
}
assert.match(
  methodSource(overlay, 'private static void AppendNightBusiness('),
  /order\.OrderLifecycleSequence/,
  'Rare-order lifecycle changes must invalidate the canonical snapshot signature.',
);
assert.match(
  methodSource(overlay, 'private static void AppendNormalBusiness('),
  /order\.OrderLifecycleSequence/,
  'Normal-order lifecycle changes must invalidate the canonical snapshot signature.',
);
const appendAutomationCookingJobs = methodSource(
  overlay,
  'private static void AppendAutomationCookingJobs(',
);
for (const field of [
  'TransactionStage',
  'HoldsControllerReservation',
  'ControllerLeaseReleaseReason',
  'OrderRuntimeKind',
  'OrderId',
  'OrderControllerId',
  'OrderLifecycleSequence',
  'FoodDeliveryCleanupCompleted',
  'FoodDeliveryCleanupTerminal',
  'FoodDeliveryEvaluationState',
  'FoodDeliveryEvaluationAttempts',
  'FoodDeliveryEvaluationEffectiveSeconds',
]) {
  assert.ok(
    appendAutomationCookingJobs.includes(`job.${field}`),
    `The canonical snapshot signature omits automation cooking-job field ${field}.`,
  );
}

const specialBusinessRequestGate = namedMethodSource(
  matching,
  'IsSpecialBusinessOrderAllowedForRequest',
);
assert.match(
  specialBusinessRequestGate,
  /SpecialBusinessOrderClassifier\.Classify\([\s\S]*TryValidateMizuchiRolePair\([\s\S]*request[\s\S]*classification/,
  'Every Mizuchi request/order match must preserve exact classifier role parity.',
);

const mizuchiRequestContract = namedMethodSource(mizuchiRolePolicy, 'TryValidateRequest');
assert.match(
  mizuchiRequestContract,
  /contract\.IsPossessed[\s\S]*Count\(id => id == contract\.TargetIngredientId\) != 1/,
  'A possessed Mizuchi request must carry exactly one scene-specific target Modifier ingredient.',
);
assert.doesNotMatch(
  mizuchiRolePolicy,
  /OrdinalIgnoreCase|role\??\.(?:Trim|Contains|StartsWith|EndsWith)\(/,
  'Mizuchi automation roles must remain opaque exact Ordinal identities.',
);
assert.match(
  mizuchiRequestContract,
  /!contract\.IsPossessed[\s\S]*Contains\(contract\.TargetIngredientId\)/,
  'An ordinary Mizuchi request must reject its scene-specific target ingredient in Food.Modifier.',
);
assert.match(
  mizuchiRolePolicy,
  /MizuchiStoryPossessed[\s\S]*PuyoyoFruitIngredientId[\s\S]*MizuchiTrialPossessed[\s\S]*PepperWaterIngredientId/,
  'Story and trial roles must resolve distinct target ingredients without inference from extras.',
);

const cookingStart = namedMethodSource(cooking, 'TryStartCooking');
const createdModifierIndex = cookingStart.indexOf('created-food-before-deduction');
const mizuchiPreDeductionIndex = cookingStart.indexOf('TryValidateCookingTargetOrderLifecycle(');
const ingredientOutIndex = cookingStart.indexOf('InvokeRuntimeStorageOut("IngredientOut"');
const mizuchiPreSetCookIndex = cookingStart.indexOf(
  'TryValidateCookingTargetOrderLifecycle(',
  mizuchiPreDeductionIndex + 1,
);
const mizuchiSetCookIndex = cookingStart.indexOf('InvokeInstance(cookController, "SetCook"');
assert.ok(
  cookingStart.includes('"before-ingredient-deduction"'),
  'Pre-deduction lifecycle validation must identify the fresh Mizuchi role checkpoint.',
);
assert.ok(
  cookingStart.includes('"immediately-before-set-cook"'),
  'Pre-SetCook lifecycle validation must identify the fresh Mizuchi role checkpoint.',
);
assert.ok(
  createdModifierIndex >= 0
    && mizuchiPreDeductionIndex > createdModifierIndex
    && ingredientOutIndex > mizuchiPreDeductionIndex
    && mizuchiPreSetCookIndex > ingredientOutIndex
    && mizuchiSetCookIndex > mizuchiPreSetCookIndex,
  'Mizuchi Modifier/role checks must bracket material deduction and SetCook in the authoritative cooking path.',
);
const cookingLifecycleValidation = namedMethodSource(service, 'TryValidateCookingTargetOrderLifecycle');
assert.match(
  cookingLifecycleValidation,
  /TryValidateMizuchiCookingTargetFresh\([\s\S]*checkpoint/,
  'Each cooking lifecycle checkpoint must fresh-read the exact Mizuchi order role and closure.',
);

const beverageDelivery = namedMethodSource(directDelivery, 'TryDeliverOrderBeverage');
assert.match(
  beverageDelivery,
  /TryValidateMizuchiRuntimeOrder\([\s\S]*"before-beverage-setter"[\s\S]*TryCommitRuntimeDelivery\(/,
  'Beverage delivery must fresh-check the exact Mizuchi role/closure immediately before its setter path.',
);

const foodDelivery = namedMethodSource(directDelivery, 'TryDeliverAutomationCookedFood');
const cookedModifierIndex = foodDelivery.indexOf('cooked-result-before-delivery');
const freshOrderIndex = foodDelivery.indexOf('fresh-order-before-food-state');
const immediateFoodIndex = foodDelivery.indexOf('immediately-before-food-setter');
const commitFoodIndex = foodDelivery.indexOf(
  'TryCommitRuntimeDelivery(runtimeOrder, cookedFood, RuntimeDeliveryItemKind.Food',
);
assert.ok(
  cookedModifierIndex >= 0
    && freshOrderIndex > cookedModifierIndex
    && immediateFoodIndex > freshOrderIndex
    && commitFoodIndex > immediateFoodIndex,
  'Cooked Mizuchi food must preserve exact Modifier and fresh role identity through the final setter boundary.',
);

const evaluationRoute = namedMethodSource(
  delivery,
  'TryEvaluateMatchedAutomationOrderRuntimeIfReady',
);
assert.match(
  evaluationRoute,
  /fulfilledPreflight = \(\) =>[\s\S]*TryValidateMizuchiEvaluationPreflight\([\s\S]*fulfilledPreflight: fulfilledPreflight/,
  'Mizuchi role and served-food Modifier validation must be attached to the fulfilled-only generic evaluation boundary.',
);
const genericEvaluation = namedMethodSource(delivery, 'TryEvaluateRuntimeOrderIfReady');
const genericFulfilledReadIndex = genericEvaluation.indexOf('get_IsFullfilled');
const genericUnfulfilledWaitIndex = genericEvaluation.indexOf('if (!isFullfilled)');
const genericFulfilledPreflightIndex = genericEvaluation.indexOf('fulfilledPreflight?.Invoke()');
const genericNativeEvaluationIndex = genericEvaluation.indexOf('TryInvokeRuntimeOrderEvaluationOnce(');
assert.ok(
  genericFulfilledReadIndex >= 0
    && genericUnfulfilledWaitIndex > genericFulfilledReadIndex
    && genericFulfilledPreflightIndex > genericUnfulfilledWaitIndex
    && genericNativeEvaluationIndex > genericFulfilledPreflightIndex,
  'An incomplete order must return the normal wait outcome before the Mizuchi ServFood preflight; a fulfilled order must run that preflight immediately before native evaluation.',
);
assert.match(
  namedMethodSource(mizuchiPolicy, 'TryValidateMizuchiEvaluationPreflight'),
  /TryReadOrderServedItem\([\s\S]*TryReadCookControllerFoodResultIdentity\([\s\S]*TryValidateMizuchiFoodModifier\(/,
  'Mizuchi evaluation must reread exact ServFood identity and Modifier before native evaluation.',
);

assert.match(
  foodModifierValidation,
  /TryReadExactMemberValue\([\s\S]*"Modifier"[\s\S]*RuntimeConcreteCollectionReader\.TryReadIntArray/,
  'Food.Modifier must use the single strict concrete int-array reader.',
);
assert.match(yuyukoPolicy, /TryValidateServedFoodExtraIngredients\(/,
  'Yuyuko must consume the shared strict Modifier reader.');
assert.doesNotMatch(yuyukoPolicy, /TryValidateYuyukoRetakeServedExtraIngredients/,
  'The obsolete Yuyuko-only Modifier path must be removed.');
assert.doesNotMatch(
  mizuchiPolicy,
  /MoveNext|FindObject|FindObjects|GetOrderFoodText|GetOrderBevText|DynamicInvoke|\.Invoke\(.*OverrideEvaluationCallback/,
  'Mizuchi automation must not add generated-state hooks, scene scans, display-text identities, or callback execution.',
);
assert.match(service, /MizuchiContractMismatch = "mizuchi-contract-mismatch"/);
assert.match(
  namedMethodSource(service, 'IsAutomationSafetyBarrierCode'),
  /MizuchiContractMismatch/,
  'Mizuchi contract drift must enter the existing exact-order manual safety barrier.',
);

console.log('PASS: runtime automation uses exact native signatures, one-shot side-effect boundaries, passive start receipts, and authoritative safety barriers.');

function sourceSlice(source, startMarker, endMarker) {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start + startMarker.length);
  assert.ok(start >= 0, `Source marker not found: ${startMarker}`);
  assert.ok(end > start, `Source boundary not found: ${startMarker} -> ${endMarker}`);
  return source.slice(start, end);
}

function methodSource(source, marker) {
  const start = source.indexOf(marker);
  assert.ok(start >= 0, `Method marker not found: ${marker}`);
  const bodyStart = source.indexOf('{', start + marker.length);
  assert.ok(bodyStart > start, `Method body not found: ${marker}`);

  let depth = 0;
  for (let index = bodyStart; index < source.length; index++) {
    if (source[index] === '{') depth++;
    if (source[index] !== '}') continue;
    depth--;
    if (depth === 0) return source.slice(start, index + 1);
  }

  assert.fail(`Method body is not balanced: ${marker}`);
}

function namedMethodSource(source, methodName) {
  const declaration = new RegExp(
    `(?:private|internal|public)\\s+static[^{;]*\\b${methodName}\\s*\\(`,
  ).exec(source);
  assert.ok(declaration, `Named method not found: ${methodName}`);
  return methodSource(source, declaration[0]);
}
