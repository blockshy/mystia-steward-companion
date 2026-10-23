import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/catalog_item_content.dart';
import 'package:mystia_steward_companion/features/data.dart';
import 'package:mystia_steward_companion/features/resource_management.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/game_media_scope.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/steward_test_fonts.dart';

/// 编号故意交叉：第二份配方的 Recipe.id 等于第一份料理的 Food.id，库存也与
/// 解锁集合相反。这样能发现错误地拿 recipeId 或非零库存冒充图鉴记录的实现。
class _CatalogModel extends FixtureModel {
  _CatalogModel({super.desktop}) {
    catalog = {
      'isComplete': true,
      'recipes': [
        {
          'id': 101,
          'recipeId': 9001,
          'name': '合成香菇清汤',
          'price': 28,
          'level': 1,
          'cooker': '煮锅',
          'ingredients': ['香菇', '香菇'],
          'ingredientIds': [10, 10],
          'positiveTags': ['鲜', '清淡', '菌类', '家常', '和风'],
          'negativeTags': ['重油'],
          'description': '图鉴中的完整合成说明',
        },
        {
          'id': 102,
          'recipeId': 101,
          'name': '尚未解锁的长名称料理用于检验完整换行',
          'price': 45,
          'cooker': '蒸锅',
          'ingredients': ['香菇'],
          'ingredientIds': [10],
          'positiveTags': <String>[],
          'negativeTags': <String>[],
        },
        {'id': 103, 'recipeId': 9003, 'name': '字段尚不完整的料理'},
      ],
      'ingredients': [
        {'id': 10, 'name': '香菇'},
      ],
      'beverages': [
        {
          'id': 201,
          'name': '零库存已解锁茶',
          'price': 10,
          'tags': ['无酒精', '清爽'],
        },
        {
          'id': 202,
          'name': '有库存未解锁酒',
          'price': 36,
          'tags': ['低酒精'],
        },
      ],
    };
    snapshot = {
      'status': 'ready',
      // 故障用例会注入 null；显式使用协议允许的可空值，避免样本字面量推断成 Map<String, Object>。
      'recommendationState': <String, Object?>{
        'inventoryReadReady': true,
        'availableRecipeIds': [101],
        'beverageUnlockReadReady': true,
        'unlockedBeverageIds': [201],
        'availableBeverageIds': [202],
        'ownedBeverageQty': {'201': 0, '202': -1},
      },
    };
  }

  @override
  Map<String, DataMap> get gameMediaRecipes => {
    '101:9001': {
      'ingredientIds': [10, 10],
      'cookerType': 1,
    },
    // 错误地用 Food.id 单独联结时，会误拿到这一份厨具。
    '101:101': {
      'ingredientIds': [99, 99],
      'cookerType': 5,
    },
  };

  @override
  List<DataMap> get gameMediaItems => [
    {'kind': 'food', 'id': 101, 'name': '合成香菇清汤'},
    {'kind': 'beverage', 'id': 202, 'name': '只在资源画廊首次查看的合成酒水'},
  ];
}

