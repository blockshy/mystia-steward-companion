import 'package:flutter/rendering.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/ui/semantic_tag.dart';
import 'package:mystia_steward_companion/ui/steward_opacity.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

/// 从实际 Forui 样式读取填充、文本与边框，覆盖同一个 primary token 的不同用途。
/// 只检查顶层 colors 无法发现按钮/tooltip 内部覆盖 alpha 的旧问题。
List<(String, Color)> surfaces(FThemeData theme) => [
  ('工作区', theme.colors.background),
  ('输入框', theme.textFieldStyles.md.color.base!),
  (
    '禁用输入框',
    theme.textFieldStyles.md.color.resolve({FTextFieldVariant.disabled})!,
  ),
  ('选择器', theme.selectStyle.fieldStyles.md.color.base!),
  (
    '下拉弹层',
    (theme.selectStyle.contentStyle.decoration as ShapeDecoration).color!,
  ),
  (
    '下拉悬停项',
    (theme.selectStyle.contentStyle.sectionStyle.itemStyle.contentDecoration
                .resolve({FTappableVariant.hovered})
            as ShapeDecoration)
        .color!,
  ),
  ('对话框', (theme.dialogStyle.decoration as ShapeDecoration).color!),
  ('提示层', (theme.tooltipStyle.decoration as ShapeDecoration).color!),
  ('开关关闭', theme.switchStyle.trackColor.base),
  ('开关开启', theme.switchStyle.trackColor.resolve({FSwitchVariant.selected})),
  ('单选底色', theme.radioStyle.backgroundColor.base),
  (
    '复选开启',
    (theme.checkboxStyle.decoration.resolve({
      FCheckboxVariant.selected,
    }) as ShapeDecoration).color!,
  ),
  ('滑轨', theme.sliderStyles.horizontal.activeColor.base),
  (
    '禁用滑轨',
    theme.sliderStyles.horizontal.activeColor.resolve({
      FSliderVariant.disabled,
    }),
  ),
  ('滑块内部', theme.sliderStyles.horizontal.thumbStyle.color.base),
  for (final (name, button) in [
    ('功能按钮', theme.buttonStyles.primary.md),
    ('次要按钮', theme.buttonStyles.secondary.md),
    ('描边按钮', theme.buttonStyles.outline.md),
    ('危险按钮', theme.buttonStyles.destructive.md),
  ])
    for (final (state, variants) in <(String, Set<FTappableVariant>)>[
      ('默认', {}),
      ('悬停', {FTappableVariant.hovered}),
      ('按下', {FTappableVariant.pressed}),
      ('选中禁用', {FTappableVariant.selected, FTappableVariant.disabled}),
    ])
      (
        '$name/$state',
        (button.decoration.resolve(variants) as ShapeDecoration).color!,
      ),
];

List<(String, Color)> foregrounds(FThemeData theme) => [
  ('正文', theme.colors.foreground),
  ('强调', theme.colors.primary),
  ('边框', theme.colors.border),
  ('提示层文字', theme.tooltipStyle.textStyle.color!),
  ('输入文字', theme.textFieldStyles.md.contentTextStyle.base.color!),
  ('输入占位', theme.textFieldStyles.md.hintTextStyle.base.color!),
  (
    '输入焦点边框',
    theme.textFieldStyles.md.border
        .resolve({FTextFieldVariant.focused})
        .borderSide
        .color,
  ),
  ('选择器文字', theme.selectStyle.fieldStyles.md.contentTextStyle.base.color!),
  (
    '搜索文字',
    theme.selectStyle.searchStyle.fieldStyles.md.contentTextStyle.base.color!,
  ),
  ('滑块边框', theme.sliderStyles.horizontal.thumbStyle.borderColor.base),
  ('开关指示', theme.switchStyle.thumbColor.base),
  ('单选指示', theme.radioStyle.indicatorColor.base),
  ('复选勾选', theme.checkboxStyle.iconStyle.base.color!),
  for (final (name, button) in [
    ('功能按钮', theme.buttonStyles.primary.md),
    ('描边按钮', theme.buttonStyles.outline.md),
    ('危险按钮', theme.buttonStyles.destructive.md),
  ]) ...[
    ('$name文字', button.contentStyle.textStyle.base.color!),
    ('$name图标', button.contentStyle.iconStyle.base.color!),
    (
      '$name禁用文字',
      button.contentStyle.textStyle.resolve({FTappableVariant.disabled}).color!,
    ),
  ],
];

