import type { RuntimeDataCatalogSnapshot } from '@/lib/recommendation-data';
import type {
  RareCustomerCatalogItem,
} from '@/lib/catalog-types';
import type {
  RareBeverageRecommendation,
  RareOrderRecommendationPlan,
  RareRecipeRecommendation,
  RecommendationBudgetResult,
} from '@/recommendation-engine';

/**
 * 工作台一级 Tab。值会持久化到 localStorage，并用于手柄导航定位。
 */
export type ModTab = 'overview' | 'normal' | 'rare' | 'custom-recipes' | 'service' | 'tasks' | 'inventory' | 'help' | 'logs' | 'settings';
export type OverviewTab = 'status' | 'inventory' | 'actions';
export type SettingsTab = 'window' | 'connection' | 'recommendation' | 'automation' | 'updates' | 'debug';
export type RareGuestInvitationScope = 'current' | 'all';
export type MissionStatusFilter = 'available' | 'tracking' | 'fulfilled';

/**
 * Mod 发布给前端的推荐基础状态快照。
 *
 * 该结构来自 C# 的 RecommendationStateSnapshot，字段使用数组和普通对象，前端再转换为 Set/Map 供推荐引擎使用。
 */
export interface RecommendationStateSnapshot {
  availableRecipeIds: number[];
  availableBeverageIds: number[];
  availableIngredientIds: number[];
  ownedIngredientQty: Record<string, number>;
  ownedBeverageQty: Record<string, number>;
  placedCookerTypeIds?: number[];
  placedCookers?: PlacedCookerSnapshot[];
  placedCookerStatus?: string;
  popularFoodTag: string | null;
  popularHateFoodTag: string | null;
  famousShopEnabled: boolean;
}

/**
 * 当前夜间经营场景中已摆放厨具的运行时快照。
 */
export interface PlacedCookerSnapshot {
  controllerIndex: number;
  typeIds: number[];
  typeNames: string[];
  name: string;
  isOpen: boolean;
  source: string;
}

/**
 * 夜间经营中的稀客或映射稀客信息。
 */
export interface NightBusinessGuest {
  deskCode: number;
  guestId: number | null;
  guestName: string;
  source: string;
  fund?: number | null;
  baseFundCarry?: number | null;
  maxFundCarry?: number | null;
  extraFundByBuff?: number | null;
  willPayMoney?: boolean | null;
}

/**
 * 夜间经营稀客订单快照。
 */
export interface NightBusinessOrder {
  deskCode: number;
  guestId: number | null;
  guestName: string;
  foodTagId: number;
  foodTag: string;
  beverageTagId: number;
  beverageTag: string;
  source: string;
  firstSeenAtUtc?: string | null;
  lastSeenAtUtc?: string | null;
  isFreeOrder?: boolean;
  hasServedFood?: boolean;
  hasServedBeverage?: boolean;
}

/**
 * 夜间经营上下文，是稀客订单页和服务自动化的主要输入。
 */
export interface NightBusinessContext {
  place: string | null;
  placeLabel: string | null;
  activeRareGuests: NightBusinessGuest[];
  orders: NightBusinessOrder[];
  source: string;
  error: string | null;
}

/**
 * 游戏运行时任务信息。
 */
export interface RuntimeMissionInfo {
  label: string;
  title: string;
  characterLabel: string;
  characterName: string;
  places?: string[];
  source: string;
  status?: MissionStatusFilter | 'finished';
  started: boolean;
  finished: boolean;
  targetRecipeId?: number | null;
  targetRecipeName?: string | null;
}

/**
 * 可直接关联到稀客/料理推荐的任务上菜目标。
 */
export interface RuntimeMissionServeTarget {
  guestId: number;
  guestName: string;
  guestLabel: string;
  missionLabel: string;
  missionTitle: string;
  recipeId: number;
  recipeName: string;
  status: MissionStatusFilter | 'finished';
  source: string;
}

/**
 * 任务面板运行时快照。
 */
