import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, rm } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

const portOffset = process.pid % 1_000;
const apiPort = 46_000 + portOffset;
const appPort = 48_000 + portOffset;
const apiUrl = `http://127.0.0.1:${apiPort}`;
const appUrl = `http://127.0.0.1:${appPort}/`;
const apiToken = 'mock-token';
const storagePrefix = 'mystia-steward-companion';
const outputDir = process.env.RARE_ORDER_PARTICIPATION_UI_OUTPUT_DIR
  || '/tmp/mystia-companion-rare-order-participation-audit';
const chromiumExecutablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH?.trim();
const viewports = [
  { name: 'wide', width: 1280, height: 900 },
  { name: 'minimum', width: 640, height: 800 },
  { name: 'narrow', width: 390, height: 844 },
];
const defaultVisibleRareOrders = [
  { guestName: '米斯蒂娅', queuePosition: null },
  { guestName: '露米娅', queuePosition: null },
];
const finalVisibleRareOrders = [
  { guestName: '露米娅', queuePosition: 1 },
  { guestName: '米斯蒂娅', queuePosition: 2 },
];
const removedPausedOrderDescription = '已暂停：仅在稀客队列和诊断中保留；不显示经营推荐，也不参与高亮、新自动化或资源预约。已开锅任务等待恢复。';

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
let primaryContext = null;
let secondaryContext = null;
let pendingMutationGate = null;
let pendingMutationFailure = null;

try {
  await rm(outputDir, { recursive: true, force: true });
  await mkdir(outputDir, { recursive: true });
  await Promise.all([
    waitForUrl(`${apiUrl}/health`, mock, 'mock API'),
    waitForUrl(appUrl, preview, 'Vite preview'),
  ]);

  browser = await chromium.launch({
    headless: true,
    ...(chromiumExecutablePath ? { executablePath: chromiumExecutablePath } : {}),
  });

  const primary = await openClient(browser, {
    deviceId: 'rare-participation-primary-0001',
    width: viewports[0].width,
    height: viewports[0].height,
  });
  primaryContext = primary.context;
  await primary.page.route(`${apiUrl}/orders/rare/participation`, async (route) => {
    const failure = pendingMutationFailure;
    if (route.request().method() === 'POST' && failure && !failure.claimed) {
      failure.claimed = true;
      failure.markEntered();
      await failure.released;
      if (pendingMutationFailure === failure) pendingMutationFailure = null;
      await route.fulfill({
        status: 409,
        headers: {
          'access-control-allow-origin': '*',
          'content-type': 'application/json; charset=utf-8',
        },
        body: JSON.stringify({ ok: false, error: failure.error }),
      });
      return;
    }
    const gate = pendingMutationGate;
    if (route.request().method() === 'POST' && gate && !gate.claimed) {
      gate.claimed = true;
      gate.markEntered();
      await gate.released;
      if (pendingMutationGate === gate) pendingMutationGate = null;
    }
    await route.continue();
  });

  await assertDefaultModuleDisabled(primary.page);
  await primary.page.screenshot({
    path: path.join(outputDir, '0-default-module-disabled.png'),
    fullPage: true,
  });
  await enableParticipationModule(primary.page);
  await assertQueueSelectionResetAcrossModuleToggle(primary.page);

  for (const [index, viewport] of viewports.entries()) {
    await primary.page.setViewportSize({ width: viewport.width, height: viewport.height });
    await primary.page.evaluate(({ prefix, scale }) => {
      localStorage.setItem(`${prefix}-font-scale-percent`, String(scale * 100));
      document.documentElement.style.setProperty('--companion-font-scale', String(scale));
    }, { prefix: storagePrefix, scale: viewport.name === 'narrow' ? 1.3 : 1 });
    await assertExtensionRoster(primary.page, viewport.name, false);
    await primary.page.screenshot({
      path: path.join(outputDir, `${index + 1}-${viewport.name}-extension.png`),
      fullPage: true,
    });

    await openServiceQueue(primary.page);
    await assertServiceTabs(primary.page, viewport.name);
    if (index === 0) await assertPrimaryParticipationLifecycle(primary.page);
    await assertRareRecommendationViews(
      primary.page,
      finalVisibleRareOrders,
      `${viewport.name} 最终稀客队列`,
      { checkOverflow: true },
    );
    await openServiceQueue(primary.page);
    await assertNoHorizontalOverflow(
      primary.page,
      '[data-rare-order-participation-panel="true"]',
      `${viewport.name} 稀客队列`,
    );
    await primary.page.screenshot({
      path: path.join(outputDir, `${index + 1}-${viewport.name}-queue.png`),
      fullPage: true,
    });
  }

  const secondary = await openClient(browser, {
    deviceId: 'rare-participation-secondary-0002',
    width: 390,
    height: 844,
    fontScale: 130,
  });
  secondaryContext = secondary.context;
  await assertExtensionRoster(secondary.page, 'secondary', true);
  const secondaryExtension = secondary.page.locator('[data-rare-guest-participation-module="true"]');
  await secondaryExtension.getByText(/此模块只能在主设备修改/).first().waitFor({ timeout: 12_000 });
  assert.equal(
    await secondaryExtension.locator('[data-gamepad-focus-key="extensions:rare-participation:module-toggle"]').isDisabled(),
    true,
    '从设备不应允许切换稀客调度模块',
  );
  await assertAllDisabled(
    secondaryExtension.locator('[data-gamepad-focus-key^="extensions:rare-participation:guest:"]'),
    '从设备扩展页的全部名单操作',
  );

  await openServiceQueue(secondary.page);
  const secondaryQueue = secondary.page.locator('[data-rare-order-participation-panel="true"]');
  await waitForManagedGroups(secondary.page);
  await assertQueueHeader(secondary.page, '从设备稀客队列');
  await assertAllDisabled(
    participationMutationButtons(secondaryQueue),
    '从设备稀客队列的全部状态操作',
  );
  await assertNoHorizontalOverflow(secondary.page, '[data-rare-order-participation-panel="true"]', '从设备稀客队列');
  await secondary.page.screenshot({
    path: path.join(outputDir, '4-secondary-read-only-queue.png'),
    fullPage: true,
  });
  await assertSecondaryQueueSelectionResetAcrossPrimaryModuleToggle(
    primary.page,
    secondary.page,
  );

  console.log('PASS: rare-order participation extension module and service queue UI audit completed.');
  console.log('- 模块默认关闭时不生成稀客队列页签，关闭后稳定切回稀客页且重开不恢复旧队列选择');
  console.log('- 1280/640/390 扩展名单搜索、调度状态、移出确认与取消焦点返回通过');
  console.log('- 经营中稀客/稀客队列/普客三个页签与横向溢出检查通过');
  console.log('- 默认暂停、订单级优先启用、稀客级非抢占优先、单订单暂停、队尾重启与所有修改按钮忙碌状态通过');
  console.log('- 普通稀客页与专注模式按 Mod 稀客队列同步隐藏、显示和排序；失败的修改操作不改变可见集合');
  console.log('- 从设备模块开关、扩展名单、队列操作禁用与主设备远程关闭/重开一致性检查通过');
  console.log(`Artifacts: ${outputDir}`);
} finally {
  pendingMutationGate?.release();
  pendingMutationFailure?.release();
  await secondaryContext?.close().catch(() => undefined);
  await primaryContext?.close().catch(() => undefined);
  await browser?.close().catch(() => undefined);
  await Promise.all([stopService(preview), stopService(mock)]);
}

