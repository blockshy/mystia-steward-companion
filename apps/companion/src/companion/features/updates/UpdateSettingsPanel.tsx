import {
  IconAlertTriangle,
  IconArrowRight,
  IconCalendar,
  IconCheck,
  IconDownload,
  IconExternalLink,
  IconPackageImport,
  IconRefresh,
} from '@tabler/icons-react';
import ReactMarkdown from 'react-markdown';

import type { UpdateManager } from '@/companion/features/updates/useUpdateManager';
import { formatBytes } from '@/companion/formatters';
import type { UpdateReleaseInfo, UpdateStatusResponse } from '@/companion/types';
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  AccordionTrigger,
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@/components/ui-kit';

export function UpdateSettingsPanel({ updateManager }: { updateManager: UpdateManager }) {
  const status = updateManager.status;
  const remoteBusy = status?.state === 'checking'
    || status?.state === 'downloading'
    || isActiveUpdateInstallState(status?.installState ?? '');
  const actionBusy = Boolean(updateManager.busy) || remoteBusy;
  const errorDetail = updateManager.error || status?.error || '';
  const installDetail = errorDetail ? '' : status?.installMessage || '';
  const releases = status?.availableReleases ?? [];

  return (
    <div className="space-y-4" data-update-settings-panel>
      <div className="grid min-w-0 gap-4 xl:grid-cols-[minmax(0,1.45fr)_minmax(18rem,0.8fr)]">
        <Card className="min-w-0 border-primary/25 bg-primary/[0.035]">
          <CardHeader>
            <CardTitle className="flex flex-wrap items-center gap-2">
              <span>版本状态</span>
              <Badge variant={status?.hasUpdate ? 'default' : 'outline'}>
                {formatUpdateState(status)}
              </Badge>
            </CardTitle>
            <CardDescription>
              更新检测与安装包校验仍使用版本固定的发布资产，不依赖 GitHub API 配额。
            </CardDescription>
          </CardHeader>

          <CardContent className="space-y-4">
            <div className="grid min-w-0 grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)] items-center gap-2">
              <VersionBlock label="当前版本" version={status?.currentVersion || '未知'} />
              <IconArrowRight className="shrink-0 text-muted-foreground" size={20} aria-hidden="true" />
              <VersionBlock label="最新版本" version={status?.latestVersion || '未检查'} align="right" />
            </div>

            {status?.hasUpdate && (
              <div className="steward-inline-panel px-3 py-2 text-sm">
                {releases.length > 0
                  ? `本次将跨越 ${releases.length} 个公开版本，完整更新内容见下方。`
                  : '已发现可安装的新版本；完整版本说明当前不可用，但不影响下载与安装。'}
              </div>
            )}

            {errorDetail && (
              <div
                className="border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
                role="alert"
              >
                {errorDetail}
              </div>
            )}

            {installDetail && (
              <div className="steward-inline-panel px-3 py-2 text-xs text-muted-foreground" role="status">
                {installDetail}
              </div>
            )}

            <UpdateActions manager={updateManager} status={status} actionBusy={actionBusy} />
          </CardContent>
        </Card>

        <Card className="min-w-0">
          <CardHeader>
            <CardTitle>检查与安装</CardTitle>
            <CardDescription>用于确认自动检查计划、更新通道和安装包状态。</CardDescription>
          </CardHeader>
          <CardContent className="grid min-w-0 gap-2 text-xs sm:grid-cols-2 xl:grid-cols-1">
            <StatusLine label="自动检查" value={!status ? '未读取' : status.autoCheck ? '已开启' : '已关闭'} />
            <StatusLine label="更新通道" value={!status ? '未读取' : status.includePrerelease ? '含预发布版本' : '仅正式版本'} />
            <StatusLine label="最近成功检查" value={!status ? '未读取' : formatUpdateDateTime(status.lastSuccessAtUtc)} />
            <StatusLine label="下次自动检查" value={formatNextUpdateCheck(status)} />
            <StatusLine label="安装包大小" value={status?.packageSize ? formatBytes(status.packageSize) : '未知'} />
            <StatusLine
              label="连续检查失败"
              value={(status?.consecutiveFailures ?? 0) > 0 ? `${status?.consecutiveFailures} 次` : '无'}
            />
          </CardContent>
        </Card>
      </div>

      <ReleaseHistory status={status} releases={releases} manager={updateManager} />

      <p className="px-1 text-xs leading-relaxed text-muted-foreground">
        更新包会先下载到配置目录；安装阶段由独立更新程序显示进度，并在游戏退出后替换插件目录。
      </p>
    </div>
  );
}

