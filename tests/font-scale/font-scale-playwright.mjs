import assert from 'node:assert/strict';
import { mkdir, rm } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';
import { inspectMinimumNestedTabsLayout } from '../ui-layout/nested-tabs-layout.mjs';
import { inspectMinimumPrimaryTabsLayout } from '../ui-layout/primary-tabs-layout.mjs';

const appUrl = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173';
const apiUrl = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const apiToken = process.env.MYSTIA_API_TOKEN || 'mock-token';
const outputDir = process.env.FONT_SCALE_AUDIT_OUTPUT_DIR || '/tmp/mystia-companion-font-scale-audit';
const storageKey = 'mystia-steward-companion-font-scale-percent';
const chromiumExecutablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH?.trim();

const primaryTabs = [
  'overview',
  'recommendations',
  'service',
  'extensions',
  'logs',
  'settings',
];
const pages = [
  { value: 'overview', topValue: 'overview' },
  { value: 'normal', topValue: 'recommendations', innerSelector: '[data-recommendation-tabs]', innerLabel: '普客' },
  { value: 'rare', topValue: 'recommendations', innerSelector: '[data-recommendation-tabs]', innerLabel: '稀客' },
  { value: 'custom-recipes', topValue: 'recommendations', innerSelector: '[data-recommendation-tabs]', innerLabel: '自定义推荐料理' },
  { value: 'favorites', topValue: 'recommendations', innerSelector: '[data-recommendation-tabs]', innerLabel: '收藏管理' },
  { value: 'service', topValue: 'service' },
  { value: 'missions', topValue: 'extensions', innerSelector: '[data-extension-tabs]', innerLabel: '任务列表' },
  { value: 'rare-invitations', topValue: 'extensions', innerSelector: '[data-extension-tabs]', innerLabel: '稀客邀请' },
  { value: 'rare-participation', topValue: 'extensions', innerSelector: '[data-extension-tabs]', innerLabel: '稀客调度' },
  { value: 'inventory', topValue: 'extensions', innerSelector: '[data-extension-tabs]', innerLabel: '修改' },
  { value: 'logs', topValue: 'logs' },
  { value: 'settings', topValue: 'settings', innerSelector: '[data-settings-tabs]', innerLabel: '窗口' },
  { value: 'connection', topValue: 'settings', innerSelector: '[data-settings-tabs]', innerLabel: '连接' },
  { value: 'help', topValue: 'settings', innerSelector: '[data-settings-tabs]', innerLabel: '帮助' },
];

const profiles = [
  { name: 'desktop-default', width: 1280, height: 900, scale: 100, allTabs: false },
  { name: 'minimum-small', width: 640, height: 520, scale: 90, allTabs: false, showDebugDetails: false },
  { name: 'minimum-large', width: 640, height: 520, scale: 130, allTabs: true, mousePassthrough: true },
  { name: 'mobile-default', width: 390, height: 844, scale: 100, allTabs: false },
  { name: 'mobile-large', width: 390, height: 844, scale: 130, allTabs: true },
];

