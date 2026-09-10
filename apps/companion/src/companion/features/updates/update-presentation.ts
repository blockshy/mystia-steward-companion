import type { UpdateInstallState, UpdateStatusResponse } from '@/companion/types';

type UpdateActivity = 'check' | 'download' | 'install';

export interface UpdatePresentation {
  label: string;
  activity: UpdateActivity | null;
  failed: boolean;
}

const INSTALL_ACTIVITY_LABELS: Partial<Record<UpdateInstallState, string>> = {
  waiting: '更新程序已打开',
  preparing: '正在准备安装',
  'closing-companion': '正在关闭伴随窗口',
  'waiting-game': '等待游戏退出',
  'terminating-game': '正在关闭游戏',
  'game-closed': '游戏已退出',
  'backing-up': '正在备份',
  installing: '正在安装',
  verifying: '正在校验',
};

/** 活动阶段先于已缓存的版本和安装包状态；没有阶段信息时不猜测失败发生在哪一步。 */
export function resolveUpdatePresentation(
  status: UpdateStatusResponse | null,
  localActivity: UpdateActivity | null = null,
): UpdatePresentation {
  const installLabel = status && INSTALL_ACTIVITY_LABELS[status.installState];
  if (installLabel) return { label: installLabel, activity: 'install', failed: false };
  const activity = status?.state === 'checking' ? 'check'
    : status?.state === 'downloading' ? 'download' : localActivity;
  if (activity) {
    return {
      label: activity === 'check' ? '检查中' : activity === 'download' ? '下载中' : '正在打开安装程序',
      activity,
      failed: false,
    };
  }
  if (!status) return { label: '等待本地 API', activity: null, failed: false };
  let label: string;
  if (status.installState === 'failed') label = '安装失败';
  else if (status.installState === 'cancelled') label = '已取消安装';
  else if (status.state === 'failed') label = '更新失败';
  else if (status.installState === 'succeeded' || status.state === 'installed') label = '安装完成';
  else if (!status.enabled || status.state === 'disabled') label = '已关闭';
  else if (status.staged) label = '已下载';
  else if (status.hasUpdate) label = '有新版本';
  else if (status.state === 'current') label = '已是最新';
  else label = '未检查';
  return { label, activity: null, failed: status.installState === 'failed' || (status.installState !== 'cancelled' && status.state === 'failed') };
}
