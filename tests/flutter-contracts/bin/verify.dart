import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:companion_client/companion_client.dart';

import 'order_presentation_checks.dart';

int checks = 0;
void check(bool condition, String message) {
  checks++;
  if (!condition) throw StateError(message);
}

Future<ApiException> expectApiError(
  Future<Object?> Function() operation,
  String code,
) async {
  try {
    await operation();
  } on ApiException catch (error) {
    check(error.code == code, '错误类型应为 $code，实际 ${error.code}');
    return error;
  }
  throw StateError('预期拒绝 $code，实际成功。');
}

void expectFormat(void Function() action) {
  try {
    action();
  } on FormatException {
    checks++;
    return;
  }
  throw StateError('非法协议值未被拒绝。');
}

/// 无第三方测试框架，直接由锁定 Dart 运行；失败非零退出，不依赖可能被关闭的 assert。
Future<void> main(List<String> args) async {
  validateModels();
  checks += validateOrderPresentation();
  await validateLiveOrdersBeforeBusiness();
  await validateOrderDisplayContinuity();
  await validateSnapshotPresentationToken();
  await validateTransport();
  await validateLeaseRace();
  await validateSession();
  await validateSessionResources();
  await validateQueryLifecycle();
  await validatePageSubscriptionRaces();
  await validateHandshakeAndResume();
  if (!args.contains('--unit-only')) await validateRealHost();
  stdout.writeln('PASS: $checks 个协议/传输/状态断言。游戏运行证据=false。');
}

void validateModels() {
  final profile = CompanionProfile.defaults();
  // v1/v2 都明确删除订单排序，不能把损坏记录补成合法默认配置。
  final legacy = Map<String, Object?>.of(profile.json)
    ..remove('gameUiEnabled')
    ..['serviceOrderSortMode'] = 'guest';
  check(
    !CompanionProfile.fromCheckpoint(legacy, null).flag('gameUiEnabled'),
    '旧版全部分项关闭时总开关必须关闭。',
  );
  for (final field in CompanionProfile.gameUiFeatureFields) {
    final migrated = CompanionProfile.fromCheckpoint({
      ...legacy,
      field: true,
      'automationEnabled': true,
    }, 1);
    check(
      migrated.flag('gameUiEnabled') &&
          migrated.flag(field) &&
          migrated.automationEnabled,
      '迁移应保留$field和自动化，并开启界面辅助模块。',
    );
  }
  check(
    !CompanionProfile.fromCheckpoint({
      ...profile.copyWith({'rareSeatHighlightEnabled': true}).json,
      'serviceOrderSortMode': 'guest',
    }, 2).flag('gameUiEnabled'),
    '新配置显式关闭总开关时不得用分项反向开启。',
  );
  for (final version in [1, 2]) {
    for (final mode in ['ordered', 'guest']) {
      final source = <String, Object?>{
        ...profile.json,
        'serviceOrderSortMode': mode,
      };
      if (version == 1) source.remove('gameUiEnabled');
      final before = canonicalJson(source);
      final migrated = CompanionProfile.fromCheckpoint(source, version);
      check(
        !migrated.json.containsKey('serviceOrderSortMode') &&
            canonicalJson(source) == before &&
            canonicalJson(migrated.json) == canonicalJson(profile.json),
        'schema $version / $mode 仅删除旧排序且不修改源 checkpoint。',
      );
    }
  }
  expectFormat(
    () =>
        CompanionProfile({...profile.json, 'serviceOrderSortMode': 'ordered'}),
  );
  expectFormat(
    () => CompanionProfile.fromCheckpoint({
      ...profile.json,
      'serviceOrderSortMode': 'unknown',
    }, 2),
  );
  expectFormat(() => CompanionProfile.fromCheckpoint(profile.json, 2));
  expectFormat(() => CompanionProfile(legacy));
  expectFormat(() => CompanionProfile.fromCheckpoint(legacy, 999));
  expectFormat(() => CompanionProfile.fromCheckpoint(legacy, 1.0));
  expectFormat(
    () => CompanionProfile.fromCheckpoint({...legacy, 'unknown': false}, 1),
  );
  expectFormat(
    () => CompanionProfile.fromCheckpoint(
      Map.of(legacy)..remove('automationEnabled'),
      1,
    ),
  );
  expectFormat(
    () => CompanionProfile.fromCheckpoint({
      ...legacy,
      'rareSeatHighlightEnabled': 'true',
    }, 1),
  );
  check(!profile.automationEnabled, '首次注册不能默认自动执行。');
  check(
    canonicalJson(profile.json['recommendationSortProfile']) ==
        canonicalJson(CompanionProfile.sortPreset('balanced')),
    '首次注册必须采用业务模块的均衡预设。',
  );
  expectFormat(() => CompanionProfile.sortPreset('unknown'));
  // 模式是持久契约，不能靠当前数值反向猜测。四个固定预设禁止自由修改，
  // custom 即使与某个预设完全同值，在重启/远端读回后也仍是 custom。
  for (final preset in ['balanced', 'resources', 'profit', 'simple']) {
    final fixed = CompanionProfile.sortPreset(preset);
    check(
      jsonMap(
            profile.copyWith({
              'recommendationSortProfile': fixed,
            }).json['recommendationSortProfile'],
          )['preset'] ==
          preset,
      '固定预设 $preset 应保留明确模式。',
    );
    for (final patch in <JsonMap>[
      {'weight': 1},
      {'enabled': false},
      {'direction': 'asc'},
    ]) {
      final objectives = jsonObjects(fixed['objectives']);
      expectFormat(
        () => profile.copyWith({
          'recommendationSortProfile': {
            ...fixed,
            'objectives': [
              {...objectives.first, ...patch},
              ...objectives.skip(1),
            ],
          },
        }),
      );
    }
    final explicitCustom = profile.copyWith({
      'recommendationSortProfile': {...fixed, 'preset': 'custom'},
    });
    final restored = CompanionProfile.fromCheckpoint(
      explicitCustom.json,
      CompanionProfile.schemaVersion,
    );
    check(
      canonicalJson(restored.json) == canonicalJson(explicitCustom.json),
      '自定义 $preset 同值模式不应在读回时改回固定预设。',
    );
    check(
      jsonMap(
            CompanionProfile.fromCheckpoint(
              profile.copyWith({'recommendationSortProfile': fixed}).json,
              3,
            ).json['recommendationSortProfile'],
          )['preset'] ==
          preset,
      '旧版精确 $preset 预设应保留。',
    );
  }
  for (final version in [1, 2, 3]) {
    final fixed = CompanionProfile.sortPreset('balanced');
    final objectives = jsonObjects(fixed['objectives']);
    // 同时保留禁用、反向和非预设权重，并反转列表，验证迁移不重排旧目标。
    final changed = [
      {...objectives.first, 'weight': 17, 'enabled': false, 'direction': 'asc'},
      ...objectives.skip(1),
    ].reversed.toList();
    final source = <String, Object?>{
      ...profile.json,
      if (version < 3) 'serviceOrderSortMode': 'guest',
      'recommendationSortProfile': {...fixed, 'objectives': changed},
    };
    if (version == 1) source.remove('gameUiEnabled');
    final before = canonicalJson(source);
    final migrated = CompanionProfile.fromCheckpoint(source, version);
    final sort = jsonMap(migrated.json['recommendationSortProfile']);
    check(sort['preset'] == 'custom', '旧 schema $version 调过参数必须明确迁移成 custom。');
    check(
      canonicalJson(sort['objectives']) == canonicalJson(changed),
      '旧 schema $version 迁移改变了目标参数/顺序。',
    );
    check(canonicalJson(source) == before, '迁移不得改写原 checkpoint。');
    expectFormat(
      () => CompanionProfile.fromCheckpoint({
        ...source,
        'recommendationSortProfile': {...fixed, 'preset': 'custom'},
      }, version),
    );
  }
  expectFormat(
    () => profile.copyWith({
      'recommendationSortProfile': {
        ...CompanionProfile.sortPreset('balanced'),
        'preset': 'unknown',
      },
    }),
  );
  expectFormat(() => CompanionProfile({...profile.json, 'unknown': false}));
  expectFormat(() => profile.copyWith({'autoRareConcurrency': 5}));
  expectFormat(() => profile.copyWith({'autoNormalTakeBeverage': true}));
  expectFormat(() => profile.copyWith({'rareTargetHighlightColor': '#abcdef'}));
  expectFormat(() => PageQuery.rare(-1));
  expectFormat(() => PageQuery.normal('x' * 101));
  for (final invalid in [
    'https://127.0.0.1:32145',
    'http://example.com:32145',
    'http://8.8.8.8:32145',
    'http://127.0.0.1:32145/path',
    'http://user:pass@127.0.0.1:32145',
    'http://[::1]:32145',
    'http://127.0.0.1:32145?token=x',
    'http://127.00.0.1:32145',
  ]) {
    expectFormat(() => validateEndpoint(invalid));
  }
  check(
    validateEndpoint('http://localhost:32145/').host == '127.0.0.1',
    'localhost 未规范化。',
  );
  check(
    validateEndpoint('http://192.168.1.2:32145').host == '192.168.1.2',
    '合法 LAN 被拒绝。',
  );
  final snapshot = GameSnapshot.fromJson(snapshotJson());
  final authority = DeviceAuthorityState.fromJson(authorityJson());
  final context = BusinessSourceContext.fromSnapshot(snapshot, authority);
  final status = BusinessStatus.fromJson(businessJson(context));
  check(status.currentFor(snapshot, context), '精确当前结果未被接受。');
  final changed = GameSnapshot.fromJson({
    ...snapshot.json,
    'snapshotSignature': 'new',
  });
  check(!status.currentFor(changed, context), '旧签名被授予当前权限。');
  final changedContext = BusinessSourceContext.fromSnapshot(
    GameSnapshot.fromJson({
      ...snapshot.json,
      'automationSessionId': 'another-process',
    }),
    authority,
  );
  check(context.key != changedContext.key, '跨游戏会话没有改变展示上下文。');
  expectFormat(
    () => BusinessStatus.fromJson({...status.json, 'inputVersion': 1}),
  );
  check(
    canonicalJson({'b': 1, 'a': null}) == canonicalJson({'a': null, 'b': 1}),
    '属性顺序影响语义。',
  );
  var immutable = false;
  try {
    profile.json['automationEnabled'] = true;
  } on UnsupportedError {
    immutable = true;
  }
  check(immutable, '共享 DTO 未冻结。');
}

ApiConnection connection(
  int port, {
  String client = 'flutter-device-00000001',
}) => ApiConnection(
  endpoint: 'http://127.0.0.1:$port',
  token: 'flutter-contracts-synthetic-token',
  clientId: client,
  clientLabel: '中文设备',
);

