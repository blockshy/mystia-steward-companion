import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

import { inspectMinimumPrimaryTabsLayout } from '../ui-layout/primary-tabs-layout.mjs';

const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.TRACKED_MISSIONS_AUDIT_OUTPUT_DIR
  || '/tmp/mystia-companion-tracked-missions-audit';
const STORAGE_PREFIX = 'mystia-steward-companion';
const requests = [];
const availableRequests = [];
const signatures = {
  initial: '1'.repeat(64),
  refreshed: '2'.repeat(64),
  stale: '3'.repeat(64),
  current: '4'.repeat(64),
  available: '5'.repeat(64),
  availableStale: '6'.repeat(64),
  availableUnavailable: '7'.repeat(64),
  trackedUnavailable: '8'.repeat(64),
};

await mkdir(OUTPUT_DIR, { recursive: true });
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 640, height: 760 } });

try {
  let requestSequence = 0;
  let delayNextAvailableResponse = false;
  let availableUnavailableReadsRemaining = 0;
  let trackedUnavailable = false;
  await page.route('**/missions/available**', async (route) => {
    if (route.request().method() !== 'GET') {
      await route.continue();
      return;
    }
    const url = new URL(route.request().url());
    availableRequests.push({
      method: route.request().method(),
      knownSignature: url.searchParams.get('knownSignature'),
    });
    if (delayNextAvailableResponse) {
      delayNextAvailableResponse = false;
      await new Promise((resolve) => setTimeout(resolve, 900));
      await fulfillAvailableMissionResponse(
        route,
        signatures.availableStale,
        '不应显示的迟到可接取任务',
      ).catch(() => {});
      return;
    }
    if (availableUnavailableReadsRemaining > 0) {
      availableUnavailableReadsRemaining -= 1;
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          ok: false,
          runtimeAvailable: false,
          status: 'waiting-for-load',
          missionGeneration: 1,
          daySceneGeneration: 1,
          contentSignature: signatures.availableUnavailable,
          availableCount: 0,
          missions: [],
          error: 'test-runtime-not-ready',
        }),
      });
      return;
    }
    if (url.searchParams.get('knownSignature') === signatures.available) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          unchanged: true,
          contentSignature: signatures.available,
        }),
      });
      return;
    }
    await fulfillAvailableMissionResponse(route, signatures.available, '可接取的美铃任务');
  });

  await page.route('**/missions/tracked**', async (route) => {
    if (route.request().method() !== 'GET') {
      await route.continue();
      return;
    }

    requestSequence += 1;
    const sequence = requestSequence;
    const url = new URL(route.request().url());
    requests.push({
      sequence,
      method: route.request().method(),
      knownSignature: url.searchParams.get('knownSignature'),
    });

    if (trackedUnavailable) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          ok: false,
          runtimeAvailable: false,
          generation: 9,
          status: 'waiting-for-load',
          contentSignature: signatures.trackedUnavailable,
          unverifiedCount: 0,
          trackingCount: 0,
          fulfilledCount: 0,
          missions: [],
          error: 'test-tracked-runtime-not-ready',
        }),
      });
      return;
    }

    if (sequence === 3) {
      await new Promise((resolve) => setTimeout(resolve, 900));
      await fulfillMissionResponse(route, signatures.stale, '不应显示的迟到任务').catch(() => {});
      return;
    }

    if (sequence >= 5 && url.searchParams.get('knownSignature') === signatures.current) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          unchanged: true,
          contentSignature: signatures.current,
        }),
      });
      return;
    }

    const response = sequence === 1
      ? [signatures.initial, '初次自动读取任务', true]
      : sequence === 2
        ? [signatures.refreshed, '手动刷新后的任务', false]
        : [signatures.current, '切回后的当前任务', true];
    await fulfillMissionResponse(route, response[0], response[1], response[2]);
  });

  await page.addInitScript(({ apiUrl, apiToken, storagePrefix }) => {
    localStorage.setItem(`${storagePrefix}-mod-api-endpoint`, apiUrl);
    localStorage.setItem(`${storagePrefix}-mod-api-token`, apiToken);
    localStorage.setItem(`${storagePrefix}-show-debug-details`, '1');
    localStorage.setItem(`${storagePrefix}-gamepad-navigation`, '1');
    localStorage.setItem(`${storagePrefix}-font-scale-percent`, '130');
  }, { apiUrl: API_URL, apiToken: API_TOKEN, storagePrefix: STORAGE_PREFIX });

  await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-gamepad-tab-value="overview"]', { timeout: 10_000 });
  await activateTopTab('missions');
  await page.getByText('初次自动读取任务', { exact: true }).waitFor({ timeout: 10_000 });
  assert.equal(requests[0]?.knownSignature, null, 'The first task read must request a full payload.');
  await assertMissionStatusTabs(
    { all: 5, available: 1, fulfilled: 1, tracking: 2, unverified: 1 },
    'all',
  );
  await assertVisibleMissionRows(
    ['available', 'fulfilled', 'tracking', 'tracking', 'unverified'],
    [
      'mission:available-meirin',
      'mission:fulfilled:初次自动读取任务',
      'mission:tracking-a',
      'mission:tracking-z',
      'mission:unverified',
    ],
  );
  await assertMissionPresentation();
  await assertMissionStatusTabFocusContract();

  const refresh = page.locator('[data-gamepad-focus-key="missions:tasks:refresh"]:visible').first();
  await refresh.focus();
  assert.equal(await refresh.evaluate((element) => document.activeElement === element), true);
  await selectMissionStatus('unverified');
  await refresh.click();
  await waitForRequestCount(2);
  await page.waitForFunction(() => (
    document.querySelector('[data-mission-status-tab-count="unverified"]')?.textContent === '0'
  ));
  assert.equal(requests[1]?.knownSignature, null, 'Manual refresh must bypass the compact unchanged response.');
  await assertMissionStatusTabs(
    { all: 4, available: 1, fulfilled: 1, tracking: 2, unverified: 0 },
    'unverified',
  );
  assert.equal(
    await page.getByLabel('任务列表', { exact: true })
      .getByText('当前没有待确认任务。', { exact: true })
      .count(),
    1,
    'The selected empty task status did not retain a deterministic empty state.',
  );
  assert.equal(
    await visibleMissionRows().count(),
    0,
    'The empty task status tab leaked rows from another status.',
  );

  const delayedAvailableRequestIndex = availableRequests.length;
  delayNextAvailableResponse = true;
  await refresh.click();
  await waitForRequestCount(3);
  await waitForAvailableRequestCount(delayedAvailableRequestIndex + 1);
  await page.getByRole('tab', { name: '稀客邀请', exact: true }).click();
  await page.getByText('当前场景', { exact: true }).waitFor({ timeout: 10_000 });
  await page.waitForTimeout(100);
  await page.getByRole('tab', { name: '任务列表', exact: true }).click();
  await page.getByText('切回后的当前任务', { exact: true }).waitFor({ timeout: 10_000 });
  await assertMissionStatusTabs(
    { all: 5, available: 1, fulfilled: 1, tracking: 2, unverified: 1 },
    'all',
  );
  await assertVisibleMissionRows(
    ['available', 'fulfilled', 'tracking', 'tracking', 'unverified'],
    [
      'mission:available-meirin',
      'mission:fulfilled:切回后的当前任务',
      'mission:tracking-a',
      'mission:tracking-z',
      'mission:unverified',
    ],
  );
  await page.waitForTimeout(1_000);
  assert.equal(
    await page.getByText('不应显示的迟到任务', { exact: true }).count(),
    0,
    'A response from the inactive task view replaced the current result.',
  );
  assert.equal(
    await page.getByText('不应显示的迟到可接取任务', { exact: true }).count(),
    0,
    'An available response from the inactive task view replaced the current result.',
  );

  await waitForRequestCount(5, 5_000);
  assert.equal(
    requests[4]?.knownSignature,
    signatures.current,
    'Periodic task polling must use the current content signature.',
  );
  await waitForAvailableRequestSignature(signatures.available, 5_000);
  assert.ok(requests.every((request) => request.method === 'GET'));
  assert.ok(availableRequests.every((request) => request.method === 'GET'));
  assert.equal(
    availableRequests.some((request) => request.knownSignature === null),
    true,
    'Available task initial/manual reads must support full responses.',
  );
  assert.equal(
    availableRequests.some((request) => request.knownSignature === signatures.available),
    true,
    'Available task polling must use its independent content signature.',
  );

  await selectMissionStatus('available');
  availableUnavailableReadsRemaining = 5;
  await refresh.click();
  await page.getByText('可接取任务：等待游戏完成存档任务初始化。', { exact: true })
    .waitFor({ timeout: 5_000 });
  assert.equal(
    await page.getByText('当前没有可接取任务。', { exact: true }).count(),
    0,
    'A transient available-task failure was misreported as a confirmed empty list.',
  );
  await page.getByText('可接取的美铃任务', { exact: true }).waitFor({ timeout: 15_000 });
  assert.equal(
    availableUnavailableReadsRemaining,
    0,
    'Available-task polling did not continue after the four fast retries were exhausted.',
  );

  await selectMissionStatus('tracking');
  trackedUnavailable = true;
  await refresh.click();
  await page.getByText('已追踪任务：test-tracked-runtime-not-ready', { exact: true })
    .waitFor({ timeout: 5_000 });
  assert.equal(
    await page.getByText('当前没有进行中任务。', { exact: true }).count(),
    0,
    'A tracked-task failure was misreported as a confirmed empty status.',
  );
  trackedUnavailable = false;
  await refresh.click();
  await page.getByText('已追踪任务：test-tracked-runtime-not-ready', { exact: true })
    .waitFor({ state: 'hidden', timeout: 5_000 });
  await selectMissionStatus('all');
  await page.getByText('切回后的当前任务', { exact: true }).waitFor({ timeout: 5_000 });

  const primaryTabs = await inspectMinimumPrimaryTabsLayout(page);
  assert.equal(primaryTabs.ok, true, `640px primary tab layout failed: ${JSON.stringify(primaryTabs)}`);
  const layout = await page.evaluate(() => {
    const innerTabs = Array.from(document.querySelectorAll('[data-slot="tabs-trigger"]'))
      .filter((element) => element instanceof HTMLElement)
      .filter((element) => ['任务列表', '稀客邀请'].includes(element.textContent?.trim() || ''));
    const refreshButton = document.querySelector('[data-gamepad-focus-key="missions:tasks:refresh"]');
    const statusTabList = document.querySelector('[aria-label="任务状态筛选"]');
    const statusTabs = Array.from(document.querySelectorAll('[data-mission-status-tab]'))
      .filter((element) => element instanceof HTMLElement);
    return {
      overflow: Math.max(0, document.documentElement.scrollWidth - document.documentElement.clientWidth),
      innerTabCount: innerTabs.length,
      innerTabsContained: innerTabs.every((element) => {
        const rect = element.getBoundingClientRect();
        return rect.left >= -1 && rect.right <= document.documentElement.clientWidth + 1;
      }),
      refreshFocusable: refreshButton instanceof HTMLElement && !refreshButton.hasAttribute('disabled'),
      statusTabCount: statusTabs.length,
      statusTabsContained: statusTabs.every((element) => {
        const rect = element.getBoundingClientRect();
        const listRect = statusTabList?.getBoundingClientRect();
        return listRect
          ? rect.left >= listRect.left - 1 && rect.right <= listRect.right + 1
          : false;
      }),
      statusTabsNoWrap: statusTabList instanceof HTMLElement
        && getComputedStyle(statusTabList).flexWrap === 'nowrap',
      statusTabsScrollable: statusTabList instanceof HTMLElement
        && statusTabList.dataset.scrollableTabs === 'true',
      statusFocusKeys: statusTabs.map((element) => element.dataset.gamepadFocusKey),
      visibleMissionRows: Array.from(
        document.querySelectorAll('[data-mission-status-list]:not([hidden]) [data-gamepad-row="true"]'),
      ).length,
    };
  });
  assert.deepEqual(layout, {
    overflow: 0,
    innerTabCount: 2,
    innerTabsContained: true,
    refreshFocusable: true,
    statusTabCount: 5,
    statusTabsContained: true,
    statusTabsNoWrap: true,
    statusTabsScrollable: true,
    statusFocusKeys: [
      'missions:tasks:status:all',
      'missions:tasks:status:available',
      'missions:tasks:status:fulfilled',
      'missions:tasks:status:tracking',
      'missions:tasks:status:unverified',
    ],
    visibleMissionRows: 5,
  });

  await page.screenshot({ path: `${OUTPUT_DIR}/minimum-tracked-missions.png`, fullPage: true });
  await page.setViewportSize({ width: 390, height: 760 });
  await selectMissionStatus('unverified');
  const narrowLayout = await page.evaluate(() => {
    const list = document.querySelector('[aria-label="任务状态筛选"]');
    const activeTab = list?.querySelector('[data-mission-status-tab][data-active]');
    const listRect = list?.getBoundingClientRect();
    const activeRect = activeTab?.getBoundingClientRect();
    return {
      overflow: Math.max(0, document.documentElement.scrollWidth - document.documentElement.clientWidth),
      fontScale: getComputedStyle(document.documentElement)
        .getPropertyValue('--companion-font-scale')
        .trim(),
      listContained: Boolean(listRect)
        && listRect.left >= -1
        && listRect.right <= document.documentElement.clientWidth + 1,
      noWrap: list instanceof HTMLElement && getComputedStyle(list).flexWrap === 'nowrap',
      overflowX: list instanceof HTMLElement && getComputedStyle(list).overflowX,
      activeContained: Boolean(listRect && activeRect)
        && activeRect.left >= listRect.left - 1
        && activeRect.right <= listRect.right + 1,
      activeFocusKey: activeTab instanceof HTMLElement ? activeTab.dataset.gamepadFocusKey : null,
    };
  });
  assert.equal(narrowLayout.overflow, 0, `390px page overflowed: ${JSON.stringify(narrowLayout)}`);
  assert.equal(narrowLayout.fontScale, '1.3');
  assert.equal(narrowLayout.listContained, true);
  assert.equal(narrowLayout.noWrap, true);
  assert.ok(
    narrowLayout.overflowX === 'auto' || narrowLayout.overflowX === 'scroll',
    `390px task status tabs cannot scroll horizontally: ${JSON.stringify(narrowLayout)}`,
  );
  assert.equal(narrowLayout.activeContained, true);
  assert.equal(narrowLayout.activeFocusKey, 'missions:tasks:status:unverified');
  await selectMissionStatus('all');
  const narrowPresentationLayout = await page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll(
      '[data-mission-status-list]:not([hidden]) [data-gamepad-row="true"]',
    )).filter((element) => element instanceof HTMLElement);
    const presentationElements = rows.flatMap((row) => Array.from(row.querySelectorAll(
      '[data-mission-character-name], [data-mission-related-scenes], [data-mission-presentation-debug]',
    )).filter((element) => element instanceof HTMLElement));
    const longSceneBadge = document.querySelector(
      '[data-mission-scene-name^="这个名称用于验证"]',
    );
    const longSceneLabel = longSceneBadge?.querySelector('[data-mission-scene-label]');
    const longSceneStyle = longSceneLabel instanceof HTMLElement
      ? getComputedStyle(longSceneLabel)
      : null;
    const longSceneRange = longSceneLabel instanceof HTMLElement
      ? document.createRange()
      : null;
    if (longSceneRange && longSceneLabel) {
      longSceneRange.selectNodeContents(longSceneLabel);
    }
    return {
      overflow: Math.max(0, document.documentElement.scrollWidth - document.documentElement.clientWidth),
      rowCount: rows.length,
      presentationCount: presentationElements.length,
      rowsContained: rows.every((row) => row.scrollWidth <= row.clientWidth + 1),
      presentationContained: presentationElements.every((element) => {
        const rect = element.getBoundingClientRect();
        const rowRect = element.closest('[data-gamepad-row="true"]')?.getBoundingClientRect();
        return rowRect
          ? rect.left >= rowRect.left - 1 && rect.right <= rowRect.right + 1
          : false;
      }),
      longScene: {
        badgeExists: longSceneBadge instanceof HTMLElement,
        labelExists: longSceneLabel instanceof HTMLElement,
        whiteSpace: longSceneStyle?.whiteSpace ?? null,
        labelClientWidth: longSceneLabel instanceof HTMLElement ? longSceneLabel.clientWidth : -1,
        labelScrollWidth: longSceneLabel instanceof HTMLElement ? longSceneLabel.scrollWidth : -1,
        lineRectCount: longSceneRange?.getClientRects().length ?? 0,
        badgeClientHeight: longSceneBadge instanceof HTMLElement ? longSceneBadge.clientHeight : -1,
        badgeScrollHeight: longSceneBadge instanceof HTMLElement ? longSceneBadge.scrollHeight : -1,
      },
    };
  });
  const { longScene, ...narrowPresentationBase } = narrowPresentationLayout;
  assert.deepEqual(narrowPresentationBase, {
    overflow: 0,
    rowCount: 5,
    presentationCount: 13,
    rowsContained: true,
    presentationContained: true,
  });
  assert.equal(longScene.badgeExists, true);
  assert.equal(longScene.labelExists, true);
  assert.equal(longScene.whiteSpace, 'normal');
  assert.ok(
    longScene.labelScrollWidth <= longScene.labelClientWidth + 1,
    `Long scene label overflowed horizontally: ${JSON.stringify(longScene)}`,
  );
  assert.ok(
    longScene.lineRectCount > 1,
    `Long scene label did not wrap: ${JSON.stringify(longScene)}`,
  );
  assert.ok(
    longScene.badgeScrollHeight <= longScene.badgeClientHeight + 1,
    `Long scene badge clipped its wrapped label: ${JSON.stringify(longScene)}`,
  );
  await page.screenshot({ path: `${OUTPUT_DIR}/narrow-tracked-missions.png`, fullPage: true });
  console.log(
    'PASS: task list merges available and tracked missions, refreshes both independent signatures, '
    + 'renders bounded character/related-scene metadata, cancels inactive reads, '
    + 'and keeps five scrollable status tabs.',
  );
} finally {
  await browser.close();
}

