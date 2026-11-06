import 'dart:convert';

import 'package:companion_client/companion_client.dart';

/// 直接调用生产纯投影，不依赖 Flutter、游戏或计时；逐项锁定现场事实与旧方案边界。
int validateOrderPresentation() {
  var checks = 0;
  void check(bool value, String message) {
    checks++;
    if (!value) throw StateError(message);
  }

  JsonMap order(String trace, {bool normal = false}) => {
    'traceId': trace,
    'orderLifecycleSequence': 1,
    'deskCode': 0,
    'guestId': 3,
    'runtimeGuestId': 33,
    'guestName': '合成客人',
    'hasServedFood': false,
    'hasServedBeverage': false,
    'isFreeOrder': false,
    'fund': 100,
    'firstSeenAtUtc': '2026-10-12T00:00:00Z',
    if (normal) ...{
      'orderKey': 'ptr:123',
      'foodId': 1,
      'foodName': '原点料理',
      'beverageId': 2,
      'beverageName': '原点酒水',
    } else ...{
      'foodTagId': 1,
      'foodTag': '鲜',
      'beverageTagId': 2,
      'beverageTag': '甜',
    },
  };
  for (final normal in [false, true]) {
    final key = normal ? 'normalOrderDetailPlans' : 'recommendations';
    final section = normal ? 'normalBusiness' : 'nightBusiness';
    final original = order(normal ? 'N-1' : 'R-1', normal: normal);
    final previous = {
      key: [
        {
          'order': original,
          'executionPlans': [
            {'marker': '旧方案'},
          ],
        },
      ],
    };
    List<JsonMap> project(
      List<JsonMap> live, {
      JsonMap? result,
      bool context = true,
    }) =>
        (projectLiveServiceOrders(
                  {
                    section: {'orders': live},
                  },
                  result ?? previous,
                  contextMatches: context,
                )[key]
                as List)
            .cast<JsonMap>();
    final before = jsonEncode(previous);
    final same = project([original]).single;
    check(
      same['presentationPending'] == false && same['presentationStale'] == true,
      '$key 同身份同需求的只读旧结果应可展示并标参考。',
    );
    // HTTP 会独立加入跨普/稀客显示顺位，旧业务结果不含该字段。
    // 此值只供专注合并，位置移动既不能撤掉匹配方案，也不能弱化真实需求检查。
    for (final position in [0, 5]) {
      final positioned = project([
        {...original, 'serviceOrderPosition': position},
      ]).single;
      check(
        positioned['presentationPending'] == false &&
            jsonMap(positioned['order'])['serviceOrderPosition'] == position &&
            (positioned['executionPlans'] as List).isNotEmpty,
        '$key 仅显示顺位新增/变化必须继续附加旧结果并保留最新顺位。',
      );
    }
    check(
      project([
            {...original, 'serviceOrderPosition': 2, 'fund': 1},
          ]).single['presentationPending'] ==
          true,
      '$key 显示顺位变化不能掩盖预算需求变化。',
    );
    // 等待条只描述当前耐心，既不是料理需求也不是方案身份。自然扣减和暂不可读
    // 都保留已匹配配方，同时把最新比例传给调度页面，避免周期性加载占位跳动。
    for (final ratio in <double?>[1, .3, .01, 0, null]) {
      final sampled = project([
        {...original, 'patienceRemainingRatio': ratio},
      ]).single;
      check(
        sampled['presentationPending'] == false &&
            jsonMap(sampled['order'])['patienceRemainingRatio'] == ratio,
        '$key 耐心变化或未知不得撤掉主方案，且应保留当前比例。',
      );
    }
    check(project([]).isEmpty, '$key 已消失订单不能被缓存结果复活。');
    final fresh = {...original, 'traceId': 'new', 'orderLifecycleSequence': 2};
    final joined = project([original, fresh]);
    check(
      joined.length == 2 &&
          joined.last['presentationPending'] == true &&
          !joined.last.containsKey('executionPlans'),
      '$key 新单必须先显示事实且没有伪造方案。',
    );
    check(
      project([original], context: false).single['presentationPending'] == true,
      '$key 跨会话或配置边界不得借用结果。',
    );
    for (final change in <JsonMap>[
      {'orderLifecycleSequence': 2},
      {'traceId': 'replacement'},
      {'deskCode': 1},
      {'guestId': 4},
      {'runtimeGuestId': 44},
      {'hasServedFood': true},
      {'hasServedBeverage': true},
      {'fund': 20},
      {'isFreeOrder': true},
      {'specialBusinessRole': 'changed'},
      {
        'missionRecipePriority': {'recipeId': 8},
      },
      if (normal) ...[
        {'orderKey': 'ptr:124'},
        {'foodId': 2},
        {'beverageId': 3},
        {'hasEvaluated': true},
        {
          'foodPreferenceTags': ['肉'],
        },
      ] else ...[
        {'foodTagId': 4},
        {'beverageTagId': 4},
        {'foodTag': '肉'},
        {'beverageTag': '苦'},
      ],
    ]) {
      final updated = {...original, ...change};
      final row = project([updated]).single;
      check(
        row['presentationPending'] == true &&
            jsonEncode(row['order']) == jsonEncode(updated),
        '$key ${change.keys.single} 变化必须显示最新事实并撤下旧方案。',
      );
    }
    check(
      project([
            {...original, 'source': 'new', 'lastSeenAtUtc': 'later'},
          ]).single['presentationPending'] ==
          false,
      '$key 纯观测时间变化不应移除只读方案。',
    );
    check(
      project([original, original])
          .every((row) => row['presentationPending'] == true),
      '$key 当前身份重复不能附加任何候选。',
    );
    check(
      project([original, original])
              .map((row) => row['presentationKey'])
              .toSet()
              .length ==
          2,
      '$key 重复业务身份也必须具有不同的只读视图 key。',
    );
    check(
      project([fresh, original]).last['presentationKey'] ==
          project([original]).single['presentationKey'],
      '$key 前项消失不应改变剩余订单的视图身份。',
    );
    check(
      project(
            [original],
            result: {
              key: [
                {'order': original},
                {'order': original},
              ],
            },
          ).single['presentationPending'] ==
          true,
      '$key 旧结果重复不能取首项。',
    );
    for (final invalid in <JsonMap>[
      {...original}..remove('traceId'),
      {...original, 'orderLifecycleSequence': -1},
      if (normal) {...original}..remove('orderKey'),
    ]) {
      check(
        project([invalid]).single['presentationPending'] == true,
        '$key 缺失强身份不能按名称或桌号猜测。',
      );
    }
    check(jsonEncode(previous) == before, '$key 不能修改共享输入树。');
    if (normal) {
      final row = project([fresh]).single;
      check(
        jsonMap(row['originalFood'])['name'] == '原点料理' &&
            !row.containsKey('executionFood'),
        '普客等待方案时原点单与执行方案必须分离。',
      );
    }
  }
  final rare = order('R-issue');
  final issue = projectLiveServiceOrders(
    {
      'nightBusiness': {
        'orders': [rare],
      },
    },
    {
      'recommendationIssues': [
        {'order': rare, 'message': '缺少点单标签'},
      ],
    },
    contextMatches: true,
    current: true,
  );
  check(
    jsonMap((issue['recommendations'] as List).single)['presentationIssue'] ==
        '缺少点单标签',
    '已完成的业务问题应保留明确原因，不能误称正在计算。',
  );
  check(
    projectLiveServiceOrders(
          {},
          {},
          contextMatches: false,
        )['rareSnapshotAvailable'] ==
        false,
    '尚未获得订单集合不能宣称当前没有订单。',
  );
  for (final section in ['nightBusiness', 'normalBusiness']) {
    final invalid = projectLiveServiceOrders(
      {
        section: {
          'orders': [rare],
          'error': '读取失败',
        },
      },
      {},
      contextMatches: false,
    );
    check(
      (invalid[section == 'nightBusiness'
                  ? 'recommendations'
                  : 'normalOrderDetailPlans']
              as List)
          .isEmpty,
      '$section 快照读取失败时残留订单不能作为当前现场事实。',
    );
  }
  // 服务端决定只读全量显示次序；暂停不删订单，已有跨类显示槽位仍按精确身份映射。
  final scheduled = [
    {...order('R-1'), 'serviceOrderPosition': 0},
    {...order('R-2'), 'serviceOrderPosition': 2},
    {...order('R-3'), 'serviceOrderPosition': 4},
  ];
  JsonMap identity(String trace) => {
    'businessGeneration': 9,
    'traceId': trace,
    'orderLifecycleSequence': 1,
    'guestId': 3,
  };
  JsonMap schedulingSnapshot(Object? keys, {bool ready = true}) => {
    'nightBusinessGeneration': 9,
    'nightBusiness': {'orders': scheduled},
    'normalBusiness': {
      'orders': [
        {...order('N-1', normal: true), 'serviceOrderPosition': 1},
      ],
    },
    'rareGuestParticipation': {
      'active': true,
      'businessGeneration': 9,
      'ready': ready,
      'displayOrderKeys': keys,
    },
  };
  final scheduledSnapshot = schedulingSnapshot([
    identity('R-3'),
    identity('R-1'),
    identity('R-2'),
  ]);
  final unchanged = jsonEncode(scheduledSnapshot);
  final projected = projectLiveServiceOrders(
    scheduledSnapshot,
    {},
    contextMatches: false,
  );
  final participating = (projected['recommendations'] as List).cast<JsonMap>();
  check(
    jsonMap(participating[0]['order'])['traceId'] == 'R-3' &&
        jsonMap(participating[1]['order'])['traceId'] == 'R-1' &&
        jsonMap(participating[2]['order'])['traceId'] == 'R-2',
    '稀客列表必须采用完整服务端显示次序并保留暂停订单。',
  );
  check(
    jsonMap(participating[0]['order'])['serviceOrderPosition'] == 0 &&
        jsonMap(participating[1]['order'])['serviceOrderPosition'] == 2 &&
        jsonMap(participating[2]['order'])['serviceOrderPosition'] == 4,
    '专注页只复用全部稀客原槽位，不按原点单时间打乱完整显示队列。',
  );
  check(
    jsonMap((projected['normalOrderDetailPlans'] as List).single)['order']
            is Map &&
        jsonMap(
              jsonMap(
                (projected['normalOrderDetailPlans'] as List).single,
              )['order'],
            )['serviceOrderPosition'] ==
            1,
    '稀客调度不改变普客位置。',
  );
  check(jsonEncode(scheduledSnapshot) == unchanged, '调度显示投影不能修改原快照。');
  for (final keys in [
    <JsonMap>[],
    [identity('R-3'), identity('R-1')],
    [identity('R-3'), identity('R-3')],
    [identity('R-9')],
    [
      {...identity('R-1'), 'businessGeneration': 8},
    ],
    [
      {...identity('R-1'), 'guestId': 4},
    ],
  ]) {
    check(
      (projectLiveServiceOrders(
                    schedulingSnapshot(keys),
                    {},
                    contextMatches: false,
                  )['recommendations']
                  as List)
              .length ==
          3,
      '缺项、重复、未知、跨经营或客人不匹配的排序不能隐藏现场订单。',
    );
  }
  check(
    (projectLiveServiceOrders(
                  schedulingSnapshot([identity('R-1')], ready: false),
                  {},
                  contextMatches: false,
                )['recommendations']
                as List)
            .length ==
        3,
    '调度状态尚未就绪仅放弃排序，不隐藏真实订单或授予执行许可。',
  );
  check(
    (projectLiveServiceOrders(
                  schedulingSnapshot(null),
                  {},
                  contextMatches: false,
                )['recommendations']
                as List)
            .length ==
        3,
    '模块停用或空名单的 null 展示队列保留原有全部订单。',
  );
  return checks;
}
