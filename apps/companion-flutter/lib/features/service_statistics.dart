import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' show SelectableText;

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/page_activity.dart';
import '../ui/steward_opacity.dart';
import '../ui/steward_style.dart';

/// 经营统计集中展示现场数量、自动化状态及待确认事项，不参与预约、推荐和执行准入。
/// 订单数取实时现场投影而非可能滞后的方案数组；厨具拟分配与现场状态分区展示，
/// 多功能厨具允许出现在多个类型中，但不能据此相加得到物理厨具总量。
class ServiceStatisticsPage extends StatelessWidget {
  const ServiceStatisticsPage(this.model, {this.onReviewBarriers, super.key});
  final WorkbenchModel model;

  /// 生产经营页提供已有的人工确认弹窗；独立只读嵌入不伪造操作入口。
  /// 这里仅打开查看流程，是否能确认仍由原弹窗的canExecute与会话代次决定。
  final VoidCallback? onReviewBarriers;

  @override
  Widget build(BuildContext context) {
    final orders = model.serviceOrders;
    final error = model.business['error'];
    final businessError = error is String ? error.trim() : '';
    final current =
        model.connected &&
        model.business['isCurrent'] == true &&
        businessError.isEmpty;
    final connectionVersion = model.connectionVersion;
    final sessionId = model.snapshot['automationSessionId'];
    final generation = model.snapshot['nightBusinessGeneration'];
    final runtime = model.connected ? model.runtime : const <String, Object?>{};
    final automation = current ? model.automation : const <String, Object?>{};
    final barriers = records(automation['safetyBarriers']);
    final pauseReason = model.connected
        ? _automationPauseReason(model.snapshot)
        : null;
    final cookerReadState = _cookerReadState(runtime, model.connected);
    final overview = object(automation['resourceOverview']);
    final cookers = records(runtime['placedCookers']);
    final allocations = records(overview['cookers']);
    final blocked = records(overview['normalBlocked']);
    String orderCount(String key, String availability) =>
        model.connected && orders[availability] != false && orders[key] is List
        ? '${records(orders[key]).length}'
        : '—';
    final metrics = [
      ('稀客订单', orderCount('recommendations', 'rareSnapshotAvailable')),
      ('普客订单', orderCount('normalOrderDetailPlans', 'normalSnapshotAvailable')),
    ];
    return PageActivityReporter(
      activity: !model.connected
          ? const PageActivity(message: '未连接')
          : businessError.isNotEmpty
          // 进入统计后，父经营页会撤回自己的状态；这里必须接续真实错误，
          // 不能把失败或仅等待新现场的非 current 状态伪装成仍在计算。
          ? PageActivity(message: '统计更新失败', details: businessError, error: true)
          : !current
          ? model.business['isCalculating'] == true
                ? const PageActivity(message: '统计正在更新', busy: true)
                : const PageActivity(
                    message: '统计待同步',
                    details: '自动化统计尚未与当前现场同步；已读取的现场订单和厨具仍可查看。',
                  )
          : runtime['placedCookerSnapshotComplete'] != true
          ? PageActivity(
              message: cookerReadState,
              details: label(runtime['placedCookerStatus'], '').isEmpty
                  ? '等待当前厨具读取完成。'
                  : '当前厨具的完整诊断可在下方“读取诊断”中查看。',
            )
          : null,
      child: ListView(
        key: const PageStorageKey('service-statistics-scroll'),
        children: [
          Section('经营概况', [
            // 数字卡仅承载现场订单事实；配置、待办与供给不混成同一组不同口径的计数。
            // 两列共享自然高度，窄屏或字号放大时标题可换行，仍不会固定高度截字。
            IntrinsicHeight(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Expanded(child: _Metric(metrics[0])),
                  const SizedBox(width: 10),
                  Expanded(child: _Metric(metrics[1])),
                ],
              ),
            ),
            const SizedBox(height: StewardDensity.detailGroupGap),
            // 保存的共享开关不等于现场执行许可；状态只能取当前business，不能将
            // 断线/刷新时保留的旧message继续显示成正在执行。
            _SummaryLine(
              '自动化配置',
              model.profile['automationEnabled'] == true ? '已开启' : '已关闭',
            ),
            _SummaryLine(
              '自动化状态',
              !model.connected
                  ? '未连接'
                  : current
                  ? label(automation['message'], '等待自动化状态')
                  : businessError.isNotEmpty
                  ? '状态更新失败'
                  : model.business['isCalculating'] == true
                  ? '正在更新'
                  : '等待当前经营状态',
            ),
            if (pauseReason != null)
              _SummaryLine('暂停原因', pauseReason, emphasis: true),
            const _SummaryDivider(),
            // 空待办只占一条摘要，不为“暂无”单独保留整张卡；真实事项仍逐条显示
            // 完整原因并提供原人工确认入口。未知、空列表和确有事项不能合并成一种状态。
            _SummaryLine(
              '待确认事项',
              !current
                  ? model.connected
                        ? '当前事项待同步'
                        : '连接后查看待确认事项'
                  : automation['safetyBarriers'] is! List
                  ? '尚未读取待确认事项'
                  : barriers.isEmpty
                  ? '暂无待确认事项'
                  : '${barriers.length} 项待处理',
              emphasis: barriers.isNotEmpty,
            ),
            for (final barrier in barriers) _ConfirmationItem(barrier),
            if (barriers.isNotEmpty && onReviewBarriers != null)
              ToolRow([
                StewardButton(
                  '查看待确认事项（${barriers.length}）',
                  key: const ValueKey('service-statistics-review-barriers'),
                  onPressed: () {
                    // 旧帧回调不能以旧列表打开新连接的事项；真正写入仍经过原确认流程。
                    if (model.connected &&
                        model.connectionVersion == connectionVersion &&
                        model.snapshot['automationSessionId'] == sessionId &&
                        model.snapshot['nightBusinessGeneration'] ==
                            generation &&
                        model.business['isCurrent'] == true &&
                        label(model.business['error'], '').trim().isEmpty &&
                        records(model.automation['safetyBarriers'])
                            .isNotEmpty) {
                      onReviewBarriers?.call();
                    }
                  },
                ),
              ]),
          ]),
          Section('当前厨具', [
            _SummaryLine('读取状态', cookerReadState),
            // 已确认空集合才显示“暂无”。读取失败/非经营场景已经由上方中文状态说明，
            // 不再叠加另一句空态，避免将未知误读为当前真的没有厨具。
            if (cookers.isEmpty &&
                runtime['placedCookerSnapshotComplete'] == true)
              const Text('暂无当前厨具记录。', style: StewardText.body),
            if (cookers.isNotEmpty) ...[
              const SizedBox(height: StewardDensity.supportingGap),
              _CookerCards(cookers),
            ],
            _CookerReadDiagnostics(runtime: runtime, cookers: cookers),
          ]),
          Section('自动化拟分配', [
            const Text('计划不等于实际占用；多功能厨具不跨类型累加。', style: StewardText.caption),
            const SizedBox(height: StewardDensity.detailGroupGap),
            if (allocations.isEmpty)
              Padding(
                padding: StewardDensity.settingPadding,
                child: Text(
                  !current
                      ? '—'
                      : model.profile['automationEnabled'] != true
                      ? '自动化未开启'
                      : '本轮暂无厨具分配',
                ),
              ),
            for (final allocation in allocations) _AllocationCard(allocation),
            if (blocked.isNotEmpty) ...[
              const SizedBox(height: StewardDensity.detailGroupGap),
              const Text('暂不能开始制作', style: StewardText.value),
              for (final item in blocked)
                Padding(
                  padding: StewardDensity.settingPadding,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Text(label(item['label']), style: StewardText.value),
                      const SizedBox(height: StewardDensity.supportingGap),
                      Text(label(item['reason']), style: StewardText.body),
                    ],
                  ),
                ),
            ],
          ]),
        ],
      ),
    );
  }
}

