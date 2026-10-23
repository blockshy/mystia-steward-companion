import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';
import 'package:mystia_steward_companion/features/service_order_highlight.dart';

import 'support/fixture_model.dart';

void main() {
  test('订单通道复用共享高亮色，缺失及非法值回到同一默认色', () {
    final model = FixtureModel();
    addTearDown(model.dispose);
    model.profile = {
      'rareTargetHighlightColor': '#123aBC',
      'normalTargetHighlightColor': '#DEF456',
    };
    expect(serviceOrderHighlight(model, rare: true), const Color(0xff123abc));
    expect(serviceOrderHighlight(model, rare: false), const Color(0xffdef456));
    model.profile = {
      'rareTargetHighlightColor': '无效',
      'normalTargetHighlightColor': '#00000000',
    };
    expect(serviceOrderHighlight(model, rare: true), const Color(0xffffdb2e));
    expect(serviceOrderHighlight(model, rare: false), const Color(0xff5facd3));
  });

  for (final dark in [false, true]) {
    testWidgets('订单底色保留自定义RGB且仅受背景透明度影响：dark=$dark', (tester) async {
      tester.view.physicalSize = const Size(640, 800);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      const tint = Color(0xff2468ac);
      await tester.pumpWidget(
        material.MaterialApp(
          home: FTheme(
            data: stewardTheme(
              dark: dark,
              touch: false,
              backgroundOpacity: .4,
              contentOpacity: .2,
            ),
            child: SizedBox(
              width: 500,
              child: DenseListDetail(
                items: const [
                  {'id': 'one'},
                ],
                identity: (item) => '${item['id']}',
                title: (_) => '合成订单',
                subtitle: (_) => '只读摘要',
                itemTint: (_) => tint,
                detail: (_) => const Text('合成详情'),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      final surfaces = tester
          .widgetList<Container>(find.byType(Container))
          .map((widget) => widget.decoration)
          .whereType<BoxDecoration>()
          .map((decoration) => decoration.color)
          .whereType<Color>()
          .toList();
      // 窄屏未选择行不会借用主强调色；文字透明度 .2 不参与底色计算。
      expect(
        surfaces.any(
          (color) =>
              color.r == tint.r &&
              color.g == tint.g &&
              color.b == tint.b &&
              (color.a - .11 * .4).abs() < .001,
        ),
        isTrue,
      );
      await tester.tap(find.text('合成订单'));
      await tester.pump(const Duration(milliseconds: 250));
      await tester.pumpAndSettle();
      final detail = tester.widget<DecoratedBox>(
        find.byKey(const ValueKey('dense-detail-surface')),
      );
      final color = (detail.decoration as BoxDecoration).color!;
      expect(color.r, tint.r);
      expect(color.g, tint.g);
      expect(color.b, tint.b);
      expect(color.a, closeTo(.09 * .4, .001));
      expect(find.text('合成详情'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  }
}
