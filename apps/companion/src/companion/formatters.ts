import type {
  AutomationLogEntry,
  NightBusinessGuest,
} from '@/companion/types';

export function formatGuestFund(guest: NightBusinessGuest): string {
  if (typeof guest.fund !== 'number' || !Number.isFinite(guest.fund)) return '';
  return String(Math.trunc(guest.fund));
}

export function formatTime(date: Date) {
  return date.toLocaleTimeString('zh-CN', { hour12: false });
}

export function formatPerformanceMs(metrics?: Record<string, number>) {
  const entries = Object.entries(metrics ?? {})
    .filter(([, value]) => Number.isFinite(value))
    .sort((left, right) => right[1] - left[1])
    .slice(0, 4);
  if (entries.length === 0) return '暂无';

  return entries
    .map(([key, value]) => `${key} ${value >= 10 ? value.toFixed(0) : value.toFixed(1)}ms`)
    .join(' · ');
}

export function formatRetryDelay(failureCount: number, retryDelaysMs: readonly number[]) {
  if (failureCount <= 0) return '稍后';
  const index = Math.max(0, Math.min(failureCount - 1, retryDelaysMs.length - 1));
  return `${Math.round(retryDelaysMs[index] / 1000)} 秒`;
}

export function formatBytes(value: number) {
  if (!Number.isFinite(value) || value <= 0) return '未知';
  if (value >= 1024 * 1024) return `${Math.round(value / 1024 / 1024)} MiB`;
  return `${Math.round(value / 1024)} KiB`;
}

export function parseAutomationLogLine(line: string): AutomationLogEntry {
  const match = /^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s+(\S+)\s*(.*)$/.exec(line);
  if (!match) {
    return {
      raw: line,
      timestamp: '',
      action: '',
      target: '',
      desk: '',
      orderKey: '',
      food: '',
      guest: '',
      message: line,
    };
  }

  const [, timestamp, action, rest] = match;
  const tokens = rest.trim().split(/\s+/).filter(Boolean);
  const attrs = new Map<string, string>();
  let messageStart = 0;
  for (let index = 0; index < tokens.length; index += 1) {
    const separator = tokens[index].indexOf('=');
    if (separator <= 0) {
      messageStart = index;
      break;
    }
    attrs.set(tokens[index].slice(0, separator), tokens[index].slice(separator + 1));
    messageStart = index + 1;
  }

  return {
    raw: line,
    timestamp,
    action,
    target: attrs.get('target') ?? '',
    desk: attrs.get('desk') ?? '',
    orderKey: attrs.get('orderKey') ?? '',
    food: attrs.get('food') ?? '',
    guest: attrs.get('guest') ?? '',
    message: tokens.slice(messageStart).join(' '),
  };
}

export function formatDesk(deskCode: number) {
  return deskCode >= 0 ? String(deskCode + 1) : String(deskCode);
}

export function formatIngredientNamesWithQty(
  names: string[],
  ownedIngredientQty: Record<number, number>,
  ingredientIdByName: Map<string, number>,
) {
  const counts = new Map<string, number>();
  const orderedNames: string[] = [];

  for (const name of names) {
    const text = name.trim();
    if (!text) continue;
    if (!counts.has(text)) orderedNames.push(text);
    counts.set(text, (counts.get(text) ?? 0) + 1);
  }

  return orderedNames
    .map((name) => {
      const count = counts.get(name) ?? 1;
      const id = ingredientIdByName.get(name);
      const countSuffix = count > 1 ? ` x${count}` : '';
      return `${name}${countSuffix}${formatQtySuffix(id == null ? undefined : ownedIngredientQty[id])}`;
    })
    .join(', ');
}

export function formatIngredientWithQty(
  name: string,
  ownedIngredientQty: Record<number, number>,
  ingredientIdByName: Map<string, number>,
) {
  const id = ingredientIdByName.get(name);
  return `${name}${formatQtySuffix(id == null ? undefined : ownedIngredientQty[id])}`;
}

export function formatQtySuffix(qty: number | undefined) {
  return `(${qty == null || qty < 0 ? '?' : qty})`;
}
