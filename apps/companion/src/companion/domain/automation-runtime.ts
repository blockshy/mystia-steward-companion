export const NIGHT_BUSINESS_TUTORIAL_ACTIVE = 'night-business-tutorial-active';
export const NIGHT_BUSINESS_TUTORIAL_STATE_UNAVAILABLE = 'night-business-tutorial-state-unavailable';
export const NIGHT_BUSINESS_LIFECYCLE_UNAVAILABLE = 'night-business-lifecycle-unavailable';

export function getNightBusinessAutomationPauseMessage(blockReason: string): string {
  if (blockReason === NIGHT_BUSINESS_TUTORIAL_ACTIVE) {
    return '教学经营中，自动化已暂停。';
  }
  if (blockReason === NIGHT_BUSINESS_TUTORIAL_STATE_UNAVAILABLE) {
    return '暂时无法确认教学状态，自动化已暂停。';
  }
  return '';
}

export function getNightBusinessAutomationPauseLabel(blockReason: string): string {
  if (blockReason === NIGHT_BUSINESS_TUTORIAL_ACTIVE) return '教学暂停';
  if (blockReason === NIGHT_BUSINESS_TUTORIAL_STATE_UNAVAILABLE) return '状态待确认';
  return '';
}

export function getNightBusinessAutomationSummary({
  configured,
  allowed,
  blockReason,
  trackedCount,
}: {
  configured: boolean;
  allowed: boolean;
  blockReason: string;
  trackedCount: number;
}): string {
  if (!configured) return '未开启';
  if (allowed) return trackedCount > 0 ? `已开启 · 跟踪 ${trackedCount} 笔` : '已开启';
  if (blockReason === NIGHT_BUSINESS_TUTORIAL_ACTIVE) return '已暂停 · 教学经营';
  if (blockReason === NIGHT_BUSINESS_TUTORIAL_STATE_UNAVAILABLE) return '已暂停 · 状态待确认';
  if (blockReason === NIGHT_BUSINESS_LIFECYCLE_UNAVAILABLE) return '已开启 · 等待经营';
  return '已暂停 · 状态待确认';
}
