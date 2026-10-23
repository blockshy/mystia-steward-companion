import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/menu_item_content.dart';
import '../ui/steward_style.dart';
import '../ui/steward_opacity.dart';
import 'menu_recipe_media.dart';
import 'service_order_content.dart';

/// 自动化标题使用整行身份，状态与当前阶段另起一行。诊断比普通订单多出重试、
/// 阻断和现场反馈，不能继续挤入标题右侧的有限宽度；状态优先级仍只映射已有事实。
class AutomationOrderHeading extends StatelessWidget {
  const AutomationOrderHeading(this.item, {super.key});
  final DataMap item;

  @override
  Widget build(BuildContext context) {
    final (status, statusKind) = item['manualResolutionRequired'] == true
        ? ('需人工处理', OrderTagKind.warning)
        : item['paused'] == true
        ? ('已暂停', OrderTagKind.warning)
        : item['completed'] == true || item['hasEvaluated'] == true
        ? ('已完成', OrderTagKind.success)
        : item['canAutomate'] == false
        ? ('暂不可自动处理', OrderTagKind.warning)
        : item['paused'] == false
        ? ('未暂停', OrderTagKind.request)
        : ('状态待确认', OrderTagKind.request);
    final imageKind = switch (item['guestImageKind']) {
      'rare-guest' => EntityImageKind.rareGuest,
      'normal-group' => EntityImageKind.normalGroup,
      _ => null,
    };
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        EntityName(
          label(item['title'], '当前订单'),
          // 服务端明确图像命名空间后才接受guestImageId；guestId可能来自不同客人结构，
          // 更不能从kind/title/runtimeGuestId推断图像身份。
          kind: imageKind ?? EntityImageKind.rareGuest,
          id: imageKind == null ? null : entityImageId(item['guestImageId']),
          fallback: FLucideIcons.userRound,
          style: StewardText.value,
        ),
        MenuInfoGroup('阶段', [
          OrderTag(status, kind: statusKind),
          OrderTag(
            label(item['stepLabel'], '阶段待确认'),
            kind: OrderTagKind.request,
          ),
        ]),
      ],
    );
  }
}

/// 自动化诊断的公共展示组件，字段严格来自AutomationCoordinator.Status投影。
/// compact用于列表摘要，完整模式按当前阶段、原因、现场反馈和高级诊断分组。
/// 回调是否可用由业务页面授权；组件不重试、不补写状态、不把请求等同于已送达。
class AutomationStatusContent extends StatelessWidget {
  const AutomationStatusContent(
    this.item, {
    this.compact = false,
    this.headerInParent = false,
    this.model,
    this.onRetry,
    this.onReset,
    super.key,
  });
  final DataMap item;
  final bool compact;
  final bool headerInParent;

  /// 只读取当前目录和已校验的资源依赖；不触发查询、不选择方案、不访问文件或网络。
  final WorkbenchModel? model;
  final VoidCallback? onRetry;
  final VoidCallback? onReset;

