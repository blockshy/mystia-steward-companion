import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.FAVORITE_MANAGEMENT_AUDIT_OUTPUT_DIR || '/tmp/mystia-favorite-management-audit';
const CHROMIUM_EXECUTABLE_PATH = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH?.trim();
const STORAGE_PREFIX = 'mystia-steward-companion';
const FAVORITE_CONNECT_TIMEOUT = '连接本地 API 超时。请确认手机和电脑位于同一局域网，并检查电脑防火墙和路由器的客户端隔离设置。';
const FAVORITE_SYNC_ERROR = `收藏数据同步失败：${FAVORITE_CONNECT_TIMEOUT}`;
const FAVORITE_WRITE_RACE_ERROR = '收藏数据同步失败：模拟收藏读取失败';
const mutationRequests = [];
let activeMutations = 0;
let maxActiveMutations = 0;
let favoriteReadRequests = 0;
let compactSnapshotPolls = 0;
let fullSnapshotRequests = 0;
let failNextCompactSnapshot = false;
let holdNextCompactSnapshot = false;
let failNextFavoriteRead = false;
let holdNextMutation = false;
let mutationStartedAt = 0;
let snapshotRecoveryWatchdogTriggered = false;
let mutationWatchdogTriggered = false;
let releaseFavoriteRetry = () => {};
let markFavoriteRetryStarted = () => {};
let releaseSnapshotRecovery = () => {};
let markSnapshotRecoveryStarted = () => {};
let releaseMutation = () => {};
let markMutationStarted = () => {};
const favoriteRetryGate = new Promise((resolve) => {
  releaseFavoriteRetry = resolve;
});
const favoriteRetryStarted = new Promise((resolve) => {
  markFavoriteRetryStarted = resolve;
});
const snapshotRecoveryGate = new Promise((resolve) => {
  releaseSnapshotRecovery = resolve;
});
const snapshotRecoveryStarted = new Promise((resolve) => {
  markSnapshotRecoveryStarted = resolve;
});
const mutationGate = new Promise((resolve) => {
  releaseMutation = resolve;
});
const mutationStarted = new Promise((resolve) => {
  markMutationStarted = resolve;
});

await mkdir(OUTPUT_DIR, { recursive: true });
const browser = await chromium.launch({
  headless: true,
  ...(CHROMIUM_EXECUTABLE_PATH ? { executablePath: CHROMIUM_EXECUTABLE_PATH } : {}),
});
const page = await browser.newPage({ viewport: { width: 640, height: 760 } });

