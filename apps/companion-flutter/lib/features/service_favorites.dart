import 'package:flutter/widgets.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import 'favorites_selection.dart';

/// 稀客经营方案的两个收藏动作直接保存用户正在看的配对实体；不会重新查询、评分或
/// 从独立候选列表选择“首项”。普通客群没有稀客收藏键，故调用方只在稀客方案显示。
class ServiceFavoriteActions extends StatefulWidget {
  const ServiceFavoriteActions(this.model, this.item, this.plan, {super.key});
  final WorkbenchModel model;
  final DataMap item, plan;
  @override
  State<ServiceFavoriteActions> createState() => _ServiceFavoriteActionsState();
}

class _ServiceFavoriteActionsState extends State<ServiceFavoriteActions> {
  bool _pending = false;

  /// 收藏虽然只写配置，也必须属于当前设备、会话和正在浏览的订单，不能让旧按钮借用
  /// 新主设备身份。边界只包含稳定标量，不依赖会被轮询原地替换的 Map 引用。
  Object get _boundary => (
    widget.model,
    widget.model.connectionVersion,
    widget.model.authority['registryId'],
    widget.model.authority['currentDeviceId'],
    widget.model.authority['primaryDeviceId'],
    widget.model.authority['authorityRevision'],
    widget.model.snapshot['automationSessionId'],
    widget.model.snapshot['nightBusinessGeneration'],
  );

  Object? _orderIdentity(DataMap item) {
    final order = object(item['order']);
    final trace = order['traceId'];
    final lifecycle = order['orderLifecycleSequence'];
    final customer = favoriteEntityId(object(item['customer'])['id']);
    if (trace is! String ||
        trace.isEmpty ||
        lifecycle is! int ||
        customer == null)
      return null;
    return (trace, lifecycle, customer, order['foodTag'], order['beverageTag']);
  }

  Object _planIdentity(DataMap plan) {
    final food = object(plan['food']);
    return (
      object(food['recipe'])['id'],
      safeFavoriteExtraIds(food['extraIngredients'], entities: true)?.join(','),
      object(object(plan['beverage'])['beverage'])['id'],
    );
  }

  @override
  Widget build(BuildContext context) {
    final customer = object(widget.item['customer']);
    final customerId = favoriteEntityId(customer['id']);
    // 精确客户身份必须来自 C# 规范化的 customer；不能用复制客人的 runtimeGuestId 猜测。
    if (customerId == null) return const SizedBox.shrink();
    final order = object(widget.item['order']);
    final favorites =
        widget.model.resources['/favorites'] ?? const <String, Object?>{};
    return ToolRow([
      for (final food in [true, false])
        Builder(
          builder: (context) {
            final row = object(widget.plan[food ? 'food' : 'beverage']);
            final tag = label(order[food ? 'foodTag' : 'beverageTag'], '');
            final match = recommendationFavorite(
              favorites,
              row,
              food: food,
              customerId: customerId,
              tag: tag,
            );
            final text =
                '${match.id == null ? '收藏' : '取消收藏'}${food ? '料理' : '酒水'}';
            final boundary = _boundary;
            final orderIdentity = _orderIdentity(widget.item);
            final planIdentity = _planIdentity(widget.plan);
            return StewardButton(
              text,
              onPressed:
                  !_pending &&
                      widget.model.canEdit &&
                      match.known &&
                      orderIdentity != null &&
                      widget.item['presentationPending'] != true
                  ? () async {
                      if (!mounted ||
                          _pending ||
                          !widget.model.canEdit ||
                          !TickerMode.valuesOf(context).enabled ||
                          boundary != _boundary ||
                          orderIdentity != _orderIdentity(widget.item) ||
                          planIdentity != _planIdentity(widget.plan))
                        return;
                      // 组件尚未重建时模型可能已收到新订单/方案。直接核对当前展示投影，
                      // 丢失、歧义、待刷新或配方改变均停止；不使用之前缓存的展示闭包写入。
                      final currentOrders =
                          records(widget.model.serviceOrders['recommendations'])
                              .where(
                                (item) => _orderIdentity(item) == orderIdentity,
                              )
                              .toList();
                      if (currentOrders.length != 1 ||
                          currentOrders.single['presentationPending'] == true ||
                          !records(
                            currentOrders.single['executionPlans'],
                          ).any((plan) => _planIdentity(plan) == planIdentity))
                        return;
                      final currentMatch = recommendationFavorite(
                        widget.model.resources['/favorites'] ?? const {},
                        row,
                        food: food,
                        customerId: customerId,
                        tag: tag,
                      );
                      // 收藏轮询改变后不能将用户点的“收藏”悄悄变成“取消收藏”，反之亦然。
                      if (!currentMatch.known || currentMatch != match) return;
                      setState(() => _pending = true);
                      try {
                        final entity = object(
                          row[food ? 'recipe' : 'beverage'],
                        );
                        await widget.model.mutate(
                          '/favorites/${match.id == null ? 'add' : 'remove'}-${food ? 'recipe' : 'beverage'}',
                          query: match.id != null
                              ? {'id': match.id!}
                              : {
                                  'customerId': '$customerId',
                                  'customerName': label(customer['name'], ''),
                                  if (food) ...{
                                    'foodTag': tag,
                                    'recipeId': '${entity['id']}',
                                    'extraIngredientIds': safeFavoriteExtraIds(
                                      row['extraIngredients'],
                                      entities: true,
                                    )!.join(','),
                                  } else ...{
                                    'beverageTag': tag,
                                    'beverageId': '${entity['id']}',
                                  },
                                },
                        );
                      } finally {
                        if (mounted) setState(() => _pending = false);
                      }
                    }
                  : null,
            );
          },
        ),
    ]);
  }
}
