import assert from 'node:assert/strict';
import { mkdir, rm } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

const appUrl = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173';
const apiUrl = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const apiToken = process.env.MYSTIA_API_TOKEN || 'mock-token';
const outputDir = process.env.SETTINGS_HELP_AUDIT_OUTPUT_DIR
  || '/tmp/mystia-companion-settings-help-audit';

const selectors = {
  field: '[data-setting-field]',
  trigger: '[data-setting-help-trigger]',
  tooltip: '[data-setting-help-tooltip]',
  describedControl: [
    'input[data-slot="switch"]',
    '[role="slider"]',
    '[role="radiogroup"]',
    'input[data-slot="number-input"]',
    'input[data-slot="input"]',
    'input[data-slot="multi-select"]',
  ].join(','),
};

const expectedHelpIdsBySection = new Map([
  ['窗口', [
    'window-background-opacity',
    'window-content-opacity',
    'window-focus-switch-behavior',
    'window-focus-switch-cooldown',
    'window-always-on-top',
    'window-mouse-passthrough',
    'window-theme',
    'window-font-scale',
    'window-gamepad-navigation',
    'window-debug-details',
  ]],
  ['连接', [
    'connection-lan-enabled',
    'connection-lan-bind-host',
  ]],
  ['推荐', [
    'recommendation-service-order-sort',
    'recommendation-focus-compact',
    'recommendation-budget-policy',
    'recommendation-filter-missing-cookers',
    'recommendation-mission-recipe-priority',
    'recommendation-pin-favorite-recipe',
    'recommendation-pin-favorite-beverage',
    'recommendation-recipe-variant-limit',
    'recommendation-excluded-ingredients',
    'recommendation-excluded-beverages',
    'recommendation-weight-preset',
    'recommendation-weight-foodPreference',
    'recommendation-weight-beveragePreference',
    'recommendation-weight-negativeRisk',
    'recommendation-weight-extraCount',
    'recommendation-weight-resourcePressure',
    'recommendation-weight-totalCost',
    'recommendation-weight-profit',
    'recommendation-weight-beverageStock',
    'recommendation-weight-cookerAvailable',
  ]],
  ['实验性功能', [
    'recommendation-rare-game-ui-pinning',
    'recommendation-rare-recipe-variant',
    'recommendation-rare-cooker-highlight',
    'recommendation-rare-seat-highlight',
    'recommendation-rare-order-highlight',
    'recommendation-rare-highlight-color',
    'recommendation-normal-game-ui-pinning',
    'recommendation-normal-recipe-variant',
    'recommendation-normal-cooker-highlight',
    'recommendation-normal-seat-highlight',
    'recommendation-normal-order-highlight',
    'recommendation-normal-highlight-color',
    'automation-enabled',
    'automation-rare-concurrency',
    'automation-normal-concurrency',
    'automation-max-step-retries',
    'automation-max-rollbacks',
    'automation-rare-enabled',
    'automation-rare-take-beverage',
    'automation-rare-start-cooking',
    'automation-rare-deliver-food',
    'automation-rare-complete-order',
    'automation-rare-stop-on-error',
    'automation-rare-recipe-favorites-only',
    'automation-rare-beverage-favorites-only',
    'automation-normal-enabled',
    'automation-normal-take-beverage',
    'automation-normal-start-cooking',
    'automation-normal-deliver-food',
    'automation-normal-complete-order',
    'automation-normal-stop-on-error',
  ]],
]);

const expectedSettingsTabs = ['窗口', '连接', '推荐', '实验性功能', '更新'];

