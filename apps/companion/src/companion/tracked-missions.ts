import type {
  TrackedMissionEntry,
  TrackedMissionRuntimeStatus,
  TrackedMissionStatus,
  TrackedMissionsApiResponse,
  TrackedMissionsResponse,
} from '@/companion/types';
import { parseMissionPresentationMetadata } from '@/companion/mission-presentation';

const MAX_TRACKED_MISSIONS = 4096;
const MAX_MISSION_CONDITIONS = 256;
const CONTENT_SIGNATURE_PATTERN = /^[0-9a-f]{64}$/;
const TRACKED_MISSION_RUNTIME_STATUSES = new Set<TrackedMissionRuntimeStatus>([
  'not-attached',
  'waiting-for-load',
  'loading',
  'ready',
  'runtime-unavailable',
  'mission-data-incomplete',
]);

export const TRACKED_MISSION_POLL_INTERVAL_MS = 2_000;
export const TRACKED_MISSION_TRANSIENT_RETRY_DELAYS_MS = [
  500,
  1_000,
  2_000,
  4_000,
] as const;
export const TRACKED_MISSION_STATUS_ORDER = [
  'fulfilled',
  'tracking',
  'unverified',
] as const satisfies readonly TrackedMissionStatus[];
export type TrackedMissionStatusView = 'all' | TrackedMissionStatus;

const TRACKED_MISSION_STATUS_VIEW_LABELS = {
  all: '全部',
  fulfilled: '可完成',
  tracking: '进行中',
  unverified: '待确认',
} as const satisfies Record<TrackedMissionStatusView, string>;

export const TRACKED_MISSION_STATUS_VIEW_ORDER = [
  'all',
  ...TRACKED_MISSION_STATUS_ORDER,
] as const satisfies readonly TrackedMissionStatusView[];

export interface TrackedMissionStatusViewModel {
  value: TrackedMissionStatusView;
  label: string;
  missions: TrackedMissionEntry[];
}

export function parseTrackedMissionsApiResponse(value: unknown): TrackedMissionsApiResponse {
  const response = requireRecord(value, '任务响应');
  const contentSignature = requireContentSignature(response.contentSignature);
  if (response.unchanged === true) {
    return {
      unchanged: true,
      contentSignature,
    };
  }

  const ok = requireBoolean(response.ok, 'ok');
  const runtimeAvailable = requireBoolean(response.runtimeAvailable, 'runtimeAvailable');
  const generation = requireBoundedInteger(response.generation, 'generation', Number.MAX_SAFE_INTEGER);
  const status = requireRuntimeStatus(response.status);
  const missionsValue = response.missions;
  if (!Array.isArray(missionsValue) || missionsValue.length > MAX_TRACKED_MISSIONS) {
    throw new Error('任务响应中的 missions 数量或类型无效。');
  }

  const missions = missionsValue.map((mission, index) => parseTrackedMission(mission, index));
  ensureUniqueMissionLabels(missions);
  const unverifiedCount = requireBoundedInteger(response.unverifiedCount, 'unverifiedCount', MAX_TRACKED_MISSIONS);
  const trackingCount = requireBoundedInteger(response.trackingCount, 'trackingCount', MAX_TRACKED_MISSIONS);
  const fulfilledCount = requireBoundedInteger(response.fulfilledCount, 'fulfilledCount', MAX_TRACKED_MISSIONS);
  const actualCounts = countTrackedMissionStatuses(missions);
  if (unverifiedCount !== actualCounts.unverified
      || trackingCount !== actualCounts.tracking
      || fulfilledCount !== actualCounts.fulfilled) {
    throw new Error('任务响应的状态计数与任务列表不一致。');
  }

  if (runtimeAvailable && generation < 1) {
    throw new Error('任务运行时可用时 generation 必须为正数。');
  }
  if (runtimeAvailable && (!ok || status !== 'ready')) {
    throw new Error('任务运行时可用时必须返回 ok=true 和 ready 状态。');
  }
  if (!runtimeAvailable && status === 'ready') {
    throw new Error('任务运行时不可用时不得返回 ready 状态。');
  }
  if ((!ok || !runtimeAvailable) && missions.length > 0) {
    throw new Error('任务运行时不可用时不得返回任务列表。');
  }

  const error = response.error;
  if (error !== undefined && error !== null && typeof error !== 'string') {
    throw new Error('任务响应中的 error 类型无效。');
  }

  return {
    ok,
    runtimeAvailable,
    generation,
    status,
    contentSignature,
    unverifiedCount,
    trackingCount,
    fulfilledCount,
    missions,
    error: error ?? null,
  };
}

export function compareTrackedMissions(left: TrackedMissionEntry, right: TrackedMissionEntry): number {
  const statusDifference = TRACKED_MISSION_STATUS_ORDER.indexOf(left.status)
    - TRACKED_MISSION_STATUS_ORDER.indexOf(right.status);
  if (statusDifference !== 0) return statusDifference;
  return left.title.localeCompare(right.title, 'zh-Hans-CN')
    || left.label.localeCompare(right.label, 'en');
}

export function buildTrackedMissionStatusViews(
  missions: readonly TrackedMissionEntry[],
): TrackedMissionStatusViewModel[] {
  const sortedMissions = missions.slice().sort(compareTrackedMissions);
  return TRACKED_MISSION_STATUS_VIEW_ORDER.map((value) => ({
    value,
    label: TRACKED_MISSION_STATUS_VIEW_LABELS[value],
    missions: value === 'all'
      ? sortedMissions
      : sortedMissions.filter((mission) => mission.status === value),
  }));
}

