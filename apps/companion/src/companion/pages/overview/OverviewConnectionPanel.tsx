import { IconRefresh } from '@tabler/icons-react';
import { Button, Card, CardContent, Input, SwitchField } from '@/components/ui-kit';
import { composeClassNames } from '@/components/ui/style';
import { formatRetryDelay, formatTime } from '@/companion/formatters';
import { CONNECTION_RETRY_DELAYS_MS } from '@/companion/hooks/useCompanionConnection';
import { MINIMUM_MULTICOLUMN_GRID_CLASS } from '@/companion/pages/shared-constants';
import type { LocalApiSnapshot, NightBusinessContext } from '@/companion/types';

type StatusTone = 'good' | 'bad' | 'neutral';

function formatPluginVersion(pluginVersion: string | undefined): string {
  if (!pluginVersion) return '等待本地 API 响应';
  return pluginVersion.match(/\d+\.\d+\.\d+/)?.[0] ?? pluginVersion;
}

function StatusMetric({
  metric,
  label,
  value,
  detail,
  tone,
}: {
  metric: 'connection' | 'runtime' | 'business';
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
    <div
      className="min-w-0 border-t border-border/45 px-3 py-2 first:border-t-0 min-[640px]:border-l min-[640px]:border-t-0 min-[640px]:first:border-l-0"
      data-overview-connection-status-metric={metric}
    >
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className={composeClassNames('mt-0.5 min-w-0 break-words text-sm font-semibold', toneClass)} title={value}>
        {value}
      </div>
      <div className="mt-0.5 min-w-0 break-words text-xs leading-snug text-muted-foreground" title={detail}>
        {detail}
      </div>
    </div>
  );
}

function ConnectionMetadata({
  label,
  value,
  breakAll = false,
}: {
  label: string;
  value: string;
  breakAll?: boolean;
}) {
  return (
    <div className="min-w-0">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div
        className={composeClassNames(
          'mt-0.5 min-w-0 font-mono text-xs leading-snug',
          breakAll ? 'break-all' : 'break-words',
        )}
        title={value}
      >
        {value}
      </div>
    </div>
  );
}

interface OverviewConnectionPanelProps {
  endpointDraft: string;
  onEndpointDraftChange: (value: string) => void;
  apiTokenDraft: string;
  onApiTokenDraftChange: (value: string) => void;
  onApplyEndpointConnection: () => void;
  onPauseConnection: () => void;
  onResumeConnection: () => void;
  onDiscardConnectionDraft: () => void;
  connectionDraftDirty: boolean;
  onRefresh: () => void;
  apiToken: string;
  connectionPaused: boolean;
  connectionFailureCount: number;
  error: string;
  lastConnectedAt: Date | null;
  loading: boolean;
  normalizedEndpoint: string;
  night: NightBusinessContext | null;
  snapshot: LocalApiSnapshot | null;
}

