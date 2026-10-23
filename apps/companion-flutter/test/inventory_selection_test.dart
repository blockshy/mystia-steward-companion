import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/tools.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/inventory_item.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 使用明确的目录编号和纯内存写入记录验证库存全选；不读取真实库存或建立连接。
/// 食材与酒水故意复用编号1，证明切换类型后不能误用上一类型的本地选择。
class _InventoryModel extends FixtureModel {
  _InventoryModel({super.desktop}) {
    localPreferences = {'inventoryEditorModuleEnabled': true};
    catalog = {
      'ingredients': <DataMap>[
        {'id': 1, 'name': 'A蘑菇'},
        {'id': 2, 'name': 'B蘑菇'},
        {'id': 3, 'name': 'C豆腐'},
      ],
      'beverages': <DataMap>[
        {'id': 1, 'name': '合成茶'},
        {'id': 9, 'name': '合成水'},
      ],
    };
    snapshot['recommendationState'] = {
      'inventoryReadReady': true,
      'ownedIngredientQty': {'1': 3, '2': 4, '3': 0},
      'ownedBeverageQty': {'1': 2, '9': 5},
    };
  }
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

  final all = find.byKey(const ValueKey('inventory-select-all'));
  Finder allCheckbox() =>
      find.descendant(of: all, matching: find.byType(FCheckbox));
  Finder row(String name) => find.descendant(
    of: find.ancestor(
      of: find.text(name),
      matching: find.byType(InventoryItem),
    ),
    matching: find.byType(FCheckbox),
  );
  Finder search() => find.descendant(
    of: find.byWidgetPredicate(
      (widget) => widget is StewardField && widget.title == '筛选名称',
    ),
    matching: find.byType(EditableText),
  );

  Future<void> tap(WidgetTester tester, Finder target) async {
    await tester.ensureVisible(target);
    await tester.pumpAndSettle();
    await tester.tap(target);
    await tester.pumpAndSettle();
  }

