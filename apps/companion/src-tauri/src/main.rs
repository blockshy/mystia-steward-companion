#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::fs;
use std::io::{Read, Write};
use std::net::{Ipv4Addr, SocketAddr, TcpListener, TcpStream};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{
    Emitter, Manager, Monitor, PhysicalPosition, PhysicalSize, Position, Size, WebviewWindow,
    Window, WindowEvent,
};

const DEFAULT_API_ENDPOINT: &str = "http://127.0.0.1:32145";
const CONTROL_PORT: u16 = 32146;
const CONTROL_SHOW: &[u8] = b"mystia-steward-companion:show";
const CONTROL_TOGGLE: &[u8] = b"mystia-steward-companion:toggle";
const CONTROL_EXIT: &[u8] = b"mystia-steward-companion:exit";
const WINDOW_STATE_FILE: &str = "window-state.txt";
const MIN_WINDOW_WIDTH: u32 = 720;
const MIN_WINDOW_HEIGHT: u32 = 520;
const DEFAULT_WINDOW_SWITCH_COOLDOWN_MS: u64 = 800;
const MIN_WINDOW_SWITCH_COOLDOWN_MS: u64 = 250;
const MAX_WINDOW_SWITCH_COOLDOWN_MS: u64 = 2000;

struct GamePidState(Arc<Mutex<Option<u32>>>);
struct WindowSwitchState(Arc<Mutex<Option<Instant>>>);
struct CompanionPreferenceState(Arc<Mutex<CompanionPreferences>>);
struct MousePassthroughState(Arc<Mutex<bool>>);
struct TrayPassthroughMenuState(Arc<Mutex<Option<MenuItem<tauri::Wry>>>>);

#[derive(Clone, Copy)]
struct CompanionPreferences {
    keep_visible_when_focused: bool,
    window_switch_cooldown_ms: u64,
}

impl Default for CompanionPreferences {
    fn default() -> Self {
        Self {
            keep_visible_when_focused: false,
            window_switch_cooldown_ms: DEFAULT_WINDOW_SWITCH_COOLDOWN_MS,
        }
    }
}

#[derive(Clone, Copy)]
struct PersistedWindowState {
    x: i32,
    y: i32,
    width: u32,
    height: u32,
}

#[tauri::command]
fn fetch_snapshot(endpoint: String, token: String) -> Result<String, String> {
    request_local_api(&endpoint, None, &token)
}

fn request_local_api(
    endpoint: &str,
    path_override: Option<&str>,
    token: &str,
) -> Result<String, String> {
    request_local_api_with_timeout(
        endpoint,
        path_override,
        token,
        Duration::from_millis(1800),
        Duration::from_millis(1800),
        Duration::from_millis(1200),
    )
}

fn request_local_api_with_timeout(
    endpoint: &str,
    path_override: Option<&str>,
    token: &str,
    connect_timeout: Duration,
    read_timeout: Duration,
    write_timeout: Duration,
) -> Result<String, String> {
    let target = LocalApiTarget::parse(&endpoint)?;
    let path = path_override.unwrap_or(&target.path);
    validate_http_fragment(path, "path")?;
    validate_http_fragment(token, "token")?;

    let address = SocketAddr::from((Ipv4Addr::LOCALHOST, target.port));
    let mut stream = TcpStream::connect_timeout(&address, connect_timeout)
        .map_err(|error| format!("connect failed: {error}"))?;

    stream
        .set_read_timeout(Some(read_timeout))
        .map_err(|error| format!("set read timeout failed: {error}"))?;
    stream
        .set_write_timeout(Some(write_timeout))
        .map_err(|error| format!("set write timeout failed: {error}"))?;

    let auth_header = if token.trim().is_empty() {
        String::new()
    } else {
        format!("X-Mystia-Steward-Companion-Token: {}\r\n", token.trim())
    };
    let request = format!(
        "GET {} HTTP/1.1\r\nHost: 127.0.0.1:{}\r\n{}Connection: close\r\nCache-Control: no-store\r\n\r\n",
        path, target.port, auth_header
    );
    stream
        .write_all(request.as_bytes())
        .map_err(|error| format!("request failed: {error}"))?;

    let mut response = String::new();
    stream
        .read_to_string(&mut response)
        .map_err(|error| format!("response failed: {error}"))?;

    parse_http_body(&response)
}

