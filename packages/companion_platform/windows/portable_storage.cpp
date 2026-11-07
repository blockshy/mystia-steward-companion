#include "portable_storage.h"
#include <windows.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <wincrypt.h>
#include <algorithm>
#include <array>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>
#include <vector>

namespace mystia_storage {
namespace {
namespace fs = std::filesystem;
constexpr wchar_t kPortableExe[] = L"mystia-steward-companion-portable.exe";
constexpr wchar_t kManifest[] = L"portable-client-v1.manifest";
constexpr char kHeader[] = "mystia-portable-client-v1\n";
constexpr char kInitialized[] = "mystia-client-storage-v1\n";
constexpr size_t kDataLimit = 1024 * 1024;
const std::array<const char*, 3> kImportFiles = {
    "preferences.json", "window-state-v1.txt", "connection-token.dpapi"};
void Require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
std::string Lower(std::string value) {
  for (auto& ch : value) if (ch >= 'A' && ch <= 'Z') ch += 'a' - 'A';
  return value;
}
std::wstring Normalized(fs::path path) {
  auto value = path.lexically_normal().wstring();
  if (value.rfind(L"\\\\?\\UNC\\", 0) == 0) value = L"\\\\" + value.substr(8);
  else if (value.rfind(L"\\\\?\\", 0) == 0) value.erase(0, 4);
  while (value.size() > 3 && (value.back() == L'\\' || value.back() == L'/')) value.pop_back();
  return value;
}
bool SamePath(const fs::path& left, const fs::path& right) {
  return CompareStringOrdinal(Normalized(left).c_str(), -1, Normalized(right).c_str(), -1, TRUE) == CSTR_EQUAL;
}
DWORD Attributes(const fs::path& path) {
  const auto result = GetFileAttributesW(path.c_str());
  if (result == INVALID_FILE_ATTRIBUTES)
    Require(GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND, "storage path inaccessible");
  return result;
}
void OrdinaryAncestors(const fs::path& path) {
  for (auto current = path; !current.empty();) {
    const auto attributes = Attributes(current);
    Require(attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT),
            "linked or missing storage ancestor refused");
    const auto parent = current.parent_path(); if (parent == current) break; current = parent;
  }
}

/// 守卫持有实际文件/目录对象，既检查属性也检查最终路径。目录允许子项读写但禁止父链重命名；
/// 不把单次 lstat 检查误认为能防止之后把 client-data 移到已批准根以外。
class Handle final {
 public:
  Handle(const fs::path& path, bool directory, bool writable = false)
      : path_(path), directory_(directory) {
    OrdinaryAncestors(path);
    handle_ = CreateFileW(path.c_str(), directory ? FILE_READ_ATTRIBUTES : GENERIC_READ,
        directory || writable ? FILE_SHARE_READ | FILE_SHARE_WRITE : FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    Require(handle_ != INVALID_HANDLE_VALUE, "storage object cannot be held");
    try { Validate(); } catch (...) { CloseHandle(handle_); throw; }
  }
  ~Handle() { if (handle_ != INVALID_HANDLE_VALUE) CloseHandle(handle_); }
  Handle(const Handle&) = delete;
  Handle& operator=(const Handle&) = delete;
  void Validate() const {
    BY_HANDLE_FILE_INFORMATION info{};
    Require(GetFileInformationByHandle(handle_, &info) != FALSE, "storage object identity unavailable");
    Require(!(info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)
        && ((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) == directory_
        && (directory_ || info.nNumberOfLinks == 1), "linked storage object refused");
    std::array<wchar_t, 32768> name{};
    const auto count = GetFinalPathNameByHandleW(handle_, name.data(), static_cast<DWORD>(name.size()), 0);
    Require(count > 0 && count < name.size() && SamePath(path_, fs::path(name.data())),
        "storage object moved outside approved root");
  }
  uint64_t Size() const {
    LARGE_INTEGER length{}; Require(GetFileSizeEx(handle_, &length) && length.QuadPart >= 0, "storage size unavailable");
    return static_cast<uint64_t>(length.QuadPart);
  }
  std::string Read(size_t maximum) const {
    const auto size = Size(); Require(size <= maximum, "storage file exceeds limit");
    LARGE_INTEGER zero{}; Require(SetFilePointerEx(handle_, zero, nullptr, FILE_BEGIN), "storage seek failed");
    std::string result(static_cast<size_t>(size), '\0'); DWORD actual = 0;
    Require(ReadFile(handle_, result.data(), static_cast<DWORD>(size), &actual, nullptr) && actual == size,
        "storage read incomplete"); return result;
  }
  std::string Hash() const {
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0, "SHA256 unavailable");
    try {
      Require(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) == 0, "SHA256 state unavailable");
      LARGE_INTEGER zero{}; Require(SetFilePointerEx(handle_, zero, nullptr, FILE_BEGIN), "storage seek failed");
      std::array<unsigned char, 65536> buffer{}; DWORD size = 0;
      do {
        Require(ReadFile(handle_, buffer.data(), static_cast<DWORD>(buffer.size()), &size, nullptr), "payload read failed");
        if (size) Require(BCryptHashData(hash, buffer.data(), size, 0) == 0, "SHA256 update failed");
      } while (size);
      std::array<unsigned char, 32> result{};
      Require(BCryptFinishHash(hash, result.data(), static_cast<ULONG>(result.size()), 0) == 0, "SHA256 finish failed");
      BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
      static constexpr char digits[] = "0123456789abcdef";
      std::string encoded; for (auto byte : result) { encoded += digits[byte >> 4]; encoded += digits[byte & 15]; }
      return encoded;
    } catch (...) { if (hash) BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0); throw; }
  }
 private:
  HANDLE handle_ = INVALID_HANDLE_VALUE;
  fs::path path_;
  bool directory_;
};

