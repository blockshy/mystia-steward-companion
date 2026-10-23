import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/menu_item_content.dart';
import '../ui/steward_style.dart';
import '../ui/steward_opacity.dart';

/// 文档与人物项目共用轻量外壳：标题和状态独立，内容按字段阅读，操作固定在末尾。
/// 整张卡片不响应点击，避免把资料误当成按钮；长内容随外层列表滚动，不增加详情层。
class ExtensionItemCard extends StatelessWidget {
  const ExtensionItemCard({
    required this.title,
    required this.status,
    required this.children,
    this.action,
    this.leading,
    super.key,
  });
  final String title;
  final Widget status;
  final List<Widget> children;
  final Widget? action;
  final Widget? leading;

  @override
  Widget build(BuildContext context) => Container(
    margin: const EdgeInsets.only(bottom: 10),
    padding: const EdgeInsets.all(14),
    decoration: BoxDecoration(
      border: Border.all(color: context.theme.colors.border),
      borderRadius: BorderRadius.circular(6),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        LayoutBuilder(
          builder: (context, constraints) {
            final heading = StewardBaselineRow(
              children: [
                if (leading != null) ...[
                  StewardFirstLineCenter(
                    textStyle: StewardText.itemTitle,
                    referenceText: title,
                    child: leading!,
                  ),
                  const SizedBox(width: 6),
                ],
                Flexible(child: Text(title, style: StewardText.itemTitle)),
              ],
            );
            // 狭窄页面先完整阅读标题，再看状态。宽屏将状态放在标题右侧；
            // 不固定标题行数或状态高度，保证中文长任务名和150%字号不被裁切。
            if (constraints.maxWidth < 460) {
              return Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [heading, const SizedBox(height: 6), status],
              );
            }
            return StewardBaselineRow(
              children: [
                Expanded(child: heading),
                const SizedBox(width: 20),
                ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 200),
                  child: status,
                ),
              ],
            );
          },
        ),
        const SizedBox(height: 12),
        ...children,
        if (action != null) ...[
          const SizedBox(height: 12),
          Align(alignment: Alignment.centerRight, child: action!),
        ],
      ],
    ),
  );
}

/// 任务按文档条目排列：身份/地点、条件进度、逐条条件或触发说明各占明确层次。
/// 这里只翻译服务器枚举，不计算完成度，也不给可接取任务伪造尚不存在的条件进度。
class MissionItemContent extends StatelessWidget {
  const MissionItemContent(this.item, {required this.available, super.key});
  final DataMap item;
  final bool available;

  @override
  Widget build(BuildContext context) {
    final status = available ? item['activationStatus'] : item['status'];
    final states = item['conditionStates'];
    final presentation = label(item['presentationStatus'], '');
    return ExtensionItemCard(
      title: label(item['title'], '未命名任务'),
      status: _ItemStatus(
        _missionStatus(status),
        positive: status == 'fulfilled' || status == 'available',
        warning: status == 'unverified',
      ),
      children: [
        _DetailField('角色', label(item['characterName'], '未知')),
        _DetailField(
          '接取人',
          label(
            item['receiverLabel'],
            presentation == 'no-receiver' ? '无接取人' : '未知',
          ),
        ),
        _DetailField('地点', _sceneNames(item)),
        if (!available) ...[
          _DetailField(
            '条件进度',
            '${_count(item['completedConditionCount'])} / ${_count(item['conditionCount'])}',
          ),
          if (states is List && states.isNotEmpty) ...[
            const SizedBox(height: 7),
            // 条件使用只读列表，不使用复选控件或料理标签，避免暗示可以手工勾选。
            for (final (index, state) in states.indexed)
              _ConditionLine(index + 1, state),
          ],
        ] else ...[
          _DetailField('方式', _activationMode(item['activationMode'])),
          _DetailField('触发', _trigger(item['triggerKind'])),
          _DetailField('时机', _timing(item['sourceTiming'])),
          const SizedBox(height: 7),
          _DescriptionBlock('触发说明', _activationHint(item['activationHint'])),
        ],
        if (presentation.isNotEmpty && presentation != 'ready') ...[
          const SizedBox(height: 8),
          _DescriptionBlock(
            '资料说明',
            presentation == 'no-receiver' ? '此任务没有接取人。' : '角色或地点资料尚不完整。',
          ),
        ],
      ],
    );
  }
}

