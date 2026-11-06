import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/game_focus_overlay_controls.dart';
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import '../tool/preview/rich_preview_workbench_model.dart';
import 'support/steward_test_fonts.dart';

/// 控件验证采用正式UI与纯内存资源；不打开游戏窗口，不把预览修订当成服务端证据。
class _OverlayModel extends RichPreviewWorkbenchModel {
  _OverlayModel({required super.desktop});
  final writes = <DataMap>[];

  void publishPending() {
    busy = true;
    resources[GameFocusOverlayControls.resourcePath] = {
      ...resources[GameFocusOverlayControls.resourcePath]!,
      'ready': false,
    };
    notifyListeners();
  }

  @override
  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  }) async {
    if (path == GameFocusOverlayControls.resourcePath) {
      writes.add({...?body, 'localOnly': localOnly});
    }
    await super.mutate(path, query: query, body: body, localOnly: localOnly);
  }
}

void main() {
  setUpAll(() async {
    await loadPreferredStewardTestFont();
    await (FontLoader('packages/forui_lucide/ForuiLucideIcons')
          ..addFont(rootBundle.load('packages/forui_lucide/assets/lucide.ttf')))
        .load();
  });
  const path = GameFocusOverlayControls.resourcePath;
  final surfaceKey = GlobalKey();

  Future<void> open(
    WidgetTester tester,
    _OverlayModel model,
    double width,
    double scale, {
    bool active = true,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 1100);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(model.dispose);
    final theme = stewardTheme(dark: false, touch: !model.desktop);
    await tester.pumpWidget(
      material.MaterialApp(
        localizationsDelegates: FLocalizations.localizationsDelegates,
        supportedLocales: FLocalizations.supportedLocales,
        theme: theme.toApproximateMaterialTheme(),
        builder: (context, child) => FTheme(
          data: theme,
          child: MediaQuery(
            data: MediaQuery.of(context)
                .copyWith(textScaler: TextScaler.linear(scale)),
            child: child!,
          ),
        ),
        home: RepaintBoundary(
          key: surfaceKey,
          child: ColoredBox(
            color: theme.colors.background,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: ListenableBuilder(
                listenable: model,
                builder: (_, _) => ListView(
                  children: [GameFocusOverlayControls(model, active: active)],
                ),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Finder toggle(String key) => find.descendant(
    of: find.byKey(ValueKey('game-focus-$key')),
    matching: find.byType(FSwitch),
  );

  for (final width in [390.0, 544.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('游戏内专注设置窄屏与修订写入 $width/$scale', (tester) async {
        final model = _OverlayModel(desktop: width > 500);
        final originalLocal = {...model.localPreferences};
        final originalProfile = {...model.profile};
        await open(tester, model, width, scale);
        await tester.tap(toggle('enabled'));
        await tester.pumpAndSettle();
        expect(model.writes.single, {
          'expectedRevision': 1,
          'changes': {
            'enabled': true,
            'content': gameFocusContentFromPreferences(model.localPreferences),
            'theme': 'light',
          },
          'localOnly': false,
        });
        expect(find.text('游戏背景不透明度'), findsOneWidget);
        // 截图使用与产品同一字体、控件与布局，证据输出由本轮显式目录控制。
        final directory = Platform.environment['MYSTIA_UI_EVIDENCE'];
        if (directory != null) {
          await tester.runAsync(() async {
            final image =
                await (surfaceKey.currentContext!.findRenderObject()!
                        as RenderRepaintBoundary)
                    .toImage();
            final bytes = await image.toByteData(
              format: ui.ImageByteFormat.png,
            );
            await Directory(directory).create(recursive: true);
            await File('$directory/game-focus-$width-$scale.png')
                .writeAsBytes(bytes!.buffer.asUint8List());
            image.dispose();
          });
        }
        expect(find.text('编辑布局'), findsNothing);
        expect(find.text('完成调整'), findsNothing);
        expect(find.text('重置布局'), findsOneWidget);
        final slider = tester.widget<IntegerSetting>(
          find.byKey(const ValueKey('game-focus-backgroundOpacity')),
        );
        await slider.onSave!(35);
        await tester.pumpAndSettle();
        expect(model.writes.last['expectedRevision'], 2);
        expect(object(model.writes.last['changes']), {'backgroundOpacity': 35});
        Future<void> adjust(String field, int value) async {
          final control = tester.widget<IntegerSetting>(
            find.byKey(ValueKey('game-focus-$field')),
          );
          await control.onSave!(value);
          await tester.pumpAndSettle();
        }

        await adjust('x', 100);
        expect(
          object(object(model.writes.last['changes'])['layout'])['x'],
          closeTo(.72, .00001),
        );
        await adjust('width', 50);
        expect(object(object(model.writes.last['changes'])['layout']), {
          'x': .5,
          'y': .12,
          'width': .5,
          'height': .72,
        });
        await adjust('y', 100);
        await adjust('height', 90);
        final layout = object(object(model.writes.last['changes'])['layout']);
        expect(layout['y'], closeTo(.1, .00001));
        expect(layout['height'], .9);
        await adjust('scrollPercent', 65);
        expect(object(model.writes.last['changes']), {'scrollPercent': 65});
        final reset = tester.widget<StewardButton>(
          find.byKey(const ValueKey('game-focus-reset')),
        );
        reset.onPressed!();
        await tester.pumpAndSettle();
        expect(object(model.writes.last['changes']), {'resetLayout': true});
        expect(object(model.resources[path]!['settings'])['scrollPercent'], 0);
        expect(object(object(model.resources[path]!['settings'])['layout']), {
          'x': .7,
          'y': .12,
          'width': .28,
          'height': .72,
        });

        expect(model.localPreferences, originalLocal);
        expect(model.profile, originalProfile);
        await tester.tap(toggle('enabled'));
        await tester.pumpAndSettle();
        expect(object(model.resources[path]!['settings'])['enabled'], isFalse);
        expect(find.text('游戏背景不透明度'), findsNothing);
        expect(tester.takeException(), isNull);
      });
    }
  }

  for (final reason in ['viewer', 'missing', 'inactive']) {
    testWidgets('游戏内设置在$reason时不可写', (tester) async {
      final model = _OverlayModel(desktop: false);
      if (reason == 'viewer')
        model.authority = {...model.authority, 'currentDeviceIsPrimary': false};
      if (reason == 'missing') model.resources.remove(path);
      await open(tester, model, 390, 1.5, active: reason != 'inactive');
      expect(
        tester
            .widget<SettingToggle>(
              find.byKey(const ValueKey('game-focus-enabled')),
            )
            .onChanged,
        isNull,
      );
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('保存等待只禁用原控件，不移动或卸载已展开设置', (tester) async {
    final model = _OverlayModel(desktop: false);
    model.resources[path]!['settings'] = {
      ...object(model.resources[path]!['settings']),
      'enabled': true,
    };
    await open(tester, model, 390, 1.5);
    final slider = find.byKey(const ValueKey('game-focus-backgroundOpacity'));
    final before = tester.getRect(slider);
    final sliderState = tester.state(slider);
    model.publishPending();
    await tester.pumpAndSettle();
    expect(tester.getRect(slider), before);
    expect(tester.state(slider), same(sliderState));
    expect(tester.widget<IntegerSetting>(slider).onSave, isNull);
    expect(find.text('显示设置尚未确认'), findsNothing);
    expect(tester.takeException(), isNull);
  });

  for (final canReset in [true, false]) {
    testWidgets('仅完整磁盘损坏响应允许显式恢复：$canReset', (tester) async {
      final model = _OverlayModel(desktop: false);
      model.resources[path] = {
        ...model.resources[path]!,
        'ready': false,
        'canReset': canReset,
      };
      await open(tester, model, 390, 1.5);
      expect(
        tester
            .widget<SettingToggle>(
              find.byKey(const ValueKey('game-focus-enabled')),
            )
            .onChanged,
        isNull,
      );
      if (canReset) {
        await tester.tap(find.text('恢复默认设置'));
        await tester.pumpAndSettle();
        expect(object(model.writes.single['changes']), {'resetLayout': true});
        expect(gameFocusOverlayConfirmed(model.resources[path]!), isTrue);
        expect(find.text('恢复默认设置'), findsNothing);
      } else {
        expect(find.text('恢复默认设置'), findsNothing);
        expect(model.writes, isEmpty);
      }
      expect(tester.takeException(), isNull);
    });
  }

  test('主设备本机内容与主题镜像，查看设备只改自己', () async {
    final model = _OverlayModel(desktop: true);
    addTearDown(model.dispose);
    await model.updateLocal({
      'serviceFocusShowFood': false,
      'themeMode': 'dark',
    });
    expect(
      object(object(model.resources[path]!['settings'])['content'])['showFood'],
      false,
    );
    expect(object(model.resources[path]!['settings'])['theme'], 'dark');
    final before = {...object(model.resources[path]!['settings'])};
    model.authority = {...model.authority, 'currentDeviceIsPrimary': false};
    await model.updateLocal({
      'serviceFocusShowFood': true,
      'themeMode': 'light',
    });
    expect(model.localPreferences['serviceFocusShowFood'], true);
    expect(object(model.resources[path]!['settings']), before);
  });

  test('完整配置确认拒绝缺字段、类型错误与越界', () {
    final model = _OverlayModel(desktop: true);
    addTearDown(model.dispose);
    final state = model.resources[path]!;
    expect(gameFocusOverlayConfirmed(state), isTrue);
    expect(gameFocusOverlayConfirmed({...state, 'revision': 0}), isFalse);
    for (final value in [19, 101, '100', null]) {
      expect(
        gameFocusOverlayConfirmed({
          ...state,
          'settings': {...object(state['settings']), 'contentOpacity': value},
        }),
        isFalse,
      );
    }
    expect(gameFocusOverlayConfirmed({...state, 'settings': {}}), isFalse);
    for (final width in [0.0, -.1, double.nan]) {
      expect(
        gameFocusOverlayConfirmed({
          ...state,
          'settings': {
            ...object(state['settings']),
            'layout': {
              ...object(object(state['settings'])['layout']),
              'width': width,
            },
          },
        }),
        isFalse,
      );
    }
    expect(
      gameFocusOverlayConfirmed({
        ...state,
        'settings': {
          ...object(state['settings']),
          'layout': {'x': 0.0, 'y': 0.0, 'width': .034, 'height': .05},
        },
      }),
      isTrue,
      reason: '旧高分辨率布局小于10%仍应可读，UI仅钳制滑块显示。',
    );
  });
}
