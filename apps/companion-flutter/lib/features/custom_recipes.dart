import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/menu_item_content.dart';
import '../ui/page_activity.dart';
import '../ui/page_toolbar.dart';
import '../ui/steward_select.dart';
import '../ui/steward_style.dart';
import '../ui/steward_opacity.dart';
import 'recommendation_item_content.dart';

/// 自定义配方仅在界面分组；启用模式、绑定适用范围和排序由 C# 存储及领域计算。
/// 两种模式各自保留条目，不把切换模式实现成客户端筛选或复制历史记录。
class CustomRecipesPanel extends StatefulWidget {
  const CustomRecipesPanel(this.model, {required this.active, super.key});
  final WorkbenchModel model;
  final bool active;
  @override
  State<CustomRecipesPanel> createState() => _CustomRecipesPanelState();
}

class _CustomRecipesPanelState extends State<CustomRecipesPanel> {
  final search = TextEditingController();
  bool _savingSettings = false;
  String _settingsFailure = '';
  Object? _settingsFailureConnection;
  WorkbenchModel get model => widget.model;
  DataMap get source => model.resources['/custom-recipes'] ?? const {};
  String get mode => label(source['activeMode'], '');
  bool get ready =>
      source['version'] == 2 &&
      const ['food', 'customer'].contains(mode) &&
      source['modeRevision'] is int &&
      (source['modeRevision'] as int) >= 0 &&
      source['enabled'] is bool;
  bool get editable =>
      ready && widget.active && model.canEdit && !_savingSettings;
  Map<String, String> get revision => {
    'expectedModeRevision': '${source['modeRevision']}',
  };
  Map<String, String> get mutationBoundary => {...revision, 'mode': mode};

  Object get _connectionBoundary => (
    model.connectionVersion,
    model.connected,
    model.authority['registryId'],
    model.authority['currentDeviceId'],
    model.authority['primaryDeviceId'],
    model.authority['authorityRevision'],
    model.snapshot['automationSessionId'],
  );
  Object get _settingsBoundary =>
      (_connectionBoundary, mode, source['modeRevision']);

  /// 单选只提交明确的新模式，不乐观修改选中值。回调固定渲染时的连接、权限与
  /// 模式修订；同帧重复点击也受本地保存锁约束，不能靠下一次模型通知才禁用控件。
  Future<void> _saveSettings(
    Map<String, String> changes,
    Object expectedBoundary,
  ) async {
    if (!editable || expectedBoundary != _settingsBoundary) return;
    if (changes['activeMode'] == mode ||
        changes['enabled'] == '${source['enabled']}')
      return;
    final connection = _connectionBoundary;
    final query = {...revision, ...changes};
    setState(() {
      _savingSettings = true;
      _settingsFailure = '';
      _settingsFailureConnection = null;
    });
    try {
      await model.mutate('/custom-recipes/settings', query: query);
      if (mounted && connection == _connectionBoundary) {
        _settingsFailure = model.error;
        _settingsFailureConnection = connection;
      }
    } catch (cause) {
      if (mounted && connection == _connectionBoundary) {
        _settingsFailure = cause.toString();
        _settingsFailureConnection = connection;
      }
    } finally {
      if (mounted) setState(() => _savingSettings = false);
    }
  }

  /// 通用读取、保存及失败状态只投影到本页页头。控件状态仍留在正文，避免总开关
  /// 关闭时把已保存的模式偏好误称为正在生效；具体编辑器校验不移出操作上下文。
  PageActivity? get _activity {
    if (!widget.active) return null;
    if (!model.connected) return const PageActivity(message: '连接后读取自定义配方');
    if (_savingSettings)
      return const PageActivity(message: '正在保存配方设置…', busy: true);
    if (_settingsFailure.isNotEmpty &&
        _settingsFailureConnection == _connectionBoundary)
      return PageActivity(
        message: '配方设置未保存',
        error: true,
        details: _settingsFailure,
      );
    if (!ready && model.error.isNotEmpty)
      return PageActivity(message: '配方读取失败', error: true, details: model.error);
    if (!ready)
      return source.isEmpty
          ? const PageActivity(message: '正在读取自定义配方…', busy: true)
          : const PageActivity(message: '配方模式数据不可用', error: true);
    return null;
  }

