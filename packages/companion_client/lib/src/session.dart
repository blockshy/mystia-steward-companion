import 'dart:async';

import 'package:companion_protocol/companion_protocol.dart';

import 'api.dart';
import 'lease.dart';
import 'order_presentation.dart';

/// 页面只读状态。business 是最新线路状态，displayBusiness 是同展示边界内最后成功结果。
/// canExecute 同时要求当前帧、主设备、已确认的自动化配置和有效 lease。
class CompanionState {
  const CompanionState({
    this.snapshot,
    this.catalog,
    this.authority,
    this.business,
    this.displayBusiness,
    this.pageResult,
    this.displayPageResult,
    this.lease,
    this.connected = false,
    this.loading = false,
    this.paused = false,
    this.error,
    this.pageError,
    this.canExecute = false,
    this.profileDirty = false,
  });
  final GameSnapshot? snapshot;
  final JsonMap? catalog;
  final DeviceAuthorityState? authority;
  final BusinessStatus? business;
  final BusinessStatus? displayBusiness;
  final PageResult? pageResult;
  final JsonMap? displayPageResult;
  final AutomationLease? lease;
  final bool connected, loading, paused, canExecute, profileDirty;
  final String? error;
  final String? pageError;

  /// 展示事实随快照立即发布，不等待 business/status 的慢计算。方案仍须同一会话、
  /// 场景及配置边界；此投影与 canExecute 完全分离，迟到的只读结果不获得执行资格。
  JsonMap get serviceOrders {
    if (!connected || snapshot == null) return const {};
    final context = authority == null
        ? null
        : BusinessSourceContext.fromSnapshot(snapshot!, authority!);
    final result = displayBusiness?.json['recommendations'];
    return projectLiveServiceOrders(
      snapshot!.json,
      result is JsonMap ? result : const {},
      contextMatches:
          context != null && displayBusiness?.sourceContext?.key == context.key,
      current:
          business?.currentFor(snapshot, context) == true &&
          displayBusiness?.currentFor(snapshot, context) == true,
    );
  }
}

/// 平台实现必须原子保存 currentDeviceProfile、身份与 revision/hash，并完成回读才返回。
/// 失败抛异常；会话不会发送 ACK、切主或自动获得执行许可。
typedef PersistProfile = Future<void> Function(DeviceAuthorityState state);

class CompanionSession {
  CompanionSession({
    required this.api,
    required this.platform,
    required this.appVersion,
    CompanionProfile? initialProfile,
    required this.persistProfile,
    this.prepareResources,
  }) : initialProfile = initialProfile ?? CompanionProfile.defaults() {
    _lease = AutomationLeaseController(api, onChanged: _notify);
  }
  final CompanionApi api;
  final String platform;
  final String appVersion;
  final CompanionProfile initialProfile;
  final PersistProfile persistProfile;

  /// 产品入口在每轮业务读取前确认 Mod 资源初始化；旧测试宿主可只验证业务契约。
  /// 未就绪仍保持连接与设备管理可用，但撤回业务展示和租约，不能继续自动执行。
  final Future<bool> Function()? prepareResources;
  late final AutomationLeaseController _lease;
  final _changes = StreamController<CompanionState>.broadcast(sync: true);
  Stream<CompanionState> get changes => _changes.stream;
  GameSnapshot? _snapshot;
  JsonMap? _catalog;
  String _catalogSignature = '';
  DeviceAuthorityState? _authority;
  BusinessStatus? _business, _displayBusiness;
  PageResult? _pageResult;
  JsonMap? _displayPageResult;
  PageQuery? _query;
  int _queryGeneration = 0, _queriedGeneration = -1;
  String? _pageSessionId;
  int _pageSequence = 0, _pageSessionGeneration = 0;
  bool _pageQueryVisible = true;
  bool _connected = false, _loading = false, _paused = false, _closed = false;
  bool _profileDirty = false;
  bool _handshakeReady = false;
  String? _error;
  String? _pageError;
  int _epoch = 0, _failures = 0;
  DateTime _authorityReadAt = DateTime.fromMillisecondsSinceEpoch(0);
  Timer? _pollTimer;
  Future<void>? _refreshing;
  Future<void> _deviceWrites = Future<void>.value();
  Future<void> _authorityCommits = Future<void>.value();
  String _persistedProfileKey = '';
  String _leaseScope = '';
  bool _resourcesReady = false;
  int _resourceEpoch = 0;
  bool get _resourcesAvailable => prepareResources == null || _resourcesReady;

