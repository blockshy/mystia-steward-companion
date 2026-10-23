import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:companion_platform/companion_platform.dart';
import 'package:companion_client/companion_client.dart';
import 'package:companion_platform/src/platform.g.dart' show PlatformHostApi;
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/live_workbench_model.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';

/// 只替换原生边界，保留生产初始化、持久化串行队列与模型状态通知。
/// 无游戏地址、游戏进程或真实凭据；HTTP 仍由 flutter_test 的隔离客户端拒绝。
class _LifecycleHost extends PlatformHostApi {
  final launch = PlatformLaunch(desktop: true);
  final secrets = <String, String>{'client-id': 'synthetic-device-00000001'};
  String? preferences = jsonEncode({'deviceLabel': '游戏电脑'});
  Completer<void>? initializeGate;
  Completer<void>? preferenceWriteGate;
  int remainingWriteFailures = 0;
  bool failPassthroughWrites = false;
  final appliedWindows = <WindowPreferences>[];
  bool failPreferences = false, failIdentity = false;
  int initializeCalls = 0, identityReads = 0, identityWrites = 0;
  int preferenceReads = 0;
  final secretReads = <String>[];
  Object? actionFailure;
  Completer<void>? actionGate;

  @override
  Future<bool> performWindowAction(WindowAction action) async {
    await actionGate?.future;
    if (actionFailure case final Object failure) throw failure;
    return true;
  }

  @override
  Future<PlatformLaunch> initialize() async {
    initializeCalls++;
    await initializeGate?.future;
    return launch;
  }

  @override
  Future<String?> readPreferences() async {
    preferenceReads++;
    if (failPreferences)
      throw PlatformException(
        code: 'platform-failure',
        message: 'synthetic preferences unavailable',
      );
    return preferences;
  }

  @override
  Future<void> writePreferences(String value) async {
    await preferenceWriteGate?.future;
    if (failPassthroughWrites &&
        (jsonDecode(value) as Map)['mousePassthrough'] == true) {
      throw StateError('synthetic passthrough persistence failed');
    }
    if (remainingWriteFailures > 0) {
      remainingWriteFailures--;
      throw StateError('synthetic local write failed');
    }
    preferences = value;
  }

  @override
  Future<String?> readSecret(String key) async {
    secretReads.add(key);
    if (key == 'client-id') {
      identityReads++;
      if (failIdentity)
        throw PlatformException(
          code: 'platform-failure',
          message: 'synthetic identity unavailable',
        );
    }
    return secrets[key];
  }

  @override
  Future<void> writeSecret(String key, String value) async {
    if (key == 'client-id') identityWrites++;
    secrets[key] = value;
  }

  @override
  Future<void> applyWindowPreferences(WindowPreferences preferences) async {
    appliedWindows.add(preferences);
  }

  @override
  Future<PlatformInput> pollInput() async => PlatformInput(
    focused: true,
    connected: false,
    buttons: 0,
    leftX: 0,
    leftY: 0,
    leftTrigger: 0,
    rightTrigger: 0,
    activationRevision: 0,
    launch: launch,
  );
}

/// 仅注入内存原生事件，继续使用真正的初始化与持久化队列。
/// 同步流让测试明确事件已经交给模型，避免依赖轮询计时或真实窗口激活。
class _EventLifecyclePlatform extends CompanionPlatform {
  _EventLifecyclePlatform({required super.host});
  final injectedEvents = StreamController<PlatformEvent>.broadcast(sync: true);
  @override
  Stream<PlatformEvent> get events => injectedEvents.stream;
  @override
  Future<void> dispose() async {
    await injectedEvents.close();
    await super.dispose();
  }
}

