import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/recommendation_item_content.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/menu_item_content.dart';

import 'support/fixture_model.dart';

const _recipe = <String, Object?>{
  'id': 10,
  'recipeId': 999,
  'name': '合成料理',
  'price': 42,
  'cooker': '煮锅',
  'ingredients': ['豆腐'],
  'positiveTags': ['家常'],
};
const _beverage = <String, Object?>{
  'id': 30,
  'name': '合成酒水',
  'price': 12,
  'tags': ['清爽', '无酒精'],
};
const _extra = <String, Object?>{'id': 2, 'name': '蘑菇'};
const _normalFood = <String, Object?>{
  'recipe': _recipe,
  'ingredientCost': 8,
  'profit': 34,
  'activeTags': ['家常', '鲜'],
  'suppressedTags': ['重油'],
  'totalCoverage': 5,
  'coveredCustomerCount': 2,
  'matchedTags': ['家常'],
  'customerCoverage': [
    {
      'customerName': '合成普客',
      'matchedTagCount': 2,
      'matchedTags': ['家常', '鲜'],
    },
  ],
};
const _rareFood = <String, Object?>{
  'recipe': _recipe,
  'extraIngredients': [_extra],
  'allTags': ['家常', '鲜'],
  'cancelledTags': ['重油'],
  'baseCost': 8,
  'extraCost': 6,
  'meetsRequiredFood': true,
  'extraIngredientReasonTags': {
    '2': ['鲜'],
  },
};
const _savedFood = <String, Object?>{
  'id': 'saved-food',
  'customerId': 301,
  'customerName': '合成稀客',
  'foodTag': '家常',
  'recipeId': 10,
  'extraIngredientIds': [2],
};
const _custom = <String, Object?>{
  'id': 'custom-food',
  'mode': 'customer',
  'customerId': 301,
  'customerName': '合成稀客',
  'foodTag': null,
  'foodId': 10,
  'recipeId': 999,
  'recipeName': '保存时名称',
  'extraIngredientIds': [2, 2],
  'enabled': true,
  'pinToTop': true,
  'sortOrder': 100,
};

/// 故意放入交叉 ID 的另一份目录，区分料理 ID 与独立游戏配方 ID。
/// 请求只返回固定的 C# 形状，不在夹具中重新实现推荐计算。
class _RecommendationModel extends FixtureModel {
  @override
  int connectionVersion = 0;
  final bool missingEntityIds;
  _RecommendationModel({this.missingEntityIds = false}) : super(orderCount: 0) {
    catalog = {
      'recipes': [
        _recipe,
        {'id': 999, 'recipeId': 10, 'name': '错误联结料理', 'price': 777},
      ],
      'ingredients': [
        {'id': 1, 'name': '豆腐'},
        _extra,
      ],
      'beverages': [_beverage],
      'normalCustomers': [
        {
          'id': 101,
          'name': '合成普客',
          'places': ['妖怪兽道'],
        },
      ],
      'rareCustomers': [
        {
          'id': 301,
          'name': '合成稀客',
          'positiveTags': ['家常'],
          'beverageTags': ['清爽'],
        },
      ],
    };
    snapshot = {
      ...snapshot,
      'recommendationState': {
        'ownedIngredientQty': {'1': 5, '2': -1},
        'ownedBeverageQty': {'30': 4},
      },
    };
    resources = {
      '/favorites': {
        'recipes': [_savedFood],
        'beverages': [
          {
            'id': 'saved-beverage',
            'customerId': 301,
            'customerName': '合成稀客',
            'beverageId': 30,
            'beverageTag': '清爽',
          },
        ],
      },
      '/custom-recipes': {
        'version': 2,
        'activeMode': 'customer',
        'modeRevision': 0,
        'enabled': true,
        'recipes': [_custom],
      },
    };
  }
  @override
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  }) async {
    pageResult = {
      'recipes': [
        {
          ...(kind == 'normal' ? _normalFood : _rareFood),
          if (missingEntityIds) 'recipe': {..._recipe, 'id': null},
        },
      ],
      'beverages': [
        kind == 'normal'
            ? {
                'beverage': {..._beverage, if (missingEntityIds) 'id': null},
                'activeTags': ['清爽'],
                'totalCoverage': 3,
                'coveredCustomerCount': 2,
              }
            : {
                'beverage': {..._beverage, if (missingEntityIds) 'id': null},
                'matchedTags': ['清爽'],
                'meetsRequiredBev': true,
              },
      ],
    };
    publish();
  }
}