async function assertDefaultModuleDisabled(page) {
  await openExtensionSection(page, '稀客调度');
  const module = page.locator('[data-rare-guest-participation-module="true"]');
  await module.waitFor({ state: 'visible', timeout: 10_000 });
  const toggle = module.locator('[data-gamepad-focus-key="extensions:rare-participation:module-toggle"]');
  assert.equal(await toggle.isChecked(), false, '稀客调度模块必须默认关闭');
  await module.getByText(/稀客调度模块已停用.*已保存 2 名稀客/).waitFor({ timeout: 12_000 });
  assert.equal(await module.locator('[data-rare-guest-participation-roster="true"]').count(), 0);

  await openServiceRecommendations(page);
  await assertQueueTabUnavailable(page, '默认关闭');
  await assertRareRecommendationViews(page, defaultVisibleRareOrders, '模块关闭时的原有流程');
}

async function enableParticipationModule(page) {
  await setParticipationModuleEnabled(page, true);
}

async function disableParticipationModule(page) {
  await setParticipationModuleEnabled(page, false);
}

async function setParticipationModuleEnabled(page, enabled) {
  await openExtensionSection(page, '稀客调度');
  const module = page.locator('[data-rare-guest-participation-module="true"]');
  const toggle = module.locator('[data-gamepad-focus-key="extensions:rare-participation:module-toggle"]');
  if ((await toggle.isChecked()) !== enabled) await toggle.click();
  await page.waitForFunction((expected) => {
    const element = document.querySelector(
      '[data-gamepad-focus-key="extensions:rare-participation:module-toggle"]',
    );
    return element instanceof HTMLInputElement && element.checked === expected;
  }, enabled, { timeout: 5_000 });
  await module.locator('[data-rare-guest-participation-roster="true"]').waitFor({
    state: enabled ? 'visible' : 'detached',
    timeout: 12_000,
  });
}

