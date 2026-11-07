//! 自动更新的独立文件替换程序。
//!
//! 主 Mod 进程只负责下载、校验和解压更新包；本程序从配置目录中的 runner 副本启动，
//! 等待游戏进程退出后再替换插件目录。这样可以避免运行中的 BepInEx DLL 或伴随窗口 exe
//! 被自身进程锁定导致半更新。

#![cfg_attr(target_os = "windows", windows_subsystem = "windows")]
#![cfg_attr(
    all(target_os = "linux", feature = "updater-windows-ui-check"),
    allow(dead_code)
)]

mod client_exit;
#[cfg(any(windows, feature = "updater-windows-ui-check"))]
mod game_process;
mod install_control;
mod legacy_cleanup;
mod legacy_cleanup_client;
mod legacy_cleanup_layout;
mod offline_migration;
mod package_manifest;
#[cfg(any(windows, feature = "updater-windows-ui-check"))]
use game_process::GameProcess;
use install_control::InstallControl;

/// 隔离进程观察与安装事务；测试只提供状态序列，不需要操作真实游戏。
trait ProcessObservation {
    fn is_running(&self) -> Result<bool, String>;
}
#[cfg(any(windows, feature = "updater-windows-ui-check"))]
impl ProcessObservation for GameProcess {
    fn is_running(&self) -> Result<bool, String> {
        self.is_running()
    }
}
#[cfg(not(any(windows, feature = "updater-windows-ui-check")))]
struct GameProcess {
    pid: u32,
}
#[cfg(not(any(windows, feature = "updater-windows-ui-check")))]
impl GameProcess {
    fn open(pid: u32) -> Result<Self, String> {
        if pid == 0 {
            return Err("游戏进程编号无效。".into());
        }
        Ok(Self { pid })
    }
}
#[cfg(not(any(windows, feature = "updater-windows-ui-check")))]
impl ProcessObservation for GameProcess {
    fn is_running(&self) -> Result<bool, String> {
        Ok(Path::new("/proc")
            .join(self.pid.to_string())
            .try_exists()
            .map_err(|e| e.to_string())?)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum InstallOutcome {
    Succeeded,
    Cancelled,
}
const CANCELLED_MESSAGE: &str = "已取消安装，插件文件未替换。";
use std::collections::HashMap;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process;
use std::thread;
use std::time::{Duration, Instant};

const DEFAULT_CONTROL_PORT: u16 = 32146;
const DEFAULT_WAIT_TIMEOUT_SECONDS: u64 = 1800;
const REQUIRED_DLL: &str = "MystiaStewardCompanion.BepInEx.dll";
const REQUIRED_COMPANION_EXE: &str = "companion/mystia-steward-companion.exe";
const REQUIRED_UPDATER_EXE: &str = "mystia-steward-companion-updater.exe";
#[cfg(any(
    target_os = "windows",
    test,
    all(target_os = "linux", feature = "updater-windows-ui-check")
))]
const BASE_DPI: u32 = 96;

#[cfg(any(
    target_os = "windows",
    test,
    all(target_os = "linux", feature = "updater-windows-ui-check")
))]
fn scale_logical_pixels(value: i32, dpi: u32) -> i32 {
    let dpi = dpi.max(1);
    let scaled = i64::from(value) * i64::from(dpi);
    let rounded = if scaled >= 0 {
        scaled + i64::from(BASE_DPI / 2)
    } else {
        scaled - i64::from(BASE_DPI / 2)
    };
    (rounded / i64::from(BASE_DPI)).clamp(i64::from(i32::MIN), i64::from(i32::MAX)) as i32
}

#[derive(Clone)]
struct InstallContext {
    game_pid: u32,
    plugin_dir: PathBuf,
    staged_dir: PathBuf,
    backup_dir: PathBuf,
    control_port: u16,
    wait_timeout: Duration,
}

#[derive(Clone)]
struct InstallProgress {
    state: &'static str,
    message: String,
    progress: u8,
}

impl InstallProgress {
    fn new(state: &'static str, message: impl Into<String>, progress: u8) -> Self {
        Self {
            state,
            message: message.into(),
            progress: progress.min(100),
        }
    }
}

/// 解析命令行参数并执行一次安装流程。
///
/// 失败时会尽量把错误写入 `--status-file`，供下一次 Mod 启动后在设置页展示。
fn main() {
    // 只读验证入口供 Node/C#/Rust 共用夹具矩阵调用；不创建状态文件、不显示窗口、不执行安装。
    let raw: Vec<String> = env::args().skip(1).collect();
    // 旧版残留清理与安装分别准入：默认只生成预览，删除需要用户确认迁移及绑定本次清单摘要。
    // 不经普通更新 GUI 启动，也不因为安装成功自动删除旧客户端资料或回退副本。
    if raw.first().is_some_and(|value| value == "--cleanup-v1-3-1") {
        let terminal = prepare_cli_output();
        match legacy_cleanup::run(&raw[1..]) {
            Ok(report) => show_cli_message(&report, false, terminal),
            Err(error) => {
                show_cli_message(&error, true, terminal);
                process::exit(1);
            }
        }
        return;
    }
    // 保留此前维护脚本的显式离线入口。公开 v1.3.1 的默认升级步骤现为手动替换程序目录；
    // 普通更新流程仍要求完整 Flutter 安装树，不得把这个维护入口当作在线兼容回退。
    if raw.first().is_some_and(|value| value == "--migrate-v1-3-1") {
        let terminal = prepare_cli_output();
        match offline_migration::run(&raw[1..]) {
            Ok(report) => show_cli_message(&report, false, terminal),
            Err(error) => {
                show_cli_message(&error, true, terminal);
                process::exit(1);
            }
        }
        return;
    }
    if raw.len() == 2 && raw[0] == "--validate-package" {
        if let Err(error) = package_manifest::validate(Path::new(&raw[1]), false) {
            eprintln!("{error}");
            process::exit(1);
        }
        return;
    }
    let args = parse_args(raw);
    // 无参数或相对状态路径不具有写入授权；不得再向进程的任意当前目录生成 update-status.json。
    let status_file = match explicit_status_path(&args) {
        Ok(path) => path,
        Err(error) => {
            show_cli_message(&error, true, prepare_cli_output());
            process::exit(1);
        }
    };
    #[cfg(target_os = "windows")]
    let result = windows_updater_ui::run(args, status_file.clone());
    #[cfg(not(target_os = "windows"))]
    let result = run_silent(&args, &status_file);

    if let Err(error) = result {
        write_status(&status_file, "failed", &error, 100);
        eprintln!("{error}");
        process::exit(1);
    }
}

/// GUI 子系统默认没有控制台；显式命令行模式附着父控制台，并保留调用方已经重定向的输出句柄。
fn prepare_cli_output() -> bool {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::{
            Foundation::INVALID_HANDLE_VALUE,
            System::Console::{
                AttachConsole, GetStdHandle, SetStdHandle, ATTACH_PARENT_PROCESS, STD_ERROR_HANDLE,
                STD_OUTPUT_HANDLE,
            },
        };
        let output = GetStdHandle(STD_OUTPUT_HANDLE);
        let error = GetStdHandle(STD_ERROR_HANDLE);
        if !output.is_null()
            && output != INVALID_HANDLE_VALUE
            && !error.is_null()
            && error != INVALID_HANDLE_VALUE
        {
            return true;
        }
        if AttachConsole(ATTACH_PARENT_PROCESS) == 0 {
            return false;
        }
        // AttachConsole 会重设标准句柄；逐一恢复原有有效句柄，保证仅重定向单路输出时也不丢结果。
        if !output.is_null() && output != INVALID_HANDLE_VALUE {
            SetStdHandle(STD_OUTPUT_HANDLE, output);
        }
        if !error.is_null() && error != INVALID_HANDLE_VALUE {
            SetStdHandle(STD_ERROR_HANDLE, error);
        }
        true
    }
    #[cfg(not(windows))]
    {
        true
    }
}

