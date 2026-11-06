import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/module_switch_row.dart';
import '../ui/menu_item_content.dart';
import '../ui/setting_control_row.dart';
import '../ui/steward_select.dart';
import '../ui/steward_controls.dart';
import '../ui/steward_dynamic_tags.dart';
import '../ui/steward_opacity.dart';
import '../ui/steward_style.dart';
import 'profile_controls.dart';

/// 稀客调度只编辑共享名单与发送精确 CAS 请求，订单队列及执行许可由 C# 决定。
/// 详情仅展示名单内稀客；同一客人的现场订单仍包含暂停项，避免丢失恢复入口。
class RareGuestSchedulingPage extends StatefulWidget {
  const RareGuestSchedulingPage(this.model, {super.key});
  final WorkbenchModel model;

  @override
  State<RareGuestSchedulingPage> createState() =>
      _RareGuestSchedulingPageState();
}

class _RareGuestSchedulingPageState extends State<RareGuestSchedulingPage> {
  WorkbenchModel get model => widget.model;

  // 展开状态只属于本页，不写共享配置。轮询及订单增减保持当前客人，主动收起时
  // 允许全部折叠；仅首次进入或展开客人被移除时选择名单首项。
  int? _expandedGuestId;
  bool _initializedExpansion = false;

  Map<int, DataMap> get _guests => {
    for (final guest in model.catalogItems('rareCustomers'))
      if (entityImageId(guest['id']) case final int id) id: guest,
  };

  Future<void> _changeRoster(Set<int> ids) => model.updateProfile({
    // 排序仅规范持久化的 ID 集合，不属于订单业务排序。
    'managedRareGuestIds': ids.toList()..sort(),
  });

  DataMap _identity(DataMap entry, Object? generation) => {
    'businessGeneration': generation,
    'traceId': entry['traceId'],
    'orderLifecycleSequence': entry['orderLifecycleSequence'],
    'guestId': entry['guestId'],
  };

