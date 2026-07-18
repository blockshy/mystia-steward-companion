import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  buildUpdateNoticeSnoozeKey,
  normalizeUpdateNoticeEndpoint,
  persistUpdateNoticeSnooze,
  readUpdateNoticeSnoozeUntil,
} from '../../apps/companion/src/companion/features/updates/update-notice-storage.ts';
import {
  ACTIVE_UPDATE_POLL_INTERVAL_MS,
  getUpdateStatusPollInterval,
  STABLE_UPDATE_POLL_INTERVAL_MS,
  UPDATE_STATUS_FAILURE_RETRY_DELAYS_MS,
} from '../../apps/companion/src/companion/features/updates/update-polling.ts';
import { getUpdateNoticeContent } from '../../apps/companion/src/companion/features/updates/update-notice-content.ts';
import { UpdateRequestCoordinator } from '../../apps/companion/src/companion/features/updates/update-request-coordinator.ts';
import { normalizeProjectReleaseUrl } from '../../apps/companion/src/lib/project-release-url.ts';

class MemoryStorage {
  #values = new Map();

  getItem(key) {
    return this.#values.get(key) ?? null;
  }

  setItem(key, value) {
    this.#values.set(key, value);
  }

  removeItem(key) {
    this.#values.delete(key);
  }
}

const endpoint = 'http://127.0.0.1:32145/';
const normalizedEndpoint = normalizeUpdateNoticeEndpoint(endpoint);
assert.equal(normalizedEndpoint, 'http://127.0.0.1:32145');
assert.equal(
  buildUpdateNoticeSnoozeKey(endpoint, 'v1.2.1'),
  buildUpdateNoticeSnoozeKey(normalizedEndpoint, 'v1.2.1'),
  'trailing slashes must not create a second snooze identity',
);
assert.notEqual(
  buildUpdateNoticeSnoozeKey(endpoint, 'v1.2.1'),
  buildUpdateNoticeSnoozeKey(endpoint, 'v1.2.2'),
  'a new tag must not inherit the previous tag snooze',
);

const storage = new MemoryStorage();
const future = Date.now() + 60_000;
persistUpdateNoticeSnooze(storage, endpoint, 'v1.2.1', future);
assert.equal(readUpdateNoticeSnoozeUntil(storage, normalizedEndpoint, 'v1.2.1'), future);
storage.setItem(buildUpdateNoticeSnoozeKey(endpoint, 'v1.2.0'), String(Date.now() - 1));
assert.equal(readUpdateNoticeSnoozeUntil(storage, endpoint, 'v1.2.0'), 0);

assert.equal(
  normalizeProjectReleaseUrl('https://github.com/blockshy/mystia-steward-companion/releases/tag/v1.2.1'),
  'https://github.com/blockshy/mystia-steward-companion/releases/tag/v1.2.1',
);
for (const blockedUrl of [
  'https://github.com/blockshy/mystia-steward-companion/issues',
  'https://github.com.evil.invalid/blockshy/mystia-steward-companion/releases',
  'javascript:alert(1)',
]) {
  assert.throws(() => normalizeProjectReleaseUrl(blockedUrl));
}

const statusBase = {
  autoCheck: true,
  installState: '',
};
assert.equal(
  getUpdateStatusPollInterval(null),
  ACTIVE_UPDATE_POLL_INTERVAL_MS,
  'the first status request must converge without a sixty-second gap',
);
const startupSequence = ['idle', 'checking', 'available'].map((state) => getUpdateStatusPollInterval({
  ...statusBase,
  state,
}));
assert.deepEqual(startupSequence, [
  ACTIVE_UPDATE_POLL_INTERVAL_MS,
  ACTIVE_UPDATE_POLL_INTERVAL_MS,
  STABLE_UPDATE_POLL_INTERVAL_MS,
]);
assert.equal(
  getUpdateStatusPollInterval({ ...statusBase, autoCheck: false, state: 'idle' }),
  STABLE_UPDATE_POLL_INTERVAL_MS,
);
assert.deepEqual(
  [1, 2, 3, 4, 12].map((failureCount) => getUpdateStatusPollInterval(null, failureCount)),
  [
    UPDATE_STATUS_FAILURE_RETRY_DELAYS_MS[0],
    UPDATE_STATUS_FAILURE_RETRY_DELAYS_MS[1],
    UPDATE_STATUS_FAILURE_RETRY_DELAYS_MS[2],
    UPDATE_STATUS_FAILURE_RETRY_DELAYS_MS[3],
    STABLE_UPDATE_POLL_INTERVAL_MS,
  ],
  'status endpoint failures must converge to the stable polling interval',
);

const requestCoordinator = new UpdateRequestCoordinator();
const interruptedAction = requestCoordinator.beginAction('download');
assert.notEqual(interruptedAction, null);
assert.equal(requestCoordinator.busy, 'download');
requestCoordinator.cancelAll();
assert.equal(requestCoordinator.busy, null, 'a transient disconnect must release busy immediately');
assert.equal(requestCoordinator.isActionCurrent(interruptedAction), false);
const reconnectedAction = requestCoordinator.beginAction('check');
assert.notEqual(reconnectedAction, null);
assert.equal(
  requestCoordinator.finishAction(interruptedAction),
  false,
  'the disconnected action response must not finish a newer action',
);
assert.equal(requestCoordinator.busy, 'check');
requestCoordinator.cancelAll();
const switchedConnectionAction = requestCoordinator.beginAction('install');
assert.notEqual(switchedConnectionAction, null);
requestCoordinator.cancelAll();
assert.equal(requestCoordinator.isActionCurrent(switchedConnectionAction), false);
assert.equal(requestCoordinator.busy, null, 'switching connection identity must release busy');

const failedInstallNotice = getUpdateNoticeContent({
  state: 'installed',
  installState: 'failed',
  latestTag: 'v1.2.1',
  latestVersion: '1.2.1',
  installMessage: 'mock install failed',
  error: 'mock install failed',
  staged: true,
});
assert.equal(failedInstallNotice.kind, 'install-failed');
assert.equal(failedInstallNotice.title, '游戏端更新 v1.2.1 安装失败');
assert.equal(failedInstallNotice.detail, 'mock install failed');

const [managerSource, noticeSource, rustSource, capabilitySource, mockSource] = await Promise.all([
  readFile(new URL('../../apps/companion/src/companion/features/updates/useUpdateManager.ts', import.meta.url), 'utf8'),
  readFile(new URL('../../apps/companion/src/companion/features/updates/UpdateNoticeBar.tsx', import.meta.url), 'utf8'),
  readFile(new URL('../../apps/companion/src-tauri/src/app.rs', import.meta.url), 'utf8'),
  readFile(new URL('../../apps/companion/src-tauri/capabilities/default.json', import.meta.url), 'utf8'),
  readFile(new URL('../../scripts/mock-local-api.mjs', import.meta.url), 'utf8'),
]);

assert.match(managerSource, /visibilitychange/);
assert.match(managerSource, /getUpdateStatusPollInterval/);
assert.match(noticeSource, /getUpdateNoticeContent/);
assert.doesNotMatch(rustSource, /open_external_url/);
assert.match(rustSource, /tauri_plugin_opener::init\(\)/);
assert.match(capabilitySource, /opener:allow-open-url/);
assert.match(capabilitySource, /mystia-steward-companion\/releases\/\*/);
assert.doesNotMatch(mockSource, /checkedAtUtc/);
assert.match(mockSource, /lastSuccessAtUtc/);
assert.match(mockSource, /nextCheckAtUtc/);

console.log('update UI protocol audit passed');
