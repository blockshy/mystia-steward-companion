#include "flutter_window.h"

#include <optional>
#include <utility>

#include "flutter/generated_plugin_registrant.h"
#include "utils.h"

FlutterWindow::FlutterWindow(const flutter::DartProject& project, lifecycle_probe::Shared* primary)
    : project_(project), primary_shared_(primary) {}

FlutterWindow::~FlutterWindow() {
  // System.exitApplication posts WM_QUIT without destroying the HWND. Run the
  // derived cleanup while this object's message handler is still alive.
  Destroy();
}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  RECT frame = GetClientArea();

  // The size here must match the window dimensions to avoid unnecessary surface
  // creation / destruction in the startup path.
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  // Ensure that basic setup of the controller was successful.
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  SetChildContent(flutter_controller_->view()->GetNativeWindow());
  auto* messenger = flutter_controller_->engine()->messenger();
  if (primary_shared_) {
    lifecycle_primary_ = std::make_unique<LifecyclePrimary>(GetHandle(), flutter_controller_.get(), *primary_shared_);
    mystia_window_probe::LifecycleFixtureHostApi::SetUp(messenger, lifecycle_primary_.get());
  } else {
    const auto args = GetCommandLineArguments();
    if (ValidateControlProbeInvocation(args) || ValidateControlClientInvocation(args)) {
      control_probe_ = std::make_unique<ControlProbeBridge>(GetHandle(), flutter_controller_.get(), args);
      mystia_control_probe::ControlProbeHostApi::SetUp(messenger, control_probe_.get());
    } else if (ValidateInputProbeInvocation(args)) {
      input_probe_ = std::make_unique<InputProbeBridge>(GetHandle(), flutter_controller_.get(), args);
      mystia_input_probe::InputProbeHostApi::SetUp(messenger, input_probe_.get());
    } else {
      probe_bridge_ = std::make_unique<WindowBridge>(GetHandle(), flutter_controller_.get(), args);
      lifecycle_controller_ = std::make_unique<LifecycleController>(GetHandle(), args);
      mystia_window_probe::WindowProbeHostApi::SetUp(messenger, probe_bridge_.get());
      mystia_window_probe::LifecycleHostApi::SetUp(messenger, lifecycle_controller_.get());
    }
  }

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    if (ValidateControlClientInvocation(GetCommandLineArguments())) {
      // A cold-start probe must retain the game's foreground until MSC1 grants it.
      ::ShowWindow(GetHandle(), SW_SHOWNOACTIVATE);
    } else {
      this->Show();
    }
  });

  // Flutter can complete the first frame before the "show window" callback is
  // registered. The following call ensures a frame is pending to ensure the
  // window is shown. It is a no-op if the first frame hasn't completed yet.
  flutter_controller_->ForceRedraw();

  return true;
}

void FlutterWindow::OnDestroy() {
  // Destroying the Flutter child HWND synchronously reenters the parent window
  // procedure. Detach both members before any cleanup can dispatch messages so
  // MessageHandler cannot forward to a controller/bridge being destroyed.
  auto controller = std::move(flutter_controller_);
  auto bridge = std::move(probe_bridge_);
  auto lifecycle = std::move(lifecycle_controller_);
  auto primary = std::move(lifecycle_primary_);
  auto input = std::move(input_probe_);
  auto control = std::move(control_probe_);
  if (controller && controller->engine()) {
    mystia_window_probe::WindowProbeHostApi::SetUp(controller->engine()->messenger(), nullptr);
    mystia_window_probe::LifecycleHostApi::SetUp(controller->engine()->messenger(), nullptr);
    mystia_window_probe::LifecycleFixtureHostApi::SetUp(controller->engine()->messenger(), nullptr);
    mystia_input_probe::InputProbeHostApi::SetUp(controller->engine()->messenger(), nullptr);
    mystia_control_probe::ControlProbeHostApi::SetUp(controller->engine()->messenger(), nullptr);
  }
  // Settle pending Pigeon replies while the messenger is still valid.
  bridge.reset();
  lifecycle.reset();
  primary.reset();
  input.reset();
  control.reset();
  controller.reset();

  Win32Window::OnDestroy();
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  if (control_probe_) {
    auto handled = control_probe_->HandleWindowMessage(message, wparam, lparam);
    if (handled) return *handled;
  }
  if (input_probe_) {
    auto handled = input_probe_->HandleWindowMessage(message, wparam, lparam);
    if (handled) return *handled;
  }
  if (lifecycle_primary_) {
    auto handled = lifecycle_primary_->HandleWindowMessage(message, wparam, lparam);
    if (handled) return *handled;
  }
  if (probe_bridge_) {
    auto handled = probe_bridge_->HandleWindowMessage(message, wparam, lparam);
    if (handled) return *handled;
  }
  // Give Flutter, including plugins, an opportunity to handle window messages.
  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_FONTCHANGE:
      if (flutter_controller_ && flutter_controller_->engine()) {
        flutter_controller_->engine()->ReloadSystemFonts();
      }
      break;
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}
