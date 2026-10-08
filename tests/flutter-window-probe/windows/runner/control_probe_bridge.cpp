#include <winsock2.h>
#include <ws2tcpip.h>
#include "control_probe_bridge.h"

#include <bcrypt.h>
#include <commctrl.h>
#include <iphlpapi.h>
#include <sddl.h>
#include <xinput.h>
#include <tlhelp32.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iomanip>
#include <iterator>
#include <limits>
#include <map>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string_view>
#include <utility>

namespace {
using namespace mystia_control_probe;
constexpr UINT_PTR kSubclass = 0x435052;
constexpr UINT_PTR kPollTimer = 0x43504f;
constexpr UINT_PTR kFinishTimer = 0x435046;
constexpr wchar_t kExecutable[] = L"mystia-steward-companion-window-probe.exe";
constexpr wchar_t kGameExecutable[] = L"Touhou Mystia Izakaya.exe";
constexpr char kOriginalModSha[] = "e1e3603ccb3a35e17f8ade9f70178d4f82f710b9ffb0df1cbbbddc901ec9bc33";
thread_local ControlProbeBridge* queued_bridge = nullptr;

class ProbeFailure : public std::runtime_error {
 public:
  ProbeFailure(const char* message, bool is_blocked = false)
      : std::runtime_error(message), blocked(is_blocked) {}
  bool blocked;
};
void RememberControlFailure(const std::exception& failure, std::string& error, bool& blocked) {
  if (!error.empty()) return;
  error = failure.what();
  const auto* known = dynamic_cast<const ProbeFailure*>(&failure);
  blocked = known && known->blocked;
}
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
struct Invocation {
  std::string run, suite;
  std::wstring root, result;
  bool client = false;
  bool legacy = false;
  std::string token_hash;
  std::string legacy_snapshot_token;  // Fixture credential, private only; never serialize Invocation.
  unsigned generation = 0;
  DWORD expected_game_pid = 0;
  uint64_t expected_game_creation = 0;
};
Invocation ParseInvocation(const std::vector<std::string>& args) {
  Require(args.size() == 7 && args[0] == "--probe" && args[1] == "--run-id" && args[3] == "--suite" &&
              args[4] == "hotkey" && args[5] == "--result-file",
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

// This sidecar has fourteen flat fields (version plus bounded ASCII strings). Do not silently accept duplicate
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
      if (key == "schemaVersion") { Skip(); Require(Peek() == '1', "Unsupported sidecar schema version."); ++at_; value = "1"; }
      else value = String();
      Require(result.emplace(key, value).second, "Duplicate input sidecar member.");
      Skip(); if (Peek() == '}') { ++at_; break; } Take(',');
    }
    Skip(); Require(at_ == source_.size(), "Trailing bytes in input sidecar.");
    constexpr const char* keys[] = {"schemaVersion", "runId", "gitSha", "gameExecutable", "expectedExeSha256",
        "expectedUnityPlayerSha256", "expectedMetadataSha256", "expectedGameAssemblySha256",
        "steamAppId", "steamBuildId", "preparedEvidenceSha256", "expectedModSha256", "modBuildEvidenceSha256", "expectedBepInExSha256"};
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

struct PendingForegroundNullObservations {
  uint64_t count = 0, first_ticks = 0, last_ticks = 0;
};
// Only an already-issued handoff may wait through NULL foreground. Windows
// can report NULL while a window is being deactivated. This helper performs
// no window/input operation and never classifies NULL as an applied handoff.
bool PendingForegroundKnown(HWND foreground, HWND source, HWND target,
                            uint64_t now, uint64_t deadline,
                            PendingForegroundNullObservations& unknown,
                            const char* foreign_message, const char* timeout_message) {
  if (!foreground) {
    if (unknown.count == 0) unknown.first_ticks = now;
    ++unknown.count; unknown.last_ticks = now;
    Available(now < deadline, timeout_message);
    return false;
  }
  Available(foreground == source || foreground == target, foreign_message);
  return true;
}

std::vector<unsigned char> UserSid(HANDLE process) {
  Handle token;
  Require(OpenProcessToken(process, TOKEN_QUERY, &token.value), "Cannot inspect the retained process user.");
  DWORD size = 0;
  GetTokenInformation(token.value, TokenUser, nullptr, 0, &size);
  Require(size >= sizeof(TOKEN_USER) && size <= 65536, "Invalid process user token size.");
  std::vector<unsigned char> storage(size);
  Require(GetTokenInformation(token.value, TokenUser, storage.data(), size, &size), "Cannot read process user token.");
  PSID sid = reinterpret_cast<TOKEN_USER*>(storage.data())->User.Sid;
  Require(IsValidSid(sid), "Invalid process user SID.");
  std::vector<unsigned char> result(GetLengthSid(sid));
  Require(CopySid(static_cast<DWORD>(result.size()), result.data(), sid), "Cannot retain process user SID.");
  return result;
}
struct Socket {
  SOCKET value = INVALID_SOCKET;
  ~Socket() { if (value != INVALID_SOCKET) closesocket(value); }
};
DWORD ListenerOwner() {
  // A different address/family on the same product port is a competing
  // service, even if Windows would allow our specific IPv4 bind to coexist.
  DWORD ipv6_size = 0;
  Require(GetExtendedTcpTable(nullptr, &ipv6_size, FALSE, AF_INET6, TCP_TABLE_OWNER_PID_LISTENER, 0) == ERROR_INSUFFICIENT_BUFFER,
          "Cannot size the IPv6 control listener table.");
  bool ipv6_observed = false;
  for (int attempt = 0; attempt < 3; ++attempt) {
    Require(ipv6_size >= offsetof(MIB_TCP6TABLE_OWNER_PID, table) && ipv6_size <= 1024 * 1024,
            "IPv6 listener table exceeds its bound.");
    std::vector<DWORD> storage((ipv6_size + sizeof(DWORD) - 1) / sizeof(DWORD));
    const auto code = GetExtendedTcpTable(storage.data(), &ipv6_size, FALSE, AF_INET6, TCP_TABLE_OWNER_PID_LISTENER, 0);
    if (code == ERROR_INSUFFICIENT_BUFFER) continue;
    Require(code == NO_ERROR, "Cannot read the IPv6 control listener table.");
    const auto* table = reinterpret_cast<const MIB_TCP6TABLE_OWNER_PID*>(storage.data());
    Require(table->dwNumEntries <= (storage.size() * sizeof(DWORD) - offsetof(MIB_TCP6TABLE_OWNER_PID, table)) /
                sizeof(MIB_TCP6ROW_OWNER_PID), "IPv6 control listener table is malformed.");
    for (DWORD index = 0; index < table->dwNumEntries; ++index)
      Available(ntohs(static_cast<u_short>(table->table[index].dwLocalPort)) != 32146,
                "An IPv6 listener already owns product port 32146; it cannot be displaced.");
    ipv6_observed = true; break;
  }
  Require(ipv6_observed, "IPv6 control listener table did not stabilize.");
  DWORD size = 0;
  Require(GetExtendedTcpTable(nullptr, &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) == ERROR_INSUFFICIENT_BUFFER,
          "Cannot size the TCP listener owner table.");
  for (int attempt = 0; attempt < 3; ++attempt) {
    Require(size >= offsetof(MIB_TCPTABLE_OWNER_PID, table) && size <= 1024 * 1024, "TCP owner table exceeds its bound.");
    std::vector<DWORD> storage((size + sizeof(DWORD) - 1) / sizeof(DWORD));
    const auto code = GetExtendedTcpTable(storage.data(), &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
    if (code == ERROR_INSUFFICIENT_BUFFER) continue;
    Require(code == NO_ERROR, "Cannot read the TCP listener owner table.");
    const auto* table = reinterpret_cast<const MIB_TCPTABLE_OWNER_PID*>(storage.data());
    Require(table->dwNumEntries <= (storage.size() * sizeof(DWORD) - offsetof(MIB_TCPTABLE_OWNER_PID, table)) /
                sizeof(MIB_TCPROW_OWNER_PID), "TCP listener table is malformed.");
    DWORD owner = 0;
    for (DWORD index = 0; index < table->dwNumEntries; ++index) {
      const auto& row = table->table[index];
      if (ntohs(static_cast<u_short>(row.dwLocalPort)) != 32146) continue;
      Available(row.dwLocalAddr == htonl(INADDR_LOOPBACK), "An unrelated IPv4 address already owns product port 32146.");
      Require(owner == 0 && row.dwOwningPid != 0, "Control listener identity is ambiguous.");
      owner = row.dwOwningPid;
    }
    return owner;
  }
  throw ProbeFailure("TCP listener owner table did not stabilize.");
}

constexpr uint32_t kMagic = 0x3143534d, kVersion = 1;
constexpr size_t kFrameSize = 208;
using Frame = std::array<unsigned char, kFrameSize>;
void Put32(Frame& frame, size_t offset, uint32_t value) {
  for (size_t index = 0; index < 4; ++index) frame[offset + index] = static_cast<unsigned char>(value >> (index * 8));
}
uint32_t Get32(const Frame& frame, size_t offset) {
  uint32_t value = 0;
  for (size_t index = 0; index < 4; ++index) value |= static_cast<uint32_t>(frame[offset + index]) << (index * 8);
  return value;
}
void Put(Frame& frame, size_t index, uint64_t value) {
  for (size_t byte = 0; byte < 8; ++byte) frame[16 + index * 8 + byte] = static_cast<unsigned char>(value >> (byte * 8));
}
uint64_t Get(const Frame& frame, size_t index) {
  uint64_t value = 0;
  for (size_t byte = 0; byte < 8; ++byte) value |= static_cast<uint64_t>(frame[16 + index * 8 + byte]) << (byte * 8);
  return value;
}
void Header(Frame& frame, uint32_t kind) {
  Put32(frame, 0, kMagic); Put32(frame, 4, kVersion); Put32(frame, 8, kind); Put32(frame, 12, static_cast<uint32_t>(kFrameSize));
}
uint32_t Kind(const Frame& frame) {
  Require(Get32(frame, 0) == kMagic && Get32(frame, 4) == kVersion && Get32(frame, 12) == kFrameSize,
          "Control protocol header/version/frame size differs.");
  const auto kind = Get32(frame, 8);
  Require(kind >= 1 && kind <= 7, "Control frame kind is outside the version-one protocol.");
  return kind;
}
void Zero(const Frame& frame, std::initializer_list<size_t> fields) {
  for (size_t index : fields) Require(Get(frame, index) == 0, "Control frame contains a nonzero reserved/action field.");
}
std::string FrameHex(const Frame& frame) {
  std::ostringstream out; out << std::hex << std::setfill('0');
  for (unsigned char byte : frame) out << std::setw(2) << static_cast<unsigned int>(byte);
  return out.str();
}

// Finish accepts only a complete bounded JSON report. Evidence comes from the
// native state below; this parser additionally refuses forged/missing check
// names, duplicate JSON members and an inconsistent declared PASS envelope.
struct ReportValue {
  char kind = 0;
  std::string text;
  std::map<std::string, ReportValue> object;
  std::vector<ReportValue> array;
};
class ReportReader {
 public:
  explicit ReportReader(const std::string& text) : text_(text) {
    Require(!text.empty() && text.size() <= 1024 * 1024, "Control report exceeds its byte bound.");
  }
  ReportValue Read() { auto value = Value(0); Space(); Require(at_ == text_.size(), "Trailing report JSON bytes."); return value; }
 private:
  const std::string& text_; size_t at_ = 0, nodes_ = 0;
  char Peek() const { return at_ < text_.size() ? text_[at_] : '\0'; }
  void Space() { while (Peek() == ' ' || Peek() == '\t' || Peek() == '\n' || Peek() == '\r') ++at_; }
  void Take(char wanted) { Space(); Require(Peek() == wanted, "Malformed control report JSON."); ++at_; }
  uint32_t Hex4() {
    uint32_t value = 0;
    for (int index = 0; index < 4; ++index) {
      const char digit = Peek(); ++at_;
      Require((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f') || (digit >= 'A' && digit <= 'F'), "Invalid report Unicode escape.");
      value = value * 16 + static_cast<uint32_t>(digit <= '9' ? digit - '0' : (digit <= 'F' ? digit - 'A' + 10 : digit - 'a' + 10));
    }
    return value;
  }
  std::string String() {
    Take('"'); std::string result;
    while (Peek() != '"') {
      const auto value = static_cast<unsigned char>(Peek()); ++at_;
      Require(value >= 32 && result.size() < 1024 * 1024, "Invalid or oversized report string.");
      if (value != '\\') { result += static_cast<char>(value); continue; }
      const auto escape = Peek(); ++at_;
      if (escape == '"' || escape == '\\' || escape == '/') result += escape;
      else if (escape == 'b') result += '\b';
      else if (escape == 'f') result += '\f';
      else if (escape == 'n') result += '\n';
      else if (escape == 'r') result += '\r';
      else if (escape == 't') result += '\t';
      else if (escape == 'u') {
        auto code = Hex4();
        if (code >= 0xd800 && code <= 0xdbff) {
          Require(Peek() == '\\', "Missing report Unicode low surrogate."); ++at_;
          Require(Peek() == 'u', "Missing report Unicode low surrogate escape."); ++at_;
          const auto low = Hex4(); Require(low >= 0xdc00 && low <= 0xdfff, "Invalid report Unicode low surrogate.");
          code = 0x10000 + ((code - 0xd800) << 10) + low - 0xdc00;
        } else Require(code < 0xdc00 || code > 0xdfff, "Unpaired report Unicode low surrogate.");
        if (code < 0x80) result += static_cast<char>(code);
        else if (code < 0x800) { result += static_cast<char>(0xc0 | (code >> 6)); result += static_cast<char>(0x80 | (code & 63)); }
        else if (code < 0x10000) {
          result += static_cast<char>(0xe0 | (code >> 12)); result += static_cast<char>(0x80 | ((code >> 6) & 63)); result += static_cast<char>(0x80 | (code & 63));
        } else {
          result += static_cast<char>(0xf0 | (code >> 18)); result += static_cast<char>(0x80 | ((code >> 12) & 63));
          result += static_cast<char>(0x80 | ((code >> 6) & 63)); result += static_cast<char>(0x80 | (code & 63));
        }
      } else throw ProbeFailure("Unsupported report string escape.");
    }
    ++at_; return result;
  }
  ReportValue Value(size_t depth) {
    Require(depth <= 32 && ++nodes_ <= 40000, "Control report JSON nesting or member count exceeds its bound.");
    Space(); ReportValue value;
    if (Peek() == '"') { value.kind = 's'; value.text = String(); return value; }
    if (Peek() == '{') {
      value.kind = 'o'; ++at_; Space();
      if (Peek() != '}') for (;;) {
        auto key = String(); Take(':');
        Require(value.object.emplace(std::move(key), Value(depth + 1)).second, "Duplicate control report JSON member.");
        Space(); if (Peek() == '}') break; Take(',');
      }
      Take('}'); return value;
    }
    if (Peek() == '[') {
      value.kind = 'a'; ++at_; Space();
      if (Peek() != ']') for (;;) { value.array.push_back(Value(depth + 1)); Space(); if (Peek() == ']') break; Take(','); }
      Take(']'); return value;
    }
    for (const auto literal : {"true", "false", "null"}) {
      const auto length = std::strlen(literal);
      if (text_.compare(at_, length, literal) == 0) { value.kind = literal[0] == 'n' ? '0' : 'b'; value.text = literal; at_ += length; return value; }
    }
    value.kind = 'n'; const auto start = at_;
    if (Peek() == '-') ++at_;
    Require(Peek() >= '0' && Peek() <= '9', "Invalid report JSON number.");
    if (Peek() == '0') ++at_; else while (Peek() >= '0' && Peek() <= '9') ++at_;
    if (Peek() == '.') { ++at_; Require(Peek() >= '0' && Peek() <= '9', "Invalid report fraction."); while (Peek() >= '0' && Peek() <= '9') ++at_; }
    if (Peek() == 'e' || Peek() == 'E') {
      ++at_; if (Peek() == '-' || Peek() == '+') ++at_;
      Require(Peek() >= '0' && Peek() <= '9', "Invalid report exponent."); while (Peek() >= '0' && Peek() <= '9') ++at_;
    }
    value.text = text_.substr(start, at_ - start); return value;
  }
};
void ReportField(const ReportValue& value, const char* name, char kind, const std::string& expected) {
  const auto found = value.object.find(name);
  Require(value.kind == 'o' && found != value.object.end() && found->second.kind == kind && found->second.text == expected,
          "Control report field does not match its native invocation/outcome.");
}
std::string TextHash(const std::string& text) {
  Require(!text.empty() && text.size() <= 32768, "Credential hash input exceeds its bound.");
  BCRYPT_ALG_HANDLE algorithm = nullptr;
  Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0, "Cannot open SHA-256.");
  std::array<unsigned char, 32> digest{};
  const auto result = BCryptHash(algorithm, nullptr, 0,
      reinterpret_cast<PUCHAR>(const_cast<char*>(text.data())), static_cast<ULONG>(text.size()),
      digest.data(), static_cast<ULONG>(digest.size()));
  BCryptCloseAlgorithmProvider(algorithm, 0);
  Require(result == 0, "Cannot hash launch credential.");
  std::ostringstream out; out << std::hex << std::setfill('0');
  for (auto byte : digest) out << std::setw(2) << static_cast<unsigned>(byte);
  return out.str();
}
std::string ReportText(const ReportValue& value, const char* name, char kind = 's') {
  const auto found = value.object.find(name);
  Require(value.kind == 'o' && found != value.object.end() && found->second.kind == kind,
          "Lifecycle record has a missing or mistyped field.");
  return found->second.text;
}
uint64_t CanonicalUnsigned(const std::string& text, int base = 10) {
  Require(!text.empty() && text.size() <= 20, "Lifecycle integer is out of bounds.");
  size_t used = 0; const auto value = std::stoull(text, &used, base);
  Require(used == text.size() && (base == 16 ? Hex(value) : std::to_string(value)) == text,
          "Lifecycle integer is not canonical.");
  return value;
}
// Exact old 1.3.1 line protocol, committed only after EOF. No reply/ACK is added.
// Raw TCP has no OS peer process identity; the initial real parent/retained game
// and fixture credential bind show/toggle. Exit is accepted only in our close phase.
unsigned ParseLegacyControl(std::string_view message, DWORD game_pid, const std::string& token_hash) {
  Require(!message.empty() && message.size() <= 1024, "Legacy control exceeds its byte bound.");
  size_t at = 0; std::vector<std::string> lines;
  while (at < message.size()) {
    const auto end = message.find('\n', at);
    auto line = message.substr(at, end == std::string_view::npos ? message.size() - at : end - at);
    if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
    Require(!line.empty() && std::all_of(line.begin(), line.end(), [](unsigned char c) { return c > 32 && c < 127; }),
            "Legacy control contains an empty or non-ASCII line.");
    lines.emplace_back(line); at = end == std::string_view::npos ? message.size() : end + 1;
  }
  unsigned action = lines.front() == "mystia-steward-companion:show" ? 1 :
      lines.front() == "mystia-steward-companion:toggle" ? 2 : lines.front() == "mystia-steward-companion:exit" ? 3 : 0;
  Require(action != 0 && lines.size() == (action == 3 ? 2U : 4U), "Legacy action or exact field count differs.");
  std::map<std::string, std::string> fields;
  for (size_t index = 1; index < lines.size(); ++index) {
    const auto separator = lines[index].find('=');
    Require(separator != std::string::npos && fields.emplace(lines[index].substr(0, separator), lines[index].substr(separator + 1)).second,
            "Legacy control has a duplicate or malformed field.");
  }
  Require(fields.count("--game-pid") && fields.at("--game-pid") == std::to_string(game_pid), "Legacy game identity differs.");
  if (action != 3)
    Require(fields.size() == 3 && fields.count("--api") && fields.count("--token") &&
                fields.at("--api") == "http://127.0.0.1:32755" && HashText(fields.at("--token")) && TextHash(fields.at("--token")) == token_hash,
            "Legacy endpoint/fixture credential differs.");
  return action;
}
#include "legacy_snapshot_probe.h"

ReportValue ReadLifecycleRecord(const std::wstring& path, size_t members) {
  auto file = OpenPinned(path);
  auto record = ReportReader(ReadSmall(file.value, 32768)).Read();
  Require(record.kind == 'o' && record.object.size() == members, "Lifecycle record schema differs.");
  return record;
}
ReportValue LifecycleAuthorization(const Invocation& invocation) {
  const auto value = ReadLifecycleRecord(invocation.root + L"\\control-lifecycle.json", 7);
  ReportField(value, "schemaVersion", 'n', "1"); ReportField(value, "kind", 's', "control-lifecycle-authorization");
  ReportField(value, "runId", 's', invocation.run); ReportField(value, "gitSha", 's', MYSTIA_WINDOW_PROBE_GIT_SHA);
  const auto scenario = ReportText(value, "scenario");
  Require(scenario == "new-mod-cold-restart" || scenario == "old-mod-legacy-client", "Unknown control lifecycle scenario.");
  const auto token_hash = ReportText(value, "tokenSha256"), prepared_hash = ReportText(value, "preparedEvidenceSha256");
  Require(HashText(token_hash) && HashText(prepared_hash), "Lifecycle authorization hashes are invalid.");
  auto prepared = OpenPinned(invocation.root + L"\\workspace\\prepared-evidence.json");
  Require(Sha256(prepared.value) == prepared_hash, "Lifecycle preparation identity differs.");
  return value;
}
Invocation ParseClientInvocation(const std::vector<std::string>& args) {
  Require(args.size() == 3, "Expected the exact Mod client CLI.");
  std::map<std::string, std::string> values;
  for (const auto& argument : args) {
    const auto at = argument.find('=');
    Require(at != std::string::npos && values.emplace(argument.substr(0, at), argument.substr(at + 1)).second,
            "Duplicate or malformed Mod client CLI.");
  }
  Require(values.size() == 3 && values.count("--api") && values.count("--game-pid") && values.count("--token") &&
              values.at("--api") == "http://127.0.0.1:32755" && HashText(values.at("--token")),
          "Mod client CLI does not target this isolated fixture.");
  const auto module = ModulePath();
  const std::wstring prefix = L"D:\\dev\\mystia-node\\runs\\", suffix = L"\\payload\\" + std::wstring(kExecutable);
  Require(module.size() > prefix.size() + suffix.size() && SamePath(module.substr(0, prefix.size()), prefix) &&
              SamePath(module.substr(module.size() - suffix.size()), suffix), "Cold client is outside the fixed payload root.");
  const auto run = Utf8(module.substr(prefix.size(), module.size() - prefix.size() - suffix.size()));
  auto invocation = ParseInvocation({"--probe", "--run-id", run, "--suite", "hotkey", "--result-file",
      Utf8(prefix + Wide(run) + L"\\probe-result.json")});
  invocation.client = true;
  const auto authorization = LifecycleAuthorization(invocation);
  invocation.legacy = ReportText(authorization, "scenario") == "old-mod-legacy-client";
  invocation.token_hash = ReportText(authorization, "tokenSha256");
  Require(TextHash(values.at("--token")) == ReportText(authorization, "tokenSha256"), "Mod credential does not match fixture authorization.");
  if (invocation.legacy) invocation.legacy_snapshot_token = values.at("--token");
  invocation.generation = GetFileAttributesW((invocation.root + L"\\control-launch-2.json").c_str()) == INVALID_FILE_ATTRIBUTES ? 1 : 2;
  Require(!invocation.legacy || invocation.generation == 1, "Old-Mod client does not authorize a replacement generation.");
  const auto generation = std::to_string(invocation.generation);
  const auto launch = ReadLifecycleRecord(invocation.root + L"\\control-launch-" + Wide(generation) + L".json", 8);
  ReportField(launch, "schemaVersion", 'n', "1"); ReportField(launch, "kind", 's', "control-client-launch");
  ReportField(launch, "runId", 's', run); ReportField(launch, "gitSha", 's', MYSTIA_WINDOW_PROBE_GIT_SHA);
  ReportField(launch, "generation", 'n', generation);
  const auto game_pid = CanonicalUnsigned(ReportText(launch, "gamePid", 'n'));
  Require(game_pid > 0 && game_pid <= MAXDWORD && values.at("--game-pid") == std::to_string(game_pid),
          "Mod CLI game PID differs from the retained controller identity.");
  invocation.expected_game_pid = static_cast<DWORD>(game_pid);
  invocation.expected_game_creation = CanonicalUnsigned(ReportText(launch, "gameCreationHex"), 16);
  Require(invocation.expected_game_creation != 0 && HashText(ReportText(launch, "clientExecutableSha256")), "Invalid retained launch identity.");
  auto self = OpenPinned(module);
  Require(Sha256(self.value) == ReportText(launch, "clientExecutableSha256"), "Launched client bytes differ.");
  Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
  Require(snapshot.value != INVALID_HANDLE_VALUE, "Cannot inspect the actual Mod-launched parent.");
  PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry); bool matched = false;
  if (Process32FirstW(snapshot.value, &entry)) do {
    if (entry.th32ProcessID == GetCurrentProcessId()) { matched = entry.th32ParentProcessID == game_pid; break; }
  } while (Process32NextW(snapshot.value, &entry));
  Require(matched, "The actual Flutter parent is not the bound game process.");
  invocation.result = invocation.root + L"\\control-client-" + Wide(generation) + L"-result.json";
  PlainPath(invocation.result, false, true);
  Require(GetFileAttributesW(invocation.result.c_str()) == INVALID_FILE_ATTRIBUTES, "Client generation result already exists.");
  return invocation;
}
Invocation ParseRuntimeInvocation(const std::vector<std::string>& args) {
  return !args.empty() && args.front().rfind("--api=", 0) == 0 ? ParseClientInvocation(args) : ParseInvocation(args);
}
void ValidateClientPassReport(const ReportValue& report, const Invocation& invocation) {
  ReportField(report, "schemaVersion", 'n', "1"); ReportField(report, "kind", 's', invocation.legacy ? "flutter-legacy-control-client" : "flutter-control-client");
  ReportField(report, "suite", 's', "hotkey"); ReportField(report, "runId", 's', invocation.run);
  ReportField(report, "gitSha", 's', MYSTIA_WINDOW_PROBE_GIT_SHA); ReportField(report, "status", 's', "PASS");
  ReportField(report, "generation", 'n', std::to_string(invocation.generation)); ReportField(report, "p0Verified", 'b', "false");
  const auto errors = report.object.find("errors"), checks = report.object.find("checks");
  Require(errors != report.object.end() && errors->second.kind == 'a' && errors->second.array.empty() &&
              checks != report.object.end() && checks->second.kind == 'a' && checks->second.array.size() == 6,
          "Client PASS must have exactly six checks and no errors.");
  std::map<std::string, bool> required = {{"client-real-mod-launch", false}, {"client-msc1-registration", false},
      {"client-activation", false}, {"client-native-and-dart-input", false}, {"client-dart-return", false},
      {invocation.generation == 1 ? "client-retired-game-alive" : "client-exit-notified", false}};
  if (invocation.legacy) required = {{"legacy-real-mod-launch", false}, {"legacy-startup-show", false},
      {"legacy-native-and-dart-input", false}, {"legacy-existing-instance-toggle", false},
      {"legacy-click-recovery", false}, {"legacy-retained-game-close-exit-zero", false}};
  for (const auto& check : checks->second.array) {
    const auto name = ReportText(check, "name");
    Require(check.object.size() == 3 && required.count(name) && !required.at(name), "Unknown or duplicated client check.");
    required.at(name) = true; ReportField(check, "status", 's', "PASS");
    const auto detail = check.object.find("detail");
    Require(detail != check.object.end() && (detail->second.kind == '0' || detail->second.kind == 's'), "Invalid client check detail.");
  }
}
void ValidateClientBlockedReport(const ReportValue& report, const Invocation& invocation) {
  ReportField(report, "schemaVersion", 'n', "1"); ReportField(report, "kind", 's', invocation.legacy ? "flutter-legacy-control-client" : "flutter-control-client");
  ReportField(report, "suite", 's', "hotkey"); ReportField(report, "runId", 's', invocation.run);
  ReportField(report, "gitSha", 's', MYSTIA_WINDOW_PROBE_GIT_SHA); ReportField(report, "status", 's', "BLOCKED");
  ReportField(report, "generation", 'n', std::to_string(invocation.generation)); ReportField(report, "p0Verified", 'b', "false");
  const auto errors = report.object.find("errors"), checks = report.object.find("checks");
  Require(errors != report.object.end() && errors->second.kind == 'a' && errors->second.array.size() == 1 &&
              errors->second.array.front().kind == 's' && !errors->second.array.front().text.empty() &&
              checks != report.object.end() && checks->second.kind == 'a' && !checks->second.array.empty() &&
              checks->second.array.size() <= 6, "Client BLOCKED requires one error and an incomplete check prefix.");
  std::vector<std::string> required = {"client-real-mod-launch", "client-msc1-registration", "client-activation",
      "client-native-and-dart-input", "client-dart-return", invocation.generation == 1 ? "client-retired-game-alive" : "client-exit-notified"};
  if (invocation.legacy) required = {"legacy-real-mod-launch", "legacy-startup-show", "legacy-native-and-dart-input",
      "legacy-existing-instance-toggle", "legacy-click-recovery", "legacy-retained-game-close-exit-zero"};
  for (size_t index = 0; index < checks->second.array.size(); ++index) {
    const auto& check = checks->second.array[index];
    Require(check.object.size() == 3, "Invalid blocked client check schema.");
    ReportField(check, "name", 's', required[index]);
    const bool last = index + 1 == checks->second.array.size();
    ReportField(check, "status", 's', last ? "BLOCKED" : "PASS");
    const auto detail = check.object.find("detail");
    Require(detail != check.object.end() && (detail->second.kind == '0' || detail->second.kind == 's'), "Invalid blocked client check detail.");
    if (last) ReportField(check, "detail", 's', errors->second.array.front().text);
  }
}
void ValidatePassReport(const std::string& report, const std::string& run) {
  const auto parsed = ReportReader(report).Read();
  ReportField(parsed, "schemaVersion", 'n', "1"); ReportField(parsed, "kind", 's', "flutter-control-probe");
  ReportField(parsed, "suite", 's', "hotkey"); ReportField(parsed, "runId", 's', run);
  ReportField(parsed, "gitSha", 's', MYSTIA_WINDOW_PROBE_GIT_SHA); ReportField(parsed, "status", 's', "PASS");
  ReportField(parsed, "p0Verified", 'b', "false");
  const auto errors = parsed.object.find("errors");
  Require(errors != parsed.object.end() && errors->second.kind == 'a' && errors->second.array.empty(),
          "Control PASS requires an explicitly empty errors array.");
  const auto found = parsed.object.find("checks");
  Require(found != parsed.object.end() && found->second.kind == 'a' && found->second.array.size() == 10,
          "Control PASS must contain the ten required checks exactly once.");
  constexpr const char* required[] = {"control-real-mod-registered-identity", "control-f8-game-to-flutter", "control-native-and-dart-input",
      "control-f8-flutter-to-game-visible", "control-f8-hidden-recovery", "control-f8-passthrough-recovery", "control-rs-game-to-flutter",
      "control-rs-held-no-bounce", "control-rs-flutter-to-game-visible", "control-retained-game-close-exit-zero"};
  std::map<std::string, bool> remaining;
  for (const auto name : required) remaining.emplace(name, false);
  for (const auto& check : found->second.array) {
    Require(check.kind == 'o' && check.object.size() == 3, "Control check has an unexpected schema.");
    const auto name = check.object.find("name"), detail = check.object.find("detail");
    Require(name != check.object.end() && name->second.kind == 's' && remaining.count(name->second.text) &&
                !remaining.at(name->second.text), "Control PASS contains an unknown or duplicated check.");
    remaining.at(name->second.text) = true; ReportField(check, "status", 's', "PASS");
    Require(detail != check.object.end() && (detail->second.kind == '0' || detail->second.kind == 's'), "Control check detail must be text or null.");
  }
}

// Exactly one OS-authenticated peer; all I/O progresses on the GUI thread.
// OVERLAPPED, its event and buffer stay together until cancellation completes.
class ControlPipe {
 public:
  struct State {
    Handle pipe, event;
    OVERLAPPED overlapped{};
    Frame buffer{};
    DWORD offset = 0;
    enum class Stage { connecting, reading, idle, writing, stopping, stopped } stage = Stage::connecting;
    bool active = false, connected = false, eof = false;
    uint64_t deadline = 0;
    uint64_t writes = 0;
    void ResetIo() {
      Require(!active, "Cannot replace a pending control I/O operation.");
      overlapped = {}; overlapped.hEvent = event.value;
      Require(ResetEvent(event.value), "Cannot reset the control I/O event.");
    }
    void Stop() {
      if (stage == Stage::stopped || stage == Stage::stopping) return;
      stage = Stage::stopping;
      if (active && !CancelIoEx(pipe.value, &overlapped))
        Require(GetLastError() == ERROR_NOT_FOUND, "Cannot cancel the pending control I/O.");
    }
    bool PollStop() {
      if (stage == Stage::stopped) return true;
      Require(stage == Stage::stopping, "Control stop has not started.");
      if (active) {
        DWORD count = 0;
        if (!GetOverlappedResult(pipe.value, &overlapped, &count, FALSE) && GetLastError() == ERROR_IO_INCOMPLETE) return false;
        active = false;
      }
      if (connected && !DisconnectNamedPipe(pipe.value)) {
        const auto error = GetLastError();
        Require(error == ERROR_PIPE_NOT_CONNECTED || error == ERROR_BROKEN_PIPE, "Cannot disconnect the control pipe.");
      }
      pipe = Handle(); connected = false; stage = Stage::stopped; return true;
    }
  };
  explicit ControlPipe(const std::string& name, const std::vector<unsigned char>& sid) : state_(std::make_unique<State>()) {
    LPWSTR sid_text = nullptr;
    Require(ConvertSidToStringSidW(const_cast<unsigned char*>(sid.data()), &sid_text), "Cannot format current user SID.");
    const std::wstring sddl = L"D:P(A;;0x12019b;;;" + std::wstring(sid_text) + L")";
    LocalFree(sid_text);
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr),
            "Cannot build the current-user-only control pipe ACL.");
    SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES), descriptor, FALSE};
    const auto path = L"\\\\.\\pipe\\" + Wide(name);
    state_->pipe.value = CreateNamedPipeW(path.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1,
        static_cast<DWORD>(kFrameSize), static_cast<DWORD>(kFrameSize), 0, &attributes);
    LocalFree(descriptor);
    Require(state_->pipe.value != INVALID_HANDLE_VALUE, "Cannot create the exclusive local identity control pipe.");
    state_->event.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    Require(state_->event.value != nullptr, "Cannot create the control pipe event.");
    state_->overlapped.hEvent = state_->event.value;
    if (!ConnectNamedPipe(state_->pipe.value, &state_->overlapped)) {
      const auto error = GetLastError();
      Require(error == ERROR_IO_PENDING || error == ERROR_PIPE_CONNECTED, "Cannot start the sole control pipe connection.");
      state_->active = error == ERROR_IO_PENDING; state_->connected = error == ERROR_PIPE_CONNECTED;
    } else state_->connected = true;
  }
  ~ControlPipe() {
    try {
      state_->Stop();
      if (!state_->PollStop()) {
        WaitForSingleObject(state_->event.value, 2000);
        if (!state_->PollStop()) state_.release();
      }
    } catch (...) { if (state_ && state_->active) state_.release(); }
  }
  HANDLE handle() const { return state_->pipe.value; }
  bool connected() const { return state_->connected; }
  bool eof() const { return state_->eof; }
  bool idle() const { return state_->stage == State::Stage::idle; }
  bool stopped() const { return state_->stage == State::Stage::stopped; }
  uint64_t writes() const { return state_->writes; }
  void Stop() { state_->Stop(); }
  bool PollStop() { return state_->PollStop(); }
  void Write(const Frame& frame) {
    auto& state = *state_;
    Require(state.stage == State::Stage::idle && !state.active, "Control reply is repeated or another operation is pending.");
    state.buffer = frame; state.offset = 0; state.stage = State::Stage::writing; state.deadline = GetTickCount64() + 5000;
  }
  void ContinueRead() {
    auto& state = *state_;
    Require(state.stage == State::Stage::idle && !state.active, "Cannot resume control reading during an outstanding operation.");
    state.buffer = {}; state.offset = 0; state.deadline = 0; state.stage = State::Stage::reading;
  }
  std::optional<Frame> Poll() {
    auto& state = *state_;
    if (state.stage == State::Stage::stopping) { state.PollStop(); return std::nullopt; }
    if (state.stage == State::Stage::stopped || state.stage == State::Stage::idle) return std::nullopt;
    if (state.stage == State::Stage::connecting) {
      if (state.active) {
        DWORD count = 0;
        if (!GetOverlappedResult(state.pipe.value, &state.overlapped, &count, FALSE)) {
          if (GetLastError() == ERROR_IO_INCOMPLETE) return std::nullopt;
          state.active = false; throw ProbeFailure("Control pipe connection failed; reconnect is forbidden.");
        }
        state.active = false; state.connected = true;
      }
      Require(state.connected, "Control connection state is inconsistent.");
      state.stage = State::Stage::reading; state.deadline = GetTickCount64() + 5000;
    }
    if (state.deadline) Require(GetTickCount64() < state.deadline, "A partial control frame or reply exceeded five seconds.");
    DWORD count = 0;
    if (state.active) {
      if (!GetOverlappedResult(state.pipe.value, &state.overlapped, &count, FALSE)) {
        const auto error = GetLastError();
        if (error == ERROR_IO_INCOMPLETE) return std::nullopt;
        state.active = false;
        if ((error == ERROR_BROKEN_PIPE || error == ERROR_PIPE_NOT_CONNECTED || error == ERROR_NO_DATA) &&
            state.stage == State::Stage::reading && state.offset == 0) {
          state.eof = true; state.Stop(); state.PollStop(); return std::nullopt;
        }
        throw ProbeFailure("Control pipe disconnected inside a frame or write; retry is forbidden.");
      }
      state.active = false;
    } else {
      state.ResetIo();
      const auto remaining = static_cast<DWORD>(kFrameSize) - state.offset;
      const BOOL complete = state.stage == State::Stage::writing
          ? WriteFile(state.pipe.value, state.buffer.data() + state.offset, remaining, &count, &state.overlapped)
          : ReadFile(state.pipe.value, state.buffer.data() + state.offset, remaining, &count, &state.overlapped);
      if (!complete) {
        const auto error = GetLastError();
        if ((error == ERROR_BROKEN_PIPE || error == ERROR_PIPE_NOT_CONNECTED || error == ERROR_NO_DATA) &&
            state.stage == State::Stage::reading && state.offset == 0) {
          state.eof = true; state.Stop(); state.PollStop(); return std::nullopt;
        }
        Require(error == ERROR_IO_PENDING, "Cannot start control frame I/O.");
        state.active = true; return std::nullopt;
      }
    }
    if (count == 0 && state.stage == State::Stage::reading && state.offset == 0) {
      state.eof = true; state.Stop(); state.PollStop(); return std::nullopt;
    }
    Require(count > 0 && count <= kFrameSize - state.offset, "Empty or oversized control frame fragment.");
    state.offset += count;
    if (state.offset != kFrameSize) { if (!state.deadline) state.deadline = GetTickCount64() + 5000; return std::nullopt; }
    if (state.stage == State::Stage::writing) {
      ++state.writes; state.buffer = {}; state.offset = 0; state.stage = State::Stage::reading; state.deadline = 0;
      return std::nullopt;
    }
    auto result = state.buffer; state.stage = State::Stage::idle; state.deadline = 0; return result;
  }
 private:
  std::unique_ptr<State> state_;
};
// Explicit P0-only observation page. The controller retains it after the game
// exits; game shutdown publishes only aligned Interlocked words, never file IO.
// It is unrelated to MSC1 and cannot authorize a foreground/exit operation.
class ExitDiagnosticPage {
 public:
  static constexpr size_t kSize = 4096, kHeader = 512;
  ExitDiagnosticPage(const Invocation& invocation, DWORD game_pid, uint64_t game_creation,
                    const std::string& client_hash, const std::string& mod_hash,
                    const std::string& manifest_hash, const std::string& game_hash)
      : invocation_(invocation), game_pid_(game_pid), game_creation_(game_creation) {
    name_ = "Local\\mystia-steward-companion.exitdiag.v3." + std::to_string(game_pid) + "." + Hex(game_creation);
    auto sid = UserSid(GetCurrentProcess()); LPWSTR text = nullptr;
    Require(ConvertSidToStringSidW(sid.data(), &text), "Cannot format diagnostic mapping user.");
    const std::wstring sddl = L"D:P(A;;GA;;;" + std::wstring(text) + L")"; LocalFree(text);
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr),
            "Cannot create current-user diagnostic ACL.");
    SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES), descriptor, FALSE};
    mapping_.value = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0,
        static_cast<DWORD>(kSize), Wide(name_).c_str());
    const auto error = GetLastError(); LocalFree(descriptor);
    Require(mapping_.value && error != ERROR_ALREADY_EXISTS, "Diagnostic mapping is unavailable or already exists.");
    view_.value = MapViewOfFile(mapping_.value, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, kSize);
    Require(view_.value != nullptr, "Cannot map the retained diagnostic page.");
    std::memset(view_.value, 0, kSize);
    auto words = static_cast<uint64_t*>(view_.value);
    words[0] = 0x333030445845434dULL; words[1] = 3; words[2] = kSize; words[3] = kHeader;
    words[4] = game_pid; words[5] = game_creation; words[6] = GetCurrentProcessId(); words[7] = Creation(GetCurrentProcess());
    DWORD session = 0; Require(ProcessIdToSessionId(GetCurrentProcessId(), &session) && session != 0, "Diagnostic session is unknown.");
    words[8] = session;
    Require(BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&words[9]), 16, BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0,
            "Cannot generate the diagnostic instance nonce.");
    words[9] &= 0x7fffffffffffffffULL; words[10] &= 0x7fffffffffffffffULL;
    Require(words[9] && words[10], "Diagnostic instance nonce is invalid."); words[11] = 2;
    auto bytes = static_cast<unsigned char*>(view_.value);
    const auto copy = [&](size_t offset, size_t size, const std::string& value) {
      Require(!value.empty() && value.size() <= size, "Diagnostic header text is invalid.");
      std::memcpy(bytes + offset, value.data(), value.size());
    };
    copy(128, 40, MYSTIA_WINDOW_PROBE_GIT_SHA); copy(168, 80, invocation.run);
    for (const auto& hash : {client_hash, mod_hash, manifest_hash, game_hash}) Require(HashText(hash), "Diagnostic SHA is invalid.");
    copy(248, 64, client_hash); copy(312, 64, mod_hash); copy(376, 64, manifest_hash); copy(440, 64, game_hash);
    std::memcpy(header_.data(), view_.value, kHeader);
  }
  std::string Capture(HANDLE game, const std::vector<DWORD>& clients, const std::vector<uint64_t>& creations) const {
    Require(GetProcessId(game) == game_pid_ && Creation(game) == game_creation_ &&
        std::memcmp(header_.data(), view_.value, kHeader) == 0, "Diagnostic retained process/header identity changed.");
    const auto read = [&](size_t offset) {
      auto pointer = reinterpret_cast<volatile LONG64*>(static_cast<unsigned char*>(view_.value) + offset);
      const auto value = InterlockedCompareExchange64(pointer, 0, 0);
      Require(value >= 0, "Diagnostic atomic value is negative."); return static_cast<uint64_t>(value);
    };
    const auto wait = WaitForSingleObject(game, 0); DWORD exit = STILL_ACTIVE;
    Require((wait == WAIT_OBJECT_0 || wait == WAIT_TIMEOUT) && GetExitCodeProcess(game, &exit), "Cannot inspect diagnostic game lifetime.");
    const auto sequence = read(512), count = read(520), thread = read(528), native_thread = read(536);
    Require(sequence <= 1000000 && count <= 2 && thread <= MAXDWORD && native_thread <= MAXDWORD, "Diagnostic counters exceed their fixed bounds.");
    const std::array<const char*, 19> global = {"attached", "quitEntered", "quitReturned", "destroyEntered", "destroyReturned",
        "disposeEntered", "disposeAlreadyDisposed", "disposeFirst", "disposeReturned", "launcherEntered", "launcherStopping", "launcherSessionMissing",
        "nativeRegistered", "nativeRegistrationFailed", "nativeEntered", "nativeAcknowledged", "nativeUnconfirmed", "nativeFailed", "nativeOriginalInvoked"};
    const std::array<const char*, 16> stages = {"prepared", "clientBound", "notifyEntered", "notifyStopping", "notifyFaulted", "stopSet",
        "queued", "queueRejected", "workerDequeued", "noRegistration", "writeStarted", "writeCompleted", "ackValidated", "failed", "cancelled", "logThrew"};
    bool complete = wait == WAIT_OBJECT_0 && read(544) != 0 && count == 2 && clients.size() == 2 && creations.size() == 2;
    std::ostringstream raw_page; raw_page << std::hex << std::setfill('0');
    const auto page_bytes = static_cast<const unsigned char*>(view_.value);
    for (size_t index = 0; index < kSize; ++index) raw_page << std::setw(2) << static_cast<unsigned>(page_bytes[index]);
    std::ostringstream out;
    out << "{\"schemaVersion\":3,\"kind\":\"control-exit-diagnostic\",\"diagnosticOnly\":true,\"productExitVerified\":false,\"runId\":"
        << Quote(invocation_.run) << ",\"gitSha\":\"" MYSTIA_WINDOW_PROBE_GIT_SHA "\",\"mappingName\":" << Quote(name_)
        << ",\"headerSha256\":" << Quote(TextHash(std::string(reinterpret_cast<const char*>(header_.data()), header_.size())))
        << ",\"pageHex\":" << Quote(raw_page.str())
        << ",\"gamePid\":" << game_pid_ << ",\"gameCreationHex\":" << Quote(Hex(game_creation_))
        << ",\"controllerPid\":" << GetCurrentProcessId() << ",\"controllerCreationHex\":" << Quote(Hex(Creation(GetCurrentProcess())))
        << ",\"gameExited\":" << (wait == WAIT_OBJECT_0 ? "true" : "false") << ",\"gameExitCode\":" << exit
        << ",\"mainThreadId\":" << thread << ",\"nativeExitThreadId\":" << native_thread << ",\"sequence\":" << sequence << ",\"global\":{";
    for (size_t index = 0; index < global.size(); ++index) {
      const auto value = read(544 + index * 8); Require(value <= sequence, "Diagnostic global sequence exceeds its counter.");
      if (index) out << ','; out << Quote(global[index]) << ':' << value;
    }
    out << "},\"sessions\":[";
    for (size_t index = 0; index < 2; ++index) {
      const auto offset = 768 + index * 512;
      const auto number = read(offset), pid = read(offset + 8), creation = read(offset + 16), input_thread = read(offset + 24);
      Require(number <= 2 && pid <= MAXDWORD && input_thread <= MAXDWORD, "Diagnostic session identity exceeds its bounds.");
      const bool matched = index < clients.size() && index < creations.size() && number == index + 1 && pid == clients[index] && creation == creations[index] && input_thread == thread;
      complete = complete && matched;
      if (index) out << ',';
      out << "{\"session\":" << number << ",\"clientPid\":" << pid << ",\"clientCreationHex\":" << Quote(Hex(creation))
          << ",\"inputThreadId\":" << input_thread << ",\"retainedClientMatched\":" << (matched ? "true" : "false") << ",\"stages\":{";
      for (size_t stage = 0; stage < stages.size(); ++stage) {
        const auto value = read(offset + 32 + stage * 8); Require(value <= sequence, "Diagnostic worker sequence exceeds its counter.");
        if (stage) out << ','; out << Quote(stages[stage]) << ':' << value;
      }
      out << "}}";
    }
    out << "],\"captureComplete\":" << (complete ? "true" : "false")
        << ",\"limitations\":[\"P0-instrumented scheduling only; received Exit/ExitAck is not evidence for an ordinary product build.\","
        << "\"Zero stages require attached and matching identities; the ordinary native exit hook has its own bounded completion wait.\"]}\n";
    return out.str();
  }
 private:
  struct View { void* value = nullptr; ~View() { if (value) UnmapViewOfFile(value); } };
  Invocation invocation_; DWORD game_pid_; uint64_t game_creation_;
  std::string name_; Handle mapping_; View view_; std::array<unsigned char, kHeader> header_{};
};
}  // namespace

