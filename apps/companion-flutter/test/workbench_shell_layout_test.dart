import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' show SelectableText;
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/ui/components.dart';

import 'support/fixture_model.dart';

/// 仅记录合成查询与结果，验证窄屏上的查询动作和结果可达性，不调用真实 Mod。
class _QueryModel extends FixtureModel {
  _QueryModel() : super(desktop: false, orderCount: 0);
  int queryCount = 0;

  @override
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  }) async {
    queryCount++;
    pageResult = {
      'recipes': [
        {
          'recipe': {'id': 901, 'name': '合成滚动可达料理'},
        },
      ],
    };
    publish();
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;

  setUpAll(() async {
    final fontPath = Platform.environment['MYSTIA_UI_FONT'];
    if (fontPath != null) {
      final bytes = await File(fontPath).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });

  Future<void> open(
    WidgetTester tester,
    FixtureModel model, {
    double width = 1280,
    double height = 900,
    int scale = 100,
    Widget? previewControls,
    GlobalKey? capture,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, height);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    model.localPreferences = {
      ...model.localPreferences,
      'fontScalePercent': scale,
      'reduceMotion': true,
    };
    // AssetBundle通过测试引擎异步读取真实构建资产。先在真实异步区域等待它完成，
    // 避免pumpAndSettle的假时钟先停止而把“读取中”误判成产品版本缺失。
    await tester.runAsync(() => rootBundle.loadString('pubspec.yaml'));
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: RepaintBoundary(
          key: capture,
          child: StewardApp(previewControls: previewControls),
        ),
      ),
    );
    // 同时等待产品Future本身：缓存资产的Future可能已在真实异步区域创建，
    // 仅预读资产并推进假时钟无法保证FutureBuilder的回调已经返回到Widget树。
    final versionBuilder = tester.widget<FutureBuilder<String>>(
      find.descendant(
        of: find.byKey(const ValueKey('workbench-status-bar')),
        matching: find.byType(FutureBuilder<String>),
      ),
    );
    await tester.runAsync(() => versionBuilder.future!);
    await tester.pumpAndSettle();
  }

  testWidgets('新增数据入口保留设置旧ID，肩键按可见顺序移动并可恢复数据页', (tester) async {
    final model = FixtureModel(orderCount: 0)
      ..localPreferences = {'lastPage': 4};
    addTearDown(model.dispose);
    await open(tester, model, width: 627);
    final title = find.byKey(const ValueKey('workbench-page-title'));
    expect(tester.widget<Text>(title).data, '设置');
    expect(find.text('资源管理'), findsNothing);
    model.input('previousTab');
    await tester.pumpAndSettle();
    expect(tester.widget<Text>(title).data, '数据');
    expect(model.localPreferences['lastPage'], 5);
    expect(find.text('资源管理'), findsOneWidget);
    model.input('nextTab');
    await tester.pumpAndSettle();
    expect(tester.widget<Text>(title).data, '设置');
    await tester.tap(find.byKey(const ValueKey('nav-5')));
    await tester.pumpAndSettle();
    await tester.pumpWidget(const SizedBox());
    await open(tester, model, width: 627);
    expect(tester.widget<Text>(title).data, '数据');
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('手机浅深主题同时更新系统栏图标与安全区真实背景', (tester) async {
    final model = FixtureModel(desktop: false, orderCount: 0);
    addTearDown(model.dispose);
    tester.view.padding = const FakeViewPadding(top: 24, bottom: 20);
    tester.view.viewPadding = const FakeViewPadding(top: 24, bottom: 20);
    addTearDown(tester.view.resetPadding);
    addTearDown(tester.view.resetViewPadding);
    final capture = GlobalKey();
    await open(tester, model, width: 390, height: 800, capture: capture);
    for (final dark in [false, true, false]) {
      model.localPreferences = {
        ...model.localPreferences,
        'darkMode': dark,
        // 手机不支持窗口透明；历史桌面偏好不能让安全区重新透出黑底。
        'backgroundOpacityPercent': 20,
      };
      model.publish();
      await tester.pumpAndSettle();
      final overlay = tester.widget<AnnotatedRegion<SystemUiOverlayStyle>>(
        find.byKey(const ValueKey('mobile-system-ui')),
      );
      final theme = tester
          .element(find.byKey(const ValueKey('workbench-page-content')))
          .theme;
      final background = theme.colors.background;
      final brightness = dark ? Brightness.light : Brightness.dark;
      expect(overlay.value.statusBarIconBrightness, brightness);
      expect(overlay.value.systemNavigationBarIconBrightness, brightness);
      expect(
        overlay.value.statusBarBrightness,
        dark ? Brightness.dark : Brightness.light,
      );
      expect(overlay.value.statusBarColor, background);
      expect(overlay.value.systemNavigationBarColor, background);
      expect(overlay.value.systemNavigationBarDividerColor, background);
      expect(overlay.value.systemStatusBarContrastEnforced, isFalse);
      expect(overlay.value.systemNavigationBarContrastEnforced, isFalse);
      // 组件样式声明不足以证明edge-to-edge可用；直接采样SafeArea上方和下方
      // 的实际RGBA像素，两处均必须为当前主题实底，不能仍是透明/黑色画布。
      await tester.runAsync(() async {
        final frame =
            await (capture.currentContext!.findRenderObject()!
                    as RenderRepaintBoundary)
                .toImage();
        final pixels = (await frame.toByteData(
          format: ui.ImageByteFormat.rawRgba,
        ))!;
        final argb = background.toARGB32();
        final expected = [
          (argb >> 16) & 255,
          (argb >> 8) & 255,
          argb & 255,
          255,
        ];
        for (final point in [const Offset(195, 12), const Offset(195, 790)]) {
          final offset =
              (point.dy.toInt() * frame.width + point.dx.toInt()) * 4;
          expect(
            List.generate(4, (index) => pixels.getUint8(offset + index)),
            expected,
          );
        }
        frame.dispose();
      });
      expect(tester.takeException(), isNull);
    }
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('手机安全区实底不进入Windows透明绘制路径', (tester) async {
    final model = FixtureModel(desktop: true, orderCount: 0)
      ..localPreferences = {
        'backgroundOpacityPercent': 50,
        'contentOpacityPercent': 100,
      };
    addTearDown(model.dispose);
    final capture = GlobalKey();
    await open(tester, model, capture: capture);
    expect(find.byKey(const ValueKey('mobile-system-ui')), findsNothing);
    final bounds = tester.getRect(
      find.byKey(const ValueKey('workbench-page-content')),
    );
    final blank = bounds.bottomRight - const Offset(6, 6);
    await tester.runAsync(() async {
      final frame =
          await (capture.currentContext!.findRenderObject()!
                  as RenderRepaintBoundary)
              .toImage();
      final pixels = (await frame.toByteData(
        format: ui.ImageByteFormat.rawRgba,
      ))!;
      final offset = (blank.dy.floor() * frame.width + blank.dx.floor()) * 4;
      expect(pixels.getUint8(offset + 3), inInclusiveRange(126, 130));
      frame.dispose();
    });
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final width in [1280.0, 640.0, 390.0]) {
    for (final scale in [100, 150]) {
      testWidgets('Windows固定窄侧栏在 $width px / $scale% 下可触控且各页面保留内容空间', (
        tester,
      ) async {
        final model = FixtureModel(desktop: true, orderCount: 2);
        addTearDown(model.dispose);
        await open(tester, model, width: width, scale: scale);
        final rail = find.byKey(const ValueKey('workbench-navigation-rail'));
        expect(tester.getSize(rail).width, 80);
        expect(find.byType(FBottomNavigationBar), findsNothing);
        expect(find.text('夜雀管家'), findsNothing);
        expect(find.text('幻想乡经营工作台'), findsNothing);
        expect(find.text('专注视图'), findsNothing);
        expect(find.text('展开导航'), findsNothing);
        for (final page in [0, 1, 2, 3, 5, 4]) {
          final destination = find.byKey(ValueKey('nav-$page'));
          final hit = tester.getRect(destination);
          // 390px 仍保留左侧目的地，不能以不足44px的按钮换取正文宽度。
          expect(hit.width, greaterThanOrEqualTo(44));
          expect(hit.height, greaterThanOrEqualTo(44));
          expect(hit.right, lessThanOrEqualTo(tester.getRect(rail).right));
          await tester.tap(destination);
          await tester.pumpAndSettle();
          expect(
            tester
                .widget<Text>(
                  find.byKey(const ValueKey('workbench-page-title')),
                )
                .data,
            const ['总览', '经营', '推荐', '扩展', '设置', '数据'][page],
          );
          final content = tester.getRect(
            find.byKey(const ValueKey('workbench-page-content')),
          );
          expect(
            content.left,
            greaterThanOrEqualTo(tester.getRect(rail).right),
          );
          expect(content.width, greaterThanOrEqualTo(250));
          expect(content.height, greaterThan(300));
          expect(
            tester.takeException(),
            isNull,
            reason: 'page=$page, width=$width, scale=$scale',
          );
        }
        await tester.pumpWidget(const SizedBox());
      });
    }
  }

  for (final size in [
    const Size(390, 800),
    const Size(360, 800),
    const Size(800, 360),
  ]) {
    for (final scale in [100, 150]) {
      testWidgets('Android $size/$scale%六目的地底部导航保留正文与选中语义', (tester) async {
        final model = FixtureModel(desktop: false, orderCount: 0);
        addTearDown(model.dispose);
        final semantics = tester.ensureSemantics();
        try {
          await open(
            tester,
            model,
            width: size.width,
            height: size.height,
            scale: scale,
          );
          expect(
            find.byKey(const ValueKey('workbench-navigation-rail')),
            findsNothing,
          );
          final navigation = find.byKey(
            const ValueKey('workbench-bottom-navigation'),
          );
          expect(find.byType(FBottomNavigationBar), findsOneWidget);
          final bar = tester.getRect(navigation);
          expect(bar.bottom, size.height);
          const order = [0, 1, 2, 3, 5, 4];
          for (final page in order) {
            final destination = find.byKey(ValueKey('nav-$page'));
            final hit = tester.getRect(destination);
            expect(hit.width, greaterThanOrEqualTo(44));
            expect(hit.height, greaterThanOrEqualTo(44));
            expect(hit.left, greaterThanOrEqualTo(bar.left));
            expect(hit.right, lessThanOrEqualTo(bar.right));
            await tester.tap(destination);
            await tester.pumpAndSettle();
            expect(
              tester.widget<FBottomNavigationBar>(navigation).index,
              order.indexOf(page),
            );
            // 官方导航项外层是MergeSemantics，真正的可操作节点由内部FTappable
            // 持有。逐项核对实际节点的三态selected、中文名称及tap动作，不能仅
            // 检查组件index，也不能把容器的Tristate.none当作导航选中状态。
            for (var candidate = 0; candidate < 6; candidate++) {
              final actionable = find.descendant(
                of: find.byKey(ValueKey('nav-$candidate')),
                matching: find.byWidgetPredicate(
                  (widget) =>
                      widget is Semantics &&
                      widget.properties.button == true &&
                      widget.properties.selected != null,
                ),
              );
              expect(actionable, findsOneWidget);
              // getSemantics会返回最近的合并边界；边界自身没有button/selected，
              // 被合并的子节点才承载真实操作语义。因此只在当前目的地的语义子树
              // 中查找，并要求恰好一个按钮，避免全页匹配或重复语义造成假阳性。
              final buttons = <SemanticsNode>[];
              void collectButtons(SemanticsNode node) {
                if (node.flagsCollection.isButton) buttons.add(node);
                node.visitChildren((child) {
                  collectButtons(child);
                  return true;
                });
              }

              collectButtons(tester.getSemantics(actionable));
              expect(buttons, hasLength(1));
              final node = buttons.single;
              expect(node.flagsCollection.isButton, isTrue);
              expect(
                node.flagsCollection.isSelected,
                candidate == page ? ui.Tristate.isTrue : ui.Tristate.isFalse,
              );
              expect(
                node.getSemanticsData().hasAction(ui.SemanticsAction.tap),
                isTrue,
              );
              expect(
                node.label,
                contains(const ['总览', '经营', '推荐', '扩展', '设置', '数据'][candidate]),
              );
            }
            expect(model.localPreferences['lastPage'], page);
            final content = tester.getRect(
              find.byKey(const ValueKey('workbench-page-content')),
            );
            expect(content.left, 16);
            expect(content.right, size.width - 16);
            expect(content.height, greaterThan(size.height < 400 ? 120 : 400));
            final status = tester.getRect(
              find.byKey(const ValueKey('workbench-status-bar')),
            );
            expect(status.bottom, closeTo(bar.top, .01));
            expect(status.height, lessThanOrEqualTo(45));
            expect(
              find.descendant(
                of: find.byKey(const ValueKey('workbench-status-bar')),
                matching: find.textContaining('自动化配置'),
              ),
              findsNothing,
            );
            expect(
              tester.takeException(),
              isNull,
              reason: 'page=$page size=$size scale=$scale',
            );
          }
          await tester.pumpWidget(const SizedBox());
        } finally {
          // 测试框架先校验语义句柄再运行tearDown，必须在测试主体结束前归还。
          semantics.dispose();
        }
      });
    }
  }

  testWidgets('手机单行反馈保持高度，状态详情保留完整版本和错误且只读', (tester) async {
    final model = FixtureModel(desktop: false, orderCount: 0)
      ..snapshot = {
        'pluginVersion': '9.8.7-synthetic-mod',
        'activeSceneName': '合成夜间',
      };
    addTearDown(model.dispose);
    await open(tester, model, width: 360, height: 800, scale: 150);
    final status = find.byKey(const ValueKey('workbench-status-bar'));
    final before = tester.getRect(status);
    const error = '合成连接错误：这里是一段足够长的完整诊断，窄屏摘要可省略，但连接详情和辅助阅读器必须保留全文。';
    model.error = error;
    model.publish();
    await tester.pumpAndSettle();
    expect(tester.getRect(status), before);
    final message = tester.widget<Text>(
      find.byKey(const ValueKey('workbench-operation-message')),
    );
    expect(message.data, error);
    expect(message.maxLines, 1);
    final button = tester.widget<FButton>(
      find.byKey(const ValueKey('mobile-status-details')),
    );
    expect(button.semanticsLabel, contains(error));
    expect(button.semanticsLabel, contains('Mod 9.8.7-synthetic-mod'));
    await tester.tap(find.byKey(const ValueKey('mobile-status-details')));
    await tester.pumpAndSettle();
    final expected = File('pubspec.yaml')
        .readAsLinesSync()
        .singleWhere((line) => line.startsWith('version:'))
        .substring('version:'.length)
        .trim();
    for (final value in ['客户端 $expected', 'Mod 9.8.7-synthetic-mod', error]) {
      expect(
        find.byWidgetPredicate(
          (widget) => widget is SelectableText && widget.data == value,
        ),
        findsOneWidget,
      );
    }
    final close = find.byKey(const ValueKey('mobile-status-close'));
    await tester.ensureVisible(close);
    await tester.tap(close);
    await tester.pumpAndSettle();
    expect(model.writes, isEmpty);
    expect(tester.getRect(status), before);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('手机键盘仅避让一次，旋转与切页保留输入草稿和最后目的地', (tester) async {
    final model = FixtureModel(desktop: false, orderCount: 0)
      ..localPreferences = {'lastPage': 4};
    addTearDown(model.dispose);
    tester.view.padding = const FakeViewPadding(top: 24, bottom: 20);
    tester.view.viewPadding = const FakeViewPadding(top: 24, bottom: 20);
    addTearDown(tester.view.resetPadding);
    addTearDown(tester.view.resetViewPadding);
    addTearDown(tester.view.resetViewInsets);
    await open(tester, model, width: 360, height: 800, scale: 150);
    final navigation = find.byType(FBottomNavigationBar);
    expect(tester.widget<FBottomNavigationBar>(navigation).index, 5);
    expect(tester.getRect(navigation).bottom, 780);
    // 大字号手机中摘要可以超过首屏，先通过真实正文滚动抵达编辑入口，
    // 不能点击已被状态栏遮住的屏幕坐标；后续仍验证同一输入节点和完整草稿。
    await tester.ensureVisible(find.text('修改连接'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('修改连接'));
    await tester.pumpAndSettle();
    final input = find.descendant(
      of: find.byWidgetPredicate(
        (widget) => widget is StewardField && widget.title == 'API 地址',
      ),
      matching: find.byType(EditableText),
    );
    await tester.ensureVisible(input);
    await tester.enterText(input, 'http://synthetic.local:32145');
    final controller = tester.widget<EditableText>(input).controller;
    final draft = controller.value.copyWith(
      composing: const TextRange(start: 7, end: 16),
    );
    controller.value = draft;
    tester.view.viewInsets = const FakeViewPadding(bottom: 280);
    tester.view.padding = const FakeViewPadding(top: 24);
    await tester.pumpAndSettle();
    expect(navigation, findsNothing);
    expect(
      tester.getRect(find.byKey(const ValueKey('workbench-status-bar'))).bottom,
      520,
    );
    expect(
      identical(tester.widget<EditableText>(input).controller, controller),
      isTrue,
    );
    expect(controller.value, draft);
    tester.view.viewInsets = const FakeViewPadding();
    tester.view.padding = const FakeViewPadding(top: 24, bottom: 20);
    await tester.pumpAndSettle();
    tester.view.physicalSize = const Size(800, 360);
    await tester.pumpAndSettle();
    expect(navigation, findsOneWidget);
    expect(
      find.byKey(const ValueKey('workbench-navigation-rail')),
      findsNothing,
    );
    expect(
      identical(tester.widget<EditableText>(input).controller, controller),
      isTrue,
    );
    expect(controller.value, draft);
    // 肩键仍使用同一导航目的地；返回设置时输入树和草稿保持，而非重新初始化。
    model.input('nextTab');
    await tester.pumpAndSettle();
    expect(tester.widget<FBottomNavigationBar>(navigation).index, 0);
    model.input('previousTab');
    await tester.pumpAndSettle();
    expect(tester.widget<FBottomNavigationBar>(navigation).index, 5);
    expect(controller.text, draft.text);
    expect(model.localPreferences['lastPage'], 4);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final previewHeight in [0.0, 48.0]) {
    testWidgets('手机横屏150%与键盘同时出现时输入及列表可用（预览额外$previewHeight）', (tester) async {
      final model = FixtureModel(desktop: false, orderCount: 6);
      addTearDown(model.dispose);
      tester.view.padding = const FakeViewPadding(top: 24, bottom: 20);
      tester.view.viewPadding = const FakeViewPadding(top: 24, bottom: 20);
      addTearDown(tester.view.resetPadding);
      addTearDown(tester.view.resetViewPadding);
      addTearDown(tester.view.resetViewInsets);
      await open(
        tester,
        model,
        width: 800,
        height: 360,
        scale: 150,
        previewControls: previewHeight == 0
            ? null
            : SizedBox(height: previewHeight),
      );
      final field = find.byWidgetPredicate(
        (widget) => widget is StewardField && widget.title == '筛选客人或桌号',
      );
      final input = find.descendant(
        of: field,
        matching: find.byType(EditableText),
      );
      await tester.ensureVisible(input);
      await tester.enterText(input, '博丽灵梦');
      final controller = tester.widget<EditableText>(input).controller;
      final draft = controller.value.copyWith(
        composing: const TextRange(start: 0, end: 2),
      );
      controller.value = draft;
      final inputElement = tester.element(input);
      tester.view.viewInsets = const FakeViewPadding(bottom: 160);
      tester.view.padding = const FakeViewPadding(top: 24);
      await tester.pumpAndSettle();
      await tester.ensureVisible(field);
      await tester.pumpAndSettle();

      final controls = tester.getRect(
        find.byKey(const ValueKey('service-controls')),
      );
      final inputBounds = tester.getRect(field);
      final results = find.byType(DenseListDetail);
      final resultBounds = tester.getRect(results);
      // 不只检查RenderFlex没有报错：完整输入框必须留在可见控制区，结果至少
      // 保留一个44px触控目标及边界间距，横屏输入时才能继续辨认筛选后的订单。
      expect(inputBounds.height, greaterThanOrEqualTo(44));
      expect(inputBounds.top, greaterThanOrEqualTo(controls.top - .01));
      expect(inputBounds.bottom, lessThanOrEqualTo(controls.bottom + .01));
      expect(input.hitTestable(), findsOneWidget);
      expect(resultBounds.height, greaterThanOrEqualTo(48));
      expect(resultBounds.bottom, lessThanOrEqualTo(200));
      expect(tester.widget<DenseListDetail>(results).items, hasLength(3));
      final firstTitle = find.text('2 桌 · 博丽灵梦').first;
      // 条目保留原有触屏内边距；极短视口允许正常滚动，但滚动后必须能完整
      // 阅读标题，不能把“节点存在”或裁剪区域外的文本误当成可用的订单列表。
      await tester.ensureVisible(firstTitle);
      await tester.pumpAndSettle();
      expect(
        tester.getRect(firstTitle).top,
        greaterThanOrEqualTo(resultBounds.top),
      );
      expect(
        tester.getRect(firstTitle).bottom,
        lessThanOrEqualTo(resultBounds.bottom),
      );
      expect(firstTitle.hitTestable(), findsOneWidget);
      final list = find.descendant(
        of: results,
        matching: find.byType(ListView),
      );
      expect(tester.getRect(list).height, greaterThanOrEqualTo(44));
      final scroll = tester.widget<ListView>(list).controller!;
      await tester.drag(list, const Offset(0, -80));
      await tester.pumpAndSettle();
      expect(scroll.offset, greaterThan(0));
      final manualOffset = scroll.offset;
      model.publish();
      await tester.pumpAndSettle();
      expect(scroll.offset, closeTo(manualOffset, .01));
      expect(identical(tester.element(input), inputElement), isTrue);
      expect(
        identical(tester.widget<EditableText>(input).controller, controller),
        isTrue,
      );
      expect(controller.value, draft);
      expect(find.byType(FBottomNavigationBar), findsNothing);
      expect(tester.takeException(), isNull);

      // 收起键盘恢复导航、标题与完整状态，不卸载正文或重建中文组词中的输入节点。
      tester.view.viewInsets = const FakeViewPadding();
      tester.view.padding = const FakeViewPadding(top: 24, bottom: 20);
      await tester.pumpAndSettle();
      expect(find.byType(FBottomNavigationBar), findsOneWidget);
      expect(
        find.byKey(const ValueKey('workbench-page-title')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('workbench-status-bar')),
        findsOneWidget,
      );
      expect(identical(tester.element(input), inputElement), isTrue);
      expect(controller.value, draft);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('手机系统返回关闭当前详情，隐藏经营页不会拦截其它目的地', (tester) async {
    final model = FixtureModel(desktop: false, orderCount: 1);
    addTearDown(model.dispose);
    await open(tester, model, width: 390, height: 800, scale: 150);
    await tester.tap(find.textContaining('2 桌 ·').first);
    await tester.pumpAndSettle();
    expect(find.text('返回列表'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('nav-2')));
    await tester.pumpAndSettle();
    final context = tester.element(
      find.byKey(const ValueKey('workbench-page-content')),
    );
    expect(ModalRoute.of(context)!.popDisposition, RoutePopDisposition.bubble);
    await tester.tap(find.byKey(const ValueKey('nav-1')));
    await tester.pumpAndSettle();
    await tester.binding.handlePopRoute();
    await tester.pumpAndSettle();
    expect(find.text('返回列表'), findsNothing);
    expect(find.textContaining('2 桌 ·'), findsOneWidget);
    expect(
      tester
          .widget<FBottomNavigationBar>(find.byType(FBottomNavigationBar))
          .index,
      1,
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('同一桌面窗口跨640px时页面原点不跳动', (tester) async {
    final model = FixtureModel(desktop: true, orderCount: 0);
    addTearDown(model.dispose);
    await open(tester, model, width: 639);
    final title = find.byKey(const ValueKey('workbench-page-title'));
    final content = find.byKey(const ValueKey('workbench-page-content'));
    final initialTitle = tester.getTopLeft(title);
    final initialContent = tester.getTopLeft(content);
    for (final width in [640.0, 641.0, 639.0]) {
      tester.view.physicalSize = Size(width, 900);
      await tester.pumpAndSettle();
      // 保留同一个模型和Widget树，只改变窗口宽度，防止平台切换掩盖样式断点。
      expect(tester.getTopLeft(title), initialTitle);
      expect(tester.getTopLeft(content), initialContent);
      expect(tester.getRect(content).left, 104);
      expect(tester.getRect(content).right, width - 24);
      expect(tester.takeException(), isNull);
    }
    await tester.pumpWidget(const SizedBox());
  });

  for (final width in [1280.0, 390.0]) {
    testWidgets('$width px连接通知和错误只出现在底栏，顶部导航位置不受提示影响', (tester) async {
      final model = FixtureModel(orderCount: 0);
      addTearDown(model.dispose);
      await open(tester, model, width: width, scale: 150);
      final rail = find.byKey(const ValueKey('workbench-navigation-rail'));
      final initialTop = tester.getRect(rail).top;
      expect(initialTop, 0);
      for (final error in [false, true]) {
        model.notice = '已连接，自动化按已保存配置运行';
        model.error = error
            ? '初始化失败（本机身份）：未完成设备身份读取，请在连接设置中点击保存并连接重试；完整诊断信息可以在设置的诊断页查看。'
            : '';
        model.publish();
        await tester.pumpAndSettle();
        expect(tester.getRect(rail).top, initialTop);
        final message = find.byKey(
          const ValueKey('workbench-operation-message'),
        );
        expect(
          find.descendant(
            of: find.byKey(const ValueKey('workbench-status-bar')),
            matching: message,
          ),
          findsOneWidget,
        );
        expect(
          tester.widget<Text>(message).data,
          error ? model.error : model.notice,
        );
        final bounds = tester.getRect(message);
        final status = tester.getRect(
          find.byKey(const ValueKey('workbench-status-bar')),
        );
        expect(bounds.left, greaterThanOrEqualTo(status.left));
        expect(bounds.right, lessThanOrEqualTo(status.right));
        expect(bounds.bottom, lessThanOrEqualTo(status.bottom));
        expect(tester.takeException(), isNull);
      }
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('底栏区分客户端和Mod版本，彻底移除自动化状态', (tester) async {
    final model = FixtureModel(orderCount: 0)
      ..profile = {'automationEnabled': true}
      ..snapshot = {
        'pluginVersion': '9.8.7-synthetic-mod',
        'activeSceneName': '合成夜间',
      };
    addTearDown(model.dispose);
    await open(tester, model, width: 390, scale: 150);
    // 期望来自构建入口的真实pubspec，不复制当前版本常量；升级版本时测试仍验证同一来源。
    final expected = File('pubspec.yaml')
        .readAsLinesSync()
        .singleWhere((line) => line.startsWith('version:'))
        .substring('version:'.length)
        .trim();
    expect(
      find.text('客户端 $expected'),
      findsOneWidget,
      reason: tester
          .widgetList<Text>(find.textContaining('客户端'))
          .map((text) => text.data)
          .join(' / '),
    );
    expect(find.text('Mod 9.8.7-synthetic-mod'), findsOneWidget);
    expect(find.textContaining('自动化配置'), findsNothing);
    expect(find.textContaining('运行许可'), findsNothing);
    final status = tester.getRect(
      find.byKey(const ValueKey('workbench-status-bar')),
    );
    expect(status.bottom, 900);
    for (final text in ['客户端 $expected', 'Mod 9.8.7-synthetic-mod']) {
      final rect = tester.getRect(find.text(text));
      expect(rect.left, greaterThanOrEqualTo(status.left));
      expect(rect.right, lessThanOrEqualTo(status.right));
      expect(rect.bottom, lessThanOrEqualTo(status.bottom));
    }
    model.connected = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(find.textContaining('自动化配置'), findsNothing);
    expect(find.text('设备角色：未确认'), findsOneWidget);
    expect(find.text('Mod 9.8.7-synthetic-mod（上次连接）'), findsOneWidget);
    expect(find.text('客户端 $expected'), findsOneWidget);
    model.snapshot = {};
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('Mod 未连接'), findsOneWidget);
    expect(find.textContaining('（上次连接）'), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('旧专注动作不再隐藏导航，肩键仍按目的地总数循环', (tester) async {
    final model = FixtureModel(orderCount: 0);
    addTearDown(model.dispose);
    await open(tester, model);
    model.input('focusMode');
    await tester.pumpAndSettle();
    expect(
      find.byKey(const ValueKey('workbench-navigation-rail')),
      findsOneWidget,
    );
    await tester.tap(find.byKey(const ValueKey('nav-0')));
    await tester.pumpAndSettle();
    model.input('previousTab');
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('workbench-page-title')))
          .data,
      '设置',
    );
    model.input('nextTab');
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('workbench-page-title')))
          .data,
      '总览',
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('窄屏大字号下自动查询条件完整可见，结果保留可用空间', (tester) async {
    final model = _QueryModel();
    addTearDown(model.dispose);
    await open(tester, model, width: 390, height: 760, scale: 150);
    await tester.tap(find.byKey(const ValueKey('nav-2')));
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pumpAndSettle();
    final controls = find.byKey(const ValueKey('recommendation-controls'));
    final scrolling = find.descendant(
      of: controls,
      matching: find.byWidgetPredicate(
        (widget) =>
            widget is Scrollable && widget.axisDirection == AxisDirection.down,
      ),
    );
    final position = tester.state<ScrollableState>(scrolling).position;
    // 移除手动查询按钮后，本尺寸的条件已经完整放得下；不能继续要求制造溢出。
    // 仍验证自动结果、条件/列表边界和列表可用高度，防止以隐藏内容换取测试通过。
    expect(position.maxScrollExtent, 0);
    expect(
      tester
          .widget<DenseListDetail>(
            find.byKey(const ValueKey('recommendations-normal-recipes')),
          )
          .items,
      hasLength(1),
    );
    expect(find.text('查询推荐'), findsNothing);
    expect(model.queryCount, 1);
    expect(find.text('合成滚动可达料理').hitTestable(), findsOneWidget);
    final resultRect = tester.getRect(
      find.byKey(const ValueKey('recommendations-normal-recipes')),
    );
    expect(resultRect.height, greaterThan(100));
    expect(
      resultRect.top,
      greaterThanOrEqualTo(tester.getRect(controls).bottom),
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
}
