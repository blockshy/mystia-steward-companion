import type {
  OrderPreparationResponse,
  RareAutomationBeverageTarget,
  RareAutomationRecipeTarget,
} from '@/companion/automation-state';
import { readLocalApiJson, writeLocalApiJsonWithTimeout } from '@/companion/local-api';
import type { CompanionPreferences } from '@/companion/preferences';
import { normalizeEditableQuantity } from '@/companion/preferences';
import { serializeRareGuestInvitationLevels } from '@/companion/storage';
import type {
  DiagnosticPackageResponse,
  AutomationCancellationResponse,
  AutomationSafetyBarrierAckResponse,
  CustomRecipeData,
  CustomRecipeFlagUpdateInput,
  CustomRecipeMutationResponse,
  CustomRecipeUpsertInput,
  FavoriteData,
  FavoriteMutationResponse,
  GameUiPinningTarget,
  InventoryBulkEditResponse,
  InventoryEditResponse,
  LocalApiAutomationLease,
  LocalApiConnectionConfig,
  LocalApiFolderResponse,
  LocalApiLogSettings,
  LocalApiSnapshotResponse,
  LocalApiStatusResponse,
  NightBusinessOrder,
  NormalOrderExecutionTarget,
  NormalBusinessOrder,
  OrderRecommendation,
  RareGuestInvitationResponse,
  RareGuestInvitationScope,
  RareOrderDismissResponse,
  UpdateStatusResponse,
} from '@/companion/types';
import {
  DEFAULT_RECOMMENDATION_DATA,
  buildRecommendationDataIndexes,
  type RuntimeDataCatalogSnapshot,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
import type {
  RareCustomerCatalogItem,
} from '@/lib/catalog-types';
import type { RareBeverageRecommendation, RareOrderRecommendationPlan, RareRecipeRecommendation } from '@/recommendation-engine';

export interface AutomationDecisionDiagnosticRequest {
  signature: string;
  eventName: string;
  message: string;
  scene: string;
  challengeType: string;
  phase: string;
  specialBusinessRole: string;
  orderCount: number;
  selectionCount: number;
  skipCount: number;
  automationEnabled: boolean;
  leaseOwned: boolean;
  autoCompleteOrder: boolean;
  autoTakeBeverage: boolean;
  autoStartCooking: boolean;
  autoCollectCooking: boolean;
  recipeFavoritesOnly: boolean;
  beverageFavoritesOnly: boolean;
  rareConcurrency: number;
  leaseMessage: string;
  orderLines: string[];
  selectionLines: string[];
  skipLines: string[];
}

/**
 * 伴随窗口访问 Mod 本地 API 的类型化门面。
 *
 * 该文件只负责把 UI/推荐引擎中的领域对象转换为本地 API 协议参数，不直接保存状态。
 * 纯读取端点使用 GET；任何会修改 Mod、游戏运行时、文件或宿主窗口状态的命令都通过
 * `writeLocalApiJsonWithTimeout` 使用 POST，避免被普通刷新或预取误触发。
 */
export async function readSnapshot(
  endpoint: string,
  apiToken: string,
  options: { signal: AbortSignal; timeoutMs: number; knownSignature?: string },
): Promise<LocalApiSnapshotResponse> {
  const params = new URLSearchParams();
  if (options.knownSignature) params.set('knownSignature', options.knownSignature);
  const path = params.size > 0 ? `/snapshot?${params.toString()}` : '/snapshot';
  return readLocalApiJson<LocalApiSnapshotResponse>(endpoint, apiToken, path, {
    signal: options.signal,
    tauriTimeoutMs: options.timeoutMs,
  });
}

export async function readRuntimeData(
  endpoint: string,
  apiToken: string,
  options: { signal: AbortSignal; timeoutMs: number },
): Promise<RuntimeDataCatalogSnapshot> {
  return readLocalApiJson<RuntimeDataCatalogSnapshot>(endpoint, apiToken, '/runtime-data', {
    signal: options.signal,
    tauriTimeoutMs: options.timeoutMs,
  });
}

export async function readLogSettings(endpoint: string, apiToken: string, signal: AbortSignal): Promise<LocalApiLogSettings> {
  return readLocalApiJson<LocalApiLogSettings>(endpoint, apiToken, '/logs/settings', signal);
}

export async function writeLogSettings(
  endpoint: string,
  apiToken: string,
  next: { aggregateLog?: boolean; aggregateLogMaxFileCount?: number },
  signal: AbortSignal,
): Promise<LocalApiLogSettings> {
  const params = new URLSearchParams();
  if (typeof next.aggregateLog === 'boolean') params.set('aggregateLog', String(next.aggregateLog));
  if (typeof next.aggregateLogMaxFileCount === 'number') params.set('aggregateLogMaxFiles', String(next.aggregateLogMaxFileCount));
  return writeLocalApiJsonWithTimeout<LocalApiLogSettings>(
    endpoint,
    apiToken,
    `/logs/config?${params.toString()}`,
    2800,
    signal,
  );
}

export async function readLocalApiConnectionConfig(
  endpoint: string,
  apiToken: string,
  signal: AbortSignal,
): Promise<LocalApiConnectionConfig> {
  return readLocalApiJson<LocalApiConnectionConfig>(endpoint, apiToken, '/local-api/config', signal);
}

export async function writeLocalApiConnectionConfig(
  endpoint: string,
  apiToken: string,
  next: { lanEnabled: boolean; lanBindHost: string },
): Promise<LocalApiConnectionConfig> {
  const params = new URLSearchParams({
    lanEnabled: String(next.lanEnabled),
    lanHost: next.lanBindHost.trim() || 'auto',
  });
  return writeLocalApiJsonWithTimeout<LocalApiConnectionConfig>(
    endpoint,
    apiToken,
    `/local-api/config?${params.toString()}`,
    3500,
  );
}

export async function regenerateLocalApiToken(
  endpoint: string,
  apiToken: string,
): Promise<LocalApiConnectionConfig> {
  return writeLocalApiJsonWithTimeout<LocalApiConnectionConfig>(
    endpoint,
    apiToken,
    '/local-api/token/regenerate',
    3500,
  );
}

export async function readAutomationLease(
  endpoint: string,
  apiToken: string,
  signal: AbortSignal,
): Promise<LocalApiAutomationLease> {
  return readLocalApiJson<LocalApiAutomationLease>(endpoint, apiToken, '/automation/lease', signal);
}

export async function acquireAutomationLease(
  endpoint: string,
  apiToken: string,
): Promise<LocalApiAutomationLease> {
  return writeLocalApiJsonWithTimeout<LocalApiAutomationLease>(
    endpoint,
    apiToken,
    '/automation/lease/acquire',
    2200,
  );
}

export async function cancelAutomationCookingJobs(
  endpoint: string,
  apiToken: string,
): Promise<AutomationCancellationResponse> {
  return writeLocalApiJsonWithTimeout<AutomationCancellationResponse>(
    endpoint,
    apiToken,
    '/automation/jobs/cancel',
    2800,
  );
}

export async function acknowledgeAutomationSafetyBarrier(
  endpoint: string,
  apiToken: string,
  sequence: number,
): Promise<AutomationSafetyBarrierAckResponse> {
  const params = new URLSearchParams({ sequence: String(sequence) });
  return writeLocalApiJsonWithTimeout<AutomationSafetyBarrierAckResponse>(
    endpoint,
    apiToken,
    `/automation/barriers/ack?${params.toString()}`,
    2800,
  );
}

export async function appendAutomationDecisionDiagnostic(
  endpoint: string,
  apiToken: string,
  diagnostic: AutomationDecisionDiagnosticRequest,
): Promise<LocalApiStatusResponse> {
  const params = new URLSearchParams({
    signature: diagnostic.signature,
    eventName: diagnostic.eventName,
    message: diagnostic.message,
    scene: diagnostic.scene,
    challengeType: diagnostic.challengeType,
    phase: diagnostic.phase,
    specialBusinessRole: diagnostic.specialBusinessRole,
    orderCount: String(diagnostic.orderCount),
    selectionCount: String(diagnostic.selectionCount),
    skipCount: String(diagnostic.skipCount),
    automationEnabled: String(diagnostic.automationEnabled),
    leaseOwned: String(diagnostic.leaseOwned),
    autoCompleteOrder: String(diagnostic.autoCompleteOrder),
    autoTakeBeverage: String(diagnostic.autoTakeBeverage),
    autoStartCooking: String(diagnostic.autoStartCooking),
    autoCollectCooking: String(diagnostic.autoCollectCooking),
    recipeFavoritesOnly: String(diagnostic.recipeFavoritesOnly),
    beverageFavoritesOnly: String(diagnostic.beverageFavoritesOnly),
    rareConcurrency: String(diagnostic.rareConcurrency),
    leaseMessage: diagnostic.leaseMessage,
    orderLines: diagnostic.orderLines.join('\n'),
    selectionLines: diagnostic.selectionLines.join('\n'),
    skipLines: diagnostic.skipLines.join('\n'),
  });
  return writeLocalApiJsonWithTimeout<LocalApiStatusResponse>(
    endpoint,
    apiToken,
    `/diagnostics/automation-decision?${params.toString()}`,
    2500,
  );
}

export async function openLogFolder(
  endpoint: string,
  apiToken: string,
  target: 'aggregate',
  signal: AbortSignal,
): Promise<LocalApiFolderResponse> {
  return writeLocalApiJsonWithTimeout<LocalApiFolderResponse>(
    endpoint,
    apiToken,
    `/logs/open-folder?target=${target}`,
    2800,
    signal,
  );
}

export async function exportDiagnosticPackage(
  endpoint: string,
  apiToken: string,
  signal: AbortSignal,
): Promise<DiagnosticPackageResponse> {
  return writeLocalApiJsonWithTimeout<DiagnosticPackageResponse>(
    endpoint,
    apiToken,
    '/logs/export-diagnostics?open=true',
    8000,
    signal,
  );
}

export async function refreshUpdateStatus(
  endpoint: string,
  apiToken: string,
  signal?: AbortSignal,
): Promise<UpdateStatusResponse> {
  return writeLocalApiJsonWithTimeout<UpdateStatusResponse>(endpoint, apiToken, '/updates/status', 2800, signal);
}

export async function checkForUpdates(
  endpoint: string,
  apiToken: string,
  signal?: AbortSignal,
): Promise<UpdateStatusResponse> {
  return writeLocalApiJsonWithTimeout<UpdateStatusResponse>(endpoint, apiToken, '/updates/check', 15000, signal);
}

export async function downloadUpdate(
  endpoint: string,
  apiToken: string,
  signal?: AbortSignal,
): Promise<UpdateStatusResponse> {
  return writeLocalApiJsonWithTimeout<UpdateStatusResponse>(endpoint, apiToken, '/updates/download', 60000, signal);
}

export async function installUpdateOnExit(
  endpoint: string,
  apiToken: string,
  signal?: AbortSignal,
): Promise<UpdateStatusResponse> {
  return writeLocalApiJsonWithTimeout<UpdateStatusResponse>(endpoint, apiToken, '/updates/install-on-exit', 5000, signal);
}

export async function inviteAllAvailableRareGuests(
  endpoint: string,
  apiToken: string,
  scope: RareGuestInvitationScope,
  levels: number[],
): Promise<RareGuestInvitationResponse> {
  const params = new URLSearchParams({ scope });
  appendRareGuestInvitationLevels(params, levels);
  return mutateRareGuestInvitation(endpoint, apiToken, `/rare-guests/invite-all?${params.toString()}`);
}

export async function fetchAvailableRareGuestInvitations(
  endpoint: string,
  apiToken: string,
  scope: RareGuestInvitationScope,
): Promise<RareGuestInvitationResponse> {
  const params = new URLSearchParams({ scope });
  return mutateRareGuestInvitation(endpoint, apiToken, `/rare-guests/invitations?${params.toString()}`);
}

export async function inviteAvailableRareGuest(
  endpoint: string,
  apiToken: string,
  guestId: number,
  scope: RareGuestInvitationScope,
): Promise<RareGuestInvitationResponse> {
  const params = new URLSearchParams({ guestId: String(guestId), scope });
  return mutateRareGuestInvitation(endpoint, apiToken, `/rare-guests/invite?${params.toString()}`);
}

export async function dismissRuntimeRareOrder(
  endpoint: string,
  apiToken: string,
  order: NightBusinessOrder,
): Promise<RareOrderDismissResponse> {
  const params = new URLSearchParams({
    deskCode: String(order.deskCode),
    guestName: order.guestName,
    foodTagId: String(order.foodTagId),
    beverageTagId: String(order.beverageTagId),
  });
  if (order.guestId != null) params.set('guestId', String(order.guestId));

  return writeLocalApiJsonWithTimeout<RareOrderDismissResponse>(
    endpoint,
    apiToken,
    `/orders/rare/dismiss?${params.toString()}`,
    2500,
  );
}

export async function writeInventoryQuantity(
  endpoint: string,
  apiToken: string,
  itemType: 'ingredient' | 'beverage',
  itemId: number,
  quantity: number,
): Promise<InventoryEditResponse> {
  const params = new URLSearchParams({
    type: itemType,
    id: String(itemId),
    qty: String(normalizeEditableQuantity(quantity)),
  });
  return writeLocalApiJsonWithTimeout<InventoryEditResponse>(
    endpoint,
    apiToken,
    `/inventory/set?${params.toString()}`,
    3200,
  );
}

export async function writeInventoryBulkQuantity(
  endpoint: string,
  apiToken: string,
  itemType: 'ingredient' | 'beverage',
  itemIds: number[],
  quantity: number,
): Promise<InventoryBulkEditResponse> {
  const params = new URLSearchParams({
    type: itemType,
    ids: itemIds.join(','),
    qty: String(normalizeEditableQuantity(quantity)),
  });
  return writeLocalApiJsonWithTimeout<InventoryBulkEditResponse>(
    endpoint,
    apiToken,
    `/inventory/bulk-set?${params.toString()}`,
    8000,
  );
}

export async function publishGameUiPinningTarget(
  endpoint: string,
  apiToken: string,
  enabled: boolean,
  highlightEnabled: boolean,
  target: GameUiPinningTarget | null,
  signal?: AbortSignal,
): Promise<void> {
  // 前端只发布当前推荐目标；Mod 在目标面板刷新作用域内复用游戏原生 pinned 排序，不直接操作 UI 列表。
  const params = new URLSearchParams({
    enabled: String(enabled),
    highlightEnabled: String(highlightEnabled),
    recipeId: target ? String(target.recipeId) : '-1',
    recipeName: target?.recipeName ?? '',
    ingredientIds: target ? target.ingredientIds.join(',') : '',
    beverageId: target ? String(target.beverageId) : '-1',
    beverageName: target?.beverageName ?? '',
    cookerTypeId: target ? String(target.cookerTypeId) : '-1',
    cookerName: target?.cookerName ?? '',
  });
  const response = await writeLocalApiJsonWithTimeout<{ ok: boolean; status?: string; error?: string | null }>(
    endpoint,
    apiToken,
    `/ui-pinning/target?${params.toString()}`,
    2200,
    signal,
  );
  if (!response.ok) {
    throw new Error(response.error || response.status || '游戏界面置顶目标更新失败。');
  }
}

export async function prepareNextRareOrder(
  endpoint: string,
  apiToken: string,
  item: OrderRecommendation,
  recipeTarget: RareAutomationRecipeTarget | null,
  beverageTarget: RareAutomationBeverageTarget | null,
  preferences: CompanionPreferences,
): Promise<OrderPreparationResponse> {
  return rareOrderAction(
    endpoint,
    apiToken,
    '/orders/prepare-next',
    item,
    recipeTarget,
    beverageTarget,
    preferences,
  );
}

export async function completeFirstRareOrder(
  endpoint: string,
  apiToken: string,
  item: OrderRecommendation,
  recipeTarget: RareAutomationRecipeTarget | null,
  beverageTarget: RareAutomationBeverageTarget | null,
  preferences: CompanionPreferences,
): Promise<OrderPreparationResponse> {
  return rareOrderAction(
    endpoint,
    apiToken,
    '/orders/complete-first',
    item,
    recipeTarget,
    beverageTarget,
    preferences,
  );
}

export async function completeFirstNormalOrder(
  endpoint: string,
  apiToken: string,
  order: NormalBusinessOrder,
  preferences: CompanionPreferences,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
  executionTarget: NormalOrderExecutionTarget | null = null,
): Promise<OrderPreparationResponse> {
  const indexes = buildRecommendationDataIndexes(data);
  const recipe = indexes.recipeByFoodId.get(order.foodId) ?? null;
  const targetRecipeId = executionTarget?.recipeId ?? recipe?.recipeId ?? -1;
  const targetFoodId = executionTarget?.foodId ?? order.foodId;
  const targetBeverageId = executionTarget?.beverageId ?? order.beverageId;
  const params = new URLSearchParams({
    traceId: order.traceId ?? '',
    orderKey: order.orderKey ?? '',
    deskCode: String(order.deskCode),
    guestName: order.guestName || '普客',
    specialBusinessRole: order.specialBusinessRole ?? '',
    matchFoodId: String(executionTarget?.matchFoodId ?? order.foodId),
    matchBeverageId: String(executionTarget?.matchBeverageId ?? order.beverageId),
    foodId: String(targetFoodId),
    recipeId: String(targetRecipeId),
    recipeName: executionTarget?.recipeName || order.foodName || recipe?.name || '',
    extraIngredientIds: executionTarget ? executionTarget.extraIngredientIds.join(',') : '',
    predictedFoodTags: executionTarget ? executionTarget.foodTags.join(',') : '',
    wackyTargetFoodTags: executionTarget ? (executionTarget.wackyTargetFoodTags ?? []).join(',') : '',
    executionMode: executionTarget?.executionMode ?? '',
    executionReason: executionTarget?.reason ?? '',
    beverageId: String(targetBeverageId),
    beverageName: executionTarget?.beverageName || order.beverageName || indexes.beverageNameById.get(order.beverageId) || '',
    autoTakeBeverage: String(preferences.autoNormalTakeBeverage),
    autoStartCooking: String(preferences.autoNormalStartCooking),
    autoCollectCooking: String(preferences.autoNormalDeliverFood),
    autoDeliverFood: String(preferences.autoNormalDeliverFood),
    autoCompleteOrder: String(preferences.autoNormalCompleteOrder),
    stopOnError: String(preferences.autoNormalStopOnError),
  });
  return writeLocalApiJsonWithTimeout<OrderPreparationResponse>(
    endpoint,
    apiToken,
    `/orders/normal/complete-first?${params.toString()}`,
    5000,
  );
}

export async function readFavorites(endpoint: string, apiToken: string, signal: AbortSignal): Promise<FavoriteData> {
  return readLocalApiJson<FavoriteData>(endpoint, apiToken, '/favorites', signal);
}

export async function readCustomRecipes(endpoint: string, apiToken: string, signal: AbortSignal): Promise<CustomRecipeData> {
  return readLocalApiJson<CustomRecipeData>(endpoint, apiToken, '/custom-recipes', signal);
}

export async function upsertCustomRecipe(
  endpoint: string,
  apiToken: string,
  input: CustomRecipeUpsertInput,
): Promise<CustomRecipeMutationResponse> {
  const params = new URLSearchParams({
    id: input.id ?? '',
    customerId: String(input.customerId),
    customerName: input.customerName,
    foodTag: input.foodTag ?? '',
    foodId: String(input.foodId),
    recipeId: String(input.recipeId),
    recipeName: input.recipeName,
    extraIngredientIds: input.extraIngredientIds.join(','),
  });
  if (input.enabled != null) params.set('enabled', String(input.enabled));
  if (input.pinToTop != null) params.set('pinToTop', String(input.pinToTop));
  if (input.sortOrder != null) params.set('sortOrder', String(input.sortOrder));
  return mutateCustomRecipe(endpoint, apiToken, `/custom-recipes/upsert?${params.toString()}`);
}

export async function removeCustomRecipe(
  endpoint: string,
  apiToken: string,
  id: string,
): Promise<CustomRecipeMutationResponse> {
  const params = new URLSearchParams({ id });
  return mutateCustomRecipe(endpoint, apiToken, `/custom-recipes/remove?${params.toString()}`);
}

export async function setCustomRecipesEnabled(
  endpoint: string,
  apiToken: string,
  enabled: boolean,
): Promise<CustomRecipeMutationResponse> {
  const params = new URLSearchParams({ enabled: String(enabled) });
  return mutateCustomRecipe(endpoint, apiToken, `/custom-recipes/settings?${params.toString()}`);
}

export async function updateCustomRecipeFlags(
  endpoint: string,
  apiToken: string,
  input: CustomRecipeFlagUpdateInput,
): Promise<CustomRecipeMutationResponse> {
  const params = new URLSearchParams({ scope: input.selection.scope });
  if (input.selection.scope === 'entry') params.set('id', input.selection.id);
  if (input.selection.scope === 'customer') params.set('customerId', String(input.selection.customerId));
  if (input.selection.scope === 'recipe') params.set('foodId', String(input.selection.foodId));
  if (input.enabled != null) params.set('enabled', String(input.enabled));
  if (input.pinToTop != null) params.set('pinToTop', String(input.pinToTop));
  return mutateCustomRecipe(endpoint, apiToken, `/custom-recipes/update-flags?${params.toString()}`);
}

export async function moveCustomRecipe(
  endpoint: string,
  apiToken: string,
  id: string,
  direction: 'up' | 'down',
): Promise<CustomRecipeMutationResponse> {
  const params = new URLSearchParams({ id, direction });
  return mutateCustomRecipe(endpoint, apiToken, `/custom-recipes/move?${params.toString()}`);
}

export async function addRecipeFavorite(
  endpoint: string,
  apiToken: string,
  customer: RareCustomerCatalogItem,
  foodTag: string,
  recipe: RareRecipeRecommendation,
): Promise<FavoriteMutationResponse> {
  const params = new URLSearchParams({
    customerId: String(customer.id),
    customerName: customer.name,
    foodTag,
    recipeId: String(recipe.recipe.id),
    extraIngredientIds: recipe.extraIngredients.map((ingredient) => ingredient.id).join(','),
  });
  return mutateFavorite(endpoint, apiToken, `/favorites/add-recipe?${params.toString()}`);
}

export async function removeRecipeFavorite(
  endpoint: string,
  apiToken: string,
  id: string,
): Promise<FavoriteMutationResponse> {
  const params = new URLSearchParams({ id });
  return mutateFavorite(endpoint, apiToken, `/favorites/remove-recipe?${params.toString()}`);
}

export async function addBeverageFavorite(
  endpoint: string,
  apiToken: string,
  customer: RareCustomerCatalogItem,
  beverageTag: string,
  beverage: RareBeverageRecommendation,
): Promise<FavoriteMutationResponse> {
  const params = new URLSearchParams({
    customerId: String(customer.id),
    customerName: customer.name,
    beverageTag,
    beverageId: String(beverage.beverage.id),
  });
  return mutateFavorite(endpoint, apiToken, `/favorites/add-beverage?${params.toString()}`);
}

export async function removeBeverageFavorite(
  endpoint: string,
  apiToken: string,
  id: string,
): Promise<FavoriteMutationResponse> {
  const params = new URLSearchParams({ id });
  return mutateFavorite(endpoint, apiToken, `/favorites/remove-beverage?${params.toString()}`);
}

function appendRareGuestInvitationLevels(params: URLSearchParams, levels: number[]) {
  const serialized = serializeRareGuestInvitationLevels(levels);
  if (serialized) params.set('levels', serialized);
}

async function mutateRareGuestInvitation(
  endpoint: string,
  apiToken: string,
  path: string,
): Promise<RareGuestInvitationResponse> {
  return writeLocalApiJsonWithTimeout<RareGuestInvitationResponse>(endpoint, apiToken, path, 5000);
}

async function rareOrderAction(
  endpoint: string,
  apiToken: string,
  path: string,
  item: OrderRecommendation,
  recipeTarget: RareAutomationRecipeTarget | null,
  beverageTarget: RareAutomationBeverageTarget | null,
  preferences: CompanionPreferences,
): Promise<OrderPreparationResponse> {
  // 订单自动化需要把本次推荐锁定的料理、加料和酒水传给 Mod，避免轮询刷新后前端列表变化影响正在执行的订单。
  const params = new URLSearchParams({
    traceId: item.order.traceId ?? '',
    deskCode: String(item.order.deskCode),
    guestId: item.order.guestId == null ? '' : String(item.order.guestId),
    guestName: item.order.guestName,
    specialBusinessRole: item.order.specialBusinessRole ?? '',
    foodTag: item.order.foodTag,
    beverageTag: item.order.beverageTag,
    foodId: recipeTarget ? String(recipeTarget.foodId) : '-1',
    recipeId: recipeTarget ? String(recipeTarget.recipeId) : '-1',
    recipeName: recipeTarget?.recipeName ?? '',
    extraIngredientIds: recipeTarget ? recipeTarget.extraIngredientIds.join(',') : '',
    predictedFoodTags: recipeTarget ? recipeTarget.foodTags.join(',') : '',
    executionReason: buildRareOrderExecutionReason(item, recipeTarget, beverageTarget),
    beverageId: beverageTarget ? String(beverageTarget.beverageId) : '-1',
    beverageName: beverageTarget?.beverageName ?? '',
    autoTakeBeverage: String(preferences.autoPrepTakeBeverage),
    autoStartCooking: String(preferences.autoPrepStartCooking),
    autoCollectCooking: String(preferences.autoPrepCollectCooking),
    autoDeliverFood: String(preferences.autoPrepCollectCooking),
    autoCompleteOrder: String(preferences.autoPrepCompleteOrder),
    recipeFavoritesOnly: String(preferences.autoPrepRecipeFavoritesOnly),
    beverageFavoritesOnly: String(preferences.autoPrepBeverageFavoritesOnly),
    stopOnError: String(preferences.autoPrepStopOnError),
    recipeFavorite: String(Boolean(recipeTarget?.favorite)),
    beverageFavorite: String(Boolean(beverageTarget?.favorite)),
  });
  return writeLocalApiJsonWithTimeout<OrderPreparationResponse>(
    endpoint,
    apiToken,
    `${path}?${params.toString()}`,
    5000,
  );
}

function buildRareOrderExecutionReason(
  item: OrderRecommendation,
  recipeTarget: RareAutomationRecipeTarget | null,
  beverageTarget: RareAutomationBeverageTarget | null,
): string {
  const planReason = findRareOrderExecutionPlanReason(item, recipeTarget, beverageTarget);
  const details = [
    item.order.specialBusinessRole ? `特殊经营角色 ${item.order.specialBusinessRole}` : '',
    planReason,
    recipeTarget?.foodTags.length ? `预测料理 Tag ${recipeTarget.foodTags.join('、')}` : '',
    beverageTarget ? `目标酒水 ${beverageTarget.beverageName || `#${beverageTarget.beverageId}`}` : '',
  ].filter(Boolean);
  return details.join('；');
}

function findRareOrderExecutionPlanReason(
  item: OrderRecommendation,
  recipeTarget: RareAutomationRecipeTarget | null,
  beverageTarget: RareAutomationBeverageTarget | null,
): string {
  const matchedPlan = item.executionPlans.find((plan) => rareOrderPlanMatchesTargets(plan, recipeTarget, beverageTarget))
    ?? (rareOrderPlanMatchesTargets(item.preparationPlan, recipeTarget, beverageTarget) ? item.preparationPlan : null);
  return matchedPlan?.reasons[0] ?? '';
}

function rareOrderPlanMatchesTargets(
  plan: RareOrderRecommendationPlan | null,
  recipeTarget: RareAutomationRecipeTarget | null,
  beverageTarget: RareAutomationBeverageTarget | null,
): boolean {
  if (!plan) return false;
  if (recipeTarget && (
    !plan.food
    || plan.food.recipe.id !== recipeTarget.foodId
    || plan.food.recipe.recipeId !== recipeTarget.recipeId
    || !sameNumberList(
      plan.food.extraIngredients.map((ingredient) => ingredient.id),
      recipeTarget.extraIngredientIds,
    )
  )) return false;
  if (beverageTarget && (!plan.beverage || plan.beverage.beverage.id !== beverageTarget.beverageId)) return false;
  return true;
}

function sameNumberList(left: readonly number[], right: readonly number[]): boolean {
  if (left.length !== right.length) return false;
  const normalizedLeft = [...left].sort((a, b) => a - b);
  const normalizedRight = [...right].sort((a, b) => a - b);
  return normalizedLeft.every((value, index) => value === normalizedRight[index]);
}

async function mutateFavorite(
  endpoint: string,
  apiToken: string,
  path: string,
): Promise<FavoriteMutationResponse> {
  return writeLocalApiJsonWithTimeout<FavoriteMutationResponse>(endpoint, apiToken, path, 3200);
}

async function mutateCustomRecipe(
  endpoint: string,
  apiToken: string,
  path: string,
): Promise<CustomRecipeMutationResponse> {
  return writeLocalApiJsonWithTimeout<CustomRecipeMutationResponse>(endpoint, apiToken, path, 3200);
}
