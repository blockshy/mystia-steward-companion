import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/menu_item_content.dart';
import '../ui/steward_style.dart';
import 'service_menu_adapter.dart';
import 'service_plan_placeholder.dart';

/// 订单标题统一占据左列，进度标签只在右列换行；避免把送达状态分散到菜品正文。
/// 右列受实际可用宽度约束，长中文与大字号可以增加高度，不截断、不缩小命中区域。
class OrderHeading extends StatelessWidget {
  const OrderHeading(
    this.title, {
    this.statuses = const [],
    this.leading,
    super.key,
  });
  final String title;
  final List<Widget> statuses;
  final Widget? leading;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) => StewardBaselineRow(
      children: [
        if (leading != null) ...[
          StewardFirstLineCenter(
            textStyle: StewardText.value,
            referenceText: title,
            child: leading!,
          ),
          const SizedBox(width: 6),
        ],
        Expanded(child: Text(title, style: StewardText.value)),
        if (statuses.isNotEmpty) ...[
          const SizedBox(width: 8),
          ConstrainedBox(
            constraints: BoxConstraints(maxWidth: constraints.maxWidth * .6),
            child: Wrap(
              alignment: WrapAlignment.end,
              spacing: 5,
              runSpacing: 5,
              children: statuses,
            ),
          ),
        ],
      ],
    ),
  );
}

/// 送达只能依据现场布尔事实。请求标志不参与推导；缺失字段不能解释为“待送”。
/// 诊断显式展示未知，普通订单只呈现已知事实，均不会凭标签赋予执行权限。
List<Widget> orderProgressTags(DataMap order, {bool includeUnknown = false}) =>
    [
      for (final (field, name) in [
        ('hasServedFood', '料理'),
        ('hasServedBeverage', '酒水'),
      ])
        if (order[field] is bool || includeUnknown)
          OrderTag(
            order[field] == true
                ? '已有$name'
                : order[field] == false
                ? '待送$name'
                : '$name送达待确认',
            kind: order[field] == true
                ? OrderTagKind.success
                : OrderTagKind.request,
          ),
      if (order['hasEvaluated'] == true)
        const OrderTag('已评价', kind: OrderTagKind.success)
      else if (order['readyToEvaluate'] == true)
        const OrderTag('待评价', kind: OrderTagKind.request)
      else if (includeUnknown && order['hasEvaluated'] == false)
        const OrderTag('未评价', kind: OrderTagKind.request),
    ];

/// 普客、稀客以及专注复用相同订单标题与进度映射；桌号只在展示时由零基转一基。
class ServiceOrderHeading extends StatelessWidget {
  const ServiceOrderHeading(
    this.item, {
    required this.rare,
    this.showKind = false,
    this.title,
    super.key,
  });
  final DataMap item;
  final bool rare, showKind;
  final String? title;

  @override
  Widget build(BuildContext context) {
    final order = object(item['order']);
    final desk = order['deskCode'];
    final deskText = desk is int && desk >= 0 ? '${desk + 1}' : label(desk);
    final guestImage = serviceGuestImage(order, rare: rare);
    return OrderHeading(
      title ?? '$deskText 桌 · ${label(order['guestName'])}',
      // 头像身份与订单通道独立：复制稀客也可能提交普通订单。
      // 精确类型或目录身份缺失时保留通用图标，不按订单类型猜测客群图片。
      leading: EntityImage(
        kind: guestImage.kind,
        id: guestImage.id,
        fallback: FLucideIcons.userRound,
        size: StewardImageSize.name,
      ),
      statuses: [
        if (showKind) StatusBadge(rare ? '稀客' : '普客'),
        ...orderProgressTags(order),
        if (order[rare ? 'automationAllowed' : 'canAutomate'] == false)
          const OrderTag('暂不可自动处理', kind: OrderTagKind.warning),
      ],
    );
  }
}

/// 订单卡片只格式化服务端已选定的结果。这里的库存查找仅补充展示数量，
/// 不重新选料理、不拼接酒水候选，也不计算满足点单、预算或特殊经营规则。
class ServiceOrderSummary extends StatelessWidget {
  const ServiceOrderSummary(
    this.model,
    this.item, {
    required this.rare,
    super.key,
  });
  final WorkbenchModel model;
  final DataMap item;
  final bool rare;