#[tauri::command]
fn launch_api_endpoint() -> Option<String> {
    std::env::args().find_map(|arg| arg.strip_prefix("--api=").map(|value| value.to_string()))
}

#[tauri::command]
fn launch_api_token() -> Option<String> {
    std::env::args().find_map(|arg| arg.strip_prefix("--token=").map(|value| value.to_string()))
}

#[tauri::command]
fn toggle_companion_focus(
    app: tauri::AppHandle,
    game_pid_state: tauri::State<'_, GamePidState>,
    switch_state: tauri::State<'_, WindowSwitchState>,
    preference_state: tauri::State<'_, CompanionPreferenceState>,
    mouse_passthrough_state: tauri::State<'_, MousePassthroughState>,
    keep_visible_when_focused: Option<bool>,
    window_switch_cooldown_ms: Option<u64>,
) {
    let preferences = current_companion_preferences(&preference_state.0);
    if !try_begin_window_switch(
        &switch_state.0,
        window_switch_cooldown_ms.unwrap_or(preferences.window_switch_cooldown_ms),
    ) {
        return;
    }
    toggle_main_window(
        &app,
        current_game_pid(&game_pid_state.0),
        keep_visible_when_focused.unwrap_or(preferences.keep_visible_when_focused),
        &mouse_passthrough_state.0,
    );
}

#[tauri::command]
fn apply_companion_preferences(
    app: tauri::AppHandle,
    preference_state: tauri::State<'_, CompanionPreferenceState>,
    keep_visible_when_focused: bool,
    always_on_top: bool,
    window_switch_cooldown_ms: u64,
) {
    if let Ok(mut preferences) = preference_state.0.lock() {
        *preferences = CompanionPreferences {
            keep_visible_when_focused,
            window_switch_cooldown_ms: normalize_window_switch_cooldown_ms(
                window_switch_cooldown_ms,
            ),
        };
    }

    if let Some(window) = app.get_webview_window("main") {
        let _ = window.set_always_on_top(always_on_top);
    }
}

#[tauri::command]
fn set_mouse_passthrough(
    app: tauri::AppHandle,
    mouse_passthrough_state: tauri::State<'_, MousePassthroughState>,
    enabled: bool,
) -> Result<bool, String> {
    set_mouse_passthrough_internal(&app, &mouse_passthrough_state.0, enabled)
}

#[tauri::command]
fn get_mouse_passthrough(mouse_passthrough_state: tauri::State<'_, MousePassthroughState>) -> bool {
    current_mouse_passthrough(&mouse_passthrough_state.0)
}

fn launch_game_pid() -> Option<u32> {
    std::env::args().find_map(|arg| {
        arg.strip_prefix("--game-pid=")
            .and_then(|value| value.parse::<u32>().ok())
    })
}

fn parse_control_game_pid(message: &[u8]) -> Option<u32> {
    let text = std::str::from_utf8(message).ok()?;
    text.split_whitespace().find_map(|part| {
        part.strip_prefix("--game-pid=")
            .and_then(|value| value.parse::<u32>().ok())
    })
}

fn update_game_pid(game_pid: &Arc<Mutex<Option<u32>>>, next: Option<u32>) {
    let Some(next) = next else {
        return;
    };
    if let Ok(mut current) = game_pid.lock() {
        *current = Some(next);
    }
}

fn current_game_pid(game_pid: &Arc<Mutex<Option<u32>>>) -> Option<u32> {
    game_pid.lock().ok().and_then(|current| *current)
}

fn current_companion_preferences(
    preferences: &Arc<Mutex<CompanionPreferences>>,
) -> CompanionPreferences {
    preferences
        .lock()
        .map(|current| *current)
        .unwrap_or_default()
}

fn current_mouse_passthrough(mouse_passthrough: &Arc<Mutex<bool>>) -> bool {
    mouse_passthrough
        .lock()
        .map(|current| *current)
        .unwrap_or(false)
}

