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

use std::collections::HashMap;
use std::env;
use std::fs;
#[cfg(any(target_os = "windows", feature = "updater-windows-ui-check", test))]
use std::io::Read;
use std::io::Write;
use std::net::TcpStream;
use std::path::{Path, PathBuf};
use std::process;
use std::sync::atomic::{AtomicU64, Ordering};
use std::thread;
use std::time::{Duration, Instant};

const DEFAULT_CONTROL_PORT: u16 = 32146;
const DEFAULT_WAIT_TIMEOUT_SECONDS: u64 = 1800;
const REQUIRED_DLL: &str = "MystiaStewardCompanion.BepInEx.dll";
const REQUIRED_COMPANION_EXE: &str = "companion/mystia-steward-companion.exe";
const REQUIRED_UPDATER_EXE: &str = "mystia-steward-companion-updater.exe";
#[cfg(any(target_os = "windows", feature = "updater-windows-ui-check", test))]
const TARGET_VERSION: &str = env!("CARGO_PKG_VERSION");
const CANCELLED_MESSAGE: &str = "已取消后续安装，尚未替换插件文件。已关闭的伴随窗口不会自动恢复。";

#[path = "updater/control.rs"]
mod install_control;
use install_control::InstallControl;
#[cfg(any(
    target_os = "windows",
    all(target_os = "linux", feature = "updater-windows-ui-check")
))]
#[path = "updater/process.rs"]
mod game_process;
#[cfg(any(
    target_os = "windows",
    all(target_os = "linux", feature = "updater-windows-ui-check")
))]
use game_process::GameProcess;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum InstallOutcome {
    Succeeded,
    Cancelled,
}

trait ProcessObservation {
    fn is_running(&self) -> Result<bool, String>;
}

#[cfg(any(
    target_os = "windows",
    all(target_os = "linux", feature = "updater-windows-ui-check")
))]
impl ProcessObservation for GameProcess {
    fn is_running(&self) -> Result<bool, String> {
        self.is_running()
    }
}

#[cfg(not(any(target_os = "windows", feature = "updater-windows-ui-check")))]
struct GameProcess {
    pid: u32,
}

#[cfg(not(any(target_os = "windows", feature = "updater-windows-ui-check")))]
impl GameProcess {
    fn open(pid: u32) -> Result<Self, String> {
        if pid == 0 {
            return Err("invalid game PID".to_string());
        }
        Ok(Self { pid })
    }
}

#[cfg(not(any(target_os = "windows", feature = "updater-windows-ui-check")))]
impl ProcessObservation for GameProcess {
    fn is_running(&self) -> Result<bool, String> {
        match fs::metadata(Path::new("/proc").join(self.pid.to_string())) {
            Ok(_) => Ok(true),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(false),
            Err(error) => Err(format!("game process state unavailable: {error}")),
        }
    }
}
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
    let args = parse_args(env::args().skip(1).collect());
    let status_file =
        get_path(&args, "status-file").unwrap_or_else(|| PathBuf::from("update-status.json"));
    #[cfg(target_os = "windows")]
    let result = windows_updater_ui::run(args, status_file.clone());
    #[cfg(not(target_os = "windows"))]
    let result = run_silent(&args, &status_file);

    if let Err(error) = result {
        if let Err(status_error) = write_status(&status_file, "failed", &error, 100) {
            eprintln!("{status_error}");
        }
        #[cfg(target_os = "windows")]
        windows_updater_ui::show_startup_error(&error);
        eprintln!("{error}");
        process::exit(1);
    }
}

