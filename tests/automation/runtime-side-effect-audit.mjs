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
const matching = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.OrderMatching.cs');
const service = read('mods/bepinex/src/Save/RuntimeOrderPreparationService.cs');
const overlay = read('mods/bepinex/src/Ui/StewardOverlayController.cs');

assert.ok(!cooking.includes('"FinishCooking"'), 'The Mod must not invoke the non-idempotent FinishCooking entry.');
assert.ok(!lifecycle.includes('FinalizeOwnedResult'), 'The cooking tracker still exposes the removed active-finalize directive.');
assert.match(lifecycle, /"cooking-native-finalize-waiting"[\s\S]*AutomationCookingJobDirective\.None/);

assert.match(cooking, /parameters\.Length == 2[\s\S]*parameters\[0\]\.ParameterType == typeof\(int\)[\s\S]*parameters\[1\]\.ParameterType == typeof\(bool\)/);
assert.match(cooking, /method\.Invoke\(null, new object\?\[\] \{ itemId, false \}\)/);
assert.ok(!cooking.includes('CopyNormalOrderRequestWithoutOrderKey'));
assert.ok(!matching.includes('CopyNormalOrderRequestWithoutOrderKey'));
assert.ok(!matching.includes('orderKeyFallback'));

assert.match(directDelivery, /parameters\.Length == 2[\s\S]*parameters\[1\]\.ParameterType == typeof\(int\)/);
assert.match(directDelivery, /methods\[0\]\.Invoke\(configure, new object\?\[\] \{ cookedFood, -1 \}\)/);
assert.ok(!directDelivery.includes('TryBuildStoreFoodArguments'));
assert.match(directDelivery, /storedAfterException[\s\S]*AutomationCommitResolution\.Committed[\s\S]*AutomationCommitResolution\.Uncertain/);

assert.match(delivery, /TryReadRuntimeOrderEvaluated[\s\S]*InvokeInstance\(manager, methodName, args\)[\s\S]*OrderEvaluationCommitUncertain/);
assert.match(delivery, /TryInvokeDeliverySetter[\s\S]*out bool invocationAttempted/);
assert.match(delivery, /writtenInAirItem == null[\s\S]*return UncertainDelivery/);
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

assert.match(overlay, /AppendAutomationRuntimeEvents[\s\S]*OrderBy\(item => item\.Sequence\)[\s\S]*AppendValue\(builder, item\.Sequence\)/);
assert.match(overlay, /AppendValue\(StringBuilder builder, long value\)[\s\S]*InvariantCulture/);

console.log('PASS: runtime automation uses exact native signatures, one-shot side-effect boundaries, passive start receipts, and authoritative safety barriers.');
