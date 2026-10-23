import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';

import 'support/fixture_model.dart';
import 'support/page_activity_assertions.dart';

/// 人工完成读取的模型仅模拟展示端口；真正的 A→B→A 响应丢弃另由 session 契约验证。
/// 这里允许旧 Future 迟到，以证明页面本身不会提前关闭新请求的加载提示。
class _AutomaticModel extends FixtureModel {
  _AutomaticModel() : super(orderCount: 0) {
    localPreferences = {'reduceMotion': true};
    setPlaces(['地点甲', '地点乙']);
    catalog = {
      ...catalog,
      'rareCustomers': [
        {
          'id': 301,
          'name': '合成稀客甲',
          'positiveTags': ['鲜', '素'],
          'beverageTags': ['清爽'],
        },
      ],
    };
  }
  @override
  int connectionVersion = 0;
  @override
  bool recommendationLoading = false;
  @override
  String recommendationError = '';
  final requests = <DataMap>[];
  final pending = <Completer<void>>[];
  int clears = 0;
  void setPlaces(List<String> places) {
    catalog = {
      ...catalog,
      'normalCustomers': [
        {'id': 1, 'name': '合成普客', 'places': places},
      ],
    };
  }

  @override
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  }) {
    requests.add({
      'kind': kind,
      'place': selectedPlace,
      'customerId': customerId,
      'foodTag': foodTag,
      'beverageTag': beverageTag,
    });
    final completion = Completer<void>();
    pending.add(completion);
    return completion.future;
  }

  @override
  void clearRecommendationQuery() {
    clears++;
    pageResult = {};
    recommendationError = '';
    recommendationLoading = false;
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  Future<void> settleQuery(WidgetTester tester) async {
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
  }

  Future<void> open(WidgetTester tester, _AutomaticModel model) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(1280, 920);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(model.dispose);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pumpAndSettle();
    expect(model.requests, isEmpty, reason: '隐藏的推荐页不能读取');
    await tester.tap(find.byKey(const ValueKey('nav-2')));
    await settleQuery(tester);
  }

  testWidgets('进入普客自动查询，结果筛选和普通模型通知不重新查询或全局忙碌', (tester) async {
    final model = _AutomaticModel();
    await open(tester, model);
    expect(find.text('普客地区'), findsNothing);
    expect(find.text('稀客需求'), findsNothing);
    expect(find.text('查询推荐'), findsNothing);
    expect(model.requests.single['place'], '地点乙');
    expect(find.text('正在更新推荐…'), findsOneWidget);
    expect(model.busy, isFalse);
    model.pending.single.complete();
    await tester.pumpAndSettle();
    final search = find.descendant(
      of: find.byWidgetPredicate(
        (widget) => widget is StewardField && widget.title == '筛选料理名称',
      ),
      matching: find.byType(EditableText),
    );
    await tester.enterText(search, '合成');
    await tester.tap(find.text('酒水'));
    model.publish();
    await settleQuery(tester);
    expect(model.requests, hasLength(1));
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('目录条件连续变化防抖合并，旧Future完成不取消新条件加载', (tester) async {
    final model = _AutomaticModel();
    await open(tester, model);
    model.setPlaces(['地点丙']);
    model.publish();
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 80));
    model.setPlaces(['地点丁']);
    model.publish();
    await settleQuery(tester);
    expect(model.requests, hasLength(2));
    expect(model.requests.last['place'], '地点丁');
    model.pending.first.completeError(StateError('过期读取错误'));
    await tester.pumpAndSettle();
    expect(find.text('正在更新推荐…'), findsOneWidget);
    expect(find.textContaining('过期读取错误'), findsNothing);
    model.pending.last.complete();
    await tester.pumpAndSettle();
    expect(find.text('正在更新推荐…'), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('普客稀客仅筛选料理酒水名称，提示随类型切换且不触发查询', (tester) async {
    final model = _AutomaticModel();
    await open(tester, model);
    for (final rare in [false, true]) {
      if (rare) {
        await tester.tap(find.text('稀客'));
        await settleQuery(tester);
      }
      // 故意混入同名客人字段，验证通用数据里即使存在该字段也不能扩大筛选范围。
      model.pageResult = {
        'recipes': [
          {
            'recipe': {'id': 101, 'name': '味噌汤'},
            'customerName': '专属客人',
          },
        ],
        'beverages': [
          {
            'beverage': {'id': 201, 'name': '梅子酒'},
            'customerName': '专属客人',
          },
        ],
      };
      model.pending.last.complete();
      model.publish();
      await tester.pumpAndSettle();
      final count = model.requests.length;
      final field = find.byKey(const ValueKey('recommendation-name-filter'));
      final input = find.descendant(
        of: field,
        matching: find.byType(EditableText),
      );
      await tester.tap(find.text('料理'));
      await tester.pumpAndSettle();
      expect(tester.widget<StewardField>(field).hint, '筛选料理名称');
      await tester.enterText(input, '专属客人');
      await tester.pumpAndSettle();
      expect(
        tester.widget<DenseListDetail>(find.byType(DenseListDetail)).items,
        isEmpty,
      );
      await tester.enterText(input, '味噌');
      await tester.pumpAndSettle();
      expect(
        tester.widget<DenseListDetail>(find.byType(DenseListDetail)).items,
        hasLength(1),
      );
      await tester.tap(find.text('酒水'));
      await tester.pumpAndSettle();
      expect(tester.widget<StewardField>(field).hint, '筛选酒水名称');
      expect(
        tester.widget<DenseListDetail>(find.byType(DenseListDetail)).items,
        isEmpty,
      );
      await tester.enterText(input, '梅子');
      await tester.pumpAndSettle();
      expect(
        tester.widget<DenseListDetail>(find.byType(DenseListDetail)).items,
        hasLength(1),
      );
      expect(model.requests.length, count);
    }
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('普客地点通过下拉内包含搜索选择并自动查询，重连关闭旧下拉', (tester) async {
    final model = _AutomaticModel();
    await open(tester, model);
    model.pending.single.complete();
    await tester.pumpAndSettle();
    final selector = find.byKey(const ValueKey('normal-place-selector'));
    await tester.tap(selector);
    await tester.pumpAndSettle();
    await tester.enterText(
      find.descendant(
        of: find.byWidgetPredicate(
          (widget) => widget is FTextField && widget.hint == '搜索经营地点',
        ),
        matching: find.byType(EditableText),
      ),
      '点甲',
    );
    await tester.pumpAndSettle();
    Finder item(String value) => find.byWidgetPredicate(
      (widget) => widget is FSelectItem<String> && widget.value == value,
    );
    expect(item('地点甲'), findsOneWidget);
    expect(item('地点乙'), findsNothing);
    expect(model.requests, hasLength(1), reason: '搜索文字只过滤目录，不发送业务请求');
    await tester.tap(item('地点甲'));
    await settleQuery(tester);
    expect(model.requests.last['place'], '地点甲');
    expect(model.requests, hasLength(2));
    model.pending.last.complete();
    await tester.pumpAndSettle();
    await tester.tap(selector);
    await tester.pumpAndSettle();
    final oldSelection = tester
        .widget<StewardSelect<String>>(selector)
        .onChanged;
    model.connectionVersion++;
    model.publish();
    await settleQuery(tester);
    expect(
      find.byWidgetPredicate((widget) => widget is FSelectItem<String>),
      findsNothing,
    );
    oldSelection('地点乙');
    await settleQuery(tester);
    expect(model.requests.last['place'], '地点甲');
    expect(model.requests, hasLength(3), reason: '仅当前选择在新连接重新查询一次');
    model.pending.last.complete();
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('断线和隐藏撤销读取，恢复可见后自动重新查询且不会重复clear循环', (tester) async {
    final model = _AutomaticModel();
    await open(tester, model);
    model.connected = false;
    model.publish();
    await settleQuery(tester);
    expect(find.textContaining('连接断开'), findsOneWidget);
    final clears = model.clears;
    for (var i = 0; i < 3; i++) {
      model.publish();
      await settleQuery(tester);
    }
    expect(model.clears, clears);
    expect(model.requests, hasLength(1));
    await tester.tap(find.byKey(const ValueKey('nav-0')));
    await tester.pumpAndSettle();
    model.connected = true;
    model.connectionVersion++;
    model.publish();
    model.pending.first.complete();
    await settleQuery(tester);
    expect(model.requests, hasLength(1));
    await tester.tap(find.byKey(const ValueKey('nav-2')));
    await settleQuery(tester);
    expect(model.requests, hasLength(2));
    model.pending.last.complete();
    await tester.pumpAndSettle();
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('稀客自动选择合法客人并查询，服务端pending与错误提供明确状态和重试', (tester) async {
    final model = _AutomaticModel();
    await open(tester, model);
    model.pending.first.complete();
    await tester.pumpAndSettle();
    await tester.tap(find.text('稀客'));
    await settleQuery(tester);
    expect(model.requests.last, {
      'kind': 'rare',
      'place': '地点乙',
      'customerId': 301,
      'foodTag': '鲜',
      'beverageTag': '清爽',
    });
    expect(find.text('合成稀客甲'), findsOneWidget);
    model.recommendationLoading = true;
    model.pending.last.complete();
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('正在更新推荐…'), findsOneWidget);
    model.recommendationLoading = false;
    model.recommendationError = '合成推荐读取失败';
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('推荐加载失败：合成推荐读取失败'), findsNothing);
    await expectPageActivityDetails(tester, '推荐加载失败：合成推荐读取失败');
    await tester.tap(find.text('重试'));
    await settleQuery(tester);
    expect(model.requests, hasLength(3));
    expect(find.text('正在更新推荐…'), findsOneWidget);
    model.pending.last.complete();
    await tester.pumpAndSettle();
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('实际请求显示动态进度与可访问状态，减少动态效果保持文字而不旋转', (tester) async {
    final model = _AutomaticModel();
    await open(tester, model);
    expect(find.byType(FCircularProgress), findsNothing);
    model.localPreferences = {'reduceMotion': false};
    model.publish();
    // 未完成的加载动画只能有限推进；不能用 pumpAndSettle 等待无限旋转停止。
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));
    expect(find.byType(FCircularProgress), findsOneWidget);
    final status = find.byKey(const ValueKey('workbench-page-activity'));
    expect(
      find.ancestor(
        of: status,
        matching: find.byWidgetPredicate(
          (widget) =>
              widget is Semantics && widget.properties.liveRegion == true,
        ),
      ),
      findsOneWidget,
    );
    model.pending.single.complete();
    await tester.pumpAndSettle();
    expect(find.byType(FCircularProgress), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });
}