async function assertQueueSelectionResetAcrossModuleToggle(page) {
  await openServiceQueue(page);
  assert.equal(
    await page.locator('[data-service-order-tab-trigger="rare-queue"]').getAttribute('aria-selected'),
    'true',
    '动态关闭前应先选中稀客队列',
  );

  try {
    await disableParticipationModule(page);
    await openServiceRecommendations(page);
    await assertQueueTabUnavailable(page, '从稀客队列关闭模块');
  } finally {
    await enableParticipationModule(page);
  }
  await openServiceRecommendations(page);
  const queueTrigger = page.locator('[data-service-order-tab-trigger="rare-queue"]');
  await queueTrigger.waitFor({ state: 'visible', timeout: 12_000 });
  assert.equal(
    await page.locator('[data-service-order-tab="rare-queue"]').count(),
    1,
    '重新开启模块后应重新生成稀客队列内容',
  );
  assert.equal(
    await queueTrigger.getAttribute('aria-selected'),
    'false',
    '重新开启模块后不应恢复旧稀客队列选择',
  );
  await assertRareTabActive(page, '重新开启模块');
}

async function assertSecondaryQueueSelectionResetAcrossPrimaryModuleToggle(primaryPage, secondaryPage) {
  assert.equal(
    await secondaryPage.locator('[data-service-order-tab-trigger="rare-queue"]').getAttribute('aria-selected'),
    'true',
    '主设备远程关闭前从设备应仍停留在稀客队列',
  );

  try {
    await disableParticipationModule(primaryPage);
    await assertQueueTabUnavailable(secondaryPage, '主设备远程关闭后的从设备');
  } finally {
    await enableParticipationModule(primaryPage);
  }

  const secondaryQueueTrigger = secondaryPage.locator(
    '[data-service-order-tab-trigger="rare-queue"]',
  );
  await secondaryQueueTrigger.waitFor({ state: 'visible', timeout: 12_000 });
  await secondaryPage.waitForFunction(() => (
    document.querySelectorAll('[data-service-order-tab="rare-queue"]').length === 1
  ), null, { timeout: 12_000 });
  assert.equal(
    await secondaryQueueTrigger.getAttribute('aria-selected'),
    'false',
    '主设备重新开启模块后，从设备不应恢复旧稀客队列选择',
  );
  await assertRareTabActive(secondaryPage, '主设备重新开启后的从设备');
}

async function assertQueueTabUnavailable(page, label) {
  await page.waitForFunction(() => (
    !document.querySelector('[data-service-order-tab-trigger="rare-queue"]')
      && !document.querySelector('[data-service-order-tab="rare-queue"]')
      && !document.querySelector('[data-rare-order-participation-panel="true"]')
  ), null, { timeout: 12_000 });
  assert.equal(
    await page.locator('[data-service-order-tab-trigger]').count(),
    2,
    `${label}时经营推荐应只保留稀客和普客两个 Tab`,
  );
  assert.equal(await page.locator('[data-service-order-tab-trigger="normal"]').count(), 1);
  await assertRareTabActive(page, label);
}

async function assertRareTabActive(page, label) {
  const rareTrigger = page.locator('[data-service-order-tab-trigger="rare"]');
  await rareTrigger.waitFor({ state: 'visible', timeout: 12_000 });
  assert.equal(await rareTrigger.getAttribute('aria-selected'), 'true', `${label}时应选中稀客 Tab`);
  await page.locator('[data-service-order-tab="rare"]').waitFor({ state: 'visible', timeout: 12_000 });
  await page.locator('[data-service-order-collection="rare"]').waitFor({ state: 'visible', timeout: 12_000 });
}

async function assertExtensionRoster(page, profileName, readOnly) {
  await openExtensionSection(page, '稀客调度');
  const root = page.locator('[data-rare-guest-participation-roster="true"]');
  await root.waitFor({ state: 'visible', timeout: 10_000 });

  const managed1001 = managedRow(root, 1001, true);
  const managed1002 = managedRow(root, 1002, true);
  await managed1001.waitFor({ state: 'visible', timeout: 12_000 });
  await managed1002.waitFor({ state: 'visible', timeout: 12_000 });
  await root.getByRole('heading', { name: '已加入名单 (2)', exact: true }).waitFor({ timeout: 12_000 });
  await managed1001.getByText('当前 1 笔', { exact: true }).waitFor({ timeout: 12_000 });

  const search = root.getByPlaceholder('输入姓名、ID或地区', { exact: true });
  assert.equal(await search.isDisabled(), false, `${profileName}: 稀客搜索不应禁用`);
  await search.fill('米斯蒂娅');
  await managed1001.waitFor({ state: 'visible' });
  await managed1002.waitFor({ state: 'detached' });
  await root.getByRole('heading', { name: '已加入名单 (1)', exact: true }).waitFor();
  await search.fill('');
  await managed1002.waitFor({ state: 'visible' });
  await root.getByRole('heading', { name: '已加入名单 (2)', exact: true }).waitFor();

  if (!readOnly) await assertRemovalCancelReturnsFocus(page, root);
  await assertNoHorizontalOverflow(page, '[data-rare-guest-participation-module="true"]', `${profileName} 稀客调度`);
}