#[cfg(not(target_os = "windows"))]
fn run_silent(args: &HashMap<String, String>, status_file: &Path) -> Result<(), String> {
    let context = parse_install_context(args)?;
    let game = GameProcess::open(context.game_pid)?;
    let mut ignore_progress = |_progress: InstallProgress| {};
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
/// zip 路径安全检查和 SHA256 校验。本函数仍会重新检查最小文件集合，防止暂存目录被外部修改。
fn run_install(
    context: &InstallContext,
    status_file: &Path,
    game: &impl ProcessObservation,
    control: &InstallControl,
    progress: &mut dyn FnMut(InstallProgress),
) -> Result<InstallOutcome, String> {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        run_install_steps(context, status_file, game, control, progress)
    }))
    .unwrap_or_else(|_| Err("安装线程异常终止。请检查插件目录与备份目录后再操作。".to_string()));
    let event = match &result {
        Ok(InstallOutcome::Succeeded) => {
            InstallProgress::new("succeeded", "更新安装完成。请重新启动游戏。", 100)
        }
        Ok(InstallOutcome::Cancelled) => InstallProgress::new("cancelled", CANCELLED_MESSAGE, 0),
        Err(error) => InstallProgress::new("failed", error, 100),
    };
    let status_result = publish_progress(status_file, progress, event);
    control.finish()?;
    match (result, status_result) {
        (Err(error), Err(status_error)) => Err(format!("{error}\n{status_error}")),
        (_, Err(status_error)) => Err(status_error),
        (result, Ok(())) => result,
    }
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
        InstallProgress::new("preparing", "正在准备更新安装器。", 5),
    )?;
    validate_plugin_path(&context.plugin_dir)?;
    validate_staged_package(&context.staged_dir)?;
    prepare_parent(&context.backup_dir)?;
    if control.cancelled()? {
        return Ok(InstallOutcome::Cancelled);
    }

    publish_progress(
        status_file,
        progress,
        InstallProgress::new("closing-companion", "正在关闭伴随窗口以释放程序文件。", 12),
    )?;
    notify_companion_exit(context.control_port);
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
    notify_companion_exit(context.control_port);
    // This is the only admission to writes affecting the installed plugin tree.
    if !control.begin_replacement()? {
        return Ok(InstallOutcome::Cancelled);
    }

    publish_progress(
        status_file,
        progress,
        InstallProgress::new("installing", "正在替换插件文件。", 45),
    )?;
    replace_plugin_directory(
        &context.plugin_dir,
        &context.staged_dir,
        &context.backup_dir,
        status_file,
        progress,
    )?;
    publish_progress(
        status_file,
        progress,
        InstallProgress::new("verifying", "正在校验新版本文件。", 90),
    )?;
    validate_staged_package(&context.plugin_dir)?;

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

/// 通过伴随窗口控制端口请求退出。
///
/// 发送失败会被忽略，因为伴随窗口可能已经退出；真正的安装安全性由等待游戏进程结束和文件重命名重试保证。
fn notify_companion_exit(control_port: u16) {
    if let Ok(mut stream) = TcpStream::connect(("127.0.0.1", control_port)) {
        let _ = stream.write_all(b"mystia-steward-companion:exit\n");
        let _ = stream.flush();
    }
}

/// 等待游戏进程退出。
///
/// BepInEx DLL 被游戏进程加载，游戏未退出时替换插件目录可能失败或留下半更新状态。
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
                InstallProgress::new("game-closed", "已检测到游戏进程退出。", 35),
            )?;
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
                        "请手动退出游戏，正在等待进程 {pid} 结束，已等待 {} 秒。",
                        elapsed.as_secs()
                    ),
                    25,
                ),
            )?;
            next_report = elapsed + Duration::from_secs(2);
        }
        control.wait(Duration::from_millis(250))?;
    }
    Err(format!("timed out waiting for game process {pid} to exit"))
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
    Ok(())
}

#[cfg(any(target_os = "windows", feature = "updater-windows-ui-check"))]
fn verify_updater_package_binding(staged_dir: &Path) -> Result<(), String> {
    let runner = env::current_exe()
        .map_err(|error| format!("read updater executable path failed: {error}"))?;
    verify_identical_files(&runner, &staged_dir.join(REQUIRED_UPDATER_EXE))
}

