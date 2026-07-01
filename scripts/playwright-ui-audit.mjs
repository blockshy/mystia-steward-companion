import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

/**
 * 伴随窗口 UI 巡检脚本。
 *
 * 脚本配合 mock-local-api 使用，会遍历主要 Tab、截图、检查透明背景模型、横向溢出、hover 反馈和 Select Portal。
 * 它不是端到端业务测试，目标是快速发现布局和主题层面的回归。
 */
const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.UI_AUDIT_OUTPUT_DIR || '/tmp/mystia-companion-ui-audit';
const STORAGE_PREFIX = 'mystia-steward-companion';

const viewports = [
  { name: 'desktop', width: 1280, height: 900 },
  { name: 'compact', width: 900, height: 760 },
  { name: 'minimum', width: 720, height: 760 },
];

const tabs = [
  { value: 'overview', label: '概览' },
  { value: 'normal', label: '普客' },
  { value: 'rare', label: '稀客' },
  { value: 'custom-recipes', label: '自定义推荐料理' },
  { value: 'service', label: '经营中' },
  { value: 'tasks', label: '任务' },
  { value: 'inventory', label: '修改' },
  { value: 'help', label: '帮助' },
  { value: 'logs', label: '日志' },
  { value: 'settings', label: '设置' },
];

const hoverTargets = [
  {
    label: 'Button',
    selector: '[data-slot="button"]:not(:disabled), button:not(:disabled)',
  },
  {
    label: 'Input',
    selector: '[data-slot="input"] input:not(:disabled), [data-slot="number-input"] input:not(:disabled), input.steward-input:not(:disabled)',
  },
  {
    label: 'Select',
    selector: 'input[data-slot="select"]:not(:disabled), input.steward-select-input:not(:disabled)',
  },
  {
    label: 'Switch',
    selector: '[data-slot="switch"]:not([data-disabled="true"])',
  },
  {
    label: 'SegmentedControl',
    selector: '[data-slot="segmented-control"] label',
  },
  {
    label: 'TabsTrigger',
    selector: '[data-slot="tabs-trigger"]:not([aria-selected="true"])',
  },
  {
    label: 'Slider',
    selector: '.mantine-Slider-thumb:not([data-disabled="true"])',
  },
  {
    label: 'Accordion',
    selector: '.steward-accordion-trigger',
  },
];

const browser = await chromium.launch({ headless: true });
const issues = [];
const screenshots = [];

await mkdir(OUTPUT_DIR, { recursive: true });

for (const viewport of viewports) {
  const page = await browser.newPage({ viewport: { width: viewport.width, height: viewport.height } });
  await page.addInitScript(seedLocalStorage, { apiUrl: API_URL, apiToken: API_TOKEN, storagePrefix: STORAGE_PREFIX });
  await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.body.innerText.includes('1.0.5'), null, { timeout: 10000 });
  await auditTransparencyModel(page, viewport);

  for (const tab of tabs) {
    await activateTab(page, tab);
    await page.waitForTimeout(tab.value === 'logs' ? 700 : 350);
    await auditPage(page, viewport, tab);
  }

  await page.close();
}

await browser.close();

const report = buildReport();
await writeFile(path.join(OUTPUT_DIR, 'report.md'), report);
console.log(report);
console.log(`\nScreenshots and report written to ${OUTPUT_DIR}`);

function seedLocalStorage({ apiUrl, apiToken, storagePrefix }) {
  localStorage.setItem(`${storagePrefix}-mod-api-endpoint`, apiUrl);
  localStorage.setItem(`${storagePrefix}-mod-api-token`, apiToken);
  localStorage.setItem(`${storagePrefix}-show-debug-details`, '1');
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

async function activateTab(page, tab) {
  const trigger = page.locator(`[data-gamepad-tab-value="${tab.value}"]`).first();
  if (!(await trigger.count())) {
    issues.push({
      viewport: page.viewportSize()?.width || 0,
      tab: tab.label,
      component: 'TabsTrigger',
      message: `未找到 ${tab.label} 页签入口。`,
    });
    return;
  }

  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
}

async function auditPage(page, viewport, tab) {
  const fileName = `${viewport.name}-${tab.value}.png`;
  const screenshotPath = path.join(OUTPUT_DIR, fileName);
  await page.screenshot({ path: screenshotPath, fullPage: true });
  screenshots.push({ tab: tab.label, viewport: viewport.name, path: screenshotPath });

  const overflow = await getHorizontalOverflow(page);
  if (overflow.hasOverflow) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'Layout',
      message: `页面横向溢出 ${overflow.scrollWidth - overflow.clientWidth}px。`,
    });
  }

  await auditMinimumViewportLayout(page, viewport, tab);

  for (const target of hoverTargets) {
    await auditHoverTarget(page, viewport, tab, target);
  }

  await auditSelectDropdown(page, viewport, tab);
}