  void _mutate(
    String action,
    DataMap state,
    List<DataMap> entries, {
    bool group = false,
  }) {
    // 点击时固化当前完整集合与三个修订边界；等待期间更新不会把请求追认到新订单。
    // 409 或网络结果不明时由模型补读，页面不乐观改队列、也不自动重发。
    final generation = state['businessGeneration'];
    model.mutate(
      '/orders/rare/participation',
      body: {
        'expectedAuthorityRevision': model.authority['authorityRevision'],
        'expectedBusinessGeneration': generation,
        'expectedParticipationRevision': state['participationRevision'],
        'action': action,
        'target': group
            ? {
                'type': 'guest',
                'guestId': entries.first['guestId'],
                'expectedCurrentOrders': [
                  for (final entry in entries) _identity(entry, generation),
                ],
              }
            : {'type': 'order', 'order': _identity(entries.single, generation)},
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    final enabled =
        model.profile['rareGuestParticipationModuleEnabled'] == true;
    final ids = <int>{
      for (final value
          in model.profile['managedRareGuestIds'] is List
              ? model.profile['managedRareGuestIds']! as List<Object?>
              : const <Object?>[])
        if (value is int && value >= 0) value,
    };
    final guests = _guests;
    final state = object(model.snapshot['rareGuestParticipation']);
    final entries = records(state['entries']);
    final current = records(model.night['orders']);
    final ready =
        state['ready'] == true &&
        state['businessGeneration'] ==
            model.snapshot['nightBusinessGeneration'];
    final canChange = model.canEdit && ready;
    final groups = <int, List<DataMap>>{};
    // 顺序沿用服务端 entries；同一客人的全部当前订单共同组成 guest CAS，不遗漏暂停项。
    for (final entry in entries) {
      if (entry['guestId'] case final int id when ids.contains(id)) {
        (groups[id] ??= []).add(entry);
      }
    }
    if ((!_initializedExpansion && ids.isNotEmpty) ||
        (_expandedGuestId != null && !ids.contains(_expandedGuestId))) {
      _expandedGuestId = ids.firstOrNull;
      _initializedExpansion = true;
    }
    // 现场客人包含尚未点单者，只采用原生明确读取出的 guestId；部分读取可以
    // 展示已确认条目，但空集不能宣称无人。旧经营代次不提供快捷添加入口。
    final sceneGuests = <int, DataMap>{
      if (state['active'] == true &&
          state['businessGeneration'] ==
              model.snapshot['nightBusinessGeneration'])
        for (final guest in records(model.night['activeRareGuests']))
          if (entityImageId(guest['guestId']) case final int id
              when !ids.contains(id))
            id: guest,
    };
    String guestName(int id) => label(guests[id]?['name'], '稀客 $id');
    DataMap? findOrder(DataMap entry) {
      // 同一 trace/lifecycle 在换局后可能重新出现，必须先匹配经营代次。
      // 旧调度条目不能借新一局订单的桌位、需求或等待比例继续展示为已确认。
      final generation = model.snapshot['nightBusinessGeneration'];
      if (generation is! int ||
          generation <= 0 ||
          state['businessGeneration'] != generation)
        return null;
      final matching = current.where(
        (order) =>
            order['traceId'] == entry['traceId'] &&
            order['orderLifecycleSequence'] ==
                entry['orderLifecycleSequence'] &&
            order['guestId'] == entry['guestId'],
      );
      return matching.length == 1 ? matching.single : null;
    }

    return ListView(
      key: const PageStorageKey('rare-guest-scheduling'),
      padding: const EdgeInsets.only(right: 8, bottom: 20),
      children: [
        ModuleSwitchRow(
          moduleName: '稀客调度',
          value: enabled,
          status: !model.connected
              ? '共享·离线'
              : model.busy
              ? '共享·保存中'
              : model.primary
              ? '共享·可写'
              : '共享·只读',
          explanation: '名单内新订单默认暂停；启用后参与自动化和游戏高亮，暂停时仍可查看方案。',
          onChanged: model.canEdit
              ? (value) => model.updateProfile({
                  'rareGuestParticipationModuleEnabled': value,
                })
              : null,
        ),
        if (enabled) ...[
          // 名单与自动启用策略合并为一个默认收起的设置区。固定键不包含人数或
          // 策略值，轮询、增删名单和保存配置均保留展开状态；折叠不改变任何共享设置。
          CollapsibleSection('调度设置', [
            Text('调度名单 · ${ids.length} 位', style: StewardText.value),
            const SizedBox(height: 8),
            StewardDynamicTags<int>(
              label: '稀客',
              showLabel: false,
              addHint: '搜索添加稀客',
              items: {
                if (ids.length < 512)
                  for (final id in guests.keys) id: guestName(id),
              },
              selected: {for (final id in ids) id: guestName(id)},
              enabled: model.canEdit,
              // 容量已满时仍允许删除已有项，不能把整个名单一起禁用。
              boundary: (
                model.connectionVersion,
                model.authority['authorityRevision'],
              ),
              itemLeading: (id) => EntityImage(
                kind: EntityImageKind.rareGuest,
                id: id,
                fallback: FLucideIcons.userRound,
              ),
              onAdd: (id) => _changeRoster({...ids, id}),
              onRemove: (id) => _changeRoster({...ids}..remove(id)),
            ),
            const SizedBox(height: 4),
            const Text('当前场景 · 点击添加', style: StewardText.caption),
            const SizedBox(height: 6),
            if (sceneGuests.isEmpty)
              Text(
                state['active'] != true
                    ? '进入经营后显示到场稀客'
                    : model.night['activeRareGuestsReadComplete'] != true
                    ? '正在读取到场稀客'
                    : '暂无可添加的到场稀客',
                style: StewardText.caption,
              )
            else
              Wrap(
                spacing: 6,
                runSpacing: 6,
                children: [
                  for (final guest in sceneGuests.entries)
                    _SchedulingSceneTag(
                      id: guest.key,
                      name: label(
                        guest.value['guestName'],
                        guestName(guest.key),
                      ),
                      onAdd: model.canEdit && ids.length < 512
                          ? () => _changeRoster({...ids, guest.key})
                          : null,
                    ),
                ],
              ),
            const MenuItemDivider(),
            const Text('等待条自动启用', style: StewardText.value),
            ..._autoResumeSettings(),
          ], key: const ValueKey('scheduling-settings')),
          for (final id in ids)
            _SchedulingGuestRow(
              key: ValueKey(('scheduling-guest', id)),
              id: id,
              name: guestName(id),
              expanded: _expandedGuestId == id,
              onToggle: () => setState(
                () => _expandedGuestId = _expandedGuestId == id ? null : id,
              ),
              // 每个客人的 CAS 必须包含其全部当前订单，包括暂停项；画面折叠不能
              // 筛掉写入身份。读取未对齐或有未管理项时，等下一份权威快照再开放操作。
              onAction:
                  canChange &&
                      (groups[id]?.isNotEmpty ?? false) &&
                      groups[id]!.every(
                        (entry) =>
                            entry['managed'] == true &&
                            findOrder(entry) != null,
                      )
                  ? (action) => _mutate(action, state, groups[id]!, group: true)
                  : null,
              entries: groups[id] ?? const [],
              children: [
                if (groups[id]?.isNotEmpty != true)
                  const Padding(
                    padding: EdgeInsets.all(12),
                    child: Text('暂无当前订单', style: StewardText.caption),
                  ),
                for (final entry in groups[id] ?? const <DataMap>[])
                  _SchedulingOrderRow(
                    key: ValueKey((
                      entry['traceId'],
                      entry['orderLifecycleSequence'],
                    )),
                    entry: entry,
                    order: findOrder(entry),
                    onAction:
                        canChange &&
                            entry['managed'] == true &&
                            findOrder(entry) != null
                        ? (action) => _mutate(action, state, [entry])
                        : null,
                  ),
              ],
            ),
        ],
      ],
    );
  }

  /// 自动启用策略与调度名单共享同一份主设备配置和持久化修订，前端只提交设置。
  /// 是否达到阈值、每单只触发一次、手动再次暂停的抑制均由 C# 在精确生命周期上决定。
  /// 默认关闭且允许先编辑阈值；滑块只在拖动结束提交，沿用项目的连接/权限边界。
  List<Widget> _autoResumeSettings() => [
    SettingToggle(
      '自动启用',
      model.profile['rareGuestParticipationAutoResumeEnabled'] == true,
      model.canEdit
          ? (value) => model.updateProfile({
              'rareGuestParticipationAutoResumeEnabled': value,
            })
          : null,
      key: const ValueKey('scheduling-auto-resume-enabled'),
    ),
    IntegerSetting(
      key: const ValueKey('scheduling-auto-resume-threshold'),
      title: '剩余等待 <',
      value:
          model.profile['rareGuestParticipationAutoResumeThresholdPercent']
              is int
          ? model.profile['rareGuestParticipationAutoResumeThresholdPercent']!
                as int
          : 30,
      min: 1,
      max: 100,
      unit: '%',
      resetKey: profileControlBoundary(model),
      onSave: model.canEdit
          ? (value) => model.updateProfile({
              'rareGuestParticipationAutoResumeThresholdPercent': value,
            })
          : null,
    ),
    Padding(
      padding: StewardDensity.settingPadding,
      child: SettingControlRow(
        title: '启用后位置',
        maxControlWidth: stewardSelectWidth,
        control: StewardSelect<String>(
          key: const ValueKey('scheduling-auto-resume-position'),
          label: '启用后位置',
          showLabel: false,
          items: const {'front': '队首', 'tail': '队尾'},
          value:
              model.profile['rareGuestParticipationAutoResumePosition'] ==
                  'front'
              ? 'front'
              : 'tail',
          boundary: profileControlBoundary(model),
          enabled: model.canEdit,
          onChanged: (value) {
            if (!model.canEdit) return;
            model.updateProfile({
              'rareGuestParticipationAutoResumePosition': value,
            });
          },
        ),
      ),
    ),
    // 短说明按两个完整语义片段换行，避免手机大字号只把最后一个字挤到下一行。
    const Wrap(
      runSpacing: 2,
      children: [
        Text('每单自动启用一次；', style: StewardText.caption),
        Text('手动再次暂停保持暂停', style: StewardText.caption),
      ],
    ),
  ];
}

/// 到场稀客以动态标签添加，和名单删除标签共享图片、文字与触屏尺寸。
/// 动作只写共享名单，不按名称寻找角色，也不直接启用其当前订单。
class _SchedulingSceneTag extends StatelessWidget {
  const _SchedulingSceneTag({
    required this.id,
    required this.name,
    required this.onAdd,
  });
  final int id;
  final String name;
  final VoidCallback? onAdd;

  @override
  Widget build(BuildContext context) => FButton(
    key: ValueKey(('scheduling-add-scene', id)),
    variant: FButtonVariant.outline,
    style: stewardButtonStyle(context),
    mainAxisSize: MainAxisSize.min,
    semanticsLabel: '加入调度名单：$name',
    onPress: onAdd,
    prefix: EntityImage(
      kind: EntityImageKind.rareGuest,
      id: id,
      fallback: FLucideIcons.userRound,
    ),
    suffix: const Icon(FLucideIcons.plus, size: 14),
    child: Flexible(child: Text(name, style: StewardText.caption)),
  );
}

/// 受控的客人列表项：名称负责展开，右侧动作是独立命中区，不嵌套按钮。
/// 窄屏或大字号采用同语义的图标动作，保留提示及至少44px触屏区域；不缩字，
/// 不把三个批量动作挪进订单正文，也不让点击动作意外展开/收起客人。
class _SchedulingGuestRow extends StatelessWidget {
  const _SchedulingGuestRow({
    required this.id,
    required this.name,
    required this.expanded,
    required this.onToggle,
    required this.onAction,
    required this.entries,
    required this.children,
    super.key,
  });
  final int id;
  final String name;
  final bool expanded;
  final VoidCallback onToggle;
  final ValueChanged<String>? onAction;
  final List<DataMap> entries;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Container(
    margin: const EdgeInsets.only(bottom: 8),
    decoration: BoxDecoration(
      color: context.stewardPaint.surface(
        context.theme.colors.mutedForeground,
        alpha: .025,
      ),
      border: Border.all(
        color: context.stewardPaint.foreground(
          context.theme.colors.border,
          alpha: .75,
        ),
      ),
      borderRadius: BorderRadius.circular(8),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        LayoutBuilder(
          builder: (context, constraints) {
            final compact =
                constraints.maxWidth <
                440 * MediaQuery.textScalerOf(context).scale(13) / 13;
            final paused = entries.any(
              (entry) => entry['participating'] != true,
            );
            final running = entries.any(
              (entry) => entry['participating'] == true,
            );
            Widget action(
              String text,
              String verb,
              IconData icon,
              bool available,
            ) {
              final callback = available && onAction != null
                  ? () => onAction!(verb)
                  : null;
              return compact
                  ? StewardIconButton(
                      key: ValueKey(('scheduling-group', id, verb)),
                      label: text,
                      icon: icon,
                      onPressed: callback,
                    )
                  : StewardButton(
                      text,
                      key: ValueKey(('scheduling-group', id, verb)),
                      onPressed: callback,
                    );
            }

            return Padding(
              padding: const EdgeInsets.all(4),
              child: Row(
                children: [
                  Expanded(
                    child: Semantics(
                      expanded: expanded,
                      child: FButton(
                        key: ValueKey(('scheduling-expand', id)),
                        variant: FButtonVariant.ghost,
                        style: stewardButtonStyle(context),
                        mainAxisAlignment: MainAxisAlignment.start,
                        onPress: onToggle,
                        prefix: Icon(
                          expanded
                              ? FLucideIcons.chevronDown
                              : FLucideIcons.chevronRight,
                          size: 14,
                        ),
                        child: Expanded(
                          child: EntityName(
                            name,
                            kind: EntityImageKind.rareGuest,
                            id: id,
                            fallback: FLucideIcons.userRound,
                            style: StewardText.value,
                          ),
                        ),
                      ),
                    ),
                  ),
                  const SizedBox(width: 4),
                  action(
                    '全部优先',
                    'enable-front',
                    FLucideIcons.chevronsUp,
                    paused,
                  ),
                  const SizedBox(width: 4),
                  action(
                    '全部排尾',
                    'enable-tail',
                    FLucideIcons.chevronsDown,
                    paused,
                  ),
                  const SizedBox(width: 4),
                  action('全部暂停', 'pause', FLucideIcons.pause, running),
                ],
              ),
            );
          },
        ),
        if (expanded) ...children,
      ],
    ),
  );
}

/// 队列行以订单身份、位置和动作分层，桌号只作显示，绝不参与写请求身份。
class _SchedulingOrderRow extends StatelessWidget {
  const _SchedulingOrderRow({
    required this.entry,
    required this.order,
    required this.onAction,
    super.key,
  });
  final DataMap entry;
  final DataMap? order;
  final ValueChanged<String>? onAction;

