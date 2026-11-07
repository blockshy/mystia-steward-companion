import 'dart:async';

import 'package:companion_platform/companion_platform.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/features/device_name_editor.dart';
import 'package:mystia_steward_companion/features/settings.dart';
import 'package:mystia_steward_companion/features/settings_storage.dart';
import 'package:mystia_steward_companion/features/update_content.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/steward_test_fonts.dart';

/// 平台回归只使用内存状态：不连接游戏、不会打开真实发布页、没有安全存储写入。
class _Model extends FixtureModel {
  _Model({super.desktop}) {
    authority = {
      'registryId': 'fixture-registry',
      'currentDeviceId': 'self',
      'devices': [
        {'deviceId': 'self', 'label': '合成设备', 'isPrimary': true},
      ],
    };
  }
  @override
  ConnectionSource connectionSource = ConnectionSource.launchArguments;
  final names = <String>[];
  final urls = <String>[];
  @override
  PlatformStorageStatus? storageStatus;
  @override
  bool credentialUnavailable = false;
  final storageChoices = <bool>[];
  @override
  Future<void> initializeStorage({required bool importLegacy}) async {
    storageChoices.add(importLegacy);
  }

  Completer<void>? renameGate;
  @override
  Future<void> renameDevice(String value) async {
    names.add(value);
    await renameGate?.future;
    authority = {
      ...authority,
      'devices': [
        {'deviceId': 'self', 'label': value},
      ],
    };
  }

