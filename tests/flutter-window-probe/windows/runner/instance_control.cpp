// Winsock2 must precede lifecycle_shared.h (which includes windows.h).
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>

#include "instance_control.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace lifecycle_probe {
namespace {
constexpr size_t kMaxMessage = 1024;
constexpr ULONGLONG kDeadlineMs = 5000;
constexpr std::string_view kPrefix = "mystia-steward-companion:";

struct Failure final : std::runtime_error {
  Failure(const char* operation, DWORD value)
      : std::runtime_error(std::string(operation) + " failed: " +
                           std::to_string(value)),
        code(value) {}
  DWORD code;
};

class Winsock final {
 public:
  Winsock() {
    WSADATA data{};
    const int error = WSAStartup(MAKEWORD(2, 2), &data);
    if (error != 0) throw Failure("WSAStartup", static_cast<DWORD>(error));
  }
  ~Winsock() { WSACleanup(); }
  Winsock(const Winsock&) = delete;
  Winsock& operator=(const Winsock&) = delete;
};

class Socket final {
 public:
  Socket() = default;
  ~Socket() { Reset(); }
  Socket(const Socket&) = delete;
  Socket& operator=(const Socket&) = delete;
  SOCKET get() const { return value_; }
  bool valid() const { return value_ != INVALID_SOCKET; }
  void Reset(SOCKET next = INVALID_SOCKET) {
    if (valid()) closesocket(value_);
    value_ = next;
  }
  void Create() {
    Reset(WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, nullptr, 0,
                    WSA_FLAG_NO_HANDLE_INHERIT));
    if (!valid()) throw Failure("socket", static_cast<DWORD>(WSAGetLastError()));
  }

 private:
  SOCKET value_ = INVALID_SOCKET;
};

void Nonblocking(SOCKET socket) {
  u_long enabled = 1;
  if (ioctlsocket(socket, FIONBIO, &enabled) == SOCKET_ERROR) {
    throw Failure("nonblocking", static_cast<DWORD>(WSAGetLastError()));
  }
}

void Exclusive(SOCKET socket) {
  const BOOL enabled = TRUE;
  if (setsockopt(socket, SOL_SOCKET, SO_EXCLUSIVEADDRUSE,
                 reinterpret_cast<const char*>(&enabled), sizeof(enabled)) ==
      SOCKET_ERROR) {
    throw Failure("exclusive bind", static_cast<DWORD>(WSAGetLastError()));
  }
}

bool IsProbePort(LONG port) {
  return port > 0 && port <= 65535 && port != 32145 && port != 32146;
}

sockaddr_in Address(uint16_t port) {
  sockaddr_in address{};
  address.sin_family = AF_INET;
  address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  address.sin_port = htons(port);
  return address;
}

template <size_t N>
std::string FixtureString(const char (&value)[N]) {
  const auto end = std::find(value, value + N, '\0');
  if (end == value || end == value + N) {
    throw Failure("fixture string", ERROR_INVALID_DATA);
  }
  const std::string result(value, end);
  for (const unsigned char character : result) {
    if (character <= 32 || character >= 127) {
      throw Failure("fixture string", ERROR_INVALID_DATA);
    }
  }
  return result;
}

void ValidateShared(const Shared& shared) {
  if (shared.magic != kMagic || shared.nonce == 0 || shared.controller_pid == 0) {
    throw Failure("shared identity", ERROR_INVALID_DATA);
  }
  (void)FixtureString(shared.expected_endpoint);
  (void)FixtureString(shared.expected_token);
}

struct Parsed {
  ControlAction action = ControlAction::none;
  bool game_pid = false;
  bool endpoint = false;
  bool token = false;
};

bool Parse(std::string_view message, const Shared& shared, Parsed& parsed) {
  if (message.empty() || message.size() > kMaxMessage) return false;
  size_t offset = 0;
  size_t line_number = 0;
  while (offset < message.size()) {
    const size_t newline = message.find('\n', offset);
    const size_t end = newline == std::string_view::npos ? message.size() : newline;
    auto line = message.substr(offset, end - offset);
    if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
    if (line.empty()) return false;
    for (const unsigned char character : line) {
      if (character <= 32 || character >= 127) return false;
    }
    if (line_number == 0) {
      if (line == "mystia-steward-companion:show") {
        parsed.action = ControlAction::show;
      } else if (line == "mystia-steward-companion:toggle") {
        parsed.action = ControlAction::toggle;
      } else if (line == "mystia-steward-companion:exit") {
        parsed.action = ControlAction::exit;
      } else {
        return false;
      }
    } else if (line.substr(0, 11) == "--game-pid=") {
      if (parsed.game_pid || line.substr(11) != std::to_string(shared.controller_pid)) {
        return false;
      }
      parsed.game_pid = true;
    } else if (line.substr(0, 6) == "--api=") {
      if (parsed.endpoint || line.substr(6) != FixtureString(shared.expected_endpoint)) {
        return false;
      }
      parsed.endpoint = true;
    } else if (line.substr(0, 8) == "--token=") {
      if (parsed.token || line.substr(8) != FixtureString(shared.expected_token)) {
        return false;
      }
      parsed.token = true;
    } else {
      return false;
    }
    ++line_number;
    offset = newline == std::string_view::npos ? message.size() : newline + 1;
  }
  return parsed.action != ControlAction::none;
}

