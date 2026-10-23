import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';

import 'support/fixture_model.dart';

/// 用可控 Future 模拟推荐读取，验证真实外壳中的等待状态和布局，不连接游戏。
class _LoadingModel extends FixtureModel {
  _LoadingModel({required super.desktop}) : super(orderCount: 0);
  Completer<void>? pending;

  @override
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  }) {
    pending = Completer<void>();
    return pending!.future;
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

  test('多个来源优先显示错误，父级清空与去重不丢失局部反馈', () {
    final channel = PageActivityChannel();
    addTearDown(channel.dispose);
    final parent = Object(), field = Object(), duplicate = Object();
    const loading = PageActivity(message: '读取中', busy: true);
    const failure = PageActivity(
      message: '颜色未保存',
      details: '网络已断开，请恢复连接后重试。',
      error: true,
    );
    channel.publish(parent, loading);
    channel.publish(field, failure);
    channel.publish(duplicate, failure);
    expect(channel.value?.message, '颜色未保存 · 2 项');
    expect(channel.value?.details, contains('读取中'));
    expect(channel.value?.details, contains('网络已断开'));
    expect(channel.value?.error, isTrue);
    channel.publish(parent, null);
    channel.clear(duplicate);
    expect(channel.value, failure);
    channel.clear(field);
    expect(channel.value, isNull);
  });

  testWidgets('同页隐藏子树撤回提示，恢复后重新发布当前状态', (tester) async {
    final channel = PageActivityChannel();
    addTearDown(channel.dispose);
    Widget surface(bool active) => Directionality(
      textDirection: TextDirection.ltr,
      child: PageActivityScope(
        channel: channel,
        child: TickerMode(
          enabled: active,
          child: const PageActivityReporter(
            activity: PageActivity(message: '局部保存失败', error: true),
            child: Text('保留的子页'),
          ),
        ),
      ),
    );
    await tester.pumpWidget(surface(true));
    expect(channel.value?.message, '局部保存失败');
    await tester.pumpWidget(surface(false));
    await tester.pump();
    expect(channel.value, isNull);
    await tester.pumpWidget(surface(true));
    await tester.pump();
    expect(channel.value?.message, '局部保存失败');
    await tester.pumpWidget(const SizedBox());
    await tester.pump();
    expect(channel.value, isNull);
  });

  testWidgets('旧发布器卸载不能清除同页新发布器，卸载外壳后的帧回调安全', (tester) async {
    final channel = PageActivityChannel();
    Widget surface(String id, String message) => Directionality(
      textDirection: TextDirection.ltr,
      child: PageActivityScope(
        channel: channel,
        child: PageActivityReporter(
          key: ValueKey(id),
          activity: PageActivity(message: message),
          child: const Text('正文'),
        ),
      ),
    );
    await tester.pumpWidget(surface('old', '旧请求'));
    await tester.pumpWidget(surface('new', '新请求'));
    await tester.pump();
    expect(channel.value?.message, '新请求');
    channel.dispose();
    await tester.pumpWidget(const SizedBox());
    await tester.pump();
    expect(tester.takeException(), isNull);
  });

  testWidgets('手机短横屏键盘展开时推荐错误可点开全文且不重建输入草稿', (tester) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(800, 360);
    tester.view.padding = const FakeViewPadding(top: 24, bottom: 20);
    tester.view.viewPadding = const FakeViewPadding(top: 24, bottom: 20);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPadding);
    addTearDown(tester.view.resetViewPadding);
    addTearDown(tester.view.resetViewInsets);
    final model = _LoadingModel(desktop: false);
    addTearDown(model.dispose);
    model.localPreferences = {
      'lastPage': 2,
      'fontScalePercent': 150,
      'reduceMotion': true,
    };
    await tester.runAsync(() => rootBundle.loadString('pubspec.yaml'));
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pumpAndSettle();
    expect(model.pending, isNotNull);

    final input = find.descendant(
      of: find.byKey(const ValueKey('recommendation-name-filter')),
      matching: find.byType(EditableText),
    );
    await tester.ensureVisible(input);
    await tester.pumpAndSettle();
    await tester.enterText(input, '海鲜味噌汤');
    final controller = tester.widget<EditableText>(input).controller;
    final inputElement = tester.element(input);
    final draft = controller.value.copyWith(
      selection: const TextSelection(baseOffset: 1, extentOffset: 3),
      composing: const TextRange(start: 0, end: 3),
    );
    controller.value = draft;

    // 使用生产外壳真正消费的键盘 inset，而不是缩小单个页面夹具。普通等待
    // 状态仍应收起辅助区，把高度留给输入；错误到达才恢复紧凑的可点击页头。
    tester.view.viewInsets = const FakeViewPadding(bottom: 160);
    tester.view.padding = const FakeViewPadding(top: 24);
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('workbench-page-title')), findsNothing);
    expect(find.byKey(const ValueKey('workbench-status-bar')), findsNothing);
    expect(controller.value, draft);

    const failure = '合成推荐读取失败：连接中断，请恢复连接后重试；原筛选条件与输入草稿保留。';
    model.pending!.completeError(StateError(failure));
    await tester.pumpAndSettle();
    final errorButton = find.byKey(
      const ValueKey('workbench-page-activity-button'),
    );
    expect(errorButton.hitTestable(), findsOneWidget);
    expect(find.text('推荐更新失败'), findsOneWidget);
    final bounds = tester.getRect(errorButton);
    expect(bounds.top, greaterThanOrEqualTo(24));
    expect(bounds.bottom, lessThanOrEqualTo(200));
    expect(bounds.height, greaterThanOrEqualTo(44));
    expect(identical(tester.element(input), inputElement), isTrue);
    expect(controller.value, draft);
    expect(tester.takeException(), isNull);

    // 只验证摘要存在不足以覆盖这次回归：必须在键盘仍展开时实际点击详情，
    // 验证完整诊断未被省略、关闭操作可达，且短高度对话框没有布局溢出。
    await tester.tap(errorButton);
    await tester.pumpAndSettle();
    final details = tester.widget<Text>(
      find.byKey(const ValueKey('workbench-page-activity-details')),
    );
    expect(details.data, contains(failure));
    expect(details.maxLines, isNull);
    expect(find.text('关闭').hitTestable(), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.tap(find.text('关闭'));
    await tester.pumpAndSettle();
    expect(
      identical(tester.widget<EditableText>(input).controller, controller),
      isTrue,
    );
    expect(controller.text, draft.text);
    expect(controller.selection, draft.selection);
    expect(errorButton.hitTestable(), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final width in [360.0, 390.0, 624.0, 1280.0]) {
    for (final scale in [100, 150]) {
      testWidgets('页头等待与正文稳定，跨页不串状态 $width/$scale', (tester) async {
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(width, 900);
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        final model = _LoadingModel(desktop: width >= 624);
        addTearDown(model.dispose);
        model.localPreferences = {
          'lastPage': 2,
          'fontScalePercent': scale,
          'reduceMotion': true,
        };
        await tester.runAsync(() => rootBundle.loadString('pubspec.yaml'));
        await tester.pumpWidget(
          ProviderScope(
            overrides: [workbenchProvider.overrideWithValue(model)],
            child: const StewardApp(),
          ),
        );
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 300));
        await tester.pump();
        final title = tester.getRect(
          find.byKey(const ValueKey('workbench-page-title')),
        );
        final status = tester.getRect(
          find.byKey(const ValueKey('workbench-page-activity')),
        );
        final content = find.byKey(const ValueKey('workbench-page-content'));
        final top = tester.getTopLeft(content).dy;
        expect(status.left, greaterThan(title.right));
        expect(status.right, closeTo(width - (model.desktop ? 24 : 16), 0.1));
        // 手机提示与标题必须同一行；桌面与标题/介绍组成的页头区域垂直对齐。
        expect(status.center.dy, lessThan(top));
        if (!model.desktop) {
          expect((status.center.dy - title.center.dy).abs(), lessThan(2));
          expect(
            tester
                .getSize(find.byKey(const ValueKey('workbench-status-bar')))
                .height,
            lessThanOrEqualTo(32),
          );
        }
        expect(model.pending, isNotNull);
        // 触屏也能查看完整提示；打开详情不向正文插入行。提示恢复时，对话框读取
        // 当前通道，而非继续展示点击前已过期的状态。用真实按钮点击覆盖可访问入口。
        await tester.tap(
          find.byKey(const ValueKey('workbench-page-activity-button')),
        );
        await tester.pumpAndSettle();
        expect(
          find.byKey(const ValueKey('workbench-page-activity-details')),
          findsOneWidget,
        );
        model.pending!.complete();
        await tester.pumpAndSettle();
        expect(find.text('当前没有待处理的页面提示。'), findsOneWidget);
        await tester.tap(find.text('关闭').last);
        await tester.pumpAndSettle();
        expect(tester.getTopLeft(content).dy, top);
        expect(
          find.byKey(const ValueKey('workbench-page-activity')),
          findsNothing,
        );
        // 切换页面后隐藏推荐页不能把它的后续状态放到设置页标题。
        await tester.tap(find.text('设置').last);
        await tester.pumpAndSettle();
        model.connected = false;
        model.publish();
        await tester.pumpAndSettle();
        expect(
          tester
              .widget<Text>(
                find.byKey(const ValueKey('workbench-page-activity')),
              )
              .data,
          '未连接',
        );
        expect(find.textContaining('恢复后自动更新推荐'), findsNothing);
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
      });
    }
  }
}
