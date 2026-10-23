import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/menu_item_content.dart';
import '../ui/page_activity.dart';
import '../ui/steward_style.dart';
import '../ui/steward_opacity.dart';
import 'service_menu_adapter.dart';
import 'service_order_content.dart';
import 'service_plan_placeholder.dart';
import 'service_order_highlight.dart';
import 'service_plan_activity.dart';
import 'service_favorites.dart';

/// 专注页的开关全部是本机显示偏好，不进入共享 profile，不影响推荐、执行或租约。
/// 顶层布尔字段通过既有 updateLocal 逐字段合并及持久化，避免保存整份旧偏好覆盖他项。
const serviceFocusPreferences = <String, String>{
  'serviceFocusShowRare': '显示稀客',
  'serviceFocusShowNormal': '显示普客',
  'serviceFocusShowOrderTags': '显示点单需求',
  'serviceFocusShowFood': '显示料理',
  'serviceFocusShowIngredients': '显示基础配方',
  'serviceFocusShowExtras': '显示加料',
  'serviceFocusShowCooker': '显示厨具',
  'serviceFocusShowBeverage': '显示酒水',
};

/// 窗口外框与订单字段分开存储，避免“隐藏窗口 UI”被误算成隐藏所有制作信息。
/// 未设置时默认显示；仅在经营专注页生效，切换其它主页面后恢复完整导航。
const serviceFocusWindowUiPreference = 'serviceFocusShowWindowUi';

/// 仅专注页的本机分组偏好。缺失/异常值默认关闭，不回写共享 profile，
/// 不改变普/稀客执行竞争；每组内部仍严格消费服务端点单显示顺位。
const serviceFocusRareFirstPreference = 'serviceFocusRareFirst';

/// 收藏按钮仅影响本机专注页的辅助操作区，默认保留现有显示；不计入制作信息字段。
/// 隐藏按钮不移除已保存收藏，也不改变推荐权重或其它页面的收藏入口。
const serviceFocusFavoritesPreference = 'serviceFocusShowFavorites';

bool serviceFocusShowsWindowUi(WorkbenchModel model) =>
    model.localPreferences[serviceFocusWindowUiPreference] != false;

/// 面向做菜的只读投影：只接受经营结果中的实际 order 及同一主方案，不从目录、
/// 独立推荐候选或备选重新拼搭配。收藏仅保存当前精确搭配，不提供完成订单、清缓存或自动化写入动作。
class ServiceFocusPage extends StatefulWidget {
  const ServiceFocusPage(this.model, {this.active = true, super.key});
  final WorkbenchModel model;
  final bool active;

  @override
  State<ServiceFocusPage> createState() => _ServiceFocusPageState();
}

class _ServiceFocusPageState extends State<ServiceFocusPage> {
  final _scroll = ScrollController();
  String? _saving;
  String? _failure;
  WorkbenchModel get model => widget.model;

