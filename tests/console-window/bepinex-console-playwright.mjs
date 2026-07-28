import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.LOG_CONSOLE_AUDIT_OUTPUT_DIR || '/tmp/mystia-companion-log-console-audit';
const STORAGE_PREFIX = 'mystia-steward-companion';

await mkdir(OUTPUT_DIR, { recursive: true });
await setMockConsoleVisibility(false);

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 640, height: 760 } });
const pageErrors = [];
const consoleErrors = [];

page.on('pageerror', (error) => pageErrors.push(error.stack || error.message));
page.on('console', (message) => {
  if (message.type() === 'error') consoleErrors.push(message.text());
});

try {
  await page.addInitScript(seedLocalStorage, {
    apiUrl: API_URL,
    apiToken: API_TOKEN,
    storagePrefix: STORAGE_PREFIX,
  });
  await page.addInitScript(installMockGamepad);
  await page.addInitScript(installAbortInsensitiveLogSettingsFetch);
  await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => Boolean(navigator.getGamepads?.()[0]?.connected));
  await page.waitForFunction(() => document.body.innerText.includes('1.0.5'), null, { timeout: 10000 });
  await page.locator('[data-gamepad-tab-value="logs"]').click();

  const showButton = page.getByRole('button', { name: '显示控制台', exact: true });
  await assertButtonReady(showButton, 'logs:toggle-bepinex-console');
  await showButton.focus();
  assert.equal(await showButton.evaluate((element) => document.activeElement === element), true);
  const refreshButton = page.locator('[data-gamepad-focus-key="logs:refresh"]');

  const staleSettings = await readMockLogSettings();
  await delayNextLogSettingsFetch(staleSettings, 700);
  await refreshButton.click();
  await page.waitForTimeout(100);
  await showButton.press('Enter');

  const hideButton = page.getByRole('button', { name: '隐藏控制台', exact: true });
  await hideButton.waitFor({ state: 'visible' });
  await assertButtonReady(hideButton, 'logs:toggle-bepinex-console');
  await assertConsoleState({
    configuredVisible: true,
    active: true,
    visible: true,
  });
  await page.waitForTimeout(800);
  await hideButton.waitFor({ state: 'visible' });

  await refreshButton.click();
  await waitForButtonEnabled(refreshButton);
  await assertConsoleState({
    configuredVisible: true,
    active: true,
    visible: true,
  });

  await hideButton.focus();
  await hideButton.press('Space');
  await showButton.waitFor({ state: 'visible' });
  await assertConsoleState({
    configuredVisible: false,
    active: true,
    visible: false,
  });

  const actionFailure = '模拟控制台切换失败';
  await setMockConsoleFailure(actionFailure, true);
  await showButton.click();
  const actionError = page.locator('div.text-destructive').filter({ hasText: actionFailure });
  await actionError.waitFor({ state: 'visible' });
  await hideButton.waitFor({ state: 'visible' });
  await assertConsoleState({
    configuredVisible: true,
    active: true,
    visible: true,
  });
  await page.waitForTimeout(3300);
  await actionError.waitFor({ state: 'visible' });

  await hideButton.focus();
  await pressGamepadButton(0);
  await showButton.waitFor({ state: 'visible' });
  await actionError.waitFor({ state: 'hidden' });

  const layout = await page.evaluate(() => {
    const button = document.querySelector('[data-gamepad-focus-key="logs:toggle-bepinex-console"]');
    if (!(button instanceof HTMLElement)) return { ok: false, reason: '未找到控制台按钮。' };
    const card = button.closest('[data-slot="card"]');
    if (!(card instanceof HTMLElement)) return { ok: false, reason: '未找到控制台卡片。' };

    const buttonRect = button.getBoundingClientRect();
    const cardRect = card.getBoundingClientRect();
    const documentElement = document.documentElement;
    return {
      ok: documentElement.scrollWidth <= documentElement.clientWidth + 1
        && buttonRect.left >= cardRect.left - 1
        && buttonRect.right <= cardRect.right + 1
        && buttonRect.left >= -1
        && buttonRect.right <= documentElement.clientWidth + 1,
      scrollWidth: documentElement.scrollWidth,
      clientWidth: documentElement.clientWidth,
      buttonLeft: Math.round(buttonRect.left),
      buttonRight: Math.round(buttonRect.right),
      cardLeft: Math.round(cardRect.left),
      cardRight: Math.round(cardRect.right),
    };
  });
  assert.equal(layout.ok, true, `640px 控制台布局异常：${JSON.stringify(layout)}`);

  await page.screenshot({
    path: path.join(OUTPUT_DIR, 'logs-console-640x760.png'),
    fullPage: true,
  });

  const endpointInput = page.locator('input').first();
  assert.equal(await endpointInput.inputValue(), API_URL);
  const staleConnectionSettings = {
    ...await readMockLogSettings(),
    bepInExConsoleConfiguredVisible: true,
    bepInExConsoleActive: true,
    bepInExConsoleVisible: true,
    bepInExConsoleStatus: 'visible',
  };
  await delayNextLogSettingsFetch(staleConnectionSettings, 700);
  await refreshButton.click();
  await page.waitForTimeout(100);
  await endpointInput.fill('http://192.168.1.20:32145');
  await endpointInput.press('Enter');
  await page.getByText('仅可在游戏电脑本机控制 BepInEx 控制台。', { exact: true })
    .waitFor({ state: 'visible' });
  await page.waitForTimeout(800);
  await showButton.waitFor({ state: 'visible' });
  assert.equal(
    await hideButton.count(),
    0,
    '旧连接的不可中止日志设置响应不应覆盖新连接状态。',
  );
  assert.equal(await showButton.isDisabled(), true, '远程连接不应允许控制游戏电脑上的控制台窗口。');

  assert.deepEqual(pageErrors, [], `页面异常：${pageErrors.join('\n')}`);
  assert.deepEqual(consoleErrors, [], `控制台异常：${consoleErrors.join('\n')}`);
} finally {
  await browser.close();
}

