import { SettingHelpProvider } from '@/components/ui-kit';
import type { CompanionDeviceAuthorityController } from '@/companion/hooks/useCompanionDeviceAuthority';
import { type CompanionPreferences, type SharedCompanionPreferences } from '@/companion/preferences';
import { SettingSegmentedControl, SwitchControl } from '@/companion/pages/shared';
import { SharedSettingsStatus } from '@/companion/pages/settings/SharedSettingsStatus';

export function ServiceDisplaySettingsPanel({
  preferences,
  serviceFocusCompact,
  deviceAuthority,
  onSharedPreferenceChange,
  onServiceFocusCompactChange,
  onOpenDevices,
}: {
  preferences: CompanionPreferences;
  serviceFocusCompact: boolean;
  deviceAuthority: CompanionDeviceAuthorityController;
  onSharedPreferenceChange: (next: Partial<SharedCompanionPreferences>) => void;
  onServiceFocusCompactChange: (value: boolean) => void;
  onOpenDevices: () => void;
}) {
  const sharedSettingsDisabled = !deviceAuthority.profileEditWritable;
  return (
    <SettingHelpProvider resetKey="ServiceDisplaySettingsPanel">
      <div className="space-y-4">
        <SwitchControl
          label="稀客专注模式默认精简"
          helpId="recommendation-focus-compact"
          description="进入稀客订单专注模式时默认使用精简显示。料理和酒水显示数量仍可在专注模式内直接调整，并会自动记住。此项仅影响当前设备。"
          checked={serviceFocusCompact}
          onCheckedChange={onServiceFocusCompactChange}
        />
        <SharedSettingsStatus
          authority={deviceAuthority}
          focusKey="settings:recommendation:open-connection"

          onOpenDevices={onOpenDevices}
        />
        <fieldset disabled={sharedSettingsDisabled} className="m-0 min-w-0 border-0 p-0">
          <SettingSegmentedControl
            label="经营中订单排序"
            helpId="recommendation-service-order-sort"
            description="点单顺序按订单进入经营的先后排列；稀客分组会把同一稀客的订单集中显示。此设置只改变页面顺序，不改变订单本身。"
            value={preferences.serviceOrderSortMode}
            options={[
              { value: 'ordered', label: '点单顺序' },
              { value: 'guest', label: '稀客分组' },
            ]}
            onChange={(serviceOrderSortMode) => onSharedPreferenceChange({ serviceOrderSortMode })}
          />
        </fieldset>
      </div>
    </SettingHelpProvider>
  );
}
