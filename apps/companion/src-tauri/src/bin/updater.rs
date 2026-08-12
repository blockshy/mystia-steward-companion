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
use std::io::Write;
use std::net::TcpStream;
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

#[derive(Clone, Copy)]
enum GameCloseMode {
    #[cfg(not(target_os = "windows"))]
    WaitOnly,
    #[cfg(any(
        target_os = "windows",
        all(target_os = "linux", feature = "updater-windows-ui-check")
    ))]
    RequestClose,
    #[cfg(any(
        target_os = "windows",
        all(target_os = "linux", feature = "updater-windows-ui-check")
    ))]
    ForceTerminate,
}

#[derive(Clone, Copy)]
struct InstallOptions {
    game_close_mode: GameCloseMode,
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
        write_status(&status_file, "failed", &error, 100);
        eprintln!("{error}");
        process::exit(1);
    }
}

#[cfg(not(target_os = "windows"))]
fn run_silent(args: &HashMap<String, String>, status_file: &Path) -> Result<(), String> {
    let context = parse_install_context(args)?;
    let mut ignore_progress = |_progress: InstallProgress| {};
    run_install(
        &context,
        status_file,
        InstallOptions {
            game_close_mode: GameCloseMode::WaitOnly,
        },
        &mut ignore_progress,
    )
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
    options: InstallOptions,
    progress: &mut dyn FnMut(InstallProgress),
) -> Result<(), String> {
    publish_progress(
        status_file,
        progress,
        InstallProgress::new("preparing", "正在准备更新安装器。", 5),
    );
    validate_plugin_path(&context.plugin_dir)?;
    validate_staged_package(&context.staged_dir)?;
    prepare_parent(&context.backup_dir)?;

    publish_progress(
        status_file,
        progress,
        InstallProgress::new("closing-companion", "正在关闭伴随窗口以释放程序文件。", 12),
    );
    notify_companion_exit(context.control_port);
    if is_process_running(context.game_pid) {
        match options.game_close_mode {
            #[cfg(not(target_os = "windows"))]
            GameCloseMode::WaitOnly => {
                publish_progress(
                    status_file,
                    progress,
                    InstallProgress::new(
                        "waiting-game",
                        format!(
                            "检测到游戏进程 {} 仍在运行，请关闭游戏后继续安装。",
                            context.game_pid
                        ),
                        20,
                    ),
                );
            }
            #[cfg(any(
                target_os = "windows",
                all(target_os = "linux", feature = "updater-windows-ui-check")
            ))]
            GameCloseMode::RequestClose => {
                let requested = request_game_close(context.game_pid);
                let message = if requested {
                    format!(
                        "已请求游戏进程 {} 正常关闭，正在等待退出。",
                        context.game_pid
                    )
                } else {
                    format!(
                        "未找到游戏窗口，请手动关闭游戏进程 {} 后继续。",
                        context.game_pid
                    )
                };
                publish_progress(
                    status_file,
                    progress,
                    InstallProgress::new("waiting-game", message, 20),
                );
            }
            #[cfg(any(
                target_os = "windows",
                all(target_os = "linux", feature = "updater-windows-ui-check")
            ))]
            GameCloseMode::ForceTerminate => {
                publish_progress(
                    status_file,
                    progress,
                    InstallProgress::new("terminating-game", "正在强制结束游戏进程。", 18),
                );
                force_terminate_game(context.game_pid)?;
            }
        }
    }
    wait_for_game_exit(
        context.game_pid,
        context.wait_timeout,
        status_file,
        progress,
    )?;
    notify_companion_exit(context.control_port);
    thread::sleep(Duration::from_millis(700));

    publish_progress(
        status_file,
        progress,
        InstallProgress::new("installing", "正在替换插件文件。", 45),
    );
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
    );
    validate_staged_package(&context.plugin_dir)?;

    publish_progress(
        status_file,
        progress,
        InstallProgress::new("succeeded", "更新安装完成。请重新启动游戏。", 100),
    );
    Ok(())
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
    pid: u32,
    timeout: Duration,
    status_file: &Path,
    progress: &mut dyn FnMut(InstallProgress),
) -> Result<(), String> {
    let started = Instant::now();
    let mut next_report = Duration::ZERO;
    while started.elapsed() < timeout {
        if !is_process_running(pid) {
            publish_progress(
                status_file,
                progress,
                InstallProgress::new("game-closed", "已检测到游戏进程退出。", 35),
            );
            return Ok(());
        }
        let elapsed = started.elapsed();
        if elapsed >= next_report {
            publish_progress(
                status_file,
                progress,
                InstallProgress::new(
                    "waiting-game",
                    format!("等待游戏进程 {pid} 退出，已等待 {} 秒。", elapsed.as_secs()),
                    25,
                ),
            );
            next_report = elapsed + Duration::from_secs(2);
        }
        thread::sleep(Duration::from_millis(700));
    }
    Err(format!("timed out waiting for game process {pid} to exit"))
}

