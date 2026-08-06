import {
  emptyNormalAutoOrderState,
  getAutomationStepLabel,
  getCurrentNormalOrderExecutionTarget,
  type AutomationStep,
  type AutoFirstOrderState,
  type NormalAutoOrderState,
  type OrderPreparationResponse,
  type RareAutomationBeverageTarget,
  type RareAutomationRecipeTarget,
} from '@/companion/automation-state';
import {
  reconcileAutomationRollbackTarget,
  resolveAutomationStepSeconds,
  resolveAutomationStepStartedAtMs,
} from '@/companion/automation-machine';
import {
  buildAutomationCookerCapacity,
  buildAutomationCookerPool,
  findAvailableAutomationCookerSlot,
  getCookerSlotCapacity,
  getNormalCookerRequirement,
  getRareCookerRequirement,
} from '@/companion/domain/cookers';
import {
  findBeverageFavorite,
  findRecipeFavorite,
  normalizeIdList,
} from '@/companion/domain/favorites';
import {
  buildNormalAutoOrderKey,
  buildNormalLifecycleAutoOrderKey,
} from '@/companion/domain/normal-order-key';
import {
  getPrimaryExecutionPlan,
} from '@/companion/domain/primary-execution-plan';
import { toRareRecipeResult } from '@/companion/domain/service-recommendations';
import {
  sortNightOrderRows,
  sortNormalOrders,
} from '@/companion/domain/sorting';
import {
  applySpecialFoodTargetWirePolicy,
  buildSpecialFoodTargetWirePolicy,
  buildSpecialBusinessOrderRule,
  buildWackyRejectedRecipeKeyForRareRecipe,
  getWackyTargetTagCountdownDeferral,
  getNormalExecutionCookerRequirement,
  matchesSpecialBusinessFoodTarget,
  requiresSpecialBusinessNormalExecutionTarget,
  selectSpecialBusinessNormalExecutionTarget,
  WACKY_CHALLENGE_TYPE,
  emptySpecialFoodTargetWirePolicy,
} from '@/companion/domain/special-business';
import { formatDesk } from '@/companion/formatters';
import type { CompanionPreferences } from '@/companion/preferences';
import type {
  AutomationCookerCycle,
  AutomationCookerPool,
  AutomationCookerResourceRow,
  AutomationCookerSlot,
  AutomationResourceOverview,
  CookerRequirement,
  CookerReservationResult,
  FavoriteBeverageEntry,
  FavoriteData,
  FavoriteRecipeEntry,
  NightBusinessOrder,
  NormalOrderExecutionTarget,
  NormalAutoOrderDiagnostic,
  NormalBusinessOrder,
  NormalCookerDemand,
  OrderRecommendation,
  RareAutoOrderDiagnostic,
  RecommendationStateSnapshot,
  SpecialBusinessContext,
  SpecialFoodTargetWirePolicy,
} from '@/companion/types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { RareBeverageRecommendation, RareOrderRecommendationPlan, RareRecipeRecommendation } from '@/recommendation-engine';

type OrderPreparationSelection =
  | {
      ok: true;
      item: OrderRecommendation;
      recipe: RareRecipeRecommendation | null;
      beverage: RareBeverageRecommendation | null;
      recipeTarget: RareAutomationRecipeTarget | null;
      beverageTarget: RareAutomationBeverageTarget | null;
      recipeFavorite: FavoriteRecipeEntry | null;
      beverageFavorite: FavoriteBeverageEntry | null;
    }
  | {
      ok: false;
      reason: OrderPreparationSkipReason;
      message: string;
    };

export type ValidOrderPreparationSelection = Extract<OrderPreparationSelection, { ok: true }>;

export type OrderPreparationSkipReason =
  | 'automation-blocked'
  | 'runtime-identity-missing'
  | 'recipe-favorite-missing'
  | 'recipe-target-missing'
  | 'beverage-favorite-missing'
  | 'beverage-target-missing';

export interface OrderPreparationCandidateSkip {
  reason: OrderPreparationSkipReason;
  orderKey: string;
  label: string;
  message: string;
  recipeRecommendationCount: number;
  beverageRecommendationCount: number;
  executionPlanCount: number;
}

export interface OrderPreparationCandidateResult {
  selections: ValidOrderPreparationSelection[];
  skips: OrderPreparationCandidateSkip[];
  messages: string[];
  message: string;
}

export interface NormalExecutionTargetSelectionLike {
  orderKey: string;
  target: NormalOrderExecutionTarget | null;
  message: string;
}

export interface NormalCookingTargetDecision {
  orderKey: string;
  target: NormalOrderExecutionTarget | null;
  blockedReason: string;
  cooker: CookerRequirement | null;
  label: string;
}

export function getWackyRecipeCookingDeferral(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
  recipeName: string,
  recipeTags: readonly string[],
): string {
  if (specialBusiness?.challengeType !== WACKY_CHALLENGE_TYPE) return '';
  const rule = buildSpecialBusinessOrderRule(specialBusiness, role);
  const target = rule.foodTarget;
  if (target.enforcement !== 'require' || target.tags.length === 0) return '';

  if (!matchesSpecialBusinessFoodTarget(recipeTags, target)) {
    return `当前怪诞料理目标 Tag 为 ${target.tags.join('、')}，${recipeName || '目标料理'} 不含该 Tag，等待目标刷新后再开锅。`;
  }

  return getWackyTargetTagCountdownDeferral(specialBusiness);
}

export function getWackyRareCookingDeferral(
  specialBusiness: SpecialBusinessContext | null | undefined,
  item: OrderRecommendation,
  target: RareAutomationRecipeTarget | null,
  fallbackRecipe: RareRecipeRecommendation | null,
): string {
  const recipe = target ? findRecipeRowForTarget(item, target) : null;
  const recipeName = target?.recipeName ?? recipe?.recipe.name ?? fallbackRecipe?.recipe.name ?? '';
  const recipeTags = target?.foodTags.length ? target.foodTags : recipe?.allTags ?? fallbackRecipe?.allTags ?? [];
  return getWackyRecipeCookingDeferral(specialBusiness, item.order.specialBusinessRole, recipeName, recipeTags);
}

export interface RareRecipeTargetReconciliation {
  state: AutoFirstOrderState;
  message: string;
  specialTargetPolicy: SpecialFoodTargetWirePolicy;
  policyError: string;
  rollbackTargetRotated?: boolean;
}

