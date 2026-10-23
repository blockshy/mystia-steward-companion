import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import 'components.dart';
import 'entity_image.dart';
import 'semantic_tag.dart';
import 'steward_style.dart';

/// 纯展示类别：页面适配服务端数据后交给组件，不在组件内决定候选、评分或执行顺序。
enum MenuItemKind { food, beverage }

/// 保留材料顺序与重复项。stock 是已经格式化的观测库存，不是配方用量。
class MenuIngredientView {
  const MenuIngredientView(this.name, {this.stock, this.entityId});
  final String name;
  final String? stock;

  /// 只接收显式食材 ID 或严格配方依赖中的 ID，禁止由名称反推图片身份。
  final int? entityId;
}

/// 服务端已给出的价格、成本或匹配统计；组件只排版，绝不从售价自行计算利润。
class MenuItemFact {
  const MenuItemFact(this.label, this.value);
  final String label;
  final String value;
}

/// 经营、推荐、收藏共用的展示数据。null材料表示未知，空列表表示服务端明确给出空值；
/// 二者不能互换。标签与解释独立保存，详情只读这一部分即可避免重复整个外层卡片。
class MenuItemView {
  const MenuItemView({
    required this.kind,
    required this.name,
    this.entityId,
    this.cookerId,
    this.cooker,
    this.ingredients,
    this.extras,
    this.stock,
    this.facts = const [],
    this.summaryTagLabel,
    this.summaryTags = const [],
    this.activeTags = const [],
    this.suppressedTags = const [],
    this.targetTags = const [],
    this.notes = const [],
    this.warnings = const [],
  });
  final MenuItemKind kind;
  final String name;

  /// 料理使用 Food.id；与独立的游戏 Recipe.id 不同。酒水使用 Beverage.id。
  final int? entityId;

  /// 厨具只使用配方依赖中的 cookerType，不使用摆放位置或升级厨具实体编号。
  final int? cookerId;
  final String? cooker;
  final List<MenuIngredientView>? ingredients;
  final List<MenuIngredientView>? extras;
  final String? stock;
  final List<MenuItemFact> facts;

  /// 外层标签的语义由适配层明确给出，例如“匹配”“生效”“标签”，不能混称。
  /// 只截取视觉摘要，完整列表仍存于此处并向读屏提供，不改变详情原有完整解释。
  final String? summaryTagLabel;
  final List<String> summaryTags;
  final List<String> activeTags, suppressedTags, targetTags, notes, warnings;
}

/// 库存哨兵严格沿用协议：只有-1代表无限；缺失、其他负数、非有限值均为未知。
/// 不把未知库存补成0，也不把库存值写成“×数量”而误导为配方材料用量。
String menuStockText(Object? quantity) {
  if (quantity == -1) return '无限';
  return quantity is num && quantity.isFinite && quantity >= 0
      ? '$quantity'
      : '未知';
}

/// 单份料理或酒水的完整外层内容。名称只出现一次；类别、厨具、材料使用稳定视觉语义，
/// 数值统计使用与材料等高的语义标签。没有整块不透明背景，继续透出用户设置的Windows透明背景。
class MenuItemContent extends StatelessWidget {
  const MenuItemContent(
    this.item, {
    this.showName = true,
    this.showDetails = false,
    this.showExtras = true,
    this.summaryTagLimit = 4,
    super.key,
  }) : assert(summaryTagLimit == null || summaryTagLimit > 0);
  final MenuItemView item;
  final bool showName;
  final bool showDetails;

  /// 静态料理图鉴只描述基础配方，不存在本次加料决策；隐藏该行以免伪造“不加料”方案。
  /// 经营和推荐保留默认值，继续完整展示服务端实际给出的加料信息。
  final bool showExtras;

  /// 列表默认最多外显四项；独立图鉴详情传 null，在原位置一次展示完整标签，
  /// 避免顶部摘要和下方完整列表重复。此限制只影响排版，不裁剪原始数据或读屏内容。
  final int? summaryTagLimit;

