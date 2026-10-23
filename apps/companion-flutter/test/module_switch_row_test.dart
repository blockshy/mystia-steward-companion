import 'dart:io';

import 'package:flutter/services.dart';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/extension_modules.dart';
import 'package:mystia_steward_companion/features/tools.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/module_switch_row.dart';
import 'package:mystia_steward_companion/ui/page_toolbar.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/text_geometry.dart';

/// 使用真实主题和扩展页验证模块布局；合成模型仅记录配置与读取，不连接游戏。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    // 与其他布局专项一致，受控环境提供字体时使用真实中文字形，避免测试方框
    // 低估标签或状态说明宽度；字体仅用于测试，不加入产品包或修改系统配置。
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null) {
      final bytes = await File(path).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });

  Future<void> open(
    WidgetTester tester,
    Widget child, {
    required double width,
    bool dark = false,
    bool touch = false,
    double scale = 1.5,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final theme = stewardTheme(dark: dark, touch: touch);
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
        home: child,
      ),
    );
    await tester.pumpAndSettle();
  }

  for (final dark in [false, true]) {
    for (final width in [262.0, 342.0, 1184.0]) {
      testWidgets('五模块在 $width px/150% dark=$dark 总开关同起点同宽且无重复标题', (
        tester,
      ) async {
        final model = FixtureModel(desktop: width != 342);
        addTearDown(model.dispose);
        model.localPreferences = {
          for (final module in extensionModules) module.preferenceKey: true,
        };
        await open(
          tester,
          AnimatedBuilder(
            animation: model,
            builder: (context, _) => ToolsPage(model),
          ),
          width: width,
          dark: dark,
          touch: !model.desktop,
        );
        Rect? first;
        for (final title in ['任务', '稀客邀请', '库存修改', '自动化', '游戏界面辅助']) {
          final mainTabs = find.byType(WorkspaceTabs<String>).first;
          final tab = find.descendant(of: mainTabs, matching: find.text(title));
          await tester.ensureVisible(tab);
          await tester.pumpAndSettle();
          await tester.tap(tab);
          await tester.pumpAndSettle();
          final row = find.byType(ModuleSwitchRow);
          expect(row, findsOneWidget);
          final line = find.descendant(
            of: row,
            matching: find.byKey(const ValueKey('module-switch-line')),
          );
          final geometry = tester.getRect(line);
          first ??= geometry;
          expect(geometry.left, closeTo(first.left, .01));
          expect(geometry.top, closeTo(first.top, .01));
          expect(geometry.width, closeTo(first.width, .01));
          final prefix = tester.getRect(
            find.descendant(of: row, matching: find.text('启用')),
          );
          final control = find.descendant(
            of: row,
            matching: find.byType(FSwitch),
          );
          final switchRect = tester.getRect(control);
          final status = tester.getRect(
            find.descendant(
              of: row,
              matching: find.byKey(const ValueKey('module-switch-status')),
            ),
          );
          final prefixText = find.descendant(
            of: row,
            matching: find.text('启用'),
          );
          final statusText = find.descendant(
            of: row,
            matching: find.byKey(const ValueKey('module-switch-status')),
          );
          // 两种字号以文字首基线相齐；开关是非文本控件，中心对准正文首行。
          // 不再要求 13px/12px 的整块文字盒中心相等，这正是旧视觉错位的来源。
          expect(
            textBaseline(tester, prefixText),
            closeTo(textBaseline(tester, statusText), .01),
          );
          expect(
            textFirstLine(tester, prefixText).center,
            closeTo(switchRect.center.dy, .01),
          );
          expect(prefix.right, lessThan(status.left));
          expect(status.right, lessThan(switchRect.left));
          expect(switchRect.right, lessThanOrEqualTo(geometry.right));
          expect(
            tester.widget<FSwitch>(control).semanticsLabel,
            contains('模块'),
          );
          expect(find.text('自动化总开关'), findsNothing);
          expect(find.text('游戏界面辅助总开关'), findsNothing);
          expect(tester.takeException(), isNull);
        }
        expect(model.writes, isEmpty);
        await tester.pumpWidget(const SizedBox());
      });
    }
  }

  testWidgets('工具搜索同排有界，横滚页签可点且缩放保留输入法与焦点', (tester) async {
    final model = FixtureModel()
      ..localPreferences = {'rareGuestInvitationModuleEnabled': true};
    addTearDown(model.dispose);
    await open(
      tester,
      AnimatedBuilder(
        animation: model,
        builder: (context, _) => ToolsPage(model),
      ),
      width: 640,
    );
    final invitation = find.descendant(
      of: find.byType(WorkspaceTabs<String>).first,
      matching: find.text('稀客邀请'),
    );
    await tester.tap(invitation);
    await tester.pumpAndSettle();
    final toolbar = find.byType(PageToolbar);
    final input = find.descendant(
      of: toolbar,
      matching: find.byType(EditableText),
    );
    await tester.enterText(input, '博丽');
    final editable = tester.widget<EditableText>(input);
    final original = editable.controller;
    final focus = editable.focusNode;
    const draft = TextEditingValue(
      text: '博丽',
      selection: TextSelection.collapsed(offset: 2),
      composing: TextRange(start: 0, end: 2),
    );
    tester.testTextInput.updateEditingValue(draft);
    await tester.pump();
    for (final width in [262.0, 342.0, 640.0]) {
      tester.view.physicalSize = Size(width, 920);
      await tester.pumpAndSettle();
      final field = find.descendant(
        of: toolbar,
        matching: find.byType(StewardField),
      );
      final navigation = find.descendant(
        of: toolbar,
        matching: find.byType(WorkspaceTabs<String>),
      );
      final refresh = find.byKey(const ValueKey('extension-refresh'));
      final searchRect = tester.getRect(field);
      final tabRect = tester.getRect(navigation);
      final refreshRect = tester.getRect(refresh);
      expect(tabRect.width, greaterThanOrEqualTo(70));
      expect(searchRect.width, lessThanOrEqualTo(152));
      expect(searchRect.width, greaterThanOrEqualTo(104));
      expect(searchRect.center.dy, closeTo(refreshRect.center.dy, .01));
      expect(tabRect.right, lessThan(searchRect.left));
      expect(searchRect.right, lessThan(refreshRect.left));
      expect(tester.widget<EditableText>(input).controller, same(original));
      expect(tester.widget<EditableText>(input).focusNode, same(focus));
      expect(focus.hasFocus, isTrue);
      expect(original.value, draft);
      expect(tester.takeException(), isNull);
    }
    tester.view.physicalSize = const Size(262, 920);
    await tester.pumpAndSettle();
    final allMaps = find.descendant(of: toolbar, matching: find.text('全部地图'));
    await tester.ensureVisible(allMaps);
    await tester.pumpAndSettle();
    expect(allMaps.hitTestable(), findsOneWidget);
    await tester.tap(allMaps);
    await tester.pumpAndSettle();
    final navigation = tester.widget<WorkspaceTabs<String>>(
      find.descendant(
        of: toolbar,
        matching: find.byType(WorkspaceTabs<String>),
      ),
    );
    expect(navigation.value, 'all');
    expect(original.text, '博丽');
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
}
