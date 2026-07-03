#![cfg_attr(mobile, allow(dead_code, unused_imports))]

//! Tauri 伴随窗口入口。
//!
//! React 前端负责业务 UI；本库负责桌面能力、Android 移动端入口，以及把 WebView
//! 发来的请求转发到游戏进程内的本地 API。

use std::fs;
use std::io::{Read, Write};
use std::net::{Ipv4Addr, SocketAddr, TcpStream};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
#[cfg(desktop)]
use std::net::TcpListener;
#[cfg(desktop)]
use std::process::Command;
#[cfg(desktop)]
use std::thread;
#[cfg(desktop)]
use tauri::menu::{Menu, MenuItem};
#[cfg(desktop)]
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
#[cfg(desktop)]
use tauri::webview::Color;
#[cfg(desktop)]
use tauri::{
    Emitter, Manager, Monitor, PhysicalPosition, PhysicalSize, Position, Size, WebviewWindow,
    Window, WindowEvent,
};
#[cfg(not(desktop))]
use tauri::Manager;

/// Mod 本地 API 默认端口；真实值可由游戏启动伴随窗口时通过 `--api=` 覆盖。
const DEFAULT_API_ENDPOINT: &str = "http://127.0.0.1:32145";
#[cfg(desktop)]
/// 伴随窗口控制端口。游戏内 F8、updater 和单实例逻辑都通过该端口发送 show/toggle/exit 消息。
const CONTROL_PORT: u16 = 32146;
#[cfg(desktop)]
const CONTROL_SHOW: &[u8] = b"mystia-steward-companion:show";
#[cfg(desktop)]
const CONTROL_TOGGLE: &[u8] = b"mystia-steward-companion:toggle";
#[cfg(desktop)]
const CONTROL_EXIT: &[u8] = b"mystia-steward-companion:exit";
#[cfg(desktop)]
const CONTROL_MAX_MESSAGE_BYTES: usize = 1024;
#[cfg(desktop)]
const CONNECTION_UPDATED_EVENT: &str = "connection-updated";
#[cfg(desktop)]
const WINDOW_STATE_FILE: &str = "window-state.txt";
#[cfg(desktop)]
const MIN_WINDOW_WIDTH: u32 = 720;
#[cfg(desktop)]
const MIN_WINDOW_HEIGHT: u32 = 520;
const DEFAULT_WINDOW_SWITCH_COOLDOWN_MS: u64 = 800;
const MIN_WINDOW_SWITCH_COOLDOWN_MS: u64 = 250;
const MAX_WINDOW_SWITCH_COOLDOWN_MS: u64 = 2000;
const PROJECT_RELEASES_URL: &str = "https://github.com/blockshy/mystia-steward-companion/releases";

#[cfg(desktop)]
struct GamePidState(Arc<Mutex<Option<u32>>>);
struct LaunchConnectionState(Arc<Mutex<LaunchConnection>>);
struct WindowSwitchState(Arc<Mutex<Option<Instant>>>);
struct CompanionPreferenceState(Arc<Mutex<CompanionPreferences>>);
struct MousePassthroughState(Arc<Mutex<bool>>);
#[cfg(desktop)]
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

#[cfg(desktop)]
#[derive(Clone, Copy)]
struct PersistedWindowState {
    x: i32,
    y: i32,
    width: u32,
    height: u32,
}

#[derive(Clone, Default)]
struct LaunchConnection {
    endpoint: Option<String>,
    token: Option<String>,
}

#[tauri::command]
async fn fetch_snapshot(
    endpoint: String,
    token: String,
    timeout_ms: Option<u64>,
) -> Result<String, String> {
    request_local_api_with_frontend_timeout_async(
        "GET".to_string(),
        endpoint,
        None,
        token,
        timeout_ms,
        None,
        None,
    )
    .await
}

