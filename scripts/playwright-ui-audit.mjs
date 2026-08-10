import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';
import { inspectMinimumNestedTabsLayout } from '../tests/ui-layout/nested-tabs-layout.mjs';
import { inspectMinimumPrimaryTabsLayout } from '../tests/ui-layout/primary-tabs-layout.mjs';

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
const CHROMIUM_EXECUTABLE_PATH = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH?.trim();
const STORAGE_PREFIX = 'mystia-steward-companion';

const viewports = [
  { name: 'desktop', width: 1280, height: 900 },
  { name: 'compact', width: 900, height: 760 },
  { name: 'minimum', width: 640, height: 760 },
];

const tabs = [
  { value: 'overview', label: '概览' },
  { value: 'normal', label: '推荐料理 · 普客', topValue: 'recommendations', innerSelector: '[data-recommendation-tabs]', innerLabel: '普客' },
  { value: 'rare', label: '推荐料理 · 稀客', topValue: 'recommendations', innerSelector: '[data-recommendation-tabs]', innerLabel: '稀客' },
  { value: 'custom-recipes', label: '推荐料理 · 自定义推荐料理', topValue: 'recommendations', innerSelector: '[data-recommendation-tabs]', innerLabel: '自定义推荐料理' },
  { value: 'favorites', label: '推荐料理 · 收藏管理', topValue: 'recommendations', innerSelector: '[data-recommendation-tabs]', innerLabel: '收藏管理' },
  { value: 'service', label: '经营中' },
  { value: 'missions', label: '扩展功能 · 任务列表', topValue: 'extensions', innerSelector: '[data-extension-tabs]', innerLabel: '任务列表' },
  { value: 'rare-invitations', label: '扩展功能 · 稀客邀请', topValue: 'extensions', innerSelector: '[data-extension-tabs]', innerLabel: '稀客邀请' },
  { value: 'inventory', label: '扩展功能 · 修改', topValue: 'extensions', innerSelector: '[data-extension-tabs]', innerLabel: '修改' },
  { value: 'logs', label: '日志' },
  { value: 'settings', label: '设置 · 窗口', topValue: 'settings', innerSelector: '[data-settings-tabs]', innerLabel: '窗口' },
  { value: 'connection', label: '设置 · 连接', topValue: 'settings', innerSelector: '[data-settings-tabs]', innerLabel: '连接' },
  { value: 'help', label: '设置 · 帮助', topValue: 'settings', innerSelector: '[data-settings-tabs]', innerLabel: '帮助' },
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

const browser = await chromium.launch({
  headless: true,
  ...(CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: CHROMIUM_EXECUTABLE_PATH }
    : {}),
});
const issues = [];
const screenshots = [];

await mkdir(OUTPUT_DIR, { recursive: true });

for (const viewport of viewports) {
  const page = await browser.newPage({ viewport: { width: viewport.width, height: viewport.height } });
  await page.addInitScript(seedLocalStorage, { apiUrl: API_URL, apiToken: API_TOKEN, storagePrefix: STORAGE_PREFIX });
  await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.body.innerText.includes('1.0.5'), null, { timeout: 10000 });
  await ensureSecondaryAuditDevice(page);
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

