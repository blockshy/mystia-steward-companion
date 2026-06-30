//! 自动更新的独立文件替换程序。
//!
//! 主 Mod 进程只负责下载、校验和解压更新包；本程序从配置目录中的 runner 副本启动，
//! 等待游戏进程退出后再替换插件目录。这样可以避免运行中的 BepInEx DLL 或伴随窗口 exe
//! 被自身进程锁定导致半更新。

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
    #[cfg(target_os = "windows")]
    RequestClose,
    #[cfg(target_os = "windows")]
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
            #[cfg(target_os = "windows")]
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
            #[cfg(target_os = "windows")]
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

#[cfg(target_os = "windows")]
mod windows_updater_ui {
    use super::{
        force_terminate_game, is_process_running, parse_install_context, run_install, write_status,
        GameCloseMode, InstallContext, InstallOptions, InstallProgress,
    };
    use std::collections::HashMap;
    use std::ffi::{c_void, OsStr};
    use std::iter;
    use std::os::windows::ffi::OsStrExt;
    use std::path::PathBuf;
    use std::ptr;
    use std::thread;

    type Bool = i32;
    type Dword = u32;
    type Hbrush = *mut c_void;
    type Hcursor = *mut c_void;
    type Hdc = *mut c_void;
    type Hfont = *mut c_void;
    type Hicon = *mut c_void;
    type Hinstance = *mut c_void;
    type Hmenu = *mut c_void;
    type Hwnd = *mut c_void;
    type Lparam = isize;
    type Lresult = isize;
    type Uint = u32;
    type Wparam = usize;

    const CW_USEDEFAULT: i32 = 0x80000000u32 as i32;
    const GWLP_USERDATA: i32 = -21;
    const WS_OVERLAPPED: Dword = 0x00000000;
    const WS_CAPTION: Dword = 0x00C00000;
    const WS_SYSMENU: Dword = 0x00080000;
    const WS_MINIMIZEBOX: Dword = 0x00020000;
    const WS_CHILD: Dword = 0x40000000;
    const WS_VISIBLE: Dword = 0x10000000;
    const SS_LEFT: Dword = 0x00000000;
    const BS_PUSHBUTTON: Dword = 0x00000000;
    const WM_COMMAND: Uint = 0x0111;
    const WM_CLOSE: Uint = 0x0010;
    const WM_DESTROY: Uint = 0x0002;
    const WM_PAINT: Uint = 0x000F;
    const WM_SETFONT: Uint = 0x0030;
    const WM_APP_PROGRESS: Uint = 0x8001;
    const SW_SHOW: i32 = 5;
    const SW_HIDE: i32 = 0;
    const COLOR_WINDOW: isize = 5;
    const DEFAULT_GUI_FONT: i32 = 17;
    const START_BUTTON_ID: u16 = 1001;
    const FORCE_BUTTON_ID: u16 = 1002;
    const CLOSE_BUTTON_ID: u16 = 1003;

    struct UiState {
        context: InstallContext,
        status_file: PathBuf,
        title_label: Hwnd,
        status_label: Hwnd,
        detail_label: Hwnd,
        progress_label: Hwnd,
        start_button: Hwnd,
        force_button: Hwnd,
        close_button: Hwnd,
        worker_started: bool,
        install_finished: bool,
        progress: u8,
    }

    struct UiMessage {
        progress: InstallProgress,
        finished: bool,
        success: bool,
    }

    #[repr(C)]
    struct Rect {
        left: i32,
        top: i32,
        right: i32,
        bottom: i32,
    }

    #[repr(C)]
    struct Point {
        x: i32,
        y: i32,
    }

    #[repr(C)]
    struct Msg {
        hwnd: Hwnd,
        message: Uint,
        w_param: Wparam,
        l_param: Lparam,
        time: Dword,
        pt: Point,
    }

