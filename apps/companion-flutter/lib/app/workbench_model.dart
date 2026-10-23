import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart' show ImageProvider;
import 'package:companion_protocol/companion_protocol.dart'
    show CompanionProfile;
import 'package:companion_client/companion_client.dart'
    show projectLiveServiceOrders;

/// 页面只消费服务端投影；该别名不赋予页面解析租约或自行执行订单的权力。
typedef DataMap = Map<String, Object?>;

/// 安全读取可选的展示对象。缺失数据保持为空，不伪造业务状态或编号。
DataMap object(Object? value) =>
    value is Map ? value.cast<String, Object?>() : const {};
List<DataMap> records(Object? value) => value is List
    ? value
          .whereType<Map<Object?, Object?>>()
          .map((item) => item.cast<String, Object?>())
          .toList(growable: false)
    : const [];
List<Object?> values(Object? value) =>
    value is List ? value.cast<Object?>() : const [];
String label(Object? value, [String empty = '—']) =>
    value == null || value == '' ? empty : value.toString();
String words(Object? value) =>
    values(value)
        .map((item) => item is Map ? label(item['name']) : label(item))
        .join(' · ');

/// 配置来源与权限独立。预览须明确标识，不能把内存合成连接称为真实启动或安全存储恢复。
enum ConnectionSource { unknown, launchArguments, saved, manual, preview }

/// 操作记录只描述用户明确发起的动作结果，不保存请求正文、Token 或服务端完整响应。
/// unknown 表示请求可能已被执行而响应无法确认，界面不得将其改写为“没有修改”。
enum WorkbenchOperationResult { success, failed, unknown }

class WorkbenchOperation {
  const WorkbenchOperation({
    required this.id,
    required this.name,
    required this.startedAt,
    required this.completedAt,
    required this.result,
    required this.summary,
  });
  final int id;
  final String name;
  final DateTime startedAt, completedAt;
  final WorkbenchOperationResult result;
  final String summary;
}

/// 动作名采用固定清单，绝不将 URL 参数、用户输入、资源 ID 或正文拼进历史记录。
String workbenchOperationName(String path) =>
    const {
      '/devices/primary': '设置主设备',
      '/devices/sync': '同步主设备配置',
      '/devices/rename': '修改设备名称',
      '/devices/forget': '移除离线设备',
      '/favorites/add-recipe': '收藏料理',
      '/favorites/add-beverage': '收藏酒水',
      '/favorites/remove-recipe': '取消料理收藏',
      '/favorites/remove-beverage': '取消酒水收藏',
      '/custom-recipes/upsert': '保存自定义配方',
      '/custom-recipes/remove': '删除自定义配方',
      '/custom-recipes/settings': '切换配方模式',
      '/custom-recipes/update-flags': '修改配方启用状态',
      '/custom-recipes/move': '调整配方顺序',
      '/rare-guests/invite': '邀请稀客',
      '/rare-guests/invite-all': '批量邀请稀客',
      '/inventory/set': '修改库存',
      '/inventory/bulk-set': '批量修改库存',
      '/orders/rare/dismiss': '清除失效订单缓存',
      '/updates/check': '检查更新',
      '/updates/download': '下载更新',
      '/updates/install-on-exit': '安排退出后更新',
      '/logs/config': '保存日志设置',
      '/logs/console': '切换游戏控制台',
      '/logs/open-folder': '打开日志目录',
      '/logs/export-diagnostics': '导出诊断包',
      '/logs/packages/delete': '删除诊断包',
      '/local-api/config': '保存局域网连接',
    }[path] ??
    '执行操作';

/// 展示层端口。生产实现连接 CompanionSession；组件测试注入离线事实，避免启动游戏。
/// 所有写入都经此边界做权限、连接代际和并发检查，Widget 不直接使用 HttpClient。
abstract class WorkbenchModel extends ChangeNotifier {
  DataMap get snapshot;
  DataMap get business;
  DataMap get catalog;
  DataMap get authority;
  DataMap get profile;
  DataMap get pageResult;

  /// 推荐读取独立于写操作忙碌状态；页面据此显示计算中与可重试错误。
  bool get recommendationLoading => false;
  String get recommendationError => '';
  DataMap get localPreferences;
  Map<String, DataMap> get resources;

  /// 图像资源是当前连接的只读事实；内存预览不连接宿主，可显式覆盖这些端口。
  /// 产品初始 verifying 与等待/提取共用独立门禁面板；完整验证清单后才挂载业务内容。
  DataMap get gameMediaStatus => const {
    'phase': 'ready',
    'completed': 0,
    'total': 0,
  };
  List<DataMap> get gameMediaItems => const [];
  Map<String, DataMap> get gameMediaRecipes => const {};
  int get gameMediaRevision => 0;
  ImageProvider<Object>? gameMediaImage(String namespace, int id) => null;
  bool get gameMediaReady => gameMediaStatus['phase'] == 'ready';
  Future<void> reloadGameMedia() async {}
  bool get connected;
  bool get busy;

  /// 连接专用等待状态与配置保存的busy分开，页面不能把后台保存误称为重新连接。
  bool get connecting => false;
  ConnectionSource get connectionSource => ConnectionSource.unknown;

