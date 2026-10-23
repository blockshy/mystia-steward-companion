import '../app/workbench_model.dart';
import '../ui/entity_image.dart';
import '../ui/menu_item_content.dart';

/// 图片依赖需要 Food.id 和独立 Recipe.id 同时匹配当前唯一目录条目。
/// 不完整/重复目录或不一致的配方编号一律不用资源依赖；显示名称仍保留原 DTO。
String? menuRecipeMediaKey(WorkbenchModel model, DataMap entity) {
  final foodId = entityImageId(entity['id']);
  final recipeId = entityImageId(entity['recipeId']);
  if (model.catalog['isComplete'] == false ||
      foodId == null ||
      recipeId == null)
    return null;
  final matches = model
      .catalogItems('recipes')
      .where((item) => item['id'] == foodId)
      .take(2)
      .toList();
  return matches.length == 1 && matches.single['recipeId'] == recipeId
      ? '$foodId:$recipeId'
      : null;
}

/// 当前资源代次保留 Recipe.ingredients 的原顺序与重复项，只有长度也一致时才逐位
/// 附加图片身份。切代次、断线或资源尚未就绪时模型撤回索引，这里自动保留无图的
/// 原 DTO；不改文字/库存、不重算配方，也不用本地中文名称去寻找另一项食材。
List<MenuIngredientView>? menuRecipeIngredientImages(
  WorkbenchModel model,
  List<MenuIngredientView>? ingredients,
  String? recipeKey,
) {
  final ids = recipeKey == null
      ? null
      : model.gameMediaRecipes[recipeKey]?['ingredientIds'];
  if (ingredients == null ||
      ids is! List ||
      ids.length != ingredients.length ||
      ids.any((id) => entityImageId(id) == null))
    return ingredients;
  return [
    for (var index = 0; index < ingredients.length; index++)
      MenuIngredientView(
        ingredients[index].name,
        stock: ingredients[index].stock,
        entityId: ids[index] as int,
      ),
  ];
}

/// cookerType 与场景里的升级厨具 ID 是两种身份；只读取已验证配方的类型映射。
int? menuRecipeCookerImage(WorkbenchModel model, String? recipeKey) {
  final type = recipeKey == null
      ? null
      : entityImageId(model.gameMediaRecipes[recipeKey]?['cookerType']);
  // 当前游戏的基础厨具类型严格为 1..5；升级实例编号和未来未知值保持原图标。
  return type != null && type >= 1 && type <= 5 ? type : null;
}
