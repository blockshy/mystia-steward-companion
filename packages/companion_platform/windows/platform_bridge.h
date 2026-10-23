#pragma once
#include <winsock2.h>
#include <windows.h>
#include <flutter/flutter_view_controller.h>
#include <memory>
#include <optional>
#include <vector>
#include "platform.g.h"

/// 在 Flutter 引擎启动前争用唯一控制端口。次实例只转发激活，不创建第二窗口。
/// 返回 0 表示主实例，1 表示次实例已通知，其他值表示明确启动失败。
int StartCompanionControl(const std::vector<std::string>& arguments);
void StopCompanionControl();

/// 所有 HWND 和原生输入资源归属 UI 线程；TCP 线程仅排队不可变命令。
class CompanionPlatformBridge final : public mystia_platform::PlatformHostApi {
 public:
  CompanionPlatformBridge(HWND window, flutter::FlutterViewController* controller);
  ~CompanionPlatformBridge() override;
  std::optional<LRESULT> HandleMessage(UINT message, WPARAM wparam, LPARAM lparam);
  mystia_platform::ErrorOr<mystia_platform::PlatformLaunch> Initialize() override;
  std::optional<mystia_platform::FlutterError> ApplyWindowPreferences(const mystia_platform::WindowPreferences&) override;
  mystia_platform::ErrorOr<bool> PerformWindowAction(const mystia_platform::WindowAction&) override;
  mystia_platform::ErrorOr<mystia_platform::PlatformInput> PollInput() override;
  mystia_platform::ErrorOr<std::optional<std::string>> ReadSecret(const std::string&) override;
  std::optional<mystia_platform::FlutterError> WriteSecret(const std::string&, const std::string&) override;
  std::optional<mystia_platform::FlutterError> DeleteSecret(const std::string&) override;
  mystia_platform::ErrorOr<std::optional<std::string>> ReadPreferences() override;
  std::optional<mystia_platform::FlutterError> WritePreferences(const std::string&) override;
 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};
