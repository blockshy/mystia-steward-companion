#include "window_bridge.h"

#include <bcrypt.h>
#include <commctrl.h>
#include <dwmapi.h>
#include <imm.h>
#include <windowsx.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <iomanip>
#include <limits>
#include <sstream>
#include <stdexcept>
#include <utility>

namespace {
using mystia_window_probe::ErrorOr;
using mystia_window_probe::FlutterError;
using mystia_window_probe::ProbeCommand;
using mystia_window_probe::ProbeInputMode;
using mystia_window_probe::ProbeOperation;
using mystia_window_probe::ProbeSnapshot;
using Reply = std::function<void(ErrorOr<ProbeSnapshot>)>;
constexpr UINT_PTR kTimer = 0x4d5750;
constexpr UINT kTargetCommand = WM_APP + 47;
constexpr int kHotkey = 0x4d50;
constexpr uint64_t kMagic = 0x4d59535457494e31ULL;
constexpr wchar_t kTargetClass[] = L"MystiaWindowProbeOwnedTarget";
constexpr wchar_t kExeName[] = L"mystia-steward-companion-window-probe.exe";
constexpr int kFocusTestVirtualKey = VK_F24;
constexpr char kFocusTestKeyName[] = "F24";

struct RawInputObservation {
  volatile LONG mouse_down, mouse_up, key_test_down;
  volatile LONG pointer_down, pointer_up;
  volatile LONG64 mouse_down_extra, mouse_up_extra, key_test_down_extra;
  volatile LONG64 pointer_down_extra, pointer_up_extra;
};

constexpr LONG kKeyboardObservationLimit = 8;
struct KeyboardEventObservation {
  LONG stage, message, virtual_key, scan_code;
  uint64_t hwnd, extra, flags, layout;
  LONG original_key_available, original_key;
  LONG ime_context_available, ime_open, ime_context_released;
};
struct KeyboardObservation {
  volatile LONG armed, count, dropped;
  volatile LONG64 deadline;
  KeyboardEventObservation events[kKeyboardObservationLimit];
};
// Each process has one GUI thread and one bound owned window pair. These
// pointers are detached before their storage/window lifetime ends.
thread_local KeyboardObservation* queued_keyboard = nullptr;
thread_local HWND queued_primary = nullptr, queued_secondary = nullptr;

struct Shared {
  uint64_t magic;
  // The inherited control identity and the Win32 input marker have separate
  // widths and independent random values; neither is derived from the other.
  uint64_t nonce;
  uint32_t input_marker;
  DWORD parent_pid;
  DWORD target_pid;
  LONG x, y, width, height;
  volatile LONG64 hwnd;
  volatile LONG command;
  volatile LONG command_sequence;
  volatile LONG acknowledged_sequence;
  volatile LONG color;
  volatile LONG mouse_down;
  volatile LONG mouse_up;
  volatile LONG key_down;
  volatile LONG authorization_result;
  volatile LONG mouse_in_pointer_enabled;
  RawInputObservation raw_input;
  KeyboardObservation keyboard;
};

LONG Read(volatile LONG* value) { return InterlockedCompareExchange(value, 0, 0); }
uint64_t Read(volatile LONG64* value) {
  return static_cast<uint64_t>(InterlockedCompareExchange64(value, 0, 0));
}
void ObserveKeyboard(KeyboardObservation& observation, HWND window, UINT message,
                     WPARAM wparam, LPARAM lparam, uint64_t extra, bool before_translate) {
  if (!Read(&observation.armed) || GetTickCount64() > Read(&observation.deadline) ||
      (message != WM_KEYDOWN && message != WM_SYSKEYDOWN &&
      message != WM_KEYUP && message != WM_SYSKEYUP)) return;
  LONG index = Read(&observation.count);
  if (index >= kKeyboardObservationLimit) { InterlockedIncrement(&observation.dropped); return; }
  KeyboardEventObservation event{};
  event.stage = before_translate ? 0 : 1;
  event.message = static_cast<LONG>(message); event.virtual_key = static_cast<LONG>(wparam);
  event.scan_code = static_cast<LONG>((static_cast<uint64_t>(lparam) >> 16) & 0xff);
  event.hwnd = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(window));
  event.extra = extra; event.flags = static_cast<uint64_t>(lparam);
  event.layout = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(GetKeyboardLayout(0)));
  if (before_translate && message == WM_KEYDOWN && wparam == VK_PROCESSKEY) {
    event.original_key_available = 1;
    event.original_key = static_cast<LONG>(ImmGetVirtualKey(window));
  }
  HIMC context = ImmGetContext(window);
  event.ime_context_available = context != nullptr;
  if (context) {
    event.ime_open = ImmGetOpenStatus(context) != FALSE;
    event.ime_context_released = ImmReleaseContext(window, context) != FALSE;
  }
  // One writer (the owning GUI thread), immutable published slots. The
  // interlocked count publishes the whole event before the other process reads.
  observation.events[index] = event;
  InterlockedExchange(&observation.count, index + 1);
}
void AppendKeyboard(std::ostringstream& out, KeyboardObservation& observation) {
  LONG count = Read(&observation.count);
  out << "{\"limit\":" << kKeyboardObservationLimit << ",\"dropped\":" << Read(&observation.dropped)
      << ",\"scope\":\"owned windows during fixed " << kFocusTestKeyName << " injection only\",\"events\":[";
  for (LONG index = 0; index < count && index < kKeyboardObservationLimit; ++index) {
    const auto& event = observation.events[index];
    if (index) out << ',';
    out << "{\"stage\":\"" << (event.stage == 0 ? "preTranslate" : "windowProc")
        << "\",\"message\":" << event.message << ",\"virtualKey\":" << event.virtual_key
        << ",\"scanCode\":" << event.scan_code << ",\"hwndHex\":\"" << std::hex << event.hwnd
        << "\",\"extraInfoHex\":\"" << event.extra << "\",\"lParamHex\":\"" << event.flags
        << "\",\"keyboardLayoutHex\":\"" << event.layout << "\"" << std::dec
        << ",\"originalKeyAvailable\":" << (event.original_key_available ? "true" : "false")
        << ",\"originalVirtualKey\":" << event.original_key
        << ",\"imeContextAvailable\":" << (event.ime_context_available ? "true" : "false")
        << ",\"imeOpen\":";
    if (event.ime_context_available) out << (event.ime_open ? "true" : "false");
    else out << "null";
    out << ",\"imeContextReleased\":" << (event.ime_context_released ? "true" : "false") << '}';
  }
  out << "]}";
}
void ObserveRawInput(RawInputObservation& raw, UINT message, WPARAM wparam, uint64_t extra) {
  volatile LONG* count = nullptr;
  volatile LONG64* last = nullptr;
  switch (message) {
    case WM_LBUTTONDOWN: count = &raw.mouse_down; last = &raw.mouse_down_extra; break;
    case WM_LBUTTONUP: count = &raw.mouse_up; last = &raw.mouse_up_extra; break;
    case WM_KEYDOWN:
      if (wparam == kFocusTestVirtualKey) { count = &raw.key_test_down; last = &raw.key_test_down_extra; }
      break;
    case WM_POINTERDOWN: count = &raw.pointer_down; last = &raw.pointer_down_extra; break;
    case WM_POINTERUP: count = &raw.pointer_up; last = &raw.pointer_up_extra; break;
  }
  if (count) {
    InterlockedExchange64(last, static_cast<LONG64>(extra));
    InterlockedIncrement(count);
  }
}
void AppendRawInput(std::ostringstream& out, RawInputObservation& raw) {
  out << "{\"mouseDown\":" << Read(&raw.mouse_down) << ",\"mouseUp\":" << Read(&raw.mouse_up)
      << ",\"keyTestDown\":" << Read(&raw.key_test_down) << ",\"pointerDown\":" << Read(&raw.pointer_down)
      << ",\"pointerUp\":" << Read(&raw.pointer_up)
      << ",\"lastMouseDownExtraHex\":\"" << std::hex << Read(&raw.mouse_down_extra)
      << "\",\"lastMouseUpExtraHex\":\"" << Read(&raw.mouse_up_extra)
      << "\",\"lastTestKeyDownExtraHex\":\"" << Read(&raw.key_test_down_extra)
      << "\",\"lastPointerDownExtraHex\":\"" << Read(&raw.pointer_down_extra)
      << "\",\"lastPointerUpExtraHex\":\"" << Read(&raw.pointer_up_extra) << "\"}" << std::dec;
}
int64_t HandleValue(HWND window) {
  return static_cast<int64_t>(reinterpret_cast<intptr_t>(window));
}
DWORD WindowPid(HWND window) {
  DWORD pid = 0;
  GetWindowThreadProcessId(window, &pid);
  return pid;
}
void Require(bool condition, const char* message) {
  if (!condition) throw std::runtime_error(message);
}
std::wstring Widen(const std::string& value) {
  int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
                                static_cast<int>(value.size()), nullptr, 0);
  Require(count > 0, "Expected nonempty strict UTF-8.");
  std::wstring result(static_cast<size_t>(count), L'\0');
  Require(MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
                             static_cast<int>(value.size()), result.data(), count) == count,
          "UTF-8 conversion failed.");
  return result;
}
std::string Narrow(const std::wstring& value) {
  int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                                 static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
  Require(count > 0, "Wide string conversion failed.");
  std::string result(static_cast<size_t>(count), '\0');
  Require(WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                             static_cast<int>(value.size()), result.data(), count,
                             nullptr, nullptr) == count, "Wide string conversion failed.");
  return result;
}
std::wstring ExecutablePath() {
  std::wstring path(32768, L'\0');
  DWORD size = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
  Require(size > 0 && size < path.size(), "Cannot identify current executable.");
  path.resize(size);
  return path;
}
std::wstring ProcessPath(HANDLE process) {
  std::wstring path(32768, L'\0');
  DWORD size = static_cast<DWORD>(path.size());
  Require(QueryFullProcessImageNameW(process, 0, path.data(), &size) != FALSE,
          "Cannot identify retained process executable.");
  path.resize(size);
  return path;
}
bool SamePath(const std::wstring& left, const std::wstring& right) {
  return _wcsicmp(left.c_str(), right.c_str()) == 0;
}
void PlainPath(const std::wstring& path, bool directory, bool absent = false) {
  Require(path.size() > 3 && path[1] == L':' && path[2] == L'\\', "Expected owned absolute drive path.");
  Require(path.find(L"..") == std::wstring::npos && path.find(L'/', 0) == std::wstring::npos,
          "Noncanonical probe path.");
  for (size_t end = 3; end <= path.size(); ++end) {
    if (end != path.size() && path[end] != L'\\') continue;
    const std::wstring component = path.substr(0, end);
    DWORD attributes = GetFileAttributesW(component.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
      Require(absent && end == path.size() && GetLastError() == ERROR_FILE_NOT_FOUND,
              "Probe path is missing or inaccessible.");
      continue;
    }
    Require(!(attributes & FILE_ATTRIBUTE_REPARSE_POINT), "Probe path contains a reparse point.");
    bool expected_directory = end != path.size() || directory;
    Require(((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) == expected_directory,
            "Probe path has an unexpected type.");
  }
}
struct Invocation { std::string run; std::wstring result; };
Invocation ParseInvocation(const std::vector<std::string>& args) {
  Require(args.size() == 7 && args[0] == "--probe" && args[1] == "--run-id" &&
              args[3] == "--suite" && args[4] == "all" && args[5] == "--result-file",
          "Expected the fixed node probe arguments.");
  const std::string& run = args[2];
  auto alnum = [](char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'); };
  Require(!run.empty() && run.size() <= 80 && alnum(run.front()) &&
              std::all_of(run.begin(), run.end(), [&](char c) { return alnum(c) || c == '_' || c == '-'; }),
          "Invalid fixed run identifier.");
  std::wstring root = L"D:\\dev\\mystia-node\\runs\\" + Widen(run);
  std::wstring expected = root + L"\\probe-result.json";
  std::wstring received = Widen(args[6]);
  std::replace(received.begin(), received.end(), L'/', L'\\');
  Require(SamePath(received, expected), "Result must be this node run's fixed probe-result.json.");
  PlainPath(root, true);
  PlainPath(root + L"\\payload", true);
  const auto executable = ExecutablePath();
  Require(SamePath(executable, root + L"\\payload\\" + kExeName), "Executable is outside this run payload.");
  PlainPath(executable, false);
  PlainPath(expected, false, true);
  Require(GetFileAttributesW(expected.c_str()) == INVALID_FILE_ATTRIBUTES, "Run result already exists; replay refused.");
  return {run, expected};
}
uint64_t ParseUnsigned(const std::string& value, int base = 10) {
  Require(!value.empty() && value.size() <= 20 && std::all_of(value.begin(), value.end(),
      [base](char c) { return (c >= '0' && c <= '9') || (base == 16 && c >= 'a' && c <= 'f'); }),
      "Malformed inherited target argument.");
  size_t consumed = 0;
  uint64_t number = std::stoull(value, &consumed, base);
  Require(consumed == value.size() && number != 0, "Invalid inherited target handle/identity.");
  return number;
}
struct TargetContext { Shared* shared; HANDLE parent; HANDLE ready; };
LRESULT CALLBACK TargetProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
  auto* context = reinterpret_cast<TargetContext*>(GetWindowLongPtrW(window, GWLP_USERDATA));
  if (message == WM_NCCREATE) {
    context = static_cast<TargetContext*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);
    SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(context));
  }
  if (!context) return DefWindowProcW(window, message, wparam, lparam);
  Shared* shared = context->shared;
  const uint64_t extra = static_cast<uint64_t>(GetMessageExtraInfo());
  ObserveRawInput(shared->raw_input, message, wparam, extra);
  ObserveKeyboard(shared->keyboard, window, message, wparam, lparam, extra, false);
  switch (message) {
    case WM_PAINT: {
      PAINTSTRUCT paint{};
      HDC dc = BeginPaint(window, &paint);
      HBRUSH brush = CreateSolidBrush(Read(&shared->color) == 0 ? RGB(0, 0, 0) : RGB(255, 255, 255));
      RECT area{}; GetClientRect(window, &area); FillRect(dc, &area, brush);
      DeleteObject(brush); EndPaint(window, &paint); return 0;
    }
    case WM_LBUTTONDOWN:
      if (extra == shared->input_marker) InterlockedIncrement(&shared->mouse_down);
      SetFocus(window); return 0;
    case WM_LBUTTONUP:
      if (extra == shared->input_marker) InterlockedIncrement(&shared->mouse_up);
      return 0;
    case WM_KEYDOWN:
      if (wparam == kFocusTestVirtualKey && extra == shared->input_marker) InterlockedIncrement(&shared->key_down);
      return 0;
    case kTargetCommand: {
      if (static_cast<uint64_t>(wparam) != shared->nonce) return 0;
      LONG sequence = Read(&shared->command_sequence);
      if (sequence != Read(&shared->acknowledged_sequence) + 1) return 0;
      LONG command = Read(&shared->command);
      if (command == 1) { InvalidateRect(window, nullptr, FALSE); UpdateWindow(window); DwmFlush(); }
      else if (command == 2) {
        SetWindowPos(window, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        SetForegroundWindow(window); SetFocus(window);
      } else if (command == 3) { PostMessageW(window, WM_CLOSE, 0, 0); }
      else if (command == 4) {
        InterlockedExchange(&shared->authorization_result, AllowSetForegroundWindow(shared->parent_pid));
      }
      InterlockedExchange(&shared->acknowledged_sequence, sequence); return 0;
    }
    case WM_TIMER:
      if (WaitForSingleObject(context->parent, 0) != WAIT_TIMEOUT) DestroyWindow(window);
      return 0;
    case WM_CLOSE: DestroyWindow(window); return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
  }
  return DefWindowProcW(window, message, wparam, lparam);
}
std::string JsonQuoted(const std::string& text) {
  std::ostringstream out; out << '"';
  for (unsigned char ch : text) {
    if (ch == '"' || ch == '\\') out << '\\' << ch;
    else if (ch < 32) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(ch) << std::dec;
    else out << ch;
  }
  out << '"'; return out.str();
}
}  // namespace

