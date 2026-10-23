import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/recommendation_preferences.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/steward_dynamic_tags.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import '../tool/preview/rich_preview_workbench_model.dart';
import 'support/page_activity_assertions.dart';

/// 只延迟内存配置回传，不伪造全局 busy，专门暴露 UI 同帧重复提交的竞态。
class _ExclusionModel extends RichPreviewWorkbenchModel {
  Completer<void>? gate;
  bool reject = false;
  final writes = <DataMap>[];

  @override
  Future<void> updateProfile(DataMap changes) async {
    writes.add(changes);
    await gate?.future;
    if (reject) {
      error = '合成保存失败';
      notifyListeners();
      return;
    }
    await super.updateProfile(changes);
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

  Finder owner(String key) => find.byKey(ValueKey(key));
  Finder add(String key) => find.descendant(
    of: owner(key),
    matching: find.byKey(const ValueKey('dynamic-tags-add')),
  );
  Finder tag(String key, int id) => find.descendant(
    of: owner(key),
    matching: find.byKey(ValueKey(('dynamic-tag', id))),
  );
  Finder item(int id) => find.byWidgetPredicate(
    (widget) => widget is FSelectItem<int> && widget.value == id,
  );

  void expectImage(WidgetTester tester, Finder parent, String key, int id) {
    final finder = find.descendant(
      of: parent,
      matching: find.byType(EntityImage),
    );
    expect(finder, findsOneWidget);
    final image = tester.widget<EntityImage>(finder);
    expect(
      image.kind,
      key == 'excludedIngredientIds'
          ? EntityImageKind.ingredient
          : EntityImageKind.beverage,
    );
    expect(image.id, id);
    final extent = MediaQuery.textScalerOf(tester.element(finder)).scale(20);
    expect(tester.getSize(finder), Size.square(extent));
    // 此设置夹具未注入资源Scope；图片缺失仍占原槽位且不增加额外语义或点击路径。
    expect(
      find.descendant(of: finder, matching: find.byType(Icon)),
      findsOneWidget,
    );
  }

  Future<void> open(
    WidgetTester tester,
    _ExclusionModel model, {
    double width = 1280,
    double scale = 1,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(model.dispose);
    final theme = stewardTheme(dark: false, touch: width < 640);
    await tester.pumpWidget(
      material.MaterialApp(
        localizationsDelegates: FLocalizations.localizationsDelegates,
        supportedLocales: FLocalizations.supportedLocales,
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
        home: Padding(
          // 真实布局保留 80px 导航与左右各 24px 内容边距，不能拿完整手机宽度测标签。
          padding: const EdgeInsets.fromLTRB(104, 12, 24, 12),
          child: ListenableBuilder(
            listenable: model,
            builder: (_, _) => RecommendationPreferencesPanel(model),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> reveal(WidgetTester tester, Finder finder) async {
    // 下拉框的文字与弹层也会创建 Scrollable；不能依赖全局 last 的建树顺序。
    // 懒构建到排除区域后全局 last 会变成输入框的横向滚动，导致窄屏继续拖错对象。
    final form = find
        .descendant(
          of: find.byKey(const PageStorageKey('recommendation-preferences')),
          matching: find.byType(Scrollable),
        )
        .first;
    await tester.scrollUntilVisible(finder, 250, scrollable: form);
    await tester.ensureVisible(finder);
    await tester.pumpAndSettle();
  }

  Future<void> select(WidgetTester tester, String key, int id) async {
    await reveal(tester, add(key));
    await tester.tap(add(key));
    await tester.pumpAndSettle();
    await tester.ensureVisible(item(id));
    expectImage(tester, item(id), key, id);
    await tester.tap(item(id));
    await tester.pumpAndSettle();
  }

  testWidgets('搜索只过滤候选，按ID添加后可逐项移除且无模态目录', (tester) async {
    final model = _ExclusionModel();
    await open(tester, model);
    await reveal(tester, add('excludedIngredientIds'));
    await tester.tap(add('excludedIngredientIds'));
    await tester.pumpAndSettle();
    final search = find.descendant(
      of: find.byWidgetPredicate(
        (widget) => widget is FTextField && widget.hint == '搜索排除食材',
      ),
      matching: find.byType(EditableText),
    );
    await tester.enterText(search, '蘑');
    await tester.pumpAndSettle();
    expect(item(1), findsOneWidget);
    expect(item(2), findsNothing);
    expectImage(tester, item(1), 'excludedIngredientIds', 1);
    expect(model.writes, isEmpty);
    expect(find.byType(FDialog), findsNothing);
    await tester.tap(item(1));
    await tester.pumpAndSettle();
    expect(tag('excludedIngredientIds', 1), findsOneWidget);
    expectImage(
      tester,
      tag('excludedIngredientIds', 1),
      'excludedIngredientIds',
      1,
    );
    await tester.ensureVisible(tag('excludedIngredientIds', 1));
    await tester.tap(tag('excludedIngredientIds', 1));
    await tester.pumpAndSettle();
    expect(tag('excludedIngredientIds', 1), findsNothing);
    expect(model.writes, hasLength(2));
    expect(
      object(
        model.profile['recommendationExclusions'],
      )['excludedIngredientIds'],
      isEmpty,
    );
  });

  testWidgets('共享保存门闩立即阻止同帧覆盖，后续添加从最新配置合并两组资源', (tester) async {
    final model = _ExclusionModel()..gate = Completer<void>();
    await open(tester, model);
    await reveal(tester, owner('excludedIngredientIds'));
    final ingredient = tester.widget<StewardDynamicTags<int>>(
      owner('excludedIngredientIds'),
    );
    final beverage = tester.widget<StewardDynamicTags<int>>(
      owner('excludedBeverageIds'),
    );
    ingredient.onAdd(1);
    beverage.onAdd(201);
    await tester.pump();
    expect(model.writes, hasLength(1));
    expect(find.text('正在保存排除设置…'), findsWidgets);
    expect(
      tag('excludedIngredientIds', 1),
      findsNothing,
      reason: '未返回前不显示虚假的成功标签',
    );
    expect(
      tester.widget<StewardSelect<int>>(add('excludedIngredientIds')).enabled,
      isFalse,
    );
    model.gate!.complete();
    await tester.pumpAndSettle();
    model.gate = null;
    await select(tester, 'excludedBeverageIds', 201);
    expectImage(
      tester,
      tag('excludedBeverageIds', 201),
      'excludedBeverageIds',
      201,
    );
    await select(tester, 'excludedIngredientIds', 2);
    expect(
      object(
        model.profile['recommendationExclusions'],
      )['excludedIngredientIds'],
      [1, 2],
    );
    expect(
      object(model.profile['recommendationExclusions'])['excludedBeverageIds'],
      [201],
    );
    expect(model.writes, hasLength(3));
  });

  testWidgets('保存失败保留旧标签并反馈，重新明确选择后才重试', (tester) async {
    final model = _ExclusionModel()..reject = true;
    await open(tester, model);
    await select(tester, 'excludedIngredientIds', 1);
    expect(tag('excludedIngredientIds', 1), findsNothing);
    expectReportedFailure(tester, '合成保存失败');
    await tester.pump(const Duration(seconds: 1));
    expect(model.writes, hasLength(1));
    model.reject = false;
    await select(tester, 'excludedIngredientIds', 1);
    expect(tag('excludedIngredientIds', 1), findsOneWidget);
    expect(model.writes, hasLength(2));
  });

  testWidgets('同名资源按编号区分，重复目录编号不猜测，已选项不重复添加', (tester) async {
    final model = _ExclusionModel();
    model.catalog['ingredients'] = [
      {'id': 1, 'name': '同名食材'},
      {'id': 2, 'name': '同名食材'},
      {'id': 3, 'name': '歧义甲'},
      {'id': 3, 'name': '歧义乙'},
    ];
    await open(tester, model);
    await reveal(tester, add('excludedIngredientIds'));
    final options = tester
        .widget<StewardSelect<int>>(add('excludedIngredientIds'))
        .items;
    expect(options, {1: '同名食材（编号 1）', 2: '同名食材（编号 2）'});
    await select(tester, 'excludedIngredientIds', 2);
    expect(
      object(
        model.profile['recommendationExclusions'],
      )['excludedIngredientIds'],
      [2],
    );
    final control = tester.widget<StewardDynamicTags<int>>(
      owner('excludedIngredientIds'),
    );
    control.onAdd(2);
    await tester.pumpAndSettle();
    expect(model.writes, hasLength(1));
    expect(
      tester
          .widget<StewardSelect<int>>(add('excludedIngredientIds'))
          .items
          .containsKey(2),
      isFalse,
    );
  });

  testWidgets('空目录不允许添加，但历史未知ID仍可删除；查看设备不可编辑', (tester) async {
    final model = _ExclusionModel();
    model.catalog['ingredients'] = <DataMap>[];
    model.profile['recommendationExclusions'] = {
      'excludedIngredientIds': [9999],
      'excludedBeverageIds': <int>[],
    };
    await open(tester, model);
    await reveal(tester, owner('excludedIngredientIds'));
    expect(find.text('编号 9999（目录中未找到）'), findsOneWidget);
    expectImage(
      tester,
      tag('excludedIngredientIds', 9999),
      'excludedIngredientIds',
      9999,
    );
    expect(
      tester.widget<StewardSelect<int>>(add('excludedIngredientIds')).items,
      isEmpty,
    );
    model.authority['currentDeviceIsPrimary'] = false;
    model.notifyListeners();
    await tester.pumpAndSettle();
    expect(
      tester.widget<FButton>(tag('excludedIngredientIds', 9999)).onPress,
      isNull,
    );
    model.authority['currentDeviceIsPrimary'] = true;
    model.notifyListeners();
    await tester.pumpAndSettle();
    await tester.ensureVisible(tag('excludedIngredientIds', 9999));
    await tester.tap(tag('excludedIngredientIds', 9999));
    await tester.pumpAndSettle();
    expect(model.writes, hasLength(1));
    expect(tag('excludedIngredientIds', 9999), findsNothing);
  });

  testWidgets('目录改变关闭旧搜索弹层，旧候选不能添加', (tester) async {
    final model = _ExclusionModel();
    await open(tester, model);
    await reveal(tester, add('excludedIngredientIds'));
    await tester.tap(add('excludedIngredientIds'));
    await tester.pumpAndSettle();
    final old = tester
        .widget<StewardSelect<int>>(add('excludedIngredientIds'))
        .onChanged;
    model.catalog['ingredients'] = <DataMap>[];
    model.notifyListeners();
    await tester.pumpAndSettle();
    expect(item(1), findsNothing);
    old(1);
    await tester.pumpAndSettle();
    expect(model.writes, isEmpty);
    expectReportedFailure(tester, '连接、目录或主设备已变化，排除设置未保存，请重新选择。');
  });

  testWidgets('不完整目录禁止新增但仍保留已选资源名称与移除能力', (tester) async {
    final model = _ExclusionModel();
    model.catalog['isComplete'] = false;
    model.profile['recommendationExclusions'] = {
      'excludedIngredientIds': [1],
      'excludedBeverageIds': <int>[],
    };
    await open(tester, model);
    await reveal(tester, owner('excludedIngredientIds'));
    expect(find.text('蘑菇'), findsOneWidget);
    expect(
      tester.widget<StewardSelect<int>>(add('excludedIngredientIds')).enabled,
      isFalse,
    );
    final control = tester.widget<StewardDynamicTags<int>>(
      owner('excludedIngredientIds'),
    );
    control.onAdd(2);
    await tester.pumpAndSettle();
    expect(model.writes, isEmpty);
    await tester.ensureVisible(tag('excludedIngredientIds', 1));
    await tester.tap(tag('excludedIngredientIds', 1));
    await tester.pumpAndSettle();
    expect(model.writes, hasLength(1));
    expect(tag('excludedIngredientIds', 1), findsNothing);
  });

  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('动态标签 ${width.toInt()}px/150%长名称完整换行且可删除', (tester) async {
      final model = _ExclusionModel();
      model.catalog['ingredients'] = [
        {'id': 1, 'name': '一个用于核对窄屏与字号放大后仍可完整阅读的合成食材名称'},
        {'id': 2, 'name': '豆腐'},
      ];
      model.profile['recommendationExclusions'] = {
        'excludedIngredientIds': [1, 2],
        'excludedBeverageIds': <int>[],
      };
      await open(tester, model, width: width, scale: 1.5);
      await reveal(tester, tag('excludedIngredientIds', 1));
      expect(
        tester.getSize(add('excludedIngredientIds')).width,
        lessThanOrEqualTo(200),
      );
      final rect = tester.getRect(tag('excludedIngredientIds', 1));
      expectImage(
        tester,
        tag('excludedIngredientIds', 1),
        'excludedIngredientIds',
        1,
      );
      expect(rect.width, greaterThanOrEqualTo(40));
      expect(rect.height, greaterThanOrEqualTo(40));
      expect(rect.left, greaterThanOrEqualTo(104));
      expect(rect.right, lessThanOrEqualTo(width - 24));
      await tester.tap(tag('excludedIngredientIds', 1));
      await tester.pumpAndSettle();
      expect(
        object(
          model.profile['recommendationExclusions'],
        )['excludedIngredientIds'],
        [2],
      );
      expect(tester.takeException(), isNull);
    });
  }
}
