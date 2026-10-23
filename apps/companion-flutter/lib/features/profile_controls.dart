import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/page_activity.dart';
import '../ui/setting_control_row.dart';
import '../ui/steward_color_picker.dart';
import '../ui/steward_controls.dart';
import '../ui/steward_style.dart';
import '../ui/steward_opacity.dart';

/// 延迟提交只能作用于开始编辑时的设备上下文。配置 revision 会随自己的成功写入
/// 变化，因此不放进此身份；真正的 revision/CAS/ACK 仍由模型在串行出队时检查。
Object profileControlBoundary(WorkbenchModel model) => (
  model.connectionVersion,
  model.connected,
  model.authority['registryId'],
  model.authority['currentDeviceId'],
  model.primary,
);

/// 整数选择器覆盖协议完整范围，键盘每次精确改变一个单位，不缩窄日志等大范围。
/// 共享参数拖动结束提交，键盘短防抖；本机外观 continuous 每 180ms 提交最新值。
/// 界面草稿立即变化，模型负责串行合并、权限复核与耐久确认。
class IntegerSetting extends StatefulWidget {
  const IntegerSetting({
    required this.title,
    required this.value,
    required this.min,
    required this.max,
    required this.onSave,
    this.unit = '',
    this.continuous = false,
    this.resetKey,
    super.key,
  }) : assert(min < max);
  final String title, unit;
  final int value, min, max;
  final Future<void> Function(int)? onSave;
  final bool continuous;
  final Object? resetKey;
  @override
  State<IntegerSetting> createState() => _IntegerSettingState();
}

class _IntegerSettingState extends State<IntegerSetting> {
  static const _interval = Duration(milliseconds: 180);
  late int _draft = widget.value;
  Timer? _timer;
  bool _pointer = false, _pointerInteraction = false, _dirty = false;
  bool _saving = false;
  int _generation = 0;
  String? _failure;

