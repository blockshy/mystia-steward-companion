import assert from 'node:assert/strict';
import path from 'node:path';
import { createServer, transformWithOxc } from 'vite';
import react from '@vitejs/plugin-react';
import { chromium } from 'playwright';

// Real production hooks, controlled network settlement: no duplicated state-machine implementation.
const fixture = `
import React, { useState, useMemo, useLayoutEffect, useCallback } from 'react';
import { createRoot } from 'react-dom/client';
import { useAutomationState } from '@/companion/hooks/useAutomationState';
import { useAutomationControl } from '@/companion/hooks/useAutomationControl';
import { useOrderAutomation } from '@/companion/hooks/useOrderAutomation';
import { emptyAutoFirstOrderState, emptyNormalAutoOrderState } from '@/companion/automation-state';
import { readStoredCompanionPreferences, readSharedCompanionPreferences } from '@/companion/preferences';
import { DEFAULT_RECOMMENDATION_DATA } from '@/lib/recommendation-data';
function Harness() {
  const [identity, setIdentity] = useState({ host: 1, token: 'token-a', revision: 1, session: 'session-1', authority: 1, connected: true });
  const refresh = useCallback(async () => {}, []);
  const state = useAutomationState({ automationUiVisible: true, refresh });
  const [preferences] = useState(() => ({ ...readStoredCompanionPreferences(), automationEnabled: true,
    autoRareOrderEnabled: false, autoNormalOrderEnabled: false }));
  const snapshot = useMemo(() => ({ automationSessionId: identity.session, nightBusinessAutomationAllowed: true,
    nightBusinessAutomationBlockReason: '', automationEvents: [], automationCookingJobs: [], normalBusiness: { orders: [] } }), [identity.session]);
  const authority = { runtimeWriterReady: identity.connected, authorityRevision: identity.authority, currentDeviceIsPrimary: true };
  const base = { state, apiToken: identity.token, normalizedEndpoint: 'http://127.0.0.1:3900' + identity.host,
    connectionPaused: !identity.connected, connectionRevision: identity.revision, error: null, snapshot,
    companionConnected: identity.connected, companionPreferences: preferences,
    sharedCompanionPreferences: readSharedCompanionPreferences(preferences), companionDeviceAuthority: authority };
  const control = useAutomationControl(base);
  const recommendations = useMemo(() => ({ isCurrent: true, pending: false, error: null,
    recommendations: [], normalExecutionTargets: [] }), []);
  const automation = useOrderAutomation({ ...base, control, automationUiVisible: true, runtime: null,
    recommendationData: DEFAULT_RECOMMENDATION_DATA, recommendationDataSignature: '', favorites: { version: 1, recipes: [], beverages: [] },
    orderRecommendations: recommendations, operationalOrderRecommendations: null, normalExecutionTargets: recommendations,
    normalExecutionTargetsEnabled: false, rareOrderParticipation: { busyMutationKey: null } });
  useLayoutEffect(() => { window.ownership = { identity, setIdentity, state, control, automation,
    seedBarrier(kind, key, sequence, message) {
      const map = kind === 'rare' ? state.rareOrderStatesRef.current : state.normalOrderStatesRef.current;
      const initial = kind === 'rare' ? emptyAutoFirstOrderState(key, 1) : emptyNormalAutoOrderState(key, 1);
      map.set(key, { ...initial, manualResolutionRequired: true, paused: true,
        lastRuntimeEventSequence: sequence, lastError: message });
    },
  }; });
  return <div>{identity.host}</div>;
}
createRoot(document.getElementById('root')).render(<React.StrictMode><Harness /></React.StrictMode>);
`;