/// 邀请采用独立的紧凑人物项目，不沿用任务文档的逐字段外壳：姓名与操作同排，状态与
/// 羁绊同排，实时地点与读取状态相邻。长姓名和说明自然换行，不用固定高度裁掉事实。
/// 当前协议没有到访时间字段，不从羁绊/地图推算时间；服务器 reason 始终完整保留，
/// 尤其不能把失败或邀请资格原因藏到展开操作里。权限仍由父工作区校验，此处只展示。
class InvitationItemContent extends StatelessWidget {
  const InvitationItemContent(this.item, {required this.onInvite, super.key});
  final DataMap item;
  final VoidCallback? onInvite;

  @override
  Widget build(BuildContext context) {
    final level = item['kizunaLevel'];
    final name = label(item['name'], '未知稀客');
    final location = _invitationLocation(item);
    final captionStyle = StewardText.caption.copyWith(
      color: context.theme.colors.mutedForeground,
    );
    return Semantics(
      container: true,
      explicitChildNodes: true,
      child: Container(
        margin: const EdgeInsets.only(bottom: 8),
        padding: const EdgeInsets.all(10),
        decoration: BoxDecoration(
          border: Border.all(color: context.theme.colors.border),
          borderRadius: BorderRadius.circular(6),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              key: const ValueKey('invitation-heading-row'),
              children: [
                Expanded(
                  child: EntityName(
                    name,
                    kind: EntityImageKind.rareGuest,
                    id: entityImageId(item['id']),
                    fallback: FLucideIcons.userRound,
                    style: StewardText.itemTitle,
                  ),
                ),
                const SizedBox(width: 8),
                // 邀请仍使用统一按钮高度和原父级回调，不缩小触屏命中区域；
                // 仅移入标题行，避免每位客人额外占一整行操作区。
                StewardButton('邀请此稀客', onPressed: onInvite),
              ],
            ),
            const SizedBox(height: StewardDensity.supportingGap),
            StewardBaselineRow(
              children: [
                Expanded(
                  child: _ItemStatus(
                    _invitationStatus(item['status']),
                    positive:
                        item['status'] == 'available' ||
                        item['status'] == 'invited',
                    warning:
                        item['status'] != 'available' &&
                        item['status'] != 'invited',
                  ),
                ),
                const SizedBox(width: 12),
                StewardBaselineRow(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text('羁绊', style: captionStyle),
                    const SizedBox(width: 6),
                    Text(
                      level is int && level >= 0 ? '$level 级' : '未知',
                      style: StewardText.body,
                    ),
                  ],
                ),
              ],
            ),
            const SizedBox(height: StewardDensity.supportingGap),
            Semantics(
              container: true,
              label: '实时地点',
              child: StewardBaselineRow(
                children: [
                  ExcludeSemantics(
                    child: StewardFirstLineCenter(
                      textStyle: StewardText.body,
                      referenceText: location,
                      child: Icon(
                        FLucideIcons.mapPin,
                        size: 15,
                        color: context.theme.colors.mutedForeground,
                      ),
                    ),
                  ),
                  const SizedBox(width: 6),
                  Expanded(
                    // 地点与读取状态共同组成一条事实；窄屏允许状态换行，不能把
                    // “未登记/读取失败”省略成无依据的“其它地图”。
                    child: Wrap(
                      spacing: 8,
                      runSpacing: 2,
                      crossAxisAlignment: WrapCrossAlignment.center,
                      children: [
                        Text(location, style: StewardText.body),
                        Text(
                          _invitationLocationStatus(item),
                          style: captionStyle,
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: StewardDensity.supportingGap),
            _DetailField('邀请说明', label(item['reason'], '邀请条件尚未确认。')),
          ],
        ),
      ),
    );
  }
}

/// 定义列表采用稳定的字段列；值可以换行，后续行保持与值列对齐。
/// 字段宽度随字号增长，避免150%时四字标题挤成两行；最多占项目宽度的38%。
class _DetailField extends StatelessWidget {
  const _DetailField(this.caption, this.value);
  final String caption, value;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 3),
    child: LayoutBuilder(
      builder: (context, constraints) {
        final desired = MediaQuery.textScalerOf(context).scale(64);
        final width = desired.clamp(0.0, constraints.maxWidth * .38).toDouble();
        // 标签和正文大小不同，按真实字体首基线排齐；多行原因仍只在值列换行。
        return StewardBaselineRow(
          children: [
            SizedBox(
              width: width,
              child: Text(
                caption,
                style: StewardText.caption.copyWith(
                  color: context.theme.colors.mutedForeground,
                ),
              ),
            ),
            const SizedBox(width: 10),
            Expanded(child: Text(value, style: StewardText.body)),
          ],
        );
      },
    ),
  );
}

/// 仅标题状态使用一个轻量圆点和文字；正文事实没有胶囊背景、装饰图标或多色边框。
class _ItemStatus extends StatelessWidget {
  const _ItemStatus(this.text, {this.positive = false, this.warning = false});
  final String text;
  final bool positive, warning;
  @override
  Widget build(BuildContext context) {
    final dark = context.theme.colors.background.computeLuminance() < .3;
    final tone = positive
        ? Color(dark ? 0xff9fddbb : 0xff30694e)
        : warning
        ? Color(dark ? 0xffffc195 : 0xff965230)
        : context.theme.colors.mutedForeground;
    final style = StewardText.caption.copyWith(
      fontWeight: FontWeight.w600,
      color: context.stewardPaint.foreground(tone),
    );
    return Semantics(
      container: true,
      label: '状态：$text',
      excludeSemantics: true,
      child: StewardBaselineRow(
        mainAxisSize: MainAxisSize.min,
        children: [
          // 圆点按相邻文字的实际首行中心定位，中文字体和系统缩放均不需要补像素。
          StewardFirstLineCenter(
            textStyle: style,
            referenceText: text,
            child: Container(
              width: 6,
              height: 6,
              decoration: BoxDecoration(
                color: context.stewardPaint.foreground(tone),
                shape: BoxShape.circle,
              ),
            ),
          ),
          const SizedBox(width: 6),
          Flexible(child: Text(text, style: style)),
        ],
      ),
    );
  }
}

class _ConditionLine extends StatelessWidget {
  const _ConditionLine(this.number, this.state);
  final int number;
  final Object? state;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 3),
    child: StewardBaselineRow(
      children: [
        StewardFirstLineCenter(
          textStyle: StewardText.caption,
          referenceText: '条件 $number',
          child: Icon(
            state == true
                ? FLucideIcons.circleCheck
                : state == false
                ? FLucideIcons.circle
                : FLucideIcons.circleHelp,
            size: 15,
            color: context.theme.colors.mutedForeground,
          ),
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Text(
            '条件 $number：${state == true
                ? '已完成'
                : state == false
                ? '未完成'
                : '未知'}',
            style: StewardText.caption,
          ),
        ),
      ],
    ),
  );
}

