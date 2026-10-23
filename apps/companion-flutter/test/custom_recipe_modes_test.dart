import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';

import '../tool/preview/rich_preview_workbench_model.dart';

/// 只控制内存预览的返回时机，验证 UI 不乐观切模、不重复提交；不接触 HTTP 或存储。
class _SettingsProbeModel extends RichPreviewWorkbenchModel {
  _SettingsProbeModel({super.desktop});
  final settingsWrites = <Map<String, String>>[];
  Completer<void>? settingsGate;
  String settingsError = '';

  @override
  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  }) async {
    if (path == '/custom-recipes/settings') {
      settingsWrites.add(Map.of(query));
      await settingsGate?.future;
      if (settingsError.isNotEmpty) {
        error = settingsError;
        notifyListeners();
        return;
      }
    }
    await super.mutate(path, query: query, body: body, localOnly: localOnly);
  }
}

/// 通过正式工作区操作真实选择器，验证模式绑定和窄屏工具栏，不只比对组件名称。
Future<void> openCustom(
  WidgetTester tester,
  RichPreviewWorkbenchModel model,
  Size size,
) async {
  tester.view.devicePixelRatio = 1;
  tester.view.physicalSize = size;
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
  await tester.tap(find.text('自定义配方'));
  await tester.pumpAndSettle();
}

Future<void> select<T>(WidgetTester tester, String key, T value) async {
  await tester.tap(find.byKey(ValueKey(key)));
  await tester.pumpAndSettle();
  final item = find.byWidgetPredicate(
    (widget) => widget is FSelectItem<T> && widget.value == value,
  );
  await tester.ensureVisible(item);
  await tester.tap(item);
  await tester.pumpAndSettle();
}