async function ensureSecondaryAuditDevice(page) {
  await page.evaluate(async ({ apiUrl, apiToken }) => {
    const primaryHeaders = {
      'X-Mystia-Steward-Companion-Token': apiToken,
      'X-Mystia-Steward-Companion-Client-Id': 'ui-audit-device-0001',
      'X-Mystia-Steward-Companion-Client-Label': 'UI audit primary',
    };
    let state = null;
    for (let attempt = 0; attempt < 50; attempt += 1) {
      const stateResponse = await fetch(`${apiUrl}/devices`, { cache: 'no-store', headers: primaryHeaders });
      if (stateResponse.ok) {
        state = await stateResponse.json();
        break;
      }
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
    if (!state) throw new Error('current UI audit device did not finish registration');
    if (state.devices.some((device) => device.deviceId === 'ui-audit-device-0002')) return;
    const registerResponse = await fetch(`${apiUrl}/devices/register`, {
      method: 'POST',
      headers: {
        ...primaryHeaders,
        'X-Mystia-Steward-Companion-Client-Id': 'ui-audit-device-0002',
        'X-Mystia-Steward-Companion-Client-Label': 'Android audit device',
        'Content-Type': 'application/json; charset=utf-8',
      },
      body: JSON.stringify({
        protocolVersion: state.protocolVersion,
        profileSchemaVersion: state.profileSchemaVersion,
        platform: 'android',
        appVersion: state.devices.find((device) => device.isCurrent)?.appVersion ?? 'unknown',
        profile: {
          ...state.activeProfile,
          automationEnabled: !state.activeProfile.automationEnabled,
        },
      }),
    });
    if (!registerResponse.ok) throw new Error(`secondary device registration HTTP ${registerResponse.status}`);
  }, { apiUrl: API_URL, apiToken: API_TOKEN });
}

function seedLocalStorage({ apiUrl, apiToken, storagePrefix }) {
  localStorage.setItem(`${storagePrefix}-mod-api-endpoint`, apiUrl);
  localStorage.setItem(`${storagePrefix}-mod-api-token`, apiToken);
  localStorage.setItem(`${storagePrefix}-client-id`, 'ui-audit-device-0001');
  localStorage.setItem(`${storagePrefix}-show-debug-details`, '1');
  localStorage.setItem(`${storagePrefix}-mission-list-module-enabled`, '1');
  localStorage.setItem(`${storagePrefix}-rare-guest-invitation-module-enabled`, '1');
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
  localStorage.setItem(`${storagePrefix}-rare-game-ui-pinning`, '1');
  localStorage.setItem(`${storagePrefix}-normal-game-ui-pinning`, '1');
  localStorage.setItem(`${storagePrefix}-recommended-extra-ingredient-fill`, '1');
  localStorage.setItem(`${storagePrefix}-cooker-highlight`, '1');
  localStorage.setItem(`${storagePrefix}-seat-highlight`, '1');
  localStorage.setItem(`${storagePrefix}-background-opacity`, '0.82');
  localStorage.setItem(`${storagePrefix}-content-opacity`, '1');
}

async function activateTab(page, tab) {
  const trigger = page.locator(`[data-gamepad-tab-value="${tab.topValue ?? tab.value}"]`).first();
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
  if (tab.innerSelector && tab.innerLabel) {
    const innerTrigger = page.locator(tab.innerSelector).getByRole('tab', {
      name: tab.innerLabel,
      exact: true,
    });
    if (!(await innerTrigger.count())) {
      issues.push({
        viewport: page.viewportSize()?.width || 0,
        tab: tab.label,
        component: 'TabsTrigger',
        message: `未找到 ${tab.label} 二级页签入口。`,
      });
      return;
    }
    await innerTrigger.scrollIntoViewIfNeeded();
    await innerTrigger.click();
  }
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
  await auditMissionRecipePriorityMarker(page, viewport, tab);
  await auditServiceDiagnosticsPlacement(page, viewport, tab);
  await auditRareGuestInvitationLayout(page, viewport, tab);
  await auditDeviceAuthorityLayout(page, viewport, tab);

  for (const target of hoverTargets) {
    await auditHoverTarget(page, viewport, tab, target);
  }

  await auditSelectDropdown(page, viewport, tab);
}

async function auditDeviceAuthorityLayout(page, viewport, tab) {
  if (tab.value !== 'connection') return;

  await page.locator('[data-device-authority-content]').waitFor({ timeout: 5_000 }).catch(() => {});
  const result = await page.evaluate(() => {
    const content = document.querySelector('[data-device-authority-content]');
    if (!(content instanceof HTMLElement)) return { ok: false, reason: 'missing-content' };
    const rows = Array.from(content.querySelectorAll('[data-device-authority-device]'))
      .filter((element) => element instanceof HTMLElement);
    return {
      ok: rows.length > 0
        && rows.every((row) => row.scrollWidth <= row.clientWidth + 1),
      rowCount: rows.length,
      overflowingRows: rows
        .filter((row) => row.scrollWidth > row.clientWidth + 1)
        .map((row) => row.dataset.deviceAuthorityDevice || ''),
    };
  });
  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'DeviceAuthority',
      message: `设备权威列表缺失或溢出：${JSON.stringify(result)}`,
    });
  }
  if (viewport.name !== 'desktop') return;
  const setPrimaryButton = page.getByRole('button', { name: '设为主设备', exact: true }).first();
  if (!(await setPrimaryButton.count()) || !(await setPrimaryButton.isEnabled())) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'DeviceAuthority',
      message: '在线且配置不同的非主设备没有可用的主设备切换入口。',
    });
    return;
  }
  await setPrimaryButton.click();
  const dialog = page.getByRole('dialog').filter({ hasText: '切换主设备' });
  await dialog.waitFor({ state: 'visible', timeout: 2_000 }).catch(() => {});
  const dialogVisible = await dialog.isVisible();
  if (dialogVisible) {
    await page.waitForFunction((element) => getComputedStyle(element).opacity === '1', await dialog.elementHandle(), {
      timeout: 2_000,
    }).catch(() => {});
  }
  const dialogText = dialogVisible ? await dialog.innerText() : '';
  if (!dialogVisible || !dialogText.includes('目标设备的配置与当前主设备不同')) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'DeviceAuthority',
      message: '配置不同的主设备切换没有显示确认与风险提示。',
    });
  }
  const dialogSurface = dialogVisible
    ? await dialog.evaluate((element) => {
        const header = element.querySelector('.mantine-Modal-header');
        const overlay = document.querySelector('.mantine-Modal-overlay');
        const inspect = (target) => {
          if (!(target instanceof HTMLElement)) return null;
          const style = getComputedStyle(target);
          return {
            backgroundColor: style.backgroundColor,
            opacity: style.opacity,
          };
        };
        return {
          content: inspect(element),
          header: inspect(header),
          overlay: inspect(overlay),
        };
      })
    : null;
  if (!dialogSurface
    || !isOpaqueCssColor(dialogSurface.content?.backgroundColor)
    || !isOpaqueCssColor(dialogSurface.header?.backgroundColor)
    || dialogSurface.content?.opacity !== '1'
    || dialogSurface.header?.opacity !== '1'
    || isTransparentCssColor(dialogSurface.overlay?.backgroundColor)) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'DialogSurface',
      message: `主设备切换弹窗没有形成实体内容层或有效遮罩：${JSON.stringify(dialogSurface)}`,
    });
  }
  const dialogScreenshot = path.join(OUTPUT_DIR, 'desktop-connection-primary-dialog.png');
  await page.screenshot({ path: dialogScreenshot, fullPage: true });
  screenshots.push({ tab: `${tab.label} · 切换确认`, viewport: viewport.name, path: dialogScreenshot });
  await dialog.getByRole('button', { name: '取消', exact: true }).click();
}

