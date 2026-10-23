import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import 'steward_style.dart';
import 'steward_opacity.dart';
import 'steward_controls.dart';

/// 优先使用本机已有的开源简体中文字体，统一中文标题、正文与控件的字面风格。
/// 不随包复制系统商业字体，也不假定所有 Windows/Android 都安装了 Noto Sans SC；
/// 缺少该字体时依次回退雅黑、平台常见 CJK 字体和系统 sans-serif。各字的自然字面
/// 留白仍由字体决定，不能针对标题末字做位移或裁切；字号、字重和行高保持原规范。
const stewardFontFamily = 'Noto Sans SC';
const stewardFontFallback = <String>[
  'Microsoft YaHei',
  'Noto Sans CJK SC',
  'sans-serif',
];

/// 暖纸底色、陶红强调与低饱和边界形成稳定层次，避免纯白大面与纯黑文字造成割裂。
/// 桌面透明度按表面与前景角色进入主题，弹窗/下拉继承同一规则；不合成整树 Opacity。
/// 触屏由调用方传默认 1，保留原有不透明呈现；平台密度、字号和焦点规则保持独立。
FThemeData stewardTheme({
  required bool dark,
  required bool touch,
  double backgroundOpacity = 1,
  double contentOpacity = 1,
}) {
  final paint = StewardPaint(
    backgroundOpacity: backgroundOpacity.clamp(0, 1),
    contentOpacity: contentOpacity.clamp(0, 1),
  );
  final base = dark ? FColors.neutralDark : FColors.neutralLight;
  final sourceColors = base.copyWith(
    background: Color(dark ? 0xff1e1d1b : 0xfff4f1e9),
    foreground: Color(dark ? 0xfff0e9df : 0xff342f29),
    card: Color(dark ? 0xff292622 : 0xfffffcf5),
    primary: Color(dark ? 0xffe8ad99 : 0xff914a3a),
    primaryForeground: Color(dark ? 0xff251915 : 0xfffffaf5),
    secondary: Color(dark ? 0xff40342d : 0xffeadbd0),
    secondaryForeground: Color(dark ? 0xffede8df : 0xff632e29),
    muted: Color(dark ? 0xff302c27 : 0xffeae5da),
    mutedForeground: Color(dark ? 0xffc1b7a9 : 0xff726859),
    border: Color(dark ? 0xff554b40 : 0xffd8cebd),
  );
  final source = _theme(sourceColors, touch, const StewardPaint());
  if (paint.backgroundOpacity == 1 && paint.contentOpacity == 1) return source;
  return stewardOpacityWidgets(
    source,
    _theme(stewardOpacityColors(sourceColors, paint), touch, paint),
    paint,
  );
}

/// 同一构造器分别生成原始色和分层色主题，确保映射只改变绘制，不复制两套密度规则。
FThemeData _theme(FColors colors, bool touch, StewardPaint paint) {
  // 字体选择只进入主题一次，标题和正文共用同一优先级；平台缺字继续使用系统回退。
  final typeface = FTypeface.inherit(
    colors: colors,
    touch: touch,
    fontFamily: stewardFontFamily,
    fontFamilyFallback: stewardFontFallback,
  );
  // Forui 的默认正文在桌面为 14px、触屏为 16px，和项目任务/邀请字段的 13px
  // 不一致。统一正文层级后，未显式指定样式的说明、开关、输入和按钮均从公共主题
  // 获得相同字号；平台差异只保留在命中区/内边距，不通过缩放整棵页面实现。
  final body = typeface.copyWith(
    xs: typeface.xs.merge(StewardText.caption),
    sm: typeface.sm.merge(StewardText.body),
    md: typeface.md.merge(StewardText.body),
  );
  final typography = FTypography(body: body, display: typeface);
  return FThemeData(
    colors: colors,
    touch: touch,
    typography: typography,
    style: FStyle.inherit(
      colors: colors,
      typography: typography,
      touch: touch,
    ).copyWith(borderRadius: const FBorderRadius().scale(0.35)),
    extensions: [
      paint,
      StewardControlDensity(touch: touch),
    ],
  );
}
