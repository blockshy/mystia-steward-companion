import 'dart:convert';

import 'package:companion_protocol/companion_protocol.dart';

/// 将最后成功的 C# 方案附到最新现场订单，供经营列表和专注页只读展示。
///
/// 订单的增删、送达、评价和客人身份只来自 snapshot；旧计算结果不能决定哪些订单仍存在。
/// contextMatches 必须由会话边界核验，不能由页面把 pending 解释为执行许可。返回值不参与
/// 自动化、租约、目标绑定或高亮计算，也不从原点料理构造执行方案。
JsonMap projectLiveServiceOrders(
  JsonMap snapshot,
  JsonMap recommendations, {
  required bool contextMatches,
  bool current = false,
}) => {
  'rareSnapshotAvailable': _available(snapshot['nightBusiness']),
  'normalSnapshotAvailable': _available(snapshot['normalBusiness']),
  'recommendations': _project(
    _available(snapshot['nightBusiness'])
        ? _rarePresentationOrders(snapshot)
        : null,
    [
      ..._records(recommendations['recommendations']),
      for (final issue in _records(recommendations['recommendationIssues']))
        {...issue, 'presentationIssue': issue['message']},
    ],
    normal: false,
    contextMatches: contextMatches,
    current: current,
    pausedRareKeys: _pausedRarePresentationKeys(snapshot),
  ),
  'normalOrderDetailPlans': _project(
    _available(snapshot['normalBusiness'])
        ? _map(snapshot['normalBusiness'])['orders']
        : null,
    _records(recommendations['normalOrderDetailPlans']),
    normal: true,
    contextMatches: contextMatches,
    current: current,
  ),
};

/// C# 发布的完整身份列表只决定显示顺序，不决定现场订单是否存在。
/// 队列缺项、过期或尚未就绪时保留最新现场事实与原时间顺序；这种只读降级
/// 不会授予自动化、收藏写入或高亮资格，执行侧仍独立验证严格参与许可。
Object? _rarePresentationOrders(JsonMap snapshot) {
  final orders = _records(_map(snapshot['nightBusiness'])['orders']);
  final participation = _map(snapshot['rareGuestParticipation']);
  final keys = participation['displayOrderKeys'];
  if (keys == null) return orders;
  if (keys is! List ||
      participation['active'] != true ||
      participation['ready'] != true ||
      keys.length != orders.length)
    return orders;
  final generation = snapshot['nightBusinessGeneration'];
  if (generation is! int ||
      generation <= 0 ||
      participation['businessGeneration'] != generation)
    return orders;

  final byKey = <String, JsonMap>{};
  for (final order in orders) {
    final identity = _rarePresentationKey(order);
    if (identity == null || byKey.containsKey(identity)) return orders;
    byKey[identity] = order;
  }
  final result = <JsonMap>[];
  final used = <String>{};
  for (final value in keys) {
    if (value is! Map<String, Object?> ||
        value['businessGeneration'] != generation)
      return orders;
    final identity = _rarePresentationKey(value);
    if (identity == null || !used.add(identity) || !byKey.containsKey(identity))
      return orders;
    result.add(byKey[identity]!);
  }
  // 专注页沿用跨普/稀客的既有显示槽位；只把 C# 已排序的全部现场订单依次放入
  // 这些稀客槽位。普客位置与原始 snapshot 均不变，不在 Dart 重新推导队列优先级。
  final slots = [
    for (final order in result)
      if (order['serviceOrderPosition'] is int)
        order['serviceOrderPosition']! as int,
  ]..sort();
  if (slots.length == result.length && slots.toSet().length == slots.length) {
    return [
      for (var i = 0; i < result.length; i++)
        {...result[i], 'serviceOrderPosition': slots[i]},
    ];
  }
  return result;
}

/// 调度暂停只是标题的只读状态。只接纳同经营代次、完整且无歧义的精确身份集合，
/// 不用 guestId 单独关联，防止同一稀客的多个桌位/生命周期互相覆盖状态。
/// 未知或过期状态不猜成暂停；此集合不改变 order 原对象，也不参与方案需求匹配。
Set<String> _pausedRarePresentationKeys(JsonMap snapshot) {
  final state = _map(snapshot['rareGuestParticipation']);
  final generation = snapshot['nightBusinessGeneration'];
  final entries = state['entries'];
  final orders = _records(_map(snapshot['nightBusiness'])['orders']);
  if (state['active'] != true ||
      state['ready'] != true ||
      generation is! int ||
      generation <= 0 ||
      state['businessGeneration'] != generation ||
      entries is! List ||
      entries.length != orders.length)
    return const {};
  final current = <String>{};
  for (final order in orders) {
    final key = _rarePresentationKey(order);
    if (key == null || !current.add(key)) return const {};
  }
  final seen = <String>{};
  final paused = <String>{};
  for (final value in entries) {
    if (value is! JsonMap) return const {};
    final key = _rarePresentationKey(value);
    if (key == null ||
        !current.contains(key) ||
        !seen.add(key) ||
        value['managed'] is! bool ||
        value['participating'] is! bool)
      return const {};
    if (value['managed'] == true && value['participating'] == false)
      paused.add(key);
  }
  return paused;
}

