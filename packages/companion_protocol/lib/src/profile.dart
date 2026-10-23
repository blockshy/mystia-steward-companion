import 'json.dart';

/// 共享配置 schema 4 的字段集合。订单始终按点单时间展示，不再保存排序选项。
/// 模块总开关随配置保存，实际写入仍受主设备、
/// 持久化确认和服务端 lease 约束；HTTP 不接收历史 schema 的不完整配置。
class CompanionProfile {
  CompanionProfile(JsonMap value) : json = jsonMap(value) {
    final expected = {...booleanFields, ...integerRanges.keys, ...otherFields};
    if (json.length != expected.length || !expected.containsAll(json.keys)) {
      throw const FormatException('共享配置字段与 schema 4 不一致。');
    }
    for (final key in booleanFields) {
      jsonBool(json, key);
    }
    for (final entry in integerRanges.entries) {
      final number = jsonInt(json, entry.key);
      if (number < entry.value.$1 || number > entry.value.$2) {
        throw FormatException('${entry.key} 超出允许范围。');
      }
    }
    for (final key in [
      'rareTargetHighlightColor',
      'normalTargetHighlightColor',
    ]) {
      if (!RegExp(r'^#[0-9A-F]{6}$').hasMatch(jsonString(json, key))) {
        throw FormatException('$key 必须为大写 #RRGGBB。');
      }
    }
    _choice(json, 'recommendationBudgetPolicy', {'block', 'warn', 'ignore'});
    _validateSort(jsonMap(json['recommendationSortProfile']));
    final exclusions = jsonMap(json['recommendationExclusions']);
    if (exclusions.length != 2) throw const FormatException('排除项字段错误。');
    for (final key in ['excludedIngredientIds', 'excludedBeverageIds']) {
      final list = exclusions[key];
      if (list is! List<Object?> || list.length > 4096)
        throw const FormatException('排除项列表无效。');
      var previous = -1;
      for (final value in list) {
        if (value is! int || value < 0 || value <= previous)
          throw const FormatException('排除 ID 必须严格递增。');
        previous = value;
      }
    }
    if (!flag('autoNormalCompleteOrder') &&
            (flag('autoNormalTakeBeverage') || flag('autoNormalDeliverFood')) ||
        !flag('autoPrepCompleteOrder') &&
            (flag('autoPrepTakeBeverage') || flag('autoPrepCollectCooking'))) {
      throw const FormatException('送餐子步骤要求开启完成订单。');
    }
  }
  final JsonMap json;
  static const schemaVersion = 4;
  static const booleanFields = <String>[
    'automationEnabled',
    'gameUiEnabled',
    'autoRareOrderEnabled',
    'autoNormalOrderEnabled',
    'autoNormalTakeBeverage',
    'autoNormalStartCooking',
    'autoNormalDeliverFood',
    'autoNormalCompleteOrder',
    'autoNormalStopOnError',
    'autoPrepCompleteOrder',
    'autoPrepTakeBeverage',
    'autoPrepStartCooking',
    'autoPrepCollectCooking',
    'autoPrepRecipeFavoritesOnly',
    'autoPrepBeverageFavoritesOnly',
    'autoPrepStopOnError',
    'filterMissingCookers',
    'missionRecipePriorityEnabled',
    'pinFavoriteRecipeEnabled',
    'pinFavoriteBeverageEnabled',
    'rareGameUiPinningEnabled',
    'normalGameUiPinningEnabled',
    'rareRecipeVariantEnabled',
    'normalRecipeVariantEnabled',
    'rareCookerHighlightEnabled',
    'normalCookerHighlightEnabled',
    'rareSeatHighlightEnabled',
    'normalSeatHighlightEnabled',
    'rareOrderHighlightEnabled',
    'normalOrderHighlightEnabled',
  ];

  /// 旧版没有界面辅助总开关，任意分项开启即代表此前使用了辅助模块。
  /// 此清单只用于一次性历史数据迁移，不会在运行中反向覆盖用户的总开关。
  static const gameUiFeatureFields = <String>[
    'rareGameUiPinningEnabled',
    'normalGameUiPinningEnabled',
    'rareRecipeVariantEnabled',
    'normalRecipeVariantEnabled',
    'rareCookerHighlightEnabled',
    'normalCookerHighlightEnabled',
    'rareSeatHighlightEnabled',
    'normalSeatHighlightEnabled',
    'rareOrderHighlightEnabled',
    'normalOrderHighlightEnabled',
  ];

