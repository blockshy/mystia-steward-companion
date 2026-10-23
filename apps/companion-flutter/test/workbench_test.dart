import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart' show FSwitch;
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/features/settings.dart';

import 'support/fixture_model.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  setUpAll(() async {
    // 字体只在本机视觉证据中载入，不随产品分发；CI 未提供时仍执行全部布局断言。
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null) {
      final bytes = await File(path).readAsBytes();
      for (final name in ['Microsoft YaHei']) {
        await (FontLoader(
          name,
        )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
      }
    }
    await (FontLoader('packages/forui_lucide/ForuiLucideIcons')
          ..addFont(rootBundle.load('packages/forui_lucide/assets/lucide.ttf')))
        .load();
  });
  Future<void> launch(
    WidgetTester tester,
    FixtureModel model,
    Size size, {
    GlobalKey? screenshot,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = size;
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: RepaintBoundary(key: screenshot, child: const StewardApp()),
      ),
    );
    await tester.pumpAndSettle();
  }

  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('全部工作区在 $width px 下无布局溢出，支持大字体', (tester) async {
      final model = FixtureModel(desktop: width > 640);
      model.localPreferences = {
        'fontScalePercent': width == 390 ? 150 : 100,
        'reduceMotion': true,
      };
      addTearDown(model.dispose);
      await launch(tester, model, Size(width, 900));
      expect(tester.takeException(), isNull);
      for (final page in ['总览', '推荐', '扩展', '设置', '经营']) {
        await tester.tap(find.text(page).last);
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull, reason: '$page / $width');
      }
      await tester.pumpWidget(const SizedBox());
    });
  }
  testWidgets('刷新保持长列表滚动与选中详情，列表按需构建', (tester) async {
    final model = FixtureModel(orderCount: 10000);
    addTearDown(model.dispose);
    await launch(tester, model, const Size(1280, 800));
    expect(find.textContaining('桌 ·').evaluate().length, lessThan(30));
    final list = find
        .descendant(
          of: find.byType(DenseListDetail).first,
          matching: find.byType(ListView),
        )
        .first;
    await tester.drag(list, const Offset(0, -460));
    await tester.pumpAndSettle();
    final before = tester
        .state<ScrollableState>(
          find.descendant(of: list, matching: find.byType(Scrollable)).first,
        )
        .position
        .pixels;
    model.publish();
    await tester.pump();
    final after = tester
        .state<ScrollableState>(
          find.descendant(of: list, matching: find.byType(Scrollable)).first,
        )
        .position
        .pixels;
    expect(after, before);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets('中文组合输入不会被后台刷新覆盖', (tester) async {
    final model = FixtureModel();
    addTearDown(model.dispose);
    await launch(tester, model, const Size(1280, 800));
    final field = find.byType(EditableText).first;
    await tester.tap(field);
    await tester.pump();
    tester.testTextInput.updateEditingValue(
      const TextEditingValue(
        text: '灵梦',
        selection: TextSelection.collapsed(offset: 2),
        composing: TextRange(start: 0, end: 2),
      ),
    );
    await tester.pump();
    model.publish();
    await tester.pump();
    final editing = tester.widget<EditableText>(field).controller.value;
    expect(editing.text, '灵梦');
    expect(editing.composing, const TextRange(start: 0, end: 2));
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets('查看设备不能修改共享自动化配置或库存', (tester) async {
    final model = FixtureModel();
    model.localPreferences = {'inventoryEditorModuleEnabled': true};
    model.authority = {'currentDeviceIsPrimary': false};
    addTearDown(model.dispose);
    await launch(tester, model, const Size(1280, 800));
    await tester.tap(find.text('扩展').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('自动化').last);
    await tester.pumpAndSettle();
    // 总开关已改为统一短标题，权限验证直接定位实际开关而非旧文案。
    // 既检查禁用事实，也保留真实点击后共享配置与库存都不得写入的断言。
    final automationSwitch = find.descendant(
      of: find.byKey(const ValueKey('automationEnabled')),
      matching: find.byType(FSwitch),
    );
    expect(tester.widget<FSwitch>(automationSwitch).enabled, isFalse);
    expect(tester.widget<FSwitch>(automationSwitch).onChange, isNull);
    await tester.tap(automationSwitch);
    await tester.pump();
    expect(model.profile['automationEnabled'], isFalse);
    await tester.tap(find.text('扩展').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('库存修改'));
    await tester.pumpAndSettle();
    await tester.tap(find.textContaining('蘑菇').last);
    await tester.pump();
    await tester.tap(find.text('设置已选 1 项'));
    await tester.pump();
    expect(model.writes, isEmpty);
    await tester.pump(const Duration(milliseconds: 500));
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets('库存写入必须确认，取消不发送命令，确认只发送一次', (tester) async {
    final model = FixtureModel();
    model.localPreferences = {'inventoryEditorModuleEnabled': true};
    addTearDown(model.dispose);
    await launch(tester, model, const Size(1280, 800));
    await tester.tap(find.text('扩展').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('库存修改'));
    await tester.pumpAndSettle();
    await tester.tap(find.textContaining('蘑菇').last);
    await tester.pump();
    await tester.tap(find.text('设置已选 1 项'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('取消'));
    await tester.pumpAndSettle();
    expect(model.writes, isEmpty);
    await tester.tap(find.text('设置已选 1 项'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('确认'));
    await tester.pumpAndSettle();
    expect(model.writes.length, 1);
    expect(model.writes.single.$1, '/inventory/bulk-set');
    expect(model.writes.single.$2, {
      'type': 'ingredient',
      'ids': '2',
      'qty': '99',
    });
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets('断线状态明确且不会自动跳走当前页面', (tester) async {
    final model = FixtureModel();
    addTearDown(model.dispose);
    await launch(tester, model, const Size(1280, 800));
    await tester.tap(find.text('推荐').last);
    await tester.pumpAndSettle();
    model.connected = false;
    model.error = '连接超时，请检查游戏电脑。';
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('连接超时，请检查游戏电脑。'), findsOneWidget);
    expect(find.text('查询推荐'), findsNothing);
    expect(find.text('普客'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets('确认库存写入期间换场景会撤销旧确认', (tester) async {
    final model = FixtureModel();
    model.localPreferences = {'inventoryEditorModuleEnabled': true};
    addTearDown(model.dispose);
    await launch(tester, model, const Size(1280, 800));
    await tester.tap(find.text('扩展').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('库存修改'));
    await tester.pumpAndSettle();
    await tester.tap(find.textContaining('蘑菇').last);
    await tester.pump();
    await tester.tap(find.text('设置已选 1 项'));
    await tester.pumpAndSettle();
    model.snapshot = {...model.snapshot, 'runtimeDaySceneGeneration': 2};
    model.publish();
    await tester.pump();
    await tester.tap(find.text('确认'));
    await tester.pumpAndSettle();
    expect(model.writes, isEmpty);
    expect(find.text('状态已变化'), findsOneWidget);
    await tester.tap(find.text('知道了'));
    await tester.pumpAndSettle();
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets('隐藏扩展页不读取资源，切回和断线恢复只恢复当前子页', (tester) async {
    final model = FixtureModel();
    model.localPreferences = {
      'missionListModuleEnabled': true,
      'rareGuestInvitationModuleEnabled': true,
    };
    addTearDown(model.dispose);
    await launch(tester, model, const Size(1280, 800));
    expect(model.reads, isEmpty);
    await tester.tap(find.text('扩展').last);
    await tester.pumpAndSettle();
    // 首个 Tab 就是任务；显式启用的本机模块进入时才开始读取。
    expect(model.reads, ['/missions/tracked']);
    await tester.tap(find.text('稀客邀请'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('经营').last);
    await tester.pumpAndSettle();
    final count = model.reads.length;
    model.publish();
    await tester.pumpAndSettle();
    expect(model.reads.length, count);
    await tester.tap(find.text('扩展').last);
    await tester.pumpAndSettle();
    expect(model.reads.last, '/rare-guests/invitations');
    expect(model.reads.length, count + 1);
    model.connected = false;
    model.publish();
    await tester.pumpAndSettle();
    model.connected = true;
    model.publish();
    await tester.pumpAndSettle();
    expect(model.reads.length, count + 2);
    expect(model.reads.last, '/rare-guests/invitations');
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets('普通暂停可以重试，人工安全屏障不能通过重试绕过', (tester) async {
    final model = FixtureModel();
    addTearDown(model.dispose);
    model.business = {
      ...model.business,
      'automation': {
        'rareDiagnostics': [
          {
            'orderKey': 'rare-paused',
            'title': '暂停订单',
            'paused': true,
            'manualResolutionRequired': false,
          },
        ],
      },
    };
    await launch(tester, model, const Size(1280, 800));
    await tester.tap(find.text('自动化状态'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('重试暂停订单'));
    await tester.pumpAndSettle();
    expect(model.writes.single.$1, 'retry');
    model.writes.clear();
    model.business = {
      ...model.business,
      'automation': {
        'rareDiagnostics': [
          {
            'orderKey': 'rare-paused',
            'title': '暂停订单',
            'paused': true,
            'manualResolutionRequired': true,
          },
        ],
      },
    };
    model.publish();
    await tester.pumpAndSettle();
    await tester.tap(find.text('重试暂停订单'));
    await tester.pump(const Duration(milliseconds: 500));
    expect(model.writes, isEmpty);
    await tester.pumpWidget(const SizedBox());
  });
  for (final width in [1280.0, 390.0]) {
    testWidgets('设置所有分组和扩展子页在 $width px / 深色 / 150% 可布局', (tester) async {
      final model = FixtureModel(desktop: width >= 640);
      model.localPreferences = {
        'fontScalePercent': 150,
        'darkMode': true,
        'reduceMotion': true,
        'missionListModuleEnabled': true,
        'rareGuestInvitationModuleEnabled': true,
        'inventoryEditorModuleEnabled': true,
      };
      addTearDown(model.dispose);
      await launch(tester, model, Size(width, 920));
      await tester.tap(find.text('设置').last);
      await tester.pumpAndSettle();
      // 推荐偏好已迁入推荐工作区，设置只遍历其当前五个分组。
      for (final tab in ['外观', '更新', '诊断', '帮助', '连接与设备']) {
        final target = find
            .descendant(of: find.byType(SettingsPage), matching: find.text(tab))
            .first;
        await tester.ensureVisible(target);
        await tester.tap(target);
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull, reason: '$tab at $width');
      }
      await tester.tap(find.text('扩展').last);
      await tester.pumpAndSettle();
      for (final tab in ['稀客邀请', '库存修改', '任务', '自动化', '游戏界面辅助']) {
        final target = find.text(tab).last;
        await tester.ensureVisible(target);
        await tester.tap(target);
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull, reason: '$tab at $width');
      }
      await tester.pumpWidget(const SizedBox());
    });
  }
  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('生成 $width px 真实组件视觉证据', (tester) async {
      final directory = Platform.environment['MYSTIA_UI_EVIDENCE'];
      if (directory == null) return;
      final model = FixtureModel(desktop: width >= 640);
      addTearDown(model.dispose);
      final key = GlobalKey();
      await launch(tester, model, Size(width, 860), screenshot: key);
      await tester.runAsync(() async {
        final image =
            await (key.currentContext!.findRenderObject()!
                    as RenderRepaintBoundary)
                .toImage();
        final bytes = (await image.toByteData(format: ui.ImageByteFormat.png))!;
        await Directory(directory).create(recursive: true);
        await File('$directory/service-${width.toInt()}.png')
            .writeAsBytes(bytes.buffer.asUint8List());
        image.dispose();
      });
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }
}
