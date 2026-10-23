import 'dart:async';
import 'dart:io';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:companion_client/companion_client.dart';
import 'package:companion_platform/companion_platform.dart';

import 'workbench_model.dart';
import 'available_mission_resource.dart';
import 'game_media_controller.dart';

/// 应用组合根：只协调会话、持久化和页面命令，不实现推荐或自动化算法。
class LiveWorkbenchModel extends WorkbenchModel with WidgetsBindingObserver {
  LiveWorkbenchModel({CompanionPlatform? platform})
    : platform = platform ?? CompanionPlatform.instance {
    _gameMedia.addListener(_changed);
  }
  final CompanionPlatform platform;
  final _gameMedia = GameMediaController();
  CompanionSession? _session;
  CompanionState _state = const CompanionState();
  StreamSubscription<CompanionState>? _states;
  StreamSubscription<PlatformEvent>? _platformEvents;
  Timer? _resourceTimer;
  Timer? _updateTimer;
  Future<void>? _updateRead;
  bool _updateReadReportError = false;
  DataMap _updateStatus = {};
  int _updateGeneration = 0;
  int _lastUpdateReadAt = -30000;
  bool _disposed = false, _desktop = false, _initialized = false;
  bool _pageQueryVisible = true;
  int _pending = 0, _epoch = 0, _resourceGeneration = 0;
  String _initializationError = '',
      _error = '',
      _notice = '',
      _endpoint = 'http://127.0.0.1:32145',
      _clientLabel = '管家客户端';
  String _clientId = '', _token = '', _inputAction = '';
  int _inputSequence = 0;
  final _gamepad = GamepadInputController();
  bool _gamepadInputActive = false;
  final _inputClock = Stopwatch()..start();
  Future<void> _connectionTail = Future<void>.value();
  int _connectionRequests = 0;
  int _manualConnectionRequests = 0;
  ConnectionSource _connectionSource = ConnectionSource.unknown;
  // 来源锁仅属于当前进程，不能写入偏好；下次独立启动恢复的连接仍可手动修改。
  // 单独保留锁，避免原生激活到达时，正在收尾的旧连接操作重新覆盖来源状态。
  bool _launchConnectionManaged = false;
  bool _launchConnectionIncomplete = false;
  int? _launchGamePid;
  // 只有收到当前本机管理请求的成功回执，才退休精确的旧凭据组。仍接受其他地址
  // 或新 Token 的合法原生激活，不能用一个永久开关忽略所有后续启动参数。
  final Set<(String, String)> _retiredLaunchPairs = {};
  final Set<String> _redactedTokens = {};
  final List<WorkbenchOperation> _operations = [];
  int _operationSequence = 0;
  // 设置控件自动提交后，必须在队列真正执行时合并最新权威配置。
  // 只串行 HTTP 写入还不够：在入队前组装完整 profile 会覆盖前一项刚保存的值。
  Future<void> _profileTail = Future<void>.value();
  int _profileWriteGeneration = 0;
  Future<void> _localTail = Future<void>.value();
  final Map<String, int> _localFieldVersions = {};
  DataMap _savedLocal = {'alwaysOnTop': true, 'gamepadNavigation': true};
  DataMap _local = {'alwaysOnTop': true, 'gamepadNavigation': true};
  final Map<String, DataMap> _resources = {};
  final Map<String, Map<String, String>> _resourceQueries = {};
  final Map<String, Map<String, String>> _resourceResultQueries = {};
  // 单飞属于一次连接、订阅和经营上下文，不能让已经撤销的旧 GET 挡住新页面读取。
  // 旧请求只允许自行结束；finally 按同一身份释放，绝不能清掉后来请求的占位。
  final Map<String, (int, int, String)> _reading = {};
  int _recommendationGeneration = 0;
  bool _recommendationActive = false, _recommendationPending = false;
  String _recommendationError = '';

  @override
  DataMap get snapshot => _state.snapshot?.json ?? const {};
  @override
  DataMap get serviceOrders => _state.serviceOrders;
  @override
  DataMap get business => {
    ...?_state.displayBusiness?.json,
    'automation': _state.business?.automation ?? const <String, Object?>{},
    'pending': _state.business?.pending ?? false,
    'isCurrent': _state.business?.isCurrent ?? false,
    // 成功展示树可能来自上一轮；计算活动与错误必须来自最新线路状态，不能沿用
    // displayBusiness 的旧值而持续转圈或掩盖失败。两字段仅供提示，不授予执行权。
    'isCalculating': _state.business?.json['isCalculating'] == true,
    'error': _state.business?.error,
  };
  @override
  DataMap get catalog => _state.catalog ?? const {};
  @override
  DataMap get authority => _state.authority?.json ?? const {};
  @override
  DataMap get profile =>
      _state.authority?.activeProfile.json ?? CompanionProfile.defaults().json;
  @override
  DataMap get pageResult => _state.displayPageResult ?? const {};
  @override
  bool get recommendationLoading =>
      _recommendationActive &&
      (_recommendationPending ||
          (_state.pageError?.isNotEmpty != true &&
              _state.pageResult?.error?.isNotEmpty != true &&
              _state.pageResult?.pending == true));
  @override
  String get recommendationError => !_recommendationActive
      ? ''
      : _recommendationError.isNotEmpty
      ? _recommendationError
      : _safeError(_state.pageError ?? _state.pageResult?.error ?? '');
  @override
  DataMap get localPreferences => Map.unmodifiable(_local);
  @override
  Map<String, DataMap> get resources => Map.unmodifiable({
    ..._resources,
    if (_updateStatus.isNotEmpty) '/updates/status': _updateStatus,
  });
  @override
  DataMap get gameMediaStatus => _gameMedia.status;
  @override
  List<DataMap> get gameMediaItems => _gameMedia.items;
  @override
  Map<String, DataMap> get gameMediaRecipes => _gameMedia.recipes;
  @override
  int get gameMediaRevision => _gameMedia.revision;
  @override
  ImageProvider<Object>? gameMediaImage(String namespace, int id) =>
      _gameMedia.imageFor(namespace, id);
  @override
  bool get connected => _state.connected;
  @override
  bool get busy => _pending > 0 || _state.profileDirty;
  @override
  bool get connecting => _connectionRequests > 0;
  @override
  ConnectionSource get connectionSource => _launchConnectionManaged
      ? ConnectionSource.launchArguments
      : _connectionSource;
  @override
  bool get canEditConnection => !_launchConnectionManaged;
  @override
  String get editableConnectionToken =>
      !_disposed && _initialized && canEditConnection ? _token : '';
  @override
  List<WorkbenchOperation> get recentOperations =>
      List.unmodifiable(_operations);
  @override
  bool get desktop => _desktop;
  @override
  bool get canExecute => _state.canExecute && !busy && gameMediaReady;
  @override
  String get error => _safeError(
    _initializationError.isNotEmpty
        ? _initializationError
        : _error.isNotEmpty
        ? _error
        : _state.error ??
              _state.business?.error ??
              _state.pageResult?.error ??
              '',
  );
  @override
  String get notice => _notice;
  @override
  String get endpoint => _endpoint;
  @override
  String get clientLabel => _clientLabel;
  @override
  String get inputAction => _inputAction;
  @override
  int get inputSequence => _inputSequence;
  @override
  int get connectionVersion => _epoch;

  /// 启动与人工重连共用串行入口；原生准备未完成时不能用空设备身份创建会话。
  Future<void> initialize() => _queueConnection(() async {
    await _ensureInitialized();
    if (_launchConnectionIncomplete)
      throw StateError('启动连接参数不完整，请通过游戏重新启动客户端，或关闭后独立启动以手动连接。');
    if (_token.isNotEmpty && !connected)
      await _replaceConnection(
        _endpoint,
        _token,
        _clientLabel,
        source: _connectionSource,
      );
  });

