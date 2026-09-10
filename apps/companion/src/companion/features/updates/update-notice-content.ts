import type { UpdateStatusResponse } from '@/companion/types';
// eslint-disable-next-line no-restricted-imports -- Node's type-strip audit uses this pure presentation module.
import { resolveUpdatePresentation } from './update-presentation.ts';

export type UpdateNoticeKind =
  | 'available'
  | 'checking'
  | 'downloading'
  | 'update-failed'
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

export function getUpdateNoticeContent(
  status: UpdateStatusResponse,
  localActivity: 'check' | 'download' | 'install' | null = null,
): UpdateNoticeContent {
  const version = status.latestTag || status.latestVersion;
  const releaseCount = status.availableReleases?.length ?? 0;
  const presentation = resolveUpdatePresentation(status, localActivity);
  if (presentation.activity) {
    return {
      kind: presentation.activity === 'install' ? 'install-active' : presentation.activity === 'download' ? 'downloading' : 'checking',
      title: `游戏端更新 ${version}：${presentation.label}`,
      detail: presentation.activity === 'install'
        ? status.installMessage || '可在更新设置中查看安装状态。'
        : '活动由所连接的游戏主机执行，完成后会自动刷新状态。',
    };
  }
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
  if (status.state === 'failed') {
    return {
      kind: 'update-failed',
      title: `游戏端更新 ${version} 未完成`,
      detail: status.error || '更新操作未完成，可在更新设置中查看详情并重试。',
    };
  }
  if (status.state === 'installed' || status.installState === 'succeeded') {
    return {
      kind: 'installed',
      title: `游戏端更新 ${version} 已安装`,
      detail: status.installMessage || '请重新启动游戏以加载新版本。',
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