  Widget _modeChoice(String value, String title, Object boundary) {
    final selected = ready && mode == value;
    final colors = context.theme.colors;
    return Expanded(
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: selected
              ? context.stewardPaint.surface(colors.primary, alpha: .09)
              : colors.background,
          border: Border.all(color: selected ? colors.primary : colors.border),
          borderRadius: BorderRadius.circular(6),
        ),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 7),
          child: FRadio(
            key: ValueKey('custom-mode-$value'),
            label: Text(title, style: StewardText.value),
            semanticsLabel: title,
            value: selected,
            enabled: editable,
            // FRadio 对当前项会回调 false；互斥模式不能取消为“零选中”。
            onChange: editable
                ? (checked) {
                    if (checked) _saveSettings({'activeMode': value}, boundary);
                  }
                : null,
          ),
        ),
      ),
    );
  }

  @override
  void dispose() {
    search.dispose();
    super.dispose();
  }

  /// 只按服务端的模式与排序字段建立展示分组；同一组合的不同加料配方仍保留 ID。
  List<DataMap> get groups {
    final entries =
        records(source['recipes']).where((row) => row['mode'] == mode).toList()
          ..sort((a, b) {
            // 与服务端组内移动使用同一并列顺序，否则历史相同 sortOrder 会移动错邻居。
            final order = ((a['sortOrder'] as num?) ?? 0).compareTo(
              (b['sortOrder'] as num?) ?? 0,
            );
            if (order != 0) return order;
            final aTime =
                DateTime.tryParse(label(a['createdAtUtc'], '')) ??
                DateTime.utc(1);
            final bTime =
                DateTime.tryParse(label(b['createdAtUtc'], '')) ??
                DateTime.utc(1);
            final time = aTime.compareTo(bTime);
            return time != 0
                ? time
                : label(a['id'], '').compareTo(label(b['id'], ''));
          });
    final grouped = <Object?, List<DataMap>>{};
    for (final entry in entries) {
      grouped
          .putIfAbsent(
            entry[mode == 'food' ? 'foodId' : 'customerId'],
            () => [],
          )
          .add(entry);
    }
    final needle = search.text.trim().toLowerCase();
    return [
      for (final group in grouped.entries)
        if (group.value.any(
          (row) => '${row['recipeName']} ${row['customerName']}'
              .toLowerCase()
              .contains(needle),
        ))
          {
            'id': '$mode:${group.key}',
            'name': label(
              group.value.first[mode == 'food' ? 'recipeName' : 'customerName'],
            ),
            'entries': group.value,
          },
    ];
  }

  Future<void> _edit({
    DataMap existing = const {},
    DataMap seed = const {},
  }) async {
    if (!editable) return;
    final editingMode = mode;
    // 弹窗自己固定连接及模式代次，等待选择和保存期间不跟随后台切模。
    await showFDialog<void>(
      context: context,
      builder: (context, style, animation) => FDialog(
        animation: MediaQuery.disableAnimationsOf(context) ? null : animation,
        builder: (context, _) {
          final media = MediaQuery.of(context);
          final available =
              media.size.height -
              media.viewInsets.bottom -
              media.padding.vertical;
          return SizedBox(
            width: 560,
            height: (available * .82).clamp(0, 680),
            child: CustomRecipeEditor(
              model,
              mode: editingMode,
              existing: existing,
              seed: seed,
            ),
          );
        },
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final displayed = groups;
    final settingsBoundary = _settingsBoundary;
    return PageActivityReporter(
      activity: _activity,
      child: LayoutBuilder(
        builder: (context, constraints) => Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // 与推荐查询条件一样有界：短横屏/键盘时工具栏可以滚动，主体仍保留浏览空间。
            ConstrainedBox(
              constraints: BoxConstraints(
                maxHeight: constraints.maxHeight * .65,
              ),
              child: SingleChildScrollView(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    PageToolbar(
                      key: const ValueKey('custom-recipe-toolbar'),
                      controller: search,
                      searchLabel: '筛选料理 / 稀客',
                      searchKey: const ValueKey('custom-recipe-search'),
                      onSearchChanged: (_) => setState(() {}),
                      actions: [
                        Text(
                          '共 ${displayed.length} 项',
                          style: StewardText.caption,
                        ),
                        StewardButton(
                          '新增配方',
                          primary: true,
                          onPressed: editable ? () => _edit() : null,
                        ),
                      ],
                    ),
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            !ready
                                ? '启用状态未确认'
                                : source['enabled'] != true
                                ? '未启用'
                                : '当前启用：${mode == 'food' ? '料理模式' : '稀客模式'}',
                            key: const ValueKey('custom-active-mode-status'),
                            style: StewardText.value,
                          ),
                        ),
                        const SizedBox(width: 8),
                        FSwitch(
                          key: const ValueKey('custom-enabled'),
                          value: source['enabled'] == true,
                          enabled: editable,
                          semanticsLabel: '参与推荐计算',
                          onChange: editable
                              ? (enabled) => _saveSettings({
                                  'enabled': '$enabled',
                                }, settingsBoundary)
                              : null,
                        ),
                      ],
                    ),
                    const SizedBox(height: 6),
                    Row(
                      children: [
                        _modeChoice('food', '料理模式', settingsBoundary),
                        const SizedBox(width: 8),
                        _modeChoice('customer', '稀客模式', settingsBoundary),
                      ],
                    ),
                    const SizedBox(height: 8),
                  ],
                ),
              ),
            ),
            Expanded(
              child: !ready
                  ? const SizedBox.expand()
                  : DenseListDetail(
                      key: ValueKey('custom-groups-$mode'),
                      items: displayed,
                      identity: (group) => '${group['id']}',
                      title: (group) => label(group['name']),
                      titleContent: _groupHeading,
                      subtitle: (group) {
                        final rows = records(group['entries']);
                        final names = rows
                            .map(
                              (row) => label(
                                row[mode == 'food'
                                    ? 'customerName'
                                    : 'recipeName'],
                              ),
                            )
                            .toSet()
                            .join('、');
                        return '${rows.length} 个绑定 · $names';
                      },
                      empty: '当前模式没有匹配的配方',
                      detail: _groupDetails,
                    ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _groupDetails(DataMap group) {
    final entries = records(group['entries']);
    final boundary = mutationBoundary;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(child: _groupHeading(group, details: true)),
            StewardButton(
              mode == 'food' ? '绑定稀客' : '添加料理',
              onPressed: editable
                  ? () => _edit(
                      seed: {
                        for (final key
                            in mode == 'food'
                                ? ['foodId', 'recipeId', 'recipeName']
                                : ['customerId', 'customerName'])
                          key: entries.first[key],
                      },
                    )
                  : null,
            ),
          ],
        ),
        const SizedBox(height: 8),
        for (var index = 0; index < entries.length; index++)
          _binding(
            entries[index],
            index: index,
            length: entries.length,
            boundary: boundary,
          ),
      ],
    );
  }

  /// 分组 ID 是界面复合键，不能拿来找图片；读取真实绑定上的 Food/Customer ID。
  Widget _groupHeading(DataMap group, {bool details = false}) {
    final first = records(group['entries']).firstOrNull;
    return EntityName(
      label(group['name']),
      kind: mode == 'food' ? EntityImageKind.food : EntityImageKind.rareGuest,
      id: entityImageId(first?[mode == 'food' ? 'foodId' : 'customerId']),
      fallback: mode == 'food' ? FLucideIcons.utensils : FLucideIcons.userRound,
      // 列表沿用 DenseListDetail 原标题的继承字号与行高；详情才用条目标题，
      // 不因添加图片让列表标题从13px跳成16px或改变原有阅读密度。
      style: details
          ? StewardText.itemTitle
          : const TextStyle(fontWeight: FontWeight.w600, height: 1.35),
    );
  }

  Widget _binding(
    DataMap row, {
    required int index,
    required int length,
    required Map<String, String> boundary,
  }) {
    final id = label(row['id'], '');
    void mutate(String path, Map<String, String> fields) {
      // 已显示的按钮也校验当前模式代次，不等待网络端再发现过期操作。
      if (editable && mapEquals(boundary, mutationBoundary))
        model.mutate(path, query: {...boundary, 'id': id, ...fields});
    }

    return Section(
      label(row[mode == 'food' ? 'customerName' : 'recipeName']),
      [
        MenuItemContent(
          recommendationItemView(
            model,
            row,
            section: RecommendationSection.custom,
            food: true,
          ),
          showName: mode == 'food',
        ),
        const SizedBox(height: 8),
        ToolRow([
          StewardButton(
            '编辑',
            key: ValueKey('custom-edit-$id'),
            onPressed: editable ? () => _edit(existing: row) : null,
          ),
          StewardButton(
            row['enabled'] == true ? '停用' : '启用',
            onPressed: editable
                ? () => mutate('/custom-recipes/update-flags', {
                    'scope': 'entry',
                    'enabled': '${row['enabled'] != true}',
                  })
                : null,
          ),
          StewardButton(
            row['pinToTop'] == true ? '取消置顶' : '置顶',
            onPressed: editable
                ? () => mutate('/custom-recipes/update-flags', {
                    'scope': 'entry',
                    'pinToTop': '${row['pinToTop'] != true}',
                  })
                : null,
          ),
          StewardButton(
            '上移',
            onPressed: editable && index > 0
                ? () => mutate('/custom-recipes/move', {'direction': 'up'})
                : null,
          ),
          StewardButton(
            '下移',
            onPressed: editable && index + 1 < length
                ? () => mutate('/custom-recipes/move', {'direction': 'down'})
                : null,
          ),
          StewardButton(
            '删除',
            onPressed: editable
                ? () async {
                    if (await confirmCurrentAction(
                      context,
                      model,
                      '删除自定义配方',
                      '${row['customerName']} · ${row['recipeName']}',
                    ))
                      mutate('/custom-recipes/remove', {});
                  }
                : null,
          ),
        ]),
      ],
      // 绑定标题与组名、推荐、经营共用实体名称行；缺图仍显示旧图标，不能因为
      // 主体已在Section标题中出现就把MenuItemContent隐藏名称后的图片一并漏掉。
      titleContent: EntityName(
        label(row[mode == 'food' ? 'customerName' : 'recipeName']),
        kind: mode == 'food' ? EntityImageKind.rareGuest : EntityImageKind.food,
        id: entityImageId(row[mode == 'food' ? 'customerId' : 'foodId']),
        fallback: mode == 'food'
            ? FLucideIcons.userRound
            : FLucideIcons.utensils,
        style: StewardText.sectionTitle.copyWith(
          color: context.theme.colors.primary,
        ),
      ),
    );
  }
}

