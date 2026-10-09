/** 客户端仅保留协议、表单和展示辅助；业务推荐与自动化决策由 C# 服务计算。 */
import type { RecommendationStateSnapshot } from '@/companion/types';

const COOKER_TYPE_NAME_BY_ID = new Map<number, string>([
  [1, '煮锅'],
  [2, '烧烤架'],
  [3, '油锅'],
  [4, '蒸锅'],
  [5, '料理台'],
]);

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
