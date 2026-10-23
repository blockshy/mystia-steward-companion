import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/help_browser.dart';
import 'package:mystia_steward_companion/features/settings.dart';
import 'package:mystia_steward_companion/features/recommendations.dart';
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';

import '../tool/preview/rich_preview_workbench_model.dart';
import 'support/page_activity_assertions.dart';

/// 记录页面真正提交的配置调用，区分“模型拒绝写入”和“UI 没有误发旧选择”。
class _RecordingPreviewModel extends RichPreviewWorkbenchModel {
  _RecordingPreviewModel({super.desktop});
  int profileWrites = 0;
  @override
  Future<void> updateProfile(DataMap changes) async {
    profileWrites++;
    await super.updateProfile(changes);
  }
}

/// 合成连接只更新内存，并允许暂停提交以验证 HTTP 等待期间的草稿边界；不读取真实凭据。
class _ConnectionPreviewModel extends _RecordingPreviewModel {
  _ConnectionPreviewModel({super.desktop, bool registered = false}) {
    // 本夹具验证尚未注册身份的连接草稿；已注册设备改名由独立组件专项覆盖，
    // 不能再用重连接时修改请求头来模拟服务端 rename 成功。
    if (!registered) authority = {...authority, 'currentDeviceId': ''};
  }
  Completer<void>? connectionGate;
  String? connectFailure;
  @override
  ConnectionSource connectionSource = ConnectionSource.saved;
  @override
  bool connecting = false;
  String _connectionToken = 'preview-only-token';
  @override
  String get editableConnectionToken =>
      canEditConnection ? _connectionToken : '';
  final submissions = <(String, String, String)>[];
  int retryRequests = 0;

  @override
  Future<void> retryConnection() async {
    retryRequests++;
  }

  void publishConnection(String endpoint, String deviceLabel) {
    this.endpoint = endpoint;
    clientLabel = deviceLabel;
    connectionVersion++;
    notifyListeners();
  }

  @override
  Future<void> connect(
    String endpoint,
    String token,
    String deviceLabel,
  ) async {
    submissions.add((endpoint, token, deviceLabel));
    connecting = true;
    busy = true;
    notifyListeners();
    await connectionGate?.future;
    connecting = false;
    if (connectFailure != null) {
      busy = false;
      connected = false;
      error = connectFailure!;
      notifyListeners();
      return;
    }
    connected = true;
    busy = false;
    error = '';
    // 成功后回显本次实际提交的合成凭据；失败不修改，空值仍沿用同地址凭据。
    // 不读取父预览模型的旧值冒充本次连接已保存的 Token。
    if (token.isNotEmpty) _connectionToken = token;
    // 模拟生产 ApiConnection 的地址和设备名规范化，不建立任何网络连接。
    publishConnection(
      Uri.parse(endpoint.trim()).replace(path: '').toString(),
      deviceLabel.trim(),
    );
  }
}

