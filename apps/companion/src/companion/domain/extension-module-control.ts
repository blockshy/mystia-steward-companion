export type ExtensionModuleScope = 'local-client' | 'primary-profile';

export type ExtensionModuleControlStatus =
  | 'writable'
  | 'disconnected'
  | 'waiting-authority'
  | 'secondary-read-only'
  | 'saving'
  | 'authority-busy'
  | 'operation-in-flight';

export interface ExtensionModuleControlModel {
  scope: ExtensionModuleScope;
  status: ExtensionModuleControlStatus;
  enabled: boolean;
  writable: boolean;
  pending: boolean;
  scopeLabel: string;
  statusLabel: string;
  reason: string;
}

export interface ResolveLocalExtensionModuleControlInput {
  enabled: boolean;
  connected: boolean;
  operationInFlight?: boolean;
  disconnectedReason?: string;
  operationReason?: string;
}

export interface ResolvePrimaryExtensionModuleControlInput {
  enabled: boolean;
  connected: boolean;
  authorityReady: boolean;
  currentDeviceIsPrimary: boolean;
  primaryDeviceLabel?: string;
  profileUpdatePending?: boolean;
  authorityBusy?: boolean;
  operationInFlight?: boolean;
  disconnectedReason?: string;
  waitingAuthorityReason?: string;
  secondaryReadOnlyReason?: string;
  profileUpdateReason?: string;
  authorityBusyReason?: string;
  operationReason?: string;
}

const LOCAL_SCOPE_LABEL = '当前设备';
const PRIMARY_PROFILE_SCOPE_LABEL = '主设备共享';

/**
 * Resolves a module switch that only controls this companion client's reads or actions.
 *
 * A disconnected local module remains writable: the selection is a device preference and
 * cannot itself claim that the Mod accepted any runtime change.
 */
export function resolveLocalExtensionModuleControl({
  enabled,
  connected,
  operationInFlight = false,
  disconnectedReason,
  operationReason,
}: ResolveLocalExtensionModuleControlInput): ExtensionModuleControlModel {
  if (operationInFlight) {
    return createControlModel({
      scope: 'local-client',
      status: 'operation-in-flight',
      enabled,
      writable: false,
      pending: true,
      reason: operationReason
        ?? '已提交的操作正在等待确定结果；完成前不能切换模块。',
    });
  }

  if (!connected) {
    return createControlModel({
      scope: 'local-client',
      status: 'disconnected',
      enabled,
      writable: true,
      pending: false,
      reason: disconnectedReason
        ?? '当前未连接 Mod；可以预先设置，选择会保存在当前设备并在连接后生效。',
    });
  }

  return createControlModel({
    scope: 'local-client',
    status: 'writable',
    enabled,
    writable: true,
    pending: false,
    reason: '',
  });
}

/**
 * Resolves a module switch backed by the primary device's authoritative shared profile.
 *
 * The resolver never models an offline desired value. Until the active authority is connected,
 * aligned and writable, the last authoritative value is visible but read-only.
 */
export function resolvePrimaryExtensionModuleControl({
  enabled,
  connected,
  authorityReady,
  currentDeviceIsPrimary,
  primaryDeviceLabel,
  profileUpdatePending = false,
  authorityBusy = false,
  operationInFlight = false,
  disconnectedReason,
  waitingAuthorityReason,
  secondaryReadOnlyReason,
  profileUpdateReason,
  authorityBusyReason,
  operationReason,
}: ResolvePrimaryExtensionModuleControlInput): ExtensionModuleControlModel {
  if (!connected) {
    return createControlModel({
      scope: 'primary-profile',
      status: 'disconnected',
      enabled,
      writable: false,
      pending: false,
      reason: disconnectedReason
        ?? '当前未连接 Mod，无法确认主设备和生效配置；连接后才能修改。',
    });
  }

  if (profileUpdatePending) {
    return createControlModel({
      scope: 'primary-profile',
      status: 'saving',
      enabled,
      writable: false,
      pending: true,
      reason: profileUpdateReason
        ?? '主设备共享配置正在提交；Mod 确认前不能再次切换。',
    });
  }

  if (!authorityReady) {
    return createControlModel({
      scope: 'primary-profile',
      status: 'waiting-authority',
      enabled,
      writable: false,
      pending: false,
      reason: waitingAuthorityReason
        ?? '正在确认主设备和生效配置；确认前只能查看。',
    });
  }

  if (!currentDeviceIsPrimary) {
    const primaryLabel = primaryDeviceLabel?.trim() || '其他设备';
    return createControlModel({
      scope: 'primary-profile',
      status: 'secondary-read-only',
      enabled,
      writable: false,
      pending: false,
      reason: secondaryReadOnlyReason
        ?? `当前由“${primaryLabel}”提供生效配置；此模块只能在主设备修改。`,
    });
  }

  if (operationInFlight) {
    return createControlModel({
      scope: 'primary-profile',
      status: 'operation-in-flight',
      enabled,
      writable: false,
      pending: true,
      reason: operationReason
        ?? '相关运行时操作正在提交；取得确定结果前不能切换模块。',
    });
  }

  if (authorityBusy) {
    return createControlModel({
      scope: 'primary-profile',
      status: 'authority-busy',
      enabled,
      writable: false,
      pending: true,
      reason: authorityBusyReason
        ?? '设备配置操作正在进行；完成前不能切换模块。',
    });
  }

  return createControlModel({
    scope: 'primary-profile',
    status: 'writable',
    enabled,
    writable: true,
    pending: false,
    reason: '',
  });
}

function createControlModel({
  scope,
  status,
  enabled,
  writable,
  pending,
  reason,
}: Pick<ExtensionModuleControlModel,
  'scope' | 'status' | 'enabled' | 'writable' | 'pending' | 'reason'>): ExtensionModuleControlModel {
  return {
    scope,
    status,
    enabled,
    writable,
    pending,
    scopeLabel: scope === 'local-client' ? LOCAL_SCOPE_LABEL : PRIMARY_PROFILE_SCOPE_LABEL,
    statusLabel: getExtensionModuleStatusLabel(status),
    reason,
  };
}

function getExtensionModuleStatusLabel(status: ExtensionModuleControlStatus): string {
  switch (status) {
    case 'writable':
      return '可修改';
    case 'disconnected':
      return '未连接';
    case 'waiting-authority':
      return '等待权威状态';
    case 'secondary-read-only':
      return '只读';
    case 'saving':
      return '正在保存';
    case 'authority-busy':
      return '设备操作中';
    case 'operation-in-flight':
      return '操作处理中';
  }
}
