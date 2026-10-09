import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

// 仅验证首次业务状态失败、尚无 runtimeSets 缓存时的展示。
// 游戏快照来自本地 mock；业务路由全部拦截，不启动游戏或依赖此前成功的 C# 计算。
const api = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const app = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4174/';
const output = process.env.BUSINESS_DISPLAY_OUTPUT_DIR || 'output/playwright/business-display-cold-error';
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true,
  ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
const page = await browser.newPage({ viewport: { width: 640, height: 900 } });
const checks = [];
try {
  await page.addInitScript(({ api }) => {
    localStorage.setItem('mystia-steward-companion-mod-api-endpoint', api);
    localStorage.setItem('mystia-steward-companion-mod-api-token', 'mock-token');
  }, { api });
  await page.route(`${api}/business/status**`, route => route.request().method() !== 'GET' ? route.continue()
    : route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'cold-start business unavailable' }) }));
  await page.route(`${api}/business/query`, route => route.request().method() !== 'POST' ? route.continue()
    : route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ protocolVersion: 1,
      result: null, isCurrent: false, pending: true, error: null, sourceSnapshotSignature: '' }) }));
  await page.goto(app, { waitUntil: 'domcontentloaded' });
  await page.locator('[data-gamepad-tab-value="recommendations"]').first().click();
  for (const [name, width] of [['普客', 640], ['稀客', 390]]) {
    await page.setViewportSize({ width, height: 900 });
    await page.getByRole('tab', { name, exact: true }).click();
    await page.getByText('业务数据读取失败：cold-start business unavailable', { exact: true }).waitFor({ state: 'visible', timeout: 10000 });
    assert.equal(await page.getByText('尚未读取到游戏实时数据。请确认游戏已加载存档，且 Mod 本地 API 已连接。', { exact: true }).count(), 0);
    assert.equal(await page.locator('h2').filter({ hasText: /^料理推荐 \([1-9]/ }).count(), 0);
    await page.screenshot({ path: path.join(output, `cold-error-${width}.png`), fullPage: true });
    checks.push(`${name}冷启动业务状态失败显示真实错误，不显示等待运行时或旧料理`);
  }
  await writeFile(path.join(output, 'result.json'), JSON.stringify({ ok: true, checks }, null, 2));
  console.log(`PASS: ${checks.length} cold-start business error checks. ${output}`);
} finally { await browser.close(); }
