import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/setting_control_row.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

/// 只验证布局和合成交互，不连接游戏、不保存用户设置。字体可使用验证入口指定的
/// 本机中文字体，防止测试默认字形掩盖150%字号下的真实宽度或行高问题。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null) {
      final bytes = await File(path).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });

  Widget host(Widget child, {bool touch = false, double scale = 1}) {
    final theme = stewardTheme(dark: touch, touch: touch);
    return material.MaterialApp(
      localizationsDelegates: FLocalizations.localizationsDelegates,
      supportedLocales: FLocalizations.supportedLocales,
      theme: theme.toApproximateMaterialTheme(),
      builder: (context, routedChild) => FTheme(
        data: theme,
        child: MediaQuery(
          data: MediaQuery.of(context).copyWith(
            textScaler: TextScaler.linear(scale),
            disableAnimations: true,
          ),
          child: routedChild!,
        ),
      ),
      home: Padding(
        padding: const EdgeInsets.all(24),
        child: Align(alignment: Alignment.topLeft, child: child),
      ),
    );
  }

  void viewport(WidgetTester tester, double width) {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
  }

  for (final touch in [false, true]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('同一平台控件跨640px保持尺寸 $touch / $scale', (tester) async {
        viewport(tester, 639);
        Size? inputSize, buttonSize;
        for (final width in [639.0, 640.0, 641.0, 390.0]) {
          tester.view.physicalSize = Size(width, 920);
          await tester.pumpWidget(
            host(
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const SizedBox(
                    width: 120,
                    child: StewardField('名称', showLabel: false, hint: '输入名称'),
                  ),
                  const SizedBox(width: 8),
                  StewardButton('保存', onPressed: () {}),
                ],
              ),
              touch: touch,
              scale: scale,
            ),
          );
          await tester.pumpAndSettle();
          final field = find.byType(material.TextField);
          final button = find.byType(FButton);
          final fieldRect = tester.getRect(field);
          final buttonRect = tester.getRect(button);
          // 检查真实输入边框和按钮，而非包含标签的外壳；同平台缩放不切换密度。
          inputSize ??= fieldRect.size;
          buttonSize ??= buttonRect.size;
          expect(fieldRect.size, inputSize);
          expect(buttonRect.size, buttonSize);
          expect(fieldRect.height, closeTo(buttonRect.height, 1));
          expect(fieldRect.center.dy, closeTo(buttonRect.center.dy, 1));
          final editable = tester.getRect(find.byType(EditableText));
          expect(editable.top, greaterThanOrEqualTo(fieldRect.top - 1));
          expect(editable.bottom, lessThanOrEqualTo(fieldRect.bottom + 1));
          expect(buttonRect.right, lessThanOrEqualTo(width - 24));
          expect(tester.takeException(), isNull);
        }
      });
    }
  }

  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('选择列收窄且与滑块列右边缘一致 $width / 150%', (tester) async {
      viewport(tester, width);
      await tester.pumpWidget(
        host(
          Padding(
            // 模拟80px固定导航后的真实剩余空间，保留页面24px边距。
            padding: const EdgeInsets.only(left: 80),
            child: Column(
              children: [
                const SettingControlRow(
                  title: '滑块参数',
                  control: SizedBox(height: 44),
                ),
                SettingControlRow(
                  title: '评分方向',
                  maxControlWidth: stewardSelectWidth,
                  control: StewardSelect<String>(
                    label: '评分方向',
                    showLabel: false,
                    items: const {'desc': '越高越优先', 'asc': '越低越优先'},
                    value: 'desc',
                    onChanged: (_) {},
                  ),
                ),
              ],
            ),
          ),
          touch: true,
          scale: 1.5,
        ),
      );
      await tester.pumpAndSettle();
      final areas = find.byKey(const ValueKey('setting-control-area'));
      final slider = tester.getRect(areas.first);
      final select = tester.getRect(areas.last);
      expect(select.width, lessThanOrEqualTo(stewardSelectWidth));
      expect(select.width, lessThanOrEqualTo(slider.width));
      expect(select.right, closeTo(slider.right, .01));
      expect(select.right, closeTo(width - 24, .01));
      final label = tester.getRect(
        find.byKey(const ValueKey('setting-control-label')).last,
      );
      expect(label.right, lessThan(select.left));
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('390px真实262px正文内预算和颜色150%可打开且边界完整', (tester) async {
    viewport(tester, 390);
    final budgetCalls = <String>[];
    final colorCalls = <String>[];
    await tester.pumpWidget(
      host(
        Padding(
          padding: const EdgeInsets.only(left: 80),
          child: SingleChildScrollView(
            child: Section('合成设置', [
              SettingControlRow(
                title: '预算不足时',
                maxControlWidth: stewardSelectWidth,
                control: StewardSelect<String>(
                  key: const ValueKey('compact-budget-field'),
                  label: '预算不足时',
                  showLabel: false,
                  items: const {'block': '不足时阻止', 'warn': '仅提示'},
                  value: 'block',
                  onChanged: budgetCalls.add,
                ),
              ),
              ColorSetting(
                title: '稀客高亮颜色',
                value: '#5FACD3',
                onSave: (value) async => colorCalls.add(value),
              ),
            ]),
          ),
        ),
        touch: true,
        scale: 1.5,
      ),
    );
    await tester.pumpAndSettle();
    final section = tester.getRect(find.byType(Section));
    expect(section.left, 104);
    expect(section.width, 262);
    final budget = find.byKey(const ValueKey('compact-budget-field'));
    final color = find.byKey(const ValueKey('color-picker-open'));
    // 加上真实Section内边距，验证不能依赖旧12px页边距多出的24px空间。
    for (final control in [budget, color]) {
      final bounds = tester.getRect(control);
      expect(bounds.left, greaterThanOrEqualTo(section.left));
      expect(bounds.right, lessThanOrEqualTo(section.right));
    }
    await tester.tap(budget);
    await tester.pumpAndSettle();
    await tester.tap(
      find.byWidgetPredicate(
        (widget) => widget is FSelectItem<String> && widget.value == 'warn',
      ),
    );
    await tester.pumpAndSettle();
    expect(budgetCalls, ['warn']);
    await tester.tap(color);
    await tester.pumpAndSettle();
    final plane = tester.getRect(find.byKey(const ValueKey('color-picker-sv')));
    expect(plane.left, greaterThanOrEqualTo(0));
    expect(plane.right, lessThanOrEqualTo(390));
    await tester.tap(find.byKey(const ValueKey('color-picker-cancel')));
    await tester.pumpAndSettle();
    expect(colorCalls, isEmpty);
    expect(tester.takeException(), isNull);
  });

  for (final searchable in [false, true]) {
    for (final anchorWidth in [stewardSelectWidth, 320.0]) {
      testWidgets('下拉弹层限制宽度且同值关闭 $searchable / $anchorWidth', (tester) async {
        viewport(tester, 390);
        final calls = <String>[];
        await tester.pumpWidget(
          host(
            SizedBox(
              width: anchorWidth,
              child: StewardSelect<String>(
                label: '合成选择',
                showLabel: false,
                searchable: searchable,
                items: const {'a': '当前项', 'b': '较长的合成选项名称应保持可阅读'},
                value: 'a',
                onChanged: calls.add,
              ),
            ),
            touch: true,
            scale: 1.5,
          ),
        );
        await tester.tap(find.byType(StewardSelect<String>));
        await tester.pumpAndSettle();
        final items = find.byWidgetPredicate(
          (widget) => widget is FSelectItem<String>,
        );
        expect(items, findsNWidgets(2));
        for (final item in items.evaluate()) {
          final rectangle = tester.getRect(find.byWidget(item.widget));
          expect(rectangle.width, lessThanOrEqualTo(stewardSelectWidth));
          expect(rectangle.left, greaterThanOrEqualTo(0));
          expect(rectangle.right, lessThanOrEqualTo(390));
        }
        // 使用真实项目激活而非直接调用回调，保护原来的同值关闭行为。
        await tester.tap(
          find.byWidgetPredicate(
            (widget) => widget is FSelectItem<String> && widget.value == 'a',
          ),
        );
        await tester.pumpAndSettle();
        expect(items, findsNothing);
        expect(calls, isEmpty);
        expect(tester.takeException(), isNull);
      });
    }
  }
}