    #[repr(C)]
    struct PaintStruct {
        hdc: Hdc,
        f_erase: Bool,
        rc_paint: Rect,
        f_restore: Bool,
        f_inc_update: Bool,
        rgb_reserved: [u8; 32],
    }

    #[repr(C)]
    struct WndClassW {
        style: Uint,
        lpfn_wnd_proc: Option<unsafe extern "system" fn(Hwnd, Uint, Wparam, Lparam) -> Lresult>,
        cb_cls_extra: i32,
        cb_wnd_extra: i32,
        h_instance: Hinstance,
        h_icon: Hicon,
        h_cursor: Hcursor,
        hbr_background: Hbrush,
        lpsz_menu_name: *const u16,
        lpsz_class_name: *const u16,
    }

    pub fn run(args: HashMap<String, String>, status_file: PathBuf) -> Result<(), String> {
        let context = parse_install_context(&args)?;
        let class_name = wide("MystiaStewardCompanionUpdaterWindow");
        let title = wide("mystia-steward-companion 更新程序");

        unsafe {
            let instance = GetModuleHandleW(ptr::null());
            let class = WndClassW {
                style: 0,
                lpfn_wnd_proc: Some(window_proc),
                cb_cls_extra: 0,
                cb_wnd_extra: 0,
                h_instance: instance,
                h_icon: ptr::null_mut(),
                h_cursor: LoadCursorW(ptr::null_mut(), 32512usize as *const u16),
                hbr_background: (COLOR_WINDOW + 1) as Hbrush,
                lpsz_menu_name: ptr::null(),
                lpsz_class_name: class_name.as_ptr(),
            };
            if RegisterClassW(&class) == 0 {
                return Err("register updater window class failed".to_string());
            }

            let hwnd = CreateWindowExW(
                0,
                class_name.as_ptr(),
                title.as_ptr(),
                WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                640,
                300,
                ptr::null_mut(),
                ptr::null_mut(),
                instance,
                ptr::null_mut(),
            );
            if hwnd.is_null() {
                return Err("create updater window failed".to_string());
            }

            let state = Box::new(build_state(hwnd, instance, context, status_file));
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, Box::into_raw(state) as isize);
            ShowWindow(hwnd, SW_SHOW);
            UpdateWindow(hwnd);

            let mut msg = Msg {
                hwnd: ptr::null_mut(),
                message: 0,
                w_param: 0,
                l_param: 0,
                time: 0,
                pt: Point { x: 0, y: 0 },
            };
            while GetMessageW(&mut msg, ptr::null_mut(), 0, 0) > 0 {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }

        Ok(())
    }

    unsafe fn build_state(
        hwnd: Hwnd,
        instance: Hinstance,
        context: InstallContext,
        status_file: PathBuf,
    ) -> UiState {
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

        let title_label = create_label(hwnd, instance, "自动更新已准备就绪", 24, 22, 560, 24);
        let status_label = create_label(hwnd, instance, &status_text, 24, 58, 560, 24);
        let detail_label = create_label(hwnd, instance, detail_text, 24, 84, 560, 42);
        let progress_label = create_label(hwnd, instance, "0%", 24, 160, 560, 24);
        let start_text = if game_running {
            "关闭游戏并安装"
        } else {
            "开始安装"
        };
        let start_button = create_button(
            hwnd,
            instance,
            START_BUTTON_ID,
            start_text,
            250,
            212,
            130,
            32,
        );
        let force_button = create_button(
            hwnd,
            instance,
            FORCE_BUTTON_ID,
            "强制结束游戏",
            390,
            212,
            120,
            32,
        );
        let close_button = create_button(hwnd, instance, CLOSE_BUTTON_ID, "取消", 520, 212, 72, 32);
        ShowWindow(force_button, if game_running { SW_SHOW } else { SW_HIDE });

        UiState {
            context,
            status_file,
            title_label,
            status_label,
            detail_label,
            progress_label,
            start_button,
            force_button,
            close_button,
            worker_started: false,
            install_finished: false,
            progress: 0,
        }
    }

