import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

const portOffset = process.pid % 1_000;
const apiPort = 42_000 + portOffset;
const appPort = 44_000 + portOffset;
const apiUrl = `http://127.0.0.1:${apiPort}`;
const appUrl = `http://127.0.0.1:${appPort}/`;
const apiToken = 'mock-token';
const outputDir = process.env.DEVICE_AUTHORITY_UI_OUTPUT_DIR
  || '/tmp/mystia-companion-device-authority-audit';
const chromiumExecutablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH?.trim();
const storagePrefix = 'mystia-steward-companion';

const devices = {
  a: {
    id: 'device-authority-web-a-0001',
    label: 'Windows companion',
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128 Safari/537.36',
    profile: {
      automationEnabled: false,
      autoRareConcurrency: 2,
      missionRecipePriorityEnabled: true,
      pinFavoriteRecipeEnabled: false,
      pinFavoriteBeverageEnabled: false,
    },
  },
  b: {
    id: 'device-authority-web-b-0002',
    label: 'Android companion',
    userAgent: 'Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 Chrome/128 Mobile Safari/537.36',
    profile: {
      automationEnabled: true,
      autoRareConcurrency: 3,
      missionRecipePriorityEnabled: true,
      pinFavoriteRecipeEnabled: true,
      pinFavoriteBeverageEnabled: false,
    },
  },
  c: {
    id: 'device-authority-web-c-0003',
    label: 'Companion',
    userAgent: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/128 Safari/537.36',
    profile: {
      automationEnabled: false,
      autoRareConcurrency: 4,
      missionRecipePriorityEnabled: false,
      pinFavoriteRecipeEnabled: false,
      pinFavoriteBeverageEnabled: true,
    },
  },
};

const mock = startService('mock API', [path.resolve('scripts/mock-local-api.mjs')], {
  MOCK_API_PORT: String(apiPort),
});
const preview = startService('Vite preview', [
  path.resolve('node_modules/vite/bin/vite.js'),
  'preview',
  '--config',
  path.resolve('apps/companion/vite.config.ts'),
  '--host',
  '127.0.0.1',
  '--port',
  String(appPort),
  '--strictPort',
]);

let browser = null;
const clients = [];
const checkpoints = [];

