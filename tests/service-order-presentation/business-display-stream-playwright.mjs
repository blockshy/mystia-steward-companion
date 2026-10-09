import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

// 使用仓库模拟 API 的真实 C# 计算，仅在浏览器传输边界制造两个轮询的快照错位。
// 不访问真实游戏端口，不通过浏览器构造候选，也不发起任何自动化动作。
const api = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const app = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4174/';
const output = process.env.BUSINESS_DISPLAY_OUTPUT_DIR || 'output/playwright/business-display-stream';
const reproduce = process.env.BUSINESS_DISPLAY_REPRODUCE === '1';
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true,
  ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
const counters = { snapshots: 0, statuses: 0, completedStatuses: 0, queries: 0, completedQueries: 0 };
const checks = [];
let sceneChanged = false;
let disconnected = false;
let pending = false;
let queryBlocked = false;
let queryError = false;
let listIncomplete = false;
let baselineSnapshot;
try {
  baselineSnapshot = await (await fetch(`${api}/snapshot`, { headers: { 'X-Mystia-Steward-Companion-Token': 'mock-token' } })).json();
  await page.addInitScript(({ api }) => {
    localStorage.setItem('mystia-steward-companion-mod-api-endpoint', api);
    localStorage.setItem('mystia-steward-companion-mod-api-token', 'mock-token');
    localStorage.setItem('mystia-steward-companion-show-debug-details', '1');
    localStorage.setItem('mystia-steward-companion-automation-enabled', '0');
  }, { api });
  await page.route(`${api}/snapshot**`, async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    if (disconnected) return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'stream-test disconnected' }) });
    const snapshot = structuredClone(baselineSnapshot);
    // 每次快照读取都推进展示签名，业务接口依然返回独立计算帧的签名。
    snapshot.snapshotSignature = `separate-snapshot-poll-${++counters.snapshots}`;
    snapshot.capturedAtUtc = new Date().toISOString();
    if (sceneChanged) {
      snapshot.nightBusinessGeneration += 1;
      snapshot.activeSceneName = 'NightScene.OtherBusiness';
    }
    if (listIncomplete) {
      snapshot.nightBusiness.activeRareGuests = [];
      snapshot.nightBusiness.activeRareGuestsReadComplete = false;
      snapshot.nightBusiness.error = 'stream-test guest collection unavailable';
    }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(snapshot) });
  });
  await page.route(`${api}/business/status**`, async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    const response = await route.fetch();
    const body = await response.json();
    counters.statuses += 1;
    if (body.isCurrent && body.recommendations?.recommendations?.length) counters.completedStatuses += 1;
    if (pending) { body.isCurrent = false; body.pending = true; }
    await route.fulfill({ response, json: body });
  });
  await page.route(`${api}/business/query`, async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    if (queryError) return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'stream-test page unavailable' }) });
    const response = await route.fetch();
    const body = await response.json();
    counters.queries += 1;
    if (body.isCurrent && body.result?.recipes?.length) counters.completedQueries += 1;
    if (pending || queryBlocked) { body.isCurrent = false; body.pending = true; body.result = null; }
    await route.fulfill({ response, json: body });
  });
  await page.goto(app, { waitUntil: 'domcontentloaded' });
  await page.locator('[data-gamepad-tab-value="service"]').first().click();
  const rareCollection = page.locator('[data-service-order-collection="rare"]');
  await rareCollection.waitFor({ state: 'visible' });
  await waitUntil(() => counters.completedStatuses >= 3, '服务端已多次完成经营推荐');
  if (reproduce) {
    assert.equal(await rareCollection.locator('[data-service-order-card="true"]').filter({ hasText: '推荐料理' }).count(), 0);
    assert.ok((await rareCollection.innerText()).includes('推荐计算中'));
    checks.push('旧前端：服务端完成结果至少3次，经营页仍只有计算中占位');
  } else {
    await rareCollection.getByText('推荐料理', { exact: true }).first().waitFor({ state: 'visible' });
    checks.push('独立快照签名持续变化时经营稀客推荐仍展示');
    const previousStatuses = counters.statuses;
    pending = true;
    await waitUntil(() => counters.statuses > previousStatuses, '等待下一次状态轮询');
    await rareCollection.getByText('推荐料理', { exact: true }).first().waitFor({ state: 'visible' });
    assert.equal(await rareCollection.getAttribute('data-service-order-state'), 'updating');
    checks.push('服务端current=false时保留同上下文经营推荐并标记更新中');
    pending = false;
  }
  await page.screenshot({ path: path.join(output, 'service-stream.png'), fullPage: true });
  await page.locator('[data-gamepad-tab-value="recommendations"]').first().click();
  await page.getByRole('tab', { name: '普客', exact: true }).click();
  await waitUntil(() => counters.completedQueries >= 2, '服务端已完成普客页面查询');
  if (reproduce) {
    assert.ok((await page.locator('[data-gamepad-scroll-key$=":recipes"]').innerText()).includes('推荐计算中'));
    checks.push('旧前端：普客页面已有完成查询，仍显示推荐计算中');
  } else {
    await waitForRecipeRows();
    checks.push('独立轮询下普客页面显示C#结果');
    pending = true;
    const previousQueries = counters.queries;
    await waitUntil(() => counters.queries > previousQueries, '页面进入重新计算');
    await waitForRecipeRows();
    await page.getByRole('status').filter({ hasText: '推荐更新中' }).waitFor({ state: 'visible' });
    checks.push('同意图pending空响应保留最后成功页面结果');
    pending = false;
    queryError = true;
    await page.getByRole('status').filter({ hasText: '推荐更新失败' }).waitFor({ state: 'visible', timeout: 10000 });
    await waitForRecipeRows();
    checks.push('页面查询错误保留结果且明确显示更新失败');
    await page.screenshot({ path: path.join(output, 'page-retained-error.png'), fullPage: true });
    for (const width of [640, 390]) {
      await page.setViewportSize({ width, height: 900 });
      await page.getByRole('status').filter({ hasText: '推荐更新失败' }).waitFor({ state: 'visible' });
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      assert.ok(overflow <= 1, `${width}px 更新失败提示不应导致横向溢出`);
      await page.screenshot({ path: path.join(output, `page-retained-error-${width}.png`), fullPage: true });
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    checks.push('1280/640/390宽度下旧结果与失败提示可见且无横向溢出');
    queryError = false;
    await page.getByRole('tab', { name: '稀客', exact: true }).click();
    await waitForRecipeRows();
    checks.push('稀客页面显示C#结果');
    queryBlocked = true;
    const selector = page.locator('input[aria-label="点单料理 Tag"]');
    await selector.click();
    const options = page.getByRole('option');
    const currentTag = await selector.inputValue();
    const optionTexts = await options.allTextContents();
    const alternate = optionTexts.find(text => text.trim() !== currentTag);
    assert.ok(alternate, 'fixture 至少提供两个料理标签');
    await page.getByRole('option', { name: alternate, exact: true }).click();
    await page.getByText('推荐计算中', { exact: true }).first().waitFor({ state: 'visible' });
    await waitForEmptyRecipes();
    checks.push('切换查询意图立即清空上一选择结果');
    queryBlocked = false;
    await waitForRecipeRows();
    sceneChanged = true;
    // 场景切换还会清空同来源 runtimeSets；页面可显示等待运行时，不强制要求某一种空文案。
    await waitUntil(async () => await page.locator('h2').filter({ hasText: /^料理推荐 \([1-9]/ }).count() === 0,
      '场景切换清空旧料理列表');
    checks.push('切换经营代次与场景清空旧上下文页面结果');
    await page.locator('[data-gamepad-tab-value="service"]').first().click();
    await rareCollection.waitFor({ state: 'visible' });
    assert.equal(await rareCollection.getByText('推荐料理', { exact: true }).count(), 0);
    checks.push('旧经营上下文结果不会穿过场景切换展示');
    sceneChanged = false;
    await rareCollection.getByText('推荐料理', { exact: true }).first().waitFor({ state: 'visible', timeout: 10000 });
    disconnected = true;
    await waitUntil(async () => await rareCollection.getByText('推荐料理', { exact: true }).count() === 0, '断线清空展示结果');
    checks.push('连接不可用立即清空保留展示');
    disconnected = false;
    await rareCollection.getByText('推荐料理', { exact: true }).first().waitFor({ state: 'visible', timeout: 15000 });
    checks.push('重连后等待当前连接的新结果并恢复');
    listIncomplete = true;
    await page.locator('[data-slot="segmented-control"]').filter({ hasText: '诊断' }).first().locator('label').filter({ hasText: /^诊断$/ }).click();
    await page.getByText('稀客名单读取不完整，请查看读取详情', { exact: true }).waitFor({ state: 'visible', timeout: 10000 });
    assert.equal(await page.getByText('暂无稀客', { exact: true }).count(), 0);
    await page.getByText('stream-test guest collection unavailable', { exact: true }).waitFor({ state: 'visible' });
    checks.push('客人名单读取不完整时显示诊断详情，不误报暂无稀客');
  }
  await page.screenshot({ path: path.join(output, 'page-stream.png'), fullPage: true });
  await writeFile(path.join(output, 'result.json'), JSON.stringify({ ok: true, reproduce, counters, checks }, null, 2));
  console.log(`${reproduce ? 'REPRODUCED' : 'PASS'}: ${checks.length} display-stream checks. ${output}`);
} finally { await browser.close(); }

async function waitUntil(predicate, label) {
  const until = Date.now() + 20000;
  while (Date.now() < until) { if (await predicate()) return; await new Promise(resolve => setTimeout(resolve, 100)); }
  throw new Error(`等待失败：${label}`);
}
async function waitForRecipeRows() {
  await page.waitForFunction(() => [...document.querySelectorAll('h2')].some(item => /^料理推荐 \([1-9]/.test(item.textContent || '')), null, { timeout: 20000 });
}
async function waitForEmptyRecipes() {
  await page.getByRole('heading', { name: '料理推荐 (0)', exact: true }).waitFor({ state: 'visible' });
}
