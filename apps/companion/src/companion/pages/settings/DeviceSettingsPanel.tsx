import { useState } from 'react';
import { IconCrown, IconDeviceDesktop, IconDeviceMobile, IconRefresh, IconTrash } from '@tabler/icons-react';
import { Button, Dialog, Input, ListPanel, SettingHelpProvider } from '@/components/ui-kit';
import type { CompanionDeviceAuthorityController } from '@/companion/hooks/useCompanionDeviceAuthority';

export function DeviceSettingsPanel({
  endpoint,
  apiToken,
  deviceAuthority,
}: {
  endpoint: string;
  apiToken: string;
  deviceAuthority: CompanionDeviceAuthorityController;
}) {
  const connectionIdentity = JSON.stringify([endpoint, apiToken]);
  const [primaryDeviceCandidateId, setPrimaryDeviceCandidateId] = useState('');
  const [deviceLabelEdit, setDeviceLabelEdit] = useState<{
    identity: string;
    label: string;
  } | null>(null);
  const currentDevice = deviceAuthority.state?.devices.find((device) => device.isCurrent) ?? null;
  const deviceLabelIdentity = JSON.stringify([
    connectionIdentity,
    currentDevice?.deviceId,
    currentDevice?.label,
  ]);
  const deviceLabelDraft =
    deviceLabelEdit?.identity === deviceLabelIdentity ? deviceLabelEdit.label : (currentDevice?.label ?? '');
  const primaryDevice = deviceAuthority.state?.devices.find((device) => device.isPrimary) ?? null;
  const primaryDeviceCandidate =
    deviceAuthority.state?.devices.find((device) => device.deviceId === primaryDeviceCandidateId) ?? null;
  return (
    <SettingHelpProvider resetKey="DeviceSettingsPanel">
      <div className="space-y-4">
        <ListPanel title="伴随设备与生效配置">
          <div className="space-y-4" data-device-authority-content>
            <div className="steward-inline-panel px-3 py-2 text-xs text-muted-foreground">
              {deviceAuthority.ready
                ? deviceAuthority.currentDeviceIsPrimary
                  ? '当前设备是主设备；本设备的推荐、自动化和游戏界面辅助配置为唯一生效配置。'
                  : `当前由“${primaryDevice?.label || '其他设备'}”提供生效配置；本设备的共享功能设置为只读。`
                : '正在确认主设备和生效配置；确认前不会执行自动化或发布游戏界面辅助目标。'}
            </div>

            {currentDevice && (
              <div className="grid gap-2 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-end">
                <label className="grid gap-1 text-sm">
                  <span className="text-muted-foreground">当前设备名称</span>
                  <Input
                    value={deviceLabelDraft}
                    maxLength={48}
                    disabled={Boolean(deviceAuthority.busy)}
                    onChange={(event) =>
                      setDeviceLabelEdit({
                        identity: deviceLabelIdentity,
                        label: event.target.value,
                      })
                    }
                  />
                </label>
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={
                    Boolean(deviceAuthority.busy) ||
                    !deviceLabelDraft.trim() ||
                    deviceLabelDraft.trim() === currentDevice.label
                  }
                  loading={deviceAuthority.busy === 'rename'}
                  data-gamepad-focus-key="settings:connection:device-rename"
                  onClick={() =>
                    void deviceAuthority.renameCurrent(deviceLabelDraft.trim()).catch(() => undefined)
                  }
                >
                  保存名称
                </Button>
              </div>
            )}

            <div className="divide-y divide-border/50 border-y border-border/50">
              {(deviceAuthority.state?.devices ?? []).map((device) => {
                const profileMatchesPrimary = device.profileHash === deviceAuthority.state?.activeProfileHash;
                return (
                  <div
                    key={device.deviceId}
                    className="grid gap-2 py-3 md:grid-cols-[minmax(0,1fr)_auto] md:items-center"
                    data-device-authority-device={device.deviceId}
                  >
                    <div className="min-w-0">
                      <div className="flex min-w-0 flex-wrap items-center gap-1.5">
                        {device.platform === 'android' ? (
                          <IconDeviceMobile size={15} aria-hidden="true" />
                        ) : (
                          <IconDeviceDesktop size={15} aria-hidden="true" />
                        )}
                        <span className="min-w-0 truncate text-sm font-medium">{device.label}</span>
                        {device.isPrimary && (
                          <span className="inline-flex items-center gap-1 text-xs font-semibold text-primary">
                            <IconCrown size={13} aria-hidden="true" />
                            主设备
                          </span>
                        )}
                        {device.isCurrent && <span className="text-xs text-muted-foreground">当前设备</span>}
                        <span
                          className={device.online ? 'text-xs text-success' : 'text-xs text-muted-foreground'}
                        >
                          {device.online ? '在线' : '离线'}
                        </span>
                        {device.syncPending && <span className="text-xs text-warning">待应用同步</span>}
                      </div>
                      <div className="mt-1 break-all text-xs text-muted-foreground">
                        {formatDevicePlatform(device.platform)} · v{device.appVersion} · 配置 #
                        {device.profileRevision}
                        {profileMatchesPrimary ? ' · 与主设备一致' : ' · 与主设备不同'}
                      </div>
                    </div>
                    <div className="flex flex-wrap gap-2 md:justify-end" data-gamepad-axis="x">
                      {!device.isPrimary && (
                        <>
                          <Button
                            type="button"
                            size="sm"
                            variant="outline"
                            disabled={
                              Boolean(deviceAuthority.busy) || device.syncPending || profileMatchesPrimary
                            }
                            loading={deviceAuthority.busy === 'sync'}
                            data-gamepad-focus-key={`settings:connection:device-sync:${device.deviceId}`}
                            onClick={() =>
                              void deviceAuthority.syncFromPrimary(device.deviceId).catch(() => undefined)
                            }
                          >
                            同步配置
                          </Button>
                          <Button
                            type="button"
                            size="sm"
                            variant="outline"
                            leftSection={<IconCrown size={14} />}
                            disabled={Boolean(deviceAuthority.busy) || !device.online || device.syncPending}
                            data-gamepad-dialog-trigger="true"
                            data-gamepad-focus-key={`settings:connection:device-primary:${device.deviceId}`}
                            onClick={() => setPrimaryDeviceCandidateId(device.deviceId)}
                          >
                            设为主设备
                          </Button>
                        </>
                      )}
                      {!device.isPrimary && !device.isCurrent && !device.online && (
                        <Button
                          type="button"
                          size="icon-sm"
                          variant="ghost"
                          aria-label={`移除设备 ${device.label}`}
                          title="移除离线设备"
                          disabled={Boolean(deviceAuthority.busy)}
                          loading={deviceAuthority.busy === 'forget'}
                          data-gamepad-focus-key={`settings:connection:device-forget:${device.deviceId}`}
                          onClick={() => void deviceAuthority.forget(device.deviceId).catch(() => undefined)}
                        >
                          <IconTrash size={14} />
                        </Button>
                      )}
                    </div>
                  </div>
                );
              })}
              {deviceAuthority.ready && (deviceAuthority.state?.devices.length ?? 0) === 0 && (
                <div className="py-3 text-xs text-muted-foreground">尚未注册伴随设备。</div>
              )}
            </div>

            <div className="flex flex-wrap items-center gap-2" data-gamepad-axis="x">
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconRefresh size={14} />}
                loading={deviceAuthority.busy === 'refresh' || deviceAuthority.busy === 'register'}
                disabled={!apiToken || Boolean(deviceAuthority.busy)}
                data-gamepad-focus-key="settings:connection:devices-refresh"
                onClick={() => void deviceAuthority.refresh()}
              >
                刷新设备
              </Button>
              <span className="text-xs text-muted-foreground">
                生效配置版本 #{deviceAuthority.authorityRevision || '未确认'}
              </span>
            </div>

            {deviceAuthority.error && (
              <div className="border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
                {deviceAuthority.error}
              </div>
            )}
          </div>
        </ListPanel>
        <Dialog
          id="primary-device-dialog"
          opened={Boolean(primaryDeviceCandidate)}
          onClose={() => setPrimaryDeviceCandidateId('')}
          returnFocusKey={
            primaryDeviceCandidateId
              ? `settings:connection:device-primary:${primaryDeviceCandidateId}`
              : 'settings:connection:devices-refresh'
          }
          title="切换主设备"
        >
          <div className="space-y-3 text-muted-foreground">
            <p>
              切换后将立即使用“{primaryDeviceCandidate?.label ?? ''}
              ”保存的推荐、自动化和游戏界面辅助配置。
            </p>
            {primaryDeviceCandidate &&
              primaryDeviceCandidate.profileHash !== deviceAuthority.state?.activeProfileHash && (
                <div className="border border-warning/35 bg-warning/10 px-3 py-2 text-xs text-warning">
                  目标设备的配置与当前主设备不同。切换会停止现有自动化控制权并清空已发布的游戏界面辅助目标。
                </div>
              )}
          </div>
          <div className="flex justify-end gap-2" data-gamepad-axis="x">
            <Button
              type="button"
              size="sm"
              variant="outline"
              data-autofocus
              data-gamepad-dialog-default="true"
              data-gamepad-focus-key="settings:connection:primary:cancel"
              onClick={() => setPrimaryDeviceCandidateId('')}
            >
              取消
            </Button>
            <Button
              type="button"
              size="sm"
              disabled={!primaryDeviceCandidate || Boolean(deviceAuthority.busy)}
              loading={deviceAuthority.busy === 'primary'}
              data-gamepad-focus-key="settings:connection:primary:confirm"
              onClick={() => {
                if (!primaryDeviceCandidate) return;
                const deviceId = primaryDeviceCandidate.deviceId;
                void deviceAuthority
                  .setPrimary(deviceId)
                  .then(() => setPrimaryDeviceCandidateId(''))
                  .catch(() => undefined);
              }}
            >
              确认切换
            </Button>
          </div>
        </Dialog>
      </div>
    </SettingHelpProvider>
  );
}

function formatDevicePlatform(platform: 'windows' | 'android' | 'browser'): string {
  switch (platform) {
    case 'windows':
      return 'Windows';
    case 'android':
      return 'Android';
    case 'browser':
      return '浏览器预览';
  }
}
