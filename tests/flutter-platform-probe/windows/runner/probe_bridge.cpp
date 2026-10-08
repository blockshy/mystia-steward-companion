#include "probe_bridge.h"

#include <array>
#include <cassert>
#include <condition_variable>
#include <cstdint>
#include <limits>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <thread>
#include <utility>

namespace {

constexpr std::size_t kMaximumFrameBytes = 16 * 1024;
constexpr DWORD kExchangeTimeoutMs = 3000;
constexpr char kPipePrefix[] =
    "\\\\.\\pipe\\mystia-steward-companion-p0-";

using Reply = std::function<void(mystia_probe::ErrorOr<std::string>)>;

class Handle final {
 public:
  explicit Handle(HANDLE value = nullptr) : value_(value) {}
  ~Handle() {
    if (value_ != nullptr && value_ != INVALID_HANDLE_VALUE) {
      CloseHandle(value_);
    }
  }
  Handle(const Handle&) = delete;
  Handle& operator=(const Handle&) = delete;
  HANDLE get() const { return value_; }

 private:
  HANDLE value_;
};

class Failure final : public std::runtime_error {
 public:
  Failure(std::string code, std::string message)
      : std::runtime_error(std::move(message)), code(std::move(code)) {}
  const std::string code;
};

[[noreturn]] void NativeFailure(const char* operation,
                                DWORD error = GetLastError()) {
  throw Failure("probe.transport", std::string(operation) +
                                       " failed (Win32 " +
                                       std::to_string(error) + ").");
}

struct Configuration {
  std::wstring pipe;
  std::string session;
  DWORD parent_pid;
  bool install_fixture = false;
};

Configuration ParseArguments(const std::vector<std::string>& arguments) {
  std::optional<std::string> pipe;
  std::optional<std::string> session;
  std::optional<std::string> parent;
  std::optional<std::string> mode;
  const auto read = [](const std::string& argument, const std::string& prefix,
                       std::optional<std::string>& target) {
    if (argument.compare(0, prefix.size(), prefix) != 0) return false;
    if (target.has_value()) {
      throw Failure("probe.configuration", "Duplicate updater probe argument.");
    }
    target = argument.substr(prefix.size());
    return true;
  };
  for (const auto& argument : arguments) {
    if (read(argument, "--updater-probe-pipe=", pipe) ||
        read(argument, "--updater-probe-session=", session) ||
        read(argument, "--updater-probe-parent-pid=", parent) ||
        read(argument, "--updater-probe-mode=", mode)) {
      continue;
    }
    if (argument.compare(0, 16, "--updater-probe-") == 0) {
      throw Failure("probe.configuration", "Unknown updater probe argument.");
    }
  }
  if (!pipe || !session || !parent || (mode && *mode != "install-fixture")) {
    throw Failure("probe.configuration",
                  "Launch this probe through its verified updater bootstrap.");
  }
  const std::string prefix(kPipePrefix);
  if (pipe->compare(0, prefix.size(), prefix) != 0 ||
      pipe->size() <= prefix.size() || pipe->size() > 200) {
    throw Failure("probe.configuration", "Invalid local probe pipe name.");
  }
  for (std::size_t index = prefix.size(); index < pipe->size(); ++index) {
    const char value = (*pipe)[index];
    if (!((value >= 'a' && value <= 'z') ||
          (value >= 'A' && value <= 'Z') ||
          (value >= '0' && value <= '9') || value == '-')) {
      throw Failure("probe.configuration", "Invalid local probe pipe suffix.");
    }
  }
  if (session->size() != 32) {
    throw Failure("probe.configuration", "Invalid probe session length.");
  }
  for (const char value : *session) {
    if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))) {
      throw Failure("probe.configuration", "Invalid probe session encoding.");
    }
  }
  if (parent->empty() || parent->front() == '0') {
    throw Failure("probe.configuration", "Invalid bootstrap process ID.");
  }
  std::uint64_t parsed_pid = 0;
  for (const char value : *parent) {
    if (value < '0' || value > '9') {
      throw Failure("probe.configuration", "Invalid bootstrap process ID.");
    }
    parsed_pid = parsed_pid * 10 + static_cast<unsigned>(value - '0');
    if (parsed_pid > std::numeric_limits<DWORD>::max()) {
      throw Failure("probe.configuration", "Bootstrap process ID is too large.");
    }
  }
  if (parsed_pid == GetCurrentProcessId()) {
    throw Failure("probe.configuration", "The UI cannot be its own bootstrap.");
  }
  return {std::wstring(pipe->begin(), pipe->end()), *session,
          static_cast<DWORD>(parsed_pid), mode.has_value()};
}