/// 没有终端时以原生对话框给出结果；预览仍不写文件，错误也不落入任意工作目录。
fn show_cli_message(message: &str, failed: bool, terminal: bool) {
    if terminal {
        if failed {
            eprintln!("{message}");
        } else {
            println!("{message}");
        }
        return;
    }
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            MessageBoxW, MB_ICONERROR, MB_ICONINFORMATION, MB_OK,
        };
        let text: Vec<u16> = message.encode_utf16().chain(Some(0)).collect();
        let title: Vec<u16> = "Mystia Steward Companion"
            .encode_utf16()
            .chain(Some(0))
            .collect();
        MessageBoxW(
            std::ptr::null_mut(),
            text.as_ptr(),
            title.as_ptr(),
            MB_OK
                | if failed {
                    MB_ICONERROR
                } else {
                    MB_ICONINFORMATION
                },
        );
    }
}

fn explicit_status_path(args: &HashMap<String, String>) -> Result<PathBuf, String> {
    let path = get_path(args, "status-file")
        .ok_or("missing explicit --status-file; no status file was written")?;
    if !path.is_absolute() || path.file_name().is_none() || path.is_dir() {
        return Err("--status-file must be an explicit absolute file path".into());
    }
    offline_migration::ensure_no_links(&path)?;
    // 只有一份完整的安装请求才能取得状态写入能力；参数缺失时也不创建旁路失败文件。
    parse_install_context(args)?;
    Ok(path)
}

#[cfg(not(target_os = "windows"))]
fn run_silent(args: &HashMap<String, String>, status_file: &Path) -> Result<(), String> {
    let context = parse_install_context(args)?;
    let mut ignore_progress = |_progress: InstallProgress| {};
    let game = GameProcess::open(context.game_pid)?;
    run_install(
        &context,
        status_file,
        &game,
        &InstallControl::default(),
        &mut ignore_progress,
    )
    .map(|_| ())
}

fn parse_install_context(args: &HashMap<String, String>) -> Result<InstallContext, String> {
    let game_pid = get_u32(args, "game-pid").ok_or("missing --game-pid")?;
    let plugin_dir = get_path(args, "plugin-dir").ok_or("missing --plugin-dir")?;
    let staged_dir = get_path(args, "staged-dir").ok_or("missing --staged-dir")?;
    let backup_dir = get_path(args, "backup-dir").ok_or("missing --backup-dir")?;
    let control_port = get_u16(args, "control-port").unwrap_or(DEFAULT_CONTROL_PORT);
    let wait_timeout = Duration::from_secs(
        get_u64(args, "wait-timeout-seconds").unwrap_or(DEFAULT_WAIT_TIMEOUT_SECONDS),
    );

    Ok(InstallContext {
        game_pid,
        plugin_dir,
        staged_dir,
        backup_dir,
        control_port,
        wait_timeout,
    })
}

/// 执行完整的退出等待、备份、替换和最终校验流程。
///
/// 参数由 Mod 的 `UpdateService.InstallOnExit` 传入；调用方必须保证 staged 目录已经通过
/// zip 路径安全检查和 SHA256 校验。本函数在等待前与实际替换事务中复核成套摘要；
/// 安装后的校验也属于同一事务，失败时保留新包诊断材料并恢复原目录。
fn run_install(
    context: &InstallContext,
    status_file: &Path,
    game: &impl ProcessObservation,
    control: &InstallControl,
    progress: &mut dyn FnMut(InstallProgress),
) -> Result<InstallOutcome, String> {
    // 工作线程的错误与正常取消都必须发布一个终态；窗口随后等待线程退出才能释放控件。
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        run_install_steps(context, status_file, game, control, progress)
    }))
    .unwrap_or_else(|_| Err("安装线程异常终止，请检查插件与备份目录。".into()));
    let event = match &result {
        Ok(InstallOutcome::Succeeded) => {
            InstallProgress::new("succeeded", "更新安装完成。请重新启动游戏。", 100)
        }
        Ok(InstallOutcome::Cancelled) => InstallProgress::new("cancelled", CANCELLED_MESSAGE, 0),
        Err(error) => InstallProgress::new("failed", error, 100),
    };
    publish_progress(status_file, progress, event);
    control.finish()?;
    result
}

fn run_install_steps(
    context: &InstallContext,
    status_file: &Path,
    game: &impl ProcessObservation,
    control: &InstallControl,
    progress: &mut dyn FnMut(InstallProgress),
) -> Result<InstallOutcome, String> {
    if control.cancelled()? {
        return Ok(InstallOutcome::Cancelled);
    }
    publish_progress(
        status_file,
        progress,
        InstallProgress::new("preparing", "正在校验更新包。准备与等待期间可以取消。", 5),
    );
    validate_update_source(&context.plugin_dir)?;
    package_manifest::validate(&context.staged_dir, false)?;
    if !control.enter_waiting()? {
        return Ok(InstallOutcome::Cancelled);
    }
    if !wait_for_game_exit(
        game,
        control,
        context.game_pid,
        context.wait_timeout,
        status_file,
        progress,
    )? {
        return Ok(InstallOutcome::Cancelled);
    }
    if control.cancelled()? {
        return Ok(InstallOutcome::Cancelled);
    }
    publish_progress(
        status_file,
        progress,
        InstallProgress::new(
            "closing-companion",
            "正在确认客户端退出。仍可取消后续安装。",
            38,
        ),
    );
    // 保留 Flutter v2 ACK 的 PID + 创建时间核验，并允许取消客户端退出等待。
    if !client_exit::request_and_wait_cancellable(
        context.control_port,
        &context.plugin_dir,
        Duration::from_secs(10),
        control,
    )? {
        return Ok(InstallOutcome::Cancelled);
    }
    // 所有安装树写入只允许通过这一处仲裁。迟到的关闭事件不能中断备份/替换/回滚。
    if !control.begin_replacement()? {
        return Ok(InstallOutcome::Cancelled);
    }
    prepare_parent(&context.backup_dir)?;
    publish_progress(
        status_file,
        progress,
        InstallProgress::new("installing", "正在替换插件文件，完成前不能取消。", 45),
    );
    replace_plugin_directory(
        &context.plugin_dir,
        &context.staged_dir,
        &context.backup_dir,
        status_file,
        progress,
    )?;
    Ok(InstallOutcome::Succeeded)
}

/// 解析 `--key value` 或 `--flag` 形式的简单参数。
///
/// updater 只由本项目启动，不需要支持复杂 shell 语法；保持解析器小而可控可以降低发布包依赖。
fn parse_args(items: Vec<String>) -> HashMap<String, String> {
    let mut parsed = HashMap::new();
    let mut index = 0;
    while index < items.len() {
        let key = items[index].trim_start_matches("--").to_string();
        if key.is_empty() {
            index += 1;
            continue;
        }

        let value = if index + 1 < items.len() && !items[index + 1].starts_with("--") {
            index += 1;
            items[index].clone()
        } else {
            "true".to_string()
        };
        parsed.insert(key, value);
        index += 1;
    }
    parsed
}

fn get_path(args: &HashMap<String, String>, key: &str) -> Option<PathBuf> {
    args.get(key)
        .filter(|value| !value.trim().is_empty())
        .map(PathBuf::from)
}

fn get_u16(args: &HashMap<String, String>, key: &str) -> Option<u16> {
    args.get(key).and_then(|value| value.parse::<u16>().ok())
}

fn get_u32(args: &HashMap<String, String>, key: &str) -> Option<u32> {
    args.get(key).and_then(|value| value.parse::<u32>().ok())
}

fn get_u64(args: &HashMap<String, String>, key: &str) -> Option<u64> {
    args.get(key).and_then(|value| value.parse::<u64>().ok())
}