  BusinessSourceContext? get _context => _snapshot != null && _authority != null
      ? BusinessSourceContext.fromSnapshot(_snapshot!, _authority!)
      : null;
  CompanionState get state => CompanionState(
    snapshot: _snapshot,
    catalog: _catalog,
    authority: _authority,
    business: _business,
    displayBusiness: _displayBusiness,
    pageResult: _pageResult,
    displayPageResult: _displayPageResult,
    lease: _lease.lease,
    connected: _connected,
    loading: _loading,
    paused: _paused,
    error: _error ?? _lease.error?.toString(),
    pageError: _pageError,
    profileDirty: _profileDirty,
    canExecute:
        _resourcesAvailable &&
        _connected &&
        !_paused &&
        !_profileDirty &&
        _error == null &&
        _authority?.currentDeviceIsPrimary == true &&
        _authority?.pendingSyncId == null &&
        _authority?.activeProfile.automationEnabled == true &&
        _snapshot?.nightActive == true &&
        _lease.owned &&
        _leaseScope == _currentLeaseScope &&
        _business?.currentFor(_snapshot, _context) == true,
  );

  // registry/revision 相同也可能已经重启游戏；进程会话是租约缓存必须覆盖的第三个维度。
  String get _currentLeaseScope =>
      '${_authority?.registryId}/${_authority?.authorityRevision}/${_snapshot?.automationSessionId}';

  /// 先完成新客户端握手，再恢复服务端保存的设备配置；新身份仍注册安全默认值。
  /// 已有主设备仅在配置耐久保存、同步 ACK 与完整读取成功后，按保存的自动化开关获取租约。
  Future<void> connect() async {
    if (_closed) throw StateError('会话已关闭。');
    final epoch = ++_epoch;
    _paused = false;
    _loading = true;
    _error = null;
    _handshakeReady = false;
    _connected = false;
    _resourcesReady = false;
    _resourceEpoch++;
    _pollTimer?.cancel();
    _notify();
    try {
      await Future.wait([_lease.disable(), _withdrawPageQuery()]);
      // 旧代次的 HTTP 最多等待自身超时；它结束前不能借用旧 single-flight Future 代替新刷新。
      await _refreshing;
      if (!_active(epoch)) return;
      final health = await api.read('/health');
      if (!_active(epoch)) return;
      if (jsonInt(health, 'clientProtocolVersion', fallback: 0) !=
          interactionProtocolVersion) {
        throw const ApiException('incompatible', '需要安装新客户端对应的成套 Mod。');
      }
      final authority = await api.register(
        platform: platform,
        appVersion: appVersion,
        profile: initialProfile,
      );
      if (!_active(epoch)) return;
      await _acceptAuthority(authority, epoch);
      if (!_active(epoch)) return;
      _handshakeReady = true;
      _connected = true;
      _loading = false;
      _notify();
      await refresh();
    } catch (cause) {
      if (_active(epoch)) {
        _connected = false;
        _loading = false;
        _error = cause.toString();
        _notify();
      }
      rethrow;
    }
  }

  /// 每轮读取单飞；完整 snapshot、目录和业务结果按会话/来源边界提交。
  Future<void> refresh() {
    if (_closed || _paused) return Future<void>.value();
    if (!_handshakeReady) {
      return Future<void>.error(
        const ApiException('handshake-required', '连接握手尚未成功，请重新连接。'),
      );
    }
    return _refreshing ??= _refresh().whenComplete(() => _refreshing = null);
  }

