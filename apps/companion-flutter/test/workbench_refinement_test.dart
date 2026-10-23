import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/recommendations.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/menu_item_content.dart';

import '../tool/preview/rich_preview_workbench_model.dart';

/// 使用丰富的合成事实检查真实页面组合，不连接 HTTP、原生存储或游戏。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  // 可点击性也是验收的一部分；目标在屏幕外时直接失败，不能把未触发的点击记作通过。
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    final font = Platform.environment['MYSTIA_UI_FONT'];
    if (font != null) {
      final data = await File(font).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(data)))).load();
    }
    await (FontLoader('packages/forui_lucide/ForuiLucideIcons')
          ..addFont(rootBundle.load('packages/forui_lucide/assets/lucide.ttf')))
        .load();
  });

  Future<void> open(
    WidgetTester tester,
    WorkbenchModel model,
    Size size, {
    GlobalKey? capture,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = size;
    // 页面组合测试保留框架错误的完整来源，避免 takeException 只剩异常文字，
    // 使窄屏切页/悬停生命周期回归无法定位；仍交给原测试处理器判定失败。
    final originalErrorHandler = FlutterError.onError;
    FlutterError.onError = (details) {
      debugPrintStack(label: '${details.exception}', stackTrace: details.stack);
      originalErrorHandler?.call(details);
    };
    addTearDown(() => FlutterError.onError = originalErrorHandler);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: RepaintBoundary(key: capture, child: const StewardApp()),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> navigate(WidgetTester tester, int index) async {
    final key = find.byKey(ValueKey('nav-$index'));
    await tester.tap(
      key.evaluate().isNotEmpty
          ? key
          : find.text(const ['总览', '经营', '推荐', '扩展', '设置'][index]).last,
    );
    await tester.pumpAndSettle();
  }

  Future<void> screenshot(
    WidgetTester tester,
    GlobalKey key,
    String name,
  ) async {
    final directory = Platform.environment['MYSTIA_UI_EVIDENCE'];
    if (directory == null) return;
    await tester.runAsync(() async {
      final frame =
          await (key.currentContext!.findRenderObject()!
                  as RenderRepaintBoundary)
              .toImage();
      final bytes = await frame.toByteData(format: ui.ImageByteFormat.png);
      await Directory(directory).create(recursive: true);
      await File('$directory/$name.png')
          .writeAsBytes(bytes!.buffer.asUint8List());
      frame.dispose();
    });
  }

  testWidgets('桌面主方案拥有较宽详情区，备选与诊断默认折叠', (tester) async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    await open(tester, model, const Size(1280, 860));
    final container = find.byType(DenseListDetail).first;
    final list = find
        .descendant(of: container, matching: find.byType(ListView))
        .first;
    final detail = find
        .descendant(of: container, matching: find.byType(SingleChildScrollView))
        .first;
    expect(
      tester.getSize(detail).width,
      greaterThan(tester.getSize(list).width),
    );
    expect(find.text('主方案 · 完整满足'), findsNothing);
    expect(
      find.descendant(of: detail, matching: find.text('完整满足')),
      findsOneWidget,
    );
    // 主方案标题改为分隔线后，仍保证唯一默认方案属于当前详情，并继续保留
    // 服务端bucket与两实体边界；备选尚未展开时不能提前挂出第二份详情计划。
    final plan = find.descendant(
      of: detail,
      matching: find.byType(ServicePlanContent),
    );
    expect(plan, findsOneWidget);
    expect(tester.widget<ServicePlanContent>(plan).plan['bucket'], 'complete');
    expect(
      find.descendant(of: plan, matching: find.byType(MenuItemDivider)),
      findsOneWidget,
    );
    expect(
      find.descendant(of: detail, matching: find.byType(MenuItemDivider)),
      findsNWidgets(2),
    );
    expect(find.text('清除此捕获缓存'), findsNothing);
    // 配方与匹配详情补齐后，诊断入口可能位于详情首屏以下；按真实滚动路径到达它。
    await tester.ensureVisible(find.text('高级诊断 · 订单捕获'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('高级诊断 · 订单捕获'));
    await tester.pumpAndSettle();
    expect(find.text('清除此捕获缓存'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('丰富数据工作区与配方对话框在 $width px 可用', (tester) async {
      final model = RichPreviewWorkbenchModel();
      model.localPreferences = {
        'fontScalePercent': width == 390 ? 150 : 100,
        'reduceMotion': true,
      };
      addTearDown(model.dispose);
      final capture = GlobalKey();
      await open(tester, model, Size(width, 920), capture: capture);
      for (final (index, name) in const [
        (1, 'service'),
        (0, 'overview'),
        (2, 'recommendations'),
        (3, 'tools'),
        (4, 'settings'),
      ]) {
        await navigate(tester, index);
        // 推荐条件确定后自动查询，经过短防抖后结果仍需能在各尺寸实际浏览。
        if (index == 2)
          await tester.pumpAndSettle(const Duration(milliseconds: 300));
        expect(tester.takeException(), isNull, reason: '$name / $width');
        await screenshot(tester, capture, '$name-rich-${width.toInt()}');
      }
      // 模块与推荐偏好分别从实际的新入口访问，不再依赖已经删除的设置内推荐页签。
      await navigate(tester, 3);
      final gameUiTab = find.text('游戏界面辅助').last;
      await tester.ensureVisible(gameUiTab);
      await tester.tap(gameUiTab);
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      await screenshot(tester, capture, 'extensions-game-ui-${width.toInt()}');
      await navigate(tester, 2);
      for (final name in ['推荐偏好']) {
        final tab = find
            .descendant(
              of: find.byType(RecommendationsPage),
              matching: find.text(name),
            )
            .first;
        await tester.ensureVisible(tab);
        await tester.pumpAndSettle();
        await tester.tap(tab);
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
      }
      final customMode = find.descendant(
        of: find.byKey(const ValueKey('recommendation-sort-mode')),
        matching: find.text('自定义'),
      );
      await tester.scrollUntilVisible(
        customMode,
        220,
        scrollable: find
            .descendant(
              of: find.byKey(
                const PageStorageKey('recommendation-preferences'),
              ),
              matching: find.byType(Scrollable),
            )
            .first,
      );
      await tester.ensureVisible(customMode);
      await tester.pumpAndSettle();
      await tester.tap(customMode);
      await tester.pumpAndSettle();
      final parameters = find.text('自定义评分参数');
      // 窄屏表单按需构建，先滚到尚未构建的分组，再检查展开后的真实布局。
      await tester.scrollUntilVisible(
        parameters,
        220,
        scrollable: find
            .descendant(
              of: find.byKey(
                const PageStorageKey('recommendation-preferences'),
              ),
              matching: find.byType(Scrollable),
            )
            .first,
      );
      await tester.pumpAndSettle();
      await Scrollable.ensureVisible(
        tester.element(parameters),
        alignment: 0.15,
      );
      await tester.pumpAndSettle();
      expect(find.text('料理喜好'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await screenshot(tester, capture, 'settings-scoring-${width.toInt()}');

      await navigate(tester, 2);
      await tester.tap(find.text('自定义配方'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('新增配方'));
      await tester.pumpAndSettle();
      expect(find.text('新增自定义配方'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await screenshot(tester, capture, 'recipe-editor-${width.toInt()}');
      await tester.tap(find.text('取消').last);
      await tester.pumpAndSettle();
      expect(find.text('新增自定义配方'), findsNothing);
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('配方编辑保留原场景边界，切换场景后不能提交旧草稿', (tester) async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    await open(tester, model, const Size(1280, 860));
    await navigate(tester, 2);
    await tester.tap(find.text('自定义配方'));
    await tester.pumpAndSettle();
    final count = records(model.resources['/custom-recipes']?['recipes'])
        .length;
    await tester.tap(find.byKey(const ValueKey('custom-edit-custom-start')));
    await tester.pumpAndSettle();
    model.selectScenario('day');
    await tester.pumpAndSettle();
    await tester.tap(find.text('保存配方'));
    await tester.pumpAndSettle();
    expect(find.text('连接或共享配置已经变化，请关闭后重新编辑。'), findsOneWidget);
    expect(
      records(model.resources['/custom-recipes']?['recipes']).length,
      count,
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('背景透明度穿过完整转场工作区，内容保持独立不透明', (tester) async {
    final model = RichPreviewWorkbenchModel();
    model.localPreferences = {
      'backgroundOpacityPercent': 50,
      'contentOpacityPercent': 100,
      'reduceMotion': true,
    };
    addTearDown(model.dispose);
    final capture = GlobalKey();
    await open(tester, model, const Size(1280, 860), capture: capture);
    // 同时采样分栏间留白和详情内边距。订单详情新增柔和底色后，内部像素是
    // 背景与订单底色的标准alpha合成，不能再把二者都要求为裸背景的50%。
    final detailBounds = tester.getRect(
      find.byKey(const ValueKey('dense-detail-surface')).first,
    );
    final blankPoint = detailBounds.bottomRight - const Offset(6, 6);
    final gapPoint = Offset(detailBounds.left - 6, blankPoint.dy);
    await tester.runAsync(() async {
      final frame =
          await (capture.currentContext!.findRenderObject()!
                  as RenderRepaintBoundary)
              .toImage();
      final pixels = (await frame.toByteData(
        format: ui.ImageByteFormat.rawRgba,
      ))!;
      int alphaAt(Offset point) => pixels.getUint8(
        (point.dy.floor() * frame.width + point.dx.floor()) * 4 + 3,
      );
      // 分栏间真实留白仍为约50%；订单底色 .09×.5 与 .5 背景叠加后约为134/255。
      // 两点都远低于不透明255，确保转场没有偷铺不透明画布，也保留新订单着色。
      expect(alphaAt(gapPoint), inInclusiveRange(126, 130));
      expect(alphaAt(blankPoint), inInclusiveRange(132, 136));
      frame.dispose();
    });
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('窄屏系统返回退出详情，隐藏页不会拦截其他工作区返回', (tester) async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    await open(tester, model, const Size(390, 900));
    await tester.tap(find.textContaining('1 桌 ·').first);
    await tester.pumpAndSettle();
    expect(find.text('返回列表'), findsOneWidget);
    await navigate(tester, 2);
    final pageContext = tester.element(find.byType(DenseListDetail).first);
    expect(
      ModalRoute.of(pageContext)!.popDisposition,
      RoutePopDisposition.bubble,
    );
    await navigate(tester, 1);
    await tester.binding.handlePopRoute();
    await tester.pumpAndSettle();
    expect(find.text('返回列表'), findsNothing);
    expect(find.textContaining('1 桌 ·'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
}
