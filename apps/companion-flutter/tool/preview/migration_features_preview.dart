part of 'rich_preview_workbench_model.dart';

/// 仅给独立预览追加一个可选场景；不改变旧夜间样本，也不运行 C# 推荐或原生动作。
/// 本文件中的固定队列是可操作的合成演示状态，不能用其排序结果证明生产调度正确。
extension _MigrationFeaturesPreview on RichPreviewWorkbenchModel {
  static const _workingTrace = 'R-1004';

  void _installMigrationFeatures() {
    profile = _copy(
      CompanionProfile({
        ...profile,
        'rareGuestParticipationModuleEnabled': true,
        'managedRareGuestIds': [301],
        'gameUiEnabled': true,
        for (final prefix in ['rare', 'normal']) ...{
          '${prefix}GameUiEnabled': true,
          '${prefix}GameUiPinningEnabled': true,
          '${prefix}CookerHighlightEnabled': true,
          '${prefix}SeatHighlightEnabled': true,
          '${prefix}OrderHighlightEnabled': true,
        },
      }).json,
    );
    authority = {
      ...authority,
      'profileSchemaVersion': CompanionProfile.schemaVersion,
      'primaryDeviceId': authority['currentDeviceId'],
    };
    final rare = <DataMap>[];
    final normal = <DataMap>[];
    for (var i = 0; i < 4; i++) {
      final guest = catalogItems('rareCustomers')[i < 2 ? 0 : i - 1];
      final row = _copy(_rareOrder(i));
      row['customer'] = _copy(guest);
      row['order'] = {
        ...object(row['order']),
        'traceId': 'R-${1001 + i}',
        'orderLifecycleSequence': _generation * 100 + i + 1,
        'guestId': guest['id'],
        'guestName': guest['name'],
        'runtimeGuestId': 800 + (i < 2 ? 0 : i - 1),
        'firstSeenAtUtc': '2026-10-28T00:00:0${i * 2}Z',
        'serviceOrderPosition': i * 2,
        'deskCode': i * 2,
        'specialBusinessRoleLabel': '',
        'automationAllowed': true,
        'automationBlockReason': '',
      };
      row['executionPlans'] = [i < 2 ? _plan(i) : _specialProductPlan(i == 2)];
      row['blockedMessages'] = <String>[];
      rare.add(row);
      final ordinary = _copy(_normalOrder(i));
      ordinary['order'] = {
        ...object(ordinary['order']),
        'traceId': 'N-${2001 + i}',
        'orderKey': 'ptr:${(0x2001 + i).toRadixString(16)}',
        'orderLifecycleSequence': _generation * 100 + i + 11,
        'firstSeenAtUtc': '2026-10-28T00:00:0${i * 2 + 1}Z',
        'serviceOrderPosition': i * 2 + 1,
        'deskCode': i * 2 + 1,
        'canAutomate': true,
        'actionBlockReason': '',
      };
      normal.add(ordinary);
    }
    snapshot = {
      ...snapshot,
      'nightBusinessAutomationAllowed': true,
      'nightBusiness': {
        ...night,
        'ordersReadComplete': true,
        'activeRareGuestsReadComplete': true,
        'orders': [for (final row in rare) row['order']],
        'activeRareGuests': [for (final row in rare) row['order']],
      },
      'normalBusiness': {
        'ordersReadComplete': true,
        'orders': [for (final row in normal) row['order']],
      },
      'specialBusiness': {'active': false, 'challengeType': 'not-challenge'},
      'recommendationState': {...runtime, 'storedDarkCuisineCount': 1},
      'rareGuestParticipation': {
        'active': true,
        'ready': true,
        'businessGeneration': _generation,
        'participationRevision': 1,
        'managedGuestIds': [301],
        'entries': [
          for (final row in rare)
            {
              ..._previewIdentity(object(row['order'])),
              'managed': object(row['order'])['guestId'] == 301,
              'participating': object(row['order'])['guestId'] != 301,
              'queuePosition': object(row['order'])['traceId'] == _workingTrace
                  ? 1
                  : object(row['order'])['guestId'] == 302
                  ? 2
                  : null,
              'reasonCode': object(row['order'])['guestId'] == 301
                  ? 'managed-default-paused'
                  : 'automatic-participation',
            },
        ],
      },
      // 一个明确的合成已开工订单用于“优先启用”的不抢占演示；无实际厨具引用。
      'automationCookingJobs': [
        {
          'jobId': 'synthetic-dark-cooking',
          'targetKind': 'rare',
          'traceId': _workingTrace,
        },
      ],
    };
    business = {
      ...business,
      'recommendations': {
        'recommendations': rare,
        'normalOrderDetailPlans': normal,
      },
    };
    _publishMigrationProjection();
  }

