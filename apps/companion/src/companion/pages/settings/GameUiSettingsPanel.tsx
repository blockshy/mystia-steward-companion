import { useCallback, useState } from 'react';
import { IconAlertTriangle } from '@tabler/icons-react';
import { Button, Input, ListPanel, SettingHelpField, SettingHelpProvider } from '@/components/ui-kit';
import type { CompanionDeviceAuthorityController } from '@/companion/hooks/useCompanionDeviceAuthority';
import {
  DEFAULT_NORMAL_TARGET_HIGHLIGHT_COLOR,
  DEFAULT_RARE_TARGET_HIGHLIGHT_COLOR,
  normalizeTargetHighlightColor,
  type CompanionPreferences,
  type SharedCompanionPreferences,
} from '@/companion/preferences';
import { SwitchControl } from '@/companion/pages/shared';
import { SharedSettingsStatus } from '@/companion/pages/settings/SharedSettingsStatus';

export function GameUiSettingsPanel({
  preferences,
  deviceAuthority,
  onSharedPreferenceChange,
  onOpenDevices,
}: {
  preferences: CompanionPreferences;
  deviceAuthority: CompanionDeviceAuthorityController;
  onSharedPreferenceChange: (next: Partial<SharedCompanionPreferences>) => void;
  onOpenDevices: () => void;
}) {
  const sharedSettingsDisabled = !deviceAuthority.profileEditWritable;
  return (
    <SettingHelpProvider resetKey="GameUiSettingsPanel">
      <div className="space-y-4">
        <div
          role="note"
          aria-label="实验性功能风险提示"
          className="border border-destructive/40 bg-destructive/10 px-3 py-2"
          data-experimental-risk-notice="true"
        >
          <div className="flex items-start gap-2.5">
            <IconAlertTriangle size={18} className="mt-0.5 shrink-0 text-destructive" aria-hidden="true" />
            <div className="min-w-0">
              <div className="text-sm font-semibold text-foreground">实验性功能风险提示</div>
              <div className="text-xs leading-relaxed text-muted-foreground">
                加料料理选项会直接改变游戏状态，存在一定风险。
              </div>
            </div>
          </div>
        </div>
        <SharedSettingsStatus
          authority={deviceAuthority}
          focusKey="settings:experimental:open-connection"

          onOpenDevices={onOpenDevices}
        />
        <fieldset disabled={sharedSettingsDisabled} className="m-0 min-w-0 border-0 p-0">
          <ListPanel title="游戏界面辅助">
            <div className="grid grid-cols-1 gap-5 min-[900px]:grid-cols-2">
              <div className="space-y-4 border-l pl-3">
                <div className="text-sm font-medium">稀客目标</div>
                <SwitchControl
                  label="稀客游戏界面置顶推荐"
                  helpId="recommendation-rare-game-ui-pinning"
                  description="打开游戏的料理或酒水选择界面时，把当前稀客目标的推荐材料、料理和酒水排到前面并显示稀客目标色。此功能不修改库存。"
                  checked={preferences.rareGameUiPinningEnabled}
                  onCheckedChange={(rareGameUiPinningEnabled) =>
                    onSharedPreferenceChange({ rareGameUiPinningEnabled })
                  }
                />
                <div className="border-l pl-3">
                  <SwitchControl
                    label="稀客加料料理选项"
                    helpId="recommendation-rare-recipe-variant"
                    description="稀客目标料理含加料时，在制作页面显示独立选项。选择后只加入该方案的加料，并按游戏规则扣除材料；基础料理保持原配方，选项使用稀客目标色。"
                    checked={preferences.rareRecipeVariantEnabled}
                    disabled={!preferences.rareGameUiPinningEnabled}
                    status={
                      !preferences.rareGameUiPinningEnabled ? '需先开启稀客游戏界面置顶推荐' : undefined
                    }
                    onCheckedChange={(rareRecipeVariantEnabled) =>
                      onSharedPreferenceChange({ rareRecipeVariantEnabled })
                    }
                  />
                </div>
                <SwitchControl
                  label="稀客目标厨具高亮"
                  helpId="recommendation-rare-cooker-highlight"
                  description="高亮当前稀客主方案需要的已摆放厨具。此功能只改变可见提示，不自动操作厨具。"
                  checked={preferences.rareCookerHighlightEnabled}
                  onCheckedChange={(rareCookerHighlightEnabled) =>
                    onSharedPreferenceChange({ rareCookerHighlightEnabled })
                  }
                />
                <SwitchControl
                  label="稀客目标桌位高亮"
                  helpId="recommendation-rare-seat-highlight"
                  description="高亮当前稀客目标的桌位；不影响游戏自身的选中效果，也不操作顾客。"
                  checked={preferences.rareSeatHighlightEnabled}
                  onCheckedChange={(rareSeatHighlightEnabled) =>
                    onSharedPreferenceChange({ rareSeatHighlightEnabled })
                  }
                />
                <SwitchControl
                  label="稀客目标订单高亮"
                  helpId="recommendation-rare-order-highlight"
                  description="高亮游戏左下 HUD 稀客订单卡片和投掷送达面板中的稀客目标订单；不切换游戏自身焦点。"
                  checked={preferences.rareOrderHighlightEnabled}
                  onCheckedChange={(rareOrderHighlightEnabled) =>
                    onSharedPreferenceChange({ rareOrderHighlightEnabled })
                  }
                />
                <TargetHighlightColorField
                  kindLabel="稀客"
                  helpId="recommendation-rare-highlight-color"
                  value={preferences.rareTargetHighlightColor}
                  defaultValue={DEFAULT_RARE_TARGET_HIGHLIGHT_COLOR}
                  onChange={(rareTargetHighlightColor) =>
                    onSharedPreferenceChange({ rareTargetHighlightColor })
                  }
                />
              </div>
              <div className="space-y-4 border-l pl-3">
                <div className="text-sm font-medium">普客目标</div>
                <SwitchControl
                  label="普客游戏界面置顶推荐"
                  helpId="recommendation-normal-game-ui-pinning"
                  description="打开游戏的料理或酒水选择界面时，把当前普客目标的推荐材料、料理和酒水排到前面并显示普客目标色。此功能不修改库存。"
                  checked={preferences.normalGameUiPinningEnabled}
                  onCheckedChange={(normalGameUiPinningEnabled) =>
                    onSharedPreferenceChange({ normalGameUiPinningEnabled })
                  }
                />
                <div className="border-l pl-3">
                  <SwitchControl
                    label="普客加料料理选项"
                    helpId="recommendation-normal-recipe-variant"
                    description="普客目标料理含加料时，在制作页面显示独立选项。选择后只加入该方案的加料，并按游戏规则扣除材料；基础料理保持原配方，选项使用普客目标色。"
                    checked={preferences.normalRecipeVariantEnabled}
                    disabled={!preferences.normalGameUiPinningEnabled}
                    status={
                      !preferences.normalGameUiPinningEnabled ? '需先开启普客游戏界面置顶推荐' : undefined
                    }
                    onCheckedChange={(normalRecipeVariantEnabled) =>
                      onSharedPreferenceChange({ normalRecipeVariantEnabled })
                    }
                  />
                </div>
                <SwitchControl
                  label="普客目标厨具高亮"
                  helpId="recommendation-normal-cooker-highlight"
                  description="高亮当前普客主方案需要的已摆放厨具。此功能只改变可见提示，不自动操作厨具。"
                  checked={preferences.normalCookerHighlightEnabled}
                  onCheckedChange={(normalCookerHighlightEnabled) =>
                    onSharedPreferenceChange({ normalCookerHighlightEnabled })
                  }
                />
                <SwitchControl
                  label="普客目标桌位高亮"
                  helpId="recommendation-normal-seat-highlight"
                  description="高亮当前普客目标的桌位；不影响游戏自身的选中效果，也不操作顾客。"
                  checked={preferences.normalSeatHighlightEnabled}
                  onCheckedChange={(normalSeatHighlightEnabled) =>
                    onSharedPreferenceChange({ normalSeatHighlightEnabled })
                  }
                />
                <SwitchControl
                  label="普客目标订单高亮"
                  helpId="recommendation-normal-order-highlight"
                  description="高亮游戏左下 HUD 普客订单卡片和投掷送达面板中的普客目标订单；不切换游戏自身焦点。"
                  checked={preferences.normalOrderHighlightEnabled}
                  onCheckedChange={(normalOrderHighlightEnabled) =>
                    onSharedPreferenceChange({ normalOrderHighlightEnabled })
                  }
                />
                <TargetHighlightColorField
                  kindLabel="普客"
                  helpId="recommendation-normal-highlight-color"
                  value={preferences.normalTargetHighlightColor}
                  defaultValue={DEFAULT_NORMAL_TARGET_HIGHLIGHT_COLOR}
                  onChange={(normalTargetHighlightColor) =>
                    onSharedPreferenceChange({ normalTargetHighlightColor })
                  }
                />
              </div>
            </div>
          </ListPanel>
        </fieldset>
      </div>
    </SettingHelpProvider>
  );
}

