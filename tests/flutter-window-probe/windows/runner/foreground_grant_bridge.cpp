#include "foreground_grant_bridge.h"

#include <bcrypt.h>
#include <sddl.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <iomanip>
#include <iterator>
#include <limits>
#include <map>
#include <sstream>
#include <stdexcept>
#include <utility>
#include <vector>

namespace {
constexpr uint32_t kMagic = 0x4d534647, kVersion = 1;
constexpr size_t kFrameSize = 176;
using Frame = std::array<unsigned char, kFrameSize>;
void Require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
struct Handle {
  HANDLE value = nullptr;
  ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
  Handle() = default;
  Handle(const Handle&) = delete;
  Handle& operator=(const Handle&) = delete;
};
struct LocalMemory {
  void* value = nullptr;
  ~LocalMemory() { if (value) LocalFree(value); }
};
uint64_t Creation(HANDLE process) {
  FILETIME created{}, exited{}, kernel{}, user{};
  Require(GetProcessTimes(process, &created, &exited, &kernel, &user), "Cannot read foreground peer creation time.");
  return (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
}
std::wstring ProcessPath(HANDLE process) {
  std::wstring path(32768, L'\0'); DWORD length = static_cast<DWORD>(path.size());
  Require(QueryFullProcessImageNameW(process, 0, path.data(), &length), "Cannot read foreground peer executable path.");
  path.resize(length); return path;
}
uint64_t HwndValue(HWND value) { return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(value)); }
std::string Hex(uint64_t value, bool padded = false) {
  std::ostringstream out; out << std::hex << std::setfill('0');
  if (padded) out << std::setw(16);
  out << value; return out.str();
}
std::string FrameHex(const Frame& frame) {
  std::ostringstream out; out << std::hex << std::setfill('0');
  for (unsigned char byte : frame) out << std::setw(2) << static_cast<unsigned int>(byte);
  return out.str();
}
void Put32(Frame& frame, size_t offset, uint32_t value) {
  for (size_t i = 0; i < 4; ++i) frame[offset + i] = static_cast<unsigned char>(value >> (i * 8));
}
uint32_t Get32(const Frame& frame, size_t offset) {
  uint32_t value = 0;
  for (size_t i = 0; i < 4; ++i) value |= static_cast<uint32_t>(frame[offset + i]) << (i * 8);
  return value;
}
void Put(Frame& frame, size_t index, uint64_t value) {
  for (size_t i = 0; i < 8; ++i) frame[16 + index * 8 + i] = static_cast<unsigned char>(value >> (i * 8));
}
uint64_t Get(const Frame& frame, size_t index) {
  uint64_t value = 0;
  for (size_t i = 0; i < 8; ++i) value |= static_cast<uint64_t>(frame[16 + index * 8 + i]) << (i * 8);
  return value;
}
void Header(Frame& frame, uint32_t kind) {
  Put32(frame, 0, kMagic); Put32(frame, 4, kVersion); Put32(frame, 8, kind); Put32(frame, 12, static_cast<uint32_t>(kFrameSize));
}
void CheckHeader(const Frame& frame, uint32_t kind) {
  Require(Get32(frame, 0) == kMagic && Get32(frame, 4) == kVersion && Get32(frame, 8) == kind &&
              Get32(frame, 12) == kFrameSize, "Foreground frame header differs from the fixed protocol.");
}

// A bounded reader for the fixed plugin evidence schema. The only nested
// structure is its <=3 request records; arbitrary JSON trees are not accepted.
struct JsonAtom { char kind; std::string value; };
using JsonObject = std::map<std::string, JsonAtom>;
class EvidenceReader {
 public:
  explicit EvidenceReader(const std::string& text) : text_(text) {
    Require(!text.empty() && text.size() <= 65536, "Game foreground evidence exceeds its bound.");
  }
  JsonObject Read(std::vector<JsonObject>& requests) {
    auto object = Object(&requests); Space(); Require(at_ == text_.size(), "Trailing bytes in game foreground evidence."); return object;
  }
 private:
  const std::string& text_; size_t at_ = 0;
  char Peek() const { return at_ < text_.size() ? text_[at_] : '\0'; }
  void Space() { while (Peek() == ' ' || Peek() == '\n' || Peek() == '\r' || Peek() == '\t') ++at_; }
  void Take(char value) { Space(); Require(Peek() == value, "Malformed game foreground evidence JSON."); ++at_; }
  std::string String() {
    Take('"'); std::string result;
    while (Peek() != '"') {
      auto value = static_cast<unsigned char>(Peek());
      Require(value >= 32 && value <= 126 && result.size() < 4096, "Evidence string is not bounded ASCII."); ++at_;
      if (value == '\\') {
        char escape = Peek(); ++at_;
        if (escape == '"' || escape == '\\' || escape == '/') value = static_cast<unsigned char>(escape);
        else if (escape == 'n') value = '\n';
        else if (escape == 'r') value = '\r';
        else if (escape == 't') value = '\t';
        else if (escape == 'b') value = '\b';
        else if (escape == 'f') value = '\f';
        else if (escape == 'u') {
          unsigned int decoded = 0;
          for (int i = 0; i < 4; ++i) {
            char digit = Peek(); ++at_;
            Require((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f') || (digit >= 'A' && digit <= 'F'),
                    "Malformed evidence string escape.");
            decoded = decoded * 16 + static_cast<unsigned int>(digit <= '9' ? digit - '0' : (digit <= 'F' ? digit - 'A' + 10 : digit - 'a' + 10));
          }
          Require(decoded <= 127, "Evidence identity must remain ASCII."); value = static_cast<unsigned char>(decoded);
        } else throw std::runtime_error("Unsupported evidence string escape.");
      }
      result += static_cast<char>(value);
    }
    ++at_; return result;
  }
  JsonAtom Atom() {
    Space(); if (Peek() == '"') return {'s', String()};
    if (Peek() >= '0' && Peek() <= '9') {
      std::string result;
      while (Peek() >= '0' && Peek() <= '9') { Require(result.size() < 20, "Evidence number is too long."); result += Peek(); ++at_; }
      Require(result == "0" || result.front() != '0', "Noncanonical evidence number."); return {'n', result};
    }
    for (const char* literal : {"true", "false", "null"}) {
      const size_t length = std::strlen(literal);
      if (text_.compare(at_, length, literal) == 0) { at_ += length; return {literal[0] == 'n' ? '0' : 'b', literal}; }
    }
    throw std::runtime_error("Unexpected evidence JSON value.");
  }
  JsonObject Object(std::vector<JsonObject>* requests) {
    JsonObject result; Take('{'); Space();
    if (Peek() == '}') { ++at_; return result; }
    for (;;) {
      auto key = String(); Take(':'); JsonAtom atom{};
      if (key == "requests" && requests) {
        Take('['); Space();
        if (Peek() != ']') {
          for (;;) {
            Require(requests->size() < 3, "Too many game foreground request records."); requests->push_back(Object(nullptr));
            Space(); if (Peek() == ']') break; Take(',');
          }
        }
        Take(']'); atom = {'a', "requests"};
      } else atom = Atom();
      Require(result.emplace(key, std::move(atom)).second, "Duplicate game foreground evidence member.");
      Space(); if (Peek() == '}') { ++at_; return result; } Take(',');
    }
  }
};
void Expect(const JsonObject& object, const char* key, char kind, const std::string& value) {
  auto found = object.find(key);
  Require(found != object.end() && found->second.kind == kind && found->second.value == value,
          "Game foreground evidence does not match the retained native transcript.");
}
uint64_t PositiveInteger(const JsonObject& object, const char* key, uint64_t maximum) {
  const auto found = object.find(key);
  Require(found != object.end() && found->second.kind == 'n' && !found->second.value.empty(),
          "Game heartbeat evidence is missing a numeric field.");
  const auto& text = found->second.value;
  Require(text.front() != '0', "Game heartbeat evidence requires a positive canonical integer.");
  uint64_t value = 0;
  for (const char character : text) {
    Require(character >= '0' && character <= '9', "Game heartbeat evidence contains a noninteger value.");
    const auto digit = static_cast<uint64_t>(character - '0');
    Require(value < maximum / 10 || (value == maximum / 10 && digit <= maximum % 10),
            "Game heartbeat evidence integer exceeds its fixed bound.");
    value = value * 10 + digit;
  }
  return value;
}
void ValidateHeartbeatEvidence(const JsonObject& object, const std::vector<JsonObject>& requests,
                               const std::vector<std::pair<Frame, Frame>>& transcript) {
  constexpr auto maximum = static_cast<uint64_t>(std::numeric_limits<int64_t>::max());
  const auto thread = PositiveInteger(object, "heartbeatThreadId", std::numeric_limits<uint32_t>::max());
  PositiveInteger(object, "heartbeatFrequency", maximum);
  auto sequence = PositiveInteger(object, "readyHeartbeatSequence", maximum);
  auto ticks = PositiveInteger(object, "readyHeartbeatTicks", maximum);
  auto advance = [&](const JsonObject& record, const char* baseline_sequence_key, const char* baseline_ticks_key,
                     const char* sequence_key, const char* ticks_key) {
    const auto baseline_sequence = PositiveInteger(record, baseline_sequence_key, maximum);
    const auto baseline_ticks = PositiveInteger(record, baseline_ticks_key, maximum);
    const auto next_sequence = PositiveInteger(record, sequence_key, maximum);
    const auto next_ticks = PositiveInteger(record, ticks_key, maximum);
    Require(baseline_sequence >= sequence && baseline_ticks >= ticks &&
                next_sequence > baseline_sequence && next_ticks > baseline_ticks,
            "Game heartbeat evidence regressed or lacks a fresh Unity Update after its baseline.");
    sequence = next_sequence; ticks = next_ticks;
  };
  Require(requests.size() == 3 && transcript.size() == 3, "Game heartbeat evidence requires all three bound grants.");
  for (size_t index = 0; index < requests.size(); ++index) {
    Require(requests[index].size() == 7 && Get(transcript[index].first, 10) == thread,
            "Game heartbeat evidence request schema or bound Unity thread differs.");
    advance(requests[index], "baselineSequence", "baselineTicks", "heartbeatSequence", "heartbeatTicks");
  }
  advance(object, "eofBaselineSequence", "eofBaselineTicks", "eofHeartbeatSequence", "eofHeartbeatTicks");
}
bool UtcTimestamp(const std::string& value) {
  // DateTimeOffset.UtcNow.ToString("O") emitted by the pinned plugin: retain
  // the seven fractional digits and require an explicit zero UTC offset.
  if (value.size() != 33 || value[4] != '-' || value[7] != '-' || value[10] != 'T' ||
      value[13] != ':' || value[16] != ':' || value[19] != '.' || value.substr(27) != "+00:00") return false;
  for (size_t index = 0; index < 27; ++index) {
    if (index == 4 || index == 7 || index == 10 || index == 13 || index == 16 || index == 19) continue;
    if (value[index] < '0' || value[index] > '9') return false;
  }
  auto number = [&](size_t start, size_t length) {
    unsigned int result = 0;
    for (size_t index = start; index < start + length; ++index)
      result = result * 10 + static_cast<unsigned int>(value[index] - '0');
    return result;
  };
  const auto year = number(0, 4), month = number(5, 2), day = number(8, 2);
  if (!year || !month || month > 12 || !day || number(11, 2) > 23 || number(14, 2) > 59 || number(17, 2) > 59) return false;
  constexpr unsigned int days[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
  unsigned int maximum = days[month - 1];
  if (month == 2 && year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)) ++maximum;
  return day <= maximum;
}
}  // namespace

struct ForegroundGrantBridge::Impl {
  enum class Stage { connecting, readyRead, idle, requestWrite, replyRead, stopping, stopped };
  std::string run, pipe_name, nonce_hex;
  HWND probe; DWORD probe_thread, probe_pid = GetCurrentProcessId(), game_pid = 0, session = MAXDWORD;
  uint64_t probe_created, game_created = 0, nonce_low = 0, nonce_high = 0;
  std::wstring game_path;
  Handle pipe, event, game;
  OVERLAPPED overlapped{};
  bool io_active = false, bound = false, connected = false;
  Stage stage = Stage::connecting;
  Frame buffer{}, request{}; DWORD offset = 0;
  std::vector<std::pair<Frame, Frame>> transcript;
  ForegroundGrantObservation state;