  /// 固定两种供给示例：stored 无生产配方、材料与厨具；cook 显示真实编号的合成基底。
  /// 禁忌标签是本夹具声明，不反射真实配方，不将特殊产物加入普通料理目录。
  DataMap _specialProductPlan(bool stored) {
    final production = _copy(catalogItems('recipes')[0]);
    production['negativeTags'] = ['肉'];
    final food = <String, Object?>{
      'productKind': 'dark-cuisine',
      'supplySource': stored ? 'stored' : 'cook',
      'productionRecipe': stored ? null : production,
      'recipe': {
        if (!stored) ...production,
        'id': -1,
        'recipeId': stored ? null : production['recipeId'],
        'name': '黑暗料理',
        'price': 1,
        'positiveTags': ['黑暗物质'],
        'negativeTags': <String>[],
        if (stored) 'ingredients': <String>[],
        if (stored) 'ingredientIds': <int>[],
        if (stored) 'cooker': '',
      },
      'extraIngredients': stored
          ? <DataMap>[]
          : [_copy(catalogItems('ingredients')[4])],
      'baseCost': stored ? 0 : 10,
      'extraCost': stored ? 0 : 8,
      'activeTags': ['黑暗物质'],
      'suppressedTags': <String>[],
      'meetsRequiredFood': true,
      'extraIngredientReasonTags': stored
          ? <String, Object?>{}
          : {
              '5': ['制作黑暗料理'],
            },
    };
    return {
      ..._plan(2),
      'food': food,
      'reasons': [stored ? '合成任务 · 使用保温箱成品' : '合成任务 · 基底配方与禁忌加料'],
      'warnings': <String>[],
    };
  }

  DataMap _previewIdentity(DataMap row) => {
    'businessGeneration': snapshot['nightBusinessGeneration'],
    'traceId': row['traceId'],
    'orderLifecycleSequence': row['orderLifecycleSequence'],
    'guestId': row['guestId'],
  };
  String _previewIdentityKey(DataMap row) => jsonEncode([
    row['businessGeneration'],
    row['traceId'],
    row['orderLifecycleSequence'],
    row['guestId'],
  ]);

  /// 模拟公开 CAS 请求，只接受本场景的完整合成身份。失败不改任何订单、修订或颜色。
  void _mutatePreviewParticipation(DataMap request) {
    final state = Map<String, Object?>.of(
      object(snapshot['rareGuestParticipation']),
    );
    final entries = records(state['entries']);
    final target = object(request['target']);
    final action = request['action'];
    void reject() {
      error = '合成队列或订单已变化，请刷新后重试。';
    }

    if (profile['rareGuestParticipationModuleEnabled'] != true ||
        state['ready'] != true ||
        request['expectedAuthorityRevision'] !=
            authority['authorityRevision'] ||
        request['expectedBusinessGeneration'] != state['businessGeneration'] ||
        request['expectedParticipationRevision'] !=
            state['participationRevision'] ||
        !const ['pause', 'enable-tail', 'enable-front'].contains(action)) {
      reject();
      return;
    }
    final candidates = target['type'] == 'guest'
        ? records(target['expectedCurrentOrders'])
        : target['type'] == 'order'
        ? [object(target['order'])]
        : <DataMap>[];
    final requested = candidates.map(_previewIdentityKey).toSet();
    final selected = entries
        .where((entry) => requested.contains(_previewIdentityKey(entry)))
        .toList();
    final allGuest = entries
        .where((entry) => entry['guestId'] == target['guestId'])
        .toList();
    if (selected.isEmpty ||
        selected.length != candidates.length ||
        requested.length != candidates.length ||
        selected.any((entry) => entry['managed'] != true) ||
        target['type'] == 'order' && selected.length != 1 ||
        target['type'] == 'guest' &&
            (allGuest.length != selected.length ||
                allGuest.any(
                  (entry) => !requested.contains(_previewIdentityKey(entry)),
                ))) {
      reject();
      return;
    }
    final queue =
        entries.where((entry) => entry['participating'] == true).toList()..sort(
          (left, right) => (left['queuePosition']! as int).compareTo(
            right['queuePosition']! as int,
          ),
        );
    var changed = false;
    if (action == 'pause') {
      changed = queue.any(
        (entry) => requested.contains(_previewIdentityKey(entry)),
      );
      queue.removeWhere(
        (entry) => requested.contains(_previewIdentityKey(entry)),
      );
    } else {
      final added = selected
          .where((entry) => entry['participating'] != true)
          .toList();
      changed = added.isNotEmpty;
      final anchor = action == 'enable-front'
          ? queue.indexWhere((entry) => entry['traceId'] == _workingTrace)
          : -1;
      queue.insertAll(
        action == 'enable-tail' ? queue.length : anchor + 1,
        added,
      );
    }
    if (changed) {
      state['participationRevision'] =
          (state['participationRevision']! as int) + 1;
      _setPreviewQueue(state, entries, queue);
    }
    _publishMigrationProjection();
  }

