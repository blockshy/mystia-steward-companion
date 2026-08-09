import type {
  LocalApiSnapshot,
  RareGuestInvitationScope,
} from '@/companion/types';

export interface RareGuestInvitationRuntimeContext {
  enabled: boolean;
  connected: boolean;
  connectionRevision: number;
  normalizedEndpoint: string;
  scope: RareGuestInvitationScope;
  snapshot: LocalApiSnapshot | null;
}

export const RARE_GUEST_INVITATION_TRANSIENT_RETRY_DELAYS_MS = [
  500,
  1_000,
  2_000,
  4_000,
] as const;

export function getRareGuestInvitationTransientRetryDelayMs(
  attemptIndex: number,
): number | null {
  if (!Number.isInteger(attemptIndex)
      || attemptIndex < 0
      || attemptIndex >= RARE_GUEST_INVITATION_TRANSIENT_RETRY_DELAYS_MS.length) {
    return null;
  }

  return RARE_GUEST_INVITATION_TRANSIENT_RETRY_DELAYS_MS[attemptIndex];
}

/**
 * Builds the stable runtime identity shared by invitation reads and writes.
 *
 * A null identity means the module is disabled or the day-scene runtime is not
 * safe to use. Page visibility is deliberately excluded: hiding the page stops
 * passive reads, but must not pretend that an already submitted write vanished.
 */
export function buildRareGuestInvitationContextIdentity({
  enabled,
  connected,
  connectionRevision,
  normalizedEndpoint,
  scope,
  snapshot,
}: RareGuestInvitationRuntimeContext): string | null {
  if (!enabled
      || !connected
      || !snapshot?.runtimeLoaded
      || !snapshot.runtimeDaySceneReady
      || snapshot.runtimeDaySceneGeneration < 1) {
    return null;
  }

  const mapLabel = snapshot.activeDayMapLabel?.trim() ?? '';
  if (!mapLabel) return null;

  return JSON.stringify([
    connectionRevision,
    normalizedEndpoint,
    scope,
    snapshot.runtimeDaySceneGeneration,
    mapLabel,
  ]);
}
