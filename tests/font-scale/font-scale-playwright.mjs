import assert from 'node:assert/strict';
import { mkdir, rm } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';
import { inspectMinimumPrimaryTabsLayout } from '../ui-layout/primary-tabs-layout.mjs';

const appUrl = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173';
const apiUrl = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const apiToken = process.env.MYSTIA_API_TOKEN || 'mock-token';
const outputDir = process.env.FONT_SCALE_AUDIT_OUTPUT_DIR || '/tmp/mystia-companion-font-scale-audit';
const storageKey = 'mystia-steward-companion-font-scale-percent';

const tabs = [
  'overview',
  'normal',
  'rare',
  'custom-recipes',
  'service',
  'missions',
  'inventory',
  'help',
  'logs',
  'settings',
];

const profiles = [
  { name: 'desktop-default', width: 1280, height: 900, scale: 100, allTabs: false },
  { name: 'minimum-small', width: 640, height: 520, scale: 90, allTabs: false, showDebugDetails: false },
  { name: 'minimum-large', width: 640, height: 520, scale: 130, allTabs: true },
  { name: 'mobile-default', width: 390, height: 844, scale: 100, allTabs: false },
  { name: 'mobile-large', width: 390, height: 844, scale: 130, allTabs: true },
];

await rm(outputDir, { recursive: true, force: true });
await mkdir(outputDir, { recursive: true });
const browser = await chromium.launch({ headless: true });

try {
  for (const profile of profiles) {
    console.log(`auditing font scale profile: ${profile.name}`);
    const page = await browser.newPage({ viewport: { width: profile.width, height: profile.height } });
    await page.addInitScript(seedLocalStorage, {
      endpoint: apiUrl,
      token: apiToken,
      fontScale: profile.scale,
      fontScaleStorageKey: storageKey,
      showDebugDetails: profile.showDebugDetails ?? true,
    });
    await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
    await page.getByText('Mod 工作台', { exact: true }).waitFor({ timeout: 10_000 });
    await assertFontScale(page, profile);
    if (profile.width === 640) {
      await assertMinimumPrimaryTabsLayout(page, profile);
      await assertMinimumHeaderToolbarLayout(page, profile);
    }

    const targetTabs = profile.allTabs ? tabs : ['overview', 'settings'];
    for (const tab of targetTabs) {
      await activateTab(page, tab);
      await page.waitForTimeout(tab === 'logs' ? 500 : 200);
      await assertNoDocumentOverflow(page, profile, tab);
      await assertControlLayout(page, profile, tab);
      if (tab === 'rare' || tab === 'service') {
        await assertEffectiveCustomRecipeHeaders(page, profile, tab);
      }
      await page.screenshot({
        path: path.join(outputDir, `${profile.name}-${tab}.png`),
        fullPage: true,
      });

      if (profile.allTabs && tab === 'custom-recipes') {
        await auditOpenSelect(page, profile);
      }
      if (profile.allTabs && tab === 'settings') {
        await auditSettingsSections(page, profile);
      }
      if (profile.allTabs && tab === 'service') {
        await auditServiceFocusMode(page, profile);
      }
    }

    if (profile.name === 'desktop-default') {
      await verifySliderPersistenceAndReset(page);
    }
    await page.close();
    console.log(`font scale profile passed: ${profile.name}`);
  }

  console.log('auditing font scale normalization boundaries');
  await verifyNormalizationBoundaries(browser);
} finally {
  await browser.close();
}

console.log(`font scale Playwright audit passed; screenshots: ${outputDir}`);

function seedLocalStorage({ endpoint, token, fontScale, fontScaleStorageKey, showDebugDetails }) {
  localStorage.setItem('mystia-steward-companion-mod-api-endpoint', endpoint);
  localStorage.setItem('mystia-steward-companion-mod-api-token', token);
  localStorage.setItem('mystia-steward-companion-show-debug-details', showDebugDetails ? '1' : '0');
  const seedMarker = 'mystia-steward-companion-font-scale-audit-seeded';
  if (!sessionStorage.getItem(seedMarker)) {
    localStorage.setItem(fontScaleStorageKey, String(fontScale));
    sessionStorage.setItem(seedMarker, '1');
  }
}

async function activateTab(page, tab) {
  const trigger = page.locator(`[data-gamepad-tab-value="${tab}"]`).first();
  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
}

