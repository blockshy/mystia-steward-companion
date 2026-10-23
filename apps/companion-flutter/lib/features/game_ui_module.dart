import 'package:flutter/widgets.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/module_switch_row.dart';
import 'profile_controls.dart';
import 'service_order_highlight.dart';

/// 游戏界面辅助的共享配置入口。总开关由 C# 执行投影统一应用，不能仅隐藏此页
/// 或在本机屏蔽控件；关闭时保留两类辅助偏好，重新开启后恢复已保存的组合。
class GameUiModule extends StatelessWidget {
  const GameUiModule(this.model, {super.key});
  final WorkbenchModel model;

  Widget _toggle(String key, String title) => SettingToggle(
    title,
    model.profile[key] == true,
    // 总开关控制执行，不控制偏好的编辑。查看设备和忙碌状态仍使用统一写入门禁。
    model.canEdit ? (value) => model.updateProfile({key: value}) : null,
    key: ValueKey(key),
  );

  Widget _group(String prefix, String displayLabel, String defaultColor) =>
      Section('$displayLabel辅助', [
        _toggle('${prefix}GameUiPinningEnabled', '列表置顶'),
        _toggle('${prefix}RecipeVariantEnabled', '配方变体'),
        _toggle('${prefix}CookerHighlightEnabled', '厨具高亮'),
        _toggle('${prefix}SeatHighlightEnabled', '座位高亮'),
        _toggle('${prefix}OrderHighlightEnabled', '订单高亮'),
        ColorSetting(
          key: ValueKey('$prefix-highlight-color'),
          title: '$displayLabel高亮颜色',
          resetKey: profileControlBoundary(model),
          value: label(
            model.profile['${prefix}TargetHighlightColor'],
            defaultColor,
          ),
          onSave: model.canEdit
              ? (value) => model.updateProfile({
                  '${prefix}TargetHighlightColor': value,
                })
              : null,
        ),
      ]);

  @override
  Widget build(BuildContext context) => ListView(
    key: const PageStorageKey('game-ui-module'),
    padding: const EdgeInsets.only(right: 8, bottom: 20),
    children: [
      ModuleSwitchRow(
        key: const ValueKey('gameUiEnabled'),
        moduleName: '游戏界面辅助',
        value: model.profile['gameUiEnabled'] == true,
        status: !model.connected
            ? '共享·离线'
            : model.busy
            ? '共享·保存中'
            : model.primary
            ? '共享·可写'
            : '共享·只读',
        explanation: '由主设备保存共享配置，下次启动沿用。关闭停止界面辅助并保留分项设置；关闭期间仍可调整偏好。',
        onChanged: model.canEdit
            ? (value) => model.updateProfile({'gameUiEnabled': value})
            : null,
      ),
      _group('normal', '普客', normalOrderHighlightHex),
      _group('rare', '稀客', rareOrderHighlightHex),
    ],
  );
}
