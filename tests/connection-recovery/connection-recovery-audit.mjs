import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import {
  CONNECTION_RETRY_DELAYS_MS,
  buildAutomationLeaseConnectionKey,
  getConnectionRetryDelayMs,
  isAutomationLeaseOwnedForConnection,
  resolveCompanionConnectionIdentity,
  updateUnavailableRuntimeData,
} from '../../apps/companion/src/companion/connection-recovery.ts';

const root = fileURLToPath(new URL('../..', import.meta.url));
const [hook, workbench, tauriApp] = await Promise.all([
  readFile(`${root}/apps/companion/src/companion/hooks/useCompanionConnection.ts`, 'utf8'),
  readFile(`${root}/apps/companion/src/companion/ModWorkbench.tsx`, 'utf8'),
  readFile(`${root}/apps/companion/src-tauri/src/app.rs`, 'utf8'),
]);

const stableIdentity = {
  endpoint: 'http://127.0.0.1:32145',
  apiToken: 'stable-token',
};
const unchanged = resolveCompanionConnectionIdentity(stableIdentity, {
  endpoint: 'http://127.0.0.1:32145',
  apiToken: 'stable-token',
});
assert.equal(unchanged.changed, false);
assert.equal(unchanged.identity, stableIdentity, 'An identical launch notification must be a referential no-op.');

const partialUnchanged = resolveCompanionConnectionIdentity(stableIdentity, {
  endpoint: 'http://127.0.0.1:32145',
});
assert.equal(partialUnchanged.changed, false);
assert.equal(partialUnchanged.identity, stableIdentity);

const changed = resolveCompanionConnectionIdentity(stableIdentity, { apiToken: 'next-token' });
assert.deepEqual(changed, {
  changed: true,
  identity: {
    endpoint: 'http://127.0.0.1:32145',
    apiToken: 'next-token',
  },
});

assert.deepEqual([...CONNECTION_RETRY_DELAYS_MS], [2000, 5000, 10000, 30000]);
assert.deepEqual(
  [1, 2, 3, 4, 5, 20].map(getConnectionRetryDelayMs),
  [2000, 5000, 10000, 30000, 30000, 30000],
);

const completeRuntimeData = {
  isComplete: true,
  source: 'game-runtime',
  status: 'complete',
  recipes: [{ id: 1 }],
  ingredients: [],
  beverages: [],
  normalCustomers: [],
  rareCustomers: [],
};
assert.equal(
  updateUnavailableRuntimeData(completeRuntimeData, 'next-source', 'temporary failure'),
  completeRuntimeData,
  'A complete runtime catalog must survive a transient incomplete snapshot.',
);
assert.deepEqual(
  updateUnavailableRuntimeData({
    isComplete: false,
    source: 'waiting',
    status: 'initial placeholder',
    recipes: [],
    ingredients: [],
    beverages: [],
    normalCustomers: [],
    rareCustomers: [],
  }, 'game-runtime', 'read-izakaya-places failed'),
  {
    isComplete: false,
    source: 'game-runtime',
    status: 'read-izakaya-places failed',
    recipes: [],
    ingredients: [],
    beverages: [],
    normalCustomers: [],
    rareCustomers: [],
  },
  'An incomplete runtime catalog placeholder must update to the latest server status.',
);
const stableUnavailableRuntimeData = updateUnavailableRuntimeData(null, 'game-runtime', 'still loading');
assert.equal(
  updateUnavailableRuntimeData(stableUnavailableRuntimeData, 'game-runtime', 'still loading'),
  stableUnavailableRuntimeData,
  'An unchanged incomplete runtime catalog status must preserve referential identity.',
);

const stableLeaseKey = buildAutomationLeaseConnectionKey(stableIdentity, 'mod-session-1');
assert.equal(
  stableLeaseKey,
  buildAutomationLeaseConnectionKey(stableIdentity, 'mod-session-1'),
  'A transient failure in the same Mod session must not change lease identity.',
);
const nextSessionLeaseKey = buildAutomationLeaseConnectionKey(stableIdentity, 'mod-session-2');
assert.notEqual(nextSessionLeaseKey, stableLeaseKey, 'A new Mod session must have a new lease identity.');

const ownedLease = { ok: true, owned: true };
assert.equal(isAutomationLeaseOwnedForConnection(ownedLease, stableLeaseKey, stableLeaseKey, false), true);
assert.equal(
  isAutomationLeaseOwnedForConnection(ownedLease, stableLeaseKey, stableLeaseKey, true),
  false,
  'A cached lease must remain disabled after reconnect until the Mod revalidates it.',
);
assert.equal(
  isAutomationLeaseOwnedForConnection(ownedLease, stableLeaseKey, nextSessionLeaseKey, false),
  false,
  'A lease from an old Mod session must not be reused.',
);
assert.equal(
  isAutomationLeaseOwnedForConnection(null, '', stableLeaseKey, true),
  false,
  'lease-unavailable must leave automation disabled until acquire succeeds.',
);

