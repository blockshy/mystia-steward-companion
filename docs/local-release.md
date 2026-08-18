# 构建与发布流程

## 流程边界

项目把日常构建与正式发布明确分开：

- 日常开发、功能验证和预览版仍在本机完成。
- 正式稳定版只允许从 `main` 分支手动触发 `.github/workflows/release.yml`。
- workflow 不修改版本号、不提交代码、不合并分支，也不响应 push、tag 或 pull request。
- 正式发布由一个 GitHub-hosted `windows-2022` job 依次构建 Windows/Mod 与 Android 产物；Linux jobs
  负责无密钥输入验证、资产汇总、校验和、provenance 和最终 create-only 发布，不在 Linux 编译产品。
- 发布脚本只创建新 tag 和新 Release；已存在同名 tag 或 Release 时直接停止，不提供覆盖、编辑或删除路径。

根目录 `toolchain.lock.json` 是 Linux、Windows 与 CI 共用的唯一构建版本基线。选择文件、workflow
和脚本只是它的受控投影，不允许使用全局 pnpm、较新 SDK、`stable` 或 `latest` 回退。

## 日常本地构建

Windows 与 Linux 的基础版本为：

- Node.js `24.19.0`
- Corepack `0.35.0`
- pnpm `10.10.0`（只能通过 Corepack 调用）
- .NET SDK `10.0.110`
- Rust/Cargo `1.97.1`

Windows 还需要 PowerShell `7.6.4`、GitHub CLI `2.97.0`、Microsoft C++ Build Tools 2022 和
WebView2 Runtime。正式 workflow 不使用 runner 预装的 PowerShell/gh：每个 job 都先按
`toolchain.lock.json.releaseToolArchives` 锁定的官方 URL、字节数和 SHA-256 下载对应 Linux/Windows x64
归档，事务式安装后再精确检查版本。runner 预装版本不参与选择；归档身份、平台或内容不一致时直接停止。
事务目录就是调用前不存在的最终安装目录，只有完整复制、版本自检和 PATH 提交全部成功后才生效；Windows 不在
执行 staged `pwsh.exe` / `gh.exe` 后重命名其父目录，避免可执行文件扫描或句柄释放窗口导致 `EPERM`。
Corepack 安装器先下载锁定版本的 npm tarball，再按仓库记录的 SHA-512 integrity 校验，并安装到调用者指定的
全新隔离目录；正式 workflow 将该目录写入后续 step 的 PATH，不覆盖 runner 预装的 Yarn、pnpm 或 Corepack，
也不执行 `corepack enable`。本机已经安装精确 Corepack 时无需运行安装器；首次需要隔离安装时执行：

```powershell
$corepackParent = Join-Path $env:LOCALAPPDATA "mystia-steward-companion\toolchain"
New-Item -ItemType Directory -Force $corepackParent | Out-Null
$corepackRoot = Join-Path $corepackParent "corepack-0.35.0"
node scripts/install-locked-corepack.mjs --install-root $corepackRoot
$env:PATH = "$corepackRoot;$env:PATH"
corepack install
rustup toolchain install 1.97.1 --profile minimal
corepack pnpm toolchain:check
```

`--install-root` 必须是尚不存在的目录；版本升级时使用新的版本目录，不覆盖原目录。

Mod 仍以 `net6.0` 为目标；这不表示产品构建需要安装已停止支持的 .NET 6 SDK。真实
Harmony/MonoMod 动态探针按 `docs/development-conventions.md` 使用仓库锁定的 .NET 6 容器。

常规验证：

```powershell
corepack pnpm install --frozen-lockfile
corepack pnpm lint
corepack pnpm build
cargo check --manifest-path apps/companion/src-tauri/Cargo.toml
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

Windows 完整发布包构建：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1
```

该命令只生成本地资产，不创建 tag 或 Release。构建缓存按仓库统一配额管理，可用
`pnpm artifacts:report` 和 `pnpm artifacts:prune -- --dry-run` 预先检查。

## 锁定的 BepInEx 构建引用

真实 DLL 不提交到公开源码仓库。`mods/bepinex/References/references.lock.json` 锁定私有 bundle、
来源身份、bundle 大小与 SHA-256，以及 7 个正式 DLL 各自的大小与 SHA-256。详细恢复命令见
`mods/bepinex/References/README.md`。

