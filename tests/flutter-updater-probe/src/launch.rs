use std::collections::BTreeMap;
use std::ffi::OsString;
use std::fs::File;
use std::io::Read;
use std::num::{NonZeroU16, NonZeroU32};
use std::path::{Path, PathBuf};
use std::time::Duration;

use crate::{paths, ErrorKind, ProbeError, Result, PRODUCT_NAME, UPDATER_FILE_NAME};

pub const DEFAULT_CONTROL_PORT: u16 = 32146;
pub const DEFAULT_WAIT_TIMEOUT_SECONDS: u64 = 1800;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallArguments {
    pub game_pid: NonZeroU32,
    pub plugin_dir: PathBuf,
    pub staged_dir: PathBuf,
    pub backup_dir: PathBuf,
    pub status_file: PathBuf,
    pub control_port: NonZeroU16,
    pub wait_timeout: Duration,
}

/// Accept the exact `--key value` shape emitted by UpdateService. Unlike the old
/// permissive parser, missing values, duplicate keys and unknown options fail.
pub fn parse_legacy_arguments(
    args: impl IntoIterator<Item = OsString>,
) -> Result<InstallArguments> {
    let allowed = [
        "game-pid",
        "plugin-dir",
        "staged-dir",
        "backup-dir",
        "status-file",
        "control-port",
        "wait-timeout-seconds",
    ];
    let mut values = BTreeMap::new();
    let mut args = args.into_iter();
    while let Some(key) = args.next() {
        let key = key
            .into_string()
            .map_err(|_| invalid("non-Unicode option"))?;
        let key = key
            .strip_prefix("--")
            .ok_or_else(|| invalid("expected --key value"))?;
        if !allowed.contains(&key) || values.contains_key(key) {
            return Err(invalid(format!("unknown or duplicate option: --{key}")));
        }
        let value = args
            .next()
            .ok_or_else(|| invalid(format!("missing value for --{key}")))?;
        let value = value
            .into_string()
            .map_err(|_| invalid(format!("non-Unicode value for --{key}")))?;
        if value.is_empty() || value.starts_with("--") {
            return Err(invalid(format!("missing value for --{key}")));
        }
        values.insert(key.to_owned(), value);
    }
    let required = |key: &str| {
        values
            .get(key)
            .map(String::as_str)
            .ok_or_else(|| invalid(format!("missing --{key}")))
    };
    let game_pid = u32::try_from(positive_decimal(required("game-pid")?, "game-pid")?)
        .ok()
        .and_then(NonZeroU32::new)
        .ok_or_else(|| invalid("game-pid is out of range"))?;
    let control_port = u16::try_from(positive_decimal(
        values
            .get("control-port")
            .map(String::as_str)
            .unwrap_or("32146"),
        "control-port",
    )?)
    .ok()
    .and_then(NonZeroU16::new)
    .ok_or_else(|| invalid("control-port is out of range"))?;
    let wait_timeout = Duration::from_secs(positive_decimal(
        values
            .get("wait-timeout-seconds")
            .map(String::as_str)
            .unwrap_or("1800"),
        "wait-timeout-seconds",
    )?);
    let result = InstallArguments {
        game_pid,
        plugin_dir: paths::absolute_path(required("plugin-dir")?, "plugin-dir")?,
        staged_dir: paths::absolute_path(required("staged-dir")?, "staged-dir")?,
        backup_dir: paths::absolute_path(required("backup-dir")?, "backup-dir")?,
        status_file: paths::absolute_path(required("status-file")?, "status-file")?,
        control_port,
        wait_timeout,
    };
    result.validate_path_layout()?;
    Ok(result)
}

