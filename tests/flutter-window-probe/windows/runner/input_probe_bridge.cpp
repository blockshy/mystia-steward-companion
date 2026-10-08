#include "input_probe_bridge.h"
#include "foreground_grant_bridge.h"

#include <bcrypt.h>
#include <commctrl.h>
#include <xinput.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <iomanip>
#include <iterator>
#include <limits>
#include <map>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <utility>

namespace {
using namespace mystia_input_probe;
constexpr UINT_PTR kSubclass = 0x495052;
constexpr UINT_PTR kFinishTimer = 0x495046;
constexpr wchar_t kExecutable[] = L"mystia-steward-companion-window-probe.exe";
constexpr wchar_t kGameExecutable[] = L"Touhou Mystia Izakaya.exe";
constexpr int kFocusKey = VK_F24;
thread_local InputProbeBridge* queued_bridge = nullptr;

class ProbeFailure : public std::runtime_error {
 public:
  ProbeFailure(const char* message, bool is_blocked = false)
      : std::runtime_error(message), blocked(is_blocked) {}
  bool blocked;
};
void Require(bool condition, const char* message) {
  if (!condition) throw ProbeFailure(message);
}
void Available(bool condition, const char* message) {
  if (!condition) throw ProbeFailure(message, true);
}
struct Handle {
  HANDLE value = nullptr;
  Handle() = default;
  explicit Handle(HANDLE next) : value(next) {}
  ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
  Handle(const Handle&) = delete;
  Handle& operator=(const Handle&) = delete;
  Handle(Handle&& other) noexcept : value(std::exchange(other.value, nullptr)) {}
  Handle& operator=(Handle&& other) noexcept {
    if (this != &other) {
      if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value);
      value = std::exchange(other.value, nullptr);
    }
    return *this;
  }
};
struct CloseReceipt {
  Handle game;
  const DWORD expected_pid, expected_sender_thread;
  const uint64_t expected_creation;
  const HWND expected_window;
  UINT callback_count = 0, callback_message = 0;
  HWND callback_window = nullptr;
  LRESULT callback_result = 0;
  DWORD callback_thread = 0, callback_pid = 0, callback_wait = WAIT_FAILED, callback_exit = 0, callback_error = 0;
  uint64_t callback_creation = 0, callback_ticks = 0;
  bool callback_creation_observed = false, callback_exit_observed = false;
  bool callback_target_matched = false, callback_identity_matched = false;