  Future<ValueNotifier<bool>> open(
    WidgetTester tester,
    _InventoryModel model, {
    double width = 800,
    double scale = 1,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 900);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(model.dispose);
    final active = ValueNotifier(true);
    addTearDown(active.dispose);
    final theme = stewardTheme(dark: width < 640, touch: !model.desktop);
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
          // 与正式桌面80px侧栏+24px正文间距、手机16px正文间距保持一致。
          padding: EdgeInsets.fromLTRB(
            model.desktop ? 104 : 16,
            12,
            model.desktop ? 24 : 16,
            12,
          ),
          child: ListenableBuilder(
            listenable: Listenable.merge([model, active]),
            builder: (_, _) => ToolsPage(model, active: active.value),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tap(tester, find.text('库存修改'));
    return active;
  }

  testWidgets('全选仅作用当前筛选，部分选择可补齐，筛选外数量与确认载荷明确', (tester) async {
    final model = _InventoryModel();
    await open(tester, model);
    final semantics = tester.ensureSemantics();
    try {
      await tap(tester, row('C豆腐'));
      expect(tester.getSemantics(all), isSemantics(isCheckStateMixed: true));
      await tester.enterText(search(), '蘑菇');
      await tester.pumpAndSettle();
      expect(find.text('筛选外 1 项'), findsOneWidget);
      expect(tester.widget<FCheckbox>(allCheckbox()).value, isFalse);
      await tap(tester, allCheckbox());
      expect(find.text('已选 3 项'), findsOneWidget);
      expect(
        tester.getSemantics(all),
        isSemantics(hasCheckedState: true, isChecked: true),
      );
      await tap(tester, allCheckbox());
      expect(find.text('已选 1 项'), findsOneWidget, reason: '清选当前范围不能删除筛选外已选豆腐');
      expect(find.text('筛选外 1 项'), findsOneWidget);
      await tap(tester, row('A蘑菇'));
      expect(tester.getSemantics(all), isSemantics(isCheckStateMixed: true));
      await tap(tester, allCheckbox());
      expect(find.text('已选 3 项'), findsOneWidget);
      await tap(tester, find.text('设置已选 3 项'));
      expect(find.textContaining('其中包含当前筛选外 1 项'), findsOneWidget);
      await tap(tester, find.text('取消'));
      expect(model.writes, isEmpty);
      await tap(tester, find.text('设置已选 3 项'));
      await tap(tester, find.text('确认'));
      expect(model.writes.single.$1, '/inventory/bulk-set');
      expect(model.writes.single.$2, {
        'type': 'ingredient',
        'ids': '1,2,3',
        'qty': '99',
      });
      expect(tester.takeException(), isNull);
    } finally {
      semantics.dispose();
    }
  });

  testWidgets('无限和未知库存只读，全选只提交有限库存，零库存仍可补货', (tester) async {
    final model = _InventoryModel();
    model.snapshot['recommendationState'] = {
      'inventoryReadReady': true,
      'ownedIngredientQty': {'1': -1, '3': 0},
    };
    await open(tester, model);
    expect(tester.widget<FCheckbox>(row('A蘑菇')).enabled, isFalse);
    expect(tester.widget<FCheckbox>(row('B蘑菇')).enabled, isFalse);
    expect(find.text('无限库存，不能修改数量。'), findsOneWidget);
    expect(find.text('库存数量未知，读取到有效数量后才能修改。'), findsOneWidget);
    await tap(tester, allCheckbox());
    expect(find.text('已选 1 项'), findsOneWidget);
    await tap(tester, find.text('设置已选 1 项'));
    await tap(tester, find.text('确认'));
    expect(model.writes.single.$2['ids'], '3');
  });

  for (final ready in <bool?>[false, null]) {
    testWidgets('业务兜底99在实读标记 $ready 时不显示为真实库存或允许全选', (tester) async {
      final model = _InventoryModel();
      model.snapshot['runtimeLoaded'] = true;
      model.snapshot['runtimeDataComplete'] = true;
      model.snapshot['recommendationState'] = {
        if (ready != null) 'inventoryReadReady': ready,
        'ownedIngredientQty': {'1': 99, '2': 99, '3': 99},
        'availableIngredientIds': [1, 2, 3],
      };
      await open(tester, model);
      expect(tester.widget<FCheckbox>(allCheckbox()).enabled, isFalse);
      expect(tester.widget<FCheckbox>(row('A蘑菇')).enabled, isFalse);
      expect(find.text('库存 99'), findsNothing);
      expect(find.text('库存 未知'), findsNWidgets(3));
      expect(model.writes, isEmpty);
    });
  }

  for (final next in <Object?>[-1, null, -2, 1.5]) {
    testWidgets('确认期间库存变为 $next 时不发送旧数量写入', (tester) async {
      final model = _InventoryModel();
      await open(tester, model);
      await tap(tester, row('A蘑菇'));
      await tap(tester, find.text('设置已选 1 项'));
      model.snapshot['recommendationState'] = {
        'inventoryReadReady': true,
        'ownedIngredientQty': {'1': next, '2': 4, '3': 0},
      };
      model.publish();
      await tester.pumpAndSettle();
      await tap(tester, find.text('确认'));
      expect(model.writes, isEmpty);
      expect(tester.widget<FCheckbox>(row('A蘑菇')).value, isFalse);
    });
  }

  testWidgets('完整稀疏快照可修改真实零库存，未加载与矛盾集合不补造零', (tester) async {
    final model = _InventoryModel();
    model.snapshot['runtimeLoaded'] = true;
    model.snapshot['runtimeDataComplete'] = true;
    model.snapshot['recommendationState'] = {
      'inventoryReadReady': true,
      'ownedIngredientQty': {'1': -1, '2': 2},
      'availableIngredientIds': [1, 2],
    };
    await open(tester, model);
    expect(tester.widget<FCheckbox>(row('C豆腐')).enabled, isTrue);
    await tap(tester, row('C豆腐'));
    model.snapshot['runtimeLoaded'] = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(tester.widget<FCheckbox>(row('C豆腐')).enabled, isFalse);
    expect(find.text('已选 1 项'), findsNothing);
    model.snapshot['runtimeLoaded'] = true;
    model.snapshot['recommendationState'] = {
      'inventoryReadReady': true,
      'ownedIngredientQty': {'1': -1, '2': 2},
      'availableIngredientIds': [1, 2, 3],
    };
    model.publish();
    await tester.pumpAndSettle();
    expect(tester.widget<FCheckbox>(row('C豆腐')).enabled, isFalse);
    expect(model.writes, isEmpty);
  });

  testWidgets('完整目录的重复或非法编号不可选择，刷新只保留仍唯一的已选编号', (tester) async {
    final model = _InventoryModel();
    model.catalog['ingredients'] = <DataMap>[
      ...model.catalogItems('ingredients'),
      {'id': 2, 'name': 'D隐藏重复'},
      {'id': -1, 'name': 'E非法编号'},
      {'name': 'F缺失编号'},
    ];
    await open(tester, model);
    await tester.enterText(search(), '蘑菇');
    await tester.pumpAndSettle();
    expect(
      tester.widget<FCheckbox>(row('B蘑菇')).enabled,
      isFalse,
      reason: '筛选外的重复编号也必须使当前行不可选',
    );
    await tap(tester, allCheckbox());
    expect(find.text('已选 1 项'), findsOneWidget);
    model.catalog['ingredients'] = <DataMap>[
      {'id': 3, 'name': 'C豆腐'},
      {'id': 4, 'name': '新蘑菇'},
    ];
    model.snapshot['recommendationState'] = {
      'inventoryReadReady': true,
      'ownedIngredientQty': {'3': 0, '4': 6},
    };
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('已选 1 项'), findsNothing);
    expect(tester.widget<FCheckbox>(row('新蘑菇')).value, isFalse);
    await tap(tester, allCheckbox());
    await tap(tester, find.text('设置已选 1 项'));
    await tap(tester, find.text('确认'));
    expect(model.writes.single.$2['ids'], '4');
    await tester.enterText(search(), '不存在');
    await tester.pumpAndSettle();
    expect(tester.widget<FCheckbox>(allCheckbox()).enabled, isFalse);
    expect(find.text('筛选外 1 项'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  for (final change in [
    'filter',
    'type',
    'catalog',
    'authority',
    'disabled',
    'hidden',
    'busy',
  ]) {
    testWidgets('旧全选及单选回调在 $change 变化后不改变选择或发起写入', (tester) async {
      final model = _InventoryModel();
      final active = await open(tester, model);
      final oldAll = tester.widget<FCheckbox>(allCheckbox()).onChange!;
      final oldRow = tester.widget<FCheckbox>(row('A蘑菇')).onChange!;
      switch (change) {
        case 'filter':
          await tester.enterText(search(), '蘑菇');
        case 'type':
          await tap(tester, find.text('酒水'));
        case 'catalog':
          model.catalog['ingredients'] = <DataMap>[
            ...model.catalogItems('ingredients'),
            {'id': 4, 'name': '新增目录行'},
          ];
        case 'authority':
          model.authority = {
            ...model.authority,
            'currentDeviceIsPrimary': false,
          };
        case 'disabled':
          model.localPreferences['inventoryEditorModuleEnabled'] = false;
        case 'hidden':
          active.value = false;
        case 'busy':
          model.busy = true;
      }
      model.publish();
      await tester.pumpAndSettle();
      oldAll(true);
      oldRow(true);
      await tester.pumpAndSettle();
      expect(find.textContaining('已选 '), findsNothing);
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('查看设备允许本地全选但不开放库存写入，类型切换清除跨类型编号', (tester) async {
    final model = _InventoryModel();
    model.authority = {...model.authority, 'currentDeviceIsPrimary': false};
    await open(tester, model);
    await tap(tester, allCheckbox());
    final edit = find.widgetWithText(StewardButton, '设置已选 3 项');
    expect(tester.widget<StewardButton>(edit).onPressed, isNull);
    await tap(tester, find.text('酒水'));
    expect(find.text('已选 3 项'), findsNothing);
    expect(tester.widget<FCheckbox>(row('合成茶')).value, isFalse);
    await tap(tester, allCheckbox());
    expect(find.text('已选 2 项'), findsOneWidget);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('数量刷新改变排序位置时复用同一库存复选框，不丢勾选或重建动画', (tester) async {
    final model = _InventoryModel();
    await open(tester, model);
    await tap(tester, row('A蘑菇'));
    await tap(tester, find.byKey(const ValueKey('inventory-sort-quantity')));
    final item = find.byKey(const ValueKey('inventory-item-ingredient-1'));
    final element = tester.element(item);
    final checkboxElement = tester.element(row('A蘑菇'));
    final before = tester.getTopLeft(item).dy;
    model.snapshot['recommendationState'] = {
      'inventoryReadReady': true,
      'ownedIngredientQty': {'1': 8, '2': 4, '3': 0},
    };
    model.publish();
    await tester.pumpAndSettle();
    expect(tester.getTopLeft(item).dy, greaterThan(before));
    expect(identical(tester.element(item), element), isTrue);
    expect(identical(tester.element(row('A蘑菇')), checkboxElement), isTrue);
    expect(tester.widget<FCheckbox>(row('A蘑菇')).value, isTrue);
    expect(find.text('已选 1 项'), findsOneWidget);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  for (final duplicate in [false, true]) {
    testWidgets('确认期间目录${duplicate ? '新增歧义' : '移除已选'}后不发送库存写入', (tester) async {
      final model = _InventoryModel();
      await open(tester, model);
      await tap(tester, allCheckbox());
      await tap(tester, find.text('设置已选 3 项'));
      model.catalog['ingredients'] = <DataMap>[
        ...model
            .catalogItems('ingredients')
            .where((row) => duplicate || row['id'] != 1),
        if (duplicate) {'id': 1, 'name': '另一同编号条目'},
      ];
      model.publish();
      await tester.pumpAndSettle();
      await tap(tester, find.text('确认'));
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  for (final sample in [
    (360.0, false),
    (390.0, true),
    (640.0, true),
    (1280.0, true),
  ]) {
    testWidgets('库存全选在 ${sample.$1} / 150% 保留44px命中区、筛选外数量与列表', (tester) async {
      final model = _InventoryModel(desktop: sample.$2);
      await open(tester, model, width: sample.$1, scale: 1.5);
      final control = tester.getRect(allCheckbox());
      expect(control.width, greaterThanOrEqualTo(44));
      expect(control.height, greaterThanOrEqualTo(44));
      expect(allCheckbox().hitTestable(), findsOneWidget);
      await tap(tester, allCheckbox());
      await tester.enterText(search(), '蘑菇');
      await tester.pumpAndSettle();
      await tester.ensureVisible(find.text('筛选外 1 项'));
      expect(find.text('筛选外 1 项').hitTestable(), findsOneWidget);
      expect(
        tester.getSize(find.byKey(const ValueKey('inventory-items'))).height,
        greaterThan(100),
      );
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }
}