/// 只观察已打开的游戏进程对象，不能发送关闭消息或终止进程。
/// 观察失败与超时均停止安装；只有明确退出后才允许进入客户端确认与文件替换。
fn wait_for_game_exit(
    game: &impl ProcessObservation,
    control: &InstallControl,
    pid: u32,
    timeout: Duration,
    status_file: &Path,
    progress: &mut dyn FnMut(InstallProgress),
) -> Result<bool, String> {
    let started = Instant::now();
    let mut next_report = Duration::ZERO;
    while started.elapsed() < timeout {
        if control.cancelled()? {
            return Ok(false);
        }
        if !game.is_running()? {
            publish_progress(
                status_file,
                progress,
                InstallProgress::new("game-closed", "已确认游戏退出。", 35),
            );
            return Ok(true);
        }
        let elapsed = started.elapsed();
        if elapsed >= next_report {
            publish_progress(
                status_file,
                progress,
                InstallProgress::new(
                    "waiting-game",
                    format!(
                        "请保存并退出游戏。等待进程 {pid} 结束，已等待 {} 秒。",
                        elapsed.as_secs()
                    ),
                    25,
                ),
            );
            next_report = elapsed + Duration::from_secs(2);
        }
        control.wait(Duration::from_millis(250))?;
    }
    Err(format!("等待游戏进程 {pid} 退出超时，插件文件未替换。"))
}

/// 校验目标路径确实像本项目插件目录。
///
/// 这是替换前的最后一道保护，避免参数错误时把任意目录改名为备份。
fn validate_plugin_path(plugin_dir: &Path) -> Result<(), String> {
    if plugin_dir.as_os_str().is_empty() {
        return Err("plugin directory is empty".to_string());
    }
    if !plugin_dir.is_absolute() {
        return Err(format!(
            "plugin directory must be absolute: {}",
            plugin_dir.display()
        ));
    }
    let name = plugin_dir
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or_default();
    if !name.eq_ignore_ascii_case("mystia-steward-companion") {
        return Err(format!(
            "refusing to replace unexpected plugin directory: {}",
            plugin_dir.display()
        ));
    }
    Ok(())
}

/// 在等待游戏和请求客户端退出之前验证自动更新的来源安装。
///
/// 远端清单协议只能阻止旧版重新发现新版；已缓存或已暂存的更新仍可能直接启动本程序。
/// 因而本地也必须拒绝没有完整 Flutter 清单的旧安装。这里不根据三个相同文件名猜测版本，
/// 也不向旧 Tauri 客户端发送新版退出请求。真正替换前的 preserve 校验继续保留，以覆盖等待
/// 期间安装树发生变化的情况；本次提前校验不能取代最后的事务准入。
fn validate_update_source(plugin_dir: &Path) -> Result<(), String> {
    validate_plugin_path(plugin_dir)?;
    package_manifest::validate(plugin_dir, true).map(|_| ()).map_err(|error| {
        format!(
            "当前插件目录不满足自动更新条件，未请求游戏或客户端退出、未替换插件。\
             从 v1.3.1 首次升级时，请退出游戏、旧客户端及更新程序，保留 BepInEx/config，\
             移出插件目录内自行保存的资料，然后删除 BepInEx/plugins/mystia-steward-companion，\
             再解压新版完整 Mod 包到该位置。已使用 Flutter 的安装请先核对程序包完整性。校验原因：{error}"
        )
    })
}

/// 校验暂存目录包含新版本运行所需的关键文件。
fn validate_staged_package(staged_dir: &Path) -> Result<(), String> {
    if !staged_dir.is_dir() {
        return Err(format!(
            "staged package directory does not exist: {}",
            staged_dir.display()
        ));
    }
    require_file(staged_dir, REQUIRED_DLL)?;
    require_file(staged_dir, REQUIRED_COMPANION_EXE)?;
    require_file(staged_dir, REQUIRED_UPDATER_EXE)?;
    validate_business_bundle(staged_dir)?;
    Ok(())
}

/// 新版完整清单委派给独立模块，确保测试与生产共用同一校验路径。
fn validate_business_bundle(root: &Path) -> Result<(), String> {
    package_manifest::validate(root, true).map(|_| ())
}

fn require_file(root: &Path, relative: &str) -> Result<(), String> {
    let path = root.join(relative);
    if path.is_file() {
        Ok(())
    } else {
        Err(format!(
            "staged package is missing {relative}: {}",
            path.display()
        ))
    }
}

fn prepare_parent(path: &Path) -> Result<(), String> {
    let Some(parent) = path.parent() else {
        return Err(format!("path has no parent: {}", path.display()));
    };
    fs::create_dir_all(parent)
        .map_err(|error| format!("failed to create {}: {error}", parent.display()))
}

/// 用暂存目录替换当前插件目录，安装后校验失败时尽量回滚旧目录。
///
/// 替换采用目录重命名而不是逐文件覆盖，减少部分文件成功、部分文件失败的窗口期。
/// 旧目录必须是通过新版清单验证的完整包；事务恢复只移动保留原件，不逐文件拼装版本。
fn replace_plugin_directory(
    plugin_dir: &Path,
    staged_dir: &Path,
    backup_dir: &Path,
    status_file: &Path,
    progress: &mut dyn FnMut(InstallProgress),
) -> Result<(), String> {
    replace_plugin_directory_using(
        plugin_dir,
        staged_dir,
        backup_dir,
        status_file,
        progress,
        || package_manifest::preserve(plugin_dir, staged_dir),
    )
}

/// 两种入口仅共用目录事务；旧包身份与附加数据准入由入口各自提供，普通更新器不接受旧版例外。
fn replace_plugin_directory_using(
    plugin_dir: &Path,
    staged_dir: &Path,
    backup_dir: &Path,
    status_file: &Path,
    progress: &mut dyn FnMut(InstallProgress),
    preserve: impl FnOnce() -> Result<(), String>,
) -> Result<(), String> {
    // 进入本事务时游戏已经退出。等待可能持续很久，不能复用等待前对暂存目录的校验结果；
    // 本次失败发生在任何旧目录移动之前，因此已安装版本完全不受影响。
    package_manifest::validate(staged_dir, false)?;
    if backup_dir.exists() {
        return Err(
            "backup directory already exists; inspect previous transaction before retry".into(),
        );
    }
    preserve()?;

    publish_progress(
        status_file,
        progress,
        InstallProgress::new("backing-up", "正在备份当前插件目录。", 55),
    );
    retry_rename(plugin_dir, backup_dir, Duration::from_secs(30))
        .map_err(|error| format!("failed to backup current plugin directory: {error}"))?;

    publish_progress(
        status_file,
        progress,
        InstallProgress::new("installing", "正在写入新版本插件目录。", 75),
    );
    if let Err(error) = retry_rename(staged_dir, plugin_dir, Duration::from_secs(30)) {
        let restore_result = if backup_dir.exists() {
            fs::rename(backup_dir, plugin_dir).map_err(|restore_error| {
                format!("restore failed after install error: {restore_error}")
            })
        } else {
            Ok(())
        };
        return Err(match restore_result {
            Ok(()) => {
                format!("failed to install staged package and restored previous version: {error}")
            }
            Err(restore_error) => {
                format!("failed to install staged package: {error}; {restore_error}")
            }
        });
    }

    publish_progress(
        status_file,
        progress,
        InstallProgress::new("verifying", "正在校验新版本文件。", 90),
    );
    if let Err(error) = validate_staged_package(plugin_dir) {
        publish_progress(
            status_file,
            progress,
            InstallProgress::new("rolling-back", "新版本校验失败，正在恢复原插件目录。", 92),
        );
        // 不删除失败的新包：原 staging 路径在成功重命名后为空，将完整失败目录移回此处供诊断。
        // 只有隔离成功才恢复旧目录，避免逐文件覆盖两个版本。任一步失败均明确保留旧备份位置。
        if let Err(isolation_error) = retry_rename(plugin_dir, staged_dir, Duration::from_secs(30))
        {
            return Err(format!(
                "installed package validation failed: {error}; failed to isolate rejected package: \
                 {isolation_error}; previous version remains at {}",
                backup_dir.display()
            ));
        }
        if let Err(restore_error) = retry_rename(backup_dir, plugin_dir, Duration::from_secs(30)) {
            return Err(format!(
                "installed package validation failed: {error}; rejected package retained at {}; \
                 failed to restore previous version from {}: {restore_error}",
                staged_dir.display(),
                backup_dir.display()
            ));
        }
        return Err(format!(
            "installed package validation failed and restored previous version: {error}; \
             rejected package retained at {}",
            staged_dir.display()
        ));
    }

    Ok(())
}