  Impl(const std::string& id, HWND window, DWORD thread) : run(id), probe(window), probe_thread(thread), probe_created(Creation(GetCurrentProcess())) {
    Require(ProcessIdToSessionId(probe_pid, &session), "Cannot bind probe session for foreground channel.");
    std::array<uint64_t, 2> nonce{};
    Require(BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(nonce.data()), static_cast<ULONG>(sizeof(nonce)),
                           BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0 && (nonce[0] || nonce[1]), "Cannot generate foreground session nonce.");
    nonce_low = nonce[0]; nonce_high = nonce[1]; nonce_hex = Hex(nonce_low, true) + Hex(nonce_high, true);
    pipe_name = "mystia-focus-" + run + "-" + nonce_hex;
    Require(pipe_name.size() < 200 && std::all_of(pipe_name.begin(), pipe_name.end(), [](char c) {
      return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
    }), "Invalid fixed foreground pipe name.");
    Handle token; Require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value), "Cannot inspect current user for foreground ACL.");
    DWORD needed = 0; GetTokenInformation(token.value, TokenUser, nullptr, 0, &needed);
    Require(needed > 0 && needed <= 65536, "Invalid current user token size.");
    std::vector<unsigned char> storage(needed);
    Require(GetTokenInformation(token.value, TokenUser, storage.data(), needed, &needed), "Cannot read current user token.");
    LPWSTR sid = nullptr;
    Require(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(storage.data())->User.Sid, &sid), "Cannot encode foreground pipe user SID.");
    LocalMemory sid_memory; sid_memory.value = sid;
    // FILE_GENERIC_READ plus data/EA/attribute writes, but not the aliased
    // FILE_APPEND_DATA / FILE_CREATE_PIPE_INSTANCE permission.
    std::wstring sddl = L"D:P(A;;0x12019b;;;" + std::wstring(sid) + L")";
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr),
            "Cannot create current-user-only foreground pipe descriptor.");
    LocalMemory descriptor_memory; descriptor_memory.value = descriptor;
    SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES), descriptor, FALSE};
    std::wstring path = L"\\\\.\\pipe\\" + std::wstring(pipe_name.begin(), pipe_name.end());
    pipe.value = CreateNamedPipeW(path.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                                 PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                                 1, static_cast<DWORD>(kFrameSize), static_cast<DWORD>(kFrameSize), 0, &attributes);
    Require(pipe.value != INVALID_HANDLE_VALUE, "Cannot create the exclusive local foreground pipe.");
    event.value = CreateEventW(nullptr, TRUE, FALSE, nullptr); Require(event.value != nullptr, "Cannot create foreground I/O event.");
    overlapped.hEvent = event.value;
    BOOL accepted = ConnectNamedPipe(pipe.value, &overlapped);
    if (!accepted) {
      DWORD error = GetLastError();
      Require(error == ERROR_IO_PENDING || error == ERROR_PIPE_CONNECTED, "Cannot start foreground pipe connection.");
      io_active = error == ERROR_IO_PENDING; connected = error == ERROR_PIPE_CONNECTED;
    } else connected = true;
    state.target_pid = probe_pid;
  }
  void CheckPeer() const {
    Require(bound && game.value && WaitForSingleObject(game.value, 0) == WAIT_TIMEOUT && GetProcessId(game.value) == game_pid &&
                Creation(game.value) == game_created && _wcsicmp(ProcessPath(game.value).c_str(), game_path.c_str()) == 0,
            "Retained foreground game peer identity changed.");
    DWORD actual_session = MAXDWORD; ULONG actual_pid = 0;
    Require(ProcessIdToSessionId(game_pid, &actual_session) && actual_session == session &&
                GetNamedPipeClientProcessId(pipe.value, &actual_pid) && actual_pid == game_pid,
            "Foreground pipe client PID/session does not match the retained game.");
    DWORD owner = 0;
    Require(IsWindow(probe) && GetWindowThreadProcessId(probe, &owner) == probe_thread && owner == probe_pid &&
                GetAncestor(probe, GA_ROOT) == probe, "Foreground target probe window identity changed.");
  }
  void Identity(Frame& frame) const {
    Put(frame, 0, nonce_low); Put(frame, 1, nonce_high); Put(frame, 2, game_pid); Put(frame, 3, game_created);
    Put(frame, 4, probe_pid); Put(frame, 5, probe_created);
  }
  void CheckIdentity(const Frame& frame) const {
    Frame expected{}; Identity(expected);
    for (size_t i = 0; i < 6; ++i) Require(Get(frame, i) == Get(expected, i), "Foreground frame nonce/process identity differs.");
  }
  void ResetIo() {
    Require(!io_active, "Cannot replace pending foreground I/O.");
    overlapped = {}; overlapped.hEvent = event.value;
    Require(ResetEvent(event.value), "Cannot reset foreground I/O event.");
  }
  bool Transfer() {
    DWORD count = 0;
    if (io_active) {
      if (!GetOverlappedResult(pipe.value, &overlapped, &count, FALSE)) {
        DWORD error = GetLastError();
        if (error == ERROR_IO_INCOMPLETE) return false;
        io_active = false; throw std::runtime_error("Foreground pipe I/O failed or disconnected; no retry is allowed.");
      }
      io_active = false;
    } else {
      ResetIo();
      DWORD remaining = static_cast<DWORD>(kFrameSize) - offset;
      BOOL success = stage == Stage::requestWrite
          ? WriteFile(pipe.value, buffer.data() + offset, remaining, &count, &overlapped)
          : ReadFile(pipe.value, buffer.data() + offset, remaining, &count, &overlapped);
      if (!success) {
        Require(GetLastError() == ERROR_IO_PENDING, "Cannot start foreground frame I/O; no retry is allowed.");
        io_active = true; return false;
      }
    }
    Require(count > 0 && count <= kFrameSize - offset, "Foreground pipe returned an empty or oversized frame fragment.");
    offset += count; return offset == kFrameSize;
  }
  void CheckNoExtra() const {
    DWORD available = 0;
    Require(PeekNamedPipe(pipe.value, nullptr, 0, nullptr, &available, nullptr), "Foreground channel disconnected unexpectedly.");
    Require(available == 0, "Unexpected extra foreground frame bytes; replay or unsolicited frame rejected.");
  }
  void Poll() {
    Require(stage != Stage::stopping && stage != Stage::stopped, "Foreground channel is closing.");
    if (stage == Stage::connecting) {
      if (io_active) {
        DWORD count = 0;
        if (!GetOverlappedResult(pipe.value, &overlapped, &count, FALSE)) {
          DWORD error = GetLastError(); if (error == ERROR_IO_INCOMPLETE) return;
          io_active = false; throw std::runtime_error("Foreground pipe connection failed.");
        }
        io_active = false; connected = true;
      }
      Require(connected, "Foreground pipe connection state is invalid."); CheckPeer();
      stage = Stage::readyRead; offset = 0; buffer = {};
    }
    CheckPeer();
    if (stage == Stage::idle) { CheckNoExtra(); return; }
    // At most one complete frame per poll. Partial completions are resumed by
    // the next inspect, never by a blocking loop on the platform thread.
    if (!Transfer()) return;
    if (stage == Stage::readyRead) {
      CheckHeader(buffer, 1); CheckIdentity(buffer);
      for (size_t i = 6; i < 20; ++i) Require(Get(buffer, i) == 0, "Foreground ready contains request/result data.");
      CheckNoExtra(); state.ready = true; state.identity_matched = true; state.issuer_pid = game_pid; stage = Stage::idle;
    } else if (stage == Stage::requestWrite) {
      stage = Stage::replyRead; buffer = {}; offset = 0;
    } else {
      CheckHeader(buffer, 3); CheckIdentity(buffer);
      for (size_t i = 0; i < 12; ++i) Require(Get(buffer, i) == Get(request, i), "Foreground reply does not match the outstanding request.");
      Require(Get(buffer, 13) <= MAXDWORD && Get(buffer, 15) <= MAXDWORD && Get(buffer, 16) <= 1 && Get(buffer, 17) <= 1 &&
                  Get(buffer, 18) <= MAXDWORD && Get(buffer, 19) == 0 && (!Get(buffer, 17) || Get(buffer, 16)),
              "Foreground reply result fields are invalid.");
      state.response_sequence = state.sequence; state.response_received = true;
      state.foreground_hwnd = Get(buffer, 12); state.foreground_pid = static_cast<DWORD>(Get(buffer, 13));
      state.foreground_after_hwnd = Get(buffer, 14); state.foreground_after_pid = static_cast<DWORD>(Get(buffer, 15));
      state.attempted = Get(buffer, 16) != 0; state.succeeded = Get(buffer, 17) != 0; state.error = static_cast<DWORD>(Get(buffer, 18));
      transcript.emplace_back(request, buffer); stage = Stage::idle;
      // A failed reply may intentionally be followed by EOF; its complete
      // outcome must remain available rather than being hidden by Peek failure.
      if (state.succeeded) CheckNoExtra();
    }
  }
  void BeginStop() {
    if (stage == Stage::stopped || stage == Stage::stopping) return;
    stage = Stage::stopping;
    if (io_active && !CancelIoEx(pipe.value, &overlapped))
      Require(GetLastError() == ERROR_NOT_FOUND, "Cannot cancel pending foreground channel I/O.");
  }
  bool PollStop() {
    if (stage == Stage::stopped) return true;
    Require(stage == Stage::stopping, "Foreground stop was not requested.");
    if (io_active) {
      DWORD count = 0;
      if (!GetOverlappedResult(pipe.value, &overlapped, &count, FALSE)) {
        DWORD error = GetLastError(); if (error == ERROR_IO_INCOMPLETE) return false;
        // Terminal errors also mean the kernel has released OVERLAPPED/buffer.
      }
      io_active = false;
    }
    if (connected && !DisconnectNamedPipe(pipe.value)) {
      DWORD error = GetLastError();
      Require(error == ERROR_PIPE_NOT_CONNECTED || error == ERROR_BROKEN_PIPE, "Cannot disconnect foreground channel.");
    }
    CloseHandle(pipe.value); pipe.value = nullptr; connected = false; stage = Stage::stopped; return true;
  }
};

