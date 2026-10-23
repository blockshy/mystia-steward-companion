import 'package:flutter/widgets.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/menu_item_content.dart';
import '../ui/page_activity.dart';
import '../ui/page_toolbar.dart';
import 'catalog_item_content.dart';
import 'resource_management.dart';

/// 数据工作区直接浏览宿主完整目录，不启动推荐查询，也不借用库存或候选结果补齐
/// 图鉴。三个子页保留各自草稿与滚动位置，隐藏子树不能继续占用焦点或拦截系统返回。
class DataPage extends StatefulWidget {
  const DataPage(this.model, {this.active = true, super.key});
  final WorkbenchModel model;
  final bool active;

  @override
  State<DataPage> createState() => _DataPageState();
}

class _DataPageState extends State<DataPage> {
  String _tab = 'food';
  static const _tabs = {'food': '料理', 'beverage': '酒水', 'resources': '资源管理'};
  final _visited = <String>{};

  @override
  void initState() {
    super.initState();
    if (widget.active) _visited.add(_tab);
  }

  @override
  void didUpdateWidget(covariant DataPage oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.active) _visited.add(_tab);
  }

  void _select(String value) {
    if (!widget.active || !_tabs.containsKey(value)) return;
    setState(() {
      _tab = value;
      _visited.add(value);
    });
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.model,
    builder: (context, child) => Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        WorkspaceTabs<String>(
          key: const ValueKey('data-tabs'),
          items: _tabs,
          value: _tab,
          horizontalScroll: !widget.model.desktop,
          enabled: widget.active,
          onChanged: _select,
        ),
        Expanded(
          child: IndexedStack(
            index: _tabs.keys.toList().indexOf(_tab),
            children: [
              for (final tab in _tabs.keys)
                TickerMode(
                  enabled: widget.active && _tab == tab,
                  child: ExcludeFocus(
                    excluding: !widget.active || _tab != tab,
                    // TickerMode 只暂停动画与交互，并不会阻止 build。图片解析器在
                    // build 中就可能排入下载队列，因此首次进入数据页之前不挂载任何
                    // 内容，子页也必须实际访问后才挂载；以后保留其 State，避免丢草稿。
                    // 未访问页保持纯空槽，普通模型轮询绝不能把它们变成提前下载入口。
                    child: !_visited.contains(tab)
                        ? const SizedBox.expand()
                        : tab == 'resources'
                        ? ResourceManagementPage(widget.model)
                        : _CatalogBrowser(
                            widget.model,
                            food: tab == 'food',
                            active: widget.active && _tab == tab,
                            key: ValueKey('data-browser-$tab'),
                          ),
                  ),
                ),
            ],
          ),
        ),
      ],
    ),
  );
}

/// 每类目录独立保存名称草稿、筛选及详情选择。轮询只替换只读事实，不覆盖用户的
/// TextEditingController；翻页和已解锁状态变化也不发起任何共享配置或游戏写操作。
class _CatalogBrowser extends StatefulWidget {
  const _CatalogBrowser(
    this.model, {
    required this.food,
    required this.active,
    super.key,
  });
  final WorkbenchModel model;
  final bool food;
  final bool active;
  @override
  State<_CatalogBrowser> createState() => _CatalogBrowserState();
}