function UpdateActions({
  manager,
  status,
  actionBusy,
}: {
  manager: UpdateManager;
  status: UpdateStatusResponse | null;
  actionBusy: boolean;
}) {
  const canDownload = Boolean(status?.enabled && status.hasUpdate && !status.staged && !actionBusy);
  const canInstall = Boolean(status?.enabled && status.staged && !actionBusy);
  const primaryAction = canInstall || status?.staged
    ? {
        label: '打开安装程序',
        icon: <IconPackageImport size={15} />,
        loading: manager.busy === 'install',
        disabled: !manager.connected || Boolean(manager.busy) || !canInstall,
        run: manager.install,
        focusKey: 'settings:updates:install',
      }
    : status?.hasUpdate
      ? {
          label: '下载更新',
          icon: <IconDownload size={15} />,
          loading: manager.busy === 'download',
          disabled: !manager.connected || Boolean(manager.busy) || !canDownload,
          run: manager.download,
          focusKey: 'settings:updates:download',
        }
      : {
          label: '检查更新',
          icon: <IconRefresh size={15} />,
          loading: manager.busy === 'check',
          disabled: !manager.connected || Boolean(manager.busy) || actionBusy,
          run: manager.check,
          focusKey: 'settings:updates:check',
        };

  return (
    <div className="flex min-w-0 flex-wrap gap-2" data-gamepad-axis="x">
      <Button
        type="button"
        size="sm"
        leftSection={primaryAction.icon}
        loading={primaryAction.loading}
        disabled={primaryAction.disabled}
        data-gamepad-focus-key={primaryAction.focusKey}
        onClick={() => void primaryAction.run()}
      >
        {primaryAction.label}
      </Button>
      {status?.hasUpdate && !status.staged && (
        <Button
          type="button"
          size="sm"
          variant="outline"
          leftSection={<IconRefresh size={14} />}
          loading={manager.busy === 'check'}
          disabled={!manager.connected || Boolean(manager.busy) || actionBusy}
          data-gamepad-focus-key="settings:updates:check"
          onClick={() => void manager.check()}
        >
          重新检查
        </Button>
      )}
      <Button
        type="button"
        size="sm"
        variant="outline"
        leftSection={<IconExternalLink size={14} />}
        disabled={!status?.releaseUrl}
        data-gamepad-focus-key="settings:updates:release-page"
        onClick={() => void manager.openReleasePage()}
      >
        最新发布页
      </Button>
    </div>
  );
}

