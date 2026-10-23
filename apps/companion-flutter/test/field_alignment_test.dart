import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/features/recommendations.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/features/tools.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import '../tool/preview/rich_preview_workbench_model.dart';
import 'support/fixture_model.dart';

const _longSettingName = '每个料理的加料变体数';

/// 使用产品真实主题与实际控件，固定字号和视口；不以测试专用高度替代产品布局。
/// 设置用内存预览模型，整个文件不连接宿主、不初始化原生服务、不写本地偏好。
Widget _themed(Widget child, double scale, {required bool touch}) {
  // 桌面浅色与触屏深色都跑完整尺寸矩阵；不能用桌面字体/内边距冒充触屏控件。
  final theme = stewardTheme(dark: touch, touch: touch);
  return material.MaterialApp(
    locale: const Locale('zh', 'CN'),
    localizationsDelegates: FLocalizations.localizationsDelegates,
    supportedLocales: FLocalizations.supportedLocales,
    theme: theme.toApproximateMaterialTheme(),
    builder: (context, routedChild) => FTheme(
      data: theme,
      child: MediaQuery(
        data: MediaQuery.of(context).copyWith(
          textScaler: TextScaler.linear(scale),
          disableAnimations: true,
        ),
        child: routedChild!,
      ),
    ),
    home: Padding(padding: const EdgeInsets.all(16), child: child),
  );
}

void _setViewport(WidgetTester tester, double width) {
  tester.view.devicePixelRatio = 1;
  tester.view.physicalSize = Size(width, 1000);
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
}

/// FTextField 的外层可能包含标签与错误。几何断言必须进入真正绘制边框的
/// material_ui.TextField，避免“外壳和按钮等高、实际输入边框仍偏下”的假阳性。
Finder _inputOf(Finder field) =>
    find.descendant(of: field, matching: find.byType(material.TextField));

Finder _editableOf(Finder field) =>
    find.descendant(of: field, matching: find.byType(EditableText));

/// 按钮同样测量真实 FButton，而不是带外部间距的工具条或设置行。
Finder _buttonOf(Finder owner) =>
    find.descendant(of: owner, matching: find.byType(FButton));

({Rect input, Rect button}) _expectAligned(
  WidgetTester tester,
  Finder field,
  Finder button,
) {
  final input = _inputOf(field);
  expect(input, findsOneWidget);
  expect(button, findsOneWidget);
  final inputRect = tester.getRect(input);
  final buttonRect = tester.getRect(button);
  expect(
    (inputRect.center.dy - buttonRect.center.dy).abs(),
    lessThanOrEqualTo(1),
    reason:
        '实际输入边框中心${inputRect.center.dy}与按钮中心${buttonRect.center.dy}应相差不超过1px。',
  );
  expect(
    (inputRect.height - buttonRect.height).abs(),
    lessThanOrEqualTo(1),
    reason:
        '实际输入边框高${inputRect.height}与按钮高${buttonRect.height}应等高，不能只测FTextField外壳。',
  );
  expect(inputRect.height, greaterThan(0));
  final editable = tester.getRect(_editableOf(field));
  expect(editable.top, greaterThanOrEqualTo(inputRect.top - 1));
  expect(
    editable.bottom,
    lessThanOrEqualTo(inputRect.bottom + 1),
    reason: '实际EditableText完整行高应被输入边框容纳，不能以裁切文字获得等高外观。',
  );
  return (input: inputRect, button: buttonRect);
}

/// 测量误差允许1px栅格取整；比较相对设置行的位置，避免滚动偏移掩盖控件自身跳动。
void _expectSameOffset(Offset before, Offset after) {
  expect((before.dx - after.dx).abs(), lessThanOrEqualTo(1));
  expect((before.dy - after.dy).abs(), lessThanOrEqualTo(1));
}

/// 直接检查输入控件的语义节点，不能让行外同名Text令语义断言误通过。
void _expectNamedInput(
  WidgetTester tester,
  Finder field,
  String name,
  String value,
) {
  final data = tester.getSemantics(_inputOf(field)).getSemanticsData();
  expect(data.label, contains(name));
  expect(data.value, value);
}

