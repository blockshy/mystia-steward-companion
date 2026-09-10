import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const OUTPUT = process.env.UI_LIFECYCLE_OUTPUT_DIR || '/tmp/mystia-ui-lifecycle';
const browser = await chromium.launch({ headless: true,
  ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}),
});
await mkdir(OUTPUT, { recursive: true });
const page = await browser.newPage({ viewport: { width: 390, height: 844 }, hasTouch: true });
page.setDefaultTimeout(12000);
let favoriteWrites = 0;
const snapshotOrigins = [];
page.on('request', (request) => {
  const url = new URL(request.url());
  if (url.pathname.startsWith('/favorites/') && request.method() === 'POST') favoriteWrites += 1;
  if (url.pathname === '/snapshot') snapshotOrigins.push(url.origin);
});

try {
  await page.goto(APP_URL);
  await page.getByRole('textbox', { name: 'API 地址（IP / 端口）', exact: true }).waitFor();
  assert.equal(await page.locator('[data-gamepad-tab-value="overview"]').getAttribute('aria-selected'), 'true');
  await page.getByRole('textbox', { name: 'API 地址（IP / 端口）', exact: true }).fill(API_URL);
  await page.getByLabel('Mod API Token', { exact: true }).fill(process.env.MYSTIA_API_TOKEN || 'mock-token');
  await page.getByRole('button', { name: '应用并连接', exact: true }).click();
  await page.locator('[data-overview-connection-status-metric="connection"]').getByText('已连接', { exact: true }).waitFor();

  // A resume must use the applied identity, leaving an edited draft untouched.
  const toggle = page.locator('[data-gamepad-focus-key="overview:connection:toggle"]');
  await toggle.click();
  const draft = `${API_URL}/unapplied`;
  await page.getByRole('textbox', { name: 'API 地址（IP / 端口）', exact: true }).fill(draft);
  await toggle.click();
  await page.locator('[data-overview-connection-status-metric="connection"]').getByText('已连接', { exact: true }).waitFor();
  assert.equal(await page.getByRole('textbox', { name: 'API 地址（IP / 端口）', exact: true }).inputValue(), draft);
  assert(snapshotOrigins.every((origin) => origin === new URL(API_URL).origin));
  await page.getByRole('button', { name: '放弃修改', exact: true }).click();
  assert.equal(await page.getByRole('textbox', { name: 'API 地址（IP / 端口）', exact: true }).inputValue(), API_URL);

  // Intercept only the page worker delivery; the real recommendation engine still computes every result.
  await page.evaluate(() => {
    const NativeWorker = window.Worker;
    window.recommendationProbe = { hold: false, failNext: false, failConstructorNext: false, workers: [], pending: [], release() {
      this.hold = false;
      const pending = this.pending.splice(0);
      pending.forEach((deliver) => deliver());
    } };
    window.Worker = class extends NativeWorker {
      constructor(url, options) {
        if (window.recommendationProbe.failConstructorNext && String(url).includes('page-recommendations')) {
          window.recommendationProbe.failConstructorNext = false;
          throw new Error('专项Worker启动失败');
        }
        super(url, options);
        this.isPageRecommendation = String(url).includes('page-recommendations');
        if (this.isPageRecommendation) window.recommendationProbe.workers.push(this);
      }
      set onmessage(callback) {
        super.onmessage = (event) => {
          if (!this.isPageRecommendation) { callback(event); return; }
          const probe = window.recommendationProbe;
          const deliver = () => {
            if (probe.failNext) {
              probe.failNext = false;
              callback(new MessageEvent('message', { data: { requestId: event.data.requestId, ok: false, error: '专项模拟计算失败' } }));
            } else callback(event);
          };
          if (probe.hold) probe.pending.push(deliver);
          else deliver();
        };
      }
    };
  });
  await primary('recommendations');
  await page.locator('[data-recommendation-tabs]').getByRole('tab', { name: '稀客', exact: true }).click();
  const stars = page.getByRole('button', { name: /^(取消)?收藏该(料理|酒水)$/ });
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some((button) => /^(取消)?收藏该/.test(button.getAttribute('aria-label') || '') && !button.disabled));
  const customer = page.getByRole('combobox', { name: '稀客', exact: true });
  const previous = await customer.inputValue();
  await page.evaluate(() => { window.recommendationProbe.hold = true; });
  await customer.click();
  const alternatives = page.getByRole('option');
  const names = await alternatives.allTextContents();
  const nextName = names.find((name) => name.trim() !== previous);
  assert(nextName, 'Fixture needs two rare guests in the detected place.');
  await page.getByRole('option', { name: nextName.trim(), exact: true }).click();
  await page.waitForFunction(() => window.recommendationProbe.pending.length > 0);
  assert.equal(await stars.count(), 0, 'Old guest recommendations must disappear while another guest is selected.');
  await page.evaluate(() => window.recommendationProbe.release());
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some((button) => /^(取消)?收藏该/.test(button.getAttribute('aria-label') || '') && !button.disabled));

  await page.evaluate(() => { window.recommendationProbe.failNext = true; });
  await stars.first().click();
  await page.getByText('推荐更新失败：专项模拟计算失败', { exact: false }).waitFor();
  assert(await stars.count() > 0, 'A failed refresh of the same selection should retain its previous rows.');
  assert(await stars.evaluateAll((buttons) => buttons.every((button) => button.disabled)), 'Retained rows must not be writable after failure.');
  await page.getByRole('button', { name: '重新计算', exact: true }).click();
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some((button) => /^(取消)?收藏该/.test(button.getAttribute('aria-label') || '') && !button.disabled));
  await page.evaluate(() => window.recommendationProbe.workers.at(-1).dispatchEvent(new ErrorEvent('error', { message: '专项Worker故障' })));
  await page.getByText('推荐更新失败：专项Worker故障', { exact: false }).waitFor();
  await customer.click();
  await page.getByRole('option', { name: previous, exact: true }).click();
  await page.getByText('推荐更新失败：专项Worker故障', { exact: false }).waitFor();
  assert.equal(await stars.count(), 0);
  await page.evaluate(() => { window.recommendationProbe.failConstructorNext = true; });
  await page.getByRole('button', { name: '重新计算', exact: true }).click();
  await page.getByRole('alert').getByText('推荐更新失败：无法启动后台推荐计算：Error: 专项Worker启动失败', { exact: true }).waitFor();
  await page.getByRole('button', { name: '重新计算', exact: true }).click();
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some((button) => /^(取消)?收藏该/.test(button.getAttribute('aria-label') || '') && !button.disabled));
  await primary('overview');
  await toggle.click();
  await primary('recommendations');
  await page.locator('[data-recommendation-tabs]').getByRole('tab', { name: '收藏管理', exact: true }).click();
  assert(await page.getByRole('button', { name: '刷新', exact: true }).isDisabled());
  assert(await page.getByRole('button', { name: /取消收藏/ }).evaluateAll((buttons) => buttons.every((button) => button.disabled)));
  const writesAtPause = favoriteWrites;
  await page.screenshot({ path: `${OUTPUT}/paused-favorites-390.png`, fullPage: true });
  assert.equal(favoriteWrites, writesAtPause);
  console.log('PASS: initial connection, explicit drafts, current recommendation ownership, retained and fatal error/retry, and paused favorite controls.');
} finally {
  await browser.close();
}

async function primary(value) {
  await page.locator(`[data-gamepad-tab-value="${value}"]`).click();
}
