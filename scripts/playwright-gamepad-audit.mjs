import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

/**
 * 伴随窗口手柄巡检脚本。
 *
 * 配合 mock-local-api 与 Vite preview 使用，通过注入 navigator.getGamepads()
 * 验证短按、失焦释放、确认键焦点恢复和 Select 展开这些高风险路径。
 */
const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.GAMEPAD_AUDIT_OUTPUT_DIR || '/tmp/mystia-companion-gamepad-audit';
const STORAGE_PREFIX = 'mystia-steward-companion';

const BUTTON_A = 0;
const BUTTON_B = 1;
const BUTTON_Y = 3;
const BUTTON_LB = 4;
const BUTTON_RB = 5;
const BUTTON_DPAD_UP = 12;
const BUTTON_DPAD_DOWN = 13;
const BUTTON_DPAD_LEFT = 14;
const BUTTON_DPAD_RIGHT = 15;

const issues = [];
const screenshots = [];

await mkdir(OUTPUT_DIR, { recursive: true });

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });

await page.addInitScript(seedLocalStorage, {
  apiUrl: API_URL,
  apiToken: API_TOKEN,
  storagePrefix: STORAGE_PREFIX,
});
await page.addInitScript(installMockGamepad);

await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
await page.waitForSelector('[data-gamepad-tab-value="overview"]', { timeout: 10000 });
await page.waitForFunction(() => Boolean(navigator.getGamepads?.()[0]?.connected), null, { timeout: 5000 });

await auditTabShortPress(page);
await auditReleaseWhileBlurred(page);
await auditConfirmFallback(page);
await auditSelectConfirm(page);
await auditFocusMode(page);
await auditCompoundControls(page);
await captureScreenshot(page, 'final');

await browser.close();

const report = buildReport();
await writeFile(path.join(OUTPUT_DIR, 'report.md'), report);
console.log(report);
console.log(`\nGamepad audit report written to ${OUTPUT_DIR}`);

if (issues.length > 0) {
  process.exitCode = 1;
}

function seedLocalStorage({ apiUrl, apiToken, storagePrefix }) {
  localStorage.setItem(`${storagePrefix}-mod-api-endpoint`, apiUrl);
  localStorage.setItem(`${storagePrefix}-mod-api-token`, apiToken);
  localStorage.setItem(`${storagePrefix}-show-debug-details`, '1');
  localStorage.setItem(`${storagePrefix}-gamepad-navigation`, '1');
  localStorage.setItem(`${storagePrefix}-automation-enabled`, '1');
  localStorage.setItem(`${storagePrefix}-auto-normal-order-enabled`, '1');
  localStorage.setItem(`${storagePrefix}-auto-normal-take-beverage`, '1');
  localStorage.setItem(`${storagePrefix}-auto-normal-start-cooking`, '1');
  localStorage.setItem(`${storagePrefix}-auto-normal-collect-cooking`, '1');
  localStorage.setItem(`${storagePrefix}-auto-normal-deliver-food`, '1');
  localStorage.setItem(`${storagePrefix}-auto-normal-complete-order`, '1');
  localStorage.setItem(`${storagePrefix}-auto-prep-take-beverage`, '1');
  localStorage.setItem(`${storagePrefix}-auto-prep-start-cooking`, '1');
  localStorage.setItem(`${storagePrefix}-auto-prep-collect-cooking`, '1');
  localStorage.setItem(`${storagePrefix}-auto-prep-complete-order`, '1');
  localStorage.setItem(`${storagePrefix}-game-ui-pinning`, '1');
  localStorage.setItem(`${storagePrefix}-cooker-highlight`, '1');
  localStorage.setItem(`${storagePrefix}-background-opacity`, '0.82');
  localStorage.setItem(`${storagePrefix}-content-opacity`, '1');
}

