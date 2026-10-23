import 'package:flutter/services.dart';
import 'package:flutter/semantics.dart' show CustomSemanticsAction;
import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import 'components.dart';
import 'steward_opacity.dart';

/// 颜色弹窗只编辑临时 HSV 值，不读取模型、不保存配置。取消、点击遮罩或返回键
/// 均返回 null；调用方必须在确认返回后重新检查连接与权限，再提交完整 #RRGGBB。
Future<String?> showStewardColorPicker(
  BuildContext context, {
  required String title,
  required String initialColor,
}) async {
  final focus = FocusManager.instance.primaryFocus;
  final result = await showFDialog<String>(
    context: context,
    builder: (context, style, animation) => FDialog(
      animation: MediaQuery.disableAnimationsOf(context) ? null : animation,
      semanticsLabel: '$title：选择颜色',
      constraints: const BoxConstraints(minWidth: 240, maxWidth: 420),
      builder: (context, _) =>
          _ColorPicker(title: title, initialColor: initialColor),
    ),
  );
  if (focus?.context != null) focus!.requestFocus();
  return result;
}

class _ColorPicker extends StatefulWidget {
  const _ColorPicker({required this.title, required this.initialColor});
  final String title, initialColor;

  @override
  State<_ColorPicker> createState() => _ColorPickerState();
}

class _ColorPickerState extends State<_ColorPicker> {
  late HSVColor _color = HSVColor.fromColor(
    Color(0xff000000 | int.parse(widget.initialColor.substring(1), radix: 16)),
  );
  final _planeFocus = FocusNode(debugLabel: '颜色饱和度与明度');
  bool _edited = false;

  String get _hex => _edited
      ? '#${(_color.toColor().toARGB32() & 0xffffff).toRadixString(16).padLeft(6, '0').toUpperCase()}'
      : widget.initialColor;

  @override
  void dispose() {
    _planeFocus.dispose();
    super.dispose();
  }

  void _change(HSVColor value) => setState(() {
    _edited = true;
    _color = value;
  });

  void _move(double saturation, double value) => _change(
    _color
        .withSaturation(saturation.clamp(0.0, 1.0))
        .withValue(value.clamp(0.0, 1.0)),
  );

  KeyEventResult _handleKey(FocusNode _, KeyEvent event) {
    if (event is! KeyDownEvent && event is! KeyRepeatEvent) {
      return KeyEventResult.ignored;
    }
    // 二维色板与传统选择器一致：左右调饱和度，上下调明度，每步 1%。
    // 不占用 Tab/Escape，读屏和键盘仍可正常离开色板或取消整个弹窗。
    if (event.logicalKey == LogicalKeyboardKey.arrowLeft) {
      _move(_color.saturation - .01, _color.value);
    } else if (event.logicalKey == LogicalKeyboardKey.arrowRight) {
      _move(_color.saturation + .01, _color.value);
    } else if (event.logicalKey == LogicalKeyboardKey.arrowUp) {
      _move(_color.saturation, _color.value + .01);
    } else if (event.logicalKey == LogicalKeyboardKey.arrowDown) {
      _move(_color.saturation, _color.value - .01);
    } else {
      return KeyEventResult.ignored;
    }
    return KeyEventResult.handled;
  }

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.all(16),
    // 弹窗由 Forui 按安全区域和键盘收缩，内部可滚动；150% 字号或较短窗口
    // 不会把确认按钮推出视口。这里没有固定字体缩放，也不裁切说明文字。
    child: SingleChildScrollView(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            widget.title,
            style: const TextStyle(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 12),
          Focus(
            focusNode: _planeFocus,
            onFocusChange: (_) => setState(() {}),
            onKeyEvent: _handleKey,
            child: Semantics(
              label: '饱和度与明度',
              value:
                  '饱和度 ${(_color.saturation * 100).round()}%，明度 ${(_color.value * 100).round()}%',
              hint: '左右方向键调饱和度，上下方向键调明度，每次百分之一',
              customSemanticsActions: {
                const CustomSemanticsAction(label: '增加饱和度'): () =>
                    _move(_color.saturation + .01, _color.value),
                const CustomSemanticsAction(label: '减少饱和度'): () =>
                    _move(_color.saturation - .01, _color.value),
                const CustomSemanticsAction(label: '增加明度'): () =>
                    _move(_color.saturation, _color.value + .01),
                const CustomSemanticsAction(label: '减少明度'): () =>
                    _move(_color.saturation, _color.value - .01),
              },
              child: LayoutBuilder(
                builder: (context, constraints) {
                  const height = 180.0;
                  void select(Offset local) {
                    _planeFocus.requestFocus();
                    _move(
                      local.dx / constraints.maxWidth,
                      1 - local.dy / height,
                    );
                  }

                  return GestureDetector(
                    behavior: HitTestBehavior.opaque,
                    onTapDown: (event) => select(event.localPosition),
                    onPanStart: (event) => select(event.localPosition),
                    onPanUpdate: (event) => select(event.localPosition),
                    child: CustomPaint(
                      key: const ValueKey('color-picker-sv'),
                      size: Size(constraints.maxWidth, height),
                      painter: _SaturationValuePainter(
                        color: _color,
                        focused: _planeFocus.hasFocus,
                        focusColor: context.theme.colors.primary,
                        contentOpacity: context.stewardPaint.contentOpacity,
                      ),
                    ),
                  );
                },
              ),
            ),
          ),
          // 可见区域只保留色板、当前色相和颜色值；方向键说明已由色板的
          // Semantics.hint 提供，移除重复教程不改变真实键盘与读屏操作。
          const SizedBox(height: 12),
          Text('色相 ${_color.hue.round()}°'),
          // 色相渐变作为可见刻度；Forui 滑块继续提供完整焦点、方向键和读屏支持。
          Container(
            height: 8,
            decoration: const BoxDecoration(
              gradient: LinearGradient(
                colors: [
                  Color(0xffff0000),
                  Color(0xffffff00),
                  Color(0xff00ff00),
                  Color(0xff00ffff),
                  Color(0xff0000ff),
                  Color(0xffff00ff),
                  Color(0xffff0000),
                ],
              ),
            ),
          ),
          FSlider(
            key: const ValueKey('color-picker-hue'),
            tooltipControls: const FSliderTooltipControls.disabled(),
            control: FSliderControl.liftedContinuous(
              value: FSliderValue(max: _color.hue / 360),
              stepPercentage: 1 / 360,
              onChange: (value) => _change(_color.withHue(value.max * 360)),
            ),
            trackHitRegionCrossExtent: 40,
            semanticValueFormatterCallback: (value) =>
                '色相 ${(value * 360).round()} 度',
          ),
          Row(
            children: [
              Container(
                width: 32,
                height: 32,
                decoration: BoxDecoration(
                  color: _color.toColor(),
                  border: Border.all(color: context.theme.colors.border),
                ),
              ),
              const SizedBox(width: 8),
              Text(_hex, key: const ValueKey('color-picker-value')),
            ],
          ),
          const SizedBox(height: 16),
          Wrap(
            alignment: WrapAlignment.end,
            spacing: 8,
            runSpacing: 8,
            children: [
              // 与页面及其它对话框共用动作尺寸；保留可换行操作区，避免窄屏
              // 或字体放大后把按钮挤出弹窗，也不改变颜色草稿的确认/取消语义。
              StewardButton(
                '取消',
                key: const ValueKey('color-picker-cancel'),
                onPressed: () => Navigator.pop(context),
              ),
              StewardButton(
                '确认',
                key: const ValueKey('color-picker-confirm'),
                primary: true,
                onPressed: () => Navigator.pop(context, _hex),
              ),
            ],
          ),
        ],
      ),
    ),
  );
}

