import type {
  AvailableMissionEntry,
  AvailableMissionActivationHint,
  AvailableMissionActivationMode,
  AvailableMissionActivationStatus,
  AvailableMissionSourceTiming,
  AvailableMissionTriggerKind,
  AvailableMissionsApiResponse,
  AvailableMissionsResponse,
  TrackedMissionRuntimeStatus,
} from '@/companion/types';
import { parseMissionPresentationMetadata } from '@/companion/mission-presentation';

const MAX_AVAILABLE_MISSIONS = 4096;
const CONTENT_SIGNATURE_PATTERN = /^[0-9a-f]{64}$/;
const AVAILABLE_MISSION_RUNTIME_STATUSES = new Set<TrackedMissionRuntimeStatus>([
  'not-attached',
  'waiting-for-load',
  'loading',
  'ready',
  'runtime-unavailable',
  'mission-data-incomplete',
]);
const AVAILABLE_MISSION_ACTIVATION_MODES = new Set<AvailableMissionActivationMode>([
  'conditional',
  'automatic',
  'multiple',
]);
const AVAILABLE_MISSION_ACTIVATION_STATUSES = new Set<AvailableMissionActivationStatus>([
  'available',
  'triggering',
]);
const AVAILABLE_MISSION_TRIGGER_KINDS = new Set<AvailableMissionTriggerKind>([
  'enter-day-scene-map',
  'enter-day-scene',
  'kizuna-checkpoint',
  'multiple',
]);
const AVAILABLE_MISSION_SOURCE_TIMINGS = new Set<AvailableMissionSourceTiming>([
  'before-performance',
  'after-performance',
  'multiple',
]);
const AVAILABLE_MISSION_ACTIVATION_HINTS = new Set<AvailableMissionActivationHint>([
  'enter-target-day-map',
  'enter-day-scene',
  'kizuna-ready',
  'native-start-pending',
  'multiple-sources',
]);

export const AVAILABLE_MISSION_POLL_INTERVAL_MS = 2_000;
export const AVAILABLE_MISSION_TRANSIENT_RETRY_DELAYS_MS = [
  500,
  1_000,
  2_000,
  4_000,
] as const;

export function parseAvailableMissionsApiResponse(value: unknown): AvailableMissionsApiResponse {
  const response = requireRecord(value, '可接取任务响应');
  const contentSignature = requireContentSignature(response.contentSignature);
  if (response.unchanged === true) {
    return {
      unchanged: true,
      contentSignature,
    };
  }

  const ok = requireBoolean(response.ok, 'ok');
  const runtimeAvailable = requireBoolean(response.runtimeAvailable, 'runtimeAvailable');
  const status = requireRuntimeStatus(response.status);
  const missionGeneration = requireBoundedInteger(
    response.missionGeneration,
    'missionGeneration',
    Number.MAX_SAFE_INTEGER,
  );
  const sourceRevision = requireBoundedInteger(
    response.sourceRevision,
    'sourceRevision',
    Number.MAX_SAFE_INTEGER,
  );
  if (!Array.isArray(response.missions) || response.missions.length > MAX_AVAILABLE_MISSIONS) {
    throw new Error('可接取任务响应中的 missions 数量或类型无效。');
  }
  const missions = response.missions.map((mission, index) => parseAvailableMission(mission, index));
  ensureUniqueMissionLabels(missions);
  const availableCount = requireBoundedInteger(
    response.availableCount,
    'availableCount',
    MAX_AVAILABLE_MISSIONS,
  );
  if (availableCount !== missions.length) {
    throw new Error('可接取任务响应的计数与任务列表不一致。');
  }

  if (runtimeAvailable
      && (!ok || status !== 'ready' || missionGeneration < 1 || sourceRevision < 1)) {
    throw new Error('可接取任务运行时可用时必须返回有效代际和 ready 状态。');
  }
  if (!runtimeAvailable && status === 'ready') {
    throw new Error('可接取任务运行时不可用时不得返回 ready 状态。');
  }
  if ((!ok || !runtimeAvailable) && missions.length > 0) {
    throw new Error('可接取任务运行时不可用时不得返回任务列表。');
  }

  const error = response.error;
  if (error !== undefined && error !== null && typeof error !== 'string') {
    throw new Error('可接取任务响应中的 error 类型无效。');
  }

  return {
    ok,
    runtimeAvailable,
    status,
    missionGeneration,
    sourceRevision,
    contentSignature,
    availableCount,
    missions,
    error: error ?? null,
  };
}

