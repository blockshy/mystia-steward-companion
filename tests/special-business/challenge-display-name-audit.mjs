import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const read = (relativePath) => fs.readFileSync(path.join(root, relativePath), 'utf8');

const service = read('mods/bepinex/src/Save/RuntimeSpecialBusinessContextService.cs');
const registry = read('mods/bepinex/src/Save/SpecialBusiness/SpecialBusinessContextRuleRegistry.cs');
const overlay = read('mods/bepinex/src/Ui/StewardOverlayController.cs');
const panels = read('apps/companion/src/companion/pages/service/ServiceContextPanels.tsx');

assert.match(service, /Il2CppType\.From\(challengeEnumType, false\)/);
assert.match(service, /Il2CppType\.Of<InspectorNameAttribute>\(false\)/);
assert.match(service, /Il2CppSystem\.Reflection\.CustomAttributeData\.GetCustomAttributes\(field\)/);
assert.match(service, /constructorArgument is null/);
assert.ok(!service.includes('constructorArgument == null'),
  'IL2CPP value-type proxies must not use their overloaded equality operator for null checks.');
assert.ok(!service.includes('constructorArgument.ArgumentType'),
  'InspectorName decoding must validate the actual value object instead of reflecting its declared argument type.');
assert.ok(!service.includes('Il2CppType.Of<string>'),
  'InspectorName decoding must not retain the redundant declared System.String type path.');
assert.match(service, /Il2CppClassPointerStore<string>\.NativeClassPtr/);
assert.match(service, /var valuePointer = argumentValue\.Pointer/);
assert.match(service, /var valueClass = IL2CPP\.il2cpp_object_get_class\(valuePointer\)/);
assert.match(service, /stringClass == IntPtr\.Zero \|\| valueClass != stringClass/);
assert.match(service, /IL2CPP\.Il2CppStringToManaged\(valuePointer\)/);
assert.match(service, /metadata read failed at stage=\{stage\}/);
assert.ok(!service.includes('CleanText(constructorArguments[0].Value)'),
  'InspectorName strings must be unboxed from their native IL2CPP string pointer.');
assert.ok(!service.includes('.GetCustomAttributesData()'), 'Managed proxy metadata must not be used for challenge names.');
assert.match(service, /var displayName = active \? ReadChallengeDisplayName/);
assert.match(service, /Dictionary<string, ChallengeDisplayNameResolution>/);
assert.match(service, /PermanentFailure[\s\S]*RetryableFailure/);
assert.match(service, /cached\.ShouldRetry\(now\)/);

assert.ok(!registry.includes('DisplayName'), 'Special-business rules must not own challenge display names.');
for (const oldName of [
  '常规经营',
  '妖梦科目一',
  '妖梦科目二',
  '幽幽子挑战（剧情版）',
  '幽幽子挑战（重修版）',
  '血池地狱 / 饕餮尤魔',
  '青娥料理挑战',
  '瑞灵挑战一',
]) {
  assert.ok(!registry.includes(oldName), `Rule registry still contains the removed display name: ${oldName}`);
}

assert.match(panels, /const displayName = context\.displayName \|\| context\.challengeType/);
assert.match(panels, /context\.challengeType !== displayName/);
assert.match(overlay, /specialDisplayName: \{specialBusiness\?\.DisplayName/);
assert.match(overlay, /specialSource: \{specialBusiness\?\.Source/);
assert.match(overlay, /specialError: \{specialBusiness\?\.Error/);

console.log('PASS: special-business names use cached native IL2CPP InspectorName metadata without a Mod-owned name map.');