function installMockGamepad() {
  const buttons = Array.from({ length: 17 }, () => ({ pressed: false, touched: false, value: 0 }));
  const axes = [0, 0, 0, 0];
  let focused = true;
  let timestamp = 1;
  const nativeHasFocus = document.hasFocus.bind(document);
  const gamepad = {
    axes,
    buttons,
    connected: true,
    id: 'Playwright Standard Gamepad',
    index: 0,
    mapping: 'standard',
    timestamp,
  };

  Object.defineProperty(document, 'hasFocus', {
    configurable: true,
    value: () => focused && nativeHasFocus(),
  });
  Object.defineProperty(navigator, 'getGamepads', {
    configurable: true,
    value: () => [gamepad],
  });

  window.__mockGamepad = {
    axis(index, value) {
      axes[index] = value;
      gamepad.timestamp = timestamp += 1;
    },
    button(index, pressed) {
      buttons[index] = {
        pressed,
        touched: pressed,
        value: pressed ? 1 : 0,
      };
      gamepad.timestamp = timestamp += 1;
    },
    connected(value) {
      gamepad.connected = value;
      gamepad.timestamp = timestamp += 1;
      window.dispatchEvent(new Event(value ? 'gamepadconnected' : 'gamepaddisconnected'));
    },
    focused(value) {
      focused = value;
      window.dispatchEvent(new Event(value ? 'focus' : 'blur'));
    },
    reset() {
      for (let index = 0; index < buttons.length; index += 1) {
        buttons[index] = { pressed: false, touched: false, value: 0 };
      }
      axes.fill(0);
      focused = true;
      gamepad.connected = true;
      gamepad.timestamp = timestamp += 1;
    },
  };
}

async function auditTabShortPress(page) {
  await activateTopTab(page, 'overview');

  await pressButton(page, BUTTON_RB, { holdMs: 70 });
  await expectTopTab(page, 'normal', 'RB 短按应从“概览”切到“普客”');

  await pressButton(page, BUTTON_LB, { holdMs: 70 });
  await expectTopTab(page, 'overview', 'LB 短按应从“普客”切回“概览”');
}

async function auditReleaseWhileBlurred(page) {
  await activateTopTab(page, 'overview');

  await setButton(page, BUTTON_RB, true);
  await page.waitForTimeout(80);
  await expectTopTab(page, 'normal', 'RB 按下后应先切到“普客”');

  await page.evaluate(() => window.__mockGamepad.focused(false));
  await setButton(page, BUTTON_RB, false);
  await page.waitForTimeout(80);
  await page.evaluate(() => window.__mockGamepad.focused(true));

  await pressButton(page, BUTTON_RB, { holdMs: 70 });
  await expectTopTab(page, 'rare', '失焦期间释放 RB 后，再按 RB 应继续切到“稀客”');
}

async function auditConfirmFallback(page) {
  await activateTopTab(page, 'overview');
  await page.locator('[data-gamepad-tab-value="overview"]').first().focus();

  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  const focusedTab = await readFocusedTopTab(page);
  if (focusedTab !== 'normal') {
    issues.push(`方向键右移后焦点应落到 normal Tab，实际为 ${focusedTab || '空'}。`);
    return;
  }

  await page.evaluate(() => {
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
  });
  await pressButton(page, BUTTON_A, { holdMs: 70 });
  await expectTopTab(page, 'normal', '焦点丢失后按 A 应激活上一次手柄高亮的“普客”Tab');
}

async function auditSelectConfirm(page) {
  await activateTopTab(page, 'normal');
  await page.waitForTimeout(300);

  const select = page.locator('[data-slot="select"] input:not(:disabled), input.steward-select-input:not(:disabled)').first();
  if (!(await select.count()) || !(await select.isVisible())) {
    issues.push('未找到可见 Select 输入，无法验证 A 键展开下拉框。');
    return;
  }

  await select.scrollIntoViewIfNeeded();
  await select.focus();
  await page.waitForTimeout(80);

  const initiallyExpanded = await isComboboxExpanded(page);
  if (initiallyExpanded) {
    issues.push('Select 聚焦后已自动展开，预期应等待 A/确认键。');
    await page.keyboard.press('Escape');
    return;
  }

  await pressButton(page, BUTTON_A, { holdMs: 70 });
  await page.waitForTimeout(160);
  const expandedAfterConfirm = await isComboboxExpanded(page);
  if (!expandedAfterConfirm) {
    issues.push('Select 聚焦后按 A 未展开下拉框。');
  }
  await page.keyboard.press('Escape');
}

