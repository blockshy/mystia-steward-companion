import {
  buildExecutionTarget,
  buildNormalTargetRuntimeContext,
  buildSyntheticDemand,
  emptySelection,
  findOrderRecipe,
  hasNoHardFailures,
  selectBestPair,
} from '@/companion/domain/special-business/normal-targets/shared';
import { resolveExactSpecialBusinessCustomer } from '@/companion/domain/special-business/customer-profile';
import {
  BLOOD_POND_HELL_CHALLENGE_TYPE,
  buildYuumaChallengeOrderRule,
  normalizeRole,
  YUUMA_BOSS_ROLE,
  YUUMA_CHARACTER_ID,
  YUUMA_UNVERIFIED_ROLE,
} from '@/companion/domain/special-business/rules';
import type {
  SpecialBusinessNormalTargetArgs,
  SpecialBusinessNormalTargetSelection,
} from '@/companion/domain/special-business/types';
import { DEFAULT_RECOMMENDATION_DATA } from '@/lib/recommendation-data';
import {
  buildRareBeverageCandidates,
  buildRareFoodCandidates,
  type BeverageCandidate,
  type FoodCandidate,
} from '@/recommendation-engine';

export function selectYuumaNormalExecutionTarget({
  order,
  specialBusiness,
  runtime,
  preferences,
  data = DEFAULT_RECOMMENDATION_DATA,
}: SpecialBusinessNormalTargetArgs): SpecialBusinessNormalTargetSelection {
  if (!specialBusiness?.active
    || specialBusiness.challengeType !== BLOOD_POND_HELL_CHALLENGE_TYPE) {
    return emptySelection();
  }
  const challengeLabel = specialBusiness.displayName.trim() || specialBusiness.challengeType;
  const role = normalizeRole(order.specialBusinessRole);
  if (role === YUUMA_UNVERIFIED_ROLE) {
    return {
      target: null,
      message: `${challengeLabel}订单角色身份尚未确认，自动化目标已暂停。`,
    };
  }
  if (role !== YUUMA_BOSS_ROLE) return emptySelection();

  if (order.runtimeGuestId !== YUUMA_CHARACTER_ID) {
    return {
      target: null,
      message: `${challengeLabel}订单运行时角色身份不完整：需要 runtimeGuestId=${YUUMA_CHARACTER_ID}，当前为 ${order.runtimeGuestId ?? 'missing'}。`,
    };
  }

  const rule = buildYuumaChallengeOrderRule(specialBusiness, order.specialBusinessRole);
  if (rule.blockingReason) return { target: null, message: rule.blockingReason };
  if (!runtime || data.source !== 'runtime') {
    return { target: null, message: `${challengeLabel}等待完整运行时推荐数据后再计算料理目标。` };
  }

  const context = buildNormalTargetRuntimeContext(runtime, preferences, data);
  if (!context) {
    return { target: null, message: `${challengeLabel}缺少完整库存、厨具或菜单运行时数据，暂不计算料理目标。` };
  }
  const originalRecipe = findOrderRecipe(order, data);
  if (!originalRecipe) {
    return { target: null, message: `${challengeLabel}无法找到原订单料理 ${order.foodName || `#${order.foodId}`} 的配方数据。` };
  }
  const originalBeverage = data.beverages.find((beverage) => beverage.id === order.beverageId) ?? null;
  if (!originalBeverage) {
    return { target: null, message: `${challengeLabel}无法找到原订单酒水 ${order.beverageName || `#${order.beverageId}`} 的数据。` };
  }

  const customer = resolveExactSpecialBusinessCustomer(data, YUUMA_CHARACTER_ID);
  if (!customer) {
    return {
      target: null,
      message: `${challengeLabel}缺少运行时 characterId=${YUUMA_CHARACTER_ID} 的完整料理、酒水喜好档案，暂不计算料理目标。`,
    };
  }

  const demand = buildSyntheticDemand(customer, '', '', rule.foodTarget);
  const exactOrderData = {
    ...data,
    recipes: [originalRecipe],
    beverages: [originalBeverage],
  };
  const foodCandidates = buildRareFoodCandidates(exactOrderData, demand, context)
    .filter(hasNoHardFailures)
    .filter((candidate) => candidate.recipe.id === originalRecipe.id);
  const beverageCandidates = buildRareBeverageCandidates(exactOrderData, demand, context)
    .filter(hasNoHardFailures)
    .filter((candidate) => candidate.beverage.id === originalBeverage.id);
  const best = selectBestPair({
    foodCandidates,
    beverageCandidates,
    scoreFood: scoreYuumaFood,
    scoreBeverage: scoreYuumaBeverage,
    scorePair: (food, beverage) => scoreYuumaFood(food) + scoreYuumaBeverage(beverage),
  });
  if (!best) {
    return {
      target: null,
      message: `原订单料理 ${originalRecipe.name} 无法通过现有材料同时满足目标 Tag：${rule.foodTarget.tags.join('、')}。`,
    };
  }

  return {
    target: buildExecutionTarget(
      order,
      best.food,
      best.beverage,
      `保持原订单料理与酒水，并同时满足${challengeLabel}目标 Tag：${rule.foodTarget.tags.join('、')}`,
      { specialTargetFoodTags: rule.foodTarget.tags },
    ),
    message: '',
  };
}

function scoreYuumaFood(candidate: FoodCandidate): number {
  return candidate.matchedSpecialFoodTargetTags.length * 10000
    + candidate.matchedPositiveTags.length * 100
    - candidate.matchedNegativeTags.length * 1000
    - candidate.extraIngredients.length * 10
    - candidate.resourcePressure;
}

function scoreYuumaBeverage(candidate: BeverageCandidate): number {
  return candidate.matchedTags.length * 100
    + Math.min(99, Math.max(0, candidate.ownedQuantity));
}
