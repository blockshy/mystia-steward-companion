import type {
  SpecialBusinessContext,
  SpecialFoodTargetWirePolicy,
} from '@/companion/types';
import type { SpecialBusinessFoodTargetPolicy } from '@/recommendation-engine';
import {
  BLOOD_POND_HELL_CHALLENGE_TYPE,
  normalizeSpecialBusinessTags,
  WACKY_CHALLENGE_TYPE,
} from '@/companion/domain/special-business/rules/shared';

export function createSpecialFoodTargetWirePolicy(
  specialBusiness: SpecialBusinessContext | null | undefined,
  businessGeneration: number,
  target: SpecialBusinessFoodTargetPolicy,
): SpecialFoodTargetWirePolicy {
  const challenge = specialBusiness?.challengeType ?? '';
  const owner = resolveSpecialTargetOwner(challenge);
  const generation = Number.isSafeInteger(businessGeneration) && businessGeneration > 0
    ? businessGeneration
    : 0;
  const yuumaRevision = specialBusiness?.yuumaFoodTargetRevision;
  const revision = owner === 'yuuma'
    && typeof yuumaRevision === 'number'
    && Number.isSafeInteger(yuumaRevision)
    && yuumaRevision > 0
    ? yuumaRevision
    : 0;
  const tags = normalizeSpecialBusinessTags(target.tags).sort(compareOrdinal);
  if (!specialBusiness?.active
    || !challenge
    || !owner
    || generation <= 0
    || (owner === 'yuuma' && revision <= 0)
    || target.enforcement !== 'require'
    || tags.length === 0) {
    return emptySpecialFoodTargetWirePolicy();
  }

  const matchMode = target.match;
  return {
    specialTargetChallenge: challenge,
    specialTargetOwner: owner,
    specialTargetGeneration: generation,
    specialTargetRevision: revision,
    specialTargetFoodTags: tags,
    specialTargetMatchMode: matchMode,
    specialTargetSignature: [
      challenge,
      owner,
      `generation:${generation}`,
      `match:${matchMode}`,
      `food:${tags.join(',')}`,
    ].join('|'),
  };
}

function resolveSpecialTargetOwner(challenge: string): string {
  if (challenge === WACKY_CHALLENGE_TYPE) return 'koishi';
  if (challenge === BLOOD_POND_HELL_CHALLENGE_TYPE) return 'yuuma';
  return '';
}

export function applySpecialFoodTargetWirePolicy<T extends SpecialFoodTargetWirePolicy>(
  target: T,
  policy: SpecialFoodTargetWirePolicy,
): T {
  return {
    ...target,
    ...policy,
    specialTargetFoodTags: [...policy.specialTargetFoodTags],
  };
}

export function emptySpecialFoodTargetWirePolicy(): SpecialFoodTargetWirePolicy {
  return {
    specialTargetChallenge: '',
    specialTargetOwner: '',
    specialTargetGeneration: 0,
    specialTargetRevision: 0,
    specialTargetFoodTags: [],
    specialTargetMatchMode: '',
    specialTargetSignature: '',
  };
}

function compareOrdinal(left: string, right: string): number {
  if (left < right) return -1;
  if (left > right) return 1;
  return 0;
}