/// 场景原因码只映射玩家可读文字，不反向修改执行许可。来源仍是当前连接的
/// snapshot，未知原因保留等待确认，不因开关开启或缺少错误字段推断可以运行。
String? _automationPauseReason(DataMap snapshot) =>
    switch (label(snapshot['nightBusinessAutomationBlockReason'], '')) {
      '' => null,
      'night-business-lifecycle-unavailable' => '尚未进入可自动经营的场景',
      'night-business-tutorial-active' => '当前经营包含教学流程，本次经营暂停自动化',
      'night-business-tutorial-state-unavailable' => '暂时无法确认教学状态，自动化已暂停',
      _ => '等待游戏确认自动化运行条件',
    };

/// placedCookerSnapshotComplete是宿主发布的事实标记；英文status仅作为失败说明，
/// 绝不解析其数字/名称来推导可用锅位或授权。只翻译当前C#明确输出的原因前缀，
/// 未来未知诊断保持“待确认”，原文在可展开的读取诊断中完整保留。
String _cookerReadState(DataMap runtime, bool connected) {
  if (!connected) return '未连接';
  if (runtime['placedCookerSnapshotComplete'] == true) return '已读取当前厨具';
  final raw = label(runtime['placedCookerStatus'], '').trim();
  if (raw == 'not in night business scene') return '当前不在夜间经营，无当前厨具';
  if (raw.startsWith('source unavailable;')) return '暂时无法读取当前厨具';
  return raw.isEmpty ? '等待当前厨具读取' : '厨具读取状态待确认';
}

