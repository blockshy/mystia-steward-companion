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

/// 解析命令行参数并执行一次安装流程。
///
/// 失败时会尽量把错误写入 `--status-file`，供下一次 Mod 启动后在设置页展示。
fn main() {
    let args = parse_args(env::args().skip(1).collect());
    let status_file = get_path(&args, "status-file").unwrap_or_else(|| PathBuf::from("update-status.json"));
    let result = run(&args, &status_file);
    if let Err(error) = result {
        write_status(&status_file, "failed", &error);
        eprintln!("{error}");
        process::exit(1);
    }
}

/// 执行完整的退出等待、备份、替换和最终校验流程。
///
/// 参数由 Mod 的 `UpdateService.InstallOnExit` 传入；调用方必须保证 staged 目录已经通过
/// zip 路径安全检查和 SHA256 校验。本函数仍会重新检查最小文件集合，防止暂存目录被外部修改。
fn run(args: &HashMap<String, String>, status_file: &Path) -> Result<(), String> {
    let game_pid = get_u32(args, "game-pid").ok_or("missing --game-pid")?;
    let plugin_dir = get_path(args, "plugin-dir").ok_or("missing --plugin-dir")?;
    let staged_dir = get_path(args, "staged-dir").ok_or("missing --staged-dir")?;
    let backup_dir = get_path(args, "backup-dir").ok_or("missing --backup-dir")?;
    let control_port = get_u16(args, "control-port").unwrap_or(DEFAULT_CONTROL_PORT);
    let wait_timeout = Duration::from_secs(
        get_u64(args, "wait-timeout-seconds").unwrap_or(DEFAULT_WAIT_TIMEOUT_SECONDS),
    );

    write_status(status_file, "waiting", "waiting for game and companion to exit");
    notify_companion_exit(control_port);
    wait_for_game_exit(game_pid, wait_timeout)?;
    notify_companion_exit(control_port);
    thread::sleep(Duration::from_millis(700));

    validate_plugin_path(&plugin_dir)?;
    validate_staged_package(&staged_dir)?;
    prepare_parent(&backup_dir)?;

    write_status(status_file, "installing", "replacing plugin directory");
    replace_plugin_directory(&plugin_dir, &staged_dir, &backup_dir)?;
    validate_staged_package(&plugin_dir)?;

    write_status(status_file, "succeeded", "update installed");
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
    args.get(key).filter(|value| !value.trim().is_empty()).map(PathBuf::from)
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
fn wait_for_game_exit(pid: u32, timeout: Duration) -> Result<(), String> {
    let started = Instant::now();
    while started.elapsed() < timeout {
        if !is_process_running(pid) {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(700));
    }
    Err(format!("timed out waiting for game process {pid} to exit"))
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
        return Err(format!("plugin directory must be absolute: {}", plugin_dir.display()));
    }
    let name = plugin_dir
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or_default();
    if !name.eq_ignore_ascii_case("mystia-steward-companion") {
        return Err(format!("refusing to replace unexpected plugin directory: {}", plugin_dir.display()));
    }
    Ok(())
}

/// 校验暂存目录包含新版本运行所需的关键文件。
fn validate_staged_package(staged_dir: &Path) -> Result<(), String> {
    if !staged_dir.is_dir() {
        return Err(format!("staged package directory does not exist: {}", staged_dir.display()));
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
        Err(format!("staged package is missing {relative}: {}", path.display()))
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
fn replace_plugin_directory(plugin_dir: &Path, staged_dir: &Path, backup_dir: &Path) -> Result<(), String> {
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

    retry_rename(plugin_dir, backup_dir, Duration::from_secs(30))
        .map_err(|error| format!("failed to backup current plugin directory: {error}"))?;

    if let Err(error) = retry_rename(staged_dir, plugin_dir, Duration::from_secs(30)) {
        let restore_result = if backup_dir.exists() {
            fs::rename(backup_dir, plugin_dir)
                .map_err(|restore_error| format!("restore failed after install error: {restore_error}"))
        } else {
            Ok(())
        };
        return Err(match restore_result {
            Ok(()) => format!("failed to install staged package and restored previous version: {error}"),
            Err(restore_error) => format!("failed to install staged package: {error}; {restore_error}"),
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

/// 写入安装状态文件。
///
/// 状态文件是 updater 与下一次 Mod 启动之间的唯一通信方式，因此写入失败不再向外抛出，避免掩盖原始安装错误。
fn write_status(path: &Path, state: &str, message: &str) {
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let payload = format!(
        "{{\n  \"state\": \"{}\",\n  \"message\": \"{}\"\n}}\n",
        escape_json(state),
        escape_json(message)
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
