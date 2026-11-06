#ifndef RUNNER_FLUTTER_WINDOW_H_
#define RUNNER_FLUTTER_WINDOW_H_

#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>

#include <memory>

#include "win32_window.h"
#include "platform_bridge.h"

/// 承载 Flutter 引擎并连接原生平台桥的顶层窗口。
/// 平台桥先接收消息，未消费的消息依次交给 Flutter 与 Win32 基类。
class FlutterWindow : public Win32Window {
 public:
  // 保存项目配置；实际引擎和原生视图在 OnCreate 中创建。
  explicit FlutterWindow(const flutter::DartProject& project);
  virtual ~FlutterWindow();

 protected:
  // 与 Win32Window 的创建、销毁及消息生命周期保持一致。
  bool OnCreate() override;
  void OnDestroy() override;
  LRESULT MessageHandler(HWND window, UINT const message, WPARAM const wparam,
                         LPARAM const lparam) noexcept override;

 private:
  // 引擎启动所需的资源路径和 Dart 入口参数副本。
  flutter::DartProject project_;

  // 独占引擎与视图控制器；须在平台桥解除回调后释放。
  std::unique_ptr<flutter::FlutterViewController> flutter_controller_;
  // 平台桥先于引擎销毁，确保原生回调不引用已经释放的 messenger/HWND。
  std::unique_ptr<CompanionPlatformBridge> platform_bridge_;
};

#endif  // RUNNER_FLUTTER_WINDOW_H_