async function auditMinimumViewportLayout(page, viewport, tab) {
  if (viewport.name !== 'minimum') return;

  await auditMinimumTwoColumnGrids(page, viewport, tab);

  if (tab.value === 'overview') {
    await auditMinimumShellGutter(page, viewport, tab);
    await auditMinimumHeaderLayout(page, viewport, tab);
  }

  if (tab.value === 'settings') {
    await auditMinimumRecommendationSettingsLayout(page, viewport, tab);
  }
}

async function auditMinimumTwoColumnGrids(page, viewport, tab) {
  const result = await page.evaluate(({ tabValue }) => {
    const expectedTabs = new Set(['overview', 'normal', 'rare', 'custom-recipes', 'service', 'tasks', 'inventory', 'settings']);
    const candidates = Array.from(document.querySelectorAll('*'))
      .filter((node) => node instanceof HTMLElement)
      .filter((element) => {
        const className = element.getAttribute('class') || '';
        return className.includes('grid-cols-2')
          && className.includes('max-[719px]:grid-cols-1')
          && isVisible(element);
      });

    const checked = [];
    const failures = [];
    for (const element of candidates) {
      const children = Array.from(element.children)
        .filter((node) => node instanceof HTMLElement && isVisible(node));
      if (children.length < 2) continue;

      const [first, second] = children;
      const firstRect = first.getBoundingClientRect();
      const secondRect = second.getBoundingClientRect();
      const gridStyle = window.getComputedStyle(element);
      const sameRow = Math.abs(firstRect.top - secondRect.top) <= 2 && secondRect.left > firstRect.left + 8;
      const usableWidth = firstRect.width >= 120 && secondRect.width >= 120;
      const summary = {
        text: normalizeText(element.textContent || '').slice(0, 40),
        columns: gridStyle.gridTemplateColumns,
        firstTop: Math.round(firstRect.top),
        secondTop: Math.round(secondRect.top),
        firstLeft: Math.round(firstRect.left),
        secondLeft: Math.round(secondRect.left),
        firstWidth: Math.round(firstRect.width),
        secondWidth: Math.round(secondRect.width),
      };
      checked.push(summary);
      if (!sameRow || !usableWidth) {
        failures.push({ ...summary, sameRow, usableWidth });
      }
    }

    return {
      ok: failures.length === 0 && (!expectedTabs.has(tabValue) || checked.length > 0),
      checkedCount: checked.length,
      failures,
      expected: expectedTabs.has(tabValue),
    };

    function isVisible(element) {
      const rect = element.getBoundingClientRect();
      const style = window.getComputedStyle(element);
      return rect.width > 0
        && rect.height > 0
        && style.display !== 'none'
        && style.visibility !== 'hidden'
        && Number(style.opacity || '1') > 0.05;
    }

    function normalizeText(value) {
      return value.replace(/\s+/g, ' ').trim();
    }
  }, { tabValue: tab.value });

  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'TwoColumnLayout',
      message: result.checkedCount === 0 && result.expected
        ? '最小宽度下未找到应保持双列的可见网格。'
        : `最小宽度双列网格异常：${JSON.stringify(result.failures).slice(0, 300)}`,
    });
  }
}

async function auditMinimumShellGutter(page, viewport, tab) {
  const result = await page.evaluate(() => {
    const main = document.querySelector('.companion-shell > main');
    const content = main?.firstElementChild;
    if (!(main instanceof HTMLElement) || !(content instanceof HTMLElement)) {
      return { ok: false, reason: '未找到外层窗口布局检查目标。' };
    }

    const rect = content.getBoundingClientRect();
    const clientWidth = document.documentElement.clientWidth;
    const top = rect.top;
    const left = rect.left;
    const right = clientWidth - rect.right;
    const maxGutter = 8;
    const maxRightGutter = 18;
    return {
      ok: top <= maxGutter && left <= maxGutter && right <= maxRightGutter,
      top: Math.round(top),
      left: Math.round(left),
      right: Math.round(right),
      maxGutter,
      maxRightGutter,
    };
  });

  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'ShellGutter',
      message: result.reason || `最小宽度外层边距过大：top=${result.top}px，left=${result.left}px，right=${result.right}px，期望 top/left 不超过 ${result.maxGutter}px、right 含滚动条稳定槽不超过 ${result.maxRightGutter}px。`,
    });
  }
}