  /// 仅本机历史 checkpoint 可调用此迁移入口。无版本的旧记录按 schema 1
  /// 处理；v1/v2 先验证旧字段并删除订单排序，v3 保留字段，三者均显式迁移评分模式。
  /// 不在运行中保留旧排序分支，也不能用补默认值掩盖损坏或未来版本数据。
  /// 返回值仅作为注册初值；revision/hash/ACK 仍以服务端返回并持久化的结果为准。
  factory CompanionProfile.fromCheckpoint(JsonMap value, Object? version) {
    if (version != null && version is! int) {
      throw const FormatException('历史共享配置版本必须为整数。');
    }
    if (version == schemaVersion) return CompanionProfile(value);
    if (version != null && version != 1 && version != 2 && version != 3) {
      throw const FormatException('不支持的历史共享配置版本。');
    }
    final legacyFields = {
      ...booleanFields,
      ...integerRanges.keys,
      ...otherFields,
      if (version != 3) 'serviceOrderSortMode',
    };
    // 前面的精确类型校验已排除非整数；显式转换避免 Object? 在空值合并后
    // 保留 Object 静态类型，确保迁移分支在 Flutter 编译器中也按整数比较。
    final legacyVersion = (version as int?) ?? 1;
    if (legacyVersion == 1) legacyFields.remove('gameUiEnabled');
    if (value.length != legacyFields.length ||
        !legacyFields.containsAll(value.keys)) {
      throw FormatException('历史共享配置字段与 schema $legacyVersion 不一致。');
    }
    if (legacyVersion < 3)
      _choice(value, 'serviceOrderSortMode', {'ordered', 'guest'});
    final migrated = Map<String, Object?>.of(value)
      ..remove('serviceOrderSortMode');
    if (legacyVersion == 1) {
      migrated['gameUiEnabled'] = gameUiFeatureFields.any(
        (key) => value[key] == true,
      );
    }
    final sort = jsonMap(value['recommendationSortProfile']);
    _validateSort(sort, legacy: true);
    // 历史界面允许在固定预设名称下修改参数。迁移只修正这个持久模式标识，
    // 逐项保留原启用、方向和权重，绝不因为名字仍是“均衡”而重置用户数值。
    migrated['recommendationSortProfile'] = {
      ...sort,
      'preset': _matchesPreset(sort) ? sort['preset'] : 'custom',
    };
    return CompanionProfile(migrated);
  }

  /// 线上固定预设必须完整匹配其定义；只有显式 custom 接受自由参数。
  /// legacy 只供一次性 checkpoint 迁移验证旧格式，不成为在线兼容分支。
  static void _validateSort(JsonMap sort, {bool legacy = false}) {
    if (sort.length != 2 || !sort.containsKey('objectives'))
      throw const FormatException('排序配置字段错误。');
    _choice(sort, 'preset', {
      'balanced',
      'resources',
      'profit',
      'simple',
      if (!legacy) 'custom',
    });
    final objectives = jsonObjects(sort['objectives']);
    if (objectives.length != objectiveKeys.length ||
        objectives.map((item) => jsonString(item, 'key')).toSet().length !=
            objectiveKeys.length) {
      throw const FormatException('排序目标必须包含九项且不得重复。');
    }
    for (final item in objectives) {
      if (item.length != 4 || !objectiveKeys.contains(jsonString(item, 'key')))
        throw const FormatException('未知排序目标。');
      jsonBool(item, 'enabled');
      final weight = jsonInt(item, 'weight');
      if (weight < 0 || weight > 100) throw const FormatException('排序权重超出范围。');
      _choice(item, 'direction', {'asc', 'desc'});
    }

    if (!legacy && sort['preset'] != 'custom' && !_matchesPreset(sort)) {
      throw const FormatException('固定评分预设不能修改参数，请使用自定义模式。');
    }
  }

