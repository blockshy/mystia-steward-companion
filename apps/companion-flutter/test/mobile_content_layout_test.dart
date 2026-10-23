import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/service.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 仅使用内存事实检验手机内容区。高度240模拟系统键盘和应用外壳扣除后的正文，
/// 不伪装实际Android输入法；平台安全区/键盘扣减另由外壳测试与实机检查负责。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;

  const destinations = {
    'connection': '连接与设备',
    'appearance': '外观与操作',
    'updates': '更新',
    'diagnostics': '诊断',
    'help': '帮助',
  };

  Widget host(Widget child, {double? height, bool touch = true}) {
    final theme = stewardTheme(dark: false, touch: touch);
    return material.MaterialApp(
      localizationsDelegates: FLocalizations.localizationsDelegates,
      supportedLocales: FLocalizations.supportedLocales,
      theme: theme.toApproximateMaterialTheme(),
      builder: (context, child) => FTheme(
        data: theme,
        child: MediaQuery(
          data: MediaQuery.of(context).copyWith(
            textScaler: const TextScaler.linear(1.5),
            disableAnimations: true,
          ),
          child: child!,
        ),
      ),
      home: Align(
        alignment: Alignment.topCenter,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: SizedBox(height: height, child: child),
        ),
      ),
    );
  }

  void size(WidgetTester tester, double width) {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 760);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
  }

  for (final width in [360.0, 390.0]) {
    testWidgets('手机${width.toInt()}宽150%页签单行且末项/首项切换自动可见', (tester) async {
      size(tester, width);
      var value = 'connection';
      late StateSetter refresh;
      final content = StatefulBuilder(
        builder: (context, setState) {
          refresh = setState;
          return Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              WorkspaceTabs<String>(
                items: destinations,
                value: value,
                horizontalScroll: true,
                onChanged: (next) => setState(() => value = next),
              ),
            ],
          );
        },
      );
      await tester.pumpWidget(host(content));
      await tester.pumpAndSettle();
      final viewport = find.byKey(
        const ValueKey('workspace-tabs-horizontal-scroll'),
      );
      final first = find.text(destinations['connection']!);
      final last = find.text(destinations['help']!);
      // 即使未进入裁剪区的末项也只占同一行，不以放宽高度来躲避大字号问题。
      expect(
        tester.getRect(first).center.dy,
        closeTo(tester.getRect(last).center.dy, .01),
      );
      final scroll = tester.widget<SingleChildScrollView>(viewport).controller!;
      expect(scroll.position.maxScrollExtent, greaterThan(0));

      await tester.drag(viewport, const Offset(-120, 0));
      await tester.pumpAndSettle();
      final manualOffset = scroll.offset;
      expect(manualOffset, greaterThan(0));
      refresh(() {});
      await tester.pumpAndSettle();
      expect(scroll.offset, closeTo(manualOffset, .01));
      // 模拟外壳因服务端状态更新而重建同值主题；不能仅覆盖局部setState的理想情况。
      await tester.pumpWidget(host(content));
      await tester.pumpAndSettle();
      expect(scroll.offset, closeTo(manualOffset, .01));

      refresh(() => value = 'help');
      await tester.pumpAndSettle();
      var bounds = tester.getRect(viewport);
      expect(tester.getRect(last).left, greaterThanOrEqualTo(bounds.left));
      expect(tester.getRect(last).right, lessThanOrEqualTo(bounds.right));
      await tester.tap(last);
      await tester.pumpAndSettle();
      expect(value, 'help');

      refresh(() => value = 'connection');
      await tester.pumpAndSettle();
      bounds = tester.getRect(viewport);
      expect(tester.getRect(first).left, greaterThanOrEqualTo(bounds.left));
      expect(tester.getRect(first).right, lessThanOrEqualTo(bounds.right));
      expect(tester.takeException(), isNull);
    });

    for (final height in [240.0, 580.0]) {
      testWidgets('手机${width.toInt()}宽${height.toInt()}高150%经营屏障/筛选可达且保留结果', (
        tester,
      ) async {
        size(tester, width);
        final model = FixtureModel(desktop: false, orderCount: 3);
        addTearDown(model.dispose);
        model.snapshot = {
          ...model.snapshot,
          'automationSessionId': 'synthetic-mobile-session',
          'nightBusinessGeneration': 1,
          'specialBusiness': {
            'active': true,
            'displayName': '合成特殊经营',
            'ruleSummary': '完整规则仍可滚动阅读，不因手机空间不足隐藏提示。',
          },
        };
        model.business = {
          ...model.business,
          'automation': {
            ...model.automation,
            'safetyBarriers': [
              {'sequence': 1, 'title': '合成阻断', 'message': '检查合成提示后才能继续。'},
            ],
            'resourceOverview': {
              'cookers': [
                for (var index = 0; index < 5; index++)
                  {
                    'label': '合成厨具$index',
                    'normalReserved': 1,
                    'rareReserved': 1,
                    'capacity': 2,
                  },
              ],
            },
          },
        };
        await tester.pumpWidget(host(ServicePage(model), height: height));
        await tester.pumpAndSettle();
        final controlFinder = find.byKey(const ValueKey('service-controls'));
        final resultFinder = find.byType(DenseListDetail);
        final controlRect = tester.getRect(controlFinder);
        final resultRect = tester.getRect(resultFinder);
        expect(controlRect.height, lessThanOrEqualTo(320));
        expect(controlRect.height, lessThanOrEqualTo(height * .6));
        expect(resultRect.height, greaterThanOrEqualTo(height * .4));
        expect(resultRect.top, greaterThanOrEqualTo(controlRect.bottom));

        // 屏障入口随导航保持紧凑；打开后仍能访问完整说明与原确认操作。
        final entry = find.widgetWithText(StewardButton, '待确认 1');
        await tester.ensureVisible(entry);
        await tester.pumpAndSettle();
        await tester.tap(entry);
        await tester.pumpAndSettle();
        final confirmation = find.widgetWithText(StewardButton, '已人工处理');
        await tester.ensureVisible(confirmation);
        await tester.pumpAndSettle();
        expect(confirmation.hitTestable(), findsOneWidget);
        // 只验证确认入口可触达；不点击、更不向真实或模拟游戏发送写入。
        expect(tester.widget<StewardButton>(confirmation).onPressed, isNotNull);
        await tester.tap(find.widgetWithText(StewardButton, '关闭'));
        await tester.pumpAndSettle();

        final filter = find.descendant(
          of: find.byWidgetPredicate(
            (widget) => widget is StewardField && widget.title == '筛选客人或桌号',
          ),
          matching: find.byType(EditableText),
        );
        await tester.ensureVisible(filter);
        await tester.pumpAndSettle();
        await tester.tap(filter);
        await tester.enterText(filter, '博丽灵梦');
        await tester.pumpAndSettle();
        expect(tester.widget<DenseListDetail>(resultFinder).items.length, 2);
        expect(model.writes, isEmpty);
        expect(tester.takeException(), isNull);
      });
    }
  }

  testWidgets('窄桌面页签保持原有换行布局', (tester) async {
    size(tester, 360);
    await tester.pumpWidget(
      host(
        Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            WorkspaceTabs<String>(
              items: destinations,
              value: 'connection',
              onChanged: (_) {},
            ),
          ],
        ),
        touch: false,
      ),
    );
    await tester.pumpAndSettle();
    expect(
      find.byKey(const ValueKey('workspace-tabs-horizontal-scroll')),
      findsNothing,
    );
    expect(
      tester.getRect(find.text(destinations['help']!)).top,
      greaterThan(tester.getRect(find.text(destinations['connection']!)).top),
    );
    expect(tester.takeException(), isNull);
  });
}
