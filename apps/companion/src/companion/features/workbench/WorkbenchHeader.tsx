import { IconRefresh } from '@tabler/icons-react';
import { Badge, Button, Input, SwitchField } from '@/components/ui-kit';
import { formatRetryDelay, formatTime } from '@/companion/formatters';
import { CONNECTION_RETRY_DELAYS_MS } from '@/companion/hooks/useCompanionConnection';
import type { LocalApiSnapshot, NightBusinessContext } from '@/companion/types';
import { composeClassNames } from '@/components/ui/style';

type StatusTone = 'good' | 'bad' | 'neutral';

function formatHeaderVersion(pluginVersion: string | undefined): string {
  if (!pluginVersion) return '等待本地 API 响应';
  return pluginVersion.match(/\d+\.\d+\.\d+/)?.[0] ?? pluginVersion;
}

function StatusMetric({
  label,
  value,
  detail,
  tone,
}: {
  label: string;
  value: string;
  detail: string;
  tone: StatusTone;
}) {
  const toneClass = tone === 'good'
    ? 'text-[#4f6d38] dark:text-[#c6d59b]'
    : tone === 'bad'
      ? 'text-destructive'
      : 'text-foreground';

  return (
    <div className="min-w-0 border-l border-border/45 px-3 py-2 first:border-l-0">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className={composeClassNames('mt-0.5 truncate text-sm font-semibold', toneClass)} title={value}>
        {value}
      </div>
      <div className="mt-0.5 truncate text-xs text-muted-foreground" title={detail}>{detail}</div>
    </div>
  );
}

interface WorkbenchHeaderProps {
  endpointDraft: string;
  onEndpointDraftChange: (value: string) => void;
  apiTokenDraft: string;
  onApiTokenDraftChange: (value: string) => void;
  onApplyEndpointConnection: () => void;
  onPauseConnection: () => void;
  onRefresh: () => void;
  apiToken: string;
  connectionPaused: boolean;
  connectionFailureCount: number;
  error: string;
  lastConnectedAt: Date | null;
  loading: boolean;
  normalizedEndpoint: string;
  mousePassthroughEnabled: boolean;
  night: NightBusinessContext | null;
  snapshot: LocalApiSnapshot | null;
}

export function WorkbenchHeader({
  endpointDraft,
  onEndpointDraftChange,
  apiTokenDraft,
  onApiTokenDraftChange,
  onApplyEndpointConnection,
  onPauseConnection,
  onRefresh,
  apiToken,
  connectionPaused,
  connectionFailureCount,
  error,
  lastConnectedAt,
  loading,
  normalizedEndpoint,
  mousePassthroughEnabled,
  night,
  snapshot,
}: WorkbenchHeaderProps) {
  const connectionValue = !apiToken
    ? '未授权'
    : connectionPaused ? '已停止' : error ? '重试中' : snapshot ? '已连接' : '连接中';
  const connectionDetail = !apiToken
    ? '请输入 Mod API Token 后连接'
    : connectionPaused
      ? '点击连接恢复自动重连'
      : error
        ? `${error}；${formatRetryDelay(connectionFailureCount, CONNECTION_RETRY_DELAYS_MS)} 后重试`
        : lastConnectedAt
          ? `最近响应 ${formatTime(lastConnectedAt)}`
          : normalizedEndpoint;
  const connectionTone: StatusTone = !apiToken || connectionPaused || error ? 'bad' : snapshot ? 'good' : 'neutral';

  const headerVersion = formatHeaderVersion(snapshot?.pluginVersion);

  return (
    <div className="steward-workbench-header">
      <div className="grid min-w-0 grid-cols-[auto_minmax(0,1fr)] items-start gap-3">
        <div className="min-w-0 space-y-1">
          <div className="flex min-w-0 flex-nowrap items-baseline gap-x-2">
            <h1 className="shrink-0 whitespace-nowrap text-[1.45rem] font-bold leading-tight text-foreground">Mod 工作台</h1>
            <span className="min-w-0 truncate text-sm leading-none text-muted-foreground" title={headerVersion}>
              {headerVersion}
            </span>
          </div>
          {mousePassthroughEnabled && (
            <Badge variant="secondary">
              鼠标穿透中 · F10 解除
            </Badge>
          )}
        </div>

        <div className="flex min-w-0 flex-nowrap items-center justify-end gap-2">
          <Input
            value={endpointDraft}
            onChange={(event) => onEndpointDraftChange(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') onApplyEndpointConnection();
            }}
            spellCheck={false}
            className="min-w-[8.5rem] max-w-[15rem] flex-1 basis-[12rem]"
            inputClassName="font-mono"
          />
          <Input
            value={apiTokenDraft}
            onChange={(event) => onApiTokenDraftChange(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') onApplyEndpointConnection();
            }}
            type="password"
            placeholder="Token"
            spellCheck={false}
            autoComplete="off"
            className="min-w-[7rem] max-w-[10rem] flex-1 basis-[8rem]"
            inputClassName="font-mono"
          />
          <SwitchField
            label="连接"
            checked={!connectionPaused}
            onCheckedChange={(checked) => {
              if (checked) {
                onApplyEndpointConnection();
              } else {
                onPauseConnection();
              }
            }}
            className="h-8 shrink-0 steward-inline-panel px-2.5"
          />
          <Button size="sm" onClick={onRefresh} disabled={loading || !apiToken} className="shrink-0">
            <IconRefresh className={loading ? 'size-4 animate-spin' : 'size-4'} />
            刷新
          </Button>
        </div>
      </div>

      <div className="mt-3 grid grid-cols-3 overflow-hidden border border-border/45 steward-background-surface-45">
        <StatusMetric
          label="连接状态"
          value={connectionValue}
          detail={connectionDetail}
          tone={connectionTone}
        />
        <StatusMetric
          label="游戏运行态"
          value={snapshot?.runtimeLoaded ? '已加载' : '未加载'}
          detail={snapshot?.activeSceneName || snapshot?.status || '暂无快照'}
          tone={snapshot?.runtimeLoaded ? 'good' : 'neutral'}
        />
        <StatusMetric
          label="经营数据"
          value={`${night?.activeRareGuests.length ?? 0} 稀客 / ${night?.orders.length ?? 0} 点单`}
          detail={night?.place || night?.placeLabel || '无经营场景'}
          tone={(night?.orders.length ?? 0) > 0 ? 'good' : 'neutral'}
        />
      </div>
    </div>
  );
}
