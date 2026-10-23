import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/page_activity.dart';
import '../ui/setting_control_row.dart';
import '../ui/steward_select.dart';
import '../ui/steward_dynamic_tags.dart';
import '../ui/steward_style.dart';
import 'profile_controls.dart';

/// 推荐偏好属于共享业务配置，独立放在推荐工作区。模式由服务端 profile 持久保存，
/// 不用本地临时选中态，也不在运行中根据权重反向猜预设；权限与确认仍由模型执行。
class RecommendationPreferencesPanel extends StatefulWidget {
  const RecommendationPreferencesPanel(
    this.model, {
    this.active = true,
    super.key,
  });
  final WorkbenchModel model;
  final bool active;
  @override
  State<RecommendationPreferencesPanel> createState() =>
      _RecommendationPreferencesPanelState();
}

class _RecommendationPreferencesPanelState
    extends State<RecommendationPreferencesPanel> {
  WorkbenchModel get model => widget.model;
  String? _exclusionError, _sortError;
  bool _exclusionSaving = false, _sortSaving = false;
  String get _mode =>
      label(object(model.profile['recommendationSortProfile'])['preset']);
  bool get _canEdit =>
      mounted &&
      widget.active &&
      model.canEdit &&
      !_sortSaving &&
      !_exclusionSaving;

  /// 模式切换是一次完整配置修改。切换自定义由模型出队读取最新目标列表，切换预设
  /// 则原子替换九项固定值；同帧双击不会提交两份旧快照，失败不显示虚假选中状态。
  Future<void> _changeMode(String mode) async {
    if (!_canEdit || mode == _mode) return;
    final identity = _selectionIdentity();
    setState(() {
      _sortSaving = true;
      _sortError = null;
    });
    try {
      await model.updateSortMode(mode);
      if (mounted && identity == _selectionIdentity() && _mode != mode) {
        setState(
          () => _sortError = model.error.isEmpty ? '评分模式未保存，请重试。' : model.error,
        );
      }
    } catch (_) {
      if (mounted && identity == _selectionIdentity()) {
        setState(() => _sortError = '评分模式保存失败，请重试。');
      }
    } finally {
      if (mounted) setState(() => _sortSaving = false);
    }
  }

  /// 资源选择也是一次用户授权：选择器只能修改打开时的连接、场景与设备权威。
  /// 与通用确认对话框使用相同字段；等待期间不能将旧目录编号套用到新的连接。
  Object _selectionIdentity() => (
    profileControlBoundary(model),
    model.snapshot['automationSessionId'],
    model.snapshot['nightBusinessGeneration'],
    model.snapshot['runtimeDaySceneGeneration'],
  );
  Object _selectionBoundary() => (
    _selectionIdentity(),
    model.authority['authorityRevision'],
    widget.active,
  );

  /// 目录以唯一整数编号关联。重复编号不猜选哪条；同名不同编号显示编号区分，
  /// 保持原始 ID 写入协议，不把显示名称当作持久身份。
  Map<int, String> _exclusionOptions(String kind) {
    final catalog = model.catalogItems(kind);
    final counts = <int, int>{};
    for (final item in catalog) {
      if (item['id'] case final int id) counts[id] = (counts[id] ?? 0) + 1;
    }
    final options = <int, String>{};
    for (final item in catalog) {
      if (item['id'] case final int id when counts[id] == 1) {
        final name = label(item['name'], '编号 $id');
        final duplicateName =
            catalog.where((entry) => entry['name'] == item['name']).length > 1;
        options[id] = duplicateName ? '$name（编号 $id）' : name;
      }
    }
    return options;
  }

  Future<void> _changeExclusion(
    String key,
    String kind,
    int id,
    bool add,
    Object boundary,
  ) async {
    // 两组资源共用即时门闩，在调用模型前就阻止第二次写入；不能等待模型的 busy
    // 通知下一帧才禁用，否则同帧点击会携带同一份旧嵌套配置并互相覆盖。
    if (!mounted || _exclusionSaving || _sortSaving) return;
    if (!model.canEdit ||
        !widget.active ||
        boundary != _selectionBoundary() ||
        add &&
            (model.catalog['isComplete'] == false ||
                !_exclusionOptions(kind).containsKey(id))) {
      setState(() => _exclusionError = '连接、目录或主设备已变化，排除设置未保存，请重新选择。');
      return;
    }
    final current = object(model.profile['recommendationExclusions']);
    final ids = values(current[key]).whereType<int>().toSet();
    final changed = add ? ids.add(id) : ids.remove(id);
    if (!changed) return;
    final next = ids.toList()..sort();
    final identity = _selectionIdentity();
    setState(() {
      _exclusionSaving = true;
      _exclusionError = null;
    });
    try {
      // 每次明确操作读取最新 profile，保留另一组排除项及服务端增加的其它字段。
      // 标签由模型实际回传驱动，不提前修改；失败或未知写入结果不会显示虚假成功。
      await model.updateProfile({
        'recommendationExclusions': {...current, key: next},
      });
      if (!mounted || identity != _selectionIdentity()) return;
      final saved = values(
        object(model.profile['recommendationExclusions'])[key],
      ).contains(id);
      if (saved != add) {
        setState(
          () => _exclusionError = model.error.isEmpty
              ? '排除设置未保存，请重试。'
              : model.error,
        );
      }
    } catch (_) {
      if (mounted && identity == _selectionIdentity()) {
        setState(() => _exclusionError = '排除设置保存失败，请重试。');
      }
    } finally {
      if (mounted) setState(() => _exclusionSaving = false);
    }
  }

  Widget _exclusionTags(String key, String kind, String title) {
    final options = _exclusionOptions(kind);
    final boundary = _selectionBoundary();
    return StewardDynamicTags<int>(
      key: ValueKey(key),
      label: '排除$title',
      items: options,
      // 两类排除项分别使用食材/酒水命名空间；已选历史ID缺少目录时仍是同一个
      // 持久身份，显示回退图标且保留移除能力，不能用名称猜另一条资源。
      itemLeading: (id) => EntityImage(
        kind: kind == 'ingredients'
            ? EntityImageKind.ingredient
            : EntityImageKind.beverage,
        id: entityImageId(id),
        fallback: kind == 'ingredients'
            ? FLucideIcons.leaf
            : FLucideIcons.glassWater,
      ),
      addHint: '添加$title',
      allowAdd: model.catalog['isComplete'] != false,
      selected: {
        for (final id in values(
          object(model.profile['recommendationExclusions'])[key],
        ).whereType<int>())
          id: options[id] ?? '编号 $id（目录中未找到）',
      },
      enabled: _canEdit,
      saving: _exclusionSaving,
      boundary: boundary,
      onAdd: (id) => _changeExclusion(key, kind, id, true, boundary),
      onRemove: (id) => _changeExclusion(key, kind, id, false, boundary),
    );
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: model,
    builder: (context, child) => PageActivityReporter(
      activity: !widget.active
          ? null
          : _sortError != null || _exclusionError != null
          ? PageActivity(
              message: '推荐偏好未保存',
              details: _sortError ?? _exclusionError,
              error: true,
            )
          : _sortSaving || _exclusionSaving
          ? const PageActivity(message: '正在保存推荐偏好…', busy: true)
          : !model.primary
          ? const PageActivity(message: '查看设备：推荐偏好只读')
          : null,
      child: Align(
        alignment: Alignment.topCenter,
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 880),
          child: ListView(
            key: const PageStorageKey('recommendation-preferences'),
            padding: const EdgeInsets.only(right: 8, bottom: 20),
            children: _contents(context),
          ),
        ),
      ),
    ),
  );

  Widget _sharedToggle(String key, String title) {
    final identity = _selectionIdentity();
    return SettingToggle(
      title,
      model.profile[key] == true,
      _canEdit
          ? (value) {
              if (_canEdit && identity == _selectionIdentity())
                model.updateProfile({key: value});
            }
          : null,
    );
  }

  /// 把绘制时身份封装到回调中；原页面仍存活但已经换到另一设备时，旧手柄/输入事件
  /// 不得仅因新设备也允许编辑就写入新配置。模式数值仍由服务器 profile 决定。
  Widget _modeTabs() {
    final identity = _selectionIdentity();
    return WorkspaceTabs<String>(
      key: const ValueKey('recommendation-sort-mode'),
      items: const {
        'balanced': '均衡',
        'resources': '节约资源',
        'profit': '利润',
        'simple': '简单料理',
        'custom': '自定义',
      },
      value: _mode,
      horizontalScroll: !model.desktop,
      enabled: _canEdit,
      onChanged: (mode) {
        if (identity == _selectionIdentity()) _changeMode(mode);
      },
    );
  }

  Widget _objective(DataMap objective) {
    final identity = _selectionIdentity();
    return _ObjectiveRow(
      key: ValueKey(objective['key']),
      objective: objective,
      enabled: _canEdit,
      resetKey: (profileControlBoundary(model), widget.active, _mode),
      onSave: (changes) =>
          _changeObjective(label(objective['key']), changes, identity),
    );
  }

  List<Widget> _contents(BuildContext context) => [
    Section('推荐规则', [
      for (final entry in const {
        'filterMissingCookers': '排除缺少厨具的料理',
        'missionRecipePriorityEnabled': '任务料理优先',
        'pinFavoriteRecipeEnabled': '收藏料理优先',
        'pinFavoriteBeverageEnabled': '收藏酒水优先',
      }.entries)
        _sharedToggle(entry.key, entry.value),
      const SizedBox(height: 8),
      SettingControlRow(
        title: '预算不足时',
        maxControlWidth: stewardSelectWidth,
        control: StewardSelect<String>(
          key: const ValueKey('recommendation-budget-policy'),
          label: '预算不足时',
          showLabel: false,
          items: const {'block': '不足时阻止', 'warn': '仅提示', 'ignore': '忽略预算'},
          enabled: _canEdit,
          boundary: profileControlBoundary(model),
          value: label(model.profile['recommendationBudgetPolicy'], 'block'),
          onChanged: (value) {
            if (_canEdit) {
              model.updateProfile({'recommendationBudgetPolicy': value});
            }
          },
        ),
      ),
      IntegerSetting(
        key: const ValueKey('recipeVariantLimitPerBase'),
        title: '每个料理的加料变体数',
        unit: ' 种',
        resetKey: profileControlBoundary(model),
        value:
            (model.profile['recipeVariantLimitPerBase'] as num?)?.toInt() ?? 1,
        min: 1,
        max: 8,
        onSave: _canEdit
            ? (value) =>
                  model.updateProfile({'recipeVariantLimitPerBase': value})
            : null,
      ),
    ]),
    Section('评分排序', [
      const _PreferenceNote('四个预设使用固定参数；选择“自定义”后才能编辑九项目标。权重共同参与评分，排列顺序不代表优先级。'),
      _modeTabs(),
      if (_mode != 'custom')
        const _PreferenceNote('当前采用固定预设。切换到自定义会保留当前参数，不会自动改动数值。'),
      if (_mode == 'custom')
        CollapsibleSection('自定义评分参数', [
          for (final objective in records(
            object(model.profile['recommendationSortProfile'])['objectives'],
          ))
            _objective(objective),
        ], initiallyExpanded: true),
    ]),
    Section('排除资源', [
      for (final entry in const {
        'excludedIngredientIds': ('ingredients', '食材'),
        'excludedBeverageIds': ('beverages', '酒水'),
      }.entries)
        _exclusionTags(entry.key, entry.value.$1, entry.value.$2),
    ]),
  ];

  /// 只提交当前目标的叶字段；模型在串行出队时合并最新配置，不携带旧九项快照。
  Future<void> _changeObjective(
    String key,
    DataMap changes,
    Object identity,
  ) async {
    // 事件回调可能晚于页面切换或远端模式更新；前端先复核，模型还会在串行出队时
    // 再确认 custom，避免旧滑块草稿覆盖后来选择的固定预设。
    if (!_canEdit || _mode != 'custom' || identity != _selectionIdentity())
      return;
    await model.updateSortObjective(key, changes);
  }
}