export function compareAvailableMissions(
  left: AvailableMissionEntry,
  right: AvailableMissionEntry,
): number {
  return left.title.localeCompare(right.title, 'zh-Hans-CN')
    || left.label.localeCompare(right.label, 'en');
}

export function getAvailableMissionTransientRetryDelayMs(attemptIndex: number): number | null {
  if (!Number.isInteger(attemptIndex)
      || attemptIndex < 0
      || attemptIndex >= AVAILABLE_MISSION_TRANSIENT_RETRY_DELAYS_MS.length) {
    return null;
  }
  return AVAILABLE_MISSION_TRANSIENT_RETRY_DELAYS_MS[attemptIndex];
}

export function getAvailableMissionsResponseError(response: AvailableMissionsResponse): string {
  switch (response.status) {
    case 'not-attached':
      return '可接取任务读取模块尚未挂载。';
    case 'waiting-for-load':
      return '等待游戏完成存档任务初始化。';
    case 'loading':
      return '正在校验可接取任务。';
    case 'runtime-unavailable':
      return '当前游戏运行时暂时不能安全读取可接取任务。';
    case 'mission-data-incomplete':
      return '任务定义或运行时数据不完整，本次可接取任务读取已停止。';
    default:
      return response.error?.trim() || response.status || '读取可接取任务失败。';
  }
}

function parseAvailableMission(value: unknown, index: number): AvailableMissionEntry {
  const mission = requireRecord(value, `missions[${index}]`);
  const presentation = parseMissionPresentationMetadata(mission, `missions[${index}]`);
  return {
    ...presentation,
    label: requireOpaqueIdentity(mission.label, `missions[${index}].label`),
    title: requireNonBlankString(mission.title, `missions[${index}].title`),
    activationMode: requireFiniteString(
      mission.activationMode,
      `missions[${index}].activationMode`,
      AVAILABLE_MISSION_ACTIVATION_MODES,
    ),
    activationStatus: requireFiniteString(
      mission.activationStatus,
      `missions[${index}].activationStatus`,
      AVAILABLE_MISSION_ACTIVATION_STATUSES,
    ),
    triggerKind: requireFiniteString(
      mission.triggerKind,
      `missions[${index}].triggerKind`,
      AVAILABLE_MISSION_TRIGGER_KINDS,
    ),
    sourceTiming: requireFiniteString(
      mission.sourceTiming,
      `missions[${index}].sourceTiming`,
      AVAILABLE_MISSION_SOURCE_TIMINGS,
    ),
    activationHint: requireFiniteString(
      mission.activationHint,
      `missions[${index}].activationHint`,
      AVAILABLE_MISSION_ACTIVATION_HINTS,
    ),
  };
}

function ensureUniqueMissionLabels(missions: AvailableMissionEntry[]) {
  const labels = new Set<string>();
  for (const mission of missions) {
    if (labels.has(mission.label)) {
      throw new Error(`可接取任务响应包含重复标签：${mission.label}`);
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
  if (typeof value !== 'boolean') {
    throw new Error(`可接取任务响应中的 ${label} 类型无效。`);
  }
  return value;
}

function requireOpaqueIdentity(value: unknown, label: string): string {
  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`可接取任务响应中的 ${label} 为空或类型无效。`);
  }
  return value;
}

function requireNonBlankString(value: unknown, label: string): string {
  if (typeof value !== 'string' || !value.trim()) {
    throw new Error(`可接取任务响应中的 ${label} 为空或类型无效。`);
  }
  return value;
}

function requireContentSignature(value: unknown): string {
  if (typeof value !== 'string' || !CONTENT_SIGNATURE_PATTERN.test(value)) {
    throw new Error('可接取任务响应中的 contentSignature 无效。');
  }
  return value;
}

function requireFiniteString<T extends string>(
  value: unknown,
  label: string,
  allowed: ReadonlySet<T>,
): T {
  if (typeof value !== 'string' || !allowed.has(value as T)) {
    throw new Error(`可接取任务响应中的 ${label} 无效。`);
  }
  return value as T;
}

function requireRuntimeStatus(value: unknown): TrackedMissionRuntimeStatus {
  if (typeof value !== 'string'
      || !AVAILABLE_MISSION_RUNTIME_STATUSES.has(value as TrackedMissionRuntimeStatus)) {
    throw new Error('可接取任务响应中的 status 无效。');
  }
  return value as TrackedMissionRuntimeStatus;
}

function requireBoundedInteger(value: unknown, label: string, maximum: number): number {
  if (typeof value !== 'number'
      || !Number.isSafeInteger(value)
      || value < 0
      || value > maximum) {
    throw new Error(`可接取任务响应中的 ${label} 超出允许范围。`);
  }
  return value;
}
