# Flutter updater P0 契约与 Windows 启动探针

本目录是迁移探针，不是产品安装器。只验证旧 CLI、runner 自绑定、内嵌 bundle、两个进程的 hello/cancel 与状态回写；**没有安装/开始命令，不打开或关闭游戏，不连接 32146，不修改插件目录**。Windows bootstrap 只持有自己启动的 Flutter UI 子进程；异常清理也只终止该精确子进程。

当前证据：Linux 临时目录、ZIP、JSON 与协议单元测试；可在 Linux 使用已安装的锁定 Windows target 做类型检查。Windows 实际启动、Defender/SmartScreen、真实旧 Mod 和游戏链必须另行实测，不能由这些测试代替。

## 代码边界

- `src/launch.rs`：现行 `UpdateService` 的 `--key value` 参数、产品目录与路径分离、同名 runner 逐字节校验。
- `src/bundle.rs`、`src/archive.rs`：有界清单、Windows 可移植路径、精确文件集合、大小/SHA-256 和 ZIP 展开。拒绝未知条目、大小写歧义、重复项、链接和重解析点；失败保留本任务新建的临时目录，不删除旧文件。
- `src/status.rs`：旧 Mod 已识别的状态 JSON；`terminating-game` 仅保留 wire 解码能力，不提供对应动作。
- `src/wire.rs`：P0 的两个消息状态机，不能用于产品安装控制。
- `src/windows_bootstrap.rs`：Windows-only 进程、受限命名管道、随机会话、结果回写；唯一公开结果是带 P0 说明的 `cancelled` 或 `failed`。
- `src/bin/bootstrap.rs`：输出名保持 `mystia-steward-companion-updater.exe`。无内嵌资源的构建明确拒绝运行，Linux 执行也明确拒绝。

这些校验是当前文件系统状态的检查；不会证明文件在未来执行/替换时未被其他进程改变，不是正式安装器的完整安全审计或 TOCTOU 防护。路径组件检查包括 Windows reparse 标志，但句柄固定、硬链接策略、磁盘故障恢复和真实 ZIP 发布来源信任仍须进入正式实现审查。

## 锁定构建与测试

根 `toolchain.lock.json` 和 `rust-toolchain.toml` 保持唯一基线（当前 Rust/Cargo `1.97.1`）。依赖使用精确版本和本目录 `Cargo.lock`；未更改 Tauri、Mod 或根配置。

从仓库根目录运行：

```bash
env CARGO_HOME=/home/blockshy/.cargo RUSTUP_HOME=/home/blockshy/.rustup \
  /home/blockshy/.cargo/bin/cargo test --locked --offline \
  --manifest-path tests/flutter-updater-probe/Cargo.toml

env CARGO_HOME=/home/blockshy/.cargo RUSTUP_HOME=/home/blockshy/.rustup \
  /home/blockshy/.cargo/bin/cargo check --locked --offline \
  --target x86_64-pc-windows-msvc \
  --manifest-path tests/flutter-updater-probe/Cargo.toml
```

`cargo check` 不链接或运行 Windows 程序；最终 EXE 必须在锁定 Windows/MSVC 环境构建。资源构建通过三项环境变量显式指定：

| 环境变量 | 内容 |
| --- | --- |
| `MYSTIA_UPDATER_PROBE_BUNDLE_ZIP` | 完整 Flutter updater UI bundle ZIP 的绝对路径 |
| `MYSTIA_UPDATER_PROBE_BUNDLE_MANIFEST` | 下述清单 JSON 的绝对路径，不放入 ZIP |
| `MYSTIA_UPDATER_PROBE_PRODUCT_VERSION` | 本次探针对齐的产品版本，来自根 `package.json` |

三项同时设置或同时不设置。Windows 构建命令：

```powershell
cargo build --locked --release --manifest-path tests/flutter-updater-probe/Cargo.toml `
  --bin mystia-steward-companion-updater