Future<void> _host(
  WidgetTester tester,
  _CatalogModel model, {
  Size size = const Size(627, 843),
  double scale = 1,
  PageActivityChannel? channel,
  bool active = true,
  ImageProvider<Object>? Function(String namespace, int id)? resolver,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  final theme = stewardTheme(dark: false, touch: !model.desktop);
  Widget page = DataPage(model, active: active);
  if (channel != null) page = PageActivityScope(channel: channel, child: page);
  if (resolver != null)
    page = GameMediaScope(resolveImage: resolver, child: page);
  await tester.pumpWidget(
    material.MaterialApp(
      localizationsDelegates: FLocalizations.localizationsDelegates,
      supportedLocales: FLocalizations.supportedLocales,
      theme: theme.toApproximateMaterialTheme(),
      home: FTheme(
        data: theme,
        child: MediaQuery(
          data: MediaQueryData(
            size: size,
            textScaler: TextScaler.linear(scale),
          ),
          child: Padding(
            // 与正式壳一致：最窄 Windows 正文扣除104/24；手机正文两侧各16。
            padding: model.desktop
                ? const EdgeInsets.fromLTRB(104, 12, 24, 12)
                : const EdgeInsets.all(16),
            child: page,
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

Future<void> _choose(WidgetTester tester, String group, String text) async {
  final target = find.descendant(
    of: find.byKey(ValueKey(group)),
    matching: find.text(text),
  );
  await tester.ensureVisible(target);
  await tester.pumpAndSettle();
  await tester.tap(target);
  await tester.pumpAndSettle();
}

Finder _input(String kind) => find.descendant(
  of: find.byKey(ValueKey('catalog-search-$kind')),
  matching: find.byType(EditableText),
);

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    await loadPreferredStewardTestFont();
  });

  testWidgets('数据页及子页首次访问前不解析图片，轮询不会挂载未访问画廊', (tester) async {
    final model = _CatalogModel();
    addTearDown(model.dispose);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final calls = <String>[];
    ImageProvider<Object>? resolve(String namespace, int id) {
      calls.add('$namespace:$id');
      return null;
    }

    await _host(tester, model, active: false, resolver: resolve);
    model.publish();
    await tester.pumpAndSettle();
    expect(calls, isEmpty);
    expect(find.byType(EntityImage, skipOffstage: false), findsNothing);
    expect(
      find.byType(ResourceManagementPage, skipOffstage: false),
      findsNothing,
    );

    await _host(tester, model, resolver: resolve);
    expect(calls, contains('food:101'));
    expect(calls.toSet(), {'food:101', 'ingredient:10', 'cooker:1'});
    expect(
      find.byKey(
        const ValueKey('catalog-search-beverage'),
        skipOffstage: false,
      ),
      findsNothing,
    );
    expect(
      find.byType(ResourceManagementPage, skipOffstage: false),
      findsNothing,
    );

    // 离开主数据页之后出现新的酒水事实，不能使从未访问的酒水/画廊发出图片请求。
    await _host(tester, model, active: false, resolver: resolve);
    model.catalog['beverages'] = [
      ...model.catalogItems('beverages'),
      {'id': 203, 'name': '新出现的合成茶'},
    ];
    model.runtime['unlockedBeverageIds'] = [201, 203];
    calls.clear();
    model.publish();
    await tester.pumpAndSettle();
    expect(calls.every((key) => !key.startsWith('beverage:')), isTrue);
    expect(
      find.byType(ResourceManagementPage, skipOffstage: false),
      findsNothing,
    );

    await _host(tester, model, resolver: resolve);
    await _choose(tester, 'data-tabs', '资源管理');
    expect(calls, contains('beverage:202'));
    expect(find.byType(ResourceManagementPage), findsOneWidget);
    expect(
      find.byKey(
        const ValueKey('catalog-search-beverage'),
        skipOffstage: false,
      ),
      findsNothing,
    );
    await _choose(tester, 'data-tabs', '酒水');
    expect(calls, containsAll(['beverage:201', 'beverage:203']));
    expect(model.reads, isEmpty);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  test('料理只认真实解锁集合中的 Food.id，酒水不把库存当图鉴', () {
    final model = _CatalogModel();
    addTearDown(model.dispose);
    final recipes = model.catalogItems('recipes');
    final food = CatalogUnlockFacts.read(model, food: true);
    expect(food.stateFor(recipes[0]), CatalogUnlockState.unlocked);
    expect(food.stateFor(recipes[1]), CatalogUnlockState.locked);
    final beverage = CatalogUnlockFacts.read(model, food: false);
    expect(
      beverage.stateFor(model.catalogItems('beverages')[0]),
      CatalogUnlockState.unlocked,
    );
    expect(
      beverage.stateFor(model.catalogItems('beverages')[1]),
      CatalogUnlockState.locked,
    );
    model.runtime.remove('beverageUnlockReadReady');
    expect(CatalogUnlockFacts.read(model, food: false).known, isFalse);
    model.runtime['inventoryReadReady'] = false;
    model.runtime['availableRecipeIds'] = [101, 102, 103];
    expect(CatalogUnlockFacts.read(model, food: true).known, isFalse);
  });

  test('缺失、损坏、断线和不完整目录不能把未知判为未解锁', () {
    final model = _CatalogModel();
    addTearDown(model.dispose);
    for (final invalid in <Object?>[
      null,
      ['101'],
      [101, -2],
      [101.0],
      '101',
    ]) {
      model.runtime['availableRecipeIds'] = invalid;
      final facts = CatalogUnlockFacts.read(model, food: true);
      expect(facts.known, isFalse);
      expect(facts.stateFor({'id': 102}), CatalogUnlockState.unknown);
    }
    model.runtime['availableRecipeIds'] = <int>[];
    expect(
      CatalogUnlockFacts.read(model, food: true).stateFor({'id': 101}),
      CatalogUnlockState.locked,
    );
    model.connected = false;
    expect(CatalogUnlockFacts.read(model, food: true).known, isFalse);
    model.connected = true;
    model.snapshot['status'] = 'error';
    expect(CatalogUnlockFacts.read(model, food: true).known, isFalse);
    model.snapshot['status'] = 'ready';
    model.catalog['isComplete'] = false;
    expect(CatalogUnlockFacts.read(model, food: true).known, isFalse);
  });

  test('目录适配只保留静态事实、双编号图像和重复材料，不伪造库存与匹配', () {
    final model = _CatalogModel();
    addTearDown(model.dispose);
    final view = catalogItemView(
      model,
      model.catalogItems('recipes').first,
      food: true,
    );
    expect(view.entityId, 101);
    expect(view.cookerId, 1);
    expect(view.ingredients!.map((item) => item.entityId), [10, 10]);
    expect(view.ingredients!.map((item) => item.name), ['香菇', '香菇']);
    expect(view.ingredients!.every((item) => item.stock == null), isTrue);
    expect(view.stock, isNull);
    expect(view.activeTags, isEmpty);
    expect(view.suppressedTags, isEmpty);
    expect(view.summaryTagLabel, '标签');
    expect(view.facts.map((fact) => fact.label), ['标价', '等级']);
    final unknown = catalogItemView(
      model,
      model.catalogItems('recipes').last,
      food: true,
    );
    expect(unknown.ingredients, isNull);
    expect(unknown.facts, isEmpty);
    model.catalog['recipes'] = [
      ...model.catalogItems('recipes'),
      {...model.catalogItems('recipes').first},
    ];
    final ambiguous = catalogItemView(
      model,
      model.catalogItems('recipes').first,
      food: true,
    );
    expect(ambiguous.entityId, isNull);
    expect(ambiguous.cookerId, isNull);
    expect(
      ambiguous.ingredients!.every((item) => item.entityId == null),
      isTrue,
    );
  });

  testWidgets('默认已解锁、名称搜索与未解锁/全部交叉筛选，第三页复用资源管理', (tester) async {
    final model = _CatalogModel();
    addTearDown(model.dispose);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    await _host(tester, model);
    expect(find.text('合成香菇清汤'), findsOneWidget);
    expect(find.text('尚未解锁的长名称料理用于检验完整换行'), findsNothing);
    await _choose(tester, 'catalog-filter-food', '未解锁');
    expect(find.text('合成香菇清汤'), findsNothing);
    expect(find.text('尚未解锁的长名称料理用于检验完整换行'), findsOneWidget);
    await tester.enterText(_input('food'), ' 字段尚 ');
    await tester.pumpAndSettle();
    expect(find.text('字段尚不完整的料理'), findsOneWidget);
    await _choose(tester, 'catalog-filter-food', '全部');
    expect(find.text('字段尚不完整的料理'), findsOneWidget);
    await tester.enterText(_input('food'), '9001');
    await tester.pumpAndSettle();
    expect(find.text('合成香菇清汤'), findsOneWidget);
    expect(find.text('尚未解锁的长名称料理用于检验完整换行'), findsNothing);
    await tester.enterText(_input('food'), ' 字段尚 ');
    await tester.pumpAndSettle();
    await _choose(tester, 'data-tabs', '酒水');
    expect(find.text('零库存已解锁茶'), findsOneWidget);
    expect(find.text('有库存未解锁酒'), findsNothing);
    await _choose(tester, 'data-tabs', '资源管理');
    expect(find.byType(ResourceManagementPage), findsOneWidget);
    expect(find.byKey(const ValueKey('resource-media-search')), findsOneWidget);
    await _choose(tester, 'data-tabs', '料理');
    expect(
      tester.widget<EditableText>(_input('food')).controller.text,
      ' 字段尚 ',
    );
    expect(model.reads, isEmpty);
    expect(model.writes, isEmpty);
  });

  testWidgets('未知解锁明确提示且全部可读，隐藏页不向页头发布资源状态', (tester) async {
    final model = _CatalogModel();
    final channel = PageActivityChannel();
    addTearDown(model.dispose);
    addTearDown(channel.dispose);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    model.runtime.remove('unlockedBeverageIds');
    await _host(tester, model, channel: channel);
    expect(channel.value?.message, '显示 1 / 3 项料理');
    await _choose(tester, 'data-tabs', '酒水');
    expect(channel.value?.message, '酒水解锁状态未确认');
    expect(find.text('酒水解锁状态未确认，可切换“全部”查看目录'), findsOneWidget);
    await _choose(tester, 'catalog-filter-beverage', '全部');
    expect(find.text('零库存已解锁茶'), findsOneWidget);
    expect(find.text('有库存未解锁酒'), findsOneWidget);
    expect(find.text('解锁状态未知'), findsNWidgets(2));
    await _host(tester, model, channel: channel, active: false);
    expect(channel.value, isNull);
  });

  testWidgets('轮询保留中文输入组合与控制器，切页保留草稿且隐藏详情不拦返回', (tester) async {
    final model = _CatalogModel(desktop: false);
    addTearDown(model.dispose);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    await _host(tester, model, size: const Size(390, 800));
    final controller = tester.widget<EditableText>(_input('food')).controller;
    controller.value = const TextEditingValue(
      text: '合成',
      selection: TextSelection.collapsed(offset: 2),
      composing: TextRange(start: 0, end: 2),
    );
    final before = controller.value;
    model.publish();
    await tester.pumpAndSettle();
    expect(
      identical(
        tester.widget<EditableText>(_input('food')).controller,
        controller,
      ),
      isTrue,
    );
    expect(controller.value, before);
    await tester.tap(find.byKey(const ValueKey('catalog-food:101:9001#0')));
    await tester.pumpAndSettle();
    expect(find.text('图鉴中的完整合成说明'), findsOneWidget);
    expect(find.text('已抵消 重油'), findsNothing);
    expect(find.text('负面'), findsOneWidget);
    expect(find.text('加料'), findsNothing);
    final detailPop = tester.widget<PopScope<Object?>>(
      find.byType(PopScope<Object?>),
    );
    expect(detailPop.canPop, isFalse);
    await _choose(tester, 'data-tabs', '酒水');
    final hiddenPop = tester.widget<PopScope<Object?>>(
      find.byType(PopScope<Object?>, skipOffstage: false),
    );
    expect(hiddenPop.canPop, isTrue);
    await _choose(tester, 'data-tabs', '料理');
    expect(find.text('图鉴中的完整合成说明'), findsOneWidget);
    expect(controller.text, '合成');
    expect(
      identical(
        tester.widget<EditableText>(_input('food')).controller,
        controller,
      ),
      isTrue,
    );
    await _host(tester, model, size: const Size(390, 800), active: false);
    expect(
      tester.widget<PopScope<Object?>>(find.byType(PopScope<Object?>)).canPop,
      isTrue,
    );
    expect(
      identical(
        tester.widget<EditableText>(_input('food')).controller,
        controller,
      ),
      isTrue,
    );
    expect(tester.takeException(), isNull);
  });

  for (final food in [true, false]) {
    testWidgets('图鉴${food ? '料理' : '酒水'}详情只显示一组完整标签', (tester) async {
      final model = _CatalogModel(desktop: false);
      addTearDown(model.dispose);
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      await _host(tester, model, size: const Size(390, 843));
      if (!food) await _choose(tester, 'data-tabs', '酒水');
      await tester.tap(
        find.byKey(
          ValueKey(
            food ? 'catalog-food:101:9001#0' : 'catalog-beverage:201:#0',
          ),
        ),
      );
      await tester.pumpAndSettle();
      // 断言详情区域而非隐藏列表；五项料理标签全部可见，不能去重后只剩摘要。
      final detail = find.byType(CatalogItemDetails);
      expect(detail, findsOneWidget);
      expect(
        find.descendant(of: detail, matching: find.text('标签')),
        findsOneWidget,
      );
      for (final tag in food ? ['鲜', '清淡', '菌类', '家常', '和风'] : ['无酒精', '清爽']) {
        expect(
          find.descendant(of: detail, matching: find.text(tag)),
          findsOneWidget,
        );
      }
      expect(
        find.descendant(of: detail, matching: find.text('+1')),
        findsNothing,
      );
      if (food) {
        expect(
          find.descendant(of: detail, matching: find.text('重油')),
          findsOneWidget,
        );
        expect(
          find.descendant(of: detail, matching: find.text('图鉴中的完整合成说明')),
          findsOneWidget,
        );
      }
      expect(tester.takeException(), isNull);
    });
  }

  for (final width in [1280.0, 627.0, 390.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('图鉴 $width / $scale 控件、图像和长名称可读且详情可达', (tester) async {
        final model = _CatalogModel(desktop: width >= 627);
        addTearDown(model.dispose);
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        await _host(tester, model, size: Size(width, 843), scale: scale);
        final images = find.byWidgetPredicate(
          (widget) =>
              widget is EntityImage &&
              widget.kind == EntityImageKind.food &&
              widget.id == 101,
        );
        expect(images, findsWidgets);
        for (final element in images.evaluate()) {
          final rect = tester.getRect(
            find.byElementPredicate(
              (candidate) => identical(candidate, element),
            ),
          );
          expect(rect.width, closeTo(28 * scale, .01));
          expect(rect.height, closeTo(28 * scale, .01));
        }
        await _choose(tester, 'catalog-filter-food', '未解锁');
        final name = find.text('尚未解锁的长名称料理用于检验完整换行').first;
        final list = find.byType(ListView).first;
        final nameRect = tester.getRect(name), listRect = tester.getRect(list);
        expect(nameRect.left, greaterThanOrEqualTo(listRect.left));
        expect(nameRect.right, lessThanOrEqualTo(listRect.right));
        expect(nameRect.bottom, lessThanOrEqualTo(listRect.bottom));
        await tester.tap(find.byKey(const ValueKey('catalog-food:102:101#0')));
        await tester.pumpAndSettle();
        expect(find.text('配方编号'), findsOneWidget);
        if (width < 1280) {
          await tester.tap(find.widgetWithText(StewardButton, '返回列表'));
          await tester.pumpAndSettle();
          expect(
            find.byKey(const ValueKey('catalog-food:102:101#0')),
            findsOneWidget,
          );
        }
        expect(tester.takeException(), isNull);
        expect(model.writes, isEmpty);
      });
    }
  }
}