async function assertRemovalCancelReturnsFocus(page, root) {
  const returnKey = 'extensions:rare-participation:guest:1001:remove';
  const removeButton = root.locator(`[data-gamepad-focus-key="${returnKey}"]`);
  await removeButton.click();
  const dialog = page.getByRole('dialog').filter({ hasText: '移出调度名单' });
  await dialog.waitFor({ state: 'visible' });
  assert.match(await dialog.innerText(), /米斯蒂娅.*当前还有 1 笔订单/s, '移出确认未说明当前订单影响');
  await dialog.getByRole('button', { name: '取消', exact: true }).click();
  await dialog.waitFor({ state: 'hidden' });
  await page.waitForFunction((focusKey) => (
    document.activeElement?.getAttribute('data-gamepad-focus-key') === focusKey
  ), returnKey, { timeout: 3_000 });
}

async function assertServiceTabs(page, profileName) {
  const expectedTabs = new Map([
    ['rare', '稀客'],
    ['rare-queue', '稀客队列'],
    ['normal', '普客'],
  ]);
  for (const [kind, label] of expectedTabs) {
    const trigger = page.locator(`[data-service-order-tab-trigger="${kind}"]`);
    assert.equal((await trigger.innerText()).trim(), label, `${profileName}: ${kind} 页签文案不正确`);
    await trigger.click();
    await page.locator(`[data-service-order-tab="${kind}"]`).waitFor({ state: 'visible', timeout: 10_000 });
  }
  await page.locator('[data-service-order-tab-trigger="rare-queue"]').click();
  const panel = page.locator('[data-rare-order-participation-panel="true"]');
  await panel.waitFor({ state: 'visible' });
  await panel.getByRole('heading', { name: '稀客队列', exact: true }).waitFor();
  await waitForManagedGroups(page);
  await assertQueueHeader(page, `${profileName} 稀客队列`);
}

async function assertQueueHeader(page, label) {
  const panel = page.locator('[data-rare-order-participation-panel="true"]');
  const summaryPanel = panel.locator('.steward-list-panel').first();
  const header = summaryPanel.locator('.steward-panel-header');
  await header.waitFor({ state: 'visible', timeout: 12_000 });
  assert.equal(
    await panel.locator('[data-rare-order-participation-disclosure]').count(),
    0,
    `${label}不应保留队列说明 disclosure`,
  );
  assert.equal(
    await panel.getByText('队列说明', { exact: true }).count(),
    0,
    `${label}不应显示队列说明`,
  );
  assert.equal(
    await summaryPanel.locator('[data-list-panel-content="true"]').count(),
    0,
    `${label}无错误时不应保留空内容区`,
  );
  assert.equal(
    await header.locator('[data-rare-order-participation-read-only="true"]').count(),
    0,
    `${label}不应显示只读提示`,
  );
  assert.equal(
    await panel.getByText(removedPausedOrderDescription, { exact: true }).count(),
    0,
    `${label}不应显示已移除的暂停订单长描述`,
  );
  const headerLayout = await summaryPanel.evaluate((element) => {
    const headerElement = element.querySelector('.steward-panel-header');
    if (!(headerElement instanceof HTMLElement)) return { ok: false, reason: 'header missing' };
    const panelRect = element.getBoundingClientRect();
    const headerRect = headerElement.getBoundingClientRect();
    const action = headerElement.lastElementChild;
    const actionRect = action instanceof HTMLElement ? action.getBoundingClientRect() : null;
    return {
      ok: headerElement.dataset.listPanelHeaderOnly === 'true'
        && Number.parseFloat(getComputedStyle(headerElement).borderBottomWidth) === 0
        && element.scrollWidth <= element.clientWidth + 1
        && headerElement.scrollWidth <= headerElement.clientWidth + 1
        && headerRect.left >= panelRect.left - 1
        && headerRect.right <= panelRect.right + 1
        && (!actionRect || (actionRect.left >= headerRect.left - 1 && actionRect.right <= headerRect.right + 1)),
      headerOnly: headerElement.dataset.listPanelHeaderOnly,
      borderBottomWidth: getComputedStyle(headerElement).borderBottomWidth,
      panelSize: `${element.clientWidth}/${element.scrollWidth}`,
      headerSize: `${headerElement.clientWidth}/${headerElement.scrollWidth}`,
    };
  });
  assert.equal(
    headerLayout.ok,
    true,
    `${label}标题区布局不稳定：${JSON.stringify(headerLayout)}`,
  );
}

