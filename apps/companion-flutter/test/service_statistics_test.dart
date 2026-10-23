import 'dart:io';

import 'package:companion_client/companion_client.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:material_ui/material_ui.dart' show SelectableText;
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/service.dart';
import 'package:mystia_steward_companion/features/service_statistics.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/steward_test_fonts.dart';

/// 用生产现场投影验证统计不会等候推荐完成，也不会在断线时沿用旧的数量。
class _StatisticsModel extends FixtureModel {
  _StatisticsModel({super.desktop}) : super(orderCount: 0) {
    snapshot['runtimeDataComplete'] = true;
    snapshot['nightBusiness'] = {
      'orders': [
        {
          'traceId': 'R-1',
          'orderLifecycleSequence': 1,
          'deskCode': 0,
          'guestName': '现场客人',
        },
      ],
    };
    snapshot['normalBusiness'] = {'orders': <DataMap>[]};
    (business['automation']! as DataMap)['safetyBarriers'] = <DataMap>[];
    runtime['placedCookerSnapshotComplete'] = true;
    runtime['placedCookerStatus'] = '合成现场完整';
    runtime['placedCookers'] = [
      {
        'name': '多功能厨具',
        'controllerIndex': 901,
        'typeIds': [1, 4],
        'automationAvailability': 'StrictIdle',
        'automationAvailabilityDiagnostic': '已确认空闲',
      },
      {
        'name': '未确认类型',
        'controllerIndex': 1,
        'typeIds': [2, 2],
        'automationAvailability': 'Unavailable',
        'automationAvailabilityDiagnostic': '等待确切类型',
      },
    ];
  }
  @override
  DataMap get serviceOrders => connected
      ? projectLiveServiceOrders(
          snapshot,
          recommendations,
          contextMatches: true,
          current: business['isCurrent'] == true,
        )
      : const {};

