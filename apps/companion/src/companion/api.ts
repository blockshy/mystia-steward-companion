/** 客户端仅保留协议、表单和展示辅助；业务推荐与自动化决策由 C# 服务计算。 */
import { readLocalApiJson, writeLocalApiJsonWithTimeout } from '@/companion/local-api';
import type { SharedCompanionPreferences } from '@/companion/preferences';
import { SHARED_COMPANION_PREFERENCES_SCHEMA_VERSION, normalizeEditableQuantity } from '@/companion/preferences';
import { serializeRareGuestInvitationLevels } from '@/companion/storage';
import type {
  DiagnosticPackageResponse,
  AutomationSafetyBarrierAckResponse,
  AvailableMissionsApiResponse,
  BepInExConsoleVisibilityResponse,
  CustomRecipeData,
  CustomRecipeFlagUpdateInput,
  CustomRecipeMutationResponse,
  CustomRecipeUpsertInput,
  CompanionDeviceAuthorityState,
  CompanionDevicePlatform,
  FavoriteData,
  FavoriteMutationResponse,
  InventoryBulkEditResponse,
  InventoryEditResponse,
  LocalApiAutomationLease,
  LocalApiConnectionConfig,
  LocalApiFolderResponse,
  LocalApiLogSettings,
  LocalApiSnapshotResponse,
  NightBusinessOrder,
  RareGuestInvitationResponse,
  RareGuestInvitationScope,
  RareGuestInvitationWriteContext,
  RareOrderDismissResponse,
  TrackedMissionsApiResponse,
  UpdateStatusResponse,
} from '@/companion/types';
import { type RuntimeDataCatalogSnapshot } from '@/lib/recommendation-data';
import type { RareCustomerCatalogItem } from '@/lib/catalog-types';
import type { RareBeverageRecommendation, RareRecipeRecommendation } from '@/recommendation-engine';

const COMPANION_DEVICE_PROTOCOL_VERSION = 1;

export async function registerCompanionDevice(
  endpoint: string,
  apiToken: string,
  platform: CompanionDevicePlatform,
  appVersion: string,
  profile: SharedCompanionPreferences,
): Promise<CompanionDeviceAuthorityState> {
  return writeLocalApiJsonWithTimeout<CompanionDeviceAuthorityState>(
    endpoint,
    apiToken,
    '/devices/register',
    3200,
    {
      body: {
        protocolVersion: COMPANION_DEVICE_PROTOCOL_VERSION,
        profileSchemaVersion: SHARED_COMPANION_PREFERENCES_SCHEMA_VERSION,
        platform,
        appVersion,
        profile,
      },
    },
  );
}

export async function readCompanionDevices(
  endpoint: string,
  apiToken: string,
  signal?: AbortSignal,
): Promise<CompanionDeviceAuthorityState> {
  return readLocalApiJson<CompanionDeviceAuthorityState>(endpoint, apiToken, '/devices', signal);
}

export async function updatePrimaryCompanionProfile(
  endpoint: string,
  apiToken: string,
  state: CompanionDeviceAuthorityState,
  profile: SharedCompanionPreferences,
): Promise<CompanionDeviceAuthorityState> {
  return writeLocalApiJsonWithTimeout<CompanionDeviceAuthorityState>(
    endpoint,
    apiToken,
    '/devices/profile',
    3200,
    {
      body: {
        protocolVersion: COMPANION_DEVICE_PROTOCOL_VERSION,
        profileSchemaVersion: SHARED_COMPANION_PREFERENCES_SCHEMA_VERSION,
        expectedAuthorityRevision: state.authorityRevision,
        expectedProfileRevision: state.currentDeviceProfileRevision,
        profile,
      },
    },
  );
}

