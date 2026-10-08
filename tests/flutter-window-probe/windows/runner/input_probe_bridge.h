#ifndef RUNNER_INPUT_PROBE_BRIDGE_H_
#define RUNNER_INPUT_PROBE_BRIDGE_H_

#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "input_probe_api.g.h"

bool ValidateInputProbeInvocation(const std::vector<std::string>& arguments);

class InputProbeBridge final : public mystia_input_probe::InputProbeHostApi {
 public:
  InputProbeBridge(HWND top, flutter::FlutterViewController* controller,
                   const std::vector<std::string>& arguments);
  ~InputProbeBridge() override;
  static void ObserveQueuedMessage(const MSG& message);
  std::optional<LRESULT> HandleWindowMessage(UINT message, WPARAM wparam, LPARAM lparam);
  void SampleXInput(std::function<void(mystia_input_probe::ErrorOr<
                       mystia_input_probe::XInputSnapshot>)> result) override;
  void ExecuteFocus(const mystia_input_probe::FocusCommand& command,
                    std::function<void(mystia_input_probe::ErrorOr<
                        mystia_input_probe::FocusSnapshot>)> result) override;
  void Finish(const std::string& report_json, int64_t exit_code,
              std::function<void(std::optional<mystia_input_probe::FlutterError>)> result) override;

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};
#endif
