import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter/foundation.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import 'gamepad_navigation.dart';
import 'semantic_tag.dart';
import 'steward_opacity.dart';
import 'steward_style.dart';
import 'steward_controls.dart';

export 'steward_controls.dart' show stewardControlHeight;

/// 手柄 X 的语义操作，由当前聚焦业务行实现；没有收藏能力的行不响应。
class FavoriteIntent extends Intent {
  const FavoriteIntent();
}

/// 用户确认只授权打开弹窗时的连接、场景和设备权威。等待期间发生切主、重连或换场景，
/// 必须重新发起操作，不能把旧确认重新绑定到后来出现的订单、库存或更新目标。
Future<bool> confirmCurrentAction(
  BuildContext context,
  WorkbenchModel model,
  String title,
  String message,
) async {
  List<Object?> boundary() => [
    model.connectionVersion,
    model.connected,
    model.snapshot['automationSessionId'],
    model.snapshot['nightBusinessGeneration'],
    model.snapshot['runtimeDaySceneGeneration'],
    model.authority['registryId'],
    model.authority['authorityRevision'],
  ];
  final expected = boundary();
  if (!await confirmAction(context, title, message)) return false;
  if (listEquals(expected, boundary())) return true;
  if (context.mounted)
    await showFDialog<void>(
      context: context,
      builder: (context, style, animation) => FDialog(
        animation: MediaQuery.disableAnimationsOf(context) ? null : animation,
        builder: (context, _) => Padding(
          padding: const EdgeInsets.all(20),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const Text(
                '状态已变化',
                style: TextStyle(fontSize: 18, fontWeight: FontWeight.w700),
              ),
              const SizedBox(height: 12),
              const Text('连接、场景或主设备已变化，本次操作没有发送。请核对当前信息后重新操作。'),
              const SizedBox(height: 16),
              ToolRow([
                StewardButton('知道了', onPressed: () => Navigator.pop(context)),
              ]),
            ],
          ),
        ),
      ),
    );
  return false;
}

/// IndexedStack 会保留隐藏页；资源页统一先在构建期撤销旧订阅，再于帧结束注册新读取。
/// 经营、推荐、扩展和设置遵循同一顺序，避免旧页失活取消新页刚开始的请求。
/// 延迟回调只属于安排时的模型及连接；隐藏、重连或替换模型后不能偷偷恢复旧轮询。
mixin ActiveResourcePage<T extends StatefulWidget> on State<T> {
  bool get resourcePageActive;
  WorkbenchModel get resourceModel;
  void loadActiveResource();
  bool _previousActive = false, _previousConnected = false;
  WorkbenchModel? _previousResourceModel;
  int? _previousConnectionVersion;
  Object? _previousResourceBoundary;
  int _resourceActivationGeneration = 0;
  Object _resourceBoundary(WorkbenchModel model) => (
    model.snapshot['automationSessionId'],
    model.snapshot['runtimeDaySceneGeneration'],
    model.snapshot['nightBusinessGeneration'],
  );
  void synchronizeResourceActivation() {
    final model = resourceModel;
    final active = resourcePageActive, connected = model.connected;
    final changedModel = !identical(model, _previousResourceModel);
    final changedConnection =
        model.connectionVersion != _previousConnectionVersion;
    final boundary = _resourceBoundary(model);
    final changedBoundary = boundary != _previousResourceBoundary;
    if (_previousActive && (!active || changedModel)) {
      _previousResourceModel?.clearResourceSubscription();
    }
    final shouldLoad =
        active &&
        connected &&
        (!_previousActive ||
            !_previousConnected ||
            changedModel ||
            changedConnection ||
            changedBoundary);
    if (active != _previousActive ||
        connected != _previousConnected ||
        changedModel ||
        changedConnection ||
        changedBoundary)
      _resourceActivationGeneration++;
    _previousActive = active;
    _previousConnected = connected;
    _previousResourceModel = model;
    _previousConnectionVersion = model.connectionVersion;
    _previousResourceBoundary = boundary;
    if (shouldLoad) {
      final generation = _resourceActivationGeneration;
      final connection = model.connectionVersion;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted &&
            generation == _resourceActivationGeneration &&
            resourcePageActive &&
            identical(resourceModel, model) &&
            model.connected &&
            model.connectionVersion == connection &&
            _resourceBoundary(model) == boundary)
          loadActiveResource();
      });
    }
  }
}

