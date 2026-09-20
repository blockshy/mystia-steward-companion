import { useCallback, useState } from 'react';
import { IconCopy, IconKey, IconRefresh } from '@tabler/icons-react';
import {
  Button,
  Dialog,
  InfoLine,
  Input,
  ListPanel,
  SettingHelpField,
  SettingHelpProvider,
} from '@/components/ui-kit';
import type { LocalApiConnectionSettingsController } from '@/companion/hooks/useLocalApiConnectionSettings';
import type { LocalApiConnectionConfig } from '@/companion/types';
import { SwitchControl } from '@/companion/pages/shared';

export function HostNetworkPanel({
  endpoint,
  apiToken,
  connectionSettings,
}: {
  endpoint: string;
  apiToken: string;
  connectionSettings: LocalApiConnectionSettingsController;
}) {
  const {
    config: connectionConfig,

    lanEnabled: connectionLanEnabled,

    lanHost: connectionLanHost,

    busy: connectionBusy,

    error: connectionError,

    hostDirty: hostDraftDirty,

    dirty: connectionDraftDirty,
  } = connectionSettings;
  const connectionIdentity = JSON.stringify([endpoint, apiToken]);
  const [copyResult, setCopyResult] = useState<{ identity: string; message: string } | null>(null);
  const copyFeedback = copyResult?.identity === connectionIdentity ? copyResult.message : '';
  const [visibleTokenIdentity, setVisibleTokenIdentity] = useState<string | null>(null);
  const connectionTokenVisible = visibleTokenIdentity === connectionIdentity;
  const [tokenResetIdentity, setTokenResetIdentity] = useState<string | null>(null);
  const tokenResetDialogOpen = tokenResetIdentity === connectionIdentity;
  const setTokenResetDialogOpen = (opened: boolean) =>
    setTokenResetIdentity(opened ? connectionIdentity : null);
  const copyConnectionText = useCallback(
    async (value: string, fallbackMessage: string) => {
      if (!value) return;
      try {
        await navigator.clipboard.writeText(value);
        setCopyResult({ identity: connectionIdentity, message: '已复制。' });
      } catch {
        setCopyResult({ identity: connectionIdentity, message: fallbackMessage });
      }
    },
    [connectionIdentity, setCopyResult],
  );
  const lanEndpoints = connectionConfig?.lanEndpoints ?? [];
  const lanEndpointStatus =
    connectionBusy === 'apply' && connectionLanEnabled
      ? '应用中'
      : hostDraftDirty
        ? '应用后刷新'
        : lanEndpoints.length > 0
          ? `${lanEndpoints.length} 个可用地址`
          : '未生成';
  const lanStatusLabel = !connectionConfig
    ? '未读取'
    : connectionBusy === 'apply' && connectionDraftDirty
      ? '应用中'
      : hostDraftDirty
        ? '监听地址待应用'
        : connectionConfig.lanEnabled
          ? connectionConfig.lanRunning
            ? '已开启'
            : '未监听'
          : '未开启';
  const tokenValue = connectionConfig?.token || apiToken;
  const tokenDisplayValue = connectionTokenVisible ? tokenValue : maskToken(tokenValue);
  return (
    <SettingHelpProvider resetKey="HostNetworkPanel">
      <div className="space-y-4">
        <ListPanel title="连接">
          <div className="space-y-4">
            <div className="grid gap-2 text-sm">
              <InfoLine label="本机地址" value={connectionConfig?.localEndpoint || endpoint} mono />
              <InfoLine label="端口" value={connectionConfig ? String(connectionConfig.port) : '未读取'} />
              <InfoLine label="LAN 状态" value={lanStatusLabel} />
              <InfoLine label="LAN 地址" value={lanEndpointStatus} />
            </div>

            <SwitchControl
              label="允许局域网设备连接"
              helpId="connection-lan-enabled"
              description="允许同一可信局域网中的 Windows 或 Android 伴随窗口连接本机 Mod API。本机回环地址始终保留；不要通过公网端口映射暴露此接口。"
              checked={connectionLanEnabled}
              onCheckedChange={(enabled) => void connectionSettings.setLanEnabled(enabled)}
              disabled={!connectionSettings.writable}
            />

            <SettingHelpField
              id="connection-lan-bind-host"
              label="LAN 监听地址"
              description="修改监听地址后需要点击应用。填写 auto 会监听活动网卡的私网 IPv4，也可以填写本机活动网卡上的一个明确地址；本机回环地址始终保留。"
              disabledControl={!connectionLanEnabled || !connectionSettings.writable}
            >
              {({ helpTrigger, descriptionId }) => (
                <div className="grid gap-1 text-sm">
                  <div className="flex min-w-0 items-center gap-1.5">
                    <label htmlFor="settings-lan-bind-host" className="min-w-0 text-muted-foreground">
                      LAN 监听地址
                    </label>
                    {helpTrigger}
                  </div>
                  <Input
                    id="settings-lan-bind-host"
                    value={connectionLanHost}
                    onChange={(event) => connectionSettings.setLanHost(event.target.value)}
                    placeholder="auto"
                    disabled={!connectionLanEnabled || !connectionSettings.writable}
                    inputClassName="font-mono"
                    aria-describedby={descriptionId}
                  />
                </div>
              )}
            </SettingHelpField>

            <div className="grid gap-1.5">
              <div className="text-xs text-muted-foreground">局域网连接地址</div>
              {lanEndpoints.length > 0 && !hostDraftDirty ? (
                <div className="divide-y divide-border/50 border-y border-border/50">
                  {lanEndpoints.map((lanEndpoint) => (
                    <div
                      key={`${lanEndpoint.address}-${lanEndpoint.interfaceName}`}
                      className="flex min-w-0 items-center gap-3 py-2"
                    >
                      <div className="min-w-0 flex-1">
                        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5">
                          <code className="min-w-0 break-all text-xs font-medium text-foreground">
                            {lanEndpoint.endpoint}
                          </code>
                          {lanEndpoint.recommended && (
                            <span className="shrink-0 text-xs font-semibold text-primary">推荐</span>
                          )}
                        </div>
                        <div className="mt-0.5 text-xs text-muted-foreground">
                          {formatLanEndpointDetail(lanEndpoint)}
                        </div>
                      </div>
                      <Button
                        type="button"
                        size="icon-sm"
                        variant="ghost"
                        className="shrink-0"
                        aria-label={`复制 ${lanEndpoint.endpoint}`}
                        title="复制此地址"
                        disabled={Boolean(connectionBusy)}
                        data-gamepad-focus-key={`settings:connection:copy-lan:${lanEndpoint.address}`}
                        onClick={() => void copyConnectionText(lanEndpoint.endpoint, '无法复制 LAN 地址。')}
                      >
                        <IconCopy size={14} />
                      </Button>
                    </div>
                  ))}
                </div>
              ) : (
                <div className="border-y border-border/50 py-2 text-xs text-muted-foreground">
                  {!connectionConfig
                    ? '等待确认局域网连接配置。'
                    : hostDraftDirty
                      ? '应用监听地址后生成连接地址。'
                      : '开启局域网连接后生成可用地址。'}
                </div>
              )}
            </div>

            <label className="grid gap-1 text-sm">
              <span className="text-muted-foreground">Token</span>
              <Input
                value={tokenDisplayValue}
                readOnly
                type={connectionTokenVisible ? 'text' : 'password'}
                inputClassName="font-mono"
              />
            </label>

            <div className="flex flex-wrap gap-2" data-gamepad-axis="x">
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconRefresh size={14} />}
                loading={connectionBusy === 'refresh'}
                disabled={!connectionSettings.refreshable}
                data-gamepad-focus-key="settings:connection:refresh"
                onClick={() => void connectionSettings.refresh()}
              >
                刷新
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                disabled={!connectionSettings.writable || !connectionDraftDirty}
                data-gamepad-focus-key="settings:connection:apply"
                onClick={() => void connectionSettings.apply()}
              >
                应用
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconCopy size={14} />}
                disabled={!tokenValue || Boolean(connectionBusy)}
                data-gamepad-focus-key="settings:connection:copy-token"
                onClick={() => void copyConnectionText(tokenValue, '无法复制 Token。')}
              >
                复制 Token
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                data-gamepad-focus-key="settings:connection:toggle-token-visibility"
                onClick={() => setVisibleTokenIdentity(connectionTokenVisible ? null : connectionIdentity)}
              >
                {connectionTokenVisible ? '隐藏 Token' : '显示 Token'}
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconKey size={14} />}
                loading={connectionBusy === 'token'}
                disabled={!connectionSettings.writable}
                aria-controls="token-reset-dialog"
                aria-expanded={tokenResetDialogOpen}
                aria-haspopup="dialog"
                data-gamepad-dialog-trigger="true"
                data-gamepad-focus-key="settings:connection:reset-token"
                onClick={() => setTokenResetDialogOpen(true)}
              >
                重置 Token
              </Button>
            </div>

            {connectionSettings.status && (
              <p className="text-xs text-muted-foreground" role="status" data-connection-settings-status>
                {connectionSettings.status}
              </p>
            )}
            {copyFeedback && (
              <p className="text-xs text-muted-foreground" role="status">
                {copyFeedback}
              </p>
            )}
            {connectionError && (
              <div
                role="alert"
                className="border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
              >
                {connectionError}
              </div>
            )}
          </div>
        </ListPanel>
        <Dialog
          id="token-reset-dialog"
          opened={tokenResetDialogOpen}
          onClose={() => setTokenResetDialogOpen(false)}
          returnFocusKey="settings:connection:reset-token"
          title="重置连接 Token"
        >
          <p className="text-muted-foreground">重置后，其他设备需要重新输入新 Token 才能连接。确定继续？</p>
          <div className="flex justify-end gap-2" data-gamepad-axis="x">
            <Button
              type="button"
              size="sm"
              variant="outline"
              data-autofocus
              data-gamepad-dialog-default="true"
              data-gamepad-focus-key="settings:connection:reset-token:cancel"
              onClick={() => setTokenResetDialogOpen(false)}
            >
              取消
            </Button>
            <Button
              type="button"
              size="sm"
              variant="destructive"
              disabled={!connectionSettings.writable}
              data-gamepad-focus-key="settings:connection:reset-token:confirm"
              onClick={() => {
                setTokenResetDialogOpen(false);
                void connectionSettings.regenerateToken();
              }}
            >
              重置 Token
            </Button>
          </div>
        </Dialog>
      </div>
    </SettingHelpProvider>
  );
}

