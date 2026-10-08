#ifndef RUNNER_LIFECYCLE_CONTROLLER_H_
#define RUNNER_LIFECYCLE_CONTROLLER_H_

#include "lifecycle_shared.h"
#include "window_api.g.h"
#include <memory>
#include <optional>
#include <vector>

namespace lifecycle_probe {
// Owns the inherited mapping for an internal child role. Validation happens
// before Flutter initialization; arbitrary external invocation is rejected.
class ChildContext {
 public:
  explicit ChildContext(const std::vector<std::string>& arguments);
  ~ChildContext();
  Shared& shared() const { return *shared_; }
  ControlAction action() const { return action_; }
 private:
  HANDLE mapping_ = nullptr, parent_ = nullptr;
  Shared* shared_ = nullptr;
  ControlAction action_ = ControlAction::none;
};
}

class LifecycleController final : public mystia_window_probe::LifecycleHostApi {
 public:
  LifecycleController(HWND top, const std::vector<std::string>& arguments);
  ~LifecycleController() override;
  void Execute(const mystia_window_probe::LifecycleCommand& command,
      std::function<void(mystia_window_probe::ErrorOr<mystia_window_probe::LifecycleSnapshot>)> reply) override;
 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};
#endif
