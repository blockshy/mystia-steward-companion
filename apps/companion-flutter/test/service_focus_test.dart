import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/service.dart';
import 'package:mystia_steward_companion/features/service_focus.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 主方案、候选、原订单、特殊执行使用不同名称，防止页面无意中把它们拼成一个方案。
/// 所有动作只记录到内存；不连接游戏、不访问原生偏好，也不依赖推荐算法。
class _FocusModel extends FixtureModel {
  _FocusModel({bool touch = false}) : super(desktop: !touch, orderCount: 0) {
    business = {
      'automation': {
        'message': '等待经营状态',
        'normalDiagnostics': [
          {'orderKey': 'normal:1', 'title': '普客乙 · 桌 2', 'stepLabel': '等待'},
        ],
      },
      'recommendations': {
        'recommendations': [
          {
            'order': {
              'traceId': 'rare-one',
              'deskCode': 0,
              'guestName': '稀客甲',
              'foodTag': '鲜',
              'beverageTag': '无酒精',
              'orderLifecycleSequence': 1,
            },
            'executionPlans': [
              {
                'bucket': 'complete',
                'food': {
                  'recipe': {
                    'name': '主方案料理',
                    'cooker': '煮锅',
                    'ingredients': ['豆腐', '海苔'],
                  },
                  'extraIngredients': [
                    {'id': 2, 'name': '蘑菇'},
                    {'id': 2, 'name': '蘑菇'},
                  ],
                },
                'beverage': {
                  'beverage': {'id': 3, 'name': '主方案酒水'},
                },
              },
              {
                'food': {
                  'recipe': {'name': '不可误用的备选料理'},
                },
                'beverage': {
                  'beverage': {'name': '不可误用的备选酒水'},
                },
              },
            ],
            'beverages': [
              {
                'beverage': {'name': '不可误用的独立酒水候选'},
              },
            ],
          },
          {
            'order': {
              'traceId': 'rare-blocked',
              'deskCode': 7,
              'guestName': '稀客丙',
              'foodTag': '清淡',
              'beverageTag': '苦',
              'orderLifecycleSequence': 1,
            },
            'executionPlans': <DataMap>[],
            'blockedMessages': ['食材不足'],
            'beverages': [
              {
                'beverage': {'name': '无方案也不能借用的酒水'},
              },
            ],
          },
          // 没有实际订单的对象不能作为专注页的一桌，避免候选列表伪装成订单。
          {
            'food': {
              'recipe': {'name': '无订单的候选料理'},
            },
          },
        ],
        'normalOrderDetailPlans': [
          {
            'order': {
              'traceId': 'normal-two',
              'deskCode': 1,
              'guestName': '普客乙',
              'orderLifecycleSequence': 1,
            },
            'originalFood': {'foodId': 5, 'name': '原点单料理'},
            'originalBeverage': {'beverageId': 6, 'name': '原点单酒水'},
            'executionFood': {
              'foodId': 7,
              'name': '特殊执行料理',
              'recipe': {'id': 7, 'name': '特殊执行料理'},
              'cookerName': '蒸锅',
              'baseIngredientNames': ['竹笋'],
              'extraIngredients': <DataMap>[],
            },
            'executionBeverage': {
              'name': '特殊执行酒水',
              'beverageId': 8,
              'beverage': {'id': 8, 'name': '特殊执行酒水'},
            },
            'usesSpecialExecution': true,
          },
        ],
      },
    };
  }

  final localWrites = <DataMap>[];
  final acknowledgements = <(int, String, int)>[];
  Completer<void>? pendingSave;
  bool failSave = false;

  @override
  Future<void> acknowledgeBarrier(
    int sequence, {
    required String expectedSessionId,
    required int expectedGeneration,
  }) async {
    acknowledgements.add((sequence, expectedSessionId, expectedGeneration));
    await super.acknowledgeBarrier(
      sequence,
      expectedSessionId: expectedSessionId,
      expectedGeneration: expectedGeneration,
    );
  }

