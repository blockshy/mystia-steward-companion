#include "platform_bridge.h"
#include <ws2tcpip.h>
#include <commctrl.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <shlobj.h>
#include <wincrypt.h>
#include <Xinput.h>
#include <algorithm>
#include <atomic>
#include <deque>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <sstream>
#include <regex>
#include <stdexcept>
#include <thread>
#include "focus_transition.h"

namespace {
using namespace mystia_platform;
constexpr UINT kControlMessage = WM_APP + 31;
constexpr UINT kTrayMessage = WM_APP + 32;
constexpr UINT_PTR kTimer = 0x4d5932;
constexpr int kHotkey = 0x4d5933;
constexpr unsigned short kControlPort = 32146;
unsigned short control_port = kControlPort;
std::string isolation_id;
constexpr size_t kMaximumFileBytes = 1024 * 1024;
std::mutex control_mutex;
std::deque<std::string> commands;
std::vector<std::string> launch_arguments;
std::atomic<bool> stopping{false};
std::atomic<HWND> control_window{nullptr};
SOCKET listener = INVALID_SOCKET;
std::thread control_thread;

void Require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
FlutterError Error(const std::exception& error) { return FlutterError("platform-failure", error.what()); }
uint64_t CreationTime(HANDLE process) {
  FILETIME created{}, exited{}, kernel{}, user{};
  Require(GetProcessTimes(process, &created, &exited, &kernel, &user) != FALSE, "process identity unavailable");
  return (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
}

/// 二次启动也先握手验证同一路径/创建时间，不能把命令行 Token 发送给占用端口的陌生进程。
std::pair<DWORD, uint64_t> ParseIdentity(const std::string& response) {
  const std::string prefix = "mystia-companion-v2:ok:";
  Require(response.rfind(prefix, 0) == 0 && response.back() == '\n', "control acknowledgement invalid");
  const auto separator = response.find(':', prefix.size());
  Require(separator != std::string::npos, "control acknowledgement identity missing");
  const auto pid_text = response.substr(prefix.size(), separator - prefix.size());
  const auto time_text = response.substr(separator + 1, response.size() - separator - 2);
  const auto digits = [](const std::string& value) { return !value.empty() && std::all_of(value.begin(), value.end(), [](char ch) { return ch >= '0' && ch <= '9'; }); };
  Require(digits(pid_text) && digits(time_text), "control acknowledgement identity invalid");
  const auto pid = std::stoull(pid_text), time = std::stoull(time_text);
  Require(pid > 0 && pid <= MAXDWORD && time > 0, "control acknowledgement identity outside bounds");
  return {static_cast<DWORD>(pid), time};
}

std::string ExchangeControl(const sockaddr_in& address, const std::string& request) {
  SOCKET client = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  Require(client != INVALID_SOCKET, "control socket unavailable");
  try {
    // 非阻塞 connect 配合 select，避免陌生端口或防火墙导致第二实例长时间挂起。
    u_long nonblocking = 1; ioctlsocket(client, FIONBIO, &nonblocking);
    const auto connected = connect(client, reinterpret_cast<const sockaddr*>(&address), sizeof(address));
    Require(connected == 0 || WSAGetLastError() == WSAEWOULDBLOCK, "control connection refused");
    if (connected != 0) {
      fd_set writes; FD_ZERO(&writes); FD_SET(client, &writes); timeval timeout{0, 500000};
      Require(select(0, nullptr, &writes, nullptr, &timeout) > 0, "control connection timed out");
      int error = 0, length = sizeof(error); getsockopt(client, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &length);
      Require(error == 0, "control connection failed");
    }
    nonblocking = 0; ioctlsocket(client, FIONBIO, &nonblocking);
    DWORD timeout = 500;
    setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    size_t written = 0;
    while (written < request.size()) {
      const auto count = send(client, request.data() + written, static_cast<int>(request.size() - written), 0);
      Require(count > 0, "control write failed"); written += static_cast<size_t>(count);
    }
    shutdown(client, SD_SEND);
    std::string response; char next = 0; const auto deadline = GetTickCount64() + 700;
    while (response.size() < 128 && GetTickCount64() < deadline) {
      Require(recv(client, &next, 1, 0) == 1, "control acknowledgement incomplete");
      response.push_back(next); if (next == '\n') break;
    }
    closesocket(client); return response;
  } catch (...) { closesocket(client); throw; }
}

struct ConnectionArguments {
  std::optional<std::string> endpoint;
  std::optional<std::string> token;
};

/// 首次启动与单实例激活共用同一凭据解析规则。地址和Token必须在本次参数中
/// 各出现一次，不能逐项覆盖旧launch而组成“新地址+旧Token”。显式空Token
/// 表示没有凭据，供隔离离线启动使用；缺少Token参数不能代替显式空值。
/// 只校验本边界的成对性，具体地址/Token格式仍由Dart协议层负责。
ConnectionArguments ParseConnectionArguments(const std::vector<std::string>& arguments) {
  ConnectionArguments result;
  for (const auto& arg : arguments) {
    const bool endpoint = arg.rfind("--api=", 0) == 0;
    const bool token = arg.rfind("--token=", 0) == 0;
    if (!endpoint && !token) continue;
    Require(arg.find_first_of("\r\n") == std::string::npos && arg.find('\0') == std::string::npos,
            "connection argument contains a frame delimiter");
    auto& value = endpoint ? result.endpoint : result.token;
    Require(!value.has_value(), "connection argument duplicated");
    value = arg.substr(endpoint ? 6 : 8);
  }
  Require(result.endpoint.has_value() == result.token.has_value(), "API and token must be supplied together");
  Require(!result.endpoint || !result.endpoint->empty(), "API argument is empty");
  return result;
}

/// 只接受一个完整、上限固定的请求；永不在日志中记录其中的 Token。
bool ValidCommand(const std::string& text) {
  if (text.empty() || text.size() > 4096 || text.back() != '\n' || text.find('\0') != std::string::npos || text.find('\r') != std::string::npos) return false;
  std::istringstream input(text); std::string line;
  std::getline(input, line);
  if (line == "mystia-companion-v2:identity") return text == "mystia-companion-v2:identity\n";
  if (line != "mystia-companion-v2:show" && line != "mystia-companion-v2:toggle" && line != "mystia-companion-v2:exit") return false;
  std::vector<std::string> arguments;
  while (std::getline(input, line)) {
    if (line.empty()) continue;
    if (line.rfind("--api=", 0) != 0 && line.rfind("--token=", 0) != 0 && line.rfind("--game-pid=", 0) != 0) return false;
    arguments.push_back(line);
  }
  // 在排入UI线程前拒绝不完整更新；拒绝不能改变现有凭据、激活代次或窗口状态。
  try { ParseConnectionArguments(arguments); return true; }
  catch (const std::exception&) { return false; }
}

void ServeControl() {
  while (!stopping) {
    fd_set read_set; FD_ZERO(&read_set); FD_SET(listener, &read_set);
    timeval timeout{0, 100000};
    if (select(0, &read_set, nullptr, nullptr, &timeout) <= 0) continue;
    SOCKET client = accept(listener, nullptr, nullptr);
    if (client == INVALID_SOCKET) continue;
    DWORD limit = 300;
    setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&limit), sizeof(limit));
    setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&limit), sizeof(limit));
    std::string request; char buffer[512];
    // 调用方在写完请求后 shutdown(SD_SEND)；也支持无参数单行命令立即提交。
    while (request.size() <= 4096) {
      int size = recv(client, buffer, sizeof(buffer), 0);
      if (size <= 0) break;
      request.append(buffer, static_cast<size_t>(size));
      if (request == "mystia-companion-v2:exit\n") break;
    }
    bool accepted = ValidCommand(request);
    const bool identity_only = request == "mystia-companion-v2:identity\n";
    if (accepted && !identity_only) {
      std::lock_guard<std::mutex> guard(control_mutex);
      if (commands.size() >= 32) accepted = false;
      else commands.push_back(request);
    }
    if (accepted) {
      const auto response = "mystia-companion-v2:ok:" + std::to_string(GetCurrentProcessId()) + ":" + std::to_string(CreationTime(GetCurrentProcess())) + "\n";
      // ACK 仅确认该进程已接管请求；退出完成由调用方持有进程句柄等待确认。
      send(client, response.data(), static_cast<int>(response.size()), 0);
      if (!identity_only) if (HWND window = control_window.load()) PostMessageW(window, kControlMessage, 0, 0);
    } else { const char error[] = "mystia-companion-v2:rejected\n"; send(client, error, sizeof(error) - 1, 0); }
    closesocket(client);
  }
}

