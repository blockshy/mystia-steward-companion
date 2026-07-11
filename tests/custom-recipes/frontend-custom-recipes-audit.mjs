import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.CUSTOM_RECIPE_AUDIT_OUTPUT_DIR || '/tmp/mystia-custom-recipes-audit';
const STORAGE_PREFIX = 'mystia-steward-companion';
const mutationRequests = [];
let activeMutations = 0;
let maxActiveMutations = 0;
let delayNextFlagsMs = 0;

await mkdir(OUTPUT_DIR, { recursive: true });
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 720, height: 760 } });

try {
  await page.route(`${API_URL}/**`, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (!request.method().startsWith('POST') || !url.pathname.startsWith('/custom-recipes/')) {
      await route.continue();
      return;
    }

    mutationRequests.push({ method: request.method(), path: url.pathname, params: Object.fromEntries(url.searchParams) });
    activeMutations += 1;
    maxActiveMutations = Math.max(maxActiveMutations, activeMutations);
    try {
      if (url.pathname === '/custom-recipes/update-flags' && delayNextFlagsMs > 0) {
        const delayMs = delayNextFlagsMs;
        delayNextFlagsMs = 0;
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
    localStorage.setItem(`${storagePrefix}-custom-recipe-group-mode`, 'customer');
  }, { apiUrl: API_URL, apiToken: API_TOKEN, storagePrefix: STORAGE_PREFIX });
  await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
  await activateTab('自定义推荐料理');
  await page.getByText('启用自定义推荐料理', { exact: true }).waitFor();

  const draftEnabled = page.getByLabel('保存后启用');
  await page.getByText('保存后启用', { exact: true }).click();
  assert(!await draftEnabled.isChecked(), '新增草稿启用状态没有关闭');
  await activateTab('概览');
  await activateTab('自定义推荐料理');
  assert(!await page.getByLabel('保存后启用').isChecked(), '新增草稿状态在页签切换后丢失');

  await page.getByText('启用自定义推荐料理', { exact: true }).click();
  await waitForRecipes((data) => data.enabled === false, '功能总开关没有持久化关闭状态');
  await page.getByText('功能已停用', { exact: true }).waitFor();
  await activateTab('稀客');
  await page.getByRole('button', { name: '查看生效的自定义配方 (0)', exact: true }).waitFor();
  await activateTab('自定义推荐料理');
  await page.getByText('启用自定义推荐料理', { exact: true }).click();
  await waitForRecipes((data) => data.enabled === true, '功能总开关没有恢复开启状态');

  await page.getByText('按基础料理', { exact: true }).click();
  await page.getByText('蜂蜜蛋糕', { exact: true }).first().waitFor();
  assert(await page.getByRole('button', { name: '上移', exact: true }).count() === 0, '基础料理分组仍显示稀客内排序按钮');
  assert(await page.evaluate((key) => localStorage.getItem(key), `${STORAGE_PREFIX}-custom-recipe-group-mode`) === 'recipe', '基础料理分组模式没有持久化');
  await activateTab('概览');
  await activateTab('自定义推荐料理');
  assert(await page.getByRole('button', { name: '上移', exact: true }).count() === 0, '页签切换后没有恢复基础料理分组');

  await page.getByText('按稀客', { exact: true }).click();
  const rumiaGroup = groupSection('露米娅');
  await rumiaGroup.getByRole('button', { name: '本组停用', exact: true }).click();
  await waitForRecipes(
    (data) => data.recipes.filter((entry) => entry.customerId === 1002).every((entry) => !entry.enabled),
    '稀客分组停用没有原子更新全部组员',
  );
  await page.getByRole('button', { name: '全部启用', exact: true }).click();
  await waitForRecipes((data) => data.recipes.every((entry) => entry.enabled), '页面级全部启用没有更新全部配方');

  const mystiaGroup = groupSection('米斯蒂娅');
  await mystiaGroup.getByRole('button', { name: '取消本组置顶', exact: true }).click();
  await waitForRecipes(
    (data) => data.recipes.filter((entry) => entry.customerId === 1001).every((entry) => !entry.pinToTop),
    '稀客分组取消置顶没有更新全部组员',
  );
  const moonRow = page.locator('.steward-data-row').filter({ hasText: '月光团子' });
  await moonRow.getByRole('button', { name: '置顶', exact: true }).click();
  await waitForRecipes(
    (data) => data.recipes.find((entry) => entry.id === 'mock-custom-1001-all-1206')?.pinToTop === true,
    '单条推荐置顶没有持久化',
  );

  const cakeRow = page.locator('.steward-data-row').filter({ hasText: '蜂蜜蛋糕' }).filter({ hasText: '优先级 1' }).last();
  await cakeRow.getByRole('button', { name: '下移', exact: true }).click();
  await waitForRecipes((data) => {
    const cake = data.recipes.find((entry) => entry.id === 'mock-custom-1001-all-1202');
    const moon = data.recipes.find((entry) => entry.id === 'mock-custom-1001-all-1206');
    return cake?.sortOrder === 200 && moon?.sortOrder === 100;
  }, '同稀客内排序没有交换目标配方');

  delayNextFlagsMs = 500;
  await page.getByRole('button', { name: '全部停用', exact: true }).click();
  await waitFor(() => activeMutations === 1, 2_000, '未观测到延迟的批量状态请求');
  assert(await page.getByRole('button', { name: '全部置顶', exact: true }).isDisabled(), '批量写入期间没有锁定其他写命令');
  await waitForRecipes((data) => data.recipes.every((entry) => !entry.enabled), '延迟的全部停用没有完成');
  assert(maxActiveMutations === 1, `自定义料理写请求出现并发：${maxActiveMutations}`);

  await page.screenshot({ path: `${OUTPUT_DIR}/minimum-custom-recipes.png`, fullPage: true });
  const overflow = await page.evaluate(() => Math.max(0, document.documentElement.scrollWidth - document.documentElement.clientWidth));
  assert(overflow === 0, `720px 视口出现 ${overflow}px 横向溢出`);
  assert(mutationRequests.every((request) => request.method === 'POST'), '自定义料理 mutation 没有全部使用 POST');
  assert(!mutationRequests.some((request) => request.path === '/custom-recipes/toggle'), '前端仍在调用已删除的 toggle 路径');

  console.log('自定义推荐料理定向巡检通过：');
  console.log('- 功能总开关：关闭/开启均持久化');
  console.log('- 草稿生命周期：跨页签保留');
  console.log('- 分组：按稀客/基础料理切换并记忆');
  console.log('- 批量操作：页面级、分组级与单条状态更新通过');
  console.log('- 排序：仅在同一稀客内移动');
  console.log(`- 单写者：最大并发 ${maxActiveMutations}`);
  console.log(`- 截图：${OUTPUT_DIR}/minimum-custom-recipes.png`);
} finally {
  await browser.close();
}

function groupSection(label) {
  return page.locator('section').filter({ has: page.getByText(label, { exact: true }) }).first();
}

async function activateTab(label) {
  await page.getByRole('tab', { name: label, exact: true }).click();
  await page.waitForTimeout(150);
}

async function readRecipes() {
  const response = await fetch(`${API_URL}/custom-recipes`, {
    headers: { 'X-Mystia-Steward-Companion-Token': API_TOKEN },
  });
  assert(response.ok, `读取自定义料理失败：HTTP ${response.status}`);
  return response.json();
}

async function waitForRecipes(predicate, message) {
  await waitFor(async () => predicate(await readRecipes()), 4_000, message);
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
