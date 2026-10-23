import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/recommendation_item_content.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/menu_item_content.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

const _recipe = <String, Object?>{
  'id': 1,
  'recipeId': 101,
  'name': '合成海鲜味噌汤',
  'price': 42,
  'cooker': '煮锅',
  'ingredients': ['豆腐'],
  'positiveTags': ['家常', '清淡'],
};
const _beverage = <String, Object?>{
  'id': 3,
  'name': '合成冰山毛玉冻柠',
  'price': 12,
  'tags': ['清爽', '无酒精'],
};
const _matched = ['家常', '鲜', '清淡', '和风', '招牌'];
const _normal = <String, Object?>{
  'recipe': _recipe,
  'activeTags': ['家常', '鲜', '清淡'],
  'suppressedTags': ['重油'],
  'matchedTags': _matched,
  'ingredientCost': 8,
  'profit': 34,
  'totalCoverage': 7,
  'coveredCustomerCount': 2,
  'customerCoverage': [
    {
      'customerName': '名称很长的合成客群',
      'matchedTags': ['家常', '鲜'],
      // 故意缺少 matchedTagCount，前端不能根据数组长度补算业务计数。
    },
  ],
  'conditionResults': [
    {'label': '合成条件', 'detail': '这是服务端提供的完整条件解释，不拆成标签。'},
  ],
};
const _rare = <String, Object?>{
  'recipe': _recipe,
  'extraIngredients': [
    {'id': 2, 'name': '合成蘑菇'},
  ],
  'allTags': ['家常', '鲜'],
  'cancelledTags': ['重油'],
  'extraIngredientReasonTags': {
    '2': ['鲜', '清淡'],
  },
};
const _plan = <String, Object?>{
  'food': {
    'recipe': _recipe,
    'activeTags': ['家常', '鲜'],
    'targetTags': ['清淡'],
    'suppressedTags': ['重油'],
    'meetsRequiredFood': true,
  },
  'beverage': {
    'beverage': _beverage,
    'activeTags': ['无酒精'],
    'suppressedTags': ['高酒精'],
    'meetsRequiredBeverage': false,
  },
  'estimatedPrice': 54,
  'reasons': ['保留同一个方案的料理与酒水解释'],
  'warnings': ['原样保留的合成警告'],
};

/// 只使用合成 DTO 检验两类页面的展示语义；不触发 HTTP、平台存储或推荐计算。
FixtureModel _model() => FixtureModel(orderCount: 0)
  ..catalog = {
    'recipes': [_recipe],
    'beverages': [_beverage],
    'ingredients': [
      {'id': 2, 'name': '合成蘑菇'},
    ],
  };

Widget _surface(Widget child, {double scale = 1, double left = 0}) {
  final theme = stewardTheme(dark: false, touch: true);
  return material.MaterialApp(
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
      padding: EdgeInsets.fromLTRB(left + 12, 12, 12, 12),
      child: SingleChildScrollView(child: child),
    ),
  );
}