  final acknowledgements = <(int, String, int)>[];
  @override
  Future<void> acknowledgeBarrier(
    int sequence, {
    required String expectedSessionId,
    required int expectedGeneration,
  }) async {
    acknowledgements.add((sequence, expectedSessionId, expectedGeneration));
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    // 几何回归优先使用产品当前中文字体；未安装时才沿用显式测试字体回退。
    if (await loadPreferredStewardTestFont()) return;
    final font = Platform.environment['MYSTIA_UI_FONT'];
    if (font != null)
      await (FontLoader('Microsoft YaHei')..addFont(
            Future.value(ByteData.sublistView(await File(font).readAsBytes())),
          ))
          .load();
  });

  Future<void> mount(
    WidgetTester tester,
    _StatisticsModel model,
    Widget page, {
    double width = 640,
    double scale = 1,
    bool dark = false,
    PageActivityChannel? activityChannel,
  }) async {
    tester.view.physicalSize = Size(width, 960);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final theme = stewardTheme(dark: dark, touch: !model.desktop);
    await tester.pumpWidget(
      material.MaterialApp(
        localizationsDelegates: FLocalizations.localizationsDelegates,
        supportedLocales: FLocalizations.supportedLocales,
        theme: theme.toApproximateMaterialTheme(),
        builder: (_, child) => FTheme(
          data: theme,
          child: MediaQuery(
            data: MediaQueryData(
              size: Size(width, 960),
              textScaler: TextScaler.linear(scale),
            ),
            child: activityChannel == null
                ? child!
                : PageActivityScope(channel: activityChannel, child: child!),
          ),
        ),
        home: Padding(
          // 使用正式壳层可用正文宽度，而不是把整扇桌面窗口当成统计内容宽度。
          padding: model.desktop
              ? const EdgeInsets.fromLTRB(104, 16, 24, 16)
              : const EdgeInsets.all(16),
          child: ListenableBuilder(
            listenable: model,
            // 每次通知重建实际页面，不能反复返回同一个Widget实例而绕过生产刷新路径。
            builder: (_, _) => page is ServicePage
                ? ServicePage(model)
                : ServiceStatisticsPage(model),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('订单数消费即时现场，未知与断线不显示旧统计', (tester) async {
    final model = _StatisticsModel();
    addTearDown(model.dispose);
    await mount(tester, model, ServiceStatisticsPage(model));
    Finder metric(String title) => find
        .ancestor(of: find.text(title), matching: find.byType(Container))
        .first;
    expect(
      find.descendant(of: metric('稀客订单'), matching: find.text('1')),
      findsOneWidget,
    );
    // 推荐仍为空；现场新单必须立即反映到数字，刷新期间不会把旧方案数冒充现场。
    model.business['isCurrent'] = false;
    object(model.snapshot['nightBusiness'])['orders'] = [
      for (var i = 1; i <= 3; i++)
        {
          'traceId': 'R-$i',
          'orderLifecycleSequence': i,
          'deskCode': i - 1,
          'guestName': '客人$i',
        },
    ];
    model.publish();
    await tester.pumpAndSettle();
    expect(
      find.descendant(of: metric('稀客订单'), matching: find.text('3')),
      findsOneWidget,
    );
    expect(find.text('当前事项待同步'), findsOneWidget);
    model.connected = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(
      find.descendant(of: metric('稀客订单'), matching: find.text('—')),
      findsOneWidget,
    );
    expect(find.text('多功能厨具'), findsNothing);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('统计只在真实计算时显示忙碌，失败优先且保留完整错误', (tester) async {
    final model = _StatisticsModel();
    final channel = PageActivityChannel();
    addTearDown(model.dispose);
    addTearDown(channel.dispose);
    model.business['isCurrent'] = false;
    model.business['pending'] = true;
    model.business['isCalculating'] = false;
    await mount(
      tester,
      model,
      ServiceStatisticsPage(model),
      activityChannel: channel,
    );
    expect(channel.value?.message, '统计待同步');
    expect(channel.value?.busy, isFalse);
    expect(channel.value?.error, isFalse);

    model.business['isCalculating'] = true;
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.message, '统计正在更新');
    expect(channel.value?.busy, isTrue);

    // 即使线路仍携带旧计算标记，错误也应胜出；不得永久转圈或丢失页头详情。
    model.business['error'] = '  合成统计失败：当前库存读取不完整。  ';
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.message, '统计更新失败');
    expect(channel.value?.details, '合成统计失败：当前库存读取不完整。');
    expect(channel.value?.error, isTrue);
    expect(channel.value?.busy, isFalse);

    model.connected = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.message, '未连接');
    expect(channel.value?.error, isFalse);
    expect(channel.value?.busy, isFalse);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('统计错误通过真实经营导航接续，隐藏后不残留在其它子页状态', (tester) async {
    final model = _StatisticsModel();
    final channel = PageActivityChannel();
    addTearDown(model.dispose);
    addTearDown(channel.dispose);
    model.business['isCurrent'] = false;
    model.business['error'] = '合成方案计算失败';
    await mount(tester, model, ServicePage(model), activityChannel: channel);
    await tester.tap(find.text('统计信息'));
    await tester.pumpAndSettle();
    expect(channel.value?.message, '统计更新失败');
    expect(channel.value?.details, '合成方案计算失败');
    expect(channel.value?.error, isTrue);
    await tester.tap(find.text('稀客').first);
    await tester.pumpAndSettle();
    expect(channel.value?.message, contains('方案计算失败'));
    expect(channel.value?.message, isNot(contains('统计更新失败')));
    expect(channel.value?.details, isNot(contains('统计更新失败')));
    expect(channel.value?.error, isTrue);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('自动化与暂停原因迁入统计，旧business及断线不冒充当前状态', (tester) async {
    final model = _StatisticsModel();
    addTearDown(model.dispose);
    model.profile = {...model.profile, 'automationEnabled': true};
    model.business['automation'] = {
      ...model.automation,
      'message': '合成当前自动化状态',
      'safetyBarriers': [
        {'sequence': 7, 'title': '合成待确认', 'message': '必须核对的现场结果。'},
      ],
    };
    await mount(tester, model, ServiceStatisticsPage(model));
    expect(find.text('自动化配置'), findsOneWidget);
    expect(find.text('已开启'), findsOneWidget);
    expect(find.text('合成当前自动化状态'), findsOneWidget);
    expect(find.text('必须核对的现场结果。'), findsOneWidget);
    const reasons = {
      'night-business-lifecycle-unavailable': '尚未进入可自动经营的场景',
      'night-business-tutorial-active': '当前经营包含教学流程，本次经营暂停自动化',
      'night-business-tutorial-state-unavailable': '暂时无法确认教学状态，自动化已暂停',
      'future-unknown-reason': '等待游戏确认自动化运行条件',
    };
    for (final reason in reasons.entries) {
      model.snapshot['nightBusinessAutomationBlockReason'] = reason.key;
      model.publish();
      await tester.pumpAndSettle();
      expect(find.text('暂停原因'), findsOneWidget);
      expect(find.text(reason.value), findsOneWidget);
      expect(find.text(reason.key), findsNothing);
      expect(model.snapshot['nightBusinessAutomationBlockReason'], reason.key);
    }
    model.business['isCurrent'] = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('合成当前自动化状态'), findsNothing);
    expect(find.text('必须核对的现场结果。'), findsNothing);
    expect(find.text('等待当前经营状态'), findsOneWidget);
    expect(find.text('当前事项待同步'), findsOneWidget);
    // 错误优先于仍残留的isCurrent/计算标记，不应一边报错一边展示旧执行状态。
    model.business['isCurrent'] = true;
    model.business['isCalculating'] = true;
    model.business['error'] = '合成当前业务失败';
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('状态更新失败'), findsOneWidget);
    expect(find.text('合成当前自动化状态'), findsNothing);
    expect(find.text('必须核对的现场结果。'), findsNothing);
    model.connected = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('暂停原因'), findsNothing);
    expect(find.text('连接后查看待确认事项'), findsOneWidget);
    expect(model.writes, isEmpty);
    expect(model.acknowledgements, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('厨具英文状态映射中文，原始失败和未知诊断仍可完整查看', (tester) async {
    final model = _StatisticsModel();
    addTearDown(model.dispose);
    model.runtime['placedCookerSnapshotComplete'] = false;
    model.runtime['placedCookers'] = <DataMap>[];
    const cases = {
      'not in night business scene': '当前不在夜间经营，无当前厨具',
      'source unavailable; manager error=synthetic-read-failure': '暂时无法读取当前厨具',
      'future-runtime-state; synthetic=unknown': '厨具读取状态待确认',
    };
    for (final entry in cases.entries) {
      model.runtime['placedCookerStatus'] = entry.key;
      // 每次独立挂载重置展开态，证明默认正文不再直接展示英文协议状态。
      await tester.pumpWidget(const SizedBox());
      await mount(tester, model, ServiceStatisticsPage(model));
      await tester.scrollUntilVisible(
        find.text('当前厨具'),
        160,
        scrollable: find.byType(Scrollable).first,
      );
      await tester.pumpAndSettle();
      expect(find.text(entry.value), findsOneWidget);
      expect(find.text(entry.key), findsNothing);
      final diagnostics = find.byKey(
        const ValueKey('service-cooker-diagnostics'),
      );
      await tester.ensureVisible(diagnostics);
      await tester.pumpAndSettle();
      await tester.tap(diagnostics);
      await tester.pumpAndSettle();
      expect(
        find.byWidgetPredicate(
          (widget) => widget is SelectableText && widget.data == entry.key,
        ),
        findsOneWidget,
      );
      expect(model.runtime['placedCookerStatus'], entry.key);
      expect(tester.takeException(), isNull);
    }
    expect(model.writes, isEmpty);
  });

  for (final desktop in [true, false]) {
    testWidgets('统计待确认入口${desktop ? '627桌面' : '390手机'}/150%复用权限、二次确认和场次门禁', (
      tester,
    ) async {
      final model = _StatisticsModel(desktop: desktop);
      addTearDown(model.dispose);
      model.snapshot['automationSessionId'] = 'statistics-synthetic-session';
      model.snapshot['nightBusinessGeneration'] = 8;
      model.business['automation'] = {
        ...model.automation,
        'safetyBarriers': [
          {
            'sequence': 42,
            'title': '合成结果待核对',
            'message': '先核对原订单和当前厨具，再确认已经人工处理。',
          },
        ],
      };
      await mount(
        tester,
        model,
        ServicePage(model),
        width: desktop ? 627 : 390,
        scale: 1.5,
      );
      final statistics = find.text('统计信息');
      await tester.ensureVisible(statistics);
      await tester.pumpAndSettle();
      await tester.tap(statistics);
      await tester.pumpAndSettle();
      final entry = find.byKey(
        const ValueKey('service-statistics-review-barriers'),
      );
      await tester.ensureVisible(entry);
      await tester.pumpAndSettle();
      expect(entry.hitTestable(), findsOneWidget);
      final oldReview = tester.widget<StewardButton>(entry).onPressed!;
      model.canExecute = false;
      model.publish();
      await tester.pumpAndSettle();
      await tester.tap(entry);
      await tester.pumpAndSettle();
      final acknowledge = find.widgetWithText(StewardButton, '已人工处理');
      expect(tester.widget<StewardButton>(acknowledge).onPressed, isNull);
      model.canExecute = true;
      model.publish();
      await tester.pumpAndSettle();
      await tester.ensureVisible(acknowledge);
      await tester.pumpAndSettle();
      await tester.tap(acknowledge);
      await tester.pumpAndSettle();
      expect(model.acknowledgements, isEmpty);
      await tester.tap(find.widgetWithText(StewardButton, '确认'));
      await tester.pumpAndSettle();
      expect(model.acknowledgements, [(42, 'statistics-synthetic-session', 8)]);
      // 同一个查看弹窗跨场次后立即失效，不能确认新场次恰巧复用的序号。
      model.snapshot['nightBusinessGeneration'] = 9;
      model.publish();
      await tester.pumpAndSettle();
      expect(find.text('连接或经营场次已变化，请关闭后重新打开。'), findsOneWidget);
      expect(acknowledge, findsNothing);
      await tester.tap(find.widgetWithText(StewardButton, '关闭'));
      await tester.pumpAndSettle();
      oldReview();
      await tester.pumpAndSettle();
      expect(find.text('人工确认事项'), findsNothing);
      expect(model.acknowledgements, [(42, 'statistics-synthetic-session', 8)]);
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  for (final width in [1280.0, 627.0, 390.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('统计表在$width/$scale指标卡等高且图片不用实例ID', (tester) async {
        final model = _StatisticsModel(desktop: width != 390);
        addTearDown(model.dispose);
        await mount(
          tester,
          model,
          ServiceStatisticsPage(model),
          width: width,
          scale: scale,
        );
        // 比较真正绘制背景的卡片边界，而非外层行约束；手机大字号仍完整展示名称
        // 和数量，不能以固定高度或裁切文本换取对齐。其余分区通过同一个滚动区访问。
        Rect card(String title) =>
            tester.getRect(find.byKey(ValueKey('service-statistic-$title')));
        for (final pair in const [('稀客订单', '普客订单')]) {
          final left = card(pair.$1);
          final right = card(pair.$2);
          expect(left.top, closeTo(right.top, .1));
          expect(left.bottom, closeTo(right.bottom, .1));
          expect(left.width, closeTo(right.width, .1));
          expect(right.left - left.right, closeTo(10, .1));
        }
        for (final title in const ['稀客订单', '普客订单']) {
          final text = tester.getRect(find.text(title));
          expect(text.bottom, lessThanOrEqualTo(card(title).bottom - 9.9));
          expect(text.right, lessThanOrEqualTo(card(title).right - 9.9));
        }
        expect(find.textContaining('请从上方'), findsNothing);
        // 整页只保留三个业务口径分区；“暂无待确认”是概况摘要，不独占大卡片。
        expect(
          tester
              .widgetList<Section>(find.byType(Section))
              .map((item) => item.title),
          ['经营概况', '当前厨具', '自动化拟分配'],
        );
        final section = find.widgetWithText(Section, '当前厨具');
        final images = tester.widgetList<EntityImage>(
          find.descendant(of: section, matching: find.byType(EntityImage)),
        );
        expect(images.map((image) => image.id), [1, 4, null]);
        expect(find.text('空闲可用'), findsOneWidget);
        expect(find.text('暂不可用'), findsOneWidget);
        // 当前厨具卡中的名称与状态分别成行，状态不是隔着整屏空白浮在最右边。
        // 验证真正的文字盒完整处于卡内，不能仅凭“无overflow异常”掩盖裁切。
        for (final entry in const [
          (0, '多功能厨具', '空闲可用'),
          (1, '未确认类型', '暂不可用'),
        ]) {
          final owner = find.byKey(ValueKey('service-cooker-${entry.$1}'));
          final bounds = tester.getRect(owner);
          final name = tester.getRect(
            find.descendant(of: owner, matching: find.text(entry.$2)),
          );
          final status = tester.getRect(
            find.descendant(of: owner, matching: find.text(entry.$3)),
          );
          expect(status.top, greaterThanOrEqualTo(name.bottom));
          for (final text in [name, status]) {
            expect(text.left, greaterThanOrEqualTo(bounds.left + 9.9));
            expect(text.right, lessThanOrEqualTo(bounds.right - 9.9));
            expect(text.bottom, lessThanOrEqualTo(bounds.bottom - 9.9));
          }
        }
        await tester.scrollUntilVisible(
          find.text('自动化拟分配'),
          160,
          scrollable: find.byType(Scrollable).first,
        );
        await tester.pumpAndSettle();
        expect(find.text('可用锅位'), findsOneWidget);
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
      });
    }
  }

  for (final desktop in [true, false]) {
    testWidgets('深色${desktop ? '627桌面' : '390手机'}/150%长厨具和分配事实完整可读', (
      tester,
    ) async {
      final model = _StatisticsModel(desktop: desktop);
      addTearDown(model.dispose);
      const longName = '合成多功能厨具：这是用于验证长名称自然换行并保留全部信息的实例';
      final cookers = records(model.runtime['placedCookers']);
      cookers[0]['name'] = longName;
      cookers[1]['automationAvailability'] = 'FutureUnavailable';
      cookers[1]['automationAvailabilityDiagnostic'] =
          'synthetic-future-cooker-state';
      model.runtime['placedCookers'] = cookers;
      model.business['automation'] = {
        ...model.automation,
        'resourceOverview': {
          'cookers': [
            {
              'label': '合成蒸锅分配',
              'cookerType': 4,
              'normalReserved': 7,
              'rareReserved': -1,
              'capacity': null,
            },
          ],
        },
      };
      await mount(
        tester,
        model,
        ServiceStatisticsPage(model),
        width: desktop ? 627 : 390,
        scale: 1.5,
        dark: true,
      );
      final owner = find.byKey(const ValueKey('service-cooker-0'));
      await tester.scrollUntilVisible(
        owner,
        120,
        scrollable: find.byType(Scrollable).first,
      );
      await tester.pumpAndSettle();
      final card = tester.getRect(owner);
      final title = find.descendant(of: owner, matching: find.text(longName));
      final status = find.descendant(of: owner, matching: find.text('空闲可用'));
      expect(tester.widget<Text>(title).maxLines, isNull);
      expect(tester.getRect(title).right, lessThanOrEqualTo(card.right - 9.9));
      expect(
        tester.getRect(status).top,
        greaterThanOrEqualTo(tester.getRect(title).bottom),
      );
      expect(
        tester.getRect(status).bottom,
        lessThanOrEqualTo(card.bottom - 9.9),
      );
      expect(find.text('状态未知'), findsOneWidget);
      // 未识别的可用性保持未知；诊断必须可操作展开，不能删字段来让卡片显得整齐。
      final diagnostics = find.byKey(
        const ValueKey('service-cooker-diagnostics'),
      );
      await tester.ensureVisible(diagnostics);
      await tester.pumpAndSettle();
      await tester.tap(diagnostics);
      await tester.pumpAndSettle();
      expect(
        find.byWidgetPredicate(
          (widget) =>
              widget is SelectableText &&
              (widget.data?.contains('synthetic-future-cooker-state') ?? false),
        ),
        findsOneWidget,
      );

      await tester.scrollUntilVisible(
        find.text('合成蒸锅分配'),
        120,
        scrollable: find.byType(Scrollable).first,
      );
      await tester.pumpAndSettle();
      final allocation = find
          .ancestor(of: find.text('合成蒸锅分配'), matching: find.byType(Container))
          .first;
      final allocationBounds = tester.getRect(allocation);
      for (final label in ['普客计划', '稀客计划', '可用锅位']) {
        final text = tester.getRect(
          find.descendant(of: allocation, matching: find.text(label)),
        );
        expect(text.left, greaterThanOrEqualTo(allocationBounds.left + 9.9));
        expect(text.right, lessThanOrEqualTo(allocationBounds.right - 9.9));
      }
      expect(
        find.descendant(of: allocation, matching: find.text('7')),
        findsOneWidget,
      );
      expect(
        find.descendant(of: allocation, matching: find.text('—')),
        findsNWidgets(2),
      );
      expect(model.automation['resourceOverview'], {
        'cookers': [
          {
            'label': '合成蒸锅分配',
            'cookerType': 4,
            'normalReserved': 7,
            'rareReserved': -1,
            'capacity': null,
          },
        ],
      });
      expect(model.writes, isEmpty);
      expect(model.acknowledgements, isEmpty);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('统计Tab位于专注右侧，返回保留原筛选且订单顶部无厨具汇总', (tester) async {
    final model = _StatisticsModel();
    addTearDown(model.dispose);
    await mount(tester, model, ServicePage(model));
    expect(find.textContaining('· 普客'), findsNothing);
    await tester.enterText(
      find.byKey(const ValueKey('service-name-filter')),
      '现场',
    );
    await tester.pumpAndSettle();
    expect(
      tester.getTopLeft(find.text('统计信息')).dx,
      greaterThan(tester.getTopLeft(find.text('专注')).dx),
    );
    await tester.tap(find.text('统计信息'));
    await tester.pumpAndSettle();
    expect(find.text('经营概况'), findsOneWidget);
    expect(find.byKey(const ValueKey('service-name-filter')), findsNothing);
    await tester.tap(find.text('稀客').first);
    await tester.pumpAndSettle();
    expect(find.text('现场'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