/// 与原生单实例激活相同的完整参数组；只传递合成数据，不通过系统进程通信。
PlatformEvent _activation(String endpoint, String token) => PlatformEvent(
  'connectionActivated',
  PlatformInput(
    focused: true,
    connected: false,
    buttons: 0,
    leftX: 0,
    leftY: 0,
    leftTrigger: 0,
    rightTrigger: 0,
    activationRevision: 1,
    launch: PlatformLaunch(desktop: true, endpoint: endpoint, token: token),
  ),
);

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  test('移动端明确编辑可读回同地址安全存储Token，受管连接不提供编辑凭据', () async {
    final host = _LifecycleHost()
      ..preferences = jsonEncode({
        'endpoint': 'http://192.168.1.50:32145',
        'deviceLabel': '合成手机',
      })
      ..secrets['connection-token'] = 'synthetic-mobile-token';
    host.launch.desktop = false;
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    expect(model.editableConnectionToken, isEmpty);
    await model.initialize();
    expect(model.endpoint, 'http://192.168.1.50:32145');
    expect(model.editableConnectionToken, 'synthetic-mobile-token');
    expect(host.preferences, isNot(contains('synthetic-mobile-token')));
    expect(model.recentOperations, isEmpty, reason: '启动和后台连接不是用户操作历史');
    final managed = _LifecycleHost();
    managed.launch.endpoint = 'http://127.0.0.1:32145';
    managed.launch.token = 'synthetic-launch-token';
    final locked = LiveWorkbenchModel(
      platform: CompanionPlatform(host: managed),
    );
    addTearDown(locked.dispose);
    await locked.initialize();
    expect(locked.editableConnectionToken, isEmpty);
  });

  test('最近操作有名称时间和结果且限量，轮询和导航不写入，未知结果不称未执行', () async {
    final host = _LifecycleHost();
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    await model.readResource('/logs/settings');
    await model.updateLocal({'lastPage': 4});
    expect(model.recentOperations, isEmpty);
    for (var index = 0; index < 22; index++)
      await model.platformCommand('hide');
    expect(model.recentOperations, hasLength(20));
    expect(model.recentOperations.first.name, '隐藏客户端');
    expect(
      model.recentOperations.first.id,
      greaterThan(model.recentOperations.last.id),
    );
    expect(
      model.recentOperations.every(
        (item) => !item.completedAt.isBefore(item.startedAt),
      ),
      isTrue,
    );
    expect(
      model.recentOperations.first.result,
      WorkbenchOperationResult.success,
    );
    host.actionFailure = const ApiException(
      'synthetic-lost-reply',
      '合成响应丢失',
      possiblyApplied: true,
    );
    await model.platformCommand('hide');
    expect(
      model.recentOperations.first.result,
      WorkbenchOperationResult.unknown,
    );
    expect(model.recentOperations.first.summary, contains('不要重复提交'));
    expect(model.recentOperations.first.summary, isNot(contains('未执行')));
    host.actionFailure = StateError('合成已拒绝');
    await model.platformCommand('hide');
    expect(
      model.recentOperations.first.result,
      WorkbenchOperationResult.failed,
    );
  });

  test('旧动作迟到失败不覆盖新连接错误，也不进入新连接历史', () async {
    final host = _LifecycleHost();
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    host.actionGate = Completer<void>();
    final old = model.platformCommand('hide');
    await Future<void>.delayed(Duration.zero);
    await model.connect(
      'http://127.0.0.1:32201',
      'synthetic-new-token',
      '合成设备',
    );
    final newError = model.error;
    host.actionFailure = StateError('不应进入新连接的旧错误');
    host.actionGate!.complete();
    await old;
    expect(model.error, newError);
    expect(model.recentOperations.any((item) => item.name == '隐藏客户端'), isFalse);
    expect(model.editableConnectionToken, 'synthetic-new-token');
  });

  test('窗口原生行为不提前应用未保存的排队值，失败后不残留穿透', () async {
    final host = _LifecycleHost()
      ..preferences = jsonEncode({
        'fontScalePercent': 100,
        'mousePassthrough': false,
      });
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    host.preferenceWriteGate = Completer<void>();
    host.failPassthroughWrites = true;
    final first = model.updateLocal({'fontScalePercent': 115});
    final failed = model.updateLocal({'mousePassthrough': true});
    host.preferenceWriteGate!.complete();
    await Future.wait([first, failed]);
    expect(model.localPreferences['fontScalePercent'], 115);
    expect(model.localPreferences['mousePassthrough'], isFalse);
    expect(host.appliedWindows.every((item) => !item.mousePassthrough), isTrue);
    expect((jsonDecode(host.preferences!) as Map)['mousePassthrough'], isFalse);
    expect(model.error, contains('synthetic passthrough persistence failed'));
  });

  test('连续本机滑块立即预览，串行保存不覆盖其他字段或最终值', () async {
    final host = _LifecycleHost();
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    host.preferenceWriteGate = Completer<void>();
    final first = model.updateLocal({'fontScalePercent': 110});
    final second = model.updateLocal({'backgroundOpacityPercent': 60});
    final last = model.updateLocal({'fontScalePercent': 125});
    // 原生持久化尚未返回，用户仍应立即看到最后一次拖动的显示效果。
    expect(model.localPreferences['fontScalePercent'], 125);
    expect(model.localPreferences['backgroundOpacityPercent'], 60);
    host.preferenceWriteGate!.complete();
    await Future.wait([first, second, last]);
    final saved = jsonDecode(host.preferences!) as Map<String, dynamic>;
    expect(saved['fontScalePercent'], 125);
    expect(saved['backgroundOpacityPercent'], 60);
    expect(model.localPreferences['fontScalePercent'], 125);
    expect(model.localPreferences['backgroundOpacityPercent'], 60);
    expect(model.error, isEmpty);
  });

  test('本机迟到保存失败只回退自身字段，不抹掉较新的滑块修改', () async {
    final host = _LifecycleHost()
      ..preferences = jsonEncode({'fontScalePercent': 100});
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    host.preferenceWriteGate = Completer<void>();
    host.remainingWriteFailures = 1;
    final failed = model.updateLocal({'fontScalePercent': 110});
    final latest = model.updateLocal({'fontScalePercent': 130});
    host.preferenceWriteGate!.complete();
    await Future.wait([failed, latest]);
    expect(model.localPreferences['fontScalePercent'], 130);
    expect((jsonDecode(host.preferences!) as Map)['fontScalePercent'], 130);
    host.remainingWriteFailures = 1;
    await model.updateLocal({'fontScalePercent': 140});
    expect(model.localPreferences['fontScalePercent'], 130);
    expect(model.error, contains('synthetic local write failed'));
  });

  test('初始化失败保留阶段，切页与外观保存不能清除，重试成功才消除', () async {
    final host = _LifecycleHost()..failPreferences = true;
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    expect(model.error, contains('初始化失败（读取本机偏好）'));
    final failure = model.error;
    host.failPreferences = false;
    await model.updateLocal({'lastPage': 4, 'darkMode': true});
    expect(model.error, failure);
    expect(model.connected, isFalse);
    expect(model.canExecute, isFalse);

    await model.initialize();
    expect(model.error, isEmpty);
    expect(model.clientLabel, '游戏电脑');
    expect(host.initializeCalls, 2);
    expect(host.identityWrites, 0, reason: '有效的原身份必须保留，不能通过重置来恢复。');
  });

  test('身份读取失败后人工连接先重新初始化，不能提交空设备ID', () async {
    final host = _LifecycleHost()..failIdentity = true;
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    expect(model.error, contains('初始化失败（读取设备身份）'));
    host.failIdentity = false;
    await model.connect('http://127.0.0.1:32199', 'synthetic-token', '游戏电脑');
    expect(host.identityReads, 2);
    expect(host.identityWrites, 0);
    expect(host.secrets['connection-token'], 'synthetic-token');
    expect(model.error, isNot(contains('设备 ID 格式无效')));
    expect(model.error, isNot(contains('初始化失败')));
    expect(model.connected, isFalse, reason: '本测试不提供 HTTP 宿主，不能误报连接成功。');
    expect(model.notice, isEmpty);
    expect(model.canExecute, isFalse);
  });

  test('并发初始化共用串行状态，只恢复一次设备身份', () async {
    final host = _LifecycleHost()..initializeGate = Completer<void>();
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    final first = model.initialize();
    final second = model.initialize();
    // 入队当帧就应反映正在连接，不等待微任务中的原生桥开始执行。
    expect(model.connecting, isTrue);
    await Future<void>.delayed(Duration.zero);
    expect(host.initializeCalls, 1);
    host.initializeGate!.complete();
    await Future.wait([first, second]);
    expect(host.initializeCalls, 1);
    expect(host.identityReads, 1);
    expect(model.error, isEmpty);
    expect(model.connecting, isFalse);
  });

  for (final source in [
    ConnectionSource.launchArguments,
    ConnectionSource.saved,
  ]) {
    test('连接来源只根据真实凭据读取路径记录：$source', () async {
      final host = _LifecycleHost()
        ..preferences = jsonEncode({
          'endpoint': 'http://127.0.0.1:32199',
          'deviceLabel': '合成设备',
        });
      if (source == ConnectionSource.launchArguments) {
        host.launch.endpoint = 'http://127.0.0.1:32199';
        host.launch.token = 'synthetic-launch-token';
      } else {
        host.secrets['connection-token'] = 'synthetic-saved-token';
      }
      final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
      addTearDown(model.dispose);
      await model.initialize();
      expect(model.connectionSource, source);
      expect(
        model.canEditConnection,
        source != ConnectionSource.launchArguments,
      );
      expect(model.connecting, isFalse);
      expect(model.connected, isFalse, reason: 'Flutter 测试网络被隔离，来源不是连接成功声明。');
      expect(host.identityWrites, 0);
    });
  }

  test('同一规范地址可沿用安全凭据，更换地址但留空 Token 时不替换连接或身份', () async {
    final host = _LifecycleHost()
      ..preferences = jsonEncode({
        'endpoint': 'http://127.0.0.1:32199',
        'deviceLabel': '原设备',
      });
    host.secrets['connection-token'] = 'synthetic-preserved-token';
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    await model.connect(' http://localhost:32199/ ', '', '同一设备新名称');
    expect(model.endpoint, 'http://127.0.0.1:32199');
    expect(model.clientLabel, '同一设备新名称');
    expect(model.connectionSource, ConnectionSource.manual);
    expect(host.secrets['connection-token'], 'synthetic-preserved-token');
    final preferences = host.preferences;
    final generation = model.connectionVersion;
    await model.connect('http://192.168.1.90:32199', '', '不得替换');
    expect(model.error, contains('更换 API 地址时请填写对应的连接 Token'));
    expect(model.endpoint, 'http://127.0.0.1:32199');
    expect(model.clientLabel, '同一设备新名称');
    expect(model.connectionVersion, generation);
    expect(model.connecting, isFalse);
    expect(host.preferences, preferences);
    expect(host.secrets['connection-token'], 'synthetic-preserved-token');
    expect(host.secrets['client-id'], 'synthetic-device-00000001');
    expect(host.identityWrites, 0);
  });

  test('不完整启动连接不能拼接存储凭据或通过手工覆盖恢复', () async {
    final host = _LifecycleHost()
      ..preferences = jsonEncode({
        'endpoint': 'http://127.0.0.1:32199',
        'deviceLabel': '原设备',
      });
    host.launch.endpoint = 'http://192.168.1.90:32199';
    host.secrets['connection-token'] = 'synthetic-original-token';
    final originalPreferences = host.preferences;
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    expect(model.error, contains('启动连接参数不完整'));
    expect(model.connectionVersion, 0, reason: '未创建会话，不能先请求新地址再报错。');
    expect(host.preferences, originalPreferences);
    expect(host.secrets['connection-token'], 'synthetic-original-token');
    expect(model.connecting, isFalse);
    expect(model.canEditConnection, isFalse);
    await model.connect(
      'http://192.168.1.90:32199',
      'synthetic-new-token',
      '原设备',
    );
    expect(host.initializeCalls, 1);
    expect(host.secrets['connection-token'], 'synthetic-original-token');
    expect(model.connectionSource, ConnectionSource.launchArguments);
    expect(model.error, contains('本次运行中不能修改'));
    expect(host.preferences, originalPreferences);
    expect(host.secretReads, isNot(contains('connection-token')));
    expect(model.connectionVersion, 0);
    await model.retryConnection();
    expect(model.error, contains('启动连接参数不完整'));
    expect(model.connectionVersion, 0);
    expect(host.identityWrites, 0);
  });

  test('冷启动显式空 Token 是离线请求，不回填旧凭据或误报缺少凭据', () async {
    final host = _LifecycleHost()
      ..preferences = jsonEncode({'endpoint': 'http://127.0.0.1:32199'});
    host.launch.endpoint = 'http://192.168.1.90:32199';
    host.launch.token = '';
    host.secrets['connection-token'] = 'synthetic-unused-token';
    final originalPreferences = host.preferences;
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    expect(model.error, isEmpty);
    expect(model.connected, isFalse);
    expect(model.connecting, isFalse);
    expect(model.connectionVersion, 0);
    expect(model.canEditConnection, isFalse);
    expect(model.endpoint, 'http://192.168.1.90:32199');
    expect(host.secretReads, isNot(contains('connection-token')));
    expect(host.secrets['connection-token'], 'synthetic-unused-token');
    expect(host.preferences, originalPreferences);
  });

  test('启动连接即使握手失败也拒绝手工覆盖，无参重试仅使用原凭据', () async {
    final host = _LifecycleHost();
    host.launch.endpoint = 'http://127.0.0.1:32199';
    host.launch.token = 'synthetic-launch-token';
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    expect(model.connected, isFalse, reason: '隔离网络不提供可用 HTTP 宿主。');
    final generation = model.connectionVersion;
    final preferences = host.preferences;
    final secrets = Map.of(host.secrets);
    for (final endpoint in [model.endpoint, 'http://192.168.1.90:32199']) {
      await model.connect(endpoint, 'synthetic-manual-token', '不应保存');
      expect(model.error, contains('本次运行中不能修改'));
      expect(model.connectionVersion, generation);
      expect(model.endpoint, 'http://127.0.0.1:32199');
      expect(model.clientLabel, '游戏电脑');
      expect(host.preferences, preferences);
      expect(host.secrets, secrets);
    }
    await model.retryConnection();
    expect(model.connectionVersion, generation + 1);
    expect(model.connectionSource, ConnectionSource.launchArguments);
    expect(model.canEditConnection, isFalse);
    expect(host.preferences, preferences);
    expect(host.secrets, secrets);
    expect(model.error, isNot(contains('本次运行中不能修改')));
  });

  test('原生初始化返回之前排队的人工连接仍须按真实启动来源拒绝', () async {
    final host = _LifecycleHost()..initializeGate = Completer<void>();
    host.launch.endpoint = 'http://127.0.0.1:32199';
    host.launch.token = 'synthetic-launch-token';
    final originalPreferences = host.preferences;
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    final manual = model.connect(
      'http://192.168.1.90:32199',
      'synthetic-manual-token',
      '排队草稿',
    );
    await Future<void>.delayed(Duration.zero);
    host.initializeGate!.complete();
    await manual;
    expect(model.canEditConnection, isFalse);
    expect(model.error, contains('本次运行中不能修改'));
    expect(model.connectionVersion, 0);
    expect(host.preferences, originalPreferences);
    expect(host.secrets.containsKey('connection-token'), isFalse);
    await model.retryConnection();
    expect(model.connectionVersion, 1);
    expect(host.secrets['connection-token'], 'synthetic-launch-token');
  });

  test('启动后存储初始化失败仍保持来源锁，重试不重置身份', () async {
    final host = _LifecycleHost()..failPreferences = true;
    host.launch.endpoint = 'http://127.0.0.1:32199';
    host.launch.token = 'synthetic-launch-token';
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    expect(model.canEditConnection, isFalse);
    expect(model.connectionSource, ConnectionSource.launchArguments);
    expect(model.endpoint, host.launch.endpoint);
    expect(model.error, contains('“重试连接”'));
    expect(host.identityReads, 0);
    host.failPreferences = false;
    await model.connect(model.endpoint, 'synthetic-manual-token', '不得更改');
    expect(host.preferenceReads, 1, reason: '已确认锁定的人工调用不应触发新的初始化。');
    expect(model.connectionVersion, 0);
    await model.retryConnection();
    expect(model.connectionVersion, 1);
    expect(model.canEditConnection, isFalse);
    expect(model.error, isNot(contains('初始化失败')));
    expect(host.identityWrites, 0);
    expect(host.secrets['connection-token'], 'synthetic-launch-token');
  });

  test('连接来源锁不持久化，下一独立启动恢复的连接仍可修改', () async {
    final launchedHost = _LifecycleHost();
    launchedHost.launch.endpoint = 'http://127.0.0.1:32199';
    launchedHost.launch.token = 'synthetic-launch-token';
    final launched = LiveWorkbenchModel(
      platform: CompanionPlatform(host: launchedHost),
    );
    addTearDown(launched.dispose);
    await launched.initialize();
    final savedHost = _LifecycleHost()..preferences = launchedHost.preferences;
    savedHost.secrets.addAll(launchedHost.secrets);
    final saved = LiveWorkbenchModel(
      platform: CompanionPlatform(host: savedHost),
    );
    addTearDown(saved.dispose);
    await saved.initialize();
    expect(saved.connectionSource, ConnectionSource.saved);
    expect(saved.canEditConnection, isTrue);
    await saved.connect(
      'http://192.168.1.90:32199',
      'synthetic-new-token',
      '新设备',
    );
    expect(saved.connectionSource, ConnectionSource.manual);
    expect(saved.endpoint, 'http://192.168.1.90:32199');
    expect(savedHost.secrets['connection-token'], 'synthetic-new-token');
    expect(launched.canEditConnection, isFalse);
  });

  test('通用本机偏好不能旁路改写连接地址或设备名称', () async {
    final host = _LifecycleHost();
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    final before = Map.of(model.localPreferences);
    final stored = host.preferences;
    for (final patch in [
      {'endpoint': 'http://192.168.1.90:32199'},
      {'deviceLabel': '旁路修改', 'darkMode': true},
    ]) {
      await model.updateLocal(patch);
      expect(model.error, contains('连接信息只能通过连接设置修改'));
      expect(model.localPreferences, before);
      expect(host.preferences, stored);
      expect(model.connectionVersion, 0);
    }
  });

  test('同凭据原生激活即使无需重连也立即锁定旧人工连接入口', () async {
    final host = _LifecycleHost()
      ..preferences = jsonEncode({'endpoint': 'http://127.0.0.1:32199'});
    host.secrets['connection-token'] = 'synthetic-saved-token';
    final platform = _EventLifecyclePlatform(host: host);
    final model = LiveWorkbenchModel(platform: platform);
    addTearDown(() async {
      model.dispose();
      await platform.dispose();
    });
    await model.initialize();
    expect(model.canEditConnection, isTrue);
    final generation = model.connectionVersion;
    // 当前存在本机保存时，原生同凭据激活沿用现有会话，不安排离线重连。
    // 来源锁仍必须立即生效，不能依赖 _replaceConnection 才更新它。
    host.preferenceWriteGate = Completer<void>();
    final saving = model.updateLocal({'darkMode': true});
    await Future<void>.delayed(Duration.zero);
    platform.injectedEvents.add(
      _activation(model.endpoint, 'synthetic-saved-token'),
    );
    expect(model.canEditConnection, isFalse);
    expect(model.connectionSource, ConnectionSource.launchArguments);
    expect(model.connecting, isFalse);
    expect(model.connectionVersion, generation);
    host.preferenceWriteGate!.complete();
    await saving;
    final preferences = host.preferences;
    await model.connect(model.endpoint, 'synthetic-stale-token', '旧编辑器');
    expect(model.connectionVersion, generation);
    expect(model.error, contains('本次运行中不能修改'));
    expect(host.preferences, preferences);
    expect(host.secrets['connection-token'], 'synthetic-saved-token');
  });

  test('启动来源锁不会阻止原生完整参数的后续激活更新', () async {
    final host = _LifecycleHost();
    host.launch.endpoint = 'http://127.0.0.1:32199';
    host.launch.token = 'synthetic-first-token';
    final platform = _EventLifecyclePlatform(host: host);
    final model = LiveWorkbenchModel(platform: platform);
    addTearDown(() async {
      model.dispose();
      await platform.dispose();
    });
    await model.initialize();
    platform.injectedEvents.add(
      _activation('http://127.0.0.1:32200', 'synthetic-next-token'),
    );
    // 无参重试排在激活之后，只使用届时最新的受管连接；不重放第一次启动参数。
    await model.retryConnection();
    expect(model.endpoint, 'http://127.0.0.1:32200');
    expect(host.secrets['connection-token'], 'synthetic-next-token');
    expect(model.canEditConnection, isFalse);
    expect(model.connectionSource, ConnectionSource.launchArguments);
    expect(host.initializeCalls, 1);
  });

  test('人工换址已开始但旧会话尚未关闭时，同旧参数激活仍在队列末尾接管', () async {
    const originalEndpoint = 'http://127.0.0.1:32199';
    const originalToken = 'synthetic-original-token';
    final host = _LifecycleHost()
      ..preferences = jsonEncode({'endpoint': originalEndpoint});
    host.secrets['connection-token'] = originalToken;
    final platform = _EventLifecyclePlatform(host: host);
    final model = LiveWorkbenchModel(platform: platform);
    addTearDown(() async {
      model.dispose();
      await platform.dispose();
    });
    await model.initialize();
    final generation = model.connectionVersion;
    var activated = false;
    void activateDuringReplacement() {
      // 生产 _replaceConnection 在代次更新、撤回图片后通知，再 await 关闭旧会话；
      // 此回调精确命中人工操作已获准但地址和凭据仍是旧值的窗口，无计时猜测。
      if (activated || model.connectionVersion != generation + 1) return;
      activated = true;
      expect(model.endpoint, originalEndpoint);
      expect(model.connecting, isTrue);
      platform.injectedEvents.add(_activation(originalEndpoint, originalToken));
    }

    model.addListener(activateDuringReplacement);
    await model.connect(
      'http://127.0.0.1:32200',
      'synthetic-manual-token',
      '人工草稿',
    );
    model.removeListener(activateDuringReplacement);
    // 排在可信激活之后的无参重试仅用于等待队列排空，不能自行恢复最初参数。
    // 若激活被误判为“同地址无需处理”，这里将继续使用人工地址并明确失败。
    await model.retryConnection();
    expect(activated, isTrue);
    expect(model.endpoint, originalEndpoint);
    expect(host.secrets['connection-token'], originalToken);
    expect(
      (jsonDecode(host.preferences!) as Map)['endpoint'],
      originalEndpoint,
    );
    expect(model.connectionSource, ConnectionSource.launchArguments);
    expect(model.canEditConnection, isFalse);
  });

  test('首次原生轮询在启动连接期间报告同参数，不再排队第二次连接', () async {
    final host = _LifecycleHost();
    host.launch.endpoint = 'http://127.0.0.1:32199';
    host.launch.token = 'synthetic-launch-token';
    final platform = _EventLifecyclePlatform(host: host);
    final model = LiveWorkbenchModel(platform: platform);
    addTearDown(() async {
      model.dispose();
      await platform.dispose();
    });
    var initialActivationReported = false;
    void reportInitialActivation() {
      // 原生轮询从未读代次进入首次代次时也产生 connectionActivated；在生产
      // 换代通知处投递同参数，确定覆盖初次 HTTP 尚未结束的窗口，不靠固定延时。
      if (initialActivationReported || model.connectionVersion != 1) return;
      initialActivationReported = true;
      expect(model.connecting, isTrue);
      platform.injectedEvents.add(
        _activation(host.launch.endpoint!, host.launch.token!),
      );
    }

    model.addListener(reportInitialActivation);
    await model.initialize();
    model.removeListener(reportInitialActivation);
    expect(initialActivationReported, isTrue);
    expect(model.connectionVersion, 1);
    expect(model.connecting, isFalse, reason: '启动返回时不得残留同参数的第二次换代操作。');
    expect(model.connectionSource, ConnectionSource.launchArguments);
    expect(model.canEditConnection, isFalse);
    expect(host.identityReads, 1);
    expect(host.secrets['connection-token'], 'synthetic-launch-token');
  });

  test('热激活显式空 Token 不切地址、不读存储且不替换已有会话', () async {
    final host = _LifecycleHost()
      ..preferences = jsonEncode({'endpoint': 'http://127.0.0.1:32199'});
    host.secrets['connection-token'] = 'synthetic-existing-token';
    final platform = _EventLifecyclePlatform(host: host);
    final model = LiveWorkbenchModel(platform: platform);
    addTearDown(() async {
      model.dispose();
      await platform.dispose();
    });
    await model.initialize();
    final before = (
      model.endpoint,
      model.connectionVersion,
      model.connectionSource,
      host.preferences,
      host.preferenceReads,
      List.of(host.secretReads),
    );
    platform.injectedEvents.add(
      PlatformEvent(
        'connectionActivated',
        PlatformInput(
          focused: true,
          connected: false,
          buttons: 0,
          leftX: 0,
          leftY: 0,
          leftTrigger: 0,
          rightTrigger: 0,
          activationRevision: 1,
          launch: PlatformLaunch(
            desktop: true,
            endpoint: 'http://192.168.1.90:32199',
            token: '',
          ),
        ),
      ),
    );
    await Future<void>.delayed(Duration.zero);
    expect(
      (model.endpoint, model.connectionVersion, model.connectionSource),
      (before.$1, before.$2, before.$3),
    );
    expect(host.preferences, before.$4);
    expect(host.preferenceReads, before.$5);
    expect(host.secretReads, before.$6);
    expect(host.secrets['connection-token'], 'synthetic-existing-token');
    expect(model.connecting, isFalse);
  });

  test('原生初始化迟到时已关闭的模型不再读取或生成身份', () async {
    final host = _LifecycleHost()..initializeGate = Completer<void>();
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    final pending = model.initialize();
    await Future<void>.delayed(Duration.zero);
    model.dispose();
    host.initializeGate!.complete();
    await pending;
    expect(host.identityReads, 0);
    expect(host.identityWrites, 0);
  });

  test('旧版本已导入的模块偏好在新键缺失时迁移并耐久保存', () async {
    final host = _LifecycleHost()
      ..preferences = jsonEncode({
        'historyPreferences': {
          'mission-list-module-enabled': 'true',
          'rare-guest-invitation-module-enabled': 'true',
          'service-focus-compact': 'true',
        },
      });
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    expect(model.error, isEmpty);
    expect(model.localPreferences['missionListModuleEnabled'], isTrue);
    expect(model.localPreferences['rareGuestInvitationModuleEnabled'], isTrue);
    final saved = jsonDecode(host.preferences!) as Map<String, dynamic>;
    expect(saved['missionListModuleEnabled'], isTrue);
    expect(saved['rareGuestInvitationModuleEnabled'], isTrue);
    expect(saved.containsKey('inventoryEditorModuleEnabled'), isFalse);
    expect(saved.containsKey('focusMode'), isFalse);
  });

  test('历史模块偏好不得覆盖用户后来明确关闭的新键', () async {
    final host = _LifecycleHost()
      ..preferences = jsonEncode({
        'missionListModuleEnabled': false,
        'rareGuestInvitationModuleEnabled': false,
        'historyPreferences': {
          'mission-list-module-enabled': 'true',
          'rare-guest-invitation-module-enabled': 'true',
        },
      });
    final original = host.preferences;
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    expect(model.localPreferences['missionListModuleEnabled'], isFalse);
    expect(model.localPreferences['rareGuestInvitationModuleEnabled'], isFalse);
    expect(host.preferences, original);
  });

  test('显式导入历史模块开关直接激活本机偏好但不改变自动化配置', () async {
    final fixture = await Directory.systemTemp.createTemp(
      'mystia-local-modules-',
    );
    addTearDown(() => fixture.delete(recursive: true));
    final file = File('${fixture.path}/preferences.json');
    await file.writeAsString(
      jsonEncode({
        'schemaVersion': 1,
        'preferences': {
          'mystia-steward-companion-mission-list-module-enabled': '1',
          'rare-guest-invitation-module-enabled': '0',
          'service-focus-compact': '1',
          'theme-mode': 'system',
        },
      }),
    );
    final host = _LifecycleHost();
    final model = LiveWorkbenchModel(platform: CompanionPlatform(host: host));
    addTearDown(model.dispose);
    await model.initialize();
    await model.importHistory(file.path);
    expect(model.error, isEmpty);
    expect(model.localPreferences['missionListModuleEnabled'], isTrue);
    expect(model.localPreferences['rareGuestInvitationModuleEnabled'], isFalse);
    expect(model.localPreferences.containsKey('focusMode'), isFalse);
    expect(model.themeMode, 'system');
    expect(model.profile['automationEnabled'], isFalse);
    expect(await file.exists(), isTrue);
  });
}
