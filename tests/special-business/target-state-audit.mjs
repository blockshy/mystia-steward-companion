import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const service = fs.readFileSync(
  path.join(root, 'mods/bepinex/src/Save/RuntimeSpecialBusinessContextService.cs'),
  'utf8',
);
const ids = fs.readFileSync(
  path.join(root, 'mods/bepinex/src/Save/SpecialBusiness/SpecialBusinessIds.cs'),
  'utf8',
);
const classifier = fs.readFileSync(
  path.join(root, 'mods/bepinex/src/Save/SpecialBusiness/SpecialBusinessOrderClassifier.cs'),
  'utf8',
);
const yuumaModule = fs.readFileSync(
  path.join(root, 'mods/bepinex/src/Save/SpecialBusiness/YuumaChallengeOrderModule.cs'),
  'utf8',
);
const diagnostics = fs.readFileSync(
  path.join(root, 'mods/bepinex/src/Save/SpecialBusiness/SpecialBusinessDiagnostics.cs'),
  'utf8',
);
const ruleRegistry = fs.readFileSync(
  path.join(root, 'mods/bepinex/src/Save/SpecialBusiness/SpecialBusinessContextRuleRegistry.cs'),
  'utf8',
);
const models = fs.readFileSync(
  path.join(root, 'mods/bepinex/src/Core/Models.cs'),
  'utf8',
);

function methodBody(source, methodName) {
  const signature = new RegExp(`(?:private|public|internal) static [^\\n=;{}]*\\b${methodName}\\(`);
  const match = signature.exec(source);
  assert.ok(match, `Method not found: ${methodName}`);

  const bodyStart = source.indexOf('{', match.index);
  assert.ok(bodyStart >= 0, `Method body not found: ${methodName}`);

  let depth = 0;
  let quote = '';
  let escaped = false;
  for (let index = bodyStart; index < source.length; index += 1) {
    const char = source[index];
    if (quote) {
      if (escaped) {
        escaped = false;
      } else if (char === '\\') {
        escaped = true;
      } else if (char === quote) {
        quote = '';
      }
      continue;
    }

    if (char === '"' || char === "'") {
      quote = char;
      continue;
    }
    if (char === '{') depth += 1;
    if (char !== '}') continue;
    depth -= 1;
    if (depth === 0) return source.slice(bodyStart, index + 1);
  }

  assert.fail(`Unterminated method body: ${methodName}`);
}

const resetBody = methodBody(service, 'ResetTargetStateLocked');
for (const assignment of [
  '_targetRawChallengeType = "";',
  '_targetKind = "";',
  '_foodTargetTags = Array.Empty<string>();',
  '_targetFund = null;',
  '_targetLabel = "";',
  '_phase = "";',
  '_currentValue = null;',
  '_maxValue = null;',
  '_targetValue = null;',
  '_currentAnger = null;',
  '_maxAnger = null;',
  '_targetAnger = null;',
  '_targetTimeProgress = null;',
  '_targetTagTimeProgress = null;',
  '_koishiShieldBroken = null;',
  '_koishiFoodPreferenceTags = Array.Empty<string>();',
  '_koishiFoodHateTags = Array.Empty<string>();',
  '_koishiBeveragePreferenceTags = Array.Empty<string>();',
  '_currentSpellCount = null;',
  '_targetSpellCount = null;',
  '_lastTargetUpdatedUtc = null;',
]) {
  assert.ok(resetBody.includes(assignment), `Target reset is missing: ${assignment}`);
}

const switchBody = methodBody(service, 'SwitchTargetContextLocked');
assert.match(switchBody, /TargetContextMatchesLocked\(rawChallengeType, kind\)/);
assert.match(switchBody, /return;/);
assert.match(switchBody, /ResetTargetStateLocked\(\)/);
assert.match(switchBody, /_targetRawChallengeType = rawChallengeType/);
assert.match(switchBody, /_targetKind = kind/);
assert.match(switchBody, /_targetBusinessGeneration = RuntimeNightBusinessLifecycle\.Generation/);
assert.match(
  methodBody(service, 'TargetContextMatchesLocked'),
  /_targetBusinessGeneration == RuntimeNightBusinessLifecycle\.Generation/,
);
assert.equal((service.match(/_targetKind\s*=(?!=)/g) ?? []).length, 3,
  'Target kind writes must stay confined to initialization, full reset, and the switch helper.');
assert.equal((service.match(/_targetRawChallengeType\s*=(?!=)/g) ?? []).length, 3,
  'Target owner writes must stay confined to initialization, full reset, and the switch helper.');

