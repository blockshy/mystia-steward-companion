import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/menu_item_content.dart';
import '../ui/steward_style.dart';
import 'menu_recipe_media.dart';

/// 四种读取来源各有明确契约，不能用一串字段回退把匹配标签误当生效标签，
/// 或把收藏的料理 ID 与自定义配方的游戏配方 ID 混为一谈。
enum RecommendationSection { normal, rare, favorites, custom }

/// 将 C# 推荐行或存储条目转换为统一展示模型。这里只做字段映射与唯一目录关联：
/// 不计算评分、利润、标签生效关系、点单满足程度，也不推测未知库存。
MenuItemView recommendationItemView(
  WorkbenchModel model,
  DataMap row, {
  required RecommendationSection section,
  required bool food,
}) {
  // 自定义存储只有料理；之前在别的页选中酒水不能改变它的实体类别。
  food = section == RecommendationSection.custom || food;
  final stored =
      section == RecommendationSection.favorites ||
      section == RecommendationSection.custom;
  final Object? catalogId = section == RecommendationSection.custom
      ? row['foodId']
      : row[food ? 'recipeId' : 'beverageId'];
  final entity = stored
      ? _storedEntity(model, row, section: section, food: food)
      : object(row[food ? 'recipe' : 'beverage']);
  final facts = <MenuItemFact>[];
  final notes = <String>[];
  final warnings = <String>[];
  List<String> active = const [], suppressed = const [];
  var summaryTagLabel = '匹配';
  List<String> summaryTags = const [];
  final name =
      _text(entity['name']) ??
      (section == RecommendationSection.custom
          ? _text(row['recipeName'])
          : null) ??
      '${food ? '料理' : '酒水'} #${catalogId ?? '未知'}';
  facts.add(MenuItemFact('标价', _number(entity['price']) ?? '未知'));
  if (stored) {
    // 存储条目没有运行时匹配结果。摘要只展示静态标签，不把目录来源误作字段名称。
    summaryTagLabel = '标签';
    summaryTags = _strings(entity[food ? 'positiveTags' : 'tags']);
    facts.add(MenuItemFact('客人', _text(row['customerName']) ?? '未知'));
    facts.add(
      MenuItemFact(
        '点单',
        section == RecommendationSection.custom && row['foodTag'] == null
            ? '全部点单'
            : _text(row[food ? 'foodTag' : 'beverageTag']) ?? '未知',
      ),
    );
    if (entity.isEmpty) {
      // 原条目的名称仍可用于辨认与删除；缺失/歧义目录绝不能联结到另一份菜品。
      facts.add(const MenuItemFact('目录', '缺失或不唯一'));
      warnings.add('目录未唯一匹配，厨具、基础材料与标价保持未知。');
    }
    notes.add('此处显示保存的选择；实时匹配与成本请在稀客页查看。');
    if (section == RecommendationSection.custom) {
      facts.add(MenuItemFact('状态', _flag(row['enabled'], '启用', '停用')));
      facts.add(MenuItemFact('置顶', _flag(row['pinToTop'], '是', '否')));
      if (_number(row['sortOrder']) case final String order)
        facts.add(MenuItemFact('排序', order));
    }
  } else if (section == RecommendationSection.normal) {
    summaryTags = _strings(row['matchedTags']);
    active = _strings(row['activeTags']);
    if (food) {
      suppressed = _strings(row['suppressedTags']);
      facts.add(MenuItemFact('材料成本', _number(row['ingredientCost']) ?? '未知'));
      facts.add(MenuItemFact('利润', _number(row['profit']) ?? '未知'));
    }
    // totalCoverage 是各客群命中标签数之和，不是人数或百分比。
    facts.add(MenuItemFact('命中标签数', _number(row['totalCoverage']) ?? '未知'));
    facts.add(
      MenuItemFact('覆盖客人数', _number(row['coveredCustomerCount']) ?? '未知'),
    );
  } else if (food) {
    summaryTagLabel = '生效';
    summaryTags = _strings(row['allTags']);
    active = _strings(row['allTags']);
    suppressed = _strings(row['cancelledTags']);
    facts.add(MenuItemFact('基础成本', _number(row['baseCost']) ?? '未知'));
    facts.add(MenuItemFact('加料成本', _number(row['extraCost']) ?? '未知'));
    facts.add(
      MenuItemFact('料理点单', _flag(row['meetsRequiredFood'], '满足', '未满足')),
    );
    if (row['customRecipe'] == true) {
      facts.add(
        MenuItemFact(
          '来源',
          row['customRecipePinned'] == true ? '自定义 · 置顶' : '自定义',
        ),
      );
      if (row['customRecipeScope'] == 'all')
        facts.add(const MenuItemFact('适用', '全部点单'));
      if (row['customRecipeScope'] == 'tag')
        facts.add(const MenuItemFact('适用', '指定点单'));
      if (_number(row['customRecipeSortOrder']) case final String order)
        facts.add(MenuItemFact('自定义排序', order));
    }
  } else {
    summaryTags = _strings(row['matchedTags']);
    // 稀客独立推荐的字段名是 meetsRequiredBev；matchedTags 只是客人喜好交集，
    // 不可退回到经营计划的 meetsRequiredBeverage，也不能放入 activeTags。
    facts.add(
      MenuItemFact('酒水点单', _flag(row['meetsRequiredBev'], '满足', '未满足')),
    );
    if (row['matchedTags'] is List)
      facts.add(
        MenuItemFact('匹配标签数', '${_strings(row['matchedTags']).length}'),
      );
  }
  return MenuItemView(
    kind: food ? MenuItemKind.food : MenuItemKind.beverage,
    entityId: entityImageId(entity['id']),
    cookerId: food
        ? menuRecipeCookerImage(model, menuRecipeMediaKey(model, entity))
        : null,
    name: name,
    cooker: food ? _text(entity['cooker']) : null,
    ingredients: food
        ? menuRecipeIngredientImages(
            model,
            _baseIngredients(model, entity['ingredients']),
            menuRecipeMediaKey(model, entity),
          )
        : null,
    extras: !food
        ? null
        : stored
        ? _storedExtras(model, row['extraIngredientIds'])
        : section == RecommendationSection.normal
        // 普客地区接口明确只计算基础配方；其 DTO 没有 extraIngredients 字段。
        ? const []
        : _projectedExtras(model, row['extraIngredients']),
    stock: food ? null : _stock(model, 'ownedBeverageQty', entity['id']),
    facts: facts,
    summaryTagLabel: summaryTagLabel,
    summaryTags: summaryTags,
    activeTags: active,
    suppressedTags: suppressed,
    notes: notes,
    warnings: warnings,
  );
}