const expectedExperimentalPanelByHelpId = new Map([
  ['automation-enabled', '自动化总控'],
  ['automation-rare-concurrency', '自动化总控'],
  ['automation-normal-concurrency', '自动化总控'],
  ['automation-max-step-retries', '自动化总控'],
  ['automation-max-rollbacks', '自动化总控'],
  ['recommendation-rare-game-ui-pinning', '游戏界面辅助'],
  ['recommendation-rare-recipe-variant', '游戏界面辅助'],
  ['recommendation-rare-cooker-highlight', '游戏界面辅助'],
  ['recommendation-rare-seat-highlight', '游戏界面辅助'],
  ['recommendation-rare-order-highlight', '游戏界面辅助'],
  ['recommendation-rare-highlight-color', '游戏界面辅助'],
  ['recommendation-normal-game-ui-pinning', '游戏界面辅助'],
  ['recommendation-normal-recipe-variant', '游戏界面辅助'],
  ['recommendation-normal-cooker-highlight', '游戏界面辅助'],
  ['recommendation-normal-seat-highlight', '游戏界面辅助'],
  ['recommendation-normal-order-highlight', '游戏界面辅助'],
  ['recommendation-normal-highlight-color', '游戏界面辅助'],
  ['automation-rare-enabled', '稀客自动化设置'],
  ['automation-rare-take-beverage', '稀客自动化设置'],
  ['automation-rare-start-cooking', '稀客自动化设置'],
  ['automation-rare-deliver-food', '稀客自动化设置'],
  ['automation-rare-complete-order', '稀客自动化设置'],
  ['automation-rare-stop-on-error', '稀客自动化设置'],
  ['automation-rare-recipe-favorites-only', '稀客自动化设置'],
  ['automation-rare-beverage-favorites-only', '稀客自动化设置'],
  ['automation-normal-enabled', '普客自动化设置'],
  ['automation-normal-take-beverage', '普客自动化设置'],
  ['automation-normal-start-cooking', '普客自动化设置'],
  ['automation-normal-deliver-food', '普客自动化设置'],
  ['automation-normal-complete-order', '普客自动化设置'],
  ['automation-normal-stop-on-error', '普客自动化设置'],
]);

await rm(outputDir, { recursive: true, force: true });
await mkdir(outputDir, { recursive: true });

const browser = await chromium.launch({ headless: true });

try {
  const desktopContext = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const desktopPage = await desktopContext.newPage();
  await seedAndOpen(desktopPage, 100);
  await auditDesktopInteractions(desktopPage);
  await desktopContext.close();

  const minimumContext = await browser.newContext({ viewport: { width: 640, height: 760 } });
  const minimumPage = await minimumContext.newPage();
  await seedAndOpen(minimumPage, 130);
  await auditCompactTooltipLayout(minimumPage, 'minimum-640');
  await minimumContext.close();

  const minimumHeightContext = await browser.newContext({ viewport: { width: 640, height: 520 } });
  const minimumHeightPage = await minimumHeightContext.newPage();
  await seedAndOpen(minimumHeightPage, 130);
  await auditCompactTooltipLayout(minimumHeightPage, 'minimum-640x520');
  await minimumHeightContext.close();

  const touchContext = await browser.newContext({
    viewport: { width: 390, height: 844 },
    hasTouch: true,
    isMobile: true,
  });
  const touchPage = await touchContext.newPage();
  await seedAndOpen(touchPage, 130);
  await auditTouchAndCompactLayout(touchPage);
  await touchContext.close();
} finally {
  await browser.close();
}

console.log(`settings help Playwright audit passed; screenshots: ${outputDir}`);

async function seedAndOpen(page, fontScale) {
  await page.addInitScript(({ endpoint, token, scale }) => {
    localStorage.setItem('mystia-steward-companion-mod-api-endpoint', endpoint);
    localStorage.setItem('mystia-steward-companion-mod-api-token', token);
    localStorage.setItem('mystia-steward-companion-font-scale-percent', String(scale));
    localStorage.setItem('mystia-steward-companion-show-debug-details', '1');
    localStorage.setItem('mystia-steward-companion-rare-game-ui-pinning', '0');
    localStorage.setItem('mystia-steward-companion-normal-game-ui-pinning', '0');
    localStorage.setItem('mystia-steward-companion-rare-target-highlight-color', '#FFDB2E');
    localStorage.setItem('mystia-steward-companion-normal-target-highlight-color', '#5FACD3');
  }, { endpoint: apiUrl, token: apiToken, scale: fontScale });
  await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
  await page.getByText('Mod 工作台', { exact: true }).waitFor({ timeout: 10_000 });
  await activateSettings(page);
  await activateSettingsSection(page, '窗口');
  await assertNoVisibleTooltip(page, '初始状态不应显示设置说明');
}