async function auditFocusMode(page) {
  await activateTopTab(page, 'service');
  await pressButton(page, BUTTON_Y, { holdMs: 70 });
  await page.waitForTimeout(220);

  const entered = await page.locator('h1', { hasText: '稀客订单专注模式' }).count();
  if (!entered) {
    issues.push('Y 键未进入稀客订单专注模式。');
    return;
  }

  const hasContentScope = await page.locator('[data-gamepad-scope="content"]', { hasText: '稀客订单专注模式' }).count();
  if (!hasContentScope) {
    issues.push('稀客订单专注模式缺少可见的 content 手柄导航 scope。');
  }

  await pressButton(page, BUTTON_B, { holdMs: 70 });
  await page.waitForTimeout(220);
  const stillFocused = await page.locator('h1', { hasText: '稀客订单专注模式' }).count();
  if (stillFocused) {
    issues.push('B 键未退出稀客订单专注模式。');
  }
}

async function auditCompoundControls(page) {
  await auditInnerTabs(page);
  await auditPlaceToolbarAndRareSelectors(page);
  await auditSegmentedControl(page);
  await auditSlider(page);
  await auditAxisGroup(page);
}

async function auditInnerTabs(page) {
  await activateTopTab(page, 'settings');
  await focusInnerTab(page, '窗口');
  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  await expectFocusedInnerTab(page, '连接', '设置页“窗口”按右键应聚焦“连接”二级 Tab');

  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  await expectFocusedInnerTab(page, '推荐', '设置页“连接”按右键应聚焦“推荐”二级 Tab');

  await pressButton(page, BUTTON_DPAD_LEFT, { holdMs: 70 });
  await expectFocusedInnerTab(page, '连接', '设置页“推荐”按左键应回到“连接”二级 Tab');

  await pressButton(page, BUTTON_DPAD_LEFT, { holdMs: 70 });
  await expectFocusedInnerTab(page, '窗口', '设置页“连接”按左键应回到“窗口”二级 Tab');

  await pressButton(page, BUTTON_DPAD_DOWN, { holdMs: 70 });
  await expectFocusedLabel(page, /背景透明度/, '设置页“窗口”按下键应进入背景透明度控件');

  await pressButton(page, BUTTON_DPAD_UP, { holdMs: 70 });
  await expectFocusedInnerTab(page, '窗口', '设置页内容区按上键应回到当前二级 Tab');

  await activateTopTab(page, 'overview');
  await focusInnerTab(page, '状态');
  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  await expectFocusedInnerTab(page, '库存', '概览页“状态”按右键应聚焦“库存”二级 Tab');

  await activateTopTab(page, 'service');
  await focusInnerTab(page, '稀客');
  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  await expectFocusedInnerTab(page, '普客', '经营中页“稀客”按右键应聚焦“普客”二级 Tab');
}

async function auditPlaceToolbarAndRareSelectors(page) {
  await activateTopTab(page, 'rare');
  await focusVisibleLocator(page, 'input[placeholder="选择地区"]');
  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  await expectFocusedText(page, /跟随经营场景/, '稀客页地区下拉框按右键应聚焦“跟随经营场景”按钮');

  await pressButton(page, BUTTON_DPAD_LEFT, { holdMs: 70 });
  await expectFocusedText(page, /选择地区/, '稀客页“跟随经营场景”按左键应回到地区下拉框');

  await focusVisibleLocator(page, 'input[aria-label="稀客"]');
  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  await expectFocusedText(page, /点单料理 Tag/, '稀客页“稀客”下拉框按右键应聚焦“点单料理 Tag”');

  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  await expectFocusedText(page, /点单酒水 Tag/, '稀客页“点单料理 Tag”按右键应聚焦“点单酒水 Tag”');

  await activateTopTab(page, 'normal');
  await focusVisibleLocator(page, 'input[placeholder="选择地区"]');
  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  await expectFocusedText(page, /跟随经营场景/, '普客页地区下拉框按右键应聚焦“跟随经营场景”按钮');
}