async function assertPrimaryParticipationLifecycle(page) {
  await waitForManagedGroups(page);
  assert.equal(
    await page.locator('[data-rare-order-participation-state="paused"]').count(),
    2,
    '调度名单内稀客的两笔当前订单都应默认暂停',
  );
  assert.equal(
    await page.locator('[data-rare-order-participation-state="queued"]').count(),
    0,
    '调度名单内稀客的订单不应在手动启用前进入稀客队列',
  );
  await assertRareRecommendationViews(page, [], '默认暂停');
  await openServiceQueue(page);

  const enable1001 = participationOrderButton(page, 'R-0001', 1, 'enable-front');
  const pause1001 = participationButton(page, 1001, 'pause');
  assert.equal(await enable1001.isEnabled(), true, '默认暂停订单应允许启用');
  assert.equal(await pause1001.isDisabled(), true, '默认暂停订单不应允许重复暂停');
  const highlighterPublished = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return url.pathname === '/ui-pinning/targets'
      && response.ok()
      && [...url.searchParams.keys()].some((key) => key.endsWith('TraceId')
        && url.searchParams.get(key) === 'R-0001');
  });
  await enable1001.click();
  let group1001 = await waitForGuestState(page, 1001, 'queued');
  assert.equal(readQueuePosition(await group1001.innerText()), 1, '无在途任务时首笔优先启用应位于队首');
  await withTimeout(highlighterPublished, 5_000, '没有观测到首笔启用订单的高亮目标发布');
  await assertRareRecommendationViews(
    page,
    [{ guestName: '米斯蒂娅', queuePosition: 1 }],
    '单订单优先启用',
  );
  await openServiceQueue(page);
  group1001 = await waitForGuestState(page, 1001, 'queued');

  const enable1002Front = participationButton(page, 1002, 'enable-front');
  await enable1002Front.click();
  const group1002 = await waitForGuestState(page, 1002, 'queued');
  assert.equal(
    readQueuePosition(await group1001.innerText()),
    1,
    '优先启用第二笔订单不得挤占当前高亮订单',
  );
  assert.equal(
    readQueuePosition(await group1002.innerText()),
    2,
    '优先启用第二笔订单应排到当前高亮订单之后',
  );
  await assertRareRecommendationViews(
    page,
    [
      { guestName: '米斯蒂娅', queuePosition: 1 },
      { guestName: '露米娅', queuePosition: 2 },
    ],
    '整稀客优先启用',
  );
  await openServiceQueue(page);
  group1001 = await waitForGuestState(page, 1001, 'queued');

  const gate = createMutationGate();
  pendingMutationGate = gate;
  const pauseOrder1001 = participationOrderButton(page, 'R-0001', 1, 'pause');
  await pauseOrder1001.click();
  await withTimeout(gate.entered, 5_000, '没有观测到暂停参与状态 POST');
  const panel = page.locator('[data-rare-order-participation-panel="true"]');
  const allMutationActions = participationMutationButtons(panel);
  await page.waitForFunction(() => {
    const buttons = Array.from(document.querySelectorAll(
      '[data-rare-order-participation-panel="true"] '
        + ':is([data-gamepad-focus-key^="service:rare-participation:guest:"], '
        + '[data-gamepad-focus-key^="service:rare-participation:order:"])',
    ));
    return buttons.length === 8 && buttons.every((button) => button instanceof HTMLButtonElement && button.disabled);
  }, null, { timeout: 3_000 });
  assert.equal(await panel.getAttribute('data-busy'), 'true', '稀客队列修改期间应标记为忙碌');
  const participationStatus = page.locator('[data-rare-order-participation-status="true"]');
  assert.equal(
    await participationStatus.textContent(),
    '稀客队列更新中。',
    '稀客队列修改期间应播报更新状态',
  );
  assert.equal(
    await participationStatus.evaluate((element) => element.closest('[aria-busy="true"]') === null),
    true,
    '状态播报区域不应位于 `aria-busy` 子树内，以免更新播报被延迟',
  );
  assert.equal(await allMutationActions.count(), 8, '两组及两笔订单应存在八个队列状态按钮');
  gate.release();
  group1001 = await waitForGuestState(page, 1001, 'paused');
  assert.doesNotMatch(await group1001.innerText(), /队列 #/, '暂停后不应保留有效队列位置');
  assert.equal(readQueuePosition(await group1002.innerText()), 1, '单订单暂停后后续队列应连续压缩');
  await assertFocusedGamepadKey(
    page,
    'service:rare-participation:order:R-0001:1:enable-front',
    '成功暂停后应把焦点恢复到同一订单作用域的首个可用操作',
  );
  const visibleBeforeFailedMutation = await assertRareRecommendationViews(
    page,
    [{ guestName: '露米娅', queuePosition: 1 }],
    '单订单暂停',
  );
  await openServiceQueue(page);

  const failure = createMutationFailure('模拟稀客参与状态更新失败。');
  pendingMutationFailure = failure;
  const failedEnable1001Tail = participationOrderButton(page, 'R-0001', 1, 'enable-tail');
  await failedEnable1001Tail.click();
  await withTimeout(failure.entered, 5_000, '没有观测到预期失败的参与状态 POST');
  await page.waitForFunction(() => Array.from(document.querySelectorAll(
    '[data-rare-order-participation-action="true"]',
  )).every((button) => button instanceof HTMLButtonElement && button.disabled), null, { timeout: 3_000 });
  failure.release();
  await panel.getByRole('alert').getByText(failure.error, { exact: true }).waitFor({ timeout: 5_000 });
  const errorHeader = panel.locator('.steward-list-panel').first().locator('.steward-panel-header');
  assert.equal(await errorHeader.getAttribute('data-list-panel-header-only'), null, '错误态不应标记为纯标题卡');
  assert.ok(
    Number.parseFloat(await errorHeader.evaluate((element) => getComputedStyle(element).borderBottomWidth)) > 0,
    '错误态应恢复标题与内容区分隔线',
  );
  assert.equal(
    await panel.locator('.steward-list-panel').first().locator('[data-list-panel-content="true"]').count(),
    1,
    '队列错误应按需打开标题卡内容区',
  );
  group1001 = await waitForGuestState(page, 1001, 'paused');
  await assertFocusedGamepadKey(
    page,
    'service:rare-participation:order:R-0001:1:enable-front',
    '失败后应把焦点恢复到同一订单作用域的首个可用操作',
  );
  assert.equal(readQueuePosition(await group1002.innerText()), 1, '失败 mutation 不得更改既有队列');
  const visibleAfterFailedMutation = await assertRareRecommendationViews(
    page,
    [{ guestName: '露米娅', queuePosition: 1 }],
    '409 冲突后',
  );
  assert.deepEqual(
    visibleAfterFailedMutation,
    visibleBeforeFailedMutation,
    '409 冲突不得改变普通稀客页或专注模式的可见集合与顺序',
  );
  await openServiceQueue(page);

  const enable1001Tail = participationButton(page, 1001, 'enable-tail');
  await enable1001Tail.click();
  group1001 = await waitForGuestState(page, 1001, 'queued');
  assert.equal(readQueuePosition(await group1001.innerText()), 2, '稀客级队尾启用应追加到当前队列末尾');
  assert.equal(readQueuePosition(await group1002.innerText()), 1, '队尾启用不得重排已启用订单');
  await assertFocusedGamepadKey(
    page,
    'service:rare-participation:guest:1001:pause',
    '稀客级成功 mutation 后应把焦点恢复到同一稀客作用域的首个可用操作',
  );
  await assertRareRecommendationViews(page, finalVisibleRareOrders, '整稀客队尾启用');
  await openServiceQueue(page);
}