/// 说明段落与字段表分开：较长的邀请原因/触发提示不再撑开一排标签。
class _DescriptionBlock extends StatelessWidget {
  const _DescriptionBlock(this.caption, this.text);
  final String caption, text;
  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(
        caption,
        style: StewardText.caption.copyWith(
          color: context.theme.colors.mutedForeground,
        ),
      ),
      const SizedBox(height: 3),
      Text(text, style: StewardText.body),
    ],
  );
}

String _sceneNames(DataMap item) {
  final scenes = values(item['sceneNames'])
      .whereType<String>()
      .where((name) => name.isNotEmpty)
      .toList(growable: false);
  return scenes.isEmpty ? '未知' : scenes.join(' · ');
}

/// 位置只来自本次精确当前地图登记读取。未登记不能推断客人在其它地图；
/// 读取失败也不能将目录中的静态出没地点误当成实时地点。地图没有本地化名称
/// 时保留服务端提供的地图标识，不按角色名称或静态目录自行补联结。
String _invitationLocation(DataMap item) {
  if (item['locationStatus'] != 'current-map') return '位置未确认';
  final names = values(item['sceneNames'])
      .whereType<String>()
      .where((value) => value.trim().isNotEmpty)
      .toList(growable: false);
  if (names.isNotEmpty) return names.join(' · ');
  final labels = values(item['sceneLabels'])
      .whereType<String>()
      .where((value) => value.trim().isNotEmpty)
      .toList(growable: false);
  return labels.isEmpty ? '当前地图（名称未提供）' : '地图标识：${labels.join(' · ')}';
}