ForegroundGrantBridge::ForegroundGrantBridge(const std::string& run, HWND probe, DWORD thread)
    : impl_(std::make_unique<Impl>(run, probe, thread)) {}
ForegroundGrantBridge::~ForegroundGrantBridge() {
  try {
    impl_->BeginStop();
    if (!impl_->PollStop()) { WaitForSingleObject(impl_->event.value, 2000); if (!impl_->PollStop()) { impl_.release(); return; } }
  } catch (...) {
    // Never release a still-pending OVERLAPPED/buffer. This failure is confined
    // to process teardown; normal finish requires PollStop before PASS.
    if (impl_->io_active) impl_.release();
  }
}
void ForegroundGrantBridge::BindGame(HANDLE game, DWORD pid, uint64_t creation, const std::wstring& executable) {
  auto& self = *impl_; Require(!self.bound && pid != 0 && creation != 0, "Foreground game binding cannot be repeated.");
  Require(DuplicateHandle(GetCurrentProcess(), game, GetCurrentProcess(), &self.game.value, 0, FALSE, DUPLICATE_SAME_ACCESS),
          "Cannot retain foreground game identity handle.");
  self.game_pid = pid; self.game_created = creation; self.game_path = executable; self.bound = true;
}
const std::string& ForegroundGrantBridge::pipe_name() const { return impl_->pipe_name; }
const std::string& ForegroundGrantBridge::nonce_hex() const { return impl_->nonce_hex; }
uint64_t ForegroundGrantBridge::probe_creation() const { return impl_->probe_created; }
void ForegroundGrantBridge::Poll() { impl_->Poll(); }
void ForegroundGrantBridge::Request(uint64_t request_id, HWND game_window, DWORD game_thread) {
  auto& self = *impl_;
  Require(self.stage == Impl::Stage::idle && self.state.ready && self.state.identity_matched &&
              self.state.sequence < 3 && request_id > self.state.request_id, "Foreground grant is pending, repeated or out of sequence.");
  self.CheckPeer(); self.CheckNoExtra(); DWORD pid = 0;
  Require(IsWindow(game_window) && GetWindowThreadProcessId(game_window, &pid) == game_thread && pid == self.game_pid &&
              GetAncestor(game_window, GA_ROOT) == game_window, "Foreground request game window identity differs.");
  const uint64_t next = self.state.sequence + 1;
  self.state = {}; self.state.ready = true; self.state.identity_matched = true; self.state.sequence = next;
  self.state.request_id = request_id; self.state.issuer_pid = self.game_pid; self.state.target_pid = self.probe_pid;
  self.request = {}; Header(self.request, 2); self.Identity(self.request);
  Put(self.request, 6, next); Put(self.request, 7, request_id); Put(self.request, 8, HwndValue(game_window));
  Put(self.request, 9, HwndValue(self.probe)); Put(self.request, 10, game_thread); Put(self.request, 11, self.probe_thread);
  self.buffer = self.request; self.offset = 0; self.stage = Impl::Stage::requestWrite;
}
void ForegroundGrantBridge::MarkActivationRequested() {
  auto& state = impl_->state;
  Require(state.response_received && state.attempted && state.succeeded && !state.activation_requested,
          "Foreground activation lacks a fresh successful grant or was already requested."); state.activation_requested = true;
}
const ForegroundGrantObservation& ForegroundGrantBridge::observation() const { return impl_->state; }
void ForegroundGrantBridge::BeginStop() { impl_->BeginStop(); }
bool ForegroundGrantBridge::PollStop() { return impl_->PollStop(); }
bool ForegroundGrantBridge::stopped() const { return impl_->stage == Impl::Stage::stopped; }
void ForegroundGrantBridge::ValidateEvidence(const std::string& json, const std::string& probe_hash,
                                            const std::string& descriptor_hash) const {
  const auto& self = *impl_;
  Require(stopped() && self.state.ready && self.state.identity_matched && self.transcript.size() == 3,
          "Normal foreground evidence requires a stopped, identified, three-grant channel.");
  std::vector<JsonObject> requests; const auto object = EvidenceReader(json).Read(requests);
  Require(object.size() == 27 && requests.size() == 3, "Game foreground evidence has an unexpected schema/count.");
  Expect(object, "schemaVersion", 'n', "2"); Expect(object, "kind", 's', "mystia-game-foreground-evidence");
  Expect(object, "runId", 's', self.run); Expect(object, "gitSha", 's', MYSTIA_WINDOW_PROBE_GIT_SHA); Expect(object, "outcome", 's', "PASS");
  Expect(object, "gamePid", 'n', std::to_string(self.game_pid)); Expect(object, "probePid", 'n', std::to_string(self.probe_pid));
  Expect(object, "pipeServerPid", 'n', std::to_string(self.probe_pid));
  Expect(object, "gameCreationHex", 's', Hex(self.game_created)); Expect(object, "probeCreationHex", 's', Hex(self.probe_created));
  Expect(object, "probeExecutableSha256", 's', probe_hash); Expect(object, "descriptorSha256", 's', descriptor_hash);
  Expect(object, "readySent", 'b', "true"); Expect(object, "pipeEof", 'b', "true"); Expect(object, "successfulGrants", 'n', "3");
  Expect(object, "requests", 'a', "requests"); Expect(object, "error", '0', "null");
  ValidateHeartbeatEvidence(object, requests, self.transcript);
  for (const char* key : {"startedAtUtc", "finishedAtUtc"}) {
    auto found = object.find(key); Require(found != object.end() && found->second.kind == 's' && UtcTimestamp(found->second.value),
                                         "Game foreground evidence timestamps are not the fixed UTC format.");
  }
  for (size_t index = 0; index < requests.size(); ++index) {
    const auto& request = self.transcript[index].first; const auto& reply = self.transcript[index].second;
    Require(Get(request, 6) == index + 1 && Get(reply, 16) == 1 && Get(reply, 17) == 1 &&
                Get(reply, 12) == Get(request, 8) && Get(reply, 14) == Get(request, 8) &&
                Get(reply, 13) == self.game_pid && Get(reply, 15) == self.game_pid,
            "Game foreground evidence contains an unsuccessful grant or changed foreground.");
    Require(requests[index].size() == 7, "Unexpected game foreground request evidence fields.");
    Expect(requests[index], "requestFrameHex", 's', FrameHex(self.transcript[index].first));
    Expect(requests[index], "replyFrameHex", 's', FrameHex(self.transcript[index].second)); Expect(requests[index], "error", '0', "null");
  }
}
