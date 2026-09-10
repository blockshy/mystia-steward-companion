import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const appUrl = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173';
const apiUrl = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const apiToken = process.env.MYSTIA_API_TOKEN || 'mock-token';
const executablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH?.trim();
const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}) });
const page = await browser.newPage({ viewport: { width: 640, height: 900 } });
page.setDefaultTimeout(10000);
const pageErrors = [];
page.on('pageerror', (error) => pageErrors.push(error.message));

try {
  await page.addInitScript(({ endpoint, token }) => {
    localStorage.setItem('mystia-steward-companion-mod-api-endpoint', endpoint);
    localStorage.setItem('mystia-steward-companion-mod-api-token', token);
    localStorage.setItem('mystia-steward-companion-client-id', 'inventory-operations-audit');
  }, { endpoint: apiUrl, token: apiToken });
  await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
  const top = (name) => page.locator('.steward-primary-tabs-list').getByRole('tab', { name, exact: true }).click();
  const openInventory = async () => {
    await top('扩展功能');
    await page.getByRole('tab', { name: '修改', exact: true }).click();
  };
  await top('概览');
  await page.waitForFunction(() => document.body.textContent.includes('1.0.5'));
  await openInventory();
  const addButtons = page.locator('[data-gamepad-focus-key^="inventory:ingredient:"][data-gamepad-focus-key$=":add10"]');
  await waitEnabled(addButtons.first());
  assert.ok(await addButtons.count() > 1);

  const writeGate = deferred();
  const writeStarted = deferred();
  const confirmGate = deferred();
  const confirmStarted = deferred();
  let holdConfirmation = false;
  let nextConfirmationUnavailable = false;
  let writes = 0;
  let holdForPause = false;
  const pauseWriteGate = deferred();
  const pauseWriteStarted = deferred();
  let snapshotReads = 0;
  await page.route('**/inventory/set?*', async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    writes += 1;
    const response = await route.fetch();
    if (holdForPause) {
      holdForPause = false;
      pauseWriteStarted.resolve();
      await pauseWriteGate.promise;
    }
    if (writes === 1) {
      writeStarted.resolve();
      await writeGate.promise;
      holdConfirmation = true;
    }
    if (nextConfirmationUnavailable) holdConfirmation = true;
    await route.fulfill({ response });
  });
  await page.route('**/snapshot*', async (route) => {
    snapshotReads += 1;
    if (!holdConfirmation) return route.continue();
    holdConfirmation = false;
    const response = await route.fetch({ url: `${apiUrl}/snapshot` });
    if (nextConfirmationUnavailable) {
      nextConfirmationUnavailable = false;
      const json = await response.json();
      return route.fulfill({ json: { ...json, recommendationState: null, snapshotSignature: 'a'.repeat(64) } });
    }
    confirmStarted.resolve();
    await confirmGate.promise;
    await route.fulfill({ response });
  });

  await addButtons.first().click();
  await writeStarted.promise;
  assert.equal(await addButtons.nth(1).isDisabled(), true, 'A 请求期间所有行共同禁用');
  // Additional activations cannot bypass the busy state while a write is owned.
  await addButtons.nth(1).evaluate((element) => element.click());
  assert.equal(writes, 1);
  await top('概览');
  await openInventory();
  assert.equal(await addButtons.nth(1).isDisabled(), true, '切页重挂载不解除写占用');
  writeGate.resolve();
  await confirmStarted.promise;
  await page.locator('[data-inventory-operation-state="confirming"]').waitFor();
  assert.equal(await addButtons.nth(1).isDisabled(), true, 'POST 返回后必须等刷新确认才释放');
  confirmGate.resolve();
  await page.locator('[data-inventory-operation-state="succeeded"]').waitFor();
  await waitEnabled(addButtons.nth(1));

  nextConfirmationUnavailable = true;
  await addButtons.nth(1).click();
  await page.locator('[data-inventory-operation-state="unconfirmed"]').waitFor();
  assert.equal(writes, 2, '确认失败不能自动重发 POST');
  assert.equal(await page.locator('[data-gamepad-focus-key="inventory:bulk:ingredient"]').isDisabled(), true);
  const refreshButton = page.locator('[data-gamepad-focus-key="inventory:refresh"]');
  await waitEnabled(refreshButton);
  await refreshButton.click();
  await page.locator('[data-inventory-operation-state="succeeded"]').waitFor();
  await waitEnabled(addButtons.first());

  const snapshot = await (await page.request.get(`${apiUrl}/snapshot`, {
    headers: { 'X-Mystia-Steward-Companion-Token': apiToken },
  })).json();
  const expectedIds = snapshot.recommendationState.availableIngredientIds;
  const bulkRequests = [];
  await page.route('**/inventory/bulk-set?*', async (route) => {
    const url = new URL(route.request().url());
    const ids = url.searchParams.get('ids').split(',').map(Number);
    bulkRequests.push(ids);
    await route.fulfill({ json: {
      ok: false,
      type: 'ingredient',
      requestedQuantity: 99,
      total: ids.length,
      changed: 0,
      unchanged: ids.length - 1,
      failed: 1,
      errors: ['单项拒绝探针'],
      error: null,
    } });
  });
  await page.getByRole('textbox', { name: '搜索库存', exact: true }).fill('没有匹配的库存项目');
  const bulk = page.locator('[data-gamepad-focus-key="inventory:bulk:ingredient"]');
  assert.match(await bulk.innerText(), /全部已解锁材料/);
  await bulk.click();
  await page.locator('[data-inventory-operation-state="partial"]').waitFor();
  assert.deepEqual([...bulkRequests[0]].sort((a, b) => a - b), [...expectedIds].sort((a, b) => a - b));
  assert.match(await page.locator('[data-inventory-operation-state="partial"]').innerText(), /失败 1 项/);
  await page.getByRole('textbox', { name: '搜索库存', exact: true }).fill('');
  holdForPause = true;
  await addButtons.first().click();
  await pauseWriteStarted.promise;
  await top('概览');
  await page.locator('[data-gamepad-focus-key="overview:connection:toggle"]').click();
  const readsAtPause = snapshotReads;
  pauseWriteGate.resolve();
  await openInventory();
  await page.locator('[data-inventory-operation-state="unconfirmed"]').waitFor();
  assert.equal(snapshotReads, readsAtPause, '后台库存确认不得撤销用户暂停或继续发起读取');
  assert.equal(await addButtons.first().isDisabled(), true);
  await top('概览');
  await page.locator('[data-overview-connection-status-metric="connection"]').getByText('已停止', { exact: true }).waitFor();
  await page.locator('[data-gamepad-focus-key="overview:connection:toggle"]').click();
  await page.locator('[data-overview-connection-status-metric="connection"]').getByText('已连接', { exact: true }).waitFor();
  await openInventory();
  await refreshButton.click();
  await page.locator('[data-inventory-operation-state="succeeded"]').waitFor();
  await waitEnabled(addButtons.first());
  assert.deepEqual(pageErrors, []);
  console.log('inventory operation UI passed: one owner through confirmation/remount, missing snapshot, explicit recovery, full bulk scope, partial result');
} finally {
  await browser.close();
}

function deferred() {
  let resolve;
  const promise = new Promise((done) => { resolve = done; });
  return { promise, resolve };
}

async function waitEnabled(locator) {
  await locator.waitFor({ state: 'attached' });
  await page.waitForFunction((element) => !element.disabled, await locator.elementHandle());
}
