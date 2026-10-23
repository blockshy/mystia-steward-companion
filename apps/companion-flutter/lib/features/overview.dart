import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/page_activity.dart';
import '../ui/steward_opacity.dart';
import '../ui/steward_style.dart';
import 'extension_item_content.dart' show inventoryEditingQuantities;

/// 总览只呈现场景、今日供给和低库存；自动化及待确认事项集中在经营统计，连接管理交由设置页。
/// 本页整理的都是宿主已提供的只读事实，不根据供给数量推导“可立即制作”或执行权限。
class OverviewPage extends StatelessWidget {
  const OverviewPage(this.model, {super.key});
  final WorkbenchModel model;

  @override
  Widget build(BuildContext context) {
    final ready = model.snapshot['runtimeDataComplete'] == true;
    return PageActivityReporter(
      // 连接与目录提示仍投影到页头；移走连接明细不能掩盖当前数据不可用的状态。
      activity: !model.connected
          ? const PageActivity(message: '未连接', details: '请在设置中核对游戏电脑地址与凭据。')
          : !ready
          ? const PageActivity(message: '游戏数据尚未就绪', details: '等待游戏提供完整目录与库存数据。')
          : null,
      child: ListView(
        children: [
          Section('经营状态', [
            Fact(
              '当前地点',
              model.night['place'] ?? model.snapshot['activeDayMapName'],
            ),
            Fact('游戏场景', model.snapshot['activeSceneName']),
            // 今日供给并入同一经营概况；这是可选目录范围，并非已验证的制作能力。
            Fact(
              '可选料理',
              model.connected &&
                      ready &&
                      model.runtime['availableRecipeIds'] is List
                  ? values(model.runtime['availableRecipeIds']).length
                  : '尚未就绪',
            ),
            Fact(
              '可选酒水',
              model.connected &&
                      ready &&
                      model.runtime['availableBeverageIds'] is List
                  ? values(model.runtime['availableBeverageIds']).length
                  : '尚未就绪',
            ),
            Fact(
              '流行喜爱',
              model.connected && ready ? model.runtime['popularFoodTag'] : null,
            ),
            Fact(
              '流行厌恶',
              model.connected && ready
                  ? model.runtime['popularHateFoodTag']
                  : null,
            ),
          ]),
          Section('低库存', [
            _OverviewStockTable(
              ingredients: _stock('ingredient', 'ingredients', ready),
              beverages: _stock('beverage', 'beverages', ready),
            ),
          ]),
        ],
      ),
    );
  }

  /// 数量必须来自真实库存读取。候选兜底的 AllAvailable/99、未连接时保留的旧快照，
  /// 都不能成为低库存事实。复用库存页对完整稀疏编码的核验，只有该核验通过才补0。
  /// 排序只是本地展示：每类独立按数量、精确ID取前五项，不改变任何业务候选或配置。
  _OverviewStockGroup _stock(String type, String catalogKey, bool ready) {
    if (!model.connected) return const _OverviewStockGroup([], '未连接');
    if (!ready) return const _OverviewStockGroup([], '数据尚未就绪');
    if (model.runtime['inventoryReadReady'] != true) {
      return const _OverviewStockGroup([], '尚未读取库存');
    }
    final field = type == 'ingredient'
        ? 'ownedIngredientQty'
        : 'ownedBeverageQty';
    if (model.runtime[field] is! Map) {
      return const _OverviewStockGroup([], '缺少库存数据');
    }
    final catalog = model.catalogItems(catalogKey);
    final quantities = inventoryEditingQuantities(
      catalog,
      model.runtime,
      type: type,
      complete: model.snapshot['runtimeLoaded'] == true && ready,
    );
    final finite = <_OverviewStockEntry>[];
    var unknown = catalog.any((entry) {
      final id = entry['id'];
      final quantity = id is int && id >= 0 ? quantities['$id'] : null;
      return quantity is! int || quantity < -1;
    });
    for (final entry in quantities.entries) {
      final id = int.tryParse(entry.key);
      final quantity = entry.value;
      if (id == null ||
          id < 0 ||
          '$id' != entry.key ||
          quantity is! int ||
          quantity < -1) {
        unknown = true;
        continue;
      }
      // -1为无限库存；不能作为负库存或零库存混入“数量最低”列表。
      if (quantity == -1) continue;
      finite.add(
        _OverviewStockEntry(id, model.itemName(catalogKey, id), quantity),
      );
    }
    finite.sort((a, b) {
      final count = a.quantity.compareTo(b.quantity);
      return count == 0 ? a.id.compareTo(b.id) : count;
    });
    return _OverviewStockGroup(
      finite.take(5).toList(),
      unknown ? '库存数量未知' : '无有限库存记录',
      partial: unknown,
    );
  }
}

class _OverviewStockEntry {
  const _OverviewStockEntry(this.id, this.name, this.quantity);
  final int id;
  final String name;
  final int quantity;
}

class _OverviewStockGroup {
  const _OverviewStockGroup(
    this.entries,
    this.emptyLabel, {
    this.partial = false,
  });
  final List<_OverviewStockEntry> entries;
  final String emptyLabel;
  final bool partial;
}

