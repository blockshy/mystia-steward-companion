import { resolveCookerTypeId } from '@/companion/domain/cookers';
import { normalizeIdList } from '@/companion/domain/favorites';
import { buildNormalAutoOrderKey } from '@/companion/domain/normal-order-key';
import {
  getPrimaryExecutionPlan,
  isVerifiedMissionPrimaryExecutionPlan,
} from '@/companion/domain/primary-execution-plan';
import { sortNightOrderRows, sortNormalOrders } from '@/companion/domain/sorting';
import {
  buildSpecialFoodTargetWirePolicy,
  requiresSpecialBusinessNormalExecutionTarget,
} from '@/companion/domain/special-business';
import type { ServiceOrderSortMode } from '@/companion/preferences';
import type {
  GameUiTarget,
  GameUiTargetFeatures,
  GameUiTargetKind,
  NightBusinessOrder,
  NormalBusinessOrder,
  NormalOrderExecutionTarget,
  OrderRecommendation,
  SpecialBusinessContext,
} from '@/companion/types';
import type { NormalExecutionTargetSelection } from '@/companion/workers/order-recommendations.types';
import {
  buildRecommendationDataIndexes,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
import type { RecipeCatalogItem } from '@/lib/catalog-types';

const RARE_TRACE_PATTERN = /^R-[0-9]{1,16}$/;
const NORMAL_TRACE_PATTERN = /^N-[0-9]{1,16}$/;
const NORMAL_ORDER_KEY_PATTERN = /^ptr:[0-9a-f]{1,16}$/;

export interface GameUiTargetSourceOrderState {
  kind: GameUiTargetKind;
  sourceOrderKey: string;
  sourceOrderSignature: string;
  hasServedFood: boolean;
  hasServedBeverage: boolean;
  terminal: boolean;
}

export function buildRareGameUiTarget(
  recommendations: readonly OrderRecommendation[],
  orderSortMode: ServiceOrderSortMode,
  color: string,
  features: GameUiTargetFeatures,
  indexes: ReturnType<typeof buildRecommendationDataIndexes>,
  options: {
    prioritizeMissionRecipe?: boolean;
    specialBusiness?: SpecialBusinessContext | null;
  } = {},
): GameUiTarget | null {
  const candidates = sortNightOrderRows(
    recommendations.map((recommendation) => ({ order: recommendation.order, recommendation })),
    orderSortMode,
    options.specialBusiness,
  ).flatMap(({ recommendation }) => {
    const order = recommendation.order;
    if (!hasStrongRareIdentity(order)) return [];
    const plan = getPrimaryExecutionPlan(recommendation.executionPlans);
    if (!plan) return [];

    const food = order.hasServedFood === true ? null : plan.food;
    const beverage = order.hasServedBeverage === true ? null : plan.beverage;
    if (!food && !beverage) return [];
    return [{ recommendation, food, beverage }];
  });
  const selected = options.prioritizeMissionRecipe
    ? candidates.find(({ recommendation, food }) =>
      food != null && isVerifiedMissionPrimaryExecutionPlan(recommendation)
    ) ?? candidates[0]
    : candidates[0];
  if (!selected) return null;

  const { order } = selected.recommendation;
  const baseIngredientIds = selected.food
    ? resolveIngredientNames(selected.food.recipe.ingredients, indexes)
    : [];
  if (baseIngredientIds == null) return null;
  const extraIngredientIds = selected.food
    ? selected.food.extraIngredients.map((ingredient) => ingredient.id)
    : [];
  if (!areValidIds(extraIngredientIds)) return null;

  return buildTarget({
    kind: 'rare',
    color,
    features,
    sourceOrderKey: buildRareSourceOrderKey(order),
    sourceOrderSignature: buildRareSourceOrderSignature(order),
    traceId: order.traceId!,
    orderKey: '',
    orderLifecycleSequence: order.orderLifecycleSequence,
    deskCode: order.deskCode,
    recipeId: selected.food?.recipe.recipeId ?? -1,
    recipeName: selected.food?.recipe.name ?? '',
    ingredientIds: normalizeIdList([...baseIngredientIds, ...extraIngredientIds]),
    extraIngredientIds,
    beverageId: selected.beverage?.beverage.id ?? -1,
    beverageName: selected.beverage?.beverage.name ?? '',
    cookerTypeId: resolveCookerTypeId(selected.food?.recipe.cooker),
    cookerName: selected.food?.recipe.cooker ?? '',
  });
}

export function buildNormalGameUiTarget({
  orders,
  executionTargets,
  executionTargetsCurrent,
  specialBusiness,
  businessGeneration,
  color,
  features,
  data,
}: {
  orders: readonly NormalBusinessOrder[];
  executionTargets: readonly NormalExecutionTargetSelection[];
  executionTargetsCurrent: boolean;
  specialBusiness: SpecialBusinessContext | null | undefined;
  businessGeneration: number;
  color: string;
  features: GameUiTargetFeatures;
  data: RecommendationDataSet;
}): GameUiTarget | null {
  const indexes = buildRecommendationDataIndexes(data);
  const selections = executionTargetsCurrent
    ? new Map(executionTargets.map((selection) => [selection.orderKey, selection]))
    : new Map<string, NormalExecutionTargetSelection>();

  for (const order of sortNormalOrders([...orders])) {
    if (!hasStrongNormalIdentity(order)
      || order.hasEvaluated
      || (order.hasServedFood && order.hasServedBeverage)) continue;

    const requiresFinalTarget = requiresSpecialBusinessNormalExecutionTarget(
      specialBusiness,
      order.specialBusinessRole,
    );
    const selection = selections.get(buildNormalAutoOrderKey(order));
    const executionTarget = requiresFinalTarget
      && selection?.target
      && isCurrentNormalExecutionTarget(
        order,
        selection.target,
        specialBusiness,
        businessGeneration,
      )
      ? selection.target
      : null;
    if (requiresFinalTarget && !executionTarget) continue;

    const recipe = order.hasServedFood
      ? null
      : resolveNormalRecipe(data, indexes.recipeByFoodId, order, executionTarget);
    if (!order.hasServedFood && !recipe) continue;
    const extraIngredientIds = order.hasServedFood
      ? []
      : executionTarget?.extraIngredientIds ?? [];
    if (!areValidIds(extraIngredientIds)) continue;
    const baseIngredientIds = recipe
      ? resolveIngredientNames(recipe.ingredients, indexes)
      : [];
    if (baseIngredientIds == null) continue;

    const beverageId = order.hasServedBeverage
      ? -1
      : executionTarget?.beverageId ?? order.beverageId;
    if (!order.hasServedBeverage && beverageId < 0) continue;
    const sourceOrderKey = buildNormalAutoOrderKey(order);
    return buildTarget({
      kind: 'normal',
      color,
      features,
      sourceOrderKey,
      sourceOrderSignature: buildNormalSourceOrderSignature(order),
      traceId: order.traceId!,
      orderKey: order.orderKey!,
      orderLifecycleSequence: order.orderLifecycleSequence,
      deskCode: order.deskCode,
      recipeId: recipe?.recipeId ?? -1,
      recipeName: executionTarget?.recipeName || recipe?.name || order.foodName,
      ingredientIds: normalizeIdList([...baseIngredientIds, ...extraIngredientIds]),
      extraIngredientIds: [...extraIngredientIds],
      beverageId,
      beverageName: beverageId < 0
        ? ''
        : executionTarget?.beverageName
          || indexes.beverageNameById.get(beverageId)
          || order.beverageName,
      cookerTypeId: resolveCookerTypeId(recipe?.cooker),
      cookerName: recipe?.cooker ?? '',
    });
  }
  return null;
}

export function buildRareGameUiTargetSource(order: NightBusinessOrder): GameUiTargetSourceOrderState {
  return {
    kind: 'rare',
    sourceOrderKey: buildRareSourceOrderKey(order),
    sourceOrderSignature: buildRareSourceOrderSignature(order),
    hasServedFood: order.hasServedFood === true,
    hasServedBeverage: order.hasServedBeverage === true,
    terminal: false,
  };
}

export function buildNormalGameUiTargetSource(order: NormalBusinessOrder): GameUiTargetSourceOrderState {
  return {
    kind: 'normal',
    sourceOrderKey: buildNormalAutoOrderKey(order),
    sourceOrderSignature: buildNormalSourceOrderSignature(order),
    hasServedFood: order.hasServedFood,
    hasServedBeverage: order.hasServedBeverage,
    terminal: order.hasEvaluated,
  };
}

export function reconcileGameUiTarget(
  target: GameUiTarget | null,
  sources: readonly GameUiTargetSourceOrderState[],
): GameUiTarget | null {
  if (!target) return null;
  const matches = sources.filter((source) =>
    source.kind === target.kind
    && source.sourceOrderKey === target.sourceOrderKey
    && source.sourceOrderSignature === target.sourceOrderSignature
  );
  if (matches.length !== 1 || matches[0].terminal) return null;

  const source = matches[0];
  const next = {
    ...target,
    recipeId: source.hasServedFood ? -1 : target.recipeId,
    recipeName: source.hasServedFood ? '' : target.recipeName,
    ingredientIds: source.hasServedFood ? [] : target.ingredientIds,
    extraIngredientIds: source.hasServedFood ? [] : target.extraIngredientIds,
    cookerTypeId: source.hasServedFood ? -1 : target.cookerTypeId,
    cookerName: source.hasServedFood ? '' : target.cookerName,
    beverageId: source.hasServedBeverage ? -1 : target.beverageId,
    beverageName: source.hasServedBeverage ? '' : target.beverageName,
  };
  if (next.recipeId < 0 && next.beverageId < 0) return null;
  const targetRevision = buildTargetRevision(next);
  return targetRevision === target.targetRevision ? target : { ...next, targetRevision };
}

function buildTarget(input: Omit<GameUiTarget, 'targetRevision'>): GameUiTarget {
  const target = { ...input, features: { ...input.features }, targetRevision: '' };
  return { ...target, targetRevision: buildTargetRevision(target) };
}

function buildTargetRevision(target: Omit<GameUiTarget, 'targetRevision'> | GameUiTarget): string {
  return [
    target.kind,
    target.sourceOrderKey,
    target.sourceOrderSignature,
    target.traceId,
    target.orderKey,
    target.orderLifecycleSequence,
    target.recipeId,
    target.ingredientIds.join(','),
    target.extraIngredientIds.join(','),
    target.beverageId,
    target.cookerTypeId,
    target.deskCode,
  ].join('|');
}

function resolveNormalRecipe(
  data: RecommendationDataSet,
  recipeByFoodId: Map<number, RecipeCatalogItem>,
  order: NormalBusinessOrder,
  target: NormalOrderExecutionTarget | null,
): RecipeCatalogItem | null {
  if (!target) return recipeByFoodId.get(order.foodId) ?? null;
  const matches = data.recipes.filter((recipe) =>
    recipe.id === target.foodId && recipe.recipeId === target.recipeId
  );
  return matches.length === 1 ? matches[0] : null;
}

function isCurrentNormalExecutionTarget(
  order: NormalBusinessOrder,
  target: NormalOrderExecutionTarget,
  specialBusiness: SpecialBusinessContext | null | undefined,
  businessGeneration: number,
): boolean {
  if (target.matchFoodId !== order.foodId || target.matchBeverageId !== order.beverageId) return false;
  const policy = buildSpecialFoodTargetWirePolicy(
    specialBusiness,
    order.specialBusinessRole,
    businessGeneration,
  );
  return target.specialTargetChallenge === policy.specialTargetChallenge
    && target.specialTargetOwner === policy.specialTargetOwner
    && target.specialTargetGeneration === policy.specialTargetGeneration
    && target.specialTargetRevision === policy.specialTargetRevision
    && target.specialTargetMatchMode === policy.specialTargetMatchMode
    && target.specialTargetSignature === policy.specialTargetSignature
    && target.specialTargetFoodTags.length === policy.specialTargetFoodTags.length
    && target.specialTargetFoodTags.every((tag, index) => tag === policy.specialTargetFoodTags[index]);
}

function resolveIngredientNames(
  names: readonly string[],
  indexes: ReturnType<typeof buildRecommendationDataIndexes>,
): number[] | null {
  const ids = names.map((name) => indexes.ingredientByName.get(name)?.id ?? -1);
  return ids.every((id) => id >= 0) ? ids : null;
}

function hasStrongRareIdentity(order: NightBusinessOrder): boolean {
  return order.orderLifecycleSequence > 0
    && order.deskCode >= 0
    && typeof order.traceId === 'string'
    && RARE_TRACE_PATTERN.test(order.traceId);
}

function hasStrongNormalIdentity(order: NormalBusinessOrder): boolean {
  return order.orderLifecycleSequence > 0
    && order.deskCode >= 0
    && typeof order.traceId === 'string'
    && NORMAL_TRACE_PATTERN.test(order.traceId)
    && typeof order.orderKey === 'string'
    && NORMAL_ORDER_KEY_PATTERN.test(order.orderKey)
    && /[1-9a-f]/.test(order.orderKey.slice(4));
}

function buildRareSourceOrderKey(order: NightBusinessOrder): string {
  return `${order.traceId}|lifecycle:${order.orderLifecycleSequence}`;
}

function buildRareSourceOrderSignature(order: NightBusinessOrder): string {
  return [
    order.traceId ?? '',
    order.orderLifecycleSequence,
    order.firstSeenAtUtc ?? '',
    order.deskCode,
    order.guestId ?? '',
    order.runtimeGuestId ?? '',
    order.specialBusinessRole ?? '',
    order.foodTagId ?? '',
    order.beverageTagId ?? '',
    order.isFreeOrder ? 1 : 0,
  ].join('|');
}

function buildNormalSourceOrderSignature(order: NormalBusinessOrder): string {
  return [
    order.traceId ?? '',
    order.orderKey ?? '',
    order.orderLifecycleSequence,
    order.firstSeenAtUtc ?? '',
    order.deskCode,
    order.guestId ?? '',
    order.runtimeGuestId ?? '',
    order.specialBusinessRole ?? '',
    order.foodId,
    order.beverageId,
  ].join('|');
}

function areValidIds(ids: readonly number[]): boolean {
  return ids.every((id) => Number.isInteger(id) && id >= 0);
}