#[cfg(any(target_os = "windows", feature = "updater-windows-ui-check", test))]
fn verify_identical_files(runner: &Path, packaged: &Path) -> Result<(), String> {
    let mut runner = fs::File::open(runner)
        .map_err(|error| format!("read updater executable failed: {error}"))?;
    let mut packaged = fs::File::open(packaged)
        .map_err(|error| format!("read packaged updater failed: {error}"))?;
    let mut left = [0u8; 8192];
    let mut right = [0u8; 8192];
    loop {
        let left_count = runner
            .read(&mut left)
            .map_err(|error| format!("read updater executable failed: {error}"))?;
        if left_count == 0 {
            if packaged
                .read(&mut right[..1])
                .map_err(|error| format!("read packaged updater failed: {error}"))?
                != 0
            {
                return Err("更新程序与暂存包不一致，无法确认目标版本。".to_string());
            }
            return Ok(());
        }
        packaged
            .read_exact(&mut right[..left_count])
            .map_err(|error| format!("更新程序与暂存包不一致或读取失败：{error}"))?;
        if left[..left_count] != right[..left_count] {
            return Err("更新程序与暂存包不一致，无法确认目标版本。".to_string());
        }
    }
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

/// 用暂存目录替换当前插件目录，失败时尽量回滚旧目录。
///
/// 替换采用目录重命名而不是逐文件覆盖，减少部分文件成功、部分文件失败的窗口期。
fn replace_plugin_directory(
    plugin_dir: &Path,
    staged_dir: &Path,
    backup_dir: &Path,
    status_file: &Path,
    progress: &mut dyn FnMut(InstallProgress),
) -> Result<(), String> {
    if backup_dir.exists() {
        return Err(format!(
            "backup directory already exists; it was not changed: {}",
            backup_dir.display()
        ));
    }

    publish_progress(
        status_file,
        progress,
        InstallProgress::new("backing-up", "正在备份当前插件目录。", 55),
    )?;
    retry_rename(plugin_dir, backup_dir, Duration::from_secs(30))
        .map_err(|error| format!("failed to backup current plugin directory: {error}"))?;

    let install_result = publish_progress(
        status_file,
        progress,
        InstallProgress::new("installing", "正在写入新版本插件目录。", 75),
    )
    .and_then(|()| retry_rename(staged_dir, plugin_dir, Duration::from_secs(30)));
    if let Err(error) = install_result {
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
) -> Result<(), String> {
    write_status(path, event.state, &event.message, event.progress)?;
    progress(event);
    Ok(())
}

/// 写入安装状态文件。
///
/// 通过自有临时文件和原子 rename 发布完整状态。写入失败必须进入可展示错误，不能被误报成功。
fn write_status(path: &Path, state: &str, message: &str, progress: u8) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)
            .map_err(|error| format!("create status directory failed: {error}"))?;
    }
    let payload = serde_json::to_vec(&serde_json::json!({
        "state": state, "message": message, "progress": progress.min(100),
    }))
    .map_err(|error| format!("serialize update status failed: {error}"))?;
    static NEXT_STATUS_FILE: AtomicU64 = AtomicU64::new(0);
    let temporary = path.with_extension(format!(
        "status-{}-{}.tmp",
        process::id(),
        NEXT_STATUS_FILE.fetch_add(1, Ordering::Relaxed)
    ));
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(|error| format!("create update status file failed: {error}"))?;
    let result = file.write_all(&payload).and_then(|()| file.sync_all());
    drop(file);
    let result = result.and_then(|()| fs::rename(&temporary, path));
    if result.is_err() {
        // Only the file successfully created by this call is eligible for cleanup.
        if let Err(error) = fs::remove_file(&temporary) {
            eprintln!("update status temporary cleanup failed: {error}");
        }
    }
    result.map_err(|error| format!("write update status failed ({}): {error}", path.display()))
}

