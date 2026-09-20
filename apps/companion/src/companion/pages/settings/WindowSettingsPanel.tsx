import { IconRefresh } from '@tabler/icons-react';
import { Button, ListPanel, SettingHelpProvider } from '@/components/ui-kit';
import type { useDesktopWindowControls } from '@/companion/hooks/useDesktopWindowControls';
import {
  DEFAULT_FONT_SCALE_PERCENT,
  type CompanionPreferences,
  type LocalCompanionPreferences,
} from '@/companion/preferences';
import type { ThemeMode } from '@/lib/theme';
import {
  BackgroundOpacitySlider,
  ContentOpacitySlider,
  FontScaleSlider,
  FocusSwitchCooldownInput,
  SettingSegmentedControl,
  SwitchControl,
} from '@/companion/pages/shared';
import { DENSE_TWO_COLUMN_GRID } from '@/companion/pages/shared-constants';

export function WindowSettingsPanel({
  preferences,
  themeMode,
  desktopWindowControls,
  onLocalPreferenceChange,
  onThemeModeChange,
  supportsDesktopWindowControls,
}: {
  preferences: CompanionPreferences;
  themeMode: ThemeMode;
  desktopWindowControls: ReturnType<typeof useDesktopWindowControls>;
  onLocalPreferenceChange: (next: Partial<LocalCompanionPreferences>) => void;
  onThemeModeChange: (mode: ThemeMode) => void;
  supportsDesktopWindowControls: boolean;
}) {
  return (
    <SettingHelpProvider resetKey="WindowSettingsPanel">
      <div className="space-y-4">
        <div className={DENSE_TWO_COLUMN_GRID}>
          <ListPanel title="窗口">
            <div className="space-y-4">
              <BackgroundOpacitySlider
                value={preferences.backgroundOpacity}
                onChange={(backgroundOpacity) => onLocalPreferenceChange({ backgroundOpacity })}
              />
              <ContentOpacitySlider
                value={preferences.contentOpacity}
                onChange={(contentOpacity) => onLocalPreferenceChange({ contentOpacity })}
              />
              {supportsDesktopWindowControls ? (
                <>
                  <SettingSegmentedControl
                    label="焦点切换"
                    helpId="window-focus-switch-behavior"
                    description="使用 F8 或 RS Click 切回游戏时，可以隐藏伴随窗口，也可以让窗口保持悬浮显示。此设置只适用于桌面窗口。"
                    value={preferences.focusSwitchBehavior}
                    options={[
                      { value: 'hide', label: '隐藏窗口' },
                      { value: 'keep-visible', label: '保持悬浮' },
                    ]}
                    onChange={(focusSwitchBehavior) => onLocalPreferenceChange({ focusSwitchBehavior })}
                  />
                  <FocusSwitchCooldownInput
                    value={preferences.focusSwitchCooldownMs}
                    onChange={(focusSwitchCooldownMs) => onLocalPreferenceChange({ focusSwitchCooldownMs })}
                  />
                  <SwitchControl
                    label="始终置顶"
                    helpId="window-always-on-top"
                    description="让伴随窗口保持在普通窗口和无边框游戏上方。独占全屏仍可能覆盖伴随窗口。"
                    checked={preferences.alwaysOnTop}
                    onCheckedChange={(alwaysOnTop) => onLocalPreferenceChange({ alwaysOnTop })}
                  />
                  <SwitchControl
                    label="鼠标穿透锁定"
                    helpId="window-mouse-passthrough"
                    description="开启后鼠标点击会落到下方窗口；F10 可用时可用它解除，也可按 F8、RS Click 或使用托盘恢复。"
                    checked={desktopWindowControls.mousePassthroughEnabled}
                    disabled={!desktopWindowControls.ready || desktopWindowControls.busy}
                    onCheckedChange={(enabled) => {
                      void desktopWindowControls.setMousePassthrough(enabled);
                    }}
                  />
                  <div
                    role="status"
                    className="text-xs text-muted-foreground"
                    data-desktop-hotkey-status="true"
                  >
                    {desktopWindowControls.hotkeyStatus?.status === 'available'
                      ? 'F10 快捷键可用。'
                      : desktopWindowControls.hotkeyStatus?.status === 'unavailable'
                        ? `F10 注册失败（系统错误 ${desktopWindowControls.hotkeyStatus.errorCode ?? '未知'}）；请检查快捷键占用，或使用 F8、RS Click 和托盘恢复窗口。`
                        : desktopWindowControls.hotkeyStatus?.status === 'registering'
                          ? '正在注册 F10 快捷键。'
                          : '当前环境未提供桌面 F10 快捷键。'}
                  </div>
                  {desktopWindowControls.error && (
                    <div role="alert" className="break-words text-xs text-destructive">
                      {desktopWindowControls.error}
                    </div>
                  )}
                </>
              ) : (
                <div className="steward-inline-panel px-3 py-2 text-xs text-muted-foreground">
                  Android 端仅保留显示设置；置顶、鼠标穿透和焦点切换由桌面窗口提供。
                </div>
              )}
            </div>
          </ListPanel>

          <ListPanel title="显示">
            <div className="space-y-4">
              <SettingSegmentedControl
                label="主题"
                helpId="window-theme"
                description="选择浅色、深色，或跟随当前设备的系统外观。只影响伴随窗口显示。"
                value={themeMode}
                options={[
                  { value: 'system', label: '跟随系统' },
                  { value: 'light', label: '浅色' },
                  { value: 'dark', label: '深色' },
                ]}
                onChange={onThemeModeChange}
              />
              <div className="flex items-end gap-2">
                <div className="min-w-0 flex-1">
                  <FontScaleSlider
                    value={preferences.fontScalePercent}
                    onChange={(fontScalePercent) => onLocalPreferenceChange({ fontScalePercent })}
                  />
                </div>
                <Button
                  type="button"
                  size="icon-sm"
                  variant="ghost"
                  aria-label="恢复默认字体大小"
                  title="恢复默认字体大小"
                  disabled={preferences.fontScalePercent === DEFAULT_FONT_SCALE_PERCENT}
                  onClick={() => onLocalPreferenceChange({ fontScalePercent: DEFAULT_FONT_SCALE_PERCENT })}
                >
                  <IconRefresh size={14} aria-hidden="true" />
                </Button>
              </div>

              <SwitchControl
                label="显示调试信息"
                helpId="window-debug-details"
                description="开启后显示日志页、扫描状态、游戏数据来源、性能耗时和订单内部来源。普通使用建议保持关闭。"
                checked={preferences.showDebugDetails}
                onCheckedChange={(showDebugDetails) => onLocalPreferenceChange({ showDebugDetails })}
              />
            </div>
          </ListPanel>
        </div>
      </div>
    </SettingHelpProvider>
  );
}