export function reconcileRareRecipeTargetForSpecialBusiness(
  specialBusiness: SpecialBusinessContext | null | undefined,
  businessGeneration: number,
  item: OrderRecommendation,
  state: AutoFirstOrderState,
  recommendedTarget: RareAutomationRecipeTarget | null,
  requiresRecipeTarget: boolean,
  now: number,
  rejectedRecipeKeys: readonly string[] = [],
): RareRecipeTargetReconciliation {
  const rule = buildSpecialBusinessOrderRule(specialBusiness, item.order.specialBusinessRole);
  const target = rule.foodTarget;
  const policy = buildSpecialFoodTargetWirePolicy(
    specialBusiness,
    item.order.specialBusinessRole,
    businessGeneration,
  );
  const signature = policy.specialTargetSignature;
  const revision = policy.specialTargetRevision;
  if (state.manualResolutionRequired) {
    const rollbackReconciliation = reconcileAutomationRollbackTarget(
      state,
      signature,
      revision,
      now,
    );
    return {
      state: rollbackReconciliation.state,
      message: '',
      specialTargetPolicy: policy,
      policyError: '',
      rollbackTargetRotated: rollbackReconciliation.rotated,
    };
  }
  if (target.enforcement !== 'require' || target.tags.length === 0) {
    if (!state.recipeTargetSignature
      && state.recipeTargetRevision === 0
      && !state.recipeTarget?.specialTargetSignature
      && (state.recipeTarget?.specialTargetRevision ?? 0) === 0) {
      return { state, message: '', specialTargetPolicy: policy, policyError: '' };
    }
    const recipeTarget = state.recipeTarget
      ? applySpecialFoodTargetWirePolicy(state.recipeTarget, emptySpecialFoodTargetWirePolicy())
      : null;
    return {
      state: {
        ...state,
        recipeTarget,
        recipeTargetSignature: '',
        recipeTargetRevision: 0,
      },
      message: '',
      specialTargetPolicy: policy,
      policyError: '',
    };
  }
  const targetTags = target.tags;
  const challengeLabel = specialBusiness?.displayName.trim()
    || specialBusiness?.challengeType
    || '特殊经营';
  const targetRequirement = target.match === 'all' ? '同时满足' : '命中';
  if (!signature) {
    const message = `${challengeLabel}的特殊料理目标缺少有效经营代际或目标身份，自动化已暂停该料理目标。`;
    return {
      state: {
        ...state,
        recipeTarget: null,
        recipeTargetSignature: '',
        recipeTargetRevision: 0,
        prepared: false,
        cookingJobId: '',
        step: 'ensure-cooking',
        stepStartedAtMs: now,
        lastProgressAtMs: now,
        lastError: message,
      },
      message,
      specialTargetPolicy: policy,
      policyError: message,
    };
  }
  if (!requiresRecipeTarget) {
    return {
      state: state.recipeTarget || state.recipeTargetSignature || state.recipeTargetRevision !== 0
        ? {
            ...state,
            recipeTarget: null,
            recipeTargetSignature: '',
            recipeTargetRevision: 0,
          }
        : state,
      message: '',
      specialTargetPolicy: policy,
      policyError: '',
    };
  }

  const rejected = new Set(rejectedRecipeKeys);
  const currentTarget = state.recipeTarget;
  const currentMatches = currentTarget ? rareRecipeTargetMatchesSpecialTarget(item, currentTarget, target) : false;
  const recommendedMatches = recommendedTarget ? rareRecipeTargetMatchesSpecialTarget(item, recommendedTarget, target) : false;
  const appliesWackyRejection = specialBusiness?.challengeType === WACKY_CHALLENGE_TYPE;
  const currentRejected = appliesWackyRejection && currentTarget
    ? isRareRecipeTargetRejectedBySpecialBusiness(currentTarget, targetTags, rejected)
    : false;
  const recommendedRejected = appliesWackyRejection && recommendedTarget
    ? isRareRecipeTargetRejectedBySpecialBusiness(recommendedTarget, targetTags, rejected)
    : false;
  const signatureChanged = state.recipeTargetSignature !== signature
    || state.recipeTargetRevision !== revision;
  const targetReconciliation = reconcileAutomationRollbackTarget(
    state,
    signature,
    revision,
    now,
  );
  const targetState = targetReconciliation.state;
  const specialTargetRotated = targetReconciliation.rotated;
  const canKeepCurrentTarget = currentTarget && currentMatches && !currentRejected;

  if (canKeepCurrentTarget && !signatureChanged) {
    return currentTarget.specialTargetSignature === signature
      && currentTarget.specialTargetRevision === revision
      ? {
          state: targetState,
          message: '',
          specialTargetPolicy: policy,
          policyError: '',
          rollbackTargetRotated: specialTargetRotated,
        }
      : {
          state: {
            ...targetState,
            recipeTarget: applySpecialFoodTargetWirePolicy(currentTarget, policy),
          },
          message: '',
          specialTargetPolicy: policy,
          policyError: '',
          rollbackTargetRotated: specialTargetRotated,
        };
  }

  if (canKeepCurrentTarget && signatureChanged) {
    return {
      state: {
        ...targetState,
        recipeTarget: applySpecialFoodTargetWirePolicy(currentTarget, policy),
        recipeTargetSignature: signature,
        recipeTargetRevision: revision,
      },
      message: '',
      specialTargetPolicy: policy,
      policyError: '',
      rollbackTargetRotated: specialTargetRotated,
    };
  }

  if (recommendedTarget && recommendedMatches && !recommendedRejected) {
    const changedTarget = !currentTarget || !isSameRareRecipeTarget(currentTarget, recommendedTarget);
    const reason = currentRejected
      ? '旧料理目标已被实机判定不匹配'
      : '旧料理目标不可用';
    return {
      state: {
        ...targetState,
        recipeTarget: applySpecialFoodTargetWirePolicy(recommendedTarget, policy),
        recipeTargetSignature: signature,
        recipeTargetRevision: revision,
        prepared: false,
        cookingJobId: '',
        step: 'ensure-cooking',
        stepStartedAtMs: now,
        lastProgressAtMs: now,
        retryCount: 0,
        retryStage: '',
        rollbackCount: specialTargetRotated
          ? 0
          : changedTarget
            ? targetState.rollbackCount + 1
            : targetState.rollbackCount,
        lastError: changedTarget
          ? `${challengeLabel}目标 Tag 为 ${targetTags.join('、')}，已切换到${targetRequirement}目标的推荐料理 ${recommendedTarget.recipeName}。`
          : '',
      },
      message: changedTarget
        ? `${challengeLabel}目标 Tag 为 ${targetTags.join('、')}，${reason}，已切换到 ${recommendedTarget.recipeName}。`
        : '',
      specialTargetPolicy: policy,
      policyError: '',
      rollbackTargetRotated: specialTargetRotated,
    };
  }

  if (!currentTarget
    && state.recipeTargetSignature === signature
    && state.recipeTargetRevision === revision) {
    return {
      state: targetState,
      message: '',
      specialTargetPolicy: policy,
      policyError: '',
      rollbackTargetRotated: specialTargetRotated,
    };
  }

  return {
    state: {
      ...targetState,
      recipeTarget: null,
      recipeTargetSignature: signature,
      recipeTargetRevision: revision,
      prepared: false,
      cookingJobId: '',
      step: 'ensure-cooking',
      stepStartedAtMs: now,
      lastProgressAtMs: now,
      retryCount: 0,
      retryStage: '',
      rollbackCount: specialTargetRotated
        ? 0
        : currentTarget
          ? targetState.rollbackCount + 1
          : targetState.rollbackCount,
      lastError: `${challengeLabel}目标 Tag 为 ${targetTags.join('、')}，当前没有可执行且${targetRequirement}目标的推荐料理。`,
    },
    message: `${challengeLabel}目标 Tag 为 ${targetTags.join('、')}，当前没有可执行且${targetRequirement}目标的推荐料理。`,
    specialTargetPolicy: policy,
    policyError: '',
    rollbackTargetRotated: specialTargetRotated,
  };
}

export function buildNormalCookingTargetDecision(
  order: NormalBusinessOrder,
  data: RecommendationDataSet,
  selection: NormalExecutionTargetSelectionLike | null | undefined,
): NormalCookingTargetDecision {
  const target = selection?.target ?? null;
  const blockedReason = selection?.message ?? '';
  return {
    orderKey: buildNormalAutoOrderKey(order),
    target,
    blockedReason,
    cooker: blockedReason
      ? null
      : getNormalExecutionCookerRequirement(target) ?? getNormalCookerRequirement(order, data),
    label: `普客 桌 ${formatDesk(order.deskCode)} · ${(target?.recipeName || order.foodName) || `#${order.foodId}`}`,
  };
}

function resolveNormalExecutionTargetSelectionForOverview({
  orderKey,
  requiresSpecialNormalTarget,
  normalExecutionTargetByKey,
  normalExecutionTargetsPending,
  normalExecutionTargetsError,
}: {
  orderKey: string;
  requiresSpecialNormalTarget: boolean;
  normalExecutionTargetByKey: ReadonlyMap<string, NormalExecutionTargetSelectionLike>;
  normalExecutionTargetsPending: boolean;
  normalExecutionTargetsError: string | null;
}): NormalExecutionTargetSelectionLike {
  const selection = normalExecutionTargetByKey.get(orderKey);
  if (selection) return selection;
  if (!requiresSpecialNormalTarget) {
    return { orderKey, target: null, message: '' };
  }
  if (normalExecutionTargetsError) {
    return { orderKey, target: null, message: `特殊经营执行目标计算失败：${normalExecutionTargetsError}` };
  }
  if (normalExecutionTargetsPending) {
    return { orderKey, target: null, message: '特殊经营执行目标计算中，等待下一轮。' };
  }
  return { orderKey, target: null, message: '特殊经营执行目标暂不可用，等待下一轮。' };
}

