import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const appUrl = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173';
const apiUrl = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const apiToken = process.env.MYSTIA_API_TOKEN || 'mock-token';
const executablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH?.trim();
const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}) });
const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
const errors = [];
page.on('pageerror', (error) => errors.push(error.message));
page.setDefaultTimeout(10000);

try {
  await page.addInitScript(() => {
    const buttons = Array.from({ length: 17 }, () => ({ pressed: false, touched: false, value: 0 }));
    const gamepad = { axes: [0, 0, 0, 0], buttons, connected: true, id: 'Settings Lifecycle Gamepad', index: 0, mapping: 'standard', timestamp: 1 };
    Object.defineProperty(navigator, 'getGamepads', { configurable: true, value: () => [gamepad] });
    window.__settingsGamepad = (index, pressed) => {
      buttons[index] = { pressed, touched: pressed, value: pressed ? 1 : 0 };
      gamepad.timestamp += 1;
    };
  });
  await page.addInitScript(({ endpoint, token }) => {
    const prefix = 'mystia-steward-companion';
    localStorage.setItem(`${prefix}-mod-api-endpoint`, endpoint);
    localStorage.setItem(`${prefix}-mod-api-token`, token);
    localStorage.setItem(`${prefix}-show-debug-details`, '1');
    localStorage.setItem(`${prefix}-client-id`, 'settings-lifecycle-audit');
  }, { endpoint: apiUrl, token: apiToken });
  await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
  const top = (name) => page.locator('.steward-primary-tabs-list').getByRole('tab', { name, exact: true }).click();
  await top('概览');
  await page.waitForFunction(() => document.body.textContent.includes('1.0.5'));

  // One explicit submit, including a temporarily empty draft; background reads
  // must not overwrite the input while the user is editing.
  await page.request.post(`${apiUrl}/logs/config?aggregateLogMaxFiles=30`, {
    headers: { 'X-Mystia-Steward-Companion-Token': apiToken },
  });
  await top('日志');
  const count = page.getByRole('textbox', { name: '文件上限', exact: true });
  await page.waitForFunction(() => document.querySelector('#logs-max-file-count')?.value === '30');
  const posts = [];
  let failNextSave = false;
  await page.route('**/logs/config?*', async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    posts.push(new URL(route.request().url()).searchParams.get('aggregateLogMaxFiles'));
    if (failNextSave) {
      failNextSave = false;
      return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: '保存失败探针' }) });
    }
    const response = await route.fetch();
    await new Promise((resolve) => setTimeout(resolve, 500));
    await route.fulfill({ response });
  });
  await count.fill('');
  assert.equal(await count.inputValue(), '');
  await count.pressSequentially('120', { delay: 60 });
  assert.deepEqual(posts, []);
  await page.waitForTimeout(3200);
  assert.equal(await count.inputValue(), '120');
  await count.press('Enter');
  await page.getByText('文件上限已保存为 120。', { exact: true }).waitFor();
  assert.deepEqual(posts, ['120']);
  await count.fill('10000');
  assert.equal(await page.locator('[data-gamepad-focus-key="logs:save-max-file-count"]').isDisabled(), true);
  await count.fill('121');
  failNextSave = true;
  await count.press('Enter');
  await page.getByText(/保存失败探针/).last().waitFor();
  assert.equal(await count.inputValue(), '121', '保存失败保留草稿');
  assert.deepEqual(posts, ['120', '121']);

  await top('设置');
  await page.getByRole('tab', { name: '帮助', exact: true }).click();
  for (const key of ['Enter', 'Space']) {
    const item = page.getByRole('treeitem', { name: '常用快捷键', exact: true });
    await item.focus();
    await page.keyboard.press(key);
    const heading = page.locator('[data-help-detail-title]');
    await heading.waitFor({ state: 'visible' });
    assert.equal(await heading.textContent(), '常用快捷键');
    assert.equal(await heading.evaluate((element) => document.activeElement === element), true);
    const bounds = await heading.boundingBox();
    assert.ok(bounds.y >= 0 && bounds.y + bounds.height <= 844, '正文标题应进入窄屏视野');
    await page.getByRole('button', { name: '返回目录', exact: true }).click();
    assert.equal(await item.evaluate((element) => document.activeElement === element), true);
  }
  const gamepadItem = page.getByRole('treeitem', { name: '常用快捷键', exact: true });
  await gamepadItem.focus();
  await pressGamepad(0);
  await page.locator('[data-help-detail-title]').waitFor({ state: 'visible' });
  assert.equal(await page.locator('[data-help-detail-title]').evaluate((element) => document.activeElement === element), true);
  await pressGamepad(1);
  assert.equal(await gamepadItem.evaluate((element) => document.activeElement === element), true, '手柄 B 返回目录并恢复原条目');
  await page.getByRole('textbox', { name: '搜索帮助', exact: true }).fill('不会存在的帮助搜索词');
  await page.getByText('没有匹配的帮助内容', { exact: true }).waitFor();
  await page.getByRole('textbox', { name: '搜索帮助', exact: true }).fill('');
  await page.setViewportSize({ width: 1280, height: 900 });
  const desktopItem = page.getByRole('treeitem', { name: '常用快捷键', exact: true });
  await desktopItem.focus();
  await desktopItem.press('Enter');
  assert.equal(await desktopItem.evaluate((element) => document.activeElement === element), true);
  assert.equal(await page.locator('[data-help-detail-title]').textContent(), '常用快捷键');

  await page.getByRole('tab', { name: '连接', exact: true }).click();
  const lanSwitch = page.locator('[data-setting-help-id="connection-lan-enabled"] input[type="checkbox"]');
  await waitEnabled(lanSwitch);

  // Preserve a newer pause while accepting the real credentials returned by a
  // Token reset that was already sent. Only explicit resume may start reading.
  let releaseTokenResponse;
  const tokenResponseGate = new Promise((resolve) => { releaseTokenResponse = resolve; });
  let tokenResetStarted;
  const tokenResetReady = new Promise((resolve) => { tokenResetStarted = resolve; });
  let regeneratedToken = '';
  let tokenResetPosts = 0;
  await page.route('**/local-api/token/regenerate', async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    tokenResetPosts += 1;
    const response = await route.fetch();
    assert.equal(response.status(), 200);
    const config = await response.json();
    assert.equal(config.ok, true);
    assert.equal(typeof config.token, 'string');
    assert.ok(config.token && config.token !== apiToken);
    regeneratedToken = config.token;
    tokenResetStarted();
    await tokenResponseGate;
    await route.fulfill({ response });
  });
  await page.locator('[data-gamepad-focus-key="settings:connection:reset-token"]').click();
  await page.locator('[data-gamepad-focus-key="settings:connection:reset-token:confirm"]').click();
  await tokenResetReady;
  await top('概览');
  const connectionToggle = page.locator('[data-gamepad-focus-key="overview:connection:toggle"]');
  const connectionStatus = page.locator('[data-overview-connection-status-metric="connection"]');
  await connectionToggle.click();
  await connectionStatus.getByText('已停止', { exact: true }).waitFor();
  const snapshotTokens = [];
  const observeSnapshot = (request) => {
    if (request.method() === 'GET' && new URL(request.url()).pathname === '/snapshot') {
      snapshotTokens.push(request.headers()['x-mystia-steward-companion-token']);
    }
  };
  page.on('request', observeSnapshot);
  releaseTokenResponse();
  await page.waitForFunction((token) => localStorage.getItem('mystia-steward-companion-mod-api-token') === token, regeneratedToken);
  assert.equal(await page.locator('[data-overview-connection-token]').inputValue(), regeneratedToken);
  await page.waitForTimeout(2200);
  assert.equal(await connectionStatus.getByText('已停止', { exact: true }).isVisible(), true, 'Token 重置成功不能撤销稍后发生的暂停');
  assert.deepEqual(snapshotTokens, [], '保存新 Token 后保持暂停，不自动读取快照');
  assert.equal(tokenResetPosts, 1, '重置 Token 不得自动重发');
  await connectionToggle.click();
  await connectionStatus.getByText('已连接', { exact: true }).waitFor();
  assert.ok(snapshotTokens.length > 0, '显式恢复后应读取快照');
  assert.ok(snapshotTokens.every((token) => token === regeneratedToken), '恢复只使用已确认的新 Token');
  page.off('request', observeSnapshot);
  await top('设置');
  await page.getByRole('tab', { name: '连接', exact: true }).click();
  await waitEnabled(lanSwitch);

  // A settings write remains owned after leaving the page. Applying a new
  // connection must prevent its late response from restoring old credentials.
  let releaseWrite;
  const writeGate = new Promise((resolve) => { releaseWrite = resolve; });
  let writeStarted;
  const started = new Promise((resolve) => { writeStarted = resolve; });
  await page.route('**/local-api/config?*', async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    const response = await route.fetch();
    writeStarted();
    await writeGate;
    await route.fulfill({ response });
  });
  await lanSwitch.press('Space');
  await started;
  let configReadsDuringWrite = 0;
  const countConfigReads = (request) => {
    if (request.method() === 'GET' && new URL(request.url()).pathname === '/local-api/config') configReadsDuringWrite += 1;
  };
  page.on('request', countConfigReads);
  await top('概览');
  await page.locator('[data-overview-connection-token]').press('Enter');
  await top('设置');
  await page.getByRole('tab', { name: '连接', exact: true }).click();
  assert.equal(await lanSwitch.isDisabled(), true, '同地址重连和设置页重挂载不能提前解除旧写占用');
  assert.equal(configReadsDuringWrite, 0, '旧写处理期间不读取可能尚未确认的配置');
  page.off('request', countConfigReads);
  await top('概览');
  await page.locator('[data-overview-connection-token]').fill('replacement-token');
  await page.locator('[data-overview-connection-token]').press('Enter');
  releaseWrite();
  await page.waitForTimeout(700);
  assert.equal(await page.locator('[data-overview-connection-token]').inputValue(), 'replacement-token');
  assert.equal(await page.evaluate(() => localStorage.getItem('mystia-steward-companion-mod-api-token')), 'replacement-token');
  assert.deepEqual(errors, []);
  console.log('settings lifecycle UI passed: explicit log submit, failed draft, help keyboard/gamepad/mobile focus, paused Token reset, stale connection write');
} finally {
  await browser.close();
}

async function waitEnabled(locator) {
  await locator.waitFor({ state: 'attached' });
  await page.waitForFunction((element) => !element.disabled, await locator.elementHandle());
}

async function pressGamepad(index) {
  for (const pressed of [true, false]) {
    await page.evaluate(({ index, pressed }) => window.__settingsGamepad(index, pressed), { index, pressed });
    await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(resolve)))));
  }
}
