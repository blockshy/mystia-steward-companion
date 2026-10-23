# 发布流程

更新日期：2026-10-23

本文是稳定版和预览版发布的唯一操作手册，负责 GitHub 配置、审批、资产事务和失败处理。本地工具安装与常规构建见[本地开发与构建](local-development.md)，Android 环境与签名见[Android 开发](android-development.md)，发布前测试选择见[验证指南](validation-guide.md)。

## 发布边界

- 发布主线约定为 `dev` → `main`；功能分支可以本地开发和构建，但整理提交历史本身不代表合入这两个分支或取得发布授权。
- 正式稳定版只允许从 `main` 手动触发 `.github/workflows/release.yml`。
- workflow 不改版本、不提交、不合并，也不响应 push、tag、pull request 或 schedule。
- 正式 tag 由发布事务在所有构建和两道审批完成后创建；不要预先创建或推送 tag。
- 发布是 create-only：同名 tag 或 Release 已存在时停止，不覆盖、不续传、不自动删除。
- 预览版可从已推送的 `dev` 提交在本机发布，必须使用 canonical `X.Y.Z-preview.N`。

## 隔离宿主测试依赖

`build-release.ps1` 在构建真实 HTTP 宿主前自动调用 `scripts/restore-bepinex-test-runtime.ps1`。脚本按 `references.lock.json` 中的固定版本下载 BepInEx 原始分发包，验证整包 SHA-256，再按 `tests/flutter-contracts/host/BepInExRuntime.props` 的唯一成员路径、长度及摘要提取 `SemanticVersioning.dll` 到 `temp/flutter-mod-host-runtime`。已有正确文件可复用；已有文件不匹配时保留并停止，不静默覆盖。宿主编译还会再次核验摘要。

