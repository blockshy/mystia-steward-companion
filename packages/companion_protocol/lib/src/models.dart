import 'json.dart';
import 'profile.dart';

const businessProtocolVersion = 1;
const deviceProtocolVersion = 1;
const interactionProtocolVersion = 2;

/// 设备权威使用服务端修订进行 CAS；本地缓存只为展示和生成预期修订。
class DeviceAuthorityState {
  DeviceAuthorityState.fromJson(JsonMap value) : json = jsonMap(value) {
    requireOperationSuccess(json);
    if (jsonInt(json, 'protocolVersion') != deviceProtocolVersion ||
        jsonInt(json, 'profileSchemaVersion') !=
            CompanionProfile.schemaVersion) {
      throw const ApiException('incompatible', '设备配置协议不兼容，请安装成套客户端和 Mod。');
    }
    activeProfile = CompanionProfile(jsonMap(json['activeProfile']));
    currentDeviceProfile = CompanionProfile(
      jsonMap(json['currentDeviceProfile']),
    );
    if (registryId.isEmpty ||
        authorityRevision <= 0 ||
        currentDeviceId.isEmpty ||
        activeProfileHash.isEmpty) {
      throw const FormatException('设备权威缺少完整身份。');
    }
  }
  final JsonMap json;
  late final CompanionProfile activeProfile;
  late final CompanionProfile currentDeviceProfile;
  String get registryId => jsonString(json, 'registryId');
  int get authorityRevision => jsonInt(json, 'authorityRevision');
  int get stateRevision => jsonInt(json, 'stateRevision');
  String get primaryDeviceId => jsonString(json, 'primaryDeviceId');
  String get currentDeviceId => jsonString(json, 'currentDeviceId');
  bool get currentDeviceIsPrimary => jsonBool(json, 'currentDeviceIsPrimary');
  int get activeProfileRevision => jsonInt(json, 'activeProfileRevision');
  String get activeProfileHash => jsonString(json, 'activeProfileHash');
  int get currentDeviceProfileRevision =>
      jsonInt(json, 'currentDeviceProfileRevision');
  String get currentDeviceProfileHash =>
      jsonString(json, 'currentDeviceProfileHash');
  String? get pendingSyncId =>
      json['pendingSyncId'] == null ? null : jsonString(json, 'pendingSyncId');
  List<JsonMap> get devices => jsonObjects(json['devices']);
}

class AutomationLease {
  AutomationLease.fromJson(JsonMap value) : json = jsonMap(value) {
    jsonBool(json, 'ok');
    jsonBool(json, 'owned');
    if (ttlMs <= 0 || ttlMs > 60000) throw const FormatException('租约 TTL 无效。');
  }
  final JsonMap json;
  bool get ok => jsonBool(json, 'ok');
  bool get owned => ok && jsonBool(json, 'owned');
  int get ttlMs => jsonInt(json, 'ttlMs');
  int get authorityRevision => jsonInt(json, 'authorityRevision', fallback: 0);
  String get error => jsonString(json, 'error', fallback: '');
  String get ownerLabel => jsonString(json, 'ownerLabel', fallback: '');
}

/// 完整游戏快照只读保存领域载荷；动作会话必须显式读取，不能从列表位置推断。
class GameSnapshot {
  GameSnapshot.fromJson(JsonMap value) : json = jsonMap(value) {
    if (json['unchanged'] == true)
      throw const FormatException('unchanged 必须合并已有快照后再构造。');
  }
  final JsonMap json;
  String get signature => jsonString(json, 'snapshotSignature', fallback: '');
  String get automationSessionId =>
      jsonString(json, 'automationSessionId', fallback: '');
  int get nightBusinessGeneration =>
      jsonInt(json, 'nightBusinessGeneration', fallback: 0);
  bool get nightActive =>
      json['nightBusinessLifecyclePhase'] == 'Active' &&
      nightBusinessGeneration > 0;
  String get runtimeDataSignature =>
      jsonString(json, 'runtimeDataSignature', fallback: '');
  bool get runtimeDataComplete =>
      jsonBool(json, 'runtimeDataComplete', fallback: false);
}

