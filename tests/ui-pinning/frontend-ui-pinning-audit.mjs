import assert from 'node:assert/strict';
import { chromium } from 'playwright';

/**
 * C# 业务宿主迁移后的界面辅助协议检查。
 * 浏览器仅编辑共享配置和读取目标投影；不再创建推荐 Worker 或发布目标字段。
 * mock API 将查询转给 source-link C# 宿主，游戏适配器始终取消，不访问真实游戏。
 */
const appUrl = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const apiUrl = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const token = process.env.MYSTIA_API_TOKEN || 'mock-token';
const clientId = 'ui-pinning-business-primary-0001';
const browser = await chromium.launch({ headless: true,
  ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}),
});
const page = await browser.newPage({ viewport: { width: 900, height: 760 } });
const requests = [];
const errors = [];
page.on('pageerror', (error) => errors.push(error.message));
page.on('request', (request) => {
  if (request.url().startsWith(apiUrl)) requests.push({ path: new URL(request.url()).pathname, method: request.method(), body: request.postDataJSON() });
});

try {
  await page.addInitScript(({ apiUrl, token, clientId }) => {
    const prefix = 'mystia-steward-companion';
    localStorage.setItem(prefix + '-mod-api-endpoint', apiUrl);
    localStorage.setItem(prefix + '-mod-api-token', token);
    localStorage.setItem(prefix + '-client-id', clientId);
    localStorage.setItem(prefix + '-show-debug-details', '1');
    localStorage.setItem(prefix + '-rare-game-ui-pinning', '1');
    localStorage.setItem(prefix + '-normal-game-ui-pinning', '1');
    // 该测试仅验证界面投影；不通过开启自动化触发模拟动作。
    localStorage.setItem(prefix + '-automation-enabled', '0');
    window.__businessWorkerCreations = 0;
    const OriginalWorker = window.Worker;
    window.Worker = class extends OriginalWorker {
      constructor(...args) { super(...args); window.__businessWorkerCreations += 1; }
    };
  }, { apiUrl, token, clientId });
  await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.body.innerText.includes('1.0.5'));

  const initial = await waitForStatus((status) => status.isCurrent && status.gameUiTargets?.rare && status.gameUiTargets?.normal);
  assert.equal(initial.gameUiTargets.rare.features.listPinningEnabled, true);
  assert.equal(initial.gameUiTargets.normal.features.listPinningEnabled, true);
  assert.ok(initial.gameUiTargets.rare.targetRevision);
  assert.ok(initial.gameUiTargets.normal.targetRevision);

  await page.locator('[data-gamepad-tab-value="settings"]').first().click();
  await page.getByRole('tab', { name: '实验性功能', exact: true }).click();
  await page.getByText('稀客游戏界面置顶推荐', { exact: true }).first().click();
  const rareDisabled = await waitForStatus((status) => status.isCurrent && status.gameUiTargets?.rare === null && status.gameUiTargets?.normal);
  assert.equal(rareDisabled.gameUiTargets.normal.targetRevision, initial.gameUiTargets.normal.targetRevision,
    '稀客显示开关不应更换普客业务目标');

  const color = page.getByRole('textbox', { name: '普客高亮色十六进制值', exact: true });
  await color.fill('#11AA44');
  await color.press('Enter');
  const recolored = await waitForStatus((status) => status.isCurrent && status.gameUiTargets?.normal?.color === '#11AA44');
  assert.equal(recolored.gameUiTargets.normal.targetRevision, initial.gameUiTargets.normal.targetRevision,
    '视觉颜色不应更换业务目标身份');

  await page.getByText('普客游戏界面置顶推荐', { exact: true }).first().click();
  await waitForStatus((status) => status.isCurrent && status.gameUiTargets?.rare === null && status.gameUiTargets?.normal === null);

  await page.locator('[data-gamepad-tab-value="recommendations"]').first().click();
  await page.waitForFunction(() => document.body.innerText.includes('推荐料理'));
  await waitFor(() => requests.some((request) => request.path === '/business/query'), '页面未提交只读业务查询');
  const queries = requests.filter((request) => request.path === '/business/query');
  for (const query of queries) {
    assert.equal(query.method, 'POST');
    assert.equal(query.body.protocolVersion, 1);
    assert.equal('data' in query.body || 'runtime' in query.body || 'preferences' in query.body, false,
      '客户端不得上传或覆盖权威业务输入');
  }
  assert.ok(requests.some((request) => request.path === '/devices/profile'), '设置必须经主设备共享配置提交');
  assert.equal(requests.some((request) => request.path === '/ui-pinning/targets'
    || ['/orders/prepare-next', '/orders/complete-first', '/orders/normal/complete-first'].includes(request.path)), false,
  '浏览器不得发布目标或执行自动化命令');
  assert.equal(await page.evaluate(() => window.__businessWorkerCreations), 0, '客户端不应创建推荐计算 Worker');
  assert.deepEqual(errors, [], '浏览器发生未捕获错误');
  console.log('PASS: UI submits settings/read intents only; C# independently projects rare/normal targets and visual color changes preserve target identity.');
} finally { await browser.close(); }

async function waitForStatus(predicate) {
  let latest;
  await waitFor(async () => {
    latest = await page.evaluate(async ({ apiUrl, token, clientId }) => {
      const response = await fetch(apiUrl + '/business/status?protocolVersion=1', { headers: {
        'X-Mystia-Steward-Companion-Token': token, 'X-Mystia-Steward-Companion-Client-Id': clientId,
        'X-Mystia-Steward-Companion-Client-Label': 'UI business audit',
      } });
      return response.json();
    }, { apiUrl, token, clientId });
    return predicate(latest);
  }, () => '未取得预期 C# 投影：' + JSON.stringify({ isCurrent: latest?.isCurrent, pending: latest?.pending,
    error: latest?.error, gameUiTargets: latest?.gameUiTargets }));
  return latest;
}
async function waitFor(predicate, message) {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(typeof message === 'function' ? message() : message);
}
