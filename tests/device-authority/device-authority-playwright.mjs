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

  const offlineClient = await openOfflineClient(browser);
  clients.push(offlineClient);
  await assertOfflineExtensionModuleOwnership(offlineClient);
  await offlineClient.context.close();
  offlineClient.closed = true;
  checkpoints.push('断线时本设备模块可预设，主设备共享模块保持只读且不产生 API 请求');

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
  await assertConnectedExtensionModuleOwnership(clientA.page, true);
  await assertConnectedExtensionModuleOwnership(clientB.page, false, devices.a.label);
  await assertSameTickDeviceMutationSingleFlight(clientA.page);
  await screenshot(clientA.page, '01-three-clients-online.png');
  checkpoints.push('A/B/C 三个独立 Web 页面同时在线且只认 A 为主设备；同 tick 重复设备 mutation 只发送一次');

  await Promise.all([
    openSettingsSection(clientA.page, '推荐'),
    openSettingsSection(clientB.page, '推荐'),
    openSettingsSection(clientC.page, '推荐'),
  ]);
  await assertSwitch(clientA.page, 'recommendation-pin-favorite-recipe', false, false);
  await assertSwitch(clientB.page, 'recommendation-pin-favorite-recipe', false, true);
  await assertSwitch(clientC.page, 'recommendation-pin-favorite-recipe', false, true);

  const profileDraftResult = await assertPrimaryProfileDraftTransaction(clientA.page);
  assert.equal(profileDraftResult.requestCount, 2);
  state = await waitForState(devices.a, (next) => (
    next.activeProfile.pinFavoriteRecipeEnabled === true
      && next.activeProfile.missionRecipePriorityEnabled === false
  ));
  await refreshAll([clientB.page, clientC.page]);
  await Promise.all([
    openSettingsSection(clientB.page, '推荐'),
    openSettingsSection(clientC.page, '推荐'),
  ]);
  await assertSwitch(clientB.page, 'recommendation-pin-favorite-recipe', true, true);
  await assertSwitch(clientC.page, 'recommendation-pin-favorite-recipe', true, true);
  await assertSwitch(clientB.page, 'recommendation-mission-recipe-priority', false, true);
  await assertSwitch(clientC.page, 'recommendation-mission-recipe-priority', false, true);
  await assertStoredBoolean(clientB.page, 'pin-favorite-recipe', true);
  await assertStoredBoolean(clientC.page, 'pin-favorite-recipe', true);
  checkpoints.push('A 的草稿经过 debounce 旧 poll 与延迟 POST，写入期后续编辑以两次严格串行 CAS 生效，B/C 只读 UI 与 localStorage 均应用最终配置');

  state = await assertCrossGenerationProfileBarrier(clientA.page);
  assert.equal(state.activeProfile.missionRecipePriorityEnabled, true);
  await refreshAll([clientB.page, clientC.page]);
  checkpoints.push('A 正在处理的配置 POST 跨断开/重连保持传输阻断，响应后按新连接轮次重新注册并采用服务端生效配置');

  await openConnection(clientB.page);
  await clickDeviceAction(clientB.page, devices.b.id, '同步配置');
  await waitForState(devices.b, (next) => Boolean(next.pendingSyncId));
  await refreshDevices(clientB.page);
  state = await waitForState(devices.a, (next) => {
    const device = next.devices.find((item) => item.deviceId === devices.b.id);
    return Boolean(device && !device.syncPending && device.profileHash === next.activeProfileHash);
  });
  checkpoints.push('B 执行“同步配置”并完成待处理同步确认');

  state = await assertPendingSyncSingleFlightAndWriterGate(clientA.page, clientB.page);
  await refreshAll([clientA.page, clientB.page, clientC.page], 3);
  assert.match((await postAutomationLease(devices.a, state.authorityRevision)).error, /不是主设备/);
  assert.equal((await postAutomationLease(devices.b, state.authorityRevision)).ok, true);
  checkpoints.push('A -> B 转移后，A 的 pending-sync ACK 单飞且等待期 writer 保持关闭；旧主设备写入被拒绝，B 获得执行权');

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
  await page.locator('[data-gamepad-tab-value="overview"]').first().waitFor({ timeout: 12_000 });
  return { context, page, device, closed: false };
}

