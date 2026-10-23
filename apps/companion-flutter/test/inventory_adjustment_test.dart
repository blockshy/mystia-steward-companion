import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/extension_item_content.dart';
import 'package:mystia_steward_companion/features/tools.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/steward_test_fonts.dart';

/// 纯内存模型只模拟既有 mutate 的成功回读/失败反馈，不建立HTTP或读取游戏。
/// 同编号食材和酒水用于证明写入类型精确，Completer用于覆盖真实按钮同帧重复调用。
class _AdjustmentModel extends FixtureModel {
  _AdjustmentModel({super.desktop}) {
    localPreferences = {'inventoryEditorModuleEnabled': true};
    catalog = {
      'ingredients': <DataMap>[
        {
          'id': 1,
          'name': 'A精选山珍蘑菇与竹笋',
          'tags': ['山珍', '鲜', '保留完整合成目录标签'],
        },
        {'id': 2, 'name': 'B合成豆腐'},
      ],
      'beverages': <DataMap>[
        {'id': 1, 'name': '合成茶'},
      ],
    };
    snapshot['recommendationState'] = {
      'inventoryReadReady': true,
      'ownedIngredientQty': {'1': 30, '2': 5},
      'ownedBeverageQty': {'1': 20},
    };
    snapshot['automationSessionId'] = 'fixture-session';
    snapshot['nightBusinessGeneration'] = 3;
    authority = {
      ...authority,
      'registryId': 'fixture-registry',
      'authorityRevision': 7,
      'currentDeviceId': 'fixture-primary',
      'primaryDeviceId': 'fixture-primary',
    };
  }

  int epoch = 1;
  Completer<void>? pending;
  bool fail = false;
  @override
  int get connectionVersion => epoch;

  void replaceQuantity(Object? count, {String type = 'ingredient'}) {
    final field = type == 'ingredient'
        ? 'ownedIngredientQty'
        : 'ownedBeverageQty';
    snapshot['recommendationState'] = {
      ...runtime,
      field: {...object(runtime[field]), '1': count},
    };
  }

