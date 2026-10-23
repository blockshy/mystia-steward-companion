import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../app/available_mission_resource.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/inventory_item.dart';
import '../ui/module_switch_row.dart';
import '../ui/page_activity.dart';
import '../ui/page_toolbar.dart';
import '../ui/steward_controls.dart';
import '../ui/steward_opacity.dart';
import '../ui/steward_style.dart';
import 'extension_modules.dart';
import 'automation_module.dart';
import 'game_ui_module.dart';
import 'extension_item_content.dart';

/// 扩展模块默认关闭；进入已启用模块后才读取数据，人工写入仍绑定原有场景与权限。
/// 保留 ToolsPage 类型名供现有工作区集成使用，页面名称由主导航统一定义。
class ToolsPage extends StatefulWidget {
  const ToolsPage(this.model, {this.active = true, super.key});
  final WorkbenchModel model;
  final bool active;
  @override
  State<ToolsPage> createState() => _ToolsPageState();
}

class _ToolsPageState extends State<ToolsPage>
    with ActiveResourcePage<ToolsPage> {
  String tab = 'missions';
  bool _changingModule = false, _operationPending = false;
  bool available = false;
  String scope = 'current';
  String inventoryType = 'ingredient';
  InventorySortColumn inventorySort = InventorySortColumn.name;
  bool inventoryAscending = true;
  // 切换模块会卸载筛选工具栏；控制器归工作区持有，返回时文字与实际过滤一致。
  final searchInput = TextEditingController();
  String get search => searchInput.text;
  final quantity = TextEditingController(text: '99');
  final selectedIds = <int>{};
  Object? _inventorySelectionOwner;
  (String, int)? _inventoryAdjustment;
  final levels = <int>{0, 1, 2, 3, 4, 5};
  final controlsScroll = ScrollController();
  // 羁绊筛选横向滚动独立于模块控制区的纵向滚动；轮询和窗口缩放不重建控制器，
  // 也不把批量邀请按钮放进滚动区，从而始终保留明确可见的操作入口。
  final invitationLevelsScroll = ScrollController();
  bool _lastLocalEnabled = false;
  String? _lastLocalTab;
  WorkbenchModel get model => widget.model;
  bool _enabled(String id) => extensionModules
      .where((module) => module.id == id)
      .any((module) => module.enabled(model.localPreferences));
  bool _canUse(String id) =>
      mounted && widget.active && tab == id && _enabled(id);
  bool get _moduleSwitchBusy =>
      model.busy || _changingModule || _operationPending;

  /// 停用模块保留所在 Tab，以便直接重新启用；清除人工选择和附加订阅，
  /// 避免停用前的选择或回调继续产生操作。共享模块没有本机偏好开关。
  void _reconcileModule() {
    if (!extensionModules.any((module) => module.id == tab) || _enabled(tab))
      return;
    selectedIds.clear();
    final disabledTab = tab;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && widget.active && tab == disabledTab && !_enabled(tab))
        model.clearResourceSubscription();
    });
  }

  void _selectModule(String next) {
    if (next == tab) return;
    if (controlsScroll.hasClients) controlsScroll.jumpTo(0);
    model.clearResourceSubscription();
    setState(() {
      tab = next;
      selectedIds.clear();
    });
    _load();
  }

  Future<void> _setModule(ExtensionModule module, bool enabled) async {
    if (!widget.active || _moduleSwitchBusy) return;
    setState(() => _changingModule = true);
    if (!enabled && tab == module.id) model.clearResourceSubscription();
    try {
      // 本机偏好不要求主设备；是否成功以模型的持久化结果为准，不乐观改写共享数据。
      await model.updateLocal({module.preferenceKey: enabled});
      if (!mounted) return;
      // 记录本页操作已处理的变化，避免模型通知延迟到下一帧时被识别为外部启用。
      _lastLocalTab = tab;
      _lastLocalEnabled = _enabled(tab);
      setState(_reconcileModule);
    } finally {
      if (mounted) {
        setState(() => _changingModule = false);
        if (_canUse(module.id)) _load();
      }
    }
  }

  /// 从确认对话框开始直到写入完成锁住本机模块开关；异步返回后仍再次核对模块，
  /// 防止外部偏好变化、导航切换或停用后沿用旧闭包执行游戏写入。
  Future<void> _runModuleOperation(
    String id,
    Future<void> Function() action,
  ) async {
    if (!_canUse(id) || !model.canEdit || _operationPending || _changingModule)
      return;
    setState(() => _operationPending = true);
    try {
      await action();
    } finally {
      if (mounted) setState(() => _operationPending = false);
    }
  }

  @override
  bool get resourcePageActive => widget.active;
  @override
  WorkbenchModel get resourceModel => model;
  @override
  void loadActiveResource() => _load();
  @override
  void didUpdateWidget(covariant ToolsPage oldWidget) {
    super.didUpdateWidget(oldWidget);
    // 独立嵌入工作区时也遵循隐藏即撤销读取；不依赖顶层路由替本模块取消订阅。
    if (oldWidget.active && !widget.active) model.clearResourceSubscription();
  }

  @override
  void dispose() {
    controlsScroll.dispose();
    invitationLevelsScroll.dispose();
    searchInput.dispose();
    quantity.dispose();
    super.dispose();
  }

  void _load() {
    if (!widget.active || _changingModule || !_enabled(tab) || !model.connected)
      return;
    model.clearResourceSubscription();
    if (tab == 'missions')
      model.readResource(
        available ? '/missions/available' : '/missions/tracked',
      );
    if (tab == 'invitations')
      model.readResource('/rare-guests/invitations', query: {'scope': scope});
  }

  @override
  Widget build(BuildContext context) {
    _reconcileModule();
    synchronizeResourceActivation();
    final localModule = extensionModules
        .where((module) => module.id == tab)
        .firstOrNull;
    final enabled = localModule != null && _enabled(tab);
    // 历史导入可能晚于切页完成。只在当前模块由外部从关变开时补读一次；
    // 本页开关自己的 finally 已负责加载，不能在同一变更上重复发起订阅。
    if (_lastLocalTab == tab &&
        !_lastLocalEnabled &&
        enabled &&
        !_changingModule) {
      final activatedTab = tab;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted && _canUse(activatedTab)) _load();
      });
    }
    _lastLocalTab = tab;
    _lastLocalEnabled = enabled;
    return PageActivityReporter(
      // 发布器固定在扩展页根部；切换模块只替换值，隐藏页和停用模块主动清空。
      // 页头承载状态及完整原因；正文不再为同一次读取重复插入提示行。
      activity: _readState?.activity,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          WorkspaceTabs<String>(
            horizontalScroll: !model.desktop,
            items: {
              for (final module in extensionModules) module.id: module.tabLabel,
              'automation': '自动化',
              'game-ui': '游戏界面辅助',
            },
            value: tab,
            onChanged: _selectModule,
          ),
          Expanded(
            child: localModule != null && !_enabled(tab)
                ? _localPage(
                    const [],
                    EmptyState('${localModule.title}模块已停用', ''),
                  )
                : switch (tab) {
                    'missions' => _missions(),
                    'invitations' => _invitations(),
                    'inventory' => _inventory(),
                    'automation' => AutomationModule(model),
                    _ => GameUiModule(model),
                  },
          ),
        ],
      ),
    );
  }

  /// 顶层状态只翻译真实读取事实，不把未知状态视为就绪，也不直接显示协议码。
  /// 正常任务 ready 和邀请的成功人数摘要没有额外操作价值，故不占用正文或页头。
  /// 网络异常仍由模型原有反馈区呈现；这里不把其它页面的全局错误冒充本次读取错误。
  _ExtensionReadState? get _readState {
    // _enabled 只查询 extensionModules 的三个本机模块；自动化/游戏界面辅助
    // 不在该目录中，必须在读取任何附加资源前返回 null，不能继承邀请的旧状态。
    if (!widget.active || !_enabled(tab)) return null;
    if (!model.connected) {
      return const _ExtensionReadState(
        '未连接',
        detail: '连接游戏电脑后可刷新；离线期间不能修改游戏数据。',
      );
    }
    if (tab == 'inventory') return null;
    final response =
        model.resources[tab == 'missions'
            ? available
                  ? '/missions/available'
                  : '/missions/tracked'
            : '/rare-guests/invitations'];
    final error = label(response?['error'], '').trim();
    final availableState = tab == 'missions' && available && response != null
        ? AvailableMissionReadState.tryRead(response)
        : null;
    if (error.isNotEmpty && availableState == null) {
      return _ExtensionReadState('读取失败', detail: error, error: true);
    }
    if (tab == 'invitations' &&
        model.snapshot['runtimeDaySceneReady'] != true) {
      return const _ExtensionReadState(
        '等待日间场景',
        detail: '邀请需要已稳定的日间场景；场景就绪后可刷新候选。',
      );
    }
    if (response == null || response.isEmpty) {
      return _ExtensionReadState(tab == 'missions' ? '等待读取任务' : '等待读取邀请');
    }
    if (tab == 'invitations') {
      if (response['ok'] == true && response['runtimeAvailable'] == true) {
        return null;
      }
      return const _ExtensionReadState(
        '邀请状态未确认',
        detail: '尚未取得可用的邀请运行时信息，请刷新后再试。',
        error: true,
      );
    }
    final state = switch (response['status']) {
      'ready' when response['runtimeAvailable'] == true => null,
      'not-attached' => const _ExtensionReadState(
        '任务读取未接入',
        detail: 'Mod 尚未接入任务读取，任务数据当前不可用。',
        error: true,
      ),
      'waiting-for-load' => const _ExtensionReadState(
        '等待读取存档',
        detail: '请在游戏中读取存档，完成后可刷新任务。',
      ),
      'loading' => const _ExtensionReadState('正在读取任务', busy: true),
      'runtime-unavailable' => const _ExtensionReadState(
        '任务运行时不可用',
        detail: '任务运行时尚不可读取，请等待场景稳定后刷新。',
        error: true,
      ),
      'mission-data-incomplete' => const _ExtensionReadState(
        '任务数据不完整',
        detail: '尚未取得完整任务数据，请刷新后再查看。',
        error: true,
      ),
      _ => const _ExtensionReadState(
        '任务状态未确认',
        detail: '尚未取得可识别的任务读取状态，请刷新后再查看。',
        error: true,
      ),
    };
    // 已识别的等待快照只显示中文状态；真正读取故障依然保留服务端诊断原文。
    // 先按 status 建立层级，避免非空 error 把“加载存档”等正常状态全部改成失败。
    if (state != null &&
        availableState != null &&
        availableState.diagnostic.isNotEmpty &&
        !availableState.hasRoutineWaitDiagnostic) {
      return _ExtensionReadState(
        state.message,
        detail:
            '${state.detail ?? '请刷新后再查看任务。'}\n诊断：${availableState.diagnostic}',
        busy: state.busy,
        error: true,
      );
    }
    return state;
  }

  /// 五模块使用相同的开关起点和正文宽度。本机模块把开关、搜索和筛选纳入
  /// 有界滚动区；手机至少保留一半高度浏览结果，键盘与大字号不能挤掉列表。
  /// 同一个布局树适用于所有宽度，改变约束时不更换输入框父级或控制器。
  Widget _localPage(List<Widget> controls, Widget content) {
    final localModule = extensionModules.singleWhere(
      (module) => module.id == tab,
    );
    final status = _moduleSwitchBusy
        ? '本机·处理中'
        : !model.connected
        ? '本机·离线'
        : tab == 'missions'
        ? '本机·只读'
        : model.primary
        ? '本机·可写'
        : '本机·查看';
    final toggle = ModuleSwitchRow(
      key: ValueKey('module-switch-${localModule.id}'),
      moduleName: localModule.title,
      value: localModule.enabled(model.localPreferences),
      onChanged: _moduleSwitchBusy
          ? null
          : (value) => _setModule(localModule, value),
      status: status,
      explanation:
          '${localModule.description} 开关仅在本机保存，下次启动沿用，不改变共享配置。'
          '${tab == 'missions' ? '任务只读。' : '游戏写入仍需已连接的主设备及对应场景权限。'}'
          '${tab == 'inventory' ? '勾选有限库存条目后设置数量；无限库存只读，未知库存不会按零处理。' : ''}',
    );
    return LayoutBuilder(
      builder: (context, constraints) => Column(
        key: model.desktop ? null : const ValueKey('mobile-extension-body'),
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          ConstrainedBox(
            key: model.desktop
                ? null
                : const ValueKey('mobile-extension-control-region'),
            constraints: BoxConstraints(
              maxHeight: model.desktop
                  ? constraints.maxHeight * .65
                  : (constraints.maxHeight * .5).clamp(0.0, 280.0).toDouble(),
            ),
            child: RawScrollbar(
              controller: controlsScroll,
              thumbVisibility: !model.desktop,
              thickness: 3,
              radius: const Radius.circular(3),
              thumbColor: context.stewardPaint.foreground(
                context.theme.colors.primary,
                alpha: .5,
              ),
              child: SingleChildScrollView(
                key: const ValueKey('extension-controls'),
                controller: controlsScroll,
                padding: const EdgeInsets.only(right: 8, bottom: 8),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [toggle, ...controls],
                ),
              ),
            ),
          ),
          const SizedBox(height: 8),
          Expanded(child: content),
        ],
      ),
    );
  }

  /// 每个本机模块的类别页签与名称筛选同排；页签自行横滚，输入框留在滚动区外。
  /// 刷新复用公共图标动作的尺寸、语义与提示，把窄屏空间留给页签文字及输入草稿。
  Widget _toolbar<T>({
    required Map<T, String> items,
    required T value,
    required ValueChanged<T> onChanged,
  }) => PageToolbar(
    key: const ValueKey('extension-page-toolbar'),
    controller: searchInput,
    searchLabel: '筛选名称',
    onSearchChanged: (_) => setState(() {}),
    navigation: WorkspaceTabs<T>(
      horizontalScroll: true,
      inToolbar: true,
      items: items,
      value: value,
      onChanged: onChanged,
    ),
    actions: [
      StewardIconButton(
        key: const ValueKey('extension-refresh'),
        label: '刷新',
        icon: FLucideIcons.refreshCw,
        onPressed: model.connected && !_moduleSwitchBusy
            ? () {
                if (!_canUse(tab)) return;
                if (tab == 'inventory') {
                  model.refresh();
                } else {
                  _load();
                }
              }
            : null,
      ),
    ],
  );

  Widget _missions() {
    final response =
        model.resources[available
            ? '/missions/available'
            : '/missions/tracked'] ??
        const <String, Object?>{};
    final items = records(response['missions'])
        .where((item) => label(item['title']).contains(search))
        .toList();
    final state = _readState;
    return _localPage(
      [
        _toolbar<bool>(
          items: const {false: '追踪任务', true: '可接取任务'},
          value: available,
          onChanged: (next) {
            setState(() => available = next);
            _load();
          },
        ),
      ],
      items.isEmpty
          ? EmptyState(state?.message ?? '暂无任务', '')
          : ListView.builder(
              key: const ValueKey('mission-items'),
              itemCount: items.length,
              itemBuilder: (context, index) => MissionItemContent(
                items[index],
                available: available,
                key: ValueKey('mission-item-${items[index]['label']}-$index'),
              ),
            ),
    );
  }

  Widget _invitations() {
    final response =
        model.resources['/rare-guests/invitations'] ??
        const <String, Object?>{};
    final candidates = records(response['candidates'])
        .where(
          (item) =>
              label(item['name']).contains(search) &&
              (item['kizunaLevel'] == null ||
                  item['kizunaLevel'] == -1 ||
                  levels.contains(item['kizunaLevel'])),
        )
        .toList();
    final canInvite =
        _canUse('invitations') &&
        !_operationPending &&
        !_changingModule &&
        model.canEdit &&
        model.snapshot['runtimeDaySceneReady'] == true;
    final invitationBoundary = _invitationBoundary;
    final levelWidth = _invitationLevelWidth();
    final state = _readState;
    return _localPage(
      [
        _toolbar<String>(
          items: const {'current': '当前地图', 'all': '全部地图'},
          value: scope,
          onChanged: (next) {
            setState(() => scope = next);
            _load();
          },
        ),
        _invitationFilters(
          levelWidth: levelWidth,
          onInvite: canInvite && levels.isNotEmpty
              ? () => _runModuleOperation('invitations', () async {
                  final requestedContext = _invitationContext();
                  final requestedLevels = (levels.toList()..sort()).join(',');
                  if (await confirmCurrentAction(
                        context,
                        model,
                        '邀请稀客',
                        '按当前地图范围和羁绊等级邀请可用稀客。名称筛选只影响列表，不改变批量邀请范围。',
                      ) &&
                      _canUse('invitations') &&
                      model.canEdit)
                    await model.mutate(
                      '/rare-guests/invite-all',
                      query: {...requestedContext, 'levels': requestedLevels},
                    );
                })
              : null,
        ),
      ],
      candidates.isEmpty
          ? EmptyState(
              state?.message ?? '暂无邀请候选',
              '',
              key: const ValueKey('invitation-items'),
            )
          : ListView.builder(
              key: const ValueKey('invitation-items'),
              itemCount: candidates.length,
              itemBuilder: (context, index) {
                final item = candidates[index], id = _itemId(candidates[index]);
                return InvitationItemContent(
                  item,
                  key: ValueKey('invitation-item-${item['id']}-$index'),
                  onInvite: canInvite && id != null && item['canInvite'] == true
                      ? () => _runModuleOperation('invitations', () async {
                          // 外层按钮捕获渲染时的场景与身份，不能借后来恢复的连接
                          // 执行旧行操作；目录刷新还需重新确认唯一 ID 的可邀请状态。
                          if (!_canUse('invitations') ||
                              !model.canEdit ||
                              invitationBoundary != _invitationBoundary)
                            return;
                          final current = records(
                            model
                                .resources['/rare-guests/invitations']?['candidates'],
                          ).where((entry) => _itemId(entry) == id).toList();
                          if (current.length != 1 ||
                              current.single['canInvite'] != true)
                            return;
                          await model.mutate(
                            '/rare-guests/invite',
                            query: {..._invitationContext(), 'guestId': '$id'},
                          );
                        })
                      : null,
                );
              },
            ),
    );
  }

  /// 筛选与批量操作保持一行：左侧说明和右侧按钮固定，中间六个等级按需横滚。
  /// 不通过缩小字体、压缩复选框命中区或换成图标省空间；短高度和150%字号下
  /// 仍只占一行控制区。横滚条留出自身绘制空间，避免覆盖数字和复选项的命中区域。
  Widget _invitationFilters({
    required double levelWidth,
    required VoidCallback? onInvite,
  }) => Padding(
    padding: StewardDensity.togglePadding,
    child: Row(
      key: const ValueKey('invitation-filter-row'),
      children: [
        const Text('羁绊', style: StewardText.body),
        const SizedBox(width: 8),
        Expanded(
          child: RawScrollbar(
            controller: invitationLevelsScroll,
            thumbVisibility: true,
            scrollbarOrientation: ScrollbarOrientation.bottom,
            thickness: 3,
            radius: const Radius.circular(3),
            thumbColor: context.stewardPaint.foreground(
              context.theme.colors.primary,
              alpha: .5,
            ),
            child: Semantics(
              label: '羁绊等级筛选，可左右滚动',
              child: SingleChildScrollView(
                key: const PageStorageKey('invitation-level-scroll'),
                controller: invitationLevelsScroll,
                scrollDirection: Axis.horizontal,
                padding: const EdgeInsets.only(bottom: 4),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    for (var level = 0; level <= 5; level++) ...[
                      if (level > 0) const SizedBox(width: 4),
                      // Forui标签列使用FlexColumnWidth，固有宽度会漏掉数字列。
                      // 沿用实际字体测量和至少44px的完整点击宽度，避免标签越界。
                      SizedBox(
                        key: ValueKey('invitation-level-$level'),
                        width: levelWidth,
                        child: FCheckbox(
                          label: Text('$level'),
                          semanticsLabel: '羁绊 $level 级',
                          value: levels.contains(level),
                          onChange: (selected) => setState(() {
                            if (selected) {
                              levels.add(level);
                            } else {
                              levels.remove(level);
                            }
                          }),
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ),
          ),
        ),
        const SizedBox(width: 8),
        StewardButton(
          '邀请筛选范围',
          key: const ValueKey('invitation-bulk-action'),
          onPressed: onInvite,
        ),
      ],
    ),
  );

  /// 使用当前主题和真实TextScaler测量0–5的最宽数字；不把字号倍率当作固定像素。
  /// 宽度包括Forui公开样式中的复选框尺寸、两列padding，并向上取整留出像素余量。
  /// 至少44px使数字及其旁边空白都属于同一个可点击复选项，不额外叠加手势区域。
  double _invitationLevelWidth() {
    final checkbox = context.theme.checkboxStyle;
    final labelStyle = checkbox.trailingLabelStyle;
    final direction = Directionality.of(context);
    final painter = TextPainter(
      textDirection: direction,
      textScaler: MediaQuery.textScalerOf(context),
      maxLines: 1,
    );
    var digitWidth = 0.0;
    try {
      for (var level = 0; level <= 5; level++) {
        painter.text = TextSpan(
          text: '$level',
          style: labelStyle.labelTextStyle.resolve(const {}),
        );
        painter.layout();
        if (painter.width > digitWidth) digitWidth = painter.width;
      }
    } finally {
      painter.dispose();
    }
    final width =
        checkbox.size +
        labelStyle.childPadding.resolve(direction).horizontal +
        labelStyle.labelPadding.resolve(direction).horizontal +
        digitWidth.ceilToDouble() +
        2;
    return width < 44 ? 44 : width;
  }

  /// 目录条目以明确的非负整数标识；缺失或非法 ID 仍可查看，但不可用于写入。
  int? _itemId(DataMap entry) => switch (entry['id']) {
    final int id when id >= 0 => id,
    _ => null,
  };

  Object get _invitationBoundary => (
    model.connectionVersion,
    model.connected,
    model.snapshot['runtimeDaySceneReady'],
    model.snapshot['runtimeDaySceneGeneration'],
    model.snapshot['activeDayMapLabel'],
    model.authority['registryId'],
    model.authority['authorityRevision'],
    scope,
  );

  Map<String, String> _invitationContext() => {
    'scope': scope,
    'expectedDaySceneGeneration':
        '${model.snapshot['runtimeDaySceneGeneration']}',
    'expectedMapLabel': label(model.snapshot['activeDayMapLabel'], ''),
  };

  /// 库存勾选是本地草稿，但不能跨连接、游戏会话或设备身份沿用。查看设备仍可
  /// 勾选以浏览范围；是否真正修改库存始终由原有 canEdit 与确认流程决定。
  Object get _inventoryOwner => (
    model,
    model.connectionVersion,
    model.connected,
    model.snapshot['automationSessionId'],
    model.authority['registryId'],
    model.authority['currentDeviceId'],
    model.authority['primaryDeviceId'],
    model.authority['authorityRevision'],
    model.primary,
  );

  /// 快捷写入不弹二次确认，但仍绑定触发控件时的游戏与设备边界；切换场景后不能
  /// 将旧控件的点击转发到另一份存档/经营生命周期。数量本身不在边界中，点击时另读最新值。
  Object get _inventoryWriteBoundary => (
    _inventoryOwner,
    model.snapshot['runtimeDaySceneGeneration'],
    model.snapshot['nightBusinessGeneration'],
    model.snapshot['activeDayMapLabel'],
    model.snapshot['activeSceneName'],
  );

  String get _inventoryCatalogKey =>
      inventoryType == 'ingredient' ? 'ingredients' : 'beverages';

  DataMap get _inventoryQuantities => inventoryEditingQuantities(
    model.catalogItems(_inventoryCatalogKey),
    model.runtime,
    type: inventoryType,
    complete:
        model.snapshot['runtimeLoaded'] == true &&
        model.snapshot['runtimeDataComplete'] == true,
  );

  /// 全选、旧回调和确认后的最终提交共用同一选择条件。实时库存变为无限或未知
  /// 时撤回该项，不让此前的可写快照继续授权一次已失效的数量修改。
  Set<int> _editableInventoryIds(List<DataMap> catalog) {
    final unique = _uniqueInventoryIds(catalog);
    final quantities = _inventoryQuantities;
    return {
      for (final entry in catalog)
        if (_itemId(entry) case final int id
            when unique.contains(id) &&
                inventoryQuantityEditable(entry, quantities))
          id,
    };
  }

  /// 同一类型目录中的重复 ID 不能因筛选后只剩一行就变成“唯一”。先检查完整
  /// 目录，再应用名称筛选；非法或歧义行仍可查看，但单选、全选和写入均排除。
  Set<int> _uniqueInventoryIds(List<DataMap> catalog) {
    final counts = <int, int>{};
    for (final entry in catalog) {
      final id = _itemId(entry);
      if (id != null)
        counts.update(id, (count) => count + 1, ifAbsent: () => 1);
    }
    return {
      for (final entry in counts.entries)
        if (entry.value == 1) entry.key,
    };
  }

  /// 将渲染时的目录身份复制成标量，防止模型原地修改 Map 后旧回调读到新含义。
  /// 库存数量轮询不改变条目身份，不因此清空选择或让正常点击失效。
  List<(Object?, Object?)> _inventoryCatalogStamp(List<DataMap> catalog) => [
    for (final entry in catalog) (entry['id'], entry['name']),
  ];

  bool _inventorySelectionCurrent(
    Object owner,
    String type,
    String filter,
    List<(Object?, Object?)> catalog,
  ) =>
      _canUse('inventory') &&
      !_moduleSwitchBusy &&
      owner == _inventoryOwner &&
      type == inventoryType &&
      filter == search &&
      listEquals(
        catalog,
        _inventoryCatalogStamp(model.catalogItems(_inventoryCatalogKey)),
      );

  Widget _inventory() {
    // 全选的真实点击区至少44px；字号增大时随公共控制区一起增长。复选图形
    // 仍使用主题尺寸，表头Row按整块居中，使排序按钮与全选保持同一中心线。
    final selectionTarget = math.max(44.0, stewardControlHeight(context));
    final catalog = model.catalogItems(_inventoryCatalogKey);
    final uniqueIds = _uniqueInventoryIds(catalog);
    final editableIds = _editableInventoryIds(catalog);
    final owner = _inventoryOwner;
    if (_inventorySelectionOwner != owner) {
      selectedIds.clear();
      _inventorySelectionOwner = owner;
    }
    // 目录与库存刷新仅保留仍然可修改的原 ID，不自动选择新增行。
    selectedIds.removeWhere((id) => !editableIds.contains(id));
    final renderedType = inventoryType, renderedFilter = search;
    final renderedCatalog = _inventoryCatalogStamp(catalog);
    final renderedWriteBoundary = _inventoryWriteBoundary;
    bool selectionCurrent() => _inventorySelectionCurrent(
      owner,
      renderedType,
      renderedFilter,
      renderedCatalog,
    );
    final quantities = _inventoryQuantities;
    final entries = sortInventoryEntries(
      catalog.where((item) => label(item['name']).contains(search)).toList(),
      quantities,
      column: inventorySort,
      ascending: inventoryAscending,
    );
    // 可确认的目录身份不包含排序位置：库存变化或用户排序时，复选框沿原ID移动，
    // 不销毁重建其焦点/动画。歧义行只读，单独使用位置键避免重复Key影响其它条目。
    final entryKeys = [
      for (final (index, entry) in entries.indexed)
        ValueKey(switch (_itemId(entry)) {
          final int id when uniqueIds.contains(id) =>
            'inventory-item-$inventoryType-$id',
          _ => 'inventory-item-$inventoryType-invalid-$index',
        }),
    ];
    final entryIndices = {
      for (final (index, key) in entryKeys.indexed) key: index,
    };
    final visibleIds = {
      for (final entry in entries)
        if (_itemId(entry) case final int id when editableIds.contains(id)) id,
    };
    final selectedVisible = visibleIds.intersection(selectedIds).length;
    final allVisible =
        visibleIds.isNotEmpty && selectedVisible == visibleIds.length;
    final partiallySelected = selectedVisible > 0 && !allVisible;
    final outsideCount = selectedIds.difference(visibleIds).length;
    final renderedSelection = Set<int>.of(selectedIds);
    void toggleVisible() {
      if (!selectionCurrent() ||
          visibleIds.isEmpty ||
          !visibleIds.every(_editableInventoryIds(catalog).contains) ||
          !setEquals(renderedSelection, selectedIds))
        return;
      setState(() {
        // 部分选择时补齐当前筛选；仅在全部已选时清除此范围，筛选外选择不受影响。
        if (visibleIds.every(selectedIds.contains)) {
          selectedIds.removeAll(visibleIds);
        } else {
          selectedIds.addAll(visibleIds);
        }
      });
    }

    return _localPage(
      [
        _toolbar<String>(
          items: const {'ingredient': '食材', 'beverage': '酒水'},
          value: inventoryType,
          onChanged: (next) => setState(() {
            inventoryType = next;
            selectedIds.clear();
          }),
        ),
        // 批量编辑只在选择后出现，与只读浏览明确分开；输入与确认仍走原有权限边界。
        if (selectedIds.isNotEmpty)
          ToolRow([
            StatusBadge('已选 ${selectedIds.length} 项', emphasis: true),
            if (outsideCount > 0) StatusBadge('筛选外 $outsideCount 项'),
            SizedBox(
              width: 150,
              child: StewardField(
                '目标数量 0–9999',
                showLabel: false,
                hint: '目标数量 0–9999',
                controller: quantity,
                numeric: true,
              ),
            ),
            StewardButton(
              '设置已选 ${selectedIds.length} 项',
              primary: true,
              onPressed:
                  model.canEdit && !_moduleSwitchBusy && selectedIds.isNotEmpty
                  ? () {
                      if (!selectionCurrent() ||
                          !setEquals(renderedSelection, selectedIds))
                        return;
                      _runModuleOperation(
                        'inventory',
                        () => _setQuantity(context),
                      );
                    }
                  : null,
            ),
            StewardButton(
              '清除选择',
              onPressed: _moduleSwitchBusy
                  ? null
                  : () {
                      if (selectionCurrent()) setState(selectedIds.clear);
                    },
            ),
          ], alignInputs: true),
      ],
      Column(
        children: [
          // 排序表头常驻于列表上方；切换字段从升序开始，同字段再次点击切换方向。
          // 排序只重排浏览，不清除勾选，也不触发 HTTP 或库存写入。
          Row(
            children: [
              // Forui 复选框本身是二态；部分选择使用相同样式的横线图标，外层
              // 独立语义节点明确报告 mixed，并保留键盘、触屏及读屏同一操作。
              Semantics(
                key: const ValueKey('inventory-select-all'),
                container: true,
                label: '全选当前筛选',
                value:
                    '当前筛选已选 $selectedVisible / ${visibleIds.length} 项，筛选外已选 $outsideCount 项',
                checked: partiallySelected ? null : allVisible,
                mixed: partiallySelected,
                enabled: visibleIds.isNotEmpty && !_moduleSwitchBusy,
                onTap: visibleIds.isNotEmpty && !_moduleSwitchBusy
                    ? toggleVisible
                    : null,
                excludeSemantics: true,
                child: FTooltip(
                  tipBuilder: (_, _) => Text(
                    '${allVisible ? '清选' : '全选'}当前筛选 · $selectedVisible / ${visibleIds.length} 项',
                  ),
                  child: SizedBox(
                    width: 44,
                    height: selectionTarget,
                    child: Center(
                      child: FCheckbox(
                        style: FCheckboxStyleDelta.delta(
                          icon: FIcon(
                            partiallySelected
                                ? FLucideIcons.minus
                                : FLucideIcons.check,
                          ),
                          // 把扩大的可点击空白纳入复选框自身布局，而不是只改变
                          // 外层尺寸。水平保留44px，垂直随统一控制区高度增长。
                          trailingLabelStyle: FLabelStyleDelta.delta(
                            childPadding: EdgeInsetsGeometryDelta.value(
                              EdgeInsets.symmetric(
                                horizontal:
                                    (44 - context.theme.checkboxStyle.size) / 2,
                                vertical:
                                    (selectionTarget -
                                        context.theme.checkboxStyle.size) /
                                    2,
                              ),
                            ),
                          ),
                        ),
                        value: allVisible || partiallySelected,
                        enabled: visibleIds.isNotEmpty && !_moduleSwitchBusy,
                        onChange: visibleIds.isNotEmpty && !_moduleSwitchBusy
                            ? (_) => toggleVisible()
                            : null,
                      ),
                    ),
                  ),
                ),
              ),
              Expanded(
                child: Align(
                  alignment: Alignment.centerLeft,
                  child: _inventoryHeader('名称', InventorySortColumn.name),
                ),
              ),
              _inventoryHeader('库存', InventorySortColumn.quantity),
            ],
          ),
          Expanded(
            child: ListView.builder(
              key: const ValueKey('inventory-items'),
              itemCount: entries.length,
              findChildIndexCallback: (key) => entryIndices[key],
              itemBuilder: (context, index) {
                final entry = entries[index];
                final id = _itemId(entry);
                final selectable =
                    id != null &&
                    editableIds.contains(id) &&
                    !_moduleSwitchBusy;
                final unavailableReason = id == null || !uniqueIds.contains(id)
                    ? '条目编号无效或不唯一，不能修改。'
                    : inventoryReadOnlyReason(entry, quantities);
                final count = inventoryQuantity(entry, quantities);
                VoidCallback? adjustment(int delta) {
                  if (id == null ||
                      !selectable ||
                      !model.canEdit ||
                      inventoryAdjustmentTarget(count, delta) == null)
                    return null;
                  return () => _adjustInventory(
                    id,
                    delta,
                    owner: owner,
                    type: renderedType,
                    filter: renderedFilter,
                    catalog: renderedCatalog,
                    boundary: renderedWriteBoundary,
                  );
                }

                return InventoryItem(
                  key: entryKeys[index],
                  name: label(entry['name']),
                  kind: inventoryType == 'ingredient'
                      ? EntityImageKind.ingredient
                      : EntityImageKind.beverage,
                  entityId: id != null && uniqueIds.contains(id) ? id : null,
                  stockText: inventoryStockText(entry, quantities),
                  tags: words(entry['tags']),
                  readOnlyReason: unavailableReason,
                  showAdjustments: true,
                  adjusting: _inventoryAdjustment == (renderedType, id),
                  onDecrease: adjustment(-10),
                  onIncrease: adjustment(10),
                  selected: selectedIds.contains(id),
                  enabled: selectable,
                  onChanged: !selectable
                      ? null
                      : (checked) {
                          if (!selectionCurrent() ||
                              !_editableInventoryIds(catalog).contains(id))
                            return;
                          setState(() {
                            if (checked) {
                              selectedIds.add(id);
                            } else {
                              selectedIds.remove(id);
                            }
                          });
                        },
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  /// 快捷写入与批量选中集合完全分离。旧回调先检查页面、目录、身份、场景和处理状态，
  /// 再从当前模型读取该精确ID的数量；不使用渲染时闭包中的旧数量，也不自动重试失败请求。
  Future<void> _adjustInventory(
    int id,
    int delta, {
    required Object owner,
    required String type,
    required String filter,
    required List<(Object?, Object?)> catalog,
    required Object boundary,
  }) async {
    if (!_inventorySelectionCurrent(owner, type, filter, catalog) ||
        !model.canEdit ||
        boundary != _inventoryWriteBoundary)
      return;
    final currentCatalog = model.catalogItems(_inventoryCatalogKey);
    if (!_editableInventoryIds(currentCatalog).contains(id)) return;
    final entry = currentCatalog.singleWhere((entry) => _itemId(entry) == id);
    final currentQuantity = inventoryQuantity(entry, _inventoryQuantities);
    final target = inventoryAdjustmentTarget(currentQuantity, delta);
    if (target == null) return;

    // _runModuleOperation 在第一次 await 前同步设置进行中，阻止同帧重复点击及其它
    // 行/批量操作并发。模型负责服务端失败反馈与成功后的快照刷新，本页不乐观改数量。
    await _runModuleOperation('inventory', () async {
      setState(() => _inventoryAdjustment = (type, id));
      try {
        await model.mutate(
          '/inventory/set',
          // 后端在同一次主线程读写中比较数量，覆盖网络/排队期间的游戏消耗窗口。
          // 失配由原失败通道提示刷新；本页既不更新乐观值，也不重放旧请求。
          query: {
            'type': type,
            'id': '$id',
            'qty': '$target',
            'expectedQuantity': '$currentQuantity',
          },
        );
      } finally {
        if (mounted) setState(() => _inventoryAdjustment = null);
      }
    });
  }

  Widget _inventoryHeader(String title, InventorySortColumn column) {
    final selected = inventorySort == column;
    return Semantics(
      label:
          '按$title排序，${selected
              ? inventoryAscending
                    ? '当前升序'
                    : '当前降序'
              : '点击升序'}',
      child: FButton(
        key: ValueKey('inventory-sort-${column.name}'),
        variant: FButtonVariant.ghost,
        // 排序是普通文本动作，和数量输入/确认按钮共用实际控制区度量，
        // 不再由页面另选sm/md密度；文字换行时仍允许自然增加高度。
        style: stewardButtonStyle(context),
        mainAxisSize: MainAxisSize.min,
        onPress: () => setState(() {
          inventoryAscending = selected ? !inventoryAscending : true;
          inventorySort = column;
        }),
        // 使用同一套矢量图标，避免Android将Unicode双向箭头渲染成彩色Emoji，
        // 导致列头字号、基线和色彩与Windows不一致；方向仍由原排序状态决定。
        suffix: Icon(
          selected
              ? inventoryAscending
                    ? FLucideIcons.arrowUp
                    : FLucideIcons.arrowDown
              : FLucideIcons.arrowUpDown,
          size: MediaQuery.textScalerOf(context).scale(14),
        ),
        child: Text(title),
      ),
    );
  }

  Future<void> _setQuantity(BuildContext context) async {
    // 确认针对打开时的精确选择，不能在等待期间借用后来改变的类别或选中集合。
    // 模型对象本身也属于身份：同数值版本的新连接模型不能复用旧模型的确认结果。
    final requestedOwner = _inventoryOwner;
    final requestedType = inventoryType;
    final requestedIds = selectedIds.toList()..sort();
    final requestedCatalog = _inventoryCatalogStamp(
      model.catalogItems(_inventoryCatalogKey),
    );
    final editableIds = _editableInventoryIds(
      model.catalogItems(_inventoryCatalogKey),
    );
    if (requestedIds.isEmpty || !requestedIds.every(editableIds.contains))
      return;
    final visibleIds = {
      for (final entry in model.catalogItems(_inventoryCatalogKey))
        if (label(entry['name']).contains(search)) _itemId(entry),
    };
    final outsideCount = requestedIds
        .where((id) => !visibleIds.contains(id))
        .length;
    final count = int.tryParse(quantity.text);
    if (count == null || count < 0 || count > 9999) {
      await confirmCurrentAction(context, model, '数量无效', '请输入 0 到 9999 之间的整数。');
      return;
    }
    if (!await confirmCurrentAction(
      context,
      model,
      '修改游戏库存',
      '将所选 ${requestedIds.length} 项的数量设置为 $count。'
          '${outsideCount > 0 ? '其中包含当前筛选外 $outsideCount 项。' : ''}'
          '该操作写入游戏库存。',
    ))
      return;
    if (!_canUse('inventory') ||
        !model.canEdit ||
        requestedOwner != _inventoryOwner ||
        requestedType != inventoryType)
      return;
    // 确认期间目录可能被替换或新增同 ID。沿用原连接/场景确认门禁，并追加精确
    // 目录复核；不能将已删除或歧义行带入批量写入，也不自动重放此次确认。
    final currentCatalog = model.catalogItems(_inventoryCatalogKey);
    if (!listEquals(requestedCatalog, _inventoryCatalogStamp(currentCatalog)) ||
        !requestedIds.every(_editableInventoryIds(currentCatalog).contains))
      return;
    await model.mutate(
      '/inventory/bulk-set',
      query: {
        'type': requestedType,
        'ids': requestedIds.join(','),
        'qty': '$count',
      },
    );
  }
}

/// 短状态与详细原因分开保存；它只是只读展示投影，不能参与模块权限或业务决策。
class _ExtensionReadState {
  const _ExtensionReadState(
    this.message, {
    this.detail,
    this.busy = false,
    this.error = false,
  });
  final String message;
  final String? detail;
  final bool busy, error;
  PageActivity get activity =>
      PageActivity(message: message, details: detail, busy: busy, error: error);
}