export interface RuntimeMissionContext {
  availableMissions: RuntimeMissionInfo[];
  serveTargets?: RuntimeMissionServeTarget[];
  source: string;
  error: string | null;
}

/**
 * 夜间经营中的普客订单快照。
 */
export interface NormalBusinessOrder {
  orderKey?: string;
  deskCode: number;
  guestName: string;
  foodId: number;
  foodName: string;
  beverageId: number;
  beverageName: string;
  hasServedFood: boolean;
  hasServedBeverage: boolean;
  readyToEvaluate: boolean;
  hasEvaluated: boolean;
  controllerAvailable?: boolean;
  canAutomate?: boolean;
  actionBlockReason?: string;
  firstSeenAtUtc?: string | null;
  source: string;
}

/**
 * 普客订单上下文，是普客服务页和普客自动化的主要输入。
 */
export interface NormalBusinessContext {
  orders: NormalBusinessOrder[];
  source: string;
  error: string | null;
}

/**
 * 从游戏运行时映射出的稀客目录项。
 */
export interface RuntimeRareCustomer {
  id: number;
  runtimeStringId: string;
  name: string;
  places: string[];
  positiveTags: string[];
  negativeTags: string[];
  beverageTags: string[];
  source: string;
}

/**
 * 本地 API `/snapshot` 返回的完整快照。
 *
 * 快照是前端唯一的实时数据入口；体积较大的 runtimeData 可能按签名和时间间隔省略。
 */
export interface LocalApiSnapshot {
  pluginVersion: string;
  capturedAtUtc: string;
  activeSceneName: string;
  activeDayMapLabel?: string;
  activeDayMapName?: string;
  runtimeLoaded: boolean;
  status: string;
  runtimeSource: string;
  runtimeSceneReadinessStatus?: string;
  runtimeUiPinningStatus?: string;
  recommendationState: RecommendationStateSnapshot | null;
  nightBusiness: NightBusinessContext | null;
  runtimeMissions?: RuntimeMissionContext | null;
  normalBusiness?: NormalBusinessContext | null;
  runtimeRareCustomers?: RuntimeRareCustomer[];
  runtimeData?: RuntimeDataCatalogSnapshot;
  performanceMs?: Record<string, number>;
}

/**
 * 前端从推荐状态快照归一化出的集合结构。
 */
export interface RuntimeSets {
  recipeIds: Set<number>;
  beverageIds: Set<number>;
  ingredientIds: Set<number>;
  unavailableIngredientIds: Set<number>;
  ownedIngredientQty: Record<number, number>;
  ownedBeverageQty: Record<number, number>;
  placedCookerTypeIds: Set<number>;
  placedCookerNames: Set<string>;
  hasCookerSnapshot: boolean;
}

/**
 * 已按稀客需求计算好的推荐结果缓存。
 */
export interface CachedRecommendation {
  customer: RareCustomerCatalogItem;
  preparationPlan: RareOrderRecommendationPlan | null;
  executionPlans: RareOrderRecommendationPlan[];
  budget: RecommendationBudgetResult | null;
  blockedMessages: string[];
  recipes: RareRecipeRecommendation[];
  beverages: RareBeverageRecommendation[];
}

/**
 * 带运行时订单的稀客推荐行。
 */
export interface OrderRecommendation extends CachedRecommendation {
  order: NightBusinessOrder;
}

export interface RecommendationIssue {
  order: NightBusinessOrder;
  message: string;
}

export interface LocalApiLogs {
  capturedAtUtc: string;
  path: string;
  exists: boolean;
  enabled: boolean;
  maxLines?: number;
  maxBytes?: number;
  lines: string[];
  error: string | null;
}

