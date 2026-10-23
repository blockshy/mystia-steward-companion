import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/favorites_selection.dart';
import 'package:mystia_steward_companion/features/service_context_content.dart';
import 'package:mystia_steward_companion/features/service_favorites.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/features/tools.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/text_geometry.dart';

void main() {
  final food = <String, Object?>{
    'recipe': {'id': 11, 'recipeId': 999, 'name': '测试料理'},
    'extraIngredients': [
      {'id': 3},
      {'id': 2},
      {'id': 3},
    ],
  };
  test('收藏使用目录id和main规范加料集合，不修改真实制作序列', () {
    final favorites = <String, Object?>{
      'recipes': [
        {
          'id': 'exact',
          'customerId': 7,
          'foodTag': '鲜',
          'recipeId': 11,
          'extraIngredientIds': [2, 3],
        },
        {
          'id': 'other-tag',
          'customerId': 7,
          'foodTag': '肉',
          'recipeId': 11,
          'extraIngredientIds': [2, 3],
        },
      ],
    };
    expect(
      recommendationFavorite(
        favorites,
        food,
        food: true,
        customerId: 7,
        tag: '鲜',
      ),
      (known: true, id: 'exact'),
    );
    expect(
      recommendationFavorite(
        favorites,
        food,
        food: true,
        customerId: 8,
        tag: '鲜',
      ),
      (known: true, id: null),
    );
    expect(
      records(food['extraIngredients']).map((item) => item['id']).toList(),
      [3, 2, 3],
    );
  });
  test('未读取、损坏与歧义收藏不当作未收藏', () {
    expect(
      recommendationFavorite(
        {},
        food,
        food: true,
        customerId: 7,
        tag: '鲜',
      ).known,
      isFalse,
    );
    expect(
      recommendationFavorite(
        {'recipes': []},
        {
          ...food,
          'extraIngredients': [
            {'id': -1},
          ],
        },
        food: true,
        customerId: 7,
        tag: '鲜',
      ).known,
      isFalse,
    );
    final duplicate = {
      'id': 'a',
      'customerId': 7,
      'foodTag': '鲜',
      'recipeId': 11,
      'extraIngredientIds': [3, 2],
    };
    expect(
      recommendationFavorite(
        {
          'recipes': [
            duplicate,
            {...duplicate, 'id': 'b'},
          ],
        },
        food,
        food: true,
        customerId: 7,
        tag: '鲜',
      ).known,
      isFalse,
    );
  });
  test('任务筛选只消费宿主状态，未知不会根据计数冒充可完成', () {
    expect(missionMatchesStatus({'status': 'fulfilled'}, 'fulfilled'), isTrue);
    expect(missionMatchesStatus({'status': 'tracking'}, 'fulfilled'), isFalse);
    expect(missionMatchesStatus({'status': 'tracking'}, 'tracking'), isTrue);
    expect(
      missionMatchesStatus({
        'conditionCount': 2,
        'completedConditionCount': 2,
      }, 'unverified'),
      isTrue,
    );
    expect(
      missionMatchesStatus({'status': 'future-status'}, 'unverified'),
      isTrue,
    );
    expect(
      missionMatchesStatus(
        {'activationStatus': 'available'},
        'available',
        available: true,
      ),
      isTrue,
    );
    expect(
      missionMatchesStatus(
        {'activationStatus': 'available'},
        'unverified',
        available: true,
      ),
      isFalse,
    );
    expect(missionMatchesStatus({'status': 'tracking'}, 'available'), isFalse);
  });

  test('任务全部来源按精确标识去重，追踪优先，同名和未知身份不吞项', () {
    final entries = mergeMissionEntries(
      [
        {'label': 'same', 'title': '重名', 'status': 'tracking'},
        {'label': 'different', 'title': '重名', 'status': 'fulfilled'},
        {'title': '无身份'},
      ],
      [
        {'label': 'same', 'title': '旧可接取'},
        {'label': 'obtainable', 'title': '重名'},
        {'title': '无身份'},
      ],
    );
    expect(entries.length, 5);
    expect(
      entries.where((entry) => entry.item['label'] == 'same').single.available,
      isFalse,
    );
    expect(entries.where((entry) => entry.item['title'] == '无身份').length, 2);
  });

  Future<void> mount(
    WidgetTester tester,
    FixtureModel model,
    Widget Function() content, {
    double width = 390,
    double scale = 1,
  }) async {
    tester.view.physicalSize = Size(width, 960);
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
              size: Size(width, 960),
              textScaler: TextScaler.linear(scale),
            ),
            child: child!,
          ),
        ),
        home: Padding(
          padding: const EdgeInsets.all(16),
          child: ListenableBuilder(
            listenable: model,
            builder: (_, _) => SingleChildScrollView(child: content()),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  for (final width in [640.0, 390.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('点单与免费标签共用流式布局：$width px / $scale', (tester) async {
        final model = FixtureModel(desktop: width >= 640, orderCount: 0);
        addTearDown(model.dispose);
        final item = <String, Object?>{
          'order': {'foodTag': '清淡', 'beverageTag': '无酒精', 'isFreeOrder': true},
          'executionPlans': <DataMap>[],
        };
        await mount(
          tester,
          model,
          () => ServiceOrderSummary(model, item, rare: true),
          width: width,
          scale: scale,
        );
        final demand = find.text('料理：清淡');
        final beverage = find.text('酒水：无酒精');
        final free = find.text('免费订单');
        Finder tagWrap(Finder text) =>
            find.ancestor(of: text, matching: find.byType(Wrap)).first;
        expect(
          tester.element(tagWrap(demand)),
          same(tester.element(tagWrap(free))),
        );
        expect(
          tester.element(tagWrap(beverage)),
          same(tester.element(tagWrap(free))),
        );
        if (width == 640) {
          expect(
            tester.getRect(free).top,
            closeTo(tester.getRect(demand).top, .1),
            reason: '宽度足够时免费等短附注不能强制占用新行',
          );
        }
        // 窄屏及大字号允许整枚标签换行，不允许截断文字或向卡片之外溢出。
        for (final text in [demand, beverage, free]) {
          expect(tester.getRect(text).right, lessThanOrEqualTo(width - 16));
          expect(tester.widget<Text>(text).maxLines, isNull);
        }
        expect(tester.takeException(), isNull);
      });
    }
  }

  testWidgets('经营收藏直接保存正在看的精确组合，取消使用持久化记录ID', (tester) async {
    final model = FixtureModel(desktop: false, orderCount: 0);
    addTearDown(model.dispose);
    final item = <String, Object?>{
      'customer': {'id': 7, 'name': '测试稀客'},
      'order': {
        'traceId': 'R-1',
        'orderLifecycleSequence': 1,
        'foodTag': '鲜',
        'beverageTag': '直饮',
      },
    };
    final plan = <String, Object?>{
      'food': food,
      'beverage': {
        'beverage': {'id': 5, 'name': '测试酒水'},
      },
    };
    item['executionPlans'] = [plan];
    model.business['recommendations'] = {
      'recommendations': [item],
    };
    model.resources['/favorites'] = {'recipes': [], 'beverages': []};
    await mount(tester, model, () => ServiceFavoriteActions(model, item, plan));
    await tester.tap(find.text('收藏料理'));
    await tester.pumpAndSettle();
    expect(model.writes.single.$1, '/favorites/add-recipe');
    expect(model.writes.single.$2, {
      'customerId': '7',
      'customerName': '测试稀客',
      'foodTag': '鲜',
      'recipeId': '11',
      'extraIngredientIds': '2,3',
    });
    model.resources['/favorites'] = {
      'recipes': [
        {
          'id': 'stored-1',
          'customerId': 7,
          'foodTag': '鲜',
          'recipeId': 11,
          'extraIngredientIds': [3, 2],
        },
      ],
      'beverages': [],
    };
    model.publish();
    await tester.pumpAndSettle();
    await tester.tap(find.text('取消收藏料理'));
    await tester.pumpAndSettle();
    expect(model.writes.last.$1, '/favorites/remove-recipe');
    expect(model.writes.last.$2, {'id': 'stored-1'});
    expect(tester.takeException(), isNull);
  });

  testWidgets('任务单层状态默认合并双来源，筛选切换订阅并保留名称搜索', (tester) async {
    final model = FixtureModel(desktop: false, orderCount: 0);
    addTearDown(model.dispose);
    model.localPreferences['missionListModuleEnabled'] = true;
    model.resources['/missions/tracked'] = {
      'status': 'ready',
      'runtimeAvailable': true,
      'missions': <DataMap>[
        {
          'label': 'shared',
          'title': '已追踪任务',
          'status': 'fulfilled',
          'conditionCount': 1,
          'completedConditionCount': 1,
        },
      ],
    };
    model.resources['/missions/available'] = {
      'status': 'ready',
      'runtimeAvailable': true,
      'missions': <DataMap>[
        {
          'label': 'shared',
          'title': '旧可接取同一任务',
          'activationStatus': 'available',
        },
        {'label': 'other', 'title': '新可接取任务', 'activationStatus': 'available'},
      ],
    };
    await mount(
      tester,
      model,
      () => SizedBox(height: 820, child: ToolsPage(model)),
    );
    expect(model.reads, ['/missions/tracked', '/missions/available']);
    expect(find.text('已追踪任务'), findsOneWidget);
    expect(find.text('新可接取任务'), findsOneWidget);
    expect(find.text('旧可接取同一任务'), findsNothing);
    final filters = find.byKey(const ValueKey('mission-status-filter'));
    expect(
      find.descendant(
        of: filters,
        matching: find.byType(WorkspaceTabs<String>),
      ),
      findsOneWidget,
    );
    expect(find.text('追踪'), findsNothing);
    expect(find.text('全部状态'), findsNothing);
    final completed = find.descendant(of: filters, matching: find.text('可完成'));
    await tester.ensureVisible(completed);
    await tester.tap(completed);
    await tester.pumpAndSettle();
    expect(model.reads.last, '/missions/tracked');
    expect(find.text('已追踪任务'), findsOneWidget);
    expect(find.text('新可接取任务'), findsNothing);
    final available = find.text('可接取');
    await tester.ensureVisible(available);
    await tester.tap(available);
    await tester.pumpAndSettle();
    expect(model.reads.last, '/missions/available');
    expect(find.text('新可接取任务'), findsOneWidget);
    expect(find.text('条件进度'), findsNothing);
    final search = find.descendant(
      of: find.byKey(const ValueKey('extension-page-toolbar')),
      matching: find.byType(EditableText),
    );
    await tester.enterText(search, '新可接取');
    await tester.pumpAndSettle();
    expect(find.text('旧可接取同一任务'), findsNothing);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  for (final width in [390.0, 423.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('任务单层状态 $width/$scale 保留横滚和独立搜索', (tester) async {
        final model = FixtureModel(desktop: false, orderCount: 0);
        addTearDown(model.dispose);
        model.localPreferences['missionListModuleEnabled'] = true;
        await mount(
          tester,
          model,
          () => SizedBox(height: 820, child: ToolsPage(model)),
          width: width,
          scale: scale,
        );
        final toolbar = find.byKey(const ValueKey('extension-page-toolbar'));
        final sourceTabs = find.descendant(
          of: toolbar,
          matching: find.byType(WorkspaceTabs<String>),
        );
        final viewport = tester.getRect(sourceTabs);
        expect(
          tester.widget<WorkspaceTabs<String>>(sourceTabs).horizontalScroll,
          isTrue,
        );
        expect(find.text('全部状态'), findsNothing);
        expect(find.text('追踪'), findsNothing);
        expect(
          find.descendant(
            of: toolbar,
            matching: find.byType(WorkspaceTabs<String>),
          ),
          findsOneWidget,
        );
        final first = find.descendant(
          of: sourceTabs,
          matching: find.text('全部'),
        );
        expect(first.hitTestable(), findsOneWidget);
        // 五种状态在手机上按需横滚；每项均能完整进入视口，不能因少一排导航而丢入口。
        for (final title in ['全部', '可完成', '进行中', '可接取', '待确认']) {
          final text = find.descendant(
            of: sourceTabs,
            matching: find.text(title),
          );
          await tester.ensureVisible(text);
          await tester.pumpAndSettle();
          final rect = tester.getRect(text);
          expect(rect.left, greaterThanOrEqualTo(viewport.left - .1));
          expect(rect.right, lessThanOrEqualTo(viewport.right + .1));
          expect(
            rect.top,
            closeTo(
              tester
                  .getRect(
                    find.descendant(of: sourceTabs, matching: find.text('全部')),
                  )
                  .top,
              .1,
            ),
          );
          expect(text.hitTestable(), findsOneWidget);
        }
        {
          // 保留独立横滚，不压缩字号，也不让搜索框随页签一起移出视口。
          final source = find.descendant(
            of: sourceTabs,
            matching: find.text('可接取'),
          );
          await tester.ensureVisible(source);
          await tester.pumpAndSettle();
          expect(source.hitTestable(), findsOneWidget);
          await tester.tap(source);
          await tester.pumpAndSettle();
          expect(model.reads.last, '/missions/available');
        }
        final search = find.descendant(
          of: toolbar,
          matching: find.byType(EditableText),
        );
        expect(tester.getRect(search).right, lessThanOrEqualTo(width - 16));
        expect(tester.takeException(), isNull);
        expect(model.writes, isEmpty);
      });
    }
  }

  for (final change in [
    'authority',
    'generation',
    'plan',
    'favorites',
    'removed',
  ]) {
    testWidgets('经营收藏旧回调在 $change 变化后不发出写入', (tester) async {
      final model = FixtureModel(orderCount: 0);
      addTearDown(model.dispose);
      final plan = <String, Object?>{'food': food};
      final item = <String, Object?>{
        'customer': {'id': 7, 'name': '测试稀客'},
        'order': {
          'traceId': 'R-1',
          'orderLifecycleSequence': 1,
          'foodTag': '鲜',
        },
        'executionPlans': [plan],
      };
      model.business['recommendations'] = {
        'recommendations': [item],
      };
      model.resources['/favorites'] = {'recipes': [], 'beverages': []};
      await mount(
        tester,
        model,
        () => ServiceFavoriteActions(model, item, plan),
      );
      final callback = tester
          .widget<StewardButton>(
            find.ancestor(
              of: find.text('收藏料理'),
              matching: find.byType(StewardButton),
            ),
          )
          .onPressed!;
      // 故意不通知重建，覆盖网络回调已替换模型但旧按钮仍在一帧内的边界。
      switch (change) {
        case 'authority':
          model.authority['authorityRevision'] = 99;
        case 'generation':
          model.snapshot['nightBusinessGeneration'] = 99;
        case 'plan':
          item['executionPlans'] = <DataMap>[];
        case 'favorites':
          model.resources['/favorites'] = {
            'recipes': <DataMap>[
              {
                'id': 'new',
                'customerId': 7,
                'foodTag': '鲜',
                'recipeId': 11,
                'extraIngredientIds': [2, 3],
              },
            ],
          };
        case 'removed':
          model.business['recommendations'] = {'recommendations': <DataMap>[]};
      }
      callback();
      await tester.pumpAndSettle();
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  for (final width in [390.0, 512.0]) {
    testWidgets('经营补充信息 ${width}px 大字号完整显示零进度、未知比例与未下单客人', (tester) async {
      final model = FixtureModel(desktop: false, orderCount: 0);
      addTearDown(model.dispose);
      model.snapshot['nightBusiness'] = {
        'orders': <DataMap>[],
        'activeRareGuests': <DataMap>[
          {'guestId': 7, 'guestName': '尚未下单稀客', 'deskCode': 7, 'fund': 120},
        ],
      };
      model.snapshot['specialBusiness'] = {
        'active': true,
        'displayName': '测试挑战',
        'phase': '阶段三',
        'foodTargetTags': ['清淡', '多字合成料理需求标签', '家常'],
        'beverageTargetTags': ['无酒精', '多字合成酒水需求标签'],
        'currentValue': 0,
        'maxValue': 20,
        'currentSpellCount': 0,
        'targetSpellCount': 3,
        'targetTagTimeProgress': .25,
        'targetTimeProgress': 2,
        'recommendationPolicy': '保留具体推荐策略',
        'automationPolicy': '保留具体执行策略',
      };
      await mount(
        tester,
        model,
        () => ServiceContextContent(model),
        width: width,
        scale: 1.5,
      );
      expect(find.text('尚未下单稀客'), findsOneWidget);
      expect(find.text('0 / 20'), findsOneWidget);
      expect(find.text('0 / 3'), findsOneWidget);
      expect(find.text('25%'), findsOneWidget);
      expect(find.text('保留具体推荐策略'), findsOneWidget);
      // 四字目标字段必须与相邻 Fact 使用同一列，不能落回菜单两字槽而强制折行。
      // 以实际段落参数的公开测量接口验证基线，避免在布局外调用 RenderBox 的受限方法。
      // 右列边界仍读取已布局矩形，覆盖 390px / 150% 的窄屏。
      final phaseName = tester.getRect(find.text('当前阶段'));
      final phaseValue = tester.getRect(find.text('阶段三'));
      for (final (name, firstTag) in [('料理目标', '清淡'), ('酒水目标', '无酒精')]) {
        final field = find.text(name);
        final fieldRect = tester.getRect(field);
        final tags = find.byKey(ValueKey('special-business-target-tags-$name'));
        final tagRect = tester.getRect(tags);
        expect(fieldRect.left, closeTo(phaseName.left, .1));
        expect(
          fieldRect.height,
          closeTo(phaseName.height, .1),
          reason: '四字字段不能因前缀槽过窄折行',
        );
        expect(
          tagRect.left,
          closeTo(phaseValue.left, .1),
          reason: '目标标签与Fact右列对齐',
        );
        expect(tagRect.left - fieldRect.left, closeTo(94, .1));
        expect(
          textBaseline(tester, field),
          closeTo(textBaseline(tester, find.text(firstTag)), .1),
        );
        for (final element
            in find
                .descendant(of: tags, matching: find.byType(OrderTag))
                .evaluate()) {
          final rect = tester.getRect(find.byWidget(element.widget));
          expect(rect.left, greaterThanOrEqualTo(tagRect.left - .1));
          expect(rect.right, lessThanOrEqualTo(tagRect.right + .1));
          expect(rect.bottom, lessThanOrEqualTo(tagRect.bottom + .1));
        }
      }
      expect(tester.takeException(), isNull);
      model.connected = false;
      model.publish();
      await tester.pumpAndSettle();
      expect(find.text('尚未下单稀客'), findsNothing);
      expect(model.writes, isEmpty);
    });
  }
}