/// 详情只补充客群分解、匹配/目录标签及条件解释；价格、材料等主体已在外层展示。
/// 明确标注目录标签，避免让未计算的收藏/自定义内容看起来已得到运行时业务结论。
class RecommendationItemExplanation extends StatelessWidget {
  const RecommendationItemExplanation(
    this.model,
    this.row, {
    required this.section,
    required this.food,
    super.key,
  });
  final WorkbenchModel model;
  final DataMap row;
  final RecommendationSection section;
  final bool food;

  @override
  Widget build(BuildContext context) {
    final isFood = section == RecommendationSection.custom || food;
    final item = recommendationItemView(
      model,
      row,
      section: section,
      food: isFood,
    );
    final stored =
        section == RecommendationSection.favorites ||
        section == RecommendationSection.custom;
    // 静态标签也只从唯一关联的目录取出，绝不根据材料重新计算有效标签。
    final entity = stored
        ? _storedEntity(model, row, section: section, food: isFood)
        : object(row[isFood ? 'recipe' : 'beverage']);
    final staticTags = _strings(entity[isFood ? 'positiveTags' : 'tags']);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // 外层摘要与详情共用同一个展示模型。完整匹配／目录标签由公共详情展开，
        // 这里不再另拼一条纯文本，避免标签颜色、含义及“+N”的摘要各自漂移。
        MenuItemDetails(item),
        // 稀客酒水的 matchedTags 是喜好交集，目录 tags 是静态属性；两者不是同义。
        // 收藏／自定义的目录标签已经由公共详情显示，不能在此再重复一份。
        if (section == RecommendationSection.rare &&
            !isFood &&
            staticTags.isNotEmpty)
          MenuTagGroup('标签', staticTags, kind: OrderTagKind.ingredient),
        if (records(row['customerCoverage']).isNotEmpty)
          CollapsibleSection('客群匹配详情', [
            for (final customer in records(row['customerCoverage']))
              _ExplanationBlock(
                _text(customer['customerName']) ?? '客人未知',
                [
                  // 数量来自服务端 matchedTagCount，缺失时不能用标签数组长度补算。
                  MenuInfoGroup('统计', [
                    MenuMetric(
                      '匹配标签数',
                      _number(customer['matchedTagCount']) ?? '未知',
                    ),
                  ]),
                  if (_strings(customer['matchedTags']).isNotEmpty)
                    MenuTagGroup(
                      '匹配',
                      _strings(customer['matchedTags']),
                      kind: OrderTagKind.success,
                    ),
                ],
                customer: true,
                guestId: entityImageId(customer['customerId']),
              ),
          ]),
        if (records(row['conditionResults']).isNotEmpty)
          CollapsibleSection('推荐依据', [
            for (final condition in records(row['conditionResults']))
              _ExplanationBlock(_text(condition['label']) ?? '条件', [
                Text(_text(condition['detail']) ?? '说明未知'),
              ]),
          ]),
        if (isFood && object(row['extraIngredientReasonTags']).isNotEmpty)
          CollapsibleSection('加料说明', [
            // 原始对象的 key 为食材 ID。只解释服务端实际提供的标签列表，
            // 不根据材料标签与点单条件在客户端重新求交集。
            for (final reason in object(
              row['extraIngredientReasonTags'],
            ).entries)
              if (_strings(reason.value).isNotEmpty)
                _ExplanationBlock(
                  _reasonIngredient(model, row, reason.key),
                  [MenuTagGroup('加料', _strings(reason.value))],
                  ingredient: true,
                  ingredientId: _reasonIngredientId(reason.key),
                ),
          ]),
        if (stored && _text(row['updatedAtUtc']) != null)
          MenuInfoGroup('更新', [Text(label(row['updatedAtUtc']))]),
      ],
    );
  }
}