struct Record { std::string hash; uint64_t size; std::string path; };
void RelativePath(const std::string& value) {
  Require(!value.empty() && value.size() <= 240 && value.front() != '/', "portable payload path invalid");
  for (const unsigned char ch : value)
    Require(ch >= 32 && ch <= 126 && std::string("\\:\x7f<>\"|?*").find(static_cast<char>(ch)) == std::string::npos,
        "portable payload path contains forbidden characters");
  std::istringstream stream(value); std::string part;
  while (std::getline(stream, part, '/')) {
    Require(!part.empty() && part != "." && part != ".." && part.back() != '.' && part.back() != ' ', "portable payload path is not canonical");
    const auto base = Lower(part.substr(0, part.find('.')));
    Require(base != "con" && base != "prn" && base != "aux" && base != "nul"
        && !(base.size() == 4 && (base.rfind("com", 0) == 0 || base.rfind("lpt", 0) == 0) && base[3] >= '1' && base[3] <= '9'),
        "portable payload path is a reserved device");
  }
  Require(value.back() != '/', "portable payload path is not a file");
}
std::vector<Record> ParseManifest(const std::string& text, const std::string& header, bool package) {
  Require(text.rfind(header, 0) == 0 && text.back() == '\n' && text.find('\r') == std::string::npos
      && text.find('\0') == std::string::npos, "portable manifest format invalid");
  std::vector<Record> records; std::set<std::string> keys; uint64_t total = 0;
  std::istringstream lines(text.substr(header.size())); std::string line, prior;
  while (std::getline(lines, line)) {
    const auto first = line.find('\t'), second = line.find('\t', first == std::string::npos ? 0 : first + 1);
    Require(first == 64 && second != std::string::npos && line.find('\t', second + 1) == std::string::npos,
        "portable manifest record invalid");
    const auto digest = line.substr(0, first), number = line.substr(first + 1, second - first - 1), path = line.substr(second + 1);
    Require(digest.find_first_not_of("0123456789abcdef") == std::string::npos, "portable manifest digest invalid");
    Require(!number.empty() && number.size() <= 10 && number.find_first_not_of("0123456789") == std::string::npos
        && (number == "0" || number[0] != '0'), "portable manifest size invalid");
    const auto size = std::stoull(number); RelativePath(path);
    Require(size <= (package ? 536870912u : kDataLimit) && keys.insert(Lower(path)).second
        && (prior.empty() || prior < path), "portable manifest bounds or ordering invalid");
    if (package) {
      std::istringstream components(Lower(path)); std::string component;
      while (std::getline(components, component, '/'))
        Require(component != "portable-client-v1.manifest" && component != "client-data"
            && component != "mystia-steward-companion.exe" && component != "preferences.json"
            && component != "window-state-v1.txt" && component != "client-id.dpapi"
            && component != "connection-token.dpapi", "portable payload contains a reserved name");
      const auto extension = Lower(fs::path(path).extension().string());
      Require(size > 0 || (extension != ".exe" && extension != ".dll"), "portable executable component is empty");
    }
    total += size; Require(total <= 2147483648ull && records.size() < 8192, "portable manifest aggregate limit exceeded");
    records.push_back({digest, size, path}); prior = path;
  }
  return records;
}
std::optional<std::string> ReadOptional(const fs::path& path, size_t limit = kDataLimit) {
  if (Attributes(path) == INVALID_FILE_ATTRIBUTES) return std::nullopt;
  return Handle(path, false).Read(limit);
}
void WriteNew(const fs::path& path, const std::string& bytes) {
  HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
  Require(file != INVALID_HANDLE_VALUE, "storage destination already exists or is not writable");
  DWORD actual = 0;
  const bool ok = WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &actual, nullptr)
      && actual == bytes.size() && FlushFileBuffers(file);
  CloseHandle(file); Require(ok, "storage durable write failed; original source preserved");
  Require(ReadOptional(path) == std::optional<std::string>(bytes), "storage durable readback differs");
}
void EnsureDirectory(const fs::path& root) {
  if (Attributes(root) == INVALID_FILE_ATTRIBUTES) {
    const auto parent = root.parent_path(); if (parent != root) EnsureDirectory(parent);
    Require(CreateDirectoryW(root.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS, "storage directory create failed");
  }
  OrdinaryAncestors(root);
  Require((Attributes(root) & FILE_ATTRIBUTE_DIRECTORY) != 0, "storage directory expected");
}
void RejectCommonRoot(const fs::path& root) {
  Require(root != root.root_path(), "portable client requires a dedicated directory");
  const KNOWNFOLDERID* folders[] = {&FOLDERID_Profile, &FOLDERID_Desktop, &FOLDERID_Documents, &FOLDERID_Downloads,
      &FOLDERID_LocalAppData, &FOLDERID_RoamingAppData, &FOLDERID_ProgramData, &FOLDERID_Windows,
      &FOLDERID_System, &FOLDERID_ProgramFiles, &FOLDERID_ProgramFilesX86, &FOLDERID_Public,
      &FOLDERID_PublicDesktop, &FOLDERID_PublicDocuments, &FOLDERID_Music, &FOLDERID_Pictures, &FOLDERID_Videos};
  for (const auto id : folders) {
    PWSTR value = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(*id, KF_FLAG_DONT_VERIFY, nullptr, &value))) {
      const bool matches = SamePath(root, fs::path(value)); CoTaskMemFree(value);
      Require(!matches, "portable client cannot use a shared Windows directory");
    }
  }
  std::array<wchar_t, 32768> temporary{};
  const auto length = GetTempPathW(static_cast<DWORD>(temporary.size()), temporary.data());
  Require(length > 0 && length < temporary.size() && !SamePath(root, fs::path(temporary.data())),
      "portable client cannot use the shared temporary root");
}
const std::array<const char*, 12> kRequired = {"mystia-steward-companion-portable.exe", "flutter_windows.dll",
    "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "data/app.so", "data/icudtl.dat",
    "data/flutter_assets/AssetManifest.bin", "data/flutter_assets/FontManifest.json",
    "data/flutter_assets/NOTICES.Z", "THIRD-PARTY-NOTICES.txt", "portable-client-v1.manifest"};
}  // namespace

