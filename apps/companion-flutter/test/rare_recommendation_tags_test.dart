import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import '../tool/preview/rich_preview_workbench_model.dart';
import 'support/page_activity_assertions.dart';

/// 标签刻意互有交集和差异，验证换客时“保留合法值，否则取首项”的契约。
/// 这些只是合成客人数据，不读取游戏、不把预览推荐当作 C# 算法结果。
const _customers = <DataMap>[
  {
    'id': 301,
    'name': '合成稀客甲',
    'positiveTags': ['流行喜爱', '鲜', '素', '流行厌恶', '鲜'],
    'beverageTags': ['无酒精', '清爽'],
  },
  {
    'id': 302,
    'name': '合成稀客乙',
    'positiveTags': ['甜', '素'],
    'beverageTags': ['低酒精', '清爽'],
  },
  {
    'id': 303,
    'name': '合成稀客丙',
    'positiveTags': ['肉'],
    'beverageTags': ['苦'],
  },
  {
    'id': 304,
    'name': '标签未就绪稀客',
    'positiveTags': ['流行喜爱', '流行厌恶'],
    'beverageTags': [],
  },
];

class _TagModel extends RichPreviewWorkbenchModel {
  _TagModel() {
    catalog = {...catalog, 'isComplete': true, 'rareCustomers': _customers};
  }
  final List<DataMap> queries = [];
  final List<(String, Map<String, String>)> mutations = [];
  int clearCount = 0;

  /// 模拟后续轮询发布目录，测试真实页面能撤销已经失效的查询条件。
  void replaceCatalog(DataMap next) {
    catalog = next;
    notifyListeners();
  }

  void reconnect() {
    connectionVersion++;
    notifyListeners();
  }

  @override
  void clearRecommendationQuery() {
    clearCount++;
    super.clearRecommendationQuery();
  }

