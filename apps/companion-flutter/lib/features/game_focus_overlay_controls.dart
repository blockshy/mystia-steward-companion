import 'package:flutter/widgets.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/steward_style.dart';
import 'profile_controls.dart';

/// 游戏内界面只读复用当前主设备的专注内容；此处仅提供开关、外观与布局。
/// 控件嵌入唯一“专注显示”下拉，避免出现两份相互独立的内容选择。
class GameFocusOverlayControls extends StatelessWidget {
  const GameFocusOverlayControls(this.model, {required this.active, super.key});
  static const resourcePath = '/ui/focus-overlay';
  final WorkbenchModel model;
  final bool active;

  Future<void> _save(DataMap changes) async {
    final current = model.resources[resourcePath] ?? const <String, Object?>{};
    final recovery =
        gameFocusOverlayCanReset(current) &&
        changes.length == 1 &&
        changes['resetLayout'] == true;
    if (!active ||
        !model.canEdit ||
        (!gameFocusOverlayConfirmed(current) && !recovery))
      return;
    await model.mutate(
      resourcePath,
      body: {'expectedRevision': current['revision'], 'changes': changes},
    );
  }

  /// 位置百分比表示剩余可移动空间中的比例，100%恰好贴右/下边缘。
  /// 改宽高时限制旧坐标，始终提交完整布局，禁止产生部分更新后出屏的中间状态。
  Future<void> _saveLayout(String field, int percent) async {
    final state = model.resources[resourcePath] ?? const <String, Object?>{};
    if (!gameFocusOverlayConfirmed(state)) return;
    final layout = object(object(state['settings'])['layout']);
    var x = (layout['x']! as num).toDouble();
    var y = (layout['y']! as num).toDouble();
    var width = (layout['width']! as num).toDouble();
    var height = (layout['height']! as num).toDouble();
    switch (field) {
      case 'x':
        x = percent / 100 * (1 - width);
      case 'y':
        y = percent / 100 * (1 - height);
      case 'width':
        width = percent / 100;
        x = x.clamp(0, 1 - width);
      case 'height':
        height = percent / 100;
        y = y.clamp(0, 1 - height);
    }
    await _save({
      'layout': {'x': x, 'y': y, 'width': width, 'height': height},
    });
  }

  @override
  Widget build(BuildContext context) {
    final state = model.resources[resourcePath] ?? const <String, Object?>{};
    final settings = object(state['settings']);
    final layout = object(settings['layout']);
    final confirmed = gameFocusOverlayConfirmed(state);
    final editable = active && model.canEdit && confirmed;
    final recoverable = gameFocusOverlayCanReset(state);
    final boundary = (profileControlBoundary(model), active);
    Widget number(
      String title,
      String field,
      int fallback,
      int min,
      int max, {
      String unit = '',
    }) => IntegerSetting(
      key: ValueKey('game-focus-$field'),
      title: title,
      value: settings[field] is int ? settings[field]! as int : fallback,
      min: min,
      max: max,
      unit: unit,
      resetKey: boundary,
      onSave: editable ? (value) => _save({field: value}) : null,
    );
    Widget position(String title, String field) {
      final size = field == 'x' ? 'width' : 'height';
      final available = 1 - ((layout[size] as num?)?.toDouble() ?? 1);
      final relative = available <= 0
          ? 0
          : ((layout[field] as num?)?.toDouble() ?? 0) / available;
      return IntegerSetting(
        key: ValueKey('game-focus-$field'),
        title: title,
        value: (relative * 100).round().clamp(0, 100),
        min: 0,
        max: 100,
        unit: '%',
        resetKey: boundary,
        onSave: editable && available > 0
            ? (value) => _saveLayout(field, value)
            : null,
      );
    }

    Widget size(String title, String field, int fallback) => IntegerSetting(
      key: ValueKey('game-focus-$field'),
      title: title,
      value: layout[field] is num
          ? ((layout[field]! as num) * 100).round().clamp(10, 100)
          : fallback,
      min: 10,
      max: 100,
      unit: '%',
      resetKey: boundary,
      onSave: editable ? (value) => _saveLayout(field, value) : null,
    );
    final children = <Widget>[
      SettingToggle(
        '游戏内同步显示',
        settings['enabled'] == true,
        editable
            ? (value) => _save({
                'enabled': value,
                if (value) ...{
                  'content': gameFocusContentFromPreferences(
                    model.localPreferences,
                  ),
                  'theme': gameFocusTheme(
                    model.localPreferences,
                    MediaQuery.platformBrightnessOf(context),
                  ),
                },
              })
            : null,
        key: const ValueKey('game-focus-enabled'),
      ),
      if (!confirmed && !model.busy)
        ToolRow([
          Text(
            recoverable
                ? '显示设置无法读取，可恢复默认值'
                : model.connected
                ? '显示设置尚未确认'
                : '连接游戏电脑后设置',
            style: StewardText.caption,
          ),
          if (recoverable)
            StewardButton(
              '恢复默认设置',
              key: const ValueKey('game-focus-recover'),
              onPressed: active && model.canEdit
                  ? () => _save({'resetLayout': true})
                  : null,
            ),
          StewardButton(
            '重新读取',
            onPressed: active && model.connected && !model.busy
                ? () => model.readResource(resourcePath)
                : null,
          ),
        ]),
      if (settings['enabled'] == true) ...[
        number('游戏背景不透明度', 'backgroundOpacity', 72, 0, 100, unit: '%'),
        number('游戏内容不透明度', 'contentOpacity', 100, 20, 100, unit: '%'),
        number('游戏边框粗细', 'borderWidth', 1, 0, 6),
        number('游戏内字号', 'fontSize', 16, 12, 24),
        position('水平位置', 'x'),
        position('垂直位置', 'y'),
        size('显示宽度', 'width', 28),
        size('显示高度', 'height', 72),
        number('列表位置', 'scrollPercent', 0, 0, 100, unit: '%'),
        ToolRow([
          StewardButton(
            '重置布局',
            key: const ValueKey('game-focus-reset'),
            onPressed: editable ? () => _save({'resetLayout': true}) : null,
          ),
        ]),
      ],
    ];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (var index = 0; index < children.length; index++) ...[
          if (index > 0) const SizedBox(height: 8),
          children[index],
        ],
      ],
    );
  }
}
