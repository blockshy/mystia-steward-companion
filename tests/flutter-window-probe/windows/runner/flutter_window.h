#ifndef RUNNER_FLUTTER_WINDOW_H_
#define RUNNER_FLUTTER_WINDOW_H_

#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>

#include <memory>

#include "win32_window.h"
#include "window_bridge.h"
#include "lifecycle_controller.h"
#include "lifecycle_primary.h"
#include "input_probe_bridge.h"
#include "control_probe_bridge.h"

// A window that does nothing but host a Flutter view.
class FlutterWindow : public Win32Window {
 public:
  // Creates a new FlutterWindow hosting a Flutter view running |project|.
  explicit FlutterWindow(const flutter::DartProject& project, lifecycle_probe::Shared* primary = nullptr);
  virtual ~FlutterWindow();

 protected:
  // Win32Window:
  bool OnCreate() override;
  void OnDestroy() override;
  LRESULT MessageHandler(HWND window, UINT const message, WPARAM const wparam,
                         LPARAM const lparam) noexcept override;

 private:
  // The project to run.
  flutter::DartProject project_;

  // The Flutter instance hosted by this window.
  std::unique_ptr<flutter::FlutterViewController> flutter_controller_;
  std::unique_ptr<WindowBridge> probe_bridge_;
  lifecycle_probe::Shared* primary_shared_;
  std::unique_ptr<LifecycleController> lifecycle_controller_;
  std::unique_ptr<LifecyclePrimary> lifecycle_primary_;
  std::unique_ptr<InputProbeBridge> input_probe_;
  std::unique_ptr<ControlProbeBridge> control_probe_;
};

#endif  // RUNNER_FLUTTER_WINDOW_H_
