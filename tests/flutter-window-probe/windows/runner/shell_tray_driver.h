#ifndef RUNNER_SHELL_TRAY_DRIVER_H_
#define RUNNER_SHELL_TRAY_DRIVER_H_

#include "lifecycle_shared.h"
#include <memory>
#include <stdexcept>
#include <string>

namespace lifecycle_probe {
class TrayBlocked : public std::runtime_error {
 public:
  using std::runtime_error::runtime_error;
};
class ShellTrayDriver {
 public:
  explicit ShellTrayDriver(Shared& shared);
  ~ShellTrayDriver();
  RECT FindTrayIcon();
  void ClickTray(bool right);
  // Poll advances only a previously authorized click awaiting Shell overflow.
  // It never retries the overflow action or injects input after cancellation.
  void Poll();
  bool ClickPending() const;
  void CancelPendingClick();
  void ClickTrayMenu(UINT command);
  bool TrayAbsent();
  std::string DiagnosticsJson() const;
 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};
}  // namespace lifecycle_probe
#endif
