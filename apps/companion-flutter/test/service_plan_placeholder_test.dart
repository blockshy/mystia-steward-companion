import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/rendering.dart' show ScrollCacheExtent;
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/service.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/features/service_plan_placeholder.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

DataMap _order(bool rare) => {
  'traceId': rare ? 'R-new' : 'N-new',
  'orderLifecycleSequence': 1,
  'deskCode': 0,
  'guestName': '现场客人',
  'foodTag': '鲜',
  'beverageTag': '清淡',
  'hasServedFood': true,
  'hasServedBeverage': false,
};

DataMap _row(bool rare, {required bool pending}) => {
  'presentationKey': rare ? 'R-new' : 'N-new',
  'presentationPending': pending,
  'order': _order(rare),
  'originalFood': {'foodId': 11, 'name': '真实原点料理'},
  'originalBeverage': {'beverageId': 21, 'name': '真实原点酒水'},
  if (!pending) ...{
    if (rare)
      'executionPlans': [
        {
          'bucket': 'complete',
          'food': {
            'recipe': {
              'id': 11,
              'recipeId': 101,
              'name': '海鲜汤',
              'price': 24,
              'cooker': '煮锅',
              'ingredients': ['虾', '海苔'],
            },
            'extraIngredients': [
              {'id': 2, 'name': '蘑菇'},
            ],
            'baseCost': 5,
            'extraCost': 2,
            'activeTags': ['鲜'],
          },
          'beverage': {
            'beverage': {'id': 21, 'name': '清茶', 'price': 12},
            'activeTags': ['清淡'],
          },
        },
      ]
    else ...{
      'executionFood': {
        'foodId': 11,
        'recipeId': 101,
        'name': '海鲜汤',
        'recipe': {'id': 11, 'recipeId': 101, 'price': 24},
        'cookerName': '煮锅',
        'baseIngredientNames': ['虾', '海苔'],
        'extraIngredients': <DataMap>[],
        'activeTags': ['鲜'],
      },
      'executionBeverage': {
        'beverageId': 21,
        'name': '清茶',
        'beverage': {'id': 21, 'price': 12},
        'activeTags': ['清淡'],
      },
    },
  },
};

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  setUpAll(() async {
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
    FixtureModel model,
    Widget child,
    double width,
    double scale,
  ) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 1000);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final theme = stewardTheme(dark: false, touch: !model.desktop);
    final channel = PageActivityChannel();
    addTearDown(channel.dispose);
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
          padding: EdgeInsets.fromLTRB(
            model.desktop ? 104 : 16,
            12,
            model.desktop ? 24 : 16,
            12,
          ),
          child: PageActivityScope(channel: channel, child: child),
        ),
      ),
    );
    await tester.pump();
  }

  for (final (width, scale) in [(640.0, 1.0), (640.0, 1.5), (390.0, 1.5)]) {
    for (final rare in [true, false]) {
      testWidgets('典型方案替换等结构占位，后续位置不动 $width/$scale rare=$rare', (
        tester,
      ) async {
        final model = FixtureModel(desktop: width >= 640, orderCount: 0);
        addTearDown(model.dispose);
        var row = _row(rare, pending: true);
        await open(
          tester,
          model,
          ListenableBuilder(
            listenable: model,
            builder: (_, _) => SingleChildScrollView(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  ServiceOrderHeading(row, rare: rare),
                  ServiceOrderSummary(model, row, rare: rare),
                  const SizedBox(
                    key: ValueKey('next-order-position'),
                    height: 40,
                  ),
                ],
              ),
            ),
          ),
          width,
          scale,
        );
        expect(find.byType(ServicePlanPlaceholder), findsOneWidget);
        expect(find.text('已有料理'), findsOneWidget);
        if (!rare) expect(find.text('原料理：真实原点料理'), findsOneWidget);
        expect(find.text('海鲜汤'), findsNothing);
        // 占位没有实体图片，不会用原点单的ID冒充尚未选定的执行目标。
        expect(
          find.descendant(
            of: find.byType(ServicePlanPlaceholder),
            matching: find.byType(EntityImage),
          ),
          findsNothing,
        );
        final before = tester.getTopLeft(
          find.byKey(const ValueKey('next-order-position')),
        );
        row = _row(rare, pending: false);
        model.publish();
        await tester.pump();
        expect(find.byType(ServicePlanPlaceholder), findsNothing);
        expect(find.text('海鲜汤'), findsOneWidget);
        expect(
          tester.getTopLeft(find.byKey(const ValueKey('next-order-position'))),
          before,
        );
        expect(find.byType(AnimatedSize), findsNothing);
        expect(tester.takeException(), isNull);
        expect(model.writes, isEmpty);
      });
    }
  }

  testWidgets('详情等待时保留原点单，错误显示且不复用旧方案', (tester) async {
    final model = FixtureModel(desktop: false, orderCount: 0);
    addTearDown(model.dispose);
    model.business = {
      'recommendations': {
        'normalOrderDetailPlans': [_row(false, pending: true)],
      },
    };
    await open(
      tester,
      model,
      ListenableBuilder(
        listenable: model,
        builder: (_, _) => ServicePage(model),
      ),
      390,
      1.5,
    );
    await tester.tap(
      find.descendant(
        of: find.byType(WorkspaceTabs<String>),
        matching: find.text('普客'),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('1 桌 · 现场客人'));
    await tester.pumpAndSettle();
    expect(find.text('真实原点料理'), findsOneWidget);
    expect(find.text('真实原点酒水'), findsOneWidget);
    expect(find.byType(ServicePlanPlaceholder), findsOneWidget);
    expect(find.text('海鲜汤'), findsNothing);
    model.recommendations['normalOrderDetailPlans'] = [
      {..._row(false, pending: false), 'presentationIssue': '合成方案读取失败'},
    ];
    model.publish();
    await tester.pump();
    expect(find.byType(ServicePlanPlaceholder), findsNothing);
    expect(find.text('合成方案读取失败'), findsOneWidget);
    expect(find.text('海鲜汤'), findsNothing);
    expect(tester.takeException(), isNull);
    expect(model.writes, isEmpty);
  });

  testWidgets('经历等待的列表行滚出再返回保留布局下限，移除后销毁', (tester) async {
    final model = FixtureModel(desktop: false, orderCount: 0);
    final scroll = ScrollController();
    addTearDown(model.dispose);
    addTearDown(scroll.dispose);
    var row = _row(true, pending: true);
    var includeOrder = true;
    const cardKey = ValueKey('kept-order-card');
    const endKey = ValueKey('list-end');
    await open(
      tester,
      model,
      ListenableBuilder(
        listenable: model,
        builder: (_, _) => ListView.builder(
          controller: scroll,
          // 显式撤掉视口外预构建，避免测试误把默认cacheExtent当成保活能力。
          scrollCacheExtent: const ScrollCacheExtent.pixels(0),
          itemCount: 8,
          itemBuilder: (_, index) => index == 0 && includeOrder
              ? Column(
                  key: cardKey,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    ServiceOrderHeading(row, rare: true),
                    ServiceOrderSummary(model, row, rare: true),
                  ],
                )
              : SizedBox(
                  key: index == 7 ? endKey : ValueKey('spacer-$index'),
                  height: 400,
                ),
        ),
      ),
      390,
      1.5,
    );
    final state = tester.state(find.byType(ServicePlanRegion));
    final pendingHeight = tester.getSize(find.byKey(cardKey)).height;
    row = _row(true, pending: false);
    model.publish();
    await tester.pump();
    expect(tester.getSize(find.byKey(cardKey)).height, pendingHeight);

    await tester.scrollUntilVisible(
      find.byKey(endKey),
      400,
      scrollable: find.byType(Scrollable),
    );
    await tester.pump();
    expect(scroll.offset, greaterThan(1000));
    expect(find.byKey(cardKey).hitTestable(), findsNothing);
    expect(state.mounted, isTrue);
    scroll.jumpTo(0);
    await tester.pump();
    expect(tester.state(find.byType(ServicePlanRegion)), same(state));
    expect(tester.getSize(find.byKey(cardKey)).height, pendingHeight);
    expect(find.byType(ServicePlanPlaceholder), findsNothing);
    expect(find.text('海鲜汤'), findsOneWidget);

    // 保活仅保存该行的布局状态，现场订单删除后必须销毁，不能残留旧方案或无限累积。
    includeOrder = false;
    model.publish();
    await tester.pump();
    expect(state.mounted, isFalse);
    expect(find.byKey(cardKey), findsNothing);
    expect(find.text('海鲜汤'), findsNothing);
    expect(tester.takeException(), isNull);
    expect(model.writes, isEmpty);
  });

  testWidgets('超长真实内容自然增高且完整保留，不以占位高度裁切', (tester) async {
    final model = FixtureModel(desktop: false, orderCount: 0);
    addTearDown(model.dispose);
    var row = _row(true, pending: true);
    await open(
      tester,
      model,
      ListenableBuilder(
        listenable: model,
        builder: (_, _) => SingleChildScrollView(
          child: Column(
            children: [
              ServiceOrderSummary(model, row, rare: true),
              const SizedBox(key: ValueKey('after-long'), height: 1),
            ],
          ),
        ),
      ),
      390,
      1.5,
    );
    final before = tester
        .getTopLeft(find.byKey(const ValueKey('after-long')))
        .dy;
    row = _row(true, pending: false);
    final food = object(records(row['executionPlans']).first['food']);
    final names = List.generate(8, (index) => '真实长名称基础材料第${index + 1}项');
    object(food['recipe'])['ingredients'] = names;
    model.publish();
    await tester.pump();
    expect(
      tester.getTopLeft(find.byKey(const ValueKey('after-long'))).dy,
      greaterThan(before),
    );
    for (final name in names) expect(find.textContaining(name), findsOneWidget);
    expect(tester.takeException(), isNull);
    expect(model.writes, isEmpty);
  });
}
