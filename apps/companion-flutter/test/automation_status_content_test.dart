import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/automation_status_content.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/menu_item_content.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 当前代次仅提供明确的配方依赖；测试不加载打包图片、不连接游戏或图片 API。
class _AutomationMediaModel extends FixtureModel {
  _AutomationMediaModel() {
    catalog = {
      'isComplete': true,
      'recipes': [
        {'id': 41, 'recipeId': 9001, 'name': '目录料理', 'cooker': '油锅'},
      ],
      'ingredients': [
        {'id': 2, 'name': '蘑菇'},
        {'id': 5, 'name': '豆腐'},
      ],
    };
  }

  @override
  Map<String, DataMap> gameMediaRecipes = {
    '41:9001': {
      'ingredientIds': [2, 2, 5],
      'cookerType': 3,
    },
  };
}

const _mediaTarget = <String, Object?>{
  'kind': 'rare',
  'guestImageKind': 'rare-guest',
  'guestImageId': 42,
  'guestId': 900,
  'runtimeGuestId': 8888,
  'targetSource': 'locked',
  'foodId': 41,
  'recipeId': 9001,
  'recipeName': '锁定料理',
  'cookerName': '油锅',
  'extraIngredientIds': [5, 2],
  'beverageId': 7,
  'beverageName': '锁定酒水',
};