async function auditDesktopInteractions(page) {
  await assertSettingsTabStructure(page);
  await auditHelpFieldContracts(page);
  await assertExperimentalPanelGroups(page);
  await assertAutomationSettingOrder(page);
  await auditAutomationDeliveryInvariant(page);
  await activateSettingsSection(page, '窗口');
  const switchField = findField(page, '始终置顶');
  const switchTrigger = switchField.locator(selectors.trigger);
  await assertEnabledHelpTriggerSkipsGamepad(switchTrigger, '始终置顶');

  const checkbox = switchField.locator('input[type="checkbox"]').first();
  const switchLabel = switchField.locator('label.steward-switch-field').first();
  const checkedBefore = await checkbox.isChecked();
  await switchLabel.click();
  assert.equal(await checkbox.isChecked(), !checkedBefore, '点击开关应继续修改设置');
  await assertNoVisibleTooltip(page, '鼠标点击原控件不应打开说明');
  await switchLabel.click();
  assert.equal(await checkbox.isChecked(), checkedBefore, '测试结束前应恢复开关状态');
  await switchTrigger.hover();
  const hoveredTooltip = await assertLinkedTooltipVisible(page, switchField, '始终置顶 hover');
  assert.equal(await checkbox.isChecked(), checkedBefore, '悬停信息图标不得切换开关');
  assert.ok((await hoveredTooltip.textContent())?.trim(), '设置说明不得为空');
  await switchTrigger.click();
  assert.equal(await checkbox.isChecked(), checkedBefore, '点击说明图标不得切换开关');
  await page.screenshot({ path: path.join(outputDir, 'desktop-hover.png'), fullPage: true });

  await page.keyboard.press('Escape');
  await assertNoVisibleTooltip(page, 'Escape 应关闭设置说明');

  await page.mouse.move(2, 2);
  await assertNoVisibleTooltip(page, '鼠标移出后应关闭设置说明');

  await auditControlFocus(page, '始终置顶', 'input[type="checkbox"]', 'switch');
  await auditControlFocus(page, '背景透明度', '[role="slider"]', 'slider');
  await auditControlFocus(
    page,
    '主题',
    '[data-slot="segmented-control"] label',
    'segmented control',
  );

  await activateSettingsSection(page, '推荐');
  const numberField = findField(page, '同基础料理显示');
  await numberField.locator('input[data-slot="number-input"]').click();
  await assertNoVisibleTooltip(page, '鼠标点击数值输入框不应打开说明');
  await auditControlFocus(page, '同基础料理显示', 'input', 'number input');

  await activateSettingsSection(page, '实验性功能');
  const disabledField = findField(page, '稀客加料料理选项（实验性）');
  const disabledControl = disabledField.locator('input[type="checkbox"]').first();
  assert.equal(await disabledControl.isDisabled(), true, '测试夹具应使加料料理选项开关处于禁用状态');
  const disabledTrigger = disabledField.locator(selectors.trigger);
  assert.equal(await disabledTrigger.getAttribute('tabindex'), '0', '禁用设置的说明图标应允许聚焦');
  await page.evaluate(() => {
    document.body.dataset.gamepadNavigation = 'active';
  });
  await disabledTrigger.focus();
  await assertLinkedTooltipVisible(page, disabledField, '禁用设置 focus');
  await page.evaluate(() => {
    delete document.body.dataset.gamepadNavigation;
  });
  await page.screenshot({ path: path.join(outputDir, 'desktop-disabled-focus.png'), fullPage: true });
  await auditTargetHighlightColors(page);

  await activateSettingsSection(page, '推荐');
  await assertNoVisibleTooltip(page, '切换设置分栏后应关闭旧说明');

  await findField(page, '任务料理置顶').locator(selectors.trigger).hover();
  await assertLinkedTooltipVisible(page, findField(page, '任务料理置顶'), '离开设置页前');
  await page.locator('[data-gamepad-tab-value="overview"]').first().click();
  await assertNoVisibleTooltip(page, '离开设置页后应关闭说明');
}

