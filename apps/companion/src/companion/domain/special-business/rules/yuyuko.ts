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
import { YUYUKO_POSITIVE_SPELL_MIN_EXTRA_PREFERENCE_MATCHES } from '@/companion/domain/special-business/yuyuko-positive-spell';

export function buildYuyukoChallengeOrderRule(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
): SpecialBusinessOrderRule {
  if (!specialBusiness?.active || !YUYUKO_CHALLENGE_TYPES.has(specialBusiness.challengeType)) return emptySpecialBusinessOrderRule();

  const phaseThree = isPhaseThreeContext(specialBusiness.phase);
  const phaseTwo = isPhaseTwoContext(specialBusiness.phase);
  const yuyukoOrder = normalizeRole(role) === YUYUKO_BOSS_ROLE || specialBusiness.category === 'boss';
  const requiresQualifiedEvaluation = (phaseTwo || phaseThree) && yuyukoOrder;
  const requiresProgress = phaseThree && yuyukoOrder;
  const storyChallenge = specialBusiness.challengeType === STORY_YUYUKO_CHALLENGE_TYPE;
  const progressEvaluationMode = requiresProgress
    ? storyChallenge
      ? 'story-level-sum'
      : 'retake-tag-order'
    : 'none';
  return {
    requiresWackyFoodTarget: false,
    foodTargetTags: [],
    requiresBaseOrderMatch: requiresQualifiedEvaluation,
    requiresHighEvaluation: requiresQualifiedEvaluation,
    highEvaluationMinPreferenceMatches: phaseTwo && yuyukoOrder
      ? YUYUKO_POSITIVE_SPELL_MIN_EXTRA_PREFERENCE_MATCHES
      : 0,
    preferHighFoodLevel: progressEvaluationMode === 'story-level-sum',
    preferHighBeverageLevel: progressEvaluationMode === 'story-level-sum',
    preferKoishiDamage: false,
    preferYuyukoPositiveSpell: phaseTwo && yuyukoOrder,
    yuyukoProgressEvaluationMode: progressEvaluationMode,
    reason: requiresProgress
      ? storyChallenge
        ? '剧情版幽幽子第三阶段需要满足料理与酒水点单，并选择等级合计可达到满意（Good）/完美（ExGood）的组合。'
        : '重修版幽幽子第三阶段使用稀客原生 Tag 评价：料理与酒水先满足点单，再避开当前稀客厌恶，并至少额外命中一个当前稀客喜好以达到满意（Good）并推进。'
      : phaseTwo && yuyukoOrder
        ? '幽幽子第二阶段需要稀客触发正面符卡；自动化只选择满足当前稀客点单、避开该稀客厌恶 Tag，且排除点单 Tag 后仍预计可达完美（ExGood）的组合。'
      : '',
  };
}

export function isYuyukoChallengeOrderRole(role: string | null | undefined): boolean {
  return normalizeRole(role) === YUYUKO_BOSS_ROLE;
}