/// 展示边界不含倒计时或库存变化；只读旧结果可保留，但不能因此恢复执行权。
class BusinessSourceContext {
  BusinessSourceContext.fromJson(JsonMap value) : json = jsonMap(value) {
    jsonMap(json['snapshot']);
    final authority = jsonMap(json['authority']);
    if (jsonString(authority, 'registryId').isEmpty ||
        jsonString(authority, 'activeProfileHash').isEmpty) {
      throw const FormatException('展示上下文缺少权威身份。');
    }
  }
  final JsonMap json;
  String get key => canonicalJson(json);
  static BusinessSourceContext fromSnapshot(
    GameSnapshot snapshot,
    DeviceAuthorityState authority,
  ) {
    const fields = [
      'automationSessionId',
      'nightBusinessGeneration',
      'nightBusinessLifecyclePhase',
      'activeSceneName',
      'runtimeLoaded',
      'runtimeDaySceneGeneration',
      'runtimeDaySceneReady',
      'runtimeDataSignature',
    ];
    const specialFields = [
      'active',
      'challengeTypeAvailable',
      'challengeType',
      'phase',
      'foodTargetTags',
      'beverageTargetTags',
      'requiredExtraIngredientIds',
      'yuumaFoodTargetRevision',
      'wackyKoishiShieldBroken',
      'wackyKoishiFoodPreferenceTags',
      'wackyKoishiFoodHateTags',
      'wackyKoishiBeveragePreferenceTags',
    ];
    final rawSpecial = snapshot.json['specialBusiness'];
    final special = rawSpecial == null ? null : jsonMap(rawSpecial);
    return BusinessSourceContext.fromJson({
      'snapshot': {
        for (final field in fields) field: snapshot.json[field],
        'specialBusiness': special == null
            ? null
            : {
                for (final field in specialFields) field: special[field],
                for (final field in ['currentValue', 'maxValue', 'targetValue'])
                  field: special['wackyKoishiShieldBroken'] == true
                      ? special[field]
                      : null,
                'error': special['challengeTypeAvailable'] != true
                    ? special['error']
                    : null,
              },
      },
      'authority': {
        'registryId': authority.registryId,
        'authorityRevision': authority.authorityRevision,
        'activeProfileRevision': authority.activeProfileRevision,
        'activeProfileHash': authority.activeProfileHash,
      },
    });
  }
}

class BusinessStatus {
  BusinessStatus.fromJson(JsonMap value) : json = jsonMap(value) {
    if (jsonInt(json, 'protocolVersion') != businessProtocolVersion) {
      throw const ApiException('incompatible', '业务协议不兼容，请安装成套客户端和 Mod。');
    }
    jsonBool(json, 'isCurrent');
    jsonBool(json, 'pending');
    if (json['inputVersion'] != null) jsonString(json, 'inputVersion');
  }
  final JsonMap json;
  bool get isCurrent => jsonBool(json, 'isCurrent');
  bool get pending => jsonBool(json, 'pending');
  String? get error => json['error'] == null ? null : jsonString(json, 'error');
  String get sourceSnapshotSignature =>
      jsonString(json, 'sourceSnapshotSignature', fallback: '');
  BusinessSourceContext? get sourceContext => json['sourceContext'] == null
      ? null
      : BusinessSourceContext.fromJson(jsonMap(json['sourceContext']));
  JsonMap? get recommendations =>
      json['recommendations'] == null ? null : jsonMap(json['recommendations']);
  JsonMap? get automation =>
      json['automation'] == null ? null : jsonMap(json['automation']);
  bool currentFor(GameSnapshot? snapshot, BusinessSourceContext? context) =>
      error == null &&
      isCurrent &&
      snapshot != null &&
      snapshot.signature.isNotEmpty &&
      sourceSnapshotSignature == snapshot.signature &&
      context != null &&
      sourceContext?.key == context.key;
}

/// 每一设备每种 kind 只有一个活动查询；构造器不允许上传库存、预算或客户端推荐。
class PageQuery {
  PageQuery.normal(String place)
    : json = jsonMap({
        'protocolVersion': businessProtocolVersion,
        'kind': 'normal',
        'selectedPlace': place,
      }) {
    if (place.length > 100) throw const FormatException('地点过长。');
  }
  PageQuery.rare(int customerId, {String foodTag = '', String beverageTag = ''})
    : json = jsonMap({
        'protocolVersion': businessProtocolVersion,
        'kind': 'rare',
        'customerId': customerId,
        'foodTag': foodTag,
        'beverageTag': beverageTag,
      }) {
    if (customerId < 0 || foodTag.length > 100 || beverageTag.length > 100)
      throw const FormatException('稀客查询无效。');
  }
  final JsonMap json;
  String get kind => jsonString(json, 'kind');
  String get key => canonicalJson(json);
}

class PageResult {
  PageResult.fromJson(JsonMap value) : json = jsonMap(value) {
    if (jsonInt(json, 'protocolVersion') != businessProtocolVersion)
      throw const ApiException('incompatible', '页面推荐协议不兼容。');
    jsonBool(json, 'isCurrent');
    jsonBool(json, 'pending');
  }
  final JsonMap json;
  JsonMap? get result =>
      json['result'] == null ? null : jsonMap(json['result']);
  bool get isCurrent => jsonBool(json, 'isCurrent');
  bool get pending => jsonBool(json, 'pending');
  String? get error => json['error'] == null ? null : jsonString(json, 'error');
  BusinessSourceContext? get sourceContext => json['sourceContext'] == null
      ? null
      : BusinessSourceContext.fromJson(jsonMap(json['sourceContext']));
  String get sourceSnapshotSignature =>
      jsonString(json, 'sourceSnapshotSignature', fallback: '');
}