function isOpaqueCssColor(value) {
  if (!value || isTransparentCssColor(value)) return false;
  const alpha = readCssColorAlpha(value);
  return alpha !== null && alpha >= 0.999;
}

function isTransparentCssColor(value) {
  if (!value || value === 'transparent') return true;
  const alpha = readCssColorAlpha(value);
  return alpha !== null && alpha <= 0.001;
}

function readCssColorAlpha(value) {
  const rgba = value.match(/^rgba?\((.+)\)$/i);
  if (rgba) {
    const parts = rgba[1].split(/[,/]/).map((part) => part.trim()).filter(Boolean);
    return parts.length >= 4 ? Number(parts.at(-1)) : 1;
  }
  const colorFunction = value.match(/^color\([^/]+(?:\/\s*([\d.]+%?))?\)$/i);
  if (colorFunction) {
    if (!colorFunction[1]) return 1;
    return colorFunction[1].endsWith('%')
      ? Number(colorFunction[1].slice(0, -1)) / 100
      : Number(colorFunction[1]);
  }
  return null;
}

async function auditServiceDiagnosticsPlacement(page, viewport, tab) {
  if (tab.value !== 'service') return;

  const unexpectedRecommendationPanels = await page.getByText(/^(当前稀客|当前稀客点单|预计厨具占用)$/).count();
  if (unexpectedRecommendationPanels > 0) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'ServiceDiagnostics',
      message: '推荐视图仍显示只应位于诊断视图的原始运行态面板。',
    });
  }

  const serviceViewControl = page.locator('[data-slot="segmented-control"]').filter({ hasText: '诊断' }).first();
  await serviceViewControl.locator('label').filter({ hasText: /^诊断$/ }).click();
  for (const title of ['当前稀客', '当前稀客点单', '预计厨具占用']) {
    if (!(await page.getByText(title, { exact: true }).count())) {
      issues.push({
        viewport: viewport.name,
        tab: tab.label,
        component: 'ServiceDiagnostics',
        message: `诊断视图缺少“${title}”面板。`,
      });
    }
  }

  await serviceViewControl.locator('label').filter({ hasText: /^自动化$/ }).click();
  if (await page.getByText('预计厨具占用', { exact: true }).count()) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'ServiceDiagnostics',
      message: '自动化视图仍重复显示预计厨具占用。',
    });
  }
  await serviceViewControl.locator('label').filter({ hasText: /^推荐$/ }).click();
}