async function auditHelpFieldContracts(page) {
  for (const [section, expectedIds] of expectedHelpIdsBySection) {
    await activateSettingsSection(page, section);
    await assertNoVisibleTooltip(page, `${section}: 分栏默认不应显示说明`);
    const result = await page.locator(`${selectors.field}:visible`).evaluateAll((fields, controlSelector) => fields.map((field) => {
      const id = field.getAttribute('data-setting-help-id') || '';
      const triggers = Array.from(field.querySelectorAll('[data-setting-help-trigger]'));
      const describedControls = Array.from(field.querySelectorAll(controlSelector));
      const expectedDescriptionId = `setting-help-${id}-description`;
      const description = document.getElementById(expectedDescriptionId);
      const descriptionText = description?.textContent?.trim() || '';
      const visibleLegacyDescriptions = Array.from(document.querySelectorAll('.text-xs.text-muted-foreground'))
        .filter((element) => {
          const style = getComputedStyle(element);
          const rect = element.getBoundingClientRect();
          return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
        })
        .map((element) => element.textContent?.trim() || '')
        .filter((text) => text === descriptionText);
      return {
        id,
        triggerCount: triggers.length,
        describedControlCount: describedControls.length,
        controlsAreDescribed: describedControls.every((control) => (
          (control.getAttribute('aria-describedby') || '').split(/\s+/).includes(expectedDescriptionId)
        )),
        descriptionExists: description instanceof HTMLElement,
        descriptionHidden: (() => {
          if (!(description instanceof HTMLElement)) return false;
          const style = getComputedStyle(description);
          const rect = description.getBoundingClientRect();
          const clippedScreenReaderText = style.position === 'absolute'
            && rect.width <= 1
            && rect.height <= 1
            && (style.clip !== 'auto' || style.clipPath !== 'none');
          return style.visibility === 'hidden'
            || style.display === 'none'
            || rect.width === 0
            || rect.height === 0
            || clippedScreenReaderText;
        })(),
        descriptionHasText: descriptionText.length > 0,
        visibleLegacyDescriptions,
      };
    }), selectors.describedControl);

    assert.deepEqual(
      result.map(({ id }) => id).sort(),
      [...expectedIds].sort(),
      `${section}: 设置说明字段集合发生变化`,
    );
    assert.equal(new Set(result.map(({ id }) => id)).size, result.length, `${section}: data-setting-help-id 必须唯一`);
    for (const field of result) {
      assert.equal(field.triggerCount, 1, `${section}/${field.id}: 应只有一个说明图标`);
      assert.ok(field.describedControlCount > 0, `${section}/${field.id}: 未找到 aria-describedby 关联控件`);
      assert.equal(field.controlsAreDescribed, true, `${section}/${field.id}: 实际操作控件未关联字段说明`);
      assert.equal(field.descriptionExists, true, `${section}/${field.id}: aria-describedby 目标不存在`);
      assert.equal(field.descriptionHidden, true, `${section}/${field.id}: 屏幕阅读器说明不应占据布局`);
      assert.equal(field.descriptionHasText, true, `${section}/${field.id}: 说明内容不得为空`);
      assert.deepEqual(
        field.visibleLegacyDescriptions,
        [],
        `${section}/${field.id}: 说明仍以旧的常驻辅助文字显示`,
      );
    }
  }
}

async function auditControlFocus(page, label, controlSelector, kind) {
  const field = findField(page, label);
  const trigger = field.locator(selectors.trigger);
  await assertEnabledHelpTriggerSkipsGamepad(trigger, label);
  const control = field.locator(controlSelector).first();
  assert.ok(await control.count(), `${label}: 未找到 ${kind} 控件`);
  await control.scrollIntoViewIfNeeded();
  await control.evaluate((element) => {
    if (!(element instanceof HTMLElement)) return;
    if (element.matches('[data-slot="segmented-control"] label')) {
      element.tabIndex = -1;
      element.dataset.gamepadManagedTabindex = 'true';
    }
    element.blur();
  });
  await page.evaluate(() => {
    document.body.dataset.gamepadNavigation = 'active';
  });
  await control.focus();
  await assertLinkedTooltipVisible(page, field, `${label} focus`);
  const describedControl = field.locator(`:is(${selectors.describedControl})[aria-describedby]`).first();
  assert.ok(await describedControl.count(), `${label}: ${kind} 缺少 aria-describedby`);
  const describedBy = (await describedControl.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean);
  assert.ok(describedBy.length > 0, `${label}: ${kind} 的 aria-describedby 为空`);
  for (const descriptionId of describedBy) {
    const description = page.locator(`#${escapeCssIdentifier(descriptionId)}`);
    assert.equal(await description.count(), 1, `${label}: aria-describedby 目标 ${descriptionId} 不存在或不唯一`);
    assert.ok((await description.textContent())?.trim(), `${label}: aria-describedby 目标 ${descriptionId} 内容为空`);
  }
  await page.locator('body').click({ position: { x: 2, y: 2 } });
  await page.evaluate(() => {
    delete document.body.dataset.gamepadNavigation;
  });
  await assertNoVisibleTooltip(page, `${label}: 失焦后应关闭说明`);
}