console.log('PASS: BepInEx console control defaults hidden, rejects stale refreshes, preserves action errors, accepts keyboard/gamepad activation, and fits the 640px layout.');
console.log(`Screenshot written to ${OUTPUT_DIR}`);

async function setMockConsoleVisibility(visible) {
  const response = await fetch(`${API_URL}/logs/console?visible=${String(visible)}`, {
    method: 'POST',
    headers: {
      'X-Mystia-Steward-Companion-Token': API_TOKEN,
    },
  });
  assert.equal(response.ok, true, `mock 控制台初始化失败：HTTP ${response.status}`);
  const payload = await response.json();
  assert.equal(payload.ok, true, `mock 控制台初始化失败：${payload.error || payload.status}`);
}

async function readMockLogSettings() {
  const response = await fetch(`${API_URL}/logs/settings`, {
    headers: {
      'X-Mystia-Steward-Companion-Token': API_TOKEN,
    },
  });
  assert.equal(response.ok, true, `读取 mock 日志设置失败：HTTP ${response.status}`);
  return response.json();
}

async function delayNextLogSettingsFetch(payload, delayMs) {
  await page.evaluate(
    ({ settings, delay }) => window.__mockLogSettingsFetch.delayOnce(settings, delay),
    { settings: payload, delay: delayMs },
  );
}

async function setMockConsoleFailure(message, reportedVisible) {
  const params = new URLSearchParams({
    message,
    reportedVisible: String(reportedVisible),
  });
  const response = await fetch(`${API_URL}/mock/logs/console-failure?${params.toString()}`, {
    method: 'POST',
  });
  assert.equal(response.ok, true, `mock 控制台失败设置失败：HTTP ${response.status}`);
}

