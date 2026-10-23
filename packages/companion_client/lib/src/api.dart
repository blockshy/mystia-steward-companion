import 'dart:typed_data';

import 'package:companion_protocol/companion_protocol.dart';

import 'transport.dart';

/// 类型化核心 API。扩展页面通过 read/write 调用当前端点，所有授权由 Mod 再次判定。
class CompanionApi {
  CompanionApi({
    required ApiConnection connection,
    CompanionTransport? transport,
  }) : _connection = connection,
       transport = transport ?? DartIoTransport();
  ApiConnection _connection;
  ApiConnection get connection => _connection;
  final CompanionTransport transport;
  Future<void> _renameTail = Future<void>.value();

  /// 图片身份由当前资源清单给出；请求头携带凭据，URL 永不包含 Token。
  Future<Uint8List> gameImage({
    required String generation,
    required String key,
  }) {
    final Object binary = transport;
    if (binary is! CompanionImageTransport) {
      throw const ApiException('image-unavailable', '当前传输不支持游戏图片。');
    }
    return binary.image(connection, generation: generation, key: key);
  }

  Future<JsonMap> read(
    String path, {
    Map<String, String> query = const {},
    int? authorityRevision,
    Duration timeout = const Duration(seconds: 5),
  }) => transport.request(
    connection,
    'GET',
    path,
    query: query,
    authorityRevision: authorityRevision,
    timeout: timeout,
  );
  Future<JsonMap> write(
    String path, {
    Map<String, String> query = const {},
    JsonMap? body,
    int? authorityRevision,
    Duration timeout = const Duration(seconds: 5),
  }) async {
    final result = await transport.request(
      connection,
      'POST',
      path,
      query: query,
      body: body,
      authorityRevision: authorityRevision,
      timeout: timeout,
    );
    requireOperationSuccess(result);
    return result;
  }

  /// 更新状态虽然沿用 POST，但只读取完整状态资源。error 可以是上次下载/检查的持久化错误，
  /// 不能据此丢弃 staged、installMessage 和 availableReleases 等事实；UI 缓存后单独展示错误。
  /// 真正检查、下载和安装仍通过 write，必须接受统一操作成功校验。
  Future<JsonMap> updateStatus() =>
      transport.request(connection, 'POST', '/updates/status');

  Future<DeviceAuthorityState> register({
    required String platform,
    required String appVersion,
    required CompanionProfile profile,
  }) async => DeviceAuthorityState.fromJson(
    await write(
      '/devices/register',
      body: {
        'protocolVersion': deviceProtocolVersion,
        'profileSchemaVersion': CompanionProfile.schemaVersion,
        'platform': platform,
        'appVersion': appVersion,
        'profile': profile.json,
      },
    ),
  );
  Future<DeviceAuthorityState> devices() async =>
      DeviceAuthorityState.fromJson(await read('/devices'));
  Future<DeviceAuthorityState> updateProfile(
    DeviceAuthorityState expected,
    CompanionProfile profile,
  ) async => DeviceAuthorityState.fromJson(
    await write(
      '/devices/profile',
      body: {
        'protocolVersion': deviceProtocolVersion,
        'profileSchemaVersion': CompanionProfile.schemaVersion,
        'expectedAuthorityRevision': expected.authorityRevision,
        'expectedProfileRevision': expected.currentDeviceProfileRevision,
        'profile': profile.json,
      },
    ),
  );
  Future<DeviceAuthorityState> syncFromPrimary(
    DeviceAuthorityState expected,
    String deviceId,
  ) async => DeviceAuthorityState.fromJson(
    await write(
      '/devices/sync',
      body: {
        'protocolVersion': deviceProtocolVersion,
        'expectedAuthorityRevision': expected.authorityRevision,
        'deviceId': deviceId,
      },
    ),
  );
  Future<DeviceAuthorityState> acknowledgeSync(
    DeviceAuthorityState expected,
  ) async => DeviceAuthorityState.fromJson(
    await write(
      '/devices/sync-ack',
      body: {
        'protocolVersion': deviceProtocolVersion,
        'syncId': expected.pendingSyncId,
        'profileRevision': expected.currentDeviceProfileRevision,
        'profileHash': expected.currentDeviceProfileHash,
      },
    ),
  );
  Future<DeviceAuthorityState> setPrimary(
    DeviceAuthorityState expected,
    String deviceId,
  ) async => DeviceAuthorityState.fromJson(
    await write(
      '/devices/primary',
      body: {
        'protocolVersion': deviceProtocolVersion,
        'expectedAuthorityRevision': expected.authorityRevision,
        'deviceId': deviceId,
      },
    ),
  );

