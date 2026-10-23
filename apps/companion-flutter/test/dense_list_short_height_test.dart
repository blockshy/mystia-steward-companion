import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

/// 使用真实控件尺寸和纯内存动作，重现横屏键盘后仅48/67.2px的结果视口。
/// 列表、详情必须能操作；不能通过删除列表或放宽命中检查掩盖固定标题挤压。
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

  Widget host(Widget child, double height) {
    final theme = stewardTheme(dark: false, touch: true);
    return material.MaterialApp(
      localizationsDelegates: FLocalizations.localizationsDelegates,
      supportedLocales: FLocalizations.supportedLocales,
      theme: theme.toApproximateMaterialTheme(),
      builder: (context, child) => FTheme(
        data: theme,
        child: MediaQuery(
          data: MediaQuery.of(context).copyWith(
            textScaler: const TextScaler.linear(1.5),
            disableAnimations: true,
          ),
          child: child!,
        ),
      ),
      home: Align(
        alignment: Alignment.topCenter,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: SizedBox(height: height, child: child),
        ),
      ),
    );
  }

  Widget workspace(VoidCallback onDetailAction) => DenseListDetail(
    key: const ValueKey('short-height-workspace'),
    items: [
      for (var index = 0; index < 3; index++) {'id': index},
    ],
    identity: (item) => 'row-${item['id']}',
    title: (item) => '订单${item['id']}',
    subtitle: (_) => '完整料理和酒水说明保留在可滚动行内，不压缩或删除业务内容。',
    detail: (item) => Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text('详情${item['id']}'),
        for (var index = 0; index < 5; index++)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 8),
            child: Text('第$index条合成详情'),
          ),
        StewardButton('详情操作${item['id']}', onPressed: onDetailAction),
      ],
    ),
  );

  void size(WidgetTester tester, double width) {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 500);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
  }

  for (final height in [48.0, 67.2]) {
    testWidgets('宽分栏${height}px首标题完整可点、详情可操作、计数保留读屏', (tester) async {
      size(tester, 800);
      final semantics = tester.ensureSemantics();
      try {
        var actions = 0;
        final content = workspace(() => actions++);
        await tester.pumpWidget(host(content, height));
        await tester.pumpAndSettle();
        final list = find.byType(ListView);
        final bounds = tester.getRect(list);
        final title = tester.getRect(find.text('订单0'));
        expect(bounds.height, greaterThanOrEqualTo(44));
        expect(title.top, greaterThanOrEqualTo(bounds.top));
        expect(title.bottom, lessThanOrEqualTo(bounds.bottom));
        expect(find.text('3 项'), findsNothing);
        final count = find.byKey(const ValueKey('dense-list-count'));
        expect(tester.getSemantics(count).label, '3 项');
        await tester.tap(find.text('订单0'));
        await tester.pumpAndSettle();

        // 先浏览后续订单并切换详情，证明移除固定计数不是仅让首帧看上去正常。
        await tester.scrollUntilVisible(
          find.text('订单2'),
          40,
          scrollable: find.descendant(
            of: list,
            matching: find.byType(Scrollable),
          ),
        );
        await tester.ensureVisible(find.text('订单2'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('订单2'));
        await tester.pumpAndSettle();
        expect(find.text('详情2'), findsOneWidget);
        await tester.ensureVisible(find.text('详情操作2'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('详情操作2'));
        await tester.pumpAndSettle();
        expect(actions, 1);

        // 键盘收起后恢复普通计数栏，但选择仍指向原来的稳定业务ID。
        await tester.pumpWidget(host(content, 400));
        await tester.pumpAndSettle();
        expect(find.text('3 项'), findsOneWidget);
        expect(find.text('详情2'), findsOneWidget);
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
      } finally {
        semantics.dispose();
      }
    });
  }

  testWidgets('窄分栏48px详情与返回入口共用滚动且仍可操作', (tester) async {
    size(tester, 390);
    var actions = 0;
    await tester.pumpWidget(host(workspace(() => actions++), 48));
    await tester.pumpAndSettle();
    await tester.tap(find.text('订单0'));
    await tester.pumpAndSettle();
    final details = find.byKey(const ValueKey('dense-detail-surface'));
    expect(tester.getRect(details).height, 48);
    await tester.ensureVisible(find.text('详情操作0'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('详情操作0'));
    await tester.pumpAndSettle();
    expect(actions, 1);
    await tester.ensureVisible(find.text('返回列表'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('返回列表'));
    await tester.pumpAndSettle();
    expect(find.byType(ListView), findsOneWidget);
    expect(find.text('订单0'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
