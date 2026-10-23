import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/gestures.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

const _labels = <OrderTagKind, String>{
  OrderTagKind.recipe: '海鲜味噌汤',
  OrderTagKind.ingredient: '基础食材：豆腐、海苔',
  OrderTagKind.extra: '追加食材：蘑菇与竹笋',
  OrderTagKind.cooker: '煮锅',
  OrderTagKind.beverage: '冰山毛玉冻柠',
  OrderTagKind.request: '清淡 / 无酒精',
  OrderTagKind.warning: '等待食材补充',
  OrderTagKind.success: '满足全部点单需求',
};

/// 使用实际主题、中文字体和合成信息验证视觉层次，不依赖业务连接或原生窗口。
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
    await (FontLoader('packages/forui_lucide/ForuiLucideIcons')
          ..addFont(rootBundle.load('packages/forui_lucide/assets/lucide.ttf')))
        .load();
  });

  Widget themeWidget(
    Widget child, {
    required bool dark,
    double scale = 1.5,
    bool touch = false,
  }) {
    final theme = stewardTheme(dark: dark, touch: touch);
    return material.MaterialApp(
      theme: theme.toApproximateMaterialTheme(),
      builder: (context, child) => FTheme(
        data: theme,
        child: MediaQuery(
          data: MediaQuery.of(context).copyWith(
            textScaler: TextScaler.linear(scale),
            disableAnimations: true,
          ),
          child: child!,
        ),
      ),
      home: child,
    );
  }

  Widget list() => DenseListDetail(
    items: const [
      {'id': 'first', 'name': '1 桌 · 博丽灵梦'},
      {'id': 'second', 'name': '8 桌 · 雾雨魔理沙'},
    ],
    identity: (item) => item['id']! as String,
    title: (item) => item['name']! as String,
    subtitle: (_) => '料理与酒水来自同一主方案',
    summary: (_) => const Wrap(
      spacing: 5,
      runSpacing: 5,
      children: [
        OrderTag('海鲜味噌汤', kind: OrderTagKind.recipe),
        OrderTag('绿茶', kind: OrderTagKind.beverage),
      ],
    ),
    detail: (item) => Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Section('${item['name']} · 主方案', const [
          Text('按点单、料理、加料和厨具分组，便于快速核对。'),
          SizedBox(height: 10),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              OrderTag('海鲜味噌汤', kind: OrderTagKind.recipe),
              OrderTag('蘑菇', kind: OrderTagKind.extra),
              OrderTag('煮锅', kind: OrderTagKind.cooker),
              OrderTag('绿茶', kind: OrderTagKind.beverage),
            ],
          ),
        ]),
        const CollapsibleSection('其他说明', [Text('折叠内容的编辑状态与滚动保持原有行为。')]),
      ],
    ),
  );

  for (final dark in [false, true]) {
    for (final width in [1280.0, 640.0, 390.0]) {
      testWidgets('公共层次 ${dark ? '深色' : '浅色'} $width px / 150% 标签换行与列表详情可用', (
        tester,
      ) async {
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(width, 1000);
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        final capture = GlobalKey();
        await tester.pumpWidget(
          themeWidget(
            RepaintBoundary(
              key: capture,
              child: ColoredBox(
                color: stewardTheme(
                  dark: dark,
                  touch: width < 640,
                ).colors.background,
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Column(
                    children: [
                      Section('订单信息 · 分类清楚，便于核对', [
                        Wrap(
                          spacing: 7,
                          runSpacing: 7,
                          children: [
                            for (final entry in _labels.entries)
                              OrderTag(
                                entry.value,
                                key: ValueKey('sample-${entry.key.name}'),
                                kind: entry.key,
                              ),
                          ],
                        ),
                      ]),
                      Expanded(child: list()),
                    ],
                  ),
                ),
              ),
            ),
            dark: dark,
            touch: width < 640,
          ),
        );
        await tester.pumpAndSettle();
        for (final entry in _labels.entries) {
          final tag = find.byKey(ValueKey('sample-${entry.key.name}'));
          final bounds = tester.getRect(tag);
          final text = find.descendant(
            of: tag,
            matching: find.text(entry.value),
          );
          final content = tester.getRect(text);
          expect(content.left, greaterThanOrEqualTo(bounds.left));
          expect(content.right, lessThanOrEqualTo(bounds.right));
          expect(content.bottom, lessThanOrEqualTo(bounds.bottom));
          expect(bounds.width, lessThanOrEqualTo(width - 32));
        }
        await tester.tap(find.byKey(const ValueKey('second')));
        await tester.pumpAndSettle();
        expect(find.text('8 桌 · 雾雨魔理沙 · 主方案'), findsOneWidget);
        if (width < 800) {
          await tester.tap(find.text('返回列表'));
          await tester.pumpAndSettle();
          expect(
            find.byKey(const ValueKey('second')).hitTestable(),
            findsOneWidget,
          );
        }
        expect(tester.takeException(), isNull);
        final directory = Platform.environment['MYSTIA_SCREENSHOT_DIR'];
        if (directory != null) {
          await tester.runAsync(() async {
            final image =
                await (capture.currentContext!.findRenderObject()!
                        as RenderRepaintBoundary)
                    .toImage();
            try {
              final data = await image.toByteData(
                format: ui.ImageByteFormat.png,
              );
              await Directory(directory).create(recursive: true);
              await File(
                '$directory/hierarchy-${dark ? 'dark' : 'light'}-${width.toInt()}.png',
              ).writeAsBytes(data!.buffer.asUint8List());
            } finally {
              image.dispose();
            }
          });
        }
        await tester.pumpWidget(const SizedBox());
      });
    }

    testWidgets('${dark ? '深色' : '浅色'}标签对比度与非颜色语义完整', (tester) async {
      final theme = stewardTheme(dark: dark, touch: false);
      await tester.pumpWidget(
        themeWidget(
          Center(
            child: Wrap(
              children: [
                for (final entry in _labels.entries)
                  OrderTag(entry.value, kind: entry.key),
              ],
            ),
          ),
          dark: dark,
        ),
      );
      await tester.pumpAndSettle();
      final tones = <int>{};
      final icons = <IconData>{};
      for (final entry in _labels.entries) {
        final tag = find.byWidgetPredicate(
          (widget) => widget is OrderTag && widget.kind == entry.key,
        );
        final foreground = tester
            .widget<Text>(
              find.descendant(of: tag, matching: find.text(entry.value)),
            )
            .style!
            .color!;
        final container = tester.widget<Container>(
          find.descendant(of: tag, matching: find.byType(Container)).first,
        );
        final tint = (container.decoration! as BoxDecoration).color!;
        final background = Color.alphaBlend(tint, theme.colors.background);
        final fg = foreground.computeLuminance(),
            bg = background.computeLuminance();
        final contrast =
            ((fg > bg ? fg : bg) + .05) / ((fg > bg ? bg : fg) + .05);
        expect(
          contrast,
          greaterThanOrEqualTo(4.5),
          reason: '${entry.key} 普通文字需维持可读对比度',
        );
        tones.add(foreground.toARGB32());
        icons.add(
          tester
              .widget<Icon>(
                find.descendant(of: tag, matching: find.byType(Icon)),
              )
              .icon!,
        );
        final semantics = tester.widget<Semantics>(
          find.descendant(of: tag, matching: find.byType(Semantics)).first,
        );
        expect(semantics.properties.label, endsWith('：${entry.value}'));
      }
      expect(tones.length, OrderTagKind.values.length);
      expect(icons.length, OrderTagKind.values.length);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('卡片半透明色层与详情空白均保留真实alpha', (tester) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(1000, 700);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final capture = GlobalKey();
    await tester.pumpWidget(
      themeWidget(
        RepaintBoundary(
          key: capture,
          child: ColoredBox(
            color: const Color(0x80f4f1e9),
            child: Padding(padding: const EdgeInsets.all(16), child: list()),
          ),
        ),
        dark: false,
        scale: 1,
      ),
    );
    await tester.pumpAndSettle();
    final detail = tester.getRect(
      find.byKey(const ValueKey('dense-detail-surface')),
    );
    final samples = [
      Offset(detail.right - 20, detail.bottom - 20),
      Offset(detail.right - 35, detail.top + 83),
    ];
    final pixels = await tester.runAsync(() async {
      final boundary =
          capture.currentContext!.findRenderObject()! as RenderRepaintBoundary;
      final image = await boundary.toImage();
      try {
        return (
          await image.toByteData(format: ui.ImageByteFormat.rawRgba),
          image.width,
        );
      } finally {
        image.dispose();
      }
    });
    for (var i = 0; i < samples.length; i++) {
      final point = samples[i];
      final alpha = pixels!.$1!.getUint8(
        (point.dy.floor() * pixels.$2 + point.dx.floor()) * 4 + 3,
      );
      expect(
        alpha,
        i == 0 ? inInclusiveRange(126, 130) : inInclusiveRange(126, 150),
        reason: i == 0 ? '详情空白不得覆盖窗口背景alpha' : '卡片仅叠加浅色层，不能恢复为不透明',
      );
    }
    expect(tester.takeException(), isNull);
  });

  testWidgets('列表选择边界在悬停后仍以明确前景边框标记', (tester) async {
    tester.view.physicalSize = const Size(1280, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    await tester.pumpWidget(themeWidget(list(), dark: false, scale: 1));
    await tester.pumpAndSettle();
    final row = find.byKey(const ValueKey('second'));
    await tester.tap(row);
    await tester.pumpAndSettle();
    final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
    await mouse.addPointer(location: Offset.zero);
    addTearDown(mouse.removePointer);
    await mouse.moveTo(tester.getCenter(row));
    await tester.pumpAndSettle();
    final selectedBorder = tester
        .widgetList<DecoratedBox>(
          find.ancestor(of: row, matching: find.byType(DecoratedBox)),
        )
        .firstWhere((box) => box.position == DecorationPosition.foreground);
    expect((selectedBorder.decoration as BoxDecoration).border, isNotNull);
    final semantics = tester
        .widgetList<Semantics>(
          find.ancestor(of: row, matching: find.byType(Semantics)),
        )
        .firstWhere((widget) => widget.properties.selected != null);
    expect(semantics.properties.selected, isTrue);
    expect(tester.takeException(), isNull);
  });

  testWidgets('IndexedStack切页保留独立行的选中语义与按钮操作', (tester) async {
    tester.view.physicalSize = const Size(1280, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final semantics = tester.ensureSemantics();
    try {
      var page = 0;
      await tester.pumpWidget(
        themeWidget(
          StatefulBuilder(
            builder: (context, setState) => Column(
              children: [
                StewardButton(
                  '切换页面',
                  onPressed: () => setState(() => page = 1 - page),
                ),
                Expanded(
                  // 与真实工作台一致：隐藏页仍挂载，只从语义树与绘制中退出。
                  // 此测试不能以销毁重建列表或停用语义来规避缓存失效问题。
                  child: IndexedStack(
                    index: page,
                    children: [
                      TickerMode(enabled: page == 0, child: list()),
                      const EmptyState('另一个页面', '返回后仍保留列表选择。'),
                    ],
                  ),
                ),
              ],
            ),
          ),
          dark: false,
          scale: 1,
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('second')));
      await tester.pumpAndSettle();
      for (var iteration = 0; iteration < 3; iteration++) {
        await tester.tap(find.text('切换页面'));
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        await tester.tap(find.text('切换页面'));
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        final row = find.byKey(const ValueKey('second'));
        final selected = find.ancestor(
          of: row,
          matching: find.byWidgetPredicate(
            (widget) =>
                widget is Semantics && widget.properties.selected == true,
          ),
        );
        // 检查实际渲染语义，既保留选择状态，也不能吞掉子按钮的可操作性与中文标题。
        expect(
          tester.getSemantics(selected).flagsCollection.isSelected,
          ui.Tristate.isTrue,
        );
        final children = <SemanticsNode>[];
        tester.getSemantics(row).visitChildren((child) {
          children.add(child);
          return true;
        });
        final button = children.singleWhere(
          (child) => child.flagsCollection.isButton,
        );
        expect(button.flagsCollection.isButton, isTrue);
        expect(
          button.getSemanticsData().hasAction(ui.SemanticsAction.tap),
          isTrue,
        );
        expect(button.label, contains('8 桌 · 雾雨魔理沙'));
      }
    } finally {
      // Flutter先检查语义句柄再执行addTearDown，因此必须在测试主体退出前归还句柄。
      semantics.dispose();
    }
  });
}