/// 配方编辑只收集明确目录 ID 与已选标签。保存仍通过主设备、连接、模式代次检查，
/// 不在 Dart 中计算可用候选或放宽 C# 对材料、厨具和稀客需求的业务约束。
class CustomRecipeEditor extends StatefulWidget {
  const CustomRecipeEditor(
    this.model, {
    required this.mode,
    this.existing = const {},
    this.seed = const {},
    super.key,
  });
  final WorkbenchModel model;
  final String mode;
  final DataMap existing, seed;
  @override
  State<CustomRecipeEditor> createState() => _CustomRecipeEditorState();
}

class _CustomRecipeEditorState extends State<CustomRecipeEditor> {
  late final value = <String, Object?>{...widget.seed, ...widget.existing};
  late final extras = values(value['extraIngredientIds'])
      .whereType<int>()
      .toList();
  late final originalBoundary = _boundary();
  late final modeRevision =
      widget.model.resources['/custom-recipes']?['modeRevision'];
  String? validation;
  bool saving = false;
  WorkbenchModel get model => widget.model;
  List<Object?> _boundary() => [
    model.connectionVersion,
    model.connected,
    model.authority['registryId'],
    model.authority['authorityRevision'],
    model.snapshot['automationSessionId'],
    model.resources['/custom-recipes']?['activeMode'],
    model.resources['/custom-recipes']?['modeRevision'],
  ];
  bool get current => listEquals(originalBoundary, _boundary());
  bool get editable => model.canEdit && !saving && current;
  Object get selectionBoundary => (
    model.connectionVersion,
    model.connected,
    model.authority['registryId'],
    model.authority['authorityRevision'],
    model.snapshot['automationSessionId'],
    model.resources['/custom-recipes']?['activeMode'],
    model.resources['/custom-recipes']?['modeRevision'],
  );