/**
 * 估算普客自动化本轮会占用的厨具需求。
 *
 * 稀客自动化在预约厨具时会先让出普客已经需要的容量，避免同一轮里两套自动化抢同一个灶台。
 */
export function buildNormalCookerDemand(
  orders: NormalBusinessOrder[],
  states: Map<string, NormalAutoOrderState>,
  preferences: CompanionPreferences,
  runtime: RecommendationStateSnapshot | null | undefined,
  now: number,
  data: RecommendationDataSet,
  dataSignature: string,
  businessGeneration: number,
  specialBusiness: SpecialBusinessContext | null | undefined = null,
  specialBusinessRejectedRecipeKeys: readonly string[] = [],
): NormalCookerDemand {
  const controllerIndexes = new Set<number>();
  const labelsByControllerIndex = new Map<number, string>();
  if (!preferences.automationEnabled || !preferences.autoNormalOrderEnabled || !preferences.autoNormalStartCooking) {
    return { controllerIndexes, labelsByControllerIndex };
  }

  const cookerPool = buildAutomationCookerPool(runtime);
  let reservedOrders = 0;
  for (const order of sortNormalOrders(orders).filter((item) => !item.hasEvaluated)) {
    const orderKey = buildNormalAutoOrderKey(order);
    const state = states.get(orderKey);
    if (!shouldAttemptNormalCooking(order, state, preferences, now)) continue;
    const requiresSpecialTarget = requiresSpecialBusinessNormalExecutionTarget(
      specialBusiness,
      order.specialBusinessRole,
    );
    const specialTargetPolicy = buildSpecialFoodTargetWirePolicy(
      specialBusiness,
      order.specialBusinessRole,
      businessGeneration,
    );
    const currentExecutionTarget = getCurrentNormalOrderExecutionTarget(
      state,
      businessGeneration,
      specialTargetPolicy.specialTargetSignature,
      specialTargetPolicy.specialTargetRevision,
    );
    const specialTargetSelection = !requiresSpecialTarget
      ? { orderKey, target: null, message: '' }
      : currentExecutionTarget
        ? {
            orderKey,
            target: applySpecialFoodTargetWirePolicy(currentExecutionTarget, specialTargetPolicy),
            message: '',
          }
        : selectSpecialBusinessNormalExecutionTarget({
            order,
            specialBusiness,
            runtime,
            preferences,
            dataSignature,
            data,
            rejectedRecipeKeys: specialBusinessRejectedRecipeKeys,
          });
    const targetDecision = buildNormalCookingTargetDecision(order, data, {
      orderKey,
      target: specialTargetSelection.target,
      message: specialTargetSelection.message,
    });
    if (targetDecision.blockedReason) continue;

    const cooker = targetDecision.cooker;
    if (!cooker) continue;

    const slot = findAvailableAutomationCookerSlot(cookerPool, cooker.key, controllerIndexes);
    if (!slot) continue;
    controllerIndexes.add(slot.controllerIndex);
    labelsByControllerIndex.set(slot.controllerIndex, targetDecision.label);
    reservedOrders += 1;
    if (reservedOrders >= preferences.autoNormalConcurrency) break;
  }

  return { controllerIndexes, labelsByControllerIndex };
}

/**
 * 构建自动化资源占用概览。
 *
 * UI 通过该结果展示本轮预计占用的厨具槽位，便于解释订单为什么等待。
 */
export function buildAutomationResourceOverview({
  runtime,
  recommendations,
  favorites,
  preferences,
  normalOrders,
  specialBusiness,
  normalExecutionTargets,
  normalExecutionTargetsEnabled = false,
  normalExecutionTargetsPending = false,
  normalExecutionTargetsError = null,
  rareDiagnostics,
  normalDiagnostics,
  data,
}: {
  runtime: RecommendationStateSnapshot | null;
  recommendations: OrderRecommendation[];
  favorites: FavoriteData;
  preferences: CompanionPreferences;
  normalOrders: NormalBusinessOrder[];
  specialBusiness?: SpecialBusinessContext | null;
  normalExecutionTargets?: readonly NormalExecutionTargetSelectionLike[];
  normalExecutionTargetsEnabled?: boolean;
  normalExecutionTargetsPending?: boolean;
  normalExecutionTargetsError?: string | null;
  rareDiagnostics: RareAutoOrderDiagnostic[];
  normalDiagnostics: NormalAutoOrderDiagnostic[];
  data: RecommendationDataSet;
}): AutomationResourceOverview {
  if (!preferences.automationEnabled) {
    return { cookers: [], normalBlocked: [] };
  }

  const cookerPool = buildAutomationCookerPool(runtime);
  const capacity = buildAutomationCookerCapacity(cookerPool);
  const overviewCycle: AutomationCookerCycle = {
    bucket: 0,
    usedControllerIndexes: new Set<number>(),
    labelsByControllerIndex: new Map<number, string>(),
  };
  const cookerRows = new Map<string, AutomationCookerResourceRow>();
  const normalBlocked: AutomationResourceOverview['normalBlocked'] = [];
  for (const [key, count] of capacity.entries()) {
    ensureCookerResourceRow(cookerRows, key, key, count);
  }

  const normalDiagnosticByKey = new Map(normalDiagnostics.map((item) => [item.orderKey, item]));
  const normalExecutionTargetByKey = new Map(
    (normalExecutionTargets ?? []).map((selection) => [selection.orderKey, selection]),
  );
  if (preferences.autoNormalOrderEnabled && preferences.autoNormalStartCooking) {
    let normalReserved = 0;
    for (const order of sortNormalOrders(normalOrders).filter((item) => !item.hasEvaluated)) {
      if (normalReserved >= preferences.autoNormalConcurrency) break;
      const orderKey = buildNormalAutoOrderKey(order);
      const diagnostic = normalDiagnosticByKey.get(orderKey);
      if (diagnostic?.prepared || diagnostic?.foodDeliveryRequested || diagnostic?.paused || diagnostic?.hasServedFood) continue;
      const requiresSpecialNormalTarget = normalExecutionTargetsEnabled
        && requiresSpecialBusinessNormalExecutionTarget(
          specialBusiness,
          order.specialBusinessRole,
        );
      const targetSelection = resolveNormalExecutionTargetSelectionForOverview({
        orderKey,
        requiresSpecialNormalTarget,
        normalExecutionTargetByKey,
        normalExecutionTargetsPending,
        normalExecutionTargetsError,
      });
      const targetDecision = buildNormalCookingTargetDecision(order, data, targetSelection);
      if (targetDecision.blockedReason) {
        normalBlocked.push({
          orderKey,
          label: targetDecision.label,
          reason: targetDecision.blockedReason,
        });
        continue;
      }

      const cooker = targetDecision.cooker;
      if (!cooker) continue;
      const reservation = reserveAutomationCookerSlot(
        overviewCycle,
        cooker,
        targetDecision.label,
        cookerPool,
      );
      if (!reservation.ok) continue;
      const row = ensureCookerResourceRow(cookerRows, cooker.key, cooker.label, getCookerSlotCapacity(cooker.key, capacity));
      row.normalReserved += 1;
      row.labels.push(targetDecision.label);
      normalReserved += 1;
    }
  }

  const rareDiagnosticByKey = new Map(rareDiagnostics.map((item) => [item.orderKey, item]));
  if (preferences.autoRareOrderEnabled && preferences.autoPrepStartCooking) {
    const candidates = selectOrderPreparationCandidates(
      recommendations,
      favorites,
      preferences,
    );
    let rareReserved = 0;
    for (const selection of candidates.selections) {
      if (rareReserved >= preferences.autoRareConcurrency) break;
      const diagnostic = rareDiagnosticByKey.get(buildAutoOrderKey(selection.item));
      if (diagnostic?.prepared || diagnostic?.hasServedFood || diagnostic?.paused) continue;
      const cooker = getRareCookerRequirement(selection.recipeTarget);
      if (!cooker) continue;
      const label = `稀客 ${selection.item.order.guestName || '未知'} · 桌 ${formatDesk(selection.item.order.deskCode)}`;
      const reservation = reserveAutomationCookerSlot(
        overviewCycle,
        cooker,
        label,
        cookerPool,
      );
      if (!reservation.ok) continue;
      const row = ensureCookerResourceRow(cookerRows, cooker.key, cooker.label, getCookerSlotCapacity(cooker.key, capacity));
      row.rareReserved += 1;
      row.labels.push(label);
      rareReserved += 1;
    }
  }

  return {
    cookers: [...cookerRows.values()]
      .filter((row) => row.normalReserved + row.rareReserved > 0)
      .sort((left, right) => left.label.localeCompare(right.label, 'zh-CN')),
    normalBlocked,
  };
}

