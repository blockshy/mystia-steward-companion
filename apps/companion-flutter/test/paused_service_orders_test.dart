import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:companion_client/companion_client.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/features/service_focus.dart';
import 'package:mystia_steward_companion/ui/components.dart';

import 'support/fixture_model.dart';
import 'support/steward_test_fonts.dart';

/// 真实故障的身份组合：同一 guestId 的四笔订单各有独立 trace/lifecycle，
/// 暂停只约束自动化参与；现场列表、手动制作配方与客户端显示必须仍可阅读。
class _PausedOrdersModel extends FixtureModel {
  _PausedOrdersModel({required super.desktop}) : super(orderCount: 0) {
    canExecute = false;
    snapshot = {
      ...snapshot,
      'nightBusinessGeneration': 9,
      'nightBusiness': {'orders': orders, 'ordersReadComplete': true},
      'normalBusiness': {'orders': <DataMap>[]},
      'rareGuestParticipation': {
        'active': true,
        'ready': true,
        'businessGeneration': 9,
        'participationRevision': 1,
        'displayOrderKeys': [for (final order in orders) identity(order)],
        'entries': [
          for (final order in orders)
            {
              ...order,
              'managed': true,
              'participating': false,
              'reasonCode': 'managed-lifecycle-default-paused',
            },
        ],
      },
    };
    business = {
      'isCurrent': true,
      'recommendations': {
        'recommendations': [
          for (final order in orders)
            {
              'order': {...order},
              'executionPlans': [
                {
                  'bucket': 'complete',
                  'food': {
                    'recipe': {
                      'name': '料理-${order['traceId']}',
                      'cooker': '煮锅',
                      'ingredients': ['豆腐'],
                    },
                    'extraIngredients': <DataMap>[],
                  },
                  'beverage': {
                    'beverage': {'name': '酒水-${order['traceId']}'},
                  },
                },
              ],
            },
        ],
      },
    };
  }

  final List<DataMap> orders = [
    for (final (trace, desk, lifecycle) in [
      ('R-0005', 7, 5),
      ('R-0022', 1, 22),
      ('R-0024', 2, 24),
      ('R-0025', 5, 25),
    ])
      {
        'traceId': trace,
        'orderLifecycleSequence': lifecycle,
        'guestId': 30,
        'guestName': '萌澄果',
        'deskCode': desk,
        'serviceOrderPosition': lifecycle,
        'foodTag': '清淡',
        'beverageTag': '无酒精',
        'hasServedFood': false,
        'hasServedBeverage': false,
      },
  ];

  DataMap identity(DataMap order) => {
    'businessGeneration': 9,
    'traceId': order['traceId'],
    'orderLifecycleSequence': order['orderLifecycleSequence'],
    'guestId': 30,
  };

  @override
  DataMap get serviceOrders => projectLiveServiceOrders(
    snapshot,
    recommendations,
    contextMatches: true,
    current: true,
  );
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  setUpAll(() async {
    await loadPreferredStewardTestFont();
    await (FontLoader('packages/forui_lucide/ForuiLucideIcons')
          ..addFont(rootBundle.load('packages/forui_lucide/assets/lucide.ttf')))
        .load();
  });

  test('四个同名精确订单的暂停、恢复和坏队列都不删现场或丢主方案', () {
    final model = _PausedOrdersModel(desktop: true);
    addTearDown(model.dispose);
    final unchanged = jsonEncode(model.snapshot);
    List<DataMap> rows() => records(model.serviceOrders['recommendations']);
    void verifyPlans() {
      expect(rows(), hasLength(4));
      expect(rows().map((row) => row['presentationKey']).toSet(), hasLength(4));
      expect(
        rows().every((row) => row['presentationPending'] == false),
        isTrue,
      );
      for (final row in rows()) {
        final food = object(records(row['executionPlans']).first['food']);
        expect(
          object(food['recipe'])['name'],
          '料理-${object(row['order'])['traceId']}',
        );
      }
      expect(model.canExecute, isFalse);
      expect(model.writes, isEmpty);
    }

    verifyPlans();
    expect(
      rows().every((row) => row['presentationSchedulingPaused'] == true),
      isTrue,
    );
    expect(jsonEncode(model.snapshot), unchanged);
    final participation = object(model.snapshot['rareGuestParticipation']);
    final entry = records(participation['entries'])[2];
    entry['participating'] = true;
    participation['displayOrderKeys'] = [
      model.identity(model.orders[2]),
      model.identity(model.orders[0]),
      model.identity(model.orders[1]),
      model.identity(model.orders[3]),
    ];
    verifyPlans();
    expect(object(rows().first['order'])['traceId'], 'R-0024');
    expect(rows().first['presentationSchedulingPaused'], isFalse);
    expect(
      rows()
          .skip(1)
          .every((row) => row['presentationSchedulingPaused'] == true),
      isTrue,
    );
    entry['participating'] = false;
    verifyPlans();
    expect(
      rows().every((row) => row['presentationSchedulingPaused'] == true),
      isTrue,
    );
    // 尚未同步的新旧队列只影响排序；不能把未知参与状态伪装为暂停。
    for (final keys in [
      <DataMap>[],
      [model.identity(model.orders[0])],
      [for (var i = 0; i < 4; i++) model.identity(model.orders[0])],
    ]) {
      participation['displayOrderKeys'] = keys;
      verifyPlans();
      expect(
        rows().map((row) => object(row['order'])['traceId']),
        model.orders.map((o) => o['traceId']),
      );
    }
    // 模块停用时即便残留完整且反向的排序键，也只能沿用现场时间顺序。
    participation['active'] = false;
    for (final keys in [
      [for (final order in model.orders.reversed) model.identity(order)],
      <DataMap>[],
    ]) {
      participation['displayOrderKeys'] = keys;
      verifyPlans();
      expect(
        rows().map((row) => object(row['order'])['traceId']),
        model.orders.map((order) => order['traceId']),
      );
      expect(
        rows().every((row) => row['presentationSchedulingPaused'] == false),
        isTrue,
      );
    }
    participation['active'] = true;
    participation['ready'] = false;
    verifyPlans();
    expect(
      rows().every((row) => row['presentationSchedulingPaused'] == false),
      isTrue,
    );
    participation['ready'] = true;
    participation['businessGeneration'] = 8;
    verifyPlans();
    expect(
      rows().every((row) => row['presentationSchedulingPaused'] == false),
      isTrue,
    );
  });