/// 危险或跨设备操作先展示精确对象；对话框关闭后恢复原控件焦点。
Future<bool> confirmAction(
  BuildContext context,
  String title,
  String message,
) async {
  final focus = FocusManager.instance.primaryFocus;
  final result = await showFDialog<bool>(
    context: context,
    builder: (context, style, animation) => FDialog(
      animation: MediaQuery.disableAnimationsOf(context) ? null : animation,
      semanticsLabel: title,
      builder: (context, _) => Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              title,
              style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 12),
            Text(message),
            const SizedBox(height: 16),
            ToolRow([
              StewardButton(
                '取消',
                onPressed: () => Navigator.pop(context, false),
              ),
              StewardButton(
                '确认',
                primary: true,
                onPressed: () => Navigator.pop(context, true),
              ),
            ]),
          ],
        ),
      ),
    ),
  );
  if (focus?.context != null) focus!.requestFocus();
  return result == true;
}

/// 项目按钮统一桌面密度、触屏目标尺寸与语义；异步错误由应用控制器展示。
class StewardButton extends StatelessWidget {
  const StewardButton(
    this.text, {
    this.onPressed,
    this.primary = false,
    this.icon,
    super.key,
  });
  final String text;
  final VoidCallback? onPressed;
  final bool primary;
  final IconData? icon;
  @override
  Widget build(BuildContext context) => FButton(
    variant: primary ? FButtonVariant.primary : FButtonVariant.outline,
    style: stewardButtonStyle(context),
    mainAxisSize: MainAxisSize.min,
    onPress: onPressed,
    prefix: icon == null ? null : Icon(icon, size: 15),
    child: Flexible(child: Text(text)),
  );
}

/// 工具栏纯图标操作共用方形框体和明确语义，禁止页面自行拼装不同宽高的刷新按钮。
class StewardIconButton extends StatelessWidget {
  const StewardIconButton({
    required this.label,
    required this.icon,
    this.onPressed,
    super.key,
  });
  final String label;
  final IconData icon;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) => FTooltip(
    tipBuilder: (context, controller) => Text(label),
    child: FButton.icon(
      variant: FButtonVariant.ghost,
      style: stewardIconButtonStyle(context),
      semanticsLabel: label,
      semanticsTooltip: label,
      onPress: onPressed,
      child: Icon(icon, size: 16),
    ),
  );
}

/// 表单保留 Flutter 原生输入控制器和 composing 区间，轮询刷新不覆盖中文输入。
class StewardField extends StatelessWidget {
  const StewardField(
    this.title, {
    this.controller,
    this.initial = '',
    this.onChanged,
    this.secret = false,
    this.numeric = false,
    this.showLabel = true,
    this.hint,
    super.key,
  });
  final String title;
  final TextEditingController? controller;
  final String initial;
  final ValueChanged<String>? onChanged;
  final bool secret;
  final bool numeric;

  /// 搜索/格式说明可放进输入框；隐藏视觉标题后仍保留非空 title 作为无障碍名称。
  final bool showLabel;
  final String? hint;
  @override
  Widget build(BuildContext context) => Semantics(
    label: !showLabel && title.isNotEmpty ? title : null,
    child: FTextField(
      // 空字符串不是“没有标签”：Text('') 仍占一行高度，必须使用 null。
      label: showLabel && title.isNotEmpty ? Text(title) : null,
      hint: hint,
      style: stewardFieldStyle(context),
      textAlignVertical: TextAlignVertical.center,
      control: FTextFieldControl.managed(
        controller: controller,
        initial: controller == null ? TextEditingValue(text: initial) : null,
        onChange: (value) => onChanged?.call(value.text),
      ),
      obscureText: secret,
      keyboardType: numeric ? TextInputType.number : TextInputType.text,
      autocorrect: !secret && !numeric,
    ),
  );
}

/// 可换行工具条供 390 px 视口使用，不通过缩小文字强行塞入操作。
class ToolRow extends StatelessWidget {
  const ToolRow(this.children, {this.alignInputs = false, super.key});
  final List<Widget> children;

  /// 仅用于没有外置标签的输入工具栏；状态文字/选择器也居中到同一控制区。
  final bool alignInputs;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 6),
    child: Wrap(
      spacing: StewardDensity.toolbarGap,
      runSpacing: StewardDensity.toolbarGap,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        for (final child in children)
          if (alignInputs)
            ConstrainedBox(
              constraints: BoxConstraints(
                minHeight: stewardControlHeight(context),
              ),
              child: Align(widthFactor: 1, heightFactor: 1, child: child),
            )
          else
            child,
      ],
    ),
  );
}