  void _setPreviewQueue(
    DataMap state,
    List<DataMap> entries,
    List<DataMap> queue,
  ) {
    final positions = {
      for (final (index, entry) in queue.indexed)
        _previewIdentityKey(entry): index + 1,
    };
    state['entries'] = [
      for (final entry in entries)
        {
          ...entry,
          'participating': positions.containsKey(_previewIdentityKey(entry)),
          'queuePosition': positions[_previewIdentityKey(entry)],
          'reasonCode': entry['managed'] != true
              ? 'automatic-participation'
              : positions.containsKey(_previewIdentityKey(entry))
              ? 'manually-enabled'
              : 'manually-paused',
        },
    ];
    snapshot = {...snapshot, 'rareGuestParticipation': state};
  }

  /// 共享配置仍经过正式当前版本 DTO。这里只在内存演示名单新增暂停、移出排尾及关闭恢复，
  /// 色槽固定在精确身份上，用户换色只修改对应颜色，不能因重排自动改色。
  void _syncMigrationProfile(DataMap previous) {
    final state = Map<String, Object?>.of(
      object(snapshot['rareGuestParticipation']),
    );
    final entries = records(state['entries']);
    final managed = profile['rareGuestParticipationModuleEnabled'] == true
        ? values(profile['managedRareGuestIds']).whereType<int>().toSet()
        : <int>{};
    final previousManaged =
        previous['rareGuestParticipationModuleEnabled'] == true
        ? values(previous['managedRareGuestIds']).whereType<int>().toSet()
        : <int>{};
    if (!managed.containsAll(previousManaged) ||
        !previousManaged.containsAll(managed)) {
      final updated = [
        for (final entry in entries)
          {...entry, 'managed': managed.contains(entry['guestId'])},
      ];
      final oldQueue =
          entries.where((entry) => entry['participating'] == true).toList()
            ..sort(
              (left, right) => (left['queuePosition']! as int).compareTo(
                right['queuePosition']! as int,
              ),
            );
      final queue = oldQueue
          .where(
            (entry) =>
                !managed.contains(entry['guestId']) ||
                previousManaged.contains(entry['guestId']),
          )
          .toList();
      final keys = queue.map(_previewIdentityKey).toSet();
      queue.addAll(
        updated.where(
          (entry) =>
              !managed.contains(entry['guestId']) &&
              !keys.contains(_previewIdentityKey(entry)),
        ),
      );
      state['managedGuestIds'] = managed.toList()..sort();
      state['participationRevision'] =
          (state['participationRevision']! as int) + 1;
      _setPreviewQueue(state, updated, queue);
    }
    _publishMigrationProjection();
  }

