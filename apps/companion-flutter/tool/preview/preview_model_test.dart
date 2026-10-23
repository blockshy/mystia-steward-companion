import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';

import 'rich_preview_workbench_model.dart';

/// 此测试只保护独立预览的可交互性与隔离边界，不证明真实 Mod 行为。
/// 所有输入均为合成数据；没有生产会话、HTTP、平台初始化或持久化依赖。
void main() {
  test('连接管理与诊断包仅改变合成内存，操作历史不含Token且读取不新增记录', () async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    final identity = model.authority['currentDeviceId'];
    await model.refreshDiagnostics();
    expect(model.resources['/logs/settings'], isNotNull);
    expect(records(model.resources['/logs/packages']!['items']), hasLength(2));
    expect(model.recentOperations, isEmpty);
    await model.updateLanConnection(
      enabled: true,
      host: '192.168.2.50',
      token: 'synthetic-changed-token',
    );
    expect(model.editableConnectionToken, 'synthetic-changed-token');
    expect(model.authority['currentDeviceId'], identity);
    expect(
      records(model.resources['/local-api/config']!['lanEndpoints'])
          .single['address'],
      '192.168.2.50',
    );
    expect(model.recentOperations.single.name, '保存局域网连接');
    expect(
      model.recentOperations.single.summary,
      isNot(contains('synthetic-changed-token')),
    );
    await model.mutate(
      '/logs/packages/delete',
      body: {'id': 'synthetic-package-1'},
    );
    expect(records(model.resources['/logs/packages']!['items']), hasLength(1));
    expect(model.recentOperations.first.name, '删除诊断包');
    await model.mutate(
      '/logs/packages/delete',
      body: {'id': 'synthetic-package-1'},
    );
    expect(
      model.recentOperations.first.result,
      WorkbenchOperationResult.failed,
    );
    await model.mutate('/logs/export-diagnostics');
    expect(records(model.resources['/logs/packages']!['items']), hasLength(2));
    model.reset();
    expect(model.recentOperations, isEmpty);
    expect(model.editableConnectionToken, 'preview-only-token');
  });

  test('图鉴预览保留双编号配方和独立酒水收录，日间可检验未解锁筛选', () {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    expect(model.catalogItems('recipes'), hasLength(8));
    expect(model.catalogItems('recipes').first['name'], '海鲜味噌汤');
    expect(model.catalogItems('recipes').first['id'], 101);
    expect(model.catalogItems('recipes').first['recipeId'], 1001);
    expect(model.catalogItems('recipes').last['ingredientIds'], [2, 2, 3]);
    expect(model.runtime['beverageUnlockReadReady'], isTrue);
    expect(model.runtime['unlockedBeverageIds'], [201, 203, 206]);
    expect(
      model.runtime['availableBeverageIds'],
      isNot(model.runtime['unlockedBeverageIds']),
    );
    model.selectScenario('day');
    expect(model.runtime['inventoryReadReady'], isTrue);
    expect(model.runtime['availableRecipeIds'], [101, 102, 103, 104, 105, 106]);
    expect(model.catalogItems('recipes'), hasLength(8));
    expect(model.runtime['placedCookerStatus'], 'not in night business scene');
    expect(model.runtime['placedCookerSnapshotComplete'], isFalse);
    expect(model.runtime['placedCookers'], isEmpty);
  });

  test('新单方案到达保留同一快照和身份，待加载不借用合成旧方案', () {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    model.selectScenario('orders-pending');
    final snapshot = model.snapshot;
    final connectionVersion = model.connectionVersion;
    final pending = records(model.serviceOrders['recommendations']);
    final pendingNormal = records(
      model.serviceOrders['normalOrderDetailPlans'],
    );
    expect(pending, hasLength(8));
    expect(pendingNormal, hasLength(8));
    expect(pending.every((row) => row['presentationPending'] == true), isTrue);
    expect(pending.every((row) => row['executionPlans'] == null), isTrue);
    expect(object(pendingNormal.first['originalFood'])['foodId'], isNotNull);
    model.selectScenario('orders-ready');
    final ready = records(model.serviceOrders['recommendations']);
    expect(identical(model.snapshot, snapshot), isTrue);
    expect(model.connectionVersion, connectionVersion);
    expect(ready.first['presentationKey'], pending.first['presentationKey']);
    expect(ready.every((row) => row['presentationPending'] == false), isTrue);
    expect(records(ready.first['executionPlans']), isNotEmpty);
    expect(model.business['pending'], isFalse);
  });

  test('自动化预览目标与图片身份来自同桌合成方案，复制稀客仍属于普客业务', () {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    final rare = records(model.automation['rareDiagnostics']);
    final normal = records(model.automation['normalDiagnostics']);
    final normalOrders = records(
      model.recommendations['normalOrderDetailPlans'],
    );
    for (final row in [...rare, ...normal]) {
      final kind = row['guestImageKind'];
      final id = row['guestImageId'];
      expect(kind, anyOf('rare-guest', 'normal-group'));
      expect(id, isA<int>());
      expect(model.gameMediaImage(kind as String, id as int), isNotNull);
      if (row['targetSource'] == '') {
        expect(row['foodId'], isNull);
        expect(row['recipeId'], isNull);
        expect(row['extraIngredientIds'], isNull);
      } else {
        expect(
          model.gameMediaRecipes['${row['foodId']}:${row['recipeId']}'],
          isNotNull,
        );
        expect(row['extraIngredientIds'], isA<List<Object?>>());
        expect(model.gameMediaImage('food', row['foodId'] as int), isNotNull);
        expect(
          model.gameMediaImage('beverage', row['beverageId'] as int),
          isNotNull,
        );
      }
    }
    final cloneOrder = object(normalOrders[2]['order']);
    final cloneDiagnostic = normal[2];
    expect(cloneOrder['guestImageKind'], 'rare-guest');
    expect(cloneOrder['guestImageId'], 302);
    expect(cloneOrder['guestId'], isNot(302));
    expect(cloneDiagnostic['kind'], 'normal');
    expect(cloneDiagnostic['guestImageKind'], 'rare-guest');
    expect(cloneDiagnostic['guestImageId'], 302);
    expect(cloneDiagnostic['specialBusinessRole'], '');
    expect(cloneDiagnostic['targetSource'], 'ordered-recipe');
    expect(
      rare.any((row) => row['traceId'] == cloneDiagnostic['traceId']),
      isFalse,
    );
    expect(
      records(object(model.automation['resourceOverview'])['cookers'])
          .map((row) => row['cookerType']),
      [1, 4, 2],
    );
  });

  test('预览导航只发布允许的内存事件，不调用宿主或更改游戏数据', () {
    var exits = 0;
    final model = RichPreviewWorkbenchModel(
      onExit: () async {
        exits++;
      },
    );
    addTearDown(model.dispose);
    final profile = model.profile;
    final business = model.business;
    final sequence = model.inputSequence;
    model.previewGamepadNavigation('nextSubTab');
    expect(model.inputSequence, sequence + 1);
    expect(model.inputAction, 'nextSubTab');
    expect(identical(model.profile, profile), isTrue);
    expect(identical(model.business, business), isTrue);
    expect(exits, 0);
    expect(() => model.previewGamepadNavigation('exit'), throwsArgumentError);
    expect(model.inputSequence, sequence + 1);
  });
  test('推荐等待状态随合成场景恢复，不需要真实请求或计时器', () async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    model.selectScenario('loading');
    await model.queryRecommendations(kind: 'normal', selectedPlace: '妖怪兽道');
    expect(model.recommendationLoading, isTrue);
    model.selectScenario('night');
    expect(model.recommendationLoading, isFalse);
    model.selectScenario('offline');
    expect(model.recommendationLoading, isFalse);
  });

  test('合成推荐能够加入并移除收藏，重置恢复预览初始数据', () async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    await model.queryRecommendations(kind: 'rare', customerId: 301);
    final candidate = records(object(model.pageResult['result'])['recipes'])
        .first;
    final count = records(model.resources['/favorites']!['recipes']).length;
    await model.mutate(
      '/favorites/add-recipe',
      query: {
        'customerId': '301',
        'customerName': '博丽灵梦',
        'recipeId': '${object(candidate['recipe'])['id']}',
        'foodTag': '家常',
        'extraIngredientIds': '1,3',
      },
    );
    var entries = records(model.resources['/favorites']!['recipes']);
    expect(entries, hasLength(count + 1));
    expect(entries.last['extraIngredientIds'], [1, 3]);
    await model.mutate(
      '/favorites/remove-recipe',
      query: {'id': '${entries.last['id']}'},
    );
    expect(
      records(model.resources['/favorites']!['recipes']),
      hasLength(count),
    );
    model.reset();
    entries = records(model.resources['/favorites']!['recipes']);
    expect(entries, hasLength(count));
    expect(entries.first['id'], 'favorite-start');
  });

  test('自定义配方编辑保留条目身份，排序和启用操作在同一内存集合中生效', () async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    final initialCount = records(model.resources['/custom-recipes']!['recipes'])
        .length;
    const created = {
      'mode': 'customer',
      'expectedModeRevision': '0',
      'customerId': '301',
      'customerName': '博丽灵梦',
      'recipeId': '1001',
      'foodId': '101',
      'recipeName': '预览配方',
      'foodTag': '家常',
      'extraIngredientIds': '1,1,3',
      'enabled': 'true',
      'pinToTop': 'false',
    };
    await model.mutate('/custom-recipes/upsert', query: created);
    final saved = records(model.resources['/custom-recipes']!['recipes']).last;
    final id = saved['id'];
    // 正式存储不会回传重复的加料 ID；验证输入去重而不伪造重复输出样例。
    expect(saved['extraIngredientIds'], [1, 3]);
    await model.mutate(
      '/custom-recipes/upsert',
      query: {
        ...created,
        'id': '$id',
        'recipeName': '已编辑的配方',
        'extraIngredientIds': '1,3',
      },
    );
    await model.mutate(
      '/custom-recipes/move',
      query: {
        'mode': 'customer',
        'expectedModeRevision': '0',
        'id': '$id',
        'direction': 'up',
      },
    );
    await model.mutate(
      '/custom-recipes/update-flags',
      query: {
        'mode': 'customer',
        'expectedModeRevision': '0',
        'id': '$id',
        'enabled': 'false',
      },
    );
    final entries = records(model.resources['/custom-recipes']!['recipes']);
    expect(entries, hasLength(initialCount + 1));
    final edited = entries.singleWhere((entry) => entry['id'] == id);
    expect(edited['recipeName'], '已编辑的配方');
    expect(edited['extraIngredientIds'], [1, 3]);
    expect(edited['enabled'], isFalse);
    final siblings = entries
        .where((entry) => entry['customerId'] == 301)
        .toList();
    expect(siblings.indexOf(edited), siblings.length - 2);
    await model.mutate(
      '/custom-recipes/remove',
      query: {'mode': 'customer', 'expectedModeRevision': '0', 'id': '$id'},
    );
    expect(
      records(model.resources['/custom-recipes']!['recipes']),
      hasLength(initialCount),
    );
  });

  test('普客预览保留八条料理并明确区分覆盖总数与人数', () async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    await model.queryRecommendations(kind: 'normal', selectedPlace: '博丽神社');
    final result = object(model.pageResult['result']);
    final foods = records(result['recipes']);
    final drinks = records(result['beverages']);
    expect(result['kind'], 'normal');
    expect(foods, hasLength(8));
    expect(drinks, hasLength(6));
    expect(object(foods.first['recipe'])['name'], '海鲜味噌汤');
    expect(object(drinks.first['beverage'])['name'], '冰山毛玉冻柠');
    for (final row in [...foods, ...drinks]) {
      expect(row['totalCoverage'], 3);
      expect(row['coveredCustomerCount'], 2);
      expect(records(row['customerCoverage']), hasLength(3));
      expect(values(row['activeTags']), isNotEmpty);
      expect(records(row['conditionResults']), isNotEmpty);
      expect(row.containsKey('allTags'), isFalse);
      expect(row.containsKey('meetsRequiredFood'), isFalse);
    }
    expect(foods.first['ingredientCost'], 10);
    expect(foods.first['profit'], 18);
    expect(object(foods.last['recipe'])['ingredients'], ['豆腐', '豆腐', '海苔']);
    expect(foods.last['ingredientCost'], 16);
    expect(foods.first.containsKey('extraIngredients'), isFalse);
    expect(drinks.first.containsKey('profit'), isFalse);
    expect(drinks.first.containsKey('suppressedTags'), isFalse);
  });

  test('普客经营目录保留真实嵌套，标价不能靠非协议扁平字段通过验收', () {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    final orders = records(model.recommendations['normalOrderDetailPlans']);
    expect(orders, hasLength(8));
    expect(object(orders.first['executionFood'])['name'], '海鲜味噌汤');
    expect(object(orders.first['executionBeverage'])['name'], '冰山毛玉冻柠');
    for (final order in orders) {
      for (final key in ['originalFood', 'executionFood']) {
        final food = object(order[key]);
        final recipe = object(food['recipe']);
        expect(food['foodId'], recipe['id']);
        expect(food['recipeId'], recipe['recipeId']);
        expect(food['baseIngredientNames'], recipe['ingredients']);
        expect(recipe['price'], isA<num>());
        for (final absent in [
          'id',
          'price',
          'ingredientCost',
          'profit',
          'matchedTagCount',
        ]) {
          expect(food.containsKey(absent), isFalse, reason: '$key.$absent');
        }
      }
      for (final key in ['originalBeverage', 'executionBeverage']) {
        final drink = object(order[key]);
        final beverage = object(drink['beverage']);
        expect(drink['beverageId'], beverage['id']);
        expect(beverage['price'], isA<num>());
        expect(drink.containsKey('price'), isFalse);
        expect(drink.containsKey('matchedTagCount'), isFalse);
      }
    }
  });

  test('稀客预览采用页面行字段而非经营计划或普客覆盖字段', () async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    await model.queryRecommendations(
      kind: 'rare',
      customerId: 301,
      foodTag: '家常',
      beverageTag: '无酒精',
    );
    final result = object(model.pageResult['result']);
    final foods = records(result['recipes']);
    final drinks = records(result['beverages']);
    expect(foods, hasLength(8));
    expect(drinks, hasLength(6));
    expect(object(foods.first['recipe'])['name'], '海鲜味噌汤');
    expect(values(foods.first['allTags']), contains('家常'));
    expect(foods[2]['meetsRequiredFood'], isFalse);
    expect(values(foods[2]['allTags']), isNot(contains('家常')));
    expect(foods[2]['customRecipeScope'], 'all');
    expect(foods[2]['customRecipePinned'], isTrue);
    for (final row in foods) {
      expect(row.containsKey('cancelledTags'), isTrue);
      expect(row['baseCost'], isA<num>());
      expect(row['extraCost'], isA<num>());
      expect(row.containsKey('activeTags'), isFalse);
      expect(row.containsKey('totalCoverage'), isFalse);
      expect(row.containsKey('missionTarget'), isFalse);
    }
    expect(drinks.first['meetsRequiredBev'], isTrue);
    expect(drinks[3]['meetsRequiredBev'], isFalse);
    for (final row in drinks) {
      expect(row.keys.toSet(), {'beverage', 'matchedTags', 'meetsRequiredBev'});
    }
  });

  test('收藏只含持久字段，目录编号与游戏配方编号不同且缺目录可见', () {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    final recipe = model.catalogItems('recipes').first;
    expect(recipe['id'], 101);
    expect(recipe['recipeId'], 1001);
    expect(recipe.containsKey('positiveTags'), isTrue);
    expect(recipe.containsKey('tags'), isFalse);
    final favorites = model.resources['/favorites']!;
    final foods = records(favorites['recipes']);
    final drinks = records(favorites['beverages']);
    expect(favorites['version'], 1);
    expect(foods.first['recipeId'], recipe['id']);
    expect(drinks.first['beverageId'], 201);
    expect(drinks.first['beverageTag'], '无酒精');
    expect(foods.last['recipeId'], 9901);
    expect(foods.last['extraIngredientIds'], [9902]);
    expect(drinks.last['beverageId'], 9903);
    for (final row in [...foods, ...drinks]) {
      expect(row.containsKey('recipeName'), isFalse);
      expect(row.containsKey('allTags'), isFalse);
      expect(row.containsKey('activeTags'), isFalse);
      expect(DateTime.tryParse(row['updatedAtUtc']! as String), isNotNull);
    }
  });

  test('自定义预览包含启停置顶和同料理不同加料，编辑不重置原标记', () async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    final source = model.resources['/custom-recipes']!;
    final recipes = records(source['recipes']);
    expect(source['version'], 2);
    expect(recipes.first['id'], 'custom-start');
    expect(recipes.first['recipeName'], '豆腐锅');
    expect(recipes.first['foodId'], 102);
    expect(recipes.first['recipeId'], 1002);
    expect(recipes.where((row) => row['foodId'] == 102), hasLength(2));
    expect(recipes.any((row) => row['enabled'] == false), isTrue);
    expect(recipes.any((row) => row['pinToTop'] == true), isTrue);
    final all = recipes.singleWhere((row) => row['foodTag'] == null);
    await model.mutate(
      '/custom-recipes/upsert',
      query: {
        'id': '${all['id']}',
        'mode': 'customer',
        'expectedModeRevision': '0',
        'customerId': '${all['customerId']}',
        'customerName': '${all['customerName']}',
        'foodId': '${all['foodId']}',
        'recipeId': '${all['recipeId']}',
        'recipeName': '${all['recipeName']}',
        'foodTag': '',
        'extraIngredientIds': '6,4,6',
      },
    );
    final edited = records(source['recipes'])
        .singleWhere((row) => row['id'] == all['id']);
    expect(edited['extraIngredientIds'], [4, 6]);
    expect(edited['foodTag'], isNull);
    expect(edited['enabled'], isTrue);
    expect(edited['pinToTop'], isTrue);
    expect(edited['sortOrder'], all['sortOrder']);
    await model.mutate(
      '/custom-recipes/settings',
      query: {'expectedModeRevision': '0', 'enabled': 'false'},
    );
    expect(source['enabled'], isFalse);
    expect(edited['enabled'], isTrue);
  });

  test('库存快捷调整只在预期数量仍一致时写入，失配不重试', () async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    await model.mutate(
      '/inventory/set',
      query: {'type': 'ingredient', 'id': '1', 'qty': '20'},
    );
    await model.mutate(
      '/inventory/set',
      query: {
        'type': 'ingredient',
        'id': '1',
        'qty': '30',
        'expectedQuantity': '20',
      },
    );
    expect(object(model.runtime['ownedIngredientQty'])['1'], 30);
    await model.mutate(
      '/inventory/set',
      query: {
        'type': 'ingredient',
        'id': '1',
        'qty': '30',
        'expectedQuantity': '20',
      },
    );
    expect(object(model.runtime['ownedIngredientQty'])['1'], 30);
    expect(model.error, contains('库存已变化'));
  });

  test('只读和断线场景拒绝库存写入，日间邀请只改变当前合成名单', () async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    await model.mutate(
      '/inventory/bulk-set',
      query: {'type': 'ingredient', 'ids': '1,3', 'qty': '88'},
    );
    expect(object(model.runtime['ownedIngredientQty'])['1'], 88);
    expect(object(model.runtime['ownedIngredientQty'])['3'], 88);
    for (final scenario in ['viewer', 'offline']) {
      model.selectScenario(scenario);
      final before = object(model.runtime['ownedIngredientQty'])['1'];
      await model.mutate(
        '/inventory/set',
        query: {'type': 'ingredient', 'id': '1', 'qty': '99'},
      );
      expect(object(model.runtime['ownedIngredientQty'])['1'], before);
      expect(model.error, isNotEmpty);
    }
    model.selectScenario('day');
    await model.readResource(
      '/rare-guests/invitations',
      query: {'scope': 'current'},
    );
    final before = records(
      model.resources['/rare-guests/invitations']!['candidates'],
    );
    expect(
      before.every((item) => values(item['sceneNames']).contains('博丽神社')),
      isTrue,
    );
    expect(
      before.every((item) => item['locationStatus'] == 'current-map'),
      isTrue,
    );
    await model.mutate('/rare-guests/invite', query: {'guestId': '303'});
    final guest = records(
      model.resources['/rare-guests/invitations']!['candidates'],
    ).singleWhere((item) => item['id'] == 303);
    expect(guest['canInvite'], isFalse);
    expect(guest['status'], 'invited');
    expect(guest['reason'], '预览已邀请');
    // 批量邀请也必须保持外层状态、可操作性和说明一致，且不改未满足条件的客人。
    await model.mutate(
      '/rare-guests/invite-all',
      query: {'scope': 'all', 'levels': '3'},
    );
    final afterBatch = records(
      model.resources['/rare-guests/invitations']!['candidates'],
    );
    final batchGuest = afterBatch.singleWhere((item) => item['id'] == 304);
    expect(batchGuest['locationStatus'], 'not-in-current-map');
    expect(batchGuest['sceneNames'], isEmpty);
    expect(batchGuest['sceneLabels'], isEmpty);
    expect(batchGuest['isCurrentScene'], isFalse);
    expect(batchGuest['canInvite'], isFalse);
    expect(batchGuest['status'], 'invited');
    expect(batchGuest['reason'], '预览已邀请');
    expect(
      afterBatch.singleWhere((item) => item['id'] == 301)['status'],
      'low-kizuna',
    );
    final unreadable = afterBatch.singleWhere((item) => item['id'] == 306);
    expect(unreadable['locationStatus'], 'unavailable');
    expect(unreadable['sceneNames'], isEmpty);
  });

  test('场景切换保留自动化配置，旧确认不能清除新场景中复用编号的屏障', () async {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    model.selectScenario('barrier');
    final oldSession = model.snapshot['automationSessionId']! as String;
    final oldGeneration = model.snapshot['nightBusinessGeneration']! as int;
    final oldConnection = model.connectionVersion;
    expect(model.canExecute, isTrue);
    model.selectScenario('barrier');
    expect(model.connectionVersion, greaterThan(oldConnection));
    expect(model.profile['automationEnabled'], isTrue);
    expect(model.canExecute, isTrue);
    await model.acknowledgeBarrier(
      1,
      expectedSessionId: oldSession,
      expectedGeneration: oldGeneration,
    );
    expect(records(model.automation['safetyBarriers']), hasLength(1));
    await model.acknowledgeBarrier(
      1,
      expectedSessionId: model.snapshot['automationSessionId']! as String,
      expectedGeneration: model.snapshot['nightBusinessGeneration']! as int,
    );
    expect(records(model.automation['safetyBarriers']), isEmpty);
    await model.mutate(
      '/devices/primary',
      body: {'deviceId': 'preview-mobile'},
    );
    expect(model.canEdit, isFalse);
    expect(model.profile['automationEnabled'], isTrue);
    expect(model.canExecute, isFalse);
  });

  test('仅明确退出调用注入的宿主动作，偏好和其他命令只改变内存', () async {
    var exitCount = 0;
    final model = RichPreviewWorkbenchModel(onExit: () async => exitCount++);
    addTearDown(model.dispose);
    await model.updateLocal({'darkMode': true, 'contentOpacityPercent': 80});
    await model.platformCommand('hide');
    await model.importHistory('不会访问的文件路径.json');
    await model.updateLanConnection(
      enabled: true,
      host: 'auto',
      token: 'synthetic-preview-token',
    );
    expect(
      model.resources['/local-api/config']!['token'],
      'synthetic-preview-token',
    );
    expect(model.dark, isTrue);
    expect(model.localPreferences['contentOpacityPercent'], 80);
    expect(exitCount, 0);
    await model.platformCommand('exit');
    expect(exitCount, 1);
    expect(model.error, isEmpty);
  });
}
