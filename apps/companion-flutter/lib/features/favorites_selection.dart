import '../app/workbench_model.dart';

/// 收藏契约使用非负整数目录 ID；不能把缺失值、负占位或浮点值转为可写身份。
int? favoriteEntityId(Object? value) =>
    value is int && value >= 0 ? value : null;

/// 收藏身份沿用 main/FavoriteStore 的集合语义：重复材料去重后按 ID 排序。
/// 这里只生成新的收藏键，不修改原方案的制作材料序列；未知材料不按空加料处理。
List<int>? safeFavoriteExtraIds(Object? value, {bool entities = false}) {
  if (value is! List) return null;
  final ids = <int>{};
  for (final entry in value) {
    final id = favoriteEntityId(entities ? object(entry)['id'] : entry);
    if (id == null) return null;
    ids.add(id);
  }
  return ids.toList()..sort();
}

/// 经营、专注及推荐共用精确收藏匹配。未读取列表与已读取空列表不同；多个相同身份
/// 的记录也不能任选一条删除。返回 known=false 时禁用写入，等待用户刷新确认。
({bool known, String? id}) recommendationFavorite(
  DataMap favorites,
  DataMap row, {
  required bool food,
  required int? customerId,
  required String tag,
}) {
  final entries = favorites[food ? 'recipes' : 'beverages'];
  final entity = object(row[food ? 'recipe' : 'beverage']);
  final id = favoriteEntityId(entity['id']);
  final extras = food
      ? safeFavoriteExtraIds(row['extraIngredients'], entities: true)
      : <int>[];
  if (favoriteEntityId(customerId) == null ||
      tag.trim().isEmpty ||
      id == null ||
      entries is! List ||
      extras == null) {
    return (known: false, id: null);
  }
  final found = <String>[];
  for (final raw in entries) {
    if (raw is! Map) return (known: false, id: null);
    final entry = object(raw);
    if (entry['customerId'] != customerId ||
        entry[food ? 'foodTag' : 'beverageTag'] != tag)
      continue;
    if (entry[food ? 'recipeId' : 'beverageId'] != id) continue;
    if (food) {
      final stored = safeFavoriteExtraIds(entry['extraIngredientIds']);
      if (stored == null) return (known: false, id: null);
      if (stored.join(',') != extras.join(',')) continue;
    }
    final recordId = entry['id'];
    if (recordId is! String || recordId.isEmpty)
      return (known: false, id: null);
    found.add(recordId);
  }
  return found.length > 1
      ? (known: false, id: null)
      : (known: true, id: found.firstOrNull);
}
