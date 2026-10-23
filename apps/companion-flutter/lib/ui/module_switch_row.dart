import 'dart:math' as math;

import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import 'steward_controls.dart';
import 'steward_style.dart';

/// 五个扩展模块共用的单行总开关。页签已经标明模块名称，因此正文只显示“启用”、
/// 保存范围与操作权限，不重复绘制总开关卡片或模块标题。长说明保留在提示和读屏中。
/// 开关值始终取调用方已保存的事实；本组件不保存、不乐观切换，也不判断业务权限。
class ModuleSwitchRow extends StatelessWidget {
  const ModuleSwitchRow({
    required this.moduleName,
    required this.value,
    required this.status,
    required this.explanation,
    required this.onChanged,
    super.key,
  });

  final String moduleName;
  final bool value;
  final String status;
  final String explanation;
  final ValueChanged<bool>? onChanged;

  @override
  Widget build(BuildContext context) => Semantics(
    container: true,
    explicitChildNodes: true,
    child: Padding(
      padding: StewardDensity.togglePadding,
      child: ConstrainedBox(
        // 模块行保留原有至少44px的可操作空间；放大字号时跟随公共控制区，
        // 不压缩开关本体，也不以固定行高裁切状态文字。
        constraints: BoxConstraints(
          minHeight: math.max(44.0, stewardControlHeight(context)),
        ),
        child: StewardBaselineRow(
          key: const ValueKey('module-switch-line'),
          children: [
            const Text('启用', style: StewardText.body),
            const SizedBox(width: 8),
            Expanded(
              child: FTooltip(
                tipBuilder: (context, controller) => Text(explanation),
                child: Text(
                  status,
                  key: const ValueKey('module-switch-status'),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  // 视觉上保持一行；缩放后即使省略，读屏和长按提示仍提供完整范围、
                  // 权限与关闭含义，不能用颜色或一个无标签开关代替这些关键说明。
                  semanticsLabel: '$status。$explanation',
                  style: StewardText.caption.copyWith(
                    color: context.theme.colors.mutedForeground,
                  ),
                ),
              ),
            ),
            const SizedBox(width: 8),
            StewardFirstLineCenter(
              textStyle: StewardText.body,
              referenceText: '启用',
              child: FSwitch(
                value: value,
                enabled: onChanged != null,
                onChange: onChanged,
                semanticsLabel: '启用$moduleName模块',
              ),
            ),
          ],
        ),
      ),
    ),
  );
}
