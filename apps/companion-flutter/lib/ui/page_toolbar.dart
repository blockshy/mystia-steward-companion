import 'dart:math' as math;

import 'package:flutter/widgets.dart';

import 'components.dart';
import 'steward_style.dart';

/// 页面查询条件的标准首行。与结果搜索栏统一为顶部不加间距、底部8逻辑像素，
/// 避免同一工作区切换页签时分别使用 ToolRow/自定义 Padding 导致控件上下跳动。
///
/// children 可使用 Flexible/Expanded 分配窄屏空间；本组件只在项目之间加入间距，
/// 不根据视口宽度切换布局，不压缩控件高度或字体。长内容应由可搜索选择器处理。
/// 通用弹窗按钮组和允许换行的批量操作继续使用 ToolRow，不混用页面筛选的留白规则。
class PageFilterRow extends StatelessWidget {
  const PageFilterRow({required this.children, super.key});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: StewardDensity.toolbarGap),
    child: Row(
      children: [
        for (final (index, child) in children.indexed) ...[
          if (index > 0) const SizedBox(width: StewardDensity.toolbarGap),
          child,
        ],
      ],
    ),
  );
}

/// 页面内的紧凑搜索工具栏。只组合相关的结果页签、名称搜索和少量紧凑操作，
/// 不把搜索强塞进模块主导航。navigation 应提供自身可横向滚动的页签组件，
/// 搜索始终留在滚动区域外，因此滑动页签不会带走输入框或改变输入焦点。
///
/// 控制器由页面持有；布局始终是一行，不在宽度断点切换 Row/Column 或重建
/// 编辑状态。宽屏搜索最多152逻辑像素，窄屏按可用宽度缩至约104–120像素，
/// 余下空间交给页签独立滚动。调用方的 actions 应为刷新等短操作，不放长说明。
class PageToolbar extends StatelessWidget {
  const PageToolbar({
    required this.controller,
    required this.searchLabel,
    required this.onSearchChanged,
    this.searchHint,
    this.navigation,
    this.actions = const [],
    this.searchKey,
    this.maxSearchWidth = 152,
    super.key,
  });

  final TextEditingController controller;
  final String searchLabel;
  final String? searchHint;
  final ValueChanged<String> onSearchChanged;
  final Widget? navigation;
  final List<Widget> actions;
  final Key? searchKey;

  /// 个别多来源工作区可收紧搜索上限，给同排页签让出空间；默认值保持既有页面密度。
  /// 这只是上限，实际宽度仍服从原有窄屏比例和页签横滚规则，不缩放字体或命中区。
  final double maxSearchWidth;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: StewardDensity.toolbarGap),
    child: LayoutBuilder(
      builder: (context, constraints) {
        final width = math.min(
          maxSearchWidth,
          constraints.maxWidth * (navigation == null ? .68 : .46),
        );
        final search = SizedBox(
          width: width,
          child: StewardField(
            searchLabel,
            key: searchKey,
            showLabel: false,
            hint: searchHint ?? searchLabel,
            controller: controller,
            onChanged: onSearchChanged,
          ),
        );
        return Row(
          children: [
            if (navigation != null) ...[
              Expanded(child: navigation!),
              const SizedBox(width: StewardDensity.toolbarGap),
              search,
            ] else
              // 帮助目录等没有对应页签时，搜索靠左；Flexible 允许极窄容器在
              // 保留操作的同时收紧输入宽度，不使用无限宽或屏幕宽度猜测正文尺寸。
              Flexible(child: search),
            for (final action in actions) ...[
              const SizedBox(width: StewardDensity.toolbarGap),
              action,
            ],
          ],
        );
      },
    ),
  );
}
