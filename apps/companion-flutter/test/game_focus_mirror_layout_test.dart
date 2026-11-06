import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/features/service.dart';
import 'package:mystia_steward_companion/ui/components.dart';

import '../tool/preview/rich_preview_workbench_model.dart';
import 'support/steward_test_fonts.dart';

/// 生产外壳与真实中文字体的布局回归。数据全部来自离线预览，测试不连接游戏。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    await loadPreferredStewardTestFont();
    await (FontLoader('packages/forui_lucide/ForuiLucideIcons')
          ..addFont(rootBundle.load('packages/forui_lucide/assets/lucide.ttf')))
        .load();
  });
  for (final desktop in [true, false]) {
    for (final scale in [100, 150]) {
      testWidgets('专注统一下拉与经营页头 ${desktop ? 'Windows最窄' : 'Android'}/$scale%', (
        tester,
      ) async {
        final width = desktop ? 627.0 : 390.0;
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(width, 1000);
        addTearDown(tester.view.resetDevicePixelRatio);
        addTearDown(tester.view.resetPhysicalSize);
        final model = RichPreviewWorkbenchModel(desktop: desktop);
        addTearDown(model.dispose);
        model.localPreferences = {
          ...model.localPreferences,
          'lastPage': 1,
          'reduceMotion': true,
          'fontScalePercent': scale,
        };
        final key = GlobalKey();
        await tester.pumpWidget(
          RepaintBoundary(
            key: key,
            child: ProviderScope(
              overrides: [workbenchProvider.overrideWithValue(model)],
              child: const StewardApp(),
            ),
          ),
        );
        await tester.pumpAndSettle();
        final title = find.byKey(const ValueKey('workbench-page-title'));
        final status = find.text('自动化已开启');
        expect(status, findsOneWidget);
        expect(
          tester.getRect(status).left,
          greaterThan(tester.getRect(title).right),
        );
        expect(
          tester.getRect(status).center.dy,
          closeTo(tester.getRect(title).center.dy, 1),
        );
        expect(
          find.descendant(of: find.byType(ServicePage), matching: status),
          findsNothing,
          reason: '自动化配置不能再占用经营Tab栏宽度。',
        );
        Future<void> capture(String name) async {
          final evidence = Platform.environment['MYSTIA_UI_EVIDENCE'];
          if (evidence == null) return;
          await tester.runAsync(() async {
            final image =
                await (key.currentContext!.findRenderObject()!
                        as RenderRepaintBoundary)
                    .toImage();
            final bytes = await image.toByteData(
              format: ui.ImageByteFormat.png,
            );
            await Directory(evidence).create(recursive: true);
            await File(
              '$evidence/$name-${desktop ? 'windows' : 'android'}-$scale.png',
            ).writeAsBytes(bytes!.buffer.asUint8List());
            image.dispose();
          });
        }

        await capture('service-header');
        final focusTab = find.descendant(
          of: find.byType(WorkspaceTabs<String>),
          matching: find.text('专注'),
        );
        await tester.tap(focusTab);
        await tester.pumpAndSettle();
        expect(find.text('专注显示'), findsOneWidget);
        expect(find.text('专注显示 · 本机'), findsNothing);
        expect(find.text('游戏内专注显示 · 游戏电脑'), findsNothing);
        await tester.tap(find.text('专注显示'));
        await tester.pumpAndSettle();
        await capture('focus-shared-options');
        final enabled = find.byKey(const ValueKey('game-focus-enabled'));
        await tester.ensureVisible(enabled);
        await tester.pumpAndSettle();
        await tester.tap(
          find.descendant(
            of: enabled,
            matching: find.byWidgetPredicate(
              (widget) => widget.runtimeType.toString() == 'FSwitch',
            ),
          ),
        );
        await tester.pumpAndSettle();
        final layout = find.byKey(const ValueKey('game-focus-scrollPercent'));
        await tester.ensureVisible(layout);
        await tester.pumpAndSettle();
        await capture('focus-layout-controls');
        expect(find.text('编辑布局'), findsNothing);
        expect(find.text('完成调整'), findsNothing);
        expect(find.text('重置布局'), findsOneWidget);
        expect(tester.takeException(), isNull);
      });
    }
  }
}