void RequireActive(HANDLE stop, HANDLE parent) {
  if (WaitForSingleObject(stop, 0) == WAIT_OBJECT_0) {
    throw Failure("probe.closed", "The probe window is closing.");
  }
  const DWORD status = WaitForSingleObject(parent, 0);
  if (status == WAIT_OBJECT_0) {
    throw Failure("probe.parent-exited", "The verified bootstrap has exited.");
  }
  if (status != WAIT_TIMEOUT) NativeFailure("Check bootstrap process");
}

DWORD Transfer(HANDLE pipe, HANDLE parent, HANDLE stop, void* buffer,
               DWORD length, bool writing, ULONGLONG deadline) {
  RequireActive(stop, parent);
  const ULONGLONG now = GetTickCount64();
  if (now >= deadline) {
    throw Failure("probe.timeout", "The probe exchange deadline elapsed.");
  }
  Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  if (!event.get()) NativeFailure("Create pipe I/O event");
  OVERLAPPED operation{};
  operation.hEvent = event.get();
  const BOOL started = writing
                           ? WriteFile(pipe, buffer, length, nullptr, &operation)
                           : ReadFile(pipe, buffer, length, nullptr, &operation);
  if (!started && GetLastError() != ERROR_IO_PENDING) {
    NativeFailure(writing ? "Write probe pipe" : "Read probe pipe");
  }
  if (!started) {
    const HANDLE handles[] = {stop, parent, event.get()};
    const DWORD wait = WaitForMultipleObjects(
        3, handles, FALSE, static_cast<DWORD>(deadline - now));
    if (wait != WAIT_OBJECT_0 + 2) {
      const DWORD wait_error = GetLastError();
      CancelIoEx(pipe, &operation);
      // Cancellation is a request, not completion. Drain it before releasing the
      // OVERLAPPED/event/buffer; window shutdown joins this same worker.
      DWORD ignored = 0;
      GetOverlappedResult(pipe, &operation, &ignored, TRUE);
      if (wait == WAIT_OBJECT_0) {
        throw Failure("probe.closed", "The probe window is closing.");
      }
      if (wait == WAIT_OBJECT_0 + 1) {
        throw Failure("probe.parent-exited", "The verified bootstrap has exited.");
      }
      if (wait == WAIT_TIMEOUT) {
        throw Failure("probe.timeout", "The probe exchange deadline elapsed.");
      }
      NativeFailure("Wait for probe pipe", wait_error);
    }
  }
  DWORD transferred = 0;
  if (!GetOverlappedResult(pipe, &operation, &transferred, FALSE)) {
    NativeFailure(writing ? "Complete pipe write" : "Complete pipe read");
  }
  if (transferred == 0) {
    throw Failure("probe.transport", "The probe pipe closed without a frame.");
  }
  return transferred;
}