/**
 * 将普客自动化本地状态与最新 Mod 快照同步。
 *
 * 快照是最终事实来源：如果游戏已经显示送达、可评价或已评价，就推进本地状态并重置重试计数。
 */
export function syncNormalOrderStateWithSnapshot(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  now: number,
  preferences: CompanionPreferences,
): NormalAutoOrderState | undefined {
  const snapshotFoodDelivered = order.hasServedFood;
  const snapshotBeverageDelivered = order.hasServedBeverage;
  const snapshotReadyToEvaluate = order.readyToEvaluate;
  const snapshotCompleted = order.hasEvaluated;
  if (!snapshotFoodDelivered && !snapshotBeverageDelivered && !snapshotReadyToEvaluate && !snapshotCompleted) return state;

  const base = state ?? emptyNormalAutoOrderState(buildNormalAutoOrderKey(order), now);
  const foodDelivered = base.foodDelivered || snapshotFoodDelivered;
  const beverageHandled = base.beverageHandled || snapshotBeverageDelivered;
  const completed = base.completed || snapshotCompleted;
  const prepared = base.prepared || foodDelivered;
  let step = base.step;
  if (completed) {
    step = 'done';
  } else if (snapshotReadyToEvaluate && preferences.autoNormalCompleteOrder) {
    step = 'complete-order';
  } else if (foodDelivered && !beverageHandled && preferences.autoNormalTakeBeverage) {
    step = 'ensure-beverage';
  } else if (beverageHandled && !foodDelivered) {
    step = 'ensure-cooking';
  } else if (base.prepared && !foodDelivered) {
    step = 'deliver-food';
  }

  const madeProgress = prepared !== base.prepared
    || foodDelivered !== base.foodDelivered
    || beverageHandled !== base.beverageHandled
    || completed !== base.completed
    || step !== base.step;
  const clearsPause = base.paused && !base.manualResolutionRequired && snapshotPassesPausedStage(
    base.pausedStage,
    foodDelivered,
    beverageHandled,
    snapshotReadyToEvaluate,
    completed,
  );
  if (base.paused && !clearsPause) step = 'paused';
  const resetsFailure = madeProgress && (!base.paused || clearsPause);

  return {
    ...base,
    executionTarget: completed ? null : base.executionTarget,
    executionTargetBusinessGeneration: completed ? 0 : base.executionTargetBusinessGeneration,
    prepared,
    cookingJobId: foodDelivered && !base.manualResolutionRequired ? '' : base.cookingJobId,
    beverageHandled,
    beverageHandledAtMs: beverageHandled && base.beverageHandledAtMs <= 0 ? now : base.beverageHandledAtMs,
    foodDelivered,
    foodDeliveredAtMs: foodDelivered && base.foodDeliveredAtMs <= 0 ? now : base.foodDeliveredAtMs,
    completed,
    completedAtMs: completed && base.completedAtMs <= 0 ? now : base.completedAtMs,
    step,
    stepStartedAtMs: resolveAutomationStepStartedAtMs(base.step, step, base.stepStartedAtMs, now),
    lastProgressAtMs: madeProgress ? now : base.lastProgressAtMs,
    retryCount: resetsFailure ? 0 : base.retryCount,
    retryStage: resetsFailure ? '' : base.retryStage,
    nextAttemptAtMs: resetsFailure ? 0 : base.nextAttemptAtMs,
    rollbackCount: base.rollbackCount,
    lastError: clearsPause ? '' : base.lastError,
    paused: clearsPause ? false : base.paused,
    manualResolutionRequired: base.manualResolutionRequired,
    pausedStage: clearsPause ? '' : base.pausedStage,
    pauseReasonCode: clearsPause ? '' : base.pauseReasonCode,
  };
}

/**
 * 判断是否应尝试为普客订单开始料理。
 */
export function shouldAttemptNormalCooking(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  preferences: CompanionPreferences,
  now: number,
): boolean {
  if (!preferences.autoNormalStartCooking) return false;
  if (order.hasServedFood || order.foodId < 0) return false;
  if (state?.paused) return false;
  if ((state?.nextAttemptAtMs ?? 0) > now) return false;
  return !state?.prepared;
}

/**
 * 判断是否应尝试为普客订单处理酒水。
 */
export function shouldAttemptNormalBeverage(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  preferences: CompanionPreferences,
  now: number,
): boolean {
  if (!preferences.autoNormalTakeBeverage) return false;
  if (order.hasServedBeverage || order.beverageId < 0) return false;
  if (state?.beverageHandled) return false;
  if (state?.paused) return false;
  if ((state?.nextAttemptAtMs ?? 0) > now) return false;
  return true;
}

/**
 * 判断普客订单是否具备触发完成评价的条件。
 */
export function shouldAttemptNormalCompletion(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  preferences: CompanionPreferences,
  now: number,
): boolean {
  if (!preferences.autoNormalCompleteOrder) return false;
  if (order.hasEvaluated || state?.completed) return false;
  if (state?.paused) return false;
  if ((state?.nextAttemptAtMs ?? 0) > now) return false;
  const hasFood = order.hasServedFood || state?.foodDelivered;
  const hasBeverage = order.hasServedBeverage || state?.beverageHandled;
  return Boolean(order.readyToEvaluate || (hasFood && hasBeverage));
}

/**
 * 在单轮自动化中预约一个厨具槽位。
 *
 * 预约只影响前端本轮选择，不修改游戏状态；真正厨具占用由 Mod 开火后产生。
 */
export function reserveAutomationCookerSlot(
  cycle: AutomationCookerCycle,
  cooker: CookerRequirement | null,
  label: string,
  pool: AutomationCookerPool,
): CookerReservationResult {
  if (!cooker) return { ok: true, message: '' };
  const slot = findAvailableAutomationCookerSlot(pool, cooker.key, cycle.usedControllerIndexes);
  if (!slot) return buildCookerReservationFailure(cycle, cooker, pool);
  return reserveAutomationCookerController(cycle, slot, label);
}

/**
 * 为稀客订单预约厨具槽位，并尊重普客本轮预留需求。
 */