Future<void> validateTransport() async {
  final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
  final transport = DartIoTransport();
  final captured = Completer<void>();
  final subscription = server.listen((request) async {
    try {
      final bytes = await request.fold<List<int>>(
        <int>[],
        (result, chunk) => result..addAll(chunk),
      );
      check(
        request.contentLength == bytes.length && bytes.length > 0,
        'UTF-8 Content-Length 不准确。',
      );
      check(
        request.headers.value('transfer-encoding') == null,
        'POST 不允许 chunked。',
      );
      check(
        jsonDecode(utf8.decode(bytes)) is Map<String, dynamic>,
        '正文不是 UTF-8 JSON。',
      );
      check(
        Uri.decodeComponent(
              request.headers.value('X-Mystia-Steward-Companion-Client-Label')!,
            ) ==
            '中文设备',
        '设备名称编码损坏。',
      );
      request.response.headers.contentType = ContentType.json;
      request.response.write('{"ok":true}');
      await request.response.close();
      captured.complete();
    } catch (cause, stack) {
      captured.completeError(cause, stack);
    }
  });
  try {
    await transport.request(
      connection(server.port),
      'POST',
      '/devices/register',
      body: {'中文': '值'},
    );
    await captured.future;
  } finally {
    transport.close();
    await subscription.cancel();
    await server.close(force: true);
  }

  final failureServer = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
  final failureTransport = DartIoTransport();
  final failureSubscription = failureServer.listen((request) async {
    await request.drain<void>();
    if (request.uri.path == '/redirect') {
      request.response.statusCode = 302;
      request.response.headers.set('location', 'http://example.com');
    } else {
      request.response.write('{broken');
    }
    await request.response.close();
  });
  try {
    await expectApiError(
      () => failureTransport.request(
        connection(failureServer.port),
        'GET',
        '/redirect',
      ),
      'redirect-rejected',
    );
    final error = await expectApiError(
      () => failureTransport.request(
        connection(failureServer.port),
        'POST',
        '/invalid',
        body: {'x': 1},
      ),
      'invalid-response',
    );
    check(error.possiblyApplied, '已发送写入的损坏响应必须标记结果未知。');
  } finally {
    failureTransport.close();
    await failureSubscription.cancel();
    await failureServer.close(force: true);
  }
}

class FakeTransport implements CompanionTransport {
  FakeTransport(this.handler, {this.handlePageSessions = true});
  final bool handlePageSessions;
  final Future<JsonMap> Function(
    String method,
    String path,
    JsonMap? body,
    int? revision,
  )
  handler;
  final List<String> paths = [];
  final List<({String path, Map<String, String> query})> requestQueries = [];
  @override
  Future<JsonMap> request(
    ApiConnection connection,
    String method,
    String path, {
    Map<String, String> query = const {},
    JsonMap? body,
    int? authorityRevision,
    Duration timeout = const Duration(seconds: 5),
  }) {
    paths.add(path);
    requestQueries.add((path: path, query: Map<String, String>.of(query)));
    // 普通夹具只提供无副作用的会话打开/撤销；逆序与 TTL 由独立真实协议回归覆盖。
    if (handlePageSessions && path == '/business/page-session/open') {
      return Future.value({
        'ok': true,
        'protocolVersion': 1,
        'sessionId': paths.length.toRadixString(16).padLeft(32, '0'),
      });
    }
    if (handlePageSessions &&
        path == '/business/page-session/update' &&
        body?['intent'] == null) {
      return Future.value({
        'ok': true,
        'protocolVersion': 1,
        'cancelled': true,
      });
    }
    return handler(method, path, body, authorityRevision);
  }

  @override
  void close() {}
}

Future<void> validateLeaseRace() async {
  final acquire = Completer<JsonMap>();
  final entered = Completer<void>();
  final transport = FakeTransport((method, path, body, revision) async {
    if (path.endsWith('/acquire')) {
      if (!entered.isCompleted) entered.complete();
      return acquire.future;
    }
    return leaseJson(owned: false);
  });
  final controller = AutomationLeaseController(
    CompanionApi(connection: connection(32145), transport: transport),
  );
  final enabling = controller.enable(1);
  await entered.future;
  final disabling = controller.disable();
  check(!controller.owned, '停用不能等待 HTTP 返回才撤销本地许可。');
  check(
    !transport.paths.contains('/automation/lease/release'),
    'release 不能先于未完成 acquire。',
  );
  acquire.complete(leaseJson());
  await enabling;
  await disabling;
  check(
    transport.paths.join(',') ==
        '/automation/lease/acquire,/automation/lease/release',
    '租约副作用没有按序完成。',
  );
  check(!controller.owned, '迟到 acquire 恢复了已停用许可。');
  final transientEnable = controller.enable(1);
  final immediatelyDisable = controller.disable();
  await transientEnable;
  await immediatelyDisable;
  check(
    !controller.owned &&
        transport.paths.where((path) => path.endsWith('/acquire')).length == 1,
    '等待释放期间取消 enable 仍发出了 acquire。',
  );
}

/// 用未完成的 business Future 停住慢请求：新快照到达时就必须推送现场订单，
/// 不能依赖业务计算返回或真实时钟轮询。迟到成功结果仍只读，不恢复 canExecute。
Future<void> validateLiveOrdersBeforeBusiness() async {
  final authority = authorityJson();
  final first = <String, Object?>{
    'traceId': 'R-1',
    'orderLifecycleSequence': 1,
    'deskCode': 0,
    'guestName': '旧客人',
    'foodTag': '鲜',
    'beverageTag': '甜',
    'hasServedFood': false,
    'hasServedBeverage': false,
  };
  var snapshot = <String, Object?>{
    ...snapshotJson(),
    'nightBusiness': {
      'orders': [first],
    },
    'normalBusiness': {'orders': <JsonMap>[]},
  };
  final context = BusinessSourceContext.fromSnapshot(
    GameSnapshot.fromJson(snapshot),
    DeviceAuthorityState.fromJson(authority),
  );
  final previous = <String, Object?>{
    ...businessJson(context),
    'recommendations': {
      'recommendations': [
        {
          'order': first,
          'executionPlans': [
            {'marker': 'only-old-plan'},
          ],
        },
      ],
    },
  };
  Completer<JsonMap>? response;
  final entered = Completer<void>();
  final transport = FakeTransport((method, path, body, revision) async {
    if (path == '/health') return {'clientProtocolVersion': 2};
    if (path == '/devices/register' || path == '/devices') return authority;
    if (path == '/snapshot') return snapshot;
    if (path == '/business/status') {
      if (response != null) {
        if (!entered.isCompleted) entered.complete();
        return response.future;
      }
      return previous;
    }
    if (path == '/automation/lease/acquire') return leaseJson();
    if (path == '/automation/lease/release') return leaseJson(owned: false);
    throw StateError('意外请求 $path');
  });
  final session = CompanionSession(
    api: CompanionApi(connection: connection(32145), transport: transport),
    platform: 'windows',
    appVersion: 'live-orders-test',
    persistProfile: (_) async {},
  );
  await session.connect();
  check(session.state.canExecute, '初始完整当前结果应保留既有执行门禁。');
  response = Completer<JsonMap>();
  final next = {
    ...first,
    'traceId': 'R-2',
    'orderLifecycleSequence': 2,
    'guestName': '新客人',
    'hasServedFood': true,
  };
  snapshot = {
    ...snapshot,
    'snapshotSignature': 'signature-2',
    'nightBusiness': {
      'orders': [next],
    },
  };
  final seen = <CompanionState>[];
  final listener = session.changes.listen(seen.add);
  final refresh = session.refresh();
  await entered.future;
  final rows = session.state.serviceOrders['recommendations'] as List;
  check(
    rows.length == 1 &&
        jsonMap(jsonMap(rows.single)['order'])['traceId'] == 'R-2',
    '业务响应仍未返回时必须显示新单并撤下旧单。',
  );
  check(
    jsonMap(jsonMap(rows.single)['order'])['hasServedFood'] == true &&
        jsonMap(rows.single)['presentationPending'] == true,
    '最新送达事实不能被旧方案覆盖。',
  );
  check(
    seen.any((state) => state.snapshot?.signature == 'signature-2') &&
        !session.state.canExecute,
    '现场通知必须先于慢结果，且旧业务不得取得执行资格。',
  );
  response.complete({...previous, 'pending': true, 'isCurrent': false});
  await refresh;
  check(
    (session.state.serviceOrders['recommendations'] as List).length == 1 &&
        !session.state.canExecute,
    '迟到只读结果不能复活旧单或恢复执行。',
  );
  await listener.cancel();
  await session.close();
}

