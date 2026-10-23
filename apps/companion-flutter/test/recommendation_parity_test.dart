import 'dart:async';

import 'package:material_ui/material_ui.dart' as material;
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/custom_recipes.dart';
import 'package:mystia_steward_companion/features/recommendation_browser_support.dart';
import 'package:mystia_steward_companion/features/recommendations.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 仅使用合成目录和可控 Future；请求记录验证页面实际发送的范围，不接游戏/网络。
class _ParityModel extends FixtureModel {
  _ParityModel() : super(orderCount: 0) {
    catalog = {
      'isComplete': true,
      'normalCustomers': [
        {
          'id': 11,
          'name': '甲地客群',
          'places': ['地点甲'],
          'positiveTags': ['甜'],
          'beverageTags': ['水果'],
        },
        {
          'id': 12,
          'name': '乙地客群',
          'places': ['地点乙'],
          'positiveTags': ['清淡', '完整额外偏好'],
          'beverageTags': ['低酒精'],
        },
      ],
      'rareCustomers': [
        {
          'id': 301,
          'name': '合成稀客',
          'positiveTags': ['鲜'],
          'beverageTags': ['清爽'],
        },
      ],
      'recipes': [
        {
          'id': 1,
          'recipeId': 101,
          'name': '合成料理',
          'ingredients': ['豆腐'],
          'positiveTags': ['清淡'],
          'cooker': '煮锅',
          'price': 20,
        },
      ],
      'ingredients': [
        {'id': 2, 'name': '蘑菇'},
      ],
      'beverages': [
        {
          'id': 3,
          'name': 'Tea',
          'tags': ['清爽'],
          'price': 10,
        },
      ],
    };
    snapshot = {
      ...snapshot,
      'nightBusiness': {'place': '地点甲'},
    };
    resources = {
      '/favorites': {
        'version': 1,
        'recipes': <DataMap>[],
        'beverages': <DataMap>[],
      },
      '/custom-recipes': {
        'version': 2,
        'activeMode': 'customer',
        'modeRevision': 0,
        'enabled': true,
        'recipes': [
          binding('a', 'customer', 301),
          binding('b', 'customer', 302),
          binding('c', 'food', 301),
        ],
      },
    };
    localPreferences = {'reduceMotion': true};
  }
  static DataMap binding(String id, String mode, int customer) => {
    'id': id,
    'mode': mode,
    'customerId': customer,
    'customerName': '客人$customer',
    'foodId': 1,
    'recipeId': 101,
    'recipeName': '合成料理',
    'extraIngredientIds': <int>[],
    'enabled': true,
    'pinToTop': false,
    'sortOrder': 1,
    'createdAtUtc': '',
  };
  final queries = <String>[];
  Completer<void>? mutationGate;
  @override
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  }) async {
    queries.add('$kind/$selectedPlace');
    pageResult = {
      'recipes': [
        {
          'recipe': catalogItems('recipes').single,
          'extraIngredients': [
            {'id': 2, 'name': '蘑菇'},
          ],
        },
      ],
      'beverages': [
        {'beverage': catalogItems('beverages').single},
      ],
    };
    publish();
  }

  @override
  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  }) async {
    await super.mutate(path, query: query, body: body, localOnly: localOnly);
    await mutationGate?.future;
    if (path == '/favorites/add-recipe') {
      resources['/favorites']!['recipes'] = [
        {
          'id': 'saved',
          'customerId': 301,
          'customerName': '合成稀客',
          'foodTag': '鲜',
          'recipeId': 1,
          'extraIngredientIds': [2],
        },
      ];
    } else if (path == '/favorites/remove-recipe') {
      resources['/favorites']!['recipes'] = <DataMap>[];
    }
    publish();
  }
}

