import type { AutomationRuntimeEvent, SpecialBusinessContext } from '@/companion/types';
import { getNormalExecutionCookerRequirement } from '@/companion/domain/special-business/normal-targets';
import {
  buildWackyRejectedRecipeKeyForRareRecipe,
  buildWackyRejectedRecipeKeyFromEvent as buildFallbackRejectedRecipeKeyFromEvent,
  getOrderSpecialBusinessRole,
  hasMatchingSpecialBusinessTag,
  isPhaseThreeContext,
  getWackyTargetTagCountdownDeferral,
  isWackyKoishiBossFullFeedContext,
  isWackyTargetTagMismatchEvent,
  normalizeSpecialBusinessTags,
  WACKY_TARGET_TAG_COOKING_MIN_PROGRESS,
  type SpecialBusinessOrderRule,
} from '@/companion/domain/special-business/rules';
import { passiveSpecialBusinessModule } from '@/companion/domain/special-business/modules/passive-special-business';
import { wackyCookingCompetitionModule } from '@/companion/domain/special-business/modules/wacky-cooking-competition';
import { yuyukoChallengeModule } from '@/companion/domain/special-business/modules/yuyuko-challenge';
import type {
  SpecialBusinessNormalTargetArgs,
  SpecialBusinessNormalTargetSelection,
  SpecialBusinessRuleModule,
} from '@/companion/domain/special-business/types';

const modules: readonly SpecialBusinessRuleModule[] = [
  wackyCookingCompetitionModule,
  yuyukoChallengeModule,
];
const NORMAL_TARGET_CACHE_LIMIT = 64;
const normalTargetCache = new Map<string, SpecialBusinessNormalTargetSelection>();

export function resolveSpecialBusinessModule(
  specialBusiness: SpecialBusinessContext | null | undefined,
): SpecialBusinessRuleModule {
  if (!specialBusiness?.active) return passiveSpecialBusinessModule;
  return modules.find((module) => module.challengeTypes.includes(specialBusiness.challengeType))
    ?? passiveSpecialBusinessModule;
}

export function buildSpecialBusinessOrderRule(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
): SpecialBusinessOrderRule {
  return resolveSpecialBusinessModule(specialBusiness).buildOrderRule(specialBusiness, role);
}

export function buildSpecialBusinessFoodTargetSignature(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
): string {
  const rule = buildSpecialBusinessOrderRule(specialBusiness, role);
  if (!specialBusiness?.active || !rule.requiresWackyFoodTarget || rule.foodTargetTags.length === 0) return '';

  const tags = [...rule.foodTargetTags]
    .sort((left, right) => left.localeCompare(right, 'zh-Hans-CN'))
    .join('&');
  return [
    specialBusiness.challengeType,
    (role ?? '').trim(),
    specialBusiness.phase ?? '',
    `food:${tags}`,
  ].join('|');
}

export function selectSpecialBusinessNormalExecutionTarget(
  args: SpecialBusinessNormalTargetArgs,
): SpecialBusinessNormalTargetSelection {
  const cacheKey = buildNormalTargetCacheKey(args);
  const cached = normalTargetCache.get(cacheKey);
  if (cached) return cached;

  const selection = resolveSpecialBusinessModule(args.specialBusiness).selectNormalExecutionTarget?.(args)
    ?? { target: null, message: '' };
  normalTargetCache.set(cacheKey, selection);
  trimNormalTargetCache();
  return selection;
}

export function buildWackyRejectedRecipeKeyFromEvent(event: AutomationRuntimeEvent): string {
  return resolveSpecialBusinessModuleFromEvent(event).buildRejectedRecipeKeyFromEvent?.(event)
    ?? buildFallbackRejectedRecipeKeyFromEvent(event);
}

export function isSpecialBusinessOrderRole(role: string | null | undefined): boolean {
  return modules.some((module) => module.isOrderRole?.(role));
}

function resolveSpecialBusinessModuleFromEvent(event: AutomationRuntimeEvent): SpecialBusinessRuleModule {
  return event.targetFoodTags && event.targetFoodTags.length > 0
    ? wackyCookingCompetitionModule
    : passiveSpecialBusinessModule;
}

function buildNormalTargetCacheKey({
  order,
  specialBusiness,
  runtime,
  preferences,
  data,
  rejectedRecipeKeys,
}: SpecialBusinessNormalTargetArgs): string {
  return [
    buildNormalOrderSignature(order),
    buildSpecialBusinessSignature(specialBusiness),
    buildRuntimeSignature(runtime),
    buildPreferenceSignature(preferences),
    data?.source ?? '',
    stableStringArraySignature(rejectedRecipeKeys),
  ].join('\n');
}

