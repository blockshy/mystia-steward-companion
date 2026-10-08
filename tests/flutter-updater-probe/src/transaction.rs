//! Minimal installation transaction exercised only in a newly owned P0 fixture.
//! Full production recovery/handle-pinned directory operations remain P6 work.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use crate::bundle::{verify_bundle, BundleManifest};
use crate::install_control::InstallControl;
use crate::launch::InstallArguments;
use crate::status::{LegacyInstallState as State, LegacyInstallStatus};
use crate::{paths, ErrorKind, ProbeError, Result, PRODUCT_NAME, UPDATER_FILE_NAME};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InstallOutcome {
    Succeeded,
    Cancelled,
}

/// A known retained process is observed; callers may neither terminate it nor
/// substitute a PID/name lookup when its state cannot be determined.
pub trait ProcessObservation {
    fn exited_zero(&self) -> Result<bool>;
}

pub fn read_fixture_metadata(path: &Path, limit: u64) -> Result<Vec<u8>> {
    use std::io::Read;
    if paths::regular_file(path)?.len() > limit {
        return Err(invalid("fixture metadata is oversized"));
    }
    let mut bytes = Vec::new();
    fs::File::open(path)
        .map_err(io_error)?
        .take(limit.saturating_add(1))
        .read_to_end(&mut bytes)
        .map_err(io_error)?;
    if bytes.len() as u64 > limit {
        return Err(invalid("fixture metadata grew beyond its limit"));
    }
    Ok(bytes)
}

pub fn fixture_root(arguments: &InstallArguments, runner: &Path) -> Result<PathBuf> {
    arguments.validate_runner_location(runner)?;
    let root = runner
        .parent()
        .and_then(Path::parent)
        .ok_or_else(|| invalid("missing fixture root"))?;
    let name = root
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| invalid("invalid fixture root name"))?;
    let id = name
        .strip_prefix("mystia-steward-companion-install-p0-")
        .ok_or_else(|| invalid("installation is restricted to a newly owned install-p0 fixture"))?;
    if id.len() != 32
        || !id
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(invalid(
            "fixture root must have a random 128-bit lowercase identity",
        ));
    }
    let expected = [
        (&arguments.plugin_dir, root.join(PRODUCT_NAME)),
        (&arguments.staged_dir, root.join("staging")),
        (&arguments.backup_dir, root.join("backups/previous")),
        (
            &arguments.status_file,
            root.join("state/install-status.json"),
        ),
    ];
    if runner != root.join("runner").join(UPDATER_FILE_NAME)
        || expected
            .iter()
            .any(|(actual, expected)| *actual != expected)
        || arguments.control_port.get() == 32145
        || arguments.control_port.get() == 32146
    {
        return Err(invalid(
            "fixture paths/isolated control port differ from the fixed layout",
        ));
    }
    paths::directory(root)?;
    Ok(root.to_path_buf())
}

pub fn run_install(
    arguments: &InstallArguments,
    old: &BundleManifest,
    new: &BundleManifest,
    game: &impl ProcessObservation,
    control: &InstallControl,
    mut publish: impl FnMut(LegacyInstallStatus) -> Result<()>,
) -> Result<InstallOutcome> {
    let execute = || -> Result<InstallOutcome> {
        if control.cancelled().map_err(control_error)? {
            return Ok(InstallOutcome::Cancelled);
        }
        publish(status(
            State::Preparing,
            "正在复核隔离 fixture 的完整新旧文件清单。",
            5,
        ))?;
        verify_bundle(&arguments.plugin_dir, old)?;
        verify_bundle(&arguments.staged_dir, new)?;
        if arguments.backup_dir.try_exists().map_err(io_error)? {
            return Err(invalid(
                "backup already exists; it will never be overwritten",
            ));
        }
        for required in [
            "MystiaStewardCompanion.BepInEx.dll",
            "companion/mystia-steward-companion.exe",
            UPDATER_FILE_NAME,
        ] {
            if !new.files().get(required).is_some_and(|file| file.size > 0) {
                return Err(invalid(format!(
                    "complete candidate package lacks {required}"
                )));
            }
        }
        if !control.enter_waiting().map_err(control_error)? {
            return Ok(InstallOutcome::Cancelled);
        }
        publish(status(
            State::WaitingGame,
            "已保留 fixture 进程句柄；等待它自行正常退出，不关闭真实游戏。",
            20,
        ))?;
        let started = Instant::now();
        loop {
            if control.cancelled().map_err(control_error)? {
                return Ok(InstallOutcome::Cancelled);
            }
            if game.exited_zero()? {
                break;
            }
            if started.elapsed() >= arguments.wait_timeout {
                return Err(invalid("retained fixture process exit deadline elapsed"));
            }
            control
                .wait(Duration::from_millis(25))
                .map_err(control_error)?;
        }
        publish(status(
            State::GameClosed,
            "保留的 fixture 进程已退出且退出码为零。",
            35,
        ))?;
        // Revalidate full trees immediately before the shared admission lock.
        // Cancellation can still win here; once admitted it cannot undo writes.
        verify_bundle(&arguments.plugin_dir, old)?;
        verify_bundle(&arguments.staged_dir, new)?;
        if !control.begin_replacement().map_err(control_error)? {
            return Ok(InstallOutcome::Cancelled);
        }
        replace(arguments, old, new, &mut publish)?;
        Ok(InstallOutcome::Succeeded)
    };
    let mut execute = execute;
    let result = execute();
    control.finish().map_err(control_error)?;
    match &result {
        Ok(InstallOutcome::Succeeded) => publish(status(
            State::Succeeded,
            "隔离 fixture 的完整 bundle 已安装并逐文件复核。",
            100,
        ))?,
        Ok(InstallOutcome::Cancelled) => publish(status(
            State::Cancelled,
            "隔离 fixture 安装已取消；未进入文件替换。",
            0,
        ))?,
        Err(error) => publish(status(
            State::Failed,
            &format!("隔离 fixture 安装失败：{error}"),
            0,
        ))?,
    }
    result
}