async function auditTargetHighlightColors(page) {
  const rareField = page.locator(
    `${selectors.field}[data-setting-help-id="recommendation-rare-highlight-color"]`,
  ).first();
  const rareInput = rareField.locator('input[aria-label="稀客高亮色十六进制值"]');
  const normalField = page.locator(
    `${selectors.field}[data-setting-help-id="recommendation-normal-highlight-color"]`,
  ).first();
  const normalInput = normalField.locator('input[aria-label="普客高亮色十六进制值"]');
  const storageKey = 'mystia-steward-companion-rare-target-highlight-color';

  assert.equal(await rareInput.inputValue(), '#FFDB2E', '稀客高亮默认色漂移');
  assert.equal(await normalInput.inputValue(), '#5FACD3', '普客高亮默认色漂移');

  await rareInput.fill('#123456');
  await rareInput.press('Escape');
  assert.equal(await rareInput.inputValue(), '#FFDB2E', 'Escape 未撤销高亮色草稿');
  assert.equal(await rareInput.evaluate((element) => document.activeElement === element), true,
    'Escape 撤销草稿时不应触发 blur 提交');
  assert.equal(await page.evaluate((key) => localStorage.getItem(key), storageKey), '#FFDB2E',
    'Escape 错误提交了高亮色草稿');

  await rareInput.fill('#abcdef');
  await rareInput.press('Enter');
  await page.waitForFunction(
    (key) => localStorage.getItem(key) === '#ABCDEF',
    storageKey,
  );
  assert.equal(await rareInput.inputValue(), '#ABCDEF', '合法颜色没有规范化为大写 #RRGGBB');

  await rareField.getByRole('button', { name: '恢复', exact: true }).click();
  await page.waitForFunction(
    (key) => localStorage.getItem(key) === '#FFDB2E',
    storageKey,
  );
  assert.equal(await rareInput.inputValue(), '#FFDB2E', '恢复按钮没有还原稀客默认色');
}

async function auditCompactTooltipLayout(page, profileName) {
  await assertSettingsTabLabelFits(page, '实验性功能', profileName);
  if (profileName === 'minimum-640x520') {
    await auditAllCompactTooltips(page, profileName);
    await page.screenshot({ path: path.join(outputDir, `${profileName}.png`), fullPage: true });
    return;
  }
  await activateSettingsSection(page, '推荐');
  const field = findField(page, '任务料理置顶');
  const baseline = await readDocumentGeometry(page);
  await field.locator(selectors.trigger).hover();
  await assertLinkedTooltipVisible(page, field, `${profileName} hover`);
  await assertTooltipGeometry(page, baseline, profileName);
  await page.screenshot({ path: path.join(outputDir, `${profileName}.png`), fullPage: true });
}

async function auditAllCompactTooltips(page, profileName) {
  for (const [section, helpIds] of expectedHelpIdsBySection) {
    await activateSettingsSection(page, section);
    for (const helpId of helpIds) {
      const field = page.locator(`${selectors.field}[data-setting-help-id="${escapeCssAttribute(helpId)}"]`).first();
      assert.equal(await field.count(), 1, `${profileName}/${section}: 未找到 ${helpId}`);
      await field.scrollIntoViewIfNeeded();
      const baseline = await readDocumentGeometry(page);
      await field.locator(selectors.trigger).hover();
      await assertLinkedTooltipVisible(page, field, `${profileName}/${helpId}`);
      await assertTooltipGeometry(page, baseline, `${profileName}/${helpId}`);
      await page.mouse.move(2, 2);
      await assertNoVisibleTooltip(page, `${profileName}/${helpId}: 鼠标移出后应关闭说明`);
    }
  }
}

async function auditTouchAndCompactLayout(page) {
  await activateSettingsSection(page, '推荐');
  const exclusionField = findField(page, '排除材料');
  await exclusionField.locator('input[data-slot="multi-select"]').tap();
  await assertNoVisibleTooltip(page, '触摸原设置控件不应打开说明');
  await page.keyboard.press('Escape');

  const field = findField(page, '任务料理置顶');
  const baseline = await readDocumentGeometry(page);
  const trigger = field.locator(selectors.trigger);
  await trigger.tap();
  await assertLinkedTooltipVisible(page, field, 'mobile touch tap');
  await page.waitForTimeout(250);
  await assertLinkedTooltipVisible(page, field, 'mobile touch pinned');
  await trigger.tap();
  await assertNoVisibleTooltip(page, '再次触摸同一说明图标应取消固定');
  await trigger.tap();
  await assertLinkedTooltipVisible(page, field, 'mobile touch repinned');
  await assertTooltipGeometry(page, baseline, 'mobile-390');
  await page.screenshot({ path: path.join(outputDir, 'mobile-390-touch.png'), fullPage: true });

  await page.locator('[data-gamepad-tab-value="settings"]').tap({ position: { x: 2, y: 2 } });
  await assertNoVisibleTooltip(page, '触摸点击浮层外部后应关闭说明');
}