#[cfg(target_os = "windows")]
fn request_game_close(pid: u32) -> bool {
    windows_process_control::request_close(pid)
}

#[cfg(all(target_os = "linux", feature = "updater-windows-ui-check"))]
fn request_game_close(_pid: u32) -> bool {
    false
}

#[cfg(target_os = "windows")]
fn force_terminate_game(pid: u32) -> Result<(), String> {
    let status = process::Command::new("taskkill")
        // updater 由游戏进程启动，`/T` 会把子进程 updater 一并结束，导致安装流程停在强制关闭阶段。
        // 这里只终止游戏进程本身；安装器继续等待目标 PID 退出后再替换文件。
        .args(["/PID", &pid.to_string(), "/F"])
        .status()
        .map_err(|error| format!("failed to start taskkill: {error}"))?;
    if status.success() {
        Ok(())
    } else {
        Err(format!("taskkill exited with status {status}"))
    }
}

#[cfg(all(target_os = "linux", feature = "updater-windows-ui-check"))]
fn force_terminate_game(_pid: u32) -> Result<(), String> {
    Err("force termination is unavailable in the Windows UI compile check".to_string())
}

#[cfg(target_os = "windows")]
fn is_process_running(pid: u32) -> bool {
    // Windows 标准库没有稳定的跨版本进程查询 API；tasklist 输出足够用于本地 updater 的短轮询。
    let output = process::Command::new("tasklist")
        .args(["/FI", &format!("PID eq {pid}"), "/NH"])
        .output();
    let Ok(output) = output else {
        return true;
    };
    let text = String::from_utf8_lossy(&output.stdout);
    text.split_whitespace().any(|part| part == pid.to_string())
}

#[cfg(not(target_os = "windows"))]
fn is_process_running(pid: u32) -> bool {
    Path::new("/proc").join(pid.to_string()).exists()
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
        let fallback = backup_dir.with_extension(format!("old-{}", process::id()));
        fs::rename(backup_dir, &fallback).map_err(|error| {
            format!(
                "failed to move existing backup {} to {}: {error}",
                backup_dir.display(),
                fallback.display()
            )
        })?;
    }

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

#[cfg(target_os = "windows")]
mod windows_process_control {
    use std::ffi::c_void;

    type Bool = i32;
    type Dword = u32;
    type Hwnd = *mut c_void;
    type Lparam = isize;

    const WM_CLOSE: u32 = 0x0010;

    #[repr(C)]
    struct EnumState {
        pid: Dword,
        requested: Bool,
    }

    pub fn request_close(pid: u32) -> bool {
        let mut state = EnumState { pid, requested: 0 };
        unsafe {
            EnumWindows(enum_windows_proc, &mut state as *mut EnumState as Lparam);
        }
        state.requested != 0
    }

    unsafe extern "system" fn enum_windows_proc(hwnd: Hwnd, lparam: Lparam) -> Bool {
        let state = &mut *(lparam as *mut EnumState);
        if IsWindowVisible(hwnd) == 0 {
            return 1;
        }

        let mut window_pid: Dword = 0;
        GetWindowThreadProcessId(hwnd, &mut window_pid);
        if window_pid == state.pid {
            let _ = PostMessageW(hwnd, WM_CLOSE, 0, 0);
            state.requested = 1;
        }

        1
    }

