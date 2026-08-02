import {
  buildOrderRecommendations,
  buildRareCustomerMap,
  createRecommendationCacheStore,
} from '@/companion/domain/service-recommendations';
import { buildNormalOrderDetailPlans } from '@/companion/domain/normal-order-details';
import { buildNormalAutoOrderKey } from '@/companion/domain/normal-order-key';
import { sortNormalOrders } from '@/companion/domain/sorting';
import { selectSpecialBusinessNormalExecutionTarget } from '@/companion/domain/special-business/registry';
import type {
  NormalExecutionTargetSelection,
  OrderRecommendationResult,
  OrderRecommendationWorkerPayload,
  OrderRecommendationWorkerRequest,
  OrderRecommendationWorkerResponse,
} from '@/companion/workers/order-recommendations.types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';

type WorkerScope = {
  postMessage: (message: OrderRecommendationWorkerResponse) => void;
  onmessage: ((event: MessageEvent<OrderRecommendationWorkerRequest>) => void) | null;
};

const workerScope = self as unknown as WorkerScope;
const recommendationCaches = createRecommendationCacheStore();
let cachedData: RecommendationDataSet | null = null;
let cachedDataSignature = '';

workerScope.onmessage = (event) => {
  const { requestId, payload: runtimePayload } = event.data;

  try {
    const data = resolveRecommendationData(runtimePayload);
    const payload: OrderRecommendationWorkerPayload = {
      ...runtimePayload,
      data,
    };
    const startedAt = now();
    const rareCustomersById = buildRareCustomerMap(payload.data);
    const recommendationStartedAt = now();
    const recommendationResult = buildOrderRecommendations(
      payload.orders,
      payload.runtime,
      rareCustomersById,
      recommendationCaches,
      payload.favorites,
      payload.customRecipes,
      payload.preferences,
      payload.activeRareGuests,
      payload.specialBusiness ?? null,
      payload.specialBusinessRejectedRecipeKeys ?? [],
      payload.data,
      { usage: payload.usage ?? 'display' },
    );
    const recommendationFinishedAt = now();

    const detailStartedAt = now();
    const normalOrderDetailPlans = payload.includeNormalOrderDetails === true
      ? buildNormalOrderDetailPlans({
        orders: sortNormalOrders(payload.normalOrders ?? []),
        specialBusiness: payload.specialBusiness ?? null,
        runtime: payload.runtime,
        preferences: payload.preferences,
        dataSignature: runtimePayload.dataSignature,
        data: payload.data,
        rejectedRecipeKeys: payload.specialBusinessRejectedRecipeKeys ?? [],
      })
      : [];
    const detailFinishedAt = now();

    const executionTargetStartedAt = now();
    const normalExecutionTargets = payload.includeNormalExecutionTargets === true
      ? buildNormalExecutionTargets(payload, runtimePayload.dataSignature)
      : [];
    const finishedAt = now();

    const result: OrderRecommendationResult = {
      ...recommendationResult,
      normalOrderDetailPlans,
      normalExecutionTargets,
      performanceMs: {
        recommendations: recommendationFinishedAt - recommendationStartedAt,
        normalDetails: detailFinishedAt - detailStartedAt,
        normalExecutionTargets: finishedAt - executionTargetStartedAt,
        total: finishedAt - startedAt,
      },
    };

    workerScope.postMessage({
      requestId,
      ok: true,
      result,
      signature: buildResultSignature(result),
    });
  } catch (error) {
    workerScope.postMessage({
      requestId,
      ok: false,
      error: error instanceof Error ? error.message : String(error),
    });
  }
};

function resolveRecommendationData(
  payload: OrderRecommendationWorkerRequest['payload'],
): RecommendationDataSet {
  if (payload.data) {
    if (cachedData && cachedDataSignature !== payload.dataSignature) {
      recommendationCaches.orders.clear();
      recommendationCaches.foodCandidates.clear();
      recommendationCaches.beverageCandidates.clear();
    }
    cachedData = payload.data;
    cachedDataSignature = payload.dataSignature;
    return payload.data;
  }

  if (cachedData && cachedDataSignature === payload.dataSignature) {
    return cachedData;
  }

  throw new Error('推荐数据集尚未初始化，等待下一轮快照。');
}

