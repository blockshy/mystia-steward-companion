import type {
  AutomationRuntimeEvent,
  SpecialBusinessContext,
} from '@/companion/types';
import {
  isPhaseThreeContext,
  isPhaseTwoContext,
  KOISHI_BOSS_ROLE,
  normalizeRole,
  normalizeSpecialBusinessTags,
  matchesSpecialBusinessTags,
  WACKY_CHALLENGE_TYPE,
  WACKY_GHOST_ROLE,
  WACKY_TARGET_ROLE,
  WACKY_TARGET_TAG_COOKING_MIN_PROGRESS,
} from '@/companion/domain/special-business/rules/shared';
import {
  emptySpecialBusinessOrderRule,
  type SpecialBusinessOrderRule,
} from '@/companion/domain/special-business/rules/types';

export function buildWackyCookingCompetitionOrderRule(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
): SpecialBusinessOrderRule {
  if (!specialBusiness?.active || specialBusiness.challengeType !== WACKY_CHALLENGE_TYPE) return emptySpecialBusinessOrderRule();

  const normalizedRole = normalizeRole(role);
  const phaseTwo = isPhaseTwoContext(specialBusiness.phase);
  const phaseThree = isPhaseThreeContext(specialBusiness.phase);
  if (normalizedRole === KOISHI_BOSS_ROLE && phaseThree) {
    const shieldBroken = specialBusiness.wackyKoishiShieldBroken === true;
    return {
      foodTarget: {
        enforcement: 'none',
        match: 'any',
        tags: [],
      },
      requiredExtraIngredientIds: [],
      forbiddenExtraIngredientIds: [],
      blockingReason: '',
      requiresBaseOrderMatch: shieldBroken,
      requiresHighEvaluation: !shieldBroken,
      highEvaluationMinPreferenceMatches: shieldBroken ? 0 : 3,
      preferHighFoodLevel: true,
      preferHighBeverageLevel: true,
      preferKoishiDamage: shieldBroken,
      preferYuyukoPositiveSpell: false,
      yuyukoProgressEvaluationMode: 'none',
      reason: shieldBroken
        ? '怪诞料理三阶段古明地恋本体已破防，需要先满足原订单料理和酒水要求，再按破防期预计伤害优先选择。'
        : '怪诞料理三阶段古明地恋本体需要按场上揭示的正面/厌恶/酒水 Tag 选择高评价组合。',
    };
  }

  const targetTags = normalizeSpecialBusinessTags(specialBusiness.foodTargetTags);
  const requiresTarget = targetTags.length > 0;
  const preferHighEvaluation = phaseTwo || phaseThree;
  return {
    foodTarget: {
      enforcement: requiresTarget ? 'require' : 'none',
      match: 'any',
      tags: requiresTarget ? targetTags : [],
    },
    requiredExtraIngredientIds: [],
    forbiddenExtraIngredientIds: [],
    blockingReason: '',
    requiresBaseOrderMatch: preferHighEvaluation,
    requiresHighEvaluation: preferHighEvaluation,
    highEvaluationMinPreferenceMatches: preferHighEvaluation ? 2 : 0,
    preferHighFoodLevel: preferHighEvaluation,
    preferHighBeverageLevel: preferHighEvaluation,
    preferKoishiDamage: false,
    preferYuyukoPositiveSpell: false,
    yuyukoProgressEvaluationMode: 'none',
    reason: requiresTarget
      ? `怪诞料理目标 Tag：${targetTags.join('、')}${preferHighEvaluation ? '，需要满足原订单并获得最高评价' : ''}`
      : preferHighEvaluation
        ? '怪诞料理需要满足原订单并获得最高评价。'
        : '',
  };
}

export function getWackyTargetTagCountdownDeferral(
  specialBusiness: SpecialBusinessContext | null | undefined,
): string {
  const progress = typeof specialBusiness?.targetTagTimeProgress === 'number'
    ? specialBusiness.targetTagTimeProgress
    : null;
  if (progress == null || progress >= WACKY_TARGET_TAG_COOKING_MIN_PROGRESS) return '';

  return `怪诞料理 Tag 倒计时剩余约 ${Math.max(0, Math.round(progress * 100))}%，等待刷新后再开锅，避免出锅时目标 Tag 已变化。`;
}

export function isWackyKoishiBossFullFeedContext(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
): boolean {
  return specialBusiness?.active === true
    && specialBusiness.challengeType === WACKY_CHALLENGE_TYPE
    && specialBusiness.wackyKoishiShieldBroken === true
    && normalizeRole(role) === KOISHI_BOSS_ROLE;
}

export function isWackyTargetTagMismatchEvent(event: AutomationRuntimeEvent): boolean {
  return event.code === 'cooking-mismatch-stored'
    && event.targetFoodTags != null
    && event.targetFoodTags.length > 0
    && event.actualFoodTags != null
    && event.actualFoodTags.length > 0
    && !matchesSpecialBusinessTags(event.actualFoodTags, event.targetFoodTags, 'any');
}

export function buildWackyRejectedRecipeKeyFromEvent(event: AutomationRuntimeEvent): string {
  if (!isWackyTargetTagMismatchEvent(event)) return '';
  return buildWackyRejectedRecipeKey({
    targetTags: event.targetFoodTags ?? [],
    foodId: event.foodId,
    recipeId: event.recipeId ?? -1,
    extraIngredientIds: event.extraIngredientIds ?? [],
  });
}

export function buildWackyRejectedRecipeKey({
  targetTags,
  foodId,
  recipeId,
  extraIngredientIds,
}: {
  targetTags: readonly string[];
  foodId: number;
  recipeId: number;
  extraIngredientIds: readonly number[];
}): string {
  const normalizedTags = normalizeSpecialBusinessTags(targetTags).sort((left, right) => left.localeCompare(right, 'zh-Hans-CN'));
  if (normalizedTags.length === 0 || foodId < 0) return '';
  const extras = [...extraIngredientIds]
    .filter((id) => Number.isFinite(id) && id >= 0)
    .map((id) => Math.trunc(id))
    .sort((left, right) => left - right)
    .join(',');
  return `${normalizedTags.join('&')}|food:${foodId}|recipe:${recipeId >= 0 ? recipeId : foodId}|extra:${extras}`;
}

export function buildWackyRejectedRecipeKeyForRareRecipe(
  targetTags: readonly string[],
  foodId: number,
  recipeId: number,
  extraIngredientIds: readonly number[],
): string {
  return buildWackyRejectedRecipeKey({
    targetTags,
    foodId,
    recipeId,
    extraIngredientIds,
  });
}

export function isWackyCookingCompetitionOrderRole(role: string | null | undefined): boolean {
  const normalizedRole = normalizeRole(role);
  return normalizedRole === KOISHI_BOSS_ROLE
    || normalizedRole === WACKY_GHOST_ROLE
    || normalizedRole === WACKY_TARGET_ROLE;
}
