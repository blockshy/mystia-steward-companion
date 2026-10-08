#include "shell_tray_driver.h"

#include <shellapi.h>
#include <UIAutomation.h>
#include <wrl/client.h>
#include <algorithm>
#include <iomanip>
#include <sstream>
#include <utility>

namespace lifecycle_probe {
namespace {
using Microsoft::WRL::ComPtr;
void Require(bool condition, const char* message) {
  if (!condition) throw std::runtime_error(message);
}
void Available(bool condition, const char* message) {
  if (!condition) throw TrayBlocked(message);
}
DWORD Owner(HWND window) { DWORD pid = 0; GetWindowThreadProcessId(window, &pid); return pid; }
HWND Window(uint64_t value) { return reinterpret_cast<HWND>(static_cast<uintptr_t>(value)); }
uint64_t FileTime(FILETIME value) {
  return (static_cast<uint64_t>(value.dwHighDateTime) << 32) | value.dwLowDateTime;
}
std::wstring Property(IUIAutomationElement* element, PROPERTYID property) {
  VARIANT value; VariantInit(&value);
  HRESULT result = element->GetCurrentPropertyValue(property, &value);
  std::wstring text;
  if (SUCCEEDED(result) && value.vt == VT_BSTR && value.bstrVal) text = value.bstrVal;
  VariantClear(&value);
  Available(SUCCEEDED(result), "Cannot read the exact Shell accessibility identity.");
  Available(text.size() <= 512, "Shell accessibility text is unexpectedly large.");
  return text;
}
std::string JsonWide(const std::wstring& value) {
  std::ostringstream out; out << '"';
  for (wchar_t ch : value) {
    if (ch >= 0x20 && ch <= 0x7e && ch != L'"' && ch != L'\\') out << static_cast<char>(ch);
    else out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<unsigned int>(ch);
  }
  out << '"'; return out.str();
}
bool SameRect(const RECT& left, const RECT& right) {
  return left.left == right.left && left.top == right.top && left.right == right.right && left.bottom == right.bottom;
}
void DesktopGuard() {
  DWORD session = 0;
  Available(ProcessIdToSessionId(GetCurrentProcessId(), &session) && session == WTSGetActiveConsoleSessionId(),
            "The fixture is not in the active physical console session.");
  HDESK desktop = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
  Available(desktop != nullptr, "The physical input desktop is unavailable.");
  wchar_t name[128]{}; DWORD needed = 0;
  bool is_default = GetUserObjectInformationW(desktop, UOI_NAME, name, sizeof(name), &needed) &&
                    wcscmp(name, L"Default") == 0;
  CloseDesktop(desktop);
  Available(is_default, "The physical input desktop is not Default.");
  wchar_t station_name[128]{};
  Available(GetUserObjectInformationW(GetProcessWindowStation(), UOI_NAME, station_name,
            sizeof(station_name), &needed) && _wcsicmp(station_name, L"WinSta0") == 0,
            "The fixture is not on WinSta0.");
}
void IdleKeys() {
  for (int key = 1; key < 256; ++key) {
    Available((GetAsyncKeyState(key) & 0x8000) == 0, "Physical keys or buttons are held; no Shell input was injected.");
  }
}
}

struct ShellTrayDriver::Impl {
  struct InputTrace {
    const char* kind = "none";
    LONG generation = 0;
  };
  Shared& shared;
  HANDLE shell_process = nullptr;
  DWORD shell_pid = 0;
  uint64_t shell_creation = 0;
  ComPtr<IUIAutomation> automation;
  HWND last_icon_host = nullptr;
  HRESULT last_rect_result = E_PENDING;
  POINT original_cursor{}, last_cursor{};
  bool cursor_saved = false, cursor_injected = false, cursor_restored = false;
  std::string overflow_candidates = "[]";
  InputTrace last_input;
  UINT input_attempts = 0;
  RECT last_guid_rect{}, last_element_rect{}, overflow_button_rect{};
  HWND last_hit = nullptr, observed_host = nullptr;
  std::wstring observed_host_class;
  int tooltip_matches = -1, overflow_button_matches = -1;
  const char* observation_stage = "none";
  const char* overflow_state = "not-requested";
  bool pending_click = false, pending_right = false;
  bool overflow_expansion_attempted = false, overflow_icon_ready = false;
  LONG pending_generation = 0;
  uint64_t pending_nonce = 0;
  GUID pending_guid{};
  ULONGLONG pending_deadline = 0;
  int point_pid = 0, point_type = 0, point_ancestor_depth = -1;
  std::wstring point_automation_id;