async function fulfillMissionResponse(route, contentSignature, title, includeUnverified = true) {
  const missions = [
    ...(includeUnverified
      ? [{
        label: 'unverified',
        title: '待确认的夜间任务',
        receiverLabel: '',
        characterName: '',
        sceneNames: [],
        presentationStatus: 'no-receiver',
        status: 'unverified',
        conditionCount: 1,
        completedConditionCount: null,
        conditionStates: [null],
      }]
      : []),
    {
      label: 'tracking-z',
      title: '同名进行中任务',
      receiverLabel: 'Reimu',
      characterName: '博丽灵梦',
      sceneNames: ['博丽神社'],
      presentationStatus: 'ready',
      status: 'tracking',
      conditionCount: 2,
      completedConditionCount: 1,
      conditionStates: [true, false],
    },
    {
      label: `fulfilled:${title}`,
      title,
      receiverLabel: 'Cirno',
      characterName: '琪露诺',
      sceneNames: ['雾之湖'],
      presentationStatus: 'ready',
      status: 'fulfilled',
      conditionCount: 2,
      completedConditionCount: 2,
      conditionStates: [true, true],
    },
    {
      label: 'tracking-a',
      title: '同名进行中任务',
      receiverLabel: 'Akyuu',
      characterName: '稗田阿求',
      sceneNames: ['人间之里', '这个名称用于验证最窄窗口中的相关场景徽标可以自然换行而不会撑宽任务列表'],
      presentationStatus: 'ready',
      status: 'tracking',
      conditionCount: 2,
      completedConditionCount: 1,
      conditionStates: [true, false],
    },
  ];
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      ok: true,
      runtimeAvailable: true,
      generation: 9,
      status: 'ready',
      contentSignature,
      unverifiedCount: includeUnverified ? 1 : 0,
      trackingCount: 2,
      fulfilledCount: 1,
      missions,
      error: null,
    }),
  });
}

