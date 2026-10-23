import 'dart:async';

import 'package:flutter/gestures.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/gamepad_navigation.dart';
import 'package:mystia_steward_companion/ui/page_toolbar.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 只向展示协调器发送合成事件。原生阈值与回中由平台包纯输入测试负责，
/// 本文件验证真正的 WorkspaceTabs、焦点、模态和指针/键盘路径，不接触手柄设备。
Future<void> _open(
  WidgetTester tester,
  GamepadNavigationController controller,
  Widget child, {
  double scale = 1,
}) async {
  final theme = stewardTheme(dark: false, touch: true);
  await tester.pumpWidget(
    material.MaterialApp(
      theme: theme.toApproximateMaterialTheme(),
      builder: (context, child) => MediaQuery(
        data: MediaQuery.of(context)
            .copyWith(textScaler: TextScaler.linear(scale)),
        child: FTheme(data: theme, child: child!),
      ),
      home: GamepadNavigationScope(
        controller: controller,
        child: Padding(padding: const EdgeInsets.all(16), child: child),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  WidgetController.hitTestWarningShouldBeFatal = true;

  testWidgets('主栏回落、焦点次栏和唯一 LT/RT 提示，不让多层栏同时切换', (tester) async {
    final controller = GamepadNavigationController();
    addTearDown(controller.dispose);
    var main = 'a', secondary = 'x';
    var mainWrites = 0, secondaryWrites = 0;
    await _open(
      tester,
      controller,
      StatefulBuilder(
        builder: (context, update) => Column(
          children: [
            const GamepadHint('LB'),
            WorkspaceTabs<String>(
              key: const ValueKey('main-tabs'),
              items: const {'a': '主甲', 'b': '主乙', 'c': '主丙'},
              value: main,
              onChanged: (value) => update(() {
                main = value;
                mainWrites++;
              }),
            ),
            WorkspaceTabs<String>(
              key: const ValueKey('secondary-tabs'),
              inToolbar: true,
              items: const {'x': '次甲', 'y': '次乙'},
              value: secondary,
              onChanged: (value) => update(() {
                secondary = value;
                secondaryWrites++;
              }),
            ),
            const GamepadHint('RB'),
          ],
        ),
      ),
    );
    expect(find.text('LT'), findsNothing);
    expect(controller.handleAction('nextSubTab'), isTrue);
    await tester.pumpAndSettle();
    expect((main, secondary, mainWrites, secondaryWrites), ('b', 'x', 1, 0));
    expect(find.text('LT'), findsOneWidget);
    expect(find.text('RT'), findsOneWidget);
    expect(find.text('LB'), findsOneWidget);
    expect(find.text('RB'), findsOneWidget);
    expect(
      find.descendant(
        of: find.byKey(const ValueKey('main-tabs')),
        matching: find.text('LT'),
      ),
      findsOneWidget,
    );

    Focus.of(tester.element(find.text('次甲'))).requestFocus();
    await tester.pumpAndSettle();
    controller.handleAction('nextSubTab');
    await tester.pumpAndSettle();
    expect((main, secondary, mainWrites, secondaryWrites), ('b', 'y', 1, 1));
    expect(
      find.descendant(
        of: find.byKey(const ValueKey('secondary-tabs')),
        matching: find.text('LT'),
      ),
      findsOneWidget,
    );
    controller.handleAction('previousSubTab');
    await tester.pumpAndSettle();
    expect(secondary, 'x');
    expect(
      controller.handleAction('nextTab'),
      isFalse,
      reason: 'LB/RB 的主页面切换继续交给壳层。',
    );
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('禁用项跳过、隐藏栏不接管，模态期间 LT/RT 丢弃且不延后重放', (tester) async {
    final controller = GamepadNavigationController();
    addTearDown(controller.dispose);
    var selected = 'a', hiddenWrites = 0;
    var enabled = true;
    late StateSetter update;
    await _open(
      tester,
      controller,
      StatefulBuilder(
        builder: (context, setState) {
          update = setState;
          return Column(
            key: const ValueKey('bars'),
            children: [
              Offstage(
                offstage: true,
                child: TickerMode(
                  enabled: false,
                  child: WorkspaceTabs<String>(
                    items: const {'h1': '隐藏甲', 'h2': '隐藏乙'},
                    value: 'h1',
                    onChanged: (_) => hiddenWrites++,
                  ),
                ),
              ),
              WorkspaceTabs<String>(
                items: const {'a': '甲', 'b': '不可选', 'c': '丙'},
                value: selected,
                enabled: enabled,
                disabledItems: const {'b'},
                onChanged: (value) => update(() => selected = value),
              ),
            ],
          );
        },
      ),
    );
    controller.handleAction('nextSubTab');
    await tester.pumpAndSettle();
    expect(selected, 'c');
    expect(hiddenWrites, 0);
    update(() => enabled = false);
    await tester.pumpAndSettle();
    controller.handleAction('nextSubTab');
    await tester.pumpAndSettle();
    expect(selected, 'c');
    expect(find.text('LT'), findsNothing);
    update(() => enabled = true);
    await tester.pumpAndSettle();

    final context = tester.element(find.byKey(const ValueKey('bars')));
    unawaited(
      showFDialog<void>(
        context: context,
        builder: (context, style, animation) => FDialog(
          animation: animation,
          builder: (context, _) =>
              const Padding(padding: EdgeInsets.all(16), child: Text('模态确认')),
        ),
      ),
    );
    await tester.pumpAndSettle();
    controller.handleAction('nextSubTab');
    await tester.pumpAndSettle();
    expect(selected, 'c');
    expect(hiddenWrites, 0);
    Navigator.of(context).pop();
    await tester.pumpAndSettle();
    expect(selected, 'c');
    controller.handleAction('nextSubTab');
    await tester.pumpAndSettle();
    expect(selected, 'a');
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('鼠标键盘恢复直接输入并撤回提示，真实弹窗外层指针同样生效', (tester) async {
    final controller = GamepadNavigationController();
    addTearDown(controller.dispose);
    await _open(
      tester,
      controller,
      Column(
        key: const ValueKey('body'),
        children: [
          WorkspaceTabs<String>(
            items: const {'a': '甲', 'b': '乙'},
            value: 'a',
            onChanged: (_) {},
          ),
          const GamepadHint('LB'),
        ],
      ),
    );
    controller.handleAction('up');
    await tester.pumpAndSettle();
    expect(controller.active, isTrue);
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pumpAndSettle();
    expect(controller.active, isFalse);
    expect(find.text('LB'), findsNothing);
    controller.handleAction('up');
    await tester.pumpAndSettle();
    final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
    await mouse.addPointer(location: Offset.zero);
    await mouse.moveTo(const Offset(10, 10));
    await tester.pumpAndSettle();
    expect(controller.active, isFalse);
    final context = tester.element(find.byKey(const ValueKey('body')));
    unawaited(
      showFDialog<void>(
        context: context,
        builder: (context, style, animation) => FDialog(
          animation: animation,
          builder: (context, _) =>
              const Padding(padding: EdgeInsets.all(24), child: Text('弹窗内点击')),
        ),
      ),
    );
    await tester.pumpAndSettle();
    controller.handleAction('up');
    await tester.pumpAndSettle();
    expect(controller.active, isTrue);
    await tester.tap(find.text('弹窗内点击'));
    await tester.pumpAndSettle();
    expect(controller.active, isFalse);
    Navigator.of(context).pop();
    await tester.pumpAndSettle();
    controller.handleAction('up');
    controller.handleAction('gamepadInactive');
    await tester.pumpAndSettle();
    expect(controller.active, isFalse);
    await mouse.removePointer();
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('可搜索下拉的独立焦点域阻止扳机切换背后的横向栏', (tester) async {
    final controller = GamepadNavigationController();
    addTearDown(controller.dispose);
    var selected = 'a';
    await _open(
      tester,
      controller,
      StatefulBuilder(
        builder: (context, update) => Column(
          children: [
            WorkspaceTabs<String>(
              items: const {'a': '主甲', 'b': '主乙'},
              value: selected,
              onChanged: (value) => update(() => selected = value),
            ),
            StewardSelect<String>(
              label: '目录',
              value: 'a',
              items: const {'a': '条目甲', 'b': '条目乙'},
              onChanged: (_) {},
              searchable: true,
            ),
          ],
        ),
      ),
    );
    await tester.tap(find.text('条目甲').first);
    await tester.pumpAndSettle();
    // Forui 的已选值本身也是只读 EditableText，不能把它与弹层搜索框混为一项。
    final search = find.descendant(
      of: find.byWidgetPredicate(
        (widget) => widget is FTextField && widget.hint == '搜索目录',
      ),
      matching: find.byWidgetPredicate(
        (widget) => widget is EditableText && !widget.readOnly,
      ),
    );
    expect(search, findsOneWidget);
    expect(
      tester.widget<EditableText>(search).focusNode.hasFocus,
      isFalse,
      reason: '触屏下拉打开尚未聚焦，不能靠后续输入才建立导航门禁。',
    );
    expect(controller.navigationBlocked, isTrue);
    for (final action in [
      'previousTab',
      'nextTab',
      'previousSubTab',
      'nextSubTab',
    ]) {
      expect(controller.handleAction(action), isTrue);
    }
    await tester.pumpAndSettle();
    expect(selected, 'a');
    await tester.enterText(search, '条目');
    await tester.pump();
    expect(tester.widget<EditableText>(search).focusNode.hasFocus, isTrue);
    controller.handleAction('nextSubTab');
    await tester.pumpAndSettle();
    expect(selected, 'a');
    expect(find.text('LT'), findsNothing);
    expect(tester.widget<EditableText>(search).controller.text, '条目');
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
    expect(controller.navigationBlocked, isFalse);
  });

  testWidgets('普通下拉打开即阻断，失效隐藏与销毁各自释放门禁', (tester) async {
    final controller = GamepadNavigationController();
    addTearDown(controller.dispose);
    var selected = 'a';
    var revision = 0;
    var visible = true;
    late StateSetter update;
    await _open(
      tester,
      controller,
      StatefulBuilder(
        builder: (context, setState) {
          update = setState;
          return Column(
            children: [
              WorkspaceTabs<String>(
                items: const {'a': '主甲', 'b': '主乙'},
                value: selected,
                onChanged: (value) => update(() => selected = value),
              ),
              Offstage(
                offstage: !visible,
                child: TickerMode(
                  enabled: visible,
                  child: StewardSelect<String>(
                    label: '普通目录',
                    value: 'a',
                    items: const {'a': '条目甲', 'b': '条目乙'},
                    boundary: revision,
                    onChanged: (_) {},
                  ),
                ),
              ),
            ],
          );
        },
      ),
    );
    await tester.tap(find.text('条目甲').first);
    await tester.pumpAndSettle();
    expect(controller.navigationBlocked, isTrue);
    expect(controller.handleAction('nextTab'), isTrue);
    controller.handleAction('nextSubTab');
    await tester.pumpAndSettle();
    expect(selected, 'a');
    update(() => revision++);
    await tester.pumpAndSettle();
    expect(
      controller.navigationBlocked,
      isFalse,
      reason: '连接/目录代次失效关闭弹层后，旧owner不得残留。',
    );
    controller.handleAction('nextSubTab');
    await tester.pumpAndSettle();
    expect(selected, 'b');
    await tester.tap(find.text('条目甲').first);
    await tester.pumpAndSettle();
    expect(controller.navigationBlocked, isTrue);
    update(() => visible = false);
    await tester.pumpAndSettle();
    expect(controller.navigationBlocked, isFalse);
    update(() => visible = true);
    await tester.pumpAndSettle();
    await tester.pumpWidget(const SizedBox());
    expect(controller.navigationBlocked, isFalse);
    expect(tester.takeException(), isNull);
  });

  testWidgets('壳层同帧肩键后扳机不误切旧页面，下拉期间两层导航均不穿透', (tester) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(1280, 900);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final model = FixtureModel(orderCount: 1);
    model.localPreferences = {'reduceMotion': true};
    addTearDown(model.dispose);
    await tester.runAsync(() => rootBundle.loadString('pubspec.yaml'));
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pumpAndSettle();
    model.input('nextTab');
    model.input('nextSubTab');
    await tester.pumpAndSettle();
    final recommendationTabs = find.byWidgetPredicate(
      (widget) =>
          widget is WorkspaceTabs<String> &&
          widget.items.containsKey('favorites'),
    );
    expect(
      tester.widget<WorkspaceTabs<String>>(recommendationTabs).value,
      'normal',
    );
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('workbench-page-title')))
          .data,
      '推荐',
    );

    // 同路由的搜索弹层不能被 LB/RB 或 LT/RT 穿透；真实点击和中文输入使
    // 官方弹层获得独立焦点域，而非用手工 mock 的“模态=true”绕过集成路径。
    await tester.tap(
      find.byWidgetPredicate(
        (widget) => widget is StewardSelect<String> && widget.label == '经营地点',
      ),
    );
    await tester.pumpAndSettle();
    // 触屏平台的官方搜索框默认不抢焦点，必须实际输入后再检查独立焦点域；
    // 依赖“弹层打开即 hasFocus”会在测试平台上找不到真实可编辑控件。
    final dropdownSearch = find.descendant(
      of: find.byWidgetPredicate(
        (widget) => widget is FTextField && widget.hint == '搜索经营地点',
      ),
      matching: find.byWidgetPredicate(
        (widget) => widget is EditableText && !widget.readOnly,
      ),
    );
    expect(dropdownSearch, findsOneWidget);
    expect(
      tester.widget<EditableText>(dropdownSearch).focusNode.hasFocus,
      isFalse,
    );
    // 未点击搜索前也逐个尝试两只肩键和扳机，不能把两次相反跳页的净结果
    // 恰好相同误当作阻断成功，因此每次事件后都核对主页面与当前子页。
    for (final action in [
      'previousTab',
      'nextTab',
      'previousSubTab',
      'nextSubTab',
    ]) {
      model.input(action);
      await tester.pumpAndSettle();
      expect(
        tester
            .widget<Text>(find.byKey(const ValueKey('workbench-page-title')))
            .data,
        '推荐',
      );
      expect(
        tester.widget<WorkspaceTabs<String>>(recommendationTabs).value,
        'normal',
      );
      expect(dropdownSearch, findsOneWidget);
    }
    await tester.enterText(dropdownSearch, '博丽');
    await tester.pump();
    expect(
      tester.widget<EditableText>(dropdownSearch).focusNode.hasFocus,
      isTrue,
    );
    model.input('nextTab');
    model.input('nextSubTab');
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('workbench-page-title')))
          .data,
      '推荐',
    );
    expect(
      tester.widget<WorkspaceTabs<String>>(recommendationTabs).value,
      'normal',
    );
    expect(tester.widget<EditableText>(dropdownSearch).controller.text, '博丽');
    await tester.tap(find.byKey(const ValueKey('workbench-page-title')));
    await tester.pumpAndSettle();
    model.input('nextSubTab');
    await tester.pumpAndSettle();
    expect(
      tester.widget<WorkspaceTabs<String>>(recommendationTabs).value,
      'rare',
    );
    model.input('previousTab');
    await tester.pumpAndSettle();
    final serviceTabs = find.byWidgetPredicate(
      (widget) =>
          widget is WorkspaceTabs<String> && widget.items.containsKey('focus'),
    );
    expect(
      tester.widget<WorkspaceTabs<String>>(serviceTabs).value,
      'rare',
      reason: '同帧被丢弃的 LT/RT 不能改动旧经营页或在稍后返回时重放。',
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final width in [360.0, 390.0, 640.0]) {
    testWidgets('手柄提示在 $width / 150% 工具栏有界且保留搜索组词与焦点', (tester) async {
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = Size(width, 600);
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.view.resetPhysicalSize);
      final controller = GamepadNavigationController();
      final search = TextEditingController();
      addTearDown(controller.dispose);
      addTearDown(search.dispose);
      var selected = 'food';
      await _open(
        tester,
        controller,
        StatefulBuilder(
          builder: (context, update) => Column(
            children: [
              PageToolbar(
                controller: search,
                searchLabel: '筛选名称',
                onSearchChanged: (_) {},
                navigation: WorkspaceTabs<String>(
                  inToolbar: true,
                  horizontalScroll: true,
                  items: const {'food': '料理', 'beverage': '酒水'},
                  value: selected,
                  onChanged: (value) => update(() => selected = value),
                ),
              ),
            ],
          ),
        ),
        scale: 1.5,
      );
      final field = find.byType(EditableText);
      await tester.enterText(field, '海鲜汤');
      final draft = search.value.copyWith(
        composing: const TextRange(start: 0, end: 2),
        selection: const TextSelection.collapsed(offset: 2),
      );
      search.value = draft;
      final element = tester.element(field);
      final focus = tester.widget<EditableText>(field).focusNode;
      controller.handleAction('nextSubTab');
      await tester.pumpAndSettle();
      expect(selected, 'beverage');
      expect(search.value, draft);
      expect(focus.hasFocus, isTrue);
      expect(identical(tester.element(field), element), isTrue);
      expect(find.text('LT'), findsOneWidget);
      expect(find.text('RT'), findsOneWidget);
      for (final hint in ['LT', 'RT']) {
        final rect = tester.getRect(find.text(hint));
        expect(rect.left, greaterThanOrEqualTo(16));
        expect(rect.right, lessThanOrEqualTo(width - 16));
      }
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }
}