Map<String, String> _facts(MenuItemView item) => {
  for (final fact in item.facts) fact.label: fact.value,
};

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
    await (FontLoader('packages/forui_lucide/ForuiLucideIcons')
          ..addFont(rootBundle.load('packages/forui_lucide/assets/lucide.ttf')))
        .load();
  });

  test('普客采用C#成本利润，命中标签数与覆盖人数分别展示', () {
    final model = _RecommendationModel();
    addTearDown(model.dispose);
    final item = recommendationItemView(
      model,
      _normalFood,
      section: RecommendationSection.normal,
      food: true,
    );
    expect(_facts(item), {
      '标价': '42',
      '材料成本': '8',
      '利润': '34',
      '命中标签数': '5',
      '覆盖客人数': '2',
    });
    expect(item.extras, isEmpty);
    expect(item.ingredients!.single.name, '豆腐');
    expect(item.ingredients!.single.stock, '5');
    expect(item.activeTags, ['家常', '鲜']);
    expect(item.summaryTagLabel, '匹配');
    expect(item.summaryTags, ['家常']);
  });

  test('稀客料理按行DTO显示加料成本和状态，不计算总成本或利润', () {
    final model = _RecommendationModel();
    addTearDown(model.dispose);
    final item = recommendationItemView(
      model,
      {
        ..._rareFood,
        'customRecipe': true,
        'customRecipePinned': true,
        'customRecipeScope': 'all',
        'customRecipeSortOrder': 100,
      },
      section: RecommendationSection.rare,
      food: true,
    );
    expect(_facts(item), containsPair('基础成本', '8'));
    expect(_facts(item), containsPair('加料成本', '6'));
    expect(_facts(item), containsPair('适用', '全部点单'));
    expect(_facts(item), containsPair('自定义排序', '100'));
    expect(_facts(item).containsKey('利润'), isFalse);
    expect(item.extras!.single.stock, '无限');
    expect(item.suppressedTags, ['重油']);
    expect(item.summaryTagLabel, '生效');
    expect(item.summaryTags, ['家常', '鲜']);
  });

  test('稀客酒水只认meetsRequiredBev且不把匹配标签冒充生效标签', () {
    final model = _RecommendationModel();
    addTearDown(model.dispose);
    final item = recommendationItemView(
      model,
      {
        'beverage': _beverage,
        'matchedTags': ['清爽'],
        'meetsRequiredBev': false,
        'meetsRequiredBeverage': true,
      },
      section: RecommendationSection.rare,
      food: false,
    );
    expect(_facts(item), containsPair('酒水点单', '未满足'));
    expect(_facts(item), containsPair('匹配标签数', '1'));
    expect(item.activeTags, isEmpty);
    expect(item.stock, '4');
    expect(item.summaryTagLabel, '匹配');
    expect(item.summaryTags, ['清爽']);
  });

  test('收藏按food目录ID，自定义核对两个ID且无视先前酒水类别，重复加料保留', () {
    final model = _RecommendationModel();
    addTearDown(model.dispose);
    final favorite = recommendationItemView(
      model,
      _savedFood,
      section: RecommendationSection.favorites,
      food: true,
    );
    final custom = recommendationItemView(
      model,
      _custom,
      section: RecommendationSection.custom,
      food: false,
    );
    expect(favorite.name, '合成料理');
    expect(custom.name, '合成料理');
    expect(custom.kind, MenuItemKind.food);
    expect(custom.extras!.map((entry) => entry.name), ['蘑菇', '蘑菇']);
    expect(_facts(custom), containsPair('点单', '全部点单'));
    expect(_facts(custom).containsKey('利润'), isFalse);
    expect(custom.activeTags, isEmpty);
    expect(custom.summaryTagLabel, '标签');
    expect(custom.summaryTags, ['家常']);
    expect(favorite.summaryTags, ['家常']);
  });

  test('目录重复ID、配方编号错配和未完整读取均不错误补全', () {
    final model = _RecommendationModel();
    addTearDown(model.dispose);
    MenuItemView project(DataMap row) => recommendationItemView(
      model,
      row,
      section: RecommendationSection.custom,
      food: true,
    );
    expect(project({..._custom, 'recipeId': 10}).ingredients, isNull);
    model.catalog = {
      ...model.catalog,
      'recipes': [
        _recipe,
        {..._recipe, 'recipeId': 888},
      ],
    };
    final duplicate = project(_custom);
    expect(duplicate.name, '保存时名称');
    expect(duplicate.cooker, isNull);
    expect(_facts(duplicate)['标价'], '未知');
    model.catalog = {
      ...model.catalog,
      'recipes': [_recipe],
      'isComplete': false,
    };
    expect(project(_custom).ingredients, isNull);
  });

  test('基础材料同名不联结库存，未知加料不说不加料，非法库存不造零', () {
    final model = _RecommendationModel();
    addTearDown(model.dispose);
    model.catalog = {
      ...model.catalog,
      'ingredients': [
        {'id': 1, 'name': '豆腐'},
        {'id': 8, 'name': '豆腐'},
      ],
    };
    final item = recommendationItemView(
      model,
      {..._rareFood, 'extraIngredients': null},
      section: RecommendationSection.rare,
      food: true,
    );
    expect(item.ingredients!.single.stock, '未知');
    expect(item.extras, isNull);
    expect(menuStockText(-2), '未知');
    expect(menuStockText(double.infinity), '未知');
    expect(menuStockText(0), '0');
    expect(menuStockText(-1), '无限');
  });

  test('缺失和非法ID不能命中字典null或负ID的伪库存', () {
    final model = _RecommendationModel();
    addTearDown(model.dispose);
    model.snapshot = {
      ...model.snapshot,
      'recommendationState': {
        'ownedBeverageQty': {'null': 99, '-1': 99, '1.0': 99},
        'ownedIngredientQty': {'null': 99, '-1': 99},
      },
    };
    for (final id in [null, -1, 1.0]) {
      final item = recommendationItemView(
        model,
        {
          'beverage': {'id': id, 'name': '未知库存酒水'},
        },
        section: RecommendationSection.rare,
        food: false,
      );
      expect(item.stock, '未知');
      expect(_facts(item)['标价'], '未知');
      expect(_facts(item)['酒水点单'], '未知');
    }
    final item = recommendationItemView(
      model,
      {
        ..._rareFood,
        'extraIngredients': [
          {'id': -1, 'name': '未知食材'},
        ],
      },
      section: RecommendationSection.rare,
      food: true,
    );
    expect(item.extras!.single.stock, '未知');
  });

  Future<void> open(
    WidgetTester tester,
    _RecommendationModel model, {
    double width = 1280,
    int scale = 100,
  }) async {
    model.localPreferences = {'fontScalePercent': scale};
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(model.dispose);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('nav-2')));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
  }

  Future<void> tap(WidgetTester tester, String text) async {
    final target = find.text(text).last;
    await tester.ensureVisible(target);
    await tester.pumpAndSettle();
    await tester.tap(target);
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
  }

  MenuItemContent outer(WidgetTester tester) =>
      tester.widgetList<MenuItemContent>(find.byType(MenuItemContent)).single;

  void entityNames(
    WidgetTester tester, {
    required bool food,
    required int count,
    required int scale,
    int? id,
  }) {
    final name = food ? '合成料理' : '合成酒水';
    final names = find.byWidgetPredicate(
      (widget) => widget is EntityName && widget.name == name,
    );
    // 按真实页面中的完整名称行检查，而非只检查适配模型有ID。旧版在summary关闭
    // 名称后，外层与Section标题都会绕过EntityName，即使DTO正确也完全没有图片。
    expect(names, findsNWidgets(count));
    for (final element in names.evaluate()) {
      final widget = element.widget as EntityName;
      expect(
        widget.kind,
        food ? EntityImageKind.food : EntityImageKind.beverage,
      );
      expect(widget.id, id);
      final image = find.descendant(
        of: find.byWidget(widget),
        matching: find.byType(EntityImage),
      );
      expect(image, findsOneWidget);
      expect(tester.getSize(image), Size.square(28 * scale / 100));
      // 本夹具没有资源Scope，所有名称都必须保留同尺寸fallback，缺ID也不能退成纯文字。
      expect(
        find.descendant(of: image, matching: find.byType(Icon)),
        findsOneWidget,
      );
    }
  }

  for (final width in [1280.0, 640.0, 390.0]) {
    for (final scale in [100, 150]) {
      testWidgets('四类页面统一完整外层及酒水→自定义类别 $width/$scale', (tester) async {
        final model = _RecommendationModel();
        await open(tester, model, width: width, scale: scale);
        expect(outer(tester).item.ingredients!.single.name, '豆腐');
        expect(_facts(outer(tester).item)['命中标签数'], '5');
        entityNames(
          tester,
          food: true,
          count: width >= 900 ? 2 : 1,
          scale: scale,
          id: 10,
        );
        await tap(tester, '酒水');
        expect(outer(tester).item.kind, MenuItemKind.beverage);
        expect(outer(tester).item.stock, '4');
        entityNames(
          tester,
          food: false,
          count: width >= 900 ? 2 : 1,
          scale: scale,
          id: 30,
        );
        await tap(tester, '稀客');
        expect(_facts(outer(tester).item)['酒水点单'], '满足');
        await tap(tester, '料理');
        expect(outer(tester).item.extras!.single.name, '蘑菇');
        expect(_facts(outer(tester).item)['加料成本'], '6');
        await tap(tester, '收藏');
        expect(outer(tester).item.name, '合成料理');
        entityNames(
          tester,
          food: true,
          count: width >= 900 ? 2 : 1,
          scale: scale,
          id: 10,
        );
        await tap(tester, '酒水');
        expect(outer(tester).item.name, '合成酒水');
        entityNames(
          tester,
          food: false,
          count: width >= 900 ? 2 : 1,
          scale: scale,
          id: 30,
        );
        await tap(tester, '自定义配方');
        // 新版以客人/料理分组，窄屏先进入该主体的绑定名单，完整菜品组件位于组内。
        if (width < 900) {
          await tester.tap(find.byKey(const ValueKey('customer:301')));
          await tester.pumpAndSettle();
        }
        expect(outer(tester).item.kind, MenuItemKind.food);
        expect(outer(tester).item.name, '合成料理');
        expect(outer(tester).item.extras, hasLength(2));
        expect(find.text('错误联结料理'), findsNothing);
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
      });
    }
  }

  testWidgets('缺少实体ID的推荐外层与窄屏详情仍统一图文名称，不用游戏配方编号猜图', (tester) async {
    final model = _RecommendationModel(missingEntityIds: true);
    await open(tester, model, width: 640, scale: 150);
    entityNames(tester, food: true, count: 1, scale: 150);
    await tap(tester, '合成料理');
    entityNames(tester, food: true, count: 1, scale: 150);
    expect(
      find.byType(MenuItemContent),
      findsNothing,
      reason: '详情保持增量，不复制整张外层卡片',
    );
    await tap(tester, '酒水');
    entityNames(tester, food: false, count: 1, scale: 150);
    await tap(tester, '合成酒水');
    entityNames(tester, food: false, count: 1, scale: 150);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('详情仅保留增量说明，收藏移除载荷保持原存储ID', (tester) async {
    final model = _RecommendationModel();
    await open(tester, model);
    final details = find.byKey(const ValueKey('dense-detail-surface'));
    expect(
      find.descendant(of: details, matching: find.byType(MenuItemContent)),
      findsNothing,
    );
    expect(
      find.descendant(of: details, matching: find.byType(MenuItemDetails)),
      findsOneWidget,
    );
    await tap(tester, '收藏');
    await tap(tester, '移除收藏');
    expect(model.writes.last.$1, '/favorites/remove-recipe');
    expect(model.writes.last.$2, {'id': 'saved-food'});
    await tap(tester, '酒水');
    await tap(tester, '移除收藏');
    expect(model.writes.last.$1, '/favorites/remove-beverage');
    expect(model.writes.last.$2, {'id': 'saved-beverage'});
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('只读设备保留完整主体而不可编辑，删除确认发生重连不提交', (tester) async {
    final model = _RecommendationModel();
    await open(tester, model);
    await tap(tester, '自定义配方');
    model.authority = {...model.authority, 'currentDeviceIsPrimary': false};
    model.publish();
    await tester.pumpAndSettle();
    for (final title in ['编辑', '停用', '取消置顶', '上移', '下移']) {
      final button = tester.widget<StewardButton>(
        find.byWidgetPredicate(
          (widget) => widget is StewardButton && widget.text == title,
        ),
      );
      expect(button.onPressed, isNull);
    }
    expect(outer(tester).item.ingredients!.single.name, '豆腐');
    model.authority = {...model.authority, 'currentDeviceIsPrimary': true};
    model.publish();
    await tester.pumpAndSettle();
    await tap(tester, '删除');
    model.connectionVersion++;
    model.publish();
    await tester.pumpAndSettle();
    await tap(tester, '确认');
    expect(model.writes, isEmpty);
    await tester.pumpWidget(const SizedBox());
  });
}