  @override
  Widget build(BuildContext context) {
    final order = object(item['order']);
    final plans = records(item['executionPlans']);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (rare)
          _Tags([
            OrderTag(
              '料理：${label(order['foodTag'])}',
              kind: OrderTagKind.request,
            ),
            OrderTag(
              '酒水：${label(order['beverageTag'])}',
              kind: OrderTagKind.request,
            ),
          ])
        else
          _Tags([
            ServiceOriginalOrderTag(
              object(item['originalFood']),
              food: true,
              labelRole: true,
            ),
            ServiceOriginalOrderTag(
              object(item['originalBeverage']),
              food: false,
              labelRole: true,
            ),
          ]),
        ServiceOrderStatus(item, rare: rare),
        const MenuItemDivider(),
        // 订单事实即时显示；尚未返回的推荐使用同结构占位，不把原点料理冒充执行结果。
        // 常态 current/pending 信封变化仍只更新页头；这里仅依据逐单精确联结的待加载状态。
        ServicePlanRegion(
          key: ValueKey('summary-plan-${item['presentationKey']}'),
          pending: item['presentationPending'] == true,
          placeholder: ServicePlanPlaceholder(budget: rare),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              if (item['usesSpecialExecution'] == true)
                const Text('执行方案', style: StewardText.caption),
              if (item['presentationIssue'] is String)
                Text(label(item['presentationIssue']))
              else if (rare && plans.isNotEmpty)
                ServicePlanContent(model, plans.first)
              else if (rare)
                const _Tags([OrderTag('暂无可执行主方案', kind: OrderTagKind.warning)])
              else
                ServiceNormalPlanContent(model, item),
              if (rare) ServiceBudget(item['budget']),
            ],
          ),
        ),
        if (rare && values(item['blockedMessages']).isNotEmpty)
          Padding(
            padding: const EdgeInsets.only(top: 5),
            child: Text(
              '阻断 · ${words(item['blockedMessages'])}',
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
            ),
          ),
      ],
    );
  }
}

/// 原订单的料理/酒水身份来自NormalDetail的显式foodId/beverageId，与执行方案独立。
/// 经营外层、详情及专注共用本组件，防止某一视图只保留名称后漏图或误用替换方案ID。
class ServiceOriginalOrderTag extends StatelessWidget {
  const ServiceOriginalOrderTag(
    this.item, {
    required this.food,
    this.labelRole = false,
    super.key,
  });
  final DataMap item;
  final bool food, labelRole;

  @override
  Widget build(BuildContext context) => OrderTag(
    '${labelRole ? (food ? '原料理：' : '原酒水：') : ''}${label(item['name'])}',
    kind: labelRole
        ? OrderTagKind.request
        : food
        ? OrderTagKind.recipe
        : OrderTagKind.beverage,
    leading: EntityImage(
      kind: food ? EntityImageKind.food : EntityImageKind.beverage,
      id: entityImageId(item[food ? 'foodId' : 'beverageId']),
      fallback: food ? FLucideIcons.utensils : FLucideIcons.glassWater,
    ),
  );
}

/// 订单的业务附注与完整阻断解释。普通送达／评价进度已在标题右侧呈现，不在此重复。
class ServiceOrderStatus extends StatelessWidget {
  const ServiceOrderStatus(
    this.item, {
    required this.rare,
    this.details = false,
    super.key,
  });
  final DataMap item;
  final bool rare;
  final bool details;
  @override
  Widget build(BuildContext context) {
    final order = object(item['order']);
    final block = order[rare ? 'automationBlockReason' : 'actionBlockReason'];
    return _Tags([
      if (label(order['specialBusinessRoleLabel'], '').isNotEmpty)
        OrderTag(
          label(order['specialBusinessRoleLabel']),
          kind: OrderTagKind.request,
        ),
      if (order['isFreeOrder'] == true)
        const OrderTag('免费订单', kind: OrderTagKind.success),
      if (item['hasExecutionOverride'] == true)
        const OrderTag('含替换或加料方案', kind: OrderTagKind.request),
      if (details &&
          order[rare ? 'automationAllowed' : 'canAutomate'] == false &&
          label(block, '').isNotEmpty &&
          block != '暂不可自动处理')
        OrderTag(label(block), kind: OrderTagKind.warning),
    ]);
  }
}