void ObserveWindowProbeQueuedMessage(const MSG& message) {
  if (!queued_keyboard || (message.hwnd != queued_primary && message.hwnd != queued_secondary)) return;
  DWORD pid = 0;
  DWORD thread = GetWindowThreadProcessId(message.hwnd, &pid);
  if (pid != GetCurrentProcessId() || thread != GetCurrentThreadId()) return;
  ObserveKeyboard(*queued_keyboard, message.hwnd, message.message, message.wParam,
                  message.lParam, static_cast<uint64_t>(GetMessageExtraInfo()), true);
}

bool ValidateWindowProbeInvocation(const std::vector<std::string>& arguments) {
  try { ParseInvocation(arguments); return true; }
  catch (const std::exception& error) { OutputDebugStringA(error.what()); return false; }
}

int RunWindowProbeTarget(const std::vector<std::string>& arguments) {
  HANDLE mapping = nullptr, ready = nullptr, parent = nullptr;
  Shared* shared = nullptr;
  int exit_code = 1;
  try {
    Require(arguments.size() == 9 && arguments[0] == "--target" && arguments[1] == "--parent-pid" &&
                arguments[3] == "--map" && arguments[5] == "--ready" && arguments[7] == "--nonce",
            "Invalid fixed target arguments.");
    uint64_t parent_id = ParseUnsigned(arguments[2]);
    Require(parent_id <= MAXDWORD, "Parent PID out of range.");
    mapping = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(ParseUnsigned(arguments[4])));
    ready = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(ParseUnsigned(arguments[6])));
    uint64_t nonce = ParseUnsigned(arguments[8], 16);
    shared = static_cast<Shared*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Shared)));
    Require(shared && shared->magic == kMagic && shared->nonce == nonce && shared->parent_pid == parent_id,
            "Inherited mapping identity mismatch.");
    Require(shared->input_marker > 0 && shared->input_marker <= 0x7fffffffU,
            "Inherited input marker is outside the nonzero 31-bit range.");
    Require(shared->width >= 400 && shared->width <= 8192 && shared->height >= 300 && shared->height <= 8192,
            "Target dimensions outside fixed fixture limits.");
    parent = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, static_cast<DWORD>(parent_id));
    Require(parent && WaitForSingleObject(parent, 0) == WAIT_TIMEOUT && SamePath(ProcessPath(parent), ExecutablePath()),
            "Target parent identity is unavailable.");
    WNDCLASSW klass{}; klass.lpfnWndProc = TargetProc; klass.hInstance = GetModuleHandleW(nullptr);
    klass.lpszClassName = kTargetClass; klass.hCursor = LoadCursor(nullptr, IDC_ARROW);
    Require(RegisterClassW(&klass) != 0, "Cannot register fixture window.");
    TargetContext context{shared, parent, ready};
    HWND window = CreateWindowExW(0, kTargetClass, L"mystia controlled input target", WS_POPUP,
        shared->x, shared->y, shared->width, shared->height, nullptr, nullptr, klass.hInstance, &context);
    Require(window != nullptr, "Cannot create fixture window.");
    queued_keyboard = &shared->keyboard; queued_primary = window;
    shared->target_pid = GetCurrentProcessId();
    InterlockedExchange(&shared->mouse_in_pointer_enabled, IsMouseInPointerEnabled());
    InterlockedExchange64(&shared->hwnd, static_cast<LONG64>(reinterpret_cast<intptr_t>(window)));
    SetTimer(window, kTimer, 100, nullptr);
    ShowWindow(window, SW_SHOWNOACTIVATE); UpdateWindow(window);
    Require(SetEvent(ready) != FALSE, "Cannot signal fixture readiness.");
    MSG message{}; BOOL got;
    while ((got = GetMessageW(&message, nullptr, 0, 0)) > 0) {
      ObserveWindowProbeQueuedMessage(message); TranslateMessage(&message); DispatchMessageW(&message);
    }
    exit_code = got == 0 ? static_cast<int>(message.wParam) : 1;
    if (IsWindow(window)) DestroyWindow(window);
  } catch (const std::exception& error) { OutputDebugStringA(error.what()); }
  queued_keyboard = nullptr; queued_primary = nullptr; queued_secondary = nullptr;
  if (shared) UnmapViewOfFile(shared);
  if (parent) CloseHandle(parent);
  if (mapping) CloseHandle(mapping);
  if (ready) CloseHandle(ready);
  return exit_code;
}