/// 字段形状沿用AutomationCoordinator.Protocol.Status，测试请求与现场事实的明确区分。
const _diagnostic = <String, Object?>{
  'title': '博丽灵梦 · 桌 1',
  'orderKey': 'rare:0|991|1|2',
  'traceId': 'synthetic-rare-0',
  'source': '合成订单捕获',
  'stepLabel': '确认料理',
  'stepSeconds': 25,
  'nextAction': '下一轮校验厨具/开锅',
  'retryCount': 2,
  'rollbackCount': 1,
  'detailMessage': '等待厨具空位，保留已锁定主方案。',
  'lastError': '上次尝试未确认开锅结果。',
  'paused': true,
  'manualResolutionRequired': false,
  'completed': false,
  'hasServedFood': false,
  'hasServedBeverage': true,
  'hasEvaluated': false,
  'foodDeliveryRequested': true,
  'beverageDeliveryRequested': true,
  'controllerAvailable': true,
  'lastRuntimeEventSequence': 17,
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
  Widget fixture(Widget child, {bool dark = false, double scale = 1.5}) {
    final theme = stewardTheme(dark: dark, touch: true);
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
        padding: const EdgeInsets.only(left: 80, right: 12),
        child: SingleChildScrollView(child: child),
      ),
    );
  }

  test('诊断方案按双ID读取当前依赖，基础材料及加料保留顺序和重复项', () {
    final model = _AutomationMediaModel();
    addTearDown(model.dispose);
    final food = automationFoodView(model, _mediaTarget)!;
    expect(food.entityId, 41);
    expect(food.name, '锁定料理');
    expect(food.cookerId, 3);
    expect(food.ingredients!.map((entry) => entry.entityId), [2, 2, 5]);
    expect(food.ingredients!.map((entry) => entry.name), ['蘑菇', '蘑菇', '豆腐']);
    expect(food.extras!.map((entry) => entry.entityId), [5, 2]);
    expect(food.activeTags, isEmpty);
    expect(food.facts, isEmpty);
    expect(automationBeverageView(_mediaTarget)!.entityId, 7);
    // 切代次撤回依赖后不能保留上一代厨具/材料图；原锁定料理与显式加料身份仍有效。
    model.gameMediaRecipes = {};
    final withdrawn = automationFoodView(model, _mediaTarget)!;
    expect(withdrawn.ingredients, isNull);
    expect(withdrawn.cookerId, isNull);
    expect(withdrawn.extras!.map((entry) => entry.entityId), [5, 2]);
  });

  test('配方编号不符、目录不完整或重复时不关联材料和厨具图片', () {
    final model = _AutomationMediaModel();
    addTearDown(model.dispose);
    void unknownDependencies(DataMap item) {
      final food = automationFoodView(model, item)!;
      expect(food.entityId, 41);
      expect(food.ingredients, isNull);
      expect(food.cookerId, isNull);
    }

    unknownDependencies({..._mediaTarget, 'recipeId': 41});
    model.catalog['isComplete'] = false;
    unknownDependencies(_mediaTarget);
    model.catalog['isComplete'] = true;
    model.catalog['recipes'] = [
      {'id': 41, 'recipeId': 9001},
      {'id': 41, 'recipeId': 9001},
    ];
    unknownDependencies(_mediaTarget);
  });

  test('未知加料与真实空数组不同，错误ID和同名目录不补造图片身份', () {
    final model = _AutomationMediaModel();
    addTearDown(model.dispose);
    final unknown = automationFoodView(model, {
      'recipeName': '目录料理',
      'foodId': '41',
      'recipeId': 9001,
    })!;
    expect(unknown.entityId, isNull);
    expect(unknown.ingredients, isNull);
    expect(unknown.extras, isNull);
    expect(
      automationFoodView(model, {
        ..._mediaTarget,
        'extraIngredientIds': [],
      })!.extras,
      isEmpty,
    );
    final invalid = automationFoodView(model, {
      ..._mediaTarget,
      'extraIngredientIds': [-1, '2', 2.0, null, 99],
    })!.extras!;
    expect(invalid.map((entry) => entry.entityId), [
      null,
      null,
      null,
      null,
      99,
    ]);
    expect(invalid.last.name, '材料 #99');
    model.catalog['ingredients'] = [
      {'id': 5, 'name': '甲'},
      {'id': 5, 'name': '乙'},
    ];
    expect(
      automationFoodView(model, _mediaTarget)!.extras!.first.name,
      '材料 #5',
    );
    expect(
      automationBeverageView({'beverageId': -1, 'beverageName': '锁定酒水'})!
          .entityId,
      isNull,
    );
    expect(automationFoodView(model, {}), isNull);
    expect(automationBeverageView({}), isNull);
  });

  testWidgets('诊断客人图仅使用明确命名空间和guestImageId，不猜guestId或运行时ID', (tester) async {
    await tester.pumpWidget(
      fixture(
        const Column(
          children: [
            AutomationOrderHeading({..._mediaTarget, 'title': '稀客'}),
            AutomationOrderHeading({
              'title': '客群',
              'guestImageKind': 'normal-group',
              'guestImageId': 3,
              'guestId': 999,
              'runtimeGuestId': 888,
            }),
            AutomationOrderHeading({
              'title': '未知类别',
              'guestImageKind': 'future-kind',
              'guestImageId': 3,
              'kind': 'rare',
              'guestId': 42,
            }),
            AutomationOrderHeading({
              'title': '缺失身份',
              'guestImageKind': 'rare-guest',
              'guestId': 42,
            }),
          ],
        ),
      ),
    );
    await tester.pumpAndSettle();
    final images = tester.widgetList<EntityImage>(find.byType(EntityImage));
    expect(images.map((entry) => (entry.kind, entry.id)), [
      (EntityImageKind.rareGuest, 42),
      (EntityImageKind.normalGroup, 3),
      (EntityImageKind.rareGuest, null),
      (EntityImageKind.rareGuest, null),
    ]);
    expect(tester.takeException(), isNull);
  });

  for (final width in [360.0, 390.0, 624.0, 1280.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('图像诊断 $width / $scale 标题独占整行、说明值列换行且处理目标可达', (tester) async {
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(width, 920);
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        final model = _AutomationMediaModel();
        addTearDown(model.dispose);
        const message = '这是一段较长的现场说明，等待厨具空位并保留当前锁定方案；只记录状态，不表示已送达。';
        await tester.pumpWidget(
          fixture(
            AutomationStatusContent({
              ..._diagnostic,
              ..._mediaTarget,
              'detailMessage': message,
            }, model: model),
            scale: scale,
          ),
        );
        await tester.pumpAndSettle();
        final heading = tester.getRect(find.text('博丽灵梦 · 桌 1'));
        final status = tester.getRect(find.text('已暂停'));
        expect(status.top, greaterThanOrEqualTo(heading.bottom));
        final caption = tester.getRect(find.text('当前说明'));
        final description = tester.getRect(find.text(message));
        expect(description.left, greaterThan(caption.right));
        expect(description.right, lessThanOrEqualTo(width - 12 + .01));
        // 首行基线一致，字号/行高可以令字形顶部相差少量像素；不能退回标题上下分段卡片。
        expect((description.top - caption.top).abs(), lessThan(12 * scale));
        await tester.ensureVisible(find.text('锁定料理'));
        await tester.pumpAndSettle();
        expect(find.text('锁定料理').hitTestable(), findsOneWidget);
        expect(find.text('已锁定的执行方案'), findsOneWidget);
        final images = tester
            .widgetList<EntityImage>(find.byType(EntityImage))
            .toList();
        expect(
          images.map((entry) => (entry.kind, entry.id)),
          containsAll([
            (EntityImageKind.rareGuest, 42),
            (EntityImageKind.food, 41),
            (EntityImageKind.beverage, 7),
            (EntityImageKind.cooker, 3),
            (EntityImageKind.ingredient, 2),
            (EntityImageKind.ingredient, 5),
          ]),
        );
        expect(find.text('待送料理'), findsOneWidget);
        expect(find.text('已有料理'), findsNothing);
        expect(tester.takeException(), isNull);
      });
    }
  }

  for (final width in [1280.0, 640.0, 390.0]) {
    for (final dark in [false, true]) {
      testWidgets('自动化详情 $width ${dark ? '深色' : '浅色'} 150%分组可达且请求不冒充送达', (
        tester,
      ) async {
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(width, 920);
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        var retries = 0;
        await tester.pumpWidget(
          fixture(
            AutomationStatusContent(_diagnostic, onRetry: () => retries++),
            dark: dark,
          ),
        );
        await tester.pumpAndSettle();
        expect(find.text('已暂停'), findsOneWidget);
        expect(find.text('确认料理'), findsOneWidget);
        expect(find.text('阶段耗时 25 秒'), findsOneWidget);
        // 诊断中的指标也复用同一标签尺寸，不能因公共抽取恢复成更高的数字卡片。
        final elapsed = find.byWidgetPredicate(
          (widget) => widget is MenuMetric && widget.name == '阶段耗时',
        );
        final state = find.byWidgetPredicate(
          (widget) => widget is OrderTag && widget.text == '已暂停',
        );
        expect(
          tester.getSize(elapsed).height,
          closeTo(tester.getSize(state).height, .01),
        );
        expect(find.text('下一轮校验厨具/开锅'), findsOneWidget);
        expect(find.text('待送料理'), findsOneWidget);
        expect(find.text('已有酒水'), findsOneWidget);
        expect(find.text('已有料理'), findsNothing);
        expect(find.text('已请求送达，等待现场确认。'), findsOneWidget);
        await tester.ensureVisible(find.text('重试暂停订单'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('重试暂停订单'));
        expect(retries, 1);
        await tester.ensureVisible(find.text('高级诊断'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('高级诊断'));
        await tester.pumpAndSettle();
        await tester.ensureVisible(find.text('订单控制器'));
        await tester.pumpAndSettle();
        expect(find.text('订单控制器').hitTestable(), findsOneWidget);
        expect(find.text('rare:0|991|1|2'), findsOneWidget);
        expect(tester.takeException(), isNull);
      });
    }
  }

  testWidgets('人工处理与字段缺失不伪造运行状态或可重试权限', (tester) async {
    await tester.pumpWidget(
      fixture(
        const AutomationStatusContent({
          'manualResolutionRequired': true,
          'paused': true,
          'foodDeliveryRequested': true,
        }),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('需人工处理'), findsOneWidget);
    expect(find.text('阶段待确认'), findsOneWidget);
    expect(find.text('阶段耗时 未知'), findsOneWidget);
    expect(find.text('料理送达待确认'), findsOneWidget);
    final retry = tester.widget<StewardButton>(
      find.byWidgetPredicate(
        (widget) => widget is StewardButton && widget.text == '重试暂停订单',
      ),
    );
    expect(retry.onPressed, isNull);
    await tester.pumpWidget(
      fixture(const AutomationStatusContent({'paused': false}, compact: true)),
    );
    await tester.pumpAndSettle();
    expect(find.text('未暂停'), findsOneWidget);
    expect(find.text('正在运行'), findsNothing);
    expect(tester.takeException(), isNull);
  });

  testWidgets('回退预算暂停展示真实原因和下一步，显式重试可达且摘要不再等待制作', (tester) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(390, 800);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    var retryCalls = 0;
    const reason = '自动回退已达到上限 2/2，已暂停该订单。';
    const detail = '$reason 此订单不会继续自动重试；请检查原因后在自动化状态中点击“重试暂停订单”。';
    const next = '自动回退预算已用尽；检查原因后点击“重试暂停订单”重新开放预算';
    const paused = <String, Object?>{
      'title': '合成普客 · 桌 6',
      'stepLabel': '暂停',
      'stepSeconds': 7,
      'retryCount': 0,
      'rollbackCount': 2,
      'paused': true,
      'manualResolutionRequired': false,
      'completed': false,
      'hasServedFood': false,
      'hasServedBeverage': true,
      'detailMessage': detail,
      'lastError': reason,
      'nextAction': next,
    };

    // DTO 使用服务端暂停对账后的说明，不把“任务已移除”自行推断为可再次执行。
    // 组件只暴露页面已经授权的重试回调，点击本身不能修改快照或清掉预算原因。
    await tester.pumpWidget(
      fixture(AutomationStatusContent(paused, onRetry: () => retryCalls++)),
    );
    await tester.pumpAndSettle();
    expect(retryCalls, 0);
    expect(find.text('已暂停'), findsOneWidget);
    expect(find.text('回滚 2'), findsOneWidget);
    await tester.ensureVisible(find.text(next));
    await tester.pumpAndSettle();
    expect(find.text(next).hitTestable(), findsOneWidget);
    await tester.ensureVisible(find.text(reason));
    await tester.pumpAndSettle();
    expect(find.text(reason).hitTestable(), findsOneWidget);
    final retry = find.byWidgetPredicate(
      (widget) => widget is StewardButton && widget.text == '重试暂停订单',
    );
    await tester.ensureVisible(retry);
    await tester.pumpAndSettle();
    expect(tester.widget<StewardButton>(retry).onPressed, isNotNull);
    await tester.tap(retry);
    expect(retryCalls, 1);
    expect(paused['paused'], isTrue);
    expect(paused['rollbackCount'], 2);
    expect(tester.takeException(), isNull);

    // 列表摘要必须消费同一份真实暂停说明；不能回退到上一锅的制作中/等待送达文本。
    // 仅检查摘要组件，避免把其它订单的合法等待说明纳入全局禁止条件。
    await tester.pumpWidget(
      fixture(const AutomationStatusContent(paused, compact: true)),
    );
    await tester.pumpAndSettle();
    expect(find.text('已暂停'), findsOneWidget);
    expect(find.text(detail), findsOneWidget);
    final summary = tester.widget<Text>(find.text(detail));
    expect(summary.maxLines, 2);
    expect(summary.data, startsWith(reason));
    expect(find.textContaining('已在制作中'), findsNothing);
    expect(find.textContaining('等待完成'), findsNothing);
    expect(find.textContaining('等待 Mod 料理任务送达'), findsNothing);
    expect(retryCalls, 1);
    expect(tester.takeException(), isNull);
  });
}
