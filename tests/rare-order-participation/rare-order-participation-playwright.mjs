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

  for (const [index, viewport] of viewports.entries()) {
    await primary.page.setViewportSize({ width: viewport.width, height: viewport.height });
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
      `${viewport.name} 最终参与队列`,
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
    width: 640,
    height: 800,
  });
  secondaryContext = secondary.context;
  await assertExtensionRoster(secondary.page, 'secondary', true);
  const secondaryExtension = secondary.page.locator('[data-rare-guest-participation-module="true"]');
  await secondaryExtension.getByText(/稀客调度仅可在主设备修改/).first().waitFor({ timeout: 12_000 });
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
  await secondaryQueue.getByText('当前设备不是主设备，可查看队列但不能修改参与状态。', { exact: true })
    .waitFor({ timeout: 12_000 });
  await waitForManagedGroups(secondary.page);
  await assertAllDisabled(
    participationMutationButtons(secondaryQueue),
    '从设备稀客队列的全部状态操作',
  );
  await assertNoHorizontalOverflow(secondary.page, '[data-rare-order-participation-panel="true"]', '从设备稀客队列');
  await secondary.page.screenshot({
    path: path.join(outputDir, '4-secondary-read-only-queue.png'),
    fullPage: true,
  });

  console.log('PASS: rare-order participation extension module and service queue UI audit completed.');
  console.log('- 模块默认关闭，关闭态不投影队列且保持稀客原有参与流程');
  console.log('- 1280/640/390 扩展名单搜索、受控状态、移出确认与取消焦点返回通过');
  console.log('- 经营中稀客/稀客队列/普客三 Tab 与横向溢出检查通过');
  console.log('- 默认暂停、订单级优先启用、稀客级非抢占优先、单订单暂停、队尾重启与全局 mutation busy 锁通过');
  console.log('- 普通稀客页与专注模式按权威队列同步隐藏、显示和排序；失败 mutation 不改变可见集合');
  console.log('- 从设备模块开关、扩展名单与稀客队列只读检查通过');
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

  await openServiceQueue(page);
  const queue = page.locator('[data-rare-order-participation-panel="true"]');
  await queue.getByText(/稀客调度模块已停用/).waitFor({ timeout: 12_000 });
  assert.equal(await queue.getAttribute('data-module-enabled'), 'false');
  assert.equal(
    await queue.locator('[data-gamepad-focus-key^="service:rare-participation:guest:"]').count(),
    0,
    '模块关闭时不应展示参与状态操作',
  );
  await assertRareRecommendationViews(page, defaultVisibleRareOrders, '模块关闭旁路');
}

async function enableParticipationModule(page) {
  await openExtensionSection(page, '稀客调度');
  const module = page.locator('[data-rare-guest-participation-module="true"]');
  const toggle = module.locator('[data-gamepad-focus-key="extensions:rare-participation:module-toggle"]');
  await toggle.click();
  await page.waitForFunction(() => {
    const element = document.querySelector(
      '[data-gamepad-focus-key="extensions:rare-participation:module-toggle"]',
    );
    return element instanceof HTMLInputElement && element.checked;
  }, null, { timeout: 5_000 });
  await module.locator('[data-rare-guest-participation-roster="true"]').waitFor({ timeout: 12_000 });
}

async function assertExtensionRoster(page, profileName, readOnly) {
  await openExtensionSection(page, '稀客调度');
  const root = page.locator('[data-rare-guest-participation-roster="true"]');
  await root.waitFor({ state: 'visible', timeout: 10_000 });

  const managed1001 = managedRow(root, 1001, true);
  const managed1002 = managedRow(root, 1002, true);
  await managed1001.waitFor({ state: 'visible', timeout: 12_000 });
  await managed1002.waitFor({ state: 'visible', timeout: 12_000 });
  await root.getByRole('heading', { name: '已受控 (2)', exact: true }).waitFor({ timeout: 12_000 });
  await managed1001.getByText('当前 1 笔', { exact: true }).waitFor({ timeout: 12_000 });

  const search = root.getByPlaceholder('输入姓名、ID、地区或 DLC', { exact: true });
  assert.equal(await search.isDisabled(), false, `${profileName}: 稀客搜索不应禁用`);
  await search.fill('米斯蒂娅');
  await managed1001.waitFor({ state: 'visible' });
  await managed1002.waitFor({ state: 'detached' });
  await root.getByRole('heading', { name: '已受控 (1)', exact: true }).waitFor();
  await search.fill('');
  await managed1002.waitFor({ state: 'visible' });
  await root.getByRole('heading', { name: '已受控 (2)', exact: true }).waitFor();

  if (!readOnly) await assertRemovalCancelReturnsFocus(page, root);
  await assertNoHorizontalOverflow(page, '[data-rare-guest-participation-module="true"]', `${profileName} 稀客调度`);
}

