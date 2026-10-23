import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/recommendations.dart';
import 'package:mystia_steward_companion/features/service.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/page_toolbar.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 只验证布局与输入状态：所有业务来自内存夹具，不初始化平台或连接游戏。
/// 同排几何断言使用实际页内宽度，包含桌面侧栏/边距，而不是只测窗口空白宽度。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null) {
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(File(path).readAsBytes().then(ByteData.sublistView))).load();
    }
  });

  Future<void> open(
    WidgetTester tester,
    Widget page, {
    required double width,
    required double scale,
    bool desktop = true,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    final theme = stewardTheme(dark: false, touch: !desktop);
    await tester.pumpWidget(
      material.MaterialApp(
        theme: theme.toApproximateMaterialTheme(),
        builder: (context, child) => FTheme(
          data: theme,
          child: MediaQuery(
            data: MediaQuery.of(context)
                .copyWith(textScaler: TextScaler.linear(scale)),
            child: child!,
          ),
        ),
        home: Padding(
          padding: EdgeInsets.only(
            left: desktop ? 104 : 24,
            right: 24,
            top: 16,
          ),
          child: page,
        ),
      ),
    );
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
  }

  void expectToolbar(WidgetTester tester, Finder toolbar) {
    final field = find.descendant(
      of: toolbar,
      matching: find.byType(StewardField),
    );
    final navigation = find.descendant(
      of: toolbar,
      matching: find.byWidgetPredicate((widget) => widget is WorkspaceTabs),
    );
    final fieldRect = tester.getRect(field),
        navigationRect = tester.getRect(navigation);
    final bounds = tester.getRect(toolbar);
    expect(fieldRect.center.dy, closeTo(navigationRect.center.dy, .1));
    expect(fieldRect.width, lessThanOrEqualTo(152));
    expect(fieldRect.width, greaterThanOrEqualTo(104));
    expect(navigationRect.width, greaterThan(0));
    expect(fieldRect.left, greaterThanOrEqualTo(navigationRect.right));
    expect(fieldRect.right, lessThanOrEqualTo(bounds.right));
    expect(tester.takeException(), isNull);
  }

  for (final width in [360.0, 390.0, 624.0, 640.0, 1280.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('推荐和经营 $width/$scale 搜索与相关页签同排且有界', (tester) async {
        addTearDown(tester.view.resetDevicePixelRatio);
        addTearDown(tester.view.resetPhysicalSize);
        final desktop = width >= 624;
        final model = FixtureModel(desktop: desktop, orderCount: 0);
        addTearDown(model.dispose);
        await open(
          tester,
          RecommendationsPage(model),
          width: width,
          scale: scale,
          desktop: desktop,
        );
        expectToolbar(
          tester,
          find.byKey(const ValueKey('recommendation-results-toolbar')),
        );
        await open(
          tester,
          ServicePage(model),
          width: width,
          scale: scale,
          desktop: desktop,
        );
        expectToolbar(
          tester,
          find.byKey(const ValueKey('service-results-toolbar')),
        );
        await tester.pumpWidget(const SizedBox());
      });
    }
  }

  testWidgets('连续缩放与页签横滚保持编辑实例、焦点、selection和composing', (tester) async {
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final controller = TextEditingController();
    addTearDown(controller.dispose);
    final toolbar = PageToolbar(
      controller: controller,
      searchLabel: '合成搜索',
      onSearchChanged: (_) {},
      navigation: WorkspaceTabs<String>(
        horizontalScroll: true,
        inToolbar: true,
        items: const {'a': '订单方案', 'b': '自动化状态', 'c': '完整合成诊断'},
        value: 'a',
        onChanged: (_) {},
      ),
    );
    Widget content() => Align(alignment: Alignment.topLeft, child: toolbar);
    await open(tester, content(), width: 1280, scale: 1.5);
    final field = find.byType(EditableText);
    await tester.tap(field);
    await tester.pump();
    const value = TextEditingValue(
      text: '料理nihao',
      selection: TextSelection(baseOffset: 2, extentOffset: 7),
      composing: TextRange(start: 2, end: 7),
    );
    tester.testTextInput.updateEditingValue(value);
    await tester.pump();
    final state = tester.state<EditableTextState>(field);
    final focus = tester.widget<EditableText>(field).focusNode;
    for (final width in [640.0, 624.0, 390.0, 360.0, 1280.0]) {
      await open(tester, content(), width: width, scale: 1.5);
      expect(identical(tester.state<EditableTextState>(field), state), isTrue);
      expect(
        identical(tester.widget<EditableText>(field).focusNode, focus),
        isTrue,
      );
      expect(focus.hasFocus, isTrue);
      expect(controller.value, value);
      expect(tester.takeException(), isNull);
    }
    await open(tester, content(), width: 360, scale: 1.5);
    final navigation = find.byKey(
      const ValueKey('workspace-tabs-horizontal-scroll'),
    );
    await tester.drag(navigation, const Offset(-150, 0));
    await tester.pumpAndSettle();
    expect(controller.value, value);
    expect(focus.hasFocus, isTrue);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
}
