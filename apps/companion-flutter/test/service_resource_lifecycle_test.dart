import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/service_favorites.dart';
import 'package:mystia_steward_companion/ui/components.dart';

import '../tool/preview/rich_preview_workbench_model.dart';
import 'support/steward_test_fonts.dart';

/// 路由测试使用生产壳层、资源页生命周期及生产纯订单投影；只把 HTTP 替换为可控
/// 内存读取。订阅代次和迟到结果由模型模拟，真实 LiveWorkbenchModel 单飞与轮询
/// 另在 live_http_contract_test 通过隔离宿主验证，不把这里称为真实游戏验收。
class _RouteResourceModel extends RichPreviewWorkbenchModel {
  _RouteResourceModel({required super.desktop}) {
    localPreferences = {
      ...localPreferences,
      'lastPage': 3,
      'reduceMotion': true,
      'missionListModuleEnabled': true,
    };
    resources.remove('/favorites');
  }

  final favoritesGate = Completer<void>();
  final storedFavorites = <String, Object?>{
    'recipes': <DataMap>[],
    'beverages': <DataMap>[],
  };
  final subscriptions = <String, Map<String, String>>{};
  final reads = <String>[];
  int generation = 0;

  @override
  void activatePage(int index) => clearResourceSubscription();

  @override
  void clearResourceSubscription() {
    generation++;
    subscriptions.clear();
  }

  @override
  Future<void> readResource(
    String path, {
    Map<String, String> query = const {},
  }) async {
    subscriptions[path] = Map.of(query);
    reads.add(path);
    final expected = generation;
    if (path == '/favorites') {
      await favoritesGate.future;
      if (expected != generation || !subscriptions.containsKey(path)) return;
      resources[path] = storedFavorites;
      notifyListeners();
    } else {
      await super.readResource(path, query: query);
    }
  }

  Future<void> pollSubscribedResources() async {
    for (final entry in subscriptions.entries.toList()) {
      await readResource(entry.key, query: entry.value);
    }
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(loadPreferredStewardTestFont);

  for (final desktop in [true, false]) {
    testWidgets(
      '${desktop ? 'Windows最窄' : 'Android'}从扩展首次进入经营可读取并切换收藏，旧页不撤销新订阅',
      (tester) async {
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(desktop ? 627 : 390, 1000);
        addTearDown(tester.view.resetDevicePixelRatio);
        addTearDown(tester.view.resetPhysicalSize);
        final model = _RouteResourceModel(desktop: desktop);
        addTearDown(model.dispose);
        await tester.pumpWidget(
          ProviderScope(
            overrides: [workbenchProvider.overrideWithValue(model)],
            child: const StewardApp(),
          ),
        );
        await tester.pumpAndSettle();
        expect(model.resources.containsKey('/favorites'), isFalse);
        expect(
          model.subscriptions.keys,
          containsAll(['/missions/tracked', '/missions/available']),
        );

        await tester.tap(find.byKey(const ValueKey('nav-1')));
        await tester.pumpAndSettle();
        expect(model.subscriptions.keys.toList(), ['/favorites']);
        final actions = find.byType(ServiceFavoriteActions).first;
        Finder button(String text) => find.descendant(
          of: actions,
          matching: find.byWidgetPredicate(
            (widget) => widget is StewardButton && widget.text == text,
          ),
        );
        expect(
          tester.widget<StewardButton>(button('收藏料理')).onPressed,
          isNull,
          reason: '尚未读取收藏不能误当作空列表，必须等权威结果',
        );
        model.favoritesGate.complete();
        await tester.pumpAndSettle();
        final projected = records(model.serviceOrders['recommendations']).first;
        expect(projected['presentationPending'], isFalse);
        expect(object(projected['customer'])['id'], isNotNull);
        expect(
          object(projected['customer'])['id'],
          isNot(object(projected['order'])['runtimeGuestId']),
          reason: '生产 customer 目录身份和现场实例编号不得混用',
        );
        expect(
          tester.widget<StewardButton>(button('收藏料理')).onPressed,
          isNotNull,
        );
        await tester.ensureVisible(button('收藏料理'));
        await tester.tap(button('收藏料理'));
        await tester.pumpAndSettle();
        final saved = records(model.resources['/favorites']!['recipes']).single;
        expect(saved['customerId'], object(projected['customer'])['id']);
        expect(
          saved['recipeId'],
          object(
            object(
              records(projected['executionPlans']).first['food'],
            )['recipe'],
          )['id'],
        );
        expect(
          tester.widget<StewardButton>(button('取消收藏料理')).onPressed,
          isNotNull,
        );

        // 经营已订阅资源仍会持续读取；取消动作沿用已保存记录 ID，不生成重复收藏。
        final readsBefore = model.reads.length;
        await model.pollSubscribedResources();
        await tester.pumpAndSettle();
        expect(model.reads.skip(readsBefore), ['/favorites']);
        await tester.tap(button('取消收藏料理'));
        await tester.pumpAndSettle();
        expect(model.resources['/favorites']!['recipes'], isEmpty);
        await tester.ensureVisible(button('收藏酒水'));
        await tester.tap(button('收藏酒水'));
        await tester.pumpAndSettle();
        expect(
          records(model.resources['/favorites']!['beverages'])
              .single['customerId'],
          object(projected['customer'])['id'],
        );
        await tester.tap(button('取消收藏酒水'));
        await tester.pumpAndSettle();
        expect(model.resources['/favorites']!['beverages'], isEmpty);

        // 同一生命周期约定覆盖其它主页面；经营隐藏之后不能继续占用收藏轮询。
        await tester.tap(find.byKey(const ValueKey('nav-4')));
        await tester.pumpAndSettle();
        expect(model.subscriptions.containsKey('/favorites'), isFalse);
        expect(
          model.subscriptions.keys.toList(),
          desktop ? ['/local-api/config'] : <String>[],
        );
        await tester.tap(find.byKey(const ValueKey('nav-3')));
        await tester.pumpAndSettle();
        expect(
          model.subscriptions.keys,
          containsAll(['/missions/tracked', '/missions/available']),
        );
        expect(model.subscriptions.containsKey('/local-api/config'), isFalse);
        expect(tester.takeException(), isNull);
      },
    );
  }
}
