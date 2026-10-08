// Included inside the control bridge's private namespace, after ReportReader.
// Observes an unchanged 1.3.1 cached publication. A stale snapshot says nothing
// about whether Update ran: the original publisher deduplicates content.
#pragma once

uint64_t LegacyUtcTicks(const std::string& value) {
  Require(value.size() >= 20 && value.size() <= 28 && value[4] == '-' &&
              value[7] == '-' && value[10] == 'T' && value[13] == ':' &&
              value[16] == ':' && value.back() == 'Z', "Invalid legacy snapshot UTC shape.");
  auto number = [&](size_t at, size_t length) {
    WORD result = 0;
    for (size_t index = at; index < at + length; ++index) {
      Require(value[index] >= '0' && value[index] <= '9', "Invalid legacy snapshot UTC digit.");
      result = static_cast<WORD>(result * 10 + value[index] - '0');
    }
    return result;
  };
  SYSTEMTIME time{};
  time.wYear = number(0, 4); time.wMonth = number(5, 2); time.wDay = number(8, 2);
  time.wHour = number(11, 2); time.wMinute = number(14, 2); time.wSecond = number(17, 2);
  Require(time.wYear >= 2020 && time.wYear <= 2100, "Legacy snapshot UTC year is outside this probe's bound.");
  uint64_t fraction = 0;
  if (value.size() != 20) {
    Require(value.size() >= 22 && value[19] == '.', "Invalid legacy snapshot UTC fraction.");
    for (size_t index = 20; index < 27; ++index) {
      fraction *= 10;
      if (index + 1 < value.size()) {
        Require(value[index] >= '0' && value[index] <= '9', "Invalid legacy snapshot UTC fraction digit.");
        fraction += static_cast<unsigned int>(value[index] - '0');
      }
    }
  }
  FILETIME file{};
  Require(SystemTimeToFileTime(&time, &file) != FALSE, "Invalid legacy snapshot UTC calendar date.");
  return (static_cast<uint64_t>(file.dwHighDateTime) << 32 | file.dwLowDateTime) + fraction;
}

uint64_t LegacyCurrentUtcTicks() {
  FILETIME file{}; GetSystemTimeAsFileTime(&file);
  return static_cast<uint64_t>(file.dwHighDateTime) << 32 | file.dwLowDateTime;
}