struct Storage::Impl {
  bool portable = false, ready = true;
  fs::path root, legacy, program_root;
  std::vector<std::unique_ptr<Handle>> held;
  std::set<std::wstring> held_directories;
  void HoldParents(const fs::path& path) {
    for (auto current = path.parent_path(); !current.empty();) {
      if (held_directories.insert(Normalized(current)).second) held.emplace_back(std::make_unique<Handle>(current, true));
      const auto parent = current.parent_path(); if (parent == current) break; current = parent;
    }
  }
  void ValidateHeld() const { for (const auto& item : held) item->Validate(); }
  bool LegacyAvailable() const {
    for (const auto* name : kImportFiles) if (Attributes(legacy / name) != INVALID_FILE_ATTRIBUTES) return true;
    return false;
  }
  void CheckDataTree() const {
    if (Attributes(root) == INVALID_FILE_ATTRIBUTES) return;
    Require((Attributes(root) & FILE_ATTRIBUTE_DIRECTORY) != 0, "portable client-data must be a directory");
    OrdinaryAncestors(root);
    const std::set<std::string> known = {"client-id.dpapi", "connection-token.dpapi", "preferences.json", "window-state-v1.txt",
        "client-id.dpapi.pending", "connection-token.dpapi.pending", "preferences.json.pending", "window-state-v1.txt.pending",
        ".initialized-v1", ".initialization-v1.pending", ".initialization.lock",
        "preferences.json.importing", "window-state-v1.txt.importing", "connection-token.dpapi.importing"};
    for (const auto& entry : fs::directory_iterator(root)) {
      const auto name = entry.path().filename().string();
      Require(known.count(name) != 0 && !(Attributes(entry.path()) & FILE_ATTRIBUTE_DIRECTORY),
          "portable client-data contains unknown entries");
      Handle check(entry.path(), false, true);
      Require(check.Size() <= kDataLimit, "portable client-data file exceeds limit");
    }
  }
};

