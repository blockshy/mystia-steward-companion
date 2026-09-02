# 发布流程

更新日期：2026-09-02

本文是稳定版和预览版发布的唯一操作手册，负责 GitHub 配置、审批、资产事务和失败处理。本地工具安装与常规构建见[本地开发与构建](local-development.md)，Android 环境与签名见[Android 开发](android-development.md)，发布前测试选择见[验证指南](validation-guide.md)。

## 发布边界

- 日常开发和本地构建在 `dev` 完成。
- 正式稳定版只允许从 `main` 手动触发 `.github/workflows/release.yml`。
- 发布工作流不改版本、不提交、不合并，也不响应推送、Git 标签、拉取请求或定时任务。
- 正式 Git 标签由发布事务在所有构建和两道审批完成后创建；不要预先创建或推送标签。
- 发布仅创建新对象：同名标签或 Release 已存在时停止，不覆盖、不续传、不自动删除。
- 预览版可从已推送的 `dev` 提交在本机发布，必须使用规范的 `X.Y.Z-preview.N`。

## 一次性 GitHub 配置

### 私有构建资产与 GitHub App

正式 Mod 引用存放在私有 `blockshy/mystia-steward-build-assets` 的不可变 Release 中，具体引用包标识以 `mods/bepinex/References/references.lock.json` 为准。

专用 GitHub App 只安装到私有构建资产仓库和主仓库，仓库权限仅允许：

- `Contents: Read-only`：下载锁定的引用包；
- `Administration: Read-only`：发布前读取主仓库 Immutable Releases 设置。

工作流每次把令牌继续限制到单仓库、单权限。创建 Release 使用发布任务自带、仅当前仓库有效的 `GITHUB_TOKEN`，不使用 PAT，也不给 App 发布写权限。

### 两道 Environment 审批

主仓库需要两个只允许 `main` 的 Environment：

| Environment | 审批时机 |
| --- | --- |
| `official-release-build` | 无密钥验证完成后，解锁私有 References 和 Android 签名材料前 |
| `official-release` | 7 项产物、SHA-256 和来源证明完成后，任何 Git 标签或 Release 写入前 |

两者都必须配置必需审批者，并关闭管理员绕过。只有一个维护者时可以允许发起者自审；启用“禁止自审”前必须先有第二位可信审批者。工作流会读取并核对 Environment、审批者、仅限 main 和管理员绕过状态，配置不一致时直接停止。

主仓库还必须启用 **Immutable Releases**。建议同时启用 **Require actions to be pinned to a full-length commit SHA**；workflow 本身及静态审计已经固定 action SHA。

### Environment Secrets

`official-release-build`：

| Secret | 内容 |
| --- | --- |
| `BUILD_ASSETS_APP_ID` | GitHub App 客户端 ID；沿用历史 Secret 名称，但值不是数字 App ID |
| `BUILD_ASSETS_APP_PRIVATE_KEY` | GitHub App 的 PEM 私钥 |
| `MYSTIA_ANDROID_KEYSTORE_BASE64` | 密钥库原始字节的规范单行 Base64 |
| `MYSTIA_ANDROID_KEYSTORE_SHA256` | keystore 文件字节的 64 位小写 SHA-256 |
| `MYSTIA_ANDROID_KEY_ALIAS` | 密钥别名 |
| `MYSTIA_ANDROID_STORE_PASSWORD` | 密钥库密码 |
| `MYSTIA_ANDROID_KEY_PASSWORD` | 密钥密码；与密钥库密码相同也要明确填写 |

`official-release` 另需同名 `BUILD_ASSETS_APP_ID` 和 `BUILD_ASSETS_APP_PRIVATE_KEY`，只用于读取主仓库 Immutable Releases 设置。Environment Secret 不跨环境共享。

签名材料只在运行器临时目录和被忽略的 `keystore.properties` 中短暂写入文件，随后由 `always()` 清理；不得进入日志、缓存、构建产物、Release 或诊断包。密钥库文件哈希与最终 APK 签名证书指纹必须分别检查。

## 稳定版发布

### 1. 准备版本提交

在 `dev` 把版本同步为规范的 `X.Y.Z`，涉及五处：

- `package.json`
- `apps/companion/src-tauri/tauri.conf.json`
- `apps/companion/src-tauri/Cargo.toml`
- `apps/companion/src-tauri/Cargo.lock`
- `MystiaStewardCompanionPlugin.PluginVersion`

运行[验证指南](validation-guide.md)中的发布前验证，提交并推送 `dev`。随后只用快进方式把 `dev` 合并到 `main` 并推送；不能快进时先处理分支历史，不创建合并提交掩盖差异。

版本说明只写从上一个正式标签到当前版本的用户可见新增、体验优化、修复与稳定性，不写提交整理、内部重构、框架升级、构建基础设施或开发文档。

### 2. 手动触发

进入 GitHub `Actions -> Official stable release -> Run workflow`，选择 `main`，填写：

- `tag`：`vX.Y.Z`
- `title`：通常与标签相同
- `notes`：面向普通用户的 Markdown 版本说明

流程顺序：

