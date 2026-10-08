#ifndef RUNNER_CONTROL_PROBE_BRIDGE_H_
#define RUNNER_CONTROL_PROBE_BRIDGE_H_

#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "control_probe_api.g.h"

bool ValidateControlProbeInvocation(const std::vector<std::string>& arguments);
bool IsControlLifecycleController(const std::vector<std::string>& arguments);
int RunControlLifecycleController(const std::vector<std::string>& arguments);
bool ValidateControlClientInvocation(const std::vector<std::string>& arguments);
std::vector<std::string> ControlClientDartArguments(const std::vector<std::string>& arguments);

class ControlProbeBridge final : public mystia_control_probe::ControlProbeHostApi {
 public:
  ControlProbeBridge(HWND top, flutter::FlutterViewController* controller,
                     const std::vector<std::string>& arguments);
  ~ControlProbeBridge() override;
  static void ObserveQueuedMessage(const MSG& message);
  std::optional<LRESULT> HandleWindowMessage(UINT message, WPARAM wparam, LPARAM lparam);
  void Execute(const mystia_control_probe::ControlCommand& command,
               std::function<void(mystia_control_probe::ErrorOr<mystia_control_probe::ControlSnapshot>)> result) override;
  void Finish(const std::string& report_json, int64_t exit_code,
              std::function<void(std::optional<mystia_control_probe::FlutterError>)> result) override;

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};
#endif