/// 懒构建设置行必须先进入视口，避免对屏外控件坐标或不可点击按钮作无效断言。
Future<void> _revealRecommendation(WidgetTester tester, Finder target) async {
  await tester.scrollUntilVisible(
    target,
    160,
    scrollable: find
        .descendant(
          of: find.byKey(const PageStorageKey('recommendation-preferences')),
          matching: find.byType(Scrollable),
        )
        .first,
  );
  await Scrollable.ensureVisible(tester.element(target), alignment: .25);
  await tester.pumpAndSettle();
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  // 锁定SDK的testWidgets默认启用语义并负责释放句柄，无需额外持有SemanticsHandle。

  for (final touch in [false, true]) {
    for (final width in [1280.0, 640.0, 390.0]) {
      for (final scale in [1.0, 1.5]) {
        final sizeLabel =
            '${touch ? '触屏深色' : '桌面浅色'} / ${width.toInt()}px / ${(scale * 100).toInt()}%';

        testWidgets('设置长名称与滑块在各尺寸完整可见并自动应用：$sizeLabel', (tester) async {
          _setViewport(tester, width);
          final model = RichPreviewWorkbenchModel();
          addTearDown(model.dispose);
          await tester.pumpWidget(
            _themed(RecommendationsPage(model), scale, touch: touch),
          );
          await tester.pumpAndSettle();
          await tester.ensureVisible(find.text('推荐偏好'));
          await tester.pumpAndSettle();
          await tester.tap(find.text('推荐偏好'));
          await tester.pumpAndSettle();
          final setting = find.byWidgetPredicate(
            (widget) =>
                widget is IntegerSetting && widget.title == _longSettingName,
          );
          await _revealRecommendation(tester, setting);
          final slider = find.descendant(
            of: setting,
            matching: find.byType(FSlider),
          );
          expect(slider, findsOneWidget);
          expect(
            find.descendant(of: setting, matching: find.byType(EditableText)),
            findsNothing,
          );
          expect(
            find.descendant(of: setting, matching: find.text('保存')),
            findsNothing,
          );
          final bounds = tester.getRect(setting);
          final title = tester.getRect(
            find.descendant(of: setting, matching: find.text(_longSettingName)),
          );
          final track = tester.getRect(slider);
          final control = tester.getRect(
            find.descendant(
              of: setting,
              matching: find.byKey(const ValueKey('setting-control-area')),
            ),
          );
          expect(title.left, greaterThanOrEqualTo(bounds.left));
          expect(title.right, lessThanOrEqualTo(bounds.right + 1));
          expect(track.width, greaterThan(100));
          // 直接比较左右两列的实际矩形，防止窄屏或大字号悄悄回退为上下布局。
          // 控件列允许包含当前值，因此比较整列中心，不要求滑轨中心等于文本基线。
          expect(control.left, greaterThanOrEqualTo(title.right));
          expect((control.right - bounds.right).abs(), lessThanOrEqualTo(1));
          expect(
            (control.center.dy - title.center.dy).abs(),
            lessThanOrEqualTo(1),
          );
          expect(track.left, greaterThanOrEqualTo(control.left));
          expect(track.right, lessThanOrEqualTo(control.right + 1));
          expect(track.bottom, lessThanOrEqualTo(bounds.bottom));
          // 从实际把手开始拖动，先越过手势阈值；FSlider外层还包含范围说明，
          // 不能用包装框中心点击来冒充用户已经修改滑块。
          final before = model.profile['recipeVariantLimitPerBase'] as int;
          final thumb = find
              .descendant(of: slider, matching: find.byType(FFocusedOutline))
              .first;
          final gesture = await tester.startGesture(tester.getCenter(thumb));
          await gesture.moveBy(const Offset(20, 0));
          await tester.pump();
          await gesture.moveTo(
            Offset(track.right - 12, tester.getCenter(thumb).dy),
          );
          await gesture.up();
          await tester.pumpAndSettle();
          expect(
            model.profile['recipeVariantLimitPerBase'],
            greaterThan(before),
          );
          expect(
            model.profile['recipeVariantLimitPerBase'],
            lessThanOrEqualTo(8),
          );
          expect(tester.takeException(), isNull);
          await tester.pumpWidget(const SizedBox());
        });

        testWidgets('内部hint、隐藏标签和空标签工具行等高且保留输入语义：$sizeLabel', (tester) async {
          _setViewport(tester, width);
          const cases =
              <({String key, String name, String? hint, bool showLabel})>[
                (
                  key: 'hint',
                  name: '筛选名称 / 客人',
                  hint: '输入名称或客人',
                  showLabel: false,
                ),
                (key: 'hidden', name: '料理筛选条件', hint: null, showLabel: false),
                // 空字符串不能生成Text('')标签；这里故意保留showLabel默认语义来覆盖该退化。
                (key: 'empty', name: '', hint: '输入条件', showLabel: true),
              ];
          await tester.pumpWidget(
            _themed(
              Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  for (final item in cases)
                    ToolRow(
                      [
                        SizedBox(
                          width: 220,
                          child: StewardField(
                            item.name,
                            key: ValueKey('field-${item.key}'),
                            showLabel: item.showLabel,
                            hint: item.hint,
                          ),
                        ),
                        StewardButton('保存', onPressed: () {}),
                      ],
                      key: ValueKey('row-${item.key}'),
                      alignInputs: true,
                    ),
                ],
              ),
              scale,
              touch: touch,
            ),
          );
          await tester.pumpAndSettle();

          for (final item in cases) {
            final field = find.byKey(ValueKey('field-${item.key}'));
            final row = find.byKey(ValueKey('row-${item.key}'));
            final button = _buttonOf(row);
            final input = _inputOf(field);
            final foruiField = tester.widget<FTextField>(
              find.descendant(of: field, matching: find.byType(FTextField)),
            );
            expect(foruiField.label, isNull);
            expect(
              tester.widget<material.TextField>(input).decoration!.hintText,
              item.hint,
            );
            final before = _expectAligned(tester, field, button);
            if (item.hint != null) {
              final hint = find.descendant(
                of: input,
                matching: find.text(item.hint!),
              );
              expect(hint, findsOneWidget);
              final hintRect = tester.getRect(hint);
              expect(before.input.contains(hintRect.center), isTrue);
              expect(hintRect.top, greaterThanOrEqualTo(before.input.top - 1));
              expect(
                hintRect.bottom,
                lessThanOrEqualTo(before.input.bottom + 1),
              );
            }
            await tester.enterText(_editableOf(field), '味噌');
            await tester.pumpAndSettle();
            final after = _expectAligned(tester, field, button);
            _expectSameOffset(before.input.topLeft, after.input.topLeft);
            _expectSameOffset(before.button.topLeft, after.button.topLeft);
            if (item.name.isNotEmpty) {
              _expectNamedInput(tester, field, item.name, '味噌');
            }
          }
          expect(tester.takeException(), isNull);
          await tester.pumpWidget(const SizedBox());
        });

        testWidgets('评分权重使用右侧滑块且无保存按钮：$sizeLabel', (tester) async {
          _setViewport(tester, width);
          final model = RichPreviewWorkbenchModel();
          // 本用例测量权重编辑器的尺寸；固定预设不可编辑，显式建立自定义模式。
          model.profile = {
            ...model.profile,
            'recommendationSortProfile': {
              ...object(model.profile['recommendationSortProfile']),
              'preset': 'custom',
            },
          };
          addTearDown(model.dispose);
          await tester.pumpWidget(
            _themed(RecommendationsPage(model), scale, touch: touch),
          );
          await tester.pumpAndSettle();
          await tester.ensureVisible(find.text('推荐偏好'));
          await tester.pumpAndSettle();
          await tester.tap(find.text('推荐偏好'));
          await tester.pumpAndSettle();
          final expand = find.text('自定义评分参数');
          await _revealRecommendation(tester, expand);
          final objective = find.byKey(
            const ValueKey<Object?>('foodPreference'),
          );
          final slider = find.descendant(
            of: objective,
            matching: find.byType(FSlider),
          );
          await _revealRecommendation(tester, slider);
          expect(slider, findsOneWidget);
          expect(
            // 方向下拉框内部使用只读 EditableText 显示所选项；禁止的是可输入
            // 数值的文本控件，而不是 Forui 正式选择器的只读展示实现。
            find.descendant(
              of: objective,
              matching: find.byWidgetPredicate(
                (widget) => widget is EditableText && !widget.readOnly,
              ),
            ),
            findsNothing,
          );
          expect(
            find.descendant(of: objective, matching: find.text('保存')),
            findsNothing,
          );
          final bounds = tester.getRect(objective);
          final track = tester.getRect(slider);
          expect(track.left, greaterThanOrEqualTo(bounds.left));
          expect(track.right, lessThanOrEqualTo(bounds.right + 1));
          expect(track.height, greaterThan(0));
          expect(track.bottom, lessThanOrEqualTo(bounds.bottom));
          expect(tester.takeException(), isNull);
          await tester.pumpWidget(const SizedBox());
        });
      }
    }
  }

  testWidgets('390px触屏深色150%邀请筛选保持紧凑，候选区仍有可用高度', (tester) async {
    _setViewport(tester, 390);
    tester.view.physicalSize = const Size(390, 920);
    final model = FixtureModel(desktop: false)
      ..localPreferences = {
        'fontScalePercent': 150,
        'darkMode': true,
        'reduceMotion': true,
        'rareGuestInvitationModuleEnabled': true,
      };
    addTearDown(model.dispose);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('扩展').last);
    await tester.pumpAndSettle();
    await tester.tap(
      find.descendant(
        of: find.byType(WorkspaceTabs<String>),
        matching: find.text('稀客邀请'),
      ),
    );
    await tester.pumpAndSettle();
    final levels = find.descendant(
      of: find.byType(ToolsPage),
      matching: find.byType(FCheckbox),
    );
    expect(levels, findsNWidgets(6));
    // 六个短标签不能各占一整行；在真实应用壳的剩余空间内仍给列表保留浏览区域。
    final rows = <int>{};
    for (var index = 0; index < 6; index++) {
      final rect = tester.getRect(levels.at(index));
      expect(rect.width, lessThan(100));
      rows.add(rect.top.round());
    }
    expect(rows.length, lessThanOrEqualTo(2));
    final candidates = find.descendant(
      of: find.byType(ToolsPage),
      matching: find.byKey(const ValueKey('invitation-items')),
    );
    expect(tester.getSize(candidates).height, greaterThan(100));
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
}
