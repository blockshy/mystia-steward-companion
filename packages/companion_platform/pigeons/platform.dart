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

/// 原生输入快照。按键为 XInput 标准位掩码，轴值已经归一化至 [-1, 1]。
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
  void applyWindowPreferences(WindowPreferences preferences);

  /// 返回是否实际完成。焦点切换被系统拒绝时为 false，不伪造成功。
  bool performWindowAction(WindowAction action);
  PlatformInput pollInput();

  /// 只接收受限键名；损坏数据必须报错且保留密文，不自动重置。
  String? readSecret(String key);
  void writeSecret(String key, String value);
  void deleteSecret(String key);
  String? readPreferences();

  /// 原子发布并回读确认；只有本调用完成后，上层才可以发 profile sync ACK。
  void writePreferences(String json);
}
