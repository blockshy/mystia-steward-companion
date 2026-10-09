import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

// 此用例只驱动显式启用场景控制的 Node mock。业务 status/query 不拦截、不伪造，
// 全部结果来自 source-link C# 宿主及生产生命周期校验器；没有真实游戏执行权限。
const api = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const app = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4174/';
const output = process.env.BUSINESS_DAYTIME_OUTPUT_DIR || 'output/playwright/business-daytime';
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true,
  ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
const statuses = [];
const queries = [];
const checks = [];
page.on('response', response => {
  if (!response.url().startsWith(api)) return;
  const route = new URL(response.url()).pathname;
  if (!['/business/status', '/business/query'].includes(route) || !response.ok() || response.request().method() === 'OPTIONS') return;
  void response.json().then(body => {
    const source = body.sourceContext?.snapshot;
    const observed = { current: body.isCurrent, phase: source?.nightBusinessLifecyclePhase,
      generation: source?.nightBusinessGeneration, dayGeneration: source?.runtimeDaySceneGeneration, error: body.error ?? null };
    if (route === '/business/status') statuses.push({ ...observed, runtimeSets: Boolean(body.runtimeSets),
      recipeIds: body.runtimeSets?.recipeIds?.length ?? 0, recommendations: body.recommendations?.recommendations?.length ?? 0,
      rareUi: Boolean(body.gameUiTargets?.rare), normalUi: Boolean(body.gameUiTargets?.normal) });
    else queries.push({ ...observed, kind: body.result?.kind, recipes: body.result?.recipes?.length ?? 0 });
  }).catch(() => undefined);
});
try {
  const day = await setScene('day-destroyed', '妖怪兽道');
  assert.equal(day.nightBusinessLifecyclePhase, 'Destroyed');
  assert.equal(day.nightBusinessGeneration, 1);
  assert.equal(day.nightBusiness, null);
  assert.equal(day.nightBusinessAutomationAllowed, false);
  await page.addInitScript(({ api }) => {
    localStorage.setItem('mystia-steward-companion-mod-api-endpoint', api);
    localStorage.setItem('mystia-steward-companion-mod-api-token', 'mock-token');
    localStorage.setItem('mystia-steward-companion-automation-enabled', '0');
  }, { api });
  await page.goto(app, { waitUntil: 'domcontentloaded' });
  await waitUntil(() => statuses.some(item => item.current && item.phase === 'Destroyed' && item.generation === 1
    && item.runtimeSets && item.recipeIds > 0 && !item.error && !item.rareUi && !item.normalUi), 'Destroyed白天返回runtimeSets且无UI目标');
  checks.push('实际C#宿主Destroyed/generation1返回runtimeSets，无错误及夜间UI目标');
  await page.locator('[data-gamepad-tab-value="recommendations"]').first().click();
  await page.getByRole('tab', { name: '普客', exact: true }).click();
  await page.getByPlaceholder('选择地区', { exact: true }).click();
  await page.getByRole('option', { name: '妖怪兽道', exact: true }).click();
  await assertPageRecipes('normal', day.runtimeDaySceneGeneration);
  checks.push('白天普客页面展示真实C#推荐料理');
  await page.screenshot({ path: path.join(output, 'day-normal-1280.png'), fullPage: true });
  await page.getByRole('tab', { name: '稀客', exact: true }).click();
  await assertPageRecipes('rare', day.runtimeDaySceneGeneration);
  checks.push('白天稀客页面展示真实C#推荐料理');
  await page.setViewportSize({ width: 390, height: 900 });
  await page.screenshot({ path: path.join(output, 'day-rare-390.png'), fullPage: true });
  const otherMap = await setScene('day-destroyed', '人间之里');
  assert.ok(otherMap.runtimeDaySceneGeneration > day.runtimeDaySceneGeneration);
  assert.equal(otherMap.nightBusinessLifecyclePhase, 'Destroyed');
  await assertPageRecipes('rare', otherMap.runtimeDaySceneGeneration);
  checks.push('白天地图/日间代次切换后仍从新上下文返回推荐');
  await page.setViewportSize({ width: 640, height: 900 });
  await page.locator('[data-gamepad-tab-value="service"]').first().click();
  const rareOrders = page.locator('[data-service-order-collection="rare"]');
  await page.locator('[data-service-order-collection="rare"][data-service-order-state="empty"]').waitFor({ state: 'visible', timeout: 15000 });
  await assertNoGateError();
  checks.push('白天经营推荐正常为空，无Destroyed生命周期错误');
  await page.screenshot({ path: path.join(output, 'day-service-640.png'), fullPage: true });
  const night = await setScene('night-active');
  assert.equal(night.nightBusinessLifecyclePhase, 'Active');
  assert.equal(night.nightBusinessGeneration, 2);
  await waitUntil(() => statuses.some(item => item.current && item.phase === 'Active' && item.generation === 2
    && item.recommendations > 0 && !item.error), '恢复夜间C#经营推荐');
  await rareOrders.getByText('推荐料理', { exact: true }).first().waitFor({ state: 'visible', timeout: 15000 });
  await assertNoGateError();
  checks.push('同一mock和C#进程切Active/generation2恢复经营稀客列表');
  await page.screenshot({ path: path.join(output, 'night-service-640.png'), fullPage: true });
  const errors = statuses.concat(queries).filter(item => item.error);
  assert.deepEqual(errors, [], '完整日夜转换中业务响应不应出现UI生命周期异常');
  checks.push('完整日夜转换无业务错误响应');
  await writeFile(path.join(output, 'result.json'), JSON.stringify({ ok: true, checks, statuses, queries }, null, 2));
  console.log(`PASS: ${checks.length} real-host daytime UI checks. ${output}`);
} finally { await browser.close(); }

async function setScene(scene, dayMap) {
  const response = await fetch(`${api}/__mock/scene`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ scene, ...(dayMap ? { dayMap } : {}) }) });
  assert.equal(response.ok, true, '测试mock必须显式开启MOCK_ENABLE_SCENE_CONTROL=1');
  return (await fetch(`${api}/snapshot`, { headers: { 'X-Mystia-Steward-Companion-Token': 'mock-token' } })).json();
}
async function assertPageRecipes(kind, dayGeneration) {
  await waitUntil(() => queries.some(item => item.current && item.phase === 'Destroyed'
    && item.dayGeneration === dayGeneration && item.kind === kind && item.recipes > 0 && !item.error), `${kind}白天页面真实C#结果`);
  await page.waitForFunction(() => [...document.querySelectorAll('h2')].some(item => /^料理推荐 \([1-9]/.test(item.textContent || '')), null, { timeout: 15000 });
  await assertNoGateError();
}
async function assertNoGateError() {
  assert.doesNotMatch(await page.locator('body').innerText(), /Night-business UI target rejected|业务数据读取失败|推荐更新失败/);
}
async function waitUntil(predicate, label) {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) { if (predicate()) return; await new Promise(resolve => setTimeout(resolve, 100)); }
  throw new Error(`等待失败：${label}`);
}