  @override
  Future<void> updateLocal(DataMap changes) async {
    localWrites.add(Map.of(changes));
    if (pendingSave != null) await pendingSave!.future;
    if (failSave) throw StateError('合成持久化失败');
    await super.updateLocal(changes);
  }
}

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
    _FocusModel model, {
    double width = 640,
    double scale = 1,
    PageActivityChannel? activityChannel,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final theme = stewardTheme(dark: false, touch: !model.desktop);
    final channel = activityChannel ?? PageActivityChannel();
    if (activityChannel == null) addTearDown(channel.dispose);
    await tester.pumpWidget(
      material.MaterialApp(
        localizationsDelegates: FLocalizations.localizationsDelegates,
        supportedLocales: FLocalizations.supportedLocales,
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
          padding: EdgeInsets.fromLTRB(
            model.desktop ? 104 : 16,
            16,
            model.desktop ? 24 : 16,
            16,
          ),
          child: PageActivityScope(
            channel: channel,
            child: AnimatedBuilder(
              animation: model,
              builder: (_, _) => ServicePage(model),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Finder focusText(String text) => find.descendant(
    of: find.byType(ServiceFocusPage),
    matching: find.text(text),
  );

  // 订单卡片也有“稀客／普客”状态徽标；导航测试必须命中真实子页签，而非同名正文。
  Finder serviceTab(String text) => find.descendant(
    of: find.byType(WorkspaceTabs<String>),
    matching: find.text(text),
  );

  Future<void> focus(WidgetTester tester) async {
    await tester.tap(serviceTab('专注'));
    await tester.pumpAndSettle();
  }

  /// 专注使用真实惰性列表；每次从列表起点寻找目标，避免前一条断言已滚到底后，
  /// 继续单向滚动去寻找被回收的前一张订单。仍须将完整文本滚入实际视口才验收。
  Future<void> revealFocus(WidgetTester tester, Finder target) async {
    final scrollable = find.descendant(
      of: find.byKey(const ValueKey('service-focus-scroll')),
      matching: find.byType(Scrollable),
    );
    tester.state<ScrollableState>(scrollable).position.jumpTo(0);
    await tester.pump();
    await tester.scrollUntilVisible(target, 140, scrollable: scrollable);
    await tester.ensureVisible(target);
    await tester.pumpAndSettle();
  }

  Future<void> preferences(WidgetTester tester) async {
    final title = focusText('专注显示 · 本机');
    await revealFocus(tester, title);
    await tester.tap(title);
    await tester.pumpAndSettle();
  }

  Finder toggle(String key) => find.descendant(
    of: find.byKey(ValueKey(key)),
    matching: find.byType(FSwitch),
  );

  testWidgets('专注只投影实际订单和同一个主方案，重复加料与原码展示保持', (tester) async {
    final model = _FocusModel();
    addTearDown(model.dispose);
    await open(tester, model);
    await focus(tester);
    for (final text in [
      '1 桌 · 稀客甲',
      '8 桌 · 稀客丙',
      '2 桌 · 普客乙',
      '主方案料理',
      '主方案酒水',
      '特殊执行料理',
      '特殊执行酒水',
      '豆腐',
      '海苔',
      '竹笋',
    ]) {
      await revealFocus(tester, focusText(text));
      expect(focusText(text), findsOneWidget);
    }
    await revealFocus(tester, focusText('蘑菇').first);
    expect(focusText('蘑菇'), findsNWidgets(2));
    expect(focusText('不可误用的备选酒水'), findsNothing);
    expect(focusText('不可误用的独立酒水候选'), findsNothing);
    await revealFocus(tester, focusText('阻断 · 食材不足'));
    expect(focusText('无方案也不能借用的酒水'), findsNothing);
    expect(focusText('无订单的候选料理'), findsNothing);
    expect(focusText('暂无主方案，请到稀客页查看阻断原因。'), findsOneWidget);
    expect(focusText('阻断 · 食材不足'), findsOneWidget);
    await revealFocus(tester, focusText('原料理：原点单料理'));
    final originalTags = find.descendant(
      of: find.byType(ServiceFocusPage),
      matching: find.byType(ServiceOriginalOrderTag),
    );
    final originalImages = tester.widgetList<EntityImage>(
      find.descendant(of: originalTags, matching: find.byType(EntityImage)),
    );
    // 专注也必须保留原点单的 5/6；不能因外层显示特殊执行料理 7/酒水 8 就借用后者。
    expect(originalImages.map((image) => (image.kind, image.id)), [
      (EntityImageKind.food, 5),
      (EntityImageKind.beverage, 6),
    ]);
    expect(model.writes, isEmpty);
    expect(model.localWrites, isEmpty);
    expect(model.reads, ['/favorites'], reason: '经营页只注册一次收藏读取；专注切换不重读或写游戏。');
    expect(tester.takeException(), isNull);
  });

  testWidgets('专注按服务端全局顺位混排普稀客，未知顺位不猜时间', (tester) async {
    final model = _FocusModel();
    addTearDown(model.dispose);
    for (final key in serviceFocusPreferences.keys.skip(2)) {
      model.localPreferences[key] = false;
    }
    final rare = records(model.recommendations['recommendations']);
    final normal = records(model.recommendations['normalOrderDetailPlans']);
    object(rare[0]['order'])['serviceOrderPosition'] = 1;
    object(rare[1]['order'])['serviceOrderPosition'] = -1;
    object(normal[0]['order'])['serviceOrderPosition'] = 0;
    await open(tester, model);
    await focus(tester);
    final normalTop = tester.getTopLeft(focusText('2 桌 · 普客乙')).dy;
    final rareTop = tester.getTopLeft(focusText('1 桌 · 稀客甲')).dy;
    final unknownTop = tester.getTopLeft(focusText('8 桌 · 稀客丙')).dy;
    expect(normalTop, lessThan(rareTop));
    expect(rareTop, lessThan(unknownTop));
    // 默认关闭：偏好缺失不能改变服务端的跨通道时间顺序。
    expect(
      model.localPreferences.containsKey(serviceFocusRareFirstPreference),
      isFalse,
    );
    await preferences(tester);
    expect(
      tester.widget<FSwitch>(toggle(serviceFocusRareFirstPreference)).value,
      isFalse,
    );
    await tester.ensureVisible(toggle(serviceFocusRareFirstPreference));
    await tester.tap(toggle(serviceFocusRareFirstPreference));
    await tester.pumpAndSettle();
    expect(model.localPreferences[serviceFocusRareFirstPreference], isTrue);
    expect(model.localWrites.single, {serviceFocusRareFirstPreference: true});
    await preferences(tester);
    await revealFocus(tester, focusText('1 桌 · 稀客甲'));
    expect(
      tester.getTopLeft(focusText('1 桌 · 稀客甲')).dy,
      lessThan(tester.getTopLeft(focusText('8 桌 · 稀客丙')).dy),
    );
    expect(
      tester.getTopLeft(focusText('8 桌 · 稀客丙')).dy,
      lessThan(tester.getTopLeft(focusText('2 桌 · 普客乙')).dy),
    );
    // 局部分组没有原地修改服务端位置或共享profile；重建仍从本机持久偏好恢复。
    expect(object(normal[0]['order'])['serviceOrderPosition'], 0);
    expect(object(rare[0]['order'])['serviceOrderPosition'], 1);
    expect(model.profile.containsKey('serviceOrderSortMode'), isFalse);
    await tester.pumpWidget(const SizedBox());
    await open(tester, model);
    await focus(tester);
    expect(
      tester.getTopLeft(focusText('1 桌 · 稀客甲')).dy,
      lessThan(tester.getTopLeft(focusText('2 桌 · 普客乙')).dy),
    );
    await preferences(tester);
    await tester.ensureVisible(toggle(serviceFocusRareFirstPreference));
    await tester.tap(toggle(serviceFocusRareFirstPreference));
    await tester.pumpAndSettle();
    await preferences(tester);
    await revealFocus(tester, focusText('2 桌 · 普客乙'));
    expect(
      tester.getTopLeft(focusText('2 桌 · 普客乙')).dy,
      lessThan(tester.getTopLeft(focusText('1 桌 · 稀客甲')).dy),
    );
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('专注收藏按钮可独立隐藏并持久恢复，不影响制作字段或共享配置', (tester) async {
    final model = _FocusModel();
    addTearDown(model.dispose);
    final item = records(model.recommendations['recommendations']).first;
    item['customer'] = {'id': 7, 'name': '稀客甲'};
    object(
      object(records(item['executionPlans']).first['food'])['recipe'],
    )['id'] = 11;
    model.resources['/favorites'] = {
      'recipes': <DataMap>[],
      'beverages': <DataMap>[],
    };
    final profile = Map<String, Object?>.of(model.profile);
    await open(tester, model);
    await focus(tester);
    await revealFocus(tester, focusText('收藏料理'));
    expect(focusText('收藏料理'), findsOneWidget);
    await preferences(tester);
    await tester.ensureVisible(toggle(serviceFocusFavoritesPreference));
    expect(
      tester.widget<FSwitch>(toggle(serviceFocusFavoritesPreference)).value,
      isTrue,
    );
    await tester.tap(toggle(serviceFocusFavoritesPreference));
    await tester.pumpAndSettle();
    expect(model.localWrites.single, {serviceFocusFavoritesPreference: false});
    await preferences(tester);
    await revealFocus(tester, focusText('主方案料理'));
    expect(focusText('收藏料理'), findsNothing);
    expect(focusText('收藏酒水'), findsNothing);
    expect(focusText('主方案料理'), findsOneWidget);
    expect(focusText('制作信息已全部隐藏，可在“专注显示”中重新开启。'), findsNothing);
    expect(model.profile, profile);
    expect(model.writes, isEmpty);
    await tester.pumpWidget(const SizedBox());
    await open(tester, model);
    await focus(tester);
    await revealFocus(tester, focusText('主方案料理'));
    expect(focusText('收藏料理'), findsNothing);
    await preferences(tester);
    await tester.ensureVisible(toggle(serviceFocusFavoritesPreference));
    await tester.tap(toggle(serviceFocusFavoritesPreference));
    await tester.pumpAndSettle();
    await preferences(tester);
    await revealFocus(tester, focusText('收藏料理'));
    expect(focusText('收藏料理'), findsOneWidget);
    expect(model.localWrites.last, {serviceFocusFavoritesPreference: true});
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('仅本机显示偏好可在查看设备修改，页面重建恢复且不修改共享配置', (tester) async {
    final model = _FocusModel()..authority['currentDeviceIsPrimary'] = false;
    addTearDown(model.dispose);
    await open(tester, model);
    await focus(tester);
    await preferences(tester);
    for (final key in [
      'serviceFocusShowNormal',
      'serviceFocusShowBeverage',
      'serviceFocusShowOrderTags',
    ]) {
      await tester.ensureVisible(toggle(key));
      await tester.tap(toggle(key));
      await tester.pumpAndSettle();
      expect(model.localPreferences[key], false);
    }
    expect(model.localWrites.map((entry) => entry.keys.single), [
      'serviceFocusShowNormal',
      'serviceFocusShowBeverage',
      'serviceFocusShowOrderTags',
    ]);
    expect(model.writes, isEmpty);
    await tester.pumpWidget(const SizedBox());
    await open(tester, model);
    await focus(tester);
    expect(focusText('主方案料理'), findsOneWidget);
    expect(focusText('主方案酒水'), findsNothing);
    expect(focusText('2 桌 · 普客乙'), findsNothing);
    expect(focusText('料理需求：鲜'), findsNothing);
    expect(tester.takeException(), isNull);
  });

  testWidgets('专注切换保留已提交筛选草稿、选择区和执行状态页签', (tester) async {
    final model = _FocusModel();
    addTearDown(model.dispose);
    await open(tester, model);
    await tester.tap(serviceTab('普客'));
    await tester.pumpAndSettle();
    final input = find.byType(EditableText);
    await tester.enterText(input, '普客乙');
    const draft = TextEditingValue(
      text: '普客乙',
      selection: TextSelection.collapsed(offset: 3),
      composing: TextRange(start: 0, end: 3),
    );
    tester.testTextInput.updateEditingValue(draft);
    await tester.pump();
    final controller = tester.widget<EditableText>(input).controller;
    await tester.tap(find.text('自动化状态'));
    await tester.pumpAndSettle();
    // 文字和选择区在普通子页签切换时仍完整；记录隐藏输入之前的基准，随后
    // 专注页的 ExcludeFocus 会按 Flutter 的正常失焦规则提交 composing。
    expect(controller.text, draft.text);
    expect(controller.selection, draft.selection);
    final beforeFocus = controller.value;
    final originalList = tester.state(find.byType(DenseListDetail));
    await focus(tester);
    await tester.tap(serviceTab('普客'));
    await tester.pumpAndSettle();
    expect(tester.widget<EditableText>(input).controller, same(controller));
    // 专注页隐藏旧输入后 ExcludeFocus 正常提交组合文本；仍精确保留字词与选择区，
    // 不能要求隐藏输入继续维持与系统输入法关联的 composing 区间。
    expect(controller.value, beforeFocus.copyWith(composing: TextRange.empty));
    expect(
      tester
          .widget<WorkspaceTabs<bool>>(find.byType(WorkspaceTabs<bool>))
          .value,
      isTrue,
    );
    expect(tester.state(find.byType(DenseListDetail)), same(originalList));
    expect(model.writes, isEmpty);
  });

  testWidgets('专注隐藏窄屏详情后放行系统返回并排除隐形焦点，返回原页仍保留详情', (tester) async {
    final model = _FocusModel(touch: true);
    addTearDown(model.dispose);
    await open(tester, model, width: 390);
    await tester.tap(find.text('1 桌 · 稀客甲'));
    await tester.pumpAndSettle();
    expect(find.text('返回列表'), findsOneWidget);
    final originalList = tester.state(find.byType(DenseListDetail));
    final input = tester.widget<EditableText>(find.byType(EditableText));
    input.focusNode.requestFocus();
    await tester.pumpAndSettle();
    expect(input.focusNode.hasFocus, isTrue);

    await focus(tester);
    final hiddenList = find.byType(DenseListDetail, skipOffstage: false);
    final hiddenScope = find.descendant(
      of: hiddenList,
      matching: find.byType(PopScope<Object?>, skipOffstage: false),
      skipOffstage: false,
    );
    // 隐藏详情仍保留 State，但必须解除整个路由的返回拦截；只检查视觉隐藏不够。
    expect(tester.state(hiddenList), same(originalList));
    expect(tester.widget<PopScope<Object?>>(hiddenScope).canPop, isTrue);
    expect(
      ModalRoute.of(tester.element(find.byType(ServiceFocusPage)))!
          .popDisposition,
      RoutePopDisposition.bubble,
    );
    expect(input.focusNode.hasFocus, isFalse);
    expect(input.focusNode.canRequestFocus, isFalse);

    await tester.tap(serviceTab('稀客'));
    await tester.pumpAndSettle();
    expect(tester.state(find.byType(DenseListDetail)), same(originalList));
    expect(find.text('返回列表'), findsOneWidget);
    expect(input.focusNode.canRequestFocus, isTrue);
    await tester.binding.handlePopRoute();
    await tester.pumpAndSettle();
    expect(find.text('返回列表'), findsNothing);
    expect(find.text('1 桌 · 稀客甲'), findsOneWidget);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('缺失加料保持未知，实际计算报告进度，断线隐藏旧订单而偏好仍可配置', (tester) async {
    final model = _FocusModel();
    addTearDown(model.dispose);
    final channel = PageActivityChannel();
    addTearDown(channel.dispose);
    final first = records(model.recommendations['recommendations']).first;
    object(records(first['executionPlans']).first['food'])
        .remove('extraIngredients');
    model.business['pending'] = true;
    model.business['isCalculating'] = true;
    await open(tester, model, activityChannel: channel);
    await focus(tester);
    expect(focusText('加料未知'), findsOneWidget);
    expect(channel.value?.message, contains('正在计算方案'));
    expect(channel.value?.busy, isTrue);
    expect(find.textContaining('正在计算方案'), findsNothing);
    model.connected = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(focusText('主方案料理'), findsNothing);
    expect(channel.value?.message, contains('断线时不展示缓存方案'));
    expect(find.textContaining('断线时不展示缓存方案'), findsNothing);
    await preferences(tester);
    expect(
      tester.widget<FSwitch>(toggle('serviceFocusShowRare')).enabled,
      isTrue,
    );
    expect(model.writes, isEmpty);
  });

  testWidgets('显示偏好等待时禁重复提交，失败保留真实偏好并明确反馈', (tester) async {
    final model = _FocusModel()
      ..pendingSave = Completer<void>()
      ..failSave = true;
    addTearDown(model.dispose);
    final channel = PageActivityChannel();
    addTearDown(channel.dispose);
    await open(tester, model, activityChannel: channel);
    await focus(tester);
    await preferences(tester);
    await tester.tap(toggle('serviceFocusShowRare'));
    await tester.pump();
    expect(
      tester.widget<FSwitch>(toggle('serviceFocusShowNormal')).enabled,
      isFalse,
    );
    expect(model.localWrites.length, 1);
    model.pendingSave!.complete();
    await tester.pumpAndSettle();
    expect(model.localPreferences.containsKey('serviceFocusShowRare'), isFalse);
    expect(
      tester.widget<FSwitch>(toggle('serviceFocusShowRare')).value,
      isTrue,
    );
    expect(channel.value?.message, contains('显示偏好未保存：'));
    expect(channel.value?.error, isTrue);
    expect(find.textContaining('显示偏好未保存：'), findsNothing);
    expect(model.writes, isEmpty);
  });

  testWidgets('隐藏专注不污染经营页头，特殊规则和人工屏障完整内容仍可访问', (tester) async {
    final model = _FocusModel();
    addTearDown(model.dispose);
    final channel = PageActivityChannel();
    addTearDown(channel.dispose);
    for (final key in serviceFocusPreferences.keys.skip(2)) {
      model.localPreferences[key] = false;
    }
    model.snapshot['specialBusiness'] = {
      'active': true,
      'displayName': '合成规则',
      'ruleSummary': '必须保留的完整规则',
    };
    model.snapshot['automationSessionId'] = 'focus-synthetic-session';
    model.snapshot['nightBusinessGeneration'] = 1;
    model.business['automation'] = {
      ...model.automation,
      'safetyBarriers': [
        {'sequence': 2, 'title': '合成人工屏障', 'message': '必须核对现场的完整说明'},
      ],
    };
    await open(tester, model, activityChannel: channel);
    expect(channel.value?.message, isNot(contains('制作信息已全部隐藏')));
    await focus(tester);
    expect(channel.value?.message, contains('制作信息已全部隐藏'));
    expect(channel.value?.details, contains('必须保留的完整规则'));
    expect(channel.value?.details, contains('必须核对现场的完整说明'));
    expect(focusText('必须核对现场的完整说明'), findsNothing);
    await tester.tap(find.widgetWithText(StewardButton, '待确认 1'));
    await tester.pumpAndSettle();
    expect(find.text('必须核对现场的完整说明'), findsOneWidget);
    final acknowledge = find.widgetWithText(StewardButton, '已人工处理');
    expect(tester.widget<StewardButton>(acknowledge).onPressed, isNotNull);
    // 弹窗持有原会话边界；后台换局后旧确认不能改写新一局同序号屏障。
    model.snapshot['nightBusinessGeneration'] = 2;
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('连接或经营场次已变化，请关闭后重新打开。'), findsOneWidget);
    expect(acknowledge, findsNothing);
    await tester.tap(find.widgetWithText(StewardButton, '关闭'));
    await tester.pumpAndSettle();
    await tester.tap(serviceTab('稀客'));
    await tester.pumpAndSettle();
    expect(channel.value?.message, isNot(contains('制作信息已全部隐藏')));
    expect(model.writes, isEmpty);
  });

  testWidgets('标题人工屏障入口保留原确认动作与精确会话代次，查看设备不能确认', (tester) async {
    final model = _FocusModel();
    addTearDown(model.dispose);
    model.snapshot['automationSessionId'] = 'synthetic-ack-session';
    model.snapshot['nightBusinessGeneration'] = 7;
    model.business['automation'] = {
      ...model.automation,
      'safetyBarriers': [
        {'sequence': 42, 'title': '合成屏障', 'message': '确认前核对合成现场'},
      ],
    };
    await open(tester, model);
    await focus(tester);
    await tester.tap(find.widgetWithText(StewardButton, '待确认 1'));
    await tester.pumpAndSettle();
    final acknowledge = find.widgetWithText(StewardButton, '已人工处理');
    model.canExecute = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(tester.widget<StewardButton>(acknowledge).onPressed, isNull);
    model.canExecute = true;
    model.publish();
    await tester.pumpAndSettle();
    await tester.tap(acknowledge);
    await tester.pumpAndSettle();
    expect(model.acknowledgements, isEmpty);
    await tester.tap(find.widgetWithText(StewardButton, '确认'));
    await tester.pumpAndSettle();
    expect(model.acknowledgements, [(42, 'synthetic-ack-session', 7)]);
    await tester.tap(find.widgetWithText(StewardButton, '关闭'));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
  });

  for (final touch in [false, true]) {
    testWidgets('390px/150% ${touch ? '触屏' : 'Windows'}专注字段和设置均可滚动完整阅读', (
      tester,
    ) async {
      final model = _FocusModel(touch: touch);
      final first = records(model.recommendations['recommendations']).first;
      object(first['order'])['hasServedFood'] = false;
      object(first['order'])['hasServedBeverage'] = true;
      addTearDown(model.dispose);
      await open(tester, model, width: 390, scale: 1.5);
      await focus(tester);
      // Map.cast 每次读取会包装视图，按实际桌号定位标题，不依赖临时 Map 对象身份。
      final firstHeading = find.descendant(
        of: find.byType(ServiceFocusPage),
        matching: find.byWidgetPredicate(
          (widget) =>
              widget is ServiceOrderHeading &&
              object(widget.item['order'])['deskCode'] == 0,
        ),
      );
      final title = find.descendant(
        of: firstHeading,
        matching: find.text('1 桌 · 稀客甲'),
      );
      final served = find.descendant(
        of: firstHeading,
        matching: find.text('已有酒水'),
      );
      expect(served, findsOneWidget);
      expect(focusText('已有酒水'), findsOneWidget);
      expect(
        tester.getRect(served).left,
        greaterThan(tester.getRect(title).right),
      );
      expect(
        tester.getRect(served).right,
        lessThanOrEqualTo(tester.getRect(firstHeading).right),
      );
      final viewport = tester.getRect(
        find.byKey(const ValueKey('service-focus-scroll')),
      );
      for (final text in ['主方案料理', '主方案酒水', '特殊执行料理', '特殊执行酒水']) {
        await revealFocus(tester, focusText(text));
        final rect = tester.getRect(focusText(text));
        expect(rect.left, greaterThanOrEqualTo(viewport.left));
        expect(rect.right, lessThanOrEqualTo(viewport.right));
        expect(rect.top, greaterThanOrEqualTo(viewport.top));
        expect(rect.bottom, lessThanOrEqualTo(viewport.bottom));
      }
      await preferences(tester);
      await tester.ensureVisible(toggle('serviceFocusShowBeverage'));
      await tester.tap(toggle('serviceFocusShowBeverage'));
      await tester.pumpAndSettle();
      expect(model.localPreferences['serviceFocusShowBeverage'], false);
      expect(tester.takeException(), isNull);
    });
  }
}