  Future<void> _refresh() async {
    final epoch = _epoch;
    final resourceEpoch = _resourceEpoch;
    _pollTimer?.cancel();
    try {
      final presentationSignature =
          _snapshot?.json['orderPresentationSignature'];
      final raw = await api.read(
        '/snapshot',
        query: {
          if (_snapshot?.signature.isNotEmpty == true)
            'knownSignature': _snapshot!.signature,
          // 只回传 C# 给出的展示令牌；订单排序不是客户端业务，也不能改写执行用的快照签名。
          if (presentationSignature is String &&
              presentationSignature.isNotEmpty)
            'knownOrderPresentationSignature': presentationSignature,
        },
      );
      if (!_active(epoch)) return;
      final previousSessionId = _snapshot?.automationSessionId;
      if (raw['unchanged'] == true) {
        if (_snapshot == null ||
            jsonString(raw, 'snapshotSignature') != _snapshot!.signature ||
            raw['orderPresentationSignature'] != presentationSignature) {
          _snapshot = GameSnapshot.fromJson(await api.read('/snapshot'));
        }
      } else {
        _snapshot = GameSnapshot.fromJson(raw);
      }
      if (!_active(epoch)) return;
      if (previousSessionId != _snapshot?.automationSessionId) {
        _leaseScope = '';
        _catalog = null;
        _catalogSignature = '';
        _displayBusiness = null;
        _displayPageResult = null;
        await _lease.disable();
        if (!_active(epoch)) return;
      }
      _clearDifferentContext();
      _notify();
      if (!_profileDirty &&
          DateTime.now().difference(_authorityReadAt).inSeconds >= 5) {
        await _acceptAuthority(await api.devices(), epoch);
      }
      if (!_active(epoch)) return;
      if (_snapshot!.runtimeDataComplete &&
          _snapshot!.runtimeDataSignature.isNotEmpty &&
          _catalogSignature != _snapshot!.runtimeDataSignature) {
        final signature = _snapshot!.runtimeDataSignature;
        final catalog = await api.read('/runtime-data');
        if (!_active(epoch)) return;
        _catalog = catalog;
        _catalogSignature = signature;
      }
      final resourcesReady =
          prepareResources == null || await prepareResources!();
      if (!_active(epoch)) return;
      if (resourceEpoch != _resourceEpoch) return;
      _resourcesReady = resourcesReady;
      if (!resourcesReady) {
        unawaited(_withdrawPageQuery());
        if (!_active(epoch)) return;
        _business = null;
        _displayBusiness = null;
        _pageResult = null;
        _displayPageResult = null;
        _leaseScope = '';
        await _lease.disable();
        if (!_active(epoch)) return;
        _connected = true;
        _loading = false;
        _error = null;
        _failures = 0;
        return;
      }
      if (!_active(epoch)) return;
      final next = await api.businessStatus();
      if (!_active(epoch) ||
          resourceEpoch != _resourceEpoch ||
          !_resourcesAvailable)
        return;
      _business = next;
      if (next.sourceContext?.key == _context?.key && _context != null) {
        if (next.error == null && next.recommendations != null)
          _displayBusiness = next;
      } else {
        // 线路状态可以先返回“尚无结果”的空信封，或返回当前快照之前捕获的旧作用域。
        // 它只能改变 _business 的当前性/错误，不能撤掉已经属于当前作用域的成功展示。
        // 真正场景、配置、目录或会话变化仍由相同核验立即清空；逐单身份/需求还会再次
        // 由 serviceOrders 联结，旧内容绝不参与租约、执行资格或已消失订单的成员判断。
        _clearDifferentContext();
      }
      final intent = _query;
      final queryGeneration = _queryGeneration;
      if (intent != null && _pageQueryVisible) {
        _queriedGeneration = queryGeneration;
        try {
          final page = await _readPage(intent, queryGeneration, epoch);
          if (!_active(epoch) ||
              resourceEpoch != _resourceEpoch ||
              !_resourcesAvailable)
            return;
          // 相同条件也可能经历 A→B→A，必须同时匹配请求代次，不能只比较内容 key。
          if (page != null &&
              _queryGeneration == queryGeneration &&
              _query?.key == intent.key) {
            _pageError = null;
            _acceptPage(page);
          }
        } catch (cause) {
          if (!_active(epoch) ||
              resourceEpoch != _resourceEpoch ||
              !_resourcesAvailable)
            return;
          // 页面推荐是附属读取。其计算/网络错误不能推翻本轮已成功的快照与业务状态，
          // 更不能撤销自动化租约；错误留在当前查询，由页面提供明确重试入口。
          if (_queryGeneration == queryGeneration)
            _pageError = cause.toString();
        }
      }
      if (resourceEpoch != _resourceEpoch || !_resourcesAvailable) return;
      _connected = true;
      _loading = false;
      _error = null;
      _failures = 0;
      await _reconcileLease();
    } catch (cause) {
      if (!_active(epoch) || resourceEpoch != _resourceEpoch) return;
      _connected = false;
      _loading = false;
      _error = cause.toString();
      _failures++;
      unawaited(_withdrawPageQuery());
      await _lease.disable();
    } finally {
      if (_active(epoch)) {
        _notify();
        final delays = [2, 5, 10, 30];
        final interval = _failures == 0
            ? const Duration(milliseconds: 750)
            : Duration(
                seconds: delays[(_failures - 1).clamp(0, delays.length - 1)],
              );
        _pollTimer = Timer(interval, () {
          unawaited(refresh());
        });
      }
    }
  }

