import 'dart:async';

import 'package:companion_protocol/companion_protocol.dart';

import 'api.dart';

/// 租约副作用串行器。停用先禁止新 acquire，等待已发送 acquire 结束，再发送 release。
/// UI 忽略迟到响应不能代替释放；未知结果保持不可执行，服务端 TTL 是最终撤销边界。
class AutomationLeaseController {
  AutomationLeaseController(this.api, {this.onChanged});
  final CompanionApi api;
  final void Function()? onChanged;
  AutomationLease? lease;
  Object? error;
  bool _enabled = false;
  int _revision = 0;
  int _generation = 0;
  int _intent = 0;
  Future<void> _tail = Future<void>.value();
  Timer? _timer;
  Stopwatch? _deadline;
  bool get owned =>
      _enabled &&
      error == null &&
      lease?.owned == true &&
      lease?.authorityRevision == _revision &&
      _deadline != null &&
      _deadline!.elapsedMilliseconds < lease!.ttlMs;

  Future<void> enable(int revision) async {
    if (revision <= 0) throw StateError('不能用空权限版本获取租约。');
    if (_enabled && _revision == revision) return;
    final intent = ++_intent;
    await _disable(cancelPendingEnable: false);
    if (intent != _intent) return;
    _enabled = true;
    _revision = revision;
    final generation = ++_generation;
    Future<void> renew() => _enqueue(() async {
      if (!_enabled || generation != _generation) return;
      final clock = Stopwatch()..start();
      try {
        final next = await api.acquireLease(revision);
        if (_enabled && generation == _generation) {
          lease = next;
          _deadline = clock;
          error = next.owned ? null : ApiException('lease-denied', next.error);
        }
      } catch (cause) {
        if (generation == _generation) {
          lease = null;
          _deadline = null;
          error = cause;
        }
      }
      onChanged?.call();
    });
    await renew();
    if (_enabled && generation == _generation) {
      _timer = Timer.periodic(const Duration(seconds: 3), (_) {
        // 排队前设置单飞标记；慢请求不能积累定时续租，在停用后也不能追加许可。
        if (_renewing) return;
        _renewing = true;
        unawaited(renew().whenComplete(() => _renewing = false));
      });
    }
  }

  bool _renewing = false;

  Future<void> disable() => _disable(cancelPendingEnable: true);
  Future<void> _disable({required bool cancelPendingEnable}) {
    if (cancelPendingEnable) ++_intent;
    final wasEnabled = _enabled;
    final revision = _revision;
    _enabled = false;
    ++_generation;
    _timer?.cancel();
    _timer = null;
    lease = null;
    _deadline = null;
    onChanged?.call();
    return _enqueue(() async {
      if (!wasEnabled || revision <= 0) return;
      try {
        await api.releaseLease(revision);
        error = null;
      } catch (cause) {
        error = cause;
      }
      onChanged?.call();
    });
  }

  Future<void> _enqueue(Future<void> Function() operation) {
    final next = _tail.then((_) => operation());
    _tail = next.catchError((Object _) {});
    return next;
  }
}