/// 保留真实设置页面的路由、响应式布局和弹窗，仅用内存事实替代生产连接。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;

  setUpAll(() async {
    // 在真实异步环境预载随包资产，避免各尺寸测试进入帮助后立即卸载，
    // 将尚未完成的资产缓存留在已结束的 widget 测试假时钟中。
    await rootBundle.loadString('assets/help-content.json');
  });

  Future<void> open(
    WidgetTester tester,
    WorkbenchModel model, {
    bool editConnection = true,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(1280, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [workbenchProvider.overrideWithValue(model)],
        child: const StewardApp(),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('nav-4')));
    await tester.pumpAndSettle();
    // 既有字段回归仍从真实“修改连接”入口进入，不再依赖已连接时默认暴露凭据表单。
    if (editConnection && model.connected && model.canEditConnection) {
      await tester.tap(find.text('修改连接'));
      await tester.pumpAndSettle();
    }
  }

  /// 帮助使用真实随包资产，其解码不受 widget 假时钟驱动。先创建加载器并等待
  /// 该次产品 Future，再等待动画收敛，避免页头加载动画导致 pumpAndSettle 超时。
  Future<void> settleSection(WidgetTester tester, String title) async {
    if (title == '帮助') {
      await tester.pump();
      final loader = tester.widget<FutureBuilder<dynamic>>(
        find.byKey(const ValueKey('help-document-loader')),
      );
      await tester.runAsync(() async {
        await loader.future;
      });
    }
    await tester.pumpAndSettle();
  }

  Future<void> section(WidgetTester tester, String title) async {
    // 推荐偏好迁入主推荐页；仍经过真实导航验证跨页面保存与返回，而不是挂载旧入口。
    final recommendation = title == '推荐';
    final destination = find.byKey(ValueKey('nav-${recommendation ? 2 : 4}'));
    await tester.tap(
      destination.evaluate().isNotEmpty
          ? destination
          : find.text(recommendation ? '推荐' : '设置').last,
    );
    await tester.pumpAndSettle();
    final tab = find
        .descendant(
          of: recommendation
              ? find.byType(RecommendationsPage)
              : find.byType(SettingsPage),
          matching: find.text(recommendation ? '推荐偏好' : title),
        )
        .first;
    await tester.ensureVisible(tab);
    await tester.pumpAndSettle();
    await tester.tap(tab);
    await settleSection(tester, title);
  }

  /// 懒构建表单先滚动创建目标，再显式把点击中心放进可视区。
  Future<void> reveal(WidgetTester tester, Finder target) async {
    await tester.scrollUntilVisible(
      target,
      200,
      scrollable: find
          .descendant(
            of: find.byKey(const PageStorageKey('recommendation-preferences')),
            matching: find.byType(Scrollable),
          )
          .first,
    );
    await tester.pumpAndSettle();
    await Scrollable.ensureVisible(tester.element(target), alignment: .2);
    await tester.pumpAndSettle();
  }

  /// 根据真实字段语义定位 EditableText，避免把标签或装饰外壳误当成输入控件。
  Finder connectionField(String title) => find.descendant(
    of: find.byWidgetPredicate(
      (widget) => widget is StewardField && widget.title == title,
    ),
    matching: find.byType(EditableText),
  );

  TextEditingController connectionController(
    WidgetTester tester,
    String title,
  ) => tester.widget<EditableText>(connectionField(title)).controller;

  /// 连接表单和设备卡共用真实正文滚动视口；操作前滚入屏幕，不绕过命中测试。
  Future<void> revealConnection(WidgetTester tester, Finder target) async {
    await tester.scrollUntilVisible(
      target,
      180,
      scrollable: find
          .descendant(
            of: find.byKey(const PageStorageKey('settings-connection')),
            matching: find.byType(Scrollable),
          )
          .first,
    );
    await Scrollable.ensureVisible(tester.element(target), alignment: .3);
    await tester.pumpAndSettle();
  }

  testWidgets('安全存储恢复的 Windows 连接仍可显式进入修改', (tester) async {
    final model = _ConnectionPreviewModel();
    addTearDown(model.dispose);
    await open(tester, model, editConnection: false);
    expect(find.text('当前连接'), findsOneWidget);
    expect(find.text('本机安全存储恢复'), findsOneWidget);
    expect(find.text('连接游戏电脑'), findsNothing);
    expect(find.text('保存并连接'), findsNothing);
    expect(connectionField('API 地址'), findsNothing);
    expect(connectionField('连接 Token'), findsNothing);
    expect(find.text('修改连接'), findsOneWidget);
    expect(model.submissions, isEmpty);
    await tester.tap(find.text('修改连接'));
    await tester.pumpAndSettle();
    expect(connectionField('API 地址'), findsOneWidget);
    expect(connectionController(tester, '连接 Token').text, 'preview-only-token');
    expect(
      tester.widget<EditableText>(connectionField('连接 Token')).obscureText,
      isTrue,
    );
    expect(find.text('保存并重新连接'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final connected in [true, false]) {
    for (final width in [1280.0, 390.0]) {
      testWidgets('启动连接只读且可原参数重试：connected=$connected/$width/150%', (
        tester,
      ) async {
        final model = _ConnectionPreviewModel()
          ..connectionSource = ConnectionSource.launchArguments
          ..connected = connected;
        model.localPreferences = {
          ...model.localPreferences,
          'fontScalePercent': 150,
        };
        addTearDown(model.dispose);
        await open(tester, model, editConnection: false);
        tester.view.physicalSize = Size(width, 920);
        await tester.pumpAndSettle();
        expect(find.text('启动参数提供'), findsOneWidget);
        expect(find.text(model.endpoint), findsOneWidget);
        expect(connectionField('API 地址'), findsNothing);
        expect(connectionField('连接 Token'), findsNothing);
        expect(connectionField('设备名称'), findsNothing);
        expect(find.text('修改连接'), findsNothing);
        expect(find.text('保存并连接'), findsNothing);
        expect(find.text('保存并重新连接'), findsNothing);
        expect(find.textContaining('本次运行中不能修改'), findsNothing);
        if (!connected) {
          final retry = find.text('重试连接');
          await revealConnection(tester, retry);
          expect(retry.hitTestable(), findsOneWidget);
          await tester.tap(retry);
          await tester.pumpAndSettle();
          expect(model.retryRequests, 1);
        }
        expect(model.submissions, isEmpty);
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
      });
    }
  }

  testWidgets('编辑期间启动参数接管后旧提交回调不再写入，也不提示恢复旧草稿', (tester) async {
    final model = _ConnectionPreviewModel();
    addTearDown(model.dispose);
    await open(tester, model);
    await tester.enterText(connectionField('设备名称'), '旧连接草稿');
    await tester.enterText(
      connectionField('连接 Token'),
      'synthetic-stale-token',
    );
    final save = find.text('保存并重新连接');
    await revealConnection(tester, save);
    final oldSubmit = tester
        .widget<StewardButton>(
          find.ancestor(of: save, matching: find.byType(StewardButton)),
        )
        .onPressed!;
    model.connectionSource = ConnectionSource.launchArguments;
    model.publishConnection('http://127.0.0.1:32199', '游戏启动设备');
    // 故意在下一帧隐藏按钮之前调用旧闭包，验证处理函数的来源检查。
    oldSubmit();
    await tester.pumpAndSettle();
    expect(model.submissions, isEmpty);
    expect(connectionField('连接 Token'), findsNothing);
    expect(find.text('修改连接'), findsNothing);
    expect(find.textContaining('有未提交的连接草稿'), findsNothing);
    expect(find.text('synthetic-stale-token'), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('取消修改仅收起表单，重新打开保留草稿且不提交凭据', (tester) async {
    // 已注册设备的连接编辑只管理地址和凭据；名称草稿由独立改名组件专项验证。
    // 未注册身份会优先显示“身份尚未确认”，不适合验证这里的连接草稿提示。
    final model = _ConnectionPreviewModel(registered: true);
    addTearDown(model.dispose);
    await open(tester, model);
    await tester.enterText(
      connectionField('API 地址'),
      'http://192.168.1.9:32145',
    );
    await tester.enterText(
      connectionField('连接 Token'),
      'synthetic-cancelled-token',
    );
    final endpoint = connectionController(tester, 'API 地址');
    final token = connectionController(tester, '连接 Token');
    await revealConnection(tester, find.text('取消修改'));
    await tester.tap(find.text('取消修改'));
    await tester.pumpAndSettle();
    expect(connectionField('API 地址'), findsNothing);
    expect(find.textContaining('有未提交的连接草稿'), findsOneWidget);
    expect(model.submissions, isEmpty);
    await revealConnection(tester, find.text('修改连接'));
    await tester.tap(find.text('修改连接'));
    await tester.pumpAndSettle();
    expect(connectionController(tester, 'API 地址'), same(endpoint));
    expect(endpoint.text, 'http://192.168.1.9:32145');
    expect(connectionController(tester, '连接 Token'), same(token));
    expect(token.text, 'synthetic-cancelled-token');
    expect(
      tester.widget<EditableText>(connectionField('连接 Token')).obscureText,
      isTrue,
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('连接等待即时阻止重复提交，失败保留输入并可重试', (tester) async {
    final model = _ConnectionPreviewModel()
      ..connected = false
      ..connectionGate = Completer<void>()
      ..connectFailure = '合成连接失败，请检查地址';
    addTearDown(model.dispose);
    await open(tester, model);
    await tester.enterText(
      connectionField('连接 Token'),
      'synthetic-retry-token',
    );
    await revealConnection(tester, find.text('保存并连接'));
    final submit = tester
        .widget<StewardButton>(
          find.ancestor(
            of: find.text('保存并连接'),
            matching: find.byType(StewardButton),
          ),
        )
        .onPressed!;
    // 复用同一帧捕获的回调模拟连续激活：局部门闩必须早于下一帧禁用状态生效。
    submit();
    submit();
    await tester.pump();
    expect(model.submissions, hasLength(1));
    final pending = tester.widget<StewardButton>(
      find.ancestor(
        of: find.text('正在连接…'),
        matching: find.byType(StewardButton),
      ),
    );
    expect(pending.onPressed, isNull);
    model.connectionGate!.complete();
    await tester.pumpAndSettle();
    await expectPageActivityDetails(tester, model.connectFailure!);
    expect(
      connectionController(tester, '连接 Token').text,
      'synthetic-retry-token',
    );
    model.connectFailure = null;
    await revealConnection(tester, find.text('保存并连接'));
    await tester.tap(find.text('保存并连接'));
    await tester.pumpAndSettle();
    expect(model.submissions, hasLength(2));
    expect(find.text('当前连接'), findsOneWidget);
    expect(connectionField('连接 Token'), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('主设备在别处时只按精确 ID 标注本设备，同名与旧 isCurrent 均不能误导', (tester) async {
    final model = _ConnectionPreviewModel(desktop: false);
    model.authority = {
      ...model.authority,
      'currentDeviceId': 'this-phone',
      'currentDeviceIsPrimary': false,
      'devices': [
        {
          'deviceId': 'other-desktop',
          'label': '同名设备',
          'isCurrent': true,
          'isPrimary': true,
          'online': true,
          'syncPending': false,
        },
        {
          'deviceId': 'this-phone',
          'label': '同名设备',
          'isCurrent': false,
          'isPrimary': false,
          'online': true,
          'syncPending': false,
        },
      ],
    };
    addTearDown(model.dispose);
    await open(tester, model, editConnection: false);
    final current = find.byKey(const ValueKey('device-this-phone'));
    await revealConnection(tester, current);
    expect(find.text('同名设备 · 本设备 · 查看设备'), findsOneWidget);
    expect(find.text('同名设备 · 主设备'), findsOneWidget);
    expect(find.text('同名设备 · 本设备 · 主设备'), findsNothing);
    final currentWidget = tester.widget<CollapsibleSection>(current);
    expect(currentWidget.initiallyExpanded, isTrue);
    final remote = find.byKey(const ValueKey('device-other-desktop'));
    await revealConnection(tester, find.text('同名设备 · 主设备'));
    await tester.tap(find.text('同名设备 · 主设备'));
    await tester.pumpAndSettle();
    final syncPrimary = find.descendant(
      of: remote,
      matching: find.byWidgetPredicate(
        (widget) => widget is StewardButton && widget.text == '同步主配置',
      ),
    );
    expect(tester.widget<StewardButton>(syncPrimary).onPressed, isNull);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('设备身份未知时不猜本设备，也不提供移除入口的可执行回调', (tester) async {
    final model = _ConnectionPreviewModel();
    model.authority = {
      ...model.authority,
      'currentDeviceId': null,
      'devices': [
        {
          'deviceId': 'unknown-offline',
          'label': '未知来源',
          'isCurrent': true,
          'isPrimary': false,
          'online': false,
          'syncPending': false,
        },
      ],
    };
    addTearDown(model.dispose);
    await open(tester, model, editConnection: false);
    await revealConnection(tester, find.text('未知来源 · 查看设备'));
    expect(find.text('本设备身份尚未确认'), findsOneWidget);
    expect(find.textContaining(' · 本设备'), findsNothing);
    await tester.tap(find.text('未知来源 · 查看设备'));
    await tester.pumpAndSettle();
    // 移除操作与同步共用同一行，不再需要额外展开；身份未知仍必须没有可执行回调。
    final forget = find.byWidgetPredicate(
      (widget) => widget is StewardButton && widget.text == '忘记设备',
    );
    await revealConnection(tester, forget);
    expect(tester.widget<StewardButton>(forget).onPressed, isNull);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('连接摘要与显式编辑在 $width px／150% 下完整可滚动访问', (tester) async {
      final model = _ConnectionPreviewModel(desktop: width != 390)
        ..connectionSource = ConnectionSource.preview;
      model.localPreferences = {
        ...model.localPreferences,
        'fontScalePercent': 150,
      };
      addTearDown(model.dispose);
      await open(tester, model, editConnection: false);
      tester.view.physicalSize = Size(width, 920);
      await tester.pumpAndSettle();
      expect(find.text('离线合成预览'), findsOneWidget);
      await revealConnection(tester, find.text('修改连接'));
      await tester.tap(find.text('修改连接'));
      await tester.pumpAndSettle();
      await revealConnection(tester, connectionField('设备名称'));
      await tester.enterText(connectionField('设备名称'), '可滚动编辑草稿');
      await revealConnection(tester, find.text('保存并重新连接'));
      final saveRect = tester.getRect(find.text('保存并重新连接'));
      final viewport = tester.getRect(
        find.byKey(const PageStorageKey('settings-connection')),
      );
      expect(viewport.contains(saveRect.center), isTrue);
      await revealConnection(tester, find.text('取消修改'));
      await tester.tap(find.text('取消修改'));
      await tester.pumpAndSettle();
      expect(model.submissions, isEmpty);
      expect(connectionField('设备名称'), findsNothing);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('迟到的连接初始化会更新未编辑地址和设备名但不回填 Token', (tester) async {
    final model = _ConnectionPreviewModel()
      ..connected = false
      ..clientLabel = '管家客户端';
    addTearDown(model.dispose);
    await open(tester, model);
    final endpoint = connectionController(tester, 'API 地址');
    final device = connectionController(tester, '设备名称');
    final token = connectionController(tester, '连接 Token');
    expect(device.text, '管家客户端');
    model.publishConnection('http://127.0.0.1:32147', '游戏电脑');
    await tester.pumpAndSettle();
    expect(connectionController(tester, 'API 地址'), same(endpoint));
    expect(endpoint.text, model.endpoint);
    expect(device.text, '游戏电脑');
    expect(token.text, isEmpty);
    expect(
      tester.widget<EditableText>(connectionField('连接 Token')).obscureText,
      isTrue,
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('初始化与后续连接切换不会覆盖各字段用户草稿', (tester) async {
    final model = _ConnectionPreviewModel()..connected = false;
    addTearDown(model.dispose);
    await open(tester, model);
    await tester.enterText(
      connectionField('API 地址'),
      'http://192.168.1.7:32145',
    );
    await tester.enterText(connectionField('设备名称'), '准备连接的电脑');
    await tester.enterText(
      connectionField('连接 Token'),
      'synthetic-draft-token',
    );
    final endpoint = connectionController(tester, 'API 地址').value;
    final device = connectionController(tester, '设备名称').value;
    final token = connectionController(tester, '连接 Token').value;
    for (final revision in [1, 2]) {
      model.publishConnection(
        'http://127.0.0.1:${32145 + revision}',
        '游戏电脑$revision',
      );
      await tester.pumpAndSettle();
      expect(connectionController(tester, 'API 地址').value, endpoint);
      expect(connectionController(tester, '设备名称').value, device);
      expect(connectionController(tester, '连接 Token').value, token);
    }
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('连接切换只更新干净字段且相同模型通知保留选择范围', (tester) async {
    final model = _ConnectionPreviewModel();
    addTearDown(model.dispose);
    await open(tester, model);
    await tester.enterText(connectionField('设备名称'), '本机尚未保存名称');
    model.publishConnection('http://192.168.1.8:32145', '远端名称');
    await tester.pumpAndSettle();
    final endpoint = connectionController(tester, 'API 地址');
    expect(endpoint.text, model.endpoint);
    expect(connectionController(tester, '设备名称').text, '本机尚未保存名称');
    endpoint.selection = const TextSelection(baseOffset: 7, extentOffset: 12);
    final selected = endpoint.value;
    await model.refresh();
    await tester.pumpAndSettle();
    expect(endpoint.value, selected);
    // 上次同步的模型值成为新的基线，第二次切换仍应继续更新该干净字段。
    model.publishConnection('http://192.168.1.9:32145', '另一台电脑');
    await tester.pumpAndSettle();
    expect(endpoint.text, model.endpoint);
    expect(connectionController(tester, '设备名称').text, '本机尚未保存名称');
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('中文 composing 即使仍等于旧绑定文本也不被迟到初始化打断', (tester) async {
    final model = _ConnectionPreviewModel()..clientLabel = '游戏电脑';
    addTearDown(model.dispose);
    await open(tester, model);
    await tester.showKeyboard(connectionField('设备名称'));
    const composing = TextEditingValue(
      text: '游戏电脑',
      selection: TextSelection.collapsed(offset: 4),
      composing: TextRange(start: 0, end: 4),
    );
    tester.testTextInput.updateEditingValue(composing);
    await tester.pump();
    model.publishConnection('http://127.0.0.1:32147', '已恢复的设备名称');
    await tester.pumpAndSettle();
    expect(connectionController(tester, '设备名称').value, composing);
    // 输入法提交后的新文本成为用户草稿，后续状态刷新仍不能替换它。
    const committed = TextEditingValue(
      text: '游戏电脑的新名称',
      selection: TextSelection.collapsed(offset: 8),
    );
    tester.testTextInput.updateEditingValue(committed);
    await model.refresh();
    await tester.pumpAndSettle();
    expect(connectionController(tester, '设备名称').value, committed);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final editWhileConnecting in [false, true]) {
    testWidgets('成功连接规范化提交值且等待期间新草稿保留：$editWhileConnecting', (tester) async {
      final model = _ConnectionPreviewModel()
        ..connectionGate = Completer<void>();
      addTearDown(model.dispose);
      await open(tester, model);
      await tester.enterText(
        connectionField('API 地址'),
        'http://192.168.1.10:32145/',
      );
      await tester.enterText(connectionField('设备名称'), '  我的电脑  ');
      await tester.enterText(
        connectionField('连接 Token'),
        'synthetic-submitted-token',
      );
      await revealConnection(tester, find.text('保存并重新连接'));
      await tester.tap(find.text('保存并重新连接'));
      await tester.pump();
      expect(model.submissions, [
        ('http://192.168.1.10:32145/', 'synthetic-submitted-token', '  我的电脑  '),
      ]);
      if (editWhileConnecting) {
        await tester.enterText(
          connectionField('API 地址'),
          'http://192.168.1.11:32145',
        );
        await tester.enterText(connectionField('设备名称'), '下一次连接草稿');
        await tester.enterText(
          connectionField('连接 Token'),
          'synthetic-next-token',
        );
      }
      model.connectionGate!.complete();
      await tester.pumpAndSettle();
      if (!editWhileConnecting) {
        expect(connectionField('连接 Token'), findsNothing);
        // 保存后编辑器收起，原列表仍保留滚动位置；先把新的只读操作行滚入
        // 实际视口再点击，继续保持严格命中检查，不能点击页头遮住的离屏按钮。
        await revealConnection(tester, find.text('修改连接'));
        await tester.tap(find.text('修改连接'));
        await tester.pumpAndSettle();
      }
      expect(
        connectionController(tester, 'API 地址').text,
        editWhileConnecting
            ? 'http://192.168.1.11:32145'
            : 'http://192.168.1.10:32145',
      );
      expect(
        connectionController(tester, '设备名称').text,
        editWhileConnecting ? '下一次连接草稿' : '我的电脑',
      );
      expect(
        connectionController(tester, '连接 Token').text,
        editWhileConnecting
            ? 'synthetic-next-token'
            : 'synthetic-submitted-token',
      );
      expect(
        tester.widget<EditableText>(connectionField('连接 Token')).obscureText,
        isTrue,
      );
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('连接输入跨越旧断点和手机宽度仍保留草稿、选择范围与焦点', (tester) async {
    final model = _RecordingPreviewModel();
    addTearDown(model.dispose);
    await open(tester, model);
    // 已注册设备名使用独立编辑器；连接表单的响应式草稿约束由地址字段验证。
    final field = connectionField('API 地址');
    final original = tester.widget<EditableText>(field).controller;
    const draft = 'http://192.168.1.99:32145';
    await tester.enterText(field, draft);
    original.selection = const TextSelection(baseOffset: 1, extentOffset: 3);
    final focus = tester.widget<EditableText>(field).focusNode;
    expect(focus.hasFocus, isTrue);
    // 统一顶部导航后不再切换父级结构；跨旧断点、触屏主题和手机宽度都不能重建输入。
    for (final width in [1000.0, 640.0, 390.0, 1280.0]) {
      tester.view.physicalSize = Size(width, 920);
      await tester.pumpAndSettle();
      final editable = tester.widget<EditableText>(field);
      final current = editable.controller;
      expect(identical(current, original), isTrue);
      expect(current.text, draft);
      expect(
        current.selection,
        const TextSelection(baseOffset: 1, extentOffset: 3),
      );
      expect(editable.focusNode, same(focus));
      expect(focus.hasFocus, isTrue);
      expect(tester.takeException(), isNull);
    }
    expect(model.endpoint, isNot(draft));
    await tester.pumpWidget(const SizedBox());
  });

  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('设置 $width px／150% 所有分组始终在正文上方使用横向 Tab', (tester) async {
      final model = _RecordingPreviewModel();
      model.localPreferences = {
        ...model.localPreferences,
        'fontScalePercent': 150,
      };
      addTearDown(model.dispose);
      await open(tester, model);
      tester.view.physicalSize = Size(width, 920);
      await tester.pumpAndSettle();
      final navigation = find.descendant(
        of: find.byType(SettingsPage),
        matching: find.byWidgetPredicate(
          (widget) =>
              widget is WorkspaceTabs<String> &&
              widget.items.containsKey('connection'),
        ),
      );
      expect(navigation, findsOneWidget, reason: '宽屏不得改成左侧 FButton 目录。');
      final settingsRect = tester.getRect(find.byType(SettingsPage));
      for (final title in ['连接与设备', '外观', '更新', '诊断', '帮助']) {
        final target = find.descendant(
          of: navigation,
          matching: find.text(title),
        );
        final tabRect = tester.getRect(target);
        expect(tabRect.left, greaterThanOrEqualTo(settingsRect.left));
        expect(tabRect.right, lessThanOrEqualTo(settingsRect.right));
        expect(target.hitTestable(), findsOneWidget);
        await tester.tap(target);
        await settleSection(tester, title);
        final body = title == '帮助'
            ? find.byType(HelpBrowser)
            : find.byKey(
                PageStorageKey(
                  'settings-${const {'连接与设备': 'connection', '外观': 'appearance', '更新': 'updates', '诊断': 'diagnostics'}[title]}',
                ),
              );
        expect(
          tester.getRect(navigation).bottom,
          lessThanOrEqualTo(tester.getRect(body).top),
        );
        expect(
          tester.getRect(body).height,
          greaterThan(200),
          reason: '导航换行后仍须留出可浏览正文。',
        );
        expect(tester.takeException(), isNull, reason: '$title at $width');
      }
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('推荐偏好保留窗口缩放及切回设置后返回的滚动位置', (tester) async {
    final model = _RecordingPreviewModel();
    // 固定模式没有九项参数，可能无需滚动；用真实自定义长表单验证滚动保留。
    model.profile = {
      ...model.profile,
      'recommendationSortProfile': {
        ...object(model.profile['recommendationSortProfile']),
        'preset': 'custom',
      },
    };
    addTearDown(model.dispose);
    await open(tester, model);
    await section(tester, '推荐');
    final scroller = find
        .descendant(
          of: find.byKey(const PageStorageKey('recommendation-preferences')),
          matching: find.byType(Scrollable),
        )
        .first;
    final position = tester.state<ScrollableState>(scroller).position;
    expect(position.maxScrollExtent, greaterThan(120));
    position.jumpTo(120);
    await tester.pumpAndSettle();
    for (final width in [640.0, 1280.0]) {
      tester.view.physicalSize = Size(width, 920);
      await tester.pumpAndSettle();
      final current = tester.state<ScrollableState>(scroller).position;
      expect(current, same(position));
      expect(current.pixels, closeTo(120, 1));
    }
    await section(tester, '外观');
    await section(tester, '推荐');
    expect(
      tester.state<ScrollableState>(scroller).position.pixels,
      closeTo(120, 1),
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('宽屏设置顶部 Tab 不取代帮助自己的目录与文章分栏', (tester) async {
    final model = _RecordingPreviewModel();
    addTearDown(model.dispose);
    await open(tester, model);
    await section(tester, '帮助');
    final directory = find.byKey(const ValueKey('help-directory'));
    final article = find.byKey(const ValueKey('help-article-title'));
    expect(directory, findsOneWidget);
    expect(article, findsOneWidget);
    expect(
      tester.getRect(directory).right,
      lessThan(tester.getRect(article).left),
    );
    expect(find.byKey(const ValueKey('help-back')), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('评分权重键盘自动应用，折叠并重新展开保留已保存值且不出现保存按钮', (tester) async {
    final model = _RecordingPreviewModel();
    model.profile = {
      ...model.profile,
      'recommendationSortProfile': {
        ...object(model.profile['recommendationSortProfile']),
        'preset': 'custom',
      },
    };
    addTearDown(model.dispose);
    await open(tester, model);
    await section(tester, '推荐');
    final toggle = find.text('自定义评分参数');
    await reveal(tester, toggle);
    // 显式选择自定义后参数默认展开，直接验证自动保存，不再点击把它收起。
    final row = find.byKey(const ValueKey<Object?>('foodPreference'));
    final slider = find.descendant(of: row, matching: find.byType(FSlider));
    await reveal(tester, slider);
    final outline = find
        .descendant(of: slider, matching: find.byType(FFocusedOutline))
        .first;
    Focus.of(tester.element(outline)).requestFocus();
    await tester.pump();
    for (var index = 0; index < 3; index++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.pump();
    }
    expect(model.profileWrites, 0);
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pumpAndSettle();
    expect(model.profileWrites, 1);
    await reveal(tester, toggle);
    await tester.tap(toggle);
    await tester.pumpAndSettle();
    expect(find.text('料理喜好'), findsNothing);
    await tester.tap(toggle);
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<IntegerSetting>(
            find.descendant(of: row, matching: find.byType(IntegerSetting)),
          )
          .value,
      73,
    );
    expect(find.descendant(of: row, matching: find.text('保存')), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final change in ['connection', 'authority']) {
    testWidgets('排除选择器打开后 $change 改变时不会将旧目录选择应用到新上下文', (tester) async {
      final model = _RecordingPreviewModel();
      addTearDown(model.dispose);
      await open(tester, model);
      await section(tester, '推荐');
      final exclude = find.descendant(
        of: find.byKey(const ValueKey('excludedIngredientIds')),
        matching: find.byKey(const ValueKey('dynamic-tags-add')),
      );
      await reveal(tester, exclude);
      await tester.tap(exclude);
      await tester.pumpAndSettle();
      final oldSelection = tester.widget<StewardSelect<int>>(exclude).onChanged;
      if (change == 'connection') {
        model.connectionVersion++;
      } else {
        model.authority = {
          ...model.authority,
          'authorityRevision':
              (model.authority['authorityRevision']! as int) + 1,
        };
      }
      // 权限仍是主设备，保证验证的是旧选择的边界，而不是按钮被动变为只读。
      await model.refresh();
      await tester.pumpAndSettle();
      expect(model.canEdit, isTrue);
      expect(
        find.byWidgetPredicate((widget) => widget is FSelectItem<int>),
        findsNothing,
      );
      // 即便旧弹层的迟到回调抵达，设置页仍重新复核而不会写入新身份。
      oldSelection(1);
      await tester.pumpAndSettle();
      expect(model.profileWrites, 0);
      expect(
        object(
          model.profile['recommendationExclusions'],
        )['excludedIngredientIds'],
        isEmpty,
      );
      await expectPageActivityDetails(tester, '连接、目录或主设备已变化，排除设置未保存，请重新选择。');
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('预算下拉位于标签右侧且与开关右边缘对齐 ${width.toInt()}px/150%', (tester) async {
      final model = _RecordingPreviewModel();
      model.localPreferences = {
        ...model.localPreferences,
        'fontScalePercent': 150,
      };
      addTearDown(model.dispose);
      await open(tester, model);
      tester.view.physicalSize = Size(width, 920);
      await tester.pumpAndSettle();
      await section(tester, '推荐');
      final budget = find.byKey(const ValueKey('recommendation-budget-policy'));
      await reveal(tester, budget);
      // 固定时间排序已经从共享协议及设置删除；预算、变体和评分仍保持独立可编辑。
      expect(find.text('订单排序'), findsNothing);
      expect(find.text('接单顺序'), findsNothing);
      expect(find.text('按客人'), findsNothing);
      expect(model.profile.containsKey('serviceOrderSortMode'), isFalse);
      final toggle = find.byWidgetPredicate(
        (widget) => widget is SettingToggle && widget.title == '收藏酒水优先',
      );
      final switchRect = tester.getRect(
        find.descendant(of: toggle, matching: find.byType(FSwitch)),
      );
      final budgetRect = tester.getRect(budget);
      final titleRect = tester.getRect(
        find.byWidgetPredicate(
          (widget) =>
              widget is Text &&
              widget.key == const ValueKey('setting-control-label') &&
              widget.data == '预算不足时',
        ),
      );
      expect(budgetRect.right, closeTo(switchRect.right, .1));
      expect(titleRect.right, lessThan(budgetRect.left));
      expect(titleRect.center.dy, closeTo(budgetRect.center.dy, .1));
      await tester.tap(budget);
      await tester.pumpAndSettle();
      await tester.tap(find.text('仅提示').last);
      await tester.pumpAndSettle();
      expect(model.profile['recommendationBudgetPolicy'], 'warn');
      expect(model.profileWrites, 1);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  testWidgets('排除选择器边界稳定时仍会提交选择一次', (tester) async {
    final model = _RecordingPreviewModel();
    addTearDown(model.dispose);
    await open(tester, model);
    await section(tester, '推荐');
    final exclude = find.descendant(
      of: find.byKey(const ValueKey('excludedIngredientIds')),
      matching: find.byKey(const ValueKey('dynamic-tags-add')),
    );
    await reveal(tester, exclude);
    await tester.tap(exclude);
    await tester.pumpAndSettle();
    final choice = find.byWidgetPredicate(
      (widget) => widget is FSelectItem<int> && widget.value == 1,
    );
    // 搜索下拉按视口选择上下展开，首项不一定处在当前滚动窗口中；先真实滚动
    // 其内部列表，再执行保持命中检查的点击，不能直接点不可见条目的布局中心。
    await tester.ensureVisible(choice);
    await tester.pumpAndSettle();
    await tester.tap(choice);
    await tester.pumpAndSettle();
    expect(model.profileWrites, 1);
    expect(
      object(
        model.profile['recommendationExclusions'],
      )['excludedIngredientIds'],
      [1],
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
}