/// 技术诊断默认收起，避免每口锅重复输出英文协议串；完整异常仍可选择、复制，
/// 不以中文摘要替换或丢弃原始原因。状态刷新保留展开选择，内容始终来自当前Widget。
class _CookerReadDiagnostics extends StatefulWidget {
  const _CookerReadDiagnostics({required this.runtime, required this.cookers});
  final DataMap runtime;
  final List<DataMap> cookers;

  @override
  State<_CookerReadDiagnostics> createState() => _CookerReadDiagnosticsState();
}

class _CookerReadDiagnosticsState extends State<_CookerReadDiagnostics> {
  bool expanded = false;

  @override
  Widget build(BuildContext context) {
    final details = <String>[
      if (label(widget.runtime['placedCookerStatus'], '').isNotEmpty)
        label(widget.runtime['placedCookerStatus']),
      for (final cooker in widget.cookers)
        if (label(cooker['automationAvailabilityDiagnostic'], '').isNotEmpty)
          '${label(cooker['name'])}：${label(cooker['automationAvailabilityDiagnostic'])}',
    ];
    if (details.isEmpty) return const SizedBox.shrink();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        ToolRow([
          StewardButton(
            expanded ? '收起读取诊断' : '读取诊断',
            key: const ValueKey('service-cooker-diagnostics'),
            onPressed: () => setState(() => expanded = !expanded),
          ),
        ]),
        if (expanded)
          Padding(
            padding: const EdgeInsets.only(top: StewardDensity.supportingGap),
            child: SelectableText(
              details.join('\n'),
              style: StewardText.caption,
            ),
          ),
      ],
    );
  }
}

/// 概况中的状态采用统一的短标签列和可换行值列。标签用辅助色，现场状态用正文，
/// 因而长原因不再与配置名称混成一段。列宽随可用空间收紧，文字和图标不缩小；
/// 独立语义容器避免列表刷新时把相邻状态意外合并到同一个可访问节点。
class _SummaryLine extends StatelessWidget {
  const _SummaryLine(this.title, this.value, {this.emphasis = false});
  final String title;
  final String value;
  final bool emphasis;

  @override
  Widget build(BuildContext context) => Semantics(
    container: true,
    explicitChildNodes: true,
    child: Padding(
      padding: StewardDensity.togglePadding,
      child: LayoutBuilder(
        builder: (context, constraints) => StewardBaselineRow(
          children: [
            SizedBox(
              width: (constraints.maxWidth * .28).clamp(0.0, 112.0).toDouble(),
              child: Text(
                title,
                style: StewardText.caption.copyWith(
                  color: context.theme.colors.mutedForeground,
                ),
              ),
            ),
            const SizedBox(width: StewardDensity.toolbarGap),
            Expanded(
              child: Text(
                value,
                style: emphasis
                    ? StewardText.value.copyWith(
                        color: context.theme.colors.primary,
                      )
                    : StewardText.body,
              ),
            ),
          ],
        ),
      ),
    ),
  );
}