/// Tauri command：为前端代理一次本地 API 请求。
///
/// WebView 环境下直接 `fetch(127.0.0.1)` 容易受到代理、CORS 或平台网络策略影响，因此生产环境统一走
/// Rust 侧 TCP 请求；浏览器开发模式仍由前端直接 fetch mock API。
#[tauri::command]
async fn request_local_api(
    endpoint: String,
    token: String,
    method: Option<String>,
    timeout_ms: Option<u64>,
    client_id: Option<String>,
    client_label: Option<String>,
) -> Result<String, String> {
    let method = method.unwrap_or_else(|| "GET".to_string());
    request_local_api_with_frontend_timeout_async(
        method,
        endpoint,
        None,
        token,
        timeout_ms,
        client_id,
        client_label,
    )
    .await
}

async fn request_local_api_with_frontend_timeout_async(
    method: String,
    endpoint: String,
    path_override: Option<String>,
    token: String,
    timeout_ms: Option<u64>,
    client_id: Option<String>,
    client_label: Option<String>,
) -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(move || {
        request_local_api_with_frontend_timeout(
            &method,
            &endpoint,
            path_override.as_deref(),
            &token,
            timeout_ms,
            client_id.as_deref(),
            client_label.as_deref(),
        )
    })
    .await
    .map_err(|error| format!("local api task failed: {error}"))?
}

fn request_local_api_with_frontend_timeout(
    method: &str,
    endpoint: &str,
    path_override: Option<&str>,
    token: &str,
    timeout_ms: Option<u64>,
    client_id: Option<&str>,
    client_label: Option<&str>,
) -> Result<String, String> {
    let timeout = normalize_local_api_timeout(timeout_ms);
    request_local_api_with_timeout(
        method,
        endpoint,
        path_override,
        token,
        timeout,
        timeout,
        Duration::from_millis(timeout.as_millis().min(1200) as u64),
        client_id,
        client_label,
    )
}

fn normalize_local_api_timeout(timeout_ms: Option<u64>) -> Duration {
    Duration::from_millis(timeout_ms.unwrap_or(1800).clamp(300, 5000))
}

/// 使用最小 HTTP 客户端访问 Mod 本地 API。
///
/// 这里只支持 GET/POST 且不发送请求体，匹配 Mod 侧 `LocalApiServer` 的协议。保持手写 TCP 请求可以避免
/// 为桌面壳引入额外 HTTP 依赖，也能精确控制连接、读取和写入超时。
fn request_local_api_with_timeout(
    method: &str,
    endpoint: &str,
    path_override: Option<&str>,
    token: &str,
    connect_timeout: Duration,
    read_timeout: Duration,
    write_timeout: Duration,
    client_id: Option<&str>,
    client_label: Option<&str>,
) -> Result<String, String> {
    let target = LocalApiTarget::parse(&endpoint)?;
    let path = path_override.unwrap_or(&target.path);
    let method = normalize_http_method(method)?;
    validate_http_fragment(path, "path")?;
    validate_http_fragment(token, "token")?;
    if let Some(value) = client_id {
        validate_http_fragment(value, "client id")?;
    }
    if let Some(value) = client_label {
        validate_http_fragment(value, "client label")?;
    }

    let address = SocketAddr::from((target.host, target.port));
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
    let client_id_header = client_id
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(|value| format!("X-Mystia-Steward-Companion-Client-Id: {value}\r\n"))
        .unwrap_or_default();
    let client_label_header = client_label
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(|value| format!("X-Mystia-Steward-Companion-Client-Label: {value}\r\n"))
        .unwrap_or_default();
    let request = format!(
        "{} {} HTTP/1.1\r\nHost: {}:{}\r\n{}{}{}Connection: close\r\nCache-Control: no-store\r\nContent-Length: 0\r\n\r\n",
        method, path, target.host, target.port, auth_header, client_id_header, client_label_header
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
fn launch_api_endpoint(
    connection_state: tauri::State<'_, LaunchConnectionState>,
) -> Option<String> {
    current_launch_connection(&connection_state.0).endpoint
}

#[tauri::command]
fn launch_api_token(connection_state: tauri::State<'_, LaunchConnectionState>) -> Option<String> {
    current_launch_connection(&connection_state.0).token
}

#[tauri::command]
#[cfg(desktop)]
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
#[cfg(not(desktop))]
fn toggle_companion_focus(
    _app: tauri::AppHandle,
    _switch_state: tauri::State<'_, WindowSwitchState>,
    _preference_state: tauri::State<'_, CompanionPreferenceState>,
    _mouse_passthrough_state: tauri::State<'_, MousePassthroughState>,
    _keep_visible_when_focused: Option<bool>,
    _window_switch_cooldown_ms: Option<u64>,
) {
}

#[tauri::command]
#[cfg(desktop)]
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
        apply_window_transparent_background(&window);
        let _ = window.set_always_on_top(always_on_top);
    }
}