function formatLanEndpointDetail(endpoint: LocalApiConnectionConfig['lanEndpoints'][number]): string {
  const interfaceType = formatLanInterfaceType(endpoint.interfaceType);
  const interfaceLabel = endpoint.interfaceName || interfaceType || '未知网络接口';
  const details = [interfaceLabel];
  if (endpoint.interfaceName && interfaceType && endpoint.interfaceName !== interfaceType)
    details.push(interfaceType);
  if (endpoint.hasGateway) details.push('默认网关');
  if (endpoint.linkLocal) details.push('路由器不转发此地址');
  return details.join(' · ');
}

function formatLanInterfaceType(value: string): string {
  switch (value.trim().toLowerCase()) {
    case 'wireless80211':
      return '无线网卡';
    case 'ethernet':
    case 'fastethernett':
    case 'fastethernetfx':
    case 'gigabitethernet':
      return '以太网';
    case 'tunnel':
      return '隧道';
    case 'ppp':
      return 'PPP / VPN';
    default:
      return value;
  }
}

function maskToken(value: string): string {
  if (!value) return '';
  if (value.length <= 8) return '*'.repeat(value.length);
  return `${value.slice(0, 4)}${'*'.repeat(Math.max(8, value.length - 8))}${value.slice(-4)}`;
}
