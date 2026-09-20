import { InfoLine, ListPanel, SettingHelpProvider } from '@/components/ui-kit';
import { type CompanionPreferences, type LocalCompanionPreferences } from '@/companion/preferences';
import { SwitchControl } from '@/companion/pages/shared';
import { DENSE_TWO_COLUMN_GRID_TIGHT } from '@/companion/pages/shared-constants';

export function InputSettingsPanel({
  preferences,
  onLocalPreferenceChange,
  supportsDesktopWindowControls,
}: {
  preferences: CompanionPreferences;
  onLocalPreferenceChange: (next: Partial<LocalCompanionPreferences>) => void;
  supportsDesktopWindowControls: boolean;
}) {
  return (
    <SettingHelpProvider resetKey="InputSettingsPanel">
      <div className="space-y-4">
        <SwitchControl
          label="手柄导航"
          helpId="window-gamepad-navigation"
          description="控制伴随窗口内的方向、确认、返回、切页、滚动和收藏操作。关闭后，F8 与 RS Click 的窗口焦点切换仍然有效。"
          checked={preferences.gamepadNavigationEnabled}
          onCheckedChange={(gamepadNavigationEnabled) =>
            onLocalPreferenceChange({ gamepadNavigationEnabled })
          }
        />
        <ListPanel title="快捷键">
          <div className={`${DENSE_TWO_COLUMN_GRID_TIGHT} text-sm`}>
            {supportsDesktopWindowControls && (
              <InfoLine label="F8" value="在游戏与独立窗口之间切换焦点或重新显示伴随窗口" />
            )}
            {supportsDesktopWindowControls && (
              <InfoLine label="F10" value="注册成功后切换鼠标穿透；状态见设置中的窗口选项" />
            )}
            {supportsDesktopWindowControls && (
              <InfoLine label="RS Click" value="手柄默认在游戏与独立窗口之间切换" />
            )}
            <InfoLine
              label="手柄导航"
              value="左摇杆/十字键移动，A 确认，B 关闭/返回，X 收藏，Y 专注模式，LB/RB 切页，LT/RT 滚动"
            />
            <InfoLine label="专注模式" value="Y 进入专注模式或切换精简模式，X 收藏当前推荐项" />
            {supportsDesktopWindowControls && (
              <InfoLine label="窗口关闭" value="关闭按钮会隐藏到托盘；托盘菜单可重新显示或退出" />
            )}
          </div>
        </ListPanel>
      </div>
    </SettingHelpProvider>
  );
}