#[tauri::command]
#[cfg(not(desktop))]
fn apply_companion_preferences(
    _app: tauri::AppHandle,
    preference_state: tauri::State<'_, CompanionPreferenceState>,
    keep_visible_when_focused: bool,
    _always_on_top: bool,
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

#[tauri::command]
fn open_external_url(url: String) -> Result<(), String> {
    let url = validate_project_release_url(&url)?;
    open_url_in_system_browser(url)
}

#[tauri::command]
fn companion_platform() -> &'static str {
    if cfg!(desktop) {
        "desktop"
    } else {
        "mobile"
    }
}

#[cfg(desktop)]
fn launch_game_pid() -> Option<u32> {
    std::env::args().find_map(|arg| {
        arg.strip_prefix("--game-pid=")
            .and_then(|value| value.parse::<u32>().ok())
    })
}

fn launch_api_endpoint_arg() -> Option<String> {
    std::env::args().find_map(|arg| arg.strip_prefix("--api=").map(|value| value.to_string()))
}

fn launch_api_token_arg() -> Option<String> {
    std::env::args().find_map(|arg| arg.strip_prefix("--token=").map(|value| value.to_string()))
}

fn launch_connection_from_args() -> LaunchConnection {
    LaunchConnection {
        endpoint: launch_api_endpoint_arg(),
        token: launch_api_token_arg(),
    }
}

#[cfg(desktop)]
fn parse_control_game_pid(message: &[u8]) -> Option<u32> {
    let text = std::str::from_utf8(message).ok()?;
    text.split_whitespace().find_map(|part| {
        part.strip_prefix("--game-pid=")
            .and_then(|value| value.parse::<u32>().ok())
    })
}

#[cfg(desktop)]
fn parse_control_launch_connection(message: &[u8]) -> LaunchConnection {
    let Some(text) = std::str::from_utf8(message).ok() else {
        return LaunchConnection::default();
    };

    let mut connection = LaunchConnection::default();
    for part in text.split_whitespace() {
        if let Some(endpoint) = part.strip_prefix("--api=") {
            if !endpoint.trim().is_empty() {
                connection.endpoint = Some(endpoint.to_string());
            }
            continue;
        }

        if let Some(token) = part.strip_prefix("--token=") {
            if !token.trim().is_empty() {
                connection.token = Some(token.to_string());
            }
        }
    }

    connection
}

fn update_launch_connection(
    current: &Arc<Mutex<LaunchConnection>>,
    next: LaunchConnection,
) -> bool {
    if next.endpoint.is_none() && next.token.is_none() {
        return false;
    }

    let Ok(mut current_connection) = current.lock() else {
        return false;
    };
    if let Some(endpoint) = next.endpoint {
        current_connection.endpoint = Some(endpoint);
    }
    if let Some(token) = next.token {
        current_connection.token = Some(token);
    }

    true
}

fn current_launch_connection(current: &Arc<Mutex<LaunchConnection>>) -> LaunchConnection {
    current
        .lock()
        .map(|connection| connection.clone())
        .unwrap_or_default()
}