async function auditRareGuestInvitationLayout(page, viewport, tab) {
  if (tab.value !== 'rare-invitations') return;

  await page.locator('[data-rare-invitation-content]').waitFor({ timeout: 5_000 }).catch(() => {});
  const result = await page.evaluate(() => {
    const content = document.querySelector('[data-rare-invitation-content]');
    if (!(content instanceof HTMLElement)) return { ok: false, reason: 'missing-content' };
    const sections = Array.from(content.querySelectorAll(':scope > [data-rare-invitation-section]'))
      .filter((element) => element instanceof HTMLElement);
    const order = sections.map((element) => element.dataset.rareInvitationSection || '');
    const candidateRows = Array.from(content.querySelectorAll('[data-rare-invitation-candidate]'))
      .filter((element) => element instanceof HTMLElement);
    const invitedEntries = content.querySelectorAll('[data-rare-invitation-invited-id]');
    const search = content.querySelector('[data-rare-invitation-search]');
    return {
      ok: order.join('|') === 'invited|filters|available|unavailable'
        && invitedEntries.length > 0
        && search instanceof HTMLInputElement
        && candidateRows.every((row) => row.scrollWidth <= row.clientWidth + 1),
      order,
      invitedCount: invitedEntries.length,
      hasSearch: search instanceof HTMLInputElement,
      candidateCount: candidateRows.length,
      overflowingCandidates: candidateRows
        .filter((row) => row.scrollWidth > row.clientWidth + 1)
        .map((row) => row.dataset.rareInvitationCandidate || ''),
    };
  });
  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'RareGuestInvitationLayout',
      message: `稀客邀请信息层级或候选换行异常：${JSON.stringify(result)}。`,
    });
  }
}

async function auditMinimumViewportLayout(page, viewport, tab) {
  if (viewport.name !== 'minimum') return;

  await auditMinimumMulticolumnGrids(page, viewport, tab);
  await auditMinimumNestedTabsLayout(page, viewport, tab);

  if (tab.value === 'service') {
    await auditMinimumServiceSummaryGrid(page, viewport, tab);
  }

  if (tab.value === 'overview') {
    await auditMinimumShellGutter(page, viewport, tab);
    await auditMinimumHeaderLayout(page, viewport, tab);
    await auditMinimumPrimaryTabsLayout(page, viewport, tab);
  }

  if (tab.value === 'settings') {
    await auditMinimumRecommendationSettingsLayout(page, viewport, tab);
  }
}

async function auditMinimumNestedTabsLayout(page, viewport, tab) {
  const result = await inspectMinimumNestedTabsLayout(page);
  const expected = tab.value !== 'logs';
  if (result.ok && (!expected || result.listCount > 0)) return;

  issues.push({
    viewport: viewport.name,
    tab: tab.label,
    component: 'NestedTabsLayout',
    message: result.listCount === 0
      ? '最小宽度下未找到应显示的二级或三级页签。'
      : `最小宽度下二级或三级页签未等分占满整行：${JSON.stringify(result.failures).slice(0, 500)}`,
  });
}