/// 内容分组使用浅色标题带、圆角边界和少量留白区分信息层级。
/// 卡片底色仅叠加低透明度色层；真正背景仍由工作台绘制，Windows透明度可继续透出桌面。
class Section extends StatelessWidget {
  const Section(
    this.title,
    this.children, {
    this.titleLeading,
    this.titleContent,
    super.key,
  });
  final String title;
  final List<Widget> children;

  /// 实体分组可用同尺寸小图代替装饰条；默认分组保持原有外观和布局。
  final Widget? titleLeading;

  /// 料理等已有共用图文名称的区域可直接提供完整标题；仍保留本组件的header语义，
  /// 避免名称在外层走实体图片、详情又退回纯文字。未提供时维持原有标题排版。
  final Widget? titleContent;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 14),
    child: DecoratedBox(
      decoration: BoxDecoration(
        color: context.stewardPaint.surface(
          context.theme.colors.mutedForeground,
          alpha: .025,
        ),
        border: Border.all(
          color: context.stewardPaint.foreground(
            context.theme.colors.border,
            alpha: .75,
          ),
        ),
        borderRadius: BorderRadius.circular(8),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          DecoratedBox(
            decoration: BoxDecoration(
              color: context.stewardPaint.surface(
                context.theme.colors.primary,
                alpha: .055,
              ),
              borderRadius: const BorderRadius.vertical(
                top: Radius.circular(8),
              ),
              border: Border(
                bottom: BorderSide(
                  color: context.stewardPaint.foreground(
                    context.theme.colors.border,
                    alpha: .55,
                  ),
                ),
              ),
            ),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
              child: titleContent != null
                  ? Semantics(header: true, child: titleContent!)
                  : StewardBaselineRow(
                      children: [
                        StewardFirstLineCenter(
                          textStyle: StewardText.sectionTitle,
                          referenceText: title,
                          child: titleLeading == null
                              ? Container(
                                  width: 3,
                                  height: 18,
                                  margin: const EdgeInsets.only(right: 9),
                                  decoration: BoxDecoration(
                                    color: context.stewardPaint.foreground(
                                      context.theme.colors.primary,
                                      alpha: .7,
                                    ),
                                    borderRadius: BorderRadius.circular(2),
                                  ),
                                )
                              : Padding(
                                  padding: const EdgeInsets.only(right: 6),
                                  child: titleLeading!,
                                ),
                        ),
                        Expanded(
                          child: Semantics(
                            header: true,
                            child: Text(
                              title,
                              style: StewardText.sectionTitle.copyWith(
                                color: context.theme.colors.primary,
                              ),
                            ),
                          ),
                        ),
                      ],
                    ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.all(12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: children,
            ),
          ),
        ],
      ),
    ),
  );
}

/// 订单信息的视觉语义固定在公共组件中，避免各页为相同食材或酒水任意选色。
enum OrderTagKind {
  recipe,
  ingredient,
  extra,
  cooker,
  beverage,
  request,
  warning,
  success,
}

/// 小标签以图标、文字和色调共同区分信息；不表达按钮语义，也不改变业务值。
/// 宽度受父布局约束、长中文自然换行，不锁定高度，适配150%字号及窄侧栏后的内容区。
class OrderTag extends StatelessWidget {
  const OrderTag(this.text, {required this.kind, this.leading, super.key});
  final String text;
  final OrderTagKind kind;

  /// 实体图片只替换既有图标位置；标签语义、文字和命中几何均维持原规范。
  final Widget? leading;

  @override
  Widget build(BuildContext context) {
    final dark = context.theme.colors.background.computeLuminance() < .3;
    final (name, icon, light, night) = switch (kind) {
      OrderTagKind.recipe => (
        '料理',
        FLucideIcons.utensils,
        0xff8b4328,
        0xffffba94,
      ),
      OrderTagKind.ingredient => (
        '基础食材',
        FLucideIcons.leaf,
        0xff416336,
        0xffa6d291,
      ),
      OrderTagKind.extra => ('追加食材', FLucideIcons.plus, 0xff71538b, 0xffd1baec),
      OrderTagKind.cooker => (
        '厨具',
        FLucideIcons.cookingPot,
        0xff4f6377,
        0xffb1cbe4,
      ),
      OrderTagKind.beverage => (
        '酒水',
        FLucideIcons.glassWater,
        0xff276a74,
        0xff95d5d4,
      ),
      OrderTagKind.request => (
        '点单',
        FLucideIcons.listChecks,
        0xff805d25,
        0xffe5cb91,
      ),
      OrderTagKind.warning => (
        '注意',
        FLucideIcons.triangleAlert,
        0xff965230,
        0xffffc195,
      ),
      OrderTagKind.success => (
        '状态',
        FLucideIcons.circleCheck,
        0xff30694e,
        0xff9fddbb,
      ),
    };
    final tone = Color(dark ? night : light);
    return SemanticTag(
      text: text,
      semanticLabel: '$name：$text',
      icon: icon,
      tone: tone,
      leading: leading,
    );
  }
}

