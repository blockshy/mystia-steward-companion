#ifndef RUNNER_WINDOW_BRIDGE_H_
#define RUNNER_WINDOW_BRIDGE_H_

#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "window_api.g.h"

// The only executable entry points: fixed node invocation or inherited fixture.
bool ValidateWindowProbeInvocation(const std::vector<std::string>& arguments);
int RunWindowProbeTarget(const std::vector<std::string>& arguments);
// Bounded observations of this probe's owned messages, before IME translation.
void ObserveWindowProbeQueuedMessage(const MSG& message);

class WindowBridge final : public mystia_window_probe::WindowProbeHostApi {
 public:
  WindowBridge(HWND window, flutter::FlutterViewController* controller,
               const std::vector<std::string>& arguments);
  ~WindowBridge() override;
  void Execute(const mystia_window_probe::ProbeCommand& command,
               std::function<void(mystia_window_probe::ErrorOr<
                   mystia_window_probe::ProbeSnapshot>)> result) override;
  std::optional<LRESULT> HandleWindowMessage(UINT message, WPARAM wparam, LPARAM lparam);

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};

#endif
