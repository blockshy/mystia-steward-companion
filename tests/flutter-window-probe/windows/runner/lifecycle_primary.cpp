#include "lifecycle_primary.h"

#include <commctrl.h>
#include <shellapi.h>
#include <algorithm>
#include <cstring>
#include <stdexcept>
#include <utility>

#include "instance_control.h"
#include "tray_probe.h"

namespace {
using namespace lifecycle_probe;
thread_local LifecyclePrimary* queued_primary = nullptr;
constexpr UINT_PTR kChildSubclass = 0x4c4651;
void Require(bool condition, const char* message) {
  if (!condition) throw std::runtime_error(message);
}
void SetStyle(HWND window, LONG_PTR style) {
  SetLastError(ERROR_SUCCESS);
  LONG_PTR previous = SetWindowLongPtrW(window, GWL_EXSTYLE, style);
  Require(previous != 0 || GetLastError() == ERROR_SUCCESS, "Cannot change fixture window style.");
}
}

struct LifecyclePrimary::Impl {
  HWND top, child;
  Shared& shared;
  std::unique_ptr<InstanceControlServer> server;
  std::unique_ptr<TrayProbe> tray;
  bool passthrough = false, exit_pending = false, exited = false, subclassed = false;
  bool focus_handoff_pending = false;
  HWND focus_handoff_target = nullptr;
  LONG focus_handoff_sequence = 0;
  ULONGLONG focus_handoff_deadline = 0;
  int exit_code = 1;
  ExitCause exit_cause = ExitCause::none;