async function openExtensionSection(page, label) {
  const topTab = page.locator('[data-gamepad-tab-value="extensions"]').first();
  await topTab.scrollIntoViewIfNeeded();
  await topTab.click();
  const trigger = page.locator('[data-extension-tabs]').getByRole('tab', { name: label, exact: true });
  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
}

async function openServiceQueue(page) {
  await openServiceRecommendations(page);
  await page.locator('[data-service-order-tab-trigger="rare-queue"]').click();
  await page.locator('[data-rare-order-participation-panel="true"]').waitFor({ state: 'visible', timeout: 10_000 });
}

async function openServiceRecommendations(page) {
  const topTab = page.locator('[data-gamepad-tab-value="service"]').first();
  await topTab.scrollIntoViewIfNeeded();
  await topTab.click();
  const serviceViewControl = page.locator('[data-slot="segmented-control"]').filter({ hasText: '推荐' }).first();
  await serviceViewControl.locator('label').filter({ hasText: /^推荐$/ }).click();
}

async function openRareRecommendations(page) {
  await openServiceRecommendations(page);
  await page.locator('[data-service-order-tab-trigger="rare"]').click();
  await page.locator('[data-service-order-tab="rare"]').waitFor({ state: 'visible', timeout: 10_000 });
  await page.locator('[data-service-order-collection="rare"]').waitFor({ state: 'visible', timeout: 10_000 });
}

