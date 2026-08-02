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
export type ModTab = 'overview' | 'normal' | 'rare' | 'custom-recipes' | 'service' | 'missions' | 'inventory' | 'help' | 'logs' | 'settings';
export type OverviewTab = 'status' | 'inventory' | 'actions';
export type SettingsTab = 'window' | 'connection' | 'recommendation' | 'experimental' | 'updates';
export type RareGuestInvitationScope = 'current' | 'all';
export type MissionPanelView = 'tasks' | 'invitations';
export type TrackedMissionStatus = 'unverified' | 'tracking' | 'fulfilled';
export type TrackedMissionRuntimeStatus =
  | 'not-attached'
  | 'waiting-for-load'
  | 'loading'
  | 'ready'
  | 'runtime-unavailable'
  | 'mission-data-incomplete';

export interface RareGuestInvitationWriteContext {
  expectedDaySceneGeneration: number;
  expectedMapLabel: string;
}

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
  placedCookerTypeIds: number[];
  placedCookers: PlacedCookerSnapshot[];
  placedCookerSnapshotComplete: boolean;
  placedCookerControllerCount: number;
  placedCookerEmptyControllerCount: number;
  placedCookerLockedControllerCount: number;
  placedCookerReadFailureCount: number;
  placedCookerStatus: string;
  popularFoodTag: string | null;
  popularHateFoodTag: string | null;
  famousShopEnabled: boolean;
}

/**
 * 当前夜间经营场景中已摆放厨具的运行时快照。
 *
 * typeIds/typeNames 描述物理厨具类型；couldOpen 是游戏 getter 事实，
 * automationAvailable 是经过完整运行时分类后的瞬时自动化容量事实。
 */
export interface PlacedCookerSnapshot {
  controllerIndex: number;
  gridPosition: CookerGridPosition;
  controllerIdentity: string;
  typeIds: number[];
  typeNames: string[];
  name: string;
  challengeLocked: boolean;
  couldOpen: boolean;
  automationAvailable: boolean;
  automationAvailability: 'StrictIdle' | 'ExtractedResidual' | 'Unavailable';
  automationAvailabilityDiagnostic: string;
  source: string;
}