/// 任意长度的客人名、食材名或条件标题单独占一行，避免把长名称挤入短前缀列。
/// 下方保留真正的标签列表或解释段落，不把服务端完整说明拆成虚假的标签。
class _ExplanationBlock extends StatelessWidget {
  const _ExplanationBlock(
    this.title,
    this.children, {
    this.customer = false,
    this.guestId,
    this.ingredient = false,
    this.ingredientId,
  });
  final String title;
  final List<Widget> children;

  /// customerCoverage.customerId 是服务端规范客群编号；缺失时保留原图标与名称，
  /// 绝不拿现场 guestId、显示名称或数组位置寻找另一位客人的图像。
  final int? guestId;
  final bool customer;

  /// 加料解释的对象键由服务端明确输出食材ID；名称只作标题，不参与图片身份匹配。
  final bool ingredient;
  final int? ingredientId;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: StewardDensity.detailGroupGap),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (customer || ingredient)
          EntityName(
            title,
            kind: customer
                ? EntityImageKind.normalGroup
                : EntityImageKind.ingredient,
            id: customer ? guestId : ingredientId,
            fallback: customer ? FLucideIcons.users : FLucideIcons.leaf,
            style: const TextStyle(fontWeight: FontWeight.w600),
          )
        else
          Text(title, style: const TextStyle(fontWeight: FontWeight.w600)),
        const SizedBox(height: StewardDensity.supportingGap),
        ...children,
      ],
    ),
  );
}

/// 收藏 recipeId 指向料理目录 id；自定义同时存有独立的游戏 recipeId。
/// 先确认目录 id 本身唯一，再校验游戏编号；不允许通过附加条件掩盖重复目录 ID。
DataMap _storedEntity(
  WorkbenchModel model,
  DataMap row, {
  required RecommendationSection section,
  required bool food,
}) {
  if (model.catalog['isComplete'] == false) return const {};
  final id = section == RecommendationSection.custom
      ? row['foodId']
      : row[food ? 'recipeId' : 'beverageId'];
  final entry = _unique(
    model.catalogItems(food ? 'recipes' : 'beverages'),
    (candidate) => _validId(id) && candidate['id'] == id,
  );
  if (section == RecommendationSection.custom &&
      (!_validId(row['recipeId']) || entry['recipeId'] != row['recipeId']))
    return const {};
  return entry;
}