for (const [wrapperName, captureName] of [
  ['OnKoishiShieldModeChanged', 'CaptureKoishiShieldModeChanged'],
  ['OnChallengeSpellCountUpdated', 'CaptureChallengeSpellCountUpdated'],
]) {
  const body = methodBody(service, wrapperName);
  assert.ok(body.includes('RunCaptureCallback('), `${wrapperName} must isolate diagnostics from the game callback.`);
  assert.ok(body.includes(`${captureName}(`), `${wrapperName} must delegate to ${captureName}.`);
}

for (const [wrapperName, updaterName] of [
  ['OnYuumaTargetTagSet', 'UpdateYuumaFoodTarget'],
  ['OnYuumaContextSet', 'UpdateProgressContext'],
  ['OnYuumaTargetProgressSet', 'UpdateTargetValue'],
  ['OnYuumaAngerProgressSet', 'UpdateYuumaTargetAnger'],
  ['OnYuumaTargetTimeSet', 'UpdateTargetTime'],
  ['OnYuumaTargetProgressImmediate', 'UpdateYuumaImmediateProgress'],
]) {
  const body = methodBody(service, wrapperName);
  assert.ok(
    body.includes('RunCaptureCallback('),
    `${wrapperName} must isolate diagnostics from the native HUD callback.`,
  );
  assert.ok(body.includes(`${updaterName}(`), `${wrapperName} must delegate to ${updaterName}.`);
}

for (const [methodName, expectedKind, firstWrite] of [
  ['CaptureKoishiShieldModeChanged', '"koishi"', '_koishiShieldBroken ='],
  ['CaptureChallengeSpellCountUpdated', '"challenge"', '_currentSpellCount ='],
  ['UpdateTargetFund', 'kind', '_foodTargetTags ='],
  ['UpdateFoodTarget', 'kind', '_foodTargetTags ='],
  ['UpdateProgressContext', 'kind', '_targetLabel ='],
  ['UpdateTargetValue', 'kind', '_targetValue ='],
  ['UpdateTargetTime', 'kind', '_targetTimeProgress ='],
  ['UpdateTargetTagTime', 'kind', '_targetTagTimeProgress ='],
  ['UpdateKoishiClueTags', '"koishi"', '_koishiFoodPreferenceTags ='],
  ['UpdateYuumaFoodTarget', '"yuuma"', '_foodTargetTags ='],
  ['UpdateYuumaTargetAnger', '"yuuma"', '_targetAnger ='],
  ['UpdateYuumaImmediateProgress', '"yuuma"', '_targetValue ='],
]) {
  const body = methodBody(service, methodName);
  const ownerReadIndex = body.indexOf(`TryReadTargetOwner(${expectedKind}, out var rawChallengeType)`);
  const switchIndex = body.indexOf('SwitchTargetContextLocked(');
  const firstWriteIndex = body.indexOf(firstWrite);
  assert.ok(ownerReadIndex >= 0, `${methodName} must validate the raw challenge owner and callback kind.`);
  assert.ok(switchIndex > ownerReadIndex, `${methodName} must pass the raw challenge owner to the switch helper.`);
  assert.ok(firstWriteIndex > switchIndex, `${methodName} must isolate state before writing target fields.`);
}

const ownerReadBody = methodBody(service, 'TryReadTargetOwner');
assert.match(ownerReadBody, /ReadRawChallengeType\(out var error\)/);
assert.match(ownerReadBody, /string\.IsNullOrWhiteSpace\(error\)/);
assert.match(ownerReadBody, /string\.Equals\(GetExpectedTargetKind\(rawChallengeType\), expectedKind, StringComparison\.Ordinal\)/);

const expectedKindBody = methodBody(service, 'GetExpectedTargetKind');
for (const mapping of [
  'SpecialBusinessChallengeTypes.BloodPondHell => "yuuma"',
  'SpecialBusinessChallengeTypes.WackyCookingCompetition => "koishi"',
  '"Story_Basic" => "challenge"',
  '"Story_Advanced" => "challenge"',
  'SpecialBusinessChallengeTypes.StoryYuyuko => "yuyuko"',
  'SpecialBusinessChallengeTypes.RetakeYuyuko => "yuyuko"',
  '"Story_Seiga_TempleCuisineCompetition" => "mausoleum"',
  '"Story_Futo_TempleCuisineCompetition" => "mausoleum"',
  '"Story_Tochiko_TempleCuisineCompetition" => "mausoleum"',
]) {
  assert.ok(expectedKindBody.includes(mapping), `Target kind mapping is missing: ${mapping}`);
}
assert.match(expectedKindBody, /_ => ""/);