async function auditMinimumServiceSummaryGrid(page, viewport, tab) {
  const result = await page.evaluate(() => {
    const grid = document.querySelector('[data-service-summary-grid="true"]');
    if (!(grid instanceof HTMLElement)) return { ok: false, reason: 'missing-grid' };

    const children = Array.from(grid.children).filter((node) => node instanceof HTMLElement);
    const gridRect = grid.getBoundingClientRect();
    const rects = children.map((child) => child.getBoundingClientRect());
    const rowTops = [];
    for (const rect of rects) {
      if (!rowTops.some((top) => Math.abs(top - rect.top) <= 2)) rowTops.push(rect.top);
    }
    const columnCount = getComputedStyle(grid).gridTemplateColumns
      .trim()
      .split(/\s+/)
      .filter(Boolean)
      .length;
    const contained = rects.every((rect) => (
      rect.left >= gridRect.left - 1
      && rect.right <= gridRect.right + 1
      && rect.top >= gridRect.top - 1
      && rect.bottom <= gridRect.bottom + 1
    ));

    return {
      ok: children.length === 6
        && columnCount === 3
        && rowTops.length === 2
        && contained
        && grid.scrollWidth <= grid.clientWidth + 1,
      childCount: children.length,
      columnCount,
      rowCount: rowTops.length,
      contained,
      clientWidth: grid.clientWidth,
      scrollWidth: grid.scrollWidth,
    };
  });

  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'ServiceSummaryGrid',
      message: result.reason || `经营中顶部六项摘要未保持三列两行：${JSON.stringify(result)}`,
    });
  }
}

async function auditMinimumMulticolumnGrids(page, viewport, tab) {
  const result = await page.evaluate(({ tabValue }) => {
    const expectedTabs = new Set(['overview', 'normal', 'rare', 'custom-recipes', 'service', 'missions', 'rare-invitations', 'inventory', 'settings', 'logs']);
    const candidates = Array.from(document.querySelectorAll('.steward-minimum-multicolumn-grid'))
      .filter((node) => node instanceof HTMLElement)
      .filter((element) => isVisible(element));

    const checked = [];
    const failures = [];
    for (const element of candidates) {
      const gridStyle = window.getComputedStyle(element);
      const columnCount = countGridTracks(gridStyle.gridTemplateColumns);
      const summary = {
        text: normalizeText(element.textContent || '').slice(0, 40),
        columns: gridStyle.gridTemplateColumns,
        columnCount,
      };
      checked.push(summary);
      if (columnCount < 2) failures.push(summary);
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

    function countGridTracks(template) {
      if (!template || template === 'none') return 0;
      let depth = 0;
      let tracks = 0;
      let inTrack = false;
      for (const character of template.trim()) {
        if (character === '(') depth += 1;
        if (character === ')') depth = Math.max(0, depth - 1);
        if (/\s/.test(character) && depth === 0) {
          if (inTrack) tracks += 1;
          inTrack = false;
        } else {
          inTrack = true;
        }
      }
      return tracks + (inTrack ? 1 : 0);
    }
  }, { tabValue: tab.value });

  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'MulticolumnLayout',
      message: result.checkedCount === 0 && result.expected
        ? '最小宽度下未找到带 steward-minimum-multicolumn-grid 语义标记的可见网格。'
        : `最小宽度多列网格未保持至少双列：${JSON.stringify(result.failures).slice(0, 300)}`,
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
    if (gridChildren.length < 2 || toolbarChildren.length !== 4 || statusChildren.length !== 3) {
      return { ok: false, reason: 'Header 工具条或状态摘要项目数量不符合预期。' };
    }

    const [brandRect, toolbarRect] = gridChildren.map((node) => node.getBoundingClientRect());
    const headerRect = header.getBoundingClientRect();
    const toolbarRects = toolbarChildren.map((node) => node.getBoundingClientRect());
    const statusRects = statusChildren.map((node) => node.getBoundingClientRect());
    const viewportWidth = document.documentElement.clientWidth;
    const toolbarStacked = toolbarRect.top >= brandRect.bottom - 1;
    const toolbarSameRow = toolbarRects.every((rect) => (
      Math.abs(rect.top - toolbarRects[0].top) <= 2
      && Math.abs(rect.bottom - toolbarRects[0].bottom) <= 2
    ));
    const toolbarOrdered = toolbarRects.slice(1).every((rect, index) => (
      rect.left >= toolbarRects[index].right - 1
    ));
    const toolbarContained = toolbar.scrollWidth <= toolbar.clientWidth + 1
      && toolbar.scrollHeight <= toolbar.clientHeight + 1;
    const statusSameRow = statusRects.every((rect) => (
      Math.abs(rect.top - statusRects[0].top) <= 2
      && Math.abs(rect.bottom - statusRects[0].bottom) <= 2
    ));
    const statusOrdered = statusRects.slice(1).every((rect, index) => (
      rect.left >= statusRects[index].right - 1
    ));
    const statusColumnCount = getComputedStyle(statusGrid).gridTemplateColumns.trim().split(/\s+/).filter(Boolean).length;
    const statusContained = statusGrid.scrollWidth <= statusGrid.clientWidth + 1
      && statusGrid.scrollHeight <= statusGrid.clientHeight + 1;
    const containedRects = [brandRect, toolbarRect, ...toolbarRects, ...statusRects];
    const contained = containedRects.every((rect) => (
      rect.left >= headerRect.left - 1
      && rect.right <= headerRect.right + 1
      && rect.left >= -1
      && rect.right <= viewportWidth + 1
    ));
    const usable = toolbarRects[0].width >= 136
      && toolbarRects[1].width >= 112
      && toolbarRects.slice(2).every((rect) => rect.width >= 32 && rect.height >= 24)
      && statusRects.every((rect) => rect.width >= 150 && rect.height >= 24);

    return {
      ok: toolbarStacked
        && toolbarSameRow
        && toolbarOrdered
        && toolbarContained
        && statusSameRow
        && statusOrdered
        && statusColumnCount === 3
        && statusContained
        && contained
        && usable,
      toolbarStacked,
      toolbarSameRow,
      toolbarOrdered,
      toolbarContained,
      statusSameRow,
      statusOrdered,
      statusColumnCount,
      statusContained,
      contained,
      usable,
      brandBottom: Math.round(brandRect.bottom),
      toolbarTop: Math.round(toolbarRect.top),
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
      message: result.reason || `最小宽度 Header 紧凑布局异常：toolbarStacked=${result.toolbarStacked}，toolbarSameRow=${result.toolbarSameRow}，toolbarOrdered=${result.toolbarOrdered}，toolbarContained=${result.toolbarContained}，statusSameRow=${result.statusSameRow}，statusOrdered=${result.statusOrdered}，statusColumns=${result.statusColumnCount}，statusContained=${result.statusContained}，contained=${result.contained}，usable=${result.usable}，brandBottom/toolbarTop=${result.brandBottom}/${result.toolbarTop}，toolbarTops=${result.toolbarTops?.join('/')}，statusWidths=${result.statusWidths?.join('/')}`,
    });
  }
}