function ReleaseHistory({
  status,
  releases,
  manager,
}: {
  status: UpdateStatusResponse | null;
  releases: UpdateReleaseInfo[];
  manager: UpdateManager;
}) {
  if (!status?.hasUpdate) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>版本更新内容</CardTitle>
          <CardDescription>发现新版本后，会在这里逐一列出当前版本到最新版本的更新说明。</CardDescription>
        </CardHeader>
        <CardContent className="flex items-center gap-2 text-sm text-muted-foreground">
          <IconCheck size={17} className="text-primary" aria-hidden="true" />
          {status?.state === 'current' ? '当前已经是最新版本。' : '检查更新后显示版本说明。'}
        </CardContent>
      </Card>
    );
  }

  if (status.releaseHistoryState !== 'ready' || releases.length === 0) {
    return (
      <Card className="border-amber-500/35 bg-amber-500/[0.04]">
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <IconAlertTriangle size={18} className="text-amber-600" aria-hidden="true" />
            完整版本说明暂不可用
          </CardTitle>
          <CardDescription>
            {status.releaseHistoryState === 'unavailable' || status.releaseHistoryState === undefined
              ? '该版本发布时还未携带累计版本目录。你仍可正常下载、校验和安装更新。'
              : '版本说明资产读取或校验失败。核心更新检测和安装包校验未受影响。'}
          </CardDescription>
        </CardHeader>
        {status.releaseHistoryError && (
          <CardContent className="break-words text-xs text-muted-foreground">
            {status.releaseHistoryError}
          </CardContent>
        )}
      </Card>
    );
  }

  const latestValue = releases.at(-1)?.tag;
  return (
    <Card className="min-w-0">
      <CardHeader>
        <CardTitle>版本更新内容</CardTitle>
        <CardDescription>
          共 {releases.length} 个版本，按版本从旧到新排列；最新版本默认展开。
        </CardDescription>
      </CardHeader>
      <CardContent className="min-w-0">
        <Accordion key={latestValue} defaultValue={latestValue}>
          {releases.map((release) => (
            <AccordionItem key={release.tag} value={release.tag}>
              <AccordionTrigger
                data-gamepad-clickable="true"
                data-gamepad-focus-key={`settings:updates:release:${release.tag}`}
              >
                <div className="grid min-w-0 flex-1 gap-1 text-left sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center sm:gap-3">
                  <div className="min-w-0">
                    <div className="flex min-w-0 flex-wrap items-center gap-2">
                      <span className="break-words font-semibold">{release.title || release.tag}</span>
                      <Badge variant="outline">{release.channel === 'preview' ? '预发布' : '正式版'}</Badge>
                    </div>
                    {release.title !== release.tag && (
                      <div className="mt-0.5 text-xs text-muted-foreground">{release.tag}</div>
                    )}
                  </div>
                  <span className="flex shrink-0 items-center gap-1 text-xs text-muted-foreground">
                    <IconCalendar size={13} aria-hidden="true" />
                    {formatUpdateDate(release.publishedAtUtc)}
                  </span>
                </div>
              </AccordionTrigger>
              <AccordionContent>
                <div className="min-w-0 space-y-3 py-1">
                  <ReleaseNotes markdown={release.notesMarkdown} />
                  <Button
                    type="button"
                    size="xs"
                    variant="outline"
                    leftSection={<IconExternalLink size={13} />}
                    data-gamepad-focus-key={`settings:updates:release-page:${release.tag}`}
                    onClick={() => void manager.openReleasePage(release.releaseUrl)}
                  >
                    查看 {release.tag} 发布页
                  </Button>
                </div>
              </AccordionContent>
            </AccordionItem>
          ))}
        </Accordion>
      </CardContent>
    </Card>
  );
}

