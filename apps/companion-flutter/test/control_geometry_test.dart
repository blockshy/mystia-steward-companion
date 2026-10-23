import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/page_toolbar.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/steward_test_fonts.dart';

/// 测量真正绘制按钮填充/边框的 DecoratedBox，不能只比较外层 ConstrainedBox。
/// 后者即使等高，也不能证明用户看到的按钮与输入框等高。
Finder _buttonPaint(Finder owner) => find
    .descendant(
      of: owner,
      matching: find.byWidgetPredicate(
        (widget) =>
            widget is DecoratedBox && widget.decoration is ShapeDecoration,
      ),
    )
    .first;

/// InputDecorator 自身仍是布局外壳：tight 约束可能把外壳撑高，但内部真正绘制
/// 填充/边框的 container 仍只有文字行高。必须从其后代 EditableText 的 context
/// 调用 material_ui 公开 containerOf；不能用 InputDecorator/Forui 外壳的矩形
/// 或样式声明值替代。该 API 返回的 RenderBox 不包含标签、说明、错误和 counter。
Rect _inputPaint(WidgetTester tester, Finder owner) {
  final decorator = find.descendant(
    of: owner,
    matching: find.byType(material.InputDecorator),
  );
  expect(decorator, findsOneWidget);
  final editable = find.descendant(
    of: decorator,
    matching: find.byType(EditableText),
  );
  expect(editable, findsOneWidget);
  // containerOf 查找的是祖先 RenderObject，因此传 EditableText 的 Element，
  // 而不是传 InputDecorator 自身，避免漏掉正在验证的这一层装饰。
  final container = material.InputDecorator.containerOf(
    tester.element(editable),
  );
  expect(container, isNotNull, reason: '必须取得实际绘制填充/边框的输入容器');
  return container!.localToGlobal(Offset.zero) & container.size;
}

void _expectInputFits(WidgetTester tester, Finder owner, double height) {
  final bounds = _inputPaint(tester, owner);
  expect(bounds.height, closeTo(height, .1));
  final editable = find.descendant(
    of: owner,
    matching: find.byType(EditableText),
  );
  expect(editable, findsOneWidget);
  final text = tester.getRect(editable);
  expect(text.top, greaterThanOrEqualTo(bounds.top));
  expect(text.bottom, lessThanOrEqualTo(bounds.bottom));
}

Widget _host(Widget child, {required bool touch, required double scale}) {
  // 同时使用非默认透明度，确保统一尺寸没有通过替换完整 Forui 样式回退绘制分层。
  final theme = stewardTheme(
    dark: touch,
    touch: touch,
    backgroundOpacity: .65,
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
        data: MediaQuery.of(context).copyWith(
          textScaler: TextScaler.linear(scale),
          disableAnimations: true,
        ),
        child: routed!,
      ),
    ),
    home: Padding(padding: const EdgeInsets.all(16), child: child),
  );
}

void _viewport(WidgetTester tester) {
  tester.view.devicePixelRatio = 1;
  tester.view.physicalSize = const Size(800, 920);
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
}

void main() => runControlGeometryTests();

