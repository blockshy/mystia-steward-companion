import 'dart:math' as math;

import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import 'steward_style.dart';
import 'steward_opacity.dart';

/// 订单说明与菜单指标共用的标签外壳。沿用原订单标签的字体、行高、内边距、
/// 圆角及图标尺寸，避免同一行的价格和材料出现两种视觉密度。
/// 调用方只提供展示文字、语义名称和色调；本组件不解释数值或推断业务状态。
class SemanticTag extends StatelessWidget {
  const SemanticTag({
    required this.text,
    required this.semanticLabel,
    this.icon,
    required this.tone,
    this.emphasizedSuffix,
    this.leading,
    super.key,
  });

  final String text;
  final String semanticLabel;

  /// 数量摘要可以省去装饰图标，仅显示“+N”；其高度和首行基线仍与普通标签一致。
  final IconData? icon;
  final Color tone;

  /// 实体图片与普通语义图标使用同一缩放槽，所有指标和材料标签保持等高。
  final Widget? leading;

  /// 指标的数值后缀仅加粗，不另设字号或行高，保持与普通文字标签等高。
  /// 该值已由来源适配层格式化，未知、无限及负数的业务含义不在这里改写。
  final String? emphasizedSuffix;

  @override
  Widget build(BuildContext context) {
    final dark = context.theme.colors.background.computeLuminance() < .3;
    final paint = context.stewardPaint;
    var textStyle = DefaultTextStyle.of(context).style
        .merge(StewardText.tag.copyWith(color: paint.foreground(tone)));
    if (MediaQuery.boldTextOf(context)) {
      textStyle = textStyle.copyWith(fontWeight: FontWeight.bold);
    }
    // 中文标签与纯数字摘要可能命中不同字体字形。相同 fontSize/height 并不保证
    // 两者的 ascent/descent 相同，外层 Wrap 顶对齐时就会出现“等高但基线不同”。
    // 所有标签使用当前实际主题字体的同一行支架；字形、字重和自动换行保持原样，
    // 不针对某个字符串补位，也不把标签锁死为只能容纳一行的固定高度。
    final strut = StrutStyle.fromTextStyle(textStyle, forceStrutHeight: true);
    const textHeightBehavior = TextHeightBehavior();
    final iconExtent = MediaQuery.textScalerOf(context)
        .scale(StewardImageSize.tag);
    final lineHeight = _lineHeight(
      context,
      textStyle,
      strut,
      textHeightBehavior,
    );
    final textInset = math.max(0.0, (iconExtent - lineHeight) / 2);
    final iconInset = math.max(0.0, (lineHeight - iconExtent) / 2);
    return Semantics(
      // 每个标签保持独立语义边界。图标只辅助辨认，读屏完整读出调用方给出的名称。
      container: true,
      label: semanticLabel,
      excludeSemantics: true,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 5),
        decoration: BoxDecoration(
          // 透明底色仅区分信息类别，不覆盖用户设置的 Windows 背景透明度。
          color: paint.surface(tone, alpha: dark ? .10 : .07),
          border: Border.all(
            color: paint.foreground(tone, alpha: dark ? .33 : .23),
          ),
          borderRadius: BorderRadius.circular(5),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // 装饰图标可能自身也是字体字形，不能以它的基线代替标签文字基线。
            // 忽略其基线后，Row/Container 向外只传递真实文字基线；居中留白取自
            // 同一行支架的实测高度，多行标签的图标仍停留在首行而非整段中心。
            IgnoreBaseline(
              child: Padding(
                padding: EdgeInsets.only(top: iconInset),
                child: icon == null && leading == null
                    // 零宽槽保留图标标签的垂直度量，不占据“+N”前方的横向空间。
                    ? SizedBox(width: 0, height: iconExtent)
                    : IconTheme(
                        data: IconThemeData(
                          size: iconExtent,
                          color: paint.foreground(tone),
                        ),
                        child: SizedBox.square(
                          dimension: iconExtent,
                          child: leading ?? Icon(icon, size: iconExtent),
                        ),
                      ),
              ),
            ),
            if (icon != null || leading != null) const SizedBox(width: 5),
            Flexible(
              // 不固定高度、不截断行数；窄屏和放大字号仍可完整阅读长标签。
              child: Padding(
                padding: EdgeInsets.symmetric(vertical: textInset),
                child: emphasizedSuffix == null
                    ? Text(
                        text,
                        softWrap: true,
                        style: textStyle,
                        strutStyle: strut,
                        textHeightBehavior: textHeightBehavior,
                      )
                    : Text.rich(
                        TextSpan(
                          text: text,
                          children: [
                            TextSpan(
                              text: emphasizedSuffix,
                              style: const TextStyle(
                                fontWeight: FontWeight.w700,
                              ),
                            ),
                          ],
                        ),
                        softWrap: true,
                        style: textStyle,
                        strutStyle: strut,
                        textHeightBehavior: textHeightBehavior,
                      ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// 测量与实际 Text 完全相同的行支架，空格仅用于产生一行，不根据标签内容选择
/// 字体或几何分支。TextScaler、语言及主题字体都参与测量，避免硬编码像素补偿。
double _lineHeight(
  BuildContext context,
  TextStyle style,
  StrutStyle strut,
  TextHeightBehavior behavior,
) {
  final painter = TextPainter(
    text: TextSpan(text: ' ', style: style),
    strutStyle: strut,
    textHeightBehavior: behavior,
    textDirection: Directionality.of(context),
    textScaler: MediaQuery.textScalerOf(context),
    locale: Localizations.maybeLocaleOf(context),
    maxLines: 1,
  )..layout();
  try {
    return painter.height;
  } finally {
    painter.dispose();
  }
}