export interface LocalApiLogSettings {
  logAccessEnabled: boolean;
  logOutputPath: string;
  logOutputDirectory: string;
  maxLogLines?: number;
  maxLogBytes?: number;
  nightBusinessDiagnosticsEnabled: boolean;
  nightBusinessDiagnosticsPath: string;
  nightBusinessDiagnosticsDirectory: string;
  aggregateModLogEnabled: boolean;
  aggregateModLogPath: string;
  aggregateModLogDirectory: string;
  aggregateModLogMaxFileBytes: number;
  nativeBepInExConsoleEnabled: boolean;
  nativeBepInExConsoleVisible: boolean;
}

export interface LocalApiConnectionConfig {
  ok: boolean;
  localEndpoint: string;
  lanEnabled: boolean;
  lanRunning: boolean;
  lanBindHost: string;
  port: number;
  token: string;
  lanBindAddresses: string[];
  lanEndpoints: string[];
  lanError: string | null;
  error: string | null;
}

export interface LocalApiHealth {
  ok: boolean;
  pluginVersion: string;
  bindAddress: string;
  port: number;
  authRequired: boolean;
  localEndpoint: string;
  lanEnabled: boolean;
  lanRunning: boolean;
  lanBindAddresses: string[];
  lanEndpoints: string[];
  lanError: string | null;
}

export interface LocalApiAutomationLease {
  ok: boolean;
  owned: boolean;
  clientId: string;
  clientLabel: string;
  ownerClientId: string;
  ownerLabel: string;
  ownerLastSeenUtc: string;
  expiresAtUtc: string;
  ttlMs: number;
  error: string | null;
}

export interface LocalApiFolderResponse {
  ok: boolean;
  directory: string;
  error: string | null;
}

export interface DiagnosticPackageResponse {
  ok: boolean;
  path: string;
  directory: string;
  files: string[];
  error: string | null;
}

export interface UpdateStatusResponse {
  ok: boolean;
  currentVersion: string;
  enabled: boolean;
  autoCheck: boolean;
  includePrerelease: boolean;
  state: string;
  latestVersion: string;
  latestTag: string;
  hasUpdate: boolean;
  checkedAtUtc: string;
  publishedAtUtc: string;
  releaseUrl: string;
  packageAsset: string;
  packageSize: number;
  downloadedVersion: string;
  downloadedAtUtc: string;
  staged: boolean;
  installState: string;
  installMessage: string;
  error: string | null;
}

export interface InventoryEditResponse {
  ok: boolean;
  type: 'ingredient' | 'beverage';
  id: number;
  requestedQuantity: number;
  previousQuantity: number;
  quantity: number;
  changed: boolean;
  error: string | null;
}

export interface InventoryBulkEditResponse {
  ok: boolean;
  type: 'ingredient' | 'beverage';
  requestedQuantity: number;
  total: number;
  changed: number;
  unchanged: number;
  failed: number;
  errors: string[];
  error: string | null;
}

export interface FavoriteData {
  version: number;
  recipes: FavoriteRecipeEntry[];
  beverages: FavoriteBeverageEntry[];
}