async function auditMinimumPrimaryTabsLayout(page, viewport, tab) {
  const result = await inspectMinimumPrimaryTabsLayout(page);

  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'PrimaryTabsLayout',
      message: result.reason || `最小宽度一级导航未完整显示：count=${result.triggerCount}，missing=${result.missingValues?.join('/')}，unexpected=${result.unexpectedValues?.join('/')}，display=${result.display}，columns/rows=${result.columnCount}/${result.rowCount}，contained=${result.failures?.length === 0}，internalOverflow=${!result.noInternalOverflow}，client=${result.clientSize?.join('x')}，scroll=${result.scrollSize?.join('x')}。`,
    });
  }
}

async function auditMinimumRecommendationSettingsLayout(page, viewport, tab) {
  const windowTab = page.getByRole('tab', { name: '窗口', exact: true }).first();
  const recommendationTab = page.getByRole('tab', { name: '推荐', exact: true }).first();
  const experimentalTab = page.getByRole('tab', { name: '实验性功能', exact: true }).first();
  if (!(await windowTab.count()) || !(await recommendationTab.count()) || !(await experimentalTab.count())) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'SettingsRecommendation',
      message: '未找到设置页窗口、推荐或实验性功能分栏入口。',
    });
    return;
  }

  await windowTab.click();
  await page.waitForTimeout(100);
  await auditMinimumSettingSegmentedControls(page, viewport, tab, '窗口', ['焦点切换', '主题']);

  await recommendationTab.click();
  await page.waitForTimeout(200);
  await auditMinimumMulticolumnGrids(page, viewport, { ...tab, label: `${tab.label} 推荐` });
  await auditMinimumSettingSegmentedControls(
    page,
    viewport,
    tab,
    '推荐',
    ['经营中订单排序', '预算处理', '权重方案'],
  );

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

  await auditMissionRecipePriorityToggle(page, viewport, tab, recommendationTab);

  await activateTab(page, { value: 'settings', label: '设置' });
  await page.getByRole('tab', { name: '实验性功能', exact: true }).first().click();
  await page.waitForTimeout(200);
  await auditMinimumMulticolumnGrids(page, viewport, { ...tab, label: `${tab.label} 实验性功能` });
  for (const title of ['自动化总控', '游戏界面辅助', '稀客自动化设置', '普客自动化设置']) {
    if (!(await page.getByText(title, { exact: true }).count())) {
      issues.push({
        viewport: viewport.name,
        tab: tab.label,
        component: 'SettingsExperimental',
        message: `实验性功能分栏缺少“${title}”面板。`,
      });
    }
  }

  const experimentalScreenshotPath = path.join(OUTPUT_DIR, `${viewport.name}-${tab.value}-experimental.png`);
  await page.screenshot({ path: experimentalScreenshotPath, fullPage: true });
  screenshots.push({ tab: `${tab.label} 实验性功能`, viewport: viewport.name, path: experimentalScreenshotPath });
}

