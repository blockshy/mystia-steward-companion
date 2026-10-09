import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';

// 加载冻结的 main 基线：生产前端删除状态机后，oracle 仍独立存在，避免拿新实现验证自身。
const server = await createServer({ configFile: fileURLToPath(new URL('../reference/vite.config.ts', import.meta.url)) });
try {
  const machine = await server.ssrLoadModule('/src/companion/automation-machine.ts');
  const states = await server.ssrLoadModule('/src/companion/automation-state.ts');
  const vectors = [];
  const expected = [];
  const outcomeNames = ['', 'waiting', 'progressed', 'completed', 'interrupted', 'retryable-failure', 'blocked', 'fatal'];
  const responseStages = ['', 'validation', 'beverage', 'cooking-start', 'cooking-delivery', 'order'];
  // 交叉覆盖旧失败阶段、重试次数、已有暂停、结构化结果与服务端阶段优先级。
  for (const retryStage of ['', 'ensure-beverage', 'ensure-cooking']) {
    for (const retryCount of [0, 2]) for (const paused of [false, true]) {
      for (const outcome of outcomeNames) for (const stage of responseStages) for (const stopOnError of [false, true]) {
        const state = { ...states.emptyNormalAutoOrderState('oracle', 100), retryStage, retryCount, paused,
          nextAttemptAtMs: 900, lastError: '旧失败', pausedStage: paused ? 'ensure-cooking' : '' };
        const response = { ok: outcome === 'progressed' || outcome === 'completed', error: '', steps: [],
          automation: { outcome, stage, reasonCode: '', retryAfterMs: 650, jobId: '' } };
        const vector = { operation: 'outcome', state, response, stage: 'ensure-cooking', now: 1000, stopOnError, maxRetries: 3 };
        vectors.push(vector);
        expected.push(states.updateAutomationAfterResponse(state, response, 1000, 'ensure-cooking', stopOnError, 3));
      }
    }
  }
  // 不确定副作用和普通暂停的区别必须保持；人工确认不能被重试或目标轮换替代。
  for (const reason of ['cooking-delivery-commit-uncertain', 'order-evaluation-commit-uncertain', 'mizuchi-contract-mismatch']) {
    for (const existingManual of [false, true]) {
      const state = { ...states.emptyNormalAutoOrderState('oracle', 100), manualResolutionRequired: existingManual,
        paused: existingManual, pauseReasonCode: existingManual ? 'old-manual' : '', pausedStage: existingManual ? 'deliver-food' : '' };
      const response = { ok: false, error: '安全屏障', steps: [], automation: { outcome: 'blocked', stage: 'cooking-delivery', reasonCode: reason, retryAfterMs: 0, jobId: 'job-1' } };
      vectors.push({ operation: 'outcome', state, response, stage: 'ensure-cooking', now: 1000, stopOnError: true, maxRetries: 3 });
      expected.push(states.updateAutomationAfterResponse(state, response, 1000, 'ensure-cooking', true, 3));
    }
  }
  for (const manualResolutionRequired of [false, true]) for (const pauseReasonCode of ['transport-failure', 'rollback-limit-reached']) {
    for (const signature of ['', 'old', 'new']) for (const revision of [1, 2]) {
      const state = { ...states.emptyNormalAutoOrderState('oracle', 100), paused: true, step: 'paused', pausedStage: 'ensure-cooking',
        rollbackCount: 3, rollbackTargetSignature: 'old', rollbackTargetRevision: 1, manualResolutionRequired, pauseReasonCode };
      vectors.push({ operation: 'target', state, signature, revision, now: 1000 });
      expected.push(machine.reconcileAutomationRollbackTarget(state, signature, revision, 1000).state);
    }
  }
  const runner = spawnSync('dotnet', [fileURLToPath(new URL('./bin/Release/net6.0/CSharpAutomationSmoke.dll', import.meta.url)), '--oracle'],
    { input: JSON.stringify(vectors), encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, windowsHide: true });
  assert.equal(runner.status, 0, runner.stderr);
  const actual = JSON.parse(runner.stdout);
  assert.equal(actual.length, expected.length);
  for (let index = 0; index < expected.length; index++) {
    // C# 状态是稀客与普客的联合超集，比较旧状态拥有的字段，额外契约字段不影响旧语义。
    const projected = Object.fromEntries(Object.keys(expected[index]).map((key) => [key, actual[index][key]]));
    assert.deepEqual(projected, expected[index], `状态转换差异 vector #${index}: ${JSON.stringify(vectors[index])}`);
  }
  console.log(`PASS csharp-automation differential: ${vectors.length} vectors against frozen TypeScript main baseline.`);
} finally {
  await server.close();
}
