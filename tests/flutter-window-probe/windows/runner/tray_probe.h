#ifndef RUNNER_TRAY_PROBE_H_
#define RUNNER_TRAY_PROBE_H_

#include "lifecycle_shared.h"
#include <functional>
#include <memory>
#include <optional>

namespace lifecycle_probe {
class TrayProbe {
 public:
  TrayProbe(Shared& shared, HWND owner, std::function<void(UINT)> command);
  ~TrayProbe();
  void Add();
  bool Delete();
  bool MenuActive() const;
  void CancelMenu();
  void PollMenu();
  std::optional<LRESULT> HandleWindowMessage(UINT message, WPARAM wparam, LPARAM lparam);
 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};
}  // namespace lifecycle_probe
#endif