String? _rarePresentationKey(JsonMap value) {
  final trace = value['traceId'];
  final life = value['orderLifecycleSequence'];
  final guest = value['guestId'];
  if (trace is! String ||
      trace.isEmpty ||
      life is! int ||
      life <= 0 ||
      guest is! int ||
      guest < 0)
    return null;
  return jsonEncode([trace, life, guest]);
}

bool _available(Object? value) {
  final context = _map(value);
  return context['orders'] is List &&
      (context['error'] == null || context['error'] == '');
}

List<JsonMap> _project(
  Object? input,
  List<JsonMap> previous, {
  required bool normal,
  required bool contextMatches,
  required bool current,
  Set<String> pausedRareKeys = const {},
}) {
  final live = _records(input);
  final liveCounts = <String, int>{};
  final previousByKey = <String, List<JsonMap>>{};
  final displayOccurrences = <String, int>{};
  for (final order in live) {
    final key = _identity(order, normal: normal);
    if (key != null)
      liveCounts.update(key, (count) => count + 1, ifAbsent: () => 1);
  }
  if (contextMatches) {
    for (final row in previous) {
      final key = _identity(_map(row['order']), normal: normal);
      if (key != null) (previousByKey[key] ??= []).add(row);
    }
  }
  return [
    for (final order in live)
      (() {
        final key = _identity(order, normal: normal);
        // 专用 Widget 身份不参与业务联结或动作：正常订单不因前一行移除而换 key；
        // 同一原始身份重复时仅用出现次数区分视图，仍拒绝给其中任一行附加方案。
        final displayIdentity = jsonEncode([
          normal ? 'normal' : 'rare',
          order['traceId'],
          order['orderLifecycleSequence'],
          if (normal) order['orderKey'],
        ]);
        final occurrence = displayOccurrences.update(
          displayIdentity,
          (value) => value + 1,
          ifAbsent: () => 0,
        );
        final matches = key == null ? null : previousByKey[key];
        final matched =
            key != null &&
            liveCounts[key] == 1 &&
            matches?.length == 1 &&
            _demand(order) == _demand(_map(matches!.single['order']));
        return <String, Object?>{
          if (matched) ...matches.single,
          // 即使方案匹配也替换为当前事实，绝不把旧 order 对象保留在屏幕上。
          'order': Map<String, Object?>.unmodifiable(order),
          'presentationKey': '$displayIdentity#$occurrence',
          // 明确覆盖旧展示行的衍生值；恢复参与后不能沿用上一帧的暂停徽标。
          'presentationSchedulingPaused':
              !normal && pausedRareKeys.contains(_rarePresentationKey(order)),
          'presentationPending': !matched,
          'presentationStale': matched && !current,
          if (!matched && normal) ...{
            // 这只是原点单的名称与 ID，不补目录、不推导配方、不伪造 executionFood。
            'originalFood': {
              'foodId': order['foodId'],
              'name': order['foodName'],
            },
            'originalBeverage': {
              'beverageId': order['beverageId'],
              'name': order['beverageName'],
            },
          },
        };
      })(),
  ];
}

/// 生命周期必须确切且双方唯一。桌号、名称、原点料理相同都不能替代这一身份；
/// 普客还需原生订单键，防止同桌换客或 native 地址重用时借用上一单方案。
String? _identity(JsonMap order, {required bool normal}) {
  final trace = order['traceId'];
  final sequence = order['orderLifecycleSequence'];
  final key = order['orderKey'];
  if (trace is! String ||
      trace.isEmpty ||
      sequence is! int ||
      sequence <= 0 ||
      (normal && (key is! String || key.isEmpty)))
    return null;
  return jsonEncode([trace, sequence, if (normal) key]);
}

/// 比较全部订单纯值而非名称猜测；忽略采集来源、最后观测时间及 HTTP 展示顺位。
/// serviceOrderPosition 与 patienceRemainingRatio 是展示/调度采样，不属于料理需求；
/// 顺位移动或耐心条自然减少不能撤掉已生成方案，更不能让卡片持续回到加载占位。
/// 送达、免费、预算、偏好、任务、特殊角色及未来新增需求字段有变化时均等待新结果。
/// 递归按键排序避免 JSON 属性顺序差异造成假失配，不改输入树或共享结果中的任何字段。
String _demand(JsonMap order) => jsonEncode(
  _canonical({
    for (final entry in order.entries)
      if (entry.key != 'lastSeenAtUtc' &&
          entry.key != 'source' &&
          entry.key != 'serviceOrderPosition' &&
          entry.key != 'patienceRemainingRatio')
        entry.key: entry.value,
  }),
);

Object? _canonical(Object? value) {
  if (value is Map) {
    final map = value.cast<String, Object?>();
    final keys = map.keys.toList()..sort();
    return {for (final key in keys) key: _canonical(map[key])};
  }
  if (value is List) return [for (final entry in value) _canonical(entry)];
  return value;
}

List<JsonMap> _records(Object? value) => value is List
    ? [
        for (final item in value)
          if (item is Map) item.cast<String, Object?>(),
      ]
    : const [];

// 线路 DTO 已经递归冻结。展示联结只读现有节点，不再次调用 jsonMap 深复制整份方案树。
JsonMap _map(Object? value) => value is Map<String, Object?> ? value : const {};
