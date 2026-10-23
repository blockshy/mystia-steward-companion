import 'package:flutter/rendering.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

/// 读取已经布局的真实文字，不用字号常量或外层矩形中心冒充字体基线。
RenderParagraph renderedParagraph(WidgetTester tester, Finder text) =>
    tester.renderObject<RenderParagraph>(
      find.descendant(of: text, matching: find.byType(RichText)),
    );

double textBaseline(WidgetTester tester, Finder text) {
  return _textGeometry(tester, text).baseline;
}

/// 复用最终 RenderParagraph 的 span、字号缩放、字体与实际宽度测量首行；只用
/// Text.size.center 会在换行后移到整段中间，恰好掩盖图标被长文字向下拖动的回归。
({double center, double height}) textFirstLine(
  WidgetTester tester,
  Finder text,
) {
  final geometry = _textGeometry(tester, text);
  return (center: geometry.center, height: geometry.height);
}

/// Flutter 3.47 的 RenderBox.getDistanceToBaseline 只允许布局/绘制阶段查询，
/// 测试在 pump 后读取会触发断言；RenderParagraph 也没有公开 computeLineMetrics。
/// 因此从实际段落的完整排版参数建立只读 TextPainter，使用其公开首行度量接口。
/// 这里保留缩放、字体回退、截断和真实宽度，不能用字号推算或关闭框架断言代替。
({double baseline, double center, double height}) _textGeometry(
  WidgetTester tester,
  Finder text,
) {
  final paragraph = renderedParagraph(tester, text);
  final painter =
      TextPainter(
        text: paragraph.text,
        textDirection: paragraph.textDirection,
        textAlign: paragraph.textAlign,
        textScaler: paragraph.textScaler,
        locale: paragraph.locale,
        strutStyle: paragraph.strutStyle,
        textHeightBehavior: paragraph.textHeightBehavior,
        textWidthBasis: paragraph.textWidthBasis,
        maxLines: paragraph.maxLines,
        ellipsis: paragraph.overflow == TextOverflow.ellipsis ? '\u2026' : null,
      )..layout(
        maxWidth:
            paragraph.softWrap || paragraph.overflow == TextOverflow.ellipsis
            ? paragraph.size.width
            : double.infinity,
      );
  try {
    final line = painter.computeLineMetrics().first;
    final top = paragraph.localToGlobal(Offset.zero).dy;
    return (
      // RenderParagraph 的实际基线采用此公开 API；LineMetrics.baseline 存在
      // 字体度量舍入差异，不能用它替代，也不能通过放宽像素容差掩盖该差异。
      baseline:
          top +
          painter.computeDistanceToActualBaseline(TextBaseline.alphabetic),
      center: top + line.height / 2,
      height: line.height,
    );
  } finally {
    painter.dispose();
  }
}
