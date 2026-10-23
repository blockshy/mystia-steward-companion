import 'dart:io';

import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/service_menu_adapter.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/menu_item_content.dart';
import 'package:mystia_steward_companion/ui/semantic_tag.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/steward_test_fonts.dart';
import 'support/text_geometry.dart';

/// 纯合成展示模型验证共同卡片，不连接游戏或重新执行服务端推荐算法。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  var preferredFontLoaded = false;
  var fallbackFontLoaded = false;
  setUpAll(() async {
    preferredFontLoaded = await loadPreferredStewardTestFont();
    final path =
        Platform.environment['MYSTIA_UI_FONT'] ??
        (Platform.isWindows ? 'C:/Windows/Fonts/msyh.ttc' : null);
    if (path != null) {
      final bytes = await File(path).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
      fallbackFontLoaded = true;
    }
  });
  const food = MenuItemView(
    kind: MenuItemKind.food,
    name: '竹笋海鲜味噌汤',
    cooker: '煮锅',
    ingredients: [
      MenuIngredientView('豆腐', stock: '无限'),
      MenuIngredientView('豆腐', stock: '无限'),
      MenuIngredientView('海苔', stock: '未知'),
    ],
    extras: [MenuIngredientView('蘑菇', stock: '3')],
    facts: [
      MenuItemFact('标价', '55'),
      MenuItemFact('基础成本', '12'),
      MenuItemFact('材料成本', '18'),
      MenuItemFact('利润', '37'),
      MenuItemFact('匹配标签', '3'),
    ],
    activeTags: ['鲜'],
    suppressedTags: ['重油'],
    notes: ['服务端给出的匹配解释'],
  );

  Widget fixture(Widget child, {required bool dark, double scale = 1.5}) {
    final theme = stewardTheme(dark: dark, touch: true);
    return material.MaterialApp(
      theme: theme.toApproximateMaterialTheme(),
      builder: (context, child) => FTheme(
        data: theme,
        child: MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: TextScaler.linear(scale)),
          child: child!,
        ),
      ),
      home: child,
    );
  }

  for (final width in [1280.0, 640.0, 390.0]) {
    for (final dark in [false, true]) {
      testWidgets('共享料理酒水 ${dark ? '深色' : '浅色'} $width / 150%外显且可滚动', (
        tester,
      ) async {
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = Size(width, 920);
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        await tester.pumpWidget(
          fixture(
            // 模拟固定80px导航后剩余空间，不能用整窗宽度掩盖390px真正可用空间。
            Padding(
              padding: const EdgeInsets.only(left: 80, right: 12),
              child: SingleChildScrollView(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: const [
                    MenuItemContent(food, showDetails: true),
                    MenuItemContent(
                      MenuItemView(
                        kind: MenuItemKind.beverage,
                        name: '冰山毛玉冻柠',
                        stock: '0',
                        facts: [MenuItemFact('标价', '30')],
                      ),
                    ),
                  ],
                ),
              ),
            ),
            dark: dark,
          ),
        );
        await tester.pumpAndSettle();
        expect(find.text('竹笋海鲜味噌汤'), findsOneWidget);
        expect(find.text('豆腐 · 库存无限'), findsNWidgets(2));
        expect(find.text('海苔 · 库存未知'), findsOneWidget);
        expect(find.text('蘑菇 · 库存 3'), findsOneWidget);
        expect(find.text('标价 55'), findsOneWidget);
        expect(find.text('基础成本 12'), findsOneWidget);
        expect(find.text('材料成本 18'), findsOneWidget);
        expect(find.text('利润 37'), findsOneWidget);
        expect(find.text('匹配标签 3'), findsOneWidget);
        expect(find.text('指标'), findsNWidgets(2));
        final foodContent = find.byType(MenuItemContent).first;
        final metricPrefix = find.descendant(
          of: foodContent,
          matching: find.text('指标'),
        );
        // 指标与材料分组使用同一前缀布局，不允许指标行自行增加左侧缩进。
        for (final prefix in ['厨具', '配方', '加料']) {
          final label = find.descendant(
            of: foodContent,
            matching: find.text(prefix),
          );
          expect(
            tester.getTopLeft(label).dx,
            closeTo(tester.getTopLeft(metricPrefix).dx, .01),
          );
        }
        for (final tag in tester.widgetList<Widget>(
          find.byWidgetPredicate(
            (widget) => widget is OrderTag || widget is MenuMetric,
          ),
        )) {
          final bounds = tester.getRect(find.byWidget(tag));
          expect(bounds.left, greaterThanOrEqualTo(80));
          expect(bounds.right, lessThanOrEqualTo(width - 12));
        }
        await tester.ensureVisible(find.text('库存 0'));
        await tester.pumpAndSettle();
        expect(find.text('库存 0').hitTestable(), findsOneWidget);
        expect(tester.takeException(), isNull);
      });
    }
  }

  for (final scale in [1.0, 1.5]) {
    for (final dark in [false, true]) {
      testWidgets('指标与普通标签共享几何尺寸 $scale / ${dark ? '深色' : '浅色'}', (
        tester,
      ) async {
        const orderKey = ValueKey('geometry-order-tag');
        const metricKey = ValueKey('geometry-menu-metric');
        await tester.pumpWidget(
          fixture(
            const Center(
              child: Wrap(
                spacing: 12,
                children: [
                  OrderTag('标价 55', kind: OrderTagKind.recipe, key: orderKey),
                  MenuMetric('标价', '55', key: metricKey),
                ],
              ),
            ),
            dark: dark,
            scale: scale,
          ),
        );
        await tester.pumpAndSettle();
        final order = find.byKey(orderKey), metric = find.byKey(metricKey);
        // 比较相同文字级别的真实布局，不以“存在共同外壳”代替最终渲染尺寸。
        expect(
          tester.getSize(metric).height,
          closeTo(tester.getSize(order).height, .01),
        );
        Text text(Finder owner) => tester.widget<Text>(
          find.descendant(of: owner, matching: find.byType(Text)),
        );
        Container surface(Finder owner) => tester.widget<Container>(
          find.descendant(of: owner, matching: find.byType(Container)),
        );
        Icon icon(Finder owner) => tester.widget<Icon>(
          find.descendant(of: owner, matching: find.byType(Icon)),
        );
        expect(text(metric).style!.fontSize, text(order).style!.fontSize);
        expect(text(metric).style!.height, text(order).style!.height);
        expect(surface(metric).padding, surface(order).padding);
        expect(
          (surface(metric).decoration! as BoxDecoration).borderRadius,
          (surface(order).decoration! as BoxDecoration).borderRadius,
        );
        expect(icon(metric).size, icon(order).size);
        // 图像与普通语义图标共用 20px 基准槽位，并随系统/用户字号一起缩放。
        expect(icon(metric).size, 20 * scale);
        expect(tester.takeException(), isNull);
      });
    }
  }

  testWidgets('390px和150%字号下标签续行与首行对齐且前缀独立成列', (tester) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(390, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    const names = ['首行材料标签', '中间材料标签', '末行材料标签'];
    await tester.pumpWidget(
      fixture(
        const Padding(
          padding: EdgeInsets.only(left: 80, right: 12),
          child: SingleChildScrollView(
            child: MenuItemContent(
              MenuItemView(
                kind: MenuItemKind.food,
                name: '合成换行料理',
                ingredients: [
                  MenuIngredientView('首行材料标签'),
                  MenuIngredientView('中间材料标签'),
                  MenuIngredientView('末行材料标签'),
                ],
              ),
            ),
          ),
        ),
        dark: true,
      ),
    );
    await tester.pumpAndSettle();
    final rectangles = [
      for (final name in names)
        tester.getRect(
          find.byWidgetPredicate(
            (widget) => widget is OrderTag && widget.text == name,
          ),
        ),
    ];
    final prefix = tester.getRect(find.text('配方'));
    // 检查实际标签边界及真实换行，不能仅靠查到Row/Wrap证明续行对齐。
    expect(prefix.right, lessThan(rectangles.first.left));
    for (var index = 1; index < rectangles.length; index++) {
      expect(rectangles[index].left, closeTo(rectangles.first.left, .01));
      expect(rectangles[index].top, greaterThan(rectangles[index - 1].bottom));
    }
    for (final rectangle in rectangles) {
      expect(rectangle.right, lessThanOrEqualTo(378));
    }
    expect(tester.takeException(), isNull);
  });

  testWidgets('390px和150%字号下长指标自然换行且保留完整语义', (tester) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(390, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    const longValue = '等待服务端确认的完整库存说明';
    await tester.pumpWidget(
      fixture(
        const Padding(
          padding: EdgeInsets.only(left: 80, right: 12),
          child: SingleChildScrollView(
            child: MenuItemContent(
              MenuItemView(
                kind: MenuItemKind.beverage,
                name: '合成长指标酒水',
                stock: longValue,
              ),
            ),
          ),
        ),
        dark: true,
      ),
    );
    await tester.pumpAndSettle();
    final metric = find.byType(MenuMetric);
    final bounds = tester.getRect(metric);
    expect(bounds.left, greaterThanOrEqualTo(80));
    expect(bounds.right, lessThanOrEqualTo(378));
    expect(find.text('指标'), findsOneWidget);
    expect(find.text('库存 $longValue'), findsOneWidget);
    final semantics = tester.widget<Semantics>(
      find.descendant(of: metric, matching: find.byType(Semantics)).first,
    );
    expect(semantics.properties.label, '库存 $longValue');
    expect(semantics.container, isTrue);
    expect(tester.takeException(), isNull);
  });

  test('库存哨兵与未知类型严格保留', () {
    expect(menuStockText(-1), '无限');
    expect(menuStockText(0), '0');
    expect(menuStockText(3), '3');
    for (final value in [null, -2, double.nan, double.infinity, '3']) {
      expect(menuStockText(value), '未知');
    }
  });

  testWidgets('指标有独立语义色图标和加重数值，半透明背景保留', (tester) async {
    await tester.pumpWidget(
      fixture(
        const Wrap(
          children: [
            MenuMetric('标价', '55'),
            MenuMetric('材料成本', '18'),
            MenuMetric('利润', '37'),
            MenuMetric('匹配标签', '3'),
          ],
        ),
        dark: false,
      ),
    );
    final tones = <Color>{};
    final icons = <IconData>{};
    for (final metric in find.byType(MenuMetric).evaluate()) {
      final owner = find.byWidget(metric.widget);
      final iconFinder = find.descendant(
        of: owner,
        matching: find.byType(Icon),
      );
      final icon = tester.widget<Icon>(iconFinder);
      // 图标与图片现在共享IconTheme色调；读取实际字形段落的颜色，既支持继承，
      // 也继续验证真正绘制的四种颜色，而不是只检查可空的构造参数。
      final paintedIcon = tester.renderObject<RenderParagraph>(
        find.descendant(of: iconFinder, matching: find.byType(RichText)),
      );
      tones.add(paintedIcon.text.style!.color!);
      icons.add(icon.icon!);
      final text = tester.widget<Text>(
        find.descendant(of: owner, matching: find.byType(Text)),
      );
      final span = text.textSpan! as TextSpan;
      final value = span.children!.single as TextSpan;
      expect(value.style!.fontWeight, FontWeight.w700);
      // 数值只通过字重突出，字号和行高继承普通标签，不能重新放大成指标卡片。
      expect(value.style!.fontSize, isNull);
      expect(value.style!.height, isNull);
      expect(text.style!.fontSize, 12);
      expect(text.style!.height, 1.4);
      final surface = tester.widget<Container>(
        find.descendant(of: owner, matching: find.byType(Container)),
      );
      expect((surface.decoration! as BoxDecoration).color!.a, lessThan(.15));
      final semantics = tester.widget<Semantics>(
        find.descendant(of: owner, matching: find.byType(Semantics)).first,
      );
      expect(semantics.container, isTrue);
      expect(
        semantics.properties.label,
        '${(metric.widget as MenuMetric).name} ${(metric.widget as MenuMetric).value}',
      );
    }
    expect(tones.length, 4);
    expect(icons.length, 4);
    expect(tester.takeException(), isNull);
  });

  test('普客真实投影的标价仅取嵌套实体，空目录回退不能伪装成无基础材料', () {
    final model = FixtureModel();
    addTearDown(model.dispose);
    final food = serviceFoodView(model, {
      'name': '本次执行料理',
      'cookerName': '煮锅',
      'baseIngredientNames': ['蘑菇'],
      'extraIngredients': <Object?>[],
      'recipe': {'id': 1, 'name': '目录料理', 'price': 88},
      // 故意放置旧夹具的错误扁平字段，确保不会把它误读为真实DTO的标价。
      'price': 999,
    }, normal: true);
    final beverage = serviceBeverageView(model, {
      'name': '本次执行酒水',
      'beverageId': 3,
      'beverage': {'id': 3, 'name': '目录酒水', 'price': 35},
      'price': 999,
    }, normal: true);
    expect(food.name, '本次执行料理');
    expect(food.cooker, '煮锅');
    expect(food.ingredients!.single.name, '蘑菇');
    expect(food.facts.map((fact) => (fact.label, fact.value)), [('标价', '88')]);
    expect(beverage.name, '本次执行酒水');
    expect(beverage.stock, '12');
    expect(beverage.facts.map((fact) => (fact.label, fact.value)), [
      ('标价', '35'),
    ]);
    final missing = serviceFoodView(model, {
      'name': '缺失目录料理',
      'recipe': null,
      'baseIngredientNames': <String>[],
      'price': 999,
    }, normal: true);
    expect(missing.ingredients, isNull);
    expect(missing.facts, isEmpty);
    final explicitEmpty = serviceFoodView(model, {
      'recipe': {'id': 1, 'price': 0},
      'baseIngredientNames': <String>[],
    }, normal: true);
    expect(explicitEmpty.ingredients, isEmpty);
    expect(explicitEmpty.facts.single.value, '0');
  });

  testWidgets('外层四项标签加紧凑数量，仍保留完整读屏语义', (tester) async {
    const item = MenuItemView(
      kind: MenuItemKind.beverage,
      name: '合成酒水',
      summaryTagLabel: '匹配',
      summaryTags: ['无酒精', '清爽', '可加冰', '水果', '甜', '现代'],
    );
    await tester.pumpWidget(fixture(const MenuItemContent(item), dark: false));
    for (final tag in item.summaryTags.take(4)) {
      expect(find.text(tag), findsOneWidget);
    }
    expect(find.text('甜'), findsNothing);
    expect(find.text('现代'), findsNothing);
    expect(find.text('+2'), findsOneWidget);
    // 数量标签省去装饰图标及其横向留白，但保留普通标签的高度和真实文字基线。
    final compact = find.ancestor(
      of: find.text('+2'),
      matching: find.byType(SemanticTag),
    );
    final ordinary = find.ancestor(
      of: find.text('无酒精'),
      matching: find.byType(SemanticTag),
    );
    expect(
      tester.getSize(compact).height,
      closeTo(tester.getSize(ordinary).height, .1),
    );
    expect(
      tester.getSize(compact).width,
      lessThan(tester.getSize(ordinary).width),
    );
    expect(
      find.descendant(of: compact, matching: find.byType(Icon)),
      findsNothing,
    );
    expect(
      textBaseline(tester, find.text('+2')),
      closeTo(textBaseline(tester, find.text('无酒精')), .1),
    );
    final summary = tester.widget<Semantics>(
      find.byWidgetPredicate(
        (widget) =>
            widget is Semantics &&
            widget.properties.label == '匹配：无酒精、清爽、可加冰、水果、甜、现代',
      ),
    );
    expect(summary.container, isTrue);
    expect(tester.takeException(), isNull);
  });

  for (final fallback in [false, true]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('标签中文数字统一真实基线与高度，长中文不裁切 fallback=$fallback/$scale', (
        tester,
      ) async {
        if (!(fallback ? fallbackFontLoaded : preferredFontLoaded)) {
          markTestSkipped('当前环境未提供本用例所需的真实中文字体。');
          return;
        }
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = const Size(820, 1000);
        addTearDown(tester.view.resetDevicePixelRatio);
        addTearDown(tester.view.resetPhysicalSize);
        const longText = '真实中文回退字体和很长的完整标签仍然自然换行并保留全部内容';
        await tester.pumpWidget(
          fixture(
            DefaultTextStyle.merge(
              // 用不存在的优先字体真实触发回退，而不是仅把测试名字写成“回退”。
              // 两个中文字体都由上述 FontLoader 读入，不能以 Ahem 的方框代替字形。
              style: TextStyle(
                fontFamily: fallback
                    ? '__missing_tag_test_font__'
                    : stewardFontFamily,
                fontFamilyFallback: const ['Microsoft YaHei'],
              ),
              child: const Padding(
                padding: EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Wrap(
                      spacing: 5,
                      children: [
                        OrderTag('清爽', kind: OrderTagKind.success),
                        SemanticTag(
                          text: 'Ag12',
                          semanticLabel: 'Ag12',
                          tone: Color(0xff665544),
                        ),
                        SemanticTag(
                          text: '+2',
                          semanticLabel: '还有两项',
                          tone: Color(0xff665544),
                        ),
                        MenuMetric('标价', '55'),
                      ],
                    ),
                    SizedBox(height: 16),
                    SizedBox(
                      width: 170,
                      child: OrderTag(longText, kind: OrderTagKind.request),
                    ),
                  ],
                ),
              ),
            ),
            dark: false,
            scale: scale,
          ),
        );
        await tester.pumpAndSettle();
        final texts = [
          find.text('清爽'),
          find.text('Ag12'),
          find.text('+2'),
          find.text('标价 55'),
        ];
        final baseline = textBaseline(tester, texts.first);
        final ordinary = find.ancestor(
          of: texts.first,
          matching: find.byType(SemanticTag),
        );
        final ordinaryHeight = tester.getSize(ordinary).height;
        for (final text in texts) {
          final tag = find.ancestor(
            of: text,
            matching: find.byType(SemanticTag),
          );
          expect(textBaseline(tester, text), closeTo(baseline, .1));
          expect(tester.getSize(tag).height, closeTo(ordinaryHeight, .1));
        }
        final compact = find.ancestor(
          of: find.text('+2'),
          matching: find.byType(SemanticTag),
        );
        expect(
          find.descendant(of: compact, matching: find.byType(Icon)),
          findsNothing,
        );
        final compactTextBounds = tester.getRect(find.text('+2'));
        // 只有原有8px内边距与1px边框，没有留下20px装饰图标的横向空槽。
        expect(
          compactTextBounds.left - tester.getRect(compact).left,
          closeTo(9, .1),
        );

        final longFinder = find.text(longText);
        final longTag = find.ancestor(
          of: longFinder,
          matching: find.byType(SemanticTag),
        );
        expect(tester.getSize(longTag).height, greaterThan(ordinaryHeight));
        final paragraph = renderedParagraph(tester, longFinder);
        final boxes = paragraph.getBoxesForSelection(
          TextSelection(baseOffset: 0, extentOffset: longText.length),
        );
        expect(boxes.length, greaterThan(1), reason: '长标签必须真正换行，不能截断或缩小字号。');
        final bounds = tester.getRect(longTag);
        for (final box in boxes) {
          final actual = box.toRect().shift(
            paragraph.localToGlobal(Offset.zero),
          );
          expect(actual.left, greaterThanOrEqualTo(bounds.left));
          expect(actual.right, lessThanOrEqualTo(bounds.right));
          expect(actual.top, greaterThanOrEqualTo(bounds.top));
          expect(actual.bottom, lessThanOrEqualTo(bounds.bottom));
        }
        expect(tester.takeException(), isNull);
      });
    }
  }

  test('经营适配保留重复材料、未知集合和服务端数值，不据价格重新计算', () {
    final model = FixtureModel();
    addTearDown(model.dispose);
    final view = serviceFoodView(model, {
      'recipe': {
        'name': '合成料理',
        'cooker': '煮锅',
        'price': 88,
        'ingredients': ['蘑菇', '蘑菇'],
      },
      'baseCost': 999,
      'extraIngredients': <Object?>[],
    });
    expect(view.ingredients!.map((item) => item.name), ['蘑菇', '蘑菇']);
    expect(view.ingredients!.map((item) => item.stock), ['3', '3']);
    expect(view.extras, isEmpty);
    expect(view.facts.map((fact) => (fact.label, fact.value)), [
      ('标价', '88'),
      ('基础成本', '999'),
    ]);
    final missing = serviceFoodView(model, const {});
    expect(missing.ingredients, isNull);
    expect(missing.extras, isNull);
  });

  for (final scale in [1.0, 1.5]) {
    testWidgets('菜单前缀与首标签真实基线一致，换行后内容仍在值列 $scale', (tester) async {
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = const Size(360, 820);
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      await tester.pumpWidget(
        fixture(
          const Padding(
            padding: EdgeInsets.all(16),
            child: SingleChildScrollView(
              child: MenuInfoGroup('匹配', [
                OrderTag('清爽', kind: OrderTagKind.success),
                OrderTag('很长的完整匹配标签必须换行并保留其全部含义', kind: OrderTagKind.success),
                MenuMetric('匹配标签数', '2'),
              ]),
            ),
          ),
          dark: false,
          scale: scale,
        ),
      );
      await tester.pumpAndSettle();
      double baseline(Finder finder) {
        return textBaseline(tester, finder);
      }

      expect(baseline(find.text('匹配')), closeTo(baseline(find.text('清爽')), .1));
      final prefixRight = tester.getRect(find.text('匹配')).right;
      for (final tag in find.byType(OrderTag).evaluate()) {
        final rect = tester.getRect(find.byWidget(tag.widget));
        expect(rect.left, greaterThan(prefixRight));
        expect(rect.right, lessThanOrEqualTo(344));
      }
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('详情补全外层截断匹配，区分目录生效与抵消并保留实体定位', (tester) async {
    const item = MenuItemView(
      kind: MenuItemKind.beverage,
      name: '合成六标签酒水',
      summaryTagLabel: '匹配',
      summaryTags: ['无酒精', '清爽', '可加冰', '水果', '甜', '现代'],
      activeTags: ['目录之外明确生效'],
      suppressedTags: ['被抵消项'],
    );
    await tester.pumpWidget(
      fixture(
        const SingleChildScrollView(
          child: MenuItemDetails(item, showName: true),
        ),
        dark: true,
      ),
    );
    expect(find.text('合成六标签酒水'), findsOneWidget);
    for (final tag in item.summaryTags) {
      expect(
        find.descendant(of: find.byType(OrderTag), matching: find.text(tag)),
        findsOneWidget,
      );
    }
    expect(find.text('标签'), findsOneWidget);
    expect(find.text('酒水'), findsNothing);
    expect(find.text('目录之外明确生效'), findsOneWidget);
    expect(find.text('已抵消 被抵消项'), findsOneWidget);
    expect(find.text('+2'), findsNothing);
    expect(tester.takeException(), isNull);
  });

  testWidgets('增量详情不重复基本菜品与统计，未知加料不同于不加料', (tester) async {
    await tester.pumpWidget(
      fixture(
        const Column(
          children: [
            MenuItemDetails(food),
            MenuItemContent(
              MenuItemView(kind: MenuItemKind.food, name: '未知配方'),
            ),
            MenuItemContent(
              MenuItemView(
                kind: MenuItemKind.food,
                name: '无加料配方',
                ingredients: [],
                extras: [],
              ),
            ),
          ],
        ),
        dark: false,
      ),
    );
    expect(find.text('竹笋海鲜味噌汤'), findsNothing);
    expect(find.text('标价 55'), findsNothing);
    expect(find.text('已抵消 重油'), findsOneWidget);
    expect(find.text('服务端给出的匹配解释'), findsOneWidget);
    expect(find.text('加料未知'), findsOneWidget);
    expect(find.text('不加料'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
