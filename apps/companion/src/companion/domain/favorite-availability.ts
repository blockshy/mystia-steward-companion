export interface FavoriteAvailability {
  canRefresh: boolean;
  canWrite: boolean;
  current: boolean;
  reason: string;
}

export function buildFavoriteAvailability(input: {
  authorized: boolean;
  connected: boolean;
  confirmed: boolean;
  busy: boolean;
  refreshing: boolean;
  readError: string;
}): FavoriteAvailability {
  const current = input.authorized && input.connected && input.confirmed && !input.readError;
  return {
    canRefresh: input.authorized && input.connected && !input.busy && !input.refreshing,
    canWrite: current && !input.busy,
    current,
    reason: !input.authorized ? '连接 Mod 后才能读取和修改收藏。'
      : !input.connected ? '连接尚未确认；已有收藏为上次读取结果，恢复连接后才能修改。'
        : input.busy ? '收藏修改正在处理中。'
          : input.readError ? '收藏数据未同步成功；确认当前收藏后才能修改。'
            : !input.confirmed ? '正在确认当前连接的收藏数据。' : '',
  };
}
