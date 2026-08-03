import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';

import { chromium } from 'playwright';

const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.SERVICE_ORDER_AUDIT_OUTPUT_DIR || '/tmp/mystia-companion-service-order-audit';
const STORAGE_PREFIX = 'mystia-steward-companion';
const viewports = [
  { name: 'desktop', width: 1280, height: 900 },
  { name: 'minimum', width: 640, height: 760 },
  { name: 'android', width: 390, height: 760 },
];

const snapshotResponse = await fetch(`${API_URL}/snapshot`, {
  headers: { 'X-Mystia-Steward-Companion-Token': API_TOKEN },
});
assert.equal(snapshotResponse.ok, true, `无法读取 mock snapshot：HTTP ${snapshotResponse.status}`);
const populatedSnapshot = await snapshotResponse.json();
const emptySnapshot = structuredClone(populatedSnapshot);
emptySnapshot.nightBusiness = {
  ...emptySnapshot.nightBusiness,
  activeRareGuests: [],
  orders: [],
  source: '',
  error: null,
};
emptySnapshot.normalBusiness = {
  ...emptySnapshot.normalBusiness,
  orders: [],
  source: '',
  error: null,
};
emptySnapshot.snapshotSignature = 'service-order-presentation-empty';

await mkdir(OUTPUT_DIR, { recursive: true });
const browser = await chromium.launch({ headless: true });

try {
  for (const viewport of viewports) {
    await auditPopulatedOrders(browser, viewport);
    await auditEmptyOrders(browser, viewport);
  }
} finally {
  await browser.close();
}

console.log('PASS: rare and normal service orders share one stable presentation across populated and empty states.');
console.log(`Screenshots written to ${OUTPUT_DIR}`);

async function auditPopulatedOrders(browserInstance, viewport) {
  const page = await openWorkbench(browserInstance, viewport);
  try {
    await activateServiceRecommendations(page);

    const rare = await inspectOrderCollection(page, 'rare', { waitForCards: true });
    assert.equal(rare.heading, '当前订单方案');
    assert.ok(rare.cardCount > 0, `${viewport.name} 稀客页应显示订单卡片。`);
    assert.equal(rare.state, 'ready', `${viewport.name} 稀客有数据状态不正确。`);
    assert.equal(rare.retainingRows, 'true', `${viewport.name} 稀客有数据时应显示订单卡片。`);
    assert.equal(rare.hasScrollFade, false, `${viewport.name} 稀客订单区域不应显示底部渐隐。`);
    assert.equal(rare.clippedBadgeCount, 0, `${viewport.name} 稀客卡片存在被裁切的徽标。`);
    assert.equal(
      await page.getByRole('button', { name: '稀客订单专注模式', exact: true }).count(),
      1,
      `${viewport.name} 稀客页必须保留专注模式控制。`,
    );
    assert.equal(rare.scrollKey, 'service:recommendations', `${viewport.name} 稀客页滚动标识发生变化。`);
    await assertNoHorizontalOverflow(page, viewport.name, 'populated rare');
    await page.screenshot({
      path: path.join(OUTPUT_DIR, `${viewport.name}-populated-rare.png`),
      fullPage: true,
    });

    const normal = await inspectOrderCollection(page, 'normal', { waitForCards: true });
    assert.equal(normal.heading, '当前订单方案');
    assert.ok(normal.cardCount > 0, `${viewport.name} 普客页应显示订单卡片。`);
    assert.equal(normal.state, 'ready', `${viewport.name} 普客有数据状态不正确。`);
    assert.equal(normal.retainingRows, 'true', `${viewport.name} 普客有数据时应显示订单卡片。`);
    assert.equal(normal.hasScrollFade, false, `${viewport.name} 普客订单区域不应显示底部渐隐。`);
    assert.equal(normal.clippedBadgeCount, 0, `${viewport.name} 普客卡片存在被裁切的徽标。`);
    assert.equal(normal.panelClass, rare.panelClass, `${viewport.name} 两类订单面板外框不一致。`);
    assert.equal(normal.contentClass, rare.contentClass, `${viewport.name} 两类订单内容区布局不一致。`);
    assert.equal(normal.minHeight, rare.minHeight, `${viewport.name} 两类订单内容区最小高度不一致。`);
    assert.equal(normal.overflowY, rare.overflowY, `${viewport.name} 两类订单滚动策略不一致。`);
    assert.equal(normal.scrollKey, 'service:recommendations:normal', `${viewport.name} 普客页滚动标识不独立。`);
    await assertNoHorizontalOverflow(page, viewport.name, 'populated normal');
    await page.screenshot({
      path: path.join(OUTPUT_DIR, `${viewport.name}-populated-normal.png`),
      fullPage: true,
    });
  } finally {
    await page.close();
  }
}

