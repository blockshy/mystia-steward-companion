#![cfg_attr(mobile, allow(dead_code, unused_imports))]

//! Tauri 伴随窗口入口。
//!
//! React 前端负责业务 UI；本库负责桌面能力、Android 移动端入口，以及把 WebView
//! 发来的请求转发到游戏进程内的本地 API。

use serde::Serialize;
use std::io::{self, ErrorKind, Read, Write};
#[cfg(desktop)]
use std::net::TcpListener;
use std::net::{Ipv4Addr, SocketAddr, TcpStream};
use std::sync::{Arc, Mutex};
#[cfg(desktop)]
use std::thread;
use std::time::{Duration, Instant};
#[cfg(desktop)]
use tauri::image::Image;
#[cfg(desktop)]
use tauri::menu::{Menu, MenuItem};
#[cfg(desktop)]
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
#[cfg(desktop)]
use tauri::webview::Color;
#[cfg(not(desktop))]
use tauri::Manager;
#[cfg(desktop)]
use tauri::{Emitter, Manager, WebviewWindow, WindowEvent};
#[cfg(desktop)]
use tauri_plugin_window_state::{StateFlags, WindowExt};

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
const CONNECTION_ACTIVATED_EVENT: &str = "connection-activation-requested";
#[cfg(desktop)]
const TRAY_ICON_BYTES: &[u8] = include_bytes!("../icons/tray-icon.png");
const DEFAULT_WINDOW_SWITCH_COOLDOWN_MS: u64 = 800;
const MIN_WINDOW_SWITCH_COOLDOWN_MS: u64 = 250;
const MAX_WINDOW_SWITCH_COOLDOWN_MS: u64 = 2000;

type LocalApiResult<T> = Result<T, LocalApiError>;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum LocalApiErrorCode {
    InvalidEndpoint,
    InvalidRequest,
    ConnectTimeout,
    ConnectionRefused,
    ConnectFailed,
    ReadTimeout,
    ReadFailed,
    WriteTimeout,
    WriteFailed,
    Unauthorized,
    Forbidden,
    HttpStatus,
    InvalidResponse,
    InternalError,
}

impl LocalApiErrorCode {
    fn as_str(self) -> &'static str {
        match self {
            Self::InvalidEndpoint => "invalid-endpoint",
            Self::InvalidRequest => "invalid-request",
            Self::ConnectTimeout => "connect-timeout",
            Self::ConnectionRefused => "connection-refused",
            Self::ConnectFailed => "connect-failed",
            Self::ReadTimeout => "read-timeout",
            Self::ReadFailed => "read-failed",
            Self::WriteTimeout => "write-timeout",
            Self::WriteFailed => "write-failed",
            Self::Unauthorized => "unauthorized",
            Self::Forbidden => "forbidden",
            Self::HttpStatus => "http-status",
            Self::InvalidResponse => "invalid-response",
            Self::InternalError => "internal-error",
        }
    }
}

#[derive(Debug, Eq, PartialEq)]
struct LocalApiError {
    code: LocalApiErrorCode,
    detail: String,
}

impl LocalApiError {
    fn new(code: LocalApiErrorCode, detail: impl Into<String>) -> Self {
        Self {
            code,
            detail: detail.into(),
        }
    }

    fn encode(&self) -> String {
        let detail = self
            .detail
            .chars()
            .map(|character| match character {
                '\r' | '\n' | '\0' => ' ',
                _ => character,
            })
            .collect::<String>();
        if detail.is_empty() {
            format!("local-api:{}", self.code.as_str())
        } else {
            format!("local-api:{}:{detail}", self.code.as_str())
        }
    }
}

#[derive(Clone, Copy)]
enum LocalApiIoStage {
    Connect,
    Read,
    Write,
}

#[cfg(desktop)]
struct GamePidState(Arc<Mutex<Option<u32>>>);
struct LaunchConnectionState(Arc<Mutex<LaunchConnection>>);
struct WindowSwitchState(Arc<Mutex<WindowSwitchGate>>);
struct CompanionPreferenceState(Arc<Mutex<CompanionPreferences>>);
struct MousePassthroughState(Arc<Mutex<bool>>);
#[cfg(desktop)]
struct TrayPassthroughMenuState(Arc<Mutex<Option<MenuItem<tauri::Wry>>>>);

#[derive(Clone, Copy)]
struct CompanionPreferences {
    keep_visible_when_focused: bool,
    window_switch_cooldown_ms: u64,
}

#[derive(Default)]
struct WindowSwitchGate {
    last_applied_at: Option<Instant>,
    in_flight: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
enum WindowSwitchStatus {
    Applied,
    Throttled,
    Busy,
    NoGamePid,
    FocusFailed,
    ShowFailed,
    HideFailed,
    StateUnavailable,
    #[cfg(not(desktop))]
    Unsupported,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
struct WindowSwitchOutcome {
    status: WindowSwitchStatus,
    applied: bool,
}

impl WindowSwitchOutcome {
    fn rejected(status: WindowSwitchStatus) -> Self {
        Self {
            status,
            applied: false,
        }
    }