async function assertConsoleState(expected) {
  const response = await fetch(`${API_URL}/logs/settings`, {
    headers: {
      'X-Mystia-Steward-Companion-Token': API_TOKEN,
    },
  });
  assert.equal(response.ok, true, `读取 mock 日志设置失败：HTTP ${response.status}`);
  const settings = await response.json();
  assert.equal(settings.bepInExConsoleConfiguredVisible, expected.configuredVisible);
  assert.equal(settings.bepInExConsoleActive, expected.active);
  assert.equal(settings.bepInExConsoleVisible, expected.visible);
}

async function assertButtonReady(locator, focusKey) {
  await locator.waitFor({ state: 'visible' });
  assert.equal(await locator.isEnabled(), true);
  assert.equal(await locator.getAttribute('data-gamepad-focus-key'), focusKey);
  assert.equal(await locator.evaluate((element) => element.tabIndex >= 0), true);
}

async function waitForButtonEnabled(locator) {
  await page.waitForFunction(
    (focusKey) => {
      const button = document.querySelector(`[data-gamepad-focus-key="${focusKey}"]`);
      return button instanceof HTMLButtonElement && !button.disabled;
    },
    await locator.getAttribute('data-gamepad-focus-key'),
  );
}

async function pressGamepadButton(index) {
  await page.evaluate((buttonIndex) => window.__mockGamepad.button(buttonIndex, true), index);
  await waitForAnimationFrames(3);
  await page.evaluate((buttonIndex) => window.__mockGamepad.button(buttonIndex, false), index);
  await waitForAnimationFrames(3);
}

async function waitForAnimationFrames(count) {
  await page.evaluate((frameCount) => new Promise((resolve) => {
    let remaining = frameCount;
    const step = () => {
      remaining -= 1;
      if (remaining <= 0) {
        resolve();
      } else {
        requestAnimationFrame(step);
      }
    };
    requestAnimationFrame(step);
  }), count);
}

function seedLocalStorage({ apiUrl, apiToken, storagePrefix }) {
  localStorage.setItem(`${storagePrefix}-mod-api-endpoint`, apiUrl);
  localStorage.setItem(`${storagePrefix}-mod-api-token`, apiToken);
  localStorage.setItem(`${storagePrefix}-show-debug-details`, '1');
}

function installMockGamepad() {
  const buttons = Array.from({ length: 17 }, () => ({ pressed: false, touched: false, value: 0 }));
  const axes = [0, 0, 0, 0];
  let timestamp = 1;
  const gamepad = {
    axes,
    buttons,
    connected: true,
    id: 'Playwright Standard Gamepad',
    index: 0,
    mapping: 'standard',
    timestamp,
  };

  Object.defineProperty(navigator, 'getGamepads', {
    configurable: true,
    value: () => [gamepad],
  });

  window.__mockGamepad = {
    button(index, pressed) {
      buttons[index] = {
        pressed,
        touched: pressed,
        value: pressed ? 1 : 0,
      };
      gamepad.timestamp = timestamp += 1;
    },
  };
}

function installAbortInsensitiveLogSettingsFetch() {
  const nativeFetch = window.fetch.bind(window);
  let delayedResponse = null;

  window.__mockLogSettingsFetch = {
    delayOnce(payload, delayMs) {
      delayedResponse = {
        payload,
        delayMs,
      };
    },
  };

  window.fetch = (input, init) => {
    const url = new URL(
      typeof input === 'string'
        ? input
        : input instanceof Request
          ? input.url
          : String(input),
      window.location.href,
    );
    if (delayedResponse !== null && url.pathname === '/logs/settings') {
      const response = delayedResponse;
      delayedResponse = null;
      return new Promise((resolve) => {
        window.setTimeout(() => {
          resolve(new Response(JSON.stringify(response.payload), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }));
        }, response.delayMs);
      });
    }

    return nativeFetch(input, init);
  };
}
