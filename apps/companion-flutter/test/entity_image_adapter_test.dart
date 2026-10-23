import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/menu_recipe_media.dart';
import 'package:mystia_steward_companion/features/recommendation_item_content.dart';
import 'package:mystia_steward_companion/features/service_menu_adapter.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/menu_item_content.dart';
import 'package:mystia_steward_companion/ui/steward_style.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 仅注入当前资源代次的只读索引；撤回索引等价于断线或切换游戏来源，不依赖静态资产。
class _MediaModel extends FixtureModel {
  @override
  Map<String, DataMap> gameMediaRecipes = {};
}

void main() {
  setUpAll(() async {
    final font = Platform.environment['MYSTIA_UI_FONT'];
    if (font != null) {
      await (FontLoader('Microsoft YaHei')..addFont(
            Future.value(ByteData.sublistView(await File(font).readAsBytes())),
          ))
          .load();
    }
  });

  testWidgets('经营头像读取规范身份，稀客与客群独立且特殊普客角色不猜身份', (tester) async {
    final theme = stewardTheme(dark: false, touch: false);
    await tester.pumpWidget(
      material.MaterialApp(
        home: FTheme(
          data: theme,
          child: const Column(
            children: [
              ServiceOrderHeading({
                'order': {
                  'deskCode': 0,
                  'guestId': 42,
                  'runtimeGuestId': 9999,
                  'guestName': '稀客',
                },
              }, rare: true),
              ServiceOrderHeading({
                'order': {
                  'deskCode': 1,
                  'guestId': 42,
                  'runtimeGuestId': 8888,
                  'guestImageKind': 'normal-group',
                  'guestImageId': 42,
                  'guestName': '客群',
                },
              }, rare: false),
              ServiceOrderHeading({
                'order': {
                  'deskCode': 2,
                  'guestId': 42,
                  'specialBusinessRole': 'unknown-special',
                  'guestName': '特殊角色',
                },
              }, rare: false),
            ],
          ),
        ),
      ),
    );
    final images = tester
        .widgetList<EntityImage>(find.byType(EntityImage))
        .toList();
    expect(images.map((image) => (image.kind, image.id)), [
      (EntityImageKind.rareGuest, 42),
      (EntityImageKind.normalGroup, 42),
      (EntityImageKind.normalGroup, null),
    ]);
    expect(find.text('1 桌 · 稀客'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
  });

  test('经营保留Food身份与重复显式食材，名称联结只用于旧库存', () {
    final model = FixtureModel();
    addTearDown(model.dispose);
    final view = serviceFoodView(model, {
      'recipe': {
        'id': 41,
        'recipeId': 9001,
        'name': '料理',
        'ingredients': ['蘑菇'],
      },
      'extraIngredients': [
        {'id': 2, 'name': '蘑菇'},
        {'id': 2, 'name': '蘑菇'},
      ],
    });
    expect(view.entityId, 41);
    expect(view.ingredients!.single.entityId, isNull);
    expect(view.ingredients!.single.stock, '3');
    expect(view.extras!.map((item) => item.entityId), [2, 2]);
    final normal = serviceFoodView(model, {
      'foodId': 41,
      'name': '料理',
      'recipe': {'id': 41, 'recipeId': 9001},
      'baseIngredientNames': ['蘑菇'],
    }, normal: true);
    expect(normal.entityId, 41);
    expect(
      serviceBeverageView(model, {'beverageId': 7}, normal: true).entityId,
      7,
    );
    expect(
      serviceBeverageView(model, {
        'beverage': {'id': 8},
      }).entityId,
      8,
    );
  });

  test('配方图片只在两个身份唯一联结时使用，保持依赖顺序及重复项', () {
    final model = _MediaModel();
    addTearDown(model.dispose);
    model.catalog = {
      'isComplete': true,
      'recipes': [
        {'id': 41, 'recipeId': 9001},
      ],
    };
    expect(menuRecipeMediaKey(model, {'id': 41, 'recipeId': 9001}), '41:9001');
    expect(menuRecipeMediaKey(model, {'id': 41, 'recipeId': 41}), isNull);
    const input = [
      MenuIngredientView('甲'),
      MenuIngredientView('甲'),
      MenuIngredientView('乙'),
    ];
    model.gameMediaRecipes = {
      '41:9001': {
        'ingredientIds': [2, 2, 5],
        'cookerType': 3,
      },
    };
    final result = menuRecipeIngredientImages(model, input, '41:9001')!;
    expect(result.map((item) => item.entityId), [2, 2, 5]);
    expect(result.map((item) => item.name), ['甲', '甲', '乙']);
    expect(menuRecipeCookerImage(model, '41:9001'), 3);
    // 不匹配的长度、数值字符串、浮点及负数都不能成为图片身份，旧文字原样返回。
    for (final ids in [
      [2],
      [2, '2', 5],
      [2, 2.0, 5],
      [2, -1, 5],
      null,
    ]) {
      model.gameMediaRecipes = {
        '41:9001': {'ingredientIds': ids, 'cookerType': 99},
      };
      expect(
        identical(menuRecipeIngredientImages(model, input, '41:9001'), input),
        isTrue,
      );
      expect(menuRecipeCookerImage(model, '41:9001'), isNull);
    }
    model.gameMediaRecipes = {};
    expect(
      identical(menuRecipeIngredientImages(model, input, '41:9001'), input),
      isTrue,
    );
    expect(menuRecipeCookerImage(model, '41:9001'), isNull);
    model.catalog['recipes'] = [
      {'id': 41, 'recipeId': 9001},
      {'id': 41, 'recipeId': 9001},
    ];
    expect(menuRecipeMediaKey(model, {'id': 41, 'recipeId': 9001}), isNull);
  });

  for (final scale in [1.0, 1.5]) {
    testWidgets('加料说明按规范食材键显示图像，名称不猜ID且窄屏换行 $scale', (tester) async {
      final model = FixtureModel();
      addTearDown(model.dispose);
      model.catalog['ingredients'] = [
        {'id': 2, 'name': '同名材料'},
        {'id': 7, 'name': '目录中按精确编号取得的长食材名称'},
        {'id': 9, 'name': '重复编号甲'},
        {'id': 9, 'name': '重复编号乙'},
      ];
      final theme = stewardTheme(dark: false, touch: true);
      await tester.pumpWidget(
        material.MaterialApp(
          home: FTheme(
            data: theme,
            child: MediaQuery(
              data: MediaQueryData(textScaler: TextScaler.linear(scale)),
              child: Center(
                child: SizedBox(
                  width: 262,
                  height: 780,
                  child: SingleChildScrollView(
                    child: RecommendationItemExplanation(
                      model,
                      const {
                        'recipe': {'id': 41, 'recipeId': 9001, 'name': '料理'},
                        'extraIngredients': [
                          {'id': 2, 'name': '方案中的明确食材'},
                        ],
                        'extraIngredientReasonTags': {
                          '2': ['鲜'],
                          '7': ['家常'],
                          '9': ['清淡'],
                          '02': ['不接受前导零'],
                          '-1': ['不接受负数'],
                          '2.0': ['不接受小数'],
                          '同名材料': ['不按名称猜测'],
                        },
                      },
                      section: RecommendationSection.rare,
                      food: true,
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('加料说明'));
      await tester.pumpAndSettle();
      final group = find.byWidgetPredicate(
        (widget) => widget is CollapsibleSection && widget.title == '加料说明',
      );
      final images = tester.widgetList<EntityImage>(
        find.descendant(of: group, matching: find.byType(EntityImage)),
      );
      expect(images.map((entry) => (entry.kind, entry.id)), [
        (EntityImageKind.ingredient, 2),
        (EntityImageKind.ingredient, 7),
        (EntityImageKind.ingredient, 9),
        (EntityImageKind.ingredient, null),
        (EntityImageKind.ingredient, null),
        (EntityImageKind.ingredient, null),
        (EntityImageKind.ingredient, null),
      ]);
      expect(find.text('方案中的明确食材'), findsOneWidget);
      expect(find.text('目录中按精确编号取得的长食材名称'), findsOneWidget);
      expect(find.text('食材 #9'), findsOneWidget);
      expect(find.text('食材 #同名材料'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('普客详情按customerId显示客群，详情分组与前一组标签保留阅读间距 $scale', (tester) async {
      final model = FixtureModel();
      addTearDown(model.dispose);
      final theme = stewardTheme(dark: scale == 1.5, touch: true);
      await tester.pumpWidget(
        material.MaterialApp(
          home: FTheme(
            data: theme,
            child: MediaQuery(
              data: MediaQueryData(textScaler: TextScaler.linear(scale)),
              child: Center(
                child: SizedBox(
                  width: 262,
                  height: 780,
                  child: SingleChildScrollView(
                    child: RecommendationItemExplanation(
                      model,
                      const {
                        'recipe': {'id': 41, 'name': '合成料理'},
                        'matchedTags': ['合成匹配标签'],
                        'customerCoverage': [
                          {
                            'customerId': 42,
                            'customerName': '同名客群',
                            'matchedTagCount': 1,
                            'matchedTags': ['合成匹配标签'],
                          },
                          {
                            'customerName': '同名客群',
                            'matchedTagCount': 0,
                            'matchedTags': <String>[],
                          },
                        ],
                      },
                      section: RecommendationSection.normal,
                      food: true,
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      final detail = find.byWidgetPredicate(
        (widget) => widget is CollapsibleSection && widget.title == '客群匹配详情',
      );
      final tags = find.byType(MenuItemDetails);
      // 比较真实折叠按钮与上方详情的几何距离，不把Padding外壳本身当作留白证据。
      final button = find
          .descendant(of: detail, matching: find.byType(FButton))
          .first;
      expect(
        tester.getRect(button).top - tester.getRect(tags).bottom,
        greaterThanOrEqualTo(StewardDensity.detailGroupGap),
      );
      await tester.tap(find.text('客群匹配详情'));
      await tester.pumpAndSettle();
      final images = tester
          .widgetList<EntityImage>(
            find.descendant(of: detail, matching: find.byType(EntityImage)),
          )
          .toList();
      expect(images.map((image) => (image.kind, image.id)), [
        (EntityImageKind.normalGroup, 42),
        (EntityImageKind.normalGroup, null),
      ]);
      expect(
        tester.getSize(
          find.descendant(of: detail, matching: find.byType(EntityImage)).first,
        ),
        Size(28 * scale, 28 * scale),
      );
      expect(find.text('同名客群'), findsNWidgets(2));
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
    });
  }

  test('推荐收藏与自定义使用Food ID，不把独立recipeId或名称当图片身份', () {
    final model = FixtureModel();
    addTearDown(model.dispose);
    model.catalog = {
      'isComplete': true,
      'recipes': [
        {
          'id': 41,
          'recipeId': 9001,
          'name': '料理',
          'ingredients': ['蘑菇'],
        },
      ],
      'ingredients': [
        {'id': 2, 'name': '蘑菇'},
      ],
    };
    final favorite = recommendationItemView(
      model,
      {
        'recipeId': 41,
        'extraIngredientIds': [2, 2],
      },
      section: RecommendationSection.favorites,
      food: true,
    );
    expect(favorite.entityId, 41);
    expect(favorite.ingredients!.single.entityId, isNull);
    expect(favorite.extras!.map((item) => item.entityId), [2, 2]);
    final custom = recommendationItemView(
      model,
      {'foodId': 41, 'recipeId': 9001},
      section: RecommendationSection.custom,
      food: true,
    );
    expect(custom.entityId, 41);
    final invalid = recommendationItemView(
      model,
      {'foodId': 41, 'recipeId': 41},
      section: RecommendationSection.custom,
      food: true,
    );
    expect(invalid.entityId, isNull);
  });
}