async function auditMinimumHeaderLayout(page, viewport, tab) {
  const result = await page.evaluate(() => {
    const header = document.querySelector('.steward-workbench-header');
    const headerGrid = header?.firstElementChild;
    const toolbar = headerGrid?.children[1];
    const statusGrid = header?.children[1];
    if (!(header instanceof HTMLElement)
      || !(headerGrid instanceof HTMLElement)
      || !(toolbar instanceof HTMLElement)
      || !(statusGrid instanceof HTMLElement)) {
      return { ok: false, reason: '未找到 Header 布局检查目标。' };
    }

    const gridChildren = Array.from(headerGrid.children).filter((node) => node instanceof HTMLElement);
    const toolbarChildren = Array.from(toolbar.children).filter((node) => node instanceof HTMLElement);
    const statusChildren = Array.from(statusGrid.children).filter((node) => node instanceof HTMLElement);
    if (gridChildren.length < 2 || toolbarChildren.length < 3 || statusChildren.length !== 3) {
      return { ok: false, reason: 'Header 工具条或状态摘要项目数量不符合预期。' };
    }

    const [brandRect, toolbarRect] = gridChildren.map((node) => node.getBoundingClientRect());
    const toolbarRects = toolbarChildren.map((node) => node.getBoundingClientRect());
    const statusRects = statusChildren.map((node) => node.getBoundingClientRect());
    const toolbarTop = toolbarRects[0].top;
    const statusTop = statusRects[0].top;
    const toolbarSameLine = Math.abs(brandRect.top - toolbarRect.top) <= 4
      && toolbarRects.every((rect) => Math.abs(rect.top - toolbarTop) <= 4);
    const statusSameLine = statusRects.every((rect) => Math.abs(rect.top - statusTop) <= 4);
    const statusUsableWidth = statusRects.every((rect) => rect.width >= 120);

    return {
      ok: toolbarSameLine && statusSameLine && statusUsableWidth,
      toolbarSameLine,
      statusSameLine,
      statusUsableWidth,
      toolbarTops: toolbarRects.map((rect) => Math.round(rect.top)),
      statusTops: statusRects.map((rect) => Math.round(rect.top)),
      statusWidths: statusRects.map((rect) => Math.round(rect.width)),
    };
  });

  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'ResponsiveHeader',
      message: result.reason || `最小宽度 Header 未保持同行或状态三列异常：toolbarSameLine=${result.toolbarSameLine}，statusSameLine=${result.statusSameLine}，statusWidths=${result.statusWidths?.join('/')}`,
    });
  }
}

async function auditMinimumRecommendationSettingsLayout(page, viewport, tab) {
  const recommendationTab = page.getByRole('tab', { name: '推荐', exact: true }).first();
  if (!(await recommendationTab.count())) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'SettingsRecommendation',
      message: '未找到设置页推荐分栏入口。',
    });
    return;
  }

  await recommendationTab.click();
  await page.waitForTimeout(200);
  await auditMinimumTwoColumnGrids(page, viewport, { ...tab, label: `${tab.label} 推荐` });

  const result = await page.evaluate(() => {
    const visibleContent = Array.from(document.querySelectorAll('[data-slot="tabs-content"]'))
      .find((node) => node instanceof HTMLElement
        && node.getAttribute('data-state') === 'active'
        && node.textContent?.includes('推荐权重'));
    const scope = visibleContent instanceof HTMLElement ? visibleContent : document.body;
    const rows = Array.from(scope.querySelectorAll('.steward-data-row'))
      .filter((node) => node instanceof HTMLElement && node.querySelector('[data-slot="slider"]'));

    if (rows.length === 0) {
      return { ok: false, reason: '未找到推荐权重滑条行。' };
    }

    const failures = rows.map((row, index) => {
      const slider = row.querySelector('[data-slot="slider"]');
      const switchField = row.querySelector('.steward-switch-field');
      const value = row.querySelector('.tabular-nums');
      if (!(slider instanceof HTMLElement)
        || !(switchField instanceof HTMLElement)
        || !(value instanceof HTMLElement)
        || !(row instanceof HTMLElement)) {
        return { index, reason: '权重行缺少滑条、标签或数值。' };
      }

      const rowRect = row.getBoundingClientRect();
      const sliderRect = slider.getBoundingClientRect();
      const switchRect = switchField.getBoundingClientRect();
      const valueRect = value.getBoundingClientRect();
      const headerBottom = Math.max(switchRect.bottom, valueRect.bottom);
      const stacked = sliderRect.top >= headerBottom - 1;
      const contained = sliderRect.left >= rowRect.left - 1 && sliderRect.right <= rowRect.right + 1;
      const usableWidth = sliderRect.width >= 180;
      if (stacked && contained && usableWidth) return null;

      return {
        index,
        stacked,
        contained,
        usableWidth,
        sliderTop: Math.round(sliderRect.top),
        headerBottom: Math.round(headerBottom),
        sliderWidth: Math.round(sliderRect.width),
      };
    }).filter(Boolean);

    return {
      ok: failures.length === 0,
      rowCount: rows.length,
      failures,
    };
  });

  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'RecommendationWeights',
      message: result.reason || `推荐权重滑条最小宽度布局异常：${JSON.stringify(result.failures).slice(0, 300)}`,
    });
  }

  const screenshotPath = path.join(OUTPUT_DIR, `${viewport.name}-${tab.value}-recommendation.png`);
  await page.screenshot({ path: screenshotPath, fullPage: true });
  screenshots.push({ tab: `${tab.label} 推荐`, viewport: viewport.name, path: screenshotPath });
}

