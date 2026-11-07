#pragma once

#include <filesystem>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <stdexcept>

namespace mystia_storage {

/// 使用稳定错误类型区分跨用户/跨电脑解密失败；调用方只显示恢复说明，不以删除密文修复错误。
class CredentialUnavailable final : public std::runtime_error { public: using std::runtime_error::runtime_error; };
std::string CryptCredential(const std::string& value, const std::string& key, bool protect);
/// 导入时复用现有 DPAPI/窗口格式约束；JSON 语义由 Dart 预检，并由原生事务核对同一原文快照。
void ValidateImportedPayload(const std::string& name, const std::string& bytes);

/// 只包含可展示的固定状态；目录位置只供设置页展示，调用者不得把它写入普通日志。
struct StorageStatus {
  bool portable = false;
  std::filesystem::path root;
  bool needs_initialization = false;
  bool can_import_legacy = false;
  std::optional<std::string> import_unavailable_reason;
};

/// 进程级 Windows 存储边界，不依赖 Flutter。Open 必须早于控制端口激活、窗口恢复及任何存储写入。
/// 生产调用方提供 GetModuleFileNameW 得到的实际 EXE；参数化路径仅用于不接触真实 AppData 的原生测试。
class Storage final {
 public:
  using ValidateImportedFile = std::function<void(const std::string&, const std::string&)>;
  static std::unique_ptr<Storage> Open(const std::filesystem::path& executable,
      const std::filesystem::path& embedded_root, const std::string& isolation_id = "");
  ~Storage();
  Storage(const Storage&) = delete;
  Storage& operator=(const Storage&) = delete;

  /// 状态查询不创建数据文件；便携新目录必须由用户明确选择初始化方式。
  StorageStatus Status() const;
  bool Ready() const;
  /// 返回已准入的目录；未初始化便携存储不能以读写 API 绕过用户选择。
  const std::filesystem::path& Root() const;
  /// 只读固定旧根中的偏好，供 Dart 用现有 JSON/偏好规则预检；不允许调用者指定路径。
  std::optional<std::string> LegacyPreferences() const;
  /// 初始化只针对空便携目标；导入固定三文件，永不读取旧设备身份。Dart 已验证的偏好原文必须
  /// 与本次锁定源一致，密文和窗口状态另由原生回调校验，全部完成后才发布初始化完成标记。
  /// checkpoint 仅供隔离故障测试；生产调用不传入观察器，抛错会保留 prepared 与已校验源供重试。
  void Initialize(bool import_legacy, const std::optional<std::string>& expected_preferences,
      const ValidateImportedFile& validate, const std::function<void(const std::string&)>& checkpoint = {});

 private:
  struct Impl;
  explicit Storage(std::unique_ptr<Impl> impl);
  std::unique_ptr<Impl> impl_;
};

/// 有界读取实际进程路径；不使用工作目录或命令行首项选择便携根。
std::filesystem::path ExecutablePath();

}  // namespace mystia_storage