export async function setPrimaryCompanionDevice(
  endpoint: string,
  apiToken: string,
  authorityRevision: number,
  deviceId: string,
): Promise<CompanionDeviceAuthorityState> {
  return writeDeviceAuthorityMutation(endpoint, apiToken, '/devices/primary', {
    protocolVersion: COMPANION_DEVICE_PROTOCOL_VERSION,
    expectedAuthorityRevision: authorityRevision,
    deviceId,
  });
}

export async function syncCompanionDeviceProfile(
  endpoint: string,
  apiToken: string,
  authorityRevision: number,
  deviceId: string,
): Promise<CompanionDeviceAuthorityState> {
  return writeDeviceAuthorityMutation(endpoint, apiToken, '/devices/sync', {
    protocolVersion: COMPANION_DEVICE_PROTOCOL_VERSION,
    expectedAuthorityRevision: authorityRevision,
    deviceId,
  });
}

export async function acknowledgeCompanionDeviceSync(
  endpoint: string,
  apiToken: string,
  syncId: string,
  profileRevision: number,
  profileHash: string,
): Promise<CompanionDeviceAuthorityState> {
  return writeDeviceAuthorityMutation(endpoint, apiToken, '/devices/sync-ack', {
    protocolVersion: COMPANION_DEVICE_PROTOCOL_VERSION,
    syncId,
    profileRevision,
    profileHash,
  });
}

export async function renameCompanionDevice(
  endpoint: string,
  apiToken: string,
  label: string,
): Promise<CompanionDeviceAuthorityState> {
  return writeDeviceAuthorityMutation(endpoint, apiToken, '/devices/rename', {
    protocolVersion: COMPANION_DEVICE_PROTOCOL_VERSION,
    label,
  });
}

export async function forgetCompanionDevice(
  endpoint: string,
  apiToken: string,
  authorityRevision: number,
  deviceId: string,
): Promise<CompanionDeviceAuthorityState> {
  return writeDeviceAuthorityMutation(endpoint, apiToken, '/devices/forget', {
    protocolVersion: COMPANION_DEVICE_PROTOCOL_VERSION,
    expectedAuthorityRevision: authorityRevision,
    deviceId,
  });
}

async function writeDeviceAuthorityMutation(
  endpoint: string,
  apiToken: string,
  path: string,
  body: Record<string, unknown>,
): Promise<CompanionDeviceAuthorityState> {
  return writeLocalApiJsonWithTimeout<CompanionDeviceAuthorityState>(endpoint, apiToken, path, 3200, { body });
}

