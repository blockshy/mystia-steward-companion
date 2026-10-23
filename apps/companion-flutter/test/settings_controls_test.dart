import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/page_activity_assertions.dart';

/// 仅验证用户选择、输入合并与身份边界；保存回调为内存对象，不接触平台存储或游戏。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;

  Widget host(Widget child, {double scale = 1}) {
    final theme = stewardTheme(dark: false, touch: true);
    return material.MaterialApp(
      localizationsDelegates: FLocalizations.localizationsDelegates,
      supportedLocales: FLocalizations.supportedLocales,
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
        // 模拟 80px 导航及左右各 24px 内容边距，窄屏按实际剩余空间验证。
        padding: const EdgeInsets.only(left: 104, right: 24, top: 16),
        child: SingleChildScrollView(child: child),
      ),
    );
  }

  Future<void> focusSlider(WidgetTester tester, Finder slider) async {
    await tester.ensureVisible(slider);
    final outline = find
        .descendant(of: slider, matching: find.byType(FFocusedOutline))
        .first;
    Focus.of(tester.element(outline)).requestFocus();
    await tester.pump();
  }

  testWidgets('键盘一步为1，连续按键合并后自动提交且无数值文本输入或保存按钮', (tester) async {
    var value = 30;
    final calls = <int>[];
    late StateSetter refresh;
    await tester.pumpWidget(
      host(
        StatefulBuilder(
          builder: (_, setState) {
            refresh = setState;
            return IntegerSetting(
              title: '日志数量',
              value: value,
              min: 1,
              max: 9999,
              unit: ' 个',
              onSave: (next) async {
                calls.add(next);
                refresh(() => value = next);
              },
            );
          },
        ),
      ),
    );
    await focusSlider(tester, find.byType(FSlider));
    for (var index = 0; index < 3; index++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.pump();
    }
    expect(calls, isEmpty);
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('integer-setting-value')))
          .data,
      '33 个',
    );
    await tester.pump(const Duration(milliseconds: 181));
    await tester.pumpAndSettle();
    expect(calls, [33]);
    expect(find.byType(EditableText), findsNothing);
    expect(find.text('保存'), findsNothing);
    // 辅助文案消失不能牺牲键盘功能：本用例仍通过真实按键验证步进、合并及边界。
    expect(find.textContaining('方向键精调'), findsNothing);
    expect(find.textContaining('步进'), findsNothing);
    expect(find.textContaining('1–9999'), findsNothing);
    expect(
      tester
          .widget<FSlider>(find.byType(FSlider))
          .semanticValueFormatterCallback(1),
      '日志数量 9999 个',
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.end);
    await tester.pump(const Duration(milliseconds: 181));
    await tester.pumpAndSettle();
    expect(calls.last, 9999);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump(const Duration(milliseconds: 181));
    expect(calls, [33, 9999]);
  });

  testWidgets('共享拖动中只预览，抬起后自动提交最后值', (tester) async {
    var value = 50;
    final calls = <int>[];
    await tester.pumpWidget(
      host(
        StatefulBuilder(
          builder: (_, refresh) => IntegerSetting(
            title: '权重',
            value: value,
            min: 0,
            max: 100,
            onSave: (next) async {
              calls.add(next);
              refresh(() => value = next);
            },
          ),
        ),
      ),
    );
    final rect = tester.getRect(find.byType(FSlider));
    final thumb = find
        .descendant(
          of: find.byType(FSlider),
          matching: find.byType(FFocusedOutline),
        )
        .first;
    final gesture = await tester.startGesture(tester.getCenter(thumb));
    await gesture.moveBy(const Offset(20, 0));
    await tester.pump();
    await gesture.moveTo(Offset(rect.right - 20, rect.center.dy));
    await tester.pump(const Duration(milliseconds: 400));
    expect(calls, isEmpty);
    await gesture.up();
    await tester.pumpAndSettle();
    expect(calls, hasLength(1));
    expect(calls.single, greaterThan(80));
  });

  testWidgets('本机连续滑动节流，前一次写入期间的新末值不丢失', (tester) async {
    var value = 50;
    final calls = <int>[];
    final first = Completer<void>();
    await tester.pumpWidget(
      host(
        StatefulBuilder(
          builder: (_, refresh) => IntegerSetting(
            title: '不透明度',
            value: value,
            min: 20,
            max: 100,
            continuous: true,
            onSave: (next) async {
              calls.add(next);
              if (calls.length == 1) await first.future;
              refresh(() => value = next);
            },
          ),
        ),
      ),
    );
    final rect = tester.getRect(find.byType(FSlider));
    final thumb = find
        .descendant(
          of: find.byType(FSlider),
          matching: find.byType(FFocusedOutline),
        )
        .first;
    final gesture = await tester.startGesture(tester.getCenter(thumb));
    await gesture.moveBy(const Offset(20, 0));
    await tester.pump();
    await gesture.moveTo(Offset(rect.right - 50, rect.center.dy));
    await tester.pump(const Duration(milliseconds: 181));
    expect(calls, hasLength(1));
    await gesture.moveTo(Offset(rect.right - 12, rect.center.dy));
    await gesture.up();
    first.complete();
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 181));
    await tester.pumpAndSettle();
    expect(calls, hasLength(2));
    expect(calls.last, greaterThan(calls.first));
    expect(value, calls.last);
  });

  testWidgets('键盘防抖期间换连接取消旧选择，不把草稿提交给新身份', (tester) async {
    var boundary = 1;
    final calls = <int>[];
    late StateSetter refresh;
    await tester.pumpWidget(
      host(
        StatefulBuilder(
          builder: (_, setState) {
            refresh = setState;
            return IntegerSetting(
              title: '并发',
              value: 2,
              min: 1,
              max: 6,
              resetKey: boundary,
              onSave: (next) async => calls.add(next),
            );
          },
        ),
      ),
    );
    await focusSlider(tester, find.byType(FSlider));
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    refresh(() => boundary = 2);
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 200));
    expect(calls, isEmpty);
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('integer-setting-value')))
          .data,
      '2',
    );
  });

  testWidgets('保存失败恢复权威整数并展示错误，未禁用后偷偷重试', (tester) async {
    await tester.pumpWidget(
      host(
        IntegerSetting(
          title: '权重',
          value: 10,
          min: 0,
          max: 100,
          onSave: (_) async => throw StateError('合成保存失败'),
        ),
      ),
    );
    await focusSlider(tester, find.byType(FSlider));
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('integer-setting-value')))
          .data,
      '10',
    );
    expectReportedFailure(tester, '合成保存失败');
    // 页头状态不改变控件布局；卸载前仍正常关闭键盘焦点提示层并推进排版任务。
    FocusManager.instance.primaryFocus?.unfocus();
    await tester.pumpAndSettle();
    await tester.pumpWidget(const SizedBox());
    await tester.pump(const Duration(milliseconds: 20));
  });

  testWidgets('颜色确认一次保存完整值，保存延迟时禁用色盘与选择器', (tester) async {
    var color = '#102030';
    final calls = <String>[];
    final saving = Completer<void>();
    await tester.pumpWidget(
      host(
        StatefulBuilder(
          builder: (_, refresh) => ColorSetting(
            title: '高亮',
            value: color,
            onSave: (next) async {
              calls.add(next);
              await saving.future;
              refresh(() => color = next);
            },
          ),
        ),
      ),
    );
    await tester.tap(find.byKey(const ValueKey('color-picker-open')));
    await tester.pumpAndSettle();
    final plane = find.byKey(const ValueKey('color-picker-sv'));
    expect(find.textContaining('色板：左右调'), findsNothing);
    // 读屏指导仍在语义节点中；可见教程的移除不能同时清掉非视觉操作帮助。
    expect(
      tester
          .widgetList<Semantics>(find.byType(Semantics))
          .any(
            (node) =>
                node.properties.label == '饱和度与明度' &&
                node.properties.hint == '左右方向键调饱和度，上下方向键调明度，每次百分之一',
          ),
      isTrue,
    );
    await tester.tap(plane);
    await tester.pump();
    final selected = tester
        .widget<Text>(find.byKey(const ValueKey('color-picker-value')))
        .data!;
    expect(calls, isEmpty);
    await tester.tap(find.byKey(const ValueKey('color-picker-confirm')));
    await tester.pumpAndSettle();
    expect(calls, [selected]);
    expect(selected, matches(RegExp(r'^#[0-9A-F]{6}$')));
    for (final button in tester.widgetList<FButton>(find.byType(FButton))) {
      expect(button.onPress, isNull);
    }
    saving.complete();
    await tester.pumpAndSettle();
    expect(calls, [selected]);
    expect(color, selected);
    expect(find.byType(FSlider), findsNothing);
  });

  testWidgets('HSV键盘与色相调整仅修改弹窗草稿，取消后不保存', (tester) async {
    final calls = <String>[];
    await tester.pumpWidget(
      host(
        ColorSetting(
          title: '高亮',
          value: '#102030',
          onSave: (value) async => calls.add(value),
        ),
      ),
    );
    await tester.tap(find.byKey(const ValueKey('color-picker-open')));
    await tester.pumpAndSettle();
    final plane = find.byKey(const ValueKey('color-picker-sv'));
    await tester.tap(plane);
    await tester.pump();
    final before = tester
        .widget<Text>(find.byKey(const ValueKey('color-picker-value')))
        .data;
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
    await tester.pump();
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('color-picker-value')))
          .data,
      isNot(before),
    );
    await focusSlider(tester, find.byKey(const ValueKey('color-picker-hue')));
    await tester.sendKeyEvent(LogicalKeyboardKey.home);
    await tester.pump();
    expect(find.text('色相 0°'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('color-picker-cancel')));
    await tester.pumpAndSettle();
    expect(calls, isEmpty);
    expect(find.text('#102030'), findsOneWidget);
  });

  for (final changed in ['identity', 'identity-roundtrip', 'permission']) {
    testWidgets('HSV确认时$changed变化拒绝旧弹窗写入', (tester) async {
      var boundary = 1;
      var enabled = true;
      final calls = <String>[];
      late StateSetter refresh;
      await tester.pumpWidget(
        host(
          StatefulBuilder(
            builder: (_, setState) {
              refresh = setState;
              return ColorSetting(
                title: '高亮',
                value: '#102030',
                resetKey: boundary,
                onSave: !enabled
                    ? null
                    : (next) async {
                        calls.add(next);
                      },
              );
            },
          ),
        ),
      );
      await tester.tap(find.byKey(const ValueKey('color-picker-open')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('color-picker-sv')));
      refresh(() {
        if (changed == 'permission') {
          enabled = false;
        } else {
          boundary++;
        }
      });
      await tester.pump();
      if (changed == 'identity-roundtrip') {
        refresh(() => boundary = 1);
        await tester.pump();
      }
      await tester.tap(find.byKey(const ValueKey('color-picker-confirm')));
      await tester.pumpAndSettle();
      // 恢复权限也不能把先前被拒绝的确认补发。
      refresh(() => enabled = true);
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 200));
      expect(calls, isEmpty);
      expectReportedFailure(tester, '连接或权限已变化，颜色未保存，请重新选择。');
    });
  }

  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('右侧设置列与HSV色盘，${width.toInt()}px/150%几何及保存可用', (tester) async {
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = Size(width, 920);
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      var color = '#001122';
      final calls = <String>[];
      await tester.pumpWidget(
        host(
          StatefulBuilder(
            builder: (_, refresh) => Column(
              children: [
                SettingToggle('合成开关', true, (_) {}),
                IntegerSetting(
                  title: '每个料理的加料变体数',
                  value: 1,
                  min: 1,
                  max: 8,
                  onSave: (_) async {},
                ),
                ColorSetting(
                  title: '稀客高亮颜色',
                  value: color,
                  onSave: (next) async {
                    calls.add(next);
                    refresh(() => color = next);
                  },
                ),
              ],
            ),
          ),
          scale: 1.5,
        ),
      );
      // 真实父宽度下，颜色按钮与滑块控制列都贴齐现有开关右边缘，标签始终在左。
      final switchRect = tester.getRect(find.byType(FSwitch));
      for (final type in [IntegerSetting, ColorSetting]) {
        final owner = find.byType(type);
        final labelRect = tester.getRect(
          find.descendant(
            of: owner,
            matching: find.byKey(const ValueKey('setting-control-label')),
          ),
        );
        final controlRect = tester.getRect(
          find.descendant(
            of: owner,
            matching: find.byKey(const ValueKey('setting-control-area')),
          ),
        );
        expect(labelRect.right, lessThan(controlRect.left));
        expect(labelRect.center.dy, closeTo(controlRect.center.dy, .1));
        expect(controlRect.width, greaterThanOrEqualTo(144));
        expect(controlRect.right, closeTo(switchRect.right, .1));
      }
      final opener = find.byKey(const ValueKey('color-picker-open'));
      expect(tester.getRect(opener).right, closeTo(switchRect.right, .1));
      await tester.tap(opener);
      await tester.pumpAndSettle();
      final plane = find.byKey(const ValueKey('color-picker-sv'));
      final planeRect = tester.getRect(plane);
      expect(planeRect.left, greaterThanOrEqualTo(0));
      expect(planeRect.right, lessThanOrEqualTo(width));
      await tester.tapAt(
        Offset(
          planeRect.left + planeRect.width * .7,
          planeRect.top + planeRect.height * .3,
        ),
      );
      await tester.pump();
      final selected = tester
          .widget<Text>(find.byKey(const ValueKey('color-picker-value')))
          .data!;
      await tester.ensureVisible(
        find.byKey(const ValueKey('color-picker-confirm')),
      );
      await tester.tap(find.byKey(const ValueKey('color-picker-confirm')));
      await tester.pumpAndSettle();
      expect(calls, [selected]);
      expect(color, selected);
      final palette = find.byKey(const ValueKey('palette-#FFDB2E'));
      await tester.ensureVisible(palette);
      await tester.tap(palette);
      await tester.pumpAndSettle();
      expect(calls.last, '#FFDB2E');
      // 真实按钮几何而非仅无 overflow：宽屏同排多色，窄屏折行，触控区域不缩水。
      final paletteRects = [
        for (final value in [
          '#FFDB2E',
          '#5FACD3',
          '#F97373',
          '#7CCB8A',
          '#BA9CE8',
          '#FFFFFF',
          '#000000',
        ])
          tester.getRect(find.byKey(ValueKey('palette-$value'))),
      ];
      for (final rect in paletteRects) {
        // 色样24px不决定操作框大小；桌面与其他按钮共同采用标准控制高度。
        final controlHeight = stewardControlHeight(tester.element(palette));
        expect(rect.width, closeTo(controlHeight, .1));
        expect(rect.height, closeTo(controlHeight, .1));
        expect(rect.width, lessThan((width - 128) / 2));
        expect(rect.left, greaterThanOrEqualTo(104));
        expect(rect.right, lessThanOrEqualTo(width - 24));
      }
      expect(paletteRects[1].top, closeTo(paletteRects.first.top, .1));
      expect(paletteRects[1].left, greaterThan(paletteRects.first.right));
      if (width == 390)
        expect(paletteRects.last.top, greaterThan(paletteRects.first.top));
      expect(tester.widget<FButton>(palette).variant, FButtonVariant.primary);
      expect(
        tester
            .widget<FButton>(find.byKey(const ValueKey('palette-#5FACD3')))
            .variant,
        FButtonVariant.outline,
      );
      expect(
        find.descendant(of: palette, matching: find.byIcon(FLucideIcons.check)),
        findsOneWidget,
      );
      expect(find.text('#FFDB2E'), findsOneWidget);
      expect(find.byType(EditableText), findsNothing);
      for (final slider in tester.widgetList<FSlider>(find.byType(FSlider))) {
        final rect = tester.getRect(find.byWidget(slider));
        expect(rect.left, greaterThanOrEqualTo(104));
        expect(rect.right, lessThanOrEqualTo(width - 24));
      }
      expect(tester.takeException(), isNull);
    });
  }
}
