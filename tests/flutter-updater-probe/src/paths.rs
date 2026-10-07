use std::fs::{self, Metadata};
use std::path::{Component, Path, PathBuf};

use crate::{ErrorKind, ProbeError, Result};

pub(crate) fn absolute_path(value: &str, label: &str) -> Result<PathBuf> {
    let path = PathBuf::from(value);
    if value.trim() != value
        || value.chars().any(char::is_control)
        || !path.is_absolute()
        || path
            .components()
            .any(|part| matches!(part, Component::ParentDir))
    {
        return Err(ProbeError::new(
            ErrorKind::InvalidPath,
            format!("{label} must be an absolute native path without controls, padding or parent traversal"),
        ));
    }
    Ok(path)
}

/// Check every existing path component, not just the final file. Windows junctions
/// are reparse points even when `is_symlink` does not identify them as symlinks.
pub(crate) fn metadata_without_links(path: &Path) -> Result<Metadata> {
    if !path.is_absolute() {
        return Err(ProbeError::new(
            ErrorKind::InvalidPath,
            "expected absolute path",
        ));
    }
    let mut current = PathBuf::new();
    let mut result = None;
    for component in path.components() {
        if matches!(component, Component::ParentDir | Component::CurDir) {
            return Err(ProbeError::new(
                ErrorKind::InvalidPath,
                "relative path component",
            ));
        }
        current.push(component.as_os_str());
        // A Windows drive prefix alone is not a rooted path yet.
        if !current.is_absolute() {
            continue;
        }
        let metadata = fs::symlink_metadata(&current).map_err(|error| {
            ProbeError::new(
                ErrorKind::Io,
                format!("inspect {}: {error}", current.display()),
            )
        })?;
        if is_link(&metadata) {
            return Err(ProbeError::new(
                ErrorKind::InvalidPath,
                format!("symbolic link or reparse point: {}", current.display()),
            ));
        }
        result = Some(metadata);
    }
    result.ok_or_else(|| ProbeError::new(ErrorKind::InvalidPath, "empty path"))
}

fn is_link(metadata: &Metadata) -> bool {
    if metadata.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
    }
    #[cfg(not(windows))]
    {
        false
    }
}

pub(crate) fn regular_file(path: &Path) -> Result<Metadata> {
    let metadata = metadata_without_links(path)?;
    if !metadata.is_file() {
        return Err(ProbeError::new(
            ErrorKind::InvalidPath,
            format!("not a regular file: {}", path.display()),
        ));
    }
    Ok(metadata)
}

pub(crate) fn directory(path: &Path) -> Result<()> {
    if !metadata_without_links(path)?.is_dir() {
        return Err(ProbeError::new(
            ErrorKind::InvalidPath,
            format!("not a directory: {}", path.display()),
        ));
    }
    Ok(())
}
