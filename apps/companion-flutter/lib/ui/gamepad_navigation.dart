import 'package:flutter/gestures.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import 'steward_opacity.dart';

/// 工作台的手柄导航协调器，只管理输入方式和当前可操作的横向栏，不保存业务状态。
/// LB/RB 仍由壳层切换主页面；LT/RT 在这里恰好分派给一个栏，避免多层页签同时跳转。
class GamepadNavigationController extends ChangeNotifier {
  GamepadNavigationController() {
    FocusManager.instance.addListener(_refreshLater);
  }

  final _regions = <_GamepadTabRegionState>[];
  final _navigationBlockers = <Object>{};
  bool _active = false;
  bool _closed = false;
  bool _refreshQueued = false;
  bool get active => _active;
  bool get navigationBlocked => _navigationBlockers.isNotEmpty;

  /// 不抢焦点的下拉也具有自己的操作上下文。由弹层实际开关登记，而非猜测
  /// 当前焦点是否已进入搜索框；多个弹层各自持有 owner，不能互相提前解除阻断。
  void setNavigationBlocked(Object owner, bool blocked) {
    if (_closed) return;
    final changed = blocked
        ? _navigationBlockers.add(owner)
        : _navigationBlockers.remove(owner);
    if (changed) _refreshLater();
  }

  /// 返回 true 表示事件已由横向栏消费或明确忽略，壳层不再把它解释成滚动。
  /// 非手柄的业务通知不会改变模式；鼠标和键盘退出由 Scope 的真实输入监听负责。
  bool handleAction(String action) {
    if (_closed) return true;
    if (action == 'gamepadInactive') {
      useDirectInput();
      return true;
    }
    const actions = {
      'up',
      'down',
      'left',
      'right',
      'activate',
      'back',
      'favorite',
      'previousTab',
      'nextTab',
      'previousSubTab',
      'nextSubTab',
    };
    if (!actions.contains(action)) return false;
    if (!_active) {
      _active = true;
      _refreshLater();
    }
    if (navigationBlocked &&
        const {
          'previousTab',
          'nextTab',
          'previousSubTab',
          'nextSubTab',
        }.contains(action))
      return true;
    if (action != 'previousSubTab' && action != 'nextSubTab') return false;
    // 每次按下重新检查焦点、可见性、启用状态与路由。弹窗打开期间既不回落到
    // 被遮挡的主栏，也不保留一个可在弹窗关闭后重放的切换请求。
    _target?.widget.onStep(action == 'nextSubTab' ? 1 : -1);
    return true;
  }

  void useDirectInput() {
    if (_closed || !_active) return;
    _active = false;
    _refreshLater();
  }

  _GamepadTabRegionState? get _target {
    if (navigationBlocked) return null;
    final available = _regions.where((region) => region._eligible).toList();
    for (final region in available) {
      if (region._focus.hasFocus) return region;
    }
    // 注册顺序对应页面结构顺序。优先最外层主导航；搜索工具栏的料理/酒水
    // 等次级栏只在获得焦点时接管，或者页面确实没有主栏时作为备用。
    return available.where((region) => region.widget.primary).firstOrNull ??
        available.firstOrNull;
  }

  void _register(_GamepadTabRegionState region) {
    if (_closed || _regions.contains(region)) return;
    _regions.add(region);
    _refreshLater();
  }

  void _unregister(_GamepadTabRegionState region) {
    _regions.remove(region);
    _refreshLater();
  }

  void _refreshLater() {
    if (_closed || _refreshQueued) return;
    _refreshQueued = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _refreshQueued = false;
      if (!_closed) notifyListeners();
    });
    WidgetsBinding.instance.ensureVisualUpdate();
  }

  @override
  void dispose() {
    _closed = true;
    FocusManager.instance.removeListener(_refreshLater);
    _regions.clear();
    _navigationBlockers.clear();
    super.dispose();
  }
}

/// 壳层只需包裹此 Scope。全局输入路由也覆盖弹窗和 Overlay：用户在弹窗内
/// 改用鼠标/键盘后不会残留手柄提示。仅监听真实输入，不拦截事件或修改焦点。
class GamepadNavigationScope extends StatefulWidget {
  const GamepadNavigationScope({
    required this.controller,
    required this.child,
    super.key,
  });
  final GamepadNavigationController controller;
  final Widget child;

  static GamepadNavigationController? maybeOf(BuildContext context) => context
      .dependOnInheritedWidgetOfExactType<_GamepadNavigationInherited>()
      ?.notifier;

  @override
  State<GamepadNavigationScope> createState() => _GamepadNavigationScopeState();
}