async function fulfillAvailableMissionResponse(route, contentSignature, title) {
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      ok: true,
      runtimeAvailable: true,
      status: 'ready',
      missionGeneration: 1,
      daySceneGeneration: 1,
      contentSignature,
      availableCount: 2,
      missions: [
        {
          label: 'available-meirin',
          title,
          receiverLabel: 'Meirin',
          characterName: '红美铃',
          sceneNames: ['红魔馆'],
          presentationStatus: 'ready',
        },
        {
          label: 'tracking-a',
          title: '应由已追踪状态覆盖的任务',
          receiverLabel: 'Akyuu',
          characterName: '稗田阿求',
          sceneNames: ['人间之里'],
          presentationStatus: 'ready',
        },
      ],
      error: null,
    }),
  });
}

async function activateTopTab(value) {
  const trigger = page.locator(`[data-gamepad-tab-value="${value}"]`).first();
  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
}

async function assertMissionStatusTabs(expectedCounts, activeStatus) {
  const panel = page.getByLabel('任务列表', { exact: true });
  assert.deepEqual(
    await panel.locator('[data-mission-status-tab]').evaluateAll((elements) => elements.map(
      (element) => element.getAttribute('data-mission-status-tab'),
    )),
    ['all', 'available', 'fulfilled', 'tracking', 'unverified'],
    'Task status tabs did not retain their fixed order.',
  );

  for (const [status, count] of Object.entries(expectedCounts)) {
    assert.equal(
      await panel.locator(`[data-mission-status-tab-count="${status}"]`).textContent(),
      String(count),
      `Task status tab ${status} displayed an incorrect count.`,
    );
  }

  const selected = panel.locator(`[data-mission-status-tab="${activeStatus}"]`);
  assert.equal(
    await selected.getAttribute('aria-selected'),
    'true',
    `Task status tab ${activeStatus} is not the controlled active value.`,
  );
}

