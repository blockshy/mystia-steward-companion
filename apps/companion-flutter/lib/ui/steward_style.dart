import 'package:flutter/rendering.dart';
import 'package:flutter/widgets.dart';

/// 管家正文采用同一套信息密度：任务、邀请、库存的字段和设置项共享 13px 正文，
/// 辅助说明采用 12px，分组及条目标题通过字号和字重建立层级。这里不固定文字颜色
/// 或字体族，继续继承主题、平台中文字体回退和系统 TextScaler；也不按窗口宽度切字号。
/// 按钮、输入与选择器共享13px/1.45倍行高；实际框体及平台内边距统一由
/// steward_controls.dart 计算，不能再依赖 Forui 不同尺寸档位隐式决定页面密度。
abstract final class StewardText {
  static const body = TextStyle(fontSize: 13, height: 1.45);
  static const caption = TextStyle(fontSize: 12, height: 1.45);
  static const tag = TextStyle(
    fontSize: 12,
    height: 1.4,
    fontWeight: FontWeight.w600,
  );
  static const value = TextStyle(
    fontSize: 13,
    height: 1.45,
    fontWeight: FontWeight.w600,
  );
  static const sectionTitle = TextStyle(
    fontSize: 15,
    height: 1.35,
    fontWeight: FontWeight.w700,
  );
  static const itemTitle = TextStyle(
    fontSize: 16,
    height: 1.45,
    fontWeight: FontWeight.w700,
  );
}

/// 设置控件的布局间距独立于字号。桌面和触屏共享文字层级，但仍由 Forui 的
/// 平台主题提供按钮/开关的命中区；不要为压缩行高而缩小触屏交互目标。
abstract final class StewardDensity {
  static const settingPadding = EdgeInsets.symmetric(vertical: 8);
  static const togglePadding = EdgeInsets.symmetric(vertical: 4);
  static const controlGap = 12.0;
  static const supportingGap = 4.0;
  static const toolbarGap = 8.0;

  /// 阅读区的相邻信息分组共用留白，不让可折叠详情头紧贴上一组彩色标签。
  static const detailGroupGap = 12.0;
}

/// 实体图片的设计尺寸与普通操作图标分开管理。调用方通过当前TextScaler换算，
/// 图片、图片缺失时的图标和所在槽位一起跟随本机字号偏好及系统字号变化。
abstract final class StewardImageSize {
  static const tag = 20.0;
  static const name = 28.0;
}

/// 相邻文字按真实首行基线排列，而不是比较不同字号的文字盒顶或盒中心。
/// Text、Padding、Container 及 Wrap 会向上传递实际基线，因此带内边距的徽标和
/// 多行标签也能让外侧前缀贴齐第一行文字。非文本图标可配合 [StewardFirstLineCenter]。
/// 滑块等复合交互区仍应按整块居中，不应为了基线对齐改变既有操作布局。
class StewardBaselineRow extends StatelessWidget {
  const StewardBaselineRow({
    required this.children,
    this.mainAxisSize = MainAxisSize.max,
    this.mainAxisAlignment = MainAxisAlignment.start,
    super.key,
  });

  final List<Widget> children;
  final MainAxisSize mainAxisSize;
  final MainAxisAlignment mainAxisAlignment;

  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: mainAxisSize,
    mainAxisAlignment: mainAxisAlignment,
    crossAxisAlignment: CrossAxisAlignment.baseline,
    textBaseline: TextBaseline.alphabetic,
    children: children,
  );
}

/// 为图标、状态圆点等非文本元素提供与相邻文字首行一致的基线。子元素尺寸、
/// 绘制、命中测试和语义均保持原样，仅向基线布局报告一个由字体度量推导的锚点。
/// 不使用固定 top 偏移：中文字体、系统字号及正文行高变化时，图标仍居中于首行。
class StewardFirstLineCenter extends StatelessWidget {
  const StewardFirstLineCenter({
    required this.child,
    this.textStyle,
    this.referenceText = '国',
    super.key,
  });

  final Widget child;
  final TextStyle? textStyle;

  /// 使用相邻文字可保留实际中文字体回退；这里只测量一行的字体度量，不决定换行。
  final String referenceText;

  @override
  Widget build(BuildContext context) {
    final defaults = DefaultTextStyle.of(context);
    var style = defaults.style.merge(textStyle);
    if (MediaQuery.boldTextOf(context)) {
      style = style.merge(const TextStyle(fontWeight: FontWeight.bold));
    }
    final painter = TextPainter(
      text: TextSpan(
        text: referenceText.isEmpty ? '国' : referenceText,
        style: style,
      ),
      textDirection: Directionality.of(context),
      textScaler: MediaQuery.textScalerOf(context),
      locale: Localizations.maybeLocaleOf(context),
      maxLines: 1,
      textHeightBehavior:
          defaults.textHeightBehavior ??
          DefaultTextHeightBehavior.maybeOf(context),
    )..layout();
    try {
      final height = painter.computeLineMetrics().first.height;
      return _FirstLineBaseline(
        alphabeticOffset:
            painter.computeDistanceToActualBaseline(TextBaseline.alphabetic) -
            height / 2,
        ideographicOffset:
            painter.computeDistanceToActualBaseline(TextBaseline.ideographic) -
            height / 2,
        child: child,
      );
    } finally {
      painter.dispose();
    }
  }
}

class _FirstLineBaseline extends SingleChildRenderObjectWidget {
  const _FirstLineBaseline({
    required this.alphabeticOffset,
    required this.ideographicOffset,
    required super.child,
  });

  final double alphabeticOffset, ideographicOffset;

  @override
  RenderObject createRenderObject(BuildContext context) =>
      _RenderFirstLineBaseline(
        alphabeticOffset: alphabeticOffset,
        ideographicOffset: ideographicOffset,
      );

  @override
  void updateRenderObject(
    BuildContext context,
    _RenderFirstLineBaseline renderObject,
  ) {
    renderObject.updateOffsets(alphabeticOffset, ideographicOffset);
  }
}

class _RenderFirstLineBaseline extends RenderProxyBox {
  _RenderFirstLineBaseline({
    required double alphabeticOffset,
    required double ideographicOffset,
  }) : _alphabeticOffset = alphabeticOffset,
       _ideographicOffset = ideographicOffset;

  double _alphabeticOffset, _ideographicOffset;

  void updateOffsets(double alphabetic, double ideographic) {
    if (_alphabeticOffset == alphabetic && _ideographicOffset == ideographic)
      return;
    _alphabeticOffset = alphabetic;
    _ideographicOffset = ideographic;
    markNeedsLayout();
  }

  double _offset(TextBaseline baseline) => baseline == TextBaseline.alphabetic
      ? _alphabeticOffset
      : _ideographicOffset;

  // 基线 = 子项中心 +（参考文字基线 - 参考首行中心），所以 Row 对齐基线后，
  // 子项中心恰好落在实际文字首行中心。多行文字的后续行不会把图标向下拖动。
  @override
  double computeDistanceToActualBaseline(TextBaseline baseline) =>
      size.height / 2 + _offset(baseline);

  // IntrinsicWidth 等布局会先询问干布局；同一公式必须同时实现，避免实布局与
  // 干布局报告不同的基线。RenderProxyBox 原样传递尺寸约束，不压缩图标或命中区。
  @override
  double computeDryBaseline(
    BoxConstraints constraints,
    TextBaseline baseline,
  ) => getDryLayout(constraints).height / 2 + _offset(baseline);
}
