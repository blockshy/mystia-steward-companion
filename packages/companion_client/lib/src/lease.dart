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

  /// 以本地单调时钟保守判断租约；服务端旧响应不能跨越权威修订或本地到期时间恢复许可。
  bool get owned =>
      _enabled &&
      error == null &&
      lease?.owned == true &&
      lease?.authorityRevision == _revision &&
      _deadline != null &&
      _deadline!.elapsedMilliseconds < lease!.ttlMs;

  /// 更换权威修订前先串行释放旧租约。意图序号保护等待期间发生的停用或再次启用，
  /// 运行代次则隔离已发送续约的迟到响应；两者分别约束用户意图与网络完成顺序。
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
      // 从发送前开始计时，把网络耗时计入 TTL，避免收到迟到响应后额外延长本地许可。
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

  /// 立即撤销本地可执行状态，再等待串行释放完成；释放失败仍保持禁用并保留错误。
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

  /// 队列尾部吸收异常仅为保证后续释放仍可运行；返回的 next 保留本次操作的失败语义。
  Future<void> _enqueue(Future<void> Function() operation) {
    final next = _tail.then((_) => operation());
    _tail = next.catchError((Object _) {});
    return next;
  }
}