try {
  await page.route(`${API_URL}/**`, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (request.method() === 'GET' && url.pathname === '/snapshot') {
      if (url.searchParams.has('knownSignature')) {
        compactSnapshotPolls += 1;
        if (failNextCompactSnapshot) {
          failNextCompactSnapshot = false;
          holdNextCompactSnapshot = true;
          await route.fulfill({
            status: 503,
            contentType: 'application/json',
            headers: { 'access-control-allow-origin': '*' },
            body: JSON.stringify({ error: '模拟主快照连接抖动' }),
          });
          return;
        }
        if (holdNextCompactSnapshot) {
          holdNextCompactSnapshot = false;
          markSnapshotRecoveryStarted();
          const recoveryWatchdog = setTimeout(() => {
            snapshotRecoveryWatchdogTriggered = true;
            releaseSnapshotRecovery();
          }, 1400);
          await snapshotRecoveryGate.finally(() => clearTimeout(recoveryWatchdog));
          await route.continue();
          return;
        }
      } else {
        fullSnapshotRequests += 1;
      }
    }
    if (request.method() === 'GET' && url.pathname === '/favorites') {
      favoriteReadRequests += 1;
      if (favoriteReadRequests === 1) {
        await route.fulfill({
          status: 503,
          contentType: 'application/json',
          headers: { 'access-control-allow-origin': '*' },
          body: JSON.stringify({ error: FAVORITE_CONNECT_TIMEOUT }),
        });
        return;
      }
      if (favoriteReadRequests === 2) {
        markFavoriteRetryStarted();
        const retryWatchdog = setTimeout(releaseFavoriteRetry, 1500);
        await favoriteRetryGate.finally(() => clearTimeout(retryWatchdog));
        const response = await route.fetch();
        await route.fulfill({ response });
        return;
      }
      if (failNextFavoriteRead) {
        failNextFavoriteRead = false;
        await route.fulfill({
          status: 503,
          contentType: 'application/json',
          headers: { 'access-control-allow-origin': '*' },
          body: JSON.stringify({ error: '模拟收藏读取失败' }),
        });
        return;
      }
    }
    if (request.method() !== 'POST' || !url.pathname.startsWith('/favorites/')) {
      await route.continue();
      return;
    }

    mutationRequests.push({
      method: request.method(),
      path: url.pathname,
      id: url.searchParams.get('id'),
    });
    activeMutations += 1;
    maxActiveMutations = Math.max(maxActiveMutations, activeMutations);
    try {
      if (holdNextMutation) {
        holdNextMutation = false;
        mutationStartedAt = Date.now();
        markMutationStarted();
        const mutationWatchdog = setTimeout(() => {
          mutationWatchdogTriggered = true;
          releaseMutation();
        }, 2900);
        await mutationGate.finally(() => clearTimeout(mutationWatchdog));
      }
      const response = await route.fetch();
      await route.fulfill({ response });
    } finally {
      activeMutations -= 1;
    }
  });

  await page.addInitScript(({ apiUrl, apiToken, storagePrefix }) => {
    localStorage.setItem(`${storagePrefix}-mod-api-endpoint`, apiUrl);
    localStorage.setItem(`${storagePrefix}-mod-api-token`, apiToken);
  }, { apiUrl: API_URL, apiToken: API_TOKEN, storagePrefix: STORAGE_PREFIX });
  await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });

  await page.getByRole('tab', { name: '经营中', exact: true }).click();
  const rareOrders = page.locator('[data-service-order-collection="rare"]');
  await rareOrders.waitFor({ state: 'visible', timeout: 10_000 });
  await rareOrders.locator('[data-service-order-card="true"]').first().waitFor({ timeout: 10_000 });
  await page.getByText(FAVORITE_SYNC_ERROR, { exact: true }).waitFor({ timeout: 10_000 });
  await page.waitForFunction(() => (
    document.querySelector('[data-service-order-collection="rare"]')?.getAttribute('data-service-order-state') === 'ready'
  ), null, { timeout: 10_000 });
  const rareOrderCountBeforeRecovery = await rareOrders.locator('[data-service-order-card="true"]').count();
  assert(rareOrderCountBeforeRecovery > 0, '收藏读取失败时经营稀客订单没有继续显示');

  await Promise.race([
    favoriteRetryStarted,
    new Promise((_, reject) => setTimeout(() => reject(new Error('收藏读取失败后没有自动重试')), 10_000)),
  ]);
  await page.evaluate(() => new Promise((resolve) => {
    requestAnimationFrame(() => requestAnimationFrame(resolve));
  }));
  assert(
    await page.getByText(FAVORITE_SYNC_ERROR, { exact: true }).isVisible(),
    '收藏重试尚未成功时提前隐藏了真实错误',
  );
  releaseFavoriteRetry();
  await page.getByText(FAVORITE_SYNC_ERROR, { exact: true }).waitFor({ state: 'detached', timeout: 10_000 });
  await rareOrders.getByRole('button', { name: '取消收藏该料理方案', exact: true }).first().waitFor({ timeout: 10_000 });
  await page.evaluate(() => new Promise((resolve) => {
    requestAnimationFrame(() => requestAnimationFrame(resolve));
  }));
  await page.waitForFunction(() => (
    document.querySelector('[data-service-order-collection="rare"]')?.getAttribute('data-service-order-state') === 'ready'
  ), null, { timeout: 10_000 });
  assert(
    await rareOrders.locator('[data-service-order-card="true"]').count() === rareOrderCountBeforeRecovery,
    '收藏恢复不应改变经营稀客订单集合',
  );
  const compactPollsAfterFavoriteRecovery = compactSnapshotPolls;
  await waitFor(
    () => compactSnapshotPolls >= compactPollsAfterFavoriteRecovery + 2,
    5_000,
    '收藏恢复后没有观测到两轮持续快照轮询',
  );
  assert(favoriteReadRequests === 2, '快照轮询不应重复读取已恢复的收藏');

  await page.getByRole('tab', { name: '推荐料理', exact: true }).click();
  await page.getByRole('tab', { name: '收藏管理', exact: true }).click();
  await page.locator('[data-favorite-management="true"]').waitFor();

  failNextFavoriteRead = true;
  await page.getByRole('button', { name: '刷新', exact: true }).click();
  await page.getByText(FAVORITE_WRITE_RACE_ERROR, { exact: true }).waitFor({ timeout: 10_000 });
  assert(favoriteReadRequests === 3, '快照抖动前没有建立收藏读取错误');
  failNextCompactSnapshot = true;
  await page.getByRole('tab', { name: '经营中', exact: true }).click();
  await Promise.race([
    snapshotRecoveryStarted,
    new Promise((_, reject) => setTimeout(() => reject(new Error('主快照失败后没有进入受控恢复请求')), 10_000)),
  ]);
  await page.evaluate(() => new Promise((resolve) => {
    requestAnimationFrame(() => requestAnimationFrame(resolve));
  }));
  assert(
    await page.getByText(FAVORITE_WRITE_RACE_ERROR, { exact: true }).isVisible(),
    '主快照瞬时断线不应清除尚未恢复的收藏读取错误',
  );
  assert(
    await rareOrders.locator('[data-service-order-card="true"]').count() === rareOrderCountBeforeRecovery,
    '主快照瞬时断线不应移除已显示的经营稀客订单',
  );
  releaseSnapshotRecovery();
  await waitFor(() => favoriteReadRequests === 4, 5_000, '主快照恢复后没有重新读取收藏');
  await page.getByText(FAVORITE_WRITE_RACE_ERROR, { exact: true }).waitFor({ state: 'detached', timeout: 10_000 });
  assert(!snapshotRecoveryWatchdogTriggered, '主快照恢复测试超过受控 gate 时限');

  await page.getByRole('tab', { name: '推荐料理', exact: true }).click();
  await page.getByRole('tab', { name: '收藏管理', exact: true }).click();
  await page.locator('[data-favorite-management="true"]').waitFor();

  const recipeRow = page.locator('[data-favorite-entry-kind="recipe"]');
  const beverageRow = page.locator('[data-favorite-entry-kind="beverage"]');
  await recipeRow.getByText('蜂蜜蛋糕', { exact: true }).waitFor();
  assert(await beverageRow.count() === 0, '收藏管理没有默认只显示料理收藏');

  await page.getByText(/^酒水 1$/, { exact: true }).last().click();
  await beverageRow.getByText('果味米酒', { exact: true }).waitFor();
  assert(await recipeRow.count() === 0, '切换酒水分类后仍显示料理收藏');

  await page.getByText(/^全部 2$/, { exact: true }).click();
  const search = page.getByLabel('搜索收藏');
  await search.fill('蜂蜜');
  await recipeRow.getByText('蜂蜜蛋糕', { exact: true }).waitFor();
  assert(await beverageRow.count() === 0, '收藏搜索没有过滤不匹配的酒水');
  await search.fill('');
  await beverageRow.getByText('果味米酒', { exact: true }).waitFor();

  failNextFavoriteRead = true;
  await page.getByRole('button', { name: '刷新', exact: true }).click();
  await page.getByText(FAVORITE_WRITE_RACE_ERROR, { exact: true }).waitFor({ timeout: 10_000 });
  assert(favoriteReadRequests === 5, '写入竞态前的手动收藏刷新没有进入预期失败代际');

  holdNextMutation = true;
  const recipeRemove = page.getByRole('button', { name: '取消收藏料理 蜂蜜蛋糕', exact: true });
  await recipeRemove.click();
  await Promise.race([
    mutationStarted,
    new Promise((_, reject) => setTimeout(() => reject(new Error('没有观测到受控料理取消收藏请求')), 2_000)),
  ]);
  const allRemoveButtons = page.getByRole('button', { name: /^取消收藏/ });
  const removeButtonCount = await allRemoveButtons.count();
  for (let index = 0; index < removeButtonCount; index += 1) {
    assert(await allRemoveButtons.nth(index).isDisabled(), '写入期间没有锁定其他收藏操作');
  }
  const fullSnapshotsBeforeConnectionReset = fullSnapshotRequests;
  await page.getByRole('tab', { name: '概览', exact: true }).click();
  await page.locator('[data-overview-connection-endpoint="true"]').press('Enter');
  await waitFor(
    () => fullSnapshotRequests > fullSnapshotsBeforeConnectionReset,
    2_000,
    '相同地址重新连接没有创建新的连接代际',
  );
  await waitFor(() => Date.now() - mutationStartedAt >= 2100, 2_500, '没有跨过收藏读取退避触发点');
  assert(activeMutations === 1, '连接代际切换提前释放了仍在途的收藏写屏障');
  assert(favoriteReadRequests === 5, '收藏退避或新连接刷新在写请求结束前并发读取了旧集合');
  releaseMutation();
  await waitFor(() => activeMutations === 0, 2_000, '受控收藏写请求没有结束');
  await waitFor(() => favoriteReadRequests === 6, 5_000, '在途旧写结束后没有读取当前连接代际的收藏');
  assert(!mutationWatchdogTriggered, '收藏写屏障测试超过受控 gate 时限');
  await page.getByRole('tab', { name: '推荐料理', exact: true }).click();
  await page.getByRole('tab', { name: '收藏管理', exact: true }).click();
  await page.locator('[data-favorite-management="true"]').waitFor();
  await recipeRow.waitFor({ state: 'detached' });
  await page.getByText(FAVORITE_WRITE_RACE_ERROR, { exact: true }).waitFor({ state: 'detached' });
  await page.getByText(/^酒水 1$/, { exact: true }).last().click();
  await beverageRow.getByText('果味米酒', { exact: true }).waitFor();
  assert(maxActiveMutations === 1, `收藏写请求出现并发：${maxActiveMutations}`);
  assert(
    mutationRequests.some((request) => request.path === '/favorites/remove-recipe'
      && request.id === 'mock-recipe-1001-甜-202'),
    '料理取消收藏没有发送精确 ID 的规范 POST 请求',
  );

  await page.screenshot({ path: `${OUTPUT_DIR}/minimum-favorite-management.png`, fullPage: true });
  await assertNoHorizontalOverflow('640px');
  await page.setViewportSize({ width: 390, height: 760 });
  await page.screenshot({ path: `${OUTPUT_DIR}/android-favorite-management.png`, fullPage: true });
  await assertNoHorizontalOverflow('390px');
  assert(favoriteReadRequests === 6, '收藏写入完成后出现了多余读取或遗漏了当前代际重读');

  console.log('收藏管理定向巡检通过：');
  console.log('- 模拟收藏连接超时期间经营订单保留，自动重试成功后旧提示清除');
  console.log('- 快照抖动保留收藏错误，收藏写入期间退避重试不会并发读取');
  console.log('- 默认料理分类、酒水/全部切换和搜索通过');
  console.log('- 料理与酒水在同一稀客分组中展示');
  console.log('- 精确取消收藏、单写者和剩余收藏保留通过');
  console.log('- 640px 与 390px 无横向溢出');
  console.log(`- 截图：${OUTPUT_DIR}`);
} finally {
  releaseFavoriteRetry();
  releaseSnapshotRecovery();
  releaseMutation();
  await page.unrouteAll({ behavior: 'wait' }).catch(() => {});
  await browser.close();
}

async function assertNoHorizontalOverflow(label) {
  const overflow = await page.evaluate(() => (
    Math.max(0, document.documentElement.scrollWidth - document.documentElement.clientWidth)
  ));
  assert(overflow === 0, `${label} 视口出现 ${overflow}px 横向溢出`);
}

async function waitFor(predicate, timeoutMs, message) {
  const startedAt = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 40));
  }
  throw new Error(message);
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
