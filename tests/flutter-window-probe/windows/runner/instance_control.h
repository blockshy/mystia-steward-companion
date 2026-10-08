#ifndef RUNNER_INSTANCE_CONTROL_H_
#define RUNNER_INSTANCE_CONTROL_H_

#include <cstdint>
#include <functional>
#include <memory>

#include "lifecycle_shared.h"

namespace lifecycle_probe {

// UI-thread owned, isolated loopback listener. No legacy protocol ACK is added.
class InstanceControlServer final {
 public:
  InstanceControlServer(Shared& shared,
                        std::function<void(ControlAction)> apply);
  ~InstanceControlServer();
  InstanceControlServer(const InstanceControlServer&) = delete;
  InstanceControlServer& operator=(const InstanceControlServer&) = delete;

  void Start();
  void Poll();
  void Stop();
  uint16_t port() const;

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};

// Runs before Flutter initialization in the retained secondary child process.
int RunInstancePeer(Shared& shared, ControlAction action);

}  // namespace lifecycle_probe

#endif  // RUNNER_INSTANCE_CONTROL_H_