/// 页内导航与业务按钮分开：桌面保留完整文字换行，手机横向滚动避免占用多行正文。
class WorkspaceTabs<T> extends StatefulWidget {
  const WorkspaceTabs({
    required this.items,
    required this.value,
    required this.onChanged,
    this.inToolbar = false,
    this.horizontalScroll = false,
    this.enabled = true,
    this.disabledItems = const {},
    super.key,
  });
  final Map<T, String> items;
  final T value;
  final ValueChanged<T> onChanged;

  /// 禁用状态同时约束鼠标、键盘和扳机；不能只把按钮画灰而仍从手柄路径切页。
  final bool enabled;
  final Set<T> disabledItems;

  /// 嵌入查询工具栏时取消页级导航的下方留白，并与输入/操作按钮保持等高。
  final bool inToolbar;

  /// 由页面的真实平台能力传入，不能按宽度猜测；窄 Windows 窗口仍使用原布局。
  final bool horizontalScroll;

  @override
  State<WorkspaceTabs<T>> createState() => _WorkspaceTabsState<T>();
}

class _WorkspaceTabsState<T> extends State<WorkspaceTabs<T>> {
  final _scroll = ScrollController();
  final _tabKeys = <T, GlobalKey>{};
  double? _viewportWidth;
  (double, double, TextDirection)? _textGeometry;
  bool _revealQueued = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    // 根主题会随模型通知重新创建；只能比较影响几何的值，不能把普通轮询误当作
    // 需要重新定位的事件，否则用户刚横滚浏览后就会被下一次状态刷新拉回。
    final geometry = (
      MediaQuery.textScalerOf(context).scale(14),
      stewardControlHeight(context),
      Directionality.of(context),
    );
    if (_textGeometry != geometry) {
      _textGeometry = geometry;
      _revealSelected();
    }
  }

  @override
  void didUpdateWidget(covariant WorkspaceTabs<T> oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.value != widget.value ||
        oldWidget.horizontalScroll != widget.horizontalScroll ||
        !mapEquals(oldWidget.items, widget.items)) {
      _revealSelected();
    }
  }

  /// 只在选择或几何发生变化后定位，不在普通轮询刷新时抢回用户手动横滚的位置。
  /// 直接操作本组件的横向 position，避免通用 ensureVisible 顺带滚动外层表单。
  void _revealSelected() {
    if (_revealQueued || !widget.horizontalScroll) return;
    _revealQueued = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _revealQueued = false;
      if (!mounted || !widget.horizontalScroll || !_scroll.hasClients) return;
      final target = _tabKeys[widget.value]?.currentContext?.findRenderObject();
      if (target == null || !target.attached) return;
      unawaited(_scroll.position.ensureVisible(target, alignment: .5));
    });
  }

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  void _stepTab(int direction) {
    if (!widget.enabled) return;
    final selectable = widget.items.keys
        .where((item) => !widget.disabledItems.contains(item))
        .toList();
    if (selectable.length < 2) return;
    final current = selectable.indexOf(widget.value);
    final next = current < 0
        ? direction > 0
              ? 0
              : selectable.length - 1
        : (current + direction + selectable.length) % selectable.length;
    widget.onChanged(selectable[next]);
  }

  @override
  Widget build(BuildContext context) {
    _tabKeys.removeWhere((key, _) => !widget.items.containsKey(key));
    final tabs = <Widget>[
      for (final entry in widget.items.entries)
        Semantics(
          key: _tabKeys.putIfAbsent(entry.key, GlobalKey.new),
          selected: entry.key == widget.value,
          child: DecoratedBox(
            // 导航标记必须在按钮之后绘制；ghost 按钮的悬停/按下背景会覆盖后景边框。
            // 前景只增加绘制层，不改变控制区尺寸、命中范围或键盘焦点轮廓。
            position: DecorationPosition.foreground,
            decoration: BoxDecoration(
              border: Border(
                bottom: BorderSide(
                  width: 2,
                  color: entry.key == widget.value
                      ? context.stewardPaint.foreground(
                          context.theme.colors.primary,
                        )
                      : const Color(0x00000000),
                ),
              ),
            ),
            child: ConstrainedBox(
              constraints: BoxConstraints(
                minHeight: widget.inToolbar ? stewardControlHeight(context) : 0,
              ),
              child: FButton(
                variant: FButtonVariant.ghost,
                style: stewardButtonStyle(context),
                mainAxisSize: MainAxisSize.min,
                onPress:
                    widget.enabled && !widget.disabledItems.contains(entry.key)
                    ? () => widget.onChanged(entry.key)
                    : null,
                child: Text(entry.value),
              ),
            ),
          ),
        ),
    ];
    final contents = widget.horizontalScroll
        ? LayoutBuilder(
            builder: (context, constraints) {
              if (_viewportWidth != constraints.maxWidth) {
                _viewportWidth = constraints.maxWidth;
                _revealSelected();
              }
              return SingleChildScrollView(
                key: const ValueKey('workspace-tabs-horizontal-scroll'),
                controller: _scroll,
                scrollDirection: Axis.horizontal,
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    for (var index = 0; index < tabs.length; index++) ...[
                      if (index > 0) const SizedBox(width: 4),
                      tabs[index],
                    ],
                  ],
                ),
              );
            },
          )
        : Wrap(spacing: 4, runSpacing: 4, children: tabs);
    return Padding(
      padding: EdgeInsets.only(bottom: widget.inToolbar ? 0 : 10),
      child: GamepadTabRegion(
        enabled:
            widget.enabled &&
            widget.items.keys
                    .where((key) => !widget.disabledItems.contains(key))
                    .length >
                1,
        primary: !widget.inToolbar,
        onStep: _stepTab,
        builder: (context, showHints) => Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            // 提示节点始终占同一兄弟位置；输入方式切换不搬动中间的滚动栏，
            // 因而保留按钮焦点、手动横滚位置和正在编辑的邻接搜索框。
            Padding(
              padding: EdgeInsets.only(right: showHints ? 4 : 0),
              child: showHints
                  ? const GamepadHint('LT')
                  : const SizedBox.shrink(),
            ),
            Flexible(child: contents),
            Padding(
              padding: EdgeInsets.only(left: showHints ? 4 : 0),
              child: showHints
                  ? const GamepadHint('RT')
                  : const SizedBox.shrink(),
            ),
          ],
        ),
      ),
    );
  }
}

