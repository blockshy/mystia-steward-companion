import 'package:flutter/services.dart' show Clipboard, ClipboardData;
import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' show SelectableText;

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/page_activity.dart';
import '../ui/steward_select.dart';
import '../ui/steward_style.dart';

/// 设置页的密码输入共用原有输入尺寸和控制器，只切换绘制方式。
/// 不把密码放进标题、提示或语义名称；显示状态局限于当前编辑器，销毁后恢复隐藏。
class SettingsSecretField extends StatefulWidget {
  const SettingsSecretField(
    this.title, {
    required this.controller,
    this.onChanged,
    this.enabled = true,
    super.key,
  });
  final String title;
  final TextEditingController controller;
  final ValueChanged<String>? onChanged;
  final bool enabled;

  @override
  State<SettingsSecretField> createState() => _SettingsSecretFieldState();
}

class _SettingsSecretFieldState extends State<SettingsSecretField> {
  bool _visible = false;

  @override
  void didUpdateWidget(covariant SettingsSecretField oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!widget.enabled || oldWidget.controller != widget.controller) {
      _visible = false;
    }
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Text(widget.title, style: StewardText.body),
      const SizedBox(height: 4),
      Row(
        children: [
          Expanded(
            child: ExcludeFocus(
              excluding: !widget.enabled,
              child: IgnorePointer(
                ignoring: !widget.enabled,
                child: StewardField(
                  widget.title,
                  showLabel: false,
                  controller: widget.controller,
                  secret: !_visible,
                  onChanged: widget.onChanged,
                ),
              ),
            ),
          ),
          const SizedBox(width: 6),
          StewardIconButton(
            label: '${_visible ? '隐藏' : '显示'}${widget.title}',
            icon: _visible ? FLucideIcons.eyeOff : FLucideIcons.eye,
            onPressed: widget.enabled
                ? () => setState(() => _visible = !_visible)
                : null,
          ),
        ],
      ),
    ],
  );
}

/// 局域网管理始终展开。网卡来自当前宿主的实际枚举结果，不能把任意文本或
/// 已失效的旧绑定地址伪装成可用网卡；自动选择仍由宿主从实际网卡中决定。
class SettingsLanSection extends StatefulWidget {
  const SettingsLanSection(this.model, {super.key});
  final WorkbenchModel model;

  @override
  State<SettingsLanSection> createState() => _SettingsLanSectionState();
}