export function OverviewConnectionPanel({
  endpointDraft,
  onEndpointDraftChange,
  apiTokenDraft,
  onApiTokenDraftChange,
  onApplyEndpointConnection,
  onPauseConnection,
  onResumeConnection,
  onDiscardConnectionDraft,
  connectionDraftDirty,
  onRefresh,
  apiToken,
  connectionPaused,
  connectionFailureCount,
  error,
  lastConnectedAt,
  loading,
  normalizedEndpoint,
  night,
  snapshot,
}: OverviewConnectionPanelProps) {
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
  const connectionTone: StatusTone = !apiToken || connectionPaused || error
    ? 'bad'
    : snapshot ? 'good' : 'neutral';

  return (
    <div className="space-y-3" data-overview-connection-panel="true">
      <section
        aria-label="连接与游戏状态"
        className={`${MINIMUM_MULTICOLUMN_GRID_CLASS} grid grid-cols-1 overflow-hidden border border-border/45 steward-background-surface-45 min-[640px]:grid-cols-3`}
        data-overview-connection-summary="true"
      >
        <StatusMetric
          metric="connection"
          label="连接状态"
          value={connectionValue}
          detail={connectionDetail}
          tone={connectionTone}
        />
        <StatusMetric
          metric="runtime"
          label="游戏状态"
          value={!snapshot ? '未读取' : snapshot.runtimeLoaded ? '已加载' : '未加载'}
          detail={snapshot?.activeSceneName || (snapshot ? '已收到游戏数据' : '暂无游戏数据')}
          tone={snapshot?.runtimeLoaded ? 'good' : 'neutral'}
        />
        <StatusMetric
          metric="business"
          label="经营数据"
          value={!snapshot ? '未读取' : `${night?.activeRareGuests.length ?? 0} 稀客 / ${night?.orders.length ?? 0} 点单`}
          detail={night?.place || night?.placeLabel || '无经营场景'}
          tone={(night?.orders.length ?? 0) > 0 ? 'good' : 'neutral'}
        />
      </section>

      <Card size="sm" role="region" aria-label="连接配置" data-overview-connection-configuration="true">
        <CardContent className="space-y-3">
          <div
            className={`${MINIMUM_MULTICOLUMN_GRID_CLASS} grid gap-3 min-[640px]:grid-cols-[minmax(0,1fr)_minmax(10rem,0.6fr)]`}
            data-overview-connection-fields="true"
          >
            <label className="grid min-w-0 gap-1 text-sm" htmlFor="overview-connection-endpoint">
              <span className="text-muted-foreground">API 地址（IP / 端口）</span>
              <Input
                id="overview-connection-endpoint"
                aria-label="API 地址（IP / 端口）"
                data-overview-connection-endpoint="true"
                data-gamepad-focus-key="overview:connection:endpoint"
                value={endpointDraft}
                onChange={(event) => onEndpointDraftChange(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') onApplyEndpointConnection();
                }}
                spellCheck={false}
                inputClassName="font-mono"
              />
            </label>

            <label className="grid min-w-0 gap-1 text-sm" htmlFor="overview-connection-token">
              <span className="text-muted-foreground">Mod API Token</span>
              <Input
                id="overview-connection-token"
                aria-label="Mod API Token"
                data-overview-connection-token="true"
                data-gamepad-focus-key="overview:connection:token"
                value={apiTokenDraft}
                onChange={(event) => onApiTokenDraftChange(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') onApplyEndpointConnection();
                }}
                type="password"
                placeholder="Token"
                spellCheck={false}
                autoComplete="off"
                inputClassName="font-mono"
              />
            </label>
          </div>

          <div
            className={`${MINIMUM_MULTICOLUMN_GRID_CLASS} grid min-w-0 gap-3 min-[640px]:grid-cols-[auto_minmax(0,1fr)] min-[640px]:items-end`}
            data-overview-connection-footer="true"
          >
            <div
              className="flex min-w-0 flex-wrap items-center gap-2"
              data-gamepad-axis="x"
              data-overview-connection-controls="true"
            >
              <SwitchField
                label="连接"
                checked={Boolean(apiToken) && !connectionPaused}
                disabled={!apiToken}
                onCheckedChange={(checked) => {
                  if (checked) {
                    onResumeConnection();
                  } else {
                    onPauseConnection();
                  }
                }}
                className="h-8 shrink-0 steward-inline-panel px-2.5"
                data-gamepad-focus-key="overview:connection:toggle"
              />
              <Button size="sm" disabled={!apiTokenDraft.trim() || !connectionDraftDirty} onClick={onApplyEndpointConnection}>
                应用并连接
              </Button>
              {connectionDraftDirty && (
                <Button size="sm" variant="outline" onClick={onDiscardConnectionDraft}>放弃修改</Button>
              )}
              <Button
                size="sm"
                onClick={onRefresh}
                disabled={loading || !apiToken}
                className="shrink-0"
                data-gamepad-focus-key="overview:connection:refresh"
              >
                <IconRefresh className={loading ? 'size-4 animate-spin' : 'size-4'} />
                刷新
              </Button>
            </div>

            <div
              className="grid min-w-0 grid-cols-[auto_minmax(0,1fr)] gap-3"
              data-overview-connection-metadata="true"
            >
              <ConnectionMetadata label="Mod 插件版本" value={formatPluginVersion(snapshot?.pluginVersion)} />
              <ConnectionMetadata label="当前连接地址" value={normalizedEndpoint} breakAll />
            </div>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