  Future<void> _acceptAuthority(DeviceAuthorityState value, int epoch) {
    // 后台读取和人工 CAS 都可能返回权威状态；串行落盘/ACK，避免旧响应晚落盘覆盖新配置。
    final next = _authorityCommits.then((_) => _commitAuthority(value, epoch));
    _authorityCommits = next.catchError((Object _) {});
    return next;
  }

  Future<void> _commitAuthority(DeviceAuthorityState value, int epoch) async {
    if (!_active(epoch)) return;
    if (_authority?.registryId == value.registryId &&
        _authority!.stateRevision > value.stateRevision)
      return;
    var next = value;
    final key =
        '${next.registryId}/${next.currentDeviceId}/${next.currentDeviceProfileRevision}/${next.currentDeviceProfileHash}/${next.pendingSyncId}';
    if (key != _persistedProfileKey) {
      await persistProfile(next);
      if (!_active(epoch)) return;
      _persistedProfileKey = key;
    }
    if (next.pendingSyncId != null) {
      next = await api.acknowledgeSync(next);
      if (!_active(epoch)) return;
    }
    // 权威变化先停止租约，再更新所有页面状态；不允许旧 revision 延续执行资格。
    if (_authority != null &&
        (_authority!.registryId != next.registryId ||
            _authority!.authorityRevision != next.authorityRevision))
      await _lease.disable();
    if (!_active(epoch)) return;
    _authority = next;
    _authorityReadAt = DateTime.now();
    _clearDifferentContext();
    _notify();
  }

  Future<void> _reconcileLease() async {
    final authority = _authority;
    if (!_shouldOwnLease || authority == null) {
      _leaseScope = '';
      await _lease.disable();
      return;
    }
    final scope = _currentLeaseScope;
    if (_leaseScope != scope) {
      _leaseScope = '';
      await _lease.disable();
    }
    // 释放旧租约期间可能发生暂停、配置写入或权威变化；必须重新检查当前持久化配置。
    if (!_shouldOwnLease || scope != _currentLeaseScope) return;
    _leaseScope = scope;
    await _lease.enable(authority.authorityRevision);
  }

  /// 自动化意图只有已保存的 automationEnabled 一处来源，不再保留本进程临时开关。
  /// 配置开启不等于获得写入资格：尚未握手、同步未确认、查看设备、暂停或失联均不能续租。
  /// 白天可持有租约等待经营开始；实际执行还由 C# 场景检查及 state.canExecute 约束。
  bool get _shouldOwnLease =>
      _resourcesAvailable &&
      !_closed &&
      _handshakeReady &&
      _connected &&
      !_paused &&
      !_profileDirty &&
      _error == null &&
      _authority?.currentDeviceIsPrimary == true &&
      _authority?.pendingSyncId == null &&
      _authority?.activeProfile.automationEnabled == true &&
      _snapshot?.automationSessionId.isNotEmpty == true;

  /// 人工重读先同步撤销资源许可和可执行事实，再等待释放租约。
  /// 资源代次同时使已通过旧 prepareResources 的在途业务 HTTP 失效，防止其返回后重新续租。
  Future<void> invalidateResources() async {
    if (prepareResources == null) return;
    _resourceEpoch++;
    unawaited(_withdrawPageQuery());
    _resourcesReady = false;
    _leaseScope = '';
    _business = null;
    _displayBusiness = null;
    _pageResult = null;
    _displayPageResult = null;
    _notify();
    await _lease.disable();
  }

