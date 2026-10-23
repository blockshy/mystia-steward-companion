import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import 'gamepad_navigation.dart';
import 'steward_controls.dart';

/// 单选控件及其弹层的统一目标宽度，推荐、预算、评分方向和动态标签共用。
/// 三个控件加两段8px间距共472px，可放入Windows最窄窗口扣除侧栏和页边距后的
/// 正文；更窄的手机或设置控制列仍由父约束收紧，不缩小字体或点击高度。
const double stewardSelectWidth = 152;

/// 工作台统一单选控件：普通选择和可搜索选择均使用 Forui 原生下拉弹层。
///
/// [items] 按「值 → 显示名称」传入，以免同名客人覆盖不同 ID。宽度由父布局
/// 约束，不固定控件高度；弹层沿用视口和软键盘避让，长列表在内部滚动。
/// [boundary] 应传不可变的连接、页面或权限边界；边界、选项或当前值变化时，
/// 已打开的选择会失效，不能把旧目录中的项目提交给新的业务上下文。
class StewardSelect<T> extends StatefulWidget {
  const StewardSelect({
    required this.label,
    required this.items,
    required this.value,
    required this.onChanged,
    this.enabled = true,
    this.searchable = false,
    this.showLabel = true,
    this.hint,
    this.searchHint,
    this.formatSelected,
    this.itemLeading,
    this.popoverWidth,
    this.wrapItems = false,
    this.boundary,
    super.key,
  }) : assert(
         popoverWidth == null ||
             popoverWidth > 0 && popoverWidth < double.infinity,
       );

  final String label;
  final Map<T, String> items;
  final T? value;
  final ValueChanged<T> onChanged;
  final bool enabled, searchable, showLabel;
  final String? hint, searchHint;

  /// 仅修饰输入框中的已选名称，不改变下拉项目或搜索匹配。例如“料理：鲜”。
  final String Function(String label)? formatSelected;

  /// 项目图像只按明确的选项值提供；不参与搜索、选择代次或表单控制器状态。
  final Widget Function(T value)? itemLeading;

  /// 可选的弹层目标宽度，不改变输入框宽度。长网卡名称等需要完整辨认的项目
  /// 可以使用更宽弹层；最终仍受当前视口安全边界约束，并由 Forui 相对锚点避让。
  /// 未指定时严格保留原有 152px/窄锚点规则，普通业务选择器无需额外配置。
  final double? popoverWidth;

  /// 仅控制候选项是否完整换行，已选输入仍保持标准单行几何。默认沿用 Forui
  /// 省略行为；启用后不缩小字体、不限制行数，长项目通过弹层原有滚动区可达。
  final bool wrapItems;
  final Object? boundary;

  @override
  State<StewardSelect<T>> createState() => _StewardSelectState<T>();
}

class _StewardSelectState<T> extends State<StewardSelect<T>> {
  bool _shown = false;
  int _revision = 0;
  int? _openingRevision;
  late Map<T, String> _items = Map.of(widget.items);
  final _focus = FocusNode(debugLabel: 'StewardSelect');
  final _navigationOwner = Object();
  GamepadNavigationController? _navigation;
  bool _visible = true;
  late final _controller = _ActivationSelectController<T>(
    value: _items.containsKey(widget.value) ? widget.value : null,
    onActivated: (value) => _accept(value, _revision),
  );

  bool get _enabled => widget.enabled && _items.isNotEmpty;