async function assertFontScale(page, profile) {
  const actual = await page.evaluate(() => {
    const rootStyle = getComputedStyle(document.documentElement);
    const workbench = document.querySelector('[data-companion-surface="workbench"]');
    if (!(workbench instanceof HTMLElement)) throw new Error('workbench surface missing');
    return {
      variable: Number(rootStyle.getPropertyValue('--companion-font-scale')),
      inheritedFontSize: Number.parseFloat(getComputedStyle(workbench).fontSize),
    };
  });
  const expectedBase = profile.width <= 719 ? 15 : 16;
  assert.equal(actual.variable, profile.scale / 100, `${profile.name}: CSS scale variable is incorrect`);
  assert.ok(
    Math.abs(actual.inheritedFontSize - expectedBase * profile.scale / 100) < 0.05,
    `${profile.name}: inherited font size is ${actual.inheritedFontSize}`,
  );
}

async function assertNoDocumentOverflow(page, profile, tab) {
  const dimensions = await page.evaluate(() => ({
    clientWidth: document.documentElement.clientWidth,
    scrollWidth: document.documentElement.scrollWidth,
  }));
  assert.ok(
    dimensions.scrollWidth <= dimensions.clientWidth + 1,
    `${profile.name}/${tab}: document overflows by ${dimensions.scrollWidth - dimensions.clientWidth}px`,
  );
}

async function assertMinimumPrimaryTabsLayout(page, profile) {
  const expectedValues = profile.showDebugDetails === false
    ? tabs.filter((value) => value !== 'logs')
    : tabs;
  const result = await inspectMinimumPrimaryTabsLayout(page, expectedValues);
  assert.equal(result.ok, true, `${profile.name}: primary tabs layout ${JSON.stringify(result)}`);
  assert.deepEqual(result.missingValues, [], `${profile.name}: primary tabs are missing`);
  assert.deepEqual(result.unexpectedValues, [], `${profile.name}: primary tabs contain unexpected values`);
  assert.equal(result.orderMatches, true, `${profile.name}: primary tab order is incorrect`);
  assert.equal(result.triggerCount, expectedValues.length, `${profile.name}: primary tab count is incorrect`);
  assert.deepEqual(result.failures, [], `${profile.name}: primary tab clipping ${JSON.stringify(result.failures)}`);
  assert.equal(result.display, 'grid', `${profile.name}: primary tabs must use the minimum-width grid`);
  assert.equal(result.columnCount, 5, `${profile.name}: primary tab column count is incorrect`);
  assert.equal(result.rowCount, 2, `${profile.name}: primary tabs must use exactly two rows`);
  assert.equal(result.noInternalOverflow, true, `${profile.name}: primary tabs overflow internally`);
}

async function assertMinimumHeaderToolbarLayout(page, profile) {
  const result = await page.locator('.steward-workbench-header').evaluate((header) => {
    const headerGrid = header.firstElementChild;
    const toolbar = headerGrid?.children[1];
    if (!(toolbar instanceof HTMLElement)) return { ok: false, reason: 'toolbar missing' };
    const children = Array.from(toolbar.children).filter((node) => node instanceof HTMLElement);
    const rects = children.map((child) => child.getBoundingClientRect());
    return {
      ok: rects.length === 4
        && rects.every((rect) => Math.abs(rect.top - rects[0].top) <= 2)
        && rects.slice(1).every((rect, index) => rect.left >= rects[index].right - 1)
        && toolbar.scrollWidth <= toolbar.clientWidth + 1
        && toolbar.scrollHeight <= toolbar.clientHeight + 1
        && rects[0].width >= 136
        && rects[1].width >= 112,
      childCount: rects.length,
      tops: rects.map((rect) => Math.round(rect.top)),
      widths: rects.map((rect) => Math.round(rect.width)),
      clientWidth: toolbar.clientWidth,
      scrollWidth: toolbar.scrollWidth,
      clientHeight: toolbar.clientHeight,
      scrollHeight: toolbar.scrollHeight,
    };
  });
  assert.equal(result.ok, true, `${profile.name}: minimum header toolbar layout ${JSON.stringify(result)}`);
}

