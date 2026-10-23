/// 平台边界的公开入口。业务 HTTP、设备权威和租约由 companion_client 管理。
library;

import 'dart:async';
import 'dart:convert';

import 'package:flutter/services.dart';

import 'src/platform.g.dart';
export 'src/platform.g.dart'
    show PlatformLaunch, WindowPreferences, PlatformInput, WindowAction;
export 'src/history_migration.dart';
export 'src/gamepad_input.dart';

/// 原生事件只包含当前连接及输入状态；不包含可重新执行的历史游戏动作。
class PlatformEvent {
  const PlatformEvent(this.kind, this.input);
  final String kind;
  final PlatformInput input;
}

/// 单一平台服务。所有持久化操作串行执行，避免 profile ACK 与 UI 偏好写入覆盖。
class CompanionPlatform {
  CompanionPlatform({PlatformHostApi? host})
    : _host = host ?? PlatformHostApi();
  static final instance = CompanionPlatform();
  final PlatformHostApi _host;
  final _events = StreamController<PlatformEvent>.broadcast();
  Stream<PlatformEvent> get events => _events.stream;
  Timer? _timer;
  Timer? _initializeDeadline, _initializeBackoff;
  Completer<PlatformLaunch>? _initializing;
  Future<void>? _disposing;
  bool _polling = false, _disposed = false;
  int _activation = -1;
  Future<void> _writeTail = Future<void>.value();

  /// Flutter 引擎可能先执行 Dart，再完成原生 Pigeon handler 注册。此处仅等待
  /// initialize 这个只读通道就绪；不能把存储失败、原生异常或其它通道错误当成竞态。
  /// 并发调用共用同一次结果，三秒总截止还覆盖原生调用本身一直不返回的情况。
  Future<PlatformLaunch> initialize() {
    if (_disposed) return Future.error(StateError('平台服务已关闭。'));
    final pending = _initializing;
    if (pending != null) return pending.future;
    final result = Completer<PlatformLaunch>();
    _initializing = result;
    PlatformException? lastNotReady;
    StackTrace? lastNotReadyStack;
    var backoffMs = 20;

    bool active() =>
        !_disposed && identical(_initializing, result) && !result.isCompleted;

    void fail(Object error, StackTrace? stack) {
      if (!active()) return;
      _cancelInitializeTimers();
      _initializing = null;
      result.completeError(error, stack);
    }

    Future<void> attempt() async {
      if (!active()) return;
      try {
        final launch = await _host.initialize();
        // 超时或 dispose 后的迟到回复只能丢弃，不能复活轮询或更改下一次初始化。
        if (!active()) return;
        _cancelInitializeTimers();
        _initializing = null;
        _timer ??= Timer.periodic(
          const Duration(milliseconds: 40),
          (_) => _poll(),
        );
        result.complete(launch);
      } catch (error, stack) {
        if (!active()) return;
        if (!_isInitializeChannelNotReady(error)) {
          fail(error, stack);
          return;
        }
        lastNotReady = error as PlatformException;
        lastNotReadyStack = stack;
        // 20/40/80/160ms 退避后保持160ms；总截止由独立 Timer 执行，不因重试续期。
        _initializeBackoff = Timer(Duration(milliseconds: backoffMs), () {
          _initializeBackoff = null;
          unawaited(attempt());
        });
        backoffMs = (backoffMs * 2).clamp(20, 160);
      }
    }

    _initializeDeadline = Timer(const Duration(seconds: 3), () {
      fail(
        lastNotReady ?? TimeoutException('原生初始化通道未在三秒内返回。'),
        lastNotReadyStack,
      );
    });
    unawaited(attempt());
    return result.future;
  }

  bool _isInitializeChannelNotReady(Object error) {
    // 精确匹配当前生成代码的缺少 handler 错误及本 HostApi 的通道后缀。
    // 仅判断 code 会误吞原生主动返回的 channel-error 或其它方法的异常。
    final channel =
        'dev.flutter.pigeon.companion_platform.PlatformHostApi.initialize'
        '${_host.pigeonVar_messageChannelSuffix}';
    return error is PlatformException &&
        error.code == 'channel-error' &&
        error.details == null &&
        error.message ==
            'Unable to establish connection on channel: "$channel".';
  }