async function assertRemovalCancelReturnsFocus(page, root) {
  const returnKey = 'extensions:rare-participation:guest:1001:remove';
  const removeButton = root.locator(`[data-gamepad-focus-key="${returnKey}"]`);
  await removeButton.click();
  const dialog = page.getByRole('dialog').filter({ hasText: '移出受控稀客' });
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
    assert.equal((await trigger.innerText()).trim(), label, `${profileName}: ${kind} Tab 文案不正确`);
    await trigger.click();
    await page.locator(`[data-service-order-tab="${kind}"]`).waitFor({ state: 'visible', timeout: 10_000 });
  }
  await page.locator('[data-service-order-tab-trigger="rare-queue"]').click();
  const panel = page.locator('[data-rare-order-participation-panel="true"]');
  await panel.waitFor({ state: 'visible' });
  await panel.getByRole('heading', { name: '稀客参与队列', exact: true }).waitFor();
}

async function assertPrimaryParticipationLifecycle(page) {
  await waitForManagedGroups(page);
  assert.equal(
    await page.locator('[data-rare-order-participation-state="paused"]').count(),
    2,
    '受控稀客的两笔当前订单都应默认暂停',
  );
  assert.equal(
    await page.locator('[data-rare-order-participation-state="queued"]').count(),
    0,
    '受控稀客订单不应在手动启用前进入参与队列',
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
  assert.equal(await allMutationActions.count(), 8, '两组及两笔订单应存在八个参与状态按钮');
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
  const topTab = page.locator('[data-gamepad-tab-value="service"]').first();
  await topTab.scrollIntoViewIfNeeded();
  await topTab.click();
  const serviceViewControl = page.locator('[data-slot="segmented-control"]').filter({ hasText: '推荐' }).first();
  await serviceViewControl.locator('label').filter({ hasText: /^推荐$/ }).click();
  await page.locator('[data-service-order-tab-trigger="rare-queue"]').click();
  await page.locator('[data-rare-order-participation-panel="true"]').waitFor({ state: 'visible', timeout: 10_000 });
}

async function openRareRecommendations(page) {
  const topTab = page.locator('[data-gamepad-tab-value="service"]').first();
  await topTab.scrollIntoViewIfNeeded();
  await topTab.click();
  const serviceViewControl = page.locator('[data-slot="segmented-control"]').filter({ hasText: '推荐' }).first();
  await serviceViewControl.locator('label').filter({ hasText: /^推荐$/ }).click();
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
  await regularTab.getByRole('button', { name: '稀客订单专注模式', exact: true }).click();
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

async function openClient(currentBrowser, { deviceId, width, height }) {
  const context = await currentBrowser.newContext({ viewport: { width, height } });
  await context.addInitScript(seedClientStorage, {
    endpoint: apiUrl,
    token: apiToken,
    prefix: storagePrefix,
    deviceId,
  });
  const page = await context.newPage();
  await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
  await page.locator('[data-gamepad-tab-value="service"]').first().waitFor({ timeout: 12_000 });
  return { context, page };
}

function seedClientStorage({ endpoint, token, prefix, deviceId }) {
  localStorage.setItem(`${prefix}-mod-api-endpoint`, endpoint);
  localStorage.setItem(`${prefix}-mod-api-token`, token);
  localStorage.setItem(`${prefix}-client-id`, deviceId);
  localStorage.setItem(`${prefix}-managed-rare-guest-ids`, JSON.stringify([1001, 1002]));
  localStorage.setItem(`${prefix}-rare-order-highlight`, '1');
  localStorage.setItem(`${prefix}-show-debug-details`, '1');
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