/// 单个主方案或备选必须作为同一个 C# 结果展示；料理与酒水不能分别取独立候选首项。
class ServicePlanContent extends StatelessWidget {
  const ServicePlanContent(
    this.model,
    this.plan, {
    this.details = false,
    this.showBasics = true,
    super.key,
  });
  final WorkbenchModel model;
  final DataMap plan;
  final bool details;
  final bool showBasics;
  @override
  Widget build(BuildContext context) {
    final food = object(plan['food']);
    final beverage = object(plan['beverage']);
    final foodView = serviceFoodView(model, food);
    final beverageView = serviceBeverageView(model, beverage);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // 每个实体的完整标签紧跟自己的名称／主体。主方案外层已有基础资料时，
        // 详情仅重复用于定位的名称，避免把料理标签和酒水标签混成一串无归属的行。
        if (showBasics) MenuItemContent(foodView),
        if (details) MenuItemDetails(foodView, showName: !showBasics),
        // 外层摘要、详情和备选复用同一条分隔线，明确区分成对的料理与酒水；
        // 两者仍取同一个plan，不借展示分组重新挑选或拼接独立候选。
        const MenuItemDivider(),
        if (showBasics) MenuItemContent(beverageView),
        if (details) MenuItemDetails(beverageView, showName: !showBasics),
        if (details) ...[
          _Tags([
            if (food['customRecipe'] == true)
              OrderTag(
                food['customRecipePinned'] == true ? '自定义置顶' : '自定义',
                kind: OrderTagKind.success,
              ),
            if (food['customRecipeScope'] == 'all')
              const OrderTag('全部点单', kind: OrderTagKind.request),
            if (food['meetsRequiredFood'] is bool)
              OrderTag(
                food['meetsRequiredFood'] == true ? '料理满足点单' : '料理偏好备选',
                kind: food['meetsRequiredFood'] == true
                    ? OrderTagKind.success
                    : OrderTagKind.warning,
              ),
            if (beverage['meetsRequiredBeverage'] is bool)
              OrderTag(
                beverage['meetsRequiredBeverage'] == true ? '酒水满足点单' : '酒水偏好备选',
                kind: beverage['meetsRequiredBeverage'] == true
                    ? OrderTagKind.success
                    : OrderTagKind.warning,
              ),
          ]),
          if (plan['estimatedPrice'] != null)
            MenuInfoGroup('方案', [
              MenuMetric('标价', label(plan['estimatedPrice'])),
            ]),
          ServiceBudget(plan['budget']),
          if (values(plan['reasons']).isNotEmpty)
            // reasons 是方案解释，不是菜品标签；保留原句与顺序，以说明段落展示。
            MenuInfoGroup('理由', [
              for (final reason in values(plan['reasons'])) Text(label(reason)),
            ]),
          for (final warning in values(plan['warnings']))
            _Tags([OrderTag(label(warning), kind: OrderTagKind.warning)]),
        ],
      ],
    );
  }
}

/// 普客执行详情与原订单分别展示；执行原因和选择消息是两个不同的服务端字段，不能互相覆盖。
class ServiceNormalPlanContent extends StatelessWidget {
  const ServiceNormalPlanContent(
    this.model,
    this.item, {
    this.details = false,
    this.showBasics = true,
    super.key,
  });
  final WorkbenchModel model;
  final DataMap item;
  final bool details;
  final bool showBasics;
  @override
  Widget build(BuildContext context) {
    final food = object(item['executionFood']);
    final drink = object(item['executionBeverage']);
    final foodView = serviceFoodView(model, food, normal: true);
    final beverageView = serviceBeverageView(model, drink, normal: true);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (showBasics) MenuItemContent(foodView),
        if (details) MenuItemDetails(foodView, showName: !showBasics),
        // 普客与稀客采用同一实体边界，保持订单方案的阅读结构一致。
        const MenuItemDivider(),
        if (showBasics) MenuItemContent(beverageView),
        if (details) MenuItemDetails(beverageView, showName: !showBasics),
        if (details) ...[
          if (label(item['executionReason'], '').isNotEmpty)
            MenuInfoGroup('规则', [Text(label(item['executionReason']))]),
          if (label(item['selectionMessage'], '').isNotEmpty)
            MenuInfoGroup('说明', [Text(label(item['selectionMessage']))]),
        ],
      ],
    );
  }
}

/// 预算数值与超支结果均取自 C#；忽略预算策略只显示预估，不把未知预算显示成零。
class ServiceBudget extends StatelessWidget {
  const ServiceBudget(this.value, {super.key});
  final Object? value;
  @override
  Widget build(BuildContext context) {
    final budget = object(value);
    if (budget.isEmpty) return const SizedBox.shrink();
    final over = budget['overBudget'];
    final text = budget['policy'] == 'ignore'
        ? '预估 ${label(budget['estimatedPrice'])}'
        : budget['willPayMoney'] == false
        ? '预估 ${label(budget['estimatedPrice'])} · 当前不会付款'
        : budget['remainingBudget'] == null
        ? '预估 ${label(budget['estimatedPrice'])} · 预算未知'
        : over is num && over > 0
        ? '预估 ${label(budget['estimatedPrice'])} · 超预算 ${label(over)}'
        : '预估 ${label(budget['estimatedPrice'])} / 预算 ${label(budget['remainingBudget'])}';
    return _Tags([
      OrderTag(
        text,
        kind:
            budget['policy'] != 'ignore' &&
                (budget['willPayMoney'] == false || over is num && over > 0)
            ? OrderTagKind.warning
            : OrderTagKind.request,
      ),
    ]);
  }
}

class _Tags extends StatelessWidget {
  const _Tags(this.children);
  final List<Widget> children;
  @override
  Widget build(BuildContext context) => children.isEmpty
      ? const SizedBox.shrink()
      : Padding(
          padding: const EdgeInsets.only(top: 5),
          child: Wrap(
            spacing: 5,
            runSpacing: 5,
            // 标签长度不同而换行时，同行短标签仍与长标签的首行对齐。
            crossAxisAlignment: WrapCrossAlignment.start,
            children: children,
          ),
        );
}
