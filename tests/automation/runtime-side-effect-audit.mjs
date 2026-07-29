import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const read = (relativePath) => fs.readFileSync(path.join(root, relativePath), 'utf8');

const lifecycle = read('mods/bepinex/src/Save/AutomationCookingJobLifecycle.cs');
const cooking = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.Cooking.cs');
const delivery = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.Delivery.cs');
const directDelivery = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.DirectDelivery.cs');
const cookerHighlight = read('mods/bepinex/src/Save/RuntimeCookerHighlightService.cs');
const cookerSnapshot = read('mods/bepinex/src/Save/RuntimeCookerSnapshotService.cs');
const cookerReflection = read('mods/bepinex/src/Save/RuntimeCookerReflection.cs');
const cookingOwnership = read('mods/bepinex/src/Save/RuntimeCookingGenerationTracker.cs');
const cookerStartPolicy = read('mods/bepinex/src/Save/AutomationCookerStartPolicy.cs');
const matching = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.OrderMatching.cs');
const runtimeReflection = read('mods/bepinex/src/Save/RuntimeReflectionUtility.cs');
const service = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.cs');
const yuyukoPolicy = read('mods/bepinex/src/Save/SpecialBusiness/RuntimeOrderPreparationService.YuyukoChallengePolicy.cs');
const capture = read('mods/bepinex/src/Save/SpecialOrderRuntimeCapture.cs');
const provider = read('mods/bepinex/src/Save/NightBusinessReflectionProvider.cs');
const overlay = read('mods/bepinex/src/Ui/StewardOverlayController.cs');

assert.ok(!cooking.includes('"FinishCooking"'), 'The Mod must not invoke the non-idempotent FinishCooking entry.');
assert.ok(!lifecycle.includes('FinalizeOwnedResult'), 'The cooking tracker still exposes the removed active-finalize directive.');
assert.match(lifecycle, /"cooking-native-finalize-waiting"[\s\S]*AutomationCookingJobDirective\.None/);

assert.match(cooking, /parameters\.Length == 2[\s\S]*parameters\[0\]\.ParameterType == typeof\(int\)[\s\S]*parameters\[1\]\.ParameterType == typeof\(bool\)/);
assert.match(cooking, /method\.Invoke\(null, new object\?\[\] \{ itemId, false \}\)/);
assert.ok(!cooking.includes('CopyNormalOrderRequestWithoutOrderKey'));
assert.ok(!matching.includes('CopyNormalOrderRequestWithoutOrderKey'));
assert.ok(!matching.includes('orderKeyFallback'));
assert.match(matching, /BuildRequestOrderIdentity[\s\S]*request\.RuntimeGuestId[\s\S]*request\.FoodTagId[\s\S]*request\.BeverageTagId/);
assert.match(matching, /TryReadGuestId[\s\S]*TryReadInt\([\s\S]*get_Id[\s\S]*ReadMember\(guest, "Id"\)/);
assert.match(matching, /ReadControllerOrderCollection[\s\S]*HasIl2CppEnumerator\(value\)[\s\S]*EnumerateIl2Cpp\(value\)/);
assert.match(matching, /Delivery,[\s\S]*Completion,[\s\S]*NativeEvaluation/);
assert.match(service, /FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.Completion\)/);
assert.match(cooking, /FindRuntimeOrder\(request, RuntimeOrderLookupPurpose\.Completion\)/);
assert.match(service, /RuntimeGuestId = request\.RuntimeGuestId/);
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

const capturedSpecialLiveness = sourceSlice(
  matching,
  'private static bool IsCapturedSpecialOrderLive(',
  'private static IEnumerable<object> EnumerateGuestControllers(');
assert.match(capturedSpecialLiveness, /EnumerateControllerOrders\(controllerObject\)[\s\S]*CompareObjectIdentity\(order, orderObject\)/,
  'Captured-order liveness must still prove current controller ownership.');
assert.match(capturedSpecialLiveness, /RareOrderIdentityMatcher\.IsExecutableCapturedOrder/);
assert.match(capturedSpecialLiveness, /return IsMatchingSpecialOrder\(orderObject!, controllerObject!, request, purpose, out rejectReason\)/,
  'Captured orders must still pass the complete raw runtime identity matcher.');

