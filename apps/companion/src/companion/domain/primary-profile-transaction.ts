import { serializeSharedCompanionPreferences } from '@/companion/preferences';
import type { CompanionDeviceAuthorityState } from '@/companion/types';

export interface PrimaryProfileTransactionBase {
  registryId: string;
  currentDeviceId: string;
  primaryDeviceId: string;
  authorityRevision: number;
  activeProfileRevision: number;
  activeProfileHash: string;
  activeProfileSignature: string;
  currentDeviceProfileRevision: number;
  currentDeviceProfileHash: string;
  currentDeviceProfileSignature: string;
}

export type PrimaryProfileObservationDecision =
  | { action: 'retain-draft'; reason: 'same-authority-baseline' }
  | { action: 'confirm-draft'; reason: 'desired-profile-committed' }
  | { action: 'rollback-draft'; reason: 'authority-changed' | 'profile-conflict' };

/**
 * Freezes the exact authority/profile CAS point used by one primary-profile edit transaction.
 */
export function capturePrimaryProfileTransactionBase(
  state: CompanionDeviceAuthorityState,
): PrimaryProfileTransactionBase {
  const activeProfileSignature = serializeSharedCompanionPreferences(state.activeProfile);
  const currentDeviceProfileSignature = serializeSharedCompanionPreferences(
    state.currentDeviceProfile,
  );
  if (!state.currentDeviceIsPrimary
    || state.currentDeviceId !== state.primaryDeviceId
    || state.authorityRevision <= 0
    || state.activeProfileRevision <= 0
    || state.currentDeviceProfileRevision !== state.activeProfileRevision
    || state.currentDeviceProfileHash !== state.activeProfileHash
    || currentDeviceProfileSignature !== activeProfileSignature) {
    throw new Error('保存前的主设备共享配置版本不一致，无法开始修改。');
  }

  return {
    registryId: state.registryId,
    currentDeviceId: state.currentDeviceId,
    primaryDeviceId: state.primaryDeviceId,
    authorityRevision: state.authorityRevision,
    activeProfileRevision: state.activeProfileRevision,
    activeProfileHash: state.activeProfileHash,
    activeProfileSignature,
    currentDeviceProfileRevision: state.currentDeviceProfileRevision,
    currentDeviceProfileHash: state.currentDeviceProfileHash,
    currentDeviceProfileSignature,
  };
}

/**
 * Reduces any poll, refresh or mutation response against the frozen transaction baseline.
 * State revision is intentionally excluded: presence/label metadata may change independently.
 */
export function resolvePrimaryProfileObservation(
  base: PrimaryProfileTransactionBase,
  desiredProfileSignature: string,
  observation: CompanionDeviceAuthorityState,
): PrimaryProfileObservationDecision {
  const activeProfileSignature = serializeSharedCompanionPreferences(observation.activeProfile);
  const currentDeviceProfileSignature = serializeSharedCompanionPreferences(
    observation.currentDeviceProfile,
  );
  const sameBinding = observation.registryId === base.registryId
    && observation.currentDeviceId === base.currentDeviceId
    && observation.primaryDeviceId === base.primaryDeviceId
    && observation.currentDeviceIsPrimary
    && observation.currentDeviceId === observation.primaryDeviceId;
  if (!sameBinding) {
    return { action: 'rollback-draft', reason: 'authority-changed' };
  }

  const sameBaseline = observation.authorityRevision === base.authorityRevision
    && observation.activeProfileRevision === base.activeProfileRevision
    && observation.activeProfileHash === base.activeProfileHash
    && activeProfileSignature === base.activeProfileSignature
    && observation.currentDeviceProfileRevision === base.currentDeviceProfileRevision
    && observation.currentDeviceProfileHash === base.currentDeviceProfileHash
    && currentDeviceProfileSignature === base.currentDeviceProfileSignature;
  if (sameBaseline) {
    return { action: 'retain-draft', reason: 'same-authority-baseline' };
  }

  const exactCommittedSuccess = observation.authorityRevision === base.authorityRevision + 1
    && observation.activeProfileRevision === base.activeProfileRevision + 1
    && observation.currentDeviceProfileRevision === base.currentDeviceProfileRevision + 1
    && observation.activeProfileHash === observation.currentDeviceProfileHash
    && activeProfileSignature === desiredProfileSignature
    && currentDeviceProfileSignature === desiredProfileSignature;
  if (exactCommittedSuccess) {
    return { action: 'confirm-draft', reason: 'desired-profile-committed' };
  }

  const authorityChanged = observation.authorityRevision !== base.authorityRevision
    && observation.authorityRevision !== base.authorityRevision + 1;
  return {
    action: 'rollback-draft',
    reason: authorityChanged ? 'authority-changed' : 'profile-conflict',
  };
}