  void _cancelInitializeTimers() {
    _initializeDeadline?.cancel();
    _initializeBackoff?.cancel();
    _initializeDeadline = null;
    _initializeBackoff = null;
  }

  Future<void> _poll() async {
    if (_polling || _disposed || _events.isClosed) return;
    _polling = true;
    try {
      final input = await _host.pollInput();
      if (_disposed || _events.isClosed) return;
      if (_activation != input.activationRevision) {
        _activation = input.activationRevision;
        _events.add(PlatformEvent('connectionActivated', input));
      }
      _events.add(PlatformEvent('input', input));
    } catch (error, stack) {
      // 平台失败必须让订阅者显式处理；绝不把读取失败当成空输入或成功。
      if (!_events.isClosed) _events.addError(error, stack);
    } finally {
      _polling = false;
    }
  }

  Future<void> applyWindowPreferences(WindowPreferences preferences) =>
      _host.applyWindowPreferences(preferences);

  /// true 表示原生端接受了请求（或吸收了冷却期重复输入），不保证目标已经绘制。
  /// Windows 跨进程激活异步完成，宿主确认目标前台后才执行可选的隐藏。
  Future<bool> toggleFocus() =>
      _host.performWindowAction(WindowAction.toggleFocus);
  Future<bool> show() => _host.performWindowAction(WindowAction.show);
  Future<bool> hide() => _host.performWindowAction(WindowAction.hide);
  Future<bool> exit() => _host.performWindowAction(WindowAction.exit);
  Future<String?> readSecret(String key) => _host.readSecret(key);
  Future<void> writeSecret(String key, String value) =>
      _serialize(() => _host.writeSecret(key, value));
  Future<void> deleteSecret(String key) =>
      _serialize(() => _host.deleteSecret(key));

  Future<Map<String, Object?>> readPreferences() async {
    await _writeTail;
    return _decode(await _host.readPreferences());
  }

  /// 更新传入的顶层字段，其余字段保留。原子写入及逐字节回读成功才完成 Future。
  Future<void> writePreferences(Map<String, Object?> fields) =>
      _serialize(() async {
        final current = _decode(await _host.readPreferences());
        current.addAll(fields);
        // 连接 Token 和 clientId 必须进入原生安全存储，禁止误写普通配置。
        if (current.keys.any(
          (key) => {'token', 'apiToken', 'clientId'}.contains(key),
        )) {
          throw ArgumentError('敏感连接身份必须写入安全存储。');
        }
        final json = jsonEncode(current);
        if (utf8.encode(json).length > 1024 * 1024)
          throw StateError('偏好数据超过容量限制。');
        await _host.writePreferences(json);
        if (await _host.readPreferences() != json)
          throw StateError('偏好持久化回读不一致，不能确认同步。');
      });

  Future<void> _serialize(Future<void> Function() action) {
    final next = _writeTail.catchError((Object _) {}).then((_) => action());
    _writeTail = next;
    return next;
  }

  static Map<String, Object?> _decode(String? raw) {
    if (raw == null) return <String, Object?>{};
    final value = jsonDecode(raw);
    if (value is! Map<String, dynamic>)
      throw const FormatException('偏好根对象损坏，原文件已保留。');
    return Map<String, Object?>.from(value);
  }

  Future<void> dispose() => _disposing ??= _dispose();

  Future<void> _dispose() async {
    _disposed = true;
    _cancelInitializeTimers();
    final pending = _initializing;
    _initializing = null;
    // 结束等待者而不是等待原生回复；只读调用可能仍返回，但 active() 会拒绝迟到结果。
    pending?.completeError(StateError('平台服务已关闭。'));
    _timer?.cancel();
    _timer = null;
    await _events.close();
  }
}