/// 说明统一采用辅助字号，不改控件高度或用文字截断掩盖配置规则。
class _PreferenceNote extends StatelessWidget {
  const _PreferenceNote(this.text);
  final String text;
  @override
  Widget build(BuildContext context) => Text(
    text,
    style: StewardText.caption.copyWith(
      color: context.theme.colors.mutedForeground,
    ),
  );
}

/// 评分目标各控件自动提交自己的字段。启停/方向不再是隐藏草稿，权重由同一个整数
/// 选择器处理结束提交和键盘防抖；模型按目标 key 合并，不重发旧快照中的其他参数。
class _ObjectiveRow extends StatelessWidget {
  const _ObjectiveRow({
    required this.objective,
    required this.enabled,
    required this.onSave,
    required this.resetKey,
    super.key,
  });
  final DataMap objective;
  final bool enabled;
  final Future<void> Function(DataMap) onSave;
  final Object resetKey;

  @override
  Widget build(BuildContext context) {
    final name =
        const {
          'foodPreference': '料理喜好',
          'beveragePreference': '酒水喜好',
          'negativeRisk': '负面风险',
          'extraCount': '加料数量',
          'resourcePressure': '资源压力',
          'totalCost': '成本',
          'profit': '利润',
          'beverageStock': '酒水库存',
          'cookerAvailable': '厨具可用',
        }[objective['key']] ??
        label(objective['key']);
    return Container(
      padding: const EdgeInsets.symmetric(vertical: 10),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: context.theme.colors.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          FCheckbox(
            label: Text(name),
            value: objective['enabled'] == true,
            enabled: enabled,
            onChange: (value) {
              if (enabled) onSave({'enabled': value});
            },
          ),
          IntegerSetting(
            title: '权重',
            value: (objective['weight'] as num?)?.toInt() ?? 0,
            min: 0,
            max: 100,
            resetKey: resetKey,
            onSave: enabled ? (value) => onSave({'weight': value}) : null,
          ),
          SettingControlRow(
            title: '评分方向',
            maxControlWidth: stewardSelectWidth,
            control: StewardSelect<String>(
              label: '评分方向',
              showLabel: false,
              items: const {'asc': '越低越优先', 'desc': '越高越优先'},
              enabled: enabled,
              boundary: resetKey,
              value: label(objective['direction'], 'desc'),
              onChanged: (value) {
                if (enabled) onSave({'direction': value});
              },
            ),
          ),
        ],
      ),
    );
  }
}
