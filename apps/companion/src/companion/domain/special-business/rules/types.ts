export interface SpecialBusinessOrderRule {
  requiresWackyFoodTarget: boolean;
  foodTargetTags: string[];
  requiresBaseOrderMatch: boolean;
  requiresHighEvaluation: boolean;
  highEvaluationMinPreferenceMatches: number;
  preferHighFoodLevel: boolean;
  preferHighBeverageLevel: boolean;
  preferKoishiDamage: boolean;
  preferYuyukoPositiveSpell: boolean;
  preferYuyukoProgress: boolean;
  reason: string;
}

export function emptySpecialBusinessOrderRule(): SpecialBusinessOrderRule {
  return {
    requiresWackyFoodTarget: false,
    foodTargetTags: [],
    requiresBaseOrderMatch: false,
    requiresHighEvaluation: false,
    highEvaluationMinPreferenceMatches: 0,
    preferHighFoodLevel: false,
    preferHighBeverageLevel: false,
    preferKoishiDamage: false,
    preferYuyukoPositiveSpell: false,
    preferYuyukoProgress: false,
    reason: '',
  };
}