class _CatalogBrowserState extends State<_CatalogBrowser> {
  final _search = TextEditingController();
  String _filter = 'unlocked';
  String get _kind => widget.food ? 'food' : 'beverage';
  String get _name => widget.food ? '料理' : '酒水';

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final model = widget.model;
    final complete = model.catalog['isComplete'] == true;
    final facts = CatalogUnlockFacts.read(model, food: widget.food);
    final query = _search.text.trim().toLowerCase();
    final source = complete
        ? model.catalogItems(widget.food ? 'recipes' : 'beverages')
        : const <DataMap>[];
    final occurrences = <String, int>{};
    final items = <DataMap>[];
    for (final entry in source) {
      // 此键只负责列表展示，绝不作为图片身份或业务操作编号。重复/损坏目录仍可
      // 安全展示为独立行；计数先于筛选，避免切换搜索后同一条目的局部序号改变。
      final identity =
          '$_kind:${entry['id']}:${widget.food ? entry['recipeId'] : ''}';
      final occurrence = occurrences.update(
        identity,
        (value) => value + 1,
        ifAbsent: () => 0,
      );
      final state = facts.stateFor(entry);
      final matchesQuery =
          query.isEmpty ||
          label(entry['name'], '').toLowerCase().contains(query) ||
          entry['id'] is int && '${entry['id']}'.contains(query) ||
          widget.food &&
              entry['recipeId'] is int &&
              '${entry['recipeId']}'.contains(query);
      if (_filter == 'unlocked' && state != CatalogUnlockState.unlocked ||
          _filter == 'locked' && state != CatalogUnlockState.locked ||
          !matchesQuery) {
        continue;
      }
      items.add({
        ...entry,
        '_catalogPresentationKey': 'catalog-$identity#$occurrence',
      });
    }
    final unknown = !facts.known;
    final empty = !complete
        ? '等待完整游戏目录'
        : unknown && _filter != 'all'
        ? '$_name解锁状态未确认，可切换“全部”查看目录'
        : query.isNotEmpty
        ? '没有匹配的$_name'
        : _filter == 'locked'
        ? '没有未解锁的$_name'
        : _filter == 'unlocked'
        ? '没有已解锁的$_name'
        : '目录中没有$_name';
    return PageActivityReporter(
      activity: !widget.active
          ? null
          : PageActivity(
              message: !complete
                  ? '等待完整游戏目录'
                  : unknown
                  ? '$_name解锁状态未确认'
                  : '显示 ${items.length} / ${source.length} 项$_name',
              details: unknown
                  ? '可切换“全部”浏览静态目录。当前存档的解锁记录尚未确认，不会把未知状态当作未解锁，也不会用库存或推荐候选推断。'
                  : widget.food
                  ? null
                  : '酒水按游戏图鉴的收录记录判断；当前库存为零不代表未解锁。',
            ),
      child: LayoutBuilder(
        builder: (context, constraints) => Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // 手机横屏与输入法弹出时工具栏仍可滚动到达，至少保留六成高度给结果。
            // 搜索和筛选始终处于同一结构，不以断点重建正在输入的组合文字。
            ConstrainedBox(
              constraints: BoxConstraints(
                maxHeight: constraints.maxHeight * .4,
              ),
              child: SingleChildScrollView(
                child: PageToolbar(
                  controller: _search,
                  searchKey: ValueKey('catalog-search-$_kind'),
                  searchLabel: '搜索$_name名称或编号',
                  searchHint: '搜索名称或编号',
                  onSearchChanged: (_) => setState(() {}),
                  navigation: WorkspaceTabs<String>(
                    key: ValueKey('catalog-filter-$_kind'),
                    items: const {
                      'unlocked': '已解锁',
                      'locked': '未解锁',
                      'all': '全部',
                    },
                    value: _filter,
                    inToolbar: true,
                    horizontalScroll: true,
                    enabled: widget.active,
                    onChanged: (value) => setState(() => _filter = value),
                  ),
                ),
              ),
            ),
            Expanded(
              child: DenseListDetail(
                key: ValueKey('catalog-list-$_kind-${model.connectionVersion}'),
                items: items,
                identity: (entry) =>
                    entry['_catalogPresentationKey']! as String,
                title: (entry) => label(entry['name'], '名称未知'),
                subtitle: (_) => '',
                titleContent: (entry) => CatalogItemHeader(
                  catalogItemView(model, entry, food: widget.food),
                  facts.stateFor(entry),
                ),
                richSummary: true,
                summary: (entry) => MenuItemContent(
                  catalogItemView(model, entry, food: widget.food),
                  showName: false,
                  showExtras: false,
                ),
                detail: (entry) => CatalogItemDetails(
                  model: model,
                  entry: entry,
                  food: widget.food,
                  unlock: facts.stateFor(entry),
                ),
                empty: empty,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