async function assertEnabledHelpTriggerSkipsGamepad(trigger, label) {
  assert.equal(await trigger.count(), 1, `${label}: 应只有一个说明图标`);
  assert.equal(await trigger.getAttribute('tabindex'), '-1', `${label}: 普通说明图标不应增加手柄焦点`);
  assert.equal(await trigger.getAttribute('data-gamepad-focus-key'), null, `${label}: 普通说明图标不应声明手柄焦点 key`);
}

async function assertLinkedTooltipVisible(page, field, context) {
  const fieldHelpId = await field.getAttribute('data-setting-help-id');
  assert.ok(fieldHelpId, `${context}: 设置项缺少 data-setting-help-id`);
  const matchingTooltip = page.locator(
    `${selectors.tooltip}[data-setting-help-id="${escapeCssAttribute(fieldHelpId)}"]:visible`,
  );
  await matchingTooltip.waitFor({ state: 'visible', timeout: 1_500 });
  const tooltips = page.locator(`${selectors.tooltip}:visible`);
  assert.equal(await tooltips.count(), 1, `${context}: 同一时间应只显示一条设置说明`);
  assert.equal(await matchingTooltip.getAttribute('role'), 'tooltip', `${context}: 说明浮层语义应为 tooltip`);
  const tooltipHelpId = await matchingTooltip.getAttribute('data-setting-help-id');
  assert.equal(tooltipHelpId, fieldHelpId, `${context}: 显示的说明与设置项不匹配`);
  const overflow = await matchingTooltip.evaluate((tooltip) => {
    const copy = tooltip.querySelector('.steward-setting-help-copy');
    const style = getComputedStyle(tooltip);
    const tooltipRect = tooltip.getBoundingClientRect();
    const copyRect = copy instanceof HTMLElement ? copy.getBoundingClientRect() : null;
    return {
      overflowY: style.overflowY,
      maxHeight: style.maxHeight,
      copyExists: copy instanceof HTMLElement,
      copyClientHeight: copy instanceof HTMLElement ? copy.clientHeight : 0,
      copyScrollHeight: copy instanceof HTMLElement ? copy.scrollHeight : 0,
      copyClientWidth: copy instanceof HTMLElement ? copy.clientWidth : 0,
      copyScrollWidth: copy instanceof HTMLElement ? copy.scrollWidth : 0,
      copyContained: copyRect !== null
        && copyRect.left >= tooltipRect.left - 1
        && copyRect.right <= tooltipRect.right + 1
        && copyRect.top >= tooltipRect.top - 1
        && copyRect.bottom <= tooltipRect.bottom + 1,
    };
  });
  assert.equal(overflow.overflowY, 'visible', `${context}: 说明浮层不应建立纵向滚动容器`);
  assert.equal(overflow.maxHeight, 'none', `${context}: 说明浮层不应使用固定最大高度`);
  assert.equal(overflow.copyExists, true, `${context}: 说明正文节点不存在`);
  assert.equal(overflow.copyContained, true, `${context}: 说明正文超出浮层内容区域`);
  assert.ok(
    overflow.copyScrollHeight <= overflow.copyClientHeight + 1,
    `${context}: 说明正文发生纵向溢出`,
  );
  assert.ok(
    overflow.copyScrollWidth <= overflow.copyClientWidth + 1,
    `${context}: 说明正文发生横向溢出`,
  );
  return matchingTooltip;
}

async function assertSettingsTabStructure(page) {
  const windowTab = page.getByRole('tab', { name: '窗口', exact: true }).first();
  const tabs = windowTab.locator('..').getByRole('tab');
  assert.deepEqual(
    (await tabs.allTextContents()).map((label) => label.trim()),
    expectedSettingsTabs,
    '设置分栏名称或顺序发生变化',
  );
  assert.equal(
    await page.getByRole('tab', { name: '自动化', exact: true }).count(),
    0,
    '旧“自动化”设置分栏不应保留',
  );
}