/// 只分隔同一概况中的现场/待办口径，不额外制造有标题和留白的大卡片。
class _SummaryDivider extends StatelessWidget {
  const _SummaryDivider();
  @override
  Widget build(BuildContext context) => Container(
    height: 1,
    margin: const EdgeInsets.symmetric(vertical: StewardDensity.toolbarGap),
    color: context.stewardPaint.foreground(
      context.theme.colors.border,
      alpha: .7,
    ),
  );
}

/// 人工确认事项以独立的小块呈现完整说明；色彩仅提示需要核对，不授予写入权限。
class _ConfirmationItem extends StatelessWidget {
  const _ConfirmationItem(this.data);
  final DataMap data;
  @override
  Widget build(BuildContext context) => Container(
    margin: const EdgeInsets.only(bottom: StewardDensity.toolbarGap),
    padding: const EdgeInsets.all(10),
    decoration: BoxDecoration(
      color: context.stewardPaint.surface(
        context.theme.colors.primary,
        alpha: .055,
      ),
      borderRadius: BorderRadius.circular(6),
    ),
    child: Semantics(
      container: true,
      explicitChildNodes: true,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(label(data['title']), style: StewardText.value),
          const SizedBox(height: StewardDensity.supportingGap),
          Text(label(data['message']), style: StewardText.body),
        ],
      ),
    ),
  );
}

/// 两项现场计数共享一行。仅数字加重，不将配置开关、待办或可选目录冒充同口径统计。
/// 数字与名称按首行基线对齐，名称可换行，自然高度随系统字号增长。
class _Metric extends StatelessWidget {
  const _Metric(this.data);
  final (String, String) data;
  @override
  Widget build(BuildContext context) => Container(
    key: ValueKey('service-statistic-${data.$1}'),
    padding: const EdgeInsets.all(10),
    decoration: BoxDecoration(
      color: context.stewardPaint.surface(
        context.theme.colors.primary,
        alpha: .045,
      ),
      borderRadius: BorderRadius.circular(6),
    ),
    child: StewardBaselineRow(
      children: [
        Expanded(child: Text(data.$1, style: StewardText.value)),
        const SizedBox(width: 8),
        Text(
          data.$2,
          style: StewardText.itemTitle.copyWith(
            color: context.theme.colors.primary,
          ),
        ),
      ],
    ),
  );
}

/// 宽正文并排两张当前厨具卡，窄正文保持单列。断点按缩放后的文字容量计算，
/// 不使用固定行高，因此150%字号和长名称不会挤压状态或把状态推到卡片之外。
class _CookerCards extends StatelessWidget {
  const _CookerCards(this.cookers);
  final List<DataMap> cookers;
  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final columns =
          constraints.maxWidth >= MediaQuery.textScalerOf(context).scale(520)
          ? 2
          : 1;
      final width =
          (constraints.maxWidth - (columns - 1) * StewardDensity.toolbarGap) /
          columns;
      return Wrap(
        spacing: StewardDensity.toolbarGap,
        runSpacing: StewardDensity.toolbarGap,
        children: [
          for (var index = 0; index < cookers.length; index++)
            SizedBox(
              width: width,
              child: _CookerState(
                cookers[index],
                key: ValueKey('service-cooker-$index'),
              ),
            ),
        ],
      );
    },
  );
}

