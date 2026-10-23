import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/automation_module.dart';
import 'package:mystia_steward_companion/features/game_ui_module.dart';
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 仅记录界面实际提交的共享补丁，不连接 HTTP，也不让测试替代 C# 的执行门禁。
class _ConfigurationModel extends FixtureModel {
  final changes = <DataMap>[];
  final localChanges = <DataMap>[];
  @override
  Future<void> updateProfile(DataMap changes) async {
    this.changes.add(Map.of(changes));
    await super.updateProfile(changes);
  }

  @override
  Future<void> updateLocal(DataMap changes) async {
    localChanges.add(Map.of(changes));
    await super.updateLocal(changes);
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
  });

  Future<void> open(
    WidgetTester tester,
    _ConfigurationModel model, {
    required bool gameUi,
    double width = 1280,
    double scale = 1,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(model.dispose);
    final theme = stewardTheme(dark: false, touch: width < 640);
    await tester.pumpWidget(
      material.MaterialApp(
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
        // 固定侧栏也占据手机窗口宽度；必须按产品剩余宽度测试，而非把全屏都让给表单。
        home: Row(
          children: [
            const SizedBox(width: 80),
            Expanded(
              child: Padding(
                padding: const EdgeInsets.all(12),
                child: ListenableBuilder(
                  listenable: model,
                  builder: (context, _) =>
                      gameUi ? GameUiModule(model) : AutomationModule(model),
                ),
              ),
            ),
          ],
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Finder toggle(String key) => find.descendant(
    of: find.byKey(ValueKey(key)),
    matching: find.byType(FSwitch),
  );

  /// 每次从确定的滚动起点寻找懒构建表单，最终必须执行真实命中测试的点击。
  Future<void> reveal(WidgetTester tester, Finder target) async {
    final scroll = find.byType(Scrollable).first;
    tester.state<ScrollableState>(scroll).position.jumpTo(0);
    await tester.pump();
    await tester.scrollUntilVisible(target, 200, scrollable: scroll);
    await tester.ensureVisible(target);
    await tester.pumpAndSettle();
  }

  Future<void> setToggle(WidgetTester tester, String key) async {
    final target = toggle(key);
    await reveal(tester, target);
    await tester.tap(target);
    await tester.pumpAndSettle();
  }

  Future<void> saveNumber(WidgetTester tester, String key, String value) async {
    final owner = find.byKey(ValueKey(key));
    await reveal(tester, owner);
    final slider = find.descendant(of: owner, matching: find.byType(FSlider));
    await tester.ensureVisible(slider);
    final outline = find
        .descendant(of: slider, matching: find.byType(FFocusedOutline))
        .first;
    Focus.of(tester.element(outline)).requestFocus();
    await tester.pump();
    // Home 到合法最小值，再用真实方向键精确调整；最大值之外不能产生写入。
    await tester.sendKeyEvent(LogicalKeyboardKey.home);
    await tester.pump();
    final minimum = tester.widget<IntegerSetting>(owner).min;
    for (var index = minimum; index < int.parse(value); index++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.pump();
    }
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pumpAndSettle();
  }

  testWidgets('自动化总开关只保存共享配置，关闭后保留两类步骤与并发', (tester) async {
    final model = _ConfigurationModel();
    model.profile = {...model.profile, 'automationEnabled': true};
    final before = Map.of(model.profile);
    await open(tester, model, gameUi: false);
    await setToggle(tester, 'automationEnabled');
    expect(model.changes.single, {'automationEnabled': false});
    expect(model.profile, {...before, 'automationEnabled': false});
    expect(model.localChanges, isEmpty);
    expect(model.writes, isEmpty);
  });

  testWidgets('全部自动化步骤保留原共享字段，关闭总开关仍可配置偏好', (tester) async {
    final model = _ConfigurationModel();
    model.profile = {...model.profile, 'automationEnabled': false};
    await open(tester, model, gameUi: false);
    for (final key in [
      'autoNormalOrderEnabled',
      'autoNormalStartCooking',
      'autoNormalCompleteOrder',
      'autoNormalTakeBeverage',
      'autoNormalDeliverFood',
      'autoNormalStopOnError',
      'autoRareOrderEnabled',
      'autoPrepStartCooking',
      'autoPrepCompleteOrder',
      'autoPrepTakeBeverage',
      'autoPrepCollectCooking',
      'autoPrepRecipeFavoritesOnly',
      'autoPrepBeverageFavoritesOnly',
      'autoPrepStopOnError',
    ]) {
      final expected = model.profile[key] != true;
      await setToggle(tester, key);
      expect(model.changes.last, {key: expected});
      expect(model.profile['automationEnabled'], isFalse);
    }
  });

  testWidgets('失败与重新安排上限仍同步原共享字段，两个并发数保持独立且验证各自边界', (tester) async {
    final model = _ConfigurationModel();
    await open(tester, model, gameUi: false);
    await saveNumber(tester, 'normal-autoMaxStepRetries', '6');
    expect(model.changes.last, {'autoMaxStepRetries': 6});
    expect(
      tester
          .widget<IntegerSetting>(
            find.byKey(const ValueKey('normal-autoMaxStepRetries')),
          )
          .title,
      '同一步骤失败上限（共享）',
    );
    await reveal(tester, find.byKey(const ValueKey('rare-autoMaxStepRetries')));
    expect(
      tester
          .widget<IntegerSetting>(
            find.byKey(const ValueKey('rare-autoMaxStepRetries')),
          )
          .value,
      6,
    );
    await saveNumber(tester, 'rare-autoMaxStepRetries', '4');
    await reveal(
      tester,
      find.byKey(const ValueKey('normal-autoMaxStepRetries')),
    );
    expect(
      tester
          .widget<IntegerSetting>(
            find.byKey(const ValueKey('normal-autoMaxStepRetries')),
          )
          .value,
      4,
    );
    await saveNumber(tester, 'normal-autoNormalConcurrency', '6');
    await saveNumber(tester, 'rare-autoRareConcurrency', '4');
    expect(model.profile['autoNormalConcurrency'], 6);
    expect(model.profile['autoRareConcurrency'], 4);
    final previousCount = model.changes.length;
    await saveNumber(tester, 'rare-autoRareConcurrency', '5');
    expect(model.changes.length, previousCount);
    expect(model.profile['autoRareConcurrency'], 4);
    // 最大值仍由上面的真实方向键路径验证，只删除重复占高的范围/教程文字。
    expect(find.text('1–4 单 · 步进 1 单 · 方向键精调'), findsNothing);
    await saveNumber(tester, 'rare-autoMaxRollbacks', '0');
    expect(model.changes.last, {'autoMaxRollbacks': 0});
    await reveal(tester, find.byKey(const ValueKey('normal-autoMaxRollbacks')));
    expect(
      tester
          .widget<IntegerSetting>(
            find.byKey(const ValueKey('normal-autoMaxRollbacks')),
          )
          .value,
      0,
    );
    expect(
      tester
          .widget<IntegerSetting>(
            find.byKey(const ValueKey('normal-autoMaxRollbacks')),
          )
          .title,
      '重新安排次数上限（共享）',
    );
  });

  testWidgets('游戏辅助总开关独立共享，关闭不清空分项并仍可配置全部十项偏好', (tester) async {
    final model = _ConfigurationModel();
    model.profile = {...model.profile, 'gameUiEnabled': true};
    final before = Map.of(model.profile);
    await open(tester, model, gameUi: true);
    await setToggle(tester, 'gameUiEnabled');
    expect(model.changes.single, {'gameUiEnabled': false});
    expect(model.profile, {...before, 'gameUiEnabled': false});
    for (final prefix in ['normal', 'rare']) {
      for (final suffix in [
        'GameUiPinningEnabled',
        'RecipeVariantEnabled',
        'CookerHighlightEnabled',
        'SeatHighlightEnabled',
        'OrderHighlightEnabled',
      ]) {
        final key = '$prefix$suffix';
        final expected = model.profile[key] != true;
        await setToggle(tester, key);
        expect(model.changes.last, {key: expected});
        expect(model.profile['gameUiEnabled'], isFalse);
      }
    }
    expect(model.localChanges, isEmpty);
    expect(model.writes, isEmpty);
  });

  for (final gameUi in [false, true]) {
    testWidgets('${gameUi ? '游戏辅助' : '自动化'}查看设备、断线与忙碌时不可写共享配置', (tester) async {
      final model = _ConfigurationModel();
      model.authority = {'currentDeviceIsPrimary': false};
      await open(tester, model, gameUi: gameUi);
      for (final state in ['observer', 'disconnected', 'busy']) {
        model.authority = {'currentDeviceIsPrimary': state != 'observer'};
        model.connected = state != 'disconnected';
        model.busy = state == 'busy';
        model.publish();
        await tester.pumpAndSettle();
        final master = toggle(gameUi ? 'gameUiEnabled' : 'automationEnabled');
        await reveal(tester, master);
        expect(tester.widget<FSwitch>(master).enabled, isFalse);
        await tester.tap(master);
        await tester.pumpAndSettle();
        if (gameUi) {
          await reveal(
            tester,
            find.byKey(const ValueKey('normal-highlight-color')),
          );
          expect(
            tester
                .widget<ColorSetting>(
                  find.byKey(const ValueKey('normal-highlight-color')),
                )
                .onSave,
            isNull,
          );
        } else {
          await reveal(
            tester,
            find.byKey(const ValueKey('normal-autoNormalConcurrency')),
          );
          expect(
            tester
                .widget<IntegerSetting>(
                  find.byKey(const ValueKey('normal-autoNormalConcurrency')),
                )
                .onSave,
            isNull,
          );
        }
      }
      expect(model.changes, isEmpty);
    });
  }

  for (final width in [1280.0, 640.0, 390.0]) {
    for (final gameUi in [false, true]) {
      testWidgets(
        '${gameUi ? '游戏辅助' : '自动化'} ${width.toInt()}px／150%可滚动至底部并真实保存',
        (tester) async {
          final model = _ConfigurationModel();
          await open(tester, model, gameUi: gameUi, width: width, scale: 1.5);
          if (gameUi) {
            final owner = find.byKey(const ValueKey('rare-highlight-color'));
            await reveal(tester, owner);
            expect(
              find.descendant(of: owner, matching: find.byType(EditableText)),
              findsNothing,
            );
            // 辅助模块真实 Section 内的颜色入口必须贴齐上一条开关，不能用
            // 全屏空白夹具掩盖 390px 导航、边距和 150% 字号实际占用的宽度。
            final switchRect = tester.getRect(
              toggle('rareOrderHighlightEnabled'),
            );
            final opener = find.descendant(
              of: owner,
              matching: find.byKey(const ValueKey('color-picker-open')),
            );
            expect(tester.getRect(opener).right, closeTo(switchRect.right, .1));
            final titleRect = tester.getRect(
              find.descendant(
                of: owner,
                matching: find.byKey(const ValueKey('setting-control-label')),
              ),
            );
            expect(titleRect.right, lessThan(tester.getRect(opener).left));
            final choice = find.descendant(
              of: owner,
              matching: find.byKey(const ValueKey('palette-#F97373')),
            );
            await tester.ensureVisible(choice);
            await tester.tap(choice);
            await tester.pumpAndSettle();
            expect(model.changes.last, {'rareTargetHighlightColor': '#F97373'});
          } else {
            await saveNumber(tester, 'rare-autoMaxRollbacks', '5');
            expect(model.changes.last, {'autoMaxRollbacks': 5});
            final saves = model.changes.length;
            // 页面只保留两项关键短提示；它们仍完整换行，不挤压滑块，也不通过
            // maxLines裁切假装简洁。详细恢复规则已归帮助，阅读不产生配置写入。
            for (final entry in const {
              'rare-step-failure-help': '启用出错暂停时生效；首次失败计入。',
              'rare-replanning-help': '达到上限即暂停；0 表示不自动重新安排。',
            }.entries) {
              final help = find.byKey(ValueKey(entry.key));
              await reveal(tester, help);
              final text = tester.widget<Text>(help);
              expect(text.data, entry.value);
              expect(text.maxLines, isNull);
              final rect = tester.getRect(help);
              expect(rect.left, greaterThanOrEqualTo(80));
              expect(rect.right, lessThanOrEqualTo(width - 12));
            }
            expect(
              find.byKey(const ValueKey('rare-recovery-help')),
              findsNothing,
            );
            expect(model.changes.length, saves);
          }
          expect(tester.takeException(), isNull);
        },
      );
    }
  }
}