async function assertVisibleMissionRows(expectedStatuses, expectedKeys) {
  const rows = visibleMissionRows();
  assert.deepEqual(
    await rows.evaluateAll((elements) => elements.map(
      (element) => element.getAttribute('data-mission-status'),
    )),
    expectedStatuses,
    'The active task status tab contains rows from another view.',
  );
  assert.deepEqual(
    await rows.evaluateAll((elements) => elements.map(
      (element) => element.getAttribute('data-gamepad-row-key'),
    )),
    expectedKeys,
    'Task rows lost their stable status/title/label ordering.',
  );
}

async function assertMissionPresentation() {
  const availableRow = page.locator('[data-gamepad-row-key="mission:available-meirin"]:visible');
  await availableRow.getByText('任务角色', { exact: true }).waitFor();
  await availableRow.getByText('红美铃', { exact: true }).waitFor();
  await availableRow.getByText('相关场景', { exact: true }).waitFor();
  await availableRow.locator('[data-mission-scene-name="红魔馆"]').waitFor();
  assert.match(
    await availableRow.locator('[data-mission-presentation-debug]').innerText(),
    /receiverLabel=Meirin; presentationStatus=ready/,
  );

  const fulfilledRow = page.locator(
    '[data-gamepad-row-key="mission:fulfilled:初次自动读取任务"]:visible',
  );
  await fulfilledRow.getByText('琪露诺', { exact: true }).waitFor();
  await fulfilledRow.locator('[data-mission-scene-name="雾之湖"]').waitFor();

  const unverifiedRow = page.locator('[data-gamepad-row-key="mission:unverified"]:visible');
  assert.equal(await unverifiedRow.locator('[data-mission-character-name]').count(), 0);
  assert.equal(await unverifiedRow.locator('[data-mission-related-scenes]').count(), 0);
  assert.match(
    await unverifiedRow.locator('[data-mission-presentation-debug]').innerText(),
    /receiverLabel=<none>; presentationStatus=no-receiver/,
  );
}