const server = await createServer({
  configFile: false,
  root: process.cwd(),
  logLevel: 'error',
  cacheDir: '/tmp/mystia-automation-ownership-audit-vite',
  resolve: { alias: { '@': path.resolve('apps/companion/src') } },
  server: { port: 0, host: '127.0.0.1', hmr: false, watch: null },
  plugins: [
    react(),
    {
      name: 'automation-ownership-fixture',
      resolveId(id) {
        if (id === '/ownership.jsx') return '\0ownership.jsx';
      },
      async load(id) {
        if (id === '\0ownership.jsx') return (await transformWithOxc(fixture, 'ownership.jsx')).code;
      },
      configureServer(vite) {
        vite.middlewares.use(async (request, response, next) => {
          if (request.url !== '/') return next();
          response.setHeader('content-type', 'text/html');
          response.end(
            await vite.transformIndexHtml(
              '/',
              '<div id="root"></div><script type="module" src="/ownership.jsx"></script>',
            ),
          );
        });
      },
    },
  ],
});
await server.listen();
const browser = await chromium.launch({
  headless: true,
  ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH }
    : {}),
});
const page = await browser.newPage();
page.setDefaultTimeout(12000);
const headers = {
  'access-control-allow-origin': '*',
  'access-control-allow-headers': '*',
  'access-control-allow-methods': 'GET,POST,OPTIONS',
};
const requests = [];
const pageErrors = [];
page.on('pageerror', (error) => pageErrors.push(String(error)));
let heldAcquire;
let holdNextAcquire = true;
let heldAck;
let activeAcks = 0;
let maxActiveAcks = 0;
const lease = { ok: true, owned: true, ownerLabel: 'test', error: null };

