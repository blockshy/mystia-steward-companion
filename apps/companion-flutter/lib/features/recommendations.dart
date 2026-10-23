import 'dart:async';
import 'dart:convert';

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
import 'recommendation_item_content.dart';
import 'custom_recipes.dart';
import 'recommendation_preferences.dart';
import 'recommendation_browser_support.dart';
import 'favorites_selection.dart';

/// 推荐页向 C# 提交最小选择条件；排序、预算和加料组合始终使用服务器计算结果。
class RecommendationsPage extends StatefulWidget {
  const RecommendationsPage(this.model, {this.active = true, super.key});
  final WorkbenchModel model;
  final bool active;
  @override
  State<RecommendationsPage> createState() => _RecommendationsPageState();
}

class _RecommendationsPageState extends State<RecommendationsPage>
    with ActiveResourcePage<RecommendationsPage> {
  String tab = 'normal';
  String place = '妖怪兽道';
  bool _followPlace = true;
  bool _favoriteBusy = false;
  int? customerId;
  // 点单标签只能来自当前稀客目录。状态归页面所有，切到收藏后返回仍显示同一组选项。
  // 名称筛选仍使用控制器，避免输入框卸载重建后显示为空、实际却按旧文字过滤。
  String foodTag = '', beverageTag = '';
  final searchInput = TextEditingController();
  final controlsScroll = ScrollController();
  String get search => searchInput.text;
  String category = 'recipes';
  String? submittedQuery;
  String get queryKey => tab == 'normal'
      ? 'normal/$place'
      : 'rare/$customerId/$foodTag/$beverageTag';
  String? _scheduledQuery;
  Timer? _queryDebounce;
  int _queryGeneration = 0;
  bool _querying = false;
  String _queryError = '';
  WorkbenchModel get model => widget.model;
  RecommendationSection get _section =>
      RecommendationSection.values.byName(tab);
  bool get _food => tab == 'custom' || category == 'recipes';
  // 普客与稀客的客人已由查询条件确定，结果框只筛选料理或酒水名称。
  // 收藏与自定义配方同时包含多个客人的记录，保留额外的客人名称筛选。
  bool get _filtersCustomer => tab == 'favorites' || tab == 'custom';
  String get _searchHint => tab == 'favorites'
      ? '筛选名称 / 标签 / 食材 / ID'
      : _filtersCustomer
      ? '筛选名称 / 客人'
      : _food
      ? '筛选料理名称'
      : '筛选酒水名称';
  MenuItemView _itemView(DataMap item) =>
      recommendationItemView(model, item, section: _section, food: _food);

  bool get _rareCatalogReady =>
      model.catalog.containsKey('rareCustomers') &&
      model.catalog['isComplete'] != false;
  List<DataMap> get _rareCustomers => _rareCatalogReady
      ? model
            .catalogItems('rareCustomers')
            .where((entry) => entry['id'] is int && (entry['id'] as int) >= 0)
            .toList()
      : const [];
  List<String> get _normalPlaces => model.catalog['isComplete'] == false
      ? const []
      : (model
            .catalogItems('normalCustomers')
            .expand((entry) => values(entry['places']).whereType<String>())
            .where((name) => name.isNotEmpty)
            .toSet()
            .toList()
          ..sort());
  String? get _detectedPlace {
    final value = model.night['place'];
    return value is String && _normalPlaces.contains(value) ? value : null;
  }

  /// 现场明确提供可用地点时才跟随，不根据场景名、地点标签或目录顺序猜测现场。
  /// 非经营场景仍可手动浏览目录；手动选定后保留固定值，直到用户恢复跟随。
  void _reconcilePlace() {
    final places = _normalPlaces;
    if (_followPlace && _detectedPlace != null) place = _detectedPlace!;
    if (places.isNotEmpty && !places.contains(place)) place = places.first;
  }

  DataMap? get _selectedCustomer {
    for (final customer in _rareCustomers) {
      if (customer['id'] == customerId) return customer;
    }
    return null;
  }

  /// 原始运行时目录也包含两种流行度状态，它们不能作为客人点单标签。
  /// 此处只限定表单选项；推荐评分、匹配、加料和排序仍完全由 C# 计算。
  List<String> _customerTags(DataMap? customer, {required bool food}) =>
      values(customer?[food ? 'positiveTags' : 'beverageTags'])
          .whereType<String>()
          .map((tag) => tag.trim())
          .where(
            (tag) =>
                tag.isNotEmpty && (!food || (tag != '流行喜爱' && tag != '流行厌恶')),
          )
          .toSet()
          .toList(growable: false);
  List<String> get _foodTags => _customerTags(_selectedCustomer, food: true);
  List<String> get _beverageTags =>
      _customerTags(_selectedCustomer, food: false);
  bool get _canQueryRare =>
      _selectedCustomer != null &&
      _foodTags.contains(foodTag) &&
      _beverageTags.contains(beverageTag);

  /// 与旧界面一致：换客人后保留仍合法的选择，否则改用新客人列表的第一项。
  /// 缺失列表明确变为空，不能沿用其他客人的条件，也不能从全局标签表补造选项。
  bool _reconcileRareSelection() {
    final previous = (customerId, foodTag, beverageTag);
    if (_selectedCustomer == null) {
      // 初次进入从已完整加载的客人目录选择首个可查询客人，界面始终显示其姓名。
      // 这是表单默认选择，不是推荐计算；空目录或不合法标签仍不能发送请求。
      final ready = _rareCustomers
          .where(
            (customer) =>
                _customerTags(customer, food: true).isNotEmpty &&
                _customerTags(customer, food: false).isNotEmpty,
          )
          .firstOrNull;
      customerId = (ready ?? _rareCustomers.firstOrNull)?['id'] as int?;
    }
    final foods = _foodTags, beverages = _beverageTags;
    if (!foods.contains(foodTag)) foodTag = foods.firstOrNull ?? '';
    if (!beverages.contains(beverageTag))
      beverageTag = beverages.firstOrNull ?? '';
    return previous != (customerId, foodTag, beverageTag);
  }

  void _changeRareSelection(VoidCallback update) {
    setState(() {
      update();
      _reconcileRareSelection();
    });
    // 旧结果立即隐藏，下一帧按新条件启动防抖查询；切换过程中不可收藏旧结果。
  }

  /// 四个下拉共用完整的条件边界。另一个条件、连接或页面变化后，旧下拉立即
  /// 失效；选择回调还会读取当前目录，不能把旧标签提交到新的客人查询中。
  Object get _selectionBoundary => (
    model.connectionVersion,
    model.connected,
    widget.active,
    tab,
    place,
    _followPlace,
    customerId,
    foodTag,
    beverageTag,
  );

  bool _canAcceptSelection(Object boundary) =>
      mounted &&
      widget.active &&
      model.connected &&
      boundary == _selectionBoundary;

  Widget _placeSelector() {
    final boundary = _selectionBoundary;
    return SizedBox(
      width: stewardSelectWidth,
      child: StewardSelect<String>(
        key: const ValueKey('normal-place-selector'),
        label: '经营地点',
        hint: '选择经营地点',
        formatSelected: (name) => '地点：$name',
        showLabel: false,
        searchable: true,
        items: {for (final name in _normalPlaces) name: name},
        value: place,
        enabled: model.connected && widget.active,
        boundary: boundary,
        onChanged: (value) {
          if (_canAcceptSelection(boundary) &&
              tab == 'normal' &&
              _normalPlaces.contains(value))
            setState(() {
              place = value;
              _followPlace = false;
            });
        },
      ),
    );
  }

  Widget _customerSelector() {
    final boundary = _selectionBoundary;
    return SizedBox(
      width: stewardSelectWidth,
      child: StewardSelect<int>(
        key: const ValueKey('rare-customer-selector'),
        itemLeading: (id) => EntityImage(
          kind: EntityImageKind.rareGuest,
          id: id,
          fallback: FLucideIcons.userRound,
        ),
        label: '稀客',
        hint: '选择稀客',
        showLabel: false,
        searchable: true,
        items: {
          for (final customer in _rareCustomers)
            customer['id'] as int: label(
              customer['name'],
              '#${customer['id']}',
            ),
        },
        value: customerId,
        enabled: model.connected && widget.active,
        boundary: boundary,
        onChanged: (value) {
          if (_canAcceptSelection(boundary) &&
              tab == 'rare' &&
              _rareCustomers.any((entry) => entry['id'] == value))
            _changeRareSelection(() => customerId = value);
        },
      ),
    );
  }

  Widget _tagSelector({required bool food}) {
    final boundary = _selectionBoundary;
    return SizedBox(
      width: stewardSelectWidth,
      child: StewardSelect<String>(
        key: ValueKey(
          food ? 'rare-food-tag-selector' : 'rare-beverage-tag-selector',
        ),
        label: food ? '料理标签' : '酒水标签',
        formatSelected: (name) => '${food ? '料理' : '酒水'}：$name',
        showLabel: false,
        searchable: true,
        items: {for (final tag in food ? _foodTags : _beverageTags) tag: tag},
        value: food ? foodTag : beverageTag,
        enabled: model.connected && widget.active,
        boundary: boundary,
        onChanged: (value) {
          if (!_canAcceptSelection(boundary) ||
              tab != 'rare' ||
              !(food ? _foodTags : _beverageTags).contains(value))
            return;
          _changeRareSelection(() {
            if (food)
              foodTag = value;
            else
              beverageTag = value;
          });
        },
      ),
    );
  }

  @override
  void dispose() {
    _queryDebounce?.cancel();
    _queryGeneration++;
    controlsScroll.dispose();
    searchInput.dispose();
    super.dispose();
  }

  @override
  bool get resourcePageActive => widget.active;
  @override
  WorkbenchModel get resourceModel => model;
  @override
  void loadActiveResource() {
    if (!widget.active) return;
    // 稀客推荐需要同一权威收藏资源显示选中态并原地取消；不另建全局轮询。
    if (tab == 'favorites' || tab == 'rare') model.readResource('/favorites');
    if (tab == 'custom') model.readResource('/custom-recipes');
  }

  void _select(String next) {
    // 每个子页的查询条件不同；返回顶部后首个选择器保持可见，输入草稿仍由控制器保留。
    if (controlsScroll.hasClients) controlsScroll.jumpTo(0);
    setState(() => tab = next);
    model.clearResourceSubscription();
    loadActiveResource();
  }

  String? get _desiredQuery =>
      widget.active &&
          model.connected &&
          (tab == 'normal' && _normalPlaces.contains(place) ||
              tab == 'rare' && _canQueryRare)
      ? '${model.connectionVersion}/$queryKey'
      : null;

  /// 每个可见条件/连接边界只安排一次查询；本地搜索、目录外的通知和结果轮询不重新提交。
  /// 取消与模型通知推迟到帧后，避免在 build 内通知共享模型造成重入。
  void _synchronizeQuery() {
    final desired = _desiredQuery;
    if (_scheduledQuery == desired) return;
    _scheduledQuery = desired;
    final generation = ++_queryGeneration;
    _queryDebounce?.cancel();
    submittedQuery = null;
    _queryError = '';
    _querying = desired != null;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || generation != _queryGeneration) return;
      model.clearRecommendationQuery();
      if (desired == null) return;
      _queryDebounce = Timer(const Duration(milliseconds: 220), () {
        if (mounted &&
            generation == _queryGeneration &&
            desired == _desiredQuery)
          unawaited(_runQuery(generation, desired));
      });
    });
  }

  Future<void> _runQuery(int generation, String desired) async {
    // 帧后清理已移除上次请求错误；进入实际请求时重新投影页头与正文，不能
    // 依赖业务模型恰好另发通知，否则手工重试可能暂留上一轮错误提示。
    setState(() {});
    try {
      await model.queryRecommendations(
        kind: tab,
        selectedPlace: place,
        customerId: customerId,
        foodTag: foodTag,
        beverageTag: beverageTag,
      );
    } catch (cause) {
      if (mounted && generation == _queryGeneration && desired == _desiredQuery)
        _queryError = '$cause';
    } finally {
      // 迟到的 Future 只允许完成，不能把旧条件标记为当前或关闭后来的加载状态。
      if (mounted && generation == _queryGeneration && desired == _desiredQuery)
        setState(() {
          _querying = false;
          submittedQuery = queryKey;
        });
    }
  }

  void _retryQuery() => setState(() => _scheduledQuery = null);

  /// 查询状态与完整诊断统一发布到页头；隐藏页和收藏/自定义页不发布旧查询。
  /// 重试保留在既有结果工具栏，沿用当前请求门闩，不让提示迁移改变重试权限。
  String get _currentQueryError =>
      _queryError.isNotEmpty ? _queryError : model.recommendationError;

  PageActivity? get _queryActivity {
    if (!widget.active || (tab != 'normal' && tab != 'rare')) return null;
    if (!model.connected) return const PageActivity(message: '连接断开，恢复后自动更新推荐。');
    if (_desiredQuery == null) {
      if (tab == 'normal')
        return PageActivity(
          message:
              model.catalog['isComplete'] == false ||
                  !model.catalog.containsKey('normalCustomers')
              ? '正在等待完整的地点资料。'
              : '当前目录没有可用地点。',
        );
      return PageActivity(
        message: '等待有效的稀客需求',
        details: !_rareCatalogReady
            ? '正在等待完整的稀客资料，请稍候或刷新。'
            : _rareCustomers.isEmpty
            ? '当前目录没有可选稀客。'
            : customerId == null
            ? '先选择稀客，再选择该客人的料理与酒水需求。'
            : '该稀客的${_foodTags.isEmpty ? '料理' : '酒水'}标签列表为空，暂不能查询推荐。',
      );
    }
    if (_currentQueryError.isNotEmpty)
      return PageActivity(
        message: '推荐更新失败',
        details: '推荐加载失败：$_currentQueryError',
        error: true,
      );
    return _querying || model.recommendationLoading
        ? const PageActivity(message: '正在更新推荐…', busy: true)
        : null;
  }

  @override
  Widget build(BuildContext context) {
    _reconcileRareSelection();
    _reconcilePlace();
    _synchronizeQuery();
    synchronizeResourceActivation();
    final result = object(model.pageResult['result']).isNotEmpty
        ? object(model.pageResult['result'])
        : model.pageResult;
    final source = tab == 'favorites'
        ? model.resources['/favorites'] ?? const <String, Object?>{}
        : tab == 'custom'
        ? model.resources['/custom-recipes'] ?? const <String, Object?>{}
        : submittedQuery == queryKey
        ? result
        : const <String, Object?>{};
    final rows = records(source[tab == 'custom' ? 'recipes' : category])
        .where(
          (item) => tab == 'favorites'
              ? favoriteMatchesSearch(model, item, search, food: _food)
              : _name(item).contains(search) ||
                    (_filtersCustomer &&
                        label(item['customerName'], '').contains(search)),
        )
        .toList();
    return PageActivityReporter(
      activity: _queryActivity,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          WorkspaceTabs<String>(
            horizontalScroll: !model.desktop,
            items: const {
              'normal': '普客',
              'rare': '稀客',
              'favorites': '收藏',
              'custom': '自定义配方',
              'preferences': '推荐偏好',
            },
            value: tab,
            onChanged: _select,
          ),
          Expanded(
            child: tab == 'preferences'
                ? RecommendationPreferencesPanel(model, active: widget.active)
                : tab == 'custom'
                ? CustomRecipesPanel(model, active: widget.active)
                : LayoutBuilder(
                    builder: (context, constraints) => Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        // 宽屏时按内容自然高度排列。窄侧栏、大字体或键盘压缩可用高度时，
                        // 仅条件区滚动，并为结果保留至少35%的剩余高度；不靠隐藏按钮缩减内容。
                        ConstrainedBox(
                          constraints: BoxConstraints(
                            maxHeight: constraints.maxHeight * .65,
                          ),
                          child: SingleChildScrollView(
                            key: const ValueKey('recommendation-controls'),
                            controller: controlsScroll,
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.stretch,
                              children: [
                                if (tab == 'normal')
                                  PageFilterRow(
                                    children: [
                                      // 与稀客条件使用同一个标准行；手机上只收紧选择器，
                                      // 保留跟随和偏好操作的完整命中区，切换页签不改变首行基线。
                                      Flexible(child: _placeSelector()),
                                      StewardIconButton(
                                        key: const ValueKey(
                                          'normal-follow-place',
                                        ),
                                        label: _followPlace
                                            ? '固定当前地点（正在跟随）'
                                            : '恢复跟随经营地点',
                                        icon: _followPlace
                                            ? FLucideIcons.locateFixed
                                            : FLucideIcons.locate,
                                        onPressed:
                                            widget.active && model.connected
                                            ? () => setState(() {
                                                _followPlace = !_followPlace;
                                                _reconcilePlace();
                                              })
                                            : null,
                                      ),
                                      StewardButton(
                                        '客群偏好',
                                        key: const ValueKey(
                                          'normal-customer-preferences',
                                        ),
                                        onPressed:
                                            widget.active &&
                                                _normalPlaces.contains(place)
                                            ? () =>
                                                  showNormalCustomerPreferences(
                                                    context,
                                                    model,
                                                    place,
                                                  )
                                            : null,
                                      ),
                                    ],
                                  ),
                                if (tab == 'rare')
                                  PageFilterRow(
                                    // 同一行按正文宽度等分收紧；不靠缩小字体或触屏点击高度
                                    // 挤入手机。每个条件保留可搜索下拉及独立读屏名称。
                                    children: [
                                      Flexible(child: _customerSelector()),
                                      Flexible(child: _tagSelector(food: true)),
                                      Flexible(
                                        child: _tagSelector(food: false),
                                      ),
                                    ],
                                  ),
                                // 浏览类型与搜索属于结果工具栏；业务页签和查询动作不再混用同一组按钮。
                                PageToolbar(
                                  key: const ValueKey(
                                    'recommendation-results-toolbar',
                                  ),
                                  controller: searchInput,
                                  searchLabel: _searchHint,
                                  searchKey: const ValueKey(
                                    'recommendation-name-filter',
                                  ),
                                  onSearchChanged: (_) => setState(() {}),
                                  actions: [
                                    if (model.connected &&
                                        _desiredQuery != null &&
                                        _currentQueryError.isNotEmpty)
                                      StewardButton(
                                        '重试',
                                        onPressed: _querying
                                            ? null
                                            : _retryQuery,
                                      ),
                                  ],
                                  navigation: tab == 'custom'
                                      ? null
                                      : WorkspaceTabs<String>(
                                          horizontalScroll: true,
                                          items: const {
                                            'recipes': '料理',
                                            'beverages': '酒水',
                                          },
                                          inToolbar: true,
                                          value: category,
                                          onChanged: (next) =>
                                              setState(() => category = next),
                                        ),
                                ),
                              ],
                            ),
                          ),
                        ),
                        Expanded(
                          child: DenseListDetail(
                            key: ValueKey('recommendations-$tab-$category'),
                            richSummary: true,
                            items: rows,
                            identity: (item) =>
                                '${item['id'] ?? object(item['recipe'])['id'] ?? object(item['beverage'])['id']}:${values(item['extraIngredientIds']).join(',')}:${records(item['extraIngredients']).map((e) => e['id']).join(',')}',
                            title: _name,
                            // 外层名称由公共图文组件绘制；summary继续不重复名称，
                            // 缺少精确ID时仍保留同尺寸旧图标，不按名称猜图。
                            titleContent: (item) => Row(
                              children: [
                                Expanded(
                                  child: MenuItemName(
                                    _itemView(item),
                                    style: const TextStyle(
                                      fontWeight: FontWeight.w600,
                                      height: 1.35,
                                    ),
                                  ),
                                ),
                                if (tab == 'rare' &&
                                    _favoriteState(item).id != null)
                                  Padding(
                                    padding: const EdgeInsets.only(left: 6),
                                    child: Semantics(
                                      label: '已收藏',
                                      child: Icon(
                                        FLucideIcons.heart,
                                        size: MediaQuery.textScalerOf(context)
                                            .scale(15),
                                        color: context.theme.colors.primary,
                                      ),
                                    ),
                                  ),
                              ],
                            ),
                            onFavorite:
                                tab == 'rare' &&
                                    model.canEdit &&
                                    customerId != null
                                ? _favoriteCallback
                                : null,
                            // 标题已显示名称；统一内容只追加主体，避免料理名称出现两遍。
                            subtitle: (_) => '',
                            summary: (item) => MenuItemContent(
                              _itemView(item),
                              showName: false,
                            ),
                            detail: (item) => _details(context, item),
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

  String _name(DataMap item) => _itemView(item).name;
  ({bool known, String? id}) _favoriteState(DataMap item) =>
      recommendationFavorite(
        model.resources['/favorites'] ?? const {},
        item,
        food: category == 'recipes',
        customerId: customerId,
        tag: category == 'recipes' ? foodTag : beverageTag,
      );
  Object get _favoriteBoundary => (
    model,
    _selectionBoundary,
    category,
    model.authority['registryId'],
    model.authority['currentDeviceId'],
    model.authority['primaryDeviceId'],
    model.authority['authorityRevision'],
    // 轮询可能先更新模型、下一帧才重绘按钮。此时不能把用户看到的“加入收藏”
    // 偷换成“取消收藏”；只比较本类别内容，忽略另一类别及读取时间的变化。
    jsonEncode(model.resources['/favorites']?[category]),
  );

  /// 行快捷键和详情按钮共用渲染时的条件与设备身份，不能借后来条件执行旧操作。
  ValueChanged<DataMap> get _favoriteCallback {
    final boundary = _favoriteBoundary;
    return (item) => _favorite(item, boundary);
  }

  Future<void> _favorite(DataMap item, Object boundary) async {
    if (!mounted ||
        !widget.active ||
        !TickerMode.valuesOf(context).enabled ||
        tab != 'rare' ||
        !model.canEdit ||
        _favoriteBusy ||
        boundary != _favoriteBoundary)
      return;
    final saved = _favoriteState(item);
    if (!saved.known) return;
    final entity = object(item[category == 'recipes' ? 'recipe' : 'beverage']);
    setState(() => _favoriteBusy = true);
    try {
      await model.mutate(
        saved.id != null
            ? category == 'recipes'
                  ? '/favorites/remove-recipe'
                  : '/favorites/remove-beverage'
            : category == 'recipes'
            ? '/favorites/add-recipe'
            : '/favorites/add-beverage',
        query: saved.id != null
            ? {'id': saved.id!}
            : {
                'customerId': '$customerId',
                'customerName': model.itemName('rareCustomers', customerId),
                if (category == 'recipes') ...{
                  'foodTag': foodTag,
                  'recipeId': '${entity['id']}',
                  'extraIngredientIds': safeFavoriteExtraIds(
                    item['extraIngredients'],
                    entities: true,
                  )!.join(','),
                } else ...{
                  'beverageTag': beverageTag,
                  'beverageId': '${entity['id']}',
                },
              },
      );
    } finally {
      if (mounted) setState(() => _favoriteBusy = false);
    }
  }

  Widget _details(BuildContext context, DataMap item) {
    final view = _itemView(item);
    final favorite = _favoriteState(item);
    final toggleFavorite = _favoriteCallback;
    return Section(
      _name(item),
      [
        RecommendationItemExplanation(
          model,
          item,
          section: _section,
          food: _food,
        ),
        ToolRow([
          if (tab == 'rare')
            StewardButton(
              favorite.id != null ? '取消收藏' : '加入收藏',
              key: const ValueKey('recommendation-toggle-favorite'),
              primary: favorite.id != null,
              onPressed:
                  widget.active &&
                      model.canEdit &&
                      customerId != null &&
                      favorite.known &&
                      !_favoriteBusy
                  ? () => toggleFavorite(item)
                  : null,
            ),
          if (tab == 'favorites')
            StewardButton(
              '移除收藏',
              onPressed: model.canEdit
                  ? () => model.mutate(
                      category == 'recipes'
                          ? '/favorites/remove-recipe'
                          : '/favorites/remove-beverage',
                      query: {'id': label(item['id'], '')},
                    )
                  : null,
            ),
        ]),
      ],
      titleContent: MenuItemName(
        view,
        style: StewardText.sectionTitle.copyWith(
          color: context.theme.colors.primary,
        ),
      ),
    );
  }
}