async function assertRareRecommendationViews(
  page,
  expected,
  label,
  { checkOverflow = false } = {},
) {
  await openRareRecommendations(page);
  const regularRows = await waitForRareRecommendationRows(page, 'rare', expected, `${label}普通页`);
  if (checkOverflow) {
    await assertNoHorizontalOverflow(
      page,
      '[data-service-order-tab="rare"]',
      `${label}普通稀客页`,
    );
  }

  const regularTab = page.locator('[data-service-order-tab="rare"]');
  await regularTab.getByRole('button', { name: '专注模式', exact: true }).click();
  await page.locator('[data-service-focus-page="true"]').waitFor({ state: 'visible', timeout: 10_000 });
  const focusRows = await waitForRareRecommendationRows(page, 'rare-focus', expected, `${label}专注模式`);
  assert.deepEqual(focusRows, regularRows, `${label}：普通稀客页与专注模式的集合或顺序不一致`);
  if (checkOverflow) {
    await assertNoHorizontalOverflow(
      page,
      '[data-service-focus-page="true"]',
      `${label}稀客专注模式`,
    );
  }

  await page.getByRole('button', { name: '退出专注模式', exact: true }).click();
  await page.locator('[data-service-order-tab="rare"]').waitFor({ state: 'visible', timeout: 10_000 });
  return regularRows;
}

