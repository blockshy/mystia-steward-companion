#ifndef RUNNER_LIFECYCLE_PRIMARY_H_
#define RUNNER_LIFECYCLE_PRIMARY_H_

#include "lifecycle_shared.h"
#include "window_api.g.h"
#include <flutter/flutter_view_controller.h>
#include <memory>
#include <optional>

class LifecyclePrimary final : public mystia_window_probe::LifecycleFixtureHostApi {
 public:
  LifecyclePrimary(HWND top, flutter::FlutterViewController* controller, lifecycle_probe::Shared& shared);
  ~LifecyclePrimary() override;
  // Called once for messages actually removed from this UI thread's queue,
  // before TranslateMessage. Flutter's SendMessage redispatch is excluded.
  static void ObserveQueuedMessage(const MSG& message);
  mystia_window_probe::ErrorOr<int64_t> PublishUi(
      const mystia_window_probe::LifecycleUiEvidence& evidence) override;
  std::optional<LRESULT> HandleWindowMessage(UINT message, WPARAM wparam, LPARAM lparam);
 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};
#endif