export function reserveRareCookerSlot(
  cycle: AutomationCookerCycle,
  cooker: CookerRequirement | null,
  label: string,
  pool: AutomationCookerPool,
  normalDemand: NormalCookerDemand,
): CookerReservationResult {
  if (!cooker) return { ok: true, message: '' };
  const unavailableControllerIndexes = new Set([
    ...cycle.usedControllerIndexes,
    ...normalDemand.controllerIndexes,
  ]);
  const slot = findAvailableAutomationCookerSlot(pool, cooker.key, unavailableControllerIndexes);
  if (slot) return reserveAutomationCookerController(cycle, slot, label);

  const normalLabels = pool.slots
    .filter((item) =>
      item.supportedKeys.includes(cooker.key)
      && normalDemand.controllerIndexes.has(item.controllerIndex)
    )
    .map((item) => normalDemand.labelsByControllerIndex.get(item.controllerIndex) ?? '')
    .filter(Boolean);
  if (normalLabels.length > 0) {
    return {
      ok: false,
      message: `等待厨具 ${cooker.label}：本轮优先给普客订单使用（${normalLabels.join('、')}）。`,
    };
  }

  return buildCookerReservationFailure(cycle, cooker, pool);
}

/**
 * 从当前稀客订单推荐中选择可执行的自动化候选。
 *
 * 选择时会结合收藏限定、已锁定目标和推荐兜底方案，返回全部可执行项以及跳过原因。
 * 并发上限只能在读取当前厨具容量后应用，避免排序靠前但厨具锁定的订单阻塞其他厨具类型。
 */
export function selectOrderPreparationCandidates(
  recommendations: OrderRecommendation[],
  favorites: FavoriteData,
  preferences: CompanionPreferences,
  states?: ReadonlyMap<string, AutoFirstOrderState>,
): OrderPreparationCandidateResult {
  const rows = sortNightOrderRows(
    recommendations.map((item) => ({ order: item.order, item })),
    preferences.serviceOrderSortMode,
  );
  if (rows.length === 0) {
    return { selections: [], skips: [], messages: [], message: '暂无可准备的稀客订单。' };
  }

  const selections: ValidOrderPreparationSelection[] = [];
  const skips: OrderPreparationCandidateSkip[] = [];
  const messages: string[] = [];
  for (const row of rows) {
    const item = row.item;
    const label = formatRareAutomationPrefix(item);
    const missingIdentityFields = [
      item.order.deskCode < 0 ? '桌位' : '',
      item.order.runtimeGuestId == null ? '运行时稀客 ID' : '',
      item.order.foodTagId == null ? '料理 Tag ID' : '',
      item.order.beverageTagId == null ? '酒水 Tag ID' : '',
    ].filter(Boolean);
    if (missingIdentityFields.length > 0) {
      const skip = buildOrderPreparationSkip(
        item,
        label,
        'runtime-identity-missing',
        `运行时订单身份不完整（缺少${missingIdentityFields.join('、')}），自动化不会使用展示文本推测目标。`,
      );
      skips.push(skip);
      messages.push(skip.message);
      continue;
    }

    if (item.order.automationAllowed === false) {
      const skip = buildOrderPreparationSkip(
        item,
        label,
        'automation-blocked',
        item.order.automationBlockReason || '特殊经营订单暂不允许标准自动化接管。',
      );
      skips.push(skip);
      messages.push(skip.message);
      continue;
    }

    const state = states?.get(buildAutoOrderKey(item));
    const needsRecipeTarget = preferences.autoPrepStartCooking
      && !state?.prepared
      && !item.order.hasServedFood;
    const needsBeverageTarget = preferences.autoPrepTakeBeverage
      && !state?.beverageHandled
      && !item.order.hasServedBeverage;
    const planPick = pickPlanForPreparation(item, favorites, preferences);
    const recipeTarget = planPick.recipe
      ? buildRareRecipeTarget(item, planPick.recipe, planPick.recipeFavorite, planPick.preferenceFallback)
      : null;
    const beverageTarget = planPick.beverage
      ? buildRareBeverageTarget(planPick.beverage, planPick.beverageFavorite)
      : null;

    if (!recipeTarget && needsRecipeTarget) {
      const skip = buildOrderPreparationSkip(
        item,
        label,
        preferences.autoPrepRecipeFavoritesOnly ? 'recipe-favorite-missing' : 'recipe-target-missing',
        formatRareAutomationMissingRecipeTargetMessage(item, preferences.autoPrepRecipeFavoritesOnly),
      );
      skips.push(skip);
      messages.push(skip.message);
      continue;
    }
    if (!beverageTarget && needsBeverageTarget) {
      const skip = buildOrderPreparationSkip(
        item,
        label,
        preferences.autoPrepBeverageFavoritesOnly ? 'beverage-favorite-missing' : 'beverage-target-missing',
        formatRareAutomationMissingBeverageTargetMessage(item, preferences.autoPrepBeverageFavoritesOnly),
      );
      skips.push(skip);
      messages.push(skip.message);
      continue;
    }

    selections.push({
      ok: true,
      item,
      recipe: planPick.recipe,
      beverage: planPick.beverage,
      recipeTarget,
      beverageTarget,
      recipeFavorite: planPick.recipeFavorite,
      beverageFavorite: planPick.beverageFavorite,
    });
  }

  return {
    selections,
    skips,
    messages,
    message: selections.length > 0 ? '' : messages[0] ?? '当前稀客订单没有可执行的自动化候选。',
  };
}

export function formatRareAutomationMissingRecipeTargetMessage(
  item: OrderRecommendation,
  favoritesOnly: boolean,
): string {
  if (favoritesOnly) {
    return item.recipes.length > 0
      ? '推荐页已有料理候选，但没有匹配收藏限定的自动化料理。'
      : '没有匹配的收藏料理。';
  }

  if (item.recipes.length > 0 && item.executionPlans.length > 0) {
    return '推荐页已有料理候选，但没有同时满足当前订单、特殊经营和酒水条件的自动化料理。';
  }

  if (item.recipes.length > 0) {
    return appendRecommendationBlockedDetails('推荐页已有料理候选，但没有可直接执行的完整自动化方案。', item);
  }

  return item.blockedDiagnostic?.message ?? '没有可用的推荐料理。';
}

export function formatRareAutomationMissingBeverageTargetMessage(
  item: OrderRecommendation,
  favoritesOnly: boolean,
): string {
  if (favoritesOnly) {
    return item.beverages.length > 0
      ? '推荐页已有酒水候选，但没有匹配收藏限定的自动化酒水。'
      : '没有匹配的收藏酒水。';
  }

  if (item.beverages.length > 0 && item.executionPlans.length > 0) {
    return '推荐页已有酒水候选，但没有同时满足当前订单、特殊经营和料理条件的自动化酒水。';
  }

  if (item.beverages.length > 0) {
    return appendRecommendationBlockedDetails('推荐页已有酒水候选，但没有可直接执行的完整自动化方案。', item);
  }

  return item.blockedDiagnostic?.message ?? '没有可用的推荐酒水。';
}

function appendRecommendationBlockedDetails(message: string, item: OrderRecommendation): string {
  if (item.blockedMessages.length === 0) return message;
  const details = item.blockedMessages
    .slice(0, 2)
    .map((value) => value.replace(/\s+/g, ' ').trim())
    .filter(Boolean)
    .join('；');
  return details ? `${message}原因：${details}` : message;
}

function buildOrderPreparationSkip(
  item: OrderRecommendation,
  label: string,
  reason: OrderPreparationSkipReason,
  detail: string,
): OrderPreparationCandidateSkip {
  return {
    reason,
    orderKey: buildAutoOrderKey(item),
    label,
    message: `${label}\n${detail}`,
    recipeRecommendationCount: item.recipes.length,
    beverageRecommendationCount: item.beverages.length,
    executionPlanCount: item.executionPlans.length,
  };
}

/**
 * 锁定一笔稀客订单的自动化料理和酒水目标。
 *
 * 锁定后即使推荐列表因库存或快照刷新重新排序，也继续处理最初选择的目标，避免自动化中途换菜。
 */
