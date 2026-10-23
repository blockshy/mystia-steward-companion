import 'dart:math' as math;

import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/menu_item_content.dart';
import '../ui/page_activity.dart';
import '../ui/page_toolbar.dart';
import 'automation_status_content.dart';
import 'service_focus.dart';
import 'service_order_content.dart';
import 'service_plan_placeholder.dart';
import 'service_order_highlight.dart';
import 'service_plan_activity.dart';
import 'service_statistics.dart';

/// 经营工作区只展示 C# 提供的主方案、执行诊断和资源占用，绝不从候选重建执行计划。
class ServicePage extends StatefulWidget {
  const ServicePage(this.model, {this.onFocusModeChanged, super.key});
  final WorkbenchModel model;

  /// 壳层只需要知道专注视图是否在前台；订单选择及滚动仍归本页面所有。
  final ValueChanged<bool>? onFocusModeChanged;
  @override
  State<ServicePage> createState() => _ServicePageState();
}

class _ServicePageState extends State<ServicePage> {
  String kind = 'rare';
  bool focusMode = false;
  bool statisticsMode = false;
  bool diagnostics = false;
  // 搜索状态归页面控制器所有；窄宽重排、切换方案/状态时不丢选择区或输入法组合文本。
  final searchInput = TextEditingController();
  String get search => searchInput.text;
  final controlsScroll = ScrollController();
  WorkbenchModel get model => widget.model;

  @override
  void dispose() {
    controlsScroll.dispose();
    searchInput.dispose();
    super.dispose();
  }

