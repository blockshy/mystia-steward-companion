import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/ui/components.dart';

import '../tool/preview/rich_preview_workbench_model.dart';

/// 在展示层端口记录实际请求，验证可见输入与传入 C# 查询的条件一致，而非只检查文字。
class _QueryRecordingModel extends RichPreviewWorkbenchModel {
  final List<DataMap> queries = [];
  @override
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  }) async {
    queries.add({
      'kind': kind,
      'customerId': customerId,
      'foodTag': foodTag,
      'beverageTag': beverageTag,
    });
    await super.queryRecommendations(
      kind: kind,
      selectedPlace: selectedPlace,
      customerId: customerId,
      foodTag: foodTag,
      beverageTag: beverageTag,
    );
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;

  testWidgets('推荐切换收藏与自定义子页后选项和筛选仍可见，重新查询条件一致', (tester) async {
    final model = _QueryRecordingModel();
    addTearDown(model.dispose);
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(1280, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('nav-2')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('稀客'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();

    Finder input(String title) => find.descendant(
      of: find.byWidgetPredicate(
        (widget) => widget is StewardField && widget.title == title,
      ),
      matching: find.byType(EditableText),
    );
    // 点单需求只能从该客人的目录选择；名称搜索仍验证持久控制器的真实可见文字。
    await tester.tap(find.byKey(const ValueKey('rare-food-tag-selector')));
    await tester.pumpAndSettle();
    await tester.tap(
      find.byWidgetPredicate(
        (widget) => widget is FSelectItem<String> && widget.value == '鲜',
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('rare-beverage-tag-selector')));
    await tester.pumpAndSettle();
    await tester.tap(
      find.byWidgetPredicate(
        (widget) => widget is FSelectItem<String> && widget.value == '清爽',
      ),
    );
    await tester.pumpAndSettle();
    await tester.enterText(input('筛选料理名称'), '味噌');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(model.queries.last, {
      'kind': 'rare',
      'customerId': 301,
      'foodTag': '鲜',
      'beverageTag': '清爽',
    });

    // 收藏页移除条件栏，自定义页还移除料理/酒水导航，覆盖两种输入子树重建路径。
    for (final title in ['收藏', '自定义配方', '稀客']) {
      await tester.tap(find.text(title));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 250));
      await tester.pumpAndSettle();
    }
    expect(find.text('料理：鲜'), findsOneWidget);
    expect(find.text('酒水：清爽'), findsOneWidget);
    expect(tester.widget<EditableText>(input('筛选料理名称')).controller.text, '味噌');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(model.queries.last['foodTag'], '鲜');
    expect(model.queries.last['beverageTag'], '清爽');
    expect(
      tester.widget<DenseListDetail>(find.byType(DenseListDetail)).items,
      hasLength(1),
    );

    // 清空搜索只取消结果过滤；已选合法需求保持可见，不能隐式重新查询或提交空标签。
    final count = model.queries.length;
    await tester.enterText(input('筛选料理名称'), '');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(model.queries.last['foodTag'], '鲜');
    expect(model.queries.last['beverageTag'], '清爽');
    expect(model.queries.length, count);
    expect(
      tester.widget<DenseListDetail>(find.byType(DenseListDetail)).items,
      hasLength(8),
    );
    // 收藏扩展为名称/标签/食材/编号检索后仍保留客人名称；自定义仍是跨客人的管理
    // 列表。使用各页当前提示定位真实输入框，不把旧提示文案当作控件已消失。
    for (final title in ['收藏', '自定义配方']) {
      await tester.tap(find.text(title));
      await tester.pumpAndSettle();
      final searchInput = input(
        title == '收藏' ? '筛选名称 / 标签 / 食材 / ID' : '筛选料理 / 稀客',
      );
      expect(searchInput, findsOneWidget);
      await tester.enterText(searchInput, '雾雨魔理沙');
      await tester.pumpAndSettle();
      final source =
          model.resources[title == '收藏' ? '/favorites' : '/custom-recipes'];
      final matched = records(source?['recipes'])
          .where((item) => label(item['customerName'], '').contains('雾雨魔理沙'));
      // 自定义模式计数的是外层主体，组内配方绑定不再占用多条客人行。
      final expected = title == '收藏'
          ? matched.length
          : matched.map((item) => item['customerId']).toSet().length;
      expect(expected, greaterThan(0));
      expect(
        tester.widget<DenseListDetail>(find.byType(DenseListDetail)).items,
        hasLength(expected),
      );
    }
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
}