export function lockRareAutomationTargets(
  state: AutoFirstOrderState,
  selection: ValidOrderPreparationSelection,
): AutoFirstOrderState {
  const recipeTarget = state.recipeTarget ?? selection.recipeTarget;
  const beverageTarget = state.beverageTarget ?? selection.beverageTarget;
  if (recipeTarget === state.recipeTarget && beverageTarget === state.beverageTarget) return state;

  return {
    ...state,
    recipeTarget,
    beverageTarget,
  };
}

/**
 * 判断稀客自动化是否至少启用了一个动作。
 */
export function hasAutomationActionEnabled(preferences: CompanionPreferences): boolean {
  return preferences.autoRareOrderEnabled
    && (preferences.autoPrepCompleteOrder
      || preferences.autoPrepTakeBeverage
      || preferences.autoPrepStartCooking
      || preferences.autoPrepCollectCooking);
}

export function hasNormalOrderActionEnabled(preferences: CompanionPreferences): boolean {
  return preferences.autoNormalTakeBeverage
    || preferences.autoNormalStartCooking
    || preferences.autoNormalDeliverFood
    || preferences.autoNormalCompleteOrder;
}

/**
 * 构建稀客自动化状态键。
 *
 * 可执行订单以原生 trace 与 lifecycle sequence 建键；不可执行行只保留隔离的展示键。
 */
export function buildAutoOrderKey(item: OrderRecommendation): string {
  const order = item.order;
  if (order.orderLifecycleSequence > 0) {
    const runtimeIdentity = order.traceId?.trim()
      ? `trace:${order.traceId.trim()}`
      : [
        order.deskCode,
        order.runtimeGuestId ?? 'unknown-runtime-guest',
        order.foodTagId ?? 'unknown-food-tag',
        order.beverageTagId ?? 'unknown-beverage-tag',
      ].join('|');
    return `${runtimeIdentity}|lifecycle:${order.orderLifecycleSequence}`;
  }
  return [
    'unbound',
    order.firstSeenAtUtc ?? order.lastSeenAtUtc ?? '',
    order.deskCode,
    order.runtimeGuestId ?? 'unknown-runtime-guest',
    order.foodTagId ?? 'unknown-food-tag',
    order.beverageTagId ?? 'unknown-beverage-tag',
    order.isFreeOrder ? 'free' : 'paid',
  ].join('|');
}

/**
 * 构建夜间稀客订单快照键。
 */
export function buildNightBusinessOrderKey(order: NightBusinessOrder): string {
  if (order.orderLifecycleSequence > 0) {
    const runtimeIdentity = order.traceId?.trim()
      ? `trace:${order.traceId.trim()}`
      : [
        order.deskCode,
        order.runtimeGuestId ?? 'unknown-runtime-guest',
        order.foodTagId ?? 'unknown-food-tag',
        order.beverageTagId ?? 'unknown-beverage-tag',
      ].join('|');
    return `${runtimeIdentity}|lifecycle:${order.orderLifecycleSequence}`;
  }
  return [
    'unbound',
    order.firstSeenAtUtc ?? order.lastSeenAtUtc ?? '',
    order.deskCode,
    order.runtimeGuestId ?? 'unknown-runtime-guest',
    order.foodTagId,
    order.beverageTagId,
    order.source,
    order.isFreeOrder ? 'free' : 'paid',
  ].join('|');
}

export function formatRareAutomationPrefix(item: OrderRecommendation): string {
  const order = item.order;
  return `${order.guestName || '稀客'} · 桌 ${formatDesk(order.deskCode)}\n料理 ${order.foodTag || '无'} / 酒水 ${order.beverageTag || '无'}`;
}

export function buildRareAutoOrderDiagnostic(
  selection: ValidOrderPreparationSelection,
  state: AutoFirstOrderState,
  now: number,
): RareAutoOrderDiagnostic {
  const order = selection.item.order;
  return {
    orderKey: buildAutoOrderKey(selection.item),
    traceId: order.traceId,
    title: `${order.guestName || '稀客'} · 桌 ${formatDesk(order.deskCode)}`,
    foodTag: order.foodTag || '',
    beverageTag: order.beverageTag || '',
    recipeName: formatRareAutomationRecipeName(state.recipeTarget, selection.recipeTarget, selection.recipe),
    beverageName: state.beverageTarget?.beverageName ?? selection.beverageTarget?.beverageName ?? selection.beverage?.beverage.name ?? '',
    stepLabel: getAutomationStepLabel(state.step),
    stepSeconds: resolveAutomationStepSeconds(state.stepStartedAtMs, now),
    nextAction: getRareAutomationNextAction(state),
    retryCount: state.retryCount,
    rollbackCount: state.rollbackCount,
    lastError: state.lastError,
    detailMessage: state.detailMessage,
    detailUpdatedAtMs: state.detailUpdatedAtMs,
    prepared: state.prepared || Boolean(order.hasServedFood),
    beverageDeliveryRequested: state.beverageHandled || Boolean(order.hasServedBeverage),
    hasServedFood: Boolean(order.hasServedFood),
    hasServedBeverage: Boolean(order.hasServedBeverage),
    paused: state.paused,
    manualResolutionRequired: state.manualResolutionRequired,
  };
}

/**
 * 构建普客自动化诊断行。
 */
export function buildNormalAutoOrderDiagnostics(
  orders: NormalBusinessOrder[],
  states: Map<string, NormalAutoOrderState>,
  now: number,
): NormalAutoOrderDiagnostic[] {
  return sortNormalOrders(orders)
    .filter((order) => !order.hasEvaluated)
    .map((order) => {
      const orderKey = buildNormalAutoOrderKey(order);
      const state = states.get(orderKey) ?? emptyNormalAutoOrderState(orderKey, now);
      return buildNormalAutoOrderDiagnostic(order, state, now);
    });
}

/**
 * 构建普客订单快照签名，用于在订单状态变化时立即触发自动化复查。
 */
export function buildNormalOrderAutomationSignature(orders: NormalBusinessOrder[]): string {
  return sortNormalOrders(orders)
    .map((order) => [
      buildNormalAutoOrderKey(order),
      order.hasEvaluated ? 'evaluated' : order.readyToEvaluate ? 'ready' : 'open',
      order.hasServedFood ? 'food-served' : 'food-open',
      order.hasServedBeverage ? 'bev-served' : 'bev-open',
      order.canAutomate === false ? 'blocked' : 'runnable',
      `role=${order.specialBusinessRole?.trim() ?? ''}`,
      order.controllerAvailable === false ? 'controller-missing' : 'controller-ok',
      order.foodId,
      order.beverageId,
      order.deskCode,
      order.runtimeGuestId ?? 'unknown-runtime-guest',
    ].join(':'))
    .join('|');
}

export { buildNormalAutoOrderKey, buildNormalLifecycleAutoOrderKey };

/**
 * 用订单快照中的已送达字段推进稀客自动化本地状态。
 */
export function syncRareStateWithOrderServedState(
  state: AutoFirstOrderState,
  order: NightBusinessOrder,
  now: number,
): AutoFirstOrderState {
  if (!order.hasServedFood && !order.hasServedBeverage) return state;
  return applyRareServedStateFromResponse(
    state,
    order,
    {
      ok: false,
      prepared: false,
      error: null,
      order: {
        traceId: order.traceId,
        deskCode: order.deskCode,
        guestId: order.guestId,
        guestName: order.guestName,
        foodTag: order.foodTag,
        beverageTag: order.beverageTag,
      },
      recipeId: -1,
      recipeName: '',
      beverageId: -1,
      beverageName: '',
      servedFood: order.hasServedFood,
      servedBeverage: order.hasServedBeverage,
      completedOrder: false,
      automation: {
        outcome: 'progressed',
        stage: 'order',
        reasonCode: 'order-snapshot-progressed',
        jobId: '',
        retryAfterMs: 0,
      },
      steps: [],
    },
    now,
  );
}

/**
 * 根据 Mod 返回的订单准备结果推进稀客自动化状态。
 */
