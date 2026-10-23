import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import 'steward_select.dart';
import 'steward_controls.dart';
import 'steward_style.dart';

/// 受控的动态标签：搜索下拉只负责添加意图，标签按钮只负责移除意图。
/// 已选值由调用方的权威配置回传，不在组件内乐观增删，保存失败不会假装成功。
/// 标签和候选分别传入，因此目录缺失的历史编号仍可显示并移除。
class StewardDynamicTags<T> extends StatelessWidget {
  const StewardDynamicTags({
    required this.label,
    required this.items,
    required this.selected,
    required this.onAdd,
    required this.onRemove,
    this.enabled = true,
    this.allowAdd = true,
    this.addHint = '搜索并添加',
    this.saving = false,
    this.boundary,
    this.itemLeading,
    super.key,
  });

  final String label;
  final Map<T, String> items, selected;
  final ValueChanged<T> onAdd, onRemove;
  final bool enabled, saving, allowAdd;
  final String addHint;
  final Object? boundary;

  /// 同一个精确选项值在候选和已选标签中共用装饰图片；缺失目录的历史ID也会传给
  /// 调用方，由其保留图标回退。图片不能参与筛选、身份判断或增删配置。
  final Widget Function(T value)? itemLeading;

  @override
  Widget build(BuildContext context) {
    final available = {
      for (final entry in items.entries)
        if (!selected.containsKey(entry.key)) entry.key: entry.value,
    };
    final editable = enabled && !saving;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Align(
            alignment: Alignment.centerLeft,
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: stewardSelectWidth),
              child: StewardSelect<T>(
                key: const ValueKey('dynamic-tags-add'),
                label: label,
                hint: !allowAdd
                    ? '目录未就绪'
                    : available.isEmpty
                    ? '暂无可添加项'
                    : addHint,
                searchHint: '搜索$label',
                items: available,
                value: null,
                enabled: editable && allowAdd,
                searchable: true,
                itemLeading: itemLeading,
                boundary: boundary,
                onChanged: (value) {
                  // 底层选择器会检查打开时的目录及身份；这里再禁止已选项目重复添加。
                  if (editable && allowAdd && available.containsKey(value))
                    onAdd(value);
                },
              ),
            ),
          ),
          const SizedBox(height: 8),
          if (selected.isEmpty)
            Text(
              '尚未选择',
              style: TextStyle(
                fontSize: 12,
                color: context.theme.colors.mutedForeground,
              ),
            )
          else
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                for (final entry in selected.entries)
                  FButton.raw(
                    key: ValueKey(('dynamic-tag', entry.key)),
                    style: stewardButtonStyle(context),
                    variant: FButtonVariant.outline,
                    semanticsLabel: '移除$label：${entry.value}',
                    onPress: editable ? () => onRemove(entry.key) : null,
                    child: ConstrainedBox(
                      constraints: BoxConstraints(
                        minWidth: stewardControlHeight(context),
                        minHeight: stewardControlHeight(context),
                      ),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 8,
                          vertical: 6,
                        ),
                        child: StewardBaselineRow(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            if (itemLeading != null) ...[
                              StewardFirstLineCenter(
                                textStyle: const TextStyle(fontSize: 12),
                                referenceText: entry.value,
                                child: ExcludeSemantics(
                                  child: itemLeading!(entry.key),
                                ),
                              ),
                              const SizedBox(width: 6),
                            ],
                            Flexible(
                              child: Text(
                                entry.value,
                                style: const TextStyle(fontSize: 12),
                              ),
                            ),
                            const SizedBox(width: 6),
                            StewardFirstLineCenter(
                              textStyle: const TextStyle(fontSize: 12),
                              referenceText: entry.value,
                              child: const Icon(FLucideIcons.x, size: 14),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          if (saving)
            const Padding(
              padding: EdgeInsets.only(top: 6),
              child: Text('正在保存排除设置…', style: TextStyle(fontSize: 12)),
            ),
          if (!allowAdd)
            const Padding(
              padding: EdgeInsets.only(top: 6),
              child: Text(
                '目录未就绪，暂不能添加；已选项仍可移除。',
                style: TextStyle(fontSize: 12),
              ),
            ),
        ],
      ),
    );
  }
}