#[cfg(desktop)]
fn update_game_pid(game_pid: &Arc<Mutex<Option<u32>>>, next: Option<u32>) {
    let Some(next) = next else {
        return;
    };
    if let Ok(mut current) = game_pid.lock() {
        *current = Some(next);
    }
}

#[cfg(desktop)]
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

#[cfg(desktop)]
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

#[cfg(not(desktop))]
fn set_mouse_passthrough_internal(
    _app: &tauri::AppHandle,
    mouse_passthrough: &Arc<Mutex<bool>>,
    _enabled: bool,
) -> Result<bool, String> {
    if let Ok(mut current) = mouse_passthrough.lock() {
        *current = false;
    }

    Ok(false)
}

#[cfg(desktop)]
fn mouse_passthrough_tray_label(enabled: bool) -> &'static str {
    if enabled {
        "关闭鼠标穿透"
    } else {
        "开启鼠标穿透"
    }
}

#[cfg(desktop)]
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

#[cfg(desktop)]
fn apply_window_transparent_background(window: &WebviewWindow) {
    let _ = window.set_background_color(Some(Color(0, 0, 0, 0)));
}

struct LocalApiTarget {
    host: Ipv4Addr,
    port: u16,
    path: String,
}

impl LocalApiTarget {
    fn parse(input: &str) -> Result<Self, String> {
        let trimmed = input.trim().trim_end_matches('/');
        let without_scheme = if let Some(rest) = trimmed.strip_prefix("http://") {
            rest
        } else if trimmed.starts_with("https://") {
            return Err("local API only supports http endpoints".to_string());
        } else if trimmed.contains("://") {
            return Err("invalid local API endpoint scheme".to_string());
        } else {
            trimmed
        };
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
        let host = parse_local_api_host(host)?;

        Ok(Self {
            host,
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
    if host.trim().is_empty() {
        return Err("missing local API host".to_string());
    }
    let port = port_text
        .parse::<u16>()
        .map_err(|_| "invalid local API port".to_string())?;
    Ok((host, port))
}

fn parse_local_api_host(host: &str) -> Result<Ipv4Addr, String> {
    if host.eq_ignore_ascii_case("localhost") {
        return Ok(Ipv4Addr::LOCALHOST);
    }

    let address = host.parse::<Ipv4Addr>().map_err(|_| {
        "local API host must be 127.0.0.1 or a private LAN IPv4 address".to_string()
    })?;
    if address == Ipv4Addr::UNSPECIFIED {
        return Err(
            "0.0.0.0 is a bind address and cannot be used as a connection endpoint".to_string(),
        );
    }
    if address.is_loopback() || address.is_private() || address.is_link_local() {
        return Ok(address);
    }

    Err("only loopback or private LAN IPv4 endpoints are allowed".to_string())
}

fn validate_http_fragment(value: &str, label: &str) -> Result<(), String> {
    if value.contains('\r') || value.contains('\n') {
        return Err(format!("invalid {label}"));
    }

    Ok(())
}

fn normalize_http_method(method: &str) -> Result<&'static str, String> {
    if method.eq_ignore_ascii_case("GET") {
        return Ok("GET");
    }
    if method.eq_ignore_ascii_case("POST") {
        return Ok("POST");
    }
    Err(format!("unsupported local api method: {method}"))
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

fn validate_project_release_url(url: &str) -> Result<&str, String> {
    let url = url.trim();
    if url.is_empty() || url.contains('\r') || url.contains('\n') || url.contains('\0') {
        return Err("invalid release url".to_string());
    }

    // 该 command 由前端“发布页”按钮调用，只允许打开本项目 GitHub Release，
    // 避免把通用外链打开能力暴露给 WebView 中的任意输入。
    if url == PROJECT_RELEASES_URL
        || url
            .strip_prefix(PROJECT_RELEASES_URL)
            .is_some_and(|suffix| suffix.starts_with('/'))
    {
        return Ok(url);
    }

    Err("only project release urls are allowed".to_string())
}

#[cfg(all(desktop, target_os = "windows"))]
fn open_url_in_system_browser(url: &str) -> Result<(), String> {
    Command::new("cmd")
        .args(["/C", "start", "", url])
        .spawn()
        .map_err(|error| format!("open browser failed: {error}"))?;
    Ok(())
}

#[cfg(all(desktop, target_os = "macos"))]
fn open_url_in_system_browser(url: &str) -> Result<(), String> {
    Command::new("open")
        .arg(url)
        .spawn()
        .map_err(|error| format!("open browser failed: {error}"))?;
    Ok(())
}

#[cfg(all(desktop, not(target_os = "windows"), not(target_os = "macos")))]
fn open_url_in_system_browser(url: &str) -> Result<(), String> {
    Command::new("xdg-open")
        .arg(url)
        .spawn()
        .map_err(|error| format!("open browser failed: {error}"))?;
    Ok(())
}

#[cfg(not(desktop))]
fn open_url_in_system_browser(_url: &str) -> Result<(), String> {
    Err("opening external URLs is not available in the mobile companion build".to_string())
}

#[cfg(desktop)]
fn notify_existing_instance() -> bool {
    let address = SocketAddr::from((Ipv4Addr::LOCALHOST, CONTROL_PORT));
    let Ok(mut stream) = TcpStream::connect_timeout(&address, Duration::from_millis(250)) else {
        return false;
    };

    stream
        .write_all(
            build_control_message(
                "mystia-steward-companion:show",
                launch_game_pid(),
                launch_api_endpoint_arg(),
                launch_api_token_arg(),
            )
            .as_bytes(),
        )
        .is_ok()
}

#[cfg(desktop)]
fn build_control_message(
    command: &str,
    game_pid: Option<u32>,
    endpoint: Option<String>,
    token: Option<String>,
) -> String {
    let mut message = String::from(command);
    message.push('\n');
    if let Some(game_pid) = game_pid {
        message.push_str(&format!("--game-pid={game_pid}\n"));
    }
    if let Some(endpoint) = endpoint {
        message.push_str(&format!("--api={endpoint}\n"));
    }
    if let Some(token) = token {
        message.push_str(&format!("--token={token}\n"));
    }
    message
}

#[cfg(desktop)]
fn start_instance_control_server(
    app: tauri::AppHandle,
    game_pid: Arc<Mutex<Option<u32>>>,
    connection_state: Arc<Mutex<LaunchConnection>>,
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
            let mut buffer = [0u8; CONTROL_MAX_MESSAGE_BYTES];
            let Ok(size) = stream.read(&mut buffer) else {
                continue;
            };
            let message = &buffer[..size];
            update_game_pid(&game_pid, parse_control_game_pid(message));
            if update_launch_connection(&connection_state, parse_control_launch_connection(message))
            {
                let _ = app.emit(CONNECTION_UPDATED_EVENT, true);
            }
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

#[cfg(desktop)]
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
                "GET",
                &endpoint,
                Some("/health"),
                "",
                Duration::from_millis(350),
                Duration::from_millis(350),
                Duration::from_millis(250),
                None,
                None,
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

#[cfg(desktop)]
fn window_state_path(app: &tauri::AppHandle) -> Option<PathBuf> {
    app.path()
        .app_data_dir()
        .ok()
        .map(|directory| directory.join(WINDOW_STATE_FILE))
}

#[cfg(desktop)]
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

#[cfg(desktop)]
fn save_webview_window_state(window: &WebviewWindow) {
    let Ok(position) = window.outer_position() else {
        return;
    };
    let Ok(size) = window.inner_size() else {
        return;
    };
    save_window_state_from_parts(window.app_handle(), position, size);
}

#[cfg(desktop)]
fn save_window_state(window: &Window) {
    let Ok(position) = window.outer_position() else {
        return;
    };
    let Ok(size) = window.inner_size() else {
        return;
    };
    save_window_state_from_parts(window.app_handle(), position, size);
}

#[cfg(desktop)]
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

#[cfg(desktop)]
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

#[cfg(desktop)]
fn is_window_state_on_screen(window: &WebviewWindow, state: PersistedWindowState) -> bool {
    let Ok(monitors) = window.available_monitors() else {
        return true;
    };
    if monitors.is_empty() {
        return true;
    }

    is_state_inside_monitors(state, &monitors)
}

#[cfg(desktop)]
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

#[cfg(desktop)]
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

#[cfg(desktop)]
fn show_main_window(app: &tauri::AppHandle, mouse_passthrough: &Arc<Mutex<bool>>) {
    let _ = set_mouse_passthrough_internal(app, mouse_passthrough, false);
    show_main_window_without_passthrough_state(app);
}

#[cfg(desktop)]
fn show_main_window_without_passthrough_state(app: &tauri::AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

#[cfg(desktop)]
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

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    #[cfg(desktop)]
    if notify_existing_instance() {
        return;
    }

    let launch_connection = Arc::new(Mutex::new(launch_connection_from_args()));

    let builder = tauri::Builder::default()
        .manage(LaunchConnectionState(launch_connection.clone()))
        .manage(WindowSwitchState(Arc::new(Mutex::new(None))))
        .manage(CompanionPreferenceState(Arc::new(Mutex::new(
            CompanionPreferences::default(),
        ))))
        .manage(MousePassthroughState(Arc::new(Mutex::new(false))));

    #[cfg(desktop)]
    let builder = builder
        .manage(GamePidState(Arc::new(Mutex::new(launch_game_pid()))))
        .manage(TrayPassthroughMenuState(Arc::new(Mutex::new(None))));

    #[cfg(desktop)]
    let builder = builder
        .setup(|app| {
            setup_tray(app)?;
            if let Some(window) = app.get_webview_window("main") {
                apply_window_transparent_background(&window);
                restore_window_state(&window);
            }
            let app_handle = app.handle().clone();
            let game_pid = app.state::<GamePidState>().0.clone();
            let connection_state = app.state::<LaunchConnectionState>().0.clone();
            let switch_state = app.state::<WindowSwitchState>().0.clone();
            let preferences = app.state::<CompanionPreferenceState>().0.clone();
            let mouse_passthrough = app.state::<MousePassthroughState>().0.clone();
            start_instance_control_server(
                app_handle.clone(),
                game_pid,
                connection_state.clone(),
                switch_state,
                preferences,
                mouse_passthrough.clone(),
            );
            start_mouse_passthrough_hotkey_monitor(app_handle.clone(), mouse_passthrough);
            start_game_shutdown_monitor(
                app_handle,
                current_launch_connection(&connection_state)
                    .endpoint
                    .unwrap_or_else(|| DEFAULT_API_ENDPOINT.to_string()),
                app.state::<GamePidState>().0.clone(),
            );
            Ok(())
        });

    #[cfg(not(desktop))]
    let builder = builder.setup(|_app| Ok(()));

    #[cfg(desktop)]
    let builder = builder.on_window_event(|window, event| match event {
            WindowEvent::Moved(_) | WindowEvent::Resized(_) => save_window_state(window),
            WindowEvent::CloseRequested { api, .. } => {
                api.prevent_close();
                save_window_state(window);
                let _ = window.hide();
            }
            _ => {}
        });

    builder
        .invoke_handler(tauri::generate_handler![
            fetch_snapshot,
            request_local_api,
            launch_api_endpoint,
            launch_api_token,
            toggle_companion_focus,
            apply_companion_preferences,
            set_mouse_passthrough,
            get_mouse_passthrough,
            open_external_url,
            companion_platform
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
