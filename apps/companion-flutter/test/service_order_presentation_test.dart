import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/service.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/menu_item_content.dart';

import 'support/fixture_model.dart';

/// 使用游戏/API的原始桌号0与7，不沿用历史UI夹具的一基编号。
/// 推荐料理和酒水来自同一个主方案，独立候选故意不同，防止展示层拼成错误搭配。
class _ServiceModel extends FixtureModel {
  @override
  int connectionVersion = 0;
  _ServiceModel({bool touch = false}) : super(desktop: !touch, orderCount: 0) {
    business = {
      'automation': {
        'rareDiagnostics': [
          {
            'orderKey': 'rare:7|991|1|2',
            'title': '雾雨魔理沙 · 桌 8',
            'stepLabel': '等待重试',
            'paused': true,
          },
        ],
        'normalDiagnostics': [
          {
            'orderKey': 'normal:0|100|200',
            'title': '桌 1 · 海鲜味噌汤',
            'stepLabel': '等待重试',
            'paused': true,
          },
        ],
      },
      'recommendations': {
        'recommendations': [
          for (final desk in [0, 7]) rare(desk),
        ],
        'normalOrderDetailPlans': [
          for (final desk in [0, 7])
            {
              'order': order(desk, rareGuest: false),
              'originalFood': {'foodId': 71, 'name': '饭团'},
              'originalBeverage': {'beverageId': 72, 'name': '绿茶'},
              'executionFood': {
                'foodId': 73,
                'name': '海鲜味噌汤',
                'cookerName': '煮锅',
                'baseIngredientNames': ['豆腐', '海苔'],
                'extraIngredients': [
                  {'id': 2, 'name': '蘑菇'},
                ],
                'activeTags': ['鲜'],
                'targetTags': ['家常'],
                'suppressedTags': ['重油'],
              },
              'executionBeverage': {
                'name': '冰山毛玉冻柠',
                'beverageId': 3,
                'activeTags': ['无酒精'],
                'suppressedTags': ['高酒精'],
              },
              'usesSpecialExecution': true,
              'hasExecutionOverride': true,
              'executionReason': '按特殊经营规则替换',
              'selectionMessage': '当前库存满足主方案',
            },
        ],
      },
    };
  }

  static DataMap order(int desk, {bool rareGuest = true}) => {
    'deskCode': desk,
    'guestName': rareGuest
        ? (desk == 0 ? '博丽灵梦' : '雾雨魔理沙')
        : (desk == 0 ? '人类村民' : '妖精'),
    'traceId': '${rareGuest ? 'rare' : 'normal'}-$desk',
    'orderLifecycleSequence': 1,
    'runtimeGuestId': 991,
    'foodTagId': 1,
    'beverageTagId': 2,
    'foodTag': '清淡',
    'beverageTag': '无酒精',
  };

