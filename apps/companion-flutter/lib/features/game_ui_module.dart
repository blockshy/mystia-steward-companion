import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/module_switch_row.dart';
import '../ui/steward_style.dart';
import 'profile_controls.dart';

import 'package:companion_protocol/companion_protocol.dart'
    show CompanionProfile;

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

  Widget _group(BuildContext context, String prefix, String displayLabel) {
    final field = '${prefix}GameUiEnabled';
    final title = '$displayLabel辅助';
    // 分类总开关复用分区标题行，避免额外占用一行。关闭只暂停该类的执行，
    // 子项与调色板仍可预先编辑；恢复开关后由服务端统一采用已保存的组合。
    return Section(
      title,
      [
        _toggle('${prefix}GameUiPinningEnabled', '列表置顶'),
        _toggle('${prefix}RecipeVariantEnabled', '配方变体'),
        _toggle('${prefix}CookerHighlightEnabled', '厨具高亮'),
        _toggle('${prefix}SeatHighlightEnabled', '座位高亮'),
        _toggle('${prefix}OrderHighlightEnabled', '订单高亮'),
        ..._palette(prefix),
      ],
      titleContent: StewardBaselineRow(
        children: [
          Expanded(
            child: Text(
              title,
              style: StewardText.sectionTitle.copyWith(
                color: context.theme.colors.primary,
              ),
            ),
          ),
          const SizedBox(width: StewardDensity.controlGap),
          StewardFirstLineCenter(
            key: ValueKey(field),
            textStyle: StewardText.sectionTitle,
            referenceText: title,
            child: FSwitch(
              value: model.profile[field] == true,
              enabled: model.canEdit,
              onChange: model.canEdit
                  ? (value) => model.updateProfile({field: value})
                  : null,
              semanticsLabel: '$title总开关',
            ),
          ),
        ],
      ),
    );
  }

  /// 两个分区各自只提交所属调色板，数量变化也不得写入另一类配置。
  /// 每种颜色对应一笔辅助订单，因此只保留一个数量入口，避免两项设置出现矛盾。
  List<Widget> _palette(String prefix) {
    final field = '${prefix}OrderHighlightPalette';
    final saved = values(model.profile[field]);
    final colors =
        saved.isNotEmpty && saved.length <= CompanionProfile.maxHighlightColors
        ? saved.cast<String>()
        : CompanionProfile.defaultPalette(prefix);
    return [
      IntegerSetting(
        key: ValueKey('$prefix-highlight-color-count'),
        title: '辅助订单与颜色',
        value: colors.length,
        min: 1,
        max: CompanionProfile.maxHighlightColors,
        unit: '组',
        resetKey: profileControlBoundary(model),
        onSave: model.canEdit
            ? (count) {
                // 扩展时保留原顺序并补充未使用颜色；缩减只移除尾部颜色，不改其他设置。
                final next = <String>{
                  ...colors,
                  ...CompanionProfile.highlightPresets(prefix),
                }.take(count).toList();
                return model.updateProfile({field: next});
              }
            : null,
      ),
      for (var index = 0; index < colors.length; index++)
        ColorSetting(
          key: ValueKey('$prefix-order-highlight-color-$index'),
          title: '颜色 ${index + 1}',
          value: colors[index],
          presetColors: CompanionProfile.highlightPresets(prefix),
          resetKey: profileControlBoundary(model),
          onSave: model.canEdit
              ? (value) {
                  final next = List<String>.of(colors)..[index] = value;
                  if (next.toSet().length != next.length) {
                    throw StateError('此类客人已使用该颜色，请选择不同颜色。');
                  }
                  return model.updateProfile({field: next});
                }
              : null,
        ),
    ];
  }

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
      _group(context, 'normal', '普客'),
      _group(context, 'rare', '稀客'),
    ],
  );
}