  for (final desktop in [true, false]) {
    for (final scale in [100, 150]) {
      testWidgets('暂停订单可浏览且配方不跳动 ${desktop ? 'Windows最窄' : 'Android'}/$scale%', (
        tester,
      ) async {
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(desktop ? 627 : 390, 1000);
        addTearDown(tester.view.resetDevicePixelRatio);
        addTearDown(tester.view.resetPhysicalSize);
        final model = _PausedOrdersModel(desktop: desktop);
        addTearDown(model.dispose);
        model.localPreferences = {
          'lastPage': 1,
          'fontScalePercent': scale,
          'reduceMotion': true,
        };
        final key = GlobalKey();
        await tester.pumpWidget(
          RepaintBoundary(
            key: key,
            child: ProviderScope(
              overrides: [workbenchProvider.overrideWithValue(model)],
              child: const StewardApp(),
            ),
          ),
        );
        await tester.pumpAndSettle();
        expect(find.text('8 桌 · 萌澄果'), findsOneWidget);
        expect(find.text('料理-R-0005'), findsWidgets);
        final heading = tester.widget<OrderHeading>(
          find.byType(OrderHeading).first,
        );
        expect(heading.statusDescription, contains('调度暂停'));
        expect(heading.statusDescription, contains('待送料理'));
        expect(tester.takeException(), isNull);
        final tab = find.descendant(
          of: find.byType(WorkspaceTabs<String>),
          matching: find.text('专注'),
        );
        await tester.tap(tab);
        await tester.pumpAndSettle();
        final focusScroll = find
            .descendant(
              of: find.byType(ServiceFocusPage),
              matching: find.byType(Scrollable),
            )
            .first;
        for (final order in model.orders) {
          final title = find.text('${(order['deskCode'] as int) + 1} 桌 · 萌澄果');
          if (title.evaluate().isEmpty) {
            await tester.scrollUntilVisible(
              title,
              220,
              scrollable: focusScroll,
            );
            await tester.pumpAndSettle();
          }
          expect(title, findsOneWidget);
          expect(find.text('料理-${order['traceId']}'), findsOneWidget);
          expect(find.text('酒水-${order['traceId']}'), findsOneWidget);
          expect(tester.takeException(), isNull);
        }
        // 惰性列表需要真实滚回首单后比较位置，不能以扩大缓存范围伪造四单可浏览。
        await tester.scrollUntilVisible(
          find.text('8 桌 · 萌澄果'),
          -300,
          scrollable: focusScroll,
        );
        await tester.pumpAndSettle();
        final position = tester.getTopLeft(find.text('料理-R-0005'));
        records(object(model.snapshot['rareGuestParticipation'])['entries'])
                .first['participating'] =
            true;
        model.publish();
        await tester.pumpAndSettle();
        expect(tester.getTopLeft(find.text('料理-R-0005')), position);
        expect(tester.takeException(), isNull);
        expect(model.canExecute, isFalse);
        expect(model.writes, isEmpty);
        final evidence = Platform.environment['MYSTIA_UI_EVIDENCE'];
        if (evidence != null)
          await tester.runAsync(() async {
            final image =
                await (key.currentContext!.findRenderObject()!
                        as RenderRepaintBoundary)
                    .toImage();
            final bytes = await image.toByteData(
              format: ui.ImageByteFormat.png,
            );
            await Directory(evidence).create(recursive: true);
            await File(
              '$evidence/paused-focus-${desktop ? 'windows' : 'android'}-$scale.png',
            ).writeAsBytes(bytes!.buffer.asUint8List());
            image.dispose();
          });
      });
    }
  }
}
