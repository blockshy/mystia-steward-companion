import '../app/workbench_model.dart';
import '../ui/entity_image.dart';
import '../ui/menu_item_content.dart';
import 'menu_recipe_media.dart';

/// 客人图片与点单方式是两套身份。普通订单可以来自猯藏复制的稀客，必须消费宿主在
/// 主线程核对真实 GuestBase 派生类型后给出的命名空间和 ID；缺失时不能按裸数字补普客图。
/// 稀客订单原有 guestId 已由宿主规范化，可继续使用；runtimeGuestId 始终不是图片目录键。
({EntityImageKind kind, int? id}) serviceGuestImage(
  DataMap order, {
  required bool rare,
}) {
  final kind = switch (order['guestImageKind']) {
    'rare-guest' => EntityImageKind.rareGuest,
    'normal-group' => EntityImageKind.normalGroup,
    _ => null,
  };
  if (kind != null) {
    return (kind: kind, id: entityImageId(order['guestImageId']));
  }
  // 显式无效字段也不能落回旧 ID；只有真实稀客订单且未携带新字段时沿用既有规范身份。
  return (
    kind: rare ? EntityImageKind.rareGuest : EntityImageKind.normalGroup,
    id: rare && !order.containsKey('guestImageKind')
        ? entityImageId(order['guestId'])
        : null,
  );
}

/// 经营投影到纯展示模型的适配层。只按明确字段做格式化和目录联结，不执行评分、
/// 预算计算、料理替换或酒水挑选；调用者必须把同一个executionPlan的两半一起传入。
MenuItemView serviceFoodView(
  WorkbenchModel model,
  DataMap food, {
  bool normal = false,
}) {
  final entity = object(food['recipe']);
  final recipe = normal ? food : entity;
  final names = recipe[normal ? 'baseIngredientNames' : 'ingredients'];
  // 普客FoodDetail的名称、厨具、基础材料在外层，但标价只在recipe实体中。
  // 缺少目录时C#用空baseIngredientNames回退；此时空集合不等于真实无基础材料。
  final ingredientsKnown = !(normal && entity.isEmpty && values(names).isEmpty);
  final extras = food['extraIngredients'];
  final recipeKey = !normal || entityImageId(food['foodId']) == entity['id']
      ? menuRecipeMediaKey(model, entity)
      : null;
  return MenuItemView(
    kind: MenuItemKind.food,
    entityId: entityImageId(normal ? food['foodId'] : entity['id']),
    cookerId: menuRecipeCookerImage(model, recipeKey),
    name: label(recipe['name'], '待确认料理'),
    cooker: label(recipe[normal ? 'cookerName' : 'cooker'], '厨具未知'),
    ingredients: menuRecipeIngredientImages(
      model,
      ingredientsKnown ? _ingredients(model, names) : null,
      recipeKey,
    ),
    extras: _ingredients(model, extras),
    facts: [
      if (entity['price'] != null) MenuItemFact('标价', label(entity['price'])),
      if (food['baseCost'] != null)
        MenuItemFact('基础成本', label(food['baseCost'])),
      if (food['extraCost'] != null)
        MenuItemFact('加料成本', label(food['extraCost'])),
    ],
    activeTags: _strings(food['activeTags']),
    summaryTagLabel: '生效',
    summaryTags: _strings(food['activeTags']),
    suppressedTags: _strings(food['suppressedTags']),
    targetTags: _strings(food['targetTags']),
  );
}

/// 酒水使用本方案携带的实体ID读取观测库存，绝不从独立beverages候选列表补首项。
MenuItemView serviceBeverageView(
  WorkbenchModel model,
  DataMap beverage, {
  bool normal = false,
}) {
  final entity = object(beverage['beverage']);
  final drink = normal ? beverage : entity;
  final id = drink[normal ? 'beverageId' : 'id'];
  return MenuItemView(
    kind: MenuItemKind.beverage,
    entityId: entityImageId(id),
    name: label(drink['name'], '待确认酒水'),
    stock: menuStockText(_stock(model, id, beverage: true)),
    facts: [
      // 普客BeverageDetail同样只在嵌套实体保存标价，不从展示外层猜测价格。
      if (entity['price'] != null) MenuItemFact('标价', label(entity['price'])),
    ],
    activeTags: _strings(beverage['activeTags']),
    summaryTagLabel: '生效',
    summaryTags: _strings(beverage['activeTags']),
    suppressedTags: _strings(beverage['suppressedTags']),
  );
}

List<String> _strings(Object? value) =>
    values(value).map((entry) => label(entry)).toList(growable: false);

/// 仅名称的基础材料只能在目录名称唯一时补ID；显式ID优先，名称冲突不能取第一项。
/// 使用List保留重复材料；未知集合保持null，不能误报“不加料”。
List<MenuIngredientView>? _ingredients(WorkbenchModel model, Object? input) {
  if (input is! List) return null;
  return [
    for (final raw in input)
      (() {
        final item = object(raw);
        final name = raw is Map
            ? label(item['name'], '材料未知')
            : label(raw, '材料未知');
        Object? id = item['id'];
        if (id == null) {
          final matches = model
              .catalogItems('ingredients')
              .where((entry) => entry['name'] == name)
              .map((entry) => entry['id'])
              .whereType<int>()
              .where((value) => value >= 0)
              .toSet();
          if (matches.length == 1) id = matches.single;
        }
        return MenuIngredientView(
          name,
          // 名称唯一联结仅沿用既有库存展示；图片必须来自原对象的显式 ID。
          entityId: entityImageId(item['id']),
          stock: menuStockText(_stock(model, id)),
        );
      })(),
  ];
}

Object? _stock(WorkbenchModel model, Object? id, {bool beverage = false}) =>
    id is int && id >= 0
    ? object(
        model.runtime[beverage ? 'ownedBeverageQty' : 'ownedIngredientQty'],
      )['$id']
    : null;
