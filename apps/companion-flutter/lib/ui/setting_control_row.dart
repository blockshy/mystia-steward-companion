import 'dart:math' as math;

import 'package:flutter/widgets.dart';

import 'steward_style.dart';

/// 设置项统一采用「左侧说明、右侧控制」的横向结构。控制区的右边缘就是内容区
/// 右边缘，与现有 SettingToggle 的开关对齐；窄屏不切成上下两行，长标题自行换行。
/// 范围、保存状态和错误放在独立辅助行，避免挤压滑轨或改变主要控件的右侧位置。
/// 此处右列是滑块、颜色按钮等复合交互区，保留整块垂直居中；相邻文本之间采用的
/// 首行基线规则不适用于整组控件，不能因此将滑块移到长标题的第一行或压缩命中区。
class SettingControlRow extends StatelessWidget {
  const SettingControlRow({
    required this.title,
    required this.control,
    this.supporting,
    this.maxControlWidth = 320,
    super.key,
  }) : assert(maxControlWidth > 0);

  final String title;
  final Widget control;
  final Widget? supporting;

  /// 滑块保留较宽操作区；短选项下拉可由调用方设置较小上限，避免和滑轨同宽。
  /// 仅限制右列宽度，不改变其右边缘位置，也不以视口断点切换字体或上下布局。
  final double maxControlWidth;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      // 390px 窗口扣除导航和页面边距后仍为控制区保留 144px。极端嵌套宽度
      // 不硬撑出父容器；正常宽度下遵守调用方上限，不把下拉或滑块拉成整页横线。
      final width = constraints.maxWidth;
      final controlWidth = math.min(
        math.max(
          math.min(144.0, maxControlWidth),
          math.min(maxControlWidth, width * .58),
        ),
        math.max(0.0, width - 60),
      );
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  title,
                  key: const ValueKey('setting-control-label'),
                  style: StewardText.body,
                ),
              ),
              const SizedBox(width: StewardDensity.controlGap),
              SizedBox(
                key: const ValueKey('setting-control-area'),
                width: controlWidth,
                child: control,
              ),
            ],
          ),
          if (supporting != null) ...[
            const SizedBox(height: StewardDensity.supportingGap),
            supporting!,
          ],
        ],
      );
    },
  );
}