async function auditMissionRecipePriorityMarker(page, viewport, tab) {
  if (tab.value !== 'service') return;

  try {
    await page.getByText('任务目标', { exact: true }).first().waitFor({
      state: 'visible',
      timeout: 5000,
    });
    await waitForPrimaryRecipe(page, '牛肉火锅');
  } catch {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'MissionRecipePriority',
      message: '默认开启任务料理置顶时，任务目标未成为带标识的主计划。',
    });
  }
}

async function auditMissionRecipePriorityToggle(page, viewport, tab, recommendationTab) {
  const storageKey = `${STORAGE_PREFIX}-mission-recipe-priority`;
  const field = page.locator('label.steward-switch-field').filter({ hasText: '任务料理置顶' }).first();
  const input = field.locator('input[type="checkbox"]').first();
  if (!(await field.count()) || !(await input.count())) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'MissionRecipePriority',
      message: '推荐设置中未找到“任务料理置顶”开关。',
    });
    return;
  }
  if (!(await input.isChecked())) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'MissionRecipePriority',
      message: '“任务料理置顶”首次读取未保持默认开启。',
    });
    return;
  }

  await field.click();
  await page.waitForFunction((key) => localStorage.getItem(key) === '0', storageKey);
  await activateTab(page, { value: 'service', label: '经营中' });
  await page.getByText('推荐料理', { exact: true }).first().waitFor({ state: 'visible' });
  await page.waitForTimeout(600);
  if (await page.getByText('任务目标', { exact: true }).count()) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'MissionRecipePriority',
      message: '关闭任务料理置顶后，主计划仍显示“任务目标”标识。',
    });
  }
  try {
    await waitForPrimaryRecipe(page, '蜂蜜蛋糕');
  } catch {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'MissionRecipePriority',
      message: '关闭任务料理置顶后，主计划未恢复原有自定义置顶排序。',
    });
  }

  await activateTab(page, { value: 'settings', label: '设置' });
  await recommendationTab.click();
  const restoredField = page.locator('label.steward-switch-field').filter({ hasText: '任务料理置顶' }).first();
  await restoredField.click();
  await page.waitForFunction((key) => localStorage.getItem(key) === '1', storageKey);
  await activateTab(page, { value: 'service', label: '经营中' });
  try {
    await page.getByText('任务目标', { exact: true }).first().waitFor({
      state: 'visible',
      timeout: 5000,
    });
    await waitForPrimaryRecipe(page, '牛肉火锅');
  } catch {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'MissionRecipePriority',
      message: '重新开启任务料理置顶后，Worker 未恢复带标识的任务主计划。',
    });
  }
}

async function waitForPrimaryRecipe(page, expectedName) {
  await page.waitForFunction((name) => {
    const primaryRow = Array.from(document.querySelectorAll('.steward-data-row'))
      .find((element) => element.textContent?.trim().startsWith('#1'));
    return primaryRow?.textContent?.includes(name) === true;
  }, expectedName, { timeout: 5000 });
}