该运行库不属于私有编译引用的 7-DLL bundle，也不进入 Mod 成套包。恢复脚本在退出时清理本次下载与提取临时文件；发布 workflow 无论构建成败均清理隔离运行库目录，删除前核对 checkout 内的绝对路径与重解析点。离线环境可以先显式提供同一原始 ZIP，详见[契约测试前置条件](../tests/flutter-contracts/README.md#执行)。这一步只准备测试依赖，不跳过真实 HTTP 验证，也不代替完整发布 workflow 的实际执行证据。

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

在已经准备发布的 `dev` 提交上执行：

```powershell
pwsh -File mods/bepinex/tools/set-version.ps1 -Version X.Y.Z -AndroidBuildNumber <递增整数>
```

脚本同时更新以下五处。产品语义版本使用 canonical `X.Y.Z`，Flutter `pubspec.yaml` 保存 `X.Y.Z+AndroidBuildNumber`，不能把 Android 构建号删掉：

- `package.json`
- `apps/companion-flutter/pubspec.yaml`
- `native/updater/Cargo.toml`
- `native/updater/Cargo.lock`
- `mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs` 中的 `PluginVersion`

构建号必须高于当前 `pubspec` 值，继续使用原 applicationId 与正式证书；脚本不会替调用者提交或发布版本。

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
3. 单个 `windows-2022` job 顺序构建 Mod ZIP、完整 Windows 运行树 ZIP 和两个已签名 APK；桌面产物固化后先清理可再生缓存，再安装 Android 工具链。
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
| `mystia-steward-companion-companion-windows-x64.zip` | `application/zip` |
| `mystia-steward-companion-android-arm64-v8a.apk` | `application/vnd.android.package-archive` |
| `mystia-steward-companion-android-armeabi-v7a.apk` | `application/vnd.android.package-archive` |
| `update-manifest.json` | `application/json` |
| `update-catalog.json` | `application/json` |
| `SHA256SUMS.txt` | `text/plain; charset=utf-8` |

资产名和 MIME 是大小写敏感的单一映射；上传请求、mutation 响应和最终 Release 共用该映射，未知名称在任何远端写入前拒绝。

`SHA256SUMS.txt` 覆盖其余 6 项。`update-manifest.json` 只以 Mod ZIP 为安装权威；独立 Windows ZIP 和 APK 不参与 Mod 自动更新。`update-catalog.json` 提供累计版本说明，其读取失败不能改变 manifest 的下载与安装权威。更新协议详见[更新系统](update-system.md)。

## 本地预览版发布

预览版通过同一版本脚本设置 `X.Y.Z-preview.N` 和递增 Android 构建号；`pubspec` 仍保留 `+构建号`。提交并推送 `dev` 后，准备 UTF-8、无 BOM 的 Release Note 文件；不要手工创建 tag。显式设置 `MYSTIA_FLUTTER_SDK_ROOT`；涉及 Android 时还须设置锁定 JDK 的 `JAVA_HOME`。离线构建先按[隔离宿主测试依赖](#隔离宿主测试依赖)准备已核验运行库，在线构建由入口自动恢复。

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
- Windows Mod 自动更新、独立完整运行树 ZIP 与两种 ABI APK 都来自本次 run；
- 在项目目标 Windows 10/11 x64 上完成多 DPI、开始前取消及安装/故障回退实机验收；当前 updater 开始后不能取消等待或安装，不把其他分支的可取消功能纳入验收声明。Android 完成 LAN、Token、前后台和 Wi-Fi 重连真机验收；不能把离线验证记作已完成实机验收。

## Flutter 构建与单一整包清单

产品入口为 `apps/companion-flutter`，独立更新器为 `native/updater`。构建前显式设置 `MYSTIA_FLUTTER_SDK_ROOT`，构建 Android 时另设置锁定 JDK 的 `JAVA_HOME`；Flutter/Dart、VS/MSVC/CMake、Gradle/AGP/Kotlin/SDK/NDK 均由 `toolchain.lock.json` 校验。Windows 独立资产是完整运行树 ZIP，不能只分发一个 EXE。

Windows 树必须包含与 EXE 同级的三个小写 VC DLL：`msvcp140.dll`、`vcruntime140.dll`、`vcruntime140_1.dll`，以及 `THIRD-PARTY-NOTICES.txt`。构建从精确锁定的 VS Redist x64 目录补齐并验证签名、实际版本和 SHA-256；打包只读复核，遗漏或与来源不同即失败。独立 updater 静态链接 CRT，打包读取实际 PE 导入，拒绝仍依赖 VC DLL 的产物。详细来源与命令见[本地开发](local-development.md)，许可来源见根 [NOTICE](../NOTICE) 和微软 [2026](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution)/[2022](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution) 可分发代码清单。

`pwsh -File mods/bepinex/tools/build-release.ps1 -FlutterSdkRoot <绝对SDK路径>` 依次验证参考包、格式与 Pigeon 生成一致性，自动恢复并核验测试运行库，构建真实 HTTP 宿主，运行完整 Flutter 与 Dart 契约测试，构建 Flutter Windows/updater/三个 C# DLL 并事务打包。Android 正式签名另加 `-BuildAndroidApk`；缺少现有正式密钥材料会失败，不生成 debug 签名替代品。

公共 CI 和发布前无密钥验证显式运行 `flutter:verify-unit` 与 `flutter:contracts-unit`；`production-http` 标签测试由受保护 Windows 构建的完整验证执行，私有编译引用由 workflow 恢复，额外测试运行库由构建入口恢复。发布仍要求原有两道环境审批和七个精确资产，不因 Flutter 迁移降低来源校验。

包内 `package-manifest.json` 是唯一安装清单，包含所有受管理 DLL、Flutter data 与插件、大小和 SHA-256，以及 sourceCommit、sourceDigest、toolchainSha256、businessProtocolVersion=1 和 clientProtocolVersion=2。旧五文件清单不再作为新包格式接受。

版本修改统一使用上文的 `set-version.ps1`，不要分别手改版本字段。首次 Flutter APK 构建号为 1003002；新版本按当前值递增。

## Windows 工具链与运行平台核验范围

Flutter 3.47 的官方部署支持 Windows 10/11；本项目实际生成 x64 runner，manifest 同样声明 Windows 10/11。没有执行 Windows 10 各功能版本或 ARM64 设备验收，因此不把 updater 历史 DPI API 的最低版本推定成整个 Flutter 客户端的已验证最低版本。参见 [Flutter 官方支持平台](https://docs.flutter.dev/reference/supported-platforms)。

CI 使用 `windows-2022` 构建主机，目标配置为根锁 `flutterWindows.profiles` 中的 `vs2022-ci`；本机可使用另一个明确锁定的配置。实际 VS、cl.exe、CMake、Windows SDK 和 VCRedist 身份仍由构建时探针逐项确认，不能以某日公开镜像清单或本机构建结果推定当前 CI 环境已经通过。

`windows-2022` 是滚动镜像标签。后续镜像升级造成精确身份漂移时，脚本会拒绝构建，应审查实际探针后更新根锁，不能绕过检查或自动接受最新工具链。