try {
  await mkdir(outputDir, { recursive: true });
  await Promise.all([
    waitForUrl(`${apiUrl}/health`, mock, 'mock API'),
    waitForUrl(appUrl, preview, 'Vite preview'),
  ]);

  browser = await chromium.launch({
    headless: true,
    ...(chromiumExecutablePath ? { executablePath: chromiumExecutablePath } : {}),
  });

  const clientA = await openClient(browser, devices.a);
  clients.push(clientA);
  await openConnection(clientA.page);
  await waitForDeviceRows(clientA.page, 1);
  let state = await waitForState(devices.a, (next) => (
    next.currentDeviceIsPrimary && next.devices.length === 1
  ));
  assert.equal(state.authorityRevision, 1);
  checkpoints.push('A 首次注册并成为主设备');

  const [clientB, clientC] = await Promise.all([
    openClient(browser, devices.b),
    openClient(browser, devices.c),
  ]);
  clients.push(clientB, clientC);
  await Promise.all([openConnection(clientB.page), openConnection(clientC.page)]);
  state = await waitForState(devices.a, (next) => next.devices.length === 3);
  await refreshAll([clientA.page, clientB.page, clientC.page], 3);
  await assertInitialThreeClientState(clientA.page, clientB.page, clientC.page, state);
  await screenshot(clientA.page, '01-three-clients-online.png');
  checkpoints.push('A/B/C 三个独立 Web 页面同时在线且只认 A 为主设备');

  await Promise.all([
    openSettingsSection(clientA.page, '推荐'),
    openSettingsSection(clientB.page, '推荐'),
    openSettingsSection(clientC.page, '推荐'),
  ]);
  await assertSwitch(clientA.page, 'recommendation-pin-favorite-recipe', false, false);
  await assertSwitch(clientB.page, 'recommendation-pin-favorite-recipe', false, true);
  await assertSwitch(clientC.page, 'recommendation-pin-favorite-recipe', false, true);

  await setSwitch(clientA.page, 'recommendation-pin-favorite-recipe', true);
  state = await waitForState(devices.a, (next) => next.activeProfile.pinFavoriteRecipeEnabled === true);
  await refreshAll([clientB.page, clientC.page]);
  await Promise.all([
    openSettingsSection(clientB.page, '推荐'),
    openSettingsSection(clientC.page, '推荐'),
  ]);
  await assertSwitch(clientB.page, 'recommendation-pin-favorite-recipe', true, true);
  await assertSwitch(clientC.page, 'recommendation-pin-favorite-recipe', true, true);
  await assertStoredBoolean(clientB.page, 'pin-favorite-recipe', true);
  await assertStoredBoolean(clientC.page, 'pin-favorite-recipe', true);
  checkpoints.push('A 修改共享配置后，B/C 只读 UI 与 localStorage 均应用 A 的生效配置');

  await openConnection(clientB.page);
  await clickDeviceAction(clientB.page, devices.b.id, '同步配置');
  await waitForState(devices.b, (next) => Boolean(next.pendingSyncId));
  await refreshDevices(clientB.page);
  state = await waitForState(devices.a, (next) => {
    const device = next.devices.find((item) => item.deviceId === devices.b.id);
    return Boolean(device && !device.syncPending && device.profileHash === next.activeProfileHash);
  });
  checkpoints.push('B 执行“同步配置”并完成 pending sync ACK');

  await openConnection(clientA.page);
  await refreshDevices(clientA.page);
  await clickDeviceAction(clientA.page, devices.b.id, '设为主设备');
  await assertPrimaryDialog(clientA.page, { expectWarning: false, screenshotName: '02-synced-transfer-dialog.png' });
  await confirmPrimaryDialog(clientA.page);
  state = await waitForState(devices.a, (next) => next.primaryDeviceId === devices.b.id);
  await refreshAll([clientA.page, clientB.page, clientC.page], 3);
  assert.match((await postAutomationLease(devices.a, state.authorityRevision)).error, /不是主设备/);
  assert.equal((await postAutomationLease(devices.b, state.authorityRevision)).ok, true);
  checkpoints.push('A -> B 转移后，旧主设备 A 的运行时写入被拒绝，B 获得执行权');

  await openSettingsSection(clientB.page, '推荐');
  await setSwitch(clientB.page, 'recommendation-pin-favorite-beverage', true);
  state = await waitForState(devices.b, (next) => next.activeProfile.pinFavoriteBeverageEnabled === true);
  await refreshAll([clientA.page, clientC.page]);
  await Promise.all([
    openSettingsSection(clientA.page, '推荐'),
    openSettingsSection(clientC.page, '推荐'),
  ]);
  await assertSwitch(clientA.page, 'recommendation-pin-favorite-beverage', true, true);
  await assertSwitch(clientC.page, 'recommendation-pin-favorite-beverage', true, true);
  checkpoints.push('B 成为主设备后的配置修改同步成为三端生效值');

  await openConnection(clientC.page);
  await clickDeviceAction(clientC.page, devices.c.id, '设为主设备');
  await assertPrimaryDialog(clientC.page, { expectWarning: true, screenshotName: '03-unsynced-transfer-dialog.png' });
  await confirmPrimaryDialog(clientC.page);
  state = await waitForState(devices.c, (next) => next.primaryDeviceId === devices.c.id);
  assert.equal(state.activeProfile.autoRareConcurrency, 4);
  assert.equal(state.activeProfile.missionRecipePriorityEnabled, false);
  assert.equal(state.activeProfile.pinFavoriteRecipeEnabled, false);
  assert.equal(state.activeProfile.pinFavoriteBeverageEnabled, true);
  await refreshAll([clientA.page, clientB.page], 3);
  await Promise.all([
    openSettingsSection(clientA.page, '推荐'),
    openSettingsSection(clientB.page, '推荐'),
    openSettingsSection(clientC.page, '推荐'),
  ]);
  for (const client of [clientA, clientB, clientC]) {
    await assertSwitch(client.page, 'recommendation-mission-recipe-priority', false, client !== clientC);
    await assertSwitch(client.page, 'recommendation-pin-favorite-recipe', false, client !== clientC);
    await assertSwitch(client.page, 'recommendation-pin-favorite-beverage', true, client !== clientC);
  }
  checkpoints.push('未同步的 C 成为主设备后，C 保存的完整配置成为唯一生效值并覆盖三端 UI');

  await openConnection(clientA.page);
  await clickDeviceAction(clientA.page, devices.a.id, '同步配置');
  await waitForState(devices.a, (next) => Boolean(next.pendingSyncId));
  await refreshDevices(clientA.page);
  await waitForState(devices.c, (next) => {
    const device = next.devices.find((item) => item.deviceId === devices.a.id);
    return Boolean(device && !device.syncPending && device.profileHash === next.activeProfileHash);
  });
  await clickDeviceAction(clientA.page, devices.a.id, '设为主设备');
  await assertPrimaryDialog(clientA.page, { expectWarning: false });
  await confirmPrimaryDialog(clientA.page);
  state = await waitForState(devices.a, (next) => next.primaryDeviceId === devices.a.id);
  assert.equal(state.activeProfile.autoRareConcurrency, 4);
  assert.equal(state.activeProfile.missionRecipePriorityEnabled, false);
  assert.equal(state.activeProfile.pinFavoriteRecipeEnabled, false);
  assert.equal(state.activeProfile.pinFavoriteBeverageEnabled, true);
  checkpoints.push('C -> A 同步后再转移不改变配置内容');

  await clientB.context.close();
  clientB.closed = true;
  await new Promise((resolve) => setTimeout(resolve, 21_000));
  await openConnection(clientA.page);
  await refreshDevices(clientA.page);
  const offlineRow = clientA.page.locator(`[data-device-authority-device="${devices.b.id}"]`);
  await waitForText(offlineRow, '离线');
  await screenshot(clientA.page, '04-former-device-offline.png');
  checkpoints.push('关闭 B 页面并超过在线 TTL 后，设备列表显示 B 离线');

  const report = {
    ok: true,
    clients: Object.values(devices).map(({ id, label }) => ({ id, label })),
    checkpoints,
    finalPrimaryDeviceId: state.primaryDeviceId,
    finalAuthorityRevision: state.authorityRevision,
    finalActiveProfile: {
      autoRareConcurrency: state.activeProfile.autoRareConcurrency,
      missionRecipePriorityEnabled: state.activeProfile.missionRecipePriorityEnabled,
      pinFavoriteRecipeEnabled: state.activeProfile.pinFavoriteRecipeEnabled,
      pinFavoriteBeverageEnabled: state.activeProfile.pinFavoriteBeverageEnabled,
    },
  };
  await writeFile(path.join(outputDir, 'result.json'), `${JSON.stringify(report, null, 2)}\n`);
  console.log(`PASS: three concurrent Web clients completed configuration sync, authority transfer, effective-profile propagation, stale-writer fencing and offline detection.\nArtifacts: ${outputDir}`);
  for (const checkpoint of checkpoints) console.log(`  - ${checkpoint}`);
} finally {
  for (const client of clients) {
    if (!client.closed) await client.context.close().catch(() => undefined);
  }
  if (browser) await browser.close().catch(() => undefined);
  await Promise.all([stopService(preview), stopService(mock)]);
}