async function openOfflineClient(currentBrowser) {
  const context = await currentBrowser.newContext({ viewport: { width: 640, height: 760 } });
  await context.addInitScript(({ apiUrl: endpoint, prefix }) => {
    localStorage.setItem(`${prefix}-mod-api-endpoint`, endpoint);
    localStorage.removeItem(`${prefix}-mod-api-token`);
    localStorage.setItem(`${prefix}-client-id`, 'device-authority-offline-0001');
    localStorage.setItem(`${prefix}-mission-list-module-enabled`, '0');
    localStorage.setItem(`${prefix}-rare-guest-invitation-module-enabled`, '0');
    localStorage.setItem(`${prefix}-rare-guest-participation-module-enabled`, '0');
  }, { apiUrl, prefix: storagePrefix });
  const page = await context.newPage();
  const apiRequests = [];
  page.on('request', (request) => {
    if (request.url().startsWith(apiUrl)) apiRequests.push(`${request.method()} ${request.url()}`);
  });
  await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
  await page.locator('[data-gamepad-tab-value="extensions"]').first().waitFor({ timeout: 10_000 });
  return { context, page, device: null, apiRequests, closed: false };
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

async function setOverviewConnectionEnabled(page, enabled) {
  await page.locator('[data-gamepad-tab-value="overview"]').first().click();
  await page.locator('[data-overview-tabs]').getByRole('tab', { name: '连接', exact: true }).click();
  const field = page.locator('[data-gamepad-focus-key="overview:connection:toggle"]');
  const input = field.locator('input[type="checkbox"]');
  await input.waitFor({ state: 'visible', timeout: 5_000 });
  if ((await input.isChecked()) !== enabled) await field.click();
  await page.waitForFunction(({ selector, expected }) => {
    const element = document.querySelector(selector);
    return element instanceof HTMLInputElement && element.checked === expected;
  }, {
    selector: '[data-gamepad-focus-key="overview:connection:toggle"] input[type="checkbox"]',
    expected: enabled,
  }, { timeout: 5_000 });
}

async function openSettingsSection(page, label) {
  const topTab = page.locator('[data-gamepad-tab-value="settings"]').first();
  await topTab.click();
  const trigger = page.locator('[data-settings-tabs]').getByRole('tab', { name: label, exact: true });
  await trigger.click();
  await page.waitForTimeout(80);
}

async function openExtensionSection(page, label) {
  await page.locator('[data-gamepad-tab-value="extensions"]').first().click();
  await page.locator('[data-extension-tabs]').getByRole('tab', { name: label, exact: true }).click();
  await page.waitForTimeout(80);
}

async function assertOfflineExtensionModuleOwnership(client) {
  const { page } = client;
  for (const module of [
    {
      tab: '任务列表',
      id: 'task-list',
      focusKey: 'missions:module-toggle',
      storageKey: `${storagePrefix}-mission-list-module-enabled`,
    },
    {
      tab: '稀客邀请',
      id: 'rare-guest-invitations',
      focusKey: 'rare-invitations:module-toggle',
      storageKey: `${storagePrefix}-rare-guest-invitation-module-enabled`,
    },
  ]) {
    await openExtensionSection(page, module.tab);
    const panel = page.locator(`[data-feature-module="${module.id}"]`);
    await panel.waitFor({ state: 'visible', timeout: 5_000 });
    assert.equal(await panel.getAttribute('data-module-scope'), 'local-client');
    assert.equal(await panel.getAttribute('data-module-status'), 'disconnected');
    assert.equal(await panel.getAttribute('data-module-writable'), 'true');
    await waitForText(panel, '当前设备');
    const toggle = panel.locator(`[data-gamepad-focus-key="${module.focusKey}"]`);
    assert.equal(await toggle.isDisabled(), false, `${module.id} must remain writable offline`);
    await toggle.click();
    assert.equal(await page.evaluate((key) => localStorage.getItem(key), module.storageKey), '1');
  }

  await openExtensionSection(page, '稀客调度');
  const sharedPanel = page.locator('[data-feature-module="rare-guest-participation"]');
  await sharedPanel.waitFor({ state: 'visible', timeout: 5_000 });
  assert.equal(await sharedPanel.getAttribute('data-module-scope'), 'primary-profile');
  assert.equal(await sharedPanel.getAttribute('data-module-status'), 'disconnected');
  assert.equal(await sharedPanel.getAttribute('data-module-writable'), 'false');
  await waitForText(sharedPanel, '主设备共享');
  await waitForText(sharedPanel, '无法确认主设备和生效配置');
  const sharedToggle = sharedPanel.locator(
    '[data-gamepad-focus-key="extensions:rare-participation:module-toggle"]',
  );
  assert.equal(await sharedToggle.isDisabled(), true);
  assert.equal(
    await page.evaluate((key) => localStorage.getItem(key),
      `${storagePrefix}-rare-guest-participation-module-enabled`),
    '0',
  );
  assert.deepEqual(client.apiRequests, [], `Offline module toggles issued API requests: ${client.apiRequests.join(', ')}`);
}

async function assertConnectedExtensionModuleOwnership(page, primary, primaryLabel = '') {
  for (const [tab, id] of [
    ['任务列表', 'task-list'],
    ['稀客邀请', 'rare-guest-invitations'],
  ]) {
    await openExtensionSection(page, tab);
    const panel = page.locator(`[data-feature-module="${id}"]`);
    await panel.waitFor({ state: 'visible', timeout: 5_000 });
    assert.equal(await panel.getAttribute('data-module-scope'), 'local-client');
    assert.equal(await panel.getAttribute('data-module-writable'), 'true');
  }

  await openExtensionSection(page, '稀客调度');
  const sharedPanel = page.locator('[data-feature-module="rare-guest-participation"]');
  await sharedPanel.waitFor({ state: 'visible', timeout: 5_000 });
  assert.equal(await sharedPanel.getAttribute('data-module-scope'), 'primary-profile');
  assert.equal(await sharedPanel.getAttribute('data-module-status'), primary ? 'writable' : 'secondary-read-only');
  assert.equal(await sharedPanel.getAttribute('data-module-writable'), primary ? 'true' : 'false');
  if (!primary) await waitForText(sharedPanel, `当前由“${primaryLabel}”提供生效配置`);
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

async function waitForDisabled(locator) {
  const deadline = Date.now() + 5_000;
  while (Date.now() < deadline) {
    if (await locator.isDisabled()) return;
    await locator.page().waitForTimeout(50);
  }
  throw new Error(`Control did not become disabled: ${await locator.evaluate((element) => element.outerHTML)}`);
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

async function assertSameTickDeviceMutationSingleFlight(page) {
  const renameUrl = `${apiUrl}/devices/rename`;
  const renameStarted = createDeferredSignal();
  const releaseRename = createDeferredSignal();
  let renameRequestCount = 0;
  const renameRoute = async (route) => {
    renameRequestCount += 1;
    renameStarted.resolve();
    await releaseRename.promise;
    await route.continue();
  };

  await openConnection(page);
  const before = await readState(devices.a);
  const labelInput = page.getByLabel('当前设备名称', { exact: true });
  const originalLabel = await labelInput.inputValue();
  const nextLabel = `${originalLabel} · 单飞`;
  assert.ok(nextLabel.length <= 48);
  await labelInput.fill(nextLabel);
  const saveButton = page.getByRole('button', { name: '保存名称', exact: true });
  await waitForEnabled(saveButton);
  await page.route(renameUrl, renameRoute);
  try {
    await saveButton.evaluate((button) => {
      button.click();
      button.click();
    });
    await waitForSignal(renameStarted, 'same-tick device rename mutation', 3_000);
    await page.waitForTimeout(100);
    assert.equal(
      renameRequestCount,
      1,
      'Two device mutations dispatched in the same browser tick must share one synchronous command gate.',
    );
    releaseRename.resolve();
    const after = await waitForState(devices.a, (next) => (
      next.devices.find((device) => device.deviceId === devices.a.id)?.label === nextLabel
    ));
    assert.equal(
      after.stateRevision,
      before.stateRevision + 1,
      'The mock state revision independently proves that the duplicate rename was not committed.',
    );
  } finally {
    releaseRename.resolve();
    await page.unroute(renameUrl, renameRoute);
  }
}

async function assertCrossGenerationProfileBarrier(page) {
  const profileUrl = `${apiUrl}/devices/profile`;
  const registerUrl = `${apiUrl}/devices/register`;
  const profilePostStarted = createDeferredSignal();
  const releaseProfilePost = createDeferredSignal();
  const registerStarted = createDeferredSignal();
  let profileRequestCount = 0;
  let registerRequestCount = 0;
  const profileRoute = async (route) => {
    profileRequestCount += 1;
    profilePostStarted.resolve();
    await releaseProfilePost.promise;
    await route.continue();
  };
  const registerRoute = async (route) => {
    registerRequestCount += 1;
    registerStarted.resolve();
    await route.continue();
  };

  await openSettingsSection(page, '推荐');
  await assertSwitch(page, 'recommendation-mission-recipe-priority', false, false);
  await page.route(profileUrl, profileRoute);
  await page.route(registerUrl, registerRoute);
  try {
    await setSwitch(page, 'recommendation-mission-recipe-priority', true);
    await waitForSignal(profilePostStarted, 'cross-generation primary profile POST', 3_000);
    assert.equal(profileRequestCount, 1);

    await setOverviewConnectionEnabled(page, false);
    await waitForText(
      page.locator('[data-overview-connection-status-metric="connection"]'),
      '已停止',
    );
    await setOverviewConnectionEnabled(page, true);
    await waitForText(
      page.locator('[data-overview-connection-status-metric="connection"]'),
      '已连接',
    );

    await openSettingsSection(page, '推荐');
    const sharedSwitch = page.locator(
      '[data-setting-help-id="recommendation-mission-recipe-priority"] input[type="checkbox"]',
    );
    await waitForDisabled(sharedSwitch);
    assert.equal(
      registerRequestCount,
      0,
      'A new connection generation must not register against a baseline that an older profile POST can still change.',
    );
    assert.equal(profileRequestCount, 1, 'The in-flight transport barrier must reject another profile write.');
    assert.equal(
      await page.evaluate((key) => localStorage.getItem(key), `${storagePrefix}-mission-recipe-priority`),
      '0',
      'An in-flight profile must not enter the authoritative local cache.',
    );

    releaseProfilePost.resolve();
    await waitForSignal(registerStarted, 'post-barrier device re-registration', 5_000);
    const state = await waitForState(devices.a, (next) => (
      next.currentDeviceIsPrimary
        && next.activeProfile.missionRecipePriorityEnabled === true
        && next.pendingSyncId === null
    ));
    await openSettingsSection(page, '推荐');
    await page.waitForFunction(() => {
      const input = document.querySelector(
        '[data-setting-help-id="recommendation-mission-recipe-priority"] input[type="checkbox"]',
      );
      return input instanceof HTMLInputElement
        && input.checked
        && !input.disabled
        && localStorage.getItem('mystia-steward-companion-mission-recipe-priority') === '1';
    }, null, { timeout: 5_000 });
    assert.equal(registerRequestCount, 1, 'The settled profile outcome must trigger one fresh registration.');
    return state;
  } finally {
    releaseProfilePost.resolve();
    await page.unroute(profileUrl, profileRoute);
    await page.unroute(registerUrl, registerRoute);
  }
}

async function assertPendingSyncSingleFlightAndWriterGate(pageA, pageB) {
  const devicesUrl = `${apiUrl}/devices`;
  const syncAckUrl = `${apiUrl}/devices/sync-ack`;
  const stalePrimaryState = await readState(devices.a);
  assert.equal(stalePrimaryState.currentDeviceIsPrimary, true);
  const firstDeviceReadCaptured = createDeferredSignal();
  const secondDeviceReadCaptured = createDeferredSignal();
  const releaseDeviceReads = createDeferredSignal();
  const syncAckStarted = createDeferredSignal();
  const releaseSyncAck = createDeferredSignal();
  let deviceReadCount = 0;
  let syncAckRequestCount = 0;
  const runtimeWriterRequests = [];
  const recordRuntimeWriterRequest = (request) => {
    if (request.method() !== 'POST') return;
    if ([
      `${apiUrl}/automation/lease/acquire`,
      `${apiUrl}/automation/lease/release`,
    ].includes(request.url())) {
      runtimeWriterRequests.push(request.url());
    }
  };
  const devicesRoute = async (route) => {
    deviceReadCount += 1;
    if (deviceReadCount === 1) firstDeviceReadCaptured.resolve();
    if (deviceReadCount === 2) secondDeviceReadCaptured.resolve();
    await releaseDeviceReads.promise;
    await route.continue();
  };
  const syncAckRoute = async (route) => {
    syncAckRequestCount += 1;
    syncAckStarted.resolve();
    await releaseSyncAck.promise;
    await route.continue();
  };

  await pageA.route(devicesUrl, devicesRoute);
  await pageA.route(syncAckUrl, syncAckRoute);
  try {
    await waitForSignal(firstDeviceReadCaptured, 'automatic device-authority poll', 8_000);
    await openConnection(pageA);
    const refreshButton = pageA.getByRole('button', { name: '刷新设备', exact: true });
    await waitForEnabled(refreshButton);
    await refreshButton.click();
    await waitForSignal(secondDeviceReadCaptured, 'manual device-authority refresh', 3_000);
    assert.equal(deviceReadCount, 2, 'The ACK race requires exactly two pending authority observations.');

    await openConnection(pageB);
    await refreshDevices(pageB);
    await clickDeviceAction(pageB, devices.b.id, '设为主设备');
    await assertPrimaryDialog(pageB, {
      expectWarning: false,
      screenshotName: '02-synced-transfer-dialog.png',
    });
    await confirmPrimaryDialog(pageB);
    let state = await waitForState(devices.b, (next) => next.primaryDeviceId === devices.b.id);

    state = await postDeviceProfile(devices.b, state, {
      ...state.activeProfile,
      automationEnabled: true,
    });
    await postDeviceSync(devices.b, state.authorityRevision, devices.a.id);
    await waitForState(devices.a, (next) => Boolean(next.pendingSyncId));
    pageA.on('request', recordRuntimeWriterRequest);
    releaseDeviceReads.resolve();
    await waitForSignal(syncAckStarted, 'pending-sync acknowledgement', 3_000);
    await pageA.waitForTimeout(100);
    assert.equal(
      syncAckRequestCount,
      1,
      'Concurrent observations of one pending sync must share one acknowledgement request.',
    );

    await openSettingsSection(pageA, '推荐');
    await waitForDisabled(pageA.locator(
      '[data-setting-help-id="recommendation-pin-favorite-recipe"] input[type="checkbox"]',
    ));
    await pageA.evaluate(() => new Promise((resolve) => {
      requestAnimationFrame(() => requestAnimationFrame(resolve));
    }));
    assert.deepEqual(
      runtimeWriterRequests,
      [],
      'A pending-sync profile must not drive stale-primary runtime lease acquire/release requests.',
    );
    releaseSyncAck.resolve();
    state = await waitForState(devices.a, (next) => (
      next.primaryDeviceId === devices.b.id && next.pendingSyncId === null
    ));
    return state;
  } finally {
    pageA.off('request', recordRuntimeWriterRequest);
    releaseDeviceReads.resolve();
    releaseSyncAck.resolve();
    await pageA.unroute(devicesUrl, devicesRoute);
    await pageA.unroute(syncAckUrl, syncAckRoute);
  }
}

async function assertPrimaryProfileDraftTransaction(page) {
  const stalePollCaptured = createDeferredSignal();
  const releaseStalePoll = createDeferredSignal();
  const profilePostStarted = createDeferredSignal();
  const secondProfilePostStarted = createDeferredSignal();
  const releaseProfilePost = createDeferredSignal();
  const profileBodies = [];
  let stalePollHandled = false;
  let firstProfilePost = true;
  const devicesUrl = `${apiUrl}/devices`;
  const profileUrl = `${apiUrl}/devices/profile`;

  const devicesRoute = async (route) => {
    if (stalePollHandled) {
      await route.continue();
      return;
    }
    stalePollHandled = true;
    const staleResponse = await route.fetch();
    stalePollCaptured.resolve();
    await releaseStalePoll.promise;
    await route.fulfill({ response: staleResponse });
  };
  const profileRoute = async (route) => {
    profileBodies.push(JSON.parse(route.request().postData() || '{}'));
    if (firstProfilePost) {
      firstProfilePost = false;
      profilePostStarted.resolve();
      await releaseProfilePost.promise;
    } else if (profileBodies.length === 2) {
      secondProfilePostStarted.resolve();
    }
    await route.continue();
  };

  await page.route(devicesUrl, devicesRoute);
  await page.route(profileUrl, profileRoute);
  try {
    await waitForSignal(stalePollCaptured, 'automatic stale device poll', 8_000);
    await setSwitch(page, 'recommendation-pin-favorite-recipe', true);
    await Promise.all([
      assertSwitch(page, 'recommendation-pin-favorite-recipe', true, false),
      assertSwitch(page, 'recommendation-mission-recipe-priority', true, false),
    ]);

    releaseStalePoll.resolve();
    await waitForSignal(profilePostStarted, 'delayed primary profile POST', 3_000);
    await Promise.all([
      assertSwitch(page, 'recommendation-pin-favorite-recipe', true, false),
      assertSwitch(page, 'recommendation-mission-recipe-priority', true, false),
    ]);
    await setSwitch(page, 'recommendation-mission-recipe-priority', false);
    assert.equal(
      await page.evaluate((key) => localStorage.getItem(key), `${storagePrefix}-pin-favorite-recipe`),
      '0',
      'An unconfirmed profile draft must not replace the last authoritative local cache.',
    );
    assert.equal(profileBodies.length, 1, 'The first frozen draft must issue one profile POST.');
    assert.equal(profileBodies[0].profile.pinFavoriteRecipeEnabled, true);
    assert.equal(profileBodies[0].profile.missionRecipePriorityEnabled, true);

    releaseProfilePost.resolve();
    await waitForSignal(secondProfilePostStarted, 'queued primary profile POST', 5_000);
    assert.equal(profileBodies.length, 2, 'A posting-phase edit must use one subsequent CAS write.');
    assert.equal(profileBodies[1].profile.pinFavoriteRecipeEnabled, true);
    assert.equal(profileBodies[1].profile.missionRecipePriorityEnabled, false);
    await page.waitForFunction(() => {
      const recipe = document.querySelector(
        '[data-setting-help-id="recommendation-pin-favorite-recipe"] input[type="checkbox"]',
      );
      const mission = document.querySelector(
        '[data-setting-help-id="recommendation-mission-recipe-priority"] input[type="checkbox"]',
      );
      return recipe instanceof HTMLInputElement
        && mission instanceof HTMLInputElement
        && recipe.checked
        && !mission.checked
        && !recipe.disabled
        && !mission.disabled
        && localStorage.getItem('mystia-steward-companion-pin-favorite-recipe') === '1'
        && localStorage.getItem('mystia-steward-companion-mission-recipe-priority') === '0';
    }, null, { timeout: 5_000 });
    assert.equal(profileBodies.length, 2, 'The stale poll must not add a third profile POST.');
    return { requestCount: profileBodies.length };
  } finally {
    releaseStalePoll.resolve();
    releaseProfilePost.resolve();
    await page.unroute(devicesUrl, devicesRoute);
    await page.unroute(profileUrl, profileRoute);
  }
}

function createDeferredSignal() {
  let resolve;
  const promise = new Promise((next) => { resolve = next; });
  return { promise, resolve };
}

async function waitForSignal(signal, label, timeoutMs) {
  let timeoutId;
  try {
    await Promise.race([
      signal.promise,
      new Promise((_, reject) => {
        timeoutId = setTimeout(() => reject(new Error(`Timed out waiting for ${label}.`)), timeoutMs);
      }),
    ]);
  } finally {
    clearTimeout(timeoutId);
  }
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

async function postDeviceSync(device, authorityRevision, targetDeviceId) {
  const response = await fetch(`${apiUrl}/devices/sync`, {
    method: 'POST',
    headers: {
      ...requestHeaders(device, 0),
      'Content-Type': 'application/json; charset=utf-8',
    },
    body: JSON.stringify({
      protocolVersion: 1,
      expectedAuthorityRevision: authorityRevision,
      deviceId: targetDeviceId,
    }),
  });
  const payload = await response.json();
  if (!response.ok) throw new Error(`POST /devices/sync HTTP ${response.status}: ${JSON.stringify(payload)}`);
  return payload;
}

async function postDeviceProfile(device, state, profile) {
  const response = await fetch(`${apiUrl}/devices/profile`, {
    method: 'POST',
    headers: {
      ...requestHeaders(device, 0),
      'Content-Type': 'application/json; charset=utf-8',
    },
    body: JSON.stringify({
      protocolVersion: 1,
      profileSchemaVersion: 4,
      expectedAuthorityRevision: state.authorityRevision,
      expectedProfileRevision: state.currentDeviceProfileRevision,
      profile,
    }),
  });
  const payload = await response.json();
  if (!response.ok) throw new Error(`POST /devices/profile HTTP ${response.status}: ${JSON.stringify(payload)}`);
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