void comparePaint(
  List<(String, Color)> actual,
  List<(String, Color)> original,
  double alpha,
) {
  expect(actual.length, original.length);
  for (var index = 0; index < original.length; index++) {
    final (name, color) = actual[index];
    final source = original[index].$2;
    expect(name, original[index].$1);
    expect(color.a, closeTo(source.a * alpha, .000001), reason: '$name alpha');
    expect(color.r, closeTo(source.r, .000001), reason: '$name 红色不变');
    expect(color.g, closeTo(source.g, .000001), reason: '$name 绿色不变');
    expect(color.b, closeTo(source.b, .000001), reason: '$name 蓝色不变');
  }
}

void main() {
  for (final dark in [false, true]) {
    for (final touch in [false, true]) {
      test('背景与前景独立覆盖默认/悬停/按下/禁用/弹层 $dark/$touch', () {
        final source = stewardTheme(dark: dark, touch: touch);
        final background = stewardTheme(
          dark: dark,
          touch: touch,
          backgroundOpacity: .2,
        );
        final content = stewardTheme(
          dark: dark,
          touch: touch,
          contentOpacity: .35,
        );
        final both = stewardTheme(
          dark: dark,
          touch: touch,
          backgroundOpacity: .2,
          contentOpacity: .35,
        );
        comparePaint(surfaces(background), surfaces(source), .2);
        comparePaint(foregrounds(background), foregrounds(source), 1);
        comparePaint(surfaces(content), surfaces(source), 1);
        comparePaint(foregrounds(content), foregrounds(source), .35);
        comparePaint(surfaces(both), surfaces(source), .2);
        comparePaint(foregrounds(both), foregrounds(source), .35);
      });
    }
  }

  testWidgets('自绘标签与默认中文正文分别着色，继承颜色不重复乘透明度', (tester) async {
    final theme = stewardTheme(
      dark: false,
      touch: false,
      backgroundOpacity: .2,
      contentOpacity: .35,
    );
    await tester.pumpWidget(
      material.MaterialApp(
        theme: theme.toApproximateMaterialTheme(),
        builder: (context, child) => FTheme(data: theme, child: child!),
        home: Column(
          children: [
            const Text('默认中文正文'),
            const Icon(FLucideIcons.info),
            SemanticTag(
              text: '真实状态',
              semanticLabel: '状态：真实状态',
              icon: FLucideIcons.check,
              tone: theme.colors.primary,
            ),
          ],
        ),
      ),
    );
    final paragraph = tester.renderObject<RenderParagraph>(
      find.descendant(of: find.text('默认中文正文'), matching: find.byType(RichText)),
    );
    expect(paragraph.text.style!.color!.a, closeTo(.35, .000001));
    final tag = find.byType(SemanticTag);
    final box = tester.widget<Container>(
      find.descendant(of: tag, matching: find.byType(Container)),
    );
    final decoration = box.decoration! as BoxDecoration;
    expect(decoration.color!.a, closeTo(.07 * .2, .000001));
    expect(decoration.border!.top.color.a, closeTo(.23 * .35, .000001));
    final text = tester.widget<Text>(find.text('真实状态'));
    expect(text.style!.color!.a, closeTo(.35, .000001));
    final icon = find.descendant(of: tag, matching: find.byType(Icon));
    // 不依赖Icon显式color字段：SemanticTag改为IconTheme统一图片回退色后，
    // 检查最终绘制字形的alpha，更直接防止前景透明度遗漏或重复相乘。
    final iconParagraph = tester.renderObject<RenderParagraph>(
      find.descendant(of: icon, matching: find.byType(RichText)),
    );
    expect(iconParagraph.text.style!.color!.a, closeTo(.35, .000001));
    final context = tester.element(find.byIcon(FLucideIcons.info));
    expect(IconTheme.of(context).color!.a, closeTo(.35, .000001));
    expect(
      context.stewardPaint.surface(theme.colors.primary, alpha: .1).a,
      closeTo(.02, .000001),
    );
  });
}
