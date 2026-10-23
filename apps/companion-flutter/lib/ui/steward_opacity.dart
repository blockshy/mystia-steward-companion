import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' show ThemeExtension;

/// 透明度按绘制职责分层，禁止再给整棵工作区套 Opacity：那会把按钮、输入框等
/// 内部背景与文字一起合成，导致“内容”设置反过来改变背景。主题扩展也会随
/// Forui 捕获的主题进入下拉、对话框和提示层，不依赖它们是否位于原页面树内。
class StewardPaint extends ThemeExtension<StewardPaint> {
  const StewardPaint({this.backgroundOpacity = 1, this.contentOpacity = 1});

  final double backgroundOpacity;
  final double contentOpacity;

  /// 模型轮询会重新构造主题。扩展必须按实际绘制值比较，否则数值未变也会
  /// 被 Forui 当成新主题反复插值，使复选框的外观 key 不必要地进入切换动画。
  /// 真正的背景/内容透明度变化仍不相等，正常主题更新与控件状态反馈继续保留。
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is StewardPaint &&
          runtimeType == other.runtimeType &&
          backgroundOpacity == other.backgroundOpacity &&
          contentOpacity == other.contentOpacity;

  @override
  int get hashCode =>
      Object.hash(runtimeType, backgroundOpacity, contentOpacity);

  /// [alpha] 是组件原来的层级浓度，不是再次叠乘传入颜色的 alpha。
  /// theme.colors 已应用用户因子，显式替换可避免调用方重复乘透明度；如需保留
  /// 原本半透明资源，请把它的原始 alpha 显式传入，不从已经处理过的主题色反推。
  Color surface(Color color, {double alpha = 1}) =>
      color.withValues(alpha: alpha * backgroundOpacity);

  Color foreground(Color color, {double alpha = 1}) =>
      color.withValues(alpha: alpha * contentOpacity);

  @override
  StewardPaint copyWith({double? backgroundOpacity, double? contentOpacity}) =>
      StewardPaint(
        backgroundOpacity: backgroundOpacity ?? this.backgroundOpacity,
        contentOpacity: contentOpacity ?? this.contentOpacity,
      );

  @override
  StewardPaint lerp(covariant StewardPaint? other, double t) => other == null
      ? this
      : StewardPaint(
          backgroundOpacity:
              backgroundOpacity +
              (other.backgroundOpacity - backgroundOpacity) * t,
          contentOpacity:
              contentOpacity + (other.contentOpacity - contentOpacity) * t,
        );
}

extension StewardPaintContext on BuildContext {
  StewardPaint get stewardPaint => theme.extension<StewardPaint>();
}

/// 先为公共语义颜色分配用途。primary/error/destructive 在项目正文中用于强调字色，
/// 因而默认属于前景；Forui 中使用这些颜色作为按钮/轨道填充的地方由下方样式映射
/// 明确覆盖。不会按颜色数值猜测用途，也不会修改字号、焦点、动画或命中区。
FColors stewardOpacityColors(FColors colors, StewardPaint paint) =>
    colors.copyWith(
      background: paint.surface(colors.background),
      card: paint.surface(colors.card),
      secondary: paint.surface(colors.secondary),
      muted: paint.surface(colors.muted),
      barrier: paint.surface(colors.barrier, alpha: colors.barrier.a),
      foreground: paint.foreground(colors.foreground),
      primary: paint.foreground(colors.primary),
      primaryForeground: paint.foreground(colors.primaryForeground),
      secondaryForeground: paint.foreground(colors.secondaryForeground),
      mutedForeground: paint.foreground(colors.mutedForeground),
      destructive: paint.foreground(colors.destructive),
      destructiveForeground: paint.foreground(colors.destructiveForeground),
      error: paint.foreground(colors.error),
      errorForeground: paint.foreground(colors.errorForeground),
      border: paint.foreground(colors.border),
    );

/// 只复制 Forui 已生成的所有具体状态，不重新手写 hover/pressed/disabled 的规则。
/// base 和 variants 同时转换，保留 disabled 原始淡化比例及平台/尺寸组合优先级。
FVariants<K, E, V, D>
_map<K extends FVariantConstraint, E extends FVariant, V, D extends Delta>(
  FVariants<K, E, V, D> values,
  V Function(V) convert,
) => FVariants.raw(convert(values.base), {
  for (final entry in values.variants.entries) entry.key: convert(entry.value),
});