  @override
  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  }) async {
    mutations.add((path, Map.of(query)));
    await super.mutate(path, query: query, body: body, localOnly: localOnly);
  }

  @override
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  }) async {
    queries.add({
      'kind': kind,
      'customerId': customerId,
      'foodTag': foodTag,
      'beverageTag': beverageTag,
    });
    await super.queryRecommendations(
      kind: kind,
      selectedPlace: selectedPlace,
      customerId: customerId,
      foodTag: foodTag,
      beverageTag: beverageTag,
    );
  }
}

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
    await (FontLoader('packages/forui_lucide/ForuiLucideIcons')
          ..addFont(rootBundle.load('packages/forui_lucide/assets/lucide.ttf')))
        .load();
  });

  Future<void> open(
    WidgetTester tester,
    _TagModel model, {
    double width = 1280,
    GlobalKey? capture,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(model.dispose);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: RepaintBoundary(key: capture, child: const StewardApp()),
      ),
    );
    await tester.pumpAndSettle();
    final desktopNavigation = find.byKey(const ValueKey('nav-2'));
    await tester.tap(
      desktopNavigation.evaluate().isNotEmpty
          ? desktopNavigation
          : find.text('推荐').last,
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('稀客'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
  }

  Finder popupSearch() => find.descendant(
    of: find.byWidgetPredicate(
      (widget) =>
          widget is FTextField && (widget.hint?.startsWith('搜索') ?? false),
    ),
    matching: find.byType(EditableText),
  );

  Future<void> choose(
    WidgetTester tester,
    String key,
    String option, {
    String? search,
  }) async {
    await tester.tap(find.byKey(ValueKey(key)));
    await tester.pumpAndSettle();
    if (search != null) {
      await tester.enterText(popupSearch(), search);
      await tester.pumpAndSettle();
    }
    if (key == 'rare-customer-selector') {
      // 带图弹层不能被三列筛选的窄锚点压缩：保留图片的真实缩放尺寸，
      // 同时给文字留下可读空间；仅“不抛overflow”不能证明未缩小图像掩盖问题。
      final item = find.ancestor(
        of: find.text(option),
        matching: find.byWidgetPredicate((widget) => widget is FSelectItem),
      );
      final image = find.descendant(
        of: item,
        matching: find.byType(EntityImage),
      );
      final extent = MediaQuery.textScalerOf(tester.element(image)).scale(20);
      expect(tester.getSize(image), Size(extent, extent));
      // 已选值会同时出现在锚点的只读EditableText和弹层Text中，不能全页面按
      // 文本取几何。这里限定真实FSelectItem子树，只测量与图片同行的候选标题。
      final title = find.descendant(
        of: item,
        matching: find.byWidgetPredicate(
          (widget) => widget is Text && widget.data == option,
        ),
      );
      expect(title, findsOneWidget);
      final textBounds = tester.getRect(title);
      final itemBounds = tester.getRect(item);
      expect(textBounds.width, greaterThanOrEqualTo(extent));
      expect(
        textBounds.left,
        greaterThanOrEqualTo(tester.getRect(image).right),
      );
      expect(textBounds.right, lessThanOrEqualTo(itemBounds.right));
      expect(tester.takeException(), isNull);
    }
    await tester.tap(
      find.ancestor(
        of: find.text(option),
        matching: find.byWidgetPredicate((widget) => widget is FSelectItem),
      ),
    );
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
  }

  Finder option(String text) => find.ancestor(
    of: find.text(text),
    matching: find.byWidgetPredicate((widget) => widget is FSelectItem),
  );
  Finder input(String key) => find.descendant(
    of: find.byKey(ValueKey(key)),
    matching: find.byType(EditableText),
  );
  void selected(WidgetTester tester, String key, String value) => expect(
    tester.widget<EditableText>(input(key)).controller.text,
    key == 'rare-food-tag-selector'
        ? '料理：$value'
        : key == 'rare-beverage-tag-selector'
        ? '酒水：$value'
        : value,
  );
  bool enabled(WidgetTester tester, String key) => tester
      .widget<FSelect<dynamic>>(
        find.descendant(
          of: find.byKey(ValueKey(key)),
          matching: find.byWidgetPredicate((widget) => widget is FSelect),
        ),
      )
      .enabled;

  for (final scale in [100, 150]) {
    testWidgets('最窄Windows三个推荐条件同排等宽且搜索仍可用：$scale%', (tester) async {
      final model = _TagModel();
      model.localPreferences['fontScalePercent'] = scale;
      // 原生最小窗口为640px；再扣除边框预留以624px内容视口验证，不能只用
      // 脱离应用侧栏和正文边距的孤立Row判断是否放得下三个选择器。
      await open(tester, model, width: 624);
      const keys = [
        'rare-customer-selector',
        'rare-food-tag-selector',
        'rare-beverage-tag-selector',
      ];
      final requestCount = model.queries.length;
      for (final width in [624.0, 640.0, 1280.0, 624.0]) {
        tester.view.physicalSize = Size(width, 920);
        await tester.pumpAndSettle();
        final rectangles = [
          for (final key in keys) tester.getRect(find.byKey(ValueKey(key))),
        ];
        for (final rectangle in rectangles) {
          expect(rectangle.width, stewardSelectWidth);
          expect(rectangle.center.dy, closeTo(rectangles.first.center.dy, .01));
          expect(rectangle.right, lessThanOrEqualTo(width - 24));
        }
        for (var index = 1; index < rectangles.length; index++) {
          expect(rectangles[index].left - rectangles[index - 1].right, 8);
        }
        selected(tester, keys.first, '合成稀客甲');
        selected(tester, keys[1], '鲜');
        selected(tester, keys[2], '无酒精');
        expect(tester.takeException(), isNull);
      }
      expect(model.queries.length, requestCount);
      // 真实打开、搜索、选择并核对查询，防止收窄后只有边框同排而无法使用候选。
      await choose(tester, keys.first, '合成稀客乙', search: '客乙');
      selected(tester, keys.first, '合成稀客乙');
      expect(model.queries.last['customerId'], 302);
      await tester.tap(find.text('普客'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 250));
      await tester.pumpAndSettle();
      expect(
        tester
            .getSize(find.byKey(const ValueKey('normal-place-selector')))
            .width,
        stewardSelectWidth,
      );
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  Future<void> screenshot(
    GlobalKey capture,
    String name,
    WidgetTester tester,
  ) async {
    final directory = Platform.environment['MYSTIA_UI_EVIDENCE'];
    if (directory == null) return;
    await tester.runAsync(() async {
      final frame =
          await (capture.currentContext!.findRenderObject()!
                  as RenderRepaintBoundary)
              .toImage();
      final bytes = await frame.toByteData(format: ui.ImageByteFormat.png);
      await Directory(directory).create(recursive: true);
      await File('$directory/$name.png')
          .writeAsBytes(bytes!.buffer.asUint8List());
      frame.dispose();
    });
  }

  for (final searchable in [false, true]) {
    testWidgets('${searchable ? '搜索' : '普通'}下拉激活当前项关闭且不重复提交', (tester) async {
      var calls = 0;
      final theme = stewardTheme(dark: false, touch: false);
      await tester.pumpWidget(
        material.MaterialApp(
          theme: theme.toApproximateMaterialTheme(),
          builder: (context, child) => FTheme(data: theme, child: child!),
          home: Center(
            child: SizedBox(
              width: 220,
              child: StewardSelect<String>(
                key: const ValueKey('same-value-select'),
                label: '预算策略',
                items: const {'block': '不足时阻止', 'warn': '仅提示'},
                value: 'block',
                searchable: searchable,
                onChanged: (_) => calls++,
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('same-value-select')));
      await tester.pumpAndSettle();
      await tester.tap(option('不足时阻止'));
      await tester.pumpAndSettle();
      expect(option('不足时阻止'), findsNothing);
      expect(calls, 0);
      if (!searchable) {
        // 普通下拉打开后当前项获得键盘焦点；Enter 与指针激活有相同关闭语义。
        await tester.tap(find.byKey(const ValueKey('same-value-select')));
        await tester.pumpAndSettle();
        await tester.sendKeyEvent(LogicalKeyboardKey.enter);
        await tester.pumpAndSettle();
        expect(option('不足时阻止'), findsNothing);
        expect(calls, 0);
      }
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('稀客标签只来自当前客人，动态流行标签不可选，请求使用所选中文名', (tester) async {
    final model = _TagModel();
    await open(tester, model);
    expect(find.text('查询推荐'), findsNothing);
    await choose(tester, 'rare-customer-selector', '合成稀客甲', search: '客甲');
    selected(tester, 'rare-food-tag-selector', '鲜');
    selected(tester, 'rare-beverage-tag-selector', '无酒精');
    await tester.tap(find.byKey(const ValueKey('rare-food-tag-selector')));
    await tester.pumpAndSettle();
    expect(option('鲜'), findsOneWidget);
    expect(option('素'), findsOneWidget);
    expect(option('流行喜爱'), findsNothing);
    expect(option('流行厌恶'), findsNothing);
    expect(option('肉'), findsNothing);
    // 搜索只过滤选项，任意文本不能成为可提交的新标签。
    await tester.enterText(popupSearch(), '任意不存在标签');
    await tester.pumpAndSettle();
    expect(option('素'), findsNothing);
    expect(find.text('没有匹配项'), findsOneWidget);
    await tester.enterText(popupSearch(), '素');
    await tester.pumpAndSettle();
    expect(find.text('没有匹配项'), findsNothing);
    await tester.tap(option('素'));
    await tester.pumpAndSettle();
    await choose(tester, 'rare-beverage-tag-selector', '清爽', search: '爽');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(model.queries.last, {
      'kind': 'rare',
      'customerId': 301,
      'foodTag': '素',
      'beverageTag': '清爽',
    });
    // 收藏键必须与刚提交的点单需求一致，不能落成标签序号或原自由文本字段的旧值。
    await tester.tap(find.text('加入收藏'));
    await tester.pumpAndSettle();
    expect(model.mutations.last.$1, '/favorites/add-recipe');
    expect(model.mutations.last.$2['customerId'], '301');
    expect(model.mutations.last.$2['foodTag'], '素');
    await tester.tap(find.text('酒水'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('加入收藏'));
    await tester.pumpAndSettle();
    expect(model.mutations.last.$1, '/favorites/add-beverage');
    expect(model.mutations.last.$2['customerId'], '301');
    expect(model.mutations.last.$2['beverageTag'], '清爽');
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('换稀客保留交集标签，不合法时取新客人首项并停止旧查询', (tester) async {
    final model = _TagModel();
    await open(tester, model);
    await choose(tester, 'rare-customer-selector', '合成稀客甲');
    await choose(tester, 'rare-food-tag-selector', '素');
    await choose(tester, 'rare-beverage-tag-selector', '清爽');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    final clearCount = model.clearCount, queryCount = model.queries.length;
    await choose(tester, 'rare-customer-selector', '合成稀客乙');
    selected(tester, 'rare-food-tag-selector', '素');
    selected(tester, 'rare-beverage-tag-selector', '清爽');
    expect(model.clearCount, greaterThan(clearCount));
    expect(model.queries.length, greaterThan(queryCount));
    expect(
      tester.widget<DenseListDetail>(find.byType(DenseListDetail)).items,
      hasLength(8),
    );
    await choose(tester, 'rare-customer-selector', '合成稀客丙');
    selected(tester, 'rare-food-tag-selector', '肉');
    selected(tester, 'rare-beverage-tag-selector', '苦');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(model.queries.last['customerId'], 303);
    expect(model.queries.last['foodTag'], '肉');
    expect(model.queries.last['beverageTag'], '苦');
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('同名稀客仍以独立 ID 选择，下拉输入不能提交任意客人', (tester) async {
    final model = _TagModel();
    model.catalog = {
      ...model.catalog,
      'rareCustomers': [
        {..._customers[0], 'name': '同名客人'},
        {..._customers[1], 'name': '同名客人'},
      ],
    };
    await open(tester, model);
    final before = model.queries.length;
    await tester.tap(find.byKey(const ValueKey('rare-customer-selector')));
    await tester.pumpAndSettle();
    await tester.enterText(popupSearch(), '不存在');
    await tester.pumpAndSettle();
    expect(find.text('没有匹配项'), findsOneWidget);
    expect(model.queries.length, before);
    await tester.enterText(popupSearch(), '同名');
    await tester.pumpAndSettle();
    expect(option('同名客人'), findsNWidgets(2));
    await tester.tap(
      find.byWidgetPredicate(
        (widget) => widget is FSelectItem<int> && widget.value == 302,
      ),
    );
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(model.queries.last['customerId'], 302);
    expect(model.queries.last['foodTag'], '甜');
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('稀客缺少合法列表时清空旧需求、禁查询并解释空状态', (tester) async {
    final model = _TagModel();
    await open(tester, model);
    await choose(tester, 'rare-customer-selector', '合成稀客甲');
    await choose(tester, 'rare-customer-selector', '标签未就绪稀客');
    expect(enabled(tester, 'rare-food-tag-selector'), isFalse);
    expect(enabled(tester, 'rare-beverage-tag-selector'), isFalse);
    expect(find.text('查询推荐'), findsNothing);
    await expectPageActivityDetails(tester, '标签列表为空');
    expect(model.queries.where((query) => query['customerId'] == 304), isEmpty);
    model.replaceCatalog({...model.catalog, 'rareCustomers': <DataMap>[]});
    await tester.pumpAndSettle();
    await expectPageActivityDetails(tester, '当前目录没有可选稀客。');
    expect(enabled(tester, 'rare-customer-selector'), isFalse);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('运行时目录未完成期间不能选择或查询，加载完成后恢复', (tester) async {
    final model = _TagModel();
    model.catalog = {...model.catalog, 'isComplete': false};
    await open(tester, model);
    await expectPageActivityDetails(tester, '正在等待完整的稀客资料');
    expect(enabled(tester, 'rare-customer-selector'), isFalse);
    expect(find.text('查询推荐'), findsNothing);
    model.replaceCatalog({...model.catalog, 'isComplete': true});
    await tester.pumpAndSettle();
    await choose(tester, 'rare-customer-selector', '合成稀客甲');
    expect(find.text('查询推荐'), findsNothing);
    expect(model.queries.where((query) => query['customerId'] == 304), isEmpty);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('目录轮询移除所选标签后同步首项且撤销旧结果', (tester) async {
    final model = _TagModel();
    await open(tester, model);
    await choose(tester, 'rare-customer-selector', '合成稀客甲');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    final before = model.clearCount;
    model.replaceCatalog({
      ...model.catalog,
      'rareCustomers': [
        {
          ..._customers.first,
          'positiveTags': ['素'],
        },
      ],
    });
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    selected(tester, 'rare-food-tag-selector', '素');
    expect(model.clearCount, greaterThan(before));
    expect(
      tester.widget<DenseListDetail>(find.byType(DenseListDetail)).items,
      hasLength(8),
    );
    await tester.pumpWidget(const SizedBox());
  });

  for (final changeConnection in [false, true]) {
    testWidgets('标签下拉打开后${changeConnection ? '连接代次改变' : '目录移除选项'}，关闭弹层且拒绝旧回调', (
      tester,
    ) async {
      final model = _TagModel();
      await open(tester, model);
      await choose(tester, 'rare-customer-selector', '合成稀客甲');
      await tester.tap(find.byKey(const ValueKey('rare-food-tag-selector')));
      await tester.pumpAndSettle();
      final oldSelection = tester
          .widget<StewardSelect<String>>(
            find.byKey(const ValueKey('rare-food-tag-selector')),
          )
          .onChanged;
      if (changeConnection) {
        model.reconnect();
      } else {
        model.replaceCatalog({
          ...model.catalog,
          'rareCustomers': [
            {
              ..._customers.first,
              'positiveTags': ['鲜'],
            },
          ],
        });
      }
      await tester.pumpAndSettle();
      expect(option('素'), findsNothing);
      oldSelection('素');
      await tester.pumpAndSettle();
      selected(tester, 'rare-food-tag-selector', '鲜');
      expect(
        model.queries.where((query) => query['customerId'] == 304),
        isEmpty,
      );
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('标签很多时官方下拉限高，末尾选项仍可滚动选择', (tester) async {
    final model = _TagModel();
    model.catalog = {
      ...model.catalog,
      'rareCustomers': [
        {
          ..._customers.first,
          'positiveTags': [for (var i = 0; i < 40; i++) '料理标签 $i'],
        },
      ],
    };
    model.localPreferences = {'fontScalePercent': 150, 'reduceMotion': true};
    await open(tester, model, width: 390);
    await choose(tester, 'rare-customer-selector', '合成稀客甲');
    await tester.tap(find.byKey(const ValueKey('rare-food-tag-selector')));
    await tester.pumpAndSettle();
    final scrollable = find
        .ancestor(of: option('料理标签 0'), matching: find.byType(Scrollable))
        .first;
    expect(tester.getSize(scrollable).height, lessThanOrEqualTo(300));
    await tester.scrollUntilVisible(
      find.text('料理标签 39'),
      180,
      scrollable: scrollable,
    );
    await tester.pumpAndSettle();
    await tester.tap(option('料理标签 39'));
    await tester.pumpAndSettle();
    selected(tester, 'rare-food-tag-selector', '料理标签 39');
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('稀客选择器在 $width px ${width == 390 ? '150% 字号' : ''}可以打开、选择和查询', (
      tester,
    ) async {
      final model = _TagModel();
      model.localPreferences = {
        'fontScalePercent': width == 390 ? 150 : 100,
        'reduceMotion': true,
      };
      final capture = GlobalKey();
      await open(tester, model, width: width, capture: capture);
      await choose(tester, 'rare-customer-selector', '合成稀客甲');
      await tester.tap(find.byKey(const ValueKey('rare-food-tag-selector')));
      await tester.pumpAndSettle();
      // 直接检查真实下拉选项与搜索框的视口边界，不依赖旧模态目录的固定尺寸。
      expect(
        find.byKey(const ValueKey('compact-catalog-picker')),
        findsNothing,
      );
      final bounds = tester.getRect(option('素'));
      expect(bounds.left, greaterThanOrEqualTo(0));
      expect(bounds.right, lessThanOrEqualTo(width));
      expect(bounds.bottom, lessThanOrEqualTo(920));
      final searchBounds = tester.getRect(popupSearch());
      expect(searchBounds.top, greaterThanOrEqualTo(0));
      expect(searchBounds.right, lessThanOrEqualTo(width));
      expect(tester.takeException(), isNull);
      await screenshot(capture, 'rare-tag-picker-${width.toInt()}', tester);
      await tester.tap(option('素'));
      await tester.pumpAndSettle();
      await choose(tester, 'rare-beverage-tag-selector', '清爽');
      await tester.pump(const Duration(milliseconds: 250));
      await tester.pumpAndSettle();
      expect(model.queries.last['foodTag'], '素');
      expect(model.queries.last['beverageTag'], '清爽');
      expect(tester.takeException(), isNull);
      await screenshot(capture, 'rare-tags-${width.toInt()}', tester);
      await tester.pumpWidget(const SizedBox());
    });
  }
}