    unsafe extern "system" fn window_proc(
        hwnd: Hwnd,
        msg: Uint,
        w_param: Wparam,
        l_param: Lparam,
    ) -> Lresult {
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
            WM_APP_PROGRESS => {
                if l_param != 0 {
                    let message = Box::from_raw(l_param as *mut UiMessage);
                    if let Some(state) = state_mut(hwnd) {
                        apply_message(hwnd, state, &message);
                    }
                }
                0
            }
            WM_PAINT => {
                paint_progress(hwnd);
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

    unsafe fn start_worker(hwnd: Hwnd, state: &mut UiState, game_close_mode: GameCloseMode) {
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
            let hwnd = hwnd_value as Hwnd;
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

    unsafe fn apply_message(hwnd: Hwnd, state: &mut UiState, message: &UiMessage) {
        state.progress = message.progress.progress;
        set_text(state.status_label, &message.progress.message);
        set_text(
            state.progress_label,
            &format!("{}%", message.progress.progress),
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
        }
        InvalidateRect(hwnd, ptr::null(), 1);
    }

    unsafe fn close_or_cancel(hwnd: Hwnd, state: &mut UiState) {
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

    unsafe fn paint_progress(hwnd: Hwnd) {
        let mut paint = PaintStruct {
            hdc: ptr::null_mut(),
            f_erase: 0,
            rc_paint: Rect {
                left: 0,
                top: 0,
                right: 0,
                bottom: 0,
            },
            f_restore: 0,
            f_inc_update: 0,
            rgb_reserved: [0; 32],
        };
        let hdc = BeginPaint(hwnd, &mut paint);
        let progress = state_mut(hwnd).map(|state| state.progress).unwrap_or(0);
        let background = CreateSolidBrush(0x00e8e6e3);
        let foreground = CreateSolidBrush(0x00b86f28);
        let border = CreateSolidBrush(0x00d0ccc7);
        let outer = Rect {
            left: 24,
            top: 136,
            right: 592,
            bottom: 158,
        };
        FillRect(hdc, &outer, border);
        let inner = Rect {
            left: 25,
            top: 137,
            right: 591,
            bottom: 157,
        };
        FillRect(hdc, &inner, background);
        let width = ((inner.right - inner.left) * i32::from(progress)) / 100;
        if width > 0 {
            let filled = Rect {
                left: inner.left,
                top: inner.top,
                right: inner.left + width,
                bottom: inner.bottom,
            };
            FillRect(hdc, &filled, foreground);
        }
        DeleteObject(background);
        DeleteObject(foreground);
        DeleteObject(border);
        EndPaint(hwnd, &paint);
    }

    unsafe fn post_ui_message(
        hwnd: Hwnd,
        progress: InstallProgress,
        finished: bool,
        success: bool,
    ) {
        let message = Box::new(UiMessage {
            progress,
            finished,
            success,
        });
        let _ = PostMessageW(hwnd, WM_APP_PROGRESS, 0, Box::into_raw(message) as Lparam);
    }

    unsafe fn state_mut(hwnd: Hwnd) -> Option<&'static mut UiState> {
        let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        if ptr == 0 {
            None
        } else {
            Some(&mut *(ptr as *mut UiState))
        }
    }

    unsafe fn create_label(
        hwnd: Hwnd,
        instance: Hinstance,
        text: &str,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
    ) -> Hwnd {
        let class_name = wide("STATIC");
        let text = wide(text);
        let control = CreateWindowExW(
            0,
            class_name.as_ptr(),
            text.as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            x,
            y,
            width,
            height,
            hwnd,
            ptr::null_mut(),
            instance,
            ptr::null_mut(),
        );
        set_default_font(control);
        control
    }

    unsafe fn create_button(
        hwnd: Hwnd,
        instance: Hinstance,
        id: u16,
        text: &str,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
    ) -> Hwnd {
        let class_name = wide("BUTTON");
        let text = wide(text);
        let control = CreateWindowExW(
            0,
            class_name.as_ptr(),
            text.as_ptr(),
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            x,
            y,
            width,
            height,
            hwnd,
            id as usize as Hmenu,
            instance,
            ptr::null_mut(),
        );
        set_default_font(control);
        control
    }

    unsafe fn set_default_font(hwnd: Hwnd) {
        let font = GetStockObject(DEFAULT_GUI_FONT) as Hfont;
        let _ = SendMessageW(hwnd, WM_SETFONT, font as usize, 1);
    }

    unsafe fn set_text(hwnd: Hwnd, text: &str) {
        let text = wide(text);
        let _ = SetWindowTextW(hwnd, text.as_ptr());
    }

    fn wide(value: &str) -> Vec<u16> {
        OsStr::new(value)
            .encode_wide()
            .chain(iter::once(0))
            .collect()
    }

    #[link(name = "user32")]
    extern "system" {
        fn BeginPaint(hWnd: Hwnd, lpPaint: *mut PaintStruct) -> Hdc;
        fn CreateWindowExW(
            dwExStyle: Dword,
            lpClassName: *const u16,
            lpWindowName: *const u16,
            dwStyle: Dword,
            X: i32,
            Y: i32,
            nWidth: i32,
            nHeight: i32,
            hWndParent: Hwnd,
            hMenu: Hmenu,
            hInstance: Hinstance,
            lpParam: *mut c_void,
        ) -> Hwnd;
        fn DefWindowProcW(hWnd: Hwnd, Msg: Uint, wParam: Wparam, lParam: Lparam) -> Lresult;
        fn DestroyWindow(hWnd: Hwnd) -> Bool;
        fn DispatchMessageW(lpMsg: *const Msg) -> Lresult;
        fn EnableWindow(hWnd: Hwnd, bEnable: Bool) -> Bool;
        fn EndPaint(hWnd: Hwnd, lpPaint: *const PaintStruct) -> Bool;
        fn FillRect(hDC: Hdc, lprc: *const Rect, hbr: Hbrush) -> i32;
        fn GetMessageW(
            lpMsg: *mut Msg,
            hWnd: Hwnd,
            wMsgFilterMin: Uint,
            wMsgFilterMax: Uint,
        ) -> Bool;
        fn GetWindowLongPtrW(hWnd: Hwnd, nIndex: i32) -> isize;
        fn InvalidateRect(hWnd: Hwnd, lpRect: *const Rect, bErase: Bool) -> Bool;
        fn LoadCursorW(hInstance: Hinstance, lpCursorName: *const u16) -> Hcursor;
        fn PostMessageW(hWnd: Hwnd, Msg: Uint, wParam: Wparam, lParam: Lparam) -> Bool;
        fn PostQuitMessage(nExitCode: i32);
        fn RegisterClassW(lpWndClass: *const WndClassW) -> u16;
        fn SendMessageW(hWnd: Hwnd, Msg: Uint, wParam: Wparam, lParam: Lparam) -> Lresult;
        fn SetWindowLongPtrW(hWnd: Hwnd, nIndex: i32, dwNewLong: isize) -> isize;
        fn SetWindowTextW(hWnd: Hwnd, lpString: *const u16) -> Bool;
        fn ShowWindow(hWnd: Hwnd, nCmdShow: i32) -> Bool;
        fn TranslateMessage(lpMsg: *const Msg) -> Bool;
        fn UpdateWindow(hWnd: Hwnd) -> Bool;
    }

    #[link(name = "gdi32")]
    extern "system" {
        fn CreateSolidBrush(color: Dword) -> Hbrush;
        fn DeleteObject(ho: *mut c_void) -> Bool;
        fn GetStockObject(i: i32) -> *mut c_void;
    }

    #[link(name = "kernel32")]
    extern "system" {
        fn GetModuleHandleW(lpModuleName: *const u16) -> Hinstance;
    }
}