  /// 编辑配置先释放旧租约，使用单条 CAS 写入；未知结果不会重放，后续读取权威恢复。
  Future<void> updateProfile(CompanionProfile profile) =>
      _deviceMutation((epoch) async {
        final authority = _requireAuthority();
        if (!authority.currentDeviceIsPrimary)
          throw const ApiException('not-primary', '只有主设备可以修改生效配置。');
        final next = await api.updateProfile(authority, profile);
        await _acceptAuthority(next, epoch);
      });
  Future<void> syncFromPrimary({String? deviceId}) =>
      _deviceMutation((epoch) async {
        final authority = _requireAuthority();
        await _acceptAuthority(
          await api.syncFromPrimary(
            authority,
            deviceId ?? authority.currentDeviceId,
          ),
          epoch,
        );
      });
  Future<void> setPrimary(String deviceId) {
    final expected = _requireAuthority();
    return _deviceMutation((epoch) async {
      final fresh = await api.devices();
      if (!_active(epoch)) return;
      if (fresh.registryId != expected.registryId ||
          fresh.authorityRevision != expected.authorityRevision ||
          fresh.stateRevision != expected.stateRevision) {
        await _acceptAuthority(fresh, epoch);
        throw const ApiException('authority-changed', '设备或配置已经变化，请核对后重新选择主设备。');
      }
      await _acceptAuthority(await api.setPrimary(fresh, deviceId), epoch);
    });
  }

  Future<void> rename(String label) => _deviceMutation((epoch) async {
    await _acceptAuthority(await api.rename(label), epoch);
  });
  Future<void> forget(String deviceId) => _deviceMutation((epoch) async {
    await _acceptAuthority(
      await api.forget(_requireAuthority(), deviceId),
      epoch,
    );
  });
  Future<void> _deviceMutation(Future<void> Function(int epoch) operation) {
    final requestedEpoch = _epoch;
    final next = _deviceWrites.then((_) async {
      if (!_active(requestedEpoch))
        throw const ApiException('stale-session', '会话已变化，请重新操作。');
      _profileDirty = true;
      _notify();
      await _lease.disable();
      try {
        if (!_active(requestedEpoch))
          throw const ApiException('stale-session', '会话已变化，请重新操作。');
        await operation(requestedEpoch);
      } catch (cause) {
        _error = cause.toString();
        rethrow;
      } finally {
        _profileDirty = false;
        _authorityReadAt = DateTime.fromMillisecondsSinceEpoch(0);
        _notify();
      }
    });
    _deviceWrites = next.catchError((Object _) {});
    return next.then((_) => refresh());
  }

  DeviceAuthorityState _requireAuthority() {
    if (!_connected || _paused || _authority == null)
      throw const ApiException('not-connected', '请先连接游戏电脑。');
    return _authority!;
  }

  Future<PageResult?> query(PageQuery? intent) async {
    if (_query?.key != intent?.key) {
      unawaited(_withdrawPageQuery());
      _queryGeneration++;
      _query = intent;
      _pageResult = null;
      _pageError = null;
      _displayPageResult = null;
      _notify();
    }
    if (intent == null || !_connected || _paused || !_pageQueryVisible)
      return null;
    final generation = _queryGeneration;
    await refresh();
    // refresh 是单飞的：若刚等到的是旧条件的在途读取，为当前条件补一轮。
    // 每次调用最多补一次；pending 的业务计算继续交给既有轮询，不能忙循环。
    if (!_closed &&
        !_paused &&
        _connected &&
        generation == _queryGeneration &&
        _queriedGeneration != generation)
      await refresh();
    return generation == _queryGeneration ? _pageResult : null;
  }

  /// 打开、发送与接收三个阶段均校验本地代次。隐藏后迟到的 open 没有意图，
  /// 在途 update 则由更高序号的撤销阻断；响应不能把旧订阅或旧展示重新放回状态。
  Future<PageResult?> _readPage(
    PageQuery intent,
    int generation,
    int epoch,
  ) async {
    final sessionGeneration = _pageSessionGeneration;
    bool current() =>
        _active(epoch) &&
        _resourcesAvailable &&
        _pageQueryVisible &&
        _queryGeneration == generation &&
        _query?.key == intent.key &&
        _pageSessionGeneration == sessionGeneration;
    var id = _pageSessionId;
    if (id == null) {
      id = await api.openPageSession();
      if (!current()) return null;
      _pageSessionId = id;
      _pageSequence = 0;
    }
    if (!current()) return null;
    try {
      final result = await api.updatePageSession(id, ++_pageSequence, intent);
      return current() ? result : null;
    } on ApiException catch (error) {
      // 服务端重启、容量淘汰或长时间暂停后，下一轮只能重新打开空会话；
      // 不能重放旧序号，也不能由更新请求自动重建已撤销的服务端槽位。
      if (error.message == 'page-session-expired' && _pageSessionId == id) {
        _pageSessionId = null;
        _pageSessionGeneration++;
      }
      rethrow;
    }
  }