/// 简短状态辅助扫读；文字承担语义，颜色只表达强调，不伪装成可点击按钮。
class StatusBadge extends StatelessWidget {
  const StatusBadge(this.text, {this.emphasis = false, super.key});
  final String text;
  final bool emphasis;
  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
    decoration: BoxDecoration(
      color: context.stewardPaint.surface(
        emphasis
            ? context.theme.colors.primary
            : context.theme.colors.mutedForeground,
        alpha: emphasis ? .11 : .06,
      ),
      borderRadius: BorderRadius.circular(4),
    ),
    child: Text(
      text,
      style: StewardText.caption.copyWith(
        color: emphasis
            ? context.theme.colors.primary
            : context.theme.colors.mutedForeground,
      ),
    ),
  );
}

/// 备选方案与高级诊断按需展开；保持本地展开状态，实时轮询不会强行打开内容。
class CollapsibleSection extends StatefulWidget {
  const CollapsibleSection(
    this.title,
    this.children, {
    this.initiallyExpanded = false,
    super.key,
  });
  final String title;
  final List<Widget> children;
  final bool initiallyExpanded;
  @override
  State<CollapsibleSection> createState() => _CollapsibleSectionState();
}

class _CollapsibleSectionState extends State<CollapsibleSection> {
  late bool expanded = widget.initiallyExpanded;
  @override
  Widget build(BuildContext context) => Padding(
    // 可折叠详情可能紧接标签、统计或普通正文；上方也保留统一阅读间隔。
    // 内容仍由父级滚动，不改变按钮高度、展开状态或内部输入草稿节点。
    padding: const EdgeInsets.only(
      top: StewardDensity.detailGroupGap,
      bottom: StewardDensity.detailGroupGap,
    ),
    child: DecoratedBox(
      decoration: BoxDecoration(
        color: context.stewardPaint.surface(
          context.theme.colors.mutedForeground,
          alpha: .025,
        ),
        border: Border.all(
          color: context.stewardPaint.foreground(
            context.theme.colors.border,
            alpha: .55,
          ),
        ),
        borderRadius: BorderRadius.circular(7),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            expanded: expanded,
            child: FButton(
              variant: FButtonVariant.ghost,
              // Forui 0.27.4 的 FTappable 默认在绘制阶段缩放按钮（bounce），但本
              // 折叠组的边框属于外层 DecoratedBox，不参与缩放，因而按下时会露出
              // 一圈空隙。仅取消此头部的缩放，保留悬停/按压填色、焦点和展开语义。
              style: stewardButtonStyle(context),
              mainAxisAlignment: MainAxisAlignment.start,
              onPress: () => setState(() => expanded = !expanded),
              prefix: Icon(
                expanded ? FLucideIcons.chevronDown : FLucideIcons.chevronRight,
                size: 16,
              ),
              child: Expanded(
                child: Text(widget.title, style: StewardText.value),
              ),
            ),
          ),
          // 保留折叠内容的 State：用户暂时收起高级表单时，未保存的输入和焦点节点不能销毁。
          Visibility(
            visible: expanded,
            maintainState: true,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(12, 8, 12, 4),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: widget.children,
              ),
            ),
          ),
        ],
      ),
    ),
  );
}

