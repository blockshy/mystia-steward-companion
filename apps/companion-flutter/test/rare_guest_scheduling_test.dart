import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/rare_guest_scheduling.dart';
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/features/service.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/steward_test_fonts.dart';

/// 纯 Widget 夹具：验证当前组件几何和请求中的精确身份，不连接游戏或修改真实共享配置。
FixtureModel schedulingModel() {
  final model = FixtureModel(orderCount: 0);
  model.catalog = {
    ...model.catalog,
    'rareCustomers': [
      {'id': 1, 'name': '博丽灵梦'},
      {'id': 2, 'name': '雾雨魔理沙'},
      {'id': 3, 'name': '二岩猯藏'},
    ],
  };
  model.profile = {
    ...model.profile,
    'rareGuestParticipationModuleEnabled': true,
    'managedRareGuestIds': [1],
  };
  model.authority = {...model.authority, 'authorityRevision': 7};
  model.snapshot = {
    ...model.snapshot,
    'nightBusinessGeneration': 4,
    'nightBusiness': {
      'ordersReadComplete': true,
      'activeRareGuestsReadComplete': true,
      'activeRareGuests': [
        {'guestId': 1, 'guestName': '博丽灵梦'},
        {'guestId': 2, 'guestName': '雾雨魔理沙'},
      ],
      'orders': [
        for (var i = 1; i <= 2; i++)
          {
            'traceId': 'R-000$i',
            'orderLifecycleSequence': i,
            'guestId': 1,
            'guestName': '博丽灵梦',
            'deskCode': i - 1,
            'foodTag': '家常',
            'beverageTag': '无酒精',
            'patienceRemainingRatio': i == 1 ? .75 : null,
          },
      ],
    },
    'rareGuestParticipation': {
      'active': true,
      'ready': true,
      'businessGeneration': 4,
      'participationRevision': 8,
      'managedGuestIds': [1],
      'entries': [
        for (var i = 1; i <= 2; i++)
          {
            'traceId': 'R-000$i',
            'orderLifecycleSequence': i,
            'guestId': 1,
            'managed': true,
            'participating': i == 1,
            'queuePosition': i == 1 ? 1 : null,
          },
      ],
    },
  };
  return model;
}

