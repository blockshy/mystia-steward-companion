import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/overview.dart';
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 真实页面/控件投影到独立页头通道，验证清理正文不会清掉未知状态或并发错误。
/// 所有模型和保存回调只在内存中运行，不访问平台配置、网络或游戏。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;

  Widget host(PageActivityChannel channel, Widget child) {
    final theme = stewardTheme(dark: false, touch: true);
    return material.MaterialApp(
      localizationsDelegates: FLocalizations.localizationsDelegates,
      supportedLocales: FLocalizations.supportedLocales,
      theme: theme.toApproximateMaterialTheme(),
      builder: (context, child) => FTheme(data: theme, child: child!),
      home: PageActivityScope(channel: channel, child: child),
    );
  }

  testWidgets('总览只投影连接与数据提示，自动化屏障迁到经营统计且不改模型', (tester) async {
    final model = FixtureModel(orderCount: 0)..connected = false;
    final channel = PageActivityChannel();
    addTearDown(model.dispose);
    addTearDown(channel.dispose);
    await tester.pumpWidget(
      host(
        channel,
        ListenableBuilder(
          listenable: model,
          builder: (_, _) => OverviewPage(model),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(channel.value?.message, '未连接');
    expect(channel.value?.details, contains('凭据'));
    expect(find.text('待处理事项'), findsNothing);
    expect(find.text('运行时库存尚未就绪。'), findsNothing);
    model.connected = true;
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value?.message, '游戏数据尚未就绪');
    model.snapshot['runtimeDataComplete'] = true;
    model.publish();
    await tester.pumpAndSettle();
    expect(channel.value, isNull);
    expect(find.text('当前没有需要人工确认的安全屏障。'), findsNothing);
    model.business['automation'] = {
      'safetyBarriers': [
        {'title': '合成屏障', 'message': '合成动作结果未知，需核对原订单。'},
      ],
    };
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('合成屏障'), findsNothing);
    expect(find.text('合成动作结果未知，需核对原订单。'), findsNothing);
    expect(channel.value, isNull);
    expect(model.automation['safetyBarriers'], [
      {'title': '合成屏障', 'message': '合成动作结果未知，需核对原订单。'},
    ]);
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('两个实际颜色控件错误聚合，成功控件不能清空另一错误且隐藏后撤回', (tester) async {
    final channel = PageActivityChannel();
    final active = ValueNotifier(true);
    addTearDown(channel.dispose);
    addTearDown(active.dispose);
    var secondFails = true;
    final calls = <String>[];
    await tester.pumpWidget(
      host(
        channel,
        ValueListenableBuilder<bool>(
          valueListenable: active,
          builder: (_, visible, _) => TickerMode(
            enabled: visible,
            child: SingleChildScrollView(
              child: Column(
                children: [
                  ColorSetting(
                    key: const ValueKey('first-color'),
                    title: '厨具颜色',
                    value: '#102030',
                    onSave: (_) async {
                      calls.add('first');
                      throw StateError('第一项合成保存失败');
                    },
                  ),
                  ColorSetting(
                    key: const ValueKey('second-color'),
                    title: '座位颜色',
                    value: '#102030',
                    onSave: (_) async {
                      calls.add('second');
                      if (secondFails) throw StateError('第二项合成保存失败');
                    },
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
    Future<void> choose(String owner) async {
      final palette = find.descendant(
        of: find.byKey(ValueKey(owner)),
        matching: find.byKey(const ValueKey('palette-#FFDB2E')),
      );
      await tester.ensureVisible(palette);
      await tester.tap(palette);
      await tester.pumpAndSettle();
    }

    await choose('first-color');
    await choose('second-color');
    expect(channel.value?.error, isTrue);
    expect(channel.value?.details, contains('第一项合成保存失败'));
    expect(channel.value?.details, contains('第二项合成保存失败'));
    expect(find.textContaining('合成保存失败'), findsNothing);
    secondFails = false;
    await choose('second-color');
    expect(channel.value?.details, contains('第一项合成保存失败'));
    expect(channel.value?.details, isNot(contains('第二项合成保存失败')));
    active.value = false;
    await tester.pumpAndSettle();
    expect(channel.value, isNull);
    active.value = true;
    await tester.pumpAndSettle();
    expect(channel.value?.details, contains('第一项合成保存失败'));
    expect(calls, ['first', 'second', 'second']);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
}