  @override
  Future<void> openReleasePage(String url) async {
    urls.add(url);
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  setUpAll(loadPreferredStewardTestFont);
  Future<void> open(
    WidgetTester tester,
    Widget child, {
    double width = 390,
  }) async {
    tester.view.physicalSize = Size(width, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final theme = stewardTheme(dark: false, touch: true);
    await tester.pumpWidget(
      material.MaterialApp(
        theme: theme.toApproximateMaterialTheme(),
        locale: const Locale('zh', 'CN'),
        localizationsDelegates: FLocalizations.localizationsDelegates,
        supportedLocales: FLocalizations.supportedLocales,
        home: FTheme(
          data: theme,
          child: material.Scaffold(body: child),
        ),
      ),
    );
    await tester.pump();
  }

  testWidgets('便携首次导入离线可用，必须确认且不继承旧身份', (tester) async {
    final model = _Model()
      ..connected = false
      ..storageStatus = PlatformStorageStatus(
        mode: 'windows-portable',
        location: 'synthetic/private/client-data',
        needsInitialization: true,
        canImportLegacy: true,
      );
    await open(
      tester,
      SingleChildScrollView(child: SettingsStorageSection(model)),
    );
    expect(find.text('独立客户端 · 便携目录'), findsOneWidget);
    expect(find.text('synthetic/private/client-data'), findsNothing);
    await tester.tap(find.text('导入旧 Flutter 数据'));
    await tester.pumpAndSettle();
    expect(model.storageChoices, isEmpty);
    expect(find.textContaining('不复制旧设备身份'), findsOneWidget);
    await tester.tap(find.text('确认'));
    await tester.pumpAndSettle();
    expect(model.storageChoices, [true]);
    expect(model.writes, isEmpty);
    await tester.tap(find.text('显示存储位置'));
    await tester.pumpAndSettle();
    expect(find.text('synthetic/private/client-data'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  for (final waiting in [true, false]) {
    testWidgets('存储${waiting ? '首次选择' : '凭据恢复'}优先于旧lastPage且不写导航偏好', (
      tester,
    ) async {
      final model = _Model()
        ..connected = false
        ..credentialUnavailable = !waiting
        ..localPreferences = {'lastPage': 1, 'reduceMotion': true}
        ..storageStatus = PlatformStorageStatus(
          mode: 'windows-portable',
          location: 'synthetic/client-data',
          needsInitialization: waiting,
          canImportLegacy: waiting,
        );
      addTearDown(model.dispose);
      await open(tester, WorkbenchShell(model), width: 640);
      await tester.pumpAndSettle();
      model.publish();
      await tester.pumpAndSettle();
      expect(find.text('本机数据存储'), findsOneWidget);
      expect(model.localPreferences['lastPage'], 1);
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('没有旧数据时允许新设备初始化，损坏凭据只提供恢复说明', (tester) async {
    final model = _Model()
      ..connected = false
      ..storageStatus = PlatformStorageStatus(
        mode: 'windows-portable',
        location: 'synthetic/client-data',
        needsInitialization: true,
        canImportLegacy: false,
        importUnavailableReason: '未找到可导入的旧 Flutter 数据。',
      );
    await open(
      tester,
      SingleChildScrollView(child: SettingsStorageSection(model)),
    );
    final import = tester.widget<StewardButton>(
      find.widgetWithText(StewardButton, '导入旧 Flutter 数据'),
    );
    expect(import.onPressed, isNull);
    await tester.tap(find.text('作为新设备开始'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('确认'));
    await tester.pumpAndSettle();
    expect(model.storageChoices, [false]);
    model.storageStatus!.needsInitialization = false;
    model.credentialUnavailable = true;
    await open(
      tester,
      SingleChildScrollView(child: SettingsStorageSection(model)),
    );
    expect(find.textContaining('不要清空原目录或凭据文件'), findsOneWidget);
    expect(find.text('作为新设备开始'), findsNothing);
    expect(tester.takeException(), isNull);
  });

  test('发布页白名单拒绝任意来源、userinfo、端口和编码路径', () {
    for (final suffix in ['', '/latest', '/tag/v1.3.2', '/tag/v1.4.0-beta.1']) {
      expect(
        CompanionPlatform.isProjectReleaseUrl(
          'https://github.com/blockshy/mystia-steward-companion/releases$suffix',
        ),
        isTrue,
      );
    }
    for (final url in [
      'file:///C:/private.txt',
      'https://example.com/releases',
      'https://github.com@evil.test/blockshy/mystia-steward-companion/releases',
      'https://github.com/blockshy/mystia-steward-companion/releases?redirect=evil',
      'https://github.com/blockshy/mystia-steward-companion/releases/tag/%2e%2e',
      'https://github.com:443/blockshy/mystia-steward-companion/releases',
      'https://github.com/blockshy/mystia-steward-companion/releases\n',
    ]) {
      expect(CompanionPlatform.isProjectReleaseUrl(url), isFalse, reason: url);
    }
  });

  testWidgets('启动参数连接仍可独立修改设备名，保存时保留后续草稿', (tester) async {
    final model = _Model()..renameGate = Completer<void>();
    await open(tester, DeviceNameEditor(model));
    final editor = find.byType(EditableText);
    await tester.enterText(editor, '新设备名');
    // 输入触发合法性重建后才模拟点击，保持与实际下一帧启用按钮的交互一致。
    await tester.pump();
    await tester.tap(find.text('保存名称'));
    await tester.pump(const Duration(milliseconds: 120));
    expect(model.names, ['新设备名']);
    await tester.enterText(editor, '继续编辑');
    model.renameGate!.complete();
    await tester.pump();
    expect(tester.widget<EditableText>(editor).controller.text, '继续编辑');
    expect(tester.takeException(), isNull);
  });

  testWidgets('外部更新导航修订可切入同一个已创建的设置页', (tester) async {
    final model = _Model();
    await open(tester, SettingsPage(model));
    expect(find.text('设备与共享配置'), findsOneWidget);
    await open(
      tester,
      SettingsPage(model, requestedTab: 'updates', navigationRevision: 1),
    );
    await tester.pump();
    expect(find.text('游戏电脑上的 Mod 更新'), findsOneWidget);
    expect(find.text('设备与共享配置'), findsNothing);
  });

  testWidgets('Android外观提供系统主题和手柄导航，保持标准窄控制列', (tester) async {
    final model = _Model(desktop: false);
    await open(tester, SettingsPage(model, requestedTab: 'appearance'));
    expect(find.text('手柄导航'), findsOneWidget);
    final select = tester.widget<StewardSelect<String>>(
      find.byType(StewardSelect<String>),
    );
    expect(select.items, const {'system': '跟随系统', 'light': '浅色', 'dark': '深色'});
    expect(tester.takeException(), isNull);
  });

  testWidgets('更新说明失败使用真实字段并保留可用发布页入口', (tester) async {
    final model = _Model();
    const url =
        'https://github.com/blockshy/mystia-steward-companion/releases/tag/v1.3.2';
    model.resources = {
      '/updates/status': {
        'enabled': true,
        'hasUpdate': true,
        'releaseUrl': url,
        'releaseHistoryState': 'failed',
        'releaseHistoryError': '合成摘要不匹配',
      },
    };
    await open(
      tester,
      SingleChildScrollView(child: SettingsUpdatesSection(model)),
    );
    expect(find.text('合成摘要不匹配'), findsOneWidget);
    await tester.tap(find.text('最新发布页'));
    await tester.pump(const Duration(milliseconds: 120));
    expect(model.urls, [url]);
    expect(tester.takeException(), isNull);
  });

  testWidgets('局域网主设备只下载游戏电脑整包，本机安装入口保持禁用', (tester) async {
    final model = _Model()..endpoint = 'http://192.168.1.20:32145';
    model.authority['currentDeviceIsPrimary'] = true;
    model.resources['/updates/status'] = {
      'enabled': true,
      'hasUpdate': true,
      'staged': true,
    };
    await open(
      tester,
      SingleChildScrollView(child: SettingsUpdatesSection(model)),
    );
    StewardButton button(String text) =>
        tester.widget<StewardButton>(find.widgetWithText(StewardButton, text));
    expect(button('退出游戏后安装').onPressed, isNull);
    expect(find.textContaining('此处不会更新当前设备的客户端'), findsOneWidget);

    model.resources['/updates/status']!['staged'] = false;
    await open(
      tester,
      SingleChildScrollView(child: SettingsUpdatesSection(model)),
    );
    await tester.tap(find.text('下载整包'));
    await tester.pump(const Duration(milliseconds: 120));
    expect(model.writes.single.$1, '/updates/download');

    model.endpoint = 'http://127.0.0.1:32145';
    model.resources['/updates/status']!['staged'] = true;
    await open(
      tester,
      SingleChildScrollView(child: SettingsUpdatesSection(model)),
    );
    expect(button('退出游戏后安装').onPressed, isNotNull);
    expect(tester.takeException(), isNull);
  });

  for (final error in ['', '客户端协议不兼容，请成套更新']) {
    testWidgets('未连接或协议不兼容仍可阅读本机更新说明并打开发布页：$error', (tester) async {
      final model = _Model()..connected = false;
      model.error = error;
      await open(
        tester,
        SettingsPage(model, requestedTab: 'updates', navigationRevision: 1),
      );
      expect(find.text('本机客户端更新'), findsOneWidget);
      expect(find.textContaining('不要只替换或复制 EXE'), findsOneWidget);
      final release = find.text('客户端发布页');
      await tester.ensureVisible(release);
      await tester.tap(release);
      await tester.pump(const Duration(milliseconds: 120));
      expect(model.urls, [
        'https://github.com/blockshy/mystia-steward-companion/releases',
      ]);
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  test('本机连接界面门禁不以主设备或桌面平台代替回环地址', () {
    final model = _Model();
    for (final endpoint in [
      'http://127.0.0.1:32145',
      'http://localhost:32145',
    ]) {
      model.endpoint = endpoint;
      expect(model.localGameConnection, isTrue);
    }
    for (final endpoint in ['http://192.168.1.20:32145', 'invalid']) {
      model.endpoint = endpoint;
      expect(model.localGameConnection, isFalse);
    }
    model.endpoint = 'http://127.0.0.1:32145';
    model.desktop = false;
    expect(model.localGameConnection, isFalse);
    model.desktop = true;
    model.connected = false;
    expect(model.localGameConnection, isFalse);
  });

  testWidgets('Markdown说明排版无原始标记，代码和图片保持文本且窄屏不溢出', (tester) async {
    await open(
      tester,
      const ReleaseNotesContent(
        '# 本次更新\n\n- **订单**响应更及时\n> 注意说明\n```dart\nprint(1);\n```\n[发布页](https://example.test) ![图片](https://example.test/image.png)',
      ),
    );
    expect(find.text('本次更新', findRichText: true), findsOneWidget);
    expect(find.text('订单响应更及时', findRichText: true), findsOneWidget);
    expect(find.text('print(1);'), findsOneWidget);
    expect(find.text('发布页 [图片：图片]', findRichText: true), findsOneWidget);
    expect(find.byType(Image), findsNothing);
    expect(tester.takeException(), isNull);
  });

  test('更新状态以安装阶段为先，未知阶段不会直接显示英文', () {
    expect(
      updateStateLabel({'installState': 'waiting-game', 'staged': true}),
      '等待游戏退出',
    );
    expect(updateStateLabel({'state': 'checking'}), '正在检查');
    expect(updateIsBusy({'installState': 'installing'}), isTrue);
    expect(updateIsBusy({'installState': 'failed'}), isFalse);
  });
}