  @override
  void initState() {
    super.initState();
    // 强制在打开时取得边界，不能等到首次保存才初始化 lazy 字段。
    originalBoundary;
    modeRevision;
  }

  /// 目录必须完整且 ID 唯一；不把 Map 的覆盖值与 firstOrNull 的另一条记录拼成绑定。
  List<DataMap> _uniqueCatalog(String key) {
    if (model.catalog['isComplete'] == false) return const [];
    final rows = model.catalogItems(key);
    final counts = <int, int>{};
    for (final row in rows) {
      final id = row['id'];
      if (id is int && id >= 0) counts[id] = (counts[id] ?? 0) + 1;
    }
    return rows.where((row) => counts[row['id']] == 1).toList();
  }

  Map<int, String> _catalog(String key, Object? oldId, String oldName) => {
    // 目录缺失的历史值仍可见，不会因为打开编辑器就删除或改绑它。
    if (oldId is int && oldId >= 0) oldId: oldName,
    for (final row in _uniqueCatalog(key)) row['id'] as int: label(row['name']),
  };

  Widget _entity(bool customer) {
    final key = customer ? 'customerId' : 'foodId';
    final nameKey = customer ? 'customerName' : 'recipeName';
    final catalogKey = customer ? 'rareCustomers' : 'recipes';
    return StewardSelect<int>(
      key: ValueKey('custom-editor-$key'),
      label: customer ? '稀客' : '料理',
      value: value[key] as int?,
      items: _catalog(catalogKey, value[key], label(value[nameKey])),
      searchable: true,
      itemLeading: (id) => EntityImage(
        kind: customer ? EntityImageKind.rareGuest : EntityImageKind.food,
        id: id,
        fallback: customer ? FLucideIcons.userRound : FLucideIcons.utensils,
      ),
      enabled: editable,
      boundary: selectionBoundary,
      onChanged: (id) {
        final entry = _uniqueCatalog(catalogKey)
            .where((row) => row['id'] == id)
            .firstOrNull;
        if (!editable || entry == null) return;
        setState(() {
          value[key] = id;
          value[nameKey] = entry['name'];
          if (customer) {
            // 换客人不能静默保留上一位客人的限定标签。
            value['foodTag'] = null;
          } else {
            value['recipeId'] = entry['recipeId'];
          }
          validation = null;
        });
      },
    );
  }