/// 临时空信封/迟到旧作用域不能抹掉已核验的展示，但当前线路状态仍决定执行资格。
/// 每次都由真实 Session 刷新两个端点，避免只测纯函数而遗漏接收阶段的清空行为。
Future<void> validateOrderDisplayContinuity() async {
  final authority = authorityJson();
  final original = <String, Object?>{
    'traceId': 'R-stable',
    'orderLifecycleSequence': 1,
    'guestName': '同一现场客人',
    'deskCode': 0,
    'foodTag': '鲜',
    'beverageTag': '甜',
    'fund': 100,
    'hasServedFood': false,
    'hasServedBeverage': false,
  };
  var snapshot = <String, Object?>{
    ...snapshotJson(),
    'nightBusiness': {
      'orders': [original],
    },
    'normalBusiness': {'orders': <JsonMap>[]},
  };
  BusinessSourceContext contextFor(JsonMap value) =>
      BusinessSourceContext.fromSnapshot(
        GameSnapshot.fromJson(value),
        DeviceAuthorityState.fromJson(authority),
      );
  final initialContext = contextFor(snapshot);
  JsonMap result(String marker, JsonMap order) => {
    ...businessJson(initialContext),
    'sourceSnapshotSignature': snapshot['snapshotSignature'],
    'recommendations': {
      'recommendations': [
        {
          'order': order,
          'executionPlans': [
            {'marker': marker},
          ],
        },
      ],
    },
  };
  var reply = result('stable-old', original);
  final transport = FakeTransport((method, path, body, revision) async {
    if (path == '/health') return {'clientProtocolVersion': 2};
    if (path == '/devices/register' || path == '/devices') return authority;
    if (path == '/snapshot') return snapshot;
    if (path == '/business/status') return reply;
    if (path == '/automation/lease/acquire') return leaseJson();
    if (path == '/automation/lease/release') return leaseJson(owned: false);
    throw StateError('意外请求 $path');
  });
  final session = CompanionSession(
    api: CompanionApi(connection: connection(32145), transport: transport),
    platform: 'windows',
    appVersion: 'display-continuity-test',
    persistProfile: (_) async {},
  );
  List<JsonMap> rows() =>
      (session.state.serviceOrders['recommendations'] as List).cast<JsonMap>();
  String marker() =>
      jsonMap((rows().single['executionPlans'] as List).single)['marker']
          as String;
  try {
    await session.connect();
    check(session.state.canExecute, '初始完整当前状态应可执行。');
    final retained = session.state.displayBusiness;
    snapshot = {
      ...snapshot,
      'snapshotSignature': 'observed-next',
      'nightBusiness': {
        'orders': [
          {...original, 'lastSeenAtUtc': 'later'},
        ],
      },
    };
    final wrongContext = contextFor({
      ...snapshot,
      'nightBusinessGeneration': 999,
    });
    for (final envelope in <JsonMap>[
      {'protocolVersion': 1, 'isCurrent': false, 'pending': true},
      {...businessJson(wrongContext), 'isCurrent': false, 'pending': true},
      {
        ...businessJson(initialContext),
        'isCurrent': false,
        'pending': false,
        'error': '明确的合成求解失败',
      },
    ]) {
      reply = envelope;
      await session.refresh();
      check(
        identical(session.state.displayBusiness, retained),
        '空/旧作用域/失败响应不能清空已属于当前作用域的成功展示。',
      );
      check(
        marker() == 'stable-old' && rows().single['presentationStale'] == true,
        '已有精确匹配内容必须保留为参考，不插入无方案空档。',
      );
      check(!session.state.canExecute, '保留展示不能覆盖最新业务信封的执行门禁。');
    }
    check(session.state.business?.error == '明确的合成求解失败', '保留成功展示不能抹掉当前真实错误。');
    reply = result('stable-new', original);
    await session.refresh();
    check(marker() == 'stable-new', '下一成功方案必须正常替换旧展示。');
    final changedAuthority = CompanionState(
      connected: true,
      snapshot: session.state.snapshot,
      authority: DeviceAuthorityState.fromJson({
        ...authority,
        'activeProfileHash': 'a-different-profile',
      }),
      business: session.state.business,
      displayBusiness: session.state.displayBusiness,
    );
    final changedProfileRow = jsonMap(
      (changedAuthority.serviceOrders['recommendations'] as List).single,
    );
    check(
      changedProfileRow['presentationPending'] == true &&
          !changedProfileRow.containsKey('executionPlans'),
      '配置摘要变化时即使保留的原始展示树仍存在，也不得附到当前订单。',
    );

    final changedDemand = {...original, 'fund': 20};
    snapshot = {
      ...snapshot,
      'snapshotSignature': 'budget-changed',
      'nightBusiness': {
        'orders': [changedDemand],
      },
    };
    reply = {'protocolVersion': 1, 'isCurrent': false, 'pending': true};
    await session.refresh();
    check(
      rows().single['presentationPending'] == true &&
          !rows().single.containsKey('executionPlans'),
      '真实预算变化仍必须撤下旧方案，不能用显示稳定性跨需求借用。',
    );
    snapshot = {...snapshot, 'nightBusinessGeneration': 2};
    await session.refresh();
    check(
      session.state.displayBusiness == null &&
          rows().single['presentationPending'] == true,
      '真实场景/经营代次改变必须立即清空旧作用域。',
    );
    snapshot = {
      ...snapshot,
      'snapshotSignature': 'orders-completed',
      'nightBusiness': {'orders': <JsonMap>[]},
    };
    await session.refresh();
    check(rows().isEmpty, '完成订单不能为了保留高度继续残留。');
  } finally {
    await session.close();
  }
}

/// 顺序只由 C# 返回；显示协议版本变化时仍接受完整新投影，不能把令牌当作排序设置。
Future<void> validateSnapshotPresentationToken() async {
  final authority = authorityJson();
  final orders = <JsonMap>[
    for (final id in [1, 2])
      {'traceId': 'R-$id', 'orderLifecycleSequence': id, 'guestName': '客人$id'},
  ];
  var snapshot = <String, Object?>{
    ...snapshotJson(),
    'orderPresentationSignature': 'display-version-a',
    'nightBusiness': {'orders': orders},
    'normalBusiness': {'orders': <JsonMap>[]},
  };
  var unchanged = false;
  final transport = FakeTransport((method, path, body, revision) async {
    if (path == '/health') return {'clientProtocolVersion': 2};
    if (path == '/devices/register' || path == '/devices') return authority;
    if (path == '/snapshot')
      return unchanged
          ? {
              'unchanged': true,
              'snapshotSignature': snapshot['snapshotSignature'],
              'orderPresentationSignature':
                  snapshot['orderPresentationSignature'],
            }
          : snapshot;
    if (path == '/business/status')
      return businessJson(
        BusinessSourceContext.fromSnapshot(
          GameSnapshot.fromJson(snapshot),
          DeviceAuthorityState.fromJson(authority),
        ),
      );
    if (path == '/automation/lease/acquire') return leaseJson();
    if (path == '/automation/lease/release') return leaseJson(owned: false);
    throw StateError('意外请求 $path');
  });
  final session = CompanionSession(
    api: CompanionApi(connection: connection(32145), transport: transport),
    platform: 'windows',
    appVersion: 'display-token-test',
    persistProfile: (_) async {},
  );
  await session.connect();
  check(
    !transport.requestQueries
        .firstWhere((entry) => entry.path == '/snapshot')
        .query
        .containsKey('knownOrderPresentationSignature'),
    '首个快照不能编造展示令牌。',
  );
  snapshot = {
    ...snapshot,
    'orderPresentationSignature': 'display-version-b',
    'nightBusiness': {'orders': orders.reversed.toList()},
  };
  await session.refresh();
  check(
    transport.requestQueries
            .lastWhere((entry) => entry.path == '/snapshot')
            .query['knownOrderPresentationSignature'] ==
        'display-version-a',
    '读取新排序前应回传已知旧展示令牌。',
  );
  final rows = session.state.serviceOrders['recommendations'] as List;
  check(
    session.state.snapshot?.signature == 'signature-1' &&
        jsonMap(jsonMap(rows.first)['order'])['traceId'] == 'R-2',
    '原快照签名不变时必须按服务端新顺序显示，不能被旧缓存吞掉。',
  );
  unchanged = true;
  await session.refresh();
  check(
    transport.requestQueries
                .lastWhere((entry) => entry.path == '/snapshot')
                .query['knownOrderPresentationSignature'] ==
            'display-version-b' &&
        session.state.snapshot?.json['orderPresentationSignature'] ==
            'display-version-b',
    'unchanged 轮询必须保留且回传最新展示令牌。',
  );
  await session.close();
}

Future<void> validateSession() async {
  var snapshot = snapshotJson();
  var authority = authorityJson();
  var failPersistence = false;
  var failSnapshot = false;
  var writes = 0;
  final transport = FakeTransport((method, path, body, revision) async {
    if (path == '/health') return {'clientProtocolVersion': 2};
    if (path == '/devices/register' || path == '/devices') return authority;
    if (path == '/snapshot') {
      if (failSnapshot) throw const ApiException('offline', '合成连接中断');
      return snapshot;
    }
    if (path == '/business/status')
      return businessJson(
        BusinessSourceContext.fromSnapshot(
          GameSnapshot.fromJson(snapshot),
          DeviceAuthorityState.fromJson(authority),
        ),
      );
    if (path == '/automation/lease/acquire') return leaseJson();
    if (path == '/automation/lease/release') return leaseJson(owned: false);
    if (path == '/business/page-session/update')
      return {
        'protocolVersion': 1,
        'isCurrent': true,
        'pending': false,
        'error': null,
        'sourceSnapshotSignature': snapshot['snapshotSignature'],
        'sourceContext': BusinessSourceContext.fromSnapshot(
          GameSnapshot.fromJson(snapshot),
          DeviceAuthorityState.fromJson(authority),
        ).json,
        'result': {
          'kind': jsonMap(body!['intent'])['kind'],
          'recipes': <Object?>[],
          'beverages': <Object?>[],
        },
      };
    if (path == '/devices/sync-ack')
      return {...authority, 'pendingSyncId': null};
    throw StateError('意外请求 $path');
  });
  final session = CompanionSession(
    api: CompanionApi(connection: connection(32145), transport: transport),
    platform: 'windows',
    appVersion: 'test',
    persistProfile: (_) async {
      writes++;
      if (failPersistence) throw StateError('合成落盘失败');
    },
  );
  await session.connect();
  check(
    session.state.connected && session.state.canExecute,
    '已保存开启的主设备完成同步后没有自动获取租约。',
  );
  check(writes == 1, '首次配置没有持久化。');
  check(session.state.lease?.owned == true, '持久化配置开启后没有持有租约。');
  await session.query(PageQuery.normal('妖怪兽道'));
  check(session.state.displayPageResult?['kind'] == 'normal', '页面查询没有发布。');
  final acquired = transport.paths
      .where((path) => path == '/automation/lease/acquire')
      .length;
  snapshot = {...snapshot, 'automationSessionId': 'restarted-process'};
  await session.refresh();
  check(
    transport.paths
            .where((path) => path == '/automation/lease/acquire')
            .length ==
        acquired + 1,
    '相同 authorityRevision 的游戏重启复用了上一进程的本地租约。',
  );
  check(session.state.canExecute, '新进程重新取得租约后没有恢复当前许可。');
  // 当前执行许可仍有效时，旧对话框确认也必须在客户端被拒绝，不能依赖权限关闭侥幸拦截。
  await expectApiError(
    () => session.acknowledge(
      1,
      expectedSessionId: 'session-1',
      expectedGeneration: session.state.snapshot!.nightBusinessGeneration,
    ),
    'interaction-changed',
  );
  check(!transport.paths.contains('/automation/barriers/ack'), '旧确认被重新绑定到新进程。');
  failSnapshot = true;
  await session.refresh();
  check(
    !session.state.connected &&
        !session.state.canExecute &&
        session.state.lease == null,
    '读取失败后仍持有执行资格。',
  );
  check(
    session.state.authority!.activeProfile.automationEnabled,
    '短暂断线不应改写已保存的自动化配置。',
  );
  failSnapshot = false;
  await session.refresh();
  check(session.state.canExecute, '恢复连接后没有按已保存配置重新获取租约。');
  await session.pause();
  check(!session.state.canExecute && session.state.paused, '暂停后仍可执行。');
  await session.resume();
  check(session.state.canExecute, '前台恢复后仍要求额外的临时许可。');
  await session.close();

  authority = {...authorityJson(), 'pendingSyncId': 'pending-sync'};
  final saving = Completer<void>(), durable = Completer<void>();
  final pending = CompanionSession(
    api: CompanionApi(connection: connection(32145), transport: transport),
    platform: 'windows',
    appVersion: 'pending-profile',
    persistProfile: (_) async {
      if (!saving.isCompleted) saving.complete();
      await durable.future;
    },
  );
  final requestsBeforeSave = transport.paths.length;
  final pendingConnection = pending.connect();
  await saving.future;
  check(
    !pending.state.canExecute && pending.state.lease == null,
    '保存尚未完成时提前取得执行资格。',
  );
  check(
    !transport.paths
        .skip(requestsBeforeSave)
        .any(
          (path) =>
              path == '/devices/sync-ack' ||
              path == '/automation/lease/acquire',
        ),
    '配置回读完成前已经 ACK 或 acquire。',
  );
  durable.complete();
  await pendingConnection;
  check(pending.state.canExecute, '落盘并 ACK 后没有按开启配置自动恢复。');
  final completedRequests = transport.paths.skip(requestsBeforeSave).toList();
  check(
    completedRequests.indexOf('/devices/sync-ack') <
        completedRequests.indexOf('/automation/lease/acquire'),
    '租约获取必须晚于配置同步 ACK。',
  );
  await pending.close();

  failPersistence = true;
  final broken = CompanionSession(
    api: CompanionApi(connection: connection(32145), transport: transport),
    platform: 'android',
    appVersion: 'test',
    persistProfile: (_) async {
      throw StateError('合成写入失败');
    },
  );
  final before = transport.paths
      .where((path) => path == '/devices/sync-ack')
      .length;
  try {
    await broken.connect();
  } on StateError {
    checks++;
  }
  check(
    transport.paths.where((path) => path == '/devices/sync-ack').length ==
        before,
    '持久化失败仍发送配置 ACK。',
  );
  check(
    !broken.state.canExecute && broken.state.lease == null,
    '持久化失败仍获得租约或执行资格。',
  );
  await broken.close();
}