export function applyRareServedStateFromResponse(
  state: AutoFirstOrderState,
  order: NightBusinessOrder,
  response: OrderPreparationResponse,
  now: number,
): AutoFirstOrderState {
  const servedFood = Boolean(response.servedFood)
    || Boolean(order.hasServedFood)
    || response.steps.some((step) => step.code === 'food-delivered' && step.ok);
  const servedBeverage = Boolean(response.servedBeverage)
    || Boolean(order.hasServedBeverage)
    || response.steps.some((step) => step.code === 'beverage-delivered' && step.ok);
  const madeProgress = (servedFood && (!state.prepared || Boolean(state.cookingJobId)))
    || (servedBeverage && !state.beverageHandled);
  if (!madeProgress) return state;

  const nextPrepared = state.prepared || servedFood;
  const nextBeverageHandled = state.beverageHandled || servedBeverage;
  const clearsPause = state.paused && !state.manualResolutionRequired && snapshotPassesPausedStage(
    state.pausedStage,
    servedFood,
    servedBeverage,
    servedFood && servedBeverage,
    Boolean(response.completedOrder),
  );
  const nextStep: AutomationStep = state.paused && !clearsPause
    ? 'paused'
    : servedFood && servedBeverage
      ? 'complete-order'
      : servedFood
        ? 'ensure-beverage'
        : 'ensure-cooking';
  return {
    ...state,
    prepared: nextPrepared,
    cookingJobId: servedFood && !state.manualResolutionRequired ? '' : state.cookingJobId,
    beverageHandled: nextBeverageHandled,
    beverageHandledAtMs: nextBeverageHandled && !state.beverageHandled ? now : state.beverageHandledAtMs,
    lastProgressAtMs: now,
    step: nextStep,
    stepStartedAtMs: resolveAutomationStepStartedAtMs(
      state.step,
      nextStep,
      state.stepStartedAtMs,
      now,
    ),
    retryCount: clearsPause || !state.paused ? 0 : state.retryCount,
    retryStage: clearsPause || !state.paused ? '' : state.retryStage,
    nextAttemptAtMs: clearsPause || !state.paused ? 0 : state.nextAttemptAtMs,
    lastError: clearsPause ? '' : state.lastError,
    paused: clearsPause ? false : state.paused,
    manualResolutionRequired: state.manualResolutionRequired,
    pausedStage: clearsPause ? '' : state.pausedStage,
    pauseReasonCode: clearsPause ? '' : state.pauseReasonCode,
  };
}

function snapshotPassesPausedStage(
  pausedStage: AutomationStep | '',
  servedFood: boolean,
  servedBeverage: boolean,
  readyToEvaluate: boolean,
  completed: boolean,
): boolean {
  if (completed) return true;
  switch (pausedStage) {
    case 'ensure-beverage':
      return servedBeverage || readyToEvaluate;
    case 'ensure-cooking':
    case 'deliver-food':
      return servedFood || readyToEvaluate;
    case 'complete-order':
      return completed;
    case 'match-order':
    case 'idle':
      return servedFood || servedBeverage || readyToEvaluate;
    default:
      return false;
  }
}

/**
 * 将 Mod 订单处理响应格式化为用户可读的多行文本。
 */
export function formatOrderPreparationResponse(response: OrderPreparationResponse) {
  const traceSuffix = response.order.traceId ? ` · 日志 ${response.order.traceId}` : '';
  const title = response.ok
    ? `已处理：${response.order.guestName} · 桌 ${formatDesk(response.order.deskCode)}${traceSuffix}`
    : `未完成：${response.order.guestName || '当前订单'} · 桌 ${formatDesk(response.order.deskCode)}${traceSuffix}`;
  const target = [
    response.recipeName ? `料理 ${response.recipeName}` : '',
    response.beverageName ? `酒水 ${response.beverageName}` : '',
  ].filter(Boolean).join(' / ');
  const steps = response.steps.map((step) => {
    const prefix = step.skipped ? '跳过' : step.ok ? '完成' : '失败';
    return `${prefix} ${step.name}：${step.message}`;
  });
  return [title, target, ...steps, response.error ? `错误：${response.error}` : ''].filter(Boolean).join('\n');
}

function ensureCookerResourceRow(
  rows: Map<string, AutomationCookerResourceRow>,
  key: string,
  label: string,
  capacity: number,
): AutomationCookerResourceRow {
  const existing = rows.get(key);
  if (existing) {
    existing.capacity = Math.max(existing.capacity, capacity);
    return existing;
  }

  const row: AutomationCookerResourceRow = {
    key,
    label,
    capacity: Math.max(0, capacity),
    normalReserved: 0,
    rareReserved: 0,
    labels: [],
  };
  rows.set(key, row);
  return row;
}

function reserveAutomationCookerController(
  cycle: AutomationCookerCycle,
  slot: AutomationCookerSlot,
  label: string,
): CookerReservationResult {
  cycle.usedControllerIndexes.add(slot.controllerIndex);
  cycle.labelsByControllerIndex.set(slot.controllerIndex, label);
  return {
    ok: true,
    message: '',
    controllerIndex: slot.controllerIndex,
    controllerIdentity: slot.controllerIdentity,
    gridPosition: { ...slot.gridPosition },
  };
}

function buildCookerReservationFailure(
  cycle: AutomationCookerCycle,
  cooker: CookerRequirement,
  pool: AutomationCookerPool,
): CookerReservationResult {
  const compatibleSlots = pool.slots.filter((slot) => slot.supportedKeys.includes(cooker.key));
  const owners = compatibleSlots
    .map((slot) => cycle.labelsByControllerIndex.get(slot.controllerIndex) ?? '')
    .filter(Boolean);
  if (compatibleSlots.length === 0) {
    const incompleteNote = pool.snapshotComplete
      ? ''
      : '；当前厨具快照不完整，未知控制器不会计入容量';
    return {
      ok: false,
      message: `等待厨具 ${cooker.label}：当前已确认自动化容量为 0${incompleteNote}。`,
    };
  }

  return {
    ok: false,
    message: `等待厨具 ${cooker.label}：本轮 ${compatibleSlots.length} 个可用控制器已预约${owners.length > 0 ? `（${owners.join('、')}）` : ''}。`,
  };
}

function buildRareRecipeTarget(
  _item: OrderRecommendation,
  recipe: RareRecipeRecommendation,
  favorite: FavoriteRecipeEntry | null,
  preferenceFallback = false,
): RareAutomationRecipeTarget {
  return {
    ...emptySpecialFoodTargetWirePolicy(),
    recipeId: recipe.recipe.recipeId,
    foodId: recipe.recipe.id,
    recipeName: recipe.recipe.name,
    cookerName: recipe.recipe.cooker,
    extraIngredientIds: recipe.extraIngredients.map((ingredient) => ingredient.id),
    foodTags: recipe.allTags,
    favorite: Boolean(favorite),
    preferenceFallback,
  };
}

function buildRareBeverageTarget(
  beverage: RareBeverageRecommendation,
  favorite: FavoriteBeverageEntry | null,
): RareAutomationBeverageTarget {
  return {
    beverageId: beverage.beverage.id,
    beverageName: beverage.beverage.name,
    favorite: Boolean(favorite),
  };
}

function formatRareAutomationRecipeName(
  stateTarget: RareAutomationRecipeTarget | null,
  selectionTarget: RareAutomationRecipeTarget | null,
  selectedRecipe: RareRecipeRecommendation | null,
): string {
  const target = stateTarget ?? selectionTarget;
  const name = target?.recipeName ?? selectedRecipe?.recipe.name ?? '';
  if (!name) return '';
  return target?.preferenceFallback ? `${name}（喜好备选）` : name;
}