export interface FavoriteRecipeEntry {
  id: string;
  customerId: number;
  customerName: string;
  foodTag: string;
  recipeId: number;
  extraIngredientIds: number[];
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface FavoriteBeverageEntry {
  id: string;
  customerId: number;
  customerName: string;
  beverageTag: string;
  beverageId: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface FavoriteMutationResponse {
  ok: boolean;
  favorites: FavoriteData;
  error: string | null;
}

export interface CustomRecipeData {
  version: number;
  recipes: CustomRecipeEntry[];
}

export interface CustomRecipeEntry {
  id: string;
  customerId: number;
  customerName: string;
  foodTag: string | null;
  foodId: number;
  recipeId: number;
  recipeName: string;
  extraIngredientIds: number[];
  enabled: boolean;
  pinToTop: boolean;
  sortOrder: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface CustomRecipeUpsertInput {
  id?: string;
  customerId: number;
  customerName: string;
  foodTag: string | null;
  foodId: number;
  recipeId: number;
  recipeName: string;
  extraIngredientIds: number[];
  enabled: boolean;
  pinToTop: boolean;
  sortOrder?: number;
}

export interface CustomRecipeMutationResponse {
  ok: boolean;
  customRecipes: CustomRecipeData;
  error: string | null;
}

export interface RareGuestInvitationEntry {
  id: number;
  name: string;
  runtimeName: string;
  reason: string;
  status?: string;
  canInvite?: boolean;
  isCurrentScene?: boolean;
  kizunaLevel?: number;
  sceneLabels?: string[];
  sceneNames?: string[];
}

export interface RareGuestInvitationResponse {
  ok: boolean;
  runtimeAvailable: boolean;
  status: string;
  error: string | null;
  candidateCount: number;
  usableCount: number;
  existingSlotCount: number;
  existingControlledCount: number;
  scheduledSlotCount: number;
  invitedCount: number;
  skippedCount: number;
  source?: string;
  diagnostics?: string;
  scope?: RareGuestInvitationScope;
  currentMapLabel?: string;
  currentMapName?: string;
  candidates?: RareGuestInvitationEntry[];
  available: RareGuestInvitationEntry[];
  existingInvited: RareGuestInvitationEntry[];
  invited: RareGuestInvitationEntry[];
  skipped: RareGuestInvitationEntry[];
}

export interface RareOrderDismissResponse {
  ok: boolean;
  removed: number;
  status: string;
  error: string | null;
}

export interface GameUiPinningTarget {
  signature: string;
  recipeId: number;
  recipeName: string;
  ingredientIds: number[];
  beverageId: number;
  beverageName: string;
  cookerTypeId: number;
  cookerName: string;
}

export interface RareAutoOrderDiagnostic {
  orderKey: string;
  title: string;
  foodTag: string;
  beverageTag: string;
  recipeName: string;
  beverageName: string;
  stepLabel: string;
  stepSeconds: number;
  nextAction: string;
  retryCount: number;
  rollbackCount: number;
  lastError: string;
  prepared: boolean;
  beverageHandled: boolean;
  hasServedFood: boolean;
  hasServedBeverage: boolean;
  paused: boolean;
}

export interface NormalAutoOrderDiagnostic {
  orderKey: string;
  title: string;
  foodName: string;
  beverageName: string;
  source: string;
  stepLabel: string;
  stepSeconds: number;
  nextAction: string;
  retryCount: number;
  rollbackCount: number;
  lastError: string;
  prepared: boolean;
  beverageHandled: boolean;
  collected: boolean;
  foodDelivered: boolean;
  completed: boolean;
  paused: boolean;
  hasServedFood: boolean;
  hasServedBeverage: boolean;
  readyToEvaluate: boolean;
  hasEvaluated: boolean;
  controllerAvailable?: boolean;
  canAutomate?: boolean;
  actionBlockReason?: string;
}

export interface AutomationCookerCycle {
  bucket: number;
  used: Map<string, number>;
  labels: Map<string, string[]>;
}

export interface CookerRequirement {
  key: string;
  label: string;
}

export interface CookerReservationResult {
  ok: boolean;
  message: string;
}

export interface NormalCookerDemand {
  counts: Map<string, number>;
  labels: Map<string, string[]>;
}

export interface AutomationCookerResourceRow {
  key: string;
  label: string;
  capacity: number;
  normalReserved: number;
  rareReserved: number;
  labels: string[];
}

export interface AutomationResourceOverview {
  cookers: AutomationCookerResourceRow[];
}

export type ToggleRecipeFavorite = (customer: RareCustomerCatalogItem, foodTag: string, recipe: RareRecipeRecommendation) => Promise<void>;
export type ToggleBeverageFavorite = (customer: RareCustomerCatalogItem, beverageTag: string, beverage: RareBeverageRecommendation) => Promise<void>;

export interface AutomationLogEntry {
  raw: string;
  timestamp: string;
  action: string;
  target: string;
  desk: string;
  orderKey: string;
  food: string;
  guest: string;
  message: string;
}