#[cfg(any(
    target_os = "windows",
    all(target_os = "linux", feature = "updater-windows-ui-check")
))]
mod windows_updater_ui {
    use super::install_control::CancelDecision;
    use super::{
        parse_install_context, run_install, scale_logical_pixels, verify_updater_package_binding,
        write_status, GameProcess, InstallContext, InstallControl, InstallOutcome, InstallProgress,
        CANCELLED_MESSAGE, TARGET_VERSION,
    };
    use std::collections::HashMap;
    use std::iter;
    use std::mem::size_of;
    use std::path::PathBuf;
    use std::ptr;
    use std::sync::{mpsc, Arc};
    use std::thread;
    use windows_sys::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, RECT, WPARAM};
    use windows_sys::Win32::Graphics::Gdi::{
        CreateFontIndirectW, DeleteObject, UpdateWindow, COLOR_WINDOW, FW_BOLD, HBRUSH, HFONT,
        HGDIOBJ,
    };
    use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows_sys::Win32::UI::Controls::{
        InitCommonControlsEx, EM_SETSEL, ICC_PROGRESS_CLASS, INITCOMMONCONTROLSEX, PBM_SETPOS,
        PBM_SETRANGE32, PBM_SETSTATE, PBST_ERROR, PBST_NORMAL, PBS_SMOOTH, PROGRESS_CLASSW,
    };
    use windows_sys::Win32::UI::HiDpi::{
        AdjustWindowRectExForDpi, AreDpiAwarenessContextsEqual, GetDpiForSystem, GetDpiForWindow,
        GetThreadDpiAwarenessContext, SetProcessDpiAwarenessContext, SystemParametersInfoForDpi,
        DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
    };
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        EnableWindow, GetFocus, IsWindowEnabled, SetFocus,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetClientRect,
        GetMessageW, GetWindowLongPtrW, IsDialogMessageW, LoadCursorW, MessageBoxW, MoveWindow,
        PostMessageW, PostQuitMessage, RegisterClassW, SendMessageW, SetWindowLongPtrW,
        SetWindowPos, SetWindowTextW, ShowWindow, TranslateMessage, BS_PUSHBUTTON, CW_USEDEFAULT,
        ES_AUTOVSCROLL, ES_MULTILINE, ES_READONLY, GWLP_USERDATA, IDC_ARROW, MB_ICONERROR, MB_OK,
        MSG, NONCLIENTMETRICSW, SPI_GETNONCLIENTMETRICS, SWP_NOACTIVATE, SWP_NOZORDER, SW_SHOW,
        WM_APP, WM_CLOSE, WM_COMMAND, WM_COPY, WM_DESTROY, WM_DPICHANGED, WM_SETFONT, WM_SIZE,
        WNDCLASSW, WS_BORDER, WS_CAPTION, WS_CHILD, WS_MINIMIZEBOX, WS_OVERLAPPED, WS_SYSMENU,
        WS_TABSTOP, WS_VISIBLE, WS_VSCROLL,
    };

    const WINDOW_CLIENT_WIDTH: i32 = 680;
    const WINDOW_CLIENT_HEIGHT: i32 = 480;
    const START_BUTTON_ID: u16 = 1001;
    const CLOSE_BUTTON_ID: u16 = 1003;
    const COPY_BUTTON_ID: u16 = 1004;
    const WM_APP_PROGRESS: u32 = WM_APP + 1;
    const STATIC_LEFT: u32 = 0;
    const STATIC_RIGHT: u32 = 2;

    struct UiState {
        context: InstallContext,
        status_file: PathBuf,
        title_label: HWND,
        status_label: HWND,
        detail_label: HWND,
        details_heading: HWND,
        details_edit: HWND,
        progress_bar: HWND,
        progress_label: HWND,
        start_button: HWND,
        close_button: HWND,
        copy_button: HWND,
        body_font: HFONT,
        title_font: HFONT,
        dpi: u32,
        worker_started: bool,
        install_finished: bool,
        progress: u8,
        game: Arc<GameProcess>,
        control: Arc<InstallControl>,
        worker: Option<thread::JoinHandle<()>>,
        updates: mpsc::Receiver<UiMessage>,
        update_sender: mpsc::Sender<UiMessage>,
        last_error: Option<String>,
    }

    impl Drop for UiState {
        fn drop(&mut self) {
            if let Some(worker) = self.worker.take() {
                let _ = self.control.cancel();
                if worker.join().is_err() {
                    eprintln!("updater worker terminated unexpectedly");
                }
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
        verify_updater_package_binding(&context.staged_dir)?;
        let class_name = wide("MystiaStewardCompanionUpdaterWindow");
        let title = wide("mystia-steward-companion 更新程序");

        unsafe {
            configure_per_monitor_dpi()?;
            initialize_common_controls()?;

            let instance = GetModuleHandleW(ptr::null());
            if instance.is_null() {
                return Err("get updater module handle failed".to_string());
            }
            let class = WNDCLASSW {
                style: 0,
                lpfnWndProc: Some(window_proc),
                cbClsExtra: 0,
                cbWndExtra: 0,
                hInstance: instance as HINSTANCE,
                hIcon: ptr::null_mut(),
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
            if let Some(state) = state_mut(hwnd) {
                SetFocus(state.start_button);
            }

            let mut msg = MSG::default();
            loop {
                let result = GetMessageW(&mut msg, ptr::null_mut(), 0, 0);
                if result == 0 {
                    break;
                }
                if result < 0 {
                    DestroyWindow(hwnd);
                    return Err("updater window message loop failed".to_string());
                }
                if IsDialogMessageW(hwnd, &msg) == 0 {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }
        }

        Ok(())
    }

    pub fn show_startup_error(error: &str) {
        unsafe {
            if let Err(dpi_error) = configure_per_monitor_dpi() {
                eprintln!("cannot show updater startup error: {dpi_error}");
                return;
            }
            let text = wide(&format!("更新程序未能启动：\n{error}"));
            let title = wide("mystia-steward-companion 更新程序");
            MessageBoxW(
                ptr::null_mut(),
                text.as_ptr(),
                title.as_ptr(),
                MB_OK | MB_ICONERROR,
            );
        }
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
        let game = Arc::new(GameProcess::open(context.game_pid)?);
        let game_running = game.is_running()?;
        let (body_font, title_font) = create_fonts(dpi)?;
        let status_text = if game_running {
            format!("检测到游戏进程 {} 正在运行。", context.game_pid)
        } else {
            "游戏进程已退出，可以开始安装。".to_string()
        };
        let detail_text = if game_running {
            "请先保存进度并手动退出游戏。开始等待后，可在替换文件前取消安装。"
        } else {
            "点击“开始安装”后会关闭伴随窗口、备份旧版本并替换插件目录。"
        };

        let title_label = create_label(
            hwnd,
            instance,
            &format!("准备安装 v{TARGET_VERSION}"),
            STATIC_LEFT,
        );
        let status_label = create_label(hwnd, instance, &status_text, STATIC_LEFT);
        let detail_label = create_label(hwnd, instance, detail_text, STATIC_LEFT);
        let progress_bar = create_progress_bar(hwnd, instance);
        let progress_label = create_label(hwnd, instance, "0%", STATIC_RIGHT);
        let start_text = if game_running {
            "等待游戏退出并安装"
        } else {
            "开始安装"
        };
        let start_button = create_button(hwnd, instance, START_BUTTON_ID, start_text);
        let close_button = create_button(hwnd, instance, CLOSE_BUTTON_ID, "取消");
        let details_heading = create_label(hwnd, instance, "详细信息（可选择并复制）", STATIC_LEFT);
        let details_edit = create_details_edit(hwnd, instance);
        let copy_button = create_button(hwnd, instance, COPY_BUTTON_ID, "复制详情");
        let controls = [
            title_label,
            status_label,
            detail_label,
            progress_bar,
            progress_label,
            start_button,
            close_button,
            details_heading,
            details_edit,
            copy_button,
        ];
        if controls.iter().any(|control| control.is_null()) {
            let _ = DeleteObject(body_font as HGDIOBJ);
            let _ = DeleteObject(title_font as HGDIOBJ);
            return Err("create updater child control failed".to_string());
        }

        let _ = SendMessageW(progress_bar, PBM_SETRANGE32, 0, 100);
        let _ = SendMessageW(progress_bar, PBM_SETPOS, 0, 0);
        apply_fonts(&controls, title_label, body_font, title_font);
        set_text(
            details_edit,
            &format!(
                "目标版本：v{TARGET_VERSION}\r\n安装位置：{}\r\n状态文件：{}",
                context.plugin_dir.display(),
                status_file.display()
            ),
        );
        let (update_sender, updates) = mpsc::channel();

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
            details_heading,
            details_edit,
            copy_button,
            body_font,
            title_font,
            dpi,
            worker_started: false,
            install_finished: false,
            progress: 0,
            game,
            control: Arc::new(InstallControl::default()),
            worker: None,
            updates,
            update_sender,
            last_error: None,
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
                let mut id = (w_param & 0xffff) as u16;
                let state = state_mut(hwnd);
                if let Some(state) = state {
                    if id == 1 {
                        let focused = GetFocus();
                        id = if focused == state.copy_button {
                            COPY_BUTTON_ID
                        } else if focused == state.close_button || state.worker_started {
                            CLOSE_BUTTON_ID
                        } else {
                            START_BUTTON_ID
                        };
                    } else if id == 2 {
                        id = CLOSE_BUTTON_ID;
                    }
                    match id {
                        START_BUTTON_ID if IsWindowEnabled(state.start_button) != 0 => {
                            start_worker(hwnd, state)
                        }
                        CLOSE_BUTTON_ID => close_or_cancel(hwnd, state),
                        COPY_BUTTON_ID => {
                            SendMessageW(state.details_edit, EM_SETSEL, 0, -1);
                            SendMessageW(state.details_edit, WM_COPY, 0, 0);
                            SetFocus(state.details_edit);
                        }
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
                if let Some(state) = state_mut(hwnd) {
                    drain_updates(hwnd, state);
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
        set_text(state.title_label, &format!("正在安装 v{TARGET_VERSION}"));
        set_text(
            state.detail_label,
            "请手动退出游戏；等待期间可以取消后续安装。",
        );
        EnableWindow(state.start_button, 0);
        set_text(state.close_button, "取消安装");
        SetFocus(state.close_button);

        let context = state.context.clone();
        let status_file = state.status_file.clone();
        let game = state.game.clone();
        let control = state.control.clone();
        let sender = state.update_sender.clone();
        let hwnd_value = hwnd as isize;
        state.worker = Some(thread::spawn(move || {
            let hwnd = hwnd_value as HWND;
            let mut post_progress = |progress: InstallProgress| unsafe {
                post_ui_message(hwnd, &sender, progress, false, false);
            };
            let result = run_install(
                &context,
                &status_file,
                game.as_ref(),
                control.as_ref(),
                &mut post_progress,
            );
            match result {
                Ok(outcome) => unsafe {
                    let event = match outcome {
                        InstallOutcome::Succeeded => {
                            InstallProgress::new("succeeded", "更新安装完成。请重新启动游戏。", 100)
                        }
                        InstallOutcome::Cancelled => {
                            InstallProgress::new("cancelled", CANCELLED_MESSAGE, 0)
                        }
                    };
                    post_ui_message(
                        hwnd,
                        &sender,
                        event,
                        true,
                        outcome == InstallOutcome::Succeeded,
                    );
                },
                Err(error) => unsafe {
                    post_ui_message(
                        hwnd,
                        &sender,
                        InstallProgress::new("failed", format!("更新安装失败：{error}"), 100),
                        true,
                        false,
                    );
                },
            }
        }));
    }

    unsafe fn apply_message(hwnd: HWND, state: &mut UiState, message: &UiMessage) {
        state.progress = message.progress.progress;
        let summary = match message.progress.state {
            "preparing" => "正在准备安装。",
            "closing-companion" => "正在关闭伴随窗口。",
            "waiting-game" => "正在等待游戏退出。",
            "game-closed" => "已确认游戏退出。",
            "backing-up" => "正在备份当前插件。",
            "installing" => "正在替换插件文件。",
            "verifying" => "正在检查安装结果。",
            "succeeded" => "更新安装完成。",
            "cancelled" => "已取消后续安装。",
            _ => "安装未完成，请查看详细信息。",
        };
        set_text(state.status_label, summary);
        if message.progress.state == "failed" {
            state.last_error = Some(message.progress.message.clone());
        }
        let details = if let Some(error) = &state.last_error {
            format!(
                "目标版本：v{TARGET_VERSION}\r\n{}\r\n\r\n最近错误：\r\n{}\r\n\r\n状态文件：{}",
                message.progress.message,
                error,
                state.status_file.display()
            )
        } else {
            format!(
                "目标版本：v{TARGET_VERSION}\r\n{}\r\n\r\n安装位置：{}\r\n状态文件：{}",
                message.progress.message,
                state.context.plugin_dir.display(),
                state.status_file.display()
            )
        };
        // Reading/copying diagnostics must not lose the selection on each waiting update.
        if GetFocus() != state.details_edit || message.finished {
            set_text(
                state.details_edit,
                &details.replace('\n', "\r\n").replace("\r\r\n", "\r\n"),
            );
        }
        if matches!(
            message.progress.state,
            "backing-up" | "installing" | "verifying"
        ) {
            if GetFocus() == state.close_button {
                SetFocus(state.copy_button);
            }
            EnableWindow(state.close_button, 0);
            set_text(
                state.detail_label,
                "已开始替换插件文件，完成前不能取消。请不要手动修改插件目录。",
            );
        }
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
        if message.finished {
            if let Some(worker) = state.worker.take() {
                if worker.join().is_err() {
                    show_error(state, "安装线程异常终止。请检查插件与备份目录。");
                }
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
                    "尚未替换插件文件；已关闭的伴随窗口不会自动恢复。"
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
        drain_updates(hwnd, state);
        if state.worker_started && !state.install_finished {
            match state.control.cancel() {
                Ok(CancelDecision::Accepted) => {
                    EnableWindow(state.close_button, 0);
                    SetFocus(state.copy_button);
                    set_text(state.detail_label, "正在取消后续安装，请等待安装线程确认。");
                }
                Ok(CancelDecision::TooLate) => {
                    set_text(state.detail_label, "已进入文件替换阶段，完成前不能取消。")
                }
                Ok(CancelDecision::Finished) => {
                    set_text(state.detail_label, "正在确认安装线程已结束。")
                }
                Err(error) => show_error(state, &error),
            }
            return;
        }
        if !state.worker_started {
            if let Err(error) = write_status(
                &state.status_file,
                "cancelled",
                "用户关闭了更新程序，未安装更新。",
                state.progress,
            ) {
                show_error(state, &error);
                return;
            }
        }
        DestroyWindow(hwnd);
    }

    unsafe fn post_ui_message(
        hwnd: HWND,
        sender: &mpsc::Sender<UiMessage>,
        progress: InstallProgress,
        finished: bool,
        success: bool,
    ) {
        let message = UiMessage {
            progress,
            finished,
            success,
        };
        if sender.send(message).is_err() {
            return;
        }
        if PostMessageW(hwnd, WM_APP_PROGRESS, 0, 0) == 0 {
            eprintln!(
                "updater UI notification failed: {}",
                std::io::Error::last_os_error()
            );
        }
    }

    unsafe fn drain_updates(hwnd: HWND, state: &mut UiState) {
        while let Ok(message) = state.updates.try_recv() {
            apply_message(hwnd, state, &message);
        }
    }

    unsafe fn show_error(state: &mut UiState, error: &str) {
        state.last_error = Some(error.to_string());
        set_text(state.status_label, "操作未完成，请查看详细信息。");
        set_text(
            state.details_edit,
            &format!("{error}\r\n\r\n状态文件：{}", state.status_file.display()),
        );
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

    unsafe fn create_details_edit(hwnd: HWND, instance: HINSTANCE) -> HWND {
        let class_name = wide("EDIT");
        CreateWindowExW(
            0,
            class_name.as_ptr(),
            ptr::null(),
            WS_CHILD
                | WS_VISIBLE
                | WS_TABSTOP
                | WS_BORDER
                | WS_VSCROLL
                | ES_MULTILINE as u32
                | ES_READONLY as u32
                | ES_AUTOVSCROLL as u32,
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
            state.details_heading,
            state.details_edit,
            state.copy_button,
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
            state.details_heading,
            margin,
            scale_logical_pixels(210, dpi),
            content_width,
            line_height,
        );
        let details_y = scale_logical_pixels(240, dpi);
        move_control(
            state.details_edit,
            margin,
            details_y,
            content_width,
            (button_y - gap - details_y).max(line_height),
        );
        move_control(
            state.copy_button,
            margin,
            button_y,
            scale_logical_pixels(100, dpi),
            button_height,
        );
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
    use super::*;

    struct TestDirectory(PathBuf);
    impl TestDirectory {
        fn new() -> Self {
            static NEXT: AtomicU64 = AtomicU64::new(0);
            let root = env::temp_dir().join(format!(
                "msc-updater-test-{}-{}",
                process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir(&root).unwrap();
            Self(root)
        }
        fn context(&self) -> InstallContext {
            let context = InstallContext {
                game_pid: 123,
                plugin_dir: self.0.join("mystia-steward-companion"),
                staged_dir: self.0.join("staged"),
                backup_dir: self.0.join("backup"),
                control_port: 0,
                wait_timeout: Duration::from_secs(1),
            };
            for root in [&context.plugin_dir, &context.staged_dir] {
                fs::create_dir_all(root.join("companion")).unwrap();
                for relative in [REQUIRED_DLL, REQUIRED_COMPANION_EXE, REQUIRED_UPDATER_EXE] {
                    fs::write(
                        root.join(relative),
                        if root == &context.plugin_dir {
                            b"old"
                        } else {
                            b"new"
                        },
                    )
                    .unwrap();
                }
            }
            context
        }
        fn assert_unchanged(&self, context: &InstallContext) {
            assert_eq!(
                fs::read(context.plugin_dir.join(REQUIRED_DLL)).unwrap(),
                b"old"
            );
            assert_eq!(
                fs::read(context.staged_dir.join(REQUIRED_DLL)).unwrap(),
                b"new"
            );
            assert!(!context.backup_dir.exists());
        }
    }
    impl Drop for TestDirectory {
        fn drop(&mut self) {
            fs::remove_dir_all(&self.0).unwrap();
        }
    }

    struct FakeProcess {
        running: Result<bool, String>,
    }
    impl FakeProcess {
        fn new(running: Result<bool, String>) -> Self {
            Self { running }
        }
    }
    impl ProcessObservation for FakeProcess {
        fn is_running(&self) -> Result<bool, String> {
            self.running.clone()
        }
    }

    #[test]
    fn cancelling_while_waiting_never_replaces_files() {
        let directory = TestDirectory::new();
        let context = directory.context();
        let control = InstallControl::default();
        let game = FakeProcess::new(Ok(true));
        let mut terminal = Vec::new();
        let result = run_install(
            &context,
            &directory.0.join("status.json"),
            &game,
            &control,
            &mut |event| {
                if event.state == "waiting-game" {
                    control.cancel().unwrap();
                }
                if matches!(event.state, "cancelled" | "succeeded" | "failed") {
                    terminal.push(event.state);
                }
            },
        )
        .unwrap();
        assert_eq!(result, InstallOutcome::Cancelled);
        assert_eq!(terminal, vec!["cancelled"]);
        directory.assert_unchanged(&context);
    }

    #[test]
    fn cancellation_at_last_process_exit_observation_wins_before_replacement() {
        let directory = TestDirectory::new();
        let context = directory.context();
        let control = InstallControl::default();
        let game = FakeProcess::new(Ok(false));
        let result = run_install(
            &context,
            &directory.0.join("status.json"),
            &game,
            &control,
            &mut |event| {
                if event.state == "game-closed" {
                    control.cancel().unwrap();
                }
            },
        )
        .unwrap();
        assert_eq!(result, InstallOutcome::Cancelled);
        directory.assert_unchanged(&context);
    }

    #[test]
    fn unknown_process_state_and_status_write_failure_stop_before_file_changes() {
        let directory = TestDirectory::new();
        let context = directory.context();
        let game = FakeProcess::new(Err("process access denied".to_string()));
        let result = run_install(
            &context,
            &directory.0.join("status.json"),
            &game,
            &InstallControl::default(),
            &mut |_| {},
        );
        assert!(result.unwrap_err().contains("process access denied"));
        directory.assert_unchanged(&context);
        let result = run_install(
            &context,
            &directory.0,
            &FakeProcess::new(Ok(false)),
            &InstallControl::default(),
            &mut |_| {},
        );
        assert!(result.unwrap_err().contains("write update status failed"));
        directory.assert_unchanged(&context);
    }

    #[test]
    fn a_step_panic_publishes_failed_status_and_finishes_the_control() {
        let directory = TestDirectory::new();
        let context = directory.context();
        let control = InstallControl::default();
        let status = directory.0.join("status.json");
        let mut terminals = Vec::new();
        let error = run_install(
            &context,
            &status,
            &FakeProcess::new(Ok(true)),
            &control,
            &mut |event| {
                if event.state == "waiting-game" {
                    panic!("injected step panic");
                }
                if matches!(event.state, "cancelled" | "succeeded" | "failed") {
                    terminals.push(event.state);
                }
            },
        )
        .unwrap_err();
        assert!(error.contains("安装线程异常终止"));
        assert_eq!(terminals, vec!["failed"]);
        let value: serde_json::Value = serde_json::from_slice(&fs::read(status).unwrap()).unwrap();
        assert_eq!(value["state"], "failed");
        assert_eq!(
            control.cancel().unwrap(),
            install_control::CancelDecision::Finished
        );
        directory.assert_unchanged(&context);
    }

    #[test]
    fn replacement_rejects_late_cancel_and_preserves_backup() {
        let directory = TestDirectory::new();
        let context = directory.context();
        let control = InstallControl::default();
        let game = FakeProcess::new(Ok(false));
        let result = run_install(
            &context,
            &directory.0.join("status.json"),
            &game,
            &control,
            &mut |event| {
                if event.state == "installing" {
                    assert_eq!(
                        control.cancel().unwrap(),
                        install_control::CancelDecision::TooLate
                    );
                }
            },
        )
        .unwrap();
        assert_eq!(result, InstallOutcome::Succeeded);
        assert_eq!(
            fs::read(context.plugin_dir.join(REQUIRED_DLL)).unwrap(),
            b"new"
        );
        assert_eq!(
            fs::read(context.backup_dir.join(REQUIRED_DLL)).unwrap(),
            b"old"
        );
    }

    #[test]
    fn status_failure_after_backup_rolls_back_before_new_files_are_moved() {
        let directory = TestDirectory::new();
        let context = directory.context();
        let status = directory.0.join("status.json");
        let mut before_backup = false;
        let result = replace_plugin_directory(
            &context.plugin_dir,
            &context.staged_dir,
            &context.backup_dir,
            &status,
            &mut |event| {
                if event.state == "backing-up" {
                    before_backup = true;
                    fs::remove_file(&status).unwrap();
                    fs::create_dir(&status).unwrap();
                }
            },
        );
        assert!(before_backup);
        assert!(result.unwrap_err().contains("restored previous version"));
        directory.assert_unchanged(&context);
    }

    #[test]
    fn old_mod_launch_parameters_remain_sufficient_and_package_binding_is_exact() {
        let directory = TestDirectory::new();
        let context = directory.context();
        let args = parse_args(vec![
            "--game-pid".to_string(),
            "123".to_string(),
            "--plugin-dir".to_string(),
            context.plugin_dir.display().to_string(),
            "--staged-dir".to_string(),
            context.staged_dir.display().to_string(),
            "--backup-dir".to_string(),
            context.backup_dir.display().to_string(),
            "--status-file".to_string(),
            directory.0.join("status.json").display().to_string(),
            "--control-port".to_string(),
            "32146".to_string(),
        ]);
        assert_eq!(parse_install_context(&args).unwrap().game_pid, 123);
        let packaged = context.staged_dir.join(REQUIRED_UPDATER_EXE);
        let runner = directory.0.join("runner.exe");
        fs::copy(&packaged, &runner).unwrap();
        assert!(verify_identical_files(&runner, &packaged).is_ok());
        fs::write(&runner, b"bad").unwrap();
        assert!(verify_identical_files(&runner, &packaged).is_err());
        fs::write(&runner, b"new trailing").unwrap();
        assert!(verify_identical_files(&runner, &packaged).is_err());
        let package: serde_json::Value =
            serde_json::from_str(include_str!("../../../../../package.json")).unwrap();
        let tauri: serde_json::Value =
            serde_json::from_str(include_str!("../../tauri.conf.json")).unwrap();
        assert_eq!(package["version"], TARGET_VERSION);
        assert_eq!(tauri["version"], TARGET_VERSION);
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
