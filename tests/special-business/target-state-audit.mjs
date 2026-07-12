import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const service = fs.readFileSync(
  path.join(root, 'mods/bepinex/src/Save/RuntimeSpecialBusinessContextService.cs'),
  'utf8',
);

function methodBody(source, methodName) {
  const signature = new RegExp(`(?:private|public) static [^\\n=;{}]*\\b${methodName}\\(`);
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
assert.equal((service.match(/_targetKind\s*=(?!=)/g) ?? []).length, 3,
  'Target kind writes must stay confined to initialization, full reset, and the switch helper.');
assert.equal((service.match(/_targetRawChallengeType\s*=(?!=)/g) ?? []).length, 3,
  'Target owner writes must stay confined to initialization, full reset, and the switch helper.');

for (const [methodName, expectedKind, firstWrite] of [
  ['OnKoishiShieldModeChanged', '"koishi"', '_koishiShieldBroken ='],
  ['OnChallengeSpellCountUpdated', '"challenge"', '_currentSpellCount ='],
  ['UpdateTargetFund', 'kind', '_foodTargetTags ='],
  ['UpdateFoodTarget', 'kind', '_foodTargetTags ='],
  ['UpdateProgressContext', 'kind', '_targetLabel ='],
  ['UpdateTargetValue', 'kind', '_targetValue ='],
  ['UpdateTargetTime', 'kind', '_targetTimeProgress ='],
  ['UpdateTargetTagTime', 'kind', '_targetTagTimeProgress ='],
  ['UpdateKoishiClueTags', '"koishi"', '_koishiFoodPreferenceTags ='],
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
  '"Story_BloodPondHell" => "yuuma"',
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

for (const [methodName, expectedKind] of [
  ['IsActiveWackyPhase', 'koishi'],
  ['IsActiveYuyukoPhase', 'yuyuko'],
  ['TryGetActiveWackyTargetSignature', 'koishi'],
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
assert.match(snapshotBody, /challengeState\.RawChallengeType, SpecialBusinessChallengeTypes\.NotChallenge/);
assert.match(snapshotBody, /ClearTargetStateForInactiveChallenge\(\)/);

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

console.log('PASS: special-business target state is isolated by raw challenge owner and target kind.');