void main() {
  WidgetController.hitTestWarningShouldBeFatal = true;
  testWidgets('互斥模式同值不重复写，总开关关闭保留选择且不声称启用', (tester) async {
    final model = _SettingsProbeModel();
    addTearDown(model.dispose);
    await openCustom(tester, model, const Size(1280, 900));
    final source = model.resources['/custom-recipes']!;
    await tester.tap(find.byKey(const ValueKey('custom-mode-customer')));
    await tester.pumpAndSettle();
    expect(model.settingsWrites, isEmpty);
    await tester.tap(find.byKey(const ValueKey('custom-mode-food')));
    await tester.pumpAndSettle();
    expect(model.settingsWrites.single, {
      'expectedModeRevision': '0',
      'activeMode': 'food',
    });
    expect(find.text('当前启用：料理模式'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('custom-enabled')));
    await tester.pumpAndSettle();
    expect(source['enabled'], isFalse);
    expect(source['activeMode'], 'food');
    expect(find.text('未启用'), findsOneWidget);
    expect(find.textContaining('当前启用：'), findsNothing);
    expect(
      tester
          .widget<FRadio>(find.byKey(const ValueKey('custom-mode-food')))
          .value,
      isTrue,
    );
    await tester.tap(find.byKey(const ValueKey('custom-mode-customer')));
    await tester.pumpAndSettle();
    expect(source['enabled'], isFalse);
    expect(source['activeMode'], 'customer');
    expect(model.settingsWrites.last, {
      'expectedModeRevision': '1',
      'activeMode': 'customer',
    });
    expect(find.text('未启用'), findsOneWidget);
    expect(
      tester
          .widget<FRadio>(find.byKey(const ValueKey('custom-mode-food')))
          .value,
      isFalse,
    );
    expect(
      tester
          .widget<FRadio>(find.byKey(const ValueKey('custom-mode-customer')))
          .value,
      isTrue,
    );
  });

  testWidgets('模式保存等待期间保持服务端选择并锁住重复写，失败只在页头报告', (tester) async {
    final model = _SettingsProbeModel();
    addTearDown(model.dispose);
    await openCustom(tester, model, const Size(1280, 900));
    final food = find.byKey(const ValueKey('custom-mode-food'));
    final previousCallback = tester.widget<FRadio>(food).onChange!;
    model.settingsGate = Completer<void>();
    await tester.tap(food);
    // 页头 busy 使用持续动画，有限 pump 验证等待状态，不能等待动画自行结束。
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 40));
    expect(model.settingsWrites, hasLength(1));
    expect(tester.widget<FRadio>(food).enabled, isFalse);
    expect(tester.widget<FRadio>(food).value, isFalse);
    expect(
      tester
          .widget<FSwitch>(find.byKey(const ValueKey('custom-enabled')))
          .enabled,
      isFalse,
    );
    previousCallback(true);
    expect(model.settingsWrites, hasLength(1));
    expect(find.text('正在保存配方设置…'), findsOneWidget);
    model.settingsGate!.complete();
    await tester.pumpAndSettle();
    expect(tester.widget<FRadio>(food).value, isTrue);
    model.settingsGate = null;
    model.settingsError = '合成模式冲突，请重新读取';
    await tester.tap(find.byKey(const ValueKey('custom-mode-customer')));
    await tester.pumpAndSettle();
    expect(find.text('配方设置未保存'), findsOneWidget);
    expect(tester.widget<FRadio>(food).value, isTrue);
    expect(find.text('仅当前模式参与推荐；切换保留另一模式的配方。'), findsNothing);
    expect(tester.takeException(), isNull);
  });

  testWidgets('旧模式回调不能跨连接、权威或模式修订，查看权限不能提交', (tester) async {
    final model = _SettingsProbeModel();
    addTearDown(model.dispose);
    await openCustom(tester, model, const Size(1280, 900));
    final food = find.byKey(const ValueKey('custom-mode-food'));
    for (final change in <void Function()>[
      () => model.connectionVersion++,
      () => model.authority['authorityRevision'] =
          (model.authority['authorityRevision'] as int) + 1,
      () => model.resources['/custom-recipes']!['modeRevision'] =
          (model.resources['/custom-recipes']!['modeRevision'] as int) + 1,
      () => model.busy = true,
    ]) {
      final oldCallback = tester.widget<FRadio>(food).onChange!;
      change();
      oldCallback(true);
      await tester.pump();
      expect(model.settingsWrites, isEmpty);
      model.busy = false;
      model.notifyListeners();
      await tester.pumpAndSettle();
    }
    model.authority['currentDeviceIsPrimary'] = false;
    model.notifyListeners();
    await tester.pumpAndSettle();
    expect(tester.widget<FRadio>(food).enabled, isFalse);
    expect(
      tester
          .widget<FSwitch>(find.byKey(const ValueKey('custom-enabled')))
          .enabled,
      isFalse,
    );
    expect(model.settingsWrites, isEmpty);
  });

  testWidgets('读取状态只进入页头，隐藏自定义配方不继续发布', (tester) async {
    final model = _SettingsProbeModel();
    addTearDown(model.dispose);
    await openCustom(tester, model, const Size(1280, 900));
    final saved = model.resources.remove('/custom-recipes')!;
    model.notifyListeners();
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 40));
    expect(find.text('正在读取自定义配方…'), findsOneWidget);
    expect(find.text('正在读取配方模式…'), findsNothing);
    expect(find.text('等待自定义配方数据'), findsNothing);
    await tester.tap(find.text('收藏').first);
    await tester.pumpAndSettle();
    expect(find.text('正在读取自定义配方…'), findsNothing);
    model.resources['/custom-recipes'] = saved;
  });

  for (final (width, desktop) in [
    (1280.0, true),
    (640.0, true),
    (390.0, false),
    (360.0, false),
  ]) {
    testWidgets('互斥模式在 $width / 150% 完整可点击并具单选语义', (tester) async {
      final model = _SettingsProbeModel(desktop: desktop);
      model.localPreferences['fontScalePercent'] = 150;
      addTearDown(model.dispose);
      final semantics = tester.ensureSemantics();
      try {
        await openCustom(tester, model, Size(width, 950));
        for (final (value, title, selected) in [
          ('food', '料理模式', false),
          ('customer', '稀客模式', true),
        ]) {
          final radio = find.byKey(ValueKey('custom-mode-$value'));
          final rect = tester.getRect(radio);
          final labelRect = tester.getRect(
            find.descendant(of: radio, matching: find.text(title)),
          );
          expect(rect.left, greaterThanOrEqualTo(0));
          expect(rect.right, lessThanOrEqualTo(width));
          expect(labelRect.left, greaterThanOrEqualTo(rect.left));
          expect(labelRect.right, lessThanOrEqualTo(rect.right));
          expect(
            tester.semantics.find(radio),
            isSemantics(
              hasCheckedState: true,
              isChecked: selected,
              isInMutuallyExclusiveGroup: true,
            ),
          );
        }
        final food = find.byKey(const ValueKey('custom-mode-food'));
        final customer = find.byKey(const ValueKey('custom-mode-customer'));
        expect(
          tester.getRect(food).right,
          lessThan(tester.getRect(customer).left),
        );
        await tester.tap(food);
        await tester.pumpAndSettle();
        expect(model.resources['/custom-recipes']!['activeMode'], 'food');
        expect(tester.widget<FRadio>(food).value, isTrue);
        expect(tester.widget<FRadio>(customer).value, isFalse);
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
      } finally {
        // WidgetTester 在测试体返回时即检查语义句柄；普通 addTearDown 比该检查晚。
        // 无论断言通过或失败，都必须在离开测试体之前释放，不削弱实际语义断言。
        semantics.dispose();
      }
    });
  }

  testWidgets('横屏与键盘压缩后配方工具栏和编辑操作仍可滚动访问', (tester) async {
    final model = RichPreviewWorkbenchModel(desktop: false);
    model.localPreferences['fontScalePercent'] = 150;
    addTearDown(model.dispose);
    await openCustom(tester, model, const Size(800, 700));
    await tester.tap(find.text('新增配方'));
    await tester.pumpAndSettle();
    tester.view.physicalSize = const Size(800, 360);
    tester.view.viewInsets = const FakeViewPadding(bottom: 160);
    addTearDown(tester.view.resetViewInsets);
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    final save = find.text('保存配方');
    // Sliver 的尾部操作区在短视口按需布局；先真实滚动，再定位按钮。
    await tester.scrollUntilVisible(
      save,
      60,
      scrollable: find
          .descendant(
            of: find.byKey(const ValueKey('custom-recipe-editor-scroll')),
            matching: find.byType(Scrollable),
          )
          .first,
    );
    await tester.pumpAndSettle();
    await tester.tap(save);
    await tester.pumpAndSettle();
    expect(find.text('请选择稀客和料理。'), findsOneWidget);
    await tester.ensureVisible(find.text('取消'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('取消'));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    // 搜索软键盘保留时返回主面板，模式行不会把剩余列表挤出有限视口。
    await tester.ensureVisible(find.text('新增配方'));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('配方编辑拒绝歧义目录编号，保留可识别的历史绑定', (tester) async {
    final model = RichPreviewWorkbenchModel();
    model.catalog['recipes'] = [
      ...model.catalogItems('recipes'),
      {...model.catalogItems('recipes').first, 'name': '重复编号不能选择'},
    ];
    addTearDown(model.dispose);
    await openCustom(tester, model, const Size(1280, 900));
    await tester.tap(find.text('新增配方'));
    await tester.pumpAndSettle();
    final selector = tester.widget<StewardSelect<int>>(
      find.byKey(const ValueKey('custom-editor-foodId')),
    );
    expect(selector.items.containsKey(101), isFalse);
    expect(selector.items.containsKey(102), isTrue);
    expect(tester.takeException(), isNull);
  });
  for (final (width, desktop) in [
    (640.0, true),
    (390.0, false),
    (360.0, false),
  ]) {
    testWidgets('自定义配方计数新增及稀客三条件在 $width 同行', (tester) async {
      final model = RichPreviewWorkbenchModel(desktop: desktop);
      addTearDown(model.dispose);
      await openCustom(tester, model, Size(width, 900));
      final search = tester.getRect(
        find.byKey(const ValueKey('custom-recipe-search')),
      );
      final add = tester.getRect(find.text('新增配方'));
      final count = tester.getRect(find.text('共 2 项'));
      expect(add.center.dy, closeTo(search.center.dy, 1));
      expect(count.center.dy, closeTo(search.center.dy, 1));
      expect(add.left, greaterThan(search.right));
      await tester.tap(find.text('稀客').first);
      await tester.pumpAndSettle();
      final controls = [
        for (final key in [
          'rare-customer-selector',
          'rare-food-tag-selector',
          'rare-beverage-tag-selector',
        ])
          tester.getRect(find.byKey(ValueKey(key))),
      ];
      expect(controls[0].top, controls[1].top);
      expect(controls[1].top, controls[2].top);
      expect(controls[2].right, lessThanOrEqualTo(width));
      // 手机收紧锚点后仍能真实选择标签，弹层不应被同行布局裁切或挡住。
      await select<String>(tester, 'rare-food-tag-selector', '鲜');
      expect(
        tester
            .widget<StewardSelect<String>>(
              find.byKey(const ValueKey('rare-food-tag-selector')),
            )
            .value,
        '鲜',
      );
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('料理模式可新增多个客人绑定，切回稀客模式保留原有记录', (tester) async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    await openCustom(tester, model, const Size(1280, 900));
    final source = model.resources['/custom-recipes']!;
    final originalIds = records(source['recipes'])
        .map((row) => row['id'])
        .toList();
    await tester.tap(find.text('料理模式'));
    await tester.pumpAndSettle();
    expect(source['activeMode'], 'food');
    expect(source['modeRevision'], 1);
    expect(find.text('当前模式没有匹配的配方'), findsOneWidget);
    await tester.tap(find.text('新增配方'));
    await tester.pumpAndSettle();
    await select<int>(tester, 'custom-editor-foodId', 102);
    await select<int>(tester, 'custom-editor-customerId', 301);
    await select<String>(tester, 'custom-editor-food-tag', '家常');
    await tester.tap(find.text('保存配方'));
    await tester.pumpAndSettle();
    expect(model.error, isEmpty);
    await tester.tap(find.text('绑定稀客'));
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<StewardSelect<int>>(
            find.byKey(const ValueKey('custom-editor-foodId')),
          )
          .value,
      102,
    );
    await select<int>(tester, 'custom-editor-customerId', 302);
    await tester.tap(find.text('保存配方'));
    await tester.pumpAndSettle();
    final bound = records(source['recipes'])
        .where((row) => row['mode'] == 'food')
        .toList();
    expect(bound.map((row) => row['customerId']), [301, 302]);
    expect(bound.every((row) => row['foodId'] == 102), isTrue);
    expect(find.text('共 1 项'), findsOneWidget);
    await tester.tap(find.text('稀客模式'));
    await tester.pumpAndSettle();
    expect(source['activeMode'], 'customer');
    expect(
      records(source['recipes'])
          .where((row) => row['mode'] == 'customer')
          .map((row) => row['id']),
      originalIds,
    );
    expect(find.text('共 2 项'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('切模后返回同一模式仍拒绝旧编辑器，标签选择不接受其他客人条件', (tester) async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    await openCustom(tester, model, const Size(1280, 900));
    await tester.tap(find.byKey(const ValueKey('custom-edit-custom-start')));
    await tester.pumpAndSettle();
    await select<int>(tester, 'custom-editor-customerId', 301);
    final tags = tester.widget<StewardSelect<String>>(
      find.byKey(const ValueKey('custom-editor-food-tag')),
    );
    expect(tags.value, '');
    expect(tags.items.containsKey('流行喜爱'), isFalse);
    expect(tags.items.containsKey('流行厌恶'), isFalse);
    expect(tags.items.containsKey('清淡'), isFalse);
    await model.mutate(
      '/custom-recipes/settings',
      query: {'expectedModeRevision': '0', 'activeMode': 'food'},
    );
    await model.mutate(
      '/custom-recipes/settings',
      query: {'expectedModeRevision': '1', 'activeMode': 'customer'},
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('保存配方'));
    await tester.pumpAndSettle();
    expect(find.text('连接或共享配置已经变化，请关闭后重新编辑。'), findsOneWidget);
    expect(
      records(model.resources['/custom-recipes']!['recipes'])
          .first['customerId'],
      302,
    );
    expect(tester.takeException(), isNull);
  });
}