  /// 只串行化改名，不阻塞普通读取。每次请求仍取得独立不可变的连接对象，因此改名
  /// 成功后旧请求继续使用原名称，后续租约/读取才携带新名称；凭据和设备 ID 始终不变。
  /// 串行队列消除两个改名响应逆序返回的覆盖窗口，失败不修改元数据，也不重放请求。
  Future<DeviceAuthorityState> rename(String label) {
    final next = _renameTail.then((_) async {
      final before = _connection;
      final requested = label.trim();
      final state = DeviceAuthorityState.fromJson(
        await write(
          '/devices/rename',
          body: {'protocolVersion': deviceProtocolVersion, 'label': requested},
        ),
      );
      final current = state.devices
          .where((device) => device['deviceId'] == before.clientId)
          .toList();
      if (state.currentDeviceId != before.clientId ||
          current.length != 1 ||
          current.single['label'] != requested) {
        throw const ApiException('rename-unconfirmed', '设备名称尚未确认，请刷新设备列表。');
      }
      // 不原地修改 ApiConnection：在途请求可能还在等待网络，不能使其身份头隐式变化。
      _connection = ApiConnection(
        endpoint: before.endpoint.toString(),
        token: before.token,
        clientId: before.clientId,
        clientLabel: requested,
      );
      return state;
    });
    _renameTail = next.then<void>((_) {}).catchError((Object _) {});
    return next;
  }

  Future<DeviceAuthorityState> forget(
    DeviceAuthorityState expected,
    String deviceId,
  ) async => DeviceAuthorityState.fromJson(
    await write(
      '/devices/forget',
      body: {
        'protocolVersion': deviceProtocolVersion,
        'expectedAuthorityRevision': expected.authorityRevision,
        'deviceId': deviceId,
      },
    ),
  );
  Future<BusinessStatus> businessStatus() async => BusinessStatus.fromJson(
    await read(
      '/business/status',
      query: {'protocolVersion': businessProtocolVersion.toString()},
    ),
  );

  /// 先打开无意图的短期会话。响应迟到时客户端可直接丢弃，不会留下后台计算任务。
  Future<String> openPageSession() async {
    final response = await write(
      '/business/page-session/open',
      body: {'protocolVersion': businessProtocolVersion},
    );
    final id = jsonString(response, 'sessionId');
    if (jsonInt(response, 'protocolVersion') != businessProtocolVersion ||
        !RegExp(r'^[0-9a-f]{32}$').hasMatch(id)) {
      throw const FormatException('页面查询会话响应无效。');
    }
    return id;
  }

  /// 更新与撤销使用同一递增序列；意图为空时只撤销本会话，不影响其它设备。
  Future<PageResult?> updatePageSession(
    String sessionId,
    int sequence,
    PageQuery? intent,
  ) async {
    final response = await transport.request(
      connection,
      'POST',
      '/business/page-session/update',
      body: {
        'protocolVersion': businessProtocolVersion,
        'sessionId': sessionId,
        'sequence': sequence,
        'intent': intent?.json,
      },
      query: const {},
      timeout: const Duration(seconds: 5),
    );
    if (intent != null) return PageResult.fromJson(response);
    requireOperationSuccess(response);
    if (response['cancelled'] != true)
      throw const FormatException('页面查询撤销未确认。');
    return null;
  }

  Future<AutomationLease> lease(int revision) async => AutomationLease.fromJson(
    await read('/automation/lease', authorityRevision: revision),
  );
  Future<AutomationLease> acquireLease(int revision) async =>
      AutomationLease.fromJson(
        await transport.request(
          connection,
          'POST',
          '/automation/lease/acquire',
          authorityRevision: revision,
          query: const {},
          timeout: const Duration(seconds: 5),
        ),
      );
  Future<AutomationLease> releaseLease(int revision) async =>
      AutomationLease.fromJson(
        await transport.request(
          connection,
          'POST',
          '/automation/lease/release',
          authorityRevision: revision,
          query: const {},
          timeout: const Duration(seconds: 5),
        ),
      );

  /// 人工操作采用新版本 JSON 信封，显式绑定游戏进程会话与经营代次，禁止旧序号跨进程复用。
  Future<JsonMap> acknowledge(
    int sequence,
    DeviceAuthorityState authority,
    GameSnapshot snapshot,
  ) => write(
    '/automation/barriers/ack',
    authorityRevision: authority.authorityRevision,
    body: {
      'protocolVersion': interactionProtocolVersion,
      'automationSessionId': snapshot.automationSessionId,
      'businessGeneration': snapshot.nightBusinessGeneration,
      'sequence': sequence,
    },
  );
  Future<JsonMap> retry(
    String kind,
    String key,
    DeviceAuthorityState authority,
    GameSnapshot snapshot,
  ) => write(
    '/business/automation/retry',
    authorityRevision: authority.authorityRevision,
    body: {
      'protocolVersion': interactionProtocolVersion,
      'automationSessionId': snapshot.automationSessionId,
      'businessGeneration': snapshot.nightBusinessGeneration,
      'kind': kind,
      'key': key,
    },
  );

  /// 重置只发送订单身份，客户端不能提供新配方或伪造已送达状态。
  Future<JsonMap> reset(
    String kind,
    String key,
    DeviceAuthorityState authority,
    GameSnapshot snapshot,
  ) => write(
    '/business/automation/reset',
    authorityRevision: authority.authorityRevision,
    body: {
      'protocolVersion': interactionProtocolVersion,
      'automationSessionId': snapshot.automationSessionId,
      'businessGeneration': snapshot.nightBusinessGeneration,
      'kind': kind,
      'key': key,
    },
  );
  void close() => transport.close();
}