export function isTrackedMissionStatusView(value: string | null): value is TrackedMissionStatusView {
  return value !== null
    && TRACKED_MISSION_STATUS_VIEW_ORDER.some((candidate) => candidate === value);
}

export function getTrackedMissionTransientRetryDelayMs(attemptIndex: number): number | null {
  if (!Number.isInteger(attemptIndex)
      || attemptIndex < 0
      || attemptIndex >= TRACKED_MISSION_TRANSIENT_RETRY_DELAYS_MS.length) {
    return null;
  }
  return TRACKED_MISSION_TRANSIENT_RETRY_DELAYS_MS[attemptIndex];
}

export function getTrackedMissionsResponseError(response: TrackedMissionsResponse): string {
  const explicitError = response.error?.trim();
  if (explicitError) return explicitError;

  switch (response.status) {
    case 'not-attached':
      return '任务读取模块尚未挂载。';
    case 'waiting-for-load':
      return '等待读取存档中的任务状态。';
    case 'loading':
      return '正在校验存档任务状态。';
    case 'runtime-unavailable':
      return '当前游戏运行时暂时不能安全读取任务。';
    case 'mission-data-incomplete':
      return '任务定义或进度数据不完整，本次读取已停止。';
    default:
      return response.status || '读取已追踪任务失败。';
  }
}

function parseTrackedMission(value: unknown, index: number): TrackedMissionEntry {
  const mission = requireRecord(value, `missions[${index}]`);
  const presentation = parseMissionPresentationMetadata(mission, `missions[${index}]`);
  const label = requireOpaqueIdentity(mission.label, `missions[${index}].label`);
  const title = requireNonBlankString(mission.title, `missions[${index}].title`);
  const status = mission.status;
  if (status !== 'unverified' && status !== 'tracking' && status !== 'fulfilled') {
    throw new Error(`missions[${index}].status 无效。`);
  }

  const conditionCount = requireBoundedInteger(
    mission.conditionCount,
    `missions[${index}].conditionCount`,
    MAX_MISSION_CONDITIONS,
  );
  if (!Array.isArray(mission.conditionStates) || mission.conditionStates.length !== conditionCount) {
    throw new Error(`missions[${index}].conditionStates 与条件数量不一致。`);
  }

  if (status === 'unverified') {
    if (mission.completedConditionCount !== null
        || mission.conditionStates.some((condition) => condition !== null)) {
      throw new Error(`missions[${index}] 未验证时不得包含已验证进度。`);
    }
    return {
      ...presentation,
      label,
      title,
      status,
      conditionCount,
      completedConditionCount: null,
      conditionStates: mission.conditionStates.map(() => null),
    };
  }

  const completedConditionCount = requireBoundedInteger(
    mission.completedConditionCount,
    `missions[${index}].completedConditionCount`,
    conditionCount,
  );
  if (mission.conditionStates.some((condition) => typeof condition !== 'boolean')) {
    throw new Error(`missions[${index}] 已验证状态必须使用布尔条件数组。`);
  }
  const conditionStates = mission.conditionStates as boolean[];
  const actualCompletedCount = conditionStates.filter(Boolean).length;
  if (completedConditionCount !== actualCompletedCount) {
    throw new Error(`missions[${index}] 的已完成条件计数不一致。`);
  }
  if ((status === 'fulfilled') !== (completedConditionCount === conditionCount)) {
    throw new Error(`missions[${index}] 的任务状态与条件完成情况不一致。`);
  }

  return {
    ...presentation,
    label,
    title,
    status,
    conditionCount,
    completedConditionCount,
    conditionStates,
  };
}

function countTrackedMissionStatuses(missions: TrackedMissionEntry[]) {
  return missions.reduce(
    (counts, mission) => {
      counts[mission.status] += 1;
      return counts;
    },
    { unverified: 0, tracking: 0, fulfilled: 0 },
  );
}

function ensureUniqueMissionLabels(missions: TrackedMissionEntry[]) {
  const labels = new Set<string>();
  for (const mission of missions) {
    if (labels.has(mission.label)) {
      throw new Error(`任务响应包含重复标签：${mission.label}`);
    }
    labels.add(mission.label);
  }
}

function requireRecord(value: unknown, label: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`${label} 不是有效对象。`);
  }
  return value as Record<string, unknown>;
}

function requireBoolean(value: unknown, label: string): boolean {
  if (typeof value !== 'boolean') throw new Error(`任务响应中的 ${label} 类型无效。`);
  return value;
}

function requireOpaqueIdentity(value: unknown, label: string): string {
  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`任务响应中的 ${label} 为空或类型无效。`);
  }
  return value;
}

function requireNonBlankString(value: unknown, label: string): string {
  if (typeof value !== 'string' || !value.trim()) {
    throw new Error(`任务响应中的 ${label} 为空或类型无效。`);
  }
  return value;
}

function requireContentSignature(value: unknown): string {
  if (typeof value !== 'string' || !CONTENT_SIGNATURE_PATTERN.test(value)) {
    throw new Error('任务响应中的 contentSignature 无效。');
  }
  return value;
}

function requireRuntimeStatus(value: unknown): TrackedMissionRuntimeStatus {
  if (typeof value !== 'string'
    || !TRACKED_MISSION_RUNTIME_STATUSES.has(value as TrackedMissionRuntimeStatus)) {
    throw new Error('任务响应中的 status 无效。');
  }
  return value as TrackedMissionRuntimeStatus;
}

function requireBoundedInteger(value: unknown, label: string, maximum: number): number {
  if (typeof value !== 'number'
      || !Number.isSafeInteger(value)
      || value < 0
      || value > maximum) {
    throw new Error(`任务响应中的 ${label} 超出允许范围。`);
  }
  return value;
}
