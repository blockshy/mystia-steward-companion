#include "../../packages/companion_platform/windows/portable_storage.h"
#include <windows.h>
#include <shlobj.h>
#include <aclapi.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <set>

namespace fs = std::filesystem;
using mystia_storage::Storage;
namespace {
int checks = 0;
void Check(bool value, const char* reason) { ++checks; if (!value) throw std::runtime_error(reason); }
template<class Action> void Reject(Action action, const char* reason) {
  try { action(); } catch (const std::exception&) { ++checks; return; }
  throw std::runtime_error(reason);
}
std::string Read(const fs::path& path) { std::ifstream source(path, std::ios::binary); return {std::istreambuf_iterator<char>(source), {}}; }
void Write(const fs::path& path, const std::string& value) {
  fs::create_directories(path.parent_path()); std::ofstream target(path, std::ios::binary | std::ios::trunc); target << value;
  if (!target.good()) throw std::runtime_error("synthetic file write failed");
}
std::map<std::string, std::string> Snapshot(const fs::path& root) {
  std::map<std::string, std::string> result;
  if (!fs::exists(root)) return result;
  for (const auto& entry : fs::recursive_directory_iterator(root)) {
    const auto key = entry.path().lexically_relative(root).generic_string();
    result[key] = entry.is_directory() ? "directory" : Read(entry.path());
  }
  return result;
}
void ValidateImport(const std::string& name, const std::string& bytes) {
  mystia_storage::ValidateImportedPayload(name, bytes);
}
}