fn set_mouse_passthrough_internal(
    app: &tauri::AppHandle,
    mouse_passthrough: &Arc<Mutex<bool>>,
    enabled: bool,
) -> Result<bool, String> {
    if let Some(window) = app.get_webview_window("main") {
        window
            .set_ignore_cursor_events(enabled)
            .map_err(|error| format!("set mouse passthrough failed: {error}"))?;
    }

    if let Ok(mut current) = mouse_passthrough.lock() {
        *current = enabled;
    }
    update_mouse_passthrough_tray_label(app, enabled);
    let _ = app.emit("mouse-passthrough-changed", enabled);
    Ok(enabled)
}

fn mouse_passthrough_tray_label(enabled: bool) -> &'static str {
    if enabled {
        "关闭鼠标穿透"
    } else {
        "开启鼠标穿透"
    }
}

fn update_mouse_passthrough_tray_label(app: &tauri::AppHandle, enabled: bool) {
    let Some(state) = app.try_state::<TrayPassthroughMenuState>() else {
        return;
    };
    let item = state
        .0
        .lock()
        .ok()
        .and_then(|current| current.as_ref().cloned());
    if let Some(item) = item {
        let _ = item.set_text(mouse_passthrough_tray_label(enabled));
    }
}

#[cfg(target_os = "windows")]
fn toggle_mouse_passthrough(app: &tauri::AppHandle, mouse_passthrough: &Arc<Mutex<bool>>) {
    let enabled = !current_mouse_passthrough(mouse_passthrough);
    let _ = set_mouse_passthrough_internal(app, mouse_passthrough, enabled);
}

#[cfg(target_os = "windows")]
fn start_mouse_passthrough_hotkey_monitor(
    app: tauri::AppHandle,
    mouse_passthrough: Arc<Mutex<bool>>,
) {
    thread::spawn(move || {
        windows_hotkey::run_f10_hotkey_loop(move || {
            toggle_mouse_passthrough(&app, &mouse_passthrough);
        });
    });
}

#[cfg(not(target_os = "windows"))]
fn start_mouse_passthrough_hotkey_monitor(
    _app: tauri::AppHandle,
    _mouse_passthrough: Arc<Mutex<bool>>,
) {
}

fn try_begin_window_switch(switch_state: &Arc<Mutex<Option<Instant>>>, cooldown_ms: u64) -> bool {
    let Ok(mut last_switch) = switch_state.lock() else {
        return true;
    };
    let cooldown = Duration::from_millis(normalize_window_switch_cooldown_ms(cooldown_ms));
    let now = Instant::now();
    if last_switch.is_some_and(|previous| now.duration_since(previous) < cooldown) {
        return false;
    }
    *last_switch = Some(now);
    true
}

fn normalize_window_switch_cooldown_ms(value: u64) -> u64 {
    value.clamp(MIN_WINDOW_SWITCH_COOLDOWN_MS, MAX_WINDOW_SWITCH_COOLDOWN_MS)
}

struct LocalApiTarget {
    port: u16,
    path: String,
}

impl LocalApiTarget {
    fn parse(input: &str) -> Result<Self, String> {
        let trimmed = input.trim().trim_end_matches('/');
        let without_scheme = trimmed
            .strip_prefix("http://")
            .or_else(|| trimmed.strip_prefix("https://"))
            .unwrap_or(trimmed);
        let (authority, path) = if let Some((host, rest)) = without_scheme.split_once('/') {
            let normalized_path = if rest.is_empty() {
                "/snapshot".to_string()
            } else {
                format!("/{rest}")
            };
            (host, normalized_path)
        } else {
            (without_scheme, "/snapshot".to_string())
        };

        let (host, port) = parse_authority(authority)?;
        if host != "127.0.0.1" && host != "localhost" {
            return Err("only 127.0.0.1 loopback endpoints are allowed".to_string());
        }

        Ok(Self {
            port,
            path: if path == "/" {
                "/snapshot".to_string()
            } else {
                path
            },
        })
    }
}

fn parse_authority(authority: &str) -> Result<(&str, u16), String> {
    let (host, port_text) = authority
        .rsplit_once(':')
        .ok_or_else(|| "missing local API port".to_string())?;
    let port = port_text
        .parse::<u16>()
        .map_err(|_| "invalid local API port".to_string())?;
    Ok((host, port))
}

