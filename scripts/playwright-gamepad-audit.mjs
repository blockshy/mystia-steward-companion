import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';
import { inspectMinimumPrimaryTabsLayout } from '../tests/ui-layout/primary-tabs-layout.mjs';

/**
 * 伴随窗口手柄巡检脚本。
 *
 * 配合 mock-local-api 与 Vite preview 使用，通过注入 navigator.getGamepads()
 * 验证中立门控、失焦恢复、摇杆仲裁和复合控件焦点这些高风险路径。
 */
const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.GAMEPAD_AUDIT_OUTPUT_DIR || '/tmp/mystia-companion-gamepad-audit';
const STORAGE_PREFIX = 'mystia-steward-companion';

const BUTTON_A = 0;
const BUTTON_B = 1;
const BUTTON_X = 2;
const BUTTON_Y = 3;
const BUTTON_LB = 4;
const BUTTON_RB = 5;
const BUTTON_LT = 6;
const BUTTON_RT = 7;
const BUTTON_DPAD_UP = 12;
const BUTTON_DPAD_DOWN = 13;
const BUTTON_DPAD_LEFT = 14;
const BUTTON_DPAD_RIGHT = 15;

const issues = [];
const screenshots = [];
const pageErrors = [];
const consoleErrors = [];
const runtimeDiagnostics = [];

await mkdir(OUTPUT_DIR, { recursive: true });

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
page.on('pageerror', (error) => pageErrors.push(error.stack || error.message));
page.on('console', (message) => {
  if (message.type() === 'error') consoleErrors.push(message.text());
});

await page.addInitScript(seedLocalStorage, {
  apiUrl: API_URL,
  apiToken: API_TOKEN,
  storagePrefix: STORAGE_PREFIX,
});
await page.addInitScript(installMockGamepad);

await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
await page.waitForSelector('[data-gamepad-tab-value="overview"]', { timeout: 10000 });
await page.waitForFunction(() => Boolean(navigator.getGamepads?.()[0]?.connected), null, { timeout: 5000 });
await waitForNeutralGate(page);

await auditTabShortPress(page);
await auditReconnectNeutralGate(page);
await auditHeldInputAcrossFocusRecovery(page);
await auditAnalogHysteresis(page);
await auditAnalogDominantAxis(page);
await auditConfirmFallback(page);
await auditSelectConfirm(page);
await auditMultiSelectBack(page);
await auditFavoriteAction(page);
await auditEffectiveCustomRecipesDisclosure(page);
await auditFocusMode(page);
await auditCompoundControls(page);
await auditNumberInput(page);
await auditLayeredBackNavigation(page);
await auditExplicitScrollRegion(page);
await auditDialogBackAndReturnFocus(page);
await auditIneligibleFocusFiltering(page);
await captureScreenshot(page, 'final');
await auditResponsiveProfiles(browser);

await browser.close();

const report = buildReport();
await writeFile(path.join(OUTPUT_DIR, 'report.md'), report);
console.log(report);
console.log(`\nGamepad audit report written to ${OUTPUT_DIR}`);

if (issues.length > 0 || pageErrors.length > 0 || consoleErrors.length > 0) {
  process.exitCode = 1;
}