Storage::Storage(std::unique_ptr<Impl> impl) : impl_(std::move(impl)) {}
Storage::~Storage() = default;

std::unique_ptr<Storage> Storage::Open(const fs::path& executable, const fs::path& embedded_root, const std::string& isolation_id) {
  Require(executable.is_absolute() && embedded_root.is_absolute(), "absolute storage paths required");
  auto value = std::make_unique<Impl>(); value->program_root = executable.parent_path(); value->legacy = embedded_root;
  // 隔离启动的导入来源也必须隔离；只替换目标根却保留真实 AppData 来源会让 UI 测试意外读取私有设置。
  if (!isolation_id.empty()) value->legacy /= fs::path("isolation") / isolation_id;
  const bool named_portable = _wcsicmp(executable.filename().c_str(), kPortableExe) == 0;
  const bool marker_exists = Attributes(value->program_root / kManifest) != INVALID_FILE_ATTRIBUTES;
  Require(named_portable || !marker_exists, "portable marker requires the portable executable");
  value->portable = named_portable;
  if (named_portable) {
    RejectCommonRoot(value->program_root);
    Require(marker_exists, "portable manifest missing; extract the complete dedicated client directory");
    value->HoldParents(executable);
    auto manifest = std::make_unique<Handle>(value->program_root / kManifest, false);
    const auto records = ParseManifest(manifest->Read(4194304), kHeader, true);
    value->held.emplace_back(std::move(manifest));
    std::map<std::string, Record> expected;
    std::set<std::string> directories;
    std::map<std::string, std::string> canonical_components;
    for (const auto& record : records) {
      expected.emplace(record.path, record);
      auto component = fs::path(record.path);
      while (!component.empty()) {
        const auto name = component.generic_string();
        const auto inserted = canonical_components.emplace(Lower(name), name);
        Require(inserted.second || inserted.first->second == name, "portable path casing is inconsistent");
        component = component.parent_path();
      }
      auto parent = fs::path(record.path).parent_path();
      while (!parent.empty()) { directories.insert(parent.generic_string()); parent = parent.parent_path(); }
    }
    for (const auto* required : kRequired) {
      if (std::string(required) == "portable-client-v1.manifest") continue;
      const auto found = expected.find(required);
      Require(found != expected.end() && found->second.size > 0, "portable package component missing or empty");
    }
    for (const auto& directory : directories)
      Require(expected.count(directory) == 0, "portable payload path is both a file and a directory");
    // 手动递归前先核对属性；禁止 iterator 跟随重解析点后才补查。所有载荷与派生目录必须精确匹配。
    std::function<void(const fs::path&)> visit = [&](const fs::path& directory) {
      for (const auto& entry : fs::directory_iterator(directory)) {
        const auto path = entry.path(); const auto relative = path.lexically_relative(value->program_root).generic_string();
        if (relative == "client-data") { Require((Attributes(path) & FILE_ATTRIBUTE_DIRECTORY) != 0, "portable client-data must be a directory"); continue; }
        if (relative == "portable-client-v1.manifest") continue;
        const auto attributes = Attributes(path);
        Require(!(attributes & FILE_ATTRIBUTE_REPARSE_POINT), "portable package links refused");
        if (attributes & FILE_ATTRIBUTE_DIRECTORY) {
          Require(directories.count(relative) != 0, "portable package contains an unknown directory");
          value->held.emplace_back(std::make_unique<Handle>(path, true)); visit(path);
        } else {
          const auto found = expected.find(relative); Require(found != expected.end(), "portable package contains an unknown file");
          auto file = std::make_unique<Handle>(path, false);
          Require(file->Size() == found->second.size && file->Hash() == found->second.hash, "portable payload integrity mismatch");
          const auto extension = Lower(path.extension().string());
          Require((extension != ".dll" && extension != ".exe") || file->Size() > 0, "portable executable component is empty");
          value->held.emplace_back(std::move(file)); expected.erase(found);
        }
      }
    };
    visit(value->program_root); Require(expected.empty(), "portable payload is missing");
    value->root = value->program_root / L"client-data"; value->CheckDataTree();
    const auto initialized = ReadOptional(value->root / L".initialized-v1");
    Require(!initialized || *initialized == kInitialized, "portable storage initialization record invalid");
    value->ready = initialized.has_value();
    if (!value->ready && Attributes(value->root) != INVALID_FILE_ATTRIBUTES) {
      Require(Attributes(value->root / L"client-id.dpapi") == INVALID_FILE_ATTRIBUTES,
          "uninitialized portable storage already contains an identity");
    }
    // 探测仅在整个专属树准入之后执行；无权写入时明确退出，不创建 AppData 替代目录。
    for (const auto& directory : {value->program_root, value->root}) {
      if (Attributes(directory) == INVALID_FILE_ATTRIBUTES) continue;
      auto probe = directory / (L".portable-write-probe-" + std::to_wstring(GetCurrentProcessId()));
      HANDLE file = CreateFileW(probe.c_str(), GENERIC_WRITE | DELETE, 0, nullptr, CREATE_NEW,
          FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
      Require(file != INVALID_HANDLE_VALUE, "portable directory is not writable"); CloseHandle(file);
    }
    if (Attributes(value->root) != INVALID_FILE_ATTRIBUTES) value->HoldParents(value->root / L".initialized-v1");
  } else {
    Require(Attributes(value->program_root / L"client-data") == INVALID_FILE_ATTRIBUTES,
        "portable data requires the portable executable and manifest");
    value->root = embedded_root;
    if (!isolation_id.empty()) value->root /= fs::path("isolation") / isolation_id;
    EnsureDirectory(value->root); value->HoldParents(value->root / L"preferences.json");
  }
  value->ValidateHeld();
  return std::unique_ptr<Storage>(new Storage(std::move(value)));
}

bool Storage::Ready() const { return impl_->ready; }
const fs::path& Storage::Root() const {
  Require(impl_->ready, "portable storage requires explicit initialization");
  impl_->ValidateHeld(); return impl_->root;
}
StorageStatus Storage::Status() const {
  impl_->ValidateHeld();
  StorageStatus result; result.portable = impl_->portable; result.root = impl_->root;
  result.needs_initialization = impl_->portable && !impl_->ready;
  if (result.needs_initialization) {
    try { result.can_import_legacy = impl_->LegacyAvailable(); }
    catch (...) { result.import_unavailable_reason = "旧 Flutter 存储无法读取，未更改原数据。"; }
    if (!result.can_import_legacy && !result.import_unavailable_reason)
      result.import_unavailable_reason = "没有可导入的旧 Flutter 偏好、窗口状态或连接凭据。";
  }
  return result;
}

std::optional<std::string> Storage::LegacyPreferences() const {
  Require(impl_->portable && !impl_->ready, "legacy preferences are available only before portable initialization");
  impl_->ValidateHeld();
  const auto text = ReadOptional(impl_->legacy / L"preferences.json");
  if (text && !text->empty())
    Require(MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text->data(), static_cast<int>(text->size()), nullptr, 0) > 0,
        "legacy preferences encoding invalid; original preserved");
  return text;
}