fn validate_http_fragment(value: &str, label: &str) -> Result<(), String> {
    if value.contains('\r') || value.contains('\n') {
        return Err(format!("invalid {label}"));
    }

    Ok(())
}

fn parse_http_body(response: &str) -> Result<String, String> {
    let (head, body) = response
        .split_once("\r\n\r\n")
        .ok_or_else(|| "invalid HTTP response".to_string())?;
    let status = head.lines().next().unwrap_or_default();
    if !status.contains(" 200 ") {
        return Err(status.to_string());
    }

    Ok(body.to_string())
}

fn notify_existing_instance() -> bool {
    let address = SocketAddr::from((Ipv4Addr::LOCALHOST, CONTROL_PORT));
    let Ok(mut stream) = TcpStream::connect_timeout(&address, Duration::from_millis(250)) else {
        return false;
    };

    stream.write_all(CONTROL_SHOW).is_ok()
}

fn start_instance_control_server(
    app: tauri::AppHandle,
    game_pid: Arc<Mutex<Option<u32>>>,
    switch_state: Arc<Mutex<Option<Instant>>>,
    preferences: Arc<Mutex<CompanionPreferences>>,
    mouse_passthrough: Arc<Mutex<bool>>,
) {
    thread::spawn(move || {
        let address = SocketAddr::from((Ipv4Addr::LOCALHOST, CONTROL_PORT));
        let Ok(listener) = TcpListener::bind(address) else {
            return;
        };

        for stream in listener.incoming() {
            let Ok(mut stream) = stream else {
                continue;
            };
            let mut buffer = [0u8; 64];
            let Ok(size) = stream.read(&mut buffer) else {
                continue;
            };
            let message = &buffer[..size];
            update_game_pid(&game_pid, parse_control_game_pid(message));
            if message.starts_with(CONTROL_SHOW) {
                show_main_window(&app, &mouse_passthrough);
            } else if message.starts_with(CONTROL_TOGGLE) {
                let preferences = current_companion_preferences(&preferences);
                if !try_begin_window_switch(&switch_state, preferences.window_switch_cooldown_ms) {
                    continue;
                }
                toggle_main_window(
                    &app,
                    current_game_pid(&game_pid),
                    preferences.keep_visible_when_focused,
                    &mouse_passthrough,
                );
            } else if message.starts_with(CONTROL_EXIT) {
                app.exit(0);
                break;
            }
        }
    });
}

fn start_game_shutdown_monitor(
    app: tauri::AppHandle,
    endpoint: String,
    game_pid: Arc<Mutex<Option<u32>>>,
) {
    thread::spawn(move || {
        let mut connected_once = false;
        let mut missing_since: Option<Instant> = None;

        loop {
            thread::sleep(Duration::from_millis(500));

            if let Some(pid) = current_game_pid(&game_pid) {
                if !is_process_running(pid) {
                    app.exit(0);
                    break;
                }
            }

            if request_local_api_with_timeout(
                &endpoint,
                Some("/health"),
                "",
                Duration::from_millis(350),
                Duration::from_millis(350),
                Duration::from_millis(250),
            )
            .is_ok()
            {
                connected_once = true;
                missing_since = None;
                continue;
            }

            if !connected_once {
                continue;
            }

            let missing_at = missing_since.get_or_insert_with(Instant::now);
            if missing_at.elapsed() >= Duration::from_millis(1500) {
                app.exit(0);
                break;
            }
        }
    });
}

fn window_state_path(app: &tauri::AppHandle) -> Option<PathBuf> {
    app.path()
        .app_data_dir()
        .ok()
        .map(|directory| directory.join(WINDOW_STATE_FILE))
}

fn restore_window_state(window: &WebviewWindow) {
    let Some(path) = window_state_path(window.app_handle()) else {
        return;
    };
    let Ok(content) = fs::read_to_string(path) else {
        return;
    };
    let Some(state) = parse_window_state(&content) else {
        return;
    };

    let width = state.width.max(MIN_WINDOW_WIDTH);
    let height = state.height.max(MIN_WINDOW_HEIGHT);
    let _ = window.set_size(Size::Physical(PhysicalSize::new(width, height)));

    if is_window_state_on_screen(window, state) {
        let _ = window.set_position(Position::Physical(PhysicalPosition::new(state.x, state.y)));
    }
}

