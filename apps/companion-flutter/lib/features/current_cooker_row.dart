import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/entity_image.dart';
import '../ui/steward_opacity.dart';
import '../ui/steward_style.dart';

/// 现场厨具共享紧凑信息行：名称、状态在首行，游戏原文效果在次行。
/// 状态来自宿主稳定原因码，说明只使用实例Text投影，不从类型或英文诊断猜测。
/// 普通行不设固定高度，长效果、未知类型及150%字号仍完整可读。
class CurrentCookerRow extends StatelessWidget {
  const CurrentCookerRow(this.cooker, {super.key});
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
      'Unavailable' => switch (cooker['unavailableReason']) {
        'loaded' => '已放入食材',
        'cooking' => '制作中',
        'finished' => '待取餐',
        'result-pending' => '待取出成品',
        'residual-unconfirmed' => '待确认上次取餐',
        'closed' => '当前未开放',
        'no-cooker' => '未放置厨具',
        _ => '状态待确认',
      },
      _ => '状态待确认',
    };
    final available =
        cooker['automationAvailability'] == 'StrictIdle' ||
        cooker['automationAvailability'] == 'ExtractedResidual';
    final tone = available
        ? context.theme.colors.primary
        : context.theme.colors.mutedForeground;
    // name继续承担宿主的规范类型校验，实际实例名称由独立displayName提供。
    final name = label(cooker['displayName'], label(cooker['name'], '厨具名称未读取'));
    final rawTypes = cooker['typeNames'];
    final types = rawTypes is List
        ? rawTypes
              .whereType<String>()
              .where((value) => value.isNotEmpty)
              .join(' / ')
        : '';
    final effect = cooker['metadataStatus'] != 'ready'
        ? '说明未读取'
        : label(cooker['effect'], '游戏未提供效果说明');
    return Semantics(
      container: true,
      explicitChildNodes: true,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            StewardBaselineRow(
              children: [
                Expanded(
                  child: Wrap(
                    spacing: 6,
                    runSpacing: StewardDensity.supportingGap,
                    crossAxisAlignment: WrapCrossAlignment.center,
                    children: [
                      // 图片目录是标准类型命名空间，实例ID绝不能作为图片ID使用。
                      for (final id in ids.isEmpty ? <int?>[null] : ids)
                        EntityImage(
                          kind: EntityImageKind.cooker,
                          id: id,
                          fallback: FLucideIcons.cookingPot,
                        ),
                      Text(name, style: StewardText.value),
                    ],
                  ),
                ),
                const SizedBox(width: StewardDensity.toolbarGap),
                ConstrainedBox(
                  constraints: BoxConstraints(
                    maxWidth: MediaQuery.textScalerOf(context).scale(104),
                  ),
                  child: Text(
                    status,
                    style: StewardText.caption.copyWith(color: tone),
                    textAlign: TextAlign.end,
                  ),
                ),
              ],
            ),
            const SizedBox(height: StewardDensity.supportingGap),
            Text.rich(
              TextSpan(
                children: [
                  if (types.isNotEmpty && types != name)
                    TextSpan(text: '$types · '),
                  const TextSpan(text: '效果：'),
                  TextSpan(text: effect),
                ],
              ),
              style: StewardText.caption.copyWith(
                color: context.theme.colors.mutedForeground,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// 使用细分隔线组织同组厨具，避免每件都重复一层大边框和背景。
class CurrentCookerList extends StatelessWidget {
  const CurrentCookerList(this.cookers, {super.key});
  final List<DataMap> cookers;
  @override
  Widget build(BuildContext context) => Column(
    children: [
      for (var index = 0; index < cookers.length; index++) ...[
        if (index > 0)
          Container(
            height: 1,
            color: context.stewardPaint.surface(context.theme.colors.border),
          ),
        CurrentCookerRow(
          cookers[index],
          key: ValueKey('service-cooker-$index'),
        ),
      ],
    ],
  );
}