class Fact extends StatelessWidget {
  const Fact(this.name, this.value, {super.key});
  final String name;
  final Object? value;
  @override
  Widget build(BuildContext context) => Padding(
    padding: StewardDensity.togglePadding,
    child: StewardBaselineRow(
      children: [
        SizedBox(
          width: 94,
          child: Text(
            name,
            style: StewardText.body.copyWith(
              color: context.theme.colors.mutedForeground,
            ),
          ),
        ),
        Expanded(child: Text(label(value), style: StewardText.body)),
      ],
    ),
  );
}

class EmptyState extends StatelessWidget {
  const EmptyState(this.title, this.detail, {super.key});
  final String title;
  final String detail;
  @override
  Widget build(BuildContext context) => Center(
    // 空状态也属于可浏览内容：窄侧栏与大字号会让说明换成多行，不能挤出结果区。
    // 内容较短时仍自然居中；高度不足时只滚动提示，不缩小文字或遮挡上方筛选控件。
    child: SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: context.stewardPaint.surface(
                  context.theme.colors.primary,
                  alpha: .07,
                ),
                border: Border.all(
                  color: context.stewardPaint.foreground(
                    context.theme.colors.primary,
                    alpha: .16,
                  ),
                ),
                borderRadius: BorderRadius.circular(14),
              ),
              child: Icon(
                FLucideIcons.inbox,
                size: 28,
                color: context.stewardPaint.foreground(
                  context.theme.colors.primary,
                  alpha: .8,
                ),
              ),
            ),
            const SizedBox(height: 12),
            Text(
              title,
              textAlign: TextAlign.center,
              style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600),
            ),
            // 简洁空态不为已移至页头的通用提示保留空白；真正的业务说明按需提供。
            if (detail.isNotEmpty) ...[
              const SizedBox(height: 8),
              Text(
                detail,
                textAlign: TextAlign.center,
                style: TextStyle(
                  height: 1.5,
                  color: context.theme.colors.mutedForeground,
                ),
              ),
            ],
          ],
        ),
      ),
    ),
  );
}

/// 行选择由稳定业务标识维护；刷新只替换事实，不把用户滚动位置和焦点重置到首行。
class DenseListDetail extends StatefulWidget {
  const DenseListDetail({
    required this.items,
    required this.identity,
    required this.title,
    required this.subtitle,
    required this.detail,
    this.titleContent,
    this.itemTint,
    this.summary,
    this.richSummary = false,
    this.onFavorite,
    this.empty = '当前没有可展示的信息',
    super.key,
  });
  final List<DataMap> items;
  final String Function(DataMap) identity;
  final String Function(DataMap) title;

  /// 订单可将进度放在标题右侧；纯标题仍保留为调用者的身份描述，不改变点击、
  /// 选择键或列表导航。组件由调用者提供，避免通用列表自行解释业务状态。
  final Widget Function(DataMap)? titleContent;

  /// 业务页可提供未经透明度处理的背景色；列表/详情统一应用背景透明度，
  /// 不污染文字、图片或选中边框，也不改变行身份及选择行为。
  final Color? Function(DataMap)? itemTint;
  final String Function(DataMap) subtitle;

  /// 多种独立信息（如点单、料理、酒水）可各占一行，避免尾部字段被总行数截断。
  /// 默认仍使用两行摘要；自定义内容只负责展示，整行选择/收藏/焦点行为保持一致。
  final Widget Function(DataMap)? summary;

