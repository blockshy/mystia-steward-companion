import 'dart:io';

import 'package:companion_client/companion_client.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/service.dart';
import 'package:mystia_steward_companion/features/service_focus.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/features/service_plan_placeholder.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 与旧几何夹具不同，本测试显式拆开现场 snapshot 和延迟不变的 business 树，
/// 使用生产纯投影；不能让测试通过更新 business 冒充真实快照已及时显示。
class _LiveModel extends FixtureModel {
  _LiveModel({required super.desktop}) : super(orderCount: 0) {
    final old = order(1);
    snapshot = {
      'nightBusiness': {
        'orders': [old],
      },
      'normalBusiness': {'orders': <DataMap>[]},
    };
    business = {
      'pending': true,
      'isCurrent': false,
      'recommendations': {
        'recommendations': [
          {
            'order': {...old},
            'executionPlans': [
              {
                'food': {
                  'recipe': {'name': '仅旧订单方案', 'ingredients': <Object?>[]},
                  'extraIngredients': <Object?>[],
                },
                'beverage': {
                  'beverage': {'name': '仅旧订单酒水'},
                },
              },
            ],
          },
        ],
      },
    };
  }
  @override
  DataMap get serviceOrders => connected
      ? projectLiveServiceOrders(
          snapshot,
          recommendations,
          contextMatches: true,
          current: business['isCurrent'] == true,
        )
      : const {};

