import 'dart:async';

import 'package:animations/animations.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_ui/material_ui.dart' show MaterialApp, SelectableText;
import 'package:forui/forui.dart';

import '../features/overview.dart';
import '../features/data.dart';
import '../features/service.dart';
import '../features/service_focus.dart';
import '../features/recommendations.dart';
import '../features/tools.dart';
import '../features/settings.dart';
import '../features/resource_management.dart';
import '../ui/game_media_scope.dart';
import '../ui/components.dart';
import '../ui/gamepad_navigation.dart';
import '../ui/page_activity.dart';
import '../ui/steward_style.dart';
import '../ui/steward_opacity.dart';
import '../ui/steward_theme.dart';
import 'workbench_model.dart';

/// Provider 只注入应用生命周期所有者；列表局部选择仍由 Widget 持有，不污染业务会话。
final workbenchProvider = Provider<WorkbenchModel>(
  (ref) => throw StateError('应用未注入工作台。'),
);

class StewardApp extends ConsumerWidget {
  const StewardApp({this.previewControls, super.key});

  /// 仅独立离线预览入口传入场景控制条，生产入口始终不传入此控件。
  final Widget? previewControls;
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final model = ref.watch(workbenchProvider);
    return ListenableBuilder(
      listenable: model,
      builder: (context, _) {
        // 按绘制角色给主题注入透明度，不在整棵控件树外套 Opacity；否则控件底色
        // 会错误地受“内容不透明度”控制，文字与填充也无法分别调整。
        double opacity(String key, double minimum) => model.desktop
            ? ((model.localPreferences[key] as num?)?.toDouble() ?? 100).clamp(
                    minimum,
                    100,
                  ) /
                  100
            : 1;
        final theme = stewardTheme(
          dark: model.dark,
          touch: !model.desktop,
          backgroundOpacity: opacity('backgroundOpacityPercent', 20),
          contentOpacity: opacity('contentOpacityPercent', 35),
        );
        return MaterialApp(
          title: '夜雀管家',
          debugShowCheckedModeBanner: false,
          locale: const Locale('zh', 'CN'),
          localizationsDelegates: FLocalizations.localizationsDelegates,
          supportedLocales: FLocalizations.supportedLocales,
          theme: theme.toApproximateMaterialTheme(),
          builder: (context, child) => FTheme(
            data: theme,
            child: FToaster(
              child: FTooltipGroup(
                child: MediaQuery(
                  data: MediaQuery.of(context).copyWith(
                    textScaler: TextScaler.linear(
                      ((model.localPreferences['fontScalePercent'] as num?)
                                      ?.toDouble() ??
                                  100)
                              .clamp(90, 150) /
                          100,
                    ),
                    disableAnimations:
                        model.reduceMotion ||
                        MediaQuery.disableAnimationsOf(context),
                  ),
                  child: GameMediaScope(
                    resolveImage: model.gameMediaImage,
                    revision: (
                      model.connectionVersion,
                      model.gameMediaRevision,
                    ),
                    // 核验、等待目录、提取和失败共用同一个独立面板，不能先在页头
                    // 报告核验、缓存失效后再切换展示位置。门禁替换整个 Navigator，
                    // 外部设备触发重读时也撤回已打开的编辑弹窗及快捷键。已就绪且
                    // 同代次的普通进度轮询仍保持 ready，不会反复挂载初始化面板。
                    child: model.connected && !model.gameMediaReady
                        ? GameMediaInitialization(
                            model,
                            key: const ValueKey('game-media-initialization'),
                            previewControls: previewControls,
                          )
                        : child!,
                  ),
                ),
              ),
            ),
          ),
          home: WorkbenchShell(model, previewControls: previewControls),
        );
      },
    );
  }
}

/// 稳定目的地按平台使用桌面侧栏或手机底部导航；数据刷新不参与页面转场，
/// 旋转和窗口缩放也不替换IndexedStack，避免丢失各页草稿、滚动和输入焦点。
class WorkbenchShell extends StatefulWidget {
  const WorkbenchShell(this.model, {this.previewControls, super.key});
  final WorkbenchModel model;
  final Widget? previewControls;
  @override
  State<WorkbenchShell> createState() => _WorkbenchShellState();
}