/// 资源初始化是独立于连接握手的门禁。使用受控 Future 精确停在 HTTP 返回边界，
/// 不依赖计时睡眠、真实图片、磁盘、Unity 或已经过期的租约偶然阻止执行。
Future<void> validateSessionResources() async {
  var resourcesReady = false;
  var durableWrites = 0;
  Completer<JsonMap>? delayedBusiness;
  Completer<void>? businessEntered;
  Completer<JsonMap>? delayedPage;
  Completer<void>? pageEntered;
  final context = BusinessSourceContext.fromSnapshot(
    GameSnapshot.fromJson(snapshotJson()),
    DeviceAuthorityState.fromJson(authorityJson()),
  );
  JsonMap page(String marker) => {
    'protocolVersion': 1,
    'isCurrent': true,
    'pending': false,
    'error': null,
    'sourceContext': context.json,
    'sourceSnapshotSignature': 'signature-1',
    'result': {
      'marker': marker,
      'recipes': <Object?>[],
      'beverages': <Object?>[],
    },
  };
  final transport = FakeTransport((method, path, body, revision) async {
    if (path == '/health') return {'clientProtocolVersion': 2};
    if (path == '/devices/register' || path == '/devices')
      return authorityJson();
    if (path == '/snapshot') return snapshotJson();
    if (path == '/business/status') {
      if (delayedBusiness != null) {
        if (!businessEntered!.isCompleted) businessEntered.complete();
        return delayedBusiness.future;
      }
      return businessJson(context);
    }
    if (path == '/business/page-session/update') {
      if (delayedPage != null) {
        if (!pageEntered!.isCompleted) pageEntered.complete();
        return delayedPage.future;
      }
      return page('resource-current');
    }
    if (path == '/automation/lease/acquire') return leaseJson();
    if (path == '/automation/lease/release') return leaseJson(owned: false);
    throw StateError('资源门禁测试出现意外请求 $path');
  });
  int requests(String path) =>
      transport.paths.where((value) => value == path).length;
  final session = CompanionSession(
    api: CompanionApi(connection: connection(32145), transport: transport),
    platform: 'windows',
    appVersion: 'resource-gate-test',
    persistProfile: (_) async {
      durableWrites++;
    },
    prepareResources: () async => resourcesReady,
  );
  try {
    await session.connect();
    check(
      session.state.connected &&
          !session.state.loading &&
          session.state.error == null,
      '图片未就绪应保持已完成的连接，不能被误报断线。',
    );
    check(
      durableWrites == 1 &&
          session.state.authority != null &&
          session.state.snapshot != null,
      '图片等待不应跳过设备配置耐久保存或清除设备/快照读取。',
    );
    check(
      session.state.business == null &&
          session.state.displayBusiness == null &&
          session.state.lease == null &&
          !session.state.canExecute,
      '图片未就绪时提前发布业务展示或执行许可。',
    );
    await session.query(PageQuery.normal('妖怪兽道'));
    check(
      requests('/business/status') == 0 &&
          requests('/business/page-session/update') == 0 &&
          requests('/automation/lease/acquire') == 0,
      '图片等待期间仍读取业务/页面推荐或获取租约。',
    );

    resourcesReady = true;
    await session.refresh();
    check(
      session.state.connected &&
          session.state.canExecute &&
          session.state.lease?.owned == true &&
          session.state.business != null &&
          session.state.displayBusiness != null &&
          session.state.displayPageResult?['marker'] == 'resource-current',
      '图片就绪后未自动恢复当前业务、页面查询与已保存的自动化配置。',
    );
    check(
      requests('/devices/register') == 1 && durableWrites == 1,
      '资源就绪恢复不应重新注册设备或重复持久化未变化的配置。',
    );

    // 已通过旧资源检查的业务读取尚未返回。使资源失效后，旧响应即便带同一业务签名，
    // 也不能填回当前状态或触发下游页面查询/租约获取。
    delayedBusiness = Completer<JsonMap>();
    businessEntered = Completer<void>();
    final pendingBusiness = session.refresh();
    await businessEntered.future;
    resourcesReady = false;
    await session.invalidateResources();
    final acquiresBeforeLateBusiness = requests('/automation/lease/acquire');
    final pagesBeforeLateBusiness = requests('/business/page-session/update');
    check(
      session.state.connected &&
          !session.state.canExecute &&
          session.state.lease == null &&
          session.state.business == null &&
          session.state.displayBusiness == null &&
          session.state.pageResult == null &&
          session.state.displayPageResult == null,
      '资源失效未立即撤回旧业务、页面结果与执行许可。',
    );
    delayedBusiness.complete(businessJson(context));
    await pendingBusiness;
    delayedBusiness = null;
    check(
      session.state.business == null &&
          session.state.displayBusiness == null &&
          session.state.pageResult == null &&
          session.state.displayPageResult == null &&
          session.state.lease == null &&
          !session.state.canExecute &&
          requests('/business/page-session/update') ==
              pagesBeforeLateBusiness &&
          requests('/automation/lease/acquire') == acquiresBeforeLateBusiness,
      '资源失效后的迟到业务结果重新发布展示、请求推荐或续租。',
    );

    resourcesReady = true;
    await session.refresh();
    check(
      session.state.canExecute && session.state.displayPageResult != null,
      '丢弃旧资源代次响应后，新的完整刷新未恢复。',
    );

    // 页面查询是业务读取之后的第二个 await；必须单独覆盖它的成功和异常迟到路径，
    // 不能只检查 businessStatus 的代次门禁而留下旧推荐或错误回流。
    for (final failReply in [false, true]) {
      delayedPage = Completer<JsonMap>();
      pageEntered = Completer<void>();
      final pendingPage = session.refresh();
      await pageEntered.future;
      resourcesReady = false;
      await session.invalidateResources();
      final acquiresBeforeLatePage = requests('/automation/lease/acquire');
      if (failReply) {
        delayedPage.completeError(
          const ApiException('old-resource-query', '旧资源代次合成错误'),
        );
      } else {
        delayedPage.complete(page('old-resource-page'));
      }
      await pendingPage;
      delayedPage = null;
      check(
        session.state.connected &&
            session.state.error == null &&
            session.state.pageError == null &&
            session.state.business == null &&
            session.state.displayBusiness == null &&
            session.state.pageResult == null &&
            session.state.displayPageResult == null &&
            session.state.lease == null &&
            !session.state.canExecute &&
            requests('/automation/lease/acquire') == acquiresBeforeLatePage,
        failReply ? '旧资源代次的推荐异常污染了当前状态。' : '旧资源代次的推荐结果重新发布或续租。',
      );
      resourcesReady = true;
      await session.refresh();
      check(
        session.state.canExecute &&
            session.state.displayPageResult?['marker'] == 'resource-current',
        '资源恢复后没有重新读取当前推荐或恢复执行许可。',
      );
    }
  } finally {
    // 即使断言失败，也停止该会话的轮询和租约定时器，保持纯内存测试可明确退出。
    await session.close();
  }
}

/// 延迟的同条件旧响应不能跨查询代次提交；推荐独立失败不改变业务租约或读取资格。
Future<void> validateQueryLifecycle() async {
  final firstEntered = Completer<void>(), firstReply = Completer<JsonMap>();
  Completer<void>? cancelledEntered;
  Completer<JsonMap>? cancelledReply;
  var pageCalls = 0, failPage = false;
  final context = BusinessSourceContext.fromSnapshot(
    GameSnapshot.fromJson(snapshotJson()),
    DeviceAuthorityState.fromJson(authorityJson()),
  );
  JsonMap page(String marker) => {
    'protocolVersion': 1,
    'isCurrent': true,
    'pending': false,
    'error': null,
    'sourceContext': context.json,
    'sourceSnapshotSignature': 'query-test',
    'result': {
      'marker': marker,
      'recipes': <Object?>[],
      'beverages': <Object?>[],
    },
  };
  final transport = FakeTransport((method, path, body, revision) async {
    if (path == '/health') return {'clientProtocolVersion': 2};
    if (path == '/devices/register' || path == '/devices')
      return authorityJson();
    if (path == '/snapshot') return snapshotJson();
    if (path == '/business/status') return businessJson(context);
    if (path == '/automation/lease/acquire') return leaseJson();
    if (path == '/automation/lease/release') return leaseJson(owned: false);
    if (path == '/business/page-session/update') {
      pageCalls++;
      if (pageCalls == 1) {
        firstEntered.complete();
        return firstReply.future;
      }
      if (failPage) throw const ApiException('query-error', '合成推荐查询失败');
      if (cancelledReply != null) {
        cancelledEntered!.complete();
        return cancelledReply.future;
      }
      return page('new-${jsonMap(body!['intent'])['selectedPlace']}');
    }
    throw StateError('意外请求 $path');
  });
  final session = CompanionSession(
    api: CompanionApi(connection: connection(32145), transport: transport),
    platform: 'windows',
    appVersion: 'test',
    persistProfile: (_) async {},
  );
  await session.connect();
  final observed = <String>[];
  final subscription = session.changes.listen((state) {
    final marker = state.displayPageResult?['marker'];
    if (marker is String) observed.add(marker);
  });
  final oldA = session.query(PageQuery.normal('A'));
  await firstEntered.future;
  final b = session.query(PageQuery.normal('B'));
  final newA = session.query(PageQuery.normal('A'));
  firstReply.complete(page('stale-A'));
  await Future.wait([oldA, b, newA]);
  check(!observed.contains('stale-A'), 'A→B→A接受了同key的旧代次结果。');
  check(
    pageCalls == 2 && session.state.displayPageResult?['marker'] == 'new-A',
    '单飞旧请求结束后未为新条件补读，或产生重复请求。',
  );
  final releases = transport.paths
      .where((path) => path.endsWith('/release'))
      .length;
  failPage = true;
  await session.query(PageQuery.normal('B'));
  check(
    session.state.connected &&
        session.state.canExecute &&
        session.state.lease?.owned == true,
    '附属推荐失败错误撤销了业务连接/租约。',
  );
  check(
    session.state.pageError?.contains('合成推荐查询失败') == true &&
        session.state.error == null,
    '推荐失败未进入独立错误状态。',
  );
  check(
    transport.paths.where((path) => path.endsWith('/release')).length ==
        releases,
    '推荐失败发出了租约释放。',
  );
  failPage = false;
  await session.query(PageQuery.normal('B'));
  check(
    session.state.pageError == null &&
        session.state.displayPageResult?['marker'] == 'new-B',
    '同条件重试未清除推荐错误。',
  );
  cancelledEntered = Completer<void>();
  cancelledReply = Completer<JsonMap>();
  final cancelled = session.query(PageQuery.normal('C'));
  await cancelledEntered.future;
  await session.query(null);
  cancelledReply.complete(page('cancelled-C'));
  await cancelled;
  check(
    session.state.displayPageResult == null &&
        !observed.contains('cancelled-C'),
    '离页取消之后接受了在途响应。',
  );
  final before = pageCalls;
  await session.refresh();
  check(
    pageCalls == before && session.state.displayPageResult == null,
    '取消查询后后台刷新仍发送推荐请求。',
  );
  await subscription.cancel();
  await session.close();
}

