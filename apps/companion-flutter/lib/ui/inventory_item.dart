import 'dart:math' as math;

import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import 'entity_image.dart';
import 'steward_controls.dart';
import 'steward_opacity.dart';
import 'steward_style.dart';

/// 库存目录的统一浏览与勾选条目。页面负责数量可信度、身份唯一性及写入门禁，
/// 本组件只排版已确认的显示值，不把未知库存补为0，也不按名称猜测图片身份。
/// 食材/酒水共享尺寸和列顺序；选中仍由外部受控，普通轮询不会创建内部勾选副本。
class InventoryItem extends StatelessWidget {
  const InventoryItem({
    required this.name,
    required this.kind,
    required this.entityId,
    required this.stockText,
    required this.selected,
    required this.enabled,
    required this.onChanged,
    this.tags = '',
    this.readOnlyReason,
    this.showAdjustments = false,
    this.adjusting = false,
    this.onDecrease,
    this.onIncrease,
    super.key,
  });

  final String name, stockText, tags;
  final EntityImageKind kind;
  final int? entityId;
  final bool selected, enabled;
  final ValueChanged<bool>? onChanged;
  final String? readOnlyReason;
  final bool showAdjustments, adjusting;
  final VoidCallback? onDecrease, onIncrease;

  @override
  Widget build(BuildContext context) {
    final colors = context.theme.colors;
    final nameStyle = StewardText.value.copyWith(color: colors.foreground);
    final captionStyle = StewardText.caption.copyWith(
      color: colors.mutedForeground,
    );
    final imageExtent = MediaQuery.textScalerOf(context)
        .scale(StewardImageSize.name);
    final selectionHeight = math.max(44.0, stewardControlHeight(context));
    final checkboxSize = context.theme.checkboxStyle.size;

    Widget adjustments() => Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (final (text, action, callback) in [
          ('−10', '减少10；库存至少10时可用', onDecrease),
          ('+10', '增加10；调整后不能超过9999', onIncrease),
        ])
          FTooltip(
            tipBuilder: (_, _) => Text(
              adjusting ? '正在修改$name，请稍候。' : readOnlyReason ?? '$name：$action。',
            ),
            child: FButton(
              key: ValueKey(
                'inventory-adjust-${text == '−10' ? 'decrease' : 'increase'}',
              ),
              variant: FButtonVariant.ghost,
              style: stewardButtonStyle(context),
              mainAxisSize: MainAxisSize.min,
              semanticsLabel: '$name${text == '−10' ? '减少' : '增加'}10',
              onPress: adjusting ? null : callback,
              child: Text(text),
            ),
          ),
      ],
    );

    Widget information() => GestureDetector(
      behavior: HitTestBehavior.opaque,
      // 内容区点击仍可勾选，但它与增减按钮是兄弟节点；单项修改不能顺带翻转勾选。
      // 键盘和读屏的勾选入口由左侧 FCheckbox 提供，不重复暴露第二个无名称动作。
      excludeFromSemantics: true,
      onTap: enabled && onChanged != null ? () => onChanged!(!selected) : null,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          LayoutBuilder(
            builder: (context, constraints) {
              final stockStyle = DefaultTextStyle.of(context).style
                  .merge(StewardText.value);
              double widthOf(String value) {
                final painter = TextPainter(
                  text: TextSpan(text: value, style: stockStyle),
                  textDirection: Directionality.of(context),
                  textScaler: MediaQuery.textScalerOf(context),
                  locale: Localizations.maybeLocaleOf(context),
                )..layout();
                try {
                  return painter.width.ceilToDouble();
                } finally {
                  painter.dispose();
                }
              }

              // 库存保持右对齐，长名称自然换行；不缩小中文字号或截掉真实数量。
              final stockWidth = math.min(
                math.max(widthOf('库存 9999'), widthOf('库存 $stockText')),
                constraints.maxWidth * .4,
              );
              return StewardBaselineRow(
                children: [
                  StewardFirstLineCenter(
                    textStyle: nameStyle,
                    referenceText: name,
                    child: EntityImage(
                      kind: kind,
                      id: entityId,
                      fallback: kind == EntityImageKind.ingredient
                          ? FLucideIcons.leaf
                          : FLucideIcons.glassWater,
                      size: StewardImageSize.name,
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(child: Text(name, style: nameStyle)),
                  const SizedBox(width: 8),
                  SizedBox(
                    width: stockWidth,
                    child: Text.rich(
                      TextSpan(
                        children: [
                          TextSpan(text: '库存 ', style: captionStyle),
                          TextSpan(text: stockText, style: nameStyle),
                        ],
                      ),
                      textAlign: TextAlign.end,
                      style: StewardText.body,
                    ),
                  ),
                ],
              );
            },
          ),
          if (tags.isNotEmpty || readOnlyReason != null)
            Padding(
              padding: EdgeInsets.only(left: imageExtent + 8, top: 3),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (tags.isNotEmpty) Text(tags, style: captionStyle),
                  if (tags.isNotEmpty && readOnlyReason != null)
                    const SizedBox(height: StewardDensity.supportingGap),
                  if (readOnlyReason != null)
                    Text(readOnlyReason!, style: captionStyle),
                ],
              ),
            ),
        ],
      ),
    );
    return Semantics(
      container: true,
      value: adjusting ? '正在修改库存' : null,
      child: Container(
        margin: const EdgeInsets.only(bottom: 4),
        decoration: BoxDecoration(
          // 背景只受表面不透明度控制，边框走前景通道；选中不会合成整行Opacity。
          color: context.stewardPaint.surface(
            selected ? colors.primary : colors.card,
            alpha: selected ? .09 : .35,
          ),
          borderRadius: context.theme.style.borderRadius.md,
        ),
        // 边框只绘制，不向子项追加1px内边距。条目与全选共用44px选择列，
        // 因而复选图形中心严格落在同一条纵线上，而不是视觉上偏移半个边框。
        foregroundDecoration: BoxDecoration(
          border: Border.all(
            color: context.stewardPaint.foreground(
              selected ? colors.primary : colors.border,
              alpha: selected ? .7 : .6,
            ),
          ),
          borderRadius: context.theme.style.borderRadius.md,
        ),
        child: Row(
          key: const ValueKey('inventory-item-body'),
          // 选择框相对整个条目（包括换行标签与快捷按钮）居中，不再由 FLabel
          // 的首行对齐决定位置。真实44px点击区与全选的选择列一致。
          crossAxisAlignment: CrossAxisAlignment.center,
          children: [
            SizedBox(
              width: 44,
              child: Semantics(
                label: '选择$name',
                child: FCheckbox(
                  value: selected,
                  enabled: enabled,
                  onChange: enabled ? onChanged : null,
                  style: FCheckboxStyleDelta.delta(
                    trailingLabelStyle: FLabelStyleDelta.delta(
                      childPadding: EdgeInsetsGeometryDelta.value(
                        EdgeInsets.symmetric(
                          horizontal: (44 - checkboxSize) / 2,
                          vertical: (selectionHeight - checkboxSize) / 2,
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
            Expanded(
              child: Padding(
                padding: const EdgeInsets.only(top: 8, right: 10, bottom: 8),
                child: LayoutBuilder(
                  builder: (context, constraints) {
                    final inline =
                        showAdjustments &&
                        constraints.maxWidth >=
                            MediaQuery.textScalerOf(context).scale(440);
                    return inline
                        ? Row(
                            children: [
                              Expanded(child: information()),
                              const SizedBox(width: 8),
                              adjustments(),
                            ],
                          )
                        : Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              information(),
                              if (showAdjustments)
                                Align(
                                  alignment: Alignment.centerRight,
                                  child: adjustments(),
                                ),
                            ],
                          );
                  },
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