  /// 触屏下拉默认不自动聚焦搜索，普通下拉甚至没有搜索框。弹层一经打开就
  /// 阻断背后的主/次导航，不能等用户输入后再依赖焦点域判断；隐藏与销毁时
  /// 撤回自己的 owner，避免保留页面或失效目录把其它页面永久锁住。
  void _syncNavigationBlocker() =>
      _navigation?.setNavigationBlocked(_navigationOwner, _shown && _visible);

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final next = GamepadNavigationScope.maybeOf(context);
    if (!identical(next, _navigation)) {
      _navigation?.setNavigationBlocked(_navigationOwner, false);
      _navigation = next;
    }
    _visible = TickerMode.valuesOf(context).enabled;
    _syncNavigationBlocker();
  }

  @override
  void didUpdateWidget(covariant StewardSelect<T> oldWidget) {
    super.didUpdateWidget(oldWidget);
    // 保存选项快照而不是旧 Map 引用，也能识别调用方原位更新目录的情况。
    // 失效先于下一帧弹层动画完成；残留事件仍会经过 _accept 的代次检查。
    if (oldWidget.boundary != widget.boundary ||
        oldWidget.enabled != widget.enabled ||
        oldWidget.value != widget.value ||
        oldWidget.searchable != widget.searchable ||
        !mapEquals(_items, widget.items)) {
      _revision++;
      _shown = false;
      _openingRevision = null;
    }
    _items = Map.of(widget.items);
    _controller.synchronize(
      _items.containsKey(widget.value) ? widget.value : null,
    );
    _syncNavigationBlocker();
  }

  void _show(bool shown) {
    if (!mounted || shown && !_enabled) return;
    setState(() {
      _shown = shown;
      _openingRevision = shown ? _revision : null;
    });
    _syncNavigationBlocker();
  }

  void _accept(T? value, int revision) {
    if (!mounted ||
        !_enabled ||
        !_shown ||
        _openingRevision != revision ||
        revision != _revision ||
        value == null ||
        !_items.containsKey(value))
      return;
    // 禁用 FSelect 默认的「先等待关闭动画，再调用业务回调」流程。
    // 立即校验并提交，再由受控弹层关闭，避免动画期间换连接产生迟到写入。
    setState(() {
      _shown = false;
      _openingRevision = null;
    });
    _syncNavigationBlocker();
    // 保留原生选择器的焦点语义：桌面继续键盘导航，触屏收起搜索软键盘。
    switch (defaultTargetPlatform) {
      case TargetPlatform.windows ||
          TargetPlatform.macOS ||
          TargetPlatform.linux:
        _focus.requestFocus();
      default:
        _focus.unfocus();
        FocusManager.instance.primaryFocus?.unfocus();
    }
    // 再次激活当前项仍关闭弹层，但不产生重复配置保存或推荐条件更新。
    if (value != widget.value) widget.onChanged(value);
  }

  @override
  void dispose() {
    _navigation?.setNavigationBlocked(_navigationOwner, false);
    _focus.dispose();
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) => _buildField(context, constraints),
  );

  Widget _buildField(BuildContext context, BoxConstraints constraints) {
    final options = Map<T, String>.unmodifiable(_items);
    final control = FSelectControl<T>.managed(controller: _controller);
    final popover = FPopoverControl.lifted(shown: _shown, onChange: _show);
    // 纯文字弹层跟随较窄锚点。带图片项目保留标准阅读宽度：三列筛选在窄窗口
    // 会将锚点压到约82px，扣除Forui项目内边距与选中标记后，150%图片连同间距
    // 已经放不下，更不能为文字留下空间。弹层可以比锚点宽，图像与字号无需缩小。
    // 视口、键盘避让和长列表滚动仍交给Forui；这里只改变布局，不重建选择代次。
    final defaultContentWidth = widget.itemLeading != null
        ? stewardSelectWidth
        : math.min(
            stewardSelectWidth,
            constraints.hasBoundedWidth
                ? constraints.maxWidth
                : stewardSelectWidth,
          );
    // 显式宽弹层可以超出较窄的输入锚点，但不能超出窗口。直接读取 View 的
    // 安全边界，避免外层 SafeArea 已消费 MediaQuery.padding 后低估系统遮挡；
    // 左右各留 8px，与 Forui 的锚点翻转/平移配合，而不是强行移动表单本身。
    final view = View.of(context);
    final viewportWidth = MediaQuery.sizeOf(context).width;
    final insets = MediaQuery.viewInsetsOf(context);
    final availableWidth = math.max(
      1.0,
      viewportWidth -
          math.max(view.viewPadding.left / view.devicePixelRatio, insets.left) -
          math.max(
            view.viewPadding.right / view.devicePixelRatio,
            insets.right,
          ) -
          16,
    );
    final contentWidth = widget.popoverWidth == null
        ? defaultContentWidth
        : math.min(widget.popoverWidth!, availableWidth);
    final contentConstraints = FPortalConstraints(
      minWidth: contentWidth,
      maxWidth: contentWidth,
      maxHeight: 300,
    );
    String format(T value) {
      final name = options[value];
      if (name == null) return widget.hint ?? widget.label;
      return widget.formatSelected?.call(name) ?? name;
    }

    Widget empty(BuildContext context, FSelectStyle style) => Padding(
      padding: const EdgeInsets.all(12),
      child: Text(options.isEmpty ? '暂无可选项' : '没有匹配项'),
    );
    Widget itemText(String name) => Text(
      name,
      softWrap: widget.wrapItems ? true : null,
      overflow: widget.wrapItems ? TextOverflow.visible : null,
    );
    Widget itemTitle(T value, String name) => widget.itemLeading == null
        ? itemText(name)
        : Row(
            children: [
              widget.itemLeading!(value),
              const SizedBox(width: 6),
              Flexible(child: itemText(name)),
            ],
          );
    final field = widget.searchable
        ? FSelect<T>.searchBuilder(
            // Forui 搜索结果缓存随弹层存活；条件失效时销毁整棵旧搜索子树，
            // 避免旧 values 在新目录上重新解析，也清空旧条件的搜索草稿。
            key: ValueKey(_revision),
            control: control,
            style: stewardSelectStyle(context),
            textAlignVertical: TextAlignVertical.center,
            popoverControl: popover,
            contentConstraints: contentConstraints,
            focusNode: _focus,
            format: format,
            label: widget.showLabel ? Text(widget.label) : null,
            hint: widget.hint ?? widget.label,
            enabled: _enabled,
            autoHide: false,
            searchFieldProperties: FSelectSearchFieldProperties(
              hint: widget.searchHint ?? '搜索${widget.label}',
              textAlignVertical: TextAlignVertical.center,
            ),
            // 本地目录仅作名称包含匹配；不改变查询条件，也不计算推荐业务。
            filter: (query) {
              final needle = query.trim().toLowerCase();
              return options.entries
                  .where((entry) => entry.value.toLowerCase().contains(needle))
                  .map((entry) => entry.key);
            },
            contentBuilder: (context, query, values) => [
              for (final value in values)
                if (options[value] case final name?)
                  FSelectItem<T>(title: itemTitle(value, name), value: value),
            ],
            contentEmptyBuilder: empty,
          )
        : FSelect<T>.rich(
            key: ValueKey(_revision),
            control: control,
            style: stewardSelectStyle(context),
            textAlignVertical: TextAlignVertical.center,
            popoverControl: popover,
            contentConstraints: contentConstraints,
            focusNode: _focus,
            format: format,
            label: widget.showLabel ? Text(widget.label) : null,
            hint: widget.hint ?? widget.label,
            enabled: _enabled,
            autoHide: false,
            children: [
              for (final entry in options.entries)
                FSelectItem<T>(
                  title: itemTitle(entry.key, entry.value),
                  value: entry.key,
                ),
            ],
            contentEmptyBuilder: empty,
          );
    return widget.showLabel
        ? field
        : Semantics(label: widget.label, child: field);
  }
}

/// 使用公开控制器截获“激活项目”，而非只观察“值改变”。Forui 的 lifted
/// 控制器会忽略同值激活，因此不足以实现「选中当前项也关闭」的标准下拉交互。
/// 用户输入只交给边界检查；真实值始终由父组件回传，保留受控表单的单一来源。
class _ActivationSelectController<T> extends FSelectController<T> {
  _ActivationSelectController({
    required super.value,
    required this.onActivated,
  });
  final ValueChanged<T?> onActivated;

  @override
  set value(T? value) => onActivated(value);

  void synchronize(T? value) => super.value = value;
}
