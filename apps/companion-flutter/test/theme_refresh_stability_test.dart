import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/ui/steward_controls.dart';
import 'package:mystia_steward_companion/ui/steward_opacity.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 真正的壳层会在模型通知时重新构造主题，不能用固定FThemeData的局部页面夹具
/// 代替本回归。全部数据和通知来自内存，不启动平台窗口、游戏或网络连接。
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

  test('等值主题扩展与整主题相等，真实颜色、透明度和密度变化仍不相等', () {
    for (final touch in [false, true]) {
      for (final dark in [false, true]) {
        for (final opacity in [1.0, .65]) {
          final first = stewardTheme(
            dark: dark,
            touch: touch,
            backgroundOpacity: opacity,
            contentOpacity: .8,
          );
          final second = stewardTheme(
            dark: dark,
            touch: touch,
            backgroundOpacity: opacity,
            contentOpacity: .8,
          );
          expect(identical(first, second), isFalse);
          expect(first, second, reason: '同值重建不得启动FTheme的隐式插值');
          // FTheme的插值判定使用operator ==。锁定Forui的FVariants.hashCode直接
          // 散列MapEntry实例，与其mapEquals实现不一致，因此整主题hash并不是本修复
          // 可保证的契约；分别验证本项目两个扩展的值散列，保留实际动画回归。
          expect(
            first.extension<StewardPaint>().hashCode,
            second.extension<StewardPaint>().hashCode,
          );
          expect(
            first.extension<StewardControlDensity>().hashCode,
            second.extension<StewardControlDensity>().hashCode,
          );
          expect(
            first,
            isNot(
              stewardTheme(
                dark: !dark,
                touch: touch,
                backgroundOpacity: opacity,
                contentOpacity: .8,
              ),
            ),
          );
          expect(
            first,
            isNot(
              stewardTheme(
                dark: dark,
                touch: !touch,
                backgroundOpacity: opacity,
                contentOpacity: .8,
              ),
            ),
          );
          expect(
            first,
            isNot(
              stewardTheme(
                dark: dark,
                touch: touch,
                backgroundOpacity: opacity,
                contentOpacity: .9,
              ),
            ),
          );
          expect(
            first,
            isNot(
              stewardTheme(
                dark: dark,
                touch: touch,
                backgroundOpacity: .7,
                contentOpacity: .8,
              ),
            ),
          );
        }
      }
    }
    expect(
      stewardTheme(dark: false, touch: false),
      stewardTheme(dark: false, touch: false),
      reason: '默认不透明路径同样不能因新密度实例而反复重启主题动画',
    );
    expect({
      const StewardPaint(backgroundOpacity: .6, contentOpacity: .8),
      StewardPaint(backgroundOpacity: .6, contentOpacity: .8),
    }, hasLength(1));
    expect({
      const StewardControlDensity(touch: true),
      StewardControlDensity(touch: true),
    }, hasLength(1));
  });

  Finder checkboxWithin(String key) => find.descendant(
    of: find.byKey(ValueKey(key)),
    matching: find.byType(FCheckbox),
  );

  Future<void> tap(WidgetTester tester, Finder target) async {
    await tester.ensureVisible(target);
    await tester.pumpAndSettle();
    await tester.tap(target);
    await tester.pumpAndSettle();
  }

  /// 不只断言业务value未变：连续通知间隔短于主题200ms动画，在每一帧检查真实
  /// checkbox的外观key和FadeTransition。若被主题反复切换，会出现额外淡出子树
  /// 或opacity小于1，即使最终pumpAndSettle后的截图看起来正常也必须失败。
  Future<void> unchangedAcrossRefreshes(
    WidgetTester tester,
    FixtureModel model,
    Finder checkbox,
  ) async {
    final switcher = find.descendant(
      of: checkbox,
      matching: find.byType(AnimatedSwitcher),
    );
    expect(switcher, findsOneWidget);
    final key = tester.widget<AnimatedSwitcher>(switcher).child!.key;
    final selected = tester.widget<FCheckbox>(checkbox).value;
    for (var tick = 0; tick < 6; tick++) {
      model.publish();
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 37));
      expect(tester.widget<FCheckbox>(checkbox).value, selected);
      expect(tester.widget<AnimatedSwitcher>(switcher).child!.key, key);
      final fades = find.descendant(
        of: switcher,
        matching: find.byType(FadeTransition),
      );
      expect(fades, findsOneWidget, reason: '等值刷新不得生成checkbox淡出副本');
      expect(tester.widget<FadeTransition>(fades).opacity.value, 1);
    }
  }

  for (final desktop in [true, false]) {
    testWidgets('${desktop ? '桌面透明' : '触屏默认'}主题刷新不闪烁，真实选择与禁用仍生效', (
      tester,
    ) async {
      final model = FixtureModel(desktop: desktop, orderCount: 0);
      model.localPreferences = {
        'lastPage': 3,
        'fontScalePercent': 150,
        'reduceMotion': false,
        'rareGuestInvitationModuleEnabled': true,
        'inventoryEditorModuleEnabled': true,
        'backgroundOpacityPercent': 65,
        'contentOpacityPercent': 80,
      };
      model.resources['/rare-guests/invitations'] = {
        'status': 'ready',
        'candidates': <Object?>[],
      };
      addTearDown(model.dispose);
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = Size(desktop ? 800 : 390, 900);
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      await tester.runAsync(() => rootBundle.loadString('pubspec.yaml'));
      await tester.pumpWidget(
        ProviderScope(
          overrides: [workbenchProvider.overrideWithValue(model)],
          child: const StewardApp(),
        ),
      );
      final version = tester.widget<FutureBuilder<String>>(
        find.descendant(
          of: find.byKey(const ValueKey('workbench-status-bar')),
          matching: find.byType(FutureBuilder<String>),
        ),
      );
      await tester.runAsync(() => version.future!);
      await tester.pumpAndSettle();
      await tap(tester, find.text('稀客邀请').first);
      final level = checkboxWithin('invitation-level-0');
      expect(tester.widget<FCheckbox>(level).value, isTrue);
      await unchangedAcrossRefreshes(tester, model, level);
      await tap(tester, level);
      expect(tester.widget<FCheckbox>(level).value, isFalse);
      await unchangedAcrossRefreshes(tester, model, level);

      await tap(tester, find.text('库存修改').first);
      final inventory = checkboxWithin('inventory-item-ingredient-2');
      await unchangedAcrossRefreshes(tester, model, inventory);
      await tap(tester, inventory);
      expect(tester.widget<FCheckbox>(inventory).value, isTrue);
      expect(find.text('已选 1 项'), findsOneWidget);
      await unchangedAcrossRefreshes(tester, model, inventory);

      // 等值通知静默不等于冻结控件：真实等待状态必须继续撤下操作回调，结束后
      // 保留已选项并允许用户取消；本回归不会确认或发出任何库存写入。
      model.busy = true;
      model.publish();
      await tester.pumpAndSettle();
      expect(tester.widget<FCheckbox>(inventory).enabled, isFalse);
      expect(tester.widget<FCheckbox>(inventory).onChange, isNull);
      expect(tester.widget<FCheckbox>(inventory).value, isTrue);
      model.busy = false;
      model.publish();
      await tester.pumpAndSettle();
      expect(tester.widget<FCheckbox>(inventory).enabled, isTrue);

      // 深浅色与非默认透明度改变必须传到实际继承主题，证明值相等没有压制
      // 合法更新。手机透明度仍按产品规则固定1，桌面才使用用户配置。
      model.localPreferences = {
        ...model.localPreferences,
        'darkMode': true,
        'backgroundOpacityPercent': 80,
        'contentOpacityPercent': 65,
      };
      model.publish();
      await tester.pumpAndSettle();
      final context = tester.element(inventory);
      expect(context.theme.colors.background.computeLuminance(), lessThan(.3));
      expect(context.stewardPaint.backgroundOpacity, desktop ? .8 : 1);
      expect(context.stewardPaint.contentOpacity, desktop ? .65 : 1);
      expect(tester.widget<FCheckbox>(inventory).value, isTrue);
      await unchangedAcrossRefreshes(tester, model, inventory);
      await tap(tester, inventory);
      expect(tester.widget<FCheckbox>(inventory).value, isFalse);
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }
}