```

清单示例仅展示结构；实际 size/hash 必须从真实构建输出生成：

```json
{
  "schemaVersion": 1,
  "product": "mystia-steward-companion",
  "version": "1.3.1",
  "entrypoint": "mystia-steward-companion-updater-ui.exe",
  "files": [
    { "path": "mystia-steward-companion-updater-ui.exe", "size": 3,
      "sha256": "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" }
  ]
}
```

真实清单必须包括 engine、插件、运行库、data/assets 的全部文件。ZIP 不加顶层包装目录；允许已列文件的父目录条目，省略没有文件的空目录。普通零字节资源合法，entrypoint 必须非空。P0 路径限定 ASCII 字母/数字与 `-_.+@/`，用于在 Linux 上同样拒绝 Windows 大小写和设备名歧义；若实际构建输出超出此集合，显式停止并审查规则，不自动重命名或丢弃资源。

P0 上限为清单 1 MiB、8192 文件、单文件 512 MiB、总展开 1 GiB、ZIP 512 MiB、相对路径 240 字节/16 层。它们是可审查的探针边界，不是已通过性能测量的正式发行预算。

## 用户运行用的隔离 fixture

构建包装脚本应创建全新临时根，而不是选择真实游戏或现有用户目录：

```text
<unique-root>/
  mystia-steward-companion/                        # 已存在空目录
  staging/mystia-steward-companion-updater.exe     # 本次 bootstrap
  runner/mystia-steward-companion-updater.exe      # 同字节副本
  backups/                                       # 存在；probe-old 叶子不存在
  state/install-status.json                       # 父目录存在，可有初始 waiting
```

传入原生绝对路径，`game-pid` 使用调用脚本进程 PID 作为 fixture 值；它不被观察或用于游戏操作：

```text
--game-pid <fixture-pid>
--plugin-dir <unique-root>/mystia-steward-companion
--staged-dir <unique-root>/staging
--backup-dir <unique-root>/backups/probe-old
--status-file <unique-root>/state/install-status.json
--control-port 32146
--wait-timeout-seconds 300
```

缺失、重复、未知参数、非规范整数、路径重叠和已有备份均拒绝。不从部分失败的 CLI 提取路径写错误文件。probe 不要求伪造 Mod DLL 或 companion EXE：fixture 只证明本探针启动链；真实旧 Mod 的包校验和启动必须用完整候选包单独验证。

bootstrap 在 runner 下创建带随机会话名的独占 `.partial` 目录，完整校验后改名，再运行确切 UI 文件。公开状态文件写临时文件、刷盘、改名；写入失败返回非零，若临时文件已生成会保留精确路径用于诊断。已展开的探针目录不自动删除，供用户收证。

## 与 Flutter/C++ 的固定 P0 wire

Pigeon 仅负责同一 Flutter 进程内 Dart ↔ C++；C++ ↔ bootstrap 使用本节独立命名管道协议。

UI 参数：`--updater-probe-pipe=...`、`--updater-probe-session=<32 位小写十六进制>`、`--updater-probe-parent-pid=...`。pipe 限当前登录会话 ACL、禁止远程客户端、随机名称且仅一个实例；bootstrap 核对 pipe 客户端 PID 为自己启动并持有句柄的 UI。C++ 必须反查 pipe 服务端 PID、核对父进程并持有句柄。随机 session 不替代进程身份检查。

客户端 `CreateFile` 权限使用 `GENERIC_READ | FILE_WRITE_DATA`；DACL 不授予 `FILE_APPEND_DATA`（该位也代表创建管道实例），因此不要请求泛化 `GENERIC_WRITE`。pipe 是 byte mode；双方按 LF 划分 UTF-8 JSON，单帧不含 LF 最多 16 KiB，拒绝 CR、未知/重复字段及未支持版本。

```json
{"protocolVersion":1,"session":"<nonce>","requestId":1,"command":"hello"}
{"protocolVersion":1,"session":"<nonce>","requestId":1,"stateSequence":1,"state":"ready","message":"P0 只读探针；不会执行安装或游戏操作"}
{"protocolVersion":1,"session":"<nonce>","requestId":2,"command":"cancel"}
{"protocolVersion":1,"session":"<nonce>","requestId":2,"stateSequence":2,"state":"cancelled","message":"P0 探针已取消；未修改插件目录或关闭游戏"}
```

连接加 hello 总时限 30 秒；ready 后等待 cancel 不超过旧 `wait-timeout-seconds` 与 300 秒中的较小值；响应写入 3 秒。cancel 回包后 UI 应有序退出，bootstrap 最多等待 10 秒。断开、超时、重放、乱序及非 hello/cancel 命令均失败，不产生成功安装状态。超时失败只清理自己启动的 UI 子进程。

后续缺口：Windows/MSVC 最终链接和运行、ACL/管道失败场景、完整 Flutter 资源/VC 运行库、UI 关闭与崩溃、文件被占用/磁盘不足/状态回写失败、干净机防护、真实旧 Mod 下载与启动。P0 通过后仍没有授权或实现正式安装事务。
