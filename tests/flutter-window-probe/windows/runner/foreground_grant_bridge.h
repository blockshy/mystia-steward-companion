#ifndef RUNNER_FOREGROUND_GRANT_BRIDGE_H_
#define RUNNER_FOREGROUND_GRANT_BRIDGE_H_

#include <windows.h>

#include <cstdint>
#include <memory>
#include <string>

struct ForegroundGrantObservation {
  bool ready = false, identity_matched = false, response_received = false;
  uint64_t sequence = 0, request_id = 0, response_sequence = 0;
  DWORD issuer_pid = 0, target_pid = 0;
  bool attempted = false, succeeded = false, activation_requested = false;
  DWORD error = 0;
  uint64_t foreground_hwnd = 0, foreground_after_hwnd = 0;
  DWORD foreground_pid = 0, foreground_after_pid = 0;
};

// One exact game connection, one outstanding fixed frame, no reconnection or
// request replay. Poll advances only nonblocking overlapped I/O and observation.
class ForegroundGrantBridge final {
 public:
  ForegroundGrantBridge(const std::string& run_id, HWND probe, DWORD probe_thread);
  ~ForegroundGrantBridge();
  ForegroundGrantBridge(const ForegroundGrantBridge&) = delete;
  ForegroundGrantBridge& operator=(const ForegroundGrantBridge&) = delete;
  void BindGame(HANDLE game, DWORD pid, uint64_t creation, const std::wstring& executable);
  const std::string& pipe_name() const;
  const std::string& nonce_hex() const;
  uint64_t probe_creation() const;
  void Poll();
  void Request(uint64_t request_id, HWND game_window, DWORD game_thread);
  void MarkActivationRequested();
  const ForegroundGrantObservation& observation() const;
  void BeginStop();
  bool PollStop();
  bool stopped() const;
  // Validates the plugin's complete normal-EOF evidence against the exact
  // ready/three request/reply bytes retained by this process.
  void ValidateEvidence(const std::string& json, const std::string& probe_sha256,
                        const std::string& descriptor_sha256) const;

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};
#endif
