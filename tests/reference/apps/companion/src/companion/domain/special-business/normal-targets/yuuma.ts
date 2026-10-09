import {
  buildExecutionTarget,
  buildNormalTargetRuntimeContext,
  buildSyntheticDemand,
  emptySelection,
  findOrderRecipe,
  hasNoHardFailures,
  hasNoHardFailuresExcept,
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
import {
  DEFAULT_RECOMMENDATION_DATA,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
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
  const searchedFoodCandidates = buildRareFoodCandidates(exactOrderData, demand, context, {
    preserveTwoTagSpecialTargetReachability: true,
  })
    .filter((candidate) => candidate.recipe.id === originalRecipe.id);
  const beverageCandidates = buildRareBeverageCandidates(exactOrderData, demand, context)
    .filter(hasNoHardFailures)
    .filter((candidate) => candidate.beverage.id === originalBeverage.id);
  const strictBest = selectBestPair({
    foodCandidates: searchedFoodCandidates.filter(hasNoHardFailures),
    beverageCandidates,
    scoreFood: scoreYuumaFood,
    scoreBeverage: scoreYuumaBeverage,
    scorePair: (food, beverage) => scoreYuumaFood(food) + scoreYuumaBeverage(beverage),
  });
  if (strictBest) {
    return {
      target: buildExecutionTarget(
        order,
        strictBest.food,
        strictBest.beverage,
        `保持原订单料理与酒水，并同时满足${challengeLabel}目标 Tag：${rule.foodTarget.tags.join('、')}`,
        { specialTargetFoodTags: rule.foodTarget.tags },
      ),
      message: '',
    };
  }

  const controlledBest = selectBestPair({
    foodCandidates: searchedFoodCandidates
      .filter((candidate) => hasNoHardFailuresExcept(candidate, 'food.special-target-tag')),
    beverageCandidates,
    scoreFood: scoreYuumaFood,
    scoreBeverage: scoreYuumaBeverage,
    scorePair: (food, beverage) => scoreYuumaFood(food) + scoreYuumaBeverage(beverage),
  });
  if (!controlledBest) {
    return {
      target: null,
      message: buildYuumaHardBlockMessage({
        challengeLabel,
        originalRecipe,
        originalBeverage,
        context,
        data,
        targetTags: rule.foodTarget.tags,
      }),
    };
  }

  const matchedTags = controlledBest.food.matchedSpecialFoodTargetTags;
  const matchText = matchedTags.length > 0
    ? `仅命中 ${matchedTags.length}/${rule.foodTarget.tags.length} 个目标 Tag：${matchedTags.join('、')}`
    : `未命中当前目标 Tag：${rule.foodTarget.tags.join('、')}`;
  return {
    target: buildExecutionTarget(
      order,
      controlledBest.food,
      controlledBest.beverage,
      `保持原订单料理与酒水；当前无法同时满足${challengeLabel}目标 Tag，改用受控推进方案（${matchText}）。该方案会交由游戏原生低收益结算，可能造成较低伤害并增加狂暴。`,
      {
        allowYuumaControlledProgression: true,
        specialTargetFoodTags: rule.foodTarget.tags,
      },
    ),
    message: '',
  };
}

function buildYuumaHardBlockMessage({
  challengeLabel,
  originalRecipe,
  originalBeverage,
  context,
  data,
  targetTags,
}: {
  challengeLabel: string;
  originalRecipe: RecommendationDataSet['recipes'][number];
  originalBeverage: RecommendationDataSet['beverages'][number];
  context: NonNullable<ReturnType<typeof buildNormalTargetRuntimeContext>>;
  data: RecommendationDataSet;
  targetTags: readonly string[];
}): string {
  if (!context.availableRecipeIds.has(originalRecipe.id)) {
    return `${challengeLabel}原订单料理 ${originalRecipe.name} 尚未解锁，不能生成受控推进方案。`;
  }

  const ingredientsByName = new Map(data.ingredients.map((ingredient) => [ingredient.name, ingredient]));
  const unavailableBaseIngredients = [...new Set(originalRecipe.ingredients.filter((name) => {
    const ingredient = ingredientsByName.get(name);
    return !ingredient
      || !context.availableIngredientIds.has(ingredient.id)
      || context.disabledIngredientIds.has(ingredient.id)
      || context.excludedIngredientIds.has(ingredient.id);
  }))];
  if (unavailableBaseIngredients.length > 0) {
    return `${challengeLabel}原订单料理 ${originalRecipe.name} 的基础材料当前不可用或已排除：${unavailableBaseIngredients.join('、')}。`;
  }

  if (context.hasCookerSnapshot && !context.placedCookerNames.has(originalRecipe.cooker)) {
    return `${challengeLabel}原订单料理 ${originalRecipe.name} 所需厨具 ${originalRecipe.cooker || '未知'} 当前不可用，不能生成受控推进方案。`;
  }

  if (!context.availableBeverageIds.has(originalBeverage.id)) {
    return `${challengeLabel}原订单酒水 ${originalBeverage.name} 当前不可用，不能生成受控推进方案。`;
  }
  if (context.excludedBeverageIds.has(originalBeverage.id)) {
    return `${challengeLabel}原订单酒水 ${originalBeverage.name} 已被排除，不能生成受控推进方案。`;
  }

  return `${challengeLabel}原订单 ${originalRecipe.name} / ${originalBeverage.name} 没有通过料理、酒水与厨具硬门禁的受控推进方案；当前目标 Tag：${targetTags.join('、')}。`;
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