/// 使用受控网络门闩验证真实 Session 的订阅生命周期，不用 sleep 或轮询次数假定请求顺序。
/// 模拟服务器只维护纯托管序号；真实服务器 TTL/身份隔离由 C# 宿主与 HTTP 回归覆盖。
Future<void> validatePageSubscriptionRaces() async {
  final context = BusinessSourceContext.fromSnapshot(
    GameSnapshot.fromJson(snapshotJson()),
    DeviceAuthorityState.fromJson(authorityJson()),
  );
  var opens = 0, updates = 0, cancels = 0;
  final sequences = <String, int>{};
  final active = <String, JsonMap>{};
  Completer<void>? openGate, openEntered, updateGate, updateEntered;
  var failCancellation = false;
  final transport = FakeTransport((method, path, body, revision) async {
    if (path == '/health') return {'clientProtocolVersion': 2};
    if (path == '/devices/register' || path == '/devices')
      return authorityJson();
    if (path == '/snapshot') return snapshotJson();
    if (path == '/business/status') return businessJson(context);
    if (path == '/automation/lease/acquire') return leaseJson();
    if (path == '/automation/lease/release') return leaseJson(owned: false);
    if (path == '/business/page-session/open') {
      final id = (++opens).toRadixString(16).padLeft(32, '0');
      sequences[id] = 0;
      if (openGate != null) {
        openEntered!.complete();
        await openGate.future;
      }
      return {'ok': true, 'protocolVersion': 1, 'sessionId': id};
    }
    if (path == '/business/page-session/update') {
      final id = jsonString(body!, 'sessionId');
      final sequence = jsonInt(body, 'sequence');
      final intent = body['intent'];
      if (intent != null) {
        updates++;
        if (updateGate != null) {
          updateEntered!.complete();
          await updateGate.future;
        }
      } else {
        cancels++;
        if (failCancellation) throw const ApiException('offline', '合成撤销网络故障');
      }
      if (sequence <= sequences[id]!)
        throw const ApiException('http-409', 'page-sequence-stale');
      sequences[id] = sequence;
      if (intent == null) {
        active.remove(id);
        return {'ok': true, 'protocolVersion': 1, 'cancelled': true};
      }
      active[id] = jsonMap(intent);
      return {
        'protocolVersion': 1,
        'isCurrent': true,
        'pending': false,
        'sourceContext': context.json,
        'sourceSnapshotSignature': 'signature-1',
        'result': {
          'marker': jsonMap(intent)['selectedPlace'],
          'recipes': <Object?>[],
          'beverages': <Object?>[],
        },
      };
    }
    throw StateError('页面订阅出现意外请求 $path');
  }, handlePageSessions: false);
  final session = CompanionSession(
    api: CompanionApi(connection: connection(32145), transport: transport),
    platform: 'android',
    appVersion: 'page-subscription-test',
    persistProfile: (_) async {},
  );
  try {
    await session.connect();
    await session.refresh();
    check(opens == 0 && updates == 0, '普通轮询在隐藏推荐页创建了订阅。');
    openGate = Completer<void>();
    openEntered = Completer<void>();
    final opening = session.query(PageQuery.normal('迟到打开'));
    await openEntered.future;
    await session.query(null);
    openGate.complete();
    await opening;
    openGate = null;
    check(
      updates == 0 && active.isEmpty && session.state.displayPageResult == null,
      '隐藏之后迟到 open 仍登记计算或恢复显示。',
    );

    await session.query(PageQuery.normal('可见页面'));
    check(
      active.length == 1 &&
          session.state.displayPageResult?['marker'] == '可见页面',
      '可见页面没有主动打开并提交新意图。',
    );
    final releases = transport.paths
        .where((path) => path.endsWith('/release'))
        .length;
    await session.query(null);
    check(active.isEmpty && cancels == 1, '离页只清本地状态，没有显式撤销服务端订阅。');
    check(
      session.state.canExecute &&
          transport.paths.where((path) => path.endsWith('/release')).length ==
              releases,
      '只读页面撤销错误影响了经营自动化租约。',
    );

    updateGate = Completer<void>();
    updateEntered = Completer<void>();
    final updating = session.query(PageQuery.normal('逆序旧意图'));
    await updateEntered.future;
    await session.query(null); // 撤销先到服务端，旧 update 之后才到。
    updateGate.complete();
    await updating;
    updateGate = null;
    check(
      active.isEmpty &&
          session.state.displayPageResult == null &&
          session.state.pageError == null,
      '迟到 update 跨越撤销序号，或把旧拒绝错误显示到已经隐藏的页面。',
    );
    final before = updates;
    await session.refresh();
    check(updates == before, '隐藏后常规轮询重新恢复了页面订阅。');

    await session.query(PageQuery.normal('暂停页面'));
    session.setPageQueryVisible(false);
    await session.refresh();
    check(
      active.isEmpty && session.state.canExecute && !session.state.paused,
      'Windows 隐藏应只撤销页面，不能暂停经营或释放租约。',
    );
    final hiddenUpdates = updates;
    await session.refresh();
    check(updates == hiddenUpdates, '隐藏窗口仍在续期推荐查询。');
    session.setPageQueryVisible(true);
    await session.refresh();
    check(active.length == 1, 'Windows 恢复可见后未重建页面订阅。');
    await session.pause();
    check(active.isEmpty && session.state.paused, 'Android 暂停后未显式撤销页面意图。');
    await session.resume();
    check(active.length == 1, '恢复前台可见页面未重新登记。');
    failCancellation = true;
    await session.query(null);
    check(
      session.state.error == null &&
          session.state.canExecute &&
          session.state.displayPageResult == null,
      '撤销网络失败污染业务状态或恢复隐藏结果；服务端 TTL 应负责最后收口。',
    );
    failCancellation = false;
    await session.query(PageQuery.normal('关闭页面'));
    final beforeClose = cancels;
    await session.close();
    check(cancels == beforeClose + 1, '客户端关闭未发送当前页面撤销。');
  } finally {
    if (openGate != null && !openGate.isCompleted) openGate.complete();
    if (updateGate != null && !updateGate.isCompleted) updateGate.complete();
    await session.close();
  }
}

/// 握手失败不能绕过健康端点读取业务；Android 恢复不能复用暂停代次尚未完成的刷新。
Future<void> validateHandshakeAndResume() async {
  var compatible = false;
  Completer<JsonMap>? delayedSnapshot;
  final transport = FakeTransport((method, path, body, revision) async {
    if (path == '/health') return {'clientProtocolVersion': compatible ? 2 : 1};
    if (path == '/devices/register' || path == '/devices')
      return authorityJson();
    if (path == '/snapshot') return delayedSnapshot?.future ?? snapshotJson();
    if (path == '/business/status')
      return businessJson(
        BusinessSourceContext.fromSnapshot(
          GameSnapshot.fromJson(snapshotJson()),
          DeviceAuthorityState.fromJson(authorityJson()),
        ),
      );
    if (path == '/automation/lease/release') return leaseJson(owned: false);
    if (path == '/automation/lease/acquire') return leaseJson();
    throw StateError('意外请求 $path');
  });
  final session = CompanionSession(
    api: CompanionApi(connection: connection(32145), transport: transport),
    platform: 'android',
    appVersion: 'test',
    persistProfile: (_) async {},
  );
  await expectApiError(() => session.connect(), 'incompatible');
  await expectApiError(() => session.refresh(), 'handshake-required');
  check(transport.paths.every((path) => path == '/health'), '未完成握手仍发送注册/业务请求。');
  compatible = true;
  await session.connect();
  check(session.state.connected, '显式重新连接未从握手恢复。');
  delayedSnapshot = Completer<JsonMap>();
  final oldRefresh = session.refresh();
  await session.pause();
  final resumed = session.resume();
  delayedSnapshot.complete(snapshotJson());
  delayedSnapshot = null;
  await oldRefresh;
  await resumed;
  check(
    session.state.connected &&
        !session.state.paused &&
        session.state.business != null,
    '暂停期间的迟到刷新阻断恢复后的完整刷新。',
  );
  final before = transport.paths
      .where((path) => path == '/business/status')
      .length;
  await session.refresh();
  check(
    transport.paths.where((path) => path == '/business/status').length ==
        before + 1,
    '恢复后的 single-flight 未释放。',
  );
  await session.close();
}