export interface CookerGridPosition {
  x: number;
  y: number;
  z: number;
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

export interface MissionRecipePriority {
  traceId: string;
  deskCode: number;
  guestId: number;
  runtimeGuestId: number;
  foodId: number;
  recipeId: number;
  missionGeneration: number;
  businessGeneration: number;
}

/**
 * 夜间经营稀客订单快照。
 */
export interface NightBusinessOrder {
  traceId?: string;
  deskCode: number;
  guestId: number | null;
  runtimeGuestId: number | null;
  guestName: string;
  specialBusinessRole?: string;
  specialBusinessRoleLabel?: string;
  automationAllowed?: boolean;
  automationBlockReason?: string;
  foodTagId: number | null;
  foodTag: string;
  beverageTagId: number | null;
  beverageTag: string;
  source: string;
  firstSeenAtUtc?: string | null;
  lastSeenAtUtc?: string | null;
  isFreeOrder?: boolean;
  fund?: number | null;
  baseFundCarry?: number | null;
  maxFundCarry?: number | null;
  extraFundByBuff?: number | null;
  willPayMoney?: boolean | null;
  remainingOrderCount?: number | null;
  hasServedFood?: boolean;
  hasServedBeverage?: boolean;
  missionRecipePriority?: MissionRecipePriority | null;
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
 * 特殊经营挑战上下文。该快照用于提示，并把明确的目标 Tag 或等级评分规则注入推荐排序。
 */
export interface SpecialBusinessContext {
  active: boolean;
  challengeTypeAvailable: boolean;
  challengeType: string;
  displayName: string;
  category: string;
  ruleSummary: string;
  foodTargetTags: string[];
  beverageTargetTags: string[];
  yuumaFoodTargetRevision: number;
  targetFund?: number | null;
  targetLabel?: string;
  phase?: string;
  currentValue?: number | null;
  maxValue?: number | null;
  targetValue?: number | null;
  currentAnger: number | null;
  maxAnger: number | null;
  targetAnger: number | null;
  targetTimeProgress?: number | null;
  targetTagTimeProgress?: number | null;
  wackyKoishiShieldBroken?: boolean | null;
  wackyKoishiFoodPreferenceTags?: string[];
  wackyKoishiFoodHateTags?: string[];
  wackyKoishiBeveragePreferenceTags?: string[];
  currentSpellCount?: number | null;
  targetSpellCount?: number | null;
  recommendationPolicy: string;
  automationPolicy: string;
  source: string;
  error: string | null;
  lastTargetUpdatedUtc?: string | null;
}

/**
 * 夜间经营中的普客订单快照。
 */
export interface NormalBusinessOrder {
  traceId?: string;
  orderKey?: string;
  deskCode: number;
  guestId?: number | null;
  runtimeGuestId: number | null;
  guestName: string;
  specialBusinessRole?: string;
  specialBusinessRoleLabel?: string;
  foodPreferenceTags?: string[];
  beveragePreferenceTags?: string[];
  fund?: number | null;
  baseFundCarry?: number | null;
  maxFundCarry?: number | null;
  extraFundByBuff?: number | null;
  willPayMoney?: boolean | null;
  remainingOrderCount?: number | null;
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

export type NormalOrderExecutionMode = 'progress' | 'refresh';
export type SpecialTargetMatchMode = 'any' | 'all';

export interface SpecialFoodTargetWirePolicy {
  specialTargetChallenge: string;
  specialTargetOwner: string;
  specialTargetGeneration: number;
  specialTargetRevision: number;
  specialTargetFoodTags: string[];
  specialTargetMatchMode: SpecialTargetMatchMode | '';
  specialTargetSignature: string;
}

export interface NormalOrderExecutionTarget extends SpecialFoodTargetWirePolicy {
  matchFoodId: number;
  matchBeverageId: number;
  foodId: number;
  recipeId: number;
  executionMode?: NormalOrderExecutionMode;
  allowYuumaControlledProgression: boolean;
  recipeName: string;
  extraIngredientIds: number[];
  beverageId: number;
  beverageName: string;
  cookerName: string;
  foodTags: string[];
  expectedFoodModifierTags: string[];
  beverageTags: string[];
  reason: string;
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
 * 本地 API `/snapshot` 返回的轻量实时快照。
 *
 * 快照是前端的实时状态入口；体积较大的运行时目录通过 `/runtime-data` 按签名单独读取。
 */
export interface LocalApiSnapshot {
  pluginVersion: string;
  automationSessionId: string;
  automationCancellationAppliedEpoch: number;
  nightBusinessGeneration: number;
  nightBusinessLifecyclePhase: 'Inactive' | 'Active' | 'Closing' | 'Destroyed';
  runtimeNightBusinessLifecycleStatus?: string;
  nightBusinessAutomationAllowed: boolean;
  nightBusinessAutomationBlockReason: string;
  runtimeNightBusinessAutomationStatus: string;
  snapshotSignature?: string;
  capturedAtUtc: string;
  activeSceneName: string;
  activeDayMapLabel?: string;
  activeDayMapName?: string;
  runtimeLoaded: boolean;
  runtimeDaySceneGeneration: number;
  runtimeDaySceneReady: boolean;
  missionGeneration: number;
  status: string;
  runtimeSource: string;
  runtimeSceneReadinessStatus?: string;
  runtimeUiPinningStatus?: string;
  recommendationState: RecommendationStateSnapshot | null;
  nightBusiness: NightBusinessContext | null;
  specialBusiness?: SpecialBusinessContext | null;
  normalBusiness?: NormalBusinessContext | null;
  automationEvents?: AutomationRuntimeEvent[];
  automationCookingJobs: AutomationCookingJobSnapshot[];
  runtimeDataComplete?: boolean;
  runtimeDataSource?: string;
  runtimeDataStatus?: string;
  runtimeDataSignature?: string;
  performanceMs?: Record<string, number>;
}

export interface LocalApiSnapshotUnchanged {
  unchanged: true;
  snapshotSignature: string;
}

export type LocalApiSnapshotResponse = LocalApiSnapshot | LocalApiSnapshotUnchanged;

export interface AutomationRuntimeEvent {
  sequence: number;
  createdAtUtc: string;
  code: 'cooking-mismatch-stored' | 'cooking-tags-unreadable-stored' | 'food-delivered' | string;
  jobId: string;
  outcome: AutomationJobOutcome | '';
  reasonCode: string;
  terminal: boolean;
  generation: number;
  cookerPhase: number;
  cookerProgress: number;
  traceId?: string;
  targetKind: 'rare' | 'normal' | string;
  orderKey?: string;
  deskCode: number;
  guestId?: number | null;
  guestName?: string;
  foodId: number;
  foodName?: string;
  beverageId: number;
  beverageName?: string;
  recipeId?: number;
  extraIngredientIds?: number[];
  actualFoodId?: number;
  targetFoodTags?: string[];
  actualFoodTags?: string[];
  message?: string;
}

export interface AutomationSafetyBarrierDiagnostic {
  sequence: number;
  targetKind: 'rare' | 'normal' | string;
  title: string;
  code: string;
  message: string;
  error: string;
}

export type AutomationJobOutcome =
  | 'waiting'
  | 'progressed'
  | 'completed'
  | 'interrupted'
  | 'retryable-failure'
  | 'blocked'
  | 'fatal'
  | 'cancelled';

export interface AutomationCookingJobSnapshot {
  jobId: string;
  targetKind: 'rare' | 'normal';
  traceId: string;
  orderKey: string;
  deskCode: number;
  guestId: number | null;
  guestName: string;
  foodId: number;
  foodName: string;
  recipeId: number;
  state: 'cooking' | 'ready' | 'manual-handoff' | 'manual-handoff-expired';
  outcome: AutomationJobOutcome;
  reasonCode: string;
  specialTargetRevision: number;
  allowYuumaControlledProgression: boolean;
  autoDeliverFood: boolean;
  controllerId: string;
  resultId: string;
  generation: number;
  contentRevision: number;
  cookerPhase: number;
  cookerProgress: number;
  ownershipObservationFailures: number;
  regressiveObservations: number;
  deliveryFailureAttempts: number;
  manualHandoffReadFailures: number;
  warmerStoreCommitted: boolean;
  warmerStoreCommitUncertain: boolean;
  warmerResetAttempts: number;
  foodDeliveryCommitted: boolean;
  foodDeliveryCommitUncertain: boolean;
  foodDeliveryCleanupAttempts: number;
  startedAtUtc: string;
  lastObservedAtUtc: string;
  lastProgressAtUtc: string;
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
  usableCookerNames: Set<string>;
  runtimeUnavailableCookerNames: Set<string>;
  hasCookerSnapshot: boolean;
}

export type RecommendationBlockReasonCode =
  | 'food-tag-not-supported'
  | 'food-recipe-locked'
  | 'food-base-ingredient-missing'
  | 'food-cooker-missing'
  | 'food-cooker-runtime-unavailable'
  | 'food-required-tag-not-generated'
  | 'food-special-rule-mismatch'
  | 'food-negative-tag'
  | 'beverage-unavailable'
  | 'beverage-excluded'
  | 'beverage-tag-mismatch'
  | 'budget-unavailable'
  | 'special-evaluation-unmet'
  | 'execution-plan-missing';

export type RecommendationCandidateStage =
  | 'food-tag-reachability'
  | 'food-recipe-unlocked'
  | 'food-base-ingredients'
  | 'food-cooker'
  | 'food-candidate-generation'
  | 'food-special-rule'
  | 'food-negative-safe'
  | 'beverage-available'
  | 'beverage-allowed'
  | 'beverage-required-tag'
  | 'budget'
  | 'special-evaluation'
  | 'execution-plan';

export interface RecommendationFoodRecipeEligibilityCounts {
  catalog: number;
  requiredTagReachable: number;
  requiredTagReachableUnlocked: number;
  requiredTagReachableBaseIngredientsReady: number;
  requiredTagReachableCookerReady: number;
}

export interface RecommendationFoodCandidateCounts {
  generated: number;
  generatedRequiredTagMatched: number;
  merged: number;
  baseOrderMatched: number;
  negativeSafe: number;
  specialRuleMatched: number;
  executable: number;
}

export interface RecommendationBeverageCandidateCounts {
  catalog: number;
  available: number;
  allowed: number;
  requiredTagMatched: number;
  specialRuleMatched: number;
}

export interface RecommendationPlanCounts {
  rawExecutable: number;
  specialRuleSafe: number;
  executable: number;
}

/**
 * 诊断计数按单位分组：配方资格使用“配方数”，候选与计划分别使用对应对象数。
 * 这些计数只解释推荐阻塞原因，不参与排序或自动化目标选择。
 */
export interface RecommendationCandidateStageCounts {
  foodRecipeEligibility: RecommendationFoodRecipeEligibilityCounts;
  foodCandidates: RecommendationFoodCandidateCounts;
  beverageCandidates: RecommendationBeverageCandidateCounts;
  plans: RecommendationPlanCounts;
}

/**
 * 推荐无可执行计划时的结构化首个清零原因。
 */
export interface RecommendationBlockedDiagnostic {
  code: RecommendationBlockReasonCode;
  firstEmptyStage: RecommendationCandidateStage;
  message: string;
  counts: RecommendationCandidateStageCounts;
  missingIngredientNames: string[];
  requiredCookerNames: string[];
  placedCookerNames: string[];
  usableCookerNames: string[];
  runtimeUnavailableCookerNames: string[];
  remainingBudget: number | null;
  minimumPairPrice: number | null;
  stateSignature: string;
}

/**
 * 已按稀客需求计算好的推荐结果缓存。
 */
export interface CachedRecommendation {
  customer: RareCustomerCatalogItem;
  executionPlans: RareOrderRecommendationPlan[];
  budget: RecommendationBudgetResult | null;
  blockedMessages: string[];
  blockedDiagnostic: RecommendationBlockedDiagnostic | null;
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

export interface LocalApiLogSettings {
  aggregateModLogEnabled: boolean;
  aggregateModLogPath: string;
  aggregateModLogDirectory: string;
  aggregateModLogMaxFileBytes: number;
  aggregateModLogMaxFileCount: number;
  aggregateModLogMaxTotalBytes: number;
  bepInExConsoleSupported: boolean;
  bepInExConsoleConfiguredVisible: boolean;
  bepInExConsoleActive: boolean;
  bepInExConsoleVisible: boolean;
  bepInExConsoleStatus: string;
}

export interface BepInExConsoleVisibilityResponse {
  ok: boolean;
  supported: boolean;
  configuredVisible: boolean;
  active: boolean;
  visible: boolean;
  status: string;
  error: string | null;
}

export interface LocalApiLanEndpoint {
  address: string;
  endpoint: string;
  interfaceName: string;
  interfaceType: string;
  hasGateway: boolean;
  linkLocal: boolean;
  recommended: boolean;
}

export interface LocalApiConnectionConfig {
  ok: boolean;
  localEndpoint: string;
  lanEnabled: boolean;
  lanRunning: boolean;
  lanBindHost: string;
  port: number;
  token: string;
  lanEndpoints: LocalApiLanEndpoint[];
  lanError: string | null;
  error: string | null;
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

export interface LocalApiStatusResponse {
  ok: boolean;
  status: string;
  error: string | null;
}

export type AutomationCancellationTarget = 'commands' | 'rare' | 'normal' | 'all';

export interface AutomationCancellationRequestBarrier {
  endpoint: string;
  target: AutomationCancellationTarget;
}

export interface AutomationCancellationAcknowledgedBarrier extends AutomationCancellationRequestBarrier {
  automationSessionId: string;
  commandEpoch: number;
}

export type AutomationCancellationBarrier =
  | AutomationCancellationRequestBarrier
  | AutomationCancellationAcknowledgedBarrier;

export interface AutomationCancellationResponse {
  ok: boolean;
  target: AutomationCancellationTarget;
  status: string;
  error: string | null;
  commandEpoch: number;
  cancelledJobs: number;
  cancelledCommands: number;
  leaseReleased: boolean;
}

export interface AutomationSafetyBarrierAckResponse {
  ok: boolean;
  sequence: number;
  acknowledgedCount: number;
  acknowledgedSequences: number[];
  status: string;
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

export type UpdateCheckState =
  | 'idle'
  | 'checking'
  | 'available'
  | 'current'
  | 'downloading'
  | 'downloaded'
  | 'installed'
  | 'failed'
  | 'disabled';

export type UpdateInstallState =
  | ''
  | 'waiting'
  | 'preparing'
  | 'closing-companion'
  | 'waiting-game'
  | 'terminating-game'
  | 'game-closed'
  | 'backing-up'
  | 'installing'
  | 'verifying'
  | 'succeeded'
  | 'failed'
  | 'cancelled';

export interface UpdateStatusResponse {
  ok: boolean;
  currentVersion: string;
  enabled: boolean;
  autoCheck: boolean;
  includePrerelease: boolean;
  state: UpdateCheckState;
  latestVersion: string;
  latestTag: string;
  hasUpdate: boolean;
  lastAttemptAtUtc: string;
  lastSuccessAtUtc: string;
  nextCheckAtUtc: string;
  consecutiveFailures: number;
  publishedAtUtc: string;
  releaseUrl: string;
  packageAsset: string;
  packageSize: number;
  downloadedVersion: string;
  downloadedAtUtc: string;
  staged: boolean;
  installState: UpdateInstallState;
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
  enabled: boolean;
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
  enabled?: boolean;
  pinToTop?: boolean;
  sortOrder?: number;
}

export type CustomRecipeGroupMode = 'customer' | 'recipe';

export type CustomRecipeSelection =
  | { scope: 'all' }
  | { scope: 'customer'; customerId: number }
  | { scope: 'recipe'; foodId: number }
  | { scope: 'entry'; id: string };

export interface CustomRecipeFlagUpdateInput {
  selection: CustomRecipeSelection;
  enabled?: boolean;
  pinToTop?: boolean;
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

export interface MissionPresentationMetadata {
  receiverLabel: string;
  characterName: string;
  sceneNames: string[];
  presentationStatus: string;
}

export interface TrackedMissionEntry extends MissionPresentationMetadata {
  label: string;
  title: string;
  status: TrackedMissionStatus;
  conditionCount: number;
  completedConditionCount: number | null;
  conditionStates: Array<boolean | null>;
}

export interface TrackedMissionsResponse {
  ok: boolean;
  runtimeAvailable: boolean;
  generation: number;
  status: TrackedMissionRuntimeStatus;
  contentSignature: string;
  unchanged?: false;
  unverifiedCount: number;
  trackingCount: number;
  fulfilledCount: number;
  missions: TrackedMissionEntry[];
  error?: string | null;
}

export interface TrackedMissionsUnchangedResponse {
  unchanged: true;
  contentSignature: string;
}

export type TrackedMissionsApiResponse =
  | TrackedMissionsResponse
  | TrackedMissionsUnchangedResponse;

export interface AvailableMissionEntry extends MissionPresentationMetadata {
  label: string;
  title: string;
}

export interface AvailableMissionsResponse {
  ok: boolean;
  runtimeAvailable: boolean;
  status: TrackedMissionRuntimeStatus;
  missionGeneration: number;
  daySceneGeneration: number;
  contentSignature: string;
  unchanged?: false;
  availableCount: number;
  missions: AvailableMissionEntry[];
  error?: string | null;
}

export interface AvailableMissionsUnchangedResponse {
  unchanged: true;
  contentSignature: string;
}

export type AvailableMissionsApiResponse =
  | AvailableMissionsResponse
  | AvailableMissionsUnchangedResponse;

export interface RareOrderDismissResponse {
  ok: boolean;
  removed: number;
  status: string;
  error: string | null;
}

export interface GameUiPinningTarget {
  signature: string;
  sourceOrderKey: string;
  sourceOrderSignature: string;
  orderTraceId: string;
  recipeId: number;
  recipeName: string;
  ingredientIds: number[];
  extraIngredientIds: number[];
  beverageId: number;
  beverageName: string;
  cookerTypeId: number;
  cookerName: string;
  deskCode: number;
}

export interface RareAutoOrderDiagnostic {
  orderKey: string;
  traceId?: string;
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
  detailMessage: string;
  detailUpdatedAtMs: number;
  prepared: boolean;
  beverageDeliveryRequested: boolean;
  hasServedFood: boolean;
  hasServedBeverage: boolean;
  paused: boolean;
  manualResolutionRequired: boolean;
}

export interface NormalAutoOrderDiagnostic {
  orderKey: string;
  traceId?: string;
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
  detailMessage: string;
  detailUpdatedAtMs: number;
  prepared: boolean;
  beverageDeliveryRequested: boolean;
  foodDeliveryRequested: boolean;
  completed: boolean;
  paused: boolean;
  manualResolutionRequired: boolean;
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
  usedControllerIndexes: Set<number>;
  labelsByControllerIndex: Map<number, string>;
}

export interface AutomationCookerSlot {
  controllerIndex: number;
  controllerIdentity: string;
  gridPosition: CookerGridPosition;
  supportedKeys: string[];
}

export interface AutomationCookerPool {
  slots: AutomationCookerSlot[];
  snapshotComplete: boolean;
  controllerCount: number;
  readFailureCount: number;
}

export interface CookerRequirement {
  key: string;
  label: string;
}

export interface CookerReservationResult {
  ok: boolean;
  message: string;
  controllerIndex?: number;
  controllerIdentity?: string;
  gridPosition?: CookerGridPosition;
}

export interface CookerControllerReservation {
  controllerIndex: number;
  controllerIdentity: string;
  gridPosition: CookerGridPosition;
}

export interface NormalCookerDemand {
  controllerIndexes: Set<number>;
  labelsByControllerIndex: Map<number, string>;
}

export interface AutomationCookerResourceRow {
  key: string;
  label: string;
  capacity: number;
  normalReserved: number;
  rareReserved: number;
  labels: string[];
}

export interface AutomationBlockedNormalResourceRow {
  orderKey: string;
  label: string;
  reason: string;
}

export interface AutomationResourceOverview {
  cookers: AutomationCookerResourceRow[];
  normalBlocked: AutomationBlockedNormalResourceRow[];
}

export type ToggleRecipeFavorite = (customer: RareCustomerCatalogItem, foodTag: string, recipe: RareRecipeRecommendation) => Promise<void>;
export type ToggleBeverageFavorite = (customer: RareCustomerCatalogItem, beverageTag: string, beverage: RareBeverageRecommendation) => Promise<void>;
