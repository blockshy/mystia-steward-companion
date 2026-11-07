import 'package:flutter/widgets.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/steward_style.dart';

/// 存储操作独立于游戏连接和主设备权限。原生层持有固定目录及磁盘准入条件；
/// 页面只展示状态并收集首次选择，不接受任意源目录，不自行复制或删除资料。
class SettingsStorageSection extends StatefulWidget {
  const SettingsStorageSection(this.model, {super.key});
  final WorkbenchModel model;

  @override
  State<SettingsStorageSection> createState() => _SettingsStorageSectionState();
}

class _SettingsStorageSectionState extends State<SettingsStorageSection> {
  bool _showLocation = false;

  Future<void> _initialize(bool importLegacy) async {
    final model = widget.model;
    if (model.busy ||
        model.connecting ||
        model.storageStatus?.needsInitialization != true)
      return;
    if (!await confirmAction(
      context,
      importLegacy ? '导入旧 Flutter 数据' : '作为新设备开始',
      importLegacy
          ? '仅从当前 Windows 用户的旧 Flutter 目录导入偏好、窗口位置和连接凭据，原文件保留；不复制旧设备身份，也不导入 Tauri WebView 数据。此客户端将作为新设备注册，连接后请先同步旧主配置、等待保存确认，再按需要设为主设备。'
          : '使用当前空 client-data 创建新的设备身份，不读取旧目录。之后此目录不再满足旧 Flutter 数据导入条件；旧资料保持不变。继续后需按新设备重新连接，并按需要同步主配置。',
    ))
      return;
    // 对话框确认只属于当前模型；Native 仍须再次检查空目标和导入资格，不能依赖
    // 弹窗打开时的快照。页面卸载或模型被替换时不再发送本机写操作。
    if (!mounted || !identical(model, widget.model)) return;
    await model.initializeStorage(importLegacy: importLegacy);
  }

  @override
  Widget build(BuildContext context) {
    final model = widget.model;
    final status = model.storageStatus;
    if (status == null) return const SizedBox.shrink();
    final waiting = status.needsInitialization;
    final enabled = waiting && !model.busy && !model.connecting;
    return Section('本机数据存储', [
      Fact('存储模式', switch (status.mode) {
        'windows-portable' => '独立客户端 · 便携目录',
        'windows-embedded' => 'Mod 内置客户端 · Windows 用户私有目录',
        'android-private' => 'Android 应用私有目录',
        _ => '未识别，请使用配套客户端',
      }),
      Text(
        status.mode == 'windows-portable'
            ? '偏好、窗口位置、设备身份和连接凭据保存在 EXE 同级 client-data。更新程序目录时须完整保留该目录。'
            : '本机资料保存在当前用户的应用私有目录，更新程序文件不会替换这些资料。',
        style: StewardText.caption,
      ),
      // 默认收起绝对路径，减少截图或共享界面时带出 Windows 用户目录的可能；
      // 展开仅改变页面状态，不写入偏好或操作历史。
      ToolRow([
        StewardButton(
          _showLocation ? '隐藏存储位置' : '显示存储位置',
          onPressed: () => setState(() => _showLocation = !_showLocation),
        ),
      ]),
      if (_showLocation) Text(status.location, style: StewardText.caption),
      if (waiting) ...[
        const Text(
          '首次使用请先选择数据来源。两种方式都会创建新设备身份；导入不会继承旧主设备角色。完成选择前不会生成设备身份或写入偏好。',
          style: StewardText.body,
        ),
        if (!status.canImportLegacy &&
            status.importUnavailableReason?.isNotEmpty == true)
          Text(status.importUnavailableReason!, style: StewardText.caption),
        ToolRow([
          StewardButton(
            '导入旧 Flutter 数据',
            onPressed: enabled && status.canImportLegacy
                ? () => _initialize(true)
                : null,
          ),
          StewardButton(
            '作为新设备开始',
            onPressed: enabled ? () => _initialize(false) : null,
          ),
        ]),
      ],
      if (model.credentialUnavailable)
        const Text(
          '已有凭据无法在当前 Windows 用户环境中解密，原文件已保留。请返回原电脑和原 Windows 用户，或恢复该环境下的原始 client-data。若要在另一台电脑作为新设备使用，请另建完整客户端目录并重新连接；不要清空原目录或凭据文件来绕过错误。',
          style: StewardText.body,
        ),
    ]);
  }
}
