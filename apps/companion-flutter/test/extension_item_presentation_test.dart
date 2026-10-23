import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/extension_item_content.dart';
import 'package:mystia_steward_companion/features/tools.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/inventory_item.dart';
import 'package:mystia_steward_companion/ui/semantic_tag.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 所有条目均为明确的合成 DTO；只验证展示、排序与写入边界，不连接真实游戏。
class _ToolsModel extends FixtureModel {
  _ToolsModel() {
    localPreferences = {
      'missionListModuleEnabled': true,
      'rareGuestInvitationModuleEnabled': true,
      'inventoryEditorModuleEnabled': true,
    };
    resources = {
      '/missions/tracked': {
        'status': '合成追踪任务',
        'missions': [
          {
            'label': 'synthetic-mission',
            'title': '合成长任务：准备神社宴会所需的各类料理与酒水',
            'receiverLabel': 'synthetic-receiver',
            'characterName': '合成角色',
            'sceneNames': ['合成神社', '合成竹林'],
            'presentationStatus': 'ready',
            'status': 'fulfilled',
            'conditionCount': 3,
            'completedConditionCount': null,
            'conditions': [
              {'description': '与合成角色交谈', 'completed': true},
              {'description': '交付三份合成料理', 'completed': false},
              {'description': '完成合成事件', 'completed': null},
            ],
          },
        ],
      },
      '/missions/available': {
        'status': '合成可接任务',
        'missions': [
          {
            'label': 'synthetic-available',
            'title': '合成可接任务标题',
            'receiverLabel': 'synthetic-receiver',
            'characterName': '合成角色',
            'sceneNames': ['合成神社'],
            'presentationStatus': 'ready',
            'activationMode': 'conditional',
            'activationStatus': 'available',
            'triggerKind': 'kizuna-checkpoint',
            'sourceTiming': 'after-performance',
            'activationHint': 'kizuna-ready',
          },
        ],
      },
      '/rare-guests/invitations': {
        'status': '合成邀请候选',
        'candidates': [
          {
            'id': 301,
            'name': '合成可邀请稀客',
            'kizunaLevel': 3,
            'sceneNames': ['合成神社', '合成竹林'],
            'isCurrentScene': true,
            'locationStatus': 'current-map',
            'status': 'available',
            'canInvite': true,
            'reason': '合成邀请条件已就绪',
          },
          {
            'id': 302,
            'name': '合成未知羁绊稀客',
            'kizunaLevel': -1,
            'sceneNames': <String>[],
            'isCurrentScene': false,
            'locationStatus': 'not-in-current-map',
            'status': 'kizuna-uninitialized',
            'canInvite': false,
            'reason': '合成羁绊尚未初始化',
          },
        ],
      },
    };
    catalog = {
      ...catalog,
      'ingredients': [
        {'id': 3, 'name': 'C材料'},
        {'id': 2, 'name': 'B材料'},
        {'id': 4, 'name': 'D材料'},
        {'id': 1, 'name': 'A材料'},
      ],
    };
    snapshot = {
      ...snapshot,
      'recommendationState': {
        'inventoryReadReady': true,
        ...runtime,
        'ownedIngredientQty': {'1': 0, '2': 4, '3': -1},
      },
    };
  }
  @override
  int connectionVersion = 0;
  Completer<void>? writeGate;
  @override
  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  }) async {
    await super.mutate(path, query: query, body: body, localOnly: localOnly);
    await writeGate?.future;
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

  Future<void> open(
    WidgetTester tester,
    _ToolsModel model, {
    double width = 1280,
    double scale = 1,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(model.dispose);
    final theme = stewardTheme(dark: width == 390, touch: width < 640);
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
        // 与正式应用一致扣除固定导航及左右留白，390px实际内容只有262px。
        home: Padding(
          padding: const EdgeInsets.fromLTRB(104, 12, 24, 12),
          child: AnimatedBuilder(
            animation: model,
            builder: (context, _) => ToolsPage(model),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> tap(WidgetTester tester, Finder finder) async {
    await tester.ensureVisible(finder);
    // ensureVisible 在零时长滚动时先改变 offset，实际卡片坐标下一帧才更新。
    // 必须完成列表布局后再点击；同时保留严格命中断言，不掩盖被裁切的操作。
    await tester.pumpAndSettle();
    expect(finder.hitTestable(), findsOneWidget);
    await tester.tap(finder);
    await tester.pumpAndSettle();
  }

  Finder invite(int id) => find.descendant(
    of: find.byWidgetPredicate(
      (widget) => widget is InvitationItemContent && widget.item['id'] == id,
    ),
    matching: find.byType(StewardButton),
  );

  test('库存稳定排序区分无限、零与未知，不修改目录且不联结非法ID', () {
    final entries = <DataMap>[
      {'id': 7, 'name': '同名'},
      {'id': 2, 'name': '同名'},
      {'id': 3, 'name': '无限'},
      {'id': 4, 'name': '零'},
      {'id': 5, 'name': '错误负值'},
      {'id': 6, 'name': '无穷值'},
      {'id': -1, 'name': '非法ID'},
      {'name': '缺ID'},
    ];
    final quantities = <String, Object?>{
      '7': 5,
      '2': 5,
      '3': -1,
      '4': 0,
      '5': -2,
      '6': double.infinity,
      '-1': 999,
      'null': 999,
    };
    final ascending = sortInventoryEntries(
      entries,
      quantities,
      column: InventorySortColumn.quantity,
      ascending: true,
    );
    final descending = sortInventoryEntries(
      entries,
      quantities,
      column: InventorySortColumn.quantity,
      ascending: false,
    );
    expect(ascending.take(4).map((item) => item['id']), [4, 2, 7, 3]);
    expect(descending.take(4).map((item) => item['id']), [3, 2, 7, 4]);
    expect(entries.first['id'], 7);
    expect(inventoryStockText(entries[2], quantities), '无限');
    expect(inventoryStockText(entries[3], quantities), '0');
    for (final entry in entries.skip(4)) {
      expect(inventoryStockText(entry, quantities), '未知');
    }
    expect(
      sortInventoryEntries(
        entries.reversed.toList(),
        quantities,
        column: InventorySortColumn.quantity,
        ascending: true,
      ).map((e) => e['id']),
      ascending.map((e) => e['id']),
    );
  });

  for (final width in [1280.0, 640.0, 390.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('任务及邀请外层信息完整 $width/$scale，直接操作且无二层详情', (tester) async {
        final model = _ToolsModel();
        await open(tester, model, width: width, scale: scale);
        // 单层状态筛选同时决定所需来源；此用例只查看可完成条目的字段。
        await tap(
          tester,
          find.descendant(
            of: find.byKey(const ValueKey('mission-status-filter')),
            matching: find.text('可完成'),
          ),
        );
        expect(find.byType(DenseListDetail), findsNothing);
        expect(find.byType(SemanticTag), findsNothing);
        expect(find.byType(OrderTag), findsNothing);
        expect(find.text('可完成'), findsNWidgets(2));
        expect(find.text('条件进度'), findsOneWidget);
        expect(find.text('未知 / 3'), findsOneWidget);
        expect(find.text('与合成角色交谈'), findsOneWidget);
        expect(find.text('交付三份合成料理'), findsOneWidget);
        expect(find.text('完成合成事件'), findsOneWidget);
        expect(find.text('已完成'), findsOneWidget);
        expect(find.text('未完成'), findsOneWidget);
        expect(
          find.descendant(
            of: find.byType(MissionItemContent),
            matching: find.text('待确认'),
          ),
          findsOneWidget,
        );
        expect(find.text('角色'), findsOneWidget);
        expect(find.text('合成角色'), findsOneWidget);
        expect(find.text('接取人'), findsOneWidget);
        expect(find.text('synthetic-receiver'), findsOneWidget);
        expect(find.text('合成神社 · 合成竹林'), findsOneWidget);
        await tap(tester, find.text('可接取'));
        expect(find.text('满足条件触发'), findsOneWidget);
        expect(find.text('羁绊检查点'), findsOneWidget);
        expect(find.text('演出后'), findsOneWidget);
        expect(find.text('羁绊条件已就绪，等待游戏检查点触发。'), findsOneWidget);
        expect(find.textContaining('条件进度'), findsNothing);
        await tap(tester, find.text('稀客邀请'));
        expect(find.byType(DenseListDetail), findsNothing);
        expect(find.byType(SemanticTag), findsNothing);
        expect(find.byType(OrderTag), findsNothing);
        expect(find.text('3 级'), findsOneWidget);
        expect(
          find.descendant(
            of: find.byType(InvitationItemContent).first,
            matching: find.text('当前地图已登记'),
          ),
          findsOneWidget,
        );
        expect(find.text('合成邀请条件已就绪'), findsOneWidget);
        await tap(tester, invite(301));
        expect(model.writes.single.$1, '/rare-guests/invite');
        expect(model.writes.single.$2, {
          'scope': 'current',
          'expectedDaySceneGeneration': '1',
          'expectedMapLabel': 'hakurei',
          'guestId': '301',
        });
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
      });
    }
  }

  testWidgets('库存点击两列表头切换方向，选择和确认载荷保持原ID', (tester) async {
    final model = _ToolsModel();
    await open(tester, model);
    await tap(tester, find.text('库存修改'));
    List<String> names() => tester
        .widgetList<InventoryItem>(
          find.descendant(
            of: find.byKey(const ValueKey('inventory-items')),
            matching: find.byType(InventoryItem),
          ),
        )
        .map((item) => item.name)
        .toList();
    expect(names(), ['A材料', 'B材料', 'C材料', 'D材料']);
    await tap(tester, find.text('B材料'));
    await tap(tester, find.byKey(const ValueKey('inventory-sort-quantity')));
    expect(names(), ['A材料', 'B材料', 'C材料', 'D材料']);
    await tap(tester, find.byKey(const ValueKey('inventory-sort-quantity')));
    expect(names(), ['C材料', 'B材料', 'A材料', 'D材料']);
    expect(find.text('已选 1 项'), findsOneWidget);
    await tap(tester, find.byKey(const ValueKey('inventory-sort-name')));
    await tap(tester, find.byKey(const ValueKey('inventory-sort-name')));
    expect(names(), ['D材料', 'C材料', 'B材料', 'A材料']);
    expect(model.writes, isEmpty);
    await tap(tester, find.text('设置已选 1 项'));
    await tap(tester, find.text('确认'));
    expect(model.writes.single.$2, {
      'type': 'ingredient',
      'ids': '2',
      'qty': '99',
    });
    expect(tester.takeException(), isNull);
  });

  for (final change in [
    'connection',
    'authority',
    'day',
    'candidate',
    'module',
  ]) {
    testWidgets('外层邀请旧回调在$change变化后拒绝执行', (tester) async {
      final model = _ToolsModel();
      await open(tester, model);
      await tap(tester, find.text('稀客邀请'));
      final action = tester.widget<StewardButton>(invite(301)).onPressed!;
      switch (change) {
        case 'connection':
          model.connectionVersion++;
        case 'authority':
          model.authority = {...model.authority, 'authorityRevision': 2};
        case 'day':
          model.snapshot = {...model.snapshot, 'runtimeDaySceneReady': false};
        case 'candidate':
          model.resources['/rare-guests/invitations'] = {
            'candidates': [
              {
                ...records(
                  model.resources['/rare-guests/invitations']!['candidates'],
                ).first,
                'canInvite': false,
              },
            ],
          };
        case 'module':
          model.localPreferences['rareGuestInvitationModuleEnabled'] = false;
      }
      model.publish();
      await tester.pumpAndSettle();
      action();
      await tester.pumpAndSettle();
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('只读邀请及未知羁绊保留信息，写入期间所有邀请操作禁用', (tester) async {
    final model = _ToolsModel();
    await open(tester, model);
    await tap(tester, find.text('稀客邀请'));
    expect(tester.widget<StewardButton>(invite(302)).onPressed, isNull);
    expect(
      find.descendant(
        of: find.byWidgetPredicate(
          (widget) =>
              widget is InvitationItemContent && widget.item['id'] == 302,
        ),
        matching: find.text('未知'),
      ),
      findsOneWidget,
    );
    model.authority = {...model.authority, 'currentDeviceIsPrimary': false};
    model.publish();
    await tester.pumpAndSettle();
    expect(tester.widget<StewardButton>(invite(301)).onPressed, isNull);
    model.authority = {...model.authority, 'currentDeviceIsPrimary': true};
    model.publish();
    await tester.pumpAndSettle();
    model.writeGate = Completer<void>();
    await tap(tester, invite(301));
    expect(tester.widget<StewardButton>(invite(301)).onPressed, isNull);
    expect(
      tester
          .widget<StewardButton>(
            find.byWidgetPredicate(
              (widget) => widget is StewardButton && widget.text == '邀请筛选范围',
            ),
          )
          .onPressed,
      isNull,
    );
    model.writeGate!.complete();
    await tester.pumpAndSettle();
    expect(model.writes, hasLength(1));
  });
}