function TargetHighlightColorField({
  kindLabel,
  helpId,
  value,
  defaultValue,
  onChange,
}: {
  kindLabel: string;
  helpId: string;
  value: string;
  defaultValue: string;
  onChange: (value: string) => void;
}) {
  const [draft, setDraft] = useState<string | null>(null);
  const displayedValue = draft ?? value;

  const commitDraft = useCallback(() => {
    if (/^#[0-9A-Fa-f]{6}$/.test(displayedValue)) {
      onChange(normalizeTargetHighlightColor(displayedValue, defaultValue));
    }
    setDraft(null);
  }, [defaultValue, displayedValue, onChange]);

  return (
    <SettingHelpField
      id={helpId}
      label={`${kindLabel}高亮色`}
      description={`设置${kindLabel}目标在料理、材料、酒水、厨具、桌位和订单区域使用的基础颜色。格式固定为 #RRGGBB。`}
    >
      {({ helpTrigger, descriptionId }) => (
        <div className="space-y-2">
          <div className="flex min-w-0 items-center gap-1.5 text-sm font-medium">
            <span>{kindLabel}高亮色</span>
            {helpTrigger}
          </div>
          <div className="flex items-center gap-2">
            <Input
              type="color"
              value={value}
              aria-label={`${kindLabel}高亮色选择器`}
              aria-describedby={descriptionId}
              className="w-10 shrink-0"
              inputClassName="h-8 cursor-pointer p-1"
              onChange={(event) => {
                setDraft(null);
                onChange(event.currentTarget.value.toUpperCase());
              }}
            />
            <Input
              value={displayedValue}
              maxLength={7}
              spellCheck={false}
              aria-label={`${kindLabel}高亮色十六进制值`}
              aria-describedby={descriptionId}
              className="min-w-0 flex-1"
              inputClassName="h-8 font-mono uppercase"
              onChange={(event) => setDraft(event.currentTarget.value.toUpperCase())}
              onBlur={commitDraft}
              onKeyDown={(event) => {
                if (event.key === 'Enter') event.currentTarget.blur();
                if (event.key === 'Escape') {
                  event.preventDefault();
                  setDraft(null);
                }
              }}
            />
            <Button
              type="button"
              size="sm"
              variant="outline"
              className="shrink-0"
              disabled={value === defaultValue}
              onClick={() => {
                setDraft(null);
                onChange(defaultValue);
              }}
            >
              恢复
            </Button>
          </div>
        </div>
      )}
    </SettingHelpField>
  );
}
