# 发布流程

更新日期：2026-08-19

本文是稳定版和预览版发布的唯一操作手册，负责 GitHub 配置、审批、资产事务和失败处理。本地工具安装与常规构建见[本地开发与构建](local-development.md)，Android 环境与签名见[Android 开发](android-development.md)，发布前测试选择见[验证指南](validation-guide.md)。

## 发布边界

- 日常开发和本地构建在 `dev` 完成。
- 正式稳定版只允许从 `main` 手动触发 `.github/workflows/release.yml`。
- workflow 不改版本、不提交、不合并，也不响应 push、tag、pull request 或 schedule。
- 正式 tag 由发布事务在所有构建和两道审批完成后创建；不要预先创建或推送 tag。
- 发布是 create-only：同名 tag 或 Release 已存在时停止，不覆盖、不续传、不自动删除。
- 预览版可从已推送的 `dev` 提交在本机发布，必须使用 canonical `X.Y.Z-preview.N`。

## 一次性 GitHub 配置

### 私有构建资产与 GitHub App

正式 Mod 引用存放在私有 `blockshy/mystia-steward-build-assets` 的 immutable Release 中，具体 bundle 身份以 `mods/bepinex/References/references.lock.json` 为准。

专用 GitHub App 只安装到私有构建资产仓库和主仓库，Repository permissions 仅允许：

- `Contents: Read-only`：下载锁定的 References bundle；
- `Administration: Read-only`：发布前读取主仓库 Immutable Releases 设置。

workflow 每次把 token 继续限制到单仓库、单权限。创建 Release 使用 publish job 自带、仅当前仓库有效的 `GITHUB_TOKEN`，不使用 PAT，也不给 App 发布写权限。

### 两道 Environment 审批

主仓库需要两个只允许 `main` 的 Environment：

| Environment | 审批时机 |
| --- | --- |
| `official-release-build` | 无密钥验证完成后，解锁私有 References 和 Android 签名材料前 |
| `official-release` | 7 项资产、SHA-256 和 provenance 完成后，任何 tag/Release 写入前 |

两者都必须配置 required reviewers，并关闭管理员绕过。只有一个维护者时可以允许发起者自审；启用 Prevent self-review 前必须先有第二位可信审批者。workflow 会读取并核对 Environment、reviewer、main-only 和 admin-bypass 状态，配置漂移时直接停止。

主仓库还必须启用 **Immutable Releases**。建议同时启用 **Require actions to be pinned to a full-length commit SHA**；workflow 本身及静态审计已经固定 action SHA。

### Environment Secrets

`official-release-build`：

| Secret | 内容 |
| --- | --- |
| `BUILD_ASSETS_APP_ID` | GitHub App Client ID；沿用历史 Secret 名称，但值不是 numeric App ID |
| `BUILD_ASSETS_APP_PRIVATE_KEY` | GitHub App PEM private key |
| `MYSTIA_ANDROID_KEYSTORE_BASE64` | keystore 原始字节的 canonical 单行 Base64 |
| `MYSTIA_ANDROID_KEYSTORE_SHA256` | keystore 文件字节的 64 位小写 SHA-256 |
| `MYSTIA_ANDROID_KEY_ALIAS` | key alias |
| `MYSTIA_ANDROID_STORE_PASSWORD` | keystore 密码 |
| `MYSTIA_ANDROID_KEY_PASSWORD` | key 密码；与 store 密码相同也要显式填写 |

`official-release` 另需同名 `BUILD_ASSETS_APP_ID` 和 `BUILD_ASSETS_APP_PRIVATE_KEY`，只用于读取主仓库 Immutable Releases 设置。Environment Secret 不跨环境共享。

签名材料只在 runner 临时目录和被忽略的 `keystore.properties` 中短暂物化，随后由 `always()` 清理；不得进入日志、cache、artifact、Release 或诊断包。keystore 文件 hash 与最终 APK 签名证书指纹是两项独立门禁。

## 稳定版发布

### 1. 准备版本提交

在 `dev` 把版本同步到 canonical `X.Y.Z` 的五处：

- `package.json`
- `apps/companion/src-tauri/tauri.conf.json`
- `apps/companion/src-tauri/Cargo.toml`
- `apps/companion/src-tauri/Cargo.lock`
- `MystiaStewardCompanionPlugin.PluginVersion`

运行[验证指南](validation-guide.md)中的发布前验证，提交并推送 `dev`。随后只用 fast-forward 把 `dev` 合并到 `main` 并推送；不能 fast-forward 时先处理分支历史，不创建 merge commit 掩盖差异。

Release Note 只写从上一个正式 tag 到当前版本的用户可见新增、体验优化、修复与稳定性，不写提交整理、内部重构、框架升级、构建基础设施或开发文档。

### 2. 手动触发

进入 GitHub `Actions -> Official stable release -> Run workflow`，选择 `main`，填写：

- `tag`：`vX.Y.Z`
- `title`：通常与 tag 相同
- `notes`：面向普通用户的 Markdown Release Note

