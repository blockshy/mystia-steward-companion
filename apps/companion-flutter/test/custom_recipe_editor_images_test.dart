import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/custom_recipes.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/game_media_scope.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import '../tool/preview/rich_preview_workbench_model.dart';

/// 同名食材与交叉配方编号用于暴露按名称、单一编号猜图的问题。检查实际组件收到的
/// 精确身份，图片仓库返回null以走产品真实fallback；不联网、不读取或打包游戏图片。
class _EditorImageModel extends RichPreviewWorkbenchModel {
  _EditorImageModel({super.desktop}) {
    catalog = {
      'isComplete': true,
      'recipes': [
        {
          'id': 101,
          'recipeId': 9001,
          'name': '合成料理',
          'cooker': '煮锅',
          'ingredients': ['同名食材', '同名食材', '同名食材'],
        },
        {
          'id': 9001,
          'recipeId': 101,
          'name': '另一份料理',
          'cooker': '蒸锅',
          'ingredients': ['追加食材'],
        },
      ],
      'rareCustomers': [
        {
          'id': 301,
          'name': '合成稀客',
          'positiveTags': ['鲜'],
        },
      ],
      'ingredients': [
        {'id': 1, 'name': '同名食材'},
        {'id': 2, 'name': '同名食材'},
        {'id': 7, 'name': '追加食材'},
        {'id': 8, 'name': '歧义甲'},
        {'id': 8, 'name': '歧义乙'},
        {'id': -1, 'name': '非法编号'},
      ],
    };
  }
  @override
  Map<String, DataMap> gameMediaRecipes = {
    '101:9001': {
      'ingredientIds': [1, 1, 2],
      'cookerType': 1,
    },
    '9001:101': {
      'ingredientIds': [7],
      'cookerType': 4,
    },
  };
}

