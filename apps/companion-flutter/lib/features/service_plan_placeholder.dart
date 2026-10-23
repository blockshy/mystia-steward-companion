import 'dart:math' as math;

import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../ui/menu_item_content.dart';
import '../ui/steward_opacity.dart';
import '../ui/steward_style.dart';

/// 方案区的布局描述只包含显示字段，不包含任何料理、材料或执行身份。
/// 普通摘要与详情各有自己的结构；专注只预留用户已经开启的制作字段。
class ServicePlanPlaceholder extends StatelessWidget {
  const ServicePlanPlaceholder({
    this.details = false,
    this.budget = false,
    this.focusFields,
    super.key,
  });

  final bool details, budget;
  final List<String>? focusFields;

  /// 按实际主题字体、TextScaler 和图标槽测量，预留常见单行字段的高度。
  /// 不猜测尚未返回的名称长度；真实长名称/多材料需要换行时允许自然增高，
  /// 不截断信息、不缩小字体，也不把任何占位块解释为已选定的执行方案。
  double reservedHeight(BuildContext context, double width) {
    final scale = MediaQuery.textScalerOf(context);
    double textHeight(TextStyle style) {
      final painter = TextPainter(
        text: TextSpan(
          text: '国Ag012',
          style: DefaultTextStyle.of(context).style.merge(style),
        ),
        textDirection: Directionality.of(context),
        textScaler: scale,
      )..layout();
      final height = painter.height;
      painter.dispose();
      return height;
    }

    final row =
        math.max(
          scale.scale(StewardImageSize.tag),
          textHeight(StewardText.tag),
        ) +
        12 +
        5;
    if (focusFields != null) return focusFields!.length * row;
    final name = math.max(
      scale.scale(StewardImageSize.name),
      textHeight(StewardText.itemTitle),
    );
    // 摘要：两实体名称、料理五组资料、酒水两组资料及分隔/实体留白。
    // 详情：两实体名称、各自标签资料；其它完整解释按真实内容继续自然展开。
    return name * 2 +
        row *
            (details
                ? 2
                : _twoMetricRows(context, width)
                ? 8
                : 7) +
        33 +
        (budget ? row : 0);
  }

  // 窄内容区的标价/基础成本/加料成本通常自然分成两行。预留第二行只用于这个
  // 明确结构，不对全部字段统一加倍，避免宽屏和短方案出现大片无意义空白。
  bool _twoMetricRows(BuildContext context, double width) =>
      width / (MediaQuery.textScalerOf(context).scale(12) / 12) < 320;

  @override
  Widget build(BuildContext context) => Semantics(
    container: true,
    label: '方案资料待加载；当前点单与送达状态已显示',
    excludeSemantics: true,
    child: LayoutBuilder(
      builder: (context, constraints) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: focusFields != null
            ? [
                for (final name in focusFields!)
                  MenuInfoGroup(name, [const _LoadingField()]),
              ]
            : [
                const _LoadingName('料理方案待加载'),
                if (!details)
                  for (final name in ['指标', '标签', '厨具', '配方', '加料'])
                    MenuInfoGroup(name, [
                      if (name == '指标' &&
                          _twoMetricRows(context, constraints.maxWidth))
                        const Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            _LoadingField(),
                            SizedBox(height: 5),
                            _LoadingField(),
                          ],
                        )
                      else
                        const _LoadingField(),
                    ])
                else
                  const MenuInfoGroup('标签', [_LoadingField()]),
                const MenuItemDivider(),
                const _LoadingName('酒水方案待加载'),
                if (!details) const MenuInfoGroup('指标', [_LoadingField()]),
                const MenuInfoGroup('标签', [_LoadingField()]),
                if (budget) const MenuInfoGroup('预算', [_LoadingField()]),
              ],
      ),
    ),
  );
}

/// 新订单立即进入列表；只给确实尚未收到的方案区预留结构空间。
/// 不保存业务对象、不计时、不播放尺寸动画。初次即有结果的订单沿用原自然高度，
/// 避免为了少数慢请求给所有卡片永久添加大块空白。经历等待的同一订单保留预留下限，
/// 结果更长则自然增长；当前性或页头提示切换不会令这一下限反复增减。
class ServicePlanRegion extends StatefulWidget {
  const ServicePlanRegion({
    required this.pending,
    required this.placeholder,
    required this.child,
    super.key,
  });
  final bool pending;
  final ServicePlanPlaceholder placeholder;
  final Widget child;

  @override
  State<ServicePlanRegion> createState() => _ServicePlanRegionState();
}

class _ServicePlanRegionState extends State<ServicePlanRegion>
    with AutomaticKeepAliveClientMixin {
  late bool _reserved = widget.pending;

  // 惰性订单列表滚出视口后仍保留这个小型纯布局标记，防止回来时丢失预留下限。
  // 不保留旧订单/方案；稳定父 key 被移除时仍正常销毁，不能使完成的订单复活。
  @override
  bool get wantKeepAlive => _reserved;

  @override
  void didUpdateWidget(covariant ServicePlanRegion oldWidget) {
    super.didUpdateWidget(oldWidget);
    final before = _reserved;
    _reserved = _reserved || widget.pending;
    if (_reserved != before) updateKeepAlive();
  }

  @override
  Widget build(BuildContext context) {
    super.build(context);
    return LayoutBuilder(
      builder: (context, constraints) => ConstrainedBox(
        constraints: BoxConstraints(
          minHeight: _reserved
              ? widget.placeholder.reservedHeight(context, constraints.maxWidth)
              : 0,
        ),
        child: Align(
          alignment: Alignment.topLeft,
          heightFactor: 1,
          child: widget.pending ? widget.placeholder : widget.child,
        ),
      ),
    );
  }
}

class _LoadingName extends StatelessWidget {
  const _LoadingName(this.text);
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: 6, bottom: 3),
    child: ConstrainedBox(
      constraints: BoxConstraints(
        minHeight: MediaQuery.textScalerOf(context)
            .scale(StewardImageSize.name),
      ),
      child: Text(
        text,
        style: StewardText.itemTitle.copyWith(
          color: context.theme.colors.mutedForeground,
        ),
      ),
    ),
  );
}

/// 静态中性加载占位与标签采用同样的内边距和图标高度；不使用闪烁/shimmer，
/// 不绘制真实实体图片，也不占用交互命中区或冒充可执行按钮。
class _LoadingField extends StatelessWidget {
  const _LoadingField();
  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 5),
    decoration: BoxDecoration(
      color: context.stewardPaint.surface(
        context.theme.colors.muted,
        alpha: .5,
      ),
      border: Border.all(color: context.theme.colors.border),
      borderRadius: BorderRadius.circular(5),
    ),
    child: ConstrainedBox(
      constraints: BoxConstraints(
        minHeight: MediaQuery.textScalerOf(context).scale(StewardImageSize.tag),
      ),
      child: Text(
        '待加载',
        style: StewardText.tag.copyWith(
          color: context.theme.colors.mutedForeground,
        ),
      ),
    ),
  );
}
