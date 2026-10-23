import 'dart:ui' as ui;

import 'package:flutter/gestures.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

/// 读取实际合成像素，而非只断言 Border 配置；按钮悬停背景可能遮住配置正确的边框。
/// 取中间一段避开圆角与抗锯齿，所有采样点都应保留当前主题的主色下划线。
Future<void> _expectVisibleUnderline(
  WidgetTester tester,
  Color color,
  String selected,
) async {
  final boundary = tester.renderObject<RenderRepaintBoundary>(
    find.byKey(const ValueKey('tab-pixels')),
  );
  final button = find.ancestor(
    of: find.text(selected),
    matching: find.byType(FButton),
  );
  final bounds = tester.getRect(button);
  final point = boundary.globalToLocal(
    Offset(bounds.center.dx, bounds.bottom - 1),
  );
  final pixels = await tester.runAsync(() async {
    final image = await boundary.toImage(pixelRatio: 1);
    try {
      final bytes = await image.toByteData(format: ui.ImageByteFormat.rawRgba);
      return (bytes: bytes!, width: image.width);
    } finally {
      image.dispose();
    }
  });
  final expected = color.toARGB32();
  for (var offset = -8; offset <= 8; offset += 4) {
    final index =
        (point.dy.floor() * pixels!.width + point.dx.floor() + offset) * 4;
    expect(
      pixels.bytes.buffer.asUint8List(pixels.bytes.offsetInBytes + index, 4),
      [(expected >> 16) & 255, (expected >> 8) & 255, expected & 255, 255],
      reason: '激活项“$selected”的下划线必须绘制在按钮悬停、按下和焦点背景之上。',
    );
  }
}

void main() {
  for (final dark in [false, true]) {
    testWidgets('Tab 下划线在${dark ? '深色' : '浅色'}主题的悬停、按下和键盘焦点中保持可见', (
      tester,
    ) async {
      final theme = stewardTheme(dark: dark, touch: false);
      var selected = 'rare';
      await tester.pumpWidget(
        material.MaterialApp(
          theme: theme.toApproximateMaterialTheme(),
          builder: (context, child) => FTheme(data: theme, child: child!),
          home: Center(
            child: RepaintBoundary(
              key: const ValueKey('tab-pixels'),
              child: StatefulBuilder(
                builder: (context, setState) => WorkspaceTabs<String>(
                  items: const {'rare': '稀客需求', 'normal': '普客'},
                  value: selected,
                  onChanged: (value) => setState(() => selected = value),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await _expectVisibleUnderline(tester, theme.colors.primary, '稀客需求');

      final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await mouse.addPointer(location: Offset.zero);
      addTearDown(mouse.removePointer);
      await mouse.moveTo(tester.getCenter(find.text('稀客需求')));
      await tester.pumpAndSettle();
      await _expectVisibleUnderline(tester, theme.colors.primary, '稀客需求');
      await mouse.down(tester.getCenter(find.text('稀客需求')));
      await tester.pumpAndSettle();
      await _expectVisibleUnderline(tester, theme.colors.primary, '稀客需求');
      await mouse.up();
      await tester.pumpAndSettle();

      // 切换后仍让鼠标留在新激活项上，覆盖“刚选中就看不到高亮”的直接复现路径。
      await mouse.moveTo(tester.getCenter(find.text('普客')));
      await mouse.down(tester.getCenter(find.text('普客')));
      await mouse.up();
      await tester.pumpAndSettle();
      expect(selected, 'normal');
      await _expectVisibleUnderline(tester, theme.colors.primary, '普客');

      // 鼠标移开后用真实 Tab 键切换焦点，焦点轮廓不能吞掉激活下划线。
      await mouse.moveTo(Offset.zero);
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pumpAndSettle();
      expect(FocusManager.instance.primaryFocus, isNotNull);
      await _expectVisibleUnderline(tester, theme.colors.primary, '普客');
      expect(tester.takeException(), isNull);
    });
  }
}