正式 bundle 位于私有仓库 `blockshy/mystia-steward-build-assets`：

- tag：`bepinex-783-tmi-91ce5ae3-995d1a08-v2`
- asset：`mystia-steward-build-references.zip`

恢复脚本是离线严格校验器，不自行联网，也不尝试当前游戏目录、其他 BepInEx 版本、cache 或旧
interop 作为替代来源。恢复后可单独验证：

```powershell
corepack pnpm references:verify
```

## Android 本机构建

Android 构建在基础版本外精确锁定：

- Eclipse Temurin JDK `21.0.4`
- Android compile SDK `36`
- Android target SDK `36`
- Gradle `8.14.3`（wrapper distribution SHA-256 同步锁定）
- Android Build Tools `35.0.0`
- Android NDK SDK 包坐标 `30.0.14904198`（r30 beta1；包内 `Pkg.Revision` 必须为
  `30.0.14904198-beta1`）
- Rust targets：`aarch64-linux-android`、`armv7-linux-androideabi`

先确认 `JAVA_HOME` 与必填的 `ANDROID_HOME` 指向上述工具链，并把必填的 `NDK_HOME` 指向
`$ANDROID_HOME/ndk/30.0.14904198`；若同时设置 `ANDROID_SDK_ROOT`、`ANDROID_NDK`、
`ANDROID_NDK_HOME` 或 `ANDROID_NDK_ROOT`，这些别名也必须分别与对应的锁定目录一致。Windows 示例：

```powershell
$env:NDK_HOME = Join-Path $env:ANDROID_HOME "ndk\30.0.14904198"
rustup target add aarch64-linux-android armv7-linux-androideabi --toolchain 1.97.1
node scripts/check-build-toolchain.mjs android
```

本机签名仍使用被 Git 忽略的
`apps/companion/src-tauri/gen/android/keystore.properties`。生成签名 APK：

```powershell
corepack pnpm tauri:android:apk:signed
```

脚本会验证两个 APK 的签名证书 SHA-256 必须是：

```text
15:40:B6:09:D5:CD:54:E0:6A:84:29:BB:0A:AA:2C:C4:B5:11:E0:55:56:5F:DA:C9:3A:CF:20:6C:17:91:D1:FB
```

该值是最终 APK 的签名证书指纹，不是 keystore 文件本身的 SHA-256。只有两个 ABI 均通过构建、
签名与证书校验，并由锁定 Build Tools `35.0.0` 的 `aapt2 dump badging` 证明 applicationId、项目
`versionName`、唯一十进制 `versionCode` 和单一 `native-code` 分别精确匹配，而且不存在额外 release
signed APK 后，脚本才会原子替换 `mods/bepinex/dist` 中的 Android 资产。`versionCode` 遵循 Tauri v2 的
`major * 1_000_000 + minor * 1_000 + patch`；预览版 `X.Y.Z-preview.N` 使用同一个 `X.Y.Z` 核心 code，
minor/patch 超过 `999`、code 为 `0` 或超过 Android 上限 `2100000000` 时停止。