function startService(label, args, extraEnv) {
  const child = spawn(process.execPath, args, {
    cwd: process.cwd(),
    env: { ...process.env, ...extraEnv },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.label = label;
  child.output = '';
  child.stdout.on('data', (chunk) => { child.output += chunk.toString(); });
  child.stderr.on('data', (chunk) => { child.output += chunk.toString(); });
  return child;
}

async function stopService(child) {
  if (!child || child.exitCode !== null) return;
  child.kill('SIGTERM');
  await Promise.race([
    new Promise((resolve) => child.once('exit', resolve)),
    new Promise((resolve) => setTimeout(resolve, 2_000)),
  ]);
}

async function waitForUrl(url, child, label) {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`${label} exited early.\n${child.output}`);
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      // Startup race.
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`${label} did not become ready.\n${child.output}`);
}

async function openClient(currentBrowser, device) {
  const context = await currentBrowser.newContext({
    viewport: { width: 1120, height: 820 },
    userAgent: device.userAgent,
  });
  await context.addInitScript(seedClientStorage, {
    apiUrl,
    apiToken,
    storagePrefix,
    deviceId: device.id,
    profile: device.profile,
  });
  const page = await context.newPage();
  await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.body.innerText.includes('1.0.5'), null, { timeout: 12_000 });
  return { context, page, device, closed: false };
}

function seedClientStorage({ apiUrl: endpoint, apiToken: token, storagePrefix: prefix, deviceId, profile }) {
  localStorage.setItem(`${prefix}-mod-api-endpoint`, endpoint);
  localStorage.setItem(`${prefix}-mod-api-token`, token);
  localStorage.setItem(`${prefix}-client-id`, deviceId);
  localStorage.setItem(`${prefix}-background-opacity`, '0.55');
  localStorage.setItem(`${prefix}-content-opacity`, '1');
  localStorage.setItem(`${prefix}-automation-enabled`, profile.automationEnabled ? '1' : '0');
  localStorage.setItem(`${prefix}-auto-rare-concurrency`, String(profile.autoRareConcurrency));
  localStorage.setItem(`${prefix}-mission-recipe-priority`, profile.missionRecipePriorityEnabled ? '1' : '0');
  localStorage.setItem(`${prefix}-pin-favorite-recipe`, profile.pinFavoriteRecipeEnabled ? '1' : '0');
  localStorage.setItem(`${prefix}-pin-favorite-beverage`, profile.pinFavoriteBeverageEnabled ? '1' : '0');
}