  static DataMap rare(int desk) => {
    'order': order(desk),
    'executionPlans': [
      {
        'bucket': 'complete',
        'food': {
          'recipe': {
            'name': '海鲜味噌汤',
            'cooker': '煮锅',
            'ingredients': ['豆腐', '海苔'],
          },
          'extraIngredients': [
            {'id': 2, 'name': '蘑菇'},
          ],
          'activeTags': ['鲜'],
          'suppressedTags': ['重油'],
          'meetsRequiredFood': true,
        },
        'beverage': {
          'beverage': {'id': 3, 'name': '冰山毛玉冻柠'},
          'activeTags': ['无酒精'],
          'meetsRequiredBeverage': true,
        },
      },
    ],
    'beverages': [
      {
        'beverage': {'name': '不属于主方案的候选酒水'},
      },
    ],
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

  Future<void> open(
    WidgetTester tester,
    _ServiceModel model, {
    double width = 1280,
    int scale = 100,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    model.localPreferences = {'fontScalePercent': scale, 'reduceMotion': true};
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pumpAndSettle();
  }

  Finder serviceText(String value) =>
      find.descendant(of: find.byType(ServicePage), matching: find.text(value));
  Finder orderRow(String kind, int desk) =>
      find.byKey(ValueKey('$kind-$desk:$desk:1'));
  Finder filter() => find.descendant(
    of: find.byWidgetPredicate(
      (widget) => widget is StewardField && widget.title == '筛选客人或桌号',
    ),
    matching: find.byType(EditableText),
  );

  Finder orderTag(Finder owner, String text, OrderTagKind kind) =>
      find.descendant(
        of: owner,
        matching: find.byWidgetPredicate(
          (widget) =>
              widget is OrderTag && widget.text == text && widget.kind == kind,
        ),
      );

  for (final width in [1280.0, 640.0, 390.0]) {
    for (final scale in [100, 150]) {
      testWidgets('稀客外层点单料理酒水及普客桌号可见：$width px / $scale%', (tester) async {
        final model = _ServiceModel(touch: width < 640);
        addTearDown(model.dispose);
        for (final item in records(model.recommendations['recommendations'])) {
          object(item['order'])['hasServedFood'] = false;
          object(item['order'])['hasServedBeverage'] = true;
        }
        await open(tester, model, width: width, scale: scale);
        expect(serviceText('1 桌 · 博丽灵梦'), findsOneWidget);
        expect(serviceText('0 桌 · 博丽灵梦'), findsNothing);
        final listScroll = find
            .descendant(
              of: find
                  .descendant(
                    of: find.byType(ServicePage),
                    matching: find.byType(ListView),
                  )
                  .first,
              matching: find.byType(Scrollable),
            )
            .first;
        for (final desk in [0, 7]) {
          final row = orderRow('rare', desk);
          // 大字号长卡片下ListView只创建视口附近的行；通过真实滚动触达下一桌，
          // 不要求屏外行预先创建，也不能把关闭惰性加载当作可浏览性的修复。
          if (row.evaluate().isEmpty) {
            await tester.scrollUntilVisible(row, 250, scrollable: listScroll);
            await tester.pumpAndSettle();
          }
          expect(
            find.descendant(
              of: row,
              matching: find.text(desk == 0 ? '1 桌 · 博丽灵梦' : '8 桌 · 雾雨魔理沙'),
            ),
            findsOneWidget,
          );
          final rowRect = tester.getRect(row);
          final heading = find.descendant(
            of: row,
            matching: find.byType(ServiceOrderHeading),
          );
          final headingTitle = find.descendant(
            of: heading,
            matching: find.text(desk == 0 ? '1 桌 · 博丽灵梦' : '8 桌 · 雾雨魔理沙'),
          );
          final served = find.descendant(
            of: heading,
            matching: find.text('已有酒水'),
          );
          expect(served, findsOneWidget);
          expect(
            tester.getRect(served).left,
            greaterThan(tester.getRect(headingTitle).right),
          );
          expect(
            tester.getRect(served).right,
            lessThanOrEqualTo(rowRect.right),
          );
          // 进度属于标题右列，不在配方摘要底部再复制一遍。
          expect(
            find.descendant(
              of: find.descendant(
                of: row,
                matching: find.byType(ServiceOrderSummary),
              ),
              matching: find.text('已有酒水'),
            ),
            findsNothing,
          );
          for (final text in [
            '料理：清淡',
            '酒水：无酒精',
            '海鲜味噌汤',
            '煮锅',
            '豆腐 · 库存未知',
            '海苔 · 库存未知',
            '蘑菇 · 库存 3',
            '冰山毛玉冻柠',
          ]) {
            final line = find.descendant(of: row, matching: find.text(text));
            expect(line, findsOneWidget);
            final rect = tester.getRect(line);
            // 摘要随内容自然增高并可滚动，不给尾部酒水或配方设总行数截断。
            expect(rect.top, greaterThanOrEqualTo(rowRect.top));
            expect(rect.bottom, lessThanOrEqualTo(rowRect.bottom));
            expect(rect.right, lessThanOrEqualTo(rowRect.right));
          }
          final beverage = find.descendant(
            of: row,
            matching: find.text('冰山毛玉冻柠'),
          );
          await tester.ensureVisible(beverage);
          await tester.pumpAndSettle();
          expect(beverage.hitTestable(), findsOneWidget);
        }
        expect(serviceText('不属于主方案的候选酒水'), findsNothing);
        await tester.tap(serviceText('普客'));
        await tester.pumpAndSettle();
        expect(serviceText('1 桌 · 人类村民'), findsOneWidget);
        // 大字号下首张完整卡片可能已超过首屏，第二张由ListView按需构建。
        // 先通过实际列表滚动使其进入视口，不能假定屏外条目始终存在于元素树。
        final serviceList = find
            .descendant(
              of: find.byType(ServicePage),
              matching: find.byType(ListView),
            )
            .first;
        await tester.scrollUntilVisible(
          serviceText('8 桌 · 妖精'),
          240,
          scrollable: find
              .descendant(of: serviceList, matching: find.byType(Scrollable))
              .first,
        );
        await tester.pumpAndSettle();
        expect(serviceText('8 桌 · 妖精'), findsOneWidget);
        if (width == 1280) {
          expect(serviceText('人类村民 · 1 桌'), findsOneWidget);
          // 外层材料与库存不再截断，150%字号时第二张卡片可低于首屏；通过真实滚动
          // 到达该卡片再选择，不能用关闭命中检查掩盖用户无法点击的问题。
          await tester.ensureVisible(orderRow('normal', 7));
          await tester.pumpAndSettle();
          await tester.tap(orderRow('normal', 7));
          await tester.pumpAndSettle();
          expect(serviceText('妖精 · 8 桌'), findsOneWidget);
        }
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
      });
    }
  }

  testWidgets('稀客普客与诊断都按显示桌号筛选，诊断不重复加一', (tester) async {
    final model = _ServiceModel();
    addTearDown(model.dispose);
    await open(tester, model);
    for (final kind in ['稀客', '普客']) {
      await tester.tap(serviceText(kind));
      await tester.enterText(filter(), '8');
      await tester.pumpAndSettle();
      expect(orderRow(kind == '稀客' ? 'rare' : 'normal', 7), findsOneWidget);
      expect(orderRow(kind == '稀客' ? 'rare' : 'normal', 0), findsNothing);
      await tester.enterText(filter(), '7');
      await tester.pumpAndSettle();
      expect(find.byType(DenseListDetail).first, findsOneWidget);
      expect(orderRow(kind == '稀客' ? 'rare' : 'normal', 7), findsNothing);
      await tester.enterText(filter(), '1');
      await tester.pumpAndSettle();
      expect(orderRow(kind == '稀客' ? 'rare' : 'normal', 0), findsOneWidget);
    }
    await tester.tap(serviceText('自动化状态'));
    await tester.pumpAndSettle();
    expect(
      find.descendant(
        of: find.byKey(const ValueKey('normal:0|100|200')),
        matching: find.text('桌 1 · 海鲜味噌汤'),
      ),
      findsOneWidget,
    );
    await tester.enterText(filter(), '8');
    await tester.tap(serviceText('稀客'));
    await tester.pumpAndSettle();
    expect(
      find.descendant(
        of: find.byKey(const ValueKey('rare:7|991|1|2')),
        matching: find.text('雾雨魔理沙 · 桌 8'),
      ),
      findsOneWidget,
    );
    expect(serviceText('雾雨魔理沙 · 桌 9'), findsNothing);
    await tester.tap(serviceText('重试暂停订单'));
    await tester.pumpAndSettle();
    final retry = model.writes.single;
    expect(retry.$1, 'retry');
    expect(retry.$2, {'kind': 'rare', 'key': 'rare:7|991|1|2'});
    expect(retry.$3, isNull);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('窄屏无主方案仍外显阻断原因，不从独立候选伪造酒水', (tester) async {
    final model = _ServiceModel(touch: true);
    addTearDown(model.dispose);
    final item = records(model.recommendations['recommendations']).first;
    item['executionPlans'] = <DataMap>[];
    item['blockedMessages'] = ['所需食材不足，等待补充'];
    await open(tester, model, width: 390, scale: 150);
    final row = orderRow('rare', 0);
    final rowRect = tester.getRect(row);
    for (final text in ['料理：清淡', '酒水：无酒精', '暂无可执行主方案', '阻断 · 所需食材不足，等待补充']) {
      final line = find.descendant(of: row, matching: find.text(text));
      expect(line, findsOneWidget);
      expect(tester.getRect(line).bottom, lessThanOrEqualTo(rowRect.bottom));
      expect(tester.getRect(line).bottom, lessThan(920));
    }
    expect(serviceText('不属于主方案的候选酒水'), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final desk in [0, 7]) {
    testWidgets('稀客详情及确认显示${desk + 1}桌，清缓存载荷仍发送原码$desk', (tester) async {
      final model = _ServiceModel();
      addTearDown(model.dispose);
      await open(tester, model);
      await tester.tap(orderRow('rare', desk));
      await tester.pumpAndSettle();
      final guest = desk == 0 ? '博丽灵梦' : '雾雨魔理沙';
      expect(serviceText('$guest · ${desk + 1} 桌'), findsOneWidget);
      await tester.ensureVisible(serviceText('高级诊断 · 订单捕获'));
      await tester.tap(serviceText('高级诊断 · 订单捕获'));
      await tester.pumpAndSettle();
      await tester.ensureVisible(serviceText('清除此捕获缓存'));
      await tester.tap(serviceText('清除此捕获缓存'));
      await tester.pumpAndSettle();
      expect(
        find.text('${desk + 1} 桌 · $guest。确认游戏现场与此残留记录不一致后再清除。'),
        findsOneWidget,
      );
      await tester.tap(find.text('确认'));
      await tester.pumpAndSettle();
      final dismiss = model.writes.single;
      expect(dismiss.$1, '/orders/rare/dismiss');
      expect(dismiss.$2, {
        'deskCode': '$desk',
        'runtimeGuestId': '991',
        'foodTagId': '1',
        'beverageTagId': '2',
      });
      expect(dismiss.$3, isNull);
      expect(
        object(
          records(model.recommendations['recommendations']).first['order'],
        )['deskCode'],
        0,
      );
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('外层共用菜品主体与彩色材料分组，普客原订单执行方案和送达状态清楚区分', (tester) async {
    final model = _ServiceModel();
    addTearDown(model.dispose);
    final normal = records(model.recommendations['normalOrderDetailPlans'])
        .first;
    normal['order'] = {
      ...object(normal['order']),
      'hasServedFood': false,
      'hasServedBeverage': true,
      'readyToEvaluate': true,
      'hasEvaluated': false,
      'canAutomate': false,
      'actionBlockReason': '特殊经营等待确认',
    };
    await open(tester, model);
    final rareRow = orderRow('rare', 0);
    for (final entry in {
      '豆腐 · 库存未知': OrderTagKind.ingredient,
      '蘑菇 · 库存 3': OrderTagKind.extra,
      '煮锅': OrderTagKind.cooker,
      '料理：清淡': OrderTagKind.request,
    }.entries) {
      expect(orderTag(rareRow, entry.key, entry.value), findsOneWidget);
    }
    // 料理/酒水名称各出现一次，主体使用相同公共组件；不再为标题额外叠一层名称标签。
    final menuItems = tester
        .widgetList<MenuItemContent>(
          find.descendant(of: rareRow, matching: find.byType(MenuItemContent)),
        )
        .map((widget) => (widget.item.kind, widget.item.name))
        .toList();
    expect(menuItems, [
      (MenuItemKind.food, '海鲜味噌汤'),
      (MenuItemKind.beverage, '冰山毛玉冻柠'),
    ]);
    // 此夹具仅酒水有库存指标，料理未提供标价或成本；不能为缺失数值伪造指标行。
    expect(
      find.descendant(of: rareRow, matching: find.text('指标')),
      findsOneWidget,
    );
    expect(
      find.descendant(of: rareRow, matching: find.text('主方案')),
      findsNothing,
    );
    // 分隔线取代重复标题；第一条位于点单与料理之间，第二条只分隔同一plan的
    // 料理和酒水。校验实际几何顺序，避免只增加不可见Divider便宣称展示已修复。
    final dividers = find.descendant(
      of: rareRow,
      matching: find.byType(MenuItemDivider),
    );
    final bodies = find.descendant(
      of: rareRow,
      matching: find.byType(MenuItemContent),
    );
    expect(dividers, findsNWidgets(2));
    expect(
      tester.getRect(dividers.at(0)).bottom,
      lessThanOrEqualTo(tester.getRect(bodies.at(0)).top),
    );
    expect(
      tester.getRect(dividers.at(1)).top,
      greaterThanOrEqualTo(tester.getRect(bodies.at(0)).bottom),
    );
    expect(
      tester.getRect(dividers.at(1)).bottom,
      lessThanOrEqualTo(tester.getRect(bodies.at(1)).top),
    );
    expect(serviceText('主方案 · 完整满足'), findsNothing);
    expect(serviceText('完整满足'), findsOneWidget);
    expect(
      find.descendant(of: rareRow, matching: find.text('配方')),
      findsOneWidget,
    );
    expect(
      find.descendant(of: rareRow, matching: find.text('加料')),
      findsOneWidget,
    );
    await tester.tap(serviceText('普客'));
    await tester.pumpAndSettle();
    final row = orderRow('normal', 0);
    for (final text in [
      '原料理：饭团',
      '原酒水：绿茶',
      '执行方案',
      '待送料理',
      '已有酒水',
      '待评价',
      '暂不可自动处理',
    ]) {
      expect(
        find.descendant(of: row, matching: find.text(text)),
        findsOneWidget,
      );
    }
    final details = find.byWidgetPredicate(
      (widget) => widget is ServiceNormalPlanContent && widget.details,
    );
    for (final text in ['按特殊经营规则替换', '当前库存满足主方案', '已抵消 重油', '已抵消 高酒精']) {
      expect(
        find.descendant(of: details, matching: find.text(text)),
        findsOneWidget,
      );
    }
    // 目标改为独立字段前缀，仍核实标签属于目标组而不是其它生效/目录行。
    final targetGroup = find.descendant(
      of: details,
      matching: find.byWidgetPredicate(
        (widget) => widget is MenuTagGroup && widget.label == '目标',
      ),
    );
    expect(
      find.descendant(of: targetGroup, matching: find.text('家常')),
      findsOneWidget,
    );
    expect(serviceText('特殊经营等待确认'), findsOneWidget);
    final status = find.byWidgetPredicate(
      (widget) =>
          widget is ServiceOrderStatus && !widget.rare && widget.details,
    );
    final original = find.byWidgetPredicate(
      (widget) => widget is Section && widget.title == '原订单',
    );
    // 原订单与替换执行方案使用完全不同编号，外层与详情必须分别沿用原始身份。
    for (final area in [row, original]) {
      final sourceTags = find.descendant(
        of: area,
        matching: find.byType(ServiceOriginalOrderTag),
      );
      final images = tester.widgetList<EntityImage>(
        find.descendant(of: sourceTags, matching: find.byType(EntityImage)),
      );
      expect(images.map((image) => (image.kind, image.id)), [
        (EntityImageKind.food, 71),
        (EntityImageKind.beverage, 72),
      ]);
    }
    // 短业务附注现在与原点单共用标签带，避免免费等附注独占一行；完整阻断原因
    // 仍在短标签下方保留说明间距，不能缩回原点单文字里或删掉原因以节省空间。
    expect(find.descendant(of: original, matching: status), findsOneWidget);
    final originalTags = find.descendant(
      of: original,
      matching: find.byType(ServiceOriginalOrderTag),
    );
    final reason = find.descendant(of: status, matching: find.text('特殊经营等待确认'));
    expect(
      tester.getRect(reason).top - tester.getRect(originalTags.last).bottom,
      greaterThanOrEqualTo(5),
    );
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('外层库存明确标注且仅名称唯一时联结，未知负数不伪造零或配方用量', (tester) async {
    final model = _ServiceModel();
    addTearDown(model.dispose);
    model.catalog['ingredients'] = [
      {'id': 10, 'name': '豆腐'},
      {'id': 11, 'name': '豆腐'},
      {'id': 5, 'name': '海苔'},
      {'id': 2, 'name': '蘑菇'},
    ];
    object(model.snapshot['recommendationState'])['ownedIngredientQty'] = {
      '10': 99,
      '11': 4,
      '5': 0,
      '2': -1,
    };
    object(model.snapshot['recommendationState'])['ownedBeverageQty'] =
        <String, Object?>{};
    await open(tester, model);
    final detail = orderRow('rare', 0);
    for (final text in ['豆腐 · 库存未知', '海苔 · 库存 0', '蘑菇 · 库存无限', '库存 未知']) {
      expect(
        find.descendant(of: detail, matching: find.text(text)),
        findsOneWidget,
      );
    }
    expect(find.textContaining('×'), findsNothing);
    expect(find.textContaining('库存 -1'), findsNothing);
    // -1 是服务端定义的无限库存；其他负数仍未知，两个类别均不能把它印成配方用量。
    object(model.snapshot['recommendationState'])['ownedIngredientQty'] = {
      '2': -2,
    };
    object(model.snapshot['recommendationState'])['ownedBeverageQty'] = {
      '3': -1,
    };
    model.publish();
    await tester.pumpAndSettle();
    expect(
      find.descendant(of: detail, matching: find.text('蘑菇 · 库存未知')),
      findsOneWidget,
    );
    expect(
      find.descendant(of: detail, matching: find.text('库存 无限')),
      findsOneWidget,
    );
    expect(find.textContaining('库存 -2'), findsNothing);
    expect(model.writes, isEmpty);
  });

  testWidgets('稀客预算直接展示服务端policy与overBudget，不在界面重算或以fund代替', (tester) async {
    final model = _ServiceModel();
    addTearDown(model.dispose);
    final item = records(model.recommendations['recommendations']).first;
    object(item['order'])['fund'] = 9999;
    await open(tester, model);
    for (final scenario in [
      (
        {
          'policy': 'warn',
          'estimatedPrice': 100,
          'remainingBudget': 500,
          'overBudget': 0,
          'willPayMoney': false,
        },
        '预估 100 · 当前不会付款',
      ),
      (
        {
          'policy': 'ignore',
          'estimatedPrice': 100,
          'remainingBudget': 500,
          'overBudget': 0,
          'willPayMoney': false,
        },
        '预估 100',
      ),
      (
        {
          'policy': 'block',
          'estimatedPrice': 100,
          'remainingBudget': 10,
          'overBudget': 0,
        },
        '预估 100 / 预算 10',
      ),
      (
        {
          'policy': 'warn',
          'estimatedPrice': 100,
          'remainingBudget': 10,
          'overBudget': 90,
        },
        '预估 100 · 超预算 90',
      ),
      (
        {
          'policy': 'ignore',
          'estimatedPrice': 100,
          'remainingBudget': 10,
          'overBudget': 90,
        },
        '预估 100',
      ),
      (
        {
          'policy': 'block',
          'estimatedPrice': 100,
          'remainingBudget': null,
          'overBudget': 0,
        },
        '预估 100 · 预算未知',
      ),
    ]) {
      item['budget'] = scenario.$1;
      model.publish();
      await tester.pumpAndSettle();
      expect(
        find.descendant(
          of: orderRow('rare', 0),
          matching: find.text(scenario.$2),
        ),
        findsOneWidget,
      );
    }
    expect(find.textContaining('9999'), findsNothing);
  });

  for (final reconnect in [false, true]) {
    testWidgets('清捕获确认期间${reconnect ? '连接变化拒绝提交' : '同一Map更新仍使用已渲染原ID'}', (
      tester,
    ) async {
      final model = _ServiceModel();
      addTearDown(model.dispose);
      await open(tester, model);
      await tester.ensureVisible(serviceText('高级诊断 · 订单捕获'));
      await tester.tap(serviceText('高级诊断 · 订单捕获'));
      await tester.pumpAndSettle();
      await tester.ensureVisible(serviceText('清除此捕获缓存'));
      await tester.tap(serviceText('清除此捕获缓存'));
      await tester.pumpAndSettle();
      if (reconnect) {
        model.connectionVersion++;
      } else {
        final order = object(
          records(model.recommendations['recommendations']).first['order'],
        );
        order['deskCode'] = 7;
        order['runtimeGuestId'] = 1234;
        order['foodTagId'] = 99;
        order['beverageTagId'] = 98;
      }
      await tester.tap(find.text('确认'));
      await tester.pumpAndSettle();
      if (reconnect) {
        expect(model.writes, isEmpty);
      } else {
        expect(model.writes.single.$2, {
          'deskCode': '0',
          'runtimeGuestId': '991',
          'foodTagId': '1',
          'beverageTagId': '2',
        });
      }
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('异常桌号保留负数哨兵或未知标记，不伪造成第一桌', (tester) async {
    final model = _ServiceModel();
    addTearDown(model.dispose);
    final items = records(model.recommendations['recommendations']);
    for (final value in <Object?>[-1, -9, null, '未知', 1.5]) {
      items[0]['order'] = {...object(items[0]['order']), 'deskCode': value};
      await open(tester, model);
      expect(serviceText('${label(value)} 桌 · 博丽灵梦'), findsOneWidget);
      expect(serviceText('1 桌 · 博丽灵梦'), findsNothing);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    }
  });
}