流程顺序：

1. 无 secret 的 validate job 锁定当前 `origin/main` SHA、五处版本、Release Note、tag/Release absence，并运行前端和发布策略验证。
2. 等待 `official-release-build` 审批。
3. 单个 `windows-2022` job 顺序构建 Mod ZIP、Windows EXE 和两个已签名 APK；桌面产物固化后先清理可再生缓存，再安装 Android 工具链。
4. 构建 job 输出四个二进制 SHA-256；Linux 汇总 job 下载并复核，再生成 manifest、catalog、checksum 和 7 项 provenance。
5. 等待 `official-release` 审批。
6. publish job 重新确认 main、历史、配置和资产，执行一次 create-only 远端事务。

中间 artifact 保留 40 天，覆盖 workflow 最长 35 天（包含审批等待）的生命周期。只重跑失败 job 会破坏 run-attempt 绑定；失败时重新 dispatch，而不是使用 `Re-run failed jobs`。

### 3. 远端事务

发布脚本按固定顺序执行：

1. 原子创建直接指向已验证 commit SHA 的轻量 tag；
2. 创建无资产 Draft，并以 POST 响应锁定 positive numeric Release ID 与精确 `uploads.github.com` URL；
3. 通过该 URL 串行上传 7 项资产；
4. 通过 `/releases/{id}` 核对名称、状态、MIME、精确大小和 `sha256:` digest；
5. 再次验证发布历史、main、tag、immutable policy 和准备好的 metadata；
6. PATCH 同一 numeric Draft ID 公开为 Latest；
7. 以 exact tag ref、numeric ID、Latest 和 immutable 状态完成终检。

远端 mutation 每项只执行一次、至少间隔 1 秒且绝不自动重试。只有 exact-ref、numeric-id 和 Latest 的明确短暂一致性状态允许有界只读等待；401/403、畸形对象、错误 identity、错误 MIME/digest 或历史漂移立即停止。分页 Release 列表只证明发布前不存在同名对象和历史未变，不用于重新发现刚创建的 Draft。

## 正式资产

Release 必须恰好包含：

| 资产 | Canonical `Content-Type` |
| --- | --- |
| `mystia-steward-companion-bepinex.zip` | `application/zip` |
| `mystia-steward-companion-companion-windows-x64.exe` | `application/x-msdownload` |
| `mystia-steward-companion-android-arm64-v8a.apk` | `application/vnd.android.package-archive` |
| `mystia-steward-companion-android-armeabi-v7a.apk` | `application/vnd.android.package-archive` |
| `update-manifest.json` | `application/json` |
| `update-catalog.json` | `application/json` |
| `SHA256SUMS.txt` | `text/plain; charset=utf-8` |

资产名和 MIME 是大小写敏感的单一映射；上传请求、mutation 响应和最终 Release 共用该映射，未知名称在任何远端写入前拒绝。

`SHA256SUMS.txt` 覆盖其余 6 项。`update-manifest.json` 只以 Mod ZIP 为安装权威；独立 Windows EXE 和 APK 不参与 Mod 自动更新。`update-catalog.json` 提供累计版本说明，其读取失败不能改变 manifest 的下载与安装权威。更新协议详见[更新系统](update-system.md)。

## 本地预览版发布

预览版本和五处版本字段都使用 `X.Y.Z-preview.N`。提交并推送 `dev` 后，准备 UTF-8、无 BOM 的 Release Note 文件；不要手工创建 tag。

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

`-SkipBuild` 只用于 CI 或明确故障诊断；调用者必须已经用完全相同的 tag、标题、Note 和 SHA 准备当前 `dist`，脚本仍会完整复核，不接受旧构建残留。

测试预览更新链的客户端需要开启：

```ini
[Updates]
IncludePrerelease = true
```

## 失败处理与终检

任何 mutation 报错都可能已经改变远端。脚本不会删除或覆盖对象，也不会按 tag 续传。立即停止并核对：

- exact tag ref 是否存在及其 commit SHA；
- 是否存在 Draft，以及 mutation 返回的 numeric Release ID；
- Draft 的 body、资产数量、状态、MIME、大小和 digest；
- 当前 workflow artifact 和 provenance 是否仍可用。

需要删除 tag、Draft、Release、Actions run 或 attestation 时，必须单独确认具体对象和不可恢复影响。清理后修复根因、重新形成版本边界，并从全新 dispatch 开始。

正式成功后至少核对：

- `dev`、`main`、tag 和 workflow head 指向预期提交；
- Release 不是 Draft/Prerelease，实际 immutable 且为 Latest；
- 7 项资产名称、MIME、大小和 digest 与 checksum 完全一致；
- provenance 覆盖 7 项资产；
- Windows Mod 自动更新、独立 EXE 与两种 ABI APK 都来自本次 run；
- updater 仍通过 Windows 10 1703+ 的多 DPI 与安装/取消实机验收，Android 仍通过 LAN、Token、前后台和 Wi-Fi 重连真机验收。
