import type { SpecialBusinessFoodTargetPolicy } from '@/recommendation-engine';

export type YuyukoProgressEvaluationMode =
  | 'none'
  | 'story-level-sum'
  | 'retake-tag-order';

export interface SpecialBusinessOrderRule {
  foodTarget: SpecialBusinessFoodTargetPolicy;
  requiredExtraIngredientIds: number[];
  forbiddenExtraIngredientIds: number[];
  blockingReason: string;
  requiresBaseOrderMatch: boolean;
  requiresHighEvaluation: boolean;
  highEvaluationMinPreferenceMatches: number;
  preferHighFoodLevel: boolean;
  preferHighBeverageLevel: boolean;
  preferKoishiDamage: boolean;
  preferYuyukoPositiveSpell: boolean;
  yuyukoProgressEvaluationMode: YuyukoProgressEvaluationMode;
  reason: string;
}

export function emptySpecialBusinessOrderRule(): SpecialBusinessOrderRule {
  return {
    foodTarget: {
      enforcement: 'none',
      match: 'any',
      tags: [],
    },
    requiredExtraIngredientIds: [],
    forbiddenExtraIngredientIds: [],
    blockingReason: '',
    requiresBaseOrderMatch: false,
    requiresHighEvaluation: false,
    highEvaluationMinPreferenceMatches: 0,
    preferHighFoodLevel: false,
    preferHighBeverageLevel: false,
    preferKoishiDamage: false,
    preferYuyukoPositiveSpell: false,
    yuyukoProgressEvaluationMode: 'none',
    reason: '',
  };
}
