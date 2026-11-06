import 'json.dart';

/// 共享配置 schema 10 的字段集合。低耐心自动启用与调度名单共用修订、持久化及同步，
/// 默认为关闭；普客、稀客分别保存独立辅助开关及 1–3 色调色板，
/// 每种颜色对应一笔辅助订单，不再保存可与颜色数量冲突的独立订单上限。
/// 模块总开关随配置保存，实际写入仍受主设备、
/// 持久化确认和服务端 lease 约束；HTTP 不接收历史 schema 的不完整配置。
class CompanionProfile {
  CompanionProfile(JsonMap value) : json = jsonMap(value) {
    final expected = {...booleanFields, ...integerRanges.keys, ...otherFields};
    if (json.length != expected.length || !expected.containsAll(json.keys)) {
      throw const FormatException('共享配置字段与 schema 10 不一致。');
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
    for (final field in paletteFields) {
      _validatePalette(json[field], field, maximum: maxHighlightColors);
    }
    _validateIds(json['managedRareGuestIds'], 512, '手动调度名单');
    _choice(json, 'rareGuestParticipationAutoResumePosition', {
      'front',
      'tail',
    });
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
  static const schemaVersion = 10;
  static const booleanFields = <String>[
    'automationEnabled',
    'gameUiEnabled',
    ...gameUiKindFields,
    'rareGuestParticipationModuleEnabled',
    'rareGuestParticipationAutoResumeEnabled',
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

  /// 分类开关与各辅助分项分开保存；关闭某类不会清空它的配色、数量和功能组合。
  /// 全局开关仍控制整个模块，不能由分类开关或分项反向推导开启。
  static const gameUiKindFields = <String>[
    'rareGameUiEnabled',
    'normalGameUiEnabled',
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
    if (version != null &&
        version != 1 &&
        version != 2 &&
        version != 3 &&
        version != 4 &&
        version != 5 &&
        version != 6 &&
        version != 7 &&
        version != 8 &&
        version != 9) {
      throw const FormatException('不支持的历史共享配置版本。');
    }
    final legacyVersion = (version as int?) ?? 1;
    final legacyFields = {
      ...booleanFields,
      ...integerRanges.keys,
      ...otherFields,
      if (legacyVersion < 3) 'serviceOrderSortMode',
      if (legacyVersion < 5) 'rareTargetHighlightColor',
      if (legacyVersion < 5) 'normalTargetHighlightColor',
      if (legacyVersion == 5 || legacyVersion == 6)
        ...legacyHighlightCountFields,
    }..removeAll(autoResumeDefaults.keys);
    if (legacyVersion < 9) legacyFields.removeAll(gameUiKindFields);
    if (legacyVersion < 5) {
      legacyFields.removeAll(newSchemaFields);
    } else if (legacyVersion == 5) {
      legacyFields.removeAll(paletteFields);
      legacyFields.add('orderHighlightPalette');
    }
    if (legacyVersion == 1) legacyFields.remove('gameUiEnabled');
    if (value.length != legacyFields.length ||
        !legacyFields.containsAll(value.keys)) {
      throw FormatException('历史共享配置字段与 schema $legacyVersion 不一致。');
    }
    // 旧版本只有全局开关及具体分项；新增分类总开关默认开启，确保升级不会
    // 关闭原本生效的辅助。全局关闭、分项关闭和自定义颜色均保持原值。
    final kindDefaults = <String, Object?>{
      if (legacyVersion < 9)
        for (final field in gameUiKindFields) field: true,
      ...autoResumeDefaults,
    };
    if (legacyVersion == 8 || legacyVersion == 9) {
      return CompanionProfile({...value, ...kindDefaults});
    }
    if (legacyVersion == 5 || legacyVersion == 6) {
      // 被删除字段仍须先按旧协议验证，不能把错误类型或越界值当成可迁移记录。
      // 验证完成后仅移除独立上限，原颜色数量成为该类唯一的辅助订单上限。
      for (final field in legacyHighlightCountFields) {
        final count = jsonInt(value, field);
        if (count < 1 || count > 8) {
          throw FormatException('$field 历史辅助数量无效。');
        }
      }
    }
    if (legacyVersion == 6 || legacyVersion == 7) {
      final migrated = Map<String, Object?>.of(value)
        ..removeWhere((key, _) => legacyHighlightCountFields.contains(key));
      for (final kind in ['rare', 'normal']) {
        final field = '${kind}OrderHighlightPalette';
        // 先核验旧版允许的完整四色，再缩减；非法第四色不能因截断而被掩盖。
        _validatePalette(value[field], field, maximum: 4);
        migrated[field] = _migrateIndependentPalette(
          (value[field]! as List<Object?>).cast<String>(),
          kind,
          replaceFormerDefaults: legacyVersion == 7,
        );
      }
      return CompanionProfile({...migrated, ...kindDefaults});
    }
    // v5 的八色公共调色板经严格核验后分别复制；只保留前三色，以符合新的数量上限。
    // 两类字段拥有独立列表，后续修改一类不得通过共享引用改变另一类；其余偏好完全保留。
    if (legacyVersion == 5) {
      _validatePalette(
        value['orderHighlightPalette'],
        '历史调色板',
        minimum: 8,
        maximum: 8,
      );
      final colors = (value['orderHighlightPalette']! as List<Object?>).take(
        maxHighlightColors,
      );
      return CompanionProfile({
        ...(Map<String, Object?>.of(value)
          ..remove('orderHighlightPalette')
          ..removeWhere((key, _) => legacyHighlightCountFields.contains(key))),
        'rareOrderHighlightPalette': colors.toList(),
        'normalOrderHighlightPalette': colors.toList(),
        ...kindDefaults,
      });
    }
    if (legacyVersion < 3) {
      _choice(value, 'serviceOrderSortMode', {'ordered', 'guest'});
    }
    for (final key in [
      'rareTargetHighlightColor',
      'normalTargetHighlightColor',
    ]) {
      if (!RegExp(r'^#[0-9A-F]{6}$').hasMatch(jsonString(value, key))) {
        throw FormatException('$key 历史颜色无效。');
      }
    }
    final migrated = Map<String, Object?>.of(value)
      ..remove('serviceOrderSortMode')
      ..remove('rareTargetHighlightColor')
      ..remove('normalTargetHighlightColor');
    if (legacyVersion == 1) {
      migrated['gameUiEnabled'] = gameUiFeatureFields.any(
        (key) => value[key] == true,
      );
    }
    // v1–v4 原本各有一种颜色，分别保留为对应通道的第一色，再补足默认两色。
    // 不把普客颜色迁入稀客通道，也不要求两个独立通道之间颜色互斥。
    for (final kind in ['rare', 'normal']) {
      migrated['${kind}OrderHighlightPalette'] = {
        jsonString(value, '${kind}TargetHighlightColor'),
        ..._legacyMigrationPalettes[kind]!,
      }.take(2).toList();
    }
    migrated['rareGuestParticipationModuleEnabled'] = false;
    migrated['managedRareGuestIds'] = <int>[];
    final sort = jsonMap(value['recommendationSortProfile']);
    _validateSort(sort, legacyVersion: legacyVersion);
    final nextSort = <String, Object?>{
      ...sort,
      'objectives': jsonObjects(sort['objectives'])
          .where((row) => row['key'] != 'cookerAvailable')
          .toList(),
    };
    // 只移除没有实际排序效果的旧目标，其他启用、方向和权重完整保留。
    if (nextSort['preset'] != 'custom' && !_matchesPreset(nextSort)) {
      nextSort['preset'] = 'custom';
    }
    migrated['recommendationSortProfile'] = nextSort;
    return CompanionProfile({...migrated, ...kindDefaults});
  }

  /// 线上固定预设必须完整匹配其定义；只有显式 custom 接受自由参数。
  /// legacy 只供一次性 checkpoint 迁移验证旧格式，不成为在线兼容分支。
  static void _validateSort(JsonMap sort, {int legacyVersion = schemaVersion}) {
    final keys = [...objectiveKeys, if (legacyVersion < 5) 'cookerAvailable'];
    if (sort.length != 2 || !sort.containsKey('objectives'))
      throw const FormatException('排序配置字段错误。');
    _choice(sort, 'preset', {
      'balanced',
      'resources',
      'profit',
      'simple',
      if (legacyVersion >= 4) 'custom',
    });
    final objectives = jsonObjects(sort['objectives']);
    if (objectives.length != keys.length ||
        objectives.map((item) => jsonString(item, 'key')).toSet().length !=
            keys.length) {
      throw const FormatException('排序目标必须完整且不得重复。');
    }
    for (final item in objectives) {
      if (item.length != 4 || !keys.contains(jsonString(item, 'key')))
        throw const FormatException('未知排序目标。');
      jsonBool(item, 'enabled');
      final weight = jsonInt(item, 'weight');
      if (weight < 0 || weight > 100) throw const FormatException('排序权重超出范围。');
      _choice(item, 'direction', {'asc', 'desc'});
    }

    if (legacyVersion >= 4 &&
        sort['preset'] != 'custom' &&
        !_matchesPreset(sort, legacyVersion: legacyVersion)) {
      throw const FormatException('固定评分预设不能修改参数，请使用自定义模式。');
    }
  }

  /// 比较完整目标值而非数组排列。这里只用于验证模式或迁移历史标识，不计算评分，
  /// 也不把自定义数值恰好相同的配置自动改回预设，避免远端读回后模式变化。
  static bool _matchesPreset(
    JsonMap sort, {
    int legacyVersion = schemaVersion,
  }) {
    final expected = {
      for (final row in jsonObjects(
        sortPreset(jsonString(sort, 'preset'))['objectives'],
      ))
        jsonString(row, 'key'): row,
    };
    if (legacyVersion < 5) {
      // v4 的固定预设已不可编辑；先按原九项目标核验，不能将损坏固定预设修成 custom。
      final weight = switch (jsonString(sort, 'preset')) {
        'profit' => 50,
        'simple' => 70,
        _ => 60,
      };
      expected['cookerAvailable'] = {
        'key': 'cookerAvailable',
        'enabled': true,
        'weight': weight,
        'direction': 'desc',
      };
    }
    return jsonObjects(sort['objectives']).every(
      (row) =>
          canonicalJson(row) == canonicalJson(expected[jsonString(row, 'key')]),
    );
  }

  static const integerRanges = <String, (int, int)>{
    'rareGuestParticipationAutoResumeThresholdPercent': (1, 100),
    'autoRareConcurrency': (1, 4),
    'autoNormalConcurrency': (1, 6),
    'autoMaxStepRetries': (1, 10),
    'autoMaxRollbacks': (0, 5),
    'recipeVariantLimitPerBase': (1, 8),
  };
  static const otherFields = [
    ...paletteFields,
    'managedRareGuestIds',
    'rareGuestParticipationAutoResumePosition',
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
  ];

  /// C# RecommendationEngine 的四组表单预设。这里只提供用户选择后的完整
  /// 配置值，不在客户端计算分数或重排候选；实际推荐结果始终由业务模块产生。
  /// 八项目标采用协议固定顺序，权重和方向决定排序，数组位置不是优先级。
  /// 隔离真实宿主测试逐组比较 C# 导出，避免两端默认值悄然漂移。
  static JsonMap sortPreset(String preset) {
    const weights = <String, List<int>>{
      'balanced': [70, 60, 90, 45, 55, 30, 35, 35],
      'resources': [55, 45, 90, 65, 100, 70, 20, 80],
      'profit': [60, 50, 85, 25, 35, 25, 100, 25],
      'simple': [50, 45, 90, 100, 45, 45, 25, 40],
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
    // 分类开关默认开启，但全局模块和具体辅助分项仍默认关闭，不隐式启用游戏操作。
    for (final key in gameUiKindFields) key: true,
    'filterMissingCookers': true,
    'missionRecipePriorityEnabled': true,
    'autoRareConcurrency': 2,
    'autoNormalConcurrency': 3,
    'autoMaxStepRetries': 3,
    'autoMaxRollbacks': 2,
    'recipeVariantLimitPerBase': 1,
    'rareOrderHighlightPalette': defaultRareHighlightPalette,
    'normalOrderHighlightPalette': defaultNormalHighlightPalette,
    'managedRareGuestIds': <int>[],
    ...autoResumeDefaults,
    'recommendationBudgetPolicy': 'block',
    'recommendationSortProfile': sortPreset('balanced'),
    'recommendationExclusions': {
      'excludedIngredientIds': <int>[],
      'excludedBeverageIds': <int>[],
    },
  });

  /// v10 新增项与名单同属共享配置。迁移默认关闭，保留旧手动暂停行为；
  /// 只接受整数百分比及明确队首/队尾选项，不在客户端执行阈值判定。
  static const autoResumeDefaults = <String, Object?>{
    'rareGuestParticipationAutoResumeEnabled': false,
    'rareGuestParticipationAutoResumeThresholdPercent': 30,
    'rareGuestParticipationAutoResumePosition': 'tail',
  };

  /// 新协议字段仅由已校验的历史版本迁移补齐，不接受线上缺省补值。
  static const newSchemaFields = {
    'rareGuestParticipationModuleEnabled',
    'managedRareGuestIds',
    ...paletteFields,
  };

  /// 仅供 v5/v6 历史记录严格校验与迁移，当前配置和在线补丁均不得包含这两个字段。
  static const legacyHighlightCountFields = [
    'rareHighlightOrderCount',
    'normalHighlightOrderCount',
  ];
  static const maxHighlightColors = 3;
  static const paletteFields = [
    'rareOrderHighlightPalette',
    'normalOrderHighlightPalette',
  ];

  /// 新设备每类默认两组，普客采用冷色、稀客采用暖色；第三组由同类快捷色补足。
  /// 同一组内的明度、色相均有区分，用户依然可以通过颜色选择器保存自定义值。
  static const defaultRareHighlightPalette = ['#F2CA5C', '#EF856D'];
  static const defaultNormalHighlightPalette = ['#5AA9E6', '#57C7B6'];

  /// 辅助分区各自使用八种同色系快捷色，前三色也是增加辅助数量时的补色顺序。
  static const rareHighlightPresets = [
    '#F2CA5C',
    '#EF856D',
    '#E785B4',
    '#EBA748',
    '#E55C65',
    '#F1B29F',
    '#F4DE96',
    '#CE7690',
  ];
  static const normalHighlightPresets = [
    '#5AA9E6',
    '#57C7B6',
    '#9A8FE8',
    '#6F87CF',
    '#6CCFE0',
    '#81BCA2',
    '#B4B0ED',
    '#4DA0A8',
  ];

  static List<String> highlightPresets(String kind) => switch (kind) {
    'rare' => rareHighlightPresets,
    'normal' => normalHighlightPresets,
    _ => throw ArgumentError.value(kind, 'kind', '未知订单类别'),
  };

  static const _formerDefaultPalettes = {
    'rare': ['#BA9CE8', '#FFFFFF', '#000000', '#F08C78'],
    'normal': ['#FFDB2E', '#5FACD3', '#F97373', '#7CCB8A'],
  };

  /// 仅schema7迁移识别旧默认整套及缩减前缀；自定义前三色与先后顺序完整保留。
  /// 颜色数量最多缩至三，不因更换产品默认把已有一组或两组自动扩充。
  static List<String> _migrateIndependentPalette(
    List<String> colors,
    String kind, {
    required bool replaceFormerDefaults,
  }) {
    final previous = _formerDefaultPalettes[kind]!;
    final isFormerDefault =
        replaceFormerDefaults &&
        List.generate(
          colors.length,
          (index) => index,
        ).every((index) => colors[index] == previous[index]);
    return (isFormerDefault ? highlightPresets(kind) : colors)
        .take(colors.length.clamp(1, maxHighlightColors))
        .toList();
  }

  /// 历史单色迁移固定沿用当时的补色规则；不能因新品默认调整而改变
  /// v1–v4 原有的两色结果、次序或辅助订单数量。
  static const _legacyMigrationPalettes = {
    'rare': ['#FFDB2E', '#F08C78'],
    'normal': ['#5FACD3', '#97C878'],
  };

  static List<String> defaultPalette(String kind) => switch (kind) {
    'rare' => defaultRareHighlightPalette,
    'normal' => defaultNormalHighlightPalette,
    _ => throw ArgumentError.value(kind, 'kind', '未知订单类别'),
  };

  /// 每类内部去重，跨类允许同色；历史八色仅由显式迁移入口请求不同的长度范围。
  static void _validatePalette(
    Object? value,
    String label, {
    int minimum = 1,
    required int maximum,
  }) {
    if (value is! List<Object?> ||
        value.length < minimum ||
        value.length > maximum ||
        value.any(
          (color) =>
              color is! String || !RegExp(r'^#[0-9A-F]{6}$').hasMatch(color),
        ) ||
        value.toSet().length != value.length) {
      throw FormatException('$label 必须包含 $minimum–$maximum 种不同的大写 #RRGGBB 颜色。');
    }
  }

  static void _validateIds(Object? value, int maximum, String label) {
    if (value is! List<Object?> || value.length > maximum) {
      throw FormatException('$label 列表无效。');
    }
    var previous = -1;
    for (final id in value) {
      if (id is! int || id < 0 || id <= previous) {
        throw FormatException('$label ID 必须为严格递增的非负整数。');
      }
      previous = id;
    }
  }

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