fn save_webview_window_state(window: &WebviewWindow) {
    let Ok(position) = window.outer_position() else {
        return;
    };
    let Ok(size) = window.inner_size() else {
        return;
    };
    save_window_state_from_parts(window.app_handle(), position, size);
}

fn save_window_state(window: &Window) {
    let Ok(position) = window.outer_position() else {
        return;
    };
    let Ok(size) = window.inner_size() else {
        return;
    };
    save_window_state_from_parts(window.app_handle(), position, size);
}

fn save_window_state_from_parts(
    app: &tauri::AppHandle,
    position: PhysicalPosition<i32>,
    size: PhysicalSize<u32>,
) {
    if size.width < MIN_WINDOW_WIDTH || size.height < MIN_WINDOW_HEIGHT {
        return;
    }

    let Some(path) = window_state_path(app) else {
        return;
    };
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }

    let content = format!(
        "x={}\ny={}\nwidth={}\nheight={}\n",
        position.x, position.y, size.width, size.height
    );
    let _ = fs::write(path, content);
}

fn parse_window_state(content: &str) -> Option<PersistedWindowState> {
    let mut x = None;
    let mut y = None;
    let mut width = None;
    let mut height = None;

    for line in content.lines() {
        let Some((key, value)) = line.split_once('=') else {
            continue;
        };
        match key.trim() {
            "x" => x = value.trim().parse::<i32>().ok(),
            "y" => y = value.trim().parse::<i32>().ok(),
            "width" => width = value.trim().parse::<u32>().ok(),
            "height" => height = value.trim().parse::<u32>().ok(),
            _ => {}
        }
    }

    Some(PersistedWindowState {
        x: x?,
        y: y?,
        width: width?,
        height: height?,
    })
}

fn is_window_state_on_screen(window: &WebviewWindow, state: PersistedWindowState) -> bool {
    let Ok(monitors) = window.available_monitors() else {
        return true;
    };
    if monitors.is_empty() {
        return true;
    }

    is_state_inside_monitors(state, &monitors)
}

fn is_state_inside_monitors(state: PersistedWindowState, monitors: &[Monitor]) -> bool {
    let center_x = i64::from(state.x) + i64::from(state.width / 2);
    let center_y = i64::from(state.y) + i64::from(state.height / 2);

    monitors.iter().any(|monitor| {
        let position = monitor.position();
        let size = monitor.size();
        let left = i64::from(position.x);
        let top = i64::from(position.y);
        let right = left + i64::from(size.width);
        let bottom = top + i64::from(size.height);
        center_x >= left && center_x <= right && center_y >= top && center_y <= bottom
    })
}

fn setup_tray(app: &mut tauri::App) -> tauri::Result<()> {
    let show = MenuItem::with_id(
        app,
        "show",
        "显示 mystia-steward-companion",
        true,
        None::<&str>,
    )?;
    let reconnect = MenuItem::with_id(app, "reconnect", "重连游戏", true, None::<&str>)?;
    let toggle_passthrough = MenuItem::with_id(
        app,
        "toggle_passthrough",
        mouse_passthrough_tray_label(false),
        true,
        None::<&str>,
    )?;
    let quit = MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&show, &reconnect, &toggle_passthrough, &quit])?;
    if let Ok(mut item) = app.state::<TrayPassthroughMenuState>().0.lock() {
        *item = Some(toggle_passthrough.clone());
    }

    let mut tray = TrayIconBuilder::new()
        .tooltip("mystia-steward-companion")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "show" | "reconnect" => {
                if let Some(state) = app.try_state::<MousePassthroughState>() {
                    show_main_window(app, &state.0);
                } else {
                    show_main_window_without_passthrough_state(app);
                }
            }
            "toggle_passthrough" => {
                if let Some(state) = app.try_state::<MousePassthroughState>() {
                    let enabled = !current_mouse_passthrough(&state.0);
                    let _ = set_mouse_passthrough_internal(app, &state.0, enabled);
                }
            }
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                if let Some(state) = tray.app_handle().try_state::<MousePassthroughState>() {
                    show_main_window(tray.app_handle(), &state.0);
                } else {
                    show_main_window_without_passthrough_state(tray.app_handle());
                }
            }
        });

    if let Some(icon) = app.default_window_icon() {
        tray = tray.icon(icon.clone());
    }

    tray.build(app)?;
    Ok(())
}

