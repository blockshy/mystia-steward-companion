import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const OUTPUT_DIR = process.env.RARE_GUEST_INVITATION_AUDIT_OUTPUT_DIR
  || '/tmp/mystia-companion-rare-guest-invitation-audit';
const STORAGE_PREFIX = 'mystia-steward-companion';
const listRequests = [];
const singleInviteRequests = [];

await mkdir(OUTPUT_DIR, { recursive: true });
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 640, height: 760 } });

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

  await page.getByText('全部场景', { exact: true }).click();
  await waitForListRequestCount(3);
  assertRequest(2, 'all');
  await page.getByText('慧音', { exact: true }).waitFor();
  const mappedGuestRow = page.locator('[data-gamepad-row-key="mission-invitation:10:DLC1_Marisa"]');
  await mappedGuestRow.getByRole('button', { name: '邀请', exact: true }).click();
  await waitFor(() => singleInviteRequests.length === 1, 10_000);
  assert.deepEqual(singleInviteRequests[0], {
    method: 'POST',
    guestId: '10',
    scope: 'all',
    expectedDaySceneGeneration: '1',
    expectedMapLabel: '妖怪兽道',
  });
  await page.getByText('mock mapped invitation write checked', { exact: true }).waitFor();

  await page
    .getByLabel('稀客邀请', { exact: true })
    .getByRole('button', { name: '刷新', exact: true })
    .click();
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
  assert.ok(listRequests.every((request) => request.method === 'GET'));

  returnPermanentInvitationFailure = true;
  await page
    .getByLabel('稀客邀请', { exact: true })
    .getByRole('button', { name: '刷新', exact: true })
    .click();
  await waitForListRequestCount(6);
  await page.getByText('mock invitation list contract failed', { exact: true }).waitFor();
  assert.equal(
    await page.getByText('暂无稀客候选', { exact: true }).count(),
    0,
    'A failed invitation response was rendered as a successful empty list.',
  );
  await page.waitForTimeout(800);
  assert.equal(listRequests.length, 6, 'A deterministic invitation failure entered the transient retry loop.');

  returnPermanentInvitationFailure = false;
  await page
    .getByLabel('稀客邀请', { exact: true })
    .getByRole('button', { name: '刷新', exact: true })
    .click();
  await waitForListRequestCount(7);
  await page.getByText('mock invitation candidates loaded', { exact: true }).waitFor();

  returnInvitationWriteFailure = true;
  await page.getByRole('button', { name: '邀请全部', exact: true }).click();
  await page.getByText('mock invitation write failed', { exact: true }).waitFor();
  await page.waitForTimeout(800);
  assert.equal(
    await page.getByText('mock invitation write failed', { exact: true }).count(),
    1,
    'An invitation write error was cleared by an unintended list refresh.',
  );
  assert.equal(listRequests.length, 7, 'A failed invitation write triggered an unintended list refresh.');

  console.log(
    'PASS: invitation page auto-refreshes on identity changes, surfaces failed responses, '
    + 'retains write errors, and keeps manual GET refresh.',
  );
} finally {
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
  await activateTab('missions');
  const trigger = page.getByRole('tab', { name: '稀客邀请', exact: true }).first();
  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
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