assert.equal(hook.includes('readHealth'), false, 'Recovery must never claim readiness from /health.');
assert.equal(hook.includes('shouldProbeHealth'), false, 'The obsolete health-probe branch must be removed.');
assert.equal(hook.includes('INITIAL_PROBE_TIMEOUT_MS'), false, 'The old health-probe timeout name must not survive.');
assert.ok(hook.includes('INITIAL_SNAPSHOT_TIMEOUT_MS'));
assert.match(hook, /const data = await readSnapshot\(/, 'Every connection attempt must read an authenticated snapshot.');
assert.match(hook, /setConnectionFailureCount\(\(current\) => current \+ 1\)/, 'Every failed attempt must advance the retry scheduler, including after the delay cap.');
assert.match(hook, /getConnectionRetryDelayMs\(connectionFailureCount\)/, 'The sole failure timer must use the fixed backoff sequence.');
assert.equal(
  hook.includes('if (current) return current;'),
  false,
  'An incomplete runtime catalog placeholder must update to the latest server failure status.',
);
assert.equal(hook.includes('connectionRevision === 0'), false, 'A refresh callback identity change must not trigger an initialization retry loop.');
assert.ok(hook.includes("const CONNECTION_ACTIVATED_EVENT = 'connection-activation-requested'"));
const resumeStart = hook.indexOf('const resumePausedConnection = useCallback');
const resumeEnd = hook.indexOf('  }, [refresh]);', resumeStart);
const resumeBlock = hook.slice(resumeStart, resumeEnd);
assert.ok(resumeBlock.includes('if (!connectionPausedRef.current) return;'), 'An activation must be a no-op while already connected.');
assert.ok(resumeBlock.includes('void refresh();'), 'A paused activation must immediately verify a snapshot.');
assert.equal(resumeBlock.includes('clearSnapshotCache'), false, 'Resuming the same identity must preserve snapshot data and signature.');
const activationHandlerStart = hook.indexOf('listen<boolean>(CONNECTION_ACTIVATED_EVENT, async () =>');
const activationHandlerEnd = hook.indexOf('      }))', activationHandlerStart);
const activationHandler = hook.slice(activationHandlerStart, activationHandlerEnd);
const activationIdentityRead = activationHandler.indexOf('await readLaunchConnection(() => disposed);');
const activationResume = activationHandler.indexOf('resumePausedConnection();');
assert.ok(activationHandlerStart >= 0, 'The activation listener must serialize identity synchronization.');
assert.ok(
  activationIdentityRead >= 0 && activationResume > activationIdentityRead,
  'Activation must apply the authoritative Tauri identity before resuming a paused connection.',
);

const refreshDependencyStart = hook.indexOf('}, [apiToken, ensureRuntimeDataCache, markConnected, normalizedEndpoint]);');
assert.ok(refreshDependencyStart >= 0, 'Refresh must not depend on error or snapshot state.');
const catchStart = hook.indexOf('    } catch (err) {');
const finallyStart = hook.indexOf('    } finally {', catchStart);
const catchBlock = hook.slice(catchStart, finallyStart);
assert.equal(catchBlock.includes("setError('')"), false, 'A failed snapshot must preserve the connection error.');
assert.equal(catchBlock.includes('markConnected('), false, 'A failed snapshot must not update the connected timestamp.');

assert.ok(workbench.includes('buildAutomationLeaseConnectionKey('));
assert.ok(workbench.includes('automationLeaseRevalidationRequiredRef.current = true'));
assert.ok(workbench.includes('automationLeaseRevalidationRequiredRef.current = false'));
assert.equal(workbench.includes('automationConnectionEpochRef'), false);
assert.equal(workbench.includes('previousAutomationConnectionReadyRef'), false);
const leaseUnavailableHandler = workbench.slice(
  workbench.indexOf('const handleAutomationControlPlaneResponse'),
  workbench.indexOf('useEffect(() => () =>', workbench.indexOf('const handleAutomationControlPlaneResponse')),
);
assert.ok(leaseUnavailableHandler.includes('setAutomationLease(null)'));
assert.ok(leaseUnavailableHandler.includes("setAutomationLeaseBindingKey('')"));
assert.ok(leaseUnavailableHandler.includes('automationLeaseRevalidationRequiredRef.current = true'));

assert.match(tauriApp, /let mut changed = false;/);
assert.match(tauriApp, /current_connection\.endpoint\.as_ref\(\) != Some\(endpoint\)/);
assert.match(tauriApp, /current_connection\.token\.as_ref\(\) != Some\(token\)/);
assert.match(tauriApp, /fn launch_connection_updates_only_when_identity_changes\(\)/);
assert.ok(tauriApp.includes('const CONNECTION_ACTIVATED_EVENT: &str = "connection-activation-requested"'));
assert.ok(tauriApp.includes('let next_connection = parse_control_launch_connection(message);'));
assert.ok(tauriApp.includes('&& next_connection.endpoint.is_some()'));
assert.ok(tauriApp.includes('&& next_connection.token.is_some()'));
assert.ok(tauriApp.includes('&next_connection,'));
assert.match(tauriApp, /fn identical_show_or_toggle_requests_connection_activation\(\)/);

console.log('Connection recovery contract audit passed.');
