import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/tools.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/module_switch_row.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 扩展提示只验证真实 DTO 形状的展示投影，不连接 HTTP 或启动游戏。
/// 页头通道与正文分别断言：成功静默不能顺带抹掉失败原因、刷新入口或写入门禁。
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

  Future<ValueNotifier<bool>> open(
    WidgetTester tester,
    FixtureModel model,
    PageActivityChannel channel, {
    double width = 640,
    double scale = 1,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 1000);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(model.dispose);
    addTearDown(channel.dispose);
    final active = ValueNotifier(true);
    addTearDown(active.dispose);
    final theme = stewardTheme(dark: false, touch: !model.desktop);
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
          padding: const EdgeInsets.all(16),
          child: PageActivityScope(
            channel: channel,
            child: AnimatedBuilder(
              animation: Listenable.merge([model, active]),
              builder: (context, _) => ToolsPage(model, active: active.value),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    return active;
  }

  FixtureModel model() => FixtureModel(orderCount: 0)
    ..localPreferences = {
      'missionListModuleEnabled': true,
      'rareGuestInvitationModuleEnabled': true,
      'inventoryEditorModuleEnabled': true,
    }
    ..resources = {
      '/missions/tracked': {
        'ok': true,
        'runtimeAvailable': true,
        'status': 'ready',
        'missions': [
          {
            'label': 'synthetic-mission',
            'title': '合成任务条目',
            'status': 'tracking',
            'conditionCount': 0,
            'completedConditionCount': 0,
            'conditionStates': <bool>[],
          },
        ],
      },
      '/rare-guests/invitations': {
        'ok': true,
        'runtimeAvailable': true,
        'status': '当前日间场景没有新的可邀请稀客。',
        'candidates': [
          {
            'id': 1100,
            'name': '合成不可邀请稀客',
            'status': 'low-kizuna',
            'kizunaLevel': 1,
            'locationStatus': 'unavailable',
            'reason': '羁绊未满足原生邀请条件，请先完成角色对话。',
            'canInvite': false,
          },
        ],
      },
    };

  Future<void> tab(WidgetTester tester, String title) async {
    final target = find.descendant(
      of: find.byType(WorkspaceTabs<String>).first,
      matching: find.text(title),
    );
    await tester.ensureVisible(target);
    await tester.tap(target);
    await tester.pumpAndSettle();
  }

  testWidgets('成功状态和重复统计静默，候选受限原因与库存说明仍可访问', (tester) async {
    final fixture = model();
    final channel = PageActivityChannel();
    await open(tester, fixture, channel);
    expect(channel.value, isNull);
    expect(find.text('ready'), findsNothing);
    expect(find.text('进行中'), findsOneWidget);
    await tab(tester, '稀客邀请');
    expect(channel.value, isNull);
    expect(find.text('当前日间场景没有新的可邀请稀客。'), findsNothing);
    expect(find.textContaining('筛选 1 位'), findsNothing);
    expect(find.text('羁绊不足'), findsOneWidget);
    expect(find.text('羁绊未满足原生邀请条件，请先完成角色对话。'), findsOneWidget);
    final invite = find.widgetWithText(StewardButton, '邀请此稀客');
    expect(tester.widget<StewardButton>(invite).onPressed, isNull);
    await tab(tester, '库存修改');
    expect(channel.value, isNull);
    expect(find.textContaining('共 1 项 ·'), findsNothing);
    expect(
      tester.widget<ModuleSwitchRow>(find.byType(ModuleSwitchRow)).explanation,
      contains('未知库存不会按零处理'),
    );
    expect(find.byKey(const ValueKey('inventory-items')), findsOneWidget);
    expect(fixture.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('任务非正常状态中文投影到页头，未知不能假装成功且保留刷新', (tester) async {
    final fixture = model();
    final channel = PageActivityChannel();
    await open(tester, fixture, channel);
    for (final sample in [
      ('not-attached', '任务读取未接入', false, true),
      ('waiting-for-load', '等待读取存档', false, false),
      ('loading', '正在读取任务', true, false),
      ('runtime-unavailable', '任务运行时不可用', false, true),
      ('mission-data-incomplete', '任务数据不完整', false, true),
      ('unrecognized-server-state', '任务状态未确认', false, true),
      ('ready', '任务状态未确认', false, true),
    ]) {
      fixture.resources['/missions/tracked'] = {
        'ok': true,
        'runtimeAvailable': false,
        'status': sample.$1,
        'missions': <Object?>[],
      };
      fixture.publish();
      await tester.pumpAndSettle();
      expect(channel.value?.message, sample.$2);
      expect(channel.value?.busy, sample.$3);
      expect(channel.value?.error, sample.$4);
      expect(find.text(sample.$1), findsNothing);
      expect(find.text('暂无任务'), findsNothing, reason: '不可读取不能冒充空数据');
      expect(
        tester
            .widget<StewardIconButton>(
              find.byKey(const ValueKey('extension-refresh')),
            )
            .onPressed,
        isNotNull,
      );
      expect(tester.takeException(), isNull);
    }
  });

  testWidgets('失败详情与场景门禁保留，切模块、隐藏或停用不残留页头状态', (tester) async {
    final fixture = model();
    final channel = PageActivityChannel();
    final active = await open(tester, fixture, channel);
    await tab(tester, '稀客邀请');
    fixture.resources['/rare-guests/invitations'] = {
      'ok': false,
      'runtimeAvailable': false,
      'error': '合成读取失败：邀请目录尚未初始化。',
    };
    fixture.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.message, '读取失败');
    expect(channel.value?.error, isTrue);
    expect(channel.value?.details, '合成读取失败：邀请目录尚未初始化。');
    expect(find.text('合成读取失败：邀请目录尚未初始化。'), findsNothing);

    fixture.resources['/rare-guests/invitations'] = {};
    fixture.snapshot['runtimeDaySceneReady'] = false;
    fixture.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.message, '等待日间场景');
    expect(channel.value?.details, '邀请需要已稳定的日间场景；场景就绪后可刷新候选。');
    expect(find.text('邀请需要已稳定的日间场景；场景就绪后可刷新候选。'), findsNothing);
    expect(
      tester
          .widget<StewardButton>(find.widgetWithText(StewardButton, '邀请筛选范围'))
          .onPressed,
      isNull,
    );
    active.value = false;
    await tester.pumpAndSettle();
    expect(channel.value, isNull);
    active.value = true;
    await tester.pumpAndSettle();
    expect(channel.value?.message, '等待日间场景');
    final readsBeforeSharedModule = fixture.reads.length;
    for (final shared in ['自动化', '游戏界面辅助']) {
      await tab(tester, shared);
      expect(channel.value, isNull, reason: '$shared 不得沿用邀请模块的等待状态');
      expect(
        fixture.reads.length,
        readsBeforeSharedModule,
        reason: '共享模块不应新增任务或邀请读取',
      );
    }
    await tab(tester, '稀客邀请');
    fixture.localPreferences['rareGuestInvitationModuleEnabled'] = false;
    fixture.publish();
    await tester.pumpAndSettle();
    expect(channel.value, isNull);
    expect(fixture.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('可接任务非空等待诊断仍显示中文等待，真实故障保留原文且未知不放行', (tester) async {
    final fixture = model();
    final channel = PageActivityChannel();
    final valid = <String, Object?>{
      'ok': true,
      'runtimeAvailable': false,
      'status': 'waiting-for-load',
      'missionGeneration': 0,
      'sourceRevision': 0,
      'contentSignature': List.filled(64, 'b').join(),
      'unchanged': false,
      'availableCount': 0,
      'missions': <Object?>[],
      'error': 'mission-runtime-not-ready:WaitingForLoad:',
    };
    fixture.resources['/missions/available'] = valid;
    await open(tester, fixture, channel);
    await tester.tap(find.text('可接取任务'));
    await tester.pumpAndSettle();
    expect(channel.value?.message, '等待读取存档');
    expect(channel.value?.error, isFalse);
    expect(find.textContaining('mission-runtime-not-ready'), findsNothing);
    expect(channel.value?.details, '请在游戏中读取存档，完成后可刷新任务。');
    expect(find.text('请在游戏中读取存档，完成后可刷新任务。'), findsNothing);
    for (final diagnostic in [
      'mission-runtime-not-ready:Loading:',
      'mapped-guest-runtime-not-ready',
    ]) {
      fixture.resources['/missions/available'] = {
        ...valid,
        'status': 'loading',
        'error': diagnostic,
      };
      fixture.publish();
      await tester.pumpAndSettle();
      expect(channel.value?.message, '正在读取任务');
      expect(channel.value?.busy, isTrue);
      expect(channel.value?.error, isFalse);
      expect(find.textContaining(diagnostic), findsNothing);
    }
    const failure =
        'available-mission-read-failed:InvalidOperationException:合成读取失败';
    fixture.resources['/missions/available'] = {
      ...valid,
      'status': 'mission-data-incomplete',
      'error': failure,
    };
    fixture.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.message, '任务数据不完整');
    expect(channel.value?.error, isTrue);
    expect(channel.value?.details, contains(failure));
    expect(find.textContaining(failure), findsNothing);
    fixture.resources['/missions/available'] = {
      ...valid,
      'status': 'future-status',
      'error': failure,
    };
    fixture.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.message, '读取失败');
    expect(channel.value?.details, failure);
    expect(find.text(failure), findsNothing);
    expect(fixture.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('390宽150%仍能阅读异常详情和邀请原因，离线禁止写入', (tester) async {
    final fixture = model()..desktop = false;
    final channel = PageActivityChannel();
    await open(tester, fixture, channel, width: 390, scale: 1.5);
    await tab(tester, '稀客邀请');
    fixture.connected = false;
    fixture.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.message, '未连接');
    expect(channel.value?.details, '连接游戏电脑后可刷新；离线期间不能修改游戏数据。');
    expect(find.byKey(const ValueKey('extension-read-notice')), findsNothing);
    expect(find.text('羁绊未满足原生邀请条件，请先完成角色对话。'), findsOneWidget);
    expect(
      tester
          .widget<StewardButton>(find.widgetWithText(StewardButton, '邀请此稀客'))
          .onPressed,
      isNull,
    );
    expect(
      tester
          .widget<StewardIconButton>(
            find.byKey(const ValueKey('extension-refresh')),
          )
          .onPressed,
      isNull,
    );
    expect(fixture.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });
}