async function assertExperimentalPanelGroups(page) {
  await activateSettingsSection(page, '实验性功能');
  const groups = await page.locator(`${selectors.field}:visible`).evaluateAll((fields) => fields.map((field) => ({
    helpId: field.getAttribute('data-setting-help-id') || '',
    panel: field.closest('.steward-list-panel')?.querySelector('h2')?.textContent?.trim() || '',
  })));
  assert.deepEqual(
    new Map(groups.map(({ helpId, panel }) => [helpId, panel])),
    expectedExperimentalPanelByHelpId,
    '实验性功能设置未按自动化与游戏界面辅助边界分组',
  );
}

async function assertAutomationSettingOrder(page) {
  await activateSettingsSection(page, '实验性功能');
  const commonOrder = [
    'enabled',
    'take-beverage',
    'start-cooking',
    'deliver-food',
    'complete-order',
    'stop-on-error',
  ];
  for (const kind of ['rare', 'normal']) {
    const panelTitle = kind === 'rare' ? '稀客自动化设置' : '普客自动化设置';
    const panel = findPanel(page, panelTitle);
    const ids = await panel.locator(selectors.field).evaluateAll((fields) => fields.map((field) => (
      field.getAttribute('data-setting-help-id') || ''
    )));
    assert.deepEqual(
      ids.slice(0, commonOrder.length),
      commonOrder.map((suffix) => `automation-${kind}-${suffix}`),
      `${panelTitle}: 共同控制项顺序必须与普客顺序一致`,
    );
  }
}

async function auditAutomationDeliveryInvariant(page) {
  await activateSettingsSection(page, '实验性功能');

  const rarePanel = findPanel(page, '稀客自动化设置');
  const rareBeverage = findHelpField(rarePanel, 'automation-rare-take-beverage').locator('input[type="checkbox"]');
  const rareFood = findHelpField(rarePanel, 'automation-rare-deliver-food').locator('input[type="checkbox"]');
  const rareCompletion = findHelpField(rarePanel, 'automation-rare-complete-order').locator('input[type="checkbox"]');
  assert.equal(await rareBeverage.isChecked(), false, '稀客自动送达酒水测试初态应关闭');
  assert.equal(await rareFood.isChecked(), false, '稀客自动送达料理测试初态应关闭');
  assert.equal(await rareCompletion.isChecked(), false, '稀客自动完成订单测试初态应关闭');
  await clickSwitchField(rarePanel, 'automation-rare-take-beverage');
  await clickSwitchField(rarePanel, 'automation-rare-deliver-food');
  assert.equal(await rareCompletion.isChecked(), true, '开启稀客直接送达必须原子开启自动完成订单');
  await clickSwitchField(rarePanel, 'automation-rare-complete-order');
  assert.equal(await rareBeverage.isChecked(), false, '关闭稀客自动完成订单必须原子关闭酒水直送');
  assert.equal(await rareFood.isChecked(), false, '关闭稀客自动完成订单必须原子关闭料理直送');

  const normalPanel = findPanel(page, '普客自动化设置');
  const normalEnabled = findHelpField(normalPanel, 'automation-normal-enabled').locator('input[type="checkbox"]');
  assert.equal(await normalEnabled.isChecked(), false, '普客处理测试初态应关闭');
  await clickSwitchField(normalPanel, 'automation-normal-enabled');
  assert.equal(await normalEnabled.isChecked(), true, '普客处理开关应可在设置页启用');
  const normalBeverage = findHelpField(normalPanel, 'automation-normal-take-beverage').locator('input[type="checkbox"]');
  const normalFood = findHelpField(normalPanel, 'automation-normal-deliver-food').locator('input[type="checkbox"]');
  const normalCompletion = findHelpField(normalPanel, 'automation-normal-complete-order').locator('input[type="checkbox"]');
  await clickSwitchField(normalPanel, 'automation-normal-take-beverage');
  await clickSwitchField(normalPanel, 'automation-normal-deliver-food');
  assert.equal(await normalCompletion.isChecked(), true, '开启普客直接送达必须原子开启自动完成订单');
  await clickSwitchField(normalPanel, 'automation-normal-complete-order');
  assert.equal(await normalBeverage.isChecked(), false, '关闭普客自动完成订单必须原子关闭酒水直送');
  assert.equal(await normalFood.isChecked(), false, '关闭普客自动完成订单必须原子关闭料理直送');
  await clickSwitchField(normalPanel, 'automation-normal-enabled');
  assert.equal(await normalEnabled.isChecked(), false, '测试结束前应恢复普客处理开关');
}