async function assertMissionStatusTabFocusContract() {
  const allTab = missionStatusTab('all');
  const trackingTab = missionStatusTab('tracking');
  await trackingTab.focus();
  assert.equal(
    await trackingTab.evaluate((element) => document.activeElement === element),
    true,
    'A task status tab cannot receive gamepad focus.',
  );
  assert.equal(
    await allTab.getAttribute('aria-selected'),
    'true',
    'Moving focus must not activate another task status before confirmation.',
  );
  await trackingTab.click();
  await assertMissionStatusTabs(
    { all: 5, available: 1, fulfilled: 1, tracking: 2, unverified: 1 },
    'tracking',
  );
  await assertVisibleMissionRows(
    ['tracking', 'tracking'],
    ['mission:tracking-a', 'mission:tracking-z'],
  );
  await selectMissionStatus('all');
}

async function selectMissionStatus(status) {
  const tab = missionStatusTab(status);
  await tab.focus();
  await tab.click();
  await page.locator(`[data-mission-status-list="${status}"]:visible`).waitFor();
}

function missionStatusTab(status) {
  return page.locator(`[data-mission-status-tab="${status}"]:visible`).first();
}

function visibleMissionRows() {
  return page.locator(
    '[data-mission-status-list]:visible [data-gamepad-row="true"]',
  );
}

async function waitForRequestCount(expected, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (requests.length < expected) {
    if (Date.now() >= deadline) {
      throw new Error(`Timed out waiting for ${expected} tracked mission requests.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

async function waitForAvailableRequestCount(expected, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (availableRequests.length < expected) {
    if (Date.now() >= deadline) {
      throw new Error(`Timed out waiting for ${expected} available mission requests.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

async function waitForAvailableRequestSignature(signature, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (!availableRequests.some((request) => request.knownSignature === signature)) {
    if (Date.now() >= deadline) {
      throw new Error(`Timed out waiting for available mission signature ${signature}.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}