  /// 启动参数管理的连接在本次运行期间只读；断线不解除来源限制。
  /// 生产模型还须在串行连接队列执行时校验，不能只依赖页面隐藏编辑器。
  bool get canEditConnection =>
      connectionSource != ConnectionSource.launchArguments;

  /// 仅在用户明确进入可编辑连接表单时读取；值与 endpoint 是同一内存连接的凭据。
  /// 不得把它加入普通偏好、操作历史、诊断或错误日志。启动参数管理的连接返回空值。
  String get editableConnectionToken => '';
  List<WorkbenchOperation> get recentOperations => const [];

  /// 游戏电脑的主设备修改监听设置；token 为 null 表示保留，为非空值表示明确替换。
  /// 生产实现须串行确认后续接当前设备身份，UI 不自行组合旧地址与新凭据。
  Future<void> updateLanConnection({
    required bool enabled,
    required String host,
    String? token,
  }) async {
    throw UnsupportedError('当前模型不支持局域网设置。');
  }

  bool get desktop;
  bool get canExecute;
  String get error;
  String get notice;
  String get endpoint;
  String get clientLabel;
  String get inputAction;
  int get inputSequence;

  /// 当前连接替换代次，只用于使用户确认失效，不能作为服务端身份。
  int get connectionVersion => 0;

  bool get primary => authority['currentDeviceIsPrimary'] == true;
  bool get canEdit => connected && primary && !busy && gameMediaReady;
  bool get dark => localPreferences['darkMode'] == true;
  bool get reduceMotion => localPreferences['reduceMotion'] == true;
  DataMap get runtime => object(snapshot['recommendationState']);
  DataMap get night => object(snapshot['nightBusiness']);
  DataMap get automation => object(business['automation']);
  DataMap get recommendations => object(business['recommendations']);

  /// 页面只读订单投影；生产由会话补充严格 sourceContext 核验，预览用同一纯值算法。
  DataMap get serviceOrders => !connected
      ? const {}
      : projectLiveServiceOrders(
          snapshot,
          recommendations,
          contextMatches: true,
          current: business['isCurrent'] == true,
        );
  List<DataMap> catalogItems(String key) => records(catalog[key]);
  String itemName(String key, Object? id) {
    for (final item in catalogItems(key)) {
      if (item['id'] == id) return label(item['name']);
    }
    return id == null ? '—' : '编号 $id';
  }

  Future<void> connect(String endpoint, String token, String deviceLabel);

  /// 使用当前来源及凭据重试，不接受页面提供的新地址、Token 或设备名称。
  /// 默认实现供内存模型使用；生产实现同时重试尚未完成的原生初始化。
  Future<void> retryConnection() => refresh();
  Future<void> refresh();
  Future<void> readResource(
    String path, {
    Map<String, String> query = const {},
  });

  /// 诊断页的两个只读资源属于同一个订阅组；页面只调用此入口，不自行启动
  /// 两个可能互相撤销的加载。生产实现仍保留离页、重连与迟到响应校验。
  Future<void> refreshDiagnostics() async {
    await readResource('/logs/settings');
    await readResource('/logs/packages');
  }

  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  });
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  });
  Future<void> updateProfile(DataMap changes);

  /// 默认实现供内存预览与测试使用；生产实现应在保存队列执行时读取最新配置，
  /// 再按目标 key 合并叶字段，避免连续调整不同评分项时相互覆盖。
  Future<void> updateSortObjective(String key, DataMap changes) {
    final sort = object(profile['recommendationSortProfile']);
    if (sort['preset'] != 'custom') {
      throw StateError('请先切换为自定义评分模式。');
    }
    return updateProfile({
      'recommendationSortProfile': {
        ...sort,
        'objectives': [
          for (final objective in records(sort['objectives']))
            objective['key'] == key ? {...objective, ...changes} : objective,
        ],
      },
    });
  }

  /// 模式本身随共享配置保存。自定义保留当前全部参数；固定预设则完整替换，
  /// 内存夹具和预览也沿用相同语义，生产实现进一步在串行保存队列内读取最新值。
  Future<void> updateSortMode(String mode) => updateProfile({
    'recommendationSortProfile': mode == 'custom'
        ? {...object(profile['recommendationSortProfile']), 'preset': 'custom'}
        : CompanionProfile.sortPreset(mode),
  });

  Future<void> updateLocal(DataMap changes);
  Future<void> acknowledgeBarrier(
    int sequence, {
    required String expectedSessionId,
    required int expectedGeneration,
  });
  Future<void> retryOrder(String kind, String key);
  Future<void> platformCommand(String command);
  Future<void> importHistory(String path);

  /// 路由切换只改变读取订阅，不能隐式改变共享配置或自动化许可。
  void activatePage(int index) {}

  /// 子页面离开时撤销附加资源订阅；正在进行的读取只允许完成，不允许覆盖新页面。
  void clearResourceSubscription() {}

  /// 离开推荐页或条件失效时取消查询轮询；不影响经营业务或自动化。
  void clearRecommendationQuery() {}
}