完整 Windows + Android 本机构建：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 -BuildAndroidApk
```

构建成功不等于 LAN 功能验证完成。正式发布前仍应在 Android 真机检查连接、Token、前后台恢复、
Wi-Fi 重连和 automation lease；没有真机时必须明确记录“仅构建、签名和包元数据通过”。

## 一次性 GitHub 配置

这些设置需要仓库管理员在 GitHub 网页完成；workflow 不创建 App、Environment 或长期凭据。

### 私有构建资产与只读 GitHub App

1. 保持 `blockshy/mystia-steward-build-assets` 为私有仓库，并为其 Release 启用 Immutable Releases。
2. 创建专用只读 GitHub App，Repository permissions 仅授予 `Contents: Read-only` 与
   `Administration: Read-only`。后者只用于在公开发布前读取 Immutable Releases 开关，不允许修改仓库设置。
3. 只把该 App 安装到 `mystia-steward-build-assets` 和 `mystia-steward-companion` 两个选定仓库，
   不要授予账户下的全部仓库。workflow 为每次用途再次向下收窄 token：私有仓库只取 `Contents: read`，
   主仓库只取 `Administration: read`。
4. 生成 App private key。workflow 每次只用 App ID 和 private key 换取短期 token，不保留 PAT，也不把引用 bundle 上传为 Actions artifact/cache。

### 两道 Environment 审批

在主仓库创建以下 Environments，并把 deployment branch 限制为 `main`：

- `official-release-build`：无密钥验证通过后、下载私有 References 和物化 Android 签名前审批。
- `official-release`：全部 7 项资产完成汇总、SHA-256 校验和 provenance 后，创建正式 Release 前审批。

两个 Environment 都应配置 required reviewers。若仓库目前只有发起 workflow 的同一位管理员，需要允许
发起者审批；若启用“Prevent self-review”，必须先增加另一位可信维护者，否则发布会永久等待。同时关闭
`Allow administrators to bypass configured protection rules`，否则管理员可绕过任一道审批。workflow 会在
构建前读取并核对 required reviewer、禁止管理员绕过和仅 `main` 的 branch policy；环境缺失或配置漂移时
直接停止。

在主仓库 `Settings -> General -> Releases` 中启用 **Immutable Releases**。该设置只影响此后发布的
Release；正式 publish job 会使用上面的只读 App 在任何远端写入前核对开关，并在发布后再次要求新 Release
实际为 immutable。不要用 PAT 或给 App 写入 `Administration` 权限。

建议同时在主仓库 Actions 设置中启用 **Require actions to be pinned to a full-length commit SHA**。
当前 workflow 与仓库审计已经逐项固定 full SHA；仓库级策略可阻止未来提交重新引入浮动 action tag。

### `official-release-build` Secrets

GitHub App 两项：

| Secret | 内容 |
| --- | --- |
| `BUILD_ASSETS_APP_ID` | 只读 GitHub App 的 Client ID（Secret 名称保持不变） |
| `BUILD_ASSETS_APP_PRIVATE_KEY` | 该 App 生成的完整 PEM private key |

Android 五项：

| Secret | 内容 |
| --- | --- |
| `MYSTIA_ANDROID_KEYSTORE_BASE64` | 发布 keystore 原始字节的单行 canonical Base64 |
| `MYSTIA_ANDROID_KEYSTORE_SHA256` | keystore 文件字节的 64 位小写 SHA-256 |
| `MYSTIA_ANDROID_KEY_ALIAS` | 发布 key alias |
| `MYSTIA_ANDROID_STORE_PASSWORD` | keystore 密码 |
| `MYSTIA_ANDROID_KEY_PASSWORD` | key 密码；相同时仍显式填写同一值 |

`MYSTIA_ANDROID_KEYSTORE_SHA256` 只保护 CI 解码出的 keystore 文件，不能填写 APK 证书指纹。APK 证书
指纹已经锁在 `toolchain.lock.json`，构建后由 `apksigner` 独立校验。

`official-release` Environment 还需配置同名的 `BUILD_ASSETS_APP_ID`（值同样为 Client ID）与
`BUILD_ASSETS_APP_PRIVATE_KEY`。GitHub Environment Secrets 不能跨环境共享；第二份只用于生成主仓库
`Administration: read` 的短期 policy token，不会获得 Contents 写权限。创建 Release 仍只使用该 job
自带、仅当前仓库有效且仅 `contents: write` 的 `GITHUB_TOKEN`。

Windows PowerShell 可在本地生成上表 Android 五项中的前两项值，输出后直接粘贴到 GitHub Environment
Secret，不要写入仓库：

```powershell
$keystore = "$env:USERPROFILE\.android\mystia-steward-companion-release.jks"
[Convert]::ToBase64String([IO.File]::ReadAllBytes($keystore))
(Get-FileHash -Algorithm SHA256 -LiteralPath $keystore).Hash.ToLowerInvariant()
```

签名文件只在 runner 临时目录和被忽略的 `keystore.properties` 中短暂物化，并在 package build job 的
`always()` 清理步骤删除。秘密不得进入日志、artifact、cache、Release 或诊断包。

## 正式稳定版发布

### 1. 准备 `main`

在 `dev` 更新版本并同步以下五处：

- `package.json`
- `apps/companion/src-tauri/tauri.conf.json`
- `apps/companion/src-tauri/Cargo.toml`
- `apps/companion/src-tauri/Cargo.lock`
- `MystiaStewardCompanionPlugin.PluginVersion`

运行发布前验证、提交并推送 `dev`，随后只以 fast-forward 把 `dev` 合并到 `main` 并推送。正式版本必须是
canonical `X.Y.Z`，workflow 不接受 preview 后缀。不要预先创建或推送 tag；发布 job 会把新 tag 精确绑定到
本次经过验证的完整 `main` SHA。

### 2. 手动触发 workflow

进入 GitHub `Actions -> Official stable release -> Run workflow`，分支选择 `main`，填写：

- `tag`：`vX.Y.Z`
- `title`：通常与 tag 相同
- `notes`：面向普通用户的 Markdown Release Note

workflow 会依次执行：

1. 在无 secret 的 `validate` job 确认当前提交仍是 `origin/main`、版本五处一致、tag/Release 不存在，并完成 lint、build 和发布策略审计。
2. 等待 `official-release-build` 审批。
3. 在唯一的 `windows-2022` 构建 job 中恢复精确 References、构建 Windows/Mod 包，清理可重建的桌面缓存，
   再临时物化签名并构建两个 Android APK；因此第一次 Environment 审批只对应一个 job。
4. 构建 job 先分别输出四个二进制资产的 SHA-256；Linux 汇总 job 下载 artifact 后逐项对照这些 digest，
   再生成 `update-catalog.json`、`update-manifest.json`、`SHA256SUMS.txt`，验证精确资产集合并生成 provenance。
5. 等待 `official-release` 审批。
6. 先原子创建精确指向该 `main` SHA 的轻量 tag，再创建无资产 Draft。两个 POST 直接响应分别锁定 exact
   ref、positive numeric Release id 和精确 `uploads.github.com` URI template；之后不再按 tag、分页列表或
   GraphQL 发现当前事务。7 项资产通过该 numeric-id upload URL 逐项串行 raw upload，并核对
   `state`、`content_type`、精确大小和 GitHub `sha256:` digest；全部一致后才 PATCH 同一 numeric Draft id
   公开为 Latest。mutation 至少间隔 1 秒且绝不重试；只读 exact-ref、direct-id 与 Latest 检查只对明确的
   404、合法资产子集、旧 Draft 或 immutable/Latest 尚未收敛状态作有界等待，畸形响应和身份漂移立即停止。
   最后复核 Release body、direct tag ref、完整资产 allowlist 和 immutable 状态。提交身份只以
   `object.type=commit` 且 SHA 精确相等的 direct tag ref 为权威，不使用 Release `target_commitish` 作为证明。
   这段远端流程由单一内部事务函数执行；`pnpm audit:release-policy` 会以分页列表始终缺少新 Draft、
   direct-id/exact-ref/Latest 延迟可见的有状态 GitHub CLI 桩，完整演练 7 项资产和公开前停止分支。

若审批期间 `main` 前进、同名 tag/Release 出现、版本或资产漂移，流程会停止；不要复用该运行的旧产物。
中间 artifact 保留 40 天，以覆盖 workflow 最长 35 天（包括两次审批等待）的生命周期。失败后不要使用
`Re-run failed jobs` 或单独重跑 job，因为正式
产物名称绑定原始 run attempt；应先按 exact ref 和 numeric Release id 检查是否已留下 tag/Draft。远端完全
未变更时重新手动 dispatch，已留下 tag 或 Draft 时停止并人工处理，脚本不会自动删除、覆盖、恢复或按 tag
续传。正式 publish job 内的脚本已经完成 numeric-id 资产、immutable 和 Latest 终检；workflow 不在下一步
再次用 `gh release view <tag>` 发现刚公开的对象，避免公开成功后因 tag 索引短暂延迟制造不可重跑的假失败。

### 3. 正式资产

正式 Release 必须恰好包含 7 项：

| 资产 | 精确 `Content-Type` |
| --- | --- |
| `mystia-steward-companion-bepinex.zip` | `application/zip` |
| `mystia-steward-companion-companion-windows-x64.exe` | `application/x-msdownload` |
| `mystia-steward-companion-android-arm64-v8a.apk` | `application/vnd.android.package-archive` |
| `mystia-steward-companion-android-armeabi-v7a.apk` | `application/vnd.android.package-archive` |
| `update-manifest.json` | `application/json` |
| `update-catalog.json` | `application/json` |
| `SHA256SUMS.txt` | `text/plain; charset=utf-8` |

资产名与 MIME 类型使用同一套大小写敏感规范映射；上传前、上传响应和最终远端 allowlist 都必须逐项匹配，
未知资产名在任何远端写入前停止。

`SHA256SUMS.txt` 校验前 6 项。`update-manifest.json` 的安装权威只指向 Mod ZIP；Windows 独立伴随窗口
和 Android APK 供 B 设备连接使用，不参与 Mod 自动更新。`update-catalog.json` 只负责累计版本说明展示，
读取失败不得改变 manifest 的下载与安装权威。当前 owner 条目的 `publishedAtUtc` 是不可变资产的准备时间；
该版本在后续 catalog 中成为历史条目时会改用 GitHub `published_at`，因此首次进入历史列表时可能发生一次
时间修正，但版本顺序和更新权威不受影响。

## 本地预览版发布

预览版仍可从已推送的 `dev` 提交在本机发布，用于验证 `preview.1 -> preview.2 -> stable` 更新链路。
先把五处版本同步为 `X.Y.Z-preview.N`、提交并推送，然后准备 UTF-8 Release Note 文件。不要手工创建或
推送 tag；脚本会在确认远端不存在同名 tag/Release 后创建一次。

```powershell
$tag = "v1.4.0-preview.1"
$target = (git rev-parse HEAD).Trim().ToLowerInvariant()
$notesFile = Join-Path $env:TEMP "mystia-release-notes.md"
[IO.File]::WriteAllText(
  $notesFile,
  "预览版更新测试说明",
  [Text.UTF8Encoding]::new($false)
)

pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag $tag `
  -Title $tag `
  -NotesFile $notesFile `
  -TargetCommitSha $target
```

如需让预览 Release 同时包含两个 Android APK，确保本机构建环境与签名配置完整后增加
`-BuildAndroidApk`。不加该参数时，预览 Release 不要求 Android 工具链。

`publish-release.ps1` 默认先构建，再调用 `prepare-release-assets.ps1` 生成累计 catalog、manifest 和
SHA-256 清单。只有 CI 或明确的故障恢复场景才使用 `-SkipBuild`；此时调用者必须已经用完全相同的 tag、
标题、Note 文件和目标 SHA 准备好当前 `dist`，脚本仍会复核全部内容，不接受上次构建残留。

测试者需要在 BepInEx 配置中开启：

```ini
[Updates]
IncludePrerelease = true
```

## Release Note 与失败处理

Release Note 只写从上个正式版本到本版本的用户可见新增、体验优化、修复与稳定性，不写提交整理、内部
重构、框架升级、构建流程或开发文档。可先检查：

```powershell
git log --oneline v1.3.0..HEAD
```

发布脚本的输入以 `-NotesFile` 和 40 位小写 `-TargetCommitSha` 为准，避免多行 Markdown 经过命令插值，
并确保 tag 不会漂移到后续提交。发布创建过程中如果 GitHub mutation 返回失败，脚本不会自动重试、删除或
覆盖任何既有 Release/tag；公开前失败最多留下精确 tag 和未公开 Draft，必须按 mutation 已返回的 numeric
Release id 人工检查远端状态和 7 项资产，再决定使用新版本号重新发布。direct-id 只读终检会对明确的短暂
一致性状态有界等待，但不会把 401/403、畸形对象、错误 upload URL 或身份漂移当作重试条件。不要通过删除
正式 tag、修改已发布资产、`Re-run failed jobs` 或单独重跑 job 来“续跑”失败流程。

正式发布完成后至少核对：

- tag 指向 workflow 验证的 `main` SHA；
- Release 不是 Draft 或 Prerelease，并被标记为 Latest；
- 7 项资产名称与大小正确，`SHA256SUMS.txt` 可复算；
- Mod ZIP 自动更新、Windows 独立伴随窗口、两个 Android ABI 安装包均来自本次运行；
- updater 在 Windows 10 1703+ 的 100%、125%、150% 和 200% 缩放下文字清晰、布局正确，安装与取消流程正常。