  @override
  Widget build(BuildContext context) => Semantics(
    // 独立边界使可滚动列表隐藏/重新显示时不与相邻菜品隐式合并语义缓存。
    container: true,
    child: Padding(
      padding: const EdgeInsets.only(top: 6, bottom: 3),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (showName) MenuItemName(item),
          if (item.stock != null || item.facts.isNotEmpty)
            // 与厨具、配方和加料共用前缀与换行规则；“指标”同时涵盖价格、库存和匹配数。
            MenuInfoGroup('指标', [
              if (item.stock != null) MenuMetric('库存', item.stock!),
              for (final fact in item.facts) MenuMetric(fact.label, fact.value),
            ]),
          if (item.summaryTags.isNotEmpty)
            Semantics(
              container: true,
              excludeSemantics: true,
              label:
                  '${item.summaryTagLabel ?? '标签'}：${item.summaryTags.join('、')}',
              child: MenuInfoGroup(item.summaryTagLabel ?? '标签', [
                // 匹配、生效、静态标签分别使用已有语义颜色与图标，不能只靠颜色区分。
                // 列表按限制展示摘要；详情可取消限制，完整内容始终保留在语义中。
                for (final tag in item.summaryTags.take(
                  summaryTagLimit ?? item.summaryTags.length,
                ))
                  OrderTag(
                    tag,
                    kind: switch (item.summaryTagLabel) {
                      '匹配' => OrderTagKind.success,
                      '标签' => OrderTagKind.ingredient,
                      _ => OrderTagKind.request,
                    },
                  ),
                if (summaryTagLimit != null &&
                    item.summaryTags.length > summaryTagLimit!)
                  // 摘要尾项也使用同尺寸标签，避免裸文字与带内边距标签的
                  // 首行基线不同，连带把左侧前缀向上拉偏。
                  SemanticTag(
                    text: '+${item.summaryTags.length - summaryTagLimit!}',
                    semanticLabel:
                        '其余${item.summaryTags.length - summaryTagLimit!}项请查看详情',
                    tone: context.theme.colors.mutedForeground,
                  ),
              ]),
            ),
          if (item.kind == MenuItemKind.food) ...[
            MenuInfoGroup('厨具', [
              OrderTag(
                item.cooker ?? '厨具未知',
                kind: OrderTagKind.cooker,
                leading: EntityImage(
                  kind: EntityImageKind.cooker,
                  id: item.cookerId,
                  fallback: FLucideIcons.cookingPot,
                ),
              ),
            ]),
            MenuInfoGroup('配方', _materials(item.ingredients, false)),
            if (showExtras) MenuInfoGroup('加料', _materials(item.extras, true)),
          ],
          if (showDetails) MenuItemDetails(item),
        ],
      ),
    ),
  );

  List<Widget> _materials(List<MenuIngredientView>? items, bool extra) {
    final kind = extra ? OrderTagKind.extra : OrderTagKind.ingredient;
    if (items == null) {
      return [OrderTag(extra ? '加料未知' : '基础配方未知', kind: kind)];
    }
    if (items.isEmpty) {
      return [OrderTag(extra ? '不加料' : '无基础材料', kind: kind)];
    }
    return [
      // 不使用Set、不按名称合并：服务端的两份相同材料仍显示为两项。
      for (final ingredient in items)
        OrderTag(
          ingredient.stock == null
              ? ingredient.name
              : '${ingredient.name} · ${ingredient.stock == '未知' || ingredient.stock == '无限' ? '库存' : '库存 '}${ingredient.stock}',
          kind: kind,
          leading: EntityImage(
            kind: EntityImageKind.ingredient,
            id: ingredient.entityId,
            fallback: extra ? FLucideIcons.plus : FLucideIcons.leaf,
          ),
        ),
    ];
  }
}

/// 详情增量区域：只展示标签变化与服务端解释，不重复名称、厨具、材料和统计。
/// 调用方可在窄屏详情前添加简短名称定位；主方案与备选的业务关系仍由页面维护。
class MenuItemDetails extends StatelessWidget {
  const MenuItemDetails(
    this.item, {
    this.showName = false,
    this.showSummaryTags = true,
    super.key,
  });
  final MenuItemView item;
  final bool showName;

  /// 仅当调用方已在同一详情面板完整展示这些标签时关闭；其他增量详情仍显示全量。
  /// 生效、目标、抵消及说明各有独立语义，不能随静态标签一同隐藏。
  final bool showSummaryTags;
  @override
  Widget build(BuildContext context) => Semantics(
    container: true,
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (showName) MenuItemName(item),
        // 外层只显示四项摘要，详情必须保留全部。只有来源明确为“生效”且与
        // activeTags逐项一致时才避免重复；“匹配/静态标签”绝不能被当成生效标签。
        if (showSummaryTags &&
            item.summaryTags.isNotEmpty &&
            !(item.summaryTagLabel == '生效' &&
                _sameTags(item.summaryTags, item.activeTags)))
          MenuTagGroup(
            item.summaryTagLabel ?? '标签',
            item.summaryTags,
            kind: switch (item.summaryTagLabel) {
              '匹配' => OrderTagKind.success,
              '标签' => OrderTagKind.ingredient,
              _ => OrderTagKind.request,
            },
          ),
        if (item.activeTags.isNotEmpty) MenuTagGroup('标签', item.activeTags),
        if (item.targetTags.isNotEmpty)
          MenuTagGroup('目标', item.targetTags, kind: OrderTagKind.success),
        if (item.suppressedTags.isNotEmpty)
          MenuTagGroup(
            '抵消',
            item.suppressedTags.map((tag) => '已抵消 $tag').toList(),
            kind: OrderTagKind.warning,
          ),
        if (item.notes.isNotEmpty)
          MenuInfoGroup('说明', [
            for (final note in item.notes) Text(note, style: StewardText.body),
          ]),
        if (item.warnings.isNotEmpty)
          MenuInfoGroup('注意', [
            for (final warning in item.warnings)
              OrderTag(warning, kind: OrderTagKind.warning),
          ]),
      ],
    ),
  );
}