/**
 * 检查 Tauri 透明窗口模型。
 *
 * 根节点必须保持透明，内容壳负责背景透明度，文字保持不透明，避免桌面透明窗口出现整窗发灰或文字半透明。
 */
async function auditTransparencyModel(page, viewport) {
  const result = await page.evaluate(() => {
    const shell = document.querySelector('.companion-shell');
    const title = document.querySelector('h1');
    if (!(shell instanceof HTMLElement) || !(title instanceof HTMLElement)) {
      return { ok: false, reason: '未找到透明度检查目标元素。' };
    }

    const htmlBackgroundAlpha = readColorAlpha(window.getComputedStyle(document.documentElement).backgroundColor);
    const bodyBackgroundAlpha = readColorAlpha(window.getComputedStyle(document.body).backgroundColor);
    const root = document.querySelector('#root');
    const rootBackgroundAlpha = root instanceof HTMLElement
      ? readColorAlpha(window.getComputedStyle(root).backgroundColor)
      : 1;
    const mantineBodyColor = window.getComputedStyle(document.documentElement).getPropertyValue('--mantine-color-body').trim();
    const mantineBodyAlpha = readColorAlpha(mantineBodyColor);
    const shellBackgroundAlpha = readColorAlpha(window.getComputedStyle(shell).backgroundColor);
    const titleColorAlpha = readColorAlpha(window.getComputedStyle(title).color);
    return {
      ok: htmlBackgroundAlpha < 0.02
        && bodyBackgroundAlpha < 0.02
        && rootBackgroundAlpha < 0.02
        && mantineBodyAlpha < 0.02
        && shellBackgroundAlpha < 0.98
        && titleColorAlpha > 0.98,
      htmlBackgroundAlpha,
      bodyBackgroundAlpha,
      rootBackgroundAlpha,
      mantineBodyAlpha,
      shellBackgroundAlpha,
      titleColorAlpha,
    };

    function readColorAlpha(value) {
      if (value.trim() === 'transparent') return 0;

      const colorFunctionMatch = value.match(/color\([^/]+\/\s*([0-9.]+%?)\s*\)/);
      if (colorFunctionMatch) {
        const alpha = Number(colorFunctionMatch[1]);
        return colorFunctionMatch[1].endsWith('%') ? alpha / 100 : alpha;
      }

      const rgbMatch = value.match(/rgba?\(([^)]+)\)/);
      if (!rgbMatch) return 1;
      const parts = rgbMatch[1].split(/[,/]/).map((part) => part.trim()).filter(Boolean);
      if (parts.length < 4) return 1;
      const rawAlpha = parts[3];
      const alpha = Number(rawAlpha.replace('%', ''));
      return rawAlpha.endsWith('%') ? alpha / 100 : alpha;
    }
  });

  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: '全局',
      component: 'Transparency',
      message: result.reason || `根背景 alpha(html/body/root/mantine-body/shell)=${result.htmlBackgroundAlpha}/${result.bodyBackgroundAlpha}/${result.rootBackgroundAlpha}/${result.mantineBodyAlpha}/${result.shellBackgroundAlpha}，文字 alpha=${result.titleColorAlpha}，不符合背景和文字透明度分离预期。`,
    });
  }
}

/**
 * 抽样检查可交互控件 hover 后是否产生可见样式变化。
 */