assert.match(rareLookup, /foreach \(var enumeratedOrder in EnumerateControllerOrders\(controller\)\)[\s\S]*var order = NormalizeRuntimeSpecialOrder\(enumeratedOrder\);[\s\S]*IsMatching(?:YuyukoPhase3EvaluationOrder|SpecialOrder)\(order/,
  'Orders enumerated through OrderBase must be normalized to SpecialOrder before raw Tag IDs are read.');
const specialOrderNormalizer = sourceSlice(
  matching,
  'private static object NormalizeRuntimeSpecialOrder(',
  'private static int? TryReadSpecialOrderTagId(');
assert.match(specialOrderNormalizer, /RuntimeReflectionUtility\.TryCastRuntimeObject\(order, SpecialOrderTypeName\) \?\? order/,
  'The live-order normalizer must use the shared runtime-object cast entry.');
assert.match(runtimeReflection, /TryCastRuntimeObject\([\s\S]*value is not Il2CppObjectBase[\s\S]*FindType\(targetTypeName\)[\s\S]*typeof\(Il2CppObjectBase\)[\s\S]*method\.Name == "TryCast"[\s\S]*MakeGenericMethod\(targetType\)/,
  'The shared runtime-object cast entry must resolve and invoke the concrete runtime cast.');

const storyManualCallbackLookup = sourceSlice(
  matching,
  'private static object? FindCapturedYuyukoPhase3ManualEvaluationCallback(',
  'private static RuntimeOrderMatch FindCapturedRuntimeNormalOrder(');
assert.match(storyManualCallbackLookup, /CompareObjectIdentity\(captured\.OrderObject, order\) == RuntimeObjectIdentityComparison\.Same/,
  'A story callback must belong to the exact captured order object selected by the live lookup.');
assert.match(storyManualCallbackLookup, /CompareObjectIdentity\(captured\.ControllerObject, controller\) == RuntimeObjectIdentityComparison\.Same/,
  'A story callback must belong to the exact captured controller object selected by the live lookup.');
assert.ok(!storyManualCallbackLookup.includes('TryMatchCapturedOrderIdentity'),
  'Story callbacks must not be selected from the first request-identity capture.');
assert.doesNotMatch(storyManualCallbackLookup, /OrderPreparationRequest\s+request/,
  'Story callback lookup must accept the matched order/controller pair, not a request identity.');

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

assert.match(capture, /IsOrderDeliveryContext[\s\S]*order\.IsFulfilled[\s\S]*AddOrder\(order with[\s\S]*"Fulfilled"/);
assert.match(provider, /HasServedFood = captured\.IsFulfilled[\s\S]*HasServedBeverage = captured\.IsFulfilled/);
const capturedLivenessStart = provider.indexOf('private bool IsCapturedRuntimeOrderStillLive');
const capturedLivenessEnd = provider.indexOf('private static bool IsSameRuntimeObject', capturedLivenessStart);
assert.ok(capturedLivenessStart >= 0 && capturedLivenessEnd > capturedLivenessStart);
assert.ok(!provider.slice(capturedLivenessStart, capturedLivenessEnd).includes('IsRuntimeOrderFulfilled'));

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
const completionStart = directDelivery.indexOf('private static AutomationFoodDeliveryCompletion BuildFoodDeliveryCompletion(');
const completionEnd = directDelivery.indexOf('private static (bool Remove, string Message, string Code) TryCompleteCommittedFoodDeliveryCleanup', completionStart);
assert.ok(completionStart >= 0 && completionEnd > completionStart);
assert.ok(!directDelivery.slice(completionStart, completionEnd).includes('TryEvaluate'), 'Cooking-job cleanup must not evaluate an order.');

assert.match(cooking, /RegisterAutomationCookingJob\([\s\S]*autoCollect\)/);
assert.match(cooking, /ManualHandoffObserved[\s\S]*TryProcessManualHandoffReceipt/);
assert.match(cooking, /TryGetUnresolvedAutomationSafetyBarrier\(job\.Target[\s\S]*job\.Tracker\.Suspend/);
assert.match(service, /TryApplyUnresolvedAutomationSafetyBarrier\(result, "rare"/);
assert.match(service, /TryApplyUnresolvedAutomationSafetyBarrier\(result, "normal"/);
assert.match(service, /AcknowledgeAutomationSafetyBarrier[\s\S]*AcknowledgedSequences/);
assert.match(service, /TryProcessAutomationCookingJob\(job, timeoutEligible\)[\s\S]*ReferenceEquals\(AutomationCookingJobs\[i\], job\)/);
assert.match(cooking, /TryProcessNormalOrderCookingJob[\s\S]*ReferenceEquals\(AutomationCookingJobs\[i\], job\)/);

const sharedCookerControllerReader = 'RuntimeCookerReflection.ReadCookerControllersFromCookSystem(';
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
  'public static List<int> ReadCookerTypeIds(',
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
  'if (typeId == 0) continue;',
  'CookerTypeNames.ContainsKey(typeId)',
  'dispose.Invoke(',
  'return valid && completed && typeIds.Count > 0;',
]) {
  assert.ok(
    exactCookerTypeReader.includes(requiredToken),
    `Exact cooker type enumeration is missing ${requiredToken}.`,
  );
}
assert.doesNotMatch(
  exactCookerTypeReader,
  /get_Type|EnumerateObjects|ReadIntEnumerable/,
  'Complete cooker type reads must not retain base-type or broad enumerable fallbacks.',
);
assert.ok(
  cookerSnapshot.includes('RuntimeCookerReflection.TryReadCookerControllerState(')
    && cookerHighlight.includes('RuntimeCookerReflection.ReadCookerTypeIds(')
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
assert.match(
  cookerReflection,
  /public bool IsIdle => Phase == 0 && ResultEmpty && ChosenRecipeEmpty && CouldOpen;/,
  'The shared native state must retain its strict idle/result/recipe/open definition.',
);
assert.match(
  cookerStartPolicy,
  /if \(phase != 0 \|\| !resultEmpty \|\| !couldOpen\)[\s\S]*if \(chosenRecipeEmpty\)[\s\S]*StrictIdle[\s\S]*completedExtractObserved[\s\S]*ExtractedResidual[\s\S]*Unavailable/,
  'Cooker reuse must allow only strict idle or a completed Extract with residual recipe metadata.',
);
const fallbackCookerSelection = methodSource(
  cooking,
  'private static (bool Ok, bool Waiting, object? CookController, string Message) TryGetCookerFromCookSystem(',
);
assert.ok(
  fallbackCookerSelection.includes('RuntimeCookerReflection.GetCookSystemManager()')
    && fallbackCookerSelection.includes('RuntimeCookerReflection.TryReadCookerControllerState('),
  'Fallback cooking must share the exact manager and full controller state reader.',
);
assert.doesNotMatch(
  fallbackCookerSelection,
  /GetSingletonInstance\(CookSystemManagerTypeName\)|get_CouldCookerOpen/,
  'Fallback cooking must not independently scan a manager or treat CouldCookerOpen as complete availability.',
);
assert.match(
  fallbackCookerSelection,
  /ClassifyCookerStartAvailability\([\s\S]*AutomationCookerStartAvailability\.Unavailable[\s\S]*AutomationCookerStartAvailability\.ExtractedResidual/,
  'Cooker selection must use the exact start-availability classifier.',
);
assert.doesNotMatch(
  cooking,
  /TryGetCookerForOrder/,
  'Cooking must not accept a second controller source outside the exact AllCookers dictionary.',
);
assert.match(
  fallbackCookerSelection,
  /return \(false, true, null,[\s\S]*自动化将等待空闲厨具，不计入失败重试/,
  'Natively busy compatible cookers must stay in a local waiting outcome.',
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
const cookerRevalidationIndex = cookerStart.indexOf('TryRevalidateCookerBeforeStart(');
const materialDeductionIndex = cookerStart.indexOf('InvokeRuntimeStorageOut("IngredientOut"');
assert.ok(
  cookerRevalidationIndex >= 0
    && materialDeductionIndex >= 0
    && cookerRevalidationIndex < materialDeductionIndex,
  'The selected cooker must be fully revalidated before the first material deduction.',
);
const setCookIndex = cookerStart.indexOf('InvokeInstance(cookController, "SetCook"');
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
  /ClassifyCookerStartAvailability\([\s\S]*AutomationCookerStartAvailability\.Unavailable/,
  'The pre-material revalidation must repeat the same fresh start-availability classification.',
);
const cookerStartAvailability = methodSource(
  cooking,
  'private static AutomationCookerStartAvailability ClassifyCookerStartAvailability(',
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
  /TryGetOwnershipSnapshot\([\s\S]*registrationOwnership != ownershipSnapshot[\s\S]*throw new InvalidOperationException/,
  'Job registration must retain the final exact ownership fence.',
);
assert.match(
  cookingJobRegistration,
  /IsSameCookingCollectionTarget\(job\.Target, target\)[\s\S]*!job\.ManualHandoffObserved && job\.ControllerPointer == controllerPointer/,
  'A reused controller must not delete a target-bound manual-handoff receipt.',
);
const cookerReservation = methodSource(
  cooking,
  'private static bool IsCookControllerReserved(',
);
assert.match(
  cookerReservation,
  /!job\.ManualHandoffObserved[\s\S]*ReferenceEquals\(job\.CookController, cookController\)/,
  'A passive manual-handoff receipt must not reserve an otherwise idle cooker.',
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
assert.match(
  cooking,
  /ownershipBeforeReadable[\s\S]*TryReadCookerContentState\([\s\S]*ownershipAfterReadable[\s\S]*ownershipStable/,
  'Cooking result reads must be fenced by stable ownership snapshots.',
);
const cookingJobProcessor = methodSource(
  cooking,
  'private static (bool Remove, string Message, string Code) TryProcessAutomationCookingJob(',
);
assert.match(
  cookingJobProcessor,
  /ownershipAlreadyLost[\s\S]*if \(!ownershipAlreadyLost\)[\s\S]*TryReadCookerContentState\(/,
  'A job must not inspect cooker content after the first snapshot already proves foreign ownership.',
);
assert.match(
  directDelivery,
  /ownership\.Generation == job\.Generation[\s\S]*ownership\.ContentRevision == job\.ContentRevision/,
  'Committed delivery cleanup must retain both ownership fences.',
);
assert.doesNotMatch(
  cooking,
  /InvokeInstance\([^;]*(?:"Extract"|"Store"|"FinishCooking")/,
  'Ownership recovery must not replay native cooker mutation entries.',
);
const snapshotReader = methodSource(
  cookerSnapshot,
  'private static List<PlacedCookerInfo> ReadCookSystemCookers(',
);
assert.ok(
  snapshotReader.includes('RuntimeCookerReflection.TryReadCookerControllerState(')
    && snapshotReader.includes('status = $"manager incomplete; controller=')
    && snapshotReader.includes('return new List<PlacedCookerInfo>();'),
  'Placed-cooker snapshots must fail the whole batch when any controller is unreadable.',
);
assert.doesNotMatch(
  snapshotReader,
  /single stale controller|if \(cooker == null\) continue|if \(typeIds\.Count == 0\) continue/,
  'Placed-cooker snapshots must not publish a partial controller/type projection.',
);

const exactAllCookersReader = methodSource(
  cookerReflection,
  'public static IReadOnlyList<object> ReadCookerControllersFromCookSystem(',
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
  /RuntimeConcreteCollectionReader\.TryReadDictionary\(/,
  'The shared controller reader must use the BepInEx 783 concrete dictionary reader.',
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

const publishCookerTarget = sourceSlice(cookerHighlight, 'public static void UpdateTarget(', 'public static void Tick()');
assert.ok(!/SpriteRenderer|UnityEngine|Time\.|Restore|ScanAndApply|PulseHighlightedRenderers/.test(publishCookerTarget),
  'Background cooker-target publication must only replace managed desired state.');
const reconcileCookerTarget = sourceSlice(cookerHighlight, 'public static void Tick()', 'public static void Suspend(');
assert.match(reconcileCookerTarget, /RuntimeNightBusinessLifecycle\.Snapshot/);
assert.match(reconcileCookerTarget, /ScanAndApply\(desired\)/);
assert.match(reconcileCookerTarget, /PulseHighlightedRenderers\(desired\)/);

assert.match(overlay, /AppendAutomationRuntimeEvents[\s\S]*OrderBy\(item => item\.Sequence\)[\s\S]*AppendValue\(builder, item\.Sequence\)/);
assert.match(overlay, /AppendValue\(StringBuilder builder, long value\)[\s\S]*InvariantCulture/);

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