  @override
  Widget build(BuildContext context) {
    final message = label(item['detailMessage'], '');
    final next = label(item['nextAction'], '');
    final error = label(item['lastError'], '');
    final block = label(item['actionBlockReason'], '');
    final food = automationFoodView(model, item);
    final beverage = automationBeverageView(item);
    if (compact) {
      return Semantics(
        container: true,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (!headerInParent) AutomationOrderHeading(item),
            MenuInfoGroup('进度', orderProgressTags(item, includeUnknown: true)),
            if (food != null)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: MenuItemName(food, style: StewardText.caption),
              ),
            if (beverage != null)
              Padding(
                padding: const EdgeInsets.only(top: 5),
                child: MenuItemName(beverage, style: StewardText.caption),
              ),
            if (message.isNotEmpty || next.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text(
                  message.isNotEmpty ? message : next,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: 12,
                    color: context.theme.colors.mutedForeground,
                  ),
                ),
              ),
          ],
        ),
      );
    }
    return Semantics(
      container: true,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Section('自动化状态', [
            AutomationOrderHeading(item),
            MenuInfoGroup('进度', orderProgressTags(item, includeUnknown: true)),
            MenuInfoGroup('统计', [
              MenuMetric(
                '阶段耗时',
                item['stepSeconds'] == null ? '未知' : '${item['stepSeconds']} 秒',
              ),
              MenuMetric('重试', label(item['retryCount'], '未知')),
              MenuMetric('回滚', label(item['rollbackCount'], '未知')),
            ]),
            if (next.isNotEmpty)
              AutomationDetailItem('接下来', next, icon: FLucideIcons.arrowRight),
            if (message.isNotEmpty) AutomationDetailItem('当前说明', message),
            if (item['canAutomate'] == false && block.isNotEmpty)
              AutomationDetailItem('阻断原因', block, warning: true),
            if (error.isNotEmpty)
              AutomationDetailItem('最近错误', error, warning: true),
            if (item['manualResolutionRequired'] == true)
              const AutomationDetailItem(
                '人工处理',
                '请先核对游戏现场，再处理对应的人工确认事项。该订单不能通过普通重试恢复。',
                warning: true,
              ),
            const SizedBox(height: 8),
            ToolRow([
              StewardButton('重试暂停订单', onPressed: onRetry),
              StewardButton('重置规划状态', onPressed: onReset),
            ]),
            if (label(item['resetBlockReason'], '').isNotEmpty)
              AutomationDetailItem('重置限制', label(item['resetBlockReason'])),
          ]),
          if (food != null || beverage != null)
            Section('处理目标', [
              // 使用编排器锁定目标，而非最新推荐候选。保留重复材料和未知值，
              // 避免把暂停前的执行目标悄悄换成当前最高分方案。
              if (food != null && item['targetSource'] == 'locked')
                const AutomationDetailItem('料理来源', '已锁定的执行方案'),
              if (food != null && item['targetSource'] == 'ordered-recipe')
                const AutomationDetailItem('料理来源', '原点料理的目录配方'),
              if (food != null) MenuItemContent(food),
              if (food != null && beverage != null) const MenuItemDivider(),
              if (beverage != null) MenuItemContent(beverage),
            ]),
          if ((item['foodDeliveryRequested'] == true &&
                  item['hasServedFood'] != true) ||
              (item['beverageDeliveryRequested'] == true &&
                  item['hasServedBeverage'] != true))
            Section('现场反馈', [
              // 请求标志仅代表编排器已发出指令；只有hasServed*才是现场确认。
              if (item['foodDeliveryRequested'] == true &&
                  item['hasServedFood'] != true)
                const AutomationDetailItem('料理请求', '已请求送达，等待现场确认。'),
              if (item['beverageDeliveryRequested'] == true &&
                  item['hasServedBeverage'] != true)
                const AutomationDetailItem('酒水请求', '已请求送达，等待现场确认。'),
            ]),
          CollapsibleSection('高级诊断', [
            AutomationDetailItem('订单标识', label(item['orderKey'])),
            AutomationDetailItem('捕获来源', label(item['source'], '未知')),
            AutomationDetailItem('追踪标识', label(item['traceId'], '未知')),
            AutomationDetailItem(
              '最近运行事件',
              label(item['lastRuntimeEventSequence'], '未知'),
            ),
            if (item['controllerAvailable'] is bool)
              AutomationDetailItem(
                '订单控制器',
                item['controllerAvailable'] == true ? '可用' : '不可用',
              ),
          ]),
        ],
      ),
    );
  }
}