    fn applied(status: WindowSwitchStatus) -> Self {
        Self {
            status,
            applied: true,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum WindowSwitchAdmission {
    Started,
    Throttled,
    Busy,
    StateUnavailable,
}

impl Default for CompanionPreferences {
    fn default() -> Self {
        Self {
            keep_visible_when_focused: false,
            window_switch_cooldown_ms: DEFAULT_WINDOW_SWITCH_COOLDOWN_MS,
        }
    }
}

#[derive(Clone, Default)]
struct LaunchConnection {
    endpoint: Option<String>,
    token: Option<String>,
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
    body: Option<String>,
    authority_revision: Option<u64>,
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
        body,
        authority_revision,
        timeout_ms,
        client_id,
        client_label,
    )
    .await
    .map_err(|error| error.encode())
}

async fn request_local_api_with_frontend_timeout_async(
    method: String,
    endpoint: String,
    path_override: Option<String>,
    token: String,
    body: Option<String>,
    authority_revision: Option<u64>,
    timeout_ms: Option<u64>,
    client_id: Option<String>,
    client_label: Option<String>,
) -> LocalApiResult<String> {
    tauri::async_runtime::spawn_blocking(move || {
        request_local_api_with_frontend_timeout(
            &method,
            &endpoint,
            path_override.as_deref(),
            &token,
            body.as_deref(),
            authority_revision,
            timeout_ms,
            client_id.as_deref(),
            client_label.as_deref(),
        )
    })
    .await
    .map_err(|error| {
        LocalApiError::new(
            LocalApiErrorCode::InternalError,
            format!("local api task failed: {error}"),
        )
    })?
}

fn request_local_api_with_frontend_timeout(
    method: &str,
    endpoint: &str,
    path_override: Option<&str>,
    token: &str,
    body: Option<&str>,
    authority_revision: Option<u64>,
    timeout_ms: Option<u64>,
    client_id: Option<&str>,
    client_label: Option<&str>,
) -> LocalApiResult<String> {
    let timeouts = normalize_local_api_timeouts(timeout_ms);
    request_local_api_with_timeout(
        method,
        endpoint,
        path_override,
        token,
        body,
        authority_revision,
        timeouts.connect,
        timeouts.read,
        timeouts.write,
        client_id,
        client_label,
    )
}

#[derive(Debug, PartialEq, Eq)]
struct LocalApiTimeouts {
    connect: Duration,
    read: Duration,
    write: Duration,
}

fn normalize_local_api_timeouts(timeout_ms: Option<u64>) -> LocalApiTimeouts {
    let read_ms = timeout_ms.unwrap_or(1800).clamp(300, 60_000);
    LocalApiTimeouts {
        connect: Duration::from_millis(read_ms.min(5_000)),
        read: Duration::from_millis(read_ms),
        write: Duration::from_millis(read_ms.min(1_200)),
    }
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
    body: Option<&str>,
    authority_revision: Option<u64>,
    connect_timeout: Duration,
    read_timeout: Duration,
    write_timeout: Duration,
    client_id: Option<&str>,
    client_label: Option<&str>,
) -> LocalApiResult<String> {
    let target = LocalApiTarget::parse(&endpoint)?;
    let path = path_override.unwrap_or(&target.path);
    let method = normalize_http_method(method)?;
    let body = body.unwrap_or("");
    if method == "GET" && !body.is_empty() {
        return Err(LocalApiError::new(
            LocalApiErrorCode::InvalidRequest,
            "GET requests cannot contain a body",
        ));
    }
    if body.len() > 65_536 {
        return Err(LocalApiError::new(
            LocalApiErrorCode::InvalidRequest,
            "local API request body exceeds 64 KiB",
        ));
    }
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
        .map_err(|error| map_local_api_io_error(LocalApiIoStage::Connect, error))?;

    stream
        .set_read_timeout(Some(read_timeout))
        .map_err(|error| {
            LocalApiError::new(
                LocalApiErrorCode::ReadFailed,
                format!("set read timeout failed: {error}"),
            )
        })?;
    stream
        .set_write_timeout(Some(write_timeout))
        .map_err(|error| {
            LocalApiError::new(
                LocalApiErrorCode::WriteFailed,
                format!("set write timeout failed: {error}"),
            )
        })?;

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
    let authority_revision_header = authority_revision
        .filter(|value| *value > 0)
        .map(|value| format!("X-Mystia-Steward-Companion-Authority-Revision: {value}\r\n"))
        .unwrap_or_default();
    let content_type_header = if body.is_empty() {
        String::new()
    } else {
        "Content-Type: application/json; charset=utf-8\r\n".to_string()
    };
    let request = format!(
        "{} {} HTTP/1.1\r\nHost: {}:{}\r\n{}{}{}{}{}Connection: close\r\nCache-Control: no-store\r\nContent-Length: {}\r\n\r\n{}",
        method,
        path,
        target.host,
        target.port,
        auth_header,
        client_id_header,
        client_label_header,
        authority_revision_header,
        content_type_header,
        body.len(),
        body,
    );
    stream
        .write_all(request.as_bytes())
        .map_err(|error| map_local_api_io_error(LocalApiIoStage::Write, error))?;

    let mut response = String::new();
    stream
        .read_to_string(&mut response)
        .map_err(|error| map_local_api_io_error(LocalApiIoStage::Read, error))?;

    parse_http_response_body(&response)
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
) -> WindowSwitchOutcome {
    let preferences = current_companion_preferences(&preference_state.0);
    request_window_switch(
        &app,
        current_game_pid(&game_pid_state.0),
        keep_visible_when_focused.unwrap_or(preferences.keep_visible_when_focused),
        &mouse_passthrough_state.0,
        &switch_state.0,
        window_switch_cooldown_ms.unwrap_or(preferences.window_switch_cooldown_ms),
    )
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
) -> WindowSwitchOutcome {
    WindowSwitchOutcome::rejected(WindowSwitchStatus::Unsupported)
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
    next: &LaunchConnection,
) -> bool {
    if next.endpoint.is_none() && next.token.is_none() {
        return false;
    }

    let Ok(mut current_connection) = current.lock() else {
        return false;
    };
    let mut changed = false;
    if let Some(endpoint) = next.endpoint.as_ref() {
        if current_connection.endpoint.as_ref() != Some(endpoint) {
            current_connection.endpoint = Some(endpoint.clone());
            changed = true;
        }
    }
    if let Some(token) = next.token.as_ref() {
        if current_connection.token.as_ref() != Some(token) {
            current_connection.token = Some(token.clone());
            changed = true;
        }
    }

    changed
}

fn current_launch_connection(current: &Arc<Mutex<LaunchConnection>>) -> LaunchConnection {
    current
        .lock()
        .map(|connection| connection.clone())
        .unwrap_or_default()
}

#[cfg(desktop)]
fn should_emit_connection_activation(
    message: &[u8],
    connection_changed: bool,
    next_connection: &LaunchConnection,
) -> bool {
    !connection_changed
        && next_connection.endpoint.is_some()
        && next_connection.token.is_some()
        && (message.starts_with(CONTROL_SHOW) || message.starts_with(CONTROL_TOGGLE))
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

fn begin_window_switch(
    switch_state: &Arc<Mutex<WindowSwitchGate>>,
    cooldown_ms: u64,
    now: Instant,
) -> WindowSwitchAdmission {
    let Ok(mut gate) = switch_state.lock() else {
        return WindowSwitchAdmission::StateUnavailable;
    };
    if gate.in_flight {
        return WindowSwitchAdmission::Busy;
    }

    let cooldown = Duration::from_millis(normalize_window_switch_cooldown_ms(cooldown_ms));
    if gate
        .last_applied_at
        .is_some_and(|previous| now.duration_since(previous) < cooldown)
    {
        return WindowSwitchAdmission::Throttled;
    }

    gate.in_flight = true;
    WindowSwitchAdmission::Started
}

fn complete_window_switch(
    switch_state: &Arc<Mutex<WindowSwitchGate>>,
    applied: bool,
    now: Instant,
) -> bool {
    let Ok(mut gate) = switch_state.lock() else {
        return false;
    };
    gate.in_flight = false;
    if applied {
        gate.last_applied_at = Some(now);
    }
    true
}

#[cfg(desktop)]
fn request_window_switch(
    app: &tauri::AppHandle,
    game_pid: Option<u32>,
    keep_visible_when_focused: bool,
    mouse_passthrough: &Arc<Mutex<bool>>,
    switch_state: &Arc<Mutex<WindowSwitchGate>>,
    cooldown_ms: u64,
) -> WindowSwitchOutcome {
    let admission = begin_window_switch(switch_state, cooldown_ms, Instant::now());
    let rejection = match admission {
        WindowSwitchAdmission::Started => None,
        WindowSwitchAdmission::Throttled => Some(WindowSwitchStatus::Throttled),
        WindowSwitchAdmission::Busy => Some(WindowSwitchStatus::Busy),
        WindowSwitchAdmission::StateUnavailable => Some(WindowSwitchStatus::StateUnavailable),
    };
    if let Some(status) = rejection {
        return WindowSwitchOutcome::rejected(status);
    }

    let outcome = toggle_main_window(app, game_pid, keep_visible_when_focused, mouse_passthrough);
    if !complete_window_switch(switch_state, outcome.applied, Instant::now()) {
        return WindowSwitchOutcome {
            status: WindowSwitchStatus::StateUnavailable,
            applied: outcome.applied,
        };
    }
    outcome
}

fn normalize_window_switch_cooldown_ms(value: u64) -> u64 {
    value.clamp(MIN_WINDOW_SWITCH_COOLDOWN_MS, MAX_WINDOW_SWITCH_COOLDOWN_MS)
}

#[cfg(any(target_os = "windows", test))]
fn foreground_switch_applied(
    request_accepted: bool,
    foreground_process_id: Option<u32>,
    expected_process_id: u32,
) -> bool {
    request_accepted || foreground_process_id == Some(expected_process_id)
}

#[cfg(desktop)]
fn apply_window_transparent_background(window: &WebviewWindow) {
    let _ = window.set_background_color(Some(Color(0, 0, 0, 0)));
}

#[derive(Debug)]
struct LocalApiTarget {
    host: Ipv4Addr,
    port: u16,
    path: String,
}

impl LocalApiTarget {
    fn parse(input: &str) -> LocalApiResult<Self> {
        let trimmed = input.trim().trim_end_matches('/');
        let without_scheme = if let Some(rest) = trimmed.strip_prefix("http://") {
            rest
        } else if trimmed.starts_with("https://") {
            return Err(invalid_local_api_endpoint(
                "local API only supports http endpoints",
            ));
        } else if trimmed.contains("://") {
            return Err(invalid_local_api_endpoint(
                "invalid local API endpoint scheme",
            ));
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

fn invalid_local_api_endpoint(detail: impl Into<String>) -> LocalApiError {
    LocalApiError::new(LocalApiErrorCode::InvalidEndpoint, detail)
}

fn parse_authority(authority: &str) -> LocalApiResult<(&str, u16)> {
    let (host, port_text) = authority
        .rsplit_once(':')
        .ok_or_else(|| invalid_local_api_endpoint("missing local API port"))?;
    if host.trim().is_empty() {
        return Err(invalid_local_api_endpoint("missing local API host"));
    }
    let port = port_text
        .parse::<u16>()
        .map_err(|_| invalid_local_api_endpoint("invalid local API port"))?;
    Ok((host, port))
}

fn parse_local_api_host(host: &str) -> LocalApiResult<Ipv4Addr> {
    if host.eq_ignore_ascii_case("localhost") {
        return Ok(Ipv4Addr::LOCALHOST);
    }

    let address = host.parse::<Ipv4Addr>().map_err(|_| {
        invalid_local_api_endpoint("local API host must be 127.0.0.1 or a private LAN IPv4 address")
    })?;
    if address == Ipv4Addr::UNSPECIFIED {
        return Err(invalid_local_api_endpoint(
            "0.0.0.0 is a bind address and cannot be used as a connection endpoint",
        ));
    }
    if address.is_loopback() || address.is_private() || address.is_link_local() {
        return Ok(address);
    }

    Err(invalid_local_api_endpoint(
        "only loopback or private LAN IPv4 endpoints are allowed",
    ))
}

fn validate_http_fragment(value: &str, label: &str) -> LocalApiResult<()> {
    if value.contains('\r') || value.contains('\n') {
        return Err(LocalApiError::new(
            LocalApiErrorCode::InvalidRequest,
            format!("invalid {label}"),
        ));
    }

    Ok(())
}

fn normalize_http_method(method: &str) -> LocalApiResult<&'static str> {
    if method.eq_ignore_ascii_case("GET") {
        return Ok("GET");
    }
    if method.eq_ignore_ascii_case("POST") {
        return Ok("POST");
    }
    Err(LocalApiError::new(
        LocalApiErrorCode::InvalidRequest,
        format!("unsupported local api method: {method}"),
    ))
}

fn map_local_api_io_error(stage: LocalApiIoStage, error: io::Error) -> LocalApiError {
    let code = match (stage, error.kind()) {
        (LocalApiIoStage::Connect, ErrorKind::TimedOut | ErrorKind::WouldBlock) => {
            LocalApiErrorCode::ConnectTimeout
        }
        (LocalApiIoStage::Connect, ErrorKind::ConnectionRefused) => {
            LocalApiErrorCode::ConnectionRefused
        }
        (LocalApiIoStage::Connect, _) => LocalApiErrorCode::ConnectFailed,
        (LocalApiIoStage::Read, ErrorKind::TimedOut | ErrorKind::WouldBlock) => {
            LocalApiErrorCode::ReadTimeout
        }
        (LocalApiIoStage::Read, ErrorKind::InvalidData) => LocalApiErrorCode::InvalidResponse,
        (LocalApiIoStage::Read, _) => LocalApiErrorCode::ReadFailed,
        (LocalApiIoStage::Write, ErrorKind::TimedOut | ErrorKind::WouldBlock) => {
            LocalApiErrorCode::WriteTimeout
        }
        (LocalApiIoStage::Write, _) => LocalApiErrorCode::WriteFailed,
    };
    LocalApiError::new(code, error.to_string())
}

fn invalid_http_response(detail: impl Into<String>) -> LocalApiError {
    LocalApiError::new(LocalApiErrorCode::InvalidResponse, detail)
}

fn parse_http_response_body(response: &str) -> LocalApiResult<String> {
    let (head, body) = response
        .split_once("\r\n\r\n")
        .ok_or_else(|| invalid_http_response("missing HTTP header terminator"))?;
    let mut lines = head.split("\r\n");
    let status_line = lines
        .next()
        .ok_or_else(|| invalid_http_response("missing HTTP status line"))?;
    let mut status_parts = status_line.split_ascii_whitespace();
    let version = status_parts
        .next()
        .ok_or_else(|| invalid_http_response("missing HTTP version"))?;
    if version != "HTTP/1.0" && version != "HTTP/1.1" {
        return Err(invalid_http_response("unsupported HTTP version"));
    }
    let status_code = status_parts
        .next()
        .ok_or_else(|| invalid_http_response("missing HTTP status code"))?
        .parse::<u16>()
        .map_err(|_| invalid_http_response("invalid HTTP status code"))?;
    if !(100..=599).contains(&status_code) {
        return Err(invalid_http_response("HTTP status code is out of range"));
    }

    let mut content_length = None;
    for line in lines {
        let (name, value) = line
            .split_once(':')
            .ok_or_else(|| invalid_http_response("malformed HTTP header"))?;
        if name.trim().is_empty() {
            return Err(invalid_http_response("empty HTTP header name"));
        }
        if name.eq_ignore_ascii_case("content-length") {
            let parsed = value
                .trim()
                .parse::<usize>()
                .map_err(|_| invalid_http_response("invalid Content-Length"))?;
            if content_length.replace(parsed).is_some() {
                return Err(invalid_http_response("duplicate Content-Length"));
            }
        }
        if name.eq_ignore_ascii_case("transfer-encoding")
            && !value.trim().eq_ignore_ascii_case("identity")
        {
            return Err(invalid_http_response("unsupported HTTP transfer encoding"));
        }
    }
    if content_length.is_some_and(|expected| expected != body.len()) {
        return Err(invalid_http_response("HTTP body length does not match"));
    }

    match status_code {
        200 => Ok(body.to_string()),
        401 => Err(LocalApiError::new(
            LocalApiErrorCode::Unauthorized,
            local_api_http_error_detail(status_code, body),
        )),
        403 => Err(LocalApiError::new(
            LocalApiErrorCode::Forbidden,
            local_api_http_error_detail(status_code, body),
        )),
        _ => Err(LocalApiError::new(
            LocalApiErrorCode::HttpStatus,
            local_api_http_error_detail(status_code, body),
        )),
    }
}

fn local_api_http_error_detail(status_code: u16, body: &str) -> String {
    serde_json::from_str::<serde_json::Value>(body)
        .ok()
        .and_then(|value| value.get("error")?.as_str().map(str::trim).map(str::to_string))
        .filter(|value| !value.is_empty())
        .unwrap_or_else(|| status_code.to_string())
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
fn claim_instance_control_listener() -> Option<TcpListener> {
    let address = SocketAddr::from((Ipv4Addr::LOCALHOST, CONTROL_PORT));
    match TcpListener::bind(address) {
        Ok(listener) => Some(listener),
        Err(error) if error.kind() == ErrorKind::AddrInUse => {
            if !notify_existing_instance() {
                eprintln!("companion control port is owned but the existing instance could not be notified");
            }
            None
        }
        Err(error) => {
            eprintln!("failed to claim companion control port: {error}");
            None
        }
    }
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
    listener: TcpListener,
    app: tauri::AppHandle,
    game_pid: Arc<Mutex<Option<u32>>>,
    connection_state: Arc<Mutex<LaunchConnection>>,
    switch_state: Arc<Mutex<WindowSwitchGate>>,
    preferences: Arc<Mutex<CompanionPreferences>>,
    mouse_passthrough: Arc<Mutex<bool>>,
) {
    thread::spawn(move || {
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
            let next_connection = parse_control_launch_connection(message);
            let connection_changed = update_launch_connection(&connection_state, &next_connection);
            if connection_changed {
                let _ = app.emit(CONNECTION_UPDATED_EVENT, true);
            } else if should_emit_connection_activation(
                message,
                connection_changed,
                &next_connection,
            ) {
                let _ = app.emit(CONNECTION_ACTIVATED_EVENT, true);
            }
            if message.starts_with(CONTROL_SHOW) {
                show_main_window(&app, &mouse_passthrough);
            } else if message.starts_with(CONTROL_TOGGLE) {
                let preferences = current_companion_preferences(&preferences);
                let outcome = request_window_switch(
                    &app,
                    current_game_pid(&game_pid),
                    preferences.keep_visible_when_focused,
                    &mouse_passthrough,
                    &switch_state,
                    preferences.window_switch_cooldown_ms,
                );
                if !matches!(
                    outcome.status,
                    WindowSwitchStatus::Applied
                        | WindowSwitchStatus::Busy
                        | WindowSwitchStatus::Throttled
                ) {
                    eprintln!(
                        "companion window focus switch rejected: {:?}",
                        outcome.status
                    );
                }
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
                None,
                None,
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
fn persisted_window_state_flags() -> StateFlags {
    // Hidden and minimized are transient states; every launch must remain discoverable.
    StateFlags::SIZE | StateFlags::POSITION | StateFlags::MAXIMIZED
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

    if let Ok(icon) = Image::from_bytes(TRAY_ICON_BYTES) {
        tray = tray.icon(icon);
    } else if let Some(icon) = app.default_window_icon() {
        tray = tray.icon(icon.clone());
    }

    tray.build(app)?;
    Ok(())
}

#[cfg(desktop)]
fn show_main_window(app: &tauri::AppHandle, mouse_passthrough: &Arc<Mutex<bool>>) -> bool {
    if let Err(error) = set_mouse_passthrough_internal(app, mouse_passthrough, false) {
        eprintln!("failed to disable mouse passthrough while showing companion: {error}");
    }
    show_main_window_without_passthrough_state(app)
}

#[cfg(desktop)]
fn show_main_window_without_passthrough_state(app: &tauri::AppHandle) -> bool {
    let Some(window) = app.get_webview_window("main") else {
        return false;
    };

    #[cfg(target_os = "windows")]
    {
        let Ok(hwnd) = window.hwnd() else {
            return false;
        };
        return windows_focus::show_and_focus_window(hwnd.0);
    }

    #[cfg(not(target_os = "windows"))]
    {
        let shown = window.show().is_ok();
        let restored = match window.is_minimized() {
            Ok(true) => window.unminimize().is_ok(),
            Ok(false) => true,
            Err(_) => false,
        };
        let focused = window.set_focus().is_ok();
        shown && restored && focused
    }
}

#[cfg(desktop)]
fn is_main_window_focused(window: &WebviewWindow) -> Option<bool> {
    #[cfg(target_os = "windows")]
    {
        return window
            .hwnd()
            .ok()
            .map(|hwnd| windows_focus::is_foreground_window(hwnd.0));
    }

    #[cfg(not(target_os = "windows"))]
    {
        window.is_focused().ok()
    }
}

#[cfg(desktop)]
fn hide_main_window(window: &WebviewWindow) -> bool {
    #[cfg(target_os = "windows")]
    {
        return window
            .hwnd()
            .ok()
            .is_some_and(|hwnd| windows_focus::hide_window(hwnd.0));
    }

    #[cfg(not(target_os = "windows"))]
    {
        window.hide().is_ok()
    }
}

#[cfg(desktop)]
fn toggle_main_window(
    app: &tauri::AppHandle,
    game_pid: Option<u32>,
    keep_visible_when_focused: bool,
    mouse_passthrough: &Arc<Mutex<bool>>,
) -> WindowSwitchOutcome {
    let Some(window) = app.get_webview_window("main") else {
        return WindowSwitchOutcome::rejected(WindowSwitchStatus::ShowFailed);
    };
    let Some(companion_focused) = is_main_window_focused(&window) else {
        return WindowSwitchOutcome::rejected(WindowSwitchStatus::FocusFailed);
    };

    if companion_focused {
        let Some(game_pid) = game_pid else {
            return WindowSwitchOutcome::rejected(WindowSwitchStatus::NoGamePid);
        };
        if !focus_game_window(game_pid) {
            return WindowSwitchOutcome::rejected(WindowSwitchStatus::FocusFailed);
        }
        if !keep_visible_when_focused && !hide_main_window(&window) {
            return WindowSwitchOutcome::applied(WindowSwitchStatus::HideFailed);
        }
        return WindowSwitchOutcome::applied(WindowSwitchStatus::Applied);
    }

    if show_main_window(app, mouse_passthrough) {
        WindowSwitchOutcome::applied(WindowSwitchStatus::Applied)
    } else {
        WindowSwitchOutcome::rejected(WindowSwitchStatus::ShowFailed)
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    #[cfg(desktop)]
    let Some(instance_control_listener) = claim_instance_control_listener() else {
        return;
    };

    let launch_connection = Arc::new(Mutex::new(launch_connection_from_args()));

    let builder = tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .manage(LaunchConnectionState(launch_connection.clone()))
        .manage(WindowSwitchState(Arc::new(Mutex::new(
            WindowSwitchGate::default(),
        ))))
        .manage(CompanionPreferenceState(Arc::new(Mutex::new(
            CompanionPreferences::default(),
        ))))
        .manage(MousePassthroughState(Arc::new(Mutex::new(false))));

    #[cfg(desktop)]
    let builder = builder
        .manage(GamePidState(Arc::new(Mutex::new(launch_game_pid()))))
        .manage(TrayPassthroughMenuState(Arc::new(Mutex::new(None))))
        .plugin(
            tauri_plugin_window_state::Builder::default()
                .with_state_flags(persisted_window_state_flags())
                .skip_initial_state("main")
                .build(),
        );

    #[cfg(desktop)]
    let builder = builder.setup(move |app| {
        setup_tray(app)?;
        if let Some(window) = app.get_webview_window("main") {
            let _ = window.restore_state(persisted_window_state_flags());
            apply_window_transparent_background(&window);
            let _ = window.show();
            let _ = window.set_focus();
        }
        let app_handle = app.handle().clone();
        let game_pid = app.state::<GamePidState>().0.clone();
        let connection_state = app.state::<LaunchConnectionState>().0.clone();
        let switch_state = app.state::<WindowSwitchState>().0.clone();
        let preferences = app.state::<CompanionPreferenceState>().0.clone();
        let mouse_passthrough = app.state::<MousePassthroughState>().0.clone();
        start_instance_control_server(
            instance_control_listener,
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
        WindowEvent::CloseRequested { api, .. } => {
            api.prevent_close();
            let _ = window.hide();
        }
        _ => {}
    });

    builder
        .invoke_handler(tauri::generate_handler![
            request_local_api,
            launch_api_endpoint,
            launch_api_token,
            toggle_companion_focus,
            apply_companion_preferences,
            set_mouse_passthrough,
            get_mouse_passthrough,
            companion_platform
        ])
        .run(tauri::generate_context!())
        .expect("failed to run mystia-steward-companion");
}

#[cfg(target_os = "windows")]
fn focus_game_window(game_pid: u32) -> bool {
    windows_focus::focus_process_window(game_pid)
}

#[cfg(not(target_os = "windows"))]
fn focus_game_window(_game_pid: u32) -> bool {
    false
}

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
    use super::foreground_switch_applied;
    use std::ffi::c_void;

    type Bool = i32;
    type Dword = u32;
    type Hwnd = *mut c_void;
    type Lparam = isize;

    const SW_RESTORE: i32 = 9;
    const SW_SHOW: i32 = 5;
    const SW_HIDE: i32 = 0;

    #[repr(C)]
    struct EnumState {
        pid: Dword,
        hwnd: Hwnd,
    }

    pub fn focus_process_window(pid: u32) -> bool {
        let mut state = EnumState {
            pid,
            hwnd: std::ptr::null_mut(),
        };

        unsafe {
            EnumWindows(enum_windows_proc, &mut state as *mut EnumState as Lparam);
            if state.hwnd.is_null() {
                return false;
            }

            if IsIconic(state.hwnd) != 0 {
                ShowWindow(state.hwnd, SW_RESTORE);
            }
            let request_accepted = SetForegroundWindow(state.hwnd) != 0;
            foreground_switch_applied(request_accepted, foreground_process_id(), pid)
        }
    }

    pub fn show_and_focus_window(hwnd: Hwnd) -> bool {
        if hwnd.is_null() {
            return false;
        }

        let Some(process_id) = window_process_id(hwnd) else {
            return false;
        };

        unsafe {
            ShowWindow(
                hwnd,
                if IsIconic(hwnd) != 0 {
                    SW_RESTORE
                } else {
                    SW_SHOW
                },
            );
            let request_accepted = SetForegroundWindow(hwnd) != 0;
            IsWindowVisible(hwnd) != 0
                && foreground_switch_applied(request_accepted, foreground_process_id(), process_id)
        }
    }

    pub fn is_foreground_window(hwnd: Hwnd) -> bool {
        !hwnd.is_null() && unsafe { GetForegroundWindow() == hwnd }
    }

    pub fn hide_window(hwnd: Hwnd) -> bool {
        if hwnd.is_null() {
            return false;
        }

        unsafe {
            ShowWindow(hwnd, SW_HIDE);
            IsWindowVisible(hwnd) == 0
        }
    }

    fn foreground_process_id() -> Option<u32> {
        unsafe { window_process_id(GetForegroundWindow()) }
    }

    fn window_process_id(hwnd: Hwnd) -> Option<u32> {
        if hwnd.is_null() {
            return None;
        }

        unsafe {
            let mut process_id: Dword = 0;
            if GetWindowThreadProcessId(hwnd, &mut process_id) == 0 || process_id == 0 {
                return None;
            }
            Some(process_id)
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
        fn IsIconic(hWnd: Hwnd) -> Bool;
        fn IsWindowVisible(hWnd: Hwnd) -> Bool;
        fn GetForegroundWindow() -> Hwnd;
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

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(desktop)]
    #[test]
    fn persists_restorable_window_geometry_without_hidden_or_minimized_startup_state() {
        let flags = persisted_window_state_flags();

        assert!(flags.contains(StateFlags::SIZE));
        assert!(flags.contains(StateFlags::POSITION));
        assert!(flags.contains(StateFlags::MAXIMIZED));
        assert!(!flags.contains(StateFlags::VISIBLE));
        assert!(!flags.contains(StateFlags::DECORATIONS));
        assert!(!flags.contains(StateFlags::FULLSCREEN));
    }

    #[test]
    fn window_switch_gate_commits_cooldown_only_after_applied_switch() {
        let state = Arc::new(Mutex::new(WindowSwitchGate::default()));
        let started_at = Instant::now();

        assert_eq!(
            begin_window_switch(&state, 800, started_at),
            WindowSwitchAdmission::Started
        );
        assert_eq!(
            begin_window_switch(&state, 800, started_at),
            WindowSwitchAdmission::Busy
        );
        assert!(complete_window_switch(&state, false, started_at));
        assert_eq!(
            begin_window_switch(&state, 800, started_at),
            WindowSwitchAdmission::Started
        );
        assert!(complete_window_switch(&state, true, started_at));

        assert_eq!(
            begin_window_switch(&state, 800, started_at + Duration::from_millis(799)),
            WindowSwitchAdmission::Throttled
        );
        assert_eq!(
            begin_window_switch(&state, 800, started_at + Duration::from_millis(800)),
            WindowSwitchAdmission::Started
        );
        assert!(complete_window_switch(
            &state,
            true,
            started_at + Duration::from_millis(800)
        ));
    }

    #[test]
    fn foreground_switch_uses_win32_result_or_target_process_identity() {
        const TARGET_PROCESS_ID: u32 = 42;

        assert!(foreground_switch_applied(true, None, TARGET_PROCESS_ID));
        assert!(foreground_switch_applied(
            false,
            Some(TARGET_PROCESS_ID),
            TARGET_PROCESS_ID,
        ));
        assert!(foreground_switch_applied(true, Some(99), TARGET_PROCESS_ID));
        assert!(!foreground_switch_applied(
            false,
            Some(99),
            TARGET_PROCESS_ID,
        ));
        assert!(!foreground_switch_applied(false, None, TARGET_PROCESS_ID));
    }

    #[test]
    fn window_switch_gate_fails_closed_when_state_is_poisoned() {
        let state = Arc::new(Mutex::new(WindowSwitchGate::default()));
        let poisoned = state.clone();
        let _ = std::thread::spawn(move || {
            let _guard = poisoned.lock().unwrap();
            panic!("poison switch state");
        })
        .join();

        assert_eq!(
            begin_window_switch(&state, 800, Instant::now()),
            WindowSwitchAdmission::StateUnavailable
        );
    }

    #[test]
    fn launch_connection_updates_only_when_identity_changes() {
        let current = Arc::new(Mutex::new(LaunchConnection {
            endpoint: Some("http://127.0.0.1:32145".to_string()),
            token: Some("stable-token".to_string()),
        }));

        assert!(!update_launch_connection(
            &current,
            &LaunchConnection {
                endpoint: Some("http://127.0.0.1:32145".to_string()),
                token: Some("stable-token".to_string()),
            },
        ));
        assert!(!update_launch_connection(
            &current,
            &LaunchConnection {
                endpoint: Some("http://127.0.0.1:32145".to_string()),
                token: None,
            },
        ));
        assert!(update_launch_connection(
            &current,
            &LaunchConnection {
                endpoint: None,
                token: Some("next-token".to_string()),
            },
        ));

        let updated = current_launch_connection(&current);
        assert_eq!(updated.endpoint.as_deref(), Some("http://127.0.0.1:32145"));
        assert_eq!(updated.token.as_deref(), Some("next-token"));
    }

    #[cfg(desktop)]
    #[test]
    fn identical_show_or_toggle_requests_connection_activation() {
        let empty = LaunchConnection::default();
        let endpoint_only = LaunchConnection {
            endpoint: Some("http://127.0.0.1:32145".to_string()),
            token: None,
        };
        let token_only = LaunchConnection {
            endpoint: None,
            token: Some("stable-token".to_string()),
        };
        let complete = LaunchConnection {
            endpoint: Some("http://127.0.0.1:32145".to_string()),
            token: Some("stable-token".to_string()),
        };

        assert!(!should_emit_connection_activation(
            CONTROL_SHOW,
            false,
            &empty
        ));
        assert!(!should_emit_connection_activation(
            CONTROL_SHOW,
            false,
            &endpoint_only,
        ));
        assert!(!should_emit_connection_activation(
            CONTROL_SHOW,
            false,
            &token_only,
        ));
        assert!(should_emit_connection_activation(
            CONTROL_SHOW,
            false,
            &complete
        ));
        assert!(should_emit_connection_activation(
            CONTROL_TOGGLE,
            false,
            &complete,
        ));
        assert!(!should_emit_connection_activation(
            CONTROL_SHOW,
            true,
            &complete
        ));
        assert!(!should_emit_connection_activation(
            CONTROL_EXIT,
            false,
            &complete
        ));
    }

    #[test]
    fn parses_loopback_and_private_lan_endpoints() {
        let loopback = LocalApiTarget::parse("http://localhost:32145").unwrap();
        assert_eq!(loopback.host, Ipv4Addr::LOCALHOST);
        assert_eq!(loopback.port, 32145);
        assert_eq!(loopback.path, "/snapshot");

        let lan = LocalApiTarget::parse("http://192.168.50.12:42145/health?full=1").unwrap();
        assert_eq!(lan.host, Ipv4Addr::new(192, 168, 50, 12));
        assert_eq!(lan.port, 42145);
        assert_eq!(lan.path, "/health?full=1");
    }

    #[test]
    fn rejects_non_local_or_malformed_endpoints_with_stable_code() {
        for endpoint in [
            "https://192.168.1.8:32145",
            "http://8.8.8.8:32145",
            "http://0.0.0.0:32145",
            "http://192.168.1.8",
            "http://localhost:not-a-port",
        ] {
            let error = LocalApiTarget::parse(endpoint).unwrap_err();
            assert_eq!(error.code, LocalApiErrorCode::InvalidEndpoint, "{endpoint}");
            assert!(error.encode().starts_with("local-api:invalid-endpoint"));
        }
    }

    #[test]
    fn parses_valid_http_response_body() {
        let body = r#"{"ok":true}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\r\n{body}",
            body.len()
        );

        assert_eq!(parse_http_response_body(&response).unwrap(), body);
    }

    #[test]
    fn maps_http_statuses_to_stable_error_codes() {
        let cases = [
            (401, LocalApiErrorCode::Unauthorized, "unauthorized"),
            (403, LocalApiErrorCode::Forbidden, "forbidden"),
            (503, LocalApiErrorCode::HttpStatus, "http-status"),
        ];

        for (status, expected_code, encoded_code) in cases {
            let response = format!("HTTP/1.1 {status} Failure\r\nContent-Length: 0\r\n\r\n");
            let error = parse_http_response_body(&response).unwrap_err();
            assert_eq!(error.code, expected_code);
            assert_eq!(error.detail, status.to_string());
            assert_eq!(error.encode(), format!("local-api:{encoded_code}:{status}"));
        }
    }

    #[test]
    fn preserves_local_api_json_error_detail() {
        let body = r#"{"ok":false,"error":"configuration authority changed"}"#;
        let response = format!(
            "HTTP/1.1 409 Conflict\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\r\n{body}",
            body.len(),
        );
        let error = parse_http_response_body(&response).unwrap_err();
        assert_eq!(error.code, LocalApiErrorCode::HttpStatus);
        assert_eq!(error.detail, "configuration authority changed");
    }

    #[test]
    fn rejects_malformed_http_responses_with_stable_code() {
        for response in [
            "not-http",
            "NOT-HTTP 200 OK\r\n\r\n{}",
            "HTTP/1.1 nope OK\r\n\r\n{}",
            "HTTP/1.1 200 OK\r\nBroken-Header\r\n\r\n{}",
            "HTTP/1.1 200 OK\r\nContent-Length: 99\r\n\r\n{}",
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n",
        ] {
            let error = parse_http_response_body(response).unwrap_err();
            assert_eq!(error.code, LocalApiErrorCode::InvalidResponse, "{response}");
            assert!(error.encode().starts_with("local-api:invalid-response"));
        }
    }

    #[test]
    fn maps_io_stages_to_stable_error_codes() {
        let cases = [
            (
                LocalApiIoStage::Connect,
                ErrorKind::TimedOut,
                LocalApiErrorCode::ConnectTimeout,
            ),
            (
                LocalApiIoStage::Connect,
                ErrorKind::ConnectionRefused,
                LocalApiErrorCode::ConnectionRefused,
            ),
            (
                LocalApiIoStage::Connect,
                ErrorKind::NetworkUnreachable,
                LocalApiErrorCode::ConnectFailed,
            ),
            (
                LocalApiIoStage::Read,
                ErrorKind::WouldBlock,
                LocalApiErrorCode::ReadTimeout,
            ),
            (
                LocalApiIoStage::Read,
                ErrorKind::UnexpectedEof,
                LocalApiErrorCode::ReadFailed,
            ),
            (
                LocalApiIoStage::Read,
                ErrorKind::InvalidData,
                LocalApiErrorCode::InvalidResponse,
            ),
            (
                LocalApiIoStage::Write,
                ErrorKind::TimedOut,
                LocalApiErrorCode::WriteTimeout,
            ),
            (
                LocalApiIoStage::Write,
                ErrorKind::BrokenPipe,
                LocalApiErrorCode::WriteFailed,
            ),
        ];

        for (stage, kind, expected_code) in cases {
            let error = map_local_api_io_error(stage, io::Error::from(kind));
            assert_eq!(error.code, expected_code);
            assert!(error
                .encode()
                .starts_with(&format!("local-api:{}", expected_code.as_str())));
        }
    }

    #[test]
    fn keeps_local_api_stage_timeouts_independently_bounded() {
        assert_eq!(
            normalize_local_api_timeouts(None),
            LocalApiTimeouts {
                connect: Duration::from_millis(1_800),
                read: Duration::from_millis(1_800),
                write: Duration::from_millis(1_200),
            }
        );
        assert_eq!(
            normalize_local_api_timeouts(Some(8_000)),
            LocalApiTimeouts {
                connect: Duration::from_millis(5_000),
                read: Duration::from_millis(8_000),
                write: Duration::from_millis(1_200),
            }
        );
        assert_eq!(
            normalize_local_api_timeouts(Some(600_000)),
            LocalApiTimeouts {
                connect: Duration::from_millis(5_000),
                read: Duration::from_millis(60_000),
                write: Duration::from_millis(1_200),
            }
        );
        assert_eq!(
            normalize_local_api_timeouts(Some(1)),
            LocalApiTimeouts {
                connect: Duration::from_millis(300),
                read: Duration::from_millis(300),
                write: Duration::from_millis(300),
            }
        );
    }
}