async function assertControlLayout(page, profile, tab) {
  const result = await page.evaluate(() => {
    const isVisible = (element) => {
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return style.display !== 'none'
        && style.visibility !== 'hidden'
        && rect.width > 0
        && rect.height > 0;
    };
    const summary = (element) => ({
      tag: element.tagName.toLowerCase(),
      role: element.getAttribute('role'),
      text: (element.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 80),
      className: typeof element.className === 'string' ? element.className.slice(0, 120) : '',
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
      clientHeight: element.clientHeight,
      scrollHeight: element.scrollHeight,
    });
    const textControls = Array.from(document.querySelectorAll([
      'button',
      '[role="tab"]',
      '.steward-switch-field',
      '.mantine-SegmentedControl-label',
    ].join(',')))
      .filter((node) => node instanceof HTMLElement && isVisible(node));
    const boundsControls = Array.from(document.querySelectorAll([
      '[data-slot="slider"]',
      '[data-slot="segmented-control"]',
      '.steward-switch-field',
      '.steward-input-root',
      '.steward-number-input-root',
      '.steward-select',
      '.steward-multi-select',
    ].join(',')))
      .filter((node) => node instanceof HTMLElement && isVisible(node));
    const viewportWidth = document.documentElement.clientWidth;

    return {
      textOverflow: textControls
        .filter((element) => (
          element.scrollWidth > element.clientWidth + 1
          || element.scrollHeight > element.clientHeight + 1
        ))
        .map(summary),
      boundsOverflow: boundsControls
        .filter((element) => {
          const rect = element.getBoundingClientRect();
          return rect.left < -1 || rect.right > viewportWidth + 1;
        })
        .map((element) => ({
          ...summary(element),
          left: element.getBoundingClientRect().left,
          right: element.getBoundingClientRect().right,
          viewportWidth,
        })),
    };
  });
  assert.deepEqual(result.textOverflow, [], `${profile.name}/${tab}: control text overflow ${JSON.stringify(result.textOverflow)}`);
  assert.deepEqual(result.boundsOverflow, [], `${profile.name}/${tab}: control bounds overflow ${JSON.stringify(result.boundsOverflow)}`);
}

async function assertEffectiveCustomRecipeHeaders(page, profile, tab) {
  const result = await page.locator('[data-effective-custom-recipes-trigger="true"]:visible').evaluateAll((triggers) => (
    triggers.map((trigger) => {
      const header = trigger.closest('[data-effective-custom-recipes-header="true"], .steward-panel-header');
      const title = header?.querySelector('h2, h3');
      if (!(header instanceof HTMLElement) || !(title instanceof HTMLElement)) {
        return { ok: false, reason: 'header or title missing' };
      }
      const headerRect = header.getBoundingClientRect();
      const titleRect = title.getBoundingClientRect();
      const triggerRect = trigger.getBoundingClientRect();
      return {
        ok: Math.abs((titleRect.top + titleRect.bottom) / 2 - (triggerRect.top + triggerRect.bottom) / 2) <= 4
          && titleRect.right <= triggerRect.left + 1
          && triggerRect.right <= headerRect.right + 1
          && header.scrollWidth <= header.clientWidth + 1,
        title: title.textContent?.trim() || '',
        titleCenter: Math.round((titleRect.top + titleRect.bottom) / 2),
        triggerCenter: Math.round((triggerRect.top + triggerRect.bottom) / 2),
        titleRight: Math.round(titleRect.right),
        triggerLeft: Math.round(triggerRect.left),
        headerWidth: Math.round(headerRect.width),
      };
    })
  ));
  assert.ok(result.length > 0, `${profile.name}/${tab}: effective custom recipe trigger missing`);
  assert.deepEqual(
    result.filter((entry) => !entry.ok),
    [],
    `${profile.name}/${tab}: effective custom recipe header layout ${JSON.stringify(result)}`,
  );
}

async function auditOpenSelect(page, profile) {
  const select = page.locator('input[data-slot="select"], input.steward-select-input').first();
  if (!(await select.count()) || await select.isDisabled()) return;
  await select.click();
  const listbox = page.locator('[role="listbox"]').first();
  await listbox.waitFor({ state: 'visible' });
  await assertNoDocumentOverflow(page, profile, 'custom-recipes-select');
  await assertControlLayout(page, profile, 'custom-recipes-select');
  await page.screenshot({
    path: path.join(outputDir, `${profile.name}-custom-recipes-select.png`),
    fullPage: true,
  });
  await page.keyboard.press('Escape');
}