  CloseReceipt(HANDLE process, DWORD pid, uint64_t creation, HWND window)
      : expected_pid(pid), expected_sender_thread(GetCurrentThreadId()), expected_creation(creation), expected_window(window) {
    Require(DuplicateHandle(GetCurrentProcess(), process, GetCurrentProcess(), &game.value, 0, FALSE, DUPLICATE_SAME_ACCESS),
            "Cannot retain the exact game handle for the close callback.");
  }
};
// Registration, callback delivery and bridge destruction occur on the runner
// GUI thread. Userdata is a never-reused token, never an Impl pointer. Erasing
// registration makes a late callback harmless; the receipt owns its own HANDLE.
thread_local std::map<ULONG_PTR, std::shared_ptr<CloseReceipt>> close_receipts;
thread_local ULONG_PTR next_close_receipt_token = 0;
ULONG_PTR RegisterCloseReceipt(const std::shared_ptr<CloseReceipt>& receipt) {
  Require(next_close_receipt_token != std::numeric_limits<ULONG_PTR>::max(), "Close callback token space is exhausted.");
  const auto token = ++next_close_receipt_token;
  Require(close_receipts.emplace(token, receipt).second, "Close callback token cannot be reused.");
  return token;
}
void CALLBACK ReceiveGameClose(HWND window, UINT message, ULONG_PTR token, LRESULT result) noexcept {
  const auto found = close_receipts.find(token);
  if (found == close_receipts.end()) return;
  auto& receipt = *found->second;
  if (receipt.callback_count != std::numeric_limits<UINT>::max()) ++receipt.callback_count;
  if (receipt.callback_count != 1) return;  // Preserve the first immutable observation.
  receipt.callback_window = window; receipt.callback_message = message; receipt.callback_result = result;
  receipt.callback_thread = GetCurrentThreadId(); receipt.callback_ticks = GetTickCount64();
  receipt.callback_target_matched = window == receipt.expected_window && message == WM_CLOSE &&
      receipt.callback_thread == receipt.expected_sender_thread;
  receipt.callback_pid = GetProcessId(receipt.game.value);
  if (!receipt.callback_pid) receipt.callback_error = GetLastError();
  FILETIME created{}, exited{}, kernel{}, user{};
  receipt.callback_creation_observed = GetProcessTimes(receipt.game.value, &created, &exited, &kernel, &user) != FALSE;
  if (receipt.callback_creation_observed)
    receipt.callback_creation = (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
  else if (!receipt.callback_error) receipt.callback_error = GetLastError();
  receipt.callback_identity_matched = receipt.callback_pid == receipt.expected_pid && receipt.callback_creation_observed &&
      receipt.callback_creation == receipt.expected_creation;
  receipt.callback_wait = WaitForSingleObject(receipt.game.value, 0);
  if (receipt.callback_wait == WAIT_FAILED && !receipt.callback_error) receipt.callback_error = GetLastError();
  if (receipt.callback_wait == WAIT_OBJECT_0) {
    receipt.callback_exit_observed = GetExitCodeProcess(receipt.game.value, &receipt.callback_exit) != FALSE;
    if (!receipt.callback_exit_observed && !receipt.callback_error) receipt.callback_error = GetLastError();
  }
}
std::wstring Wide(const std::string& text) {
  Require(!text.empty() && text.size() <= 32768, "Invalid bounded UTF-8 string.");
  int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(),
                                static_cast<int>(text.size()), nullptr, 0);
  Require(count > 0, "Invalid UTF-8 string.");
  std::wstring result(static_cast<size_t>(count), L'\0');
  Require(MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(),
                             static_cast<int>(text.size()), result.data(), count) == count,
          "UTF-8 conversion failed.");
  return result;
}
std::string Utf8(const std::wstring& text) {
  Require(!text.empty() && text.size() <= 32768, "Invalid bounded UTF-16 string.");
  int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(),
                                 static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
  Require(count > 0, "Invalid UTF-16 string.");
  std::string result(static_cast<size_t>(count), '\0');
  Require(WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(),
                              static_cast<int>(text.size()), result.data(), count, nullptr, nullptr) == count,
          "UTF-16 conversion failed.");
  return result;
}
bool SamePath(const std::wstring& a, const std::wstring& b) { return _wcsicmp(a.c_str(), b.c_str()) == 0; }
std::wstring ModulePath(HMODULE module = nullptr) {
  std::wstring path(32768, L'\0');
  DWORD size = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
  Require(size > 0 && size < path.size(), "Cannot identify the loaded executable/module path.");
  path.resize(size); return path;
}
std::wstring ProcessPath(HANDLE process) {
  std::wstring path(32768, L'\0'); DWORD size = static_cast<DWORD>(path.size());
  Require(QueryFullProcessImageNameW(process, 0, path.data(), &size) != FALSE,
          "Cannot identify the retained game process path.");
  path.resize(size); return path;
}
void PlainPath(const std::wstring& path, bool directory, bool allow_absent = false) {
  Require(path.size() > 3 && path[1] == L':' && path[2] == L'\\' &&
              path.find(L"..") == std::wstring::npos && path.find(L'/') == std::wstring::npos &&
              path.find(L':', 2) == std::wstring::npos,
          "Expected a canonical local drive path without alternate streams.");
  for (size_t end = 3; end <= path.size(); ++end) {
    if (end != path.size() && path[end] != L'\\') continue;
    DWORD attributes = GetFileAttributesW(path.substr(0, end).c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
      Require(allow_absent && end == path.size() && GetLastError() == ERROR_FILE_NOT_FOUND,
              "Owned path is missing or inaccessible.");
      continue;
    }
    Require(!(attributes & FILE_ATTRIBUTE_REPARSE_POINT), "Owned path contains a reparse point.");
    Require(((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) == (end != path.size() || directory),
            "Owned path has an unexpected file type.");
  }
}
std::string Hex(uint64_t value) { std::ostringstream out; out << std::hex << value; return out.str(); }
bool HashText(const std::string& value) {
  return value.size() == 64 && std::all_of(value.begin(), value.end(), [](char c) {
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
  });
}
std::string Quote(const std::string& value) {
  std::ostringstream out; out << '"';
  for (unsigned char c : value) {
    if (c == '"' || c == '\\') out << '\\' << static_cast<char>(c);
    else if (c < 32) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(c) << std::dec;
    else out << static_cast<char>(c);
  }
  out << '"'; return out.str();
}
int64_t WindowValue(HWND window) { return static_cast<int64_t>(reinterpret_cast<intptr_t>(window)); }
DWORD WindowPid(HWND window) { DWORD pid = 0; GetWindowThreadProcessId(window, &pid); return pid; }
uint64_t Creation(HANDLE process) {
  FILETIME created{}, exited{}, kernel{}, user{};
  Require(GetProcessTimes(process, &created, &exited, &kernel, &user) != FALSE,
          "Cannot read retained game process creation time.");
  return (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
}
struct Invocation { std::string run, suite; std::wstring root, result; };
Invocation ParseInvocation(const std::vector<std::string>& args) {
  Require(args.size() == 7 && args[0] == "--probe" && args[1] == "--run-id" && args[3] == "--suite" &&
              (args[4] == "xinput" || args[4] == "focus") && args[5] == "--result-file",
          "Expected the fixed input probe invocation.");
  const auto& run = args[2];
  auto alnum = [](char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'); };
  Require(!run.empty() && run.size() <= 80 && alnum(run.front()) &&
              std::all_of(run.begin(), run.end(), [&](char c) { return alnum(c) || c == '_' || c == '-'; }),
          "Invalid fixed node run identifier.");
  auto root = L"D:\\dev\\mystia-node\\runs\\" + Wide(run);
  auto result = root + L"\\probe-result.json";
  auto received = Wide(args[6]); std::replace(received.begin(), received.end(), L'/', L'\\');
  Require(SamePath(received, result), "Result file is outside this exact node run.");
  PlainPath(root, true); PlainPath(root + L"\\payload", true);
  Require(SamePath(ModulePath(), root + L"\\payload\\" + kExecutable), "Executable is outside this exact run payload.");
  PlainPath(ModulePath(), false); PlainPath(result, false, true);
  Require(GetFileAttributesW(result.c_str()) == INVALID_FILE_ATTRIBUTES, "Probe result already exists; replay rejected.");
  return {run, args[4], root, result};
}

// This sidecar has thirteen flat ASCII fields. Do not silently accept duplicate
// JSON members, additional keys, nested values or alternative number spellings.
class SidecarParser {
 public:
  explicit SidecarParser(const std::string& source) : source_(source) {}
  std::map<std::string, std::string> Parse() {
    std::map<std::string, std::string> result;
    Take('{');
    for (;;) {
      auto key = String(); Take(':');
      std::string value;
      if (key == "schemaVersion") { Skip(); Require(Peek() == '2', "Unsupported sidecar schema version."); ++at_; value = "2"; }
      else value = String();
      Require(result.emplace(key, value).second, "Duplicate input sidecar member.");
      Skip(); if (Peek() == '}') { ++at_; break; } Take(',');
    }
    Skip(); Require(at_ == source_.size(), "Trailing bytes in input sidecar.");
    constexpr const char* keys[] = {"schemaVersion", "runId", "gitSha", "gameExecutable", "expectedExeSha256",
        "expectedUnityPlayerSha256", "expectedMetadataSha256", "expectedGameAssemblySha256",
        "steamAppId", "steamBuildId", "preparedEvidenceSha256", "expectedCooperatorSha256", "cooperatorBuildEvidenceSha256"};
    Require(result.size() == std::size(keys), "Input sidecar does not have the exact schema.");
    for (const auto* key : keys) Require(result.count(key) == 1, "Input sidecar is missing a required member.");
    return result;
  }
 private:
  const std::string& source_; size_t at_ = 0;
  char Peek() const { return at_ < source_.size() ? source_[at_] : '\0'; }
  void Skip() { while (at_ < source_.size() && (source_[at_] == ' ' || source_[at_] == '\t' || source_[at_] == '\r' || source_[at_] == '\n')) ++at_; }
  void Take(char c) { Skip(); Require(Peek() == c, "Malformed fixed input sidecar JSON."); ++at_; }
  std::string String() {
    Take('"'); std::string result;
    while (Peek() != '"') {
      unsigned char c = static_cast<unsigned char>(Peek());
      Require(c >= 32 && c <= 126 && result.size() < 1024, "Sidecar strings must be bounded ASCII."); ++at_;
      if (c == '\\') {
        char escaped = Peek(); ++at_;
        Require(escaped == '"' || escaped == '\\' || escaped == '/', "Unsupported escape in fixed ASCII sidecar field.");
        c = static_cast<unsigned char>(escaped);
      }
      result += static_cast<char>(c);
    }
    ++at_; return result;
  }
};
Handle OpenPinned(const std::wstring& path) {
  PlainPath(path, false);
  Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                          FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
  Require(file.value != INVALID_HANDLE_VALUE, "Cannot pin the owned regular file for read-only identity validation.");
  FILE_ATTRIBUTE_TAG_INFO info{};
  Require(GetFileType(file.value) == FILE_TYPE_DISK &&
              GetFileInformationByHandleEx(file.value, FileAttributeTagInfo, &info, sizeof(info)) &&
              !(info.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)),
          "Pinned file is not a regular non-reparse disk file.");
  std::wstring final_path(32768, L'\0');
  DWORD length = GetFinalPathNameByHandleW(file.value, final_path.data(), static_cast<DWORD>(final_path.size()), FILE_NAME_NORMALIZED);
  Require(length > 4 && length < final_path.size(), "Cannot identify the pinned file final path.");
  final_path.resize(length);
  Require(final_path.compare(0, 4, L"\\\\?\\") == 0 && SamePath(final_path.substr(4), path),
          "Pinned file final path differs from the fixed owned path.");
  return file;
}
uint64_t FileSize(HANDLE file) {
  LARGE_INTEGER size{}; Require(GetFileSizeEx(file, &size) && size.QuadPart >= 0, "Cannot read regular file length.");
  return static_cast<uint64_t>(size.QuadPart);
}
std::string ReadSmall(HANDLE file, uint64_t limit) {
  auto size = FileSize(file); Require(size > 0 && size <= limit, "Fixed input file exceeds its size bound.");
  LARGE_INTEGER start{}; Require(SetFilePointerEx(file, start, nullptr, FILE_BEGIN), "Cannot rewind owned file.");
  std::string data(static_cast<size_t>(size), '\0'); DWORD read = 0;
  Require(ReadFile(file, data.data(), static_cast<DWORD>(data.size()), &read, nullptr) && read == data.size(),
          "Cannot read all fixed input file bytes.");
  return data;
}
std::string Sha256(HANDLE file) {
  Require(FileSize(file) > 0 && FileSize(file) <= 1024ULL * 1024 * 1024, "Identity file exceeds the one GiB bound.");
  LARGE_INTEGER start{}; Require(SetFilePointerEx(file, start, nullptr, FILE_BEGIN), "Cannot rewind identity file.");
  BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
  Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0,
          "Cannot open SHA-256 provider.");
  try {
    Require(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) == 0, "Cannot create SHA-256 hash.");
    std::array<unsigned char, 65536> buffer{}; DWORD read = 0;
    for (;;) {
      Require(ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr), "Cannot read pinned identity file.");
      if (!read) break;
      Require(BCryptHashData(hash, buffer.data(), read, 0) == 0, "Cannot hash pinned identity file.");
    }
    std::array<unsigned char, 32> digest{};
    Require(BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) == 0, "Cannot finish identity hash.");
    BCryptDestroyHash(hash); hash = nullptr; BCryptCloseAlgorithmProvider(algorithm, 0); algorithm = nullptr;
    std::ostringstream out; out << std::hex << std::setfill('0');
    for (unsigned char byte : digest) out << std::setw(2) << static_cast<unsigned int>(byte);
    return out.str();
  } catch (...) { if (hash) BCryptDestroyHash(hash); if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0); throw; }
}
void WriteNew(const std::wstring& path, const std::string& text, size_t limit) {
  Require(!text.empty() && text.size() <= limit, "Native output exceeds its byte bound.");
  PlainPath(path, false, true);
  Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                          FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
  Require(file.value != INVALID_HANDLE_VALUE, "Cannot exclusively create the fixed evidence file.");
  DWORD written = 0;
  Require(WriteFile(file.value, text.data(), static_cast<DWORD>(text.size()), &written, nullptr) &&
              written == text.size() && FlushFileBuffers(file.value), "Cannot persist complete native evidence.");
}
void DesktopGuard() {
  DWORD session = MAXDWORD;
  Available(ProcessIdToSessionId(GetCurrentProcessId(), &session) && session == WTSGetActiveConsoleSessionId(),
            "Probe is not on the active physical console.");
  HDESK desktop = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
  Available(desktop != nullptr, "Physical input desktop is unavailable.");
  wchar_t name[256]{}; DWORD needed = 0;
  BOOL read = GetUserObjectInformationW(desktop, UOI_NAME, name, sizeof(name), &needed); CloseDesktop(desktop);
  Available(read && wcscmp(name, L"Default") == 0, "Physical desktop changed or is locked.");
  Available(GetUserObjectInformationW(GetProcessWindowStation(), UOI_NAME, name, sizeof(name), &needed) &&
                wcscmp(name, L"WinSta0") == 0, "Process is not on the interactive window station.");
}
GUITHREADINFO ThreadInfo(DWORD thread) {
  GUITHREADINFO info{}; info.cbSize = sizeof(info);
  Require(thread != 0 && GetGUIThreadInfo(thread, &info), "Cannot inspect the bound GUI thread.");
  return info;
}
}  // namespace

