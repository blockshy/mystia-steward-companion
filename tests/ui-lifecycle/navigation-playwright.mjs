import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const prefix = 'mystia-steward-companion';
const apiUrl = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const appUrl = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173';
const executablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH?.trim();
const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}) });
const page = await browser.newPage({ viewport: { width: 640, height: 760 } });
const errors = [];
page.on('pageerror', (error) => errors.push(error.message));
page.setDefaultTimeout(10000);
try {
  await page.addInitScript(({ prefix, apiUrl }) => {
    if (!sessionStorage.getItem('navigation-seeded')) {
      localStorage.setItem(`${prefix}-mod-api-endpoint`, apiUrl);
      localStorage.setItem(`${prefix}-mod-api-token`, 'mock-token');
      localStorage.setItem(`${prefix}-client-id`, 'navigation-audit');
      localStorage.setItem(`${prefix}-show-debug-details`, '1');
      localStorage.setItem(`${prefix}-mod-tab`, 'logs');
      sessionStorage.setItem('navigation-seeded', '1');
    }
    const NativeWorker = window.Worker;
    let failedOnce = false;
    window.__normalDetailPosts = 0;
    window.Worker = class extends NativeWorker {
      constructor(url, options) {
        if (!failedOnce && String(url).includes('order-recommendations.worker')) {
          failedOnce = true;
          throw new Error('导航回归：首次推荐计算不可用');
        }
        super(url, options);
      }
      postMessage(message, transfer = []) {
        if (message.payload?.includeNormalOrderDetails) window.__normalDetailPosts += 1;
        super.postMessage(message, transfer);
      }
    };
  }, { prefix, apiUrl });
  await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
  const primary = page.locator('.steward-primary-tabs-list');
  const top = (name) => primary.getByRole('tab', { name, exact: true }).click();
  await page.locator('[data-settings-tabs]').getByRole('tab', { name: '日志', exact: true }).and(page.locator('[aria-selected="true"]')).waitFor();
  assert.equal(await page.evaluate((key) => localStorage.getItem(key), `${prefix}-mod-tab`), 'settings:logs');
  assert.deepEqual(await primary.getByRole('tab').allTextContents(), ['概览', '推荐', '经营', '自动化', '工具', '设置']);
  await page.reload();
  await page.locator('[data-settings-tabs]').getByRole('tab', { name: '日志', exact: true }).and(page.locator('[aria-selected="true"]')).waitFor();

  await page.locator('[data-settings-tabs]').getByRole('tab', { name: '外观窗口', exact: true }).click();
  await page.locator('label.steward-switch-field').filter({ hasText: '显示调试信息' }).click();
  assert.equal(await page.locator('[data-settings-tabs]').getByRole('tab', { name: '日志', exact: true }).count(), 0);
  assert.equal(await primary.getByRole('tab').count(), 6, '关闭调试不能改变一级导航与手柄顺序');
  await page.reload();
  await page.locator('[data-settings-tabs]').getByRole('tab', { name: '外观窗口', exact: true }).and(page.locator('[aria-selected="true"]')).waitFor();

  await top('概览');
  await page.locator('[data-overview-connection-status-metric="connection"]').getByText('已连接', { exact: true }).waitFor();
  await top('经营');
  const retry = page.getByRole('button', { name: '重试稀客推荐', exact: true });
  await retry.waitFor();
  await retry.click();
  await retry.waitFor({ state: 'hidden' });
  await page.getByText('推荐料理', { exact: true }).first().waitFor();
  assert.equal(await page.locator('[data-service-tabs]').getByRole('tab', { name: '诊断', exact: true }).count(), 0);

  // Disabling diagnostics must update the owner view as well as the visible tab,
  // so the previously selected normal-order view resumes its detail Worker.
  await top('设置');
  await page.locator('label.steward-switch-field').filter({ hasText: '显示调试信息' }).click();
  await top('经营');
  await page.locator('[data-service-tabs]').getByRole('tab', { name: '普客', exact: true }).click();
  await page.waitForFunction(() => window.__normalDetailPosts > 0);
  await page.locator('[data-service-tabs]').getByRole('tab', { name: '诊断', exact: true }).click();
  await top('设置');
  await page.locator('label.steward-switch-field').filter({ hasText: '显示调试信息' }).click();
  const normalDetailsBeforeReturn = await page.evaluate(() => window.__normalDetailPosts);
  await top('经营');
  await page.locator('[data-service-tabs]').getByRole('tab', { name: '普客', exact: true }).and(page.locator('[aria-selected="true"]')).waitFor();
  await page.waitForFunction((previous) => window.__normalDetailPosts > previous, normalDetailsBeforeReturn);

  // Page classification must retain distinct live/config polling rates.
  const snapshotTimes = [];
  page.on('request', (request) => {
    if (new URL(request.url()).pathname === '/snapshot') snapshotTimes.push(Date.now());
  });
  await top('自动化');
  await page.waitForTimeout(600);
  snapshotTimes.length = 0;
  await page.waitForTimeout(3300);
  const runtimeReads = snapshotTimes.length;
  await page.locator('[data-automation-tabs]').getByRole('tab', { name: '执行配置', exact: true }).click();
  await page.waitForTimeout(600);
  snapshotTimes.length = 0;
  await page.waitForTimeout(3300);
  assert.ok(runtimeReads >= 3, `运行状态仍需实时刷新：${runtimeReads}`);
  assert.ok(snapshotTimes.length <= 2, `配置页不能沿用运行页的高频轮询：${snapshotTimes.length}`);
  assert.deepEqual(errors, []);
  console.log('navigation lifecycle passed: exact legacy migration, conditional diagnostics, worker retry, live/config polling');
} finally {
  await browser.close();
}