impl InstallArguments {
    /// Before a bootstrap creates its private bundle, prove the runner/status
    /// parents are real directories outside the target/staging/backup trees.
    /// This is a preflight snapshot, not a substitute for pinned handles during
    /// a future production installation transaction.
    pub fn validate_runner_location(&self, runner: &Path) -> Result<()> {
        paths::directory(&self.plugin_dir)?;
        paths::directory(&self.staged_dir)?;
        let runner_parent = runner
            .parent()
            .ok_or_else(|| invalid("runner has no parent"))?;
        let status_parent = self
            .status_file
            .parent()
            .ok_or_else(|| invalid("status has no parent"))?;
        let backup_parent = self
            .backup_dir
            .parent()
            .ok_or_else(|| invalid("backup has no parent"))?;
        for parent in [runner_parent, status_parent, backup_parent] {
            paths::directory(parent)?;
        }
        match std::fs::symlink_metadata(&self.backup_dir) {
            Ok(_) => return Err(invalid("backup directory already exists")),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(read_error(error)),
        }
        match std::fs::symlink_metadata(&self.status_file) {
            Ok(_) => {
                paths::regular_file(&self.status_file)?;
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(read_error(error)),
        }
        let canonical = |path: &Path| path.canonicalize().map_err(read_error);
        let runner_parent = canonical(runner_parent)?;
        let status = canonical(status_parent)?.join(
            self.status_file
                .file_name()
                .ok_or_else(|| invalid("status has no filename"))?,
        );
        let backup = canonical(backup_parent)?.join(
            self.backup_dir
                .file_name()
                .ok_or_else(|| invalid("backup has no name"))?,
        );
        let directories = [
            canonical(&self.plugin_dir)?,
            canonical(&self.staged_dir)?,
            backup,
        ];
        for (index, directory) in directories.iter().enumerate() {
            if overlaps(&runner_parent, directory) || overlaps(&status, directory) {
                return Err(invalid("runner/status must be outside installation trees"));
            }
            if directories
                .iter()
                .skip(index + 1)
                .any(|other| overlaps(directory, other))
            {
                return Err(invalid("resolved installation paths overlap"));
            }
        }
        Ok(())
    }

    fn validate_path_layout(&self) -> Result<()> {
        if !self
            .plugin_dir
            .file_name()
            .and_then(|name| name.to_str())
            .is_some_and(|name| name.eq_ignore_ascii_case(PRODUCT_NAME))
        {
            return Err(invalid(
                "plugin-dir must have the exact product directory name",
            ));
        }
        let directories = [&self.plugin_dir, &self.staged_dir, &self.backup_dir];
        for (index, left) in directories.iter().enumerate() {
            for right in directories.iter().skip(index + 1) {
                if overlaps(left, right) {
                    return Err(invalid(
                        "plugin, staged and backup directories must be disjoint",
                    ));
                }
            }
            if overlaps(left, &self.status_file) {
                return Err(invalid(
                    "status-file must be outside all installation directories",
                ));
            }
        }
        Ok(())
    }
}

fn overlaps(left: &Path, right: &Path) -> bool {
    #[cfg(windows)]
    let (left, right) = (
        PathBuf::from(left.as_os_str().to_string_lossy().to_lowercase()),
        PathBuf::from(right.as_os_str().to_string_lossy().to_lowercase()),
    );
    #[cfg(windows)]
    {
        left.starts_with(&right) || right.starts_with(&left)
    }
    #[cfg(not(windows))]
    {
        left.starts_with(right) || right.starts_with(left)
    }
}

fn positive_decimal(value: &str, label: &str) -> Result<u64> {
    if value.starts_with('0') || !value.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(invalid(format!(
            "{label} must be a canonical positive decimal"
        )));
    }
    value
        .parse::<u64>()
        .map_err(|_| invalid(format!("{label} is out of range")))
}

fn invalid(message: impl Into<String>) -> ProbeError {
    ProbeError::new(ErrorKind::InvalidArguments, message)
}

/// The copied runner must be byte-for-byte identical to the updater from the
/// staged package. This does not authenticate an arbitrary package by itself.
pub fn verify_runner_binding(runner: &Path, staged_dir: &Path) -> Result<()> {
    paths::directory(staged_dir)?;
    if !runner
        .file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| name.eq_ignore_ascii_case(UPDATER_FILE_NAME))
    {
        return Err(invalid(
            "runner must retain the legacy updater executable name",
        ));
    }
    if overlaps(runner, staged_dir) {
        return Err(invalid("runner must be outside the staged directory"));
    }
    let packaged = staged_dir.join(UPDATER_FILE_NAME);
    let left_metadata = paths::regular_file(runner)?;
    let right_metadata = paths::regular_file(&packaged)?;
    if left_metadata.len() == 0 || left_metadata.len() != right_metadata.len() {
        return Err(ProbeError::new(
            ErrorKind::IntegrityMismatch,
            "runner size mismatch or empty file",
        ));
    }
    let open = |path: &Path| {
        File::open(path).map_err(|error| {
            ProbeError::new(ErrorKind::Io, format!("open {}: {error}", path.display()))
        })
    };
    let mut left = open(runner)?;
    let mut right = open(&packaged)?;
    let mut left_bytes = [0_u8; 64 * 1024];
    let mut right_bytes = [0_u8; 64 * 1024];
    let mut remaining = left_metadata.len();
    while remaining > 0 {
        let length = remaining.min(left_bytes.len() as u64) as usize;
        left.read_exact(&mut left_bytes[..length])
            .map_err(read_error)?;
        right
            .read_exact(&mut right_bytes[..length])
            .map_err(read_error)?;
        if left_bytes[..length] != right_bytes[..length] {
            return Err(ProbeError::new(
                ErrorKind::IntegrityMismatch,
                "runner bytes differ from staged updater",
            ));
        }
        remaining -= length as u64;
    }
    if left.read(&mut left_bytes[..1]).map_err(read_error)? != 0
        || right.read(&mut right_bytes[..1]).map_err(read_error)? != 0
    {
        return Err(ProbeError::new(
            ErrorKind::IntegrityMismatch,
            "runner changed while reading",
        ));
    }
    Ok(())
}

fn read_error(error: std::io::Error) -> ProbeError {
    ProbeError::new(ErrorKind::Io, format!("read runner binding: {error}"))
}