void main() {
  WidgetController.hitTestWarningShouldBeFatal = true;
  Future<void> open(
    WidgetTester tester,
    _ParityModel model, {
    bool custom = false,
    double width = 1280,
    double scale = 1,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 900);
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
            data: MediaQuery.of(context)
                .copyWith(textScaler: TextScaler.linear(scale)),
            child: child!,
          ),
        ),
        home: ListenableBuilder(
          listenable: model,
          builder: (context, _) => Padding(
            padding: const EdgeInsets.all(16),
            child: custom
                ? CustomRecipesPanel(model, active: true)
                : RecommendationsPage(model),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
  }

  Future<void> choose(WidgetTester tester, String key, String value) async {
    await tester.tap(find.byKey(ValueKey(key)));
    await tester.pumpAndSettle();
    final item = find.byWidgetPredicate(
      (widget) => widget is FSelectItem<String> && widget.value == value,
    );
    await tester.ensureVisible(item);
    await tester.tap(item);
    await tester.pumpAndSettle();
  }

  test('收藏搜索覆盖标签、原料、加料、编号，并规范全角/大小写/空白', () {
    final model = _ParityModel();
    addTearDown(model.dispose);
    final entry = {
      'id': 'saved',
      'recipeId': 1,
      'customerId': 301,
      'customerName': '合成稀客',
      'foodTag': '鲜',
      'extraIngredientIds': [2],
    };
    for (final query in ['鲜', '豆腐', '蘑菇', '３０１', ' １ ', '合成料理', '清淡']) {
      expect(
        favoriteMatchesSearch(model, entry, query, food: true),
        isTrue,
        reason: query,
      );
    }
    expect(favoriteMatchesSearch(model, entry, '不存在', food: true), isFalse);
    expect(
      favoriteMatchesSearch(
        model,
        {'beverageId': 3, 'customerId': 301, 'beverageTag': '清爽'},
        '　ＴＥＡ　',
        food: false,
      ),
      isTrue,
    );
    expect(
      favoriteMatchesSearch(
        model,
        {...entry, 'recipeId': 999},
        '９９９',
        food: true,
      ),
      isTrue,
    );
  });

  testWidgets('默认跟随现场、手动固定后不变，恢复跟随立即切到新现场', (tester) async {
    final model = _ParityModel();
    await open(tester, model);
    expect(model.queries.last, 'normal/地点甲');
    await choose(tester, 'normal-place-selector', '地点乙');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(model.queries.last, 'normal/地点乙');
    final count = model.queries.length;
    model.publish();
    await tester.pumpAndSettle();
    expect(model.queries.length, count);
    await tester.tap(find.byKey(const ValueKey('normal-follow-place')));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(model.queries.last, 'normal/地点甲');
    model.snapshot['nightBusiness'] = {'place': '地点乙'};
    model.publish();
    await tester.pumpAndSettle();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(model.queries.last, 'normal/地点乙');
    await tester.pumpWidget(const SizedBox());
  });

  for (final width in [640.0, 390.0]) {
    testWidgets('客群偏好 $width/150% 完整展示且不混入其它地区', (tester) async {
      final model = _ParityModel();
      model.snapshot['nightBusiness'] = {'place': '地点乙'};
      await open(tester, model, width: width, scale: 1.5);
      await tester.ensureVisible(
        find.byKey(const ValueKey('normal-customer-preferences')),
      );
      await tester.tap(
        find.byKey(const ValueKey('normal-customer-preferences')),
      );
      await tester.pumpAndSettle();
      expect(find.text('乙地客群'), findsOneWidget);
      expect(find.text('完整额外偏好'), findsOneWidget);
      expect(find.text('低酒精'), findsOneWidget);
      expect(find.text('甲地客群'), findsNothing);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('稀客收藏读取、选中态和原地取消共用精确记录，等待时不重复提交', (tester) async {
    final model = _ParityModel();
    await open(tester, model);
    await tester.tap(find.text('稀客'));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(model.reads, contains('/favorites'));
    final button = find.byKey(const ValueKey('recommendation-toggle-favorite'));
    final callback = tester.widget<StewardButton>(button).onPressed!;
    model.mutationGate = Completer<void>();
    await tester.tap(button);
    await tester.pump();
    callback();
    expect(model.writes, hasLength(1));
    model.mutationGate!.complete();
    await tester.pumpAndSettle();
    expect(find.text('取消收藏'), findsOneWidget);
    await tester.tap(button);
    await tester.pumpAndSettle();
    expect(model.writes.last.$1, '/favorites/remove-recipe');
    expect(model.writes.last.$2, {'id': 'saved'});
    expect(find.text('加入收藏'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('当前模式全部批量操作包含筛选外条目，单次提交scope而不逐条重放', (tester) async {
    final model = _ParityModel();
    await open(tester, model, custom: true);
    await choose(tester, 'custom-batch-all', 'pin');
    expect(find.textContaining('当前模式全部 2 个绑定'), findsOneWidget);
    expect(find.textContaining('其他模式不受影响'), findsOneWidget);
    await tester.tap(find.text('确认'));
    await tester.pumpAndSettle();
    expect(model.writes.single.$1, '/custom-recipes/update-flags');
    expect(model.writes.single.$2, {
      'mode': 'customer',
      'expectedModeRevision': '0',
      'scope': 'all',
      'pinToTop': 'true',
    });
  });

  testWidgets('收藏轮询先于下一帧更新时，旧加入按钮不会反向删除新记录', (tester) async {
    final model = _ParityModel();
    await open(tester, model);
    await tester.tap(find.text('稀客'));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    final callback = tester
        .widget<StewardButton>(
          find.byKey(const ValueKey('recommendation-toggle-favorite')),
        )
        .onPressed!;
    model.resources['/favorites']!['recipes'] = <DataMap>[
      {
        'id': 'arrived-before-frame',
        'customerId': 301,
        'foodTag': '鲜',
        'recipeId': 1,
        'extraIngredientIds': [2],
      },
    ];
    model.publish();
    callback();
    await tester.pumpAndSettle();
    expect(model.writes, isEmpty);
    expect(find.text('取消收藏'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('本组批量操作只发送精确组ID，确认期间条目变化则不发送', (tester) async {
    final model = _ParityModel();
    await open(tester, model, custom: true);
    await choose(tester, 'custom-batch-customer:301', 'disable');
    await tester.tap(find.text('确认'));
    await tester.pumpAndSettle();
    expect(model.writes.single.$2['scope'], 'customer');
    expect(model.writes.single.$2['customerId'], '301');
    await choose(tester, 'custom-batch-all', 'pin');
    records(model.resources['/custom-recipes']!['recipes']).first['enabled'] =
        false;
    model.publish();
    await tester.pumpAndSettle();
    await tester.tap(find.text('确认'));
    await tester.pumpAndSettle();
    expect(model.writes, hasLength(1));
  });

  for (final width in [640.0, 390.0]) {
    testWidgets('配方批量菜单 $width/150% 保留可操作布局', (tester) async {
      final model = _ParityModel();
      await open(tester, model, custom: true, width: width, scale: 1.5);
      expect(find.byKey(const ValueKey('custom-batch-all')), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  }
}
