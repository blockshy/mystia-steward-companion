import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.RARE_GUEST_INVITATION_AUDIT_OUTPUT_DIR
  || '/tmp/mystia-companion-rare-guest-invitation-audit';
const CHROMIUM_EXECUTABLE_PATH = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH?.trim();
const STORAGE_PREFIX = 'mystia-steward-companion';
const listRequests = [];
const singleInviteRequests = [];

await mkdir(OUTPUT_DIR, { recursive: true });
const browser = await chromium.launch({
  headless: true,
  ...(CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: CHROMIUM_EXECUTABLE_PATH }
    : {}),
});
const page = await browser.newPage({ viewport: { width: 640, height: 760 } });
let releaseSingleInviteResponse = null;

try {
  let transientInvitationReadsRemaining = 1;
  let returnPermanentInvitationFailure = false;
  let returnInvitationWriteFailure = false;
  await page.route('**/rare-guests/invitations?**', async (route) => {
    if (route.request().method() !== 'GET') {
      await route.continue();
      return;
    }

    if (returnPermanentInvitationFailure) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          ok: false,
          runtimeAvailable: true,
          status: 'mock invitation list contract failed',
          error: 'mock invitation list contract failed',
          candidateCount: 0,
          usableCount: 0,
          existingSlotCount: 0,
          existingControlledCount: 0,
          scheduledSlotCount: 0,
          invitedCount: 0,
          skippedCount: 0,
          candidates: [],
          available: [],
          existingInvited: [],
          invited: [],
          skipped: [],
        }),
      });
      return;
    }

    if (transientInvitationReadsRemaining === 0) {
      await route.continue();
      return;
    }

    transientInvitationReadsRemaining -= 1;
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        ok: false,
        runtimeAvailable: false,
        status: 'day scene runtime temporarily unavailable',
        error: 'day scene runtime temporarily unavailable',
        candidateCount: 0,
        usableCount: 0,
        existingSlotCount: 0,
        existingControlledCount: 0,
        scheduledSlotCount: 0,
        invitedCount: 0,
        skippedCount: 0,
        candidates: [],
        available: [],
        existingInvited: [],
        invited: [],
        skipped: [],
      }),
    });
  });
  await page.route('**/rare-guests/invite-all?**', async (route) => {
    if (!returnInvitationWriteFailure) {
      await route.continue();
      return;
    }

    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        ok: false,
        runtimeAvailable: true,
        status: 'mock invitation write failed',
        error: 'mock invitation write failed',
        candidateCount: 0,
        usableCount: 0,
        existingSlotCount: 0,
        existingControlledCount: 0,
        scheduledSlotCount: 0,
        invitedCount: 0,
        skippedCount: 0,
        candidates: [],
        available: [],
        existingInvited: [],
        invited: [],
        skipped: [],
      }),
    });
  });
  await page.route('**/rare-guests/invite?**', async (route) => {
    const url = new URL(route.request().url());
    singleInviteRequests.push({
      method: route.request().method(),
      guestId: url.searchParams.get('guestId'),
      scope: url.searchParams.get('scope'),
      expectedDaySceneGeneration: url.searchParams.get('expectedDaySceneGeneration'),
      expectedMapLabel: url.searchParams.get('expectedMapLabel'),
    });
    await new Promise((resolve) => {
      releaseSingleInviteResponse = resolve;
    });
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        ok: false,
        runtimeAvailable: true,
        status: 'mock mapped invitation write checked',
        error: 'mock mapped invitation write checked',
        candidateCount: 0,
        usableCount: 0,
        existingSlotCount: 0,
        existingControlledCount: 0,
        scheduledSlotCount: 0,
        invitedCount: 0,
        skippedCount: 0,
        candidates: [],
        available: [],
        existingInvited: [],
        invited: [],
        skipped: [],
      }),
    });
  });

  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.origin === new URL(API_URL).origin && url.pathname === '/rare-guests/invitations') {
      listRequests.push({
        method: request.method(),
        scope: url.searchParams.get('scope'),
        startedAt: Date.now(),
      });
    }
  });

  await page.addInitScript(({ apiUrl, apiToken, storagePrefix }) => {
    localStorage.setItem(`${storagePrefix}-mod-api-endpoint`, apiUrl);
    localStorage.setItem(`${storagePrefix}-mod-api-token`, apiToken);
    localStorage.setItem(`${storagePrefix}-rare-guest-invitation-scope`, 'current');
  }, { apiUrl: API_URL, apiToken: API_TOKEN, storagePrefix: STORAGE_PREFIX });

  await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.body.innerText.includes('1.0.5'), null, { timeout: 10_000 });
  await activateInvitationPanel();
  await page.getByText('稀客邀请模块已停用。手动开启总控后才会读取候选或执行邀请。', { exact: true })
    .waitFor({ timeout: 10_000 });
  await page.waitForTimeout(500);
  assert.equal(listRequests.length, 0, 'The default-off invitation module issued a candidate read.');
  const moduleToggle = page.locator('[data-gamepad-focus-key="rare-invitations:module-toggle"]');
  assert.equal(await moduleToggle.isChecked(), false, 'The invitation module did not default to disabled.');
  await moduleToggle.click();
  assert.equal(
    await page.evaluate((storagePrefix) => localStorage.getItem(`${storagePrefix}-rare-guest-invitation-module-enabled`), STORAGE_PREFIX),
    '1',
    'Enabling the invitation module was not persisted.',
  );
  await waitForListRequestCount(2);
  await page.getByText('mock invitation candidates loaded', { exact: true }).waitFor();
  assertRequest(0, 'current');
  assertRequest(1, 'current');
  assert.ok(
    listRequests[1].startedAt - listRequests[0].startedAt >= 400,
    'Transient runtime readiness was retried without the reviewed backoff.',
  );
  await page.waitForTimeout(800);
  assert.equal(listRequests.length, 2, 'A successful retry left a hot invitation-read loop active.');

  const invitationSectionOrder = await page.evaluate(() => {
    const content = document.querySelector('[data-rare-invitation-content]');
    if (!(content instanceof HTMLElement)) return [];
    return Array.from(content.querySelectorAll(':scope > [data-rare-invitation-section]'))
      .map((element) => element.getAttribute('data-rare-invitation-section'));
  });
  assert.deepEqual(
    invitationSectionOrder,
    ['invited', 'filters', 'available', 'unavailable'],
    'Invitation sections are not ordered by current state before controls and candidates.',
  );
  const invitedList = page.locator('[data-rare-invitation-invited-list]');
  assert.equal(
    await invitedList.locator('[data-rare-invitation-invited-id]').count(),
    3,
    'The complete current invited list was not rendered.',
  );
  const invitationSearch = page.locator('[data-rare-invitation-search]');
  await invitationSearch.fill('华扇');
  assert.equal(
    await invitedList.locator('[data-rare-invitation-invited-id]').count(),
    3,
    'Candidate search incorrectly filtered the current invited list.',
  );
  await page.getByText('用于验证完整展示且不会被搜索隐藏的超长稀客名称', { exact: true }).waitFor();
  await page.getByRole('button', { name: '邀请全部匹配项 (2)', exact: true }).waitFor();
  assert.equal(
    await page.locator('[data-rare-invitation-section="unavailable"] [data-rare-invitation-candidate="1005"]').count(),
    1,
    'Unavailable candidates were not separated into their own section.',
  );
  await invitationSearch.fill('');

  await page.getByText('全部场景', { exact: true }).click();
  await waitForListRequestCount(3);
  assertRequest(2, 'all');
  await page.getByText('慧音', { exact: true }).waitFor();
  const mappedGuestRow = page.locator('[data-gamepad-row-key="rare-invitation:10:DLC1_Marisa"]');
  await mappedGuestRow.getByRole('button', { name: '邀请', exact: true }).click();
  await waitFor(() => singleInviteRequests.length === 1, 10_000);
  assert.equal(await moduleToggle.isDisabled(), true, 'The module toggle remained enabled during an in-flight invitation write.');
  assert.deepEqual(singleInviteRequests[0], {
    method: 'POST',
    guestId: '10',
    scope: 'all',
    expectedDaySceneGeneration: '1',
    expectedMapLabel: '妖怪兽道',
  });
  await page.getByRole('tab', { name: '任务列表', exact: true }).click();
  await page.getByText('任务列表模块已停用。手动开启总控后才会读取任务数据。', { exact: true }).waitFor();
  await page.getByRole('tab', { name: '稀客邀请', exact: true }).click();
  assert.equal(
    await page.locator('[data-gamepad-focus-key="rare-invitations:module-toggle"]').isDisabled(),
    true,
    'Switching extension subpages retired the in-flight invitation operation.',
  );
  releaseSingleInviteResponse?.();
  releaseSingleInviteResponse = null;
  await page.getByText('mock mapped invitation write checked', { exact: true }).waitFor();
  assert.equal(await moduleToggle.isDisabled(), false, 'The module toggle did not recover after the write completed.');

  await page.locator('[data-gamepad-focus-key="rare-invitations:refresh"]').click();
  await waitForListRequestCount(4);
  assertRequest(3, 'all');

  await activateTab('overview');
  await activateInvitationPanel();
  await waitForListRequestCount(5);
  assertRequest(4, 'all');

  await page.screenshot({ path: `${OUTPUT_DIR}/minimum-auto-refresh.png`, fullPage: true });
  const overflow = await page.evaluate(
    () => Math.max(0, document.documentElement.scrollWidth - document.documentElement.clientWidth),
  );
  assert.equal(overflow, 0, `640px invitation page has ${overflow}px horizontal overflow.`);
  await page.setViewportSize({ width: 390, height: 760 });
  const narrowOverflow = await page.evaluate(
    () => Math.max(0, document.documentElement.scrollWidth - document.documentElement.clientWidth),
  );
  assert.equal(narrowOverflow, 0, `390px invitation page has ${narrowOverflow}px horizontal overflow.`);
  const narrowCandidateContainment = await page.locator('[data-rare-invitation-candidate]').evaluateAll(
    (elements) => elements.every((element) => element.scrollWidth <= element.clientWidth + 1),
  );
  assert.equal(narrowCandidateContainment, true, 'A candidate row clips long content at 390px.');
  await page.screenshot({ path: `${OUTPUT_DIR}/narrow-auto-refresh.png`, fullPage: true });
  await page.setViewportSize({ width: 640, height: 760 });
  assert.ok(listRequests.every((request) => request.method === 'GET'));

  returnPermanentInvitationFailure = true;
  await page.locator('[data-gamepad-focus-key="rare-invitations:refresh"]').click();
  await waitForListRequestCount(6);
  await page.getByText('mock invitation list contract failed', { exact: true }).waitFor();
  assert.equal(
    await page.locator('[data-rare-invitation-content]').count(),
    0,
    'A failed invitation response was rendered as a successful empty list.',
  );
  await page.waitForTimeout(800);
  assert.equal(listRequests.length, 6, 'A deterministic invitation failure entered the transient retry loop.');

  returnPermanentInvitationFailure = false;
  await page.locator('[data-gamepad-focus-key="rare-invitations:refresh"]').click();
  await waitForListRequestCount(7);
  await page.getByText('mock invitation candidates loaded', { exact: true }).waitFor();

  returnInvitationWriteFailure = true;
  await page.getByRole('button', { name: /邀请全部匹配项/ }).click();
  await page.getByText('mock invitation write failed', { exact: true }).waitFor();
  await page.waitForTimeout(800);
  assert.equal(
    await page.getByText('mock invitation write failed', { exact: true }).count(),
    1,
    'An invitation write error was cleared by an unintended list refresh.',
  );
  assert.equal(listRequests.length, 7, 'A failed invitation write triggered an unintended list refresh.');

  await moduleToggle.click();
  await page.getByText('稀客邀请模块已停用。手动开启总控后才会读取候选或执行邀请。', { exact: true }).waitFor();
  const readsAfterDisable = listRequests.length;
  await page.waitForTimeout(1_000);
  assert.equal(listRequests.length, readsAfterDisable, 'Invitation reads continued while the module was disabled.');
  assert.equal(
    await page.evaluate((storagePrefix) => localStorage.getItem(`${storagePrefix}-rare-guest-invitation-module-enabled`), STORAGE_PREFIX),
    '0',
    'Disabling the invitation module was not persisted.',
  );

  await moduleToggle.click();
  await waitForListRequestCount(readsAfterDisable + 1);
  const readsBeforeReload = listRequests.length;
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-gamepad-tab-value="extensions"]', { timeout: 10_000 });
  await activateInvitationPanel();
  await page.waitForSelector('[data-gamepad-focus-key="rare-invitations:module-toggle"]', { timeout: 10_000 });
  assert.equal(
    await page.locator('[data-gamepad-focus-key="rare-invitations:module-toggle"]').isChecked(),
    true,
    'The manually enabled invitation module did not persist across reload.',
  );
  await waitFor(() => listRequests.length >= readsBeforeReload + 1, 10_000);

  console.log(
    'PASS: invitation extension subpage is independently default-off and persistent, keeps invited/filter/candidate '
    + 'sections stable at 640px and 390px, auto-refreshes on identity changes, preserves writes across page '
    + 'switches, surfaces failed responses, and keeps manual GET refresh.',
  );
} finally {
  releaseSingleInviteResponse?.();
  await browser.close();
}

function assertRequest(index, scope) {
  assert.equal(listRequests[index]?.method, 'GET');
  assert.equal(listRequests[index]?.scope, scope);
}

async function activateTab(value) {
  const trigger = page.locator(`[data-gamepad-tab-value="${value}"]`).first();
  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
}

async function activateInvitationPanel() {
  await activateTab('extensions');
  await page.locator('[data-extension-tabs]').getByRole('tab', { name: '稀客邀请', exact: true }).click();
}

async function waitForListRequestCount(expected) {
  await page.waitForFunction(
    ({ origin, expectedCount }) => performance.getEntriesByType('resource')
      .filter((entry) => {
        const url = new URL(entry.name);
        return url.origin === origin && url.pathname === '/rare-guests/invitations';
      }).length >= expectedCount,
    { origin: new URL(API_URL).origin, expectedCount: expected },
    { timeout: 10_000 },
  );
  await waitFor(() => listRequests.length >= expected, 10_000);
}

async function waitFor(predicate, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error('Timed out waiting for invitation request.');
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}
