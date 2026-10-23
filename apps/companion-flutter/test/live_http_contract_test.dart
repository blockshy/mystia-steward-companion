@Tags(['production-http'])
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:companion_client/companion_client.dart';
import 'package:companion_platform/companion_platform.dart';
import 'package:companion_platform/src/platform.g.dart' show PlatformHostApi;
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/live_workbench_model.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/app/available_mission_resource.dart';

/// 只替换原生 HostApi；真实平台协调器、生产 UI 组合根、Dart HTTP 与 Mod router/store 全部参与。
/// 安全存储在本测试为内存替身，因此本测试不声称验证 Windows DPAPI 或 Android Keystore。
class _IsolatedPlatformHost extends PlatformHostApi {
  _IsolatedPlatformHost(this.launch, this.file);
  final PlatformLaunch launch;
  final File file;
  final Map<String, String> secrets = {};
  int readBacks = 0;
  Completer<void>? tokenWriteGate, tokenWriteEntered;

  @override
  Future<PlatformLaunch> initialize() async => launch;
  @override
  Future<void> applyWindowPreferences(WindowPreferences preferences) async {}
  @override
  Future<bool> performWindowAction(WindowAction action) async => true;
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
  @override
  Future<String?> readSecret(String key) async => secrets[key];
  @override
  Future<void> writeSecret(String key, String value) async {
    if (key == 'connection-token' && tokenWriteGate != null) {
      tokenWriteEntered?.complete();
      await tokenWriteGate!.future;
    }
    secrets[key] = value;
  }

  @override
  Future<void> deleteSecret(String key) async {
    secrets.remove(key);
  }

  @override
  Future<String?> readPreferences() async {
    readBacks++;
    return await file.exists() ? file.readAsString() : null;
  }

  @override
  Future<void> writePreferences(String json) async {
    await file.writeAsString(json, flush: true);
  }
}

/// flutter_test 默认屏蔽网络；这里显式使用 Dart 原始 HttpClient，且连接由隔离宿主动态返回。
class _RealHttp extends HttpOverrides {
  final requests = <String, int>{};
  final failedReads = <String>{};
  final heldReplies = <String, _HeldHttpReply>{};

  /// 只对精确方法/路径的下一笔真实响应加闸；请求仍先到达隔离 Mod router。
  /// 不保存请求头、查询或凭据，也不改变其它生产 HTTP 的传输行为。
  _HeldHttpReply holdNext(
    String method,
    String path, {
    DataMap? replacement,
    bool failAfterRelease = false,
  }) {
    final key = '$method $path';
    if (heldReplies.containsKey(key)) throw StateError('该路径已有待领取回复闸门。');
    return heldReplies[key] = _HeldHttpReply(
      replacement: replacement,
      failAfterRelease: failAfterRelease,
    );
  }

  @override
  HttpClient createHttpClient(SecurityContext? context) => _CountedHttpClient(
    super.createHttpClient(context),
    requests,
    failedReads,
    heldReplies,
  );
}

/// 等待真实宿主发送完整响应后才通知测试切换会话，避免把“请求尚未发出”误当迟到。
/// 宿主更新服务明确 disabled，不访问更新源；仅成功分支使用带标记的合成响应覆盖，
/// 用于验证客户端过期结果边界，不声称覆盖真实下载、GitHub 或安装行为。
class _HeldHttpReply {
  _HeldHttpReply({this.replacement, this.failAfterRelease = false});
  final DataMap? replacement;
  final bool failAfterRelease;
  final arrived = Completer<void>(), released = Completer<void>();
  DataMap? originalBody;

  void release() {
    if (!released.isCompleted) released.complete();
  }

  Future<HttpClientResponse> wait(HttpClientResponse response) async {
    final bytes = await response.fold<List<int>>(
      <int>[],
      (buffer, chunk) => buffer..addAll(chunk),
    );
    originalBody = jsonMap(jsonDecode(utf8.decode(bytes)));
    arrived.complete();
    await released.future;
    if (failAfterRelease) throw const SocketException('合成迟到更新传输异常');
    return _BufferedHttpReply(
      response,
      replacement == null ? bytes : utf8.encode(jsonEncode(replacement)),
    );
  }
}