/// 水平白色→纯色表示饱和度，垂直透明→黑色表示明度。这里只绘制颜色与游标，
/// 所有交互、键盘和读屏逻辑都在 Widget 层，重绘不会产生保存或平台副作用。
class _SaturationValuePainter extends CustomPainter {
  const _SaturationValuePainter({
    required this.color,
    required this.focused,
    required this.focusColor,
    required this.contentOpacity,
  });
  final HSVColor color;
  final bool focused;
  final Color focusColor;
  final double contentOpacity;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = Offset.zero & size;
    canvas.drawRect(
      rect,
      Paint()
        ..shader = LinearGradient(
          colors: [
            const Color(0xffffffff),
            HSVColor.fromAHSV(1, color.hue, 1, 1).toColor(),
          ],
        ).createShader(rect),
    );
    canvas.drawRect(
      rect,
      Paint()
        ..shader = const LinearGradient(
          begin: Alignment.topCenter,
          end: Alignment.bottomCenter,
          colors: [Color(0x00000000), Color(0xff000000)],
        ).createShader(rect),
    );
    final point = Offset(
      color.saturation * size.width,
      (1 - color.value) * size.height,
    );
    // 游标压在合法边界时也完整留在色板内部，不溢出弹窗内容区。
    final cursor = Offset(
      point.dx.clamp(6.0, size.width - 6),
      point.dy.clamp(6.0, size.height - 6),
    );
    canvas.drawCircle(
      cursor,
      5,
      Paint()
        ..color = const Color(0xff000000).withValues(alpha: contentOpacity)
        ..style = PaintingStyle.stroke
        ..strokeWidth = 3,
    );
    canvas.drawCircle(
      cursor,
      5,
      Paint()
        ..color = const Color(0xffffffff).withValues(alpha: contentOpacity)
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.5,
    );
    if (focused)
      canvas.drawRect(
        rect.deflate(1),
        Paint()
          ..color = focusColor
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2,
      );
  }

  @override
  bool shouldRepaint(covariant _SaturationValuePainter oldDelegate) =>
      color != oldDelegate.color ||
      focused != oldDelegate.focused ||
      focusColor != oldDelegate.focusColor ||
      contentOpacity != oldDelegate.contentOpacity;
}