/// 将诊断中的处理目标投影为公共展示模型。Food.id 与独立 Recipe.id 同时验证后，才能读取
/// 当前资源代次的基础材料/厨具依赖；原始加料始终按显式 ID 保留顺序及重复项。
/// 缺目录或重复目录只影响名称补全，不按名称反查图片、不重新计算配方或生效标签。
MenuItemView? automationFoodView(WorkbenchModel? model, DataMap item) {
  final foodId = entityImageId(item['foodId']);
  final recipeId = entityImageId(item['recipeId']);
  final name = label(item['recipeName'], '');
  if (foodId == null && name.isEmpty) return null;
  final key = model == null
      ? null
      : menuRecipeMediaKey(model, {'id': foodId, 'recipeId': recipeId});
  final recipe = key == null || model == null
      ? const <String, Object?>{}
      : _uniqueEntity(model, 'recipes', foodId);
  final dependency = key == null || model == null
      ? const <String, Object?>{}
      : model.gameMediaRecipes[key] ?? const <String, Object?>{};
  final ingredients = _ingredientViews(model, dependency['ingredientIds']);
  return MenuItemView(
    kind: MenuItemKind.food,
    entityId: foodId,
    name: name.isNotEmpty
        ? name
        : label(recipe['name'], foodId == null ? '料理待确认' : '料理 #$foodId'),
    cooker: label(item['cookerName'], label(recipe['cooker'], '厨具未知')),
    cookerId: model == null ? null : menuRecipeCookerImage(model, key),
    ingredients: ingredients,
    extras: _ingredientViews(model, item['extraIngredientIds']),
  );
}

/// 酒水名称和ID由同一处理目标投影；仅有名称仍显示通用图标，绝不联结同名目录项。
MenuItemView? automationBeverageView(DataMap item) {
  final id = entityImageId(item['beverageId']);
  final name = label(item['beverageName'], '');
  if (id == null && name.isEmpty) return null;
  return MenuItemView(
    kind: MenuItemKind.beverage,
    entityId: id,
    name: name.isNotEmpty ? name : '酒水 #$id',
  );
}

DataMap _uniqueEntity(WorkbenchModel? model, String catalog, int? id) {
  if (model == null || model.catalog['isComplete'] == false || id == null)
    return const {};
  final matches = model
      .catalogItems(catalog)
      .where((entry) => entityImageId(entry['id']) == id)
      .take(2)
      .toList();
  return matches.length == 1 ? matches.single : const {};
}

List<MenuIngredientView>? _ingredientViews(WorkbenchModel? model, Object? raw) {
  if (raw is! List) return null;
  return [
    for (final value in raw)
      (() {
        final id = entityImageId(value);
        final entity = _uniqueEntity(model, 'ingredients', id);
        return MenuIngredientView(
          label(entity['name'], id == null ? '材料未知' : '材料 #$id'),
          entityId: id,
        );
      })(),
  ];
}

/// 说明使用左标签、右内容的首行基线，长文字仅在内容列换行。普通字段不再各自占用
/// 一块背景卡；警告保留图标和明确标题，不靠颜色单独传达含义。列宽随文字缩放，
/// 不设置固定高度，因此窄屏和大字号都能完整阅读且保持透明度规则。
class AutomationDetailItem extends StatelessWidget {
  const AutomationDetailItem(
    this.title,
    this.value, {
    this.warning = false,
    this.icon = FLucideIcons.info,
    super.key,
  });
  final String title, value;
  final bool warning;
  final IconData icon;
  @override
  Widget build(BuildContext context) {
    final dark = context.theme.colors.background.computeLuminance() < .3;
    final tone = warning
        ? Color(dark ? 0xffffc195 : 0xff965230)
        : context.theme.colors.primary;
    final titleStyle = StewardText.caption.copyWith(
      fontWeight: FontWeight.w600,
      color: context.stewardPaint.foreground(tone),
    );
    return Semantics(
      container: true,
      child: Padding(
        padding: const EdgeInsets.only(top: 8),
        child: StewardBaselineRow(
          children: [
            SizedBox(
              width: MediaQuery.textScalerOf(context).scale(52),
              child: Text(title, style: titleStyle),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: StewardBaselineRow(
                children: [
                  StewardFirstLineCenter(
                    textStyle: titleStyle,
                    referenceText: title,
                    child: Icon(
                      warning ? FLucideIcons.triangleAlert : icon,
                      size: 14,
                      color: context.stewardPaint.foreground(tone),
                    ),
                  ),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(value, style: const TextStyle(height: 1.4)),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