async function auditMinimumSettingSegmentedControls(page, viewport, tab, section, expectedLabels) {
  const result = await page.evaluate(({ labels }) => {
    const controls = Array.from(document.querySelectorAll('.steward-settings-segmented-control'))
      .filter((node) => node instanceof HTMLElement)
      .filter((element) => {
        const rect = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
      });

    const summaries = controls.map((control) => {
      const field = control.parentElement;
      const label = field?.firstElementChild?.textContent?.trim() || '';
      const items = Array.from(control.querySelectorAll('.mantine-SegmentedControl-control'))
        .filter((node) => node instanceof HTMLElement);
      const itemLabels = items.map((item) => item.querySelector('.mantine-SegmentedControl-label'));
      const innerLabels = items.map((item) => item.querySelector('.mantine-SegmentedControl-innerLabel'));
      const activeItem = items.find((item) => item.getAttribute('data-active') === 'true');
      const indicator = control.querySelector('.mantine-SegmentedControl-indicator');
      const controlRect = control.getBoundingClientRect();
      const itemRects = items.map((item) => item.getBoundingClientRect());
      const labelRects = itemLabels.map((item) => item?.getBoundingClientRect());
      const activeRect = activeItem?.getBoundingClientRect();
      const indicatorRect = indicator?.getBoundingClientRect();
      const within = (left, right, tolerance = 1) => left >= controlRect.left - tolerance
        && right <= controlRect.right + tolerance;
      const same = (left, right, tolerance = 2) => Math.abs(left - right) <= tolerance;
      const ordered = itemRects.slice(1).every((rect, index) => rect.left >= itemRects[index].right - 1);
      const labelsMatchItems = labelRects.every((rect, index) => rect
        && same(rect.left, itemRects[index].left)
        && same(rect.right, itemRects[index].right)
        && same(rect.top, itemRects[index].top)
        && same(rect.bottom, itemRects[index].bottom));
      const singleLine = innerLabels.every((inner) => {
        if (!(inner instanceof HTMLElement)) return false;
        const rect = inner.getBoundingClientRect();
        const lineHeight = Number.parseFloat(getComputedStyle(inner).lineHeight);
        return getComputedStyle(inner).whiteSpace === 'nowrap'
          && rect.height <= lineHeight + 1
          && inner.scrollWidth <= inner.clientWidth + 1
          && inner.scrollHeight <= inner.clientHeight + 1;
      });
      const indicatorMatchesActive = activeRect && indicatorRect
        ? same(indicatorRect.left, activeRect.left)
          && same(indicatorRect.right, activeRect.right)
          && same(indicatorRect.top, activeRect.top)
          && same(indicatorRect.bottom, activeRect.bottom)
        : false;

      return {
        label,
        optionCount: items.length,
        width: Math.round(controlRect.width),
        noOverflow: control.scrollWidth <= control.clientWidth + 1
          && control.scrollHeight <= control.clientHeight + 1,
        contained: itemRects.every((rect) => within(rect.left, rect.right)),
        ordered,
        labelsMatchItems,
        singleLine,
        indicatorMatchesActive,
      };
    });
    const actualLabels = summaries.map((summary) => summary.label);
    const failures = summaries.filter((summary) => !summary.noOverflow
      || !summary.contained
      || !summary.ordered
      || !summary.labelsMatchItems
      || !summary.singleLine
      || !summary.indicatorMatchesActive);

    return {
      ok: actualLabels.length === labels.length
        && labels.every((label, index) => actualLabels[index] === label)
        && failures.length === 0,
      expectedLabels: labels,
      actualLabels,
      summaries,
      failures,
    };
  }, { labels: expectedLabels });

  if (!result.ok) {
    issues.push({
      viewport: viewport.name,
      tab: tab.label,
      component: 'SettingsSegmentedControl',
      message: `设置-${section}分段控件最小宽度布局异常：expected=${result.expectedLabels.join('/')}，actual=${result.actualLabels.join('/')}，failures=${JSON.stringify(result.failures).slice(0, 500)}`,
    });
  }
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
