import type {
  LocalApiSnapshot,
  ModTab,
  RareGuestInvitationScope,
} from '@/companion/types';

export interface RareGuestInvitationRefreshContext {
  connected: boolean;
  connectionRevision: number;
  normalizedEndpoint: string;
  scope: RareGuestInvitationScope;
  snapshot: LocalApiSnapshot | null;
  tab: ModTab;
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
 * Builds the identity of one passive invitation-list read.
 *
 * A null identity means the day-scene runtime is not safe to read. Every value
 * that can make a previous result stale is included so a scene or connection
 * transition cannot reuse an earlier candidate list.
 */
export function buildRareGuestInvitationRefreshIdentity({
  connected,
  connectionRevision,
  normalizedEndpoint,
  scope,
  snapshot,
  tab,
}: RareGuestInvitationRefreshContext): string | null {
  if (!connected
      || tab !== 'rare-invitations'
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