async function assertSettingsTabLabelFits(page, label, profileName) {
  const tab = page.getByRole('tab', { name: label, exact: true }).first();
  const geometry = await tab.evaluate((element) => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
    clientHeight: element.clientHeight,
    scrollHeight: element.scrollHeight,
  }));
  assert.ok(
    geometry.scrollWidth <= geometry.clientWidth + 1,
    `${profileName}: ${label}页签文字横向截断`,
  );
  assert.ok(
    geometry.scrollHeight <= geometry.clientHeight + 1,
    `${profileName}: ${label}页签文字纵向截断`,
  );
}

async function assertNoVisibleTooltip(page, message) {
  try {
    await page.waitForFunction((selector) => {
      return !Array.from(document.querySelectorAll(selector)).some((element) => {
        const style = getComputedStyle(element);
        const rect = element.getBoundingClientRect();
        return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
      });
    }, selectors.tooltip, { timeout: 1_500 });
  } catch {
    const visible = await page.locator(`${selectors.tooltip}:visible`).evaluateAll((elements) => elements.map((element) => ({
      id: element.id,
      helpId: element.getAttribute('data-setting-help-id'),
      text: element.textContent?.trim(),
    })));
    assert.fail(`${message}; visible=${JSON.stringify(visible)}`);
  }
}

async function assertTooltipGeometry(page, baseline, profileName) {
  const geometry = await page.locator(`${selectors.tooltip}:visible`).first().evaluate((element) => {
    const rect = element.getBoundingClientRect();
    return {
      left: rect.left,
      right: rect.right,
      top: rect.top,
      bottom: rect.bottom,
      width: rect.width,
      viewportWidth: document.documentElement.clientWidth,
      viewportHeight: document.documentElement.clientHeight,
      documentScrollWidth: document.documentElement.scrollWidth,
      documentScrollHeight: document.documentElement.scrollHeight,
    };
  });
  assert.ok(geometry.width > 0, `${profileName}: 说明浮层宽度为 0`);
  assert.ok(geometry.left >= -1, `${profileName}: 说明浮层超出视口左侧`);
  assert.ok(geometry.right <= geometry.viewportWidth + 1, `${profileName}: 说明浮层超出视口右侧`);
  assert.ok(geometry.top >= -1, `${profileName}: 说明浮层超出视口顶部`);
  assert.ok(geometry.bottom <= geometry.viewportHeight + 1, `${profileName}: 说明浮层超出视口底部`);
  assert.ok(
    geometry.documentScrollWidth <= geometry.viewportWidth + 1,
    `${profileName}: 说明浮层导致文档横向溢出`,
  );
  assert.equal(
    geometry.documentScrollHeight,
    baseline.scrollHeight,
    `${profileName}: 显示说明浮层不应改变页面高度`,
  );
}

async function readDocumentGeometry(page) {
  return page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    scrollHeight: document.documentElement.scrollHeight,
  }));
}

function findField(page, label) {
  return page.locator(selectors.field).filter({ hasText: label }).first();
}

function findPanel(page, title) {
  return page.locator('.steward-list-panel').filter({
    has: page.getByRole('heading', { name: title, exact: true }),
  }).first();
}

function findHelpField(panel, helpId) {
  return panel.locator(`${selectors.field}[data-setting-help-id="${escapeCssAttribute(helpId)}"]`).first();
}

async function clickSwitchField(panel, helpId) {
  await findHelpField(panel, helpId).locator('label.steward-switch-field').first().click();
}

function escapeCssIdentifier(value) {
  return value.replace(/([^a-zA-Z0-9_-])/g, '\\$1');
}

function escapeCssAttribute(value) {
  return value.replace(/([\\"])/g, '\\$1');
}

async function activateSettings(page) {
  const tab = page.locator('[data-gamepad-tab-value="settings"]').first();
  await tab.scrollIntoViewIfNeeded();
  await tab.click();
  await page.getByRole('tab', { name: '窗口', exact: true }).waitFor({ state: 'visible' });
}

async function activateSettingsSection(page, label) {
  const tab = page.getByRole('tab', { name: label, exact: true }).first();
  await tab.scrollIntoViewIfNeeded();
  await tab.click();
  await page.waitForTimeout(100);
}
