import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

// 只启动自有 mock 和静态预览；业务结果经 mock 桥接仓库内 source-link C# 宿主，绝不连接游戏端口。
const apiPort = 45000 + process.pid % 1000;
const appPort = 47000 + process.pid % 1000;
const endpoint = `http://127.0.0.1:${apiPort}`;
const appUrl = `http://127.0.0.1:${appPort}/`;
const output = path.resolve(process.env.SCHEMA5_UI_OUTPUT_DIR || 'output/playwright/schema5-participation');
const primaryId = 'schema5-ui-primary-0001';
const secondaryId = 'schema5-ui-secondary-0002';
const headers = (id) => ({ 'X-Mystia-Steward-Companion-Token': 'mock-token',
  'X-Mystia-Steward-Companion-Client-Id': id, 'X-Mystia-Steward-Companion-Client-Label': 'schema5 offline audit' });
const children = [];
function start(args, env = {}) {
  const child = spawn(process.execPath, args, { cwd: process.cwd(), env: { ...process.env, ...env },
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  child.output = '';
  child.stdout.on('data', (data) => { child.output += data; });
  child.stderr.on('data', (data) => { child.output += data; });
  children.push(child); return child;
}
async function until(work, message, timeout = 12000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    if (await work()) return;
    await new Promise((resolve) => setTimeout(resolve, 80));
  }
  throw new Error(`${message}\n${children.map((child) => child.output).join('\n')}`);
}
async function getState(id = primaryId) {
  const response = await fetch(`${endpoint}/devices`, { headers: headers(id) });
  return response.ok ? response.json() : null;
}
function seed({ endpoint: api, id }) {
  const prefix = 'mystia-steward-companion';
  localStorage.setItem(`${prefix}-mod-api-endpoint`, api);
  localStorage.setItem(`${prefix}-mod-api-token`, 'mock-token');
  localStorage.setItem(`${prefix}-client-id`, id);
  localStorage.setItem(`${prefix}-automation-enabled`, '0');
  localStorage.setItem(`${prefix}-rare-guest-participation-module-enabled`, '1');
  localStorage.setItem(`${prefix}-managed-rare-guest-ids`, '[1001,999999]');
}
async function openSettings(page) {
  await page.locator('[data-gamepad-tab-value="settings"]').first().click();
  await page.locator('[data-settings-tabs]').getByRole('tab', { name: '实验性功能', exact: true }).click();
  await page.locator('[data-rare-participation-settings]').waitFor({ state: 'visible' });
}
const checks = [];
let browser;
try {
  await mkdir(output, { recursive: true });
  const mock = start(['scripts/mock-local-api.mjs'], { MOCK_API_PORT: String(apiPort) });
  const preview = start(['node_modules/vite/bin/vite.js', 'preview', '--config', 'apps/companion/vite.config.ts',
    '--host', '127.0.0.1', '--port', String(appPort), '--strictPort']);
  await until(async () => {
    if (mock.exitCode !== null || preview.exitCode !== null) throw new Error('离线服务提前退出。');
    try { return (await fetch(`${endpoint}/health`)).ok && (await fetch(appUrl)).ok; } catch { return false; }
  }, '离线服务未启动');
  browser = await chromium.launch({ headless: true,
    ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await context.addInitScript(seed, { endpoint, id: primaryId });
  const page = await context.newPage();
  await page.goto(appUrl);
  await until(async () => (await getState())?.profileSchemaVersion === 5, '主设备未建立 schema5 配置');
  await openSettings(page);
  const panel = page.locator('[data-rare-participation-settings]');
  const toggle = panel.getByRole('switch', { name: '保留稀客手动参与模块' });
  await until(async () => !(await toggle.isDisabled()), '主设备配置没有成为可编辑状态');
  assert.equal(await toggle.isChecked(), true);
  assert.match(await panel.innerText(), /稀客 ID 999999（当前目录未提供）/);
  assert.match(await panel.innerText(), /暂停全部稀客自动化和游戏辅助/);
  checks.push('继承历史模块/名单，保留目录外ID，显示兼容限制');

  // 只检查真实 C# 回传的诊断，不在测试 HTTP 层捏造业务结论。
  await until(async () => {
    const response = await fetch(`${endpoint}/business/status?protocolVersion=1`, { headers: headers(primaryId) });
    if (!response.ok) return false;
    const state = await response.json();
    return state.isCurrent && state.recommendations?.rareGuestParticipation?.automationBlocked === true;
  }, 'C# 宿主没有发布参与队列兼容诊断', 20000);
  await page.locator('[data-gamepad-tab-value="service"]').first().click();
  await page.locator('[data-rare-participation-notice]').waitFor({ state: 'visible', timeout: 15000 });
  assert.match(await page.locator('[data-rare-participation-notice]').innerText(), /未实现按订单参与队列/);
  checks.push('经营页直接展示当前C#兼容诊断');

  await openSettings(page);
  for (const width of [1280, 640, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await panel.scrollIntoViewIfNeeded();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    assert.equal(overflow, false, `${width}px 页面横向溢出。`);
    await panel.screenshot({ path: path.join(output, `settings-${width}.png`) });
    checks.push(`${width}px 设置显示无横向溢出`);
  }
  await page.setViewportSize({ width: 1280, height: 900 });
  const input = panel.locator('#rare-participation-managed-guests');
  await input.fill('露米娅');
  await page.getByRole('option', { name: /露米娅/ }).click();
  await page.keyboard.press('Escape');
  await until(async () => JSON.stringify((await getState())?.activeProfile.managedRareGuestIds) === '[1001,1002,999999]',
    '名单编辑没有通过共享配置保存');
  checks.push('编辑名单持久化且保留目录外ID');
  // Mantine 将可视轨道覆盖在原生 input 上；点击关联标签模拟实际用户点击，不能强行穿透轨道。
  await panel.getByText('保留稀客手动参与模块', { exact: true }).click();
  await until(async () => (await getState())?.activeProfile.rareGuestParticipationModuleEnabled === false,
    '主动关闭模块没有保存');
  assert.deepEqual((await getState()).activeProfile.managedRareGuestIds, [1001, 1002, 999999]);
  checks.push('主动关闭模块保留完整名单');

  const secondary = await browser.newContext({ viewport: { width: 390, height: 900 } });
  await secondary.addInitScript(seed, { endpoint, id: secondaryId });
  const secondPage = await secondary.newPage();
  await secondPage.goto(appUrl);
  await until(async () => (await getState(secondaryId))?.currentDeviceIsPrimary === false, '次设备未注册');
  await openSettings(secondPage);
  const secondPanel = secondPage.locator('[data-rare-participation-settings]');
  assert.equal(await secondPanel.getByRole('switch').isDisabled(), true);
  assert.equal(await secondPanel.locator('#rare-participation-managed-guests').isDisabled(), true);
  assert.match(await secondPanel.innerText(), /999999/);
  await secondPanel.screenshot({ path: path.join(output, 'secondary-readonly-390.png') });
  checks.push('次设备显示生效名单，开关与列表只读');
  await writeFile(path.join(output, 'report.json'), JSON.stringify({ realGameTouched: false, checks }, null, 2));
  console.log(`PASS schema5 participation UI: ${checks.length} checks; 1280/640/390; real C# mock bridge; no game connection.`);
} finally {
  await browser?.close();
  for (const child of children) child.kill('SIGTERM');
  await Promise.all(children.map((child) => child.exitCode !== null ? Promise.resolve()
    : Promise.race([new Promise((resolve) => child.once('exit', resolve)), new Promise((resolve) => setTimeout(resolve, 2000))])));
}