class _WorkbenchShellState extends State<WorkbenchShell>
    with SingleTickerProviderStateMixin {
  /// 初始化面板由应用外层统一挂载。模型通知到下一帧卸载业务壳之间仍可能收到
  /// 输入事件，因此事件入口继续检查资源门禁，不能把旧按键排队到恢复后的页面。
  bool get _resourcesBlocked =>
      widget.model.connected && !widget.model.gameMediaReady;
  // 目的地 ID 属于持久化偏好；新增页面不重排旧 ID，避免升级后恢复到错误页面。
  // 显示顺序独立管理，桌面、手机及手柄采用同一顺序，设置始终位于最后。
  static const names = ['总览', '经营', '推荐', '扩展', '设置', '数据'];
  static const destinationOrder = [0, 1, 2, 3, 5, 4];
  static const descriptions = [
    '掌握经营状态与实时供给',
    '查看订单方案与执行进度',
    '查找搭配、管理收藏与配方',
    '任务、邀请、库存、自动化与游戏界面辅助',
    '连接设备与调整工作台',
    '浏览料理、酒水与游戏图像资源',
  ];
  static const icons = [
    FLucideIcons.house,
    FLucideIcons.utensils,
    FLucideIcons.sparkles,
    FLucideIcons.wrench,
    FLucideIcons.settings,
    FLucideIcons.database,
  ];
  int index = 1;
  int inputSequence = 0;
  bool restoredPage = false;
  bool serviceFocusActive = false;
  bool pageSwitchPending = false;
  final gamepadNavigation = GamepadNavigationController();
  // 每个主页面独立拥有提示通道，隐藏页完成异步读取时不污染当前页标题。
  final pageActivities = List.generate(
    names.length,
    (_) => PageActivityChannel(),
  );
  // 版本从随应用一起打包的 pubspec 读取，只读一次；不把 Mod 版本冒充客户端版本。
  late final Future<String> clientVersion = _readClientVersion();
  late final animation = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 180),
    value: 1,
  );
  @override
  void initState() {
    super.initState();
    // 壳层在资源门禁解除后会重新创建。当前输入序号是历史事件，不能把核验期间
    // 按下的确认/切页键重放到刚恢复的页面；仅监听本实例挂载之后的新输入边缘。
    inputSequence = widget.model.inputSequence;
    widget.model.addListener(_handleInput);
    // 热启动时偏好可能在壳层创建前已完成读取，首次挂载也应恢复最后目的地。
    _handleInput();
  }

  @override
  void dispose() {
    widget.model.removeListener(_handleInput);
    gamepadNavigation.dispose();
    animation.dispose();
    for (final channel in pageActivities) {
      channel.dispose();
    }
    super.dispose();
  }

  void _select(int next) {
    if (_resourcesBlocked) return;
    if (index == next) return;
    widget.model.activatePage(next);
    setState(() => index = next);
    // 同一原生轮询可能同时产生肩键和扳机。新页面尚未提交布局前丢弃页内切换，
    // 不能让紧随其后的 LT/RT 操作仍登记为可见的旧页签，也不延迟重放该事件。
    pageSwitchPending = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) pageSwitchPending = false;
    });
    unawaited(widget.model.updateLocal({'lastPage': next}));
    if (!widget.model.reduceMotion && !MediaQuery.disableAnimationsOf(context))
      animation.forward(from: 0);
  }

  void _handleInput() {
    if (_resourcesBlocked) return;
    if (!restoredPage && widget.model.localPreferences['lastPage'] is int) {
      restoredPage = true;
      final saved = widget.model.localPreferences['lastPage'] as int;
      if (saved >= 0 && saved < names.length)
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) _select(saved);
        });
    }
    if (inputSequence == widget.model.inputSequence) return;
    inputSequence = widget.model.inputSequence;
    final action = widget.model.inputAction;
    // 原生事件可能在构建阶段到达，导航只在该帧提交后运行。
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _dispatch(action);
    });
  }

  void _dispatch(String action) {
    if (_resourcesBlocked) return;
    if (pageSwitchPending &&
        (action == 'previousSubTab' || action == 'nextSubTab'))
      return;
    // 控制器只消费页内切栏/失活事件，其余动作继续走既有焦点及主导航行为。
    if (gamepadNavigation.handleAction(action)) return;
    final focus = FocusManager.instance.primaryFocus;
    final target = focus?.context ?? context;
    final route = ModalRoute.of(context);
    final shellScope = FocusScope.of(context);
    final focusedScope = focus is FocusScopeNode
        ? focus
        : focus?.enclosingScope;
    // 模态可能尚未完成自动聚焦；因此同时检查壳路由仍在前台与目标所属路由。
    // Forui 下拉使用同路由的独立焦点域，不能仅凭路由相等切走其后的主页面。
    final canChangePage =
        route?.isCurrent != false &&
        ModalRoute.of(target) == route &&
        (focusedScope == null ||
            identical(focusedScope, shellScope) ||
            shellScope.ancestors.contains(focusedScope));
    switch (action) {
      case 'up':
        focus?.focusInDirection(TraversalDirection.up);
        break;
      case 'down':
        focus?.focusInDirection(TraversalDirection.down);
        break;
      case 'left':
        focus?.focusInDirection(TraversalDirection.left);
        break;
      case 'right':
        focus?.focusInDirection(TraversalDirection.right);
        break;
      case 'activate':
        Actions.maybeInvoke(target, const ActivateIntent());
        break;
      case 'favorite':
        Actions.maybeInvoke(target, const FavoriteIntent());
        break;
      case 'back':
        if (Navigator.of(context).canPop()) {
          Navigator.of(context).maybePop();
        } else {
          Actions.maybeInvoke(target, const DismissIntent());
        }
        break;
      case 'previousTab':
        if (canChangePage) {
          _select(
            destinationOrder[(destinationOrder.indexOf(index) +
                    names.length -
                    1) %
                names.length],
          );
        }
        break;
      case 'nextTab':
        if (canChangePage) {
          _select(
            destinationOrder[(destinationOrder.indexOf(index) + 1) %
                names.length],
          );
        }
        break;
      case 'pageUp':
        Actions.maybeInvoke(
          target,
          const ScrollIntent(
            direction: AxisDirection.up,
            type: ScrollIncrementType.page,
          ),
        );
        break;
      case 'pageDown':
        Actions.maybeInvoke(
          target,
          const ScrollIntent(
            direction: AxisDirection.down,
            type: ScrollIncrementType.page,
          ),
        );
        break;
    }
  }

  Future<String> _readClientVersion() async {
    try {
      final source = await rootBundle.loadString('pubspec.yaml');
      final match = RegExp(
        r'^version:\s*([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)\s*(?:#.*)?$',
        multiLine: true,
      ).firstMatch(source);
      return match?.group(1) ?? '未知';
    } catch (_) {
      // 版本元数据缺失不能阻断连接与经营，也不能用固定字符串伪装成已确认版本。
      return '未知';
    }
  }

  @override
  Widget build(BuildContext context) {
    final model = widget.model;
    // 平台能力由宿主模型给出，不能把横屏手机误判为桌面，也不能把窄Windows
    // 窗口误判为手机。应用未使用Scaffold，因此仅在这里消费一次键盘viewInsets。
    final keyboardInset = model.desktop
        ? 0.0
        : MediaQuery.viewInsetsOf(context).bottom;
    final keyboardVisible = keyboardInset > 0;
    // 横屏与软键盘同时出现时，固定页头和状态行会占去大部分可用高度，
    // 即使正文可以滚动，也可能连一个完整输入框或列表触控目标都容纳不下。
    // 仅在手机输入且安全区扣除后不足320px时暂时收起这两处辅助信息；若当前
    // 页面出错，保留无上下边距的页头入口，避免错误移出正文后被键盘完全遮蔽。
    // 桌面及正常高度手机保持原布局，收起键盘后完整标题、反馈和版本立即恢复。
    final compactInput =
        !model.desktop &&
        keyboardVisible &&
        MediaQuery.sizeOf(context).height -
                keyboardInset -
                MediaQuery.paddingOf(context).vertical <
            320;
    final hideWindowUi =
        index == 1 && serviceFocusActive && !serviceFocusShowsWindowUi(model);
    final pages = [
      OverviewPage(model),
      ServicePage(
        model,
        onFocusModeChanged: (active) {
          if (serviceFocusActive != active) {
            setState(() => serviceFocusActive = active);
          }
        },
      ),
      RecommendationsPage(model, active: index == 2),
      ToolsPage(model, active: index == 3),
      SettingsPage(model, active: index == 4),
      DataPage(model, active: index == 5),
    ];
    final body = FocusTraversalGroup(
      policy: ReadingOrderTraversalPolicy(),
      child: FadeThroughTransition(
        animation: animation,
        secondaryAnimation: const AlwaysStoppedAnimation<double>(0),
        // animations 默认用 Material canvasColor 填满工作区；显式透明才能保留外层背景 alpha。
        fillColor: const Color(0x00000000),
        child: IndexedStack(
          index: index,
          children: [
            // 隐藏页保留草稿和滚动，但不参与动效与窄屏系统返回处理。
            for (var page = 0; page < pages.length; page++)
              TickerMode(
                enabled: index == page,
                child: ExcludeFocus(
                  excluding: index != page,
                  // IndexedStack保留各页状态，但隐藏页仍可能因模型通知更新语义。
                  // 显式撤回隐藏页，并给每个页面稳定的独立边界，避免旧页语义片段
                  // 在切换当帧继续与新页合并；不卸载草稿、滚动或焦点节点，也不关闭
                  // 全应用无障碍功能。页面重新显示时由同一边界重新发布完整语义。
                  child: ExcludeSemantics(
                    excluding: index != page,
                    child: Semantics(
                      key: ValueKey('workbench-page-semantics-$page'),
                      container: true,
                      explicitChildNodes: true,
                      child: PageActivityScope(
                        channel: pageActivities[page],
                        child: pages[page],
                      ),
                    ),
                  ),
                ),
              ),
          ],
        ),
      ),
    );
    final shell = GamepadNavigationScope(
      controller: gamepadNavigation,
      child: Shortcuts(
        shortcuts: const <ShortcutActivator, Intent>{
          SingleActivator(LogicalKeyboardKey.escape): DismissIntent(),
        },
        child: SafeArea(
          child: Padding(
            padding: EdgeInsets.only(bottom: keyboardInset),
            child: ColoredBox(
              color: context.theme.colors.background,
              child: Column(
                children: [
                  if (widget.previewControls != null) widget.previewControls!,
                  // 连接状态和操作反馈统一归入底部状态区；顶部直接从工作区开始。
                  // 不再插入全局提示条，避免连接/保存反馈改变导航和正文的起始位置。
                  Expanded(
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        if (model.desktop)
                          Offstage(
                            offstage: hideWindowUi,
                            child: ExcludeFocus(
                              excluding: hideWindowUi,
                              child: _sidebar(),
                            ),
                          ),
                        Expanded(
                          child: Padding(
                            // 窗口缩放只改变可用空间，不在640px处切换另一套页面边距。
                            // 列表/详情等内容布局仍各自按实际约束决定如何适配。
                            padding: EdgeInsets.fromLTRB(
                              model.desktop ? 24 : 16,
                              0,
                              model.desktop ? 24 : 16,
                              model.desktop ? 12 : 8,
                            ),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.stretch,
                              children: [
                                // Offstage始终占据同一组件位置，只改变辅助区是否参与
                                // 布局，不增删正文前的兄弟节点，保留IndexedStack、
                                // 输入controller、焦点和中文composing正在编辑的草稿。
                                ValueListenableBuilder<PageActivity?>(
                                  valueListenable: pageActivities[index],
                                  builder: (context, activity, _) {
                                    // 专注外框隐藏时仍保留失败入口，否则本机偏好写入失败
                                    // 会完全失去反馈。此时仅显示紧凑错误，标题与介绍继续隐藏。
                                    final hidden =
                                        (hideWindowUi || compactInput) &&
                                        activity?.error != true;
                                    return Offstage(
                                      offstage: hidden,
                                      child: ExcludeFocus(
                                        excluding: hidden,
                                        child: _pageHeader(
                                          compact: compactInput || hideWindowUi,
                                          hideTitle: hideWindowUi,
                                        ),
                                      ),
                                    );
                                  },
                                ),
                                Expanded(
                                  child: SizedBox(
                                    key: const ValueKey(
                                      'workbench-page-content',
                                    ),
                                    child: body,
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                  Offstage(
                    offstage: compactInput || hideWindowUi,
                    child: ExcludeFocus(
                      excluding: compactInput || hideWindowUi,
                      child: _statusBar(),
                    ),
                  ),
                  // 输入时让键盘上方空间留给正文；导航暂时隐藏但页面树不卸载。
                  // 收起键盘后原导航选择恢复，系统返回仍由页面自己的PopScope处理。
                  if (!model.desktop && !keyboardVisible)
                    Offstage(
                      offstage: hideWindowUi,
                      child: ExcludeFocus(
                        excluding: hideWindowUi,
                        child: _bottomNavigation(),
                      ),
                    ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
    // Windows窗口依赖Flutter逐像素透明合成，不能在现有背景透明度之外再铺实底。
    // 手机则必须在SafeArea外绘制主题背景：edge-to-edge会让应用内容延伸至系统栏，
    // 仅设置系统栏颜色在新Android版本可能被忽略，安全区的真实像素仍需由应用提供。
    if (model.desktop) return shell;
    final background = context.theme.colors.background;
    final iconBrightness = model.dark ? Brightness.light : Brightness.dark;
    return AnnotatedRegion<SystemUiOverlayStyle>(
      key: const ValueKey('mobile-system-ui'),
      value: SystemUiOverlayStyle(
        statusBarColor: background,
        statusBarIconBrightness: iconBrightness,
        // statusBarBrightness仅供iOS使用，其值描述背景亮度，与Android图标亮度相反。
        statusBarBrightness: model.dark ? Brightness.dark : Brightness.light,
        systemNavigationBarColor: background,
        systemNavigationBarDividerColor: background,
        systemNavigationBarIconBrightness: iconBrightness,
        // 背景与图标已由同一主题匹配，禁止系统再叠加默认对比度蒙层造成黑色安全区。
        systemStatusBarContrastEnforced: false,
        systemNavigationBarContrastEnforced: false,
      ),
      child: ColoredBox(color: background, child: shell),
    );
  }

  /// 短状态固定在标题右侧，完整说明可点开查看；等待结束时保留相同页头高度，
  /// 防止正文上下跳动。Forui 按钮保留键盘、触屏和读屏操作，不只依赖鼠标悬停。
  Widget _pageHeader({bool compact = false, bool hideTitle = false}) => Padding(
    padding: EdgeInsets.symmetric(
      vertical: compact ? 0 : (widget.model.desktop ? 18 : 8),
    ),
    child: ConstrainedBox(
      constraints: BoxConstraints(minHeight: stewardControlHeight(context)),
      child: Row(
        children: [
          Expanded(
            child: Offstage(
              offstage: hideTitle,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    names[index],
                    key: const ValueKey('workbench-page-title'),
                    style: TextStyle(
                      fontSize: widget.model.desktop ? 23 : 18,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  if (widget.model.desktop) ...[
                    const SizedBox(height: 4),
                    Text(
                      descriptions[index],
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: StewardText.caption.copyWith(
                        color: context.theme.colors.mutedForeground,
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ),
          Flexible(
            child: ValueListenableBuilder<PageActivity?>(
              valueListenable: pageActivities[index],
              builder: (context, activity, _) => activity == null
                  ? const SizedBox.shrink()
                  : Align(
                      alignment: Alignment.centerRight,
                      child: Padding(
                        padding: const EdgeInsets.only(left: 12),
                        child: FTooltip(
                          tipBuilder: (context, _) =>
                              Text('${activity.message}\n点击查看完整提示'),
                          child: Semantics(
                            liveRegion: true,
                            child: FButton(
                              key: const ValueKey(
                                'workbench-page-activity-button',
                              ),
                              variant: FButtonVariant.ghost,
                              size: FButtonSizeVariant.sm,
                              mainAxisSize: MainAxisSize.min,
                              style: FButtonStyleDelta.delta(
                                tappableStyle: const FTappableStyleDelta.delta(
                                  motion: FTappableMotion.none,
                                ),
                                contentStyle: FButtonContentStyleDelta.delta(
                                  padding: const EdgeInsetsGeometryDelta.value(
                                    EdgeInsets.zero,
                                  ),
                                  constraints: BoxConstraints(
                                    minHeight: stewardControlHeight(context),
                                  ),
                                ),
                              ),
                              semanticsLabel: '页面状态：${activity.message}，查看完整提示',
                              onPress: () =>
                                  _showPageActivity(pageActivities[index]),
                              child: Flexible(
                                child: ExcludeSemantics(
                                  child: Row(
                                    mainAxisSize: MainAxisSize.min,
                                    children: [
                                      if (activity.busy && !activity.error)
                                        if (widget.model.reduceMotion ||
                                            MediaQuery.disableAnimationsOf(
                                              context,
                                            ))
                                          const Icon(
                                            FLucideIcons.hourglass,
                                            size: 12,
                                          )
                                        else
                                          const SizedBox(
                                            width: 12,
                                            height: 12,
                                            child: FCircularProgress(),
                                          )
                                      else
                                        Icon(
                                          activity.error
                                              ? FLucideIcons.circleAlert
                                              : FLucideIcons.info,
                                          size: 13,
                                          color: activity.error
                                              ? context.theme.colors.error
                                              : context
                                                    .theme
                                                    .colors
                                                    .mutedForeground,
                                        ),
                                      const SizedBox(width: 6),
                                      Flexible(
                                        child: Text(
                                          activity.message,
                                          key: const ValueKey(
                                            'workbench-page-activity',
                                          ),
                                          maxLines: 1,
                                          overflow: TextOverflow.ellipsis,
                                          style: StewardText.caption.copyWith(
                                            color: activity.error
                                                ? context.theme.colors.error
                                                : context
                                                      .theme
                                                      .colors
                                                      .mutedForeground,
                                          ),
                                        ),
                                      ),
                                    ],
                                  ),
                                ),
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
            ),
          ),
        ],
      ),
    ),
  );

  /// 对话框只读当前通道，不缓存点击前的错误文本。后台恢复后立即更新内容，
  /// 且没有可执行回调，不能借查看提示绕过业务页的重试、人工确认和权限检查。
  Future<void> _showPageActivity(PageActivityChannel channel) =>
      showFDialog<void>(
        context: context,
        builder: (context, style, animation) => FDialog(
          animation: widget.model.reduceMotion ? null : animation,
          semanticsLabel: '页面提示',
          builder: (context, _) => ConstrainedBox(
            constraints: BoxConstraints(
              maxHeight: MediaQuery.sizeOf(context).height * .75,
              maxWidth: 560,
            ),
            child: Padding(
              padding: const EdgeInsets.all(20),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const Text('页面提示', style: StewardText.sectionTitle),
                  const SizedBox(height: 12),
                  Flexible(
                    child: SingleChildScrollView(
                      child: ValueListenableBuilder<PageActivity?>(
                        valueListenable: channel,
                        builder: (context, activity, _) => Text(
                          activity == null
                              ? '当前没有待处理的页面提示。'
                              : activity.details?.trim().isNotEmpty == true
                              ? activity.details!
                              : activity.message,
                          key: const ValueKey(
                            'workbench-page-activity-details',
                          ),
                          style: StewardText.body,
                        ),
                      ),
                    ),
                  ),
                  const SizedBox(height: 12),
                  Align(
                    alignment: Alignment.centerRight,
                    child: StewardButton(
                      '关闭',
                      onPressed: () => Navigator.pop(context),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      );

  /// 非桌面平台使用六项底部导航。官方组件保留键盘激活、选中状态、读屏
  /// 位置和焦点样式；SafeArea由最外层统一处理，避免重复增加系统手势区高度。
  Widget _bottomNavigation() => FBottomNavigationBar(
    key: const ValueKey('workbench-bottom-navigation'),
    index: destinationOrder.indexOf(index),
    safeAreaBottom: true,
    style: context.theme.bottomNavigationBarStyle.copyWith(
      padding: const EdgeInsetsGeometryDelta.value(
        EdgeInsets.symmetric(horizontal: 4, vertical: 2),
      ),
      itemStyle: context.theme.bottomNavigationBarStyle.itemStyle.copyWith(
        padding: const EdgeInsetsGeometryDelta.value(
          EdgeInsets.symmetric(horizontal: 4, vertical: 6),
        ),
        spacing: 3,
      ),
    ),
    onChange: _resourcesBlocked
        ? null
        : (next) {
            // 底部目的地切换结束当前输入，不让隐藏页持有软键盘；controller草稿仍保留。
            FocusManager.instance.primaryFocus?.unfocus();
            _select(destinationOrder[next]);
          },
    children: [
      for (final destination in destinationOrder)
        FBottomNavigationBarItem(
          key: ValueKey('nav-$destination'),
          icon: Icon(icons[destination], size: 20),
          label: Text(
            names[destination],
            style: const TextStyle(fontSize: 11, height: 1.3),
          ),
        ),
    ],
  );

  /// Windows所有宽度保持同一窄侧栏，导航不会因窗口缩放搬到底部；设置独立停靠底部。
  /// 品牌标题不占主导航空间，系统窗口标题继续承担产品识别。
  Widget _sidebar() => Container(
    key: const ValueKey('workbench-navigation-rail'),
    width: 80,
    padding: const EdgeInsets.fromLTRB(10, 12, 10, 8),
    decoration: BoxDecoration(
      color: context.stewardPaint.surface(
        context.theme.colors.mutedForeground,
        alpha: .045,
      ),
      border: Border(right: BorderSide(color: context.theme.colors.border)),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const GamepadHint('LB'),
        // 键盘或较矮窗口压缩高度时，主要目的地独立滚动；设置仍固定在底部且可点击。
        Expanded(
          child: ListView(
            padding: EdgeInsets.zero,
            children: [
              for (final destination in destinationOrder.where((id) => id != 4))
                _navigation(destination),
            ],
          ),
        ),
        _navigation(4),
        const GamepadHint('RB'),
      ],
    ),
  );

  Widget _navigation(int destination) => Padding(
    padding: const EdgeInsets.only(bottom: 6),
    child: Semantics(
      selected: index == destination,
      child: FButton(
        key: ValueKey('nav-$destination'),
        variant: index == destination
            ? FButtonVariant.secondary
            : FButtonVariant.ghost,
        mainAxisAlignment: MainAxisAlignment.center,
        onPress: _resourcesBlocked ? null : () => _select(destination),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icons[destination], size: 18),
            const SizedBox(height: 5),
            Text(names[destination], style: const TextStyle(fontSize: 11)),
          ],
        ),
      ),
    ),
  );

  /// 断线时只能标记上次读取的 Mod 版本；从未读取则保持未知，不推断与客户端版本相同。
  String _modVersionText() {
    final raw = widget.model.snapshot['pluginVersion'];
    final version = raw is String ? raw.trim() : '';
    if (version.isEmpty) return widget.model.connected ? 'Mod 未知' : 'Mod 未连接';
    return 'Mod $version${widget.model.connected ? '' : '（上次连接）'}';
  }

  /// 持续状态固定在底部，跨页面可见；背景与内容透明度仍由外层分别管理。
  Widget _statusBar() => Container(
    key: const ValueKey('workbench-status-bar'),
    width: double.infinity,
    padding: EdgeInsets.symmetric(
      horizontal: 12,
      vertical: widget.model.desktop ? 5 : 2,
    ),
    decoration: BoxDecoration(
      border: Border(top: BorderSide(color: context.theme.colors.border)),
    ),
    child: FutureBuilder<String>(
      future: clientVersion,
      builder: (context, version) => !widget.model.desktop
          ? _mobileStatus(version.data ?? '读取中')
          : Wrap(
              spacing: 16,
              runSpacing: 3,
              children: [
                Text(
                  widget.model.connected
                      ? '● 已连接 · ${label(widget.model.snapshot['activeSceneName'])}'
                      : '○ 未连接 · 前往设置连接游戏电脑',
                  style: const TextStyle(fontSize: 11),
                ),
                Text(
                  !widget.model.connected
                      ? '设备角色：未确认'
                      : widget.model.primary
                      ? '主设备'
                      : '查看设备',
                  style: const TextStyle(fontSize: 11),
                ),
                Text(
                  '客户端 ${version.data ?? '读取中'}',
                  style: const TextStyle(fontSize: 11),
                ),
                Text(_modVersionText(), style: const TextStyle(fontSize: 11)),
                if (widget.model.busy)
                  const Text('正在处理…', style: TextStyle(fontSize: 11)),
                if (widget.model.error.isNotEmpty ||
                    widget.model.notice.isNotEmpty)
                  _operationMessage(),
              ],
            ),
    ),
  );

  /// 手机将连接、角色和版本压缩为单行；通知或错误临时占据同一行，不增加高度。
  /// 完整文字保留于按钮语义，并可打开只读详情。版本读取和连接状态不会触发写入。
  Widget _mobileStatus(String version) {
    final model = widget.model;
    final connection = model.connected
        ? '已连接 · ${label(model.snapshot['activeSceneName'])}'
        : '未连接 · 前往设置连接游戏电脑';
    final role = !model.connected
        ? '设备角色：未确认'
        : model.primary
        ? '主设备'
        : '查看设备';
    final summary =
        '${model.connected ? '● 已连接' : '○ 未连接'} · $role · 客户端 $version · ${_modVersionText()}';
    final message = model.error.isNotEmpty ? model.error : model.notice;
    final lines = [
      connection,
      role,
      '客户端 $version',
      _modVersionText(),
      if (model.busy) '正在处理…',
      if (message.isNotEmpty) message,
    ];
    return Semantics(
      container: true,
      liveRegion: message.isNotEmpty,
      child: FButton.raw(
        key: const ValueKey('mobile-status-details'),
        variant: FButtonVariant.ghost,
        semanticsLabel: '${lines.join('；')}。点击查看完整连接状态。',
        onPress: _resourcesBlocked ? null : () => _showMobileStatus(lines),
        child: ConstrainedBox(
          // 整行仍可点击；减少信息条留白，不裁剪大字号或缩小实际文字。
          constraints: const BoxConstraints(minHeight: 26),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  message.isNotEmpty
                      ? message
                      : model.busy
                      ? '正在处理… · $summary'
                      : summary,
                  key: ValueKey(
                    message.isNotEmpty
                        ? 'workbench-operation-message'
                        : 'mobile-status-summary',
                  ),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: 11,
                    color: model.error.isNotEmpty
                        ? context.theme.colors.error
                        : context.theme.colors.mutedForeground,
                  ),
                ),
              ),
              const SizedBox(width: 6),
              const Icon(FLucideIcons.info, size: 14),
            ],
          ),
        ),
      ),
    );
  }

  /// 详情只读且可滚动，长版本号和完整错误不会被摘要的省略号隐藏；关闭恢复焦点。
  Future<void> _showMobileStatus(List<String> lines) async {
    final focus = FocusManager.instance.primaryFocus;
    await showFDialog<void>(
      context: context,
      builder: (context, style, animation) => FDialog(
        animation: MediaQuery.disableAnimationsOf(context) ? null : animation,
        semanticsLabel: '连接状态',
        builder: (context, _) => ConstrainedBox(
          constraints: BoxConstraints(
            maxHeight: MediaQuery.sizeOf(context).height * .75,
          ),
          child: SingleChildScrollView(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const Text(
                    '连接状态',
                    style: TextStyle(fontSize: 18, fontWeight: FontWeight.w700),
                  ),
                  for (final line in lines)
                    Padding(
                      padding: const EdgeInsets.only(top: 8),
                      child: SelectableText(line),
                    ),
                  const SizedBox(height: 12),
                  StewardButton(
                    '关闭',
                    key: const ValueKey('mobile-status-close'),
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
    if (focus?.context != null) focus!.requestFocus();
  }

  /// 操作结果与初始化错误保持可见，但不再创建位于导航上方的横幅。
  /// 长消息由提示浮层提供全文，诊断页继续保留原始内容；错误优先于普通通知。
  /// liveRegion 使辅助阅读器能够获知保存/失败结果，不依赖颜色或鼠标悬停。
  Widget _operationMessage() {
    final error = widget.model.error.isNotEmpty;
    final message = error ? widget.model.error : widget.model.notice;
    return Semantics(
      liveRegion: true,
      child: FTooltip(
        tipBuilder: (context, controller) => Text(message),
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 480),
          child: Text(
            message,
            key: const ValueKey('workbench-operation-message'),
            maxLines: error ? 3 : 1,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(
              fontSize: 11,
              color: error
                  ? context.theme.colors.error
                  : context.theme.colors.mutedForeground,
            ),
          ),
        ),
      ),
    );
  }
}
