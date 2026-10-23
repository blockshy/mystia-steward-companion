import 'package:flutter/widgets.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';

/// 设备名是注册表元数据，不属于启动连接的地址/凭据。编辑始终提交独立 rename，
/// 服务端确认后才更新本机显示；游戏启动参数锁不会禁用此操作。
class DeviceNameEditor extends StatefulWidget {
  const DeviceNameEditor(this.model, {super.key});
  final WorkbenchModel model;
  @override
  State<DeviceNameEditor> createState() => _DeviceNameEditorState();
}

class _DeviceNameEditorState extends State<DeviceNameEditor> {
  final _controller = TextEditingController();
  Object? _owner;
  String _bound = '';
  bool _saving = false;
  WorkbenchModel get model => widget.model;
  Object get _identity => (
    model,
    model.connectionVersion,
    model.authority['registryId'],
    model.authority['currentDeviceId'],
  );
  DataMap? get _device {
    final current = model.authority['currentDeviceId'];
    if (current is! String || current.isEmpty) return null;
    final matching = records(model.authority['devices'])
        .where((entry) => entry['deviceId'] == current)
        .toList();
    return matching.length == 1 ? matching.single : null;
  }

  @override
  void initState() {
    super.initState();
    _synchronize();
  }

  @override
  void didUpdateWidget(covariant DeviceNameEditor oldWidget) {
    super.didUpdateWidget(oldWidget);
    _synchronize();
  }

  void _synchronize() {
    final name = label(_device?['label'], '');
    final changedOwner = _owner != _identity;
    if (changedOwner ||
        _controller.text == _bound && !_controller.value.composing.isValid) {
      if (_controller.text != name)
        _controller.value = TextEditingValue(
          text: name,
          selection: TextSelection.collapsed(offset: name.length),
        );
    }
    _bound = name;
    _owner = _identity;
  }

  bool get _allowed =>
      model.connected &&
      !model.busy &&
      !_saving &&
      _device != null &&
      _controller.text.trim().isNotEmpty &&
      _controller.text.trim().length <= 48 &&
      !_controller.text.contains(RegExp(r'[\x00-\x1f\x7f-\x9f]')) &&
      _controller.text.trim() != _bound;
  Future<void> _save() async {
    if (!_allowed) return;
    final owner = _identity;
    final submitted = _controller.value;
    final target = model;
    setState(() => _saving = true);
    try {
      await target.renameDevice(submitted.text.trim());
      if (!mounted || owner != _identity) return;
      // 保存期间用户继续输入时保留新草稿；失败也保留原输入，不伪造服务端已改名。
      final actual = label(_device?['label'], '');
      if (_controller.value == submitted && actual == submitted.text.trim()) {
        _controller.value = TextEditingValue(
          text: actual,
          selection: TextSelection.collapsed(offset: actual.length),
        );
      }
      _bound = actual;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Row(
    children: [
      Expanded(
        child: StewardField(
          '本设备名称',
          controller: _controller,
          showLabel: false,
          onChanged: (_) => setState(() {}),
        ),
      ),
      const SizedBox(width: 8),
      StewardButton(
        _saving ? '正在保存…' : '保存名称',
        onPressed: _allowed ? _save : null,
      ),
    ],
  );
}
