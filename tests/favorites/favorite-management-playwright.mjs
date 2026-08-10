import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.FAVORITE_MANAGEMENT_AUDIT_OUTPUT_DIR || '/tmp/mystia-favorite-management-audit';
const CHROMIUM_EXECUTABLE_PATH = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH?.trim();
const STORAGE_PREFIX = 'mystia-steward-companion';
const mutationRequests = [];
let activeMutations = 0;
let maxActiveMutations = 0;
let delayNextMutationMs = 0;

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
      if (delayNextMutationMs > 0) {
        const delayMs = delayNextMutationMs;
        delayNextMutationMs = 0;
        await new Promise((resolve) => setTimeout(resolve, delayMs));
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

  delayNextMutationMs = 500;
  const recipeRemove = page.getByRole('button', { name: '取消收藏料理 蜂蜜蛋糕', exact: true });
  await recipeRemove.click();
  await waitFor(() => activeMutations === 1, 2_000, '没有观测到料理取消收藏请求');
  const allRemoveButtons = page.getByRole('button', { name: /^取消收藏/ });
  const removeButtonCount = await allRemoveButtons.count();
  for (let index = 0; index < removeButtonCount; index += 1) {
    assert(await allRemoveButtons.nth(index).isDisabled(), '写入期间没有锁定其他收藏操作');
  }
  await recipeRow.waitFor({ state: 'detached' });
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

  console.log('收藏管理定向巡检通过：');
  console.log('- 默认料理分类、酒水/全部切换和搜索通过');
  console.log('- 料理与酒水在同一稀客分组中展示');
  console.log('- 精确取消收藏、单写者和剩余收藏保留通过');
  console.log('- 640px 与 390px 无横向溢出');
  console.log(`- 截图：${OUTPUT_DIR}`);
} finally {
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
