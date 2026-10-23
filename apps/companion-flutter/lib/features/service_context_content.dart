import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/steward_style.dart';

/// 经营统计的现场补充信息：在场稀客与订单分开，尚未下单者也保留；挑战数据只展示
/// 宿主已提供的数值，不根据计时推算剩余时间，也不参与推荐、目标优先级或自动化权限。
class ServiceContextContent extends StatelessWidget {
  const ServiceContextContent(this.model, {super.key});
  final WorkbenchModel model;

  @override
  Widget build(BuildContext context) {
    if (!model.connected) return const SizedBox.shrink();
    final night = model.night;
    final guests = records(night['activeRareGuests']);
    final special = object(model.snapshot['specialBusiness']);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Section('在场稀客', [
          if (night['activeRareGuests'] is! List)
            const Text('尚未取得在场稀客列表。', style: StewardText.caption)
          else if (guests.isEmpty)
            const Text('当前没有在场稀客。', style: StewardText.caption),
          for (final guest in guests)
            Padding(
              padding: StewardDensity.settingPadding,
              child: StewardBaselineRow(
                children: [
                  Expanded(
                    child: EntityName(
                      label(guest['guestName']),
                      kind: EntityImageKind.rareGuest,
                      id: entityImageId(guest['guestId']),
                      fallback: FLucideIcons.userRound,
                      style: StewardText.body,
                    ),
                  ),
                  const SizedBox(width: 8),
                  Flexible(
                    child: Text(
                      '${_desk(guest['deskCode'])} 桌 · 资金 ${_number(guest['fund'])}',
                      style: StewardText.caption,
                      textAlign: TextAlign.end,
                    ),
                  ),
                ],
              ),
            ),
        ]),
        if (special['active'] == true)
          Section('特殊经营 · ${label(special['displayName'], '挑战')}', [
            if (label(special['phase'], '').isNotEmpty)
              Fact('当前阶段', special['phase']),
            if (values(special['foodTargetTags']).isNotEmpty)
              _SpecialBusinessTagFact(
                '料理目标',
                values(special['foodTargetTags']).map((v) => label(v)).toList(),
              ),
            if (values(special['beverageTargetTags']).isNotEmpty)
              _SpecialBusinessTagFact(
                '酒水目标',
                values(special['beverageTargetTags'])
                    .map((v) => label(v))
                    .toList(),
              ),
            if (special['targetFund'] is num)
              Fact('目标营业额', '${_number(special['targetFund'])} ¥'),
            if (special['currentValue'] is num || special['maxValue'] is num)
              Fact(
                label(special['targetLabel'], '进度'),
                '${_number(special['currentValue'])} / ${_number(special['maxValue'])}',
              ),
            if (special['targetValue'] is num)
              Fact('目标进度', _number(special['targetValue'])),
            if (special['currentSpellCount'] is num ||
                special['targetSpellCount'] is num)
              Fact(
                '符卡进度',
                '${_number(special['currentSpellCount'])} / ${_number(special['targetSpellCount'])}',
              ),
            if (special['currentAnger'] is num || special['maxAnger'] is num)
              Fact(
                '怒气',
                '${_number(special['currentAnger'])} / ${_number(special['maxAnger'])}',
              ),
            if (special['targetAnger'] is num)
              Fact('目标怒气', _number(special['targetAnger'])),
            if (special['targetTagTimeProgress'] is num)
              Fact('标签剩余', _percent(special['targetTagTimeProgress'])),
            if (special['targetTimeProgress'] is num)
              Fact('阶段剩余', _percent(special['targetTimeProgress'])),
            if (special['wackyKoishiShieldBroken'] is bool)
              Fact(
                '恋的护盾',
                special['wackyKoishiShieldBroken'] == true ? '已破防' : '未破防',
              ),
            for (final (field, title) in [
              ('ruleSummary', '当前规则'),
              ('recommendationPolicy', '推荐策略'),
              ('automationPolicy', '自动化策略'),
            ])
              if (label(special[field], '').isNotEmpty)
                Fact(title, special[field]),
            CollapsibleSection('特殊经营读取诊断', [
              Fact('挑战标识', special['challengeType']),
              Fact('读取来源', special['source']),
              if (label(special['error'], '').isNotEmpty)
                Fact('读取说明', special['error']),
              if (label(special['lastTargetUpdatedUtc'], '').isNotEmpty)
                Fact('目标更新时间', special['lastTargetUpdatedUtc']),
            ]),
          ]),
      ],
    );
  }

  String _desk(Object? value) =>
      value is int && value >= 0 ? '${value + 1}' : '未知';
  String _number(Object? value) =>
      value is num && value.isFinite ? '$value' : '未知';
  String _percent(Object? value) =>
      value is num && value.isFinite && value >= 0 && value <= 1
      ? '${(value * 100).round()}%'
      : '未知';
}

/// 挑战目标属于统计字段，和相邻 Fact 共用 94px 首列、正文与行间距。菜单组件的
/// 两字前缀槽只适合“配方”等短名称；此处保留四字字段在同一行，不改变全局菜单规范。
/// 右列独立换行，长标签仅增加本行高度，首行文字基线仍与字段名对齐。
class _SpecialBusinessTagFact extends StatelessWidget {
  const _SpecialBusinessTagFact(this.name, this.tags);
  final String name;
  final List<String> tags;

  @override
  Widget build(BuildContext context) => Padding(
    padding: StewardDensity.togglePadding,
    child: StewardBaselineRow(
      children: [
        SizedBox(
          width: 94,
          child: Text(
            name,
            style: StewardText.body.copyWith(
              color: context.theme.colors.mutedForeground,
            ),
          ),
        ),
        Expanded(
          child: Wrap(
            key: ValueKey('special-business-target-tags-$name'),
            spacing: 6,
            runSpacing: 6,
            children: [
              for (final tag in tags) OrderTag(tag, kind: OrderTagKind.request),
            ],
          ),
        ),
      ],
    ),
  );
}
