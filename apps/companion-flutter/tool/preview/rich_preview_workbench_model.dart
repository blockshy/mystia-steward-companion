import 'dart:convert';

import 'package:companion_protocol/companion_protocol.dart';
import 'package:flutter/painting.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';

import 'synthetic_game_media.dart';

/// 独立预览入口的纯内存事实投影。此文件不导入网络、文件、原生平台或生产会话。
/// 所有名称、数量、计划与结果仅用于交互验收，不计算推荐算法，不代表真实游戏结果。
/// 生产 main.dart 不导入本模型；断线也绝不能自动回退到此模型。
class RichPreviewWorkbenchModel extends WorkbenchModel {
  RichPreviewWorkbenchModel({this.onExit, this.desktop = true}) {
    reset();
  }

  /// 由独立入口注入的唯一宿主动作；测试可以注入计数器，模型自身不接触原生 API。
  final Future<void> Function()? onExit;

  static const scenarios = {
    'night': '夜间经营',
    'day': '日间工具',
    'viewer': '查看设备',
    'offline': '连接中断',
    'barrier': '人工屏障',
    'loading': '刷新中',
    'orders-pending': '新单待方案',
    'orders-ready': '方案已到达',
    'media-verifying': '缓存核验中',
    'media-loading': '资源初始化中',
    'media-failed': '资源初始化失败',
  };
  String scenario = 'night';
  int _generation = 0;
  int _identity = 200;
  String _previewToken = 'preview-only-token';
  final List<WorkbenchOperation> _operations = [];
  int _operationSequence = 0;
  int _mediaGeneration = 0;
  DataMap? _pendingServicePlans;
  final _mediaImages = createSyntheticGameImages();

  /// 合成配方的身份表与下方 reset 中的编号构造对应；不从中文食材或厨具名反查。
  /// 第八道配方故意含两份豆腐，验证运行时索引保留顺序及重复项。
  final Map<String, DataMap> _mediaRecipes = Map.unmodifiable({
    for (var index = 0; index < 8; index++)
      '${101 + index}:${1001 + index}': Map<String, Object?>.unmodifiable({
        'foodId': 101 + index,
        'recipeId': 1001 + index,
        'ingredientIds': List<int>.unmodifiable([
          index.isEven ? 1 : 2,
          if (index == 7) 2,
          3,
        ]),
        'cookerType': const [1, 4, 2][index % 3],
      }),
  });

  @override
  int get gameMediaRevision => _mediaGeneration;
  @override
  DataMap get gameMediaStatus {
    final phase = !connected
        ? 'waiting'
        : scenario == 'media-verifying'
        ? 'verifying'
        : scenario == 'media-loading'
        ? 'extracting'
        : scenario == 'media-failed'
        ? 'failed'
        : 'ready';
    return {
      'phase': phase,
      'generation': phase == 'ready'
          ? 'synthetic-media-$_mediaGeneration'
          : null,
      'completed': phase == 'ready'
          ? _mediaImages.length
          : phase == 'extracting'
          ? 12
          : 0,
      'total': _mediaImages.length,
      'message': phase == 'extracting'
          ? '合成资源初始化中，可用预览条结束本场景。'
          : phase == 'failed'
          ? '合成资源初始化失败'
          : '纯合成图案，仅用于离线界面验收。',
      if (phase == 'failed') 'error': '合成错误：资源读取失败。重新读取只会恢复预览内存，不访问游戏。',
      'loadedImages': phase == 'ready' ? _mediaImages.length : 0,
      'failedImages': 0,
    };
  }

  @override
  Map<String, DataMap> get gameMediaRecipes =>
      gameMediaReady ? _mediaRecipes : const {};
  @override
  ImageProvider<Object>? gameMediaImage(String namespace, int id) =>
      gameMediaReady && id >= 0 ? _mediaImages['$namespace:$id'] : null;