1. 不使用 Secret 的验证任务锁定当前 `origin/main` SHA、五处版本、版本说明，并确认标签与 Release 均不存在，再运行前端和发布策略验证。
2. 等待 `official-release-build` 审批。
3. 单个 `windows-2022` 任务顺序构建 Mod ZIP、Windows EXE 和两个已签名 APK；桌面产物固定后先清理可再生缓存，再安装 Android 工具链。
4. 构建任务输出四个二进制 SHA-256；Linux 汇总任务下载并复核，再生成清单、目录、校验和与 7 项来源证明。
5. 等待 `official-release` 审批。
6. 发布任务重新确认 main、历史、配置和资产，执行一次仅创建新对象的远端事务。

中间产物保留 40 天，覆盖工作流最长 35 天（包含审批等待）的生命周期。只重跑失败任务会破坏运行次数绑定；失败时重新启动整个流程，而不是使用 `Re-run failed jobs`。

### 3. 远端事务

发布脚本按固定顺序执行：

1. 一次性创建直接指向已验证提交 SHA 的轻量标签；
2. 创建无资产草稿 Release，并以 POST 响应锁定正数 Release ID 与精确的 `uploads.github.com` URL；
3. 通过该 URL 串行上传 7 项资产；
4. 通过 `/releases/{id}` 核对名称、状态、MIME、精确大小和 `sha256:` digest；
5. 再次验证发布历史、main、标签、不可变策略和准备好的元数据；
6. PATCH 同一数字草稿 ID，公开并设为 Latest；
7. 以精确标签引用、数字 ID、Latest 和不可变状态完成终检。

每项远端变更只执行一次、至少间隔 1 秒且绝不自动重试。只有精确引用、数字 ID 和 Latest 的明确短暂一致性状态允许按时间上限只读等待；遇到 401/403、畸形对象、错误标识、错误 MIME/摘要或历史不一致时立即停止。分页 Release 列表只证明发布前不存在同名对象且历史未变，不用于重新发现刚创建的草稿。

## 正式资产

Release 必须恰好包含：

| 资产 | 规范 `Content-Type` |
| --- | --- |
| `mystia-steward-companion-bepinex.zip` | `application/zip` |
| `mystia-steward-companion-companion-windows-x64.exe` | `application/x-msdownload` |
| `mystia-steward-companion-android-arm64-v8a.apk` | `application/vnd.android.package-archive` |
| `mystia-steward-companion-android-armeabi-v7a.apk` | `application/vnd.android.package-archive` |
| `update-manifest.json` | `application/json` |
| `update-catalog.json` | `application/json` |
| `SHA256SUMS.txt` | `text/plain; charset=utf-8` |

资产名和 MIME 是大小写敏感的单一映射；上传请求、修改响应和最终 Release 共用该映射，未知名称在任何远端写入前拒绝。

`SHA256SUMS.txt` 覆盖其余 6 项。`update-manifest.json` 只以 Mod ZIP 作为可安装包；独立 Windows EXE 和 APK 不参与 Mod 自动更新。`update-catalog.json` 提供累计版本说明，其读取失败不能改变 manifest 对下载与安装包的判定。更新协议详见[更新系统](update-system.md)。

## 本地预览版发布

预览版本和五处版本字段都使用 `X.Y.Z-preview.N`。提交并推送 `dev` 后，准备 UTF-8、无 BOM 的版本说明文件；不要手工创建标签。

```powershell
$tag = "vX.Y.Z-preview.N"
$target = (git rev-parse HEAD).Trim().ToLowerInvariant()
$notesFile = Join-Path $env:TEMP "mystia-release-notes.md"
[IO.File]::WriteAllText($notesFile, "预览版说明", [Text.UTF8Encoding]::new($false))

pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag $tag `
  -Title $tag `
  -NotesFile $notesFile `
  -TargetCommitSha $target
```

需要同时发布 Android APK 时，在完整的本地 Android 与签名环境中增加 `-BuildAndroidApk`。不加时，预览版不要求 Android 工具链。

`-SkipBuild` 只用于 CI 或明确故障诊断；调用者必须已经用完全相同的标签、标题、版本说明和 SHA 准备当前 `dist`，脚本仍会完整复核，不接受旧构建残留。

测试预览更新链的客户端需要开启：

```ini
[Updates]
IncludePrerelease = true
```

## 失败处理与终检

任何修改请求报错都可能已经改变远端。脚本不会删除或覆盖对象，也不会按标签续传。立即停止并核对：

- 精确标签引用是否存在及其提交 SHA；
- 是否存在草稿，以及修改请求返回的数字 Release ID；
- 草稿的正文、资产数量、状态、MIME、大小和摘要；
- 当前工作流产物和来源证明是否仍可用。

需要删除标签、草稿、Release、Actions 运行记录或证明文件时，必须单独确认具体对象和不可恢复影响。清理后修复根因、重新形成版本边界，并从全新的工作流运行开始。

正式成功后至少核对：

- `dev`、`main`、标签和工作流起始提交指向预期提交；
- Release 不是草稿或预览版，实际不可变且为 Latest；
- 7 项资产名称、MIME、大小和摘要与校验和完全一致；
- 来源证明覆盖 7 项资产；
- Windows Mod 自动更新、独立 EXE 与两种 ABI APK 都来自本次运行；
- updater 仍通过 Windows 10 1703+ 的多 DPI 与安装/取消实机验收，Android 仍通过 LAN、Token、前后台和 Wi-Fi 重连真机验收。
