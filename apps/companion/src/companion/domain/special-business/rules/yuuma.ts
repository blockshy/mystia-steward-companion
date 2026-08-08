import type { SpecialBusinessContext } from '@/companion/types';
import {
  BLOOD_POND_HELL_CHALLENGE_TYPE,
  normalizeRole,
  normalizeSpecialBusinessTags,
  YUUMA_BOSS_ROLE,
  YUUMA_UNVERIFIED_ROLE,
} from '@/companion/domain/special-business/rules/shared';
import {
  emptySpecialBusinessOrderRule,
  type SpecialBusinessOrderRule,
} from '@/companion/domain/special-business/rules/types';

const YUUMA_TARGET_TAG_COUNT = 2;

export function buildYuumaChallengeOrderRule(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
): SpecialBusinessOrderRule {
  if (!specialBusiness?.active
    || specialBusiness.challengeType !== BLOOD_POND_HELL_CHALLENGE_TYPE) {
    return emptySpecialBusinessOrderRule();
  }

  const normalizedRole = normalizeRole(role);
  if (normalizedRole === YUUMA_UNVERIFIED_ROLE) {
    const challengeLabel = specialBusiness.displayName.trim() || specialBusiness.challengeType;
    return {
      ...emptySpecialBusinessOrderRule(),
      blockingReason: `${challengeLabel}订单角色身份尚未确认，推荐已暂停。`,
    };
  }
  if (normalizedRole !== YUUMA_BOSS_ROLE) return emptySpecialBusinessOrderRule();

  const targetTags = normalizeSpecialBusinessTags(specialBusiness.foodTargetTags);
  const targetReady = targetTags.length === YUUMA_TARGET_TAG_COUNT;
  const challengeLabel = specialBusiness.displayName.trim() || specialBusiness.challengeType;
  return {
    foodTarget: {
      enforcement: 'require',
      match: 'all',
      tags: targetTags,
    },
    requiredExtraIngredientIds: [],
    forbiddenExtraIngredientIds: [],
    blockingReason: targetReady
      ? ''
      : `${challengeLabel}需要同时读取 ${YUUMA_TARGET_TAG_COUNT} 个料理目标 Tag，当前读取到 ${targetTags.length} 个。`,
    requiresBaseOrderMatch: true,
    requiresHighEvaluation: false,
    highEvaluationMinPreferenceMatches: 0,
    preferHighFoodLevel: false,
    preferHighBeverageLevel: false,
    preferKoishiDamage: false,
    preferYuyukoPositiveSpell: false,
    yuyukoProgressEvaluationMode: 'none',
    reason: targetReady
      ? `${challengeLabel}要求原订单成立，并同时满足目标 Tag：${targetTags.join('、')}`
      : `${challengeLabel}目标 Tag 尚未完整读取。`,
  };
}

export function isYuumaChallengeOrderRole(role: string | null | undefined): boolean {
  const normalizedRole = normalizeRole(role);
  return normalizedRole === YUUMA_BOSS_ROLE
    || normalizedRole === YUUMA_UNVERIFIED_ROLE;
}