  Impl(HWND owner, flutter::FlutterViewController* controller, Shared& state)
      : top(owner), child(controller->view()->GetNativeWindow()), shared(state) {
    try {
      Require(shared.magic == kMagic && shared.input_marker > 0 && shared.input_marker <= 0x7fffffff,
              "Fixture mapping/input marker is invalid.");
      Require(IsWindow(top) && IsWindow(child) && GetAncestor(child, GA_ROOT) == top,
              "Fixture Flutter HWND identity is invalid.");
      Publish(&shared.primary_pid, static_cast<LONG>(GetCurrentProcessId()));
      Publish(&shared.top_hwnd, reinterpret_cast<uintptr_t>(top));
      Publish(&shared.child_hwnd, reinterpret_cast<uintptr_t>(child));
      Require(SetWindowSubclass(child, ChildProc, kChildSubclass, reinterpret_cast<DWORD_PTR>(this)) != FALSE,
              "Cannot observe fixture Flutter input.");
      subclassed = true;
      ApplyMode(false);
      server = std::make_unique<InstanceControlServer>(shared, [this](ControlAction action) { Control(action); });
      server->Start();
      tray = std::make_unique<TrayProbe>(shared, top, [this](UINT command) { TrayCommand(command); });
      tray->Add();
      Require(SetTimer(top, kTimer, 20, nullptr) != 0, "Cannot schedule fixture lifecycle polling.");
    } catch (const std::exception& error) { Fail(error.what()); }
  }
  ~Impl() {
    KillTimer(top, kTimer);
    if (subclassed && IsWindow(child)) RemoveWindowSubclass(child, ChildProc, kChildSubclass);
    if (server) server->Stop();
    tray.reset();
  }
  void Error(const char* message) {
    if (Read(&shared.error_code) != 0) return;
    strncpy_s(shared.error_message, message, _TRUNCATE);
    Publish(&shared.error_code, 1);
  }
  void Fail(const char* message) {
    Error(message);
    BeginExit(1, ExitCause::controllerAbort);
  }
  void ApplyMode(bool next) {
    LONG_PTR style = GetWindowLongPtrW(top, GWL_EXSTYLE) | WS_EX_LAYERED;
    if (next) style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    else style &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    SetStyle(top, style);
    Require(SetLayeredWindowAttributes(top, 0, 255, LWA_ALPHA) != FALSE,
            "Cannot keep fixture pixels opaque while testing input mode.");
    LONG_PTR actual = GetWindowLongPtrW(top, GWL_EXSTYLE);
    constexpr LONG_PTR mask = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    Require((actual & mask) == (style & mask), "Fixture input mode readback differs.");
    passthrough = next; Publish(&shared.passthrough, next ? 1 : 0);
  }
  void ShowInteractive() {
    Require(!exit_pending && !focus_handoff_pending, "Cannot show a closing fixture or interrupt controller focus handoff.");
    ApplyMode(false);
    ShowWindow(top, SW_SHOWNORMAL);
    Require(SetWindowPos(top, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW) != FALSE,
            "Cannot show the fixture window.");
    SetForegroundWindow(top);
    Require(GetForegroundWindow() == top, "Fixture show did not obtain real foreground.");
    SetFocus(child);
    Require(GetFocus() == child, "Fixture show did not focus its Flutter child.");
    Publish(&shared.visible, IsWindowVisible(top) ? 1 : 0);
  }
  void Hide() {
    ShowWindow(top, SW_HIDE);
    Require(!IsWindowVisible(top), "Fixture hide was not observed.");
    Publish(&shared.visible, 0);
  }
  void FocusController() {
    Require(!focus_handoff_pending && !exit_pending, "Controller focus handoff is already pending or closing.");
    HWND controller = reinterpret_cast<HWND>(static_cast<uintptr_t>(shared.controller_hwnd));
    DWORD pid = 0; GetWindowThreadProcessId(controller, &pid);
    Require(IsWindow(controller) && pid == shared.controller_pid && GetAncestor(controller, GA_ROOT) == controller,
            "Fixture controller HWND identity changed.");
    LONG previous = Read(&shared.focus_handoff_sequence);
    Require(previous >= 0 && previous < 1000000 && Read(&shared.focus_handoff_ack) == previous,
            "Controller focus handoff sequence is ambiguous.");
    focus_handoff_target = controller;
    focus_handoff_sequence = previous + 1;
    focus_handoff_deadline = GetTickCount64() + 5000;
    focus_handoff_pending = true;
    Publish(&shared.focus_handoff_authorize_result, 0); Publish(&shared.focus_handoff_authorize_error, 0);
    Publish(&shared.focus_handoff_foreground_result, 0); Publish(&shared.focus_handoff_foreground_error, 0);
    Publish(&shared.focus_handoff_sequence, focus_handoff_sequence);
    SetLastError(ERROR_SUCCESS);
    BOOL authorized = AllowSetForegroundWindow(shared.controller_pid);
    DWORD authorize_error = GetLastError();
    Publish(&shared.focus_handoff_authorize_result, authorized ? 1 : 0);
    Publish(&shared.focus_handoff_authorize_error, static_cast<LONG>(authorize_error));
    // The controller has a separate input queue. Do not synchronously wait for
    // that queue, and do not confuse SetForegroundWindow's return with actual
    // foreground arrival. Each request is issued once; Poll only observes.
    Require(ShowWindowAsync(controller, SW_SHOWNORMAL) != FALSE, "Cannot queue the owned controller visibility request.");
    SetLastError(ERROR_SUCCESS);
    BOOL foreground = SetForegroundWindow(controller);
    DWORD foreground_error = GetLastError();
    Publish(&shared.focus_handoff_foreground_result, foreground ? 1 : 0);
    Publish(&shared.focus_handoff_foreground_error, static_cast<LONG>(foreground_error));
  }
  void ObserveControllerFocus() {
    if (!focus_handoff_pending) return;
    Require(Read(&shared.focus_handoff_sequence) == focus_handoff_sequence &&
                Read(&shared.focus_handoff_ack) == focus_handoff_sequence - 1,
            "Pending controller focus identity changed.");
    DWORD pid = 0; GetWindowThreadProcessId(focus_handoff_target, &pid);
    Require(IsWindow(focus_handoff_target) && pid == shared.controller_pid &&
                reinterpret_cast<uintptr_t>(focus_handoff_target) == shared.controller_hwnd &&
                GetAncestor(focus_handoff_target, GA_ROOT) == focus_handoff_target,
            "Pending controller HWND identity changed.");
    Require(GetTickCount64() < focus_handoff_deadline,
            "Owned controller foreground handoff timed out; no activation request was replayed.");
    if (GetForegroundWindow() == focus_handoff_target && IsWindowVisible(focus_handoff_target)) {
      focus_handoff_pending = false;
      Publish(&shared.focus_handoff_ack, focus_handoff_sequence);
    }
  }
  void Control(ControlAction action) {
    Require(!exit_pending && !focus_handoff_pending, "Control arrived while fixture is closing or handing off focus.");
    if (action == ControlAction::show) ShowInteractive();
    else if (action == ControlAction::toggle) {
      if (GetForegroundWindow() == top) { Hide(); FocusController(); }
      else ShowInteractive();
    } else if (action == ControlAction::exit) BeginExit(0, ExitCause::control);
    else throw std::runtime_error("Unexpected fixture control action.");
  }
  void TrayCommand(UINT command) {
    Require(!focus_handoff_pending, "Tray command cannot interrupt controller focus handoff.");
    if (command == kMenuShow) ShowInteractive();
    else if (command == kMenuPassthrough) ApplyMode(!passthrough);
    else if (command == kMenuExit) BeginExit(0, ExitCause::tray);
    else throw std::runtime_error("Unknown fixture tray command.");
  }
  void BeginExit(int code, ExitCause cause) {
    if (exited) return;
    if (!exit_pending || code != 0) { exit_code = code; exit_cause = cause; }
    exit_pending = true;
    focus_handoff_pending = false;
    if (server) server->Stop();
    if (tray && tray->MenuActive()) { tray->CancelMenu(); return; }
    CompleteExit();
  }
  void CompleteExit() {
    if (exited || (tray && tray->MenuActive())) return;
    bool deleted = tray && tray->Delete();
    if (!deleted && exit_code == 0) { Error("Shell did not confirm fixture tray deletion."); exit_code = 1; }
    Publish(&shared.exit_cause, static_cast<LONG>(exit_cause));
    exited = true;
    PostQuitMessage(exit_code);
  }
  void Poll() {
    if (Read(&shared.controller_abort)) BeginExit(1, ExitCause::controllerAbort);
    if (exit_pending) { CompleteExit(); return; }
    ObserveControllerFocus();
    if (focus_handoff_pending) return;
    if (server) server->Poll();
    if (exit_pending) { CompleteExit(); return; }
    if (focus_handoff_pending) return;
    LONG sequence = Read(&shared.fixture_sequence), ack = Read(&shared.fixture_ack);
    if (sequence != ack) {
      Require(sequence > 0 && sequence == ack + 1, "Fixture command sequence is ambiguous.");
      LONG command = Read(&shared.fixture_command);
      if (command == 1) ApplyMode(true);
      else if (command == 2) { Hide(); FocusController(); }
      else throw std::runtime_error("Unknown fixed fixture command.");
      Publish(&shared.fixture_ack, sequence);
    }
    if (focus_handoff_pending) return;
    if (tray) tray->PollMenu();
    if (!tray || !tray->MenuActive()) ApplyMode(passthrough);
    Publish(&shared.visible, IsWindowVisible(top) ? 1 : 0);
  }
  static LRESULT CALLBACK ChildProc(HWND window, UINT message, WPARAM wparam,
                                    LPARAM lparam, UINT_PTR, DWORD_PTR reference) {
    auto* self = reinterpret_cast<Impl*>(reference);
    if (static_cast<uint64_t>(GetMessageExtraInfo()) == self->shared.input_marker) {
      if (message == WM_LBUTTONDOWN) InterlockedIncrement(&self->shared.native_down);
      if (message == WM_LBUTTONUP) InterlockedIncrement(&self->shared.native_up);
    }
    if (self->passthrough && message == WM_NCHITTEST) return HTTRANSPARENT;
    if (self->passthrough && message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    return DefSubclassProc(window, message, wparam, lparam);
  }
};

LifecyclePrimary::LifecyclePrimary(HWND top, flutter::FlutterViewController* controller, Shared& shared)
    : impl_(std::make_unique<Impl>(top, controller, shared)) { queued_primary = this; }
LifecyclePrimary::~LifecyclePrimary() { if (queued_primary == this) queued_primary = nullptr; }
void LifecyclePrimary::ObserveQueuedMessage(const MSG& message) {
  if (!queued_primary) return;
  auto& self = *queued_primary->impl_;
  if (message.hwnd == self.child && message.message == WM_KEYDOWN && message.wParam == VK_F24 &&
      static_cast<uint64_t>(GetMessageExtraInfo()) == self.shared.input_marker &&
      (static_cast<uint64_t>(message.lParam) & (uint64_t{1} << 30)) == 0) {
    InterlockedIncrement(&self.shared.native_key);
  }
}
mystia_window_probe::ErrorOr<int64_t> LifecyclePrimary::PublishUi(
    const mystia_window_probe::LifecycleUiEvidence& evidence) {
  try {
    Require(Read(&impl_->shared.error_code) == 0, "Fixture is not accepting UI evidence after a native failure.");
    Require(evidence.git_sha() == MYSTIA_WINDOW_PROBE_GIT_SHA && evidence.git_sha() == impl_->shared.git_sha,
            "Fixture UI build identity differs.");
    Require(evidence.sequence() == static_cast<int64_t>(Read(&impl_->shared.ui_sequence)) + 1 &&
                evidence.sequence() <= 1000000 && evidence.pointer_down() >= Read(&impl_->shared.ui_pointer) &&
                evidence.pointer_down() <= 1000000 && evidence.key_down() >= Read(&impl_->shared.ui_key) &&
                evidence.key_down() <= 1000000, "Fixture UI evidence sequence/counters are invalid.");
    Require(evidence.ready(), "Fixture UI readiness cannot be revoked.");
    if (Read(&impl_->shared.ui_ready) == 0) {
      Require(!impl_->exit_pending && impl_->server && impl_->server->port() != 0 &&
                  Read(&impl_->shared.tray_versioned) == 1 && IsWindow(impl_->child),
              "Fixture UI readiness lacks native prerequisites.");
    }
    // Already queued read-only focus/counter observations may arrive between a
    // normal Stop/NIM_DELETE and WM_QUIT. Validate and publish them without
    // reopening the fixture or turning successful shutdown into a new error.
    Publish(&impl_->shared.ui_pointer, static_cast<LONG>(evidence.pointer_down()));
    Publish(&impl_->shared.ui_key, static_cast<LONG>(evidence.key_down()));
    Publish(&impl_->shared.ui_focused, evidence.focused() ? 1 : 0);
    Publish(&impl_->shared.ui_sequence, static_cast<LONG>(evidence.sequence()));
    Publish(&impl_->shared.ui_ready, 1);
    return evidence.sequence();
  } catch (const std::exception& error) {
    impl_->Fail(error.what());
    return mystia_window_probe::FlutterError("fixture-error", error.what());
  }
}
std::optional<LRESULT> LifecyclePrimary::HandleWindowMessage(UINT message, WPARAM wparam, LPARAM lparam) {
  try {
    if (impl_->focus_handoff_pending && message == kTrayCallback) {
      UINT event = LOWORD(lparam);
      Require(event != NIN_SELECT && event != NIN_KEYSELECT && event != WM_CONTEXTMENU,
              "Tray activation cannot interrupt pending controller focus handoff.");
    }
    if (impl_->tray) {
      auto result = impl_->tray->HandleWindowMessage(message, wparam, lparam);
      if (result) {
        if (impl_->exit_pending) impl_->CompleteExit();
        else if (!impl_->tray->MenuActive()) impl_->ApplyMode(impl_->passthrough);
        return result;
      }
    }
    if (message == WM_TIMER && wparam == kTimer) { impl_->Poll(); return 0; }
    if (message == WM_CLOSE) { impl_->Hide(); return 0; }
    if (message == WM_MOUSEACTIVATE && impl_->passthrough) return MA_NOACTIVATE;
    if (message == WM_NCHITTEST && impl_->passthrough) return HTTRANSPARENT;
    return std::nullopt;
  } catch (const std::exception& error) { impl_->Fail(error.what()); return 0; }
}