await rm(outputDir, { recursive: true, force: true });
await mkdir(outputDir, { recursive: true });
const browser = await chromium.launch({
  headless: true,
  ...(chromiumExecutablePath ? { executablePath: chromiumExecutablePath } : {}),
});

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
      mousePassthrough: profile.mousePassthrough ?? false,
    });
    await page.goto(appUrl, { waitUntil: 'domcontentloaded' });
    await page.locator('[data-gamepad-tab-value="overview"]').first().waitFor({ timeout: 10_000 });
    await assertFontScale(page, profile);
    if (profile.width === 640) {
      await assertMinimumPrimaryTabsLayout(page, profile);
    }

    const targetPages = profile.allTabs
      ? pages
      : pages.filter((candidate) => candidate.value === 'overview' || candidate.value === 'settings');
    for (const pageView of targetPages) {
      const tab = pageView.value;
      await activatePage(page, pageView);
      await page.waitForTimeout(tab === 'logs' ? 500 : 200);
      if (tab === 'service'
        && profile.scale === 130
        && (profile.width === 640 || profile.width === 390)) {
        await auditServiceRecommendationToolbar(page, profile);
        await auditExpandedServiceSummary(page, profile);
      }
      await assertNoDocumentOverflow(page, profile, tab);
      await assertControlLayout(page, profile, tab);
      if (profile.width === 640 && tab !== 'logs') {
        await assertMinimumNestedTabsLayout(page, profile, tab);
      }
      if (profile.width === 390 && tab === 'overview') {
        await assertMobileOverviewTabsLayout(page, profile);
      }
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

function seedLocalStorage({ endpoint, token, fontScale, fontScaleStorageKey, showDebugDetails, mousePassthrough }) {
  localStorage.setItem('mystia-steward-companion-mod-api-endpoint', endpoint);
  localStorage.setItem('mystia-steward-companion-mod-api-token', token);
  localStorage.setItem('mystia-steward-companion-client-id', 'font-scale-audit-device');
  localStorage.setItem('mystia-steward-companion-show-debug-details', showDebugDetails ? '1' : '0');
  localStorage.setItem('mystia-steward-companion-mouse-passthrough', mousePassthrough ? '1' : '0');
  const seedMarker = 'mystia-steward-companion-font-scale-audit-seeded';
  if (!sessionStorage.getItem(seedMarker)) {
    localStorage.setItem(fontScaleStorageKey, String(fontScale));
    sessionStorage.setItem(seedMarker, '1');
  }
}

async function activatePage(page, pageView) {
  const trigger = page.locator(`[data-gamepad-tab-value="${pageView.topValue}"]`).first();
  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
  if (pageView.innerSelector && pageView.innerLabel) {
    const innerTrigger = page.locator(pageView.innerSelector).getByRole('tab', {
      name: pageView.innerLabel,
      exact: true,
    });
    await innerTrigger.scrollIntoViewIfNeeded();
    await innerTrigger.click();
  }
}

async function activatePageByValue(page, value) {
  const pageView = pages.find((candidate) => candidate.value === value);
  assert.ok(pageView, `Unknown audit page: ${value}`);
  await activatePage(page, pageView);
}

async function assertMinimumNestedTabsLayout(page, profile, tab) {
  const result = await inspectMinimumNestedTabsLayout(page);
  assert.ok(result.listCount > 0, `${profile.name}/${tab}: nested tab list missing`);
  assert.equal(
    result.ok,
    true,
    `${profile.name}/${tab}: nested tabs do not fill the row ${JSON.stringify(result.failures)}`,
  );
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
    ? primaryTabs.filter((value) => value !== 'logs')
    : primaryTabs;
  const result = await inspectMinimumPrimaryTabsLayout(page, expectedValues);
  assert.equal(result.ok, true, `${profile.name}: primary tabs layout ${JSON.stringify(result)}`);
  assert.deepEqual(result.missingValues, [], `${profile.name}: primary tabs are missing`);
  assert.deepEqual(result.unexpectedValues, [], `${profile.name}: primary tabs contain unexpected values`);
  assert.equal(result.orderMatches, true, `${profile.name}: primary tab order is incorrect`);
  assert.equal(result.triggerCount, expectedValues.length, `${profile.name}: primary tab count is incorrect`);
  assert.deepEqual(result.failures, [], `${profile.name}: primary tab clipping ${JSON.stringify(result.failures)}`);
  assert.equal(result.display, 'grid', `${profile.name}: primary tabs must use the minimum-width grid`);
  assert.equal(result.columnCount, expectedValues.length, `${profile.name}: primary tab column count is incorrect`);
  assert.equal(result.rowCount, 1, `${profile.name}: grouped primary tabs must use one row`);
  assert.equal(result.noInternalOverflow, true, `${profile.name}: primary tabs overflow internally`);
}

async function assertMobileOverviewTabsLayout(page, profile) {
  const result = await page.locator('[data-overview-tabs="true"]').evaluate((list) => {
    const triggers = Array.from(list.querySelectorAll(':scope > [data-slot="tabs-trigger"]'));
    const rects = triggers.map((trigger) => trigger.getBoundingClientRect());
    return {
      labels: triggers.map((trigger) => (trigger.textContent || '').trim()),
      scrollable: list.getAttribute('data-scrollable-tabs') === 'true',
      overflows: list.scrollWidth > list.clientWidth,
      singleRow: rects.every((rect) => Math.abs(rect.top - rects[0].top) <= 2),
    };
  });
  assert.deepEqual(result.labels, ['连接', '状态', '库存', '操作'], `${profile.name}: overview tab order drifted`);
  assert.equal(result.scrollable, true, `${profile.name}: overview tabs must opt in to horizontal scrolling`);
  assert.equal(result.overflows, true, `${profile.name}: overview tabs must expose horizontal scrolling at 390px`);
  assert.equal(result.singleRow, true, `${profile.name}: overview tabs must stay on one row`);
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
    { key: 'experimental', label: '实验性功能' },
    { key: 'updates', label: '更新' },
  ];

  for (const section of sections) {
    await page.getByRole('tab', { name: section.label, exact: true }).click();
    if (section.key === 'connection') await page.waitForTimeout(400);
    if (section.key === 'updates') {
      await page.locator('[data-gamepad-focus-key="settings:updates:check"]').waitFor();
    }
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

async function auditExpandedServiceSummary(page, profile) {
  const trigger = page.locator('[data-service-summary-trigger="true"]:visible').first();
  assert.equal(await trigger.count(), 1, `${profile.name}/service-summary: trigger missing`);
  assert.equal(
    await trigger.getAttribute('aria-expanded'),
    'false',
    `${profile.name}/service-summary: summary must be collapsed on entry`,
  );
  await auditCompactCollapsedServiceSummary(trigger, profile);

  await trigger.scrollIntoViewIfNeeded();
  await trigger.focus();
  await trigger.press('Enter');
  await page.waitForFunction(() => {
    const summaryTrigger = document.querySelector('[data-service-summary-trigger="true"]');
    const content = document.querySelector('[data-service-summary-content="true"]');
    return summaryTrigger?.getAttribute('aria-expanded') === 'true'
      && content instanceof HTMLElement
      && content.getBoundingClientRect().width > 0
      && content.getBoundingClientRect().height > 0;
  }, null, { timeout: 2_000 });

  const layout = await page.locator('[data-service-summary-content="true"]:visible').evaluate((content, viewportWidth) => {
    const accordion = content.closest('[data-service-summary-accordion="true"]');
    const grid = content.querySelector('[data-service-summary-grid="true"]');
    if (!(accordion instanceof HTMLElement) || !(grid instanceof HTMLElement)) {
      return { ok: false, reason: 'accordion or grid missing' };
    }
    const gridRect = grid.getBoundingClientRect();
    const children = Array.from(grid.children).filter((child) => child instanceof HTMLElement);
    const overflowingChildren = children
      .filter((child) => child.scrollWidth > child.clientWidth + 1)
      .map((child) => (child.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 80));
    const columnCount = getComputedStyle(grid).gridTemplateColumns
      .trim()
      .split(/\s+/)
      .filter(Boolean)
      .length;
    const expectedColumns = viewportWidth === 640 ? 3 : 1;
    return {
      ok: columnCount === expectedColumns
        && accordion.scrollWidth <= accordion.clientWidth + 1
        && content.scrollWidth <= content.clientWidth + 1
        && grid.scrollWidth <= grid.clientWidth + 1
        && overflowingChildren.length === 0
        && children.every((child) => {
          const rect = child.getBoundingClientRect();
          return rect.left >= gridRect.left - 1 && rect.right <= gridRect.right + 1;
        }),
      columnCount,
      expectedColumns,
      accordionWidth: `${accordion.clientWidth}/${accordion.scrollWidth}`,
      contentWidth: `${content.clientWidth}/${content.scrollWidth}`,
      gridWidth: `${grid.clientWidth}/${grid.scrollWidth}`,
      overflowingChildren,
    };
  }, profile.width);
  assert.equal(
    layout.ok,
    true,
    `${profile.name}/service-summary: expanded layout overflow ${JSON.stringify(layout)}`,
  );
  await assertNoDocumentOverflow(page, profile, 'service-summary-expanded');
  await assertControlLayout(page, profile, 'service-summary-expanded');
  await page.screenshot({
    path: path.join(outputDir, `${profile.name}-service-summary-expanded.png`),
    fullPage: true,
  });

  await trigger.press('Enter');
  await page.waitForFunction(() => {
    const summaryTrigger = document.querySelector('[data-service-summary-trigger="true"]');
    const content = document.querySelector('[data-service-summary-content="true"]');
    const contentVisible = content instanceof HTMLElement
      && content.getBoundingClientRect().width > 0
      && content.getBoundingClientRect().height > 0
      && getComputedStyle(content).visibility !== 'hidden';
    return summaryTrigger?.getAttribute('aria-expanded') === 'false' && !contentVisible;
  }, null, { timeout: 2_000 });
}

async function auditServiceRecommendationToolbar(page, profile) {
  await page.locator('[data-service-order-tab-trigger="rare"]').click();
  const toolbar = page.locator('[data-service-recommendation-toolbar="true"]:visible');
  await toolbar.waitFor({ state: 'visible', timeout: 10_000 });
  const layout = await toolbar.evaluate((element) => {
    const panelToolbar = element.closest('[data-list-panel-toolbar="true"]');
    const panel = element.closest('.steward-list-panel');
    const header = panel?.querySelector('.steward-panel-header');
    const heading = header?.querySelector('h2');
    const countBadge = header?.querySelector('[data-service-order-count-badge="true"]');
    const recipe = element.querySelector('[data-service-recommendation-limit="recipe"]');
    const beverage = element.querySelector('[data-service-recommendation-limit="beverage"]');
    const button = element.querySelector('[data-gamepad-focus-key="service:focus:enter"]');
    if (!(panelToolbar instanceof HTMLElement)
      || !(header instanceof HTMLElement)
      || !(heading instanceof HTMLElement)
      || !(countBadge instanceof HTMLElement)
      || !(recipe instanceof HTMLElement)
      || !(beverage instanceof HTMLElement)
      || !(button instanceof HTMLElement)) {
      return { ok: false, reason: 'toolbar elements missing' };
    }

    const toolbarRect = element.getBoundingClientRect();
    const headerRect = header.getBoundingClientRect();
    const headingRect = heading.getBoundingClientRect();
    const countRect = countBadge.getBoundingClientRect();
    const recipeRect = recipe.getBoundingClientRect();
    const beverageRect = beverage.getBoundingClientRect();
    const buttonRect = button.getBoundingClientRect();
    const center = (rect) => (rect.top + rect.bottom) / 2;
    const contained = [recipeRect, beverageRect, buttonRect].every((rect) => (
      rect.left >= toolbarRect.left - 1 && rect.right <= toolbarRect.right + 1
    ));
    const headerClean = Math.abs(center(headingRect) - center(countRect)) <= 2
      && headingRect.left >= headerRect.left - 1
      && countRect.right <= headerRect.right + 1;
    const singleRowGeometry = Math.abs(center(recipeRect) - center(beverageRect)) <= 2
      && Math.abs(center(beverageRect) - center(buttonRect)) <= 2
      && recipeRect.right <= beverageRect.left + 1
      && beverageRect.right <= buttonRect.left + 1;
    const compactInputs = recipe.querySelector('[data-focus-limit-density="compact"]') !== null
      && beverage.querySelector('[data-focus-limit-density="compact"]') !== null;

    return {
      ok: contained
        && headerClean
        && element.scrollWidth <= element.clientWidth + 1
        && panelToolbar.scrollWidth <= panelToolbar.clientWidth + 1
        && button.scrollWidth <= button.clientWidth + 1
        && singleRowGeometry
        && compactInputs,
      contained,
      headerClean,
      singleRowGeometry,
      compactInputs,
      toolbarSize: `${element.clientWidth}/${element.scrollWidth}`,
      panelToolbarSize: `${panelToolbar.clientWidth}/${panelToolbar.scrollWidth}`,
      buttonSize: `${button.clientWidth}/${button.scrollWidth}`,
      rects: [recipeRect, beverageRect, buttonRect].map((rect) => ({
        left: Math.round(rect.left),
        top: Math.round(rect.top),
        right: Math.round(rect.right),
        bottom: Math.round(rect.bottom),
      })),
    };
  });
  assert.equal(
    layout.ok,
    true,
    `${profile.name}/service-recommendation-toolbar: ${JSON.stringify(layout)}`,
  );
  await assertNoDocumentOverflow(page, profile, 'service-recommendation-toolbar');
  await assertControlLayout(page, profile, 'service-recommendation-toolbar');
}

async function auditCompactCollapsedServiceSummary(trigger, profile) {
  const layout = await trigger.evaluate((element) => {
    const label = element.querySelector('.mantine-Accordion-label');
    const row = label?.firstElementChild;
    if (!(label instanceof HTMLElement) || !(row instanceof HTMLElement)) {
      return { ok: false, reason: 'accordion label or compact row missing' };
    }

    const rowChildren = Array.from(row.children).filter((child) => child instanceof HTMLElement);
    const title = rowChildren.find((child) => child.textContent?.trim() === '经营概况');
    const summary = rowChildren.find((child) => child.hasAttribute('title'));
    if (!(title instanceof HTMLElement) || !(summary instanceof HTMLElement)) {
      return { ok: false, reason: 'compact title or summary missing' };
    }

    const triggerStyle = getComputedStyle(element);
    const summaryStyle = getComputedStyle(summary);
    const triggerRect = element.getBoundingClientRect();
    const rowRect = row.getBoundingClientRect();
    const titleRect = title.getBoundingClientRect();
    const summaryRect = summary.getBoundingClientRect();
    const summaryLineHeight = Number.parseFloat(summaryStyle.lineHeight);
    const expectedMinimumHeightProbe = document.createElement('div');
    expectedMinimumHeightProbe.style.cssText = [
      'position:absolute',
      'visibility:hidden',
      'pointer-events:none',
      'height:var(--steward-control-height-md)',
    ].join(';');
    document.body.append(expectedMinimumHeightProbe);
    const expectedMinimumHeight = expectedMinimumHeightProbe.getBoundingClientRect().height;
    expectedMinimumHeightProbe.remove();

    const sameVisualLine = Math.abs(
      (titleRect.top + titleRect.bottom) / 2 - (summaryRect.top + summaryRect.bottom) / 2,
    ) <= 2;
    const summaryUsesSingleLineEllipsis = summaryStyle.whiteSpace === 'nowrap'
      && summaryStyle.overflowX === 'hidden'
      && summaryStyle.textOverflow === 'ellipsis'
      && Number.isFinite(summaryLineHeight)
      && summaryRect.height <= summaryLineHeight + 1;
    const contained = titleRect.left >= rowRect.left - 1
      && summaryRect.right <= rowRect.right + 1
      && titleRect.top >= rowRect.top - 1
      && titleRect.bottom <= rowRect.bottom + 1
      && summaryRect.top >= rowRect.top - 1
      && summaryRect.bottom <= rowRect.bottom + 1;
    const noContainerOverflow = element.scrollWidth <= element.clientWidth + 1
      && label.scrollWidth <= label.clientWidth + 1
      && row.scrollWidth <= row.clientWidth + 1
      && element.scrollHeight <= element.clientHeight + 1;
    const minimumHeight = Number.parseFloat(triggerStyle.minHeight);
    const minimumClickTarget = expectedMinimumHeight > 0
      && Number.isFinite(minimumHeight)
      && Math.abs(minimumHeight - expectedMinimumHeight) <= 1
      && triggerRect.height >= expectedMinimumHeight - 1
      && triggerStyle.maxHeight === 'none';

    return {
      ok: element.getAttribute('data-ui-density') === 'compact'
        && sameVisualLine
        && summaryUsesSingleLineEllipsis
        && contained
        && noContainerOverflow
        && minimumClickTarget,
      density: element.getAttribute('data-ui-density'),
      sameVisualLine,
      summaryUsesSingleLineEllipsis,
      summaryActuallyTruncated: summary.scrollWidth > summary.clientWidth + 1,
      contained,
      noContainerOverflow,
      triggerSize: `${Math.round(triggerRect.width)}x${Math.round(triggerRect.height)}`,
      triggerScrollSize: `${element.clientWidth}/${element.scrollWidth} x ${element.clientHeight}/${element.scrollHeight}`,
      rowWidth: `${row.clientWidth}/${row.scrollWidth}`,
      summaryWidth: `${summary.clientWidth}/${summary.scrollWidth}`,
      minimumHeight,
      expectedMinimumHeight,
      maxHeight: triggerStyle.maxHeight,
    };
  });

  assert.equal(
    layout.ok,
    true,
    `${profile.name}/service-summary: compact collapsed layout ${JSON.stringify(layout)}`,
  );
}

async function auditServiceFocusMode(page, profile) {
  await page.getByRole('button', { name: '专注模式', exact: true }).click();
  const focusPage = page.locator('[data-service-focus-page="true"]');
  const toolbar = focusPage.locator('[data-service-focus-toolbar="true"]');
  const controls = focusPage.locator('[data-service-focus-controls="true"]');
  await focusPage.waitFor();
  assert.equal(await focusPage.getAttribute('aria-label'), '稀客订单专注模式');
  assert.equal(await focusPage.getByText('只显示当前稀客点单推荐。', { exact: true }).count(), 0);
  assert.equal(
    await focusPage.locator('[data-mouse-passthrough-safety="true"]').count(),
    profile.mousePassthrough ? 1 : 0,
    `${profile.name}/service-focus: mouse-passthrough safety notice visibility drifted`,
  );
  assert.equal(await controls.getAttribute('aria-label'), '专注模式显示控制');
  assert.equal(
    await controls.getByRole('button', { name: '退出专注模式', exact: true }).count(),
    1,
    `${profile.name}/service-focus: icon exit accessible name drifted`,
  );
  assert.equal(
    await toolbar.locator(':scope > *').count(),
    profile.mousePassthrough ? 2 : 1,
    `${profile.name}/service-focus: safety notice must remain separate from the one-line controls`,
  );
  const toolbarLayout = await controls.evaluate((element) => {
    const controlsRect = element.getBoundingClientRect();
    const children = Array.from(element.children).filter((node) => node instanceof HTMLElement);
    const rects = children.map((child) => child.getBoundingClientRect());
    const center = (rect) => (rect.top + rect.bottom) / 2;
    const exitButton = children.at(-1);
    const exitRect = exitButton?.getBoundingClientRect();
    const exitIcon = exitButton?.querySelector('svg');
    const compactSwitch = children[0];
    const compactSwitchStyle = compactSwitch ? getComputedStyle(compactSwitch) : null;
    const compactSwitchLabel = compactSwitch?.querySelector(':scope > span');
    const compactSwitchLabelStyle = compactSwitchLabel ? getComputedStyle(compactSwitchLabel) : null;
    const compactSwitchLabelRect = compactSwitchLabel?.getBoundingClientRect();
    const compactSwitchLabelLineHeight = Number.parseFloat(compactSwitchLabelStyle?.lineHeight ?? '');
    const sameRow = rects.every((rect) => Math.abs(center(rect) - center(rects[0])) <= 2);
    const ordered = rects.slice(1).every((rect, index) => rect.left >= rects[index].right - 1);
    const contained = rects.every((rect) => rect.left >= controlsRect.left - 1
      && rect.right <= controlsRect.right + 1
      && rect.top >= controlsRect.top - 1
      && rect.bottom <= controlsRect.bottom + 1);
    return {
      ok: children.length === 4
        && sameRow
        && ordered
        && contained
        && element.scrollWidth <= element.clientWidth + 1
        && element.scrollHeight <= element.clientHeight + 1
        && children[0]?.getAttribute('data-switch-control-density') === 'compact'
        && children[1]?.getAttribute('data-focus-limit-density') === 'compact'
        && children[2]?.getAttribute('data-focus-limit-density') === 'compact'
        && Number.parseFloat(compactSwitchStyle?.columnGap ?? '') === 6
        && Number.parseFloat(compactSwitchStyle?.paddingLeft ?? '') === 0
        && Number.parseFloat(compactSwitchStyle?.paddingRight ?? '') === 0
        && compactSwitchLabelStyle?.whiteSpace === 'nowrap'
        && compactSwitchLabelRect !== undefined
        && Number.isFinite(compactSwitchLabelLineHeight)
        && compactSwitchLabelRect.height <= compactSwitchLabelLineHeight + 1
        && compactSwitchLabel.scrollHeight <= compactSwitchLabel.clientHeight + 1
        && exitButton?.getAttribute('data-ui-size') === 'icon-sm'
        && exitButton.getAttribute('aria-label') === '退出专注模式'
        && exitButton.getAttribute('title') === '退出专注模式'
        && exitButton.textContent?.trim() === ''
        && exitIcon?.getAttribute('aria-hidden') === 'true'
        && exitRect !== undefined
        && Math.abs(exitRect.width - exitRect.height) <= 2
        && exitRect.width >= 31,
      childCount: children.length,
      sameRow,
      ordered,
      contained,
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
      clientHeight: element.clientHeight,
      scrollHeight: element.scrollHeight,
      exitSize: exitRect ? `${Math.round(exitRect.width)}x${Math.round(exitRect.height)}` : null,
      compactSwitch: compactSwitchStyle && compactSwitchLabelStyle && compactSwitchLabelRect
        ? {
            gap: compactSwitchStyle.columnGap,
            padding: `${compactSwitchStyle.paddingLeft}/${compactSwitchStyle.paddingRight}`,
            labelWhiteSpace: compactSwitchLabelStyle.whiteSpace,
            labelSize: `${Math.round(compactSwitchLabelRect.width)}x${Math.round(compactSwitchLabelRect.height)}`,
            labelLineHeight: compactSwitchLabelStyle.lineHeight,
          }
        : null,
      rects: rects.map((rect) => ({
        left: Math.round(rect.left),
        top: Math.round(rect.top),
        right: Math.round(rect.right),
        bottom: Math.round(rect.bottom),
      })),
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
  await page.locator('[data-gamepad-tab-value="overview"]').first().waitFor();
}

async function verifySliderPersistenceAndReset(page) {
  await activatePageByValue(page, 'settings');
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
  await page.locator('[data-gamepad-tab-value="overview"]').first().waitFor();
  assert.equal(
    await page.evaluate(() => getComputedStyle(document.documentElement)
      .getPropertyValue('--companion-font-scale').trim()),
    '1.3',
  );
  await activatePageByValue(page, 'settings');
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
    await page.locator('[data-gamepad-tab-value="overview"]').first().waitFor({ timeout: 10_000 });
    await page.waitForFunction(({ key, expected }) => (
      localStorage.getItem(key) === String(expected)
      && getComputedStyle(document.documentElement).getPropertyValue('--companion-font-scale').trim() === String(expected / 100)
    ), { key: storageKey, expected: testCase.expected });
    await page.close();
  }
}