void Storage::Initialize(bool import_legacy, const std::optional<std::string>& expected_preferences,
    const ValidateImportedFile& validate, const std::function<void(const std::string&)>& checkpoint) {
  Require(impl_->portable && !impl_->ready, "portable storage is already initialized or unavailable");
  impl_->ValidateHeld(); impl_->CheckDataTree();
  Require(Attributes(impl_->root / L"client-id.dpapi") == INVALID_FILE_ATTRIBUTES
      && Attributes(impl_->root / L".initialized-v1") == INVALID_FILE_ATTRIBUTES,
      "portable destination was initialized by another operation");
  // 仅这一显式动作创建数据目录。数据身份永不读取旧源；新身份由 Dart 在提交成功后生成。
  EnsureDirectory(impl_->root); impl_->HoldParents(impl_->root / L".initialized-v1");
  auto lock_path = impl_->root / L".initialization.lock";
  HANDLE lock = CreateFileW(lock_path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_ALWAYS,
      FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
  Require(lock != INVALID_HANDLE_VALUE, "portable initialization is already in progress");
  try {
    const auto pending_path = impl_->root / L".initialization-v1.pending";
    const std::string header = std::string(kInitialized) + (import_legacy ? "import\n" : "fresh\n");
    auto pending = ReadOptional(pending_path);
    std::map<std::string, std::string> input;
    std::vector<std::unique_ptr<Handle>> source_handles;
    if (import_legacy) {
      Require(impl_->LegacyAvailable(), "no legacy Flutter settings available");
      OrdinaryAncestors(impl_->legacy);
      for (const auto* name : kImportFiles) {
        const auto source = impl_->legacy / name;
        if (Attributes(source) == INVALID_FILE_ATTRIBUTES) continue;
        auto handle = std::make_unique<Handle>(source, false);
        auto bytes = handle->Read(kDataLimit); validate(name, bytes);
        input.emplace(name, std::move(bytes)); source_handles.emplace_back(std::move(handle));
      }
      const auto preference = input.find("preferences.json");
      const auto actual_preferences = preference == input.end() ? std::optional<std::string>{}
          : std::optional<std::string>(preference->second);
      Require(actual_preferences == expected_preferences, "legacy preferences changed after validation; original preserved");
    }
    if (!pending) {
      // 首次意图发布前目标只能包含空目录及锁；不得把现有数据默认为可覆盖的导入目标。
      for (const auto& entry : fs::directory_iterator(impl_->root))
        Require(entry.path().filename() == L".initialization.lock", "portable initialization target is not empty");
      std::string prepared = header;
      for (const auto& item : input) {
        const auto source = impl_->legacy / item.first;
        Handle file(source, false);
        prepared += file.Hash() + "\t" + std::to_string(item.second.size()) + "\t" + item.first + "\n";
      }
      WriteNew(pending_path, prepared); pending = prepared;
    }
    if (checkpoint) checkpoint("after-intent");
    const auto records = ParseManifest(*pending, header, false);
    Require(records.size() == input.size(), "portable initialization source set changed");
    std::set<std::string> allowed{".initialization.lock", ".initialization-v1.pending"};
    for (const auto& record : records) { allowed.insert(record.path); allowed.insert(record.path + ".importing"); }
    for (const auto& entry : fs::directory_iterator(impl_->root))
      Require(allowed.count(entry.path().filename().string()) != 0, "portable initialization contains unrelated data");
    std::vector<std::unique_ptr<Handle>> committed;
    for (const auto& record : records) {
      Require(std::find(kImportFiles.begin(), kImportFiles.end(), record.path) != kImportFiles.end(),
          "portable initialization record names an unsupported source");
      const auto found = input.find(record.path); Require(found != input.end(), "portable initialization source missing");
      Handle original(impl_->legacy / record.path, false);
      Require(original.Size() == record.size && original.Hash() == record.hash, "legacy source changed during initialization");
      const auto destination = impl_->root / record.path;
      const auto current = ReadOptional(destination);
      if (current) Require(*current == found->second, "portable destination differs; existing data preserved");
      else {
        auto staged = destination; staged += L".importing";
        const auto existing = ReadOptional(staged);
        if (existing) Require(*existing == found->second, "portable staged data differs; preserved");
        else WriteNew(staged, found->second);
        Require(MoveFileExW(staged.c_str(), destination.c_str(), MOVEFILE_WRITE_THROUGH), "portable imported file publish failed");
      }
      auto final_file = std::make_unique<Handle>(destination, false);
      Require(final_file->Read(kDataLimit) == found->second, "portable imported readback differs");
      committed.emplace_back(std::move(final_file));
      if (checkpoint) checkpoint("after-file:" + record.path);
    }
    if (checkpoint) checkpoint("before-ready");
    WriteNew(impl_->root / L".initialized-v1", kInitialized);
    // 完成标记耐久回读成功后才允许身份生成。中断留下的 pending 原件可保留，不作为重新初始化许可。
    impl_->ready = true; CloseHandle(lock);
  } catch (...) { CloseHandle(lock); throw; }
}

fs::path ExecutablePath() {
  std::array<wchar_t, 32768> path{};
  const auto length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
  Require(length > 0 && length < path.size(), "executable path unavailable");
  return fs::path(path.data());
}

std::string CryptCredential(const std::string& value, const std::string& key, bool protect) {
  // DPAPI 当前用户保护与原存储协议保持一致，键名作为附加熵；不添加机器可迁移明文副本。
  if (value.empty() || value.size() > (protect ? 60000u : 65536u))
    throw CredentialUnavailable("credential size invalid; original data preserved");
  DATA_BLOB source{static_cast<DWORD>(value.size()), reinterpret_cast<BYTE*>(const_cast<char*>(value.data()))};
  DATA_BLOB entropy{static_cast<DWORD>(key.size()), reinterpret_cast<BYTE*>(const_cast<char*>(key.data()))}, output{};
  const BOOL ok = protect ? CryptProtectData(&source, L"Mystia companion", &entropy, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output)
      : CryptUnprotectData(&source, nullptr, &entropy, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output);
  if (!ok) throw CredentialUnavailable("credential protection failed; original data preserved");
  std::string result(reinterpret_cast<char*>(output.pbData), output.cbData);
  SecureZeroMemory(output.pbData, output.cbData); LocalFree(output.pbData); return result;
}

void ValidateImportedPayload(const std::string& name, const std::string& bytes) {
  if (name == "connection-token.dpapi") { CryptCredential(bytes, "connection-token", false); return; }
  if (name == "window-state-v1.txt") {
    int schema = 0, x = 0, y = 0, width = 0, height = 0, maximized = 0, dpi = 0;
    std::istringstream input(bytes); std::string excess;
    Require(static_cast<bool>(input >> schema >> x >> y >> width >> height >> maximized >> dpi) && !(input >> excess)
        && schema == 1 && width >= 100 && width <= 20000 && height >= 100 && height <= 20000 && dpi >= 48 && dpi <= 768,
        "legacy window state invalid; original preserved");
    return;
  }
  Require(name == "preferences.json", "unsupported legacy source");
}
}  // namespace mystia_storage