/// 带超时的重命名重试。
///
/// Windows 上刚退出的进程可能短时间内仍持有文件句柄；短重试能吸收这类正常延迟。
fn retry_rename(from: &Path, to: &Path, timeout: Duration) -> Result<(), String> {
    let started = Instant::now();
    let mut last_error = None;
    while started.elapsed() < timeout {
        match fs::rename(from, to) {
            Ok(()) => return Ok(()),
            Err(error) => {
                last_error = Some(error.to_string());
                thread::sleep(Duration::from_millis(500));
            }
        }
    }
    Err(last_error.unwrap_or_else(|| "unknown rename error".to_string()))
}

fn publish_progress(
    path: &Path,
    progress: &mut dyn FnMut(InstallProgress),
    event: InstallProgress,
) {
    write_status(path, event.state, &event.message, event.progress);
    progress(event);
}

/// 写入安装状态文件。
///
/// 状态文件是 updater 与下一次 Mod 启动之间的唯一通信方式，因此写入失败不再向外抛出，避免掩盖原始安装错误。
fn write_status(path: &Path, state: &str, message: &str, progress: u8) {
    // 每次写入重新检查既有祖先，避免安装过程中出现的链接把诊断文件导向其他目录。
    if !path.is_absolute() || offline_migration::ensure_no_links(path).is_err() {
        return;
    }
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let payload = format!(
        "{{\n  \"state\": \"{}\",\n  \"message\": \"{}\",\n  \"progress\": {}\n}}\n",
        escape_json(state),
        escape_json(message),
        progress.min(100)
    );
    let _ = fs::write(path, payload);
}

fn escape_json(value: &str) -> String {
    value
        .chars()
        .flat_map(|character| match character {
            '\\' => "\\\\".chars().collect::<Vec<_>>(),
            '"' => "\\\"".chars().collect::<Vec<_>>(),
            '\n' => "\\n".chars().collect::<Vec<_>>(),
            '\r' => "\\r".chars().collect::<Vec<_>>(),
            '\t' => "\\t".chars().collect::<Vec<_>>(),
            other => vec![other],
        })
        .collect()
}