  /// 先同步撤回本地身份，再发只读撤销。网络失败由服务端 15 秒意图 TTL 收口，
  /// 不把页面撤销失败升级成自动化权限失败，也不允许旧返回重新保存此会话。
  Future<void> _withdrawPageQuery() async {
    final id = _pageSessionId;
    final sequence = ++_pageSequence;
    _pageSessionId = null;
    _pageSessionGeneration++;
    if (id == null) return;
    try {
      await api.updatePageSession(id, sequence, null);
    } catch (_) {
      // 这是附属只读订阅；断线时无法确认撤销，服务端截止时间是最终安全边界。
    }
  }

  /// Windows 隐藏窗口只暂停页面推荐；经营状态与自动化租约继续按原周期维护。
  /// 保留已选条件，恢复显示后由下一轮正常轮询重新打开订阅，不通过恢复动作重放旧请求。
  void setPageQueryVisible(bool visible) {
    if (_pageQueryVisible == visible) return;
    _pageQueryVisible = visible;
    _queryGeneration++;
    if (!visible) unawaited(_withdrawPageQuery());
  }

  void _acceptPage(PageResult page) {
    _pageResult = page;
    if (_context != null && page.sourceContext?.key == _context!.key) {
      _displayPageResult = page.result ?? _displayPageResult;
    } else {
      _displayPageResult = null;
    }
  }

  void _clearDifferentContext() {
    final context = _context;
    if (context == null || _displayBusiness?.sourceContext?.key != context.key)
      _displayBusiness = null;
    if (context == null || _pageResult?.sourceContext?.key != context.key) {
      _pageResult = null;
      _displayPageResult = null;
    }
  }

  /// 人工确认必须绑定“打开确认界面时”的业务边界，而不是发送时重新绑定当前会话。
  /// sequence 在游戏重启后可以重用；边界改变后即便已获得新 lease，也必须重新查看并确认。
  Future<void> acknowledge(
    int sequence, {
    required String expectedSessionId,
    required int expectedGeneration,
  }) async {
    final snapshot = _snapshot;
    if (expectedSessionId.isEmpty ||
        expectedGeneration <= 0 ||
        snapshot?.automationSessionId != expectedSessionId ||
        snapshot?.nightBusinessGeneration != expectedGeneration) {
      throw const ApiException(
        'interaction-changed',
        '人工确认期间游戏或本次经营状态已变化，请重新查看并确认。',
      );
    }
    if (!state.canExecute)
      throw const ApiException('not-current', '当前状态暂不允许此操作，请刷新后重试。');
    // 前置检查与构造 HTTP 正文之间没有 await，避免校验后的另一次事件循环重绑定。
    await api.acknowledge(sequence, _authority!, snapshot!);
    await refresh();
  }

  Future<void> retry(String kind, String key) async {
    if (!state.canExecute)
      throw const ApiException('not-current', '当前状态暂不允许此操作，请刷新后重试。');
    await api.retry(kind, key, _authority!, _snapshot!);
    await refresh();
  }

  /// 页面后台、明确暂停和退出复用同一释放流程；Windows 托盘隐藏无需调用本方法。
  Future<void> pause() async {
    _paused = true;
    ++_epoch;
    _pollTimer?.cancel();
    _pollTimer = null;
    _connected = false;
    _notify();
    await Future.wait([_lease.disable(), _withdrawPageQuery()]);
  }

  Future<void> resume() => connect();
  Future<void> close() async {
    if (_closed) return;
    await pause();
    _closed = true;
    api.close();
    await _changes.close();
  }

  bool _active(int epoch) => !_closed && !_paused && epoch == _epoch;
  void _notify() {
    if (!_closed && !_changes.isClosed) _changes.add(state);
  }
}