/// 使用精确的标准typeIds显示图片；现场controllerIndex不具备图像编号语义。
/// 未知、重复或超出1..5的类型整组回退，仍保留原名称和宿主诊断。
class _CookerState extends StatelessWidget {
  const _CookerState(this.cooker, {super.key});
  final DataMap cooker;
  @override
  Widget build(BuildContext context) {
    final raw = cooker['typeIds'];
    final ids =
        raw is List &&
            raw.isNotEmpty &&
            raw.every((value) => value is int && value >= 1 && value <= 5) &&
            raw.toSet().length == raw.length
        ? raw.cast<int>()
        : const <int>[];
    final status = switch (cooker['automationAvailability']) {
      'StrictIdle' => '空闲可用',
      'ExtractedResidual' => '取餐后可用',
      'Unavailable' => '暂不可用',
      _ => '状态未知',
    };
    final available =
        cooker['automationAvailability'] == 'StrictIdle' ||
        cooker['automationAvailability'] == 'ExtractedResidual';
    final tone = available
        ? context.theme.colors.primary
        : context.theme.colors.mutedForeground;
    return Container(
      padding: const EdgeInsets.all(10),
      decoration: BoxDecoration(
        color: context.stewardPaint.surface(tone, alpha: .045),
        border: Border.all(
          color: context.stewardPaint.foreground(tone, alpha: .2),
        ),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Semantics(
        container: true,
        explicitChildNodes: true,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // 图片和名称占满首行；状态另有固定阅读顺序，不靠两端大空白区分字段。
            // 多功能类型图片只取精确typeIds，Wrap允许多图换行，不按实例编号猜图。
            Wrap(
              spacing: 6,
              runSpacing: StewardDensity.supportingGap,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                for (final id in ids.isEmpty ? <int?>[null] : ids)
                  EntityImage(
                    kind: EntityImageKind.cooker,
                    id: id,
                    size: StewardImageSize.tag,
                    fallback: FLucideIcons.cookingPot,
                  ),
                Text(label(cooker['name']), style: StewardText.value),
              ],
            ),
            const SizedBox(height: StewardDensity.supportingGap),
            StewardBaselineRow(
              children: [
                StewardFirstLineCenter(
                  textStyle: StewardText.caption,
                  referenceText: status,
                  child: ExcludeSemantics(
                    child: Icon(
                      available
                          ? FLucideIcons.circleCheck
                          : FLucideIcons.circleHelp,
                      size: MediaQuery.textScalerOf(context).scale(14),
                      color: tone,
                    ),
                  ),
                ),
                const SizedBox(width: 6),
                Expanded(
                  child: Text(
                    status,
                    style: StewardText.caption.copyWith(color: tone),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// 每类拟分配先显示厨具身份，再以三等分计数呈现普客/稀客计划与可用锅位。
/// 相比窄屏四列表，名称不再与三个数字争抢同一行；各计数仍完整对应宿主字段。
/// 不用“容量-预约”推导空闲值，也不把预约数标为已经制作中的数量。
class _AllocationCard extends StatelessWidget {
  const _AllocationCard(this.data);
  final DataMap data;
  String number(String key) =>
      data[key] is int && (data[key]! as int) >= 0 ? '${data[key]}' : '—';
  @override
  Widget build(BuildContext context) => Container(
    margin: const EdgeInsets.only(bottom: StewardDensity.toolbarGap),
    padding: const EdgeInsets.all(10),
    decoration: BoxDecoration(
      color: context.stewardPaint.surface(
        context.theme.colors.mutedForeground,
        alpha: .045,
      ),
      borderRadius: BorderRadius.circular(6),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        EntityName(
          label(data['label']),
          kind: EntityImageKind.cooker,
          id: entityImageId(data['cookerType']),
          fallback: FLucideIcons.cookingPot,
          style: StewardText.value,
        ),
        const SizedBox(height: StewardDensity.toolbarGap),
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            for (final column in const [
              ('普客计划', 'normalReserved'),
              ('稀客计划', 'rareReserved'),
              ('可用锅位', 'capacity'),
            ])
              Expanded(
                child: Semantics(
                  container: true,
                  explicitChildNodes: true,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        column.$1,
                        style: StewardText.caption.copyWith(
                          color: context.theme.colors.mutedForeground,
                        ),
                      ),
                      const SizedBox(height: StewardDensity.supportingGap),
                      Text(number(column.$2), style: StewardText.value),
                    ],
                  ),
                ),
              ),
          ],
        ),
      ],
    ),
  );
}