#[cfg(any(
    target_os = "windows",
    all(target_os = "linux", feature = "updater-windows-ui-check")
))]
mod windows_updater_ui {
    use super::{
        parse_install_context, run_install, scale_logical_pixels, write_status, GameProcess,
        InstallContext, InstallControl, InstallOutcome, InstallProgress, CANCELLED_MESSAGE,
    };
    use crate::install_control::CancelDecision;
    use std::collections::HashMap;
    use std::iter;
    use std::mem::size_of;
    use std::path::PathBuf;
    use std::ptr;
    use std::sync::Arc;
    use std::thread;
    use windows_sys::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, RECT, WPARAM};
    use windows_sys::Win32::Graphics::Gdi::{
        CreateFontIndirectW, DeleteObject, UpdateWindow, COLOR_WINDOW, FW_BOLD, HBRUSH, HFONT,
        HGDIOBJ,
    };
    use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows_sys::Win32::UI::Controls::{
        InitCommonControlsEx, ICC_PROGRESS_CLASS, INITCOMMONCONTROLSEX, PBM_SETPOS, PBM_SETRANGE32,
        PBM_SETSTATE, PBST_ERROR, PBST_NORMAL, PBS_SMOOTH, PROGRESS_CLASSW,
    };
    use windows_sys::Win32::UI::HiDpi::{
        AdjustWindowRectExForDpi, AreDpiAwarenessContextsEqual, GetDpiForSystem, GetDpiForWindow,
        GetThreadDpiAwarenessContext, SetProcessDpiAwarenessContext, SystemParametersInfoForDpi,
        DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
    };
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{EnableWindow, GetFocus, SetFocus};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetClientRect,
        GetMessageW, GetWindowLongPtrW, IsDialogMessageW, LoadCursorW, LoadIconW, MoveWindow,
        PostMessageW, PostQuitMessage, RegisterClassW, SendMessageW, SetWindowLongPtrW,
        SetWindowPos, SetWindowTextW, ShowWindow, TranslateMessage, BS_PUSHBUTTON, CW_USEDEFAULT,
        GWLP_USERDATA, IDC_ARROW, IDI_APPLICATION, MSG, NONCLIENTMETRICSW, SPI_GETNONCLIENTMETRICS,
        SWP_NOACTIVATE, SWP_NOZORDER, SW_SHOW, WM_APP, WM_CLOSE, WM_COMMAND, WM_DESTROY,
        WM_DPICHANGED, WM_SETFONT, WM_SIZE, WNDCLASSW, WS_CAPTION, WS_CHILD, WS_MINIMIZEBOX,
        WS_OVERLAPPED, WS_SYSMENU, WS_TABSTOP, WS_VISIBLE,
    };

    const WINDOW_CLIENT_WIDTH: i32 = 680;
    const WINDOW_CLIENT_HEIGHT: i32 = 330;
    const START_BUTTON_ID: u16 = 1001;
    const CLOSE_BUTTON_ID: u16 = 1003;
    const WM_APP_PROGRESS: u32 = WM_APP + 1;
    const STATIC_LEFT: u32 = 0;
    const STATIC_RIGHT: u32 = 2;

    struct UiState {
        context: InstallContext,
        status_file: PathBuf,
        title_label: HWND,
        status_label: HWND,
        detail_label: HWND,
        progress_bar: HWND,
        progress_label: HWND,
        start_button: HWND,
        close_button: HWND,
        body_font: HFONT,
        title_font: HFONT,
        dpi: u32,
        worker_started: bool,
        install_finished: bool,
        game: Arc<GameProcess>,
        control: Arc<InstallControl>,
        worker: Option<thread::JoinHandle<()>>,
        progress: u8,
    }

    impl Drop for UiState {
        fn drop(&mut self) {
            // 异常退出消息循环也先取消可取消阶段并等待工作线程，防止窗口释放后继续替换。
            if let Some(worker) = self.worker.take() {
                let _ = self.control.cancel();
                let _ = worker.join();
            }
            unsafe {
                if !self.body_font.is_null() {
                    let _ = DeleteObject(self.body_font as HGDIOBJ);
                }
                if !self.title_font.is_null() {
                    let _ = DeleteObject(self.title_font as HGDIOBJ);
                }
            }
        }
    }

    struct UiMessage {
        progress: InstallProgress,
        finished: bool,
        success: bool,
    }

    pub fn run(args: HashMap<String, String>, status_file: PathBuf) -> Result<(), String> {
        let context = parse_install_context(&args)?;
        let class_name = wide("MystiaStewardCompanionUpdaterWindow");
        let title = wide("mystia-steward-companion 更新程序");

        unsafe {
            configure_per_monitor_dpi()?;
            initialize_common_controls()?;

            let instance = GetModuleHandleW(ptr::null());
            if instance.is_null() {
                return Err("get updater module handle failed".to_string());
            }
            // build.rs 通过 Windows SDK rc.exe 将 resources/app_icon.ico 以资源 ID 32512 嵌入本更新器。
            // IDI_APPLICATION 的数值同为 32512；必须传入当前模块实例，才能读取更新器自己的
            // 程序图标，而不是系统默认图标。LoadIconW 返回共享资源句柄，不应调用 DestroyIcon。
            let window_icon = LoadIconW(instance as HINSTANCE, IDI_APPLICATION);
            if window_icon.is_null() {
                return Err("load updater window icon failed".to_string());
            }
            let class = WNDCLASSW {
                style: 0,
                lpfnWndProc: Some(window_proc),
                cbClsExtra: 0,
                cbWndExtra: 0,
                hInstance: instance as HINSTANCE,
                hIcon: window_icon,
                hCursor: LoadCursorW(ptr::null_mut(), IDC_ARROW),
                hbrBackground: (COLOR_WINDOW + 1) as usize as HBRUSH,
                lpszMenuName: ptr::null(),
                lpszClassName: class_name.as_ptr(),
            };
            if RegisterClassW(&class) == 0 {
                return Err("register updater window class failed".to_string());
            }

            let initial_dpi = GetDpiForSystem();
            if initial_dpi == 0 {
                return Err("get updater system DPI failed".to_string());
            }
            let (window_width, window_height) = adjusted_window_size(initial_dpi)?;

            let hwnd = CreateWindowExW(
                0,
                class_name.as_ptr(),
                title.as_ptr(),
                WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                window_width,
                window_height,
                ptr::null_mut(),
                ptr::null_mut(),
                instance as HINSTANCE,
                ptr::null_mut(),
            );
            if hwnd.is_null() {
                return Err("create updater window failed".to_string());
            }

            let state = match build_state(hwnd, instance as HINSTANCE, context, status_file) {
                Ok(state) => Box::new(state),
                Err(error) => {
                    DestroyWindow(hwnd);
                    return Err(error);
                }
            };
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, Box::into_raw(state) as isize);
            if let Some(state) = state_mut(hwnd) {
                layout_controls(hwnd, state);
            }
            ShowWindow(hwnd, SW_SHOW);
            UpdateWindow(hwnd);

            let mut msg = MSG::default();
            loop {
                let result = GetMessageW(&mut msg, ptr::null_mut(), 0, 0);
                if result == 0 {
                    break;
                }
                if result < 0 {
                    return Err("updater window message loop failed".to_string());
                }
                // 原生 Tab / Shift+Tab / Enter / Escape 由对话框导航统一处理。
                if IsDialogMessageW(hwnd, &msg) == 0 {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }
        }

        Ok(())
    }

    unsafe fn build_state(
        hwnd: HWND,
        instance: HINSTANCE,
        context: InstallContext,
        status_file: PathBuf,
    ) -> Result<UiState, String> {
        let dpi = GetDpiForWindow(hwnd);
        if dpi == 0 {
            return Err("get updater window DPI failed".to_string());
        }
        // 在用户点击开始之前固定观察对象；观察失败不能伪装为游戏已退出。
        let game = Arc::new(GameProcess::open(context.game_pid)?);
        let game_running = game.is_running()?;
        let (body_font, title_font) = create_fonts(dpi)?;
        let status_text = if game_running {
            format!("检测到游戏进程 {} 正在运行。", context.game_pid)
        } else {
            "游戏进程已退出，可以开始安装。".to_string()
        };
        let detail_text = if game_running {
            "点击“等待退出并安装”后，请自行保存并退出游戏；等待期间可以取消安装。"
        } else {
            "点击“开始安装”后会关闭伴随窗口、备份旧版本并替换插件目录。"
        };

        let title_label = create_label(hwnd, instance, "自动更新已准备就绪", STATIC_LEFT);
        let status_label = create_label(hwnd, instance, &status_text, STATIC_LEFT);
        let detail_label = create_label(hwnd, instance, detail_text, STATIC_LEFT);
        let progress_bar = create_progress_bar(hwnd, instance);
        let progress_label = create_label(hwnd, instance, "0%", STATIC_RIGHT);
        let start_text = if game_running {
            "等待退出并安装"
        } else {
            "开始安装"
        };
        let start_button = create_button(hwnd, instance, START_BUTTON_ID, start_text);
        let close_button = create_button(hwnd, instance, CLOSE_BUTTON_ID, "取消");
        let controls = [
            title_label,
            status_label,
            detail_label,
            progress_bar,
            progress_label,
            start_button,
            close_button,
        ];
        if controls.iter().any(|control| control.is_null()) {
            let _ = DeleteObject(body_font as HGDIOBJ);
            let _ = DeleteObject(title_font as HGDIOBJ);
            return Err("create updater child control failed".to_string());
        }

        let _ = SendMessageW(progress_bar, PBM_SETRANGE32, 0, 100);
        let _ = SendMessageW(progress_bar, PBM_SETPOS, 0, 0);
        apply_fonts(&controls, title_label, body_font, title_font);
        SetFocus(start_button);

        Ok(UiState {
            context,
            status_file,
            title_label,
            status_label,
            detail_label,
            progress_bar,
            progress_label,
            start_button,
            close_button,
            body_font,
            title_font,
            dpi,
            worker_started: false,
            install_finished: false,
            game,
            control: Arc::new(InstallControl::default()),
            worker: None,
            progress: 0,
        })
    }

    unsafe extern "system" fn window_proc(
        hwnd: HWND,
        msg: u32,
        w_param: WPARAM,
        l_param: LPARAM,
    ) -> LRESULT {
        match msg {
            WM_COMMAND => {
                let id = (w_param & 0xffff) as u16;
                let state = state_mut(hwnd);
                if let Some(state) = state {
                    match id {
                        START_BUTTON_ID => start_worker(hwnd, state),
                        // IsDialogMessageW 的默认 Enter / Escape 使用 IDOK=1 / IDCANCEL=2。
                        1 => {
                            if GetFocus() == state.close_button || state.worker_started {
                                close_or_cancel(hwnd, state);
                            } else {
                                start_worker(hwnd, state);
                            }
                        }
                        CLOSE_BUTTON_ID | 2 => close_or_cancel(hwnd, state),
                        _ => {}
                    }
                }
                0
            }
            WM_SIZE => {
                if let Some(state) = state_mut(hwnd) {
                    layout_controls(hwnd, state);
                }
                0
            }
            WM_DPICHANGED => {
                let suggested = l_param as *const RECT;
                if !suggested.is_null() {
                    let suggested = &*suggested;
                    let _ = SetWindowPos(
                        hwnd,
                        ptr::null_mut(),
                        suggested.left,
                        suggested.top,
                        suggested.right - suggested.left,
                        suggested.bottom - suggested.top,
                        SWP_NOZORDER | SWP_NOACTIVATE,
                    );
                }
                if let Some(state) = state_mut(hwnd) {
                    let next_dpi = (w_param & 0xffff) as u32;
                    if let Err(error) = replace_fonts_for_dpi(state, next_dpi) {
                        set_text(
                            state.detail_label,
                            &format!("更新程序无法适配当前显示缩放：{error}"),
                        );
                    }
                    layout_controls(hwnd, state);
                }
                0
            }
            WM_APP_PROGRESS => {
                if l_param != 0 {
                    let message = Box::from_raw(l_param as *mut UiMessage);
                    if let Some(state) = state_mut(hwnd) {
                        apply_message(hwnd, state, &message);
                    }
                }
                0
            }
            WM_CLOSE => {
                if let Some(state) = state_mut(hwnd) {
                    close_or_cancel(hwnd, state);
                } else {
                    DestroyWindow(hwnd);
                }
                0
            }
            WM_DESTROY => {
                let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
                if ptr != 0 {
                    SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
                    drop(Box::from_raw(ptr as *mut UiState));
                }
                PostQuitMessage(0);
                0
            }
            _ => DefWindowProcW(hwnd, msg, w_param, l_param),
        }
    }

    unsafe fn start_worker(hwnd: HWND, state: &mut UiState) {
        if state.worker_started {
            return;
        }

        state.worker_started = true;
        set_text(state.title_label, "正在安装更新");
        set_text(
            state.detail_label,
            "请保存并退出游戏；准备与等待期间可以取消安装。",
        );
        EnableWindow(state.start_button, 0);
        set_text(state.close_button, "取消安装");
        SetFocus(state.close_button);

        let context = state.context.clone();
        let status_file = state.status_file.clone();
        let game = state.game.clone();
        let control = state.control.clone();
        let hwnd_value = hwnd as isize;
        state.worker = Some(thread::spawn(move || {
            let hwnd = hwnd_value as HWND;
            let mut post_progress = |progress: InstallProgress| unsafe {
                post_ui_message(hwnd, progress, false, false);
            };
            let result = run_install(
                &context,
                &status_file,
                game.as_ref(),
                control.as_ref(),
                &mut post_progress,
            );
            let (event, success) = match result {
                Ok(InstallOutcome::Succeeded) => (
                    InstallProgress::new("succeeded", "更新安装完成。请重新启动游戏。", 100),
                    true,
                ),
                Ok(InstallOutcome::Cancelled) => (
                    InstallProgress::new("cancelled", CANCELLED_MESSAGE, 0),
                    false,
                ),
                Err(error) => (
                    InstallProgress::new("failed", format!("更新安装失败：{error}"), 100),
                    false,
                ),
            };
            unsafe {
                post_ui_message(hwnd, event, true, success);
            }
        }));
    }

    unsafe fn apply_message(hwnd: HWND, state: &mut UiState, message: &UiMessage) {
        state.progress = message.progress.progress;
        set_text(state.status_label, &message.progress.message);
        set_text(
            state.progress_label,
            &format!("{}%", message.progress.progress),
        );
        let _ = SendMessageW(
            state.progress_bar,
            PBM_SETPOS,
            usize::from(message.progress.progress),
            0,
        );
        if matches!(
            message.progress.state,
            "installing" | "backing-up" | "verifying" | "rolling-back"
        ) {
            EnableWindow(state.close_button, 0);
            set_text(
                state.detail_label,
                "已开始替换插件文件，完成前不能取消。请不要手动修改插件目录。",
            );
        }
        if message.finished {
            // 最后消息入队不等于线程退出，join 后才允许关闭窗口。
            if let Some(worker) = state.worker.take() {
                let _ = worker.join();
            }
            state.install_finished = true;
            EnableWindow(state.close_button, 1);
            set_text(state.close_button, "关闭");
            set_text(
                state.title_label,
                if message.success {
                    "更新安装完成"
                } else if message.progress.state == "cancelled" {
                    "已取消安装"
                } else {
                    "更新安装失败"
                },
            );
            set_text(
                state.detail_label,
                if message.success {
                    "旧版本已备份，新版本已写入。关闭此窗口后重新启动游戏即可使用。"
                } else if message.progress.state == "cancelled" {
                    "插件文件保持原样；如客户端已退出，可再次打开客户端。"
                } else {
                    "旧版本目录会尽量保留或回滚。请查看更新状态文件或重新下载更新包后再试。"
                },
            );
            let _ = SendMessageW(
                state.progress_bar,
                PBM_SETSTATE,
                if message.success || message.progress.state == "cancelled" {
                    PBST_NORMAL as usize
                } else {
                    PBST_ERROR as usize
                },
                0,
            );
            SetFocus(state.close_button);
        }
        layout_controls(hwnd, state);
    }

    unsafe fn close_or_cancel(hwnd: HWND, state: &mut UiState) {
        if state.worker_started && !state.install_finished {
            match state.control.cancel() {
                Ok(CancelDecision::Accepted) => {
                    EnableWindow(state.close_button, 0);
                    set_text(state.detail_label, "正在取消安装，请等待工作线程确认。");
                }
                Ok(CancelDecision::TooLate) => {
                    set_text(state.detail_label, "已进入文件替换阶段，完成前不能取消。")
                }
                Ok(CancelDecision::Finished) => {
                    set_text(state.detail_label, "正在确认安装线程已结束。")
                }
                Err(error) => set_text(state.detail_label, &error),
            }
            return;
        }
        if !state.worker_started {
            write_status(
                &state.status_file,
                "cancelled",
                "用户关闭了更新程序，未安装更新。",
                state.progress,
            );
        }
        DestroyWindow(hwnd);
    }

    unsafe fn post_ui_message(
        hwnd: HWND,
        progress: InstallProgress,
        finished: bool,
        success: bool,
    ) {
        let message = Box::new(UiMessage {
            progress,
            finished,
            success,
        });
        let raw = Box::into_raw(message);
        if PostMessageW(hwnd, WM_APP_PROGRESS, 0, raw as LPARAM) == 0 {
            drop(Box::from_raw(raw));
        }
    }

    unsafe fn state_mut(hwnd: HWND) -> Option<&'static mut UiState> {
        let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        if ptr == 0 {
            None
        } else {
            Some(&mut *(ptr as *mut UiState))
        }
    }

    unsafe fn create_label(hwnd: HWND, instance: HINSTANCE, text: &str, static_style: u32) -> HWND {
        let class_name = wide("STATIC");
        let text = wide(text);
        CreateWindowExW(
            0,
            class_name.as_ptr(),
            text.as_ptr(),
            WS_CHILD | WS_VISIBLE | static_style,
            0,
            0,
            1,
            1,
            hwnd,
            ptr::null_mut(),
            instance,
            ptr::null_mut(),
        )
    }

    unsafe fn create_button(hwnd: HWND, instance: HINSTANCE, id: u16, text: &str) -> HWND {
        let class_name = wide("BUTTON");
        let text = wide(text);
        CreateWindowExW(
            0,
            class_name.as_ptr(),
            text.as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON as u32,
            0,
            0,
            1,
            1,
            hwnd,
            id as usize as _,
            instance,
            ptr::null_mut(),
        )
    }

    unsafe fn create_progress_bar(hwnd: HWND, instance: HINSTANCE) -> HWND {
        CreateWindowExW(
            0,
            PROGRESS_CLASSW,
            ptr::null(),
            WS_CHILD | WS_VISIBLE | PBS_SMOOTH,
            0,
            0,
            1,
            1,
            hwnd,
            ptr::null_mut(),
            instance,
            ptr::null_mut(),
        )
    }

    unsafe fn set_text(hwnd: HWND, text: &str) {
        let text = wide(text);
        let _ = SetWindowTextW(hwnd, text.as_ptr());
    }

    unsafe fn configure_per_monitor_dpi() -> Result<(), String> {
        let target = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2;
        if SetProcessDpiAwarenessContext(target) == 0
            && AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), target) == 0
        {
            return Err(
                "enable Per-Monitor DPI Awareness V2 failed; Windows 10 1703 or later is required"
                    .to_string(),
            );
        }
        if AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), target) == 0 {
            return Err("updater DPI awareness context verification failed".to_string());
        }
        Ok(())
    }

    unsafe fn initialize_common_controls() -> Result<(), String> {
        let controls = INITCOMMONCONTROLSEX {
            dwSize: size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_PROGRESS_CLASS,
        };
        if InitCommonControlsEx(&controls) == 0 {
            return Err("initialize updater progress control failed".to_string());
        }
        Ok(())
    }

    unsafe fn adjusted_window_size(dpi: u32) -> Result<(i32, i32), String> {
        let mut rect = RECT {
            left: 0,
            top: 0,
            right: scale_logical_pixels(WINDOW_CLIENT_WIDTH, dpi),
            bottom: scale_logical_pixels(WINDOW_CLIENT_HEIGHT, dpi),
        };
        let style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        if AdjustWindowRectExForDpi(&mut rect, style, 0, 0, dpi) == 0 {
            return Err("calculate updater DPI-aware window size failed".to_string());
        }
        Ok((rect.right - rect.left, rect.bottom - rect.top))
    }

    unsafe fn create_fonts(dpi: u32) -> Result<(HFONT, HFONT), String> {
        let mut metrics = NONCLIENTMETRICSW::default();
        metrics.cbSize = size_of::<NONCLIENTMETRICSW>() as u32;
        if SystemParametersInfoForDpi(
            SPI_GETNONCLIENTMETRICS,
            metrics.cbSize,
            &mut metrics as *mut NONCLIENTMETRICSW as *mut _,
            0,
            dpi,
        ) == 0
        {
            return Err("read DPI-aware Windows message font failed".to_string());
        }

        let body_font = CreateFontIndirectW(&metrics.lfMessageFont);
        if body_font.is_null() {
            return Err("create updater message font failed".to_string());
        }
        let mut title_log_font = metrics.lfMessageFont;
        title_log_font.lfWeight = FW_BOLD as i32;
        let title_font = CreateFontIndirectW(&title_log_font);
        if title_font.is_null() {
            let _ = DeleteObject(body_font as HGDIOBJ);
            return Err("create updater title font failed".to_string());
        }
        Ok((body_font, title_font))
    }

    unsafe fn apply_fonts(
        controls: &[HWND],
        title_label: HWND,
        body_font: HFONT,
        title_font: HFONT,
    ) {
        for &control in controls {
            let font = if control == title_label {
                title_font
            } else {
                body_font
            };
            let _ = SendMessageW(control, WM_SETFONT, font as usize, 1);
        }
    }

    unsafe fn replace_fonts_for_dpi(state: &mut UiState, dpi: u32) -> Result<(), String> {
        if dpi == 0 || dpi == state.dpi {
            return Ok(());
        }
        let (body_font, title_font) = create_fonts(dpi)?;
        let controls = [
            state.title_label,
            state.status_label,
            state.detail_label,
            state.progress_label,
            state.start_button,
            state.close_button,
        ];
        apply_fonts(&controls, state.title_label, body_font, title_font);
        let previous_body = std::mem::replace(&mut state.body_font, body_font);
        let previous_title = std::mem::replace(&mut state.title_font, title_font);
        state.dpi = dpi;
        let _ = DeleteObject(previous_body as HGDIOBJ);
        let _ = DeleteObject(previous_title as HGDIOBJ);
        Ok(())
    }

    unsafe fn layout_controls(hwnd: HWND, state: &UiState) {
        let mut client = RECT::default();
        if GetClientRect(hwnd, &mut client) == 0 {
            return;
        }
        let dpi = state.dpi;
        let width = client.right - client.left;
        let height = client.bottom - client.top;
        let margin = scale_logical_pixels(24, dpi);
        let content_width = (width - margin * 2).max(1);
        let title_height = scale_logical_pixels(28, dpi);
        let line_height = scale_logical_pixels(24, dpi);
        let detail_height = scale_logical_pixels(48, dpi);
        let progress_height = scale_logical_pixels(18, dpi);
        let button_height = scale_logical_pixels(34, dpi);
        let gap = scale_logical_pixels(10, dpi);

        move_control(
            state.title_label,
            margin,
            scale_logical_pixels(22, dpi),
            content_width,
            title_height,
        );
        move_control(
            state.status_label,
            margin,
            scale_logical_pixels(62, dpi),
            content_width,
            line_height,
        );
        move_control(
            state.detail_label,
            margin,
            scale_logical_pixels(92, dpi),
            content_width,
            detail_height,
        );
        move_control(
            state.progress_bar,
            margin,
            scale_logical_pixels(158, dpi),
            content_width,
            progress_height,
        );
        move_control(
            state.progress_label,
            margin,
            scale_logical_pixels(181, dpi),
            content_width,
            line_height,
        );

        let close_width = scale_logical_pixels(78, dpi);
        let start_width = scale_logical_pixels(146, dpi);
        let button_y = (height - margin - button_height).max(margin);
        let close_x = width - margin - close_width;
        let start_x = close_x - gap - start_width;
        move_control(
            state.start_button,
            start_x,
            button_y,
            start_width,
            button_height,
        );
        move_control(
            state.close_button,
            close_x,
            button_y,
            close_width,
            button_height,
        );
    }

    unsafe fn move_control(hwnd: HWND, x: i32, y: i32, width: i32, height: i32) {
        let _ = MoveWindow(hwnd, x, y, width.max(1), height.max(1), 1);
    }

    fn wide(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(iter::once(0)).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::scale_logical_pixels;

    /// 安装事务测试专用目录。所有组件都是普通文本，不加载 DLL、不启动程序，也不连接真实游戏。
    struct PackageTransactionFixture {
        root: std::path::PathBuf,
        plugin: std::path::PathBuf,
        staged: std::path::PathBuf,
        backup: std::path::PathBuf,
        status: std::path::PathBuf,
    }

    impl PackageTransactionFixture {
        fn new() -> Self {
            use super::*;
            // Windows 时间精度可能让并行测试取得相同时间戳；加入进程内原子序号隔离每个目录。
            static SEQUENCE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
            let sequence = SEQUENCE.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            let nonce = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let root = env::temp_dir().join(format!(
                "mystia-updater-transaction-{}-{nonce}-{sequence}",
                process::id()
            ));
            let fixture = Self {
                plugin: root.join("mystia-steward-companion"),
                staged: root.join("staged"),
                backup: root.join("backup"),
                status: root.join("status.json"),
                root,
            };
            package_manifest::fixture(&fixture.plugin);
            fs::create_dir_all(fixture.plugin.join("user-data")).unwrap();
            fs::write(
                fixture.plugin.join("user-data/old-plugin.txt"),
                "previous complete version",
            )
            .unwrap();
            package_manifest::fixture(&fixture.staged);
            fixture
        }

        fn assert_old_version_active(&self) {
            assert_eq!(
                std::fs::read_to_string(self.plugin.join("user-data/old-plugin.txt")).unwrap(),
                "previous complete version"
            );
            assert!(self.plugin.join("package-manifest.json").exists());
            assert!(self
                .plugin
                .join("MystiaStewardCompanion.Business.dll")
                .exists());
            assert!(!self.backup.exists());
        }
    }

    impl Drop for PackageTransactionFixture {
        fn drop(&mut self) {
            // root 是本测试新建的固定前缀唯一目录，只清理本 fixture，不接触任何游戏路径。
            let _ = std::fs::remove_dir_all(&self.root);
        }
    }

    /// 状态注入只影响观察结果，不持有任何真实游戏进程。
    struct ObservedGame(Result<bool, String>);
    impl super::ProcessObservation for ObservedGame {
        fn is_running(&self) -> Result<bool, String> {
            self.0.clone()
        }
    }

    impl PackageTransactionFixture {
        fn context(&self) -> super::InstallContext {
            super::InstallContext {
                game_pid: 17,
                plugin_dir: self.plugin.clone(),
                staged_dir: self.staged.clone(),
                backup_dir: self.backup.clone(),
                // 以下取消/观察失败测试必须在连接客户端之前返回；这个端口不会被访问。
                control_port: 1,
                wait_timeout: std::time::Duration::from_millis(10),
            }
        }
        fn assert_no_installation_writes(&self) {
            self.assert_old_version_active();
            super::package_manifest::validate(&self.staged, false).unwrap();
            assert!(!self.staged.join("user-data").exists());
        }
    }

    #[test]
    fn unsupported_installation_is_rejected_before_game_wait_or_client_request() {
        use super::*;
        // 旧三文件安装缺少完整清单；已损坏的新安装也不得进入等待及客户端控制阶段。
        // 两种情形都必须保留原树和暂存包，不能为了显示升级提示先触碰安装目录。
        for missing_manifest in [true, false] {
            let fixture = PackageTransactionFixture::new();
            if missing_manifest {
                // 收窄为公开旧包的三个文件名，不把“新包删一份清单”误称为旧三文件结构。
                // 内容仍是测试文本，不执行任何旧载荷；真正公开 ZIP 由独立手动升级脚本验证。
                for (name, path) in package_manifest::tree(&fixture.plugin).unwrap() {
                    if ![REQUIRED_DLL, REQUIRED_COMPANION_EXE, REQUIRED_UPDATER_EXE].contains(&name.as_str()) {
                        fs::remove_file(path).unwrap();
                    }
                }
                assert_eq!(package_manifest::tree(&fixture.plugin).unwrap().len(), 3);
            } else {
                fs::write(fixture.plugin.join(REQUIRED_DLL), "受控损坏组件").unwrap();
            }
            let snapshot = |root: &Path| -> std::collections::BTreeMap<String, Vec<u8>> {
                package_manifest::tree(root).unwrap().into_iter()
                    .map(|(name, path)| (name, fs::read(path).unwrap())).collect()
            };
            let previous = snapshot(&fixture.plugin);
            let staged = snapshot(&fixture.staged);
            struct NeverObserve;
            impl ProcessObservation for NeverObserve {
                fn is_running(&self) -> Result<bool, String> {
                    panic!("不兼容安装不应观察游戏进程");
                }
            }
            let mut states = Vec::new();
            let error = run_install(
                &fixture.context(), &fixture.status, &NeverObserve,
                &InstallControl::default(), &mut |event| states.push(event.state),
            ).unwrap_err();
            assert!(error.contains("从 v1.3.1 首次升级"), "{error}");
            assert_eq!(states, ["preparing", "failed"]);
            assert_eq!(snapshot(&fixture.plugin), previous);
            assert_eq!(snapshot(&fixture.staged), staged);
            assert!(!fixture.backup.exists());
        }
    }

    #[test]
    fn cancellation_during_preparation_or_wait_never_mutates_installation() {
        use super::*;
        for cancel_state in [
            "preparing",
            "waiting-game",
            "game-closed",
            "closing-companion",
        ] {
            let fixture = PackageTransactionFixture::new();
            let control = InstallControl::default();
            let game = ObservedGame(Ok(cancel_state == "waiting-game"));
            let mut terminal = Vec::new();
            let result = run_install(
                &fixture.context(),
                &fixture.status,
                &game,
                &control,
                &mut |event| {
                    if event.state == cancel_state {
                        assert_eq!(
                            control.cancel().unwrap(),
                            install_control::CancelDecision::Accepted
                        );
                    }
                    if matches!(event.state, "cancelled" | "succeeded" | "failed") {
                        terminal.push(event.state);
                    }
                },
            );
            assert_eq!(result.unwrap(), InstallOutcome::Cancelled, "{cancel_state}");
            assert_eq!(terminal, ["cancelled"]);
            fixture.assert_no_installation_writes();
            let state: serde_json::Value =
                serde_json::from_slice(&fs::read(&fixture.status).unwrap()).unwrap();
            assert_eq!(state["state"], "cancelled");
        }
    }

    #[test]
    fn process_observation_failure_and_timeout_do_not_admit_replacement() {
        use super::*;
        for game in [
            ObservedGame(Err("受控观察错误".into())),
            ObservedGame(Ok(true)),
        ] {
            let fixture = PackageTransactionFixture::new();
            let mut states = Vec::new();
            assert!(run_install(
                &fixture.context(),
                &fixture.status,
                &game,
                &InstallControl::default(),
                &mut |event| states.push(event.state)
            )
            .is_err());
            assert!(!states.contains(&"installing"));
            assert!(!states.contains(&"closing-companion"));
            assert_eq!(states.last(), Some(&"failed"));
            fixture.assert_no_installation_writes();
        }
    }

    #[test]
    fn package_transaction_rechecks_staging_before_moving_old_version() {
        use super::*;
        let fixture = PackageTransactionFixture::new();
        // 模拟等待游戏退出期间暂存组件发生变化；事务入口必须拒绝，旧目录不应被移动。
        validate_staged_package(&fixture.staged).unwrap();
        fs::write(
            fixture.staged.join("MystiaStewardCompanion.Business.dll"),
            "changed during wait",
        )
        .unwrap();
        let mut observed = Vec::new();
        let result = replace_plugin_directory(
            &fixture.plugin,
            &fixture.staged,
            &fixture.backup,
            &fixture.status,
            &mut |event| observed.push(event.state),
        );
        assert!(result.unwrap_err().contains("hash mismatch"));
        assert!(
            observed.is_empty(),
            "validation must precede all backup/install progress"
        );
        fixture.assert_old_version_active();
        assert!(fixture.staged.exists());
    }

    #[test]
    fn package_transaction_restores_legacy_package_after_final_hash_failure() {
        use super::*;
        let fixture = PackageTransactionFixture::new();
        let mut observed = Vec::new();
        let result = replace_plugin_directory(
            &fixture.plugin,
            &fixture.staged,
            &fixture.backup,
            &fixture.status,
            &mut |event| {
                // 使用真实生产进度回调在重命名完成后注入损坏，覆盖安装后校验的失败路径。
                if event.state == "verifying" {
                    fs::write(
                        fixture.plugin.join("MystiaStewardCompanion.Business.dll"),
                        "post-move corruption",
                    )
                    .unwrap();
                }
                observed.push(event.state);
            },
        );
        let error = result.unwrap_err();
        assert!(
            error.contains("restored previous version"),
            "unexpected rollback failure: {error}"
        );
        assert!(observed.contains(&"rolling-back"));
        fixture.assert_old_version_active();
        assert_eq!(
            fs::read_to_string(fixture.staged.join("MystiaStewardCompanion.Business.dll")).unwrap(),
            "post-move corruption",
            "the rejected new package must remain available for diagnosis"
        );
    }

    #[test]
    fn package_transaction_keeps_legacy_backup_after_success() {
        use super::*;
        let fixture = PackageTransactionFixture::new();
        replace_plugin_directory(
            &fixture.plugin,
            &fixture.staged,
            &fixture.backup,
            &fixture.status,
            &mut |_| {},
        )
        .unwrap();
        validate_staged_package(&fixture.plugin).unwrap();
        assert!(!fixture.staged.exists());
        assert!(fixture.plugin.join("user-data/old-plugin.txt").exists());
        assert_eq!(
            fs::read_to_string(fixture.backup.join("user-data/old-plugin.txt")).unwrap(),
            "previous complete version"
        );
        assert!(fixture.backup.join("package-manifest.json").exists());
    }

    /// 真正调用安装前校验器，覆盖完整包、组件混版、缺失与重复清单；全部文件位于独立临时目录。
    #[test]
    fn business_bundle_rejects_missing_mixed_and_duplicate_components() {
        use super::*;
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root =
            env::temp_dir().join(format!("mystia-business-bundle-{}-{nonce}", process::id()));
        fs::create_dir_all(root.join("companion")).unwrap();
        let files = package_manifest::REQUIRED;
        let reset = || package_manifest::fixture(&root);
        reset();
        assert!(validate_staged_package(&root).is_ok());
        for file in files {
            reset();
            fs::remove_file(root.join(file)).unwrap();
            assert!(validate_staged_package(&root).is_err(), "missing {file}");
            reset();
            fs::write(root.join(file), "另一轮构建").unwrap();
            assert!(validate_staged_package(&root).is_err(), "mixed {file}");
        }
        reset();
        let mut manifest: serde_json::Value =
            serde_json::from_slice(&fs::read(root.join("package-manifest.json")).unwrap()).unwrap();
        manifest["files"][1] = manifest["files"][0].clone();
        fs::write(
            root.join("package-manifest.json"),
            serde_json::to_vec(&manifest).unwrap(),
        )
        .unwrap();
        assert!(validate_staged_package(&root).is_err());
        // root 是当前测试独有、固定前缀的临时目录，清理不接触真实插件文件。
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn logical_pixels_scale_for_supported_dpi_steps() {
        let fixtures = [
            (96, 24, 24),
            (120, 24, 30),
            (144, 24, 36),
            (192, 24, 48),
            (120, 680, 850),
            (144, 330, 495),
        ];
        for (dpi, logical, expected) in fixtures {
            assert_eq!(scale_logical_pixels(logical, dpi), expected);
        }
    }

    #[test]
    fn logical_pixel_rounding_is_symmetric() {
        for dpi in [96, 120, 144, 192] {
            assert_eq!(
                scale_logical_pixels(-17, dpi),
                -scale_logical_pixels(17, dpi)
            );
        }
    }
}