  /// 比较完整目标值而非数组排列。这里只用于验证模式或迁移历史标识，不计算评分，
  /// 也不把自定义数值恰好相同的配置自动改回预设，避免远端读回后模式变化。
  static bool _matchesPreset(JsonMap sort) {
    final expected = {
      for (final row in jsonObjects(
        sortPreset(jsonString(sort, 'preset'))['objectives'],
      ))
        jsonString(row, 'key'): row,
    };
    return jsonObjects(sort['objectives']).every(
      (row) =>
          canonicalJson(row) == canonicalJson(expected[jsonString(row, 'key')]),
    );
  }

  static const integerRanges = <String, (int, int)>{
    'autoRareConcurrency': (1, 4),
    'autoNormalConcurrency': (1, 6),
    'autoMaxStepRetries': (1, 10),
    'autoMaxRollbacks': (0, 5),
    'recipeVariantLimitPerBase': (1, 8),
  };
  static const otherFields = [
    'rareTargetHighlightColor',
    'normalTargetHighlightColor',
    'recommendationSortProfile',
    'recommendationBudgetPolicy',
    'recommendationExclusions',
  ];
  static const objectiveKeys = [
    'foodPreference',
    'beveragePreference',
    'negativeRisk',
    'extraCount',
    'resourcePressure',
    'totalCost',
    'profit',
    'beverageStock',
    'cookerAvailable',
  ];

  /// C# RecommendationEngine 的四组表单预设。这里只提供用户选择后的完整
  /// 配置值，不在客户端计算分数或重排候选；实际推荐结果始终由业务模块产生。
  /// 九项目标采用协议固定顺序，权重和方向决定排序，数组位置不是优先级。
  /// 隔离真实宿主测试逐组比较 C# 导出，避免两端默认值悄然漂移。
  static JsonMap sortPreset(String preset) {
    const weights = <String, List<int>>{
      'balanced': [70, 60, 90, 45, 55, 30, 35, 35, 60],
      'resources': [55, 45, 90, 65, 100, 70, 20, 80, 60],
      'profit': [60, 50, 85, 25, 35, 25, 100, 25, 50],
      'simple': [50, 45, 90, 100, 45, 45, 25, 40, 70],
    };
    const ascending = {
      'negativeRisk',
      'extraCount',
      'resourcePressure',
      'totalCost',
    };
    final selected = weights[preset];
    if (selected == null) throw const FormatException('未知排序预设。');
    return jsonMap({
      'preset': preset,
      'objectives': [
        for (var index = 0; index < objectiveKeys.length; index++)
          {
            'key': objectiveKeys[index],
            'enabled': true,
            'weight': selected[index],
            'direction': ascending.contains(objectiveKeys[index])
                ? 'asc'
                : 'desc',
          },
      ],
    });
  }

  /// 全新设备默认关闭两个执行模块；历史配置由显式迁移入口保留已有选择。
  factory CompanionProfile.defaults() => CompanionProfile({
    for (final key in booleanFields) key: false,
    'filterMissingCookers': true,
    'missionRecipePriorityEnabled': true,
    'autoRareConcurrency': 2,
    'autoNormalConcurrency': 3,
    'autoMaxStepRetries': 3,
    'autoMaxRollbacks': 2,
    'recipeVariantLimitPerBase': 1,
    'rareTargetHighlightColor': '#FFDB2E',
    'normalTargetHighlightColor': '#5FACD3',
    'recommendationBudgetPolicy': 'block',
    'recommendationSortProfile': sortPreset('balanced'),
    'recommendationExclusions': {
      'excludedIngredientIds': <int>[],
      'excludedBeverageIds': <int>[],
    },
  });
  bool flag(String key) => jsonBool(json, key);
  bool get automationEnabled => flag('automationEnabled');
  CompanionProfile copyWith(JsonMap changes) =>
      CompanionProfile({...json, ...changes});
  JsonMap toJson() => json;
  static void _choice(JsonMap value, String key, Set<String> choices) {
    if (!choices.contains(jsonString(value, key)))
      throw FormatException('$key 包含未知枚举。');
  }
}