  DataMap order(int id, {bool normal = false}) => {
    'traceId': '${normal ? 'N' : 'R'}-$id',
    'orderLifecycleSequence': id,
    'deskCode': id - 1,
    'guestName': '现场客人$id',
    'hasServedFood': false,
    'hasServedBeverage': false,
    if (normal) ...{
      'orderKey': 'ptr:$id',
      'foodId': id,
      'foodName': '现场料理$id',
      'beverageId': id + 100,
      'beverageName': '现场酒水$id',
    } else ...{
      'foodTag': '鲜',
      'beverageTag': '甜',
    },
  };
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    final font = Platform.environment['MYSTIA_UI_FONT'];
    if (font != null) {
      final bytes = await File(font).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });
  testWidgets('经营页头只对可见类型的实际计算转圈，专注隐藏不污染页头', (tester) async {
    final model = _LiveModel(desktop: true);
    final channel = PageActivityChannel();
    addTearDown(model.dispose);
    addTearDown(channel.dispose);
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(1280, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final theme = stewardTheme(dark: false, touch: false);
    await tester.pumpWidget(
      material.MaterialApp(
        localizationsDelegates: FLocalizations.localizationsDelegates,
        supportedLocales: FLocalizations.supportedLocales,
        theme: theme.toApproximateMaterialTheme(),
        builder: (context, child) => FTheme(data: theme, child: child!),
        home: PageActivityScope(
          channel: channel,
          child: ListenableBuilder(
            listenable: model,
            builder: (_, _) => ServicePage(model),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    Finder tab(String value) => find.descendant(
      of: find.byType(WorkspaceTabs<String>),
      matching: find.text(value),
    );
    expect(channel.value?.busy, isFalse);
    expect(channel.value?.message, contains('方案仅供参考，执行状态待同步'));

    model.business['isCalculating'] = true;
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.busy, isTrue);
    expect(channel.value?.message, contains('正在计算方案'));
    await tester.enterText(
      find.byKey(const ValueKey('service-name-filter')),
      '不存在的客人',
    );
    await tester.pumpAndSettle();
    expect(channel.value?.busy, isFalse);
    expect(channel.value?.message, isNot(contains('正在计算方案')));
    await tester.enterText(
      find.byKey(const ValueKey('service-name-filter')),
      '',
    );
    await tester.pumpAndSettle();
    expect(channel.value?.busy, isTrue);
    // 后台仍在计算稀客；本页没有普客，不能因全局计算标志继续转圈。
    await tester.tap(tab('普客'));
    await tester.pumpAndSettle();
    expect(channel.value?.busy, isFalse);
    expect(channel.value?.message, isNot(contains('正在计算方案')));

    model.localPreferences['serviceFocusShowRare'] = false;
    model.localPreferences['serviceFocusShowNormal'] = true;
    model.publish();
    await tester.tap(tab('专注'));
    await tester.pumpAndSettle();
    expect(channel.value?.busy, isFalse);
    expect(channel.value?.message, contains('当前没有所选类型的实际订单'));
    model.localPreferences['serviceFocusShowRare'] = true;
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.busy, isTrue);
    expect(channel.value?.message, contains('正在计算方案'));
    model.localPreferences['serviceFocusShowRare'] = false;
    model.localPreferences['serviceFocusShowNormal'] = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.busy, isFalse);
    expect(channel.value?.message, contains('稀客和普客均已隐藏'));
    model.localPreferences['serviceFocusShowRare'] = true;

    model.business['isCalculating'] = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.busy, isFalse);
    expect(channel.value?.message, contains('方案仅供参考，执行状态待同步'));
    model.snapshot['nightBusiness'] = {
      'orders': [model.order(2)],
    };
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.busy, isFalse);
    expect(channel.value?.message, contains('方案待更新'));

    model.business['error'] = '合成求解错误';
    model.business['isCalculating'] = true;
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.error, isTrue);
    expect(channel.value?.busy, isFalse);
    expect(channel.value?.message, contains('方案计算失败：合成求解错误'));
    expect(tester.takeException(), isNull);
    expect(model.writes, isEmpty);
  });
  testWidgets('重复现场身份保留独立展示key且不借方案，不因键冲突崩溃', (tester) async {
    final model = _LiveModel(desktop: true);
    final channel = PageActivityChannel();
    addTearDown(model.dispose);
    addTearDown(channel.dispose);
    model.snapshot['nightBusiness'] = {
      'orders': [model.order(1), model.order(1)],
    };
    final rows = records(model.serviceOrders['recommendations']);
    expect(rows.map((row) => row['presentationKey']).toSet().length, 2);
    expect(
      rows.every(
        (row) =>
            row['presentationPending'] == true &&
            !row.containsKey('executionPlans'),
      ),
      isTrue,
    );
    final theme = stewardTheme(dark: false, touch: false);
    await tester.pumpWidget(
      material.MaterialApp(
        localizationsDelegates: FLocalizations.localizationsDelegates,
        supportedLocales: FLocalizations.supportedLocales,
        theme: theme.toApproximateMaterialTheme(),
        builder: (context, child) => FTheme(data: theme, child: child!),
        home: PageActivityScope(channel: channel, child: ServicePage(model)),
      ),
    );
    // 重复现场身份也必须立即显示各自占位，不等待计时器或借用旧方案。
    await tester.pump();
    expect(tester.takeException(), isNull);
    final listScrollable = find.descendant(
      of: find.descendant(
        of: find.byType(DenseListDetail),
        matching: find.byType(ListView),
      ),
      matching: find.byType(Scrollable),
    );
    expect(listScrollable, findsOneWidget);
    for (final row in rows) {
      // DenseListDetail 的 identity 返回 String，因此真实按钮是 ValueKey<String>。
      // Map 下标的静态类型是 Object?；直接推断 ValueKey<Object?> 会因 key 的泛型类型
      // 不同而找不到已存在的按钮，不能把这种夹具错误解释成产品漏接了展示身份。
      final card = find.byKey(
        ValueKey<String>(row['presentationKey'] as String),
      );
      // 完整占位会使后一行落在惰性列表的构建范围外。必须实际滚动到每个精确
      // 展示key，再检查其独立身份与无方案状态，不能只验证首行或把查找改成宽泛标题。
      await tester.scrollUntilVisible(card, 160, scrollable: listScrollable);
      // 实际拖动会触发 Forui 的触摸反馈计时器；等待该反馈结束，避免把测试
      // 手势未收尾误报为产品订单入场计时器泄漏。订单即时出现已在前文验证。
      await tester.pumpAndSettle(const Duration(milliseconds: 150));
      expect(card, findsOneWidget);
      expect(tester.widget(card), isA<FButton>());
      final heading = tester.widget<ServiceOrderHeading>(
        find.descendant(of: card, matching: find.byType(ServiceOrderHeading)),
      );
      expect(heading.item['presentationKey'], row['presentationKey']);
      expect(heading.item['presentationPending'], isTrue);
      expect(heading.item.containsKey('executionPlans'), isFalse);
      expect(
        find.descendant(
          of: card,
          matching: find.byType(ServicePlanPlaceholder),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(of: card, matching: find.text('仅旧订单方案')),
        findsNothing,
      );
    }
    expect(find.text('仅旧订单方案'), findsNothing);
    expect(find.text('方案待更新'), findsNothing);
    expect(channel.value?.message, contains('方案待更新'));
    expect(model.writes, isEmpty);
  });
  for (final (width, scale) in [(640.0, 1.0), (640.0, 1.5), (390.0, 1.5)]) {
    for (final focus in [false, true]) {
      testWidgets('新订单立即显示真实点单，方案替换占位不移动后卡 $width/$scale focus=$focus', (
        tester,
      ) async {
        final model = _LiveModel(desktop: width >= 640);
        final channel = PageActivityChannel();
        addTearDown(model.dispose);
        addTearDown(channel.dispose);
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(width, 1400);
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        model.snapshot['nightBusiness'] = {'orders': <DataMap>[]};
        final theme = stewardTheme(dark: false, touch: !model.desktop);
        await tester.pumpWidget(
          material.MaterialApp(
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
            home: PageActivityScope(
              channel: channel,
              child: ListenableBuilder(
                listenable: model,
                builder: (_, _) => ServicePage(model),
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();
        if (focus) {
          await tester.tap(
            find.descendant(
              of: find.byType(WorkspaceTabs<String>),
              matching: find.text('专注'),
            ),
          );
          await tester.pumpAndSettle();
        }
        final order = model.order(2);
        final following = model.order(3);
        model.snapshot['nightBusiness'] = {
          'orders': [order, following],
        };
        model.publish();
        await tester.pump();
        expect(find.byType(ServiceOrderHeading), findsNWidgets(2));
        expect(find.byType(ServicePlanPlaceholder), findsNWidgets(2));
        expect(find.text('仅旧订单方案'), findsNothing);
        expect(find.textContaining('当前没有'), findsNothing);
        expect(channel.value?.message, contains('方案待更新'));
        final live = records(model.serviceOrders['recommendations']);
        final key = live.first['presentationKey'] as String;
        final nextKey = live.last['presentationKey'] as String;
        final card = find.byKey(
          ValueKey<String>(focus ? 'focus-rare-$key' : key),
        );
        final nextCard = find.byKey(
          ValueKey<String>(focus ? 'focus-rare-$nextKey' : nextKey),
        );
        final before = tester.getRect(card);
        final nextBefore = tester.getTopLeft(nextCard);
        // 方案直接来自独立业务响应；显示顺位只存在于 HTTP 快照，不得破坏严格需求联结。
        model.snapshot['nightBusiness'] = {
          'orders': [
            {...order, 'serviceOrderPosition': 0},
            {...following, 'serviceOrderPosition': 1},
          ],
        };
        model.business['recommendations'] = <String, Object?>{
          'recommendations': <DataMap>[
            {
              ...records(model.recommendations['recommendations']).single,
              'order': order,
            },
          ],
        };
        model.publish();
        await tester.pump();
        expect(find.byType(ServiceOrderHeading), findsNWidgets(2));
        expect(find.byType(ServicePlanPlaceholder), findsOneWidget);
        expect(find.text('仅旧订单方案'), findsWidgets);
        expect(find.text('仅旧订单酒水'), findsWidgets);
        final rect = tester.getRect(card);
        expect(rect, before);
        expect(tester.getTopLeft(nextCard), nextBefore);
        await tester.pump(const Duration(milliseconds: 250));
        expect(tester.getRect(card), rect);
        expect(tester.takeException(), isNull);
        expect(model.writes, isEmpty);
      });
    }
    testWidgets('刷新状态不改普稀卡片和专注内容高度 $width/$scale', (tester) async {
      final model = _LiveModel(desktop: width >= 640);
      final channel = PageActivityChannel();
      addTearDown(model.dispose);
      addTearDown(channel.dispose);
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = Size(width, 1000);
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final normal = model.order(2, normal: true);
      model.snapshot['normalBusiness'] = {
        'orders': [normal],
      };
      model.recommendations['normalOrderDetailPlans'] = [
        {
          'order': normal,
          'originalFood': {'foodId': 2, 'name': '现场料理2'},
          'originalBeverage': {'beverageId': 102, 'name': '现场酒水2'},
          'executionFood': {
            'foodId': 2,
            'recipeId': 42,
            'name': '稳定普客料理',
            'cookerName': '煮锅',
            'baseIngredientNames': <String>[],
            'extraIngredientIds': <int>[],
            'extraIngredients': <DataMap>[],
            'activeTags': <String>[],
            'suppressedTags': <String>[],
          },
          'executionBeverage': {
            'beverageId': 102,
            'name': '稳定普客酒水',
            'activeTags': <String>[],
            'suppressedTags': <String>[],
          },
        },
      ];
      model.business['isCurrent'] = true;
      model.business['pending'] = false;
      final theme = stewardTheme(dark: false, touch: !model.desktop);
      await tester.pumpWidget(
        material.MaterialApp(
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
            padding: const EdgeInsets.all(16),
            child: PageActivityScope(
              channel: channel,
              child: ListenableBuilder(
                listenable: model,
                builder: (_, _) => ServicePage(model),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      Finder tab(String value) => find.descendant(
        of: find.byType(WorkspaceTabs<String>),
        matching: find.text(value),
      );
      Future<void> assertStable(Finder card, String food) async {
        expect(card, findsOneWidget);
        final bounds = tester.getRect(card);
        for (final (current, calculating) in [
          (false, true),
          (false, false),
          (true, false),
        ]) {
          model.business['isCurrent'] = current;
          model.business['pending'] = !current;
          model.business['isCalculating'] = calculating;
          model.publish();
          await tester.pumpAndSettle();
          // 比较真实整卡矩形而非只检查无溢出：瞬时状态不得新增文字行或删掉主方案。
          expect(tester.getRect(card), bounds);
          expect(
            find.descendant(of: card, matching: find.text(food)),
            findsWidgets,
          );
          expect(find.text('参考方案'), findsNothing);
          expect(find.text('方案待更新'), findsNothing);
          expect(tester.takeException(), isNull);
        }
      }

      for (final (kind, tabLabel, food) in [
        ('rare', '稀客', '仅旧订单方案'),
        ('normal', '普客', '稳定普客料理'),
      ]) {
        await tester.tap(tab(tabLabel));
        await tester.pumpAndSettle();
        final row = records(
          model.serviceOrders[kind == 'rare'
              ? 'recommendations'
              : 'normalOrderDetailPlans'],
        ).single;
        final identity = row['presentationKey'] as String;
        await assertStable(find.byKey(ValueKey<String>(identity)), food);
        model.localPreferences['serviceFocusShowRare'] = kind == 'rare';
        model.localPreferences['serviceFocusShowNormal'] = kind == 'normal';
        model.publish();
        await tester.tap(tab('专注'));
        await tester.pumpAndSettle();
        await assertStable(find.byKey(ValueKey('focus-$kind-$identity')), food);
      }
      expect(model.writes, isEmpty);
    });
    testWidgets('现场增删送达无需等待旧结果，经营与专注一致 $width/$scale', (tester) async {
      final model = _LiveModel(desktop: width >= 640);
      final channel = PageActivityChannel();
      addTearDown(model.dispose);
      addTearDown(channel.dispose);
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = Size(width, 920);
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final theme = stewardTheme(dark: false, touch: !model.desktop);
      await tester.pumpWidget(
        material.MaterialApp(
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
            padding: const EdgeInsets.all(16),
            child: PageActivityScope(
              channel: channel,
              child: ListenableBuilder(
                listenable: model,
                builder: (_, _) => ServicePage(model),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      Finder heading(String trace) => find.byWidgetPredicate(
        (widget) =>
            widget is ServiceOrderHeading &&
            object(widget.item['order'])['traceId'] == trace,
      );
      Finder tab(String value) => find.descendant(
        of: find.byType(WorkspaceTabs<String>),
        matching: find.text(value),
      );
      expect(heading('R-1'), findsWidgets);
      final retained = model.business;
      model.snapshot['nightBusiness'] = {
        'orders': [
          {...model.order(2), 'hasServedFood': true},
        ],
      };
      model.snapshot['normalBusiness'] = {
        'orders': [model.order(3, normal: true)],
      };
      model.publish();
      await tester.pump();
      // 旧订单移除与新订单事实均不延迟；无需任何经过时间即可看到新桌号及送达事实。
      expect(heading('R-1'), findsNothing);
      expect(identical(model.business, retained), isTrue);
      expect(heading('R-1'), findsNothing);
      expect(heading('R-2'), findsWidgets);
      expect(find.text('已有料理'), findsWidgets);
      expect(find.text('仅旧订单方案'), findsNothing);
      expect(find.text('方案待更新'), findsNothing);
      expect(channel.value?.message, contains('方案待更新'));

      await tester.tap(tab('普客'));
      await tester.pumpAndSettle();
      expect(heading('N-3'), findsWidgets);
      expect(find.textContaining('现场料理3'), findsWidgets);
      expect(find.text('仅旧订单酒水'), findsNothing);
      await tester.tap(tab('专注'));
      await tester.pumpAndSettle();
      expect(find.byType(ServiceFocusPage), findsOneWidget);
      expect(heading('R-1'), findsNothing);
      expect(heading('R-2'), findsWidgets);
      // 两个待计算订单已经预留制作资料，窄屏大字仍须能滚动访问原点单而无溢出。
      final focusScroll = find.descendant(
        of: find.byKey(const ValueKey('service-focus-scroll')),
        matching: find.byType(Scrollable),
      );
      await tester.scrollUntilVisible(
        heading('N-3'),
        120,
        scrollable: focusScroll,
      );
      expect(heading('N-3'), findsOneWidget);
      expect(tester.takeException(), isNull);
      model.snapshot['nightBusiness'] = {'orders': <DataMap>[]};
      model.snapshot['normalBusiness'] = {'orders': <DataMap>[]};
      model.publish();
      await tester.pumpAndSettle();
      expect(find.byType(ServiceOrderHeading), findsNothing);
      expect(channel.value?.message, contains('当前没有所选类型的实际订单'));
      expect(model.writes, isEmpty);
      model.snapshot['nightBusiness'] = {
        'orders': [model.order(1)],
        'error': '合成读取失败',
      };
      model.publish();
      await tester.pumpAndSettle();
      expect(find.byType(ServiceOrderHeading), findsNothing);
      expect(channel.value?.message, contains('现场订单读取尚未完成'));
    });
  }
}
