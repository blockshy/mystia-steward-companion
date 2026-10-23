import 'package:flutter/widgets.dart';

/// 页头展示当前页面的短状态；完整说明由页头的详情入口读取，不占用正文行。
/// 这里只承载不可执行的文字，重试和人工确认仍由业务页面检查权限与请求代次。
@immutable
class PageActivity {
  const PageActivity({
    required this.message,
    this.details,
    this.busy = false,
    this.error = false,
  });

  final String message;
  final String? details;
  final bool busy;
  final bool error;

  @override
  bool operator ==(Object other) =>
      other is PageActivity &&
      other.message == message &&
      other.details == details &&
      other.busy == busy &&
      other.error == error;
  @override
  int get hashCode => Object.hash(message, details, busy, error);
}

/// 每个主页面拥有独立通道。IndexedStack 中隐藏页可以完成旧读取，但不能把其
/// 状态显示到另一个页头。按发布者分别保存状态，父页面的空状态不能覆盖子表单的
/// 错误；旧发布者卸载也只能撤回自己。错误优先，其次加载，其余说明按注册顺序展示。
class PageActivityChannel extends ValueNotifier<PageActivity?> {
  PageActivityChannel() : super(null);
  final _activities = <Object, PageActivity>{};
  bool _closed = false;

  void publish(Object owner, PageActivity? activity) {
    if (_closed) return;
    if (activity == null || activity.message.trim().isEmpty) {
      _activities.remove(owner);
    } else {
      _activities[owner] = activity;
    }
    _refresh();
  }

  void clear(Object owner) {
    if (_closed || _activities.remove(owner) == null) return;
    _refresh();
  }

  void _refresh() {
    // 同一事实可能同时由父页面与局部控件报告；仅相同消息、详情与严重级别去重。
    // 不以“最后发布者获胜”掩盖其它错误，也不让一个忙碌字段清空其它字段反馈。
    final distinct = _activities.values.toSet().toList();
    if (distinct.isEmpty) {
      value = null;
      return;
    }
    final primary =
        distinct.where((entry) => entry.error).firstOrNull ??
        distinct.where((entry) => entry.busy).firstOrNull ??
        distinct.first;
    if (distinct.length == 1) {
      value = primary;
      return;
    }
    final ordered = [primary, ...distinct.where((entry) => entry != primary)];
    value = PageActivity(
      message: '${primary.message} · ${distinct.length} 项',
      details: ordered
          .map((entry) {
            final detail = entry.details?.trim();
            return detail == null || detail.isEmpty || detail == entry.message
                ? entry.message
                : '${entry.message}\n$detail';
          })
          .join('\n\n'),
      busy: distinct.any((entry) => entry.busy),
      error: distinct.any((entry) => entry.error),
    );
  }

  @override
  void dispose() {
    _closed = true;
    _activities.clear();
    super.dispose();
  }
}

/// 工作台提供页面专属通道；组件独立嵌入测试或其它容器时，发布器只透传正文，
/// 不另外生成占高度的提示行。是否显示页头由宿主负责，不由业务模型保存UI状态。
class PageActivityScope extends InheritedWidget {
  const PageActivityScope({
    required this.channel,
    required super.child,
    super.key,
  });
  final PageActivityChannel channel;

  static PageActivityChannel? maybeOf(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<PageActivityScope>()?.channel;

  @override
  bool updateShouldNotify(PageActivityScope oldWidget) =>
      channel != oldWidget.channel;
}

/// 页面的局部防抖/请求状态在帧完成后投影到页头，避免build过程中通知祖先。
/// 只发布最后一帧的值，卸载或移入其它通道后，旧回调不能覆盖新状态。
class PageActivityReporter extends StatefulWidget {
  const PageActivityReporter({
    required this.activity,
    required this.child,
    super.key,
  });
  final PageActivity? activity;
  final Widget child;

  @override
  State<PageActivityReporter> createState() => _PageActivityReporterState();
}

class _PageActivityReporterState extends State<PageActivityReporter> {
  final _owner = Object();
  PageActivityChannel? _channel;
  int _generation = 0;
  bool _enabled = true;

  void _clearLater(PageActivityChannel? channel) {
    WidgetsBinding.instance.addPostFrameCallback((_) => channel?.clear(_owner));
  }

  void _schedule() {
    final generation = ++_generation;
    final channel = _channel;
    final activity = _enabled ? widget.activity : null;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && generation == _generation && identical(channel, _channel))
        channel?.publish(_owner, activity);
    });
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final next = PageActivityScope.maybeOf(context);
    // IndexedStack 保留各页和专注/详情的 State；隐藏子树必须撤回自己的提示，
    // 恢复显示时再发布当前事实，不让上一个子页的保存失败留在新页头。
    _enabled = TickerMode.valuesOf(context).enabled;
    if (!identical(next, _channel)) {
      _clearLater(_channel);
      _channel = next;
    }
    _schedule();
  }

  @override
  void didUpdateWidget(covariant PageActivityReporter oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.activity != oldWidget.activity) _schedule();
  }

  @override
  void dispose() {
    ++_generation;
    _clearLater(_channel);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
