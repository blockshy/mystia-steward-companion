import type {
  AutomationRuntimeEvent,
  RecommendationStateSnapshot,
  SpecialBusinessContext,
  SpecialFoodTargetWirePolicy,
} from '@/companion/types';
import { getNormalExecutionCookerRequirement } from '@/companion/domain/special-business/normal-targets';
import {
  buildWackyRejectedRecipeKeyForRareRecipe,
  buildWackyRejectedRecipeKeyFromEvent as buildFallbackRejectedRecipeKeyFromEvent,
  emptySpecialBusinessOrderRule,
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
import { yuumaChallengeModule } from '@/companion/domain/special-business/modules/yuuma-challenge';
import type {
  SpecialBusinessNormalTargetArgs,
  SpecialBusinessNormalTargetSelection,
  SpecialBusinessRuleModule,
} from '@/companion/domain/special-business/types';
import {
  applySpecialFoodTargetWirePolicy,
  createSpecialFoodTargetWirePolicy,
  emptySpecialFoodTargetWirePolicy,
} from '@/companion/domain/special-business/target-policy';

const modules: readonly SpecialBusinessRuleModule[] = [
  wackyCookingCompetitionModule,
  yuyukoChallengeModule,
  yuumaChallengeModule,
  passiveSpecialBusinessModule,
];
const NORMAL_TARGET_CACHE_LIMIT = 64;
const normalTargetCache = new Map<string, SpecialBusinessNormalTargetSelection>();
let normalTargetDataSignature = '';

function resolveSpecialBusinessModule(
  specialBusiness: SpecialBusinessContext | null | undefined,
): SpecialBusinessRuleModule {
  if (!specialBusiness?.active) return passiveSpecialBusinessModule;
  return findRegisteredSpecialBusinessModule(specialBusiness) ?? passiveSpecialBusinessModule;
}

function findRegisteredSpecialBusinessModule(
  specialBusiness: SpecialBusinessContext | null | undefined,
): SpecialBusinessRuleModule | null {
  if (!specialBusiness?.active) return passiveSpecialBusinessModule;
  return modules.find((module) => module.challengeTypes.includes(specialBusiness.challengeType))
    ?? null;
}

export function buildSpecialBusinessOrderRule(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
): SpecialBusinessOrderRule {
  if (specialBusiness && specialBusiness.challengeTypeAvailable !== true) {
    return {
      ...emptySpecialBusinessOrderRule(),
      blockingReason: specialBusiness.error?.trim()
        || '特殊经营类型暂时无法读取，推荐已暂停。',
    };
  }
  return resolveSpecialBusinessModule(specialBusiness).buildOrderRule(specialBusiness, role);
}

export function buildSpecialFoodTargetWirePolicy(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
  businessGeneration: number,
): SpecialFoodTargetWirePolicy {
  const rule = buildSpecialBusinessOrderRule(specialBusiness, role);
  return createSpecialFoodTargetWirePolicy(
    specialBusiness,
    businessGeneration,
    rule.foodTarget,
  );
}

export function requiresSpecialBusinessNormalExecutionTarget(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
): boolean {
  if (!specialBusiness?.active) return false;
  if (specialBusiness.challengeTypeAvailable !== true) return true;
  const module = findRegisteredSpecialBusinessModule(specialBusiness);
  if (!module) return true;
  if (module === passiveSpecialBusinessModule) return false;
  return module.requiresNormalExecutionTarget?.(specialBusiness, role) === true;
}

export function buildSpecialBusinessRecommendationSignature(
  specialBusiness: SpecialBusinessContext | null | undefined,
  includeCookingCountdown = false,
): string {
  if (!specialBusiness) return 'special:missing';
  if (!specialBusiness.challengeTypeAvailable) {
    return [
      'special:unavailable',
      specialBusiness.challengeType,
      specialBusiness.error ?? '',
    ].join('|');
  }
  if (!specialBusiness.active) return 'special:none';

  const module = resolveSpecialBusinessModule(specialBusiness);
  const values: Array<string | number | boolean> = [
    'special:active',
    specialBusiness.challengeType,
    stableStringArraySignature(specialBusiness.foodTargetTags),
    stableStringArraySignature(specialBusiness.beverageTargetTags),
  ];
  if (module === wackyCookingCompetitionModule) {
    values.push(specialBusiness.phase ?? '');
    if (includeCookingCountdown) {
      values.push(buildTargetTagProgressSignature(specialBusiness.targetTagTimeProgress));
    }
    values.push(
      specialBusiness.wackyKoishiShieldBroken ?? '',
      stableStringArraySignature(specialBusiness.wackyKoishiFoodPreferenceTags),
      stableStringArraySignature(specialBusiness.wackyKoishiFoodHateTags),
      stableStringArraySignature(specialBusiness.wackyKoishiBeveragePreferenceTags),
    );
    if (specialBusiness.wackyKoishiShieldBroken === true) {
      values.push(
        specialBusiness.currentValue ?? '',
        specialBusiness.maxValue ?? '',
        specialBusiness.targetValue ?? '',
      );
    }
  } else if (module === yuyukoChallengeModule) {
    values.push(specialBusiness.phase ?? '');
  } else if (module === yuumaChallengeModule) {
    values.push(specialBusiness.yuumaFoodTargetRevision);
  }
  return values.join('|');
}

export function selectSpecialBusinessNormalExecutionTarget(
  args: SpecialBusinessNormalTargetArgs,
): SpecialBusinessNormalTargetSelection {
  if (normalTargetDataSignature !== args.dataSignature) {
    normalTargetCache.clear();
    normalTargetDataSignature = args.dataSignature;
  }

  const cacheKey = buildNormalTargetCacheKey(args);
  const cached = normalTargetCache.get(cacheKey);
  if (cached) return cached;

  if (args.specialBusiness && args.specialBusiness.challengeTypeAvailable !== true) {
    const selection = {
      target: null,
      message: args.specialBusiness.error?.trim()
        || '特殊经营类型暂时无法读取，自动化目标已暂停。',
    };
    normalTargetCache.set(cacheKey, selection);
    trimNormalTargetCache();
    return selection;
  }

  const registeredModule = findRegisteredSpecialBusinessModule(args.specialBusiness);
  if (args.specialBusiness?.active && !registeredModule) {
    const challengeLabel = args.specialBusiness.displayName.trim()
      || args.specialBusiness.challengeType
      || '当前特殊经营';
    const selection = {
      target: null,
      message: `${challengeLabel}尚未适配普客自动化执行目标，当前订单已暂停。`,
    };
    normalTargetCache.set(cacheKey, selection);
    trimNormalTargetCache();
    return selection;
  }

  const module = registeredModule ?? passiveSpecialBusinessModule;
  const selection = module.selectNormalExecutionTarget?.(args)
    ?? { target: null, message: '' };
  normalTargetCache.set(cacheKey, selection);
  trimNormalTargetCache();
  return selection;
}

export function buildWackyRejectedRecipeKeyFromEvent(event: AutomationRuntimeEvent): string {
  return wackyCookingCompetitionModule.buildRejectedRecipeKeyFromEvent?.(event)
    ?? buildFallbackRejectedRecipeKeyFromEvent(event);
}

export function isSpecialBusinessOrderRole(role: string | null | undefined): boolean {
  return modules.some((module) => module.isOrderRole?.(role));
}

function buildNormalTargetCacheKey({
  order,
  specialBusiness,
  runtime,
  preferences,
  rejectedRecipeKeys,
}: SpecialBusinessNormalTargetArgs): string {
  return [
    buildNormalOrderSignature(order),
    buildSpecialBusinessRecommendationSignature(specialBusiness, true),
    buildRuntimeSignature(runtime),
    buildPreferenceSignature(preferences),
    stableStringArraySignature(rejectedRecipeKeys),
  ].join('\n');
}

function buildNormalOrderSignature(order: SpecialBusinessNormalTargetArgs['order']): string {
  return [
    order.traceId ?? '',
    order.orderKey ?? '',
    order.deskCode,
    order.guestId ?? '',
    order.runtimeGuestId ?? '',
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

function buildRuntimeSignature(runtime: SpecialBusinessNormalTargetArgs['runtime']): string {
  if (!runtime) return 'runtime:null';
  return [
    stableNumberArraySignature(runtime.availableRecipeIds),
    stableNumberArraySignature(runtime.availableBeverageIds),
    stableNumberArraySignature(runtime.availableIngredientIds),
    stableNumberRecordSignature(runtime.ownedIngredientQty),
    stableNumberRecordSignature(runtime.ownedBeverageQty),
    stableNumberArraySignature(runtime.placedCookerTypeIds),
    buildPlacedCookerSemanticSignature(runtime.placedCookers),
    runtime.placedCookerSnapshotComplete ? 1 : 0,
    runtime.placedCookerControllerCount,
    runtime.placedCookerEmptyControllerCount,
    runtime.placedCookerLockedControllerCount,
    runtime.placedCookerReadFailureCount,
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

function buildPlacedCookerSemanticSignature(
  cookers: RecommendationStateSnapshot['placedCookers'],
): string {
  return [...new Set((cookers ?? []).map((cooker) => [
    cooker.controllerIndex,
    cooker.controllerIdentity,
    cooker.gridPosition.x,
    cooker.gridPosition.y,
    cooker.gridPosition.z,
    cooker.challengeLocked ? 1 : 0,
    cooker.couldOpen ? 1 : 0,
    cooker.name.trim(),
    stableNumberArraySignature(cooker.typeIds),
    stableStringArraySignature(cooker.typeNames),
  ].join(':')))].sort(compareOrdinal).join(';');
}

function compareOrdinal(left: string, right: string): number {
  if (left < right) return -1;
  if (left > right) return 1;
  return 0;
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
  isPhaseThreeContext,
  isWackyKoishiBossFullFeedContext,
  isWackyTargetTagMismatchEvent,
  normalizeSpecialBusinessTags,
  WACKY_TARGET_TAG_COOKING_MIN_PROGRESS,
  applySpecialFoodTargetWirePolicy,
  emptySpecialFoodTargetWirePolicy,
};

export type {
  SpecialBusinessNormalTargetArgs,
  SpecialBusinessNormalTargetSelection,
  SpecialBusinessOrderRule,
  SpecialBusinessRuleModule,
};
