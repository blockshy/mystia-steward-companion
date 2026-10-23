import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/overview.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/steward_test_fonts.dart';

/// 真实总览只消费内存快照。覆盖供给归组、四列几何与真实库存来源，不能把候选兜底99、
/// 缺字段或无限库存伪装成零库存；不访问游戏、网络、平台存储，也不触发任何写操作。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;

  setUpAll(() async {
    if (await loadPreferredStewardTestFont()) return;
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null && File(path).existsSync()) {
      final font = FontLoader('Microsoft YaHei')
        ..addFont(
          Future.value(ByteData.sublistView(File(path).readAsBytesSync())),
        );
      await font.load();
    }
  });

  Future<void> mount(
    WidgetTester tester,
    FixtureModel model, {
    double width = 640,
    double scale = 1,
  }) async {
    tester.view.physicalSize = Size(width, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final theme = stewardTheme(dark: false, touch: !model.desktop);
    await tester.pumpWidget(
      material.MaterialApp(
        localizationsDelegates: FLocalizations.localizationsDelegates,
        supportedLocales: FLocalizations.supportedLocales,
        theme: theme.toApproximateMaterialTheme(),
        builder: (_, child) => FTheme(
          data: theme,
          child: MediaQuery(
            data: MediaQueryData(
              size: Size(width, 900),
              textScaler: TextScaler.linear(scale),
            ),
            child: child!,
          ),
        ),
        home: Padding(
          // 与实际工作台边距一致：Windows含窄导航轨，Android只保留两侧正文边距。
          padding: model.desktop
              ? const EdgeInsets.fromLTRB(104, 12, 24, 12)
              : const EdgeInsets.all(16),
          child: ListenableBuilder(
            listenable: model,
            builder: (_, _) => OverviewPage(model),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  FixtureModel readyModel({bool desktop = true}) {
    final model = FixtureModel(desktop: desktop, orderCount: 0);
    model.snapshot['runtimeLoaded'] = true;
    model.snapshot['runtimeDataComplete'] = true;
    model.runtime['availableIngredientIds'] = [2];
    model.runtime['popularHateFoodTag'] = '重油';
    addTearDown(model.dispose);
    return model;
  }

  Finder cell(String type, String column, int row) =>
      find.byKey(ValueKey('overview-stock-$type-$column-$row'));

  String quantity(WidgetTester tester, String type, int row) => tester
      .widget<Text>(
        find.descendant(
          of: cell(type, 'quantity', row),
          matching: find.byType(Text),
        ),
      )
      .data!;

  Future<void> revealTable(WidgetTester tester) async {
    // Section包含完整表格，先把首个名称行移进视口。大字号的整表允许纵向滚动，
    // 不能要求整表同时塞进小屏，也不能以省略名称或缩小字体换取断言通过。
    await tester.scrollUntilVisible(
      cell('ingredient', 'name', 0),
      180,
      scrollable: find.byType(Scrollable).first,
    );
    await tester.pumpAndSettle();
  }

  testWidgets('总览保留场景供给，自动化和待确认事项集中到经营统计', (tester) async {
    final model = readyModel();
    model.business['automation'] = {
      'message': '执行中',
      'safetyBarriers': [
        {'title': '需确认现场结果', 'message': '核对原订单后再确认。'},
      ],
    };
    await mount(tester, model);
    for (final title in [
      '稀客订单',
      '普客订单',
      '待人工处理',
      '自动化配置',
      '自动化状态',
      '暂停原因',
      '待处理事项',
      '今日供给',
      '连接详情',
      '厨具详情',
    ]) {
      expect(find.text(title), findsNothing);
    }
    final state = find.byWidgetPredicate(
      (widget) => widget is Section && widget.title == '经营状态',
    );
    expect(
      find.descendant(of: state, matching: find.text('可选料理')),
      findsOneWidget,
    );
    expect(
      find.descendant(of: state, matching: find.text('可选酒水')),
      findsOneWidget,
    );
    expect(
      find.descendant(of: state, matching: find.text('清淡')),
      findsOneWidget,
    );
    expect(
      find.descendant(of: state, matching: find.text('重油')),
      findsOneWidget,
    );
    expect(find.text('核对原订单后再确认。'), findsNothing);
    expect(find.text('当前地点'), findsOneWidget);
    expect(find.text('游戏场景'), findsOneWidget);
    expect(model.writes, isEmpty);
    expect(model.reads, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('总览不再重复自动化暂停说明，也不更改原始原因与共享配置', (tester) async {
    final model = readyModel();
    // 协议原因码保持原值，界面只映射展示，不修改快照或触发写入。
    final originalProfile = Map<String, Object?>.of(model.profile);
    for (final reason in [
      'night-business-lifecycle-unavailable',
      'night-business-tutorial-active',
      'night-business-tutorial-state-unavailable',
      'future-unknown-reason',
    ]) {
      model.snapshot['nightBusinessAutomationBlockReason'] = reason;
      await mount(tester, model);
      expect(find.text('自动化配置'), findsNothing);
      expect(find.text('自动化状态'), findsNothing);
      expect(find.text('暂停原因'), findsNothing);
      expect(find.text(reason), findsNothing);
      expect(find.text('场景门禁'), findsNothing);
      expect(model.snapshot['nightBusinessAutomationBlockReason'], reason);
      expect(model.profile, originalProfile);
      expect(tester.takeException(), isNull);
    }
    model.snapshot.remove('nightBusinessAutomationBlockReason');
    await mount(tester, model);
    expect(find.text('暂停原因'), findsNothing);
    expect(model.writes, isEmpty);
  });

  for (final layout in [
    (1280.0, true),
    (640.0, true),
    (390.0, false),
    (360.0, false),
  ]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('低库存四列完整可读 ${layout.$1}/$scale', (tester) async {
        final model = readyModel(desktop: layout.$2);
        model.catalog['ingredients'] = [
          {'id': 2, 'name': '合成超长名称的菌菇食材'},
        ];
        model.catalog['beverages'] = [
          {'id': 3, 'name': '合成超长名称的无酒精酒水'},
        ];
        model.runtime['ownedIngredientQty'] = {'2': 12345};
        model.runtime['ownedBeverageQty'] = {'3': 23456};
        await mount(tester, model, width: layout.$1, scale: scale);
        await revealTable(tester);
        final table = tester.getRect(
          find.byKey(const ValueKey('overview-stock-table')),
        );
        final columns = [
          cell('ingredient', 'name', 0),
          cell('ingredient', 'quantity', 0),
          cell('beverage', 'name', 0),
          cell('beverage', 'quantity', 0),
        ].map(tester.getRect).toList();
        for (var column = 0; column < columns.length; column++) {
          expect(columns[column].left, greaterThanOrEqualTo(table.left));
          expect(columns[column].right, lessThanOrEqualTo(table.right + .01));
          expect(columns[column].width, greaterThan(0));
          expect(columns[column].top, closeTo(columns.first.top, .01));
          if (column > 0)
            expect(
              columns[column].left,
              greaterThanOrEqualTo(columns[column - 1].right - .01),
            );
        }
        for (final name in ['合成超长名称的菌菇食材', '合成超长名称的无酒精酒水']) {
          final text = tester.widget<Text>(find.text(name));
          expect(text.maxLines, isNull);
          expect(text.overflow, isNot(TextOverflow.ellipsis));
          final rect = tester.getRect(find.text(name));
          expect(rect.left, greaterThanOrEqualTo(table.left));
          expect(rect.right, lessThanOrEqualTo(table.right + .01));
          expect(rect.bottom, lessThanOrEqualTo(table.bottom + .01));
        }
        expect(quantity(tester, 'ingredient', 0), '12345');
        expect(quantity(tester, 'beverage', 0), '23456');
        expect(find.text('食材名称'), findsOneWidget);
        expect(find.text('酒水名称'), findsOneWidget);
        expect(tester.takeException(), isNull);
        expect(model.writes, isEmpty);
      });
    }
  }

  testWidgets('完整稀疏库存补真实零，各类型分别按数量及ID稳定取五项并排除无限', (tester) async {
    final model = readyModel();
    for (final key in ['ingredients', 'beverages']) {
      model.catalog[key] = [
        for (var id = 0; id < 8; id++) {'id': id, 'name': '$key-$id'},
      ];
    }
    model.runtime['availableIngredientIds'] = [1, 2, 3, 4, 5, 6, 7];
    model.runtime['ownedIngredientQty'] = {
      '1': 3,
      '2': -1,
      '3': 5,
      '4': 2,
      '6': 8,
      '5': 8,
      '7': 9,
    };
    model.runtime['availableBeverageIds'] = [0, 1, 2, 3, 4, 5, 6];
    model.runtime['ownedBeverageQty'] = {
      '0': -1,
      '1': 11,
      '2': 12,
      '3': 13,
      '4': 14,
      '5': 15,
      '6': 16,
    };
    await mount(tester, model);
    await revealTable(tester);
    final images = tester
        .widgetList<EntityImage>(find.byType(EntityImage))
        .toList();
    expect(
      images
          .where((image) => image.kind == EntityImageKind.ingredient)
          .map((image) => image.id),
      [0, 4, 1, 3, 5],
    );
    expect(
      images
          .where((image) => image.kind == EntityImageKind.beverage)
          .map((image) => image.id),
      [7, 1, 2, 3, 4],
    );
    expect(
      [for (var row = 0; row < 5; row++) quantity(tester, 'ingredient', row)],
      ['0', '2', '3', '5', '8'],
    );
    expect(
      [for (var row = 0; row < 5; row++) quantity(tester, 'beverage', row)],
      ['0', '11', '12', '13', '14'],
    );
    expect(cell('ingredient', 'name', 5), findsNothing);
    expect(cell('beverage', 'name', 5), findsNothing);
    expect(model.runtime['ownedIngredientQty'], isNot(contains('0')));
    expect(model.runtime['ownedBeverageQty'], isNot(contains('7')));
    expect(tester.takeException(), isNull);
  });

  testWidgets('未实读库存不显示候选99，恢复实读才显示，断线立即撤下旧数量', (tester) async {
    final model = readyModel();
    model.runtime['inventoryReadReady'] = false;
    model.runtime['ownedIngredientQty'] = {'2': 99};
    model.runtime['ownedBeverageQty'] = {'3': 99};
    await mount(tester, model);
    await revealTable(tester);
    expect(find.text('尚未读取库存'), findsNWidgets(2));
    expect(quantity(tester, 'ingredient', 0), '—');
    expect(quantity(tester, 'beverage', 0), '—');
    expect(find.text('99'), findsNothing);
    expect(find.byType(EntityImage), findsNothing);
    model.runtime['inventoryReadReady'] = true;
    model.publish();
    await tester.pumpAndSettle();
    expect(quantity(tester, 'ingredient', 0), '99');
    model.connected = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(quantity(tester, 'ingredient', 0), '—');
    expect(find.text('99'), findsNothing);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('缺少一类映射和不完整稀疏数据明确未知，不把负数分数或缺项补零', (tester) async {
    final model = readyModel();
    model.runtime.remove('ownedBeverageQty');
    model.runtime.remove('availableIngredientIds');
    model.catalog['ingredients'] = [
      for (var id = 0; id < 5; id++) {'id': id, 'name': '食材$id'},
    ];
    model.runtime['ownedIngredientQty'] = {
      '1': -1,
      '2': -2,
      '3': 1.5,
      '4': '0',
    };
    await mount(tester, model);
    await revealTable(tester);
    expect(find.text('库存数量未知'), findsOneWidget);
    expect(find.text('缺少库存数据'), findsOneWidget);
    expect(quantity(tester, 'ingredient', 0), '—');
    expect(quantity(tester, 'beverage', 0), '—');
    expect(find.byType(EntityImage), findsNothing);
    expect(find.text('食材部分数量未知，仅列出已确认的有限库存。'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('有实际数量但名称目录缺失时保留精确编号，缺少的对侧行不冒充零', (tester) async {
    final model = readyModel();
    model.runtime['ownedBeverageQty'] = {'123': 4, '124': 5};
    model.runtime['availableBeverageIds'] = [123, 124];
    model.catalog['beverages'] = <Map<String, Object?>>[];
    await mount(tester, model);
    await revealTable(tester);
    expect(find.text('编号 123'), findsOneWidget);
    expect(find.text('编号 124'), findsOneWidget);
    expect(quantity(tester, 'beverage', 0), '4');
    expect(quantity(tester, 'ingredient', 1), '—');
    final image = tester
        .widgetList<EntityImage>(find.byType(EntityImage))
        .firstWhere((image) => image.kind == EntityImageKind.beverage);
    expect(image.id, 123);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });
}