std::string LegacySnapshotBody(const std::string& response) {
  const auto split = response.find("\r\n\r\n");
  Require(split != std::string::npos && split <= 8192 &&
              response.rfind("HTTP/1.1 200 OK\r\n", 0) == 0, "Legacy snapshot HTTP status/header is invalid.");
  std::map<std::string, std::string> headers;
  size_t at = response.find("\r\n") + 2;
  while (at < split) {
    const auto end = response.find("\r\n", at), colon = response.find(": ", at);
    Require(end != std::string::npos && colon != std::string::npos && colon < end,
            "Legacy snapshot HTTP header is malformed.");
    auto name = response.substr(at, colon - at);
    for (auto& character : name) {
      Require((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z') || character == '-',
              "Legacy snapshot HTTP header name is invalid.");
      if (character >= 'A' && character <= 'Z') character = static_cast<char>(character + ('a' - 'A'));
    }
    Require(headers.emplace(name, response.substr(colon + 2, end - colon - 2)).second,
            "Legacy snapshot HTTP header is duplicated.");
    at = end + 2;
  }
  Require(headers.count("content-length") && headers.count("content-type") && headers.count("connection") &&
              !headers.count("transfer-encoding") && !headers.count("content-encoding") &&
              headers.at("content-type") == "application/json; charset=utf-8" && headers.at("connection") == "close",
          "Legacy snapshot HTTP framing differs from the original server.");
  const auto length = CanonicalUnsigned(headers.at("content-length"));
  Require(length > 0 && length <= 1024 * 1024 && response.size() - split - 4 == length,
          "Legacy snapshot HTTP length differs or exceeds its bound.");
  return response.substr(split + 4);
}

class LegacySnapshotProbe {
 public:
  LegacySnapshotProbe() = default;
  LegacySnapshotProbe(const LegacySnapshotProbe&) = delete;
  LegacySnapshotProbe& operator=(const LegacySnapshotProbe&) = delete;
  ~LegacySnapshotProbe() { Close(); }
  bool started() const { return started_; }
  bool pending() const { return started_ && !ready_; }
  bool ready() const { return ready_; }
  void Start(const std::string& token, DWORD game_pid) {
    Require(!started_ && HashText(token) && game_pid != 0, "Legacy snapshot observation cannot be replayed.");
    started_ = true; game_pid_ = game_pid; started_utc_ = LegacyCurrentUtcTicks();
    deadline_ = GetTickCount64() + 30000;
    request_ = "GET /snapshot HTTP/1.1\r\nHost: 127.0.0.1:32755\r\nConnection: close\r\n"
        "X-Mystia-Steward-Companion-Token: " + token + "\r\n\r\n";
  }
  void Poll() {
    if (!pending()) return;
    const auto now = GetTickCount64();
    Available(now < deadline_, "No post-focus cached publication was observed; content deduplication means Update readiness remains unknown.");
    if (socket_ == INVALID_SOCKET) {
      if (now < next_request_) return;
      Require(request_count_ < 300 && ListenerMatched(), "Legacy snapshot listener is not uniquely owned by the retained game.");
      socket_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
      Require(socket_ != INVALID_SOCKET, "Cannot create the legacy snapshot observation socket.");
      u_long nonblocking = 1;
      Require(ioctlsocket(socket_, FIONBIO, &nonblocking) == 0, "Cannot bound the legacy snapshot socket.");
      sockaddr_in endpoint{}; endpoint.sin_family = AF_INET; endpoint.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
      endpoint.sin_port = htons(32755);
      const auto result = connect(socket_, reinterpret_cast<sockaddr*>(&endpoint), sizeof(endpoint));
      Require(result == 0 || WSAGetLastError() == WSAEWOULDBLOCK, "Cannot connect to the exact legacy snapshot endpoint.");
      request_deadline_ = now + 3000; sent_ = 0; connected_ = false; response_.clear(); ++request_count_;
    }
    Require(now < request_deadline_, "Legacy snapshot request exceeded three seconds.");
    if (!connected_) {
      fd_set writable{}, failed{}; FD_ZERO(&writable); FD_ZERO(&failed);
      FD_SET(socket_, &writable); FD_SET(socket_, &failed); timeval timeout{};
      const auto result = select(0, nullptr, &writable, &failed, &timeout);
      Require(result != SOCKET_ERROR && !FD_ISSET(socket_, &failed), "Legacy snapshot connection failed.");
      if (!FD_ISSET(socket_, &writable)) return;
      if (!ConnectionMatched()) return;
      connected_ = true;
    }
    if (sent_ < request_.size()) {
      Require(ListenerMatched() && ConnectionMatched(), "Legacy snapshot peer changed before credential transmission.");
      const int count = send(socket_, request_.data() + sent_, static_cast<int>(request_.size() - sent_), 0);
      if (count == SOCKET_ERROR) { Require(WSAGetLastError() == WSAEWOULDBLOCK, "Legacy snapshot request send failed."); return; }
      Require(count > 0, "Legacy snapshot request send made no progress.");
      sent_ += static_cast<size_t>(count);
      if (sent_ != request_.size()) return;
    }
    std::array<char, 65536> bytes{};
    const auto count = recv(socket_, bytes.data(), static_cast<int>(bytes.size()), 0);
    if (count == SOCKET_ERROR) { Require(WSAGetLastError() == WSAEWOULDBLOCK, "Legacy snapshot receive failed."); return; }
    if (count > 0) {
      Require(response_.size() + static_cast<size_t>(count) <= 1024 * 1024 + 8196, "Legacy snapshot response exceeds its bound.");
      response_.append(bytes.data(), static_cast<size_t>(count)); return;
    }
    Require(ListenerMatched(), "Legacy snapshot listener changed before its EOF was consumed.");
    const auto body = LegacySnapshotBody(response_);
    const auto value = ReportReader(body).Read();
    ReportField(value, "pluginVersion", 's', "1.3.1");
    const auto captured = ReportText(value, "capturedAtUtc");
    const auto ticks = LegacyUtcTicks(captured), observed = LegacyCurrentUtcTicks();
    Require(ticks <= observed && observed >= started_utc_, "Legacy snapshot clock moved backwards or is from the future.");
    Require(!last_ticks_ || ticks >= last_ticks_, "Legacy snapshot publication regressed.");
    ++response_count_; last_ticks_ = ticks; last_captured_ = captured;
    Close(); response_.clear(); next_request_ = now + 100;
    if (ticks > started_utc_) {
      ready_ = true; completed_ticks_ = now;
      std::fill(request_.begin(), request_.end(), '\0'); request_.clear();
    }
  }
  std::string Json() const {
    std::ostringstream out;
    out << "{\"started\":" << (started_ ? "true" : "false") << ",\"ready\":" << (ready_ ? "true" : "false")
        << ",\"kind\":\"original-mod-cached-snapshot-publication\",\"gamePid\":" << game_pid_
        << ",\"startedUtcFileTime\":" << Quote(std::to_string(started_utc_))
        << ",\"requestCount\":" << request_count_ << ",\"responseCount\":" << response_count_
        << ",\"capturedUtcFileTime\":" << Quote(std::to_string(last_ticks_))
        << ",\"capturedAtUtc\":" << (last_captured_.empty() ? "null" : Quote(last_captured_))
        << ",\"completedMonotonicMs\":" << completed_ticks_
        << ",\"businessReadinessClaimed\":false,\"foregroundGrantClaimed\":false}";
    return out.str();
  }
 private:
  static std::vector<MIB_TCPROW_OWNER_PID> Rows() {
    DWORD size = 0;
    Require(GetExtendedTcpTable(nullptr, &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) == ERROR_INSUFFICIENT_BUFFER,
            "Cannot size legacy snapshot TCP identities.");
    for (int attempt = 0; attempt < 3; ++attempt) {
      Require(size >= offsetof(MIB_TCPTABLE_OWNER_PID, table) && size <= 1024 * 1024, "Legacy snapshot TCP table exceeds its bound.");
      std::vector<DWORD> memory((size + sizeof(DWORD) - 1) / sizeof(DWORD));
      const auto result = GetExtendedTcpTable(memory.data(), &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
      if (result == ERROR_INSUFFICIENT_BUFFER) continue;
      Require(result == NO_ERROR, "Cannot read legacy snapshot TCP identities.");
      const auto* table = reinterpret_cast<const MIB_TCPTABLE_OWNER_PID*>(memory.data());
      Require(table->dwNumEntries <= (memory.size() * sizeof(DWORD) - offsetof(MIB_TCPTABLE_OWNER_PID, table)) / sizeof(MIB_TCPROW_OWNER_PID),
              "Legacy snapshot TCP table is malformed.");
      return {table->table, table->table + table->dwNumEntries};
    }
    throw ProbeFailure("Legacy snapshot TCP identities did not stabilize.");
  }
  bool ListenerMatched() const {
    unsigned count = 0;
    for (const auto& row : Rows()) {
      if (row.dwState != MIB_TCP_STATE_LISTEN || ntohs(static_cast<u_short>(row.dwLocalPort)) != 32755) continue;
      Require(row.dwLocalAddr == htonl(INADDR_LOOPBACK) && row.dwOwningPid == game_pid_, "Unrelated process owns the legacy snapshot listener.");
      ++count;
    }
    return count == 1;
  }
  bool ConnectionMatched() const {
    sockaddr_in local{}; int size = static_cast<int>(sizeof(local));
    Require(getsockname(socket_, reinterpret_cast<sockaddr*>(&local), &size) == 0 && size == static_cast<int>(sizeof(local)) &&
                local.sin_family == AF_INET && local.sin_addr.s_addr == htonl(INADDR_LOOPBACK) && local.sin_port != 0,
            "Legacy snapshot local endpoint is invalid.");
    unsigned server_count = 0, client_count = 0;
    for (const auto& row : Rows()) {
      if (row.dwLocalAddr != htonl(INADDR_LOOPBACK) || row.dwRemoteAddr != htonl(INADDR_LOOPBACK) || row.dwState != MIB_TCP_STATE_ESTAB) continue;
      if (ntohs(static_cast<u_short>(row.dwLocalPort)) == 32755 && static_cast<u_short>(row.dwRemotePort) == local.sin_port) {
        Require(row.dwOwningPid == game_pid_, "Legacy snapshot accepted peer is not the retained game."); ++server_count;
      }
      if (static_cast<u_short>(row.dwLocalPort) == local.sin_port && ntohs(static_cast<u_short>(row.dwRemotePort)) == 32755) {
        Require(row.dwOwningPid == GetCurrentProcessId(), "Legacy snapshot client endpoint owner differs."); ++client_count;
      }
    }
    Require(server_count <= 1 && client_count <= 1, "Legacy snapshot connection ownership is ambiguous.");
    return server_count == 1 && client_count == 1;
  }
  void Close() { if (socket_ != INVALID_SOCKET) { closesocket(socket_); socket_ = INVALID_SOCKET; } }
  SOCKET socket_ = INVALID_SOCKET;
  bool started_ = false, ready_ = false, connected_ = false;
  DWORD game_pid_ = 0;
  uint64_t started_utc_ = 0, last_ticks_ = 0, deadline_ = 0, request_deadline_ = 0, next_request_ = 0, completed_ticks_ = 0;
  unsigned request_count_ = 0, response_count_ = 0;
  size_t sent_ = 0;
  std::string request_, response_, last_captured_;
};
