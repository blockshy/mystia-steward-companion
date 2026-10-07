#ifndef RUNNER_PROBE_BRIDGE_H_
#define RUNNER_PROBE_BRIDGE_H_

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <functional>
#include <memory>
#include <string>
#include <vector>

#include "probe_api.g.h"

// Owned by FlutterWindow. Destroy before the engine/messenger so any outstanding
// Pigeon reply can be completed on the platform thread during shutdown.
class ProbeBridge final : public mystia_probe::ProbeHostApi {
 public:
  static constexpr UINT kCompletionMessage = WM_APP + 0x451;

  ProbeBridge(HWND window, const std::vector<std::string>& arguments);
  ~ProbeBridge() override;
  ProbeBridge(const ProbeBridge&) = delete;
  ProbeBridge& operator=(const ProbeBridge&) = delete;

  void Exchange(
      const std::string& command,
      std::function<void(mystia_probe::ErrorOr<std::string>)> result) override;

  // Called only by the owning window's platform-thread message handler.
  bool HandleWindowMessage(UINT message);

 private:
  struct State;
  std::unique_ptr<State> state_;
};

#endif  // RUNNER_PROBE_BRIDGE_H_