const _binding = <String, Object?>{
  'id': 'synthetic-binding',
  'mode': 'customer',
  'customerId': 301,
  'customerName': '合成稀客',
  'foodId': 101,
  'recipeId': 9001,
  'recipeName': '合成料理',
  'foodTag': null,
  'extraIngredientIds': [7, 9999],
  'enabled': true,
  'pinToTop': false,
};

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null) {
      final bytes = await File(path).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });

  Future<void> open(
    WidgetTester tester,
    _EditorImageModel model, {
    bool editing = true,
    double width = 640,
    double scale = 1,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 900);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(model.dispose);
    final theme = stewardTheme(dark: false, touch: !model.desktop);
    await tester.pumpWidget(
      material.MaterialApp(
        localizationsDelegates: FLocalizations.localizationsDelegates,
        supportedLocales: FLocalizations.supportedLocales,
        builder: (context, child) => FTheme(
          data: theme,
          child: MediaQuery(
            data: MediaQuery.of(context)
                .copyWith(textScaler: TextScaler.linear(scale)),
            child: GameMediaScope(resolveImage: (_, _) => null, child: child!),
          ),
        ),
        home: CustomRecipeEditor(
          model,
          mode: 'customer',
          existing: editing ? _binding : const {},
          seed: editing
              ? const {}
              : {..._binding, 'extraIngredientIds': <int>[]},
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Finder imageIn(Finder parent) =>
      find.descendant(of: parent, matching: find.byType(EntityImage));
  Finder option(int id) => find.byWidgetPredicate(
    (widget) => widget is FSelectItem<int> && widget.value == id,
  );
  void image(
    WidgetTester tester,
    Finder parent,
    EntityImageKind kind,
    int? id,
    double extent,
  ) {
    final finder = imageIn(parent);
    expect(finder, findsOneWidget);
    final widget = tester.widget<EntityImage>(finder);
    expect(widget.kind, kind);
    expect(widget.id, id);
    expect(tester.getSize(finder), Size.square(extent));
    expect(
      find.descendant(of: finder, matching: find.byType(Icon)),
      findsOneWidget,
    );
  }

  for (final editing in [false, true]) {
    for (final (width, scale, desktop) in [
      (640.0, 1.0, true),
      (390.0, 1.5, false),
    ]) {
      testWidgets(
        '${editing ? '编辑' : '新增'}配方 $width/$scale：料理、厨具、基础材料、追加候选及名单共用图片身份',
        (tester) async {
          final model = _EditorImageModel(desktop: desktop);
          await open(
            tester,
            model,
            editing: editing,
            width: width,
            scale: scale,
          );
          image(
            tester,
            find.byKey(const ValueKey('custom-editor-cooker')),
            EntityImageKind.cooker,
            1,
            20 * scale,
          );
          final base = imageIn(
            find.byKey(const ValueKey('custom-editor-base-ingredients')),
          );
          expect(tester.widgetList<EntityImage>(base).map((item) => item.id), [
            1,
            1,
            2,
          ]);
          final food = find.byKey(const ValueKey('custom-editor-foodId'));
          await tester.ensureVisible(food);
          await tester.tap(food);
          await tester.pumpAndSettle();
          image(tester, option(101), EntityImageKind.food, 101, 20 * scale);
          await tester.tap(option(101));
          await tester.pumpAndSettle();
          final add = find.byKey(const ValueKey('custom-editor-extra'));
          await tester.ensureVisible(add);
          await tester.pumpAndSettle();
          final options = tester.widget<StewardSelect<int>>(add).items;
          expect(options.containsKey(8), isFalse);
          expect(options.containsKey(-1), isFalse);
          expect(options.containsKey(7), !editing);
          await tester.tap(add);
          await tester.pumpAndSettle();
          image(tester, option(2), EntityImageKind.ingredient, 2, 20 * scale);
          await tester.ensureVisible(option(2));
          await tester.tap(option(2));
          await tester.pumpAndSettle();
          final selected = find.byKey(const ValueKey(('custom-extra-name', 2)));
          await tester.ensureVisible(selected);
          await tester.pumpAndSettle();
          image(tester, selected, EntityImageKind.ingredient, 2, 28 * scale);
          expect(tester.getRect(selected).right, lessThanOrEqualTo(width));
          if (editing) {
            // 历史未知ID保留自身身份和移除按钮，不能联结到另一个同名食材。
            final unknown = find.byKey(
              const ValueKey(('custom-extra-name', 9999)),
            );
            await tester.ensureVisible(unknown);
            await tester.pumpAndSettle();
            image(
              tester,
              unknown,
              EntityImageKind.ingredient,
              9999,
              28 * scale,
            );
            await tester.tap(
              find.byKey(const ValueKey(('custom-extra-remove', 9999))),
            );
            await tester.pumpAndSettle();
            expect(unknown, findsNothing);
          }
          expect(tester.takeException(), isNull);
          await tester.pumpWidget(const SizedBox.shrink());
        },
      );
    }
  }

  testWidgets('资源索引撤回后基础材料和厨具只回退图标，显式追加ID及草稿不变', (tester) async {
    final model = _EditorImageModel();
    await open(tester, model);
    model.gameMediaRecipes = {};
    model.notifyListeners();
    await tester.pumpAndSettle();
    image(
      tester,
      find.byKey(const ValueKey('custom-editor-cooker')),
      EntityImageKind.cooker,
      null,
      20,
    );
    final base = imageIn(
      find.byKey(const ValueKey('custom-editor-base-ingredients')),
    );
    expect(tester.widgetList<EntityImage>(base).map((item) => item.id), [
      null,
      null,
      null,
    ]);
    final selected = find.byKey(const ValueKey(('custom-extra-name', 7)));
    await tester.ensureVisible(selected);
    await tester.pumpAndSettle();
    image(tester, selected, EntityImageKind.ingredient, 7, 28);
    // 换成查看设备后真实移除按钮禁用，增加图片不能新增不受权限限制的点击路径。
    model.authority['currentDeviceIsPrimary'] = false;
    model.notifyListeners();
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<StewardButton>(
            find.byKey(const ValueKey(('custom-extra-remove', 7))),
          )
          .onPressed,
      isNull,
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
  });
}
