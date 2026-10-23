import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/page_activity.dart';
import '../ui/steward_style.dart';
import '../ui/steward_select.dart';
import '../ui/setting_control_row.dart';
import 'help_browser.dart';
import 'profile_controls.dart';
import 'settings_management.dart';
import 'update_content.dart';
import 'device_name_editor.dart';

/// 设置将本机外观、共享业务配置和宿主管理明确分组，设备角色与操作限制持续可见。
class SettingsPage extends StatefulWidget {
  const SettingsPage(
    this.model, {
    this.active = true,
    this.requestedTab,
    this.navigationRevision = 0,
    super.key,
  });
  final WorkbenchModel model;
  final bool active;

  /// 外部更新提醒仅请求切到一个有效设置页签；修订递增才接受同一目标的再次导航。
  final String? requestedTab;
  final int navigationRevision;
  @override
  State<SettingsPage> createState() => _SettingsPageState();
}

class _SettingsPageState extends State<SettingsPage>
    with ActiveResourcePage<SettingsPage> {
  static const destinations = {
    'connection': '连接与设备',
    'appearance': '外观',
    'updates': '更新',
    'diagnostics': '诊断',
    'help': '帮助',
  };
  late String tab = destinations.containsKey(widget.requestedTab)
      ? widget.requestedTab!
      : 'connection';
  // 页面会早于原生初始化创建；保存字段上次实际绑定的值，不能用可变模型的旧引用比较。
  // 与该基线不同的文本是用户草稿，后台初始化、连接切换和轮询都不能覆盖它。
  late String _boundEndpoint = widget.model.endpoint;
  late String _boundDeviceLabel = widget.model.clientLabel;
  late final endpoint = TextEditingController(text: _boundEndpoint);
  final token = TextEditingController();
  String _boundConnectionToken = '';
  String? _restoredTokenEndpoint;
  late final deviceLabel = TextEditingController(text: _boundDeviceLabel);
  bool _editingConnection = false, _submittingConnection = false;
  String? _connectionSubmitError;
  final historyPath = TextEditingController();
  // 设置导航在所有宽度保持顶部；表单仍保留同一身份，窗口缩放只改变约束，
  // 不替换输入子树，避免丢失未保存草稿、输入法组合或当前焦点。
  final _contentKey = GlobalKey(debugLabel: 'settings-form-content');
  WorkbenchModel get model => widget.model;

  @override
  bool get resourcePageActive => widget.active;
  @override
  WorkbenchModel get resourceModel => model;
  @override
  void loadActiveResource() {
    if (!widget.active) return;
    if (tab == 'updates') model.readResource('/updates/status');
    if (tab == 'diagnostics') model.refreshDiagnostics();
    if (tab == 'connection' && model.desktop)
      model.readResource('/local-api/config');
  }

  @override
  void didUpdateWidget(covariant SettingsPage oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.navigationRevision != widget.navigationRevision &&
        destinations.containsKey(widget.requestedTab)) {
      tab = widget.requestedTab!;
      // didUpdateWidget 已会触发 build；资源读取仍推迟至帧结束，不在重建中通知模型。
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted || !widget.active) return;
        model.clearResourceSubscription();
        loadActiveResource();
      });
    }
    _boundEndpoint = _synchronizeConnectionField(
      endpoint,
      _boundEndpoint,
      model.endpoint,
    );
    _boundDeviceLabel = _synchronizeConnectionField(
      deviceLabel,
      _boundDeviceLabel,
      model.clientLabel,
    );
    // 自动回填的凭据只属于打开编辑器时的连接。原生接管或换模型后撤回该副本，
    // 但不覆盖用户手写的下一次草稿；是否提交仍由模型的连接代次门禁决定。
    if (_restoredTokenEndpoint != null &&
        (!identical(oldWidget.model, model) ||
            _restoredTokenEndpoint != model.endpoint ||
            !model.canEditConnection)) {
      _clearRestoredToken();
    }
  }

  /// 仅同步仍跟随模型的字段，或本次已成功提交且提交后未再编辑的完整输入值。
  /// 中文输入法正在组合时不写 controller，避免中断候选词；相同文本也不重写，
  /// 保留用户的光标、选择范围和 composing。Token 不参与后台通知回填，
  /// 仅在用户明确打开修改连接时读取模型当前连接的安全凭据副本。
  String _synchronizeConnectionField(
    TextEditingController controller,
    String bound,
    String latest, {
    TextEditingValue? submitted,
  }) {
    final current = controller.value;
    if (current.text == latest) return latest;
    if (current.composing.isValid && !current.composing.isCollapsed ||
        current.text != bound && current != submitted) {
      return bound;
    }
    controller.value = TextEditingValue(
      text: latest,
      selection: TextSelection.collapsed(offset: latest.length),
    );
    return latest;
  }

  @override
  void dispose() {
    endpoint.dispose();
    token.dispose();
    deviceLabel.dispose();
    historyPath.dispose();
    super.dispose();
  }

  void _select(String next) {
    setState(() => tab = next);
    model.clearResourceSubscription();
    loadActiveResource();
  }

  /// 显式取消只收起编辑器，不删除本机凭据或未提交的 controller 值；再次打开可继续。
  /// 地址/名称的异步回填仍沿用上方“只更新干净字段”的规则。
  void _editConnection() {
    if (!model.canEditConnection) return;
    setState(() => _editingConnection = true);
  }

  /// 明确进入连接编辑时才回显同一地址的安全存储凭据；取消后再次进入保留
  /// 手写草稿和输入法组合。密码默认隐藏，模型通知不能不断覆盖正在编辑的值。
  void _openConnectionEditor() {
    if (!model.canEditConnection || _connectionPending) return;
    if (endpoint.text == model.endpoint &&
        token.text == _boundConnectionToken &&
        (!token.value.composing.isValid || token.value.composing.isCollapsed)) {
      _boundConnectionToken = model.editableConnectionToken;
      _restoredTokenEndpoint = model.endpoint;
      token.value = TextEditingValue(
        text: _boundConnectionToken,
        selection: TextSelection.collapsed(
          offset: _boundConnectionToken.length,
        ),
      );
    }
    _editConnection();
  }

  void _clearRestoredToken() {
    if (token.text == _boundConnectionToken &&
        (!token.value.composing.isValid || token.value.composing.isCollapsed)) {
      token.clear();
      _boundConnectionToken = '';
    }
    _restoredTokenEndpoint = null;
  }

  void _changeConnectionEndpoint(String _) {
    // 地址只要离开回填来源，就不自动把旧电脑的密码带给新电脑。
    // 用户明确手写的新密码不在此处清除；同地址留空复用仍由模型严格核验。
    if (_restoredTokenEndpoint != null &&
        endpoint.text != _restoredTokenEndpoint) {
      _clearRestoredToken();
    }
    _editConnection();
  }

  bool get _connectionPending => _submittingConnection || model.connecting;
  bool get _registeredDevice {
    final id = model.authority['currentDeviceId'];
    return id is String &&
        id.isNotEmpty &&
        records(model.authority['devices'])
                .where((item) => item['deviceId'] == id)
                .length ==
            1;
  }

  bool get _hasConnectionDraft =>
      endpoint.text != model.endpoint ||
      !_registeredDevice && deviceLabel.text != model.clientLabel ||
      token.text != _boundConnectionToken;

  String get _deviceRole =>
      !model.connected || model.authority['currentDeviceIsPrimary'] is! bool
      ? '未确认'
      : model.primary
      ? '主设备'
      : '查看设备';

  Future<void> _submitConnection() async {
    // 原生激活可在编辑期间接管连接；即使旧按钮回调已经被捕获，也不能继续提交。
    if (!model.canEditConnection || _connectionPending || model.busy) return;
    final submittedEndpoint = endpoint.value;
    final submittedLabel = deviceLabel.value;
    final registered = _registeredDevice;
    final connectionLabel = registered
        ? model.clientLabel
        : submittedLabel.text;
    final submittedToken = token.value;
    final requestedModel = model;
    setState(() {
      _submittingConnection = true;
      _connectionSubmitError = null;
    });
    try {
      await requestedModel.connect(
        submittedEndpoint.text,
        submittedToken.text,
        connectionLabel,
      );
      if (!mounted ||
          !identical(requestedModel, model) ||
          !model.connected ||
          model.error.isNotEmpty)
        return;
      // 请求期间允许编辑下一次草稿。只在本次提交后未再编辑且没有中文组合时
      // 收起表单；新草稿或composing保留在原controller上，不能被成功通知清掉。
      final retainEditor =
          endpoint.value != submittedEndpoint ||
          !registered && deviceLabel.value != submittedLabel ||
          token.value != submittedToken ||
          [endpoint, if (!registered) deviceLabel, token].any(
            (controller) =>
                controller.value.composing.isValid &&
                !controller.value.composing.isCollapsed,
          );
      _boundEndpoint = _synchronizeConnectionField(
        endpoint,
        _boundEndpoint,
        model.endpoint,
        submitted: submittedEndpoint,
      );
      _boundDeviceLabel = _synchronizeConnectionField(
        deviceLabel,
        _boundDeviceLabel,
        model.clientLabel,
        submitted: submittedLabel,
      );
      if (token.value == submittedToken &&
          (token.value.composing.isCollapsed ||
              !token.value.composing.isValid)) {
        token.clear();
        _boundConnectionToken = '';
        _restoredTokenEndpoint = null;
      }
      _editingConnection = retainEditor;
    } catch (cause) {
      // 生产模型将错误转为可见状态；测试或其它实现抛出时也必须保留草稿。
      // 不把本次输入的安全凭据拼入错误提示。
      if (mounted)
        _connectionSubmitError = submittedToken.text.isEmpty
            ? cause.toString()
            : cause.toString().replaceAll(submittedToken.text, '[已隐藏]');
    } finally {
      if (mounted) setState(() => _submittingConnection = false);
    }
  }

  Widget _connectionPanel() {
    final registered = _registeredDevice;
    final editing =
        model.canEditConnection &&
        (!model.connected || _editingConnection || _submittingConnection);
    final source = switch (model.connectionSource) {
      ConnectionSource.launchArguments => '启动参数提供',
      ConnectionSource.saved => '本机安全存储恢复',
      ConnectionSource.manual => '手动保存',
      ConnectionSource.preview => '离线合成预览',
      ConnectionSource.unknown => '未记录',
    };
    return Section(model.connected ? '当前连接' : '连接游戏电脑', [
      if (model.connected || !model.canEditConnection) ...[
        Fact('连接状态', model.connected ? '已连接' : '未连接'),
        Fact('API 地址', model.endpoint),
        Fact('本设备名称', model.clientLabel),
        Fact('本设备角色', _deviceRole),
        Fact('连接来源', source),
      ],
      if (editing) ...[
        if (model.connected) const SizedBox(height: 12),
        StewardField(
          'API 地址',
          controller: endpoint,
          onChanged: _changeConnectionEndpoint,
        ),
        const SizedBox(height: 8),
        SettingsSecretField(
          '连接 Token',
          controller: token,
          onChanged: (_) => _editConnection(),
        ),
        const SizedBox(height: 8),
        const _SettingsNote('同一地址留空 Token 会沿用已保存凭据；更换地址时请填写该电脑的 Token。'),
        const SizedBox(height: 8),
        // 已注册设备改名必须走独立 rename 回执，不能仅靠重连接修改请求头。
        if (!registered)
          StewardField(
            '设备名称',
            controller: deviceLabel,
            onChanged: (_) => _editConnection(),
          ),
        ToolRow([
          StewardButton(
            _connectionPending
                ? '正在连接…'
                : model.connected
                ? '保存并重新连接'
                : '保存并连接',
            primary: true,
            onPressed: _connectionPending || model.busy
                ? null
                : _submitConnection,
          ),
          if (model.connected)
            StewardButton(
              '取消修改',
              onPressed: _connectionPending
                  ? null
                  : () {
                      FocusManager.instance.primaryFocus?.unfocus();
                      setState(() => _editingConnection = false);
                    },
            ),
        ]),
      ] else ...[
        ToolRow([
          if (model.canEditConnection)
            StewardButton(
              '修改连接',
              onPressed: _connectionPending ? null : _openConnectionEditor,
            ),
          StewardButton(
            _connectionPending
                ? '正在连接…'
                : model.connected
                ? '刷新'
                : '重试连接',
            onPressed: model.busy || _connectionPending
                ? null
                : model.connected
                ? model.refresh
                : model.retryConnection,
          ),
        ]),
      ],
    ]);
  }

  /// 页面状态只发布当前设置分区的信息。字段帮助、设备事实和危险操作说明仍在
  /// 原控件附近；错误完整内容交给页头详情，不再占用表单的独立段落。
  PageActivity? get _settingsActivity {
    if (!widget.active) return null;
    if (tab == 'connection') {
      final error = _connectionSubmitError ?? model.error;
      if (error.isNotEmpty) {
        return PageActivity(message: '连接失败', details: error, error: true);
      }
      if (_connectionPending) {
        return const PageActivity(message: '正在连接…', busy: true);
      }
      if (!model.connected) {
        return PageActivity(
          message: '未连接',
          details: model.canEditConnection
              ? '请核对游戏电脑地址与凭据后连接。'
              : '当前连接由启动参数管理，可使用“重试连接”沿用原参数重试。',
        );
      }
      final currentId = model.authority['currentDeviceId'];
      if (currentId is! String ||
          currentId.isEmpty ||
          records(model.authority['devices'])
                  .where((device) => device['deviceId'] == currentId)
                  .length !=
              1) {
        return const PageActivity(
          message: '本设备身份尚未确认',
          details: '设备身份未确认前不会开放依赖本设备身份的管理操作。',
        );
      }
      if (model.canEditConnection &&
          !_editingConnection &&
          _hasConnectionDraft) {
        return const PageActivity(
          message: '有未提交的连接草稿',
          details: '重新打开修改连接后可继续，当前连接不受影响。',
        );
      }
    }
    return null;
  }

  @override
  Widget build(BuildContext context) {
    synchronizeResourceActivation();
    // 各分组只在下方正文中独立滚动；帮助继续使用它自己的文档目录与正文布局，
    // 不将帮助目录误作设置导航，也不因设置宽度变化切换表单的父级结构。
    final content = Align(
      key: _contentKey,
      alignment: Alignment.topCenter,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 880),
        child: tab == 'help'
            ? const HelpBrowser()
            : ListView(
                key: PageStorageKey('settings-$tab'),
                padding: const EdgeInsets.only(right: 8, bottom: 20),
                children: [
                  ...switch (tab) {
                    'connection' => _connection(context),
                    'appearance' => _appearance(),
                    'updates' => _updates(context),
                    'diagnostics' => _diagnostics(),
                    _ => const <Widget>[],
                  },
                ],
              ),
      ),
    );
    return PageActivityReporter(
      activity: _settingsActivity,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          WorkspaceTabs<String>(
            horizontalScroll: !model.desktop,
            items: destinations,
            value: tab,
            onChanged: _select,
          ),
          // 主页签已经统一提供正文间距；此处不能再叠加页面专属留白，
          // 否则与推荐、数据和扩展等同级页面的内容起点不一致。
          Expanded(child: content),
        ],
      ),
    );
  }

  List<Widget> _connection(BuildContext context) {
    final devices = records(model.authority['devices']);
    final currentId = model.authority['currentDeviceId'];
    // 本设备是服务端当前连接的精确deviceId，不按名称、平台、主设备角色或行顺序猜测。
    // 缺失或重复身份只展示未知，并禁用可能误删本设备的操作。
    final currentKnown =
        currentId is String &&
        currentId.isNotEmpty &&
        devices.where((device) => device['deviceId'] == currentId).length == 1;
    bool isCurrent(DataMap device) =>
        currentKnown && device['deviceId'] == currentId;
    bool hasUniqueId(DataMap device) =>
        device['deviceId'] is String &&
        (device['deviceId'] as String).isNotEmpty &&
        devices
                .where((entry) => entry['deviceId'] == device['deviceId'])
                .length ==
            1;
    return [
      _connectionPanel(),
      Section('设备与共享配置', [
        DeviceNameEditor(model),
        const SizedBox(height: 8),
        Fact('本设备角色', _deviceRole),
        Fact('生效配置版本', model.authority['activeProfileRevision']),
        for (final device in devices)
          CollapsibleSection(
            '${label(device['label'])}${isCurrent(device) ? ' · 本设备' : ''} · ${device['isPrimary'] == true
                ? '主设备'
                : device['isPrimary'] == false
                ? '查看设备'
                : '角色未确认'}',
            [
              Fact(
                '平台 / 在线',
                '${label(device['platform'])} · ${device['online'] == true
                    ? '在线'
                    : device['online'] == false
                    ? '离线'
                    : '未确认'}',
              ),
              Fact('客户端版本', device['appVersion']),
              Fact('设备配置版本', device['profileRevision']),
              Fact(
                '同步状态',
                device['syncPending'] == true
                    ? '等待落盘确认'
                    : device['syncPending'] == false
                    ? '已确认'
                    : '未确认',
              ),
              ToolRow([
                StewardButton(
                  '设为主设备',
                  onPressed:
                      model.connected &&
                          !model.busy &&
                          hasUniqueId(device) &&
                          device['isPrimary'] == false &&
                          device['online'] == true &&
                          device['syncPending'] == false &&
                          device['profileRevision'] is int &&
                          device['appliedProfileRevision'] ==
                              device['profileRevision']
                      ? () async {
                          if (await confirmCurrentAction(
                            context,
                            model,
                            '切换主设备',
                            '将 ${label(device['label'])} 设为主设备。旧主设备将停止发出自动化指令；新主设备完成配置同步后，按其已保存的自动化开关运行。',
                          ))
                            await model.mutate(
                              '/devices/primary',
                              body: {'deviceId': device['deviceId']},
                            );
                        }
                      : null,
                ),
                StewardButton(
                  '同步主配置',
                  onPressed:
                      model.connected &&
                          !model.busy &&
                          hasUniqueId(device) &&
                          device['isPrimary'] == false
                      ? () => model.mutate(
                          '/devices/sync',
                          body: {'deviceId': device['deviceId']},
                        )
                      : null,
                ),
                StewardButton(
                  '忘记设备',
                  onPressed: _canForgetDevice(device['deviceId'])
                      ? () => _forgetDevice(device['deviceId'] as String)
                      : null,
                ),
              ]),
            ],
            // 使用精确身份保持展开状态，后台列表排序改变时不能把操作区移交给另一设备。
            key: hasUniqueId(device)
                ? ValueKey('device-${device['deviceId']}')
                : null,
            initiallyExpanded: isCurrent(device),
          ),
      ]),
      if (model.desktop) SettingsLanSection(model),
      CollapsibleSection('历史偏好迁移', [
        const _SettingsNote(
          '导入一次性导出的历史偏好 JSON，迁移显示、窗口及扩展模块开关；不导入旧设备身份。自动化使用当前共享配置，收藏和配方保留在 Mod 数据中。',
        ),
        StewardField('历史偏好文件路径', controller: historyPath),
        ToolRow([
          StewardButton(
            '导入偏好',
            onPressed: model.busy
                ? null
                : () => model.importHistory(historyPath.text),
          ),
        ]),
      ]),
    ];
  }

  /// 删除依据当前服务端身份重新判断，不依赖展开时捕获的旧设备记录。
  /// 本设备身份必须唯一且明确，离线查看设备才可移除；主设备与在线设备保持保护。
  bool _canForgetDevice(Object? id) {
    if (!model.canEdit || id is! String || id.isEmpty) return false;
    final devices = records(model.authority['devices']);
    final current = model.authority['currentDeviceId'];
    if (current is! String ||
        current.isEmpty ||
        current == id ||
        devices.where((entry) => entry['deviceId'] == current).length != 1)
      return false;
    final matches = devices.where((entry) => entry['deviceId'] == id).toList();
    return matches.length == 1 &&
        matches.single['isPrimary'] == false &&
        matches.single['online'] == false;
  }

  Future<void> _forgetDevice(String id) async {
    if (!_canForgetDevice(id)) return;
    final requestedModel = model;
    final device = records(model.authority['devices'])
        .singleWhere((entry) => entry['deviceId'] == id);
    if (!await confirmCurrentAction(
      context,
      requestedModel,
      '忘记设备',
      '移除 ${label(device['label'])} 的设备记录，不会删除游戏存档。本设备、主设备和在线设备不能移除。',
    ))
      return;
    if (!mounted || !identical(requestedModel, model) || !_canForgetDevice(id))
      return;
    await requestedModel.mutate('/devices/forget', body: {'deviceId': id});
  }

  List<Widget> _appearance() => [
    Section('显示', [
      Padding(
        padding: StewardDensity.settingPadding,
        child: SettingControlRow(
          title: '主题',
          maxControlWidth: stewardSelectWidth,
          control: StewardSelect<String>(
            label: '主题',
            showLabel: false,
            items: const {'system': '跟随系统', 'light': '浅色', 'dark': '深色'},
            value: model.themeMode,
            onChanged: (value) => model.updateLocal({'themeMode': value}),
          ),
        ),
      ),
      SettingToggle(
        '减少动效',
        model.reduceMotion,
        (value) => model.updateLocal({'reduceMotion': value}),
      ),
      _localNumber('字体缩放', 'fontScalePercent', 100, 90, 150),
      SettingToggle(
        '手柄导航',
        model.localPreferences['gamepadNavigation'] != false,
        (value) => model.updateLocal({'gamepadNavigation': value}),
      ),
      if (model.desktop) ...[
        const _SettingsNote('背景不透明度影响页面和控件填充；内容不透明度影响文字、图标与边框。'),
        _localNumber('背景不透明度', 'backgroundOpacityPercent', 100, 20, 100),
        _localNumber('内容不透明度', 'contentOpacityPercent', 100, 35, 100),
      ],
    ]),
    if (model.desktop)
      Section('桌面窗口', [
        for (final entry in const {
          'alwaysOnTop': '窗口置顶',
          'mousePassthrough': '鼠标穿透（F10 恢复）',
          'keepVisible': '切回游戏时保持可见',
        }.entries)
          SettingToggle(
            entry.value,
            model.localPreferences[entry.key] == true,
            (value) => model.updateLocal({entry.key: value}),
          ),
        _localNumber('焦点切换冷却', 'switchCooldownMs', 800, 250, 2000),
        ToolRow([
          StewardButton(
            '切换焦点',
            onPressed: () => model.platformCommand('focus'),
          ),
          StewardButton(
            '隐藏到托盘',
            onPressed: () => model.platformCommand('hide'),
          ),
        ]),
        CollapsibleSection('结束本次客户端', [
          const _SettingsNote(
            '完整退出会断开连接并停止由本设备驱动的自动化，已保存的配置仍会保留。隐藏到托盘会保留当前客户端。',
          ),
          ToolRow([
            StewardButton(
              '退出客户端',
              onPressed: () => model.platformCommand('exit'),
            ),
          ]),
        ]),
      ]),
  ];
  Widget _localNumber(
    String title,
    String key,
    int fallback,
    int min,
    int max,
  ) => IntegerSetting(
    key: ValueKey(key),
    title: title,
    unit: key == 'switchCooldownMs' ? ' ms' : '%',
    continuous: true,
    value: (model.localPreferences[key] as num?)?.toInt() ?? fallback,
    min: min,
    max: max,
    onSave: (value) => model.updateLocal({key: value}),
  );
  List<Widget> _updates(BuildContext context) => [
    SettingsUpdatesSection(model),
  ];

  List<Widget> _diagnostics() {
    final logs = model.resources['/logs/settings'] ?? const <String, Object?>{};
    return [
      Section(
        '日志与诊断',
        [
          SettingToggle(
            '聚合 Mod 日志',
            logs['aggregateModLogEnabled'] == true,
            model.canEdit
                ? (value) => model.mutate(
                    '/logs/config',
                    query: {'aggregateLog': '$value'},
                    localOnly: true,
                  )
                : null,
          ),
          IntegerSetting(
            key: const ValueKey('aggregateModLogMaxFileCount'),
            title: '日志保留文件数',
            unit: ' 个',
            resetKey: profileControlBoundary(model),
            value: (logs['aggregateModLogMaxFileCount'] as num?)?.toInt() ?? 30,
            min: 1,
            max: 30,
            onSave: model.canEdit && model.desktop
                ? (value) => model.mutate(
                    '/logs/config',
                    query: {'aggregateLogMaxFiles': '$value'},
                    localOnly: true,
                  )
                : null,
          ),
          SettingToggle(
            'BepInEx 控制台',
            logs['bepInExConsoleVisible'] == true,
            model.canEdit &&
                    model.desktop &&
                    logs['bepInExConsoleSupported'] == true
                ? (value) => model.mutate(
                    '/logs/console',
                    query: {'visible': '$value'},
                    localOnly: true,
                  )
                : null,
          ),
          ToolRow([
            StewardButton(
              '打开日志目录',
              onPressed: model.canEdit && model.desktop
                  ? () => model.mutate('/logs/open-folder', localOnly: true)
                  : null,
            ),
            StewardButton(
              '导出诊断包',
              onPressed: model.canEdit && model.desktop
                  ? () => model.mutate(
                      '/logs/export-diagnostics',
                      query: {'open': 'true'},
                      localOnly: true,
                    )
                  : null,
            ),
          ]),
          Fact('最近错误', model.error),
        ],
        titleContent: Row(
          children: [
            const Expanded(
              child: Text('日志与诊断', style: StewardText.sectionTitle),
            ),
            const SizedBox(width: 8),
            StewardButton(
              '刷新诊断',
              onPressed: model.connected && !model.busy
                  ? model.refreshDiagnostics
                  : null,
            ),
          ],
        ),
      ),
      SettingsDiagnosticPackages(model),
      SettingsRecentOperations(model),
      CollapsibleSection('运行详情与目录', [
        Fact('日志目录', logs['aggregateModLogDirectory']),
        Fact('控制台状态', logs['bepInExConsoleStatus']),
        Fact('快照时间', model.snapshot['capturedAtUtc']),
        Fact('运行时状态', model.snapshot['runtimeSceneReadinessStatus']),
        Fact('业务计算', model.business['calculationMs']),
      ]),
    ];
  }
}

/// 设置中的范围、保存规则与操作说明采用统一辅助字号，和任务/邀请字段标签同层级。
/// 只保留字段约束与危险操作所需说明，不截断、不缩小控件或改变表单/焦点节点。
/// 保存与连接状态通过页头完整详情呈现，避免同一错误在每段表单反复占用空间。
class _SettingsNote extends StatelessWidget {
  const _SettingsNote(this.text);
  final String text;

  @override
  Widget build(BuildContext context) => Text(
    text,
    style: StewardText.caption.copyWith(
      color: context.theme.colors.mutedForeground,
    ),
  );
}