async function auditSegmentedControl(page) {
  await activateTopTab(page, 'settings');

  const root = page.locator('[data-slot="segmented-control"]:visible').first();
  if (!(await root.count())) {
    issues.push('未找到可用 SegmentedControl 选项，无法验证横向选项组。');
    return;
  }

  await root.scrollIntoViewIfNeeded();
  const focused = await root.evaluate((element) => {
    const option = Array.from(element.querySelectorAll('label'))
      .find((label) =>
        label instanceof HTMLElement
        && label instanceof HTMLLabelElement
        && label.control instanceof HTMLInputElement
        && !label.control.disabled
        && label.getBoundingClientRect().width > 0
        && label.getBoundingClientRect().height > 0
      );
    if (!(option instanceof HTMLElement)) return false;
    if (!option.matches('[tabindex]')) option.tabIndex = -1;
    option.focus();
    return true;
  });
  if (!focused) {
    issues.push('未找到可聚焦的 SegmentedControl 内部选项，无法验证横向选项组。');
    return;
  }
  await page.waitForTimeout(80);
  const before = await readFocusedSummary(page);
  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  const afterRight = await readFocusedSummary(page);
  if (!before?.segmented || !afterRight?.segmented) {
    issues.push('SegmentedControl 按右键后焦点离开了当前选项组。');
    return;
  }

  await pressButton(page, BUTTON_DPAD_LEFT, { holdMs: 70 });
  const afterLeft = await readFocusedSummary(page);
  if (!afterLeft?.segmented) {
    issues.push('SegmentedControl 按左键后焦点离开了当前选项组。');
  }
}

async function auditSlider(page) {
  await activateTopTab(page, 'settings');

  const slider = page.locator('[role="slider"]').first();
  if (!(await slider.count())) {
    issues.push('未找到 Mantine Slider thumb，无法验证手柄左右调值。');
    return;
  }

  await slider.scrollIntoViewIfNeeded();
  await slider.focus();
  await page.waitForTimeout(80);
  const before = await readFocusedSummary(page);
  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  const afterRight = await readFocusedSummary(page);
  if (before?.role !== 'slider' || afterRight?.role !== 'slider') {
    issues.push('Slider 按右键后焦点离开了 slider。');
    return;
  }
  if (before.value === afterRight.value) {
    issues.push('Slider 按右键后数值未变化。');
  }

  await pressButton(page, BUTTON_DPAD_LEFT, { holdMs: 70 });
  const afterLeft = await readFocusedSummary(page);
  if (afterLeft?.role !== 'slider') {
    issues.push('Slider 按左键后焦点离开了 slider。');
  }
}

async function auditAxisGroup(page) {
  await activateTopTab(page, 'tasks');
  await page.waitForTimeout(300);

  const filterButton = page.getByRole('button', { name: /可接取/ }).first();
  if (!(await filterButton.count())) return;

  await filterButton.scrollIntoViewIfNeeded();
  await filterButton.focus();
  await page.waitForTimeout(80);
  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  const afterRight = await readFocusedSummary(page);
  if (!afterRight?.text.includes('进行中')) {
    issues.push(`任务筛选按钮组按右键后未聚焦“进行中”，实际为 ${afterRight?.text || '空'}。`);
  }
}

async function activateTopTab(page, value) {
  const trigger = page.locator(`[data-gamepad-tab-value="${value}"]`).first();
  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
  await page.waitForTimeout(180);
  await expectTopTab(page, value, `应能切换到 ${value} Tab`);
}

async function pressButton(page, index, { holdMs = 70 } = {}) {
  await setButton(page, index, true);
  await page.waitForTimeout(holdMs);
  await setButton(page, index, false);
  await page.waitForTimeout(120);
}

async function setButton(page, index, pressed) {
  await page.evaluate(
    ({ buttonIndex, buttonPressed }) => window.__mockGamepad.button(buttonIndex, buttonPressed),
    { buttonIndex: index, buttonPressed: pressed },
  );
}

async function expectTopTab(page, value, message) {
  try {
    await page.waitForFunction(
      (expectedValue) => {
        const tab = document.querySelector(`[data-gamepad-tab-value="${expectedValue}"]`);
        return Boolean(tab?.hasAttribute('data-active') || tab?.getAttribute('aria-selected') === 'true');
      },
      value,
      { timeout: 1200 },
    );
  } catch {
    const active = await readActiveTopTab(page);
    issues.push(`${message}，实际当前 Tab 为 ${active || '空'}。`);
  }
}

