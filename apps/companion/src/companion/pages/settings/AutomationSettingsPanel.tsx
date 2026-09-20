import { useCallback } from 'react';
import { IconAlertTriangle } from '@tabler/icons-react';
import { ListPanel, SettingHelpProvider } from '@/components/ui-kit';
import type { CompanionDeviceAuthorityController } from '@/companion/hooks/useCompanionDeviceAuthority';
import {
  MAX_AUTO_ROLLBACKS_LIMIT,
  MAX_AUTO_STEP_RETRIES_LIMIT,
  MAX_NORMAL_AUTO_ORDER_CONCURRENCY,
  MAX_RARE_AUTO_ORDER_CONCURRENCY,
  MIN_AUTO_ORDER_CONCURRENCY,
  MIN_AUTO_ROLLBACKS,
  MIN_AUTO_STEP_RETRIES,
  type CompanionPreferences,
  type SharedCompanionPreferences,
} from '@/companion/preferences';
import { AutomationSliderField, SwitchControl } from '@/companion/pages/shared';
import { DENSE_TWO_COLUMN_GRID } from '@/companion/pages/shared-constants';
import { SharedSettingsStatus } from '@/companion/pages/settings/SharedSettingsStatus';

export function AutomationSettingsPanel({
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
  const setRareBeverageDelivery = useCallback(
    (enabled: boolean) => {
      onSharedPreferenceChange(
        enabled
          ? { autoPrepTakeBeverage: true, autoPrepCompleteOrder: true }
          : { autoPrepTakeBeverage: false },
      );
    },
    [onSharedPreferenceChange],
  );
  const setRareFoodDelivery = useCallback(
    (enabled: boolean) => {
      onSharedPreferenceChange(
        enabled
          ? { autoPrepCollectCooking: true, autoPrepCompleteOrder: true }
          : { autoPrepCollectCooking: false },
      );
    },
    [onSharedPreferenceChange],
  );
  const setRareOrderCompletion = useCallback(
    (enabled: boolean) => {
      onSharedPreferenceChange(
        enabled
          ? { autoPrepCompleteOrder: true }
          : {
              autoPrepCompleteOrder: false,
              autoPrepTakeBeverage: false,
              autoPrepCollectCooking: false,
            },
      );
    },
    [onSharedPreferenceChange],
  );
  const setNormalBeverageDelivery = useCallback(
    (enabled: boolean) => {
      onSharedPreferenceChange(
        enabled
          ? { autoNormalTakeBeverage: true, autoNormalCompleteOrder: true }
          : { autoNormalTakeBeverage: false },
      );
    },
    [onSharedPreferenceChange],
  );
  const setNormalFoodDelivery = useCallback(
    (enabled: boolean) => {
      onSharedPreferenceChange(
        enabled
          ? { autoNormalDeliverFood: true, autoNormalCompleteOrder: true }
          : { autoNormalDeliverFood: false },
      );
    },
    [onSharedPreferenceChange],
  );
  const setNormalOrderCompletion = useCallback(
    (enabled: boolean) => {
      onSharedPreferenceChange(
        enabled
          ? { autoNormalCompleteOrder: true }
          : {
              autoNormalCompleteOrder: false,
              autoNormalTakeBeverage: false,
              autoNormalDeliverFood: false,
            },
      );
    },
    [onSharedPreferenceChange],
  );
  return (
    <SettingHelpProvider resetKey="AutomationSettingsPanel">
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
                自动化会直接改变游戏状态，存在一定风险。
              </div>
            </div>
          </div>
        </div>
        <SharedSettingsStatus
          authority={deviceAuthority}
          focusKey="settings:experimental:open-connection"

          onOpenDevices={onOpenDevices}
        />
        <fieldset disabled={sharedSettingsDisabled} className="m-0 min-w-0 space-y-4 border-0 p-0">
          <ListPanel title="自动化总控">
            <div className="space-y-4">
              <SwitchControl
                label="启用自动化（实验性）"
                helpId="automation-enabled"
                description="关闭后会停止新动作并使排队命令失效；已开锅任务继续由游戏制作，后续送达或评价会停在尚未执行的步骤，重新开启后可继续。玩家在暂停期间取走或替换成品时改为手动交接。教学经营会保留开关设置但暂停全部自动化动作。"
                checked={preferences.automationEnabled}
                onCheckedChange={(automationEnabled) => onSharedPreferenceChange({ automationEnabled })}
              />
              <div className="grid grid-cols-1 gap-4 min-[960px]:grid-cols-2">
                <AutomationSliderField
                  label="稀客并发"
                  helpId="automation-rare-concurrency"
                  description={`同时允许进入处理流程的稀客订单数量，范围 ${MIN_AUTO_ORDER_CONCURRENCY} - ${MAX_RARE_AUTO_ORDER_CONCURRENCY}。修改后在下一轮自动化调度生效。`}
                  value={preferences.autoRareConcurrency}
                  min={MIN_AUTO_ORDER_CONCURRENCY}
                  max={MAX_RARE_AUTO_ORDER_CONCURRENCY}
                  onChange={(autoRareConcurrency) => onSharedPreferenceChange({ autoRareConcurrency })}
                />
                <AutomationSliderField
                  label="普客并发"
                  helpId="automation-normal-concurrency"
                  description={`同时允许进入处理流程的普客订单数量，范围 ${MIN_AUTO_ORDER_CONCURRENCY} - ${MAX_NORMAL_AUTO_ORDER_CONCURRENCY}。修改后在下一轮自动化调度生效。`}
                  value={preferences.autoNormalConcurrency}
                  min={MIN_AUTO_ORDER_CONCURRENCY}
                  max={MAX_NORMAL_AUTO_ORDER_CONCURRENCY}
                  onChange={(autoNormalConcurrency) => onSharedPreferenceChange({ autoNormalConcurrency })}
                />
                <AutomationSliderField
                  label="最大重试"
                  helpId="automation-max-step-retries"
                  description={`同一订单阶段执行失败时允许自动重试的最大次数，范围 ${MIN_AUTO_STEP_RETRIES} - ${MAX_AUTO_STEP_RETRIES_LIMIT}。达到上限后会暂停该订单，避免重复执行游戏操作。`}
                  value={preferences.autoMaxStepRetries}
                  min={MIN_AUTO_STEP_RETRIES}
                  max={MAX_AUTO_STEP_RETRIES_LIMIT}
                  onChange={(autoMaxStepRetries) => onSharedPreferenceChange({ autoMaxStepRetries })}
                />
                <AutomationSliderField
                  label="最大重新制作"
                  helpId="automation-max-rollbacks"
                  description={`同一料理目标因玩家操作、成品不符或游戏状态变化而重新制作的最大次数，范围 ${MIN_AUTO_ROLLBACKS} - ${MAX_AUTO_ROLLBACKS_LIMIT}。特殊经营目标真正变化后会重新计算次数。`}
                  value={preferences.autoMaxRollbacks}
                  min={MIN_AUTO_ROLLBACKS}
                  max={MAX_AUTO_ROLLBACKS_LIMIT}
                  onChange={(autoMaxRollbacks) => onSharedPreferenceChange({ autoMaxRollbacks })}
                />
              </div>
            </div>
          </ListPanel>
          <div className={DENSE_TWO_COLUMN_GRID}>
            <ListPanel title="稀客自动化设置">
              <div className="space-y-4">
                <SwitchControl
                  label="启用稀客处理"
                  helpId="automation-rare-enabled"
                  description="单独控制稀客订单是否进入自动化调度。关闭后保留各阶段设置并停止新处理；已经开锅的任务会在可以安全停止的位置暂停，重新开启后继续。"
                  checked={preferences.autoRareOrderEnabled}
                  onCheckedChange={(autoRareOrderEnabled) =>
                    onSharedPreferenceChange({ autoRareOrderEnabled })
                  }
                />
                <SwitchControl
                  label="自动送达酒水"
                  helpId="automation-rare-take-beverage"
                  description="为稀客订单选择并直接送达推荐酒水。开启时会同时开启自动完成订单，避免酒水和料理均已送达后无法通过游戏自身流程完成订单。"
                  checked={preferences.autoPrepTakeBeverage}
                  disabled={!preferences.autoRareOrderEnabled}
                  onCheckedChange={setRareBeverageDelivery}
                />
                <SwitchControl
                  label="自动开始料理"
                  helpId="automation-rare-start-cooking"
                  description="为稀客订单选择厨具、投入推荐料理及加料并自动完成 QTE。未开启自动送达料理时，成品会留给玩家自行取出和送达。"
                  checked={preferences.autoPrepStartCooking}
                  disabled={!preferences.autoRareOrderEnabled}
                  onCheckedChange={(autoPrepStartCooking) =>
                    onSharedPreferenceChange({ autoPrepStartCooking })
                  }
                />
                <SwitchControl
                  label="自动送达料理"
                  helpId="automation-rare-deliver-food"
                  description="料理完成后直接送达稀客订单。开启时会同时开启自动完成订单；制作途中关闭时不会中止游戏倒计时，成品会留在原厨具，重新开启后继续送达。"
                  checked={preferences.autoPrepCollectCooking}
                  disabled={!preferences.autoRareOrderEnabled}
                  onCheckedChange={setRareFoodDelivery}
                />
                <SwitchControl
                  label="自动完成订单"
                  helpId="automation-rare-complete-order"
                  description="酒水和料理均送达后调用游戏已验证的评价入口完成稀客订单。关闭时会同时关闭自动送达酒水和料理；已送达但尚未评价的任务会暂停，重新开启后从评价步骤继续。"
                  checked={preferences.autoPrepCompleteOrder}
                  disabled={!preferences.autoRareOrderEnabled}
                  onCheckedChange={setRareOrderCompletion}
                />
                <SwitchControl
                  label="出错时暂停"
                  helpId="automation-rare-stop-on-error"
                  description="稀客自动化步骤失败时暂停对应订单，等待手动重试、重置或人工确认。关闭后仍受最大重试和最大重新制作次数限制。"
                  checked={preferences.autoPrepStopOnError}
                  disabled={!preferences.autoRareOrderEnabled}
                  onCheckedChange={(autoPrepStopOnError) => onSharedPreferenceChange({ autoPrepStopOnError })}
                />
                <div className="border-t pt-4">
                  <div className="mb-3 text-sm font-medium text-foreground">稀客限定</div>
                  <div className="space-y-4">
                    <SwitchControl
                      label="只处理收藏料理"
                      helpId="automation-rare-recipe-favorites-only"
                      description="稀客自动化只选择已收藏的料理。收藏中没有满足订单、库存和厨具条件的料理时，该订单不会开始制作。"
                      checked={preferences.autoPrepRecipeFavoritesOnly}
                      disabled={!preferences.autoRareOrderEnabled}
                      onCheckedChange={(autoPrepRecipeFavoritesOnly) =>
                        onSharedPreferenceChange({ autoPrepRecipeFavoritesOnly })
                      }
                    />
                    <SwitchControl
                      label="只处理收藏酒水"
                      helpId="automation-rare-beverage-favorites-only"
                      description="稀客自动化只选择已收藏的酒水。收藏中没有满足点单与库存条件的酒水时，该订单不会自动送达酒水。"
                      checked={preferences.autoPrepBeverageFavoritesOnly}
                      disabled={!preferences.autoRareOrderEnabled}
                      onCheckedChange={(autoPrepBeverageFavoritesOnly) =>
                        onSharedPreferenceChange({ autoPrepBeverageFavoritesOnly })
                      }
                    />
                  </div>
                </div>
              </div>
            </ListPanel>
            <ListPanel title="普客自动化设置">
              <div className="space-y-4">
                <SwitchControl
                  label="启用普客处理"
                  helpId="automation-normal-enabled"
                  description="单独控制普客订单是否进入自动化调度。关闭后保留各阶段设置并停止新处理；已经开锅的任务会在可以安全停止的位置暂停，重新开启后继续。"
                  checked={preferences.autoNormalOrderEnabled}
                  onCheckedChange={(autoNormalOrderEnabled) =>
                    onSharedPreferenceChange({ autoNormalOrderEnabled })
                  }
                />
                <SwitchControl
                  label="自动送达酒水"
                  helpId="automation-normal-take-beverage"
                  description="为普客订单选择并直接送达指定酒水。开启时会同时开启自动完成订单，避免酒水和料理均已送达后无法通过游戏自身流程完成订单。"
                  checked={preferences.autoNormalTakeBeverage}
                  disabled={!preferences.autoNormalOrderEnabled}
                  onCheckedChange={setNormalBeverageDelivery}
                />
                <SwitchControl
                  label="自动开始料理"
                  helpId="automation-normal-start-cooking"
                  description="为普客订单选择厨具、投入指定料理并自动完成 QTE。未开启自动送达料理时，成品会留给玩家自行取出和送达。"
                  checked={preferences.autoNormalStartCooking}
                  disabled={!preferences.autoNormalOrderEnabled}
                  onCheckedChange={(autoNormalStartCooking) =>
                    onSharedPreferenceChange({ autoNormalStartCooking })
                  }
                />
                <SwitchControl
                  label="自动送达料理"
                  helpId="automation-normal-deliver-food"
                  description="料理完成后直接送达普客订单。开启时会同时开启自动完成订单；制作途中关闭时不会中止游戏倒计时，成品会留在原厨具，重新开启后继续送达。"
                  checked={preferences.autoNormalDeliverFood}
                  disabled={!preferences.autoNormalOrderEnabled}
                  onCheckedChange={setNormalFoodDelivery}
                />
                <SwitchControl
                  label="自动完成订单"
                  helpId="automation-normal-complete-order"
                  description="酒水和料理均送达后调用游戏已验证的评价入口完成普客订单。关闭时会同时关闭自动送达酒水和料理；已送达但尚未评价的任务会暂停，重新开启后从评价步骤继续。"
                  checked={preferences.autoNormalCompleteOrder}
                  disabled={!preferences.autoNormalOrderEnabled}
                  onCheckedChange={setNormalOrderCompletion}
                />
                <SwitchControl
                  label="出错时暂停"
                  helpId="automation-normal-stop-on-error"
                  description="普客自动化步骤失败时暂停对应订单，等待手动重试、重置或人工确认。关闭后仍受最大重试和最大重新制作次数限制。"
                  checked={preferences.autoNormalStopOnError}
                  disabled={!preferences.autoNormalOrderEnabled}
                  onCheckedChange={(autoNormalStopOnError) =>
                    onSharedPreferenceChange({ autoNormalStopOnError })
                  }
                />
              </div>
            </ListPanel>
          </div>
        </fieldset>
      </div>
    </SettingHelpProvider>
  );
}
