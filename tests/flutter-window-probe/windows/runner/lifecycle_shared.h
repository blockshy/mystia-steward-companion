#ifndef RUNNER_LIFECYCLE_SHARED_H_
#define RUNNER_LIFECYCLE_SHARED_H_

#include <windows.h>
#include <cstdint>
#include <string>

namespace lifecycle_probe {
constexpr uint64_t kMagic = 0x4d59534c49464531ULL;
constexpr UINT kTimer = 0x4c4650;
constexpr UINT kTrayCallback = WM_APP + 71;
constexpr UINT kTrayId = 71;
constexpr UINT kMenuShow = 7101, kMenuPassthrough = 7102, kMenuExit = 7103;
enum class ControlAction : LONG { none, show, toggle, exit, invalid };
enum class ExitCause : LONG { none, tray, control, controllerAbort };

// Only anonymous inherited mappings connect the exact retained processes.
// Published counters use interlocked access; strings are immutable after launch.
struct Shared {
  uint64_t magic;
  uint64_t nonce;
  char git_sha[41];
  wchar_t run_id[81];
  DWORD controller_pid;
  uint64_t controller_hwnd;
  LONG generation;
  LONG requested_port;
  uint32_t input_marker;
  GUID tray_guid;
  wchar_t tray_tooltip[128];
  char expected_endpoint[128];
  char expected_token[64];
  volatile LONG primary_pid, control_port;
  volatile LONG64 top_hwnd, child_hwnd;
  volatile LONG ui_ready, ui_sequence, ui_pointer, ui_key, ui_focused;
  volatile LONG native_down, native_up, native_key;
  volatile LONG visible, passthrough;
  volatile LONG controller_abort;
  // Fixed fixture-only operations: 1 = pass-through, 2 = hide. Never legacy commands.
  volatile LONG fixture_command, fixture_sequence, fixture_ack;
  volatile LONG focus_handoff_sequence, focus_handoff_ack;
  volatile LONG focus_handoff_authorize_result, focus_handoff_authorize_error;
  volatile LONG focus_handoff_foreground_result, focus_handoff_foreground_error;
  volatile LONG tray_added, tray_versioned, tray_deleted;
  volatile LONG tray_taskbar_created, tray_readds;
  volatile LONG tray_shell_pid;
  volatile LONG64 tray_shell_creation;
  RECT tray_rect;
  volatile LONG tray_rect_ready;
  volatile LONG tray_last_send_requested, tray_last_send_inserted, tray_last_send_error;
  volatile LONG tray_callbacks, last_tray_event, menu_commands, last_menu_command;
  volatile LONG menu_open;
  volatile LONG64 menu_handle;
  volatile LONG64 menu_window;
  RECT menu_rects[3];
  volatile LONG menu_rects_ready;
  volatile LONG control_applied, control_rejected, control_last_action;
  volatile LONG control_receive_calls, control_pending_bytes;
  volatile LONG connection_updates, connection_activations;
  volatile LONG connection_game_pid, endpoint_matches, token_matches;
  volatile LONG exit_cause;
  volatile LONG error_code;
  char error_message[512];
  // One serial secondary is allowed. Its report is published before peer_done.
  volatile LONG peer_pid, peer_action, peer_request_id;
  volatile LONG peer_bind_error, peer_server_pid, peer_bytes_sent;
  volatile LONG peer_window_count, peer_done, peer_error;
};

inline LONG Read(volatile LONG* value) { return InterlockedCompareExchange(value, 0, 0); }
inline uint64_t Read(volatile LONG64* value) {
  return static_cast<uint64_t>(InterlockedCompareExchange64(value, 0, 0));
}
inline void Publish(volatile LONG* value, LONG next) { InterlockedExchange(value, next); }
inline void Publish(volatile LONG64* value, uint64_t next) {
  InterlockedExchange64(value, static_cast<LONG64>(next));
}

}  // namespace lifecycle_probe
#endif