fn replace(
    arguments: &InstallArguments,
    old: &BundleManifest,
    new: &BundleManifest,
    publish: &mut impl FnMut(LegacyInstallStatus) -> Result<()>,
) -> Result<()> {
    if arguments.backup_dir.try_exists().map_err(io_error)? {
        return Err(invalid("backup appeared before replacement"));
    }
    paths::directory(
        arguments
            .backup_dir
            .parent()
            .ok_or_else(|| invalid("backup parent missing"))?,
    )?;
    publish(status(
        State::BackingUp,
        "正在原子移动 fixture 旧目录到独占备份。",
        50,
    ))?;
    fs::rename(&arguments.plugin_dir, &arguments.backup_dir).map_err(io_error)?;
    let install: Result<()> = (|| {
        publish(status(
            State::Installing,
            "正在将完整暂存 bundle 移入 fixture 安装目录。",
            70,
        ))?;
        fs::rename(&arguments.staged_dir, &arguments.plugin_dir).map_err(io_error)?;
        publish(status(
            State::Verifying,
            "正在复核全部 DLL、运行库与 assets。",
            90,
        ))?;
        verify_bundle(&arguments.plugin_dir, new)?;
        verify_bundle(&arguments.backup_dir, old)?;
        Ok(())
    })();
    if let Err(install_error) = install {
        let rollback: Result<()> = (|| {
            // A failed new tree is preserved, never erased. Restoration is
            // attempted only while the fixed destination is demonstrably free.
            if arguments.plugin_dir.try_exists().map_err(io_error)? {
                let failed = arguments
                    .staged_dir
                    .parent()
                    .ok_or_else(|| invalid("staging parent missing"))?
                    .join("failed-installed");
                if failed.try_exists().map_err(io_error)? {
                    return Err(invalid("failed-installed already exists; rollback stopped"));
                }
                paths::directory(&arguments.plugin_dir)?;
                fs::rename(&arguments.plugin_dir, failed).map_err(io_error)?;
            }
            verify_bundle(&arguments.backup_dir, old)?;
            fs::rename(&arguments.backup_dir, &arguments.plugin_dir).map_err(io_error)?;
            verify_bundle(&arguments.plugin_dir, old)?;
            Ok(())
        })();
        return Err(match rollback {
            Ok(()) => invalid(format!(
                "installation failed; verified old fixture restored: {install_error}"
            )),
            Err(rollback_error) => invalid(format!(
                "installation failed: {install_error}; rollback unresolved: {rollback_error}"
            )),
        });
    }
    Ok(())
}

pub fn status(state: State, message: &str, progress: u8) -> LegacyInstallStatus {
    LegacyInstallStatus {
        state,
        message: message.to_owned(),
        progress,
    }
}
fn invalid(message: impl Into<String>) -> ProbeError {
    ProbeError::new(ErrorKind::InvalidArguments, message)
}
fn io_error(error: std::io::Error) -> ProbeError {
    ProbeError::new(ErrorKind::Io, error.to_string())
}
fn control_error(error: String) -> ProbeError {
    ProbeError::new(ErrorKind::InvalidStatus, error)
}