class _SettingsLanSectionState extends State<SettingsLanSection>
    with AutomaticKeepAliveClientMixin<SettingsLanSection> {
  final _token = TextEditingController();
  String? _host, _boundHost;
  String _boundToken = '';
  bool _enabled = false, _boundEnabled = false, _saving = false;
  String? _validation;
  late Object _owner;

  WorkbenchModel get model => widget.model;
  DataMap get _config => model.resources['/local-api/config'] ?? const {};
  Object get _connectionOwner =>
      (model, model.connectionVersion, model.endpoint);
  bool get _loaded =>
      _config['lanEnabled'] is bool && _config['lanBindHost'] is String;
  bool get _editable => model.desktop && model.canEdit && _loaded && !_saving;
  @override
  bool get wantKeepAlive => true;

  @override
  void initState() {
    super.initState();
    _owner = _connectionOwner;
    _synchronize();
  }

  @override
  void didUpdateWidget(covariant SettingsLanSection oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (_owner != _connectionOwner) {
      // LAN 草稿和服务端密码属于精确连接；替换宿主时不得继续显示上一台电脑密码。
      _owner = _connectionOwner;
      _host = _boundHost = null;
      _token.clear();
      _boundToken = '';
      _enabled = _boundEnabled = false;
      _validation = null;
    }
    _synchronize();
  }

  void _synchronize() {
    if (!_loaded) return;
    final host = _config['lanBindHost'] as String;
    if (_host == _boundHost) _host = host;
    _boundHost = host;
    final enabled = _config['lanEnabled'] == true;
    if (_enabled == _boundEnabled) _enabled = enabled;
    _boundEnabled = enabled;
    final value = _config['token'];
    if (value is String) {
      if (_token.text == value) {
        // 相同文本只更新绑定基线，绝不重写 selection/composing 或明文显示状态。
        _boundToken = value;
      } else if (_token.text == _boundToken &&
          (!_token.value.composing.isValid ||
              _token.value.composing.isCollapsed)) {
        _token.value = TextEditingValue(
          text: value,
          selection: TextSelection.collapsed(offset: value.length),
        );
        _boundToken = value;
      }
      // 用户草稿或输入法组合期间保留上次真正绑定的基线；后续通知仍有机会
      // 识别干净输入，不能仅因服务端刷新就把尚未同步的值标成已经绑定。
    }
  }

  Map<String, String> get _addresses {
    final entries = records(_config['availableLanEndpoints']);
    final counts = <String, int>{};
    for (final entry in entries) {
      final address = entry['address'];
      if (address is String && address.isNotEmpty) {
        counts[address] = (counts[address] ?? 0) + 1;
      }
    }
    return {
      'auto': '自动（全部网卡）',
      for (final entry in entries)
        if (entry['address'] is String && counts[entry['address']] == 1)
          entry['address'] as String:
              '${entry['address']} · ${label(entry['interfaceName'], '网卡')}',
    };
  }

  Future<void> _save() async {
    if (!_editable || !_addresses.containsKey(_host)) return;
    final requestedModel = model;
    final owner = _connectionOwner;
    final tokenValue = _token.value;
    final changedToken = tokenValue.text != _boundToken;
    if (changedToken && !RegExp(r'^[!-~]{8,128}$').hasMatch(tokenValue.text)) {
      setState(() => _validation = 'Token 需为 8–128 位英文、数字或符号，不含空格。');
      return;
    }
    final host = _host!;
    final enabled = _enabled;
    final original = (
      _config['lanBindHost'],
      _config['lanEnabled'],
      _config['token'],
    );
    setState(() {
      _saving = true;
      _validation = null;
    });
    try {
      if (changedToken &&
          !await confirmCurrentAction(
            context,
            requestedModel,
            '保存连接 Token',
            '当前客户端会使用新 Token 继续连接；其他设备需要填写新 Token 后重新连接。',
          ))
        return;
      // 确认仅授权本次草稿。宿主、权限、实际网卡或服务端配置发生变化均取消，
      // 不把旧弹窗确认转交给后来的模型，也不自动重放失败/结果未知的保存请求。
      if (!mounted ||
          owner != _connectionOwner ||
          !model.canEdit ||
          !_addresses.containsKey(host) ||
          original !=
              (
                _config['lanBindHost'],
                _config['lanEnabled'],
                _config['token'],
              ) ||
          _token.value != tokenValue ||
          _host != host ||
          _enabled != enabled)
        return;
      await requestedModel.updateLanConnection(
        enabled: enabled,
        host: host,
        token: changedToken ? tokenValue.text : null,
      );
    } catch (_) {
      // 生产模型会发布经过脱敏的错误；其它实现若直接抛出，也不允许把
      // 异常中的 URL、body 或用户密码回显到页面。保留草稿供用户核对。
      if (mounted && owner == _connectionOwner) {
        setState(() => _validation = '服务配置保存未完成，请核对当前状态后重试。');
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  void dispose() {
    _token.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    super.build(context);
    final addresses = _addresses;
    final validHost = addresses.containsKey(_host);
    final error =
        _validation ?? (_loaded && !validHost ? '原绑定地址不在当前网卡列表中，请重新选择。' : null);
    return PageActivityReporter(
      activity: error == null
          ? null
          : PageActivity(message: '局域网配置待检查', details: error, error: true),
      child: Section(
        '局域网服务',
        [
          Fact(
            '监听状态',
            !_loaded
                ? '尚未读取'
                : _config['lanRunning'] == true
                ? '正在监听'
                : _config['lanEnabled'] == true
                ? '已启用但监听失败'
                : '未开启',
          ),
          if (label(_config['lanError'], '').isNotEmpty)
            Fact('监听错误', _config['lanError']),
          for (final entry in records(_config['lanEndpoints']))
            Fact(label(entry['interfaceName'], '连接地址'), entry['endpoint']),
          SettingToggle(
            '启用局域网服务',
            _enabled,
            _editable ? (value) => setState(() => _enabled = value) : null,
          ),
          StewardSelect<String>(
            key: const ValueKey('lan-address-select'),
            label: '绑定网卡',
            items: addresses,
            value: validHost ? _host : null,
            hint: '请选择当前可用网卡',
            // 网卡地址和名称共同构成可辨识信息；弹层独立加宽并允许完整换行，
            // 不把窗口内已存在的表单宽度限制误套为普通 152px 业务选择器。
            popoverWidth: 360,
            wrapItems: true,
            enabled: _editable,
            boundary: (_connectionOwner, model.canEdit),
            onChanged: (value) {
              if (!_editable || !_addresses.containsKey(value)) return;
              setState(() {
                _host = value;
                _validation = null;
              });
            },
          ),
          const SizedBox(height: 8),
          SettingsSecretField(
            '局域网 Token',
            controller: _token,
            enabled: _editable,
            onChanged: (_) => setState(() => _validation = null),
          ),
          const SizedBox(height: 4),
          const Text('8–128 位英文、数字或符号，不含空格。', style: StewardText.caption),
          ToolRow([
            StewardButton(
              _saving ? '正在保存…' : '保存服务配置',
              primary: true,
              onPressed: _editable && validHost ? _save : null,
            ),
            StewardButton(
              '复制连接 Token',
              onPressed: model.connected && _config['token'] is String
                  ? () => Clipboard.setData(
                      ClipboardData(text: _config['token'] as String),
                    )
                  : null,
            ),
          ]),
        ],
        titleContent: Row(
          children: [
            const Expanded(
              child: Text('局域网服务', style: StewardText.sectionTitle),
            ),
            const SizedBox(width: 8),
            StewardButton(
              '刷新配置',
              onPressed: model.connected && !_saving && !model.busy
                  ? () => model.readResource('/local-api/config')
                  : null,
            ),
          ],
        ),
      ),
    );
  }
}

/// 诊断包只接受服务端给出的不可猜测身份，不从文件名拼接路径。确认等待期间
/// 列表内容变化、换连接或失去主设备权限时取消，实际删除仍由宿主精确核验文件版本。
class SettingsDiagnosticPackages extends StatefulWidget {
  const SettingsDiagnosticPackages(this.model, {super.key});
  final WorkbenchModel model;
  @override
  State<SettingsDiagnosticPackages> createState() =>
      _SettingsDiagnosticPackagesState();
}

class _SettingsDiagnosticPackagesState
    extends State<SettingsDiagnosticPackages> {
  bool _deleting = false;
  WorkbenchModel get model => widget.model;
  DataMap get _packageState => model.resources['/logs/packages'] ?? const {};
  String get _readError {
    final error = label(_packageState['error'], '');
    return error.isNotEmpty
        ? error
        : _packageState['ok'] == false
        ? '诊断包读取失败，请刷新重试。'
        : '';
  }

  // 读取失败代表当前列表未经确认，不能把 items:[] 当作成功空列表；即使某个
  // 实现保留了旧 items，也撤下删除入口，直到刷新得到新一轮成功的文件身份。
  List<DataMap> get _items =>
      _readError.isEmpty ? records(_packageState['items']) : const [];

  Future<void> _delete(DataMap item) async {
    final id = item['id'];
    if (_deleting || !model.canEdit || id is! String || id.isEmpty) return;
    final requestedModel = model;
    final original = (
      id,
      item['fileName'],
      item['byteLength'],
      item['createdAtUtc'],
    );
    setState(() => _deleting = true);
    try {
      if (!await confirmCurrentAction(
        context,
        requestedModel,
        '删除诊断包',
        '删除 ${label(item['fileName'])}。原始日志和游戏存档不会删除。',
      ))
        return;
      final matches = _items.where((entry) => entry['id'] == id).toList();
      if (!mounted ||
          !identical(requestedModel, model) ||
          !model.canEdit ||
          matches.length != 1 ||
          original !=
              (
                matches.single['id'],
                matches.single['fileName'],
                matches.single['byteLength'],
                matches.single['createdAtUtc'],
              ))
        return;
      await requestedModel.mutate('/logs/packages/delete', body: {'id': id});
    } finally {
      if (mounted) setState(() => _deleting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final items = _items;
    final error = _readError;
    return PageActivityReporter(
      activity: error.isEmpty
          ? null
          : PageActivity(
              message: '诊断包读取失败',
              details: '$error\n请使用“刷新列表”重新读取。',
              error: true,
            ),
      child: Section(
        '诊断包',
        [
          if (error.isNotEmpty)
            const Text('诊断包读取失败')
          else if (items.isEmpty)
            Text(
              model.resources.containsKey('/logs/packages')
                  ? '暂无诊断包'
                  : '尚未读取诊断包',
            ),
          for (final item in items)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 6),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  SelectableText(
                    label(item['fileName']),
                    style: StewardText.body,
                  ),
                  Wrap(
                    spacing: 12,
                    runSpacing: 4,
                    crossAxisAlignment: WrapCrossAlignment.center,
                    children: [
                      Text(
                        _fileSize(item['byteLength']),
                        style: StewardText.caption,
                      ),
                      Text(
                        _localTime(item['createdAtUtc']),
                        style: StewardText.caption,
                      ),
                      StewardButton(
                        '删除',
                        key: ValueKey('delete-package-${item['id']}'),
                        onPressed:
                            model.canEdit &&
                                !_deleting &&
                                item['id'] is String &&
                                (item['id'] as String).isNotEmpty &&
                                items
                                        .where(
                                          (entry) => entry['id'] == item['id'],
                                        )
                                        .length ==
                                    1
                            ? () => _delete(item)
                            : null,
                      ),
                    ],
                  ),
                ],
              ),
            ),
        ],
        titleContent: Row(
          children: [
            const Expanded(child: Text('诊断包', style: StewardText.sectionTitle)),
            const SizedBox(width: 8),
            StewardButton(
              '刷新列表',
              onPressed: model.connected && !_deleting && !model.busy
                  ? () => model.readResource('/logs/packages')
                  : null,
            ),
          ],
        ),
      ),
    );
  }
}

/// 只展示模型记录的已完成操作，不把瞬时 notice 伪装成历史。记录限本次客户端运行，
/// 模型只提供脱敏摘要；这里不从资源请求或当前错误反推过去发生了什么。
class SettingsRecentOperations extends StatelessWidget {
  const SettingsRecentOperations(this.model, {super.key});
  final WorkbenchModel model;
  @override
  Widget build(BuildContext context) => Section('最近操作 · 本次运行', [
    if (model.recentOperations.isEmpty) const Text('本次运行暂无操作记录'),
    for (final entry in model.recentOperations)
      Padding(
        key: ValueKey('operation-${entry.id}'),
        padding: const EdgeInsets.symmetric(vertical: 5),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // 名称与状态采用不同字号，沿公共工具行规则居中，避免小字贴到行顶。
            Wrap(
              spacing: 8,
              runSpacing: 4,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                Text(entry.name, style: StewardText.body),
                Text(switch (entry.result) {
                  WorkbenchOperationResult.success => '成功',
                  WorkbenchOperationResult.failed => '失败',
                  WorkbenchOperationResult.unknown => '结果待确认',
                }, style: StewardText.caption),
                Text(
                  _localTime(entry.completedAt.toIso8601String()),
                  style: StewardText.caption,
                ),
              ],
            ),
            if (entry.summary.isNotEmpty)
              SelectableText(entry.summary, style: StewardText.caption),
          ],
        ),
      ),
  ]);
}

String _localTime(Object? value) {
  final parsed = value is String ? DateTime.tryParse(value)?.toLocal() : null;
  if (parsed == null) return '时间未提供';
  String two(int number) => number.toString().padLeft(2, '0');
  return '${parsed.year}-${two(parsed.month)}-${two(parsed.day)} ${two(parsed.hour)}:${two(parsed.minute)}:${two(parsed.second)}';
}

String _fileSize(Object? value) {
  if (value is! int || value < 0) return '大小未提供';
  return value < 1024
      ? '$value B'
      : value < 1024 * 1024
      ? '${(value / 1024).toStringAsFixed(1)} KiB'
      : '${(value / (1024 * 1024)).toStringAsFixed(1)} MiB';
}
