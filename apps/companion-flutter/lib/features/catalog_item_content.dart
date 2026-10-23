import 'package:flutter/widgets.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/menu_item_content.dart';
import '../ui/steward_style.dart';
import 'menu_recipe_media.dart';

/// 图鉴只描述目录条目；是否已解锁来自当前存档的独立读取事实，与库存、推荐候选
/// 及主设备执行许可无关。未知状态不能被归入“未解锁”，也不能由曾显示过图片推断。
enum CatalogUnlockState { unlocked, locked, unknown }

extension CatalogUnlockLabel on CatalogUnlockState {
  String get text => switch (this) {
    CatalogUnlockState.unlocked => '已解锁',
    CatalogUnlockState.locked => '未解锁',
    CatalogUnlockState.unknown => '解锁状态未知',
  };
}

/// 每次页面刷新只解析一次完整的解锁集合，避免每个条目重复扫描整个列表。
/// 料理的 availableRecipeIds 是宿主由 HaveRecipe 精确映射得到的 Food.id，不能拿
/// 独立的 Recipe.id 比较。酒水仅使用图鉴记录，不使用 availableBeverageIds（库存）。
class CatalogUnlockFacts {
  const CatalogUnlockFacts._(this.ids);
  final Set<int>? ids;
  bool get known => ids != null;

  factory CatalogUnlockFacts.read(WorkbenchModel model, {required bool food}) {
    final runtime = model.runtime;
    final ready =
        runtime[food ? 'inventoryReadReady' : 'beverageUnlockReadReady'];
    final raw = runtime[food ? 'availableRecipeIds' : 'unlockedBeverageIds'];
    // 断线或快照读取失败后，不沿用上一存档的解锁判断；完整静态目录仍可只读浏览。
    // 非整数、负数或缺失列表说明完整事实未建立，不能把异常项悄悄过滤成“未解锁”。
    if (!model.connected ||
        model.snapshot['status'] == 'error' ||
        model.catalog['isComplete'] != true ||
        ready != true ||
        raw is! List ||
        raw.any((id) => entityImageId(id) == null)) {
      return const CatalogUnlockFacts._(null);
    }
    return CatalogUnlockFacts._(Set<int>.unmodifiable(raw.cast<int>()));
  }

  CatalogUnlockState stateFor(DataMap entry) {
    final id = entityImageId(entry['id']);
    final current = ids;
    if (id == null || current == null) return CatalogUnlockState.unknown;
    return current.contains(id)
        ? CatalogUnlockState.unlocked
        : CatalogUnlockState.locked;
  }
}

/// 只接受原目录的文字列表，保留顺序和重复材料；缺失/损坏与明确空列表分开。
/// 不按标签推导配方，不按材料名称猜编号，也不把目录价格改算成成本或利润。
List<String>? _catalogStrings(Object? raw) =>
    raw is List && raw.every((item) => item is String && item.trim().isNotEmpty)
    ? raw.cast<String>().toList(growable: false)
    : null;

String? _catalogText(Object? raw) =>
    raw is String && raw.trim().isNotEmpty ? raw : null;

String? _catalogNumber(Object? raw) =>
    raw is num && raw.isFinite && raw >= 0 ? raw.toString() : null;

