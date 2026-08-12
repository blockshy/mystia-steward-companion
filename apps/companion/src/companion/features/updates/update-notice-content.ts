import type { UpdateStatusResponse } from '@/companion/types';

export type UpdateNoticeKind =
  | 'available'
  | 'downloaded'
  | 'install-active'
  | 'install-failed'
  | 'install-cancelled'
  | 'installed';

export interface UpdateNoticeContent {
  kind: UpdateNoticeKind;
  title: string;
  detail: string;
}

export function getUpdateNoticeContent(status: UpdateStatusResponse): UpdateNoticeContent {
  const version = status.latestTag || status.latestVersion;
  const releaseCount = status.availableReleases?.length ?? 0;
  if (status.installState === 'failed') {
    return {
      kind: 'install-failed',
      title: `游戏端更新 ${version} 安装失败`,
      detail: status.installMessage || status.error || '安装程序未完成更新，可在更新设置中重试。',
    };
  }
  if (status.installState === 'cancelled') {
    return {
      kind: 'install-cancelled',
      title: `游戏端更新 ${version} 安装已取消`,
      detail: status.installMessage || '更新包仍可使用，可在更新设置中重新打开安装程序。',
    };
  }
  if (status.state === 'installed' || status.installState === 'succeeded') {
    return {
      kind: 'installed',
      title: `游戏端更新 ${version} 已安装`,
      detail: status.installMessage || '请重新启动游戏以加载新版本。',
    };
  }
  if (isInstallActive(status.installState)) {
    return {
      kind: 'install-active',
      title: `游戏端更新 ${version} 的安装程序已打开`,
      detail: status.installMessage || '可在更新设置中查看安装状态。',
    };
  }
  if (status.staged) {
    return {
      kind: 'downloaded',
      title: `游戏端更新 ${version} 已下载`,
      detail: '更新包已暂存，可在更新设置中打开安装程序。',
    };
  }
  return {
    kind: 'available',
    title: `发现游戏端更新 ${version}`,
    detail: releaseCount > 1
      ? `本次跨越 ${releaseCount} 个公开版本，可在更新设置中逐一查看更新内容并手动下载。`
      : '这是所连接游戏主机上的 Mod 更新，可在更新设置中查看版本并手动下载。',
  };
}

function isInstallActive(state: UpdateStatusResponse['installState']): boolean {
  return state !== '' && state !== 'succeeded' && state !== 'failed' && state !== 'cancelled';
}