async function auditSettingsSections(page, profile) {
  const sections = [
    { key: 'window', label: '窗口' },
    { key: 'connection', label: '连接' },
    { key: 'recommendation', label: '推荐' },
    { key: 'automation', label: '自动化' },
    { key: 'updates', label: '更新' },
  ];

  for (const section of sections) {
    await page.getByRole('tab', { name: section.label, exact: true }).click();
    if (section.key === 'connection') await page.waitForTimeout(400);
    if (section.key === 'updates') await page.getByRole('button', { name: '检查', exact: true }).waitFor();
    if (profile.width === 640 && (section.key === 'window' || section.key === 'recommendation')) {
      await assertMinimumSettingSegmentedControls(page, profile, section);
    }
    const auditKey = `settings-${section.key}`;
    await assertNoDocumentOverflow(page, profile, auditKey);
    await assertControlLayout(page, profile, auditKey);
    await page.screenshot({
      path: path.join(outputDir, `${profile.name}-${auditKey}.png`),
      fullPage: true,
    });
  }

  await page.getByRole('tab', { name: '窗口', exact: true }).click();
}

async function assertMinimumSettingSegmentedControls(page, profile, section) {
  const expectedLabels = section.key === 'window'
    ? ['焦点切换', '主题']
    : ['经营中订单排序', '预算处理', '权重方案'];
  const result = await page.locator('.steward-settings-segmented-control:visible').evaluateAll(
    (controls) => controls.map((control) => {
      const field = control.parentElement;
      const label = field?.firstElementChild?.textContent?.trim() || '';
      const items = Array.from(control.querySelectorAll('.mantine-SegmentedControl-control'));
      const innerLabels = Array.from(control.querySelectorAll('.mantine-SegmentedControl-innerLabel'));
      const itemRects = items.map((item) => item.getBoundingClientRect());
      const controlRect = control.getBoundingClientRect();
      return {
        label,
        ok: items.length > 1
          && control.scrollWidth <= control.clientWidth + 1
          && control.scrollHeight <= control.clientHeight + 1
          && itemRects.every((rect) => rect.left >= controlRect.left - 1 && rect.right <= controlRect.right + 1)
          && itemRects.slice(1).every((rect, index) => rect.left >= itemRects[index].right - 1)
          && innerLabels.every((inner) => {
            const rect = inner.getBoundingClientRect();
            const style = getComputedStyle(inner);
            return style.whiteSpace === 'nowrap'
              && rect.height <= Number.parseFloat(style.lineHeight) + 1
              && inner.scrollWidth <= inner.clientWidth + 1
              && inner.scrollHeight <= inner.clientHeight + 1;
          }),
        width: Math.round(controlRect.width),
        optionCount: items.length,
      };
    }),
  );
  assert.deepEqual(
    result.map((entry) => entry.label),
    expectedLabels,
    `${profile.name}/settings-${section.key}: setting segmented controls changed`,
  );
  assert.deepEqual(
    result.filter((entry) => !entry.ok),
    [],
    `${profile.name}/settings-${section.key}: setting segmented geometry ${JSON.stringify(result)}`,
  );
}

async function auditServiceFocusMode(page, profile) {
  await page.getByRole('button', { name: '稀客订单专注模式', exact: true }).click();
  const focusPage = page.locator('[data-service-focus-page="true"]');
  const toolbar = focusPage.locator('[data-service-focus-toolbar="true"]');
  await focusPage.waitFor();
  assert.equal(await focusPage.getAttribute('aria-label'), '稀客订单专注模式');
  assert.equal(await focusPage.getByText('只显示当前稀客点单推荐。', { exact: true }).count(), 0);
  const toolbarLayout = await toolbar.evaluate((element) => {
    const toolbarRect = element.getBoundingClientRect();
    const children = Array.from(element.children).filter((node) => node instanceof HTMLElement);
    const rects = children.map((child) => child.getBoundingClientRect());
    const rows = new Map();
    for (const rect of rects) {
      const rowKey = Math.round((rect.top + rect.bottom) / 2);
      const currentRight = rows.get(rowKey) ?? Number.NEGATIVE_INFINITY;
      rows.set(rowKey, Math.max(currentRight, rect.right));
    }
    return {
      ok: children.length === 4
        && element.scrollWidth <= element.clientWidth + 1
        && rects.every((rect) => rect.left >= toolbarRect.left - 1 && rect.right <= toolbarRect.right + 1)
        && Array.from(rows.values()).every((right) => Math.abs(right - toolbarRect.right) <= 2),
      childCount: children.length,
      rowRights: Array.from(rows.values()).map((right) => Math.round(right)),
      toolbarRight: Math.round(toolbarRect.right),
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    };
  });
  assert.equal(toolbarLayout.ok, true, `${profile.name}/service-focus: toolbar layout ${JSON.stringify(toolbarLayout)}`);
  await assertNoDocumentOverflow(page, profile, 'service-focus');
  await assertControlLayout(page, profile, 'service-focus');
  await page.screenshot({
    path: path.join(outputDir, `${profile.name}-service-focus.png`),
    fullPage: true,
  });
  await page.getByRole('button', { name: '退出专注模式', exact: true }).click();
  await page.getByText('Mod 工作台', { exact: true }).waitFor();
}