  @override
  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  }) async {
    writes.add((path, Map<String, String>.of(query), body));
    notice = '';
    if (pending != null) await pending!.future;
    if (fail) {
      error = '库存写入未确认，请刷新核对，不要重复提交。';
    } else {
      replaceQuantity(int.parse(query['qty']!), type: query['type']!);
      notice = '库存修改已确认，当前数量 ${query['qty']}。';
    }
    publish();
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(loadPreferredStewardTestFont);

  Finder item(int id, {String type = 'ingredient'}) =>
      find.byKey(ValueKey('inventory-item-$type-$id'));
  Finder checkbox(int id, {String type = 'ingredient'}) => find.descendant(
    of: item(id, type: type),
    matching: find.byType(FCheckbox),
  );
  Finder button(bool increase, {int id = 1, String type = 'ingredient'}) =>
      find.descendant(
        of: item(id, type: type),
        matching: find.byKey(
          ValueKey('inventory-adjust-${increase ? 'increase' : 'decrease'}'),
        ),
      );
  Future<void> tap(WidgetTester tester, Finder target) async {
    await tester.ensureVisible(target);
    await tester.pumpAndSettle();
    await tester.tap(target);
    await tester.pumpAndSettle();
  }

  Future<ValueNotifier<bool>> open(
    WidgetTester tester,
    _AdjustmentModel model, {
    double width = 800,
    double scale = 1,
    ValueNotifier<_AdjustmentModel>? replacement,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 1000);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(model.dispose);
    final active = ValueNotifier(true);
    addTearDown(active.dispose);
    final theme = stewardTheme(dark: !model.desktop, touch: !model.desktop);
    await tester.pumpWidget(
      material.MaterialApp(
        locale: const Locale('zh', 'CN'),
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
          child: ListenableBuilder(
            listenable: Listenable.merge([
              model,
              active,
              if (replacement != null) replacement,
            ]),
            builder: (_, _) =>
                ToolsPage(replacement?.value ?? model, active: active.value),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tap(tester, find.text('库存修改'));
    return active;
  }

  test('快捷调整只接受精确整数±10，越界不夹值，未知和无限不可写', () {
    expect(inventoryAdjustmentTarget(10, -10), 0);
    expect(inventoryAdjustmentTarget(0, 10), 10);
    expect(inventoryAdjustmentTarget(9989, 10), 9999);
    expect(inventoryAdjustmentTarget(9999, -10), 9989);
    for (final value in <Object?>[null, -1, -2, 3.5, '30', double.infinity]) {
      expect(inventoryAdjustmentTarget(value, 10), isNull);
      expect(inventoryAdjustmentTarget(value, -10), isNull);
    }
    expect(inventoryAdjustmentTarget(9, -10), isNull);
    expect(inventoryAdjustmentTarget(9990, 10), isNull);
    expect(inventoryAdjustmentTarget(30, 0), isNull);
    expect(inventoryAdjustmentTarget(30, 1), isNull);
  });

  testWidgets('单条按钮不夹带批量选择、不弹确认，按最新数量写入精确类型和ID', (tester) async {
    final model = _AdjustmentModel();
    await open(tester, model);
    await tap(tester, checkbox(2));
    final oldIncrease = tester.widget<FButton>(button(true)).onPress!;
    // 保持旧控件闭包，直接替换模型事实，证明目标值在点击时而非build时计算。
    model.replaceQuantity(40);
    oldIncrease();
    await tester.pumpAndSettle();
    expect(model.writes.single.$1, '/inventory/set');
    expect(model.writes.single.$2, {
      'type': 'ingredient',
      'id': '1',
      'qty': '50',
      'expectedQuantity': '40',
    });
    expect(find.text('确认'), findsNothing);
    expect(tester.widget<FCheckbox>(checkbox(1)).value, isFalse);
    expect(tester.widget<FCheckbox>(checkbox(2)).value, isTrue);
    await tap(tester, button(false));
    expect(model.writes.last.$2['qty'], '40');
    await tap(tester, find.text('酒水'));
    await tap(tester, button(true, type: 'beverage'));
    expect(model.writes.last.$2, {
      'type': 'beverage',
      'id': '1',
      'qty': '30',
      'expectedQuantity': '20',
    });
    expect(object(model.runtime['ownedIngredientQty'])['1'], 40);
    expect(tester.takeException(), isNull);
  });

  testWidgets('进行中同帧重复和其它行点击只产生一次写入，不乐观更新数量', (tester) async {
    final model = _AdjustmentModel()..pending = Completer<void>();
    await open(tester, model);
    final first = tester.widget<FButton>(button(true)).onPress!;
    final other = tester.widget<FButton>(button(true, id: 2)).onPress!;
    first();
    first();
    other();
    await tester.pump();
    expect(model.writes, hasLength(1));
    expect(find.text('库存 30'), findsOneWidget);
    expect(tester.widget<FButton>(button(true)).onPress, isNull);
    expect(tester.widget<FButton>(button(true, id: 2)).onPress, isNull);
    model.pending!.complete();
    await tester.pumpAndSettle();
    expect(find.text('库存 40'), findsOneWidget);
    expect(tester.widget<FButton>(button(true)).onPress, isNotNull);
  });

  testWidgets('失败由模型反馈且不刷新出伪成功或自动重放，批量草稿保留', (tester) async {
    final model = _AdjustmentModel()..fail = true;
    model.notice = '此前操作成功';
    await open(tester, model);
    await tap(tester, checkbox(1));
    await tap(tester, button(true));
    expect(model.writes, hasLength(1));
    expect(model.error, contains('写入未确认'));
    expect(model.notice, isEmpty);
    expect(find.text('库存 30'), findsOneWidget);
    expect(tester.widget<FCheckbox>(checkbox(1)).value, isTrue);
    await tester.pump(const Duration(seconds: 3));
    expect(model.writes, hasLength(1));
    expect(tester.takeException(), isNull);
  });

  testWidgets('批量确认不能跨同版本同目录的新模型提交', (tester) async {
    final old = _AdjustmentModel(), next = _AdjustmentModel();
    final replacement = ValueNotifier(old);
    addTearDown(next.dispose);
    addTearDown(replacement.dispose);
    await open(tester, old, replacement: replacement);
    await tap(tester, checkbox(1));
    await tap(tester, find.text('设置已选 1 项'));
    // 对话框仍持有旧模型；新模型故意使用相同数字版本与条目，不能只靠字段巧合。
    replacement.value = next;
    await tester.pumpAndSettle();
    await tap(tester, find.text('确认'));
    expect(old.writes, isEmpty);
    expect(next.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  for (final quantity in <Object?>[-1, -2, null, 3.5, 9990]) {
    testWidgets('旧增加回调在库存变为 $quantity 后重新拒绝，不提交旧目标', (tester) async {
      final model = _AdjustmentModel();
      await open(tester, model);
      final old = tester.widget<FButton>(button(true)).onPress!;
      model.replaceQuantity(quantity);
      old();
      await tester.pumpAndSettle();
      expect(model.writes, isEmpty);
    });
  }

  for (final change in [
    'connection',
    'authority',
    'session',
    'night',
    'day',
    'hidden',
    'disabled',
    'readonly',
    'busy',
    'catalog',
    'readiness',
    'type',
    'filter',
  ]) {
    testWidgets('单项旧回调遇到 $change 边界变化后不写入', (tester) async {
      final model = _AdjustmentModel();
      final active = await open(tester, model);
      final old = tester.widget<FButton>(button(true)).onPress!;
      switch (change) {
        case 'connection':
          model.epoch++;
        case 'authority':
          model.authority['authorityRevision'] = 8;
        case 'session':
          model.snapshot['automationSessionId'] = 'next-session';
        case 'night':
          model.snapshot['nightBusinessGeneration'] = 4;
        case 'day':
          model.snapshot['runtimeDaySceneGeneration'] = 2;
        case 'hidden':
          active.value = false;
          await tester.pump();
        case 'disabled':
          model.localPreferences['inventoryEditorModuleEnabled'] = false;
        case 'readonly':
          model.authority['currentDeviceIsPrimary'] = false;
        case 'busy':
          model.busy = true;
        case 'catalog':
          model.catalog['ingredients'] = <DataMap>[
            ...model.catalogItems('ingredients'),
            {'id': 1, 'name': '重复编号'},
          ];
        case 'readiness':
          model.snapshot['recommendationState'] = {
            ...model.runtime,
            'inventoryReadReady': false,
          };
        case 'type':
          await tap(tester, find.text('酒水'));
        case 'filter':
          final input = find.descendant(
            of: find.byWidgetPredicate(
              (widget) => widget is StewardField && widget.title == '筛选名称',
            ),
            matching: find.byType(EditableText),
          );
          await tester.enterText(input, '山珍');
      }
      old();
      await tester.pumpAndSettle();
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  for (final sample in [
    (360.0, false),
    (390.0, false),
    (624.0, true),
    (640.0, true),
    (1280.0, true),
  ]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('库存 ${sample.$1}px/$scale 选择框与全选同轴且整条居中，快捷按钮独立可达', (
        tester,
      ) async {
        final model = _AdjustmentModel(desktop: sample.$2);
        await open(tester, model, width: sample.$1, scale: scale);
        final header = find.descendant(
          of: find.byKey(const ValueKey('inventory-select-all')),
          matching: find.byType(FCheckbox),
        );
        final checkRect = tester.getRect(checkbox(1));
        final bodyRect = tester.getRect(
          find.descendant(
            of: item(1),
            matching: find.byKey(const ValueKey('inventory-item-body')),
          ),
        );
        expect(
          checkRect.center.dx,
          closeTo(tester.getRect(header).center.dx, .1),
        );
        expect(checkRect.center.dy, closeTo(bodyRect.center.dy, .1));
        expect(checkRect.width, greaterThanOrEqualTo(44));
        expect(checkRect.height, greaterThanOrEqualTo(44));
        for (final increase in [false, true]) {
          final rect = tester.getRect(button(increase));
          expect(rect.left, greaterThanOrEqualTo(bodyRect.left + 44));
          expect(rect.right, lessThanOrEqualTo(bodyRect.right));
          expect(rect.top, greaterThanOrEqualTo(bodyRect.top));
          expect(rect.bottom, lessThanOrEqualTo(bodyRect.bottom));
          expect(button(increase).hitTestable(), findsOneWidget);
        }
        await tap(tester, button(true));
        expect(model.writes.single.$2['qty'], '40');
        expect(tester.widget<FCheckbox>(checkbox(1)).value, isFalse);
        await tap(tester, checkbox(1));
        expect(tester.widget<FCheckbox>(checkbox(1)).value, isTrue);
        expect(model.writes, hasLength(1));
        expect(tester.takeException(), isNull);
      });
    }
  }
}
