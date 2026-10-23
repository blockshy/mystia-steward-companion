import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/game_media_scope.dart';
import 'package:mystia_steward_companion/ui/inventory_item.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/steward_test_fonts.dart';
import 'support/text_geometry.dart';

/// 使用实际正文中文字体和正式壳层剩余宽度检查库存项目，不把640px窗口直接当成
/// 640px正文。项目只收合成显示数据，测试不会读真实库存、发HTTP或写游戏状态。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(loadPreferredStewardTestFont);

  Widget host(Widget child, {required bool desktop, required double scale}) {
    final theme = stewardTheme(
      dark: !desktop,
      touch: !desktop,
      backgroundOpacity: .6,
      contentOpacity: .8,
    );
    return material.MaterialApp(
      locale: const Locale('zh', 'CN'),
      localizationsDelegates: FLocalizations.localizationsDelegates,
      supportedLocales: FLocalizations.supportedLocales,
      theme: theme.toApproximateMaterialTheme(),
      builder: (context, routed) => FTheme(
        data: theme,
        child: MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: TextScaler.linear(scale)),
          child: routed!,
        ),
      ),
      home: Padding(
        padding: EdgeInsets.fromLTRB(
          desktop ? 104 : 16,
          16,
          desktop ? 24 : 16,
          16,
        ),
        child: Align(alignment: Alignment.topLeft, child: child),
      ),
    );
  }

  Finder check(String key) => find.descendant(
    of: find.byKey(ValueKey(key)),
    matching: find.byType(FCheckbox),
  );
  // pump结束后不直接查询仅允许布局/绘制阶段使用的RenderBox基线；共享工具
  // 使用实际段落的字体、缩放和宽度进行只读测量，仍保持原0.1px对齐容差。
  double baseline(WidgetTester tester, String text) =>
      textBaseline(tester, find.text(text));

  for (final desktop in [true, false]) {
    for (final scale in [1.0, 1.5]) {
      for (final kind in [
        EntityImageKind.ingredient,
        EntityImageKind.beverage,
      ]) {
        testWidgets('库存项目 $kind ${desktop ? 627 : 390}px/$scale 名称库存对齐且整行可选', (
          tester,
        ) async {
          final width = desktop ? 627.0 : 390.0;
          tester.view.devicePixelRatio = 1;
          tester.view.physicalSize = Size(width, 850);
          addTearDown(tester.view.resetPhysicalSize);
          addTearDown(tester.view.resetDevicePixelRatio);
          var selected = false;
          final requests = <(String, int)>[];
          await tester.pumpWidget(
            host(
              GameMediaScope(
                resolveImage: (namespace, id) {
                  requests.add((namespace, id));
                  return null; // 缺图仍须保留同尺寸图标，不按名称寻找别的资源。
                },
                child: StatefulBuilder(
                  builder: (_, setState) => Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      InventoryItem(
                        key: const ValueKey('long'),
                        name: '精选山珍蘑菇与竹笋',
                        kind: kind,
                        entityId: 17,
                        stockText: '9999',
                        tags: '山珍 · 清淡 · 合成目录标签',
                        selected: selected,
                        enabled: true,
                        onChanged: (value) => setState(() => selected = value),
                      ),
                      InventoryItem(
                        key: const ValueKey('short'),
                        name: '合成豆腐',
                        kind: kind,
                        entityId: 18,
                        stockText: '0',
                        selected: false,
                        enabled: true,
                        onChanged: (_) {},
                      ),
                    ],
                  ),
                ),
              ),
              desktop: desktop,
              scale: scale,
            ),
          );
          await tester.pumpAndSettle();
          expect(
            requests,
            containsAll([(kind.namespace, 17), (kind.namespace, 18)]),
          );
          final name = tester.getRect(find.text('精选山珍蘑菇与竹笋'));
          final stock = tester.getRect(find.text('库存 9999'));
          final shortStock = tester.getRect(find.text('库存 0'));
          expect(name.right, lessThanOrEqualTo(stock.left));
          expect(stock.right, closeTo(shortStock.right, .1));
          expect(
            baseline(tester, '精选山珍蘑菇与竹笋'),
            closeTo(baseline(tester, '库存 9999'), .1),
          );
          expect(
            tester.getSize(check('long')).height,
            greaterThanOrEqualTo(44),
          );
          expect(tester.getSize(check('short')).height, lessThanOrEqualTo(64));
          for (final element in find.byType(EntityImage).evaluate()) {
            expect(
              tester.getSize(find.byWidget(element.widget)),
              Size.square(28 * scale),
            );
          }
          // 点击数量列也属于同一个受控复选框，不能为了排版将其变成无响应的空白。
          await tester.tap(find.text('库存 9999'));
          await tester.pumpAndSettle();
          expect(selected, isTrue);
          expect(tester.widget<FCheckbox>(check('long')).value, isTrue);
          final itemSurface = tester
              .widgetList<Container>(
                find.descendant(
                  of: find.byKey(const ValueKey('long')),
                  matching: find.byType(Container),
                ),
              )
              .firstWhere((widget) => widget.decoration is BoxDecoration);
          final decoration = itemSurface.decoration! as BoxDecoration;
          expect(decoration.color!.a, closeTo(.09 * .6, .000001));
          await tester.tap(find.text('精选山珍蘑菇与竹笋'));
          await tester.pumpAndSettle();
          expect(selected, isFalse);
          expect(tester.takeException(), isNull);
        });
      }
    }
  }

  testWidgets('无限与未知项目保留原因和目录标签，不公开修改回调', (tester) async {
    var changes = 0;
    await tester.pumpWidget(
      host(
        Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            for (final stock in ['无限', '未知'])
              InventoryItem(
                key: ValueKey(stock),
                name: '合成$stock库存',
                kind: EntityImageKind.ingredient,
                entityId: null,
                stockText: stock,
                tags: '保留目录标签',
                readOnlyReason: stock == '无限'
                    ? '无限库存，不能修改数量。'
                    : '库存数量未知，读取到有效数量后才能修改。',
                selected: false,
                enabled: false,
                onChanged: (_) => changes++,
              ),
          ],
        ),
        desktop: false,
        scale: 1.5,
      ),
    );
    await tester.pumpAndSettle();
    for (final stock in ['无限', '未知']) {
      expect(find.text('库存 $stock'), findsOneWidget);
      expect(tester.widget<FCheckbox>(check(stock)).enabled, isFalse);
      expect(tester.widget<FCheckbox>(check(stock)).onChange, isNull);
    }
    expect(find.text('保留目录标签'), findsNWidgets(2));
    expect(find.text('无限库存，不能修改数量。'), findsOneWidget);
    expect(find.text('库存数量未知，读取到有效数量后才能修改。'), findsOneWidget);
    expect(changes, 0);
    expect(tester.takeException(), isNull);
  });
}