/// 已收完整的真实响应重新提供一次字节流；仅可控成功分支替换正文，HTTP 元数据保留。
class _BufferedHttpReply extends Stream<List<int>>
    implements HttpClientResponse {
  _BufferedHttpReply(this.inner, this.bytes);
  final HttpClientResponse inner;
  final List<int> bytes;
  @override
  int get statusCode => inner.statusCode;
  @override
  int get contentLength => bytes.length;
  @override
  bool get isRedirect => inner.isRedirect;
  @override
  HttpHeaders get headers => inner.headers;
  @override
  StreamSubscription<List<int>> listen(
    void Function(List<int>)? onData, {
    Function? onError,
    void Function()? onDone,
    bool? cancelOnError,
  }) => Stream<List<int>>.value(bytes).listen(
    onData,
    onError: onError,
    onDone: onDone,
    cancelOnError: cancelOnError,
  );
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// 只包装生产传输实际使用的请求成员；所有认证、正文、发送与取消继续委托真实请求。
class _HeldHttpRequest implements HttpClientRequest {
  _HeldHttpRequest(this.inner, this.reply);
  final HttpClientRequest inner;
  final _HeldHttpReply reply;
  @override
  set followRedirects(bool value) => inner.followRedirects = value;
  @override
  set persistentConnection(bool value) => inner.persistentConnection = value;
  @override
  set contentLength(int value) => inner.contentLength = value;
  @override
  HttpHeaders get headers => inner.headers;
  @override
  void add(List<int> data) => inner.add(data);
  @override
  void abort([Object? exception, StackTrace? stackTrace]) =>
      inner.abort(exception, stackTrace);
  @override
  Future<HttpClientResponse> close() async => reply.wait(await inner.close());
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// 只计方法和不带参数的路径，其余请求仍由真实 Dart HTTP 客户端发送。
/// 不能记录 headers/body/URL query，避免把连接 Token 或用户配置混入测试证据。
class _CountedHttpClient implements HttpClient {
  _CountedHttpClient(
    this.inner,
    this.requests,
    this.failedReads,
    this.heldReplies,
  );
  final HttpClient inner;
  final Map<String, int> requests;
  final Set<String> failedReads;
  final Map<String, _HeldHttpReply> heldReplies;
  @override
  set findProxy(String Function(Uri)? value) => inner.findProxy = value;
  @override
  set connectionTimeout(Duration? value) => inner.connectionTimeout = value;
  @override
  set maxConnectionsPerHost(int? value) => inner.maxConnectionsPerHost = value;
  @override
  Future<HttpClientRequest> openUrl(String method, Uri url) {
    requests.update(
      '$method ${url.path}',
      (count) => count + 1,
      ifAbsent: () => 1,
    );
    // 只拒绝测试明确指定的 GET；管理写入仍到达真实宿主且不被自动重试。
    if (method == 'GET' && failedReads.contains(url.path))
      throw const SocketException('合成只读连接中断');
    final reply = heldReplies.remove('$method ${url.path}');
    final request = inner.openUrl(method, url);
    return reply == null
        ? request
        : request.then((value) => _HeldHttpRequest(value, reply));
  }

  @override
  void close({bool force = false}) => inner.close(force: force);
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// 原生激活以同步内存事件注入，仍使用生产模型、平台存储和真实 HTTP；不接触
/// 用户窗口或游戏进程，专门覆盖服务器改密后迟到的旧启动参数。
class _ManagementEventPlatform extends CompanionPlatform {
  _ManagementEventPlatform({required super.host});
  final activations = StreamController<PlatformEvent>.broadcast(sync: true);
  @override
  Stream<PlatformEvent> get events => activations.stream;
  void activate(String endpoint, String token) => activations.add(
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
        launch: PlatformLaunch(desktop: true, endpoint: endpoint, token: token),
      ),
    ),
  );
  @override
  Future<void> dispose() async {
    await activations.close();
    await super.dispose();
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  test('可接任务专用只读状态只接受完整非就绪 DTO，不吞操作失败或矛盾字段', () {
    final valid = <String, Object?>{
      'ok': true,
      'runtimeAvailable': false,
      'status': 'waiting-for-load',
      'missionGeneration': 0,
      'sourceRevision': 0,
      'contentSignature': List.filled(64, 'a').join(),
      'unchanged': false,
      'availableCount': 0,
      'missions': <Object?>[],
      'error': 'mission-runtime-not-ready:WaitingForLoad:',
    };
    for (final status in [
      'not-attached',
      'waiting-for-load',
      'loading',
      'runtime-unavailable',
      'mission-data-incomplete',
    ]) {
      expect(
        AvailableMissionReadState.tryRead({...valid, 'status': status})?.status,
        status,
      );
    }
    expect(
      AvailableMissionReadState.tryRead(valid)?.hasRoutineWaitDiagnostic,
      isTrue,
    );
    expect(
      AvailableMissionReadState.tryRead({
        ...valid,
        'error':
            'available-mission-read-failed:InvalidOperationException:合成读取失败',
      })?.hasRoutineWaitDiagnostic,
      isFalse,
      reason: '只有已知等待诊断可以不铺正文，任意读取异常仍需保留',
    );
    for (final change in <Map<String, Object?>>[
      {'ok': false},
      {'runtimeAvailable': true},
      {'status': 'ready'},
      {'status': 'future-status'},
      {'unchanged': true},
      {
        'missions': [<String, Object?>{}],
      },
      {'availableCount': 1},
      {'availableCount': 0.0},
      {'missionGeneration': -1},
      {'sourceRevision': '0'},
      {'contentSignature': 'invalid'},
      {'error': null},
    ]) {
      final invalid = {...valid, ...change};
      expect(
        AvailableMissionReadState.tryRead(invalid),
        isNull,
        reason: '$change',
      );
      // 专用形状不通过时，普通操作校验仍拒绝非空 error/ok:false。
      if (invalid['error'] is String) {
        expect(
          () => requireOperationSuccess(invalid),
          throwsA(isA<ApiException>()),
        );
      }
    }
    for (final key in valid.keys) {
      expect(
        AvailableMissionReadState.tryRead({...valid}..remove(key)),
        isNull,
        reason: '缺少 $key 不得获得专用只读豁免',
      );
    }
  });

  test('生产组合根通过隔离真实 Mod HTTP 完成身份恢复与资源写入', () async {
    final http = _RealHttp();
    await HttpOverrides.runWithHttpOverrides(() async {
      final fixture = await Directory.systemTemp.createTemp(
        'mystia-live-http-',
      );
      final profile = File('${fixture.path}/profile.json');
      await profile.writeAsString(
        jsonEncode(CompanionProfile.defaults().json),
        flush: true,
      );
      final path =
          Platform.environment['FLUTTER_CONTRACT_HOST'] ??
          '../../tests/flutter-contracts/host/bin/Release/net6.0/FlutterContractsHost.dll';
      expect(
        File(path).existsSync(),
        isTrue,
        reason: '先构建 tests/flutter-contracts/host，测试不自动构建或启动游戏。',
      );
      final dotnet =
          Platform.environment['DOTNET_EXE'] ??
          (Platform.isWindows
              ? 'C:/Program Files/dotnet/dotnet.exe'
              : 'dotnet');
      final host = await Process.start(dotnet, [
        File(path).absolute.path,
        'recovery',
        profile.path,
        // UI 生产初始化要求真实资源握手；宿主只在独占临时目录生成合成图片，不访问游戏。
        '--with-game-media',
      ]);
      final lines = StreamIterator(
        host.stdout.transform(utf8.decoder).transform(const LineSplitter()),
      );
      final diagnostics = StringBuffer();
      final errors = host.stderr
          .transform(utf8.decoder)
          .listen(diagnostics.write);
      LiveWorkbenchModel? model;
      try {
        expect(
          await lines.moveNext().timeout(const Duration(seconds: 15)),
          isTrue,
          reason: '$diagnostics',
        );
        final ready = jsonMap(jsonDecode(lines.current));
        expect(ready['kind'], 'flutter-contracts-real-local-api');
        final native = _IsolatedPlatformHost(
          PlatformLaunch(
            desktop: true,
            endpoint: 'http://127.0.0.1:${ready['port']}',
            token: 'flutter-contracts-synthetic-token',
          ),
          File('${fixture.path}/preferences.json'),
        );
        model = LiveWorkbenchModel(platform: CompanionPlatform(host: native));
        await model.initialize();
        expect(model.connected, isTrue, reason: model.error);
        expect(model.primary, isFalse);
        expect(model.profile['automationEnabled'], isFalse);
        expect(model.canExecute, isFalse);
        expect(native.secrets['client-id'], isNot('p0-old-device-00000001'));
        final id = model.authority['currentDeviceId'];
        // 生产改名必须命中真实登记存储，启动参数只限制连接编辑，不能限制自身改名。
        final originalEndpoint = model.endpoint;
        await model.renameDevice('离线验证设备');
        expect(model.error, isEmpty);
        expect(model.clientLabel, '离线验证设备');
        expect(
          jsonObjects(model.authority['devices'])
              .singleWhere((item) => item['deviceId'] == id)['label'],
          '离线验证设备',
        );
        expect(
          jsonMap(jsonDecode(await native.file.readAsString()))['deviceLabel'],
          '离线验证设备',
        );
        expect(model.endpoint, originalEndpoint);
        expect(model.canEditConnection, isFalse);
        // 页面激活只撤销资源；真实壳层在旧页失活完成后才注册新读取。先扣住旧收藏
        // GET，再模拟切页并立即重读同一路径，验证旧代次单飞不能吞掉新订阅的首读。
        // 这里只使用隔离宿主的真实收藏端点；marker 是传输层注入的迟到回执标记。
        model.activatePage(3);
        await model.readResource('/missions/tracked');
        final oldFavorites = http.holdNext(
          'GET',
          '/favorites',
          replacement: {
            'recipes': <DataMap>[],
            'beverages': <DataMap>[],
            'testMarker': 'discarded-old-subscription',
          },
        );
        final oldFavoritesRead = model.readResource('/favorites');
        await oldFavorites.arrived.future.timeout(const Duration(seconds: 5));
        final beforeActivation = http.requests['GET /favorites'] ?? 0;
        model.activatePage(1);
        expect(
          http.requests['GET /favorites'],
          beforeActivation,
          reason: '新页面必须等旧页失活后统一注册，activatePage 不得抢先发起读取',
        );
        await model
            .readResource('/favorites')
            .timeout(const Duration(seconds: 5));
        expect(http.requests['GET /favorites'], greaterThan(beforeActivation));
        expect(model.resources['/favorites'], isNotNull);
        oldFavorites.release();
        await oldFavoritesRead;
        expect(
          model.resources['/favorites']!['testMarker'],
          isNull,
          reason: '旧订阅的迟到结果不能覆盖新页，也不能恢复旧轮询',
        );
        final beforePoll = http.requests['GET /favorites'] ?? 0;
        await Future<void>.delayed(const Duration(milliseconds: 5200));
        expect(
          http.requests['GET /favorites'],
          greaterThan(beforePoll),
          reason: '首读完成后当前收藏订阅仍须保留，继续由生产五秒轮询更新',
        );
        // 更新状态读取不能撤销当前页面订阅；切页也不能丢弃全局更新状态。
        model.activatePage(1);
        await model.readResource('/updates/status');
        expect(model.resources['/updates/status'], isNotEmpty);
        model.activatePage(3);
        expect(model.resources['/updates/status'], isNotEmpty);

        // 同一个 HTTP 地址和设备不断开，仅通过宿主 stdin 替换游戏进程身份。
        // 旧回复已完整到达传输层才切换，验证生产组合根不能把迟到结果归入新进程。
        final updateModel = model;
        final updateConnection = updateModel.connectionVersion;
        Future<void> changeUpdateSession(String stage) async {
          host.stdin.writeln('confirmation-$stage');
          await host.stdin.flush();
          expect(
            await lines.moveNext().timeout(const Duration(seconds: 3)),
            isTrue,
          );
          final acknowledged = jsonMap(jsonDecode(lines.current));
          expect(acknowledged['kind'], 'confirmation-scope');
          expect(acknowledged['session'], 'confirmation-process-$stage');
          // refresh 可以与已有快照读取单飞；至多再读一次便应取得宿主已确认的新快照。
          for (var attempt = 0; attempt < 3; attempt++) {
            await updateModel.refresh();
            if (updateModel.snapshot['automationSessionId'] ==
                acknowledged['session']) {
              break;
            }
          }
          expect(
            updateModel.snapshot['automationSessionId'],
            acknowledged['session'],
          );
          expect(updateModel.connectionVersion, updateConnection);
          expect(updateModel.endpoint, originalEndpoint);
          expect(updateModel.connected, isTrue, reason: updateModel.error);
        }

        // 同连接换游戏进程不会增加页面订阅代次。旧收藏请求即使此后失败，也不能污染
        // 新会话的资源或全局错误；成功同样检查 Map 身份，不能因两次正文相同漏掉回归。
        for (final fail in [false, true]) {
          await changeUpdateSession('before');
          final heldFavorite = http.holdNext(
            'GET',
            '/favorites',
            failAfterRelease: fail,
          );
          final pendingFavorite = updateModel.readResource('/favorites');
          late final DataMap freshFavorite;
          late final String freshFavoriteError;
          try {
            await heldFavorite.arrived.future.timeout(
              const Duration(seconds: 3),
            );
            await changeUpdateSession('after');
            await updateModel
                .readResource('/favorites')
                .timeout(const Duration(seconds: 3));
            freshFavorite = updateModel.resources['/favorites']!;
            freshFavoriteError = updateModel.error;
          } finally {
            heldFavorite.release();
            await pendingFavorite;
          }
          expect(updateModel.resources['/favorites'], same(freshFavorite));
          expect(
            updateModel.error,
            freshFavoriteError,
            reason: '${fail ? '失败' : '成功'}旧回执不能归入同地址的新游戏进程',
          );
        }

        // 短暂断线后恢复到相同 session/day/night，仍须撤销旧在途 GET 的单飞身份。
        // 这里只令隔离 HTTP 快照读取失败，不重建模型或连接，也不靠切页改变订阅代次。
        final beforeDisconnect = (
          updateModel.connectionVersion,
          updateModel.snapshot['automationSessionId'],
          updateModel.snapshot['runtimeDaySceneGeneration'],
          updateModel.snapshot['nightBusinessGeneration'],
        );
        final disconnectedFavorite = http.holdNext(
          'GET',
          '/favorites',
          failAfterRelease: true,
        );
        final pendingDisconnectedFavorite = updateModel.readResource(
          '/favorites',
        );
        late final DataMap recoveredFavorites;
        late final String recoveredError;
        try {
          await disconnectedFavorite.arrived.future.timeout(
            const Duration(seconds: 3),
          );
          http.failedReads.add('/snapshot');
          for (
            var attempt = 0;
            attempt < 3 && updateModel.connected;
            attempt++
          ) {
            await updateModel.refresh();
          }
          expect(updateModel.connected, isFalse);
          http.failedReads.remove('/snapshot');
          for (
            var attempt = 0;
            attempt < 3 && !updateModel.connected;
            attempt++
          ) {
            await updateModel.refresh();
          }
          expect(updateModel.connected, isTrue, reason: updateModel.error);
          expect((
            updateModel.connectionVersion,
            updateModel.snapshot['automationSessionId'],
            updateModel.snapshot['runtimeDaySceneGeneration'],
            updateModel.snapshot['nightBusinessGeneration'],
          ), beforeDisconnect);
          final callsBeforeRecovery = http.requests['GET /favorites'] ?? 0;
          await updateModel
              .readResource('/favorites')
              .timeout(const Duration(seconds: 3));
          expect(
            http.requests['GET /favorites'],
            greaterThan(callsBeforeRecovery),
          );
          recoveredFavorites = updateModel.resources['/favorites']!;
          recoveredError = updateModel.error;
        } finally {
          http.failedReads.remove('/snapshot');
          disconnectedFavorite.release();
          await pendingDisconnectedFavorite;
        }
        expect(updateModel.resources['/favorites'], same(recoveredFavorites));
        expect(updateModel.error, recoveredError);

        // 更新状态协议使用 POST，但此端点只读。真实回复正文不替换：用 Map 身份证明
        // 后取得的新会话状态没有被内容恰好相同的旧状态覆盖，避免仅比文本漏掉回归。
        await changeUpdateSession('before');
        await updateModel.readResource('/updates/status');
        final heldRead = http.holdNext('POST', '/updates/status');
        final pendingRead = updateModel.readResource('/updates/status');
        late final DataMap freshReadStatus;
        late final String freshReadNotice, freshReadError;
        late final List<int> freshReadOperations;
        try {
          await heldRead.arrived.future.timeout(const Duration(seconds: 3));
          await changeUpdateSession('after');
          await updateModel.readResource('/updates/status');
          freshReadStatus = updateModel.resources['/updates/status']!;
          freshReadNotice = updateModel.notice;
          freshReadError = updateModel.error;
          freshReadOperations = updateModel.recentOperations
              .map((entry) => entry.id)
              .toList();
        } finally {
          heldRead.release();
          await pendingRead;
        }
        expect(updateModel.resources['/updates/status'], same(freshReadStatus));
        expect(updateModel.notice, freshReadNotice);
        expect(updateModel.error, freshReadError);
        expect(
          updateModel.recentOperations.map((entry) => entry.id).toList(),
          freshReadOperations,
        );

        // 更新服务在宿主中 Enabled=false/AutoCheck=false，真实 /updates/check 只返回
        // “已关闭”而不访问网络。额外成功正文仅为客户端时序夹具；另外保留真实拒绝与
        // 接收后传输异常两条失败路径，三者都不得向新会话写状态、notice 或操作记录。
        for (final outcome in ['success', 'rejected', 'transport-failure']) {
          await changeUpdateSession('before');
          await updateModel.readResource('/updates/status');
          final callsBefore = http.requests['POST /updates/check'] ?? 0;
          final heldWrite = http.holdNext(
            'POST',
            '/updates/check',
            replacement: outcome == 'success'
                ? {
                    'ok': true,
                    'state': 'available',
                    'currentVersion': 'synthetic-before',
                    'latestVersion': 'synthetic-late-response',
                    'hasUpdate': true,
                    'error': null,
                  }
                : null,
            failAfterRelease: outcome == 'transport-failure',
          );
          final pendingWrite = updateModel.mutate('/updates/check');
          late final DataMap freshStatus;
          late final String freshNotice, freshError;
          late final List<int> freshOperations;
          try {
            await heldWrite.arrived.future.timeout(const Duration(seconds: 3));
            expect(heldWrite.originalBody?['state'], 'disabled');
            expect(heldWrite.originalBody?['error'], contains('自动更新已关闭'));
            await changeUpdateSession('after');
            await updateModel.readResource('/updates/status');
            freshStatus = updateModel.resources['/updates/status']!;
            freshNotice = updateModel.notice;
            freshError = updateModel.error;
            freshOperations = updateModel.recentOperations
                .map((entry) => entry.id)
                .toList();
          } finally {
            heldWrite.release();
            await pendingWrite;
          }
          expect(
            updateModel.resources['/updates/status'],
            same(freshStatus),
            reason: '$outcome 的旧状态不能覆盖新游戏进程。',
          );
          expect(updateModel.notice, freshNotice, reason: outcome);
          expect(updateModel.error, freshError, reason: outcome);
          expect(
            updateModel.recentOperations.map((entry) => entry.id).toList(),
            freshOperations,
            reason: '$outcome 的过期操作不能记作新会话成功或失败。',
          );
          expect(http.requests['POST /updates/check'], callsBefore + 1);
        }

        await model.mutate('/devices/sync', body: {'deviceId': id});
        expect(model.authority['pendingSyncId'], isNull, reason: model.error);
        final stored = jsonMap(jsonDecode(await native.file.readAsString()));
        final checkpoint = jsonMap(stored['profileCheckpoint']);
        expect(
          checkpoint['profileSchemaVersion'],
          CompanionProfile.schemaVersion,
        );
        expect(
          checkpoint['profileHash'],
          model.authority['currentDeviceProfileHash'],
        );
        expect(checkpoint['registryId'], model.authority['registryId']);
        expect(native.readBacks, greaterThan(2));
        expect(model.primary, isFalse, reason: '同步 ACK 不得自动切主。');
        await model.mutate('/devices/primary', body: {'deviceId': id});
        expect(model.primary, isTrue, reason: model.error);
        expect(model.profile['automationEnabled'], isFalse);
        await model.updateProfile({'autoRareConcurrency': 3});
        expect(model.profile['autoRareConcurrency'], 3, reason: model.error);
        // 模拟自动保存控件在前一个 HTTP 尚未完成时连续修改不同配置。
        // 必须按执行时的最新权威 profile 合并，不能用入队时的旧完整快照覆盖。
        await model.updateSortMode('custom');
        await Future.wait([
          model.updateProfile({'autoRareConcurrency': 4}),
          model.updateProfile({'autoNormalConcurrency': 5}),
          model.updateSortObjective('profit', {'weight': 71}),
          model.updateSortObjective('totalCost', {'weight': 23}),
        ]);
        expect(model.error, isEmpty);
        expect(model.profile['autoRareConcurrency'], 4);
        expect(model.profile['autoNormalConcurrency'], 5);
        final sortObjectives = jsonObjects(
          jsonMap(model.profile['recommendationSortProfile'])['objectives'],
        );
        expect(
          sortObjectives.singleWhere(
            (item) => item['key'] == 'profit',
          )['weight'],
          71,
        );
        expect(
          sortObjectives.singleWhere(
            (item) => item['key'] == 'totalCost',
          )['weight'],
          23,
        );
        // 两个请求同帧入队时，旧滑块仍看到 custom；必须在真正出队后再检查模式。
        // 不得把刚保存的固定预设改成“固定名称、自定义数值”的非法组合。
        await Future.wait([
          model.updateSortMode('profit'),
          model.updateSortObjective('profit', {'weight': 7}),
        ]);
        expect(model.error, contains('评分模式已经变化'));
        expect(
          canonicalJson(model.profile['recommendationSortProfile']),
          canonicalJson(CompanionProfile.sortPreset('profit')),
        );
        // 新一轮明确切换 custom 会以队列执行时最新的 profit 参数为起点。
        await model.updateSortMode('custom');
        expect(model.error, isEmpty);
        expect(
          canonicalJson(
            jsonMap(model.profile['recommendationSortProfile'])['objectives'],
          ),
          canonicalJson(CompanionProfile.sortPreset('profit')['objectives']),
        );
        // 第二个 HTTP 连接使用同一已授权设备，模拟另一窗口合法切换模式。
        // 旧编辑必须明确失败，同时组合根补读新修订；不得自动重放 flags 或 settings。
        final customPeer = CompanionApi(
          connection: ApiConnection(
            endpoint: model.endpoint,
            token: 'flutter-contracts-synthetic-token',
            clientId: native.secrets['client-id']!,
            clientLabel: '合成配方模式观察器',
          ),
        );
        try {
          await model.readResource('/custom-recipes');
          expect(model.resources['/custom-recipes']!['modeRevision'], 0);
          await model.mutate(
            '/custom-recipes/upsert',
            query: {
              'mode': 'customer',
              'expectedModeRevision': '0',
              'customerId': '1',
              'customerName': '合成稀客',
              'foodId': '2',
              'recipeId': '102',
              'recipeName': '合成料理',
              'extraIngredientIds': '',
            },
          );
          expect(model.error, isEmpty);
          await customPeer.write(
            '/custom-recipes/settings',
            query: {'expectedModeRevision': '0', 'activeMode': 'food'},
            authorityRevision: model.authority['authorityRevision'] as int,
          );
          await model.mutate(
            '/custom-recipes/update-flags',
            query: {
              'mode': 'customer',
              'expectedModeRevision': '0',
              'scope': 'all',
              'enabled': 'false',
            },
          );
          expect(model.error, contains('mode changed'));
          final recovered = model.resources['/custom-recipes']!;
          expect(recovered['activeMode'], 'food');
          expect(recovered['modeRevision'], 1);
          expect(jsonObjects(recovered['recipes']).single['enabled'], isTrue);
          // 只有用户明确重新选择当前修订才可再次操作；补读本身不是写入成功。
          await model.mutate(
            '/custom-recipes/settings',
            query: {'expectedModeRevision': '1', 'activeMode': 'customer'},
          );
          expect(model.error, isEmpty);
          final actual = await customPeer.read('/custom-recipes');
          expect(actual['modeRevision'], 2);
          expect(jsonObjects(actual['recipes']).single['enabled'], isTrue);
          expect(jsonObjects(actual['recipes']), hasLength(1));
        } finally {
          customPeer.close();
        }
        await model.mutate(
          '/favorites/add-recipe',
          query: {
            'customerId': '1',
            'customerName': '合成稀客',
            'recipeId': '2',
            'foodTag': '甜',
          },
        );
        await model.readResource('/favorites');
        expect(
          jsonObjects(model.resources['/favorites']!['recipes'])
              .single['customerName'],
          '合成稀客',
        );
        // 该路径走真实 C# LocalApiAvailableMissionsPayload。宿主的读取委托明确
        // 抛 Forbidden；LocalApiServer 捕获后使用缓存快照的 0/0 代次，构造
        // mission-data-incomplete 与精确命令失败诊断，而非原样返回缓存中的
        // not-attached。该故障应保留为资源事实；后续库存 POST 仍由原校验拒绝。
        await model.readResource('/missions/available');
        final availableMissions = model.resources['/missions/available'];
        expect(availableMissions, isNotNull, reason: model.error);
        expect(availableMissions!['ok'], isTrue);
        expect(availableMissions['runtimeAvailable'], isFalse);
        expect(availableMissions['status'], 'mission-data-incomplete');
        expect(availableMissions['missionGeneration'], 0);
        expect(availableMissions['sourceRevision'], 0);
        expect(
          availableMissions['error'],
          'available-mission-command-failed:InvalidOperationException:'
          'Game/update operation is outside the identity P0 fixture.',
        );
        expect(availableMissions['missions'], isEmpty);
        expect(AvailableMissionReadState.tryRead(availableMissions), isNotNull);
        expect(model.error, isEmpty, reason: '非就绪的只读快照不得冒充命令失败');
        model.clearResourceSubscription();
        await model.queryRecommendations(kind: 'normal', selectedPlace: '妖怪兽道');
        expect(model.canExecute, isFalse, reason: '只读查询不可在合成白天场景取得运行许可。');
        // 真实 HTTP 返回先成功、后拒绝、再部分失败。旧成功提示不能残留；部分成功
        // 不能被描述为全批成功或自动回滚，也不能自动重发。数据和副作用仅在宿主内存。
        await model.mutate(
          '/inventory/bulk-set',
          query: {'type': 'beverage', 'ids': '9903', 'qty': '99'},
        );
        expect(model.error, isEmpty);
        expect(model.notice, '库存修改已确认：变更 1 项，未变 0 项。');
        await model.mutate(
          '/inventory/bulk-set',
          query: {'type': 'beverage', 'ids': '9902', 'qty': '99'},
        );
        expect(model.error, contains('无限库存，不能修改数量'));
        expect(model.notice, isEmpty, reason: '本次拒绝不得并列显示上次成功。');
        await model.mutate(
          '/inventory/bulk-set',
          query: {'type': 'beverage', 'ids': '9903,9902', 'qty': '99'},
        );
        expect(model.error, contains('变更 1 项，未变 0 项，失败 1 项'));
        expect(model.notice, isEmpty, reason: '部分执行仍是失败，不得刷新成伪成功。');
        await model.mutate(
          '/inventory/bulk-set',
          query: {'type': 'beverage', 'ids': '9904', 'qty': '99'},
        );
        expect(model.error, isEmpty);
        expect(model.notice, '库存修改已确认：变更 0 项，未变 1 项。');
        // 真实 Mod HTTP 包装透传数量前提到生产纯托管读写规则。合成条目9903实读3，
        // 客户端旧值20不能被解释为“加10后设30”；失败也必须清除上一条成功提示。
        await model.mutate(
          '/inventory/set',
          query: {
            'type': 'ingredient',
            'id': '9903',
            'qty': '30',
            'expectedQuantity': '20',
          },
        );
        expect(model.error, contains('库存数量已变化（原为 20，当前为 3）'));
        expect(model.error, contains('本次未修改。请刷新后再调整'));
        expect(model.notice, isEmpty);
        await model.mutate(
          '/inventory/set',
          query: {
            'type': 'ingredient',
            'id': '9903',
            'qty': '13',
            'expectedQuantity': '3',
          },
        );
        expect(model.error, isEmpty);
        expect(model.notice, '库存修改已确认，当前数量 13。');
        for (final invalid in ['', '-1', '3.5', 'unknown']) {
          await model.mutate(
            '/inventory/set',
            query: {
              'type': 'ingredient',
              'id': '9903',
              'qty': '13',
              'expectedQuantity': invalid,
            },
          );
          expect(model.error, contains('库存校验数量无效'));
          expect(model.notice, isEmpty);
        }
        await model.mutate(
          '/inventory/set',
          query: {'type': 'ingredient', 'id': '1', 'qty': '99'},
        );
        expect(model.error, contains('outside the identity P0 fixture'));
        expect(model.notice, isEmpty);
        expect(model.canExecute, isFalse);
        expect(
          native.secrets['connection-token'],
          'flutter-contracts-synthetic-token',
        );
        expect(
          await native.file.readAsString(),
          isNot(contains('flutter-contracts-synthetic-token')),
        );

        // 生产组合根重建时只恢复原生存储中的身份和服务端保存的配置，
        // 不能依赖旧 LiveWorkbenchModel 的内存变量，也没有第二个“本次许可”步骤。
        final leaseProbe = CompanionApi(
          connection: ApiConnection(
            endpoint: model.endpoint,
            token: 'flutter-contracts-synthetic-token',
            clientId: native.secrets['client-id']!,
            clientLabel: '合成租约观察器',
          ),
        );
        try {
          await model.updateProfile({'automationEnabled': true});
          expect(model.error, isEmpty);
          expect(
            (await leaseProbe.read(
              '/automation/lease',
              authorityRevision: model.authority['authorityRevision'] as int,
            ))['owned'],
            isTrue,
          );
          expect(model.canExecute, isFalse, reason: '取得租约不能绕过非经营场景。');
          // exit 使用本测试原生 HostApi 替身，仅等待会话释放，不启动或关闭真实窗口。
          await model.platformCommand('exit');
          model.dispose();
          // 模拟上一版本客户端实际保存的无 profileSchemaVersion checkpoint。
          // 恢复历史订单排序并去掉当时尚不存在的总开关，保留既有身份与修订；
          // 新组合根明确丢弃旧排序、迁移旧评分模式，再以当前权威响应落盘 schema 4。
          final previousPreferences = jsonMap(
            jsonDecode(await native.file.readAsString()),
          );
          final previousCheckpoint = Map<String, Object?>.of(
            jsonMap(previousPreferences['profileCheckpoint']),
          )..remove('profileSchemaVersion');
          previousCheckpoint['profile'] =
              Map<String, Object?>.of(jsonMap(previousCheckpoint['profile']))
                ..remove('gameUiEnabled')
                ..['serviceOrderSortMode'] = 'guest'
                // 旧版没有 custom 枚举，曾允许 balanced 名称下保存已改动的目标。
                ..['recommendationSortProfile'] = {
                  ...jsonMap(
                    jsonMap(
                      previousCheckpoint['profile'],
                    )['recommendationSortProfile'],
                  ),
                  'preset': 'balanced',
                };
          await native.file.writeAsString(
            jsonEncode({
              ...previousPreferences,
              'profileCheckpoint': previousCheckpoint,
            }),
            flush: true,
          );
          model = LiveWorkbenchModel(platform: CompanionPlatform(host: native));
          await model.initialize();
          expect(model.connected, isTrue, reason: model.error);
          expect(model.primary, isTrue);
          expect(model.authority['currentDeviceId'], id);
          final migratedPreferences = jsonMap(
            jsonDecode(await native.file.readAsString()),
          );
          final migratedCheckpoint = jsonMap(
            migratedPreferences['profileCheckpoint'],
          );
          expect(
            migratedCheckpoint['profileSchemaVersion'],
            CompanionProfile.schemaVersion,
          );
          expect(
            jsonMap(migratedCheckpoint['profile']).containsKey('gameUiEnabled'),
            isTrue,
          );
          expect(model.profile['automationEnabled'], isTrue);
          expect(
            (await leaseProbe.read(
              '/automation/lease',
              authorityRevision: model.authority['authorityRevision'] as int,
            ))['owned'],
            isTrue,
            reason: '重启没有按已保存开启配置自动获取租约。',
          );
          await model.updateProfile({'automationEnabled': false});
          await model.platformCommand('exit');
          model.dispose();
          model = LiveWorkbenchModel(platform: CompanionPlatform(host: native));
          await model.initialize();
          expect(model.profile['automationEnabled'], isFalse);
          expect(
            (await leaseProbe.read(
              '/automation/lease',
              authorityRevision: model.authority['authorityRevision'] as int,
            ))['owned'],
            isFalse,
            reason: '重启不应重开已保存关闭的自动化配置。',
          );
        } finally {
          leaseProbe.close();
        }

        // 服务 Token 轮转属于本机主设备管理，受启动参数只读锁的连接也必须
        // 能执行。退出旧会话后重建注入平台；身份、配置和凭据仍来自同一存储。
        await model.platformCommand('exit');
        model.dispose();
        final managementPlatform = _ManagementEventPlatform(host: native);
        model = LiveWorkbenchModel(platform: managementPlatform);
        await model.initialize();
        expect(model.connected, isTrue, reason: model.error);
        final originalId = model.authority['currentDeviceId'];
        final originalHash = model.authority['currentDeviceProfileHash'];
        final deviceCount = jsonObjects(model.authority['devices']).length;
        await model.refreshDiagnostics();
        expect(
          model.resources.containsKey('/logs/settings'),
          isTrue,
          reason: model.error,
        );
        expect(
          model.resources.containsKey('/logs/packages'),
          isTrue,
          reason: model.error,
        );
        expect(model.recentOperations, isEmpty, reason: '启动与诊断 GET 不写入用户操作记录');
        await model.mutate(
          '/logs/export-diagnostics',
          query: {'open': 'false'},
          localOnly: true,
        );
        expect(model.error, isEmpty, reason: model.error);
        expect(
          jsonObjects(model.resources['/logs/packages']!['items']),
          isNotEmpty,
        );
        final settingsBeforeFailure = model.resources['/logs/settings'];
        final historyBeforeFailure = model.recentOperations.length;
        http.failedReads.add('/logs/packages');
        await model.readResource('/logs/packages');
        expect(model.resources['/logs/packages']!['ok'], isFalse);
        expect(model.resources['/logs/packages']!['error'], '诊断包读取失败，请刷新重试。');
        expect(
          jsonObjects(model.resources['/logs/packages']!['items']),
          isEmpty,
          reason: '读取失败撤回旧列表，不能让用户继续删除未核验条目',
        );
        expect(model.resources['/logs/settings'], settingsBeforeFailure);
        expect(
          model.recentOperations,
          hasLength(historyBeforeFailure),
          reason: '后台读取失败不冒充用户操作',
        );
        http.failedReads.remove('/logs/packages');
        await model.refreshDiagnostics();
        expect(model.resources['/logs/packages']!['ok'], isTrue);
        expect(
          jsonObjects(model.resources['/logs/packages']!['items']),
          isNotEmpty,
        );
        await model.updateLanConnection(
          enabled: false,
          host: 'auto',
          token: 'bad value',
        );
        expect(
          model.recentOperations.first.result,
          WorkbenchOperationResult.failed,
        );
        expect(
          native.secrets['connection-token'],
          'flutter-contracts-synthetic-token',
        );
        const changedToken = 'synthetic-management-new-token';
        await model.readResource('/local-api/config');
        native.tokenWriteGate = Completer<void>();
        native.tokenWriteEntered = Completer<void>();
        final changing = model.updateLanConnection(
          enabled: false,
          host: 'auto',
          token: changedToken,
        );
        // 管理 POST 已回执、正在保存新凭据时切离设置。旧 LAN 缓存可以存在，
        // 但隐藏页面不得因迟到成功回执恢复周期 HTTP 订阅。
        await native.tokenWriteEntered!.future.timeout(
          const Duration(seconds: 10),
        );
        model.activatePage(1);
        native.tokenWriteGate!.complete();
        await changing;
        native.tokenWriteGate = null;
        native.tokenWriteEntered = null;
        expect(model.error, isEmpty, reason: model.error);
        expect(model.connected, isTrue);
        expect(model.primary, isTrue);
        expect(model.canEditConnection, isFalse, reason: '修改服务设置不解除启动参数来源锁');
        expect(model.authority['currentDeviceId'], originalId);
        expect(model.authority['currentDeviceProfileHash'], originalHash);
        expect(jsonObjects(model.authority['devices']), hasLength(deviceCount));
        expect(native.secrets['connection-token'], changedToken);
        expect(await native.file.readAsString(), isNot(contains(changedToken)));
        final configReads = http.requests['GET /local-api/config'];
        await Future<void>.delayed(const Duration(milliseconds: 5100));
        expect(
          http.requests['GET /local-api/config'],
          configReads,
          reason: '保存期间离页，至少一个真实资源轮询周期内不得读取隐藏 LAN 配置',
        );
        expect(
          model.resources['/local-api/config']?['token'],
          changedToken,
          reason: '停止隐藏订阅不能丢失已确认回执',
        );
        final stableVersion = model.connectionVersion;
        managementPlatform.activate(
          model.endpoint,
          'flutter-contracts-synthetic-token',
        );
        await Future<void>.delayed(Duration.zero);
        expect(
          model.connectionVersion,
          stableVersion,
          reason: '已替换的精确旧凭据不得回滚当前连接',
        );
        expect(native.secrets['connection-token'], changedToken);
        final obsolete = CompanionApi(
          connection: ApiConnection(
            endpoint: model.endpoint,
            token: 'flutter-contracts-synthetic-token',
            clientId: native.secrets['client-id']!,
            clientLabel: '旧凭据观察器',
          ),
        );
        try {
          await expectLater(obsolete.devices(), throwsA(isA<ApiException>()));
        } finally {
          obsolete.close();
        }
        // 改回原值 A 后，该 pair 必须退出退休表；显式原生激活和重试仍能沿用 A。
        await model.updateLanConnection(
          enabled: false,
          host: 'auto',
          token: 'flutter-contracts-synthetic-token',
        );
        expect(model.error, isEmpty, reason: model.error);
        expect(model.authority['currentDeviceId'], originalId);
        managementPlatform.activate(
          model.endpoint,
          'flutter-contracts-synthetic-token',
        );
        await model.retryConnection();
        expect(model.connected, isTrue, reason: model.error);
        expect(
          native.secrets['connection-token'],
          'flutter-contracts-synthetic-token',
        );
        expect(model.authority['currentDeviceId'], originalId);
        expect(
          model.recentOperations.where(
            (item) =>
                item.name == '保存局域网连接' &&
                item.result == WorkbenchOperationResult.success,
          ),
          hasLength(2),
        );
        expect(
          model.recentOperations.every(
            (item) => !item.summary.contains(changedToken),
          ),
          isTrue,
        );
        // Session 周期读取故意吸收异常以保持恢复能力；人工刷新历史仍必须
        // 按 state 记录失败，而不是把正常完成的 Future 当作成功回执。
        http.failedReads.add('/snapshot');
        await model.refresh();
        expect(model.recentOperations.first.name, '刷新当前状态');
        expect(
          model.recentOperations.first.result,
          WorkbenchOperationResult.failed,
        );
        expect(model.error, isNotEmpty);
        http.failedReads.remove('/snapshot');
        await model.retryConnection();
        expect(model.connected, isTrue, reason: model.error);
      } finally {
        model?.dispose();
        try {
          host.stdin.writeln();
          await host.stdin.close();
        } on Object {
          /* 失败宿主可能已结束。 */
        }
        final exit = await host.exitCode.timeout(
          const Duration(seconds: 15),
          onTimeout: () {
            host.kill();
            return -1;
          },
        );
        await lines.cancel();
        await errors.cancel();
        await fixture.delete(recursive: true);
        expect(exit, 0, reason: '$diagnostics');
      }
    }, http);
  }, timeout: const Timeout(Duration(seconds: 60)));
}