struct InputProbeBridge::Impl {
  HWND top, child, game_window = nullptr;
  DWORD probe_thread, game_pid = 0, game_thread = 0;
  Invocation invocation;
  bool invocation_valid = false, subclassed = false, finishing = false, initialized = false;
  bool identity_matched = false, game_resumed = false, startup_terminated = false;
  bool parent_in_job = false, game_in_job = false, close_requested = false;
  bool pending = false, child_focus_requested = false, mode_pass = false;
  bool foreground_result = false, cursor_saved = false, cursor_injected = false, cursor_restored = false;
  DWORD foreground_error = 0, send_error = 0;
  UINT send_requested = 0, send_inserted = 0;
  uint32_t marker = 0;
  int64_t sequence = 0, request_id = 0, mouse_down = 0, mouse_up = 0, key_down = 0;
  int64_t expected_down = 0, expected_up = 0, expected_key = 0;
  uint64_t creation = 0, deadline = 0, finish_deadline = 0;
  uint64_t raw_down_extra = 0, raw_up_extra = 0, raw_key_extra = 0;
  LONG raw_down = 0, raw_up = 0, raw_key = 0;
  FocusOperation operation = FocusOperation::kInitialize;
  std::string initial_error, cleanup_error, game_exe_hash, unity_hash, metadata_hash, assembly_hash, prepared_hash;
  std::string cooperator_hash, cooperator_build_hash, probe_exe_hash, descriptor_hash, cooperator_evidence_hash;
  std::wstring game_path, game_root;
  Handle game;
  std::unique_ptr<ForegroundGrantBridge> foreground_grant;
  bool close_pipeline_started = false, cooperator_evidence_read = false, cooperator_evidence_verified = false;
  bool cleanup_close_attempted = false;
  HWND close_target = nullptr, close_foreground = nullptr, close_game_focus = nullptr;
  DWORD close_target_pid = 0, close_foreground_pid = 0, close_game_focus_pid = 0, close_send_error = 0;
  bool close_foreground_required = false, close_foreground_matched = false, close_game_focus_observed = false;
  bool close_send_result = false;
  UINT close_send_attempts = 0;
  ULONG_PTR close_receipt_token = 0;
  std::shared_ptr<CloseReceipt> close_receipt;
  uint64_t cooperator_close_deadline = 0;
  std::vector<Handle> pinned_files;
  HMODULE xinput = nullptr;
  using GetState = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);
  GetState get_state = nullptr;
  std::string xinput_path;
  LARGE_INTEGER frequency{}, started{};
  POINT original_cursor{}, last_cursor{};
  int finish_code = 1;
  std::function<void(std::optional<FlutterError>)> finish_reply;

  Impl(HWND owner, flutter::FlutterViewController* controller, const std::vector<std::string>& args)
      : top(owner), child(controller->view()->GetNativeWindow()), probe_thread(GetCurrentThreadId()) {
    try {
      invocation = ParseInvocation(args); invocation_valid = true;
      Require(IsWindow(top) && IsWindow(child) && WindowPid(top) == GetCurrentProcessId() &&
                  WindowPid(child) == GetCurrentProcessId() && GetAncestor(child, GA_ROOT) == top,
              "Input probe HWND ownership is invalid.");
      Require(QueryPerformanceFrequency(&frequency) && frequency.QuadPart > 0 && QueryPerformanceCounter(&started),
              "Monotonic clock is unavailable.");
      Require(BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&marker), sizeof(marker), BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0,
              "Cannot create input marker.");
      marker &= 0x7fffffff; Require(marker != 0, "Random input marker is zero.");
      Require(SetWindowSubclass(child, ChildProc, kSubclass, reinterpret_cast<DWORD_PTR>(this)),
              "Cannot observe the exact Flutter child input.");
      subclassed = true;
      ApplyMode(false);
    } catch (const std::exception& error) { initial_error = error.what(); }
  }
  ~Impl() {
    if (close_receipt_token) close_receipts.erase(close_receipt_token);
    KillTimer(top, kFinishTimer);
    if (subclassed && IsWindow(child)) RemoveWindowSubclass(child, ChildProc, kSubclass);
    foreground_grant.reset();
    try { RestoreCursor(); } catch (...) {}
    if (xinput) FreeLibrary(xinput);
    if (finish_reply) { auto reply = std::move(finish_reply); reply(FlutterError("closed", "Probe closed before cleanup completed.")); }
  }
  int64_t MonotonicMicros() const {
    LARGE_INTEGER now{}; Require(QueryPerformanceCounter(&now), "Cannot read monotonic clock.");
    const auto ticks = now.QuadPart - started.QuadPart;
    return (ticks / frequency.QuadPart) * 1000000 + (ticks % frequency.QuadPart) * 1000000 / frequency.QuadPart;
  }
  void OwnWindows() const {
    Require(IsWindow(top) && IsWindow(child) && WindowPid(top) == GetCurrentProcessId() &&
                WindowPid(child) == GetCurrentProcessId() && GetAncestor(child, GA_ROOT) == top &&
                GetWindowThreadProcessId(child, nullptr) == probe_thread,
            "Owned Flutter window identity changed.");
  }
  bool GameAlive() const {
    if (!game.value) return false;
    DWORD status = WaitForSingleObject(game.value, 0);
    Require(status == WAIT_TIMEOUT || status == WAIT_OBJECT_0, "Cannot inspect retained game process state.");
    return status == WAIT_TIMEOUT;
  }
  DWORD GameExitCode() const {
    Require(game.value && !GameAlive(), "Game has not exited on its retained handle.");
    DWORD code = 0; Require(GetExitCodeProcess(game.value, &code), "Cannot read retained game exit code."); return code;
  }
  bool ObservingGameExit() const {
    return close_requested && close_send_attempts == 1 && close_send_result;
  }
  void CheckGameIdentity() const {
    Require(game.value && identity_matched && GetProcessId(game.value) == game_pid && Creation(game.value) == creation,
            "Retained game process identity changed.");
  }
  void CheckGame() const {
    CheckGameIdentity();
    if (GameAlive()) Require(SamePath(ProcessPath(game.value), game_path), "Retained game image path changed.");
  }
  struct WindowCandidates { DWORD pid; std::vector<HWND> windows; };
  static BOOL CALLBACK Enumerate(HWND window, LPARAM value) {
    auto* found = reinterpret_cast<WindowCandidates*>(value);
    if (WindowPid(window) != found->pid || !IsWindowVisible(window) || GetWindow(window, GW_OWNER)) return TRUE;
    wchar_t name[128]{}; RECT client{};
    if (GetClassNameW(window, name, static_cast<int>(std::size(name))) && wcscmp(name, L"UnityWndClass") == 0 &&
        GetClientRect(window, &client) && client.right > client.left && client.bottom > client.top)
      found->windows.push_back(window);
    return TRUE;
  }
  std::vector<HWND> GameWindows() const {
    if (!game.value || !GameAlive()) return {};
    WindowCandidates candidates{game_pid, {}};
    Require(EnumWindows(Enumerate, reinterpret_cast<LPARAM>(&candidates)), "Cannot enumerate the exact game PID windows.");
    return candidates.windows;
  }
  void BoundGameWindow() const {
    Require(!ObservingGameExit(), "The game window cannot be targeted after its single accepted close request.");
    CheckGame(); Require(GameAlive(), "Bound game exited before the operation.");
    auto candidates = GameWindows();
    Require(candidates.size() == 1 && candidates[0] == game_window && WindowPid(game_window) == game_pid &&
                GetWindowThreadProcessId(game_window, nullptr) == game_thread,
            "Game window is absent, ambiguous or changed after binding.");
  }
  bool OwnedGameFocus(HWND focus) const {
    return focus && game_window && IsWindow(focus) && WindowPid(focus) == game_pid &&
        GetWindowThreadProcessId(focus, nullptr) == game_thread && GetAncestor(focus, GA_ROOT) == game_window;
  }
  void ApplyMode(bool pass) {
    OwnWindows();
    LONG_PTR style = GetWindowLongPtrW(top, GWL_EXSTYLE) | WS_EX_LAYERED;
    if (pass) style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    else style &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    SetLastError(ERROR_SUCCESS);
    LONG_PTR previous = SetWindowLongPtrW(top, GWL_EXSTYLE, style);
    Require(previous != 0 || GetLastError() == ERROR_SUCCESS, "Cannot set probe input mode.");
    Require(SetLayeredWindowAttributes(top, 0, 255, LWA_ALPHA), "Cannot preserve opaque focus fixture pixels.");
    constexpr LONG_PTR mask = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    Require((GetWindowLongPtrW(top, GWL_EXSTYLE) & mask) == (style & mask), "Probe input mode readback differs.");
    mode_pass = pass;
  }
  void LoadXInput() {
    if (xinput) return;
    wchar_t system[32768]{}; UINT size = GetSystemDirectoryW(system, static_cast<UINT>(std::size(system)));
    Require(size > 0 && size < std::size(system), "Cannot determine the Windows system DLL directory.");
    std::wstring path = std::wstring(system, size) + L"\\XInput1_4.dll";
    PlainPath(path, false);
    xinput = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    Available(xinput != nullptr, "The fixed system XInput1_4.dll is unavailable.");
    Require(SamePath(ModulePath(xinput), path), "Loaded XInput DLL is not the exact system path.");
    auto address = GetProcAddress(xinput, "XInputGetState");
    static_assert(sizeof(address) == sizeof(get_state));
    std::memcpy(&get_state, &address, sizeof(get_state));
    Require(get_state != nullptr, "System XInputGetState export is unavailable.");
    xinput_path = Utf8(ModulePath(xinput));
  }
  XInputSnapshot Sample() {
    Require(initial_error.empty() && !finishing && invocation.suite == "xinput", "XInput call is unavailable for this invocation.");
    DesktopGuard(); OwnWindows(); LoadXInput();
    flutter::EncodableList slots;
    for (DWORD index = 0; index < XUSER_MAX_COUNT; ++index) {
      XINPUT_STATE state{}; DWORD code = get_state(index, &state);
      XInputSlot slot(index, code);
      if (code == ERROR_SUCCESS) {
        const auto& p = state.Gamepad;
        slot.set_state(mystia_input_probe::XInputState(state.dwPacketNumber, p.wButtons, p.bLeftTrigger, p.bRightTrigger,
                                                      p.sThumbLX, p.sThumbLY, p.sThumbRX, p.sThumbRY));
      }
      slots.emplace_back(flutter::CustomEncodableValue(slot));
    }
    HWND foreground = GetForegroundWindow();
    return XInputSnapshot(MYSTIA_WINDOW_PROBE_GIT_SHA, GetCurrentProcessId(), ++sequence, MonotonicMicros(),
                          xinput_path, WindowPid(foreground), foreground == top && GetFocus() == child, slots);
  }
  void PinHash(const std::wstring& path, const std::string& expected, std::string& actual) {
    Require(HashText(expected), "Sidecar SHA-256 is not canonical lowercase hex.");
    auto file = OpenPinned(path); actual = Sha256(file.value);
    Require(actual == expected, "An owned game/preparation file SHA-256 differs from the sidecar.");
    pinned_files.push_back(std::move(file));
  }
  void StartGame() {
    Require(!game.value && !initialized, "Game initialization cannot be replayed.");
    initialized = true; DesktopGuard();
    auto sidecar = OpenPinned(invocation.root + L"\\input-probe.json");
    auto values = SidecarParser(ReadSmall(sidecar.value, 32768)).Parse();
    pinned_files.push_back(std::move(sidecar));
    Require(values.at("runId") == invocation.run && values.at("gitSha") == MYSTIA_WINDOW_PROBE_GIT_SHA &&
                values.at("steamAppId") == "1584090" && values.at("steamBuildId") == "23158340",
            "Input sidecar run, build or Steam identity differs.");
    game_root = invocation.root + L"\\workspace\\game"; game_path = game_root + L"\\" + kGameExecutable;
    auto supplied = Wide(values.at("gameExecutable")); std::replace(supplied.begin(), supplied.end(), L'/', L'\\');
    Require(SamePath(supplied, game_path), "Sidecar game executable is outside the fixed copied game directory.");
    PlainPath(game_root, true);
    PinHash(invocation.root + L"\\workspace\\prepared-evidence.json", values.at("preparedEvidenceSha256"), prepared_hash);
    PinHash(game_path, values.at("expectedExeSha256"), game_exe_hash);
    PinHash(game_root + L"\\UnityPlayer.dll", values.at("expectedUnityPlayerSha256"), unity_hash);
    PinHash(game_root + L"\\GameAssembly.dll", values.at("expectedGameAssemblySha256"), assembly_hash);
    PinHash(game_root + L"\\Touhou Mystia Izakaya_Data\\il2cpp_data\\Metadata\\global-metadata.dat",
            values.at("expectedMetadataSha256"), metadata_hash);
    PinHash(game_root + L"\\BepInEx\\plugins\\mystia-steward-companion-focus-probe\\MystiaStewardCompanion.FocusProbe.dll",
            values.at("expectedCooperatorSha256"), cooperator_hash);
    PinHash(invocation.root + L"\\workspace\\cooperator-build-evidence.json", values.at("cooperatorBuildEvidenceSha256"), cooperator_build_hash);
    auto probe_file = OpenPinned(ModulePath()); probe_exe_hash = Sha256(probe_file.value); pinned_files.push_back(std::move(probe_file));
    auto appid = OpenPinned(game_root + L"\\steam_appid.txt");
    const auto appid_text = ReadSmall(appid.value, 9);
    Require(appid_text == "1584090" || appid_text == "1584090\n" || appid_text == "1584090\r\n",
            "Copied game development App ID bytes differ.");
    pinned_files.push_back(std::move(appid));
    BOOL own_job = FALSE; Require(IsProcessInJob(GetCurrentProcess(), nullptr, &own_job) && own_job,
                                 "Focus probe must already be held by the node cleanup job."); parent_in_job = true;
    foreground_grant = std::make_unique<ForegroundGrantBridge>(invocation.run, top, probe_thread);
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION info{};
    std::wstring command = L"\"" + game_path + L"\"";
    Require(CreateProcessW(game_path.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED,
                           nullptr, game_root.c_str(), &startup, &info), "Cannot create the exact copied game suspended.");
    game = Handle(info.hProcess); Handle thread(info.hThread); game_pid = info.dwProcessId;
    try {
      creation = Creation(game.value);
      DWORD own_session = MAXDWORD, child_session = MAXDWORD; BOOL child_job = FALSE;
      Require(GetProcessId(game.value) == game_pid && SamePath(ProcessPath(game.value), game_path) &&
                  ProcessIdToSessionId(GetCurrentProcessId(), &own_session) && ProcessIdToSessionId(game_pid, &child_session) &&
                  own_session == child_session && IsProcessInJob(game.value, nullptr, &child_job) && child_job,
              "Suspended copied game path, session or inherited job identity differs.");
      game_in_job = true; identity_matched = true;
      foreground_grant->BindGame(game.value, game_pid, creation, game_path);
      std::ostringstream descriptor;
      descriptor << "{\"schemaVersion\":1,\"runId\":" << Quote(invocation.run) << ",\"gitSha\":\"" << MYSTIA_WINDOW_PROBE_GIT_SHA
          << "\",\"pipeName\":" << Quote(foreground_grant->pipe_name()) << ",\"nonceHex\":" << Quote(foreground_grant->nonce_hex())
          << ",\"gamePid\":" << Quote(std::to_string(game_pid)) << ",\"gameCreationHex\":" << Quote(Hex(creation))
          << ",\"probePid\":" << Quote(std::to_string(GetCurrentProcessId()))
          << ",\"probeCreationHex\":" << Quote(Hex(foreground_grant->probe_creation()))
          << ",\"probeHwnd\":" << Quote(std::to_string(WindowValue(top))) << ",\"probeThreadId\":" << Quote(std::to_string(probe_thread))
          << ",\"probeExeSha256\":" << Quote(probe_exe_hash) << "}\n";
      const auto descriptor_path = invocation.root + L"\\foreground-session.json";
      WriteNew(descriptor_path, descriptor.str(), 32768);
      auto descriptor_file = OpenPinned(descriptor_path); descriptor_hash = Sha256(descriptor_file.value);
      pinned_files.push_back(std::move(descriptor_file));
      Require(ResumeThread(thread.value) == 1, "Cannot resume the single suspended copied game thread.");
      game_resumed = true;
    } catch (...) {
      // Only the process created suspended by this call can be terminated here;
      // no Unity code has run. A running game is closed solely through its HWND.
      if (!game_resumed) startup_terminated = TerminateProcess(game.value, 1) != FALSE;
      throw;
    }
    pending = true; deadline = GetTickCount64() + 120000;
  }
  void IdleInput() const {
    for (int key : {VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2,
                    VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN, kFocusKey})
      Available((GetAsyncKeyState(key) & 0x8000) == 0, "A physical key/button is held; input refused.");
    auto own = ThreadInfo(probe_thread);
    Available(!own.hwndCapture && !(own.flags & (GUI_INMENUMODE | GUI_INMOVESIZE)), "Probe capture/menu/drag is active.");
    if (game_window && GameAlive()) {
      auto other = ThreadInfo(game_thread);
      Available(!other.hwndCapture && !(other.flags & (GUI_INMENUMODE | GUI_INMOVESIZE)), "Game capture/menu/drag is active.");
    }
  }
  void PairForeground() const {
    HWND foreground = GetForegroundWindow();
    Available((foreground == top && WindowPid(foreground) == GetCurrentProcessId()) ||
                  (foreground == game_window && game_window && WindowPid(foreground) == game_pid),
              "An unrelated window owns foreground; operation refused.");
  }
  void Focus(bool probe) {
    DesktopGuard(); OwnWindows(); BoundGameWindow(); IdleInput(); PairForeground();
    if (probe) {
      Available(GetForegroundWindow() == game_window && OwnedGameFocus(ThreadInfo(game_thread).hwndFocus),
                "A fresh grant requires the exact game to still own actual foreground/focus.");
      Require(foreground_grant != nullptr, "Foreground cooperation is unavailable; no unauthorised fallback is allowed.");
      ApplyMode(false);
      foreground_result = false; foreground_error = 0; child_focus_requested = false;
      foreground_grant->Request(static_cast<uint64_t>(request_id), game_window, game_thread);
      pending = true; deadline = GetTickCount64() + 5000; return;
    }
    if (IsIconic(game_window)) Require(ShowWindowAsync(game_window, SW_RESTORE), "Cannot request copied game restoration.");
    child_focus_requested = false; SetLastError(ERROR_SUCCESS);
    foreground_result = SetForegroundWindow(game_window) != FALSE; foreground_error = GetLastError();
    pending = true; deadline = GetTickCount64() + 5000;
  }
  void ActivateGrantedProbe() {
    const auto& grant = foreground_grant->observation();
    Require(grant.ready && grant.identity_matched && grant.request_id == static_cast<uint64_t>(request_id) &&
                grant.response_sequence == grant.sequence, "The foreground reply is not for this exact pending action.");
    Available(grant.attempted && grant.succeeded && grant.foreground_hwnd == static_cast<uint64_t>(WindowValue(game_window)) &&
                  grant.foreground_after_hwnd == static_cast<uint64_t>(WindowValue(game_window)) &&
                  grant.foreground_pid == game_pid && grant.foreground_after_pid == game_pid,
              "Game foreground grant was denied or its actual foreground changed.");
    // The reply is historical evidence. Re-check the live desktop and exact
    // pair immediately before consuming it; never retry a withdrawn grant.
    ApplyMode(false); ShowWindow(top, SW_SHOWNOACTIVATE);
    DesktopGuard(); OwnWindows(); BoundGameWindow(); IdleInput();
    Available(GetForegroundWindow() == game_window && WindowPid(game_window) == game_pid &&
                  OwnedGameFocus(ThreadInfo(game_thread).hwndFocus),
              "Actual game foreground/focus changed while its grant was in flight.");
    foreground_grant->MarkActivationRequested();
    SetLastError(ERROR_SUCCESS); foreground_result = SetForegroundWindow(top) != FALSE; foreground_error = GetLastError();
  }
  void InputGuard() const {
    DesktopGuard(); OwnWindows(); BoundGameWindow(); IdleInput();
    Available(GetForegroundWindow() == top && GetFocus() == child && IsWindowVisible(top) && !mode_pass,
              "Exact visible interactive Flutter child does not own foreground/focus.");
  }
  void Send(std::vector<INPUT>& events) {
    InputGuard(); send_requested = static_cast<UINT>(events.size()); SetLastError(ERROR_SUCCESS);
    send_inserted = SendInput(send_requested, events.data(), sizeof(INPUT)); send_error = GetLastError();
    Require(send_inserted == send_requested, "SendInput inserted an incomplete fixed sequence; input will not be replayed.");
  }
  void Click() {
    InputGuard(); RECT area{}; Require(GetClientRect(child, &area) && area.right > 0 && area.bottom > 0, "Flutter client geometry unavailable.");
    POINT point{area.right / 2, area.bottom / 2}; Require(ClientToScreen(child, &point), "Cannot map the fixed client center.");
    HWND hit = WindowFromPoint(point);
    Available(hit == child && WindowPid(hit) == GetCurrentProcessId(), "Fixed Flutter click point is occluded or belongs to another window.");
    int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
    int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    Require(width > 1 && height > 1 && point.x >= x && point.x < x + width && point.y >= y && point.y < y + height,
            "Fixed click is outside the physical virtual desktop.");
    std::vector<INPUT> events(3);
    for (auto& event : events) { event.type = INPUT_MOUSE; event.mi.dwExtraInfo = marker; }
    events[0].mi.dx = static_cast<LONG>(std::llround(static_cast<double>(point.x - x) * 65535 / (width - 1)));
    events[0].mi.dy = static_cast<LONG>(std::llround(static_cast<double>(point.y - y) * 65535 / (height - 1)));
    events[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
    events[1].mi.dwFlags = MOUSEEVENTF_LEFTDOWN; events[2].mi.dwFlags = MOUSEEVENTF_LEFTUP;
    expected_down = mouse_down + 1; expected_up = mouse_up + 1;
    if (!cursor_saved) cursor_saved = GetCursorPos(&original_cursor) != FALSE;
    last_cursor = point; cursor_injected = true; Send(events);
    pending = true; deadline = GetTickCount64() + 5000;
  }
  void Key() {
    InputGuard(); std::vector<INPUT> events(2);
    for (auto& event : events) { event.type = INPUT_KEYBOARD; event.ki.wVk = kFocusKey; event.ki.dwExtraInfo = marker; }
    events[1].ki.dwFlags = KEYEVENTF_KEYUP; expected_key = key_down + 1; Send(events);
    pending = true; deadline = GetTickCount64() + 5000;
  }
  void CloseForegroundGuard() const {
    DesktopGuard(); OwnWindows(); BoundGameWindow(); IdleInput();
    Available(GetForegroundWindow() == game_window && WindowPid(game_window) == game_pid &&
                  OwnedGameFocus(ThreadInfo(game_thread).hwndFocus),
              "Normal close requires the exact game to own actual foreground and thread focus.");
  }
  void CloseGame() {
    Require(!close_requested && !close_pipeline_started, "Copied game close request cannot be replayed.");
    CloseForegroundGuard();
    Require(foreground_grant != nullptr, "Cannot close the cooperative scenario without a channel identity.");
    close_pipeline_started = true; cooperator_close_deadline = GetTickCount64() + 5000;
    foreground_grant->BeginStop();
    pending = true; deadline = GetTickCount64() + 30000;
  }
  void SendGameClose(bool require_game_foreground) {
    BoundGameWindow(); Require(!close_requested, "Copied game WM_CLOSE request cannot be replayed.");
    Require(GetCurrentThreadId() == probe_thread, "Close callback must be registered on the bound probe GUI thread.");
    if (!close_receipt) {
      close_receipt = std::make_shared<CloseReceipt>(game.value, game_pid, creation, game_window);
      close_receipt_token = RegisterCloseReceipt(close_receipt);
    }
    Require(close_receipt_token != 0, "The single close request has no callback registration.");
    if (require_game_foreground) CloseForegroundGuard();
    close_foreground_required = require_game_foreground;
    close_target = game_window; close_target_pid = WindowPid(close_target);
    close_foreground = GetForegroundWindow(); close_foreground_pid = WindowPid(close_foreground);
    GUITHREADINFO info{}; info.cbSize = sizeof(info);
    close_game_focus_observed = GetGUIThreadInfo(game_thread, &info) != FALSE;
    close_game_focus = close_game_focus_observed ? info.hwndFocus : nullptr;
    close_game_focus_pid = WindowPid(close_game_focus);
    close_foreground_matched = close_foreground == game_window && close_foreground_pid == game_pid &&
        close_game_focus_observed && OwnedGameFocus(close_game_focus);
    if (require_game_foreground)
      Available(close_foreground_matched, "Game foreground/focus changed immediately before its normal close request.");
    // This records the sole sending attempt, including failure. Cleanup may
    // close the retained game without foreground, but never activate or retry.
    close_requested = true; ++close_send_attempts;
    SetLastError(ERROR_SUCCESS);
    close_send_result = SendMessageCallbackW(close_target, WM_CLOSE, 0, 0, ReceiveGameClose, close_receipt_token) != FALSE;
    close_send_error = GetLastError();
    Require(close_send_result, "Cannot send the single close request to the exact copied game HWND.");
  }
  std::string CloseMessageJson() const {
    std::ostringstream out;
    out << "{\"transport\":\"SendMessageCallbackW\",\"attempts\":" << close_send_attempts
        << ",\"foregroundRequired\":" << (close_foreground_required ? "true" : "false")
        << ",\"foregroundMatched\":" << (close_foreground_matched ? "true" : "false")
        << ",\"targetHwnd\":" << WindowValue(close_target) << ",\"targetPid\":" << close_target_pid
        << ",\"foregroundHwnd\":" << WindowValue(close_foreground) << ",\"foregroundPid\":" << close_foreground_pid
        << ",\"gameFocusObserved\":" << (close_game_focus_observed ? "true" : "false")
        << ",\"gameFocusHwnd\":" << WindowValue(close_game_focus) << ",\"gameFocusPid\":" << close_game_focus_pid
        << ",\"sendResult\":";
    if (close_send_attempts) out << (close_send_result ? "true" : "false"); else out << "null";
    out << ",\"sendError\":";
    if (close_send_attempts) out << close_send_error; else out << "null";
    const bool observed = close_receipt && close_receipt->callback_count != 0;
    out << ",\"callbackObserved\":" << (observed ? "true" : "false")
        << ",\"callbackCount\":" << (close_receipt ? close_receipt->callback_count : 0) << ",\"callback\":";
    if (observed) {
      const auto& receipt = *close_receipt;
      out << "{\"hwnd\":" << WindowValue(receipt.callback_window) << ",\"message\":" << receipt.callback_message
          << ",\"lResult\":" << static_cast<int64_t>(receipt.callback_result)
          << ",\"senderThreadId\":" << receipt.callback_thread << ",\"tickCount64\":" << receipt.callback_ticks
          << ",\"targetMatched\":" << (receipt.callback_target_matched ? "true" : "false")
          << ",\"gameIdentityMatched\":" << (receipt.callback_identity_matched ? "true" : "false")
          << ",\"gamePid\":" << receipt.callback_pid << ",\"gameCreationTimeHex\":";
      if (receipt.callback_creation_observed) out << Quote(Hex(receipt.callback_creation)); else out << "null";
      out << ",\"gameWaitResult\":" << receipt.callback_wait << ",\"gameAlive\":";
      if (receipt.callback_wait == WAIT_TIMEOUT) out << "true";
      else if (receipt.callback_wait == WAIT_OBJECT_0) out << "false";
      else out << "null";
      out << ",\"gameExitCode\":";
      if (receipt.callback_exit_observed) out << receipt.callback_exit; else out << "null";
      out << ",\"win32Error\":" << receipt.callback_error << '}';
    } else out << "null";
    out << '}'; return out.str();
  }
  bool AdvanceClose(bool require_success) {
    Require(close_pipeline_started && foreground_grant, "Foreground close pipeline was not started.");
    if (!foreground_grant->PollStop()) {
      Require(GetTickCount64() < cooperator_close_deadline, "Foreground channel cancellation did not complete before its deadline."); return false;
    }
    if (!cooperator_evidence_read) {
      const auto path = invocation.root + L"\\game-foreground-evidence.json";
      DWORD attributes = GetFileAttributesW(path.c_str());
      if (attributes == INVALID_FILE_ATTRIBUTES) {
        Require(GetLastError() == ERROR_FILE_NOT_FOUND && GetTickCount64() < cooperator_close_deadline,
                "Game foreground evidence was not atomically published before its deadline."); return false;
      }
      auto evidence = OpenPinned(path); const auto json = ReadSmall(evidence.value, 65536);
      cooperator_evidence_hash = Sha256(evidence.value);
      if (require_success) {
        foreground_grant->ValidateEvidence(json, probe_exe_hash, descriptor_hash); cooperator_evidence_verified = true;
      }
      pinned_files.push_back(std::move(evidence)); cooperator_evidence_read = true;
    }
    if (require_success) Require(cooperator_evidence_verified, "Game foreground evidence was not validated as a complete three-grant session.");
    if (!close_requested && GameAlive()) SendGameClose(require_success);
    return true;
  }
  void Observe() {
    OwnWindows(); if (!game.value) return;
    const bool observing_exit = ObservingGameExit();
    // After our sole accepted close send, only observe the retained process.
    // Teardown may remove its image/window/GUI thread before HANDLE signaling;
    // actual exit still requires that signal and the retained exit code.
    if (observing_exit) CheckGameIdentity(); else CheckGame();
    if (foreground_grant && !close_pipeline_started) foreground_grant->Poll();
    if (!pending) { if (!observing_exit && GameAlive() && game_window) BoundGameWindow(); return; }
    if (operation == FocusOperation::kCloseGame) {
      if (!AdvanceClose(true)) return;
      if (!GameAlive()) { CheckGameIdentity(); Require(close_requested && cooperator_evidence_verified && GameExitCode() == 0,
                           "Copied game exited unexpectedly or abnormally during the cooperative close."); pending = false; return; }
    } else {
      Require(GameAlive(), "Exact copied game exited before completing the focus scenario; no replacement PID was followed.");
      if (operation == FocusOperation::kInitialize) {
        auto candidates = GameWindows(); Require(candidates.size() <= 1, "Multiple eligible Unity windows are ambiguous.");
        if (candidates.size() == 1) {
          if (game_window) Require(candidates[0] == game_window, "Unity HWND changed while waiting for foreground cooperation.");
          else { game_window = candidates[0]; game_thread = GetWindowThreadProcessId(game_window, nullptr); }
          Require(game_thread != 0, "Cannot bind the exact Unity GUI thread.");
          const auto& grant = foreground_grant->observation();
          if (grant.ready && grant.identity_matched) { pending = false; return; }
        }
      } else {
        BoundGameWindow();
        if (operation == FocusOperation::kFocusGame && GetForegroundWindow() == game_window &&
            OwnedGameFocus(ThreadInfo(game_thread).hwndFocus)) pending = false;
        if (operation == FocusOperation::kFocusProbe) {
          const auto& grant = foreground_grant->observation();
          if (grant.response_received && !grant.activation_requested) ActivateGrantedProbe();
          if (grant.activation_requested && GetForegroundWindow() == top) {
            if (!child_focus_requested) { child_focus_requested = true; SetFocus(child); }
            if (ThreadInfo(probe_thread).hwndFocus == child) pending = false;
          }
        }
        if (operation == FocusOperation::kClickProbe && mouse_down == expected_down && mouse_up == expected_up) pending = false;
        if (operation == FocusOperation::kSendFocusKey && key_down == expected_key) pending = false;
      }
    }
    if (pending && GetTickCount64() >= deadline) {
      bool focus = operation == FocusOperation::kFocusGame || operation == FocusOperation::kFocusProbe;
      throw ProbeFailure("Bound operation timed out while observing actual state; no action was replayed.", focus);
    }
  }
  FocusSnapshot Snapshot() {
    const bool observing_exit = ObservingGameExit();
    if (observing_exit) CheckGameIdentity();
    OwnWindows(); const bool alive = GameAlive();
    auto candidates = observing_exit ? std::vector<HWND>{} : GameWindows();
    HWND foreground = GetForegroundWindow(); auto probe_info = ThreadInfo(probe_thread);
    HWND game_focus = nullptr; bool game_focus_owned = false;
    if (!observing_exit && alive && game_window && IsWindow(game_window)) {
      game_focus = ThreadInfo(game_thread).hwndFocus; game_focus_owned = OwnedGameFocus(game_focus);
      Require(!game_focus || game_focus_owned, "Game GUI thread focus belongs to an unexpected process/window.");
    }
    LONG_PTR style = GetWindowLongPtrW(top, GWL_EXSTYLE);
    constexpr LONG_PTR mask = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    LONG_PTR expected = WS_EX_LAYERED | (mode_pass ? WS_EX_TRANSPARENT | WS_EX_NOACTIVATE : 0);
    Require((style & mask) == expected, "Probe input style changed outside the fixed operation.");
    POINT cursor{}; BOOL cursor_ok = GetCursorPos(&cursor);
    std::ostringstream diagnostics;
    diagnostics << "{\"schemaVersion\":1,\"nativeGitSha\":\"" << MYSTIA_WINDOW_PROBE_GIT_SHA
        << "\",\"runId\":" << Quote(invocation.run) << ",\"suite\":\"focus\",\"gameFocusOwned\":" << (game_focus_owned ? "true" : "false")
        << ",\"gameObservationPhase\":" << Quote(observing_exit ? "retained-process-exit" : "live-window")
        << ",\"gameWindowCountKnown\":" << (!observing_exit || !alive ? "true" : "false")
        << ",\"inputMarkerHex\":\"" << Hex(marker) << "\",\"focusTestVirtualKey\":135,\"focusTestKeyName\":\"F24\""
        << ",\"gameExeSha256\":" << Quote(game_exe_hash) << ",\"unityPlayerSha256\":" << Quote(unity_hash)
        << ",\"metadataSha256\":" << Quote(metadata_hash) << ",\"gameAssemblySha256\":" << Quote(assembly_hash)
        << ",\"preparedEvidenceSha256\":" << Quote(prepared_hash)
        << ",\"cooperatorSha256\":" << Quote(cooperator_hash) << ",\"cooperatorBuildEvidenceSha256\":" << Quote(cooperator_build_hash)
        << ",\"foregroundDescriptorSha256\":" << Quote(descriptor_hash)
        << ",\"cooperatorEvidenceSha256\":" << Quote(cooperator_evidence_hash)
        << ",\"cooperatorEvidenceVerified\":" << (cooperator_evidence_verified ? "true" : "false")
        << ",\"closeMessage\":" << CloseMessageJson()
        << ",\"foregroundChannelStopped\":" << (foreground_grant && foreground_grant->stopped() ? "true" : "false")
        << ",\"allowAttempted\":" << (foreground_grant && foreground_grant->observation().attempted ? "true" : "false")
        << ",\"parentInJob\":" << (parent_in_job ? "true" : "false") << ",\"gameInJob\":" << (game_in_job ? "true" : "false")
        << ",\"gameResumed\":" << (game_resumed ? "true" : "false") << ",\"requestOperation\":" << static_cast<int>(operation)
        << ",\"probeStyleHex\":\"" << Hex(static_cast<uint64_t>(style)) << "\",\"probeDpi\":" << GetDpiForWindow(top)
        << ",\"lastSendRequested\":" << send_requested << ",\"lastSendInserted\":" << send_inserted << ",\"lastSendError\":" << send_error
        << ",\"cursorAvailable\":" << (cursor_ok ? "true" : "false") << ",\"cursor\":[" << cursor.x << ',' << cursor.y << ']'
        << ",\"rawMouseDown\":" << raw_down << ",\"rawMouseUp\":" << raw_up << ",\"rawF24Down\":" << raw_key
        << ",\"rawDownExtraHex\":\"" << Hex(raw_down_extra) << "\",\"rawUpExtraHex\":\"" << Hex(raw_up_extra)
        << "\",\"rawF24ExtraHex\":\"" << Hex(raw_key_extra) << "\"}";
    FocusSnapshot result(MYSTIA_WINDOW_PROBE_GIT_SHA, GetCurrentProcessId(), ++sequence, request_id, pending,
                         game_pid, creation ? Hex(creation) : "", alive, identity_matched, close_requested,
                         static_cast<int64_t>(candidates.size()), game_thread, WindowValue(top), WindowValue(child), probe_thread,
                         IsWindowVisible(top) != FALSE, mode_pass ? InputProbeMode::kPassThrough : InputProbeMode::kInteractive,
                         WindowValue(foreground), WindowPid(foreground), WindowPid(probe_info.hwndFocus), mouse_down, mouse_up,
                         key_down, foreground_result, foreground_error, diagnostics.str());
    if (game.value && !alive) result.set_game_exit_code(GameExitCode());
    if (!observing_exit && alive && game_window && IsWindow(game_window) && WindowPid(game_window) == game_pid &&
        GetWindowThreadProcessId(game_window, nullptr) == game_thread) result.set_game_hwnd(WindowValue(game_window));
    if (game_focus) result.set_game_focus_hwnd(WindowValue(game_focus));
    if (probe_info.hwndFocus) result.set_focus_hwnd(WindowValue(probe_info.hwndFocus));
    if (foreground_grant) {
      const auto& observed = foreground_grant->observation();
      ForegroundGrantSnapshot grant(observed.ready, observed.identity_matched, static_cast<int64_t>(observed.sequence),
          static_cast<int64_t>(observed.request_id), static_cast<int64_t>(observed.response_sequence), observed.issuer_pid, observed.target_pid,
          static_cast<int64_t>(observed.foreground_hwnd), observed.foreground_pid, static_cast<int64_t>(observed.foreground_after_hwnd),
          observed.foreground_after_pid, observed.activation_requested);
      if (observed.response_received) { grant.set_allow_result(observed.succeeded); grant.set_allow_error(observed.error); }
      result.set_foreground_grant(grant);
    }
    return result;
  }
  flutter::EncodableValue ErrorDetails() noexcept {
    try { return flutter::EncodableValue(flutter::CustomEncodableValue(Snapshot())); }
    catch (...) {
      return flutter::EncodableValue(std::string("{\"schemaVersion\":1,\"kind\":\"input-native-error-fallback\",\"nativeGitSha\":\"") +
          MYSTIA_WINDOW_PROBE_GIT_SHA + "\",\"runId\":" + Quote(invocation.run) + "}");
    }
  }
  FocusSnapshot Execute(const FocusCommand& command) {
    Require(initial_error.empty() && !finishing && invocation.suite == "focus", "Focus call is unavailable for this invocation.");
    if (command.operation() == FocusOperation::kInspect) {
      Require(command.request_id() == request_id, "Inspect request does not match the current action.");
      Observe(); return Snapshot();
    }
    Require(!close_pipeline_started, "Only inspection and finish are allowed after the close pipeline starts.");
    Require(!pending && command.request_id() == request_id + 1 && command.request_id() <= 1000,
            "Focus action is pending, repeated or out of order.");
    operation = command.operation(); request_id = command.request_id();
    if (operation != FocusOperation::kInitialize) Require(initialized && game_window, "Game has not completed unique window binding.");
    switch (operation) {
      case FocusOperation::kInitialize: StartGame(); break;
      case FocusOperation::kFocusGame: Focus(false); break;
      case FocusOperation::kFocusProbe: Focus(true); break;
      case FocusOperation::kSetPassThrough: BoundGameWindow(); DesktopGuard(); IdleInput(); ApplyMode(true); break;
      case FocusOperation::kHideProbe:
        BoundGameWindow(); DesktopGuard();
        Require(GetForegroundWindow() == game_window && OwnedGameFocus(ThreadInfo(game_thread).hwndFocus),
                "Probe may hide only after exact game foreground/focus is observed.");
        ShowWindow(top, SW_HIDE); Require(!IsWindowVisible(top), "Probe hide was not observed."); break;
      case FocusOperation::kClickProbe: Click(); break;
      case FocusOperation::kSendFocusKey: Key(); break;
      case FocusOperation::kCloseGame: CloseGame(); break;
      default: throw ProbeFailure("Unknown focus operation.");
    }
    Observe(); return Snapshot();
  }
  void RestoreCursor() {
    POINT current{};
    if (!cursor_saved || !cursor_injected || !GetCursorPos(&current) || current.x != last_cursor.x || current.y != last_cursor.y) return;
    DesktopGuard(); cursor_restored = SetCursorPos(original_cursor.x, original_cursor.y) != FALSE;
    Require(cursor_restored, "Cannot restore the unchanged injected cursor position."); cursor_injected = false;
  }
  void CleanupRecord() {
    bool alive = GameAlive(); std::ostringstream out;
    out << "{\"schemaVersion\":1,\"kind\":\"input-probe-native-cleanup\",\"runId\":" << Quote(invocation.run)
        << ",\"gitSha\":\"" << MYSTIA_WINDOW_PROBE_GIT_SHA << "\",\"suite\":" << Quote(invocation.suite)
        << ",\"gamePid\":" << game_pid << ",\"gameCreationTimeHex\":\"" << Hex(creation)
        << "\",\"gameIdentityMatched\":" << (identity_matched ? "true" : "false")
        << ",\"closeRequested\":" << (close_requested ? "true" : "false") << ",\"gameAlive\":" << (alive ? "true" : "false")
        << ",\"gameExitCode\":";
    if (game.value && !alive) out << GameExitCode(); else out << "null";
    out << ",\"startupSuspendedTerminated\":" << (startup_terminated ? "true" : "false")
        << ",\"nodeJobFallbackRequired\":" << (alive ? "true" : "false")
        << ",\"cursorRestored\":" << (cursor_restored ? "true" : "false")
        << ",\"foregroundChannelStopped\":" << (foreground_grant && foreground_grant->stopped() ? "true" : "false")
        << ",\"cooperatorEvidenceSha256\":" << Quote(cooperator_evidence_hash)
        << ",\"cooperatorEvidenceVerified\":" << (cooperator_evidence_verified ? "true" : "false")
        << ",\"closeMessage\":" << CloseMessageJson()
        << ",\"exitCode\":" << finish_code << ",\"cleanupError\":" << Quote(cleanup_error) << "}\n";
    WriteNew(invocation.root + L"\\native-cleanup.json", out.str(), 16384);
  }
  void CompleteFinish() {
    try {
      if (foreground_grant && !foreground_grant->stopped()) {
        if (!foreground_grant->PollStop() && GetTickCount64() < finish_deadline) return;
        if (!foreground_grant->stopped()) { cleanup_error = "Foreground channel cancellation did not complete."; finish_code = 1; }
      }
      if (GameAlive() && !close_requested) {
        try { AdvanceClose(false); }
        catch (const std::exception& error) {
          cleanup_error = error.what(); finish_code = 1;
          // Failed/missing cooperative evidence is not a reason to leave this
          // exact owned game running. Only the retained unique HWND may receive
          // one normal close; that cleanup never converts the failed suite.
          if (!cleanup_close_attempted && !close_requested) {
            cleanup_close_attempted = true;
            try { SendGameClose(false); } catch (const std::exception& close_error) { cleanup_error += std::string("; ") + close_error.what(); }
          }
        }
      }
      if (GameAlive() && GetTickCount64() < finish_deadline) return;
      if (GameAlive()) { cleanup_error = "Bound game did not exit within cleanup deadline; node job fallback remains necessary."; finish_code = 1; }
      if (game.value && !GameAlive() && GameExitCode() != 0) finish_code = 1;
      RestoreCursor(); CleanupRecord();
      KillTimer(top, kFinishTimer); auto reply = std::move(finish_reply);
      if (reply) reply(std::nullopt);
      PostQuitMessage(finish_code);
    } catch (const std::exception& error) {
      KillTimer(top, kFinishTimer); auto reply = std::move(finish_reply);
      if (reply) reply(FlutterError("cleanup-failed", error.what()));
      PostQuitMessage(1);
    }
  }
  void Finish(const std::string& report, int64_t code, std::function<void(std::optional<FlutterError>)> reply) {
    Require(invocation_valid && !finishing && (code == 0 || code == 1 || code == 2), "Invalid or repeated fixed finish request.");
    Require(report.size() >= 2 && report.front() == '{' && report.back() == '}' && report.size() <= 1024 * 1024,
            "Probe report must be a bounded JSON object.");
    if (code == 0 && invocation.suite == "focus") {
      CheckGameIdentity();
      // Callback delivery diagnoses WindowProc progress; it is not acceptance
      // of quitting. The target may exit normally before callback delivery.
      Require(initial_error.empty() && game.value && identity_matched && close_requested && !pending && !GameAlive() && GameExitCode() == 0 &&
                  foreground_grant && foreground_grant->stopped() && cooperator_evidence_verified &&
                  close_send_attempts == 1 && close_send_result && close_foreground_required && close_foreground_matched,
              "Focus PASS requires verified cooperative EOF evidence, a single foreground close and the exact game's observed exit zero.");
    }
    if (code == 0 && invocation.suite == "xinput") Require(initial_error.empty() && sequence > 0 && xinput && get_state,
                                                          "XInput PASS requires real system API sampling.");
    // Preserve the original FAIL/BLOCKED report even when normal cleanup cannot
    // finish. Separate native evidence identifies remaining job cleanup.
    WriteNew(invocation.result, report, 1024 * 1024);
    finishing = true; pending = false; finish_code = static_cast<int>(code); finish_reply = std::move(reply);
    finish_deadline = GetTickCount64() + 30000;
    try {
      if (foreground_grant) {
        if (!close_pipeline_started) { close_pipeline_started = true; cooperator_close_deadline = GetTickCount64() + 5000; }
        foreground_grant->BeginStop();
      }
      if (GameAlive() || (foreground_grant && !foreground_grant->stopped())) {
        if (!SetTimer(top, kFinishTimer, 25, nullptr)) {
          cleanup_error = "Cannot schedule bounded cleanup observation."; finish_code = 1; finish_deadline = GetTickCount64(); CompleteFinish();
        } else CompleteFinish();
      } else CompleteFinish();
    } catch (const std::exception& error) {
      // Ownership of the single reply has already moved to finish_reply.
      // Never throw back to the outer entry point and answer its copy again.
      auto callback = std::move(finish_reply);
      if (callback) callback(FlutterError("cleanup-failed", error.what()));
      PostQuitMessage(1);
    }
  }
  static LRESULT CALLBACK ChildProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR reference) {
    auto* self = reinterpret_cast<Impl*>(reference); auto extra = static_cast<uint64_t>(GetMessageExtraInfo());
    if (message == WM_LBUTTONDOWN) { ++self->raw_down; self->raw_down_extra = extra; if (extra == self->marker) ++self->mouse_down; }
    if (message == WM_LBUTTONUP) { ++self->raw_up; self->raw_up_extra = extra; if (extra == self->marker) ++self->mouse_up; }
    if (self->mode_pass && message == WM_NCHITTEST) return HTTRANSPARENT;
    if (self->mode_pass && message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    return DefSubclassProc(window, message, wparam, lparam);
  }
};

