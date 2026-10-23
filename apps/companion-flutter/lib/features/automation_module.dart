import 'package:flutter/widgets.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/module_switch_row.dart';
import '../ui/steward_style.dart';
import 'profile_controls.dart';

/// 自动化扩展的共享配置表单。页面只保存配置，不在客户端实现调度或派发游戏操作；
/// 主设备身份、同步状态、场景代次及执行租约继续由会话和 C# 业务模块判定。
/// 父工作区必须提供有界高度，本组件独立滚动，适配桌面与手机的扩展 Tab。
class AutomationModule extends StatelessWidget {
  const AutomationModule(this.model, {super.key});
  final WorkbenchModel model;

  Widget _toggle(String key, String title) => SettingToggle(
    title,
    model.profile[key] == true,
    model.canEdit ? (value) => model.updateProfile({key: value}) : null,
    key: ValueKey(key),
  );

  /// 普客与稀客的并发数各自保存；重试和回滚仍使用同一份共享字段。
  /// 同一个字段分别出现在两类步骤中，不能拆成两个虚假的独立配置值。
  Widget _number(
    String group,
    String key,
    String title,
    int fallback,
    int minimum,
    int maximum,
  ) => IntegerSetting(
    key: ValueKey('$group-$key'),
    title: title,
    unit: key.endsWith('Concurrency') ? ' 单' : ' 次',
    resetKey: profileControlBoundary(model),
    value: (model.profile[key] as num?)?.toInt() ?? fallback,
    min: minimum,
    max: maximum,
    onSave: model.canEdit ? (value) => model.updateProfile({key: value}) : null,
  );

  /// 这两个字段保留服务端计数语义，只将名称翻译成玩家能直接判断的暂停条件。
  /// autoMaxStepRetries 计的是失败次数（包含第一次），并非首次失败后额外尝试的次数；
  /// autoMaxRollbacks 计的是订单重新规划，不会回滚存档、退回材料或撤销已提交的操作。
  /// 页面只保留设置时必须知道的生效条件与零值含义；清零、恢复和人工确认边界在
  /// 完整帮助中说明。删减说明不改变共享字段、可编辑权限、取值范围或保存调用。
  List<Widget> _recovery(String group) => [
    const SizedBox(height: 8),
    _number(group, 'autoMaxStepRetries', '同一步骤失败上限（共享）', 3, 1, 10),
    Text(
      '启用出错暂停时生效；首次失败计入。',
      key: ValueKey('$group-step-failure-help'),
      style: StewardText.caption,
    ),
    _number(group, 'autoMaxRollbacks', '重新安排次数上限（共享）', 2, 0, 5),
    Text(
      '达到上限即暂停；0 表示不自动重新安排。',
      key: ValueKey('$group-replanning-help'),
      style: StewardText.caption,
    ),
  ];

  @override
  Widget build(BuildContext context) => ListView(
    key: const PageStorageKey('automation-module'),
    padding: const EdgeInsets.only(right: 8, bottom: 20),
    children: [
      ModuleSwitchRow(
        key: const ValueKey('automationEnabled'),
        moduleName: '自动化',
        value: model.profile['automationEnabled'] == true,
        status: !model.connected
            ? '共享·离线'
            : model.busy
            ? '共享·保存中'
            : model.primary
            ? '共享·可写'
            : '共享·只读',
        explanation: '由主设备保存共享配置，下次启动沿用；配置同步完成且游戏进入可自动经营的场景后运行。关闭保留分项偏好；两项失败恢复上限普客和稀客共用，各订单分别计数。',
        onChanged: model.canEdit
            ? (value) => model.updateProfile({'automationEnabled': value})
            : null,
      ),
      Section('普客自动化', [
        _toggle('autoNormalOrderEnabled', '自动处理普客'),
        for (final entry in const {
          'autoNormalStartCooking': '开始烹饪',
          'autoNormalCompleteOrder': '完成订单',
          'autoNormalTakeBeverage': '取送酒水',
          'autoNormalDeliverFood': '送出料理',
          'autoNormalStopOnError': '出错暂停',
        }.entries)
          _toggle(entry.key, entry.value),
        _number('normal', 'autoNormalConcurrency', '普客并发数', 3, 1, 6),
        ..._recovery('normal'),
      ]),
      Section('稀客自动化', [
        _toggle('autoRareOrderEnabled', '自动处理稀客'),
        for (final entry in const {
          'autoPrepStartCooking': '开始烹饪',
          'autoPrepCompleteOrder': '完成订单',
          'autoPrepTakeBeverage': '取送酒水',
          'autoPrepCollectCooking': '收取料理',
          'autoPrepRecipeFavoritesOnly': '仅收藏料理',
          'autoPrepBeverageFavoritesOnly': '仅收藏酒水',
          'autoPrepStopOnError': '出错暂停',
        }.entries)
          _toggle(entry.key, entry.value),
        _number('rare', 'autoRareConcurrency', '稀客并发数', 2, 1, 4),
        ..._recovery('rare'),
      ]),
    ],
  );
}