/// 两类库存共享四个明确的列：食材名称/数量、酒水名称/数量。数量列使用受约束的
/// 弹性宽度，长数值允许换行，避免大字号或异常大数量把名称列挤到视口之外；名称
/// 同样完整换行，不省略真实资源名称。两侧分别最多五项，缺项只显示破折号，不补零。
class _OverviewStockTable extends StatelessWidget {
  const _OverviewStockTable({
    required this.ingredients,
    required this.beverages,
  });
  final _OverviewStockGroup ingredients;
  final _OverviewStockGroup beverages;

  @override
  Widget build(BuildContext context) {
    final rows = ingredients.entries.length > beverages.entries.length
        ? ingredients.entries.length
        : beverages.entries.length;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Table(
          key: const ValueKey('overview-stock-table'),
          columnWidths: const {
            0: FlexColumnWidth(3),
            1: FlexColumnWidth(1),
            2: FlexColumnWidth(3),
            3: FlexColumnWidth(1),
          },
          defaultVerticalAlignment: TableCellVerticalAlignment.top,
          children: [
            TableRow(
              children: [
                _header('食材名称', 'ingredient-name'),
                _header(
                  '库存',
                  'ingredient-quantity',
                  quantity: true,
                  gutter: true,
                ),
                _header('酒水名称', 'beverage-name', second: true),
                _header('库存', 'beverage-quantity', quantity: true),
              ],
            ),
            for (var row = 0; row < (rows == 0 ? 1 : rows); row++)
              TableRow(
                children: [
                  ..._cells(context, ingredients, 'ingredient', row),
                  ..._cells(context, beverages, 'beverage', row, second: true),
                ],
              ),
          ],
        ),
        const SizedBox(height: 8),
        Text(
          '每类最多 5 项 · 不含无限库存',
          style: StewardText.caption.copyWith(
            color: context.theme.colors.mutedForeground,
          ),
        ),
        if (ingredients.partial || beverages.partial)
          Text(
            '${[if (ingredients.partial) '食材', if (beverages.partial) '酒水'].join('、')}部分数量未知，仅列出已确认的有限库存。',
            style: StewardText.caption.copyWith(
              color: context.theme.colors.mutedForeground,
            ),
          ),
      ],
    );
  }

  Widget _header(
    String text,
    String key, {
    bool quantity = false,
    bool second = false,
    bool gutter = false,
  }) => Padding(
    key: ValueKey('overview-stock-header-$key'),
    padding: EdgeInsets.only(
      left: second ? 8 : 0,
      right: gutter
          ? 8
          : quantity
          ? 0
          : 4,
      bottom: 6,
    ),
    child: Text(
      text,
      textAlign: quantity ? TextAlign.end : TextAlign.start,
      style: StewardText.value,
    ),
  );

  List<Widget> _cells(
    BuildContext context,
    _OverviewStockGroup group,
    String type,
    int row, {
    bool second = false,
  }) {
    final entry = row < group.entries.length ? group.entries[row] : null;
    final name = entry?.name ?? (row == 0 ? group.emptyLabel : '—');
    return [
      Padding(
        key: ValueKey('overview-stock-$type-name-$row'),
        padding: EdgeInsets.only(
          left: second ? 8 : 0,
          right: 4,
          top: 6,
          bottom: 6,
        ),
        child: Semantics(
          // 表格的数量是独立列，读屏仍需把资源类型、名称与数量作为一项读出，
          // 不把左右两类的相邻数字误理解为同一资源的两种数量。
          label:
              '${type == 'ingredient' ? '食材' : '酒水'}，$name${entry == null ? '' : '，库存${entry.quantity}'}',
          excludeSemantics: true,
          child: StewardBaselineRow(
            children: [
              if (entry != null) ...[
                StewardFirstLineCenter(
                  referenceText: name,
                  textStyle: StewardText.body,
                  child: EntityImage(
                    size: StewardImageSize.tag,
                    kind: type == 'ingredient'
                        ? EntityImageKind.ingredient
                        : EntityImageKind.beverage,
                    id: entry.id,
                    fallback: type == 'ingredient'
                        ? FLucideIcons.leaf
                        : FLucideIcons.glassWater,
                  ),
                ),
                const SizedBox(width: 4),
              ],
              Expanded(child: Text(name, style: StewardText.body)),
            ],
          ),
        ),
      ),
      ExcludeSemantics(
        child: Container(
          key: ValueKey('overview-stock-$type-quantity-$row'),
          padding: EdgeInsets.only(top: 6, bottom: 6, right: second ? 0 : 8),
          decoration: second
              ? null
              : BoxDecoration(
                  border: Border(
                    right: BorderSide(
                      color: context.stewardPaint.foreground(
                        context.theme.colors.border,
                      ),
                    ),
                  ),
                ),
          child: Text(
            entry == null ? '—' : '${entry.quantity}',
            textAlign: TextAlign.end,
            style: StewardText.value,
          ),
        ),
      ),
    ];
  }
}