bool _sameTags(List<String> left, List<String> right) =>
    left.length == right.length &&
    Iterable<int>.generate(left.length)
        .every((index) => left[index] == right[index]);

/// 名称与图标在外层和增量详情中共用同一行。图标按真实字体首行居中，长名称
/// 换行时不会把图标居中到整段高度，也不靠固定top偏移补偿字号。
class MenuItemName extends StatelessWidget {
  const MenuItemName(this.item, {this.style = StewardText.value, super.key});
  final MenuItemView item;
  // 标题所在区域继续决定原有文字级别；共用名称组件不把列表13px提升成分组15px。
  final TextStyle style;
  @override
  Widget build(BuildContext context) => IconTheme.merge(
    data: IconThemeData(color: context.theme.colors.primary),
    child: EntityName(
      item.name,
      kind: item.kind == MenuItemKind.food
          ? EntityImageKind.food
          : EntityImageKind.beverage,
      id: item.entityId,
      fallback: item.kind == MenuItemKind.food
          ? FLucideIcons.utensils
          : FLucideIcons.glassWater,
      style: style,
    ),
  );
}

/// 标签在外层和详情中采用相同尺寸及语义色。标签顺序与重复项来自服务端，
/// 不在展示层去重或计算匹配；调用方使用两字前缀，完整含义由文字和语义表达。
class MenuTagGroup extends StatelessWidget {
  const MenuTagGroup(
    this.label,
    this.tags, {
    this.kind = OrderTagKind.request,
    super.key,
  });
  final String label;
  final List<String> tags;
  final OrderTagKind kind;
  @override
  Widget build(BuildContext context) => tags.isEmpty
      ? const SizedBox.shrink()
      : MenuInfoGroup(label, [
          for (final tag in tags) OrderTag(tag, kind: kind),
        ]);
}

/// 普通方案的结构分隔不再占用一整行“基础方案”标题；使用主题边界色的细线，
/// 不铺不透明背景，既保留分区也保留Windows用户配置的透明效果。
class MenuItemDivider extends StatelessWidget {
  const MenuItemDivider({super.key});
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 7),
    child: SizedBox(
      width: double.infinity,
      height: 1,
      child: ColoredBox(color: context.theme.colors.border),
    ),
  );
}

/// 菜品与自动化诊断共用的紧凑指标标签。数值来自调用方，不根据标价重算成本、利润、
/// 匹配率或状态；颜色只标记指标类别，不把未知数值解释为成功或失败。
/// 与OrderTag共用尺寸规范，仅将数值适度加粗；保留半透明底色和窄屏自然换行。
class MenuMetric extends StatelessWidget {
  const MenuMetric(this.name, this.value, {super.key});
  final String name, value;
  @override
  Widget build(BuildContext context) {
    final dark = context.theme.colors.background.computeLuminance() < .3;
    final (icon, light, night) = switch (name) {
      '标价' || '方案标价' => (FLucideIcons.coins, 0xff8b5236, 0xffe9b99a),
      '材料成本' ||
      '基础成本' ||
      '加料成本' => (FLucideIcons.shoppingBasket, 0xff805d25, 0xffe5cb91),
      '利润' => (FLucideIcons.chartNoAxesCombined, 0xff30694e, 0xff9fddbb),
      '匹配标签' ||
      '匹配标签数' ||
      '命中标签数' ||
      '覆盖客人数' => (FLucideIcons.tags, 0xff276a74, 0xff95d5d4),
      '库存' => (FLucideIcons.package, 0xff4f6377, 0xffb1cbe4),
      '阶段耗时' => (FLucideIcons.timer, 0xff4f6377, 0xffb1cbe4),
      '重试' || '回滚' => (FLucideIcons.rotateCcw, 0xff71538b, 0xffd1baec),
      _ => (FLucideIcons.info, 0xff4f6377, 0xffb1cbe4),
    };
    final tone = Color(dark ? night : light);
    return SemanticTag(
      text: '$name ',
      emphasizedSuffix: value,
      semanticLabel: '$name $value',
      icon: icon,
      tone: tone,
    );
  }
}

/// 类别前缀与标签分列，标签续行始终与首行对齐，不回到左侧前缀下方。
/// 前缀列按两个汉字的缩放字号预留宽度；较长名称自然换行，不限制整组高度。
class MenuInfoGroup extends StatelessWidget {
  const MenuInfoGroup(this.name, this.children, {super.key});
  final String name;
  final List<Widget> children;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: 5),
    child: StewardBaselineRow(
      children: [
        SizedBox(
          width: MediaQuery.textScalerOf(context).scale(12) * 2,
          // RenderWrap暴露首行实际基线，标签自己的边框与内边距自然参与对齐。
          // 不再手动补top:6；文本、指标、标签以及大字号均遵循同一条基线。
          child: Text(
            name,
            style: StewardText.caption.copyWith(
              color: context.theme.colors.mutedForeground,
            ),
          ),
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Wrap(
            spacing: 5,
            runSpacing: 5,
            crossAxisAlignment: WrapCrossAlignment.start,
            children: children,
          ),
        ),
      ],
    ),
  );
}
