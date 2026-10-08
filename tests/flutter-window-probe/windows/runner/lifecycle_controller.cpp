#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include "lifecycle_controller.h"
#include "shell_tray_driver.h"
#include <bcrypt.h>
#include <tlhelp32.h>
#include <algorithm>
#include <cstring>
#include <sstream>
#include <stdexcept>

using namespace mystia_window_probe;
using namespace lifecycle_probe;
namespace {
void Require(bool value, const char* message) {
  if (!value) throw std::runtime_error(message);
}
uint64_t Number(const std::string& value, int base) {
  Require(!value.empty() && value.find_first_not_of(base == 16 ? "0123456789abcdef" : "0123456789") == std::string::npos,
          "Invalid internal numeric identity.");
  size_t used = 0; const auto result = std::stoull(value, &used, base);
  Require(used == value.size() && result != 0, "Missing internal numeric identity.");
  return result;
}
std::wstring ProcessPath(HANDLE process) {
  std::wstring path(32768, L'\0'); DWORD size = static_cast<DWORD>(path.size());
  Require(QueryFullProcessImageNameW(process, 0, path.data(), &size) != FALSE, "Cannot inspect retained executable identity.");
  path.resize(size); return path;
}
bool SamePath(HANDLE process) { return _wcsicmp(ProcessPath(process).c_str(), ProcessPath(GetCurrentProcess()).c_str()) == 0; }
bool Alive(HANDLE process) {
  if (!process) return false;
  DWORD wait = WaitForSingleObject(process, 0);
  Require(wait == WAIT_TIMEOUT || wait == WAIT_OBJECT_0, "Retained process wait failed.");
  return wait == WAIT_TIMEOUT;
}
int64_t ExitCode(HANDLE process) {
  DWORD result = 0;
  Require(process && !Alive(process) && GetExitCodeProcess(process, &result), "Retained process exit is not available.");
  return result;
}
DWORD Owner(HWND window) { DWORD pid = 0; if (window) GetWindowThreadProcessId(window, &pid); return pid; }
HWND Window(uint64_t value) { return reinterpret_cast<HWND>(static_cast<uintptr_t>(value)); }
int64_t Value(HWND value) { return static_cast<int64_t>(reinterpret_cast<uintptr_t>(value)); }
LONG MainWindowCount(DWORD pid, const std::wstring& known_class) {
  struct Census { DWORD pid; const wchar_t* class_name; LONG count; } census{pid, known_class.c_str(), 0};
  Require(EnumWindows([](HWND window, LPARAM context) -> BOOL {
    auto& current = *reinterpret_cast<Census*>(context);
    if (Owner(window) != current.pid) return TRUE;
    wchar_t name[256]{};
    if (GetClassNameW(window, name, 256) > 0 && wcscmp(name, current.class_name) == 0) ++current.count;
    return TRUE;
  }, reinterpret_cast<LPARAM>(&census)) != FALSE, "Cannot enumerate lifecycle main windows.");
  return census.count;
}
bool PortReleased(LONG port) {
  Require(port > 0 && port != 32145 && port != 32146, "Invalid isolated control port.");
  DWORD size = 0;
  DWORD result = GetExtendedTcpTable(nullptr, &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
  Require(result == ERROR_INSUFFICIENT_BUFFER, "Cannot size control listener observation.");
  std::vector<unsigned char> bytes(size);
  result = GetExtendedTcpTable(bytes.data(), &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
  Require(result == NO_ERROR, "Cannot observe control listeners.");
  const auto* table = reinterpret_cast<MIB_TCPTABLE_OWNER_PID*>(bytes.data());
  for (DWORD i = 0; i < table->dwNumEntries; ++i) {
    const auto& row = table->table[i];
    if (ntohs(static_cast<u_short>(row.dwLocalPort)) == port &&
        (row.dwLocalAddr == htonl(INADDR_LOOPBACK) || row.dwLocalAddr == INADDR_ANY)) return false;
  }
  return true;
}
void IdleInput() {
  for (int key : {VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2, VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN, VK_F24})
    Require((GetAsyncKeyState(key) & 0x8000) == 0, "User input is active; lifecycle input is blocked.");
}
void DesktopGuard() {
  DWORD session = MAXDWORD;
  Require(ProcessIdToSessionId(GetCurrentProcessId(), &session) && session == WTSGetActiveConsoleSessionId(),
          "Lifecycle input left the physical console.");
  HDESK desktop = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
  Require(desktop != nullptr, "Cannot inspect lifecycle input desktop.");
  wchar_t name[256]{}; DWORD needed = 0;
  BOOL read = GetUserObjectInformationW(desktop, UOI_NAME, name, sizeof(name), &needed);
  CloseDesktop(desktop);
  Require(read && wcscmp(name, L"Default") == 0, "Lifecycle desktop changed or locked.");
  Require(GetUserObjectInformationW(GetProcessWindowStation(), UOI_NAME, name, sizeof(name), &needed) && wcscmp(name, L"WinSta0") == 0,
          "Lifecycle input is not on the interactive station.");
}
std::string Escape(const char* value) {
  std::ostringstream out;
  for (const unsigned char* p = reinterpret_cast<const unsigned char*>(value); *p; ++p) {
    if (*p == '"' || *p == '\\') out << '\\' << *p;
    else if (*p >= 32 && *p < 127) out << *p;
    else out << '?';
  }
  return out.str();
}
}

ChildContext::ChildContext(const std::vector<std::string>& args) {
  try {
    const bool peer = !args.empty() && args[0] == "--lifecycle-peer";
    Require(args.size() == (peer ? 7U : 5U) && (peer || args[0] == "--lifecycle-primary") &&
            args[1] == "--map" && args[3] == "--nonce", "Invalid internal lifecycle invocation.");
    if (peer) {
      Require(args[5] == "--action", "Missing peer action.");
      const auto action = Number(args[6], 10);
      Require(action >= 1 && action <= 4, "Invalid peer action.");
      action_ = static_cast<ControlAction>(action);
    }
    mapping_ = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(Number(args[2], 10)));
    DWORD flags = 0;
    Require(GetHandleInformation(mapping_, &flags) && (flags & HANDLE_FLAG_INHERIT), "Lifecycle mapping was not inherited.");
    shared_ = static_cast<Shared*>(MapViewOfFile(mapping_, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Shared)));
    Require(shared_ && shared_->magic == kMagic && shared_->nonce == Number(args[4], 16) &&
            std::strcmp(shared_->git_sha, MYSTIA_WINDOW_PROBE_GIT_SHA) == 0,
            "Inherited lifecycle identity mismatch.");
    Require(shared_->generation == 1 || shared_->generation == 2, "Invalid lifecycle generation.");
    parent_ = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, shared_->controller_pid);
    Require(Alive(parent_) && SamePath(parent_), "Lifecycle controller executable is not retained.");
    DWORD our_session = 0, parent_session = 0;
    Require(ProcessIdToSessionId(GetCurrentProcessId(), &our_session) &&
            ProcessIdToSessionId(shared_->controller_pid, &parent_session) && our_session == parent_session &&
            our_session == WTSGetActiveConsoleSessionId(), "Lifecycle process session mismatch.");
    Require(Owner(Window(shared_->controller_hwnd)) == shared_->controller_pid, "Controller window identity mismatch.");
    Require(static_cast<DWORD>(Read(peer ? &shared_->peer_pid : &shared_->primary_pid)) == GetCurrentProcessId(),
            "Lifecycle child PID was not published before resume.");
  } catch (...) {
    if (shared_) UnmapViewOfFile(shared_);
    if (parent_) CloseHandle(parent_);
    // Do not close an unvalidated command-line handle on failed construction.
    throw;
  }
}
ChildContext::~ChildContext() {
  if (shared_) UnmapViewOfFile(shared_);
  if (mapping_) CloseHandle(mapping_);
  if (parent_) CloseHandle(parent_);
}