  Future<void> _save() async {
    if (!current) {
      setState(() => validation = '连接或共享配置已经变化，请关闭后重新编辑。');
      return;
    }
    if (!model.canEdit || saving) return;
    if (value['customerId'] is! int ||
        value['foodId'] is! int ||
        value['recipeId'] is! int) {
      setState(() => validation = '请选择稀客和料理。');
      return;
    }
    setState(() {
      saving = true;
      validation = null;
    });
    try {
      await model.mutate(
        '/custom-recipes/upsert',
        query: {
          'mode': widget.mode,
          'expectedModeRevision': '$modeRevision',
          for (final key in [
            'id',
            'customerId',
            'customerName',
            'foodTag',
            'foodId',
            'recipeId',
            'recipeName',
          ])
            key: label(value[key], ''),
          'extraIngredientIds': extras.join(','),
          'enabled': '${value['enabled'] != false}',
          'pinToTop': '${value['pinToTop'] != false}',
        },
      );
      if (!mounted) return;
      if (!current) {
        // 写入等待期间的重连或切模可能使模型丢弃旧响应；不能把没有新错误
        // 误认为保存成功并关闭草稿，保留内容供用户核对后重新打开编辑。
        setState(() => validation = '连接或共享配置已经变化，请关闭后重新编辑。');
        return;
      }
      if (model.error.isEmpty)
        Navigator.pop(context);
      else
        setState(() => validation = model.error);
    } catch (error) {
      if (mounted) setState(() => validation = '$error');
    } finally {
      if (mounted) setState(() => saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: model,
    builder: (context, _) {
      final customer = _uniqueCatalog('rareCustomers')
          .where((row) => row['id'] == value['customerId'])
          .firstOrNull;
      final food = _uniqueCatalog('recipes')
          .where((row) => row['id'] == value['foodId'])
          .firstOrNull;
      final preview = recommendationItemView(
        model,
        value,
        section: RecommendationSection.custom,
        food: true,
      );
      final tag = label(value['foodTag'], '');
      final tags = <String, String>{
        '': '全部点单标签',
        if (tag.isNotEmpty) tag: tag,
        for (final item in values(
          customer?['positiveTags'],
        ).whereType<String>())
          if (item.isNotEmpty && item != '流行喜爱' && item != '流行厌恶') item: item,
      };
      return Padding(
        padding: const EdgeInsets.all(16),
        // 同一条滚动轴容纳表单和操作栏；SliverFillRemaining 在正常高度把按钮
        // 放到底部，键盘或横屏压缩高度时则自然滚动，不切换输入子树或裁切错误文字。
        child: CustomScrollView(
          key: const ValueKey('custom-recipe-editor-scroll'),
          slivers: [
            SliverToBoxAdapter(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(
                    widget.existing.isEmpty ? '新增自定义配方' : '编辑自定义配方',
                    style: StewardText.itemTitle,
                  ),
                  const SizedBox(height: 12),
                  // 两列选择在手机弹窗内等分，不用文字按钮打开第二层目录对话框。
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(child: _entity(widget.mode != 'food')),
                      const SizedBox(width: 12),
                      Expanded(child: _entity(widget.mode == 'food')),
                    ],
                  ),
                  const SizedBox(height: 12),
                  StewardSelect<String>(
                    key: const ValueKey('custom-editor-food-tag'),
                    label: '限定料理标签',
                    value: tag,
                    items: tags,
                    searchable: true,
                    hint: '全部点单标签',
                    enabled: editable && customer != null,
                    boundary: selectionBoundary,
                    onChanged: (tag) {
                      if (editable)
                        setState(
                          () => value['foodTag'] = tag.isEmpty ? null : tag,
                        );
                    },
                  ),
                  const SizedBox(height: 6),
                  const Text(
                    '标签来自所选稀客的料理需求；选择全部时适用于该稀客的所有点单。',
                    style: StewardText.caption,
                  ),
                  if (food != null) ...[
                    const SizedBox(height: 12),
                    // 编辑器只补充不能直接编辑的基础配方与厨具；价格、启停、排序
                    // 已在绑定列表显示，不在此重复。追加食材由下面的可移除名单负责。
                    MenuInfoGroup('厨具', [
                      OrderTag(
                        preview.cooker ?? '未知',
                        key: const ValueKey('custom-editor-cooker'),
                        kind: OrderTagKind.cooker,
                        // 类型身份已经由foodId:recipeId双键校验；未知映射只回退图标，
                        // 不用中文厨具名称反查升级厨具或另一个配方的图片。
                        leading: EntityImage(
                          kind: EntityImageKind.cooker,
                          id: preview.cookerId,
                          fallback: FLucideIcons.cookingPot,
                        ),
                      ),
                    ]),
                    MenuInfoGroup('配方', [
                      if (preview.ingredients == null)
                        const OrderTag('未知', kind: OrderTagKind.warning)
                      else
                        for (final ingredient in preview.ingredients!)
                          OrderTag(
                            ingredient.name,
                            kind: OrderTagKind.ingredient,
                            leading: EntityImage(
                              kind: EntityImageKind.ingredient,
                              id: ingredient.entityId,
                              fallback: FLucideIcons.leaf,
                            ),
                          ),
                    ], key: const ValueKey('custom-editor-base-ingredients')),
                  ],
                  const SizedBox(height: 12),
                  Text('追加食材', style: StewardText.sectionTitle),
                  const SizedBox(height: 8),
                  StewardSelect<int>(
                    key: const ValueKey('custom-editor-extra'),
                    label: '追加食材',
                    showLabel: false,
                    hint: '搜索并添加食材',
                    value: null,
                    searchable: true,
                    // 候选键即唯一目录的食材ID；名称只用于搜索和阅读，不参与图片联结。
                    itemLeading: (id) => EntityImage(
                      kind: EntityImageKind.ingredient,
                      id: entityImageId(id),
                      fallback: FLucideIcons.leaf,
                    ),
                    enabled: editable,
                    boundary: selectionBoundary,
                    items: {
                      if (model.catalog['isComplete'] != false)
                        for (final row in _uniqueCatalog('ingredients'))
                          if (row['id'] is int && !extras.contains(row['id']))
                            row['id'] as int: label(row['name']),
                    },
                    onChanged: (id) {
                      if (editable && !extras.contains(id))
                        setState(() => extras.add(id));
                    },
                  ),
                  if (extras.isEmpty)
                    const Padding(
                      padding: EdgeInsets.only(top: 8),
                      child: Text('没有追加食材。', style: StewardText.caption),
                    ),
                  for (final id in extras)
                    Padding(
                      padding: const EdgeInsets.only(top: 6),
                      // 名称允许多行，但移除操作仍对齐名称首行；不以整段文字
                      // 的高度居中按钮，避免长名称让操作在相邻条目间上下漂移。
                      child: StewardBaselineRow(
                        children: [
                          Expanded(
                            child: EntityName(
                              model.itemName('ingredients', id),
                              key: ValueKey(('custom-extra-name', id)),
                              kind: EntityImageKind.ingredient,
                              id: entityImageId(id),
                              fallback: FLucideIcons.leaf,
                              style: StewardText.body,
                            ),
                          ),
                          StewardFirstLineCenter(
                            textStyle: StewardText.body,
                            referenceText: model.itemName('ingredients', id),
                            child: StewardButton(
                              '移除',
                              key: ValueKey(('custom-extra-remove', id)),
                              onPressed: editable
                                  ? () => setState(() => extras.remove(id))
                                  : null,
                            ),
                          ),
                        ],
                      ),
                    ),
                ],
              ),
            ),
            SliverFillRemaining(
              hasScrollBody: false,
              child: Column(
                mainAxisAlignment: MainAxisAlignment.end,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (validation != null || !current)
                    Padding(
                      padding: const EdgeInsets.only(top: 8),
                      child: Text(
                        validation ?? '连接、共享配置或生效模式已变化，请重新打开编辑。',
                        style: StewardText.caption.copyWith(
                          color: context.theme.colors.error,
                        ),
                      ),
                    ),
                  const SizedBox(height: 8),
                  // 操作区与表单共用滚动轴；保存状态文字变长或窄屏放大字号时
                  // 自然换行并继续右对齐，不缩小按钮，也不改变编辑器状态树。
                  Wrap(
                    alignment: WrapAlignment.end,
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                      StewardButton(
                        '取消',
                        onPressed: saving ? null : () => Navigator.pop(context),
                      ),
                      StewardButton(
                        saving ? '正在保存…' : '保存配方',
                        primary: true,
                        onPressed: model.canEdit && !saving ? _save : null,
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      );
    },
  );
}