function seedLocalStorage({ apiUrl, apiToken, storagePrefix, fontScalePercent = 100 }) {
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
  localStorage.setItem(`${storagePrefix}-font-scale-percent`, String(fontScalePercent));
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
    axes(values) {
      for (let index = 0; index < axes.length; index += 1) {
        axes[index] = Number.isFinite(values[index]) ? values[index] : 0;
      }
      gamepad.timestamp = timestamp += 1;
    },
    button(index, pressed, value = pressed ? 1 : 0) {
      buttons[index] = {
        pressed,
        touched: pressed || value > 0,
        value,
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

async function auditReconnectNeutralGate(page) {
  await activateTopTab(page, 'overview');

  await page.evaluate(() => window.__mockGamepad.connected(false));
  await page.waitForTimeout(80);
  await setButton(page, BUTTON_RB, true);
  await page.evaluate(() => window.__mockGamepad.connected(true));
  await page.waitForTimeout(180);
  await expectTopTab(page, 'overview', '手柄按住 RB 重连时，中立门控不应切换页签');

  await setButton(page, BUTTON_RB, false);
  await waitForNeutralGate(page);
  await pressButton(page, BUTTON_RB);
  await expectTopTab(page, 'normal', '重连后释放至中立再按 RB 应切换到“普客”');
}

async function auditHeldInputAcrossFocusRecovery(page) {
  await activateTopTab(page, 'overview');

  await setButton(page, BUTTON_RB, true);
  await page.waitForTimeout(90);
  await expectTopTab(page, 'normal', 'RB 按下后应先切到“普客”');

  await page.evaluate(() => window.__mockGamepad.focused(false));
  await page.waitForTimeout(80);
  await page.evaluate(() => window.__mockGamepad.focused(true));
  await page.waitForTimeout(180);
  await expectTopTab(page, 'normal', '恢复焦点时仍按住 RB 不应重复切换页签');

  await setButton(page, BUTTON_RB, false);
  await waitForNeutralGate(page);
  await pressButton(page, BUTTON_RB);
  await expectTopTab(page, 'rare', '焦点恢复后释放至中立再按 RB 应继续切到“稀客”');
}

async function auditAnalogHysteresis(page) {
  await activateTopTab(page, 'overview');
  await page.locator('[data-gamepad-tab-value="overview"]').first().focus();

  await setAxes(page, [0.66, 0, 0, 0]);
  await page.waitForTimeout(90);
  await expectFocusedTopTab(page, 'normal', '左摇杆超过按下阈值后应向右聚焦“普客”');

  await setAxes(page, [0.5, 0, 0, 0]);
  await page.waitForTimeout(100);
  await expectFocusedTopTab(page, 'normal', '左摇杆回落但未低于释放阈值时不应重复移动');

  await setAxes(page, [0.39, 0, 0, 0]);
  await page.waitForTimeout(70);
  await setAxes(page, [0.55, 0, 0, 0]);
  await page.waitForTimeout(100);
  await expectFocusedTopTab(page, 'normal', '摇杆释放后未再次超过按下阈值时不应移动');

  await setAxes(page, [0.66, 0, 0, 0]);
  await page.waitForTimeout(90);
  await expectFocusedTopTab(page, 'rare', '摇杆再次超过按下阈值后应向右聚焦“稀客”');
  await releaseAxes(page);
}

async function auditAnalogDominantAxis(page) {
  await activateTopTab(page, 'overview');
  await page.locator('[data-gamepad-tab-value="overview"]').first().focus();

  await setAxes(page, [0.9, 0.74, 0, 0]);
  await page.waitForTimeout(90);
  await expectFocusedTopTab(page, 'normal', '斜向输入应只采用幅度更大的横轴并聚焦“普客”');
  await releaseAxes(page);

  await page.locator('[data-gamepad-tab-value="overview"]').first().focus();
  await setAxes(page, [0.74, 0.9, 0, 0]);
  await page.waitForTimeout(90);
  const focusState = await page.evaluate(() => {
    const active = document.activeElement;
    const overviewTab = document.querySelector('[data-gamepad-tab-value="overview"]');
    const panelId = overviewTab?.getAttribute('aria-controls');
    const overviewPanel = panelId ? document.getElementById(panelId) : null;
    return {
      focusedTopTab: active instanceof HTMLElement ? active.dataset.gamepadTabValue || '' : '',
      insideVisiblePanel: active instanceof HTMLElement && overviewPanel instanceof HTMLElement
        ? overviewPanel.contains(active)
        : false,
    };
  });
  if (focusState.focusedTopTab === 'normal' || !focusState.insideVisiblePanel) {
    issues.push('纵轴占优的斜向输入应进入当前页签内容，且不能同时向右移动页签。');
  }
  await releaseAxes(page);
}

async function auditConfirmFallback(page) {
  await activateTopTab(page, 'overview');
  await page.locator('[data-gamepad-tab-value="overview"]').first().focus();

  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  const focusedTab = await readFocusedTopTab(page);
  if (focusedTab !== 'normal') {
    const focused = await readFocusedSummary(page);
    issues.push(`方向键右移后焦点应落到 normal Tab，实际为 ${focused?.text || focusedTab || '空'}。`);
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
    return;
  }

  const optionState = await page.locator('[role="option"]:visible').evaluateAll((options) => ({
    count: options.length,
    selectedIndex: options.findIndex((option) => option.getAttribute('aria-selected') === 'true'),
  }));
  const valueBefore = await select.inputValue();
  if (optionState.count < 2) {
    issues.push('Select 展开后不足两个可见选项，无法验证方向键选择。');
  } else {
    const direction = optionState.selectedIndex === optionState.count - 1
      ? BUTTON_DPAD_UP
      : BUTTON_DPAD_DOWN;
    await pressButton(page, direction, { holdMs: 70 });
    await pressButton(page, BUTTON_A, { holdMs: 70 });
    await page.waitForTimeout(160);
    const valueAfter = await select.inputValue();
    if (valueAfter === valueBefore) {
      issues.push('Select 展开后使用方向键和 A 未选择相邻选项。');
    }
    if (await isComboboxExpanded(page)) {
      issues.push('Select 选择选项后下拉框未关闭。');
    }
  }

  await select.focus();
  await pressButton(page, BUTTON_A, { holdMs: 70 });
  await page.waitForTimeout(120);
  if (!(await isComboboxExpanded(page))) {
    issues.push('Select 再次按 A 未展开，无法验证 B 关闭。');
    return;
  }
  await pressButton(page, BUTTON_B, { holdMs: 70 });
  if (await isComboboxExpanded(page)) {
    issues.push('Select 展开后按 B 未关闭下拉框。');
  }
  await expectFocusedElement(page, select, 'Select 按 B 关闭后应保留输入焦点');
}

async function auditMultiSelectBack(page) {
  await activateTopTab(page, 'settings');
  await activateInnerTab(page, '推荐');
  const input = page.locator('input[data-slot="multi-select"]:visible, [data-gamepad-control="multi-select"] input:visible').first();
  if (!(await input.count()) || !(await input.isVisible())) {
    issues.push('未找到可见 MultiSelect 输入，无法验证 A/B 开关下拉框。');
    return;
  }

  await input.scrollIntoViewIfNeeded();
  await input.focus();
  await pressButton(page, BUTTON_A, { holdMs: 70 });
  await page.waitForTimeout(120);
  if (!(await isComboboxExpanded(page))) {
    issues.push('MultiSelect 聚焦后按 A 未展开下拉框。');
    return;
  }
  await pressButton(page, BUTTON_B, { holdMs: 70 });
  if (await isComboboxExpanded(page)) {
    issues.push('MultiSelect 展开后按 B 未关闭下拉框。');
  }
  await expectFocusedElement(page, input, 'MultiSelect 按 B 关闭后应保留输入焦点');
}

async function auditFavoriteAction(page) {
  await activateTopTab(page, 'rare');
  const row = page.locator('[data-gamepad-favorite-scope="true"]:visible').first();
  const favorite = row.locator('[data-gamepad-favorite="true"]:visible').first();
  if (!(await row.count()) || !(await favorite.count())) {
    issues.push('稀客推荐中未找到可收藏条目，无法验证 X 键收藏。');
    return;
  }

  const anchor = await row.evaluate((element) => ({
    rowKey: element.dataset.gamepadRowKey || '',
    focusKey: element.querySelector('[data-gamepad-favorite="true"]')?.getAttribute('data-gamepad-focus-key') || '',
  }));
  await favorite.evaluate((element) => {
    window.__gamepadFavoriteClickCount = 0;
    element.addEventListener('click', () => {
      window.__gamepadFavoriteClickCount += 1;
    }, { once: true });
  });
  await row.scrollIntoViewIfNeeded();
  await row.focus();
  await pressButton(page, BUTTON_X);

  const clickCount = await page.evaluate(() => window.__gamepadFavoriteClickCount || 0);
  if (clickCount !== 1) {
    issues.push(`推荐条目聚焦后按 X 应触发一次收藏，实际触发 ${clickCount} 次。`);
    return;
  }

  try {
    await page.waitForFunction(({ rowKey, focusKey }) => {
      const active = document.activeElement;
      if (!(active instanceof HTMLElement)) return false;
      return active.dataset.gamepadFocusKey === focusKey
        || active.closest('[data-gamepad-row="true"]')?.getAttribute('data-gamepad-row-key') === rowKey;
    }, anchor, { timeout: 1200 });
  } catch {
    const focused = await readFocusedSummary(page);
    issues.push(`X 收藏完成后未恢复到原推荐条目，实际焦点为 ${focused?.text || '空'}。`);
  }
}

async function auditEffectiveCustomRecipesDisclosure(page) {
  await activateTopTab(page, 'rare');
  const trigger = page.locator('[data-effective-custom-recipes-trigger="true"]:visible').first();
  if (!(await trigger.count())) {
    issues.push('稀客推荐中未找到生效自定义配方按钮，无法验证移动后的手柄路径。');
    return;
  }

  await trigger.scrollIntoViewIfNeeded();
  await trigger.focus();
  await pressButton(page, BUTTON_A, { holdMs: 70 });
  const details = page.locator('[data-effective-custom-recipes-details="true"]:visible').first();
  try {
    await details.waitFor({ state: 'visible', timeout: 1200 });
  } catch {
    issues.push('生效自定义配方按钮按 A 后未展开详情。');
    return;
  }

  if (await trigger.getAttribute('aria-expanded') !== 'true') {
    issues.push('生效自定义配方详情展开后按钮未同步 aria-expanded。');
  }

  try {
    await page.waitForFunction(() => (
      document.activeElement instanceof HTMLElement
      && (document.activeElement.dataset.gamepadScrollKey || '').includes(':custom-recipes')
    ), null, { timeout: 1200 });
  } catch {
    const focused = await readFocusedSummary(page);
    issues.push(`生效配方按钮按 A 展开后未聚焦配方滚动区，实际焦点为 ${focused?.text || '空'}。`);
  }

  await pressButton(page, BUTTON_DPAD_UP, { holdMs: 70 });
  await expectFocusedElement(page, trigger, '生效配方滚动区按上键应回到标题行触发按钮');
  await pressButton(page, BUTTON_A, { holdMs: 70 });
  await page.waitForTimeout(120);
  if (await page.locator('[data-effective-custom-recipes-details="true"]:visible').count()) {
    issues.push('生效自定义配方按钮再次按 A 后未收起详情。');
  }
  if (await trigger.getAttribute('aria-expanded') !== 'false') {
    issues.push('生效自定义配方详情收起后按钮未同步 aria-expanded。');
  }
}

async function auditFocusMode(page) {
  await activateTopTab(page, 'service');
  await pressButton(page, BUTTON_Y, { holdMs: 70 });
  await page.waitForTimeout(220);

  const focusPage = page.locator('[data-service-focus-page="true"]:visible').first();
  const entered = await focusPage.count();
  if (!entered) {
    issues.push('Y 键未进入稀客订单专注模式。');
    return;
  }

  if (await focusPage.getAttribute('data-gamepad-scope') !== 'content') {
    issues.push('稀客订单专注模式缺少可见的 content 手柄导航 scope。');
  }

  const exit = page.locator('[data-gamepad-focus-key="service-focus:exit"]:visible').first();
  if (!(await exit.count())) {
    issues.push('稀客订单专注模式缺少可由手柄确认的退出按钮。');
    return;
  }
  await exit.focus();
  await pressButton(page, BUTTON_A, { holdMs: 70 });
  await page.waitForTimeout(220);
  if (await page.locator('[data-service-focus-page="true"]:visible').count()) {
    issues.push('退出专注模式按钮按 A 后仍停留在专注模式。');
    return;
  }
  await expectFocusedTopTab(page, 'service', '按 A 退出专注模式后应回到“经营中”Tab');

  await pressButton(page, BUTTON_Y, { holdMs: 70 });
  await page.waitForTimeout(220);
  if (!(await page.locator('[data-service-focus-page="true"]:visible').count())) {
    issues.push('按 A 退出后再次按 Y 未能重新进入稀客订单专注模式。');
    return;
  }

  const compactSwitch = page.locator('label:has-text("精简模式") input[type="checkbox"]:visible').first();
  if (!(await compactSwitch.count())) {
    issues.push('专注模式中未找到“精简模式”开关，无法验证 Y 键切换。');
  } else {
    const compactBefore = await compactSwitch.isChecked();
    await pressButton(page, BUTTON_Y, { holdMs: 70 });
    const compactAfter = await compactSwitch.isChecked();
    if (compactAfter === compactBefore) {
      issues.push('专注模式中按 Y 未切换精简模式。');
    }
  }

  await pressButton(page, BUTTON_B, { holdMs: 70 });
  await page.waitForTimeout(220);
  const stillFocused = await page.locator('[data-service-focus-page="true"]:visible').count();
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

async function auditNumberInput(page) {
  await activateTopTab(page, 'settings');
  await activateInnerTab(page, '推荐');

  const input = page.locator([
    '[data-gamepad-control="number-input"] input:visible',
    'input[data-gamepad-control="number-input"]:visible',
  ].join(',')).first();
  if (!(await input.count()) || !(await input.isVisible())) {
    issues.push('设置推荐页未找到可见 NumberInput，无法验证左右调值。');
    return;
  }

  await input.scrollIntoViewIfNeeded();
  await input.focus();
  const before = Number(await input.inputValue());
  await pressButton(page, BUTTON_DPAD_RIGHT);
  const afterRight = Number(await input.inputValue());
  if (afterRight <= before) {
    issues.push(`NumberInput 按右键应递增，实际从 ${before} 变为 ${afterRight}。`);
  }
  await expectFocusedElement(page, input, 'NumberInput 调值后焦点应保持在输入框，不能落到内部步进按钮');

  await pressButton(page, BUTTON_DPAD_LEFT);
  const afterLeft = Number(await input.inputValue());
  if (afterLeft >= afterRight) {
    issues.push(`NumberInput 按左键应递减，实际从 ${afterRight} 变为 ${afterLeft}。`);
  }
  await expectFocusedElement(page, input, 'NumberInput 反向调值后焦点应保持在输入框');
}

async function auditLayeredBackNavigation(page) {
  await activateTopTab(page, 'settings');
  await activateInnerTab(page, '推荐');
  const input = page.locator([
    '[data-gamepad-control="number-input"] input:visible',
    'input[data-gamepad-control="number-input"]:visible',
  ].join(',')).first();
  if (!(await input.count())) {
    issues.push('设置推荐页未找到内容控件，无法验证 B 键分层返回。');
    return;
  }

  await input.focus();
  await pressButton(page, BUTTON_B);
  await expectFocusedInnerTab(page, '推荐', '内容控件按 B 应先返回当前二级 Tab');
  await pressButton(page, BUTTON_B);
  await expectFocusedTopTab(page, 'settings', '二级 Tab 再按 B 应返回“设置”顶部 Tab');
}

async function auditExplicitScrollRegion(page) {
  await activateTopTab(page, 'help');
  const region = page.locator('[data-gamepad-scroll-key="help:navigation"]:visible').first();
  if (!(await region.count())) {
    issues.push('帮助页未找到显式手柄滚动区。');
    return;
  }

  const metrics = await region.evaluate((element) => ({
    clientHeight: element.clientHeight,
    scrollHeight: element.scrollHeight,
  }));
  if (metrics.scrollHeight <= metrics.clientHeight) {
    issues.push('帮助目录滚动区在巡检视口下不可滚动，无法验证显式滚动行为。');
    return;
  }

  await region.evaluate((element) => { element.scrollTop = 0; });
  await region.focus();
  await pressButton(page, BUTTON_DPAD_DOWN);
  const afterDpad = await region.evaluate((element) => element.scrollTop);
  if (afterDpad <= 0) {
    issues.push('显式滚动区聚焦后按方向键下未向下滚动。');
  }

  const beforeHorizontal = await readScrollState(page, region);
  await pressButton(page, BUTTON_DPAD_RIGHT);
  const afterHorizontal = await readScrollState(page, region);
  if (afterHorizontal.regionTop !== beforeHorizontal.regionTop) {
    issues.push('显式滚动区按方向键右发生了错误的纵向滚动。');
  }
  if (afterHorizontal.documentTop !== beforeHorizontal.documentTop) {
    issues.push('显式滚动区按方向键右错误地滚动了页面。');
  }

  await region.focus();
  const beforeLeft = await readScrollState(page, region);
  await pressButton(page, BUTTON_DPAD_LEFT);
  const afterLeft = await readScrollState(page, region);
  if (afterLeft.regionTop !== beforeLeft.regionTop || afterLeft.documentTop !== beforeLeft.documentTop) {
    issues.push('显式滚动区按方向键左发生了错误的纵向滚动。');
  }

  await region.focus();
  const beforeTrigger = await region.evaluate((element) => element.scrollTop);
  await pressButton(page, BUTTON_RT);
  const afterTrigger = await region.evaluate((element) => element.scrollTop);
  if (afterTrigger <= beforeTrigger) {
    issues.push('显式滚动区聚焦后按 RT 未向下滚动。');
  }

  await pressButton(page, BUTTON_LT);
  const afterReverseTrigger = await region.evaluate((element) => element.scrollTop);
  if (afterReverseTrigger >= afterTrigger) {
    issues.push('显式滚动区聚焦后按 LT 未向上滚动。');
  }
}

async function auditDialogBackAndReturnFocus(page) {
  await activateTopTab(page, 'settings');
  await activateInnerTab(page, '连接');

  const trigger = page.locator('[data-gamepad-focus-key="settings:connection:reset-token"]:visible').first();
  if (!(await trigger.count()) || await trigger.isDisabled()) {
    issues.push('连接设置页的“重置 Token”按钮不可用，无法验证 Dialog 手柄生命周期。');
    return;
  }

  await trigger.scrollIntoViewIfNeeded();
  await trigger.focus();
  await pressButton(page, BUTTON_A);
  const dialog = page.locator([
    '[role="dialog"]:has([data-gamepad-scope="modal"]):visible',
    '[data-gamepad-scope="modal"] [role="dialog"]:visible',
    '[role="dialog"][data-gamepad-scope="modal"]:visible',
  ].join(',')).first();
  try {
    await dialog.waitFor({ state: 'visible', timeout: 1200 });
  } catch {
    const diagnostic = await readGamepadDiagnostic(page);
    issues.push(`“重置 Token”按钮按 A 后未打开 Dialog（${diagnostic}）。`);
    return;
  }

  const dialogFocusKey = await page.evaluate(() => (
    document.activeElement instanceof HTMLElement
      ? document.activeElement.dataset.gamepadFocusKey || ''
      : ''
  ));
  if (dialogFocusKey !== 'settings:connection:reset-token:cancel') {
    issues.push(`Dialog 打开后应聚焦默认“取消”按钮，实际焦点为 ${dialogFocusKey || '空'}。`);
  }

  await pressButton(page, BUTTON_B);
  try {
    await dialog.waitFor({ state: 'hidden', timeout: 1200 });
  } catch {
    issues.push('Dialog 中按 B 未关闭弹窗。');
    await closeResidualDialog(page);
  }
  await page.waitForTimeout(80);
  const returnedFocusKey = await page.evaluate(() => (
    document.activeElement instanceof HTMLElement
      ? document.activeElement.dataset.gamepadFocusKey || ''
      : ''
  ));
  if (returnedFocusKey !== 'settings:connection:reset-token') {
    issues.push(`Dialog 按 B 关闭后应回焦触发按钮，实际焦点为 ${returnedFocusKey || '空'}。`);
  }

  await trigger.focus();
  await pressButton(page, BUTTON_A);
  try {
    await dialog.waitFor({ state: 'visible', timeout: 1200 });
  } catch {
    issues.push('无法再次打开 Dialog，未执行手柄与鼠标混合输入巡检。');
    return;
  }
  const cancel = page.locator('[data-gamepad-dialog-default="true"]:visible').first();
  if (!(await cancel.count())) {
    issues.push('Dialog 缺少默认取消按钮，未执行手柄与鼠标混合输入巡检。');
    return;
  }
  await cancel.click();
  await dialog.waitFor({ state: 'hidden', timeout: 1200 }).catch(() => {});

  const settingsTab = page.locator('[data-gamepad-tab-value="settings"]').first();
  const expectedPreviousTab = await page.evaluate(() => {
    const tabs = Array.from(document.querySelectorAll('[data-gamepad-tab-value]'))
      .filter((element) => element instanceof HTMLElement && element.getBoundingClientRect().width > 0);
    const settingsIndex = tabs.findIndex((element) => element.getAttribute('data-gamepad-tab-value') === 'settings');
    const previous = tabs[Math.max(0, settingsIndex - 1)];
    return previous instanceof HTMLElement ? previous.dataset.gamepadTabValue || '' : '';
  });
  await settingsTab.focus();
  await pressButton(page, BUTTON_DPAD_LEFT);
  await expectFocusedTopTab(page, expectedPreviousTab, '鼠标关闭 Dialog 后重新使用手柄应从当前 Tab 正常导航');
  await page.evaluate(() => {
    const marker = document.createElement('span');
    marker.dataset.gamepadTestMutation = 'mixed-input';
    document.body.append(marker);
    marker.remove();
  });
  await page.waitForTimeout(80);
  await expectFocusedTopTab(page, expectedPreviousTab, '鼠标关闭 Dialog 后的 DOM 变化不应跳回旧触发按钮');
}

async function auditIneligibleFocusFiltering(page) {
  await activateTopTab(page, 'settings');
  await page.evaluate(() => {
    const panel = Array.from(document.querySelectorAll('[data-slot="tabs-content"]'))
      .find((element) => element instanceof HTMLElement
        && element.getBoundingClientRect().width > 0
        && element.getBoundingClientRect().height > 0);
    if (!(panel instanceof HTMLElement)) return;

    const fixture = document.createElement('div');
    fixture.dataset.gamepadAxis = 'x';
    fixture.dataset.gamepadTestFixture = 'focus-filter';
    fixture.style.display = 'flex';
    fixture.style.gap = '8px';
    fixture.innerHTML = [
      '<button type="button" data-gamepad-test="start">start</button>',
      '<button type="button" data-gamepad-test="disabled" disabled>disabled</button>',
      '<span aria-hidden="true"><button type="button" data-gamepad-test="hidden">hidden</button></span>',
      '<button type="button" data-gamepad-test="target">target</button>',
    ].join('');
    panel.prepend(fixture);
  });

  const fixture = page.locator('[data-gamepad-test-fixture="focus-filter"]');
  const start = fixture.locator('[data-gamepad-test="start"]');
  const target = fixture.locator('[data-gamepad-test="target"]');
  if (!(await start.count())) {
    issues.push('无法建立禁用/隐藏控件焦点过滤巡检夹具。');
    return;
  }

  await start.focus();
  await pressButton(page, BUTTON_DPAD_RIGHT);
  await expectFocusedElement(page, target, '横向导航应跳过 disabled 和 aria-hidden 内部控件');
  const invalidFocus = await page.evaluate(() => {
    const active = document.activeElement;
    return active instanceof HTMLElement && (
      active.matches(':disabled,[aria-disabled="true"],[data-disabled="true"]')
      || Boolean(active.closest('[aria-hidden="true"],[hidden],[inert]'))
    );
  });
  if (invalidFocus) {
    issues.push('手柄导航焦点落在了禁用或隐藏控件。');
  }
  await fixture.evaluate((element) => element.remove());
}

async function auditInnerTabs(page) {
  await activateTopTab(page, 'settings');
  await activateInnerTab(page, '窗口');
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
  const serviceTabBefore = await readActiveElementDebug(page);
  runtimeDiagnostics.push(`经营中二级 Tab 右移前：${formatElementDebug(serviceTabBefore)}`);
  await pressButton(page, BUTTON_DPAD_RIGHT, { holdMs: 70 });
  const serviceTabAfter = await readActiveElementDebug(page);
  runtimeDiagnostics.push(`经营中二级 Tab 右移后：${formatElementDebug(serviceTabAfter)}`);
  if (!serviceTabAfter.innerTab || serviceTabAfter.text !== '普客') {
    issues.push([
      '经营中页“稀客”按右键应聚焦“普客”二级 Tab。',
      `操作前 ${formatElementDebug(serviceTabBefore)}；`,
      `操作后 ${formatElementDebug(serviceTabAfter)}。`,
    ].join(''));
  }
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
  await closeResidualDialog(page);
  const trigger = page.locator(`[data-gamepad-tab-value="${value}"]`).first();
  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
  await page.waitForTimeout(180);
  await expectTopTab(page, value, `应能切换到 ${value} Tab`);
}

async function closeResidualDialog(page) {
  const overlay = page.locator('.mantine-Modal-overlay:visible');
  if (!(await overlay.count())) return;

  const cancel = page.locator('[data-gamepad-dialog-default="true"]:visible').first();
  if (await cancel.count()) {
    await cancel.click({ force: true });
  } else {
    await page.keyboard.press('Escape');
  }
  await page.waitForFunction(() => !Array.from(document.querySelectorAll('.mantine-Modal-overlay')).some((element) => {
    if (!(element instanceof HTMLElement)) return false;
    const rect = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
  }), null, { timeout: 2000 }).catch(() => {});
}

async function auditResponsiveProfiles(browser) {
  const profiles = [
    { name: 'minimum-130', viewport: { width: 640, height: 520 }, fontScalePercent: 130 },
    { name: 'mobile-90', viewport: { width: 390, height: 844 }, fontScalePercent: 90 },
  ];

  for (const profile of profiles) {
    const profilePage = await browser.newPage({ viewport: profile.viewport });
    profilePage.on('pageerror', (error) => pageErrors.push(`[${profile.name}] ${error.stack || error.message}`));
    profilePage.on('console', (message) => {
      if (message.type() === 'error') consoleErrors.push(`[${profile.name}] ${message.text()}`);
    });
    await profilePage.addInitScript(seedLocalStorage, {
      apiUrl: API_URL,
      apiToken: API_TOKEN,
      storagePrefix: STORAGE_PREFIX,
      fontScalePercent: profile.fontScalePercent,
    });
    await profilePage.addInitScript(installMockGamepad);
    await profilePage.goto(APP_URL, { waitUntil: 'domcontentloaded' });
    await profilePage.waitForSelector('[data-gamepad-tab-value="overview"]', { timeout: 10000 });
    await profilePage.waitForFunction(() => Boolean(navigator.getGamepads?.()[0]?.connected), null, { timeout: 5000 });
    await waitForNeutralGate(profilePage);

    const appliedScale = await profilePage.evaluate(() => (
      Number.parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--companion-font-scale'))
    ));
    if (Math.abs(appliedScale - profile.fontScalePercent / 100) > 0.001) {
      issues.push(`[${profile.name}] 字号比例应为 ${profile.fontScalePercent}%，实际为 ${appliedScale || '无'}。`);
    }

    if (profile.name === 'minimum-130') {
      const primaryTabsLayout = await inspectMinimumPrimaryTabsLayout(profilePage);
      if (!primaryTabsLayout.ok) {
        issues.push(`[${profile.name}] 一级导航未完整显示：${JSON.stringify(primaryTabsLayout)}。`);
      }

      await profilePage.locator('[data-gamepad-tab-value="service"]').first().focus();
      await pressButton(profilePage, BUTTON_DPAD_RIGHT);
      await expectFocusedTopTab(profilePage, 'tasks', `[${profile.name}] 一级导航跨行焦点移动失败`);
      const focusedTabContained = await profilePage.evaluate(() => {
        const active = document.activeElement;
        const list = active?.closest('.steward-primary-tabs-list');
        if (!(active instanceof HTMLElement) || !(list instanceof HTMLElement)) return false;
        const activeRect = active.getBoundingClientRect();
        const listRect = list.getBoundingClientRect();
        return activeRect.left >= listRect.left - 1
          && activeRect.right <= listRect.right + 1
          && activeRect.top >= listRect.top - 1
          && activeRect.bottom <= listRect.bottom + 1;
      });
      if (!focusedTabContained) {
        issues.push(`[${profile.name}] 一级导航跨行后的焦点页签不在导航容器内。`);
      }
      await pressButton(profilePage, BUTTON_A);
      await expectTopTab(profilePage, 'tasks', `[${profile.name}] A 键未激活跨行后的顶部 Tab`);
    }

    await profilePage.locator('[data-gamepad-tab-value="overview"]').first().focus();
    await pressButton(profilePage, BUTTON_DPAD_RIGHT);
    await expectFocusedTopTab(profilePage, 'normal', `[${profile.name}] 顶部 Tab 方向导航失败`);
    await pressButton(profilePage, BUTTON_A);
    await expectTopTab(profilePage, 'normal', `[${profile.name}] A 键未激活聚焦的顶部 Tab`);
    await pressButton(profilePage, BUTTON_DPAD_DOWN);
    const contentFocus = await profilePage.evaluate(() => {
      const active = document.activeElement;
      if (!(active instanceof HTMLElement)) return { inside: false, visible: false };
      const rect = active.getBoundingClientRect();
      return {
        inside: !active.matches('[data-gamepad-tab="true"]'),
        visible: rect.width > 0
          && rect.height > 0
          && rect.bottom > 0
          && rect.right > 0
          && rect.top < window.innerHeight
          && rect.left < window.innerWidth,
      };
    });
    if (!contentFocus.inside || !contentFocus.visible) {
      issues.push(`[${profile.name}] 顶部 Tab 按下后未进入视口内的页面控件。`);
    }

    const overflow = await profilePage.evaluate(() => ({
      clientWidth: document.documentElement.clientWidth,
      scrollWidth: document.documentElement.scrollWidth,
    }));
    if (overflow.scrollWidth > overflow.clientWidth + 1) {
      issues.push(`[${profile.name}] 页面产生横向溢出：${overflow.scrollWidth}px > ${overflow.clientWidth}px。`);
    }

    await captureScreenshot(profilePage, profile.name);
    await profilePage.close();
  }
}

async function activateInnerTab(page, label) {
  const trigger = page.locator('[data-slot="tabs-trigger"]:not([data-gamepad-tab])')
    .filter({ hasText: label })
    .first();
  if (!(await trigger.count())) {
    issues.push(`未找到“${label}”二级 Tab。`);
    return;
  }
  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
  await page.waitForTimeout(160);
}

async function pressButton(page, index, { holdMs = 70 } = {}) {
  const frameCount = Math.max(2, Math.min(4, Math.ceil(holdMs / 30)));
  await setButton(page, index, true);
  await waitForAnimationFrames(page, frameCount);
  await setButton(page, index, false);
  await waitForAnimationFrames(page, 3);
}

async function setButton(page, index, pressed) {
  await page.evaluate(
    ({ buttonIndex, buttonPressed }) => window.__mockGamepad.button(buttonIndex, buttonPressed),
    { buttonIndex: index, buttonPressed: pressed },
  );
}

async function setAxes(page, values) {
  await page.evaluate((nextValues) => window.__mockGamepad.axes(nextValues), values);
}

async function releaseAxes(page) {
  await setAxes(page, [0, 0, 0, 0]);
  await page.waitForTimeout(100);
}

async function waitForNeutralGate(page) {
  await waitForAnimationFrames(page, 5);
}

async function waitForAnimationFrames(page, count) {
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

async function expectFocusedTopTab(page, value, message) {
  const focused = await readFocusedTopTab(page);
  if (focused !== value) {
    const diagnostic = await readGamepadDiagnostic(page);
    const summary = await readFocusedSummary(page);
    issues.push(`${message}，实际焦点为 ${summary?.text || focused || '空'}（${diagnostic}）。`);
  }
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

async function readActiveElementDebug(page) {
  return page.evaluate(() => {
    const element = document.activeElement;
    if (!(element instanceof HTMLElement)) {
      return {
        tag: '',
        slot: '',
        control: '',
        tabIndex: null,
        innerTab: false,
        text: '',
        closestTabsList: '',
      };
    }
    const tabsList = element.closest('[data-slot="tabs-list"]');
    return {
      tag: element.tagName.toLowerCase(),
      slot: element.dataset.slot || '',
      control: element.dataset.gamepadControl || '',
      tabIndex: element.tabIndex,
      innerTab: element.matches('[data-slot="tabs-trigger"]:not([data-gamepad-tab])'),
      text: element.textContent?.trim().replace(/\s+/g, ' ')
        || element.getAttribute('aria-label')
        || element.getAttribute('placeholder')
        || '',
      closestTabsList: tabsList instanceof HTMLElement
        ? `${tabsList.tagName.toLowerCase()}[data-slot=${tabsList.dataset.slot || ''}][role=${tabsList.getAttribute('role') || ''}]`
        : '',
    };
  });
}

function formatElementDebug(debug) {
  return [
    `tag=${debug.tag || 'none'}`,
    `slot=${debug.slot || 'none'}`,
    `control=${debug.control || 'none'}`,
    `tabIndex=${debug.tabIndex ?? 'none'}`,
    `innerTab=${debug.innerTab}`,
    `tabsList=${debug.closestTabsList || 'none'}`,
    `text=${debug.text || 'none'}`,
  ].join(', ');
}

async function readGamepadDiagnostic(page) {
  return page.evaluate(() => [
    `status=${document.body.dataset.gamepadStatus || 'unknown'}`,
    `last=${document.body.dataset.gamepadLastAction || 'none'}`,
    `neutral=${document.body.dataset.gamepadNeutralReason || 'none'}`,
  ].join(', '));
}

async function expectFocusedElement(page, locator, message) {
  const focused = await locator.evaluate((element) => document.activeElement === element);
  if (!focused) {
    const summary = await readFocusedSummary(page);
    issues.push(`${message}，实际焦点为 ${summary?.text || '空'}。`);
  }
}

async function readScrollState(page, region) {
  const regionTop = await region.evaluate((element) => element.scrollTop);
  const documentTop = await page.evaluate(() => (
    document.scrollingElement?.scrollTop ?? document.documentElement.scrollTop
  ));
  return { regionTop, documentTop };
}

async function isComboboxExpanded(page) {
  return page.evaluate(() => {
    const selectors = [
      '[data-slot="select"][data-expanded="true"]',
      '[data-slot="multi-select"][data-expanded="true"]',
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

  lines.push('', '## Runtime diagnostics', '');
  if (runtimeDiagnostics.length === 0) {
    lines.push('- 无。');
  } else {
    for (const diagnostic of runtimeDiagnostics) lines.push(`- ${diagnostic}`);
  }

  lines.push('', '## Browser errors', '');
  if (pageErrors.length === 0 && consoleErrors.length === 0) {
    lines.push('- 未捕获 pageerror 或 console.error。');
  } else {
    for (const error of pageErrors) lines.push(`- pageerror: ${error}`);
    for (const error of consoleErrors) lines.push(`- console.error: ${error}`);
  }

  return `${lines.join('\n')}\n`;
}