  void _publishMigrationProjection() {
    final state = Map<String, Object?>.of(
      object(snapshot['rareGuestParticipation']),
    );
    final entries = records(state['entries']);
    final queue =
        entries.where((entry) => entry['participating'] == true).toList()..sort(
          (left, right) => (left['queuePosition']! as int).compareTo(
            right['queuePosition']! as int,
          ),
        );
    state['displayOrderKeys'] = values(state['managedGuestIds']).isEmpty
        ? null
        : [for (final entry in queue) _previewIdentity(entry)];
    snapshot = {...snapshot, 'rareGuestParticipation': state};
    final palettes = {
      for (final kind in ['rare', 'normal'])
        kind: values(profile['${kind}OrderHighlightPalette']),
    };
    final rareByTrace = {
      for (final order in records(night['orders'])) order['traceId']: order,
    };
    // 预览只给参与队列中前若干笔分色；暂停项和超出颜色数的订单继续显示，但不占颜色。
    // 这里只维护合成界面状态，不代替生产 C# 对方案就绪、身份完整性或实际执行许可的判断。
    final rows = [
      for (final entry in queue.take(palettes['rare']!.length))
        if (rareByTrace[entry['traceId']] case final order?)
          (kind: 'rare', order: order),
      for (final order in records(
        object(snapshot['normalBusiness'])['orders'],
      ).take(palettes['normal']!.length))
        (kind: 'normal', order: order),
    ];
    final previousColors = records(
      object(business['gameUiTargets'])['orderColors'],
    );
    final slots = <String, int>{};
    String key(String kind, DataMap row) => '$kind:${row['traceId']}';
    // 每类独立四个声明槽，先保留仍入选且未越界的旧序号，再给新增项补空位。
    // 缩减颜色立即移出越界声明；另一类别的原生槽位与色值保持不变。
    for (final row in rows) {
      final base = row.kind == 'rare' ? 0 : 4;
      final previous = previousColors
          .where(
            (item) =>
                item['kind'] == row.kind &&
                item['traceId'] == row.order['traceId'],
          )
          .firstOrNull;
      final slot = previous?['colorSlot'];
      if (slot is int &&
          slot >= base &&
          slot < base + palettes[row.kind]!.length &&
          !slots.containsValue(slot)) {
        slots[key(row.kind, row.order)] = slot;
      }
    }
    for (final row in rows) {
      final base = row.kind == 'rare' ? 0 : 4;
      slots.putIfAbsent(
        key(row.kind, row.order),
        () => List.generate(
          palettes[row.kind]!.length,
          (index) => base + index,
        ).firstWhere((slot) => !slots.containsValue(slot)),
      );
    }
    final colors = [
      for (final row in rows)
        {
          'kind': row.kind,
          ..._previewIdentity(row.order),
          'orderKey': row.kind == 'normal' ? row.order['orderKey'] : '',
          'colorSlot': slots[key(row.kind, row.order)],
          'color':
              palettes[row.kind]![slots[key(row.kind, row.order)]! -
                  (row.kind == 'rare' ? 0 : 4)],
        },
    ];
    final gameUi = profile['gameUiEnabled'] == true;
    business = {
      ...business,
      'gameUiTargets': {
        'orderColors': colors,
        'rare': gameUi && profile['rareGameUiEnabled'] == true
            ? colors.where((row) => row['kind'] == 'rare').toList()
            : <DataMap>[],
        'normal': gameUi && profile['normalGameUiEnabled'] == true
            ? colors.where((row) => row['kind'] == 'normal').toList()
            : <DataMap>[],
      },
      'automation': {
        ...automation,
        'message': '纯合成调度与供给示例，不发送任何游戏操作。',
        'rareDiagnostics': [
          for (final entry in entries)
            {
              'kind': 'rare',
              'orderKey': entry['traceId'],
              'stateKey': 'rare:${entry['traceId']}',
              'traceId': entry['traceId'],
              'orderLifecycleSequence': entry['orderLifecycleSequence'],
              'guestName': itemName('rareCustomers', entry['guestId']),
              'guestImageKind': 'rare-guest',
              'guestImageId': entry['guestId'],
              'step': entry['participating'] == true
                  ? 'ensure-cooking'
                  : 'paused',
              'detailMessage': entry['traceId'] == _workingTrace
                  ? '合成已开工：基底配方与禁忌加料制作黑暗料理。'
                  : entry['traceId'] == 'R-1003'
                  ? '合成供给：使用保温箱黑暗料理，不占厨具。'
                  : entry['participating'] == true
                  ? '合成订单已进入队列。'
                  : '合成订单已暂停，可从稀客调度恢复。',
              'canReset': false,
              'resetBlockReason': '请从稀客调度操作此合成订单。',
            },
        ],
        'normalDiagnostics': <DataMap>[],
      },
    };
  }
}