Finder _group(String label) => find.byWidgetPredicate(
  (widget) => widget is MenuTagGroup && widget.label == label,
);
Finder _tag(String text, OrderTagKind kind) => find.byWidgetPredicate(
  (widget) => widget is OrderTag && widget.text == text && widget.kind == kind,
);

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

  testWidgets('普客详情保留完整匹配与独立生效标签，客群缺计数不补算', (tester) async {
    final model = _model();
    addTearDown(model.dispose);
    await tester.pumpWidget(
      _surface(
        RecommendationItemExplanation(
          model,
          _normal,
          section: RecommendationSection.normal,
          food: true,
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(tester.widget<MenuTagGroup>(_group('匹配')).tags, _matched);
    expect(tester.widget<MenuTagGroup>(_group('标签')).tags, ['家常', '鲜', '清淡']);
    expect(_tag('招牌', OrderTagKind.success), findsOneWidget);
    expect(_tag('已抵消 重油', OrderTagKind.warning), findsOneWidget);
    expect(find.byType(MenuItemContent), findsNothing);
    expect(find.text('家常 · 鲜 · 清淡 · 和风 · 招牌'), findsNothing);

    await tester.tap(find.text('客群匹配详情'));
    await tester.pumpAndSettle();
    expect(find.text('名称很长的合成客群'), findsOneWidget);
    final metric = find.byWidgetPredicate(
      (widget) => widget is MenuMetric && widget.name == '匹配标签数',
    );
    expect(tester.widget<MenuMetric>(metric).value, '未知');
    expect(find.byType(Fact), findsNothing);
    await tester.ensureVisible(find.text('推荐依据'));
    await tester.tap(find.text('推荐依据'));
    await tester.pumpAndSettle();
    expect(find.text('这是服务端提供的完整条件解释，不拆成标签。'), findsOneWidget);
  });

  testWidgets('稀客酒水完整匹配与静态目录标签分列，不能冒充生效结果', (tester) async {
    final model = _model();
    addTearDown(model.dispose);
    await tester.pumpWidget(
      _surface(
        RecommendationItemExplanation(
          model,
          const {
            'beverage': _beverage,
            'matchedTags': ['清爽'],
            'meetsRequiredBev': true,
          },
          section: RecommendationSection.rare,
          food: false,
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(tester.widget<MenuTagGroup>(_group('匹配')).tags, ['清爽']);
    expect(tester.widget<MenuTagGroup>(_group('标签')).tags, ['清爽', '无酒精']);
    expect(_tag('清爽', OrderTagKind.success), findsOneWidget);
    expect(_tag('清爽', OrderTagKind.ingredient), findsOneWidget);
    // 统一字段名后仍区分静态属性与运行时生效：目录属性不能渲染成 request 标签。
    expect(_tag('无酒精', OrderTagKind.request), findsNothing);
  });

  for (final section in [
    RecommendationSection.favorites,
    RecommendationSection.custom,
  ]) {
    testWidgets('$section 详情只展示一次目录标签且无虚构实时匹配', (tester) async {
      final model = _model();
      addTearDown(model.dispose);
      await tester.pumpWidget(
        _surface(
          RecommendationItemExplanation(
            model,
            {
              'recipeId': section == RecommendationSection.custom ? 101 : 1,
              'foodId': 1,
              'foodTag': null,
              'extraIngredientIds': <int>[],
            },
            section: section,
            // 自定义强制为料理，不能受上一个页面酒水筛选影响。
            food: section != RecommendationSection.custom,
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(_group('标签'), findsOneWidget);
      expect(tester.widget<MenuTagGroup>(_group('标签')).tags, ['家常', '清淡']);
      expect(_tag('家常', OrderTagKind.ingredient), findsOneWidget);
      expect(_group('匹配'), findsNothing);
      // 收藏/自定义只有目录静态标签；不能因前缀统一而额外制造一组生效标签。
      expect(_tag('家常', OrderTagKind.request), findsNothing);
      expect(find.text('此处显示保存的选择；实时匹配与成本请在稀客页查看。'), findsOneWidget);
    });
  }

  testWidgets('稀客料理生效标签不重复，加料原因保持原食材与真实原因列表', (tester) async {
    final model = _model();
    addTearDown(model.dispose);
    await tester.pumpWidget(
      _surface(
        RecommendationItemExplanation(
          model,
          _rare,
          section: RecommendationSection.rare,
          food: true,
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(_tag('鲜', OrderTagKind.request), findsOneWidget);
    expect(_group('生效'), findsNothing);
    await tester.tap(find.text('加料说明'));
    await tester.pumpAndSettle();
    expect(find.text('合成蘑菇'), findsOneWidget);
    expect(tester.widget<MenuTagGroup>(_group('加料')).tags, ['鲜', '清淡']);
    expect(find.text('鲜 · 清淡'), findsNothing);
  });

  testWidgets('经营增量详情按料理与酒水归属，不重复基础材料且保留方案原因和警告', (tester) async {
    final model = _model();
    addTearDown(model.dispose);
    await tester.pumpWidget(
      _surface(
        ServicePlanContent(model, _plan, details: true, showBasics: false),
      ),
    );
    await tester.pumpAndSettle();
    final details = tester
        .widgetList<MenuItemDetails>(find.byType(MenuItemDetails))
        .toList();
    expect(details.map((item) => item.item.name), ['合成海鲜味噌汤', '合成冰山毛玉冻柠']);
    expect(details.every((item) => item.showName), isTrue);
    expect(find.text('合成海鲜味噌汤'), findsOneWidget);
    expect(find.text('合成冰山毛玉冻柠'), findsOneWidget);
    expect(find.byType(MenuItemContent), findsNothing);
    expect(_tag('清淡', OrderTagKind.success), findsOneWidget);
    expect(_tag('已抵消 高酒精', OrderTagKind.warning), findsOneWidget);
    expect(_tag('酒水偏好备选', OrderTagKind.warning), findsOneWidget);
    expect(find.text('保留同一个方案的料理与酒水解释'), findsOneWidget);
    expect(_tag('原样保留的合成警告', OrderTagKind.warning), findsOneWidget);
    expect(tester.widget<MenuMetric>(find.byType(MenuMetric)).value, '54');
  });

  testWidgets('普通订单以细线替代基础方案标题，特殊执行和原订单身份保留', (tester) async {
    final model = _model();
    addTearDown(model.dispose);
    final DataMap normal = {
      'order': <String, Object?>{},
      'originalFood': {'name': '原料理'},
      'originalBeverage': {'name': '原酒水'},
      'executionFood': {'name': '执行料理'},
      'executionBeverage': {'name': '执行酒水'},
    };
    await tester.pumpWidget(
      _surface(ServiceOrderSummary(model, normal, rare: false)),
    );
    await tester.pumpAndSettle();
    expect(find.byType(MenuItemDivider), findsNWidgets(2));
    // 第一条区分点单与方案，第二条区分同一方案的料理和酒水；不只放宽数量，
    // 直接确认分隔线落在正确实体之间，避免把多画装饰线误当作需求完成。
    final food = find.byType(MenuItemContent).at(0);
    final beverage = find.byType(MenuItemContent).at(1);
    final requestDivider = tester.getRect(find.byType(MenuItemDivider).at(0));
    final entityDivider = tester.getRect(find.byType(MenuItemDivider).at(1));
    expect(
      requestDivider.top,
      greaterThanOrEqualTo(tester.getRect(find.text('原酒水：原酒水')).bottom),
    );
    expect(requestDivider.bottom, lessThanOrEqualTo(tester.getRect(food).top));
    expect(
      entityDivider.top,
      greaterThanOrEqualTo(tester.getRect(food).bottom),
    );
    expect(
      entityDivider.bottom,
      lessThanOrEqualTo(tester.getRect(beverage).top),
    );
    expect(find.text('基础方案'), findsNothing);
    expect(find.text('原料理：原料理'), findsOneWidget);
    expect(find.text('执行料理'), findsOneWidget);
    await tester.pumpWidget(
      _surface(
        ServiceOrderSummary(model, {
          ...normal,
          'usesSpecialExecution': true,
        }, rare: false),
      ),
    );
    await tester.pumpAndSettle();
    // 新单等待时尚不知道是否为特殊执行：点单与方案的边界始终存在，
    // 避免结果到达后移除边界而跳高；特殊执行身份标题仍保留在方案区内。
    expect(find.byType(MenuItemDivider), findsNWidgets(2));
    expect(find.text('执行方案'), findsOneWidget);
    final specialRequestDivider = tester.getRect(
      find.byType(MenuItemDivider).at(0),
    );
    expect(
      specialRequestDivider.top,
      greaterThanOrEqualTo(tester.getRect(find.text('原酒水：原酒水')).bottom),
    );
    expect(
      specialRequestDivider.bottom,
      lessThanOrEqualTo(tester.getRect(find.text('执行方案')).top),
    );
    final specialDivider = tester.getRect(find.byType(MenuItemDivider).at(1));
    expect(
      specialDivider.top,
      greaterThanOrEqualTo(tester.getRect(food).bottom),
    );
    expect(
      specialDivider.bottom,
      lessThanOrEqualTo(tester.getRect(beverage).top),
    );
    expect(
      tester.getRect(find.text('执行方案')).bottom,
      lessThanOrEqualTo(tester.getRect(food).top),
    );
  });

  for (final width in [360.0, 390.0, 624.0, 640.0, 1280.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('经营与推荐增量标签 $width px / $scale 字号保留完整文本并可滚动', (tester) async {
        final model = _model();
        addTearDown(model.dispose);
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(width, 760);
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        await tester.pumpWidget(
          _surface(
            Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                RecommendationItemExplanation(
                  model,
                  _normal,
                  section: RecommendationSection.normal,
                  food: true,
                ),
                const MenuItemDivider(),
                ServicePlanContent(
                  model,
                  _plan,
                  details: true,
                  showBasics: false,
                ),
              ],
            ),
            scale: scale,
            left: 80,
          ),
        );
        await tester.pumpAndSettle();
        await tester.tap(find.text('客群匹配详情'));
        await tester.pumpAndSettle();
        // 使用真正的窄内容区，不把整窗宽度当作卡片可用宽度。每个渲染标签的
        // 完整文字必须留在自身分组以内；末项通过滚动可见，而非放宽命中警告。
        for (final group in find.byType(MenuTagGroup).evaluate()) {
          final groupFinder = find.byWidget(group.widget);
          final bounds = tester.getRect(groupFinder);
          for (final tag
              in find
                  .descendant(of: groupFinder, matching: find.byType(OrderTag))
                  .evaluate()) {
            final rect = tester.getRect(find.byWidget(tag.widget));
            expect(rect.left, greaterThanOrEqualTo(bounds.left));
            expect(rect.right, lessThanOrEqualTo(bounds.right + .1));
            expect(rect.bottom, lessThanOrEqualTo(bounds.bottom + .1));
          }
        }
        final tail = find.text('原样保留的合成警告');
        await tester.ensureVisible(tail);
        await tester.pumpAndSettle();
        expect(tail.hitTestable(), findsOneWidget);
        expect(tester.takeException(), isNull);
      });
    }
  }
}
