import 'dart:math' as math;

import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' show ThemeExtension;

import 'steward_style.dart';

/// 项目交互尺寸的唯一平台标记。平台由工作台能力决定，不能根据窗口宽度切换，
/// 也不能用测试宿主的 defaultTargetPlatform 推断正在验证的桌面/触屏主题。
class StewardControlDensity extends ThemeExtension<StewardControlDensity> {
  const StewardControlDensity({required this.touch});
  final bool touch;

  /// 密度是值对象，不能让一次无关库存/羁绊轮询生成的新实例触发整主题动画。
  /// 比较真实平台能力而非实例身份；切换桌面/触屏时仍必须重新计算控件几何。
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is StewardControlDensity &&
          runtimeType == other.runtimeType &&
          touch == other.touch;

  @override
  int get hashCode => Object.hash(runtimeType, touch);

  @override
  StewardControlDensity copyWith({bool? touch}) =>
      StewardControlDensity(touch: touch ?? this.touch);

  @override
  StewardControlDensity lerp(
    covariant StewardControlDensity? other,
    double t,
  ) => other == null || t < .5 ? this : other;
}

bool _touch(BuildContext context) =>
    context.theme.extension<StewardControlDensity>().touch;

/// 所有单行操作控件共享实际框体高度：桌面至少36、触屏至少44逻辑像素。
/// 必须测量真实中文字体、回退字体和 TextScaler，不能用 fontSize×height 估算，
/// 否则原生中文字体与测试默认字体会产生不同结果。额外2px为输入边界保留空间；
/// 大字号时整组一起增长，不靠压缩字体、裁切或扩大空白外壳伪装成对齐。
double stewardControlHeight(BuildContext context) {
  final touch = _touch(context);
  return math
      .max(touch ? 44 : 36, _controlLineHeight(context) + (touch ? 16 : 12) + 2)
      .ceilToDouble();
}

/// 普通文字保留真实字形的自然行高；输入内容则使用 EditableText 的固定行支架。
/// 两者不能混用：EditableText 默认 forceStrutHeight=true，中文回退字形较高时，
/// 普通 TextPainter 的高度会比输入实际行高多出数像素。若用该值反算输入 padding，
/// 会把真正绘制的输入边框缩短，尽管外层 tight 约束仍看似等高。这里复用框架的
/// StrutStyle 构造规则，不针对特定字体加 2px/3px 补偿，也不裁切输入内容。
double _controlLineHeight(BuildContext context, {bool input = false}) {
  final text = context.theme.typography.body.sm
      .merge(StewardText.body)
      .copyWith(
        fontWeight: MediaQuery.boldTextOf(context)
            ? FontWeight.w700
            : FontWeight.w500,
      );
  final painter = TextPainter(
    text: TextSpan(text: '国Ag012', style: text),
    strutStyle: input
        ? StrutStyle.fromTextStyle(text, forceStrutHeight: true)
        : null,
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

/// 只转换几何相关属性，base及所有状态共同处理，保留透明度、禁用、焦点及颜色。
FVariants<K, E, V, D>
_map<K extends FVariantConstraint, E extends FVariant, V, D extends Delta>(
  FVariants<K, E, V, D> values,
  V Function(V) convert,
) => FVariants.raw(convert(values.base), {
  for (final entry in values.variants.entries) entry.key: convert(entry.value),
});

/// 普通/主操作/ghost/列表动作使用同一个正文、内边距和最小框体高度。
/// 长中文按钮允许自然换行增高；同排单行按钮不再混用 Forui sm/md/lg 档位。
FButtonStyleDelta stewardButtonStyle(BuildContext context) =>
    _ControlButtonStyle(stewardControlHeight(context), _touch(context), false);

/// 图标与色盘属于方形操作，两个 Forui 内容分支均应用相同约束，避免调用方
/// 使用普通 FButton 放色块时绕过 iconContentStyle。图标/色块本身不强行放大。
FButtonStyleDelta stewardIconButtonStyle(BuildContext context) =>
    _ControlButtonStyle(stewardControlHeight(context), _touch(context), true);

class _ControlButtonStyle implements FButtonStyleDelta {
  _ControlButtonStyle(this.height, this.touch, this.square);
  final double height;
  final bool touch, square;

  @override
  FButtonStyle call(FButtonStyle original) {
    final constraints = square
        ? BoxConstraints.tightFor(width: height, height: height)
        : BoxConstraints(minHeight: height);
    final padding = square
        ? EdgeInsets.zero
        : EdgeInsets.symmetric(
            horizontal: touch ? 12 : 10,
            vertical: touch ? 8 : 6,
          );
    return original.copyWith(
      contentStyle: original.contentStyle.copyWith(
        constraints: constraints,
        padding: .value(padding),
        textStyle: _map(
          original.contentStyle.textStyle,
          (text) => text.copyWith(
            fontSize: StewardText.body.fontSize,
            height: StewardText.body.height,
          ),
        ),
      ),
      iconContentStyle: original.iconContentStyle.copyWith(
        constraints: BoxConstraints.tightFor(width: height, height: height),
        padding: .value(EdgeInsets.zero),
      ),
      // 按压仅改变颜色，不缩放框体；避免同行字段静止、按钮突然缩小造成再次错位。
      tappableStyle: const FTappableStyleDelta.delta(
        motion: FTappableMotion.none,
      ),
    );
  }
}

/// 只约束真正的输入区域，外置标题、说明与校验错误仍由各自表单行排版。
/// InputDecorator 的 tight 约束只固定外壳，不会强迫内部填充容器同高；因此必须
/// 按真实行高分配上下留白，使实际边框恰好填满共同高度。不能清零垂直 padding，
/// 否则会得到“36px外壳内只画18px输入框”的假对齐。外置说明不参与此计算。
FTextFieldStyleDelta stewardFieldStyle(BuildContext context) =>
    FTextFieldStyleDelta.delta(
      constraints: BoxConstraints.tightFor(
        height: stewardControlHeight(context),
      ),
      contentPadding: .value(
        EdgeInsets.symmetric(
          horizontal: _touch(context) ? 12 : 10,
          vertical: math.max(
            0,
            (stewardControlHeight(context) -
                    _controlLineHeight(context, input: true)) /
                2,
          ),
        ),
      ),
    );

/// Forui 选择器及其搜索弹层各自持有独立字段样式，必须一起接入同一高度规则。
/// 映射当前主题而非重新生成颜色，保证 Windows 背景/内容透明度分层继续有效。
FSelectStyle stewardSelectStyle(BuildContext context) {
  final original = context.theme.selectStyle;
  final field = stewardFieldStyle(context);
  FTextFieldSizeStyles fields(FTextFieldSizeStyles values) =>
      FTextFieldSizeStyles(_map(values, field.call));
  return original.copyWith(
    fieldStyles: fields(original.fieldStyles),
    searchStyle: original.searchStyle.copyWith(
      fieldStyles: fields(original.searchStyle.fieldStyles),
    ),
  );
}