  @override
  void didUpdateWidget(covariant IntegerSetting oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.resetKey != widget.resetKey) {
      // 切换连接或失去主设备身份后取消草稿，不把旧授权带到新设备。
      _generation++;
      _timer?.cancel();
      _dirty = _pointer = _pointerInteraction = false;
      _draft = widget.value;
      _failure = null;
    } else if (widget.onSave == null) {
      // 同一身份下另一项正在保存时只暂缓，不丢弃刚才已经调整的最终值。
      // 主设备/连接变化由 resetKey 区分；实际发送仍交由模型再次检查权限。
      _timer?.cancel();
    } else if (oldWidget.onSave == null && _dirty && !_saving) {
      _timer = Timer(_interval, _commit);
    } else if (!_dirty && !_saving) {
      _draft = widget.value;
    }
  }

  @override
  void dispose() {
    _generation++;
    _timer?.cancel();
    super.dispose();
  }

  int _fromFraction(double value) =>
      (widget.min + value * (widget.max - widget.min))
          .round()
          .clamp(widget.min, widget.max)
          .toInt();
  double get _fraction => ((_draft - widget.min) / (widget.max - widget.min))
      .clamp(0.0, 1.0)
      .toDouble();

  void _change(FSliderValue value) {
    if (widget.onSave == null) return;
    final next = _fromFraction(value.max);
    if (next == _draft) return;
    setState(() {
      _draft = next;
      _dirty = true;
      _failure = null;
    });
    if (widget.continuous) {
      // 固定窗口节流而非不断重置防抖：长时间拖动也持续更新本机外观。
      if (_timer?.isActive != true) _timer = Timer(_interval, _commit);
    } else if (!_pointer) {
      _timer?.cancel();
      _timer = Timer(_interval, _commit);
    }
  }

  void _end(FSliderValue _) {
    if (_pointerInteraction) {
      _pointerInteraction = false;
      _timer?.cancel();
      _commit();
    } else {
      // Forui 键盘每一步也通知 onEnd，合并长按产生的连续按键，避免每步一次 CAS。
      _timer?.cancel();
      _timer = Timer(_interval, _commit);
    }
  }

  Future<void> _commit() async {
    final save = widget.onSave;
    if (!mounted || save == null || !_dirty || _saving) return;
    if (_draft == widget.value) {
      setState(() => _dirty = false);
      return;
    }
    final sent = _draft;
    final generation = _generation;
    setState(() => _saving = true);
    try {
      await save(sent);
    } catch (error) {
      if (mounted && generation == _generation) _failure = '应用失败：$error';
    } finally {
      if (mounted) {
        setState(() => _saving = false);
        if (generation == _generation) {
          if (_draft != sent) {
            // 前次写入期间的最终位置不能丢弃，始终只排队当前最新整数。
            _timer?.cancel();
            if (widget.onSave != null) _timer = Timer(_interval, _commit);
          } else {
            // 模型通知先构建权威值，帧末再结束草稿；失败也回到实际已保存值。
            WidgetsBinding.instance.addPostFrameCallback((_) {
              if (!mounted || generation != _generation || _draft != sent)
                return;
              setState(() {
                _dirty = false;
                _draft = widget.value;
              });
            });
          }
        }
      }
    }
  }

  @override
  Widget build(BuildContext context) => PageActivityReporter(
    // 每个控件独立发布，不依赖模型全局 error；并发控件的成功不能清掉另一项失败。
    // 发布器根据可见性收口，滑块草稿、取消及保存队列仍由本控件原生命周期处理。
    activity: _failure != null
        ? PageActivity(
            message: '${widget.title}应用失败',
            details: _failure,
            error: true,
          )
        : _saving
        ? PageActivity(message: '正在应用${widget.title}…', busy: true)
        : _dirty
        ? PageActivity(message: '${widget.title}待应用')
        : null,
    child: Padding(
      padding: StewardDensity.settingPadding,
      child: SettingControlRow(
        title: widget.title,
        control: Column(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Text(
              '$_draft${widget.unit}',
              key: const ValueKey('integer-setting-value'),
              style: StewardText.value,
            ),
            Listener(
              onPointerDown: (_) {
                _pointer = _pointerInteraction = true;
                if (!widget.continuous) _timer?.cancel();
              },
              onPointerUp: (_) => _pointer = false,
              onPointerCancel: (_) {
                _pointer = _pointerInteraction = false;
                _timer?.cancel();
                if (!_saving)
                  setState(() {
                    _dirty = false;
                    _draft = widget.value;
                  });
              },
              child: FSlider(
                key: const ValueKey('integer-setting-slider'),
                enabled: widget.onSave != null,
                // 当前值已常驻在上方，禁用重复的浮动气泡；忙碌时控件可立即禁用，
                // 无需在 Forui 提示层动画尚未完成时移除浮层，键盘与读屏数值仍保留。
                tooltipControls: const FSliderTooltipControls.disabled(),
                control: FSliderControl.liftedContinuous(
                  value: FSliderValue(max: _fraction),
                  stepPercentage: 1 / (widget.max - widget.min),
                  onChange: _change,
                ),
                onEnd: _end,
                trackHitRegionCrossExtent: 32,
                semanticValueFormatterCallback: (value) =>
                    '${widget.title} ${_fromFraction(value)}${widget.unit}',
              ),
            ),
          ],
        ),
        // 当前数值已经常驻在滑轨上方，不再为每个字段重复绘制范围和键盘教程。
        // 这里只移除辅助文字：合法范围、一步一单位、读屏数值和提交节流仍由上方
        // FSlider 的 control / semanticValueFormatterCallback 保持原有契约。
      ),
    ),
  );
}

/// 常用颜色可直接选色块，其余颜色通过 HSV 弹窗选择，十六进制值始终只读。
/// 弹窗没有模型访问能力；确认后本组件复核连接代次和当前权限，避免旧窗口写入新身份。
class ColorSetting extends StatefulWidget {
  const ColorSetting({
    required this.title,
    required this.value,
    required this.onSave,
    this.resetKey,
    super.key,
  });
  final String title, value;
  final Future<void> Function(String)? onSave;
  final Object? resetKey;
  @override
  State<ColorSetting> createState() => _ColorSettingState();
}

class _ColorSettingState extends State<ColorSetting> {
  static const _palette = [
    '#FFDB2E',
    '#5FACD3',
    '#F97373',
    '#7CCB8A',
    '#BA9CE8',
    '#FFFFFF',
    '#000000',
  ];
  late int _draft = _parse(widget.value);
  bool _saving = false;
  bool _pickerOpen = false;
  String? _failure;
  int _generation = 0;
  static int _parse(String value) =>
      int.tryParse(value.replaceFirst('#', ''), radix: 16) ?? 0;
  String get _hex =>
      '#${_draft.toRadixString(16).padLeft(6, '0').toUpperCase()}';