DataMap _unique(List<DataMap> entries, bool Function(DataMap) matches) {
  final found = entries.where(matches).take(2).toList(growable: false);
  return found.length == 1 ? found.single : const {};
}

String? _text(Object? value) =>
    value is String && value.trim().isNotEmpty ? value : null;
String? _number(Object? value) =>
    value is num && value.isFinite ? '$value' : null;
List<String> _strings(Object? value) => value is List
    ? value.whereType<String>().toList(growable: false)
    : const [];
String _flag(Object? value, String yes, String no) => value == true
    ? yes
    : value == false
    ? no
    : '未知';

List<MenuIngredientView>? _baseIngredients(WorkbenchModel model, Object? raw) {
  if (raw is! List) return null;
  return [
    for (final value in raw)
      if (value is String)
        _ingredient(
          model,
          _uniqueCatalog(
            model,
            'ingredients',
            (entry) => entry['name'] == value,
          ),
          fallback: value,
          imageIdentityKnown: false,
        )
      else
        const MenuIngredientView('食材未知', stock: '未知'),
  ];
}

List<MenuIngredientView>? _storedExtras(WorkbenchModel model, Object? raw) {
  if (raw is! List) return null;
  // 原始顺序和重复项都是配方内容，不能使用 Set 或以名称折叠。
  return [
    for (final id in raw)
      _ingredient(
        model,
        _uniqueCatalog(
          model,
          'ingredients',
          (entry) => _validId(id) && entry['id'] == id,
        ),
        fallback: '食材 #${id ?? '未知'}',
      ),
  ];
}

List<MenuIngredientView>? _projectedExtras(WorkbenchModel model, Object? raw) {
  if (raw is! List) return null;
  // C# 已提供完整食材对象，按其 ID 读取库存即可；不能丢弃未知对象而假装不加料。
  return [
    for (final entry in raw)
      _ingredient(model, object(entry), fallback: '食材未知'),
  ];
}

MenuIngredientView _ingredient(
  WorkbenchModel model,
  DataMap entry, {
  required String fallback,
  bool imageIdentityKnown = true,
}) => MenuIngredientView(
  _text(entry['name']) ?? fallback,
  // 基础材料 DTO 只有名称时保留原图标，不能把库存的名称联结当作图片身份。
  entityId: imageIdentityKnown ? entityImageId(entry['id']) : null,
  stock: _stock(model, 'ownedIngredientQty', entry['id']),
);

bool _validId(Object? id) => id is int && id >= 0;

/// 未完整返回的目录无法证明唯一性；只保留原始名称或 ID，不据此联结库存。
DataMap _uniqueCatalog(
  WorkbenchModel model,
  String collection,
  bool Function(DataMap) matches,
) => model.catalog['isComplete'] == false
    ? const {}
    : _unique(model.catalogItems(collection), matches);
String _stock(WorkbenchModel model, String field, Object? id) =>
    menuStockText(_validId(id) ? object(model.runtime[field])['$id'] : null);

String _reasonIngredient(WorkbenchModel model, DataMap row, String rawId) {
  final id = _reasonIngredientId(rawId);
  if (id == null) return '食材 #$rawId';
  final projected = _unique(
    records(row['extraIngredients']),
    (entry) => entry['id'] == id,
  );
  final entry = projected.isNotEmpty
      ? projected
      : _uniqueCatalog(model, 'ingredients', (entry) => entry['id'] == id);
  return _text(entry['name']) ?? '食材 #$rawId';
}

/// JSON对象键只能是字符串；这里仅接收服务端整数ID的规范十进制形式，拒绝正负号、
/// 小数、前导零和名称，防止不同键被宽松解析成同一食材。缺失图片仍使用通用图标。
int? _reasonIngredientId(String value) =>
    RegExp(r'^(0|[1-9][0-9]*)$').hasMatch(value)
    ? entityImageId(int.tryParse(value))
    : null;