async function waitForRareRecommendationRows(page, mode, expected, label) {
  const selector = `[data-service-order-collection="${mode}"]`;
  const collection = page.locator(selector);
  await collection.waitFor({ state: 'visible', timeout: 10_000 });
  await page.waitForFunction(({ collectionSelector, expectedRows }) => {
    const root = document.querySelector(collectionSelector);
    if (!(root instanceof HTMLElement)) return false;
    const cards = Array.from(root.querySelectorAll('[data-service-order-card="true"]'));
    if (cards.length !== expectedRows.length) return false;
    if (root.getAttribute('data-service-order-count') !== String(expectedRows.length)) return false;
    if (expectedRows.length === 0) {
      return root.getAttribute('data-service-order-state') === 'empty';
    }
    return cards.every((card, index) => {
      const text = card.textContent ?? '';
      const expectedRow = expectedRows[index];
      const queuePositionMatches = expectedRow.queuePosition === null
        ? !text.includes('已启用 · 队列 #')
        : text.includes(`已启用 · 队列 #${expectedRow.queuePosition}`);
      return text.includes(`${expectedRow.guestName} · 桌`)
        && queuePositionMatches;
    });
  }, { collectionSelector: selector, expectedRows: expected }, { timeout: 12_000 });

  const cardTexts = await collection.locator('[data-service-order-card="true"]').allInnerTexts();
  const rows = cardTexts.map((text) => {
    const guest = expected.find((candidate) => text.includes(`${candidate.guestName} · 桌`));
    const queuePositionMatch = text.match(/已启用 · 队列 #(\d+)/);
    const queuePosition = queuePositionMatch ? Number(queuePositionMatch[1]) : null;
    return { guestName: guest?.guestName ?? '', queuePosition };
  });
  assert.deepEqual(rows, expected, `${label}未按 participation queue 显示当前订单`);
  return rows;
}

async function openClient(currentBrowser, { deviceId, width, height, fontScale = 100 }) {
  const context = await currentBrowser.newContext({ viewport: { width, height } });
  await context.addInitScript(seedClientStorage, {
    endpoint: apiUrl,
    token: apiToken,
    prefix: storagePrefix,
    deviceId,
    fontScale,
  });
  const page = await context.newPage();
  await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
  await page.locator('[data-gamepad-tab-value="service"]').first().waitFor({ timeout: 12_000 });
  return { context, page };
}

function seedClientStorage({ endpoint, token, prefix, deviceId, fontScale }) {
  localStorage.setItem(`${prefix}-mod-api-endpoint`, endpoint);
  localStorage.setItem(`${prefix}-mod-api-token`, token);
  localStorage.setItem(`${prefix}-client-id`, deviceId);
  localStorage.setItem(`${prefix}-managed-rare-guest-ids`, JSON.stringify([1001, 1002]));
  localStorage.setItem(`${prefix}-rare-order-highlight`, '1');
  localStorage.setItem(`${prefix}-show-debug-details`, '1');
  localStorage.setItem(`${prefix}-font-scale-percent`, String(fontScale));
}

function managedRow(root, guestId, selected) {
  return root.locator(
    `[data-managed-rare-guest-id="${guestId}"][data-managed-rare-guest-selected="${selected}"]`,
  );
}

function participationButton(page, guestId, action) {
  return page.locator(
    `[data-gamepad-focus-key="service:rare-participation:guest:${guestId}:${action}"]`,
  );
}

function participationGroup(page, guestId) {
  return page.locator('.steward-list-panel').filter({
    has: participationButton(page, guestId, 'enable-front'),
  }).first();
}

function participationOrderButton(page, traceId, lifecycle, action) {
  return page.locator(
    `[data-gamepad-focus-key="service:rare-participation:order:${traceId}:${lifecycle}:${action}"]`,
  );
}

function participationMutationButtons(root) {
  return root.locator(
    ':is([data-gamepad-focus-key^="service:rare-participation:guest:"], '
      + '[data-gamepad-focus-key^="service:rare-participation:order:"])',
  );
}

async function waitForManagedGroups(page) {
  await waitForGuestState(page, 1001, /^(paused|queued)$/);
  await waitForGuestState(page, 1002, /^(paused|queued)$/);
  assert.equal(await page.locator('[data-rare-order-participation-state="unavailable"]').count(), 0);
}

async function waitForGuestState(page, guestId, state) {
  const group = participationGroup(page, guestId);
  await group.waitFor({ state: 'visible', timeout: 12_000 });
  await page.waitForFunction(({ id, expected }) => {
    const enable = document.querySelector(
      `[data-gamepad-focus-key="service:rare-participation:guest:${id}:enable-front"]`,
    );
    const panel = enable?.closest('.steward-list-panel');
    const rowState = panel?.querySelector('[data-rare-order-participation-state]')
      ?.getAttribute('data-rare-order-participation-state') ?? '';
    return new RegExp(expected).test(rowState);
  }, { id: guestId, expected: state instanceof RegExp ? state.source : `^${state}$` }, { timeout: 12_000 });
  return group;
}

function readQueuePosition(text) {
  const match = text.match(/队列 #(\d+)/);
  assert.ok(match, `队列卡缺少队列位置：${text}`);
  return Number(match[1]);
}

async function assertAllDisabled(locator, label) {
  const count = await locator.count();
  assert.ok(count > 0, `${label}不存在`);
  for (let index = 0; index < count; index += 1) {
    assert.equal(await locator.nth(index).isDisabled(), true, `${label}第 ${index + 1} 个按钮仍可写`);
  }
}

async function assertFocusedGamepadKey(page, focusKey, message) {
  try {
    await page.waitForFunction((expected) => (
      document.activeElement?.getAttribute('data-gamepad-focus-key') === expected
    ), focusKey, { timeout: 3_000 });
  } catch {
    const actual = await page.evaluate(() => (
      document.activeElement?.getAttribute('data-gamepad-focus-key')
        || document.activeElement?.tagName
        || 'none'
    ));
    assert.fail(`${message}：期望 ${focusKey}，实际 ${actual}`);
  }
}

async function assertNoHorizontalOverflow(page, rootSelector, label) {
  const overflow = await page.evaluate((selector) => {
    const root = document.querySelector(selector);
    if (!(root instanceof HTMLElement)) throw new Error(`Missing overflow root: ${selector}`);
    const descendants = Array.from(root.querySelectorAll('.steward-list-panel, .steward-data-row'))
      .filter((element) => element instanceof HTMLElement)
      .map((element) => element.scrollWidth - element.clientWidth);
    return {
      document: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      root: root.scrollWidth - root.clientWidth,
      descendants,
    };
  }, rootSelector);
  assert.ok(overflow.document <= 1, `${label}造成文档横向溢出 ${overflow.document}px`);
  assert.ok(overflow.root <= 1, `${label}根容器横向溢出 ${overflow.root}px`);
  assert.ok(
    overflow.descendants.every((value) => value <= 1),
    `${label}列表内容横向溢出：${overflow.descendants.join('/')}`,
  );
}

function createMutationGate() {
  let markEntered;
  let release;
  const entered = new Promise((resolve) => { markEntered = resolve; });
  const released = new Promise((resolve) => { release = resolve; });
  return {
    claimed: false,
    entered,
    released,
    markEntered,
    release,
  };
}

function createMutationFailure(error) {
  return { ...createMutationGate(), error };
}

async function withTimeout(promise, timeoutMs, message) {
  let timeoutId;
  try {
    await Promise.race([
      promise,
      new Promise((_, reject) => {
        timeoutId = setTimeout(() => reject(new Error(message)), timeoutMs);
      }),
    ]);
  } finally {
    clearTimeout(timeoutId);
  }
}

function startService(label, args, extraEnv = {}) {
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
  if (child.exitCode === null) child.kill('SIGKILL');
}

async function waitForUrl(url, child, label) {
  const deadline = Date.now() + 12_000;
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