try {
  await page.route(/^http:\/\/127\.0\.0\.1:3900[123]\//, async (route) => {
    const request = route.request();
    if (request.method() === 'OPTIONS') return route.fulfill({ status: 204, headers });
    const url = new URL(request.url());
    requests.push({ origin: url.origin, path: url.pathname, query: url.search });
    if (url.pathname === '/automation/lease/acquire') {
      if (holdNextAcquire) {
        holdNextAcquire = false;
        heldAcquire = route;
        return;
      }
      return route.fulfill({ status: 200, headers, json: lease });
    }
    if (url.pathname === '/automation/barriers/ack') {
      activeAcks++;
      maxActiveAcks = Math.max(activeAcks, maxActiveAcks);
      heldAck = route;
      return;
    }
    return route.fulfill({ status: 200, headers, json: { ok: true } });
  });
  await page.goto('http://127.0.0.1:' + server.httpServer.address().port);
  await waitFor(() => heldAcquire);
  await change({ host: 2, session: 'session-2' });
  await change({ host: 3, session: 'session-3' });
  await heldAcquire.fulfill({ status: 200, headers, json: lease });
  heldAcquire = undefined;
  await ready();
  assert.deepEqual(
    requests
      .filter((request) => request.path === '/automation/lease/acquire')
      .map((request) => request.origin),
    ['http://127.0.0.1:39001', 'http://127.0.0.1:39003'],
    'Queued acquisition for superseded B must never be sent.',
  );

  let sequence = 1;
  for (const field of ['endpoint', 'token', 'revision', 'authority', 'pause']) {
    for (const outcome of ['success', 'failure']) {
      const oldSequence = sequence++;
      await beginAck(oldSequence);
      const previousIdentity = await page.evaluate(() => window.ownership.identity);
      const next =
        field === 'endpoint'
          ? { host: previousIdentity.host === 2 ? 3 : 2, session: `session-${oldSequence + 10}` }
          : field === 'token'
            ? { token: `${previousIdentity.token}-changed` }
            : field === 'revision'
              ? { revision: previousIdentity.revision + 1 }
              : field === 'authority'
                ? { authority: previousIdentity.authority + 1 }
                : { connected: false };
      await change(next);
      if (next.connected === false) await change({ connected: true });
      await ready();
      assert.equal(
        await page.evaluate(() => Boolean(window.ownership.state.automationBarrierAckRef.current)),
        true,
        'Changing connection must retain the admitted ACK transport owner.',
      );
      const writesBeforeBlockedAck = requests.filter(
        (request) => request.path === '/automation/barriers/ack',
      ).length;
      const blockedSequence = sequence++;
      await page.evaluate(
        (value) => window.ownership.automation.acknowledgeAutomationBarrierEvent(value),
        blockedSequence,
      );
      await page.waitForFunction(
        (value) => !!window.ownership.state.automationBarrierAckErrors[value],
        blockedSequence,
      );
      assert.equal(
        requests.filter((request) => request.path === '/automation/barriers/ack').length,
        writesBeforeBlockedAck,
      );
      const messageBeforeOldCompletion = await page.evaluate(() => window.ownership.state.autoPrepMessage);
      await finishAck(oldSequence, outcome);
      await page.waitForFunction(() => !window.ownership.state.automationBarrierAckRef.current);
      assert.equal(
        await page.evaluate((value) => window.ownership.state.automationBarrierAckErrors[value], oldSequence),
        undefined,
      );
      assert.equal(
        await page.evaluate(() => window.ownership.state.autoPrepMessage),
        messageBeforeOldCompletion,
        'An old ACK success or failure must not replace current connection diagnostics.',
      );
      const currentSequence = sequence++;
      await beginAck(currentSequence);
      await finishAck(currentSequence, 'success');
      await page.waitForFunction(() => !window.ownership.state.automationBarrierAckRef.current);
    }
  }
  for (const kind of ['rare', 'normal']) {
    for (const outcome of ['success', 'failure']) {
      const key = `${kind}:same-order-key`;
      const oldSequence = sequence++;
      const newSequence = sequence++;
      await page.evaluate(
        ({ kind, key, oldSequence }) => {
          const test = window.ownership;
          test.seedBarrier(kind, key, oldSequence, 'old barrier');
          if (kind === 'rare') test.automation.resetRareAutomationOrder(key);
          else test.automation.resetNormalAutomationOrder(key);
        },
        { kind, key, oldSequence },
      );
      await waitFor(() => heldAck);
      const revision = await page.evaluate(() => window.ownership.identity.revision);
      await change({ revision: revision + 1 });
      await ready();
      await page.evaluate(
        ({ kind, key, newSequence }) => {
          window.ownership.seedBarrier(kind, key, newSequence, 'current barrier');
        },
        { kind, key, newSequence },
      );
      await finishAck(oldSequence, outcome);
      await page.waitForFunction(() => !window.ownership.state.automationBarrierAckRef.current);
      const state = await page.evaluate(
        ({ kind, key }) => {
          const state = window.ownership.state;
          const value = (kind === 'rare' ? state.rareOrderStatesRef : state.normalOrderStatesRef).current.get(
            key,
          );
          return {
            manual: value?.manualResolutionRequired,
            sequence: value?.lastRuntimeEventSequence,
            message: value?.lastError,
          };
        },
        { kind, key },
      );
      assert.deepEqual(
        state,
        { manual: true, sequence: newSequence, message: 'current barrier' },
        `A stale ${kind} reset completion must not change a newer manual barrier.`,
      );
    }
  }
  assert.equal(maxActiveAcks, 1);
  assert.deepEqual(pageErrors, []);
  console.log(
    'PASS: superseded queued leases never send; ACKs keep one transport owner across endpoint/token/revision/authority/pause changes and discard both stale success and failure.',
  );
} finally {
  await browser.close();
  await server.close();
}

async function waitFor(predicate) {
  const deadline = Date.now() + 12000;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error('Timed out waiting for controlled automation request.');
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}
async function change(next) {
  await page.evaluate((value) => window.ownership.setIdentity((current) => ({ ...current, ...value })), next);
  await page.waitForFunction(
    (value) => Object.entries(value).every(([key, expected]) => window.ownership.identity[key] === expected),
    next,
  );
}
async function ready() {
  await page.waitForFunction(() => window.ownership.control.automationLeaseOwned);
}
async function beginAck(sequence) {
  await page.evaluate(
    (value) => window.ownership.automation.acknowledgeAutomationBarrierEvent(value),
    sequence,
  );
  await waitFor(() => heldAck);
}
async function finishAck(sequence, outcome) {
  const route = heldAck;
  heldAck = undefined;
  activeAcks--;
  await route.fulfill({
    status: outcome === 'failure' ? 503 : 200,
    headers,
    json:
      outcome === 'failure'
        ? { error: 'controlled old failure' }
        : {
            ok: true,
            sequence,
            acknowledgedCount: 1,
            acknowledgedSequences: [sequence],
            status: 'confirmed',
            error: null,
          },
  });
}