  /// 菜品摘要含材料、厨具与库存时给予约半幅空间；普通列表仍沿用原紧凑宽度。
  /// 只影响桌面分栏，窄屏的列表/详情导航、滚动与语义边界不变。
  final bool richSummary;
  final Widget Function(DataMap) detail;
  final ValueChanged<DataMap>? onFavorite;
  final String empty;
  @override
  State<DenseListDetail> createState() => _DenseListDetailState();
}

class _DenseListDetailState extends State<DenseListDetail> {
  String? selectedId;
  bool showDetail = false;
  final scroll = ScrollController();
  @override
  void dispose() {
    scroll.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (widget.items.isEmpty) return EmptyState(widget.empty, '');
    final selected = widget.items
        .where((item) => widget.identity(item) == selectedId)
        .firstOrNull;
    final current = selected ?? widget.items.first;
    // 按工作区剩余宽度决定分栏；侧栏或字号变化后不能继续按整窗宽度挤压详情。
    return LayoutBuilder(
      builder: (context, constraints) {
        final wide = constraints.maxWidth >= 740;
        // 横屏软键盘可把结果压缩到48px。此时固定计数栏或返回栏可能占完视口，
        // 即使列表本身可滚动也无法浏览。以真实控件高度判定空间压力，兼顾大字号，
        // 仅调整非业务信息的位置，不删除订单、详情或操作，也不改变稳定选择标识。
        final short = constraints.maxHeight < stewardControlHeight(context) * 3;
        final returnToList = ToolRow([
          StewardButton(
            '返回列表',
            icon: FLucideIcons.arrowLeft,
            onPressed: () => setState(() => showDetail = false),
          ),
        ]);
        final currentTint = widget.itemTint?.call(current);
        final details = DecoratedBox(
          // 可选订单底色保持低对比度，普通详情只画边界；两者均保留背景透明度设置。
          key: const ValueKey('dense-detail-surface'),
          decoration: BoxDecoration(
            color: currentTint != null
                ? context.stewardPaint.surface(currentTint, alpha: .09)
                : null,
            border: Border.all(
              color: context.stewardPaint.foreground(
                context.theme.colors.border,
                alpha: .65,
              ),
            ),
            borderRadius: BorderRadius.circular(8),
          ),
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(12),
            child: !wide && short
                ? Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [returnToList, widget.detail(current)],
                  )
                : widget.detail(current),
          ),
        );
        if (!wide && showDetail && selected != null) {
          // Android 系统返回先回列表；隐藏工作区不能拦截当前页面的返回事件。
          final active = TickerMode.valuesOf(context).enabled;
          return PopScope<Object?>(
            canPop: !active,
            onPopInvokedWithResult: (didPop, result) {
              if (!didPop && active) setState(() => showDetail = false);
            },
            child: Actions(
              actions: {
                DismissIntent: CallbackAction<DismissIntent>(
                  onInvoke: (_) {
                    setState(() => showDetail = false);
                    return null;
                  },
                ),
              },
              child: Focus(
                autofocus: true,
                // 短视口把返回入口纳入同一条滚动轴；正常高度仍固定返回入口。
                // 系统返回/手柄返回继续由上方PopScope与Actions处理。
                child: short
                    ? details
                    : Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          returnToList,
                          Expanded(child: details),
                        ],
                      ),
              ),
            ),
          );
        }
        final list = ListView.builder(
          key: PageStorageKey(widget.key ?? widget.empty),
          controller: scroll,
          itemCount: widget.items.length,
          itemBuilder: (context, index) {
            final item = widget.items[index];
            final id = widget.identity(item);
            final tint = widget.itemTint?.call(item);
            final active =
                id == widget.identity(current) && (wide || selected != null);
            return Padding(
              // 极短视口优先完整呈现首行标题，只撤去行外留白；按钮自身内边距、
              // 内容与命中尺寸不缩减，正常高度仍保留原来的上下3px行间距。
              padding: EdgeInsets.symmetric(
                horizontal: 5,
                vertical: short ? 0 : 3,
              ),
              child: Semantics(
                // 每一行是独立的可选择对象，选中状态不能与相邻行隐式合并。
                // 显式语义边界也确保 IndexedStack 隐藏页面时，框架能整体移除该行的
                // 语义与几何缓存；否则 Flutter 3.47 在兄弟节点合并后可能复用空几何。
                // 不排除子语义，按钮操作、标题和订单标签仍交给读屏完整读取。
                container: true,
                selected: active,
                child: Container(
                  decoration: BoxDecoration(
                    color: tint != null
                        ? context.stewardPaint.surface(
                            tint,
                            alpha: active ? .18 : .11,
                          )
                        : active
                        ? context.stewardPaint.surface(
                            context.theme.colors.primary,
                            alpha: .10,
                          )
                        : null,
                    borderRadius: BorderRadius.circular(6),
                  ),
                  child: DecoratedBox(
                    // 与横向Tab一样把选中边界画在前景，悬停和按下不能盖住当前选择。
                    position: DecorationPosition.foreground,
                    decoration: BoxDecoration(
                      border: Border.all(
                        color: active
                            ? context.stewardPaint.foreground(
                                context.theme.colors.primary,
                                alpha: .7,
                              )
                            : context.stewardPaint.foreground(
                                context.theme.colors.border,
                                alpha: .35,
                              ),
                      ),
                      borderRadius: BorderRadius.circular(6),
                    ),
                    child: FButton(
                      key: ValueKey(id),
                      variant: FButtonVariant.ghost,
                      style: stewardButtonStyle(context),
                      mainAxisAlignment: MainAxisAlignment.start,
                      onPress: () => setState(() {
                        selectedId = id;
                        showDetail = true;
                      }),
                      actions: widget.onFavorite == null
                          ? null
                          : {
                              FavoriteIntent: CallbackAction<FavoriteIntent>(
                                onInvoke: (_) {
                                  widget.onFavorite!(item);
                                  return null;
                                },
                              ),
                            },
                      child: Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            widget.titleContent?.call(item) ??
                                Text(
                                  widget.title(item),
                                  style: const TextStyle(
                                    fontWeight: FontWeight.w600,
                                    height: 1.35,
                                  ),
                                ),
                            const SizedBox(height: 5),
                            widget.summary?.call(item) ??
                                Text(
                                  widget.subtitle(item),
                                  maxLines: 2,
                                  overflow: TextOverflow.ellipsis,
                                  style: TextStyle(
                                    fontSize: 12,
                                    height: 1.35,
                                    color: context.theme.colors.mutedForeground,
                                  ),
                                ),
                          ],
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            );
          },
        );
        return wide
            ? Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Container(
                    width: widget.richSummary
                        ? (constraints.maxWidth * .5).clamp(360, 560)
                        : (constraints.maxWidth * .34).clamp(290, 360),
                    decoration: BoxDecoration(
                      color: context.stewardPaint.surface(
                        context.theme.colors.primary,
                        alpha: .025,
                      ),
                      border: Border.all(
                        color: context.stewardPaint.foreground(
                          context.theme.colors.border,
                          alpha: .65,
                        ),
                      ),
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: short
                        // 计数不是操作目标。短高时不在列表首插入需先滚过的标题，
                        // 将完整视口交给订单；独立语义容器仍向读屏提供当前总项数。
                        ? Semantics(
                            key: const ValueKey('dense-list-count'),
                            container: true,
                            explicitChildNodes: true,
                            label: '${widget.items.length} 项',
                            child: list,
                          )
                        : Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              Padding(
                                padding: const EdgeInsets.fromLTRB(
                                  12,
                                  10,
                                  8,
                                  8,
                                ),
                                child: Text(
                                  '${widget.items.length} 项',
                                  style: TextStyle(
                                    fontSize: 12,
                                    color: context.theme.colors.mutedForeground,
                                  ),
                                ),
                              ),
                              Expanded(child: list),
                            ],
                          ),
                  ),
                  const SizedBox(width: 14),
                  Expanded(child: details),
                ],
              )
            : list;
      },
    );
  }
}

/// 业务开关的文字总是可见，避免仅用颜色或图标表达运行状态。
class SettingToggle extends StatelessWidget {
  const SettingToggle(
    this.title,
    this.value,
    this.onChanged, {
    this.description,
    super.key,
  });
  final String title;
  final bool value;
  final ValueChanged<bool>? onChanged;
  final String? description;
  @override
  Widget build(BuildContext context) => Padding(
    padding: StewardDensity.togglePadding,
    child: Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title, style: StewardText.body),
              if (description != null)
                Text(
                  description!,
                  style: StewardText.caption.copyWith(
                    color: context.theme.colors.mutedForeground,
                  ),
                ),
            ],
          ),
        ),
        const SizedBox(width: StewardDensity.controlGap),
        FSwitch(
          value: value,
          onChange: onChanged,
          enabled: onChanged != null,
          semanticsLabel: title,
        ),
      ],
    ),
  );
}