/// 将静态目录适配到项目统一料理/酒水组件。列表和详情复用同一事实，不制造匹配数、
/// 库存、推荐分值或可执行状态。图片身份要求当前类别中的精确编号唯一；同名条目互不关联。
MenuItemView catalogItemView(
  WorkbenchModel model,
  DataMap entry, {
  required bool food,
}) {
  final id = entityImageId(entry['id']);
  final unique =
      id != null &&
      model
              .catalogItems(food ? 'recipes' : 'beverages')
              .where((item) => item['id'] == id)
              .take(2)
              .length ==
          1;
  final mediaKey = food && unique ? menuRecipeMediaKey(model, entry) : null;
  final names = _catalogStrings(entry['ingredients']);
  var ingredients = names == null
      ? null
      : [for (final name in names) MenuIngredientView(name)];
  if (food && unique && ingredients != null) {
    final ids = entry['ingredientIds'];
    // 显式 ingredientIds 与材料名称逐位对应，重复加料仍保留重复；若旧目录没有
    // 此字段，才使用已校验图片清单中的双编号依赖。两条路径都不通过名称反查。
    if (ids is List &&
        ids.length == ingredients.length &&
        ids.every((value) => entityImageId(value) != null)) {
      ingredients = [
        for (var index = 0; index < ingredients.length; index++)
          MenuIngredientView(
            ingredients[index].name,
            entityId: ids[index] as int,
          ),
      ];
    } else {
      ingredients = menuRecipeIngredientImages(model, ingredients, mediaKey);
    }
  }
  final price = _catalogNumber(entry['price']);
  final level = _catalogNumber(entry['level']);
  final description = _catalogText(entry['description']);
  return MenuItemView(
    kind: food ? MenuItemKind.food : MenuItemKind.beverage,
    name: _catalogText(entry['name']) ?? '名称未知',
    entityId: unique ? id : null,
    cooker: food ? _catalogText(entry['cooker']) : null,
    cookerId: food ? menuRecipeCookerImage(model, mediaKey) : null,
    ingredients: food ? ingredients : null,
    facts: [
      if (price != null) MenuItemFact('标价', price),
      if (level != null) MenuItemFact('等级', level),
    ],
    summaryTagLabel: '标签',
    summaryTags:
        _catalogStrings(entry[food ? 'positiveTags' : 'tags']) ?? const [],
    notes: [if (description != null) description],
  );
}

/// 图鉴身份行将解锁状态放在名称右侧，不为每条相同状态额外占一整行。
/// 长名称在剩余宽度自然换行，状态保留完整文字；列表与详情沿用同一布局。
class CatalogItemHeader extends StatelessWidget {
  const CatalogItemHeader(this.item, this.unlock, {super.key});
  final MenuItemView item;
  final CatalogUnlockState unlock;

  @override
  Widget build(BuildContext context) => Row(
    crossAxisAlignment: CrossAxisAlignment.center,
    children: [
      Expanded(child: MenuItemName(item)),
      const SizedBox(width: 8),
      StatusBadge(unlock.text, emphasis: unlock == CatalogUnlockState.unlocked),
    ],
  );
}

/// 图鉴详情只增补完整标签、说明和精确目录编号。负面标签是料理的固有属性，
/// 不能套用推荐组件的“已抵消”含义；未知数值保持未知，不由默认零掩盖读取缺失。
class CatalogItemDetails extends StatelessWidget {
  const CatalogItemDetails({
    required this.model,
    required this.entry,
    required this.food,
    required this.unlock,
    super.key,
  });
  final WorkbenchModel model;
  final DataMap entry;
  final bool food;
  final CatalogUnlockState unlock;

  @override
  Widget build(BuildContext context) {
    final item = catalogItemView(model, entry, food: food);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        CatalogItemHeader(item, unlock),
        // 图鉴详情的上方标签一次展示完整内容，下方只补充说明与独有事实。
        // 列表仍保留四项摘要，不能直接删除下方标签后把第五项起的内容丢掉。
        MenuItemContent(
          item,
          showName: false,
          showExtras: false,
          summaryTagLimit: null,
        ),
        const MenuItemDivider(),
        MenuItemDetails(item, showSummaryTags: false),
        if (food)
          MenuTagGroup(
            '负面',
            _catalogStrings(entry['negativeTags']) ?? const [],
            kind: OrderTagKind.warning,
          ),
        if (entityImageId(entry['id']) case final int id)
          Fact(food ? '料理编号' : '酒水编号', '$id'),
        if (food)
          if (entityImageId(entry['recipeId']) case final int recipeId)
            Fact('配方编号', '$recipeId'),
        if (food)
          const Text(
            '这里展示基础配方资料；当前推荐、加料与制作条件请在经营或推荐中查看。',
            style: StewardText.caption,
          ),
      ],
    );
  }
}