    #[link(name = "user32")]
    extern "system" {
        fn EnumWindows(
            lpEnumFunc: unsafe extern "system" fn(Hwnd, Lparam) -> Bool,
            lParam: Lparam,
        ) -> Bool;
        fn GetWindowThreadProcessId(hWnd: Hwnd, lpdwProcessId: *mut Dword) -> Dword;
        fn IsWindowVisible(hWnd: Hwnd) -> Bool;
        fn PostMessageW(hWnd: Hwnd, Msg: u32, wParam: usize, lParam: isize) -> Bool;
    }
}

#[cfg(any(
    target_os = "windows",
    all(target_os = "linux", feature = "updater-windows-ui-check")
))]
mod windows_updater_ui {
    use super::{
        force_terminate_game, is_process_running, parse_install_context, run_install,
        scale_logical_pixels, write_status, GameCloseMode, InstallContext, InstallOptions,
        InstallProgress,
    };
    use std::collections::HashMap;
    use std::iter;
    use std::mem::size_of;
    use std::path::PathBuf;
    use std::ptr;
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
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::EnableWindow;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetClientRect,
        GetMessageW, GetWindowLongPtrW, LoadCursorW, MoveWindow, PostMessageW, PostQuitMessage,
        RegisterClassW, SendMessageW, SetWindowLongPtrW, SetWindowPos, SetWindowTextW, ShowWindow,
        TranslateMessage, BS_PUSHBUTTON, CW_USEDEFAULT, GWLP_USERDATA, IDC_ARROW, MSG,
        NONCLIENTMETRICSW, SPI_GETNONCLIENTMETRICS, SWP_NOACTIVATE, SWP_NOZORDER, SW_HIDE, SW_SHOW,
        WM_APP, WM_CLOSE, WM_COMMAND, WM_DESTROY, WM_DPICHANGED, WM_SETFONT, WM_SIZE, WNDCLASSW,
        WS_CAPTION, WS_CHILD, WS_MINIMIZEBOX, WS_OVERLAPPED, WS_SYSMENU, WS_VISIBLE,
    };

    const WINDOW_CLIENT_WIDTH: i32 = 680;
    const WINDOW_CLIENT_HEIGHT: i32 = 330;
    const START_BUTTON_ID: u16 = 1001;
    const FORCE_BUTTON_ID: u16 = 1002;
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
        force_button: HWND,
        close_button: HWND,
        body_font: HFONT,
        title_font: HFONT,
        dpi: u32,
        worker_started: bool,
        install_finished: bool,
        progress: u8,
    }

    impl Drop for UiState {
        fn drop(&mut self) {
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

            let mut msg = MSG::default();
            loop {
                let result = GetMessageW(&mut msg, ptr::null_mut(), 0, 0);
                if result == 0 {
                    break;
                }
                if result < 0 {
                    return Err("updater window message loop failed".to_string());
                }
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
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
        let (body_font, title_font) = create_fonts(dpi)?;
        let game_running = is_process_running(context.game_pid);
        let status_text = if game_running {
            format!("检测到游戏进程 {} 正在运行。", context.game_pid)
        } else {
            "游戏进程已退出，可以开始安装。".to_string()
        };
        let detail_text = if game_running {
            "请保存游戏进度后点击“关闭游戏并安装”。如果游戏无法正常退出，再使用“强制结束游戏”。"
        } else {
            "点击“开始安装”后会关闭伴随窗口、备份旧版本并替换插件目录。"
        };

        let title_label = create_label(hwnd, instance, "自动更新已准备就绪", STATIC_LEFT);
        let status_label = create_label(hwnd, instance, &status_text, STATIC_LEFT);
        let detail_label = create_label(hwnd, instance, detail_text, STATIC_LEFT);
        let progress_bar = create_progress_bar(hwnd, instance);
        let progress_label = create_label(hwnd, instance, "0%", STATIC_RIGHT);
        let start_text = if game_running {
            "关闭游戏并安装"
        } else {
            "开始安装"
        };
        let start_button = create_button(hwnd, instance, START_BUTTON_ID, start_text);
        let force_button = create_button(hwnd, instance, FORCE_BUTTON_ID, "强制结束游戏");
        let close_button = create_button(hwnd, instance, CLOSE_BUTTON_ID, "取消");
        let controls = [
            title_label,
            status_label,
            detail_label,
            progress_bar,
            progress_label,
            start_button,
            force_button,
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
        ShowWindow(force_button, if game_running { SW_SHOW } else { SW_HIDE });

        Ok(UiState {
            context,
            status_file,
            title_label,
            status_label,
            detail_label,
            progress_bar,
            progress_label,
            start_button,
            force_button,
            close_button,
            body_font,
            title_font,
            dpi,
            worker_started: false,
            install_finished: false,
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
                        START_BUTTON_ID => start_worker(hwnd, state, GameCloseMode::RequestClose),
                        FORCE_BUTTON_ID => {
                            if state.worker_started {
                                set_text(
                                    state.detail_label,
                                    "正在强制结束游戏进程，安装程序会在进程退出后继续。",
                                );
                                EnableWindow(state.force_button, 0);
                                let pid = state.context.game_pid;
                                thread::spawn(move || {
                                    let _ = force_terminate_game(pid);
                                });
                            } else {
                                start_worker(hwnd, state, GameCloseMode::ForceTerminate);
                            }
                        }
                        CLOSE_BUTTON_ID => close_or_cancel(hwnd, state),
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

    unsafe fn start_worker(hwnd: HWND, state: &mut UiState, game_close_mode: GameCloseMode) {
        if state.worker_started {
            return;
        }

        state.worker_started = true;
        set_text(state.title_label, "正在安装更新");
        set_text(state.detail_label, "安装开始后请不要手动删除插件目录。");
        EnableWindow(state.start_button, 0);
        EnableWindow(state.close_button, 0);
        if is_process_running(state.context.game_pid) {
            EnableWindow(state.force_button, 1);
        } else {
            ShowWindow(state.force_button, SW_HIDE);
        }

        let context = state.context.clone();
        let status_file = state.status_file.clone();
        let hwnd_value = hwnd as isize;
        thread::spawn(move || {
            let hwnd = hwnd_value as HWND;
            let mut post_progress = |progress: InstallProgress| unsafe {
                post_ui_message(hwnd, progress, false, false);
            };
            let result = run_install(
                &context,
                &status_file,
                InstallOptions { game_close_mode },
                &mut post_progress,
            );
            match result {
                Ok(()) => unsafe {
                    post_ui_message(
                        hwnd,
                        InstallProgress::new("succeeded", "更新安装完成。请重新启动游戏。", 100),
                        true,
                        true,
                    );
                },
                Err(error) => unsafe {
                    post_ui_message(
                        hwnd,
                        InstallProgress::new("failed", format!("更新安装失败：{error}"), 100),
                        true,
                        false,
                    );
                },
            }
        });
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
        if message.finished {
            state.install_finished = true;
            EnableWindow(state.force_button, 0);
            ShowWindow(state.force_button, SW_HIDE);
            EnableWindow(state.close_button, 1);
            set_text(state.close_button, "关闭");
            set_text(
                state.title_label,
                if message.success {
                    "更新安装完成"
                } else {
                    "更新安装失败"
                },
            );
            set_text(
                state.detail_label,
                if message.success {
                    "旧版本已备份，新版本已写入。关闭此窗口后重新启动游戏即可使用。"
                } else {
                    "旧版本目录会尽量保留或回滚。请查看更新状态文件或重新下载更新包后再试。"
                },
            );
            let _ = SendMessageW(
                state.progress_bar,
                PBM_SETSTATE,
                if message.success {
                    PBST_NORMAL as usize
                } else {
                    PBST_ERROR as usize
                },
                0,
            );
        }
        layout_controls(hwnd, state);
    }

    unsafe fn close_or_cancel(hwnd: HWND, state: &mut UiState) {
        if state.worker_started && !state.install_finished {
            set_text(
                state.detail_label,
                "更新程序正在等待游戏退出或替换文件，完成前不能关闭。",
            );
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
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON as u32,
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
            state.force_button,
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
        let force_width = scale_logical_pixels(128, dpi);
        let start_width = scale_logical_pixels(146, dpi);
        let button_y = (height - margin - button_height).max(margin);
        let close_x = width - margin - close_width;
        let force_x = close_x - gap - force_width;
        let start_x = force_x - gap - start_width;
        move_control(
            state.start_button,
            start_x,
            button_y,
            start_width,
            button_height,
        );
        move_control(
            state.force_button,
            force_x,
            button_y,
            force_width,
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
