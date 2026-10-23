import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/service_focus.dart';
import 'package:mystia_steward_companion/ui/components.dart';

import 'support/fixture_model.dart';

class _FailingFocusModel extends FixtureModel {
  bool failSave = false;
  @override
  Future<void> updateLocal(DataMap changes) async {
    if (failSave) throw StateError('合成显示偏好保存失败');
    await super.updateLocal(changes);
  }
}

/// 整壳回归使用内存模型：验证本机配置、稳定子树和手柄恢复，不调用游戏或平台存储。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    final font = Platform.environment['MYSTIA_UI_FONT'];
    if (font != null) {
      final bytes = await File(font).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });

  Future<void> open(
    WidgetTester tester,
    FixtureModel model,
    double width,
  ) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 900);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    model.localPreferences = {...model.localPreferences, 'reduceMotion': true};
    await tester.runAsync(() => rootBundle.loadString('pubspec.yaml'));
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pumpAndSettle();
  }

  Finder tab(String name) => find.descendant(
    of: find.byType(WorkspaceTabs<String>),
    matching: find.text(name),
  );
  Finder uiSwitch() => find.descendant(
    of: find.byKey(const ValueKey(serviceFocusWindowUiPreference)),
    matching: find.byType(FSwitch),
  );

  for (final desktop in [true, false]) {
    testWidgets('${desktop ? 'Windows最窄' : 'Android'}专注可隐藏/恢复外框且列表State不变', (
      tester,
    ) async {
      final model = FixtureModel(desktop: desktop, orderCount: 12);
      addTearDown(model.dispose);
      await open(tester, model, desktop ? 627 : 390);
      await tester.tap(tab('专注'));
      await tester.pumpAndSettle();
      final scrollFinder = find.descendant(
        of: find.byKey(const ValueKey('service-focus-scroll')),
        matching: find.byType(Scrollable),
      );
      final state = tester.state<ScrollableState>(scrollFinder);
      await tester.tap(find.text('专注显示 · 本机'));
      await tester.pumpAndSettle();
      await tester.ensureVisible(uiSwitch());
      await tester.tap(uiSwitch());
      await tester.pumpAndSettle();
      expect(model.localPreferences[serviceFocusWindowUiPreference], isFalse);
      expect(find.byKey(const ValueKey('workbench-page-title')), findsNothing);
      expect(find.byKey(const ValueKey('workbench-status-bar')), findsNothing);
      expect(
        find.byKey(
          ValueKey(
            desktop
                ? 'workbench-navigation-rail'
                : 'workbench-bottom-navigation',
          ),
        ),
        findsNothing,
      );
      expect(tab('稀客'), findsNothing);
      expect(uiSwitch(), findsOneWidget);
      expect(
        identical(tester.state<ScrollableState>(scrollFinder), state),
        isTrue,
      );

      // LT/RT 不得切换已经收起的经营导航；LB/RB 仍能离开专注，防止手柄被困。
      model.input('nextSubTab');
      await tester.pumpAndSettle();
      expect(
        find.byKey(const ValueKey('service-focus-scroll')),
        findsOneWidget,
      );
      model.input('nextTab');
      await tester.pumpAndSettle();
      expect(
        find.byKey(const ValueKey('workbench-page-title')),
        findsOneWidget,
      );
      model.input('previousTab');
      await tester.pumpAndSettle();
      expect(find.byKey(const ValueKey('workbench-page-title')), findsNothing);
      expect(
        identical(tester.state<ScrollableState>(scrollFinder), state),
        isTrue,
      );
      await tester.ensureVisible(uiSwitch());
      await tester.tap(uiSwitch());
      await tester.pumpAndSettle();
      expect(model.localPreferences[serviceFocusWindowUiPreference], isTrue);
      expect(
        find.byKey(const ValueKey('workbench-page-title')),
        findsOneWidget,
      );
      expect(tab('稀客'), findsOneWidget);
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('隐藏偏好只在进入专注后生效，重建后仍可恢复完整UI', (tester) async {
    final model = FixtureModel(orderCount: 1)
      ..localPreferences = {serviceFocusWindowUiPreference: false};
    addTearDown(model.dispose);
    await open(tester, model, 627);
    expect(find.byKey(const ValueKey('workbench-page-title')), findsOneWidget);
    await tester.tap(tab('专注'));
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('workbench-page-title')), findsNothing);
    await tester.tap(find.text('专注显示 · 本机'));
    await tester.pumpAndSettle();
    await tester.tap(uiSwitch());
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('workbench-page-title')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('外框隐藏时保存失败仍可查看全文，恢复后撤回错误', (tester) async {
    final model = _FailingFocusModel()
      ..localPreferences = {serviceFocusWindowUiPreference: false};
    addTearDown(model.dispose);
    await open(tester, model, 627);
    await tester.tap(tab('专注'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('专注显示 · 本机'));
    await tester.pumpAndSettle();
    model.failSave = true;
    await tester.tap(uiSwitch());
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('workbench-page-title')), findsNothing);
    final error = find.byKey(const ValueKey('workbench-page-activity-button'));
    expect(error, findsOneWidget);
    await tester.tap(error);
    await tester.pumpAndSettle();
    expect(find.textContaining('合成显示偏好保存失败'), findsWidgets);
    await tester.tap(find.text('关闭'));
    await tester.pumpAndSettle();
    model.failSave = false;
    await tester.tap(uiSwitch());
    await tester.pumpAndSettle();
    expect(model.localPreferences[serviceFocusWindowUiPreference], isTrue);
    expect(find.byKey(const ValueKey('workbench-page-title')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