String _invitationLocationStatus(DataMap item) =>
    switch (item['locationStatus']) {
      'current-map' => '当前地图已登记',
      'not-in-current-map' => '当前地图未登记',
      'unavailable' => '位置读取失败',
      _ => '位置读取状态未知',
    };

String _count(Object? value) => value is int && value >= 0 ? '$value' : '未知';
String _missionStatus(Object? value) => switch (value) {
  'tracking' => '进行中',
  'fulfilled' => '可完成',
  'unverified' => '待核验',
  'available' => '可触发',
  'triggering' => '正在触发',
  _ => '状态未确认',
};
String _activationMode(Object? value) => switch (value) {
  'automatic' => '自动触发',
  'conditional' => '满足条件触发',
  'multiple' => '多种方式',
  _ => '方式未确认',
};
String _trigger(Object? value) => switch (value) {
  'enter-day-scene-map' => '进入目标日间地图',
  'enter-day-scene' => '进入日间场景',
  'kizuna-checkpoint' => '羁绊检查点',
  'multiple' => '多个触发来源',
  _ => '触发来源未确认',
};
String _timing(Object? value) => switch (value) {
  'before-performance' => '演出前',
  'after-performance' => '演出后',
  'multiple' => '多个时机',
  _ => '时机未确认',
};
String _activationHint(Object? value) => switch (value) {
  'enter-target-day-map' => '进入目标日间地图后，由游戏触发。',
  'enter-day-scene' => '进入日间场景后，由游戏触发。',
  'kizuna-ready' => '羁绊条件已就绪，等待游戏检查点触发。',
  'native-start-pending' => '游戏正在启动该任务，请稍候。',
  'multiple-sources' => '该任务有多个触发来源。',
  _ => '触发方式尚未确认，请刷新后再查看。',
};
String _invitationStatus(Object? value) => switch (value) {
  'available' => '可邀请',
  'invited' => '已邀请',
  'unavailable' => '当前不可用',
  'kizuna-uninitialized' => '羁绊未初始化',
  'low-kizuna' => '羁绊不足',
  'missing-dialog' => '无邀请对话',
  'failed' => '邀请失败',
  _ => '状态未确认',
};

enum InventorySortColumn { name, quantity }

/// 库存只按非负整数 ID 对应的事实数量展示；不接受 null、负 ID 或名称猜测。
Object? inventoryQuantity(DataMap entry, DataMap quantities) {
  final id = entry['id'];
  return id is int && id >= 0 ? quantities['$id'] : null;
}

String inventoryStockText(DataMap entry, DataMap quantities) =>
    menuStockText(inventoryQuantity(entry, quantities));

/// 修改只接受快照明确给出的非负整数数量。-1 是游戏的无限库存哨兵，不能通过
/// 设置数量把它转换为有限库存；缺字段、非法负值和非整数也不能假定为零。
/// 此检查只负责客户端选择资格，实际写入仍由 Mod 主线程重新读取并验证。
bool inventoryQuantityEditable(DataMap entry, DataMap quantities) {
  final quantity = inventoryQuantity(entry, quantities);
  return quantity is int && quantity >= 0;
}