struct WindowBridge::Impl {
  HWND top;
  HWND child;
  flutter::FlutterViewController* controller;
  Invocation invocation;
  HANDLE mapping = nullptr, ready = nullptr, process = nullptr;
  Shared* shared = nullptr;
  DWORD target_pid = 0;
  HWND target = nullptr;
  mutable bool target_identity_verified_while_alive = false;
  uint64_t nonce = 0;
  uint32_t input_marker = 0;
  bool initialized = false, hotkey_registered = false, finishing = false;
  bool invocation_valid = false;
  bool core_retirement_started = false, core_retired = false;
  ProbeInputMode mode = ProbeInputMode::kInteractive;
  int64_t revision = 0, armed_revision = -1, frame_revision = 0;
  LONG flutter_down = 0, flutter_up = 0, flutter_key = 0, hotkeys = 0;
  RawInputObservation flutter_raw_input{};
  KeyboardObservation flutter_keyboard{};
  LONG_PTR original_exstyle = 0;
  HRESULT dwm_extend = E_PENDING, dwm_flush = E_PENDING;
  UINT last_send_requested = 0, last_send_inserted = 0;
  DWORD last_send_error = 0;
  BOOL last_foreground_result = FALSE, last_authorize_result = FALSE;
  POINT original_cursor{}, last_injected_cursor{};
  bool original_cursor_valid = false, cursor_injected = false, cursor_restored = false;
  LONG command_sequence = 0;
  std::shared_ptr<bool> alive = std::make_shared<bool>(true);
  Reply pending;
  std::function<bool()> condition;
  std::function<void()> completion;
  ULONGLONG deadline = 0;
  bool frame_done = false;
  std::string init_error;