async function auditEmptyOrders(browserInstance, viewport) {
  const page = await openWorkbench(browserInstance, viewport, emptySnapshot);
  try {
    await activateServiceRecommendations(page);

    const rare = await inspectOrderCollection(page, 'rare', {
      emptyText: '暂无当前稀客点单推荐',
    });
    assert.equal(rare.state, 'empty', `${viewport.name} 稀客空数据状态不正确。`);
    assert.equal(rare.retainingRows, 'false', `${viewport.name} 稀客空状态不应保留旧卡片。`);
    assert.equal(rare.hasScrollFade, false, `${viewport.name} 稀客空状态不应显示底部渐隐。`);
    await assertNoHorizontalOverflow(page, viewport.name, 'empty rare');
    await page.screenshot({
      path: path.join(OUTPUT_DIR, `${viewport.name}-empty-rare.png`),
      fullPage: true,
    });
    const normal = await inspectOrderCollection(page, 'normal', {
      emptyText: '暂无普客订单',
    });
    assert.equal(normal.state, 'empty', `${viewport.name} 普客空数据状态不正确。`);
    assert.equal(normal.retainingRows, 'false', `${viewport.name} 普客空状态不应保留旧卡片。`);
    assert.equal(normal.hasScrollFade, false, `${viewport.name} 普客空状态不应显示底部渐隐。`);
    assert.equal(rare.cardCount, 0, `${viewport.name} 空稀客页不应保留订单卡片。`);
    assert.equal(normal.cardCount, 0, `${viewport.name} 空普客页不应保留订单卡片。`);
    assert.equal(normal.panelClass, rare.panelClass, `${viewport.name} 空状态面板外框不一致。`);
    assert.equal(normal.contentClass, rare.contentClass, `${viewport.name} 空状态内容区布局不一致。`);
    assert.equal(normal.minHeight, rare.minHeight, `${viewport.name} 空状态内容区最小高度不一致。`);
    assert.ok(
      Math.abs(normal.emptyHeight - rare.emptyHeight) <= 1,
      `${viewport.name} 两类空状态高度不一致：${rare.emptyHeight}/${normal.emptyHeight}。`,
    );
    assert.ok(
      Math.abs(normal.emptyWidth - rare.emptyWidth) <= 1,
      `${viewport.name} 两类空状态宽度不一致：${rare.emptyWidth}/${normal.emptyWidth}。`,
    );
    await assertNoHorizontalOverflow(page, viewport.name, 'empty normal');
    await page.screenshot({
      path: path.join(OUTPUT_DIR, `${viewport.name}-empty-normal.png`),
      fullPage: true,
    });
  } finally {
    await page.close();
  }
}

async function openWorkbench(browserInstance, viewport, interceptedSnapshot = null) {
  const page = await browserInstance.newPage({ viewport: { width: viewport.width, height: viewport.height } });
  await page.addInitScript(seedLocalStorage, {
    apiUrl: API_URL,
    apiToken: API_TOKEN,
    storagePrefix: STORAGE_PREFIX,
  });
  if (interceptedSnapshot) {
    await page.route(`${API_URL}/snapshot**`, async (route) => {
      if (route.request().method() !== 'GET') {
        await route.continue();
        return;
      }
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        headers: { 'access-control-allow-origin': '*' },
        body: JSON.stringify(interceptedSnapshot),
      });
    });
  }
  await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.body.innerText.includes('1.0.5'), null, { timeout: 10000 });
  return page;
}