/// 检查每一级目录的重解析属性，避免身份或 Token 被重定向到外部目录。
std::filesystem::path StorageRoot() {
  PWSTR value = nullptr;
  Require(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &value)), "local application directory unavailable");
  std::filesystem::path root(value); CoTaskMemFree(value);
  root /= L"mystia-steward-companion"; root /= L"flutter-v1";
  // 离线测试明确使用隔离名称，不读取或改写真实客户端凭据与窗口状态。
  if (!isolation_id.empty()) { root /= L"isolation"; root /= isolation_id; }
  std::filesystem::path current;
  for (const auto& part : root) {
    current /= part;
    if (current == current.root_name()) continue;
    auto attrs = GetFileAttributesW(current.c_str());
    if (attrs == INVALID_FILE_ATTRIBUTES) {
      Require(GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND, "storage path inaccessible");
      Require(CreateDirectoryW(current.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS, "storage directory create failed");
      attrs = GetFileAttributesW(current.c_str());
    }
    Require(attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) && !(attrs & FILE_ATTRIBUTE_REPARSE_POINT), "storage directory reparse point refused");
  }
  return root;
}
std::filesystem::path SecretPath(const std::string& key) {
  Require(!key.empty() && key.size() <= 64 && std::all_of(key.begin(), key.end(), [](char ch) {
    return (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-';
  }), "invalid secret key");
  return StorageRoot() / (key + ".dpapi");
}
std::optional<std::string> ReadFile(const std::filesystem::path& path) {
  DWORD attrs = GetFileAttributesW(path.c_str());
  if (attrs == INVALID_FILE_ATTRIBUTES && GetLastError() == ERROR_FILE_NOT_FOUND) return std::nullopt;
  Require(attrs != INVALID_FILE_ATTRIBUTES && !(attrs & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)), "invalid persisted file");
  std::ifstream file(path, std::ios::binary);
  Require(file.good(), "persisted file read failed");
  file.seekg(0, std::ios::end); auto size = file.tellg();
  Require(size >= 0 && size <= static_cast<std::streamoff>(kMaximumFileBytes), "persisted file limit exceeded");
  file.seekg(0); std::string value(static_cast<size_t>(size), '\0');
  file.read(value.data(), static_cast<std::streamsize>(value.size()));
  Require(file.good() || file.eof(), "persisted file incomplete");
  return value;
}
void AtomicWrite(const std::filesystem::path& path, const std::string& value) {
  Require(value.size() <= kMaximumFileBytes, "persisted value limit exceeded");
  // 先读取旧文件检查链接与大小；损坏密文的解密由上层完成，失败时不覆盖。
  ReadFile(path);
  auto pending = path; pending += L".pending";
  HANDLE file = CreateFileW(pending.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
  Require(file != INVALID_HANDLE_VALUE, "pending file exists or cannot be created; preserved for diagnosis");
  DWORD written = 0;
  const bool ok = WriteFile(file, value.data(), static_cast<DWORD>(value.size()), &written, nullptr) && written == value.size() && FlushFileBuffers(file);
  CloseHandle(file);
  Require(ok, "durable file write failed; pending file preserved");
  Require(MoveFileExW(pending.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE, "atomic file publish failed; pending file preserved");
  Require(ReadFile(path) == std::optional<std::string>(value), "durable readback differs");
}
std::string Crypt(const std::string& value, const std::string& key, bool protect) {
  // 预留 DPAPI 包装开销，不能接受写入后超出解密读取上限的明文。
  Require(!value.empty() && value.size() <= (protect ? 60000u : 65536u), "credential size invalid");
  DATA_BLOB source{static_cast<DWORD>(value.size()), reinterpret_cast<BYTE*>(const_cast<char*>(value.data()))};
  DATA_BLOB entropy{static_cast<DWORD>(key.size()), reinterpret_cast<BYTE*>(const_cast<char*>(key.data()))}, output{};
  BOOL ok = protect ? CryptProtectData(&source, L"Mystia companion", &entropy, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output)
                    : CryptUnprotectData(&source, nullptr, &entropy, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output);
  Require(ok != FALSE, "credential protection failed; original data preserved");
  std::string result(reinterpret_cast<char*>(output.pbData), output.cbData);
  SecureZeroMemory(output.pbData, output.cbData); LocalFree(output.pbData); return result;
}
}

int StartCompanionControl(const std::vector<std::string>& arguments) {
  try {
    // 必须早于端口握手/绑定：不完整命令行既不能创建主实例，也不能通知旧实例。
    ParseConnectionArguments(arguments);
    for (const auto& arg : arguments) {
      if (arg.rfind("--control-port=", 0) == 0) {
        size_t count = 0; const auto port = std::stoul(arg.substr(15), &count);
        Require(count == arg.size() - 15 && port >= 1024 && port <= 65535, "control port invalid");
        control_port = static_cast<unsigned short>(port);
      }
      if (arg.rfind("--isolation-id=", 0) == 0) {
        isolation_id = arg.substr(15);
        Require(isolation_id.size() >= 8 && isolation_id.size() <= 64 && std::all_of(isolation_id.begin(), isolation_id.end(), [](char ch) {
          return (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-';
        }), "isolation name invalid");
      }
    }
    Require((control_port == kControlPort) == isolation_id.empty(), "isolated control and storage must be selected together");
    launch_arguments = arguments;
  } catch (const std::exception&) { return 2; }
  WSADATA data{}; if (WSAStartup(MAKEWORD(2, 2), &data) != 0) return 2;
  listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP); if (listener == INVALID_SOCKET) return 2;
  BOOL exclusive = TRUE; setsockopt(listener, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<char*>(&exclusive), sizeof(exclusive));
  sockaddr_in address{}; address.sin_family = AF_INET; address.sin_port = htons(control_port); address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  if (bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) {
    closesocket(listener); listener = INVALID_SOCKET;
    int result = 2;
    HANDLE process = nullptr;
    try {
      const auto identity = ParseIdentity(ExchangeControl(address, "mystia-companion-v2:identity\n"));
      process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, identity.first);
      Require(process && CreationTime(process) == identity.second, "control process identity mismatch");
      wchar_t current_path[32768]{}, target_path[32768]{}; DWORD target_length = 32768;
      Require(GetModuleFileNameW(nullptr, current_path, 32768) > 0 && QueryFullProcessImageNameW(process, 0, target_path, &target_length), "control process path unavailable");
      Require(_wcsicmp(current_path, target_path) == 0, "control process path mismatch");
      AllowSetForegroundWindow(identity.first);
      std::string request = "mystia-companion-v2:show\n";
      for (const auto& arg : arguments) if (arg.rfind("--api=", 0) == 0 || arg.rfind("--token=", 0) == 0 || arg.rfind("--game-pid=", 0) == 0) request += arg + "\n";
      if (ValidCommand(request) && ParseIdentity(ExchangeControl(address, request)) == identity) result = 1;
    } catch (const std::exception&) { /* 身份不匹配或 ACK 无效时不启动第二窗口、不泄露凭据。 */ }
    if (process) CloseHandle(process);
    WSACleanup(); return result;
  }
  if (listen(listener, 8) == SOCKET_ERROR) { closesocket(listener); WSACleanup(); return 2; }
  stopping = false; control_thread = std::thread(ServeControl); return 0;
}
void StopCompanionControl() {
  stopping = true; control_window = nullptr;
  if (control_thread.joinable()) control_thread.join();
  if (listener != INVALID_SOCKET) closesocket(listener);
  listener = INVALID_SOCKET; WSACleanup();
}