  // 缺失或历史异常值使用明确默认值，不把字符串 "false" 当成已保存布尔偏好。
  bool _show(String key) => model.localPreferences[key] is bool
      ? model.localPreferences[key] as bool
      : true;

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  Future<void> _save(String key, bool value) async {
    if (!mounted || _saving != null) return;
    setState(() {
      _saving = key;
      _failure = null;
    });
    try {
      await model.updateLocal({key: value});
    } catch (error) {
      if (mounted) setState(() => _failure = '显示偏好未保存：$error');
    } finally {
      if (mounted) setState(() => _saving = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final liveOrders = model.serviceOrders;
    final selected = <({bool rare, DataMap item})>[
      if (_show('serviceFocusShowRare'))
        for (final item in records(liveOrders['recommendations']))
          if (object(item['order']).isNotEmpty) (rare: true, item: item),
      if (_show('serviceFocusShowNormal'))
        for (final item in records(liveOrders['normalOrderDetailPlans']))
          if (object(item['order']).isNotEmpty) (rare: false, item: item),
    ];
    // 混合视图只消费服务端已计算的全局点单顺位；不在 Dart 重写时间排序或特殊客人规则。
    // 缺失/非法顺位的行保留输入相对次序放在末尾，保证旧离线夹具也不会猜测订单时间。
    final rareFirst =
        model.localPreferences[serviceFocusRareFirstPreference] == true;
    final indexed = selected.indexed.toList()
      ..sort((a, b) {
        if (rareFirst && a.$2.rare != b.$2.rare) return a.$2.rare ? -1 : 1;
        final left = object(a.$2.item['order'])['serviceOrderPosition'];
        final right = object(b.$2.item['order'])['serviceOrderPosition'];
        final leftValid = left is int && left >= 0;
        final rightValid = right is int && right >= 0;
        if (leftValid && rightValid) {
          final position = left.compareTo(right);
          if (position != 0) return position;
        } else if (leftValid != rightValid) {
          return leftValid ? -1 : 1;
        }
        return a.$1.compareTo(b.$1);
      });
    final displayed = [for (final entry in indexed) entry.$2];
    final barriers = records(model.automation['safetyBarriers']);
    final visibleFields = serviceFocusPreferences.keys.skip(2).any(_show);
    final special = object(model.snapshot['specialBusiness']);
    final planActivity = servicePlanActivity(
      connected: model.connected,
      business: model.business,
      visibleOrders: selected.map((row) => row.item),
    );
    final messages = <String>[
      if (_failure != null) _failure!,
      if (_saving != null) '正在保存显示偏好…',
      if (!model.connected)
        '连接后查看实际经营订单；断线时不展示缓存方案。'
      else ...[
        if (planActivity != null) planActivity.message,
        if (barriers.isNotEmpty) '需人工处理 ${barriers.length} 项',
        if (!visibleFields) '制作信息已全部隐藏，可在“专注显示”中重新开启。',
        if (selected.isEmpty)
          !_show('serviceFocusShowRare') && !_show('serviceFocusShowNormal')
              ? '稀客和普客均已隐藏，可在“专注显示”中选择。'
              : (_show('serviceFocusShowRare') &&
                        liveOrders['rareSnapshotAvailable'] == false ||
                    _show('serviceFocusShowNormal') &&
                        liveOrders['normalSnapshotAvailable'] == false)
              ? '现场订单读取尚未完成。'
              : '当前没有所选类型的实际订单。',
        if (special['active'] == true) label(special['displayName']),
      ],
    ];
    // IndexedStack 保留本机设置和滚动 State，但只由可见专注页发布提示。
    // 完整规则与人工处理说明交给页头详情；正文仅保留实际订单与制作资料。
    return PageActivityReporter(
      activity: !widget.active || messages.isEmpty
          ? null
          : PageActivity(
              message: messages.join(' · '),
              busy: _saving != null || (planActivity?.busy ?? false),
              error: _failure != null || (planActivity?.error ?? false),
              details: [
                ...messages,
                if (planActivity != null &&
                    planActivity.details != null &&
                    planActivity.details != planActivity.message)
                  planActivity.details!,
                if (model.connected && special['active'] == true)
                  label(special['ruleSummary']),
                if (model.connected)
                  for (final barrier in barriers)
                    '${label(barrier['title'])} · ${label(barrier['message'])}',
              ].join('\n'),
            ),
      child: ListView(
        key: const ValueKey('service-focus-scroll'),
        controller: _scroll,
        children: [
          CollapsibleSection('专注显示 · 本机', [
            const Text('只调整本机显示，桌号和客人始终保留；不会改变方案或自动化。'),
            SettingToggle(
              '显示窗口 UI',
              serviceFocusShowsWindowUi(model),
              _saving == null
                  ? (value) => _save(serviceFocusWindowUiPreference, value)
                  : null,
              key: const ValueKey(serviceFocusWindowUiPreference),
            ),
            SettingToggle(
              '稀客优先',
              rareFirst,
              _saving == null
                  ? (value) => _save(serviceFocusRareFirstPreference, value)
                  : null,
              key: const ValueKey(serviceFocusRareFirstPreference),
              description: '仅本机将稀客列在普客之前，各类内部保持点单顺序。',
            ),
            SettingToggle(
              '显示收藏按钮',
              _show(serviceFocusFavoritesPreference),
              _saving == null
                  ? (value) => _save(serviceFocusFavoritesPreference, value)
                  : null,
              key: const ValueKey(serviceFocusFavoritesPreference),
            ),
            for (final entry in serviceFocusPreferences.entries)
              SettingToggle(
                entry.value,
                _show(entry.key),
                _saving == null ? (value) => _save(entry.key, value) : null,
                key: ValueKey(entry.key),
              ),
          ]),
          if (model.connected) ...[
            for (var index = 0; index < displayed.length; index++)
              _FocusOrder(
                key: ValueKey(
                  'focus-${displayed[index].rare ? 'rare' : 'normal'}-${displayed[index].item['presentationKey'] ?? index}',
                ),
                model: model,
                item: displayed[index].item,
                rare: displayed[index].rare,
                show: _show,
              ),
          ],
        ],
      ),
    );
  }
}

class _FocusOrder extends StatelessWidget {
  const _FocusOrder({
    required this.model,
    required this.item,
    required this.rare,
    required this.show,
    super.key,
  });

  final WorkbenchModel model;
  final DataMap item;
  final bool rare;
  final bool Function(String) show;

  @override
  Widget build(BuildContext context) {
    final order = object(item['order']);
    final plans = records(item['executionPlans']);
    final plan = rare && plans.isNotEmpty
        ? plans.first
        : const <String, Object?>{};
    final food = rare ? object(plan['food']) : object(item['executionFood']);
    final drink = rare
        ? object(plan['beverage'])
        : object(item['executionBeverage']);
    final foodView = food.isEmpty
        ? null
        : serviceFoodView(model, food, normal: !rare);
    final drinkView = drink.isEmpty
        ? null
        : serviceBeverageView(model, drink, normal: !rare);
    final block = order[rare ? 'automationBlockReason' : 'actionBlockReason'];
    return Semantics(
      container: true,
      child: Container(
        margin: const EdgeInsets.only(bottom: 8),
        padding: const EdgeInsets.symmetric(vertical: 10, horizontal: 8),
        decoration: BoxDecoration(
          // 专注列表与普通列表共用订单通道色，背景只受背景透明度影响。
          color: context.stewardPaint.surface(
            serviceOrderHighlight(model, rare: rare),
            alpha: .11,
          ),
          borderRadius: BorderRadius.circular(6),
          border: Border(
            bottom: BorderSide(color: context.theme.colors.border),
          ),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            ServiceOrderHeading(item, rare: rare, showKind: true),
            if (show('serviceFocusShowOrderTags'))
              MenuInfoGroup('点单', [
                if (rare)
                  OrderTag(
                    '料理需求：${label(order['foodTag'])}',
                    kind: OrderTagKind.request,
                  )
                else
                  ServiceOriginalOrderTag(
                    object(item['originalFood']),
                    food: true,
                    labelRole: true,
                  ),
                if (rare)
                  OrderTag(
                    '酒水需求：${label(order['beverageTag'])}',
                    kind: OrderTagKind.request,
                  )
                else
                  ServiceOriginalOrderTag(
                    object(item['originalBeverage']),
                    food: false,
                    labelRole: true,
                  ),
              ]),
            // 占位只承载尚未到达的方案字段。当前点单、送达和已知阻断始终在其外立即展示。
            ServicePlanRegion(
              pending: item['presentationPending'] == true,
              placeholder: ServicePlanPlaceholder(
                focusFields: [
                  if (show('serviceFocusShowFood')) '料理',
                  if (show('serviceFocusShowIngredients')) '配方',
                  if (show('serviceFocusShowExtras')) '加料',
                  if (show('serviceFocusShowCooker')) '厨具',
                  if (show('serviceFocusShowBeverage')) '酒水',
                ],
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (item['presentationIssue'] is String)
                    Text(
                      label(item['presentationIssue']),
                      style: StewardText.caption,
                    )
                  else if (rare && plans.isEmpty)
                    const Text('暂无主方案，请到稀客页查看阻断原因。', style: StewardText.caption)
                  else if (foodView == null && drinkView == null)
                    const Text('料理与酒水方案尚未给出。', style: StewardText.caption)
                  else ...[
                    if (show('serviceFocusShowFood'))
                      MenuInfoGroup('料理', [
                        OrderTag(
                          foodView?.name ?? '料理未知',
                          kind: OrderTagKind.recipe,
                          leading: EntityImage(
                            kind: EntityImageKind.food,
                            id: foodView?.entityId,
                            fallback: FLucideIcons.utensils,
                          ),
                        ),
                      ]),
                    if (show('serviceFocusShowIngredients'))
                      MenuInfoGroup(
                        '配方',
                        _ingredients(foodView?.ingredients, extra: false),
                      ),
                    if (show('serviceFocusShowExtras'))
                      MenuInfoGroup(
                        '加料',
                        _ingredients(foodView?.extras, extra: true),
                      ),
                    if (show('serviceFocusShowCooker'))
                      MenuInfoGroup('厨具', [
                        OrderTag(
                          foodView?.cooker ?? '厨具未知',
                          kind: OrderTagKind.cooker,
                          leading: EntityImage(
                            kind: EntityImageKind.cooker,
                            id: foodView?.cookerId,
                            fallback: FLucideIcons.cookingPot,
                          ),
                        ),
                      ]),
                    if (show('serviceFocusShowBeverage'))
                      MenuInfoGroup('酒水', [
                        OrderTag(
                          drinkView?.name ?? '酒水未知',
                          kind: OrderTagKind.beverage,
                          leading: EntityImage(
                            kind: EntityImageKind.beverage,
                            id: drinkView?.entityId,
                            fallback: FLucideIcons.glassWater,
                          ),
                        ),
                      ]),
                  ],
                ],
              ),
            ),
            if (plan['bucket'] == 'blocked')
              const Text('当前主方案不可执行。', style: StewardText.caption),
            if (show(serviceFocusFavoritesPreference) &&
                rare &&
                plans.isNotEmpty &&
                item['presentationPending'] != true)
              ServiceFavoriteActions(model, item, plan),
            if (values(item['blockedMessages']).isNotEmpty)
              Text(
                '阻断 · ${words(item['blockedMessages'])}',
                style: StewardText.caption,
              ),
            if (order[rare ? 'automationAllowed' : 'canAutomate'] == false &&
                label(block, '').isNotEmpty &&
                block != '暂不可自动处理')
              Text(label(block), style: StewardText.caption),
          ],
        ),
      ),
    );
  }

  List<Widget> _ingredients(
    List<MenuIngredientView>? ingredients, {
    required bool extra,
  }) => [
    if (ingredients == null)
      OrderTag(extra ? '加料未知' : '基础配方未知', kind: OrderTagKind.warning)
    else if (ingredients.isEmpty)
      OrderTag(
        extra ? '不加料' : '无基础材料',
        kind: extra ? OrderTagKind.extra : OrderTagKind.ingredient,
      )
    else
      // 相同加料可以出现多次：只显示服务端顺序，不去重、不重算用量。
      for (final ingredient in ingredients)
        OrderTag(
          ingredient.name,
          kind: extra ? OrderTagKind.extra : OrderTagKind.ingredient,
          leading: EntityImage(
            kind: EntityImageKind.ingredient,
            id: ingredient.entityId,
            fallback: extra ? FLucideIcons.plus : FLucideIcons.leaf,
          ),
        ),
  ];
}