/// 从旧 identity P0 复用真实 Mod reader/router/store 宿主；仅使用临时合成数据。
Future<void> validateRealHost() async {
  final root = Directory.current.path;
  final temporaryRoot = await Directory('$root/../../temp')
      .create(recursive: true);
  final fixture = await temporaryRoot.createTemp('flutter-contract-');
  final profileFile = File('${fixture.path}/profile.json');
  await profileFile.writeAsString(
    jsonEncode(CompanionProfile.defaults().json),
    flush: true,
  );
  final dll = '$root/host/bin/Release/net6.0/FlutterContractsHost.dll';
  if (!File(dll).existsSync()) throw StateError('请先构建真实 HTTP host：$dll');
  // 复用同一合成配置串行验证 C# /snapshot 实时排序；此专项仅绑定临时回环端口，
  // 完成后才启动常规宿主，不创建第二份配置或与其争用文件/业务线程。
  final presentation = await Process.run(
    'C:/Program Files/dotnet/dotnet.exe',
    [dll, '--snapshot-presentation', profileFile.path],
    stdoutEncoding: utf8,
    stderrEncoding: utf8,
  );
  check(
    presentation.exitCode == 0,
    '真实 C# 快照展示专项失败：${presentation.stdout}\n${presentation.stderr}',
  );
  stdout.write(presentation.stdout);
  // 连接密码更新及诊断包精确删除复用生产路由，所有文件、凭据与监听均为隔离合成输入。
  final management = await Process.run(
    'C:/Program Files/dotnet/dotnet.exe',
    [dll, '--management', profileFile.path],
    stdoutEncoding: utf8,
    stderrEncoding: utf8,
  );
  check(
    management.exitCode == 0,
    '真实 C# 连接与诊断管理专项失败：${management.stdout}\n${management.stderr}',
  );
  stdout.write(management.stdout);
  final host = await Process.start('C:/Program Files/dotnet/dotnet.exe', [
    dll,
    'recovery',
    profileFile.path,
  ]);
  final lines = StreamIterator(
    host.stdout.transform(utf8.decoder).transform(const LineSplitter()),
  );
  final errors = StringBuffer();
  final errorSubscription = host.stderr
      .transform(utf8.decoder)
      .listen(errors.write);
  final apis = <CompanionApi>[];
  try {
    if (!await lines.moveNext().timeout(const Duration(seconds: 15)))
      throw StateError('HTTP host 未就绪：$errors');
    final ready = jsonMap(jsonDecode(lines.current));
    final presentationFixture = jsonMap(ready['orderPresentation']);
    final actualOrders = projectLiveServiceOrders(
      jsonMap(presentationFixture['snapshot']),
      jsonMap(presentationFixture['result']),
      contextMatches: true,
      current: true,
    );
    for (final kind in ['recommendations', 'normalOrderDetailPlans']) {
      final rows = actualOrders[kind] as List;
      check(
        rows.length == 1 &&
            jsonMap(rows.single)['presentationPending'] == false,
        '真实 C# DTO 与 $kind 的 order 不可联结，不能永久等待方案。',
      );
    }
    // 这里读取的是实际生产 C# 导出，不以另一份测试常量证明自身正确。
    final sortPresets = jsonMap(ready['sortPresets']);
    for (final preset in ['balanced', 'resources', 'profit', 'simple']) {
      check(
        canonicalJson(CompanionProfile.sortPreset(preset)) ==
            canonicalJson(sortPresets[preset]),
        'Dart 表单预设 $preset 与实际 C# 默认配置不同。',
      );
    }
    final port = jsonInt(ready, 'port');
    check(
      jsonString(ready, 'kind') == 'flutter-contracts-real-local-api',
      '不是真实 HTTP host。',
    );
    CompanionApi api(String id) {
      final result = CompanionApi(
        connection: connection(port, client: id),
        transport: RecordingHttpTransport(),
      );
      apis.add(result);
      return result;
    }

    final primary = api('p0-old-device-00000001');
    final secondary = api('flutter-secondary-00001');
    check(
      (await primary.read('/health'))['clientProtocolVersion'] == 2,
      '服务端没有新版协议握手。',
    );
    var p = await primary.register(
      platform: 'windows',
      appVersion: 'test',
      profile: CompanionProfile.defaults(),
    );
    var s = await secondary.register(
      platform: 'android',
      appVersion: 'test',
      profile: CompanionProfile.defaults(),
    );
    check(p.currentDeviceIsPrimary && !s.currentDeviceIsPrimary, '第二设备抢占主设备。');
    for (final path in manualWritePaths) {
      await expectApiError(
        () => secondary.write(path, authorityRevision: s.authorityRevision),
        'http-403',
      );
      await expectApiError(() => primary.write(path), 'http-403');
    }
    await expectApiError(
      () =>
          api('unregistered-device-0001')
              .write('/inventory/set', authorityRevision: p.authorityRevision),
      'http-403',
    );
    check(
      s.devices.any((device) => device['label'] == '中文设备'),
      '生产 HTTP 丢失中文设备名。',
    );
    await expectApiError(
      () => secondary.updateProfile(s, s.currentDeviceProfile),
      'http-403',
    );
    check(
      !(await secondary.acquireLease(s.authorityRevision)).owned,
      '非主设备取得租约。',
    );
    final stale = p;
    p = await primary.updateProfile(
      p,
      p.currentDeviceProfile.copyWith({'autoRareConcurrency': 3}),
    );
    check(p.authorityRevision > stale.authorityRevision, '配置更新没有推进 CAS。');
    for (final path in manualWritePaths) {
      await expectApiError(
        () => primary.write(path, authorityRevision: stale.authorityRevision),
        'http-403',
      );
    }
    await expectApiError(
      () => primary.updateProfile(stale, stale.currentDeviceProfile),
      'http-409',
    );
    check(
      !(await primary.acquireLease(stale.authorityRevision)).owned,
      '旧权限仍能取得租约。',
    );
    s = await secondary.devices();
    s = await secondary.syncFromPrimary(s, s.currentDeviceId);
    check(
      s.pendingSyncId != null &&
          s.currentDeviceProfileHash == p.activeProfileHash,
      '同步未创建可核验 pending。',
    );
    await expectApiError(
      () => secondary.setPrimary(s, s.currentDeviceId),
      'http-409',
    );
    final restored = File('${fixture.path}/restored.json');
    await restored.writeAsString(jsonEncode(s.json), flush: true);
    final readBack = DeviceAuthorityState.fromJson(
      jsonMap(jsonDecode(await restored.readAsString())),
    );
    check(
      readBack.currentDeviceProfileHash == s.currentDeviceProfileHash,
      '落盘回读不一致。',
    );
    s = await secondary.acknowledgeSync(readBack);
    check(s.pendingSyncId == null, '真实 ACK 没有完成同步。');
    s = await secondary.setPrimary(s, s.currentDeviceId);
    check(s.currentDeviceIsPrimary, '明确切主没有生效。');
    check(
      (await secondary.acquireLease(s.authorityRevision)).owned,
      '新主设备不能获取租约。',
    );
    final snapshot = GameSnapshot.fromJson(await secondary.read('/snapshot'));
    await expectApiError(
      () => secondary.acknowledge(1, s, snapshot),
      'http-409',
    );
    await expectApiError(
      () => secondary.write(
        '/automation/barriers/ack',
        query: {'sequence': '1'},
        authorityRevision: s.authorityRevision,
      ),
      'http-400',
    );
    await expectApiError(
      () => secondary.write('/orders/prepare-next'),
      'http-404',
    );
    final unchanged = await secondary.read(
      '/snapshot',
      query: {
        'knownSignature': snapshot.signature,
        'knownOrderPresentationSignature': jsonString(
          snapshot.json,
          'orderPresentationSignature',
        ),
      },
    );
    check(unchanged['unchanged'] == true, '签名缓存不是当前协议。');
    final missingDisplayToken = await secondary.read(
      '/snapshot',
      query: {'knownSignature': snapshot.signature},
    );
    check(
      missingDisplayToken['unchanged'] != true,
      '仅有领域快照签名不能省略可能已改变排序的完整展示快照。',
    );
    final business = await secondary.businessStatus();
    check(business.json['protocolVersion'] == 1, '业务信封版本失配。');
    final page = (await secondary.updatePageSession(
      await secondary.openPageSession(),
      1,
      PageQuery.normal('妖怪兽道'),
    ))!;
    check(page.json['protocolVersion'] == 1, '真实页面查询没有经过新 Dart 传输。');
    // 真实 HTTP 与生产 C# 存储：只撤销属于该设备的随机会话，旧路由没有兼容分支。
    final pageSession = await secondary.openPageSession();
    await secondary.updatePageSession(pageSession, 1, PageQuery.normal('妖怪兽道'));
    await secondary.updatePageSession(pageSession, 3, null);
    await expectApiError(
      () =>
          secondary.updatePageSession(pageSession, 2, PageQuery.normal('迟到请求')),
      'http-409',
    );
    await expectApiError(
      () => primary.updatePageSession(pageSession, 4, null),
      'http-409',
    );
    await expectApiError(
      () => secondary.write(
        '/business/query',
        body: PageQuery.normal('旧路由').json,
      ),
      'http-404',
    );
    await secondary.releaseLease(s.authorityRevision);
    await validateResources(secondary);
    // 手工写不需要 lease，但队列中保留的原修订不得跨越切主；真实 HTTP 负责准入和结果。
    final queuedCases = <String, Map<String, String>>{
      '/inventory/set': {'type': 'ingredient', 'id': '9901', 'qty': '99'},
      '/inventory/bulk-set': {
        'type': 'ingredient',
        'ids': '9901,9902',
        'qty': '99',
      },
      '/rare-guests/invite': {
        'guestId': '1',
        'scope': 'fixture-queued',
        'expectedDaySceneGeneration': '1',
        'expectedMapLabel': '合成地图',
      },
      '/rare-guests/invite-all': {
        'scope': 'fixture-queued',
        'levels': '1',
        'expectedDaySceneGeneration': '1',
        'expectedMapLabel': '合成地图',
      },
    };
    var writer = secondary;
    var nextWriter = primary;
    for (final test in queuedCases.entries) {
      final authority = await writer.devices();
      final queued = expectApiError(
        () => writer.write(
          test.key,
          authorityRevision: authority.authorityRevision,
          query: test.value,
        ),
        'business-rejected',
      );
      check(
        await lines.moveNext().timeout(const Duration(seconds: 5)) &&
            jsonMap(jsonDecode(lines.current))['kind'] == 'manual-write-queued',
        '${test.key} 未进入可控待执行队列。',
      );
      final changed = await writer.setPrimary(
        authority,
        nextWriter.connection.clientId,
      );
      check(!changed.currentDeviceIsPrimary, '${test.key} 的等待阶段阻塞了切主。');
      host.stdin.writeln('release-manual-write');
      await host.stdin.flush();
      await queued;
      host.stdin.writeln('manual-write-effects');
      await host.stdin.flush();
      check(
        await lines.moveNext().timeout(const Duration(seconds: 5)) &&
            jsonInt(jsonMap(jsonDecode(lines.current)), 'count') == 0,
        '${test.key} 切主后仍产生副作用。',
      );
      final previous = writer;
      writer = nextWriter;
      nextWriter = previous;
    }
    // 四条路径各自验证正向许可，避免接线错误导致某条队列永远拒绝；无需 acquire lease。
    var expectedEffects = 0;
    for (final test in queuedCases.entries) {
      final permitted = writer.write(
        test.key,
        query: test.value,
        authorityRevision: (await writer.devices()).authorityRevision,
      );
      check(
        await lines.moveNext().timeout(const Duration(seconds: 5)) &&
            jsonMap(jsonDecode(lines.current))['kind'] == 'manual-write-queued',
        '${test.key} 正向写入未进入队列。',
      );
      host.stdin.writeln('release-manual-write');
      await host.stdin.flush();
      check(
        (await permitted)['ok'] == true,
        '${test.key} 当前主设备无 lease 的写入被错误拒绝。',
      );
      host.stdin.writeln('manual-write-effects');
      await host.stdin.flush();
      expectedEffects += 1;
      check(
        await lines.moveNext().timeout(const Duration(seconds: 5)) &&
            jsonInt(jsonMap(jsonDecode(lines.current)), 'count') ==
                expectedEffects,
        '${test.key} 有效许可必须恰好执行一次副作用。',
      );
    }
    final confirming = CompanionSession(
      api: secondary,
      platform: 'android',
      appVersion: 'confirmation-test',
      persistProfile: (_) async {},
    );
    try {
      await confirming.connect();
      Future<void> advance(String command) async {
        host.stdin.writeln(command);
        await host.stdin.flush();
        check(
          await lines.moveNext().timeout(const Duration(seconds: 5)) &&
              jsonMap(jsonDecode(lines.current))['kind'] ==
                  'confirmation-scope',
          '合成宿主没有确认边界推进。',
        );
        await confirming.refresh();
      }

      await advance('confirmation-before');
      final before = confirming.state.snapshot!;
      final sequence = jsonInt(
        jsonObjects(before.json['automationEvents']).single,
        'sequence',
      );
      await advance('confirmation-after');
      final after = confirming.state.snapshot!;
      check(
        sequence ==
                jsonInt(
                  jsonObjects(after.json['automationEvents']).single,
                  'sequence',
                ) &&
            before.automationSessionId != after.automationSessionId &&
            before.nightBusinessGeneration != after.nightBusinessGeneration,
        '真实 HTTP 快照未覆盖跨进程/代次的相同 sequence。',
      );
      final transport = secondary.transport as RecordingHttpTransport;
      final sentBefore = transport.barrierRequests;
      await expectApiError(
        () => confirming.acknowledge(
          sequence,
          expectedSessionId: before.automationSessionId,
          expectedGeneration: before.nightBusinessGeneration,
        ),
        'interaction-changed',
      );
      await expectApiError(
        () => confirming.acknowledge(
          sequence,
          expectedSessionId: after.automationSessionId,
          expectedGeneration: before.nightBusinessGeneration,
        ),
        'interaction-changed',
      );
      check(transport.barrierRequests == sentBefore, '前置确认失败仍发出了 ACK HTTP 请求。');
    } finally {
      await confirming.close();
    }
    await validateSavedAutomationHttp(api, fixture);
  } catch (_) {
    stderr.writeln(errors);
    rethrow;
  } finally {
    for (final api in apis) {
      api.close();
    }
    try {
      host.stdin.writeln();
      await host.stdin.close();
    } on Object {
      /* 已失败宿主可能提前关闭 stdin；仍等待并记录真实退出码。 */
    }
    final exit = await host.exitCode.timeout(
      const Duration(seconds: 15),
      onTimeout: () {
        host.kill();
        return -1;
      },
    );
    await lines.cancel();
    await errorSubscription.cancel();
    await fixture.delete(recursive: true);
    check(exit == 0, '真实宿主退出失败：$errors');
  }
}

