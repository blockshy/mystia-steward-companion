import type { SpecialBusinessContext } from '@/companion/types';
import { normalizeSpecialBusinessTags } from '@/companion/domain/special-business/rules/shared';
import {
  emptySpecialBusinessOrderRule,
  type SpecialBusinessOrderRule,
} from '@/companion/domain/special-business/rules/types';

export function buildPassiveSpecialBusinessOrderRule(
  specialBusiness: SpecialBusinessContext | null | undefined,
): SpecialBusinessOrderRule {
  if (!specialBusiness?.active) return emptySpecialBusinessOrderRule();
  const targetTags = normalizeSpecialBusinessTags(specialBusiness.foodTargetTags);
  if (targetTags.length === 0) return emptySpecialBusinessOrderRule();
  return {
    requiresWackyFoodTarget: false,
    foodTargetTags: targetTags,
    requiresBaseOrderMatch: false,
    requiresHighEvaluation: false,
    highEvaluationMinPreferenceMatches: 0,
    preferHighFoodLevel: false,
    preferHighBeverageLevel: false,
    preferKoishiDamage: false,
    preferYuyukoPositiveSpell: false,
    yuyukoProgressEvaluationMode: 'none',
    reason: `特殊经营目标 Tag：${targetTags.join('、')}`,
  };
}