  explicit Impl(Shared& state) : shared(state) {}
  ~Impl() {
    // Never move the cursor after the user moved it away from our last input.
    if (cursor_saved && cursor_injected) {
      try {
        DesktopGuard(); POINT current{};
        if (GetCursorPos(&current) && current.x == last_cursor.x && current.y == last_cursor.y)
          cursor_restored = SetCursorPos(original_cursor.x, original_cursor.y) != FALSE;
      } catch (...) { /* A locked/changed desktop prohibits restoration. */ }
    }
    if (shell_process) CloseHandle(shell_process);
  }
  void ShellIdentity() {
    DesktopGuard();
    HWND shell = GetShellWindow();
    DWORD pid = Owner(shell), session = 0, own_session = 0;
    Available(shell && pid && ProcessIdToSessionId(pid, &session) &&
                  ProcessIdToSessionId(GetCurrentProcessId(), &own_session) && session == own_session,
              "The interactive Shell identity/session is unavailable.");
    if (!shell_process) {
      shell_process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
      Available(shell_process != nullptr, "Cannot retain the interactive Shell process.");
      std::wstring path(32768, L'\0'); DWORD size = static_cast<DWORD>(path.size());
      Available(QueryFullProcessImageNameW(shell_process, 0, path.data(), &size), "Cannot inspect the Shell image path.");
      path.resize(size);
      wchar_t windows[MAX_PATH]{};
      UINT count = GetWindowsDirectoryW(windows, MAX_PATH);
      Available(count > 0 && count < MAX_PATH && _wcsicmp(path.c_str(), (std::wstring(windows) + L"\\explorer.exe").c_str()) == 0,
                "The interactive Shell is not the exact Windows Explorer image.");
      FILETIME created{}, exited{}, kernel{}, user{};
      Available(GetProcessTimes(shell_process, &created, &exited, &kernel, &user), "Cannot bind Shell creation identity.");
      shell_pid = pid; shell_creation = FileTime(created);
      Available(SUCCEEDED(CoCreateInstance(__uuidof(CUIAutomation8), nullptr, CLSCTX_INPROC_SERVER,
                                          IID_PPV_ARGS(automation.GetAddressOf()))), "UI Automation is unavailable.");
      ComPtr<IUIAutomation2> bounded;
      Available(SUCCEEDED(automation.As(&bounded)) && SUCCEEDED(bounded->put_ConnectionTimeout(2000)) &&
                    SUCCEEDED(bounded->put_TransactionTimeout(2000)), "Cannot bound Shell accessibility calls.");
    }
    Available(pid == shell_pid && WaitForSingleObject(shell_process, 0) == WAIT_TIMEOUT,
              "The bound Shell process changed or exited; tray evidence is blocked.");
    FILETIME created{}, exited{}, kernel{}, user{};
    Available(GetProcessTimes(shell_process, &created, &exited, &kernel, &user) && FileTime(created) == shell_creation,
              "The bound Shell creation identity changed.");
    Publish(&shared.tray_shell_pid, static_cast<LONG>(shell_pid));
    Publish(&shared.tray_shell_creation, shell_creation);
  }
  ComPtr<IUIAutomationElement> Element(HWND window) {
    ComPtr<IUIAutomationElement> element;
    Available(IsWindow(window) && Owner(window) == shell_pid &&
                  SUCCEEDED(automation->ElementFromHandle(window, &element)), "Cannot inspect the exact Shell host.");
    return element;
  }
  ComPtr<IUIAutomationCondition> StringCondition(PROPERTYID property, const wchar_t* expected) {
    VARIANT value; VariantInit(&value); value.vt = VT_BSTR;
    value.bstrVal = SysAllocString(expected);
    Available(value.bstrVal != nullptr, "Cannot prepare an exact Shell condition.");
    ComPtr<IUIAutomationCondition> condition;
    HRESULT result = automation->CreatePropertyCondition(property, value, &condition);
    VariantClear(&value);
    Available(SUCCEEDED(result), "Cannot bind an exact Shell condition.");
    return condition;
  }
  ComPtr<IUIAutomationElementArray> Named(IUIAutomationElement* root) {
    auto condition = StringCondition(UIA_NamePropertyId, shared.tray_tooltip);
    ComPtr<IUIAutomationElementArray> matches;
    Available(SUCCEEDED(root->FindAll(TreeScope_Subtree, condition.Get(), &matches)), "Cannot enumerate this exact fixture tooltip.");
    return matches;
  }
  bool PointBelongs(IUIAutomationElement* expected, POINT center) {
    point_pid = 0; point_type = 0; point_ancestor_depth = -1; point_automation_id.clear();
    ComPtr<IUIAutomationElement> current;
    Available(SUCCEEDED(automation->ElementFromPoint(center, &current)), "Cannot inspect the physical Shell point.");
    Available(SUCCEEDED(current->get_CurrentProcessId(&point_pid)) &&
                  SUCCEEDED(current->get_CurrentControlType(&point_type)), "Cannot inspect Shell point type/process.");
    point_automation_id = Property(current.Get(), UIA_AutomationIdPropertyId).substr(0, 96);
    if (static_cast<DWORD>(point_pid) != shell_pid) return false;
    ComPtr<IUIAutomationTreeWalker> walker;
    Available(SUCCEEDED(automation->get_RawViewWalker(&walker)), "Cannot inspect the bounded Shell point ancestry.");
    // A button's text/content may own the UIA hit-test point. Only exact element
    // identity within five Shell-owned parents is accepted, never a name/rect
    // approximation. No parent names are read.
    for (int depth = 0; current && depth <= 5; ++depth) {
      int pid = 0; BOOL same = FALSE;
      Available(SUCCEEDED(current->get_CurrentProcessId(&pid)), "Cannot inspect Shell point ancestor process.");
      if (static_cast<DWORD>(pid) != shell_pid) return false;
      Available(SUCCEEDED(automation->CompareElements(expected, current.Get(), &same)), "Cannot compare exact Shell element identity.");
      if (same) { point_ancestor_depth = depth; return true; }
      if (depth == 5) break;
      ComPtr<IUIAutomationElement> parent;
      Available(SUCCEEDED(walker->GetParentElement(current.Get(), &parent)), "Cannot inspect Shell point parent.");
      current = std::move(parent);
    }
    return false;
  }
  ComPtr<IUIAutomationElementArray> OverflowButtons(HWND taskbar) {
    auto root = Element(taskbar);
    // This exact triple was observed in run10. Filtering happens in UIA:
    // application icon names are neither enumerated nor read by this driver.
    auto name = StringCondition(UIA_NamePropertyId, L"显示隐藏的图标");
    auto id = StringCondition(UIA_AutomationIdPropertyId, L"SystemTrayIcon");
    ComPtr<IUIAutomationCondition> name_and_id, type, exact;
    Available(SUCCEEDED(automation->CreateAndCondition(name.Get(), id.Get(), &name_and_id)),
              "Cannot bind the observed overflow identity.");
    VARIANT value; VariantInit(&value); value.vt = VT_I4; value.lVal = UIA_ButtonControlTypeId;
    Available(SUCCEEDED(automation->CreatePropertyCondition(UIA_ControlTypePropertyId, value, &type)) &&
                  SUCCEEDED(automation->CreateAndCondition(name_and_id.Get(), type.Get(), &exact)),
              "Cannot bind the observed overflow button type.");
    ComPtr<IUIAutomationElementArray> matches;
    Available(SUCCEEDED(root->FindAll(TreeScope_Descendants, exact.Get(), &matches)),
              "Cannot enumerate the exact observed overflow button.");
    return matches;
  }
  ComPtr<IUIAutomationElement> OverflowButton() {
    overflow_candidates = "[]";
    HWND taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
    Available(taskbar && Owner(taskbar) == shell_pid, "The exact Shell taskbar is unavailable for overflow.");
    auto matches = OverflowButtons(taskbar);
    Available(SUCCEEDED(matches->get_Length(&overflow_button_matches)), "Cannot count the exact overflow button.");
    Available(overflow_button_matches == 1, "The observed overflow button is absent or ambiguous.");
    ComPtr<IUIAutomationElement> element;
    Available(SUCCEEDED(matches->GetElement(0, &element)), "Cannot inspect the exact overflow button.");
    // Re-read only the already filtered candidate, never other application names.
    const auto name = Property(element.Get(), UIA_NamePropertyId);
    const auto id = Property(element.Get(), UIA_AutomationIdPropertyId);
    CONTROLTYPEID type = 0; int pid = 0; BOOL offscreen = TRUE, enabled = FALSE;
    Available(name == L"显示隐藏的图标" && id == L"SystemTrayIcon" &&
              SUCCEEDED(element->get_CurrentControlType(&type)) && type == UIA_ButtonControlTypeId &&
              SUCCEEDED(element->get_CurrentProcessId(&pid)) && static_cast<DWORD>(pid) == shell_pid &&
              SUCCEEDED(element->get_CurrentIsOffscreen(&offscreen)) && !offscreen &&
              SUCCEEDED(element->get_CurrentIsEnabled(&enabled)) && enabled &&
              SUCCEEDED(element->get_CurrentBoundingRectangle(&overflow_button_rect)) &&
              overflow_button_rect.right > overflow_button_rect.left && overflow_button_rect.bottom > overflow_button_rect.top,
              "The exact overflow button is not visible/enabled in the bound Shell.");
    std::ostringstream out;
    out << "[{\"automationId\":" << JsonWide(id) << ",\"name\":" << JsonWide(name)
        << ",\"controlType\":" << type << "}]";
    overflow_candidates = out.str();
    POINT center{overflow_button_rect.left + (overflow_button_rect.right - overflow_button_rect.left) / 2,
                 overflow_button_rect.top + (overflow_button_rect.bottom - overflow_button_rect.top) / 2};
    HWND hit = WindowFromPoint(center);
    Available(hit && Owner(hit) == shell_pid && GetAncestor(hit, GA_ROOT) == taskbar,
              "The exact overflow button is physically occluded.");
    Available(PointBelongs(element.Get(), center),
              "The physical overflow point is not the unique observed button.");
    return element;
  }
  bool TryFind(RECT& rect, bool awaiting_overflow) {
    observation_stage = "guid-rectangle"; tooltip_matches = -1;
    NOTIFYICONIDENTIFIER identity{}; identity.cbSize = sizeof(identity); identity.guidItem = shared.tray_guid;
    rect = {}; last_rect_result = Shell_NotifyIconGetRect(&identity, &rect); last_guid_rect = rect;
    if (awaiting_overflow && (FAILED(last_rect_result) || rect.right <= rect.left || rect.bottom <= rect.top)) return false;
    Available(SUCCEEDED(last_rect_result) && rect.right > rect.left && rect.bottom > rect.top,
              "The exact tray GUID has no rectangle; no unbound overflow action was attempted.");
    POINT center{rect.left + (rect.right - rect.left) / 2, rect.top + (rect.bottom - rect.top) / 2};
    HWND hit = WindowFromPoint(center), host = GetAncestor(hit, GA_ROOT);
    last_hit = hit; observed_host = host; observed_host_class.clear();
    wchar_t class_name[128]{};
    if (host && GetClassNameW(host, class_name, 128)) observed_host_class = class_name;
    observation_stage = "guid-point-shell-ownership";
    if (awaiting_overflow && (!hit || Owner(hit) != shell_pid || !host || Owner(host) != shell_pid)) return false;
    Available(hit && Owner(hit) == shell_pid && host && Owner(host) == shell_pid,
              "The exact tray rectangle is occluded or not owned by the bound Shell.");
    auto root = Element(host); auto matches = Named(root.Get()); int count = 0;
    observation_stage = "exact-tooltip-count";
    Available(SUCCEEDED(matches->get_Length(&count)), "Cannot count the exact fixture tooltip.");
    tooltip_matches = count;
    Available(count <= 1 && count >= 0, "The fixture tooltip is ambiguous in the exact Shell host.");
    if (count == 0) return false;
    ComPtr<IUIAutomationElement> element;
    Available(SUCCEEDED(matches->GetElement(0, &element)), "Cannot inspect the fixture tray element.");
    int pid = 0; BOOL offscreen = TRUE, enabled = FALSE; RECT bounds{};
    observation_stage = "exact-tooltip-visibility";
    Available(SUCCEEDED(element->get_CurrentProcessId(&pid)) && static_cast<DWORD>(pid) == shell_pid &&
              SUCCEEDED(element->get_CurrentIsOffscreen(&offscreen)) &&
              SUCCEEDED(element->get_CurrentIsEnabled(&enabled)) &&
              SUCCEEDED(element->get_CurrentBoundingRectangle(&bounds)),
              "Cannot inspect the exact fixture tray element visibility/identity.");
    last_element_rect = bounds;
    if (awaiting_overflow && (offscreen || !enabled || !PtInRect(&bounds, center))) return false;
    Available(!offscreen && enabled && PtInRect(&bounds, center), "The exact fixture tray element is not visible/enabled at the GUID rectangle.");
    observation_stage = "exact-tooltip-point";
    bool belongs = PointBelongs(element.Get(), center);
    if (awaiting_overflow && !belongs) return false;
    Available(belongs, "The actual tray click target is not the uniquely bound fixture element.");
    last_icon_host = host;
    shared.tray_rect = rect; Publish(&shared.tray_rect_ready, 1);
    observation_stage = "exact-icon-ready";
    return true;
  }
  RECT Find() {
    ShellIdentity(); RECT rect{};
    Available(TryFind(rect, false), "The exact fixture tooltip is absent in its current Shell host.");
    return rect;
  }
  void Inject(POINT point, bool right, HWND allowed_capture, const char* kind) {
    DesktopGuard(); IdleKeys();
    GUITHREADINFO gui{}; gui.cbSize = sizeof(gui);
    Available(GetGUIThreadInfo(0, &gui), "Cannot inspect foreground input ownership.");
    Available(!gui.hwndCapture || gui.hwndCapture == allowed_capture,
              "An unrelated mouse capture blocks Shell input.");
    if (!cursor_saved) { Available(GetCursorPos(&original_cursor), "Cannot retain the original cursor."); cursor_saved = true; }
    int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
    int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    Available(width > 1 && height > 1 && point.x >= left && point.x < left + width && point.y >= top && point.y < top + height,
              "The owned Shell input point is outside the physical desktop.");
    Require(shared.input_marker > 0 && shared.input_marker <= 0x7fffffff, "Invalid lifecycle input marker.");
    Require(input_attempts < 64, "The bounded Shell input attempt limit was reached.");
    INPUT input[3]{};
    for (auto& item : input) { item.type = INPUT_MOUSE; item.mi.dwExtraInfo = shared.input_marker; }
    input[0].mi.dx = static_cast<LONG>((static_cast<int64_t>(point.x - left) * 65535) / (width - 1));
    input[0].mi.dy = static_cast<LONG>((static_cast<int64_t>(point.y - top) * 65535) / (height - 1));
    input[0].mi.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE;
    input[1].mi.dwFlags = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
    input[2].mi.dwFlags = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
    SetLastError(ERROR_SUCCESS); UINT inserted = SendInput(3, input, sizeof(INPUT)); DWORD error = GetLastError();
    Publish(&shared.tray_last_send_requested, 3); Publish(&shared.tray_last_send_inserted, static_cast<LONG>(inserted));
    Publish(&shared.tray_last_send_error, static_cast<LONG>(error));
    last_cursor = point; cursor_injected = true;
    ++input_attempts; last_input = {kind, shared.generation};
    Require(inserted == 3, "SendInput did not insert the complete owned Shell click.");
  }
  void ClickIcon(const RECT& rect, bool right) {
    POINT center{rect.left + (rect.right - rect.left) / 2, rect.top + (rect.bottom - rect.top) / 2};
    Available(Owner(WindowFromPoint(center)) == shell_pid, "The exact tray point changed before injection.");
    Inject(center, right, nullptr, right ? "icon-right" : "icon-left");
  }
  void Click(bool right) {
    Require(!pending_click, "A Shell overflow click is already pending.");
    overflow_expansion_attempted = false; overflow_icon_ready = false;
    overflow_state = "not-requested"; overflow_candidates = "[]";
    overflow_button_matches = -1; overflow_button_rect = {};
    ShellIdentity(); RECT rect{};
    if (TryFind(rect, false)) { ClickIcon(rect, right); return; }
    // Only zero exact-tooltip matches may initiate expansion. Ambiguity and
    // unknown GUID/ownership states fail in TryFind without clicking anything.
    Require(tooltip_matches == 0, "Overflow expansion lacks a zero-match observation.");
    observation_stage = "overflow-button-identity";
    auto button = OverflowButton();
    (void)button;
    POINT center{overflow_button_rect.left + (overflow_button_rect.right - overflow_button_rect.left) / 2,
                 overflow_button_rect.top + (overflow_button_rect.bottom - overflow_button_rect.top) / 2};
    pending_right = right; pending_generation = shared.generation; pending_nonce = shared.nonce;
    pending_guid = shared.tray_guid; pending_deadline = GetTickCount64() + 5000;
    overflow_expansion_attempted = true; overflow_icon_ready = false;
    overflow_state = "expansion-requested";
    try {
      Inject(center, false, nullptr, "overflow-expand");
      pending_click = true;
      overflow_state = "awaiting-exact-icon";
    } catch (...) { pending_click = false; overflow_state = "failed"; throw; }
  }
  void Poll() {
    if (!pending_click) return;
    try {
      Require(shared.generation == pending_generation && shared.nonce == pending_nonce &&
                  IsEqualGUID(shared.tray_guid, pending_guid), "Pending tray request identity changed.");
      Available(GetTickCount64() < pending_deadline, "The exact tray icon did not become ready after one overflow expansion.");
      ShellIdentity(); RECT rect{};
      if (!TryFind(rect, true)) return;
      Available(GetTickCount64() < pending_deadline, "The overflow observation exceeded its deadline; no icon click was injected.");
      pending_click = false; overflow_icon_ready = true;
      overflow_state = "exact-icon-ready";
      ClickIcon(rect, pending_right);
      overflow_state = "icon-click-inserted";
    } catch (...) { pending_click = false; overflow_state = "failed"; throw; }
  }
  void CancelPending() {
    if (pending_click) { pending_click = false; overflow_state = "cancelled"; }
  }
  void Menu(UINT command) {
    Require(!pending_click, "A menu action cannot interrupt a pending overflow click.");
    ShellIdentity();
    UINT index = command == kMenuShow ? 0 : command == kMenuPassthrough ? 1 : command == kMenuExit ? 2 : 3;
    Require(index < 3, "Unknown fixture menu command.");
    Available(Read(&shared.menu_open) == 1 && Read(&shared.menu_rects_ready) == 1, "The real fixture menu is not ready.");
    HMENU menu = reinterpret_cast<HMENU>(static_cast<uintptr_t>(Read(&shared.menu_handle)));
    HWND owner = Window(Read(&shared.top_hwnd)); DWORD primary_pid = static_cast<DWORD>(Read(&shared.primary_pid));
    RECT rect{};
    Available(menu && IsMenu(menu) && GetMenuItemID(menu, static_cast<int>(index)) == command &&
              GetMenuItemRect(nullptr, menu, index, &rect) && SameRect(rect, shared.menu_rects[index]),
              "The real menu/item identity changed before input.");
    POINT center{rect.left + (rect.right - rect.left) / 2, rect.top + (rect.bottom - rect.top) / 2};
    HWND hit = WindowFromPoint(center); wchar_t class_name[64]{};
    MENUBARINFO info{}; info.cbSize = sizeof(info);
    DWORD thread = GetWindowThreadProcessId(owner, nullptr);
    GUITHREADINFO gui{}; gui.cbSize = sizeof(gui);
    Available(hit && Owner(hit) == primary_pid && GetClassNameW(hit, class_name, 64) && wcscmp(class_name, L"#32768") == 0 &&
              GetMenuBarInfo(hit, OBJID_CLIENT, 0, &info) && info.hMenu == menu &&
              GetGUIThreadInfo(thread, &gui) && gui.hwndMenuOwner == owner && GetForegroundWindow() == owner,
              "The physical menu point is not the exact owned popup/menu/foreground.");
    Publish(&shared.menu_window, reinterpret_cast<uintptr_t>(hit));
    // Native menu loops may capture on the menu or its owner; no other capture
    // is accepted. The exact target is read again immediately before SendInput.
    Available(!gui.hwndCapture || gui.hwndCapture == hit || gui.hwndCapture == owner,
              "An unrelated capture blocks the fixture menu.");
    Available(WindowFromPoint(center) == hit, "The fixture menu became occluded before input.");
    Inject(center, false, gui.hwndCapture, "menu-item");
  }
  bool Absent() {
    ShellIdentity();
    if (Read(&shared.tray_deleted) != 1) return false;
    NOTIFYICONIDENTIFIER identity{}; identity.cbSize = sizeof(identity); identity.guidItem = shared.tray_guid;
    RECT rect{}; last_rect_result = Shell_NotifyIconGetRect(&identity, &rect);
    if (SUCCEEDED(last_rect_result)) return false;
    if (last_icon_host && IsWindow(last_icon_host) && Owner(last_icon_host) == shell_pid) {
      auto root = Element(last_icon_host); auto matches = Named(root.Get()); int count = 0;
      Available(SUCCEEDED(matches->get_Length(&count)), "Cannot verify fixture tooltip removal.");
      if (count != 0) return false;
    }
    return true;
  }
  std::string Diagnostics() const {
    std::ostringstream out;
    out << "{\"schemaVersion\":1,\"shellPid\":" << shell_pid << ",\"shellCreation\":" << shell_creation
        << ",\"iconRectHresult\":" << static_cast<int64_t>(last_rect_result)
        << ",\"cursorSaved\":" << (cursor_saved ? "true" : "false")
        << ",\"cursorRestored\":" << (cursor_restored ? "true" : "false")
        << ",\"lastInjectedPoint\":[" << last_cursor.x << ',' << last_cursor.y << ']'
        << ",\"lastSendRequested\":" << Read(&shared.tray_last_send_requested)
        << ",\"lastSendInserted\":" << Read(&shared.tray_last_send_inserted)
        << ",\"lastSendError\":" << Read(&shared.tray_last_send_error)
        << ",\"recognizedOverflowCandidates\":" << overflow_candidates
        << ",\"observationStage\":\"" << observation_stage << "\",\"tooltipMatches\":" << tooltip_matches
        << ",\"guidRect\":[" << last_guid_rect.left << ',' << last_guid_rect.top << ',' << last_guid_rect.right << ',' << last_guid_rect.bottom << ']'
        << ",\"elementRect\":[" << last_element_rect.left << ',' << last_element_rect.top << ',' << last_element_rect.right << ',' << last_element_rect.bottom << ']'
        << ",\"guidHit\":" << reinterpret_cast<uintptr_t>(last_hit)
        << ",\"guidHost\":" << reinterpret_cast<uintptr_t>(observed_host) << ",\"guidHostClass\":" << JsonWide(observed_host_class)
        << ",\"lastPoint\":[" << point_pid << ',' << point_type << ',' << point_ancestor_depth << ',' << JsonWide(point_automation_id) << ']'
        << ",\"overflowExpansionAttempted\":" << (overflow_expansion_attempted ? "true" : "false")
        << ",\"overflowPending\":" << (pending_click ? "true" : "false")
        << ",\"overflowIconReady\":" << (overflow_icon_ready ? "true" : "false")
        << ",\"overflowState\":\"" << overflow_state << "\",\"overflowButtonMatches\":" << overflow_button_matches
        << ",\"overflowButtonRect\":[" << overflow_button_rect.left << ',' << overflow_button_rect.top << ','
        << overflow_button_rect.right << ',' << overflow_button_rect.bottom << ']'
        << ",\"inputAttempts\":" << input_attempts << ",\"lastInputKind\":\"" << last_input.kind
        << "\",\"lastInputGeneration\":" << last_input.generation << ",\"foregroundRestoration\":\"none\"}";
    return out.str();
  }
};
ShellTrayDriver::ShellTrayDriver(Shared& shared) : impl_(std::make_unique<Impl>(shared)) {}
ShellTrayDriver::~ShellTrayDriver() = default;
RECT ShellTrayDriver::FindTrayIcon() { return impl_->Find(); }
void ShellTrayDriver::ClickTray(bool right) { impl_->Click(right); }
void ShellTrayDriver::Poll() { impl_->Poll(); }
bool ShellTrayDriver::ClickPending() const { return impl_->pending_click; }
void ShellTrayDriver::CancelPendingClick() { impl_->CancelPending(); }
void ShellTrayDriver::ClickTrayMenu(UINT command) { impl_->Menu(command); }
bool ShellTrayDriver::TrayAbsent() { return impl_->Absent(); }
std::string ShellTrayDriver::DiagnosticsJson() const { return impl_->Diagnostics(); }
}  // namespace lifecycle_probe