/// 使用同一个真实 C# 身份存储反复创建全新 Dart 会话，证明恢复来自服务端保存的配置。
/// 该宿主仅发布合成非经营快照：可以验证租约与持久化，不能冒充真实订单执行证据。
Future<void> validateSavedAutomationHttp(
  CompanionApi Function(String) createApi,
  Directory fixture,
) async {
  final probe = createApi('flutter-secondary-00001');
  final authority = await probe.devices();
  final ownerId = authority.primaryDeviceId;
  final owner = createApi(ownerId);
  var expected = await owner.devices();
  expected = await owner.updateProfile(
    expected,
    expected.currentDeviceProfile.copyWith({'automationEnabled': true}),
  );
  final sessions = <CompanionSession>[];
  CompanionSession open(String id, {bool initialEnabled = false}) {
    final session = CompanionSession(
      api: createApi(id),
      platform: 'windows',
      appVersion: 'saved-automation-test',
      initialProfile: CompanionProfile.defaults().copyWith({
        'automationEnabled': initialEnabled,
      }),
      persistProfile: (state) async {
        final file = File('${fixture.path}/saved-automation-$id.json');
        await file.writeAsString(jsonEncode(state.json), flush: true);
        final readBack = DeviceAuthorityState.fromJson(
          jsonMap(jsonDecode(await file.readAsString())),
        );
        check(
          readBack.currentDeviceProfileHash == state.currentDeviceProfileHash &&
              readBack.currentDeviceProfileRevision ==
                  state.currentDeviceProfileRevision,
          '合成持久化回读与权威配置不一致。',
        );
      },
    );
    sessions.add(session);
    return session;
  }

  try {
    final first = open(ownerId);
    await first.connect();
    check(
      first.state.authority!.activeProfile.automationEnabled &&
          first.state.lease?.owned == true,
      '已保存开启的主设备启动后没有自动 acquire。',
    );
    check(!first.state.canExecute, '合成非经营快照不得开放场景操作。');
    await first.close();
    check(
      (await owner.read(
            '/automation/lease',
            authorityRevision: first.state.authority!.authorityRevision,
          ))['owned'] ==
          false,
      '关闭客户端未释放真实服务端租约。',
    );

    final restarted = open(ownerId);
    await restarted.connect();
    check(restarted.state.lease?.owned == true, '新建会话未自动恢复已保存配置，仍依赖上次进程内状态。');
    await restarted.updateProfile(
      restarted.state.authority!.activeProfile.copyWith({
        'automationEnabled': false,
      }),
    );
    check(
      !restarted.state.authority!.activeProfile.automationEnabled &&
          restarted.state.lease == null,
      '持久关闭自动化后未释放租约。',
    );
    await restarted.close();

    // 即便启动缓存与服务端相反，也必须服从已经保存的服务端设备配置。
    final disabled = open(ownerId, initialEnabled: true);
    await disabled.connect();
    check(
      !disabled.state.authority!.activeProfile.automationEnabled &&
          disabled.state.lease == null,
      '启动缓存覆盖了服务端保存的关闭状态。',
    );
    await disabled.updateProfile(
      disabled.state.authority!.activeProfile.copyWith({
        'automationEnabled': true,
      }),
    );
    check(disabled.state.lease?.owned == true, '保存自动化开启后仍需要额外的本次许可。');

    final observer = open('automation-observer-00001', initialEnabled: true);
    await observer.connect();
    check(
      observer.state.authority!.activeProfile.automationEnabled &&
          !observer.state.authority!.currentDeviceIsPrimary &&
          observer.state.lease == null,
      '查看设备因读取了开启配置而抢占租约。',
    );
    await observer.syncFromPrimary();
    check(
      observer.state.authority!.pendingSyncId == null &&
          observer.state.lease == null,
      '同步配置 ACK 隐式切主或取得租约。',
    );
    await disabled.close();
    await observer.setPrimary(observer.state.authority!.currentDeviceId);
    check(
      observer.state.authority!.currentDeviceIsPrimary &&
          observer.state.lease?.owned == true,
      '明确切主后没有按已确认的开启配置恢复租约。',
    );
    final oldOwner = open(ownerId, initialEnabled: true);
    await oldOwner.connect();
    check(
      !oldOwner.state.authority!.currentDeviceIsPrimary &&
          oldOwner.state.lease == null,
      '旧主设备重开客户端后夺回租约。',
    );
  } finally {
    for (final session in sessions.reversed) {
      await session.close();
    }
  }
}

/// 记录实际传输调用，不替换 HTTP 响应；用于证明确认边界失败发生在发包之前。
class RecordingHttpTransport extends DartIoTransport {
  int barrierRequests = 0;
  @override
  Future<JsonMap> request(
    ApiConnection connection,
    String method,
    String path, {
    Map<String, String> query = const {},
    JsonMap? body,
    int? authorityRevision,
    Duration timeout = const Duration(seconds: 5),
  }) {
    if (path == '/automation/barriers/ack') barrierRequests++;
    return super.request(
      connection,
      method,
      path,
      query: query,
      body: body,
      authorityRevision: authorityRevision,
      timeout: timeout,
    );
  }
}

/// 与产品手工写表一致的拒绝矩阵；每条端点验证查看设备、缺失修订和过期修订。
const manualWritePaths = [
  '/favorites/add-recipe',
  '/favorites/remove-recipe',
  '/favorites/add-beverage',
  '/favorites/remove-beverage',
  '/custom-recipes/upsert',
  '/custom-recipes/remove',
  '/custom-recipes/settings',
  '/custom-recipes/update-flags',
  '/custom-recipes/move',
  '/inventory/set',
  '/inventory/bulk-set',
  '/rare-guests/invite',
  '/rare-guests/invite-all',
  '/orders/rare/dismiss',
  '/logs/config',
  '/logs/console',
  '/logs/open-folder',
  '/logs/export-diagnostics',
  '/logs/packages/delete',
  '/diagnostics/automation-decision',
  '/local-api/config',
  '/updates/download',
  '/updates/install-on-exit',
];

/// 正向资源测试固定使用调用前已读取的修订，绝不遇到拒绝后自动读取新修订重放写入。
class FixedRevisionTransport implements CompanionTransport {
  FixedRevisionTransport(this.inner, this.revision);
  final CompanionTransport inner;
  final int revision;
  @override
  Future<JsonMap> request(
    ApiConnection connection,
    String method,
    String path, {
    Map<String, String> query = const {},
    JsonMap? body,
    int? authorityRevision,
    Duration timeout = const Duration(seconds: 5),
  }) => inner.request(
    connection,
    method,
    path,
    query: query,
    body: body,
    authorityRevision: authorityRevision ?? revision,
    timeout: timeout,
  );
  @override
  void close() {} // 借用外层真实 transport，不取得其生命周期所有权。
}