int wmain(int argc, wchar_t** argv) {
  try {
    Check(argc == 2, "fixture argument required");
    const fs::path root(argv[1]);
    Check(root.is_absolute() && Read(root / ".synthetic-native-storage-test") == "synthetic-only", "synthetic sentinel required");
    const auto embedded = root / "synthetic-appdata" / "flutter-v1";
    int sequence = 0;
    const auto fixture = [&]() {
      const auto area = root / ("case-" + std::to_string(sequence++));
      fs::copy(root / "base", area, fs::copy_options::recursive);
      return area;
    };
    const auto open = [&](const fs::path& area) { return Storage::Open(area / "mystia-steward-companion-portable.exe", embedded); };
    {
      const auto area = fixture(); const auto before = Snapshot(area);
      auto storage = open(area);
      Check(storage->Status().portable && storage->Status().needs_initialization, "portable startup requires choice");
      Check(!fs::exists(area / "client-data") && Snapshot(area) == before && !fs::exists(embedded), "startup guard does not create client-data or AppData");
      Reject([&] { storage->Root(); }, "read must not bypass initialization");
      Reject([&] { fs::rename(area, root / "unexpected-move"); }, "held root must not move");
      storage->Initialize(false, std::nullopt, ValidateImport);
      Check(storage->Ready() && storage->Root() == area / "client-data", "fresh initialization selected portable root");
      Check(!fs::exists(storage->Root() / "client-id.dpapi"), "native initialization never generates an identity");
      Reject([&] { storage->Initialize(false, std::nullopt, ValidateImport); }, "second initialization refused");
      storage.reset(); storage = open(area);
      Check(storage->Ready() && !storage->Status().needs_initialization, "completed initialization reopens");
      storage.reset();
      const auto moved = root / "moved-portable"; fs::rename(area, moved);
      const auto previous_directory = fs::current_path(); fs::current_path(root);
      storage = open(moved); fs::current_path(previous_directory);
      Check(storage->Root() == moved / "client-data" && storage->Ready(), "same-user moved package keeps portable storage independently of cwd");
    }
    for (const auto& damage : {"missing-marker", "bad-marker", "crlf", "missing-file", "changed-file", "unknown-file", "unknown-directory",
        "data-file", "duplicate", "case-prefix", "nested-reserved", "zero-dll", "leaked-preferences"}) {
      const auto area = fixture(); const auto marker = area / "portable-client-v1.manifest";
      const auto text = Read(marker);
      const std::string mode = damage;
      if (mode == "missing-marker") fs::remove(marker);
      if (mode == "bad-marker") Write(marker, "mystia-portable-client-v2\n");
      if (mode == "crlf") Write(marker, text + "\r\n");
      if (mode == "missing-file") fs::remove(area / "flutter_windows.dll");
      if (mode == "changed-file") Write(area / "flutter_windows.dll", "changed bytes");
      if (mode == "unknown-file") Write(area / "unrelated.txt", "keep");
      if (mode == "unknown-directory") fs::create_directory(area / "unrelated-empty");
      if (mode == "data-file") Write(area / "client-data", "not directory");
      if (mode == "duplicate") Write(marker, text + text.substr(text.find('\n') + 1));
      if (mode == "case-prefix") {
        auto changed = text; auto start = changed.find("data/app.so");
        changed.replace(start, 4, "Data"); Write(marker, changed);
      }
      if (mode == "nested-reserved") {
        auto changed = text; auto start = changed.find("data/app.so");
        changed.replace(start, 11, "data/client-data/app.so"); Write(marker, changed);
      }
      if (mode == "zero-dll") {
        auto changed = text; const auto file = changed.find("flutter_windows.dll");
        const auto first_tab = changed.rfind('\t', file - 2);
        changed.replace(first_tab + 1, changed.find('\t', first_tab + 1) - first_tab - 1, "0"); Write(marker, changed);
      }
      if (mode == "leaked-preferences") {
        auto changed = text; const auto start = changed.find("native_assets.json");
        changed.replace(start, 18, "preferences.json"); Write(marker, changed);
      }
      const auto before = Snapshot(area);
      Reject([&] { open(area); }, "damaged package must fail");
      Check(before == Snapshot(area) && !fs::exists(embedded), "guard failure must not write or fall back");
    }
    // 只读探测真实公共根：RejectCommonRoot 在清单读取或目录创建之前拒绝，不向其复制夹具。
    for (const auto* known : {&FOLDERID_Profile, &FOLDERID_Desktop, &FOLDERID_Documents, &FOLDERID_Downloads}) {
      PWSTR value = nullptr; Check(SUCCEEDED(SHGetKnownFolderPath(*known, KF_FLAG_DONT_VERIFY, nullptr, &value)), "known root readable");
      const fs::path common(value); CoTaskMemFree(value);
      Reject([&] { Storage::Open(common / "mystia-steward-companion-portable.exe", embedded); }, "common root must fail without writes");
    }
    {
      const auto area = fixture(); const auto unknown = area / "flutter_windows.dll";
      fs::remove(unknown); Check(CreateHardLinkW(unknown.c_str(), (area / "msvcp140.dll").c_str(), nullptr), "synthetic hard link creation");
      Reject([&] { open(area); }, "payload hard links refused");
      Check(!fs::exists(area / "client-data"), "link refusal has no data side effect");
    }
    {
      const auto area = fixture(); const auto destination = root / "synthetic-linked-data";
      fs::create_directory(destination);
      Check(CreateSymbolicLinkW((area / "client-data").c_str(), destination.c_str(),
          SYMBOLIC_LINK_FLAG_DIRECTORY | SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE), "synthetic directory link creation");
      Reject([&] { open(area); }, "client-data symlink refused");
      Check(fs::is_empty(destination), "linked target remains empty");
    }
    {
      const auto area = fixture(); fs::create_directory(area / "client-data");
      Write(area / "client-data/.initialized-v1", "bad record");
      Reject([&] { open(area); }, "bad storage marker refused");
    }
    {
      // ACL 只改合成目录，并在测试结束前恢复。它验证不可写根不会回退到 AppData。
      const auto area = fixture(); PSECURITY_DESCRIPTOR original = nullptr; PACL old_acl = nullptr, replacement = nullptr;
      Check(GetNamedSecurityInfoW(const_cast<wchar_t*>(area.c_str()), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
          nullptr, nullptr, &old_acl, nullptr, &original) == ERROR_SUCCESS, "read synthetic ACL");
      SID_IDENTIFIER_AUTHORITY authority = SECURITY_WORLD_SID_AUTHORITY; PSID everyone = nullptr;
      Check(AllocateAndInitializeSid(&authority, 1, SECURITY_WORLD_RID, 0, 0, 0, 0, 0, 0, 0, &everyone), "create everyone SID");
      EXPLICIT_ACCESSW deny{}; deny.grfAccessPermissions = FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY;
      deny.grfAccessMode = DENY_ACCESS; deny.grfInheritance = NO_INHERITANCE;
      deny.Trustee.TrusteeForm = TRUSTEE_IS_SID; deny.Trustee.ptstrName = static_cast<LPWSTR>(everyone);
      Check(SetEntriesInAclW(1, &deny, old_acl, &replacement) == ERROR_SUCCESS, "create synthetic deny ACL");
      Check(SetNamedSecurityInfoW(const_cast<wchar_t*>(area.c_str()), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
          nullptr, nullptr, replacement, nullptr) == ERROR_SUCCESS, "set synthetic deny ACL");
      bool refused = false; try { open(area); } catch (const std::exception&) { refused = true; }
      const auto restored = SetNamedSecurityInfoW(const_cast<wchar_t*>(area.c_str()), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
          nullptr, nullptr, old_acl, nullptr);
      LocalFree(replacement); LocalFree(original); FreeSid(everyone);
      Check(restored == ERROR_SUCCESS && refused && !fs::exists(area / "client-data"), "nonwritable portable root rejected");
    }
    const auto cipher = mystia_storage::CryptCredential("synthetic portable token", "connection-token", true);
    Check(mystia_storage::CryptCredential(cipher, "connection-token", false) == "synthetic portable token", "real DPAPI round trip");
    Reject([&] { mystia_storage::CryptCredential(cipher, "different-key", false); }, "DPAPI entropy mismatch refuses");
    Write(embedded / "preferences.json", "{\"endpoint\":\"http://127.0.0.1:32145\"}");
    Write(embedded / "window-state-v1.txt", "1 10 10 1280 720 0 96");
    Write(embedded / "connection-token.dpapi", cipher);
    Write(embedded / "client-id.dpapi", "must-never-be-read");
    const auto original = Snapshot(embedded);
    {
      const auto area = fixture();
      auto storage = Storage::Open(area / "mystia-steward-companion-portable.exe", embedded, "synthetic-portable-isolation");
      Check(!storage->Status().can_import_legacy && !storage->LegacyPreferences(),
          "portable isolation never reads unisolated legacy preferences");
      Check(!fs::exists(embedded / "isolation"), "isolated source query creates no directory");
    }
    HANDLE old_identity = CreateFileW((embedded / "client-id.dpapi").c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    Check(old_identity != INVALID_HANDLE_VALUE, "occupy synthetic identity");
    {
      const auto area = fixture(); auto storage = open(area);
      Check(storage->Status().can_import_legacy, "legacy import available without reading old identity");
      const auto expected = storage->LegacyPreferences();
      storage->Initialize(true, expected, ValidateImport);
      Check(storage->Ready() && !fs::exists(storage->Root() / "client-id.dpapi"), "import leaves new identity creation to Dart");
      for (const auto* name : {"preferences.json", "window-state-v1.txt", "connection-token.dpapi"})
        Check(Read(storage->Root() / name) == Read(embedded / name), "import preserves source bytes");
    }
    CloseHandle(old_identity);
    Check(Snapshot(embedded) == original, "import does not modify any legacy source");
    for (const auto* phase : {"after-intent", "after-file:connection-token.dpapi", "before-ready"}) {
      const auto area = fixture(); auto storage = open(area); auto expected = storage->LegacyPreferences();
      Reject([&] { storage->Initialize(true, expected, ValidateImport, [&](const auto& current) {
        if (current == phase) throw std::runtime_error("synthetic interruption");
      }); }, "initialization fault reached");
      Check(!storage->Ready(), "interruption does not publish ready");
      storage.reset(); storage = open(area); storage->Initialize(true, expected, ValidateImport);
      Check(storage->Ready(), "same source can resume initialization");
    }
    {
      const auto area = fixture(); auto storage = open(area); const auto expected = storage->LegacyPreferences();
      Reject([&] { storage->Initialize(true, expected, ValidateImport, [](const auto& phase) {
        if (phase == "after-intent") throw std::runtime_error("synthetic interruption");
      }); }, "pending source-change fixture prepared");
      Write(embedded / "preferences.json", "{\"changedAfterIntent\":true}");
      Reject([&] { storage->Initialize(true, storage->LegacyPreferences(), ValidateImport); }, "prepared source hash must remain unchanged");
      Check(!storage->Ready() && !fs::exists(area / "client-data/.initialized-v1"), "changed prepared source is preserved without ready");
      Write(embedded / "preferences.json", *expected);
      storage->Initialize(true, expected, ValidateImport);
      Check(storage->Ready(), "restoring exact prepared source permits retry");
    }
    {
      const auto area = fixture(); auto storage = open(area);
      auto expected = storage->LegacyPreferences();
      Write(embedded / "preferences.json", "{\"changed\":true}");
      Reject([&] { storage->Initialize(true, expected, ValidateImport); }, "source changes after Dart validation refused");
      Check(!storage->Ready() && !fs::exists(area / "client-data/.initialized-v1"), "source mismatch does not mark ready");
      storage->Initialize(false, std::nullopt, ValidateImport);
      Check(storage->Ready(), "failed preflight empty directory can initialize fresh");
      Write(embedded / "preferences.json", *expected);
    }
    {
      const auto area = fixture(); auto storage = open(area); auto expected = storage->LegacyPreferences();
      Write(embedded / "connection-token.dpapi", "invalid dpapi");
      Reject([&] { storage->Initialize(true, expected, ValidateImport); }, "unreadable credential refuses import");
      Check(Read(embedded / "connection-token.dpapi") == "invalid dpapi" && !fs::exists(area / "client-data/connection-token.dpapi"),
          "failed DPAPI preserves original and publishes no cipher");
      Write(embedded / "connection-token.dpapi", cipher);
    }
    {
      const auto area = fixture(); auto storage = open(area); auto expected = storage->LegacyPreferences();
      Write(embedded / "window-state-v1.txt", "invalid window state");
      Reject([&] { storage->Initialize(true, expected, ValidateImport); }, "invalid window geometry refuses import");
      Check(!storage->Ready() && !fs::exists(area / "client-data/.initialized-v1"), "invalid geometry never publishes ready");
      Write(embedded / "window-state-v1.txt", "1 10 10 1280 720 0 96");
    }
    {
      const auto ordinary = root / "embedded-program"; fs::create_directory(ordinary);
      auto storage = Storage::Open(ordinary / "mystia-steward-companion.exe", root / "isolated-embedded", "synthetic-isolation");
      Check(!storage->Status().portable && storage->Ready()
          && storage->Root() == root / "isolated-embedded/isolation/synthetic-isolation", "embedded storage behavior retained");
    }
    std::cout << "Native portable storage: " << checks << " checks passed; synthetic paths only.\n";
    return 0;
  } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