  Impl(HWND window, flutter::FlutterViewController* view, const std::vector<std::string>& args)
      : top(window), child(view->view()->GetNativeWindow()), controller(view) {
    try {
      invocation = ParseInvocation(args);
      invocation_valid = true;
      queued_keyboard = &flutter_keyboard; queued_primary = child; queued_secondary = top;
      original_exstyle = GetWindowLongPtrW(top, GWL_EXSTYLE);
      Require(SetWindowSubclass(child, ChildProc, kTimer, reinterpret_cast<DWORD_PTR>(this)) != FALSE,
              "Cannot observe the precise Flutter child HWND.");
      Require(SetTimer(top, kTimer, 15, nullptr) != 0, "Cannot create bounded asynchronous probe timer.");
    } catch (const std::exception& error) { init_error = error.what(); }
  }
  ~Impl() {
    DisarmKeyboard(); queued_keyboard = nullptr; queued_primary = nullptr; queued_secondary = nullptr;
    *alive = false;
    if (hotkey_registered) UnregisterHotKey(top, kHotkey);
    KillTimer(top, kTimer);
    if (IsWindow(child)) RemoveWindowSubclass(child, ChildProc, kTimer);
    if (pending) { auto callback = std::move(pending); callback(FlutterError("shutdown", "Window probe is closing.")); }
    if (target && process && WaitForSingleObject(process, 0) == WAIT_TIMEOUT && WindowPid(target) == target_pid) {
      PostMessageW(target, WM_CLOSE, 0, 0);
      WaitForSingleObject(process, 2000);
    }
    if (shared) UnmapViewOfFile(shared);
    if (mapping) CloseHandle(mapping);
    if (ready) CloseHandle(ready);
    if (process) CloseHandle(process);
  }
  static LRESULT CALLBACK ChildProc(HWND window, UINT message, WPARAM wparam,
                                    LPARAM lparam, UINT_PTR id, DWORD_PTR data) {
    auto* self = reinterpret_cast<Impl*>(data);
    if (message == WM_NCDESTROY) { RemoveWindowSubclass(window, ChildProc, id); return DefSubclassProc(window, message, wparam, lparam); }
    const uint64_t extra = static_cast<uint64_t>(GetMessageExtraInfo());
    ObserveRawInput(self->flutter_raw_input, message, wparam, extra);
    ObserveKeyboard(self->flutter_keyboard, window, message, wparam, lparam, extra, false);
    if (extra == self->input_marker && self->input_marker != 0) {
      if (message == WM_LBUTTONDOWN) ++self->flutter_down;
      if (message == WM_LBUTTONUP) ++self->flutter_up;
      if (message == WM_KEYDOWN && wparam == kFocusTestVirtualKey) ++self->flutter_key;
    }
    if (self->mode == ProbeInputMode::kPassThrough) {
      if (message == WM_NCHITTEST) return HTTRANSPARENT;
      if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    }
    return DefSubclassProc(window, message, wparam, lparam);
  }
  void CheckPair(bool require_alive = true) const {
    Require(IsWindow(top) && IsWindow(child) && WindowPid(top) == GetCurrentProcessId() &&
                WindowPid(child) == GetCurrentProcessId() && GetParent(child) == top,
            "Flutter window ownership changed.");
    Require(process && target_pid != 0 && GetProcessId(process) == target_pid,
            "Retained fixture process identity changed.");
    const DWORD state = WaitForSingleObject(process, 0);
    Require(state == WAIT_TIMEOUT || state == WAIT_OBJECT_0,
            "Cannot determine the retained fixture process identity state.");
    if (state == WAIT_OBJECT_0) {
      // The same handle is retained from CreateProcess until destruction. The
      // exited process may no longer expose its image path; only a prior full
      // live identity check authorizes retirement of this exact handle/PID.
      Require(!require_alive && target_identity_verified_while_alive,
              "The exited fixture lacks a verified live process identity.");
      return;
    }
    Require(SamePath(ProcessPath(process), ExecutablePath()),
            "Retained fixture executable identity changed.");
    Require(WaitForSingleObject(process, 0) == WAIT_TIMEOUT && IsWindow(target) &&
                WindowPid(target) == target_pid, "The exact fixture process/window exited.");
    target_identity_verified_while_alive = true;
  }
  bool IsPairWindow(HWND window) const {
    HWND root = GetAncestor(window, GA_ROOT);
    return (root == top && WindowPid(window) == GetCurrentProcessId()) ||
           (root == target && WindowPid(window) == target_pid);
  }
  POINT PointAt(double x, double y) const {
    double ratio = static_cast<double>(GetDpiForWindow(child)) / 96.0;
    POINT point{static_cast<LONG>(std::lround(x * ratio)), static_cast<LONG>(std::lround(y * ratio))};
    RECT area{}; GetClientRect(child, &area);
    Require(point.x >= 0 && point.y >= 0 && point.x < area.right && point.y < area.bottom,
            "A fixed probe sample lies outside the current Flutter client.");
    Require(ClientToScreen(child, &point) != FALSE, "Cannot map fixed sample into physical screen coordinates.");
    return point;
  }
  void DesktopGuard() const {
    DWORD session = MAXDWORD;
    Require(ProcessIdToSessionId(GetCurrentProcessId(), &session) != FALSE &&
                session == WTSGetActiveConsoleSessionId(), "The probe is no longer on the active physical console.");
    HDESK desktop = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
    Require(desktop != nullptr, "Cannot inspect the current physical input desktop.");
    wchar_t name[256]{}; DWORD needed = 0;
    BOOL read = GetUserObjectInformationW(desktop, UOI_NAME, name, sizeof(name), &needed);
    CloseDesktop(desktop);
    Require(read && wcscmp(name, L"Default") == 0, "The physical input desktop changed or locked.");
    wchar_t station[256]{};
    Require(GetUserObjectInformationW(GetProcessWindowStation(), UOI_NAME, station, sizeof(station), &needed) &&
                wcscmp(station, L"WinSta0") == 0, "The probe is not on the interactive window station.");
  }
  void IdleMouse() const {
    for (int key : {VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2}) {
      Require((GetAsyncKeyState(key) & 0x8000) == 0, "A physical key/button is still held; input refused.");
    }
    for (HWND window : {top, target}) {
      GUITHREADINFO info{}; info.cbSize = sizeof(info);
      Require(GetGUIThreadInfo(GetWindowThreadProcessId(window, nullptr), &info) != FALSE,
              "Cannot inspect probe input queue.");
      Require(info.hwndCapture == nullptr && !(info.flags & (GUI_INMENUMODE | GUI_INMOVESIZE)),
              "Probe input capture/menu/drag is active.");
    }
  }
  void IdleInput() const {
    IdleMouse();
    for (int key : {VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN, kFocusTestVirtualKey, VK_F10}) {
      Require((GetAsyncKeyState(key) & 0x8000) == 0, "A physical key is still held; input refused.");
    }
  }
  void InputGuard() const {
    DesktopGuard(); CheckPair(); IdleInput();
    Require(IsPairWindow(GetForegroundWindow()), "An unrelated window owns foreground; input refused.");
  }
  void ArmKeyboard() {
    const auto until = static_cast<LONG64>(GetTickCount64() + 5000);
    InterlockedExchange64(&flutter_keyboard.deadline, until);
    InterlockedExchange64(&shared->keyboard.deadline, until);
    InterlockedExchange(&flutter_keyboard.armed, 1);
    InterlockedExchange(&shared->keyboard.armed, 1);
  }
  void DisarmKeyboard() {
    InterlockedExchange(&flutter_keyboard.armed, 0);
    if (shared) InterlockedExchange(&shared->keyboard.armed, 0);
  }
  void StartTarget() {
    Require(!process, "Fixture initialization cannot be replayed.");
    Require(BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&nonce), sizeof(nonce), BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0 && nonce != 0,
            "Cannot generate inherited control identity nonce.");
    // Keep the high bit clear for pointer/integer JSON and WPARAM conversions.
    nonce &= 0x7fffffffffffffffULL; Require(nonce != 0, "Invalid generated nonce.");
    Require(BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&input_marker), sizeof(input_marker), BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0,
            "Cannot independently generate the input marker.");
    // The observed Windows input path preserves only 32 bits of extraInfo.
    // Keep the sign bit clear, also excluding Flutter's 0xff5157xx touch/pen
    // signature, and still compare the complete received value without masks.
    input_marker &= 0x7fffffffU;
    Require(input_marker != 0, "Invalid generated input marker.");
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
    mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &security, PAGE_READWRITE, 0, sizeof(Shared), nullptr);
    ready = CreateEventW(&security, TRUE, FALSE, nullptr);
    Require(mapping && ready, "Cannot create isolated inherited fixture handles.");
    shared = static_cast<Shared*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Shared)));
    Require(shared != nullptr, "Cannot map isolated fixture state.");
    ZeroMemory(shared, sizeof(Shared)); shared->magic = kMagic; shared->nonce = nonce; shared->parent_pid = GetCurrentProcessId();
    shared->input_marker = input_marker;
    RECT area{}; GetClientRect(child, &area); POINT origin{0, 0}; ClientToScreen(child, &origin);
    shared->x = origin.x; shared->y = origin.y; shared->width = area.right; shared->height = area.bottom;
    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
    std::vector<unsigned char> storage(bytes);
    auto* attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    Require(InitializeProcThreadAttributeList(attributes, 1, 0, &bytes) != FALSE, "Cannot initialize explicit inherited handle list.");
    HANDLE handles[] = {mapping, ready};
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, sizeof(handles), nullptr, nullptr)) {
      DeleteProcThreadAttributeList(attributes); throw std::runtime_error("Cannot restrict inherited target handles.");
    }
    std::wostringstream command;
    command << L'"' << ExecutablePath() << L"\" --target --parent-pid " << GetCurrentProcessId()
            << L" --map " << reinterpret_cast<uintptr_t>(mapping) << L" --ready " << reinterpret_cast<uintptr_t>(ready)
            << L" --nonce " << std::hex << nonce;
    auto line = command.str(); STARTUPINFOEXW startup{}; startup.StartupInfo.cb = sizeof(startup); startup.lpAttributeList = attributes;
    PROCESS_INFORMATION info{};
    BOOL created = CreateProcessW(ExecutablePath().c_str(), line.data(), nullptr, nullptr, TRUE,
        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, nullptr, nullptr, &startup.StartupInfo, &info);
    DeleteProcThreadAttributeList(attributes);
    Require(created != FALSE, "Cannot start the exact fixture executable.");
    process = info.hProcess; target_pid = info.dwProcessId; CloseHandle(info.hThread);
  }
  void CommandTarget(LONG command) {
    CheckPair();
    Require(Read(&shared->acknowledged_sequence) == command_sequence, "Previous fixture command has not completed.");
    InterlockedExchange(&shared->command, command);
    InterlockedExchange(&shared->command_sequence, ++command_sequence);
    Require(PostMessageW(target, kTargetCommand, static_cast<WPARAM>(nonce), 0) != FALSE,
            "Cannot deliver fixed fixture control operation.");
  }
  bool Acknowledged() const { return Read(&shared->acknowledged_sequence) == command_sequence; }
  void ApplyInputMode(ProbeInputMode next) {
    Require(next == ProbeInputMode::kInteractive || next == ProbeInputMode::kPassThrough, "Invalid typed input mode.");
    if (initialized) IdleMouse();
    LONG_PTR style = GetWindowLongPtrW(top, GWL_EXSTYLE);
    style &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    // Keep alpha at 255: opacity belongs exclusively to Flutter's paint layers.
    style |= WS_EX_LAYERED;
    if (next == ProbeInputMode::kPassThrough) style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    SetLastError(0);
    LONG_PTR prior = SetWindowLongPtrW(top, GWL_EXSTYLE, style);
    Require(prior != 0 || GetLastError() == 0, "Cannot apply input window styles.");
    Require(SetLayeredWindowAttributes(top, 0, 255, LWA_ALPHA) != FALSE, "Cannot initialize layered alpha 255.");
    Require(SetWindowPos(top, nullptr, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED) != FALSE,
        "Cannot commit input window styles.");
    constexpr LONG_PTR mask = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    Require((GetWindowLongPtrW(top, GWL_EXSTYLE) & mask) == (style & mask),
            "Native input styles did not match the requested mode after commit.");
    mode = next;
  }
  void ShowInteractive() {
    ApplyInputMode(ProbeInputMode::kInteractive);
    ShowWindow(top, SW_SHOWNOACTIVATE);
    SetWindowPos(top, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    last_foreground_result = SetForegroundWindow(top);
    if (GetForegroundWindow() == top) SetFocus(child);
  }
  void Send(std::vector<INPUT>& inputs) {
    InputGuard();
    last_send_requested = static_cast<UINT>(inputs.size()); SetLastError(0);
    last_send_inserted = SendInput(last_send_requested, inputs.data(), sizeof(INPUT));
    last_send_error = GetLastError();
    Require(last_send_inserted == last_send_requested, "SendInput did not insert every requested event; operation will not be replayed.");
  }
  void SendKey(WORD key) {
    std::vector<INPUT> inputs(2);
    for (auto& input : inputs) { input.type = INPUT_KEYBOARD; input.ki.wVk = key; input.ki.dwExtraInfo = static_cast<ULONG_PTR>(input_marker); }
    inputs[1].ki.dwFlags = KEYEVENTF_KEYUP; Send(inputs);
  }
  void SendClick() {
    InputGuard(); POINT point = PointAt(160, 96);
    Require(IsPairWindow(WindowFromPoint(point)), "Fixed click point is covered by an unrelated window.");
    int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
    int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    Require(width > 1 && height > 1 && point.x >= x && point.y >= y && point.x < x + width && point.y < y + height,
            "Fixed click point lies outside the virtual desktop.");
    std::vector<INPUT> inputs(3);
    for (auto& input : inputs) { input.type = INPUT_MOUSE; input.mi.dwExtraInfo = static_cast<ULONG_PTR>(input_marker); }
    inputs[0].mi.dx = static_cast<LONG>(std::llround(static_cast<double>(point.x - x) * 65535.0 / (width - 1)));
    inputs[0].mi.dy = static_cast<LONG>(std::llround(static_cast<double>(point.y - y) * 65535.0 / (height - 1)));
    inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
    inputs[1].mi.dwFlags = MOUSEEVENTF_LEFTDOWN; inputs[2].mi.dwFlags = MOUSEEVENTF_LEFTUP;
    if (!original_cursor_valid) original_cursor_valid = GetCursorPos(&original_cursor) != FALSE;
    last_injected_cursor = point;
    // Even a partial insertion can have moved the cursor; cleanup checks its
    // exact current position before attempting the conservative restoration.
    cursor_injected = true;
    Send(inputs);
  }
  void RestoreCursor() {
    POINT current{};
    if (!original_cursor_valid || !cursor_injected || !GetCursorPos(&current) ||
        current.x != last_injected_cursor.x || current.y != last_injected_cursor.y) return;
    DesktopGuard();
    cursor_restored = SetCursorPos(original_cursor.x, original_cursor.y) != FALSE;
    Require(cursor_restored, "Could not restore the unchanged injected cursor position.");
  }
  bool AboveTarget() const {
    for (HWND window = GetTopWindow(nullptr); window; window = GetWindow(window, GW_HWNDNEXT)) {
      if (window == top) return true;
      if (window == target) return false;
    }
    return false;
  }
  std::string InputDiagnostics() {
    POINT cursor{};
    BOOL cursor_available = GetCursorPos(&cursor);
    HWND cursor_hit = cursor_available ? WindowFromPoint(cursor) : nullptr;
    std::ostringstream out;
    out << "{\"lastSendRequested\":" << last_send_requested
        << ",\"lastSendInserted\":" << last_send_inserted << ",\"lastSendError\":" << last_send_error
        << ",\"focusTestVirtualKey\":" << kFocusTestVirtualKey << ",\"focusTestKeyName\":\"" << kFocusTestKeyName << "\""
        << ",\"controlNonceHex\":\"" << std::hex << nonce
        << "\",\"inputMarkerHex\":\"" << input_marker << "\"" << std::dec
        << ",\"cursorAvailable\":" << (cursor_available ? "true" : "false") << ",\"cursorPosition\":";
    if (cursor_available) out << '[' << cursor.x << ',' << cursor.y << ']';
    else out << "null";
    out << ",\"cursorHitHwnd\":" << HandleValue(cursor_hit)
        << ",\"cursorHitProcessId\":" << WindowPid(cursor_hit)
        << ",\"mouseInPointerEnabled\":" << (IsMouseInPointerEnabled() ? "true" : "false")
        << ",\"targetMouseInPointerEnabled\":";
    if (shared && target_pid) out << (Read(&shared->mouse_in_pointer_enabled) ? "true" : "false");
    else out << "null";
    out << ",\"flutterRawInput\":"; AppendRawInput(out, flutter_raw_input);
    out << ",\"targetRawInput\":";
    if (shared) AppendRawInput(out, shared->raw_input);
    else out << "null";
    out << ",\"flutterKeyboard\":"; AppendKeyboard(out, flutter_keyboard);
    out << ",\"targetKeyboard\":";
    if (shared) AppendKeyboard(out, shared->keyboard);
    else out << "null";
    out << '}';
    return out.str();
  }
  ProbeSnapshot Snapshot(const ::flutter::EncodableList& pixels = {}) {
    HWND foreground = GetForegroundWindow();
    GUITHREADINFO thread{}; thread.cbSize = sizeof(thread);
    GetGUIThreadInfo(GetWindowThreadProcessId(foreground, nullptr), &thread);
    UINT dpi = GetDpiForWindow(child);
    POINT point = PointAt(160, 96); HWND hit = WindowFromPoint(point);
    RECT client{}; GetClientRect(child, &client); POINT origin{}; ClientToScreen(child, &origin);
    MONITORINFOEXW monitor{}; monitor.cbSize = sizeof(monitor);
    GetMonitorInfoW(MonitorFromWindow(top, MONITOR_DEFAULTTONEAREST), &monitor);
    std::ostringstream diagnostics;
    diagnostics << "{\"schemaVersion\":1,\"nativeGitSha\":\"" << MYSTIA_WINDOW_PROBE_GIT_SHA
        << "\",\"runId\":" << JsonQuoted(invocation.run)
        << ",\"focusTestVirtualKey\":" << kFocusTestVirtualKey << ",\"focusTestKeyName\":\"" << kFocusTestKeyName << "\""
        << ",\"dwmExtendHresult\":" << static_cast<int64_t>(dwm_extend)
        << ",\"dwmFlushHresult\":" << static_cast<int64_t>(dwm_flush)
        << ",\"topStyle\":" << GetWindowLongPtrW(top, GWL_STYLE)
        << ",\"topExStyle\":" << GetWindowLongPtrW(top, GWL_EXSTYLE)
        << ",\"childStyle\":" << GetWindowLongPtrW(child, GWL_STYLE)
        << ",\"childExStyle\":" << GetWindowLongPtrW(child, GWL_EXSTYLE)
        << ",\"topClassStyle\":" << GetClassLongPtrW(top, GCL_STYLE)
        << ",\"childClassStyle\":" << GetClassLongPtrW(child, GCL_STYLE)
        << ",\"dpiAwareness\":" << GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(top))
        << ",\"monitor\":" << JsonQuoted(Narrow(monitor.szDevice))
        << ",\"clientPhysicalRect\":[" << origin.x << ',' << origin.y << ',' << client.right << ',' << client.bottom << ']'
        << ",\"samplePhysicalPoint\":[" << point.x << ',' << point.y << ']'
        << ",\"lastSendRequested\":" << last_send_requested << ",\"lastSendInserted\":" << last_send_inserted
        << ",\"lastSendError\":" << last_send_error
        << ",\"lastSetForegroundResult\":" << (last_foreground_result ? "true" : "false")
        << ",\"controlledAsfwResult\":" << (last_authorize_result ? "true" : "false")
        << ",\"cursorRestored\":" << (cursor_restored ? "true" : "false")
        << ",\"cursorRestorePolicy\":\"restore only if still at the last injected point; never restore foreign foreground\""
        << ",\"inputDiagnostics\":" << InputDiagnostics()
        << ",\"controlNonceHex\":\"" << std::hex << nonce
        << "\",\"inputMarkerHex\":\"" << input_marker << "\"}";
    return ProbeSnapshot(revision, frame_revision, GetCurrentProcessId(), HandleValue(top), HandleValue(child),
        target_pid, HandleValue(target), IsWindowVisible(top) != FALSE, mode,
        (GetWindowLongPtrW(top, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0,
        HandleValue(foreground), WindowPid(foreground), HandleValue(thread.hwndFocus),
        flutter_down, flutter_up, shared ? Read(&shared->mouse_down) : 0,
        shared ? Read(&shared->mouse_up) : 0, flutter_key, shared ? Read(&shared->key_down) : 0,
        hotkeys, dpi, static_cast<double>(dpi) / 96.0, target && AboveTarget(), HandleValue(hit), WindowPid(hit), pixels, diagnostics.str());
  }
  ::flutter::EncodableValue FailureDetails() noexcept {
    // Construct evidence before transferring the reply. Window observation can
    // itself fail; retain a small diagnostic object without retrying input.
    try {
      std::string snapshot_error;
      try {
        return ::flutter::EncodableValue(::flutter::CustomEncodableValue(
            core_retirement_started ? TerminalSnapshot() : Snapshot()));
      } catch (const std::exception& error) {
        snapshot_error = std::string(error.what()).substr(0, 2048);
      } catch (...) {
        snapshot_error = "Non-standard exception while observing native state.";
      }
      std::ostringstream out;
      out << "{\"schemaVersion\":1,\"kind\":\"native-error-fallback\",\"nativeGitSha\":\""
          << MYSTIA_WINDOW_PROBE_GIT_SHA << "\",\"runId\":" << JsonQuoted(invocation.run)
          << ",\"snapshotError\":" << JsonQuoted(snapshot_error)
          << ",\"inputDiagnostics\":" << InputDiagnostics() << '}';
      const auto details = out.str();
      if (details.size() <= 16384) return ::flutter::EncodableValue(details);
      return ::flutter::EncodableValue("{\"schemaVersion\":1,\"kind\":\"native-error-fallback\",\"snapshotError\":\"Native diagnostic bound exceeded.\"}");
    } catch (...) {
      // Even allocation/diagnostic failure must not strand the pending reply.
      return ::flutter::EncodableValue();
    }
  }
  FlutterError Failure(const std::string& code, const std::string& message) {
    return FlutterError(code, message, FailureDetails());
  }
  ProbeSnapshot TerminalSnapshot() const {
    // Retirement and terminal persistence do not perform a fresh paired-window
    // sample. Zero values denote unobserved fields; retirement has its own
    // explicit retained-process, hotkey and controller-window evidence.
    std::ostringstream diagnostics;
    diagnostics << "{\"schemaVersion\":1,\"nativeGitSha\":\"" << MYSTIA_WINDOW_PROBE_GIT_SHA
        << "\",\"runId\":" << JsonQuoted(invocation.run) << ",\"terminalOnly\":true"
        << ",\"coreRetirementStarted\":" << (core_retirement_started ? "true" : "false")
        << ",\"coreRetired\":" << (core_retired ? "true" : "false")
        << ",\"coreTargetProcessId\":" << target_pid
        << ",\"coreTargetIdentityVerifiedWhileAlive\":" << (target_identity_verified_while_alive ? "true" : "false")
        << ",\"coreTargetExitCode\":" << (core_retired ? "0" : "null")
        << ",\"coreHotkeyReleased\":" << (!hotkey_registered ? "true" : "false")
        << ",\"coreControllerHidden\":" << (!IsWindowVisible(top) ? "true" : "false")
        << ",\"coreControllerTopmost\":" << ((GetWindowLongPtrW(top, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0 ? "true" : "false")
        << ",\"cursorRestored\":" << (cursor_restored ? "true" : "false") << '}';
    return ProbeSnapshot(revision, frame_revision, GetCurrentProcessId(), HandleValue(top), HandleValue(child),
        target_pid, HandleValue(target), false, mode, false, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0.0, false, 0, 0, {}, diagnostics.str());
  }
  void Wait(Reply callback, std::function<bool()> ready_test, std::function<void()> done = {}, ULONGLONG milliseconds = 5000) {
    pending = std::move(callback); condition = std::move(ready_test); completion = std::move(done); deadline = GetTickCount64() + milliseconds;
  }
  void Tick() {
    if (!pending) return;
    try {
      if (condition()) {
        auto done = std::move(completion); if (done) done();
        DisarmKeyboard();
        auto snapshot = (finishing || core_retirement_started) ? TerminalSnapshot() : Snapshot();
        auto callback = std::move(pending); callback(snapshot);
        if (finishing) PostQuitMessage(finish_exit_code);
      } else if (GetTickCount64() >= deadline) {
        throw std::runtime_error("Bounded native observation timed out; no input operation was replayed.");
      }
    } catch (const std::exception& error) {
      DisarmKeyboard();
      auto failure = Failure("native-observation", error.what());
      auto callback = std::move(pending); completion = {}; condition = {};
      if (callback) callback(failure);
    }
  }
  int finish_exit_code = 1;
  void WriteReport(const std::string& report) {
    Require(!report.empty() && report.size() <= 1024 * 1024 && report.front() == '{' && report.back() == '}',
            "Expected a bounded JSON object for the fixed result file.");
    Widen(report); PlainPath(invocation.result, false, true);
    HANDLE file = CreateFileW(invocation.result.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                              FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    Require(file != INVALID_HANDLE_VALUE, "Cannot exclusively create this run's result file.");
    DWORD written = 0;
    BOOL ok = WriteFile(file, report.data(), static_cast<DWORD>(report.size()), &written, nullptr);
    BOOL flushed = FlushFileBuffers(file); CloseHandle(file);
    Require(ok && written == report.size() && flushed, "Cannot completely persist the run result.");
  }
  bool TargetExitObserved() const {
    Require(process != nullptr, "No retained fixture process is available for exit observation.");
    DWORD state = WaitForSingleObject(process, 0);
    Require(state == WAIT_TIMEOUT || state == WAIT_OBJECT_0,
            "Cannot determine the retained fixture process exit state.");
    return state == WAIT_OBJECT_0;
  }
  void CompleteCoreRetirement() {
    CheckPair(false);
    Require(TargetExitObserved(), "The retained core fixture is still running.");
    DWORD exit_code = 1;
    Require(GetExitCodeProcess(process, &exit_code) && exit_code == 0,
            "The retained core fixture did not exit normally.");
    Require(!hotkey_registered, "The core F10 registration is still owned.");
    Require(!IsWindowVisible(top) && (GetWindowLongPtrW(top, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0,
            "The core controller must remain hidden and not topmost.");
    core_retired = true;
  }
  void WriteTerminalReport(const std::string& report) {
    if (process) {
      Require(TargetExitObserved(), "The retained fixture process has not exited.");
      DWORD exit_code = 1;
      Require(GetExitCodeProcess(process, &exit_code) && exit_code == 0,
              "Owned fixture failed to exit normally.");
    } else {
      Require(finish_exit_code == 1, "A run without a retained fixture cannot report success.");
    }
    WriteReport(report);
  }
  void Execute(const ProbeCommand& command, Reply callback) {
    try {
      Require(!pending, "Another native observation is still pending.");
      Require(command.revision() >= 0 && command.revision() <= 1000000, "Frame revision outside probe bounds.");
      revision = command.revision();
      auto operation = command.operation();
      Require(operation >= ProbeOperation::kInitialize && operation <= ProbeOperation::kRetireCore, "Unknown typed operation.");
      Require(invocation_valid, "The fixed result-file invocation was not validated.");
      Require(!core_retirement_started || operation == ProbeOperation::kFinish || operation == ProbeOperation::kInspect,
              "Core retirement cannot be replayed or followed by further core input.");
      if (operation != ProbeOperation::kFinish) Require(init_error.empty(), init_error.c_str());
      Require((operation == ProbeOperation::kInitialize || operation == ProbeOperation::kFinish) || command.text().empty(),
              "Text is only allowed for identity and final report.");
      bool uses_value = operation == ProbeOperation::kSetUnderlayColor || operation == ProbeOperation::kSetTopmost || operation == ProbeOperation::kFinish;
      Require((uses_value && (command.value() == 0 || command.value() == 1)) || (!uses_value && command.value() == 0), "Unexpected operation value.");
      if (operation != ProbeOperation::kFinish && !core_retirement_started) Require(std::isfinite(command.device_pixel_ratio()) && command.device_pixel_ratio() > 0 &&
                  std::abs(command.device_pixel_ratio() - static_cast<double>(GetDpiForWindow(child)) / 96.0) < 0.001,
              "Dart devicePixelRatio does not match the physical Flutter HWND DPI.");
      if (operation == ProbeOperation::kInitialize) {
        Require(!initialized && !process, "Initialization cannot be replayed.");
        Require(command.text() == MYSTIA_WINDOW_PROBE_GIT_SHA, "Dart and native Git SHA differ.");
        MARGINS margins{-1, -1, -1, -1}; dwm_extend = DwmExtendFrameIntoClientArea(top, &margins);
        Require(SUCCEEDED(dwm_extend), "DwmExtendFrameIntoClientArea failed; no fallback was attempted.");
        ApplyInputMode(ProbeInputMode::kInteractive);
        if (!RegisterHotKey(top, kHotkey, MOD_NOREPEAT, VK_F10)) {
          auto failure = Failure("blocked", "F10 registration failed with Win32 error " + std::to_string(GetLastError()));
          callback(failure); return;
        }
        hotkey_registered = true; StartTarget();
        Wait(std::move(callback), [this] {
          Require(WaitForSingleObject(process, 0) == WAIT_TIMEOUT, "Fixture exited before readiness.");
          if (WaitForSingleObject(ready, 0) != WAIT_OBJECT_0) return false;
          target = reinterpret_cast<HWND>(static_cast<uintptr_t>(Read(&shared->hwnd)));
          Require(shared->target_pid == target_pid, "Fixture mapping PID mismatch."); CheckPair(); return true;
        }, [this] { initialized = true; SetWindowPos(top, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); });
        return;
      }
      if (operation == ProbeOperation::kFinish) {
        finishing = true; finish_exit_code = static_cast<int>(command.value());
        RestoreCursor();
        if (hotkey_registered) { Require(UnregisterHotKey(top, kHotkey) != FALSE, "Cannot release F10."); hotkey_registered = false; }
        const std::string report = command.text();
        if (process && !TargetExitObserved()) {
          CommandTarget(3);
          Wait(std::move(callback), [this] { return TargetExitObserved(); },
               [this, report] { WriteTerminalReport(report); });
        } else {
          WriteTerminalReport(report); callback(TerminalSnapshot()); PostQuitMessage(finish_exit_code);
        }
        return;
      }
      if (operation == ProbeOperation::kInspect && core_retirement_started) {
        callback(TerminalSnapshot());
        return;
      }
      if (operation == ProbeOperation::kRetireCore) {
        Require(initialized, "Initialize the exact fixture before retirement.");
        CheckPair(false);
        core_retirement_started = true;
        DisarmKeyboard();
        RestoreCursor();
        // Later lifecycle probes own their cursor restoration; final report
        // persistence must not revive a stale core-stage restore intention.
        cursor_injected = false;
        if (hotkey_registered) {
          Require(UnregisterHotKey(top, kHotkey) != FALSE, "Cannot release core F10.");
          hotkey_registered = false;
        }
        Require(SetWindowPos(top, HWND_NOTOPMOST, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW) != FALSE,
                "Cannot hide and remove the core controller's topmost state.");
        Require(!IsWindowVisible(top) && (GetWindowLongPtrW(top, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0,
                "Core controller retirement styles did not take effect.");
        if (!TargetExitObserved()) {
          CommandTarget(3);
          Wait(std::move(callback), [this] { return TargetExitObserved(); },
               [this] { CompleteCoreRetirement(); });
        } else {
          CompleteCoreRetirement();
          callback(TerminalSnapshot());
        }
        return;
      }
      Require(initialized, "Initialize the exact fixture first."); CheckPair();
      switch (operation) {
        case ProbeOperation::kInspect: callback(Snapshot()); return;
        case ProbeOperation::kArmFrame:
          Require(revision >= 1 && revision > frame_revision, "Frame revision must advance."); armed_revision = revision; callback(Snapshot()); return;
        case ProbeOperation::kPresentFrame: {
          Require(armed_revision == revision, "Presentation must match the armed revision."); frame_done = false;
          std::weak_ptr<bool> weak = alive;
          controller->engine()->SetNextFrameCallback([weak, this] { auto keep = weak.lock(); if (keep && *keep) frame_done = true; });
          controller->ForceRedraw();
          Wait(std::move(callback), [this] { return frame_done; }, [this] {
            dwm_flush = DwmFlush(); Require(SUCCEEDED(dwm_flush), "DwmFlush failed."); frame_revision = revision;
          }); return;
        }
        case ProbeOperation::kSetInputMode: ApplyInputMode(command.input_mode()); callback(Snapshot()); return;
        case ProbeOperation::kSetUnderlayColor:
          InterlockedExchange(&shared->color, static_cast<LONG>(command.value())); CommandTarget(1);
          Wait(std::move(callback), [this] { return Acknowledged(); }); return;
        case ProbeOperation::kFocusUnderlay:
          InputGuard(); last_authorize_result = AllowSetForegroundWindow(target_pid); CommandTarget(2);
          Wait(std::move(callback), [this] { return Acknowledged() && GetForegroundWindow() == target; }); return;
        case ProbeOperation::kShowInteractive:
          InputGuard(); CommandTarget(4);
          Wait(std::move(callback), [this] { return Acknowledged(); }, [this] {
            last_authorize_result = Read(&shared->authorization_result); ShowInteractive();
            Require(GetForegroundWindow() == top && GetFocus() == child, "Interactive restore did not actually regain foreground/focus.");
          }); return;
        case ProbeOperation::kHide: ShowWindow(top, SW_HIDE); callback(Snapshot()); return;
        case ProbeOperation::kCloseToHide:
          PostMessageW(top, WM_CLOSE, 0, 0); Wait(std::move(callback), [this] { return !IsWindowVisible(top); }); return;
        case ProbeOperation::kSetTopmost:
          Require(SetWindowPos(top, command.value() ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
              SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) != FALSE, "Cannot change probe z-order."); callback(Snapshot()); return;
        case ProbeOperation::kClickSample: {
          LONG before = flutter_up + Read(&shared->mouse_up); SendClick();
          Wait(std::move(callback), [this, before] { return flutter_up + Read(&shared->mouse_up) > before; }); return;
        }
        case ProbeOperation::kSendKey: {
          InputGuard(); ArmKeyboard();
          LONG before = flutter_key + Read(&shared->key_down); SendKey(static_cast<WORD>(kFocusTestVirtualKey));
          Wait(std::move(callback), [this, before] { return flutter_key + Read(&shared->key_down) > before; }); return;
        }
        case ProbeOperation::kSendF10: {
          InputGuard(); CommandTarget(4); LONG before = hotkeys;
          Wait(std::move(callback), [this, before, sent = false]() mutable {
            if (!sent) {
              if (!Acknowledged()) return false;
              last_authorize_result = Read(&shared->authorization_result);
              SendKey(VK_F10); sent = true;
            }
            return hotkeys > before && IsWindowVisible(top) && mode == ProbeInputMode::kInteractive && GetForegroundWindow() == top;
          }); return;
        }
        case ProbeOperation::kCapture: {
          Require(frame_revision == revision && IsWindowVisible(top), "Capture requires the current presented visible frame.");
          DesktopGuard();
          Require(IsPairWindow(GetForegroundWindow()), "Owned foreground is not ready for sampling.");
          dwm_flush = DwmFlush(); Require(SUCCEEDED(dwm_flush), "Cannot flush the probe rendering.");
          ::flutter::EncodableList pixels; HDC screen = GetDC(nullptr); Require(screen != nullptr, "Cannot obtain composed screen DC.");
          bool valid = true;
          for (const auto& logical : {std::pair<double, double>{48, 48}, {160, 96}, {32, 272}}) {
            POINT point = PointAt(logical.first, logical.second);
            if (!IsPairWindow(WindowFromPoint(point))) { valid = false; break; }
            COLORREF color = GetPixel(screen, point.x, point.y);
            if (color == CLR_INVALID) { valid = false; break; }
            pixels.emplace_back(static_cast<int64_t>(GetRValue(color)));
            pixels.emplace_back(static_cast<int64_t>(GetGValue(color)));
            pixels.emplace_back(static_cast<int64_t>(GetBValue(color)));
          }
          ReleaseDC(nullptr, screen); Require(valid, "Controlled screen sample failed or was occluded by an unrelated window.");
          callback(Snapshot(pixels)); return;
        }
        default: throw std::runtime_error("Unsupported operation state.");
      }
    } catch (const std::exception& error) {
      DisarmKeyboard();
      auto failure = Failure("native-operation", error.what());
      callback(failure);
    }
  }
};

WindowBridge::WindowBridge(HWND window, flutter::FlutterViewController* controller,
                           const std::vector<std::string>& arguments)
    : impl_(std::make_unique<Impl>(window, controller, arguments)) {}
WindowBridge::~WindowBridge() = default;
void WindowBridge::Execute(const ProbeCommand& command, Reply result) { impl_->Execute(command, std::move(result)); }
std::optional<LRESULT> WindowBridge::HandleWindowMessage(UINT message, WPARAM wparam, LPARAM lparam) {
  ObserveKeyboard(impl_->flutter_keyboard, impl_->top, message, wparam, lparam,
                  static_cast<uint64_t>(GetMessageExtraInfo()), false);
  if (message == WM_TIMER && wparam == kTimer) { impl_->Tick(); return 0; }
  if (message == WM_CLOSE) { ShowWindow(impl_->top, SW_HIDE); return 0; }
  if (message == WM_HOTKEY && wparam == kHotkey) {
    if (impl_->core_retirement_started || !impl_->hotkey_registered) return 0;
    try { ++impl_->hotkeys; impl_->ShowInteractive(); } catch (const std::exception& error) { impl_->init_error = error.what(); }
    return 0;
  }
  if (message == WM_MOUSEACTIVATE && impl_->mode == ProbeInputMode::kPassThrough) return MA_NOACTIVATE;
  (void)lparam;
  return std::nullopt;
}