  @override
  List<DataMap> get gameMediaItems {
    if (!gameMediaReady) return const [];
    const categories = {
      'food': 'recipes',
      'beverage': 'beverages',
      'ingredient': 'ingredients',
      'rare-guest': 'rareCustomers',
      'normal-group': 'normalCustomers',
    };
    const cookers = ['煮锅', '烧烤架', '油炸锅', '蒸锅', '料理台'];
    return List.unmodifiable([
      for (final entry in _mediaImages.entries)
        (() {
          final parts = entry.key.split(':');
          final kind = parts[0], id = int.parse(parts[1]);
          final matches = kind == 'cooker'
              ? <DataMap>[]
              : catalogItems(categories[kind]!)
                    .where((item) => item['id'] == id)
                    .toList();
          return Map<String, Object?>.unmodifiable({
            'key': entry.key,
            'kind': kind,
            'id': id,
            'name':
                '合成图案 · ${kind == 'cooker'
                    ? cookers[id - 1]
                    : matches.length == 1
                    ? matches.single['name']
                    : '#$id'}',
            'width': 64,
            'height': 64,
            'byteLength': entry.value.bytes.length,
          });
        })(),
    ]);
  }

  /// 预览重读立即发布一个新的纯内存代次；不启动提取器、不读目录、不调用宿主。
  /// 等待/失败场景也有预览条出口，不能为了验收门禁而让离线窗口永远无法恢复。
  @override
  Future<void> reloadGameMedia() async {
    if (!connected || !primary) {
      _publish('预览资源重读仅允许已连接的合成主设备');
      return;
    }
    if (scenario == 'media-loading' ||
        scenario == 'media-failed' ||
        scenario == 'media-verifying')
      scenario = 'night';
    _mediaGeneration++;
    _publish('合成资源已重新读取');
  }

  // 合成记录固定使用合法时间字符串；不读取系统时钟，方便重置和截图逐字复核。
  static const _fixtureTime = '2026-10-10T00:00:00Z';
  @override
  int connectionVersion = 0;
  @override
  DataMap snapshot = {},
      business = {},
      catalog = {},
      authority = {},
      profile = {},
      pageResult = {},
      localPreferences = {};
  @override
  Map<String, DataMap> resources = {};
  @override
  bool connected = true, busy = false;
  // “刷新中”场景也覆盖推荐的后台等待提示，纯内存即可复核加载动画与文案。
  @override
  bool get recommendationLoading => connected && scenario == 'loading';
  @override
  /// 由独立入口显式选择平台布局；不读取宿主、文件或生产平台初始化结果。
  /// Windows 预览沿用默认值，Android 预览以 false 复用真实的触屏主题与导航。
  final bool desktop;
  @override
  ConnectionSource get connectionSource => ConnectionSource.preview;
  @override
  String get editableConnectionToken => _previewToken;
  @override
  List<WorkbenchOperation> get recentOperations =>
      List.unmodifiable(_operations);
  @override
  bool get canExecute =>
      connected &&
      gameMediaReady &&
      primary &&
      profile['automationEnabled'] == true &&
      (scenario == 'night' || scenario == 'barrier');
  @override
  String error = '',
      notice = '',
      endpoint = 'http://127.0.0.1:32145',
      clientLabel = '离线预览电脑',
      inputAction = '';
  @override
  int inputSequence = 0;

  /// 独立预览把 F1–F4 转为四个导航事件，供没有实体手柄时检查原生窗口中的
  /// 按键提示与布局。只发内存通知，不模拟平台接入，也不证明 XInput 硬件通过。
  void previewGamepadNavigation(String action) {
    const allowed = {'previousTab', 'nextTab', 'previousSubTab', 'nextSubTab'};
    if (!allowed.contains(action)) throw ArgumentError.value(action, 'action');
    inputAction = action;
    inputSequence++;
    _publish();
  }

  List<DataMap> _invitationCandidates = [];

  DataMap _copy(DataMap value) => object(jsonDecode(jsonEncode(value)));
  void _publish([String message = '']) {
    if (message.isNotEmpty) notice = '$message（仅预览内存）';
    notifyListeners();
  }

  /// 固定基准时间加本次内存序号，保证预览可复核且不接触真实日志；记录只含
  /// 固定动作名，不包含输入 Token、目录路径或请求正文，重置时一并清空。
  void _recordOperation(String name) {
    final time = DateTime.parse(_fixtureTime)
        .add(Duration(seconds: ++_operationSequence));
    _operations.insert(
      0,
      WorkbenchOperation(
        id: _operationSequence,
        name: name,
        startedAt: time,
        completedAt: time,
        result: error.isEmpty
            ? WorkbenchOperationResult.success
            : WorkbenchOperationResult.failed,
        summary: error.isEmpty ? '已完成（仅预览内存）。' : '未完成（仅预览内存）。',
      ),
    );
    if (_operations.length > 20) _operations.removeLast();
  }

  /// 重置只替换内存；不创建目录、不保存凭据，也不调用真实设备注册。
  void reset() {
    _operations.clear();
    _operationSequence = 0;
    _previewToken = 'preview-only-token';
    localPreferences = {
      'reduceMotion': false,
      'fontScalePercent': 100,
      'darkMode': false,
      'backgroundOpacityPercent': 100,
    };
    profile = _copy(CompanionProfile.defaults().json)
      ..['automationEnabled'] = true;
    catalog = {
      'isComplete': true,
      'ingredients': [
        for (final (i, name) in [
          '蘑菇',
          '豆腐',
          '海苔',
          '鳟鱼',
          '猪肉',
          '蜂蜜',
          '南瓜',
          '土豆',
          '洋葱',
          '莲子',
          '白果',
          '银杏',
        ].indexed)
          {
            'id': i + 1,
            'name': name,
            'price': 4 + i,
            'tags': [i.isEven ? '鲜' : '清淡', if (i % 3 == 0) '素'],
          },
      ],
      'recipes': [
        for (final (i, name) in [
          '海鲜味噌汤',
          '豆腐锅',
          '豚骨拉面',
          '秘制烤鱼',
          '蔬菜沙拉',
          '南瓜煎饼',
          '蜜汁叉烧',
          '温暖饭团',
        ].indexed)
          {
            'id': i + 101,
            'recipeId': i + 1001,
            'name': name,
            'price': 28 + i * 7,
            'level': 1 + i % 3,
            'description': '合成图鉴资料：$name 的基础配方，供离线界面查看。',
            'cooker': ['煮锅', '蒸锅', '烧烤架'][i % 3],
            // 基础材料是数量敏感序列，可以合法重复；末条用于验证不能按名称去重。
            'ingredients': [i.isEven ? '蘑菇' : '豆腐', if (i == 7) '豆腐', '海苔'],
            'ingredientIds': [i.isEven ? 1 : 2, if (i == 7) 2, 3],
            // 料理目录使用 positiveTags；tags 是食材和酒水目录字段，不能混用。
            'positiveTags': ['家常', i.isEven ? '鲜' : '清淡'],
            'negativeTags': <String>[],
          },
      ],
      'beverages': [
        for (final (i, name) in [
          '冰山毛玉冻柠',
          '绿茶',
          '果味气泡水',
          '梅子酒',
          '月光饮',
          '红魔馆红茶',
        ].indexed)
          {
            'id': i + 201,
            'name': name,
            'price': 10 + i * 6,
            'level': 1 + i % 3,
            'description': '合成图鉴资料：$name，收录状态与当前库存分别展示。',
            'tags': [i < 3 ? '无酒精' : '低酒精', '清爽'],
          },
      ],
      'rareCustomers': [
        for (final (i, name) in [
          '博丽灵梦',
          '雾雨魔理沙',
          '露米娅',
          '琪露诺',
          '橙',
          '上白泽慧音',
        ].indexed)
          {
            'id': i + 301,
            'name': name,
            // 合成客人提供各自不同的合法选项，供选择器与换客行为验收使用。
            // 名称/偏好只是夹具，不宣称与游戏人物的实际目录一致。
            'positiveTags': [if (i.isEven) '家常', i.isEven ? '鲜' : '清淡', '素'],
            'beverageTags': [i.isEven ? '无酒精' : '低酒精', '清爽'],
            'places': [i.isEven ? '博丽神社' : '妖怪兽道'],
          },
      ],
      'normalCustomers': [
        {
          'id': 401,
          'name': '妖精',
          'places': ['妖怪兽道', '博丽神社'],
        },
        {
          'id': 402,
          'name': '人类村民',
          'places': ['人间之里', '博丽神社'],
        },
        {
          'id': 403,
          'name': '妖怪客人',
          'places': ['妖怪兽道', '红魔馆'],
        },
      ],
    };
    _invitationCandidates = [
      for (final (i, guest) in catalogItems('rareCustomers').indexed)
        {
          ...guest,
          'kizunaLevel': i == 5 ? -1 : i,
          // 模拟生产精确当前地图登记读取：查无不代表客人在其它地图，失败也
          // 不使用静态出没地点填充实时位置。全部为合成结果，不访问游戏字典。
          'sceneNames': i.isEven ? ['博丽神社'] : <String>[],
          'sceneLabels': i.isEven ? ['synthetic-shrine'] : <String>[],
          'locationStatus': i.isEven
              ? 'current-map'
              : i == 5
              ? 'unavailable'
              : 'not-in-current-map',
          // 邀请状态使用生产 DTO 的枚举；仅作合成展示，不推导真实角色资格。
          'isCurrentScene': i.isEven,
          'status': i == 5
              ? 'kizuna-uninitialized'
              : i == 4
              ? 'invited'
              : i < 2
              ? 'low-kizuna'
              : 'available',
          'canInvite': i >= 2 && i < 4,
          'reason': i == 5
              ? '羁绊信息未知'
              : i == 4
              ? '今天已邀请'
              : i < 2
              ? '羁绊等级不足 $i'
              : '可以邀请',
        },
    ];
    resources = {
      '/favorites': {
        'version': 1,
        'recipes': [
          {
            'id': 'favorite-start',
            // 收藏沿用既有字段名 recipeId，但这里指目录食物 id=101，绝非配方编号1001。
            'recipeId': 101,
            'customerId': 301,
            'customerName': '博丽灵梦',
            'foodTag': '家常',
            'extraIngredientIds': [1],
            'createdAtUtc': _fixtureTime,
            'updatedAtUtc': _fixtureTime,
          },
          {
            'id': 'favorite-catalog-missing',
            'recipeId': 9901,
            'customerId': 302,
            'customerName': '雾雨魔理沙',
            'foodTag': '清淡',
            'extraIngredientIds': [9902],
            'createdAtUtc': _fixtureTime,
            'updatedAtUtc': _fixtureTime,
          },
        ],
        // 收藏接口返回持久条目，不携带 recipeName、allTags 或满足点单的计算结果。
        // 第二条有意缺目录，验证公共组件仍保留未知项 ID，而不是将其隐藏。
        'beverages': [
          {
            'id': 'favorite-beverage-start',
            'beverageId': 201,
            'customerId': 301,
            'customerName': '博丽灵梦',
            'beverageTag': '无酒精',
            'createdAtUtc': _fixtureTime,
            'updatedAtUtc': _fixtureTime,
          },
          {
            'id': 'favorite-beverage-missing',
            'beverageId': 9903,
            'customerId': 302,
            'customerName': '雾雨魔理沙',
            'beverageTag': '清爽',
            'createdAtUtc': _fixtureTime,
            'updatedAtUtc': _fixtureTime,
          },
        ],
      },
      '/custom-recipes': {
        'version': 2,
        'activeMode': 'customer',
        'modeRevision': 0,
        'enabled': true,
        'recipes': [
          {
            'id': 'custom-start',
            'mode': 'customer',
            'customerId': 302,
            'customerName': '雾雨魔理沙',
            'foodId': 102,
            'recipeId': 1002,
            'recipeName': '豆腐锅',
            'foodTag': '清淡',
            'extraIngredientIds': [1, 3],
            'enabled': true,
            'pinToTop': false,
            'sortOrder': 100,
            'createdAtUtc': _fixtureTime,
            'updatedAtUtc': _fixtureTime,
          },
          {
            'id': 'custom-all-pinned',
            'mode': 'customer',
            'customerId': 301,
            'customerName': '博丽灵梦',
            'foodId': 103,
            'recipeId': 1003,
            'recipeName': '豚骨拉面',
            'foodTag': null,
            'extraIngredientIds': [4, 6],
            'enabled': true,
            'pinToTop': true,
            'sortOrder': 200,
            'createdAtUtc': _fixtureTime,
            'updatedAtUtc': _fixtureTime,
          },
          {
            // 同一料理的另一个加料组合必须保留条目身份；不能只按 foodId 去重。
            'id': 'custom-same-food-disabled',
            'mode': 'customer',
            'customerId': 302,
            'customerName': '雾雨魔理沙',
            'foodId': 102,
            'recipeId': 1002,
            'recipeName': '豆腐锅',
            'foodTag': '素',
            'extraIngredientIds': <int>[],
            'enabled': false,
            'pinToTop': true,
            'sortOrder': 300,
            'createdAtUtc': _fixtureTime,
            'updatedAtUtc': _fixtureTime,
          },
          {
            'id': 'custom-catalog-missing',
            'mode': 'customer',
            'customerId': 301,
            'customerName': '博丽灵梦',
            'foodId': 9904,
            'recipeId': 19904,
            'recipeName': '历史合成配方',
            'foodTag': '鲜',
            'extraIngredientIds': [9902],
            'enabled': false,
            'pinToTop': false,
            'sortOrder': 400,
            'createdAtUtc': _fixtureTime,
            'updatedAtUtc': _fixtureTime,
          },
        ],
      },
      '/missions/tracked': {
        // 合成数据仍遵循真实列表协议，使状态展示检查覆盖正式分支。
        'ok': true,
        'runtimeAvailable': true,
        'status': 'ready',
        'missions': [
          for (var i = 0; i < 3; i++)
            {
              'label': 'tracked-$i',
              'title': ['收集新鲜食材', '招待神社的客人', '准备暖心料理'][i],
              'characterName': ['博丽灵梦', '橙', '上白泽慧音'][i],
              'receiverLabel': '米斯蒂娅',
              'sceneNames': ['博丽神社'],
              'presentationStatus': 'ready',
              'status': ['tracking', 'fulfilled', 'unverified'][i],
              'completedConditionCount': i == 0
                  ? 2
                  : i == 1
                  ? 3
                  : null,
              'conditionCount': 3,
              'conditionStates': i == 0
                  ? [true, true, false]
                  : i == 1
                  ? [true, true, true]
                  : [null, null, null],
            },
        ],
      },
      '/missions/available': {
        'ok': true,
        'runtimeAvailable': true,
        'status': 'ready',
        'missions': [
          for (var i = 0; i < 2; i++)
            {
              'label': 'available-$i',
              'title': ['新的料理灵感', '夜晚的约定'][i],
              'characterName': ['雾雨魔理沙', '露米娅'][i],
              'receiverLabel': '米斯蒂娅',
              'sceneNames': ['妖怪兽道'],
              'presentationStatus': 'ready',
              'activationMode': i == 0 ? 'automatic' : 'conditional',
              'activationStatus': 'available',
              'triggerKind': i == 0
                  ? 'enter-day-scene-map'
                  : 'kizuna-checkpoint',
              'sourceTiming': i == 0
                  ? 'before-performance'
                  : 'after-performance',
              'activationHint': i == 0
                  ? 'enter-target-day-map'
                  : 'kizuna-ready',
            },
        ],
      },
      '/rare-guests/invitations': {
        'ok': true,
        'runtimeAvailable': true,
        'status': '合成邀请名单',
        'candidates': _invitationCandidates,
      },
      '/local-api/config': {
        'lanEnabled': false,
        'lanRunning': false,
        'lanBindHost': 'auto',
        'token': _previewToken,
        'availableLanEndpoints': [
          {
            'address': '192.168.1.50',
            'endpoint': 'http://192.168.1.50:32145',
            'interfaceName': '合成有线网卡',
            'interfaceType': 'Ethernet',
            'hasGateway': true,
            'linkLocal': false,
            'recommended': true,
          },
          {
            'address': '192.168.2.50',
            'endpoint': 'http://192.168.2.50:32145',
            'interfaceName': '合成无线网卡',
            'interfaceType': 'Wireless80211',
            'hasGateway': true,
            'linkLocal': false,
            'recommended': false,
          },
        ],
        'lanEndpoints': [
          {'interfaceName': '合成局域网', 'endpoint': 'http://192.168.1.50:32145'},
        ],
      },
      '/logs/settings': {
        'aggregateModLogEnabled': false,
        'aggregateModLogMaxFileCount': 30,
        'bepInExConsoleVisible': false,
        'bepInExConsoleSupported': true,
        'bepInExConsoleConfiguredVisible': false,
        'aggregateModLogDirectory': '预览不读写日志目录',
        'bepInExConsoleStatus': '合成状态',
      },
      '/logs/packages': {
        'ok': true,
        'items': [
          {
            'id': 'synthetic-package-1',
            'fileName': 'diagnostics-preview-1.zip',
            'byteLength': 123456,
            'createdAtUtc': _fixtureTime,
          },
          {
            'id': 'synthetic-package-2',
            'fileName': 'diagnostics-preview-2.zip',
            'byteLength': 45678,
            'createdAtUtc': _fixtureTime,
          },
        ],
      },
      '/updates/status': {
        'currentVersion': '1.3.1',
        'latestVersion': '1.3.2-preview',
        'state': '尚未检查',
        'hasUpdate': false,
        'staged': false,
        'installMessage': '预览不会下载或执行更新程序。',
        'availableReleases': [
          {
            'title': '1.3.2 · 合成版本说明',
            'notesMarkdown': '界面预览用说明：优化信息组织、保留原有权限边界。不是已发布版本。',
          },
        ],
      },
    };
    selectScenario('night');
  }

  /// 切换场景推进连接与场景代次，使正在打开的写入确认按生产 UI 规则失效。
  void selectScenario(String next) {
    // 方案到达只替换业务结果，保留订单生命周期、页面和连接身份，才能用原生
    // 预览检查同一张卡片从占位到内容的变化；不以重建场景掩盖布局跳动。
    if (next == 'orders-ready' && _pendingServicePlans != null) {
      scenario = next;
      business = {
        ...business,
        'isCurrent': true,
        'pending': false,
        'recommendations': _pendingServicePlans!,
      };
      _pendingServicePlans = null;
      _publish();
      return;
    }
    _pendingServicePlans = null;
    scenario = next;
    connectionVersion++;
    _generation++;
    _mediaGeneration++;
    connected = next != 'offline';
    error = connected ? '' : '离线预览：模拟连接中断。可切换场景或保存连接以恢复。';
    notice = '合成数据不会发送到游戏。';
    final viewer = next == 'viewer';
    authority = {
      'registryId': 'preview-registry',
      'authorityRevision': _generation,
      'stateRevision': _generation,
      'currentDeviceIsPrimary': !viewer,
      'currentDeviceId': 'preview-desktop',
      'activeProfileRevision': _generation,
      'devices': [
        {
          'deviceId': 'preview-desktop',
          'label': clientLabel,
          'platform': 'windows',
          'online': true,
          'isCurrent': true,
          'isPrimary': !viewer,
          'syncPending': false,
        },
        {
          'deviceId': 'preview-mobile',
          'label': '合成手机',
          'platform': 'android',
          'online': viewer,
          'isCurrent': false,
          'isPrimary': viewer,
          'syncPending': false,
        },
        {
          'deviceId': 'preview-old',
          'label': '旧设备记录',
          'platform': 'windows',
          'online': false,
          'isCurrent': false,
          'isPrimary': false,
          'syncPending': false,
        },
      ],
    };
    final day = next == 'day';
    // 每类列表覆盖零基桌号的两个边界，方便原生预览核对首桌1与末桌8。
    final orders = [for (var i = 0; i < 8; i++) _rareOrder(i)];
    final normal = [for (var i = 0; i < 8; i++) _normalOrder(i)];
    snapshot = {
      'pluginVersion': '离线预览',
      'activeSceneName': day ? '日间探索' : '夜间经营',
      'status': connected ? '合成运行时已就绪' : '连接中断',
      'runtimeDataComplete': true,
      'runtimeLoaded': true,
      'automationSessionId': 'preview-session-$_generation',
      'nightBusinessGeneration': _generation,
      'nightBusinessLifecyclePhase': day ? 'Inactive' : 'Active',
      'runtimeDaySceneGeneration': _generation,
      'runtimeDaySceneReady': day,
      'activeDayMapName': '博丽神社',
      'activeDayMapLabel': 'preview-hakurei',
      // 预览也使用正式只读原因码，避免合成自由文本绕过页面的原因解释分支。
      'nightBusinessAutomationBlockReason': day
          ? 'night-business-lifecycle-unavailable'
          : '',
      'runtimeSceneReadinessStatus': '合成场景',
      'capturedAtUtc': '离线合成时刻',
      'recommendationState': {
        // 合成预览使用明确内存样本；不以此声称已读取游戏或连接真实库存。
        'inventoryReadReady': true,
        'availableRecipeIds': [
          // 日间样本保留两份尚未解锁料理，便于真实窗口验收筛选；夜间既有
          // 八单推荐仍沿用全解锁样本，不改变原来方案/候选夹具的解释边界。
          for (final item in catalogItems('recipes').take(day ? 6 : 8))
            item['id'],
        ],
        // 这是独立的合成图鉴收录事实，故意不等于下方有库存酒水集合。
        // 预览仍只持有内存值，不读取真实存档、图鉴或平台服务。
        'beverageUnlockReadReady': true,
        'unlockedBeverageIds': [201, 203, 206],
        'availableBeverageIds': [
          for (final item in catalogItems('beverages')) item['id'],
        ],
        'popularFoodTag': '鲜',
        'popularHateFoodTag': '重油',
        // 明确的合成现场状态用于验收统计页；类型ID、可用枚举与生产契约一致。
        // 日间没有经营现场厨具；使用真实原因码验证中文投影，不能继承夜间的空闲锅样本。
        'placedCookerStatus': day
            ? 'not in night business scene'
            : '合成现场：3 个厨具',
        'placedCookerSnapshotComplete': !day,
        'placedCookers': [
          if (!day)
            for (final entry in [(1, '煮锅'), (4, '蒸锅'), (2, '烧烤架')])
              {
                'name': entry.$2,
                'typeIds': [entry.$1],
                'automationAvailability': entry.$1 == 4
                    ? 'ExtractedResidual'
                    : 'StrictIdle',
                'automationAvailabilityDiagnostic': '合成状态，仅用于离线排版验收',
              },
        ],
        'ownedIngredientQty': {
          for (var i = 1; i <= 11; i++) '$i': i == 3 ? -1 : i * 3,
        },
        'ownedBeverageQty': {
          for (var i = 201; i <= 205; i++) '$i': (i - 198) * 4,
        },
      },
      'nightBusiness': {
        'place': '博丽神社',
        'orders': day
            ? <Object?>[]
            : [for (final entry in orders) entry['order']],
      },
      'normalBusiness': {
        'orders': day
            ? <Object?>[]
            : [for (final entry in normal) entry['order']],
      },
      'specialBusiness': null,
    };
    business = {
      'protocolVersion': 1,
      'isCurrent': next != 'loading',
      'isCalculating': next == 'loading',
      'pending': next == 'loading',
      'calculationMs': 12,
      'recommendations': {
        'recommendations': day ? <DataMap>[] : orders,
        'normalOrderDetailPlans': day ? <DataMap>[] : normal,
      },
      'automation': {
        'message': day ? '当前为日间，自动化等待夜间营业。' : '合成经营状态 · 厨具可用 3 / 4',
        'safetyBarriers': next == 'barrier'
            ? [
                {
                  'sequence': 1,
                  'title': '需要人工核对',
                  'message': '合成订单送出结果不确定。核对现场后确认，仅更新预览状态。',
                },
              ]
            : <DataMap>[],
        'rareDiagnostics': [for (var i = 0; i < 5; i++) _diagnostic(i, 'rare')],
        'normalDiagnostics': [
          for (var i = 0; i < 3; i++) _diagnostic(i, 'normal'),
        ],
        'resourceOverview': {
          'cookers': [
            {
              'label': '煮锅',
              'cookerType': 1,
              'capacity': 2,
              'normalReserved': 1,
              'rareReserved': 0,
            },
            {
              'label': '蒸锅',
              'cookerType': 4,
              'capacity': 1,
              'normalReserved': 0,
              'rareReserved': 0,
            },
            {
              'label': '烧烤架',
              'cookerType': 2,
              'capacity': 1,
              'normalReserved': 0,
              'rareReserved': 1,
            },
          ],
        },
      },
    };
    if (next == 'orders-pending') {
      // 快照已有点单，但合成推荐尚未发布。复用生产只读投影生成 pending 字段，
      // 不在预览中直接伪造页面状态；已知原点料理、酒水仍由当前快照提供。
      _pendingServicePlans = object(business['recommendations']);
      business = {
        ...business,
        'isCurrent': false,
        'pending': true,
        'recommendations': <String, Object?>{},
      };
    }
    pageResult = {};
    _publish();
  }

  DataMap _rareOrder(int index) {
    final guest = catalogItems('rareCustomers')[index % 6];
    return {
      'order': {
        'deskCode': index,
        'guestName': guest['name'],
        // 规范人物 ID 与现场 runtimeGuestId 独立，方便验收精确头像联结。
        'guestId': guest['id'],
        'traceId': 'preview-trace-$index',
        'serviceOrderPosition': index * 2 + 2,
        'runtimeGuestId': 500 + index,
        'orderLifecycleSequence': _generation * 100 + index,
        'foodTagId': 1,
        'beverageTagId': 2,
        'foodTag': index.isEven ? '家常' : '清淡',
        'beverageTag': '无酒精',
        'fund': 100 + index * 15,
        'remainingOrderCount': 3,
        // 合成状态仅用于检查标签层次；不把人物名称当作真实游戏规则来源。
        'specialBusinessRoleLabel': index == 3 ? '合成挑战订单' : '',
        'automationAllowed': index != 4,
        'automationBlockReason': index == 4 ? '合成资源不足，暂不可自动处理。' : '',
        'isFreeOrder': index == 2,
      },
      'budget': index == 2
          ? null
          : {
              'policy': 'block',
              'estimatedPrice': 42 + index * 7,
              'remainingBudget': 100 + index * 15,
              'overBudget': 0,
              'source': 'preview',
            },
      'executionPlans': index == 4
          ? []
          : [
              _plan(index, free: index == 2),
              if (index < 3)
                _plan(index + 1, alternate: true, free: index == 2),
            ],
      'blockedMessages': index == 4 ? ['所需食材不足，等待补充。'] : [],
      'blockedDiagnostic': {
        'missingIngredientNames': ['南瓜'],
        'requiredCookerNames': ['蒸锅'],
      },
    };
  }

  DataMap _plan(int index, {bool alternate = false, bool free = false}) => {
    'food': {
      'recipe': catalogItems('recipes')[index % 8],
      'extraIngredients': [catalogItems('ingredients')[index % 12]],
      'activeTags': ['家常', '鲜', '素'],
      'suppressedTags': alternate ? ['肉'] : <String>[],
      'meetsRequiredFood': true,
    },
    'beverage': {
      'beverage': catalogItems('beverages')[index % 6],
      'activeTags': ['无酒精', '清爽'],
      'meetsRequiredBeverage': true,
    },
    'estimatedPrice': 42 + index * 7,
    'budget': free
        ? null
        : {
            'policy': 'block',
            'estimatedPrice': 42 + index * 7,
            'remainingBudget': 100 + index * 15,
            'overBudget': 0,
            'source': 'preview',
          },
    'bucket': alternate ? 'tradeoff' : 'complete',
    'reasons': ['满足料理需求', '覆盖客人偏好'],
    'warnings': alternate ? ['使用一份稀有食材'] : [],
  };
  DataMap _normalOrder(int index) => {
    'order': {
      'deskCode': index,
      // 与稀客交错排位，便于检查“稀客优先”只改变本机分组而不改变组内顺序。
      'serviceOrderPosition': index * 2 + 1,
      // 第三桌是“普通订单结构承载复制稀客”的合成样本。业务仍留在 normal，
      // 图片使用独立显式身份，不能让 UI 把 Guest.Type 当作规范稀客/客群编号。
      'guestName': index == 2
          ? '雾雨魔理沙（合成复制）'
          : ['妖精', '人类村民', '妖怪客人'][index % 3],
      'guestId': 401 + index % 3,
      'guestImageKind': index == 2 ? 'rare-guest' : 'normal-group',
      'guestImageId': index == 2 ? 302 : 401 + index % 3,
      'foodId': catalogItems('recipes')[index % 8]['id'],
      'foodName': catalogItems('recipes')[index % 8]['name'],
      'beverageId': catalogItems('beverages')[index % 6]['id'],
      'beverageName': catalogItems('beverages')[index % 6]['name'],
      'specialBusinessRole': '',
      'traceId': 'preview-normal-$index',
      // 仅合成预览身份；普通订单展示合并必须具有独立订单键，不能只靠桌号或名称。
      'orderKey': 'preview-pointer-$index',
      'orderLifecycleSequence': _generation * 100 + index,
      'hasServedFood': index == 2,
      'hasServedBeverage': index.isEven,
      'readyToEvaluate': index == 2,
      'hasEvaluated': false,
      'canAutomate': index != 1,
      'actionBlockReason': index == 1 ? '合成场景正在确认订单，暂不可自动处理。' : '',
    },
    'originalFood': _normalFood(index),
    'originalBeverage': _normalBeverage(index),
    'executionFood': _normalFood(index, execution: true),
    'executionBeverage': _normalBeverage(index),
    'usesSpecialExecution': index == 1,
    'hasExecutionOverride': index == 1,
    'executionReason': index == 1 ? '合成挑战要求补足当前目标标签。' : '',
    'selectionMessage': index == 1
        ? '合成特殊方案追加一份豆腐，完整规则见订单详情。'
        : '保持原订单料理，使用当前可用库存。',
  };

  /// 普客详情的扁平字段描述执行内容，完整目录实体仍位于 recipe/beverage 内。
  /// 标价不能通过展开目录字段额外塞入父对象，否则预览会掩盖生产适配取错路径。
  DataMap _normalFood(int index, {bool execution = false}) {
    final recipe = catalogItems('recipes')[index % 8];
    final extras = execution && index == 1
        ? [catalogItems('ingredients')[1]]
        : <DataMap>[];
    return {
      'recipe': recipe,
      'foodId': recipe['id'],
      'recipeId': recipe['recipeId'],
      'name': recipe['name'],
      'cookerName': recipe['cooker'],
      'baseIngredientNames': recipe['ingredients'],
      'extraIngredients': extras,
      'extraIngredientIds': [for (final extra in extras) extra['id']],
      'activeTags': ['家常', index.isEven ? '鲜' : '清淡'],
      'targetTags': execution && index == 1 ? ['清淡'] : <String>[],
      'suppressedTags': execution && index == 1 ? ['重油'] : <String>[],
    };
  }

  DataMap _normalBeverage(int index) {
    final beverage = catalogItems('beverages')[index % 6];
    return {
      'beverage': beverage,
      'beverageId': beverage['id'],
      'name': beverage['name'],
      'activeTags': beverage['tags'],
      'suppressedTags': <String>[],
    };
  }

  DataMap _diagnostic(int index, String kind) {
    final normal = kind == 'normal';
    final entry = normal ? _normalOrder(index) : _rareOrder(index);
    final order = object(entry['order']);
    final recipe = catalogItems('recipes')[index % 8];
    final beverage = catalogItems('beverages')[index % 6];
    final hasTarget = normal || records(entry['executionPlans']).isNotEmpty;
    final locked = !normal || index == 1;
    // 诊断样本与同一桌的只读订单/方案保持一致；不通过名称构造身份，也不模拟推荐算法。
    // 普客普通点单的目录回退与特殊方案的锁定目标在生产协议中是不同来源。
    return {
      'orderKey': 'preview-$kind-$index',
      'kind': kind,
      'guestId': order['guestId'],
      'guestName': order['guestName'],
      'guestImageKind': normal ? order['guestImageKind'] : 'rare-guest',
      'guestImageId': normal ? order['guestImageId'] : order['guestId'],
      'specialBusinessRole': '',
      'title': normal
          ? '桌 ${index + 1} · ${order['guestName']}'
          : '${order['guestName']} · 桌 ${index + 1}',
      'traceId': order['traceId'],
      'source': '合成订单捕获',
      'foodTag': order['foodTag'] ?? '',
      'beverageTag': order['beverageTag'] ?? '',
      'targetSource': !hasTarget
          ? ''
          : locked
          ? 'locked'
          : 'ordered-recipe',
      'foodId': hasTarget ? recipe['id'] : null,
      'recipeId': hasTarget ? recipe['recipeId'] : null,
      'recipeName': hasTarget ? recipe['name'] : '',
      'cookerName': hasTarget ? recipe['cooker'] : '',
      'extraIngredientIds': !hasTarget
          ? null
          : normal
          ? object(entry['executionFood'])['extraIngredientIds']
          : [catalogItems('ingredients')[index % 12]['id']],
      'beverageId': hasTarget ? beverage['id'] : null,
      'beverageName': hasTarget ? beverage['name'] : '',
      'foodName': normal ? recipe['name'] : '',
      'originalFoodId': normal ? recipe['id'] : null,
      'originalBeverageId': normal ? beverage['id'] : null,
      'originalBeverageName': normal ? beverage['name'] : '',
      'stepLabel': index == 0 ? '暂停待重试' : '等待厨具',
      'nextAction': '核对资源后继续',
      'stepSeconds': 3 + index * 2,
      'retryCount': index == 0 ? 1 : 0,
      'rollbackCount': 0,
      'paused': index == 0,
      'manualResolutionRequired': scenario == 'barrier' && index == 0,
      'completed': false,
      'prepared': order['hasServedFood'] == true,
      'foodDeliveryRequested': index == 0 || order['hasServedFood'] == true,
      'beverageDeliveryRequested': order['hasServedBeverage'] == true,
      'hasServedFood': order['hasServedFood'] == true,
      'hasServedBeverage': order['hasServedBeverage'] == true,
      'readyToEvaluate': order['readyToEvaluate'] == true,
      'hasEvaluated': false,
      'controllerAvailable': true,
      'canAutomate': normal ? order['canAutomate'] : order['automationAllowed'],
      'actionBlockReason': normal
          ? order['actionBlockReason']
          : order['automationBlockReason'],
      'lastRuntimeEventSequence': index + 1,
      'detailMessage': !hasTarget
          ? '尚未取得可执行目标，等待资源。'
          : index == 0
          ? '合成资源暂不可用，可验证重试入口。'
          : '计划已生成，等待可用资源。',
      'lastError': '',
    };
  }

  @override
  Future<void> connect(
    String endpoint,
    String token,
    String deviceLabel,
  ) async {
    // 接收表单仅为了验收流程；端点和 Token 不用于建立连接，也不保存任何秘密。
    clientLabel = deviceLabel.isEmpty ? '离线预览电脑' : deviceLabel;
    this.endpoint = endpoint;
    if (token.isNotEmpty) _previewToken = token;
    selectScenario('night');
    _recordOperation('保存并连接');
    _publish('模拟连接已恢复');
  }

  @override
  Future<void> refresh() async {
    _recordOperation('刷新当前状态');
    _publish('预览信息已刷新');
  }

  @override
  Future<void> readResource(
    String path, {
    Map<String, String> query = const {},
  }) async {
    if (!connected) return;
    if (path == '/rare-guests/invitations')
      resources[path] = {
        'ok': true,
        'runtimeAvailable': true,
        'status': '合成邀请名单 · ${query['scope'] == 'all' ? '全部地图' : '当前地图'}',
        'candidates': _invitationCandidates
            .where(
              (item) =>
                  query['scope'] == 'all' || item['isCurrentScene'] == true,
            )
            .toList(),
      };
    _publish();
  }

  /// 预览写入有独立白名单；未实现动作明确说明不执行，不能静默假装真实操作成功。
  @override
  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  }) async {
    error = '';
    try {
      if (!connected) {
        error = '预览处于断线状态，请先切换场景。';
        _publish();
        return;
      }
      if (!gameMediaReady) {
        error = '合成资源尚未就绪，本次预览操作未执行。';
        _publish();
        return;
      }
      if (!primary &&
          !const [
            '/devices/primary',
            '/devices/sync',
            '/devices/rename',
            '/updates/check',
          ].contains(path)) {
        error = '查看设备不能执行此操作。';
        _publish();
        return;
      }
      int number(String key) => int.tryParse(query[key] ?? '') ?? 0;
      List<int> ids(String key) => (query[key] ?? '')
          .split(',')
          .map(int.tryParse)
          .whereType<int>()
          .toList();
      // 正式收藏/自定义存储会去重、过滤负数并排序 ID；预览输入仍可包含重复值，
      // 但回显必须采用持久接口的形状，不能用非法重复输出误导公共组件验收。
      List<int> extraIds() =>
          ids('extraIngredientIds').where((id) => id >= 0).toSet().toList()
            ..sort();
      // 离线预览同样拒绝过期模式，不能让预览 UI 显示正式 API 不会接受的保存结果。
      if (path.startsWith('/custom-recipes/')) {
        final custom = resources['/custom-recipes']!;
        final mode = custom['activeMode'];
        final id = query['id'];
        final entry = records(custom['recipes'])
            .where((row) => row['id'] == id)
            .firstOrNull;
        if (query['expectedModeRevision'] != '${custom['modeRevision']}' ||
            (path != '/custom-recipes/settings' && query['mode'] != mode) ||
            (entry != null && entry['mode'] != mode) ||
            (path == '/custom-recipes/settings' &&
                query.containsKey('activeMode') &&
                !const ['food', 'customer'].contains(query['activeMode']))) {
          error = '配方模式已变化，请重新读取后操作。';
          _publish();
          return;
        }
      }
      switch (path) {
        case '/favorites/add-recipe':
        case '/favorites/add-beverage':
          final recipe = path.endsWith('recipe');
          final key = recipe ? 'recipes' : 'beverages';
          final id = number(recipe ? 'recipeId' : 'beverageId');
          final rows = [...records(resources['/favorites']![key])];
          rows.add({
            'id': 'favorite-${_identity++}',
            'customerId': number('customerId'),
            'customerName': query['customerName'],
            if (recipe) 'recipeId': id else 'beverageId': id,
            if (recipe) ...{
              'foodTag': query['foodTag'] ?? '',
              'extraIngredientIds': extraIds(),
            } else
              'beverageTag': query['beverageTag'] ?? '',
            'createdAtUtc': _fixtureTime,
            'updatedAtUtc': _fixtureTime,
          });
          resources['/favorites']![key] = rows;
        case '/favorites/remove-recipe':
        case '/favorites/remove-beverage':
          final key = path.endsWith('recipe') ? 'recipes' : 'beverages';
          resources['/favorites']![key] = records(resources['/favorites']![key])
              .where((item) => item['id'] != query['id'])
              .toList();
        case '/custom-recipes/upsert':
          final rows = [...records(resources['/custom-recipes']!['recipes'])];
          final id = (query['id'] ?? '').isEmpty
              ? 'custom-${_identity++}'
              : query['id'];
          final index = rows.indexWhere((item) => item['id'] == id);
          final existing = index < 0 ? const <String, Object?>{} : rows[index];
          final nextSortOrder =
              rows.fold<int>(0, (highest, row) {
                final order = row['sortOrder'];
                return order is int && order > highest ? order : highest;
              }) +
              100;
          final selectedTag = query['foodTag']?.trim();
          final value = <String, Object?>{
            'id': id,
            'mode': query['mode'],
            for (final key in ['customerId', 'foodId', 'recipeId'])
              key: number(key),
            for (final key in ['customerName', 'recipeName']) key: query[key],
            'foodTag': selectedTag == null || selectedTag.isEmpty
                ? null
                : selectedTag,
            'extraIngredientIds': extraIds(),
            // 编辑器可以只提交内容；未显式更新的启用、置顶和排序沿用原记录。
            'enabled': query.containsKey('enabled')
                ? query['enabled'] == 'true'
                : existing['enabled'] ?? true,
            'pinToTop': query.containsKey('pinToTop')
                ? query['pinToTop'] == 'true'
                : existing['pinToTop'] ?? true,
            'sortOrder':
                int.tryParse(query['sortOrder'] ?? '') ??
                existing['sortOrder'] ??
                nextSortOrder,
            'createdAtUtc': existing['createdAtUtc'] ?? _fixtureTime,
            'updatedAtUtc': _fixtureTime,
          };
          if (index < 0) {
            rows.add(value);
          } else {
            rows[index] = value;
          }
          resources['/custom-recipes']!['recipes'] = rows;
        case '/custom-recipes/remove':
          resources['/custom-recipes']!['recipes'] = records(
            resources['/custom-recipes']!['recipes'],
          ).where((item) => item['id'] != query['id']).toList();
        case '/custom-recipes/settings':
          final custom = resources['/custom-recipes']!;
          if (query.containsKey('enabled'))
            custom['enabled'] = query['enabled'] == 'true';
          if (query.containsKey('activeMode') &&
              custom['activeMode'] != query['activeMode']) {
            custom['activeMode'] = query['activeMode'];
            custom['modeRevision'] = (custom['modeRevision'] as int) + 1;
          }
        case '/custom-recipes/update-flags':
          resources['/custom-recipes']!['recipes'] = [
            for (final row in records(resources['/custom-recipes']!['recipes']))
              {
                ...row,
                if (row['id'] == query['id']) ...{
                  if (query.containsKey('enabled'))
                    'enabled': query['enabled'] == 'true',
                  if (query.containsKey('pinToTop'))
                    'pinToTop': query['pinToTop'] == 'true',
                },
              },
          ];
        case '/custom-recipes/move':
          final rows = [...records(resources['/custom-recipes']!['recipes'])];
          final index = rows.indexWhere((item) => item['id'] == query['id']);
          if (index >= 0) {
            // 与正式存储同样按当前模式的主体分组，不能跨组或跨模式移动。
            final siblings =
                rows
                    .where(
                      (row) =>
                          row['mode'] == query['mode'] &&
                          row[query['mode'] == 'food'
                                  ? 'foodId'
                                  : 'customerId'] ==
                              rows[index][query['mode'] == 'food'
                                  ? 'foodId'
                                  : 'customerId'],
                    )
                    .toList()
                  ..sort(
                    (a, b) => (a['sortOrder']! as int).compareTo(
                      b['sortOrder']! as int,
                    ),
                  );
            final from = siblings.indexWhere((row) => row['id'] == query['id']);
            final target = from + (query['direction'] == 'up' ? -1 : 1);
            if (target >= 0 && target < siblings.length) {
              final oldOrder = siblings[from]['sortOrder'];
              siblings[from]['sortOrder'] = siblings[target]['sortOrder'];
              siblings[target]['sortOrder'] = oldOrder;
              rows.sort(
                (a, b) =>
                    (a['sortOrder']! as int).compareTo(b['sortOrder']! as int),
              );
            }
          }
          resources['/custom-recipes']!['recipes'] = rows;
        case '/inventory/bulk-set':
        case '/inventory/set':
          final field = query['type'] == 'ingredient'
              ? 'ownedIngredientQty'
              : 'ownedBeverageQty';
          final quantities = {...object(runtime[field])};
          // 离线预览复现正式接口的比较后写入边界：快捷增减依赖点击时的数量，
          // 若该数量已变化则拒绝本次操作，不把旧目标值覆盖到更新后的库存。
          if (path == '/inventory/set' &&
              query.containsKey('expectedQuantity')) {
            final expected = int.tryParse(query['expectedQuantity'] ?? '');
            if (expected == null ||
                expected < 0 ||
                quantities['${number('id')}'] != expected) {
              error = '库存已变化，本次未修改，请刷新后再调整。';
              _publish();
              return;
            }
          }
          for (final id
              in path.endsWith('bulk-set') ? ids('ids') : [number('id')]) {
            quantities['$id'] = number('qty');
          }
          snapshot['recommendationState'] = {...runtime, field: quantities};
        case '/rare-guests/invite':
        case '/rare-guests/invite-all':
          if (scenario != 'day') {
            error = '请切换至日间工具场景。';
            _publish();
            return;
          }
          final levels = ids('levels');
          _invitationCandidates = [
            for (final item in _invitationCandidates)
              {
                ...item,
                if (item['canInvite'] == true &&
                    (path.endsWith('invite')
                        ? item['id'] == number('guestId')
                        : levels.contains(item['kizunaLevel']) &&
                              (query['scope'] == 'all' ||
                                  item['isCurrentScene'] == true))) ...{
                  // 单邀与批量邀请共用状态投影；三个字段必须一起更新，避免
                  // 外层卡片同时显示“可邀请”和“预览已邀请”的矛盾信息。
                  'canInvite': false,
                  'status': 'invited',
                  'reason': '预览已邀请',
                },
              },
          ];
          await readResource('/rare-guests/invitations', query: query);
        case '/devices/primary':
          authority = {
            ...authority,
            'currentDeviceIsPrimary': body?['deviceId'] == 'preview-desktop',
            'authorityRevision': (authority['authorityRevision'] as int) + 1,
            'devices': [
              for (final device in records(authority['devices']))
                {
                  ...device,
                  'isPrimary': device['deviceId'] == body?['deviceId'],
                },
            ],
          };
        case '/devices/sync':
          authority['devices'] = [
            for (final device in records(authority['devices']))
              {...device, 'syncPending': false},
          ];
        case '/devices/rename':
          clientLabel = label(body?['label'], clientLabel);
          authority['devices'] = [
            for (final device in records(authority['devices']))
              {
                ...device,
                if (device['isCurrent'] == true) 'label': clientLabel,
              },
          ];
        case '/devices/forget':
          authority['devices'] = records(authority['devices'])
              .where(
                (device) =>
                    device['deviceId'] != body?['deviceId'] ||
                    device['isPrimary'] == true ||
                    device['isCurrent'] == true,
              )
              .toList();
        case '/logs/packages/delete':
          final items = records(resources['/logs/packages']!['items']);
          if (items.where((item) => item['id'] == body?['id']).length != 1) {
            error = '合成诊断包已变化，请刷新列表后重新确认。';
            return;
          }
          resources['/logs/packages'] = {
            'ok': true,
            'items': items.where((item) => item['id'] != body?['id']).toList(),
          };
        case '/logs/config':
          resources['/logs/settings'] = {
            ...resources['/logs/settings']!,
            if (query.containsKey('aggregateLog'))
              'aggregateModLogEnabled': query['aggregateLog'] == 'true',
            if (query.containsKey('aggregateLogMaxFiles'))
              'aggregateModLogMaxFileCount': number('aggregateLogMaxFiles'),
          };
        case '/logs/console':
          resources['/logs/settings']!['bepInExConsoleVisible'] =
              query['visible'] == 'true';
        case '/updates/check':
          resources['/updates/status']!.addAll({
            'hasUpdate': true,
            'state': '发现合成更新',
          });
        case '/updates/download':
          resources['/updates/status']!.addAll({
            'staged': true,
            'state': '合成包已就绪',
            'installMessage': '未下载任何文件，仅切换预览状态。',
          });
        case '/updates/install-on-exit':
          resources['/updates/status']!['installMessage'] =
              '预览安装流程已确认，未退出进程或启动更新程序。';
        case '/orders/rare/dismiss':
          business['recommendations'] = {
            ...recommendations,
            'recommendations': records(recommendations['recommendations'])
                .where(
                  (item) =>
                      object(item['order'])['deskCode'] != number('deskCode'),
                )
                .toList(),
          };
        case '/logs/export-diagnostics':
          resources['/logs/packages'] = {
            'ok': true,
            'items': [
              {
                'id': 'synthetic-package-${++_identity}',
                'fileName': 'diagnostics-preview-$_identity.zip',
                'byteLength': 65432,
                'createdAtUtc': _fixtureTime,
              },
              ...records(resources['/logs/packages']!['items']),
            ],
          };
          _publish('已添加合成诊断包记录，不生成文件');
          return;
        case '/logs/open-folder':
          _publish('预览不打开目录');
          return;
        default:
          error = '此动作不在离线预览范围：$path';
          _publish();
          return;
      }
      _publish('预览操作已完成');
    } finally {
      if (path != '/updates/status')
        _recordOperation(workbenchOperationName(path));
      _publish();
    }
  }

  @override
  Future<void> updateLanConnection({
    required bool enabled,
    required String host,
    String? token,
  }) async {
    error = '';
    try {
      if (!desktop || !connected || !primary) {
        error = '请在合成游戏电脑主设备修改局域网设置。';
        return;
      }
      if (token != null && !RegExp(r'^[\x21-\x7E]{8,128}$').hasMatch(token)) {
        error = 'Token 须为 8–128 位非空白可见 ASCII 字符。';
        return;
      }
      final config = resources['/local-api/config']!;
      if (host != 'auto' &&
          !records(config['availableLanEndpoints'])
              .any((item) => item['address'] == host)) {
        error = '所选合成网卡已不可用。';
        return;
      }
      if (token != null) _previewToken = token;
      resources['/local-api/config'] = {
        ...config,
        'lanEnabled': enabled,
        'lanRunning': enabled,
        'lanBindHost': host,
        'token': _previewToken,
        'lanEndpoints': enabled
            ? records(config['availableLanEndpoints'])
                  .where(
                    (item) => host == 'auto'
                        ? item['recommended'] == true
                        : item['address'] == host,
                  )
                  .toList()
            : <DataMap>[],
      };
      connectionVersion++;
      _publish('合成局域网设置已保存，不修改真实网络或凭据');
    } finally {
      _recordOperation('保存局域网连接');
      _publish();
    }
  }

  @override
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  }) async {
    if (!connected) return;
    // 固定合成候选只验证筛选/选择/收藏等界面流程，不冒充 C# 推荐计算或排序结果。
    // 四类页面行分别遵循 BusinessQueries、RecommendationEngine.Normal 与
    // OrderRecommendationService.Rows 的输出字段；禁止把经营计划字段混入页面行。
    pageResult = {
      'protocolVersion': 1,
      'isCurrent': true,
      'result': {
        'kind': kind,
        'recipes': [
          for (final (i, recipe) in catalogItems('recipes').indexed)
            kind == 'normal'
                ? _normalPageFood(recipe, i)
                : _rarePageFood(recipe, i, foodTag),
        ],
        'beverages': [
          for (final (i, beverage) in catalogItems('beverages').indexed)
            kind == 'normal'
                ? _normalPageBeverage(beverage)
                : _rarePageBeverage(beverage, i, beverageTag),
        ],
      },
    };
    _publish('已显示合成候选');
  }

  /// 覆盖是已给定的合成结果：三项命中分布在两名客人，第三名未命中。
  /// 特意让 totalCoverage 与 coveredCustomerCount 不同，防止 UI 把标签总数称为人数。
  List<DataMap> _pageCoverage(List<Object?> tags) => [
    {
      'customerId': 401,
      'customerName': '妖精',
      'matchedTagCount': 2,
      'matchedTags': tags,
    },
    {
      'customerId': 402,
      'customerName': '人类村民',
      'matchedTagCount': 1,
      'matchedTags': [tags.first],
    },
    {
      'customerId': 403,
      'customerName': '妖怪客人',
      'matchedTagCount': 0,
      'matchedTags': <String>[],
    },
  ];

  DataMap _normalPageFood(DataMap recipe, int index) {
    final tags = values(recipe['positiveTags']);
    return {
      'recipe': recipe,
      'activeTags': tags,
      'suppressedTags': index == 3 ? ['重油'] : <String>[],
      'matchedTags': tags,
      'ingredientCost': index == 7
          ? 16
          : index.isEven
          ? 10
          : 11,
      'profit': const [18, 24, 32, 38, 46, 52, 60, 61][index],
      'totalCoverage': 3,
      'coveredCustomerCount': 2,
      'customerCoverage': _pageCoverage(tags),
      'conditionResults': [
        {
          'id': 'normal.food.coverage',
          'target': 'food',
          'status': 'boost',
          'severity': 'soft',
          'label': '普客覆盖',
          'detail': '合成覆盖：妖精命中2项，人类村民命中1项。',
        },
      ],
    };
  }

  DataMap _normalPageBeverage(DataMap beverage) {
    final tags = values(beverage['tags']);
    return {
      'beverage': beverage,
      'activeTags': tags,
      'matchedTags': tags,
      'totalCoverage': 3,
      'coveredCustomerCount': 2,
      'customerCoverage': _pageCoverage(tags),
      'conditionResults': [
        {
          'id': 'normal.beverage.coverage',
          'target': 'beverage',
          'status': 'boost',
          'severity': 'soft',
          'label': '普客覆盖',
          'detail': '合成酒水覆盖：三项匹配，覆盖两名客人。',
        },
      ],
    };
  }

  DataMap _rarePageFood(DataMap recipe, int index, String foodTag) {
    final requiredTag = foodTag.isEmpty ? '家常' : foodTag;
    final meets = index % 3 != 2;
    final extras = index == 1
        ? <DataMap>[]
        : [catalogItems('ingredients')[3 + index % 4]];
    return {
      'recipe': recipe,
      'extraIngredients': extras,
      'allTags': [
        if (meets) requiredTag,
        for (final tag in ['鲜', '素'])
          if (tag != requiredTag) tag,
      ],
      'cancelledTags': index == 3 ? ['肉'] : <String>[],
      'meetsRequiredFood': meets,
      'baseCost': index == 7
          ? 16
          : index.isEven
          ? 10
          : 11,
      'extraCost': index == 1 ? 0 : 7 + index % 4,
      'extraIngredientReasonTags': {
        for (final extra in extras) '${extra['id']}': ['鲜'],
      },
      if (index == 1 || index == 2) ...{
        'customRecipe': true,
        'customRecipePinned': index == 2,
        'customRecipeSortOrder': index == 1 ? 100 : 200,
        'customRecipeScope': index == 2 ? 'all' : 'tag',
        'customRecipeId': index == 1 ? 'custom-start' : 'custom-all-pinned',
      },
    };
  }

  /// 稀客页面只给出匹配标签；不伪造经营计划才携带的 activeTags/suppressedTags，
  /// 也不把 meetsRequiredBev 的页面字段写成 meetsRequiredBeverage。
  /// 三组固定合成标签各有预设匹配行；此处仅切换夹具，不调用或复刻推荐评分算法。
  DataMap _rarePageBeverage(DataMap beverage, int index, String beverageTag) =>
      {
        'beverage': beverage,
        'meetsRequiredBev': beverageTag == '清爽'
            ? true
            : beverageTag == '低酒精'
            ? index >= 3
            : index < 3,
        'matchedTags': ['清爽'],
      };

  @override
  Future<void> updateProfile(DataMap changes) async {
    if (!canEdit) return;
    // 复用规范 DTO 验证表单范围，但不使用真实权限、租约或业务计算服务。
    try {
      profile = _copy(CompanionProfile({...profile, ...changes}).json);
      error = '';
    } catch (cause) {
      error = cause.toString();
      _publish();
      return;
    }
    authority['authorityRevision'] =
        (authority['authorityRevision'] as int) + 1;
    _recordOperation('保存共享设置');
    _publish('共享设置已保存');
  }

  @override
  Future<void> updateLocal(DataMap changes) async {
    localPreferences = {...localPreferences, ...changes};
    _publish();
  }

  @override
  Future<void> acknowledgeBarrier(
    int sequence, {
    required String expectedSessionId,
    required int expectedGeneration,
  }) async {
    if (!canExecute ||
        snapshot['automationSessionId'] != expectedSessionId ||
        snapshot['nightBusinessGeneration'] != expectedGeneration) {
      error = '场景或权限已经变化，未确认合成屏障。';
      _publish();
      return;
    }
    business['automation'] = {
      ...automation,
      'safetyBarriers': records(automation['safetyBarriers'])
          .where((item) => item['sequence'] != sequence)
          .toList(),
    };
    error = '';
    _publish('人工确认已记录');
  }

  @override
  Future<void> retryOrder(String kind, String key) async {
    if (!canExecute) return;
    final field = kind == 'rare' ? 'rareDiagnostics' : 'normalDiagnostics';
    business['automation'] = {
      ...automation,
      field: [
        for (final item in records(automation[field]))
          {
            ...item,
            if (item['orderKey'] == key &&
                item['manualResolutionRequired'] != true) ...{
              'paused': false,
              'stepLabel': '重试已排队',
              'detailMessage': '仅改变合成诊断，没有调用游戏。',
            },
          },
      ],
    };
    _publish('预览重试已记录');
  }

  @override
  Future<void> platformCommand(String command) async {
    if (command == 'exit') {
      if (onExit == null) {
        error = '当前预览宿主未提供退出回调，请关闭测试宿主。';
        _publish();
        return;
      }
      try {
        await onExit!();
      } catch (cause) {
        error = '退出预览失败：$cause';
        _publish();
      }
      return;
    }
    _publish('预览不执行原生窗口命令：$command');
  }

  @override
  Future<void> importHistory(String path) async {
    _publish('预览不读取历史文件；正式客户端使用真实迁移入口');
  }

  @override
  void clearRecommendationQuery() {
    pageResult = {};
  }
}