async function auditHoverTarget(page, viewport, tab, target) {
  const locators = page.locator(target.selector);
  const count = Math.min(await locators.count(), 4);
  for (let index = 0; index < count; index += 1) {
    const element = locators.nth(index);
    if (!(await isVisibleForAudit(element))) continue;
    const before = await readElementStyles(element);
    await element.scrollIntoViewIfNeeded();
    await element.hover({ timeout: 2000 });
    await page.waitForTimeout(80);
    const after = await readElementStyles(element);
    if (!hasMeaningfulStyleChange(before, after)) {
      const label = await element.evaluate((node) => {
        const text = node.textContent?.trim().replace(/\s+/g, ' ') || '';
        const title = node.getAttribute('aria-label') || node.getAttribute('title') || '';
        return (text || title || node.tagName).slice(0, 30);
      });
      issues.push({
        viewport: viewport.name,
        tab: tab.label,
        component: target.label,
        message: `hover 后视觉样式未变化：${label}`,
      });
    }
    return;
  }
}

async function auditSelectDropdown(page, viewport, tab) {
  const select = page.locator('input[data-slot="select"]:not(:disabled), input.steward-select-input:not(:disabled)').first();
  if (!(await select.count()) || !(await isVisibleForAudit(select))) return;

  await select.scrollIntoViewIfNeeded();
  await select.click();
  await page.waitForTimeout(160);
  const dropdown = page.locator('.mantine-Combobox-dropdown, .mantine-Select-dropdown, [role="listbox"]').first();
  if (!(await dropdown.count()) || !(await dropdown.isVisible())) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'Select',
      message: '点击 Select 后未显示 Portal 下拉层。',
    });
    return;
  }

  const dropdownStyles = await readElementStyles(dropdown);
  if (isTransparentOrEmpty(dropdownStyles.backgroundColor)) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'Select',
      message: 'Select 下拉层背景接近全透明，列表内容可能压在页面内容上。',
    });
  }

  const screenshotPath = path.join(OUTPUT_DIR, `${viewport.name}-${tab.value}-select-open.png`);
  await page.screenshot({ path: screenshotPath, fullPage: true });
  screenshots.push({ tab: `${tab.label} Select`, viewport: viewport.name, path: screenshotPath });
  await page.keyboard.press('Escape');
}

async function isVisibleForAudit(locator) {
  try {
    return await locator.evaluate((node) => {
      const element = node instanceof HTMLElement ? node : null;
      if (!element) return false;
      const rect = element.getBoundingClientRect();
      const styles = window.getComputedStyle(element);
      return rect.width > 4
        && rect.height > 4
        && styles.visibility !== 'hidden'
        && styles.display !== 'none'
        && Number(styles.opacity) > 0.05;
    });
  } catch {
    return false;
  }
}

async function readElementStyles(locator) {
  return locator.evaluate((node) => {
    const styles = window.getComputedStyle(node);
    return {
      backgroundColor: styles.backgroundColor,
      borderColor: styles.borderColor,
      boxShadow: styles.boxShadow,
      color: styles.color,
      filter: styles.filter,
      opacity: styles.opacity,
      outlineColor: styles.outlineColor,
      textDecorationColor: styles.textDecorationColor,
      transform: styles.transform,
    };
  });
}

async function getHorizontalOverflow(page) {
  return page.evaluate(() => ({
    clientWidth: document.documentElement.clientWidth,
    scrollWidth: document.documentElement.scrollWidth,
    bodyScrollWidth: document.body.scrollWidth,
    hasOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
  }));
}

function hasMeaningfulStyleChange(before, after) {
  return Object.keys(before).some((key) => before[key] !== after[key]);
}

function isTransparentOrEmpty(backgroundColor) {
  return backgroundColor === 'transparent'
    || backgroundColor === 'rgba(0, 0, 0, 0)'
    || backgroundColor === 'rgba(0,0,0,0)';
}

function buildReport() {
  const lines = [
    '# mystia-steward-companion UI audit',
    '',
    `- App: ${APP_URL}`,
    `- API: ${API_URL}`,
    `- Output: ${OUTPUT_DIR}`,
    `- Viewports: ${viewports.map((item) => `${item.name} ${item.width}x${item.height}`).join(', ')}`,
    '',
    '## Issues',
    '',
  ];

  if (issues.length === 0) {
    lines.push('- 未发现自动化可判定的 hover 或横向溢出问题。');
  } else {
    for (const issue of issues) {
      lines.push(`- [${issue.viewport}] ${issue.tab} / ${issue.component}: ${issue.message}`);
    }
  }

  lines.push('', '## Screenshots', '');
  for (const screenshot of screenshots) {
    lines.push(`- [${screenshot.viewport}] ${screenshot.tab}: ${screenshot.path}`);
  }

  return `${lines.join('\n')}\n`;
}
