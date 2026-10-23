import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/resource_management.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';

import 'support/fixture_model.dart';

/// 图片状态与资源列表均为明确合成事实，门禁测试不建立网络连接或启动游戏。
class _ResourcesModel extends FixtureModel {
  _ResourcesModel({super.desktop});
  @override
  DataMap gameMediaStatus = {'phase': 'ready', 'completed': 24, 'total': 24};
  @override
  List<DataMap> get gameMediaItems => [
    for (var i = 0; i < 24; i++) {'kind': 'food', 'id': i, 'name': '合成料理 $i'},
  ];
  int reloads = 0;
  @override
  Future<void> reloadGameMedia() async {
    reloads++;
  }

  void phase(
    String value, {
    String message = '合成进度',
    int completed = 3,
    int total = 24,
  }) {
    gameMediaStatus = {
      'phase': value,
      'completed': completed,
      'total': total,
      'message': message,
      if (value == 'failed') 'error': '合成失败说明',
    };
    notifyListeners();
  }
}

class _MixedResourcesModel extends _ResourcesModel {
  @override
  List<DataMap> get gameMediaItems => [
    ...super.gameMediaItems,
    {'kind': 'beverage', 'id': 100, 'name': '合成茶水'},
  ];
}

void main() {
  for (final width in [390.0, 640.0]) {
    for (final scale in [100, 150]) {
      testWidgets('缓存核验到等待和提取 $width/$scale 始终复用独立面板，撤回旧弹窗', (tester) async {
        tester.view.physicalSize = Size(width, 800);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        final model = _ResourcesModel(desktop: width >= 640);
        model.localPreferences['fontScalePercent'] = scale;
        addTearDown(model.dispose);
        await tester.pumpWidget(
          ProviderScope(
            overrides: [workbenchProvider.overrideWithValue(model)],
            child: const StewardApp(),
          ),
        );
        await tester.pumpAndSettle();
        final navigator = tester.state<NavigatorState>(
          find.byType(Navigator).first,
        );
        navigator.push(
          PageRouteBuilder<void>(
            pageBuilder: (_, __, ___) => const Text('核验前的编辑窗口'),
          ),
        );
        await tester.pumpAndSettle();
        model.phase('verifying', message: '正在确认游戏图片缓存', completed: 0, total: 0);
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 200));
        expect(find.text('初始化游戏图像资源'), findsOneWidget);
        expect(find.text('正在核验缓存与资源目录'), findsOneWidget);
        expect(
          find.text('校验完成后自动进入工作台；需要完整确认时，请进入存档。可用的图片会保留复用。'),
          findsNothing,
        );
        expect(find.text('等待游戏目录就绪'), findsNothing);
        expect(find.text('核验前的编辑窗口'), findsNothing);
        expect(find.byType(Navigator), findsNothing);
        final panel = find.byKey(const ValueKey('game-media-initialization'));
        expect(panel, findsOneWidget);
        final panelElement = tester.element(panel);
        expect(find.byType(WorkbenchShell), findsNothing);
        expect(
          find.byKey(const ValueKey('workbench-page-title')),
          findsNothing,
        );
        expect(
          find.byKey(const ValueKey('resource-verification-status')),
          findsNothing,
        );
        expect(model.canEdit, isFalse);
        expect(find.byKey(const ValueKey('nav-4')), findsNothing);
        // 有效缓存核验失败后等待目录、实际提取、最终清单核验都属于同一初始化流程。
        // 除展示内容外 Element 不重建，且整个流程不曾提前挂载业务导航。
        model.phase(
          'waiting',
          message: '图片缓存无法直接复用，等待游戏目录就绪',
          completed: 0,
          total: 0,
        );
        await tester.pump();
        expect(identical(tester.element(panel), panelElement), isTrue);
        expect(find.text('图片缓存无法直接复用，等待游戏目录就绪'), findsOneWidget);
        expect(find.text('等待游戏目录就绪'), findsOneWidget);
        expect(find.text('正在核验缓存与资源目录'), findsNothing);
        model.phase('extracting', message: '正在读取游戏图像');
        await tester.pump();
        expect(identical(tester.element(panel), panelElement), isTrue);
        expect(find.text('3 / 24'), findsOneWidget);
        expect(find.byType(FDeterminateProgress), findsOneWidget);
        model.phase('verifying', message: '正在验证游戏资源目录');
        await tester.pump();
        expect(identical(tester.element(panel), panelElement), isTrue);
        expect(find.byType(Navigator), findsNothing);
        expect(find.byType(WorkbenchShell), findsNothing);
        expect(model.localPreferences['lastPage'], isNot(4));
        // 核验中产生的最后一个手柄输入不得在重新挂载业务壳时重放。
        model.input('nextTab');
        await tester.pump();
        model.phase('ready');
        await tester.pumpAndSettle();
        expect(find.byType(Navigator), findsOneWidget);
        expect(panel, findsNothing);
        expect(
          tester
              .widget<Text>(find.byKey(const ValueKey('workbench-page-title')))
              .data,
          '经营',
        );
        model.input('nextTab');
        await tester.pumpAndSettle();
        expect(
          tester
              .widget<Text>(find.byKey(const ValueKey('workbench-page-title')))
              .data,
          '推荐',
        );
        expect(tester.takeException(), isNull);
      });
    }
  }
  testWidgets('资源搜索按名称或编号即时筛选，并与分类交叉且不丢草稿', (tester) async {
    final model = _MixedResourcesModel();
    addTearDown(model.dispose);
    await tester.pumpWidget(
      material.MaterialApp(
        home: FTheme(
          data: stewardTheme(dark: false, touch: false),
          child: ResourceManagementPage(model),
        ),
      ),
    );
    await tester.pumpAndSettle();
    final input = find.descendant(
      of: find.byKey(const ValueKey('resource-media-search')),
      matching: find.byType(EditableText),
    );
    await tester.enterText(input, ' 茶水 ');
    await tester.pumpAndSettle();
    expect(find.text('合成茶水'), findsOneWidget);
    expect(
      tester.widget<EntityImage>(find.byType(EntityImage)).kind,
      EntityImageKind.beverage,
    );
    await tester.enterText(input, '11');
    await tester.pumpAndSettle();
    expect(find.text('合成料理 11'), findsOneWidget);
    expect(find.text('合成茶水'), findsNothing);
    await tester.tap(find.byType(StewardSelect<String>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('酒水（1）').last);
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(find.text('没有匹配的图片'), findsOneWidget);
    expect(tester.widget<EditableText>(input).controller.text, '11');
    await tester.enterText(input, '100');
    await tester.pumpAndSettle();
    expect(find.text('合成茶水'), findsOneWidget);
    expect(model.reloads, 0); // 搜索及分类绝不触发重新提取或业务写入。
    expect(tester.takeException(), isNull);
  });
  for (final size in const [Size(390, 800), Size(640, 700), Size(360, 170)]) {
    testWidgets('资源列表在 $size /150% 分类、图片和重读可用且短高不溢出', (tester) async {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final model = _ResourcesModel(desktop: size.width >= 640);
      addTearDown(model.dispose);
      final theme = stewardTheme(dark: false, touch: !model.desktop);
      await tester.pumpWidget(
        material.MaterialApp(
          home: FTheme(
            data: theme,
            child: MediaQuery(
              data: MediaQueryData(
                size: size,
                textScaler: TextScaler.linear(1.5),
              ),
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: ResourceManagementPage(model),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.byType(EntityImage), findsWidgets);
      expect(
        tester
            .widgetList<EntityImage>(find.byType(EntityImage))
            .every((item) => item.size == 64),
        isTrue,
      );
      await tester.tap(find.widgetWithText(StewardButton, '重新读取'));
      await tester.pump(const Duration(milliseconds: 250));
      await tester.pumpAndSettle();
      expect(model.reloads, 1);
      expect(tester.takeException(), isNull);
    });
  }
  testWidgets('初始化与失败门禁撤回业务及已打开弹窗，ready后恢复工作区', (tester) async {
    final model = _ResourcesModel(desktop: false);
    addTearDown(model.dispose);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pumpAndSettle();
    final navigator = tester.state<NavigatorState>(
      find.byType(Navigator).first,
    );
    // 真实 Navigator route 被替换，不能只靠遮挡 home 让旧编辑器继续存在。
    navigator.push(
      PageRouteBuilder<void>(pageBuilder: (_, __, ___) => const Text('旧弹窗内容')),
    );
    await tester.pumpAndSettle();
    expect(find.text('旧弹窗内容'), findsOneWidget);
    model.phase('extracting');
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));
    expect(find.text('初始化游戏图像资源'), findsOneWidget);
    expect(find.text('旧弹窗内容'), findsNothing);
    expect(find.byType(Navigator), findsNothing);
    model.phase('failed');
    await tester.pump();
    expect(find.text('合成失败说明'), findsOneWidget);
    expect(find.widgetWithText(StewardButton, '重新读取'), findsOneWidget);
    model.phase('ready');
    await tester.pumpAndSettle();
    expect(find.text('初始化游戏图像资源'), findsNothing);
    expect(find.byType(WorkbenchShell), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