/// 主专项加载生产优先字体；独立 fallback 文件复用完全相同的几何和交互断言。
/// 字体注册在测试引擎内不能撤销，因此两种来源必须在不同测试文件的隔离引擎执行，
/// 不能在同一文件里“切开关”后误以为已经测试了系统缺少优先字体的场景。
void runControlGeometryTests({bool loadPreferredFont = true}) {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    if (loadPreferredFont) await loadPreferredStewardTestFont();
    // 验证脚本以 MYSTIA_UI_FONT 指向实际中文字体。注册产品字体族才能覆盖中文
    // 字形回退和真实行高；未提供字体的环境只能作为基本布局回归，不能声称字体实测。
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null) {
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(File(path).readAsBytes().then(ByteData.sublistView))).load();
    }
  });

  for (final touch in [false, true]) {
    for (final scale in [1.0, 1.5]) {
      final scenario = '${touch ? '触屏' : '桌面'} ${(scale * 100).toInt()}%';
      testWidgets('真实输入/主次按钮/图标与工具栏页签框体等高：$scenario', (tester) async {
        _viewport(tester);
        final search = TextEditingController(text: '中文Ag012');
        addTearDown(search.dispose);
        await tester.pumpWidget(
          _host(
            Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  children: [
                    const SizedBox(
                      width: 180,
                      child: StewardField(
                        '目标数量',
                        key: ValueKey('plain-field'),
                        showLabel: false,
                        initial: '中文Ag012',
                      ),
                    ),
                    const SizedBox(width: 8),
                    StewardButton(
                      '确认',
                      key: const ValueKey('primary-button'),
                      primary: true,
                      onPressed: () {},
                    ),
                    const SizedBox(width: 8),
                    StewardButton(
                      '取消',
                      key: const ValueKey('outline-button'),
                      onPressed: () {},
                    ),
                    const SizedBox(width: 8),
                    StewardIconButton(
                      key: const ValueKey('icon-button'),
                      label: '刷新',
                      icon: FLucideIcons.refreshCw,
                      onPressed: () {},
                    ),
                  ],
                ),
                const SizedBox(height: 16),
                PageToolbar(
                  controller: search,
                  searchLabel: '订单搜索',
                  searchKey: const ValueKey('toolbar-field'),
                  onSearchChanged: (_) {},
                  navigation: WorkspaceTabs<String>(
                    key: const ValueKey('toolbar-tabs'),
                    inToolbar: true,
                    horizontalScroll: true,
                    items: const {'normal': '普客', 'rare': '稀客'},
                    value: 'normal',
                    onChanged: (_) {},
                  ),
                  actions: [
                    StewardButton(
                      '刷新',
                      key: const ValueKey('toolbar-action'),
                      onPressed: () {},
                    ),
                  ],
                ),
              ],
            ),
            touch: touch,
            scale: scale,
          ),
        );
        await tester.pumpAndSettle();
        final field = find.byKey(const ValueKey('plain-field'));
        final height = stewardControlHeight(tester.element(field));
        expect(height, greaterThanOrEqualTo(touch ? 44 : 36));
        _expectInputFits(tester, field, height);
        final fieldBounds = _inputPaint(tester, field);
        for (final key in ['primary-button', 'outline-button', 'icon-button']) {
          final bounds = tester.getRect(
            _buttonPaint(find.byKey(ValueKey(key))),
          );
          expect(bounds.height, closeTo(height, .1), reason: '$key 实际绘制高度');
          expect(bounds.center.dy, closeTo(fieldBounds.center.dy, .1));
          if (key == 'icon-button') expect(bounds.width, closeTo(height, .1));
        }
        final primaryPaint = _buttonPaint(
          find.byKey(const ValueKey('primary-button')),
        );
        final decoration =
            tester.widget<DecoratedBox>(primaryPaint).decoration
                as ShapeDecoration;
        expect(
          decoration.color!.a,
          closeTo(.65, .000001),
          reason: '统一尺寸只改变几何，不能把功能按钮的表面透明度重置为不透明',
        );
        final primaryBounds = tester.getRect(primaryPaint);
        final pointer = await tester.startGesture(primaryBounds.center);
        await tester.pump(const Duration(milliseconds: 100));
        expect(
          tester.getRect(primaryPaint),
          primaryBounds,
          reason: '按下按钮时也不能缩小框体而让同行输入框显得更高',
        );
        await pointer.up();
        await tester.pumpAndSettle();
        final toolbarField = find.byKey(const ValueKey('toolbar-field'));
        _expectInputFits(tester, toolbarField, height);
        final toolbarBounds = _inputPaint(tester, toolbarField);
        final tabs = find.descendant(
          of: find.byKey(const ValueKey('toolbar-tabs')),
          matching: find.byType(FButton),
        );
        expect(tabs, findsNWidgets(2));
        for (final button in [
          ...tabs.evaluate().map((element) => find.byWidget(element.widget)),
          find.byKey(const ValueKey('toolbar-action')),
        ]) {
          final bounds = tester.getRect(_buttonPaint(button));
          expect(bounds.height, closeTo(height, .1));
          expect(bounds.center.dy, closeTo(toolbarBounds.center.dy, .1));
        }
        expect(tester.takeException(), isNull);
      });

      testWidgets('普通/搜索下拉及弹层搜索采用同一框体且仍可选择：$scenario', (tester) async {
        _viewport(tester);
        final accepted = <int>[];
        await tester.pumpWidget(
          _host(
            Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                for (final searchable in [false, true])
                  Padding(
                    padding: const EdgeInsets.only(bottom: 16),
                    child: SizedBox(
                      width: stewardSelectWidth,
                      child: StewardSelect<int>(
                        key: ValueKey('select-$searchable'),
                        label: searchable ? '搜索选择' : '普通选择',
                        showLabel: false,
                        searchable: searchable,
                        searchHint: '搜索候选',
                        value: 1,
                        items: const {1: '味噌', 2: '豆腐'},
                        onChanged: accepted.add,
                      ),
                    ),
                  ),
              ],
            ),
            touch: touch,
            scale: scale,
          ),
        );
        await tester.pumpAndSettle();
        for (final searchable in [false, true]) {
          final select = find.byKey(ValueKey('select-$searchable'));
          final height = stewardControlHeight(tester.element(select));
          _expectInputFits(tester, select, height);
          await tester.tap(select);
          await tester.pumpAndSettle();
          if (searchable) {
            final search = find.byWidgetPredicate(
              (widget) =>
                  widget is material.TextField &&
                  widget.decoration?.hintText == '搜索候选',
            );
            expect(search, findsOneWidget);
            _expectInputFits(tester, search, height);
            await tester.enterText(search, '豆');
            await tester.pumpAndSettle();
          }
          final target = find.byWidgetPredicate(
            (widget) => widget is FSelectItem<int> && widget.value == 2,
          );
          expect(target, findsOneWidget);
          await tester.tap(target);
          await tester.pumpAndSettle();
          expect(target, findsNothing, reason: '真实点击应完成选择并关闭弹层');
        }
        expect(accepted, [2, 2]);
        expect(tester.takeException(), isNull);
      });
    }

    testWidgets('标签和说明不压缩输入框，长按钮允许自然增高：${touch ? '触屏' : '桌面'}', (tester) async {
      _viewport(tester);
      await tester.pumpWidget(
        _host(
          Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const SizedBox(
                width: 220,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    StewardField(
                      '中文连接地址',
                      key: ValueKey('labelled-field'),
                      initial: '中文Ag012',
                    ),
                    SizedBox(height: 4),
                    Text('辅助说明单独占行，不属于输入框内部。'),
                  ],
                ),
              ),
              const SizedBox(height: 16),
              SizedBox(
                width: 118,
                child: StewardButton(
                  '确认设置所有已选资源',
                  key: const ValueKey('long-button'),
                  onPressed: () {},
                ),
              ),
            ],
          ),
          touch: touch,
          scale: 1.5,
        ),
      );
      await tester.pumpAndSettle();
      final field = find.byKey(const ValueKey('labelled-field'));
      final height = stewardControlHeight(tester.element(field));
      _expectInputFits(tester, field, height);
      final frame = _inputPaint(tester, field);
      expect(tester.getRect(field).height, greaterThan(frame.height));
      expect(
        tester.getRect(find.text('中文连接地址')).bottom,
        lessThanOrEqualTo(frame.top),
      );
      expect(
        tester.getRect(find.text('辅助说明单独占行，不属于输入框内部。')).top,
        greaterThanOrEqualTo(frame.bottom),
      );
      final button = find.byKey(const ValueKey('long-button'));
      final painted = tester.getRect(_buttonPaint(button));
      final text = tester.getRect(find.text('确认设置所有已选资源'));
      expect(painted.height, greaterThan(height));
      expect(text.top, greaterThanOrEqualTo(painted.top));
      expect(text.bottom, lessThanOrEqualTo(painted.bottom));
      expect(tester.takeException(), isNull);
    });
  }
}