async function openConnection(page) {
  await openSettingsSection(page, '连接');
  await page.locator('[data-device-authority-content]').waitFor({ state: 'visible', timeout: 5_000 });
}

async function openSettingsSection(page, label) {
  const topTab = page.locator('[data-gamepad-tab-value="settings"]').first();
  await topTab.click();
  const trigger = page.locator('[data-settings-tabs]').getByRole('tab', { name: label, exact: true });
  await trigger.click();
  await page.waitForTimeout(80);
}

async function refreshAll(pages, expectedRows = null) {
  await Promise.all(pages.map(async (page) => {
    await openConnection(page);
    await refreshDevices(page);
    if (expectedRows !== null) await waitForDeviceRows(page, expectedRows);
  }));
}

async function refreshDevices(page) {
  const button = page.getByRole('button', { name: '刷新设备', exact: true });
  await button.waitFor({ state: 'visible', timeout: 5_000 });
  await waitForEnabled(button);
  await button.click();
  await waitForEnabled(button);
}

async function waitForEnabled(locator) {
  await locator.page().waitForFunction((element) => !element.disabled, await locator.elementHandle(), {
    timeout: 5_000,
  });
}

async function waitForDeviceRows(page, count) {
  await page.waitForFunction((expected) => (
    document.querySelectorAll('[data-device-authority-device]').length === expected
  ), count, { timeout: 8_000 });
}

async function assertInitialThreeClientState(pageA, pageB, pageC, state) {
  assert.equal(state.primaryDeviceId, devices.a.id);
  const backendB = await readState(devices.b);
  const backendC = await readState(devices.c);
  assert.equal(backendB.currentDeviceIsPrimary, false);
  assert.equal(backendB.currentDeviceProfile.autoRareConcurrency, 3);
  assert.equal(backendB.currentDeviceProfile.automationEnabled, true);
  assert.equal(backendC.currentDeviceIsPrimary, false);
  assert.equal(backendC.currentDeviceProfile.autoRareConcurrency, 4);
  assert.equal(backendC.currentDeviceProfile.missionRecipePriorityEnabled, false);
  for (const page of [pageA, pageB, pageC]) {
    assert.equal(await page.locator('[data-device-authority-device]').count(), 3);
    await waitForText(page.locator(`[data-device-authority-device="${devices.a.id}"]`), '主设备');
  }
  await waitForText(pageA.locator('[data-device-authority-content]'), '当前设备是主设备');
  await waitForText(pageA.locator('[data-device-authority-content]'), '生效配置版本 #1');
  await waitForText(pageB.locator('[data-device-authority-content]'), '本设备的共享功能设置为只读');
  await waitForText(pageC.locator('[data-device-authority-content]'), '本设备的共享功能设置为只读');
}

async function assertSwitch(page, helpId, checked, disabled) {
  const input = page.locator(`[data-setting-help-id="${helpId}"] input[type="checkbox"]`);
  await input.waitFor({ state: 'visible', timeout: 5_000 });
  assert.equal(await input.isChecked(), checked, `${helpId} checked state`);
  assert.equal(await input.isDisabled(), disabled, `${helpId} disabled state`);
}

async function setSwitch(page, helpId, checked) {
  const field = page.locator(`[data-setting-help-id="${helpId}"]`);
  const input = field.locator('input[type="checkbox"]');
  await input.waitFor({ state: 'visible', timeout: 5_000 });
  assert.equal(await input.isDisabled(), false, `${helpId} must be writable on the primary device`);
  if ((await input.isChecked()) !== checked) await field.locator('label').first().click();
  await page.waitForFunction(({ selector, expected }) => {
    const element = document.querySelector(selector);
    return element instanceof HTMLInputElement && element.checked === expected;
  }, {
    selector: `[data-setting-help-id="${helpId}"] input[type="checkbox"]`,
    expected: checked,
  });
}

async function assertStoredBoolean(page, suffix, expected) {
  const actual = await page.evaluate((key) => localStorage.getItem(key), `${storagePrefix}-${suffix}`);
  assert.equal(actual, expected ? '1' : '0', `stored ${suffix}`);
}

async function clickDeviceAction(page, deviceId, action) {
  const row = page.locator(`[data-device-authority-device="${deviceId}"]`);
  const button = row.getByRole('button', { name: action, exact: true });
  await button.waitFor({ state: 'visible', timeout: 5_000 });
  await waitForEnabled(button);
  await button.click();
}