struct CompanionPlatformBridge::Impl {
  HWND top, child; flutter::FlutterViewController* controller;
  PlatformLaunch launch{true}; int64_t activation = 0;
  bool passthrough = false, keep_visible = false, hotkey = false, tray = false;
  bool right_stick_held = true, restore_maximized = false; uint64_t last_switch = 0; int64_t cooldown = 800;
  DWORD last_poll_controller = MAXDWORD, last_toggle_controller = MAXDWORD;
  // 系统合成重建发生在Win32回调中，错误通过下一次平台轮询交给Dart显示，不能跨ABI抛出。
  std::optional<std::string> composition_error;
  bool dark_mode = false;
  FocusTransition focus_transition;

  /// 使用系统非客户区绘制，仅同步项目暖纸/深棕配色；保留原生标题栏的
  /// DPI、辅助功能、贴靠、系统菜单与最小化/最大化/关闭行为。
  /// 色值与 steward_theme.dart 的 background/foreground/border 对应。
  /// 标题栏保持不透明，避免背景透明设置削弱恢复窗口所需的系统按钮。
  void ApplyCaptionTheme() {
    HIGHCONTRASTW contrast{}; contrast.cbSize = sizeof(contrast);
    const bool high_contrast = SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0)
      && (contrast.dwFlags & HCF_HIGHCONTRASTON);
    const BOOL dark = !high_contrast && dark_mode;
    DwmSetWindowAttribute(top, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, &dark, sizeof(dark));
    const COLORREF background = high_contrast ? 0xffffffff : (dark_mode ? RGB(30, 29, 27) : RGB(244, 241, 233));
    const COLORREF foreground = high_contrast ? 0xffffffff : (dark_mode ? RGB(240, 233, 223) : RGB(52, 47, 41));
    const COLORREF border = high_contrast ? 0xffffffff : (dark_mode ? RGB(85, 75, 64) : RGB(216, 206, 189));
    // 35/36/34 分别是 Windows 11 的标题底色、标题文字、外边框属性。
    // 较早的 Windows 不支持时仍由系统绘制；纯外观失败不能阻止 F10 恢复输入。
    // 官方契约：https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
    DwmSetWindowAttribute(top, 35, &background, sizeof(background));
    DwmSetWindowAttribute(top, 36, &foreground, sizeof(foreground));
    DwmSetWindowAttribute(top, 34, &border, sizeof(border));
    RedrawWindow(top, nullptr, nullptr, RDW_FRAME | RDW_INVALIDATE);
  }
  HANDLE game = nullptr; DWORD game_pid = 0;
  NOTIFYICONDATAW icon{}; UINT taskbar_created = RegisterWindowMessageW(L"TaskbarCreated");
  Impl(HWND window, flutter::FlutterViewController* view) : top(window), child(view->view()->GetNativeWindow()), controller(view) {
    ApplyArguments(launch_arguments);
    SetPassThrough(false);
    hotkey = RegisterHotKey(top, kHotkey, MOD_NOREPEAT, VK_F10) != FALSE;
    Require(SetWindowSubclass(child, ChildProc, kTimer, reinterpret_cast<DWORD_PTR>(this)) != FALSE, "input subclass failed");
    SetTimer(top, kTimer, 40, nullptr);
    icon.cbSize = sizeof(icon); icon.hWnd = top; icon.uID = 1;
    icon.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP; icon.uCallbackMessage = kTrayMessage;
    icon.hIcon = LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(101));
    wcscpy_s(icon.szTip, L"夜雀管家"); AddTray();
    RestoreGeometry();
    ApplyCaptionTheme();
    control_window = top;
  }
  ~Impl() {
    try { SaveGeometry(); } catch (const std::exception&) { /* 退出不删除坏数据；下次显式存储操作仍会报告错误。 */ }
    control_window = nullptr; KillTimer(top, kTimer); RemoveWindowSubclass(child, ChildProc, kTimer);
    if (hotkey) UnregisterHotKey(top, kHotkey);
    if (tray) Shell_NotifyIconW(NIM_DELETE, &icon);
    if (game) CloseHandle(game);
  }
  void AddTray() { tray = Shell_NotifyIconW(NIM_ADD, &icon) != FALSE; if (tray) { icon.uVersion = NOTIFYICON_VERSION_4; Shell_NotifyIconW(NIM_SETVERSION, &icon); } }
  void SaveGeometry() {
    WINDOWPLACEMENT placement{}; placement.length = sizeof(placement);
    Require(GetWindowPlacement(top, &placement) != FALSE, "window placement unavailable");
    const auto& rect = placement.rcNormalPosition;
    // 仅保存普通尺寸、位置、最大化和DPI，不保存隐藏或最小化状态。
    std::ostringstream output;
    output << 1 << ' ' << rect.left << ' ' << rect.top << ' ' << rect.right - rect.left << ' ' << rect.bottom - rect.top << ' '
      << (placement.showCmd == SW_SHOWMAXIMIZED ? 1 : 0) << ' ' << GetDpiForWindow(top);
    AtomicWrite(StorageRoot() / L"window-state-v1.txt", output.str());
  }
  void RestoreGeometry() {
    auto stored = ReadFile(StorageRoot() / L"window-state-v1.txt"); if (!stored) return;
    int schema = 0, x = 0, y = 0, width = 0, height = 0, maximized = 0, dpi = 0;
    std::istringstream input(*stored); std::string excess;
    Require(static_cast<bool>(input >> schema >> x >> y >> width >> height >> maximized >> dpi) && !(input >> excess), "window geometry invalid; original preserved");
    Require(schema == 1 && width >= 100 && width <= 20000 && height >= 100 && height <= 20000 && dpi >= 48 && dpi <= 768, "window geometry outside bounds");
    POINT origin{x, y}; MONITORINFO monitor{}; monitor.cbSize = sizeof(monitor);
    Require(GetMonitorInfoW(MonitorFromPoint(origin, MONITOR_DEFAULTTONEAREST), &monitor) != FALSE, "monitor unavailable");
    const auto& work = monitor.rcWork;
    const int current_dpi = static_cast<int>(GetDpiForWindow(top));
    width = std::clamp(MulDiv(width, current_dpi, dpi), std::min(640, static_cast<int>(work.right - work.left)), static_cast<int>(work.right - work.left));
    height = std::clamp(MulDiv(height, current_dpi, dpi), std::min(480, static_cast<int>(work.bottom - work.top)), static_cast<int>(work.bottom - work.top));
    x = std::clamp(x, static_cast<int>(work.left), static_cast<int>(work.right) - width);
    y = std::clamp(y, static_cast<int>(work.top), static_cast<int>(work.bottom) - height);
    SetWindowPos(top, nullptr, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    restore_maximized = maximized == 1;
  }
  void ApplyArguments(const std::vector<std::string>& arguments) {
    const auto connection = ParseConnectionArguments(arguments);
    if (connection.endpoint) {
      // 先在临时值上构造完整pair，再一次提交；无凭据show/toggle完全保留旧值。
      // 显式空Token也必须覆盖旧Token，绝不能把空字符串当成“本次未提供”。
      auto next = launch;
      next.set_endpoint(*connection.endpoint);
      next.set_token(*connection.token);
      launch = std::move(next);
    }
    for (const auto& arg : arguments) ApplyGameArgument(arg);
  }
  void ApplyGameArgument(const std::string& arg) {
    if (arg.rfind("--game-pid=", 0) == 0) {
      try {
        size_t end = 0; auto parsed = std::stoul(arg.substr(11), &end);
        if (end != arg.size() - 11 || parsed == 0 || parsed > MAXDWORD) return;
        // 保留进程句柄，而非每次按 PID 重新打开，避免 PID 重用后误控新进程。
        if (game_pid == parsed && game) return;
        HANDLE next = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, static_cast<DWORD>(parsed));
        if (!next) return;
        focus_transition.Cancel();
        if (game) CloseHandle(game); game = next; game_pid = static_cast<DWORD>(parsed); launch.set_game_pid(static_cast<int64_t>(parsed));
      } catch (const std::exception&) { /* 非法输入不猜测目标，也不改变既有绑定。 */ }
    }
  }
  void ConfigureTransparency() {
    // Flutter Windows EGL配置保留8位alpha，但延伸窗框本身不会启用DWM逐像素合成。
    // 不覆盖客户区的屏外blur区域只启用窗口alpha；Win8及之后没有旧式玻璃模糊，不能宣传为Acrylic。
    // 官方契约：https://learn.microsoft.com/windows/win32/api/dwmapi/nf-dwmapi-dwmenableblurbehindwindow
    // 已通过真实Flutter红色色块alpha=0/64/128/255叠加黑/白/绿背景板验证。
    // 保留系统标题栏的不透明绘制；负边距会把玻璃效果扩展到caption，造成标题难辨。
    // 客户区透明由下面的DWM alpha合成负责，不依赖整窗玻璃扩展。
    MARGINS margins{0, 0, 0, 0};
    Require(SUCCEEDED(DwmExtendFrameIntoClientArea(top, &margins)), "DWM frame extension failed");
    HRGN region = CreateRectRgn(-1, -1, 0, 0);
    Require(region != nullptr, "DWM transparency region allocation failed");
    DWM_BLURBEHIND blur{};
    blur.dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION;
    blur.fEnable = TRUE;
    blur.hRgnBlur = region;
    const HRESULT result = DwmEnableBlurBehindWindow(top, &blur);
    // DWM在调用中复制region，调用返回后即可释放；失败分支同样不能泄漏GDI对象。
    DeleteObject(region);
    Require(SUCCEEDED(result), "DWM per-pixel transparency composition failed");
    composition_error.reset();
  }
  void SetPassThrough(bool enabled) {
    Require(!enabled || hotkey, "F10 recovery unavailable; click-through refused");
    LONG_PTR style = GetWindowLongPtrW(top, GWL_EXSTYLE);
    style = (style | WS_EX_LAYERED) & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    if (enabled) style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    SetLastError(0); auto old = SetWindowLongPtrW(top, GWL_EXSTYLE, style);
    Require(old != 0 || GetLastError() == 0, "window input style failed");
    // 顶层样式一旦写入，子窗口命中逻辑必须同步切换。尤其是F10恢复时，
    // 后续DWM配置即使失败，也不能遗留passthrough=true继续拒绝客户区交互。
    passthrough = enabled;
    Require(SetWindowPos(top, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED) != FALSE, "window input commit failed");
    Require(SetLayeredWindowAttributes(top, 0, 255, LWA_ALPHA) != FALSE, "window alpha initialization failed");
    // 全窗alpha保持255，背景和内容的独立不透明度由Flutter实际绘制的像素决定。
    // 穿透/恢复会重设layered属性，因此必须同时重新确认DWM逐像素合成，避免操作后回到黑底。
    try {
      ConfigureTransparency();
    } catch (const std::exception& error) {
      // 显式平台调用仍收到失败，F10/托盘等系统回调则由后续轮询显示一次错误。
      // 此时交互状态已提交；报告合成问题不能反向锁住已经恢复的鼠标输入。
      composition_error = error.what();
      throw;
    }
  }
  bool Show() {
    focus_transition.Cancel();
    SetPassThrough(false); ShowWindow(top, IsIconic(top) ? SW_RESTORE : SW_SHOW);
    const bool accepted = SetForegroundWindow(top) != FALSE;
    if (GetForegroundWindow() == top) SetFocus(child);
    return IsWindowVisible(top) && (accepted || GetForegroundWindow() == top);
  }
  static BOOL CALLBACK FindGame(HWND window, LPARAM data) {
    auto* pair = reinterpret_cast<std::pair<DWORD, HWND>*>(data); DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
    if (pid == pair->first && IsWindowVisible(window) && GetWindow(window, GW_OWNER) == nullptr) { pair->second = window; return FALSE; }
    return TRUE;
  }
  bool Toggle() {
    // 冷却期内重复点击属于已处理的重复输入，不能被按钮误报为系统拒绝。
    if (last_switch != 0 && GetTickCount64() - last_switch < static_cast<uint64_t>(cooldown)) return true;
    bool applied = false;
    if (GetForegroundWindow() == top) {
      if (!game || WaitForSingleObject(game, 0) != WAIT_TIMEOUT) return false;
      std::pair<DWORD, HWND> target{game_pid, nullptr}; EnumWindows(FindGame, reinterpret_cast<LPARAM>(&target));
      if (!target.second) return false;
      if (IsIconic(target.second)) ShowWindowAsync(target.second, SW_RESTORE);
      // SetForegroundWindow 成功表示请求被系统接受，跨进程激活仍需目标处理消息。
      // 原来紧接着比较 GetForegroundWindow 会产生假失败；F8 忽略 bool，按钮却会报错。
      // 不使用 AttachThreadInput、模拟按键或等待游戏线程；由已有 40ms 轮询确认后再隐藏。
      // 契约：https://devblogs.microsoft.com/oldnewthing/20161118-00/?p=94745
      applied = focus_transition.Begin(SetForegroundWindow(target.second) != FALSE, GetTickCount64());
    } else { applied = Show(); }
    if (applied) last_switch = GetTickCount64();
    return applied;
  }
  void ObserveFocusTransition() {
    DWORD foreground_pid = 0;
    const HWND foreground = GetForegroundWindow();
    GetWindowThreadProcessId(foreground, &foreground_pid);
    if (focus_transition.Observe(GetTickCount64(), game && WaitForSingleObject(game, 0) == WAIT_TIMEOUT,
          foreground_pid == game_pid, foreground == top || foreground == nullptr) && !keep_visible) {
      ShowWindow(top, SW_HIDE);
    }
  }
  void Drain() {
    std::deque<std::string> pending; { std::lock_guard<std::mutex> guard(control_mutex); pending.swap(commands); }
    for (const auto& request : pending) {
      std::istringstream input(request); std::string action, line; std::getline(input, action);
      std::vector<std::string> arguments;
      while (std::getline(input, line)) if (!line.empty()) arguments.push_back(line);
      ApplyArguments(arguments);
      ++activation;
      if (action == "mystia-companion-v2:exit") PostQuitMessage(0);
      else if (action == "mystia-companion-v2:show") Show();
      else if (action == "mystia-companion-v2:toggle") Toggle();
    }
  }
  static LRESULT CALLBACK ChildProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR data) {
    auto* self = reinterpret_cast<Impl*>(data);
    if (message == WM_KEYDOWN && wparam == VK_F8 && !(lparam & (1LL << 30))) { self->Toggle(); return 0; }
    if (self->passthrough && message == WM_NCHITTEST) return HTTRANSPARENT;
    if (self->passthrough && message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    return DefSubclassProc(window, message, wparam, lparam);
  }
};

