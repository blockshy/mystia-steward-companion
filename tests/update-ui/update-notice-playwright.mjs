import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const appUrl = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173';
const apiUrl = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const apiToken = process.env.MYSTIA_API_TOKEN || 'mock-token';
const outputDir = process.env.UPDATE_UI_AUDIT_OUTPUT_DIR || '/tmp/mystia-companion-update-ui-audit';

function createUpdateStatus(overrides = {}) {
  return {
    ok: true,
    currentVersion: '1.2.0',
    enabled: true,
    autoCheck: true,
    includePrerelease: false,
    state: 'available',
    latestVersion: '1.2.1',
    latestTag: 'v1.2.1',
    hasUpdate: true,
    lastAttemptAtUtc: new Date().toISOString(),
    lastSuccessAtUtc: new Date().toISOString(),
    nextCheckAtUtc: new Date(Date.now() + 60_000).toISOString(),
    consecutiveFailures: 0,
    publishedAtUtc: new Date().toISOString(),
    releaseUrl: 'https://github.com/blockshy/mystia-steward-companion/releases/tag/v1.2.1',
    packageAsset: 'mystia-steward-companion-bepinex.zip',
    packageSize: 1024,
    downloadedVersion: '',
    downloadedAtUtc: '',
    staged: false,
    installState: '',
    installMessage: '',
    error: null,
    ...overrides,
  };
}

await mkdir(outputDir, { recursive: true });
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 640, height: 760 } });
await page.addInitScript(({ endpoint, token }) => {
  localStorage.setItem('mystia-steward-companion-mod-api-endpoint', endpoint);
  localStorage.setItem('mystia-steward-companion-mod-api-token', token);
  window.__updateNoticeEverVisible = false;
  const markVisibleNotice = () => {
    if (document.querySelector('[data-update-notice="visible"]')) {
      window.__updateNoticeEverVisible = true;
    }
  };
  new MutationObserver(markVisibleNotice).observe(document, { childList: true, subtree: true });
}, { endpoint: apiUrl, token: apiToken });