function buildNormalAutoOrderDiagnostic(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState,
  now: number,
): NormalAutoOrderDiagnostic {
  return {
    orderKey: buildNormalAutoOrderKey(order),
    traceId: order.traceId,
    title: `桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}`,
    foodName: order.foodName || `#${order.foodId}`,
    beverageName: order.beverageName || `#${order.beverageId}`,
    source: order.source || '',
    stepLabel: getAutomationStepLabel(state.step),
    stepSeconds: resolveAutomationStepSeconds(state.stepStartedAtMs, now),
    nextAction: getNormalAutomationNextAction(state, now),
    retryCount: state.retryCount,
    rollbackCount: state.rollbackCount,
    lastError: state.lastError,
    detailMessage: state.detailMessage,
    detailUpdatedAtMs: state.detailUpdatedAtMs,
    prepared: state.prepared || order.hasServedFood,
    beverageDeliveryRequested: state.beverageHandled || order.hasServedBeverage,
    foodDeliveryRequested: state.foodDelivered || order.hasServedFood,
    completed: state.completed || order.hasEvaluated,
    paused: state.paused,
    manualResolutionRequired: state.manualResolutionRequired,
    hasServedFood: order.hasServedFood,
    hasServedBeverage: order.hasServedBeverage,
    readyToEvaluate: order.readyToEvaluate,
    hasEvaluated: order.hasEvaluated,
    controllerAvailable: order.controllerAvailable,
    canAutomate: order.canAutomate,
    actionBlockReason: order.actionBlockReason,
  };
}

function getRareAutomationNextAction(state: AutoFirstOrderState): string {
  if (state.manualResolutionRequired) return '确认游戏状态后点击“确认已处理”';
  if (state.paused) return '等待手动重试或订单变化';
  if (state.step === 'complete-order') return '下一轮尝试完成订单';
  if (state.step === 'ensure-beverage') return '下一轮校验酒水送达';
  if (state.step === 'ensure-cooking') return '下一轮校验厨具/开锅';
  if (state.step === 'match-order') return '下一轮匹配订单';
  if (state.step === 'done') return '等待订单从列表移除';
  return '下一轮刷新';
}

function getNormalAutomationNextAction(
  state: NormalAutoOrderState,
  now: number,
): string {
  if (state.manualResolutionRequired) return '确认游戏状态后点击“确认已处理”';
  if (state.paused) {
    void now;
    return '等待订单变化或手动处理';
  }
  if (state.completed || state.step === 'done') return '等待订单从列表移除';
  if (state.step === 'complete-order') return '下一轮尝试完成订单';
  if (state.step === 'deliver-food') return '等待 Mod 料理任务送达';
  if (state.step === 'ensure-beverage') return '下一轮校验酒水';
  if (state.prepared) {
    return '等待 Mod 料理任务送达';
  }
  if (state.step === 'ensure-cooking') return '下一轮校验厨具/开锅';
  if (state.step === 'match-order') return '下一轮匹配订单';
  return '下一轮刷新';
}

function pickPlanForPreparation(
  item: OrderRecommendation,
  favorites: FavoriteData,
  preferences: CompanionPreferences,
): {
  recipe: RareRecipeRecommendation | null;
  beverage: RareBeverageRecommendation | null;
  recipeFavorite: FavoriteRecipeEntry | null;
  beverageFavorite: FavoriteBeverageEntry | null;
  preferenceFallback: boolean;
} {
  const needsRecipe = preferences.autoPrepStartCooking;
  const needsBeverage = preferences.autoPrepTakeBeverage;
  if (!needsRecipe && !needsBeverage) {
    return emptyPlanPick();
  }

  const plan = getPrimaryExecutionPlan(item.executionPlans);
  if (!plan) {
    return emptyPlanPick();
  }

  const recipe = plan.food ? getRecipeRowForPlan(item, plan) : null;
  const beverage = plan.beverage ? getBeverageRowForPlan(item, plan) : null;
  const recipeFavorite = recipe ? findRecipeFavorite(favorites, item.customer.id, item.order.foodTag, recipe) : null;
  const beverageFavorite = beverage ? findBeverageFavorite(favorites, item.customer.id, item.order.beverageTag, beverage) : null;
  if (needsRecipe && (!recipe || (preferences.autoPrepRecipeFavoritesOnly && !recipeFavorite))) {
    return emptyPlanPick();
  }
  if (needsBeverage && (!beverage || (preferences.autoPrepBeverageFavoritesOnly && !beverageFavorite))) {
    return emptyPlanPick();
  }

  return {
    recipe: needsRecipe ? recipe : null,
    beverage: needsBeverage ? beverage : null,
    recipeFavorite: needsRecipe ? recipeFavorite : null,
    beverageFavorite: needsBeverage ? beverageFavorite : null,
    preferenceFallback: Boolean(needsRecipe && recipe && !recipe.meetsRequiredFood),
  };
}

function emptyPlanPick() {
  return {
    recipe: null,
    beverage: null,
    recipeFavorite: null,
    beverageFavorite: null,
    preferenceFallback: false,
  };
}

function findRecipeRowForPlan(
  item: OrderRecommendation,
  recipeId: number,
  extraIngredientIds: number[],
): RareRecipeRecommendation | null {
  const normalizedExtras = normalizeIdList(extraIngredientIds).join(',');
  return item.recipes.find((recipe) =>
    recipe.recipe.id === recipeId
    && normalizeIdList(recipe.extraIngredients.map((ingredient) => ingredient.id)).join(',') === normalizedExtras
  ) ?? null;
}

function findRecipeRowForTarget(
  item: OrderRecommendation,
  target: RareAutomationRecipeTarget,
): RareRecipeRecommendation | null {
  return findRecipeRowForPlan(item, target.foodId, target.extraIngredientIds);
}

function rareRecipeTargetMatchesSpecialTarget(
  item: OrderRecommendation,
  target: RareAutomationRecipeTarget,
  foodTarget: ReturnType<typeof buildSpecialBusinessOrderRule>['foodTarget'],
): boolean {
  const recipe = findRecipeRowForTarget(item, target);
  const tags = target.foodTags.length ? target.foodTags : recipe?.allTags ?? [];
  return matchesSpecialBusinessFoodTarget(tags, foodTarget);
}

function isRareRecipeTargetRejectedBySpecialBusiness(
  target: RareAutomationRecipeTarget,
  targetTags: readonly string[],
  rejectedRecipeKeys: ReadonlySet<string>,
): boolean {
  if (rejectedRecipeKeys.size === 0) return false;
  const key = buildWackyRejectedRecipeKeyForRareRecipe(
    targetTags,
    target.foodId,
    target.recipeId,
    target.extraIngredientIds,
  );
  return Boolean(key && rejectedRecipeKeys.has(key));
}

function getRecipeRowForPlan(
  item: OrderRecommendation,
  plan: RareOrderRecommendationPlan,
): RareRecipeRecommendation | null {
  if (!plan.food) return null;
  return findRecipeRowForPlan(
    item,
    plan.food.recipe.id,
    plan.food.extraIngredients.map((ingredient) => ingredient.id),
  ) ?? toRareRecipeResult(plan.food);
}

function findBeverageRowForPlan(
  item: OrderRecommendation,
  beverageId: number,
): RareBeverageRecommendation | null {
  return item.beverages.find((beverage) =>
    beverage.beverage.id === beverageId
  ) ?? null;
}

function getBeverageRowForPlan(
  item: OrderRecommendation,
  plan: RareOrderRecommendationPlan,
): RareBeverageRecommendation | null {
  if (!plan.beverage) return null;
  return findBeverageRowForPlan(item, plan.beverage.beverage.id) ?? {
    beverage: plan.beverage.beverage,
    meetsRequiredBev: plan.beverage.meetsRequiredBeverage,
    matchedTags: plan.beverage.matchedTags,
  };
}

function isSameRareRecipeTarget(
  left: RareAutomationRecipeTarget,
  right: RareAutomationRecipeTarget,
): boolean {
  return left.foodId === right.foodId
    && left.recipeId === right.recipeId
    && normalizeIdList(left.extraIngredientIds).join(',') === normalizeIdList(right.extraIngredientIds).join(',');
}