function buildNormalOrderSignature(order: SpecialBusinessNormalTargetArgs['order']): string {
  return [
    order.traceId ?? '',
    order.orderKey ?? '',
    order.deskCode,
    order.guestId ?? '',
    order.guestName,
    order.specialBusinessRole ?? '',
    stableStringArraySignature(order.foodPreferenceTags),
    stableStringArraySignature(order.beveragePreferenceTags),
    order.fund ?? '',
    order.baseFundCarry ?? '',
    order.maxFundCarry ?? '',
    order.extraFundByBuff ?? '',
    order.willPayMoney ?? '',
    order.remainingOrderCount ?? '',
    order.foodId,
    order.foodName,
    order.beverageId,
    order.beverageName,
    order.hasServedFood ? 1 : 0,
    order.hasServedBeverage ? 1 : 0,
    order.readyToEvaluate ? 1 : 0,
    order.hasEvaluated ? 1 : 0,
    order.controllerAvailable === false ? 0 : 1,
    order.canAutomate === false ? 0 : 1,
    order.actionBlockReason ?? '',
    order.firstSeenAtUtc ?? '',
    order.source,
  ].join('|');
}

function buildSpecialBusinessSignature(specialBusiness: SpecialBusinessNormalTargetArgs['specialBusiness']): string {
  if (!specialBusiness?.active) return 'none';
  return [
    specialBusiness.challengeType,
    specialBusiness.phase ?? '',
    stableStringArraySignature(specialBusiness.foodTargetTags),
    stableStringArraySignature(specialBusiness.beverageTargetTags),
    buildTargetTagProgressSignature(specialBusiness.targetTagTimeProgress),
    specialBusiness.wackyKoishiShieldBroken ?? '',
    stableStringArraySignature(specialBusiness.wackyKoishiFoodPreferenceTags),
    stableStringArraySignature(specialBusiness.wackyKoishiFoodHateTags),
    stableStringArraySignature(specialBusiness.wackyKoishiBeveragePreferenceTags),
    specialBusiness.currentValue ?? '',
    specialBusiness.maxValue ?? '',
    specialBusiness.targetValue ?? '',
    specialBusiness.recommendationPolicy,
    specialBusiness.automationPolicy,
  ].join('|');
}

function buildRuntimeSignature(runtime: SpecialBusinessNormalTargetArgs['runtime']): string {
  if (!runtime) return 'runtime:null';
  return [
    stableNumberArraySignature(runtime.availableRecipeIds),
    stableNumberArraySignature(runtime.availableBeverageIds),
    stableNumberArraySignature(runtime.availableIngredientIds),
    stableNumberRecordSignature(runtime.ownedIngredientQty),
    stableNumberRecordSignature(runtime.ownedBeverageQty),
    stableNumberArraySignature(runtime.placedCookerTypeIds),
    (runtime.placedCookers ?? [])
      .map((cooker) => [
        cooker.controllerIndex,
        cooker.name,
        cooker.isOpen ? 1 : 0,
        stableNumberArraySignature(cooker.typeIds),
        stableStringArraySignature(cooker.typeNames),
      ].join(':'))
      .join(';'),
    runtime.popularFoodTag ?? '',
    runtime.popularHateFoodTag ?? '',
    runtime.famousShopEnabled ? 1 : 0,
  ].join('|');
}

function buildPreferenceSignature(preferences: SpecialBusinessNormalTargetArgs['preferences']): string {
  return [
    preferences.filterMissingCookers ? 1 : 0,
    preferences.recommendationBudgetPolicy,
    stableNumberArraySignature(preferences.recommendationExclusions.excludedIngredientIds),
    stableNumberArraySignature(preferences.recommendationExclusions.excludedBeverageIds),
  ].join('|');
}

function stableNumberArraySignature(values: readonly number[] | null | undefined): string {
  return [...(values ?? [])].sort((left, right) => left - right).join(',');
}

function buildTargetTagProgressSignature(value: number | null | undefined): string {
  if (!Number.isFinite(value)) return '';
  const progress = Math.max(0, Math.min(1, value ?? 0));
  if (progress >= WACKY_TARGET_TAG_COOKING_MIN_PROGRESS) return 'safe';
  return `wait:${Math.round(progress * 100)}`;
}

function stableStringArraySignature(values: readonly string[] | null | undefined): string {
  return [...(values ?? [])].map((value) => value.trim()).filter(Boolean).sort().join(',');
}

function stableNumberRecordSignature(values: Record<string, number> | null | undefined): string {
  return Object.entries(values ?? {})
    .sort(([left], [right]) => Number(left) - Number(right))
    .map(([key, value]) => `${key}:${value}`)
    .join(',');
}

function trimNormalTargetCache() {
  if (normalTargetCache.size <= NORMAL_TARGET_CACHE_LIMIT) return;
  const overflow = normalTargetCache.size - NORMAL_TARGET_CACHE_LIMIT;
  const keys = normalTargetCache.keys();
  for (let index = 0; index < overflow; index += 1) {
    const key = keys.next().value;
    if (key === undefined) return;
    normalTargetCache.delete(key);
  }
}

export {
  buildWackyRejectedRecipeKeyForRareRecipe,
  getWackyTargetTagCountdownDeferral,
  getNormalExecutionCookerRequirement,
  getOrderSpecialBusinessRole,
  hasMatchingSpecialBusinessTag,
  isPhaseThreeContext,
  isWackyKoishiBossFullFeedContext,
  isWackyTargetTagMismatchEvent,
  normalizeSpecialBusinessTags,
  WACKY_TARGET_TAG_COOKING_MIN_PROGRESS,
};

export type {
  SpecialBusinessNormalTargetArgs,
  SpecialBusinessNormalTargetSelection,
  SpecialBusinessOrderRule,
  SpecialBusinessRuleModule,
};
