import type { UpdateInstallState, UpdateStatusResponse } from '@/companion/types';

export const ACTIVE_UPDATE_POLL_INTERVAL_MS = 2_000;
export const STABLE_UPDATE_POLL_INTERVAL_MS = 60_000;
export const UPDATE_STATUS_FAILURE_RETRY_DELAYS_MS = [2_000, 5_000, 15_000, STABLE_UPDATE_POLL_INTERVAL_MS] as const;

const ACTIVE_INSTALL_STATES = new Set<UpdateInstallState>([
  'waiting',
  'preparing',
  'closing-companion',
  'waiting-game',
  'terminating-game',
  'game-closed',
  'backing-up',
  'installing',
  'verifying',
]);

export function getUpdateStatusPollInterval(
  status: UpdateStatusResponse | null,
  consecutiveStatusFailures = 0,
): number {
  if (consecutiveStatusFailures > 0) {
    const retryIndex = Math.min(
      Math.max(0, Math.trunc(consecutiveStatusFailures) - 1),
      UPDATE_STATUS_FAILURE_RETRY_DELAYS_MS.length - 1,
    );
    return UPDATE_STATUS_FAILURE_RETRY_DELAYS_MS[retryIndex];
  }
  const waitingForInitialStatus = status === null;
  const waitingForInitialAutoCheck = status?.state === 'idle' && status.autoCheck;
  const activelyChanging = waitingForInitialStatus
    || waitingForInitialAutoCheck
    || status?.state === 'checking'
    || status?.state === 'downloading'
    || ACTIVE_INSTALL_STATES.has(status?.installState ?? '');
  return activelyChanging ? ACTIVE_UPDATE_POLL_INTERVAL_MS : STABLE_UPDATE_POLL_INTERVAL_MS;
}