/// 单项快捷调整必须恰好增加或减少10，不把越界请求静默夹到0/9999。
/// 这里只转换已确认的整数数量；未知、无限、非法负值及其它步长均不产生写入目标。
/// 调用方在点击时重新读取最新快照，并沿用真实单项库存接口的主设备与回读校验。
int? inventoryAdjustmentTarget(Object? quantity, int delta) {
  if (quantity is! int || quantity < 0 || (delta != -10 && delta != 10))
    return null;
  final target = quantity + delta;
  return target >= 0 && target <= 9999 ? target : null;
}

/// 生产快照的库存映射采用稀疏编码：原生采集逐个读取完整目录，但不序列化数量0。
/// 只有运行时和目录明确完整、数量映射与可用ID集合互相一致时，才为当前目录补0。
/// 该副本仅用于库存修改页面；未加载、缺字段或矛盾快照不得借此制造可写的零库存。
DataMap inventoryEditingQuantities(
  List<DataMap> catalog,
  DataMap runtime, {
  required String type,
  required bool complete,
}) {
  // 服务端的业务候选兜底可能含合成99。只有明确的实读来源标记才允许展示数量；
  // 标记缺失/false时连显式数值也不能当真实库存，不以场景/目录就绪替代此判断。
  if (runtime['inventoryReadReady'] != true) return const {};
  final ingredient = type == 'ingredient';
  final raw = runtime[ingredient ? 'ownedIngredientQty' : 'ownedBeverageQty'];
  final quantities = object(raw);
  if (!complete || raw is! Map) return quantities;
  final available =
      runtime[ingredient ? 'availableIngredientIds' : 'availableBeverageIds'];
  if (available is! List ||
      available.any((id) => id is! int || id < 0) ||
      available.toSet().length != available.length)
    return quantities;
  final availableIds = available.cast<int>().toSet();
  final nonzeroIds = <int>{};
  for (final entry in quantities.entries) {
    final id = int.tryParse(entry.key);
    final count = entry.value;
    if (id == null ||
        id < 0 ||
        '$id' != entry.key ||
        count is! int ||
        count < -1)
      return quantities;
    if (count != 0) nonzeroIds.add(id);
  }
  if (availableIds.length != nonzeroIds.length ||
      !availableIds.containsAll(nonzeroIds))
    return quantities;
  return {
    ...quantities,
    for (final entry in catalog)
      if (entry['id'] case final int id
          when id >= 0 && !quantities.containsKey('$id'))
        '$id': 0,
  };
}

String? inventoryReadOnlyReason(DataMap entry, DataMap quantities) {
  if (inventoryQuantityEditable(entry, quantities)) return null;
  return inventoryQuantity(entry, quantities) == -1
      ? '无限库存，不能修改数量。'
      : '库存数量未知，读取到有效数量后才能修改。';
}

/// 排序仅改变本地浏览顺序，不修改目录或业务数据。未知库存无论升降序都放在
/// 最后；-1 按无限处理。相同字段再按名称、ID、原索引排序，保证轮询时不抖动。
List<DataMap> sortInventoryEntries(
  List<DataMap> entries,
  DataMap quantities, {
  required InventorySortColumn column,
  required bool ascending,
}) {
  final indexed = entries.indexed.toList();
  int name(DataMap a, DataMap b) =>
      label(a['name']).compareTo(label(b['name']));
  num? stock(DataMap entry) {
    final raw = inventoryQuantity(entry, quantities);
    if (raw == -1) return double.infinity;
    return raw is num && raw.isFinite && raw >= 0 ? raw : null;
  }

  indexed.sort((a, b) {
    int result;
    if (column == InventorySortColumn.quantity) {
      final first = stock(a.$2), second = stock(b.$2);
      if (first == null && second != null) return 1;
      if (second == null && first != null) return -1;
      result = first == null ? 0 : first.compareTo(second!);
    } else {
      result = name(a.$2, b.$2);
    }
    if (result != 0) return ascending ? result : -result;
    final names = name(a.$2, b.$2);
    if (names != 0) return names;
    final firstId = a.$2['id'], secondId = b.$2['id'];
    if (firstId is int && secondId is int && firstId != secondId)
      return firstId.compareTo(secondId);
    return a.$1.compareTo(b.$1);
  });
  return [for (final entry in indexed) entry.$2];
}