  /// 专注页只改变查看方式，不覆盖原先选择的客人类型、搜索草稿或执行诊断页签。
  /// 订单与专注各保留自己的导航实例；切换统计时也保持原列表、详情与筛选状态。
  Widget _navigation() => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Expanded(
        child: WorkspaceTabs(
          horizontalScroll: !model.desktop,
          items: const {
            'rare': '稀客',
            'normal': '普客',
            'focus': '专注',
            'statistics': '统计信息',
          },
          value: statisticsMode
              ? 'statistics'
              : focusMode
              ? 'focus'
              : kind,
          onChanged: (value) {
            setState(() {
              focusMode = value == 'focus';
              statisticsMode = value == 'statistics';
              if (!focusMode && !statisticsMode) kind = value;
            });
            widget.onFocusModeChanged?.call(focusMode);
          },
        ),
      ),
      const SizedBox(width: 8),
      Column(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          // 配置状态不等于正在执行；共享总开关仍统一在扩展自动化页维护。
          StatusBadge(
            model.profile['automationEnabled'] == true ? '自动化已开启' : '自动化已关闭',
          ),
          if (records(model.automation['safetyBarriers']).isNotEmpty)
            StewardButton(
              '待确认 ${records(model.automation['safetyBarriers']).length}',
              onPressed: _showBarriers,
            ),
        ],
      ),
    ],
  );

  @override
  Widget build(BuildContext context) {
    final liveOrders = model.serviceOrders;
    final raw = diagnostics
        ? records(
            model.automation[kind == 'rare'
                ? 'rareDiagnostics'
                : 'normalDiagnostics'],
          )
        : records(
            liveOrders[kind == 'rare'
                ? 'recommendations'
                : 'normalOrderDetailPlans'],
          );
    // 筛选与屏幕上的桌号使用同一展示映射；不能要求玩家用原始零基编号查桌。
    // 执行诊断的 title 已由 C# 格式化，直接匹配它，避免再次加一。
    final items = raw.where((item) {
      final order = object(item['order']);
      final desk = _displayDesk(order['deskCode']);
      final text = diagnostics
          ? label(item['title'], '')
          : '${label(order['guestName'], '')} $desk 桌 桌 $desk';
      return text.contains(search.trim());
    }).toList();
    final barriers = records(model.automation['safetyBarriers']);
    final special = object(model.snapshot['specialBusiness']);
    final planActivity = servicePlanActivity(
      connected: model.connected,
      business: model.business,
      // 执行诊断行不是现场订单方案，不能让其历史任务数驱动“正在计算”动画。
      visibleOrders: diagnostics ? const <DataMap>[] : items,
    );
    final messages = <String>[
      if (!model.connected)
        '连接后查看经营订单'
      else ...[
        planActivity?.message ?? label(model.automation['message'], '等待经营状态'),
        if (special['active'] == true) label(special['displayName']),
        if (barriers.isNotEmpty) '需人工处理 ${barriers.length} 项',
      ],
    ];
    final controls = <Widget>[
      _navigation(),
      PageToolbar(
        key: const ValueKey('service-results-toolbar'),
        controller: searchInput,
        searchLabel: '筛选客人或桌号',
        searchKey: const ValueKey('service-name-filter'),
        onSearchChanged: (_) => setState(() {}),
        navigation: WorkspaceTabs(
          horizontalScroll: true,
          inToolbar: true,
          items: const {false: '订单方案', true: '自动化状态'},
          value: diagnostics,
          onChanged: (value) => setState(() => diagnostics = value),
        ),
      ),
    ];
    final results = DenseListDetail(
      key: ValueKey('service-$kind-$diagnostics'),
      // 现场事实当次重建立即显示，方案等待只由卡片内等结构占位承担。
      items: items,
      itemTint: (_) => serviceOrderHighlight(model, rare: kind == 'rare'),
      richSummary: !diagnostics,
      identity: (item) => diagnostics
          ? label(item['orderKey'])
          : item['presentationKey'] is String
          ? item['presentationKey'] as String
          : '${object(item['order'])['traceId']}:${object(item['order'])['deskCode']}:${object(item['order'])['orderLifecycleSequence']}',
      title: (item) => diagnostics
          ? label(item['title'])
          : '${_displayDesk(object(item['order'])['deskCode'])} 桌 · ${label(object(item['order'])['guestName'])}',
      titleContent: (item) => diagnostics
          ? AutomationOrderHeading(item)
          : ServiceOrderHeading(item, rare: kind == 'rare'),
      subtitle: (item) => diagnostics
          ? '${label(item['stepLabel'])} · ${label(item['detailMessage'], '')}'
          : kind == 'rare'
          ? '料理点单 ${label(object(item['order'])['foodTag'])} / 酒水点单 ${label(object(item['order'])['beverageTag'])}'
          : '${label(object(item['executionFood'])['name'])} / ${label(object(item['executionBeverage'])['name'])}',
      summary: diagnostics
          ? (item) => AutomationStatusContent(
              item,
              model: model,
              compact: true,
              headerInParent: true,
            )
          : (item) => ServiceOrderSummary(model, item, rare: kind == 'rare'),
      detail: diagnostics
          ? _diagnostic
          : kind == 'rare'
          ? _rare
          : _normal,
      empty: model.connected
          ? !diagnostics &&
                    liveOrders[kind == 'rare'
                            ? 'rareSnapshotAvailable'
                            : 'normalSnapshotAvailable'] ==
                        false
                ? '现场订单读取尚未完成'
                : '当前没有${kind == 'rare' ? '稀客' : '普客'}${diagnostics ? '执行任务' : '订单'}'
          : '连接后查看经营订单',
    );
    return PageActivityReporter(
      activity: focusMode || statisticsMode
          ? null
          : PageActivity(
              message: messages.join(' · '),
              busy: planActivity?.busy ?? false,
              error: planActivity?.error ?? false,
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
      child: IndexedStack(
        index: statisticsMode
            ? 2
            : focusMode
            ? 1
            : 0,
        children: [
          // IndexedStack 只隐藏绘制，不会停用子树的返回处理和焦点。显式跟随可见性
          // 禁用 ticker，让窄屏详情的 PopScope 放行返回；排除焦点则避免隐藏输入框
          // 继续接收键盘或手柄输入。两层包装始终存在，原列表和控制器 State 不销毁。
          TickerMode(
            enabled: !focusMode && !statisticsMode,
            child: ExcludeFocus(
              excluding: focusMode || statisticsMode,
              child: LayoutBuilder(
                builder: (context, constraints) => Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    if (model.desktop)
                      ...controls
                    else
                      // 手机旋转或键盘弹出后，只按剩余正文高度分配控制区；至少40%留给结果。
                      // 320px上限避免高屏也被长状态占满。提示、屏障及确认按钮全部保留在可滚动区，
                      // 不能通过裁去阻断提示或隐藏操作来“解决”小屏溢出。
                      ConstrainedBox(
                        constraints: BoxConstraints(
                          maxHeight: math.min(
                            320.0,
                            constraints.maxHeight * .6,
                          ),
                        ),
                        child: SingleChildScrollView(
                          key: const ValueKey('service-controls'),
                          controller: controlsScroll,
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: controls,
                          ),
                        ),
                      ),
                    Expanded(child: results),
                  ],
                ),
              ),
            ),
          ),
          TickerMode(
            enabled: focusMode,
            child: ExcludeFocus(
              excluding: !focusMode,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // 保留同一个导航子树，收起外框不会销毁专注滚动、显示偏好或订单状态。
                  // 隐藏导航不参与焦点与手柄页签登记；本机显示设置始终可访问以恢复 UI。
                  Offstage(
                    offstage: !serviceFocusShowsWindowUi(model),
                    child: TickerMode(
                      enabled: serviceFocusShowsWindowUi(model),
                      child: ExcludeFocus(
                        excluding: !serviceFocusShowsWindowUi(model),
                        child: _navigation(),
                      ),
                    ),
                  ),
                  Expanded(child: ServiceFocusPage(model, active: focusMode)),
                ],
              ),
            ),
          ),
          // 统计页也保留独立滚动节点；隐藏的订单页不能继续接收返回、手柄或输入。
          TickerMode(
            enabled: statisticsMode,
            child: ExcludeFocus(
              excluding: !statisticsMode,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _navigation(),
                  Expanded(
                    child: ServiceStatisticsPage(
                      model,
                      // 统计中的入口复用现有弹窗和会话/代次检查，不另建确认写入通道。
                      onReviewBarriers: _showBarriers,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  /// 人工屏障移到标题旁的明确入口，完整说明与原确认动作仍可滚动访问。
  /// 弹窗固定游戏会话和经营代次；重连或换局后只允许关闭重开，不能确认新一局的同序号。
  Future<void> _showBarriers() async {
    final expectedConnection = model.connectionVersion;
    final expectedSessionId = label(model.snapshot['automationSessionId'], '');
    final expectedGeneration = model.snapshot['nightBusinessGeneration'];
    bool sameScope() =>
        model.connected &&
        model.connectionVersion == expectedConnection &&
        label(model.snapshot['automationSessionId'], '') == expectedSessionId &&
        model.snapshot['nightBusinessGeneration'] == expectedGeneration;
    await showFDialog<void>(
      context: context,
      builder: (context, style, animation) => FDialog(
        animation: MediaQuery.disableAnimationsOf(context) ? null : animation,
        builder: (context, _) => ListenableBuilder(
          listenable: model,
          builder: (context, _) {
            final current = sameScope();
            final entries = current
                ? records(model.automation['safetyBarriers'])
                : const <DataMap>[];
            final media = MediaQuery.of(context);
            return SizedBox(
              width: 560,
              height: math.min(
                520.0,
                math.max(
                  0.0,
                  (media.size.height -
                          media.viewInsets.bottom -
                          media.padding.vertical) *
                      .75,
                ),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const Text('人工确认事项'),
                  const SizedBox(height: 8),
                  Expanded(
                    child: ListView(
                      children: [
                        if (!current)
                          const Text('连接或经营场次已变化，请关闭后重新打开。')
                        else if (entries.isEmpty)
                          const Text('当前没有待确认事项。'),
                        for (final barrier in entries)
                          Section(label(barrier['title']), [
                            Text(label(barrier['message'])),
                            ToolRow([
                              StewardButton(
                                '已人工处理',
                                onPressed:
                                    !model.canExecute ||
                                        barrier['sequence'] is! int ||
                                        expectedGeneration is! int ||
                                        expectedSessionId.isEmpty
                                    ? null
                                    : () async {
                                        if (!sameScope() || !model.canExecute)
                                          return;
                                        if (await confirmCurrentAction(
                                          context,
                                          model,
                                          '确认已人工处理',
                                          label(barrier['message']),
                                        )) {
                                          if (!sameScope() || !model.canExecute)
                                            return;
                                          await model.acknowledgeBarrier(
                                            barrier['sequence'] as int,
                                            expectedSessionId:
                                                expectedSessionId,
                                            expectedGeneration:
                                                expectedGeneration,
                                          );
                                        }
                                      },
                              ),
                            ]),
                          ]),
                      ],
                    ),
                  ),
                  ToolRow([
                    StewardButton(
                      '关闭',
                      onPressed: () => Navigator.pop(context),
                    ),
                  ]),
                ],
              ),
            );
          },
        ),
      ),
    );
  }

  /// 游戏与 API 的 deskCode 从0开始；沿用 C# DisplayDesk 与原客户端 formatDesk 的
  /// 非负编号加一规则。这里只生成文字，列表 identity、重试键和 dismiss payload 保留原码。
  String _displayDesk(Object? value) =>
      value is int && value >= 0 ? '${value + 1}' : label(value);

  Widget _rare(DataMap item) {
    // 确认弹窗期间模型可能轮询更新；复制本次渲染的标量身份，避免复用可变 Map 时
    // 把新订单的原始编号带入旧确认。服务端仍须核对这些原始 ID，不发送显示桌号。
    final order = Map<String, Object?>.of(object(item['order']));
    final plans = records(item['executionPlans']);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        ServiceOrderHeading(
          item,
          rare: true,
          title:
              '${label(order['guestName'])} · ${_displayDesk(order['deskCode'])} 桌',
        ),
        const SizedBox(height: 10),
        Wrap(
          spacing: 6,
          runSpacing: 6,
          children: [
            OrderTag(
              '料理：${label(order['foodTag'])}',
              kind: OrderTagKind.request,
            ),
            OrderTag(
              '酒水：${label(order['beverageTag'])}',
              kind: OrderTagKind.request,
            ),
            if (order['remainingOrderCount'] != null)
              OrderTag(
                '剩余 ${label(order['remainingOrderCount'])} 单',
                kind: OrderTagKind.request,
              ),
          ],
        ),
        ServiceOrderStatus(item, rare: true, details: true),
        if (item['presentationIssue'] is String)
          Text(label(item['presentationIssue'])),
        const SizedBox(height: 18),
        ServicePlanRegion(
          key: ValueKey('rare-detail-plan-${item['presentationKey']}'),
          pending: item['presentationPending'] == true,
          placeholder: const ServicePlanPlaceholder(
            details: true,
            budget: true,
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ServiceBudget(item['budget']),
              if (plans.isNotEmpty) _plan(plans.first, primary: true),
            ],
          ),
        ),
        if (values(item['blockedMessages']).isNotEmpty)
          Section('阻断原因', [
            Text(words(item['blockedMessages'])),
            MenuTagGroup(
              '缺料',
              values(
                object(item['blockedDiagnostic'])['missingIngredientNames'],
              ).map((value) => label(value)).toList(growable: false),
              kind: OrderTagKind.warning,
            ),
            MenuTagGroup(
              '厨具',
              values(object(item['blockedDiagnostic'])['requiredCookerNames'])
                  .map((value) => label(value))
                  .toList(growable: false),
              kind: OrderTagKind.cooker,
            ),
          ]),
        // 第一项始终是 C# 返回的主方案；展开备选不会改变执行选择或偷偷重新排序。
        if (plans.length > 1)
          CollapsibleSection('备选搭配 · ${plans.length - 1} 项', [
            for (final plan in plans.skip(1)) _plan(plan),
          ]),
        CollapsibleSection('高级诊断 · 订单捕获', [
          Fact('追踪标识', order['traceId']),
          const Text('仅当订单已结束但此处仍显示旧记录时清除。此操作不会在游戏中完成或取消订单。'),
          ToolRow([
            StewardButton(
              '清除此捕获缓存',
              onPressed:
                  model.canEdit &&
                      const [
                        'deskCode',
                        'runtimeGuestId',
                        'foodTagId',
                        'beverageTagId',
                      ].every((key) => order[key] is int)
                  ? () async {
                      if (await confirmCurrentAction(
                        context,
                        model,
                        '清除稀客捕获缓存',
                        '${_displayDesk(order['deskCode'])} 桌 · ${label(order['guestName'])}。确认游戏现场与此残留记录不一致后再清除。',
                      )) {
                        await model.mutate(
                          '/orders/rare/dismiss',
                          query: {
                            for (final key in [
                              'deskCode',
                              'runtimeGuestId',
                              'foodTagId',
                              'beverageTagId',
                            ])
                              key: '${order[key]}',
                          },
                        );
                      }
                    }
                  : null,
            ),
          ]),
        ]),
      ],
    );
  }

  /// 详情沿用同一个服务端方案对象，备选仅供查看，不改变实际执行选择。
  Widget _plan(DataMap plan, {bool primary = false}) {
    final bucket =
        const {
          'complete': '完整满足',
          'tradeoff': '权衡方案',
          'preference': '喜好备选',
          'blocked': '不可执行',
        }[plan['bucket']] ??
        '待确认';
    // 主方案材料/厨具/库存已在列表外显，详情只加标签与解释；备选没有外层卡片，
    // 因此展开后保留公共组件的完整内容，不能让用户靠猜测辨认备选材料。
    final content = ServicePlanContent(
      model,
      plan,
      details: true,
      showBasics: !primary,
    );
    if (!primary) return Section('备选 · $bucket', [content]);
    // 当前方案与外层摘要使用相同分隔方式，不再用“主方案”增加一层标题。
    // bucket 是服务端的方案事实，仍独立保留，不能随装饰性标题一起删除。
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const MenuItemDivider(),
        Align(alignment: Alignment.centerLeft, child: StatusBadge(bucket)),
        const SizedBox(height: 8),
        content,
      ],
    );
  }

  Widget _normal(DataMap item) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      ServiceOrderHeading(
        item,
        rare: false,
        title:
            '${label(object(item['order'])['guestName'])} · ${_displayDesk(object(item['order'])['deskCode'])} 桌',
      ),
      const SizedBox(height: 10),
      ServiceOrderStatus(item, rare: false, details: true),
      // 送达状态属于订单进度；与下方“原订单”资料留出独立分组间距，避免紧贴标题带。
      const SizedBox(height: 14),
      Section('原订单', [
        Wrap(
          spacing: 6,
          runSpacing: 6,
          children: [
            ServiceOriginalOrderTag(object(item['originalFood']), food: true),
            ServiceOriginalOrderTag(
              object(item['originalBeverage']),
              food: false,
            ),
          ],
        ),
      ]),
      ServicePlanRegion(
        key: ValueKey('normal-detail-plan-${item['presentationKey']}'),
        pending: item['presentationPending'] == true,
        placeholder: const ServicePlanPlaceholder(details: true),
        child: item['presentationIssue'] is String
            ? Text(label(item['presentationIssue']))
            : item['usesSpecialExecution'] == true
            ? Section('执行方案', [
                ServiceNormalPlanContent(
                  model,
                  item,
                  details: true,
                  showBasics: false,
                ),
              ])
            : Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const MenuItemDivider(),
                  ServiceNormalPlanContent(
                    model,
                    item,
                    details: true,
                    showBasics: false,
                  ),
                ],
              ),
      ),
    ],
  );
  Widget _diagnostic(DataMap item) => AutomationStatusContent(
    item,
    model: model,
    // 保留原有执行权限、暂停及人工处理门禁；纯展示组件不会放宽操作范围。
    onRetry:
        model.canExecute &&
            item['paused'] == true &&
            item['manualResolutionRequired'] != true
        ? () => model.retryOrder(kind, label(item['orderKey'], ''))
        : null,
  );
}