function seedLocalStorage({ apiUrl, apiToken, storagePrefix }) {
  localStorage.setItem(`${storagePrefix}-mod-api-endpoint`, apiUrl);
  localStorage.setItem(`${storagePrefix}-mod-api-token`, apiToken);
  localStorage.setItem(`${storagePrefix}-show-debug-details`, '1');
}

async function activateServiceRecommendations(page) {
  await page.locator('[data-gamepad-tab-value="service"]').first().click();
  const serviceViewControl = page.locator('[data-slot="segmented-control"]').filter({ hasText: '推荐' }).first();
  await serviceViewControl.locator('label').filter({ hasText: /^推荐$/ }).click();
}

async function inspectOrderCollection(page, kind, options = {}) {
  await page.locator(`[data-service-order-tab-trigger="${kind}"]`).click();
  const collection = page.locator(`[data-service-order-collection="${kind}"]`);
  await collection.waitFor({ state: 'visible', timeout: 10000 });
  if (options.waitForCards) {
    await collection.locator('[data-service-order-card="true"]').first().waitFor({ state: 'visible', timeout: 10000 });
  }
  if (options.emptyText) {
    await collection.getByText(options.emptyText, { exact: true }).waitFor({ state: 'visible', timeout: 10000 });
  }

  return collection.evaluate((element) => {
    const panel = element.closest('.steward-list-panel');
    const content = element.parentElement;
    const heading = panel?.querySelector('h2');
    const empty = element.querySelector('.steward-empty-state');
    if (!(panel instanceof HTMLElement) || !(content instanceof HTMLElement)) {
      throw new Error('订单展示没有位于统一 ListPanel 内容区中。');
    }
    const contentStyle = getComputedStyle(content);
    const emptyRect = empty?.getBoundingClientRect();
    return {
      heading: heading?.textContent?.trim() ?? '',
      panelClass: panel.className,
      contentClass: content.className,
      minHeight: contentStyle.minHeight,
      overflowY: contentStyle.overflowY,
      hasScrollFade: content.classList.contains('steward-scroll-fade'),
      scrollKey: content.dataset.gamepadScrollKey ?? '',
      state: element.dataset.serviceOrderState ?? '',
      retainingRows: element.dataset.serviceOrderRetainingRows ?? '',
      cardCount: element.querySelectorAll('[data-service-order-card="true"]').length,
      emptyWidth: Math.round(emptyRect?.width ?? 0),
      emptyHeight: Math.round(emptyRect?.height ?? 0),
      clippedBadgeCount: Array.from(element.querySelectorAll('[data-slot="badge"]'))
        .filter((badge) => badge instanceof HTMLElement)
        .filter((badge) => {
          const badgeRect = badge.getBoundingClientRect();
          const card = badge.closest('[data-service-order-card="true"]');
          if (!(card instanceof HTMLElement)) return false;
          const cardRect = card.getBoundingClientRect();
          return badgeRect.left < cardRect.left - 1
            || badgeRect.right > cardRect.right + 1
            || badgeRect.top < cardRect.top - 1
            || badgeRect.bottom > cardRect.bottom + 1;
        }).length,
    };
  });
}

async function assertNoHorizontalOverflow(page, viewportName, state) {
  const overflow = await page.evaluate(() => ({
    document: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    collections: Array.from(document.querySelectorAll('[data-service-order-collection]'))
      .filter((element) => element instanceof HTMLElement)
      .map((element) => element.scrollWidth - element.clientWidth),
  }));
  assert.ok(overflow.document <= 1, `${viewportName} ${state} 页面横向溢出 ${overflow.document}px。`);
  assert.ok(
    overflow.collections.every((value) => value <= 1),
    `${viewportName} ${state} 订单区域横向溢出：${overflow.collections.join('/')}。`,
  );
}