async function verifySliderPersistenceAndReset(page) {
  await activateTab(page, 'settings');
  const slider = page.getByRole('slider', { name: '字体大小', exact: true });
  assert.equal(await slider.count(), 1, 'font size must expose exactly one slider control');
  assert.equal(await slider.getAttribute('aria-valuetext'), '100%');
  const sliderRoot = slider.locator('xpath=ancestor::*[@data-slot="slider"][1]');
  const pointerTarget = sliderRoot.locator('.mantine-Slider-trackContainer');
  const pointerBounds = await pointerTarget.boundingBox();
  assert.ok(pointerBounds && pointerBounds.height >= 28, 'font size slider pointer target is too small');
  await pointerTarget.click({ position: { x: pointerBounds.width * 0.75, y: pointerBounds.height / 2 } });
  const sliderHandle = await slider.elementHandle();
  assert.ok(sliderHandle, 'font size slider element is missing');
  await page.waitForFunction(
    (element) => element.getAttribute('aria-valuetext') === '120%',
    sliderHandle,
  );
  assert.equal(await slider.getAttribute('aria-valuetext'), '120%');
  await slider.focus();
  await slider.press('End');
  await page.waitForFunction(() => getComputedStyle(document.documentElement)
    .getPropertyValue('--companion-font-scale').trim() === '1.3');
  assert.equal(await slider.getAttribute('aria-valuetext'), '130%');
  assert.equal(await page.evaluate((key) => localStorage.getItem(key), storageKey), '130');

  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.getByText('Mod 工作台', { exact: true }).waitFor();
  assert.equal(
    await page.evaluate(() => getComputedStyle(document.documentElement)
      .getPropertyValue('--companion-font-scale').trim()),
    '1.3',
  );
  await activateTab(page, 'settings');
  await page.getByRole('button', { name: '恢复默认字体大小' }).click();
  await page.waitForFunction(() => getComputedStyle(document.documentElement)
    .getPropertyValue('--companion-font-scale').trim() === '1');
  assert.equal(await page.evaluate((key) => localStorage.getItem(key), storageKey), '100');
}

async function verifyNormalizationBoundaries(browser) {
  const cases = [
    { name: 'missing', raw: null, expected: 100 },
    { name: 'invalid', raw: 'not-a-number', expected: 100 },
    { name: 'below-minimum', raw: '89', expected: 90 },
    { name: 'above-maximum', raw: '131', expected: 130 },
    { name: 'round-down', raw: '92', expected: 90 },
    { name: 'round-up', raw: '93', expected: 95 },
  ];

  for (const testCase of cases) {
    const page = await browser.newPage({ viewport: { width: 640, height: 520 } });
    await page.addInitScript(({ endpoint, token, key, raw }) => {
      localStorage.setItem('mystia-steward-companion-mod-api-endpoint', endpoint);
      localStorage.setItem('mystia-steward-companion-mod-api-token', token);
      if (raw === null) localStorage.removeItem(key);
      else localStorage.setItem(key, raw);
    }, {
      endpoint: apiUrl,
      token: apiToken,
      key: storageKey,
      raw: testCase.raw,
    });
    await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
    await page.getByText('Mod 工作台', { exact: true }).waitFor({ timeout: 10_000 });
    await page.waitForFunction(({ key, expected }) => (
      localStorage.getItem(key) === String(expected)
      && getComputedStyle(document.documentElement).getPropertyValue('--companion-font-scale').trim() === String(expected / 100)
    ), { key: storageKey, expected: testCase.expected });
    await page.close();
  }
}