CompanionPlatformBridge::CompanionPlatformBridge(HWND top, flutter::FlutterViewController* controller) : impl_(std::make_unique<Impl>(top, controller)) {
  PlatformHostApi::SetUp(controller->engine()->messenger(), this);
}
CompanionPlatformBridge::~CompanionPlatformBridge() { PlatformHostApi::SetUp(impl_->controller->engine()->messenger(), nullptr); }
std::optional<LRESULT> CompanionPlatformBridge::HandleMessage(UINT message, WPARAM wparam, LPARAM lparam) {
  auto& p = *impl_;
  if (message == WM_DWMCOMPOSITIONCHANGED) {
    try {
      p.ConfigureTransparency();
      p.ApplyCaptionTheme();
      p.controller->ForceRedraw();
    } catch (const std::exception& error) {
      p.composition_error = error.what();
    }
    // 仍交给Flutter处理合成变化和交换间隔；不能吞掉引擎需要接收的系统消息。
    return std::nullopt;
  }
  try {
    if (message == WM_THEMECHANGED || message == WM_SETTINGCHANGE || message == WM_DWMCOLORIZATIONCOLORCHANGED) {
      // 系统主题/对比度改变后重施当前应用主题，不能被 runner 的系统明暗覆盖。
      p.ApplyCaptionTheme();
      if (message == WM_DWMCOLORIZATIONCOLORCHANGED) return 0;
    }
    if (message == WM_CLOSE) { ShowWindow(p.top, SW_HIDE); return 0; }
    if (message == WM_SHOWWINDOW && wparam && p.restore_maximized) { p.restore_maximized = false; ShowWindow(p.top, SW_MAXIMIZE); }
    if (message == WM_EXITSIZEMOVE) { p.SaveGeometry(); }
    if (message == WM_GETMINMAXINFO) { auto* info = reinterpret_cast<MINMAXINFO*>(lparam); UINT dpi = GetDpiForWindow(p.top); info->ptMinTrackSize = {MulDiv(640, static_cast<int>(dpi), 96), MulDiv(480, static_cast<int>(dpi), 96)}; return 0; }
    if (message == kControlMessage) { p.Drain(); return 0; }
    if (message == WM_TIMER && wparam == kTimer) {
      p.Drain();
      p.ObserveFocusTransition();
      if (p.game && WaitForSingleObject(p.game, 0) == WAIT_OBJECT_0) PostQuitMessage(0);
      XINPUT_STATE state{}; bool connected = false;
      DWORD current_controller = MAXDWORD;
      for (DWORD index = 0; index < XUSER_MAX_COUNT; ++index) if (XInputGetState(index, &state) == ERROR_SUCCESS) { connected = true; current_controller = index; break; }
      if (current_controller != p.last_toggle_controller) { p.right_stick_held = true; p.last_toggle_controller = current_controller; }
      bool pressed = connected && (state.Gamepad.wButtons & XINPUT_GAMEPAD_RIGHT_THUMB);
      if (GetForegroundWindow() != p.top) p.right_stick_held = true;
      else { if (pressed && !p.right_stick_held) p.Toggle(); p.right_stick_held = pressed; }
      return 0;
    }
    if (message == WM_HOTKEY && wparam == kHotkey) { p.SetPassThrough(false); p.Show(); return 0; }
    if (message == p.taskbar_created) { p.AddTray(); return 0; }
    if (message == kTrayMessage) {
      UINT event = LOWORD(lparam);
      if (event == NIN_SELECT || event == WM_LBUTTONUP) p.Show();
      if (event == WM_CONTEXTMENU || event == WM_RBUTTONUP) {
        HMENU menu = CreatePopupMenu(); AppendMenuW(menu, MF_STRING, 1, L"显示夜雀管家"); AppendMenuW(menu, MF_STRING, 2, L"恢复鼠标交互"); AppendMenuW(menu, MF_STRING, 3, L"退出");
        POINT cursor{}; GetCursorPos(&cursor); SetForegroundWindow(p.top);
        UINT item = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, cursor.x, cursor.y, 0, p.top, nullptr); DestroyMenu(menu);
        if (item == 1 || item == 2) p.Show(); else if (item == 3) PostQuitMessage(0);
      }
      return 0;
    }
  } catch (const std::exception&) {
    // 系统回调不能跨 ABI 抛异常。恢复交互并让下一次显式 API 调用返回错误。
    ShowWindow(p.top, SW_SHOW);
  }
  return std::nullopt;
}
ErrorOr<PlatformLaunch> CompanionPlatformBridge::Initialize() { return impl_->launch; }
std::optional<FlutterError> CompanionPlatformBridge::ApplyWindowPreferences(const WindowPreferences& preferences) {
  try {
    Require(preferences.switch_cooldown_ms() >= 250 && preferences.switch_cooldown_ms() <= 2000, "invalid switch cooldown");
    if (impl_->dark_mode != preferences.dark_mode()) {
      impl_->dark_mode = preferences.dark_mode();
      impl_->ApplyCaptionTheme();
    }
    impl_->SetPassThrough(preferences.mouse_passthrough());
    Require(SetWindowPos(impl_->top, preferences.always_on_top() ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) != FALSE, "topmost update failed");
    impl_->keep_visible = preferences.keep_visible(); impl_->cooldown = preferences.switch_cooldown_ms(); return std::nullopt;
  } catch (const std::exception& error) { return Error(error); }
}
ErrorOr<bool> CompanionPlatformBridge::PerformWindowAction(const mystia_platform::WindowAction& action) {
  try {
    switch (action) {
      case WindowAction::kShow: return impl_->Show();
      case WindowAction::kHide: ShowWindow(impl_->top, SW_HIDE); return !IsWindowVisible(impl_->top);
      case WindowAction::kToggleFocus: return impl_->Toggle();
      case WindowAction::kExit: PostQuitMessage(0); return true;
    }
    return false;
  } catch (const std::exception& error) { return Error(error); }
}
ErrorOr<bool> CompanionPlatformBridge::OpenExternalUrl(const std::string& url) {
  try {
    // Dart 的检查不是原生权限边界；完整匹配 ASCII 发布页路径后才交给系统浏览器。
    // 不接受查询、用户信息、文件路径、任意协议或重定向参数。
    static const std::regex allowed(R"(^https://github\.com/blockshy/mystia-steward-companion/releases(/latest|/tag/[A-Za-z0-9._-]+)?$)");
    Require(std::regex_match(url, allowed), "only project release pages may be opened");
    const std::wstring target(url.begin(), url.end());
    const auto result = ShellExecuteW(impl_->top, L"open", target.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    return reinterpret_cast<INT_PTR>(result) > 32;
  } catch (const std::exception& error) { return Error(error); }
}
ErrorOr<PlatformInput> CompanionPlatformBridge::PollInput() {
  if (impl_->composition_error) {
    // 一次错误通知足够；恢复后的输入轮询继续工作，不以每40ms重复异常淹没前端。
    const auto message = *impl_->composition_error;
    impl_->composition_error.reset();
    return Error(std::runtime_error(message));
  }
  XINPUT_STATE state{}; bool connected = false;
  DWORD current_controller = MAXDWORD;
  for (DWORD index = 0; index < XUSER_MAX_COUNT; ++index) if (XInputGetState(index, &state) == ERROR_SUCCESS) { connected = true; current_controller = index; break; }
  // 多控制器之间切换时插入一次断开状态，Dart 必须重新等待回中，不能继承上一只手柄的长按。
  if (current_controller != impl_->last_poll_controller) { connected = false; impl_->last_poll_controller = current_controller; }
  return PlatformInput(GetForegroundWindow() == impl_->top, connected, state.Gamepad.wButtons,
    std::max(-1.0, state.Gamepad.sThumbLX / 32767.0), std::max(-1.0, state.Gamepad.sThumbLY / 32767.0),
    state.Gamepad.bLeftTrigger / 255.0, state.Gamepad.bRightTrigger / 255.0, impl_->activation, impl_->launch);
}
ErrorOr<std::optional<std::string>> CompanionPlatformBridge::ReadSecret(const std::string& key) {
  try { auto value = ReadFile(SecretPath(key)); if (!value) return std::optional<std::string>{}; return std::optional<std::string>(Crypt(*value, key, false)); }
  catch (const std::exception& error) { return Error(error); }
}
std::optional<FlutterError> CompanionPlatformBridge::WriteSecret(const std::string& key, const std::string& value) {
  try { auto path = SecretPath(key); if (auto prior = ReadFile(path)) Crypt(*prior, key, false); AtomicWrite(path, Crypt(value, key, true)); return std::nullopt; }
  catch (const std::exception& error) { return Error(error); }
}
std::optional<FlutterError> CompanionPlatformBridge::DeleteSecret(const std::string& key) {
  try { auto path = SecretPath(key); if (auto prior = ReadFile(path)) { Crypt(*prior, key, false); Require(DeleteFileW(path.c_str()) != FALSE, "credential delete failed"); } return std::nullopt; }
  catch (const std::exception& error) { return Error(error); }
}
ErrorOr<std::optional<std::string>> CompanionPlatformBridge::ReadPreferences() {
  try { return ReadFile(StorageRoot() / L"preferences.json"); } catch (const std::exception& error) { return Error(error); }
}
std::optional<FlutterError> CompanionPlatformBridge::WritePreferences(const std::string& json) {
  try { AtomicWrite(StorageRoot() / L"preferences.json", json); return std::nullopt; } catch (const std::exception& error) { return Error(error); }
}