std::string ExchangeFrame(HANDLE pipe, HANDLE parent, HANDLE stop,
                          const Configuration& config,
                          const std::string& command, unsigned request_id) {
  const ULONGLONG deadline = GetTickCount64() + kExchangeTimeoutMs;
  // Both interpolated strings have a closed alphabet; no arbitrary JSON input
  // crosses this native API. Schema/state validation of the reply stays in Dart.
  std::string request = "{\"protocolVersion\":" + std::string(config.install_fixture ? "2" : "1") + ",\"session\":\"" +
                        config.session + "\",\"requestId\":" +
                        std::to_string(request_id) + ",\"command\":\"" +
                        command + "\"}\n";
  std::size_t written = 0;
  while (written < request.size()) {
    written += Transfer(pipe, parent, stop, request.data() + written,
                        static_cast<DWORD>(request.size() - written), true,
                        deadline);
  }
  std::string response;
  std::array<char, 4096> buffer{};
  while (true) {
    const DWORD count = Transfer(pipe, parent, stop, buffer.data(),
                                 static_cast<DWORD>(buffer.size()), false,
                                 deadline);
    response.append(buffer.data(), count);
    const auto newline = response.find('\n');
    if (response.size() > kMaximumFrameBytes + 1 ||
        response.find('\r') != std::string::npos ||
        response.find('\0') != std::string::npos) {
      throw Failure("probe.framing", "Invalid or oversized probe response.");
    }
    if (newline == std::string::npos) continue;
    if (newline == 0 || newline != response.size() - 1) {
      throw Failure("probe.framing", "Expected exactly one non-empty LF frame.");
    }
    response.pop_back();
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, response.data(),
                            static_cast<int>(response.size()), nullptr, 0) == 0) {
      throw Failure("probe.framing", "Probe response is not valid UTF-8.");
    }
    return response;
  }
}

}  // namespace

struct ProbeBridge::State {
  struct Job {
    std::string command;
    unsigned request_id;
  };
  struct Completion {
    std::string response;
    std::string error_code;
    std::string error_message;
  };

  State(HWND window, const std::vector<std::string>& arguments)
      : window(window), platform_thread(GetCurrentThreadId()) {
    try {
      if (!IsWindow(window)) {
        throw Failure("probe.configuration", "Probe window is unavailable.");
      }
      config = ParseArguments(arguments);
      stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
      if (!stop) NativeFailure("Create probe shutdown event");
      worker = std::thread([this] { Work(); });
    } catch (const std::exception& error) {
      startup_error = error.what();
    }
  }

  ~State() {
    assert(GetCurrentThreadId() == platform_thread);
    {
      std::lock_guard<std::mutex> guard(mutex);
      stopping = true;
    }
    if (stop) SetEvent(stop);
    wake.notify_all();
    if (worker.joinable()) worker.join();
    if (callback) {
      auto result = std::move(*callback);
      callback.reset();
      result(mystia_probe::FlutterError("probe.closed",
                                       "The probe window closed."));
    }
    if (stop) CloseHandle(stop);
  }