async function assertPrimaryDialog(page, { expectWarning, screenshotName = '' }) {
  const dialog = page.getByRole('dialog').filter({ hasText: '切换主设备' });
  await dialog.waitFor({ state: 'visible', timeout: 3_000 });
  await page.waitForFunction((element) => getComputedStyle(element).opacity === '1', await dialog.elementHandle(), {
    timeout: 2_000,
  });
  const text = await dialog.innerText();
  assert.equal(text.includes('目标设备的配置与当前主设备不同'), expectWarning);
  const surfaces = await dialog.evaluate((element) => {
    const header = element.querySelector('.mantine-Modal-header');
    const overlay = document.querySelector('.mantine-Modal-overlay');
    const inspect = (target) => {
      assertElement(target);
      const style = getComputedStyle(target);
      return { backgroundColor: style.backgroundColor, opacity: style.opacity };
    };
    function assertElement(target) {
      if (!(target instanceof HTMLElement)) throw new Error('missing dialog surface element');
    }
    return { content: inspect(element), header: inspect(header), overlay: inspect(overlay) };
  });
  assert.equal(readCssColorAlpha(surfaces.content.backgroundColor), 1, JSON.stringify(surfaces));
  assert.equal(readCssColorAlpha(surfaces.header.backgroundColor), 1, JSON.stringify(surfaces));
  assert.equal(surfaces.content.opacity, '1');
  assert.equal(surfaces.header.opacity, '1');
  const overlayAlpha = readCssColorAlpha(surfaces.overlay.backgroundColor);
  assert.ok(overlayAlpha > 0 && overlayAlpha < 1, JSON.stringify(surfaces));
  if (screenshotName) await screenshot(page, screenshotName);
}

async function confirmPrimaryDialog(page) {
  const dialog = page.getByRole('dialog').filter({ hasText: '切换主设备' });
  await dialog.getByRole('button', { name: '确认切换', exact: true }).click();
  await dialog.waitFor({ state: 'hidden', timeout: 5_000 });
}

function readCssColorAlpha(value) {
  if (!value || value === 'transparent') return 0;
  const rgba = value.match(/^rgba?\((.+)\)$/i);
  if (rgba) {
    const parts = rgba[1].split(/[,/]/).map((part) => part.trim()).filter(Boolean);
    return parts.length >= 4 ? Number(parts.at(-1)) : 1;
  }
  const colorFunction = value.match(/^color\([^/]+(?:\/\s*([\d.]+%?))?\)$/i);
  if (colorFunction) {
    if (!colorFunction[1]) return 1;
    return colorFunction[1].endsWith('%')
      ? Number(colorFunction[1].slice(0, -1)) / 100
      : Number(colorFunction[1]);
  }
  throw new Error(`unsupported computed color: ${value}`);
}

async function waitForText(locator, text) {
  await locator.filter({ hasText: text }).waitFor({ state: 'visible', timeout: 5_000 });
}

async function waitForState(device, predicate) {
  const deadline = Date.now() + 10_000;
  let lastState = null;
  while (Date.now() < deadline) {
    lastState = await readState(device);
    if (predicate(lastState)) return lastState;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`device authority state did not reach the expected condition: ${JSON.stringify(lastState)}`);
}

async function readState(device) {
  const response = await fetch(`${apiUrl}/devices`, { headers: requestHeaders(device, 0) });
  const payload = await response.json();
  if (!response.ok) throw new Error(`GET /devices HTTP ${response.status}: ${JSON.stringify(payload)}`);
  return payload;
}

async function postAutomationLease(device, authorityRevision) {
  const response = await fetch(`${apiUrl}/automation/lease/acquire`, {
    method: 'POST',
    headers: requestHeaders(device, authorityRevision),
  });
  const payload = await response.json();
  if (!response.ok) throw new Error(`POST /automation/lease/acquire HTTP ${response.status}: ${JSON.stringify(payload)}`);
  return payload;
}

function requestHeaders(device, authorityRevision) {
  return {
    'X-Mystia-Steward-Companion-Token': apiToken,
    'X-Mystia-Steward-Companion-Client-Id': device.id,
    'X-Mystia-Steward-Companion-Client-Label': device.label,
    ...(authorityRevision > 0
      ? { 'X-Mystia-Steward-Companion-Authority-Revision': String(authorityRevision) }
      : {}),
  };
}

async function screenshot(page, fileName) {
  await page.screenshot({ path: path.join(outputDir, fileName), fullPage: true });
}
