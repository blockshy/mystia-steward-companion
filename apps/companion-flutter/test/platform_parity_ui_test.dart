import 'dart:async';

import 'package:companion_platform/companion_platform.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/device_name_editor.dart';
import 'package:mystia_steward_companion/features/settings.dart';
import 'package:mystia_steward_companion/features/update_content.dart';
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
    expect(find.text('成套更新'), findsOneWidget);
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