  @override
  Widget build(BuildContext context) {
    final desk = order?['deskCode'];
    final participating = entry['participating'] == true;
    // 等待条是服务端按当前原生客人精确读取的比例。缺失、过期身份或非法值
    // 一律显示待读取，不能以 0% 代替未知、也不能在前端据此启用订单。
    final ratio = order?['patienceRemainingRatio'];
    final remaining = ratio is num && ratio.isFinite && ratio >= 0 && ratio <= 1
        ? '等待 ${(ratio * 100).round()}%'
        : '等待待读取';
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        border: Border(
          top: BorderSide(
            color: context.stewardPaint.foreground(
              context.theme.colors.border,
              alpha: .55,
            ),
          ),
        ),
      ),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${desk is int && desk >= 0 ? '${desk + 1}桌' : '桌位待同步'} · ${label(entry['traceId'])}',
                  style: StewardText.body,
                ),
                const SizedBox(height: 4),
                // 需求字段各自作为完整短语换行，避免窄屏把“无酒精”等值的最后
                // 一个字挤到下一行；操作仍在整条右侧，不参与需求文本的排版。
                Wrap(
                  spacing: 8,
                  runSpacing: 2,
                  children: [
                    Text(
                      '料理 ${label(order?['foodTag'])}',
                      style: StewardText.caption,
                    ),
                    Text(
                      '酒水 ${label(order?['beverageTag'])}',
                      style: StewardText.caption,
                    ),
                  ],
                ),
                const SizedBox(height: 4),
                Wrap(
                  spacing: 6,
                  runSpacing: 2,
                  crossAxisAlignment: WrapCrossAlignment.center,
                  children: [
                    StatusBadge(
                      participating ? '队列 ${entry['queuePosition']}' : '已暂停',
                    ),
                    Text(remaining, style: StewardText.caption),
                  ],
                ),
              ],
            ),
          ),
          if (entry['managed'] == true) ...[
            const SizedBox(width: 8),
            Row(
              mainAxisSize: MainAxisSize.min,
              children: participating
                  ? [
                      StewardButton(
                        '暂停',
                        onPressed: onAction == null
                            ? null
                            : () => onAction!('pause'),
                      ),
                    ]
                  : [
                      StewardButton(
                        '优先',
                        onPressed: onAction == null
                            ? null
                            : () => onAction!('enable-front'),
                      ),
                      const SizedBox(width: 6),
                      StewardButton(
                        '排尾',
                        onPressed: onAction == null
                            ? null
                            : () => onAction!('enable-tail'),
                      ),
                    ],
            ),
          ],
        ],
      ),
    );
  }
}
