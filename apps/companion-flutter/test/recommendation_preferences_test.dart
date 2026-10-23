import 'dart:async';

import 'package:companion_protocol/companion_protocol.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/features/recommendation_preferences.dart';
import 'package:mystia_steward_companion/features/recommendations.dart';
import 'package:mystia_steward_companion/features/settings.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/page_activity_assertions.dart';
import 'support/steward_test_fonts.dart';

/// 只延迟纯内存 profile 回传，不伪造 busy 通知，暴露控件自身的同帧双击门闩。
/// 实际 HTTP 串行出队/ACK 另由 live_http_contract_test 覆盖。
class _PreferencesModel extends FixtureModel {
  _PreferencesModel({super.desktop});
  Completer<void>? gate;
  bool fail = false;
  int saves = 0;

  @override
  Future<void> updateProfile(DataMap changes) async {
    saves++;
    await gate?.future;
    if (fail) {
      error = '合成保存失败';
      publish();
      return;
    }
    // 夹具也执行正式 schema 验证，不允许错误的模式/数值组合被浅合并掩盖。
    profile = CompanionProfile({...profile, ...changes}).json;
    publish();
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(loadPreferredStewardTestFont);
  final modeFinder = find.byKey(const ValueKey('recommendation-sort-mode'));
  // 行键来自 DataMap['key']，其静态类型是 Object?。Flutter ValueKey 的相等判断
  // 同时比较泛型运行时类型；ValueKey<String> 即使文本相同也无法命中真实行。
  // 集中复用生产键类型，不能把不存在的目标不断向下滚动误判为表单没有生成。
  final foodObjective = find.byKey(const ValueKey<Object?>('foodPreference'));

  Future<void> mount(
    WidgetTester tester,
    _PreferencesModel model, {
    double width = 1280,
    double scale = 1,
    bool active = true,
    Widget? page,
    bool disposeModel = true,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 900);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    if (disposeModel) addTearDown(model.dispose);
    final theme = stewardTheme(dark: false, touch: !model.desktop);
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
          // Windows 保留窄导航与内容边距，Android 使用实际 16px 正文边距。
          padding: EdgeInsets.fromLTRB(
            model.desktop ? 104 : 16,
            12,
            model.desktop ? 24 : 16,
            12,
          ),
          child: page ?? RecommendationPreferencesPanel(model, active: active),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> reveal(WidgetTester tester, Finder target) async {
    final form = find
        .descendant(
          of: find.byKey(const PageStorageKey('recommendation-preferences')),
          matching: find.byType(Scrollable),
        )
        .first;
    await tester.scrollUntilVisible(target, 180, scrollable: form);
    await tester.ensureVisible(target);
    await tester.pumpAndSettle();
  }

  Future<void> choose(WidgetTester tester, String title) async {
    await reveal(tester, modeFinder);
    final target = find.descendant(of: modeFinder, matching: find.text(title));
    await tester.ensureVisible(target);
    await tester.pumpAndSettle();
    await tester.tap(target);
    await tester.pumpAndSettle();
  }

  String mode(WidgetTester tester) =>
      tester.widget<WorkspaceTabs<String>>(modeFinder).value;

  testWidgets('推荐工作区承接推荐偏好，设置不再提供重复入口', (tester) async {
    final model = _PreferencesModel();
    await mount(tester, model, page: RecommendationsPage(model));
    await tester.tap(find.text('推荐偏好'));
    await tester.pumpAndSettle();
    expect(find.byType(RecommendationPreferencesPanel), findsOneWidget);
    expect(model.reads, isEmpty, reason: '推荐偏好只读已同步 profile，不加载收藏等隐藏资源。');
    await mount(tester, model, page: SettingsPage(model), disposeModel: false);
    expect(find.text('推荐'), findsNothing);
    expect(find.text('评分排序'), findsNothing);
  });

  testWidgets('四个固定模式明确选中且没有自由编辑参数', (tester) async {
    final model = _PreferencesModel();
    await mount(tester, model);
    for (final entry in const {
      'balanced': '均衡',
      'resources': '节约资源',
      'profit': '利润',
      'simple': '简单料理',
    }.entries) {
      await choose(tester, entry.value);
      expect(mode(tester), entry.key);
      expect(
        canonicalJson(model.profile['recommendationSortProfile']),
        canonicalJson(CompanionProfile.sortPreset(entry.key)),
      );
      expect(find.text('自定义评分参数'), findsNothing);
      expect(foodObjective, findsNothing);
      expect(tester.takeException(), isNull);
    }
  });

  testWidgets('自定义保留当前值，叶字段自动保存，远端和重建仍以持久模式为准', (tester) async {
    final model = _PreferencesModel();
    await mount(tester, model);
    await choose(tester, '利润');
    final original = canonicalJson(
      object(model.profile['recommendationSortProfile'])['objectives'],
    );
    await choose(tester, '自定义');
    expect(mode(tester), 'custom');
    expect(
      canonicalJson(
        object(model.profile['recommendationSortProfile'])['objectives'],
      ),
      original,
    );
    final row = foodObjective;
    await reveal(tester, row);
    final slider = tester.widget<IntegerSetting>(
      find.descendant(of: row, matching: find.byType(IntegerSetting)),
    );
    await slider.onSave!(29);
    await tester.pumpAndSettle();
    final objectives = records(
      object(model.profile['recommendationSortProfile'])['objectives'],
    );
    expect(objectives.first['weight'], 29);
    expect(
      objectives.singleWhere((entry) => entry['key'] == 'profit')['weight'],
      100,
    );

    await tester.pumpWidget(const SizedBox());
    await mount(tester, model, disposeModel: false);
    await reveal(tester, modeFinder);
    expect(mode(tester), 'custom');
    model.profile = {
      ...model.profile,
      'recommendationSortProfile': CompanionProfile.sortPreset('simple'),
    };
    model.publish();
    await tester.pumpAndSettle();
    expect(mode(tester), 'simple');
    expect(foodObjective, findsNothing);
    final saves = model.saves;
    await slider.onSave!(80);
    expect(model.saves, saves, reason: '已销毁的旧自定义控件回调不能修改远端新模式。');
  });

  testWidgets('保存期间保持权威选中态并阻止同帧重复切换，失败明确反馈', (tester) async {
    final model = _PreferencesModel()
      ..gate = Completer<void>()
      ..fail = true;
    await mount(tester, model);
    await reveal(tester, modeFinder);
    final control = tester.widget<WorkspaceTabs<String>>(modeFinder);
    control.onChanged('profit');
    control.onChanged('simple');
    await tester.pump();
    expect(model.saves, 1);
    expect(mode(tester), 'balanced');
    expect(tester.widget<WorkspaceTabs<String>>(modeFinder).enabled, isFalse);
    model.gate!.complete();
    await tester.pumpAndSettle();
    expect(mode(tester), 'balanced');
    expectReportedFailure(tester, '合成保存失败');
  });

  testWidgets('页面隐藏或失去主设备后旧自定义草稿不得保存', (tester) async {
    final model = _PreferencesModel();
    await model.updateSortMode('custom');
    await mount(tester, model);
    final row = foodObjective;
    await reveal(tester, row);
    final slider = tester.widget<IntegerSetting>(
      find.descendant(of: row, matching: find.byType(IntegerSetting)),
    );
    model.authority = {...model.authority, 'currentDeviceIsPrimary': false};
    model.publish();
    await tester.pumpAndSettle();
    final saves = model.saves;
    await slider.onSave!(99);
    expect(model.saves, saves);
    model.authority = {...model.authority, 'currentDeviceIsPrimary': true};
    await mount(tester, model, active: false, disposeModel: false);
    await slider.onSave!(99);
    expect(model.saves, saves);
  });

  for (final width in [627.0, 390.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('推荐偏好 $width / $scale 模式可达且参数不溢出', (tester) async {
        final model = _PreferencesModel(desktop: width >= 627);
        await mount(tester, model, width: width, scale: scale);
        await choose(tester, '自定义');
        expect(mode(tester), 'custom');
        final row = foodObjective;
        await reveal(tester, row);
        final rect = tester.getRect(row);
        expect(rect.left, greaterThanOrEqualTo(0));
        expect(rect.right, lessThanOrEqualTo(width));
        final control = find.descendant(
          of: row,
          matching: find.byType(IntegerSetting),
        );
        expect(tester.widget<IntegerSetting>(control).onSave, isNotNull);
        expect(tester.takeException(), isNull);
      });
    }
  }
}
