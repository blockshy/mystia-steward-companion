import { Button } from '@/components/ui-kit';
import type { CompanionDeviceAuthorityController } from '@/companion/hooks/useCompanionDeviceAuthority';
export function SharedSettingsStatus({
  authority,
  onOpenDevices,
  focusKey,
}: {
  authority: CompanionDeviceAuthorityController;
  onOpenDevices: () => void;
  focusKey: string;
}) {
  const phase = authority.profileTransactionPhase;
  const primaryDevice = authority.state?.devices.find((device) => device.isPrimary) ?? null;
  const disabledReason = !authority.ready
    ? '正在确认共享功能配置的主设备；确认前保持只读。'
    : !authority.currentDeviceIsPrimary
      ? `共享功能配置由主设备“${primaryDevice?.label || '其他设备'}”管理；切换到主设备后才能修改。`
      : '设备配置操作正在进行；完成前共享功能设置保持只读。';
  const message = authority.error
    ? `共享配置未确认：${authority.error}`
    : phase === 'reconciling'
      ? '保存结果尚未确定，正在确认 Mod 当前配置；确认前不执行新修改。'
      : phase === 'posting'
        ? '正在保存共享配置；未确认的修改尚未生效，可以继续编辑。'
        : phase === 'debouncing'
          ? '修改待保存；Mod 确认前仍使用上次已确认配置。'
          : !authority.profileEditWritable
            ? disabledReason
            : '当前共享配置已确认。';
  const needsAttention = Boolean(authority.error) || !authority.profileEditWritable;
  return (
    <div
      className={
        authority.error
          ? 'border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive'
          : 'steward-inline-panel px-3 py-2 text-xs text-muted-foreground'
      }
      role={authority.error ? 'alert' : 'status'}
      data-shared-settings-status={
        authority.error ? 'error' : phase || (needsAttention ? 'read-only' : 'confirmed')
      }
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="min-w-0 flex-1">{message}</span>
        {needsAttention && (
          <Button
            type="button"
            variant="outline"
            size="xs"
            data-gamepad-focus-key={focusKey}
            onClick={onOpenDevices}
          >
            查看设备共享
          </Button>
        )}
      </div>
    </div>
  );
}