  @override
  void didUpdateWidget(covariant ColorSetting oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.resetKey != widget.resetKey) {
      _generation++;
      _draft = _parse(widget.value);
      _failure = null;
    } else if (!_saving && oldWidget.value != widget.value) {
      _draft = _parse(widget.value);
    }
  }

  Future<void> _openPicker() async {
    if (widget.onSave == null || _saving || _pickerOpen) return;
    final generation = _generation;
    final boundary = widget.resetKey;
    setState(() => _pickerOpen = true);
    final selected = await showStewardColorPicker(
      context,
      title: widget.title,
      initialColor: _hex,
    );
    if (!mounted) return;
    setState(() => _pickerOpen = false);
    // 弹窗期间可发生断开、换设备或主设备转移；即便随后切回原身份，也不能使用
    // 旧草稿，因此同时检查代次和边界。权限暂不可用时丢弃确认，不排队偷偷补发。
    if (selected == null) return;
    if (generation != _generation ||
        boundary != widget.resetKey ||
        widget.onSave == null ||
        _saving) {
      setState(() => _failure = '连接或权限已变化，颜色未保存，请重新选择。');
      return;
    }
    await _save(selected);
  }

  Future<void> _save(String value) async {
    final save = widget.onSave;
    if (save == null || _saving || value == widget.value) return;
    final generation = _generation;
    // 色块和弹窗均提交完整颜色；在进入异步回调前置忙碌标记，防止同帧重复确认。
    setState(() {
      _saving = true;
      _failure = null;
      _draft = _parse(value);
    });
    try {
      await save(value);
    } catch (error) {
      if (mounted && generation == _generation) _failure = '应用失败：$error';
    } finally {
      if (mounted) {
        setState(() => _saving = false);
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted && generation == _generation && !_saving) {
            setState(() => _draft = _parse(widget.value));
          }
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) => PageActivityReporter(
    activity: _failure != null
        ? PageActivity(
            message: '${widget.title}应用失败',
            details: _failure,
            error: true,
          )
        : _saving
        ? PageActivity(message: '正在应用${widget.title}…', busy: true)
        : null,
    child: Padding(
      padding: StewardDensity.settingPadding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SettingControlRow(
            title: widget.title,
            control: Align(
              alignment: Alignment.centerRight,
              child: Semantics(
                label: '${widget.title} $_hex，选择颜色',
                button: true,
                child: FButton(
                  key: const ValueKey('color-picker-open'),
                  mainAxisSize: MainAxisSize.min,
                  // 颜色触发与输入框、普通操作共用控制区度量；色块只是内容，
                  // 不再叠加大号按钮的垂直内边距，放大字号仍由统一样式计算。
                  style: stewardButtonStyle(context),
                  variant: FButtonVariant.outline,
                  // Forui 默认把 child 当作非伸缩的 Row 子项；在窄屏大字号时必须
                  // 将整块内容约束到右列剩余宽度，让 HEX 自然换行而不挤出按钮。
                  builder: (
                    context,
                    style,
                    textStyle,
                    iconStyle,
                    progressStyle,
                    child,
                  ) => Flexible(child: child!),
                  onPress: widget.onSave == null || _saving || _pickerOpen
                      ? null
                      : _openPicker,
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Container(
                        width: 20,
                        height: 20,
                        decoration: BoxDecoration(
                          color: Color(0xff000000 | _draft),
                          border: Border.all(
                            color: context.theme.colors.border,
                          ),
                        ),
                      ),
                      const SizedBox(width: 6),
                      Flexible(
                        child: Text(
                          _hex,
                          key: const ValueKey('color-setting-value'),
                          style: StewardText.caption,
                        ),
                      ),
                      const SizedBox(width: 4),
                      const Icon(FLucideIcons.chevronDown, size: 14),
                    ],
                  ),
                ),
              ),
            ),
          ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              for (final color in _palette)
                Semantics(
                  label: '${widget.title} $color',
                  button: true,
                  selected: _hex == color,
                  child: FButton(
                    key: ValueKey('palette-$color'),
                    // 色盘使用统一正方形命中区，触屏至少 44px；24px 色样保持原值，
                    // 不把样块尺寸当成按钮尺寸。Wrap 在窄屏自然折行。
                    mainAxisSize: MainAxisSize.min,
                    style: stewardIconButtonStyle(context),
                    variant: _hex == color
                        ? FButtonVariant.primary
                        : FButtonVariant.outline,
                    selected: _hex == color,
                    onPress: widget.onSave == null || _saving || _pickerOpen
                        ? null
                        : () => _save(color),
                    child: Container(
                      width: 24,
                      height: 24,
                      alignment: Alignment.center,
                      decoration: BoxDecoration(
                        color: Color(0xff000000 | _parse(color)),
                        border: Border.all(color: context.theme.colors.border),
                      ),
                      child: _hex == color
                          ? Icon(
                              FLucideIcons.check,
                              size: 18,
                              // 黑/白取较高对比度的一侧，深浅色块都能辨认选中勾选。
                              color: context.stewardPaint.foreground(
                                Color(0xff000000 | _parse(color))
                                            .computeLuminance() >
                                        .179
                                    ? const Color(0xff000000)
                                    : const Color(0xffffffff),
                              ),
                            )
                          : null,
                    ),
                  ),
                ),
            ],
          ),
        ],
      ),
    ),
  );
}