DWORD ListenerOwner(uint16_t port) {
  DWORD size = 0;
  DWORD error = GetExtendedTcpTable(nullptr, &size, FALSE, AF_INET,
                                    TCP_TABLE_OWNER_PID_LISTENER, 0);
  if (error != ERROR_INSUFFICIENT_BUFFER) throw Failure("TCP owner size", error);
  for (int attempt = 0; attempt < 3; ++attempt) {
    if (size < sizeof(MIB_TCPTABLE_OWNER_PID) || size > 1024 * 1024) {
      throw Failure("TCP owner table size", ERROR_INVALID_DATA);
    }
    // DWORD backing guarantees the table's natural alignment.
    std::vector<DWORD> storage((size + sizeof(DWORD) - 1) / sizeof(DWORD));
    error = GetExtendedTcpTable(storage.data(), &size, FALSE, AF_INET,
                                TCP_TABLE_OWNER_PID_LISTENER, 0);
    if (error == ERROR_INSUFFICIENT_BUFFER) continue;
    if (error != NO_ERROR) throw Failure("TCP owner table", error);
    const auto* table = reinterpret_cast<const MIB_TCPTABLE_OWNER_PID*>(storage.data());
    const size_t capacity = storage.size() * sizeof(DWORD);
    if (table->dwNumEntries >
        (capacity - offsetof(MIB_TCPTABLE_OWNER_PID, table)) / sizeof(MIB_TCPROW_OWNER_PID)) {
      throw Failure("TCP owner table bounds", ERROR_INVALID_DATA);
    }
    DWORD owner = 0;
    for (DWORD i = 0; i < table->dwNumEntries; ++i) {
      const auto& row = table->table[i];
      if (ntohs(static_cast<u_short>(row.dwLocalPort)) != port) continue;
      if (row.dwLocalAddr == htonl(INADDR_ANY)) {
        throw Failure("wildcard control listener", ERROR_INVALID_OWNER);
      }
      if (row.dwLocalAddr != htonl(INADDR_LOOPBACK)) continue;
      if (owner != 0 || row.dwOwningPid == 0) {
        throw Failure("ambiguous control owner", ERROR_INVALID_OWNER);
      }
      owner = row.dwOwningPid;
    }
    if (owner == 0) throw Failure("missing control owner", ERROR_NOT_FOUND);
    return owner;
  }
  throw Failure("unstable TCP owner table", ERROR_RETRY);
}

void WaitSocket(SOCKET socket, bool writing, ULONGLONG deadline) {
  const ULONGLONG now = GetTickCount64();
  if (now >= deadline) throw Failure("control deadline", WSAETIMEDOUT);
  const ULONGLONG remaining = deadline - now;
  timeval timeout{};
  timeout.tv_sec = static_cast<long>(remaining / 1000);
  timeout.tv_usec = static_cast<long>((remaining % 1000) * 1000);
  fd_set wanted{}, errors{};
  FD_ZERO(&wanted);
  FD_ZERO(&errors);
  FD_SET(socket, &wanted);
  FD_SET(socket, &errors);
  const int count = select(0, writing ? nullptr : &wanted,
                           writing ? &wanted : nullptr, &errors, &timeout);
  if (count == 0) throw Failure("control deadline", WSAETIMEDOUT);
  if (count == SOCKET_ERROR) throw Failure("select", static_cast<DWORD>(WSAGetLastError()));
  int error = 0;
  int length = sizeof(error);
  if (getsockopt(socket, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &length) == SOCKET_ERROR) {
    throw Failure("socket result", static_cast<DWORD>(WSAGetLastError()));
  }
  if (error != 0) throw Failure("socket operation", static_cast<DWORD>(error));
  if (FD_ISSET(socket, &errors) || !FD_ISSET(socket, &wanted)) {
    throw Failure("socket readiness", ERROR_INVALID_DATA);
  }
}