  void Work() {
    try {
      // Keeping this handle prevents a recycled numeric PID from substituting
      // another server after the identity check.
      Handle parent(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION,
                                FALSE, config->parent_pid));
      if (!parent.get()) NativeFailure("Open bootstrap process");
      RequireActive(stop, parent.get());
      // Bootstrap creates the unique pipe before launching the UI. A missing or
      // busy pipe is an identity/setup failure, not a reason to guess/reconnect.
      Handle pipe(CreateFileW(
          config->pipe.c_str(), GENERIC_READ | FILE_WRITE_DATA, 0, nullptr,
          OPEN_EXISTING,
          FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
          nullptr));
      if (pipe.get() == INVALID_HANDLE_VALUE) NativeFailure("Open probe pipe");
      ULONG server_pid = 0;
      if (!GetNamedPipeServerProcessId(pipe.get(), &server_pid)) {
        NativeFailure("Identify probe pipe server");
      }
      RequireActive(stop, parent.get());
      if (server_pid != config->parent_pid) {
        throw Failure("probe.identity", "Pipe server is not the bootstrap.");
      }
      // The bootstrap creates a byte pipe. GENERIC_WRITE would also request
      // FILE_CREATE_PIPE_INSTANCE, intentionally absent from its restricted ACL.
      while (true) {
        std::optional<Job> current;
        {
          std::unique_lock<std::mutex> guard(mutex);
          wake.wait(guard, [this] { return stopping || job.has_value(); });
          if (stopping) return;
          current = std::move(job);
          job.reset();
        }
        const auto response = ExchangeFrame(pipe.get(), parent.get(), stop,
                                             *config, current->command,
                                             current->request_id);
        Complete({response, {}, {}});
        if (current->command == (config->install_fixture ? "finish" : "cancel")) return;
      }
    } catch (const Failure& error) {
      Complete({{}, error.code, error.what()});
    } catch (const std::exception& error) {
      Complete({{}, "probe.transport", error.what()});
    } catch (...) {
      Complete({{}, "probe.transport", "Unexpected native probe failure."});
    }
  }

  void Complete(Completion value) {
    {
      std::lock_guard<std::mutex> guard(mutex);
      if (stopping) return;
      completion = std::move(value);
    }
    // No object pointer is posted: a queued message cannot dereference a bridge
    // that was already destroyed. Destruction settles any still-pending reply.
    PostMessageW(window, ProbeBridge::kCompletionMessage, 0, 0);
  }

  const HWND window;
  const DWORD platform_thread;
  std::optional<Configuration> config;
  std::string startup_error;
  HANDLE stop = nullptr;
  std::thread worker;
  std::mutex mutex;
  std::condition_variable wake;
  bool stopping = false;
  std::optional<Job> job;
  std::optional<Completion> completion;
  // The fields below are only read/written on the platform thread.
  std::optional<Reply> callback;
  unsigned next_request = 1;
  bool failed = false;
  bool install_started = false;
  bool install_finished = false;
};

ProbeBridge::ProbeBridge(HWND window,
                         const std::vector<std::string>& arguments)
    : state_(std::make_unique<State>(window, arguments)) {}

ProbeBridge::~ProbeBridge() = default;

void ProbeBridge::Exchange(const std::string& command, Reply result) {
  assert(GetCurrentThreadId() == state_->platform_thread);
  if (!state_->startup_error.empty()) {
    result(mystia_probe::FlutterError("probe.configuration",
                                     state_->startup_error));
    return;
  }
  if (state_->failed) {
    result(mystia_probe::FlutterError("probe.failed",
                                     "The failed probe cannot be replayed."));
    return;
  }
  if (state_->callback) {
    result(mystia_probe::FlutterError("probe.busy",
                                     "A probe request is already pending."));
    return;
  }
  const bool fixture = state_->config && state_->config->install_fixture;
  const std::string expected = state_->next_request == 1 ? "hello" : "cancel";
  const bool fixture_valid = !state_->install_finished && state_->next_request <= 10000 &&
      (state_->next_request == 1 ? command == "hello" :
       ((command == "start" && !state_->install_started) || command == "status" || command == "cancel" || command == "finish"));
  if (fixture ? !fixture_valid : (state_->next_request > 2 || command != expected)) {
    result(mystia_probe::FlutterError("probe.command",
                                     "Expected one hello followed by one cancel."));
    return;
  }
  if (fixture && command == "start") state_->install_started = true;
  if (fixture && command == "finish") state_->install_finished = true;
  state_->callback = std::move(result);
  {
    std::lock_guard<std::mutex> guard(state_->mutex);
    state_->job = State::Job{command, state_->next_request};
  }
  state_->wake.notify_one();
}

bool ProbeBridge::HandleWindowMessage(UINT message) {
  if (message != kCompletionMessage) return false;
  assert(GetCurrentThreadId() == state_->platform_thread);
  std::optional<State::Completion> completion;
  {
    std::lock_guard<std::mutex> guard(state_->mutex);
    completion = std::move(state_->completion);
    state_->completion.reset();
  }
  if (!completion) return true;
  if (!completion->error_code.empty()) state_->failed = true;
  if (!state_->callback) return true;
  auto result = std::move(*state_->callback);
  state_->callback.reset();
  if (completion->error_code.empty()) {
    ++state_->next_request;
    result(completion->response);
  } else {
    result(mystia_probe::FlutterError(completion->error_code,
                                     completion->error_message));
  }
  return true;
}
