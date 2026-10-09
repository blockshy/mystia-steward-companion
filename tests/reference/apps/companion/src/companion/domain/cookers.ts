import type { RareAutomationRecipeTarget } from '@/companion/automation-state';
import type {
  AutomationCookerPool,
  AutomationCookerSlot,
  CookerRequirement,
  NormalBusinessOrder,
  RecommendationStateSnapshot,
  RuntimeSets,
} from '@/companion/types';
import {
  DEFAULT_RECOMMENDATION_DATA,
  buildRecommendationDataIndexes,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
import type { RecipeCatalogItem } from '@/lib/catalog-types';

const COOKER_TYPE_NAME_BY_ID = new Map<number, string>([
  [1, '煮锅'],
  [2, '烧烤架'],
  [3, '油锅'],
  [4, '蒸锅'],
  [5, '料理台'],
]);

const COOKER_NAME_ALIASES = new Map<string, string>([
  ['烤架', '烧烤架'],
  ['烧烤台', '烧烤架'],
  ['锅', '煮锅'],
  ['炸锅', '油锅'],
]);

export function buildRuntimeSets(
  runtime: RecommendationStateSnapshot | null,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): RuntimeSets | null {
  if (!runtime) return null;
  const ingredientIds = new Set(runtime.availableIngredientIds);
  const allIngredientIds = data.ingredients.map((ingredient) => ingredient.id);
  const unavailableIngredientIds = new Set(allIngredientIds.filter((id) => !ingredientIds.has(id)));
  const placedCookerTypeIds = new Set(runtime.placedCookerTypeIds);
  const placedCookerNames = buildCookerNameSet(placedCookerTypeIds);
  const hasCookerSnapshot = runtime.placedCookerSnapshotComplete === true;
  const usableCookerTypeIds = new Set(placedCookerTypeIds);
  const runtimeUnavailableCookerTypeIds = hasCookerSnapshot
    && runtime.placedCookerLockedControllerCount > 0
    ? setDifference(new Set(COOKER_TYPE_NAME_BY_ID.keys()), usableCookerTypeIds)
    : new Set<number>();

  return {
    recipeIds: new Set(runtime.availableRecipeIds),
    beverageIds: new Set(runtime.availableBeverageIds),
    ingredientIds,
    unavailableIngredientIds,
    ownedIngredientQty: normalizeOwnedIngredientQty(runtime.ownedIngredientQty),
    ownedBeverageQty: normalizeOwnedIngredientQty(runtime.ownedBeverageQty ?? {}),
    placedCookerTypeIds,
    placedCookerNames,
    usableCookerNames: buildCookerNameSet(usableCookerTypeIds),
    runtimeUnavailableCookerNames: buildCookerNameSet(runtimeUnavailableCookerTypeIds),
    hasCookerSnapshot,
  };
}

/**
 * 构造推荐硬过滤使用的厨具集合。
 *
 * 用户关闭“排除缺失厨具”时，未摆放类型继续由设置放行；完整快照中已摆放但
 * 存在不可安全读取的锁定条目时，只保留开放控制器能够证明的类型；快照不可用时不保留部分容量。
 */
export function buildRecommendationCookerNameSet(
  runtimeSets: RuntimeSets,
  filterMissingCookers: boolean,
): Set<string> {
  if (!runtimeSets.hasCookerSnapshot) {
    return new Set(runtimeSets.placedCookerNames);
  }
  if (filterMissingCookers) {
    return new Set(runtimeSets.usableCookerNames);
  }

  const available = new Set<string>();
  for (const name of COOKER_TYPE_NAME_BY_ID.values()) {
    const normalized = normalizeCookerName(name);
    if (normalized && !runtimeSets.runtimeUnavailableCookerNames.has(normalized)) {
      available.add(normalized);
    }
  }
  return available;
}

export function validateRecommendationCookerSnapshot(runtime: RecommendationStateSnapshot): string {
  if (!Array.isArray(runtime.placedCookerTypeIds)) return 'placedCookerTypeIds 不是数组';
  if (!Array.isArray(runtime.placedCookers)) return 'placedCookers 不是数组';
  if (typeof runtime.placedCookerSnapshotComplete !== 'boolean') return 'placedCookerSnapshotComplete 不是布尔值';
  if (!isSnapshotCount(runtime.placedCookerControllerCount)) return 'placedCookerControllerCount 不是非负整数';
  if (!isSnapshotCount(runtime.placedCookerEmptyControllerCount)) {
    return 'placedCookerEmptyControllerCount 不是非负整数';
  }
  if (!isSnapshotCount(runtime.placedCookerLockedControllerCount)) {
    return 'placedCookerLockedControllerCount 不是非负整数';
  }
  if (!isSnapshotCount(runtime.placedCookerReadFailureCount)) return 'placedCookerReadFailureCount 不是非负整数';
  if (typeof runtime.placedCookerStatus !== 'string') return 'placedCookerStatus 不是字符串';
  if (runtime.placedCookerEmptyControllerCount
    + runtime.placedCookerLockedControllerCount
    + runtime.placedCookerReadFailureCount
    > runtime.placedCookerControllerCount) {
    return '空位、锁定与读取失败数量大于 controllerCount';
  }
  if (runtime.placedCookers.length
    + runtime.placedCookerEmptyControllerCount
    + runtime.placedCookerLockedControllerCount
    + runtime.placedCookerReadFailureCount !== runtime.placedCookerControllerCount) {
    return 'placedCookers 数量与 controllerCount/emptyControllerCount/lockedControllerCount/readFailureCount 不一致';
  }
  if (runtime.placedCookerSnapshotComplete
    && (runtime.placedCookerReadFailureCount !== 0
      || runtime.placedCookers.length
        + runtime.placedCookerEmptyControllerCount
        + runtime.placedCookerLockedControllerCount
        !== runtime.placedCookerControllerCount)) {
    return '完整厨具快照包含读取失败或缺失控制器';
  }
  if (!runtime.placedCookerSnapshotComplete
    && (runtime.placedCookers.length !== 0
      || runtime.placedCookerTypeIds.length !== 0
      || runtime.placedCookerEmptyControllerCount !== 0)) {
    return '不可用厨具快照包含部分控制器、空位或类型';
  }

  const placedTypeIds = new Set<number>();
  for (const typeId of runtime.placedCookerTypeIds) {
    if (!isCookerTypeId(typeId)) return 'placedCookerTypeIds 包含非法厨具类型';
    if (placedTypeIds.has(typeId)) return 'placedCookerTypeIds 包含重复厨具类型';
    placedTypeIds.add(typeId);
  }

  const seenControllerIndexes = new Set<number>();
  const seenControllerIdentities = new Set<string>();
  const seenGridPositions = new Set<string>();
  const projectedTypeIds = new Set<number>();
  for (const cooker of runtime.placedCookers) {
    if (!Number.isInteger(cooker.controllerIndex)
      || cooker.controllerIndex < 0
      || cooker.controllerIndex >= runtime.placedCookerControllerCount) {
      return 'placedCookers 包含非法 controllerIndex';
    }
    if (seenControllerIndexes.has(cooker.controllerIndex)) {
      return 'placedCookers 包含重复 controllerIndex';
    }
    seenControllerIndexes.add(cooker.controllerIndex);
    if (!isGridPosition(cooker.gridPosition)) {
      return `controller ${cooker.controllerIndex} 的 gridPosition 非法`;
    }
    const gridKey = buildGridPositionKey(cooker.gridPosition);
    if (seenGridPositions.has(gridKey)) {
      return 'placedCookers 包含重复 gridPosition';
    }
    seenGridPositions.add(gridKey);
    if (!isControllerIdentity(cooker.controllerIdentity)) {
      return `controller ${cooker.controllerIndex} 的 controllerIdentity 非法`;
    }
    if (seenControllerIdentities.has(cooker.controllerIdentity)) {
      return 'placedCookers 包含重复 controllerIdentity';
    }
    seenControllerIdentities.add(cooker.controllerIdentity);
    if (!Array.isArray(cooker.typeIds)
      || cooker.typeIds.length === 0
      || cooker.typeIds.some((typeId) => !isCookerTypeId(typeId))
      || new Set(cooker.typeIds).size !== cooker.typeIds.length) {
      return `controller ${cooker.controllerIndex} 的 typeIds 非法`;
    }
    if (!Array.isArray(cooker.typeNames) || cooker.typeNames.some((name) => typeof name !== 'string')) {
      return `controller ${cooker.controllerIndex} 的 typeNames 非法`;
    }
    const expectedTypeNames = cooker.typeIds.map((typeId) => COOKER_TYPE_NAME_BY_ID.get(typeId) ?? '');
    if (cooker.typeNames.length !== expectedTypeNames.length
      || cooker.typeNames.some((name, index) => name !== expectedTypeNames[index])
      || cooker.name !== expectedTypeNames.join('/')) {
      return `controller ${cooker.controllerIndex} 的厨具名称与 typeIds 不一致`;
    }
    if (typeof cooker.name !== 'string'
      || typeof cooker.challengeLocked !== 'boolean'
      || typeof cooker.couldOpen !== 'boolean'
      || typeof cooker.automationAvailable !== 'boolean'
      || typeof cooker.automationAvailabilityDiagnostic !== 'string'
      || typeof cooker.source !== 'string') {
      return `controller ${cooker.controllerIndex} 的基础字段非法`;
    }
    if (!isAutomationAvailability(cooker.automationAvailability)) {
      return `controller ${cooker.controllerIndex} 的 automationAvailability 非法`;
    }
    if (cooker.challengeLocked !== false || cooker.couldOpen !== true) {
      return `controller ${cooker.controllerIndex} 已锁定或不可开，不应进入 placedCookers`;
    }
    if (cooker.automationAvailable !== (cooker.automationAvailability !== 'Unavailable')) {
      return `controller ${cooker.controllerIndex} 的自动化可用状态不一致`;
    }
    for (const typeId of cooker.typeIds) projectedTypeIds.add(typeId);
  }

  if (!setsEqual(placedTypeIds, projectedTypeIds)) {
    return 'placedCookerTypeIds 与控制器类型投影不一致';
  }
  return '';
}

/**
 * 构建当前可供自动化预约的控制器槽位。
 *
 * 物理已摆放类型由 buildRuntimeSets 独立投影；这里仅接受后端完整分类后明确标记为
 * automationAvailable 的控制器。来源或锁定状态任一不完整时整轮不提供容量。
 */
export function buildAutomationCookerPool(
  runtime: RecommendationStateSnapshot | null | undefined,
): AutomationCookerPool {
  if (!runtime) {
    return {
      slots: [],
      snapshotComplete: false,
      controllerCount: 0,
      readFailureCount: 0,
    };
  }

  if (runtime.placedCookerSnapshotComplete !== true
    || runtime.placedCookerReadFailureCount !== 0
    || runtime.placedCookers.length
      + runtime.placedCookerEmptyControllerCount
      + runtime.placedCookerLockedControllerCount
      !== runtime.placedCookerControllerCount) {
    return {
      slots: [],
      snapshotComplete: false,
      controllerCount: runtime.placedCookerControllerCount,
      readFailureCount: runtime.placedCookerReadFailureCount,
    };
  }

  const slotsByControllerIndex = new Map<number, AutomationCookerSlot>();
  const duplicateControllerIndexes = new Set<number>();
  for (const cooker of runtime.placedCookers) {
    if (cooker.automationAvailable !== true) continue;
    if (cooker.couldOpen !== true || cooker.challengeLocked !== false) continue;
    if (!Number.isInteger(cooker.controllerIndex) || cooker.controllerIndex < 0) continue;
    if (!isGridPosition(cooker.gridPosition)) continue;
    if (!isControllerIdentity(cooker.controllerIdentity)) continue;
    if (slotsByControllerIndex.has(cooker.controllerIndex)) {
      slotsByControllerIndex.delete(cooker.controllerIndex);
      duplicateControllerIndexes.add(cooker.controllerIndex);
      continue;
    }
    if (duplicateControllerIndexes.has(cooker.controllerIndex)) continue;

    const supportedKeys = buildCookerSupportedKeySet(cooker.typeIds);
    if (supportedKeys.size === 0) continue;
    slotsByControllerIndex.set(cooker.controllerIndex, {
      controllerIndex: cooker.controllerIndex,
      controllerIdentity: cooker.controllerIdentity,
      gridPosition: { ...cooker.gridPosition },
      supportedKeys: [...supportedKeys].sort(compareOrdinal),
    });
  }

  return {
    slots: [...slotsByControllerIndex.values()]
      .sort((left, right) => left.controllerIndex - right.controllerIndex),
    snapshotComplete: runtime.placedCookerSnapshotComplete === true,
    controllerCount: runtime.placedCookerControllerCount,
    readFailureCount: runtime.placedCookerReadFailureCount,
  };
}

/**
 * 按厨具类型汇总开放槽位，仅用于资源概览显示。
 *
 * 一个多类型控制器会出现在多个类型的显示容量里；真实预约必须使用 controllerIndex 槽位池。
 */
export function buildAutomationCookerCapacity(pool: AutomationCookerPool): Map<string, number> {
  const capacity = new Map<string, number>();
  for (const slot of pool.slots) {
    for (const key of slot.supportedKeys) {
      capacity.set(key, (capacity.get(key) ?? 0) + 1);
    }
  }
  return capacity;
}

export function getCookerSlotCapacity(key: string, capacity: Map<string, number>): number {
  return Math.max(0, capacity.get(key) ?? 0);
}

export function findAvailableAutomationCookerSlot(
  pool: AutomationCookerPool,
  cookerKey: string,
  unavailableControllerIndexes: ReadonlySet<number>,
): AutomationCookerSlot | null {
  return pool.slots
    .filter((slot) =>
      slot.supportedKeys.includes(cookerKey)
      && !unavailableControllerIndexes.has(slot.controllerIndex)
    )
    .sort((left, right) =>
      left.supportedKeys.length - right.supportedKeys.length
      || left.controllerIndex - right.controllerIndex
    )[0] ?? null;
}

export function getRareCookerRequirement(target: RareAutomationRecipeTarget | null): CookerRequirement | null {
  const key = normalizeCookerName(target?.cookerName);
  if (!key) return null;
  return {
    key,
    label: key,
  };
}

export function getNormalCookerRequirement(
  order: NormalBusinessOrder,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): CookerRequirement | null {
  const recipe = getNormalOrderRecipe(order, data);
  if (!recipe) return null;
  return getRecipeCookerRequirement(recipe);
}

function getRecipeCookerRequirement(recipe: RecipeCatalogItem | null | undefined): CookerRequirement | null {
  const key = normalizeCookerName(recipe?.cooker);
  if (!key) return null;
  return {
    key,
    label: key,
  };
}

function getNormalOrderRecipe(
  order: NormalBusinessOrder,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): RecipeCatalogItem | null {
  const indexes = buildRecommendationDataIndexes(data);
  return indexes.recipeByFoodId.get(order.foodId) ?? null;
}

export function resolveCookerTypeId(value: string | null | undefined): number {
  const normalized = normalizeCookerName(value);
  if (!normalized) return -1;

  for (const [typeId, name] of COOKER_TYPE_NAME_BY_ID) {
    if (normalizeCookerName(name) === normalized) return typeId;
  }

  return -1;
}

function normalizeCookerName(value: string | null | undefined): string {
  const normalized = (value ?? '').trim();
  if (!normalized) return '';
  return COOKER_NAME_ALIASES.get(normalized) ?? normalized;
}

function buildCookerSupportedKeySet(
  typeIds: readonly number[],
): Set<string> {
  const keys = new Set<string>();
  for (const typeId of typeIds) {
    const normalized = normalizeCookerName(COOKER_TYPE_NAME_BY_ID.get(typeId));
    if (normalized) keys.add(normalized);
  }
  return keys;
}

function isSnapshotCount(value: number): boolean {
  return Number.isInteger(value) && value >= 0;
}

function isCookerTypeId(value: number): boolean {
  return Number.isInteger(value) && value >= 1 && value <= 5;
}

function isGridPosition(
  value: { x?: number; y?: number; z?: number } | null | undefined,
): value is { x: number; y: number; z: number } {
  return value != null
    && Number.isInteger(value.x)
    && Number.isInteger(value.y)
    && Number.isInteger(value.z);
}

function buildGridPositionKey(position: { x: number; y: number; z: number }): string {
  return `${position.x},${position.y},${position.z}`;
}

function isControllerIdentity(value: unknown): value is string {
  return typeof value === 'string'
    && /^0x(?=[0-9A-F]*[1-9A-F])[0-9A-F]+$/u.test(value);
}

function isAutomationAvailability(
  value: string,
): value is 'StrictIdle' | 'ExtractedResidual' | 'Unavailable' {
  return value === 'StrictIdle' || value === 'ExtractedResidual' || value === 'Unavailable';
}

function setsEqual<T>(left: ReadonlySet<T>, right: ReadonlySet<T>): boolean {
  return left.size === right.size && [...left].every((value) => right.has(value));
}

function compareOrdinal(left: string, right: string): number {
  if (left < right) return -1;
  if (left > right) return 1;
  return 0;
}

function buildCookerNameSet(typeIds: ReadonlySet<number>): Set<string> {
  const names = new Set<string>();
  for (const typeId of typeIds) {
    const mapped = COOKER_TYPE_NAME_BY_ID.get(typeId);
    if (mapped) names.add(normalizeCookerName(mapped));
  }
  return names;
}

function setDifference<T>(left: ReadonlySet<T>, right: ReadonlySet<T>): Set<T> {
  return new Set([...left].filter((value) => !right.has(value)));
}

function normalizeOwnedIngredientQty(ownedIngredientQty: Record<string, number>): Record<number, number> {
  return Object.fromEntries(
    Object.entries(ownedIngredientQty).map(([id, qty]) => [Number(id), qty]),
  ) as Record<number, number>;
}
