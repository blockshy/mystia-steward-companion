#include "tray_probe.h"

#include <commctrl.h>
#include <shellapi.h>
#include <windowsx.h>
#include <stdexcept>
#include <utility>

#include "resource.h"

namespace lifecycle_probe {
namespace {
void Require(bool condition, const char* message) {
  if (!condition) throw std::runtime_error(message);
}
}
struct TrayProbe::Impl {
  Shared& shared;
  HWND owner;
  std::function<void(UINT)> command;
  HICON icon = nullptr;
  HMENU menu = nullptr;
  UINT taskbar_created = 0;
  bool added = false, closing = false, menu_active = false, readd_pending = false;

  Impl(Shared& state, HWND window, std::function<void(UINT)> apply)
      : shared(state), owner(window), command(std::move(apply)) {
    taskbar_created = RegisterWindowMessageW(L"TaskbarCreated");
    Require(taskbar_created != 0, "Cannot register TaskbarCreated observation.");
    Require(SUCCEEDED(LoadIconMetric(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_APP_ICON), LIM_SMALL, &icon)),
            "Cannot load this fixture's tray icon.");
  }
  ~Impl() {
    closing = true;
    if (menu_active) EndMenu();
    Delete();
    if (menu) DestroyMenu(menu);
    if (icon) DestroyIcon(icon);
  }
  NOTIFYICONDATAW Data() const {
    NOTIFYICONDATAW data{}; data.cbSize = sizeof(data);
    data.hWnd = owner; data.uID = kTrayId; data.guidItem = shared.tray_guid;
    data.uFlags = NIF_GUID;
    return data;
  }
  void Add() {
    Require(!closing && !added, "Tray registration cannot be replayed in this state.");
    auto data = Data();
    data.uFlags |= NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP;
    data.hIcon = icon; data.uCallbackMessage = kTrayCallback;
    Require(wcsnlen_s(shared.tray_tooltip, 128) > 0 && wcsnlen_s(shared.tray_tooltip, 128) < 128,
            "The inherited tray tooltip is not bounded.");
    wcscpy_s(data.szTip, shared.tray_tooltip);
    Require(Shell_NotifyIconW(NIM_ADD, &data) != FALSE, "Shell rejected the fixture tray icon.");
    added = true; Publish(&shared.tray_added, 1); Publish(&shared.tray_deleted, 0);
    data.uVersion = NOTIFYICON_VERSION_4;
    Require(Shell_NotifyIconW(NIM_SETVERSION, &data) != FALSE, "Shell rejected notification icon version 4.");
    Publish(&shared.tray_versioned, 1);
  }
  bool Delete() {
    closing = true;
    if (!added) return Read(&shared.tray_deleted) != 0;
    auto data = Data();
    bool deleted = Shell_NotifyIconW(NIM_DELETE, &data) != FALSE;
    if (deleted) { added = false; Publish(&shared.tray_deleted, 1); }
    return deleted;
  }
  void PollMenu() {
    if (!menu_active || !menu || Read(&shared.menu_rects_ready)) return;
    RECT rectangles[3]{};
    for (UINT index = 0; index < 3; ++index) {
      if (!GetMenuItemRect(nullptr, menu, index, &rectangles[index]) ||
          rectangles[index].right <= rectangles[index].left || rectangles[index].bottom <= rectangles[index].top) return;
    }
    POINT center{rectangles[0].left + (rectangles[0].right - rectangles[0].left) / 2,
                 rectangles[0].top + (rectangles[0].bottom - rectangles[0].top) / 2};
    HWND popup = WindowFromPoint(center);
    DWORD pid = 0; GetWindowThreadProcessId(popup, &pid);
    MENUBARINFO info{}; info.cbSize = sizeof(info);
    if (!popup || pid != GetCurrentProcessId() || !GetMenuBarInfo(popup, OBJID_CLIENT, 0, &info) || info.hMenu != menu) return;
    for (size_t index = 0; index < 3; ++index) shared.menu_rects[index] = rectangles[index];
    Publish(&shared.menu_window, reinterpret_cast<uintptr_t>(popup));
    Publish(&shared.menu_rects_ready, 1);
  }
  void ShowMenu() {
    Require(!closing && added && !menu_active, "Unexpected overlapping tray menu request.");
    NOTIFYICONIDENTIFIER identity{}; identity.cbSize = sizeof(identity); identity.guidItem = shared.tray_guid;
    RECT icon_rect{};
    Require(SUCCEEDED(Shell_NotifyIconGetRect(&identity, &icon_rect)), "Cannot anchor the exact registered tray icon.");
    menu = CreatePopupMenu(); Require(menu != nullptr, "Cannot create fixture tray menu.");
    try {
      Require(AppendMenuW(menu, MF_STRING, kMenuShow, L"显示 mystia-steward-companion") &&
                  AppendMenuW(menu, MF_STRING | (Read(&shared.passthrough) ? MF_CHECKED : MF_UNCHECKED),
                              kMenuPassthrough, L"切换鼠标穿透") &&
                  AppendMenuW(menu, MF_STRING, kMenuExit, L"退出"), "Cannot populate fixture tray menu.");
      // The menu uses the real owner and OS menu loop. Temporarily permit that
      // owner to activate; the primary restores its exact mode on the next Poll.
      LONG_PTR style = GetWindowLongPtrW(owner, GWL_EXSTYLE);
      if (style & WS_EX_NOACTIVATE) {
        SetLastError(0);
        auto previous = SetWindowLongPtrW(owner, GWL_EXSTYLE, style & ~WS_EX_NOACTIVATE);
        Require(previous != 0 || GetLastError() == 0, "Cannot allow the owned tray menu to activate.");
      }
      SetForegroundWindow(owner);
      Require(GetForegroundWindow() == owner, "The tray menu owner did not obtain foreground.");
      menu_active = true;
      Publish(&shared.menu_handle, reinterpret_cast<uintptr_t>(menu));
      Publish(&shared.menu_window, 0);
      Publish(&shared.menu_rects_ready, 0); Publish(&shared.menu_open, 1);
      UINT flags = TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN;
      flags |= GetSystemMetrics(SM_MENUDROPALIGNMENT) ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
      UINT selected = static_cast<UINT>(TrackPopupMenuEx(menu, flags,
          (icon_rect.left + icon_rect.right) / 2, icon_rect.top, owner, nullptr));
      menu_active = false;
      Publish(&shared.menu_open, 0); Publish(&shared.menu_rects_ready, 0); Publish(&shared.menu_handle, 0);
      DestroyMenu(menu); menu = nullptr;
      PostMessageW(owner, WM_NULL, 0, 0);
      if (selected) {
        Require(selected == kMenuShow || selected == kMenuPassthrough || selected == kMenuExit,
                "Shell returned an unknown fixture menu command.");
        Publish(&shared.last_menu_command, static_cast<LONG>(selected));
        InterlockedIncrement(&shared.menu_commands);
        command(selected);
      } else if (!closing) {
        auto data = Data(); Shell_NotifyIconW(NIM_SETFOCUS, &data);
      }
      if (readd_pending && !closing) { readd_pending = false; Add(); InterlockedIncrement(&shared.tray_readds); }
    } catch (...) {
      menu_active = false; Publish(&shared.menu_open, 0); Publish(&shared.menu_rects_ready, 0);
      Publish(&shared.menu_handle, 0);
      if (menu) { DestroyMenu(menu); menu = nullptr; }
      throw;
    }
  }
  std::optional<LRESULT> Handle(UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == taskbar_created) {
      if (closing) return 0;
      InterlockedIncrement(&shared.tray_taskbar_created);
      // A primary-display DPI change can also produce this broadcast. Remove
      // only our GUID before re-adding, without claiming an Explorer restart.
      if (added) { auto data = Data(); Shell_NotifyIconW(NIM_DELETE, &data); added = false; }
      Publish(&shared.tray_versioned, 0);
      if (menu_active) { readd_pending = true; EndMenu(); }
      else { Add(); InterlockedIncrement(&shared.tray_readds); }
      return 0;
    }
    if (message == WM_ENTERIDLE && menu_active) { PollMenu(); return std::nullopt; }
    if (message != kTrayCallback) return std::nullopt;
    if (closing) return 0;
    Require(HIWORD(lparam) == kTrayId, "Notification callback icon identity differs.");
    UINT event = LOWORD(lparam);
    Publish(&shared.last_tray_event, static_cast<LONG>(event)); InterlockedIncrement(&shared.tray_callbacks);
    if (event == NIN_SELECT || event == NIN_KEYSELECT) command(kMenuShow);
    else if (event == WM_CONTEXTMENU) ShowMenu();
    (void)wparam;
    return 0;
  }
};
TrayProbe::TrayProbe(Shared& shared, HWND owner, std::function<void(UINT)> command)
    : impl_(std::make_unique<Impl>(shared, owner, std::move(command))) {}
TrayProbe::~TrayProbe() = default;
void TrayProbe::Add() { impl_->Add(); }
bool TrayProbe::Delete() { return impl_->Delete(); }
bool TrayProbe::MenuActive() const { return impl_->menu_active; }
void TrayProbe::CancelMenu() { impl_->closing = true; if (impl_->menu_active) EndMenu(); }
void TrayProbe::PollMenu() { impl_->PollMenu(); }
std::optional<LRESULT> TrayProbe::HandleWindowMessage(UINT message, WPARAM wparam, LPARAM lparam) {
  return impl_->Handle(message, wparam, lparam);
}
}  // namespace lifecycle_probe