struct LifecycleController::Impl {
  HWND top;
  std::wstring run_id;
  HANDLE mapping = nullptr, primary = nullptr, peer = nullptr;
  Shared* shared = nullptr;
  DWORD primary_pid = 0, peer_pid = 0;
  LONG last_port = 0, generation = 0;
  int64_t sequence = 0;
  bool primary_path_matched = false;
  std::wstring primary_class;
  bool foreground_grant = false;
  DWORD foreground_grant_error = 0;
  POINT original_cursor{}, last_cursor{};
  bool cursor_owned = false, cursor_restored = false;
  std::unique_ptr<ShellTrayDriver> tray;

  Impl(HWND window, const std::vector<std::string>& args) : top(window) {
    for (size_t i = 0; i + 1 < args.size(); ++i) if (args[i] == "--run-id") run_id.assign(args[i + 1].begin(), args[i + 1].end());
    Require(!run_id.empty() && run_id.size() <= 80, "Missing validated lifecycle run ID.");
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
    mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &security, PAGE_READWRITE, 0, sizeof(Shared), nullptr);
    Require(mapping != nullptr, "Cannot create lifecycle mapping.");
    shared = static_cast<Shared*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Shared)));
    if (!shared) { CloseHandle(mapping); mapping = nullptr; throw std::runtime_error("Cannot map lifecycle state."); }
    ZeroMemory(shared, sizeof(Shared));
  }
  ~Impl() {
    try { if (shared && Alive(primary)) Publish(&shared->controller_abort, 1); } catch (...) {}
    try { RestoreCursor(); } catch (...) {}
    tray.reset();
    if (peer) CloseHandle(peer);
    if (primary) CloseHandle(primary);
    if (shared) UnmapViewOfFile(shared);
    if (mapping) CloseHandle(mapping);
  }
  void Spawn(bool secondary, ControlAction action = ControlAction::none) {
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    std::vector<unsigned char> bytes(size);
    auto* attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(bytes.data());
    Require(InitializeProcThreadAttributeList(attributes, 1, 0, &size) != FALSE, "Cannot prepare lifecycle inheritance.");
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &mapping, sizeof(mapping), nullptr, nullptr)) {
      DeleteProcThreadAttributeList(attributes); throw std::runtime_error("Cannot restrict lifecycle inherited handles.");
    }
    const auto path = ProcessPath(GetCurrentProcess());
    std::wostringstream command;
    command << L'"' << path << L"\" " << (secondary ? L"--lifecycle-peer" : L"--lifecycle-primary")
            << L" --map " << reinterpret_cast<uintptr_t>(mapping) << L" --nonce " << std::hex << shared->nonce;
    if (secondary) command << L" --action " << std::dec << static_cast<LONG>(action);
    auto line = command.str(); STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup); startup.lpAttributeList = attributes;
    PROCESS_INFORMATION info{};
    const BOOL created = CreateProcessW(path.c_str(), line.data(), nullptr, nullptr, TRUE,
        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
        nullptr, nullptr, &startup.StartupInfo, &info);
    DeleteProcThreadAttributeList(attributes);
    Require(created != FALSE, "Cannot launch retained lifecycle child.");
    if (secondary) { peer = info.hProcess; peer_pid = info.dwProcessId; Publish(&shared->peer_pid, static_cast<LONG>(peer_pid)); }
    else { primary = info.hProcess; primary_pid = info.dwProcessId; Publish(&shared->primary_pid, static_cast<LONG>(primary_pid)); }
    // Validate the suspended process: a fast peer may already have exited by
    // the time the controller regains execution after ResumeThread.
    try { Require(SamePath(info.hProcess), "Spawned lifecycle image mismatch."); }
    catch (...) { TerminateProcess(info.hProcess, ERROR_PROCESS_ABORTED); CloseHandle(info.hThread); throw; }
    if (!secondary) primary_path_matched = true;
    // Never grant to the secondary: doing so revokes an earlier primary grant.
    if (!secondary) GrantForeground(info.dwProcessId);
    const DWORD resumed = ResumeThread(info.hThread); CloseHandle(info.hThread);
    if (resumed == static_cast<DWORD>(-1)) {
      TerminateProcess(info.hProcess, ERROR_PROCESS_ABORTED);
      throw std::runtime_error("Cannot resume retained lifecycle child.");
    }
  }
  void GrantForeground(DWORD pid) {
    SetLastError(ERROR_SUCCESS);
    foreground_grant = AllowSetForegroundWindow(pid) != FALSE;
    foreground_grant_error = GetLastError();
  }
  void Start(LONG requested_generation) {
    Require(requested_generation == generation + 1 && requested_generation <= 2, "Lifecycle generations must advance once.");
    Require(!Alive(primary) && !Alive(peer), "Previous lifecycle generation is still alive.");
    if (!generation) Require(!IsWindowVisible(top) && (GetWindowLongPtrW(top, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0,
                            "Core controller has not retired its window.");
    if (generation) {
      Require(ExitCode(primary) == 0 && Read(&shared->tray_deleted) == 1 && tray && tray->TrayAbsent() &&
              PortReleased(Read(&shared->control_port)), "Previous lifecycle cleanup is incomplete.");
      last_port = Read(&shared->control_port);
    }
    if (primary) { CloseHandle(primary); primary = nullptr; }
    if (peer) { CloseHandle(peer); peer = nullptr; }
    peer_pid = 0; primary_pid = 0; primary_path_matched = false; primary_class.clear();
    ZeroMemory(shared, sizeof(Shared));
    shared->magic = kMagic; shared->controller_pid = GetCurrentProcessId(); shared->controller_hwnd = Value(top);
    shared->generation = requested_generation; shared->requested_port = last_port;
    std::memcpy(shared->git_sha, MYSTIA_WINDOW_PROBE_GIT_SHA, sizeof(shared->git_sha));
    wcscpy_s(shared->run_id, run_id.c_str());
    Require(BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&shared->nonce), sizeof(shared->nonce), BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0,
            "Cannot generate lifecycle identity.");
    shared->nonce &= 0x7fffffffffffffffULL;
    Require(shared->nonce != 0 && BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&shared->input_marker), sizeof(shared->input_marker),
            BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0, "Cannot generate lifecycle input identity.");
    shared->input_marker &= 0x7fffffffU;
    Require(shared->input_marker != 0 && CoCreateGuid(&shared->tray_guid) == S_OK, "Cannot generate lifecycle tray identity.");
    const auto tooltip = L"Mystia lifecycle " + run_id + L" #" + std::to_wstring(requested_generation);
    wcscpy_s(shared->tray_tooltip, tooltip.c_str());
    strcpy_s(shared->expected_endpoint, "http://127.0.0.1:1/lifecycle-fixture");
    strcpy_s(shared->expected_token, "synthetic-lifecycle-token-no-game-access");
    generation = requested_generation;
    if (!tray) tray = std::make_unique<ShellTrayDriver>(*shared);
    Spawn(false);
  }
  void CheckPrimary() {
    Require(Alive(primary) && primary_path_matched && Read(&shared->error_code) == 0, "Lifecycle primary is not healthy/alive.");
    Require(static_cast<DWORD>(Read(&shared->primary_pid)) == primary_pid && Read(&shared->ui_ready) == 1,
            "Lifecycle primary UI is not ready.");
    Require(Read(&shared->focus_handoff_sequence) == Read(&shared->focus_handoff_ack),
            "Lifecycle foreground handoff is still pending.");
    HWND window = Window(Read(&shared->top_hwnd)), child = Window(Read(&shared->child_hwnd));
    Require(IsWindow(window) && Owner(window) == primary_pid && IsWindow(child) && Owner(child) == primary_pid &&
            GetAncestor(child, GA_ROOT) == window, "Lifecycle HWND ownership changed.");
  }
  void Secondary(ControlAction action) {
    CheckPrimary(); Require(!Alive(peer), "Previous secondary is still running.");
    if (peer) { Require(ExitCode(peer) == 0 && Read(&shared->peer_done), "Previous secondary did not complete."); CloseHandle(peer); peer = nullptr; }
    Publish(&shared->peer_done, 0); Publish(&shared->peer_error, 0); Publish(&shared->peer_pid, 0);
    Publish(&shared->peer_action, static_cast<LONG>(action));
    Publish(&shared->peer_request_id, Read(&shared->peer_request_id) + 1);
    Publish(&shared->peer_bind_error, 0); Publish(&shared->peer_server_pid, 0); Publish(&shared->peer_bytes_sent, 0);
    Publish(&shared->peer_window_count, 0);
    GrantForeground(primary_pid);
    Spawn(true, action);
  }
  void Input(bool keyboard) {
    CheckPrimary(); DesktopGuard(); IdleInput();
    HWND window = Window(Read(&shared->top_hwnd)), child = Window(Read(&shared->child_hwnd));
    Require(IsWindowVisible(window) && GetForegroundWindow() == window && Read(&shared->passthrough) == 0,
            "Lifecycle input requires the exact interactive foreground window.");
    GUITHREADINFO guard{sizeof(guard)};
    Require(GetGUIThreadInfo(GetWindowThreadProcessId(window, nullptr), &guard) && !guard.hwndCapture &&
            !(guard.flags & (GUI_INMENUMODE | GUI_INMOVESIZE)), "Lifecycle input capture/menu/drag is active.");
    if (keyboard) {
      GUITHREADINFO gui{sizeof(gui)};
      Require(GetGUIThreadInfo(GetWindowThreadProcessId(window, nullptr), &gui) && gui.hwndFocus == child,
              "Lifecycle keyboard focus is not the exact Flutter child.");
      INPUT keys[2]{};
      keys[0].type = keys[1].type = INPUT_KEYBOARD; keys[0].ki.wVk = keys[1].ki.wVk = VK_F24;
      keys[1].ki.dwFlags = KEYEVENTF_KEYUP;
      keys[0].ki.dwExtraInfo = keys[1].ki.dwExtraInfo = shared->input_marker;
      Require(SendInput(2, keys, sizeof(INPUT)) == 2, "Lifecycle focus key injection failed.");
      return;
    }
    RECT rect{}; POINT point{80, 80}, previous{};
    Require(GetClientRect(child, &rect) && rect.right > 100 && rect.bottom > 100 && ClientToScreen(child, &point) && GetCursorPos(&previous),
            "Lifecycle input geometry unavailable.");
    Require(WindowFromPoint(point) == child, "Lifecycle input point is covered by another window.");
    if (!cursor_owned) original_cursor = previous;
    cursor_restored = false;
    INPUT inputs[3]{};
    inputs[0].type = inputs[1].type = inputs[2].type = INPUT_MOUSE;
    const int left = GetSystemMetrics(SM_XVIRTUALSCREEN), upper = GetSystemMetrics(SM_YVIRTUALSCREEN);
    const int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    Require(width > 1 && height > 1, "Invalid lifecycle desktop geometry.");
    inputs[0].mi.dx = MulDiv(point.x - left, 65535, width - 1); inputs[0].mi.dy = MulDiv(point.y - upper, 65535, height - 1);
    inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
    inputs[1].mi.dwFlags = MOUSEEVENTF_LEFTDOWN; inputs[2].mi.dwFlags = MOUSEEVENTF_LEFTUP;
    for (auto& input : inputs) input.mi.dwExtraInfo = shared->input_marker;
    UINT inserted = SendInput(3, inputs, sizeof(INPUT));
    if (inserted) { cursor_owned = true; last_cursor = point; }
    Require(inserted == 3, "Lifecycle pointer injection failed.");
    // Do not restore immediately: queued mouse messages must reach the sample
    // before moving away. Shell driver restores its separately owned movements.
  }
  void RestoreCursor() {
    POINT current{};
    if (!cursor_owned || cursor_restored || !GetCursorPos(&current) || current.x != last_cursor.x || current.y != last_cursor.y) return;
    DesktopGuard(); IdleInput();
    cursor_restored = SetCursorPos(original_cursor.x, original_cursor.y) != FALSE;
  }
  LifecycleSnapshot Snapshot() {
    Require(shared && generation > 0, "Lifecycle has not started.");
    const bool alive = Alive(primary);
    HWND fg = GetForegroundWindow();
    HWND window = Window(Read(&shared->top_hwnd)), child = Window(Read(&shared->child_hwnd));
    const bool valid_window = alive && IsWindow(window) && IsWindow(child) && Owner(window) == primary_pid && Owner(child) == primary_pid;
    if (primary_class.empty() && alive && IsWindow(window) && Owner(window) == primary_pid) {
      wchar_t class_name[256]{};
      Require(GetClassNameW(window, class_name, 256) > 0, "Cannot bind lifecycle main-window class.");
      primary_class = class_name;
    }
    const LONG window_count = alive && !primary_class.empty() ? MainWindowCount(primary_pid, primary_class) : 0;
    bool port_released = false, tray_absent = false;
    if (!alive && Read(&shared->control_port) > 0) { port_released = PortReleased(Read(&shared->control_port)); }
    if (!alive && Read(&shared->tray_deleted) == 1) { tray_absent = tray && tray->TrayAbsent(); }
    std::ostringstream diagnostics;
    diagnostics << "{\"exitCause\":" << Read(&shared->exit_cause) << ",\"primaryErrorCode\":" << Read(&shared->error_code)
                << ",\"primaryErrorMessage\":\"" << Escape(shared->error_message) << "\",\"portReleased\":" << (port_released ? "true" : "false")
                << ",\"trayAbsent\":" << (tray_absent ? "true" : "false") << ",\"fixtureSequence\":" << Read(&shared->fixture_sequence)
                << ",\"fixtureAck\":" << Read(&shared->fixture_ack) << ",\"exitObservedByRetainedHandle\":" << (!alive ? "true" : "false")
                << ",\"menuOpen\":" << (Read(&shared->menu_open) ? "true" : "false")
                << ",\"menuRectsReady\":" << (Read(&shared->menu_rects_ready) ? "true" : "false")
                << ",\"focusHandoffSequence\":" << Read(&shared->focus_handoff_sequence)
                << ",\"focusHandoffAck\":" << Read(&shared->focus_handoff_ack)
                << ",\"focusHandoffAuthorizeResult\":" << Read(&shared->focus_handoff_authorize_result)
                << ",\"focusHandoffAuthorizeError\":" << Read(&shared->focus_handoff_authorize_error)
                << ",\"focusHandoffForegroundResult\":" << Read(&shared->focus_handoff_foreground_result)
                << ",\"focusHandoffForegroundError\":" << Read(&shared->focus_handoff_foreground_error)
                << ",\"cursorRestored\":" << (cursor_restored ? "true" : "false")
                << ",\"foregroundGrantResult\":" << (foreground_grant ? "true" : "false")
                << ",\"foregroundGrantError\":" << foreground_grant_error
                << ",\"tray\":" << (tray ? tray->DiagnosticsJson() : "{}") << "}";
    LifecycleSnapshot result(generation, ++sequence, GetCurrentProcessId(), alive, primary_path_matched,
        window_count, MYSTIA_WINDOW_PROBE_GIT_SHA, Read(&shared->ui_ready) ? shared->git_sha : "",
        Value(fg), Owner(fg), Read(&shared->native_down), Read(&shared->native_up), Read(&shared->native_key), Read(&shared->ui_ready) == 1,
        Read(&shared->ui_pointer), Read(&shared->ui_key), Read(&shared->ui_focused) == 1, Read(&shared->ui_sequence),
        Read(&shared->tray_added) == 1, Read(&shared->tray_versioned) == 1, Read(&shared->tray_deleted) == 1,
        Read(&shared->tray_callbacks), Read(&shared->menu_commands), Read(&shared->control_port),
        Read(&shared->control_applied), Read(&shared->control_rejected), Read(&shared->control_receive_calls), Read(&shared->control_pending_bytes),
        Read(&shared->connection_updates), Read(&shared->connection_activations), Read(&shared->connection_game_pid),
        Read(&shared->endpoint_matches) == 1, Read(&shared->token_matches) == 1, Read(&shared->peer_request_id), Read(&shared->peer_done) == 1,
        Read(&shared->peer_bind_error), Read(&shared->peer_server_pid), Read(&shared->peer_bytes_sent), Read(&shared->peer_window_count),
        Read(&shared->peer_error), diagnostics.str());
    result.set_primary_process_id(primary_pid);
    if (!alive) result.set_primary_exit_code(ExitCode(primary));
    if (valid_window) {
      result.set_top_hwnd(Value(window)); result.set_child_hwnd(Value(child)); result.set_visible(IsWindowVisible(window) != FALSE);
      const auto style = GetWindowLongPtrW(window, GWL_EXSTYLE);
      const bool pass = (style & (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) == (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
      const bool expected_pass = Read(&shared->passthrough) == 1;
      GUITHREADINFO menu_gui{sizeof(menu_gui)};
      const bool menu_lease = Read(&shared->menu_open) && Read(&shared->menu_handle) &&
          GetGUIThreadInfo(GetWindowThreadProcessId(window, nullptr), &menu_gui) &&
          menu_gui.hwndMenuOwner == window && (menu_gui.flags & GUI_INMENUMODE) &&
          (style & WS_EX_TRANSPARENT) && !(style & WS_EX_NOACTIVATE);
      // A cross-process snapshot may land between ApplyMode's native write and
      // its counter publication. Report the native state; completed-operation
      // assertions are gated by the fixture/control acknowledgement counters.
      result.set_input_mode((pass || (expected_pass && menu_lease)) ? ProbeInputMode::kPassThrough : ProbeInputMode::kInteractive);
      GUITHREADINFO gui{sizeof(gui)};
      Require(GetGUIThreadInfo(GetWindowThreadProcessId(window, nullptr), &gui) != FALSE, "Cannot read lifecycle thread focus.");
      result.set_focus_hwnd(Value(gui.hwndFocus));
    }
    if (peer_pid) {
      result.set_secondary_pid(peer_pid);
      if (!Alive(peer)) result.set_secondary_exit_code(ExitCode(peer));
    }
    const auto action = Read(&shared->control_last_action);
    if (action >= 1 && action <= 3) result.set_control_last_action(static_cast<LifecycleControlAction>(action - 1));
    const auto peer_action = Read(&shared->peer_action);
    if (peer_action >= 1 && peer_action <= 3) result.set_secondary_action(static_cast<LifecycleControlAction>(peer_action - 1));
    const auto menu = Read(&shared->last_menu_command);
    if (menu == kMenuShow) result.set_tray_last_action(LifecycleTrayAction::kShow);
    else if (menu == kMenuPassthrough) result.set_tray_last_action(LifecycleTrayAction::kPassthrough);
    else if (menu == kMenuExit) result.set_tray_last_action(LifecycleTrayAction::kExit);
    else if (Read(&shared->tray_callbacks)) result.set_tray_last_action(LifecycleTrayAction::kActivate);
    return result;
  }
  void Execute(const LifecycleCommand& command, std::function<void(ErrorOr<LifecycleSnapshot>)> reply) {
    try {
      const auto operation = command.operation();
      Require(operation >= LifecycleOperation::kStartPrimary && operation <= LifecycleOperation::kAbort, "Unknown lifecycle operation.");
      Require(!tray || !tray->ClickPending() || operation == LifecycleOperation::kInspect ||
                  operation == LifecycleOperation::kAbort,
              "The existing Shell click request is still pending.");
      if (operation == LifecycleOperation::kStartPrimary) Start(static_cast<LONG>(command.generation()));
      else {
        Require(command.generation() == generation && generation > 0, "Stale lifecycle generation.");
        switch (operation) {
          case LifecycleOperation::kInspect:
            if (tray && tray->ClickPending()) { CheckPrimary(); tray->Poll(); }
            break;
          case LifecycleOperation::kSecondaryShow: Secondary(ControlAction::show); break;
          case LifecycleOperation::kSecondaryToggle: Secondary(ControlAction::toggle); break;
          case LifecycleOperation::kSecondaryExit: Secondary(ControlAction::exit); break;
          case LifecycleOperation::kSecondaryInvalid: Secondary(ControlAction::invalid); break;
          case LifecycleOperation::kSetPassThrough:
          case LifecycleOperation::kHidePrimary:
            CheckPrimary();
            Require(Read(&shared->fixture_sequence) == Read(&shared->fixture_ack), "Fixture operation is still pending.");
            Publish(&shared->fixture_command, operation == LifecycleOperation::kSetPassThrough ? 1 : 2);
            Publish(&shared->fixture_sequence, Read(&shared->fixture_sequence) + 1); break;
          case LifecycleOperation::kClickTray: CheckPrimary(); RestoreCursor(); tray->ClickTray(false); break;
          case LifecycleOperation::kOpenTrayMenu: CheckPrimary(); RestoreCursor(); tray->ClickTray(true); break;
          case LifecycleOperation::kTrayMenuShow: CheckPrimary(); tray->ClickTrayMenu(kMenuShow); break;
          case LifecycleOperation::kTrayMenuPassthrough: CheckPrimary(); tray->ClickTrayMenu(kMenuPassthrough); break;
          case LifecycleOperation::kTrayMenuExit: CheckPrimary(); tray->ClickTrayMenu(kMenuExit); break;
          case LifecycleOperation::kClickPrimary: Input(false); break;
          case LifecycleOperation::kSendFocusKey: Input(true); break;
          case LifecycleOperation::kAbort:
            if (tray) tray->CancelPendingClick();
            Publish(&shared->controller_abort, 1); break;
          default: throw std::runtime_error("Unsupported lifecycle operation.");
        }
      }
      reply(Snapshot());
    } catch (const TrayBlocked& error) {
      reply(Failure("blocked", error.what()));
    } catch (const std::exception& error) {
      reply(Failure("lifecycle_failed", error.what()));
    }
  }
  FlutterError Failure(const char* code, const char* message) {
    try {
      return FlutterError(code, message, ::flutter::EncodableValue(::flutter::CustomEncodableValue(Snapshot())));
    } catch (...) { return FlutterError(code, message); }
  }
};

LifecycleController::LifecycleController(HWND top, const std::vector<std::string>& arguments)
    : impl_(std::make_unique<Impl>(top, arguments)) {}
LifecycleController::~LifecycleController() = default;
void LifecycleController::Execute(const LifecycleCommand& command, std::function<void(ErrorOr<LifecycleSnapshot>)> reply) {
  impl_->Execute(command, std::move(reply));
}
