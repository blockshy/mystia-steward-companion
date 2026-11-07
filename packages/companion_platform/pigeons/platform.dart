import 'package:pigeon/pigeon.dart';

/// 原生启动信息。Token 仅在可信进程内传输，不允许写入普通偏好或日志。
class PlatformLaunch {
  PlatformLaunch({
    required this.desktop,
    this.endpoint,
    this.token,
    this.gamePid,
  });
  bool desktop;
  String? endpoint;
  String? token;
  int? gamePid;
}

/// 本次运行已验证的存储位置。绝对路径仅供本机设置页展示，不得写入公共日志、
/// 诊断包或业务协议。待初始化的便携目录尚未获准生成设备身份或写入偏好。
class PlatformStorageStatus {
  PlatformStorageStatus({
    required this.mode,
    required this.location,
    required this.needsInitialization,
    required this.canImportLegacy,
    this.importUnavailableReason,
  });
  String mode;
  String location;
  bool needsInitialization;
  bool canImportLegacy;
  String? importUnavailableReason;
}

/// 窗口行为偏好；透明度由 Flutter 分层绘制，原生只提供透明表面。
class WindowPreferences {
  WindowPreferences({
    required this.alwaysOnTop,
    required this.mousePassthrough,
    required this.keepVisible,
    required this.switchCooldownMs,
    this.darkMode = false,
  });
  bool alwaysOnTop;
  bool mousePassthrough;
  bool keepVisible;
  int switchCooldownMs;

  /// 窗口标题栏跟随客户端主题，不再独立读取系统明暗设置。
  /// 此字段只控制系统非客户区配色，不能改变整窗透明度或鼠标交互。
  bool darkMode;
}

/// 原生输入快照。方向及常用按钮采用 XInput 位掩码，摇杆轴范围为 [-1, 1]，
/// 扳机范围为 [0, 1]；Android 适配层还使用扩展位保存数字扳机的短按。
class PlatformInput {
  PlatformInput({
    required this.focused,
    required this.connected,
    required this.buttons,
    required this.leftX,
    required this.leftY,
    required this.leftTrigger,
    required this.rightTrigger,
    required this.activationRevision,
    required this.launch,
  });
  bool focused;
  bool connected;
  int buttons;
  double leftX;
  double leftY;
  double leftTrigger;
  double rightTrigger;
  int activationRevision;
  PlatformLaunch launch;
}

enum WindowAction { show, hide, toggleFocus, exit }

@ConfigurePigeon(
  PigeonOptions(
    dartOut: 'lib/src/platform.g.dart',
    cppHeaderOut: 'windows/platform.g.h',
    cppSourceOut: 'windows/platform.g.cpp',
    cppOptions: CppOptions(namespace: 'mystia_platform'),
    kotlinOut: 'android/src/PlatformApi.g.kt',
    kotlinOptions: KotlinOptions(
      package: 'com.tyukki.mystia.steward.companion',
    ),
  ),
)
@HostApi()
abstract class PlatformHostApi {
  PlatformLaunch initialize();

  /// 查询固定的存储模式与首次使用状态；不创建身份、不从历史目录自动复制文件。
  PlatformStorageStatus readStorageStatus();

  /// 只读固定旧 Flutter 目录的偏好原文，限便携目录首次导入预校验；null 表示无文件。
  /// 不返回凭据内容、不接受调用方指定路径，读取失败不能伪装成文件不存在。
  String? readLegacyPreferences();

  /// 便携客户端首次使用的显式选择。true 仅导入旧 Flutter 偏好、窗口及连接凭据，
  /// 不读取或复制旧设备身份；导入后仍以新设备注册。
  /// false 确认使用新的空目录；原生层重新检查目标与一次性导入资格，失败不重建身份。
  /// expectedLegacyPreferences 是已由 Dart 验证的原字符串，原生在提交前严格比较
  /// 当前读取结果（包括 null），防止校验后的源文件变化被当作同一导入请求。
  void initializeStorage(bool importLegacy, String? expectedLegacyPreferences);
  void applyWindowPreferences(WindowPreferences preferences);

  /// 返回原生端是否接受或处理请求；焦点切换可能异步完成，true 不代表目标已成为前台。
  /// 不支持的动作或明确拒绝返回 false；调用者不能据此推导游戏操作权限。
  bool performWindowAction(WindowAction action);

  /// 仅打开本项目 GitHub 发布页。原生端再次校验 URL，不能启动任意协议或文件。
  bool openExternalUrl(String url);
  PlatformInput pollInput();

  /// 只接收受限键名；损坏数据必须报错且保留密文，不自动重置。
  String? readSecret(String key);
  void writeSecret(String key, String value);
  void deleteSecret(String key);
  String? readPreferences();

  /// 原子发布并回读确认；只有本调用完成后，上层才可以发 profile sync ACK。
  void writePreferences(String json);
}
