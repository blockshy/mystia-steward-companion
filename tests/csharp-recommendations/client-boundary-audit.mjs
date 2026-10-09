import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

// 这是当前生产边界检查，独立于 tests/reference 的历史行为断言。
// 目的在于阻止未来误把旧 TS 算法、Worker 或可上传执行候选的旧协议重新接回客户端。
const root = fileURLToPath(new URL('../../', import.meta.url));
const client = path.join(root, 'apps/companion/src');
const list = directory => readdirSync(directory, { withFileTypes: true }).flatMap(entry => entry.isDirectory()
  ? list(path.join(directory, entry.name)) : [path.join(directory, entry.name)]);
const files = list(client).filter(file => /\.[cm]?tsx?$/.test(file));
const retiredNames = /\b(?:buildRareFoodCandidates|buildRareBeverageCandidates|buildRareOrderPlans|buildNormalFoodRecommendations|buildNormalBeverageRecommendations|normalizePrimaryExecutionPlans|selectSpecialBusinessNormalExecutionTarget|reduceAutomationStageOutcome|prepareNextRareOrder|completeFirstRareOrder|completeFirstNormalOrder|publishGameUiTargets)\b/;
for (const file of files) {
  const text = readFileSync(file, 'utf8');
  const name = path.relative(root, file);
  assert.doesNotMatch(text, /(?:from\s*|import\s*\()['"][^'"]*(?:tests\/reference|reference\/apps)/, `${name} must not import the historical oracle.`);
  assert.doesNotMatch(text, /new\s+Worker\s*\(/, `${name} must not restore client business Workers.`);
  assert.doesNotMatch(text, retiredNames, `${name} must consume server results instead of client business algorithms.`);
}
for (const relative of ['companion/automation-state.ts', 'companion/automation-machine.ts', 'companion/domain/game-ui-targets.ts', 'companion/domain/primary-execution-plan.ts', 'recommendation-engine/rare-orders.ts', 'recommendation-engine/tag-resolution.ts']) {
  assert.equal(existsSync(path.join(client, relative)), false, `${relative} must remain removed from production.`);
}
const api = readFileSync(path.join(client, 'companion/api.ts'), 'utf8');
assert.doesNotMatch(api, /\/orders\/(?:prepare-next|complete-first|normal\/complete-first)|\/ui-pinning\/targets/, 'The client must not expose retired command builders.');
const page = readFileSync(path.join(client, 'companion/hooks/usePageRecommendations.ts'), 'utf8');
const status = readFileSync(path.join(client, 'companion/hooks/useBusinessStatus.ts'), 'utf8');
assert.match(page, /\/business\/query/);
assert.match(status, /\/business\/status/);
const testProject = readFileSync(path.join(root, 'tests/csharp-recommendations/CSharpRecommendations.csproj'), 'utf8');
assert.match(testProject, /ProjectReference[^>]+MystiaStewardCompanion\.Business\.csproj/);
assert.doesNotMatch(testProject, /Compile Include/, 'Differential tests must run the actual production assembly.');
// 冻结内容按 SHA-256 校验；测试不得悄悄改 oracle 来追随新实现，从而把双边同错伪装成一致。
const manifest = JSON.parse(readFileSync(path.join(root, 'tests/reference/manifest.json'), 'utf8'));
assert.equal(manifest.revision, '098f414a4ddacc2471e3ca04119f13e2f0733ba8');
const frozenFiles = list(path.join(root, 'tests/reference/apps')).map(file => path.relative(path.join(root, 'tests/reference'), file).replaceAll('\\', '/')).sort();
assert.deepEqual(frozenFiles, [...manifest.files].sort(), 'Every frozen source file must be covered by the oracle manifest.');
for (const file of manifest.files) {
  const bytes = readFileSync(path.join(root, 'tests/reference', file));
  assert.equal(createHash('sha256').update(bytes).digest('hex'), manifest.sha256[file], `Frozen oracle changed: ${file}`);
}
console.log(`PASS current client business boundary: ${files.length} source files; no TS decision fallback or retired command producer.`);