try {
  await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
  const notice = page.locator('[data-update-notice="visible"]');
  await notice.waitFor({ state: 'visible', timeout: 10_000 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth), true);
  await page.screenshot({ path: `${outputDir}/notice-minimum.png`, fullPage: true });

  await notice.getByRole('button', { name: '查看更新' }).click();
  await page.locator('[data-gamepad-tab-value="settings"][aria-selected="true"]').waitFor();
  await page.getByRole('tab', { name: '更新', exact: true }).and(page.locator('[aria-selected="true"]')).waitFor();
  await page.getByText('最近成功检查', { exact: true }).waitFor();
  await page.getByText('下次自动检查', { exact: true }).waitFor();
  await page.screenshot({ path: `${outputDir}/settings-update-minimum.png`, fullPage: true });

  let failSnapshots = false;
  const snapshotRoute = (route) => failSnapshots
    ? route.fulfill({
      status: 503,
      contentType: 'application/json',
      body: JSON.stringify({ ok: false, error: 'simulated transient disconnect' }),
    })
    : route.continue();
  await page.route('**/snapshot*', snapshotRoute);

  let releaseInterruptedDownload;
  let markInterruptedDownloadStarted;
  let downloadRequestCount = 0;
  const interruptedDownloadStarted = new Promise((resolve) => {
    markInterruptedDownloadStarted = resolve;
  });
  const downloadRoute = async (route) => {
    downloadRequestCount += 1;
    if (downloadRequestCount !== 1) {
      await route.continue();
      return;
    }
    markInterruptedDownloadStarted();
    await new Promise((resolve) => {
      releaseInterruptedDownload = resolve;
    });
    try {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(createUpdateStatus({
          state: 'downloaded',
          latestVersion: '9.9.9',
          latestTag: 'v9.9.9',
          downloadedVersion: '9.9.9',
          downloadedAtUtc: new Date().toISOString(),
          staged: true,
        })),
      });
    } catch {
      // The browser fetch is expected to be aborted when the connection drops.
    }
  };
  await page.route('**/updates/download', downloadRoute);

  const downloadButton = page.getByRole('button', { name: '下载', exact: true });
  await downloadButton.click();
  await interruptedDownloadStarted;
  assert.equal(await downloadButton.getAttribute('data-loading'), 'true');

  failSnapshots = true;
  await page.getByRole('button', { name: '刷新', exact: true }).click();
  await page.getByText('重试中', { exact: true }).waitFor();
  assert.equal(
    await downloadButton.getAttribute('data-loading'),
    null,
    'a transient disconnect must clear the interrupted update action busy state',
  );

  failSnapshots = false;
  await page.getByRole('button', { name: '刷新', exact: true }).click();
  await page.getByText('已连接', { exact: true }).waitFor();
  releaseInterruptedDownload();
  await page.waitForTimeout(100);
  assert.equal(await page.getByText('v9.9.9', { exact: false }).count(), 0);

  await downloadButton.click();
  await page.getByText('已下载', { exact: true }).waitFor();

  let releaseSwitchedInstall;
  let markSwitchedInstallStarted;
  let installRequestCount = 0;
  const switchedInstallStarted = new Promise((resolve) => {
    markSwitchedInstallStarted = resolve;
  });
  const installRoute = async (route) => {
    installRequestCount += 1;
    if (installRequestCount !== 1) {
      await route.continue();
      return;
    }
    markSwitchedInstallStarted();
    await new Promise((resolve) => {
      releaseSwitchedInstall = resolve;
    });
    try {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(createUpdateStatus({
          state: 'downloaded',
          latestVersion: '9.9.9',
          latestTag: 'v9.9.9',
          downloadedVersion: '9.9.9',
          downloadedAtUtc: new Date().toISOString(),
          staged: true,
          installState: 'failed',
          installMessage: 'stale connection failure',
          error: 'stale connection failure',
        })),
      });
    } catch {
      // The old connection request is deliberately invalidated by the identity switch.
    }
  };
  await page.route('**/updates/install-on-exit', installRoute);

  const installButton = page.getByRole('button', { name: '打开安装程序', exact: true });
  await installButton.click();
  await switchedInstallStarted;
  assert.equal(await installButton.getAttribute('data-loading'), 'true');

  const switchedApiToken = `${apiToken}-switched`;
  const alternateSnapshot = page.waitForResponse((response) => (
    response.url().startsWith(new URL(apiUrl).origin)
    && new URL(response.url()).pathname === '/snapshot'
    && response.request().headers()['x-mystia-steward-companion-token'] === switchedApiToken
    && response.ok()
  ));
  const tokenInput = page.locator('.steward-workbench-header input').nth(1);
  await tokenInput.fill(switchedApiToken);
  await tokenInput.press('Enter');
  await alternateSnapshot;
  await page.getByText('已连接', { exact: true }).waitFor();
  assert.equal(
    await installButton.getAttribute('data-loading'),
    null,
    'switching connection identity must clear the old action busy state',
  );

  releaseSwitchedInstall();
  await page.waitForTimeout(100);
  assert.equal(await page.getByText('v9.9.9', { exact: false }).count(), 0);
  assert.equal(await page.getByText('stale connection failure', { exact: true }).count(), 0);

  await installButton.click();
  await page.getByText('更新程序已打开', { exact: true }).waitFor();
  await page.unroute('**/snapshot*', snapshotRoute);
  await page.unroute('**/updates/download', downloadRoute);
  await page.unroute('**/updates/install-on-exit', installRoute);

  const originalApiOrigin = new URL(apiUrl).origin;
  const originalSnapshot = page.waitForResponse((response) => (
    response.url().startsWith(originalApiOrigin)
    && new URL(response.url()).pathname === '/snapshot'
    && response.request().headers()['x-mystia-steward-companion-token'] === apiToken
    && response.ok()
  ));
  await tokenInput.fill(apiToken);
  await tokenInput.press('Enter');
  await originalSnapshot;
  await notice.waitFor({ state: 'visible' });

  await notice.getByRole('button', { name: '24 小时后提醒' }).click();
  await notice.waitFor({ state: 'detached' });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.getByText('Mod 工作台', { exact: true }).waitFor();
  await page.waitForTimeout(500);
  assert.equal(await page.locator('[data-update-notice="visible"]').count(), 0);
  assert.equal(
    await page.evaluate(() => window.__updateNoticeEverVisible),
    false,
    'a persisted snooze must suppress the first render without flashing the notice',
  );

  await page.evaluate(() => {
    for (const key of Object.keys(localStorage)) {
      if (key.startsWith('mystia-steward-companion:update-notice-snooze:')) localStorage.removeItem(key);
    }
  });
  await page.route('**/updates/status', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(createUpdateStatus({
      state: 'installed',
      downloadedVersion: '1.2.1',
      downloadedAtUtc: new Date().toISOString(),
      staged: true,
      installState: 'failed',
      installMessage: '模拟安装失败，请重新打开安装程序。',
      error: '模拟安装失败，请重新打开安装程序。',
    })),
  }));
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.getByText('游戏端更新 v1.2.1 安装失败', { exact: true }).waitFor();
  await notice.getByText('模拟安装失败，请重新打开安装程序。', { exact: true }).waitFor();
  await page.screenshot({ path: `${outputDir}/install-failed-minimum.png`, fullPage: true });
} finally {
  await page.close();
  await browser.close();
}

console.log(`update notice Playwright audit passed; screenshots: ${outputDir}`);