function now(): number {
  return typeof performance !== 'undefined' ? performance.now() : Date.now();
}

function buildNormalExecutionTargets(
  payload: OrderRecommendationWorkerPayload,
  dataSignature: string,
): NormalExecutionTargetSelection[] {
  return sortNormalOrders(payload.normalOrders ?? [])
    .filter((order) => !order.hasEvaluated)
    .map((order) => {
      const selection = selectSpecialBusinessNormalExecutionTarget({
        order,
        specialBusiness: payload.specialBusiness ?? null,
        runtime: payload.runtime,
        preferences: payload.preferences,
        dataSignature,
        data: payload.data,
        rejectedRecipeKeys: payload.specialBusinessRejectedRecipeKeys ?? [],
      });
      return {
        orderKey: buildNormalAutoOrderKey(order),
        target: selection.target,
        message: selection.message,
      };
    });
}

function buildResultSignature(result: OrderRecommendationResult): string {
  return [
    result.recommendations.map((item) => [
      item.order.traceId ?? '',
      item.order.deskCode,
      item.order.guestId ?? '',
      item.order.runtimeGuestId ?? '',
      item.order.guestName,
      item.order.foodTagId,
      item.order.foodTag,
      item.order.beverageTagId,
      item.order.beverageTag,
      item.order.specialBusinessRole ?? '',
      item.order.firstSeenAtUtc ?? '',
      item.order.isFreeOrder ? 1 : 0,
      item.order.hasServedFood ? 1 : 0,
      item.order.hasServedBeverage ? 1 : 0,
      item.blockedMessages.join('~'),
      item.blockedDiagnostic?.stateSignature ?? '',
      item.executionPlans.map((plan) => [
        plan.bucket,
        plan.food?.recipe.id ?? '',
        plan.food?.recipe.recipeId ?? '',
        plan.food?.extraIngredients.map((ingredient) => ingredient.id).join(',') ?? '',
        plan.beverage?.beverage.id ?? '',
        plan.estimatedPrice,
        plan.reasons.join(','),
      ].join(':')).join(';'),
      item.recipes.map((recipe) => [
        recipe.recipe.id,
        recipe.recipe.recipeId,
        recipe.extraIngredients.map((ingredient) => ingredient.id).join(','),
        recipe.meetsRequiredFood ? 1 : 0,
        recipe.missionTarget ? 1 : 0,
        recipe.allTags.join(','),
      ].join(':')).join(';'),
      item.beverages.map((beverage) => [
        beverage.beverage.id,
        beverage.meetsRequiredBev ? 1 : 0,
        beverage.matchedTags.join(','),
      ].join(':')).join(';'),
    ].join('|')).join('\n'),
    result.recommendationIssues.map((issue) => [
      issue.order.traceId ?? '',
      issue.order.deskCode,
      issue.order.guestId ?? '',
      issue.order.runtimeGuestId ?? '',
      issue.order.foodTagId,
      issue.order.beverageTagId,
      issue.message,
    ].join('|')).join('\n'),
    result.normalOrderDetailPlans.map((plan) => JSON.stringify(plan)).join('\n'),
    result.normalExecutionTargets.map((item) => [
      item.orderKey,
      item.message,
      item.target?.foodId ?? '',
      item.target?.recipeId ?? '',
      item.target?.executionMode ?? '',
      item.target?.allowYuumaControlledProgression ? 1 : 0,
      item.target?.recipeName ?? '',
      item.target?.extraIngredientIds.join(',') ?? '',
      item.target?.beverageId ?? '',
      item.target?.beverageName ?? '',
      item.target?.reason ?? '',
      item.target?.foodTags.join(',') ?? '',
      item.target?.expectedFoodModifierTags.join(',') ?? '',
      item.target?.beverageTags.join(',') ?? '',
      item.target?.specialTargetChallenge ?? '',
      item.target?.specialTargetOwner ?? '',
      item.target?.specialTargetGeneration ?? '',
      item.target?.specialTargetRevision ?? '',
      item.target?.specialTargetFoodTags.join(',') ?? '',
      item.target?.specialTargetMatchMode ?? '',
      item.target?.specialTargetSignature ?? '',
    ].join('|')).join('\n'),
  ].join('\n---\n');
}

export {};