void SendBytes(SOCKET socket, std::string_view bytes, ULONGLONG deadline,
               Shared& shared) {
  size_t offset = 0;
  while (offset < bytes.size()) {
    if (GetTickCount64() >= deadline) throw Failure("send deadline", WSAETIMEDOUT);
    const int sent = send(socket, bytes.data() + offset,
                          static_cast<int>(bytes.size() - offset), 0);
    if (sent == SOCKET_ERROR) {
      const int error = WSAGetLastError();
      if (error != WSAEWOULDBLOCK) throw Failure("send", static_cast<DWORD>(error));
      WaitSocket(socket, true, deadline);
      continue;
    }
    if (sent == 0) throw Failure("send closed", WSAECONNRESET);
    offset += static_cast<size_t>(sent);
    InterlockedExchangeAdd(&shared.peer_bytes_sent, sent);
  }
}

struct WindowCount {
  DWORD pid;
  LONG count = 0;
};

BOOL CALLBACK CountPeerWindow(HWND window, LPARAM parameter) {
  auto& count = *reinterpret_cast<WindowCount*>(parameter);
  DWORD pid = 0;
  if (GetWindowThreadProcessId(window, &pid) != 0 && pid == count.pid) ++count.count;
  return TRUE;
}

void PeerWork(Shared& shared, ControlAction action) {
  ValidateShared(shared);
  if (action != ControlAction::show && action != ControlAction::toggle &&
      action != ControlAction::exit && action != ControlAction::invalid) {
    throw Failure("peer action", ERROR_INVALID_PARAMETER);
  }
  const LONG port = Read(&shared.control_port);
  const LONG primary_pid = Read(&shared.primary_pid);
  if (!IsProbePort(port) || primary_pid <= 0 ||
      static_cast<DWORD>(primary_pid) == GetCurrentProcessId()) {
    throw Failure("peer server identity", ERROR_INVALID_DATA);
  }
  Winsock winsock;
  const auto address = Address(static_cast<uint16_t>(port));
  {
    Socket claim;
    claim.Create();
    Exclusive(claim.get());
    if (bind(claim.get(), reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == 0) {
      throw Failure("peer unexpectedly claimed port", ERROR_ALREADY_EXISTS);
    }
    const int error = WSAGetLastError();
    Publish(&shared.peer_bind_error, error);
    // Both are documented for a conflicting exclusive bind on Windows.
    if (error != WSAEADDRINUSE && error != WSAEACCES) {
      throw Failure("peer bind", static_cast<DWORD>(error));
    }
  }
  const DWORD owner = ListenerOwner(static_cast<uint16_t>(port));
  Publish(&shared.peer_server_pid, static_cast<LONG>(owner));
  if (owner != static_cast<DWORD>(primary_pid)) {
    throw Failure("control owner mismatch", ERROR_INVALID_OWNER);
  }
  Socket socket;
  socket.Create();
  Nonblocking(socket.get());
  const ULONGLONG deadline = GetTickCount64() + kDeadlineMs;
  if (connect(socket.get(), reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) {
    const int error = WSAGetLastError();
    if (error != WSAEWOULDBLOCK) throw Failure("connect", static_cast<DWORD>(error));
    WaitSocket(socket.get(), true, deadline);
  }
  if (Read(&shared.primary_pid) != primary_pid || Read(&shared.control_port) != port ||
      ListenerOwner(static_cast<uint16_t>(port)) != owner) {
    throw Failure("control owner changed", ERROR_INVALID_OWNER);
  }

  const LONG applied = Read(&shared.control_applied);
  const LONG updates = Read(&shared.connection_updates);
  const LONG activations = Read(&shared.connection_activations);
  const LONG rejected = Read(&shared.control_rejected);
  std::string message;
  if (action == ControlAction::exit) {
    message = std::string(kPrefix) + "exit\n";
  } else if (action == ControlAction::invalid) {
    message = std::string(kPrefix) + "unknown\n--game-pid=0\n--api=invalid\n--token=invalid\n";
  } else {
    const std::string command = std::string(kPrefix) +
        (action == ControlAction::show ? "show\n" : "toggle\n");
    message = command + "--game-pid=" + std::to_string(shared.controller_pid) +
        "\n--api=" + FixtureString(shared.expected_endpoint) +
        "\n--token=" + FixtureString(shared.expected_token) + "\n";
    if (action == ControlAction::show) {
      SendBytes(socket.get(), command, deadline, shared);
      while (Read(&shared.control_pending_bytes) < static_cast<LONG>(command.size())) {
        if (GetTickCount64() >= deadline) throw Failure("partial read deadline", WSAETIMEDOUT);
        if (Read(&shared.control_rejected) != rejected) {
          throw Failure("partial frame rejected", ERROR_INVALID_DATA);
        }
        Sleep(1);
      }
      if (Read(&shared.control_applied) != applied ||
          Read(&shared.connection_updates) != updates ||
          Read(&shared.connection_activations) != activations) {
        throw Failure("partial frame applied", ERROR_INVALID_DATA);
      }
      message.erase(0, command.size());
    }
  }
  SendBytes(socket.get(), message, deadline, shared);
  if (shutdown(socket.get(), SD_SEND) == SOCKET_ERROR) {
    throw Failure("send EOF", static_cast<DWORD>(WSAGetLastError()));
  }
  // EOF is transport completion only. The controller separately checks the
  // primary's applied/rejected counters and actual window/UI observations.
  for (;;) {
    char byte = 0;
    const int received = recv(socket.get(), &byte, 1, 0);
    if (received == 0) break;
    if (received > 0) throw Failure("unexpected legacy response", ERROR_INVALID_DATA);
    const int error = WSAGetLastError();
    if (error != WSAEWOULDBLOCK) throw Failure("receive EOF", static_cast<DWORD>(error));
    WaitSocket(socket.get(), false, deadline);
  }
}
}  // namespace

struct InstanceControlServer::Impl {
  Impl(Shared& value, std::function<void(ControlAction)> callback)
      : shared(value), apply(std::move(callback)) {}
  Shared& shared;
  std::function<void(ControlAction)> apply;
  Winsock winsock;
  Socket listener;
  Socket client;
  uint16_t bound_port = 0;
  ULONGLONG deadline = 0;
  std::string pending;
  bool has_endpoint = false;
  bool has_token = false;

  void CloseClient() {
    client.Reset();
    pending.clear();
    Publish(&shared.control_pending_bytes, 0);
  }
  void Reject() {
    CloseClient();
    InterlockedIncrement(&shared.control_rejected);
  }
  void Complete() {
    Parsed parsed;
    const bool valid = Parse(pending, shared, parsed);
    CloseClient();
    if (!valid) {
      InterlockedIncrement(&shared.control_rejected);
      return;
    }
    const bool changed = (parsed.endpoint && !has_endpoint) || (parsed.token && !has_token);
    if (parsed.game_pid) Publish(&shared.connection_game_pid, static_cast<LONG>(shared.controller_pid));
    if (parsed.endpoint) {
      has_endpoint = true;
      Publish(&shared.endpoint_matches, 1);
    }
    if (parsed.token) {
      has_token = true;
      Publish(&shared.token_matches, 1);
    }
    if (changed) {
      InterlockedIncrement(&shared.connection_updates);
    } else if (parsed.endpoint && parsed.token &&
               (parsed.action == ControlAction::show || parsed.action == ControlAction::toggle)) {
      InterlockedIncrement(&shared.connection_activations);
    }
    apply(parsed.action);
    Publish(&shared.control_last_action, static_cast<LONG>(parsed.action));
    InterlockedIncrement(&shared.control_applied);
  }
};

InstanceControlServer::InstanceControlServer(Shared& shared,
                                           std::function<void(ControlAction)> apply)
    : impl_(std::make_unique<Impl>(shared, std::move(apply))) {}

InstanceControlServer::~InstanceControlServer() = default;

void InstanceControlServer::Start() {
  auto& state = *impl_;
  if (state.listener.valid()) throw Failure("listener already started", ERROR_ALREADY_EXISTS);
  ValidateShared(state.shared);
  if (!state.apply) throw Failure("missing control callback", ERROR_INVALID_PARAMETER);
  const LONG requested = state.shared.requested_port;
  if (requested != 0 && !IsProbePort(requested)) {
    throw Failure("reserved control port", ERROR_INVALID_PARAMETER);
  }
  // Port zero is selected while bound, never by probing and releasing a port.
  for (int attempt = 0; attempt < 16; ++attempt) {
    state.listener.Create();
    Exclusive(state.listener.get());
    const auto address = Address(static_cast<uint16_t>(requested));
    if (bind(state.listener.get(), reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) {
      throw Failure("control bind", static_cast<DWORD>(WSAGetLastError()));
    }
    sockaddr_in bound{};
    int length = sizeof(bound);
    if (getsockname(state.listener.get(), reinterpret_cast<sockaddr*>(&bound), &length) == SOCKET_ERROR) {
      throw Failure("bound control address", static_cast<DWORD>(WSAGetLastError()));
    }
    const uint16_t port = ntohs(bound.sin_port);
    if (!IsProbePort(port)) {
      state.listener.Reset();
      continue;
    }
    Nonblocking(state.listener.get());
    if (listen(state.listener.get(), 4) == SOCKET_ERROR) {
      throw Failure("control listen", static_cast<DWORD>(WSAGetLastError()));
    }
    state.bound_port = port;
    Publish(&state.shared.control_port, port);
    return;
  }
  throw Failure("isolated control port allocation", ERROR_RETRY);
}

void InstanceControlServer::Poll() {
  auto& state = *impl_;
  if (!state.listener.valid()) return;
  if (!state.client.valid()) {
    sockaddr_in address{};
    int length = sizeof(address);
    const SOCKET accepted = accept(state.listener.get(), reinterpret_cast<sockaddr*>(&address), &length);
    if (accepted == INVALID_SOCKET) {
      const int error = WSAGetLastError();
      if (error == WSAEWOULDBLOCK) return;
      throw Failure("control accept", static_cast<DWORD>(error));
    }
    state.client.Reset(accepted);
    if (address.sin_family != AF_INET || address.sin_addr.s_addr != htonl(INADDR_LOOPBACK)) {
      state.Reject();
      return;
    }
    if (!SetHandleInformation(reinterpret_cast<HANDLE>(accepted), HANDLE_FLAG_INHERIT, 0)) {
      throw Failure("accepted socket inheritance", GetLastError());
    }
    Nonblocking(accepted);
    state.deadline = GetTickCount64() + kDeadlineMs;
    state.pending.clear();
    Publish(&state.shared.control_pending_bytes, 0);
  }
  // Each timer tick is bounded even if a sender continuously feeds the socket.
  for (int read = 0; read < 16; ++read) {
    if (GetTickCount64() >= state.deadline) {
      state.Reject();
      return;
    }
    std::array<char, kMaxMessage + 1> buffer{};
    const int received = recv(state.client.get(), buffer.data(),
                              static_cast<int>(buffer.size() - state.pending.size()), 0);
    if (received == SOCKET_ERROR) {
      const int error = WSAGetLastError();
      if (error == WSAEWOULDBLOCK) return;
      state.Reject();
      return;
    }
    if (received == 0) {
      state.Complete();
      return;
    }
    InterlockedIncrement(&state.shared.control_receive_calls);
    state.pending.append(buffer.data(), static_cast<size_t>(received));
    Publish(&state.shared.control_pending_bytes, static_cast<LONG>(state.pending.size()));
    if (state.pending.size() > kMaxMessage) {
      state.Reject();
      return;
    }
  }
}

void InstanceControlServer::Stop() {
  impl_->CloseClient();
  impl_->listener.Reset();
  // Keep the actual bound port as evidence after shutdown.
}

uint16_t InstanceControlServer::port() const { return impl_->bound_port; }

int RunInstancePeer(Shared& shared, ControlAction action) {
  Publish(&shared.peer_pid, static_cast<LONG>(GetCurrentProcessId()));
  Publish(&shared.peer_action, static_cast<LONG>(action));
  Publish(&shared.peer_bind_error, 0);
  Publish(&shared.peer_server_pid, 0);
  Publish(&shared.peer_bytes_sent, 0);
  Publish(&shared.peer_window_count, -1);
  Publish(&shared.peer_done, 0);
  Publish(&shared.peer_error, 0);
  DWORD error = ERROR_SUCCESS;
  try {
    PeerWork(shared, action);
  } catch (const Failure& failure) {
    error = failure.code;
  } catch (...) {
    error = ERROR_UNHANDLED_EXCEPTION;
  }
  WindowCount windows{GetCurrentProcessId()};
  if (!EnumWindows(CountPeerWindow, reinterpret_cast<LPARAM>(&windows))) {
    if (error == ERROR_SUCCESS) error = ERROR_INVALID_DATA;
  } else {
    Publish(&shared.peer_window_count, windows.count);
    if (windows.count != 0 && error == ERROR_SUCCESS) error = ERROR_INVALID_WINDOW_HANDLE;
  }
  Publish(&shared.peer_error, static_cast<LONG>(error));
  Publish(&shared.peer_done, 1);
  return error == ERROR_SUCCESS ? 0 : 1;
}

}  // namespace lifecycle_probe
