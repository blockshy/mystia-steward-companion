import 'package:companion_protocol/companion_protocol.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';

/// 仅测试使用的事实投影；不启动游戏，也不将模拟数据注入生产入口。
class FixtureModel extends WorkbenchModel {
  FixtureModel({this.desktop = true, int orderCount = 40}) {
    business = {
      'protocolVersion': 1,
      'isCurrent': true,
      'pending': false,
      'automation': {
        'message': '等待料理完成 · 厨具 3 / 4',
        'resourceOverview': {
          'cookers': [
            {
              'label': '煮锅',
              'capacity': 2,
              'normalReserved': 1,
              'rareReserved': 1,
            },
          ],
        },
      },
      'recommendations': {
        'recommendations': [
          for (var i = 0; i < orderCount; i++)
            {
              'order': {
                'deskCode': i + 1,
                'guestName': i.isEven ? '博丽灵梦' : '雾雨魔理沙',
                'traceId': 'trace-$i',
                'orderLifecycleSequence': i,
                'foodTag': '家常',
                'beverageTag': '无酒精',
                'fund': 120,
              },
              'executionPlans': [
                {
                  'food': {
                    'recipe': {'id': 1, 'name': '海鲜味噌汤'},
                    'extraIngredients': [
                      {'id': 2, 'name': '蘑菇'},
                    ],
                  },
                  'beverage': {
                    'beverage': {'id': 3, 'name': '冰山毛玉冻柠'},
                  },
                  'estimatedPrice': 42,
                  'bucket': 'complete',
                  'reasons': ['满足订单与喜好'],
                  'warnings': <String>[],
                },
              ],
            },
        ],
        'normalOrderDetailPlans': <DataMap>[],
      },
    };
  }
  @override
  DataMap snapshot = {
    'pluginVersion': '1.3.1',
    'activeSceneName': '夜间经营',
    'status': 'ready',
    'runtimeDaySceneReady': true,
    'runtimeDaySceneGeneration': 1,
    'activeDayMapLabel': 'hakurei',
    'recommendationState': {
      // 纯内存夹具显式声明数量事实；生产必须由实际库存读取成功后发布此标记。
      'inventoryReadReady': true,
      'availableRecipeIds': [1, 2],
      'availableBeverageIds': [3],
      'popularFoodTag': '清淡',
      'ownedIngredientQty': {'2': 3},
      'ownedBeverageQty': {'3': 12},
    },
    'nightBusiness': {'place': '博丽神社'},
  };
  @override
  DataMap business = {};

  /// 旧几何夹具只描述卡片，不模拟 HTTP 时序和原生订单身份，继续把这些显式输入作为
  /// 展示样本。实时同步专项使用真实 snapshot 并重载此端口，不能用本夹具证明会话正确。
  @override
  DataMap get serviceOrders => connected ? recommendations : const {};
  @override
  DataMap catalog = {
    'recipes': [
      {'id': 1, 'recipeId': 101, 'name': '海鲜味噌汤'},
    ],
    'ingredients': [
      {
        'id': 2,
        'name': '蘑菇',
        'tags': ['鲜'],
      },
    ],
    'beverages': [
      {'id': 3, 'name': '冰山毛玉冻柠'},
    ],
    'rareCustomers': [
      {'id': 1, 'name': '博丽灵梦'},
    ],
    'normalCustomers': [
      {
        'id': 2,
        'name': '妖精',
        'places': ['博丽神社'],
      },
    ],
  };
  @override
  DataMap authority = {
    'currentDeviceIsPrimary': true,
    'activeProfileRevision': 1,
    'devices': [],
  };
  @override
  DataMap profile = CompanionProfile.defaults().json;
  @override
  DataMap pageResult = {};
  @override
  DataMap localPreferences = {};
  @override
  Map<String, DataMap> resources = {};
  @override
  bool connected = true;
  @override
  bool busy = false;
  @override
  bool desktop;
  @override
  bool canExecute = true;
  @override
  String error = '';
  @override
  String notice = '';
  @override
  String endpoint = 'http://127.0.0.1:32145';
  @override
  String clientLabel = '测试电脑';
  @override
  String inputAction = '';
  @override
  int inputSequence = 0;
  final List<(String, Map<String, String>, DataMap?)> writes = [];
  final List<String> reads = [];
  void publish() => notifyListeners();
  void input(String action) {
    inputAction = action;
    inputSequence++;
    notifyListeners();
  }

  @override
  Future<void> connect(
    String endpoint,
    String token,
    String deviceLabel,
  ) async {}
  @override
  Future<void> refresh() async {}
  @override
  Future<void> readResource(
    String path, {
    Map<String, String> query = const {},
  }) async {
    reads.add(path);
  }

  @override
  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  }) async {
    writes.add((path, query, body));
  }

  @override
  Future<void> queryRecommendations({
    required String kind,
    String selectedPlace = '',
    int? customerId,
    String foodTag = '',
    String beverageTag = '',
  }) async {}
  @override
  Future<void> updateProfile(DataMap changes) async {
    profile = {...profile, ...changes};
    publish();
  }

  @override
  Future<void> updateLocal(DataMap changes) async {
    localPreferences = {...localPreferences, ...changes};
    publish();
  }

  @override
  Future<void> acknowledgeBarrier(
    int sequence, {
    required String expectedSessionId,
    required int expectedGeneration,
  }) async {
    writes.add(('ack', {}, {'sequence': sequence}));
  }

  @override
  Future<void> retryOrder(String kind, String key) async {
    writes.add(('retry', {'kind': kind, 'key': key}, null));
  }

  @override
  Future<void> platformCommand(String command) async {}
  @override
  Future<void> importHistory(String path) async {}
}
