import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 审计当前 Flutter 生产源，防止把冻结 TS oracle、旧候选上传路由或业务计算重新接回客户端。
const root = fileURLToPath(new URL('../../', import.meta.url));
const list = directory => readdirSync(directory, { withFileTypes: true }).flatMap(entry => entry.isDirectory()
  ? list(path.join(directory, entry.name)) : [path.join(directory, entry.name)]);
const files = ['apps/companion-flutter/lib', 'packages/companion_client/lib', 'packages/companion_protocol/lib']
  .flatMap(directory => list(path.join(root, directory))).filter(file => file.endsWith('.dart'));
const retiredNames = /\b(?:buildRareFoodCandidates|buildRareBeverageCandidates|buildRareOrderPlans|buildNormalFoodRecommendations|normalizePrimaryExecutionPlans|selectSpecialBusinessNormalExecutionTarget|reduceAutomationStageOutcome|prepareNextRareOrder|completeFirstRareOrder|publishGameUiTargets)\b/;
for (const file of files) {
  const source = readFileSync(file, 'utf8');
  const name = path.relative(root, file);
  assert.doesNotMatch(source, /(?:import|export)\s+['"][^'"]*(?:reference-client|reference\/apps)/, `${name} 引入冻结测试参考。`);
  assert.doesNotMatch(source, retiredNames, `${name} 必须消费服务端结果，不应生成业务决策。`);
  assert.doesNotMatch(source, /\/orders\/(?:prepare-next|complete-first|normal\/complete-first)|\/ui-pinning\/targets/,
    `${name} 恢复已删除的执行候选上传端点。`);
}
const api = readFileSync(path.join(root, 'packages/companion_client/lib/src/api.dart'), 'utf8');
assert.ok(api.includes("'/business/page-session/open'") && api.includes("'/business/page-session/update'") && api.includes("'/business/status'"), '新客户端缺少可撤销页面订阅或权威业务读取入口。');
assert.ok(!api.includes("'/business/query'"), '旧的无期限页面查询路由不应保留。');
const project = readFileSync(path.join(root, 'tests/csharp-recommendations/CSharpRecommendations.csproj'), 'utf8');
assert.match(project, /ProjectReference[^>]+MystiaStewardCompanion\.Business\.csproj/);
assert.doesNotMatch(project, /Compile Include/, '差分必须调用实际生产程序集。');
console.log(`PASS Flutter client business boundary: ${files.length} Dart files; no TS fallback or retired command producer.`);