const contextMatchesBody = methodBody(service, 'TargetContextMatchesLocked');
assert.match(contextMatchesBody, /string\.Equals\(_targetRawChallengeType, rawChallengeType, StringComparison\.Ordinal\)/);
assert.match(contextMatchesBody, /string\.Equals\(_targetKind, kind, StringComparison\.Ordinal\)/);
assert.match(contextMatchesBody, /_targetBusinessGeneration == RuntimeNightBusinessLifecycle\.Generation/);

for (const [methodName, expectedKind] of [
  ['IsActiveWackyPhase', 'koishi'],
  ['IsActiveYuyukoPhase', 'yuyuko'],
]) {
  const body = methodBody(service, methodName);
  assert.ok(
    body.includes(`TryReadTargetOwner("${expectedKind}", out var rawChallengeType)`),
    `${methodName} must validate the current raw challenge before reading captured state.`,
  );
  assert.ok(
    body.includes(`TargetContextMatchesLocked(rawChallengeType, "${expectedKind}")`),
    `${methodName} must reject captured state owned by another raw challenge.`,
  );
}

const specialFoodPolicyBody = methodBody(service, 'TryGetActiveSpecialFoodTargetPolicy');
assert.match(specialFoodPolicyBody, /ReadRawChallengeType\(out var error\)/);
assert.match(
  specialFoodPolicyBody,
  /SpecialBusinessChallengeTypes\.WackyCookingCompetition => SpecialFoodTargetMatchMode\.Any/,
);
assert.match(
  specialFoodPolicyBody,
  /SpecialBusinessChallengeTypes\.BloodPondHell => SpecialFoodTargetMatchMode\.All/,
);
assert.match(
  specialFoodPolicyBody,
  /TargetContextMatchesLocked\(rawChallengeType, owner\)/,
);
assert.match(
  specialFoodPolicyBody,
  /SpecialBusinessChallengeTypes\.BloodPondHell, StringComparison\.Ordinal\)[\s\S]*normalized\.Length != 2/,
);
assert.match(
  specialFoodPolicyBody,
  /SpecialFoodTargetPolicy\.CreateActive\([\s\S]*_targetBusinessGeneration/,
);

const shieldProperty = service.slice(
  service.indexOf('public static bool IsWackyKoishiShieldBroken'),
  service.indexOf('public static bool IsActiveYuyukoPhase'),
);
assert.match(shieldProperty, /TryReadTargetOwner\("koishi", out var rawChallengeType\)/);
assert.match(shieldProperty, /TargetContextMatchesLocked\(rawChallengeType, "koishi"\)/);

const rawReadBody = methodBody(service, 'ReadRawChallengeType');
assert.match(rawReadBody, /if \(value is null\)/);
assert.match(rawReadBody, /error = "NightSceneDirector\.ChallengeMode value not found"/);
assert.match(rawReadBody, /if \(text\.Length == 0\)/);
assert.match(rawReadBody, /error = "NightSceneDirector\.ChallengeMode value is empty"/);
assert.match(rawReadBody, /return NormalizeChallengeTypeText\(text\)/);

const inactiveResetBody = methodBody(service, 'ClearTargetStateForInactiveChallenge');
assert.match(inactiveResetBody, /_targetRawChallengeType\.Length == 0 && _targetKind\.Length == 0\) return/);
assert.match(inactiveResetBody, /ResetTargetStateLocked\(\)/);
assert.match(inactiveResetBody, /_changeVersion\+\+/);
const snapshotBody = methodBody(service, 'Snapshot');
assert.match(snapshotBody, /challengeState\.RawChallengeTypeAvailable/);
assert.match(snapshotBody, /ClearTargetStateForInactiveChallenge\(\)/);
assert.match(snapshotBody, /ClearTargetStateForUnavailableChallenge\(\)/);
assert.match(snapshotBody, /var active = challengeState\.RawChallengeTypeAvailable && IsActiveChallenge\(challengeType\)/);
assert.match(snapshotBody, /ChallengeTypeAvailable = challengeState\.RawChallengeTypeAvailable/);
assert.match(models, /public bool ChallengeTypeAvailable \{ get; init; \}/);