class _GamepadNavigationScopeState extends State<GamepadNavigationScope> {
  @override
  void initState() {
    super.initState();
    GestureBinding.instance.pointerRouter.addGlobalRoute(_pointer);
    HardwareKeyboard.instance.addHandler(_keyboard);
  }

  void _pointer(PointerEvent event) {
    if (event is PointerDownEvent ||
        event is PointerSignalEvent ||
        (event is PointerHoverEvent || event is PointerMoveEvent) &&
            event.delta != Offset.zero) {
      widget.controller.useDirectInput();
    }
  }

  bool _keyboard(KeyEvent event) {
    if (event is KeyDownEvent || event is KeyRepeatEvent) {
      widget.controller.useDirectInput();
    }
    return false;
  }

  @override
  void dispose() {
    GestureBinding.instance.pointerRouter.removeGlobalRoute(_pointer);
    HardwareKeyboard.instance.removeHandler(_keyboard);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => _GamepadNavigationInherited(
    notifier: widget.controller,
    child: widget.child,
  );
}

class _GamepadNavigationInherited
    extends InheritedNotifier<GamepadNavigationController> {
  const _GamepadNavigationInherited({
    required super.notifier,
    required super.child,
  });
}

/// WorkspaceTabs 的注册边界；不新增可 Tab 聚焦的节点，保留内部官方按钮的
/// 键盘/读屏语义。独立组件没有 Scope 时保持原布局和行为。
class GamepadTabRegion extends StatefulWidget {
  const GamepadTabRegion({
    required this.enabled,
    required this.primary,
    required this.onStep,
    required this.builder,
    super.key,
  });
  final bool enabled;
  final bool primary;
  final ValueChanged<int> onStep;
  final Widget Function(BuildContext context, bool showHints) builder;

  @override
  State<GamepadTabRegion> createState() => _GamepadTabRegionState();
}

class _GamepadTabRegionState extends State<GamepadTabRegion> {
  final _focus = FocusNode(skipTraversal: true, canRequestFocus: false);
  GamepadNavigationController? _controller;
  bool _visible = true;
  ModalRoute<dynamic>? _route;

  bool get _eligible {
    if (!mounted ||
        !_visible ||
        !widget.enabled ||
        (_route != null && !_route!.isCurrent))
      return false;
    // Forui 下拉弹层通过 Overlay/独立 FocusScope 呈现，不一定创建新路由。
    // 临时输入域获得焦点后不能切换其后的页面；普通搜索与页签共享祖先域，
    // 仍可按既定主栏回落规则切换。父级路由尚未分配具体焦点时也保持可用。
    final focused = FocusManager.instance.primaryFocus;
    final scope = focused is FocusScopeNode ? focused : focused?.enclosingScope;
    if (scope != null && !_focus.ancestors.contains(scope)) return false;
    final render = context.findRenderObject();
    return render is RenderBox &&
        render.attached &&
        render.hasSize &&
        render.size.width > 0 &&
        render.size.height > 0;
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _visible = TickerMode.valuesOf(context).enabled;
    _route = ModalRoute.of(context);
    final next = GamepadNavigationScope.maybeOf(context);
    if (!identical(next, _controller)) {
      _controller?._unregister(this);
      _controller = next;
      _controller?._register(this);
    }
  }

  @override
  void didUpdateWidget(covariant GamepadTabRegion oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.enabled != widget.enabled ||
        oldWidget.primary != widget.primary) {
      _controller?._refreshLater();
    }
  }

  @override
  void dispose() {
    _controller?._unregister(this);
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Focus(
    focusNode: _focus,
    child: widget.builder(
      context,
      _controller?.active == true && identical(_controller?._target, this),
    ),
  );
}

/// 轻量实体按键提示，没有点击行为；使用文字而非颜色说明操作。
/// LT/RT 由唯一激活栏放置，LB/RB 可由壳层放在主导航的两端。
class GamepadHint extends StatelessWidget {
  const GamepadHint(this.text, {super.key});
  final String text;

  @override
  Widget build(BuildContext context) {
    final controller = GamepadNavigationScope.maybeOf(context);
    if (controller == null ||
        !controller.active ||
        controller.navigationBlocked) {
      return const SizedBox.shrink();
    }
    final colors = context.theme.colors;
    return Semantics(
      label: '手柄 $text',
      child: ExcludeSemantics(
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 3, vertical: 2),
          decoration: BoxDecoration(
            color: context.stewardPaint.surface(colors.secondary),
            border: Border.all(
              color: context.stewardPaint.foreground(colors.border),
            ),
            borderRadius: BorderRadius.circular(4),
          ),
          child: Text(
            text,
            style: TextStyle(fontSize: 10, color: colors.foreground),
          ),
        ),
      ),
    );
  }
}