  @override
  Future<void> retryConnection() => _queueConnection(() async {
    await _ensureInitialized();
    if (_launchConnectionIncomplete)
      throw StateError('启动连接参数不完整，请通过游戏重新启动客户端。');
    if (_token.isEmpty) throw StateError('请填写连接 Token 后再连接。');
    await _replaceConnection(
      _endpoint,
      _token,
      _clientLabel,
      source: _connectionSource,
    );
    _requireConnectedResult();
  }, operationName: '重试连接');

  /// 初始化错误独立保留，切页、保存外观和平台输入都不能将其清除。
  /// 重试逐阶段重新读取原始存储，绝不以删除身份或重置偏好的方式绕过损坏数据。
  Future<void> _ensureInitialized() async {
    if (_initialized) return;
    var phase = '原生平台';
    try {
      await _initializeLocal((value) => phase = value);
      if (_disposed) throw StateError('客户端已关闭。');
      _initialized = true;
      _initializationError = '';
    } catch (cause) {
      _initializationError =
          '初始化失败（$phase）：${_safeError(cause)}。请在连接设置中点击“${canEditConnection ? '保存并连接' : '重试连接'}”重试。';
      rethrow;
    }
  }

  Future<void> _initializeLocal(void Function(String) phase) async {
    final launch = await platform.initialize();
    if (_disposed) throw StateError('客户端已关闭。');
    _desktop = launch.desktop;
    _launchGamePid = launch.gamePid;
    // 在任何存储 await 之前记录来源。即使偏好或身份读取失败，页面和排队中的
    // 人工连接也不能将一次由启动参数管理的连接误判为可编辑的独立客户端。
    if (launch.endpoint != null || launch.token != null) {
      _launchConnectionManaged = true;
      _launchConnectionIncomplete =
          (launch.endpoint == null) != (launch.token == null);
      _connectionSource = ConnectionSource.launchArguments;
      _endpoint = launch.endpoint ?? _endpoint;
    }
    // 凭据仅用于连接与错误脱敏，不写入普通偏好。
    _token = launch.token ?? _token;
    _rememberToken(_token);
    if (_launchConnectionManaged) _changed();
    phase('读取本机偏好');
    _local = {..._local, ...await platform.readPreferences()};
    _savedLocal = Map.of(_local);
    if (_disposed) throw StateError('客户端已关闭。');
    // 旧版本只把历史模块开关存入 historyPreferences。仅补齐从未设置的新键，
    // 用户随后在当前客户端明确关闭的值必须保留，不能每次启动都被历史导入覆盖。
    final historical = object(_local['historyPreferences']);
    final moduleDefaults = <String, Object?>{};
    for (final entry in const {
      'mission-list-module-enabled': 'missionListModuleEnabled',
      'rare-guest-invitation-module-enabled':
          'rareGuestInvitationModuleEnabled',
    }.entries) {
      if (!_local.containsKey(entry.value) &&
          (historical[entry.key] == 'true' || historical[entry.key] == 'false'))
        moduleDefaults[entry.value] = historical[entry.key] == 'true';
    }
    if (moduleDefaults.isNotEmpty) {
      phase('迁移本机模块偏好');
      await platform.writePreferences(moduleDefaults);
      if (_disposed) throw StateError('客户端已关闭。');
      _local.addAll(moduleDefaults);
      _savedLocal.addAll(moduleDefaults);
    }
    phase('读取设备身份');
    _clientId = await platform.readSecret('client-id') ?? '';
    if (_disposed) throw StateError('客户端已关闭。');
    if (_clientId.isEmpty) {
      // 新客户端独立生成随机身份；绝不复用旧进程 clientId，避免两个进程争用同一租约。
      final random = Random.secure();
      _clientId = List.generate(
        24,
        (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
      ).join();
      phase('保存设备身份');
      await platform.writeSecret('client-id', _clientId);
      if (await platform.readSecret('client-id') != _clientId)
        throw StateError('设备身份未能耐久保存。');
    }
    _clientLabel = label(
      _local['deviceLabel'],
      _desktop ? '游戏电脑' : 'Android 伴随设备',
    );
    final savedEndpoint = label(_local['endpoint'], _endpoint);
    _endpoint = launch.endpoint ?? savedEndpoint;
    phase('读取连接凭据');
    // 启动参数必须构成独立的一组，不能拿已保存的另一组凭据补齐缺失部分。
    // 显式空 Token 仍表示离线启动，不读取或覆写已有的安全存储。
    _token = _launchConnectionManaged
        ? launch.token ?? ''
        : await platform.readSecret('connection-token') ?? '';
    _rememberToken(_token);
    // 只记录实际读取路径；Windows、回环地址或主设备身份都不能证明由游戏启动。
    _connectionSource = _launchConnectionManaged
        ? ConnectionSource.launchArguments
        : _token.isNotEmpty
        ? ConnectionSource.saved
        : ConnectionSource.unknown;
    if (_disposed) throw StateError('客户端已关闭。');
    phase('应用窗口设置');
    await _applyWindow();
    if (_disposed) throw StateError('客户端已关闭。');
    WidgetsBinding.instance.addObserver(this);
    _platformEvents = platform.events.listen(
      _onPlatformEvent,
      onError: (Object cause) {
        _error = _safeError(cause);
        _changed();
      },
    );
    _resourceTimer = Timer.periodic(const Duration(seconds: 5), (_) {
      if (!connected || !_desktop && _state.paused) return;
      for (final entry in _resourceQueries.entries.toList()) {
        unawaited(readResource(entry.key, query: entry.value));
      }
    });
    // 更新提醒属于客户端全局只读状态，不与可见页面抢占资源订阅，也不触发更新检查。
    // 后台服务决定检查频率；这里仅低频读取已存在状态，手机退后台时停止读取。
    _updateTimer = Timer.periodic(const Duration(seconds: 2), (_) {
      final phase = _updateStatus['state'];
      final install = label(_updateStatus['installState'], '');
      final active =
          phase == 'checking' ||
          phase == 'downloading' ||
          phase == 'idle' && _updateStatus['autoCheck'] == true ||
          install.isNotEmpty &&
              !{'succeeded', 'failed', 'cancelled'}.contains(install);
      // 检查/下载进行中两秒刷新，稳定状态三十秒；用户停留更新页也无需反复手动刷新。
      if (connected &&
          (_desktop || !_state.paused) &&
          _inputClock.elapsedMilliseconds - _lastUpdateReadAt >=
              (active ? 2000 : 30000)) {
        unawaited(_refreshUpdateStatus());
      }
    });
  }

  /// 比较经过协议校验的实际地址；无效或未知旧地址不能作为复用安全凭据的依据。
  bool _sameEndpoint(String first, String second) {
    try {
      return validateEndpoint(first) == validateEndpoint(second);
    } on FormatException {
      return false;
    }
  }

  /// 同一进程内的激活凭据与人工连接严格串行；旧会话先停止，再注册新连接。
  @override
  Future<void> connect(String endpoint, String token, String deviceLabel) =>
      _connectFrom(endpoint, token, deviceLabel, ConnectionSource.manual);

  Future<void> _connectFrom(
    String endpoint,
    String token,
    String deviceLabel,
    ConnectionSource source, {
    int? launchGamePid,
  }) => _queueConnection(
    () async {
      _rememberToken(token);
      if (source == ConnectionSource.launchArguments &&
          launchGamePid != null &&
          launchGamePid != _launchGamePid)
        return;
      if (source == ConnectionSource.launchArguments &&
          _isRetiredPair(endpoint, token))
        return;
      if (source == ConnectionSource.manual) _requireEditableConnection();
      await _ensureInitialized();
      // 调用时原生初始化可能尚未返回，必须在执行队列内再次检查刚确认的来源。
      // 拒绝发生在会话代次、凭据及本机偏好变更之前，旧表单回调也走相同边界。
      if (source == ConnectionSource.manual) _requireEditableConnection();
      final normalized = validateEndpoint(endpoint).toString();
      // 同一规范地址留空Token表示沿用安全存储；新地址必须显式提供凭据。
      // 在替换会话和持久化之前拒绝跨地址复用，不能把旧Token发送到另一台电脑。
      if (token.isEmpty && !_sameEndpoint(normalized, _endpoint))
        throw StateError('更换 API 地址时请填写对应的连接 Token。');
      if (source == ConnectionSource.launchArguments)
        _launchConnectionIncomplete = false;
      await _replaceConnection(
        normalized,
        token.isEmpty ? _token : token,
        deviceLabel,
        source: source,
      );
      _requireConnectedResult();
      if (source == ConnectionSource.manual) _retiredLaunchPairs.clear();
    },
    manual: source == ConnectionSource.manual,
    operationName: source == ConnectionSource.manual ? '保存并连接' : null,
  );

  void _requireConnectedResult() {
    if (!connected)
      throw StateError(_safeError(_state.error ?? '连接尚未成功，请检查地址和 Token。'));
  }

  bool _isRetiredPair(String endpoint, String token) {
    try {
      return _retiredLaunchPairs.contains((
        validateEndpoint(endpoint).toString(),
        token,
      ));
    } on FormatException {
      return false;
    }
  }

  void _requireEditableConnection() {
    if (!canEditConnection)
      throw StateError('连接由启动参数管理，本次运行中不能修改；请使用“重试连接”沿用原连接。');
  }

  Future<void> _queueConnection(
    Future<void> Function() operation, {
    bool manual = false,
    String? operationName,
    bool Function()? wasConfirmed,
  }) {
    // 入队即公开等待状态，覆盖下一微任务开始前的快速重复点击及原生激活等待。
    _connectionRequests++;
    // 首次平台轮询也会报告当前启动参数，不能把初始化本身误当成人工换址，
    // 否则初始化完成后会再建一次会话，清掉刚发布的身份和正在进行的同步。
    if (manual) _manualConnectionRequests++;
    _changed();
    final next = _connectionTail
        .then(
          (_) => _guard(
            operation,
            operationName: operationName,
            acrossConnection: true,
            wasConfirmed: wasConfirmed,
          ),
        )
        .whenComplete(() {
          _connectionRequests--;
          if (manual) _manualConnectionRequests--;
          _changed();
        });
    _connectionTail = next.catchError((Object _) {});
    return next;
  }

  Future<void> _replaceConnection(
    String endpoint,
    String token,
    String deviceLabel, {
    required ConnectionSource source,
  }) async {
    _rememberToken(token);
    final connection = ApiConnection(
      endpoint: endpoint,
      token: token,
      clientId: _clientId,
      clientLabel: deviceLabel.trim(),
    );
    ++_epoch;
    _updateGeneration++;
    _updateRead = null;
    _updateStatus = {};
    _gameMedia.bind(null);
    _notice = '';
    await _states?.cancel();
    await _session?.close();
    if (_disposed) throw StateError('客户端已关闭。');
    _state = const CompanionState();
    _resources.clear();
    _resourceResultQueries.clear();
    _resourceQueries.clear();
    _endpoint = connection.endpoint.toString();
    _clientLabel = connection.clientLabel;
    _token = token;
    _connectionSource = source;
    await platform.writeSecret('connection-token', token);
    if (await platform.readSecret('connection-token') != token)
      throw StateError('连接凭据未能耐久保存，请重新保存连接；不要重复提交服务器设置。');
    if (_disposed) throw StateError('客户端已关闭。');
    await platform.writePreferences({
      'schemaVersion': 1,
      'endpoint': _endpoint,
      'deviceLabel': _clientLabel,
    });
    if (_disposed) throw StateError('客户端已关闭。');
    final stored = object(_local['profileCheckpoint']);
    final initial = stored['endpoint'] == _endpoint && stored['profile'] is Map
        ? CompanionProfile.fromCheckpoint(
            object(stored['profile']),
            stored['profileSchemaVersion'],
          )
        : CompanionProfile.defaults();
    final api = CompanionApi(connection: connection);
    _gameMedia.bind(api);
    final session = CompanionSession(
      api: api,
      prepareResources: _gameMedia.synchronize,
      platform: _desktop ? 'windows' : 'android',
      appVersion: '1.3.1-flutter',
      initialProfile: initial,
      persistProfile: (state) async {
        final checkpoint = <String, Object?>{
          'endpoint': _endpoint,
          'profileSchemaVersion': CompanionProfile.schemaVersion,
          'registryId': state.registryId,
          'currentDeviceId': state.currentDeviceId,
          'pendingSyncId': state.pendingSyncId,
          'profileRevision': state.currentDeviceProfileRevision,
          'profileHash': state.currentDeviceProfileHash,
          'profile': state.currentDeviceProfile.json,
        };
        await platform.writePreferences({'profileCheckpoint': checkpoint});
        _local['profileCheckpoint'] = checkpoint;
      },
    );
    _session = session;
    if (_desktop) session.setPageQueryVisible(_pageQueryVisible);
    _states = session.changes.listen((state) {
      // 所属设备或连接有效性变化时，尚未提交的旧编辑失效；普通 profile ACK
      // 不改变这些身份字段，因此不会误取消同一用户连续调整的不同数值。
      final oldIdentity = (
        connected,
        authority['registryId'],
        authority['currentDeviceId'],
        authority['primaryDeviceId'],
      );
      final nextAuthority = state.authority?.json ?? const <String, Object?>{};
      final newIdentity = (
        state.connected,
        nextAuthority['registryId'],
        nextAuthority['currentDeviceId'],
        nextAuthority['primaryDeviceId'],
      );
      if (oldIdentity != newIdentity) _profileWriteGeneration++;
      final oldBoundary =
          '${snapshot['automationSessionId']}/${snapshot['runtimeDaySceneGeneration']}/${snapshot['nightBusinessGeneration']}';
      final oldSessionId = snapshot['automationSessionId'];
      final wasConnected = connected;
      _state = state;
      if (wasConnected && !state.connected) {
        // 断线后可能恢复到完全相同的游戏会话与场景。保留可见页面的订阅意图，
        // 但撤销旧在途读取身份，恢复时首读不能被旧连接尚未结束的单飞挡住。
        _resourceGeneration++;
      }
      if ((wasConnected && !state.connected) ||
          (oldSessionId != null &&
              oldSessionId != snapshot['automationSessionId'])) {
        // HTTP 会话重试仍可能连接同一个地址的新游戏进程，必须重新确认资源代次。
        _gameMedia.bind(api);
        _updateGeneration++;
        _updateRead = null;
        _updateStatus = {};
      }
      final newBoundary =
          '${snapshot['automationSessionId']}/${snapshot['runtimeDaySceneGeneration']}/${snapshot['nightBusinessGeneration']}';
      if (oldBoundary != newBoundary) _resources.clear();
      _changed();
    });
    await session.connect();
    // 首次快照读取也可能失败；只有会话已确认可读时才显示成功提示。
    if (session.state.connected) _notice = '已连接，自动化按已保存配置运行。';
    if (session.state.connected) {
      unawaited(_refreshUpdateStatus());
    }
  }

  @override
  Future<void> refresh() => _guard(() async {
    final session = _session;
    if (session == null) throw StateError('请先填写连接信息。');
    final epoch = _epoch;
    await session.refresh();
    if (_disposed || epoch != _epoch || !identical(session, _session)) return;
    // Session 的周期读取将故障写入 state，以便下次定时重试；其 Future 不一定
    // 抛错。人工刷新必须检查真实结果，不能把“Future 已结束”记录为刷新成功。
    if (!session.state.connected || session.state.error?.isNotEmpty == true)
      throw ApiException(
        'refresh-failed',
        session.state.error ?? '刷新未完成，请稍后重试。',
      );
  }, operationName: '刷新当前状态');

  /// 监听配置只允许游戏电脑的主设备管理，但不受“连接由启动参数管理”的编辑锁
  /// 限制。旧会话先停止轮询并释放租约，管理 POST 只发送一次；成功回执确认实际
  /// Token 后才保存并使用同一个 client-id 重建会话，绝不生成新的设备身份。
  @override
  Future<void> updateLanConnection({
    required bool enabled,
    required String host,
    String? token,
  }) async {
    if (busy || connecting || _disposed) return;
    _rememberToken(token ?? '');
    final requestedSession = _session, requestedEpoch = _epoch;
    var confirmed = false;
    await _queueConnection(
      () async {
        final session = _requiredSession();
        if (!identical(session, requestedSession) || requestedEpoch != _epoch)
          throw StateError('连接已变化，请重新检查局域网设置。');
        if (!desktop || !session.api.connection.isLoopback || !primary)
          throw StateError('请在游戏电脑本机的主设备修改局域网设置。');
        if (token != null && !RegExp(r'^[\x21-\x7E]{8,128}$').hasMatch(token))
          throw const FormatException('Token 须为 8–128 位非空白可见 ASCII 字符。');
        if (host.isEmpty) throw const FormatException('请选择实际网卡或自动选择。');
        final revision = session.state.authority?.authorityRevision;
        if (revision == null) throw StateError('设备信息尚未就绪，请刷新后重试。');
        final oldEndpoint = _endpoint,
            oldToken = _token,
            oldLabel = _clientLabel;
        final source = _connectionSource;
        final restoreLanSubscription = _resourceQueries.containsKey(
          '/local-api/config',
        );
        clearResourceSubscription();
        final subscriptionGeneration = _resourceGeneration;
        clearRecommendationQuery();
        _gameMedia.bind(null);
        await session.pause();
        late final DataMap response;
        try {
          response = await session.api.write(
            '/local-api/config',
            body: {
              'lanEnabled': enabled,
              'lanHost': host,
              if (token != null) 'token': token,
            },
            authorityRevision: revision,
          );
        } catch (cause) {
          // 只有明确拒绝才恢复旧连接。网络断开或响应不完整时，服务器可能已经
          // 修改 Token；保留断开状态让用户核对，不拿旧凭据自动重试管理 POST。
          if (!_disposed &&
              requestedEpoch == _epoch &&
              !(cause is ApiException && cause.possiblyApplied)) {
            _gameMedia.bind(session.api);
            await session.resume();
          }
          rethrow;
        }
        if (_disposed ||
            requestedEpoch != _epoch ||
            !identical(session, _session))
          return;
        final actualToken = response['token'];
        if (actualToken is! String ||
            !RegExp(r'^[\x21-\x7E]{8,128}$').hasMatch(actualToken))
          throw const ApiException(
            'local-config-receipt-invalid',
            '局域网设置可能已保存，但连接凭据回执无效；请核对游戏电脑设置，不要重复提交。',
            possiblyApplied: true,
          );
        confirmed = true;
        _rememberToken(actualToken);
        if (actualToken != oldToken)
          _retiredLaunchPairs.add((
            validateEndpoint(oldEndpoint).toString(),
            oldToken,
          ));
        // 用户可明确把 B 改回 A；当前已确认 pair 不再属于退休集合。
        _retiredLaunchPairs.remove((
          validateEndpoint(oldEndpoint).toString(),
          actualToken,
        ));
        // _replaceConnection 写入密文存储与同一地址的配置 checkpoint。保持来源锁，
        // 重试连接沿用新组；精确退休旧 pair 后，迟到的单实例激活不能改回旧 Token。
        await _replaceConnection(
          oldEndpoint,
          actualToken,
          oldLabel,
          source: source,
        );
        _resources['/local-api/config'] = response;
        _resourceResultQueries['/local-api/config'] = const {};
        // 缓存保留已确认回执，但订阅属于可见页面。等待改密/安全存储/重新握手时
        // 切页会推进资源代次；此时不能把隐藏 LAN 页面重新挂回五秒读取定时器。
        if (restoreLanSubscription &&
            subscriptionGeneration == _resourceGeneration)
          _resourceQueries['/local-api/config'] = const {};
        _requireConnectedResult();
        _notice = '局域网设置已保存，当前设备连接已更新。';
      },
      operationName: '保存局域网连接',
      wasConfirmed: () => confirmed,
    );
  }

  @override
  Future<void> reloadGameMedia() => _guard(() async {
    final session = _requiredSession();
    final revision = session.state.authority?.authorityRevision;
    if (!primary || revision == null) throw StateError('请在主设备重新读取游戏资源。');
    clearRecommendationQuery();
    clearResourceSubscription();
    await _gameMedia.reload(
      revision,
      beforeReload: session.invalidateResources,
    );
    await session.refresh();
  }, operationName: '重新读取游戏资源');
  @override
  void activatePage(int index) {
    // 路由只同步撤销旧订阅。所有页面（含经营）统一由 ActiveResourcePage 在帧后
    // 建立新订阅，确保 IndexedStack 内旧页的失活清理先完成，不能取消新页请求。
    clearResourceSubscription();
    // 推荐查询不是自动化许可；离开后撤销服务器查询订阅，避免隐藏页面持续产生 POST。
    if (index != 2) clearRecommendationQuery();
  }

  @override
  void clearRecommendationQuery() {
    // 撤销读取不进入全局写操作 guard，避免切页清除无关错误或临时禁用所有按钮。
    _recommendationGeneration++;
    _recommendationActive = false;
    _recommendationPending = false;
    _recommendationError = '';
    final session = _session;
    if (session != null) unawaited(session.query(null));
    _changed();
  }

  @override
  void clearResourceSubscription() {
    _resourceQueries.clear();
    _resourceGeneration++;
  }

  CompanionSession _requiredSession() {
    if (_session == null || !connected) throw StateError('请先连接游戏电脑。');
    return _session!;
  }

  /// 附加资源读取单飞且校验连接代次/筛选参数；迟到结果不得覆盖新地图或新筛选。
  @override
  Future<void> readResource(
    String path, {
    Map<String, String> query = const {},
  }) async {
    if (path == '/updates/status') {
      await _refreshUpdateStatus(reportError: true);
      return;
    }
    const allowed = {
      '/favorites',
      '/custom-recipes',
      '/missions/tracked',
      '/missions/available',
      '/rare-guests/invitations',
      '/logs/settings',
      '/logs/packages',
      '/local-api/config',
      '/updates/status',
    };
    if (!allowed.contains(path) || !connected) return;
    if (!mapEquals(query, _resourceResultQueries[path]))
      _resources.remove(path);
    // 诊断设置与包列表同属一个可见页面；仅这两个已声明的只读端点共享订阅。
    // 切到其他资源仍撤销整个旧组，不能让隐藏页面继续周期读取。
    const groups = [
      {'/logs/settings', '/logs/packages'},
      // 全部任务由两个不同来源组成；仅在同一任务页共同订阅，离页整组撤销。
      {'/missions/tracked', '/missions/available'},
    ];
    final sameResourceGroup = groups.any(
      (group) =>
          group.contains(path) && _resourceQueries.keys.every(group.contains),
    );
    if ((!_resourceQueries.containsKey(path) && !sameResourceGroup) ||
        _resourceQueries.containsKey(path) &&
            !mapEquals(query, _resourceQueries[path]))
      clearResourceSubscription();
    _resourceQueries[path] = Map.of(query);
    final epoch = _epoch;
    final generation = _resourceGeneration;
    final boundary =
        '${snapshot['automationSessionId']}/${snapshot['runtimeDaySceneGeneration']}/${snapshot['nightBusinessGeneration']}';
    final flight = (epoch, generation, boundary);
    if (_reading[path] == flight) return;
    _reading[path] = flight;
    // 成功和失败必须接受相同的上下文校验。只校验连接/订阅代次还不足以识别同一
    // HTTP 连接上的游戏进程或场景更换；旧失败也不能覆盖新页面已经读到的成功状态。
    bool isCurrentRead() =>
        !_disposed &&
        connected &&
        epoch == _epoch &&
        generation == _resourceGeneration &&
        mapEquals(query, _resourceQueries[path]) &&
        boundary ==
            '${snapshot['automationSessionId']}/${snapshot['runtimeDaySceneGeneration']}/${snapshot['nightBusinessGeneration']}';
    try {
      final api = _requiredSession().api;
      final result = path == '/updates/status'
          ? await api.updateStatus()
          : await api.read(path, query: query);
      if (!isCurrentRead()) return;
      // 更新状态中的 error 描述此前的下载/安装失败，不能据此丢弃仍有效的完整状态。
      // 可接任务也有专用只读 DTO：非就绪快照的 error 是运行时诊断。只有此端点
      // 且完整合法的非就绪形状才保留；其它资源及所有写入继续接受普通操作校验。
      final availableMissionState = path == '/missions/available'
          ? AvailableMissionReadState.tryRead(result)
          : null;
      if (path != '/updates/status' && availableMissionState == null) {
        requireOperationSuccess(result);
      }
      if (result['unchanged'] != true) {
        _resources[path] = result;
        _resourceResultQueries[path] = Map.of(query);
      }
    } catch (cause) {
      if (isCurrentRead()) {
        _error = _safeError(cause);
        if (path == '/logs/packages') {
          // 包列表失败有独立只读状态，不能保留旧条目供删除，也不能用空集合
          // 冒充“确实没有诊断包”。不复制服务端异常路径或响应正文到此投影。
          _resources[path] = const {
            'ok': false,
            'error': '诊断包读取失败，请刷新重试。',
            'items': <DataMap>[],
          };
          _resourceResultQueries[path] = Map.of(query);
        }
      }
    } finally {
      if (_reading[path] == flight) _reading.remove(path);
      _changed();
    }
  }

  /// 全局状态读取独立于页签、经营场景和五秒列表订阅。连接替换或写入成功后旧回复
  /// 必须丢弃；静默读取失败不覆盖用户正在查看的操作错误，更新页主动刷新才报告。
  Future<void> _refreshUpdateStatus({bool reportError = false}) {
    if (!connected || _disposed || !_desktop && _state.paused)
      return Future.value();
    final pending = _updateRead;
    if (pending != null) {
      // 用户主动刷新复用后台请求时提升报错意图，不把网络失败误显示为无变化。
      _updateReadReportError = _updateReadReportError || reportError;
      return pending;
    }
    _updateReadReportError = reportError;
    final session = _requiredSession();
    final epoch = _epoch, generation = _updateGeneration;
    final gameSession = snapshot['automationSessionId'];
    _lastUpdateReadAt = _inputClock.elapsedMilliseconds;
    late final Future<void> operation;
    operation = (() async {
      try {
        final result = await session.api.updateStatus();
        if (_disposed ||
            epoch != _epoch ||
            generation != _updateGeneration ||
            !identical(session, _session) ||
            !connected ||
            gameSession != snapshot['automationSessionId'])
          return;
        // DTO 的 error 是更新操作结果，必须完整保留，不能当读取异常丢弃。
        _updateStatus = result;
      } catch (cause) {
        if (_updateReadReportError &&
            !_disposed &&
            epoch == _epoch &&
            generation == _updateGeneration)
          _error = _safeError(cause);
      } finally {
        if (identical(_updateRead, operation)) _updateRead = null;
        _changed();
      }
    })();
    _updateRead = operation;
    return operation;
  }

  @override
  Future<void> refreshDiagnostics() async {
    final epoch = _epoch;
    await readResource('/logs/settings');
    if (_disposed ||
        epoch != _epoch ||
        !_resourceQueries.containsKey('/logs/settings'))
      return;
    // 第二项开始前重新检查订阅，避免离页后的顺序 await 又把诊断组接回来。
    await readResource('/logs/packages');
  }

  @override
  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  }) async {
    // 所有 UI 写入口默认拒绝重复点击；未知结果只读取确认，永远不自动重发。
    if (busy) return;
    var confirmed = false;
    await _guard(
      () async {
        final session = _requiredSession();
        final epoch = _epoch;
        if (localOnly && (!desktop || !session.api.connection.isLoopback))
          throw StateError('该操作只能在游戏电脑本机执行。');
        switch (path) {
          case '/devices/primary':
            await session.setPrimary(label(body?['deviceId'], ''));
            break;
          case '/devices/sync':
            await session.syncFromPrimary(
              deviceId: label(body?['deviceId'], ''),
            );
            break;
          case '/devices/rename':
            await session.rename(label(body?['label'], ''));
            break;
          case '/devices/forget':
            await session.forget(label(body?['deviceId'], ''));
            break;
          default:
            const allowed = {
              '/favorites/add-recipe',
              '/favorites/add-beverage',
              '/favorites/remove-recipe',
              '/favorites/remove-beverage',
              '/custom-recipes/upsert',
              '/custom-recipes/remove',
              '/custom-recipes/settings',
              '/custom-recipes/update-flags',
              '/custom-recipes/move',
              '/rare-guests/invite',
              '/rare-guests/invite-all',
              '/inventory/set',
              '/inventory/bulk-set',
              '/orders/rare/dismiss',
              '/updates/status',
              '/updates/check',
              '/updates/download',
              '/updates/install-on-exit',
              '/logs/config',
              '/logs/console',
              '/logs/open-folder',
              '/logs/export-diagnostics',
              '/logs/packages/delete',
            };
            if (!allowed.contains(path)) throw StateError('未声明的客户端操作。');
            if (!primary &&
                path != '/updates/status' &&
                path != '/updates/check')
              throw StateError('请在主设备进行此操作。');
            final resourceGeneration = _resourceGeneration;
            final identityGeneration = _profileWriteGeneration;
            final updateGeneration = _updateGeneration;
            final updateSessionId = snapshot['automationSessionId'];
            // 同一地址也可能已换游戏进程。更新写回必须与只读轮询使用相同边界，
            // 迟到的成功或失败都不能覆盖新进程刚取得的状态，也不能显示旧操作提示。
            bool currentUpdateContext() =>
                !_disposed &&
                epoch == _epoch &&
                updateGeneration == _updateGeneration &&
                identical(session, _session) &&
                connected &&
                updateSessionId == snapshot['automationSessionId'];
            // 配方模式修订属于独立资源，写入被拒绝后不能只保留旧缓存。补读仍绑定
            // 原连接、权威及游戏上下文；切主/重连后的响应不得污染新会话或显示旧错误。
            final customBoundary = (
              authority['registryId'],
              authority['currentDeviceId'],
              authority['primaryDeviceId'],
              authority['authorityRevision'],
              snapshot['automationSessionId'],
              snapshot['runtimeDaySceneGeneration'],
              snapshot['nightBusinessGeneration'],
            );
            bool currentCustomContext() =>
                !_disposed &&
                epoch == _epoch &&
                identityGeneration == _profileWriteGeneration &&
                identical(session, _session) &&
                connected &&
                customBoundary ==
                    (
                      authority['registryId'],
                      authority['currentDeviceId'],
                      authority['primaryDeviceId'],
                      authority['authorityRevision'],
                      snapshot['automationSessionId'],
                      snapshot['runtimeDaySceneGeneration'],
                      snapshot['nightBusinessGeneration'],
                    );
            late final DataMap result;
            try {
              result = await session.api.write(
                path,
                query: query,
                body: body,
                authorityRevision: session.state.authority?.authorityRevision,
                timeout: Duration(
                  seconds: path == '/updates/download'
                      ? 60
                      : path == '/updates/check'
                      ? 15
                      : 8,
                ),
              );
            } catch (_) {
              if (path.startsWith('/updates/') && !currentUpdateContext())
                return;
              if (!path.startsWith('/custom-recipes/')) rethrow;
              if (!currentCustomContext()) return;
              if (resourceGeneration == _resourceGeneration) {
                // 撤销更早启动的 GET，直接读取一次最新模式与修订。不调用 readResource，
                // 因而不抢占现有页面订阅，也不使隐藏页面开始周期读取。
                final recoveryGeneration = ++_resourceGeneration;
                try {
                  final latest = await session.api.read('/custom-recipes');
                  if (currentCustomContext() &&
                      recoveryGeneration == _resourceGeneration) {
                    requireOperationSuccess(latest);
                    _resources['/custom-recipes'] = latest;
                    _resourceResultQueries['/custom-recipes'] = const {};
                  }
                } catch (_) {
                  // 补读失败不能掩盖原写入错误；草稿由页面持有，且永远不自动重发写入。
                }
              }
              if (!currentCustomContext()) return;
              rethrow;
            }
            if (_disposed || epoch != _epoch) return;
            if (path.startsWith('/updates/') && !currentUpdateContext()) return;
            confirmed = true;
            // 写入成功后的缓存比任何更早启动的 GET 新；拒绝旧读取把已确认结果覆盖回去。
            _resourceGeneration++;
            if (path.startsWith('/updates/')) {
              _updateGeneration++;
              _updateStatus = result;
            }
            if (path.startsWith('/favorites/'))
              _resources['/favorites'] = object(result['favorites']);
            if (path.startsWith('/custom-recipes/'))
              _resources['/custom-recipes'] = object(result['customRecipes']);
            if (path.startsWith('/rare-guests/'))
              _resources['/rare-guests/invitations'] = result;
            if (path.startsWith('/logs/') &&
                (_resourceQueries.containsKey('/logs/settings') ||
                    _resourceQueries.containsKey('/logs/packages')))
              await refreshDiagnostics();
            if (path == '/orders/rare/dismiss')
              _notice = label(result['status'], '订单缓存操作已确认。');
            if (path == '/inventory/set') {
              _notice = result['changed'] == true
                  ? '库存修改已确认，当前数量 ${result['quantity']}。'
                  : '库存数量未变，当前数量 ${result['quantity']}。';
            }
            if (path == '/inventory/bulk-set') {
              _notice =
                  '库存修改已确认：变更 ${result['changed']} 项，'
                  '未变 ${result['unchanged']} 项。';
            }
        }
        if (_disposed || epoch != _epoch) return;
        confirmed = true;
        if (path != '/orders/rare/dismiss' && !path.startsWith('/inventory/'))
          _notice = '${workbenchOperationName(path)}已完成。';
        await session.refresh();
      },
      operationName: path == '/updates/status'
          ? null
          : workbenchOperationName(path),
      wasConfirmed: () => confirmed,
    );
  }

  @override
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  }) async {
    if (_disposed) return;
    final generation = ++_recommendationGeneration, epoch = _epoch;
    _recommendationActive = true;
    _recommendationPending = true;
    _recommendationError = '';
    _changed();
    bool current() =>
        !_disposed &&
        generation == _recommendationGeneration &&
        epoch == _epoch;
    try {
      await _requiredSession().query(
        kind == 'normal'
            ? PageQuery.normal(selectedPlace)
            : PageQuery.rare(
                customerId!,
                foodTag: foodTag,
                beverageTag: beverageTag,
              ),
      );
    } catch (cause) {
      if (current()) _recommendationError = _safeError(cause);
    } finally {
      // 请求被新条件或连接替换后，旧完成通知不能取消新请求的加载状态。
      if (current()) {
        _recommendationPending = false;
        _changed();
      }
    }
  }

  @override
  Future<void> updateProfile(DataMap changes) {
    final patch = Map<String, Object?>.of(changes);
    return _queueProfile((current) => _mergeProfile(current, patch));
  }

  /// 评分控件只传被调整的目标及叶字段，完整列表由队列读取最新配置后生成。
  /// 预设由 updateSortMode 一次替换整套排序规则，避免多个中间 CAS 状态。
  @override
  Future<void> updateSortObjective(String key, DataMap changes) {
    final patch = Map<String, Object?>.of(changes);
    return _queueProfile((current) {
      final sort = object(current['recommendationSortProfile']);
      if (sort['preset'] != 'custom') {
        throw StateError('评分模式已经变化，请先切换为自定义再调整参数。');
      }
      final objectives = records(sort['objectives']);
      if (objectives.where((item) => item['key'] == key).length != 1 ||
          patch.keys.any(
            (field) => !{'weight', 'enabled', 'direction'}.contains(field),
          )) {
        throw ArgumentError('评分目标或字段无效，请刷新设置后重试。');
      }
      return {
        ...current,
        'recommendationSortProfile': {
          ...sort,
          'objectives': [
            for (final item in objectives)
              item['key'] == key ? {...item, ...patch} : item,
          ],
        },
      };
    });
  }

  /// 模式变更同样进入共享配置队列：自定义读取出队时的最新九项，不能由页面
  /// 携带旧目标列表覆盖先前保存；预设切换一次替换整套固定值，不经过中间状态。
  @override
  Future<void> updateSortMode(String mode) => _queueProfile(
    (current) => {
      ...current,
      'recommendationSortProfile': mode == 'custom'
          ? {
              ...object(current['recommendationSortProfile']),
              'preset': 'custom',
            }
          : CompanionProfile.sortPreset(mode),
    },
  );

  Future<void> _queueProfile(DataMap Function(DataMap) build) {
    final requestedEpoch = _epoch;
    final generation = _profileWriteGeneration;
    final session = _session;
    final next = _profileTail.then(
      (_) => _guard(() async {
        if (_disposed ||
            requestedEpoch != _epoch ||
            session != _session ||
            generation != _profileWriteGeneration) {
          throw StateError('连接、设备或前一项保存结果已变化，本次设置未提交，请重新调整。');
        }
        try {
          if (!primary ||
              authority['pendingSyncId'] != null &&
                  authority['pendingSyncId'] != '') {
            throw StateError('请等待主设备配置同步完成后再调整。');
          }
          await _requiredSession().updateProfile(
            CompanionProfile(build(profile)),
          );
        } catch (_) {
          // 失败可能是未知写入结果，不重放同一批尚未发送的编辑；后续明确编辑可重新开始。
          _profileWriteGeneration++;
          rethrow;
        }
      }, operationName: '保存共享设置'),
    );
    _profileTail = next;
    return next;
  }

  DataMap _mergeProfile(DataMap current, DataMap changes) {
    final next = {...current, ...changes};
    // 表单上的父开关关闭时同时关闭相关子步骤，避免提交违反 schema 的中间态。
    if (changes['autoNormalCompleteOrder'] == false) {
      next['autoNormalTakeBeverage'] = false;
      next['autoNormalDeliverFood'] = false;
    }
    if (changes['autoPrepCompleteOrder'] == false) {
      next['autoPrepTakeBeverage'] = false;
      next['autoPrepCollectCooking'] = false;
    }
    if (changes['autoNormalTakeBeverage'] == true ||
        changes['autoNormalDeliverFood'] == true)
      next['autoNormalCompleteOrder'] = true;
    if (changes['autoPrepTakeBeverage'] == true ||
        changes['autoPrepCollectCooking'] == true)
      next['autoPrepCompleteOrder'] = true;
    return next;
  }

  @override
  Future<void> acknowledgeBarrier(
    int sequence, {
    required String expectedSessionId,
    required int expectedGeneration,
  }) => _guard(
    () => _requiredSession().acknowledge(
      sequence,
      expectedSessionId: expectedSessionId,
      expectedGeneration: expectedGeneration,
    ),
    operationName: '确认自动化处理结果',
  );
  @override
  Future<void> retryOrder(String kind, String key) =>
      _guard(() => _requiredSession().retry(kind, key), operationName: '重试订单');
  @override
  Future<void> resetOrder(
    String kind,
    String key, {
    required String expectedSessionId,
    required int expectedGeneration,
  }) => _guard(
    () => _requiredSession().reset(
      kind,
      key,
      expectedSessionId: expectedSessionId,
      expectedGeneration: expectedGeneration,
    ),
    operationName: '重置订单状态',
  );

  @override
  Future<void> renameDevice(String name) async {
    if (busy) return;
    var confirmed = false;
    await _guard(
      () async {
        final session = _requiredSession();
        final epoch = _epoch;
        final requested = name.trim();
        if (requested.isEmpty ||
            requested.length > 48 ||
            requested.runes.any(
              (value) => value < 32 || value >= 127 && value <= 159,
            )) {
          throw ArgumentError('设备名称应为1至48个字符，且不含控制字符。');
        }
        await session.rename(requested);
        if (_disposed || epoch != _epoch || !identical(session, _session))
          return;
        final current = records(authority['devices'])
            .where((item) => item['deviceId'] == authority['currentDeviceId']);
        if (current.length != 1 ||
            label(current.single['label'], '') != requested) {
          throw StateError('设备名称尚未确认，请刷新设备列表。');
        }
        confirmed = true;
        // 服务端登记是名称的权威来源，成功后才更新本机；不替换地址、凭据或设备身份。
        _clientLabel = requested;
        await platform.writePreferences({'deviceLabel': requested});
        if (_disposed || epoch != _epoch || !identical(session, _session))
          return;
        _savedLocal['deviceLabel'] = requested;
        _local['deviceLabel'] = requested;
        _notice = '设备名称已更新。';
      },
      operationName: '修改设备名称',
      wasConfirmed: () => confirmed,
    );
  }

  @override
  Future<void> openReleasePage(String url) => _guard(() async {
    if (!await platform.openExternalUrl(url)) throw StateError('无法打开发布页。');
  }, operationName: '打开版本发布页');
  @override
  Future<void> updateLocal(DataMap changes) {
    if (_disposed) return Future.value();
    // 地址和设备名只能通过连接队列一起保存，不能由通用外观偏好入口旁路修改。
    // 此检查也覆盖尚未读取启动参数时的调用，且发生在乐观内存更新之前。
    if (changes.containsKey('endpoint') || changes.containsKey('deviceLabel'))
      return _guard(() async {
        throw StateError('连接信息只能通过连接设置修改；启动参数管理的连接不可手动修改。');
      });
    final patch = Map<String, Object?>.of(changes);
    final versions = {
      for (final key in patch.keys)
        key: _localFieldVersions.update(
          key,
          (value) => value + 1,
          ifAbsent: () => 1,
        ),
    };
    // 字号和透明度先在内存即时预览；持久化按顺序完成，不能用旧整份快照回填。
    _local.addAll(patch);
    _changed();
    final next = _localTail.then(
      (_) => _guard(
        () async {
          try {
            await platform.writePreferences(patch);
            _savedLocal.addAll(patch);
          } catch (_) {
            // 只回退仍属于这次失败请求的字段；新拖动和其他设置不受迟到失败影响。
            for (final key in patch.keys) {
              if (_localFieldVersions[key] != versions[key]) continue;
              if (_savedLocal.containsKey(key)) {
                _local[key] = _savedLocal[key];
              } else {
                _local.remove(key);
              }
            }
            rethrow;
          }
          if (!_disposed) await _applyWindow();
        },
        operationName: patch.keys.any((key) => key != 'lastPage')
            ? '保存本机偏好'
            : null,
      ),
    );
    _localTail = next;
    return next;
  }

  Future<void> _applyWindow() async {
    if (!_desktop) return;
    // 原生窗口行为只采用耐久保存成功的值。Flutter 字号/透明度可乐观预览，
    // 但不能把队列中尚未确认的鼠标穿透或置顶提前施加到真实窗口。
    await platform.applyWindowPreferences(
      WindowPreferences(
        alwaysOnTop: _savedLocal['alwaysOnTop'] != false,
        mousePassthrough: _savedLocal['mousePassthrough'] == true,
        keepVisible: _savedLocal['keepVisible'] == true,
        darkMode: workbenchDark(
          _savedLocal,
          WidgetsBinding.instance.platformDispatcher.platformBrightness,
        ),
        switchCooldownMs:
            (_savedLocal['switchCooldownMs'] as num?)?.toInt() ?? 800,
      ),
    );
  }

  @override
  Future<void> platformCommand(String command) => _guard(
    () async {
      switch (command) {
        case 'focus':
          if (!await platform.toggleFocus())
            throw StateError('系统未允许切换焦点，请手动选择窗口。');
          break;
        case 'hide':
          await platform.hide();
          break;
        case 'exit':
          await _session?.close();
          await platform.exit();
          break;
        default:
          throw ArgumentError('未知窗口操作。');
      }
    },
    operationName: const {
      'focus': '切换窗口焦点',
      'hide': '隐藏客户端',
      'exit': '退出客户端',
    }[command],
  );
  @override
  Future<void> importHistory(String path) => _guard(() async {
    final file = File(path.trim());
    if (await file.length() > 65536) throw const FormatException('历史偏好超过允许大小。');
    final imported = HistoryMigration.parse(await file.readAsString());
    final changes = <String, Object?>{
      'historyMigrationVersion': 1,
      'historyMigratedAtUtc': DateTime.now().toUtc().toIso8601String(),
      'historyPreferences': imported,
    };
    for (final entry in const {
      'font-scale-percent': 'fontScalePercent',
      'focus-switch-cooldown-ms': 'switchCooldownMs',
    }.entries) {
      if (imported[entry.key] != null)
        changes[entry.value] = int.parse(imported[entry.key]!);
    }
    for (final entry in const {
      'always-on-top': 'alwaysOnTop',
      'gamepad-navigation': 'gamepadNavigation',
      'mission-list-module-enabled': 'missionListModuleEnabled',
      'rare-guest-invitation-module-enabled':
          'rareGuestInvitationModuleEnabled',
    }.entries) {
      if (imported[entry.key] != null)
        changes[entry.value] = imported[entry.key] == 'true';
    }
    if (imported['theme-mode'] != null)
      changes['themeMode'] = imported['theme-mode'];
    final oldPage = imported['mod-tab'];
    if (oldPage != null)
      changes['lastPage'] = const {
        'overview': 0,
        'service': 1,
        'recommendations': 2,
        'extensions': 3,
        'logs': 4,
        'settings': 4,
      }[oldPage];
    if (imported['focus-switch-behavior'] != null)
      changes['keepVisible'] =
          imported['focus-switch-behavior'] == 'keep-visible';
    for (final entry in const {
      'background-opacity': 'backgroundOpacityPercent',
      'content-opacity': 'contentOpacityPercent',
    }.entries) {
      if (imported[entry.key] != null)
        changes[entry.value] = (double.parse(imported[entry.key]!) * 100)
            .round();
    }
    await platform.writePreferences(changes);
    _local.addAll(changes);
    _savedLocal.addAll(changes);
    await _applyWindow();
    _notice = '历史偏好已导入；原文件保留，自动化仍按当前设备保存的配置运行。';
  }, operationName: '导入历史偏好');

  /// 系统亮度变化不保存为用户选择；仅当当前选择跟随系统时更新原生和 Flutter 绘制。
  @override
  void didChangePlatformBrightness() {
    if (themeMode != 'system' || _disposed) return;
    _changed();
    unawaited(
      _applyWindow().catchError((Object cause) {
        if (!_disposed) {
          _error = _safeError(cause);
          _changed();
        }
      }),
    );
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state != AppLifecycleState.inactive) {
      _pageQueryVisible = state == AppLifecycleState.resumed;
    }
    // Windows 隐藏仍维护经营租约，但不再续算不可见的推荐页面；单纯失焦仍可能
    // 看得到窗口，所以 inactive 不改变页面订阅。Android 继续使用原整会话暂停边界。
    if (_desktop) {
      if (state != AppLifecycleState.inactive) {
        _session?.setPageQueryVisible(_pageQueryVisible);
      }
      return;
    }
    if (_session == null || state == AppLifecycleState.inactive) return;
    unawaited(
      _guard(() async {
        if (state == AppLifecycleState.resumed)
          await _session!.resume();
        else
          await _session!.pause();
      }),
    );
  }

  void _onPlatformEvent(PlatformEvent event) {
    if (event.kind == 'connectionActivated') {
      final launch = event.input.launch;
      // 显式空 Token 的原生激活只用于匿名/离线窗口，不替换正在使用的会话。
      // 不能把空串交给人工连接的“同地址沿用”分支，也不重新读取安全存储。
      if (launch.endpoint != null && launch.token?.isNotEmpty == true) {
        // 已确认的新游戏进程或新地址属于新的启动来源；旧进程的凭据退休表不能
        // 跨生命周期永久阻止合法连接。无 PID 的同址激活不猜测进程发生变化。
        if ((launch.gamePid != null && launch.gamePid != _launchGamePid) ||
            !_sameEndpoint(launch.endpoint!, _endpoint)) {
          _retiredLaunchPairs.clear();
        }
        if (launch.gamePid != null) _launchGamePid = launch.gamePid;
        if (_isRetiredPair(launch.endpoint!, launch.token!)) return;
        // 同凭据激活也确认了启动来源，必须立即关闭旧编辑入口；是否需要重连
        // 是另一件事。显式空 Token 的匿名激活仍完全不接管现有连接。
        _launchConnectionManaged = true;
        _launchConnectionIncomplete = false;
        _connectionSource = ConnectionSource.launchArguments;
        _changed();
        if (launch.endpoint != _endpoint ||
            launch.token != _token ||
            // 人工换址可能正在关闭旧会话，字段尚未换成新值。此时即使原生参数
            // 等于旧连接，也须排在该次换址之后重新接管，不能只设置来源锁留下人工地址。
            _manualConnectionRequests > 0 ||
            !connected && !_state.loading && _pending == 0)
          unawaited(
            _connectFrom(
              launch.endpoint!,
              launch.token!,
              _clientLabel,
              ConnectionSource.launchArguments,
              launchGamePid: launch.gamePid,
            ),
          );
      }
    }
    _gamepad.enabled = _local['gamepadNavigation'] != false;
    final actions = _gamepad.update(event.input, _inputClock.elapsed);
    if ((!event.input.focused || !event.input.connected || !_gamepad.enabled) &&
        _gamepadInputActive) {
      // 失焦、拔出及关闭手柄导航立即撤回输入提示；只报告一次失活事件，
      // 不把原生轮询变成持续 UI 刷新，也不将上次按键重放到重新激活的窗口。
      _gamepadInputActive = false;
      _inputAction = 'gamepadInactive';
      _inputSequence++;
      _changed();
    }
    for (final action in actions) {
      _gamepadInputActive = true;
      _inputAction = switch (action) {
        GamepadAction.confirm => 'activate',
        _ => action.name,
      };
      _inputSequence++;
      _changed();
    }
  }

  Future<void> _guard(
    Future<void> Function() operation, {
    String? operationName,
    bool acrossConnection = false,
    bool Function()? wasConfirmed,
  }) async {
    if (_disposed) return;
    final epoch = _epoch, startedAt = DateTime.now();
    Object? failure;
    _pending++;
    _error = '';
    // 提示属于一次操作，不能让前一次成功与本次失败并存。错误仍由原有 guard
    // 保留；失败不改变库存、不补造成功结果，也不自动重发可能已部分执行的写入。
    _notice = '';
    _changed();
    try {
      await operation();
    } catch (cause) {
      failure = cause;
      // 普通写入在换连接后完成，其错误不属于新连接。连接操作可接受自身换代，
      // 因为所有 _replaceConnection 均在同一 connectionTail 内严格串行；dispose
      // 是唯一可从队列外推进代次的入口，并由 _disposed 再次拒绝。
      if (!_disposed && (acrossConnection || epoch == _epoch))
        _error = _safeError(cause);
    } finally {
      if (!_disposed &&
          operationName != null &&
          (acrossConnection || epoch == _epoch) &&
          (failure != null || wasConfirmed == null || wasConfirmed())) {
        final confirmed = wasConfirmed?.call() == true;
        final result = failure == null || confirmed
            ? WorkbenchOperationResult.success
            : failure is ApiException && failure.possiblyApplied
            ? WorkbenchOperationResult.unknown
            : WorkbenchOperationResult.failed;
        // 历史只含固定摘要，不复制异常、路径、响应正文或用户输入。操作已成功而
        // 后续刷新失败时仍记录“已确认”，不能把真实写入误报为失败并诱导重复提交。
        _operations.insert(
          0,
          WorkbenchOperation(
            id: ++_operationSequence,
            name: operationName,
            startedAt: startedAt,
            completedAt: DateTime.now(),
            result: result,
            summary: result == WorkbenchOperationResult.unknown
                ? '结果未确认，请刷新核对，不要重复提交。'
                : result == WorkbenchOperationResult.failed
                ? '未完成，请查看当前错误信息。'
                : failure != null
                ? '操作已确认，后续连接或刷新未成功。'
                : '已完成。',
          ),
        );
        if (_operations.length > 20) _operations.removeLast();
      }
      _pending--;
      _changed();
    }
  }

  void _rememberToken(String value) {
    if (value.isNotEmpty) _redactedTokens.add(value);
  }

  String _safeError(Object cause) {
    var message = cause.toString();
    if (message.isEmpty) return message;
    // Token 更新失败也可能把新输入带入异常；所有本进程已使用/提交的凭据都
    // 参加脱敏，而不只替换当前 token。集合只存内存，从不进入日志或普通偏好。
    final tokens = {..._redactedTokens, if (_token.isNotEmpty) _token}.toList()
      ..sort((first, second) => second.length.compareTo(first.length));
    for (final token in tokens) message = message.replaceAll(token, '[已隐藏]');
    return message;
  }

  void _changed() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _epoch++;
    WidgetsBinding.instance.removeObserver(this);
    _resourceTimer?.cancel();
    _updateTimer?.cancel();
    _gameMedia.removeListener(_changed);
    _gameMedia.dispose();
    unawaited(_states?.cancel());
    unawaited(_platformEvents?.cancel());
    unawaited(_session?.close());
    unawaited(platform.dispose());
    super.dispose();
  }
}