/**
 * 伴随窗口访问 Mod 本地 API 的类型化门面。
 *
 * 该文件只负责把 界面输入转换为本地 API 协议参数，不直接保存状态。
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

export async function setBepInExConsoleVisibility(
  endpoint: string,
  apiToken: string,
  visible: boolean,
  signal: AbortSignal,
): Promise<BepInExConsoleVisibilityResponse> {
  return writeLocalApiJsonWithTimeout<BepInExConsoleVisibilityResponse>(
    endpoint,
    apiToken,
    `/logs/console?visible=${String(visible)}`,
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
  authorityRevision: number,
): Promise<LocalApiAutomationLease> {
  return readLocalApiJson<LocalApiAutomationLease>(endpoint, apiToken, '/automation/lease', {
    signal,
    authorityRevision,
  });
}

export async function acquireAutomationLease(
  endpoint: string,
  apiToken: string,
  authorityRevision: number,
): Promise<LocalApiAutomationLease> {
  return writeLocalApiJsonWithTimeout<LocalApiAutomationLease>(
    endpoint,
    apiToken,
    '/automation/lease/acquire',
    2200,
    { authorityRevision },
  );
}

export async function releaseAutomationLease(
  endpoint: string,
  apiToken: string,
  authorityRevision: number,
): Promise<LocalApiAutomationLease> {
  return writeLocalApiJsonWithTimeout<LocalApiAutomationLease>(
    endpoint,
    apiToken,
    '/automation/lease/release',
    2800,
    { authorityRevision },
  );
}

export async function acknowledgeAutomationSafetyBarrier(
  endpoint: string,
  apiToken: string,
  sequence: number,
  authorityRevision: number,
): Promise<AutomationSafetyBarrierAckResponse> {
  const params = new URLSearchParams({ sequence: String(sequence) });
  return writeLocalApiJsonWithTimeout<AutomationSafetyBarrierAckResponse>(
    endpoint,
    apiToken,
    `/automation/barriers/ack?${params.toString()}`,
    2800,
    { authorityRevision },
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
  context: RareGuestInvitationWriteContext,
): Promise<RareGuestInvitationResponse> {
  const params = new URLSearchParams({
    scope,
    expectedDaySceneGeneration: String(context.expectedDaySceneGeneration),
    expectedMapLabel: context.expectedMapLabel,
  });
  appendRareGuestInvitationLevels(params, levels);
  return mutateRareGuestInvitation(endpoint, apiToken, `/rare-guests/invite-all?${params.toString()}`);
}

export async function fetchAvailableRareGuestInvitations(
  endpoint: string,
  apiToken: string,
  scope: RareGuestInvitationScope,
  signal: AbortSignal,
): Promise<RareGuestInvitationResponse> {
  const params = new URLSearchParams({ scope });
  return readLocalApiJson<RareGuestInvitationResponse>(
    endpoint,
    apiToken,
    `/rare-guests/invitations?${params.toString()}`,
    {
      signal,
      tauriTimeoutMs: 5000,
    },
  );
}

export async function readTrackedMissions(
  endpoint: string,
  apiToken: string,
  options: { signal: AbortSignal; timeoutMs: number; knownSignature?: string },
): Promise<TrackedMissionsApiResponse> {
  const params = new URLSearchParams();
  if (options.knownSignature) params.set('knownSignature', options.knownSignature);
  const path = params.size > 0
    ? `/missions/tracked?${params.toString()}`
    : '/missions/tracked';
  return readLocalApiJson<TrackedMissionsApiResponse>(endpoint, apiToken, path, {
    signal: options.signal,
    tauriTimeoutMs: options.timeoutMs,
  });
}

export async function readAvailableMissions(
  endpoint: string,
  apiToken: string,
  options: { signal: AbortSignal; timeoutMs: number; knownSignature?: string },
): Promise<AvailableMissionsApiResponse> {
  const params = new URLSearchParams();
  if (options.knownSignature) params.set('knownSignature', options.knownSignature);
  const path = params.size > 0
    ? `/missions/available?${params.toString()}`
    : '/missions/available';
  return readLocalApiJson<AvailableMissionsApiResponse>(endpoint, apiToken, path, {
    signal: options.signal,
    tauriTimeoutMs: options.timeoutMs,
  });
}

export async function inviteAvailableRareGuest(
  endpoint: string,
  apiToken: string,
  guestId: number,
  scope: RareGuestInvitationScope,
  context: RareGuestInvitationWriteContext,
): Promise<RareGuestInvitationResponse> {
  const params = new URLSearchParams({
    guestId: String(guestId),
    scope,
    expectedDaySceneGeneration: String(context.expectedDaySceneGeneration),
    expectedMapLabel: context.expectedMapLabel,
  });
  return mutateRareGuestInvitation(endpoint, apiToken, `/rare-guests/invite?${params.toString()}`);
}

export async function dismissRuntimeRareOrder(
  endpoint: string,
  apiToken: string,
  order: NightBusinessOrder,
): Promise<RareOrderDismissResponse> {
  const params = new URLSearchParams({
    deskCode: String(order.deskCode),
  });
  if (order.runtimeGuestId != null) params.set('runtimeGuestId', String(order.runtimeGuestId));
  if (order.foodTagId != null) params.set('foodTagId', String(order.foodTagId));
  if (order.beverageTagId != null) params.set('beverageTagId', String(order.beverageTagId));

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
