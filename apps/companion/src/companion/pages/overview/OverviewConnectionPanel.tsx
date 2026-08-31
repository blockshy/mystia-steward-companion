import { IconRefresh } from '@tabler/icons-react';
import { Button, Card, CardContent, InfoLine, Input, SwitchField } from '@/components/ui-kit';
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
    <div className="min-w-0 border-t border-border/45 px-3 py-2 first:border-t-0 min-[640px]:border-l min-[640px]:border-t-0 min-[640px]:first:border-l-0">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className={composeClassNames('mt-0.5 truncate text-sm font-semibold', toneClass)} title={value}>
        {value}
      </div>
      <div className="mt-0.5 truncate text-xs text-muted-foreground" title={detail}>{detail}</div>
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
    <div className="space-y-4" data-overview-connection-panel="true">
      <Card>
        <CardContent className="space-y-4 p-4">
          <div className={`${MINIMUM_MULTICOLUMN_GRID_CLASS} grid gap-3 min-[640px]:grid-cols-[minmax(0,1fr)_minmax(10rem,0.6fr)]`}>
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
            className="flex min-w-0 flex-wrap items-center gap-2"
            data-gamepad-axis="x"
            data-overview-connection-controls="true"
          >
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
              data-gamepad-focus-key="overview:connection:toggle"
            />
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

          <div className="grid gap-2 text-sm min-[640px]:grid-cols-2">
            <InfoLine label="Mod 插件版本" value={formatPluginVersion(snapshot?.pluginVersion)} mono />
            <InfoLine label="当前连接地址" value={normalizedEndpoint} mono />
          </div>
        </CardContent>
      </Card>

      <div
        className={`${MINIMUM_MULTICOLUMN_GRID_CLASS} grid grid-cols-1 overflow-hidden border border-border/45 steward-background-surface-45 min-[640px]:grid-cols-3`}
        data-overview-connection-summary="true"
      >
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