async function readActiveTopTab(page) {
  return page.evaluate(() => {
    const tab = Array.from(document.querySelectorAll('[data-gamepad-tab-value]'))
      .find((element) => element.hasAttribute('data-active') || element.getAttribute('aria-selected') === 'true');
    return tab instanceof HTMLElement ? tab.dataset.gamepadTabValue || '' : '';
  });
}

async function readFocusedTopTab(page) {
  return page.evaluate(() => {
    const active = document.activeElement;
    return active instanceof HTMLElement ? active.dataset.gamepadTabValue || '' : '';
  });
}

async function focusVisibleLocator(page, selector) {
  const locator = page.locator(`${selector}:visible`).first();
  await locator.scrollIntoViewIfNeeded();
  await locator.focus();
  await page.waitForTimeout(80);
}

async function focusInnerTab(page, label) {
  const trigger = page.locator('[data-slot="tabs-trigger"]:not([data-gamepad-tab])')
    .filter({ hasText: label })
    .first();
  await trigger.scrollIntoViewIfNeeded();
  await trigger.focus();
  await page.waitForTimeout(80);
}

async function expectFocusedInnerTab(page, label, message) {
  const focused = await readFocusedSummary(page);
  if (!focused?.innerTab || focused.text !== label) {
    issues.push(`${message}，实际为 ${focused?.text || '空'}。`);
  }
}

async function expectFocusedLabel(page, pattern, message) {
  const focused = await readFocusedSummary(page);
  if (!focused?.text.match(pattern)) {
    issues.push(`${message}，实际为 ${focused?.text || '空'}。`);
  }
}

async function expectFocusedText(page, pattern, message) {
  const focused = await readFocusedSummary(page);
  if (!focused?.text.match(pattern)) {
    issues.push(`${message}，实际为 ${focused?.text || '空'}。`);
  }
}

async function readFocusedSummary(page) {
  return page.evaluate(() => {
    const element = document.activeElement;
    if (!(element instanceof HTMLElement)) return null;
    const label = element.closest('label');
    return {
      text: element.textContent?.trim().replace(/\s+/g, ' ')
        || label?.textContent?.trim().replace(/\s+/g, ' ')
        || element.getAttribute('aria-label')
        || element.getAttribute('placeholder')
        || '',
      role: element.getAttribute('role') || '',
      value: element.getAttribute('aria-valuenow') || element.getAttribute('value') || '',
      innerTab: element.matches('[data-slot="tabs-trigger"]:not([data-gamepad-tab])'),
      segmented: Boolean(element.closest('[data-slot="segmented-control"]')),
    };
  });
}

async function isComboboxExpanded(page) {
  return page.evaluate(() => {
    const selectors = [
      '[data-slot="select"][aria-expanded="true"]',
      '[data-slot="multi-select"][aria-expanded="true"]',
      '[data-slot="select"] [aria-expanded="true"]',
      '[data-slot="multi-select"] [aria-expanded="true"]',
      '[role="listbox"]',
    ].join(',');
    return Array.from(document.querySelectorAll(selectors)).some((node) => {
      if (!(node instanceof HTMLElement)) return false;
      const rect = node.getBoundingClientRect();
      const style = window.getComputedStyle(node);
      return rect.width > 0
        && rect.height > 0
        && style.display !== 'none'
        && style.visibility !== 'hidden';
    });
  });
}

async function captureScreenshot(page, name) {
  const screenshotPath = path.join(OUTPUT_DIR, `${name}.png`);
  await page.screenshot({ path: screenshotPath, fullPage: true });
  screenshots.push(screenshotPath);
}

function buildReport() {
  const lines = [
    '# mystia-steward-companion gamepad audit',
    '',
    `- App: ${APP_URL}`,
    `- API: ${API_URL}`,
    `- Output: ${OUTPUT_DIR}`,
    '',
    '## Issues',
    '',
  ];

  if (issues.length === 0) {
    lines.push('- 未发现自动化可判定的手柄导航问题。');
  } else {
    for (const issue of issues) {
      lines.push(`- ${issue}`);
    }
  }

  lines.push('', '## Screenshots', '');
  for (const screenshot of screenshots) {
    lines.push(`- ${screenshot}`);
  }

  return `${lines.join('\n')}\n`;
}