void main() {
  final surfaceKey = GlobalKey();
  setUpAll(() async {
    await loadPreferredStewardTestFont();
    // 截图须使用项目真实图标字体，避免把测试宿主的方框占位当作实际界面。
    await (FontLoader('packages/forui_lucide/ForuiLucideIcons')
          ..addFont(rootBundle.load('packages/forui_lucide/assets/lucide.ttf')))
        .load();
  });

  Future<void> open(
    WidgetTester tester,
    FixtureModel model,
    double width,
    double scale, {
    bool expandSettings = true,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 850);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(model.dispose);
    final theme = stewardTheme(dark: false, touch: width < 500);
    await tester.pumpWidget(
      material.MaterialApp(
        theme: theme.toApproximateMaterialTheme(),
        builder: (context, child) => FTheme(
          data: theme,
          child: MediaQuery(
            data: MediaQuery.of(context).copyWith(
              textScaler: TextScaler.linear(scale),
              disableAnimations: true,
            ),
            child: child!,
          ),
        ),
        home: RepaintBoundary(
          key: surfaceKey,
          child: ColoredBox(
            color: theme.colors.background,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: ListenableBuilder(
                listenable: model,
                builder: (context, _) => RareGuestSchedulingPage(model),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    if (expandSettings && find.text('调度设置').evaluate().isNotEmpty) {
      await tester.tap(find.text('调度设置'));
      await tester.pumpAndSettle();
    }
  }

  for (final width in [390.0, 544.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('调度紧凑布局 $width / $scale', (tester) async {
        final model = schedulingModel();
        await open(tester, model, width, scale);
        expect(tester.takeException(), isNull);
        expect(find.text('博丽灵梦'), findsNWidgets(2));
        expect(find.text('已暂停'), findsOneWidget);
        expect(find.text('队列 1'), findsOneWidget);
        expect(find.text('等待 75%'), findsOneWidget);
        expect(find.text('等待待读取'), findsOneWidget);
        // 可选截图仅写测试证据目录，使用当前实际字体与布局，不影响请求断言。
        final directory = Platform.environment['MYSTIA_UI_EVIDENCE'];
        if (directory != null) {
          await tester.runAsync(() async {
            final image =
                await (surfaceKey.currentContext!.findRenderObject()!
                        as RenderRepaintBoundary)
                    .toImage();
            final bytes = await image.toByteData(
              format: ui.ImageByteFormat.png,
            );
            await Directory(directory).create(recursive: true);
            await File('$directory/scheduling-$width-$scale.png')
                .writeAsBytes(bytes!.buffer.asUint8List());
            image.dispose();
          });
        }
        await tester.tap(
          find.byKey(const ValueKey(('scheduling-group', 1, 'enable-tail'))),
        );
        await tester.pump(const Duration(milliseconds: 200));
        await tester.pumpAndSettle();
        final write = model.writes.single;
        expect(write.$1, '/orders/rare/participation');
        expect(write.$3?['expectedAuthorityRevision'], 7);
        expect(write.$3?['expectedBusinessGeneration'], 4);
        expect(write.$3?['expectedParticipationRevision'], 8);
        final target = object(write.$3?['target']);
        expect(target['type'], 'guest');
        expect(records(target['expectedCurrentOrders']).length, 2);
        expect(tester.takeException(), isNull);
      });
    }
  }
  for (final width in [390.0, 544.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('自动启用共享设置布局与写入 $width/$scale', (tester) async {
        final model = schedulingModel();
        final snapshot = jsonEncode(model.snapshot);
        await open(tester, model, width, scale, expandSettings: false);
        expect(
          find.byKey(const ValueKey('scheduling-auto-resume-enabled')),
          findsNothing,
        );
        await tester.tap(find.text('调度设置'));
        await tester.pumpAndSettle();
        final toggle = find.byKey(
          const ValueKey('scheduling-auto-resume-enabled'),
        );
        final threshold = find.byKey(
          const ValueKey('scheduling-auto-resume-threshold'),
        );
        final position = find.byKey(
          const ValueKey('scheduling-auto-resume-position'),
        );
        expect(tester.widget<SettingToggle>(toggle).value, isFalse);
        expect(tester.widget<IntegerSetting>(threshold).value, 30);
        expect(tester.widget<IntegerSetting>(threshold).min, 1);
        expect(tester.widget<IntegerSetting>(threshold).max, 100);
        expect(tester.widget<StewardSelect<String>>(position).value, 'tail');
        expect(find.text('每单自动启用一次；'), findsOneWidget);
        expect(find.text('手动再次暂停保持暂停'), findsOneWidget);
        await tester.tap(
          find.descendant(of: toggle, matching: find.byType(FSwitch)),
        );
        await tester.pumpAndSettle();
        expect(
          model.profile['rareGuestParticipationAutoResumeEnabled'],
          isTrue,
        );
        await tester.widget<IntegerSetting>(threshold).onSave!(45);
        await tester.pumpAndSettle();
        expect(
          model.profile['rareGuestParticipationAutoResumeThresholdPercent'],
          45,
        );
        await tester.tap(position);
        await tester.pumpAndSettle();
        await tester.tap(find.text('队首').last);
        await tester.pumpAndSettle();
        expect(
          model.profile['rareGuestParticipationAutoResumePosition'],
          'front',
        );
        expect(model.profile['managedRareGuestIds'], [1]);
        expect(
          jsonEncode(model.snapshot),
          snapshot,
          reason: '修改策略只写配置，前端不得立即启用或重新排列任何现场订单',
        );
        expect(model.writes, isEmpty);
        expect(tester.takeException(), isNull);
        final directory = Platform.environment['MYSTIA_UI_EVIDENCE'];
        if (directory != null)
          await tester.runAsync(() async {
            final image =
                await (surfaceKey.currentContext!.findRenderObject()!
                        as RenderRepaintBoundary)
                    .toImage();
            final bytes = await image.toByteData(
              format: ui.ImageByteFormat.png,
            );
            await Directory(directory).create(recursive: true);
            await File('$directory/scheduling-auto-$width-$scale.png')
                .writeAsBytes(bytes!.buffer.asUint8List());
            image.dispose();
          });
        // 切为查看设备后所有配置控件均只读，不能借已展开的表单发送配置。
        model.authority['currentDeviceIsPrimary'] = false;
        model.publish();
        await tester.pumpAndSettle();
        expect(tester.widget<SettingToggle>(toggle).onChanged, isNull);
        expect(tester.widget<IntegerSetting>(threshold).onSave, isNull);
        expect(tester.widget<StewardSelect<String>>(position).enabled, isFalse);
      });
    }
  }

  for (final desktop in [true, false]) {
    for (final scale in [100, 150]) {
      testWidgets('生产外壳调度策略 ${desktop ? 'Windows最窄' : 'Android'}/$scale%', (
        tester,
      ) async {
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(desktop ? 627 : 390, 1000);
        addTearDown(tester.view.resetDevicePixelRatio);
        addTearDown(tester.view.resetPhysicalSize);
        final model = schedulingModel()..desktop = desktop;
        addTearDown(model.dispose);
        model.localPreferences = {
          'lastPage': 1,
          'fontScalePercent': scale,
          'reduceMotion': true,
        };
        final captureKey = GlobalKey();
        await tester.pumpWidget(
          RepaintBoundary(
            key: captureKey,
            child: ProviderScope(
              overrides: [workbenchProvider.overrideWithValue(model)],
              child: const StewardApp(),
            ),
          ),
        );
        await tester.pumpAndSettle();
        final tab = find.descendant(
          of: find.byType(WorkspaceTabs<String>),
          matching: find.text('稀客调度'),
        );
        await tester.ensureVisible(tab);
        await tester.pumpAndSettle();
        await tester.tap(tab);
        await tester.pumpAndSettle();
        await tester.tap(find.text('调度设置'));
        await tester.pumpAndSettle();
        expect(find.text('等待 75%'), findsOneWidget);
        expect(find.text('剩余等待 <'), findsOneWidget);
        expect(tester.takeException(), isNull);
        final directory = Platform.environment['MYSTIA_UI_EVIDENCE'];
        if (directory != null)
          await tester.runAsync(() async {
            final image =
                await (captureKey.currentContext!.findRenderObject()!
                        as RenderRepaintBoundary)
                    .toImage();
            final bytes = await image.toByteData(
              format: ui.ImageByteFormat.png,
            );
            await Directory(directory).create(recursive: true);
            await File(
              '$directory/scheduling-shell-${desktop ? 'windows' : 'android'}-$scale.png',
            ).writeAsBytes(bytes!.buffer.asUint8List());
            image.dispose();
          });
        expect(model.writes, isEmpty);
      });
    }
  }

  testWidgets('等待比例只消费精确当前订单；未知或非法值不伪造百分比', (tester) async {
    final model = schedulingModel();
    await open(tester, model, 390, 1, expandSettings: false);
    final order = records(model.night['orders']).first;
    for (final ratio in [null, -0.1, 1.1, double.nan, double.infinity]) {
      order['patienceRemainingRatio'] = ratio;
      model.publish();
      await tester.pumpAndSettle();
      expect(find.text('等待待读取'), findsNWidgets(2));
      expect(find.text('等待 0%'), findsNothing);
    }
    for (final ratio in [0, .3, 1]) {
      order['patienceRemainingRatio'] = ratio;
      model.publish();
      await tester.pumpAndSettle();
      expect(find.text('等待 ${(ratio * 100).round()}%'), findsOneWidget);
    }
    object(model.snapshot['rareGuestParticipation'])['businessGeneration'] = 3;
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('等待待读取'), findsNWidgets(2));
    object(model.snapshot['rareGuestParticipation'])['businessGeneration'] = 4;
    order['orderLifecycleSequence'] = 99;
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('等待待读取'), findsNWidgets(2));
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('经营页签始终把统计信息放在稀客调度之后', (tester) async {
    final model = schedulingModel();
    addTearDown(model.dispose);
    final theme = stewardTheme(dark: false, touch: true);
    await tester.pumpWidget(
      material.MaterialApp(
        theme: theme.toApproximateMaterialTheme(),
        home: FTheme(data: theme, child: ServicePage(model)),
      ),
    );
    await tester.pumpAndSettle();
    final tabs = tester.widget<WorkspaceTabs<String>>(
      find.byType(WorkspaceTabs<String>),
    );
    expect(tabs.items.keys.toList(), [
      'rare',
      'normal',
      'focus',
      'scheduling',
      'statistics',
    ]);
    expect(tester.takeException(), isNull);
  });

  testWidgets('统一调度设置默认收起；名单和策略一起展开且跨人数与刷新保留', (tester) async {
    final model = schedulingModel();
    await open(tester, model, 390, 1.5, expandSettings: false);
    final header = find.text('调度设置');
    final add = find.byKey(const ValueKey('dynamic-tags-add'));
    expect(header, findsOneWidget);
    expect(find.byType(CollapsibleSection), findsOneWidget);
    expect(find.text('等待条自动启用'), findsNothing);
    expect(
      find.byKey(const ValueKey('scheduling-auto-resume-enabled')),
      findsNothing,
    );
    expect(add, findsNothing);
    expect(find.textContaining('R-0001'), findsOneWidget);
    expect(
      tester.getTopLeft(header).dy,
      lessThan(
        tester
            .getTopLeft(find.byKey(const ValueKey(('scheduling-expand', 1))))
            .dy,
      ),
    );
    await tester.tap(header);
    await tester.pumpAndSettle();
    expect(find.text('调度名单 · 1 位'), findsOneWidget);
    expect(find.text('等待条自动启用'), findsOneWidget);
    expect(
      find.byKey(const ValueKey('scheduling-auto-resume-enabled')),
      findsOneWidget,
    );
    expect(add, findsOneWidget);
    await tester.tap(find.byKey(const ValueKey(('scheduling-add-scene', 2))));
    await tester.pumpAndSettle();
    expect(find.text('调度名单 · 2 位'), findsOneWidget);
    expect(add, findsOneWidget);
    model.publish();
    await tester.pumpAndSettle();
    expect(add, findsOneWidget);
    await tester.tap(find.text('调度设置'));
    await tester.pumpAndSettle();
    expect(find.text('等待条自动启用'), findsNothing);
    expect(
      find.byKey(const ValueKey('scheduling-auto-resume-enabled')),
      findsNothing,
    );
    expect(add, findsNothing);
    model.publish();
    await tester.pumpAndSettle();
    expect(add, findsNothing);
    expect(model.profile['managedRareGuestIds'], [1, 2]);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });
  testWidgets('关闭模块保留配置但卸载队列和名单；查看设备不可写', (tester) async {
    final model = schedulingModel();
    model.profile = {
      ...model.profile,
      'rareGuestParticipationModuleEnabled': false,
    };
    await open(tester, model, 390, 1);
    expect(find.text('全部优先'), findsNothing);
    expect(model.profile['managedRareGuestIds'], [1]);
    model.profile = {
      ...model.profile,
      'rareGuestParticipationModuleEnabled': true,
    };
    model.authority = {...model.authority, 'currentDeviceIsPrimary': false};
    model.publish();
    await tester.pumpAndSettle();
    final button = tester.widget<StewardIconButton>(
      find.byKey(const ValueKey(('scheduling-group', 1, 'enable-front'))),
    );
    expect(button.onPressed, isNull);
  });
  testWidgets('暂停与单订单启用不使用桌号作身份', (tester) async {
    final model = schedulingModel();
    await open(tester, model, 544, 1);
    await tester.tap(find.text('优先'));
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pumpAndSettle();
    final target = object(model.writes.single.$3?['target']);
    expect(target['type'], 'order');
    expect(object(target['order']), {
      'businessGeneration': 4,
      'traceId': 'R-0002',
      'orderLifecycleSequence': 2,
      'guestId': 1,
    });
  });

  testWidgets('名单在顶部；只显示已加入稀客且单展开在刷新后保持', (tester) async {
    final model = schedulingModel();
    model.profile = {
      ...model.profile,
      'managedRareGuestIds': [1, 3],
    };
    final state = object(model.snapshot['rareGuestParticipation']);
    model.snapshot = {
      ...model.snapshot,
      'rareGuestParticipation': {
        ...state,
        'entries': [
          ...records(state['entries']),
          {'guestId': 2, 'traceId': 'R-OTHER', 'managed': false},
        ],
      },
    };
    await open(tester, model, 544, 1);
    final first = find.byKey(const ValueKey(('scheduling-expand', 1)));
    final third = find.byKey(const ValueKey(('scheduling-expand', 3)));
    final add = find.byKey(const ValueKey('dynamic-tags-add'));
    expect(tester.getTopLeft(add).dy, lessThan(tester.getTopLeft(first).dy));
    expect(find.byKey(const ValueKey(('scheduling-expand', 2))), findsNothing);
    expect(find.text('R-OTHER'), findsNothing);
    expect(find.textContaining('R-0001'), findsOneWidget);
    final addTop = tester.getTopLeft(add);
    await tester.ensureVisible(third);
    await tester.tap(third);
    await tester.pumpAndSettle();
    expect(find.textContaining('R-0001'), findsNothing);
    expect(find.text('暂无当前订单'), findsOneWidget);
    model.publish();
    await tester.pumpAndSettle();
    expect(find.textContaining('R-0001'), findsNothing);
    expect(find.text('暂无当前订单'), findsOneWidget);
    expect(tester.getTopLeft(add), addTop);
    await tester.tap(third);
    await tester.pumpAndSettle();
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('暂无当前订单'), findsNothing);
    expect(find.textContaining('R-0001'), findsNothing);
    expect(model.writes, isEmpty);
  });

  testWidgets('现场动态标签按精确ID添加；删除名单不发送订单操作', (tester) async {
    final model = schedulingModel();
    await open(tester, model, 390, 1);
    expect(
      find.byKey(const ValueKey(('scheduling-add-scene', 1))),
      findsNothing,
    );
    final add = find.byKey(const ValueKey(('scheduling-add-scene', 2)));
    expect(add, findsOneWidget);
    await tester.tap(add);
    await tester.pumpAndSettle();
    expect(model.profile['managedRareGuestIds'], [1, 2]);
    expect(add, findsNothing);
    await tester.tap(find.byKey(const ValueKey(('dynamic-tag', 1))));
    await tester.pumpAndSettle();
    expect(model.profile['managedRareGuestIds'], [2]);
    expect(find.byKey(const ValueKey(('scheduling-expand', 1))), findsNothing);
    expect(
      find.byKey(const ValueKey(('scheduling-add-scene', 1))),
      findsOneWidget,
    );
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('读取未就绪或代次改变不得操作；不把未知场景显示为无人', (tester) async {
    final model = schedulingModel();
    final state = object(model.snapshot['rareGuestParticipation']);
    model.snapshot = {
      ...model.snapshot,
      'nightBusiness': {
        ...model.night,
        'activeRareGuestsReadComplete': false,
        'activeRareGuests': <DataMap>[],
      },
      'rareGuestParticipation': {...state, 'ready': false},
    };
    await open(tester, model, 390, 1);
    expect(find.text('正在读取到场稀客'), findsOneWidget);
    expect(
      tester
          .widget<StewardIconButton>(
            find.byKey(const ValueKey(('scheduling-group', 1, 'pause'))),
          )
          .onPressed,
      isNull,
    );
    expect(
      tester
          .widget<StewardButton>(find.widgetWithText(StewardButton, '暂停'))
          .onPressed,
      isNull,
    );
    model.snapshot = {
      ...model.snapshot,
      'nightBusinessGeneration': 5,
      'rareGuestParticipation': {...state, 'ready': true},
    };
    model.publish();
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<StewardIconButton>(
            find.byKey(const ValueKey(('scheduling-group', 1, 'pause'))),
          )
          .onPressed,
      isNull,
    );
    expect(model.writes, isEmpty);
  });
}
