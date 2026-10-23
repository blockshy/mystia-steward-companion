import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/update_notice.dart';

import 'support/fixture_model.dart';

/// 全部状态来自内存，不发起更新检查、打开外部站点或连接真实游戏。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  test('更新暂缓仅命中同版本同设备，安装错误和到期提醒不被遮蔽', () {
    final model = FixtureModel();
    addTearDown(model.dispose);
    model.resources['/updates/status'] = {
      'hasUpdate': true,
      'currentVersion': '1.3.1',
      'latestVersion': '1.4.0',
    };
    final now = DateTime.utc(2026, 10, 23);
    final scope = UpdateReminder.read(model, now: now)!.scope;
    model.localPreferences['updateReminderDeferred'] = {
      'scope': scope,
      'untilUtc': now.add(const Duration(hours: 24)).toIso8601String(),
    };
    expect(UpdateReminder.read(model, now: now), isNull);
    expect(
      UpdateReminder.read(model, now: now.add(const Duration(hours: 24))),
      isNotNull,
    );
    model.resources['/updates/status']!['latestVersion'] = '1.4.1';
    expect(UpdateReminder.read(model, now: now), isNotNull);
    model.resources['/updates/status']!['latestVersion'] = '1.4.0';
    model.resources['/updates/status']!['installState'] = 'failed';
    expect(UpdateReminder.read(model, now: now)!.failed, isTrue);
    model.connected = false;
    expect(UpdateReminder.read(model, now: now), isNull);
  });

  test('三态主题保留旧明确偏好，system不受旧darkMode干扰', () {
    expect(workbenchThemeMode({'darkMode': true}), 'dark');
    expect(workbenchThemeMode({'darkMode': false}), 'light');
    expect(workbenchThemeMode({}), 'system');
    expect(
      workbenchDark({
        'themeMode': 'system',
        'darkMode': false,
      }, Brightness.dark),
      isTrue,
    );
    expect(workbenchDark({'themeMode': 'light'}, Brightness.dark), isFalse);
  });

  for (final desktop in [true, false]) {
    testWidgets('${desktop ? 'Windows640' : 'Android390'}更新提醒不增高页头，可直达更新及暂缓', (
      tester,
    ) async {
      tester.view.physicalSize = Size(desktop ? 640 : 390, 900);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final model = FixtureModel(desktop: desktop);
      model.localPreferences = {'fontScalePercent': 150, 'reduceMotion': true};
      addTearDown(model.dispose);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [workbenchProvider.overrideWithValue(model)],
          child: const StewardApp(),
        ),
      );
      await tester.pumpAndSettle();
      final before = tester.getRect(
        find.byKey(const ValueKey('workbench-page-title')),
      );
      model.resources['/updates/status'] = {
        'hasUpdate': true,
        'currentVersion': '1.3.1',
        'latestVersion': '1.4.0',
      };
      model.publish();
      await tester.pumpAndSettle();
      expect(
        tester.getRect(find.byKey(const ValueKey('workbench-page-title'))).top,
        before.top,
      );
      expect(tester.takeException(), isNull);
      await tester.tap(find.byKey(const ValueKey('global-update-notice')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('24小时后提醒'));
      await tester.pumpAndSettle();
      expect(find.byKey(const ValueKey('global-update-notice')), findsNothing);
      model.resources['/updates/status']!['latestVersion'] = '1.4.1';
      model.publish();
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('global-update-notice')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('查看更新'));
      await tester.pumpAndSettle();
      expect(find.text('检查更新'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
    });
  }

  testWidgets('跟随系统主题在没有模型通知时也更新', (tester) async {
    final model = FixtureModel()..localPreferences = {'themeMode': 'system'};
    addTearDown(model.dispose);
    addTearDown(tester.platformDispatcher.clearPlatformBrightnessTestValue);
    tester.platformDispatcher.platformBrightnessTestValue = Brightness.light;
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pumpAndSettle();
    expect(model.dark, isFalse);
    tester.platformDispatcher.platformBrightnessTestValue = Brightness.dark;
    await tester.pumpAndSettle();
    expect(model.dark, isTrue);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
  });
}
