import type { SpecialBusinessContext } from '@/companion/types';
import {
  isPhaseTwoContext,
  isPhaseThreeContext,
  normalizeRole,
  STORY_YUYUKO_CHALLENGE_TYPE,
  YUYUKO_BOSS_ROLE,
  YUYUKO_CHALLENGE_TYPES,
} from '@/companion/domain/special-business/rules/shared';
import {
  emptySpecialBusinessOrderRule,
  type SpecialBusinessOrderRule,
} from '@/companion/domain/special-business/rules/types';

export function buildYuyukoChallengeOrderRule(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
): SpecialBusinessOrderRule {
  if (!specialBusiness?.active || !YUYUKO_CHALLENGE_TYPES.has(specialBusiness.challengeType)) return emptySpecialBusinessOrderRule();

  const phaseThree = isPhaseThreeContext(specialBusiness.phase);
  const phaseTwo = isPhaseTwoContext(specialBusiness.phase);
  const yuyukoOrder = normalizeRole(role) === YUYUKO_BOSS_ROLE || specialBusiness.category === 'boss';
  const requiresSafeEvaluation = (phaseTwo || phaseThree) && yuyukoOrder;
  const requiresProgress = phaseThree && yuyukoOrder;
  const storyChallenge = specialBusiness.challengeType === STORY_YUYUKO_CHALLENGE_TYPE;
  return {
    requiresWackyFoodTarget: false,
    foodTargetTags: [],
    requiresBaseOrderMatch: requiresSafeEvaluation,
    requiresHighEvaluation: requiresSafeEvaluation,
    highEvaluationMinPreferenceMatches: requiresSafeEvaluation ? 1 : 0,
    preferHighFoodLevel: requiresSafeEvaluation,
    preferHighBeverageLevel: requiresSafeEvaluation,
    preferKoishiDamage: false,
    preferYuyukoSafeEvaluation: phaseTwo && yuyukoOrder,
    preferYuyukoProgress: requiresProgress,
    reason: requiresProgress
      ? storyChallenge
        ? '剧情版幽幽子第三阶段需要满足原订单，避开幽幽子厌恶 Tag，并优先选择等级合计可触发橙评/粉评的组合；仅能达到绿评的原订单可清理但不承诺推进。'
        : '重修版幽幽子第三阶段需要满足原订单，避开幽幽子厌恶 Tag，并优先选择等级合计可触发橙评/粉评的组合；仅能达到绿评的原订单可清理但不承诺推进。'
      : phaseTwo && yuyukoOrder
        ? '幽幽子第二阶段会周期性触发负面符卡，自动化只选择满足原订单、避开幽幽子厌恶 Tag 且预计可达橙评/粉评的组合。'
      : '',
  };
}

export function isYuyukoChallengeOrderRole(role: string | null | undefined): boolean {
  return normalizeRole(role) === YUYUKO_BOSS_ROLE;
}