struct ControlProbeBridge::Impl {
  HWND top, child, game_window = nullptr;
  DWORD owner_thread, game_pid = 0, game_thread = 0, session = MAXDWORD;
  const DWORD owner_pid = GetCurrentProcessId();
  const uint64_t owner_creation = Creation(GetCurrentProcess());
  Invocation invocation;
  bool invocation_valid = false, initialized = false, subclassed = false, finishing = false;
  bool identity_matched = false, game_resumed = false, startup_terminated = false;
  bool parent_in_job = false, game_in_job = false, winsock_started = false, mode_pass = false;
  bool registered = false, exit_received = false, activation_pending = false, return_pending = false;
  bool activation_confirmation_pending = false;
  bool child_focus_requested = false, last_flutter_focused = false, last_client_foreground = false;
  bool injected_f8_held = false, rs_armed = false;
  bool legacy_ready = false, legacy_startup_attempted = false, legacy_startup_foreground = false;
  bool legacy_click_required = false;
  uint64_t legacy_show_count = 0, legacy_toggle_count = 0, legacy_exit_count = 0;
  uint64_t legacy_click_count = 0, legacy_background_clicks = 0, legacy_peer_deadline = 0, legacy_startup_deadline = 0;
  std::string legacy_bytes;
  bool activation_hidden = false, activation_pass = false, rs_held_verified = false;
  bool cursor_saved = false, cursor_injected = false, cursor_restored = false;
  bool close_requested = false, close_send_result = false, close_foreground_required = false, close_foreground_matched = false;
  bool foreground_result = false;
  DWORD foreground_error = 0, send_error = 0, close_send_error = 0;
  DWORD selected_slot = MAXDWORD;
  uint64_t game_creation = 0, nonce_low = 0, nonce_high = 0, deadline = 0, startup_deadline = 0;
  uint64_t finish_deadline = 0, registration_count = 0, activation_count = 0, activation_attempts = 0;
  uint64_t protocol_request = 0, input_sequence = 0, source = 0, injected_down_count = 0, injected_up_count = 0;
  uint64_t focus_game_count = 0, f8_return_count = 0, rs_return_count = 0, raw_unsupported_count = 0;
  uint64_t f8_activation_count = 0, rs_activation_count = 0, hidden_recovery_count = 0, pass_recovery_count = 0;
  uint64_t rs_held_started = 0, rs_held_last = 0, rs_held_duration = 0;
  uint64_t native_f8_down = 0, native_f8_up = 0, raw_f8_down = 0, raw_f8_up = 0;
  uint64_t marker_f8_down = 0, marker_f8_up = 0, native_f24_down = 0, native_mouse_down = 0, native_mouse_up = 0;
  uint64_t consumed_f8_down = 0, rs_edge_count = 0, event_sequence = 0, close_send_attempts = 0;
  int64_t command_id = 0, snapshot_sequence = 0, last_dart_f8 = 0;
  uint32_t marker = 0;
  UINT send_requested = 0, send_inserted = 0;
  std::string error, cleanup_error, pipe_name, rs_unavailable_reason;
  bool error_blocked = false;
  std::string game_hash, unity_hash, metadata_hash, assembly_hash, prepared_hash, mod_hash, mod_build_hash, bep_hash, client_hash;
  std::wstring game_path, game_root;
  std::vector<unsigned char> user_sid;
  Handle game;
  Socket listener;
  Socket legacy_peer;
  std::unique_ptr<ControlPipe> pipe;
  std::unique_ptr<LegacySnapshotProbe> legacy_snapshot;
  std::vector<Handle> pinned_files;
  std::vector<std::string> events;
  Frame registration{}, pending_activation{}, pending_ack{};
  PendingForegroundNullObservations activation_null_foreground, return_null_foreground;
  int pending_return_kind = 0;
  std::array<XINPUT_STATE, XUSER_MAX_COUNT> slots{};
  std::array<DWORD, XUSER_MAX_COUNT> slot_errors{};
  HMODULE xinput = nullptr;
  using GetState = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);
  GetState get_state = nullptr;
  std::string xinput_path;
  HWND close_target = nullptr, close_foreground = nullptr, close_focus = nullptr;
  DWORD close_target_pid = 0, close_foreground_pid = 0;
  std::shared_ptr<CloseReceipt> close_receipt;
  ULONG_PTR close_receipt_token = 0;
  POINT original_cursor{}, last_cursor{};
  int finish_code = 1;
  std::function<void(std::optional<FlutterError>)> finish_reply;

  Impl(HWND owner, flutter::FlutterViewController* controller, const std::vector<std::string>& args)
      : top(owner), child(controller->view()->GetNativeWindow()), owner_thread(GetCurrentThreadId()) {
    try {
      invocation = ParseRuntimeInvocation(args); invocation_valid = true;
      if (invocation.legacy) legacy_snapshot = std::make_unique<LegacySnapshotProbe>();
      OwnWindows();
      Require(ProcessIdToSessionId(owner_pid, &session), "Cannot identify the client console session.");
      user_sid = UserSid(GetCurrentProcess());
      Require(BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&marker), sizeof(marker), BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0,
              "Cannot create the control input marker.");
      marker &= 0x7fffffff; Require(marker != 0, "Control input marker is zero.");
      std::array<uint64_t, 2> nonce{};
      Require(BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(nonce.data()), static_cast<ULONG>(sizeof(nonce)),
                             BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0 && nonce[0] && nonce[1], "Cannot create the identity nonce.");
      nonce_low = nonce[0]; nonce_high = nonce[1];
      Require(SetWindowSubclass(child, ChildProc, kSubclass, reinterpret_cast<DWORD_PTR>(this)),
              "Cannot observe exact Flutter child input.");
      subclassed = true; ApplyMode(false); LoadXInput();
      Require(SetTimer(top, kPollTimer, 16, nullptr) != 0, "Cannot schedule native control observation.");
    } catch (const std::exception& failure) { RememberControlFailure(failure, error, error_blocked); }
  }
  ~Impl() {
    KillTimer(top, kPollTimer); KillTimer(top, kFinishTimer);
    if (close_receipt_token) close_receipts.erase(close_receipt_token);
    if (injected_f8_held) ReleaseInjectedKey(true);
    try { RestoreCursor(); } catch (...) {}
    if (subclassed && IsWindow(child)) RemoveWindowSubclass(child, ChildProc, kSubclass);
    pipe.reset();
    legacy_snapshot.reset();
    if (legacy_peer.value != INVALID_SOCKET) { closesocket(legacy_peer.value); legacy_peer.value = INVALID_SOCKET; }
    if (listener.value != INVALID_SOCKET) { closesocket(listener.value); listener.value = INVALID_SOCKET; }
    if (winsock_started) WSACleanup();
    if (xinput) FreeLibrary(xinput);
    if (finish_reply) { auto reply = std::move(finish_reply); reply(FlutterError("closed", "Control probe closed before cleanup.")); }
  }
  void OwnWindows() const {
    Require(IsWindow(top) && IsWindow(child) && WindowPid(top) == owner_pid && WindowPid(child) == owner_pid &&
                GetAncestor(top, GA_ROOT) == top && GetAncestor(child, GA_ROOT) == top &&
                GetWindowThreadProcessId(top, nullptr) == owner_thread && GetWindowThreadProcessId(child, nullptr) == owner_thread,
            "Owned client HWND/PID/thread identity changed.");
  }
  void Event(const char* name, const std::string& detail = "{}") {
    Require(events.size() < 256, "Control evidence event count exceeded its fixed bound.");
    std::ostringstream out;
    out << "{\"sequence\":" << ++event_sequence << ",\"ticks\":" << GetTickCount64() << ",\"kind\":" << Quote(name)
        << ",\"detail\":" << detail << '}';
    events.push_back(out.str());
  }
  bool GameAlive() const {
    if (!game.value) return false;
    const DWORD status = WaitForSingleObject(game.value, 0);
    Require(status == WAIT_TIMEOUT || status == WAIT_OBJECT_0, "Cannot observe retained game process state.");
    return status == WAIT_TIMEOUT;
  }
  DWORD GameExitCode() const {
    Require(game.value && !GameAlive(), "Retained game has not exited.");
    DWORD code = 0; Require(GetExitCodeProcess(game.value, &code), "Cannot read retained game exit code."); return code;
  }
  bool ObservingGameExit() const { return close_requested && close_send_attempts == 1 && close_send_result; }
  void CheckGameIdentity() const {
    Require(game.value && identity_matched && GetProcessId(game.value) == game_pid && Creation(game.value) == game_creation,
            "Retained game HANDLE/PID/creation binding changed.");
  }
  void CheckGame() const {
    CheckGameIdentity();
    Require(GameAlive() && SamePath(ProcessPath(game.value), game_path), "Live game process path or state changed.");
    DWORD actual_session = MAXDWORD;
    Require(ProcessIdToSessionId(game_pid, &actual_session) && actual_session == session, "Live game console session changed.");
  }
  struct Candidates { DWORD pid; std::vector<HWND> windows; };
  static BOOL CALLBACK Enumerate(HWND window, LPARAM reference) {
    auto& found = *reinterpret_cast<Candidates*>(reference);
    if (WindowPid(window) != found.pid || !IsWindowVisible(window) || GetWindow(window, GW_OWNER)) return TRUE;
    wchar_t name[128]{}; RECT client{};
    if (GetClassNameW(window, name, static_cast<int>(std::size(name))) && wcscmp(name, L"UnityWndClass") == 0 &&
        GetClientRect(window, &client) && client.right > client.left && client.bottom > client.top) found.windows.push_back(window);
    return TRUE;
  }
  std::vector<HWND> GameWindows() const {
    Candidates found{game_pid, {}};
    Require(EnumWindows(Enumerate, reinterpret_cast<LPARAM>(&found)), "Cannot enumerate exact game windows.");
    return found.windows;
  }
  void BindOrCheckGameWindow() {
    CheckGame();
    const auto found = GameWindows();
    Require(found.size() <= 1, "Game owns multiple eligible Unity windows.");
    if (!game_window) {
      if (found.empty()) return;
      game_window = found[0]; game_thread = GetWindowThreadProcessId(game_window, nullptr);
      Require(game_thread != 0, "Cannot bind the unique game GUI thread.");
      Event("game-window-bound");
    }
    Require(found.size() == 1 && found[0] == game_window && WindowPid(game_window) == game_pid &&
                GetWindowThreadProcessId(game_window, nullptr) == game_thread, "Bound game window disappeared or changed.");
  }
  void BoundGameWindow() {
    Require(!close_requested, "Game window cannot be targeted after its sole close attempt.");
    BindOrCheckGameWindow(); Require(game_window != nullptr, "The unique game window is not yet available.");
  }
  bool OwnedGameFocus(HWND focus) const {
    return focus && IsWindow(focus) && WindowPid(focus) == game_pid &&
        GetWindowThreadProcessId(focus, nullptr) == game_thread && GetAncestor(focus, GA_ROOT) == game_window;
  }
  void GameForegroundGuard() {
    DesktopGuard(); OwnWindows(); BoundGameWindow();
    Available(GetForegroundWindow() == game_window && WindowPid(game_window) == game_pid &&
                  OwnedGameFocus(ThreadInfo(game_thread).hwndFocus), "Exact game does not own actual foreground/thread focus.");
  }
  void ClientForegroundGuard(bool require_dart = true) {
    DesktopGuard(); OwnWindows(); BoundGameWindow();
    Available(GetForegroundWindow() == top && GetFocus() == child && IsWindowVisible(top) && !mode_pass &&
                  (!require_dart || last_flutter_focused), "Exact interactive Flutter child does not own foreground/focus.");
  }
  void ApplyMode(bool pass) {
    OwnWindows();
    auto style = GetWindowLongPtrW(top, GWL_EXSTYLE) | WS_EX_LAYERED;
    if (pass) style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    else style &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    SetLastError(ERROR_SUCCESS);
    const auto old = SetWindowLongPtrW(top, GWL_EXSTYLE, style);
    Require(old != 0 || GetLastError() == ERROR_SUCCESS, "Cannot apply client input mode.");
    Require(SetLayeredWindowAttributes(top, 0, 255, LWA_ALPHA), "Cannot retain opaque probe pixels.");
    constexpr LONG_PTR mask = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    Require((GetWindowLongPtrW(top, GWL_EXSTYLE) & mask) == (style & mask), "Client mode readback differs.");
    mode_pass = pass;
  }
  void LoadXInput() {
    wchar_t system[32768]{}; const auto length = GetSystemDirectoryW(system, static_cast<UINT>(std::size(system)));
    Require(length > 0 && length < std::size(system), "Cannot identify the system DLL directory.");
    const auto path = std::wstring(system, length) + L"\\XInput1_4.dll";
    PlainPath(path, false);
    xinput = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    Available(xinput != nullptr && SamePath(ModulePath(xinput), path), "Exact system XInput1_4.dll is unavailable.");
    const auto address = GetProcAddress(xinput, "XInputGetState");
    static_assert(sizeof(address) == sizeof(get_state));
    std::memcpy(&get_state, &address, sizeof(get_state));
    Require(get_state != nullptr, "System XInputGetState export is unavailable."); xinput_path = Utf8(path);
    slot_errors.fill(ERROR_DEVICE_NOT_CONNECTED);
  }
  void StartListener() {
    WSADATA data{};
    Require(WSAStartup(MAKEWORD(2, 2), &data) == 0, "Cannot initialize Winsock."); winsock_started = true;
    Available(ListenerOwner() == 0, "Port 32146 is already occupied; the existing listener must not be displaced.");
    listener.value = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    Require(listener.value != INVALID_SOCKET, "Cannot create the legacy reservation socket.");
    const BOOL exclusive = TRUE;
    Require(setsockopt(listener.value, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive)) == 0,
            "Cannot exclusively reserve the real legacy port.");
    sockaddr_in address{}; address.sin_family = AF_INET; address.sin_addr.s_addr = htonl(INADDR_LOOPBACK); address.sin_port = htons(32146);
    Available(bind(listener.value, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == 0,
              "Port 32146 became occupied before exclusive bind; no existing listener was changed.");
    // In the MSC1 scenario publish only after its identity pipe exists. The
    // explicitly authorized original-Mod scenario has no identity pipe.
    if (!invocation.legacy) {
      pipe_name = "mystia-steward-companion.control.v1." + std::to_string(owner_pid) + "." + Hex(owner_creation);
      pipe = std::make_unique<ControlPipe>(pipe_name, user_sid);
    }
    Require(listen(listener.value, 4) == 0, "Cannot listen on the exclusively owned legacy port.");
    u_long nonblocking = 1;
    Require(ioctlsocket(listener.value, FIONBIO, &nonblocking) == 0 && ListenerOwner() == owner_pid,
            "Actual control listener owner does not match the current retained client.");
    Event("listener-bound", "{\"port\":32146,\"ownerPid\":" + std::to_string(owner_pid) + "}");
  }
  void PollLegacy() {
    if (invocation.legacy) { PollLegacyControl(); return; }
    if (listener.value == INVALID_SOCKET) return;
    for (int attempt = 0; attempt < 4; ++attempt) {
      sockaddr_in address{}; int size = sizeof(address);
      Socket peer; peer.value = accept(listener.value, reinterpret_cast<sockaddr*>(&address), &size);
      if (peer.value == INVALID_SOCKET) { Require(WSAGetLastError() == WSAEWOULDBLOCK, "Legacy reservation accept failed."); return; }
      Require(size == static_cast<int>(sizeof(address)) && address.sin_family == AF_INET && address.sin_addr.s_addr == htonl(INADDR_LOOPBACK),
              "Non-loopback connection reached the legacy reservation.");
      ++raw_unsupported_count;
      Event("legacy-unsupported");  // Never authorize or mutate identity from untrusted raw TCP bytes.
    }
  }
  bool ControlReady() const { return invocation.legacy ? legacy_ready : registered; }
  void LegacyDiscoverable() {
    DesktopGuard(); OwnWindows(); BoundGameWindow(); ApplyMode(false);
    ShowWindow(top, SW_SHOWNOACTIVATE);
    Require(SetWindowPos(top, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE),
            "Cannot expose the exact legacy recovery window without activating it.");
    Require(IsWindowVisible(top), "Legacy recovery window is not visible.");
  }
  void PollLegacyControl() {
    if (listener.value == INVALID_SOCKET) return;
    CheckGameIdentity();
    Require(ListenerOwner() == owner_pid, "Legacy fixture listener ownership changed.");
    if (legacy_peer.value == INVALID_SOCKET) {
      sockaddr_in address{}; int size = sizeof(address);
      legacy_peer.value = accept(listener.value, reinterpret_cast<sockaddr*>(&address), &size);
      if (legacy_peer.value == INVALID_SOCKET) { Require(WSAGetLastError() == WSAEWOULDBLOCK, "Legacy accept failed."); return; }
      Require(size == sizeof(address) && address.sin_family == AF_INET && address.sin_addr.s_addr == htonl(INADDR_LOOPBACK),
              "Legacy fixture received a non-loopback connection.");
      u_long nonblocking = 1;
      Require(ioctlsocket(legacy_peer.value, FIONBIO, &nonblocking) == 0, "Cannot bound legacy receive.");
      legacy_bytes.clear(); legacy_peer_deadline = GetTickCount64() + 5000;
    }
    Require(GetTickCount64() < legacy_peer_deadline, "Legacy EOF receive exceeded five seconds.");
    std::array<char, 1025> bytes{};
    const int count = recv(legacy_peer.value, bytes.data(), static_cast<int>(bytes.size()), 0);
    if (count == SOCKET_ERROR) { Require(WSAGetLastError() == WSAEWOULDBLOCK, "Legacy receive failed."); return; }
    if (count > 0) {
      Require(legacy_bytes.size() + static_cast<size_t>(count) <= 1024, "Legacy wire exceeds 1024 bytes.");
      legacy_bytes.append(bytes.data(), static_cast<size_t>(count)); return;
    }
    const auto action = ParseLegacyControl(legacy_bytes, game_pid, invocation.token_hash);
    const auto wire_hash = TextHash(legacy_bytes);
    closesocket(legacy_peer.value); legacy_peer.value = INVALID_SOCKET; legacy_bytes.clear();
    if (action == 1) {
      Require(legacy_show_count == 0 && legacy_toggle_count == 0 && !close_requested, "Legacy startup show was replayed or late.");
      ++legacy_show_count;
    } else if (action == 2) {
      Require(legacy_ready && legacy_toggle_count == 0 && !close_requested && injected_f8_held && !return_pending,
              "Legacy existing-instance toggle lacks the sole owned F8 action.");
      GameForegroundGuard(); LegacyDiscoverable(); GameForegroundGuard();
      ++legacy_toggle_count; legacy_click_required = true;
      // An old sender cannot authorize this already-running process. Do not
      // retry/forge foreground grants; its discoverable window awaits a click.
    } else {
      Require(close_requested && legacy_exit_count == 0, "Unauthenticated legacy exit is only observed after our exact close.");
      ++legacy_exit_count;
    }
    Event("legacy-wire-received", "{\"action\":" + std::to_string(action) + ",\"wireSha256\":" + Quote(wire_hash) +
        ",\"eofObserved\":true,\"ackSent\":false}");
  }
  void ObserveLegacyStartup() {
    if (legacy_ready || legacy_show_count == 0 || !game_window) return;
    DesktopGuard(); OwnWindows(); BoundGameWindow();
    if (!legacy_startup_attempted) {
      LegacyDiscoverable(); legacy_startup_attempted = true; legacy_startup_deadline = GetTickCount64() + 5000;
      SetLastError(ERROR_SUCCESS); foreground_result = SetForegroundWindow(top) != FALSE; foreground_error = GetLastError();
      SetFocus(child);
      Event("legacy-startup-foreground-attempt", "{\"setForegroundResult\":" + std::string(foreground_result ? "true" : "false") +
          ",\"error\":" + std::to_string(foreground_error) + ",\"asfwCalled\":false}");
    }
    const auto foreground = GetForegroundWindow();
    Available(!foreground || foreground == top || foreground == game_window, "An unrelated window interrupted legacy startup observation.");
    if (foreground == top && GetFocus() == child) { legacy_startup_foreground = true; legacy_ready = true; }
    else if (GetTickCount64() >= legacy_startup_deadline) {
      GameForegroundGuard(); legacy_click_required = true; legacy_ready = true;
    }
    if (legacy_ready) Event("legacy-startup-foreground-observed", "{\"automaticForeground\":" + std::string(legacy_startup_foreground ? "true" : "false") +
        ",\"clickRequired\":" + std::string(legacy_click_required ? "true" : "false") + "}");
  }
  void PinHash(const std::wstring& path, const std::string& expected, std::string& actual) {
    Require(HashText(expected), "Sidecar hash is not canonical lowercase SHA-256.");
    auto file = OpenPinned(path); actual = Sha256(file.value);
    Require(actual == expected, "Prepared game/candidate identity file hash differs."); pinned_files.push_back(std::move(file));
  }
  void StartGame() {
    Require(!initialized && !game.value, "Control game initialization cannot be replayed.");
    initialized = true; DesktopGuard();
    auto sidecar = OpenPinned(invocation.root + L"\\control-probe.json");
    const auto values = SidecarParser(ReadSmall(sidecar.value, 32768)).Parse(); pinned_files.push_back(std::move(sidecar));
    Require(values.at("runId") == invocation.run && values.at("gitSha") == MYSTIA_WINDOW_PROBE_GIT_SHA &&
                values.at("steamAppId") == "1584090" && values.at("steamBuildId") == "23158340", "Control sidecar run/build/Steam identity differs.");
    Require(!invocation.legacy || values.at("expectedModSha256") == kOriginalModSha, "Legacy mode requires the unchanged physical Mod 1.3.1 DLL.");
    game_root = invocation.root + L"\\workspace\\game"; game_path = game_root + L"\\" + kGameExecutable;
    auto supplied = Wide(values.at("gameExecutable")); std::replace(supplied.begin(), supplied.end(), L'/', L'\\');
    Require(SamePath(supplied, game_path), "Control sidecar points outside the fixed copied game."); PlainPath(game_root, true);
    PinHash(invocation.root + L"\\workspace\\prepared-evidence.json", values.at("preparedEvidenceSha256"), prepared_hash);
    PinHash(game_path, values.at("expectedExeSha256"), game_hash);
    PinHash(game_root + L"\\UnityPlayer.dll", values.at("expectedUnityPlayerSha256"), unity_hash);
    PinHash(game_root + L"\\GameAssembly.dll", values.at("expectedGameAssemblySha256"), assembly_hash);
    PinHash(game_root + L"\\Touhou Mystia Izakaya_Data\\il2cpp_data\\Metadata\\global-metadata.dat", values.at("expectedMetadataSha256"), metadata_hash);
    PinHash(game_root + L"\\BepInEx\\plugins\\mystia-steward-companion\\MystiaStewardCompanion.BepInEx.dll", values.at("expectedModSha256"), mod_hash);
    PinHash(game_root + L"\\BepInEx\\core\\BepInEx.Unity.IL2CPP.dll", values.at("expectedBepInExSha256"), bep_hash);
    PinHash(invocation.root + L"\\workspace\\mod-build-evidence.json", values.at("modBuildEvidenceSha256"), mod_build_hash);
    auto own_file = OpenPinned(ModulePath()); client_hash = Sha256(own_file.value); pinned_files.push_back(std::move(own_file));
    auto appid = OpenPinned(game_root + L"\\steam_appid.txt"); const auto text = ReadSmall(appid.value, 9);
    Require(text == "1584090" || text == "1584090\n" || text == "1584090\r\n", "Copied game App ID bytes differ.");
    pinned_files.push_back(std::move(appid));
    BOOL own_job = FALSE;
    Require(IsProcessInJob(GetCurrentProcess(), nullptr, &own_job) && own_job, "Control host must belong to the node cleanup job.");
    parent_in_job = true;
    if (invocation.client) {
      game_pid = invocation.expected_game_pid;
      game = Handle(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, game_pid));
      Require(game.value != nullptr, "Cannot retain the controller-bound game process.");
      game_creation = Creation(game.value);
      DWORD actual_session = MAXDWORD; BOOL in_job = FALSE;
      const auto sid = UserSid(game.value);
      Require(game_creation == invocation.expected_game_creation && GetProcessId(game.value) == game_pid &&
                  WaitForSingleObject(game.value, 0) == WAIT_TIMEOUT && SamePath(ProcessPath(game.value), game_path) &&
                  ProcessIdToSessionId(game_pid, &actual_session) && actual_session == session &&
                  IsProcessInJob(game.value, nullptr, &in_job) && in_job &&
                  EqualSid(user_sid.data(), const_cast<unsigned char*>(sid.data())), "Actual cold-client game identity differs.");
      game_in_job = true; identity_matched = true; game_resumed = true;
      StartListener();
      startup_deadline = GetTickCount64() + 120000;
      Event("mod-launched-client-attached", "{\"generation\":" + std::to_string(invocation.generation) +
          ",\"parentPid\":" + std::to_string(game_pid) + ",\"gameCreationHex\":" + Quote(Hex(game_creation)) + "}");
      return;
    }
    StartListener();
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION info{};
    auto command = L"\"" + game_path + L"\"";
    Require(CreateProcessW(game_path.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr,
                           game_root.c_str(), &startup, &info), "Cannot create the exact copied game suspended.");
    game = Handle(info.hProcess); Handle thread(info.hThread); game_pid = info.dwProcessId;
    try {
      game_creation = Creation(game.value);
      DWORD actual_session = MAXDWORD; BOOL in_job = FALSE;
      const auto sid = UserSid(game.value);
      Require(GetProcessId(game.value) == game_pid && SamePath(ProcessPath(game.value), game_path) &&
                  ProcessIdToSessionId(game_pid, &actual_session) && actual_session == session &&
                  IsProcessInJob(game.value, nullptr, &in_job) && in_job &&
                  EqualSid(user_sid.data(), const_cast<unsigned char*>(sid.data())), "Suspended game path/user/session/job binding differs.");
      game_in_job = true; identity_matched = true;
      Require(ResumeThread(thread.value) == 1, "Cannot resume the sole suspended copied game thread.");
      game_resumed = true;
    } catch (...) {
      if (!game_resumed) startup_terminated = TerminateProcess(game.value, 1) != FALSE;
      throw;
    }
    startup_deadline = GetTickCount64() + 120000;
    Event("game-created", "{\"pid\":" + std::to_string(game_pid) + ",\"creationHex\":" + Quote(Hex(game_creation)) + "}");
  }
  void CheckPeer(bool closing) {
    Require(pipe && pipe->connected(), "Identity control pipe has no connected peer.");
    CheckGameIdentity();
    if (!closing) BoundGameWindow();
    ULONG actual_pid = 0;
    Require(GetNamedPipeClientProcessId(pipe->handle(), &actual_pid) && actual_pid == game_pid,
            "Actual pipe client PID differs from the retained copied game.");
    Require(ListenerOwner() == owner_pid, "The identity pipe server no longer owns the exact raw control listener.");
    OwnWindows();
  }
  void CheckFrameIdentity(const Frame& frame, bool with_nonce) const {
    Require(Get(frame, 0) == (with_nonce ? nonce_low : 0) && Get(frame, 1) == (with_nonce ? nonce_high : 0) &&
                Get(frame, 2) == game_pid && Get(frame, 3) == game_creation &&
                Get(frame, 4) == owner_pid && Get(frame, 5) == owner_creation &&
                Get(frame, 8) == static_cast<uint64_t>(WindowValue(game_window)) && Get(frame, 10) == game_thread &&
                Get(frame, 20) == game_thread, "Control frame differs from retained process/window/input-thread identities.");
    if (with_nonce)
      Require(Get(frame, 9) == static_cast<uint64_t>(WindowValue(top)) && Get(frame, 11) == owner_thread,
              "Control frame targets a different registered client window/thread.");
    else Zero(frame, {9, 11});
  }
  void HandleFrame(const Frame& frame) {
    const auto kind = Kind(frame);
    CheckPeer(ObservingGameExit());
    if (!registered) {
      Require(kind == 1 && !close_requested, "First control frame must be registration.");
      CheckFrameIdentity(frame, false);
      Zero(frame, {6, 7, 12, 13, 14, 15, 16, 17, 18, 19, 21, 22, 23});
      const auto sid = UserSid(game.value);
      Require(EqualSid(user_sid.data(), const_cast<unsigned char*>(sid.data())), "Pipe peer process user does not match current user.");
      registration = frame;
      auto reply = frame; Header(reply, 2); Put(reply, 0, nonce_low); Put(reply, 1, nonce_high);
      Put(reply, 9, static_cast<uint64_t>(WindowValue(top))); Put(reply, 11, owner_thread); Put(reply, 23, 1);
      pipe->Write(reply); registered = true; ++registration_count;
      Event("registered", "{\"requestFrameHex\":" + Quote(FrameHex(frame)) + ",\"replyFrameHex\":" + Quote(FrameHex(reply)) + "}");
      return;
    }
    CheckFrameIdentity(frame, true);
    if (kind == 7) {
      Require(activation_pending && activation_confirmation_pending && !close_requested && GetTickCount64() < deadline,
              "Activation consumption confirmation is unsolicited, repeated or late.");
      for (size_t index = 0; index < 24; ++index)
        Require(Get(frame, index) == Get(pending_ack, index), "Activation confirmation does not exactly echo the applied acknowledgement.");
      Require(Get(frame, 23) == 1, "A rejected activation cannot be confirmed.");
      ClientForegroundGuard(false);
      pipe->ContinueRead(); activation_pending = false; activation_confirmation_pending = false;
      ++activation_count;
      if (source == 1) ++f8_activation_count;
      if (source == 2) ++rs_activation_count;
      if (activation_hidden) ++hidden_recovery_count;
      if (activation_pass) ++pass_recovery_count;
      Event("activation-consumed", "{\"confirmationFrameHex\":" + Quote(FrameHex(frame)) + "}");
      return;
    }
    Require(Get(frame, 6) == protocol_request + 1 && Get(frame, 6) <= 1000 && !exit_received,
            "Control request ID is repeated, skipped, exhausted or after Exit.");
    if (kind == 5) {
      Require(Get(frame, 7) == 3 && !activation_pending && !return_pending,
              "Exit control frame overlaps an activation/return operation.");
      Zero(frame, {12, 13, 14, 15, 16, 17, 18, 19, 21, 22, 23});
      protocol_request = Get(frame, 6); exit_received = true;
      auto reply = frame; Header(reply, 6); Put(reply, 23, 1); pipe->Write(reply);
      Event("exit-acknowledged", "{\"requestFrameHex\":" + Quote(FrameHex(frame)) + ",\"replyFrameHex\":" + Quote(FrameHex(reply)) + "}");
      return;
    }
    Require(kind == 3 && !close_requested && !activation_pending && !return_pending,
            "Control activation is unknown, overlapping, or after close.");
    Zero(frame, {14, 15, 21, 22, 23});
    Require(Get(frame, 7) <= 2 && Get(frame, 16) == 1 && Get(frame, 17) == 1 && Get(frame, 18) <= MAXDWORD &&
                Get(frame, 12) == static_cast<uint64_t>(WindowValue(game_window)) && Get(frame, 13) == game_pid,
            "Activation lacks the exact successful game foreground grant.");
    const auto next_source = Get(frame, 7), next_input = Get(frame, 19);
    Require((next_source == 0 && Get(frame, 6) == 1 && next_input == 0) ||
                (next_source != 0 && next_input > input_sequence && next_input <= static_cast<uint64_t>(std::numeric_limits<int64_t>::max())),
            "Activation input sequence/source is stale or invalid.");
    GameForegroundGuard();
    protocol_request = Get(frame, 6); source = next_source; input_sequence = next_input;
    pending_activation = frame;
    activation_hidden = IsWindowVisible(top) == FALSE; activation_pass = mode_pass;
    ApplyMode(false); ShowWindow(top, SW_SHOWNOACTIVATE);
    // A valid historical ASFW report does not replace the last live guard.
    GameForegroundGuard();
    activation_pending = true; child_focus_requested = false; deadline = GetTickCount64() + 5000; ++activation_attempts;
    rs_armed = false;
    SetLastError(ERROR_SUCCESS); foreground_result = SetForegroundWindow(top) != FALSE; foreground_error = GetLastError();
    Event("activation-sent", "{\"requestFrameHex\":" + Quote(FrameHex(frame)) + ",\"setForegroundResult\":" +
          (foreground_result ? "true" : "false") + ",\"error\":" + std::to_string(foreground_error) + "}");
  }
  void ObserveActivation() {
    if (!activation_pending) return;
    if (activation_confirmation_pending) {
      Available(GetTickCount64() < deadline, "The real Mod did not confirm consuming its applied activation within three seconds.");
      return;
    }
    DesktopGuard(); OwnWindows(); BoundGameWindow();
    const auto foreground = GetForegroundWindow();
    if (!PendingForegroundKnown(foreground, game_window, top, GetTickCount64(), deadline, activation_null_foreground,
                                "An unrelated foreground window interrupted activation.",
                                "Foreground remained unknown until the client activation deadline.")) return;
    if (foreground == top && !child_focus_requested) {
      child_focus_requested = true; SetFocus(child);
    }
    const bool applied = foreground == top && GetFocus() == child && IsWindowVisible(top) && !mode_pass;
    if (!applied && GetTickCount64() < deadline) return;
    auto reply = pending_activation; Header(reply, 4);
    Put(reply, 14, static_cast<uint64_t>(WindowValue(foreground))); Put(reply, 15, WindowPid(foreground));
    Put(reply, 21, static_cast<uint64_t>(WindowValue(GetFocus())));
    Put(reply, 22, (IsWindowVisible(top) ? uint64_t{1} : 0) | (!mode_pass ? uint64_t{2} : 0));
    Put(reply, 23, applied ? 1 : 2); pipe->Write(reply); pending_ack = reply;
    Event(applied ? "activation-applied" : "activation-rejected", "{\"replyFrameHex\":" + Quote(FrameHex(reply)) + "}");
    if (applied) {
      activation_confirmation_pending = true; deadline = GetTickCount64() + 3000;
    } else {
      activation_pending = false;
      error = "The sole authorized foreground request did not reach the actual Flutter child within five seconds.";
    }
  }
  void BeginReturn(int kind) {
    Require(ControlReady() && !activation_pending && !return_pending && !close_requested && !exit_received,
            "Game handoff is unready, repeated or overlaps another action.");
    if (kind != 1) ClientForegroundGuard();
    else {
      DesktopGuard(); OwnWindows(); BoundGameWindow();
      Available(GetForegroundWindow() == top || GetForegroundWindow() == game_window,
                "An unrelated foreground window prevents the explicit setup handoff.");
    }
    return_pending = true; pending_return_kind = kind; deadline = GetTickCount64() + 5000; rs_armed = false;
    SetLastError(ERROR_SUCCESS); foreground_result = SetForegroundWindow(game_window) != FALSE; foreground_error = GetLastError();
    Event("game-handoff-sent", "{\"source\":" + std::to_string(kind) + ",\"setForegroundResult\":" +
          (foreground_result ? "true" : "false") + ",\"error\":" + std::to_string(foreground_error) + "}");
  }
  void ObserveReturn() {
    if (!return_pending) return;
    DesktopGuard(); OwnWindows(); BoundGameWindow();
    const auto foreground = GetForegroundWindow();
    if (!PendingForegroundKnown(foreground, top, game_window, GetTickCount64(), deadline, return_null_foreground,
                                "An unrelated foreground window interrupted game handoff.",
                                "Foreground remained unknown until the game handoff deadline.")) return;
    if (foreground == game_window && OwnedGameFocus(ThreadInfo(game_thread).hwndFocus)) {
      return_pending = false;
      if (pending_return_kind == 1) ++focus_game_count;
      else if (pending_return_kind == 2) ++f8_return_count;
      else if (pending_return_kind == 3) ++rs_return_count;
      Event("game-handoff-observed", "{\"source\":" + std::to_string(pending_return_kind) + "}");
      if (invocation.legacy && pending_return_kind == 1 && focus_game_count == 1) {
        Require(legacy_snapshot != nullptr, "Legacy snapshot observer is unavailable.");
        legacy_snapshot->Start(invocation.legacy_snapshot_token, game_pid);
        std::fill(invocation.legacy_snapshot_token.begin(), invocation.legacy_snapshot_token.end(), '\0');
        invocation.legacy_snapshot_token.clear();
        Event("legacy-post-focus-publication-started", legacy_snapshot->Json());
      }
      return;
    }
    Available(GetTickCount64() < deadline, "The sole game handoff did not reach exact game foreground/focus within five seconds.");
  }
  static bool Neutral(const XINPUT_GAMEPAD& state) {
    return state.wButtons == 0 && state.bLeftTrigger <= XINPUT_GAMEPAD_TRIGGER_THRESHOLD &&
        state.bRightTrigger <= XINPUT_GAMEPAD_TRIGGER_THRESHOLD &&
        state.sThumbLX >= -XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE && state.sThumbLX <= XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE &&
        state.sThumbLY >= -XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE && state.sThumbLY <= XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE &&
        state.sThumbRX >= -XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE && state.sThumbRX <= XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE &&
        state.sThumbRY >= -XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE && state.sThumbRY <= XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE;
  }
  std::string SlotJson(DWORD index) const {
    const auto& value = slots[index]; const auto& pad = value.Gamepad;
    std::ostringstream out;
    out << "{\"index\":" << index << ",\"error\":" << slot_errors[index] << ",\"packet\":" << value.dwPacketNumber
        << ",\"buttons\":" << pad.wButtons << ",\"leftTrigger\":" << static_cast<unsigned int>(pad.bLeftTrigger)
        << ",\"rightTrigger\":" << static_cast<unsigned int>(pad.bRightTrigger) << ",\"thumbLX\":" << pad.sThumbLX
        << ",\"thumbLY\":" << pad.sThumbLY << ",\"thumbRX\":" << pad.sThumbRX << ",\"thumbRY\":" << pad.sThumbRY << '}';
    return out.str();
  }
  void PollController() {
    if (!get_state) return;
    DWORD connected = 0, selected = MAXDWORD;
    for (DWORD index = 0; index < XUSER_MAX_COUNT; ++index) {
      slots[index] = {}; slot_errors[index] = get_state(index, &slots[index]);
      if (slot_errors[index] == ERROR_SUCCESS) { ++connected; selected = index; }
      else Require(slot_errors[index] == ERROR_DEVICE_NOT_CONNECTED, "System XInput sampling returned an unexpected error.");
    }
    const bool foreground = GetForegroundWindow() == top && GetFocus() == child && IsWindowVisible(top) && !mode_pass && last_flutter_focused;
    if (!foreground || !last_client_foreground || selected != selected_slot || connected != 1 ||
        !registered || activation_pending || return_pending || close_requested || !error.empty()) rs_armed = false;
    last_client_foreground = foreground; selected_slot = connected == 1 ? selected : MAXDWORD;
    rs_unavailable_reason = connected == 0 ? "No XInput controller is connected." :
        (connected != 1 ? "Multiple XInput controllers are ambiguous for this P0 run." : "");
    if (!foreground || selected_slot == MAXDWORD || !registered || activation_pending || return_pending || close_requested || !error.empty()) {
      rs_held_started = 0; rs_held_last = 0; return;
    }
    const auto& pad = slots[selected_slot].Gamepad;
    const auto now = GetTickCount64();
    if (!rs_armed && source == 2 && (pad.wButtons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0) {
      if (!rs_held_started || !rs_held_last || now - rs_held_last > 250) rs_held_started = now;
      rs_held_last = now; rs_held_duration = std::max(rs_held_duration, now - rs_held_started);
      if (now - rs_held_started >= 1000) rs_held_verified = true;
    } else { rs_held_started = 0; rs_held_last = 0; }
    if (Neutral(pad)) { rs_armed = true; return; }
    if (rs_armed && (pad.wButtons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0) {
      rs_armed = false; ++rs_edge_count; Event("rs-neutral-edge", SlotJson(selected_slot)); BeginReturn(3);
    }
  }
  void Send(std::vector<INPUT>& input) {
    Require(input.size() <= 8 && !input.empty(), "Input sequence exceeds the fixed bound.");
    send_requested = static_cast<UINT>(input.size()); SetLastError(ERROR_SUCCESS);
    send_inserted = SendInput(send_requested, input.data(), sizeof(INPUT)); send_error = GetLastError();
    if (send_requested == 2 && send_inserted == 1) {
      // Only repair our known inserted down with its matching up. Never retry
      // the incomplete action; the original failure stays terminal evidence.
      INPUT release = input[1]; SendInput(1, &release, sizeof(release));
    }
    Require(send_inserted == send_requested, "SendInput inserted an incomplete sequence; it must not be replayed.");
  }
  void PressF8() {
    Require(ControlReady() && !injected_f8_held && !activation_pending && !return_pending, "F8 press is unready or already in progress.");
    DesktopGuard(); OwnWindows(); BoundGameWindow();
    if (invocation.legacy) {
      Require(legacy_snapshot && legacy_snapshot->ready(), "Legacy F8 needs a fresh post-focus cached publication observation.");
      GameForegroundGuard();
    } else if (GetForegroundWindow() == game_window) GameForegroundGuard(); else ClientForegroundGuard();
    for (const int key : {VK_F8, VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN})
      Available((GetAsyncKeyState(key) & 0x8000) == 0, "A real F8 or modifier is held; synthetic key ownership cannot be established.");
    INPUT input{}; input.type = INPUT_KEYBOARD; input.ki.wVk = VK_F8; input.ki.dwExtraInfo = marker;
    std::vector<INPUT> events_to_send{input}; Send(events_to_send);
    injected_f8_held = true; ++injected_down_count;
    Event("f8-injected-down", "{\"foregroundHwnd\":" + std::to_string(WindowValue(GetForegroundWindow())) +
          ",\"foregroundPid\":" + std::to_string(WindowPid(GetForegroundWindow())) + "}");
  }
  void ReleaseInjectedKey(bool cleanup) {
    if (!injected_f8_held) { if (!cleanup) throw ProbeFailure("No injected F8 key is owned by this probe."); return; }
    if (!cleanup) {
      Require(!activation_pending, "F8 release cannot interrupt a pending game ASFW-to-client activation.");
      DesktopGuard(); OwnWindows(); BoundGameWindow();
      Available(GetForegroundWindow() == top || GetForegroundWindow() == game_window,
                "An unrelated window owns foreground before injected key release.");
    }
    INPUT input{}; input.type = INPUT_KEYBOARD; input.ki.wVk = VK_F8; input.ki.dwFlags = KEYEVENTF_KEYUP; input.ki.dwExtraInfo = marker;
    SetLastError(ERROR_SUCCESS); const auto inserted = SendInput(1, &input, sizeof(input)); const auto code = GetLastError();
    send_requested = 1; send_inserted = inserted; send_error = code;
    if (inserted == 1) { injected_f8_held = false; ++injected_up_count; }
    if (!cleanup) {
      Require(inserted == 1, "Cannot release the sole injected F8 key; no repeated input action is allowed.");
      Event("f8-injected-up");
    }
  }
  void Click() {
    const bool recovery = invocation.legacy && legacy_click_required;
    if (recovery) { GameForegroundGuard(); LegacyDiscoverable(); GameForegroundGuard(); }
    else ClientForegroundGuard();
    Available((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0, "Physical left mouse button is held.");
    RECT client{}; Require(GetClientRect(child, &client) && client.right > 0 && client.bottom > 0, "Flutter child client area is unavailable.");
    // The probe UI reserves its center as an inert input target.
    POINT point{(client.left + client.right) / 2, (client.top + client.bottom) / 2};
    Require(ClientToScreen(child, &point), "Cannot locate the exact Flutter input target.");
    const auto hit = WindowFromPoint(point);
    Available(hit && WindowPid(hit) == owner_pid && GetAncestor(hit, GA_ROOT) == top, "Another window covers the Flutter input target.");
    Available(!invocation.legacy || (hit == child && GetWindowThreadProcessId(hit, nullptr) == owner_thread),
              "Legacy recovery point must hit the exact retained Flutter child.");
    if (!cursor_saved) { Require(GetCursorPos(&original_cursor), "Cannot save cursor position."); cursor_saved = true; }
    Require(SetCursorPos(point.x, point.y), "Cannot position the fixed probe cursor."); last_cursor = point; cursor_injected = true;
    INPUT down{}; down.type = INPUT_MOUSE; down.mi.dwFlags = MOUSEEVENTF_LEFTDOWN; down.mi.dwExtraInfo = marker;
    INPUT up = down; up.mi.dwFlags = MOUSEEVENTF_LEFTUP;
    std::vector<INPUT> input{down, up};
    if (recovery) GameForegroundGuard(); else ClientForegroundGuard();
    Available(!invocation.legacy || WindowFromPoint(point) == child, "Legacy recovery target changed before the single click.");
    Send(input);
    if (invocation.legacy) { ++legacy_click_count; if (recovery) ++legacy_background_clicks; legacy_click_required = false; }
    Event("probe-click-injected", "{\"backgroundRecovery\":" + std::string(recovery ? "true" : "false") + ",\"automatedOsInput\":true}");
  }
  void SendFocusKey() {
    ClientForegroundGuard(); Available((GetAsyncKeyState(VK_F24) & 0x8000) == 0, "Physical F24 is held.");
    INPUT down{}; down.type = INPUT_KEYBOARD; down.ki.wVk = VK_F24; down.ki.dwExtraInfo = marker;
    INPUT up = down; up.ki.dwFlags = KEYEVENTF_KEYUP;
    std::vector<INPUT> input{down, up}; Send(input); Event("probe-f24-injected");
  }
  void RestoreCursor() {
    POINT current{};
    if (!cursor_saved || !cursor_injected || !GetCursorPos(&current) || current.x != last_cursor.x || current.y != last_cursor.y) return;
    DesktopGuard(); cursor_restored = SetCursorPos(original_cursor.x, original_cursor.y) != FALSE;
    Require(cursor_restored, "Cannot restore unchanged injected cursor position."); cursor_injected = false;
  }
  void SendGameClose(bool require_foreground) {
    Require(!close_requested && !activation_pending && !return_pending, "Game close cannot be repeated or overlap a handoff.");
    BoundGameWindow();
    if (require_foreground) GameForegroundGuard();
    Require(GetCurrentThreadId() == owner_thread, "Close callback must stay on the bound GUI thread.");
    close_receipt = std::make_shared<CloseReceipt>(game.value, game_pid, game_creation, game_window);
    close_receipt_token = RegisterCloseReceipt(close_receipt);
    close_target = game_window; close_target_pid = WindowPid(close_target);
    close_foreground = GetForegroundWindow(); close_foreground_pid = WindowPid(close_foreground);
    GUITHREADINFO info{}; info.cbSize = sizeof(info);
    const auto focus_observed = GetGUIThreadInfo(game_thread, &info) != FALSE;
    close_focus = focus_observed ? info.hwndFocus : nullptr;
    close_foreground_required = require_foreground;
    close_foreground_matched = close_foreground == game_window && close_foreground_pid == game_pid && focus_observed && OwnedGameFocus(close_focus);
    if (require_foreground) Available(close_foreground_matched, "Exact game foreground/focus changed before its sole close request.");
    close_requested = true; ++close_send_attempts;
    SetLastError(ERROR_SUCCESS);
    close_send_result = SendMessageCallbackW(close_target, WM_CLOSE, 0, 0, ReceiveGameClose, close_receipt_token) != FALSE;
    close_send_error = GetLastError(); deadline = GetTickCount64() + 30000;
    Event("game-close-sent", CloseMessageJson());
    Require(close_send_result, "Cannot send the sole close request to the bound game window.");
  }
  std::string CloseMessageJson() const {
    std::ostringstream out;
    out << "{\"transport\":\"SendMessageCallbackW\",\"attempts\":" << close_send_attempts
        << ",\"sendResult\":" << (close_send_result ? "true" : "false") << ",\"sendError\":" << close_send_error
        << ",\"foregroundRequired\":" << (close_foreground_required ? "true" : "false")
        << ",\"foregroundMatched\":" << (close_foreground_matched ? "true" : "false")
        << ",\"targetHwnd\":" << WindowValue(close_target) << ",\"targetPid\":" << close_target_pid
        << ",\"foregroundHwnd\":" << WindowValue(close_foreground) << ",\"foregroundPid\":" << close_foreground_pid
        << ",\"gameFocusHwnd\":" << WindowValue(close_focus) << ",\"callbackObserved\":";
    const bool observed = close_receipt && close_receipt->callback_count;
    out << (observed ? "true" : "false") << ",\"callbackCount\":" << (close_receipt ? close_receipt->callback_count : 0)
        << ",\"callback\":";
    if (!observed) out << "null";
    else {
      const auto& receipt = *close_receipt;
      out << "{\"hwnd\":" << WindowValue(receipt.callback_window) << ",\"message\":" << receipt.callback_message
          << ",\"lResult\":" << static_cast<int64_t>(receipt.callback_result) << ",\"threadId\":" << receipt.callback_thread
          << ",\"ticks\":" << receipt.callback_ticks << ",\"targetMatched\":" << (receipt.callback_target_matched ? "true" : "false")
          << ",\"identityMatched\":" << (receipt.callback_identity_matched ? "true" : "false")
          << ",\"retainedPid\":" << receipt.callback_pid << ",\"retainedCreationHex\":" << Quote(Hex(receipt.callback_creation))
          << ",\"waitResult\":" << receipt.callback_wait << ",\"exitCode\":";
      if (receipt.callback_exit_observed) out << receipt.callback_exit; else out << "null";
      out << ",\"error\":" << receipt.callback_error << '}';
    }
    out << '}'; return out.str();
  }
  void Poll() {
    if (!initialized || finishing) return;
    PollLegacy();
    if (pipe && !pipe->stopped()) {
      const auto received = pipe->Poll();
      if (received) HandleFrame(*received);
    }
    if (!error.empty()) return;
    if (ObservingGameExit()) {
      // Once the sole close send is accepted, observe the retained process.
      // Teardown may remove the image/window before its HANDLE is signaled.
      CheckGameIdentity();
      if (GameAlive()) Available(GetTickCount64() < deadline, "Game did not exit normally within the close deadline.");
      else Require(GameExitCode() == 0, "The retained game exited with a nonzero status.");
      return;
    }
    Require(GameAlive(), "Game exited before its explicitly observed close stage.");
    BindOrCheckGameWindow();
    if (invocation.legacy) {
      if (!legacy_ready) Available(GetTickCount64() < startup_deadline, "The original Mod startup show did not complete within deadline.");
      ObserveLegacyStartup(); ObserveReturn();
      if (legacy_snapshot && legacy_snapshot->pending()) {
        GameForegroundGuard();
        legacy_snapshot->Poll();
        if (legacy_snapshot->ready()) Event("legacy-post-focus-publication-observed", legacy_snapshot->Json());
      }
      PollController(); return;
    }
    if (!registered) Available(GetTickCount64() < startup_deadline, "The real Mod did not register from its Unity Update within startup deadline.");
    if (pipe && pipe->eof()) throw ProbeFailure("Registered game identity pipe closed before the normal close stage.");
    ObserveActivation(); ObserveReturn(); PollController();
  }
  void PollSafe() noexcept {
    try { Poll(); }
    catch (const std::exception& failure) {
      RememberControlFailure(failure, error, error_blocked);
      activation_pending = false; activation_confirmation_pending = false; return_pending = false; rs_armed = false;
      if (pipe && !pipe->stopped()) { try { pipe->Stop(); } catch (...) {} }
    }
  }
  std::string NullForegroundJson() const {
    std::ostringstream out;
    auto append = [&](const PendingForegroundNullObservations& value) {
      out << "{\"count\":" << value.count << ",\"firstTicks\":" << value.first_ticks << ",\"lastTicks\":" << value.last_ticks << '}';
    };
    out << "{\"activation\":"; append(activation_null_foreground);
    out << ",\"gameReturn\":"; append(return_null_foreground); out << '}'; return out.str();
  }
  std::string LegacyJson() const {
    if (!invocation.legacy) return "null";
    std::ostringstream out;
    out << "{\"scenario\":\"old-mod-legacy-client\",\"ready\":" << (legacy_ready ? "true" : "false")
        << ",\"showCount\":" << legacy_show_count << ",\"toggleCount\":" << legacy_toggle_count << ",\"exitCount\":" << legacy_exit_count
        << ",\"startupAttempted\":" << (legacy_startup_attempted ? "true" : "false")
        << ",\"automaticForeground\":" << (legacy_startup_foreground ? "true" : "false")
        << ",\"clickRequired\":" << (legacy_click_required ? "true" : "false")
        << ",\"clickCount\":" << legacy_click_count << ",\"backgroundClickCount\":" << legacy_background_clicks
        << ",\"asfwCalled\":false,\"automatedOsInput\":true,\"snapshotPublication\":"
        << (legacy_snapshot ? legacy_snapshot->Json() : "null") << '}';
    return out.str();
  }
  std::string SnapshotJson() {
    const bool alive = GameAlive(), observing_exit = ObservingGameExit();
    if (game.value) CheckGameIdentity();
    OwnWindows();
    const auto foreground = GetForegroundWindow();
    bool game_focus_owned = false;
    if (alive && game_window && !observing_exit) {
      GUITHREADINFO info{}; info.cbSize = sizeof(info);
      if (GetGUIThreadInfo(game_thread, &info)) game_focus_owned = OwnedGameFocus(info.hwndFocus);
    }
    std::ostringstream out;
    auto boolean = [](bool value) { return value ? "true" : "false"; };
    out << "{\"schemaVersion\":1,\"kind\":\"real-mod-control-native\",\"runId\":" << Quote(invocation.run)
        << ",\"modLaunchedClient\":" << boolean(invocation.client) << ",\"clientGeneration\":" << invocation.generation
        << ",\"error\":" << (error.empty() ? "null" : Quote(error)) << ",\"errorBlocked\":" << boolean(error_blocked)
        << ",\"gameReady\":" << boolean(registered)
        << ",\"gameWindowBound\":" << boolean(game_window != nullptr) << ",\"gameAlive\":" << boolean(alive)
        << ",\"gamePid\":" << game_pid << ",\"gameCreationHex\":" << Quote(Hex(game_creation)) << ",\"gameHwnd\":";
    if (alive && game_window && !observing_exit) out << WindowValue(game_window); else out << "null";
    out << ",\"gameThreadId\":" << game_thread << ",\"gameForeground\":" << boolean(alive && !observing_exit && foreground == game_window)
        << ",\"gameFocusOwned\":" << boolean(game_focus_owned) << ",\"gameExitCode\":";
    if (game.value && !alive) out << GameExitCode(); else out << "null";
    out << ",\"registered\":" << boolean(registered) << ",\"registrationCount\":" << registration_count
        << ",\"protocolRequestId\":" << protocol_request << ",\"inputSequence\":" << input_sequence << ",\"source\":" << source
        << ",\"activationCount\":" << activation_count << ",\"activationAttempts\":" << activation_attempts
        << ",\"activationPending\":" << boolean(activation_pending) << ",\"returnPending\":" << boolean(return_pending)
        << ",\"activationConfirmationPending\":" << boolean(activation_confirmation_pending)
        << ",\"nullForegroundObservations\":" << NullForegroundJson() << ",\"legacyControl\":" << LegacyJson()
        << ",\"focusGameCount\":" << focus_game_count << ",\"f8ReturnCount\":" << f8_return_count << ",\"rsReturnCount\":" << rs_return_count
        << ",\"rsEdgeCount\":" << rs_edge_count << ",\"f8ActivationCount\":" << f8_activation_count << ",\"rsActivationCount\":" << rs_activation_count
        << ",\"hiddenRecoveryCount\":" << hidden_recovery_count << ",\"passThroughRecoveryCount\":" << pass_recovery_count
        << ",\"rsHeldVerified\":" << boolean(rs_held_verified) << ",\"rsHeldDurationMs\":" << rs_held_duration
        << ",\"nativeF8Down\":" << native_f8_down << ",\"nativeF8Up\":" << native_f8_up << ",\"rawF8Down\":" << raw_f8_down
        << ",\"rawF8Up\":" << raw_f8_up << ",\"markerF8Down\":" << marker_f8_down << ",\"markerF8Up\":" << marker_f8_up
        << ",\"nativeF24Down\":" << native_f24_down << ",\"nativeMouseDown\":" << native_mouse_down << ",\"nativeMouseUp\":" << native_mouse_up
        << ",\"clientHwnd\":" << WindowValue(top) << ",\"childHwnd\":" << WindowValue(child) << ",\"clientThreadId\":" << owner_thread
        << ",\"visible\":" << boolean(IsWindowVisible(top) != FALSE) << ",\"interactive\":" << boolean(!mode_pass)
        << ",\"clientForeground\":" << boolean(foreground == top) << ",\"childFocused\":" << boolean(GetFocus() == child)
        << ",\"flutterFocused\":" << boolean(last_flutter_focused) << ",\"foregroundHwnd\":" << WindowValue(foreground)
        << ",\"foregroundPid\":" << WindowPid(foreground) << ",\"listenerOwnerPid\":" << (listener.value != INVALID_SOCKET ? owner_pid : 0)
        << ",\"legacyUnsupportedCount\":" << raw_unsupported_count << ",\"pipeConnected\":" << boolean(pipe && pipe->connected())
        << ",\"pipeEof\":" << boolean(pipe && pipe->eof()) << ",\"exitReceived\":" << boolean(exit_received)
        << ",\"injectedF8Held\":" << boolean(injected_f8_held) << ",\"injectedDownCount\":" << injected_down_count
        << ",\"injectedUpCount\":" << injected_up_count << ",\"rsArmed\":" << boolean(rs_armed) << ",\"selectedSlot\":";
    if (selected_slot != MAXDWORD) out << selected_slot; else out << "null";
    out << ",\"rsUnavailableReason\":" << Quote(rs_unavailable_reason) << ",\"slots\":[";
    for (DWORD index = 0; index < XUSER_MAX_COUNT; ++index) { if (index) out << ','; out << SlotJson(index); }
    out << "],\"lastSendRequested\":" << send_requested << ",\"lastSendInserted\":" << send_inserted << ",\"lastSendError\":" << send_error
        << ",\"closeRequested\":" << boolean(close_requested) << ",\"closeMessage\":" << CloseMessageJson()
        << ",\"gameObservationPhase\":" << Quote(observing_exit ? "retained-process-exit" : "live-window")
        << ",\"identity\":{\"matched\":" << boolean(identity_matched) << ",\"clientCreationHex\":" << Quote(Hex(owner_creation))
        << ",\"clientExecutableSha256\":" << Quote(client_hash) << ",\"gameExecutableSha256\":" << Quote(game_hash)
        << ",\"unityPlayerSha256\":" << Quote(unity_hash) << ",\"gameAssemblySha256\":" << Quote(assembly_hash)
        << ",\"metadataSha256\":" << Quote(metadata_hash) << ",\"modSha256\":" << Quote(mod_hash)
        << ",\"modBuildEvidenceSha256\":" << Quote(mod_build_hash) << ",\"bepInExSha256\":" << Quote(bep_hash)
        << ",\"preparedEvidenceSha256\":" << Quote(prepared_hash) << ",\"pipeName\":" << Quote(pipe_name)
        << ",\"session\":" << session << ",\"xinputPath\":" << Quote(xinput_path) << ",\"inputMarkerHex\":" << Quote(Hex(marker))
        << ",\"parentInJob\":" << boolean(parent_in_job) << ",\"gameInJob\":" << boolean(game_in_job)
        << ",\"gameResumed\":" << boolean(game_resumed) << "},\"events\":[";
    for (size_t index = 0; index < events.size(); ++index) { if (index) out << ','; out << events[index]; }
    out << "]}"; return out.str();
  }
  ControlSnapshot Snapshot() {
    return ControlSnapshot(MYSTIA_WINDOW_PROBE_GIT_SHA, owner_pid, ++snapshot_sequence, SnapshotJson());
  }
  ControlSnapshot Execute(const ControlCommand& command) {
    Require(invocation_valid && !finishing, "Control invocation is invalid or finishing.");
    Require(command.dart_f8_down_count() >= last_dart_f8 && command.dart_f8_down_count() >= 0 &&
                static_cast<uint64_t>(command.dart_f8_down_count()) <= native_f8_down, "Dart F8 count is stale or lacks native input evidence.");
    last_flutter_focused = command.flutter_focused(); last_dart_f8 = command.dart_f8_down_count();
    if (command.operation() == ControlOperation::kInspect) {
      Require(command.request_id() == command_id, "Inspect must name the current command ID."); PollSafe(); return Snapshot();
    }
    if (!error.empty()) throw ProbeFailure(error.c_str(), error_blocked);
    Require(command.request_id() == command_id + 1 && command.request_id() <= 1000,
            "Control command ID is repeated, skipped or exhausted.");
    Require(!close_requested, "Only inspection and finish are allowed after close.");
    Require(!injected_f8_held || command.operation() == ControlOperation::kReleaseF8 || command.operation() == ControlOperation::kReturnGame,
            "Only release/actual F8 return is allowed while this probe owns injected F8 down.");
    command_id = command.request_id();
    if (command.operation() != ControlOperation::kInitialize)
      Require(ControlReady() && !activation_pending && !return_pending, "The real Mod is not ready or a foreground operation is pending.");
    switch (command.operation()) {
      case ControlOperation::kInitialize: StartGame(); break;
      case ControlOperation::kFocusGame: BeginReturn(1); break;
      case ControlOperation::kReturnGame:
        Require(static_cast<uint64_t>(last_dart_f8) == native_f8_down && native_f8_down == consumed_f8_down + 1,
                "Flutter return requires exactly one new native and Dart F8 edge.");
        BeginReturn(2); consumed_f8_down = native_f8_down; break;
      case ControlOperation::kHideProbe:
        GameForegroundGuard(); ShowWindow(top, SW_HIDE); Require(!IsWindowVisible(top), "Hidden client state was not observed."); Event("client-hidden"); break;
      case ControlOperation::kSetPassThrough: GameForegroundGuard(); ApplyMode(true); Event("client-pass-through"); break;
      case ControlOperation::kSetInteractive: GameForegroundGuard(); ApplyMode(false); Event("client-interactive"); break;
      case ControlOperation::kPressF8: PressF8(); break;
      case ControlOperation::kReleaseF8: ReleaseInjectedKey(false); break;
      case ControlOperation::kClickProbe: Click(); break;
      case ControlOperation::kSendFocusKey: SendFocusKey(); break;
      case ControlOperation::kCloseGame: SendGameClose(true); break;
      default: throw ProbeFailure("Unknown control operation.");
    }
    PollSafe(); return Snapshot();
  }
  void CleanupRecord() {
    const bool alive = GameAlive();
    std::ostringstream out;
    out << "{\"schemaVersion\":1,\"kind\":\"control-probe-native-cleanup\",\"runId\":" << Quote(invocation.run)
        << ",\"gitSha\":\"" << MYSTIA_WINDOW_PROBE_GIT_SHA << "\",\"suite\":\"hotkey\",\"processId\":" << owner_pid << ",\"gamePid\":" << game_pid
        << ",\"gameCreationTimeHex\":" << Quote(Hex(game_creation)) << ",\"gameIdentityMatched\":" << (identity_matched ? "true" : "false")
        << ",\"gameAlive\":" << (alive ? "true" : "false") << ",\"gameExitCode\":";
    if (game.value && !alive) out << GameExitCode(); else out << "null";
    out << ",\"startupSuspendedTerminated\":" << (startup_terminated ? "true" : "false")
        << ",\"clientGeneration\":" << invocation.generation
        << ",\"gameRetainedForNextGeneration\":" << (RetiringClient() ? "true" : "false")
        << ",\"nodeJobFallbackRequired\":" << (alive && !RetiringClient() ? "true" : "false")
        << ",\"injectedF8Held\":" << (injected_f8_held ? "true" : "false")
        << ",\"cursorRestored\":" << (cursor_restored ? "true" : "false")
        << ",\"pipeStopped\":" << (pipe && pipe->stopped() ? "true" : "false") << ",\"exitReceived\":" << (exit_received ? "true" : "false")
        << ",\"registrationCount\":" << registration_count << ",\"confirmedActivationCount\":" << activation_count
        << ",\"activationAttempts\":" << activation_attempts << ",\"f8ActivationCount\":" << f8_activation_count
        << ",\"hiddenRecoveryCount\":" << hidden_recovery_count << ",\"passThroughRecoveryCount\":" << pass_recovery_count
        << ",\"f8ReturnCount\":" << f8_return_count << ",\"nativeF8Down\":" << native_f8_down << ",\"nativeF8Up\":" << native_f8_up
        << ",\"dartF8DownCount\":" << last_dart_f8 << ",\"dartConsumedF8Down\":" << consumed_f8_down
        << ",\"nativeMouseDown\":" << native_mouse_down << ",\"nativeMouseUp\":" << native_mouse_up << ",\"nativeF24Down\":" << native_f24_down
        << ",\"rsActivationCount\":" << rs_activation_count << ",\"rsEdgeCount\":" << rs_edge_count << ",\"rsReturnCount\":" << rs_return_count
        << ",\"rsHeldVerified\":" << (rs_held_verified ? "true" : "false") << ",\"rsHeldDurationMs\":" << rs_held_duration
        << ",\"injectedDownCount\":" << injected_down_count << ",\"injectedUpCount\":" << injected_up_count
        << ",\"legacyUnsupportedCount\":" << raw_unsupported_count << ",\"error\":" << (error.empty() ? "null" : Quote(error))
        << ",\"errorBlocked\":" << (error_blocked ? "true" : "false")
        << ",\"nullForegroundObservations\":" << NullForegroundJson() << ",\"legacyControl\":" << LegacyJson()
        << ",\"closeMessage\":" << CloseMessageJson() << ",\"exitCode\":" << finish_code
        << ",\"cleanupError\":" << Quote(cleanup_error) << "}\n";
    const auto name = invocation.client ? L"\\control-client-" + Wide(std::to_string(invocation.generation)) + L"-native-cleanup.json" : L"\\native-cleanup.json";
    WriteNew(invocation.root + name, out.str(), 16384);
  }
  bool RetiringClient() const { return invocation.client && !invocation.legacy && invocation.generation == 1 && finish_code == 0 && error.empty(); }
  void CompleteFinish() {
    try {
      if (pipe && !pipe->stopped()) {
        pipe->Stop();
        if (!pipe->PollStop() && GetTickCount64() < finish_deadline) return;
        if (!pipe->stopped()) { cleanup_error = "Control pipe cancellation did not complete."; finish_code = 1; }
      }
      if (GameAlive() && !close_requested && !RetiringClient()) {
        try { SendGameClose(false); }
        catch (const std::exception& failure) { cleanup_error = failure.what(); finish_code = 1; }
      }
      if (GameAlive() && GetTickCount64() < finish_deadline && !RetiringClient()) return;
      if (GameAlive() && !RetiringClient()) { cleanup_error = "Retained game did not exit during cleanup; node Job fallback remains necessary."; finish_code = 1; }
      if (RetiringClient()) GameForegroundGuard();
      if (game.value && !GameAlive()) { CheckGameIdentity(); if (GameExitCode() != 0) finish_code = 1; }
      if (injected_f8_held) { ReleaseInjectedKey(true); if (injected_f8_held) { cleanup_error = "Injected F8 release failed."; finish_code = 1; } }
      RestoreCursor(); CleanupRecord(); KillTimer(top, kFinishTimer);
      auto reply = std::move(finish_reply); if (reply) reply(std::nullopt); PostQuitMessage(finish_code);
    } catch (const std::exception& failure) {
      KillTimer(top, kFinishTimer); auto reply = std::move(finish_reply);
      if (reply) reply(FlutterError("cleanup-failed", failure.what())); PostQuitMessage(1);
    }
  }
  void Finish(const std::string& report, int64_t code, std::function<void(std::optional<FlutterError>)> reply) {
    Require(invocation_valid && !finishing && (code == 0 || code == 1 || code == 2), "Invalid/repeated control finish request.");
    const auto parsed = ReportReader(report).Read(); Require(parsed.kind == 'o', "Control report must be a JSON object.");
    if (code == 0 && invocation.legacy) {
      CheckGameIdentity(); ValidateClientPassReport(parsed, invocation);
      Require(invocation.client && invocation.generation == 1 && error.empty() && mod_hash == kOriginalModSha &&
                  legacy_ready && legacy_startup_attempted && legacy_show_count == 1 && legacy_toggle_count == 1 &&
                  legacy_click_count == 2 && legacy_background_clicks >= 1 && !legacy_click_required &&
                  legacy_peer.value == INVALID_SOCKET && legacy_bytes.empty() &&
                  !pipe && !registered && registration_count == 0 && activation_count == 0 && activation_attempts == 0 &&
                  focus_game_count == 2 && native_mouse_down == 2 && native_mouse_up == 2 && native_f24_down == 2 &&
                  !return_pending && !injected_f8_held && injected_down_count == 1 && injected_up_count == 1 &&
                  close_requested && close_send_attempts == 1 && close_send_result && close_foreground_required && close_foreground_matched &&
                  !GameAlive() && GameExitCode() == 0,
              "Legacy PASS requires the unchanged original Mod, real EOF show/toggle, actual automated click/input recovery and retained normal exit.");
    } else if (code == 0 && invocation.client) {
      CheckGameIdentity(); ValidateClientPassReport(parsed, invocation);
      Require(error.empty() && registered && registration_count == 1 && activation_count == 1 && activation_attempts == 1 &&
                  source == (invocation.generation == 1 ? 0ULL : 1ULL) && raw_unsupported_count == 0 &&
                  f8_return_count == 1 && consumed_f8_down == 1 && last_dart_f8 == 1 &&
                  native_mouse_down == 1 && native_mouse_up == 1 && native_f24_down == 1 &&
                  !activation_pending && !return_pending && !injected_f8_held && injected_down_count == injected_up_count,
              "Cold-client PASS lacks native launch, activation or actual input evidence.");
      if (invocation.generation == 1) {
        Require(GameAlive() && !close_requested && !exit_received, "First client must leave the exact game alive.");
        GameForegroundGuard();
      } else {
        Require(exit_received && pipe && pipe->writes() >= 3 && close_requested && close_send_attempts == 1 &&
                    close_send_result && close_foreground_required && close_foreground_matched && !GameAlive() && GameExitCode() == 0,
                "Final client must observe the exit protocol and the retained game's normal exit.");
      }
    } else if (code == 0) {
      CheckGameIdentity(); ValidatePassReport(report, invocation.run);
      Require(error.empty() && registered && registration_count == 1 && f8_activation_count >= 3 &&
                  hidden_recovery_count >= 1 && pass_recovery_count >= 1 && f8_return_count >= 1 &&
                  consumed_f8_down >= 1 && static_cast<uint64_t>(last_dart_f8) == consumed_f8_down &&
                  native_mouse_down >= 1 && native_mouse_up >= 1 && native_f24_down >= 1 &&
                  rs_activation_count >= 1 && rs_return_count >= 1 && rs_edge_count == rs_return_count && rs_held_verified &&
                  activation_attempts == activation_count && raw_unsupported_count == 0 &&
                  !activation_pending && !return_pending && !injected_f8_held && injected_down_count == injected_up_count &&
                  close_requested && close_send_attempts == 1 && close_send_result && close_foreground_required && close_foreground_matched &&
                  !GameAlive() && GameExitCode() == 0,
              "Control PASS requires real Mod F8/RS identity/input/neutral evidence, all ten checks and the retained game's single normal close exit zero.");
    }
    WriteNew(invocation.result, report, 1024 * 1024);
    finishing = true; finish_code = static_cast<int>(code); finish_reply = std::move(reply);
    finish_deadline = GetTickCount64() + 30000; activation_pending = false; return_pending = false; rs_armed = false;
    KillTimer(top, kPollTimer);
    if (injected_f8_held) ReleaseInjectedKey(true);
    if (!SetTimer(top, kFinishTimer, 25, nullptr)) {
      cleanup_error = "Cannot schedule bounded control cleanup."; finish_code = 1; finish_deadline = GetTickCount64();
    }
    CompleteFinish();
  }
  static LRESULT CALLBACK ChildProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR reference) {
    auto& self = *reinterpret_cast<Impl*>(reference);
    const auto extra = static_cast<uint64_t>(GetMessageExtraInfo());
    if (extra == self.marker && GetForegroundWindow() == self.top && !self.mode_pass) {
      if (message == WM_LBUTTONDOWN) ++self.native_mouse_down;
      if (message == WM_LBUTTONUP) ++self.native_mouse_up;
    }
    if (self.mode_pass && message == WM_NCHITTEST) return HTTRANSPARENT;
    if (self.mode_pass && message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    return DefSubclassProc(window, message, wparam, lparam);
  }
};

bool ValidateControlProbeInvocation(const std::vector<std::string>& arguments) {
  try { ParseInvocation(arguments); return true; } catch (...) { return false; }
}
bool ValidateControlClientInvocation(const std::vector<std::string>& arguments) {
  try { ParseClientInvocation(arguments); return true; } catch (...) { return false; }
}
std::vector<std::string> ControlClientDartArguments(const std::vector<std::string>& arguments) {
  const auto invocation = ParseClientInvocation(arguments);
  return {invocation.legacy ? "--control-legacy-client" : "--control-client", invocation.run, std::to_string(invocation.generation)};
}
bool IsControlLifecycleController(const std::vector<std::string>& arguments) {
  try {
    const auto invocation = ParseInvocation(arguments);
    return GetFileAttributesW((invocation.root + L"\\control-lifecycle.json").c_str()) != INVALID_FILE_ATTRIBUTES;
  } catch (...) { return false; }
}
int RunControlLifecycleController(const std::vector<std::string>& arguments) {
  Handle game, active_client;
  std::unique_ptr<ExitDiagnosticPage> exit_diagnostic;
  std::vector<Handle> pinned_files;
  Invocation invocation;
  DWORD game_pid = 0; uint64_t game_creation = 0;
  std::vector<DWORD> client_pids;
  std::vector<uint64_t> client_creations;
  std::vector<std::string> generations;
  bool resumed = false, key_down = false, game_normal_exit = false, verified_client_blocked = false;
  UINT injected = 0;
  int result = 1;
  std::string failure;
  auto pump = [] {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
    Sleep(16);  // Poll cadence; no elapsed delay is treated as game/input readiness.
  };
  auto wait = [&](const std::function<bool()>& predicate, uint64_t duration, const char* error) {
    const auto end = GetTickCount64() + duration;
    do { DesktopGuard(); if (predicate()) return; pump(); } while (GetTickCount64() < end);
    throw ProbeFailure(error);
  };
  auto exit_zero = [](HANDLE process) {
    DWORD code = 0;
    return process && WaitForSingleObject(process, 0) == WAIT_OBJECT_0 && GetExitCodeProcess(process, &code) && code == 0;
  };
  auto exact_game_window = [&]() {
    struct Windows { DWORD pid; std::vector<HWND> candidates; } context{game_pid, {}};
    EnumWindows([](HWND hwnd, LPARAM raw) -> BOOL {
      auto& value = *reinterpret_cast<Windows*>(raw);
      if (WindowPid(hwnd) != value.pid || GetWindow(hwnd, GW_OWNER) || GetAncestor(hwnd, GA_ROOT) != hwnd || !IsWindowVisible(hwnd)) return TRUE;
      wchar_t name[256]{};
      if (GetClassNameW(hwnd, name, 256) && wcscmp(name, L"UnityWndClass") == 0) value.candidates.push_back(hwnd);
      return TRUE;
    }, reinterpret_cast<LPARAM>(&context));
    Require(context.candidates.size() == 1, "Lifecycle game window is missing or ambiguous.");
    return context.candidates.front();
  };
  try {
    invocation = ParseInvocation(arguments); DesktopGuard();
    const auto authorization = LifecycleAuthorization(invocation);
    invocation.legacy = ReportText(authorization, "scenario") == "old-mod-legacy-client";
    Available(ListenerOwner() == 0, "Lifecycle control port is already occupied.");
    auto sidecar_file = OpenPinned(invocation.root + L"\\control-probe.json");
    const auto sidecar = SidecarParser(ReadSmall(sidecar_file.value, 32768)).Parse();
    Require(sidecar.at("runId") == invocation.run && sidecar.at("gitSha") == MYSTIA_WINDOW_PROBE_GIT_SHA &&
                sidecar.at("steamAppId") == "1584090" && sidecar.at("steamBuildId") == "23158340" &&
                sidecar.at("preparedEvidenceSha256") == ReportText(authorization, "preparedEvidenceSha256"), "Lifecycle preparation differs.");
    const auto game_root = invocation.root + L"\\workspace\\game";
    Require(!invocation.legacy || sidecar.at("expectedModSha256") == kOriginalModSha, "Legacy controller requires the unchanged original Mod 1.3.1.");
    const auto game_path = game_root + L"\\" + kGameExecutable;
    auto supplied = Wide(sidecar.at("gameExecutable")); std::replace(supplied.begin(), supplied.end(), L'/', L'\\');
    Require(SamePath(supplied, game_path), "Lifecycle sidecar points outside the fixed copied game.");
    PlainPath(game_root, true);
    auto pin = [&](const std::wstring& path, const std::string& expected) {
      Require(HashText(expected), "Lifecycle hash must be canonical SHA-256.");
      auto file = OpenPinned(path);
      Require(Sha256(file.value) == expected, "Lifecycle prepared file changed before game launch.");
      pinned_files.push_back(std::move(file));
    };
    pin(game_path, sidecar.at("expectedExeSha256"));
    pin(game_root + L"\\UnityPlayer.dll", sidecar.at("expectedUnityPlayerSha256"));
    pin(game_root + L"\\GameAssembly.dll", sidecar.at("expectedGameAssemblySha256"));
    pin(game_root + L"\\Touhou Mystia Izakaya_Data\\il2cpp_data\\Metadata\\global-metadata.dat", sidecar.at("expectedMetadataSha256"));
    pin(game_root + L"\\BepInEx\\plugins\\mystia-steward-companion\\MystiaStewardCompanion.BepInEx.dll", sidecar.at("expectedModSha256"));
    pin(game_root + L"\\BepInEx\\core\\BepInEx.Unity.IL2CPP.dll", sidecar.at("expectedBepInExSha256"));
    pin(invocation.root + L"\\workspace\\mod-build-evidence.json", sidecar.at("modBuildEvidenceSha256"));
    bool diagnostic_requested = false;
    if (!invocation.legacy) {
      auto manifest_file = OpenPinned(invocation.root + L"\\workspace\\mod-build-evidence.json");
      const auto manifest = ReportReader(ReadSmall(manifest_file.value, 1024 * 1024)).Read();
      diagnostic_requested = manifest.object.count("exitDiagnostic") != 0;
      if (diagnostic_requested) ReportField(manifest, "exitDiagnostic", 'b', "true");
    }
    const auto diagnostic_path = invocation.root + L"\\control-exit-diagnostic.json";
    const auto diagnostic_attributes = GetFileAttributesW(diagnostic_path.c_str());
    Require(diagnostic_requested == (diagnostic_attributes != INVALID_FILE_ATTRIBUTES), "Diagnostic bundle and explicit authorization must match.");
    if (diagnostic_requested) {
      auto diagnostic_file = OpenPinned(diagnostic_path);
      const auto diagnostic = ReportReader(ReadSmall(diagnostic_file.value, 32768)).Read();
      Require(diagnostic.kind == 'o' && diagnostic.object.size() == 7, "Diagnostic authorization schema differs.");
      ReportField(diagnostic, "schemaVersion", 'n', "1"); ReportField(diagnostic, "kind", 's', "control-exit-diagnostic-authorization");
      ReportField(diagnostic, "runId", 's', invocation.run); ReportField(diagnostic, "gitSha", 's', MYSTIA_WINDOW_PROBE_GIT_SHA);
      ReportField(diagnostic, "diagnosticOnly", 'b', "true");
      ReportField(diagnostic, "modBuildEvidenceSha256", 's', sidecar.at("modBuildEvidenceSha256"));
      ReportField(diagnostic, "preparedEvidenceSha256", 's', sidecar.at("preparedEvidenceSha256"));
      pinned_files.push_back(std::move(diagnostic_file));
      PlainPath(invocation.root + L"\\control-exit-diagnostic-result.json", false, true);
      Require(GetFileAttributesW((invocation.root + L"\\control-exit-diagnostic-result.json").c_str()) == INVALID_FILE_ATTRIBUTES,
              "Diagnostic capture already exists.");
    }
    auto appid = OpenPinned(game_root + L"\\steam_appid.txt");
    const auto appid_text = ReadSmall(appid.value, 9);
    Require(appid_text == "1584090" || appid_text == "1584090\n" || appid_text == "1584090\r\n", "Lifecycle copied App ID differs.");
    pinned_files.push_back(std::move(appid));
    auto prepared_file = OpenPinned(invocation.root + L"\\workspace\\prepared-evidence.json");
    Require(Sha256(prepared_file.value) == sidecar.at("preparedEvidenceSha256"), "Lifecycle prepared evidence changed.");
    const auto prepared = ReportReader(ReadSmall(prepared_file.value, 1024 * 1024)).Read();
    const auto& config_evidence = prepared.object.at("config");
    ReportField(config_evidence, "autoLaunch", 'b', "true");
    const std::string protocol = invocation.legacy ? "LegacyTcp" : "IdentityPipeV1";
    ReportField(config_evidence, "controlProtocol", 's', protocol);
    ReportField(config_evidence, "port", 'n', "32755");
    ReportField(config_evidence, "localApiEnabled", 'b', "true");
    for (const auto name : {"allowLanConnections", "updatesEnabled", "autoCheck"}) ReportField(config_evidence, name, 'b', "false");
    auto client_file = OpenPinned(ModulePath()); const auto client_hash = Sha256(client_file.value);
    Require(client_hash == ReportText(prepared, "windowExecutableSha256"), "Lifecycle Flutter payload changed.");
    auto configured_client = Wide(ReportText(config_evidence, "executablePath"));
    std::replace(configured_client.begin(), configured_client.end(), L'/', L'\\');
    Require(SamePath(configured_client, ModulePath()), "Lifecycle configured client is not the exact payload.");
    auto config_file = OpenPinned(game_root + L"\\BepInEx\\config\\com.tyukki.mystia-steward-companion.cfg");
    Require(Sha256(config_file.value) == ReportText(config_evidence, "sha256"), "Lifecycle configuration changed before launch.");
    const auto config = ReadSmall(config_file.value, 32768);
    Require(config.find("AutoLaunch = true\nControlProtocol = " + protocol + "\n") != std::string::npos,
            "Lifecycle copy must explicitly auto-launch its authorized protocol.");
    config_file = Handle();  // BepInEx may append its generated setting descriptions.
    BOOL in_job = FALSE;
    Require(IsProcessInJob(GetCurrentProcess(), nullptr, &in_job) && in_job, "Lifecycle controller is outside the node job.");
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION info{};
    auto command = L"\"" + game_path + L"\"";
    Require(CreateProcessW(game_path.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr,
                           game_root.c_str(), &startup, &info), "Cannot create the cold-start game copy suspended.");
    game = Handle(info.hProcess); Handle thread(info.hThread); game_pid = info.dwProcessId; game_creation = Creation(game.value);
    DWORD game_session = MAXDWORD, own_session = MAXDWORD;
    const auto own_sid = UserSid(GetCurrentProcess()), game_sid = UserSid(game.value);
    Require(GetProcessId(game.value) == game_pid && SamePath(ProcessPath(game.value), game_path) &&
                ProcessIdToSessionId(game_pid, &game_session) && ProcessIdToSessionId(GetCurrentProcessId(), &own_session) &&
                game_session == own_session && EqualSid(const_cast<unsigned char*>(own_sid.data()), const_cast<unsigned char*>(game_sid.data())) &&
                IsProcessInJob(game.value, nullptr, &in_job) && in_job, "Suspended lifecycle game identity differs.");
    if (diagnostic_requested) exit_diagnostic = std::make_unique<ExitDiagnosticPage>(invocation, game_pid, game_creation,
        client_hash, sidecar.at("expectedModSha256"), sidecar.at("modBuildEvidenceSha256"), sidecar.at("expectedExeSha256"));
    auto publish_launch = [&](unsigned generation) {
      const auto text = "{\"schemaVersion\":1,\"kind\":\"control-client-launch\",\"runId\":" + Quote(invocation.run) +
          ",\"gitSha\":\"" MYSTIA_WINDOW_PROBE_GIT_SHA "\",\"generation\":" + std::to_string(generation) +
          ",\"gamePid\":" + std::to_string(game_pid) + ",\"gameCreationHex\":" + Quote(Hex(game_creation)) +
          ",\"clientExecutableSha256\":" + Quote(client_hash) + "}\n";
      WriteNew(invocation.root + L"\\control-launch-" + Wide(std::to_string(generation)) + L".json", text, 32768);
    };
    publish_launch(1);
    Require(ResumeThread(thread.value) == 1, "Cannot resume the exact lifecycle game."); resumed = true;
    for (unsigned generation = 1; generation <= (invocation.legacy ? 1U : 2U); ++generation) {
      if (generation == 2) {
        Require(exit_zero(active_client.value) && WaitForSingleObject(game.value, 0) == WAIT_TIMEOUT && ListenerOwner() == 0,
                "Previous client has not retired cleanly or the game changed.");
        publish_launch(2); active_client = Handle();
        wait([&] {
          // Read only the exact copied game's log. Never publish arbitrary log
          // bytes or infer readiness merely from a fixed startup delay.
          const auto path = game_root + L"\\BepInEx\\LogOutput.log";
          Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
          if (file.value == INVALID_HANDLE_VALUE) return false;
          const auto log = ReadSmall(file.value, 1024 * 1024);
          const auto replaced = log.find("companion_control protocol=IdentityPipeV1 event=session_replaced outcome=retained_client_exited");
          return replaced != std::string::npos && log.find("event=registration_absent outcome=no_listener", replaced) != std::string::npos;
        }, 15000, "The Mod did not observe the retained client's exit and prepare a fresh session.");
        const auto window = exact_game_window();
        Require(GetForegroundWindow() == window && WindowPid(window) == game_pid && Creation(game.value) == game_creation,
                "Exact game is not foreground for the single restart F8 action.");
        INPUT input[2]{};
        for (auto& value : input) { value.type = INPUT_KEYBOARD; value.ki.wVk = VK_F8; value.ki.dwExtraInfo = 0x4d53434c; }
        input[1].ki.dwFlags = KEYEVENTF_KEYUP;
        injected = SendInput(2, input, sizeof(INPUT)); key_down = injected == 1;
        Require(injected == 2, "The single bounded restart F8 pair was not accepted completely.");
      }
      DWORD pid = 0;
      wait([&] {
        Require(WaitForSingleObject(game.value, 0) == WAIT_TIMEOUT && Creation(game.value) == game_creation,
                "Game exited or changed before launching its client.");
        pid = ListenerOwner(); return pid != 0;
      }, 120000, "Real Mod did not launch a control listener.");
      Require(pid != game_pid && pid != GetCurrentProcessId() && std::find(client_pids.begin(), client_pids.end(), pid) == client_pids.end(),
              "The Mod reused an unexpected client PID.");
      active_client = Handle(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid));
      Require(active_client.value && SamePath(ProcessPath(active_client.value), ModulePath()), "Cold client path differs from the exact payload.");
      const auto creation = Creation(active_client.value); client_pids.push_back(pid); client_creations.push_back(creation);
      wait([&] { return WaitForSingleObject(active_client.value, 0) == WAIT_OBJECT_0; }, 150000, "Client generation did not finish its checks.");
      DWORD client_exit = MAXDWORD;
      Require(GetProcessId(active_client.value) == pid && Creation(active_client.value) == creation &&
                  GetExitCodeProcess(active_client.value, &client_exit) && (client_exit == 0 || client_exit == 2),
              "Client generation did not finish with an accepted exit code and its retained identity.");
      auto report_file = OpenPinned(invocation.root + L"\\control-client-" + Wide(std::to_string(generation)) + L"-result.json");
      const auto report_text = ReadSmall(report_file.value, 1024 * 1024);
      const auto report = ReportReader(report_text).Read();
      auto client_invocation = invocation; client_invocation.client = true; client_invocation.generation = generation;
      if (client_exit == 0) ValidateClientPassReport(report, client_invocation);
      else ValidateClientBlockedReport(report, client_invocation);
      ReportField(report.object.at("context"), "processId", 'n', std::to_string(pid));
      auto cleanup_file = OpenPinned(invocation.root + L"\\control-client-" + Wide(std::to_string(generation)) + L"-native-cleanup.json");
      const auto cleanup = ReportReader(ReadSmall(cleanup_file.value, 32768)).Read();
      ReportField(cleanup, "processId", 'n', std::to_string(pid)); ReportField(cleanup, "gamePid", 'n', std::to_string(game_pid));
      ReportField(cleanup, "gitSha", 's', MYSTIA_WINDOW_PROBE_GIT_SHA); ReportField(cleanup, "runId", 's', invocation.run);
      ReportField(cleanup, "exitCode", 'n', std::to_string(client_exit)); ReportField(cleanup, "cleanupError", 's', "");
      ReportField(cleanup, "nodeJobFallbackRequired", 'b', "false");
      if (client_exit == 2) {
        // Exit 2 alone is not evidence of an environmental block. Require this
        // exact client's structured first native error and successful cleanup.
        ReportField(cleanup, "schemaVersion", 'n', "1"); ReportField(cleanup, "kind", 's', "control-probe-native-cleanup");
        ReportField(cleanup, "suite", 's', "hotkey"); ReportField(cleanup, "clientGeneration", 'n', std::to_string(generation));
        ReportField(cleanup, "gameCreationTimeHex", 's', Hex(game_creation)); ReportField(cleanup, "gameIdentityMatched", 'b', "true");
        ReportField(cleanup, "errorBlocked", 'b', "true");
        Require(!ReportText(cleanup, "error").empty(), "Client BLOCKED lacks a retained native error.");
        ReportField(cleanup, "gameAlive", 'b', "false"); ReportField(cleanup, "gameExitCode", 'n', "0");
        ReportField(cleanup, "injectedF8Held", 'b', "false"); ReportField(cleanup, "startupSuspendedTerminated", 'b', "false");
        ReportField(cleanup, "gameRetainedForNextGeneration", 'b', "false");
        Require(exit_zero(game.value) && ListenerOwner() == 0, "Blocked client did not release the retained game and control port normally.");
      }
      generations.push_back("{\"generation\":" + std::to_string(generation) + ",\"pid\":" + std::to_string(pid) +
          ",\"creationHex\":" + Quote(Hex(creation)) + ",\"reportSha256\":" + Quote(Sha256(report_file.value)) +
          ",\"cleanupSha256\":" + Quote(Sha256(cleanup_file.value)) + "}");
      if (client_exit == 2) {
        game_normal_exit = true; verified_client_blocked = true;
        throw ProbeFailure(report.object.at("errors").array.front().text.c_str(), true);
      }
    }
    Require(exit_zero(game.value) && ListenerOwner() == 0, "The game/client lifecycle did not release normally.");
    game_normal_exit = true; result = 0;
  } catch (const std::exception& error) { failure = error.what(); result = verified_client_blocked ? 2 : 1; }
  if (key_down) { INPUT up{}; up.type = INPUT_KEYBOARD; up.ki.wVk = VK_F8; up.ki.dwFlags = KEYEVENTF_KEYUP; up.ki.dwExtraInfo = 0x4d53434c; SendInput(1, &up, sizeof(up)); }
  if (game.value && !resumed) TerminateProcess(game.value, 1);
  if (game.value && resumed && WaitForSingleObject(game.value, 0) == WAIT_TIMEOUT) {
    try {
      const auto window = exact_game_window();
      if (GetForegroundWindow() == window) {
        SendMessageCallbackW(window, WM_CLOSE, 0, 0, [](HWND, UINT, ULONG_PTR, LRESULT) {}, 0);
        const auto end = GetTickCount64() + 30000;
        while (WaitForSingleObject(game.value, 0) == WAIT_TIMEOUT && GetTickCount64() < end) pump();
      }
    } catch (...) { /* Preserve failure; node owns the remaining exact Job. */ }
  }
  try {
    Require(!invocation.root.empty(), "Lifecycle invocation was never validated.");
    if (exit_diagnostic) WriteNew(invocation.root + L"\\control-exit-diagnostic-result.json",
        exit_diagnostic->Capture(game.value, client_pids, client_creations), 32768);
    std::ostringstream report;
    report << "{\"schemaVersion\":1,\"kind\":" << Quote(invocation.legacy ? "flutter-legacy-control" : "flutter-control-lifecycle")
        << ",\"scenario\":" << Quote(invocation.legacy ? "old-mod-legacy-client" : "new-mod-cold-restart")
        << ",\"suite\":\"hotkey\",\"runId\":" << Quote(invocation.run)
        << ",\"gitSha\":\"" MYSTIA_WINDOW_PROBE_GIT_SHA "\",\"processId\":" << GetCurrentProcessId()
        << ",\"status\":" << Quote(result == 0 ? "PASS" : result == 2 ? "BLOCKED" : "FAIL") << ",\"p0Verified\":false,\"gamePid\":" << game_pid
        << ",\"exitDiagnosticOnly\":" << (exit_diagnostic ? "true" : "false")
        << ",\"gameCreationHex\":" << Quote(Hex(game_creation)) << ",\"restartInputInserted\":" << injected
        << ",\"normalGameExitVerified\":" << (game_normal_exit ? "true" : "false")
        << ",\"nodeJobFallbackRequired\":" << ((game.value && WaitForSingleObject(game.value, 0) == WAIT_TIMEOUT) ||
            (active_client.value && WaitForSingleObject(active_client.value, 0) == WAIT_TIMEOUT) ? "true" : "false")
        << ",\"generations\":[";
    for (size_t index = 0; index < generations.size(); ++index) { if (index) report << ','; report << generations[index]; }
    report << "],\"errors\":["; if (!failure.empty()) report << Quote(failure);
    report << "],\"limitations\":[" << Quote(invocation.legacy
        ? "Original Mod 1.3.1 launch and raw TCP show/toggle only. Click recovery is marked OS automation, not a human action or ASFW grant. Ordinary-user privileges and the finished client remain separate."
        : "New Mod first launch and replacement only; old Mod and ordinary-user privileges remain separate.") << "]}\n";
    WriteNew(invocation.result, report.str(), 32768);
  } catch (...) { return 1; }
  return result;
}
ControlProbeBridge::ControlProbeBridge(HWND top, flutter::FlutterViewController* controller, const std::vector<std::string>& arguments)
    : impl_(std::make_unique<Impl>(top, controller, arguments)) { queued_bridge = this; }
ControlProbeBridge::~ControlProbeBridge() { if (queued_bridge == this) queued_bridge = nullptr; }
void ControlProbeBridge::ObserveQueuedMessage(const MSG& message) {
  if (!queued_bridge) return;
  auto& self = *queued_bridge->impl_;
  if (message.hwnd != self.child) return;
  const auto extra = static_cast<uint64_t>(GetMessageExtraInfo());
  if (message.wParam == VK_F8) {
    if (message.message == WM_KEYDOWN) {
      ++self.raw_f8_down;
      const bool edge = (static_cast<uint64_t>(message.lParam) & (uint64_t{1} << 30)) == 0;
      if (edge && GetForegroundWindow() == self.top && GetFocus() == self.child && !self.mode_pass) ++self.native_f8_down;
      if (edge && extra == self.marker) ++self.marker_f8_down;
    } else if (message.message == WM_KEYUP) {
      ++self.raw_f8_up; ++self.native_f8_up; if (extra == self.marker) ++self.marker_f8_up;
    }
  }
  if (message.message == WM_KEYDOWN && message.wParam == VK_F24 && extra == self.marker &&
      (static_cast<uint64_t>(message.lParam) & (uint64_t{1} << 30)) == 0 &&
      GetForegroundWindow() == self.top && GetFocus() == self.child && !self.mode_pass) ++self.native_f24_down;
}
std::optional<LRESULT> ControlProbeBridge::HandleWindowMessage(UINT message, WPARAM wparam, LPARAM) {
  if (message == WM_TIMER && wparam == kPollTimer) { impl_->PollSafe(); return 0; }
  if (message == WM_TIMER && wparam == kFinishTimer) { impl_->CompleteFinish(); return 0; }
  if (message == WM_NCHITTEST && impl_->mode_pass) return HTTRANSPARENT;
  if (message == WM_MOUSEACTIVATE && impl_->mode_pass) return MA_NOACTIVATE;
  return std::nullopt;
}
void ControlProbeBridge::Execute(const ControlCommand& command, std::function<void(ErrorOr<ControlSnapshot>)> reply) {
  try { reply(impl_->Execute(command)); }
  catch (const std::exception& failure) {
    RememberControlFailure(failure, impl_->error, impl_->error_blocked);
    flutter::EncodableValue details;
    try { details = flutter::EncodableValue(impl_->SnapshotJson()); } catch (...) {}
    reply(FlutterError(impl_->error_blocked ? "blocked" : "native-error", impl_->error, details));
  }
}
void ControlProbeBridge::Finish(const std::string& report, int64_t code, std::function<void(std::optional<FlutterError>)> reply) {
  try { impl_->Finish(report, code, reply); }
  catch (const std::exception& failure) { reply(FlutterError("finish-failed", failure.what())); PostQuitMessage(1); }
}