bool ValidateInputProbeInvocation(const std::vector<std::string>& arguments) {
  try { ParseInvocation(arguments); return true; } catch (...) { return false; }
}
InputProbeBridge::InputProbeBridge(HWND top, flutter::FlutterViewController* controller, const std::vector<std::string>& args)
    : impl_(std::make_unique<Impl>(top, controller, args)) { queued_bridge = this; }
InputProbeBridge::~InputProbeBridge() { if (queued_bridge == this) queued_bridge = nullptr; }
void InputProbeBridge::ObserveQueuedMessage(const MSG& message) {
  if (!queued_bridge) return;
  auto& self = *queued_bridge->impl_;
  if (message.hwnd == self.child && message.message == WM_KEYDOWN && message.wParam == kFocusKey) {
    auto extra = static_cast<uint64_t>(GetMessageExtraInfo()); ++self.raw_key; self.raw_key_extra = extra;
    if (extra == self.marker && (static_cast<uint64_t>(message.lParam) & (uint64_t{1} << 30)) == 0) ++self.key_down;
  }
}
std::optional<LRESULT> InputProbeBridge::HandleWindowMessage(UINT message, WPARAM wparam, LPARAM) {
  if (message == WM_TIMER && wparam == kFinishTimer) { impl_->CompleteFinish(); return 0; }
  if (message == WM_MOUSEACTIVATE && impl_->mode_pass) return MA_NOACTIVATE;
  if (message == WM_NCHITTEST && impl_->mode_pass) return HTTRANSPARENT;
  return std::nullopt;
}
void InputProbeBridge::SampleXInput(std::function<void(ErrorOr<XInputSnapshot>)> result) {
  try { auto snapshot = impl_->Sample(); result(snapshot); }
  catch (const ProbeFailure& error) { result(FlutterError(error.blocked ? "blocked" : "native-error", error.what())); }
  catch (const std::exception& error) { result(FlutterError("native-error", error.what())); }
}
void InputProbeBridge::ExecuteFocus(const FocusCommand& command, std::function<void(ErrorOr<FocusSnapshot>)> result) {
  try { auto snapshot = impl_->Execute(command); result(snapshot); }
  catch (const ProbeFailure& error) { auto details = impl_->ErrorDetails(); result(FlutterError(error.blocked ? "blocked" : "native-error", error.what(), details)); }
  catch (const std::exception& error) { auto details = impl_->ErrorDetails(); result(FlutterError("native-error", error.what(), details)); }
}
void InputProbeBridge::Finish(const std::string& report, int64_t code, std::function<void(std::optional<FlutterError>)> result) {
  try { impl_->Finish(report, code, result); }
  catch (const std::exception& error) { result(FlutterError("finish-failed", error.what())); PostQuitMessage(1); }
}