const currentChallengeBody = methodBody(service, 'TryGetCurrentChallengeType');
assert.match(currentChallengeBody, /return state\.RawChallengeTypeAvailable/);
assert.match(classifier, /if \(!RuntimeSpecialBusinessContextService\.TryGetCurrentChallengeType/);
assert.match(classifier, /SpecialBusinessOrderRoles\.ContextUnavailable/);
assert.match(classifier, /SpecialBusinessModuleRegistry\.Blocked/);

assert.ok(!service.includes('set_ChallengeMode'),
  'Challenge isolation must not depend on the IL2CPP-inlined ChallengeMode setter.');
assert.ok(!service.includes('OnChallengeModeChanged'),
  'The ineffective ChallengeMode setter postfix must not be retained.');

const phaseResetBody = methodBody(service, 'ResetTransientStateForContextLocked');
assert.match(phaseResetBody, /IsKoishiPhaseThreeLocked\(phase\)/);
assert.match(phaseResetBody, /_koishiFoodPreferenceTags = Array\.Empty<string>\(\)/);

const readTargetBody = methodBody(service, 'ReadTargetForChallenge');
assert.ok(!readTargetBody.includes('ResetTargetStateLocked'),
  'Target reads must remain side-effect free.');
assert.match(readTargetBody, /GetExpectedTargetKind\(challengeType\)/);
assert.match(readTargetBody, /TargetContextMatchesLocked\(rawChallengeType, expectedKind\)/);

const captureStatusBody = methodBody(service, 'BuildCaptureStatus');
assert.match(captureStatusBody, /target\.Source\.Length == 0/);
assert.match(methodBody(service, 'BuildSource'), /BuildCaptureStatus\(target\)/);

const attachBody = methodBody(service, 'TryAttach');
for (const contract of [
  ['SetTargetTag', 'new[] { typeof(string), typeof(string), typeof(bool) }', 'OnYuumaTargetTagSet'],
  ['SetContext', 'new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(Il2CppSystem.Action) }', 'OnYuumaContextSet'],
  ['SetTargetProgress', 'new[] { typeof(int) }', 'OnYuumaTargetProgressSet'],
  ['SetAngerProgress', 'new[] { typeof(int) }', 'OnYuumaAngerProgressSet'],
  ['SetTargetTime', 'new[] { typeof(float) }', 'OnYuumaTargetTimeSet'],
  ['SetTargetProgressImmediate', 'new[] { typeof(int), typeof(int) }', 'OnYuumaTargetProgressImmediate'],
]) {
  const [methodName, parameterTypes, postfixName] = contract;
  const methodIndex = attachBody.indexOf(`"${methodName}"`);
  assert.ok(methodIndex >= 0, `Yuuma HUD hook is missing: ${methodName}.`);
  const contractSlice = attachBody.slice(methodIndex, methodIndex + 360);
  assert.ok(
    contractSlice.includes(parameterTypes),
    `Yuuma HUD hook ${methodName} must use the exact managed parameter types.`,
  );
  assert.ok(
    contractSlice.includes(`nameof(${postfixName})`),
    `Yuuma HUD hook ${methodName} must use the passive ${postfixName} postfix.`,
  );
}

const yuumaTargetBody = methodBody(service, 'UpdateYuumaFoodTarget');
assert.match(
  yuumaTargetBody,
  /var complete = firstTag\.Length > 0[\s\S]*&& secondTag\.Length > 0[\s\S]*&& normalized\.Length == 2/,
);
assert.match(yuumaTargetBody, /Distinct\(StringComparer\.Ordinal\)/);
assert.match(yuumaTargetBody, /OrderBy\(tag => tag, StringComparer\.Ordinal\)/);
assert.match(
  yuumaTargetBody,
  /if \(complete[\s\S]*!string\.Equals\([\s\S]*_yuumaFoodTargetIdentity[\s\S]*identity[\s\S]*StringComparison\.Ordinal\)[\s\S]*_yuumaFoodTargetRevision\+\+[\s\S]*_yuumaFoodTargetIdentity = identity/,
);
assert.match(
  yuumaTargetBody,
  /_foodTargetTags = complete \? normalized : Array\.Empty<string>\(\)/,
);
assert.match(yuumaTargetBody, /AppendYuumaSnapshot/);

const yuumaTargetStateBody = methodBody(service, 'TryGetActiveYuumaFoodTargetState');
assert.match(yuumaTargetStateBody, /lock \(SyncRoot\)/);
assert.match(yuumaTargetStateBody, /_targetBusinessGeneration != generation/);
assert.match(yuumaTargetStateBody, /NormalizeFoodTargetTagsLocked\(\)/);
assert.match(yuumaTargetStateBody, /normalized\.Length != 2/);
assert.match(yuumaTargetStateBody, /_yuumaFoodTargetRevision <= 0/);
assert.match(yuumaTargetStateBody, /revision = _yuumaFoodTargetRevision/);

for (const declaration of [
  'public const string BloodPondHell = "Story_BloodPondHell";',
  'public const int YuumaBoss = 1003;',
  'public const string YuumaBoss = "yuuma-boss-order";',
  'public const string YuumaUnverified = "yuuma-order-unverified";',
]) {
  assert.ok(ids.includes(declaration), `Central Yuuma identity is missing: ${declaration}`);
}
assert.equal(
  ([
    service,
    ruleRegistry,
    yuumaModule,
  ].join('\n').match(/"Story_BloodPondHell"/g) ?? []).length,
  0,
  'Blood Pond Hell consumers must use the centralized challenge constant.',
);

for (const requiredIdentityContract of [
  'NormalOrderTypeName = "NightScene.GuestManagementUtility.GuestsManager+NormalOrder"',
  'SpecialOrderTypeName = "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder"',
  'BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly',
  '"GameData.Core.Collections.NightSceneUtility.GuestBase"',
  'idProperty.PropertyType != typeof(int)',
  'controllerGuestId != orderGuestId',
  'identity.OrderGuestId != SpecialBusinessGuestIds.YuumaBoss',
]) {
  assert.ok(
    yuumaModule.includes(requiredIdentityContract),
    `Yuuma order identity must retain exact runtime evidence: ${requiredIdentityContract}`,
  );
}
for (const forbiddenIdentityFallback of [
  '.IsGuest(',
  'guest.Text',
  '"Yuuma"',
  '"Toutetsu"',
  '"饕餮"',
  '"尤魔"',
]) {
  assert.equal(
    yuumaModule.includes(forbiddenIdentityFallback),
    false,
    `Yuuma identity must not restore a name/text fallback: ${forbiddenIdentityFallback}`,
  );
}
assert.match(
  yuumaModule,
  /SpecialBusinessModuleRegistry\.AllowedSpecialOrder\([\s\S]*SpecialBusinessOrderRoles\.YuumaBoss/,
);
assert.match(
  yuumaModule,
  /SpecialBusinessOrderRoles\.YuumaUnverified[\s\S]*已阻止自动化接管/,
);

for (const requiredDiagnosticContract of [
  'AppendSnapshot("special-business.yuuma"',
  'AppendProgressSnapshot("special-business.yuuma"',
  'MaxSeenOnceKeys = 512',
  'MaxProgressKeys = 256',
  'SpecialBusinessDiagnostics.Reset();',
  'targetOwnerGeneration:',
  'orderGuestId:',
  'controllerGuestId:',
]) {
  assert.ok(
    `${diagnostics}\n${service}`.includes(requiredDiagnosticContract),
    `Bounded Yuuma diagnostics are missing: ${requiredDiagnosticContract}`,
  );
}

for (const forbiddenSideEffectCall of [
  'MainChallengeLoop(',
  'OverrideEvaluationCallback(',
  'AttackYuuma(',
  'AddAnger(',
  'EvaulateManualOrder(',
  'EvaluateOrder(',
]) {
  assert.equal(
    `${service}\n${yuumaModule}\n${diagnostics}`.includes(forbiddenSideEffectCall),
    false,
    `Initial Yuuma capture must remain passive: ${forbiddenSideEffectCall}`,
  );
}

assert.match(ruleRegistry, /SpecialBusinessChallengeTypes\.BloodPondHell\] = BloodPondHell\(\)/);
const bloodPondRule = methodBody(ruleRegistry, 'BloodPondHell');
assert.match(bloodPondRule, /同时命中两个目标 Tag/);
assert.match(bloodPondRule, /BOSS 身份、经营代际、原订单、双 Tag 目标、实际成品和实时订单均严格复核/);
assert.match(bloodPondRule, /酒水先送达/);
assert.match(bloodPondRule, /料理送达与订单完成均开启时按订单原生路由精确结算/);
assert.match(bloodPondRule, /否则保留成品等待玩家处理/);
assert.doesNotMatch(bloodPondRule, /固定进入手动交接|通用评价|EvaluateOrder/);

console.log(
  'PASS: special-business target state is generation-scoped and Blood Pond Hell uses exact passive HUD, controlled automation, fail-closed identity, and bounded diagnostic contracts.',
);