function ReleaseNotes({ markdown }: { markdown: string }) {
  if (!markdown.trim()) {
    return <p className="text-sm text-muted-foreground">该版本没有提供更新说明。</p>;
  }

  return (
    <div className="min-w-0 break-words text-sm leading-relaxed text-foreground/90" data-release-notes>
      <ReactMarkdown
        skipHtml
        components={{
          h1: ({ children }) => <h3 className="mb-2 mt-4 text-base font-semibold first:mt-0">{children}</h3>,
          h2: ({ children }) => <h3 className="mb-2 mt-4 text-base font-semibold first:mt-0">{children}</h3>,
          h3: ({ children }) => <h4 className="mb-1.5 mt-3 font-semibold first:mt-0">{children}</h4>,
          h4: ({ children }) => <h4 className="mb-1.5 mt-3 font-semibold first:mt-0">{children}</h4>,
          p: ({ children }) => <p className="my-2 first:mt-0 last:mb-0">{children}</p>,
          ul: ({ children }) => <ul className="my-2 list-disc space-y-1 pl-5">{children}</ul>,
          ol: ({ children }) => <ol className="my-2 list-decimal space-y-1 pl-5">{children}</ol>,
          li: ({ children }) => <li className="pl-0.5">{children}</li>,
          blockquote: ({ children }) => (
            <blockquote className="my-2 border-l-2 border-primary/40 pl-3 text-muted-foreground">{children}</blockquote>
          ),
          code: ({ children }) => (
            <code className="break-all bg-muted px-1 py-0.5 font-mono text-[0.92em]">{children}</code>
          ),
          pre: ({ children }) => (
            <pre className="my-2 max-w-full overflow-x-auto bg-muted p-3 text-xs">{children}</pre>
          ),
          a: ({ children, title }) => (
            <span className="underline decoration-dotted underline-offset-2" title={title ?? undefined}>
              {children}
            </span>
          ),
          img: ({ alt }) => <span className="text-muted-foreground">[图片：{alt || '未命名'}]</span>,
        }}
      >
        {markdown}
      </ReactMarkdown>
    </div>
  );
}

function VersionBlock({
  label,
  version,
  align = 'left',
}: {
  label: string;
  version: string;
  align?: 'left' | 'right';
}) {
  return (
    <div className={`min-w-0 ${align === 'right' ? 'text-right' : ''}`}>
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="mt-1 break-all text-lg font-semibold tracking-tight sm:text-xl">{version}</div>
    </div>
  );
}

function StatusLine({ label, value }: { label: string; value: string }) {
  return (
    <div className="grid min-w-0 grid-cols-[minmax(5.5rem,auto)_minmax(0,1fr)] gap-2 border-b border-border/45 py-1.5 last:border-b-0">
      <span className="text-muted-foreground">{label}</span>
      <span className="min-w-0 break-words text-right text-foreground">{value}</span>
    </div>
  );
}

function formatUpdateState(status: UpdateStatusResponse | null): string {
  if (!status) return '等待本地 API';
  if (!status.enabled) return '已关闭';
  switch (status.installState) {
    case 'waiting': return '更新程序已打开';
    case 'preparing': return '正在准备安装';
    case 'closing-companion': return '正在关闭伴随窗口';
    case 'waiting-game': return '等待游戏退出';
    case 'terminating-game': return '正在关闭游戏';
    case 'game-closed': return '游戏已退出';
    case 'backing-up': return '正在备份';
    case 'installing': return '正在安装';
    case 'verifying': return '正在校验';
    case 'succeeded': return '安装完成';
    case 'failed': return '安装失败';
    case 'cancelled': return '已取消安装';
  }
  if (status.staged) return '已下载';
  if (status.hasUpdate) return '有新版本';
  switch (status.state) {
    case 'checking': return '检查中';
    case 'downloading': return '下载中';
    case 'current': return '已是最新';
    case 'installed': return '安装完成';
    case 'failed': return '检查失败';
    case 'disabled': return '已关闭';
    default: return '未检查';
  }
}

function isActiveUpdateInstallState(state: UpdateStatusResponse['installState']): boolean {
  return state !== '' && state !== 'succeeded' && state !== 'failed' && state !== 'cancelled';
}

function formatUpdateDateTime(value: string | null | undefined): string {
  if (!value) return '未记录';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return '未记录';
  return parsed.toLocaleString('zh-CN', { hour12: false });
}

function formatUpdateDate(value: string): string {
  if (!value) return '日期未知';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return '日期未知';
  return parsed.toLocaleDateString('zh-CN');
}

function formatNextUpdateCheck(status: UpdateStatusResponse | null): string {
  if (!status) return '未读取';
  if (!status.enabled || !status.autoCheck) return '未计划';
  return formatUpdateDateTime(status.nextCheckAtUtc);
}