fn show_main_window(app: &tauri::AppHandle, mouse_passthrough: &Arc<Mutex<bool>>) {
    let _ = set_mouse_passthrough_internal(app, mouse_passthrough, false);
    show_main_window_without_passthrough_state(app);
}

fn show_main_window_without_passthrough_state(app: &tauri::AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

fn toggle_main_window(
    app: &tauri::AppHandle,
    game_pid: Option<u32>,
    keep_visible_when_focused: bool,
    mouse_passthrough: &Arc<Mutex<bool>>,
) {
    if let Some(window) = app.get_webview_window("main") {
        if window.is_focused().unwrap_or(false) {
            save_webview_window_state(&window);
            if !keep_visible_when_focused {
                let _ = window.hide();
            }
            focus_game_window(game_pid);
            return;
        }
    }

    show_main_window(app, mouse_passthrough);
}

fn main() {
    if notify_existing_instance() {
        return;
    }

    tauri::Builder::default()
        .manage(GamePidState(Arc::new(Mutex::new(launch_game_pid()))))
        .manage(WindowSwitchState(Arc::new(Mutex::new(None))))
        .manage(CompanionPreferenceState(Arc::new(Mutex::new(
            CompanionPreferences::default(),
        ))))
        .manage(MousePassthroughState(Arc::new(Mutex::new(false))))
        .manage(TrayPassthroughMenuState(Arc::new(Mutex::new(None))))
        .setup(|app| {
            setup_tray(app)?;
            if let Some(window) = app.get_webview_window("main") {
                restore_window_state(&window);
            }
            let app_handle = app.handle().clone();
            let game_pid = app.state::<GamePidState>().0.clone();
            let switch_state = app.state::<WindowSwitchState>().0.clone();
            let preferences = app.state::<CompanionPreferenceState>().0.clone();
            let mouse_passthrough = app.state::<MousePassthroughState>().0.clone();
            start_instance_control_server(
                app_handle.clone(),
                game_pid,
                switch_state,
                preferences,
                mouse_passthrough.clone(),
            );
            start_mouse_passthrough_hotkey_monitor(app_handle.clone(), mouse_passthrough);
            start_game_shutdown_monitor(
                app_handle,
                launch_api_endpoint().unwrap_or_else(|| DEFAULT_API_ENDPOINT.to_string()),
                app.state::<GamePidState>().0.clone(),
            );
            Ok(())
        })
        .on_window_event(|window, event| match event {
            WindowEvent::Moved(_) | WindowEvent::Resized(_) => save_window_state(window),
            WindowEvent::CloseRequested { api, .. } => {
                api.prevent_close();
                save_window_state(window);
                let _ = window.hide();
            }
            _ => {}
        })
        .invoke_handler(tauri::generate_handler![
            fetch_snapshot,
            launch_api_endpoint,
            launch_api_token,
            toggle_companion_focus,
            apply_companion_preferences,
            set_mouse_passthrough,
            get_mouse_passthrough
        ])
        .run(tauri::generate_context!())
        .expect("failed to run mystia-steward-companion");
}

#[cfg(target_os = "windows")]
fn focus_game_window(game_pid: Option<u32>) {
    windows_focus::focus_process_window(game_pid);
}

#[cfg(not(target_os = "windows"))]
fn focus_game_window(_game_pid: Option<u32>) {}

#[cfg(target_os = "windows")]
fn is_process_running(pid: u32) -> bool {
    windows_process::is_process_running(pid)
}

#[cfg(not(target_os = "windows"))]
fn is_process_running(pid: u32) -> bool {
    std::path::PathBuf::from(format!("/proc/{pid}")).exists()
}

#[cfg(target_os = "windows")]
mod windows_process {
    use std::ffi::c_void;

    type Bool = i32;
    type Dword = u32;
    type Handle = *mut c_void;

    const PROCESS_QUERY_LIMITED_INFORMATION: Dword = 0x1000;
    const STILL_ACTIVE: Dword = 259;

    pub fn is_process_running(pid: u32) -> bool {
        unsafe {
            let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if handle.is_null() {
                return false;
            }

            let mut exit_code: Dword = 0;
            let ok = GetExitCodeProcess(handle, &mut exit_code as *mut Dword);
            CloseHandle(handle);
            ok != 0 && exit_code == STILL_ACTIVE
        }
    }

    #[link(name = "kernel32")]
    extern "system" {
        fn OpenProcess(dwDesiredAccess: Dword, bInheritHandle: Bool, dwProcessId: Dword) -> Handle;
        fn GetExitCodeProcess(hProcess: Handle, lpExitCode: *mut Dword) -> Bool;
        fn CloseHandle(hObject: Handle) -> Bool;
    }
}

#[cfg(target_os = "windows")]
mod windows_focus {
    use std::ffi::c_void;

    type Bool = i32;
    type Dword = u32;
    type Hwnd = *mut c_void;
    type Lparam = isize;

    const SW_RESTORE: i32 = 9;

    #[repr(C)]
    struct EnumState {
        pid: Dword,
        hwnd: Hwnd,
    }

    pub fn focus_process_window(pid: Option<u32>) {
        let Some(pid) = pid else {
            return;
        };

        let mut state = EnumState {
            pid,
            hwnd: std::ptr::null_mut(),
        };

        unsafe {
            EnumWindows(enum_windows_proc, &mut state as *mut EnumState as Lparam);
            if state.hwnd.is_null() {
                return;
            }

            ShowWindow(state.hwnd, SW_RESTORE);
            SetForegroundWindow(state.hwnd);
        }
    }

    unsafe extern "system" fn enum_windows_proc(hwnd: Hwnd, lparam: Lparam) -> Bool {
        let state = &mut *(lparam as *mut EnumState);
        if IsWindowVisible(hwnd) == 0 {
            return 1;
        }

        let mut window_pid: Dword = 0;
        GetWindowThreadProcessId(hwnd, &mut window_pid);
        if window_pid == state.pid {
            state.hwnd = hwnd;
            return 0;
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
        fn SetForegroundWindow(hWnd: Hwnd) -> Bool;
        fn ShowWindow(hWnd: Hwnd, nCmdShow: i32) -> Bool;
    }
}

#[cfg(target_os = "windows")]
mod windows_hotkey {
    use std::ffi::c_void;

    type Bool = i32;
    type Hwnd = *mut c_void;
    type Uint = u32;
    type Wparam = usize;
    type Lparam = isize;

    const HOTKEY_ID: i32 = 0x4D53;
    const VK_F10: Uint = 0x79;
    const WM_HOTKEY: Uint = 0x0312;

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
        time: u32,
        pt: Point,
    }

    pub fn run_f10_hotkey_loop<F>(mut on_hotkey: F)
    where
        F: FnMut() + Send + 'static,
    {
        unsafe {
            if RegisterHotKey(std::ptr::null_mut(), HOTKEY_ID, 0, VK_F10) == 0 {
                return;
            }

            let mut message = Msg {
                hwnd: std::ptr::null_mut(),
                message: 0,
                w_param: 0,
                l_param: 0,
                time: 0,
                pt: Point { x: 0, y: 0 },
            };

            while GetMessageW(&mut message as *mut Msg, std::ptr::null_mut(), 0, 0) > 0 {
                if message.message == WM_HOTKEY && message.w_param == HOTKEY_ID as usize {
                    on_hotkey();
                }
            }

            UnregisterHotKey(std::ptr::null_mut(), HOTKEY_ID);
        }
    }

    #[link(name = "user32")]
    extern "system" {
        fn RegisterHotKey(hWnd: Hwnd, id: i32, fsModifiers: Uint, vk: Uint) -> Bool;
        fn UnregisterHotKey(hWnd: Hwnd, id: i32) -> Bool;
        fn GetMessageW(
            lpMsg: *mut Msg,
            hWnd: Hwnd,
            wMsgFilterMin: Uint,
            wMsgFilterMax: Uint,
        ) -> Bool;
    }
}