/// 持久化资源使用真实生产 store；运行时委托明确失败，证明错误不会伪装为成功空结果。
Future<void> validateResources(CompanionApi source) async {
  final api = CompanionApi(
    connection: source.connection,
    transport: FixedRevisionTransport(
      source.transport,
      (await source.devices()).authorityRevision,
    ),
  );
  final initialUpdate = await api.updateStatus();
  check(initialUpdate['state'] == 'disabled', '隔离宿主不应启动真实更新网络请求。');
  await expectApiError(() => api.write('/updates/check'), 'business-rejected');
  final failedUpdate = await api.updateStatus();
  check(
    failedUpdate['ok'] == true &&
        jsonString(failedUpdate, 'error').isNotEmpty &&
        failedUpdate['staged'] == false,
    '更新读取应保留历史错误及完整资源事实，而不能把读取错误混同为新的安装写入。',
  );
  final rare = (await api.updatePageSession(
    await api.openPageSession(),
    1,
    PageQuery.rare(1, foodTag: '甜', beverageTag: '低酒精'),
  ))!;
  check(rare.json['protocolVersion'] == 1, '稀客页面查询协议不匹配。');
  final initial = await api.read('/favorites');
  check(
    jsonObjects(initial['recipes']).isEmpty &&
        jsonObjects(initial['beverages']).isEmpty,
    '合成宿主意外读取了真实收藏。',
  );
  final addRecipe = await api.write(
    '/favorites/add-recipe',
    query: {
      'customerId': '1',
      'customerName': '合成稀客',
      'foodTag': '甜',
      'recipeId': '5',
      'extraIngredientIds': '3,2,3',
    },
  );
  final recipe = jsonObjects(jsonMap(addRecipe['favorites'])['recipes']).single;
  check(
    recipe['customerName'] == '合成稀客' &&
        canonicalJson(recipe['extraIngredientIds']) == '[2,3]',
    '收藏中文参数或服务端配料规范化丢失。',
  );
  final addBeverage = await api.write(
    '/favorites/add-beverage',
    query: {
      'customerId': '1',
      'customerName': '合成稀客',
      'beverageTag': '低酒精',
      'beverageId': '7',
    },
  );
  final beverage = jsonObjects(jsonMap(addBeverage['favorites'])['beverages'])
      .single;
  check(beverage['beverageId'] == 7, '饮料收藏未经过真实 store。');
  await api.write(
    '/favorites/remove-recipe',
    query: {'id': jsonString(recipe, 'id')},
  );
  await api.write(
    '/favorites/remove-beverage',
    query: {'id': jsonString(beverage, 'id')},
  );
  final removed = await api.read('/favorites');
  check(
    jsonObjects(removed['recipes']).isEmpty &&
        jsonObjects(removed['beverages']).isEmpty,
    '收藏删除未落盘。',
  );
  await expectApiError(
    () => api.write('/favorites/add-recipe', query: {'customerId': 'invalid'}),
    'business-rejected',
  );

  final upsert = await api.write(
    '/custom-recipes/upsert',
    query: {
      'mode': 'customer',
      'expectedModeRevision': '0',
      'customerId': '1',
      'customerName': '合成稀客',
      'foodTag': '甜',
      'foodId': '5',
      'recipeId': '5',
      'recipeName': '合成菜谱',
      'extraIngredientIds': '2,3',
      'enabled': 'true',
      'pinToTop': 'false',
    },
  );
  final entry = jsonObjects(jsonMap(upsert['customRecipes'])['recipes']).single;
  final id = jsonString(entry, 'id');
  check(entry['recipeName'] == '合成菜谱', '自定义菜谱 UTF-8 数据未落盘。');
  await api.write(
    '/custom-recipes/update-flags',
    query: {
      'mode': 'customer',
      'expectedModeRevision': '0',
      'scope': 'entry',
      'id': id,
      'pinToTop': 'true',
    },
  );
  await api.write(
    '/custom-recipes/settings',
    query: {'expectedModeRevision': '0', 'enabled': 'false'},
  );
  final saved = await api.read('/custom-recipes');
  check(
    saved['version'] == 2 &&
        saved['activeMode'] == 'customer' &&
        saved['modeRevision'] == 0 &&
        saved['enabled'] == false &&
        entry['mode'] == 'customer' &&
        jsonObjects(saved['recipes']).single['pinToTop'] == true,
    '自定义菜谱标记或总开关未落盘。',
  );
  await expectApiError(
    () => api.write(
      '/custom-recipes/move',
      query: {
        'mode': 'customer',
        'expectedModeRevision': '0',
        'id': id,
        'direction': 'sideways',
      },
    ),
    'business-rejected',
  );
  // 所有写入均绑定读取时的模式修订；总开关不改变修订，切换往返也不得复用旧弹窗。
  for (final bad in <Map<String, String>>[
    {'id': id},
    {'mode': 'customer', 'id': id},
    {'mode': 'invalid', 'expectedModeRevision': '0', 'id': id},
    {'mode': 'food', 'expectedModeRevision': '0', 'id': id},
  ]) {
    await expectApiError(
      () => api.write('/custom-recipes/remove', query: bad),
      'business-rejected',
    );
  }
  await api.write(
    '/custom-recipes/move',
    query: {
      'mode': 'customer',
      'expectedModeRevision': '0',
      'id': id,
      'direction': 'up',
    },
  );
  await api.write(
    '/custom-recipes/settings',
    query: {'expectedModeRevision': '0', 'activeMode': 'food'},
  );
  final foodUpsert = await api.write(
    '/custom-recipes/upsert',
    query: {
      'mode': 'food',
      'expectedModeRevision': '1',
      'customerId': '1',
      'customerName': '料理模式绑定稀客',
      'foodId': '5',
      'recipeId': '5',
      'recipeName': '料理模式合成配方',
      'foodTag': '甜',
      'extraIngredientIds': '3',
    },
  );
  final foodId = jsonString(
    jsonObjects(jsonMap(foodUpsert['customRecipes'])['recipes'])
        .singleWhere((row) => row['mode'] == 'food'),
    'id',
  );
  await expectApiError(
    () => api.write(
      '/custom-recipes/remove',
      query: {'mode': 'food', 'expectedModeRevision': '1', 'id': id},
    ),
    'business-rejected',
  );
  await expectApiError(
    () => api.write(
      '/custom-recipes/upsert',
      query: {
        'mode': 'food',
        'expectedModeRevision': '1',
        'id': id,
        'customerId': '1',
        'customerName': '错误迁入',
        'foodId': '5',
        'recipeId': '5',
        'recipeName': '不应保存',
      },
    ),
    'business-rejected',
  );
  await api.write(
    '/custom-recipes/update-flags',
    query: {
      'mode': 'food',
      'expectedModeRevision': '1',
      'scope': 'all',
      'pinToTop': 'false',
    },
  );
  final dual = await api.read('/custom-recipes');
  final dualEntries = jsonObjects(dual['recipes']);
  check(
    dual['activeMode'] == 'food' &&
        dual['modeRevision'] == 1 &&
        dualEntries.length == 2 &&
        dualEntries.singleWhere(
              (row) => row['mode'] == 'customer',
            )['pinToTop'] ==
            true &&
        dualEntries.singleWhere((row) => row['mode'] == 'food')['pinToTop'] ==
            false,
    '两模式条目或批量标记未正确隔离。',
  );
  await api.write(
    '/custom-recipes/settings',
    query: {'expectedModeRevision': '1', 'activeMode': 'customer'},
  );
  await expectApiError(
    () => api.write(
      '/custom-recipes/remove',
      query: {'mode': 'customer', 'expectedModeRevision': '0', 'id': id},
    ),
    'business-rejected',
  );
  await expectApiError(
    () => api.write(
      '/custom-recipes/settings',
      query: {'expectedModeRevision': '0', 'enabled': 'true'},
    ),
    'business-rejected',
  );
  await api.write(
    '/custom-recipes/remove',
    query: {'mode': 'customer', 'expectedModeRevision': '2', 'id': id},
  );
  check(
    jsonObjects((await api.read('/custom-recipes'))['recipes']).single['id'] ==
        foodId,
    '删除客人模式条目误改料理模式绑定。',
  );
  await api.write(
    '/custom-recipes/settings',
    query: {'expectedModeRevision': '2', 'activeMode': 'food'},
  );
  await api.write(
    '/custom-recipes/remove',
    query: {'mode': 'food', 'expectedModeRevision': '3', 'id': foodId},
  );
  check(
    jsonObjects((await api.read('/custom-recipes'))['recipes']).isEmpty,
    '自定义菜谱删除未落盘。',
  );

  for (final path in ['/missions/tracked', '/missions/available']) {
    final mission = await api.read(path);
    check(
      mission['runtimeAvailable'] == false &&
          jsonString(mission, 'contentSignature').isNotEmpty,
      '$path 未公开真实不可用状态和签名。',
    );
    final cached = await api.read(
      path,
      query: {'knownSignature': jsonString(mission, 'contentSignature')},
    );
    check(cached['unchanged'] == true, '$path 未复用签名协议。');
  }
  final invitations = await api.read(
    '/rare-guests/invitations',
    query: {'scope': 'current'},
  );
  check(
    invitations['ok'] == false && invitations['runtimeAvailable'] == false,
    '未连接游戏时邀请读取误报可用。',
  );
  await expectApiError(
    () => api.write(
      '/rare-guests/invite',
      query: {
        'guestId': '1',
        'scope': 'current',
        'expectedDaySceneGeneration': '1',
        'expectedMapLabel': '合成地图',
      },
    ),
    'business-rejected',
  );
  await expectApiError(
    () => api.write(
      '/inventory/set',
      query: {'type': 'ingredient', 'id': '1', 'qty': '99'},
    ),
    'business-rejected',
  );
  await expectApiError(
    () => api.write(
      '/inventory/bulk-set',
      query: {'type': 'ingredient', 'ids': '1,2', 'qty': '99'},
    ),
    'business-rejected',
  );
}

JsonMap leaseJson({bool owned = true}) => {
  'ok': true,
  'owned': owned,
  'ttlMs': 15000,
  'authorityRevision': 1,
  'ownerLabel': '测试',
  'error': null,
};
JsonMap snapshotJson() => {
  'snapshotSignature': 'signature-1',
  'automationSessionId': 'session-1',
  'nightBusinessGeneration': 1,
  'nightBusinessLifecyclePhase': 'Active',
  'runtimeLoaded': true,
  'runtimeDataComplete': false,
  'runtimeDataSignature': '',
  'specialBusiness': null,
};
JsonMap authorityJson() {
  final profile = CompanionProfile.defaults().copyWith({
    'automationEnabled': true,
  });
  return {
    'ok': true,
    'protocolVersion': 1,
    'profileSchemaVersion': CompanionProfile.schemaVersion,
    'registryId': 'registry-1',
    'authorityRevision': 1,
    'stateRevision': 1,
    'primaryDeviceId': 'flutter-device-00000001',
    'currentDeviceId': 'flutter-device-00000001',
    'currentDeviceIsPrimary': true,
    'activeProfileRevision': 1,
    'activeProfileHash': 'profile-hash',
    'activeProfile': profile.json,
    'currentDeviceProfileRevision': 1,
    'currentDeviceProfileHash': 'profile-hash',
    'currentDeviceProfile': profile.json,
    'pendingSyncId': null,
    'devices': <Object?>[],
  };
}

JsonMap businessJson(BusinessSourceContext context) => {
  'protocolVersion': 1,
  'isCurrent': true,
  'pending': false,
  'error': null,
  'inputVersion': '1',
  'sourceSnapshotSignature': 'signature-1',
  'sourceContext': context.json,
  'recommendations': {
    'recommendations': <Object?>[],
    'recommendationIssues': <Object?>[],
    'normalOrderDetailPlans': <Object?>[],
    'normalExecutionTargets': <Object?>[],
  },
  'automation': {'runtimeEnabled': true, 'leaseOwned': true},
};