/// 颜色的共享 token 无法独自表达所有绘制职责：例如 primary 同时用于按钮填充、
/// 输入框焦点边框和文本，tooltip 又反转前背景。这里从不透明源主题派生这些少量
/// 混合样式，明确区分填充/边框/字形；其它控件直接继承已分层的主题颜色即可。
FThemeData stewardOpacityWidgets(
  FThemeData source,
  FThemeData theme,
  StewardPaint paint,
) {
  Color surface(Color color) => paint.surface(color, alpha: color.a);
  Color foreground(Color color) => paint.foreground(color, alpha: color.a);
  TextStyle text(TextStyle style) => style.copyWith(
    color: style.color == null ? null : foreground(style.color!),
  );
  IconThemeData icon(IconThemeData style) => style.copyWith(
    color: style.color == null ? null : foreground(style.color!),
  );
  Decoration decoration(Decoration value) {
    // 当前锁定 Forui 使用 ShapeDecoration；保留形状及所有阴影几何，只分配颜色。
    if (value is ShapeDecoration) {
      final shape = value.shape;
      return ShapeDecoration(
        color: value.color == null ? null : surface(value.color!),
        shape: shape is OutlinedBorder
            ? shape.copyWith(
                side: shape.side.copyWith(color: foreground(shape.side.color)),
              )
            : shape,
        gradient: value.gradient,
        image: value.image,
        shadows: value.shadows
            ?.map((shadow) => shadow.copyWith(color: surface(shadow.color)))
            .toList(),
      );
    }
    // 不存在的未来装饰类型不能悄悄沿用错误透明度；升级 Forui 时测试会要求明确映射。
    throw StateError('未映射的 Forui 装饰类型：${value.runtimeType}');
  }

  FButtonStyle button(FButtonStyle style) => style.copyWith(
    decoration: _map(style.decoration, decoration),
    contentStyle: style.contentStyle.copyWith(
      textStyle: _map(style.contentStyle.textStyle, text),
      iconStyle: _map(style.contentStyle.iconStyle, icon),
      circularProgressStyle: _map(
        style.contentStyle.circularProgressStyle,
        (progress) =>
            progress.copyWith(iconStyle: .value(icon(progress.iconStyle))),
      ),
    ),
    iconContentStyle: style.iconContentStyle.copyWith(
      iconStyle: _map(style.iconContentStyle.iconStyle, icon),
    ),
    focusedOutlineStyle: style.focusedOutlineStyle.copyWith(
      color: foreground(style.focusedOutlineStyle.color),
    ),
  );
  final tooltip = theme.tooltipStyle.copyWith(
    decoration: .value(decoration(source.tooltipStyle.decoration)),
    textStyle: .value(text(source.tooltipStyle.textStyle)),
  );
  return theme.copyWith(
    buttonStyles: _map(
      source.buttonStyles,
      (sizes) => FButtonSizeStyles(_map(sizes, button)),
    ),
    checkboxStyle: theme.checkboxStyle.copyWith(
      decoration: _map(source.checkboxStyle.decoration, decoration),
    ),
    switchStyle: theme.switchStyle.copyWith(
      trackColor: _map(source.switchStyle.trackColor, surface),
      // 滑钮作为当前状态指示符，和复选框勾选图标一样属于前景。
      thumbColor: _map(source.switchStyle.thumbColor, foreground),
    ),
    sliderStyles: _map(
      theme.sliderStyles,
      (style) => style.copyWith(
        // track 的原始色来自对应不透明源主题，避免 disabled alphaBlend 引入另一因子。
        activeColor: _map(source.sliderStyles.horizontal.activeColor, surface),
        inactiveColor: _map(
          source.sliderStyles.horizontal.inactiveColor,
          surface,
        ),
        thumbStyle: style.thumbStyle.copyWith(
          color: _map(source.sliderStyles.horizontal.thumbStyle.color, surface),
        ),
        tooltipStyle: tooltip,
      ),
    ),
    tooltipStyle: tooltip,
  );
}
