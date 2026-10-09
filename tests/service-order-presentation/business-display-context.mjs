import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

// 直接加载生产纯展示 helper，避免测试另写一套上下文算法掩盖字段漂移。
const source = await readFile('apps/companion/src/companion/business-display-context.ts', 'utf8');
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const { buildBusinessSourceContext: build, businessSourceContextKey: key } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);
const authority = { ok: true, registryId: 'display-test', authorityRevision: 1, activeProfileRevision: 2, activeProfileHash: 'hash-a' };
const snapshot = { automationSessionId: 'session-a', nightBusinessGeneration: 3, nightBusinessLifecyclePhase: 'Active',
  activeSceneName: 'NightScene.A', runtimeLoaded: true, runtimeDaySceneGeneration: 4, runtimeDaySceneReady: true,
  runtimeDataSignature: 'catalog-a', snapshotSignature: 'frame-a', specialBusiness: { active: true,
    challengeTypeAvailable: true, challengeType: 'WackyCookingCompetition', phase: 'Cooking',
    foodTargetTags: ['肉'], beverageTargetTags: ['水果'], requiredExtraIngredientIds: [1],
    wackyKoishiShieldBroken: false, source: 'captured-a', lastTargetUpdatedUtc: 't0', targetTimeProgress: 0.3 } };
let checks = 0;
const expect = (actual, expected, label) => { assert.equal(actual, expected, label); checks += 1; };
const base = key(build(snapshot, authority));
expect(key(build(null, authority)), '', '缺少快照不能保留显示');
expect(key(build(snapshot, null)), '', '缺少设备上下文不能保留显示');
expect(key(null), '', '缺少响应上下文不能命中');
for (const field of ['snapshotSignature', 'capturedAtUtc', 'runtimeUiPinningStatus', 'automationCookingJobs']) {
  expect(key(build({ ...snapshot, [field]: `changed-${field}` }, authority)), base, `观察变化不清空展示：${field}`);
}
for (const field of ['automationSessionId', 'nightBusinessGeneration', 'nightBusinessLifecyclePhase', 'activeSceneName',
  'runtimeLoaded', 'runtimeDaySceneGeneration', 'runtimeDaySceneReady', 'runtimeDataSignature']) {
  expect(key(build({ ...snapshot, [field]: `changed-${field}` }, authority)) === base, false, `边界变化清空：${field}`);
}
for (const field of ['registryId', 'authorityRevision', 'activeProfileRevision', 'activeProfileHash']) {
  expect(key(build(snapshot, { ...authority, [field]: `changed-${field}` })) === base, false, `配置变化清空：${field}`);
}
for (const field of ['source', 'lastTargetUpdatedUtc', 'targetTimeProgress', 'targetTagTimeProgress', 'currentValue', 'error']) {
  expect(key(build({ ...snapshot, specialBusiness: { ...snapshot.specialBusiness, [field]: 0.8 } }, authority)), base,
    `连续观察或当前无关数值不清空：${field}`);
}
for (const field of ['foodTargetTags', 'beverageTargetTags', 'requiredExtraIngredientIds', 'phase', 'yuumaFoodTargetRevision']) {
  expect(key(build({ ...snapshot, specialBusiness: { ...snapshot.specialBusiness, [field]: ['changed'] } }, authority)) === base,
    false, `特殊经营语义变化清空：${field}`);
}
const broken = { ...snapshot, specialBusiness: { ...snapshot.specialBusiness, wackyKoishiShieldBroken: true, currentValue: 1 } };
expect(key(build({ ...broken, specialBusiness: { ...broken.specialBusiness, currentValue: 2 } }, authority)) === key(build(broken, authority)),
  false, '破防阶段伤害变化属于展示语义');
const value = build(snapshot, authority);
expect(key({ authority: value.authority, snapshot: Object.fromEntries(Object.entries(value.snapshot).reverse()) }), base, 'JSON 属性顺序不影响匹配');
console.log(`PASS: ${checks} display context boundary checks.`);
